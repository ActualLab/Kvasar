using ActualLab.Kvasar;
using System.Diagnostics;
using System.Text;

namespace ActualLab.Kvasar.Benchmarks;

/// <summary>
/// "ActualChat cold start": a client cache is opened cold, then 8 app threads speculatively render the
/// UI — a short burst of distinct-key reads, 10% of which also write. Each engine runs in the stack it
/// would actually ship in; two dataset sizes are measured, with identical workloads and seeds.
/// </summary>
public static class ChatCacheScenario
{
    public const int DefaultPageSize = 16 * 1024; // comfortably above the p99 tile (15.5 KB)

    private const double TileByteShare = 0.80;
    private const int ChatCount = 48;
    private const int MiscKeyLength = 49;          // "kvas:" + 44 base64url chars
    private const double EntryOverheadBytes = 120; // ids, author, timestamps, flags, version
    private const double TextMedianChars = 40;
    private const double TextSigma = 1.0;
    private const int TextMaxChars = 4000;
    private const int AppThreads = 8;
    private const double WriteProbability = 0.10;
    private const int Repeats = 5; // a 1-2 k op burst is a small sample; every reported row is the median run
    private const long PageCacheBytes = 16L * 1024 * 1024; // a phone-sized budget: above config A, below config B
    private static readonly int[] TileLayers = [5, 20, 80];
    private static readonly double[] TileLayerWeights = [0.60, 0.30, 0.10];
    // Kvasar's own write debounce and the harness LazyWriter's use the same value, so neither side wins on
    // how long it may defer a write; both configurations are drained and made durable before the clock stops.
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(0.5);
    private static readonly Config[] Configs = [
        new(12_000_000, 500, 500),
        new(25_000_000, 1000, 1000),
    ];

    public static async Task Run(
        string root, byte[] encryptionKey, string engines, int pageSize,
        KvasarDurability durability = KvasarDurability.Flushed)
    {
        // 8 app threads + 4 reader workers + 1 writer must all be resident, or thread injection
        // (~1 thread/sec) would land inside the measured burst.
        ThreadPool.GetMinThreads(out var minWorkers, out var minIoThreads);
        ThreadPool.SetMinThreads(Math.Max(minWorkers, AppThreads + ReaderAndWriterThreads), minIoThreads);

        var kvasarAes = (string dir) => (IKvEngine)new KvasarEngine(
            Path.Combine(dir, "store"), encryptionKey, true, pageSize, PageCacheBytes, FlushDelay, durability);
        var kvasarNoEnc = (string dir) => (IKvEngine)new KvasarEngine(
            Path.Combine(dir, "store"), encryptionKey, false, pageSize, PageCacheBytes, FlushDelay, durability);
        var runs = new List<(string Tag, KvasStack Stack, Func<string, IKvEngine> Factory)>();
        if (engines is "sqlite" or "both")
            runs.Add(("sq", KvasStack.BatchingKvas,
                dir => new SqliteEngine(Path.Combine(dir, "cache.db3"), encryptionKey)));
        if (engines is "kvasar" or "both") {
            runs.Add(("kvp", KvasStack.Plain, kvasarAes));
            runs.Add(("kvnep", KvasStack.Plain, kvasarNoEnc));
            runs.Add(("kv", KvasStack.BatchingKvas, kvasarAes));
        }

        for (var c = 0; c < Configs.Length; c++) {
            var config = Configs[c];
            Console.WriteLine($"===== Config {(char)('A' + c)}: {config.TargetBytes / 1e6:N0} MB dataset, "
                + $"{config.TileReads:N0} tile + {config.MiscReads:N0} misc reads =====");
            var dataset = Dataset.Generate(config);
            dataset.Print();
            Console.WriteLine();

            // Warm up on a 10x smaller dataset with a 10x shorter burst: without it, whichever engine runs
            // first pays for jitting the whole harness path and for the thread-pool ramp, which is worth
            // ~10 ms of a ~50 ms measurement.
            var warmupDataset = Dataset.Generate(config.Warmup);
            foreach (var (tag, stack, factory) in runs)
                _ = await RunEngine(() => factory(NewDir(root, "warmup-" + tag)), warmupDataset, stack);

            var results = new List<ChatResult>();
            foreach (var (tag, stack, factory) in runs)
                results.Add(await RunEngine(() => factory(NewDir(root, tag)), dataset, stack));
            PrintTable(results, config);
            Console.WriteLine();
        }
    }

