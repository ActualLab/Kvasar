using System.Numerics;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// Shared, thread-safe, byte-budget-bounded LRU of decrypted <em>immutable</em> pages, keyed by
/// <c>(segmentId, pageId)</c> (§6.3, §6.5). Sharded so readers never block behind the single writer:
/// each shard has its own short-held lock and its own LRU list, giving approximate global LRU.
/// Cached <c>byte[]</c> pages must never be mutated after <see cref="Add"/>.
/// </summary>
public sealed class PageCache
{
    private const int MaxShardCount = 1 << 20;

    private readonly Shard[] _shards;
    private readonly int _shardMask;

    public long BudgetBytes { get; }

    public PageCache(long budgetBytes)
        : this(budgetBytes, DefaultShardCount())
    { }

    internal PageCache(long budgetBytes, int shardCount)
    {
        if (budgetBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(budgetBytes));

        shardCount = RoundUpPow2(Math.Max(1, shardCount));
        BudgetBytes = budgetBytes;
        _shards = new Shard[shardCount];
        _shardMask = shardCount - 1;

        var perShard = Math.Max(1, budgetBytes / shardCount);
        for (var i = 0; i < shardCount; i++)
            _shards[i] = new Shard(perShard);
        // Give the floor-division remainder to shard 0 so the sum matches the budget exactly.
        _shards[0].Budget += budgetBytes - perShard * shardCount;
    }

    public bool TryGet(uint segmentId, long pageId, [MaybeNullWhen(false)] out byte[] page)
    {
        var key = new PageKey(segmentId, pageId);
        var shard = ShardFor(key);
        lock (shard.Lock) {
            if (shard.Map.TryGetValue(key, out var node)) {
                shard.Lru.Remove(node);
                shard.Lru.AddFirst(node);
                page = node.Value.Page;
                return true;
            }
        }
        page = null;
        return false;
    }

    public void Add(uint segmentId, long pageId, byte[] page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var key = new PageKey(segmentId, pageId);
        var shard = ShardFor(key);
        lock (shard.Lock) {
            if (shard.Map.TryGetValue(key, out var existing)) {
                // Pages are immutable and content-addressed by (segment, page) — keep the first, refresh recency.
                shard.Lru.Remove(existing);
                shard.Lru.AddFirst(existing);
                return;
            }

            // Map first: if Add throws (OOM on a resize), the node never enters the LRU. The reverse order
            // would leave an unmapped, uncounted node that later evicts and drives Bytes negative — after
            // which the shard would never evict again.
            var node = new LinkedListNode<Entry>(new Entry(key, page));
            shard.Map.Add(key, node);
            shard.Lru.AddFirst(node);
            shard.Bytes += page.Length;

            while (shard.Bytes > shard.Budget && shard.Lru.Count > 1) {
                var tail = shard.Lru.Last!;
                shard.Lru.RemoveLast();
                shard.Map.Remove(tail.Value.Key);
                shard.Bytes -= tail.Value.Page.Length;
            }
        }
    }

    public void DropSegment(uint segmentId)
    {
        foreach (var shard in _shards) {
            lock (shard.Lock) {
                var node = shard.Lru.First;
                while (node != null) {
                    var next = node.Next;
                    if (node.Value.Key.SegmentId == segmentId) {
                        shard.Lru.Remove(node);
                        shard.Map.Remove(node.Value.Key);
                        shard.Bytes -= node.Value.Page.Length;
                    }
                    node = next;
                }
            }
        }
    }

    internal void Clear()
    {
        foreach (var shard in _shards) {
            lock (shard.Lock) {
                shard.Map.Clear();
                shard.Lru.Clear();
                shard.Bytes = 0;
            }
        }
    }

    internal long ByteCount {
        get {
            var total = 0L;
            foreach (var shard in _shards)
                lock (shard.Lock)
                    total += shard.Bytes;
            return total;
        }
    }

    internal int Count {
        get {
            var total = 0;
            foreach (var shard in _shards)
                lock (shard.Lock)
                    total += shard.Map.Count;
            return total;
        }
    }

    private Shard ShardFor(PageKey key) => _shards[key.GetHashCode() & _shardMask];

    private static int DefaultShardCount() => RoundUpPow2(Math.Min(256, Environment.ProcessorCount * 4));

    private static int RoundUpPow2(int value)
    {
        // Clamped before rounding: a naive doubling loop overflows past 2^30 to int.MinValue and then 0,
        // where `result < value` stays true forever — an infinite loop rather than an exception.
        if (value <= 1)
            return 1;
        return value >= MaxShardCount ? MaxShardCount : (int)BitOperations.RoundUpToPowerOf2((uint)value);
    }

    private sealed class Shard
    {
        public readonly object Lock = new();
        public readonly Dictionary<PageKey, LinkedListNode<Entry>> Map = new();
        public readonly LinkedList<Entry> Lru = new();
        public long Bytes;
        public long Budget;

        public Shard(long budget) => Budget = budget;
    }

    private readonly struct Entry
    {
        public readonly PageKey Key;
        public readonly byte[] Page;

        public Entry(PageKey key, byte[] page)
        {
            Key = key;
            Page = page;
        }
    }

    private readonly struct PageKey : IEquatable<PageKey>
    {
        public readonly uint SegmentId;
        public readonly long PageId;

        public PageKey(uint segmentId, long pageId)
        {
            SegmentId = segmentId;
            PageId = pageId;
        }

        public bool Equals(PageKey other) => SegmentId == other.SegmentId && PageId == other.PageId;
        public override bool Equals(object? obj) => obj is PageKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(SegmentId, PageId);
    }
}
