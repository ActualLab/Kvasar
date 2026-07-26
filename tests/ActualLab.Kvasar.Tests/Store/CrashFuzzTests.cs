using System.IO;
using System.Linq;
using System.Text;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Randomized crash-recovery fuzz (§8 recovery, §12 error model). A <c>kill -9</c> loses user-space buffers
/// only, so the files of a still-open store are exactly what a killed writer leaves behind: they are copied
/// mid-write, a random fault is injected into the copy, and the reopened store is checked against an oracle
/// that remembers every value each key ever held.
/// </summary>
[Trait("Category", "Slow")]
public sealed class CrashFuzzTests : IDisposable
{
    private const string StoreName = "store";
    private const string ProbeKey = "probe-after-recovery";
    private const int PageSize = 4096;
    private const long SegmentBytes = 96 * 1024;
    private const int KeyPoolSize = 80;
    private const int OpsPerRound = 45;
    private const int RoundsPerSeed = 4;

    // 0, sub-page, exactly one page and several pages — so single-page and multi-page runs both occur.
    private static readonly int[] ValueSizes = [0, 1, 40, 700, 4000, 4096, 4097, 9000];
    private static readonly FaultKind[] AllFaults = Enum.GetValues<FaultKind>();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kvasar-crashfuzz-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public CrashFuzzTests()
    {
        Directory.CreateDirectory(_root);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 11 + 17);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public async Task AbortedMidWriteRecovers(int seed)
    {
        var rnd = new Random(seed);
        var encrypt = (seed & 1) == 0;
        var liveDir = Path.Combine(_root, $"live{seed:D2}");
        Directory.CreateDirectory(liveDir);

        var model = new Model();
        await using var store = await OpenOrFail(Options(BaseIn(liveDir), encrypt), $"seed {seed} live store");
        for (var round = 0; round < RoundsPerSeed; round++) {
            await ApplyOps(store, model, rnd, OpsPerRound);
            var fault = AllFaults[(seed + round) % AllFaults.Length];
            var snapshotDir = Path.Combine(_root, $"snap{seed:D2}-{round}");
            // A dedicated RNG for the fault + probe keeps the op stream identical no matter which fault runs.
            await CheckRecovery(liveDir, snapshotDir, encrypt, model, fault, new Random(seed * 100 + round));
        }
    }

    [Fact]
    public async Task RepeatedAbortsNeverBrickTheStore()
    {
        // Each generation is killed mid-write, faulted, recovered, then written to again — so a defect that
        // only shows up on a store that has already been recovered once can't hide.
        const int generations = 6;
        var rnd = new Random(4242);
        var model = new Model();
        var dir = Path.Combine(_root, "gen00");
        Directory.CreateDirectory(dir);

        for (var gen = 0; gen < generations; gen++) {
            var fault = AllFaults[gen * 3 % AllFaults.Length];
            var next = Path.Combine(_root, $"gen{gen + 1:D2}");
            var because = $"generation {gen}, fault {fault}";
            await using (var store = await OpenOrFail(Options(BaseIn(dir), encrypt: true), because)) {
                await ApplyOps(store, model, rnd, 40);
                Snapshot(dir, next);
            }
            ApplyFault(next, fault, rnd);

            await using var recovered = await OpenOrFail(Options(BaseIn(next), encrypt: true), because);
            var contents = await ScanToMap(recovered);
            AssertNoWrongData(contents, model, because);
            await AssertScanAgreesWithGet(recovered, contents, because);
            model.Resync(contents);
            dir = next;
        }
    }

