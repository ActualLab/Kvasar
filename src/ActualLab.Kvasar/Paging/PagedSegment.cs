using System.Collections.Concurrent;
using ActualLab.Kvasar.Crypto;
using Microsoft.Win32.SafeHandles;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// One <c>.klog</c> segment file (§5.1): a 64-byte plaintext <see cref="SegmentHeader"/> followed by a
/// sequence of encrypted pages, each <c>PageSize + cipher.Overhead</c> bytes on disk. Positional async I/O
/// via <see cref="RandomAccess"/> makes concurrent reads safe; a single writer appends sealed pages at the
/// tail. Decrypted pages are cached as immutable buffers in the shared <see cref="PageCache"/>, so a cached
/// <see cref="GetPage"/> completes synchronously (no state machine, no allocation).
/// </summary>
public sealed class PagedSegment : IDisposable
{
    // Appended pages are staged here and written in one I/O, because a per-page WriteAsync costs far more
    // than the memcpy: batching turns a 64-record SetMany at 4 KiB values into 1 write instead of 64.
    private const int MaxPendingBytes = 1 << 20;

    private readonly SafeFileHandle _handle;
    private readonly IPageCipher _cipher;
    private readonly PageCache _cache;
    private readonly int _onDiskPageSize;
    private readonly byte[] _pending;
    private readonly int _pendingCapacity;
    // Plaintext of staged pages. They aren't on disk yet, so a reader that misses the (evictable) page
    // cache must be served from here — compaction scans the log while appending to it.
    private readonly ConcurrentDictionary<long, byte[]> _pendingPlain = new();
    private long _pendingFirstPageId;
    private int _pendingCount;
    private long _pageCount;

    public uint SegmentId { get; }
    public int PageSize { get; }
    public long PageCount => Volatile.Read(ref _pageCount);
    // Counts staged-but-unwritten pages too, so the reported size doesn't dip between append and flush.
    public long FileByteLength
        => RandomAccess.GetLength(_handle) + (long)Volatile.Read(ref _pendingCount) * _onDiskPageSize;

    // True when the file ends mid-page. Such a segment must never be appended to again: Open floors
    // PageCount, so the next append would reuse that pageId — and the GCM nonce is a pure function of
    // (fileSalt, pageId), so reusing it under the same key is catastrophic keystream reuse.
    public bool HasTornTail { get; private init; }

    private PagedSegment(
        SafeFileHandle handle, IPageCipher cipher, PageCache cache,
        uint segmentId, int pageSize, long pageCount)
    {
        _handle = handle;
        _cipher = cipher;
        _cache = cache;
        SegmentId = segmentId;
        PageSize = pageSize;
        _onDiskPageSize = pageSize + cipher.Overhead;
        _pageCount = pageCount;
        _pendingCapacity = Math.Max(1, MaxPendingBytes / _onDiskPageSize);
        _pending = new byte[_pendingCapacity * _onDiskPageSize];
    }

