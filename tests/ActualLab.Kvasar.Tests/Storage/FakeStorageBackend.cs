using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Storage;

/// <summary>
/// How a <see cref="FakeStorageBackend.PowerLoss"/> disposes of page-cache writes or faults the
/// selected <c>FlushToDisk</c>.
/// </summary>
public enum CrashMode
{
    // Nothing in the page cache reaches the device.
    LoseAll = 0,
    // One selected write may tear within a sector; the other writes land whole or vanish.
    Torn,
    // An arbitrary subset of whole writes reaches the device in a shuffled order.
    Reorder,
    // FlushToDisk returns success without promoting that file's page cache.
    LyingFsync,
    // FlushToDisk throws without promoting that file's page cache.
    FailingFsync,
}

/// <summary>
/// An in-RAM <see cref="IStorageBackend"/> with separate per-file device and OS page-cache images.
/// Writes change only the page cache; honest <c>FlushToDisk</c> promotes one file to the device.
/// Process kill preserves both layers, while power loss reconstructs the cache from surviving device
/// bytes and deterministic mode-selected writes (<c>docs/DESIGN-Durability.md</c> §6).
/// </summary>
public sealed class FakeStorageBackend : IStorageBackend
{
    private const int SectorSize = 512;
    private const int MaxFileLength = 1 << 26;

    private readonly ConcurrentDictionary<string, FileState> _files = new(StringComparer.Ordinal);

    public Func<string, long, int, int?>? WriteFailure { get; set; }
    public CrashMode? FlushFaultMode { get; set; }