    [Fact]
    public async Task SetManyWritesSurviveAnIndexCheckpointFiredMidBatch()
    {
        // Minimal deterministic reduction of the failure AbortedMidWriteRecovers(seed 5) once found: an
        // index checkpoint taken while a batch was only half-published stamped an extent covering the
        // whole batch, so recovery replayed from *after* the records whose deltas were still buffered and
        // lost them for good — even though SetMany had returned and the log held them.
        //
        // It cannot happen in the superblock model: a checkpoint is only ever written from inside a commit,
        // which runs after the whole batch is published, and its stamp is the extent that same commit
        // names. The warmups still straddle several checkpoint epochs so a future trigger change is caught.
        const int batchSize = 24;
        foreach (var warmup in new[] { 124, 126, 128, 386 }) {
            var liveDir = Path.Combine(_root, $"mid{warmup}");
            var snapshotDir = Path.Combine(_root, $"mid{warmup}-snap");
            Directory.CreateDirectory(liveDir);
            await using (var store = await KvasarStore.Open(Options(BaseIn(liveDir), encrypt: false))) {
                for (var i = 0; i < warmup; i++)
                    await store.Set(Utf8($"warm{i:D4}"), new byte[16]);
                var batch = new List<(KvasarKey Key, KvasarValue? Value)>();
                for (var j = 0; j < batchSize; j++)
                    batch.Add((Utf8($"batch{j:D2}"), Val(new byte[16])));
                await store.SetMany(batch);
                // FlushDelay = 0 commits per write, so every record is committed here. Only the index's
                // staged delta buffer can still be in user space, which is exactly what a kill -9 loses.
                Snapshot(liveDir, snapshotDir);
            }

            await using var recovered = await OpenOrFail(Options(BaseIn(snapshotDir), encrypt: false), "reopen");
            var contents = await ScanToMap(recovered);
            var missing = Enumerable.Range(0, batchSize).Select(j => $"batch{j:D2}")
                .Where(key => !contents.ContainsKey(key)).ToList();
            missing.Should().BeEmpty(
                $"a completed SetMany whose records are already in the .klog must survive an abort "
                + $"(warmup {warmup}: {missing.Count} of {batchSize} batch keys were dropped)");
        }
    }

    // Private methods

    private async Task CheckRecovery(
        string liveDir, string snapshotDir, bool encrypt, Model model, FaultKind fault, Random rnd)
    {
        Snapshot(liveDir, snapshotDir);
        ApplyFault(snapshotDir, fault, rnd);
        var options = Options(BaseIn(snapshotDir), encrypt);
        var because = $"the store was aborted mid-write and then hit fault {fault} (snapshot: {snapshotDir})";

        Dictionary<string, byte[]> contents;
        await using (var store = await OpenOrFail(options, because)) {
            contents = await ScanToMap(store);
            AssertNoWrongData(contents, model, because);
            await AssertGetReturnsOnlyKnownValues(store, model, because);
            await AssertScanAgreesWithGet(store, contents, because);
            AssertLossIsASuffix(contents, model, because);
            if (fault is not (FaultKind.TruncateActiveLog or FaultKind.CorruptLogHeader
                or FaultKind.TruncateSuperblockSlot))
                AssertFlushedDataSurvives(contents, model, because);

            var probeValue = NewValue(rnd);
            await store.Set(Utf8(ProbeKey), probeValue);
            var probeRead = await store.Get(Utf8(ProbeKey));
            probeRead.Should().NotBeNull(because);
            probeRead!.Value.ToArray().Should().Equal(probeValue, because);
            contents[ProbeKey] = probeValue;
            await store.Flush(true);
        }

        // Recovery must converge: reopening what recovery produced yields exactly the same store.
        await using var reopened = await OpenOrFail(options, because + ", reopened");
        var contents2 = await ScanToMap(reopened);
        contents2.Keys.Should().BeEquivalentTo(contents.Keys, because);
        foreach (var (key, value) in contents)
            contents2[key].Should().Equal(value, because);
    }

    private static void AssertNoWrongData(Dictionary<string, byte[]> contents, Model model, string because)
    {
        // The single most important invariant: recovery may lose recent writes, but it may never invent one.
        foreach (var (key, value) in contents) {
            model.History.Should().ContainKey(key, because);
            model.History[key].Any(v => v.SequenceEqual(value)).Should()
                .BeTrue($"'{key}' must hold a value it was actually given (got {value.Length} bytes) — {because}");
        }
    }

