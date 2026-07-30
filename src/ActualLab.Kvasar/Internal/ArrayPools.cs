namespace ActualLab.Kvasar.Internal;

// Ported from ActualLab.Core's ArrayPools per the zero-dependency carve-out. Reaching a static through a
// generic type costs a static-base lookup at every call site; a plain static readonly field does not.
internal static class ArrayPools
{
    public static readonly ArrayPool<byte> SharedBytePool = ArrayPool<byte>.Shared;
}
