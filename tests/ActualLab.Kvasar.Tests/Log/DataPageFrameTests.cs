using System.Buffers.Binary;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Log;

public class DataPageFrameTests
{
    [Theory]
    [InlineData(false, 0, 2)]
    [InlineData(true, -1, 1)]
    [InlineData(true, 100, 3)]
    public void FrameRoundTrips(bool isContinuation, int firstRecordOffset, byte expectedFlags)
    {
        var page = new byte[512];
        DataPageFraming.Write(page, isContinuation, firstRecordOffset);

        page[0].Should().Be(expectedFlags);
        page.AsSpan(1, 3).IndexOfAnyExcept((byte)0).Should().Be(-1);
        BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(4)).Should().Be(firstRecordOffset);
        DataPageFraming.TryRead(
            page,
            page.Length - KvasarConstants.DataPageHeaderSize,
            out var frame).Should().BeTrue();
        frame.IsContinuation.Should().Be(isContinuation);
        frame.FirstRecordOffset.Should().Be(firstRecordOffset);
    }

    [Fact]
    public void InvalidFlagsAndOffsetsAreRejected()
    {
        var page = new byte[512];
        DataPageFraming.Write(page, isContinuation: false, firstRecordOffset: 0);

        page[0] = byte.MaxValue;
        DataPageFraming.TryRead(page, 504, out _).Should().BeFalse();

        DataPageFraming.Write(page, isContinuation: true, firstRecordOffset: -1);
        BinaryPrimitives.WriteInt32LittleEndian(page.AsSpan(4), 0);
        DataPageFraming.TryRead(page, 504, out _).Should().BeFalse();
    }
}
