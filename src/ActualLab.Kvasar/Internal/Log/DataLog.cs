using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// The two-slot <c>.kdat</c> record log (DESIGN-Durability.md §3.2, §4): two fixed data files, exactly one
/// active, the other free or a compaction target. Nothing is ever created or deleted — slots are recycled
/// in place. Single-writer for append/seal, concurrent readers for the <c>*Read*</c> methods. Offsets are
/// logical: <c>pageId = offset / PagePayloadSize</c>, in-page = <c>offset % PagePayloadSize</c>.
/// </summary>
public sealed class DataLog : IAsyncDisposable
{
    public const int SlotCount = 2;

    // The unsealed tail, published as one immutable triple. Readers may resolve locators that point into the
    // tail, so they must see a consistent (buffer, fill, pageId) set: reading those fields separately lets a
    // concurrent seal swap the buffer between them, and the reader then slices the fresh empty one and
    // reports a miss for a key that exists. Bytes below Fill are never rewritten, and SealTail installs a new
    // buffer rather than reusing this one, so a snapshot stays valid for as long as anyone holds it.
    private sealed record TailSnapshot(
        byte[] Buffer,
        int Fill,
        long PageId,
        bool IsContinuation,
        int FirstRecordOffset);

    private sealed class SlotState
    {
        public required int Slot;
        public required PagedFile File;
        public long LiveBytes;
        public long DeadBytes;
        // Writer-only; Tail is their published view.
        public byte[] TailBuffer = [];
        public int TailFill;
        public bool TailIsContinuation;
        public int TailFirstRecordOffset = -1;
        public volatile TailSnapshot? Tail; // non-null only while this slot is an append target
    }

    private readonly int _pageSize;
    private readonly int _pagePayloadSize;
    private readonly int _maxInlineValueBytes;
    private readonly Func<uint> _mintCacheId;
    private readonly SlotState[] _slots;
    private SlotState _active;
    private SlotState? _target;
    private bool _isDisposed;

    public int ActiveSlot => _active.Slot;
    public uint ActiveFileId => FileIdOf(_active.Slot);
    public int CompactionTargetSlot => _target?.Slot ?? -1;
    public int PageSize => _pageSize;
    public int PagePayloadSize => _pagePayloadSize;
    // ~1 MiB of readahead per I/O: enough to amortize the per-operation cost of async I/O over a
    // sequential walk without pinning much of the page-cache budget.
    public int PrefetchPages => Math.Max(1, (1 << 20) / _pageSize);

    // Logical end of the active file, unsealed tail included — where the next append lands.
    public long ActiveHwm => LogicalLength(_active);
    // Physical extent, the currency of the superblock and of PagedFile.Open — not a logical offset.
    public long ActiveCommitLength => _active.File.CommitLength;
    public long ActiveCommittedOffset => _active.File.CommittedPageCount * _pagePayloadSize;
    // Where appending resumes: at or above the physical end, so a torn tail's page id is never re-issued.
    public long ActiveResumeOffset => _active.File.ResumePageId * _pagePayloadSize;
    // The [committedEnd, resumeOffset) gap burned by a torn tail, measured once at open (§5.2.1).
    // Recovery adds it to the restored dead-byte counter.
    public long BurnedBytes { get; private set; }

    public long ActiveLiveBytes => _active.LiveBytes;
    public long ActiveDeadBytes => _active.DeadBytes;
    public long LiveBytes => _slots[0].LiveBytes + _slots[1].LiveBytes;
    public long DeadBytes => _slots[0].DeadBytes + _slots[1].DeadBytes;
    public long FileBytes => _slots[0].File.Length + _slots[1].File.Length;

