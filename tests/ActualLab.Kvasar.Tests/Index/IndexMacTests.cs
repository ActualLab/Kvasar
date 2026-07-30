using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Index;

public sealed class IndexMacTests
{
    private const int BlockSize = IndexMac.BlockSize;

    private static readonly byte[] Key = Bytes(32, 11);
    private static readonly byte[] Header = Bytes(32, 22);
    private static readonly byte[] Context = Bytes(16, 33);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(BlockSize - 1)]
    [InlineData(BlockSize)]
    [InlineData(BlockSize + 1)]
    [InlineData((3 * BlockSize) + 77)]
    public void AppendGranularityDoesNotChangeTheTag(int length)
    {
        // The writer feeds 32 bytes at a time and the verifier feeds the whole prefix in one call, so the
        // scheme is only sound if every chunking of the same bytes folds into the same chain.
        var body = Bytes(length, 1);
        var expected = Tag(mac => mac.Append(body));

        int[] chunkSizes = [1, 7, 32, 1024, BlockSize - 1, BlockSize, BlockSize + 1];
        foreach (var chunkSize in chunkSizes) {
            var tag = Tag(mac => {
                for (var offset = 0; offset < body.Length; offset += chunkSize)
                    mac.Append(body.AsSpan(offset, Math.Min(chunkSize, body.Length - offset)));
            });
            tag.Should().Equal(expected, $"chunk size {chunkSize} must fold to the same chain");
        }
    }

    [Fact]
    public void ComputeTagLeavesTheOpenBlockAppendable()
    {
        // This is the operation Android cannot do with a Mac object, and the one every commit performs.
        var committed = Bytes(BlockSize + 100, 1);
        var appended = Bytes(50, 2);

        using var mac = new IndexMac(Key, Header, Context);
        mac.Append(committed);
        var first = ComputeTag(mac);
        ComputeTag(mac).Should().Equal(first, "computing a tag must not consume the chain");

        mac.Append(appended);
        var second = ComputeTag(mac);
        second.Should().NotEqual(first);
        Tag(m => m.Append([..committed, ..appended])).Should().Equal(second);
    }

    [Fact]
    public void EveryPrefixLengthGetsItsOwnTag()
    {
        var body = Bytes((2 * BlockSize) + 64, 1);
        var lengths = new List<int> { 0, 1, 2, body.Length };
        for (var delta = -2; delta <= 2; delta++) {
            lengths.Add(BlockSize + delta);
            lengths.Add((2 * BlockSize) + delta);
        }

        var tags = lengths
            .Distinct()
            .Select(n => Convert.ToHexString(Tag(mac => mac.Append(body.AsSpan(0, n)))))
            .ToList();
        tags.Distinct().Should().HaveCount(tags.Count, "the tag binds the exact committed length");
    }

    [Fact]
    public void BlockOrderIsBound()
    {
        var first = Bytes(BlockSize, 1);
        var second = Bytes(BlockSize, 2);

        Tag(mac => { mac.Append(first); mac.Append(second); })
            .Should().NotEqual(Tag(mac => { mac.Append(second); mac.Append(first); }));
    }

    [Fact]
    public void KeyHeaderAndContextAreAllBound()
    {
        var body = Bytes(BlockSize + 33, 1);
        var baseline = Tag(mac => mac.Append(body));

        Tag(mac => mac.Append(body), key: Flip(Key)).Should().NotEqual(baseline);
        Tag(mac => mac.Append(body), header: Flip(Header)).Should().NotEqual(baseline);
        Tag(mac => mac.Append(body), context: Flip(Context)).Should().NotEqual(baseline);
        Tag(mac => mac.Append(body), context: []).Should().NotEqual(baseline);
    }

    [Fact]
    public void ASingleBitFlipAnywhereChangesTheTag()
    {
        var body = Bytes((3 * BlockSize) + 7, 1);
        var baseline = Tag(mac => mac.Append(body));

        int[] positions = [
            0, 1, BlockSize - 1, BlockSize, BlockSize + 1,
            2 * BlockSize, 3 * BlockSize, body.Length - 1,
        ];
        foreach (var position in positions) {
            var mutated = body.ToArray();
            mutated[position] ^= 0x80;
            Tag(mac => mac.Append(mutated)).Should().NotEqual(baseline, $"byte {position}");
        }
    }

    [Fact]
    public void AContextThatCannotFitOneBlockIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => new IndexMac(Key, Header, new byte[BlockSize]).Dispose());
        Assert.Throws<ArgumentNullException>(
            () => new IndexMac(null!, Header, Context).Dispose());
    }

    // Private methods

    private static byte[] Tag(
        Action<IndexMac> append, byte[]? key = null, byte[]? header = null, byte[]? context = null)
    {
        using var mac = new IndexMac(key ?? Key, header ?? Header, context ?? Context);
        append(mac);
        return ComputeTag(mac);
    }

    private static byte[] ComputeTag(IndexMac mac)
    {
        var tag = new byte[IndexMac.TagSize];
        mac.ComputeTag(tag);
        return tag;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Flip(byte[] bytes)
    {
        var result = bytes.ToArray();
        result[0] ^= 0x01;
        return result;
    }
}
