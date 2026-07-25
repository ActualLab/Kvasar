using System.Diagnostics;
using System.Text;
using ActualLab.Kvasar.Benchmarks;

// ---- Args ----------------------------------------------------------------
int N = ArgInt("--n", 50_000);
int[] sizes = ArgStr("--value", "sweep") is "sweep"
    ? [128, 1024, 4096]
    : [ArgInt("--value", 1024)];
int threads = ArgInt("--threads", Math.Min(8, Environment.ProcessorCount));
int totalLookups = ArgInt("--lookups", 500_000);
var engines = ArgStr("--engines", "both")!;   // kvasar | sqlite | both
var scenario = ArgStr("--scenario", "sweep")!; // sweep | chat
int pageSize = ArgInt("--pagesize", 4096);  // Kvasar page size; larger pages = fewer, bigger async I/Os
const int BatchSize = 64;

SQLitePCL.Batteries_V2.Init();

// Lookup workers block on a Barrier for a simultaneous start, so the pool must be able to host them all
// at once — otherwise thread injection (~1 thread/sec) would stall the start and skew the timings.
ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIoThreads);
ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, threads + 4), minIoThreads);

var root = Path.Combine(Path.GetTempPath(), "kvasar-bench-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var key = new byte[32];
for (var i = 0; i < 32; i++) key[i] = (byte)(i * 3 + 1);

Console.WriteLine($"Machine: {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
if (scenario is "chat") {
    Console.WriteLine("Scenario: ActualChat cold start (BatchingKvas harness over each engine)");
    Console.WriteLine();
    await ChatCacheScenario.Run(root, key, engines);
    try { Directory.Delete(root, true); } catch { /* ignore */ }
    return;
}

Console.WriteLine($"N={N:N0} keys (50-byte keys), lookup threads={threads}, total lookups={totalLookups:N0}, batch={BatchSize}");
Console.WriteLine($"Value sizes: {string.Join(", ", sizes)} bytes");
Console.WriteLine();

foreach (var size in sizes) {
    Console.WriteLine($"==================  value size = {size} bytes  ({(double)N * size / 1e6:N1} MB of values)  ==================");
    var data = GenData(N, size);
    var results = new List<Result>();
    if (engines is "kvasar" or "both") {
        results.Add(await RunEngine(() => new KvasarEngine(Path.Combine(NewDir("kv"), "store"), key, true, pageSize), data));
        results.Add(await RunEngine(() => new KvasarEngine(Path.Combine(NewDir("kvne"), "store"), key, false, pageSize), data));
    }
    if (engines is "sqlite" or "both")
        results.Add(await RunEngine(() => new SqliteEngine(Path.Combine(NewDir("sq"), "cache.db3"), key), data));
    PrintTable(results);
    Console.WriteLine();
}

try { Directory.Delete(root, true); } catch { /* ignore */ }
return;

// ---- Runner --------------------------------------------------------------
async Task<Result> RunEngine(Func<IKvEngine> factory, (byte[] Key, byte[] Value)[] data)
{
    var engine = factory();
    await engine.Open();

    // 1) Write / construct
    var sw = Stopwatch.StartNew();
    var batch = new List<(byte[], byte[])>(BatchSize);
    foreach (var kv in data) {
        batch.Add(kv);
        if (batch.Count == BatchSize) { await engine.WriteBatch(batch); batch.Clear(); }
    }
    if (batch.Count > 0) await engine.WriteBatch(batch);
    await engine.FlushDurable();
    sw.Stop();
    var writeMs = sw.Elapsed.TotalMilliseconds;
    var fileBytes = engine.FileBytes;

    // 2) Reopen / load (index load for Kvasar; connection open for SQLite)
    await engine.Close();
    sw.Restart();
    await engine.Open();
    sw.Stop();
    var openMs = sw.Elapsed.TotalMilliseconds;

    // 2a) Startup hydration: enumerate ALL entries (what the client cache does on launch).
    sw.Restart();
    var scanned = await engine.ScanAll();
    sw.Stop();
    var scanMs = sw.Elapsed.TotalMilliseconds;
    if (scanned != data.Length) Console.WriteLine($"  WARN {engine.Name}: scanned {scanned} != {data.Length}");

    // 2b) Kvasar only: rebuild index from the log (delete .kidx) to isolate the scan cost
    double? rebuildMs = null;
    var kidx = engine.IndexHintFile;
    if (kidx != null) {
        await engine.Close();
        try { if (File.Exists(kidx)) File.Delete(kidx); } catch { /* ignore */ }
        sw.Restart();
        await engine.Open();
        sw.Stop();
        rebuildMs = sw.Elapsed.TotalMilliseconds;
    }

    // 3) Multithreaded lookup (warm) — random existing keys
    await Warmup(engine, data, 5_000);
    var perThread = totalLookups / threads;
    var latencies = new long[threads][];
    var mismatches = 0;
    var workers = new Task[threads];
    var barrier = new Barrier(threads + 1);
    for (var t = 0; t < threads; t++) {
        var tid = t;
        workers[t] = Task.Run(async () => {
            var rnd = new Random(1000 + tid);
            var lat = new long[perThread];
            var local = 0;
            barrier.SignalAndWait();
            for (var i = 0; i < perThread; i++) {
                var idx = rnd.Next(data.Length);
                var t0 = Stopwatch.GetTimestamp();
                var v = await engine.Get(data[idx].Key);
                lat[i] = Stopwatch.GetTimestamp() - t0;
                if (v == null || v.Length != data[idx].Value.Length) local++;
            }
            latencies[tid] = lat;
            if (local > 0) Interlocked.Add(ref mismatches, local);
        });
    }
    barrier.SignalAndWait();
    var lsw = Stopwatch.StartNew();
    await Task.WhenAll(workers);
    lsw.Stop();

    await engine.Close();
    await engine.DisposeAsync();

    var all = MergeSorted(latencies);
    var actualLookups = (long)perThread * threads;
    var name = engine.Name;
    return new Result(name, N / (writeMs / 1000.0), fileBytes, openMs, scanMs, rebuildMs,
        actualLookups / (lsw.Elapsed.TotalSeconds), Pct(all, 0.50), Pct(all, 0.99), mismatches);
}

async Task Warmup(IKvEngine engine, (byte[] Key, byte[] Value)[] data, int count)
{
    var rnd = new Random(7);
    for (var i = 0; i < count; i++) _ = await engine.Get(data[rnd.Next(data.Length)].Key);
}

// ---- Helpers -------------------------------------------------------------
(byte[] Key, byte[] Value)[] GenData(int n, int size)
{
    var arr = new (byte[], byte[])[n];
    var rnd = new Random(12345 + size);
    for (var i = 0; i < n; i++) {
        var k = Encoding.ASCII.GetBytes($"kvasar-bench-key-{i:D33}"); // 17 + 33 = 50 bytes
        var v = new byte[size];
        rnd.NextBytes(v);
        arr[i] = (k, v);
    }
    return arr;
}

string NewDir(string tag) { var d = Path.Combine(root, tag + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

static long[] MergeSorted(long[][] parts)
{
    var total = 0;
    foreach (var p in parts) total += p?.Length ?? 0;
    var all = new long[total];
    var o = 0;
    foreach (var p in parts) { if (p == null) continue; Array.Copy(p, 0, all, o, p.Length); o += p.Length; }
    Array.Sort(all);
    return all;
}

static double Pct(long[] sortedTicks, double q)
{
    if (sortedTicks.Length == 0) return 0;
    var idx = (int)Math.Clamp(q * (sortedTicks.Length - 1), 0, sortedTicks.Length - 1);
    return sortedTicks[idx] * 1_000_000.0 / Stopwatch.Frequency; // microseconds
}

void PrintTable(List<Result> rows)
{
    Console.WriteLine($"{"Engine",-24} {"Write k/s",11} {"File MB",9} {"Open ms",9} {"Scan ms",9} {"Startup ms",11} {"Lookup k/s",12} {"p50 us",8} {"p99 us",8}");
    Console.WriteLine(new string('-', 24 + 11 + 9 + 9 + 9 + 11 + 12 + 8 + 8 + 8));
    foreach (var r in rows)
        Console.WriteLine($"{r.Name,-24} {r.WriteKps / 1000.0,11:N1} {r.FileBytes / 1e6,9:N1} {r.OpenMs,9:N1} {r.ScanMs,9:N1} {r.OpenMs + r.ScanMs,11:N1} {r.LookupPerSec / 1000.0,12:N1} {r.P50Us,8:N1} {r.P99Us,8:N1}");
    Console.WriteLine("(Startup ms = Open + full ListAllEntries scan — the client cache's launch-time hydration cost)");
}

int ArgInt(string name, int dflt) { var s = ArgStr(name, null); return s != null && int.TryParse(s, out var v) ? v : dflt; }
string? ArgStr(string name, string? dflt)
{
    var a = Environment.GetCommandLineArgs();
    for (var i = 1; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return dflt;
}

record Result(
    string Name, double WriteKps, long FileBytes, double OpenMs, double ScanMs, double? RebuildMs,
    double LookupPerSec, double P50Us, double P99Us, int Mismatches);
