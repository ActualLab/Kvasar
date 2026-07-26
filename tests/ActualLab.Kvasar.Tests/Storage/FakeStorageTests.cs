using System.IO;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Storage;

public class FakeStorageTests
{
    private static readonly string Dir = Path.Combine("kvasar", "store");
    private static readonly string PathA = Path.Combine(Dir, "a.kdat");
    private static readonly string PathB = Path.Combine(Dir, "b.kdat");
    private static readonly string PathAIndex = Path.Combine(Dir, "a.kidx");

    [Fact]
    public async Task WriteIsVisibleBeforeFlushButNotStable()
    {
        var backend = new FakeStorageBackend();
        var data = MakeBytes(64, 1);
        await using var file = await backend.Open(PathA);
        await file.Write(0, data);

        file.Length.Should().Be(64);
        var buffer = new byte[64];
        await file.ReadExact(0, buffer);
        buffer.Should().Equal(data);
        backend.GetStableBytes(PathA).Should().BeEmpty();
        backend.GetBytes(PathA).Should().Equal(data);
    }

    [Fact]
    public async Task CrashLoseAllRevertsToLastFlush()
    {
        var backend = new FakeStorageBackend();
        var flushed = MakeBytes(32, 2);
        var unflushed = MakeBytes(32, 3);
        await using (var file = await backend.Open(PathA)) {
            await file.Write(0, flushed);
            await file.FlushToDisk();
            await file.Write(32, unflushed);
        }
        backend.Crash(CrashMode.LoseAll);

        backend.GetStableBytes(PathA).Should().Equal(flushed);
        await using var reopened = await backend.Open(PathA);
        reopened.Length.Should().Be(32);
        var buffer = new byte[32];
        await reopened.ReadExact(0, buffer);
        buffer.Should().Equal(flushed);
    }

    [Fact]
    public async Task ProcessKillLosesNothing()
    {
        var backend = new FakeStorageBackend();
        var flushed = MakeBytes(32, 4);
        var unflushed = MakeBytes(32, 5);
        await using (var fileA = await backend.Open(PathA)) {
            await fileA.Write(0, flushed);
            await fileA.FlushToDisk();
            await fileA.Write(32, unflushed);
        }
        await using (var fileB = await backend.Open(PathB))
            await fileB.Write(0, unflushed);
        backend.ProcessKill();

        backend.GetStableBytes(PathA).Should().Equal(flushed.Concat(unflushed));
        backend.GetStableBytes(PathB).Should().Equal(unflushed);
        await using var reopened = await backend.Open(PathA);
        reopened.Length.Should().Be(64);
    }

    [Fact]
    public async Task CrashAppliesToEveryFile()
    {
        var backend = new FakeStorageBackend();
        await using (var fileA = await backend.Open(PathA)) {
            await fileA.Write(0, MakeBytes(16, 6));
            await fileA.FlushToDisk();
            await fileA.Write(16, MakeBytes(16, 7));
        }
        await using (var fileB = await backend.Open(PathB))
            await fileB.Write(0, MakeBytes(16, 8));
        backend.Crash(CrashMode.LoseAll);

        backend.GetStableBytes(PathA).Should().HaveCount(16);
        backend.GetStableBytes(PathB).Should().BeEmpty();
    }

    [Fact]
    public async Task TornWritesLandAsPrefixesOnly()
    {
        const int stableLength = 128;
        const int writeLength = 64;
        var sawFull = false;
        var sawPartial = false;
        for (var seed = 0; seed < 100; seed++) {
            var backend = new FakeStorageBackend();
            await using (var file = await backend.Open(PathA)) {
                await file.Write(0, new byte[stableLength]);
                await file.FlushToDisk();
                await file.Write(0, Enumerable.Repeat((byte)0xFF, writeLength).ToArray());
            }
            backend.Crash(CrashMode.Torn, seed);

            var bytes = backend.GetStableBytes(PathA);
            bytes.Should().HaveCount(stableLength);
            var applied = 0;
            while (applied < writeLength && bytes[applied] == 0xFF)
                applied++;
            // Everything after the applied prefix must still be the pre-crash bytes: no interleaving.
            bytes.AsSpan(applied).ToArray().Should().OnlyContain(x => x == 0);
            sawFull |= applied == writeLength;
            sawPartial |= applied is > 0 and < writeLength;
        }
        sawFull.Should().BeTrue();
        sawPartial.Should().BeTrue();
    }

