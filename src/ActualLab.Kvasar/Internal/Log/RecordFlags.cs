namespace ActualLab.Kvasar.Internal;

/// <summary>Per-record flag bits stored in the record header (§5.2) and mirrored in <see cref="IndexEntry"/>.</summary>
[Flags]
public enum RecordFlags : byte
{
    None = 0,
    // A tombstone (delete): no value bytes follow.
    Tombstone = 1,
}
