using ActualLab.Kvasar.Crypto;

namespace ActualLab.Kvasar.Tests.Crypto;

public class SipHash24HasherTests
{
    // Reference key from the SipHash paper / reference impl: bytes 00..0f.
    private static readonly byte[] RefKey =
    [
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
        0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
    ];

    [Theory]
    // Known-answer vectors from the SipHash-2-4 reference implementation (vectors.h), message[i] = i.
    [InlineData(0, 0x726fdb47dd0e0e31UL)]
    [InlineData(1, 0x74f839c593dc67fdUL)]
    [InlineData(2, 0x0d6c8009d9a94f5aUL)]
    [InlineData(3, 0x85676696d7fb7e2dUL)]
    [InlineData(7, 0xab0200f58b01d137UL)]
    [InlineData(8, 0x93f5f5799a932462UL)]
    [InlineData(15, 0xa129ca6149be45e5UL)]
    public void KnownAnswerVectors(int length, ulong expected)
    {
        var message = new byte[length];
        for (var i = 0; i < length; i++)
            message[i] = (byte)i;

        var hasher = KeyHashers.SipHash24;
        hasher.Hash(message, RefKey).Should().Be(expected);
    }

    [Fact]
    public void ExposesKeyedMetadata()
    {
        KeyHashers.SipHash24.IsKeyed.Should().BeTrue();
        KeyHashers.SipHash24.SecretSize.Should().Be(16);
    }

    [Fact]
    public void RejectsWrongKeySize()
    {
        var act = () => KeyHashers.SipHash24.Hash("abc"u8, new byte[8]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DifferentKeysProduceDifferentHashes()
    {
        var k2 = (byte[])RefKey.Clone();
        k2[0] ^= 0xff;
        var msg = "the quick brown fox"u8;
        KeyHashers.SipHash24.Hash(msg, RefKey).Should().NotBe(KeyHashers.SipHash24.Hash(msg, k2));
    }
}
