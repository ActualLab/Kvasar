namespace ActualLab.Kvasar;

/// <summary>
/// Thrown when the store is in an unrecoverable state (bad magic/version/key, global auth failure,
/// unreadable index). The ActualChat adapter catches this to wipe &amp; recreate the file set (§8, §12).
/// </summary>
public sealed class KvasarCorruptException : Exception
{
    public KvasarCorruptException() { }
    public KvasarCorruptException(string? message) : base(message) { }
    public KvasarCorruptException(string? message, Exception? innerException) : base(message, innerException) { }
}
