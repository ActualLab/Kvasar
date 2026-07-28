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
    // No readable superblock file: inspect current-format data before deciding this is a new store.
    Missing,
    // Not a Kvasar superblock, or a different formatVer: current-format data distinguishes damage
    // from a deliberate KvasarOptions.Version bump.
    FormatMismatch,
    // A recognized format, but neither its KCV nor any compatible slot authenticates: throw, never wipe.
    WrongKey,
    // The current KCV authenticates, but neither slot does: recover from authenticated data.
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
    long DeadBytes,
    long DataAuthenticationFloor = KvasarConstants.SegmentHeaderSize,
    ulong NextKeyId = 1);

/// <summary>
/// The outcome of reading <c>&lt;base&gt;.kvs</c>: a status the caller must branch on, plus every slot
/// that authenticated, newest generation first. The older candidate matters — recovery falls back to
/// it when the newest commit's data fails validation (§5.2 step 3).
/// </summary>
public readonly record struct SuperblockReadResult(SuperblockStatus Status, SuperblockState[] States)
{
    public SuperblockState? Newest => States.Length == 0 ? null : States[0];
}

// File layout: [512B header][512B slot 0][512B slot 1] = 1536 bytes.
//
// Header — written by Initialize at creation or last-resort recovery, never by an ordinary commit:
//   off size field
//    0    4  Magic       "KSUP"
//    4    4  FormatVer   uint
//    8   12  KcvNonce    random, fixed for the life of the file
//   20   16  KcvTag      AES-GCM tag over the fixed KcvPlaintext constant, AAD = formatVer(4 LE)
//   36  476  Reserved    zero
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
//   48    8  DataAuthenticationFloor long
//   56    8  NextKeyId          ulong
//   64  420  Reserved           zero
//
// A slot carries no magic and no formatVer of its own — the header identifies the file, and formatVer
// is bound into every slot as GCM AAD. That leaves no unauthenticated byte in a slot, so a write torn
// at any offset fails its tag instead of decoding as a shorter-but-valid record.
//
/// <summary>
/// The <c>&lt;base&gt;.kvs</c> commit record: a key check value in the header, then two 512-byte slots
/// written alternately, each independently authenticated. A torn or tampered slot is simply "not a
/// valid slot" and never an exception. <see cref="Initialize"/> creates the file; the first
/// <see cref="Write"/> assumes it already exists (<c>docs/DESIGN-Durability.md</c> §3.1, §5).
/// </summary>
public sealed class Superblock : IDisposable
{
    public const int HeaderSize = 512;
    public const int SlotSize = 512;
    public const int SlotCount = 2;
    public const int FileSize = HeaderSize + (SlotSize * SlotCount);

    private const int PreviousHeaderSize = 64;
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
    private const int DataAuthenticationFloorOffset = 48;
    private const int NextKeyIdOffset = 56;

    private static ReadOnlySpan<byte> KcvPlaintext => "kvasar:kcv/v1"u8;

    private readonly byte[] _key;
    private readonly uint _formatVer;
    private readonly uint _previousFormatVer;
    private readonly uint _slotLayoutVersion;
    private readonly int _headerSize;

    public Superblock(
        ReadOnlySpan<byte> masterKey,
        uint formatVer,
        IKeyDerivation? keyDerivation = null,
        uint previousFormatVer = KvasarConstants.PreviousDataFormatVersion,
        uint slotLayoutVersion = KvasarConstants.DataFormatVersion)
    {
        if (slotLayoutVersion is not KvasarConstants.PreviousDataFormatVersion
            and not KvasarConstants.DataFormatVersion)
            throw new ArgumentOutOfRangeException(nameof(slotLayoutVersion));

        _key = new byte[KvasarConstants.SuperblockKeySize];
        try {
            (keyDerivation ?? KeyDerivations.HkdfSha256)
                .Derive(masterKey, [], KvasarConstants.SuperblockKeyInfo, _key);
        }
        catch {
            CryptographicOperations.ZeroMemory(_key);
            throw;
        }
        _formatVer = formatVer;
        _previousFormatVer = previousFormatVer;
        _slotLayoutVersion = slotLayoutVersion;
        _headerSize = HeaderSizeFor(slotLayoutVersion);
    }

    public void Dispose()
        => CryptographicOperations.ZeroMemory(_key);

