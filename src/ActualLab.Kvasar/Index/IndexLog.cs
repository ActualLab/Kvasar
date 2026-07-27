using System.Buffers.Binary;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A loaded <c>.kidx</c>: the resolved entries (tombstones included, for the caller to apply) plus the
/// <c>.kdat</c> extent the index is consistent up to.
/// </summary>
public readonly record struct IndexSnapshot(IndexEntry[] Entries, long DataCommitLength);

// Header layout (fixed 64 bytes, little-endian):
//   off size field
//    0    4  Magic             "KIDX" (KvasarConstants.KIdxMagic)
//    4    4  FormatVer         uint  — must match the caller's formatVer
//    8    4  EntrySize         uint  — sizeof(IndexEntry); structural sanity/identity check
//   12    4  LayoutVersion     uint  — .kidx entry-layout version
//   16    8  CheckpointCount   long  — # of IndexEntry in the checkpoint region
//   24    8  DataCommitLength  long  — the .kdat extent this index is consistent up to
//   32   32  Reserved          zero  — pad to 64
// Checkpoint/delta entries are cast straight to/from bytes with no byte-swapping, so the file is only
// portable across identical-endianness (little-endian) devices — acceptable for a single-device store (§2).
//
/// <summary>
/// The <c>&lt;base&gt;.N.kidx</c> index log (DESIGN-Durability.md §3.3): a 64-byte header, a blittable
/// checkpoint region, then an append-only delta tail. The index is a pure function of the data prefix, so
/// this file is <b>never flushed and never trusted</b> — a lost, torn or stale one costs replay time at
/// open and nothing else.
/// </summary>
public sealed class IndexLog : IAsyncDisposable
{
    public const int HeaderSize = 64;

    // Header field offsets.
    private const int MagicOffset = 0;
    private const int FormatVerOffset = 4;
    private const int EntrySizeOffset = 8;
    private const int LayoutVersionOffset = 12;
    private const int CheckpointCountOffset = 16;
    private const int DataCommitLengthOffset = 24;
    private const uint LayoutVersion = 2;

    // Deltas are staged here and written in one I/O: a Write per entry costs orders of magnitude more than
    // the memcpy, which is why v1 kept a FileStream open for the whole store's life.
    private const int MaxPendingBytes = 1 << 16;

    private readonly IStorageFile _file;
    private readonly uint _formatVer;
    private readonly byte[] _pending;
    private readonly int _pendingCapacity;
    private int _pendingCount;
    private long _flushedLength;

    public static int EntrySize => Unsafe.SizeOf<IndexEntry>();

    // Where the next delta lands: the adopted content end, staged deltas included.
    public long Length => _flushedLength + (_pendingCount * (long)EntrySize);

