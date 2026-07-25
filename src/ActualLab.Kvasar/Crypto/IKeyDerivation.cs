namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// Derives subkeys from the caller-supplied master key (§5.3). The default is HKDF-SHA256.
/// </summary>
public interface IKeyDerivation
{
    public void Derive(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        Span<byte> output);
}
