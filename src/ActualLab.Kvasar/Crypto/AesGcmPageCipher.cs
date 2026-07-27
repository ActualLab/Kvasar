using System.Buffers.Binary;
using System.Security.Cryptography;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// AES-256-GCM per-page cipher (§5.1). On-disk page = <c>ciphertext(PageSize) || tag(16)</c>.
/// The 12-byte nonce is a deterministic function of (fileSalt, pageId) and is never stored — safe only
/// because sealed pages are immutable (a rewritten page would reuse a nonce and break GCM).
/// AAD = <c>pageId(8 LE) || formatVer(4 LE)</c> binds each page to its position and format version.
/// </summary>
public sealed class AesGcmPageCipher : IPageCipher, IDisposable
{
    private const int NonceSize = KvasarConstants.GcmNonceSize; // 12
    private const int TagSize = KvasarConstants.GcmTagSize;     // 16
    private const int AadSize = 12;                             // pageId(8) + formatVer(4)

    private readonly byte[] _pageKey;
    private readonly uint _formatVer;
    private readonly byte[] _nonceKey;

    public int Overhead => TagSize;

    internal AesGcmPageCipher(ReadOnlySpan<byte> pageKey, uint formatVer, byte[] nonceKey)
    {
        _pageKey = pageKey.ToArray();
        _formatVer = formatVer;
        _nonceKey = nonceKey;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_pageKey);
        CryptographicOperations.ZeroMemory(_nonceKey);
    }

    // Writer-only (single-threaded).
    public void Encrypt(long pageId, ReadOnlySpan<byte> plain, Span<byte> onDisk)
    {
        if (onDisk.Length != plain.Length + TagSize)
            throw new ArgumentException($"onDisk must be plain.Length + {TagSize} bytes.", nameof(onDisk));

        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> aad = stackalloc byte[AadSize];
        DeriveNonce(pageId, nonce);
        BuildAad(pageId, aad);

        var ciphertext = onDisk[..plain.Length];
        var tag = onDisk.Slice(plain.Length, TagSize);
        using var aes = new AesGcm(_pageKey, TagSize);
        aes.Encrypt(nonce, plain, ciphertext, tag, aad);
    }

    // Called concurrently by many reader threads -> uses a fresh AesGcm per call (AesGcm isn't
    // safe for concurrent use); nonce/AAD derivation is on stack, so this method is fully reentrant.
    public void Decrypt(long pageId, ReadOnlySpan<byte> onDisk, Span<byte> plain)
    {
        if (onDisk.Length != plain.Length + TagSize)
            throw new ArgumentException($"onDisk must be plain.Length + {TagSize} bytes.", nameof(onDisk));

        Span<byte> nonce = stackalloc byte[NonceSize];
        Span<byte> aad = stackalloc byte[AadSize];
        DeriveNonce(pageId, nonce);
        BuildAad(pageId, aad);

        var ciphertext = onDisk[..plain.Length];
        var tag = onDisk.Slice(plain.Length, TagSize);
        try {
            using var aes = new AesGcm(_pageKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plain, aad);
        }
        catch (CryptographicException e) {
            // Deliberately broader than AuthenticationTagMismatchException: any crypto failure that escaped
            // here would bypass Open's wipe-and-recreate (it only catches KvasarCorruptException) and
            // surface to the app, which §12 forbids.
            throw new KvasarCorruptException($"Page {pageId} failed AES-GCM authentication (wrong key or tampered data).", e);
        }
    }

    private void DeriveNonce(long pageId, Span<byte> nonce)
    {
        Span<byte> pageIdBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(pageIdBytes, pageId);
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(_nonceKey, pageIdBytes, full);
        full[..NonceSize].CopyTo(nonce);
    }

    private void BuildAad(long pageId, Span<byte> aad)
    {
        BinaryPrimitives.WriteInt64LittleEndian(aad[..8], pageId);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.Slice(8, 4), _formatVer);
    }
}