    public async ValueTask Initialize(IStorageFile file)
    {
        // Both slots are left zeroed, so a store killed between initialization and its first commit
        // returns to the data-prefix fallback or an empty new store.
        var buffer = new byte[_headerSize + (SlotSize * SlotCount)];
        var header = buffer.AsSpan(0, _headerSize);
        KvasarConstants.KSupMagic.CopyTo(header[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(FormatVerOffset, 4), _formatVer);
        var kcvNonce = header.Slice(KcvNonceOffset, KvasarConstants.GcmNonceSize);
        RandomNumberGenerator.Fill(kcvNonce);
        ComputeKcvTag(kcvNonce, header.Slice(KcvTagOffset, KvasarConstants.GcmTagSize), _formatVer);
        await file.Truncate(0).ConfigureAwait(false);
        await file.Write(0, buffer).ConfigureAwait(false);
    }

    public ValueTask<SuperblockReadResult> Read(
        IStorageFile file, CancellationToken cancellationToken = default)
        => ReadCore(file, cancellationToken);

    public ValueTask Write(IStorageFile file, SuperblockState state)
    {
        if (state.DataSlot >= SlotCount || state.IndexSlot >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(state), "Data and index slot indexes must be 0 or 1.");
        // Commit lengths only: LiveBytes/DeadBytes are an advisory compaction hint, and this runs inside
        // Commit, so rejecting them here would turn an accounting bug into a failed write. Recovery
        // already degrades to deriving the accounting when the persisted pair makes no sense.
        if (state.DataCommitLength < 0 || state.IndexCommitLength < 0
            || state.DataAuthenticationFloor < KvasarConstants.SegmentHeaderSize
            || state.DataAuthenticationFloor > state.DataCommitLength
            || state.NextKeyId == 0)
            throw new ArgumentOutOfRangeException(
                nameof(state), "Commit lengths, authentication floor, or next key identity are invalid.");

        var slot = (int)(state.Generation % SlotCount);
        var buffer = new byte[SlotSize];
        FormatSlot(buffer, state);
        return file.Write(SlotOffset(slot, _slotLayoutVersion), buffer);
    }

    // Private methods

    private async ValueTask<SuperblockReadResult> ReadCore(
        IStorageFile file, CancellationToken cancellationToken)
    {
        var (status, header) = await ReadHeader(file, cancellationToken).ConfigureAwait(false);
        if (header is null)
            return new SuperblockReadResult(status, []);

        var storedFormatVer = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(FormatVerOffset, 4));
        if (storedFormatVer == _formatVer) {
            var states = await ReadSlots(
                    file, _formatVer, _slotLayoutVersion, cancellationToken)
                .ConfigureAwait(false);
            if (states.Length != 0)
                return new SuperblockReadResult(SuperblockStatus.Ok, states);
            if (IsKcvValid(header, _formatVer))
                return new SuperblockReadResult(SuperblockStatus.NoValidSlot, []);
            if (IsKcvValid(header, _previousFormatVer))
                return new SuperblockReadResult(SuperblockStatus.FormatMismatch, []);

            var legacyStates = await ReadSlots(
                    file,
                    _previousFormatVer,
                    KvasarConstants.PreviousDataFormatVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            return new SuperblockReadResult(
                legacyStates.Length != 0
                    ? SuperblockStatus.FormatMismatch
                    : SuperblockStatus.WrongKey,
                []);
        }
        if (storedFormatVer != _previousFormatVer)
            return new SuperblockReadResult(SuperblockStatus.FormatMismatch, []);
        if (IsKcvValid(header, _previousFormatVer))
            return new SuperblockReadResult(SuperblockStatus.FormatMismatch, []);
        if (IsKcvValid(header, _formatVer))
            return new SuperblockReadResult(SuperblockStatus.FormatMismatch, []);

        var previousStates = await ReadSlots(
                file,
                _previousFormatVer,
                KvasarConstants.PreviousDataFormatVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (previousStates.Length != 0)
            return new SuperblockReadResult(SuperblockStatus.FormatMismatch, []);

        var currentStates = await ReadSlots(
                file, _formatVer, _slotLayoutVersion, cancellationToken)
            .ConfigureAwait(false);
        return new SuperblockReadResult(
            currentStates.Length != 0
                ? SuperblockStatus.FormatMismatch
                : SuperblockStatus.WrongKey,
            []);
    }

    private static async ValueTask<(SuperblockStatus Status, byte[]? Header)> ReadHeader(
        IStorageFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
            return (SuperblockStatus.Missing, null);
        if (file.Length < PreviousHeaderSize)
            return (SuperblockStatus.FormatMismatch, null);

        var header = new byte[PreviousHeaderSize];
        if (!await file.TryReadExact(0, header, cancellationToken).ConfigureAwait(false))
            return (SuperblockStatus.FormatMismatch, null);

        var magic = KvasarConstants.KSupMagic;
        if (!header.AsSpan(MagicOffset, magic.Length).SequenceEqual(magic))
            return (SuperblockStatus.FormatMismatch, null);

        return (SuperblockStatus.Ok, header);
    }

    private bool IsKcvValid(ReadOnlySpan<byte> header, uint formatVer)
    {
        Span<byte> tag = stackalloc byte[KvasarConstants.GcmTagSize];
        ComputeKcvTag(header.Slice(KcvNonceOffset, KvasarConstants.GcmNonceSize), tag, formatVer);
        return CryptographicOperations.FixedTimeEquals(tag, header.Slice(KcvTagOffset, KvasarConstants.GcmTagSize));
    }

    private void ComputeKcvTag(ReadOnlySpan<byte> nonce, Span<byte> tag, uint formatVer)
    {
        // The plaintext is a fixed constant, so the ciphertext carries no information and is dropped:
        // the tag alone proves the file was created under this key. A constant plaintext also makes the
        // nonce safe to reuse across opens, which is why the header can be written exactly once.
        Span<byte> ciphertext = stackalloc byte[KcvPlaintext.Length];
        Span<byte> aad = stackalloc byte[AadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, formatVer);
        using var aes = new AesGcm(_key, KvasarConstants.GcmTagSize);
        aes.Encrypt(nonce, KcvPlaintext, ciphertext, tag, aad);
    }

    private async ValueTask<SuperblockState[]> ReadSlots(
        IStorageFile file,
        uint formatVer,
        uint slotLayoutVersion,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[SlotSize];
        var state0 = await ReadSlot(0).ConfigureAwait(false);
        var state1 = await ReadSlot(1).ConfigureAwait(false);
        if (state0 is not { } s0)
            return state1 is { } only1 ? [only1] : [];
        if (state1 is not { } s1)
            return [s0];

        return s0.Generation > s1.Generation ? [s0, s1] : [s1, s0];

        async ValueTask<SuperblockState?> ReadSlot(int slot) {
            var offset = SlotOffset(slot, slotLayoutVersion);
            if (file.Length < offset + SlotSize)
                return null;

            return await file.TryReadExact(offset, buffer, cancellationToken).ConfigureAwait(false)
                ? TryParseSlot(buffer, slot, formatVer, slotLayoutVersion)
                : null;
        }
    }

    private SuperblockState? TryParseSlot(
        ReadOnlySpan<byte> slot, int slotIndex, uint formatVer, uint slotLayoutVersion)
    {
        Span<byte> plain = stackalloc byte[PlaintextSize];
        Span<byte> aad = stackalloc byte[AadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(aad, formatVer);
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
        var dataAuthenticationFloor =
            BinaryPrimitives.ReadInt64LittleEndian(plain.Slice(DataAuthenticationFloorOffset, 8));
        var nextKeyId = BinaryPrimitives.ReadUInt64LittleEndian(plain.Slice(NextKeyIdOffset, 8));
        if (slotLayoutVersion < KvasarConstants.PreviousDataFormatVersion) {
            dataAuthenticationFloor = KvasarConstants.SegmentHeaderSize;
            nextKeyId = 1;
        }
        if (dataAuthenticationFloor < KvasarConstants.SegmentHeaderSize
            || dataAuthenticationFloor > dataCommitLength
            || nextKeyId == 0)
            return null;

        return new SuperblockState(
            generation,
            dataSlot,
            dataCommitLength,
            indexSlot,
            indexCommitLength,
            liveBytes,
            deadBytes,
            dataAuthenticationFloor,
            nextKeyId);
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
        if (_slotLayoutVersion >= KvasarConstants.PreviousDataFormatVersion) {
            BinaryPrimitives.WriteInt64LittleEndian(
                plain.Slice(DataAuthenticationFloorOffset, 8), state.DataAuthenticationFloor);
            BinaryPrimitives.WriteUInt64LittleEndian(plain.Slice(NextKeyIdOffset, 8), state.NextKeyId);
        }

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

    private static long SlotOffset(int slot, uint slotLayoutVersion)
        => HeaderSizeFor(slotLayoutVersion) + ((long)slot * SlotSize);

    private static int HeaderSizeFor(uint slotLayoutVersion)
        => slotLayoutVersion >= KvasarConstants.DataFormatVersion
            ? HeaderSize
            : PreviousHeaderSize;
}
