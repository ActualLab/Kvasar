using System.Collections.Concurrent;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// One <c>.kdat</c> data file (DESIGN-Durability.md §3.2): a plaintext <see cref="SegmentHeader"/> followed
/// by sealed, individually authenticated pages. Page ids never rewind, so a nonce — a pure function of
/// <c>(fileSalt, pageId)</c> — is never reused; <see cref="Recycle"/> re-stamps a fresh salt and restarts
/// the ids instead of unlinking the file.
/// </summary>
public sealed class PagedFile : IAsyncDisposable
{
    // Appended pages are staged here and written in one I/O, because a per-page Write costs far more
    // than the memcpy: batching turns a 64-record SetMany at 4 KiB values into 1 write instead of 64.
    private const int MaxPendingBytes = 1 << 20;

    private readonly IStorageFile _file;
    private readonly IPageCipherFactory _cipherFactory;
    private readonly PageCache _cache;
    private readonly uint _formatVer;
    private readonly int _onDiskPageSize;
    private readonly byte[] _pending;
    private readonly int _pendingCapacity;
    // Plaintext of staged pages. They aren't on disk yet, so a reader that misses the (evictable) page
    // cache must be served from here — compaction scans the file while appending to it.
    private readonly ConcurrentDictionary<long, byte[]> _pendingPlain = new();
    // The (cache id, cipher) pair a page must be decrypted and cached under, as one immutable value.
    // Recycle swaps it atomically: readers are lock-free, so reading the two separately let a reader
    // decrypt with the old cipher and then publish under the *new* id, poisoning the cache with a page
    // from the slot's previous life. AES-GCM cannot catch that — both pages are genuine.
    private volatile Incarnation _incarnation;
    private long _pendingFirstPageId;
    private int _pendingCount;
    private long _pageCount;
    // Whole pages whose bytes are known to be *in* the file. Deliberately not derived from
    // IStorageFile.Length: that counts issued writes, so it runs ahead of the data during a Flush.
    private long _flushedPageCount;
    private long _commitLength;

    // Identifies this file's current incarnation; also the PageCache key, so it must be unique among the
    // files sharing that cache and must change whenever the file is recycled.
    public uint FileId => _incarnation.FileId;
    public int PageSize { get; }
    // Pages that physically exist (or are staged); equivalently, the next page id to be issued.
    public long PageCount => Volatile.Read(ref _pageCount);
    public long CommittedPageCount => (CommitLength - KvasarConstants.SegmentHeaderSize) / _onDiskPageSize;
    public long CommitLength => Volatile.Read(ref _commitLength);
    // Logical end of the file. Counts staged pages, and page ids burned by a torn tail.
    public long Length => PagePosition(PageCount);
    public long ResumeLength => PagePosition(ResumePageId);
    // The lowest page id this incarnation may append at. Uncommitted pages below it are a crash's
    // leftovers: readable, unreferenced, and accounted dead by the store.
    public long ResumePageId { get; private set; }

