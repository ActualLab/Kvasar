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
        // Every key hashes the same here, which is the state R12's fan-out made legal. The scan's snapshot
        // is taken on the first MoveNextAsync and then the slot it points into is recycled twice, so its
        // pending locators resolve onto *other* keys' records. Verifying by re-hashing the decoded record
        // — what Scan used to do — accepts every one of them, so a scan yields some keys twice and drops
        // the entries those locators actually stood for.
        var options = Options();
        var keys = Enumerable.Range(0, KeyCount).Select(i => $"k{i:D2}").ToArray();
        await using var store = await KvasarStore.Open(options);
        foreach (var key in keys)
            await store.Set(key, Value(key));
        await store.Flush();

        var seen = new List<string>();
        var postSnapshotFills = new List<string>();
        await using var scan = store.Scan().GetAsyncEnumerator();
        // The first step captures the snapshot and consumes the record at offset 0.
        (await scan.MoveNextAsync()).Should().BeTrue();
        seen.Add(scan.Current.Key.AsString);

        // Drop that first record and rewrite the slot twice, so the live set slides down by one record and
        // slot 0 — which every pending snapshot locator names — comes back holding different keys.
        await store.Set(seen[0], null);
        await store.Compact();
        await store.Set(keys[^1], Value(keys[^1], PostSnapshotFill));
        await store.Compact();

        while (await scan.MoveNextAsync()) {
            seen.Add(scan.Current.Key.AsString);
            if (scan.Current.Value.Span[^1] == PostSnapshotFill)
                postSnapshotFills.Add(scan.Current.Key.AsString);
        }

        // The snapshot was taken before any of this ran, so no entry in it can legitimately resolve to a
        // record written afterwards. Verifying a locator by re-hashing whatever it now decodes to cannot
        // tell the difference — with fan-out legal, the hash stopped being an identity.
        postSnapshotFills.Should().BeEmpty(
            "a snapshot entry must not resolve onto a record written after the scan began");
        seen.Should().OnlyHaveUniqueItems("a scan must never yield one key twice");
        seen.Should().BeSubsetOf(keys, "a scan must never yield a key that was not stored");
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
