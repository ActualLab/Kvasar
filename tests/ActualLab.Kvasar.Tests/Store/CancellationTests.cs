using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using ActualLab.Kvasar.Crypto;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Cancellation contract (§4.4): a token abandons the caller's <i>wait</i>, never the write in flight.
/// An acknowledged write is always readable afterwards, and a cancelled one never corrupts the log.
/// The gate-driven tests pin that contract down at an exact point of the write; the timer-driven ones
/// stay as smoke tests, since on a fast machine their token may never land mid-append at all.
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
            await store.Flush();
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
            await store.Flush();
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

    // --- Deterministic injection (no timers) --------------------------------

    [Fact]
    public async Task CancelInsideSynchronousCopyChangesNothing()
    {
        // The value's bytes are served by a gate, so cancellation lands while Set is taking ownership.
        var value = Value(4000, 0x11);
        using var gate = new Gate();
        using var gatedValue = new GatedMemory(gate, value);
        var gatedMemory = new KvasarValue(gatedValue.Memory); // taking the Memory reads the span itself
        gatedValue.Arm();
        using var cts = new CancellationTokenSource();
        await using (var store = await KvasarStore.Open(Options())) {
            var setTask = Task.Run(async () => await store.Set(K("gated"), gatedMemory, cts.Token));
            gate.WaitUntilEntered();
            (await store.Get(K("gated"))).Should()
                .BeNull("the write is parked mid-append, so nothing is published yet");
            await cts.CancelAsync();
            gate.Resume();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setTask);
            // The copy now happens before the write lock is taken, so cancelling inside it means the
            // write body never starts — §4.4's "a cancelled write may still land" stays satisfied.
            await store.Flush();
            (await store.Get(K("gated"))).Should().BeNull();
        }

        await using (var store = await KvasarStore.Open(Options())) {
            (await store.Get(K("gated"))).Should().BeNull();
            (await ScanCount(store)).Should().Be(0);
        }
    }

    [Fact]
    public async Task CancelAfterTheAppendStillPublishesTheIndexEntry()
    {
        // The store hashes the key inside the write body — after the record's bytes are appended and
        // before the index is updated. Abandoning the body there would leave a value on disk that no
        // index entry points at, i.e. an acknowledged-looking write that Get can't see.
        var value = Value(4000, 0x22);
        using var gate = new Gate();
        using var cts = new CancellationTokenSource();
        var options = Options() with { Hasher = new GatedHasher(gate, K("gated")) };
        await using (var store = await KvasarStore.Open(options)) {
            var setTask = Task.Run(async () => await store.Set(K("gated"), value, cts.Token));
            gate.WaitUntilEntered();
            (await store.Get(K("gated"))).Should()
                .BeNull("the bytes are appended but the index entry isn't published yet");
            await cts.CancelAsync();
            gate.Resume();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setTask);
            await store.Flush();
            (await store.Get(K("gated")))!.Value.ToArray().Should().Equal(value);
        }

        await using (var store = await KvasarStore.Open(Options())) {
            (await store.Get(K("gated")))!.Value.ToArray().Should().Equal(value);
            (await ScanCount(store)).Should().Be(1);
        }
    }

    [Fact]
    public async Task CancelWhileWaitingForTheWriteLockChangesNothing()
    {
        // The other half of the contract: a token that fires before the write owns the lock must leave
        // the store untouched. The gate holds the lock for the whole window, so the loser can't sneak in.
        var value = Value(4000, 0x33);
        using var gate = new Gate();
        using var cts = new CancellationTokenSource();
        var options = Options() with { Hasher = new GatedHasher(gate, K("winner")) };
        await using (var store = await KvasarStore.Open(options)) {
            var winnerTask = Task.Run(async () => await store.Set(K("winner"), value));
            gate.WaitUntilEntered();
            var loserTask = Task.Run(async () => await store.Set(K("loser"), K("nope"), cts.Token));
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loserTask);
            gate.Resume();
            await winnerTask;
            await store.Flush();
        }

        await using (var store = await KvasarStore.Open(Options())) {
            (await store.Get(K("loser"))).Should().BeNull();
            (await store.Get(K("winner")))!.Value.ToArray().Should().Equal(value);
            (await ScanCount(store)).Should().Be(1);
        }
    }

    // Private methods

    private KvasarOptions Options() => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        PageSize = 512,
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

    // Several pages at PageSize = 512, so every gated write takes the multi-page append path.
    private static byte[] Value(int length, byte seed)
    {
        var value = new byte[length];
        for (var i = 0; i < length; i++)
            value[i] = (byte)(seed + i);
        return value;
    }

    private static async Task<int> ScanCount(KvasarStore store)
    {
        var count = 0;
        await foreach (var _ in store.Scan())
            count++;
        return count;
    }

    // Nested types

    /// <summary>
    /// Parks a writer at an exact point of the write path until the test thread releases it — the seam
    /// that makes cancellation deterministic instead of a race against a timer.
    /// </summary>
    private sealed class Gate : IDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _resumed = new(false);

        public void Dispose()
        {
            _entered.Dispose();
            _resumed.Dispose();
        }

        public void Enter()
        {
            _entered.Set();
            _resumed.Wait(Timeout);
        }

        public void WaitUntilEntered()
            => _entered.Wait(Timeout).Should().BeTrue("the write must reach the injection point");

        public void Resume()
            => _resumed.Set();
    }

    /// <summary>
    /// Backs a <see cref="KvasarValue"/> whose bytes can only be read through <see cref="Gate"/>: once
    /// armed, the append parks the next time it reads the span, i.e. mid-record.
    /// </summary>
    private sealed class GatedMemory(Gate gate, byte[] bytes) : MemoryManager<byte>
    {
        private int _isArmed;

        public void Arm()
            => _isArmed = 1;

        public override Span<byte> GetSpan()
        {
            if (Interlocked.Exchange(ref _isArmed, 0) == 1)
                gate.Enter();
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0)
            => throw new NotSupportedException(); // the write path only ever copies out of the span
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    // Delegates to the default hasher, but parks in `gate` the first time it hashes `triggerKey` — which
    // the store does inside the write body, between the append and the index update.
    private sealed class GatedHasher(Gate gate, byte[] triggerKey) : IKeyHasher
    {
        private int _isTriggered;

        public bool IsKeyed => true;
        public int SecretSize => 16;

        public ulong Hash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> secret)
        {
            if (key.SequenceEqual(triggerKey) && Interlocked.Exchange(ref _isTriggered, 1) == 0)
                gate.Enter();
            return KeyHashers.SipHash24.Hash(key, secret);
        }
    }
}
