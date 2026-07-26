using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Storage;

/// <summary>
/// How a <see cref="FakeStorageBackend.Crash"/> disposes of the volatile (unflushed) writes.
/// </summary>
public enum CrashMode
{
    // Nothing unflushed survives.
    LoseAll = 0,
    // Each write independently applies fully, vanishes, or applies a random prefix of its bytes.
    Torn,
    // Writes apply in a shuffled order, and some are dropped.
    Reorder,
}

/// <summary>
/// An in-RAM <see cref="IStorageBackend"/> modelling a device with a write cache: writes land in
/// volatile state, <c>FlushToDisk</c> promotes volatile to stable, and <see cref="Crash"/> discards the
/// volatile state — optionally tearing or reordering it first (<c>docs/DESIGN-Durability.md</c> §8).
/// </summary>
public sealed class FakeStorageBackend : IStorageBackend
{
    private const int MaxFileLength = 1 << 26;

    private readonly ConcurrentDictionary<string, FileState> _files = new(StringComparer.Ordinal);

    public ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _files.GetOrAdd(path, _ => new FileState());
        return new ValueTask<IStorageFile>(new FakeStorageFile(state));
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
        // The process dies but the OS (and its page cache) survives, so nothing unflushed is lost.
        foreach (var state in OrderedStates()) {
            lock (state.Lock) {
                state.Stable = state.Merged.Clone();
                state.Ops.Clear();
            }
        }
    }

    public void Crash(CrashMode mode, int seed = 0)
    {
        var random = new Random(seed);
        foreach (var state in OrderedStates()) {
            lock (state.Lock) {
                var image = state.Stable.Clone();
                ApplyCrash(image, state.Ops, mode, random);
                state.Stable = image;
                state.Merged = image.Clone();
                state.Ops.Clear();
            }
        }
    }

    public byte[] GetStableBytes(string path)
    {
        var state = _files[path];
        lock (state.Lock)
            return state.Stable.ToArray();
    }

    public byte[] GetBytes(string path)
    {
        var state = _files[path];
        lock (state.Lock)
            return state.Merged.ToArray();
    }

    // Private methods

    private FileState[] OrderedStates()
        => _files
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Value)
            .ToArray();

    private static void ApplyCrash(Image image, List<Op> ops, CrashMode mode, Random random)
    {
        switch (mode) {
            case CrashMode.LoseAll:
                return;
            case CrashMode.Torn:
                foreach (var op in ops)
                    ApplyTorn(image, op, random);
                return;
            case CrashMode.Reorder:
                var shuffled = ops.ToList();
                Shuffle(shuffled, random);
                foreach (var op in shuffled) {
                    if (random.Next(4) != 0)
                        op.ApplyTo(image);
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void ApplyTorn(Image image, Op op, Random random)
    {
        var roll = random.Next(3);
        if (roll == 0) {
            op.ApplyTo(image);
            return;
        }
        if (roll == 1)
            return;

        // A partial write lands as a prefix of the buffer; a truncate has no partial form, so it drops.
        if (op is WriteOp write && write.Data.Length > 1)
            image.Write(write.Offset, write.Data.AsSpan(0, random.Next(1, write.Data.Length)));
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

    private sealed class FakeStorageFile(FileState state) : IStorageFile
    {
        public long Length {
            get {
                lock (state.Lock)
                    return state.Merged.Length;
            }
        }

        public ValueTask DisposeAsync()
            => default;

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            cancellationToken.ThrowIfCancellationRequested();
            lock (state.Lock)
                return new ValueTask<int>(state.Merged.Read(ToInt(offset), buffer.Span));
        }

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            var at = ToInt(offset);
            _ = ToInt(offset + buffer.Length);
            lock (state.Lock) {
                state.Ops.Add(new WriteOp(at, buffer.ToArray()));
                state.Merged.Write(at, buffer.Span);
            }
            return default;
        }

        public ValueTask FlushToDisk()
        {
            lock (state.Lock) {
                state.Stable = state.Merged.Clone();
                state.Ops.Clear();
            }
            return default;
        }

        public ValueTask Truncate(long length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            var at = ToInt(length);
            lock (state.Lock) {
                state.Ops.Add(new TruncateOp(at));
                state.Merged.Truncate(at);
            }
            return default;
        }
    }

    private sealed class FileState
    {
        public readonly Lock Lock = new();
        public readonly List<Op> Ops = [];
        public Image Stable = new([], 0);
        public Image Merged = new([], 0);
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
