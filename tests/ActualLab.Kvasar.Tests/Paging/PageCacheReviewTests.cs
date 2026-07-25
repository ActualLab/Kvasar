using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Paging;

/// <summary>
/// Adversarial checks for <see cref="PageCache"/>: byte-budget accounting under re-<c>Add</c>, eviction
/// of oversized pages, degenerate budgets, <c>DropSegment</c>, and concurrent mutation. Every assertion
/// pins down *current* behavior, including the places where the budget is deliberately approximate.
/// </summary>
public class PageCacheReviewTests
{
    private const int PageSize = 256;

    // --- Budget accounting ---------------------------------------------------

    [Fact]
    public void NegativeBudgetThrows()
    {
        var act = () => new PageCache(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ReAddOfSameKeyDoesNotDoubleCount()
    {
        var cache = new PageCache(1 << 20, shardCount: 1);
        var first = new byte[PageSize];
        cache.Add(1, 7, first);
        for (var i = 0; i < 10; i++)
            cache.Add(1, 7, new byte[PageSize]);

        cache.Count.Should().Be(1);
        cache.ByteCount.Should().Be(PageSize);
        cache.TryGet(1, 7, out var got).Should().BeTrue();
        got.Should().BeSameAs(first); // the first array wins; later ones are dropped
    }

    [Fact]
    public void ConcurrentAddOfSameKeyCountsOnce()
    {
        var cache = new PageCache(1 << 20, shardCount: 8);
        Parallel.For(0, 128, _ => cache.Add(3, 11, new byte[PageSize]));

        cache.Count.Should().Be(1);
        cache.ByteCount.Should().Be(PageSize);
    }

    [Fact]
    public void ReAddRefreshesRecency()
    {
        var cache = new PageCache(3 * PageSize, shardCount: 1);
        for (var i = 0; i < 3; i++)
            cache.Add(1, i, new byte[PageSize]);

        cache.Add(1, 0, new byte[PageSize]); // re-Add of a resident key must promote it to MRU
        cache.Add(1, 3, new byte[PageSize]);

        cache.TryGet(1, 0, out _).Should().BeTrue();
        cache.TryGet(1, 1, out _).Should().BeFalse(); // the LRU victim
        cache.ByteCount.Should().Be(3 * PageSize);
        cache.Count.Should().Be(3);
    }

    [Fact]
    public void ReAddAfterEvictionAccountsAgain()
    {
        var cache = new PageCache(2 * PageSize, shardCount: 1);
        for (var i = 0; i < 3; i++)
            cache.Add(1, i, new byte[PageSize]);
        cache.TryGet(1, 0, out _).Should().BeFalse();

        cache.Add(1, 0, new byte[PageSize]);
        cache.Count.Should().Be(2);
        cache.ByteCount.Should().Be(2 * PageSize);
    }

    // --- Oversized pages & degenerate budgets --------------------------------

    [Fact]
    public void OversizedPageSurvivesAndOverflowsBudget()
    {
        var cache = new PageCache(4 * PageSize, shardCount: 1);
        for (var i = 0; i < 4; i++)
            cache.Add(1, i, new byte[PageSize]);
        var huge = new byte[16 * PageSize];

        cache.Add(1, 100, huge);

        // The shard evicts everything else, then stops at one entry rather than looping forever.
        cache.Count.Should().Be(1);
        cache.TryGet(1, 100, out var got).Should().BeTrue();
        got.Should().BeSameAs(huge);
        cache.ByteCount.Should().Be(huge.Length);
        cache.ByteCount.Should().BeGreaterThan(cache.BudgetBytes);
    }

    [Fact]
    public void ZeroBudgetKeepsExactlyOnePage()
    {
        var cache = new PageCache(0, shardCount: 1);
        cache.BudgetBytes.Should().Be(0);
        for (var i = 0; i < 5; i++)
            cache.Add(1, i, new byte[PageSize]);

        cache.Count.Should().Be(1);
        cache.ByteCount.Should().Be(PageSize);
        cache.TryGet(1, 4, out _).Should().BeTrue(); // the most recent Add is never the eviction victim
    }

    [Fact]
    public void TinyBudgetOverCommitsByUpToOnePagePerShard()
    {
        const int shardCount = 16;
        var cache = new PageCache(PageSize - 1, shardCount: shardCount);
        for (var i = 0; i < 512; i++)
            cache.Add(1, i, new byte[PageSize]);

        // Each shard refuses to evict its last entry, so residency floors at one page per touched shard —
        // the budget can be exceeded by up to shardCount * pageSize.
        cache.Count.Should().BeInRange(1, shardCount);
        cache.ByteCount.Should().Be(cache.Count * (long)PageSize);
        cache.ByteCount.Should().BeGreaterThan(cache.BudgetBytes);
    }

    [Fact]
    public void NonPowerOfTwoShardCountIsRoundedUp()
    {
        var cache = new PageCache(1 << 20, shardCount: 3);
        for (var i = 0; i < 32; i++)
            cache.Add(1, i, new byte[PageSize]);

        cache.Count.Should().Be(32);
        for (var i = 0; i < 32; i++)
            cache.TryGet(1, i, out _).Should().BeTrue();
    }

    // --- Immutability & lifetime ---------------------------------------------

    [Fact]
    public void EvictedPageStaysIntactForItsHolder()
    {
        var cache = new PageCache(2 * PageSize, shardCount: 1);
        var victim = new byte[PageSize];
        victim.AsSpan().Fill(0xAB);
        cache.Add(1, 0, victim);
        for (var i = 1; i <= 8; i++)
            cache.Add(1, i, new byte[PageSize]);

        cache.TryGet(1, 0, out _).Should().BeFalse();
        victim.Should().OnlyContain(b => b == 0xAB); // eviction drops the reference, never touches the bytes
    }

    // --- DropSegment ---------------------------------------------------------

    [Fact]
    public void DropSegmentRemovesEveryPageAndItsBytes()
    {
        const int pageCount = 64;
        var cache = new PageCache(1 << 20, shardCount: 8);
        for (var i = 0; i < pageCount; i++) {
            cache.Add(1, i, new byte[PageSize]);
            cache.Add(2, i, new byte[PageSize]);
        }
        cache.Count.Should().Be(2 * pageCount);
        cache.ByteCount.Should().Be(2L * pageCount * PageSize);

        cache.DropSegment(1);

        cache.Count.Should().Be(pageCount);
        cache.ByteCount.Should().Be((long)pageCount * PageSize);
        for (var i = 0; i < pageCount; i++) {
            cache.TryGet(1, i, out _).Should().BeFalse();
            cache.TryGet(2, i, out _).Should().BeTrue();
        }

        cache.DropSegment(99); // unknown segment ⇒ no-op
        cache.Count.Should().Be(pageCount);
        cache.ByteCount.Should().Be((long)pageCount * PageSize);
    }

    [Fact]
    public void DropSegmentIsIdempotent()
    {
        var cache = new PageCache(1 << 20, shardCount: 4);
        for (var i = 0; i < 16; i++)
            cache.Add(5, i, new byte[PageSize]);

        cache.DropSegment(5);
        cache.DropSegment(5);

        cache.Count.Should().Be(0);
        cache.ByteCount.Should().Be(0);
    }

    // --- Concurrency ---------------------------------------------------------

    [Fact]
    public async Task ConcurrentAddGetDropKeepsAccountingConsistent()
    {
        const int shardCount = 8;
        const int workerCount = 4;
        const int opCount = 20_000;
        var cache = new PageCache(64 * PageSize, shardCount: shardCount);

        var workers = new List<Task>(workerCount);
        for (var t = 0; t < workerCount; t++) {
            var seed = 7_000 + t;
            workers.Add(Task.Run(() => {
                var rnd = new Random(seed);
                for (var i = 0; i < opCount; i++) {
                    var segmentId = (uint)rnd.Next(1, 4);
                    var pageId = rnd.Next(0, 96);
                    switch (rnd.Next(16)) {
                    case 0:
                        cache.DropSegment(segmentId);
                        break;
                    case < 9:
                        cache.Add(segmentId, pageId, new byte[PageSize]);
                        break;
                    default:
                        cache.TryGet(segmentId, pageId, out _);
                        break;
                    }
                }
            }));
        }
        await Task.WhenAll(workers);

        // Every page is PageSize bytes, so a map/LRU/byte-counter divergence shows up as a mismatch here.
        cache.ByteCount.Should().Be(cache.Count * (long)PageSize);
        cache.ByteCount.Should().BeGreaterThanOrEqualTo(0);
        cache.ByteCount.Should().BeLessThanOrEqualTo(cache.BudgetBytes + (long)shardCount * PageSize);
    }
}
