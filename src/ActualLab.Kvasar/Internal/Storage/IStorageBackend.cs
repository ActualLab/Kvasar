namespace ActualLab.Kvasar.Internal.Storage;

/// <summary>
/// Opens <see cref="IStorageFile"/>s and answers the few directory-level questions Kvasar asks.
/// Kvasar creates its files once, at store creation, and never renames or unlinks one while the
/// store is open — so this deliberately offers no rename (<c>docs/DESIGN-Durability.md</c> §3.4).
/// </summary>
public interface IStorageBackend
{
    // Opens the file, creating it if it does not exist.
    public ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default);
    public bool Exists(string path);
    public void Delete(string path);
    public string[] ListFiles(string directoryPath, string searchPattern);
}
