using ActualLab.Kvasar;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A decoded record (§5.2). <see cref="Key"/>/<see cref="Value"/> are memory slices — zero-copy into a
/// cached page for single-page records, a fresh buffer for multi-page runs (§5.2).
/// </summary>
public readonly struct RecordView
{
    public RecordFlags Flags { get; }
    public KvasarValueKind ValueKind { get; }
    public ReadOnlyMemory<byte> Key { get; }
    public ReadOnlyMemory<byte> Value { get; }
    public bool IsTombstone { get; }

    public RecordView(
        RecordFlags flags, KvasarValueKind valueKind,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone)
    {
        Flags = flags;
        ValueKind = valueKind;
        Key = key;
        Value = value;
        IsTombstone = isTombstone;
    }
}
