using ActualLab.Kvasar;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// A decoded record (§5.2). <see cref="Key"/>/<see cref="Value"/> are memory slices — zero-copy into a
/// cached page for single-page records, a fresh buffer for multi-page runs (§5.2).
/// </summary>
public readonly struct RecordView
{
    public RecordFlags Flags { get; }
    public KvasarValueType ValType { get; }
    public ReadOnlyMemory<byte> Key { get; }
    public ReadOnlyMemory<byte> Value { get; }
    public bool IsTombstone { get; }

    public RecordView(
        RecordFlags flags, KvasarValueType valType,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, bool isTombstone)
    {
        Flags = flags;
        ValType = valType;
        Key = key;
        Value = value;
        IsTombstone = isTombstone;
    }
}
