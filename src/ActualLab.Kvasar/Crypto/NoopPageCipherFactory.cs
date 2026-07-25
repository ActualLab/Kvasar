namespace ActualLab.Kvasar.Crypto;

public sealed class NoopPageCipherFactory : IPageCipherFactory
{
    public static readonly NoopPageCipherFactory Instance = new();

    public int Overhead => 0;

    public IPageCipher Create(ReadOnlySpan<byte> fileSalt)
        => NoopPageCipher.Instance;
}
