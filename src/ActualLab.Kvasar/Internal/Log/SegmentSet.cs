using System.Collections.Concurrent;
using System.Linq;
using ActualLab.Kvasar;
using ActualLab.Kvasar.Crypto;

namespace ActualLab.Kvasar.Internal;

/// <summary>The outcome of a record read: whether it was found, the view, and its on-stream length.</summary>
public readonly record struct RecordRead(bool IsFound, RecordView View, int TotalLength);

/// <summary>
/// The set of <c>&lt;base&gt;.NNN.klog</c> segments plus the active tail — the writer's append log (§5.2, §9).
/// Single-writer for append/seal; concurrent readers for the <c>Read*</c> methods. Offsets are logical
/// offsets in the decrypted page stream: <c>pageId = offset / PageSize</c>, in-page = <c>offset % PageSize</c>.
/// </summary>
public sealed class SegmentSet : IDisposable
{
    // The unsealed tail, published as one immutable triple. Deferred-flush mode publishes locators into the
    // tail, so a reader must see a consistent (buffer, fill, pageId) set: reading those fields separately
    // lets a concurrent seal swap the buffer between them, and the reader then slices the fresh empty one
    // and reports a miss for a key that exists. Bytes below Fill are never rewritten, and SealTail installs
    // a new buffer rather than reusing this one, so a snapshot stays valid for as long as anyone holds it.
    private sealed record TailSnapshot(byte[] Buffer, int Fill, long PageId);

    private sealed class SegState
    {
        public required PagedSegment Segment;
        public long LiveBytes;
        public long DeadBytes;
        public volatile TailSnapshot? Tail; // non-null only while this is the active segment
    }

    private readonly string _basePath;
    private readonly int _pageSize;
    private readonly IPageCipherFactory _cipherFactory;
    private readonly uint _formatVer;
    private readonly PageCache _cache;
    private readonly long _segmentBytes;
    private readonly int _maxInlineValueBytes;
    // Concurrent so lock-free readers (TryReadRecord) can probe while the single writer adds a rolled
    // segment or removes a compacted one. Order isn't guaranteed; ScanAll sorts where recovery needs it.
    private readonly ConcurrentDictionary<uint, SegState> _states = new();

    private PagedSegment _active = null!;
    private SegState _activeState = null!;
    // volatile: readers may slice the unsealed tail (deferred-flush mode publishes into it), and SealTail
    // swaps in a fresh array rather than reusing this one, so a reader must not cache a stale reference.
    private volatile byte[] _tail;
    private int _tailFill;
    private bool _disposed;

    public uint ActiveSegmentId => _active.SegmentId;
    public long ActiveSegmentPageCount => _active.PageCount;
    public long ActiveLogicalHwm => (long)_active.PageCount * _pageSize + _tailFill;
    // ~1 MiB of readahead per I/O: enough to amortize the per-operation cost of async I/O over a
    // sequential walk without pinning much of the page-cache budget.
    public int PrefetchPages => Math.Max(1, (1 << 20) / _pageSize);

    public long LiveBytes => Sum(static s => s.LiveBytes);
    public long DeadBytes => Sum(static s => s.DeadBytes);
    public long FileBytes { get { long sum = 0; foreach (var s in _states.Values) sum += s.Segment.FileByteLength; return sum; } }

    private SegmentSet(
        string basePath, int pageSize, IPageCipherFactory cipherFactory, uint formatVer,
        PageCache cache, long segmentBytes, int maxInlineValueBytes)
    {
        _basePath = basePath;
        _pageSize = pageSize;
        _cipherFactory = cipherFactory;
        _formatVer = formatVer;
        _cache = cache;
        _segmentBytes = segmentBytes;
        _maxInlineValueBytes = maxInlineValueBytes <= 0 ? pageSize : maxInlineValueBytes;
        _tail = new byte[pageSize];
    }

