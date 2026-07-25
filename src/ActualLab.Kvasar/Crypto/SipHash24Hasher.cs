using System.Buffers.Binary;
using System.Numerics;

namespace ActualLab.Kvasar.Crypto;

/// <summary>
/// SipHash-2-4 keyed PRF (64-bit output). The 16-byte <c>secret</c> is used as the SipHash key,
/// so the produced hashes aren't computable without the store key (keeps <c>.kidx</c> unencryptable-safe).
/// </summary>
public sealed class SipHash24Hasher : IKeyHasher
{
    public bool IsKeyed => true;
    public int SecretSize => 16;

    public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret)
    {
        if (secret.Length != 16)
            throw new ArgumentException($"SipHash-2-4 requires a 16-byte key, got {secret.Length}.", nameof(secret));

        var k0 = BinaryPrimitives.ReadUInt64LittleEndian(secret);
        var k1 = BinaryPrimitives.ReadUInt64LittleEndian(secret[8..]);

        var v0 = 0x736f6d6570736575UL ^ k0;
        var v1 = 0x646f72616e646f6dUL ^ k1;
        var v2 = 0x6c7967656e657261UL ^ k0;
        var v3 = 0x7465646279746573UL ^ k1;

        var length = key.Length;
        var end = length - (length & 7); // last full 8-byte boundary
        var i = 0;
        for (; i < end; i += 8) {
            var m = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(i, 8));
            v3 ^= m;
            SipRound(ref v0, ref v1, ref v2, ref v3);
            SipRound(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        // Final block: high byte = length mod 256, low bytes = remaining 0..7 tail bytes.
        var b = (ulong)length << 56;
        var tail = length & 7;
        for (var j = 0; j < tail; j++)
            b |= (ulong)key[end + j] << (8 * j);

        v3 ^= b;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        v0 ^= b;

        v2 ^= 0xff;
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);
        SipRound(ref v0, ref v1, ref v2, ref v3);

        return v0 ^ v1 ^ v2 ^ v3;
    }

    private static void SipRound(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
        v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
        v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
        v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
    }
}
