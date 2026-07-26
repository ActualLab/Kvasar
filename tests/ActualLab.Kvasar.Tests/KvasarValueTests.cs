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
        fromChars.ToArray().Should().Equal(expected);
        fromCharMemory.ToArray().Should().Equal(expected);
        fromBytes.ToArray().Should().Equal(expected);
        fromByteMemory.ToArray().Should().Equal(expected);
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
}
