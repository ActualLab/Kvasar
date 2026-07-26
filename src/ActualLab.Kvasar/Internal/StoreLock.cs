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
        catch (IOException e) when (IsLockContention(e)) {
            // A distinct exception: lock contention must NOT be mistaken for corruption (which wipes data).
            // Any other I/O failure (bad path, full disk) propagates as-is — it isn't a lock conflict.
            throw new KvasarLockException($"The store '{path}' is already open in this or another process.", e);
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }

    // Private methods

    private static bool IsLockContention(IOException error)
    {
        // Windows reports a share-mode conflict as ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33)
        // in the low HResult word. Unix emulates FileShare.None with a non-blocking flock, so a conflict
        // surfaces as a bare EWOULDBLOCK errno in HResult (11 on Linux, 35 on macOS); every other Unix I/O
        // failure comes back mapped to a 0x8007xxxx HResult instead.
        return OperatingSystem.IsWindows()
            ? (error.HResult & 0xFFFF) is 32 or 33
            : error.HResult is 11 or 35;
    }
}
