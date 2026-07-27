using System.Text;
using ActualLab.Kvasar.Crypto;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// X2 (docs/REVIEW-R3.md): a scan's snapshot locator stops identifying a record once compaction recycles
/// the slot it points into, and since R12 a 64-bit hash no longer identifies a key either.
/// </summary>
public sealed class ScanIdentityTests : IDisposable
{
    private const int PageSize = 512;
    private const int KeyCount = 8;
    private const byte PostSnapshotFill = 0xC2;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-scan-id-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public ScanIdentityTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 7 + 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task ARecycledSnapshotLocatorIsNotIdentifiedByItsHash()
    {
        var options = Options();
        var keys = Enumerable.Range(0, KeyCount).Select(i => $"k{i:D2}").ToArray();
        await using var store = await KvasarStore.Open(options);
        foreach (var key in keys)
            await store.Set(key, Value(key));
        await store.Flush();

        var seen = new Dictionary<string, byte[]>();
        await using var scan = store.Scan().GetAsyncEnumerator();
        (await scan.MoveNextAsync()).Should().BeTrue();
        seen.Add(scan.Current.Key.AsString, scan.Current.Value.ToArray());

        await store.Set(scan.Current.Key, null);
        await store.Compact();
        await store.Set(keys[^1], Value(keys[^1], PostSnapshotFill));
        await store.Compact();

        while (await scan.MoveNextAsync())
            seen.Add(scan.Current.Key.AsString, scan.Current.Value.ToArray());

        seen.Keys.Should().BeEquivalentTo(keys);
        foreach (var key in keys)
            seen[key].Should().Equal(Value(key, key == keys[^1] ? PostSnapshotFill : (byte)0xA1));
    }

    [Fact]
    public async Task ACompactionDuringScanDoesNotTruncateItsSnapshot()
    {
        var options = Options() with { Hasher = KeyHashers.SipHash24 };
        var expected = Enumerable.Range(0, 39).ToDictionary(
            i => $"k{i:D2}",
            i => Value($"k{i:D2}"));
        await using var store = await KvasarStore.Open(options);
        foreach (var (key, value) in expected)
            await store.Set(key, value);
        await store.Set(expected.First().Key, expected.First().Value);

        var seen = new Dictionary<string, byte[]>();
        await using var scan = store.Scan().GetAsyncEnumerator();
        (await scan.MoveNextAsync()).Should().BeTrue();
        seen.Add(scan.Current.Key.AsString, scan.Current.Value.ToArray());

        await store.Compact();

        while (await scan.MoveNextAsync())
            seen.Add(scan.Current.Key.AsString, scan.Current.Value.ToArray());

        seen.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task AnOverwriteDuringScanDoesNotTruncateItsSnapshot()
    {
        var options = Options() with { Hasher = KeyHashers.SipHash24 };
        var expected = Enumerable.Range(0, 40).ToDictionary(
            i => $"k{i:D2}",
            i => Value($"k{i:D2}"));
        await using var store = await KvasarStore.Open(options);
        foreach (var (key, value) in expected)
            await store.Set(key, value);

        var seen = new Dictionary<string, byte[]>();
        await using var scan = store.Scan().GetAsyncEnumerator();
        (await scan.MoveNextAsync()).Should().BeTrue();
        seen.Add(scan.Current.Key.AsString, scan.Current.Value.ToArray());

        foreach (var (key, value) in expected)
            await store.Set(key, value);

        while (await scan.MoveNextAsync())
            seen.Add(scan.Current.Key.AsString, scan.Current.Value.ToArray());

        seen.Should().BeEquivalentTo(expected);
    }

    // Private methods

    private KvasarOptions Options() => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        DisableEncryption = true,
        Hasher = new ConstantHasher(),
        PageSize = PageSize,
        PageCacheBytes = PageSize,
        FlushDelay = TimeSpan.Zero,
        CompactionMinBytes = long.MaxValue, // only the explicit Compact() calls above run a pass
    };

    private static byte[] Value(string key, byte fill = 0xA1)
    {
        // Uniform record sizes, so dropping one record shifts every later one onto its predecessor's
        // offset — that is what makes a stale locator land on a *valid* record rather than on padding.
        var value = new byte[(3 * PageSize) - 64];
        value.AsSpan().Fill(fill);
        Encoding.UTF8.GetBytes(key).CopyTo(value.AsSpan());
        return value;
    }

    // Nested types

    private sealed class ConstantHasher : IKeyHasher
    {
        public bool IsKeyed => true;
        public int SecretSize => 16;

        public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret)
            => 0xAB;
    }
}
