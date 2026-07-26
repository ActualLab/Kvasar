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

    private string[] DataFiles() => Directory.GetFiles(_dir, Path.GetFileName(BasePath) + ".*.kdat");
    private string[] IndexFiles() => Directory.GetFiles(_dir, Path.GetFileName(BasePath) + ".*.kidx");
    // Slot 0 is the active data file of a freshly created store, and nothing here compacts.
    private string ActiveDataFile() => BasePath + ".0.kdat";

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
    public async Task TornTailAboveTheCommittedExtentIsDropped()
    {
        // The v2 shape of "torn tail": bytes past the committed extent are not data — not read, not
        // parsed, not scanned (§3.1) — and the page ids they occupy are burned rather than re-issued.
        // Everything the superblock names must survive intact.
        const int total = 340;
        const int valueSize = 80;
        const int pageSize = 512;
        var options = Options(encrypt: false, pageSize: pageSize);

        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++) {
                var v = V(i, valueSize);
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            await store.Flush(true);
        }

        // Append a partial page of garbage: what a process killed mid-append leaves above the extent.
        var committedLength = new FileInfo(ActiveDataFile()).Length;
        using (var fs = new FileStream(ActiveDataFile(), FileMode.Append, FileAccess.Write, FileShare.None)) {
            var garbage = new byte[pageSize + 100];
            new Random(7).NextBytes(garbage);
            fs.Write(garbage);
        }

        // Drop the index so the store rebuilds purely from the log, which is what walks the pages.
        foreach (var path in IndexFiles())
            File.Delete(path);

        await using (var store = await KvasarStore.Open(options)) {
            await AssertMatches(store, oracle); // nothing below the committed extent was lost
            await store.Set(K("after-recovery"), V(7, 123));
            (await store.Get(K("after-recovery")))!.Value.ToArray().Should().Equal(V(7, 123));
            await store.Flush(true);
        }
        // The garbage was never overwritten — its page ids are burned, so the file only grew.
        new FileInfo(ActiveDataFile()).Length.Should().BeGreaterThan(committedLength + pageSize + 100);

        await using (var store = await KvasarStore.Open(options)) {
            (await store.Get(K("after-recovery")))!.Value.ToArray().Should().Equal(V(7, 123));
            (await store.Get(K(0)))!.Value.ToArray().Should().Equal(V(0, valueSize));
        }
    }

    [Fact]
    public async Task TruncatingIntoTheCommittedExtentRebuildsRatherThanServingGarbage()
    {
        // The other half of the contract: a superblock slot naming more data than the file holds is not
        // adoptable, and with no adoptable slot left the store rebuilds. It never serves a partial commit.
        const int pageSize = 512;
        var options = Options(encrypt: false, pageSize: pageSize);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < 200; i++)
                await store.Set(K(i), V(i, 80));
            await store.Flush(true);
        }

        using (var fs = new FileStream(ActiveDataFile(), FileMode.Open, FileAccess.Write, FileShare.None))
            fs.SetLength(fs.Length / 2);

        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K("fresh"), V(1, 10));
            (await store.Get(K("fresh")))!.Value.ToArray().Should().Equal(V(1, 10));
        }
    }

    [Fact]
    public async Task TheFileSetIsFixedAndNeverGrows()
    {
        // §3: five files, all created once. Nothing is created, renamed or deleted while the store is
        // open — which is what removed the whole segment-lifecycle bug family.
        var options = Options(encrypt: false, pageSize: 512) with {
            CompactionMinBytes = 1024,
            CompactionDeadRatio = 0.3,
        };
        for (var round = 0; round < 3; round++) {
            await using var store = await KvasarStore.Open(options);
            for (var i = 0; i < 200; i++)
                await store.Set(K(i), V(i + round * 1000, 90));
            await store.Compact();
            await store.Flush(true);

            DataFiles().Should().HaveCount(2);
            IndexFiles().Should().HaveCount(2);
            File.Exists(BasePath + ".kvs").Should().BeTrue();
            Directory.GetFiles(_dir).Should().HaveCount(6); // + the .lock
        }
    }

    // --- 3. The index is rebuildable ---------------------------------------

    [Fact]
    public async Task IndexIsRebuildable()
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

        IndexFiles().Should().Contain(x => new FileInfo(x).Length > 0, "a keyed hasher persists the index");
        foreach (var path in IndexFiles())
            File.Delete(path);

        await using (var store = await KvasarStore.Open(Options())) {
            await AssertMatches(store, oracle); // rebuilt from the .kdat
        }
    }

    // --- 4. Stale index tail gap -------------------------------------------

    [Fact]
    public async Task StaleIndexTailGapReplaysFromLog()
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

        var kidxPath = BasePath + ".0.kidx";
        // Snapshot the stale index (checkpoint + A's deltas) to restore after B is written.
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

        // Simulate the index lagging behind the data: restore the pre-B image. The commit named a longer
        // index prefix than the file now holds, so recovery stops trusting it and replays the data from
        // the checkpoint's own stamp, recovering all of B (§5.2 step 5).
        File.WriteAllBytes(kidxPath, staleKidx);

        await using (var store = await KvasarStore.Open(Options())) {
            await AssertMatches(store, oracle); // A ∪ B, with the overwrite applied
        }
    }

    // --- 5. A v1 file set left in place ------------------------------------

    [Fact]
    public async Task PreSuperblockLeftoversAreIgnoredAndWiped()
    {
        // Upgrading from the segment layout must not strand its files, and their presence must not
        // confuse an open: the superblock is the only thing that names anything.
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: 512))) {
            for (var i = 0; i < 60; i++) {
                var v = V(i, 90);
                await store.Set(K(i), v);
                oracle[Encoding.UTF8.GetString(K(i))] = v;
            }
            await store.Flush(true);
        }

        var leftovers = new[] { BasePath + ".001.klog", BasePath + ".kidx", BasePath + ".kidx.tmp", BasePath + ".clean" };
        foreach (var path in leftovers)
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7]);

        await using (var store = await KvasarStore.Open(Options(encrypt: false, pageSize: 512)))
            await AssertMatches(store, oracle); // leftovers ignored, data intact

        // A Version bump wipes, and the wipe claims the leftovers too — they really are this store's.
        await using (await KvasarStore.Open(Options(encrypt: false, pageSize: 512) with { Version = "next" })) { }
        foreach (var path in leftovers)
            File.Exists(path).Should().BeFalse();
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