    private static async Task AssertGetReturnsOnlyKnownValues(KvasarStore store, Model model, string because)
    {
        foreach (var (key, known) in model.History) {
            var got = await store.Get(Utf8(key));
            if (got is null)
                continue;
            var value = got.Value.ToArray();
            known.Any(v => v.SequenceEqual(value)).Should()
                .BeTrue($"Get('{key}') must return a value it was actually given — {because}");
        }
    }

    private static async Task AssertScanAgreesWithGet(
        KvasarStore store, Dictionary<string, byte[]> contents, string because)
    {
        foreach (var (key, value) in contents) {
            var got = await store.Get(Utf8(key));
            got.Should().NotBeNull(because);
            got!.Value.ToArray().Should().Equal(value, because);
        }
    }

    private static void AssertFlushedDataSurvives(Dictionary<string, byte[]> contents, Model model, string because)
    {
        // Only keys the last Flush made durable *and* nothing has touched since: a key overwritten or
        // deleted after that Flush is legitimately allowed to hold the newer state (or be gone).
        foreach (var (key, entry) in model.Flushed) {
            if (!model.Current.TryGetValue(key, out var current) || current.Seq != entry.Seq)
                continue;
            contents.Should().ContainKey(key, because);
            contents[key].Should().Equal(entry.Value, because);
        }
    }

    private static void AssertLossIsASuffix(Dictionary<string, byte[]> contents, Model model, string because)
    {
        // Recovery is a prefix replay, so a loss must be a suffix of the write order — never a hole.
        // Every Set/SetMany hands its records to the OS before returning (both Flush() the segments before
        // publishing), and the injected log faults only ever hit the *active* (highest-numbered) segment.
        // So the surviving records are exactly those below some cutoff offset, and offsets grow with write
        // order: a key whose latest write survived must be older than any key whose latest write was lost.
        //
        // Note this counts a key that merely *reverted* to an earlier value as lost at its latest Seq, which
        // is the right call — under prefix replay a rolled-back update means its record was above the cutoff,
        // so every later write must be above it too. Losing a low-Seq write while a high-Seq write survives
        // is not a rollback; it means recovery dropped a record it had no right to drop.
        var lastSurvived = -1L;
        var firstLost = long.MaxValue;
        var lost = new List<string>();
        foreach (var (key, entry) in model.Current) {
            if (contents.TryGetValue(key, out var actual) && actual.SequenceEqual(entry.Value))
                lastSurvived = Math.Max(lastSurvived, entry.Seq);
            else {
                firstLost = Math.Min(firstLost, entry.Seq);
                lost.Add($"{key}@{entry.Seq}"
                    + (contents.TryGetValue(key, out var stale) ? $" reverted to {stale.Length}b" : " missing"));
            }
        }
        lost.Sort(StringComparer.Ordinal);
        lastSurvived.Should().BeLessThan(firstLost,
            $"a crash loses a suffix of the write order, not a hole in the middle "
            + $"(lost: {string.Join(", ", lost)}) — {because}");
    }

