using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Write→read consistency stress tests (§7). Complements <see cref="ConcurrencyTests"/> (which stresses
/// torn-reads / no-lost-writes with a single shared writer). Here we assert:
/// (1) each thread reads back its OWN writes over a disjoint key namespace despite concurrent activity;
/// (2) a write completed on one thread is visible to a read handed off to another thread;
/// (3) once a reader is handed version N of a key it never observes a version &lt; N (monotonic visibility).
/// Values are self-describing payloads so any torn/garbage read is detectable from the bytes.
/// Worker-thread failures are surfaced to the test thread via a <see cref="ConcurrentQueue{T}"/>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ConsistencyTests : IDisposable
{
    private const int PageSize = 512; // small ⇒ "large" values span multiple pages; segments roll quickly

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-cons-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];
    private readonly int _threadCount = Math.Clamp(Environment.ProcessorCount, 2, 8);

    public ConsistencyTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 11 + 5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private KvasarOptions Options(bool encrypt) => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = PageSize,
        CompactionMinBytes = 8 * 1024,
        CompactionDeadRatio = 0.5,
    };

    // --- Self-describing payload -------------------------------------------------
    // Layout: [keyId u32 LE][version u32 LE][length u32 LE][filler...], every filler byte a pure
    // function of (keyId, version, position). A mix of two versions, truncation, or garbage fails a check.

    private static byte[] MakeValue(int keyId, int version, int length)
    {
        var len = Math.Max(12, length);
        var v = new byte[len];
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(0), keyId);
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4), version);
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(8), len);
        for (var i = 12; i < len; i++)
            v[i] = Fill(keyId, version, i);
        return v;
    }

    private static byte Fill(int keyId, int version, int i)
        => unchecked((byte)(keyId * 31 + version * 131 + i * 7 + 17));

    // Verify a value is internally consistent and, if so, report its (keyId, version).
    private static bool TryDecode(ReadOnlySpan<byte> v, out int keyId, out int version)
    {
        keyId = 0;
        version = 0;
        if (v.Length < 12)
            return false;
        keyId = BinaryPrimitives.ReadInt32LittleEndian(v[..4]);
        version = BinaryPrimitives.ReadInt32LittleEndian(v[4..8]);
        var len = BinaryPrimitives.ReadInt32LittleEndian(v[8..12]);
        if (len != v.Length || version < 0)
            return false;
        for (var i = 12; i < v.Length; i++)
            if (v[i] != Fill(keyId, version, i))
                return false;
        return true;
    }

    private static string Hex(ReadOnlySpan<byte> v)
    {
        var n = Math.Min(v.Length, 24);
        var sb = new StringBuilder($"len={v.Length} [");
        for (var i = 0; i < n; i++)
            sb.Append(v[i].ToString("X2", null));
        return sb.Append(']').ToString();
    }

    // A random length across the interesting classes: 0, small, medium, and > PageSize (multi-page).
    private static int PickLength(Random rnd) => rnd.Next(5) switch {
        0 => 0,                                 // empty (present, not a miss)
        1 => rnd.Next(1, 32),                   // tiny
        2 => rnd.Next(32, PageSize),            // sub-page
        3 => PageSize + rnd.Next(1, PageSize),  // just over a page (multi-page)
        _ => rnd.Next(2 * PageSize, 4 * PageSize), // several pages
    };

    // --- Test 1: per-thread isolated create/update/remove sequences --------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PerThreadIsolatedSequences_SelfConsistent(bool encrypt)
    {
        const int keysPerThread = 24;
        const int opsPerThread = 3000;

        var errors = new ConcurrentQueue<string>();
        await using var store = await KvasarStore.Open(Options(encrypt));

        var workers = new List<Task>(_threadCount);
        for (var t = 0; t < _threadCount; t++) {
            var threadIndex = t;
            workers.Add(Task.Run(async () => {
                try {
                    var rnd = new Random(7_000 + threadIndex);
                    // Thread-local expected state: local keyId -> expected bytes (null = absent/deleted).
                    var expected = new Dictionary<int, byte[]?>();
                    // Per-key version counter so successive writes to the same key differ.
                    var versions = new int[keysPerThread];

                    // Precompute this thread's disjoint keys.
                    var keys = new KvasarKey[keysPerThread];
                    for (var k = 0; k < keysPerThread; k++)
                        keys[k] = Encoding.ASCII.GetBytes($"t{threadIndex:D2}:k{k:D3}");

                    for (var op = 0; op < opsPerThread; op++) {
                        var k = rnd.Next(keysPerThread);
                        var key = keys[k];
                        var globalKeyId = threadIndex * keysPerThread + k;
                        var present = expected.TryGetValue(k, out var cur) && cur is not null;

                        // Choose an op: remove only when present; otherwise create/update.
                        var roll = rnd.Next(10);
                        if (present && roll < 3) {
                            // remove
                            await store.Set(key, null);
                            expected[k] = null;
                        }
                        else {
                            // create or update
                            var version = ++versions[k];
                            var value = MakeValue(globalKeyId, version, PickLength(rnd));
                            await store.Set(key, value);
                            expected[k] = value;
                        }

                        // Assert the write is immediately readable by THIS thread.
                        var got = await store.Get(key);
                        var want = expected[k];
                        if (want is null) {
                            if (got is not null) {
                                errors.Enqueue($"t{threadIndex} k{k}: expected absent after remove, got {Hex(got.Value.Span)}");
                                return;
                            }
                        }
                        else {
                            if (got is null) {
                                errors.Enqueue($"t{threadIndex} k{k}: expected present ({want.Length} bytes) but Get returned null");
                                return;
                            }
                            if (!got.Value.Span.SequenceEqual(want)) {
                                errors.Enqueue($"t{threadIndex} k{k}: own write not read back. want len={want.Length}, got {Hex(got.Value.Span)}");
                                return;
                            }
                        }

                        // Occasionally re-verify a DIFFERENT own key still matches expectation (no cross-key clobber).
                        if ((op & 7) == 0) {
                            var k2 = rnd.Next(keysPerThread);
                            if (expected.TryGetValue(k2, out var want2)) {
                                var got2 = await store.Get(keys[k2]);
                                if (want2 is null) {
                                    if (got2 is not null) {
                                        errors.Enqueue($"t{threadIndex} k{k2}: expected absent, got {Hex(got2.Value.Span)}");
                                        return;
                                    }
                                }
                                else if (got2 is null || !got2.Value.Span.SequenceEqual(want2)) {
                                    errors.Enqueue($"t{threadIndex} k{k2}: stale/lost own value; want len={want2.Length}");
                                    return;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    errors.Enqueue($"t{threadIndex}: {ex}");
                }
            }));
        }

        await Task.WhenAll(workers);

        errors.Should().BeEmpty();
    }

    // --- Test 2: cross-thread write-then-read handoff ----------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CrossThreadWriteThenReadHandoff(bool encrypt, bool flushBeforeHandoff)
    {
        const int totalIterations = 5000;
        var producerCount = Math.Clamp(_threadCount - 1, 1, 4);
        var consumerCount = Math.Clamp(_threadCount / 3, 1, 2);
        var perProducer = totalIterations / producerCount;

        var errors = new ConcurrentQueue<string>();
        await using var store = await KvasarStore.Open(Options(encrypt));

        // Each item: a key written exactly once (globally unique) and the value it was given.
        var handoff = Channel.CreateBounded<(byte[] Key, byte[] Value)>(1024);

        var producers = new List<Task>(producerCount);
        for (var p = 0; p < producerCount; p++) {
            var producerIndex = p;
            producers.Add(Task.Run(async () => {
                try {
                    var rnd = new Random(11_000 + producerIndex);
                    for (var i = 0; i < perProducer; i++) {
                        // Globally-unique key (distinct each iteration): never overwritten or deleted.
                        var key = Encoding.ASCII.GetBytes($"p{producerIndex:D2}:i{i:D7}");
                        var globalId = producerIndex * perProducer + i;
                        var value = MakeValue(globalId, 1, PickLength(rnd));
                        await store.Set(key, value);
                        if (flushBeforeHandoff)
                            await store.Flush();
                        await handoff.Writer.WriteAsync((key, value));
                    }
                }
                catch (Exception ex) {
                    errors.Enqueue($"producer{producerIndex}: {ex}");
                }
            }));
        }

        var consumers = new List<Task>(consumerCount);
        for (var c = 0; c < consumerCount; c++) {
            consumers.Add(Task.Run(async () => {
                try {
                    await foreach (var (key, value) in handoff.Reader.ReadAllAsync()) {
                        var got = await store.Get(key);
                        if (got is null) {
                            errors.Enqueue($"handoff: key {Encoding.ASCII.GetString(key)} written then invisible to reader (Get==null)");
                            return;
                        }
                        if (!got.Value.Span.SequenceEqual(value)) {
                            errors.Enqueue($"handoff: key {Encoding.ASCII.GetString(key)} value mismatch. want len={value.Length}, got {Hex(got.Value.Span)}");
                            return;
                        }
                    }
                }
                catch (Exception ex) {
                    errors.Enqueue($"consumer: {ex}");
                }
            }));
        }

        await Task.WhenAll(producers);
        handoff.Writer.Complete();
        await Task.WhenAll(consumers);

        errors.Should().BeEmpty();
    }

    // --- Test 3: monotonic cross-thread update visibility ------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateVisibleCrossThread_Monotonic(bool encrypt)
    {
        const int sharedKeyCount = 8;
        const int rounds = 6000;

        var errors = new ConcurrentQueue<string>();
        await using var store = await KvasarStore.Open(Options(encrypt));

        var keys = new KvasarKey[sharedKeyCount];
        for (var k = 0; k < sharedKeyCount; k++)
            keys[k] = Encoding.ASCII.GetBytes($"shared:k{k:D2}");

        // Seed v1 for every key so readers always find the key present.
        for (var k = 0; k < sharedKeyCount; k++)
            await store.Set(keys[k], MakeValue(k, 1, PickLength(new Random(k))));

        var handoff = Channel.CreateBounded<(int KeyId, int Version)>(1024);

        // Single writer ⇒ per-key versions are strictly increasing in the order published.
        var writer = Task.Run(async () => {
            try {
                var rnd = new Random(23_000);
                var versions = new int[sharedKeyCount];
                for (var k = 0; k < sharedKeyCount; k++)
                    versions[k] = 1;
                for (var r = 0; r < rounds; r++) {
                    var k = rnd.Next(sharedKeyCount);
                    var version = ++versions[k];
                    await store.Set(keys[k], MakeValue(k, version, PickLength(rnd)));
                    await handoff.Writer.WriteAsync((k, version));
                }
            }
            catch (Exception ex) {
                errors.Enqueue($"writer: {ex}");
            }
        });

        var readerCount = Math.Clamp(_threadCount - 1, 1, 4);
        var readers = new List<Task>(readerCount);
        for (var c = 0; c < readerCount; c++) {
            readers.Add(Task.Run(async () => {
                try {
                    await foreach (var (keyId, handedVersion) in handoff.Reader.ReadAllAsync()) {
                        var got = await store.Get(keys[keyId]);
                        if (got is null) {
                            errors.Enqueue($"monotonic: key {keyId} vanished (Get==null) after being handed v{handedVersion}");
                            return;
                        }
                        if (!TryDecode(got.Value.Span, out var gotKeyId, out var gotVersion)) {
                            errors.Enqueue($"monotonic: key {keyId} returned inconsistent value {Hex(got.Value.Span)}");
                            return;
                        }
                        if (gotKeyId != keyId) {
                            errors.Enqueue($"monotonic: key {keyId} returned foreign keyId {gotKeyId}");
                            return;
                        }
                        // Never observe an older version than the one already published & handed off.
                        if (gotVersion < handedVersion) {
                            errors.Enqueue($"monotonic: key {keyId} regressed. handed v{handedVersion}, observed v{gotVersion}");
                            return;
                        }
                    }
                }
                catch (Exception ex) {
                    errors.Enqueue($"reader: {ex}");
                }
            }));
        }

        await writer;
        handoff.Writer.Complete();
        await Task.WhenAll(readers);

        errors.Should().BeEmpty();
    }
}
