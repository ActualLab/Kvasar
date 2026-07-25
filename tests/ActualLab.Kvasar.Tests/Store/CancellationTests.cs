using System.Globalization;
using System.IO;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Cancellation contract (§4.4): a token abandons the caller's <i>wait</i>, never the write in flight.
/// An acknowledged write is always readable afterwards, and a cancelled one never corrupts the log.
/// </summary>
public class CancellationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-cancel-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public CancellationTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 5 + 11);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task AlreadyCancelledWriteChangesNothing()
    {
        // The token fires while waiting for the write lock, i.e. before anything is mutated.
        await using var store = await KvasarStore.Open(Options());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.Set(K("a"), K("alpha"), cts.Token));
        (await store.Get(K("a"))).Should().BeNull();

        // ... and the store is still fully usable.
        await store.Set(K("a"), K("alpha"));
        (await store.Get(K("a")))!.Value.ToArray().Should().Equal(K("alpha"));
    }

    [Fact]
    public async Task CancelledWritesNeverCorruptTheLog()
    {
        // Cancels each write after a random sub-millisecond delay, so the token lands all over the
        // append path — including mid-multi-page-record, the case that would otherwise leave a record
        // header claiming more bytes than were written.
        var rnd = new Random(1234);
        var acknowledged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(Options())) {
            for (var i = 0; i < 300; i++) {
                var key = $"key-{i:D4}";
                var value = RandomValue(rnd, i);
                using var cts = new CancellationTokenSource(TimeSpan.FromTicks(rnd.Next(0, 20_000)));
                try {
                    await store.Set(key, value, cts.Token);
                    acknowledged[key] = value; // returned normally ⇒ must be readable from now on
                }
                catch (OperationCanceledException) {
                    // The write may or may not have landed — both are correct.
                }
            }
            await store.Flush(true);
        }

        await using (var store = await KvasarStore.Open(Options())) {
            acknowledged.Should().NotBeEmpty();
            foreach (var (key, value) in acknowledged)
                (await store.Get(key))!.Value.ToArray().Should().Equal(value);
            // A full scan decodes every record on disk: a torn record would surface here as a short
            // scan (the walk stops at the tear) or a corrupt-record throw.
            var scanned = 0;
            await foreach (var _ in store.Scan())
                scanned++;
            scanned.Should().BeGreaterThanOrEqualTo(acknowledged.Count);
        }
    }

    [Fact]
    public async Task CancelledBatchIsStillAtomicPerKey()
    {
        var rnd = new Random(4321);
        await using (var store = await KvasarStore.Open(Options())) {
            for (var round = 0; round < 30; round++) {
                var updates = new (KvasarKey Key, KvasarValue? Value)[16];
                for (var i = 0; i < updates.Length; i++)
                    updates[i] = ($"batch-{round:D2}-{i:D2}", RandomValue(rnd, i));
                using var cts = new CancellationTokenSource(TimeSpan.FromTicks(rnd.Next(0, 40_000)));
                try {
                    await store.SetMany(updates, cts.Token);
                }
                catch (OperationCanceledException) {
                    // Fine: SetMany is not transactional across keys (§4.3), only per key.
                }
            }
            await store.Flush(true);
        }

        // Every value that survived must be intact — a partial value is corruption, a missing one isn't.
        await using (var store = await KvasarStore.Open(Options())) {
            await foreach (var (key, value) in store.Scan()) {
                var i = int.Parse(key.AsString[^2..], CultureInfo.InvariantCulture);
                value.Length.Should().Be(ValueLength(i));
                value.Span[0].Should().Be((byte)i);
            }
        }
    }

    // Private methods

    private KvasarOptions Options() => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        PageSize = 512,
        SegmentBytes = 32 * 1024,
        FlushDelay = TimeSpan.Zero, // every Set writes through, so cancellation hits real I/O
    };

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

    // Every fourth value spans several pages, so the multi-page append path gets cancelled too.
    private static int ValueLength(int i) => i % 4 == 0 ? 2000 : 40;

    private static byte[] RandomValue(Random rnd, int i)
    {
        var value = new byte[ValueLength(i)];
        rnd.NextBytes(value);
        value[0] = (byte)i;
        return value;
    }
}
