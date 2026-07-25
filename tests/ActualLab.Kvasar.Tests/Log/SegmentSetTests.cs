using System.IO;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Log;

public class SegmentSetTests : IDisposable
{
    private const int PageSize = 512;
    private readonly string _dir;
    private readonly string _basePath;

    public SegmentSetTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kvasar-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _basePath = Path.Combine(_dir, "store");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private ValueTask<SegmentSet> NewSet(long segmentBytes = 1 << 20, int maxInline = 0)
        => SegmentSet.Create(
            _basePath, PageSize, NoopPageCipherFactory.Instance, 1u, new PageCache(1 << 20), segmentBytes, maxInline);

    private static byte[] Key(int i) => Encoding_ASCII($"key-{i:D6}");
    private static byte[] Val(int i, int len)
    {
        var v = new byte[len];
        new Random(i + 12345).NextBytes(v);
        return v;
    }

    private static byte[] Encoding_ASCII(string s)
    {
        var b = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
            b[i] = (byte)s[i];
        return b;
    }

    [Fact]
    public async Task AppendReadRoundTripMixedSizes()
    {
        using var set = await NewSet();
        var locs = new List<(Locator Loc, byte[] Key, byte[] Val)>();
        for (var i = 0; i < 200; i++) {
            var len = i % 7 == 0 ? 1500 : (i % 3 == 0 ? 0 : 20 + i % 40); // some multi-page, some empty, some tiny
            var key = Key(i);
            var val = Val(i, len);
            var (loc, _) = await set.Append(RecordFlags.None, KvasarValueType.Raw, key, val, false);
            locs.Add((loc, key, val));
        }
        foreach (var (loc, key, val) in locs) {
            var view = await set.ReadRecord(loc);
            view.Key.ToArray().Should().Equal(key);
            view.Value.ToArray().Should().Equal(val);
            view.IsTombstone.Should().BeFalse();
        }
    }

    [Fact]
    public async Task TombstoneRoundTrips()
    {
        using var set = await NewSet();
        var key = Key(1);
        var (loc, _) = await set.Append(
            RecordFlags.None, KvasarValueType.Raw, key, ReadOnlyMemory<byte>.Empty, true);
        var view = await set.ReadRecord(loc);
        view.IsTombstone.Should().BeTrue();
        view.Key.ToArray().Should().Equal(key);
        view.Value.Length.Should().Be(0);
    }

