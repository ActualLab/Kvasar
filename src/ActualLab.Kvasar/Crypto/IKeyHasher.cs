namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// Hashes a key's bytes into a 64-bit value used by the in-RAM index (§6.1).
/// A keyed PRF (<see cref="IsKeyed"/> = true) isn't computable without the store key, which lets the
/// <c>.kidx</c> file stay unencrypted and resists hash-flooding.
/// </summary>
public interface IKeyHasher
{
    public bool IsKeyed { get; }
    public int SecretSize { get; }
    public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret);
}
