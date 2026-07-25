namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// Pass-through cipher (no encryption, no overhead). Used when encryption is disabled for
/// tests/benchmarks. On-disk page == plaintext page.
/// </summary>
public sealed class NoopPageCipher : IPageCipher
{
    public static readonly NoopPageCipher Instance = new();

    public int Overhead => 0;

    public void Encrypt(long pageId, ReadOnlySpan<byte> plain, Span<byte> onDisk)
        => plain.CopyTo(onDisk);

    public void Decrypt(long pageId, ReadOnlySpan<byte> onDisk, Span<byte> plain)
        => onDisk.CopyTo(plain);
}