    private static void Snapshot(string liveDir, string snapshotDir)
    {
        // Copying the files of a still-open store reproduces a kill -9 without spawning a process: whatever
        // reached the OS is here, whatever sits in a user-space buffer is not. The .lock is skipped, since a
        // killed process leaves no live lock behind (and it's opened FileShare.None anyway).
        Directory.CreateDirectory(snapshotDir);
        foreach (var path in Directory.EnumerateFiles(liveDir)) {
            if (path.EndsWith(".lock", StringComparison.Ordinal))
                continue;
            using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = new FileStream(
                Path.Combine(snapshotDir, Path.GetFileName(path)), FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static void ApplyFault(string dir, FaultKind fault, Random rnd)
    {
        switch (fault) {
        case FaultKind.None:
            break;
        case FaultKind.TruncateActiveLog: {
            var path = ActiveDataPath(dir);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            if (fs.Length > 0)
                fs.SetLength(rnd.NextInt64(0, fs.Length));
            break;
        }
        case FaultKind.CorruptLogHeader: {
            var path = ActiveDataPath(dir);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            fs.Seek(rnd.Next(KvasarConstants.SegmentHeaderSize), SeekOrigin.Begin);
            var b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.Current);
            fs.WriteByte((byte)(b ^ 0x5A));
            break;
        }
        case FaultKind.TruncateIndex: {
            foreach (var path in IndexPaths(dir)) {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                if (fs.Length > 0)
                    fs.SetLength(rnd.NextInt64(0, fs.Length));
            }
            break;
        }
        case FaultKind.PartialIndexDelta: {
            foreach (var path in IndexPaths(dir)) {
                var partial = new byte[rnd.Next(1, IndexLog.EntrySize)];
                rnd.NextBytes(partial);
                AppendBytes(path, partial);
            }
            break;
        }
        case FaultKind.GarbageIndexDelta: {
            foreach (var path in IndexPaths(dir)) {
                var garbage = new byte[IndexLog.EntrySize * rnd.Next(1, 4)];
                rnd.NextBytes(garbage);
                AppendBytes(path, garbage);
            }
            break;
        }
        case FaultKind.TruncateSuperblockSlot: {
            // The newest slot is lost but the older one survives: recovery must fall back to it rather
            // than wipe, which is the whole point of writing the two slots alternately (§3.1).
            var path = Path.Combine(dir, StoreName + ".kvs");
            if (!File.Exists(path))
                break;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            if (fs.Length > Superblock.HeaderSize + Superblock.SlotSize)
                fs.SetLength(Superblock.HeaderSize + Superblock.SlotSize);
            break;
        }
        default:
            throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }

    // The store never renames or recreates its files, so the active one is whichever the superblock
    // names — which the test can't read. Faulting the larger file hits the active slot in practice.
    private static string ActiveDataPath(string dir)
        => Directory.EnumerateFiles(dir, StoreName + ".*.kdat")
            .OrderByDescending(x => new FileInfo(x).Length).First();

    private static string[] IndexPaths(string dir)
        => Directory.EnumerateFiles(dir, StoreName + ".*.kidx").Where(x => new FileInfo(x).Length > 0).ToArray();

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        fs.Write(bytes, 0, bytes.Length);
    }

    private static async Task ApplyOps(KvasarStore store, Model model, Random rnd, int count)
    {
        for (var i = 0; i < count; i++) {
            var roll = rnd.Next(100);
            if (roll < 10) {
                await store.Flush(rnd.Next(3) == 0);
                model.OnFlush();
                continue;
            }
            if (roll < 25) {
                var batch = NewBatch(model, rnd);
                await store.SetMany(batch.Select(x => (Key: Utf8(x.Key), Value: Val(x.Value))).ToList());
                foreach (var (key, value) in batch)
                    model.OnSet(key, value);
                continue;
            }
            if (roll < 40) {
                var key = PickKey(model, rnd, preferExisting: true);
                await store.Set(Utf8(key), null);
                model.OnSet(key, null);
                continue;
            }
            {
                var key = PickKey(model, rnd, preferExisting: roll < 70);
                var value = NewValue(rnd);
                await store.Set(Utf8(key), value);
                model.OnSet(key, value);
            }
        }
    }

    private static List<(string Key, byte[]? Value)> NewBatch(Model model, Random rnd)
    {
        // Deduplicated: a superseded value inside one SetMany is never actually held by the key, and letting
        // it into the history would weaken the "no wrong data" check.
        var batch = new List<(string Key, byte[]? Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var size = rnd.Next(2, 5);
        for (var i = 0; i < size; i++) {
            var key = PickKey(model, rnd, preferExisting: rnd.Next(2) == 0);
            if (!seen.Add(key))
                continue;
            batch.Add((key, rnd.Next(10) == 0 ? null : NewValue(rnd)));
        }
        return batch;
    }

    private static string PickKey(Model model, Random rnd, bool preferExisting)
    {
        if (preferExisting) {
            var live = model.LiveKeys();
            if (live.Count != 0)
                return live[rnd.Next(live.Count)];
        }
        return $"k{rnd.Next(KeyPoolSize):D3}";
    }

    private static byte[] NewValue(Random rnd)
    {
        var value = new byte[ValueSizes[rnd.Next(ValueSizes.Length)]];
        rnd.NextBytes(value);
        return value;
    }

    private static async Task<KvasarStore> OpenOrFail(KvasarOptions options, string because)
    {
        // §12: Open recovers or wipes-and-recreates; it must never propagate an exception of any type.
        KvasarStore? store = null;
        var open = async () => store = await KvasarStore.Open(options);
        await open.Should().NotThrowAsync("KvasarStore.Open must never throw — " + because);
        return store!;
    }

    private static async Task<Dictionary<string, byte[]>> ScanToMap(KvasarStore store)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var (key, value) in store.Scan())
            map[Encoding.UTF8.GetString(key.Span)] = value.ToArray();
        return map;
    }

    private KvasarOptions Options(string basePath, bool encrypt) => new() {
        BasePath = basePath,
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = PageSize,
        SegmentBytes = SegmentBytes,
        // Durable mode: these tests assert that flushed writes survive an abort, which is only a guarantee
        // when Set is durable before it returns. Deferred flushing has its own suite in FlushDelayTests.
        FlushDelay = TimeSpan.Zero,
    };

    private static string BaseIn(string dir) => Path.Combine(dir, StoreName);
    private static KvasarKey Utf8(string s) => Encoding.UTF8.GetBytes(s);
    // The explicit default() is load-bearing: byte[] converts implicitly to KvasarValue, so a plain
    // `value is null ? null : ...` types the conditional as non-nullable and turns every delete into an
    // empty-value write.
    private static KvasarValue? Val(byte[]? value)
        => value is null ? default(KvasarValue?) : new KvasarValue(value);

    // Nested types

    private enum FaultKind
    {
        None = 0,
        TruncateActiveLog,
        CorruptLogHeader,
        TruncateIndex,
        PartialIndexDelta,
        GarbageIndexDelta,
        TruncateSuperblockSlot,
    }

    /// <summary>
    /// The oracle: every value each key has ever held (what recovery is allowed to return), the latest state
    /// with per-key write sequence numbers (what a crash may only lose as a suffix of), and the state as of
    /// the last explicit <c>Flush</c> (what must survive a fault that doesn't touch the log).
    /// </summary>
    private sealed class Model
    {
        private long _seq;

        public Dictionary<string, List<byte[]>> History { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (byte[] Value, long Seq)> Current { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (byte[] Value, long Seq)> Flushed { get; } = new(StringComparer.Ordinal);

        public void OnSet(string key, byte[]? value)
        {
            _seq++;
            if (!History.TryGetValue(key, out var known)) {
                known = [];
                History.Add(key, known);
            }
            if (value is null) {
                Current.Remove(key);
                return;
            }
            known.Add(value);
            Current[key] = (value, _seq);
        }

        public void OnFlush()
        {
            Flushed.Clear();
            foreach (var (key, entry) in Current)
                Flushed.Add(key, entry);
        }

        public void Resync(Dictionary<string, byte[]> contents)
        {
            // After a fault the store legitimately holds less than Current; re-baseline so the next
            // generation's ops build on what actually survived.
            Current.Clear();
            foreach (var (key, value) in contents)
                Current[key] = (value, ++_seq);
            OnFlush();
        }

        public List<string> LiveKeys()
            => Current.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
