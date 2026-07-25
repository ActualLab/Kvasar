namespace ActualLab.Kvasar.Internal;

/// <summary>
/// Locates a record inside a <c>.klog</c> segment: <c>(SegmentId, Offset)</c> packed into a single
/// 64-bit word so it can be published atomically to lock-free readers (§7). The record's length is
/// recovered from its on-disk header, so it isn't part of the locator.
/// </summary>
public readonly struct Locator(uint segmentId, uint offset) : IEquatable<Locator>
{
    public static readonly Locator None = default;

    public uint SegmentId { get; } = segmentId;
    public uint Offset { get; } = offset;

    public ulong Packed => ((ulong)SegmentId << 32) | Offset;
    public bool IsNone => Packed == 0;

    public static Locator FromPacked(ulong packed)
        => new((uint)(packed >> 32), (uint)packed);

    public bool Equals(Locator other) => Packed == other.Packed;
    public override bool Equals(object? obj) => obj is Locator other && Equals(other);
    public override int GetHashCode() => Packed.GetHashCode();
    public override string ToString() => IsNone ? "Locator.None" : $"Locator({SegmentId}:{Offset})";

    public static bool operator ==(Locator a, Locator b) => a.Equals(b);
    public static bool operator !=(Locator a, Locator b) => !a.Equals(b);
}
