using System.Security.Cryptography;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// Builds <see cref="AesGcmPageCipher"/> instances bound to a segment file's salt. Holds the
/// store-wide 32-byte AES-256 page key and the format version (part of the per-page AAD).
/// </summary>
public sealed class AesGcmPageCipherFactory : IPageCipherFactory
{
    private readonly byte[] _pageKey;
    private readonly uint _formatVer;

    public int Overhead => KvasarConstants.GcmTagSize;

    public AesGcmPageCipherFactory(ReadOnlySpan<byte> pageKey, uint formatVer)
    {
        if (pageKey.Length != KvasarConstants.PageKeySize)
            throw new ArgumentException(
                $"AES-256 page key must be {KvasarConstants.PageKeySize} bytes, got {pageKey.Length}.", nameof(pageKey));
        _pageKey = pageKey.ToArray();
        _formatVer = formatVer;
    }

    public IPageCipher Create(ReadOnlySpan<byte> fileSalt)
    {
        if (fileSalt.Length != KvasarConstants.FileSaltSize)
            throw new ArgumentException(
                $"File salt must be {KvasarConstants.FileSaltSize} bytes, got {fileSalt.Length}.", nameof(fileSalt));

        // Per-file nonce key = HMAC-SHA256(pageKey, fileSalt). Nonces are then derived per page from
        // this key, so the (salt, pageId) -> nonce map is unique per file and never stored on disk.
        var nonceKey = new byte[32];
        HMACSHA256.HashData(_pageKey, fileSalt, nonceKey);
        return new AesGcmPageCipher(_pageKey, _formatVer, nonceKey);
    }
}
