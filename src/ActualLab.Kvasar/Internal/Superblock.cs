using System.Buffers.Binary;
using System.Security.Cryptography;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A complete commit record (<c>docs/DESIGN-Durability.md</c> §3.1): which data/index file is active
/// and the committed byte extent of each. Recovery adopts one of these; nothing is ever inferred
/// from a file length.
/// </summary>
public readonly record struct SuperblockState(
    ulong Generation,
    byte DataSlot,
    long DataCommitLength,
    byte IndexSlot,
    long IndexCommitLength,
    long LiveBytes,
    long DeadBytes);

// Slot layout (512 bytes, little-endian):
//   off size field
//    0    4  Magic       "KSUP"
//    4    4  FormatVer   uint — also the GCM AAD
//    8   12  Nonce       fresh CSPRNG bytes on every write
//   20  476  Ciphertext  AES-256-GCM over the plaintext block below
//  496   16  Tag
// Plaintext block (476 bytes, little-endian):
//    0    8  Generation         ulong
//    8    1  DataSlot           byte
//    9    1  IndexSlot          byte
//   10    6  Reserved           zero
//   16    8  DataCommitLength   long
//   24    8  IndexCommitLength  long
//   32    8  LiveBytes          long
//   40    8  DeadBytes          long
//   48  428  Reserved           zero
// The authenticated region deliberately runs to the end of the slot, trailing padding included, so a
// write torn anywhere past the 20-byte plaintext header fails its tag rather than decoding as a
// shorter-but-valid record. Every byte of a slot is thus either structurally checked or authenticated.
//
/// <summary>
/// The <c>&lt;base&gt;.kvs</c> commit record: two 512-byte slots written alternately (slot = G mod 2),
/// each independently authenticated so a torn slot, a tampered slot and a wrong master key are all
/// simply "not a valid slot". Never flushed — see <c>docs/DESIGN-Durability.md</c> §3.1 and §5.
/// </summary>
public sealed class Superblock
{
    public const int SlotSize = 512;
    public const int SlotCount = 2;
    public const int FileSize = SlotSize * SlotCount;

    private const int MagicOffset = 0;
    private const int FormatVerOffset = 4;
    private const int NonceOffset = 8;
    private const int CiphertextOffset = 20;
    private const int PlaintextSize = SlotSize - CiphertextOffset - KvasarConstants.GcmTagSize;
    private const int TagOffset = CiphertextOffset + PlaintextSize;
    private const int AadSize = 4;

    private const int GenerationOffset = 0;
    private const int DataSlotOffset = 8;
    private const int IndexSlotOffset = 9;
    private const int DataCommitLengthOffset = 16;
    private const int IndexCommitLengthOffset = 24;
    private const int LiveBytesOffset = 32;
    private const int DeadBytesOffset = 40;

    private readonly byte[] _key;
    private readonly uint _formatVer;

    public Superblock(ReadOnlySpan<byte> masterKey, uint formatVer, IKeyDerivation? keyDerivation = null)
    {
        _key = new byte[KvasarConstants.SuperblockKeySize];
        (keyDerivation ?? KeyDerivations.HkdfSha256)
            .Derive(masterKey, [], KvasarConstants.SuperblockKeyInfo, _key);
        _formatVer = formatVer;
    }

    public async ValueTask<SuperblockState?> Read(
        IStorageFile file, CancellationToken cancellationToken = default)
    {
        var states = await ReadAll(file, cancellationToken).ConfigureAwait(false);
        return states.Length == 0 ? null : states[0];
    }

    public async ValueTask<SuperblockState[]> ReadAll(
        IStorageFile file, CancellationToken cancellationToken = default)
    {
        // Newest first, and the older candidate is kept: recovery falls back to it when the newest
        // generation's data fails authentication (§5.2 step 3).
        var buffer = new byte[SlotSize];
        var state0 = await ReadSlot(file, 0, buffer, cancellationToken).ConfigureAwait(false);
        var state1 = await ReadSlot(file, 1, buffer, cancellationToken).ConfigureAwait(false);
        if (state0 is not { } s0)
            return state1 is { } only1 ? [only1] : [];
        if (state1 is not { } s1)
            return [s0];

        return s0.Generation > s1.Generation ? [s0, s1] : [s1, s0];
    }