    [Fact]
    public async Task ReorderKeepsWholeWritesAndActuallyReorders()
    {
        var winners = new HashSet<byte>();
        for (var seed = 0; seed < 200; seed++) {
            var backend = new FakeStorageBackend();
            await using (var file = await backend.Open(PathA)) {
                await file.Write(0, new byte[8]);
                await file.FlushToDisk();
                await file.Write(0, new byte[] { 0xAA, 0xAA, 0xAA, 0xAA });
                await file.Write(0, new byte[] { 0xBB, 0xBB, 0xBB, 0xBB });
            }
            backend.Crash(CrashMode.Reorder, seed);

            var bytes = backend.GetStableBytes(PathA);
            bytes.Should().HaveCount(8);
            bytes.AsSpan(0, 4).ToArray().Should().OnlyContain(x => x == bytes[0]);
            winners.Add(bytes[0]);
        }
        winners.Should().Contain((byte)0xAA);
        winners.Should().Contain((byte)0xBB);
    }

    [Theory]
    [InlineData(CrashMode.LoseAll)]
    [InlineData(CrashMode.Torn)]
    [InlineData(CrashMode.Reorder)]
    public async Task SameSeedReproducesTheSameOutcome(CrashMode mode)
    {
        var first = await Run();
        var second = await Run();
        second.Should().Equal(first);
        return;

        async Task<byte[]> Run() {
            var backend = new FakeStorageBackend();
            await using (var file = await backend.Open(PathA)) {
                await file.Write(0, MakeBytes(96, 9));
                await file.FlushToDisk();
                for (var i = 0; i < 8; i++)
                    await file.Write(i * 12, MakeBytes(12, 100 + i));
                await file.Truncate(64);
                await file.Write(64, MakeBytes(16, 200));
            }
            backend.Crash(mode, 12345);
            return backend.GetStableBytes(PathA);
        }
    }

    [Fact]
    public async Task TruncateIsVolatileUntilFlush()
    {
        var backend = new FakeStorageBackend();
        var data = MakeBytes(100, 10);
        await using (var file = await backend.Open(PathA)) {
            await file.Write(0, data);
            await file.FlushToDisk();

            await file.Truncate(40);
            file.Length.Should().Be(40);
            (await file.Read(40, new byte[8])).Should().Be(0);
            backend.GetStableBytes(PathA).Should().HaveCount(100);
        }
        backend.Crash(CrashMode.LoseAll);

        backend.GetStableBytes(PathA).Should().Equal(data);
        await using var reopened = await backend.Open(PathA);
        reopened.Length.Should().Be(100);
    }

    [Fact]
    public async Task TruncateThenFlushIsStableAndRegrowsWithZeros()
    {
        var backend = new FakeStorageBackend();
        await using var file = await backend.Open(PathA);
        await file.Write(0, MakeBytes(100, 11));
        await file.Truncate(40);
        await file.Truncate(80);
        await file.FlushToDisk();

        file.Length.Should().Be(80);
        var tail = new byte[40];
        await file.ReadExact(40, tail);
        tail.Should().OnlyContain(x => x == 0);
        backend.GetStableBytes(PathA).Should().HaveCount(80);
    }

    [Fact]
    public async Task ExistsDeleteAndListFilesWork()
    {
        var backend = new FakeStorageBackend();
        backend.Exists(PathA).Should().BeFalse();
        await using (var file = await backend.Open(PathA))
            await file.Write(0, MakeBytes(8, 12));
        await using (var file = await backend.Open(PathB))
            await file.Write(0, MakeBytes(8, 13));
        await using (var file = await backend.Open(PathAIndex))
            await file.Write(0, MakeBytes(8, 14));

        backend.Exists(PathA).Should().BeTrue();
        backend.ListFiles(Dir, "*.kdat").Should().Equal(PathA, PathB);
        backend.ListFiles(Dir, "a.*").Should().Equal(PathA, PathAIndex);
        backend.ListFiles("other", "*").Should().BeEmpty();

        backend.Delete(PathA);
        backend.Exists(PathA).Should().BeFalse();
        backend.ListFiles(Dir, "*.kdat").Should().Equal(PathB);
    }

    [Fact]
    public async Task ConcurrentReadersAndOneWriterAreSafe()
    {
        const int length = 4096;
        var backend = new FakeStorageBackend();
        await using var file = await backend.Open(PathA);
        await file.Write(0, new byte[length]);

        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => {
            var buffer = new byte[64];
            while (!stop.IsCancellationRequested) {
                await file.Read(0, buffer);
                await Task.Yield();
            }
        })).ToArray();
        for (var i = 0; i < 500; i++)
            await file.Write(i % (length - 64), MakeBytes(64, i));
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        file.Length.Should().Be(length);
    }

    private static byte[] MakeBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
