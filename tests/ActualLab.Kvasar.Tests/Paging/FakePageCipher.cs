using ActualLab.Kvasar.Crypto;

namespace ActualLab.Kvasar.Tests.Paging;

// Local, self-contained IPageCipher fakes so these tests never depend on the Crypto module's concrete
// types. Two flavors are exercised: an "identity" cipher (Overhead 0, like the no-op) and a keystream
// XOR cipher that also appends an Overhead-byte trailer (models AES-GCM's tag + on-disk overhead).
internal sealed class FakePageCipherFactory : IPageCipherFactory
{
    private readonly byte _keyByte;

    public int Overhead { get; }

    public FakePageCipherFactory(int overhead, byte keyByte = 0x5A)
    {
        Overhead = overhead;
        _keyByte = keyByte;
    }

    public IPageCipher Create(ReadOnlySpan<byte> fileSalt)
        => new FakePageCipher(Overhead, _keyByte, fileSalt);
}

internal sealed class FakePageCipher : IPageCipher
{
    private readonly byte _keyByte;
    private readonly byte[] _salt;

    public int Overhead { get; }

    public FakePageCipher(int overhead, byte keyByte, ReadOnlySpan<byte> salt)
    {
        Overhead = overhead;
        _keyByte = keyByte;
        _salt = salt.ToArray();
    }

    public void Encrypt(long pageId, ReadOnlySpan<byte> plain, Span<byte> onDisk)
    {
        var payload = onDisk[..plain.Length];
        for (var i = 0; i < plain.Length; i++)
            payload[i] = (byte)(plain[i] ^ KeyStream(pageId, i));
        for (var i = 0; i < Overhead; i++)
            onDisk[plain.Length + i] = Trailer(pageId, i);
    }

    public void Decrypt(long pageId, ReadOnlySpan<byte> onDisk, Span<byte> plain)
    {
        var payload = onDisk[..plain.Length];
        for (var i = 0; i < Overhead; i++)
            if (onDisk[plain.Length + i] != Trailer(pageId, i))
                throw new KvasarCorruptException("Fake cipher trailer mismatch.");
        for (var i = 0; i < plain.Length; i++)
            plain[i] = (byte)(payload[i] ^ KeyStream(pageId, i));
    }

    private byte KeyStream(long pageId, int i)
        => (byte)(_keyByte ^ _salt[i % _salt.Length] ^ (byte)pageId ^ (byte)(pageId >> 8) ^ (byte)i);

    private byte Trailer(long pageId, int i)
        => (byte)(_salt[i % _salt.Length] + (byte)pageId + (byte)(i * 7));
}
