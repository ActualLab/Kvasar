using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar;

/// <summary>
/// An embedded, encrypted, file-system-based key-value store (Bitcask model): an in-RAM hash index
/// over an append-only, encrypted, paged log. Keys and values are binary (<see cref="ReadOnlyMemory{Byte}"/>);
/// reads are zero-copy slices into cached, immutable pages. Multi-reader / single-writer (§7).
/// </summary>
public sealed class KvasarStore : IAsyncDisposable
{
    private readonly KvasarOptions _options;
    private readonly uint _formatVer;
    private readonly IKeyHasher _hasher;
    private readonly byte[] _hashKey;
    private readonly IPageCipherFactory _cipherFactory;
    private readonly bool _persistIndex;
    private readonly string _kidxPath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly StoreLock _lock;
    private int _pageSize;
    // volatile: Clear() swaps these while lock-free readers are dereferencing them, so readers must not
    // cache a stale reference.
    private volatile PageCache _cache = null!;
    private volatile SegmentSet _segments = null!;
    private volatile HashIndex _index = null!;
    private FileStream? _kidxDelta;   // held open across the store's life so each delta is one buffered write, not a file open
    private long _deltaCount;
    private bool _checkpointDue;
    private bool _disposed;

    public KvasarStats Stats
        // Best-effort snapshot: read without taking the write lock, so a concurrent writer may shift
        // the numbers mid-read. They're advisory (compaction/diagnostics), never used for correctness.
        => new(_index.Count, _segments.LiveBytes, _segments.DeadBytes, _segments.FileBytes);

    private KvasarStore(KvasarOptions options, StoreLock storeLock)
    {
        _options = options;
        _kidxPath = options.BasePath + ".kidx";

        var kdf = options.Kdf ?? KeyDerivations.HkdfSha256;
        _hasher = options.Hasher ?? KeyHashers.SipHash24;
        _formatVer = ParseFormatVersion(options.FormatVersion);

        // Derive per-store subkeys from the master key. The page nonce's uniqueness comes from each
        // segment's own random salt, so a store-level KDF salt isn't needed (the master key is already
        // a uniformly-random 256-bit secret); subkeys are separated by info label.
        var pageKey = new byte[KvasarConstants.PageKeySize];
        _hashKey = new byte[_hasher.IsKeyed ? Math.Max(1, _hasher.SecretSize) : 0];
        kdf.Derive(options.EncryptionKey, [], KvasarConstants.PageKeyInfo, pageKey);
        if (_hashKey.Length != 0)
            kdf.Derive(options.EncryptionKey, [], KvasarConstants.HashKeyInfo, _hashKey);

        _cipherFactory = options.DisableEncryption
            ? NoopPageCipherFactory.Instance
            : new AesGcmPageCipherFactory(pageKey, _formatVer);

        // The .kidx may live unencrypted only under a keyed-PRF hasher; otherwise we simply don't
        // persist it (always rebuild from the log) rather than leaking key-derived metadata.
        _persistIndex = _options.IndexEncryption switch {
            IndexEncryption.Off => true,
            IndexEncryption.On => false, // encrypted .kidx not implemented yet ⇒ rebuild each open
            _ => _hasher.IsKeyed,
        };

        _lock = storeLock;
    }

