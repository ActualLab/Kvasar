using System.Text;
using System.Reflection;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Store;

public sealed class Round3EdgeCaseTests : IDisposable
{
    private const int PageSize = 4096;
    private const int FillerCount = 15;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "kvasar-round3-edge-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _encryptionKey = new byte[32];

    public Round3EdgeCaseTests()
    {
        Directory.CreateDirectory(_dir);
        new Random(7319).NextBytes(_encryptionKey);
    }

    public void Dispose()
    {
        try {
            Directory.Delete(_dir, true);
        }
        catch {
        }
    }

    [Fact]
    public async Task ScanRejectsACollidingRecordAtARecycledSnapshotLocator()
    {
        var backend = new SlotRecycleGateBackend(FileStorageBackend.Instance);
        var options = new KvasarOptions {
            BasePath = Path.Combine(_dir, "store"),
            EncryptionKey = _encryptionKey,
            Hasher = new ConstantHasher(),
            DisableEncryption = true,
            StorageBackend = backend,
            PageSize = PageSize,
            PageCacheBytes = PageSize,
            FlushDelay = TimeSpan.Zero,
            CompactionMinBytes = long.MaxValue,
        };
        var keyA = Key("a");
        var keyB = Key("b");
        var trailingKey = Key("trailing");
        var fillerKeys = Enumerable.Range(0, FillerCount).Select(i => Key($"f{i:D2}")).ToArray();
        await using (var seed = await KvasarStore.Open(options)) {
            await seed.Set(keyA, PageValue(keyA, 0xA1));
            foreach (var fillerKey in fillerKeys)
                await seed.Set(fillerKey, PageValue(fillerKey, 0xF1));
            await seed.Set(keyB, PageValue(keyB, 0xB1));
            await seed.Set(trailingKey, PageValue(trailingKey, 0xC1));
            await seed.Flush();
        }

        await using var store = await KvasarStore.Open(options);
        (await store.Get(keyB)).Should().NotBeNull();
        ClearCache(store);

        backend.ArmScan();
        var scanTask = Task.Run(async () => {
            var items = new List<(string Key, byte[] Value)>();
            await foreach (var (key, value) in store.Scan())
                items.Add((key.AsString, value.ToArray()));
            return items;
        });
        var scanPauseTask = backend.WhenScanPaused;
        if (await Task.WhenAny(scanPauseTask, scanTask) == scanTask)
            throw new InvalidOperationException($"Scan completed without a storage read: {(await scanTask).Count} items.");
        await scanPauseTask.WaitAsync(TimeSpan.FromSeconds(30));

        await store.Set(keyA, null);
        await store.Compact();
        await store.Set(trailingKey, PageValue(trailingKey, 0xC2));
        ClearCache(store);

        var initialSlot = backend.ScanSourceSlot;
        backend.ArmCompaction($".{1 - initialSlot}.kdat", $".{initialSlot}.kdat");
        var compactionTask = store.Compact().AsTask();
        await backend.WhenCompactionPaused.WaitAsync(TimeSpan.FromSeconds(30));
        var expectedB = PageValue(keyB, 0xB2);
        await store.Set(keyB, expectedB);
        backend.ResumeCompaction();
        await compactionTask;

        backend.ResumeScan();
        var items = await scanTask.WaitAsync(TimeSpan.FromSeconds(30));
        items.Should().NotContain(item => item.Key == "a");
        items.Should().ContainSingle(item => item.Key == "b")
            .Which.Value.Should().Equal(expectedB);
    }

    // Private methods

    private static byte[] Key(string value)
        => Encoding.UTF8.GetBytes(value);

    private static void ClearCache(KvasarStore store)
    {
        var field = typeof(KvasarStore).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((PageCache)field.GetValue(store)!).Clear();
    }

    private static byte[] PageValue(byte[] key, byte fill)
    {
        var valueLength = PageSize;
        while (RecordCodec.GetRecordLength(key.Length, valueLength, false) > PageSize)
            valueLength--;
        var value = new byte[valueLength];
        value.AsSpan().Fill(fill);
        return value;
    }

    private sealed class ConstantHasher : IKeyHasher
    {
        public bool IsKeyed => true;
        public int SecretSize => 16;

        public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret)
            => 0xAB;
    }

    private sealed class SlotRecycleGateBackend(IStorageBackend backend) : IStorageBackend
    {
        private TaskCompletionSource _whenScanPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _whenScanResumed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _whenCompactionPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _whenCompactionResumed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _scanSourceSuffix = "";
        private string _compactionSourceSuffix = "";
        private string _compactionTargetSuffix = "";
        private int _scanSourceSlot = -1;
        private int _isScanArmed;
        private int _isCompactionArmed;
        private int _hasCompactionTargetWrite;

        public Task WhenScanPaused => _whenScanPaused.Task;
        public Task WhenCompactionPaused => _whenCompactionPaused.Task;
        public int ScanSourceSlot => Volatile.Read(ref _scanSourceSlot);

        public void ArmScan()
        {
            _whenScanPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _whenScanResumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _scanSourceSuffix = ".kdat";
            _scanSourceSlot = -1;
            _isScanArmed = 1;
        }

        public void ResumeScan()
            => _whenScanResumed.TrySetResult();

        public void ArmCompaction(string sourceSuffix, string targetSuffix)
        {
            _whenCompactionPaused =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _whenCompactionResumed =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _compactionSourceSuffix = sourceSuffix;
            _compactionTargetSuffix = targetSuffix;
            _hasCompactionTargetWrite = 0;
            _isCompactionArmed = 1;
        }

        public void ResumeCompaction()
            => _whenCompactionResumed.TrySetResult();

        public async ValueTask<IStorageFile> Open(
            string path, CancellationToken cancellationToken = default)
            => new SlotRecycleGateFile(
                await backend.Open(path, cancellationToken).ConfigureAwait(false), this, path);

        public bool Exists(string path)
            => backend.Exists(path);
        public void Delete(string path)
            => backend.Delete(path);
        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);

        internal async ValueTask PauseRead(string path, CancellationToken cancellationToken)
        {
            if (path.EndsWith(_scanSourceSuffix, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _isScanArmed, 0) != 0) {
                Volatile.Write(ref _scanSourceSlot, path.EndsWith(".0.kdat", StringComparison.Ordinal) ? 0 : 1);
                _whenScanPaused.TrySetResult();
                await _whenScanResumed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            if (path.EndsWith(_compactionSourceSuffix, StringComparison.Ordinal)
                && Volatile.Read(ref _hasCompactionTargetWrite) != 0
                && Interlocked.Exchange(ref _isCompactionArmed, 0) != 0) {
                _whenCompactionPaused.TrySetResult();
                await _whenCompactionResumed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        internal void ObserveWrite(string path, long offset)
        {
            if (offset >= KvasarConstants.SegmentHeaderSize
                && path.EndsWith(_compactionTargetSuffix, StringComparison.Ordinal)
                && Volatile.Read(ref _isCompactionArmed) != 0)
                Volatile.Write(ref _hasCompactionTargetWrite, 1);
        }
    }

    private sealed class SlotRecycleGateFile(
        IStorageFile file, SlotRecycleGateBackend backend, string path) : IStorageFile
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
}
