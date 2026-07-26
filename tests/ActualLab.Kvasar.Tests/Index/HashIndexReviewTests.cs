using System.Threading;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Index;

public class HashIndexReviewTests
{
    // H(i) pins the low bits (bucket) and the top 16 bits (fingerprint), so every H(*) shares one
    // probe run whatever the table capacity is — the worst case for clustering, probing and resize.
    private const int Capacity1K = 1024;
    private const int Threshold1K = (int)(Capacity1K * 0.7);

    private static ulong H(int i) => ((ulong)i << 20) | 5;
    // These tests mint far more synthetic ids than Locator.MaxFileId allows, so the per-key variation
    // lives in the offset. Locators stay unique because the offsets do — and Loc/FillerLoc offsets are
    // 7 and 3 mod 8, so the two families can never collide either.
    private static Locator Loc(ulong h)
        => new((uint)((h >> 20) % Locator.MaxFileId) + 1, ((long)(h >> 20) * 64) + 7);
    private static int Len(ulong h) => ((int)(h >> 20) * 3) + 1;
    private static ulong Filler(int j) => (0xBEEFUL << 48) | ((ulong)j << 20) | 9;
    private static Locator FillerLoc(int j)
        => new((uint)(j % Locator.MaxFileId) + 1, ((long)j * 8) + 11);

    [Fact]
    public void ProbeTerminatesAtMaxLoadFactor()
    {
        var index = new HashIndex(Capacity1K);
        for (var i = 1; i <= Threshold1K; i++)
            index.Set(H(i), Loc(H(i)), Len(H(i)));
        index.Count.Should().Be(Threshold1K);

        // The table sits exactly at its resize threshold and the whole run is one cluster; an absent
        // key with the same bucket+fingerprint must still walk it to the terminating empty slot.
        var probed = 0;
        var cursor = index.Probe(H(999_999));
        while (cursor.MoveNext(out _, out _))
            probed++;
        probed.Should().Be(Threshold1K);
        index.TryGetFirst(H(999_999), out _, out _).Should().BeFalse();

        for (var i = 1; i <= Threshold1K; i++) {
            index.TryGetFirst(H(i), out var loc, out var len).Should().BeTrue();
            loc.Should().Be(Loc(H(i)));
            len.Should().Be(Len(H(i)));
        }

        // One more key crosses the threshold and forces a rehash; nothing may be lost.
        index.Set(H(Threshold1K + 1), Loc(H(Threshold1K + 1)), Len(H(Threshold1K + 1)));
        index.Count.Should().Be(Threshold1K + 1);
        for (var i = 1; i <= Threshold1K + 1; i++)
            index.TryGetFirst(H(i), out _, out _).Should().BeTrue();
    }

    [Fact]
    public void TombstoneSaturatedTableStaysUsable()
    {
        var index = new HashIndex(Capacity1K);
        for (var i = 1; i <= Threshold1K; i++)
            index.Set(H(i), Loc(H(i)), Len(H(i)));
        for (var i = 1; i <= Threshold1K; i++)
            index.Remove(H(i), Loc(H(i))).Should().BeTrue();
        index.Count.Should().Be(0);

        // Every occupied slot is now a tombstone: the probe must skip them all and still terminate.
        var cursor = index.Probe(H(1));
        cursor.MoveNext(out _, out _).Should().BeFalse();
        index.Snapshot().Should().BeEmpty();

        // Fresh keys reuse the tombstones rather than the (still empty) tail of the cluster.
        for (var i = 1; i <= Threshold1K; i++) {
            var h = H(i + 100_000);
            index.Set(h, Loc(h), Len(h));
        }
        index.Count.Should().Be(Threshold1K);
        for (var i = 1; i <= Threshold1K; i++) {
            index.TryGetFirst(H(i), out _, out _).Should().BeFalse();
            var h = H(i + 100_000);
            index.TryGetFirst(h, out var loc, out var len).Should().BeTrue();
            loc.Should().Be(Loc(h));
            len.Should().Be(Len(h));
        }
    }

