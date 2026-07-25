using System.Security.Cryptography;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Crypto;

// Adversarial review coverage for the Crypto module: official SipHash-2-4 vectors for every message
// length the reference suite defines, AES-GCM nonce-uniqueness properties (page id, salt, format
// version), determinism, tamper detection, and buffer-bounds contracts.
public class CryptoReviewTests
{
    private const int PageSize = 4096;
    private const uint FormatVer = 1;

    // SipHash-2-4 reference key 00..0f; message[i] = i (the reference suite's vectors.h layout).
    private static readonly byte[] SipRefKey =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
    ];

    // Cross-checked against OpenSSL 3.2's SIPHASH MAC (2-4 rounds, 8-byte output), read little-endian.
    private static readonly ulong[] SipRefVectors =
    [
        0x726fdb47dd0e0e31, 0x74f839c593dc67fd, 0x0d6c8009d9a94f5a, 0x85676696d7fb7e2d,
        0xcf2794e0277187b7, 0x18765564cd99a68d, 0xcbc9466e58fee3ce, 0xab0200f58b01d137,
        0x93f5f5799a932462, 0x9e0082df0ba9e4b0, 0x7a5dbbc594ddb9f3, 0xf4b32f46226bada7,
        0x751e8fbc860ee5fb, 0x14ea5627c0843d90, 0xf723ca908e7af2ee, 0xa129ca6149be45e5,
        0x3f2acc7f57c29bdb, 0x699ae9f52cbe4794, 0x4bc1b3f0968dd39c, 0xbb6dc91da77961bd,
        0xbed65cf21aa2ee98, 0xd0f2cbb02e3b67c7, 0x93536795e3a33e88, 0xa80c038ccd5ccec8,
        0xb8ad50c6f649af94, 0xbce192de8a85b8ea, 0x17d835b85bbb15f3, 0x2f2e6163076bcfad,
        0xde4daaaca71dc9a5, 0xa6a2506687956571, 0xad87a3535c49ef28, 0x32d892fad841c342,
        0x7127512f72f27cce, 0xa7f32346f95978e3, 0x12e0b01abb051238, 0x15e034d40fa197ae,
        0x314dffbe0815a3b4, 0x027990f029623981, 0xcadcd4e59ef40c4d, 0x9abfd8766a33735c,
        0x0e3ea96b5304a7d0, 0xad0c42d6fc585992, 0x187306c89bc215a9, 0xd4a60abcf3792b95,
        0xf935451de4f21df2, 0xa9538f0419755787, 0xdb9acddff56ca510, 0xd06c98cd5c0975eb,
        0xe612a3cb9ecba951, 0xc766e62cfcadaf96, 0xee64435a9752fe72, 0xa192d576b245165a,
        0x0a8787bf8ecb74b2, 0x81b3e73d20b49b6f, 0x7fa8220ba3b2ecea, 0x245731c13ca42499,
        0xb78dbfaf3a8d83bd, 0xea1ad565322a1a0b, 0x60e61c23a3795013, 0x6606d7e446282b93,
        0x6ca4ecb15c5f91e1, 0x9f626da15c9625f3, 0xe51b38608ef25f57, 0x958a324ceb064572,
    ];

    // --- SipHash-2-4 ---------------------------------------------------------

    [Fact]
    public void SipHash24MatchesOfficialVectors()
    {
        var hasher = KeyHashers.SipHash24;
        for (var length = 0; length < SipRefVectors.Length; length++) {
            var message = new byte[length];
            for (var i = 0; i < length; i++)
                message[i] = (byte)i;
            hasher.Hash(message, SipRefKey).Should().Be(SipRefVectors[length], "vector for length {0}", length);
        }
    }

    [Fact]
    public void SipHash24LengthIsPartOfTheFinalBlock()
    {
        // A zero-filled message must hash differently at every length: the reference folds the length
        // into the final block's high byte, so an implementation dropping it collapses these.
        var hasher = KeyHashers.SipHash24;
        var seen = new HashSet<ulong>();
        for (var length = 0; length <= 64; length++)
            seen.Add(hasher.Hash(new byte[length], SipRefKey)).Should().BeTrue();
    }

    [Fact]
    public void SipHash24IgnoresBytesPastTheKeySpan()
    {
        // Hash must depend only on the passed slice, not on the backing array's neighbours.
        var hasher = KeyHashers.SipHash24;
        var backing = new byte[64];
        for (var i = 0; i < backing.Length; i++)
            backing[i] = (byte)i;
        hasher.Hash(backing.AsSpan(0, 13), SipRefKey).Should().Be(SipRefVectors[13]);
    }

    [Fact]
    public void XxHash3IgnoresSecretAndReportsUnkeyed()
    {
        var hasher = KeyHashers.XxHash3;
        hasher.IsKeyed.Should().BeFalse();
        hasher.SecretSize.Should().Be(0);
        var withEmpty = hasher.Hash("payload"u8, []);
        var withJunk = hasher.Hash("payload"u8, RandomNumberGenerator.GetBytes(16));
        withJunk.Should().Be(withEmpty);
    }

    // --- HKDF subkey separation ---------------------------------------------

    [Fact]
    public void PageKeyAndHashKeyAreIndependentSubkeys()
    {
        // The store derives both from the same master key with an empty salt, so the info labels are
        // the only separation: a long expansion of each must not overlap anywhere.
        var kdf = KeyDerivations.HkdfSha256;
        var masterKey = RandomNumberGenerator.GetBytes(KvasarConstants.MasterKeySize);
        var pageKeyStream = new byte[256];
        var hashKeyStream = new byte[256];
        kdf.Derive(masterKey, [], KvasarConstants.PageKeyInfo, pageKeyStream);
        kdf.Derive(masterKey, [], KvasarConstants.HashKeyInfo, hashKeyStream);

        pageKeyStream.Should().NotEqual(hashKeyStream);
        for (var offset = 0; offset < pageKeyStream.Length - 16; offset++)
            pageKeyStream.AsSpan(offset, 16).SequenceEqual(hashKeyStream.AsSpan(0, 16)).Should().BeFalse();
        KvasarConstants.PageKeyInfo.SequenceEqual(KvasarConstants.HashKeyInfo).Should().BeFalse();
    }

    [Fact]
    public void DerivedSubkeysDependOnEveryMasterKeyBit()
    {
        var kdf = KeyDerivations.HkdfSha256;
        var masterKey = RandomNumberGenerator.GetBytes(KvasarConstants.MasterKeySize);
        var baseline = new byte[KvasarConstants.PageKeySize];
        kdf.Derive(masterKey, [], KvasarConstants.PageKeyInfo, baseline);

        for (var i = 0; i < masterKey.Length; i++) {
            var flipped = (byte[])masterKey.Clone();
            flipped[i] ^= 0x01;
            var derived = new byte[KvasarConstants.PageKeySize];
            kdf.Derive(flipped, [], KvasarConstants.PageKeyInfo, derived);
            derived.Should().NotEqual(baseline);
        }
    }

    // --- AES-GCM nonce uniqueness & determinism -----------------------------

    [Fact]
    public void CiphertextIsDeterministicAcrossCipherInstances()
    {
        // Required by the design: the nonce is never stored, so the same (key, salt, pageId, plaintext)
        // must reproduce the same on-disk bytes on any later run.
        var key = NewKey();
        var salt = NewSalt();
        var plain = NewPlainPage();

        var a = Encrypt(key, salt, FormatVer, 17, plain);
        var b = Encrypt(key, salt, FormatVer, 17, plain);
        b.Should().Equal(a);
    }

    [Fact]
    public void NoncesDifferForEveryPageIdInAFile()
    {
        // Identical plaintext under a repeated nonce yields identical ciphertext (AAD only affects the
        // tag), so distinct ciphertext bodies across page ids prove distinct nonces.
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = new byte[PageSize];
        var bodies = new HashSet<string>(StringComparer.Ordinal);
        for (long pageId = 0; pageId < 512; pageId++) {
            var onDisk = new byte[PageSize + cipher.Overhead];
            cipher.Encrypt(pageId, plain, onDisk);
            bodies.Add(Convert.ToHexString(onDisk.AsSpan(0, 64))).Should().BeTrue("page {0}", pageId);
        }
    }

    [Theory]
    [InlineData(0L, 1L << 32)]
    [InlineData(1L, (1L << 32) | 1L)]
    [InlineData(0x7fff_ffffL, 0x8000_0000L)]
    [InlineData(long.MaxValue, long.MaxValue - 1)]
    public void NonceUsesTheFullSixtyFourBitPageId(long a, long b)
    {
        // Guards against a truncation to 32 bits somewhere in the nonce derivation.
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = new byte[PageSize];
        var onDiskA = new byte[PageSize + cipher.Overhead];
        var onDiskB = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(a, plain, onDiskA);
        cipher.Encrypt(b, plain, onDiskB);
        onDiskA.AsSpan(0, PageSize).SequenceEqual(onDiskB.AsSpan(0, PageSize)).Should().BeFalse();
    }

    [Fact]
    public void SingleBitSaltChangeChangesEveryPageNonce()
    {
        var key = NewKey();
        var salt = NewSalt();
        var flipped = (byte[])salt.Clone();
        flipped[^1] ^= 0x01;
        var plain = new byte[PageSize];

        for (long pageId = 0; pageId < 32; pageId++) {
            var a = Encrypt(key, salt, FormatVer, pageId, plain);
            var b = Encrypt(key, flipped, FormatVer, pageId, plain);
            a.AsSpan(0, PageSize).SequenceEqual(b.AsSpan(0, PageSize)).Should().BeFalse("page {0}", pageId);
        }
    }

    [Fact]
    public void SingleBitKeyChangeChangesEveryPageNonce()
    {
        var key = NewKey();
        var flipped = (byte[])key.Clone();
        flipped[0] ^= 0x01;
        var salt = NewSalt();
        var plain = new byte[PageSize];

        for (long pageId = 0; pageId < 32; pageId++) {
            var a = Encrypt(key, salt, FormatVer, pageId, plain);
            var b = Encrypt(flipped, salt, FormatVer, pageId, plain);
            a.AsSpan(0, PageSize).SequenceEqual(b.AsSpan(0, PageSize)).Should().BeFalse("page {0}", pageId);
        }
    }

    [Fact]
    public void CiphertextLeaksNothingAboutAnAllZeroPage()
    {
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(0, new byte[PageSize], onDisk);
        onDisk.AsSpan(0, PageSize).IndexOfAnyExcept((byte)0).Should().NotBe(-1);
    }

    // --- AES-GCM authentication ---------------------------------------------

    [Fact]
    public void FormatVersionIsAuthenticatedAsAad()
    {
        var key = NewKey();
        var salt = NewSalt();
        var plain = NewPlainPage();
        var onDisk = Encrypt(key, salt, FormatVer, 9, plain);

        var other = NewCipher(key, salt, FormatVer + 1);
        var decrypted = new byte[PageSize];
        var act = () => other.Decrypt(9, onDisk, decrypted);
        act.Should().Throw<KvasarCorruptException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PageSize - 1)]
    [InlineData(PageSize)]
    [InlineData(PageSize + KvasarConstants.GcmTagSize - 1)]
    public void TamperingAnywhereThrowsCorrupt(int position)
    {
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(11, plain, onDisk);
        onDisk[position] ^= 0x01;

        var decrypted = new byte[PageSize];
        var act = () => cipher.Decrypt(11, onDisk, decrypted);
        act.Should().Throw<KvasarCorruptException>();
    }

    [Fact]
    public void AuthenticationFailureIsWrappedNotLeakedAsCryptographicException()
    {
        // The store's wipe-and-recreate path keys off KvasarCorruptException, so a raw
        // CryptographicException escaping here would take down the app instead of resetting the cache.
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(11, plain, onDisk);
        onDisk[^1] ^= 0x01;

        var decrypted = new byte[PageSize];
        var act = () => cipher.Decrypt(11, onDisk, decrypted);
        var thrown = act.Should().Throw<KvasarCorruptException>().Which;
        thrown.Should().NotBeAssignableTo<CryptographicException>();
        thrown.InnerException.Should().BeOfType<AuthenticationTagMismatchException>();
    }

    [Fact]
    public void FailedDecryptDoesNotLeavePlaintextInTheOutputBuffer()
    {
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(11, plain, onDisk);
        onDisk[5] ^= 0x01;

        var decrypted = new byte[PageSize];
        var act = () => cipher.Decrypt(11, onDisk, decrypted);
        act.Should().Throw<KvasarCorruptException>();
        decrypted.AsSpan().IndexOfAnyExcept((byte)0).Should().Be(-1);
    }

    // --- Buffer bounds & argument validation --------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(512)]
    [InlineData(PageSize - 1)]
    public void RoundTripsAnyPayloadLength(int length)
    {
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = RandomNumberGenerator.GetBytes(length);
        var onDisk = new byte[length + cipher.Overhead];
        cipher.Encrypt(3, plain, onDisk);

        var decrypted = new byte[length];
        cipher.Decrypt(3, onDisk, decrypted);
        decrypted.Should().Equal(plain);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(-KvasarConstants.GcmTagSize)]
    public void RejectsMismatchedOnDiskLength(int delta)
    {
        var cipher = NewCipher(NewKey(), NewSalt(), FormatVer);
        var plain = new byte[PageSize];
        var onDisk = new byte[PageSize + cipher.Overhead + delta];

        var encrypt = () => cipher.Encrypt(1, plain, onDisk);
        encrypt.Should().Throw<ArgumentException>();
        var decrypt = () => cipher.Decrypt(1, onDisk, new byte[PageSize]);
        decrypt.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void FactoryRejectsWrongPageKeySize(int keySize)
    {
        var act = () => new AesGcmPageCipherFactory(new byte[keySize], FormatVer);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(32)]
    public void FactoryRejectsWrongFileSaltSize(int saltSize)
    {
        var factory = new AesGcmPageCipherFactory(NewKey(), FormatVer);
        var act = () => factory.Create(new byte[saltSize]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OverheadIsTheGcmTagSizeOnBothFactoryAndCipher()
    {
        var factory = new AesGcmPageCipherFactory(NewKey(), FormatVer);
        factory.Overhead.Should().Be(KvasarConstants.GcmTagSize);
        factory.Create(NewSalt()).Overhead.Should().Be(KvasarConstants.GcmTagSize);
    }

    [Fact]
    public void FactoryCopiesThePageKey()
    {
        // A caller mutating its key buffer after construction must not silently repoint the cipher.
        var key = NewKey();
        var factory = new AesGcmPageCipherFactory(key, FormatVer);
        var salt = NewSalt();
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + KvasarConstants.GcmTagSize];
        factory.Create(salt).Encrypt(4, plain, onDisk);

        key.AsSpan().Clear();
        var decrypted = new byte[PageSize];
        factory.Create(salt).Decrypt(4, onDisk, decrypted);
        decrypted.Should().Equal(plain);
    }

    [Fact]
    public void SegmentHeaderSaltIsFreshAndRandomPerFile()
    {
        // Nonce uniqueness across files rests entirely on this salt being unpredictable and unique.
        var salts = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 256; i++) {
            var header = new SegmentHeader(FormatVer, PageSize, 1);
            header.FileSalt.Length.Should().Be(KvasarConstants.FileSaltSize);
            header.FileSalt.AsSpan().IndexOfAnyExcept((byte)0).Should().NotBe(-1);
            salts.Add(Convert.ToHexString(header.FileSalt)).Should().BeTrue();
        }
    }

    [Fact]
    public void SegmentHeaderRoundTripsTheSalt()
    {
        var header = new SegmentHeader(FormatVer, PageSize, 7, 3);
        var bytes = new byte[KvasarConstants.SegmentHeaderSize];
        header.Write(bytes);
        var parsed = SegmentHeader.Read(bytes);
        parsed.FileSalt.Should().Equal(header.FileSalt);
        parsed.FormatVer.Should().Be(FormatVer);
        parsed.PageSize.Should().Be(PageSize);
        parsed.SegmentId.Should().Be(7u);
        parsed.Flags.Should().Be(3u);
    }

    // Private methods

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(KvasarConstants.PageKeySize);
    private static byte[] NewSalt() => RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);
    private static byte[] NewPlainPage() => RandomNumberGenerator.GetBytes(PageSize);

    private static IPageCipher NewCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, uint formatVer)
        => new AesGcmPageCipherFactory(key, formatVer).Create(salt);

    private static byte[] Encrypt(
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> salt, uint formatVer, long pageId, ReadOnlySpan<byte> plain)
    {
        var cipher = NewCipher(key, salt, formatVer);
        var onDisk = new byte[plain.Length + cipher.Overhead];
        cipher.Encrypt(pageId, plain, onDisk);
        return onDisk;
    }
}
