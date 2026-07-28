namespace ActualLab.Kvasar;

/// <summary>
/// Thrown when neither the superblock nor a current-format data page can establish the supplied
/// master key. Distinct from <see cref="KvasarCorruptException"/> so a mistyped key never reaches the
/// wipe-and-recreate path (§3.1).
/// </summary>
public sealed class KvasarKeyException : Exception
{
    public KvasarKeyException() { }
    public KvasarKeyException(string? message) : base(message) { }
    public KvasarKeyException(string? message, Exception? innerException) : base(message, innerException) { }
}
