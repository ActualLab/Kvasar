namespace ActualLab.Kvasar.Internal.Storage;

/// <summary>
/// One open file, as positional async I/O plus an explicit durability barrier. This is the only way
/// Kvasar touches file contents, so the device model a crash test needs and the real file system are
/// the same interface (<c>docs/DESIGN-Durability.md</c> §8).
/// </summary>
public interface IStorageFile : IAsyncDisposable
{
    public long Length { get; }

    public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default);
    // No token on the write path, by design: abandoning a write partway leaves a torn page that
    // recovery must then reason about. Callers guard the *wait* for the write lock instead.
    public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer);
    public ValueTask FlushToDisk();
    public ValueTask Truncate(long length);
}

public static class StorageFileExt
{
    public static async ValueTask ReadExact(
        this IStorageFile file, long offset, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (!await file.TryReadExact(offset, buffer, cancellationToken).ConfigureAwait(false))
            throw new KvasarCorruptException("Unexpected end of file.");
    }

    public static async ValueTask<bool> TryReadExact(
        this IStorageFile file, long offset, Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var total = 0;
        while (total < buffer.Length) {
            var read = await file.Read(offset + total, buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }
}