    public static async ValueTask<DataLog> Create(
        IStorageFile[] slotFiles, int pageSize, IPageCipherFactory cipherFactory,
        uint formatVer, PageCache cache, int maxInlineValueBytes, Func<uint> mintCacheId)
    {
        RequireArguments(slotFiles, mintCacheId);
        var slots = new SlotState[SlotCount];
        try {
            for (var i = 0; i < SlotCount; i++) {
                var file = await PagedFile
                    .Create(slotFiles[i], mintCacheId(), pageSize, cipherFactory, formatVer, cache)
                    .ConfigureAwait(false);
                slots[i] = new SlotState { Slot = i, File = file };
            }
            if (slots[0].File.FileId == slots[1].File.FileId)
                throw new InvalidOperationException("mintCacheId returned the same id twice.");
        }
        catch {
            await DisposeOpened(slots).ConfigureAwait(false);
            throw;
        }
        return new DataLog(pageSize, maxInlineValueBytes, mintCacheId, slots, 0);
    }

    public static async ValueTask<DataLog> Open(
        IStorageFile[] slotFiles, int activeSlot, long activeCommitLength,
        int pageSize, IPageCipherFactory cipherFactory, uint formatVer, PageCache cache,
        int maxInlineValueBytes, Func<uint> mintCacheId, CancellationToken cancellationToken = default)
    {
        RequireArguments(slotFiles, mintCacheId);
        if ((uint)activeSlot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(activeSlot));

        var slots = new SlotState[SlotCount];
        try {
            // Mint the PageCache id rather than inheriting the header's: the header is unauthenticated
            // plaintext, and the cache holds *decrypted* pages keyed by (fileId, pageId), so two files
            // claiming one id would serve each other's plaintext past the point AES-GCM could catch it.
            var activeFile = await PagedFile
                .Open(slotFiles[activeSlot], cipherFactory, formatVer, cache,
                    activeCommitLength, mintCacheId(), cancellationToken)
                .ConfigureAwait(false);
            slots[activeSlot] = new SlotState { Slot = activeSlot, File = activeFile };
            if (activeFile.PageSize != pageSize)
                throw new KvasarCorruptException("Data file page size does not match the store's.");

            var freeSlot = 1 - activeSlot;
            var freeFile = await PagedFile
                .OpenOrCreateFree(
                    slotFiles[freeSlot], mintCacheId(), pageSize, cipherFactory, formatVer, cache,
                    cancellationToken)
                .ConfigureAwait(false);
            slots[freeSlot] = new SlotState { Slot = freeSlot, File = freeFile };
        }
        catch {
            await DisposeOpened(slots).ConfigureAwait(false);
            throw;
        }

        var result = new DataLog(pageSize, maxInlineValueBytes, mintCacheId, slots, activeSlot);
        var active = slots[activeSlot].File;
        result.BurnedBytes =
            Math.Max(0, active.ResumePageId - active.CommittedPageCount) * result._pagePayloadSize;
        return result;
    }

