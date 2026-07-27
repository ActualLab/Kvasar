using System.Security.Cryptography;
using System.Text;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar;

/// <summary>
/// An embedded, encrypted, file-system-based key-value store (Bitcask model): an in-RAM hash index
/// over an append-only, encrypted, paged log. Keys and values are binary (<see cref="KvasarKey"/> /
/// <see cref="KvasarValue"/>); reads are zero-copy slices into cached, immutable pages.
/// Multi-reader / single-writer (§7).
/// </summary>
public sealed class KvasarStore : IAsyncDisposable
{
    private const int CompactionBatchBytes = 64 * 1024;
    private const int CompactionBatchRecords = 64;
    // Below this, a GetMany batch cannot consume the ~1 MiB run a prefetch pulls, so it costs more
    // than it saves. See the note in GetMany.
    private const int MinPrefetchBatchSize = 8;
    private static readonly IComparer<(ulong Packed, int Index, ulong Hash)> GetManyOrderComparer =
        Comparer<(ulong Packed, int Index, ulong Hash)>.Create(
            static (a, b) => a.Packed.CompareTo(b.Packed));

    private readonly KvasarOptions _options;
    private readonly uint _formatVer;
    private readonly IKeyHasher _hasher;
    private readonly byte[] _hashKey;
    private readonly IPageCipherFactory _cipherFactory;
    private readonly byte[] _indexMacKey;
    private readonly IStorageBackend _storage;
    private readonly Superblock _superblock;
    private readonly bool _mustPersistIndex;
    private readonly string _kvsPath;
    private readonly string[] _kdatPaths;
    private readonly string[] _kidxPaths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly StoreLock _lock;
    private readonly TimeSpan _flushDelay;
    private readonly CancellationTokenSource _disposeCts = new();

    private int _pageSize;
    private int _nextCacheId;
    private PageCache _cache = null!;
    private volatile DataLog _data = null!;
    private volatile HashIndex _index = null!;
    private IStorageFile? _superblockFile;
    private IndexLog[] _indexLogs = [];
    private int _indexSlot;
    private ulong _generation;
    // The generation of the commit that last moved the active data or index file to the other slot. The
    // freed slot stays referenced by the superblock slot below it, so it may only be recycled once one
    // further commit has pushed that generation out of the pair (§3.2).
    private ulong _slotSwitchGeneration;
    private bool _isSlotSwitchPending;
    private bool _mustRotateIndex;
    private bool _isCompacting;
    private CompactionState? _compaction;
    private Task? _compactionTask;
    private long _uncommittedBytes;
    private Task? _flushLoopTask;
    private int _isDirty;
    // Completed on the clean⇒dirty edge and replaced once consumed, so the flush loop waits on a
    // signal instead of polling. Constructed directly: CODING_STYLE routes these through
    // TaskCompletionSourceExt, which lives in ActualLab.Core and is covered by the zero-dependency
    // carve-out.
    private volatile TaskCompletionSource _whenDirty = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isDisposeStarted;
    private bool _isDisposed;

    public KvasarStats Stats {
        get {
            // Best-effort snapshot: read without taking the write lock, so a concurrent writer may shift
            // the numbers mid-read. They're advisory (compaction/diagnostics), never used for correctness.
            ThrowIfDisposed();
            return new(_index.Count, _data.LiveBytes, _data.DeadBytes, _data.FileBytes);
        }
    }

