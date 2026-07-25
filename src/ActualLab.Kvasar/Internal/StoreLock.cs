namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A single-process advisory lock over <c>&lt;base&gt;.lock</c> (§10). Fail-fast on contention: the file
/// is opened exclusively (<see cref="FileShare.None"/>), so a second opener throws.
/// </summary>
public sealed class StoreLock : IDisposable
{
    private readonly string _path;
    private FileStream? _stream;

    public StoreLock(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        try {
            _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException e) {
            // A distinct exception: lock contention must NOT be mistaken for corruption (which wipes data).
            throw new KvasarLockException($"The store '{path}' is already open in this or another process.", e);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
