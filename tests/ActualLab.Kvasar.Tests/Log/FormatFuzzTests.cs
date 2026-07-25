using System.Buffers.Binary;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Log;

/// <summary>
/// Adversarial coverage for the on-disk parsers (§5.2 records, §6.5 <c>.kidx</c>, §5.3 segment header):
/// mutated/truncated input must be rejected cleanly, never with a bounds/overflow exception.
/// </summary>
public class FormatFuzzTests
{
    private const uint FormatVer = 1;
    private const int Seed = 20260724;

    // --- Record round-trip fidelity ----------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(127, 0)]
    [InlineData(128, 1)]        // keyLen crosses the 1→2 byte varint boundary
    [InlineData(16383, 1)]
    [InlineData(16384, 1)]      // keyLen crosses the 2→3 byte varint boundary
    [InlineData(1, 4095)]
    [InlineData(1, 4096)]       // value exactly a default page
    [InlineData(1, 4097)]
    [InlineData(3, 16375)]      // recordLen straddles the 2→3 byte varint boundary
    [InlineData(3, 16376)]
    public void RoundTripsEdgeSizes(int keyLen, int valueLen)
    {
        var key = Payload(keyLen, 11);
        var value = Payload(valueLen, 12);
        var buf = new byte[RecordCodec.MaxHeaderSize(keyLen) + valueLen];

        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueType.Raw, key, value, false);
        written.Should().Be(RecordCodec.GetRecordLength(keyLen, valueLen, false));

        RecordCodec.TryDecode(buf.AsMemory(0, written), out var view, out var totalLen).Should().BeTrue();
        totalLen.Should().Be(written);
        view.Key.ToArray().Should().Equal(key);
        view.Value.ToArray().Should().Equal(value);

        RecordCodec.TryDecode(buf.AsSpan(0, written), out var spanView, out var spanTotalLen).Should().BeTrue();
        spanTotalLen.Should().Be(written);
        spanView.Key.ToArray().Should().Equal(key);
        spanView.Value.ToArray().Should().Equal(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    public void TombstoneDropsTheSuppliedValue(int keyLen)
    {
        var key = Payload(keyLen, 13);
        var value = Payload(64, 14);
        var buf = new byte[RecordCodec.MaxHeaderSize(keyLen) + value.Length];

        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueType.Raw, key, value, true);
        written.Should().Be(RecordCodec.GetRecordLength(keyLen, value.Length, true));

        RecordCodec.TryDecode(buf.AsMemory(0, written), out var view, out var totalLen).Should().BeTrue();
        totalLen.Should().Be(written);
        view.IsTombstone.Should().BeTrue();
        view.Key.ToArray().Should().Equal(key);
        view.Value.Length.Should().Be(0);
    }

    [Fact]
    public void EveryStrictPrefixOfARecordIsRejected()
    {
        foreach (var (keyLen, valueLen, isTombstone) in RecordShapes()) {
            var record = EncodeRecord(keyLen, valueLen, isTombstone);
            for (var cut = 0; cut < record.Length; cut++) {
                RecordCodec.TryDecode(record.AsMemory(0, cut), out _, out _)
                    .Should().BeFalse($"prefix of {cut}/{record.Length} bytes is torn");
                RecordCodec.TryDecode(record.AsSpan(0, cut), out _, out _)
                    .Should().BeFalse($"prefix of {cut}/{record.Length} bytes is torn");
            }
        }
    }

    [Theory]
    [InlineData(0UL)]                    // padding
    [InlineData(1UL)]                    // shorter than the fixed 2-byte header
    [InlineData(200UL)]                  // longer than the buffer
    [InlineData((ulong)int.MaxValue)]
    [InlineData(1UL << 62)]
    [InlineData(1UL << 63)]              // negative once cast to long
    [InlineData(ulong.MaxValue)]
    public void HostileRecordLengthIsRejected(ulong bodyLen)
    {
        var buf = new byte[64];
        var pos = Varint.Write(buf, bodyLen);
        buf.AsSpan(pos).Fill(0x41);

        NoCrash(() => RecordCodec.TryDecode(buf.AsSpan(), out _, out _).Should().BeFalse());
        var memory = buf.AsMemory();
        NoCrash(() => RecordCodec.TryDecode(memory, out _, out _).Should().BeFalse());
    }

