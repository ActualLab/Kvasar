using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

// Recovery from a real SIGKILL, not a graceful close. A child process writes in a tight loop and is
// killed at a random point *while writing*, so the store is left exactly as an aborted process leaves
// it — including whatever the OS was midway through. That last part is the bit an in-process test
// cannot reproduce (see CrashFuzzTests for the systematic fault-injection side).
//
// The contract under test: Set returns only after the record's bytes reached the OS (seal-before-
// publish), so every record the worker acknowledged must survive the kill, even though the buffered
// .kidx delta tail almost certainly did not — recovery has to replay the log past the stale HWM.
[Trait("Category", "Slow")]
public class ProcessCrashRecoveryTests : IDisposable
{
    private readonly string _dir;
    private readonly byte[] _key;

    public ProcessCrashRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kvasar-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _key = new byte[32];
        for (var i = 0; i < 32; i++)
            _key[i] = (byte)(i * 7 + 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData(200, 4096, 137)]    // single-page values
    [InlineData(9000, 4096, 421)]   // values larger than a page => multi-page runs
    [InlineData(200, 8192, 733)]    // a different page size
    public async Task EveryAcknowledgedWriteSurvivesAKill(int valueSize, int pageSize, int seed)
    {
        var basePath = Path.Combine(_dir, $"crash{seed}");
        var acknowledged = await RunAndKill(basePath, valueSize, pageSize, seed);
        acknowledged.Should().BeGreaterThan(4, "the worker must get far enough in to make the test meaningful");

        await using var store = await KvasarStore.Open(Options(basePath, pageSize));
        for (var i = 0; i <= acknowledged; i++) {
            var value = await store.Get(Key(i));
            value.Should().NotBeNull($"record {i} was acknowledged before the kill, so it reached the OS");
            value!.Value.ToArray().Should().Equal(ExpectedValue(i, valueSize));
        }

        // The store must also still be writable after recovering.
        await store.Set(Key(acknowledged + 1000), ExpectedValue(acknowledged + 1000, valueSize));
        (await store.Get(Key(acknowledged + 1000))).Should().NotBeNull();
    }

    [Fact]
    public async Task RepeatedKillsConvergeWithoutLosingEarlierData()
    {
        // Crash, recover, crash again on the recovered store. Each round must keep everything the
        // previous rounds acknowledged — recovery has to be idempotent, not merely survivable once.
        var basePath = Path.Combine(_dir, "repeat");
        var highWaterMark = 0;
        for (var round = 0; round < 3; round++) {
            var acknowledged = await RunAndKill(basePath, 300, 4096, 11 + round);
            acknowledged.Should().BeGreaterThan(0);
            highWaterMark = Math.Max(highWaterMark, acknowledged);

            await using var store = await KvasarStore.Open(Options(basePath, 4096));
            for (var i = 0; i <= highWaterMark; i++)
                (await store.Get(Key(i))).Should().NotBeNull($"record {i} survived an earlier round");
        }

        // A final clean open must agree with the last recovered view.
        await using var reopened = await KvasarStore.Open(Options(basePath, 4096));
        reopened.Stats.Entries.Should().BeGreaterThanOrEqualTo(highWaterMark + 1);
    }

    [Fact]
    public async Task ScanAgreesWithPointReadsAfterAKill()
    {
        var basePath = Path.Combine(_dir, "scan");
        var acknowledged = await RunAndKill(basePath, 250, 4096, 97);
        acknowledged.Should().BeGreaterThan(4);

        await using var store = await KvasarStore.Open(Options(basePath, 4096));
        var scanned = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var (key, value) in store.Scan())
            scanned[Encoding.UTF8.GetString(key.Span)] = value.ToArray();

        for (var i = 0; i <= acknowledged; i++) {
            var name = $"key-{i:D8}";
            scanned.Should().ContainKey(name);
            scanned[name].Should().Equal(ExpectedValue(i, 250));
        }
    }

    // Private methods

    // Starts the worker, lets it get going, then kills it at a randomized point mid-write.
    // Returns the highest record index the worker acknowledged on stdout before dying.
    private static async Task<int> RunAndKill(string basePath, int valueSize, int pageSize, int seed)
    {
        var workerPath = FindCrashWorker();
        var startInfo = new ProcessStartInfo {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add(basePath);
        startInfo.ArgumentList.Add(valueSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo)!;
        var acknowledged = -1;
        try {
            process.OutputDataReceived += (_, e) => {
                if (e.Data is { } line && line.StartsWith("w:", StringComparison.Ordinal)
                    && int.TryParse(line.AsSpan(2), out var index))
                    Interlocked.Exchange(ref acknowledged, index);
            };
            process.BeginOutputReadLine();

            // Wait for real progress, then kill after a randomized extra delay so the abort lands at an
            // arbitrary point in the write path rather than always at the same place.
            var rnd = new Random(seed);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref acknowledged) < 5 && DateTime.UtcNow < deadline && !process.HasExited)
                await Task.Delay(10);
            await Task.Delay(rnd.Next(20, 400));

            var survived = Volatile.Read(ref acknowledged);
            process.HasExited.Should().BeFalse("the worker writes forever; it must still be running when killed");
            return survived;
        }
        finally {
            if (!process.HasExited) {
                process.Kill(entireProcessTree: true);
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try {
                    // WaitForExit also guarantees the OS has released the store's file handles and the .lock.
                    await process.WaitForExitAsync(cleanupCts.Token);
                }
                catch (OperationCanceledException) {
                }
            }
        }
    }

    private static string FindCrashWorker()
    {
        // Walk up to the artifacts root, then locate the worker wherever the current configuration and
        // target framework put it (release/debug, and the _netX.0 suffixes under multitargeting).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !string.Equals(dir.Name, "artifacts", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var candidates = Directory
            .EnumerateFiles(dir!.FullName, "ActualLab.Kvasar.CrashWorker.dll", SearchOption.AllDirectories)
            .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
        candidates.Should().NotBeEmpty("the tests project references the crash worker, so it must be built");
        return candidates[0];
    }

    private KvasarOptions Options(string basePath, int pageSize)
        => new() {
            BasePath = basePath,
            EncryptionKey = _key,
            PageSize = pageSize,
            PageCacheBytes = 4L * 1024 * 1024,
        };

    private static KvasarKey Key(int index) => Encoding.UTF8.GetBytes($"key-{index:D8}");

    private static byte[] ExpectedValue(int index, int size)
    {
        var value = new byte[size];
        for (var i = 0; i < size; i++)
            value[i] = (byte)(index + i);
        return value;
    }
}
