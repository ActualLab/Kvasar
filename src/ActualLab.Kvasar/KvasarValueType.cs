namespace ActualLab.Kvasar;

/// <summary>
/// The 1-byte value type tag stored with every record (§4.3). v1 defines only <see cref="Raw"/>;
/// remaining values are reserved for future Redis-style typed values. An unknown tag ⇒ corrupt.
/// </summary>
public enum KvasarValueType : byte
{
    Raw = 0,
}
