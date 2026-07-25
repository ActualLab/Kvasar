using System.Security.Cryptography;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Crypto;

public class HkdfSha256KeyDerivationTests
{
    private static readonly byte[] MasterKey = RandomNumberGenerator.GetBytes(KvasarConstants.MasterKeySize);

    [Fact]
    public void IsDeterministic()
    {
        var kdf = KeyDerivations.HkdfSha256;
        var salt = RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);

        var a = new byte[KvasarConstants.PageKeySize];
        var b = new byte[KvasarConstants.PageKeySize];
        kdf.Derive(MasterKey, salt, KvasarConstants.PageKeyInfo, a);
        kdf.Derive(MasterKey, salt, KvasarConstants.PageKeyInfo, b);

        a.Should().Equal(b);
    }

    [Fact]
    public void DifferentSaltProducesDifferentOutput()
    {
        var kdf = KeyDerivations.HkdfSha256;
        var salt1 = RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);
        var salt2 = RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);

        var a = new byte[KvasarConstants.PageKeySize];
        var b = new byte[KvasarConstants.PageKeySize];
        kdf.Derive(MasterKey, salt1, KvasarConstants.PageKeyInfo, a);
        kdf.Derive(MasterKey, salt2, KvasarConstants.PageKeyInfo, b);

        a.Should().NotEqual(b);
    }

    [Fact]
    public void DifferentInfoProducesDifferentSubkey()
    {
        var kdf = KeyDerivations.HkdfSha256;
        var salt = RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);

        var pageKey = new byte[KvasarConstants.PageKeySize];
        var hashKey = new byte[KvasarConstants.HashKeySize];
        kdf.Derive(MasterKey, salt, KvasarConstants.PageKeyInfo, pageKey);
        kdf.Derive(MasterKey, salt, KvasarConstants.HashKeyInfo, hashKey);

        // Compare the overlapping prefix; a shared master/salt with different info must diverge.
        pageKey.AsSpan(0, KvasarConstants.HashKeySize).SequenceEqual(hashKey).Should().BeFalse();
    }

    [Fact]
    public void MatchesReferenceHkdf()
    {
        var kdf = KeyDerivations.HkdfSha256;
        var salt = RandomNumberGenerator.GetBytes(KvasarConstants.FileSaltSize);

        var mine = new byte[KvasarConstants.PageKeySize];
        kdf.Derive(MasterKey, salt, KvasarConstants.PageKeyInfo, mine);

        var reference = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, MasterKey, KvasarConstants.PageKeySize,
            salt, KvasarConstants.PageKeyInfo.ToArray());

        mine.Should().Equal(reference);
    }
}
