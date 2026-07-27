using System.Buffers.Binary;
using System.Security.Cryptography;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// Why a read of <c>&lt;base&gt;.kvs</c> produced no adoptable commit — the four failures need four
/// different responses, and conflating them is how a wrong key ends up wiping an intact store.
/// </summary>
public enum SuperblockStatus
{
    // At least one slot authenticated; States holds the candidates, newest first.
    Ok = 0,
    // No superblock file yet: create the store.
    Missing,
    // Not a Kvasar superblock, or a different formatVer: wipe and recreate (this is how a
    // KvasarOptions.Version bump takes effect).
    FormatMismatch,
    // The file is ours and current, but the master key does not match it: throw, never wipe.
    WrongKey,
    // Right key, but neither slot authenticates: genuine corruption, wipe and rebuild.
    NoValidSlot,
}

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

/// <summary>
/// The outcome of reading <c>&lt;base&gt;.kvs</c>: a status the caller must branch on, plus every slot
/// that authenticated, newest generation first. The older candidate matters — recovery falls back to
/// it when the newest commit's data fails validation (§5.2 step 3).
/// </summary>
public readonly record struct SuperblockReadResult(SuperblockStatus Status, SuperblockState[] States)
{
    public SuperblockState? Newest => States.Length == 0 ? null : States[0];
}

// File layout: [64B header][512B slot 0][512B slot 1] = 1088 bytes.
//
// Header — written once, by Initialize, and never rewritten:
//   off size field
//    0    4  Magic       "KSUP"
//    4    4  FormatVer   uint
//    8   12  KcvNonce    random, fixed for the life of the file
//   20   16  KcvTag      AES-GCM tag over the fixed KcvPlaintext constant, AAD = formatVer(4 LE)
//   36   28  Reserved    zero
//
// Slot — written alternately, slot = generation mod 2:
//   off size field
//    0   12  Nonce       fresh CSPRNG bytes on every write
//   12  484  Ciphertext  AES-256-GCM over the plaintext block below, AAD = formatVer(4 LE)
//  496   16  Tag
//
// Plaintext block (484 bytes, little-endian):
//    0    8  Generation         ulong
//    8    1  DataSlot           byte
//    9    1  IndexSlot          byte
//   10    6  Reserved           zero
//   16    8  DataCommitLength   long
//   24    8  IndexCommitLength  long
//   32    8  LiveBytes          long
//   40    8  DeadBytes          long
//   48  436  Reserved           zero
//
// A slot carries no magic and no formatVer of its own — the header identifies the file, and formatVer
// is bound into every slot as GCM AAD. That leaves no unauthenticated byte in a slot, so a write torn
// at any offset fails its tag instead of decoding as a shorter-but-valid record.
//
/// <summary>
/// The <c>&lt;base&gt;.kvs</c> commit record: a key check value in the header, then two 512-byte slots
/// written alternately, each independently authenticated. A torn or tampered slot is simply "not a
/// valid slot" and never an exception, which is what lets the superblock skip fsync entirely
/// (<c>docs/DESIGN-Durability.md</c> §3.1, §5). <see cref="Initialize"/> creates the file; the first
/// <see cref="Write"/> assumes it already exists.
/// </summary>
public sealed class Superblock : IDisposable
{
    public const int HeaderSize = 64;
    public const int SlotSize = 512;
    public const int SlotCount = 2;
    public const int FileSize = HeaderSize + (SlotSize * SlotCount);

    private const int MagicOffset = 0;
    private const int FormatVerOffset = 4;
    private const int KcvNonceOffset = 8;
    private const int KcvTagOffset = 20;

    private const int SlotNonceOffset = 0;
    private const int CiphertextOffset = 12;
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

    private static ReadOnlySpan<byte> KcvPlaintext => "kvasar:kcv/v1"u8;

    private readonly byte[] _key;
    private readonly uint _formatVer;

    public Superblock(ReadOnlySpan<byte> masterKey, uint formatVer, IKeyDerivation? keyDerivation = null)
    {
        _key = new byte[KvasarConstants.SuperblockKeySize];
        (keyDerivation ?? KeyDerivations.HkdfSha256)
            .Derive(masterKey, [], KvasarConstants.SuperblockKeyInfo, _key);
        _formatVer = formatVer;
    }

    public void Dispose()
        => CryptographicOperations.ZeroMemory(_key);

