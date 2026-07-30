using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ActualLab.Kvasar.Internal;

// Every step is one one-shot HMAC-SHA256 over this 48-byte prefix followed by its payload:
//   off size field
//    0    1  step kind: 1 = start, 2 = block, 3 = tag
//    1    3  reserved, zero
//    4    4  payload byte count, uint
//    8    8  folded block count, long — the block's own index for a block step
//   16   32  the chain value the step extends; zero for the start step
//   48    n  payload — header prefix, label and context for the start step, block bytes otherwise
//
// One-shot is the whole point. Android's Mac cannot be cloned, so no step may peek a running digest the
// way IncrementalHash.GetCurrentHash does, and feeding one 32-byte entry at a time costs a JNI round-trip
// per cached key there.
//
/// <summary>
/// The block-chained HMAC-SHA256 over a <c>.kidx</c> committed prefix: the body is cut into fixed
/// <see cref="BlockSize"/> blocks and each block's step covers its predecessor's chain value, so a commit
/// costs one HMAC over the open trailing block rather than one over the whole index.
/// </summary>
internal sealed class IndexMac : IDisposable
{
    public const int BlockSize = 1 << 14;
    public const int TagSize = 16;

    private const int ChainSize = 32; // HMAC-SHA256 output size
    private const int PrefixSize = 48;
    private const int KindOffset = 0;
    private const int PayloadLengthOffset = 4;
    private const int BlockCountOffset = 8;
    private const int ChainOffset = 16;

    private const byte StartKind = 1;
    private const byte BlockKind = 2;
    private const byte TagKind = 3;

    private static ReadOnlySpan<byte> Label => "kvasar:kidx-mac/v1"u8;

    // Borrowed, not copied: the owner zeroes this array, and a private copy would only widen the window
    // in which the authentication key is resident.
    private readonly byte[] _key;
    private readonly byte[] _buffer;
    private readonly byte[] _chain = new byte[ChainSize];
    private long _blockCount;
    private int _blockLength;

    public IndexMac(byte[] key, ReadOnlySpan<byte> headerPrefix, ReadOnlySpan<byte> context)
    {
        ArgumentNullException.ThrowIfNull(key);
        var payloadLength = headerPrefix.Length + Label.Length + context.Length;
        if (payloadLength > BlockSize)
            throw new ArgumentException("The header prefix and context do not fit one block.", nameof(context));

        _key = key;
        _buffer = new byte[PrefixSize + BlockSize];
        var payload = _buffer.AsSpan(PrefixSize);
        headerPrefix.CopyTo(payload);
        Label.CopyTo(payload[headerPrefix.Length..]);
        context.CopyTo(payload[(headerPrefix.Length + Label.Length)..]);
        WritePrefix(StartKind, payloadLength);
        HMACSHA256.HashData(_key, _buffer.AsSpan(0, PrefixSize + payloadLength), _chain);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_chain);
        CryptographicOperations.ZeroMemory(_buffer);
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty) {
            var count = Math.Min(BlockSize - _blockLength, bytes.Length);
            bytes[..count].CopyTo(_buffer.AsSpan(PrefixSize + _blockLength));
            _blockLength += count;
            bytes = bytes[count..];
            if (_blockLength != BlockSize)
                continue;

            WritePrefix(BlockKind, BlockSize);
            HMACSHA256.HashData(_key, _buffer.AsSpan(), _chain);
            _blockCount++;
            _blockLength = 0;
        }
    }

    public void ComputeTag(Span<byte> tag)
    {
        // Deliberately non-destructive: more deltas follow this commit, so the trailing block stays open.
        WritePrefix(TagKind, _blockLength);
        Span<byte> hash = stackalloc byte[ChainSize];
        HMACSHA256.HashData(_key, _buffer.AsSpan(0, PrefixSize + _blockLength), hash);
        hash[..TagSize].CopyTo(tag);
    }

    // Private methods

    private void WritePrefix(byte kind, int payloadLength)
    {
        // A one-shot HMAC takes one contiguous input, so the chain is staged into the buffer ahead of the
        // payload that already sits there rather than fed as a separate append.
        var prefix = _buffer.AsSpan(0, PrefixSize);
        prefix.Clear();
        prefix[KindOffset] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[PayloadLengthOffset..], (uint)payloadLength);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[BlockCountOffset..], _blockCount);
        _chain.AsSpan().CopyTo(prefix[ChainOffset..]);
    }
}
