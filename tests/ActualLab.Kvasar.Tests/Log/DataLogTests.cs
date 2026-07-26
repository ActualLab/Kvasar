using System.Collections.Concurrent;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;
using ActualLab.Kvasar.Tests.Paging;

namespace ActualLab.Kvasar.Tests.Log;

public class DataLogTests
{
    private const int PageSize = 512;
    private const uint FormatVer = 7;
    private const int HeaderSize = 64;
    private const int Overhead = 16;
    private const int OnDiskPageSize = PageSize + Overhead;

    [Fact]
    public async Task AppendReadRoundTripsMixedSizes()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var written = new List<(Locator Loc, byte[] Key, byte[] Value)>();
        for (var i = 0; i < 200; i++) {
            // Some multi-page, some empty, some tiny.
            var len = i % 7 == 0 ? 1500 : (i % 3 == 0 ? 0 : 20 + (i % 40));
            var key = Key(i);
            var value = Value(i, len);
            var (loc, recordLength) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, key, value, false);
            loc.FileId.Should().Be(1u); // slot 0 is 1-based file id 1
            recordLength.Should().BeGreaterThan(0);
            written.Add((loc, key, value));
        }

        foreach (var (loc, key, value) in written) {
            var view = await log.ReadRecord(loc);
            view.Key.ToArray().Should().Equal(key);
            view.Value.ToArray().Should().Equal(value);
            view.IsTombstone.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TombstoneRoundTrips()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var key = Key(1);
        var (loc, _) = await log.Append(
            RecordFlags.None, KvasarValueKind.Raw, key, ReadOnlyMemory<byte>.Empty, true);

        var view = await log.ReadRecord(loc);
        view.IsTombstone.Should().BeTrue();
        view.Key.ToArray().Should().Equal(key);
        view.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task SinglePageReadsAliasThePageAndMultiPageReadsCopy()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var (small, _) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(1), Value(1, 16), false);
        SameBackingArray((await log.ReadRecord(small)).Value, (await log.ReadRecord(small)).Value)
            .Should().BeTrue("single-page reads must alias the same page buffer");

        var big = Value(2, (3 * PageSize) + 40);
        var (large, recordLength) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(2), big, false);
        recordLength.Should().BeGreaterThan(PageSize);
        (await log.ReadRecord(large)).Value.ToArray().Should().Equal(big);
        SameBackingArray((await log.ReadRecord(large)).Value, (await log.ReadRecord(large)).Value)
            .Should().BeFalse("multi-page reads are copied");
    }

    [Fact]
    public async Task ReadsTheUnsealedTail()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        log.ActiveHwm.Should().Be(0);

        var value = Value(0, 10);
        var (loc, recordLength) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(0), value, false);
        log.ActiveHwm.Should().Be(recordLength);
        ctx.Files[0].Length.Should().Be(HeaderSize, "nothing is sealed yet");

        // Both read paths must see the record while it lives only in the in-RAM tail.
        (await log.ReadRecord(loc)).Value.ToArray().Should().Equal(value);
        log.TryReadRecordCached(loc, out var cached).Should().BeTrue();
        cached.Value.ToArray().Should().Equal(value);
    }

    [Fact]
    public async Task ReadsFromBothSlots()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var oldValue = Value(1, 100);
        var (oldLoc, _) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(1), oldValue, false);

        var target = await log.BeginCompaction();
        target.Should().Be(1);
        var newValue = Value(2, 100);
        var (newLoc, _) = await log.AppendToTarget(RecordFlags.None, KvasarValueKind.Raw, Key(2), newValue, false);
        newLoc.FileId.Should().Be(2u);

        // Both slots resolve while the compaction is in flight.
        (await log.ReadRecord(oldLoc)).Value.ToArray().Should().Equal(oldValue);
        (await log.ReadRecord(newLoc)).Value.ToArray().Should().Equal(newValue);

        await log.CommitCompaction(target);
        log.ActiveSlot.Should().Be(1);
        log.ActiveFileId.Should().Be(2u);
        (await log.ReadRecord(oldLoc)).Value.ToArray().Should().Equal(oldValue);
        (await log.ReadRecord(newLoc)).Value.ToArray().Should().Equal(newValue);
    }

    [Fact]
    public async Task CompactionSwitchesTheActiveSlotAndResetsItsAccounting()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var (_, oldLength) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(1), Value(1, 100), false);
        log.LiveBytes.Should().Be(oldLength);
        log.ActiveSlot.Should().Be(0);
        log.CompactionTargetSlot.Should().Be(-1);

        var target = await log.BeginCompaction();
        log.CompactionTargetSlot.Should().Be(1);
        var (_, newLength) = await log.AppendToTarget(
            RecordFlags.None, KvasarValueKind.Raw, Key(1), Value(1, 100), false);
        log.LiveBytes.Should().Be(oldLength + newLength, "both slots count until the switch");

        await log.CommitCompaction(target);
        log.CompactionTargetSlot.Should().Be(-1);
        // The old slot is now free; the store drops its accounting by seeding it empty.
        log.SeedAccounting(0, 0);
        log.LiveBytes.Should().Be(newLength);

        var next = await log.BeginCompaction();
        next.Should().Be(0, "compaction always targets the free slot");
    }

    [Fact]
    public async Task AppendToTargetWithoutBeginCompactionThrows()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var act = async () => await log.AppendToTarget(
            RecordFlags.None, KvasarValueKind.Raw, Key(1), Value(1, 8), false);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var commitAct = async () => await log.CommitCompaction(1);
        await commitAct.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task BeginCompactionRecyclesTheFreeSlotWithAFreshCacheId()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var freeId = log.SlotCacheId(1);
        await log.BeginCompaction();
        log.SlotCacheId(1).Should().NotBe(freeId);
        log.SlotCacheId(1).Should().NotBe(log.SlotCacheId(0));
    }

    [Fact]
    public async Task OnSupersededMovesBytesLiveToDead()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var (loc0, len0) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(0), Value(0, 50), false);
        var (_, len1) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(1), Value(1, 60), false);

        log.DeadBytes.Should().Be(0);
        log.LiveBytes.Should().Be(len0 + len1);

        log.OnSuperseded(loc0, len0);
        log.LiveBytes.Should().Be(len1);
        log.DeadBytes.Should().Be(len0);

        // A locator naming neither slot is ignored rather than throwing.
        log.OnSuperseded(new Locator(9, 0), 1000);
        log.DeadBytes.Should().Be(len0);
    }

    [Fact]
    public async Task SeedAccountingSplitsLogicalBytesIntoLiveAndDead()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        for (var i = 0; i < 40; i++)
            await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(i), Value(i, 60), false);
        await log.SealTail();

        var gross = log.ActiveHwm;
        log.SeedAccounting(0, 500);
        log.LiveBytes.Should().Be(500);
        log.DeadBytes.Should().Be(gross - 500);
    }

    [Fact]
    public async Task ScanFromYieldsRecordsInWriteOrder()
    {
        var ctx = new Context();
        var expected = new List<(byte[] Key, byte[] Value)>();
        long commitLength;
        await using (var log = await ctx.Create()) {
            for (var i = 0; i < 150; i++) {
                var len = i % 11 == 0 ? 1200 : 25;
                var key = Key(i);
                var value = Value(i, len);
                await log.Append(RecordFlags.None, KvasarValueKind.Raw, key, value, false);
                expected.Add((key, value));
            }
            commitLength = await log.MarkCommitted();
        }

        await using var reopened = await ctx.Open(0, commitLength);
        var scanned = await ScanAll(reopened, reopened.ActiveSlot, 0, -1);
        scanned.Count.Should().Be(expected.Count);
        for (var i = 0; i < expected.Count; i++) {
            scanned[i].View.Key.ToArray().Should().Equal(expected[i].Key);
            scanned[i].View.Value.ToArray().Should().Equal(expected[i].Value);
            (await reopened.ReadRecord(scanned[i].Loc)).Value.ToArray().Should().Equal(expected[i].Value);
        }
        scanned.Select(x => x.Loc.Offset).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ScanFromHonorsTheStartAndEndBounds()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var offsets = new List<long>();
        for (var i = 0; i < 30; i++) {
            var (loc, _) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(i), Value(i, 40), false);
            offsets.Add(loc.Offset);
        }
        await log.SealTail();

        var fromFifth = await ScanAll(log, 0, offsets[5], -1);
        fromFifth[0].Loc.Offset.Should().Be(offsets[5]);
        fromFifth.Count.Should().Be(offsets.Count - 5);

        var bounded = await ScanAll(log, 0, 0, offsets[10]);
        bounded.Count.Should().Be(10, "the record at the bound starts outside it");

        var otherSlot = await ScanAll(log, 1, 0, -1);
        otherSlot.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkCommittedSealsTheTailAndAdvancesTheExtent()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        log.ActiveCommitLength.Should().Be(HeaderSize);
        log.ActiveCommittedOffset.Should().Be(0);

        await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(0), Value(0, 40), false);
        var commitLength = await log.MarkCommitted();
        commitLength.Should().Be(HeaderSize + OnDiskPageSize, "the partial tail page is sealed and padded");
        log.ActiveCommitLength.Should().Be(commitLength);
        log.ActiveCommittedOffset.Should().Be(PageSize);
        log.ActiveHwm.Should().Be(PageSize);
        ctx.Files[0].Length.Should().Be(commitLength);

        // Committing again with nothing appended is a no-op, not a new page.
        (await log.MarkCommitted()).Should().Be(commitLength);
    }

    [Fact]
    public async Task FlushToDiskIsTheOnlyDurabilityBarrier()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(0), Value(0, 40), false);

        await log.Flush();
        ctx.Files[0].Length.Should().Be(HeaderSize + OnDiskPageSize);
        ctx.Files[0].FlushToDiskCount.Should().Be(0);

        await log.FlushToDisk();
        ctx.Files[0].FlushToDiskCount.Should().Be(1);
        ctx.Files[1].FlushToDiskCount.Should().Be(0, "the free slot is never flushed");
    }

    [Fact]
    public async Task ReopenSeesEveryCommittedRecord()
    {
        var ctx = new Context();
        var written = new List<(Locator Loc, byte[] Value)>();
        long commitLength;
        await using (var log = await ctx.Create()) {
            for (var i = 0; i < 50; i++) {
                var value = Value(i, 30 + i);
                var (loc, _) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(i), value, false);
                written.Add((loc, value));
            }
            await log.FlushToDisk();
            commitLength = await log.MarkCommitted();
        }

        await using var reopened = await ctx.Open(0, commitLength);
        reopened.ActiveSlot.Should().Be(0);
        reopened.ActiveCommitLength.Should().Be(commitLength);
        reopened.BurnedBytes.Should().Be(0);
        foreach (var (loc, value) in written)
            (await reopened.ReadRecord(loc)).Value.ToArray().Should().Equal(value);
    }

    // §5.2.1: PagedFile.Open resumes at ceil(...), so a torn tail page's id is burned. Those bytes sit
    // between the committed extent and the resume point, and the store has to account them as dead.
    [Fact]
    public async Task BurnedRangeIsReportedAfterATornTail()
    {
        var ctx = new Context();
        long commitLength;
        await using (var log = await ctx.Create()) {
            for (var i = 0; i < 3; i++) {
                await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(i), Value(i, 300), false);
                await log.SealTail();
            }
            await log.FlushToDisk();
            commitLength = await log.MarkCommitted();
        }
        commitLength.Should().Be(HeaderSize + (3 * OnDiskPageSize));

        // A crash lands half of page 3 on disk, above the committed extent.
        await ctx.Files[0].Write(commitLength, new byte[OnDiskPageSize / 2]);

        await using var reopened = await ctx.Open(0, commitLength);
        reopened.ActiveCommittedOffset.Should().Be(3 * PageSize);
        reopened.ActiveResumeOffset.Should().Be(4 * PageSize);
        reopened.BurnedBytes.Should().Be(PageSize);

        // Appending resumes above the burned page, so its id is never re-issued.
        var value = Value(99, 100);
        var (loc, _) = await reopened.Append(RecordFlags.None, KvasarValueKind.Raw, Key(99), value, false);
        loc.Offset.Should().Be(4 * PageSize);
        (await reopened.ReadRecord(loc)).Value.ToArray().Should().Equal(value);

        // A scan bounded by the committed extent never walks into the gap.
        var scanned = await ScanAll(reopened, 0, 0, reopened.ActiveCommittedOffset);
        scanned.Count.Should().Be(3);
    }

    [Fact]
    public async Task ScanStopsAtAPageThatFailsAuthentication()
    {
        var ctx = new Context();
        long commitLength;
        await using (var log = await ctx.Create()) {
            for (var i = 0; i < 3; i++) {
                await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(i), Value(i, 300), false);
                await log.SealTail();
            }
            commitLength = await log.MarkCommitted();
        }
        await ctx.Files[0].Write(commitLength, new byte[OnDiskPageSize / 2]);

        await using var reopened = await ctx.Open(0, commitLength);
        // Unbounded, the walk reaches the burned page and must stop rather than throw.
        var scanned = await ScanAll(reopened, 0, 0, -1);
        scanned.Count.Should().Be(3);
    }

    [Fact]
    public async Task OpenGivesTheSlotsDistinctPageCacheIds()
    {
        // PageCache keys decrypted pages by (fileId, pageId), and PagedFile reads that id from an
        // unauthenticated plaintext header — so two slots claiming the same id must be separated at open.
        var ctx = new Context();
        var files = ctx.Files;
        await using (var pf = await PagedFile.Create(
            files[0], 7, PageSize, ctx.CipherFactory, FormatVer, ctx.Cache)) {
            await pf.AppendPage(new byte[PageSize]);
            await pf.Flush();
        }
        await using (var pf = await PagedFile.Create(
            files[1], 7, PageSize, ctx.CipherFactory, FormatVer, ctx.Cache))
            await pf.Flush();

        await using var log = await ctx.Open(0, HeaderSize + OnDiskPageSize);

        // Both ids are minted, so the colliding header value cannot reach the cache at all — stronger
        // than detecting the collision afterwards, which left the active slot on its header's id.
        log.SlotCacheId(0).Should().NotBe(log.SlotCacheId(1));
        log.SlotCacheId(0).Should().NotBe(7u, "the header's id is unauthenticated and must not key the cache");
        log.SlotCacheId(1).Should().NotBe(7u);

        // ... and the active slot's pages still read back correctly under the minted id.
        var read = await log.TryReadRecord(new Locator(log.ActiveFileId, 0));
        read.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateGivesTheSlotsDistinctPageCacheIds()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        log.SlotCacheId(0).Should().NotBe(log.SlotCacheId(1));
    }

    [Fact]
    public async Task OpenRejectsAPageSizeMismatch()
    {
        var ctx = new Context();
        await using (var log = await ctx.Create())
            await log.Flush();

        var act = async () => await DataLog.Open(
            ctx.StorageFiles, 0, HeaderSize, PageSize * 2, ctx.CipherFactory, FormatVer, ctx.Cache, 0,
            ctx.MintCacheId);
        await act.Should().ThrowAsync<KvasarCorruptException>();
    }

    [Fact]
    public async Task OpenValidatesItsArguments()
    {
        var ctx = new Context();
        await using (var log = await ctx.Create())
            await log.Flush();

        var wrongCount = async () => await DataLog.Open(
            [ctx.Files[0]], 0, HeaderSize, PageSize, ctx.CipherFactory, FormatVer, ctx.Cache, 0, ctx.MintCacheId);
        await wrongCount.Should().ThrowAsync<ArgumentException>();

        var wrongSlot = async () => await DataLog.Open(
            ctx.StorageFiles, 2, HeaderSize, PageSize, ctx.CipherFactory, FormatVer, ctx.Cache, 0,
            ctx.MintCacheId);
        await wrongSlot.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadsOfUnknownFileIdsAndOffsetsMiss()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var (loc, recordLength) = await log.Append(
            RecordFlags.None, KvasarValueKind.Raw, Key(0), Value(0, 20), false);

        (await log.TryReadRecord(new Locator(3, 0))).IsFound.Should().BeFalse();
        (await log.TryReadRecord(new Locator(1, recordLength))).IsFound.Should().BeFalse();
        (await log.TryReadRecord(new Locator(2, 0))).IsFound.Should().BeFalse();
        log.TryReadRecordCached(new Locator(3, 0), out _).Should().BeFalse();
        (await log.TryReadRecord(loc)).IsFound.Should().BeTrue();
    }

    [Fact]
    public async Task MaxInlineValueBytesForcesLargeValuesOntoWholePages()
    {
        var ctx = new Context();
        await using var log = await ctx.Create(maxInlineValueBytes: 32);
        var (small, _) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(0), Value(0, 16), false);
        small.Offset.Should().Be(0);

        var value = Value(1, 200); // fits a page, but exceeds the inline limit
        var (large, _) = await log.Append(RecordFlags.None, KvasarValueKind.Raw, Key(1), value, false);
        (large.Offset % PageSize).Should().Be(0, "a non-inline value starts at a page boundary");
        (await log.ReadRecord(large)).Value.ToArray().Should().Equal(value);
    }

    // The TailSnapshot invariant: a reader resolving a locator that points into the unsealed tail must
    // never see a torn or missing record, however the writer seals underneath it. Reading the buffer, the
    // fill and the page id separately would let a seal swap the buffer between two of them, and the reader
    // would then slice the fresh empty one and report a miss for a key that exists.
    [Fact]
    public async Task ConcurrentReadersNeverMissARecordWhileTheWriterSeals()
    {
        var ctx = new Context();
        await using var log = await ctx.Create();
        var published = new ConcurrentQueue<(Locator Loc, int Index)>();
        var readCount = 0L;
        using var stopCts = new CancellationTokenSource();

        var readerTasks = new Task[3];
        for (var r = 0; r < readerTasks.Length; r++)
            readerTasks[r] = Task.Run(async () => {
                while (!stopCts.IsCancellationRequested) {
                    foreach (var (loc, index) in published) {
                        var read = await log.TryReadRecord(loc);
                        read.IsFound.Should().BeTrue($"record {index} at {loc} was published");
                        read.View.Key.ToArray().Should().Equal(Key(index));
                        read.View.Value.ToArray().Should().Equal(Value(index, 30 + (index % 50)));
                        if (log.TryReadRecordCached(loc, out var cached))
                            cached.Key.ToArray().Should().Equal(Key(index));
                        Interlocked.Increment(ref readCount);
                    }
                    await Task.Yield();
                }
            });

        for (var i = 0; i < 3000; i++) {
            var (loc, _) = await log.Append(
                RecordFlags.None, KvasarValueKind.Raw, Key(i), Value(i, 30 + (i % 50)), false);
            published.Enqueue((loc, i));
            if (i % 97 == 0)
                await log.SealTail();
            if (i % 401 == 0)
                await log.Flush();
            // Bounded so the readers keep a short, hot working set instead of an O(n^2) walk.
            if (published.Count > 64)
                published.TryDequeue(out _);
        }

        await stopCts.CancelAsync();
        await Task.WhenAll(readerTasks);
        Interlocked.Read(ref readCount).Should().BeGreaterThan(0, "the readers must have raced the writer");
    }

    // Private methods

    private static async Task<List<(Locator Loc, RecordView View, int RecordLength)>> ScanAll(
        DataLog log, int slot, long fromOffset, long toOffset)
    {
        var result = new List<(Locator Loc, RecordView View, int RecordLength)>();
        await foreach (var item in log.ScanFrom(slot, fromOffset, toOffset))
            result.Add(item);
        return result;
    }

    private static byte[] Key(int i)
    {
        var s = $"key-{i:D6}";
        var bytes = new byte[s.Length];
        for (var j = 0; j < s.Length; j++)
            bytes[j] = (byte)s[j];
        return bytes;
    }

    private static byte[] Value(int i, int length)
    {
        var value = new byte[length];
        new Random(i + 12345).NextBytes(value);
        return value;
    }

    private static bool SameBackingArray(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b)
    {
        if (!MemoryMarshal.TryGetArray(a, out var sa) || !MemoryMarshal.TryGetArray(b, out var sb))
            return false;
        return ReferenceEquals(sa.Array, sb.Array);
    }

    // Nested types

    private sealed class Context
    {
        private uint _nextCacheId;

        public DataLogTestFile[] Files { get; } = [new DataLogTestFile(), new DataLogTestFile()];
        public IStorageFile[] StorageFiles { get; }
        public IPageCipherFactory CipherFactory { get; } = new FakePageCipherFactory(Overhead);
        public PageCache Cache { get; } = new(1 << 20);

        public Context()
            => StorageFiles = [Files[0], Files[1]];

        public uint MintCacheId() => ++_nextCacheId;

        public ValueTask<DataLog> Create(int maxInlineValueBytes = 0)
            => DataLog.Create(
                StorageFiles, PageSize, CipherFactory, FormatVer, Cache, maxInlineValueBytes, MintCacheId);

        public ValueTask<DataLog> Open(int activeSlot, long activeCommitLength, int maxInlineValueBytes = 0)
            => DataLog.Open(
                StorageFiles, activeSlot, activeCommitLength, PageSize, CipherFactory, FormatVer, Cache,
                maxInlineValueBytes, MintCacheId);
    }

    // A private in-memory IStorageFile, named to avoid colliding with the shared storage fakes. Reads and
    // writes are locked because the concurrency test drives it from several threads at once.
    private sealed class DataLogTestFile : IStorageFile
    {
        private readonly Lock _lock = new();
        private byte[] _bytes = [];
        private int _length;

        public long Length {
            get { lock (_lock) return _length; }
        }
        public int FlushToDiskCount { get; private set; }

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock) {
                if (offset >= _length)
                    return new ValueTask<int>(0);

                var count = (int)Math.Min(buffer.Length, _length - offset);
                _bytes.AsSpan((int)offset, count).CopyTo(buffer.Span);
                return new ValueTask<int>(count);
            }
        }

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            lock (_lock) {
                var end = (int)offset + buffer.Length;
                Grow(end);
                buffer.Span.CopyTo(_bytes.AsSpan((int)offset));
                _length = Math.Max(_length, end);
            }
            return default;
        }

        public ValueTask FlushToDisk()
        {
            lock (_lock)
                FlushToDiskCount++;
            return default;
        }

        public ValueTask Truncate(long length)
        {
            lock (_lock) {
                Grow((int)length);
                if (length < _length)
                    _bytes.AsSpan((int)length, _length - (int)length).Clear();
                _length = (int)length;
            }
            return default;
        }

        public ValueTask DisposeAsync() => default;

        private void Grow(int minLength)
        {
            if (_bytes.Length >= minLength)
                return;

            var bytes = new byte[Math.Max(minLength, Math.Max(1024, _bytes.Length * 2))];
            _bytes.AsSpan(0, _length).CopyTo(bytes);
            _bytes = bytes;
        }
    }
}
