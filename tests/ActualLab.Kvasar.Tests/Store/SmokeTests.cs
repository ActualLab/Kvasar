using System.IO;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

public class SmokeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-smoke-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public SmokeTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 7 + 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private KvasarOptions Options(bool encrypt = true) => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = 4096,
        SegmentBytes = 64 * 1024,
    };

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

    private static async Task<Dictionary<string, string>> ScanToMap(KvasarStore store)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        await foreach (var (key, value) in store.Scan())
            map.Add(Encoding.UTF8.GetString(key.Span), Encoding.UTF8.GetString(value.Span));
        return map;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BasicSetGetReopen(bool encrypt)
    {
        await using (var store = await KvasarStore.Open(Options(encrypt))) {
            await store.Set(K("a"), K("alpha"));
            await store.Set(K("b"), K("bravo"));
            await store.Set(K("empty"), ReadOnlyMemory<byte>.Empty);
            (await store.Get(K("a")))!.Value.ToArray().Should().Equal(K("alpha"));
            (await store.Get(K("missing"))).Should().BeNull();
            (await store.Get(K("empty")))!.Value.Length.Should().Be(0);
        }
        // Reopen: data survives.
        await using (var store = await KvasarStore.Open(Options(encrypt))) {
            (await store.Get(K("a")))!.Value.ToArray().Should().Equal(K("alpha"));
            (await store.Get(K("b")))!.Value.ToArray().Should().Equal(K("bravo"));
            (await store.Get(K("empty")))!.Value.Length.Should().Be(0);
        }
    }

    [Fact]
    public async Task OverwriteDeleteScanClear()
    {
        await using var store = await KvasarStore.Open(Options());
        await store.Set(K("k"), K("v1"));
        await store.Set(K("k"), K("v2"));
        (await store.Get(K("k")))!.Value.ToArray().Should().Equal(K("v2"));

        await store.Set(K("k"), null); // delete
        (await store.Get(K("k"))).Should().BeNull();

        await store.Set(K("x"), K("1"));
        await store.Set(K("y"), K("2"));
        var all = await ScanToMap(store);
        all.Should().HaveCount(2);
        all["x"].Should().Be("1");
        all["y"].Should().Be("2");

        await store.Clear();
        (await ScanToMap(store)).Should().BeEmpty();
        (await store.Get(K("x"))).Should().BeNull();
    }

    [Fact]
    public async Task SetManyPositionalGetManyAndDupWins()
    {
        await using var store = await KvasarStore.Open(Options());
        (ReadOnlyMemory<byte>, ReadOnlyMemory<byte>?)[] updates = [
            (K("k1"), K("a")),
            (K("k2"), K("b")),
            (K("k1"), K("c")), // duplicate: last wins
        ];
        await store.SetMany(updates);

        ReadOnlyMemory<byte>[] keys = [K("k1"), K("nope"), K("k2")];
        var results = await store.GetMany(keys);
        results.Length.Should().Be(3);
        results[0]!.Value.ToArray().Should().Equal(K("c"));
        results[1].Should().BeNull();
        results[2]!.Value.ToArray().Should().Equal(K("b"));
    }

    [Fact]
    public async Task LargeMultiPageValueRoundTrips()
    {
        await using var store = await KvasarStore.Open(Options());
        var big = new byte[20_000];
        new Random(42).NextBytes(big);
        await store.Set(K("big"), big);
        (await store.Get(K("big")))!.Value.ToArray().Should().Equal(big);
        await store.Flush(true);
        (await store.Get(K("big")))!.Value.ToArray().Should().Equal(big);
    }

    [Fact]
    public async Task WrongKeyRegenerates()
    {
        await using (var store = await KvasarStore.Open(Options())) {
            await store.Set(K("a"), K("secret"));
            await store.Flush(true);
        }
        // Open with a different key ⇒ corrupt ⇒ wipe & recreate (empty store), never throws.
        var wrong = (byte[])_key.Clone();
        wrong[0] ^= 0xFF;
        var opts = Options() with { EncryptionKey = wrong };
        await using var store2 = await KvasarStore.Open(opts);
        (await store2.Get(K("a"))).Should().BeNull();
        await store2.Set(K("a"), K("new")); // usable
        (await store2.Get(K("a")))!.Value.ToArray().Should().Equal(K("new"));
    }
}