    public static async ValueTask<KvasarStore> Open(
        KvasarOptions options, CancellationToken cancellationToken = default)
    {
        if (options.EncryptionKey is not { Length: KvasarConstants.MasterKeySize })
            throw new ArgumentException($"EncryptionKey must be {KvasarConstants.MasterKeySize} bytes.", nameof(options));
        if (string.IsNullOrEmpty(options.BasePath))
            throw new ArgumentException("BasePath is required.", nameof(options));
        if (options.MaxValueBytes is <= 0 or > KvasarConstants.MaxRecordValueBytes)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxValueBytes,
                $"MaxValueBytes must be in [1, {KvasarConstants.MaxRecordValueBytes}].");
        if (options.MaxInlineValueBytes < 0 || options.MaxInlineValueBytes > options.MaxValueBytes)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxInlineValueBytes,
                "MaxInlineValueBytes must be zero or no greater than MaxValueBytes.");
        if (options.CommitBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.CommitBytes, "CommitBytes must be positive.");
        if (double.IsNaN(options.CompactionDeadRatio)
            || options.CompactionDeadRatio <= 0 || options.CompactionDeadRatio > 1)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.CompactionDeadRatio,
                "CompactionDeadRatio must be in (0, 1].");
        if (options.CompactionMinBytes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.CompactionMinBytes,
                "CompactionMinBytes must be non-negative.");
        if (options.PageCacheBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.PageCacheBytes, "PageCacheBytes must be positive.");
        if (options.IndexEncryption == IndexEncryption.On)
            throw new NotSupportedException("Encrypted index persistence is not supported.");

        // The lock is taken here and held across wipe-and-recreate. Releasing it around the wipe would let
        // another process open a fresh store that we then delete out from under it — on Unix the unlink
        // succeeds silently and that store's writes vanish.
        var storeLock = new StoreLock(options.BasePath + ".lock");
        try {
            try {
                return await Create(options, storeLock, false, cancellationToken).ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                // A file the superblock names but that recovery could not route around ⇒ wipe & recreate.
                // KvasarKeyException is deliberately NOT caught here: a wrong key must never destroy an
                // intact store, which is what conflating the two used to do (I9).
                return await Create(options, storeLock, true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch {
            storeLock.Dispose();
            throw;
        }
    }

    private static async ValueTask<KvasarStore> Create(
        KvasarOptions options, StoreLock storeLock, bool mustWipe, CancellationToken cancellationToken)
    {
        var store = new KvasarStore(options, storeLock);
        try {
            if (mustWipe)
                store.WipeFiles();
            await store.Initialize(cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch {
            // Release what this attempt opened, but leave the lock to Open — it stays held across the
            // wipe-and-retry, and a throw here must not strand it.
            try {
                await store.CloseFiles().ConfigureAwait(false);
            }
            finally {
                store.DisposeKeyMaterial();
            }
            throw;
        }
    }

    private KvasarStore(KvasarOptions options, StoreLock storeLock)
    {
        _options = options;
        _kvsPath = options.BasePath + ".kvs";
        _kdatPaths = [options.BasePath + ".0.kdat", options.BasePath + ".1.kdat"];
        _kidxPaths = [options.BasePath + ".0.kidx", options.BasePath + ".1.kidx"];
        _storage = options.StorageBackend ?? FileStorageBackend.Instance;

        var kdf = options.Kdf ?? KeyDerivations.HkdfSha256;
        _hasher = options.Hasher ?? KeyHashers.SipHash24;
        _formatVer = ParseFormatVersion(options.FormatVersion, options.Version);

        // Derive per-store subkeys from the master key. The page nonce's uniqueness comes from each
        // file's own random salt, so a store-level KDF salt isn't needed (the master key is already
        // a uniformly-random 256-bit secret); subkeys are separated by info label.
        var pageKey = new byte[KvasarConstants.PageKeySize];
        var indexMacKey = new byte[KvasarConstants.IndexMacKeySize];
        var hashKey = new byte[_hasher.IsKeyed ? Math.Max(1, _hasher.SecretSize) : 0];
        IPageCipherFactory? cipherFactory = null;
        Superblock? superblock = null;
        try {
            kdf.Derive(options.EncryptionKey, [], KvasarConstants.PageKeyInfo, pageKey);
            kdf.Derive(options.EncryptionKey, [], KvasarConstants.IndexMacKeyInfo, indexMacKey);
            if (hashKey.Length != 0)
                kdf.Derive(options.EncryptionKey, [], KvasarConstants.HashKeyInfo, hashKey);
            cipherFactory = options.DisableEncryption
                ? NoopPageCipherFactory.Instance
                : new AesGcmPageCipherFactory(pageKey, _formatVer);
            superblock = new Superblock(options.EncryptionKey, _formatVer, kdf);
            _hashKey = hashKey;
            _indexMacKey = indexMacKey;
            _cipherFactory = cipherFactory;
            _superblock = superblock;
        }
        catch {
            CryptographicOperations.ZeroMemory(hashKey);
            CryptographicOperations.ZeroMemory(indexMacKey);
            (cipherFactory as IDisposable)?.Dispose();
            superblock?.Dispose();
            throw;
        }
        finally {
            CryptographicOperations.ZeroMemory(pageKey);
        }

        // The .kidx may live unencrypted only under a keyed-PRF hasher; otherwise we persist only its
        // header (no entries at all) rather than leaking key-derived metadata.
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

        // Stop the background committer before taking the lock, so it can't be mid-commit while we tear down.
        await _disposeCts.CancelAsync().ConfigureAwait(false);
        if (_flushLoopTask is { } flushLoopTask) {
            try {
                await flushLoopTask.ConfigureAwait(false);
            }
            catch {
                // Already best-effort; dispose still has to run.
            }
        }
        if (_compactionTask is { } compactionTask) {
            try {
                await compactionTask.ConfigureAwait(false);
            }
            catch {
                // The pass has already rolled back or reached a switch; disposal still has to run.
            }
        }

        await _writeLock.WaitAsync().ConfigureAwait(false);
        if (_compactionTask is { } racedCompactionTask) {
            _writeLock.Release();
            try {
                await racedCompactionTask.ConfigureAwait(false);
            }
            catch {
                // The pass has already rolled back or reached a switch; disposal still has to run.
            }
            await _writeLock.WaitAsync().ConfigureAwait(false);
        }
        try {
            _isDisposed = true;
            try {
                // With index persistence off, the checkpoint's stamp is the only record of how far the
                // in-RAM index got, so re-stamp it on the way out — otherwise the next open replays the
                // whole session.
                if (!_mustPersistIndex && _generation > _slotSwitchGeneration)
                    _mustRotateIndex = true;
                await Commit(_mustRotateIndex).ConfigureAwait(false);
            }
            catch {
                // Best-effort commit on dispose; a regenerable cache tolerates losing the last writes.
            }
            finally {
                // Must run even if the commit throws (a full disk can fail it): _isDisposed is already
                // set, so a retry would no-op and the store lock would leak for the rest of the process.
                await CloseFiles().ConfigureAwait(false);
                DisposeKeyMaterial();
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
        ThrowIfDisposed();
        // Not an async method: when the record's page is already decrypted in the cache — the common
        // case — this returns an already-completed ValueTask with no state machine and no allocation.
        var h = _hasher.Hash(key.Span, _hashKey);
        var cursor = _index.Probe(h);
        while (cursor.MoveNext(out var loc, out _)) {
            if (cursor.CurrentHash != h)
                continue;
            if (!_data.TryReadRecordCached(loc, out var view))
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);
        var results = new KvasarValue?[keys.Count];
        if (keys.Count <= 1) {
            if (keys.Count == 1)
                results[0] = await Get(keys[0], cancellationToken).ConfigureAwait(false);
            return results;
        }

        // §6.4: resolve locators from the in-RAM index first, then read in locator order with run
        // readahead, so a cold batch walks the log forward instead of paying one random I/O per key.
        // The resolved hash and first locator are carried into the read pass, which still performs
        // full-key verification and walks any further collision candidates.
        var order = new (ulong Packed, int Index, ulong Hash)[keys.Count];
        for (var i = 0; i < keys.Count; i++) {
            var h = _hasher.Hash(keys[i].Span, _hashKey);
            var cursor = _index.Probe(h);
            var packed = ulong.MaxValue; // unresolved ⇒ sorts last; Get returns null without touching disk
            while (cursor.MoveNext(out var loc, out _)) {
                if (cursor.CurrentHash != h)
                    continue;
                packed = loc.Packed;
                break;
            }
            order[i] = (packed, i, h);
        }
        Array.Sort(order, GetManyOrderComparer);

        // Readahead only pays when the batch is big enough to consume the run it pulls. A run is
        // PrefetchPages (~1 MiB), so issuing one for a 2-key batch reads a megabyte to serve two
        // records — which is what `BatchingKvas` in front of the store actually produces (keys/op ≈ 1.1),
        // and it cost more than the batching saved. Large cold batches, the case §6.4 is about, still get it.
        var prefetchPages = keys.Count >= MinPrefetchBatchSize ? _data.PrefetchPages : 0;
        var prefetchedFile = uint.MaxValue;
        var nextPrefetchPage = 0L;
        foreach (var (packed, index, hash) in order) {
            if (packed != ulong.MaxValue && prefetchPages > 0) {
                var loc = Locator.FromPacked(packed);
                var pageId = loc.Offset / _pageSize;
                if (loc.FileId != prefetchedFile || pageId >= nextPrefetchPage) {
                    await _data.Prefetch(loc.FileId, pageId, prefetchPages, cancellationToken)
                        .ConfigureAwait(false);
                    prefetchedFile = loc.FileId;
                    nextPrefetchPage = pageId + prefetchPages;
                }
            }
            if (packed != ulong.MaxValue)
                results[index] = await GetManyValue(
                    keys[index], hash, Locator.FromPacked(packed), cancellationToken).ConfigureAwait(false);
        }
        return results;
    }

    public async IAsyncEnumerable<(KvasarKey Key, KvasarValue Value)> Scan(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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

        var prefetchPages = _data.PrefetchPages;
        var prefetchedFile = uint.MaxValue;
        var nextPrefetchPage = 0L;
        foreach (var e in entries) {
            if (e.IsTombstone)
                continue;
            var loc = e.Locator;
            var pageId = loc.Offset / _pageSize;
            if (loc.FileId != prefetchedFile || pageId >= nextPrefetchPage) {
                await _data.Prefetch(loc.FileId, pageId, prefetchPages, cancellationToken).ConfigureAwait(false);
                prefetchedFile = loc.FileId;
                nextPrefetchPage = pageId + prefetchPages;
            }
            RecordRead read;
            try {
                read = await _data.TryReadRecord(e.Locator, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) {
                continue; // an unauthenticatable page or a slot recycled mid-scan ⇒ skip (§5.3)
            }
            if (!read.IsFound || read.View.IsTombstone)
                continue;
            if (!IsLiveIndexEntry(e))
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
        ThrowIfDisposed();
        if (key.Length > KvasarConstants.MaxKeyBytes)
            throw new ArgumentOutOfRangeException(
                nameof(key), key.Length, $"Key length must not exceed {KvasarConstants.MaxKeyBytes} bytes.");
        if (cancellationToken.CanBeCanceled) {
            cancellationToken.ThrowIfCancellationRequested();
            (key, value) = CopyUpdate(key, value);
        }
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SetLocked(key, value).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetMany(
        IReadOnlyList<(KvasarKey Key, KvasarValue? Value)> updates,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(updates);
        for (var i = 0; i < updates.Count; i++)
            if (updates[i].Key.Length > KvasarConstants.MaxKeyBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(updates), updates[i].Key.Length,
                    $"Key length must not exceed {KvasarConstants.MaxKeyBytes} bytes.");
        if (updates.Count == 0)
            return;
        if (cancellationToken.CanBeCanceled) {
            cancellationToken.ThrowIfCancellationRequested();
            var copied = new (KvasarKey Key, KvasarValue? Value)[updates.Count];
            for (var i = 0; i < copied.Length; i++)
                copied[i] = CopyUpdate(updates[i].Key, updates[i].Value);
            updates = copied;
        }
        // Last write wins for byte-identical keys. Carry the hashes forward so Publish does not recompute them.
        var hashes = new ulong[updates.Count];
        var lastByKey = new Dictionary<KvasarKey, int>(updates.Count);
        for (var i = 0; i < updates.Count; i++) {
            var h = _hasher.Hash(updates[i].Key.Span, _hashKey);
            hashes[i] = h;
            lastByKey[updates[i].Key] = i;
        }
        var isLast = new bool[updates.Count];
        foreach (var i in lastByKey.Values)
            isLast[i] = true;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SetManyLocked(updates, hashes, isLast).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask Clear(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await ClearLocked().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask Flush()
    {
        ThrowIfDisposed();
        return FlushCore(CancellationToken.None);
    }

    [Obsolete(
        "Use Flush(); configure durability through KvasarOptions.Durability.")]
    public ValueTask Flush(bool fsync, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return FlushCore(cancellationToken);
    }

    public async ValueTask Compact(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await CompactLocked(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Private methods

    // --- Private: write-lock bodies ----------------------------------------
    // Each of these is entered with the write lock already held and releases it; they return Task (not
    // ValueTask) because the public wrapper awaits them through Task.WaitAsync.

    private async ValueTask FlushCore(CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        await FlushLocked().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SetLocked(KvasarKey key, KvasarValue? value)
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var appended = await AppendOne(key, value).ConfigureAwait(false);
            await Publish(key, _hasher.Hash(key.Span, _hashKey), appended).ConfigureAwait(false);
            await OnWritesPublished(appended.RecordLength).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task SetManyLocked(
        IReadOnlyList<(KvasarKey Key, KvasarValue? Value)> updates,
        ulong[] hashes,
        bool[] isLast)
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var pending = new List<(KvasarKey Key, ulong Hash, AppendResult Appended)>();
            var bytes = 0L;
            for (var i = 0; i < updates.Count; i++) {
                var (key, value) = updates[i];
                if (!isLast[i])
                    continue; // superseded within this batch
                var appended = await AppendOne(key, value).ConfigureAwait(false);
                pending.Add((key, hashes[i], appended));
                bytes += appended.RecordLength;
            }
            foreach (var p in pending)
                await Publish(p.Key, p.Hash, p.Appended).ConfigureAwait(false);
            await OnWritesPublished(bytes).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task ClearLocked()
    {
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_compaction is { } compaction)
                AbortCompaction(compaction);
            _index.Clear();
            // Deliberately the one place that unlinks: Clear is an explicit, exclusive request to destroy
            // the data, so leaving the old bytes on disk under a recycled slot would be the wrong answer.
            // A crash mid-Clear reads back as an uninitialized store, which is a defined outcome (§3.4).
            await CloseFiles().ConfigureAwait(false);
            WipeFiles();
            await CreateFresh(CancellationToken.None).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task FlushLocked()
    {
        try {
            ThrowIfDisposed();
            await Commit(false).ConfigureAwait(false);
            await MaybeCompact().ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
    }

    private async Task CompactLocked(CancellationToken cancellationToken)
    {
        Task? compactionTask = null;
        try {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_data.DeadBytes > 0)
                compactionTask = await CompactCore(cancellationToken).ConfigureAwait(false);
        }
        finally {
            _writeLock.Release();
        }
        if (compactionTask is not null)
            await compactionTask.ConfigureAwait(false);
    }

    // --- Private: open / recovery (§3.1, §5.2) ------------------------------

    private async ValueTask Initialize(CancellationToken cancellationToken)
    {
        _superblockFile = await _storage.Open(_kvsPath, cancellationToken).ConfigureAwait(false);
        var read = await _superblock.Read(_superblockFile, cancellationToken).ConfigureAwait(false);
        if (read.Status == SuperblockStatus.WrongKey)
            throw new KvasarKeyException(
                $"The store '{_options.BasePath}' was created under a different encryption key.");

        var isAdopted = false;
        if (read.Status == SuperblockStatus.Ok) {
            // Newest generation first; the older candidate is the fallback for step 3 of §5.2.
            for (var i = 0; i < read.States.Length; i++) {
                SuperblockState? previousState = i + 1 < read.States.Length ? read.States[i + 1] : null;
                isAdopted = await TryAdopt(read.States[i], previousState, cancellationToken)
                    .ConfigureAwait(false);
                if (isAdopted)
                    break;
            }
        }
        if (!isAdopted) {
            // Missing (a new store), FormatMismatch (a deliberate FormatVersion/Version bump) or nothing
            // adoptable (genuine corruption) — all three rebuild from scratch (§3.1).
            await CloseFiles().ConfigureAwait(false);
            WipeFiles();
            await CreateFresh(cancellationToken).ConfigureAwait(false);
        }
        if (_flushDelay > TimeSpan.Zero)
            _flushLoopTask = Task.Run(RunFlushLoop, CancellationToken.None);
    }

    private async ValueTask<bool> TryAdopt(
        SuperblockState state, SuperblockState? previousState, CancellationToken cancellationToken)
    {
        try {
            await OpenLogs(state, cancellationToken).ConfigureAwait(false);
            await AuthenticateCommitWindow(state, previousState, cancellationToken).ConfigureAwait(false);
            await Recover(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (KvasarCorruptException) {
            // Under Buffered, a slot naming more data than its file holds is the expected fallback rather
            // than corruption (§5.2 step 3): drop this generation and retry with the older slot.
            await CloseLogs().ConfigureAwait(false);
            return false;
        }
    }

    private ValueTask AuthenticateCommitWindow(
        SuperblockState state, SuperblockState? previousState, CancellationToken cancellationToken)
    {
        // Only the pages this generation *adds* may be authenticated. Below the previous committed extent
        // §5.2.1 deliberately leaves unauthenticatable pages: a torn tail burns its page id and the commit
        // that follows stamps an extent covering it forever, so authenticating from 0 there fails
        // permanently once any tail has been torn — and a rejected candidate falls through to the older
        // one and then to WipeFiles, turning one burned page into total loss on exactly the crash path
        // §5.2 step 3 exists to survive. Down there a failing page is a read-time miss (§5.3).
        if (previousState is { } previous
            && previous.DataSlot == state.DataSlot
            && previous.DataCommitLength >= KvasarConstants.SegmentHeaderSize
            && previous.DataCommitLength <= state.DataCommitLength) {
            var onDiskPageSize = _pageSize + _cipherFactory.Overhead;
            var previousBodyLength = previous.DataCommitLength - KvasarConstants.SegmentHeaderSize;
            if (previousBodyLength % onDiskPageSize != 0)
                return default;

            var fromOffset = previousBodyLength / onDiskPageSize * _pageSize;
            return _data.Authenticate(
                state.DataSlot, fromOffset, _data.ActiveCommittedOffset, cancellationToken);
        }

        // A different data slot means a compaction switch: BeginCompaction truncated that slot to its
        // header and restarted page ids under a fresh salt, and the switch commit names only pages the
        // pass itself wrote and flushed — so nothing below the extent is burned and the whole extent is
        // checkable. This is the §5.2 step-3 path, so skipping it entirely (as the C1 fix briefly did)
        // would drop the guarantee precisely where it is load-bearing.
        if (previousState is { } other && other.DataSlot != state.DataSlot)
            return _data.Authenticate(state.DataSlot, 0, _data.ActiveCommittedOffset, cancellationToken);

        // No predecessor at all: there is no floor to bound the window against, and the extent may
        // legitimately contain a burned page, so there is nothing safe to check here. Recorded as an
        // open gap in docs/REVIEW-R4.md rather than papered over.
        return default;
    }

    private async ValueTask OpenLogs(SuperblockState state, CancellationToken cancellationToken)
    {
        var dataFiles = await OpenSlotFiles(_kdatPaths, cancellationToken).ConfigureAwait(false);
        try {
            // Adopt the existing page size rather than the option's, and reject a caller that asks for a
            // different one — the alternative is silently reading the store at the wrong geometry.
            var pageSize = await ReadPageSize(dataFiles[state.DataSlot], cancellationToken).ConfigureAwait(false);
            if (_options.PageSize > 0 && _options.PageSize != pageSize)
                throw new KvasarCorruptException("PageSize does not match the existing store.");

            _pageSize = pageSize;
            _cache = new PageCache(_options.PageCacheBytes);
            _data = await DataLog.Open(
                dataFiles, state.DataSlot, state.DataCommitLength, pageSize, _cipherFactory, _formatVer,
                _cache, ResolveInlineCap(_options, pageSize), MintCacheId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch {
            await DisposeFiles(dataFiles).ConfigureAwait(false);
            throw;
        }
        _indexLogs = await OpenIndexLogs(
            cancellationToken, state.IndexSlot, state.IndexCommitLength).ConfigureAwait(false);
    }

    private async ValueTask Recover(SuperblockState state, CancellationToken cancellationToken)
    {
        _index = new HashIndex();
        var committedOffset = _data.ActiveCommittedOffset;
        var indexLog = _indexLogs[state.IndexSlot];
        var snapshot = await indexLog
            .Read(state.IndexCommitLength, state.Generation, cancellationToken)
            .ConfigureAwait(false);
        // Only an index at the exact committed extent can be adopted without rotation.
        var isIndexComplete = snapshot is not null
            && _mustPersistIndex && indexLog.Length == state.IndexCommitLength;
        var replayFrom = 0L;
        if (snapshot is { } s) {
            _index.BulkLoad(s.Entries);
            // Anything shorter (a tail lost to a crash, or an index that never carried entries at all)
            // falls back to the checkpoint's own stamp, which §5.2.1 keeps above every burned page.
            replayFrom = isIndexComplete ? committedOffset : s.DataCommitLength;
        }
        if (replayFrom < committedOffset) {
            await foreach (var (loc, view, recordLength) in _data
                .ScanFrom(state.DataSlot, replayFrom, committedOffset, cancellationToken)
                .ConfigureAwait(false))
                ApplyLoaded(loc, view, recordLength);
        }
        SeedAccounting(state);

        _generation = state.Generation;
        _indexSlot = state.IndexSlot;
        // Confirm the adoption by re-writing it as the next generation. That overwrites the slot below it,
        // so the free .kdat/.kidx stop being referenced by any valid superblock slot and the rotation
        // below — and any later compaction — may recycle them (§3.2).
        var generation = _generation + 1;
        var confirmedState = state with { Generation = generation };
        if (isIndexComplete)
            await indexLog.WriteCommitMac(generation).ConfigureAwait(false);
        else
            confirmedState = confirmedState with { IndexCommitLength = 0 };
        await _superblock.Write(_superblockFile!, confirmedState).ConfigureAwait(false);
        _generation = generation;
        _slotSwitchGeneration = 0;

        if (!isIndexComplete || _data.BurnedBytes > 0) {
            // §3.3/§5.2.1: an index the recovery had to replay past is only ever appended to *after a
            // hole*, so its length stops implying its contents — a later open would trust a prefix that
            // is missing entries. Rotating rebases it as one contiguous checkpoint, stamped at the resume
            // offset so the replay range also stays above any page a torn tail burned.
            _mustRotateIndex = true;
            await Commit(true).ConfigureAwait(false);
        }
    }

    private async ValueTask CreateFresh(CancellationToken cancellationToken)
    {
        if (_pageSize == 0)
            _pageSize = ResolveFreshPageSize(_options);
        _superblockFile = await _storage.Open(_kvsPath, cancellationToken).ConfigureAwait(false);
        await _superblock.Initialize(_superblockFile).ConfigureAwait(false);

        var dataFiles = await OpenSlotFiles(_kdatPaths, cancellationToken).ConfigureAwait(false);
        try {
            _cache = new PageCache(_options.PageCacheBytes);
            _data = await DataLog.Create(
                dataFiles, _pageSize, _cipherFactory, _formatVer, _cache,
                ResolveInlineCap(_options, _pageSize), MintCacheId).ConfigureAwait(false);
        }
        catch {
            await DisposeFiles(dataFiles).ConfigureAwait(false);
            throw;
        }
        _indexLogs = await OpenIndexLogs(cancellationToken).ConfigureAwait(false);

        _index = new HashIndex();
        _generation = 0;
        _slotSwitchGeneration = 0;
        _isSlotSwitchPending = false;
        _isCompacting = false;
        _compaction = null;
        _compactionTask = null;
        _uncommittedBytes = 0;
        _indexSlot = 1; // so the checkpoint below rotates into slot 0
        _mustRotateIndex = true;
        await Commit(true).ConfigureAwait(false);
    }

    private async ValueTask<IStorageFile[]> OpenSlotFiles(string[] paths, CancellationToken cancellationToken)
    {
        var files = new IStorageFile[paths.Length];
        try {
            for (var i = 0; i < paths.Length; i++)
                files[i] = await _storage.Open(paths[i], cancellationToken).ConfigureAwait(false);
        }
        catch {
            await DisposeFiles(files).ConfigureAwait(false);
            throw;
        }
        return files;
    }

    private async ValueTask<IndexLog[]> OpenIndexLogs(
        CancellationToken cancellationToken, int committedSlot = -1, long committedLength = long.MaxValue)
    {
        var files = await OpenSlotFiles(_kidxPaths, cancellationToken).ConfigureAwait(false);
        var logs = new IndexLog[files.Length];
        try {
            for (var i = 0; i < files.Length; i++) {
                var slotCommitLength = i == committedSlot ? committedLength : long.MaxValue;
                logs[i] = await IndexLog
                    .Open(files[i], _formatVer, _indexMacKey, slotCommitLength, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch {
            await DisposeFiles(files).ConfigureAwait(false);
            throw;
        }
        return logs;
    }

    private static async ValueTask<int> ReadPageSize(IStorageFile file, CancellationToken cancellationToken)
    {
        var header = new byte[KvasarConstants.SegmentHeaderSize];
        await file.ReadExact(0, header, cancellationToken).ConfigureAwait(false);
        return SegmentHeader.Read(header).PageSize;
    }

    private static async ValueTask DisposeFiles(IStorageFile?[] files)
    {
        foreach (var file in files) {
            if (file is null)
                continue;
            try {
                await file.DisposeAsync().ConfigureAwait(false);
            }
            catch {
                // Ignored: we're already unwinding, and nothing here has been acknowledged.
            }
        }
    }

    private async ValueTask CloseFiles()
    {
        await CloseLogs().ConfigureAwait(false);
        if (_superblockFile is { } file) {
            _superblockFile = null;
            await DisposeFiles([file]).ConfigureAwait(false);
        }
    }

    private async ValueTask CloseLogs()
    {
        if (_data is { } data) {
            try {
                await data.DisposeAsync().ConfigureAwait(false);
            }
            catch {
                // Ignored: the data files hold nothing that isn't already committed or expendable.
            }
        }
        foreach (var log in _indexLogs) {
            try {
                await log.DisposeAsync().ConfigureAwait(false);
            }
            catch {
                // Ignored: the index is derivable from the data prefix (§3.3).
            }
        }
        _indexLogs = [];
        if (_cache is { } cache)
            cache.Clear();
    }

    private void DisposeKeyMaterial()
    {
        CryptographicOperations.ZeroMemory(_hashKey);
        CryptographicOperations.ZeroMemory(_indexMacKey);
        (_cipherFactory as IDisposable)?.Dispose();
        _superblock.Dispose();
    }

    private void WipeFiles()
    {
        var name = Path.GetFileName(_options.BasePath);
        foreach (var path in _storage.ListFiles(Path.GetDirectoryName(_options.BasePath) ?? "", name + ".*")) {
            if (!IsOwnFile(name, Path.GetFileName(path)))
                continue;
            try {
                _storage.Delete(path);
            }
            catch {
                // Best-effort: a file we can't delete is one the recreate below overwrites anyway.
            }
        }
    }

    private static bool IsOwnFile(string baseName, string fileName)
    {
        // Exact suffixes only. A loose glob meant that wiping `cache` also deleted a caller's
        // `cache.backup.klog` sitting in the same directory (I31).
        if (fileName.Length <= baseName.Length || !fileName.StartsWith(baseName, StringComparison.Ordinal))
            return false;

        var suffix = fileName.AsSpan(baseName.Length);
        if (suffix is ".kvs")
            return true;
        if (suffix is ".0.kdat" or ".1.kdat" or ".0.kidx" or ".1.kidx")
            return true;

        // Pre-superblock leftovers, so the first open on the new layout doesn't strand a v1 file set.
        return suffix is ".kidx" or ".clean" or ".kidx.tmp" || IsNumericSuffix(suffix, ".klog");
    }

    private static bool IsNumericSuffix(ReadOnlySpan<char> suffix, string extension)
    {
        if (suffix.Length < extension.Length + 2 || suffix[0] != '.'
            || !suffix.EndsWith(extension, StringComparison.Ordinal))
            return false;

        foreach (var c in suffix[1..^extension.Length])
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }

    // --- Private: the commit protocol (§5.1) -------------------------------

    private async ValueTask Commit(bool isForced)
    {
        if (!isForced && _uncommittedBytes == 0)
            return;

        // The order is load-bearing: the flush must *return* before the superblock write is *issued*, which
        // is what proof step (b) in §6 rests on.
        await _data.Flush().ConfigureAwait(false);
        if (_options.Durability == KvasarDurability.Flushed)
            await _data.FlushToDisk().ConfigureAwait(false);
        var dataCommitLength = await _data.MarkCommitted().ConfigureAwait(false);

        if (MustRotateIndex())
            await RotateIndex(_data.ActiveCommittedOffset).ConfigureAwait(false);

        var indexLog = _indexLogs[_indexSlot];
        var generation = _generation + 1;
        await indexLog.WriteCommitMac(generation).ConfigureAwait(false);
        await _superblock.Write(_superblockFile!, new SuperblockState(
            generation, (byte)_data.ActiveSlot, dataCommitLength,
            (byte)_indexSlot, indexLog.Length, _data.ActiveLiveBytes, _data.ActiveDeadBytes))
            .ConfigureAwait(false);
        _generation = generation;
        _uncommittedBytes = 0;
        if (_isSlotSwitchPending) {
            _slotSwitchGeneration = _generation;
            _isSlotSwitchPending = false;
        }
    }

    private bool MustRotateIndex()
    {
        if (_isCompacting)
            return false;
        if (_mustRotateIndex)
            return true;
        if (!_mustPersistIndex || _isSlotSwitchPending || _generation <= _slotSwitchGeneration)
            return false;

        // Keep the delta tail from outgrowing the checkpoint it sits on: rotating rewrites the whole
        // index, so doing it eagerly costs more than the replay it saves.
        var checkpointBytes = IndexLog.HeaderSize + ((long)_index.Count * IndexLog.EntrySize);
        return _indexLogs[_indexSlot].Length > (2 * checkpointBytes) + (64L * IndexLog.EntrySize);
    }

    private async ValueTask RotateIndex(long dataStamp)
    {
        var slot = 1 - _indexSlot;
        IndexEntry[] entries = _mustPersistIndex ? _index.Snapshot().ToArray() : [];
        // An entry-less checkpoint is consistent with offset 0, not with the extent — it carries no
        // record of anything. Stamping it at dataStamp made recovery compute replayFrom == committedOffset,
        // replay nothing, and adopt an empty index: every key gone on the first reopen, no crash needed.
        // Stamping 0 makes recovery replay the whole committed log; damaged pages are skipped by that
        // best-effort rebuild after the candidate generation has been validated independently.
        var stamp = _mustPersistIndex ? dataStamp : 0L;
        await _indexLogs[slot].WriteCheckpoint(entries, stamp).ConfigureAwait(false);
        _indexSlot = slot;
        _isSlotSwitchPending = true;
        _mustRotateIndex = false;
    }

    private ValueTask OnWritesPublished(long bytes)
    {
        _uncommittedBytes += bytes;
        // §2.2: first-dirty + FlushDelay, or CommitBytes of uncommitted work, whichever comes first. The
        // byte trigger bounds how much recovery has to validate, not just how much a crash can lose.
        if (_flushDelay > TimeSpan.Zero && _uncommittedBytes < _options.CommitBytes) {
            MarkDirty();
            return default;
        }
        return CommitAndMaybeCompact();
    }

    private async ValueTask CommitAndMaybeCompact()
    {
        await Commit(false).ConfigureAwait(false);
        await MaybeCompact().ConfigureAwait(false);
    }

    private async ValueTask MaybeCompact()
    {
        // §4: one trigger, checked at commit, over the store as a whole rather than per file.
        if (_isCompacting || Volatile.Read(ref _isDisposeStarted) != 0)
            return;

        var dead = _data.DeadBytes;
        var total = _data.LiveBytes + dead;
        if (dead < _options.CompactionMinBytes || total <= 0
            || (double)dead / total < _options.CompactionDeadRatio)
            return;

        var compactionTask = await CompactCore(CancellationToken.None).ConfigureAwait(false);
        if (compactionTask is not null)
            _ = ObserveCompaction(compactionTask);
    }

    private static async Task ObserveCompaction(Task compactionTask)
    {
        try {
            await compactionTask.ConfigureAwait(false);
        }
        catch {
            // Auto-compaction is maintenance; its rollback is complete before the task faults.
        }
    }

    private void MarkDirty()
    {
        // Only the clean⇒dirty edge opens a batch, so the flush loop sleeps on a signal rather than
        // waking on a timer while the store is idle.
        if (Interlocked.Exchange(ref _isDirty, 1) == 0)
            _whenDirty.TrySetResult();
    }

    private async Task RunFlushLoop()
    {
        // Bounds how long a write can sit uncommitted. Losing writes newer than the last commit is the
        // accepted cost of FlushDelay; losing anything older would not be, so failures only retry.
        //
        // The delay is armed on the clean⇒dirty edge rather than run as a period: an idle store costs
        // zero wakeups, where the fixed-period loop woke every FlushDelay for the life of the process
        // and usually did nothing. The staleness bound is identical either way, since the *first* write
        // of a batch waits the full delay and later ones wait less.
        try {
            while (!_disposeCts.IsCancellationRequested) {
                await _whenDirty.Task.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                await Task.Delay(_flushDelay, _disposeCts.Token).ConfigureAwait(false);
                // Re-arm before committing, so a write landing during the commit opens the next batch
                // rather than being swallowed by this one.
                _whenDirty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (Interlocked.Exchange(ref _isDirty, 0) == 0)
                    continue;
                try {
                    await FlushCore(_disposeCts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException) {
                    // Best-effort: a failed background commit costs durability, never consistency, so the
                    // next tick simply tries again.
                    MarkDirty();
                }
            }
        }
        catch (OperationCanceledException) {
            // Disposed.
        }
    }

    // --- Private: compaction (§4) ------------------------------------------

    private async ValueTask<Task?> CompactCore(CancellationToken cancellationToken)
    {
        if (_isCompacting)
            return _compactionTask;
        if (Volatile.Read(ref _isDisposeStarted) != 0)
            return null;

        // A data file may be recycled only once no valid superblock slot names it, which with two slots
        // means the switch commit and one further commit both have to pass (§3.2).
        while (_isSlotSwitchPending || _generation <= _slotSwitchGeneration)
            await Commit(true).ConfigureAwait(false);

        var sourceSlot = _data.ActiveSlot;
        var targetSlot = await _data.BeginCompaction().ConfigureAwait(false);
        try {
            var entries = _index.Snapshot().ToArray();
            Array.Sort(entries, static (a, b) => a.PackedLocator.CompareTo(b.PackedLocator));
            var cancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
            var compaction = new CompactionState(sourceSlot, targetSlot, entries, cancellationSource);
            _compaction = compaction;
            _isCompacting = true;
            var compactionTask = RunCompaction(compaction);
            _compactionTask = compactionTask;
            return compactionTask;
        }
        catch {
            _data.AbortCompaction();
            _data.ResetAccounting(targetSlot);
            throw;
        }
    }

    private async Task RunCompaction(CompactionState compaction)
    {
        // Reads are concurrent; only bounded target-appends/CAS batches and the final switch take the lock.
        await Task.Yield();
        try {
            while (compaction.NextEntry < compaction.Entries.Length) {
                var batch = await ReadCompactionBatch(compaction).ConfigureAwait(false);
                await _writeLock.WaitAsync(compaction.CancellationToken).ConfigureAwait(false);
                try {
                    if (!ReferenceEquals(_compaction, compaction))
                        return;
                    await ApplyCompactionBatch(compaction, batch).ConfigureAwait(false);
                }
                finally {
                    _writeLock.Release();
                }
                await Task.Yield();
            }

            await _writeLock.WaitAsync(compaction.CancellationToken).ConfigureAwait(false);
            try {
                if (ReferenceEquals(_compaction, compaction))
                    await FinishCompaction(compaction).ConfigureAwait(false);
            }
            finally {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) {
            await AbortCompactionPass(compaction).ConfigureAwait(false);
        }
        catch {
            await AbortCompactionPass(compaction).ConfigureAwait(false);
            throw;
        }
        finally {
            compaction.CancellationSource.Dispose();
        }
    }

    private async ValueTask<List<CompactionCopy>> ReadCompactionBatch(CompactionState compaction)
    {
        var batch = new List<CompactionCopy>(CompactionBatchRecords);
        var bytes = 0L;
        while (compaction.NextEntry < compaction.Entries.Length
            && batch.Count < CompactionBatchRecords
            && (bytes < CompactionBatchBytes || batch.Count == 0)) {
            compaction.CancellationToken.ThrowIfCancellationRequested();
            var entry = compaction.Entries[compaction.NextEntry++];
            RecordRead read;
            try {
                read = await _data.TryReadRecord(entry.Locator, compaction.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                batch.Add(new CompactionCopy(entry, default, true, false));
                continue;
            }

            var view = read.View;
            var isCopy = read.IsFound && !view.IsTombstone
                && _hasher.Hash(view.Key.Span, _hashKey) == entry.KeyHash;
            batch.Add(new CompactionCopy(entry, view, false, isCopy));
            if (isCopy)
                bytes += entry.Length;
        }
        return batch;
    }

    private async ValueTask ApplyCompactionBatch(
        CompactionState compaction, List<CompactionCopy> batch)
    {
        var pending = new List<CompactionRelocation>(batch.Count);
        foreach (var copy in batch) {
            var entry = copy.Entry;
            if (copy.IsCorrupt) {
                _index.Remove(entry.KeyHash, entry.Locator);
                continue;
            }
            if (!copy.IsCopy)
                continue;

            var view = copy.View;
            var (newLoc, newLength) = await _data
                .AppendToTarget(view.Flags, view.ValueKind, view.Key, view.Value, false)
                .ConfigureAwait(false);
            pending.Add(new CompactionRelocation(
                entry.KeyHash, entry.Locator, (int)entry.Length, newLoc, newLength));
        }
        if (pending.Count == 0)
            return;

        await _data.SealCompactionTarget().ConfigureAwait(false);
        foreach (var relocation in pending) {
            compaction.Relocated.Add(relocation);
            compaction.RelocatedByTarget.Add(relocation.TargetLocator.Packed, relocation);
            if (_index.Set(
                    relocation.KeyHash, relocation.TargetLocator, relocation.TargetLength,
                    relocation.SourceLocator))
                continue;

            compaction.RelocatedByTarget.Remove(relocation.TargetLocator.Packed);
            compaction.Relocated.RemoveAt(compaction.Relocated.Count - 1);
            _data.OnSuperseded(relocation.TargetLocator, relocation.TargetLength);
        }
    }

    private async ValueTask FinishCompaction(CompactionState compaction)
    {
        // No target locator may reach a persisted checkpoint before the commit that also switches data slots.
        await _data.SealCompactionTarget().ConfigureAwait(false);
        foreach (var relocation in compaction.WriteRelocations) {
            compaction.Relocated.Add(relocation);
            if (_index.Set(
                    relocation.KeyHash, relocation.TargetLocator, relocation.TargetLength,
                    relocation.SourceLocator))
                continue;

            compaction.Relocated.RemoveAt(compaction.Relocated.Count - 1);
            _data.OnSuperseded(relocation.TargetLocator, relocation.TargetLength);
        }

        var targetFileId = (uint)compaction.TargetSlot + 1;
        if (_index.Snapshot().Any(e => e.Locator.FileId != targetFileId))
            throw new InvalidOperationException("Compaction left an index entry in the drained slot.");

        await _data.CommitCompaction(compaction.TargetSlot).ConfigureAwait(false);
        _data.ResetAccounting(compaction.SourceSlot);
        _isSlotSwitchPending = true;
        _mustRotateIndex = true;
        _compaction = null;
        _isCompacting = false;
        await Commit(true).ConfigureAwait(false);
    }

    private async Task AbortCompactionPass(CompactionState compaction)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try {
            if (ReferenceEquals(_compaction, compaction))
                AbortCompaction(compaction);
        }
        finally {
            _writeLock.Release();
        }
    }

    private void AbortCompaction(CompactionState compaction)
    {
        compaction.CancellationSource.Cancel();
        for (var i = compaction.Relocated.Count - 1; i >= 0; i--) {
            var relocation = compaction.Relocated[i];
            if (_index.Set(
                    relocation.KeyHash, relocation.SourceLocator, relocation.SourceLength,
                    relocation.TargetLocator))
                continue;
            if (!compaction.SupersededSources.Contains(relocation.SourceLocator.Packed))
                _data.OnSuperseded(relocation.SourceLocator, relocation.SourceLength);
        }
        _data.AbortCompaction();
        _data.ResetAccounting(compaction.TargetSlot);
        _compaction = null;
        _isCompacting = false;
    }

    // --- Private: append / publish -----------------------------------------

    private (KvasarKey Key, KvasarValue? Value) CopyUpdate(KvasarKey key, KvasarValue? value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, KvasarConstants.MaxKeyBytes);
        var copiedKey = new KvasarKey(key.Memory.ToArray());
        if (value is not { } record || record.Length > _options.MaxValueBytes)
            return (copiedKey, value);
        return (copiedKey, new KvasarValue(record.Memory.ToArray(), record.Kind));
    }

    private async ValueTask<AppendResult> AppendOne(KvasarKey key, KvasarValue? value)
    {
        // The source copy keeps interim commits recoverable; the target copy becomes canonical at the switch.
        var isTombstone = value is null;
        var record = value ?? default;
        if (!isTombstone && record.Length > _options.MaxValueBytes) {
            if (_options.OversizedValueThrows)
                throw new ArgumentException($"Value exceeds MaxValueBytes ({_options.MaxValueBytes}).", nameof(value));
            // §12: an oversized value isn't stored. It is recorded as a *delete* rather than skipped,
            // because skipping left the previous value in place — so the key kept serving data the caller
            // had already replaced, permanently and silently (I16). A miss costs one upstream lookup;
            // stale data has no downstream defence. Throwing doesn't help on its own: callers ignore a
            // throw from Set and carry on believing the write landed.
            isTombstone = true;
            record = default;
        }
        var compactionLocator = Locator.None;
        if (!isTombstone && _compaction is not null) {
            (compactionLocator, _) = await _data
                .AppendToTarget(RecordFlags.None, record.Kind, key.Memory, record.Memory, false)
                .ConfigureAwait(false);
        }
        var (locator, recordLength) = await _data
            .Append(RecordFlags.None, record.Kind, key.Memory, record.Memory, isTombstone)
            .ConfigureAwait(false);
        return new AppendResult(locator, compactionLocator, recordLength, isTombstone);
    }

    private async ValueTask Publish(KvasarKey key, ulong h, AppendResult appended)
    {
        var (loc, compactionLoc, recordLength, isTombstone) = appended;
        var old = await FindIndexed(key, h, loc).ConfigureAwait(false);
        if (isTombstone) {
            if (old.IsFound) {
                _index.Remove(h, old.Locator);
                TrackCompactionSupersession(old.Locator);
                _data.OnSuperseded(old.Locator, old.Length);
            }
            _data.OnSuperseded(loc, recordLength); // the tombstone itself is reclaimable space
            await AppendDelta(h, old.KeyId, loc, recordLength, true).ConfigureAwait(false);
        }
        else {
            if (!compactionLoc.IsNone && _compaction is { } compaction)
                compaction.WriteRelocations.Add(new CompactionRelocation(
                    h, loc, recordLength, compactionLoc, recordLength));
            if (old.IsFound) {
                TrackCompactionSupersession(old.Locator);
                _data.OnSuperseded(old.Locator, old.Length);
                if (!_index.Set(h, loc, recordLength, old.Locator))
                    throw new InvalidOperationException("The index entry changed while the write lock was held.");
            }
            if (!old.IsFound)
                _index.Add(h, old.KeyId, loc, recordLength);
            await AppendDelta(h, old.KeyId, loc, recordLength, false).ConfigureAwait(false);
        }
    }

    private async ValueTask<IndexedRecord> FindIndexed(KvasarKey key, ulong keyHash, Locator newLoc)
    {
        var isKeyIdUsed = false;
        IndexedRecord? unreadable = null;
        var cursor = _index.Probe(keyHash);
        while (cursor.MoveNext(out var loc, out var length)) {
            if (cursor.CurrentHash != keyHash)
                continue;
            if (cursor.CurrentKeyId == newLoc.Packed)
                isKeyIdUsed = true;
            try {
                RecordView view;
                if (!_data.TryReadRecordCached(loc, out view)) {
                    var read = await _data.TryReadRecord(loc, CancellationToken.None).ConfigureAwait(false);
                    if (!read.IsFound) {
                        unreadable ??= new IndexedRecord(true, loc, length, cursor.CurrentKeyId);
                        continue;
                    }
                    view = read.View;
                }
                if (!view.IsTombstone && view.Key.Span.SequenceEqual(key.Span))
                    return new IndexedRecord(true, loc, length, cursor.CurrentKeyId);
            }
            catch (KvasarCorruptException) {
                unreadable ??= new IndexedRecord(true, loc, length, cursor.CurrentKeyId);
            }
        }
        if (unreadable is { } candidate)
            return candidate;

        var keyId = isKeyIdUsed ? MintKeyId(keyHash, newLoc) : newLoc.Packed;
        return new IndexedRecord(false, default, 0, keyId);
    }

    private ulong MintKeyId(ulong keyHash, Locator loc)
    {
        var keyId = loc.Packed;
        while (true) {
            var isUsed = false;
            var cursor = _index.Probe(keyHash);
            while (cursor.MoveNext(out _, out _)) {
                if (cursor.CurrentHash == keyHash && cursor.CurrentKeyId == keyId) {
                    isUsed = true;
                    break;
                }
            }
            if (!isUsed)
                return keyId;
            keyId = keyId == ulong.MaxValue ? 1 : keyId + 1;
        }
    }

    private void TrackCompactionSupersession(Locator locator)
    {
        if (_compaction is not { } compaction
            || !compaction.RelocatedByTarget.TryGetValue(locator.Packed, out var relocation)
            || !compaction.SupersededSources.Add(relocation.SourceLocator.Packed))
            return;
        _data.OnSuperseded(relocation.SourceLocator, relocation.SourceLength);
    }

    private async ValueTask AppendDelta(
        ulong keyHash, ulong keyId, Locator loc, int length, bool isTombstone)
    {
        if (!_mustPersistIndex)
            return;

        var entry = new IndexEntry {
            KeyHash = keyHash,
            PackedLocator = loc.Packed,
            KeyId = keyId,
            Length = (uint)length,
            Flags = isTombstone ? (byte)RecordFlags.Tombstone : (byte)0,
        };
        try {
            await _indexLogs[_indexSlot].AppendDelta(entry).ConfigureAwait(false);
        }
        catch {
            // The index is a rebuildable hint, so a failed delta is non-fatal: the commit still names a
            // prefix longer than the file holds, which recovery reads as "replay from the checkpoint".
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

    private async ValueTask<KvasarValue?> GetManyValue(
        KvasarKey key, ulong keyHash, Locator firstLoc, CancellationToken cancellationToken)
    {
        var value = await TryReadValue(firstLoc, key, cancellationToken).ConfigureAwait(false);
        if (value is not null)
            return value;

        var cursor = _index.Probe(keyHash);
        while (cursor.MoveNext(out var loc, out _)) {
            if (cursor.CurrentHash != keyHash || loc == firstLoc)
                continue;
            value = await TryReadValue(loc, key, cancellationToken).ConfigureAwait(false);
            if (value is not null)
                return value;
        }
        return null;
    }

    private async ValueTask<KvasarValue?> TryReadValue(
        Locator loc, KvasarKey key, CancellationToken cancellationToken)
    {
        try {
            var read = await _data.TryReadRecord(loc, cancellationToken).ConfigureAwait(false);
            if (!read.IsFound || read.View.IsTombstone)
                return null;
            if (!read.View.Key.Span.SequenceEqual(key.Span))
                return null; // hash collision ⇒ different key
            return new KvasarValue(read.View.Value, read.View.ValueKind);
        }
        catch (Exception e) when (e is not OperationCanceledException) {
            // A page that fails its tag is a miss, never an error (§5.3): it can only be one that a torn
            // tail burned or that never landed, and the caller's answer for both is "not cached". The
            // same goes for a slot recycled under a locator a reader was already holding.
            return null;
        }
    }

    private bool IsLiveIndexEntry(IndexEntry entry)
    {
        var cursor = _index.Probe(entry.KeyHash);
        while (cursor.MoveNext(out var locator, out _))
            if (cursor.CurrentHash == entry.KeyHash
                && cursor.CurrentKeyId == entry.KeyId
                && locator == entry.Locator)
                return true;
        return false;
    }

    private void ApplyLoaded(Locator loc, in RecordView view, int recordLength)
    {
        // The adopted superblock supplies accounting after replay, so recovery does not mutate it here.
        var h = _hasher.Hash(view.Key.Span, _hashKey);
        var findTask = FindIndexed(new KvasarKey(view.Key), h, loc);
        var old = findTask.IsCompletedSuccessfully
            ? findTask.Result
            : findTask.AsTask().GetAwaiter().GetResult();
        if (view.IsTombstone) {
            if (old.IsFound)
                _index.Remove(h, old.Locator);
        }
        else if (!old.IsFound)
            _index.Add(h, old.KeyId, loc, recordLength);
        else if (!_index.Set(h, loc, recordLength, old.Locator))
            throw new InvalidOperationException("The loaded index entry changed during recovery.");
    }

    private void SeedAccounting(SuperblockState state)
    {
        // Accounting only drives the compaction trigger, so a pair that cannot describe this committed
        // extent must not fail adoption: the slot is authenticated, which makes an out-of-range value a
        // bug or a store written before the counters were trusted — never tampering. Rejecting it here
        // would reach WipeFiles through TryAdopt and throw away intact data over a hint. Fall back to
        // deriving it from the index instead, which is where these numbers came from before §3.1.
        var committedOffset = _data.ActiveCommittedOffset;
        var isUsable = state.LiveBytes >= 0 && state.DeadBytes >= 0
            && state.LiveBytes <= committedOffset
            && state.DeadBytes <= committedOffset - state.LiveBytes
            && state.DeadBytes <= long.MaxValue - _data.BurnedBytes;
        if (isUsable) {
            _data.SeedAccounting(state.DataSlot, state.LiveBytes, state.DeadBytes + _data.BurnedBytes);
            return;
        }

        // FileId is 1-based (Locator), so slot n is file id n+1.
        var live = 0L;
        foreach (var e in _index.Snapshot())
            if ((int)e.Locator.FileId - 1 == state.DataSlot)
                live += e.Length;
        _data.SeedAccounting(state.DataSlot, live);
        _data.ResetAccounting(1 - state.DataSlot);
    }

    private uint MintCacheId()
        // PageCache keys *decrypted* pages by (fileId, pageId) and the .kdat header's id is unauthenticated
        // plaintext, so the ids come from here instead — the cache is per-process, so a counter does (§3.2).
        => unchecked((uint)Interlocked.Increment(ref _nextCacheId));

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed), this);

    private static int ResolveFreshPageSize(KvasarOptions options)
    {
        var size = options.PageSize > 0 ? options.PageSize : KvasarConstants.DefaultPageSize;
        if (size < KvasarConstants.MinPageSize || size > KvasarConstants.MaxPageSize || (size & (size - 1)) != 0)
            throw new ArgumentException(
                $"PageSize must be a power of two in [{KvasarConstants.MinPageSize}, {KvasarConstants.MaxPageSize}].",
                nameof(options));
        return size;
    }

    private static int ResolveInlineCap(KvasarOptions options, int pageSize)
        => options.MaxInlineValueBytes > 0 ? Math.Min(options.MaxInlineValueBytes, pageSize) : pageSize;

    private static uint ParseFormatVersion(string formatVersion, string? version)
    {
        // The app-level Version folds into the same on-disk tag as FormatVersion: both are bound into the
        // superblock as GCM AAD, so changing either makes the next open read FormatMismatch and wipe &
        // recreate the store (§11).
        if (string.IsNullOrEmpty(version) && uint.TryParse(formatVersion, out var v))
            return v;
        // Anything else ⇒ stable 32-bit hash (FNV-1a) so it still round-trips on reopen.
        uint hash = 2166136261;
        Add(formatVersion);
        Add("\0"); // separator, so ("1", "0") and ("10", "") don't hash alike
        Add(version ?? "");
        return hash | 0x8000_0000; // keep it distinct from small numeric versions

        void Add(string s) {
            foreach (var b in Encoding.UTF8.GetBytes(s)) {
                hash ^= b;
                hash *= 16777619;
            }
        }
    }

    // Nested types

    private readonly record struct AppendResult(
        Locator Locator, Locator CompactionLocator, int RecordLength, bool IsTombstone);
    private readonly record struct IndexedRecord(bool IsFound, Locator Locator, int Length, ulong KeyId);

    private readonly record struct CompactionCopy(
        IndexEntry Entry, RecordView View, bool IsCorrupt, bool IsCopy);

    private readonly record struct CompactionRelocation(
        ulong KeyHash,
        Locator SourceLocator,
        int SourceLength,
        Locator TargetLocator,
        int TargetLength);

    private sealed class CompactionState
    {
        public int SourceSlot { get; }
        public int TargetSlot { get; }
        public IndexEntry[] Entries { get; }
        public CancellationTokenSource CancellationSource { get; }
        public CancellationToken CancellationToken => CancellationSource.Token;
        public List<CompactionRelocation> Relocated { get; }
        public Dictionary<ulong, CompactionRelocation> RelocatedByTarget { get; }
        public HashSet<ulong> SupersededSources { get; }
        public List<CompactionRelocation> WriteRelocations { get; }
        public int NextEntry;

        public CompactionState(
            int sourceSlot, int targetSlot, IndexEntry[] entries,
            CancellationTokenSource cancellationSource)
        {
            SourceSlot = sourceSlot;
            TargetSlot = targetSlot;
            Entries = entries;
            CancellationSource = cancellationSource;
            var initialCapacity = Math.Min(entries.Length, 1024);
            Relocated = new List<CompactionRelocation>(initialCapacity);
            RelocatedByTarget = new Dictionary<ulong, CompactionRelocation>(initialCapacity);
            SupersededSources = new HashSet<ulong>();
            WriteRelocations = new List<CompactionRelocation>(initialCapacity);
        }
    }
}
