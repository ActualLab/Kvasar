using System.Text;

namespace ActualLab.Kvasar.Tests;

public class KvasarKeyTests
{
    [Fact]
    public void ConvertsFromEveryShapeToTheSameBytes()
    {
        var expected = Encoding.UTF8.GetBytes("ключ/key");
        KvasarKey fromString = "ключ/key";
        KvasarKey fromChars = "ключ/key".ToCharArray();
        KvasarKey fromCharMemory = "ключ/key".AsMemory();
        KvasarKey fromBytes = expected;
        KvasarKey fromByteMemory = new ReadOnlyMemory<byte>(expected);

        fromString.ToArray().Should().Equal(expected);
        fromChars.Should().Be(fromString);
        fromCharMemory.Should().Be(fromString);
        fromBytes.Should().Be(fromString);
        fromByteMemory.Should().Be(fromString);
        fromString.AsString.Should().Be("ключ/key");
    }

    [Fact]
    public void ConvertsBackToMemoryWithoutCopying()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var key = new KvasarKey(bytes);
        ReadOnlyMemory<byte> memory = key;

        MemoryMarshal.TryGetArray(memory, out var segment).Should().BeTrue();
        segment.Array.Should().BeSameAs(bytes);
        key.Span.SequenceEqual(bytes).Should().BeTrue();
        key.Length.Should().Be(3);
    }

    [Fact]
    public void DefaultIsEmpty()
    {
        var key = default(KvasarKey);
        key.IsEmpty.Should().BeTrue();
        key.Length.Should().Be(0);
        key.AsString.Should().BeEmpty();
        key.Should().Be(new KvasarKey(Array.Empty<byte>()));
        ((KvasarKey)(byte[]?)null).Should().Be(key);
        ((KvasarKey)(string?)null).Should().Be(key);
    }

    [Fact]
    public void EqualityIsByContent()
    {
        KvasarKey a = "abc";
        KvasarKey b = Encoding.UTF8.GetBytes("abc");
        KvasarKey c = "abd";

        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Equals((object)b).Should().BeTrue();
        a.Equals((object?)null).Should().BeFalse();
    }
}
