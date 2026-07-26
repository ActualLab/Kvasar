using System.IO;
using System.Text;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Store;

public class LockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-lock-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public LockTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i + 3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private KvasarOptions Options() => new() {
        BasePath = Path.Combine(_dir, "store"),
        EncryptionKey = _key,
    };

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task SecondOpenThrowsLockException_AndDoesNotWipeData()
    {
        await using var first = await KvasarStore.Open(Options());
        await first.Set(K("keep"), K("me"));
        await first.Flush(true);

        // A second opener must fail with a lock exception — NOT wipe the live store's data.
        Func<Task> open = async () => await KvasarStore.Open(Options());
        await open.Should().ThrowAsync<KvasarLockException>();

        // The first store is untouched.
        (await first.Get(K("keep")))!.Value.ToArray().Should().Equal(K("me"));
    }

    [Fact]
    public void ContendedStoreLockThrowsLockException()
    {
        var path = Path.Combine(_dir, "contended.lock");
        using var first = new StoreLock(path);

        var second = () => new StoreLock(path);
        second.Should().Throw<KvasarLockException>();
    }

    [Fact]
    public void NonContentionIOFailureIsNotALockException()
    {
        // An over-long path component fails the open with an I/O error that has nothing to do with locking.
        var path = Path.Combine(_dir, new string('n', 300) + ".lock");

        var open = () => new StoreLock(path);
        open.Should().Throw<IOException>().Which.Should().NotBeOfType<KvasarLockException>();
    }

    [Fact]
    public async Task LockReleasedAfterDisposeAllowsReopen()
    {
        await using (var s = await KvasarStore.Open(Options())) {
            await s.Set(K("a"), K("1"));
        }
        // Lock released ⇒ reopen succeeds and data survived.
        await using var s2 = await KvasarStore.Open(Options());
        (await s2.Get(K("a")))!.Value.ToArray().Should().Equal(K("1"));
    }
}
