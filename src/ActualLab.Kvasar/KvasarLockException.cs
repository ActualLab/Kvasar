namespace ActualLab.Kvasar;

/// <summary>
/// Thrown when the store's advisory lock (<c>&lt;base&gt;.lock</c>) can't be acquired because the store is
/// already open in this or another process (§10). Distinct from <see cref="KvasarCorruptException"/> so it
/// never triggers the wipe-and-recreate path — a live store's data must not be destroyed by a second opener.
/// </summary>
public sealed class KvasarLockException : Exception
{
    public KvasarLockException() { }
    public KvasarLockException(string? message) : base(message) { }
    public KvasarLockException(string? message, Exception? innerException) : base(message, innerException) { }
}