    public ValueTask Initialize(IStorageFile file)
    {
        // Both slots are left zeroed, so a store killed between creation and its first commit reads
        // back as NoValidSlot — an empty store, which is what wipe-and-rebuild would produce anyway.
        var buffer = new byte[FileSize];
        var header = buffer.AsSpan(0, HeaderSize);
        KvasarConstants.KSupMagic.CopyTo(header[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(FormatVerOffset, 4), _formatVer);
        var kcvNonce = header.Slice(KcvNonceOffset, KvasarConstants.GcmNonceSize);
        RandomNumberGenerator.Fill(kcvNonce);
        ComputeKcvTag(kcvNonce, header.Slice(KcvTagOffset, KvasarConstants.GcmTagSize));
        return file.Write(0, buffer);
    }

    public async ValueTask<SuperblockReadResult> Read(
        IStorageFile file, CancellationToken cancellationToken = default)
    {
        var status = await ReadHeader(file, cancellationToken).ConfigureAwait(false);
        if (status != SuperblockStatus.Ok)
            return new SuperblockReadResult(status, []);

        var buffer = new byte[SlotSize];
        var state0 = await ReadSlot(file, 0, buffer, cancellationToken).ConfigureAwait(false);
        var state1 = await ReadSlot(file, 1, buffer, cancellationToken).ConfigureAwait(false);
        if (state0 is not { } s0)
            return state1 is { } only1
                ? new SuperblockReadResult(SuperblockStatus.Ok, [only1])
                : new SuperblockReadResult(SuperblockStatus.NoValidSlot, []);
        if (state1 is not { } s1)
            return new SuperblockReadResult(SuperblockStatus.Ok, [s0]);

        return new SuperblockReadResult(
            SuperblockStatus.Ok,
            s0.Generation > s1.Generation ? [s0, s1] : [s1, s0]);
    }

    public ValueTask Write(IStorageFile file, SuperblockState state)
    {
        if (state.DataSlot >= SlotCount || state.IndexSlot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(state), "Data and index slot indexes must be 0 or 1.");
        // Commit lengths only: LiveBytes/DeadBytes are an advisory compaction hint, and this runs inside
        // Commit, so rejecting them here would turn an accounting bug into a failed write. Recovery
        // already degrades to deriving the accounting when the persisted pair makes no sense.
        if (state.DataCommitLength < 0 || state.IndexCommitLength < 0)
            throw new ArgumentOutOfRangeException(nameof(state), "Commit lengths must be non-negative.");

        var slot = (int)(state.Generation % SlotCount);
        var buffer = new byte[SlotSize];
        FormatSlot(buffer, state);
        return file.Write(SlotOffset(slot), buffer);
    }

    // Private methods

    private static long SlotOffset(int slot)
        => HeaderSize + ((long)slot * SlotSize);

    private async ValueTask<SuperblockStatus> ReadHeader(IStorageFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
            return SuperblockStatus.Missing;
        if (file.Length < HeaderSize)
            return SuperblockStatus.FormatMismatch;

        var header = new byte[HeaderSize];
        if (!await file.TryReadExact(0, header, cancellationToken).ConfigureAwait(false))
            return SuperblockStatus.FormatMismatch;

        var magic = KvasarConstants.KSupMagic;
        if (!header.AsSpan(MagicOffset, magic.Length).SequenceEqual(magic))
            return SuperblockStatus.FormatMismatch;
        // Ordered ahead of the key check on purpose: a deliberate KvasarOptions.Version bump must read
        // as FormatMismatch (wipe and recreate) even when the key changed in the same step.
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(FormatVerOffset, 4)) != _formatVer)
            return SuperblockStatus.FormatMismatch;

        return IsKcvValid(header) ? SuperblockStatus.Ok : SuperblockStatus.WrongKey;
    }

    private bool IsKcvValid(ReadOnlySpan<byte> header)
    {
        Span<byte> tag = stackalloc byte[KvasarConstants.GcmTagSize];
        ComputeKcvTag(header.Slice(KcvNonceOffset, KvasarConstants.GcmNonceSize), tag);
        return CryptographicOperations.FixedTimeEquals(tag, header.Slice(KcvTagOffset, KvasarConstants.GcmTagSize));
    }

    private void ComputeKcvTag(ReadOnlySpan<byte> nonce, Span<byte> tag)
    {
        // The plaintext is a fixed constant, so the ciphertext carries no information and is dropped:
        // the tag alone proves the file was created under this key. A constant plaintext also makes the
        // nonce safe to reuse across opens, which is why the header can be written exactly once.
        Span<byte> ciphertext = stackalloc byte[KcvPlaintext.Length];
        Span<byte> aad = stackalloc byte[AadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, _formatVer);
        using var aes = new AesGcm(_key, KvasarConstants.GcmTagSize);
        aes.Encrypt(nonce, KcvPlaintext, ciphertext, tag, aad);
    }

    private async ValueTask<SuperblockState?> ReadSlot(
        IStorageFile file, int slot, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = SlotOffset(slot);
        if (file.Length < offset + SlotSize)
            return null;

        return await file.TryReadExact(offset, buffer, cancellationToken).ConfigureAwait(false)
            ? TryParseSlot(buffer, slot)
            : null;
    }

    private SuperblockState? TryParseSlot(ReadOnlySpan<byte> slot, int slotIndex)
    {
        Span<byte> plain = stackalloc byte[PlaintextSize];
        Span<byte> aad = stackalloc byte[AadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, _formatVer);
        try {
            using var aes = new AesGcm(_key, KvasarConstants.GcmTagSize);
            aes.Decrypt(
                slot.Slice(SlotNonceOffset, KvasarConstants.GcmNonceSize),
                slot.Slice(CiphertextOffset, PlaintextSize),
                slot.Slice(TagOffset, KvasarConstants.GcmTagSize),
                plain,
                aad);
        }
        catch (CryptographicException) {
            // A torn slot and a tampered slot are the same answer here: "no valid slot". Recovery
            // decides what to do about it; this is never an exception.
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

        // Accounting is parsed as-is, however odd: rejecting the slot for it would invalidate an
        // otherwise-adoptable generation and reach WipeFiles. SeedAccounting derives its own numbers
        // when the persisted pair cannot describe the committed extent.
        var liveBytes = BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(LiveBytesOffset, 8));
        var deadBytes = BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(DeadBytesOffset, 8));

        return new SuperblockState(
            generation,
            dataSlot,
            dataCommitLength,
            indexSlot,
            indexCommitLength,
            liveBytes,
            deadBytes);
    }

    private void FormatSlot(Span<byte> slot, SuperblockState state)
    {
        // 96-bit random nonces under one key: a store committing twice a second for a year issues
        // ~2^26 of them, so the collision probability stays around 2^-44.
        slot[..SlotSize].Clear();
        var nonce = slot.Slice(SlotNonceOffset, KvasarConstants.GcmNonceSize);
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
