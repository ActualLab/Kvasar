using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Internal;

public class VarintTests
{
    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(1UL, 1)]
    [InlineData(0x7FUL, 1)]
    [InlineData(0x80UL, 2)]
    [InlineData(0x3FFFUL, 2)]
    [InlineData(0x4000UL, 3)]
    [InlineData(0x1FFFFFUL, 3)]
    [InlineData(0x200000UL, 4)]
    [InlineData(uint.MaxValue, 5)]
    [InlineData(long.MaxValue, 9)]
    [InlineData(ulong.MaxValue, 10)]
    public void RoundTripsAtBoundaries(ulong value, int expectedSize)
    {
        var buf = new byte[Varint.MaxSize];
        var written = Varint.Write(buf, value);
        written.Should().Be(expectedSize);
        Varint.SizeOf(value).Should().Be(expectedSize);

        Varint.TryRead(buf.AsSpan(0, written), out var read, out var bytesRead).Should().BeTrue();
        read.Should().Be(value);
        bytesRead.Should().Be(written);
    }

    [Fact]
    public void MatchesReferenceLeb128()
    {
        // The fast paths are unrolled/branchless (ported from ActualLab.Core), so the encoding is
        // compared byte-for-byte against a naive LEB128 encoder over every power-of-two boundary.
        var buf = new byte[Varint.MaxSize];
        var expected = new byte[Varint.MaxSize];
        foreach (var value in Boundaries()) {
            var written = Varint.Write(buf, value);
            var refWritten = ReferenceWrite(expected, value);
            written.Should().Be(refWritten);
            buf.AsSpan(0, written).SequenceEqual(expected.AsSpan(0, refWritten)).Should().BeTrue();

            Varint.TryRead(buf.AsSpan(0, written), out var read, out _).Should().BeTrue();
            read.Should().Be(value);
        }
    }

    [Fact]
    public void RoundTripsRandomValues()
    {
        var rnd = new Random(12345);
        var buf = new byte[Varint.MaxSize];
        for (var i = 0; i < 20_000; i++) {
            var value = ((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next();
            var written = Varint.Write(buf, value);
            Varint.TryRead(buf.AsSpan(0, written), out var read, out var bytesRead).Should().BeTrue();
            read.Should().Be(value);
            bytesRead.Should().Be(written);
        }
    }

    [Fact]
    public void StopsAtStopBitIgnoringTrailingBytes()
    {
        // Records are decoded in place from a page, so whatever follows the varint must not affect it.
        var buf = new byte[Varint.MaxSize];
        foreach (var value in Boundaries()) {
            Array.Clear(buf);
            var written = Varint.Write(buf, value);
            buf.AsSpan(written).Fill(0xFF);

            Varint.TryRead(buf, out var read, out var bytesRead).Should().BeTrue();
            read.Should().Be(value);
            bytesRead.Should().Be(written);
        }
    }

    [Fact]
    public void ReportsTruncatedEncoding()
    {
        // The log-tail scan relies on TryRead returning false (never throwing) to detect a torn write,
        // so every strict prefix of a valid varint must be rejected.
        var buf = new byte[Varint.MaxSize];
        foreach (var value in Boundaries()) {
            var written = Varint.Write(buf, value);
            for (var cut = 1; cut < written; cut++)
                Varint.TryRead(buf.AsSpan(0, cut), out _, out _).Should().BeFalse();
        }
    }

    [Fact]
    public void ReportsEmptyAndOverlongEncodings()
    {
        Varint.TryRead(ReadOnlySpan<byte>.Empty, out _, out _).Should().BeFalse();

        var allContinuations = new byte[Varint.MaxSize];
        Array.Fill(allContinuations, (byte)0x80);
        Varint.TryRead(allContinuations, out _, out _).Should().BeFalse();
    }

    private static IEnumerable<ulong> Boundaries()
    {
        yield return 0;
        yield return ulong.MaxValue;
        for (var shift = 0; shift < 64; shift++) {
            yield return 1UL << shift;
            yield return (1UL << shift) - 1;
        }
    }

    private static int ReferenceWrite(Span<byte> target, ulong value)
    {
        var i = 0;
        while (value >= 0x80) {
            target[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        target[i++] = (byte)value;
        return i;
    }
}
