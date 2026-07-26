using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar;

/// <summary>
/// An embedded, encrypted, file-system-based key-value store (Bitcask model): an in-RAM hash index
/// over an append-only, encrypted, paged log. Keys and values are binary (<see cref="KvasarKey"/> /
/// <see cref="KvasarValue"/>); reads are zero-copy slices into cached, immutable pages.
/// Multi-reader / single-writer (§7).
/// </summary>
public sealed class KvasarStore : IAsyncDisposable
{
    private readonly KvasarOptions _options;
    private readonly uint _formatVer;
    private readonly IKeyHasher _hasher;
    private readonly byte[] _hashKey;
    private readonly IPageCipherFactory _cipherFactory;
    private readonly bool _mustPersistIndex;
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
    private bool _isCheckpointDue;
    private readonly TimeSpan _flushDelay;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _flushLoopTask;
    private int _isDirty;
    private int _isDisposeStarted;
    private bool _isDisposed;

    public KvasarStats Stats
        // Best-effort snapshot: read without taking the write lock, so a concurrent writer may shift
        // the numbers mid-read. They're advisory (compaction/diagnostics), never used for correctness.
        => new(_index.Count, _segments.LiveBytes, _segments.DeadBytes, _segments.FileBytes);

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

    private KvasarStore(KvasarOptions options, StoreLock storeLock)
    {
        _options = options;
        _kidxPath = options.BasePath + ".kidx";

        var kdf = options.Kdf ?? KeyDerivations.HkdfSha256;
        _hasher = options.Hasher ?? KeyHashers.SipHash24;
        _formatVer = ParseFormatVersion(options.FormatVersion, options.Version);

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
        _mustPersistIndex = _options.IndexEncryption switch {
            IndexEncryption.Off => true,
            IndexEncryption.On => false, // encrypted .kidx not implemented yet ⇒ rebuild each open
            _ => _hasher.IsKeyed,
        };

        _flushDelay = options.FlushDelay;
        _lock = storeLock;
    }

