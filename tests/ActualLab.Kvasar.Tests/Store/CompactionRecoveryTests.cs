using System.IO;
using System.Text;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Tests.Storage;

namespace ActualLab.Kvasar.Tests.Store;

public sealed class CompactionRecoveryTests : IDisposable
{
    private const int PageSize = 512;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kvasar-compaction-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _encryptionKey = Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();

    public CompactionRecoveryTests()
        => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ACompactionCheckpointLostToPowerFailureCannotAdoptTheSourceSlotIndex()
    {
        var backend = new FakeStorageBackend();
        var original = NewValue(1, 96);
        var committed = NewValue(2, 1256);
        await using (var store = await KvasarStore.Open(Options(backend))) {
            await store.Set(Key(0), original);
            for (var i = 1; i < 15; i++)
                await store.Set(Key(i), NewValue(i + 10, 96));
            await store.Flush();
        }

        var sourceState = await ReadSuperblock(backend);
        var indexPath = IndexPath(sourceState.IndexSlot);
        await using (var indexFile = await backend.Open(indexPath))
            await indexFile.FlushToDisk();
        var stableSourceIndex = backend.GetStableBytes(indexPath);

        await using (var indexFile = await backend.Open(indexPath))
            await indexFile.Truncate(IndexLog.HeaderSize);
        await using (var store = await KvasarStore.Open(Options(backend))) {
            (await store.Get(Key(0)))!.Value.ToArray().Should().Equal(original);
            await store.Set(Key(0), committed);
            await store.Flush();
            await store.Compact();
        }

        var switchedState = await ReadSuperblock(backend);
        switchedState.DataSlot.Should().NotBe(sourceState.DataSlot);
        switchedState.IndexSlot.Should().Be(sourceState.IndexSlot);
        switchedState.IndexCommitLength.Should().Be(sourceState.IndexCommitLength);
        (switchedState.Generation % Superblock.SlotCount).Should()
            .Be(sourceState.Generation % Superblock.SlotCount);

        backend.PowerLoss(CrashMode.LoseAll);
        backend.GetBytes(IndexPath(switchedState.IndexSlot)).Should().Equal(stableSourceIndex);

        await using var recovered = await KvasarStore.Open(Options(backend));
        recovered.Stats.FallbackRecoveries.Should().Be(0);
        (await recovered.Get(Key(0)))!.Value.ToArray().Should().Equal(committed);
    }

    private string BasePath => Path.Combine(_directory, "store");

    private KvasarOptions Options(FakeStorageBackend backend)
        => new() {
            BasePath = BasePath,
            EncryptionKey = _encryptionKey,
            PageSize = PageSize,
            PageCacheBytes = 32 * 1024,
            Durability = KvasarDurability.Flushed,
            FlushDelay = TimeSpan.FromHours(1),
            CommitBytes = long.MaxValue,
            CompactionMinBytes = long.MaxValue,
            StorageBackend = backend,
        };

    private async Task<SuperblockState> ReadSuperblock(FakeStorageBackend backend)
    {
        await using var file = await backend.Open(BasePath + ".kvs");
        using var superblock = new Superblock(_encryptionKey, KvasarConstants.DataFormatVersion);
        var read = await superblock.Read(file);
        read.Status.Should().Be(SuperblockStatus.Ok);
        return read.Newest!.Value;
    }

    private string IndexPath(int slot)
        => $"{BasePath}.{slot}.kidx";

    private static KvasarKey Key(int index)
        => Encoding.UTF8.GetBytes($"key-{index:D2}");

    private static byte[] NewValue(int seed, int length)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }
}