    [Theory]
    [InlineData(100UL)]                  // past the end of a 62-byte body
    [InlineData(1UL << 20)]
    [InlineData(uint.MaxValue)]
    [InlineData(1UL << 62)]
    public void HostileKeyLengthIsRejected(ulong keyLen)
    {
        var body = new byte[62];
        body[0] = 0;
        body[1] = 0;
        Varint.Write(body.AsSpan(2), keyLen);
        var buf = new byte[64];
        var pos = Varint.Write(buf, (ulong)body.Length);
        body.CopyTo(buf.AsSpan(pos));

        NoCrash(() => RecordCodec.TryDecode(buf.AsSpan(0, pos + body.Length), out _, out _).Should().BeFalse());
        var memory = buf.AsMemory(0, pos + body.Length);
        NoCrash(() => RecordCodec.TryDecode(memory, out _, out _).Should().BeFalse());
    }

    // --- Randomized mutation fuzz ------------------------------------------

    [Fact]
    public void RecordCodecSurvivesRandomMutations()
    {
        // Corpus payloads stay in 0x00..0x7F so a corrupted length field can't chain into a 9+ byte
        // varint made of payload bytes — that case is covered deterministically elsewhere.
        var rnd = new Random(Seed);
        for (var i = 0; i < 20_000; i++) {
            var (keyLen, valueLen, isTombstone) = RandomShape(rnd);
            var record = EncodeRecord(keyLen, valueLen, isTombstone);
            var mutated = Mutate(record, rnd);
            var memory = mutated.AsMemory();
            NoCrash(() => {
                if (!RecordCodec.TryDecode(memory, out var view, out var totalLen))
                    return;

                totalLen.Should().BeInRange(1, memory.Length);
                view.Key.Length.Should().BeLessThanOrEqualTo(totalLen);
                view.Value.Length.Should().BeLessThanOrEqualTo(totalLen);
            });
            var span = mutated;
            NoCrash(() => RecordCodec.TryDecode(span.AsSpan(), out _, out _));
        }
    }

    [Fact]
    public void VarintSurvivesRandomBytes()
    {
        var rnd = new Random(Seed + 1);
        var buf = new byte[Varint.MaxSize + 4];
        for (var i = 0; i < 100_000; i++) {
            rnd.NextBytes(buf);
            var length = rnd.Next(0, buf.Length + 1);
            var source = buf.AsSpan(0, length);
            // A span can't be captured by a lambda, so this one is asserted inline.
            if (!Varint.TryRead(source, out var value, out var bytesRead)) {
                bytesRead.Should().Be(0);
                value.Should().Be(0);
                continue;
            }
            bytesRead.Should().BeInRange(1, Math.Min(length, Varint.MaxSize));
            // Whatever follows the stop byte must not change the result.
            Varint.TryRead(source[..bytesRead], out var exact, out var exactBytesRead).Should().BeTrue();
            exact.Should().Be(value);
            exactBytesRead.Should().Be(bytesRead);
        }
    }

    // --- .kidx --------------------------------------------------------------

