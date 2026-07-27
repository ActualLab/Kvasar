namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A fixed-size, blittable <c>.kidx</c> entry (§6.5): the file <em>is</em> an array of these, so the
/// checkpoint region is loaded with a near-memcpy via <see cref="System.Runtime.InteropServices.MemoryMarshal"/>.
/// Keep it blittable and layout-stable — it's persisted on disk.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 32)]
public struct IndexEntry : IEquatable<IndexEntry>
{
    public ulong KeyHash;        // keyed hash of the key; fingerprint = its top 16 bits
    public ulong PackedLocator;  // Locator.Packed — (FileId, Offset) in one word
    public ulong KeyId;
    public uint Length;          // record (value) length hint; authoritative length is on disk
    public byte Flags;           // RecordFlags (Tombstone, ...)

    public readonly RecordFlags RecordFlags => (RecordFlags)Flags;
    public readonly bool IsTombstone => (Flags & (byte)RecordFlags.Tombstone) != 0;
    public readonly ushort Fingerprint => (ushort)(KeyHash >> 48);
    public readonly Locator Locator => Locator.FromPacked(PackedLocator);

    // Equality
    public readonly bool Equals(IndexEntry other)
        => KeyHash == other.KeyHash && PackedLocator == other.PackedLocator
            && KeyId == other.KeyId
            && Length == other.Length && Flags == other.Flags;
    public override readonly bool Equals(object? obj)
        => obj is IndexEntry other && Equals(other);
    public override readonly int GetHashCode()
        => HashCode.Combine(KeyHash, PackedLocator, KeyId, Length, Flags);
}
