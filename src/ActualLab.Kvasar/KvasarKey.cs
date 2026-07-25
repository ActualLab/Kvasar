using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar;

/// <summary>
/// A store key — raw bytes (<see cref="Memory"/>) with implicit conversions from byte and char
/// memory, arrays, and strings; chars are UTF-8 encoded. Key identity is the byte content (§4.2).
/// </summary>
public readonly struct KvasarKey : IEquatable<KvasarKey>
{
    public ReadOnlyMemory<byte> Memory { get; }
    public ReadOnlySpan<byte> Span => Memory.Span;
    public int Length => Memory.Length;
    public bool IsEmpty => Memory.IsEmpty;

    public KvasarKey(ReadOnlyMemory<byte> memory)
        => Memory = memory;
    public KvasarKey(ReadOnlyMemory<char> chars)
        => Memory = KvasarEncoding.Encode(chars.Span);

    public byte[] ToArray()
        => Memory.ToArray();

    public override string ToString()
        => $"{nameof(KvasarKey)}({Length} bytes)";

    // Conversion operators

    public static implicit operator KvasarKey(ReadOnlyMemory<byte> memory) => new(memory);
    public static implicit operator KvasarKey(ReadOnlyMemory<char> chars) => new(chars);
    public static implicit operator KvasarKey(byte[]? bytes) => new(bytes.AsMemory());
    public static implicit operator KvasarKey(char[]? chars) => new(chars.AsMemory());
    public static implicit operator KvasarKey(string? text) => new(text.AsMemory());
    public static implicit operator ReadOnlyMemory<byte>(KvasarKey key) => key.Memory;
    public static implicit operator ReadOnlySpan<byte>(KvasarKey key) => key.Memory.Span;

    // Equality

    public bool Equals(KvasarKey other)
        => Memory.Span.SequenceEqual(other.Memory.Span);
    public override bool Equals(object? obj)
        => obj is KvasarKey other && Equals(other);
    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        hashCode.AddBytes(Memory.Span);
        return hashCode.ToHashCode();
    }

    public static bool operator ==(KvasarKey left, KvasarKey right) => left.Equals(right);
    public static bool operator !=(KvasarKey left, KvasarKey right) => !left.Equals(right);
}
