using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar;

public static class KvasarValueExt
{
    extension(KvasarValue value)
    {
        public string AsString => KvasarEncoding.Decode(value.Require(KvasarValueKind.Raw).Span);
    }
}
