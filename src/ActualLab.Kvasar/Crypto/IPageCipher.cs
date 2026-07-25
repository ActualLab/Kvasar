namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// The per-page encryption hook (§5.1). Bound to one segment file's salt; nonces are derived
/// deterministically from <c>pageId</c> and that salt (safe only because sealed pages are immutable).
/// </summary>
public interface IPageCipher
{
    public int Overhead { get; }
    public void Encrypt(long pageId, ReadOnlySpan<byte> plain, Span<byte> onDisk);
    public void Decrypt(long pageId, ReadOnlySpan<byte> onDisk, Span<byte> plain);
}

/// <summary>
/// Creates an <see cref="IPageCipher"/> for a segment file given that file's random salt.
/// Holds the store-wide derived page key and format version (used as AES-GCM AAD).
/// </summary>
public interface IPageCipherFactory
{
    public int Overhead { get; }
    public IPageCipher Create(ReadOnlySpan<byte> fileSalt);
}
