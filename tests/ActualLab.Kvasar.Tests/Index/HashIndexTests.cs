using System.Collections.Concurrent;
using System.Threading;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Index;

public class HashIndexTests
{
    private static Locator Loc(ulong key) => new((uint)((key % 250) + 1), (uint)((key * 8) + 3));
    private static int Len(ulong key) => (int)((key % 1000) + 1);

    [Fact]
    public void SetGetUpdateRemoveManyRandom()
    {
        var index = new HashIndex(16);
        var oracle = new Dictionary<ulong, (Locator Loc, int Length)>();
        var rnd = new Random(1234);

        // Insert.
        for (var n = 0; n < 5000; n++) {
            var h = (ulong)rnd.NextInt64();
            if (h is HashIndex.Empty or HashIndex.Tombstone)
                continue;
            index.UpsertUniqueHash(h, Loc(h), Len(h));
            oracle[h] = (Loc(h), Len(h));
        }
        index.Count.Should().Be(oracle.Count);
        foreach (var (h, v) in oracle) {
            index.TryGetUniqueHash(h, out var loc, out var len).Should().BeTrue();
            loc.Should().Be(v.Loc);
            len.Should().Be(v.Length);
        }

        // Update half with a different locator/length.
        foreach (var h in oracle.Keys.Take(oracle.Count / 2).ToArray()) {
            var newLoc = new Locator((uint)((h % 17) + 1000), (uint)((h % 31) + 7));
            var newLen = (int)((h % 500) + 5000);
            index.UpsertUniqueHash(h, newLoc, newLen);
            oracle[h] = (newLoc, newLen);
        }
        index.Count.Should().Be(oracle.Count);
        foreach (var (h, v) in oracle) {
            index.TryGetUniqueHash(h, out var loc, out var len).Should().BeTrue();
            loc.Should().Be(v.Loc);
            len.Should().Be(v.Length);
        }

        // Remove a third of them.
        var toRemove = oracle.Keys.Take(oracle.Count / 3).ToArray();
        foreach (var h in toRemove) {
            index.Remove(h, oracle[h].Loc).Should().BeTrue();
            oracle.Remove(h);
        }
        index.Count.Should().Be(oracle.Count);
        foreach (var h in toRemove)
            index.TryGetUniqueHash(h, out _, out _).Should().BeFalse();
        foreach (var (h, v) in oracle) {
            index.TryGetUniqueHash(h, out var loc, out _).Should().BeTrue();
            loc.Should().Be(v.Loc);
        }
    }

    [Fact]
    public void RemoveWithWrongLocatorFails()
    {
        var index = new HashIndex(16);
        index.UpsertUniqueHash(42, new Locator(1, 100), 10);
        index.Remove(42, new Locator(9, 999)).Should().BeFalse();
        index.Count.Should().Be(1);
        index.Remove(42, new Locator(1, 100)).Should().BeTrue();
        index.Count.Should().Be(0);
        index.Remove(42, new Locator(1, 100)).Should().BeFalse();
    }

    [Fact]
    public void SameHashEntriesReplaceAndRemoveByExactLocator()
    {
        var index = new HashIndex(16);
        var first = new Locator(1, 100);
        var second = new Locator(1, 200);
        var replacement = new Locator(1, 300);

        index.Set(42, first, 10, Locator.None).Should().BeTrue();
        index.Set(42, second, 20, Locator.None).Should().BeTrue();
        index.Set(42, replacement, 30, first).Should().BeTrue();
        index.Remove(42, second).Should().BeTrue();

        index.Count.Should().Be(1);
        index.TryGetUniqueHash(42, out var loc, out var length).Should().BeTrue();
        loc.Should().Be(replacement);
        length.Should().Be(30);
    }

