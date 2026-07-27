using System.Buffers.Binary;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// Authenticated metadata decoded from a framed data-log page.
/// </summary>
internal readonly record struct DataPageFrame(bool IsContinuation, int FirstRecordOffset)
{
    public bool HasRecordStart => FirstRecordOffset >= 0;
}

/// <summary>
/// Encodes and validates the authenticated header on each data-log page.
/// </summary>
internal static class DataPageFraming
{
    private const byte ContinuationFlag = 1;
    private const byte RecordStartFlag = 2;
    private const byte KnownFlags = ContinuationFlag | RecordStartFlag;
    private const int FlagsOffset = 0;
    private const int ReservedOffset = 1;
    private const int FirstRecordOffset = 4;

    public static void Write(Span<byte> page, bool isContinuation, int firstRecordOffset)
    {
        var payloadLength = page.Length - KvasarConstants.DataPageHeaderSize;
        var hasRecordStart = firstRecordOffset >= 0;
        if (payloadLength <= 0
            || firstRecordOffset < -1
            || firstRecordOffset >= payloadLength
            || (!isContinuation && firstRecordOffset != 0)
            || (isContinuation && firstRecordOffset == 0))
            throw new ArgumentOutOfRangeException(nameof(firstRecordOffset));

        var header = page[..KvasarConstants.DataPageHeaderSize];
        header.Clear();
        header[FlagsOffset] = (byte)(
            (isContinuation ? ContinuationFlag : 0)
            | (hasRecordStart ? RecordStartFlag : 0));
        BinaryPrimitives.WriteInt32LittleEndian(header[FirstRecordOffset..], firstRecordOffset);
    }

    public static bool TryRead(
        ReadOnlySpan<byte> page, int payloadLength, out DataPageFrame frame)
    {
        frame = default;
        if (page.Length < KvasarConstants.DataPageHeaderSize
            || payloadLength <= 0
            || payloadLength > page.Length - KvasarConstants.DataPageHeaderSize)
            return false;

        var header = page[..KvasarConstants.DataPageHeaderSize];
        var flags = header[FlagsOffset];
        if ((flags & ~KnownFlags) != 0
            || header.Slice(ReservedOffset, FirstRecordOffset - ReservedOffset)
                .IndexOfAnyExcept((byte)0) >= 0)
            return false;

        var isContinuation = (flags & ContinuationFlag) != 0;
        var hasRecordStart = (flags & RecordStartFlag) != 0;
        var firstRecordOffset = BinaryPrimitives.ReadInt32LittleEndian(header[FirstRecordOffset..]);
        if (hasRecordStart != (firstRecordOffset >= 0)
            || firstRecordOffset < -1
            || firstRecordOffset >= payloadLength
            || (!isContinuation && firstRecordOffset != 0)
            || (isContinuation && firstRecordOffset == 0))
            return false;

        frame = new DataPageFrame(isContinuation, firstRecordOffset);
        return true;
    }
}
