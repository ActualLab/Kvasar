using System.IO;
using System.Linq;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

public class EncryptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-enc-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public EncryptionTests()
    {
        Directory.CreateDirectory(_dir);
        // Fixed, deterministic 32-byte key (seeded Random ⇒ reproducible).
        new Random(1234).NextBytes(_key);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch { /* best-effort cleanup */ }
    }

    private KvasarOptions Options(string name = "store", bool encrypt = true, byte[]? key = null) => new() {
        BasePath = Path.Combine(_dir, name),
        EncryptionKey = key ?? _key,
        DisableEncryption = !encrypt,
        PageSize = 4096,
        SegmentBytes = 64 * 1024,
    };

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

    // Slot 0 is the active data file of a freshly created store, and nothing here compacts.
    private string LogPath(string name = "store") => Path.Combine(_dir, name + ".0.kdat");

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    // 1a. Encryption ON: a distinctive marker must NOT survive as plaintext on disk.
    [Fact]
    public async Task EncryptedLogDoesNotLeakPlaintext()
    {
        var marker = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray(); // 0xA0..0xAF
        var value = new byte[64];
        for (var i = 0; i < value.Length; i++)
            value[i] = marker[i % marker.Length];

        await using (var store = await KvasarStore.Open(Options())) {
            await store.Set(K("marker-key"), value);
            await store.Flush(true);
        }

        var raw = await File.ReadAllBytesAsync(LogPath());
        Contains(raw, marker).Should().BeFalse("the 16-byte marker must be encrypted at rest");
        Contains(raw, K("marker-key")).Should().BeFalse("the key bytes must be encrypted at rest");
    }

    // 1b. Control: with encryption disabled the same marker DOES appear verbatim.
    [Fact]
    public async Task UnencryptedLogLeaksPlaintext()
    {
        var marker = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var value = new byte[64];
        for (var i = 0; i < value.Length; i++)
            value[i] = marker[i % marker.Length];

        await using (var store = await KvasarStore.Open(Options(encrypt: false))) {
            await store.Set(K("marker-key"), value);
            await store.Flush(true);
        }

        var raw = await File.ReadAllBytesAsync(LogPath());
        Contains(raw, marker).Should().BeTrue("with encryption disabled the value is plaintext on disk");
        Contains(raw, K("marker-key")).Should().BeTrue("with encryption disabled the key is plaintext on disk");
    }

    // 2. Wrong key ⇒ throw, and leave the store untouched.
    [Fact]
    public async Task WrongKeyThrowsAndLeavesTheStoreIntact()
    {
        // I9. This used to wipe & recreate: "no readable state" and "wrong key" were the same answer, so
        // a mistyped key destroyed an intact store — and it passed vacuously when the active segment was
        // empty, which is worse still. The .kvs key check value separates the two (§3.1), and the wrong-key
        // answer is the one case Open must NOT route into wipe-and-recreate.
        await using (var store = await KvasarStore.Open(Options())) {
            await store.Set(K("a"), K("secret"));
            await store.Set(K("b"), K("more-secret"));
            await store.Flush(true);
        }

        var wrong = new byte[32];
        new Random(9999).NextBytes(wrong); // a different, deterministic key
        wrong.Should().NotEqual(_key);

        Func<Task> open = async () => await KvasarStore.Open(Options(key: wrong));
        await open.Should().ThrowAsync<KvasarKeyException>();

        // The store is still there, and still readable under the right key.
        await using var store2 = await KvasarStore.Open(Options());
        (await store2.Get(K("a")))!.Value.ToArray().Should().Equal(K("secret"));
        (await store2.Get(K("b")))!.Value.ToArray().Should().Equal(K("more-secret"));
    }

    // 2b. An empty store must give the same answer: the check is on the file, not on its contents.
    [Fact]
    public async Task WrongKeyThrowsOnAnEmptyStoreToo()
    {
        await using (await KvasarStore.Open(Options())) { }

        var wrong = new byte[32];
        new Random(4242).NextBytes(wrong);
        Func<Task> open = async () => await KvasarStore.Open(Options(key: wrong));
        await open.Should().ThrowAsync<KvasarKeyException>();
    }

    // 3. Tamper detection: flipping a byte in the encrypted page region must not surface an
    //    exception to the caller on reopen; the store stays usable (wipe & recreate on auth
    //    failure, §8). We drop the rebuildable .kidx hint so open re-scans the log and actually
    //    exercises the GCM auth check on the tampered page.
    [Fact]
    public async Task TamperedLogReopensToUsableStore()
    {
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 0; i < 8; i++)
                await store.Set(K("key-" + i), K("payload-value-" + i));
            await store.Flush(true);
        }

        var logPath = LogPath();
        var bytes = await File.ReadAllBytesAsync(logPath);
        // Flip a byte well past the 64-byte plaintext header (inside an encrypted page).
        var pos = 64 + (bytes.Length - 64) / 2;
        pos.Should().BeGreaterThan(64);
        bytes[pos] ^= 0xFF;
        await File.WriteAllBytesAsync(logPath, bytes);

        // Force a full log rescan (so the tampered page is decrypted & authenticated at open).
        foreach (var kidx in Directory.GetFiles(_dir, "store.*.kidx"))
            File.Delete(kidx);

        KvasarStore? reopened = null;
        Func<Task> open = async () => reopened = await KvasarStore.Open(Options());
        await open.Should().NotThrowAsync("global auth failure must wipe & recreate, never throw to the caller (§8)");

        await using var store2 = reopened!;
        // Usable after recovery.
        await store2.Set(K("recovered"), K("ok"));
        (await store2.Get(K("recovered")))!.Value.ToArray().Should().Equal(K("ok"));
    }

    // 4. Correct key round-trips across reopen with encryption ON (sanity).
    [Fact]
    public async Task CorrectKeyRoundTripsAcrossReopen()
    {
        var payload = new byte[500];
        new Random(7).NextBytes(payload);

        await using (var store = await KvasarStore.Open(Options())) {
            await store.Set(K("k1"), K("v1"));
            await store.Set(K("blob"), payload);
            await store.Flush(true);
        }
        await using (var store = await KvasarStore.Open(Options())) {
            (await store.Get(K("k1")))!.Value.ToArray().Should().Equal(K("v1"));
            (await store.Get(K("blob")))!.Value.ToArray().Should().Equal(payload);
            (await store.Get(K("missing"))).Should().BeNull();
        }
    }

    // 5. Two stores with the SAME key but different BasePath are fully independent.
    [Fact]
    public async Task SameKeyDifferentBasePathsDoNotInterfere()
    {
        await using var a = await KvasarStore.Open(Options("alpha"));
        await using var b = await KvasarStore.Open(Options("beta"));

        await a.Set(K("shared"), K("from-a"));
        await b.Set(K("shared"), K("from-b"));
        await a.Set(K("only-a"), K("1"));
        await b.Set(K("only-b"), K("2"));

        (await a.Get(K("shared")))!.Value.ToArray().Should().Equal(K("from-a"));
        (await b.Get(K("shared")))!.Value.ToArray().Should().Equal(K("from-b"));
        (await a.Get(K("only-b"))).Should().BeNull("store A must not see store B's keys");
        (await b.Get(K("only-a"))).Should().BeNull("store B must not see store A's keys");
    }
}