    [Fact]
    public async Task SmallValueIsZeroCopy()
    {
        using var set = await NewSet();
        var (loc, _) = await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(1), Val(1, 16), false);
        var a = (await set.ReadRecord(loc)).Value;
        var b = (await set.ReadRecord(loc)).Value;
        SameBackingArray(a, b).Should().BeTrue("single-page reads must alias the same page buffer");
    }

    [Fact]
    public async Task LargeValueSpansPagesAndCopies()
    {
        using var set = await NewSet();
        var val = Val(1, 3 * PageSize + 40);
        var (loc, recLen) = await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(1), val, false);
        recLen.Should().BeGreaterThan(PageSize);

        var view = await set.ReadRecord(loc);
        view.Value.ToArray().Should().Equal(val);

        // Multi-page reads produce a fresh copy each time (not aliased).
        var a = (await set.ReadRecord(loc)).Value;
        var b = (await set.ReadRecord(loc)).Value;
        SameBackingArray(a, b).Should().BeFalse("multi-page reads are copied");
    }

    [Fact]
    public async Task RollsToNewSegmentsPastSegmentBytes()
    {
        using var set = await NewSet(segmentBytes: 4 * PageSize);
        for (var i = 0; i < 400; i++)
            await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(i), Val(i, 30), false);
        set.ActiveSegmentId.Should().BeGreaterThan(0u);
        set.SealedSegments().Should().NotBeEmpty();
    }

    [Fact]
    public async Task ScanAllRecoversEveryRecordInOrderAcrossSegments()
    {
        var expected = new List<(byte[] Key, byte[] Val)>();
        using (var set = await NewSet(segmentBytes: 4 * PageSize)) {
            for (var i = 0; i < 300; i++) {
                var len = i % 11 == 0 ? 1200 : 25;
                var key = Key(i);
                var val = Val(i, len);
                await set.Append(RecordFlags.None, KvasarValueType.Raw, key, val, false);
                expected.Add((key, val));
            }
            await set.Flush(false);
        }
        using var reopened = await NewSet(segmentBytes: 4 * PageSize);
        var scanned = await ScanAll(reopened);
        scanned.Count.Should().Be(expected.Count);
        for (var i = 0; i < expected.Count; i++) {
            scanned[i].View.Key.ToArray().Should().Equal(expected[i].Key);
            scanned[i].View.Value.ToArray().Should().Equal(expected[i].Val);
        }
    }

    [Fact]
    public async Task TruncatedTailStopsCleanly()
    {
        var keys = new List<byte[]>();
        using (var set = await NewSet()) {
            for (var i = 0; i < 120; i++) {
                var key = Key(i);
                await set.Append(RecordFlags.None, KvasarValueType.Raw, key, Val(i, 40), false);
                keys.Add(key);
            }
            await set.Flush(false);
        }

        // Chop off the last page (and then some) to simulate a torn tail.
        var path = _basePath + ".001.klog";
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            fs.SetLength(fs.Length - PageSize - 10);

        using var reopened = await NewSet();
        List<(Locator Loc, RecordView View, int RecordLength)> scanned = null!;
        var act = async () => scanned = await ScanAll(reopened);
        await act.Should().NotThrowAsync();

        scanned.Count.Should().BeGreaterThan(0);
        scanned.Count.Should().BeLessThan(keys.Count, "the truncated tail records are dropped");
        for (var i = 0; i < scanned.Count; i++)
            scanned[i].View.Key.ToArray().Should().Equal(keys[i], "recovery yields a clean prefix");
    }

    [Fact]
    public async Task OnSupersededMovesBytesLiveToDead()
    {
        using var set = await NewSet();
        var (loc0, len0) = await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(0), Val(0, 50), false);
        var (_, len1) = await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(1), Val(1, 60), false);

        var liveBefore = set.LiveBytes;
        set.DeadBytes.Should().Be(0);
        liveBefore.Should().Be(len0 + len1);

        set.OnSuperseded(loc0, len0);
        set.LiveBytes.Should().Be(len1);
        set.DeadBytes.Should().Be(len0);
    }

    [Fact]
    public async Task ActiveHwmAdvancesAndReadsFromUnsealedTail()
    {
        using var set = await NewSet();
        set.ActiveLogicalHwm.Should().Be(0);
        var (loc, len) = await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(0), Val(0, 10), false);
        set.ActiveLogicalHwm.Should().Be(len);
        // Read before any flush: served from the in-memory tail buffer.
        (await set.ReadRecord(loc)).Value.ToArray().Should().Equal(Val(0, 10));
    }

    [Fact]
    public async Task RemoveSegmentDeletesSealedSegment()
    {
        using var set = await NewSet(segmentBytes: 4 * PageSize);
        for (var i = 0; i < 400; i++)
            await set.Append(RecordFlags.None, KvasarValueType.Raw, Key(i), Val(i, 30), false);
        var sealedId = set.SealedSegments().First().SegmentId;
        var path = _basePath + $".{sealedId:D3}.klog";
        File.Exists(path).Should().BeTrue();

        set.RemoveSegment(sealedId);
        File.Exists(path).Should().BeFalse();
        (await set.TryReadRecord(new Locator(sealedId, 0))).IsFound.Should().BeFalse();
    }

    private static async Task<List<(Locator Loc, RecordView View, int RecordLength)>> ScanAll(SegmentSet set)
    {
        var result = new List<(Locator Loc, RecordView View, int RecordLength)>();
        await foreach (var item in set.ScanAll())
            result.Add(item);
        return result;
    }

    private static bool SameBackingArray(ReadOnlyMemory<byte> a, ReadOnlyMemory<byte> b)
    {
        if (!MemoryMarshal.TryGetArray(a, out var sa) || !MemoryMarshal.TryGetArray(b, out var sb))
            return false;
        return ReferenceEquals(sa.Array, sb.Array);
    }
}
