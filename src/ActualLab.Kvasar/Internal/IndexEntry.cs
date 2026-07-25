namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A fixed-size, blittable <c>.kidx</c> entry (§6.5): the file <em>is</em> an array of these, so the
/// checkpoint region is loaded with a near-memcpy via <see cref="System.Runtime.InteropServices.MemoryMarshal"/>.
/// Keep it blittable and layout-stable — it's persisted on disk.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct IndexEntry : IEquatable<IndexEntry>
{
    public ulong KeyHash;    // keyed hash of the key; fingerprint = its top 16 bits
    public uint SegmentId;   // .klog segment (§9)
    public uint Offset;      // record offset within the segment
    public uint Length;      // record (value) length hint; authoritative length is on disk
    public byte Flags;       // RecordFlags (Tombstone, ...)

    public readonly RecordFlags RecordFlags => (RecordFlags)Flags;
    public readonly bool IsTombstone => (Flags & (byte)RecordFlags.Tombstone) != 0;
    public readonly ushort Fingerprint => (ushort)(KeyHash >> 48);
    public readonly Locator Locator => new(SegmentId, Offset);

    // Equality
    public readonly bool Equals(IndexEntry other)
        => KeyHash == other.KeyHash && SegmentId == other.SegmentId
            && Offset == other.Offset && Length == other.Length && Flags == other.Flags;
    public override readonly bool Equals(object? obj)
        => obj is IndexEntry other && Equals(other);
    public override readonly int GetHashCode()
        => HashCode.Combine(KeyHash, SegmentId, Offset, Length, Flags);
}