    public async ValueTask DisposeAsync()
    {
        // Interlocked rather than _isDisposed, which is only set further down under the write lock: the
        // second caller has to bail out *before* touching _writeLock, since the first disposes it below.
        if (Interlocked.Exchange(ref _isDisposeStarted, 1) != 0)
            return;

        // Stop the background flusher before taking the lock, so it can't be mid-flush while we tear down.
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        if (_flushLoopTask is { } flushLoopTask) {
            try {
                await flushLoopTask.ConfigureAwait(false);
            }
            catch {
                // Already best-effort; dispose still has to run.
            }
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try {
            _isDisposed = true;
            try {
                await _segments.Flush(true).ConfigureAwait(false);
                await WriteCheckpoint().ConfigureAwait(false);
                await DisposeDeltaStream().ConfigureAwait(false);
                // Everything is on disk, so the next open can safely append to the active segment.
                WriteCleanMarker();
            }
            catch {
                // Best-effort flush on dispose; a regenerable cache tolerates losing the last writes.
            }
            finally {
                // Must run even if the flush throws (a full disk can fail the buffered .kidx dispose):
                // _isDisposed is already set, so a retry would no-op and the store lock would leak for the
                // rest of the process.
                _segments.Dispose();
                _lock.Dispose();
            }
        }
        finally {
            _writeLock.Release();
        }
        // After the release, so a waiter never blocks on an already-disposed semaphore (I33).
        _writeLock.Dispose();
        _disposeCts.Dispose();
    }

    // --- Reads (lock-free) --------------------------------------------------

    public ValueTask<KvasarValue?> Get(
        KvasarKey key, CancellationToken cancellationToken = default)
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
                return new ValueTask<KvasarValue?>(new KvasarValue(view.Value, view.ValueKind));
        }
        return default;
    }

    public async ValueTask<KvasarValue?[]> GetMany(
        IReadOnlyList<KvasarKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var results = new KvasarValue?[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            results[i] = await Get(keys[i], cancellationToken).ConfigureAwait(false);
        return results;
    }

    public async IAsyncEnumerable<(KvasarKey Key, KvasarValue Value)> Scan(
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
        Array.Sort(entries, static (a, b) => a.PackedLocator.CompareTo(b.PackedLocator));

        var prefetchPages = _segments.PrefetchPages;
        var prefetchedSegment = uint.MaxValue;
        var nextPrefetchPage = 0L;
        foreach (var e in entries) {
            if (e.IsTombstone)
                continue;
            var loc = e.Locator;
            var pageId = loc.Offset / _pageSize;
            if (loc.FileId != prefetchedSegment || pageId >= nextPrefetchPage) {
                await _segments.Prefetch(loc.FileId, pageId, prefetchPages, cancellationToken).ConfigureAwait(false);
                prefetchedSegment = loc.FileId;
                nextPrefetchPage = pageId + prefetchPages;
            }
            RecordRead read;
            try {
                read = await _segments.TryReadRecord(e.Locator, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not (KvasarCorruptException or OperationCanceledException)) {
                continue; // a segment compacted away mid-scan ⇒ skip (rare; cache-safe)
            }
            if (!read.IsFound || read.View.IsTombstone)
                continue;
            yield return (new KvasarKey(read.View.Key), new KvasarValue(read.View.Value, read.View.ValueKind));
        }
    }

    // --- Writes (single-writer) --------------------------------------------

    // Every write method has the same two-part shape: acquire the write lock (cancellable — nothing is
    // mutated yet), then await an uncancellable *Locked body via WaitAsync(cancellationToken). Cancelling
    // therefore stops the caller's *wait*, never the write: the body keeps running to completion and
    // releases the lock on its way out. That's deliberate — a write abandoned midway is what would leave
    // a truncated record in the log (recovery reads the records after it as its own tail) or an index
    // pointing at bytes that were never written, so no token is passed down to the log/paging/index
    // layers at all — their write methods don't even take one.
    public async ValueTask Set(
        KvasarKey key, KvasarValue? value, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SetLocked(key, value).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetMany(
        IReadOnlyList<(KvasarKey Key, KvasarValue? Value)> updates,
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
        await SetManyLocked(updates, lastByHash).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask Clear(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await ClearLocked().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask Flush(bool fsync, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await FlushLocked(fsync).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask Compact(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        // The only write with a genuinely cancellable interior: the token stops the copy pass at a record
        // boundary (see TryCompactOne), and the pass in flight still finishes repointing the index.
        await CompactLocked(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Private methods

    private readonly record struct AppendResult(Locator Locator, int RecordLength, bool IsTombstone);

    // --- Private: write-lock bodies ----------------------------------------
    // Each of these is entered with the write lock already held and releases it; they return Task (not
    // ValueTask) because the public wrapper awaits them through Task.WaitAsync.

    private async Task SetLocked(KvasarKey key, KvasarValue? value)
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var appended = await AppendOne(key, value).ConfigureAwait(false);
            await SealOrDefer().ConfigureAwait(false);
            await Publish(key, appended).ConfigureAwait(false);
            await MaybeCheckpoint().ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task SetManyLocked(
        IReadOnlyList<(KvasarKey Key, KvasarValue? Value)> updates, Dictionary<ulong, int> lastByHash)
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var pending = new List<(KvasarKey Key, AppendResult Appended)>(lastByHash.Count);
            for (var i = 0; i < updates.Count; i++) {
                var (key, value) = updates[i];
                if (lastByHash[_hasher.Hash(key.Span, _hashKey)] != i)
                    continue; // superseded within this batch
                var appended = await AppendOne(key, value).ConfigureAwait(false);
                pending.Add((key, appended));
            }
            await SealOrDefer().ConfigureAwait(false); // seal once for the whole batch
            foreach (var p in pending)
                await Publish(p.Key, p.Appended).ConfigureAwait(false);
            await MaybeCheckpoint().ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task ClearLocked()
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _index.Clear();
            _segments.Dispose();
            await DisposeDeltaStream().ConfigureAwait(false);
            WipeFiles(_options.BasePath);
            _deltaCount = 0;
            _cache = new PageCache(_options.PageCacheBytes);
            _segments = await SegmentSet.Create(
                _options.BasePath, _pageSize, _cipherFactory, _formatVer, _cache,
                _options.SegmentBytes, ResolveInlineCap(_options, _pageSize), CancellationToken.None)
                .ConfigureAwait(false);
            // Recreate a fresh (empty) .kidx and reopen the delta stream.
            await WriteCheckpoint().ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task FlushLocked(bool fsync)
    {
        try {
            if (_isDisposed)
                return;
            await _segments.Flush(fsync).ConfigureAwait(false);
            if (_kidxDelta != null) {
                await _kidxDelta.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (fsync)
                    _kidxDelta.Flush(flushToDisk: true);
            }
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task CompactLocked(CancellationToken cancellationToken)
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            while (await TryCompactOne(cancellationToken).ConfigureAwait(false)) { }
        }
        catch (OperationCanceledException) {
            // Compaction is opportunistic maintenance: a cancelled pass has already left the store
            // consistent (see TryCompactOne), and the caller's await observed the cancellation anyway.
        }
        finally {
            _writeLock.Release();
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
        if (_flushDelay > TimeSpan.Zero) {
            // See RollToNewSegment: deferred flushing can lose whole unwritten pages on a crash, which
            // would otherwise let the next run re-encrypt different data under an already-used page nonce.
            // A graceful close flushes everything and leaves a marker, so only an unclean shutdown needs
            // the roll — otherwise every launch would strand another segment. An empty active segment has
            // no pages to collide with either.
            var isClosedCleanly = TryConsumeCleanMarker();
            if (!isClosedCleanly && _segments.ActiveSegmentPageCount > 0)
                await _segments.RollToNewSegment().ConfigureAwait(false);
            _flushLoopTask = Task.Run(RunFlushLoop, CancellationToken.None);
        }
    }

    private async ValueTask<KvasarValue?> GetSlow(
        KvasarKey key, ulong keyHash, CancellationToken cancellationToken)
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

    private async ValueTask<KvasarValue?> TryReadValue(
        Locator loc, KvasarKey key, CancellationToken cancellationToken)
    {
        try {
            var read = await _segments.TryReadRecord(loc, cancellationToken).ConfigureAwait(false);
            if (!read.IsFound || read.View.IsTombstone)
                return null;
            if (!read.View.Key.Span.SequenceEqual(key.Span))
                return null; // hash collision ⇒ different key
            return new KvasarValue(read.View.Value, read.View.ValueKind);
        }
        catch (Exception e) when (e is not (KvasarCorruptException or OperationCanceledException)) {
            return null; // transient (e.g. segment compacted away) ⇒ treat as miss
        }
    }

    private async ValueTask<AppendResult> AppendOne(KvasarKey key, KvasarValue? value)
    {
        var isTombstone = value is null;
        var record = value ?? default;
        if (!isTombstone && record.Length > _options.MaxValueBytes) {
            if (_options.OversizedValueThrows)
                throw new ArgumentException($"Value exceeds MaxValueBytes ({_options.MaxValueBytes}).", nameof(value));
            // §12: skip oversized value (default). Callers wanting failures set OversizedValueThrows.
            return new AppendResult(Locator.None, 0, isTombstone);
        }
        var (locator, recordLength) = await _segments
            .Append(RecordFlags.None, record.Kind, key.Memory, record.Memory, isTombstone)
            .ConfigureAwait(false);
        return new AppendResult(locator, recordLength, isTombstone);
    }

    private async ValueTask Publish(KvasarKey key, AppendResult appended)
    {
        var (loc, recordLength, isTombstone) = appended;
        if (loc.IsNone)
            return; // skipped oversized value
        var h = _hasher.Hash(key.Span, _hashKey);
        var hasOld = _index.TryGetFirst(h, out var oldLoc, out var oldLen);
        if (isTombstone) {
            if (hasOld) {
                _index.Remove(h, oldLoc);
                _segments.OnSuperseded(oldLoc, oldLen);
            }
            _segments.OnSuperseded(loc, recordLength); // the tombstone itself is reclaimable space
            await AppendDelta(h, loc, recordLength, true).ConfigureAwait(false);
        }
        else {
            if (hasOld)
                _segments.OnSuperseded(oldLoc, oldLen);
            _index.Set(h, loc, recordLength);
            await AppendDelta(h, loc, recordLength, false).ConfigureAwait(false);
        }
    }

    private async ValueTask AppendDelta(ulong keyHash, Locator loc, int length, bool isTombstone)
    {
        if (!_mustPersistIndex)
            return;
        var entry = new IndexEntry {
            KeyHash = keyHash,
            PackedLocator = loc.Packed,
            Length = (uint)length,
            Flags = isTombstone ? (byte)RecordFlags.Tombstone : (byte)0,
        };
        try {
            // CancellationToken.None: the delta stream is buffered, so a cancelled write can tear an entry
            // in the middle of the file — and every entry after it then decodes as garbage locators.
            if (_kidxDelta != null)
                await IndexFile.AppendDelta(_kidxDelta, entry, CancellationToken.None).ConfigureAwait(false);
            if (++_deltaCount > (_index.Count / 2) + 64)
                _isCheckpointDue = true; // deferred — see MaybeCheckpoint
        }
        catch {
            // .kidx is a rebuildable hint; a failed lazy delta is non-fatal.
        }
    }

    // Present only between a graceful close and the next open, so its absence means "the last run may have
    // died with unflushed pages". Deleted on open so a crash during this session leaves it absent.
    private string CleanMarkerPath => _options.BasePath + ".clean";

    private bool TryConsumeCleanMarker()
    {
        try {
            if (!File.Exists(CleanMarkerPath))
                return false;
            File.Delete(CleanMarkerPath);
            return true;
        }
        catch {
            return false; // can't prove a clean shutdown ⇒ assume the unsafe case
        }
    }

    private void WriteCleanMarker()
    {
        try {
            File.WriteAllBytes(CleanMarkerPath, []);
        }
        catch {
            // Best-effort: a missing marker only costs one extra segment on the next open.
        }
    }

    private ValueTask SealOrDefer()
    {
        // Durable mode seals the tail before publishing, so a published locator always points at an
        // immutable page. Deferred mode publishes into the *unsealed* tail instead, which readers already
        // handle: the writer only ever appends past _tailFill, and SealTail swaps in a fresh array rather
        // than rewriting this one, so bytes under a handed-out slice never change. Skipping the seal is
        // what makes an unbatched Set cheap — sealing per call pads a whole page per record.
        if (_flushDelay <= TimeSpan.Zero)
            return _segments.Flush(false);
        Volatile.Write(ref _isDirty, 1);
        return default;
    }

    private async Task RunFlushLoop()
    {
        // Bounds how long a write can sit unflushed. Losing writes newer than the last flush is the
        // accepted cost of FlushDelay; losing anything older would not be, so failures only retry.
        try {
            while (!_disposeCts.IsCancellationRequested) {
                await Task.Delay(_flushDelay, _disposeCts.Token).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _isDirty, 0) == 0)
                    continue;
                try {
                    await Flush(false, _disposeCts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException) {
                    // Best-effort: a failed background flush costs durability, never consistency, so the
                    // next tick simply tries again.
                    Volatile.Write(ref _isDirty, 1);
                }
            }
        }
        catch (OperationCanceledException) {
            // Disposed.
        }
    }

    private ValueTask MaybeCheckpoint()
    {
        // A checkpoint stamps the log HWM alongside the index, so it must never run while a batch is
        // only half-published. SetMany and compaction seal the *whole* batch before publishing any of
        // it, so a checkpoint taken mid-loop would pair an end-of-batch HWM with an index missing the
        // rest of the batch — and recovery, which replays only past the HWM, would never re-read those
        // records. They would be lost permanently despite the write having been acknowledged.
        if (!_isCheckpointDue)
            return default;
        _isCheckpointDue = false;
        return WriteCheckpoint();
    }

    private void OpenDeltaStream()
    {
        // Idempotent: LoadIndex's rebuild path checkpoints (which reopens the stream) before Initialize
        // calls this, and a second FileStream on the same path would be a sharing violation.
        if (!_mustPersistIndex || _kidxDelta != null)
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

    private async ValueTask WriteCheckpoint()
    {
        if (!_mustPersistIndex)
            return;
        // Rewriting the whole file invalidates the append handle; close, rewrite, reopen at the end.
        // CancellationToken.None: bailing out between the two would leave the store with no delta stream
        // for the rest of its life, silently dropping every later index update.
        await DisposeDeltaStream().ConfigureAwait(false);
        var live = _index.Snapshot().ToArray();
        var hwm = (_segments.ActiveSegmentId, checked((uint)_segments.ActiveLogicalHwm));
        await IndexFile.WriteCheckpoint(_kidxPath, live, hwm, _formatVer, CancellationToken.None)
            .ConfigureAwait(false);
        _deltaCount = 0;
        _isCheckpointDue = false;
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

        // The copy loop is the one cancellable part of a write path: it only ever *adds* records, and the
        // index still points at the originals, so an abandoned pass leaves unreferenced copies (dead bytes
        // a later compaction reclaims), never a broken record or a dangling locator. Everything below —
        // repointing the index and dropping the drained segment — must run as a whole.
        var pending = new List<(ulong Hash, Locator NewLoc, int NewLen, Locator OldLoc, int OldLen)>();
        await foreach (var (loc, view, recordLength) in _segments.ScanAll(cancellationToken).ConfigureAwait(false)) {
            if (loc.FileId != target || view.IsTombstone)
                continue;
            var h = _hasher.Hash(view.Key.Span, _hashKey);
            if (!_index.TryGetFirst(h, out var cur, out _) || cur != loc)
                continue; // dead record (superseded) ⇒ don't carry forward
            var (newLoc, newLen) = await _segments
                .Append(view.Flags, view.ValueKind, view.Key, view.Value, false)
                .ConfigureAwait(false);
            pending.Add((h, newLoc, newLen, loc, recordLength));
        }
        // Seal before repointing so readers see immutable pages.
        await _segments.Flush(false).ConfigureAwait(false);
        foreach (var p in pending) {
            _index.Set(p.Hash, p.NewLoc, p.NewLen);
            _segments.OnSuperseded(p.OldLoc, p.OldLen);
            await AppendDelta(p.Hash, p.NewLoc, p.NewLen, false).ConfigureAwait(false);
        }
        _segments.RemoveSegment(target);
        // Only now is the index fully repointed off the drained segment; checkpointing mid-loop would
        // pair an end-of-compaction HWM with stale locators into a file this line just deleted.
        await MaybeCheckpoint().ConfigureAwait(false);
        return true;
    }

    // --- Private: open / lifecycle -----------------------------------------

    private async ValueTask LoadIndex(CancellationToken cancellationToken)
    {
        var checkpoint = _mustPersistIndex
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
            await WriteCheckpoint().ConfigureAwait(false);
        }
        SeedAccounting();
    }

    private void SeedAccounting()
    {
        // Seed live/dead bytes from the final index (no extra decrypt) instead of scanning the log.
        var live = new Dictionary<uint, long>();
        foreach (var e in _index.Snapshot())
            live[e.Locator.FileId] = live.GetValueOrDefault(e.Locator.FileId) + e.Length;
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
                throw new KvasarCorruptException("FormatVersion or Version mismatch.");
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
            if (!IsOwnFile(name, Path.GetFileName(file)))
                continue;
            try { File.Delete(file); }
            catch { /* best-effort */ }
        }
    }

    private static bool IsOwnFile(string baseName, string fileName)
    {
        // Exact suffixes only. The old test accepted any "<base>.*" ending in .klog, so wiping `cache`
        // also deleted a user's `cache.backup.klog` (I31) — a segment suffix must be all digits.
        if (fileName.Length <= baseName.Length || !fileName.StartsWith(baseName, StringComparison.Ordinal))
            return false;

        var suffix = fileName.AsSpan(baseName.Length);
        if (suffix is ".kidx" or ".clean" or ".kidx.tmp")
            return true;
        if (suffix.Length < 7 || suffix[0] != '.' || !suffix.EndsWith(".klog", StringComparison.Ordinal))
            return false;

        foreach (var c in suffix[1..^5])
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }

    private static uint ParseFormatVersion(string formatVersion, string? version)
    {
        // The app-level Version folds into the same on-disk tag as FormatVersion: both are stamped into
        // every segment header, so changing either makes the next open see a mismatched header and
        // wipe & recreate the store (§11).
        if (string.IsNullOrEmpty(version) && uint.TryParse(formatVersion, out var v))
            return v;
        // Anything else ⇒ stable 32-bit hash (FNV-1a) so it still round-trips on reopen.
        uint hash = 2166136261;
        Add(formatVersion);
        Add("\0"); // separator, so ("1", "0") and ("10", "") don't hash alike
        Add(version ?? "");
        return hash | 0x8000_0000; // keep it distinct from small numeric versions

        void Add(string s) {
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) {
                hash ^= b;
                hash *= 16777619;
            }
        }
    }
}
