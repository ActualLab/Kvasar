using System.Threading.Channels;

namespace ActualLab.Kvasar.Benchmarks;

/// <summary>A cache key in both forms the harness needs: text for the read cache, UTF-8 for the backend.</summary>
public sealed record KvKey(string Text, byte[] Bytes);

/// <summary>
/// A minimal port of ActualChat's <c>BatchingKvas</c>: a 256-entry LRU read cache, a batching reader
/// (batches of up to 64 keys served by up to 4 workers), and one lazy writer (250 ms / 64 items).
/// With <c>batchedReads: false</c> the whole read path is bypassed and app threads hit the backend directly.
/// </summary>
public sealed class BatchingKvasHarness : IAsyncDisposable
{
    public const int ReadCacheCapacity = 256;
    public const int ReaderBatchSize = 64;
    public const int ReaderWorkerCount = 4;
    public const int FlushMaxItemCount = 64;
    public static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(0.25);

    private readonly IKvEngine _engine;
    private readonly bool _batchedReads;
    private readonly LruCache _readCache = new(ReadCacheCapacity);
    private readonly Channel<ReadItem> _reads;
    private readonly Channel<WriteCommand> _writes;
    private readonly Task[] _readers;
    private readonly Task _writer;
    private long _cacheHits;
    private long _cacheMisses;
    private long _getManyCalls;
    private long _getManyKeys;
    private long _setManyCalls;
    private long _setManyKeys;

    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long GetManyCalls => Interlocked.Read(ref _getManyCalls);
    public long GetManyKeys => Interlocked.Read(ref _getManyKeys);
    public long SetManyCalls => Interlocked.Read(ref _setManyCalls);
    public long SetManyKeys => Interlocked.Read(ref _setManyKeys);

