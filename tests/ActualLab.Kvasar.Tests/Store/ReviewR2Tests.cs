using System.Text;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Store;

public sealed class ReviewR2Tests : IDisposable
{
    private const int PageSize = 4096;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-review-r2-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _encryptionKey = new byte[32];

    public ReviewR2Tests()
    {
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

        await store.Flush(true);
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
            await store.Flush(true);
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

    private KvasarOptions Options(IStorageBackend storageBackend) => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _encryptionKey,
        PageSize = PageSize,
        StorageBackend = storageBackend,
        FlushDelay = TimeSpan.Zero,
    };

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
