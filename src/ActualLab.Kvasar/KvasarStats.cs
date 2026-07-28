namespace ActualLab.Kvasar;

public readonly record struct KvasarStats(
    long Entries,
    long LiveBytes,
    long DeadBytes,
    long FileBytes,
    long FallbackRecoveries = 0)
{
    public double DeadRatio => FileBytes > 0 ? (double)DeadBytes / FileBytes : 0d;
}
