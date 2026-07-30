using System.Buffers.Binary;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Index;

public sealed class IndexLogTests
{
    private const uint FormatVer = 7;
    // IndexEntry is a fixed 32 bytes, so this is a const and can reach [InlineData].
    private const int EntriesPerMacBlock = IndexMac.BlockSize / 32;

    private static readonly byte[] MacKey = Enumerable.Range(0, KvasarConstants.IndexMacKeySize).Select(i => (byte)(i * 7 + 1)).ToArray();

    [Fact]
    public void EntrySizeIsTheThirtyTwoBytesTheMacBlockMathAssumes()
        => IndexLog.EntrySize.Should().Be(32);

    [Fact]
    public async Task CheckpointRoundTrip()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        var entries = new[] {
            Entry(0x1111_0000_0000_0001, 1, 100, 10),
            Entry(0x2222_0000_0000_0002, 1, 200, 20),
            Entry(0x3333_0000_0000_0003, 2, 300, 30),
        };

        var length = await log.WriteCheckpoint(entries, 4096);
        length.Should().Be(IndexLog.HeaderSize + (3 * IndexLog.EntrySize));
        log.Length.Should().Be(length);

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.DataCommitLength.Should().Be(4096);
        snapshot.Value.Entries.Should().BeEquivalentTo(entries, o => o.WithoutStrictOrdering());
    }

    [Fact]
    public async Task EmptyCheckpointRoundTrips()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);

        await log.WriteCheckpoint(Array.Empty<IndexEntry>(), 12345);

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.Entries.Should().BeEmpty();
        snapshot.Value.DataCommitLength.Should().Be(12345);
    }

    [Fact]
    public async Task DeltasResolveLastWriterWins()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(10, 1, 100, 10) }, 100);

        // Later delta for the same key overrides the checkpoint entry.
        await log.AppendDelta(Entry(10, 1, 500, 50));
        // A brand-new key via delta.
        await log.AppendDelta(Entry(20, 1, 600, 60));
        // Two deltas for the same new key — the later one wins.
        await log.AppendDelta(Entry(30, 1, 700, 70));
        await log.AppendDelta(Entry(30, 2, 800, 80));
        await log.Flush();

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();

        var map = snapshot!.Value.Entries.ToDictionary(e => e.KeyHash);
        map.Should().HaveCount(3);
        map[10].Locator.Offset.Should().Be(500);
        map[10].Length.Should().Be(50);
        map[20].Locator.Offset.Should().Be(600);
        map[30].Locator.FileId.Should().Be(2u);
        map[30].Locator.Offset.Should().Be(800);
    }

    [Fact]
    public async Task DeltasPreserveSameHashFanOut()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] {
            Entry(10, 1, 100, 10, keyId: 1001),
            Entry(10, 1, 200, 20, keyId: 1002),
        }, 100);
        await log.AppendDelta(Entry(10, 1, 300, 30, keyId: 1001));
        await log.AppendDelta(Entry(10, 1, 400, 40, isTombstone: true, keyId: 1002));

        var snapshot = await log.CommitAndRead();

        snapshot.Should().NotBeNull();
        snapshot!.Value.Entries.Should().HaveCount(2);
        snapshot.Value.Entries.Single(e => e.KeyId == 1001).Locator.Offset.Should().Be(300);
        snapshot.Value.Entries.Single(e => e.KeyId == 1002).IsTombstone.Should().BeTrue();
    }

    [Fact]
    public async Task TombstoneDeltaSurvivesIntoEntries()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(10, 1, 100, 10) }, 0);
        await log.AppendDelta(Entry(10, 1, 100, 10, isTombstone: true));

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();

        snapshot!.Value.Entries.Should().HaveCount(1);
        snapshot.Value.Entries[0].KeyHash.Should().Be(10);
        snapshot.Value.Entries[0].IsTombstone.Should().BeTrue();
    }

    [Fact]
    public async Task CheckpointDropsTombstonesAndResetsDeltaTail()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        for (var i = 0; i < 8; i++)
            await log.AppendDelta(Entry(100 + (ulong)i, 1, 1000 + i, 10));
        await log.Flush();
        (await log.CommitAndRead())!.Value.Entries.Should().HaveCount(9);

        var live = new[] {
            Entry(1, 1, 100, 10),
            Entry(2, 1, 200, 20, isTombstone: true),
            Entry(3, 1, 300, 30),
        };
        var length = await log.WriteCheckpoint(live, 8192);

        length.Should().Be(IndexLog.HeaderSize + (2 * IndexLog.EntrySize));
        file.Length.Should().Be(length);

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.DataCommitLength.Should().Be(8192);
        snapshot.Value.Entries.Select(e => e.KeyHash).Should().BeEquivalentTo([1UL, 3UL]);
    }

    [Fact]
    public async Task TornTrailingDeltaIsDropped()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        await log.AppendDelta(Entry(2, 1, 200, 20));
        await log.Flush();

        // A partial trailing entry, exactly what a crash leaves behind in an un-flushed delta tail.
        await file.Write(log.Length, new byte[IndexLog.EntrySize - 1]);

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.Entries.Select(e => e.KeyHash).Should().BeEquivalentTo([1UL, 2UL]);
    }

    [Fact]
    public async Task ValidLengthBoundsTheRead_I3Regression()
    {
        // I3: v1 read the delta tail to raw EOF, so a torn delta misaligned every later delta and admitted
        // fabricated locators. validLength is authoritative here — bytes past it are never read.
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);

        await log.AppendDelta(Entry(2, 1, 200, 20));
        await log.AppendDelta(Entry(3, 1, 300, 30));
        await log.Flush();
        var afterFirstDeltas = log.Length;
        await log.WriteCommitMac(1);

        await log.AppendDelta(Entry(4, 1, 400, 40));
        await log.AppendDelta(Entry(5, 1, 500, 50));
        await log.Flush();
        await log.WriteCommitMac(0);

        (await log.Read(afterFirstDeltas, 1))!.Value.Entries.Select(e => e.KeyHash)
            .Should().BeEquivalentTo([1UL, 2UL, 3UL]);
        (await log.Read(log.Length, 0))!.Value.Entries.Select(e => e.KeyHash)
            .Should().BeEquivalentTo([1UL, 2UL, 3UL, 4UL, 5UL]);
    }

    [Fact]
    public async Task GarbagePastValidLengthIsIgnored()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        await log.AppendDelta(Entry(2, 1, 200, 20));
        await log.Flush();
        var validLength = log.Length;

        var garbage = new byte[13 * IndexLog.EntrySize];
        new Random(42).NextBytes(garbage);
        await file.Write(validLength, garbage);

        var snapshot = await log.CommitAndRead(validLength);
        snapshot.Should().NotBeNull();
        snapshot!.Value.Entries.Select(e => e.KeyHash).Should().BeEquivalentTo([1UL, 2UL]);
        (await log.Read(file.Length, 0)).Should().BeNull(
            "bytes beyond the authenticated committed prefix must never be parsed");
    }

    [Fact]
    public async Task AbsentFileReturnsNull()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);

        (await log.CommitAndRead()).Should().BeNull();
        log.Length.Should().Be(0);
    }

    [Fact]
    public async Task ShortFileReturnsNull()
    {
        var file = new IndexLogTestFile();
        await file.Write(0, new byte[8]);
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);

        (await log.CommitAndRead()).Should().BeNull();
    }

    [Fact]
    public async Task BadMagicReturnsNull()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        await file.Write(0, "XIDX"u8.ToArray());

        (await log.CommitAndRead()).Should().BeNull();
    }

    [Fact]
    public async Task WrongFormatVerReturnsNull()
    {
        var file = new IndexLogTestFile();
        await using (var log = await IndexLog.Open(file, FormatVer, MacKey))
            await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);

        await using var other = await IndexLog.Open(file, FormatVer + 1, MacKey);
        (await other.CommitAndRead()).Should().BeNull();
    }

    [Fact]
    public async Task WrongEntrySizeReturnsNull()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        await PatchUInt32(file, 8, (uint)IndexLog.EntrySize + 1);

        (await log.CommitAndRead()).Should().BeNull();
    }

    [Fact]
    public async Task WrongLayoutVersionReturnsNull()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        await PatchUInt32(file, 12, 1);

        (await log.CommitAndRead()).Should().BeNull();
    }

    [Fact]
    public async Task NegativeDataCommitLengthReturnsNull()
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 0);
        await PatchInt64(file, 24, -1);

        (await log.CommitAndRead()).Should().BeNull();
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(1L << 62)]
    [InlineData(4L)] // one past the file's capacity of 3
    public async Task HostileCheckpointCountReturnsNull(long checkpointCount)
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(
            new[] { Entry(1, 1, 100, 10), Entry(2, 1, 200, 20), Entry(3, 1, 300, 30) }, 0);
        await PatchInt64(file, 16, checkpointCount);

        (await log.CommitAndRead()).Should().BeNull();
        (await log.CommitAndRead(file.Length)).Should().BeNull();
    }

    [Fact]
    public async Task DeltasAreBatchedIntoFewWrites()
    {
        const int deltaCount = 1000;
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(Array.Empty<IndexEntry>(), 0);

        file.WriteCount = 0;
        for (var i = 0; i < deltaCount; i++)
            await log.AppendDelta(Entry((ulong)i, 1, i * 100, 10));
        await log.Flush();

        file.WriteCount.Should().BeLessThan(deltaCount / 10);
        file.WriteCount.Should().BeGreaterThan(0);

        var snapshot = await log.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.Entries.Should().HaveCount(deltaCount);
    }

    [Fact]
    public async Task ReopenAppendsAfterTheTornTail()
    {
        var file = new IndexLogTestFile();
        await using (var log = await IndexLog.Open(file, FormatVer, MacKey)) {
            await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 555);
            await log.AppendDelta(Entry(2, 1, 200, 20));
            await log.WriteCommitMac(0);
        }
        // Torn tail: a partial delta left by a crash. The reopened log overwrites it rather than appending
        // past it, so no later delta is ever misaligned by it.
        await file.Write(file.Length, new byte[IndexLog.EntrySize - 3]);

        await using var reopened = await IndexLog.Open(file, FormatVer, MacKey);
        reopened.Length.Should().Be(IndexLog.HeaderSize + (2 * IndexLog.EntrySize));
        (await reopened.Read(reopened.Length, 0)).Should().NotBeNull();
        await reopened.AppendDelta(Entry(3, 1, 300, 30));

        var snapshot = await reopened.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.DataCommitLength.Should().Be(555);
        snapshot.Value.Entries.Select(e => e.KeyHash).Should().BeEquivalentTo([1UL, 2UL, 3UL]);
    }

    [Fact]
    public async Task IndexSpanningManyMacBlocksRoundTrips()
    {
        const int checkpointCount = 3 * EntriesPerMacBlock;
        const int deltaCount = (2 * EntriesPerMacBlock) + 7;
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(
            Enumerable.Range(0, checkpointCount).Select(i => Entry((ulong)i, 1, i * 64, 64)).ToArray(),
            1 << 20);
        for (var i = 0; i < deltaCount; i++)
            await log.AppendDelta(Entry((ulong)(checkpointCount + i), 2, i * 64, 32));

        var snapshot = await log.CommitAndRead();

        snapshot.Should().NotBeNull();
        snapshot!.Value.Entries.Should().HaveCount(checkpointCount + deltaCount);
        snapshot.Value.DataCommitLength.Should().Be(1 << 20);
    }

    [Fact]
    public async Task EveryCommitAcrossAMacBlockBoundaryAuthenticates()
    {
        // The delta that completes a MAC block folds it into the chain; the next one opens a fresh block.
        // A commit landing on either side of that seam must still authenticate at its own length.
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(Array.Empty<IndexEntry>(), 0);

        var generation = 0UL;
        for (var i = 0; i < EntriesPerMacBlock + 4; i++) {
            await log.AppendDelta(Entry((ulong)i, 1, i * 64, 32));
            var committedLength = log.Length;
            await log.WriteCommitMac(++generation);
            var snapshot = await log.Read(committedLength, generation);
            snapshot.Should().NotBeNull($"commit after delta {i} must authenticate");
            snapshot!.Value.Entries.Should().HaveCount(i + 1);
        }
    }

    [Fact]
    public async Task ReopeningMidMacBlockContinuesTheChain()
    {
        // A reopened log rebuilds its chain from the file, so it must land on the same folded-block count
        // and the same partially filled trailing block the writer had.
        const int firstCount = EntriesPerMacBlock + 5;
        var file = new IndexLogTestFile();
        await using (var log = await IndexLog.Open(file, FormatVer, MacKey)) {
            await log.WriteCheckpoint(new[] { Entry(1, 1, 100, 10) }, 777);
            for (var i = 0; i < firstCount; i++)
                await log.AppendDelta(Entry((ulong)(100 + i), 1, i * 64, 32));
            await log.WriteCommitMac(0);
        }

        await using var reopened = await IndexLog.Open(file, FormatVer, MacKey);
        (await reopened.Read(reopened.Length, 0)).Should().NotBeNull();
        for (var i = 0; i < EntriesPerMacBlock; i++)
            await reopened.AppendDelta(Entry((ulong)(1_000 + i), 2, i * 64, 32));

        var snapshot = await reopened.CommitAndRead();
        snapshot.Should().NotBeNull();
        snapshot!.Value.DataCommitLength.Should().Be(777);
        snapshot.Value.Entries.Should().HaveCount(1 + firstCount + EntriesPerMacBlock);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(EntriesPerMacBlock - 1)]
    [InlineData(EntriesPerMacBlock)]
    [InlineData(EntriesPerMacBlock + 1)]
    [InlineData(2 * EntriesPerMacBlock)]
    public async Task TamperingInsideAnyMacBlockIsDetected(int entryIndex)
    {
        var file = new IndexLogTestFile();
        await using var log = await IndexLog.Open(file, FormatVer, MacKey);
        await log.WriteCheckpoint(
            Enumerable.Range(0, (2 * EntriesPerMacBlock) + 10)
                .Select(i => Entry((ulong)i, 1, i * 64, 64))
                .ToArray(),
            4096);
        var committedLength = log.Length;
        await log.WriteCommitMac(0);
        (await log.Read(committedLength, 0)).Should().NotBeNull();

        // Retarget one checkpoint entry's locator: structurally valid, so only the MAC can catch it.
        await file.Write(
            IndexLog.HeaderSize + (entryIndex * IndexLog.EntrySize) + 8,
            BitConverter.GetBytes(new Locator(1, 0x4242).Packed));

        (await log.Read(committedLength, 0)).Should().BeNull();
    }

    // Private methods

    private static IndexEntry Entry(
        ulong keyHash, uint fileId, long offset, uint length,
        bool isTombstone = false, ulong keyId = 0)
        => new() {
            KeyHash = keyHash, PackedLocator = new Locator(fileId, offset).Packed, KeyId = keyId, Length = length,
            Flags = isTombstone ? (byte)RecordFlags.Tombstone : (byte)RecordFlags.None,
        };

    private static ValueTask PatchUInt32(IndexLogTestFile file, long offset, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return file.Write(offset, bytes);
    }

    private static ValueTask PatchInt64(IndexLogTestFile file, long offset, long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return file.Write(offset, bytes);
    }

    // Nested types

    // A minimal in-RAM IStorageFile that counts writes; named to avoid colliding with the fakes other
    // test files define.
    private sealed class IndexLogTestFile : IStorageFile
    {
        private byte[] _bytes = [];

        public long Length { get; private set; }
        public int WriteCount { get; set; }

        public ValueTask DisposeAsync()
            => default;

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset >= Length)
                return new ValueTask<int>(0);

            var count = (int)Math.Min(buffer.Length, Length - offset);
            _bytes.AsSpan((int)offset, count).CopyTo(buffer.Span);
            return new ValueTask<int>(count);
        }

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            WriteCount++;
            var end = (int)(offset + buffer.Length);
            if (_bytes.Length < end)
                Array.Resize(ref _bytes, Math.Max(end, _bytes.Length * 2));
            buffer.Span.CopyTo(_bytes.AsSpan((int)offset));
            Length = Math.Max(Length, end);
            return default;
        }

        public ValueTask FlushToDisk()
            => throw new InvalidOperationException("The index log must never be flushed.");

        public ValueTask Truncate(long length)
        {
            if (_bytes.Length < length)
                Array.Resize(ref _bytes, (int)length);
            if (length < Length)
                Array.Clear(_bytes, (int)length, (int)(Length - length));
            Length = length;
            return default;
        }
    }
}
