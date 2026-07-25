using System.Security.Cryptography;

namespace ActualLab.Kvasar.Crypto;

/// <summary>HKDF-SHA256 subkey derivation (master key -&gt; page key / hash key, per §5.3).</summary>
public sealed class HkdfSha256KeyDerivation : IKeyDerivation
{
    public void Derive(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        Span<byte> output)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, output, salt, info);
}
