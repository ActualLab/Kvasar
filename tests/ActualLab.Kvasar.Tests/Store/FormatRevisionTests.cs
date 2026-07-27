using System.Buffers.Binary;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Store;

public sealed class FormatRevisionTests : IDisposable
{
    private const int PageSize = 512;
    private const int PagePayloadSize = PageSize - KvasarConstants.DataPageHeaderSize;
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kvasar-format-revision-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();

    public FormatRevisionTests()
        => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try {
            Directory.Delete(_directory, true);
        }
        catch {
            // Best effort on Windows, where a failed assertion can leave a handle closing asynchronously.
        }
    }

    [Fact]
    public async Task DeletedKeyIdentitiesAreNeverReusedAndRelocationPreservesThem()
    {
        var options = Options() with {
            Hasher = new ConstantHasher(),
            IndexEncryption = IndexEncryption.Off,
            CompactionMinBytes = long.MaxValue,
        };
        ulong firstKeyId;
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set("first", "value");
            await store.Flush();
            firstKeyId = (await ReadLiveIndex()).Single().KeyId;

            await store.Set("first", null);
            await store.Compact();
            await store.Set("second", "value-1");
            await store.Flush();
        }

        var second = (await ReadLiveIndex()).Single();
        second.KeyId.Should().BeGreaterThan(firstKeyId);
        var secondKeyId = second.KeyId;
        var state = await ReadSuperblock();
        state.NextKeyId.Should().BeGreaterThan(secondKeyId);

        await using (var store = await KvasarStore.Open(options)) {
            await store.Set("second", "value-2");
            await store.Compact();
            await store.Flush();
        }

        var relocated = (await ReadLiveIndex()).Single();
        relocated.KeyId.Should().Be(secondKeyId);
        relocated.Locator.Should().NotBe(second.Locator);
    }

    [Fact]
    public async Task IndexLessRebuildRejectsAFullyTiledRecordInContinuationBytes()
    {
        const int damagedRecordLength = 4 * PagePayloadSize;
        const string damagedKey = "damaged";
        const string phantomKey = "never-written";
        var damagedValueLength = damagedRecordLength;
        while (RecordCodec.GetRecordLength(damagedKey.Length, damagedValueLength, false) > damagedRecordLength)
            damagedValueLength--;
        RecordCodec.GetRecordLength(damagedKey.Length, damagedValueLength, false)
            .Should().Be(damagedRecordLength);

        var damagedValue = new byte[damagedValueLength];
        var phantomValue = "phantom-value"u8.ToArray();
        var phantomRecord = new byte[RecordCodec.GetRecordLength(phantomKey.Length, phantomValue.Length, false)];
        RecordCodec.Encode(
            phantomRecord,
            RecordFlags.None,
            KvasarValueKind.Raw,
            "never-written"u8,
            phantomValue,
            false);
        var damagedHeaderLength = damagedRecordLength - damagedValueLength;
        var phantomOffset = (3 * PagePayloadSize) - damagedHeaderLength;
        phantomRecord.CopyTo(damagedValue, phantomOffset);
        damagedValue.AsSpan(phantomOffset + phantomRecord.Length)
            .IndexOfAnyExcept((byte)0)
            .Should().Be(-1);

        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set("before", "value-before");
            await store.Set(damagedKey, damagedValue);
            await store.Set("after", "value-after");
            await store.Flush();
        }
        var damagedEntry = (await ReadLiveIndex()).Single(x => x.Length == damagedRecordLength);
        foreach (var path in IndexPaths)
            File.Delete(path);
        CorruptPage(0, damagedEntry.Locator.Offset / PagePayloadSize);

        await using var reopened = await KvasarStore.Open(options);
        (await reopened.Get("before"))!.Value.AsString.Should().Be("value-before");
        (await reopened.Get("after"))!.Value.AsString.Should().Be("value-after");
        (await reopened.Get(damagedKey)).Should().BeNull();
        (await reopened.Get(phantomKey)).Should().BeNull();
    }

    [Fact]
    public async Task APredecessorlessCandidateAuthenticatesFromItsPersistedFloor()
    {
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set("first", "value-1");
            await store.Set("second", "value-2");
            await store.Flush();
        }

        SuperblockState newest;
        SuperblockState older;
        await using (var file = await FileStorageBackend.Instance.Open(SuperblockPath)) {
            var read = await new Superblock(_key, KvasarConstants.DataFormatVersion).Read(file);
            read.States.Should().HaveCount(2);
            newest = read.States[0];
            older = read.States[1];
        }
        newest.DataAuthenticationFloor.Should().Be(older.DataCommitLength);
        CorruptPage(newest.DataSlot, 1);
        CorruptSuperblockSlot((int)(older.Generation % Superblock.SlotCount));

        await using var reopened = await KvasarStore.Open(options);
        (await reopened.Get("first")).Should().BeNull();
        (await reopened.Get("second")).Should().BeNull();
        reopened.Stats.Entries.Should().Be(0);
    }

    [Fact]
    public async Task PreRevisionStoreIsImportedIntoTheFramedFormat()
    {
        await CreateLegacyStore();

        await using var store = await KvasarStore.Open(Options());
        (await store.Get("legacy-a"))!.Value.AsString.Should().Be("value-a");
        (await store.Get("legacy-b"))!.Value.AsString.Should().Be("value-b");

        var state = await ReadSuperblock();
        state.NextKeyId.Should().Be(3);
        (await ReadFormatVersion()).Should().Be(KvasarConstants.DataFormatVersion);
    }

    [Fact]
    public async Task IndexLessPreRevisionStoreIsImportedIntoTheFramedFormat()
    {
        await CreateLegacyStore();
        foreach (var path in IndexPaths)
            File.Delete(path);

        await using var store = await KvasarStore.Open(Options());
        (await store.Get("legacy-a"))!.Value.AsString.Should().Be("value-a");
        (await store.Get("legacy-b"))!.Value.AsString.Should().Be("value-b");
        (await ReadFormatVersion()).Should().Be(KvasarConstants.DataFormatVersion);
    }

    [Fact]
    public async Task UnreadablePreRevisionStoreDegradesToAnEmptyFramedStore()
    {
        await CreateLegacyStore();
        foreach (var path in IndexPaths)
            File.Delete(path);
        CorruptPage(0, 0);

        await using var store = await KvasarStore.Open(Options());
        (await store.Get("legacy-a")).Should().BeNull();
        (await store.Get("legacy-b")).Should().BeNull();
        store.Stats.Entries.Should().Be(0);

        (await ReadFormatVersion()).Should().Be(KvasarConstants.DataFormatVersion);
    }

    // Private methods

    private string BasePath => Path.Combine(_directory, "store");
    private string SuperblockPath => BasePath + ".kvs";
    private string[] DataPaths => [$"{BasePath}.0.kdat", $"{BasePath}.1.kdat"];
    private string[] IndexPaths => [$"{BasePath}.0.kidx", $"{BasePath}.1.kidx"];

    private KvasarOptions Options() => new() {
        BasePath = BasePath,
        EncryptionKey = _key,
        PageSize = PageSize,
        FlushDelay = TimeSpan.Zero,
    };

    private async Task CreateLegacyStore()
    {
        var pageKey = new byte[KvasarConstants.PageKeySize];
        var indexKey = new byte[KvasarConstants.IndexMacKeySize];
        KeyDerivations.HkdfSha256.Derive(_key, [], KvasarConstants.PageKeyInfo, pageKey);
        KeyDerivations.HkdfSha256.Derive(_key, [], KvasarConstants.IndexMacKeyInfo, indexKey);
        using var cipherFactory =
            new AesGcmPageCipherFactory(pageKey, KvasarConstants.PreviousDataFormatVersion);

        long dataCommitLength;
        IndexEntry[] entries;
        await using (var active = await PagedFile.Create(
            await FileStorageBackend.Instance.Open(DataPaths[0]),
            1,
            PageSize,
            cipherFactory,
            KvasarConstants.PreviousDataFormatVersion,
            new PageCache(PageSize))) {
            var page = new byte[PageSize];
            var records = new[] {
                (Key: "legacy-a"u8.ToArray(), Value: "value-a"u8.ToArray()),
                (Key: "legacy-b"u8.ToArray(), Value: "value-b"u8.ToArray()),
            };
            entries = new IndexEntry[records.Length];
            var offset = 0;
            for (var i = 0; i < records.Length; i++) {
                var record = records[i];
                var length = RecordCodec.Encode(
                    page.AsSpan(offset),
                    RecordFlags.None,
                    KvasarValueKind.Raw,
                    record.Key,
                    record.Value,
                    false);
                var locator = new Locator(1, offset);
                entries[i] = new IndexEntry {
                    KeyHash = (ulong)i + 1,
                    PackedLocator = locator.Packed,
                    KeyId = locator.Packed,
                    Length = (uint)length,
                };
                offset += length;
            }
            await active.AppendPage(page);
            await active.Flush();
            dataCommitLength = active.MarkCommitted();
        }
        await using (var free = await PagedFile.Create(
            await FileStorageBackend.Instance.Open(DataPaths[1]),
            2,
            PageSize,
            cipherFactory,
            KvasarConstants.PreviousDataFormatVersion,
            new PageCache(PageSize)))
            await free.Flush();

        long indexCommitLength;
        await using (var index = await IndexLog.Open(
            await FileStorageBackend.Instance.Open(IndexPaths[0]),
            KvasarConstants.PreviousDataFormatVersion,
            indexKey)) {
            indexCommitLength = await index.WriteCheckpoint(entries, PageSize);
            await index.WriteCommitMac(1);
        }
        await using (var index = await IndexLog.Open(
            await FileStorageBackend.Instance.Open(IndexPaths[1]),
            KvasarConstants.PreviousDataFormatVersion,
            indexKey))
            await index.WriteCheckpoint(Array.Empty<IndexEntry>(), 0);

        await using var superblockFile = await FileStorageBackend.Instance.Open(SuperblockPath);
        using var superblock = new Superblock(
            _key,
            KvasarConstants.PreviousDataFormatVersion);
        await superblock.Initialize(superblockFile);
        await superblock.Write(superblockFile, new SuperblockState(
            1,
            0,
            dataCommitLength,
            0,
            indexCommitLength,
            entries.Sum(x => (long)x.Length),
            PageSize - entries.Sum(x => (long)x.Length)));
    }

    private async Task<IndexEntry[]> ReadLiveIndex()
    {
        var state = await ReadSuperblock();
        var indexKey = new byte[KvasarConstants.IndexMacKeySize];
        KeyDerivations.HkdfSha256.Derive(_key, [], KvasarConstants.IndexMacKeyInfo, indexKey);
        await using var index = await IndexLog.Open(
            await FileStorageBackend.Instance.Open(IndexPaths[state.IndexSlot]),
            KvasarConstants.DataFormatVersion,
            indexKey);
        var snapshot = await index.Read(state.IndexCommitLength, state.Generation);
        return snapshot!.Value.Entries.Where(x => !x.IsTombstone).ToArray();
    }

    private async Task<SuperblockState> ReadSuperblock()
    {
        await using var file = await FileStorageBackend.Instance.Open(SuperblockPath);
        var read = await new Superblock(_key, KvasarConstants.DataFormatVersion).Read(file);
        read.Status.Should().Be(SuperblockStatus.Ok);
        return read.Newest!.Value;
    }

    private async Task<uint> ReadFormatVersion()
    {
        await using var file = await FileStorageBackend.Instance.Open(SuperblockPath);
        var header = new byte[8];
        await file.ReadExact(0, header);
        return BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
    }

    private void CorruptPage(int slot, long pageId)
    {
        var offset = KvasarConstants.SegmentHeaderSize
            + pageId * (PageSize + KvasarConstants.GcmTagSize);
        using var stream = new FileStream(DataPaths[slot], FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset + 1;
        var value = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(value ^ 0x80));
    }

    private void CorruptSuperblockSlot(int slot)
    {
        var offset = Superblock.HeaderSize + (slot + 1L) * Superblock.SlotSize - 1;
        using var stream = new FileStream(SuperblockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = offset;
        var value = stream.ReadByte();
        stream.Position--;
        stream.WriteByte((byte)(value ^ 0x80));
    }

    private sealed class ConstantHasher : IKeyHasher
    {
        public bool IsKeyed => false;
        public int SecretSize => 0;

        public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret)
            => 1;
    }
}