    [Fact]
    public void ForcedCollisionsProbeWalksAllCandidates()
    {
        // Small table (mask=15). Same home bucket (low 4 bits) AND same fingerprint (top 16 bits),
        // distinct full hashes => distinct slots forming one probe run.
        var index = new HashIndex(16);
        const ushort fp = 0xABCD;
        const int bucket = 0x5;
        var keys = new List<ulong>();
        for (var mid = 1; mid <= 5; mid++) {
            var h = ((ulong)fp << 48) | ((ulong)(uint)mid << 4) | bucket;
            keys.Add(h);
            index.UpsertUniqueHash(h, Loc(h), Len(h));
        }
        index.Count.Should().Be(5);

        // The cursor for any query with this fingerprint+bucket walks all 5 candidates.
        var seen = new List<ulong>();
        var cursor = index.Probe(keys[0]);
        while (cursor.MoveNext(out _, out _))
            seen.Add(cursor.CurrentHash);
        seen.Should().BeEquivalentTo(keys);

        // Exact lookup finds each with its own locator/length.
        foreach (var h in keys) {
            index.TryGetUniqueHash(h, out var loc, out var len).Should().BeTrue();
            loc.Should().Be(Loc(h));
            len.Should().Be(Len(h));
        }

        // A non-present hash sharing fingerprint+bucket: cursor still yields the 5 candidates,
        // exact lookup misses.
        var absent = ((ulong)fp << 48) | ((ulong)999u << 4) | bucket;
        index.TryGetUniqueHash(absent, out _, out _).Should().BeFalse();
        var probeCount = 0;
        var c2 = index.Probe(absent);
        while (c2.MoveNext(out _, out _))
            probeCount++;
        probeCount.Should().Be(5);

        // Remove a middle one => tombstone in the run; the rest still resolve past it.
        var removed = keys[2];
        index.Remove(removed, Loc(removed)).Should().BeTrue();
        index.TryGetUniqueHash(removed, out _, out _).Should().BeFalse();
        foreach (var h in keys.Where(k => k != removed))
            index.TryGetUniqueHash(h, out _, out _).Should().BeTrue();
        index.Count.Should().Be(4);
    }

    [Fact]
    public void ResizeKeepsAllEntriesResolvable()
    {
        var index = new HashIndex(16); // grows many times
        var oracle = new Dictionary<ulong, (Locator Loc, int Length)>();
        var rnd = new Random(777);
        while (oracle.Count < 2000) {
            var h = (ulong)rnd.NextInt64();
            if (h is HashIndex.Empty or HashIndex.Tombstone || oracle.ContainsKey(h))
                continue;
            index.UpsertUniqueHash(h, Loc(h), Len(h));
            oracle[h] = (Loc(h), Len(h));
        }
        index.Count.Should().Be(oracle.Count);
        foreach (var (h, v) in oracle) {
            index.TryGetUniqueHash(h, out var loc, out var len).Should().BeTrue();
            loc.Should().Be(v.Loc);
            len.Should().Be(v.Length);
        }
    }

    [Fact]
    public void TombstoneReuseDoesNotGrowUnbounded()
    {
        // Repeatedly set+remove distinct keys; the table should reclaim tombstones on rehash
        // rather than growing without bound.
        var index = new HashIndex(16);
        for (ulong h = 1; h <= 100_000; h++) {
            index.UpsertUniqueHash(h, Loc(h), Len(h));
            index.Remove(h, Loc(h)).Should().BeTrue();
        }
        index.Count.Should().Be(0);
        // A fresh insert still works and resolves.
        index.UpsertUniqueHash(123456, new Locator(3, 33), 7);
        index.TryGetUniqueHash(123456, out var loc, out var len).Should().BeTrue();
        loc.Should().Be(new Locator(3, 33));
        len.Should().Be(7);
    }

    [Fact]
    public void ClearResetsAndStaysUsable()
    {
        var index = new HashIndex(16);
        for (ulong h = 1; h <= 100; h++)
            index.UpsertUniqueHash(h, Loc(h), Len(h));
        index.Count.Should().Be(100);
        index.Clear();
        index.Count.Should().Be(0);
        index.TryGetUniqueHash(50, out _, out _).Should().BeFalse();
        index.UpsertUniqueHash(7, new Locator(2, 22), 3);
        index.Count.Should().Be(1);
        index.TryGetUniqueHash(7, out var loc, out _).Should().BeTrue();
        loc.Should().Be(new Locator(2, 22));
    }