    public static async ValueTask<IndexLog> Open(
        IStorageFile file, uint formatVer, long committedLength = long.MaxValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegative(committedLength);
        try {
            var fileLength = file.Length;
            var flushedLength = fileLength;
            if (fileLength >= HeaderSize) {
                var header = new byte[HeaderSize];
                await file.ReadExact(0, header, cancellationToken).ConfigureAwait(false);
                if (TryReadHeader(header, formatVer, fileLength, out var checkpointCount, out _)) {
                    var checkpointEnd = HeaderSize + (checkpointCount * EntrySize);
                    flushedLength = checkpointEnd + ((fileLength - checkpointEnd) / EntrySize * EntrySize);
                }
            }
            flushedLength = Math.Min(flushedLength, committedLength);
            return new IndexLog(file, formatVer, flushedLength);
        }
        catch {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IndexLog(IStorageFile file, uint formatVer, long flushedLength)
    {
        _file = file;
        _formatVer = formatVer;
        _flushedLength = flushedLength;
        _pendingCapacity = Math.Max(1, MaxPendingBytes / EntrySize);
        _pending = new byte[_pendingCapacity * EntrySize];
    }

    public async ValueTask DisposeAsync()
    {
        try {
            await Flush().ConfigureAwait(false);
        }
        catch {
            // Ignored: the index is derivable from the data prefix, so a lost tail costs replay time only.
        }
        await _file.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask<IndexSnapshot?> Read(long validLength = -1, CancellationToken cancellationToken = default)
    {
        // Staged deltas are part of the log's content, so they must be on the file before it is parsed.
        await Flush().ConfigureAwait(false);

        var end = _file.Length;
        if (validLength >= 0)
            end = Math.Min(end, validLength);
        if (end < HeaderSize || end > Array.MaxLength)
            return null;

        var bytes = new byte[end];
        await _file.ReadExact(0, bytes, cancellationToken).ConfigureAwait(false);
        return Parse(bytes, _formatVer);
    }

    public ValueTask AppendDelta(IndexEntry entry)
    {
        MemoryMarshal.Write(_pending.AsSpan(_pendingCount * EntrySize), in entry);
        _pendingCount++;
        return _pendingCount == _pendingCapacity ? Flush() : default;
    }

    public async ValueTask Flush()
    {
        if (_pendingCount == 0)
            return;

        var byteCount = _pendingCount * EntrySize;
        var offset = _flushedLength;
        await _file.Write(offset, _pending.AsMemory(0, byteCount)).ConfigureAwait(false);
        _flushedLength = offset + byteCount;
        _pendingCount = 0;
    }

    public async ValueTask<long> WriteCheckpoint(ReadOnlyMemory<IndexEntry> liveEntries, long dataCommitLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataCommitLength);

        // Staged deltas are superseded by the checkpoint they'd be replayed on top of, so they're dropped.
        _pendingCount = 0;
        var buffer = BuildCheckpoint(liveEntries.Span, dataCommitLength, _formatVer);
        // Truncate first: a shorter checkpoint would otherwise leave the old tail behind, and whole entries
        // of it would parse as this checkpoint's deltas.
        await _file.Truncate(0).ConfigureAwait(false);
        await _file.Write(0, buffer).ConfigureAwait(false);
        _flushedLength = buffer.Length;
        return _flushedLength;
    }

    // Private methods

    private static IndexSnapshot? Parse(byte[] bytes, uint formatVer)
    {
        if (!TryReadHeader(bytes, formatVer, bytes.Length, out var checkpointCount, out var dataCommitLength))
            return null;

        var entrySize = EntrySize;
        var checkpointBytes = (int)checkpointCount * entrySize;
        var checkpointEnd = HeaderSize + checkpointBytes;
        var checkpoint = MemoryMarshal.Cast<byte, IndexEntry>(bytes.AsSpan(HeaderSize, checkpointBytes));

        // Delta tail: whole entries only. A trailing partial entry (an un-flushed write torn by a crash)
        // is dropped rather than treated as corruption.
        var deltaCount = (bytes.Length - checkpointEnd) / entrySize;
        var deltas = MemoryMarshal.Cast<byte, IndexEntry>(bytes.AsSpan(checkpointEnd, deltaCount * entrySize));

        var resolved = new Dictionary<(ulong KeyHash, ulong KeyId), IndexEntry>(
            checkpoint.Length + deltas.Length);
        foreach (var e in checkpoint)
            resolved[(e.KeyHash, e.KeyId)] = e;
        foreach (var e in deltas)
            resolved[(e.KeyHash, e.KeyId)] = e;

        var entries = new IndexEntry[resolved.Count];
        resolved.Values.CopyTo(entries, 0);
        return new IndexSnapshot(entries, dataCommitLength);
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> bytes, uint formatVer, long capacity,
        out long checkpointCount, out long dataCommitLength)
    {
        checkpointCount = 0;
        dataCommitLength = 0;
        if (bytes.Length < HeaderSize || capacity < HeaderSize)
            return false;

        var header = bytes[..HeaderSize];
        if (!header.Slice(MagicOffset, 4).SequenceEqual(KvasarConstants.KIdxMagic))
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[FormatVerOffset..]) != formatVer)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[EntrySizeOffset..]) != (uint)EntrySize)
            return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[LayoutVersionOffset..]) != LayoutVersion)
            return false;

        var count = BinaryPrimitives.ReadInt64LittleEndian(header[CheckpointCountOffset..]);
        // Compare against the capacity *before* multiplying: checkpointCount is attacker/corruption
        // controlled, and both `count * entrySize` and `HeaderSize + that` wrap for large values, sliding
        // a negative length past the guards and into a throwing Slice.
        if (count < 0 || count > (capacity - HeaderSize) / EntrySize)
            return false;

        var commitLength = BinaryPrimitives.ReadInt64LittleEndian(header[DataCommitLengthOffset..]);
        if (commitLength < 0)
            return false;

        checkpointCount = count;
        dataCommitLength = commitLength;
        return true;
    }

    private static byte[] BuildCheckpoint(
        ReadOnlySpan<IndexEntry> liveEntries, long dataCommitLength, uint formatVer)
    {
        var liveCount = 0;
        foreach (var e in liveEntries)
            if (!e.IsTombstone)
                liveCount++;

        var byteLength = HeaderSize + ((long)liveCount * EntrySize);
        if (byteLength > Array.MaxLength)
            throw new InvalidOperationException("Index checkpoint exceeds the maximum addressable size.");

        var buffer = new byte[byteLength];
        WriteHeader(buffer, formatVer, liveCount, dataCommitLength);

        var dst = MemoryMarshal.Cast<byte, IndexEntry>(buffer.AsSpan(HeaderSize));
        var i = 0;
        foreach (var e in liveEntries)
            if (!e.IsTombstone)
                dst[i++] = e;
        return buffer;
    }

    private static void WriteHeader(
        Span<byte> header, uint formatVer, long checkpointCount, long dataCommitLength)
    {
        header[..HeaderSize].Clear();
        KvasarConstants.KIdxMagic.CopyTo(header[MagicOffset..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[FormatVerOffset..], formatVer);
        BinaryPrimitives.WriteUInt32LittleEndian(header[EntrySizeOffset..], (uint)EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[LayoutVersionOffset..], LayoutVersion);
        BinaryPrimitives.WriteInt64LittleEndian(header[CheckpointCountOffset..], checkpointCount);
        BinaryPrimitives.WriteInt64LittleEndian(header[DataCommitLengthOffset..], dataCommitLength);
    }
}
