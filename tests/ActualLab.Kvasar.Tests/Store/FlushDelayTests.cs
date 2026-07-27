using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

// KvasarOptions.FlushDelay defers sealing and the disk write. The contract being tested: it delays only
// *durability*, never *visibility* — a write is readable the instant Set returns — and a crash may drop
// writes newer than the last flush but must never corrupt the store or lose older, flushed data.
public class FlushDelayTests : IDisposable
{
    private readonly string _dir;
    private readonly byte[] _key;

    public FlushDelayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kvasar-flushdelay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _key = new byte[32];
        for (var i = 0; i < 32; i++)
            _key[i] = (byte)(i * 5 + 11);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task WritesAreVisibleImmediatelyEvenThoughUnflushed()
    {
        // The whole point of doing this inside the store rather than in a LazyWriter above it: the record
        // is published to the index synchronously, so every reader sees it at once regardless of caching.
        var basePath = Path.Combine(_dir, "visible");
        await using var store = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)));
        for (var i = 0; i < 200; i++)
            await store.Set(Key(i), Value(i, 300));

        for (var i = 0; i < 200; i++) {
            var value = await store.Get(Key(i));
            value.Should().NotBeNull($"key {i} must be readable before any flush");
            value!.Value.ToArray().Should().Equal(Value(i, 300));
        }

        var scanned = new List<string>();
        await foreach (var (key, _) in store.Scan())
            scanned.Add(Encoding.UTF8.GetString(key.Span));
        scanned.Should().HaveCount(200, "Scan must see unflushed writes too");
    }

    [Fact]
    public async Task DeferredWritesPackDenselyInsteadOfBurningAPagePerSet()
    {
        // Durable mode seals on every Set, so each record pads out a full page. Deferring the seal is what
        // makes an unbatched Set cheap in space as well as time.
        const int count = 500;
        const int valueSize = 200;
        var eager = await FileBytesAfterUnbatchedSets(Path.Combine(_dir, "eager"), TimeSpan.Zero, count, valueSize);
        var deferred = await FileBytesAfterUnbatchedSets(
            Path.Combine(_dir, "deferred"), TimeSpan.FromSeconds(30), count, valueSize);

        var payload = (long)count * valueSize;
        deferred.Should().BeLessThan(eager / 4, "deferred sealing must pack many records per page");
        deferred.Should().BeLessThan(payload * 3, "the file should be within a small factor of the payload");
    }

    [Fact]
    public async Task ExplicitFlushMakesDeferredWritesDurable()
    {
        var basePath = Path.Combine(_dir, "explicit");
        await using (var store = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)))) {
            for (var i = 0; i < 100; i++)
                await store.Set(Key(i), Value(i, 250));
            await store.Flush();
        }

        await using var reopened = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)));
        for (var i = 0; i < 100; i++)
            (await reopened.Get(Key(i))).Should().NotBeNull($"key {i} was flushed before close");
    }

    [Fact]
    public async Task BackgroundFlushEventuallyPersistsWithoutAnExplicitFlush()
    {
        var basePath = Path.Combine(_dir, "background");
        var snapshotDir = Path.Combine(_dir, "background-snap");
        await using var store = await KvasarStore.Open(Options(basePath, TimeSpan.FromMilliseconds(50)));
        for (var i = 0; i < 50; i++)
            await store.Set(Key(i), Value(i, 200));

        // No explicit flush: the timer alone has to get the data onto disk. Poll rather than sleep once,
        // so the test doesn't depend on a single timing guess.
        var recovered = 0;
        for (var attempt = 0; attempt < 40 && recovered < 50; attempt++) {
            await Task.Delay(50);
            recovered = await CountRecoverableFrom(basePath, snapshotDir + attempt);
        }
        recovered.Should().Be(50, "the background flusher must persist writes without an explicit Flush");
    }

    [Fact]
    public async Task AbortLosesOnlyRecentWritesAndKeepsFlushedOnesIntact()
    {
        // The accepted failure mode: writes newer than the last flush may vanish. Anything already flushed
        // must survive, and the store must reopen cleanly and stay usable.
        var basePath = Path.Combine(_dir, "abort");
        var snapshotDir = Path.Combine(_dir, "abort-snap");
        await using (var store = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)))) {
            for (var i = 0; i < 100; i++)
                await store.Set(Key(i), Value(i, 220));
            await store.Flush(); // everything below 100 is now durable
            for (var i = 100; i < 300; i++)
                await store.Set(Key(i), Value(i, 220)); // may or may not survive
            Snapshot(basePath, snapshotDir);
        }

        await using var recovered = await KvasarStore.Open(Options(Path.Combine(snapshotDir, "abort"), TimeSpan.Zero));
        for (var i = 0; i < 100; i++) {
            var value = await recovered.Get(Key(i));
            value.Should().NotBeNull($"key {i} was flushed before the abort");
            value!.Value.ToArray().Should().Equal(Value(i, 220));
        }
        // Whatever survived from the unflushed tail must at least be correct — never another key's bytes.
        for (var i = 100; i < 300; i++) {
            var value = await recovered.Get(Key(i));
            if (value is not null)
                value.Value.ToArray().Should().Equal(Value(i, 220), $"key {i} must not come back corrupted");
        }
        await recovered.Set(Key(9999), Value(9999, 220));
        (await recovered.Get(Key(9999))).Should().NotBeNull("the store must stay usable after recovery");
    }

    [Fact]
    public async Task UncleanShutdownNeverReusesAPageIdItMayHaveLost()
    {
        // A crash under deferred commit can leave a half-written trailing page, so appending at the
        // committed extent would re-encrypt different data under an already-used GCM nonce. The
        // never-rewind rule resumes at the *physical* end instead, so the file only ever grows — which is
        // what replaced the .clean marker and the roll-to-a-new-segment this test used to assert (§5.2.1).
        var basePath = Path.Combine(_dir, "nonce");
        var snapshotDir = Path.Combine(_dir, "nonce-snap");
        await using (var store = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)))) {
            for (var i = 0; i < 50; i++)
                await store.Set(Key(i), Value(i, 300));
            await store.Flush();
            Snapshot(basePath, snapshotDir); // copy while open == killed process
        }

        var snapshotBase = Path.Combine(snapshotDir, "nonce");
        var dataPath = $"{snapshotBase}.0.kdat";
        File.Exists(dataPath).Should().BeTrue();
        // Tear the tail, which is what an abort mid-page leaves behind.
        using (var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Write))
            fs.SetLength(fs.Length - 11);
        var tornLength = new FileInfo(dataPath).Length;

        await using (var recovered = await KvasarStore.Open(Options(snapshotBase, TimeSpan.FromSeconds(30)))) {
            await recovered.Set(Key(1000), Value(1000, 300));
            await recovered.Flush();
        }

        new FileInfo(dataPath).Length.Should().BeGreaterThan(tornLength,
            "recovery must append above the torn page rather than re-issue its page id");
        Directory.GetFiles(snapshotDir, "nonce.*.kdat").Should()
            .HaveCount(2, "the file set is fixed: recovery never creates a file");

        await using var reopened = await KvasarStore.Open(Options(snapshotBase, TimeSpan.FromSeconds(30)));
        (await reopened.Get(Key(1000))).Should().NotBeNull("the post-recovery write must survive");
    }

    [Fact]
    public async Task CleanShutdownKeepsAppendingToTheSameFile()
    {
        // The counterpart: a graceful close commits everything, so there is no burned range and reopening
        // costs nothing — and, unlike the segment model, cannot strand a file per launch.
        var basePath = Path.Combine(_dir, "clean");
        await using (var store = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)))) {
            for (var i = 0; i < 50; i++)
                await store.Set(Key(i), Value(i, 300));
        }
        var lengthAfterClose = new FileInfo($"{basePath}.0.kdat").Length;

        await using (var reopened = await KvasarStore.Open(Options(basePath, TimeSpan.FromSeconds(30)))) {
            await reopened.Set(Key(1000), Value(1000, 300));
            (await reopened.Get(Key(0))).Should().NotBeNull("a clean close must persist everything");
            await reopened.Flush();
        }

        // The reopen appended into the same file: no page id was burned, so nothing was skipped either.
        var lengthAfterReopen = new FileInfo($"{basePath}.0.kdat").Length;
        lengthAfterReopen.Should().BeGreaterThan(lengthAfterClose);
        lengthAfterReopen.Should().BeLessThan(lengthAfterClose * 2);
    }

    // Private methods

    private async Task<long> FileBytesAfterUnbatchedSets(string basePath, TimeSpan flushDelay, int count, int valueSize)
    {
        await using var store = await KvasarStore.Open(Options(basePath, flushDelay));
        for (var i = 0; i < count; i++)
            await store.Set(Key(i), Value(i, valueSize));
        await store.Flush();
        return store.Stats.FileBytes;
    }

    private async Task<int> CountRecoverableFrom(string basePath, string snapshotDir)
    {
        Snapshot(basePath, snapshotDir);
        await using var store = await KvasarStore.Open(Options(Path.Combine(snapshotDir, Path.GetFileName(basePath)), TimeSpan.Zero));
        var found = 0;
        for (var i = 0; i < 50; i++)
            if (await store.Get(Key(i)) is not null)
                found++;
        return found;
    }

    // Copies a live store's files, which is what a killed process would leave behind.
    private static void Snapshot(string basePath, string snapshotDir)
    {
        Directory.CreateDirectory(snapshotDir);
        var dir = Path.GetDirectoryName(Path.GetFullPath(basePath))!;
        var name = Path.GetFileName(basePath);
        foreach (var path in Directory.EnumerateFiles(dir, name + ".*")) {
            if (path.EndsWith(".lock", StringComparison.Ordinal))
                continue;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var target = new FileStream(Path.Combine(snapshotDir, Path.GetFileName(path)),
                FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private KvasarOptions Options(string basePath, TimeSpan flushDelay)
        => new() {
            BasePath = basePath,
            EncryptionKey = _key,
            PageSize = 4096,
            PageCacheBytes = 4L * 1024 * 1024,
            FlushDelay = flushDelay,
        };

    private static KvasarKey Key(int index) => Encoding.UTF8.GetBytes($"fd-key-{index:D6}");

    private static byte[] Value(int index, int size)
    {
        var value = new byte[size];
        for (var i = 0; i < size; i++)
            value[i] = (byte)(index * 31 + i);
        return value;
    }
}
