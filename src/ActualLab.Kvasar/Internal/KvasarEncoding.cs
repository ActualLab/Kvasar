using System.Text;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// The single char ⇄ byte encoding used by <see cref="KvasarKey"/> and <see cref="KvasarValue"/>:
/// UTF-8, no BOM, no preamble.
/// </summary>
public static class KvasarEncoding
{
    public static ReadOnlyMemory<byte> Encode(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
            return default;

        var bytes = new byte[Encoding.UTF8.GetByteCount(chars)];
        Encoding.UTF8.GetBytes(chars, bytes);
        return bytes;
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
        => Encoding.UTF8.GetString(bytes);
}
