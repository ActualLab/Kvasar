using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Paging;

public class SegmentHeaderTests
{
    [Fact]
    public void RoundTrips()
    {
        var header = new SegmentHeader(formatVer: 7, pageSize: 4096, segmentId: 42, flags: 0xABCD);
        var buffer = new byte[KvasarConstants.SegmentHeaderSize];
        header.Write(buffer);

        var read = SegmentHeader.Read(buffer);
        read.FormatVer.Should().Be(7u);
        read.PageSize.Should().Be(4096);
        read.SegmentId.Should().Be(42u);
        read.Flags.Should().Be(0xABCDu);
        read.FileSalt.Should().Equal(header.FileSalt);
    }

    [Fact]
    public void CtorGeneratesNonZeroSalt()
    {
        var a = new SegmentHeader(1, 4096, 0);
        var b = new SegmentHeader(1, 4096, 0);
        a.FileSalt.Should().HaveCount(KvasarConstants.FileSaltSize);
        a.FileSalt.Should().NotEqual(new byte[KvasarConstants.FileSaltSize]);
        a.FileSalt.Should().NotEqual(b.FileSalt); // fresh salt per header (astronomically unlikely to collide)
    }

    [Fact]
    public void BadMagicThrows()
    {
        var header = new SegmentHeader(1, 4096, 0);
        var buffer = new byte[KvasarConstants.SegmentHeaderSize];
        header.Write(buffer);
        buffer[0] ^= 0xFF;

        var act = () => SegmentHeader.Read(buffer);
        act.Should().Throw<KvasarCorruptException>();
    }

    [Fact]
    public void TruncatedThrows()
    {
        var act = () => SegmentHeader.Read(new byte[KvasarConstants.SegmentHeaderSize - 1]);
        act.Should().Throw<KvasarCorruptException>();
    }
}
