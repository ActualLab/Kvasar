using System.Collections.Concurrent;
using System.Text;
using ActualLab.Kvasar;
using SQLite;

namespace ActualLab.Kvasar.Benchmarks;

/// <summary>A key-value engine under test. Keys/values are raw bytes; the same data feeds every engine.</summary>
public interface IKvEngine : IAsyncDisposable
{
    public string Name { get; }
    public ValueTask Open();                              // open or create the store at BasePath
    public ValueTask Close();                             // release handles (so we can reopen & time it)
    public ValueTask WriteBatch(IReadOnlyList<(byte[] Key, byte[] Value)> batch);
    public ValueTask FlushDurable();                      // persist everything (fsync / checkpoint)
    public ValueTask<byte[]?> Get(byte[] key);
    public ValueTask<long> ScanAll();                     // enumerate ALL entries (the cache's startup hydration); returns count
    public long FileBytes { get; }
    public string? IndexHintFile { get; }                 // .kidx path for Kvasar (deletable to force rebuild); null otherwise
}

public sealed class KvasarEngine(string basePath, byte[] key, bool encrypt, int pageSize = 4096) : IKvEngine
{
    private KvasarStore _store = null!;

    public string Name { get; } =
        (encrypt ? "Kvasar (AES-GCM)" : "Kvasar (no-enc)") + (pageSize == 4096 ? "" : $" p{pageSize / 1024}K");

    public async ValueTask Open()
        => _store = await KvasarStore.Open(new KvasarOptions {
            BasePath = basePath,
            EncryptionKey = key,
            DisableEncryption = !encrypt,
            PageSize = pageSize,
            PageCacheBytes = 64L * 1024 * 1024,
            SegmentBytes = 16 * 1024 * 1024,
        });

    public ValueTask Close()
        => _store.DisposeAsync();

    public ValueTask WriteBatch(IReadOnlyList<(byte[] Key, byte[] Value)> batch)
    {
        var updates = new (ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte>? Value)[batch.Count];
        for (var i = 0; i < batch.Count; i++)
            updates[i] = (batch[i].Key, batch[i].Value);
        return _store.SetMany(updates);
    }

    public ValueTask FlushDurable() => _store.Flush(true);

    public async ValueTask<byte[]?> Get(byte[] key)
    {
        var v = await _store.Get(key);
        return v?.ToArray();
    }

    public async ValueTask<long> ScanAll()
    {
        long n = 0, sum = 0;
        await foreach (var (_, v) in _store.Scan()) {
            n++;
            sum += v.Length != 0 ? v.Span[0] : 0; // touch the value bytes so the scan isn't elided
        }
        return n + (sum & 0);
    }

    public long FileBytes => _store.Stats.FileBytes;
    public string? IndexHintFile => basePath + ".kidx";

    public ValueTask DisposeAsync() => Close();
}

// sqlite-net is synchronous by design, so the baseline stays synchronous: every method below does the
// real work inline and returns an already-completed ValueTask. Wrapping it into Task.Run would only add
// thread-pool overhead and misrepresent SQLite's actual cost.

/// <summary>
/// SQLCipher baseline that mirrors ActualChat's <c>SQLiteBatchingKvasBackend</c>: encrypted
/// <c>items(Key TEXT PK, Value BLOB)</c>, WAL, <c>synchronous=normal</c>, upsert via <c>insert or replace</c>,
/// batched writes in a transaction. Each calling thread gets its own connection for concurrent reads.
/// </summary>
public sealed class SqliteEngine(string dbPath, byte[] key) : IKvEngine
{
    private const SQLiteOpenFlags Flags = SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.NoMutex;

    private readonly ConcurrentBag<SQLiteConnection> _all = new();
    private SQLiteConnection _writer = null!;
    private ThreadLocal<SQLiteConnection> _readers = null!;

    public string Name => "SQLCipher (sqlite-net)";

    public ValueTask Open()
    {
        _readers = new ThreadLocal<SQLiteConnection>(NewConnection, trackAllValues: false);
        _writer = NewConnection();
        return default;
    }

    public ValueTask Close()
    {
        foreach (var c in _all) {
            try { c.Close(); } catch { /* ignore */ }
        }
        _all.Clear();
        _readers?.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return default;
    }

    public ValueTask WriteBatch(IReadOnlyList<(byte[] Key, byte[] Value)> batch)
    {
        _writer.RunInTransaction(() => {
            foreach (var (k, v) in batch)
                _writer.Execute("insert or replace into items (Key, Value) values (?, ?)", Encoding.UTF8.GetString(k), v);
        });
        return default;
    }

    public ValueTask FlushDurable()
    {
        _writer.ExecuteScalar<string>("PRAGMA wal_checkpoint(TRUNCATE)");
        return default;
    }

    public ValueTask<byte[]?> Get(byte[] key)
    {
        var v = _readers.Value!.ExecuteScalar<byte[]>("select Value from items where Key = ?", Encoding.UTF8.GetString(key));
        return new ValueTask<byte[]?>(v);
    }

    public ValueTask<long> ScanAll()
    {
        long n = 0, sum = 0;
        foreach (var row in _writer.Query<KvRow>("select Key, Value from items")) {
            n++;
            sum += row.Value is { Length: > 0 } b ? b[0] : 0;
        }
        return new ValueTask<long>(n + (sum & 0));
    }

    public sealed class KvRow
    {
        public string Key { get; set; } = "";
        public byte[]? Value { get; set; }
    }

    public long FileBytes {
        get {
            long sum = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" }) {
                var f = dbPath + suffix;
                if (File.Exists(f))
                    sum += new FileInfo(f).Length;
            }
            return sum;
        }
    }

    public string? IndexHintFile => null;

    public ValueTask DisposeAsync() => Close();

    private SQLiteConnection NewConnection()
    {
        var cs = new SQLiteConnectionString(dbPath, Flags, storeDateTimeAsTicks: true, key: key);
        var c = new SQLiteConnection(cs);
        c.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
        c.ExecuteScalar<string>("PRAGMA synchronous=normal");
        c.Execute("create table if not exists items (Key text primary key, Value blob)");
        _all.Add(c);
        return c;
    }
}
