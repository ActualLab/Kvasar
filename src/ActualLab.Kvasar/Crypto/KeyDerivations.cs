namespace ActualLab.Kvasar.Crypto;

/// <summary>Default <see cref="IKeyDerivation"/> singleton (stateless, thread-safe).</summary>
public static class KeyDerivations
{
    public static IKeyDerivation HkdfSha256 { get; } = new HkdfSha256KeyDerivation();
}
