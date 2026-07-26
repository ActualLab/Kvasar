namespace ActualLab.Kvasar.Internal.Storage;

/// <summary>
/// The real <see cref="IStorageBackend"/> — the local file system, reached through
/// <see cref="FileStorageFile"/>.
/// </summary>
public sealed class FileStorageBackend : IStorageBackend
{
    public static readonly FileStorageBackend Instance = new();

    public ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IStorageFile>(new FileStorageFile(path));
    }

    public bool Exists(string path)
        => File.Exists(path);

    public void Delete(string path)
        => File.Delete(path);

    public string[] ListFiles(string directoryPath, string searchPattern)
        => Directory.Exists(directoryPath)
            ? Directory.GetFiles(directoryPath, searchPattern)
            : [];
}