    [Fact]
    public void RemoveKeepsProbeChainIntact()
    {
        var index = new HashIndex(64);
        const int n = 32;
        for (var i = 1; i <= n; i++)
            index.Set(H(i), Loc(H(i)), Len(H(i)));

        // Delete the head of the run, then every other entry: open addressing must not orphan the rest.
        index.Remove(H(1), Loc(H(1))).Should().BeTrue();
        for (var i = 2; i <= n; i += 2)
            index.Remove(H(i), Loc(H(i))).Should().BeTrue();
        for (var i = 3; i <= n; i += 2) {
            index.TryGetFirst(H(i), out var loc, out _).Should().BeTrue();
            loc.Should().Be(Loc(H(i)));
        }
        index.Count.Should().Be(index.Snapshot().Count());

        // Re-adding the removed keys must land them in the same run, not duplicate them.
        index.Remove(H(1), Loc(H(1))).Should().BeFalse();
        index.Set(H(1), Loc(H(1)), Len(H(1)));
        for (var i = 2; i <= n; i += 2)
            index.Set(H(i), Loc(H(i)), Len(H(i)));
        index.Count.Should().Be(n);
        index.Snapshot().Count().Should().Be(n);
        for (var i = 1; i <= n; i++)
            index.TryGetFirst(H(i), out _, out _).Should().BeTrue();
    }

    [Fact]
    public void CursorTakenBeforeResizeKeepsSeeingItsSnapshot()
    {
        var index = new HashIndex(16); // Threshold == 11
        for (var i = 1; i <= 5; i++)
            index.Set(H(i), Loc(H(i)), Len(H(i)));
        var cursor = index.Probe(H(1)); // pins the 16-slot table

        for (var j = 1; j <= 7; j++) // the 7th filler crosses the threshold and rehashes
            index.Set(Filler(j), FillerLoc(j), j);
        for (var i = 1; i <= 5; i++)
            index.Remove(H(i), Loc(H(i))).Should().BeTrue();
        for (var i = 1; i <= 5; i++)
            index.TryGetFirst(H(i), out _, out _).Should().BeFalse();

        // The pinned snapshot is copy-on-write: it still resolves everything it held, fillers excluded
        // by fingerprint, and it terminates on the old table's empty slot.
        var seen = new List<ulong>();
        while (cursor.MoveNext(out var loc, out var len)) {
            seen.Add(cursor.CurrentHash);
            loc.Should().Be(Loc(cursor.CurrentHash));
            len.Should().Be(Len(cursor.CurrentHash));
        }
        seen.Should().BeEquivalentTo(Enumerable.Range(1, 5).Select(i => H(i)));
    }

