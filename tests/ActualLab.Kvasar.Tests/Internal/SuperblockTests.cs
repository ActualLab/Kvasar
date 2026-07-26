using System.Security.Cryptography;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Internal;

public class SuperblockTests
{
    private const uint FormatVer = 3;
    private static readonly byte[] MasterKey = MakeKey(0x11);
    private static readonly byte[] OtherKey = MakeKey(0x22);

    [Fact]
    public async Task RoundTrips()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        var state = new SuperblockState(7, 1, 123456, 0, 654321, 1000, 250);
        await superblock.Write(file, state);

        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.Ok);
        result.Newest.Should().Be(state);
        file.Length.Should().Be(Superblock.FileSize);
    }

    [Fact]
    public async Task RoundTripsExtremeValues()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        var state = new SuperblockState(ulong.MaxValue, 1, long.MaxValue, 1, long.MaxValue, long.MinValue, -1);
        await superblock.Write(file, state);

        (await superblock.Read(file)).Newest.Should().Be(state);
    }

    [Fact]
    public async Task InitializeWritesHeaderAndTwoEmptySlots()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);

        file.Length.Should().Be(1088);
        Superblock.FileSize.Should().Be(Superblock.HeaderSize + (2 * Superblock.SlotSize));
        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.NoValidSlot);
        result.States.Should().BeEmpty();
    }

    [Fact]
    public async Task WriteNeverTouchesTheHeader()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        var header = file.Snapshot()[..Superblock.HeaderSize];

        foreach (var generation in new ulong[] { 1, 2, 3, 4 })
            await superblock.Write(file, NewState(generation));

        file.Snapshot()[..Superblock.HeaderSize].Should().Equal(header);
    }

    [Fact]
    public async Task AlternatesSlotsByGenerationParity()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        foreach (var generation in new ulong[] { 1, 2, 3, 4 }) {
            var before = file.Snapshot();
            await superblock.Write(file, NewState(generation));
            ChangedSlots(before, file.Snapshot()).Should().Equal((int)(generation % 2));
            (await superblock.Read(file)).Newest!.Value.Generation.Should().Be(generation);
        }
    }

    [Fact]
    public async Task HighestGenerationWinsWhenNewestIsInSlot0()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(5)); // slot 1
        await superblock.Write(file, NewState(6)); // slot 0

        (await superblock.Read(file)).Newest!.Value.Generation.Should().Be(6ul);
    }

    [Fact]
    public async Task HighestGenerationWinsWhenNewestIsInSlot1()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(6)); // slot 0
        await superblock.Write(file, NewState(7)); // slot 1

        (await superblock.Read(file)).Newest!.Value.Generation.Should().Be(7ul);
    }

    [Fact]
    public async Task ReadReturnsBothSlotsNewestFirst()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(8)); // slot 0
        await superblock.Write(file, NewState(9)); // slot 1

        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.Ok);
        result.States.Select(x => x.Generation).Should().Equal(9ul, 8ul);
    }

    [Fact]
    public async Task ReadReturnsTheOnlyValidSlot()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(4)); // slot 0 only

        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.Ok);
        result.States.Select(x => x.Generation).Should().Equal(4ul);
    }

    [Fact]
    public async Task AnyCorruptByteOfNewestSlotFallsBackToOlder()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1)); // slot 1
        await superblock.Write(file, NewState(2)); // slot 0, newest
        var pristine = file.Snapshot();

        for (var i = 0; i < Superblock.SlotSize; i++) {
            file.Restore(pristine);
            file.FlipByte(Superblock.HeaderSize + i);
            var result = await superblock.Read(file);
            result.Status.Should().Be(SuperblockStatus.Ok, $"slot 0 byte {i} is corrupt");
            result.Newest!.Value.Generation.Should().Be(1ul, $"slot 0 byte {i} is corrupt");
        }
    }

    [Fact]
    public async Task CorruptingBothSlotsIsNoValidSlotNotWrongKey()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));
        await superblock.Write(file, NewState(2));
        file.FlipByte(Superblock.HeaderSize + Superblock.SlotSize - 1);  // slot 0 tag
        file.FlipByte(Superblock.HeaderSize + Superblock.SlotSize);      // slot 1 nonce

        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.NoValidSlot);
        result.States.Should().BeEmpty();
    }

    [Fact]
    public async Task TornSlotWriteIsAlwaysRejected()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1)); // slot 1
        await superblock.Write(file, NewState(2)); // slot 0
        await superblock.Write(file, NewState(3)); // slot 1, newest
        var pristine = file.Snapshot();

        var scratch = await NewFile(superblock);
        await superblock.Write(scratch, NewState(4)); // slot 0
        var slot4 = scratch.Snapshot().AsMemory(Superblock.HeaderSize, Superblock.SlotSize);

        for (var n = 0; n <= Superblock.SlotSize; n++) {
            file.Restore(pristine);
            await file.Write(Superblock.HeaderSize, slot4[..n]);
            var result = await superblock.Read(file);
            var expected = n == Superblock.SlotSize ? 4ul : 3ul;
            result.Status.Should().Be(SuperblockStatus.Ok, $"{n} bytes of generation 4 landed");
            result.Newest!.Value.Generation.Should().Be(expected, $"{n} bytes of generation 4 landed");
        }
    }

    [Fact]
    public async Task WrongKeyIsWrongKeyNotNoValidSlot()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));
        await superblock.Write(file, NewState(2));

        var result = await new Superblock(OtherKey, FormatVer).Read(file);
        result.Status.Should().Be(SuperblockStatus.WrongKey);
        result.Status.Should().NotBe(SuperblockStatus.NoValidSlot); // wiping here would destroy an intact store
        result.States.Should().BeEmpty();
        result.Newest.Should().BeNull();
    }

    [Fact]
    public async Task WrongKeyIsWrongKeyEvenWithNoSlotsWritten()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);

        (await new Superblock(OtherKey, FormatVer).Read(file)).Status.Should().Be(SuperblockStatus.WrongKey);
    }

    [Fact]
    public async Task CorruptKcvIsWrongKey()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));
        var pristine = file.Snapshot();

        // Bytes 8..36 are the KCV nonce and tag; flipping any of them must read as a wrong key.
        for (var i = 8; i < 36; i++) {
            file.Restore(pristine);
            file.FlipByte(i);
            (await superblock.Read(file)).Status.Should().Be(SuperblockStatus.WrongKey, $"header byte {i}");
        }
    }

    [Fact]
    public async Task WrongFormatVerIsFormatMismatch()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));

        var result = await new Superblock(MasterKey, FormatVer + 1).Read(file);
        result.Status.Should().Be(SuperblockStatus.FormatMismatch);
        result.States.Should().BeEmpty();
    }

    [Fact]
    public async Task WrongFormatVerBeatsWrongKey()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));

        // A Version bump usually comes with a rekey; it must still wipe rather than throw.
        var result = await new Superblock(OtherKey, FormatVer + 1).Read(file);
        result.Status.Should().Be(SuperblockStatus.FormatMismatch);
    }

    [Fact]
    public async Task BadMagicIsFormatMismatch()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));
        var pristine = file.Snapshot();

        for (var i = 0; i < 4; i++) {
            file.Restore(pristine);
            file.FlipByte(i);
            (await superblock.Read(file)).Status.Should().Be(SuperblockStatus.FormatMismatch, $"magic byte {i}");
        }
    }

    [Fact]
    public async Task EmptyFileIsMissing()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();

        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.Missing);
        result.States.Should().BeEmpty();
    }

    [Fact]
    public async Task ShortOrForeignFileIsFormatMismatch()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();

        await file.Write(0, RandomBytes(Superblock.HeaderSize - 1));
        (await superblock.Read(file)).Status.Should().Be(SuperblockStatus.FormatMismatch);

        await file.Write(0, RandomBytes(Superblock.FileSize));
        (await superblock.Read(file)).Status.Should().Be(SuperblockStatus.FormatMismatch);
    }

    [Fact]
    public async Task TruncatedSlotsAreNoValidSlot()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1));
        await superblock.Write(file, NewState(2));

        await file.Truncate(Superblock.FileSize - 1);
        var result = await superblock.Read(file);
        result.Status.Should().Be(SuperblockStatus.Ok);
        result.States.Select(x => x.Generation).Should().Equal(2ul); // slot 1 is gone, slot 0 survives

        // The header still authenticates, so losing both slots is corruption — not a wrong key.
        await file.Truncate(Superblock.HeaderSize);
        (await superblock.Read(file)).Status.Should().Be(SuperblockStatus.NoValidSlot);
    }

    [Fact]
    public async Task SlotMovedToTheOtherPositionIsRejected()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        await superblock.Write(file, NewState(1)); // slot 1
        var slot1 = file.Snapshot().AsMemory(Superblock.HeaderSize + Superblock.SlotSize, Superblock.SlotSize);
        await file.Write(Superblock.HeaderSize, slot1); // copy it over slot 0 as well

        var result = await superblock.Read(file);
        result.States.Select(x => x.Generation).Should().Equal(1ul); // the slot-0 copy has the wrong parity
    }

    [Fact]
    public async Task NonceIsFreshOnEveryWrite()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);
        var state = NewState(2);
        await superblock.Write(file, state);
        var first = file.Snapshot();
        await superblock.Write(file, state);
        var second = file.Snapshot();

        second.Should().NotEqual(first);
        (await superblock.Read(file)).Newest.Should().Be(state);
    }

    [Fact]
    public async Task WriteRejectsOutOfRangeState()
    {
        var superblock = NewSuperblock();
        var file = await NewFile(superblock);

        var badDataSlot = () => WriteState(new SuperblockState(1, 2, 0, 0, 0, 0, 0));
        await badDataSlot.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var badIndexSlot = () => WriteState(new SuperblockState(1, 0, 0, 9, 0, 0, 0));
        await badIndexSlot.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var badLength = () => WriteState(new SuperblockState(1, 0, -1, 0, 0, 0, 0));
        await badLength.Should().ThrowAsync<ArgumentOutOfRangeException>();
        return;

        async Task WriteState(SuperblockState state)
            => await superblock.Write(file, state);
    }

    // Private methods

    private static Superblock NewSuperblock()
        => new(MasterKey, FormatVer);

    private static async Task<SuperblockTestFile> NewFile(Superblock superblock)
    {
        var file = new SuperblockTestFile();
        await superblock.Initialize(file);
        return file;
    }

    private static SuperblockState NewState(ulong generation)
        => new(generation, (byte)(generation % 2), (long)generation * 4096, 0, (long)generation * 24, 100, 20);

    private static byte[] MakeKey(byte seed)
    {
        var key = new byte[KvasarConstants.MasterKeySize];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(seed + i);
        return key;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static List<int> ChangedSlots(byte[] before, byte[] after)
    {
        var padded = new byte[after.Length];
        before.AsSpan(0, Math.Min(before.Length, padded.Length)).CopyTo(padded);
        var result = new List<int>();
        for (var slot = 0; slot < Superblock.SlotCount; slot++) {
            var offset = Superblock.HeaderSize + (slot * Superblock.SlotSize);
            if (offset + Superblock.SlotSize > padded.Length)
                continue;
            if (!padded.AsSpan(offset, Superblock.SlotSize).SequenceEqual(after.AsSpan(offset, Superblock.SlotSize)))
                result.Add(slot);
        }
        return result;
    }

    // Nested types

    // A minimal in-memory IStorageFile: no volatile/stable split, since the superblock is never
    // flushed and every test here drives tearing explicitly through Write/FlipByte/Restore.
    private sealed class SuperblockTestFile : IStorageFile
    {
        private byte[] _bytes = [];

        public long Length => _bytes.Length;

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset >= _bytes.Length)
                return ValueTask.FromResult(0);

            var count = (int)Math.Min(buffer.Length, _bytes.Length - offset);
            _bytes.AsSpan((int)offset, count).CopyTo(buffer.Span);
            return ValueTask.FromResult(count);
        }

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            var end = (int)offset + buffer.Length;
            if (end > _bytes.Length)
                Array.Resize(ref _bytes, end);
            buffer.Span.CopyTo(_bytes.AsSpan((int)offset));
            return default;
        }

        public ValueTask FlushToDisk()
            => default;

        public ValueTask Truncate(long length)
        {
            Array.Resize(ref _bytes, (int)length);
            return default;
        }

        public ValueTask DisposeAsync()
            => default;

        public byte[] Snapshot()
            => _bytes.AsSpan().ToArray();

        public void Restore(byte[] bytes)
            => _bytes = bytes.AsSpan().ToArray();

        public void FlipByte(int index)
            => _bytes[index] ^= 0xFF;
    }
}
