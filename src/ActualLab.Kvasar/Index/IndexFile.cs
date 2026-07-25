using System.Buffers.Binary;
using System.IO;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A loaded <c>.kidx</c>: the resolved entries (tombstones included, for the caller to apply) plus the
/// klog high-water mark the file is consistent up to.
/// </summary>
public readonly record struct IndexCheckpoint(IndexEntry[] Entries, uint SegmentId, uint Hwm);

// Header layout (fixed 64 bytes, little-endian):
//   off size field
//    0    4  Magic         "KIDX" (KvasarConstants.KIdxMagic)
//    4    4  FormatVer     uint  — must match the caller's formatVer
//    8    4  EntrySize     uint  — sizeof(IndexEntry); structural sanity/identity check
//   12    4  Reserved0     uint  — 0
//   16    8  CheckpointCnt long  — # of IndexEntry in the checkpoint region
//   24    4  HwmSegmentId  uint  — klog active segment id the file is consistent up to
//   28    4  HwmOffset     uint  — klog active logical HWM
//   32   32  Reserved      zero  — pad to 64
// Checkpoint/delta entries are cast straight to/from bytes with no byte-swapping, so the file is only
// portable across identical-endianness (little-endian) devices — acceptable for a single-device store (§2).
//
/// <summary>
/// Reads/writes the <c>&lt;base&gt;.kidx</c> index file (§6.5): a fixed 64-byte header, a blittable
/// <b>checkpoint</b> region (an <see cref="IndexEntry"/> array), then an append-only <b>delta tail</b>
/// of individual <see cref="IndexEntry"/> records. A trailing partial delta entry is tolerated and dropped.
/// </summary>
public static class IndexFile
{
    public const int HeaderSize = 64;

    // Header field offsets.
    private const int MagicOffset = 0;
    private const int FormatVerOffset = 4;
    private const int EntrySizeOffset = 8;
    private const int CheckpointCountOffset = 16;
    private const int HwmSegmentIdOffset = 24;
    private const int HwmOffsetOffset = 28;

    public static int EntrySize => Unsafe.SizeOf<IndexEntry>();

    public static async ValueTask<IndexCheckpoint?> TryLoad(
        string path, uint formatVer, CancellationToken cancellationToken = default)
    {
        // Returns null on a missing/mismatched/inconsistent file (caller falls back to a full klog scan);
        // tombstones survive in Entries for the caller to resolve.
        if (!File.Exists(path))
            return null;

        byte[] bytes;
        try {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) {
            return null;
        }
        return Parse(bytes, formatVer);
    }

