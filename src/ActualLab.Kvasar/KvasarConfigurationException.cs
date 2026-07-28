namespace ActualLab.Kvasar;

/// <summary>
/// Thrown when open-time options request storage geometry different from an existing store.
/// </summary>
public sealed class KvasarConfigurationException : ArgumentException
{
    public KvasarConfigurationException() { }
    public KvasarConfigurationException(string? message) : base(message) { }
    public KvasarConfigurationException(string? message, Exception? innerException)
        : base(message, innerException) { }
    public KvasarConfigurationException(string? message, string? paramName)
        : base(message, paramName) { }
    public KvasarConfigurationException(
        string? message, string? paramName, Exception? innerException)
        : base(message, paramName, innerException) { }
}
