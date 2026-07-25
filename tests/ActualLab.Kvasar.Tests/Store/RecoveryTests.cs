using System.IO;
using System.Linq;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Crash-recovery / torn-tail / reopen suite (§8 durability, §6.5 .kidx startup, §10 lifecycle).
/// Each test uses an isolated temp directory and a fixed 32-byte key.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public RecoveryTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 7 + 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    // --- Helpers ------------------------------------------------------------

    private string BasePath => Path.Combine(_dir, "store");

    private KvasarOptions Options(bool encrypt = false, int pageSize = 512, long segmentBytes = 8 * 1024) => new() {
        BasePath = BasePath,
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = pageSize,
        SegmentBytes = segmentBytes,
    };

    private static byte[] K(int i) => Encoding.UTF8.GetBytes($"k{i:D6}");
    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] V(int i, int size)
    {
        var seed = Encoding.UTF8.GetBytes($"value-{i:D6}-");
        var buf = new byte[size];
        for (var j = 0; j < size; j++)
            buf[j] = seed[j % seed.Length];
        return buf;
    }

    private static async Task<Dictionary<string, byte[]>> ScanToMap(KvasarStore store)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var (key, value) in store.Scan())
            map.Add(Encoding.UTF8.GetString(key.Span), value.ToArray());
        return map;
    }

    private static async Task AssertMatches(KvasarStore store, Dictionary<string, byte[]> oracle)
    {
        var actual = await ScanToMap(store);
        actual.Should().HaveCount(oracle.Count);
        foreach (var (k, v) in oracle) {
            actual.Should().ContainKey(k);
            actual[k].Should().Equal(v);
            var got = await store.Get(K(k));
            got.Should().NotBeNull();
            got!.Value.ToArray().Should().Equal(v);
        }
    }

    private string[] SegmentFiles()
    {
        var name = Path.GetFileName(BasePath);
        return Directory.GetFiles(_dir, name + ".*.klog");
    }

    private uint SegmentId(string path)
    {
        var prefix = Path.GetFileName(BasePath) + ".";
        var fn = Path.GetFileName(path);
        var mid = fn.Substring(prefix.Length, fn.Length - prefix.Length - ".klog".Length);
        return uint.Parse(mid);
    }

    private string HighestSegmentFile() => SegmentFiles().OrderBy(SegmentId).Last();

    // --- 1. Reopen preserves state -----------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // at least one encrypted reopen path
    public async Task ReopenPreservesState(bool encrypt)
    {
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        int[] sizes = [0, 1, 37, 300, 600, 1500, 5000]; // incl. empty + multi-page (> PageSize=512)

        await using (var store = await KvasarStore.Open(Options(encrypt))) {
            // Initial writes, mixed sizes.
            for (var i = 0; i < 200; i++) {
                var size = sizes[i % sizes.Length];
                var v = V(i, size);
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            // Overwrites (some grow across page boundaries, some shrink).
            for (var i = 0; i < 200; i += 3) {
                var size = sizes[(i + 4) % sizes.Length];
                var v = V(i + 100000, size);
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            // Deletes.
            for (var i = 1; i < 200; i += 7) {
                await store.Set(K(i), null);
                oracle.Remove(Encoding.UTF8.GetString(K(i)));
            }
            // A batch write (last dup wins) + an explicit empty value.
            await store.SetMany([
                (K("dupe"), V(1, 10)),
                (K("dupe"), V(2, 20)), // wins
                (K("emptyval"), ReadOnlyMemory<byte>.Empty),
            ]);
            oracle["dupe"] = V(2, 20);
            oracle["emptyval"] = [];

            await store.Flush(true);
            await AssertMatches(store, oracle); // sanity before close
        }

        await using (var store = await KvasarStore.Open(Options(encrypt))) {
            await AssertMatches(store, oracle);
            // Store is usable after reopen.
            await store.Set(K("post"), V(9, 64));
            (await store.Get(K("post")))!.Value.ToArray().Should().Equal(V(9, 64));
        }
    }

    // --- 2. Torn tail dropped, earlier data intact -------------------------

    [Fact]
    public async Task TornTailDroppedEarlierDataIntact()
    {
        const int mainCount = 300;
        const int tailCount = 40;
        const int total = mainCount + tailCount;
        const int valueSize = 80;
        const int pageSize = 512;

        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: pageSize, segmentBytes: 8 * 1024))) {
            // Bulk writes to roll several segments.
            for (var i = 0; i < mainCount; i++)
                await store.Set(K(i), V(i, valueSize));
            // Extra distinct keys land in the active (highest-numbered) segment, sealed by Flush.
            for (var i = mainCount; i < total; i++)
                await store.Set(K(i), V(i, valueSize));
            await store.Flush(true);
        }

        // Multiple segments must exist for a "last segment" truncation to be meaningful.
        SegmentFiles().Length.Should().BeGreaterThan(1);

        // Truncate the LAST segment mid-write: drop a couple of full pages + a partial page.
        var lastPath = HighestSegmentFile();
        long newLength;
        using (var fs = new FileStream(lastPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
            var length = fs.Length;
            newLength = Math.Max(64 + pageSize, length - (2 * pageSize + 100));
            newLength.Should().BeLessThan(length); // we actually removed bytes
            fs.SetLength(newLength);
        }

        // Force recovery via the .klog torn-tail scan (§8): drop the .kidx so the index rebuilds
        // purely from the log, exercising ScanAll's torn-tail truncation.
        var kidxPath = BasePath + ".kidx";
        if (File.Exists(kidxPath))
            File.Delete(kidxPath);

        Dictionary<string, byte[]> survivors;
        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: pageSize, segmentBytes: 8 * 1024))) {
            // Open must not throw and earlier data must be intact.
            survivors = await ScanToMap(store);

            // A nonempty subset survives; the torn tail cost us at least a few trailing entries.
            survivors.Count.Should().BeInRange(total / 2, total - 1);

            // Entries written well before the truncation point (segment 1) are present & correct.
            for (var i = 0; i < 50; i++) {
                var got = await store.Get(K(i));
                got.Should().NotBeNull();
                got!.Value.ToArray().Should().Equal(V(i, valueSize));
            }

            // The store is writable afterward.
            await store.Set(K("after-recovery"), V(7, 123));
            (await store.Get(K("after-recovery")))!.Value.ToArray().Should().Equal(V(7, 123));
            await store.Flush(true);
        }

        // Recovered state (survivors + the new key) persists across another reopen.
        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: pageSize, segmentBytes: 8 * 1024))) {
            (await store.Get(K("after-recovery")))!.Value.ToArray().Should().Equal(V(7, 123));
            (await store.Get(K(0)))!.Value.ToArray().Should().Equal(V(0, valueSize));
        }
    }

    // --- 3. .kidx is rebuildable -------------------------------------------

    [Fact]
    public async Task KidxIsRebuildable()
    {
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 0; i < 250; i++) {
                var v = V(i, 50 + (i % 400)); // some multi-page
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            for (var i = 0; i < 250; i += 5) { // deletes
                await store.Set(K(i), null);
                oracle.Remove(Encoding.UTF8.GetString(K(i)));
            }
            await store.Flush(true);
        }

        var kidxPath = BasePath + ".kidx";
        File.Exists(kidxPath).Should().BeTrue("a keyed hasher persists the index");
        File.Delete(kidxPath);

        await using (var store = await KvasarStore.Open(Options())) {
            await AssertMatches(store, oracle); // rebuilt from the .klog
        }
    }

    // --- 4. Stale .kidx tail gap -------------------------------------------

    [Fact]
    public async Task StaleKidxTailGapReplaysFromLog()
    {
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // Batch A: write & dispose (graceful ⇒ .kidx checkpoint written, HWM at A's end).
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 0; i < 120; i++) {
                var v = V(i, 60);
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            await store.Flush(true);
        }

        var kidxPath = BasePath + ".kidx";
        // Snapshot the stale .kidx (checkpoint@A, HWM@A) to restore after B is written.
        var staleKidx = File.ReadAllBytes(kidxPath);

        // Batch B: new keys + an overwrite of an A key. Flush persists the .klog.
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 120; i < 260; i++) {
                var v = V(i, 60);
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            var overwrite = V(999999, 90);
            await store.Set(K(5), overwrite); // supersedes an A key
            oracle[Encoding.UTF8.GetString(K(5))] = overwrite;
            await store.Flush(true);
        }

        // Simulate the .kidx lagging behind the .klog: restore the pre-B checkpoint. On reopen the
        // store must replay the .klog tail past HWM@A and recover all of B (§6.5 step 4).
        File.WriteAllBytes(kidxPath, staleKidx);

        await using (var store = await KvasarStore.Open(Options())) {
            await AssertMatches(store, oracle); // A ∪ B, with the overwrite applied
        }
    }

    // --- 5. Interrupted compaction / leftover .kidx.tmp --------------------

    [Fact]
    public async Task LeftoverKidxTmpIsIgnoredAndCleaned()
    {
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: 512, segmentBytes: 8 * 1024))) {
            // Create dead space: write keys, then overwrite them many times to roll segments.
            for (var round = 0; round < 12; round++) {
                for (var i = 0; i < 60; i++) {
                    var v = V(i + round * 1000, 90);
                    await store.Set(K(i), v);
                    oracle[Encoding.UTF8.GetString(K(i))] = v; // last round wins
                }
            }
            await store.Flush(true);
        }

        SegmentFiles().Length.Should().BeGreaterThan(1);

        // Leftover temp from an interrupted checkpoint/compaction (partial garbage bytes).
        var tmpPath = BasePath + ".kidx.tmp";
        File.WriteAllBytes(tmpPath, [1, 2, 3, 4, 5, 6, 7]);

        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: 512, segmentBytes: 8 * 1024))) {
            await AssertMatches(store, oracle); // temp ignored, data intact
        }

        // A graceful open/close consumes the stale temp (checkpoint rewrite = temp -> rename).
        File.Exists(tmpPath).Should().BeFalse();
    }

    // --- 6. Clear() then reopen --------------------------------------------

    [Fact]
    public async Task ClearThenReopenIsEmpty()
    {
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 0; i < 100; i++)
                await store.Set(K(i), V(i, 120));
            await store.Flush(true);
            await store.Clear();
            (await ScanToMap(store)).Should().BeEmpty();
            await store.Set(K("survivor"), V(1, 10)); // still usable after Clear
            await store.Flush(true);
        }

        await using (var store = await KvasarStore.Open(Options())) {
            var all = await ScanToMap(store);
            all.Should().HaveCount(1);
            all.Should().ContainKey("survivor");
            (await store.Get(K(0))).Should().BeNull();
        }
    }
}
