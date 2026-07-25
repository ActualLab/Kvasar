using System.IO;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Randomized, model-based tests (§14): a long sequence of random operations is applied to both the
/// store and a <see cref="Dictionary{TKey,TValue}"/> oracle (structural byte[] comparer), and the store
/// is continuously checked against the oracle — per-op point reads, periodic full <c>Scan()</c> equality,
/// and dispose/reopen invariance — with encryption both on and off.
/// </summary>
public class PropertyTests : IDisposable
{
    private const int KeyCount = 200;
    private const int OpsPerRun = 1200;
    private const int PageSize = 512;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-prop-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public PropertyTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 13 + 5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private KvasarOptions Options(bool encrypt) => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = PageSize,
        SegmentBytes = 16 * 1024,          // small ⇒ segments roll frequently
        CompactionMinBytes = 1024,         // small ⇒ compaction actually has work
        CompactionDeadRatio = 0.3,
    };

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public async Task ModelBasedVsOracle(int seed, bool encrypt)
    {
        var rnd = new Random(seed);
        var cmp = ByteArrayComparer.Instance;
        var oracle = new Dictionary<byte[], byte[]>(cmp);
        var keys = BuildKeys(KeyCount);
        var opts = Options(encrypt);

        var store = await KvasarStore.Open(opts);
        try {
            for (var op = 0; op < OpsPerRun; op++) {
                await Step(store, oracle, keys, rnd);

                // Per-op point-read spot check over a random key sample (present + never-set keys).
                for (var s = 0; s < 8; s++)
                    await AssertGet(store, oracle, keys[rnd.Next(keys.Length)]);

                // Periodic full-content equality: Scan() vs the whole oracle.
                if (op % 150 == 149)
                    await AssertScanMatches(store, oracle, cmp);

                // Reopen invariance: dispose + reopen, then the entire oracle must survive.
                if (op is 300 or 600 or 900) {
                    await store.DisposeAsync();
                    store = await KvasarStore.Open(opts);
                    await AssertScanMatches(store, oracle, cmp);
                }
            }
            await AssertScanMatches(store, oracle, cmp);

            // Final reopen + re-verify.
            await store.DisposeAsync();
            store = await KvasarStore.Open(opts);
            await AssertScanMatches(store, oracle, cmp);
        }
        finally {
            await store.DisposeAsync();
        }
    }

    // --- One random operation, applied to both the store and the oracle -----

    private async Task Step(KvasarStore store, Dictionary<byte[], byte[]> oracle, byte[][] keys, Random rnd)
    {
        var r = rnd.Next(100);
        if (r < 55) {
            // Set a present (non-empty) value; mix small + multi-page (> PageSize).
            var ki = rnd.Next(keys.Length);
            var v = RandomValue(rnd);
            await store.Set(keys[ki], v);
            oracle[keys[ki]] = v;
        }
        else if (r < 68) {
            // Set an empty (but present) value.
            var ki = rnd.Next(keys.Length);
            await store.Set(keys[ki], ReadOnlyMemory<byte>.Empty);
            oracle[keys[ki]] = [];
        }
        else if (r < 80) {
            // Delete.
            var ki = rnd.Next(keys.Length);
            await store.Set(keys[ki], null);
            oracle.Remove(keys[ki]);
        }
        else if (r < 90) {
            // SetMany batch with intentional duplicate keys (last write wins).
            var count = rnd.Next(2, 8);
            var picks = new List<(int Ki, byte[]? Val)>(count);
            for (var i = 0; i < count; i++) {
                var ki = i > 0 && rnd.Next(3) == 0 ? picks[rnd.Next(i)].Ki : rnd.Next(keys.Length);
                var vr = rnd.Next(10);
                byte[]? val = vr < 6 ? RandomValue(rnd) : vr < 8 ? [] : null;
                picks.Add((ki, val));
            }
            var updates = new (ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?)[count];
            for (var i = 0; i < count; i++)
                updates[i] = (keys[picks[i].Ki], ToVal(picks[i].Val));
            await store.SetMany(updates);
            // Apply in the same order ⇒ last duplicate wins, matching the store.
            foreach (var (ki, val) in picks) {
                if (val is null)
                    oracle.Remove(keys[ki]);
                else
                    oracle[keys[ki]] = val;
            }
        }
        else if (r < 94) {
            await store.Flush(rnd.Next(2) == 0);
        }
        else if (r < 98) {
            await store.Compact();
        }
        else {
            await store.Clear();
            oracle.Clear();
        }
    }

    // --- Assertions ---------------------------------------------------------

    private static async Task AssertGet(KvasarStore store, Dictionary<byte[], byte[]> oracle, byte[] key)
    {
        var got = await store.Get(key);
        if (oracle.TryGetValue(key, out var expected)) {
            got.Should().NotBeNull();
            got!.Value.ToArray().Should().Equal(expected);
        }
        else {
            got.Should().BeNull();
        }
    }

    private static async Task AssertScanMatches(
        KvasarStore store, Dictionary<byte[], byte[]> oracle, IEqualityComparer<byte[]> cmp)
    {
        var actual = new Dictionary<byte[], byte[]>(cmp);
        await foreach (var (k, v) in store.Scan())
            actual[k.ToArray()] = v.ToArray();

        actual.Should().HaveCount(oracle.Count);
        foreach (var (k, expected) in oracle) {
            actual.TryGetValue(k, out var got).Should().BeTrue();
            got.Should().Equal(expected);
        }
    }

    // --- Helpers ------------------------------------------------------------

    // NOTE: the null branch needs an explicit nullable target, otherwise the conditional's natural type
    // is byte[] and a null byte[] flows through the implicit byte[]->ReadOnlyMemory<byte> operator,
    // producing a *present* empty value (HasValue==true) instead of a null (delete).
    private static ReadOnlyMemory<byte>? ToVal(byte[]? a) => a is null ? null : (ReadOnlyMemory<byte>?)a;

    private static byte[] RandomValue(Random rnd)
    {
        // 60% small; 40% multi-page (> PageSize) to exercise the multi-page run path.
        var len = rnd.Next(100) < 60 ? rnd.Next(1, 41) : rnd.Next(PageSize, 3 * PageSize + 1);
        var v = new byte[len];
        rnd.NextBytes(v);
        return v;
    }

    private static byte[][] BuildKeys(int n)
    {
        var keys = new byte[n][];
        for (var i = 0; i < n; i++) {
            var k = new byte[32];
            k[0] = (byte)i;
            k[1] = (byte)(i >> 8);
            for (var j = 2; j < k.Length; j++)
                k[j] = (byte)(i * 31 + j * 7);
            keys[i] = k;
        }
        return keys;
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
            => x.AsSpan().SequenceEqual(y.AsSpan());

        public int GetHashCode(byte[] obj)
        {
            var hc = new HashCode();
            hc.AddBytes(obj);
            return hc.ToHashCode();
        }
    }
}
