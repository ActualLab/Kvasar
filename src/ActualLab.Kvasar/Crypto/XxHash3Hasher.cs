using System.IO.Hashing;

namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// Unkeyed xxHash3 (64-bit). Fast but not a PRF, so a store using it must encrypt its <c>.kidx</c>.
/// </summary>
public sealed class XxHash3Hasher : IKeyHasher
{
    public bool IsKeyed => false;
    public int SecretSize => 0;

    public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret)
        => XxHash3.HashToUInt64(key);
}
