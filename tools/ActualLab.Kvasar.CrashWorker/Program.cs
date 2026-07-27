using System.Text;
using ActualLab.Kvasar;

// Writes records forever so a parent test can kill the process at an arbitrary point mid-write —
// the one thing an in-process test cannot reproduce, because it also leaves whatever the OS was
// midway through writing. Every "w:<i>" line means Set(i) returned, so that record's bytes reached
// the OS and must survive the kill; the parent asserts exactly those come back.
if (args.Length < 1) {
    await Console.Error.WriteLineAsync("usage: CrashWorker <basePath> [valueSize] [pageSize]").ConfigureAwait(false);
    return 2;
}

var basePath = args[0];
var valueSize = args.Length > 1 ? int.Parse(args[1]) : 200;
var pageSize = args.Length > 2 ? int.Parse(args[2]) : 4096;

var key = new byte[32];
for (var i = 0; i < 32; i++)
    key[i] = (byte)(i * 7 + 3);

var store = await KvasarStore.Open(new KvasarOptions {
    BasePath = basePath,
    EncryptionKey = key,
    PageSize = pageSize,
    PageCacheBytes = 4L * 1024 * 1024,
    // Durable mode: the parent test asserts that every acknowledged write survives the kill, which only
    // holds when Set is durable before it returns. Deferred flushing is covered by FlushDelayTests.
    FlushDelay = TimeSpan.Zero,
}).ConfigureAwait(false);

await using (store.ConfigureAwait(false)) {
    for (var i = 0; ; i++) {
        await store.Set(Encoding.UTF8.GetBytes($"key-{i:D8}"), CrashWorkerData.Value(i, valueSize))
            .ConfigureAwait(false);
        Console.Out.WriteLine($"w:{i}");
        Console.Out.Flush();
    }
}

// Shared with the test so both sides agree on what a record's payload should be.
internal static class CrashWorkerData
{
    public static byte[] Value(int index, int size)
    {
        var value = new byte[size];
        for (var i = 0; i < size; i++)
            value[i] = (byte)(index + i);
        return value;
    }
}
