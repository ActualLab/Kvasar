using System.Text;
using ActualLab.Kvasar.Internal;

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

    // --- Data-file integrity ----------------------------------------------------------------------

    [Fact]
    public async Task TornTailBurnsItsPageIdsInsteadOfReusingThem()
    {
        // The GCM nonce is a pure function of (fileSalt, pageId). Open rounds PageCount *up* past a torn
        // trailing page, so the next append starts above it: re-issuing that id would re-encrypt a
        // different plaintext under an already-used nonce (§5.2 step 6). The file therefore only ever
        // grows, and the torn page's bytes stay where they are.
        var basePath = Path.Combine(_dir, "torn");
        await using (var store = await KvasarStore.Open(Options(basePath))) {
            for (var i = 0; i < 64; i++)
                await store.Set(Key($"k{i}"), Val($"v{i}"));
            await store.Flush();
        }

        var dataPath = $"{basePath}.0.kdat";
        File.Exists(dataPath).Should().BeTrue();
        // Chop a few bytes off the end so the file ends mid-page.
        long tornLength;
        using (var fs = new FileStream(dataPath, FileMode.Open, FileAccess.Write)) {
            tornLength = fs.Length - 7;
            fs.SetLength(tornLength);
        }

        await using (var store = await KvasarStore.Open(Options(basePath))) {
            await store.Set(Key("after"), Val("recovered"));
            (await store.Get(Key("after")))!.Value.ToArray().Should().Equal(Val("recovered").ToArray());
            await store.Flush();
        }

        new FileInfo(dataPath).Length.Should().BeGreaterThan(tornLength,
            "recovery resumes above the torn page rather than overwriting its page ids");

        // ... and the store reopens cleanly, with the post-recovery write intact.
        await using (var reopened = await KvasarStore.Open(Options(basePath)))
            (await reopened.Get(Key("after")))!.Value.ToArray().Should().Equal(Val("recovered").ToArray());
    }

    // Private methods

    private KvasarOptions Options(string basePath)
        => new() {
            BasePath = basePath,
            EncryptionKey = _key,
            PageSize = 4096,
            PageCacheBytes = 1 << 20,
        };

    private static IndexEntry NewEntry(ulong keyHash, uint segmentId, uint offset, uint length)
        => new() { KeyHash = keyHash, PackedLocator = new Locator(segmentId, offset).Packed, Length = length, Flags = 0 };

    private static KvasarKey Key(string s) => Encoding.UTF8.GetBytes(s);
    private static KvasarValue Val(string s) => Encoding.UTF8.GetBytes(s);
}