    [Fact]
    public async Task IndexFileSurvivesRandomMutations()
    {
        var rnd = new Random(Seed + 2);
        var path = TempPath("kidx-fuzz");
        try {
            for (var i = 0; i < 500; i++) {
                var original = await BuildIndexFile(rnd);
                var mutated = Mutate(original, rnd);
                if (rnd.Next(3) == 0 && mutated.Length > 0)
                    mutated = mutated[..rnd.Next(mutated.Length)];
                await File.WriteAllBytesAsync(path, mutated);
                await NoCrash(async () => {
                    var loaded = await IndexFile.TryLoad(path, FormatVer);
                    if (loaded is { } cp)
                        cp.Entries.Should().NotBeNull();
                });
            }
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task IndexFileTruncatedAtAnyLengthIsClean()
    {
        var path = TempPath("kidx-cut");
        try {
            var entries = Entries(6);
            await IndexFile.WriteCheckpoint(path, entries, (3u, 999u), FormatVer);
            for (var i = 0; i < 4; i++)
                await IndexFile.AppendDelta(path, Entry((ulong)(100 + i), 2, (uint)(i * 8), 8));
            var full = await File.ReadAllBytesAsync(path);

            for (var cut = 0; cut <= full.Length; cut++) {
                await File.WriteAllBytesAsync(path, full[..cut]);
                await NoCrash(async () => await IndexFile.TryLoad(path, FormatVer));
            }
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task IndexFilePartialTrailingDeltaOfAnyLengthIsDropped()
    {
        var path = TempPath("kidx-tail");
        try {
            for (var partial = 1; partial < IndexFile.EntrySize; partial++) {
                await IndexFile.WriteCheckpoint(path, Entries(2), (1u, 0u), FormatVer);
                await IndexFile.AppendDelta(path, Entry(50, 1, 200, 20));
                var bytes = await File.ReadAllBytesAsync(path);
                await File.WriteAllBytesAsync(path, [..bytes, ..new byte[partial]]);

                var loaded = await IndexFile.TryLoad(path, FormatVer);
                loaded.Should().NotBeNull();
                loaded!.Value.Entries.Should().HaveCount(3);
                loaded.Value.Entries.Select(e => e.KeyHash).Should().Contain(50UL);
            }
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task IndexFileCheckpointCountBeyondTheFileIsRejected()
    {
        var path = TempPath("kidx-count");
        try {
            await IndexFile.WriteCheckpoint(path, Entries(3), (1u, 0u), FormatVer);
            var bytes = await File.ReadAllBytesAsync(path);
            foreach (var count in new long[] { 4, 100, 1L << 20, int.MaxValue }) {
                BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), count);
                await File.WriteAllBytesAsync(path, bytes);
                await NoCrash(async () => (await IndexFile.TryLoad(path, FormatVer)).Should().BeNull());
            }
            // A negative count must be rejected rather than sign-extended into a span length.
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), -1);
            await File.WriteAllBytesAsync(path, bytes);
            await NoCrash(async () => (await IndexFile.TryLoad(path, FormatVer)).Should().BeNull());
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task IndexFileEntrySizeMismatchIsRejected()
    {
        var path = TempPath("kidx-esize");
        try {
            await IndexFile.WriteCheckpoint(path, Entries(3), (1u, 0u), FormatVer);
            var bytes = await File.ReadAllBytesAsync(path);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)IndexFile.EntrySize + 1);
            await File.WriteAllBytesAsync(path, bytes);

            (await IndexFile.TryLoad(path, FormatVer)).Should().BeNull();
        }
        finally {
            TryDelete(path);
        }
    }

    // --- Segment header ------------------------------------------------------

    [Fact]
    public void SegmentHeaderRoundTripsAndRejectsShortInput()
    {
        var header = new SegmentHeader(FormatVer, 4096, 7, flags: 0x1234);
        var bytes = new byte[KvasarConstants.SegmentHeaderSize];
        header.Write(bytes);

        var read = SegmentHeader.Read(bytes);
        read.FormatVer.Should().Be(header.FormatVer);
        read.PageSize.Should().Be(header.PageSize);
        read.SegmentId.Should().Be(header.SegmentId);
        read.Flags.Should().Be(header.Flags);
        read.FileSalt.Should().Equal(header.FileSalt);
        bytes.AsSpan(36).ToArray().Should().AllSatisfy(b => b.Should().Be(0));

        for (var cut = 0; cut < KvasarConstants.SegmentHeaderSize; cut++) {
            var truncated = bytes[..cut];
            Assert.Throws<KvasarCorruptException>(() => SegmentHeader.Read(truncated));
        }
    }

    [Fact]
    public void SegmentHeaderSurvivesRandomMutations()
    {
        var rnd = new Random(Seed + 3);
        var bytes = new byte[KvasarConstants.SegmentHeaderSize + 32];
        new SegmentHeader(FormatVer, 4096, 7).Write(bytes);
        for (var i = 0; i < 20_000; i++) {
            var mutated = Mutate(bytes, rnd);
            if (rnd.Next(4) == 0)
                mutated = mutated[..rnd.Next(mutated.Length + 1)];
            NoCrash(() => {
                var header = SegmentHeader.Read(mutated);
                header.FileSalt.Length.Should().Be(KvasarConstants.FileSaltSize);
            });
        }
    }

    // Private methods

    private static void NoCrash(Action action)
    {
        try {
            action();
        }
        catch (KvasarCorruptException) {
            // The only exception a corrupt on-disk format may surface (SPEC §12).
        }
        catch (Exception e) {
            Assert.Fail($"Parsing corrupt input threw {e.GetType().Name}: {e.Message}");
        }
    }

    private static async Task NoCrash(Func<Task> action)
    {
        try {
            await action();
        }
        catch (KvasarCorruptException) {
            // The only exception a corrupt on-disk format may surface (SPEC §12).
        }
        catch (Exception e) {
            Assert.Fail($"Parsing corrupt input threw {e.GetType().Name}: {e.Message}");
        }
    }

    private static byte[] Mutate(byte[] source, Random rnd)
    {
        var result = source.ToArray();
        if (result.Length == 0)
            return result;

        var mutations = rnd.Next(1, 4);
        for (var i = 0; i < mutations; i++) {
            var at = rnd.Next(result.Length);
            result[at] = rnd.Next(3) switch {
                0 => (byte)(result[at] ^ (1 << rnd.Next(8))), // bit flip
                1 => (byte)rnd.Next(256),                     // random byte
                _ => (byte)(rnd.Next(2) == 0 ? 0x00 : 0xFF),  // extreme byte
            };
        }
        return result;
    }

    private static (int KeyLen, int ValueLen, bool IsTombstone) RandomShape(Random rnd)
        => (rnd.Next(5) == 0 ? rnd.Next(128, 1024) : rnd.Next(0, 24),
            rnd.Next(5) == 0 ? rnd.Next(128, 2048) : rnd.Next(0, 24),
            rnd.Next(4) == 0);

    private static IEnumerable<(int KeyLen, int ValueLen, bool IsTombstone)> RecordShapes()
    {
        yield return (0, 0, false);
        yield return (1, 1, false);
        yield return (5, 0, false);
        yield return (9, 0, true);
        yield return (200, 300, false);
        yield return (300, 5, false);
    }

    private static byte[] EncodeRecord(int keyLen, int valueLen, bool isTombstone)
    {
        var key = Payload(keyLen, 21);
        var value = Payload(valueLen, 22);
        var buf = new byte[RecordCodec.MaxHeaderSize(keyLen) + valueLen];
        var written = RecordCodec.Encode(buf, RecordFlags.None, KvasarValueType.Raw, key, value, isTombstone);
        return buf[..written];
    }

    private static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        var rnd = new Random(seed);
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)rnd.Next(0, 0x80);
        return bytes;
    }