    public static async ValueTask<PagedFile> Create(
        IStorageFile file, uint fileId, int pageSize, IPageCipherFactory cipherFactory,
        uint formatVer, PageCache cache)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(cipherFactory);
        ArgumentNullException.ThrowIfNull(cache);
        if (pageSize < KvasarConstants.MinPageSize || pageSize > KvasarConstants.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        try {
            var flags = cipherFactory.Overhead == 0 ? 0u : KvasarConstants.EncryptedDataFileFlag;
            var header = new SegmentHeader(formatVer, pageSize, fileId, flags);
            await WriteHeader(file, header).ConfigureAwait(false);
            return new PagedFile(file, cipherFactory, cache, header, 0, KvasarConstants.SegmentHeaderSize);
        }
        catch {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<PagedFile> Open(
        IStorageFile file, IPageCipherFactory cipherFactory, uint formatVer, PageCache cache,
        long commitLength = -1, uint? cacheId = null, CancellationToken cancellationToken = default)
    {
        // cacheId overrides the header's id for PageCache keying. The header is unauthenticated
        // plaintext, and the cache holds *decrypted* pages keyed by (fileId, pageId) — so two files
        // whose headers claimed the same id would serve each other's plaintext, past the point where
        // AES-GCM could catch it. Callers mint ids instead; the cache is per-process, so a counter does.
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(cipherFactory);
        ArgumentNullException.ThrowIfNull(cache);

        try {
            return await OpenCore(
                file, cipherFactory, formatVer, cache, commitLength, cacheId, 0, cancellationToken)
                .ConfigureAwait(false);
        }
        catch {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async ValueTask<PagedFile> OpenOrCreateFree(
        IStorageFile file, uint fileId, int pageSize,
        IPageCipherFactory cipherFactory, uint formatVer, PageCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(cipherFactory);
        ArgumentNullException.ThrowIfNull(cache);
        if (pageSize < KvasarConstants.MinPageSize || pageSize > KvasarConstants.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        try {
            return await OpenCore(
                file, cipherFactory, formatVer, cache, -1, fileId, pageSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KvasarCorruptException) {
            return await Create(file, fileId, pageSize, cipherFactory, formatVer, cache).ConfigureAwait(false);
        }
        catch {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private PagedFile(
        IStorageFile file, IPageCipherFactory cipherFactory, PageCache cache,
        SegmentHeader header, long pageCount, long commitLength, uint? cacheId = null)
    {
        _file = file;
        _cipherFactory = cipherFactory;
        _cache = cache;
        _formatVer = header.FormatVer;
        _onDiskPageSize = header.PageSize + cipherFactory.Overhead;
        _pageCount = pageCount;
        _commitLength = commitLength;
        _pendingCapacity = Math.Max(1, MaxPendingBytes / _onDiskPageSize);
        _pending = new byte[_pendingCapacity * _onDiskPageSize];
        _incarnation = new Incarnation(cacheId ?? header.SegmentId, cipherFactory.Create(header.FileSalt));
        PageSize = header.PageSize;
        ResumePageId = pageCount;
        // Whole pages only: a torn trailing page is present but not readable as a page.
        _flushedPageCount = (commitLength - KvasarConstants.SegmentHeaderSize) / _onDiskPageSize;
    }

    public async ValueTask DisposeAsync()
    {
        try {
            await Flush().ConfigureAwait(false);
        }
        catch {
            // Ignored: a tail lost here is below no committed extent, so it is exactly the dead space
            // that the never-rewind rule already accounts for.
        }
        try {
            await _file.DisposeAsync().ConfigureAwait(false);
        }
        finally {
            (_incarnation.Cipher as IDisposable)?.Dispose();
        }
    }

    public bool TryGetCachedPage(long pageId, out ReadOnlyMemory<byte> page)
    {
        var incarnation = _incarnation;
        if ((ulong)pageId < (ulong)PageCount) {
            if (_cache.TryGet(incarnation.FileId, pageId, out var cached)) {
                page = cached;
                return true;
            }
            if (_pendingPlain.TryGetValue(pageId, out var staged)) {
                page = staged;
                return true;
            }
        }
        page = default;
        return false;
    }

    internal bool TryAcquireRead(uint fileId, out ReadLease lease)
    {
        var incarnation = _incarnation;
        if (incarnation.FileId == fileId && incarnation.TryAcquireRead()) {
            lease = new ReadLease(incarnation);
            return true;
        }
        lease = default;
        return false;
    }

    public ValueTask<ReadOnlyMemory<byte>> GetPage(long pageId, CancellationToken cancellationToken = default)
    {
        // Deliberately not an async method: a cache hit returns an already-completed ValueTask, so the
        // hot read path costs no state machine and no allocation. Only a miss builds one.
        var incarnation = _incarnation;
        if ((ulong)pageId >= (ulong)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageId));
        if (_cache.TryGet(incarnation.FileId, pageId, out var cached))
            return new ValueTask<ReadOnlyMemory<byte>>(cached);
        if (_pendingPlain.TryGetValue(pageId, out var staged))
            return new ValueTask<ReadOnlyMemory<byte>>(staged);
        return ReadAndCache(incarnation, pageId, cancellationToken);
    }

    public async ValueTask ReadPage(long pageId, Memory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length != PageSize)
            throw new ArgumentException("Payload length must equal PageSize.", nameof(payload));
        var page = await GetPage(pageId, cancellationToken).ConfigureAwait(false);
        page.CopyTo(payload);
    }

    public async ValueTask Prefetch(long firstPageId, int maxPages, CancellationToken cancellationToken = default)
    {
        // Best-effort readahead for sequential walks: pulls a run of pages in one I/O instead of faulting
        // them one at a time. Any failure is swallowed — this only warms the cache, so the normal read
        // path stays the single place that decides what a bad page means. The incarnation is captured
        // before its flushed bound and rechecked after the read, so Recycle discards the whole run.
        if (firstPageId < 0 || maxPages <= 0)
            return;
        try {
            var inc = _incarnation;
            // Bound by pages whose writes have *completed*. Using _file.Length here read bytes belonging
            // to a write still in flight: with a real cipher that page failed authentication and was
            // swallowed, but under NoopPageCipher it decrypted to garbage and was cached under a valid
            // (fileId, pageId) — permanently, since Add keeps the first entry. Unlike GetPage, Prefetch
            // reads a whole run straight from the file and never consults _pendingPlain.
            var onDisk = Volatile.Read(ref _flushedPageCount);
            if (firstPageId >= onDisk)
                return;
            var count = (int)Math.Min(maxPages, onDisk - firstPageId);
            if (count <= 0)
                return;

            var byteLength = count * _onDiskPageSize;
            var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
            try {
                await _file.ReadExact(PagePosition(firstPageId), buffer.AsMemory(0, byteLength), cancellationToken)
                    .ConfigureAwait(false);
                if (!ReferenceEquals(_incarnation, inc))
                    return;

                for (var i = 0; i < count; i++) {
                    var pageId = firstPageId + i;
                    if (_cache.TryGet(inc.FileId, pageId, out _))
                        continue;
                    var page = new byte[PageSize];
                    inc.Cipher.Decrypt(pageId, buffer.AsSpan(i * _onDiskPageSize, _onDiskPageSize), page);
                    _cache.Add(inc.FileId, pageId, page);
                }
            }
            finally {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch {
            // Ignored: a prefetch miss just means the normal path reads (and validates) that page itself.
        }
    }

    // No CancellationToken on the write path, by design: abandoning an append or a flush partway would
    // leave a half-written record in the log (a record's bytes are self-describing, so recovery would
    // read the following records as its tail). Writes are local, bounded, and fast; the caller's token
    // guards the *wait* for the write lock instead.
    public async ValueTask<long> AppendPage(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length != PageSize)
            throw new ArgumentException("Payload length must equal PageSize.", nameof(payload));

        if (_pendingCount == _pendingCapacity)
            await Flush().ConfigureAwait(false);

        var inc = _incarnation;
        var pageId = _pageCount;
        if (_pendingCount == 0)
            _pendingFirstPageId = pageId;
        inc.Cipher.Encrypt(pageId, payload.Span, _pending.AsSpan(_pendingCount * _onDiskPageSize, _onDiskPageSize));
        _pendingCount++;

        // Cache the immutable plaintext copy, then publish the new page (record-before-index ordering).
        var page = payload.ToArray();
        _pendingPlain[pageId] = page;
        _cache.Add(inc.FileId, pageId, page);
        Volatile.Write(ref _pageCount, pageId + 1);
        return pageId;
    }

    public async ValueTask Flush()
    {
        if (_pendingCount == 0)
            return;

        var count = _pendingCount;
        var firstPageId = _pendingFirstPageId;
        await _file.Write(PagePosition(firstPageId), _pending.AsMemory(0, count * _onDiskPageSize))
            .ConfigureAwait(false);
        // Only now are these pages readable from the file — published after the write *returns*.
        Volatile.Write(ref _flushedPageCount, firstPageId + count);
        _pendingCount = 0;
        // Drop the staged plaintext only after the bytes are in the file, so a concurrent reader always
        // finds the page in one place or the other, never neither.
        for (var i = 0; i < count; i++)
            _pendingPlain.TryRemove(firstPageId + i, out _);
    }

    public async ValueTask FlushToDisk()
    {
        await Flush().ConfigureAwait(false);
        await _file.FlushToDisk().ConfigureAwait(false);
    }

    public long MarkCommitted()
    {
        // The extent is a high-water mark, so page ids burned by a torn tail fall inside it — the store
        // accounts that gap as dead, and that is what stops the id (and its nonce) from being re-issued.
        // Staged pages may not, hence the guard: a committed extent must never name bytes the storage
        // file has not seen. A burned *partial* page is the one exception, and Open tolerates it.
        if (_pendingCount != 0)
            throw new InvalidOperationException("Flush the staged pages before committing.");

        var commitLength = Length;
        Volatile.Write(ref _commitLength, commitLength);
        return commitLength;
    }

    public async ValueTask Recycle(uint fileId)
    {
        // Resets the file for a new life instead of unlinking it (§3.2/§4). The fresh fileSalt is what
        // makes restarting page ids at 0 safe: it puts this life's nonces in a different space entirely.
        // Readers that acquired this incarnation before its index locators moved keep the old file intact
        // until they finish. Later readers re-resolve against the index instead of touching a recycled slot.
        var oldIncarnation = _incarnation;
        await oldIncarnation.DrainReads().ConfigureAwait(false);
        _pendingCount = 0;
        _pendingPlain.Clear();
        _cache.DropSegment(oldIncarnation.FileId);
        if (fileId != oldIncarnation.FileId)
            _cache.DropSegment(fileId);

        var flags = _cipherFactory.Overhead == 0 ? 0u : KvasarConstants.EncryptedDataFileFlag;
        var header = new SegmentHeader(_formatVer, PageSize, fileId, flags);
        await WriteHeader(_file, header).ConfigureAwait(false);
        ResumePageId = 0;
        Volatile.Write(ref _flushedPageCount, 0);
        Volatile.Write(ref _commitLength, KvasarConstants.SegmentHeaderSize);
        Volatile.Write(ref _pageCount, 0);
        _incarnation = new Incarnation(fileId, _cipherFactory.Create(header.FileSalt));
    }

    // Private methods

    private static async ValueTask<PagedFile> OpenCore(
        IStorageFile file, IPageCipherFactory cipherFactory, uint formatVer, PageCache cache,
        long commitLength, uint? cacheId, int expectedPageSize, CancellationToken cancellationToken)
    {
        var headerBytes = new byte[KvasarConstants.SegmentHeaderSize];
        await file.ReadExact(0, headerBytes, cancellationToken).ConfigureAwait(false);
        var header = SegmentHeader.Read(headerBytes);
        if (header.FormatVer != formatVer)
            throw new KvasarCorruptException("Data file format version mismatch.");
        if (header.PageSize < KvasarConstants.MinPageSize || header.PageSize > KvasarConstants.MaxPageSize)
            throw new KvasarCorruptException("Data file page size is out of range.");
        if (expectedPageSize > 0 && header.PageSize != expectedPageSize)
            throw new KvasarCorruptException("Data file page size does not match the store's.");
        if ((header.Flags & ~KvasarConstants.EncryptedDataFileFlag) != 0)
            throw new KvasarCorruptException("Data file flags are invalid.");
        var isEncrypted = (header.Flags & KvasarConstants.EncryptedDataFileFlag) != 0;
        if (isEncrypted != (cipherFactory.Overhead != 0))
            throw new KvasarConfigurationException(
                "DisableEncryption does not match the existing store.");

        var onDiskPageSize = header.PageSize + cipherFactory.Overhead;
        var bodyLength = Math.Max(0, file.Length - KvasarConstants.SegmentHeaderSize);
        // Rounded up, not down: a torn trailing page burns its page id rather than getting overwritten.
        // The nonce is a pure function of (fileSalt, pageId), so re-issuing that id would reuse it.
        var pageCount = (bodyLength + onDiskPageSize - 1) / onDiskPageSize;
        var wholePagesLength = KvasarConstants.SegmentHeaderSize + bodyLength / onDiskPageSize * onDiskPageSize;
        // The extent may name one page more than the file physically holds: MarkCommitted publishes the
        // rounded-up PageCount so a torn tail's page id stays burned, and until an append covers that page
        // the file is short by its remainder. Bounding by wholePagesLength instead would reject a
        // generation the store itself wrote and, once a second commit put the same extent in the other
        // slot, leave nothing adoptable. The burned page still reads as a miss — it cannot authenticate.
        var burnedPagesLength = KvasarConstants.SegmentHeaderSize + (pageCount * onDiskPageSize);
        if (commitLength < 0)
            commitLength = wholePagesLength;
        else if (commitLength < KvasarConstants.SegmentHeaderSize || commitLength > burnedPagesLength)
            throw new KvasarCorruptException("Committed extent is outside the data file.");
        else if ((commitLength - KvasarConstants.SegmentHeaderSize) % onDiskPageSize != 0)
            throw new KvasarCorruptException("Committed extent is not page-aligned.");

        return new PagedFile(file, cipherFactory, cache, header, pageCount, commitLength, cacheId);
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadAndCache(
        Incarnation incarnation, long pageId, CancellationToken cancellationToken)
    {
        // The decrypt and the cache insert must name the same incarnation, so the caller captures it once
        // and threads it through rather than re-reading it after the await.
        var page = await ReadAndDecrypt(incarnation, pageId, cancellationToken).ConfigureAwait(false);
        // Redundant concurrent decrypts of the same page are harmless (identical bytes); Add keeps the first.
        _cache.Add(incarnation.FileId, pageId, page);
        return page;
    }

    private async ValueTask<byte[]> ReadAndDecrypt(
        Incarnation inc, long pageId, CancellationToken cancellationToken)
    {
        var filePos = PagePosition(pageId);
        var onDisk = ArrayPool<byte>.Shared.Rent(_onDiskPageSize);
        try {
            await _file.ReadExact(filePos, onDisk.AsMemory(0, _onDiskPageSize), cancellationToken)
                .ConfigureAwait(false);
            var page = new byte[PageSize];
            inc.Cipher.Decrypt(pageId, onDisk.AsSpan(0, _onDiskPageSize), page);
            return page;
        }
        finally {
            ArrayPool<byte>.Shared.Return(onDisk);
        }
    }

    private long PagePosition(long pageId)
        => KvasarConstants.SegmentHeaderSize + pageId * (long)_onDiskPageSize;

    private static async ValueTask WriteHeader(IStorageFile file, SegmentHeader header)
    {
        var headerBytes = new byte[KvasarConstants.SegmentHeaderSize];
        header.Write(headerBytes);
        await file.Truncate(0).ConfigureAwait(false);
        await file.Write(0, headerBytes).ConfigureAwait(false);
    }

    // Nested types

    internal readonly struct ReadLease : IDisposable
    {
        private readonly Incarnation? _incarnation;

        internal ReadLease(Incarnation incarnation)
            => _incarnation = incarnation;

        public void Dispose()
            => _incarnation?.ReleaseRead();
    }

    internal sealed record Incarnation(uint FileId, IPageCipher Cipher)
    {
        private readonly TaskCompletionSource _whenDrained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;
        private int _isDraining;

        public bool TryAcquireRead()
        {
            if (Volatile.Read(ref _isDraining) != 0)
                return false;

            Interlocked.Increment(ref _readCount);
            if (Volatile.Read(ref _isDraining) == 0)
                return true;

            ReleaseRead();
            return false;
        }

        public Task DrainReads()
        {
            Interlocked.Increment(ref _readCount);
            Volatile.Write(ref _isDraining, 1);
            ReleaseRead();
            return _whenDrained.Task;
        }

        public void ReleaseRead()
        {
            if (Interlocked.Decrement(ref _readCount) == 0 && Volatile.Read(ref _isDraining) != 0)
                _whenDrained.TrySetResult();
        }
    }
}
