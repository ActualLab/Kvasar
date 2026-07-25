using System.Security.Cryptography;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Crypto;

public class AesGcmPageCipherTests
{
    private const int PageSize = 4096;
    private const uint FormatVer = 1;

    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(KvasarConstants.PageKeySize);
    private static byte[] NewSalt() => RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);

    private static byte[] NewPlainPage()
    {
        var page = new byte[PageSize];
        RandomNumberGenerator.Fill(page);
        return page;
    }

    [Fact]
    public void RoundTripReturnsPlaintext()
    {
        var cipher = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(NewSalt());
        cipher.Overhead.Should().Be(KvasarConstants.GcmTagSize);

        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(42, plain, onDisk);

        // Ciphertext must differ from plaintext (actually encrypted).
        onDisk.AsSpan(0, PageSize).SequenceEqual(plain).Should().BeFalse();

        var decrypted = new byte[PageSize];
        cipher.Decrypt(42, onDisk, decrypted);
        decrypted.Should().Equal(plain);
    }

    [Fact]
    public void TamperedByteThrowsCorrupt()
    {
        var cipher = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(NewSalt());
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(7, plain, onDisk);

        onDisk[100] ^= 0x01; // flip a ciphertext bit

        var decrypted = new byte[PageSize];
        var act = () => cipher.Decrypt(7, onDisk, decrypted);
        act.Should().Throw<KvasarCorruptException>();
    }

    [Fact]
    public void TamperedTagThrowsCorrupt()
    {
        var cipher = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(NewSalt());
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(7, plain, onDisk);

        onDisk[PageSize] ^= 0x80; // flip a tag bit

        var decrypted = new byte[PageSize];
        var act = () => cipher.Decrypt(7, onDisk, decrypted);
        act.Should().Throw<KvasarCorruptException>();
    }

    [Fact]
    public void WrongKeyThrowsCorrupt()
    {
        var salt = NewSalt();
        var plain = NewPlainPage();

        var enc = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(salt);
        var onDisk = new byte[PageSize + enc.Overhead];
        enc.Encrypt(3, plain, onDisk);

        var dec = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(salt); // different key
        var decrypted = new byte[PageSize];
        var act = () => dec.Decrypt(3, onDisk, decrypted);
        act.Should().Throw<KvasarCorruptException>();
    }

    [Fact]
    public void WrongPageIdThrowsCorrupt()
    {
        var cipher = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(NewSalt());
        var plain = NewPlainPage();
        var onDisk = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(3, plain, onDisk);

        var decrypted = new byte[PageSize];
        var act = () => cipher.Decrypt(4, onDisk, decrypted); // AAD/nonce mismatch
        act.Should().Throw<KvasarCorruptException>();
    }

    [Fact]
    public void DifferentPageIdsProduceDifferentCiphertext()
    {
        var cipher = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(NewSalt());
        var plain = NewPlainPage(); // same plaintext for both pages

        var onDisk0 = new byte[PageSize + cipher.Overhead];
        var onDisk1 = new byte[PageSize + cipher.Overhead];
        cipher.Encrypt(0, plain, onDisk0);
        cipher.Encrypt(1, plain, onDisk1);

        onDisk0.AsSpan(0, PageSize).SequenceEqual(onDisk1.AsSpan(0, PageSize)).Should().BeFalse();
    }

    [Fact]
    public void DifferentSaltsProduceDifferentCiphertext()
    {
        var key = NewKey();
        var factory = new AesGcmPageCipherFactory(key, FormatVer);
        var plain = NewPlainPage();

        var a = factory.Create(NewSalt());
        var b = factory.Create(NewSalt());
        var onDiskA = new byte[PageSize + a.Overhead];
        var onDiskB = new byte[PageSize + b.Overhead];
        a.Encrypt(5, plain, onDiskA);
        b.Encrypt(5, plain, onDiskB);

        onDiskA.AsSpan(0, PageSize).SequenceEqual(onDiskB.AsSpan(0, PageSize)).Should().BeFalse();
    }

    [Fact]
    public void ConcurrentDecryptIsThreadSafe()
    {
        var cipher = new AesGcmPageCipherFactory(NewKey(), FormatVer).Create(NewSalt());
        var pages = new (byte[] Plain, byte[] OnDisk)[16];
        for (var i = 0; i < pages.Length; i++) {
            var plain = NewPlainPage();
            var onDisk = new byte[PageSize + cipher.Overhead];
            cipher.Encrypt(i, plain, onDisk);
            pages[i] = (plain, onDisk);
        }

        Parallel.For(0, 4096, iteration => {
            var pageId = iteration % pages.Length;
            var decrypted = new byte[PageSize];
            cipher.Decrypt(pageId, pages[pageId].OnDisk, decrypted);
            decrypted.AsSpan().SequenceEqual(pages[pageId].Plain).Should().BeTrue();
        });
    }

    [Fact]
    public void NoopCipherCopies()
    {
        var factory = NoopPageCipherFactory.Instance;
        factory.Overhead.Should().Be(0);
        var cipher = factory.Create(NewSalt());
        cipher.Overhead.Should().Be(0);

        var plain = NewPlainPage();
        var onDisk = new byte[PageSize];
        cipher.Encrypt(1, plain, onDisk);
        onDisk.Should().Equal(plain);

        var decrypted = new byte[PageSize];
        cipher.Decrypt(1, onDisk, decrypted);
        decrypted.Should().Equal(plain);
    }
}
