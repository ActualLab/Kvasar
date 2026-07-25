using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar;

public static class KvasarKeyExt
{
    extension(KvasarKey key)
    {
        public string AsString => KvasarEncoding.Decode(key.Span);
    }
}