    public static async ValueTask<SegmentSet> Create(
        string basePath, int pageSize, IPageCipherFactory cipherFactory, uint formatVer,
        PageCache cache, long segmentBytes, int maxInlineValueBytes = 0,
        CancellationToken cancellationToken = default)
    {
        var result = new SegmentSet(
            basePath, pageSize, cipherFactory, formatVer, cache, segmentBytes, maxInlineValueBytes);
        await result.Discover(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // --- Append -------------------------------------------------------------

    public async ValueTask<(Locator Locator, int RecordLength)> Append(
        RecordFlags flags, KvasarValueType valType,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var valueLen = isTombstone ? 0 : value.Length;
        var recordLength = RecordCodec.GetRecordLength(key.Length, valueLen, isTombstone);
        await RollIfNeeded(cancellationToken).ConfigureAwait(false);
        var singlePage = recordLength <= _pageSize && (isTombstone || value.Length <= _maxInlineValueBytes);
        var locator = singlePage
            ? await AppendSinglePage(flags, valType, key, value, isTombstone, recordLength, cancellationToken)
                .ConfigureAwait(false)
            : await AppendMultiPage(flags, valType, key, value, isTombstone, recordLength, cancellationToken)
                .ConfigureAwait(false);
        return (locator, recordLength);
    }

    // --- Read ---------------------------------------------------------------

    public async ValueTask<RecordView> ReadRecord(Locator loc, CancellationToken cancellationToken = default)
    {
        var read = await TryReadRecord(loc, cancellationToken).ConfigureAwait(false);
        if (!read.IsFound)
            throw new KvasarCorruptException($"Cannot read record at {loc}.");
        return read.View;
    }

    public ValueTask<RecordRead> TryReadRecord(Locator loc, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(loc.SegmentId, out var st))
            return default;
        return TryReadAt(st, loc.Offset, cancellationToken);
    }

    // Abandons the current active segment and starts a fresh one. Deferred-flush mode calls this on open:
    // a crash there can lose whole unwritten pages, so PageCount comes back lower and appends would reuse
    // those page ids — and the GCM nonce is a pure function of (fileSalt, pageId). A new segment gets a new
    // random salt, so its nonce space is disjoint and reuse is structurally impossible.
    public async ValueTask RollToNewSegment(CancellationToken cancellationToken = default)
    {
        await SealTail(cancellationToken).ConfigureAwait(false);
        await StartNewSegment(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask Prefetch(uint segmentId, long fromPageId, int maxPages, CancellationToken cancellationToken = default)
        => _states.TryGetValue(segmentId, out var st)
            ? st.Segment.Prefetch(fromPageId, maxPages, cancellationToken)
            : default;

    public bool TryReadRecordCached(Locator loc, out RecordView view)
    {
        // Zero-I/O fast path: a single-page record whose page is already decrypted in the cache — the
        // common case. Anything else (miss, or a record spanning pages) returns false so the caller awaits
        // the general path; it never returns a wrong answer, only "not right now".
        view = default;
        if (!_states.TryGetValue(loc.SegmentId, out var st))
            return false;

        var tail = st.Tail; // one read: everything below must agree on the same tail generation
        long offset = loc.Offset;
        var len = LogicalLength(st, tail);
        if (offset >= len)
            return false;

        var pageId = offset / _pageSize;
        var inPage = (int)(offset % _pageSize);
        if (!TryGetLogicalPageCached(st, tail, pageId, out var firstPage))
            return false;

        var span = firstPage.Span;
        if (inPage >= span.Length)
            return false;
        if (!Varint.TryRead(span[inPage..], out var bodyLenU, out var recLenBytes) || bodyLenU == 0)
            return false;
        // Bound before narrowing: a varint >= 2^63 casts negative and defeats the checks below.
        if (bodyLenU > (ulong)(len - offset))
            return false;
        var total = recLenBytes + (long)bodyLenU;
        if (offset + total > len || total > int.MaxValue)
            return false;
        var totalLen = (int)total;
        if (inPage + totalLen > span.Length)
            return false;

        return RecordCodec.TryDecode(firstPage.Slice(inPage, totalLen), out view, out _);
    }

    private async ValueTask<RecordRead> TryReadAt(SegState st, long offset, CancellationToken cancellationToken)
    {
        var tail = st.Tail; // one read: everything below must agree on the same tail generation
        var len = LogicalLength(st, tail);
        if (offset < 0 || offset >= len)
            return default;

        var pageId = offset / _pageSize;
        var inPage = (int)(offset % _pageSize);
        var firstPage = await GetLogicalPage(st, tail, pageId, cancellationToken).ConfigureAwait(false);
        int totalLen;
        {
            var span = firstPage.Span;
            if (inPage >= span.Length)
                return default;
            if (!Varint.TryRead(span[inPage..], out var bodyLenU, out var recLenBytes) || bodyLenU == 0)
                return default;
            // Bound before narrowing: a varint >= 2^63 casts negative and defeats the checks below.
            if (bodyLenU > (ulong)(len - offset))
                return default;
            var total = recLenBytes + (long)bodyLenU;
            if (offset + total > len || total > int.MaxValue)
                return default;
            totalLen = (int)total;

            if (inPage + totalLen <= span.Length) {
                return RecordCodec.TryDecode(firstPage.Slice(inPage, totalLen), out var view, out _)
                    ? new RecordRead(true, view, totalLen)
                    : default;
            }
        }

        // Spans pages -> assemble into a contiguous buffer, then decode (copy).
        var buf = new byte[totalLen];
        var copied = 0;
        var pid = pageId;
        var start = inPage;
        while (copied < totalLen) {
            var page = await GetLogicalPage(st, tail, pid, cancellationToken).ConfigureAwait(false);
            var n = CopyPart(page, start, buf, copied, totalLen);
            if (n < 0)
                return default;
            copied += n;
            pid++;
            start = 0;
        }
        return RecordCodec.TryDecode(buf.AsMemory(0, totalLen), out var spanned, out _)
            ? new RecordRead(true, spanned, totalLen)
            : default;
    }

    // --- Recovery -----------------------------------------------------------

    public IAsyncEnumerable<(Locator Loc, RecordView View, int RecordLength)> ScanAll(
        CancellationToken cancellationToken = default)
        => ScanFrom(0, 0, cancellationToken);

    // Walks records in write order starting at (fromSegmentId, fromOffset) — the .kidx HWM on the fast
    // open path, so only the tiny gap written after the last checkpoint is decrypted, not the whole log.
    public async IAsyncEnumerable<(Locator Loc, RecordView View, int RecordLength)> ScanFrom(
        uint fromSegmentId, long fromOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var (segId, st) in _states.OrderBy(kv => kv.Key)) {
            if (segId < fromSegmentId)
                continue;
            var len = LogicalLength(st);
            var p = segId == fromSegmentId ? fromOffset : 0;
            var prefetchPages = PrefetchPages;
            var nextPrefetchPage = p / _pageSize;
            while (p < len) {
                var pageId = p / _pageSize;
                if (pageId >= nextPrefetchPage) {
                    await st.Segment.Prefetch(pageId, prefetchPages, cancellationToken).ConfigureAwait(false);
                    nextPrefetchPage = pageId + prefetchPages;
                }
                var read = await TryReadAt(st, p, cancellationToken).ConfigureAwait(false);
                if (read.IsFound) {
                    yield return (new Locator(segId, (uint)p), read.View, read.TotalLength);
                    p += read.TotalLength;
                }
                else {
                    var nextPage = (p / _pageSize + 1) * _pageSize;
                    if (p % _pageSize != 0 && nextPage <= len)
                        p = nextPage; // page-end padding: skip to the next page
                    else
                        yield break; // torn tail: stop
                }
            }
        }
    }

    // --- Accounting & lifecycle --------------------------------------------

    public void OnSuperseded(Locator oldLoc, int oldRecordLength)
    {
        if (!_states.TryGetValue(oldLoc.SegmentId, out var st))
            return;
        st.LiveBytes -= oldRecordLength;
        st.DeadBytes += oldRecordLength;
    }

    // Seeds per-segment accounting at open from the loaded index (no log decrypt): LiveBytes is the
    // sum of live record lengths in that segment; DeadBytes is the rest of the segment's logical bytes
    // (superseded records + page padding — a slight over-count that only makes compaction a touch more
    // eager). Called once after the index is loaded; runtime OnSuperseded continues from here.
    public void SeedAccountingFromIndex(IReadOnlyDictionary<uint, long> liveBytesPerSegment)
    {
        foreach (var (id, st) in _states) {
            var live = liveBytesPerSegment.GetValueOrDefault(id, 0);
            var gross = LogicalLength(st);
            st.LiveBytes = live;
            st.DeadBytes = Math.Max(0, gross - live);
        }
    }

    public IEnumerable<(uint SegmentId, long LiveBytes, long DeadBytes, long SegmentBytes)> SealedSegments()
    {
        var activeId = _active.SegmentId;
        foreach (var (id, st) in _states) {
            if (id == activeId)
                continue;
            yield return (id, st.LiveBytes, st.DeadBytes, st.LiveBytes + st.DeadBytes);
        }
    }

    public async ValueTask Flush(bool fsync, CancellationToken cancellationToken = default)
    {
        await SealTail(cancellationToken).ConfigureAwait(false);
        foreach (var st in _states.Values)
            await st.Segment.Flush(fsync, cancellationToken).ConfigureAwait(false);
    }

    public void RemoveSegment(uint segmentId)
    {
        if (segmentId == _active.SegmentId)
            throw new InvalidOperationException("Cannot remove the active segment.");
        if (!_states.TryRemove(segmentId, out var st))
            return;
        st.Segment.Dispose();
        var path = PathFor(segmentId);
        if (File.Exists(path))
            File.Delete(path);
        _cache.DropSegment(segmentId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var st in _states.Values)
            st.Segment.Dispose();
        _states.Clear();
    }

    // Private methods

    private async ValueTask<Locator> AppendSinglePage(
        RecordFlags flags, KvasarValueType valType,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone, int recordLength,
        CancellationToken cancellationToken)
    {
        if (_tailFill + recordLength > _pageSize)
            await SealTail(cancellationToken).ConfigureAwait(false);
        var offset = (long)_active.PageCount * _pageSize + _tailFill;
        RecordCodec.Encode(_tail.AsSpan(_tailFill), flags, valType, key.Span, value.Span, isTombstone);
        _tailFill += recordLength;
        PublishTail();
        _activeState.LiveBytes += recordLength;
        return new Locator(_active.SegmentId, checked((uint)offset));
    }

    private async ValueTask<Locator> AppendMultiPage(
        RecordFlags flags, KvasarValueType valType,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone, int recordLength,
        CancellationToken cancellationToken)
    {
        // Multi-page runs start at a page boundary and occupy whole pages (§5.2).
        if (_tailFill > 0)
            await SealTail(cancellationToken).ConfigureAwait(false);
        var offset = (long)_active.PageCount * _pageSize;
        var buf = ArrayPool<byte>.Shared.Rent(recordLength);
        try {
            RecordCodec.Encode(buf, flags, valType, key.Span, value.Span, isTombstone);
            var pos = 0;
            while (recordLength - pos >= _pageSize) {
                await _active.AppendPage(buf.AsMemory(pos, _pageSize), cancellationToken).ConfigureAwait(false);
                pos += _pageSize;
            }
            var rem = recordLength - pos;
            if (rem > 0) {
                buf.AsSpan(pos, rem).CopyTo(_tail);
                _tailFill = rem;
            }
            else {
                _tailFill = 0;
            }
            PublishTail();
        }
        finally {
            ArrayPool<byte>.Shared.Return(buf);
        }
        _activeState.LiveBytes += recordLength;
        return new Locator(_active.SegmentId, checked((uint)offset));
    }

    private async ValueTask SealTail(CancellationToken cancellationToken)
    {
        if (_tailFill == 0)
            return;
        _tail.AsSpan(_tailFill).Clear(); // pad the remainder with zeros
        await _active.AppendPage(_tail, cancellationToken).ConfigureAwait(false);
        _tail = new byte[_pageSize]; // fresh buffer so any handed-out zero-copy slice stays immutable
        _tailFill = 0;
        PublishTail();
    }

    private async ValueTask RollIfNeeded(CancellationToken cancellationToken)
    {
        if (ActiveLogicalHwm < _segmentBytes)
            return;
        await SealTail(cancellationToken).ConfigureAwait(false);
        await StartNewSegment(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask StartNewSegment(CancellationToken cancellationToken)
    {
        var newId = _active.SegmentId + 1;
        var seg = await PagedSegment
            .Create(PathFor(newId), newId, _pageSize, _cipherFactory, _formatVer, _cache, cancellationToken)
            .ConfigureAwait(false);
        var st = new SegState { Segment = seg };
        _states[newId] = st;
        var previous = _activeState;
        _active = seg;
        _activeState = st;
        _tailFill = 0;
        PublishTail();
        previous.Tail = null; // the old segment is sealed now; its pages come from the cache
    }

    private async ValueTask Discover(CancellationToken cancellationToken)
    {
        var ids = DiscoverSegmentIds();
        if (ids.Count == 0) {
            // Segment ids are 1-based: a locator packs (segmentId, offset) into a 64-bit word, and
            // packed == 0 is reserved as the index's empty-slot sentinel (segment 0 offset 0 would collide).
            const uint firstId = 1;
            var seg = await PagedSegment
                .Create(PathFor(firstId), firstId, _pageSize, _cipherFactory, _formatVer, _cache, cancellationToken)
                .ConfigureAwait(false);
            var st = new SegState { Segment = seg };
            _states[firstId] = st;
            _active = seg;
            _activeState = st;
            PublishTail();
            return;
        }
        ids.Sort();
        try {
            foreach (var id in ids) {
                var seg = await PagedSegment
                    .Open(PathFor(id), _cipherFactory, _formatVer, _cache, id, cancellationToken)
                    .ConfigureAwait(false);
                _states[id] = new SegState { Segment = seg };
            }
            _activeState = _states[ids[^1]];
            _active = _activeState.Segment;
            PublishTail();
            // Validate the key with a single-page decrypt instead of scanning the whole log (§6.5): a
            // wrong key / tampered page 0 throws here → the caller wipes & recreates. Accounting is
            // seeded later from the loaded index (SeedAccountingFromIndex), keeping open O(index).
            if (_active.PageCount > 0)
                _ = await _active.GetPage(0, cancellationToken).ConfigureAwait(false);
            // A crash can leave the last page half-written; Open floors PageCount and discards it. Appending
            // here would reuse that pageId, and the GCM nonce is a pure function of (fileSalt, pageId), so
            // the reused nonce would expose the XOR of both plaintexts. Start a fresh segment instead —
            // it gets a fresh random salt, so its nonce space is disjoint.
            if (_active.HasTornTail)
                await StartNewSegment(cancellationToken).ConfigureAwait(false);
        }
        catch {
            // A bad key / corrupt segment surfaces here (decrypt fails); dispose any handles we opened
            // so the caller can wipe & recreate the file set (open handles would block deletion).
            foreach (var st in _states.Values)
                st.Segment.Dispose();
            _states.Clear();
            throw;
        }
    }

    private List<uint> DiscoverSegmentIds()
    {
        var ids = new List<uint>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(_basePath));
        if (dir == null || !Directory.Exists(dir))
            return ids;
        var name = Path.GetFileName(_basePath);
        var prefix = name + ".";
        const string suffix = ".klog";
        foreach (var path in Directory.EnumerateFiles(dir, prefix + "*" + suffix)) {
            var fn = Path.GetFileName(path);
            if (fn.Length <= prefix.Length + suffix.Length)
                continue;
            var mid = fn.Substring(prefix.Length, fn.Length - prefix.Length - suffix.Length);
            if (uint.TryParse(mid, out var id))
                ids.Add(id);
        }
        return ids;
    }

    // All three helpers below take the caller's single snapshot read, so one operation can't observe the
    // tail from two different generations.
    private long LogicalLength(SegState st) => LogicalLength(st, st.Tail);

    private long LogicalLength(SegState st, TailSnapshot? tail)
        => tail is { } t ? t.PageId * _pageSize + t.Fill : (long)st.Segment.PageCount * _pageSize;

    private static ValueTask<ReadOnlyMemory<byte>> GetLogicalPage(
        SegState st, TailSnapshot? tail, long pageId, CancellationToken cancellationToken)
        => tail is { } t && t.PageId == pageId
            ? new ValueTask<ReadOnlyMemory<byte>>(t.Buffer.AsMemory(0, t.Fill))
            : st.Segment.GetPage(pageId, cancellationToken);

    private static bool TryGetLogicalPageCached(
        SegState st, TailSnapshot? tail, long pageId, out ReadOnlyMemory<byte> page)
    {
        if (tail is { } t && t.PageId == pageId) {
            page = t.Buffer.AsMemory(0, t.Fill);
            return true;
        }
        return st.Segment.TryGetCachedPage(pageId, out page);
    }

    // Writer-only: republishes the tail so readers pick up the newly appended bytes.
    private void PublishTail()
        => _activeState.Tail = new TailSnapshot(_tail, _tailFill, _active.PageCount);

    private static int CopyPart(ReadOnlyMemory<byte> page, int start, byte[] destination, int copied, int totalLen)
    {
        var span = page.Span;
        if (start >= span.Length)
            return -1;
        var n = Math.Min(span.Length - start, totalLen - copied);
        span.Slice(start, n).CopyTo(destination.AsSpan(copied));
        return n;
    }

    private string PathFor(uint id) => $"{_basePath}.{id:D3}.klog";

    private long Sum(Func<SegState, long> selector)
    {
        long sum = 0;
        foreach (var s in _states.Values)
            sum += selector(s);
        return sum;
    }
}
