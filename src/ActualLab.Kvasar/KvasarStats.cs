namespace ActualLab.Kvasar;

public readonly record struct KvasarStats(
    long Entries,
    long LiveBytes,
    long DeadBytes,
    long FileBytes)
{
    public double DeadRatio => FileBytes > 0 ? (double)DeadBytes / FileBytes : 0d;
}