    private DataLog(
        int pageSize, int maxInlineValueBytes, Func<uint> mintCacheId,
        SlotState[] slots, int activeSlot)
    {
        _pageSize = pageSize;
        _pagePayloadSize = pageSize - KvasarConstants.DataPageHeaderSize;
        if (_pagePayloadSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        _maxInlineValueBytes = maxInlineValueBytes <= 0 ? _pagePayloadSize : maxInlineValueBytes;
        _mintCacheId = mintCacheId;
        _slots = slots;
        _active = slots[activeSlot];
        _active.TailBuffer = new byte[_pagePayloadSize];
        PublishTail(_active);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        // The tail is deliberately not sealed here: it is above every committed extent, so sealing it would
        // only add a page the next open has to account as dead.
        foreach (var st in _slots)
            await st.File.DisposeAsync().ConfigureAwait(false);
    }

    // No CancellationToken on any append/seal/flush path, by design: a record's bytes are self-describing,
    // so abandoning a multi-page append partway leaves a header claiming more bytes than were written, and
    // recovery then swallows the records appended after it. See PagedFile's write-path note.
    public ValueTask<(Locator Locator, int RecordLength)> Append(
        RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        return AppendTo(_active, flags, valueKind, key, value, isTombstone);
    }

    public ValueTask<(Locator Locator, int RecordLength)> AppendToTarget(
        RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        var target = _target ?? throw new InvalidOperationException("No compaction is in progress.");
        return AppendTo(target, flags, valueKind, key, value, isTombstone);
    }

    public async ValueTask<RecordView> ReadRecord(Locator loc, CancellationToken cancellationToken = default)
    {
        var read = await TryReadRecord(loc, cancellationToken).ConfigureAwait(false);
        if (!read.IsFound)
            throw new KvasarCorruptException($"Cannot read record at {loc}.");
        return read.View;
    }

    public ValueTask<RecordRead> TryReadRecord(Locator loc, CancellationToken cancellationToken = default)
    {
        var st = TryGetSlot(loc.FileId);
        if (st == null)
            return default;
        var slotCacheId = st.File.FileId;
        return TryReadWithLease(st, loc.Offset, slotCacheId, cancellationToken);
    }

    public bool TryReadRecordCached(Locator loc, out RecordView view)
    {
        // Zero-I/O fast path: a single-page record whose page is already decrypted in the cache — the
        // common case. Anything else (miss, or a record spanning pages) returns false so the caller awaits
        // the general path; it never returns a wrong answer, only "not right now".
        view = default;
        var st = TryGetSlot(loc.FileId);
        if (st == null)
            return false;

        var tail = st.Tail; // one read: everything below must agree on the same tail generation
        var offset = loc.Offset;
        var len = LogicalLength(st, tail);
        if (offset < 0 || offset >= len)
            return false;

        var pageId = offset / _pagePayloadSize;
        var inPage = (int)(offset % _pagePayloadSize);
        if (!TryGetLogicalPageCached(st, tail, pageId, out var firstPage))
            return false;

        var span = firstPage.Span;
        if (inPage >= span.Length
            || !RecordCodec.TryReadHeader(span[inPage..], len - offset, out var totalLen))
            return false;
        if (inPage + totalLen > span.Length)
            return false;

        return RecordCodec.TryDecode(firstPage.Slice(inPage, totalLen), out view, out _);
    }

    public async ValueTask Authenticate(
        int slot, long fromPageId, long toPageId, CancellationToken cancellationToken = default)
    {
        if ((uint)slot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (fromPageId < 0 || toPageId < fromPageId || toPageId > _slots[slot].File.CommittedPageCount)
            throw new ArgumentOutOfRangeException(nameof(toPageId));

        var file = _slots[slot].File;
        var nextPrefetchPageId = fromPageId;
        for (var pageId = fromPageId; pageId < toPageId; pageId++) {
            if (pageId >= nextPrefetchPageId) {
                await file.Prefetch(pageId, PrefetchPages, cancellationToken).ConfigureAwait(false);
                nextPrefetchPageId = pageId + PrefetchPages;
            }
            _ = await file.GetPage(pageId, cancellationToken).ConfigureAwait(false);
        }
    }

    // Walks records in write order over one slot. Every page authenticates its continuation state and exact
    // first record start, so recovery never probes caller bytes for a plausible boundary.
    public async IAsyncEnumerable<(Locator Loc, RecordView View, int RecordLength)> ScanFrom(
        int slot, long fromOffset, long toOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if ((uint)slot >= SlotCount)
            yield break;

        var st = _slots[slot];
        var fileId = FileIdOf(slot);
        var len = LogicalLength(st);
        if (toOffset >= 0)
            len = Math.Min(len, toOffset);
        var p = Math.Max(0, fromOffset);
        var prefetchPages = PrefetchPages;
        var nextPrefetchPage = p / _pagePayloadSize;
        var mustResynchronize = false;
        while (p < len) {
            var pageId = p / _pagePayloadSize;
            var pageStart = pageId * _pagePayloadSize;
            var nextPage = pageStart + _pagePayloadSize;
            if (pageId >= nextPrefetchPage) {
                await st.File.Prefetch(pageId, prefetchPages, cancellationToken).ConfigureAwait(false);
                nextPrefetchPage = pageId + prefetchPages;
            }

            DataPageFrame frame;
            try {
                (_, frame) = await GetFramedLogicalPage(st, st.Tail, pageId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                p = nextPage;
                mustResynchronize = true;
                continue;
            }

            if (mustResynchronize || p == pageStart && frame.IsContinuation) {
                if (!frame.HasRecordStart) {
                    p = nextPage;
                    mustResynchronize = true;
                    continue;
                }
                p = pageStart + frame.FirstRecordOffset;
                mustResynchronize = false;
            }

            int recordLength;
            try {
                recordLength = await ReadRecordLengthAt(st, p, len, cancellationToken).ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                p = nextPage;
                mustResynchronize = true;
                continue;
            }
            if (recordLength == 0) {
                p = nextPage;
                continue;
            }

            RecordRead read;
            try {
                read = await TryReadAt(st, p, cancellationToken).ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                read = default;
            }
            if (read.IsFound)
                yield return (new Locator(fileId, p), read.View, read.TotalLength);

            p += recordLength;
        }
    }

    public ValueTask Prefetch(
        uint fileId, long fromPageId, int maxPages, CancellationToken cancellationToken = default)
    {
        var st = TryGetSlot(fileId);
        return st == null ? default : st.File.Prefetch(fromPageId, maxPages, cancellationToken);
    }

    public void OnSuperseded(Locator oldLoc, int oldRecordLength)
    {
        var st = TryGetSlot(oldLoc.FileId);
        if (st == null)
            return;

        st.LiveBytes -= oldRecordLength;
        st.DeadBytes += oldRecordLength;
    }

    // Reconstructs a slot's accounting from an index without decrypting the log. DeadBytes includes page
    // padding, so it is intentionally less precise than restoring persisted counters.
    public void SeedAccounting(int slot, long liveBytes)
    {
        if ((uint)slot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        var st = _slots[slot];
        st.LiveBytes = liveBytes;
        st.DeadBytes = Math.Max(0, LogicalLength(st) - liveBytes);
    }

    public void SeedAccounting(int activeSlot, long liveBytes, long deadBytes)
    {
        if ((uint)activeSlot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(activeSlot));
        ArgumentOutOfRangeException.ThrowIfNegative(liveBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(deadBytes);

        var active = _slots[activeSlot];
        active.LiveBytes = liveBytes;
        active.DeadBytes = deadBytes;
        var inactive = _slots[1 - activeSlot];
        inactive.LiveBytes = 0;
        inactive.DeadBytes = 0;
    }

    // Zeroes a slot's counters. Used on the slot a compaction just drained: its bytes are awaiting
    // recycling, not garbage inside the active file, and counting them would immediately re-arm the
    // dead-ratio trigger that the compaction just satisfied.
    public void ResetAccounting(int slot)
    {
        if ((uint)slot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        var st = _slots[slot];
        st.LiveBytes = 0;
        st.DeadBytes = 0;
    }

    public async ValueTask SealTail()
    {
        await SealTail(_active).ConfigureAwait(false);
        if (_target is { } target)
            await SealTail(target).ConfigureAwait(false);
    }

    public ValueTask SealCompactionTarget()
    {
        var target = _target ?? throw new InvalidOperationException("No compaction is in progress.");
        return SealTail(target);
    }

    public async ValueTask Flush()
    {
        await SealTail().ConfigureAwait(false);
        await _active.File.Flush().ConfigureAwait(false);
        if (_target is { } target)
            await target.File.Flush().ConfigureAwait(false);
    }

    public async ValueTask FlushToDisk()
    {
        await SealTail().ConfigureAwait(false);
        await _active.File.FlushToDisk().ConfigureAwait(false);
        // The target's pages must be stable before the switch commit names it, so it is flushed too. The
        // free slot never is: nothing references it.
        if (_target is { } target)
            await target.File.FlushToDisk().ConfigureAwait(false);
    }

    public async ValueTask<long> MarkCommitted()
    {
        // Seals and pushes first: a committed extent must never name bytes the storage file has not seen,
        // and half a page cannot be committed — a page is the authenticated unit.
        await SealTail(_active).ConfigureAwait(false);
        await _active.File.Flush().ConfigureAwait(false);
        return _active.File.MarkCommitted();
    }

    public async ValueTask<int> BeginCompaction()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_target != null)
            throw new InvalidOperationException("A compaction is already in progress.");

        var st = _slots[1 - _active.Slot];
        await st.File.Recycle(MintFreeCacheId()).ConfigureAwait(false);
        st.LiveBytes = 0;
        st.DeadBytes = 0;
        st.TailBuffer = new byte[_pagePayloadSize];
        st.TailFill = 0;
        st.TailIsContinuation = false;
        st.TailFirstRecordOffset = -1;
        PublishTail(st);
        _target = st;
        return st.Slot;
    }

    public async ValueTask CommitCompaction(int newActiveSlot)
    {
        var target = _target ?? throw new InvalidOperationException("No compaction is in progress.");
        if (target.Slot != newActiveSlot)
            throw new ArgumentOutOfRangeException(nameof(newActiveSlot));

        // Both tails are sealed before the switch: the old active's tail bytes would otherwise stay
        // unreachable in RAM, and the new one must be a whole number of pages to be committable.
        await SealTail().ConfigureAwait(false);
        _active = target;
        _target = null;
        BurnedBytes = 0; // the burned range lived in the file the switch just left behind
    }

    public void AbortCompaction()
        => _target = null;

    // Protected/internal methods

    internal uint SlotCacheId(int slot) => _slots[slot].File.FileId;

    internal ReadLease AcquireReadLease()
    {
        if (!_slots[0].File.TryAcquireRead(out var first))
            throw new InvalidOperationException("The first data slot is being recycled.");
        if (_slots[1].File.TryAcquireRead(out var second))
            return new ReadLease(first, second);

        first.Dispose();
        throw new InvalidOperationException("The second data slot is being recycled.");
    }

    internal ValueTask<RecordRead> TryReadRecordLeased(
        Locator loc, CancellationToken cancellationToken = default)
    {
        var st = TryGetSlot(loc.FileId);
        return st == null ? default : TryReadAt(st, loc.Offset, cancellationToken);
    }

    internal ValueTask<RecordRead> TryReadRecord(
        Locator loc, uint slotCacheId, CancellationToken cancellationToken)
    {
        var st = TryGetSlot(loc.FileId);
        return st == null ? default : TryReadWithLease(st, loc.Offset, slotCacheId, cancellationToken);
    }

    // Private methods

    private async ValueTask<(Locator Locator, int RecordLength)> AppendTo(
        SlotState st, RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone)
    {
        var valueLen = isTombstone ? 0 : value.Length;
        var recordLength = RecordCodec.GetRecordLength(key.Length, valueLen, isTombstone);
        // Checked before anything is encoded: a locator packs the offset into 48 bits, and overflowing it
        // after the bytes are in the tail would leave the log advanced past a record nobody can address.
        if (LogicalLength(st) + recordLength > Locator.MaxOffset)
            throw new InvalidOperationException("The data file is full.");

        var isSinglePage = recordLength <= _pagePayloadSize
            && (isTombstone || value.Length <= _maxInlineValueBytes);
        var locator = isSinglePage
            ? await AppendSinglePage(st, flags, valueKind, key, value, isTombstone, recordLength)
                .ConfigureAwait(false)
            : await AppendMultiPage(st, flags, valueKind, key, value, isTombstone, recordLength)
                .ConfigureAwait(false);
        return (locator, recordLength);
    }

    private async ValueTask<Locator> AppendSinglePage(
        SlotState st, RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone, int recordLength)
    {
        if (st.TailFill + recordLength > _pagePayloadSize)
            await SealTail(st).ConfigureAwait(false);
        var offset = st.File.PageCount * _pagePayloadSize + st.TailFill;
        if (st.TailFirstRecordOffset < 0)
            st.TailFirstRecordOffset = st.TailFill;
        RecordCodec.Encode(st.TailBuffer.AsSpan(st.TailFill), flags, valueKind, key.Span, value.Span, isTombstone);
        st.TailFill += recordLength;
        PublishTail(st);
        st.LiveBytes += recordLength;
        return new Locator(FileIdOf(st.Slot), offset);
    }

    private async ValueTask<Locator> AppendMultiPage(
        SlotState st, RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone, int recordLength)
    {
        // Multi-page runs start at a page boundary and occupy whole pages (§5.2).
        if (st.TailFill > 0)
            await SealTail(st).ConfigureAwait(false);
        var offset = st.File.PageCount * _pagePayloadSize;
        var buf = ArrayPool<byte>.Shared.Rent(recordLength);
        try {
            RecordCodec.Encode(buf, flags, valueKind, key.Span, value.Span, isTombstone);
            var pos = 0;
            while (recordLength - pos >= _pagePayloadSize) {
                await AppendFramedPage(
                        st,
                        buf.AsMemory(pos, _pagePayloadSize),
                        isContinuation: pos > 0,
                        firstRecordOffset: pos == 0 ? 0 : -1)
                    .ConfigureAwait(false);
                pos += _pagePayloadSize;
            }
            var rem = recordLength - pos;
            if (rem > 0) {
                buf.AsSpan(pos, rem).CopyTo(st.TailBuffer);
                st.TailFill = rem;
                st.TailIsContinuation = pos > 0;
                st.TailFirstRecordOffset = pos == 0 ? 0 : -1;
            }
            else {
                st.TailFill = 0;
                st.TailIsContinuation = false;
                st.TailFirstRecordOffset = -1;
            }
            PublishTail(st);
        }
        finally {
            ArrayPool<byte>.Shared.Return(buf, clearArray: true);
        }
        st.LiveBytes += recordLength;
        return new Locator(FileIdOf(st.Slot), offset);
    }

    private async ValueTask SealTail(SlotState st)
    {
        if (st.TailFill == 0)
            return;

        st.TailBuffer.AsSpan(st.TailFill).Clear();
        await AppendFramedPage(
                st,
                st.TailBuffer,
                st.TailIsContinuation,
                st.TailFirstRecordOffset)
            .ConfigureAwait(false);
        st.TailBuffer = new byte[_pagePayloadSize];
        st.TailFill = 0;
        st.TailIsContinuation = false;
        st.TailFirstRecordOffset = -1;
        PublishTail(st);
    }

    private async ValueTask AppendFramedPage(
        SlotState st,
        ReadOnlyMemory<byte> payload,
        bool isContinuation,
        int firstRecordOffset)
    {
        var page = ArrayPool<byte>.Shared.Rent(_pageSize);
        try {
            var framed = page.AsMemory(0, _pageSize);
            framed.Span.Clear();
            DataPageFraming.Write(framed.Span, isContinuation, firstRecordOffset);
            payload.CopyTo(framed[KvasarConstants.DataPageHeaderSize..]);
            await st.File.AppendPage(framed).ConfigureAwait(false);
        }
        finally {
            ArrayPool<byte>.Shared.Return(page, clearArray: true);
        }
    }

    private async ValueTask<RecordRead> TryReadWithLease(
        SlotState st, long offset, uint slotCacheId, CancellationToken cancellationToken)
    {
        if (!st.File.TryAcquireRead(slotCacheId, out var lease))
            return RecordRead.Retry;

        using (lease)
            return await TryReadAt(st, offset, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<RecordRead> TryReadAt(
        SlotState st, long offset, CancellationToken cancellationToken)
        => TryReadAt(st, offset, -1, cancellationToken);

    private async ValueTask<RecordRead> TryReadAt(
        SlotState st, long offset, long maxEndOffset, CancellationToken cancellationToken)
    {
        // Pin the slot's incarnation for the whole read. A multi-page record is assembled from several
        // GetPage calls, and BeginCompaction can recycle this slot between any two of them — which
        // yields a record stitched from two different lives of the same file. With encryption on, the
        // pages are individually genuine, so nothing downstream can detect it. A recycled slot means
        // the record is gone or relocated, and the caller re-resolves through the index, so reporting
        // a miss is both correct and what the compaction switch already promises readers.
        var incarnation = st.File.FileId;
        var tail = st.Tail; // one read: everything below must agree on the same tail generation
        var len = LogicalLength(st, tail);
        if (maxEndOffset >= 0)
            len = Math.Min(len, maxEndOffset);
        if (offset < 0 || offset >= len)
            return default;

        var pageId = offset / _pagePayloadSize;
        var inPage = (int)(offset % _pagePayloadSize);
        var firstPage = await GetLogicalPage(st, tail, pageId, cancellationToken).ConfigureAwait(false);
        int totalLen;
        {
            var span = firstPage.Span;
            if (inPage >= span.Length
                || !RecordCodec.TryReadHeader(span[inPage..], len - offset, out totalLen))
                return default;
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
        // Re-check after assembling: if the slot was recycled while we walked its pages, the buffer may
        // hold bytes from two incarnations, and with encryption on every one of them authenticates.
        if (st.File.FileId != incarnation)
            return default;

        return RecordCodec.TryDecode(buf.AsMemory(0, totalLen), out var spanned, out _)
            ? new RecordRead(true, spanned, totalLen)
            : default;
    }

    private async ValueTask<int> ReadRecordLengthAt(
        SlotState st, long offset, long len, CancellationToken cancellationToken)
    {
        var tail = st.Tail;
        var pageId = offset / _pagePayloadSize;
        var inPage = (int)(offset % _pagePayloadSize);
        var page = await GetLogicalPage(st, tail, pageId, cancellationToken).ConfigureAwait(false);
        return TryReadRecordLength(page.Span, offset, len, inPage, out var totalLen) ? totalLen : 0;
    }

    // The record's on-stream length, read from its varint header. Every bound is checked against len — the
    // caller's single tail snapshot — so one operation can't mix two tail generations.
    private static bool TryReadRecordLength(
        ReadOnlySpan<byte> page, long offset, long len, int inPage, out int totalLen)
    {
        totalLen = 0;
        if (inPage >= page.Length)
            return false;
        if (!Varint.TryRead(page[inPage..], out var bodyLenU, out var recLenBytes) || bodyLenU == 0)
            return false;
        // Bound before narrowing: a varint >= 2^63 casts negative and defeats the checks below.
        if (bodyLenU > (ulong)(len - offset))
            return false;

        var total = recLenBytes + (long)bodyLenU;
        if (offset + total > len || total > int.MaxValue)
            return false;

        totalLen = (int)total;
        return true;
    }

    // All three helpers below take the caller's single snapshot read, so one operation can't observe the
    // tail from two different generations.
    private long LogicalLength(SlotState st) => LogicalLength(st, st.Tail);

    private long LogicalLength(SlotState st, TailSnapshot? tail)
        => tail is { } t
            ? t.PageId * _pagePayloadSize + t.Fill
            : st.File.PageCount * _pagePayloadSize;

    private async ValueTask<ReadOnlyMemory<byte>> GetLogicalPage(
        SlotState st, TailSnapshot? tail, long pageId, CancellationToken cancellationToken)
    {
        var (payload, _) = await GetFramedLogicalPage(st, tail, pageId, cancellationToken)
            .ConfigureAwait(false);
        return payload;
    }

    private async ValueTask<(ReadOnlyMemory<byte> Payload, DataPageFrame Frame)> GetFramedLogicalPage(
        SlotState st, TailSnapshot? tail, long pageId, CancellationToken cancellationToken)
    {
        if (tail is { } t && t.PageId == pageId)
            return (
                t.Buffer.AsMemory(0, t.Fill),
                new DataPageFrame(t.IsContinuation, t.FirstRecordOffset));

        var page = await st.File.GetPage(pageId, cancellationToken).ConfigureAwait(false);
        if (!DataPageFraming.TryRead(page.Span, _pagePayloadSize, out var frame))
            throw new KvasarCorruptException($"Invalid data-page framing at page {pageId}.");
        return (page[KvasarConstants.DataPageHeaderSize..], frame);
    }

    private bool TryGetLogicalPageCached(
        SlotState st, TailSnapshot? tail, long pageId, out ReadOnlyMemory<byte> page)
    {
        if (tail is { } t && t.PageId == pageId) {
            page = t.Buffer.AsMemory(0, t.Fill);
            return true;
        }
        if (!st.File.TryGetCachedPage(pageId, out var framed)
            || !DataPageFraming.TryRead(framed.Span, _pagePayloadSize, out _)) {
            page = default;
            return false;
        }
        page = framed[KvasarConstants.DataPageHeaderSize..];
        return true;
    }

    // Writer-only: republishes the tail so readers pick up the newly appended bytes.
    private static void PublishTail(SlotState st)
        => st.Tail = new TailSnapshot(
            st.TailBuffer,
            st.TailFill,
            st.File.PageCount,
            st.TailIsContinuation,
            st.TailFirstRecordOffset);

    private static int CopyPart(ReadOnlyMemory<byte> page, int start, byte[] destination, int copied, int totalLen)
    {
        var span = page.Span;
        if (start >= span.Length)
            return -1;
        var n = Math.Min(span.Length - start, totalLen - copied);
        span.Slice(start, n).CopyTo(destination.AsSpan(copied));
        return n;
    }

    private SlotState? TryGetSlot(uint fileId)
        => fileId is >= 1 and <= SlotCount ? _slots[fileId - 1] : null;

    private static uint FileIdOf(int slot) => (uint)slot + 1;

    private uint MintFreeCacheId()
    {
        // PageCache keys *decrypted* pages by (fileId, pageId), and PagedFile takes its file id from the
        // file's unauthenticated plaintext header. If two slots ever reported the same id, one file's read
        // would be served the other's plaintext — after decryption, where AES-GCM cannot catch it (§3.2).
        // So the ids come from the store's monotonic counter, skipping anything already in use.
        while (true) {
            var fileId = _mintCacheId();
            if (fileId != _slots[0].File.FileId && fileId != _slots[1].File.FileId)
                return fileId;
        }
    }

    private static void RequireArguments(IStorageFile[] slotFiles, Func<uint> mintCacheId)
    {
        ArgumentNullException.ThrowIfNull(slotFiles);
        ArgumentNullException.ThrowIfNull(mintCacheId);
        if (slotFiles.Length != SlotCount)
            throw new ArgumentException($"Exactly {SlotCount} slot files are required.", nameof(slotFiles));
    }

    private static async ValueTask DisposeOpened(SlotState[] slots)
    {
        foreach (var slot in slots)
            if (slot is not null)
                await slot.File.DisposeAsync().ConfigureAwait(false);
    }

    // Nested types

    internal readonly struct ReadLease : IDisposable
    {
        private readonly PagedFile.ReadLease _first;
        private readonly PagedFile.ReadLease _second;

        public ReadLease(PagedFile.ReadLease first, PagedFile.ReadLease second)
        {
            _first = first;
            _second = second;
        }

        public void Dispose()
        {
            _second.Dispose();
            _first.Dispose();
        }
    }
}