    public static async ValueTask<KvasarStore> Open(
        KvasarOptions options, CancellationToken cancellationToken = default)
    {
        if (options.EncryptionKey is not { Length: KvasarConstants.MasterKeySize })
            throw new ArgumentException($"EncryptionKey must be {KvasarConstants.MasterKeySize} bytes.", nameof(options));
        if (string.IsNullOrEmpty(options.BasePath))
            throw new ArgumentException("BasePath is required.", nameof(options));

        // The lock is taken here and held across wipe-and-recreate. Releasing it around WipeFiles would let
        // another process open a fresh store that we then delete out from under it — on Unix the unlink
        // succeeds silently and that store's writes vanish.
        var storeLock = new StoreLock(options.BasePath + ".lock");
        try {
            try {
                return await Create(options, storeLock, cancellationToken).ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                // Bad magic/version/pageSize/key or unreadable state ⇒ wipe & recreate (§8, §11).
                WipeFiles(options.BasePath);
                return await Create(options, storeLock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch {
            storeLock.Dispose();
            throw;
        }
    }

    // --- Reads (lock-free) --------------------------------------------------

    public ValueTask<ReadOnlyMemory<byte>?> Get(
        ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        // Not an async method: when the record's page is already decrypted in the cache — the common
        // case — this returns an already-completed ValueTask with no state machine and no allocation.
        var h = _hasher.Hash(key.Span, _hashKey);
        var cursor = _index.Probe(h);
        while (cursor.MoveNext(out var loc, out _)) {
            if (cursor.CurrentHash != h)
                continue;
            if (!_segments.TryReadRecordCached(loc, out var view))
                return GetSlow(key, h, cancellationToken);
            if (view.IsTombstone)
                continue;
            if (view.Key.Span.SequenceEqual(key.Span))
                return new ValueTask<ReadOnlyMemory<byte>?>(view.Value);
        }
        return default;
    }

    public async ValueTask<ReadOnlyMemory<byte>?[]> GetMany(
        IReadOnlyList<ReadOnlyMemory<byte>> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var results = new ReadOnlyMemory<byte>?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            results[i] = await Get(keys[i], cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async IAsyncEnumerable<(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> Value)> Scan(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IndexEntry[] entries;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            entries = _index.Snapshot().ToArray();
        }
        finally {
            _writeLock.Release();
        }

        // Scan order is unspecified (§4), so walk the log in write order rather than hash order: the index
        // snapshot is in hash order, which turns a full scan into random page faults. Sorting makes access
        // sequential, which is what lets the readahead below pull whole runs in one I/O.
        Array.Sort(entries, static (a, b) => a.SegmentId != b.SegmentId
            ? a.SegmentId.CompareTo(b.SegmentId)
            : a.Offset.CompareTo(b.Offset));

        var prefetchPages = _segments.PrefetchPages;
        var prefetchedSegment = uint.MaxValue;
        var nextPrefetchPage = 0L;
        foreach (var e in entries) {
            if (e.IsTombstone)
                continue;
            var pageId = e.Offset / _pageSize;
            if (e.SegmentId != prefetchedSegment || pageId >= nextPrefetchPage) {
                await _segments.Prefetch(e.SegmentId, pageId, prefetchPages, cancellationToken).ConfigureAwait(false);
                prefetchedSegment = e.SegmentId;
                nextPrefetchPage = pageId + prefetchPages;
            }
            RecordRead read;
            try {
                read = await _segments.TryReadRecord(e.Locator, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not KvasarCorruptException) {
                continue; // a segment compacted away mid-scan ⇒ skip (rare; cache-safe)
            }
            if (!read.IsFound || read.View.IsTombstone)
                continue;
            yield return (read.View.Key, read.View.Value);
        }
    }

    // --- Writes (single-writer) --------------------------------------------

    public async ValueTask Set(
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var appended = await AppendOne(key, value, cancellationToken).ConfigureAwait(false);
            // Seal the tail so the published locator points at an immutable page.
            await _segments.Flush(false, cancellationToken).ConfigureAwait(false);
            await Publish(key, appended, cancellationToken).ConfigureAwait(false);
            await MaybeCheckpoint(cancellationToken).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    public async ValueTask SetMany(
        IReadOnlyList<(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte>? Value)> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0)
            return;
        // Last write wins for duplicate keys: keep only the last occurrence per key hash.
        var lastByHash = new Dictionary<ulong, int>(updates.Count);
        for (var i = 0; i < updates.Count; i++)
            lastByHash[_hasher.Hash(updates[i].Key.Span, _hashKey)] = i;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pending = new List<(ReadOnlyMemory<byte> Key, AppendResult Appended)>(lastByHash.Count);
            for (var i = 0; i < updates.Count; i++) {
                var (key, value) = updates[i];
                if (lastByHash[_hasher.Hash(key.Span, _hashKey)] != i)
                    continue; // superseded within this batch
                var appended = await AppendOne(key, value, cancellationToken).ConfigureAwait(false);
                pending.Add((key, appended));
            }
            await _segments.Flush(false, cancellationToken).ConfigureAwait(false); // seal once for the whole batch
            foreach (var p in pending)
                await Publish(p.Key, p.Appended, cancellationToken).ConfigureAwait(false);
            await MaybeCheckpoint(cancellationToken).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    public async ValueTask Clear(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _index.Clear();
            _segments.Dispose();
            await DisposeDeltaStream().ConfigureAwait(false);
            WipeFiles(_options.BasePath);
            _deltaCount = 0;
            _cache = new PageCache(_options.PageCacheBytes);
            _segments = await SegmentSet.Create(
                _options.BasePath, _pageSize, _cipherFactory, _formatVer, _cache,
                _options.SegmentBytes, ResolveInlineCap(_options, _pageSize), cancellationToken).ConfigureAwait(false);
            // Recreate a fresh (empty) .kidx and reopen the delta stream.
            await WriteCheckpoint(cancellationToken).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    public async ValueTask Flush(bool fsync = false, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_disposed)
                return;
            await _segments.Flush(fsync, cancellationToken).ConfigureAwait(false);
            if (_kidxDelta != null) {
                await _kidxDelta.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (fsync)
                    _kidxDelta.Flush(true);
            }
        }
        finally {
            _writeLock.Release();
        }
    }

    public async ValueTask Compact(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (await TryCompactOne(cancellationToken).ConfigureAwait(false)) { }
        }
        finally {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try {
            if (_disposed)
                return;
            _disposed = true;
            try {
                await _segments.Flush(true).ConfigureAwait(false);
                await WriteCheckpoint(CancellationToken.None).ConfigureAwait(false);
                await DisposeDeltaStream().ConfigureAwait(false);
            }
            catch {
                // Best-effort flush on dispose; a regenerable cache tolerates losing the last writes.
            }
            finally {
                // Must run even if the flush throws (a full disk can fail the buffered .kidx dispose):
                // _disposed is already set, so a retry would no-op and the store lock would leak for the
                // rest of the process.
                _segments.Dispose();
                _lock.Dispose();
            }
        }
        finally {
            _writeLock.Release();
        }
    }

    // Private methods

    private readonly record struct AppendResult(Locator Locator, int RecordLength, bool IsTombstone);

    private static async ValueTask<KvasarStore> Create(
        KvasarOptions options, StoreLock storeLock, CancellationToken cancellationToken)
    {
        var store = new KvasarStore(options, storeLock);
        try {
            await store.Initialize(cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch {
            // Release what this attempt opened, but leave the lock to Open — it stays held across the
            // wipe-and-retry, and a throw here must not strand it.
            try {
                await store.DisposeDeltaStream().ConfigureAwait(false);
            }
            catch {
                // Ignored: we're already unwinding, and the files are about to be wiped or abandoned.
            }
            store._segments?.Dispose();
            throw;
        }
    }

    private async ValueTask Initialize(CancellationToken cancellationToken)
    {
        _pageSize = await ResolvePageSize(_options, cancellationToken).ConfigureAwait(false);
        _cache = new PageCache(_options.PageCacheBytes);
        _segments = await SegmentSet.Create(
            _options.BasePath, _pageSize, _cipherFactory, _formatVer, _cache,
            _options.SegmentBytes, ResolveInlineCap(_options, _pageSize), cancellationToken).ConfigureAwait(false);
        _index = new HashIndex();
        await LoadIndex(cancellationToken).ConfigureAwait(false);
        OpenDeltaStream();
    }

    private async ValueTask<ReadOnlyMemory<byte>?> GetSlow(
        ReadOnlyMemory<byte> key, ulong keyHash, CancellationToken cancellationToken)
    {
        var cursor = _index.Probe(keyHash);
        while (cursor.MoveNext(out var loc, out _)) {
            if (cursor.CurrentHash != keyHash)
                continue;
            var value = await TryReadValue(loc, key, cancellationToken).ConfigureAwait(false);
            if (value is not null)
                return value;
        }
        return null;
    }

    private async ValueTask<ReadOnlyMemory<byte>?> TryReadValue(
        Locator loc, ReadOnlyMemory<byte> key, CancellationToken cancellationToken)
    {
        try {
            var read = await _segments.TryReadRecord(loc, cancellationToken).ConfigureAwait(false);
            if (!read.IsFound || read.View.IsTombstone)
                return null;
            if (!read.View.Key.Span.SequenceEqual(key.Span))
                return null; // hash collision ⇒ different key
            return read.View.Value;
        }
        catch (Exception e) when (e is not KvasarCorruptException) {
            return null; // transient (e.g. segment compacted away) ⇒ treat as miss
        }
    }

    private async ValueTask<AppendResult> AppendOne(
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte>? value, CancellationToken cancellationToken)
    {
        var isTombstone = value is null;
        var valueMemory = isTombstone ? default : value!.Value;
        if (!isTombstone && valueMemory.Length > _options.MaxValueBytes) {
            if (_options.OversizedValueThrows)
                throw new ArgumentException($"Value exceeds MaxValueBytes ({_options.MaxValueBytes}).", nameof(value));
            // §12: skip oversized value (default). Callers wanting failures set OversizedValueThrows.
            return new AppendResult(Locator.None, 0, isTombstone);
        }
        var (locator, recordLength) = await _segments
            .Append(RecordFlags.None, KvasarValueType.Raw, key, valueMemory, isTombstone, cancellationToken)
            .ConfigureAwait(false);
        return new AppendResult(locator, recordLength, isTombstone);
    }

    private async ValueTask Publish(
        ReadOnlyMemory<byte> key, AppendResult appended, CancellationToken cancellationToken)
    {
        var (loc, recordLength, isTombstone) = appended;
        if (loc.IsNone)
            return; // skipped oversized value
        var h = _hasher.Hash(key.Span, _hashKey);
        var hadOld = _index.TryGetFirst(h, out var oldLoc, out var oldLen);
        if (isTombstone) {
            if (hadOld) {
                _index.Remove(h, oldLoc);
                _segments.OnSuperseded(oldLoc, oldLen);
            }
            _segments.OnSuperseded(loc, recordLength); // the tombstone itself is reclaimable space
            await AppendDelta(h, loc, recordLength, true, cancellationToken).ConfigureAwait(false);
        }
        else {
            if (hadOld)
                _segments.OnSuperseded(oldLoc, oldLen);
            _index.Set(h, loc, recordLength);
            await AppendDelta(h, loc, recordLength, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask AppendDelta(
        ulong keyHash, Locator loc, int length, bool tombstone, CancellationToken cancellationToken)
    {
        if (!_persistIndex)
            return;
        var entry = new IndexEntry {
            KeyHash = keyHash,
            SegmentId = loc.SegmentId,
            Offset = loc.Offset,
            Length = (uint)length,
            Flags = tombstone ? (byte)RecordFlags.Tombstone : (byte)0,
        };
        try {
            if (_kidxDelta != null)
                await IndexFile.AppendDelta(_kidxDelta, entry, cancellationToken).ConfigureAwait(false);
            if (++_deltaCount > (_index.Count / 2) + 64)
                _checkpointDue = true; // deferred — see MaybeCheckpoint
        }
        catch {
            // .kidx is a rebuildable hint; a failed lazy delta is non-fatal.
        }
    }

    private ValueTask MaybeCheckpoint(CancellationToken cancellationToken)
    {
        // A checkpoint stamps the log HWM alongside the index, so it must never run while a batch is
        // only half-published. SetMany and compaction seal the *whole* batch before publishing any of
        // it, so a checkpoint taken mid-loop would pair an end-of-batch HWM with an index missing the
        // rest of the batch — and recovery, which replays only past the HWM, would never re-read those
        // records. They would be lost permanently despite the write having been acknowledged.
        if (!_checkpointDue)
            return default;
        _checkpointDue = false;
        return WriteCheckpoint(cancellationToken);
    }

    private void OpenDeltaStream()
    {
        // Idempotent: LoadIndex's rebuild path checkpoints (which reopens the stream) before Initialize
        // calls this, and a second FileStream on the same path would be a sharing violation.
        if (!_persistIndex || _kidxDelta != null)
            return;
        // The file exists after LoadIndex (either loaded or freshly checkpointed). Keep it open for
        // buffered appends; a 4 KiB buffer coalesces a batch's deltas into one write.
        _kidxDelta = new FileStream(
            _kidxPath, FileMode.Open, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
        _kidxDelta.Seek(0, SeekOrigin.End);
    }

    private async ValueTask DisposeDeltaStream()
    {
        if (_kidxDelta == null)
            return;
        await _kidxDelta.DisposeAsync().ConfigureAwait(false);
        _kidxDelta = null;
    }

    private async ValueTask WriteCheckpoint(CancellationToken cancellationToken)
    {
        if (!_persistIndex)
            return;
        // Rewriting the whole file invalidates the append handle; close, rewrite, reopen at the end.
        await DisposeDeltaStream().ConfigureAwait(false);
        var live = _index.Snapshot().ToArray();
        var hwm = (_segments.ActiveSegmentId, checked((uint)_segments.ActiveLogicalHwm));
        await IndexFile.WriteCheckpoint(_kidxPath, live, hwm, _formatVer, cancellationToken).ConfigureAwait(false);
        _deltaCount = 0;
        _checkpointDue = false;
        OpenDeltaStream();
    }

    // --- Private: compaction (segment GC, §9) ------------------------------

    private async ValueTask<bool> TryCompactOne(CancellationToken cancellationToken)
    {
        var target = uint.MaxValue;
        foreach (var s in _segments.SealedSegments()) {
            if (s.DeadBytes < _options.CompactionMinBytes)
                continue;
            if (s.SegmentBytes <= 0 || (double)s.DeadBytes / s.SegmentBytes < _options.CompactionDeadRatio)
                continue;
            target = s.SegmentId;
            break;
        }
        if (target == uint.MaxValue)
            return false;

        var pending = new List<(ulong Hash, Locator NewLoc, int NewLen, Locator OldLoc, int OldLen)>();
        await foreach (var (loc, view, recordLength) in _segments.ScanAll(cancellationToken).ConfigureAwait(false)) {
            if (loc.SegmentId != target || view.IsTombstone)
                continue;
            var h = _hasher.Hash(view.Key.Span, _hashKey);
            if (!_index.TryGetFirst(h, out var cur, out _) || cur != loc)
                continue; // dead record (superseded) ⇒ don't carry forward
            var (newLoc, newLen) = await _segments
                .Append(view.Flags, view.ValType, view.Key, view.Value, false, cancellationToken)
                .ConfigureAwait(false);
            pending.Add((h, newLoc, newLen, loc, recordLength));
        }
        // Seal before repointing so readers see immutable pages.
        await _segments.Flush(false, cancellationToken).ConfigureAwait(false);
        foreach (var p in pending) {
            _index.Set(p.Hash, p.NewLoc, p.NewLen);
            _segments.OnSuperseded(p.OldLoc, p.OldLen);
            await AppendDelta(p.Hash, p.NewLoc, p.NewLen, false, cancellationToken).ConfigureAwait(false);
        }
        _segments.RemoveSegment(target);
        // Only now is the index fully repointed off the drained segment; checkpointing mid-loop would
        // pair an end-of-compaction HWM with stale locators into a file this line just deleted.
        await MaybeCheckpoint(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // --- Private: open / lifecycle -----------------------------------------

    private async ValueTask LoadIndex(CancellationToken cancellationToken)
    {
        var checkpoint = _persistIndex
            ? await IndexFile.TryLoad(_kidxPath, _formatVer, cancellationToken).ConfigureAwait(false)
            : null;
        if (checkpoint is { } cp) {
            _index.BulkLoad(cp.Entries);
            // Fast path: only decrypt the gap written after the checkpoint HWM (the .kidx is lazy), not
            // the whole log — this is what keeps open O(index) rather than O(data) (§6.5).
            await foreach (var (loc, view, recordLength) in
                _segments.ScanFrom(cp.SegmentId, cp.Hwm, cancellationToken).ConfigureAwait(false))
                ApplyLoaded(loc, view, recordLength);
        }
        else {
            // Fallback: no usable .kidx ⇒ full log scan (decrypts everything), then write a checkpoint.
            await foreach (var (loc, view, recordLength) in
                _segments.ScanAll(cancellationToken).ConfigureAwait(false))
                ApplyLoaded(loc, view, recordLength);
            await WriteCheckpoint(cancellationToken).ConfigureAwait(false);
        }
        SeedAccounting();
    }

    private void SeedAccounting()
    {
        // Seed live/dead bytes from the final index (no extra decrypt) instead of scanning the log.
        var live = new Dictionary<uint, long>();
        foreach (var e in _index.Snapshot())
            live[e.SegmentId] = live.GetValueOrDefault(e.SegmentId) + e.Length;
        _segments.SeedAccountingFromIndex(live);
    }

    private void ApplyLoaded(Locator loc, in RecordView view, int recordLength)
    {
        // Accounting is seeded afterwards from the final index (SeedAccounting), so no OnSuperseded here.
        var h = _hasher.Hash(view.Key.Span, _hashKey);
        if (view.IsTombstone) {
            if (_index.TryGetFirst(h, out var oldLoc, out _))
                _index.Remove(h, oldLoc);
        }
        else
            _index.Set(h, loc, recordLength);
    }

    private async ValueTask<int> ResolvePageSize(KvasarOptions options, CancellationToken cancellationToken)
    {
        // Adopt an existing store's page size; otherwise use the option (or the 4 KiB default).
        var existing = await TryReadExistingHeader(options.BasePath, cancellationToken).ConfigureAwait(false);
        if (existing is { } header) {
            if (header.FormatVer != _formatVer)
                throw new KvasarCorruptException("FormatVersion mismatch.");
            if (options.PageSize > 0 && options.PageSize != header.PageSize)
                throw new KvasarCorruptException("PageSize mismatch with the existing store.");
            return header.PageSize;
        }
        var size = options.PageSize > 0 ? options.PageSize : KvasarConstants.DefaultPageSize;
        if (size < KvasarConstants.MinPageSize || size > KvasarConstants.MaxPageSize || (size & (size - 1)) != 0)
            throw new ArgumentException(
                $"PageSize must be a power of two in [{KvasarConstants.MinPageSize}, {KvasarConstants.MaxPageSize}].",
                nameof(options));
        return size;
    }

    private static int ResolveInlineCap(KvasarOptions options, int pageSize)
        => options.MaxInlineValueBytes > 0 ? Math.Min(options.MaxInlineValueBytes, pageSize) : pageSize;

    private static async ValueTask<SegmentHeader?> TryReadExistingHeader(
        string basePath, CancellationToken cancellationToken)
    {
        // Probe the lowest surviving segment rather than a hardcoded .001: compaction can delete segment 1,
        // and missing the header would silently adopt the option/default page size for an existing store.
        var path = FindLowestSegmentPath(basePath);
        if (path == null)
            return null;
        try {
            using var handle = File.OpenHandle(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.Asynchronous);
            var buf = new byte[KvasarConstants.SegmentHeaderSize];
            var read = await RandomAccess.ReadAsync(handle, buf, 0, cancellationToken).ConfigureAwait(false);
            return read < buf.Length ? null : SegmentHeader.Read(buf);
        }
        catch (KvasarCorruptException) {
            throw;
        }
        catch {
            return null;
        }
    }

    private static string? FindLowestSegmentPath(string basePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(basePath));
        if (dir == null || !Directory.Exists(dir))
            return null;
        var prefix = Path.GetFileName(basePath) + ".";
        const string suffix = ".klog";

        string? bestPath = null;
        var bestId = uint.MaxValue;
        foreach (var path in Directory.EnumerateFiles(dir, prefix + "*" + suffix)) {
            var fileName = Path.GetFileName(path);
            if (fileName.Length <= prefix.Length + suffix.Length)
                continue;
            var mid = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
            if (uint.TryParse(mid, out var id) && id < bestId) {
                bestId = id;
                bestPath = path;
            }
        }
        return bestPath;
    }

    private static void WipeFiles(string basePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(basePath));
        var name = Path.GetFileName(basePath);
        if (dir == null || !Directory.Exists(dir))
            return;
        foreach (var file in Directory.EnumerateFiles(dir, name + ".*")) {
            var ext = Path.GetExtension(file);
            if (ext is ".klog" or ".kidx" || file.EndsWith(".kidx.tmp", StringComparison.Ordinal)
                    || Path.GetFileName(file).EndsWith(".klog", StringComparison.Ordinal)) {
                try { File.Delete(file); }
                catch { /* best-effort */ }
            }
        }
    }

    private static uint ParseFormatVersion(string formatVersion)
    {
        if (uint.TryParse(formatVersion, out var v))
            return v;
        // Non-numeric version ⇒ stable 32-bit hash (FNV-1a) so it still round-trips on reopen.
        uint hash = 2166136261;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(formatVersion)) {
            hash ^= b;
            hash *= 16777619;
        }
        return hash | 0x8000_0000; // keep it distinct from small numeric versions
    }
}
