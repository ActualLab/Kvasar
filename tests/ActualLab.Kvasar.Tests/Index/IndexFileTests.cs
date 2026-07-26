using System.IO;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Index;

public sealed class IndexFileTests : IDisposable
{
    private const uint FormatVer = 1;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"kidx-test-{Guid.NewGuid():N}.kidx");

    public void Dispose()
    {
        TryDelete(_path);
        TryDelete(_path + ".tmp");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private static IndexEntry Entry(ulong keyHash, uint seg, uint off, uint len, bool tombstone = false)
        => new() {
            KeyHash = keyHash, PackedLocator = new Locator(seg, off).Packed, Length = len,
            Flags = tombstone ? (byte)RecordFlags.Tombstone : (byte)RecordFlags.None,
        };

    [Fact]
    public async Task CheckpointRoundTrip()
    {
        var entries = new[] {
            Entry(0x1111_0000_0000_0001, 1, 100, 10),
            Entry(0x2222_0000_0000_0002, 1, 200, 20),
            Entry(0x3333_0000_0000_0003, 2, 300, 30),
        };
        var hwm = (SegmentId: 2u, Hwm: 4096u);

        await IndexFile.WriteCheckpoint(_path, entries, hwm, FormatVer);
        var loaded = await IndexFile.TryLoad(_path, FormatVer);

        loaded.Should().NotBeNull();
        (loaded!.Value.SegmentId, loaded.Value.Hwm).Should().Be(hwm);
        loaded.Value.Entries.Should().BeEquivalentTo(entries, o => o.WithoutStrictOrdering());
    }

    [Fact]
    public async Task CheckpointDropsTombstones()
    {
        var entries = new[] {
            Entry(1, 1, 100, 10),
            Entry(2, 1, 200, 20, tombstone: true),
            Entry(3, 1, 300, 30),
        };

        await IndexFile.WriteCheckpoint(_path, entries, (1u, 0u), FormatVer);
        var loaded = await IndexFile.TryLoad(_path, FormatVer);
        loaded.Should().NotBeNull();

        loaded!.Value.Entries.Should().HaveCount(2);
        loaded.Value.Entries.Should().NotContain(e => e.KeyHash == 2);
    }

    [Fact]
    public async Task DeltaLastWriterWins()
    {
        await IndexFile.WriteCheckpoint(_path, new[] { Entry(10, 1, 100, 10) }, (1u, 100u), FormatVer);

        // Later delta for the same key overrides the checkpoint entry.
        await IndexFile.AppendDelta(_path, Entry(10, 1, 500, 50));
        // A brand-new key via delta.
        await IndexFile.AppendDelta(_path, Entry(20, 1, 600, 60));
        // Two deltas for the same new key — the later one wins.
        await IndexFile.AppendDelta(_path, Entry(30, 1, 700, 70));
        await IndexFile.AppendDelta(_path, Entry(30, 2, 800, 80));

        var loaded = await IndexFile.TryLoad(_path, FormatVer);
        loaded.Should().NotBeNull();

        var map = loaded!.Value.Entries.ToDictionary(e => e.KeyHash);
        map.Should().HaveCount(3);
        map[10].Locator.Offset.Should().Be(500);
        map[10].Length.Should().Be(50);
        map[20].Locator.Offset.Should().Be(600);
        map[30].Locator.FileId.Should().Be(2u);
        map[30].Locator.Offset.Should().Be(800);
    }

    [Fact]
    public async Task DeltaTombstoneKeptAsTombstone()
    {
        await IndexFile.WriteCheckpoint(_path, new[] { Entry(10, 1, 100, 10) }, (1u, 0u), FormatVer);
        await IndexFile.AppendDelta(_path, Entry(10, 1, 100, 10, tombstone: true));

        var loaded = await IndexFile.TryLoad(_path, FormatVer);
        loaded.Should().NotBeNull();

        loaded!.Value.Entries.Should().HaveCount(1);
        loaded.Value.Entries[0].KeyHash.Should().Be(10);
        loaded.Value.Entries[0].IsTombstone.Should().BeTrue();
    }

    [Fact]
    public async Task TornDeltaTailIsDropped()
    {
        await IndexFile.WriteCheckpoint(_path, new[] { Entry(1, 1, 100, 10) }, (1u, 0u), FormatVer);
        await IndexFile.AppendDelta(_path, Entry(2, 1, 200, 20));

        // Append a partial (short) trailing entry, simulating a torn lazy write.
        using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write))
            stream.Write(new byte[IndexFile.EntrySize - 1]);

        var loaded = await IndexFile.TryLoad(_path, FormatVer);
        loaded.Should().NotBeNull();

        loaded!.Value.Entries.Should().HaveCount(2);
        loaded.Value.Entries.Select(e => e.KeyHash).Should().BeEquivalentTo([1UL, 2UL]);
    }

    [Fact]
    public async Task MissingFileReturnsNull()
    {
        var loaded = await IndexFile.TryLoad(_path, FormatVer);
        loaded.Should().BeNull();
    }

    [Fact]
    public async Task BadMagicReturnsNull()
    {
        await IndexFile.WriteCheckpoint(_path, new[] { Entry(1, 1, 100, 10) }, (1u, 0u), FormatVer);
        var bytes = File.ReadAllBytes(_path);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(_path, bytes);

        (await IndexFile.TryLoad(_path, FormatVer)).Should().BeNull();
    }

    [Fact]
    public async Task FormatVerMismatchReturnsNull()
    {
        await IndexFile.WriteCheckpoint(_path, new[] { Entry(1, 1, 100, 10) }, (1u, 0u), FormatVer);

        (await IndexFile.TryLoad(_path, FormatVer + 1)).Should().BeNull();
    }

    [Fact]
    public async Task TruncatedHeaderReturnsNull()
    {
        File.WriteAllBytes(_path, new byte[8]); // shorter than HeaderSize

        (await IndexFile.TryLoad(_path, FormatVer)).Should().BeNull();
    }

    [Fact]
    public async Task EmptyCheckpointRoundTrips()
    {
        await IndexFile.WriteCheckpoint(_path, Array.Empty<IndexEntry>(), (7u, 12345u), FormatVer);

        var loaded = await IndexFile.TryLoad(_path, FormatVer);
        loaded.Should().NotBeNull();
        loaded!.Value.Entries.Should().BeEmpty();
        (loaded.Value.SegmentId, loaded.Value.Hwm).Should().Be((7u, 12345u));
    }
}
