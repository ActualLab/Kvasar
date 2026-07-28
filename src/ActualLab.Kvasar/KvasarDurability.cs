namespace ActualLab.Kvasar;

/// <summary>
/// What a commit does about durability (see <c>docs/DESIGN-Durability.md</c> §2). Atomicity is
/// identical under both values — recovery validates every commit cryptographically either way — so
/// this chooses only how much recent work survives an OS-level crash.
/// </summary>
public enum KvasarDurability
{
    // FlushToDisk on the data file, then on the superblock after its slot write.
    Flushed = 0,
    // No FlushToDisk; recovery-time authentication substitutes for it. Identical to Flushed when the
    // process is killed but the OS survives, which is the dominant mobile crash.
    Buffered,
}
