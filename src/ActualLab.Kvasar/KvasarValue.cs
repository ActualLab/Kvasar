using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar;

/// <summary>
/// A store value — raw bytes (<see cref="Memory"/>) tagged with a <see cref="KvasarValueKind"/> (§4.3).
/// Same conversions as <see cref="KvasarKey"/>, except every conversion out of a value requires a
/// matching <see cref="Kind"/>. A read returns a zero-copy slice into a cached page (§6.3).
/// </summary>
public readonly struct KvasarValue : IEquatable<KvasarValue>
{
    public ReadOnlyMemory<byte> Memory { get; }
    public KvasarValueKind Kind { get; }
    public ReadOnlySpan<byte> Span => Memory.Span;
    public int Length => Memory.Length;
    public bool IsEmpty => Memory.IsEmpty;

    public KvasarValue(ReadOnlyMemory<byte> memory)
        => Memory = memory;
    public KvasarValue(ReadOnlyMemory<char> chars)
        => Memory = KvasarEncoding.Encode(chars.Span);

    // Only the store may tag a value: an unknown kind on disk is corruption, so kinds enter the API
    // exclusively by being read back from a record.
    internal KvasarValue(ReadOnlyMemory<byte> memory, KvasarValueKind kind)
    {
        Memory = memory;
        Kind = kind;
    }

    public KvasarValue Require(KvasarValueKind kind)
        => Kind == kind
            ? this
            : throw new InvalidOperationException($"Expected a {kind} value, but got {Kind}.");

    public byte[] ToArray()
        => Memory.ToArray();

    public override string ToString()
        => $"{nameof(KvasarValue)}({Kind}, {Length} bytes)";

    // Conversion operators

    public static implicit operator KvasarValue(ReadOnlyMemory<byte> memory) => new(memory);
    public static implicit operator KvasarValue(ReadOnlyMemory<char> chars) => new(chars);
    public static implicit operator KvasarValue(byte[]? bytes) => new(bytes.AsMemory());
    public static implicit operator KvasarValue(char[]? chars) => new(chars.AsMemory());
    public static implicit operator KvasarValue(string? text) => new(text.AsMemory());
    public static implicit operator ReadOnlyMemory<byte>(KvasarValue value)
        => value.Require(KvasarValueKind.Raw).Memory;
    public static implicit operator ReadOnlySpan<byte>(KvasarValue value)
        => value.Require(KvasarValueKind.Raw).Memory.Span;

    // Equality

    public bool Equals(KvasarValue other)
        => Kind == other.Kind && Memory.Span.SequenceEqual(other.Memory.Span);
    public override bool Equals(object? obj)
        => obj is KvasarValue other && Equals(other);
    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.Add(Kind);
        hashCode.AddBytes(Memory.Span);
        return hashCode.ToHashCode();
    }

    public static bool operator ==(KvasarValue left, KvasarValue right) => left.Equals(right);
    public static bool operator !=(KvasarValue left, KvasarValue right) => !left.Equals(right);
}