    public ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _files.GetOrAdd(path, _ => new FileState());
        return new ValueTask<IStorageFile>(new FakeStorageFile(this, path, state));
    }

    public bool Exists(string path)
        => _files.ContainsKey(path);

    public void Delete(string path)
        => _files.TryRemove(path, out _);

    public string[] ListFiles(string directoryPath, string searchPattern)
        => _files.Keys
            .Where(x => string.Equals(Path.GetDirectoryName(x) ?? "", directoryPath, StringComparison.Ordinal)
                && IsMatch(Path.GetFileName(x), searchPattern))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    public void ProcessKill()
    {
        // The process owns neither layer, so killing it changes neither one.
    }

    public void PowerLoss(CrashMode mode, int seed = 0)
    {
        var random = new Random(seed);
        foreach (var state in OrderedStates()) {
            lock (state.Lock) {
                var device = state.Device.Clone();
                ApplyPowerLoss(device, state.PendingOps, mode, random);
                state.Device = device;
                state.PageCache = device.Clone();
                state.PendingOps.Clear();
            }
        }
    }

    public void Crash(CrashMode mode, int seed = 0)
        => PowerLoss(mode, seed);

    public byte[] GetDeviceBytes(string path)
    {
        var state = _files[path];
        lock (state.Lock)
            return state.Device.ToArray();
    }

    public byte[] GetStableBytes(string path)
        => GetDeviceBytes(path);

    public byte[] GetBytes(string path)
    {
        var state = _files[path];
        lock (state.Lock)
            return state.PageCache.ToArray();
    }

    public int GetFlushCount(string path)
    {
        var state = _files[path];
        lock (state.Lock)
            return state.FlushCount;
    }

    public void ResetFlushCounts()
    {
        foreach (var state in OrderedStates())
            lock (state.Lock)
                state.FlushCount = 0;
    }

    // Private methods

    private FileState[] OrderedStates()
        => _files
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Value)
            .ToArray();

    private static void ApplyPowerLoss(Image device, List<Op> pendingOps, CrashMode mode, Random random)
    {
        switch (mode) {
            case CrashMode.LoseAll:
            case CrashMode.LyingFsync:
            case CrashMode.FailingFsync:
                return;
            case CrashMode.Torn:
                var tornIndex = pendingOps.Count == 0 ? -1 : random.Next(pendingOps.Count);
                for (var i = 0; i < pendingOps.Count; i++) {
                    var op = pendingOps[i];
                    if (i == tornIndex)
                        ApplyTorn(device, op, random);
                    else if (random.Next(2) != 0)
                        op.ApplyTo(device);
                }
                return;
            case CrashMode.Reorder:
                var shuffled = pendingOps.ToList();
                Shuffle(shuffled, random);
                foreach (var op in shuffled) {
                    if (random.Next(4) != 0)
                        op.ApplyTo(device);
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void ApplyTorn(Image device, Op op, Random random)
    {
        var roll = random.Next(3);
        if (roll == 0) {
            op.ApplyTo(device);
            return;
        }
        if (roll == 1)
            return;

        if (op is not WriteOp { Data.Length: > 1 } write)
            return;

        var landedLength = random.Next(1, write.Data.Length);
        if (landedLength % SectorSize == 0)
            landedLength += landedLength + 1 < write.Data.Length ? 1 : -1;
        device.Write(write.Offset, write.Data.AsSpan(0, landedLength));
    }

    private static void Shuffle(List<Op> ops, Random random)
    {
        for (var i = ops.Count - 1; i > 0; i--) {
            var j = random.Next(i + 1);
            (ops[i], ops[j]) = (ops[j], ops[i]);
        }
    }

    private static int ToInt(long value)
        => value <= MaxFileLength
            ? (int)value
            : throw new ArgumentOutOfRangeException(nameof(value), "The fake device models small files only.");

    private static bool IsMatch(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        if (pattern.IsEmpty)
            return name.IsEmpty;
        if (pattern[0] == '*')
            return IsMatch(name, pattern[1..]) || (!name.IsEmpty && IsMatch(name[1..], pattern));
        if (name.IsEmpty)
            return false;
        if (pattern[0] == '?' || pattern[0] == name[0])
            return IsMatch(name[1..], pattern[1..]);

        return false;
    }

    // Nested types

    private sealed class FakeStorageFile(FakeStorageBackend backend, string path, FileState state) : IStorageFile
    {
        public long Length {
            get {
                lock (state.Lock)
                    return state.PageCache.Length;
            }
        }

        public ValueTask DisposeAsync()
            => default;

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            cancellationToken.ThrowIfCancellationRequested();
            lock (state.Lock)
                return new ValueTask<int>(state.PageCache.Read(ToInt(offset), buffer.Span));
        }

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            var at = ToInt(offset);
            _ = ToInt(offset + buffer.Length);
            if (backend.WriteFailure?.Invoke(path, offset, buffer.Length) is { } landedLength) {
                ArgumentOutOfRangeException.ThrowIfNegative(landedLength);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(landedLength, buffer.Length);
                lock (state.Lock) {
                    var landed = buffer[..landedLength].ToArray();
                    state.PendingOps.Add(new WriteOp(at, landed));
                    state.PageCache.Write(at, landed);
                }
                throw new IOException("Injected write failure.");
            }
            lock (state.Lock) {
                state.PendingOps.Add(new WriteOp(at, buffer.ToArray()));
                state.PageCache.Write(at, buffer.Span);
            }
            return default;
        }

        public ValueTask FlushToDisk()
        {
            lock (state.Lock) {
                state.FlushCount++;
                if (backend.FlushFaultMode == CrashMode.FailingFsync)
                    throw new IOException("Injected FlushToDisk failure.");
                if (backend.FlushFaultMode == CrashMode.LyingFsync)
                    return default;

                state.Device = state.PageCache.Clone();
                state.PendingOps.Clear();
            }
            return default;
        }

        public ValueTask Truncate(long length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            var at = ToInt(length);
            lock (state.Lock) {
                state.PendingOps.Add(new TruncateOp(at));
                state.PageCache.Truncate(at);
            }
            return default;
        }
    }

    private sealed class FileState
    {
        public readonly Lock Lock = new();
        public readonly List<Op> PendingOps = [];
        public Image Device = new([], 0);
        public Image PageCache = new([], 0);
        public int FlushCount;
    }

    // Bytes at [Length, capacity) are always zero, which is what makes a sparse write and a re-grown
    // truncate read back as zeros — exactly what a real file system guarantees.
    private sealed class Image(byte[] bytes, int length)
    {
        private byte[] _bytes = bytes;

        public int Length { get; private set; } = length;

        public Image Clone()
            => new(ToArray(), Length);

        public byte[] ToArray()
            => _bytes.AsSpan(0, Length).ToArray();

        public int Read(int offset, Span<byte> buffer)
        {
            if (offset >= Length)
                return 0;

            var count = Math.Min(buffer.Length, Length - offset);
            _bytes.AsSpan(offset, count).CopyTo(buffer);
            return count;
        }

        public void Write(int offset, ReadOnlySpan<byte> data)
        {
            EnsureCapacity(offset + data.Length);
            data.CopyTo(_bytes.AsSpan(offset));
            Length = Math.Max(Length, offset + data.Length);
        }

        public void Truncate(int length)
        {
            EnsureCapacity(length);
            if (length < Length)
                Array.Clear(_bytes, length, Length - length);
            Length = length;
        }

        private void EnsureCapacity(int capacity)
        {
            if (_bytes.Length < capacity)
                Array.Resize(ref _bytes, Math.Max(capacity, _bytes.Length * 2));
        }
    }

    private abstract class Op
    {
        public abstract void ApplyTo(Image image);
    }

    private sealed class WriteOp(int offset, byte[] data) : Op
    {
        public int Offset { get; } = offset;
        public byte[] Data { get; } = data;

        public override void ApplyTo(Image image)
            => image.Write(Offset, Data);
    }

    private sealed class TruncateOp(int length) : Op
    {
        public int Length { get; } = length;

        public override void ApplyTo(Image image)
            => image.Truncate(Length);
    }
}
