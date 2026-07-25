using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// The fixed 64-byte plaintext prefix of a <c>.klog</c> segment file (§5.3). Encrypted pages follow it.
/// Layout (little-endian): magic(4) | formatVer(u32) | pageSize(i32) | segmentId(u32) | fileSalt(16) |
/// flags(u32) | reserved(zero-filled up to <see cref="KvasarConstants.SegmentHeaderSize"/>).
/// </summary>
public readonly struct SegmentHeader
{
    private const int MagicOffset = 0;
    private const int FormatVerOffset = 4;
    private const int PageSizeOffset = 8;
    private const int SegmentIdOffset = 12;
    private const int FileSaltOffset = 16;
    private const int FlagsOffset = 32;

    public uint FormatVer { get; }
    public int PageSize { get; }
    public uint SegmentId { get; }
    public byte[] FileSalt { get; }
    public uint Flags { get; }

    public SegmentHeader(uint formatVer, int pageSize, uint segmentId, uint flags = 0)
    {
        var salt = new byte[KvasarConstants.FileSaltSize];
        RandomNumberGenerator.Fill(salt);
        FormatVer = formatVer;
        PageSize = pageSize;
        SegmentId = segmentId;
        FileSalt = salt;
        Flags = flags;
    }

    private SegmentHeader(uint formatVer, int pageSize, uint segmentId, byte[] fileSalt, uint flags)
    {
        FormatVer = formatVer;
        PageSize = pageSize;
        SegmentId = segmentId;
        FileSalt = fileSalt;
        Flags = flags;
    }

    public static SegmentHeader Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < KvasarConstants.SegmentHeaderSize)
            throw new KvasarCorruptException("Segment header is truncated.");
        if (!source[MagicOffset..(MagicOffset + KvasarConstants.KLogMagic.Length)].SequenceEqual(KvasarConstants.KLogMagic))
            throw new KvasarCorruptException("Segment header has a bad magic.");

        var formatVer = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FormatVerOffset, 4));
        var pageSize = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(PageSizeOffset, 4));
        var segmentId = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(SegmentIdOffset, 4));
        var fileSalt = source.Slice(FileSaltOffset, KvasarConstants.FileSaltSize).ToArray();
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(FlagsOffset, 4));
        return new SegmentHeader(formatVer, pageSize, segmentId, fileSalt, flags);
    }

    public void Write(Span<byte> target)
    {
        if (target.Length < KvasarConstants.SegmentHeaderSize)
            throw new ArgumentException("Target is smaller than the segment header size.", nameof(target));

        target[..KvasarConstants.SegmentHeaderSize].Clear();
        KvasarConstants.KLogMagic.CopyTo(target[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FormatVerOffset, 4), FormatVer);
        BinaryPrimitives.WriteInt32LittleEndian(target.Slice(PageSizeOffset, 4), PageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(SegmentIdOffset, 4), SegmentId);
        FileSalt.AsSpan().CopyTo(target.Slice(FileSaltOffset, KvasarConstants.FileSaltSize));
        BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(FlagsOffset, 4), Flags);
    }
}
