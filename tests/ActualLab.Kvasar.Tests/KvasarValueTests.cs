using System.Text;

namespace ActualLab.Kvasar.Tests;

public class KvasarValueTests
{
    [Fact]
    public void ConvertsFromEveryShapeToTheSameBytes()
    {
        var expected = Encoding.UTF8.GetBytes("значение/value");
        KvasarValue fromString = "значение/value";
        KvasarValue fromChars = "значение/value".ToCharArray();
        KvasarValue fromCharMemory = "значение/value".AsMemory();
        KvasarValue fromBytes = expected;
        KvasarValue fromByteMemory = new ReadOnlyMemory<byte>(expected);

        fromString.ToArray().Should().Equal(expected);
        fromChars.Should().Be(fromString);
        fromCharMemory.Should().Be(fromString);
        fromBytes.Should().Be(fromString);
        fromByteMemory.Should().Be(fromString);
        fromString.AsString.Should().Be("значение/value");
    }

    [Fact]
    public void EveryConstructedValueIsRaw()
    {
        default(KvasarValue).Kind.Should().Be(KvasarValueKind.Raw);
        new KvasarValue("x".AsMemory()).Kind.Should().Be(KvasarValueKind.Raw);
        ((KvasarValue)"x"u8.ToArray()).Require(KvasarValueKind.Raw).AsString.Should().Be("x");
    }

    [Fact]
    public void NullIsADeleteNotAnEmptyValue()
    {
        // The distinction the store relies on: KvasarValue? == null deletes, an empty value is present.
        KvasarValue? deleted = null;
        KvasarValue? empty = ReadOnlyMemory<byte>.Empty;

        deleted.HasValue.Should().BeFalse();
        empty.HasValue.Should().BeTrue();
        empty!.Value.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void EqualityIsByContent()
    {
        KvasarValue a = "abc";
        KvasarValue b = Encoding.UTF8.GetBytes("abc");
        KvasarValue c = "abd";

        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Equals((object)b).Should().BeTrue();
    }
}
