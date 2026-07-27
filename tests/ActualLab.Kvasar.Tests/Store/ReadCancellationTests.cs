using System.IO;
using System.Text;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// I28 / TODO T3: the read filters must let <see cref="OperationCanceledException"/> through while still
/// swallowing everything else. The cancellation is injected at the record read itself, through a
/// wrapping <see cref="IStorageBackend"/> — the readahead is neutralized separately, because it rethrows
/// cancellation on its own and a test that let it do so would pass against the unfixed filters.
/// </summary>
public sealed class ReadCancellationTests : IDisposable
{
    private const int PageSize = 512;
    private const int OnDiskPageSize = PageSize + KvasarConstants.GcmTagSize;
    private const int KeyCount = 50;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-readcancel-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];
    private readonly PageReadFaultBackend _backend = new(FileStorageBackend.Instance, OnDiskPageSize);

    public ReadCancellationTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 17 + 9);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task GetPropagatesCancellationFromTheRecordRead()
    {
        // Get takes no readahead at all, so the single-page read TryReadValue makes is the only device
        // operation in play: whether the cancellation surfaces is entirely down to that filter. With the
        // pre-fix filter (everything but KvasarCorruptException) it is swallowed and Get answers "miss",
        // which the caller cannot tell from a genuine cache miss.
        await using var store = await OpenSeeded();
        _backend.Arm(DataPath(await ActiveDataSlot()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.Get(K(0)));
        // The message pins it to the injected read, so a cancellation raised anywhere else cannot stand
        // in for the one the filter is supposed to let through.
        error.Message.Should().Be(PageReadFaultBackend.InjectedMessage);
        _backend.SinglePageReadCount.Should().BeGreaterThan(0);

        _backend.Disarm();
        (await store.Get(K(0)))!.Value.ToArray().Should().Equal(V(0),
            "the injected read is the only reason the call above failed");
    }

    [Fact]
    public async Task ScanPropagatesCancellationFromTheRecordRead()
    {
        // The half that actually mattered: a swallowed cancellation makes Scan *complete normally* with
        // silently partial results, so the caller reads a short enumeration as the whole store. The
        // readahead is failed with a plain IOException — which PagedFile.Prefetch is documented to
        // swallow — so nothing is cached and no cancellation can reach the caller through it.
        await using var store = await OpenSeeded();
        _backend.Arm(DataPath(await ActiveDataSlot()));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => {
            await foreach (var _ in store.Scan()) {
                // The throw lands on the first record read; yielding anything would already be a bug.
            }
        });
        error.Message.Should().Be(PageReadFaultBackend.InjectedMessage);
        _backend.MultiPageReadCount.Should().BeGreaterThan(0, "the readahead ran and was neutralized");
        _backend.SinglePageReadCount.Should().BeGreaterThan(0, "the record read is what threw");

        _backend.Disarm();
        var scanned = 0;
        await foreach (var _ in store.Scan())
            scanned++;
        scanned.Should().Be(KeyCount);
    }

    // Private methods

    private string BasePath => Path.Combine(_dir, "store");
    private string DataPath(int slot) => $"{BasePath}.{slot}.kdat";

    private KvasarOptions Options() => new() {
        BasePath = BasePath,
        EncryptionKey = _key,
        PageSize = PageSize,
        StorageBackend = _backend,
        FlushDelay = TimeSpan.Zero,
    };

    private async Task<KvasarStore> OpenSeeded()
    {
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 0; i < KeyCount; i++)
                await store.Set(K(i), V(i));
            await store.Flush();
        }
        // Reopened, so the page cache is cold and every read below really reaches the device.
        return await KvasarStore.Open(Options());
    }

    private async Task<int> ActiveDataSlot()
    {
        await using var file = await FileStorageBackend.Instance.Open(BasePath + ".kvs");
        var read = await new Superblock(_key, KvasarConstants.DataFormatVersion).Read(file);
        return read.Newest!.Value.DataSlot;
    }

    private static KvasarKey K(int i) => Encoding.UTF8.GetBytes($"k{i:D6}");

    private static byte[] V(int i)
    {
        var value = new byte[120];
        for (var j = 0; j < value.Length; j++)
            value[j] = (byte)(i * 29 + j * 3 + 7);
        return value;
    }

    // Nested types

    // Fails reads on one armed file: a whole-page read — the shape only TryReadRecord makes — as a
    // cancellation, anything larger (the readahead's run) as an ordinary I/O error, which Prefetch
    // swallows. That split is what isolates the filter from the readahead's own rethrow.
    private sealed class PageReadFaultBackend(IStorageBackend backend, int onDiskPageSize) : IStorageBackend
    {
        public const string InjectedMessage = "Injected at the record read.";

        private string? _armedPath;
        private int _singlePageReadCount;
        private int _multiPageReadCount;

        public int SinglePageReadCount => Volatile.Read(ref _singlePageReadCount);
        public int MultiPageReadCount => Volatile.Read(ref _multiPageReadCount);

        public void Arm(string path)
        {
            _singlePageReadCount = 0;
            _multiPageReadCount = 0;
            Volatile.Write(ref _armedPath, path);
        }

        public void Disarm()
            => Volatile.Write(ref _armedPath, null);

        public async ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
            => new PageReadFaultFile(await backend.Open(path, cancellationToken), this, path);

        public bool Exists(string path)
            => backend.Exists(path);

        public void Delete(string path)
            => backend.Delete(path);

        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);

        internal void OnRead(string path, int byteCount)
        {
            if (!string.Equals(Volatile.Read(ref _armedPath), path, StringComparison.Ordinal))
                return;
            if (byteCount > onDiskPageSize) {
                Interlocked.Increment(ref _multiPageReadCount);
                throw new IOException("The readahead is disabled for this test.");
            }
            if (byteCount < onDiskPageSize)
                return; // headers and other sub-page reads are left alone

            Interlocked.Increment(ref _singlePageReadCount);
            throw new OperationCanceledException(InjectedMessage);
        }
    }

    private sealed class PageReadFaultFile(IStorageFile file, PageReadFaultBackend backend, string path)
        : IStorageFile
    {
        public long Length => file.Length;

        public ValueTask DisposeAsync()
            => file.DisposeAsync();

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            backend.OnRead(path, buffer.Length);
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
