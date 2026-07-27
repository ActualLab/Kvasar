using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ActualLab.Kvasar;
using System.Text.Json.Serialization;
using SQLite;

namespace ActualLab.Kvasar.Benchmarks;

[JsonSerializable(typeof(string[]))]
internal sealed partial class BenchJsonContext : JsonSerializerContext;

/// <summary>A key-value engine under test. Keys/values are raw bytes; the same data feeds every engine.</summary>
public interface IKvEngine : IAsyncDisposable
{
    public string Name { get; }
    public ValueTask Open();                              // open or create the store at BasePath
    public ValueTask Close();                             // release handles (so we can reopen & time it)
    public ValueTask WriteBatch(IReadOnlyList<(byte[] Key, byte[] Value)> batch);
    public ValueTask WriteOne(byte[] key, byte[] value);  // single-record write, no batching layer
    public ValueTask FlushDurable();                      // persist everything (fsync / checkpoint)
    public ValueTask<byte[]?> Get(byte[] key);
    public ValueTask<byte[]?[]> GetMany(IReadOnlyList<byte[]> keys); // one batched backend call, results by index
    public ValueTask<long> ScanAll();                     // enumerate ALL entries (the cache's startup hydration); returns count
    public long FileBytes { get; }
    public string[] IndexHintFiles { get; }               // .kidx paths for Kvasar (deletable to force rebuild); empty otherwise
}

public sealed class KvasarEngine(
    string basePath,
    byte[] key,
    bool encrypt,
    int pageSize = 4096,
    long pageCacheBytes = 64L * 1024 * 1024,
    TimeSpan? flushDelay = null,
    KvasarDurability durability = KvasarDurability.Flushed
) : IKvEngine
{
    private KvasarStore _store = null!;

    public string Name { get; } =
        (encrypt ? "Kvasar (AES-GCM)" : "Kvasar (no-enc)")
            + (pageSize == 4096 ? "" : $" p{pageSize / 1024}K")
            + (durability == KvasarDurability.Flushed ? "" : $" {durability}");

    public async ValueTask Open()
    {
        var options = new KvasarOptions {
            BasePath = basePath,
            EncryptionKey = key,
            DisableEncryption = !encrypt,
            PageSize = pageSize,
            PageCacheBytes = pageCacheBytes,
            Durability = durability,
        };
        if (flushDelay is { } d)
            options = options with { FlushDelay = d };
        _store = await KvasarStore.Open(options);
    }

    public ValueTask Close()
        => _store.DisposeAsync();

    public ValueTask WriteBatch(IReadOnlyList<(byte[] Key, byte[] Value)> batch)
    {
        var updates = new (KvasarKey Key, KvasarValue? Value)[batch.Count];
        for (var i = 0; i < batch.Count; i++)
            updates[i] = (batch[i].Key, batch[i].Value);
        return _store.SetMany(updates);
    }

    public ValueTask WriteOne(byte[] key, byte[] value) => _store.Set(key, value);
    public ValueTask FlushDurable() => _store.Flush();

    public async ValueTask<byte[]?> Get(byte[] key)
    {
        var v = await _store.Get(key);
        return v?.ToArray();
    }

    public async ValueTask<byte[]?[]> GetMany(IReadOnlyList<byte[]> keys)
    {
        var kvasarKeys = new KvasarKey[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            kvasarKeys[i] = keys[i];
        var values = await _store.GetMany(kvasarKeys);
        var result = new byte[]?[values.Length];
        for (var i = 0; i < values.Length; i++)
            result[i] = values[i]?.ToArray();
        return result;
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
    public string[] IndexHintFiles => [basePath + ".0.kidx", basePath + ".1.kidx"];

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

    private const string FindManySql = "select Key, Value from items where Key in (select e.value from json_each(?) e)";

    private readonly ConcurrentBag<SQLiteConnection> _all = new();
    private readonly ConcurrentBag<SQLiteConnection> _pool = new();
    private readonly Lock _writeLock = new();
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
        _pool.Clear();
        _readers?.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return default;
    }

    public ValueTask WriteBatch(IReadOnlyList<(byte[] Key, byte[] Value)> batch)
    {
        // The connection is opened with NoMutex, so the writer connection is guarded here instead.
        using (_writeLock.EnterScope())
            _writer.RunInTransaction(() => {
                foreach (var (k, v) in batch)
                    _writer.Execute("insert or replace into items (Key, Value) values (?, ?)",
                        Encoding.UTF8.GetString(k), v);
            });
        return default;
    }

    public ValueTask WriteOne(byte[] key, byte[] value)
    {
        using (_writeLock.EnterScope())
            _writer.Execute("insert or replace into items (Key, Value) values (?, ?)",
                Encoding.UTF8.GetString(key), value);
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

    public ValueTask<byte[]?[]> GetMany(IReadOnlyList<byte[]> keys)
    {
        // Mirrors ActualChat's DbHelpers.FindMany: one batched query per call, keys passed as a JSON array,
        // and a connection rented per reader worker.
        var result = new byte[]?[keys.Count];
        if (keys.Count == 0)
            return new ValueTask<byte[]?[]>(result);

        var texts = new string[keys.Count];
        var indexes = new Dictionary<string, int>(keys.Count, StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++) {
            texts[i] = Encoding.UTF8.GetString(keys[i]);
            indexes[texts[i]] = i;
        }
        var connection = _pool.TryTake(out var c) ? c : NewConnection();
        try {
            var keysJson = JsonSerializer.Serialize(texts, BenchJsonContext.Default.StringArray);
            foreach (var row in connection.Query<KvRow>(FindManySql, keysJson))
                if (indexes.TryGetValue(row.Key, out var i))
                    result[i] = row.Value;
        }
        finally {
            _pool.Add(connection);
        }
        return new ValueTask<byte[]?[]>(result);
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

    public string[] IndexHintFiles => [];

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