    [Fact]
    public void CountNeverUnderflows()
    {
        var index = new HashIndex(16);
        index.Remove(H(1), Loc(H(1))).Should().BeFalse();
        index.Count.Should().Be(0);

        index.Set(H(1), Loc(H(1)), Len(H(1)));
        index.Remove(H(1), Loc(H(1))).Should().BeTrue();
        index.Remove(H(1), Loc(H(1))).Should().BeFalse();
        index.Count.Should().Be(0);

        index.Apply(new IndexEntry { KeyHash = H(2), Flags = (byte)RecordFlags.Tombstone });
        index.Apply(new IndexEntry { KeyHash = H(2), Flags = (byte)RecordFlags.Tombstone });
        index.Count.Should().Be(0);
        index.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void CountMatchesOracleUnderSetRemoveChurn()
    {
        var index = new HashIndex(16);
        var oracle = new Dictionary<ulong, Locator>();
        var rnd = new Random(4242);
        for (var step = 0; step < 60_000; step++) {
            var h = H(rnd.Next(1, 3000));
            if (rnd.Next(3) == 0) {
                if (oracle.TryGetValue(h, out var loc)) {
                    index.Remove(h, loc).Should().BeTrue();
                    oracle.Remove(h);
                }
                else
                    index.Remove(h, Loc(h)).Should().BeFalse();
            }
            else {
                index.Set(h, Loc(h), Len(h));
                oracle[h] = Loc(h);
            }
            index.Count.Should().Be(oracle.Count);
        }
        index.Snapshot().Count().Should().Be(oracle.Count);
        foreach (var (h, loc) in oracle) {
            index.TryGetFirst(h, out var actual, out _).Should().BeTrue();
            actual.Should().Be(loc);
        }
    }

    [Fact]
    public void BulkLoadResolvesDuplicatesAndSizesTheTable()
    {
        // 10 revisions per key, interleaved, so the entry count is 10x the distinct-key count: the
        // capacity is derived from the raw entry count, and the last revision must win.
        const int keyCount = 500;
        const int revisions = 10;
        var entries = new List<IndexEntry>(keyCount * revisions);
        for (var rev = 1; rev <= revisions; rev++) {
            for (var i = 1; i <= keyCount; i++)
                entries.Add(new IndexEntry {
                    KeyHash = H(i),
                    PackedLocator = new Locator((uint)rev, (i * 16) + rev).Packed,
                    Length = (uint)((i * 2) + rev),
                    Flags = 0,
                });
        }
        entries.Add(new IndexEntry { KeyHash = H(keyCount + 1), Flags = (byte)RecordFlags.Tombstone });

        var index = new HashIndex();
        index.BulkLoad(CollectionsMarshal.AsSpan(entries));
        index.Count.Should().Be(keyCount);
        index.Snapshot().Count().Should().Be(keyCount);
        index.TryGetFirst(H(keyCount + 1), out _, out _).Should().BeFalse();
        for (var i = 1; i <= keyCount; i++) {
            index.TryGetFirst(H(i), out var loc, out var len).Should().BeTrue();
            loc.Should().Be(new Locator(revisions, (uint)((i * 16) + revisions)));
            len.Should().Be((i * 2) + revisions);
        }

        var probed = 0;
        var cursor = index.Probe(H(999_999));
        while (cursor.MoveNext(out _, out _))
            probed++;
        probed.Should().Be(keyCount);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void StableKeysAreNeverMissedWhileTheWriterResizes()
    {
        // The stable keys are never touched by the writer, so a lock-free reader must find every one of
        // them on every probe, no matter how many resizes / tombstone reuses happen concurrently.
        const int stableCount = 100;
        const int churnFrom = 200;
        const int churnTo = 2000;
        var index = new HashIndex(16);
        for (var i = 1; i <= stableCount; i++)
            index.Set(H(i), Loc(H(i)), Len(H(i)));

        using var stop = new CancellationTokenSource();
        var misses = 0L;
        var wrong = 0L;
        var ops = 0L;
        var writer = new Thread(() => {
            var rnd = new Random(11);
            while (!stop.IsCancellationRequested) {
                var k = rnd.Next(churnFrom, churnTo);
                if ((k & 1) == 0)
                    index.Set(H(k), Loc(H(k)), Len(H(k)));
                else
                    index.Remove(H(k), Loc(H(k)));
                Interlocked.Increment(ref ops);
            }
        });

        var readers = new Thread[8];
        for (var r = 0; r < readers.Length; r++) {
            readers[r] = new Thread(() => {
                var rnd = new Random(Environment.CurrentManagedThreadId);
                while (!stop.IsCancellationRequested) {
                    var i = rnd.Next(1, stableCount + 1);
                    var found = false;
                    var cursor = index.Probe(H(i));
                    while (cursor.MoveNext(out var loc, out _)) {
                        if (cursor.CurrentHash != H(i))
                            continue;
                        found = true;
                        if (loc != Loc(H(i)))
                            Interlocked.Increment(ref wrong);
                    }
                    if (!found)
                        Interlocked.Increment(ref misses);
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

        Interlocked.Read(ref ops).Should().BeGreaterThan(0);
        Interlocked.Read(ref misses).Should().Be(0);
        Interlocked.Read(ref wrong).Should().Be(0);
        for (var i = 1; i <= stableCount; i++)
            index.TryGetFirst(H(i), out _, out _).Should().BeTrue();
    }
}
