using System.IO;
using System.Linq;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Storage;

public class FileStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-file-" + Guid.NewGuid().ToString("N"));
    private readonly FileStorageBackend _backend = new();

    public FileStorageTests()
        => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task OpenCreatesFileAndRoundTripsAtOffsets()
    {
        var path = NewPath();
        _backend.Exists(path).Should().BeFalse();

        var data = MakeBytes(64, 1);
        await using var file = await _backend.Open(path);
        _backend.Exists(path).Should().BeTrue();
        file.Length.Should().Be(0);

        await file.Write(0, data);
        file.Length.Should().Be(64);
        await file.Write(128, data);
        file.Length.Should().Be(192);

        var buffer = new byte[64];
        (await file.Read(0, buffer)).Should().Be(64);
        buffer.Should().Equal(data);
        (await file.Read(128, buffer)).Should().Be(64);
        buffer.Should().Equal(data);

        // The gap between the two writes must read back as zeros.
        var gap = new byte[64];
        await file.ReadExact(64, gap);
        gap.Should().OnlyContain(x => x == 0);
    }

    [Fact]
    public async Task ReadPastEndIsShortAndReadExactThrows()
    {
        var path = NewPath();
        await using var file = await _backend.Open(path);
        await file.Write(0, MakeBytes(16, 2));

        var buffer = new byte[8];
        (await file.Read(16, buffer)).Should().Be(0);
        (await file.Read(12, buffer)).Should().Be(4);

        var act = async () => await file.ReadExact(12, new byte[8]);
        await act.Should().ThrowAsync<KvasarCorruptException>();
    }

    [Fact]
    public async Task TruncateShrinksAndGrows()
    {
        var path = NewPath();
        await using var file = await _backend.Open(path);
        await file.Write(0, MakeBytes(100, 3));
        file.Length.Should().Be(100);

        await file.Truncate(40);
        file.Length.Should().Be(40);
        (await file.Read(40, new byte[8])).Should().Be(0);

        await file.Truncate(80);
        file.Length.Should().Be(80);
        var tail = new byte[40];
        await file.ReadExact(40, tail);
        tail.Should().OnlyContain(x => x == 0);
    }

    [Fact]
    public async Task FlushToDiskPersistsAcrossReopen()
    {
        var path = NewPath();
        var data = MakeBytes(256, 4);
        await using (var file = await _backend.Open(path)) {
            await file.Write(0, data);
            await file.FlushToDisk();
        }

        await using var reopened = await _backend.Open(path);
        reopened.Length.Should().Be(256);
        var buffer = new byte[256];
        await reopened.ReadExact(0, buffer);
        buffer.Should().Equal(data);
    }

    [Fact]
    public async Task ConcurrentReadsSeeTheSameBytes()
    {
        const int length = 64 * 1024;
        var path = NewPath();
        var data = MakeBytes(length, 5);
        await using var file = await _backend.Open(path);
        await file.Write(0, data);
        await file.FlushToDisk();

        var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () => {
            var random = new Random(i);
            for (var j = 0; j < 50; j++) {
                var offset = random.Next(length - 1024);
                var buffer = new byte[1024];
                await file.ReadExact(offset, buffer);
                buffer.Should().Equal(data.AsSpan(offset, 1024).ToArray());
            }
        })).ToArray();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task FileCanBeDeletedWhileHandleIsOpen()
    {
        // FileShare.Delete: on Windows, deleting a file with an open handle fails without it (I20).
        var path = NewPath();
        var file = await _backend.Open(path);
        await file.Write(0, MakeBytes(32, 6));
        await file.FlushToDisk();

        var act = () => _backend.Delete(path);
        act.Should().NotThrow();

        await file.DisposeAsync();
        _backend.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task ListFilesMatchesThePattern()
    {
        foreach (var name in new[] { "store.0.kdat", "store.1.kdat", "store.0.kidx" }) {
            await using var file = await _backend.Open(Path.Combine(_dir, name));
            await file.Write(0, MakeBytes(8, 7));
        }

        var kdat = _backend.ListFiles(_dir, "*.kdat");
        kdat.Should().HaveCount(2);
        kdat.Should().OnlyContain(x => x.EndsWith(".kdat", StringComparison.Ordinal));
        _backend.ListFiles(_dir, "*.kidx").Should().HaveCount(1);
        _backend.ListFiles(Path.Combine(_dir, "missing"), "*").Should().BeEmpty();
    }

    private string NewPath()
        => Path.Combine(_dir, $"{Guid.NewGuid():N}.bin");

    private static byte[] MakeBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
