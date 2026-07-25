using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Paging;

public class PageCacheTests
{
    [Fact]
    public void TryGetHitAndMiss()
    {
        var cache = new PageCache(1 << 20, shardCount: 1);
        var page = new byte[256];
        cache.Add(1, 10, page);

        cache.TryGet(1, 10, out var got).Should().BeTrue();
        got.Should().BeSameAs(page);
        cache.TryGet(1, 11, out _).Should().BeFalse();
        cache.TryGet(2, 10, out _).Should().BeFalse();
    }

    [Fact]
    public void EvictionRespectsByteBudget()
    {
        const int pageSize = 256;
        var cache = new PageCache(3 * pageSize, shardCount: 1);
        for (var i = 0; i < 4; i++)
            cache.Add(0, i, new byte[pageSize]);

        cache.ByteCount.Should().BeLessThanOrEqualTo(3 * pageSize);
        cache.Count.Should().Be(3);
        cache.TryGet(0, 0, out _).Should().BeFalse(); // LRU victim
        cache.TryGet(0, 3, out _).Should().BeTrue();  // most recent survives
    }

    [Fact]
    public void RecentUseAvoidsEviction()
    {
        const int pageSize = 256;
        var cache = new PageCache(3 * pageSize, shardCount: 1);
        for (var i = 0; i < 3; i++)
            cache.Add(0, i, new byte[pageSize]);

        cache.TryGet(0, 0, out _).Should().BeTrue(); // touch page 0 -> becomes MRU
        cache.Add(0, 3, new byte[pageSize]);         // evicts LRU, which is now page 1

        cache.TryGet(0, 0, out _).Should().BeTrue();
        cache.TryGet(0, 1, out _).Should().BeFalse();
    }

    [Fact]
    public void DropSegmentClearsOnlyThatSegment()
    {
        var cache = new PageCache(1 << 20, shardCount: 4);
        for (var i = 0; i < 10; i++) {
            cache.Add(1, i, new byte[128]);
            cache.Add(2, i, new byte[128]);
        }

        cache.DropSegment(1);

        for (var i = 0; i < 10; i++) {
            cache.TryGet(1, i, out _).Should().BeFalse();
            cache.TryGet(2, i, out _).Should().BeTrue();
        }
    }
}