    // Private methods

    private static int ReaderAndWriterThreads
        => BatchingKvasHarness.ReaderWorkerCount + 1;

    private static async Task<ChatResult> RunEngine(
        Func<IKvEngine> factory,
        Dataset dataset,
        KvasStack stack)
    {
        var engine = factory();

        // Populate (not measured): this is the state a previous app session left behind.
        await engine.Open();
        var batch = new List<(byte[], byte[])>(64);
        foreach (var (key, value) in dataset.All) {
            batch.Add((key.Bytes, value));
            if (batch.Count == 64) { await engine.WriteBatch(batch); batch.Clear(); }
        }
        if (batch.Count > 0) await engine.WriteBatch(batch);
        await engine.FlushDurable();
        var fileBytes = engine.FileBytes;
        await engine.Close();

        var runs = new List<ChatResult>(Repeats);
        for (var r = 0; r < Repeats; r++)
            runs.Add(await ColdStart());
        await engine.DisposeAsync();
        runs.Sort((a, b) => a.TotalMs.CompareTo(b.TotalMs));
        return runs[runs.Count / 2] with { MinMs = runs[0].TotalMs, MaxMs = runs[^1].TotalMs };

        async Task<ChatResult> ColdStart() {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // --- Cold start starts here ---
            var total = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();
            await engine.Open();
            var kvas = NewKvas(engine, stack);
            sw.Stop();
            var openMs = sw.Elapsed.TotalMilliseconds;

            var latencies = new long[AppThreads][];
            var misses = 0;
            var workers = new Task[AppThreads];
            var barrier = new Barrier(AppThreads + 1);
            for (var t = 0; t < AppThreads; t++) {
                var tid = t;
                workers[t] = Task.Run(async () => {
                    var rnd = new Random(9000 + tid);
                    var plan = dataset.Plan[tid];
                    var lat = new long[plan.Length];
                    var local = 0;
                    barrier.SignalAndWait();
                    for (var i = 0; i < plan.Length; i++) {
                        var (isTile, index) = plan[i];
                        var (key, value) = (isTile ? dataset.Tiles : dataset.Misc)[index];
                        var t0 = Stopwatch.GetTimestamp();
                        var read = await kvas.Get(key);
                        lat[i] = Stopwatch.GetTimestamp() - t0;
                        if (read == null || read.Length != value.Length) local++;
                        if (rnd.NextDouble() < WriteProbability) {
                            var fresh = new byte[read?.Length ?? value.Length];
                            rnd.NextBytes(fresh);
                            await kvas.Set(key, fresh);
                        }
                    }
                    latencies[tid] = lat;
                    if (local > 0) Interlocked.Add(ref misses, local);
                });
            }
            barrier.SignalAndWait();
            sw.Restart();
            await Task.WhenAll(workers);
            sw.Stop();
            var readMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            await kvas.Flush();
            await engine.FlushDurable();
            sw.Stop();
            var flushMs = sw.Elapsed.TotalMilliseconds;
            total.Stop();
            // --- Cold start ends here ---

            var getManyCalls = kvas.GetManyCalls;
            var reads = kvas.CacheHits + kvas.CacheMisses;
            var all = MergeSorted(latencies);
            await kvas.DisposeAsync();
            await engine.Close();

            var totalMs = total.Elapsed.TotalMilliseconds;
            return new ChatResult(engine.Name, StackName(stack), fileBytes,
                totalMs, totalMs, totalMs, openMs, readMs, flushMs,
                getManyCalls, getManyCalls == 0 ? 0 : (double)kvas.GetManyKeys / getManyCalls,
                reads == 0 ? 0 : (double)kvas.CacheHits / reads, kvas.SetManyCalls, kvas.SetManyKeys,
                Pct(all, 0.50), Pct(all, 0.99), misses);
        }
    }

