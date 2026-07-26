using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Multi-reader / single-writer stress tests (§7). Values are self-verifying payloads so any torn
/// read (a mix of two versions, truncation, or garbage) is detectable from the value's own bytes.
/// Worker-thread failures are surfaced to the test thread via a <see cref="ConcurrentQueue{T}"/>.
/// </summary>
[Trait("Category", "Slow")]
public sealed class ConcurrencyTests : IDisposable
{
    private const int PageSize = 512; // small ⇒ values routinely span pages; segments roll quickly
    private const int KeyCount = 400;
    private const int DurationMs = 2000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-conc-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];
    private readonly int _readerCount = Math.Clamp(Environment.ProcessorCount - 1, 2, 6);

    public ConcurrencyTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 7 + 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private KvasarOptions Options(bool encrypt, long segmentBytes) => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = PageSize,
        SegmentBytes = segmentBytes,
        CompactionMinBytes = 8 * 1024,
        CompactionDeadRatio = 0.5,
    };

    // --- Self-verifying payload -------------------------------------------------

    private static byte[] Key(int keyIndex) => Encoding.ASCII.GetBytes($"key:{keyIndex:D6}");

    // Layout: [keyIndex u32 LE][version u32 LE][length u32 LE][filler...], where every filler byte is
    // a pure function of (keyIndex, version, position). A read that mixes two versions, is truncated,
    // or is garbage fails one of these checks.
    private static byte[] MakeValue(int keyIndex, int version)
    {
        var len = 12 + (keyIndex * 37 + version * 101) % 1900; // 12..~1911 bytes
        var v = new byte[len];
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(0), keyIndex);
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(4), version);
        BinaryPrimitives.WriteInt32LittleEndian(v.AsSpan(8), len);
        for (var i = 12; i < len; i++)
            v[i] = Fill(keyIndex, version, i);
        return v;
    }

    private static byte Fill(int keyIndex, int version, int i)
        => unchecked((byte)(keyIndex * 31 + version * 131 + i * 7 + 17));

    private static bool IsConsistent(ReadOnlySpan<byte> v)
    {
        if (v.Length < 12)
            return false;
        var keyIndex = BinaryPrimitives.ReadInt32LittleEndian(v[..4]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(v[4..8]);
        var len = BinaryPrimitives.ReadInt32LittleEndian(v[8..12]);
        if (len != v.Length || keyIndex < 0 || version <= 0)
            return false;
        for (var i = 12; i < v.Length; i++)
            if (v[i] != Fill(keyIndex, version, i))
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

    // --- Test 1 & 2: no torn reads + no lost committed writes -------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadersAndWriter_NoTornReads_NoLostWrites(bool encrypt)
    {
        var errors = new ConcurrentQueue<string>();
        await using var store = await KvasarStore.Open(Options(encrypt, segmentBytes: 128 * 1024));

        // Writer-owned state (read by the test thread only after the writer is joined).
        var lastVer = new int[KeyCount];
        var present = new bool[KeyCount];

        var deadline = Environment.TickCount64 + DurationMs;
        var readers = StartReaders(store, errors, deadline);

        var writer = Task.Run(async () => {
            try {
                var rnd = new Random(1_000);
                var batch = new List<(KvasarKey, KvasarValue?)>();
                var ops = 0;
                while (Environment.TickCount64 < deadline) {
                    if (rnd.Next(4) == 0) {
                        // SetMany batch of distinct keys (last-write-wins per key inside the batch).
                        batch.Clear();
                        var used = new HashSet<int>();
                        var count = 1 + rnd.Next(16);
                        for (var i = 0; i < count; i++) {
                            var k = rnd.Next(KeyCount);
                            if (!used.Add(k))
                                continue;
                            var v = ++lastVer[k];
                            present[k] = true;
                            batch.Add((Key(k), MakeValue(k, v)));
                        }
                        await store.SetMany(batch);
                    }
                    else {
                        var k = rnd.Next(KeyCount);
                        if (present[k] && rnd.Next(8) == 0) {
                            present[k] = false;
                            await store.Set(Key(k), null); // delete (tombstone)
                        }
                        else {
                            var v = ++lastVer[k];
                            present[k] = true;
                            await store.Set(Key(k), MakeValue(k, v));
                        }
                    }
                    if ((++ops & 63) == 0)
                        await store.Flush(false);
                }
            }
            catch (Exception ex) {
                errors.Enqueue("writer: " + ex);
            }
        });

        await writer;
        await Task.WhenAll(readers);

        errors.Should().BeEmpty();

        // No lost committed writes: quiesce, flush durably, then read every key back.
        await store.Flush(true);
        for (var k = 0; k < KeyCount; k++) {
            var got = await store.Get(Key(k));
            if (present[k]) {
                got.Should().NotBeNull($"key {k} was last committed as present (v{lastVer[k]})");
                got!.Value.ToArray().Should().Equal(MakeValue(k, lastVer[k]),
                    $"key {k} must read back the last committed value (v{lastVer[k]})");
            }
            else {
                got.Should().BeNull($"key {k} was last committed as absent/deleted");
            }
        }
    }

    // --- Test 3: concurrent readers during compaction ---------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadersDuringCompaction(bool encrypt)
    {
        var errors = new ConcurrentQueue<string>();
        // A low dead-ratio threshold ⇒ the writer compacts (and switches data slots) constantly while
        // readers run, so every read races a slot switch.
        await using var store = await KvasarStore.Open(Options(encrypt, segmentBytes: 24 * 1024));

        var deadline = Environment.TickCount64 + DurationMs;
        var readers = StartReaders(store, errors, deadline);

        var writer = Task.Run(async () => {
            try {
                var rnd = new Random(2_000);
                var ops = 0;
                while (Environment.TickCount64 < deadline) {
                    var k = rnd.Next(KeyCount);
                    await store.Set(Key(k), MakeValue(k, rnd.Next(1, 1 << 20)));
                    if ((++ops & 31) == 0)
                        await store.Flush(false);
                    if ((ops & 511) == 0)
                        await store.Compact(); // slot switch concurrent with lock-free reads (§4)
                }
            }
            catch (Exception ex) {
                errors.Enqueue("writer: " + ex);
            }
        });

        await writer;
        await Task.WhenAll(readers);

        errors.Should().BeEmpty();

        // Compaction recycles slots in place: the file set is the same two data files it started with.
        Directory.GetFiles(_dir, "store.*.kdat").Should().HaveCount(2);
        await store.Flush(true);
        for (var k = 0; k < KeyCount; k++)
            (await store.Get(Key(k))).Should().NotBeNull($"key {k} must survive every compaction");
    }

    // --- Test 4: Scan during writes --------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanDuringWrites(bool encrypt)
    {
        var errors = new ConcurrentQueue<string>();
        await using var store = await KvasarStore.Open(Options(encrypt, segmentBytes: 48 * 1024));

        // Seed so Scan has something to enumerate from the start.
        for (var k = 0; k < KeyCount; k++)
            await store.Set(Key(k), MakeValue(k, 1));
        await store.Flush(false);

        var deadline = Environment.TickCount64 + DurationMs;

        var scanners = new List<Task>();
        for (var t = 0; t < 2; t++) {
            scanners.Add(Task.Run(async () => {
                try {
                    var all = new List<(KvasarKey Key, KvasarValue Value)>();
                    while (Environment.TickCount64 < deadline) {
                        // Scan must never throw and every value it yields must be internally consistent
                        // (Scan may observe any snapshot-ish set as the writer mutates).
                        all.Clear();
                        await foreach (var item in store.Scan())
                            all.Add(item);
                        foreach (var (_, value) in all) {
                            if (!IsConsistent(value.Span)) {
                                errors.Enqueue($"Scan yielded inconsistent value: {Hex(value.Span)}");
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    errors.Enqueue("scanner: " + ex);
                }
            }));
        }

        var writer = Task.Run(async () => {
            try {
                var rnd = new Random(3_000);
                var ops = 0;
                while (Environment.TickCount64 < deadline) {
                    var k = rnd.Next(KeyCount);
                    await store.Set(Key(k), MakeValue(k, rnd.Next(2, 1 << 20)));
                    if ((++ops & 63) == 0)
                        await store.Flush(false);
                }
            }
            catch (Exception ex) {
                errors.Enqueue("writer: " + ex);
            }
        });

        await writer;
        await Task.WhenAll(scanners);

        errors.Should().BeEmpty();
    }

    // --- Shared reader workers --------------------------------------------------

    private List<Task> StartReaders(KvasarStore store, ConcurrentQueue<string> errors, long deadline)
    {
        var readers = new List<Task>(_readerCount);
        for (var t = 0; t < _readerCount; t++) {
            var seed = 5_000 + t;
            readers.Add(Task.Run(async () => {
                try {
                    var rnd = new Random(seed);
                    var keys = new KvasarKey[16];
                    while (Environment.TickCount64 < deadline) {
                        if (rnd.Next(3) == 0) {
                            // Batched GetMany.
                            for (var i = 0; i < keys.Length; i++)
                                keys[i] = Key(rnd.Next(KeyCount));
                            var results = await store.GetMany(keys);
                            for (var i = 0; i < results.Length; i++) {
                                if (results[i] is { } val && !IsConsistent(val.Span)) {
                                    errors.Enqueue($"Torn/garbage GetMany value: {Hex(val.Span)}");
                                    return;
                                }
                            }
                        }
                        else {
                            var res = await store.Get(Key(rnd.Next(KeyCount)));
                            if (res is { } val && !IsConsistent(val.Span)) {
                                errors.Enqueue($"Torn/garbage Get value: {Hex(val.Span)}");
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex) {
                    errors.Enqueue("reader: " + ex);
                }
            }));
        }
        return readers;
    }
}