    private static async Task<byte[]> BuildIndexFile(Random rnd)
    {
        var path = TempPath("kidx-src");
        try {
            await IndexFile.WriteCheckpoint(
                path, Entries(rnd.Next(0, 8)), ((uint)rnd.Next(1, 4), (uint)rnd.Next(0, 1 << 16)), FormatVer);
            var deltas = rnd.Next(0, 5);
            for (var i = 0; i < deltas; i++)
                await IndexFile.AppendDelta(path, Entry((ulong)rnd.Next(1, 100), 1, (uint)rnd.Next(), 16));
            return await File.ReadAllBytesAsync(path);
        }
        finally {
            TryDelete(path);
        }
    }

    private static IndexEntry[] Entries(int count)
    {
        var result = new IndexEntry[count];
        for (var i = 0; i < count; i++)
            result[i] = Entry((ulong)(i + 1) * 0x1111_1111u, 1, (uint)(i * 32), 32);
        return result;
    }

    private static IndexEntry Entry(ulong keyHash, uint segmentId, uint offset, uint length)
        => new() { KeyHash = keyHash, SegmentId = segmentId, Offset = offset, Length = length, Flags = 0 };

    private static string TempPath(string prefix)
        => Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.bin");

    private static void TryDelete(string path)
    {
        try {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
    }
}
