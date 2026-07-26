using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Log;

public class RecordCodecTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(5, 0)]      // empty value, present
    [InlineData(5, 1)]
    [InlineData(5, 200)]
    [InlineData(300, 5)]    // multi-byte key varint
    [InlineData(10, 5000)]  // multi-byte record + key varints
    public void RoundTrips(int keyLen, int valueLen)
    {
        var key = RandomBytes(keyLen, 1);
        var value = RandomBytes(valueLen, 2);
        var buf = new byte[RecordCodec.MaxHeaderSize(keyLen) + valueLen];

        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueKind.Raw, key, value, false);
        RecordCodec.GetRecordLength(keyLen, valueLen, false).Should().Be(written);

        RecordCodec.TryDecode(buf.AsMemory(0, written), out var view, out var totalLen).Should().BeTrue();
        totalLen.Should().Be(written);
        view.IsTombstone.Should().BeFalse();
        view.ValueKind.Should().Be(KvasarValueKind.Raw);
        view.Key.ToArray().Should().Equal(key);
        view.Value.ToArray().Should().Equal(value);
    }

    [Fact]
    public void TombstoneHasNoValue()
    {
        var key = RandomBytes(9, 3);
        var buf = new byte[RecordCodec.MaxHeaderSize(key.Length)];
        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueKind.Raw, key, ReadOnlySpan<byte>.Empty, true);

        RecordCodec.TryDecode(buf.AsMemory(0, written), out var view, out _).Should().BeTrue();
        view.IsTombstone.Should().BeTrue();
        (view.Flags & RecordFlags.Tombstone).Should().Be(RecordFlags.Tombstone);
        view.Key.ToArray().Should().Equal(key);
        view.Value.Length.Should().Be(0);
    }

    [Fact]
    public void SpanDecodeMatchesMemoryDecode()
    {
        var key = RandomBytes(7, 4);
        var value = RandomBytes(33, 5);
        var buf = new byte[RecordCodec.MaxHeaderSize(key.Length) + value.Length];
        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueKind.Raw, key, value, false);

        RecordCodec.TryDecode(buf.AsSpan(0, written), out var view, out var totalLen).Should().BeTrue();
        totalLen.Should().Be(written);
        view.Key.ToArray().Should().Equal(key);
        view.Value.ToArray().Should().Equal(value);
    }

    [Fact]
    public void TruncatedBufferFails()
    {
        var key = RandomBytes(20, 6);
        var value = RandomBytes(400, 7);
        var buf = new byte[RecordCodec.MaxHeaderSize(key.Length) + value.Length];
        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueKind.Raw, key, value, false);

        for (var cut = 0; cut < written; cut++)
            RecordCodec.TryDecode(buf.AsMemory(0, cut), out _, out _).Should().BeFalse($"cut at {cut}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(255)]
    public void UnknownValueKindFails(byte kind)
    {
        var key = RandomBytes(4, 8);
        var value = RandomBytes(16, 9);
        var buf = new byte[RecordCodec.MaxHeaderSize(key.Length) + value.Length];
        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueKind.Raw, key, value, false);

        // The body is < 128 bytes, so recordLen is a single varint byte and valueKind lands at buf[2].
        buf[2].Should().Be((byte)KvasarValueKind.Raw);
        buf[2] = kind;

        RecordCodec.TryDecode(buf.AsMemory(0, written), out _, out _).Should().BeFalse();
        RecordCodec.TryDecode(buf.AsSpan(0, written), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void OversizedKeyIsRejected()
    {
        var atCap = () => RecordCodec.GetRecordLength(KvasarConstants.MaxKeyBytes, 16, false);
        atCap.Should().NotThrow();

        var overCap = () => RecordCodec.GetRecordLength(KvasarConstants.MaxKeyBytes + 1, 16, false);
        overCap.Should().Throw<ArgumentOutOfRangeException>();

        var header = () => RecordCodec.MaxHeaderSize(KvasarConstants.MaxKeyBytes + 1);
        header.Should().Throw<ArgumentOutOfRangeException>();

        var encode = () => RecordCodec.Encode(
            new byte[64], RecordFlags.None, KvasarValueKind.Raw,
            new byte[KvasarConstants.MaxKeyBytes + 1], ReadOnlySpan<byte>.Empty, false);
        encode.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HugeLengthsDoNotWrapToANegativeRecordLength()
    {
        // Without a cap these sum to a negative int, and the caller then allocates/slices with it.
        var hugeKey = () => RecordCodec.GetRecordLength(int.MaxValue - 8, 16, false);
        hugeKey.Should().Throw<ArgumentOutOfRangeException>();

        var hugeValue = () => RecordCodec.GetRecordLength(16, int.MaxValue - 8, false);
        hugeValue.Should().Throw<ArgumentOutOfRangeException>();

        var negative = () => RecordCodec.GetRecordLength(-1, 16, false);
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EmptyOrZeroBufferFails()
    {
        RecordCodec.TryDecode(ReadOnlyMemory<byte>.Empty, out _, out _).Should().BeFalse();
        RecordCodec.TryDecode(new byte[8].AsMemory(), out _, out _).Should().BeFalse(); // varint 0 => padding
    }

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