    public BatchingKvasHarness(IKvEngine engine, bool batchedReads = true)
    {
        _engine = engine;
        _batchedReads = batchedReads;
        _reads = Channel.CreateUnbounded<ReadItem>(new UnboundedChannelOptions {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = true,
        });
        _writes = Channel.CreateUnbounded<WriteCommand>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        });
        // BatchProcessor grows its worker pool on demand up to MaxWorkerCount; starting all 4 up front is
        // both deterministic and the conservative choice — idle workers pick items up sooner, so batches
        // are smaller than the real pool would produce, never larger.
        _readers = new Task[batchedReads ? ReaderWorkerCount : 0];
        for (var i = 0; i < _readers.Length; i++)
            _readers[i] = Task.Run(RunReader);
        _writer = Task.Run(RunWriter);
    }

    public ValueTask<byte[]?> Get(KvKey key)
    {
        if (!_batchedReads) {
            Interlocked.Increment(ref _cacheMisses);
            Interlocked.Increment(ref _getManyCalls);
            Interlocked.Increment(ref _getManyKeys);
            return _engine.Get(key.Bytes);
        }
        if (_readCache.TryGetValue(key.Text, out var cached)) {
            Interlocked.Increment(ref _cacheHits);
            return new ValueTask<byte[]?>(cached);
        }
        Interlocked.Increment(ref _cacheMisses);
        var item = new ReadItem(key);
        if (!_reads.Writer.TryWrite(item))
            throw new InvalidOperationException("The harness is already disposed.");
        return new ValueTask<byte[]?>(item.Source.Task);
    }

    public void Set(KvKey key, byte[]? value)
    {
        // Writes stay batched even when reads are direct: this asymmetry is deliberate. A single-record
        // Set seals the tail page, so per-record writes cost roughly a page write each, while a 64-item
        // SetMany amortizes that away — batching is a genuine win for Kvasar writes, unlike for its reads.
        if (_batchedReads)
            _readCache.Set(key.Text, value);
        if (!_writes.Writer.TryWrite(new WriteCommand((key, value), null)))
            throw new InvalidOperationException("The harness is already disposed.");
    }

    public Task Flush()
    {
        var whenFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writes.Writer.TryWrite(new WriteCommand(null, whenFlushed)))
            throw new InvalidOperationException("The harness is already disposed.");
        return whenFlushed.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _reads.Writer.TryComplete();
        _writes.Writer.TryComplete();
        await Task.WhenAll(_readers).ConfigureAwait(false);
        await _writer.ConfigureAwait(false);
    }

    // Private methods

    private async Task RunReader()
    {
        var reader = _reads.Reader;
        var batch = new List<ReadItem>(ReaderBatchSize);
        while (await reader.WaitToReadAsync().ConfigureAwait(false)) {
            while (reader.TryRead(out var item)) {
                batch.Add(item);
                if (batch.Count >= ReaderBatchSize)
                    await ReadBatch(batch).ConfigureAwait(false);
            }
            await ReadBatch(batch).ConfigureAwait(false);
        }
    }

    private async Task ReadBatch(List<ReadItem> batch)
    {
        if (batch.Count == 0)
            return;

        var keys = new byte[batch.Count][];
        for (var i = 0; i < batch.Count; i++)
            keys[i] = batch[i].Key.Bytes;
        Interlocked.Increment(ref _getManyCalls);
        Interlocked.Add(ref _getManyKeys, keys.Length);
        try {
            var values = await _engine.GetMany(keys).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
                batch[i].Source.TrySetResult(values[i]);
        }
        catch (Exception e) {
            foreach (var item in batch)
                item.Source.TrySetException(e);
        }
        batch.Clear();
    }

    private async Task RunWriter()
    {
        var batch = new List<(byte[] Key, byte[] Value)>(FlushMaxItemCount);
        CancellationTokenSource? delayCts = null;
        await foreach (var command in _writes.Reader.ReadAllAsync().ConfigureAwait(false)) {
            if (command.Item is { } item) {
                batch.Add((item.Key.Bytes, item.Value ?? []));
                if (batch.Count >= FlushMaxItemCount)
                    await WriteBatch().ConfigureAwait(false);
                else if (delayCts == null) {
                    delayCts = new CancellationTokenSource();
                    var delayToken = delayCts.Token;
                    _ = Task.Delay(FlushDelay, delayToken).ContinueWith(
                        t => {
                            if (!t.IsCanceled)
                                _writes.Writer.TryWrite(new WriteCommand(null, null));
                        },
                        TaskScheduler.Default);
                }
            }
            else {
                try {
                    await WriteBatch().ConfigureAwait(false);
                    command.WhenFlushed?.TrySetResult();
                }
                catch (Exception e) {
                    command.WhenFlushed?.TrySetException(e);
                }
            }
        }
        delayCts?.Cancel();
        delayCts?.Dispose();
        return;

        async Task WriteBatch() {
            delayCts?.Cancel();
            delayCts?.Dispose();
            delayCts = null;
            if (batch.Count == 0)
                return;

            Interlocked.Increment(ref _setManyCalls);
            Interlocked.Add(ref _setManyKeys, batch.Count);
            await _engine.WriteBatch(batch).ConfigureAwait(false);
            batch.Clear();
        }
    }

    // Nested types

    private sealed record WriteCommand((KvKey Key, byte[]? Value)? Item, TaskCompletionSource? WhenFlushed);

    private sealed class ReadItem(KvKey key)
    {
        public readonly KvKey Key = key;
        public readonly TaskCompletionSource<byte[]?> Source = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A lock-guarded exact LRU — the harness equivalent of ActualChat's ThreadSafeLruCache.</summary>
    private sealed class LruCache(int capacity)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, byte[]?>>> _map
            = new(capacity, StringComparer.Ordinal);
        private readonly LinkedList<KeyValuePair<string, byte[]?>> _list = new();

        public bool TryGetValue(string key, out byte[]? value)
        {
            using var _ = _lock.EnterScope();
            if (!_map.TryGetValue(key, out var node)) {
                value = null;
                return false;
            }
            _list.Remove(node);
            _list.AddFirst(node);
            value = node.Value.Value;
            return true;
        }

        public void Set(string key, byte[]? value)
        {
            using var _ = _lock.EnterScope();
            var pair = KeyValuePair.Create(key, value);
            if (_map.TryGetValue(key, out var node)) {
                node.Value = pair;
                _list.Remove(node);
                _list.AddFirst(node);
                return;
            }
            _map.Add(key, _list.AddFirst(pair));
            while (_list.Count > capacity) {
                var last = _list.Last!;
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
