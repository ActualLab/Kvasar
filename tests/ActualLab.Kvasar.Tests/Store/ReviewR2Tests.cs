using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Store;

public sealed class ReviewR2Tests : IDisposable
{
    private const int PageSize = 4096;
    private const int CompactionKeyCount = 800;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-review-r2-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _encryptionKey = new byte[32];
    private readonly ITestOutputHelper _output;

    public ReviewR2Tests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _encryptionKey.Length; i++)
            _encryptionKey[i] = (byte)(i * 11 + 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWriteOwnsCallerBuffersBeforeReturning(bool isBatch)
    {
        var backend = new PausingWriteBackend(FileStorageBackend.Instance);
        var options = Options(backend) with {
            DisableEncryption = true,
            FlushDelay = TimeSpan.FromHours(1),
            CommitBytes = long.MaxValue,
        };
        await using var store = await KvasarStore.Open(options);

        var seedKey = Encoding.UTF8.GetBytes("seed");
        var targetLength = 256 * PageSize;
        var seedValueLength = targetLength;
        while (RecordCodec.GetRecordLength(seedKey.Length, seedValueLength, false) > targetLength)
            seedValueLength--;
        RecordCodec.GetRecordLength(seedKey.Length, seedValueLength, false).Should().Be(targetLength);
        await store.Set(seedKey, new byte[seedValueLength]);
        await store.Set("tail", "x");

        var key = Encoding.UTF8.GetBytes(isBatch ? "batch-original" : "set-original");
        var value = Enumerable.Repeat((byte)0x5A, PageSize * 2).ToArray();
        var expectedKey = key.ToArray();
        var expectedValue = value.ToArray();
        var updates = new List<(KvasarKey Key, KvasarValue? Value)> { (key, value) };
        using var cts = new CancellationTokenSource();

        backend.Arm();
        var writeTask = isBatch
            ? store.SetMany(updates, cts.Token).AsTask()
            : store.Set(key, value, cts.Token).AsTask();
        await backend.WhenPaused.WaitAsync(TimeSpan.FromSeconds(30));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writeTask);

        key.AsSpan().Fill(0x33);
        value.AsSpan().Fill(0x44);
        updates.Clear();
        updates.Add((Encoding.UTF8.GetBytes("batch-replacement"), Encoding.UTF8.GetBytes("replacement")));
        backend.Resume();

        await store.Flush();
        (await store.Get(expectedKey))!.Value.ToArray().Should().Equal(expectedValue);
        (await store.Get(key)).Should().BeNull();
    }

    [Fact]
    public async Task TransientReadFailureAbortsCompactionWithoutChangingTheIndex()
    {
        var backend = new FailingReadBackend(FileStorageBackend.Instance, PageSize);
        var options = Options(backend) with {
            DisableEncryption = true,
            PageCacheBytes = PageSize,
            CompactionMinBytes = long.MaxValue,
        };
        var keys = Enumerable.Range(0, 200).Select(i => $"key-{i:D4}").ToArray();
        await using (var store = await KvasarStore.Open(options)) {
            foreach (var key in keys)
                await store.Set(key, new byte[PageSize - 128]);
            // Overwrite half of them, so the reopened store has genuinely dead bytes: CompactLocked skips
            // the pass entirely when DeadBytes is 0, and the accounting is exact now that recovery
            // restores the persisted counters rather than over-counting page padding (R9/R18).
            foreach (var key in keys.Where((_, i) => i % 2 == 0))
                await store.Set(key, new byte[PageSize - 128]);
            await store.Flush();
        }

        await using var reopened = await KvasarStore.Open(options);
        backend.Arm();
        var compact = async () => await reopened.Compact();
        await compact.Should().ThrowAsync<IOException>();
        backend.Disarm();

        foreach (var key in keys)
            (await reopened.Get(key)).Should().NotBeNull();
        await reopened.Compact();
        foreach (var key in keys)
            (await reopened.Get(key)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptCommittedIndexRebuildsFromDataWithoutWiping(bool isDelta)
    {
        const int checkpointKeyCount = 60;
        const int keyCount = 100;
        var options = Options(FileStorageBackend.Instance);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < checkpointKeyCount; i++)
                await store.Set($"key-{i:D4}", $"value-{i:D4}");
            await store.Flush();
        }

        var preRotationState = await ReadSuperblock();
        using (var file = File.OpenWrite($"{options.BasePath}.{preRotationState.IndexSlot}.kidx"))
            file.SetLength(IndexLog.HeaderSize);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = checkpointKeyCount; i < keyCount; i++)
                await store.Set($"key-{i:D4}", $"value-{i:D4}");
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var indexPath = $"{options.BasePath}.{state.IndexSlot}.kidx";
        var indexBytes = await File.ReadAllBytesAsync(indexPath);
        var checkpointCount = BinaryPrimitives.ReadInt64LittleEndian(indexBytes.AsSpan(16, 8));
        var checkpointEnd = IndexLog.HeaderSize + (checkpointCount * IndexLog.EntrySize);
        checkpointCount.Should().BeGreaterThan(0);
        state.IndexCommitLength.Should().BeGreaterThan(checkpointEnd);
        var corruptOffset = isDelta ? checkpointEnd : IndexLog.HeaderSize;
        indexBytes[checked((int)corruptOffset)] ^= 0x80;
        await File.WriteAllBytesAsync(indexPath, indexBytes);

        var dataBefore = DataPaths(options.BasePath).ToDictionary(
            path => path, File.ReadAllBytes, StringComparer.Ordinal);
        await using (var reopened = await KvasarStore.Open(options)) {
            for (var i = 0; i < keyCount; i++)
                (await reopened.Get($"key-{i:D4}"))!.Value.AsString.Should().Be($"value-{i:D4}");
        }
        foreach (var (path, bytes) in dataBefore)
            File.ReadAllBytes(path).Should().Equal(bytes, "index recovery must not wipe or rewrite data files");
    }

    [Fact]
    public async Task WritersYieldThroughALargeCompaction()
    {
        var backend = new CompactionGateBackend(FileStorageBackend.Instance);
        var options = CompactionOptions(backend);
        await SeedCompactionStore(options);
        await using var store = await KvasarStore.Open(options);

        backend.Arm(false);
        var compactTask = store.Compact().AsTask();
        await backend.WhenPaused.WaitAsync(TimeSpan.FromSeconds(30));

        var maxStall = TimeSpan.Zero;
        var written = Enumerable.Range(0, 128).Select(i => $"during-{i:D3}").ToArray();
        var whenFirstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerTask = Task.Run(async () => {
            for (var i = 0; i < written.Length; i++) {
                var startedAt = Stopwatch.GetTimestamp();
                await store.Set(written[i], "written-during-compaction");
                maxStall = Max(maxStall, Stopwatch.GetElapsedTime(startedAt));
                if (i == 15)
                    whenFirstWave.TrySetResult();
            }
        });
        var didWriterProgress = await CompletesWithin(whenFirstWave.Task, TimeSpan.FromMilliseconds(500));
        backend.Resume();
        await writerTask;
        await compactTask;

        didWriterProgress.Should().BeTrue("a paused compaction read must not hold the global write lock");
        maxStall.Should().BeLessThan(
            TimeSpan.FromMilliseconds(100), "bounded compaction batches must yield promptly to a queued writer");
        foreach (var key in written)
            Encoding.UTF8.GetString((await store.Get(key))!.Value.Span).Should().Be("written-during-compaction");
        _output.WriteLine($"R14 writer max stall: {maxStall.TotalMilliseconds:F3} ms");
    }

    [Fact]
    public async Task WriteAfterTheCopiedVersionWins()
    {
        var backend = new CompactionGateBackend(FileStorageBackend.Instance);
        var options = CompactionOptions(backend);
        await SeedCompactionStore(options);
        await using var store = await KvasarStore.Open(options);

        backend.Arm(true);
        var compactTask = store.Compact().AsTask();
        await backend.WhenPaused.WaitAsync(TimeSpan.FromSeconds(30));

        var writerTask = store.Set(CompactionKey(0), "new-version").AsTask();
        var didWriterFinish = await CompletesWithin(writerTask, TimeSpan.FromMilliseconds(500));
        backend.Resume();
        await writerTask;
        await compactTask;

        didWriterFinish.Should().BeTrue("the writer must run between completed compaction batches");
        Encoding.UTF8.GetString((await store.Get(CompactionKey(0)))!.Value.Span).Should().Be("new-version");
    }

    [Fact]
    public async Task CancelledPassWithAnInterleavedWriteLeavesEveryKeyReadable()
    {
        var backend = new CompactionGateBackend(FileStorageBackend.Instance);
        var options = CompactionOptions(backend);
        await SeedCompactionStore(options);
        await using var store = await KvasarStore.Open(options);
        using var cancellationSource = new CancellationTokenSource();

        backend.Arm(true);
        var compactTask = store.Compact(cancellationSource.Token).AsTask();
        await backend.WhenPaused.WaitAsync(TimeSpan.FromSeconds(30));

        var writerTask = store.Set("during-cancel", "survives").AsTask();
        var didWriterFinish = await CompletesWithin(writerTask, TimeSpan.FromMilliseconds(500));
        await cancellationSource.CancelAsync();
        backend.Resume();
        await writerTask;
        await IgnoreCancellation(compactTask);

        didWriterFinish.Should().BeTrue("the writer must run before the paused pass is cancelled");
        await AssertCompactionValues(store);
        Encoding.UTF8.GetString((await store.Get("during-cancel"))!.Value.Span).Should().Be("survives");
    }

    [Fact]
    public async Task ReopenAfterCancellationBetweenBatchesReturnsEveryCommittedKey()
    {
        var backend = new CompactionGateBackend(FileStorageBackend.Instance);
        var options = CompactionOptions(backend);
        await SeedCompactionStore(options);
        using var cancellationSource = new CancellationTokenSource();

        await using (var store = await KvasarStore.Open(options)) {
            backend.Arm(true);
            var compactTask = store.Compact(cancellationSource.Token).AsTask();
            await backend.WhenPaused.WaitAsync(TimeSpan.FromSeconds(30));

            var writerTask = Task.Run(async () => {
                await store.Set("committed-during-pass", "survives-reopen");
                await store.Flush();
            });
            var didWriterFinish = await CompletesWithin(writerTask, TimeSpan.FromMilliseconds(500));
            await cancellationSource.CancelAsync();
            backend.Resume();
            await writerTask;
            await IgnoreCancellation(compactTask);
            didWriterFinish.Should().BeTrue("the committed writer must run before cancellation");
        }

        await using var reopened = await KvasarStore.Open(options);
        await AssertCompactionValues(reopened);
        Encoding.UTF8.GetString((await reopened.Get("committed-during-pass"))!.Value.Span)
            .Should().Be("survives-reopen");
    }

    private KvasarOptions Options(IStorageBackend storageBackend) => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _encryptionKey,
        PageSize = PageSize,
        StorageBackend = storageBackend,
        FlushDelay = TimeSpan.Zero,
    };

    private async Task<SuperblockState> ReadSuperblock()
    {
        await using var file = await FileStorageBackend.Instance.Open(Path.Combine(_dir, "store.kvs"));
        var read = await new Superblock(_encryptionKey, 1).Read(file);
        read.Status.Should().Be(SuperblockStatus.Ok);
        return read.Newest!.Value;
    }

    private static string[] DataPaths(string basePath)
        => [$"{basePath}.0.kdat", $"{basePath}.1.kdat"];
    private KvasarOptions CompactionOptions(IStorageBackend storageBackend)
        => Options(storageBackend) with {
            DisableEncryption = true,
            PageCacheBytes = PageSize,
            FlushDelay = TimeSpan.FromHours(1),
            CommitBytes = long.MaxValue,
            CompactionMinBytes = long.MaxValue,
        };

    private static async Task SeedCompactionStore(KvasarOptions options)
    {
        var updates = new List<(KvasarKey Key, KvasarValue? Value)>(CompactionKeyCount);
        for (var i = 0; i < CompactionKeyCount; i++)
            updates.Add((CompactionKey(i), CompactionValue(i, 0)));

        await using var store = await KvasarStore.Open(options);
        await store.SetMany(updates);
        updates.Clear();
        for (var i = 0; i < CompactionKeyCount; i += 4)
            updates.Add((CompactionKey(i), CompactionValue(i, 1)));
        await store.SetMany(updates);
        await store.Flush();
    }

    private static async Task AssertCompactionValues(KvasarStore store)
    {
        for (var i = 0; i < CompactionKeyCount; i++) {
            var version = i % 4 == 0 ? 1 : 0;
            (await store.Get(CompactionKey(i)))!.Value.ToArray().Should().Equal(CompactionValue(i, version));
        }
    }

    private static async Task<bool> CompletesWithin(Task task, TimeSpan timeout)
        => await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;

    private static async Task IgnoreCancellation(Task task)
    {
        try {
            await task;
        }
        catch (OperationCanceledException) {
        }
    }

    private static string CompactionKey(int index)
        => $"compact-{index:D4}";

    private static byte[] CompactionValue(int index, int version)
    {
        var value = new byte[3072];
        BitConverter.TryWriteBytes(value, index);
        BitConverter.TryWriteBytes(value.AsSpan(sizeof(int)), version);
        value.AsSpan(2 * sizeof(int)).Fill((byte)(index * 17 + version * 31));
        return value;
    }

    private static TimeSpan Max(TimeSpan x, TimeSpan y)
        => x >= y ? x : y;

    private sealed class CompactionGateBackend(IStorageBackend backend) : IStorageBackend
    {
        private TaskCompletionSource _whenPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _whenResumed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isArmed;
        private int _hasTargetDataWrite;
        private bool _mustObserveTargetDataWrite;

        public Task WhenPaused => _whenPaused.Task;
        internal Task WhenResumed => _whenResumed.Task;

        public void Arm(bool mustObserveTargetDataWrite)
        {
            _whenPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _whenResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _mustObserveTargetDataWrite = mustObserveTargetDataWrite;
            _hasTargetDataWrite = 0;
            _isArmed = 1;
        }

        public void Resume()
            => _whenResumed.TrySetResult();

        public async ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
            => new CompactionGateFile(
                await backend.Open(path, cancellationToken).ConfigureAwait(false), this, path);

        public bool Exists(string path)
            => backend.Exists(path);
        public void Delete(string path)
            => backend.Delete(path);
        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);

        internal async ValueTask PauseRead(string path, CancellationToken cancellationToken)
        {
            if (!path.EndsWith(".0.kdat", StringComparison.Ordinal)
                || _mustObserveTargetDataWrite && Volatile.Read(ref _hasTargetDataWrite) == 0
                || Interlocked.Exchange(ref _isArmed, 0) == 0)
                return;

            _whenPaused.TrySetResult();
            await _whenResumed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        internal void ObserveWrite(string path, long offset)
        {
            if (offset >= KvasarConstants.SegmentHeaderSize
                && path.EndsWith(".1.kdat", StringComparison.Ordinal))
                Volatile.Write(ref _hasTargetDataWrite, 1);
        }
    }

    private sealed class CompactionGateFile(
        IStorageFile file, CompactionGateBackend backend, string path) : IStorageFile
    {
        public long Length => file.Length;

        public ValueTask DisposeAsync()
            => file.DisposeAsync();
        public async ValueTask<int> Read(
            long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await backend.PauseRead(path, cancellationToken).ConfigureAwait(false);
            return await file.Read(offset, buffer, cancellationToken).ConfigureAwait(false);
        }
        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            backend.ObserveWrite(path, offset);
            return file.Write(offset, buffer);
        }
        public ValueTask FlushToDisk()
            => file.FlushToDisk();
        public ValueTask Truncate(long length)
            => file.Truncate(length);
    }

    private sealed class PausingWriteBackend(IStorageBackend backend) : IStorageBackend
    {
        private TaskCompletionSource _whenPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _whenResumed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _isArmed;

        public Task WhenPaused => _whenPaused.Task;
        internal Task WhenResumed => _whenResumed.Task;

        public void Arm()
        {
            _whenPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _whenResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _isArmed = 1;
        }

        public void Resume()
            => _whenResumed.TrySetResult();

        public async ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
            => new PausingWriteFile(
                await backend.Open(path, cancellationToken).ConfigureAwait(false), this, path);

        public bool Exists(string path)
            => backend.Exists(path);
        public void Delete(string path)
            => backend.Delete(path);
        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);

        internal bool Pause(string path)
        {
            if (!path.EndsWith(".kdat", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _isArmed, 0) == 0)
                return false;
            _whenPaused.TrySetResult();
            return true;
        }
    }

    private sealed class PausingWriteFile(
        IStorageFile file, PausingWriteBackend backend, string path) : IStorageFile
    {
        public long Length => file.Length;

        public ValueTask DisposeAsync()
            => file.DisposeAsync();
        public ValueTask<int> Read(
            long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
            => file.Read(offset, buffer, cancellationToken);
        public async ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            if (backend.Pause(path))
                await backend.WhenResumed.ConfigureAwait(false);
            await file.Write(offset, buffer).ConfigureAwait(false);
        }
        public ValueTask FlushToDisk()
            => file.FlushToDisk();
        public ValueTask Truncate(long length)
            => file.Truncate(length);
    }

    private sealed class FailingReadBackend(IStorageBackend backend, int pageSize) : IStorageBackend
    {
        private int _isArmed;

        public void Arm()
            => _isArmed = 1;
        public void Disarm()
            => _isArmed = 0;

        public async ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
            => new FailingReadFile(
                await backend.Open(path, cancellationToken).ConfigureAwait(false), this, path);

        public bool Exists(string path)
            => backend.Exists(path);
        public void Delete(string path)
            => backend.Delete(path);
        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);

        internal void Fail(string path, int length)
        {
            if (length == pageSize && path.EndsWith(".kdat", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _isArmed, 0) == 1)
                throw new IOException("Injected transient compaction read failure.");
        }
    }

    private sealed class FailingReadFile(
        IStorageFile file, FailingReadBackend backend, string path) : IStorageFile
    {
        public long Length => file.Length;

        public ValueTask DisposeAsync()
            => file.DisposeAsync();
        public ValueTask<int> Read(
            long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            backend.Fail(path, buffer.Length);
            return file.Read(offset, buffer, cancellationToken);
        }
        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
            => file.Write(offset, buffer);
        public ValueTask FlushToDisk()
            => file.FlushToDisk();
        public ValueTask Truncate(long length)
            => file.Truncate(length);
    }
}
