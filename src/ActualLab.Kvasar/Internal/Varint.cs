using System.Buffers.Binary;
using System.Numerics;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// LEB128 unsigned varint read/write over spans, used by the record format (§5.2).
/// Fast paths are ported from ActualLab.Core's <c>SpanExt</c> (unrolled write + branchless read);
/// <see cref="TryRead"/> keeps a bounded, non-throwing fallback that reports a torn/overlong tail.
/// </summary>
public static class Varint
{
    public const int MaxSize = 10;

    private const byte LowBits = 0x7F;
    private const byte HighBit = 0x80;

    public static int SizeOf(ulong value)
    {
        var size = 1;
        while (value >= HighBit) {
            value >>= 7;
            size++;
        }
        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Write(Span<byte> target, ulong value)
    {
        // Unrolled first three bytes (ported from ActualLab.Core SpanExt.WriteVarUInt64).
        if (value < 1ul << 7) {
            target[0] = (byte)value;
            return 1;
        }
        target[0] = (byte)(HighBit | value);
        value >>= 7;
        if (value < 1ul << 7) {
            target[1] = (byte)value;
            return 2;
        }
        target[1] = (byte)(HighBit | value);
        value >>= 7;
        if (value < 1ul << 7) {
            target[2] = (byte)value;
            return 3;
        }

        var offset = 2;
        while (value >= HighBit) {
            target[offset++] = (byte)(HighBit | value);
            value >>= 7;
        }
        target[offset++] = (byte)value;
        return offset;
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out ulong value, out int bytesRead)
    {
        // Returns false (not throws) on a truncated/overlong varint, so the log-tail scan can detect a torn write.
        if (source.Length > 0) {
            uint first = source[0];
            if (first <= LowBits) {
                value = first;
                bytesRead = 1;
                return true;
            }
            // Branchless fast path for 2..3-byte varints (ported from SpanExt.ReadVarUInt64).
            // We read 4 bytes but only decode up to 3; the stop-bit mask covers bytes 0..2.
            if (source.Length >= 4) {
                var data = BinaryPrimitives.ReadUInt32LittleEndian(source);
                var stopBits = ~data & 0x00808080u;
                if (stopBits != 0) {
                    var length = (BitOperations.TrailingZeroCount(stopBits) >> 3) + 1;
                    var fast = (data & 0x7F)
                        | ((data & 0x7F00) >> 1)
                        | ((data & 0x7F0000) >> 2);
                    fast &= (1u << (length * 7)) - 1;
                    value = fast;
                    bytesRead = length;
                    return true;
                }
            }
        }

        // Bounded, non-throwing fallback: 4..10-byte varints and truncated tails.
        value = 0;
        var shift = 0;
        var n = Math.Min(source.Length, MaxSize);
        for (var i = 0; i < n; i++) {
            var b = source[i];
            // The 10th byte carries only bit 63; anything larger encodes a value past 2^64-1, so reject it
            // rather than silently dropping the excess bits.
            if (i == MaxSize - 1 && b > 1)
                break;
            value |= (ulong)(b & LowBits) << shift;
            if ((b & HighBit) == 0) {
                bytesRead = i + 1;
                return true;
            }
            shift += 7;
        }
        value = 0;
        bytesRead = 0;
        return false;
    }
}