    public ValueTask Write(IStorageFile file, SuperblockState state)
    {
        if (state.DataSlot >= SlotCount || state.IndexSlot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(state), "Data and index slot indexes must be 0 or 1.");
        if (state.DataCommitLength < 0 || state.IndexCommitLength < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Commit lengths must be non-negative.");

        var slot = (int)(state.Generation % SlotCount);
        var buffer = new byte[SlotSize];
        Format(buffer, state);
        return file.Write((long)slot * SlotSize, buffer);
    }

    // Private methods

    private async ValueTask<SuperblockState?> ReadSlot(
        IStorageFile file, int slot, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = (long)slot * SlotSize;
        if (file.Length < offset + SlotSize)
            return null;

        var total = 0;
        while (total < SlotSize) {
            var read = await file
                .Read(offset + total, buffer.AsMemory(total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return null;
            total += read;
        }
        return TryParse(buffer, slot);
    }

    private SuperblockState? TryParse(ReadOnlySpan<byte> slot, int slotIndex)
    {
        var magic = KvasarConstants.KSupMagic;
        if (!slot.Slice(MagicOffset, magic.Length).SequenceEqual(magic))
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(FormatVerOffset, 4)) != _formatVer)
            return null;

        Span<byte> plain = stackalloc byte[PlaintextSize];
        Span<byte> aad = stackalloc byte[AadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, _formatVer);
        try {
            using var aes = new AesGcm(_key, KvasarConstants.GcmTagSize);
            aes.Decrypt(
                slot.Slice(NonceOffset, KvasarConstants.GcmNonceSize),
                slot.Slice(CiphertextOffset, PlaintextSize),
                slot.Slice(TagOffset, KvasarConstants.GcmTagSize),
                plain,
                aad);
        }
        catch (CryptographicException) {
            // A torn slot, a tampered slot and a wrong master key are all the same answer here:
            // "no valid slot". Recovery decides what to do about it; this is never an exception.
            return null;
        }

        var generation = BinaryPrimitives.ReadUInt64LittleEndian(plain.Slice(GenerationOffset, 8));
        if (generation % SlotCount != (ulong)slotIndex)
            return null;

        var dataSlot = plain[DataSlotOffset];
        var indexSlot = plain[IndexSlotOffset];
        if (dataSlot >= SlotCount || indexSlot >= SlotCount)
            return null;

        var dataCommitLength = BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(DataCommitLengthOffset, 8));
        var indexCommitLength = BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(IndexCommitLengthOffset, 8));
        if (dataCommitLength < 0 || indexCommitLength < 0)
            return null;

        return new SuperblockState(
            generation,
            dataSlot,
            dataCommitLength,
            indexSlot,
            indexCommitLength,
            BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(LiveBytesOffset, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(DeadBytesOffset, 8)));
    }

    private void Format(Span<byte> slot, SuperblockState state)
    {
        slot[..SlotSize].Clear();
        KvasarConstants.KSupMagic.CopyTo(slot[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(FormatVerOffset, 4), _formatVer);
        var nonce = slot.Slice(NonceOffset, KvasarConstants.GcmNonceSize);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> plain = stackalloc byte[PlaintextSize];
        plain.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(plain.Slice(GenerationOffset, 8), state.Generation);
        plain[DataSlotOffset] = state.DataSlot;
        plain[IndexSlotOffset] = state.IndexSlot;
        BinaryPrimitives.WriteInt64LittleEndian(plain.Slice(DataCommitLengthOffset, 8), state.DataCommitLength);
        BinaryPrimitives.WriteInt64LittleEndian(plain.Slice(IndexCommitLengthOffset, 8), state.IndexCommitLength);
        BinaryPrimitives.WriteInt64LittleEndian(plain.Slice(LiveBytesOffset, 8), state.LiveBytes);
        BinaryPrimitives.WriteInt64LittleEndian(plain.Slice(DeadBytesOffset, 8), state.DeadBytes);

        Span<byte> aad = stackalloc byte[AadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, _formatVer);
        using var aes = new AesGcm(_key, KvasarConstants.GcmTagSize);
        aes.Encrypt(
            nonce,
            plain,
            slot.Slice(CiphertextOffset, PlaintextSize),
            slot.Slice(TagOffset, KvasarConstants.GcmTagSize),
            aad);
    }
}
