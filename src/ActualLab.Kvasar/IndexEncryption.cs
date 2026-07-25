namespace ActualLab.Kvasar;

/// <summary>
/// Controls whether the <c>.kidx</c> index snapshot is encrypted.
/// <see cref="Auto"/> encrypts it unless the configured hasher is a keyed PRF.
/// </summary>
public enum IndexEncryption
{
    Auto = 0,
    On,
    Off,
}
