namespace ActualLab.Kvasar.Internal;

/// <summary>
/// Encodes/decodes the plaintext record format (§5.2):
/// <c>recordLen: varint (bytes after this field)</c>, <c>flags: u8</c>, <c>valueKind: u8</c>,
/// <c>keyLen: varint</c>, <c>key</c>, <c>value</c> (absent for a tombstone).
/// </summary>
public static class RecordCodec
{
    public static int MaxHeaderSize(int keyLen)
    {
        RequireLengths(keyLen, 0);
        return Varint.MaxSize + 2 + Varint.SizeOf((ulong)keyLen) + keyLen;
    }

    public static int GetRecordLength(int keyLen, int valueLen, bool isTombstone)
    {
        RequireLengths(keyLen, isTombstone ? 0 : valueLen);
        var body = 2 + Varint.SizeOf((ulong)keyLen) + keyLen + (isTombstone ? 0 : valueLen);
        return Varint.SizeOf((ulong)body) + body;
    }

    public static int Encode(
        Span<byte> dst, RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool isTombstone)
    {
        if (isTombstone)
            flags |= RecordFlags.Tombstone;
        var valueLen = isTombstone ? 0 : value.Length;
        RequireLengths(key.Length, valueLen);
        var body = 2 + Varint.SizeOf((ulong)key.Length) + key.Length + valueLen;
        var pos = Varint.Write(dst, (ulong)body);
        dst[pos++] = (byte)flags;
        dst[pos++] = (byte)valueKind;
        pos += Varint.Write(dst[pos..], (ulong)key.Length);
        key.CopyTo(dst[pos..]);
        pos += key.Length;
        if (!isTombstone) {
            value.CopyTo(dst[pos..]);
            pos += valueLen;
        }
        return pos;
    }

    public static bool TryDecode(ReadOnlyMemory<byte> src, out RecordView view, out int totalLen)
    {
        // Zero-copy: the returned view's Key/Value slices alias src.
        view = default;
        if (!TryParse(src.Span, out var flags, out var valueKind,
                out var keyOffset, out var keyLen, out var valueOffset, out var valueLen, out var isTombstone, out totalLen))
            return false;
        view = new RecordView(
            flags, valueKind,
            src.Slice(keyOffset, keyLen),
            isTombstone ? ReadOnlyMemory<byte>.Empty : src.Slice(valueOffset, valueLen),
            isTombstone);
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> src, out RecordView view, out int totalLen)
    {
        // Copies key/value out: a span has no backing Memory to slice into the view.
        view = default;
        if (!TryParse(src, out var flags, out var valueKind,
                out var keyOffset, out var keyLen, out var valueOffset, out var valueLen, out var isTombstone, out totalLen))
            return false;
        var key = src.Slice(keyOffset, keyLen).ToArray();
        var value = isTombstone ? Array.Empty<byte>() : src.Slice(valueOffset, valueLen).ToArray();
        view = new RecordView(flags, valueKind, key, value, isTombstone);
        return true;
    }

    // Protected/internal methods

    internal static bool TryReadHeader(ReadOnlySpan<byte> src, long availableLength, out int totalLen)
    {
        totalLen = 0;
        if (availableLength <= 0
            || !Varint.TryRead(src, out var bodyLenU, out var recordLenByteCount))
            return false;
        if (bodyLenU < 2
            || availableLength < recordLenByteCount
            || bodyLenU > (ulong)(availableLength - recordLenByteCount)
            || bodyLenU > (ulong)(int.MaxValue - recordLenByteCount)
            || src.Length < recordLenByteCount + 2)
            return false;

        var valueKind = (KvasarValueKind)src[recordLenByteCount + 1];
        if (!Enum.IsDefined(valueKind))
            return false;

        totalLen = recordLenByteCount + (int)bodyLenU;
        return true;
    }

    // Private methods

    private static void RequireLengths(int keyLen, int valueLen)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keyLen);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(keyLen, KvasarConstants.MaxKeyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(valueLen);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(valueLen, KvasarConstants.MaxRecordValueBytes);
    }

    private static bool TryParse(
        ReadOnlySpan<byte> src, out RecordFlags flags, out KvasarValueKind valueKind,
        out int keyOffset, out int keyLen, out int valueOffset, out int valueLen,
        out bool isTombstone, out int totalLen)
    {
        flags = default;
        valueKind = default;
        keyOffset = keyLen = valueOffset = valueLen = 0;
        isTombstone = false;
        totalLen = 0;

        if (!TryReadHeader(src, src.Length, out totalLen)
            || !Varint.TryRead(src, out var bodyLenU, out var recLenBytes))
            return false;
        var bodyLen = (int)bodyLenU;

        var body = src.Slice(recLenBytes, bodyLen);
        flags = (RecordFlags)body[0];
        valueKind = (KvasarValueKind)body[1];
        isTombstone = (flags & RecordFlags.Tombstone) != 0;
        if (!Varint.TryRead(body[2..], out var keyLenU, out var keyLenBytes))
            return false;
        var headerLen = 2 + keyLenBytes;
        if (headerLen > bodyLen || keyLenU > (ulong)(bodyLen - headerLen))
            return false;
        var kLen = (int)keyLenU;

        keyOffset = recLenBytes + headerLen;
        keyLen = kLen;
        valueOffset = keyOffset + keyLen;
        valueLen = isTombstone ? 0 : bodyLen - headerLen - kLen;
        return true;
    }
}