    [Fact]
    public void BulkLoadAndSnapshotRoundTrip()
    {
        var rnd = new Random(9);
        var entries = new List<IndexEntry>();
        var keys = new HashSet<ulong>();
        while (keys.Count < 1000) {
            var h = (ulong)rnd.NextInt64();
            if (h is HashIndex.Empty or HashIndex.Tombstone || !keys.Add(h))
                continue;
            var locator = new Locator((uint)((h % 200) + 1), (long)((h % 9000) + 1));
            entries.Add(new IndexEntry {
                KeyHash = h,
                PackedLocator = locator.Packed,
                KeyId = locator.Packed,
                Length = (uint)((h % 777) + 1),
                Flags = 0,
            });
        }

        var index = new HashIndex();
        index.BulkLoad(CollectionsMarshal.AsSpan(entries));
        index.Count.Should().Be(entries.Count);
        foreach (var e in entries) {
            index.TryGetUniqueHash(e.KeyHash, out var loc, out var len).Should().BeTrue();
            loc.Should().Be(e.Locator);
            len.Should().Be((int)e.Length);
        }

        var snapshot = index.Snapshot().ToList();
        snapshot.Should().BeEquivalentTo(entries);

        // Round-trip: load the snapshot into a fresh index.
        var index2 = new HashIndex();
        index2.BulkLoad(CollectionsMarshal.AsSpan(snapshot));
        index2.Count.Should().Be(entries.Count);
        index2.Snapshot().Should().BeEquivalentTo(entries);
    }

    [Fact]
    public void BulkLoadSkipsTombstonesAndLastDupWins()
    {
        var entries = new List<IndexEntry> {
            new() { KeyHash = 100, PackedLocator = new Locator(1, 10).Packed, KeyId = 1, Length = 1, Flags = 0 },
            new() { KeyHash = 200, PackedLocator = new Locator(1, 20).Packed, KeyId = 2, Length = 2, Flags = (byte)RecordFlags.Tombstone },
            new() { KeyHash = 100, PackedLocator = new Locator(5, 50).Packed, KeyId = 1, Length = 9, Flags = 0 }, // later dup wins
            new() { KeyHash = 300, PackedLocator = new Locator(2, 30).Packed, KeyId = 3, Length = 3, Flags = 0 },
        };
        var index = new HashIndex();
        index.BulkLoad(CollectionsMarshal.AsSpan(entries));

        index.Count.Should().Be(2); // 100 (deduped) + 300; 200 was a tombstone
        index.TryGetUniqueHash(200, out _, out _).Should().BeFalse();
        index.TryGetUniqueHash(100, out var loc, out var len).Should().BeTrue();
        loc.Should().Be(new Locator(5, 50));
        len.Should().Be(9);
        index.TryGetUniqueHash(300, out _, out _).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void ConcurrentReadersSingleWriterNoTornReads()
    {
        // The writer only ever (re)sets a key to its ONE deterministic locator/length, or removes it.
        // So any reader that sees a full-hash match MUST see exactly that locator/length — any other
        // value would be a torn/garbage read. The writer varies which keys are present, not values.
        var index = new HashIndex(64);
        const int keySpace = 4000;
        for (var k = 1; k <= keySpace; k++) // pre-populate so readers have hits from the start
            index.UpsertUniqueHash((ulong)k, Loc((ulong)k), Len((ulong)k));

        using var stop = new CancellationTokenSource();
        var errors = new ConcurrentQueue<string>();
        var writerOps = 0L;

        var writer = new Thread(() => {
            var rnd = new Random(2024);
            while (!stop.IsCancellationRequested) {
                var k = (ulong)rnd.Next(1, keySpace + 1);
                if (rnd.Next(2) == 0)
                    index.UpsertUniqueHash(k, Loc(k), Len(k));
                else
                    index.Remove(k, Loc(k));
                Interlocked.Increment(ref writerOps);
            }
        });

        var readers = new Thread[4];
        for (var r = 0; r < readers.Length; r++) {
            readers[r] = new Thread(() => {
                var rnd = new Random(Environment.CurrentManagedThreadId);
                while (!stop.IsCancellationRequested) {
                    var k = (ulong)rnd.Next(1, keySpace + 1);
                    var cursor = index.Probe(k);
                    while (cursor.MoveNext(out var loc, out var len)) {
                        var h = cursor.CurrentHash;
                        // Full-hash verify stand-in: if the slot's hash is a real key, its published
                        // (locator, length) must equal the one and only value the writer uses for it.
                        if (h >= 1 && h <= keySpace) {
                            if (loc != Loc(h) || len != Len(h))
                                errors.Enqueue($"torn: key={h} loc={loc} len={len}");
                        }
                    }
                }
            });
        }

        writer.Start();
        foreach (var t in readers)
            t.Start();
        Thread.Sleep(2000);
        stop.Cancel();
        writer.Join();
        foreach (var t in readers)
            t.Join();

        errors.Should().BeEmpty();
        Interlocked.Read(ref writerOps).Should().BeGreaterThan(0);
    }
}
