namespace ActualLab.Kvasar;

/// <summary>
/// Thrown when <c>&lt;base&gt;.kvs</c> is intact and current but its key check value does not match the
/// supplied master key. Distinct from <see cref="KvasarCorruptException"/> precisely so it never reaches
/// the wipe-and-recreate path: a mistyped key must not destroy an intact store (§3.1).
/// </summary>
public sealed class KvasarKeyException : Exception
{
    public KvasarKeyException() { }
    public KvasarKeyException(string? message) : base(message) { }
    public KvasarKeyException(string? message, Exception? innerException) : base(message, innerException) { }
}