    public static async ValueTask AppendDelta(
        string path, IndexEntry entry, CancellationToken cancellationToken = default)
    {
        // Lazy: no fsync — the klog is the source of truth, so a torn trailing delta is tolerated on load.
        var bytes = ToBytes(entry);
        var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    // Appends one delta to an already-open stream (the caller keeps it open across many writes,
    // avoiding a file open/close per entry). Does not flush — the caller controls durability.
    public static ValueTask AppendDelta(
        Stream stream, IndexEntry entry, CancellationToken cancellationToken = default)
        => stream.WriteAsync(ToBytes(entry), cancellationToken);

    public static async ValueTask WriteCheckpoint(
        string path, ReadOnlyMemory<IndexEntry> liveEntries,
        (uint SegmentId, uint Hwm) hwm, uint formatVer, CancellationToken cancellationToken = default)
    {
        // Atomic: writes a temp file then renames over the target; tombstones are dropped, delta tail reset.
        var buffer = BuildCheckpoint(liveEntries.Span, hwm, formatVer);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, buffer, cancellationToken).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    // Private methods

    private static IndexCheckpoint? Parse(byte[] bytes, uint formatVer)
    {
        if (bytes.Length < HeaderSize)
            return null;

        var header = bytes.AsSpan(0, HeaderSize);
        if (!header.Slice(MagicOffset, 4).SequenceEqual(KvasarConstants.KIdxMagic))
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[FormatVerOffset..]) != formatVer)
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[EntrySizeOffset..]) != (uint)EntrySize)
            return null;

        var checkpointCount = BinaryPrimitives.ReadInt64LittleEndian(header[CheckpointCountOffset..]);
        if (checkpointCount < 0)
            return null;

        var entrySize = EntrySize;
        // Compare against the file's capacity *before* multiplying: checkpointCount is attacker/corruption
        // controlled, and both `count * entrySize` and `HeaderSize + that` wrap for large values, sliding
        // a negative length past the guards and into a throwing Slice.
        if (checkpointCount > (bytes.Length - HeaderSize) / entrySize)
            return null; // header claims more checkpoint entries than the file holds ⇒ inconsistent
        var checkpointBytes = (int)checkpointCount * entrySize;
        var checkpointEnd = HeaderSize + checkpointBytes;

        var segmentId = BinaryPrimitives.ReadUInt32LittleEndian(header[HwmSegmentIdOffset..]);
        var hwmOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[HwmOffsetOffset..]);

        var checkpoint = MemoryMarshal.Cast<byte, IndexEntry>(
            bytes.AsSpan(HeaderSize, checkpointBytes));

        // Delta tail: whole entries only; a trailing partial entry (lazy/un-fsynced write torn on crash)
        // is dropped rather than treated as corruption.
        var deltaByteLen = bytes.Length - checkpointEnd;
        var deltaCount = deltaByteLen / entrySize;
        var deltas = MemoryMarshal.Cast<byte, IndexEntry>(
            bytes.AsSpan(checkpointEnd, deltaCount * entrySize));

        // Resolve last-writer-wins by KeyHash. Checkpoint first, then deltas in order; tombstones kept.
        var resolved = new Dictionary<ulong, IndexEntry>(checkpoint.Length + deltas.Length);
        foreach (var e in checkpoint)
            resolved[e.KeyHash] = e;
        foreach (var e in deltas)
            resolved[e.KeyHash] = e;

        var entries = new IndexEntry[resolved.Count];
        resolved.Values.CopyTo(entries, 0);
        return new IndexCheckpoint(entries, segmentId, hwmOffset);
    }

    private static byte[] BuildCheckpoint(
        ReadOnlySpan<IndexEntry> liveEntries, (uint SegmentId, uint Hwm) hwm, uint formatVer)
    {
        var liveCount = 0;
        foreach (var e in liveEntries)
            if (!e.IsTombstone)
                liveCount++;

        var byteLength = HeaderSize + (long)liveCount * EntrySize;
        if (byteLength > int.MaxValue)
            throw new InvalidOperationException("Index checkpoint exceeds the maximum addressable size.");

        var buffer = new byte[byteLength];
        WriteHeader(buffer, formatVer, liveCount, hwm);

        var dst = MemoryMarshal.Cast<byte, IndexEntry>(buffer.AsSpan(HeaderSize));
        var i = 0;
        foreach (var e in liveEntries)
            if (!e.IsTombstone)
                dst[i++] = e;
        return buffer;
    }

    private static byte[] ToBytes(IndexEntry entry)
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in entry, 1)).ToArray();

    private static void WriteHeader(Span<byte> header, uint formatVer, long checkpointCount, (uint SegmentId, uint Hwm) hwm)
    {
        header[..HeaderSize].Clear();
        KvasarConstants.KIdxMagic.CopyTo(header[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[FormatVerOffset..], formatVer);
        BinaryPrimitives.WriteUInt32LittleEndian(header[EntrySizeOffset..], (uint)EntrySize);
        BinaryPrimitives.WriteInt64LittleEndian(header[CheckpointCountOffset..], checkpointCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[HwmSegmentIdOffset..], hwm.SegmentId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[HwmOffsetOffset..], hwm.Hwm);
    }
}