    private static IKvas NewKvas(IKvEngine engine, KvasStack stack)
        => stack == KvasStack.Plain
            ? new PlainKvas(engine)
            : new BatchingKvasHarness(engine, FlushDelay);

    private static string StackName(KvasStack stack)
        => stack == KvasStack.Plain ? "plain" : "BatchingKvas";

    private static string NewDir(string root, string tag)
    {
        var d = Path.Combine(root, tag + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static long[] MergeSorted(long[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p?.Length ?? 0;
        var all = new long[total];
        var o = 0;
        foreach (var p in parts) {
            if (p == null) continue;
            Array.Copy(p, 0, all, o, p.Length);
            o += p.Length;
        }
        Array.Sort(all);
        return all;
    }

    private static double Pct(long[] sortedTicks, double q)
    {
        if (sortedTicks.Length == 0) return 0;
        var idx = (int)Math.Clamp(q * (sortedTicks.Length - 1), 0, sortedTicks.Length - 1);
        return sortedTicks[idx] * 1_000_000.0 / Stopwatch.Frequency;
    }

    private static void PrintTable(List<ChatResult> rows, Config config)
    {
        Console.WriteLine($"Cold start: open -> {AppThreads} threads x "
            + $"{(config.TileReads + config.MiscReads) / AppThreads} reads of distinct keys "
            + $"({config.TileReads:N0} tiles + {config.MiscReads:N0} misc, "
            + $"{WriteProbability:P0} of them also write) -> flush; median of {Repeats} runs");
        Console.WriteLine($"{"Engine",-24} {"Stack",-13} {"DB MB",6} {"TOTAL ms",9} {"min-max",13} {"Open ms",8} "
            + $"{"Read ms",8} {"Flush ms",9} {"Backend",8} {"keys/op",8} {"CacheHit",9} {"Writes",8} "
            + $"{"p50 us",8} {"p99 us",8}");
        Console.WriteLine(new string('-', 153));
        foreach (var r in rows)
            Console.WriteLine($"{r.Name,-24} {r.Stack,-13} {r.FileBytes / 1e6,6:N1} {r.TotalMs,9:N1} "
                + $"{$"{r.MinMs:N1}-{r.MaxMs:N1}",13} {r.OpenMs,8:N1} {r.ReadMs,8:N1} {r.FlushMs,9:N1} "
                + $"{r.GetManyCalls,8:N0} {r.KeysPerGetMany,8:N2} {r.CacheHitRate,9:P1} {r.SetManyCalls,8:N0} "
                + $"{r.P50Us,8:N1} {r.P99Us,8:N1}");
        Console.WriteLine("(TOTAL ms = wall clock from the cold open until every write is durable; "
            + $"min-max spans all {Repeats} runs.");
        Console.WriteLine(" Stack: 'BatchingKvas' = 256-entry LRU + BatchProcessor(64 keys, 4 workers) -> GetMany,");
        Console.WriteLine($" LazyWriter({FlushDelay.TotalMilliseconds:N0} ms / 64 items) -> SetMany; "
            + "'plain' = no layer: app threads call the engine's Get/Set directly.");
        Console.WriteLine(" Backend = backend read calls; keys/op = keys per call; CacheHit = read-cache hit rate;");
        Console.WriteLine(" Writes = backend write calls.)");
        foreach (var r in rows.Where(r => r.Misses > 0))
            Console.WriteLine($"  WARN {r.Name}: {r.Misses} reads returned a missing/short value");
    }

    // Nested types

    private enum KvasStack { BatchingKvas, Plain }

    private sealed record Config(long TargetBytes, int TileReads, int MiscReads)
    {
        public Config Warmup => new(TargetBytes / 10, TileReads / 10, MiscReads / 10);
    }

    private sealed record ChatResult(
        string Name, string Stack, long FileBytes, double TotalMs, double MinMs, double MaxMs,
        double OpenMs, double ReadMs, double FlushMs, long GetManyCalls, double KeysPerGetMany,
        double CacheHitRate, long SetManyCalls, long SetManyKeys, double P50Us, double P99Us, int Misses);

    private sealed class Dataset
    {
        public (KvKey Key, byte[] Value)[] Tiles { get; private init; } = [];
        public (KvKey Key, byte[] Value)[] Misc { get; private init; } = [];
        public (KvKey Key, byte[] Value)[] All { get; private init; } = [];
        public (bool IsTile, int Index)[][] Plan { get; private init; } = [];
        public int[] TilesPerLayer { get; private init; } = [];
        public long TileBytes { get; private init; }
        public long MiscBytes { get; private init; }
        public long ChatEntryCount { get; private init; }
        public double TextMean { get; private init; }
        public int[] TileSizes { get; private init; } = [];

        public static Dataset Generate(Config config)
        {
            var rnd = new Random(20260724);
            var tileBudget = (long)(config.TargetBytes * TileByteShare);
            var miscBudget = config.TargetBytes - tileBudget;

            var tiles = new List<(KvKey, byte[])>();
            var tilesPerLayer = new int[TileLayers.Length];
            var nextTileIndex = new int[ChatCount, TileLayers.Length];
            var chatIds = new string[ChatCount];
            for (var i = 0; i < ChatCount; i++)
                chatIds[i] = NewId(rnd, 16);
            long tileBytes = 0, entryCount = 0, textChars = 0;
            var chat = 0;
            while (tileBytes < tileBudget) {
                var layerIndex = PickLayer(rnd);
                var layer = TileLayers[layerIndex];
                var start = layer * nextTileIndex[chat, layerIndex]++;
                var text = Encoding.ASCII.GetBytes($"ChatEntryReader.GetTile:{chatIds[chat]}:{layer}:{start}");
                var size = 0;
                for (var e = 0; e < layer; e++) {
                    var chars = Math.Clamp((int)Math.Round(TextMedianChars * Math.Exp(TextSigma * Gauss(rnd))),
                        1, TextMaxChars);
                    textChars += chars;
                    size += chars + (int)EntryOverheadBytes;
                }
                var value = new byte[size];
                rnd.NextBytes(value);
                tiles.Add((new KvKey(Encoding.ASCII.GetString(text), text), value));
                tilesPerLayer[layerIndex]++;
                entryCount += layer;
                tileBytes += text.Length + size;
                chat = (chat + 1) % ChatCount;
            }

            var misc = new List<(KvKey, byte[])>();
            long miscBytes = 0;
            while (miscBytes < miscBudget) {
                var text = "kvas:" + NewId(rnd, 33);
                var value = new byte[80 + rnd.Next(41)];
                rnd.NextBytes(value);
                misc.Add((new KvKey(text, Encoding.ASCII.GetBytes(text)), value));
                miscBytes += text.Length + value.Length;
            }

            var tileArray = tiles.ToArray();
            var miscArray = misc.ToArray();
            var sizes = new int[tileArray.Length];
            for (var i = 0; i < tileArray.Length; i++)
                sizes[i] = tileArray[i].Item2.Length;
            Array.Sort(sizes);
            return new Dataset {
                Tiles = tileArray,
                Misc = miscArray,
                All = [..tileArray, ..miscArray],
                Plan = BuildPlan(
                    Shuffle(tileArray.Length, new Random(4242)),
                    Shuffle(miscArray.Length, new Random(4243)),
                    config),
                TilesPerLayer = tilesPerLayer,
                TileBytes = tileBytes,
                MiscBytes = miscBytes,
                ChatEntryCount = entryCount,
                TextMean = (double)textChars / entryCount,
                TileSizes = sizes,
            };
        }

        public void Print()
        {
            var total = TileBytes + MiscBytes;
            Console.WriteLine($"Dataset: {total / 1e6:N1} MB in {All.Length:N0} entries "
                + $"(tiles {TileBytes / 1e6:N1} MB = {(double)TileBytes / total:P1}, "
                + $"misc {MiscBytes / 1e6:N1} MB = {(double)MiscBytes / total:P1})");
            var layers = string.Join(", ", TileLayers.Select((l, i) =>
                $"L{l}: {TilesPerLayer[i]:N0} ({(double)TilesPerLayer[i] / Tiles.Length:P1})"));
            Console.WriteLine($"  Tiles: {Tiles.Length:N0} over {ChatCount} chats [{layers}], "
                + $"{ChatEntryCount:N0} chat entries, {Tiles[0].Key.Text.Length}-byte keys");
            Console.WriteLine($"  Tile size: mean {Mean(TileSizes) / 1024:N2} KB, "
                + $"median {TileSizes[TileSizes.Length / 2] / 1024.0:N2} KB, "
                + $"p99 {TileSizes[(int)(TileSizes.Length * 0.99)] / 1024.0:N2} KB, "
                + $"max {TileSizes[^1] / 1024.0:N2} KB");
            Console.WriteLine($"  Message text: mean {TextMean:N1} chars (log-normal, median {TextMedianChars:N0}, "
                + $"sigma {TextSigma:N1}), +{EntryOverheadBytes:N0} B/entry overhead");
            Console.WriteLine($"  Misc: {Misc.Length:N0} entries, {MiscKeyLength}-byte keys, "
                + $"{(double)MiscBytes / Misc.Length - MiscKeyLength:N0}-byte mean value");
        }

        // Private methods

        private static (bool IsTile, int Index)[][] BuildPlan(int[] tileOrder, int[] miscOrder, Config config)
        {
            // Every read hits a distinct key: an app start speculatively renders distinct UI, and Fusion's
            // compute cache already dedupes repeats upstream. Tiles and misc are interleaved per thread the
            // way a render pass issues them.
            var tileCounts = Split(config.TileReads, false);
            var miscCounts = Split(config.MiscReads, true);
            var plan = new (bool, int)[AppThreads][];
            int tileOffset = 0, miscOffset = 0;
            for (var t = 0; t < AppThreads; t++) {
                var ops = new (bool, int)[tileCounts[t] + miscCounts[t]];
                for (var i = 0; i < tileCounts[t]; i++)
                    ops[i] = (true, tileOrder[tileOffset + i]);
                for (var i = 0; i < miscCounts[t]; i++)
                    ops[tileCounts[t] + i] = (false, miscOrder[miscOffset + i]);
                tileOffset += tileCounts[t];
                miscOffset += miscCounts[t];
                var rnd = new Random(9000 + t);
                for (var i = ops.Length - 1; i > 0; i--) {
                    var j = rnd.Next(i + 1);
                    (ops[i], ops[j]) = (ops[j], ops[i]);
                }
                plan[t] = ops;
            }
            return plan;
        }

        private static int[] Split(int total, bool remainderLast)
        {
            // Tiles take their remainder from the front and misc from the back, so every thread issues the
            // same number of reads even when neither count divides evenly by AppThreads.
            var counts = new int[AppThreads];
            var min = total / AppThreads;
            var extra = total - min * AppThreads;
            for (var i = 0; i < AppThreads; i++) {
                var isExtra = remainderLast ? i >= AppThreads - extra : i < extra;
                counts[i] = min + (isExtra ? 1 : 0);
            }
            return counts;
        }

        private static double Mean(int[] values)
        {
            long sum = 0;
            foreach (var v in values) sum += v;
            return (double)sum / values.Length;
        }

        private static int PickLayer(Random rnd)
        {
            var p = rnd.NextDouble();
            for (var i = 0; i < TileLayerWeights.Length; i++) {
                p -= TileLayerWeights[i];
                if (p <= 0) return i;
            }
            return TileLayerWeights.Length - 1;
        }

        private static int[] Shuffle(int count, Random rnd)
        {
            var order = new int[count];
            for (var i = 0; i < count; i++) order[i] = i;
            for (var i = count - 1; i > 0; i--) {
                var j = rnd.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            return order;
        }

        private static string NewId(Random rnd, int byteCount)
        {
            var bytes = new byte[byteCount];
            rnd.NextBytes(bytes);
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static double Gauss(Random rnd)
        {
            var u1 = 1.0 - rnd.NextDouble();
            var u2 = rnd.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
