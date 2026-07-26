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
        var file = new SuperblockTestFile();
        var state = new SuperblockState(7, 1, 123456, 0, 654321, 1000, 250);
        await superblock.Write(file, state);

        var read = await superblock.Read(file);
        read.HasValue.Should().BeTrue();
        read!.Value.Should().Be(state);
    }

    [Fact]
    public async Task RoundTripsExtremeValues()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        var state = new SuperblockState(ulong.MaxValue, 1, long.MaxValue, 1, long.MaxValue, long.MinValue, -1);
        await superblock.Write(file, state);

        (await superblock.Read(file))!.Value.Should().Be(state);
    }

    [Fact]
    public async Task AlternatesSlotsByGenerationParity()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        foreach (var generation in new ulong[] { 1, 2, 3, 4 }) {
            var before = file.Snapshot();
            await superblock.Write(file, NewState(generation));
            ChangedSlots(before, file.Snapshot()).Should().Equal((int)(generation % 2));
            (await superblock.Read(file))!.Value.Generation.Should().Be(generation);
        }
        file.Length.Should().Be(Superblock.FileSize);
    }

    [Fact]
    public async Task HighestGenerationWinsWhenNewestIsInSlot0()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(5)); // slot 1
        await superblock.Write(file, NewState(6)); // slot 0

        (await superblock.Read(file))!.Value.Generation.Should().Be(6ul);
    }

    [Fact]
    public async Task HighestGenerationWinsWhenNewestIsInSlot1()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(6)); // slot 0
        await superblock.Write(file, NewState(7)); // slot 1

        (await superblock.Read(file))!.Value.Generation.Should().Be(7ul);
    }

    [Fact]
    public async Task ReadAllReturnsBothSlotsNewestFirst()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(8)); // slot 0
        await superblock.Write(file, NewState(9)); // slot 1

        var states = await superblock.ReadAll(file);
        states.Select(x => x.Generation).Should().Equal(9ul, 8ul);
    }

    [Fact]
    public async Task ReadAllReturnsTheOnlyValidSlot()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(4)); // slot 0 only

        var states = await superblock.ReadAll(file);
        states.Select(x => x.Generation).Should().Equal(4ul);
    }

    [Fact]
    public async Task AnyCorruptByteOfNewestSlotFallsBackToOlder()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(1)); // slot 1
        await superblock.Write(file, NewState(2)); // slot 0, newest
        var pristine = file.Snapshot();

        for (var i = 0; i < Superblock.SlotSize; i++) {
            file.Restore(pristine);
            file.FlipByte(i);
            var state = await superblock.Read(file);
            state.HasValue.Should().BeTrue($"slot 0 byte {i} is corrupt, so generation 1 must still be readable");
            state!.Value.Generation.Should().Be(1ul, $"slot 0 byte {i} is corrupt");
        }
    }

    [Fact]
    public async Task CorruptBothSlotsReturnsNull()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(1));
        await superblock.Write(file, NewState(2));
        file.FlipByte(Superblock.SlotSize - 1);     // slot 0 tag
        file.FlipByte(Superblock.SlotSize + 8);     // slot 1 nonce

        (await superblock.Read(file)).Should().BeNull();
        (await superblock.ReadAll(file)).Should().BeEmpty();
    }

    [Fact]
    public async Task TornSlotWriteIsAlwaysRejected()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(1)); // slot 1
        await superblock.Write(file, NewState(2)); // slot 0
        await superblock.Write(file, NewState(3)); // slot 1, newest
        var pristine = file.Snapshot();

        var scratch = new SuperblockTestFile();
        await superblock.Write(scratch, NewState(4)); // slot 0
        var slot4 = scratch.Snapshot();

        for (var n = 0; n <= Superblock.SlotSize; n++) {
            file.Restore(pristine);
            await file.Write(0, slot4.AsMemory(0, n));
            var state = await superblock.Read(file);
            state.HasValue.Should().BeTrue($"{n} bytes of generation 4 landed");
            var expected = n == Superblock.SlotSize ? 4ul : 3ul;
            state!.Value.Generation.Should().Be(expected, $"{n} bytes of generation 4 landed");
        }
    }

    [Fact]
    public async Task WrongKeyInvalidatesBothSlots()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(1));
        await superblock.Write(file, NewState(2));

        var wrongKey = new Superblock(OtherKey, FormatVer);
        (await wrongKey.Read(file)).Should().BeNull();
        (await wrongKey.ReadAll(file)).Should().BeEmpty();
    }

    [Fact]
    public async Task WrongFormatVerInvalidatesBothSlots()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(1));
        await superblock.Write(file, NewState(2));

        var wrongFormatVer = new Superblock(MasterKey, FormatVer + 1);
        (await wrongFormatVer.Read(file)).Should().BeNull();
        (await wrongFormatVer.ReadAll(file)).Should().BeEmpty();
    }

    [Fact]
    public async Task SlotMovedToTheOtherPositionIsRejected()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        await superblock.Write(file, NewState(1)); // slot 1
        var slot1 = file.Snapshot().AsMemory(Superblock.SlotSize);
        await file.Write(0, slot1); // copy it over slot 0 as well

        var states = await superblock.ReadAll(file);
        states.Select(x => x.Generation).Should().Equal(1ul); // the slot-0 copy has the wrong parity
    }

    [Fact]
    public async Task EmptyOrShortFileReturnsNull()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        (await superblock.Read(file)).Should().BeNull();
        (await superblock.ReadAll(file)).Should().BeEmpty();

        await file.Write(0, new byte[Superblock.SlotSize - 1]);
        (await superblock.Read(file)).Should().BeNull();

        await file.Write(0, RandomBytes(Superblock.FileSize - 1));
        (await superblock.Read(file)).Should().BeNull();

        await file.Write(0, RandomBytes(Superblock.FileSize));
        (await superblock.Read(file)).Should().BeNull();
    }

    [Fact]
    public async Task NonceIsFreshOnEveryWrite()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();
        var state = NewState(2);
        await superblock.Write(file, state);
        var first = file.Snapshot();
        await superblock.Write(file, state);
        var second = file.Snapshot();

        second.Should().NotEqual(first);
        (await superblock.Read(file))!.Value.Should().Be(state);
    }

    [Fact]
    public async Task WriteRejectsOutOfRangeState()
    {
        var superblock = NewSuperblock();
        var file = new SuperblockTestFile();

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
            var offset = slot * Superblock.SlotSize;
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
