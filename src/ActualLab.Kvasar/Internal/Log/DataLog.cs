using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// The two-slot <c>.kdat</c> record log (DESIGN-Durability.md §3.2, §4): two fixed data files, exactly one
/// active, the other free or a compaction target. Nothing is ever created or deleted — slots are recycled
/// in place. Single-writer for append/seal, concurrent readers for the <c>*Read*</c> methods. Offsets are
/// logical: <c>pageId = offset / PageSize</c>, in-page = <c>offset % PageSize</c>.
/// </summary>
public sealed class DataLog : IAsyncDisposable
{
    public const int SlotCount = 2;

    // The unsealed tail, published as one immutable triple. Readers may resolve locators that point into the
    // tail, so they must see a consistent (buffer, fill, pageId) set: reading those fields separately lets a
    // concurrent seal swap the buffer between them, and the reader then slices the fresh empty one and
    // reports a miss for a key that exists. Bytes below Fill are never rewritten, and SealTail installs a new
    // buffer rather than reusing this one, so a snapshot stays valid for as long as anyone holds it.
    private sealed record TailSnapshot(byte[] Buffer, int Fill, long PageId);

    private sealed class SlotState
    {
        public required int Slot;
        public required PagedFile File;
        public long LiveBytes;
        public long DeadBytes;
        // Writer-only; Tail is their published view.
        public byte[] TailBuffer = [];
        public int TailFill;
        public volatile TailSnapshot? Tail; // non-null only while this slot is an append target
    }

    private readonly int _pageSize;
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
    // ~1 MiB of readahead per I/O: enough to amortize the per-operation cost of async I/O over a
    // sequential walk without pinning much of the page-cache budget.
    public int PrefetchPages => Math.Max(1, (1 << 20) / _pageSize);

    // Logical end of the active file, unsealed tail included — where the next append lands.
    public long ActiveHwm => LogicalLength(_active);
    // Physical extent, the currency of the superblock and of PagedFile.Open — not a logical offset.
    public long ActiveCommitLength => _active.File.CommitLength;
    public long ActiveCommittedOffset => _active.File.CommittedPageCount * _pageSize;
    // Where appending resumes: at or above the physical end, so a torn tail's page id is never re-issued.
    public long ActiveResumeOffset => _active.File.ResumePageId * _pageSize;
    // The [committedEnd, resumeOffset) gap burned by a torn tail, measured once at open (§5.2.1). It is
    // dead space the store must account for; DeadBytes below does not include it.
    public long BurnedBytes { get; private set; }

    public long LiveBytes => _slots[0].LiveBytes + _slots[1].LiveBytes;
    public long DeadBytes => _slots[0].DeadBytes + _slots[1].DeadBytes;
    public long FileBytes => _slots[0].File.Length + _slots[1].File.Length;

