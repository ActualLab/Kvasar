namespace ActualLab.Kvasar.Internal;

/// <summary>
/// Locates a record inside a <c>.kdat</c> data file: <c>(FileId, Offset)</c> packed into a single
/// 64-bit word so it can be published atomically to lock-free readers (§7). The record's length is
/// recovered from its on-disk header, so it isn't part of the locator.
/// </summary>
public readonly struct Locator : IEquatable<Locator>
{
    // 16/48 rather than 32/32: the offset only ever spans a single .kdat, so 256 TiB is already far
    // past any device, while the file dimension is worth headroom. The old 32-bit offset capped a file
    // at 4 GiB and threw OverflowException mid-append instead of rejecting at Open (I24).
    public const int OffsetBits = 48;
    public const long MaxOffset = (1L << OffsetBits) - 1;
    public const uint MaxFileId = (1u << (64 - OffsetBits)) - 1;

    public static readonly Locator None = default;

    public uint FileId { get; }
    public long Offset { get; }

    public ulong Packed => ((ulong)FileId << OffsetBits) | (ulong)Offset;
    public bool IsNone => Packed == 0;

    // FileId is 1-based: packed == 0 is HashIndex's empty-slot sentinel, and file 0 offset 0 would
    // collide with it. packed == ulong.MaxValue is its tombstone, unreachable while FileId <= MaxFileId.
    public Locator(uint fileId, long offset)
    {
        if (fileId > MaxFileId)
            throw new ArgumentOutOfRangeException(nameof(fileId));
        if ((ulong)offset > MaxOffset)
            throw new ArgumentOutOfRangeException(nameof(offset));

        FileId = fileId;
        Offset = offset;
    }

    public static Locator FromPacked(ulong packed)
        => new((uint)(packed >> OffsetBits), (long)(packed & MaxOffset));

    public bool Equals(Locator other) => Packed == other.Packed;
    public override bool Equals(object? obj) => obj is Locator other && Equals(other);
    public override int GetHashCode() => Packed.GetHashCode();
    public override string ToString() => IsNone ? "Locator.None" : $"Locator({FileId}:{Offset})";

    public static bool operator ==(Locator a, Locator b) => a.Equals(b);
    public static bool operator !=(Locator a, Locator b) => !a.Equals(b);
}