    public static async ValueTask<PagedSegment> Create(
        string path, uint segmentId, int pageSize,
        IPageCipherFactory cipherFactory, uint formatVer, PageCache cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cipherFactory);
        ArgumentNullException.ThrowIfNull(cache);
        if (pageSize < KvasarConstants.MinPageSize || pageSize > KvasarConstants.MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        var header = new SegmentHeader(formatVer, pageSize, segmentId);
        var handle = File.OpenHandle(
            path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
        try {
            var headerBytes = new byte[KvasarConstants.SegmentHeaderSize];
            header.Write(headerBytes);
            await RandomAccess.WriteAsync(handle, headerBytes, 0, cancellationToken).ConfigureAwait(false);
            var cipher = cipherFactory.Create(header.FileSalt);
            return new PagedSegment(handle, cipher, cache, segmentId, pageSize, 0);
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    public static async ValueTask<PagedSegment> Open(
        string path, IPageCipherFactory cipherFactory, uint formatVer, PageCache cache,
        uint? expectedSegmentId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cipherFactory);
        ArgumentNullException.ThrowIfNull(cache);

        var handle = File.OpenHandle(
            path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
        try {
            var headerBytes = new byte[KvasarConstants.SegmentHeaderSize];
            await ReadExact(handle, 0, headerBytes, cancellationToken).ConfigureAwait(false);
            var header = SegmentHeader.Read(headerBytes);
            if (header.FormatVer != formatVer)
                throw new KvasarCorruptException("Segment format version mismatch.");
            if (header.PageSize < KvasarConstants.MinPageSize || header.PageSize > KvasarConstants.MaxPageSize)
                throw new KvasarCorruptException("Segment page size is out of range.");
            // The header isn't authenticated, so a flipped segmentId would silently mint locators for a
            // segment the set doesn't know — every later write would read back as a miss, forever.
            if (expectedSegmentId is { } expected && header.SegmentId != expected)
                throw new KvasarCorruptException("Segment id does not match its file name.");

            var onDiskPageSize = header.PageSize + cipherFactory.Overhead;
            var length = RandomAccess.GetLength(handle);
            var bodyLength = length - KvasarConstants.SegmentHeaderSize;
            // A torn trailing page (partial write on crash) is ignored: only fully-written pages count.
            var pageCount = bodyLength <= 0 ? 0 : bodyLength / onDiskPageSize;
            var cipher = cipherFactory.Create(header.FileSalt);
            return new PagedSegment(handle, cipher, cache, header.SegmentId, header.PageSize, pageCount) {
                HasTornTail = bodyLength > 0 && bodyLength % onDiskPageSize != 0,
            };
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    public bool TryGetCachedPage(long pageId, out ReadOnlyMemory<byte> page)
    {
        if ((ulong)pageId < (ulong)PageCount) {
            if (_cache.TryGet(SegmentId, pageId, out var cached)) {
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

    public ValueTask<ReadOnlyMemory<byte>> GetPage(long pageId, CancellationToken cancellationToken = default)
    {
        // Deliberately not an async method: a cache hit returns an already-completed ValueTask, so the
        // hot read path costs no state machine and no allocation. Only a miss builds one.
        if ((ulong)pageId >= (ulong)PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageId));
        if (_cache.TryGet(SegmentId, pageId, out var cached))
            return new ValueTask<ReadOnlyMemory<byte>>(cached);
        if (_pendingPlain.TryGetValue(pageId, out var staged))
            return new ValueTask<ReadOnlyMemory<byte>>(staged);
        return ReadAndCache(pageId, cancellationToken);
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
        // path stays the single place that decides what a bad page means.
        if (firstPageId < 0 || maxPages <= 0)
            return;
        try {
            // Bound by what is actually on disk (staged pages aren't), so we never decrypt unwritten bytes.
            var onDisk = (RandomAccess.GetLength(_handle) - KvasarConstants.SegmentHeaderSize) / _onDiskPageSize;
            if (firstPageId >= onDisk)
                return;
            var count = (int)Math.Min(maxPages, onDisk - firstPageId);
            if (count <= 0)
                return;

            var byteLength = count * _onDiskPageSize;
            var buffer = ArrayPool<byte>.Shared.Rent(byteLength);
            try {
                await ReadExact(_handle, PagePosition(firstPageId), buffer.AsMemory(0, byteLength), cancellationToken)
                    .ConfigureAwait(false);
                for (var i = 0; i < count; i++) {
                    var pageId = firstPageId + i;
                    if (_cache.TryGet(SegmentId, pageId, out _))
                        continue;
                    var page = new byte[PageSize];
                    _cipher.Decrypt(pageId, buffer.AsSpan(i * _onDiskPageSize, _onDiskPageSize), page);
                    _cache.Add(SegmentId, pageId, page);
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

    public async ValueTask<long> AppendPage(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length != PageSize)
            throw new ArgumentException("Payload length must equal PageSize.", nameof(payload));

        if (_pendingCount == _pendingCapacity)
            await FlushPending(cancellationToken).ConfigureAwait(false);

        var pageId = _pageCount;
        if (_pendingCount == 0)
            _pendingFirstPageId = pageId;
        _cipher.Encrypt(pageId, payload.Span, _pending.AsSpan(_pendingCount * _onDiskPageSize, _onDiskPageSize));
        _pendingCount++;

        // Cache the immutable plaintext copy, then publish the new page (record-before-index ordering).
        var page = payload.ToArray();
        _pendingPlain[pageId] = page;
        _cache.Add(SegmentId, pageId, page);
        Volatile.Write(ref _pageCount, pageId + 1);
        return pageId;
    }

    public async ValueTask Flush(bool fsync, CancellationToken cancellationToken = default)
    {
        await FlushPending(cancellationToken).ConfigureAwait(false);
        // .NET has no async fsync, so the blocking syscall is offloaded off the caller's thread.
        if (fsync)
            await Task.Run(() => RandomAccess.FlushToDisk(_handle), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Async Flush is the intended path, but Dispose can't await, so anything still staged is written
        // synchronously here rather than silently lost.
        try {
            if (_pendingCount > 0) {
                RandomAccess.Write(
                    _handle, _pending.AsSpan(0, _pendingCount * _onDiskPageSize), PagePosition(_pendingFirstPageId));
                _pendingCount = 0;
                _pendingPlain.Clear();
            }
        }
        catch {
            // Ignored: the segment is going away, and a partial tail is what torn-tail recovery is for.
        }
        _handle.Dispose();
    }

    // Private methods

    private async ValueTask FlushPending(CancellationToken cancellationToken)
    {
        if (_pendingCount == 0)
            return;
        var count = _pendingCount;
        var firstPageId = _pendingFirstPageId;
        await RandomAccess
            .WriteAsync(_handle, _pending.AsMemory(0, count * _onDiskPageSize), PagePosition(firstPageId), cancellationToken)
            .ConfigureAwait(false);
        _pendingCount = 0;
        // Drop the staged plaintext only after the bytes are on disk, so a concurrent reader always finds
        // the page in one place or the other, never neither.
        for (var i = 0; i < count; i++)
            _pendingPlain.TryRemove(firstPageId + i, out _);
    }

    private async ValueTask<ReadOnlyMemory<byte>> ReadAndCache(long pageId, CancellationToken cancellationToken)
    {
        var page = await ReadAndDecrypt(pageId, cancellationToken).ConfigureAwait(false);
        // Redundant concurrent decrypts of the same page are harmless (identical bytes); Add keeps the first.
        _cache.Add(SegmentId, pageId, page);
        return page;
    }

    private async ValueTask<byte[]> ReadAndDecrypt(long pageId, CancellationToken cancellationToken)
    {
        var filePos = PagePosition(pageId);
        var onDisk = ArrayPool<byte>.Shared.Rent(_onDiskPageSize);
        try {
            await ReadExact(_handle, filePos, onDisk.AsMemory(0, _onDiskPageSize), cancellationToken)
                .ConfigureAwait(false);
            var page = new byte[PageSize];
            _cipher.Decrypt(pageId, onDisk.AsSpan(0, _onDiskPageSize), page);
            return page;
        }
        finally {
            ArrayPool<byte>.Shared.Return(onDisk);
        }
    }

    private long PagePosition(long pageId)
        => KvasarConstants.SegmentHeaderSize + pageId * (long)_onDiskPageSize;

    private static async ValueTask ReadExact(
        SafeFileHandle handle, long fileOffset, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length) {
            var read = await RandomAccess
                .ReadAsync(handle, buffer[total..], fileOffset + total, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new KvasarCorruptException("Unexpected end of segment file.");
            total += read;
        }
    }
}