    public static async ValueTask<DataLog> Create(
        IStorageFile[] slotFiles, int pageSize, IPageCipherFactory cipherFactory,
        uint formatVer, PageCache cache, int maxInlineValueBytes, Func<uint> mintCacheId)
    {
        RequireArguments(slotFiles, mintCacheId);
        var slots = new SlotState[SlotCount];
        var openCount = 0;
        try {
            for (var i = 0; i < SlotCount; i++) {
                var file = await PagedFile
                    .Create(slotFiles[i], mintCacheId(), pageSize, cipherFactory, formatVer, cache)
                    .ConfigureAwait(false);
                slots[i] = new SlotState { Slot = i, File = file };
                openCount = i + 1;
            }
            if (slots[0].File.FileId == slots[1].File.FileId)
                throw new InvalidOperationException("mintCacheId returned the same id twice.");
        }
        catch {
            await DisposeOpened(slots, openCount).ConfigureAwait(false);
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
        var openCount = 0;
        try {
            for (var i = 0; i < SlotCount; i++) {
                // Only the active slot has a committed extent; the other is free or an abandoned compaction
                // target, so nothing in it is referenced and its whole-page end is extent enough.
                // Mint the PageCache id rather than inheriting the header's: the header is unauthenticated
                // plaintext, and the cache holds *decrypted* pages keyed by (fileId, pageId), so two files
                // claiming one id would serve each other's plaintext past the point AES-GCM could catch it.
                var file = await PagedFile
                    .Open(slotFiles[i], cipherFactory, formatVer, cache,
                        i == activeSlot ? activeCommitLength : -1, mintCacheId(), cancellationToken)
                    .ConfigureAwait(false);
                slots[i] = new SlotState { Slot = i, File = file };
                openCount = i + 1;
                if (file.PageSize != pageSize)
                    throw new KvasarCorruptException("Data file page size does not match the store's.");
            }
        }
        catch {
            await DisposeOpened(slots, openCount).ConfigureAwait(false);
            throw;
        }

        var result = new DataLog(pageSize, maxInlineValueBytes, mintCacheId, slots, activeSlot);
        var active = slots[activeSlot].File;
        result.BurnedBytes = Math.Max(0, active.ResumePageId - active.CommittedPageCount) * pageSize;
        return result;
    }

    private DataLog(
        int pageSize, int maxInlineValueBytes, Func<uint> mintCacheId,
        SlotState[] slots, int activeSlot)
    {
        _pageSize = pageSize;
        _maxInlineValueBytes = maxInlineValueBytes <= 0 ? pageSize : maxInlineValueBytes;
        _mintCacheId = mintCacheId;
        _slots = slots;
        _active = slots[activeSlot];
        _active.TailBuffer = new byte[pageSize];
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
        return st == null ? default : TryReadAt(st, loc.Offset, cancellationToken);
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

        var pageId = offset / _pageSize;
        var inPage = (int)(offset % _pageSize);
        if (!TryGetLogicalPageCached(st, tail, pageId, out var firstPage))
            return false;

        var span = firstPage.Span;
        if (!TryReadRecordLength(span, offset, len, inPage, out var totalLen))
            return false;
        if (inPage + totalLen > span.Length)
            return false;

        return RecordCodec.TryDecode(firstPage.Slice(inPage, totalLen), out view, out _);
    }

    // Walks records in write order over one slot. toOffset < 0 means "to the logical end"; recovery passes
    // the committed offset instead, so the walk can never step into the burned range above it (§5.2.1).
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
        var nextPrefetchPage = p / _pageSize;
        while (p < len) {
            var pageId = p / _pageSize;
            if (pageId >= nextPrefetchPage) {
                await st.File.Prefetch(pageId, prefetchPages, cancellationToken).ConfigureAwait(false);
                nextPrefetchPage = pageId + prefetchPages;
            }

            RecordRead read;
            var isPageBroken = false;
            try {
                read = await TryReadAt(st, p, cancellationToken).ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                // A page that fails its tag ends the walk: it is a torn tail or the burned range, and
                // everything past it is unreferenced by construction.
                read = default;
                isPageBroken = true;
            }
            if (isPageBroken)
                yield break;

            if (read.IsFound) {
                yield return (new Locator(fileId, p), read.View, read.TotalLength);
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

    // Seeds a slot's accounting at open from the loaded index (no log decrypt): LiveBytes is the sum of the
    // live record lengths in that slot; DeadBytes is the rest of its logical bytes (superseded records plus
    // page padding — a slight over-count that only makes compaction a touch more eager).
    public void SeedAccounting(int slot, long liveBytes)
    {
        if ((uint)slot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));

        var st = _slots[slot];
        st.LiveBytes = liveBytes;
        st.DeadBytes = Math.Max(0, LogicalLength(st) - liveBytes);
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
        st.TailBuffer = new byte[_pageSize];
        st.TailFill = 0;
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

        var isSinglePage = recordLength <= _pageSize && (isTombstone || value.Length <= _maxInlineValueBytes);
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
        if (st.TailFill + recordLength > _pageSize)
            await SealTail(st).ConfigureAwait(false);
        var offset = st.File.PageCount * _pageSize + st.TailFill;
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
        var offset = st.File.PageCount * _pageSize;
        var buf = ArrayPool<byte>.Shared.Rent(recordLength);
        try {
            RecordCodec.Encode(buf, flags, valueKind, key.Span, value.Span, isTombstone);
            var pos = 0;
            while (recordLength - pos >= _pageSize) {
                await st.File.AppendPage(buf.AsMemory(pos, _pageSize)).ConfigureAwait(false);
                pos += _pageSize;
            }
            var rem = recordLength - pos;
            if (rem > 0) {
                buf.AsSpan(pos, rem).CopyTo(st.TailBuffer);
                st.TailFill = rem;
            }
            else
                st.TailFill = 0;
            PublishTail(st);
        }
        finally {
            ArrayPool<byte>.Shared.Return(buf);
        }
        st.LiveBytes += recordLength;
        return new Locator(FileIdOf(st.Slot), offset);
    }

    private async ValueTask SealTail(SlotState st)
    {
        if (st.TailFill == 0)
            return;

        st.TailBuffer.AsSpan(st.TailFill).Clear(); // pad the remainder with zeros
        await st.File.AppendPage(st.TailBuffer).ConfigureAwait(false);
        st.TailBuffer = new byte[_pageSize]; // fresh buffer so any handed-out zero-copy slice stays immutable
        st.TailFill = 0;
        PublishTail(st);
    }

    private async ValueTask<RecordRead> TryReadAt(
        SlotState st, long offset, CancellationToken cancellationToken)
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
        if (offset < 0 || offset >= len)
            return default;

        var pageId = offset / _pageSize;
        var inPage = (int)(offset % _pageSize);
        var firstPage = await GetLogicalPage(st, tail, pageId, cancellationToken).ConfigureAwait(false);
        int totalLen;
        {
            var span = firstPage.Span;
            if (!TryReadRecordLength(span, offset, len, inPage, out totalLen))
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
        => tail is { } t ? t.PageId * _pageSize + t.Fill : st.File.PageCount * _pageSize;

    private static ValueTask<ReadOnlyMemory<byte>> GetLogicalPage(
        SlotState st, TailSnapshot? tail, long pageId, CancellationToken cancellationToken)
        => tail is { } t && t.PageId == pageId
            ? new ValueTask<ReadOnlyMemory<byte>>(t.Buffer.AsMemory(0, t.Fill))
            : st.File.GetPage(pageId, cancellationToken);

    private static bool TryGetLogicalPageCached(
        SlotState st, TailSnapshot? tail, long pageId, out ReadOnlyMemory<byte> page)
    {
        if (tail is { } t && t.PageId == pageId) {
            page = t.Buffer.AsMemory(0, t.Fill);
            return true;
        }
        return st.File.TryGetCachedPage(pageId, out page);
    }

    // Writer-only: republishes the tail so readers pick up the newly appended bytes.
    private static void PublishTail(SlotState st)
        => st.Tail = new TailSnapshot(st.TailBuffer, st.TailFill, st.File.PageCount);

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

    private static async ValueTask DisposeOpened(SlotState[] slots, int count)
    {
        for (var i = 0; i < count; i++)
            await slots[i].File.DisposeAsync().ConfigureAwait(false);
    }
}
