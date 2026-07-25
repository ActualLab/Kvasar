namespace ActualLab.Kvasar.Benchmarks;

/// <summary>A cache key in both forms the harness needs: text for the read cache, UTF-8 for the backend.</summary>
public sealed record KvKey(string Text, byte[] Bytes);

/// <summary>
/// The store API the cold-start scenario drives, plus the counters the result table reports.
/// Implemented by <see cref="BatchingKvasHarness"/> and by <see cref="PlainKvas"/>.
/// </summary>
public interface IKvas : IAsyncDisposable
{
    public long CacheHits { get; }
    public long CacheMisses { get; }
    public long GetManyCalls { get; }
    public long GetManyKeys { get; }
    public long SetManyCalls { get; }
    public long SetManyKeys { get; }

    public ValueTask<byte[]?> Get(KvKey key);
    public ValueTask Set(KvKey key, byte[]? value);
    public Task Flush();
}

/// <summary>
/// No layer above the engine: app threads call <c>Get</c>/<c>Set</c> on it directly, with no read cache,
/// no read batching and no external writer — write debouncing is the engine's own job.
/// </summary>
public sealed class PlainKvas(IKvEngine engine) : IKvas
{
    private long _gets;
    private long _sets;

    public long CacheHits => 0;
    public long CacheMisses => Interlocked.Read(ref _gets);
    public long GetManyCalls => Interlocked.Read(ref _gets);
    public long GetManyKeys => Interlocked.Read(ref _gets);
    public long SetManyCalls => Interlocked.Read(ref _sets);
    public long SetManyKeys => Interlocked.Read(ref _sets);

    public ValueTask<byte[]?> Get(KvKey key)
    {
        Interlocked.Increment(ref _gets);
        return engine.Get(key.Bytes);
    }

    public ValueTask Set(KvKey key, byte[]? value)
    {
        Interlocked.Increment(ref _sets);
        return engine.WriteOne(key.Bytes, value ?? []);
    }

    public Task Flush() => Task.CompletedTask;
    public ValueTask DisposeAsync() => default;
}
