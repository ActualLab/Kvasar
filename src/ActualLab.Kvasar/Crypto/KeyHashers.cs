namespace ActualLab.Kvasar.Crypto;

/// <summary>Default <see cref="IKeyHasher"/> singletons (stateless, thread-safe).</summary>
public static class KeyHashers
{
    public static IKeyHasher SipHash24 { get; } = new SipHash24Hasher();
    public static IKeyHasher XxHash3 { get; } = new XxHash3Hasher();
}
