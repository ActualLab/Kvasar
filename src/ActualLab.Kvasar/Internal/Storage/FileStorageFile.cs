using Microsoft.Win32.SafeHandles;

namespace ActualLab.Kvasar.Internal.Storage;

/// <summary>
/// An <see cref="IStorageFile"/> over a <see cref="SafeFileHandle"/> opened for positional async I/O,
/// so concurrent reads need no seek lock and a single writer can append at the tail.
/// </summary>
public sealed class FileStorageFile : IStorageFile
{
    private readonly SafeFileHandle _handle;
    private long _length;

    public long Length => Volatile.Read(ref _length);

    public FileStorageFile(string path)
    {
        // FileShare.Delete matters on Windows: without it, deleting the file fails while a handle is open.
        _handle = File.OpenHandle(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        _length = RandomAccess.GetLength(_handle);
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return default;
    }

    public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return RandomAccess.ReadAsync(_handle, buffer, offset, cancellationToken);
    }

    public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
    {
        // The tracked length is extended before the write is issued, so Length covers in-flight writes.
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Extend(offset + buffer.Length);
        return RandomAccess.WriteAsync(_handle, buffer, offset);
    }

    public ValueTask FlushToDisk()
    {
        // .NET has no async fsync, so the blocking syscall is offloaded off the caller's thread.
        return new ValueTask(Task.Run(() => RandomAccess.FlushToDisk(_handle)));
    }

    public ValueTask Truncate(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        RandomAccess.SetLength(_handle, length);
        Volatile.Write(ref _length, length);
        return default;
    }

    // Private methods

    private void Extend(long length)
    {
        var current = Volatile.Read(ref _length);
        while (current < length) {
            var old = Interlocked.CompareExchange(ref _length, length, current);
            if (old == current)
                return;

            current = old;
        }
    }
}
