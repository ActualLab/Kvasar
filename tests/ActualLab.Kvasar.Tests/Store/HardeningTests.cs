using System.Buffers.Binary;
using System.Text;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Tests.Paging;

namespace ActualLab.Kvasar.Tests.Store;

// Regression tests for the bugs found in the code review. Each one failed before its fix.
// The recurring theme: corrupt input must be rejected cleanly, because anything other than
// KvasarCorruptException escapes Open's wipe-and-recreate and bricks the store (§12).
public class HardeningTests : IDisposable
{
    private readonly string _dir;
    private readonly byte[] _key;

    public HardeningTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kvasar-hardening-" + Guid.NewGuid().ToString("N"));
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

    // --- Parser hardening: hostile length fields must be rejected, never throw ---------------------

    [Fact]
    public void RecordCodecRejectsBodyLengthNearLongMaxValue()
    {
        // varint(long.MaxValue) — the old code did `recLenBytes + (long)bodyLenU`, which wrapped negative
        // and slid past the bounds check into a negative-length Slice.
        var src = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0 };
        RecordCodec.TryDecode(src.AsSpan(), out _, out _).Should().BeFalse();
        RecordCodec.TryDecode(src.AsMemory(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void RecordCodecRejectsKeyLengthNearUlongMaxValue()
    {
        // bodyLen = 12, then keyLen = varint(ulong.MaxValue) => (long)keyLen was -1, so the
        // `headerLen + kLen > bodyLen` guard passed and Slice(keyOffset, -1) threw.
        var src = new byte[] { 0x0C, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
        RecordCodec.TryDecode(src.AsSpan(), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void VarintRejectsOverlongEncoding()
    {
        // A 10-byte varint's last byte carries only bit 63; 0x7F encodes a value past 2^64-1, which used
        // to be accepted with the excess bits silently dropped.
        var overlong = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F };
        Varint.TryRead(overlong, out _, out _).Should().BeFalse();

        // The in-range 10-byte encoding of ulong.MaxValue must still decode.
        var maxValue = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 };
        Varint.TryRead(maxValue, out var value, out var read).Should().BeTrue();
        value.Should().Be(ulong.MaxValue);
        read.Should().Be(10);
    }

    [Fact]
    public async Task IndexFileRejectsCheckpointCountThatOverflows()
    {
        // checkpointCount = long.MaxValue: `count * EntrySize` and `HeaderSize + that` both wrapped, so
        // the negative result passed both guards and reached a throwing Slice.
        var path = Path.Combine(_dir, "overflow.kidx");
        await IndexFile.WriteCheckpoint(path, Array.Empty<IndexEntry>(), (1u, 0u), 1u);
        var bytes = await File.ReadAllBytesAsync(path);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), long.MaxValue);
        await File.WriteAllBytesAsync(path, bytes);

        (await IndexFile.TryLoad(path, 1u)).Should().BeNull();
    }

    // --- HashIndex: a sentinel locator must never reach a live slot --------------------------------

    [Fact]
    public void HashIndexRejectsSentinelLocator()
    {
        // Locator.None packs to 0 == the empty-slot sentinel. Writing it into a live slot terminates the
        // probe run early and orphans every later key in that chain. The old guard was a Debug.Assert,
        // which is stripped in Release.
        var index = new HashIndex();
        var act = () => index.Set(0x1234_5678_9ABC_DEF0, Locator.None, 42);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HashIndexBulkLoadSkipsSentinelEntriesAndKeepsTheRunIntact()
    {
        // A zero-filled .kidx tail decodes to entries with packed == 0; they must be dropped rather than
        // corrupt the table, since .kidx contents are never validated per-entry.
        const ulong hashA = 0x1111_1111_0000_0001;
        const ulong hashB = 0x2222_2222_0000_0002;
        var entries = new[] {
            NewEntry(hashA, 1, 100, 10),
            new IndexEntry { KeyHash = 0, PackedLocator = new Locator(0, 0).Packed, Length = 0, Flags = 0 }, // sentinel
            NewEntry(hashB, 1, 200, 20),
        };

        var index = new HashIndex();
        index.BulkLoad(entries);

        index.TryGetFirst(hashA, out var locA, out _).Should().BeTrue();
        locA.Should().Be(new Locator(1, 100));
        index.TryGetFirst(hashB, out var locB, out _).Should().BeTrue();
        locB.Should().Be(new Locator(1, 200));
        index.Count.Should().Be(2);
    }

    // --- Segment integrity ------------------------------------------------------------------------

    [Fact]
    public async Task PagedSegmentRejectsSegmentIdThatDisagreesWithItsFileName()
    {
        // The 64-byte header isn't authenticated. A flipped segmentId used to be adopted verbatim, so the
        // set kept indexing by filename while new locators carried the bogus id: every later write read
        // back as a miss, permanently and silently.
        var path = Path.Combine(_dir, "seg.klog");
        var factory = new FakePageCipherFactory(16);
        using (await PagedSegment.Create(path, 11, 4096, factory, 1u, new PageCache(1 << 20))) { }

        var act = async () => await PagedSegment.Open(path, factory, 1u, new PageCache(1 << 20), expectedSegmentId: 12);
        await act.Should().ThrowAsync<KvasarCorruptException>();

        // The matching id still opens fine.
        using var ok = await PagedSegment.Open(path, factory, 1u, new PageCache(1 << 20), expectedSegmentId: 11);
        ok.SegmentId.Should().Be(11u);
    }

    [Fact]
    public async Task TornTailStartsANewSegmentInsteadOfReusingPageIds()
    {
        // The GCM nonce is a pure function of (fileSalt, pageId). Open floors PageCount past a torn
        // trailing page, so appending into that segment would re-encrypt a *different* plaintext under an
        // already-used nonce — catastrophic keystream reuse. Recovery must roll to a fresh segment.
        var basePath = Path.Combine(_dir, "torn");
        await using (var store = await KvasarStore.Open(Options(basePath))) {
            for (var i = 0; i < 64; i++)
                await store.Set(Key($"k{i}"), Val($"v{i}"));
            await store.Flush(true);
        }

        var segment1 = $"{basePath}.001.klog";
        File.Exists(segment1).Should().BeTrue();
        // Chop a few bytes off the end so the file ends mid-page.
        using (var fs = new FileStream(segment1, FileMode.Open, FileAccess.Write))
            fs.SetLength(fs.Length - 7);

        await using (var store = await KvasarStore.Open(Options(basePath))) {
            await store.Set(Key("after"), Val("recovered"));
            (await store.Get(Key("after")))!.Value.ToArray().Should().Equal(Val("recovered").ToArray());
        }

        File.Exists($"{basePath}.002.klog").Should()
            .BeTrue("a torn segment must never be appended to again, or its page nonces get reused");
    }

    // Private methods

    private KvasarOptions Options(string basePath)
        => new() {
            BasePath = basePath,
            EncryptionKey = _key,
            PageSize = 4096,
            PageCacheBytes = 1 << 20,
            SegmentBytes = 64 * 1024 * 1024,
        };

    private static IndexEntry NewEntry(ulong keyHash, uint segmentId, uint offset, uint length)
        => new() { KeyHash = keyHash, PackedLocator = new Locator(segmentId, offset).Packed, Length = length, Flags = 0 };

    private static KvasarKey Key(string s) => Encoding.UTF8.GetBytes(s);
    private static KvasarValue Val(string s) => Encoding.UTF8.GetBytes(s);
}
