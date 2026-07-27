using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Hashing;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Seeded per-owner invariants across reads, writes, deletes, scans, and compaction.
/// Set KVASAR_CONCURRENCY_DEEP=1 for the 30-second, 8-owner/8-reader deep mode.
/// Set KVASAR_CONCURRENCY_SEED to replay with a different base seed.
/// </summary>
[Trait("Category", "Slow")]
public sealed class HighConcurrencyInvariantTests : IDisposable
{
    private sealed record RunSettings(
        int OwnerCount,
        int ReaderCount,
        int KeysPerOwner,
        TimeSpan Duration,
        int BaseSeed);

    private const int PageSize = 512;
    private const int HeaderSize = 28;
    private const int ManifestEntrySize = 12;
    private const int ManifestInterval = 8;
    private const int DefaultSeed = 0x4B56_4153;
    private const int MaxFailuresPerInvariant = 4;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kvasar-invariants-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _encryptionKey = new byte[32];
    private readonly ITestOutputHelper _output;

    public HighConcurrencyInvariantTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
        for (var i = 0; i < _encryptionKey.Length; i++)
            _encryptionKey[i] = unchecked((byte)(i * 17 + 11));
    }

    public void Dispose()
    {
        try {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OwnedKeyInvariantsHoldUnderConcurrentReadsWritesAndCompaction()
    {
        var deepMode = Environment.GetEnvironmentVariable("KVASAR_CONCURRENCY_DEEP");
        var isDeep = string.Equals(deepMode, "1", StringComparison.Ordinal)
            || string.Equals(deepMode, "true", StringComparison.OrdinalIgnoreCase);
        var baseSeed = int.TryParse(
            Environment.GetEnvironmentVariable("KVASAR_CONCURRENCY_SEED"),
            out var configuredSeed)
            ? configuredSeed
            : DefaultSeed;
        var settings = isDeep
            ? new RunSettings(8, 8, 48, TimeSpan.FromSeconds(15), baseSeed)
            : new RunSettings(4, 4, 24, TimeSpan.FromSeconds(5), baseSeed);
        var runFailures = new List<string>();

        foreach (var isEncrypted in new[] { true, false }) {
            var runSeed = DeriveSeed(settings.BaseSeed, isEncrypted ? 1 : 2, 0);
            _output.WriteLine(
                $"START seed={runSeed} mode={ModeName(isEncrypted)} owners={settings.OwnerCount} "
                + $"readers={settings.ReaderCount} keysPerOwner={settings.KeysPerOwner} "
                + $"durationMs={settings.Duration.TotalMilliseconds:F0}");
            try {
                var report = await RunMode(settings, isEncrypted, runSeed);
                runFailures.AddRange(report.Failures);
                _output.WriteLine(report.ResultLine);
            }
            catch (Exception ex) {
                var failure =
                    $"seed={runSeed} mode={ModeName(isEncrypted)} invariant=run owner=-1 key=-1 version=-1 "
                    + $"unexpected run failure: {ex}";
                runFailures.Add(failure);
                _output.WriteLine(failure);
            }
        }

        Assert.True(
            runFailures.Count == 0,
            $"baseSeed={settings.BaseSeed} owner=-1 key=-1 version=-1{Environment.NewLine}"
            + string.Join(Environment.NewLine, runFailures));
    }

    // Private methods

    private async Task<RunReport> RunMode(RunSettings settings, bool isEncrypted, int runSeed)
    {
        var mode = ModeName(isEncrypted);
        var failures = new FailureSink(runSeed, mode);
        var counters = new RunCounters();
        var owners = new OwnerState[settings.OwnerCount];
        for (var ownerId = 0; ownerId < owners.Length; ownerId++)
            owners[ownerId] = new OwnerState(ownerId, settings.KeysPerOwner);
        var keys = BuildKeyCatalog(owners);
        var options = new KvasarOptions {
            BasePath = Path.Combine(_directory, $"store-{mode}"),
            EncryptionKey = _encryptionKey,
            DisableEncryption = !isEncrypted,
            PageSize = PageSize,
            PageCacheBytes = 64 * 1024,
            FlushDelay = TimeSpan.FromMilliseconds(20),
            CommitBytes = 32 * 1024,
            CompactionMinBytes = 8 * 1024,
            CompactionDeadRatio = 0.15,
        };
        var totalClock = Stopwatch.StartNew();
        await using var store = await KvasarStore.Open(options);
        var context = new RunContext(
            store,
            settings,
            owners,
            keys,
            failures,
            counters,
            runSeed);
        await InitializeOwners(context);

        var startSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runClock = new Stopwatch();
        var writerTasks = new List<Task>(settings.OwnerCount);
        for (var ownerId = 0; ownerId < settings.OwnerCount; ownerId++) {
            var owner = owners[ownerId];
            writerTasks.Add(Task.Run(async () => {
                await startSource.Task;
                await RunWriter(context, owner, runClock);
            }));
        }
        var readerTasks = new List<Task>(settings.ReaderCount);
        for (var readerIndex = 0; readerIndex < settings.ReaderCount; readerIndex++) {
            var capturedReaderIndex = readerIndex;
            readerTasks.Add(Task.Run(async () => {
                await startSource.Task;
                await RunReader(context, capturedReaderIndex, runClock);
            }));
        }
        var compactorTask = Task.Run(async () => {
            await startSource.Task;
            await RunCompactor(context, runClock);
        });
        var workerTasks = writerTasks.Concat(readerTasks).Append(compactorTask);
        var allWorkersTask = Task.WhenAll(workerTasks);

        runClock.Start();
        startSource.SetResult();
        await allWorkersTask.WaitAsync(settings.Duration + TimeSpan.FromSeconds(30));
        runClock.Stop();

        await store.Flush();
        await store.Compact();
        await AssertFinalState(context);
        totalClock.Stop();

        var resultLine =
            $"RESULT seed={runSeed} mode={mode} owners={settings.OwnerCount} readers={settings.ReaderCount} "
            + $"keys={settings.OwnerCount * settings.KeysPerOwner} activeMs={runClock.Elapsed.TotalMilliseconds:F0} "
            + $"totalMs={totalClock.Elapsed.TotalMilliseconds:F0} writes={counters.DataWrites} "
            + $"deletes={counters.Deletes} manifests={counters.ManifestWrites} "
            + $"setMany={counters.SetManyCalls} gets={counters.Gets} "
            + $"getMany={counters.GetManyCalls} scans={counters.Scans} "
            + $"compactCalls={counters.CompactCalls} failures={failures.Count}";
        return new RunReport(resultLine, failures.Messages);
    }

    private async Task InitializeOwners(RunContext context)
    {
        foreach (var owner in context.Owners) {
            var random = new Random(DeriveSeed(context.RunSeed, 11, owner.OwnerId));
            for (var keyIndex = 0; keyIndex < owner.KeyCount; keyIndex++)
                await WriteData(context, owner, keyIndex, random);
            await WriteManifest(context, owner, excludedKeyIndex: -1);
        }
    }

    private async Task RunWriter(RunContext context, OwnerState owner, Stopwatch runClock)
    {
        try {
            var random = new Random(DeriveSeed(context.RunSeed, 23, owner.OwnerId));
            var operationsSinceManifest = 0;
            var operationCount = 0;
            while (runClock.Elapsed < context.Settings.Duration) {
                var keyIndex = random.Next(owner.KeyCount);
                var roll = random.Next(100);
                if (roll < 20)
                    await WriteBatch(context, owner, random);
                else if (roll < 38 && owner.IsLive(keyIndex))
                    await DeleteData(context, owner, keyIndex);
                else
                    await WriteData(context, owner, keyIndex, random);

                if (++operationsSinceManifest >= ManifestInterval) {
                    await WriteManifest(context, owner, excludedKeyIndex: -1);
                    operationsSinceManifest = 0;
                }
                if ((++operationCount & 31) == 0)
                    await Task.Yield();
            }
            await WriteManifest(context, owner, excludedKeyIndex: -1);
        }
        catch (Exception ex) {
            context.Failures.Record(
                "writer",
                new KeyIdentity(owner.OwnerId, -1),
                owner.ManifestVersion,
                ex.ToString());
        }
    }

    private async Task RunReader(RunContext context, int readerIndex, Stopwatch runClock)
    {
        var reader = new ReaderState(context, readerIndex);
        try {
            var random = new Random(DeriveSeed(context.RunSeed, 37, readerIndex));
            while (runClock.Elapsed < context.Settings.Duration) {
                var roll = random.Next(100);
                if (roll < 45)
                    await ReadSingle(reader, random);
                else if (roll < 75)
                    await ReadMany(reader, random);
                else
                    await ReadScan(reader);
            }
        }
        catch (Exception ex) {
            context.Failures.Record(
                "reader",
                KeyIdentity.Unknown,
                -1,
                $"reader={readerIndex}: {ex}");
        }
    }

    private async Task RunCompactor(RunContext context, Stopwatch runClock)
    {
        try {
            var random = new Random(DeriveSeed(context.RunSeed, 53, 0));
            while (runClock.Elapsed < context.Settings.Duration) {
                await Task.Delay(random.Next(20, 61));
                if (runClock.Elapsed >= context.Settings.Duration)
                    break;

                await context.Store.Compact();
                context.Counters.IncrementCompactCalls();
            }
        }
        catch (Exception ex) {
            context.Failures.Record("compaction", KeyIdentity.Unknown, -1, ex.ToString());
        }
    }

    private async Task AssertFinalState(RunContext context)
    {
        var expectedScanKeys = new HashSet<KeyIdentity>();
        foreach (var owner in context.Owners) {
            var snapshot = owner.Snapshot;
            if (snapshot.PendingKind != PendingKind.None) {
                context.Failures.Record(
                    "final-owner-state",
                    new KeyIdentity(owner.OwnerId, snapshot.PendingKeyIndex),
                    -1,
                    $"pending={snapshot.PendingKind}");
            }
            for (var keyIndex = 0; keyIndex < owner.KeyCount; keyIndex++) {
                var key = owner.DataKeys[keyIndex];
                var value = await context.Store.Get(key.Bytes);
                context.Counters.AddGets(1);
                if (snapshot.IsLive[keyIndex]) {
                    expectedScanKeys.Add(key.Identity);
                    AssertFinalValue(context, key, value, snapshot.Versions[keyIndex]);
                }
                else if (value is not null) {
                    var version = TryDecodeValue(value.Value.Span, out var decoded, out _)
                        ? decoded.Version
                        : -1;
                    context.Failures.Record(
                        "delete-finality",
                        key.Identity,
                        version,
                        "Deleted key reappeared after quiescence.");
                }
            }

            expectedScanKeys.Add(owner.ManifestKey.Identity);
            var manifestValue = await context.Store.Get(owner.ManifestKey.Bytes);
            context.Counters.AddGets(1);
            AssertFinalValue(context, owner.ManifestKey, manifestValue, snapshot.ManifestVersion);
        }

        var scannedKeys = new HashSet<KeyIdentity>();
        await foreach (var (key, value) in context.Store.Scan()) {
            if (!TryParseKey(key.Span, out var identity)) {
                context.Failures.Record(
                    "well-formed",
                    KeyIdentity.Unknown,
                    -1,
                    $"Final Scan returned unknown key {Hex(key.Span)}.");
                continue;
            }
            if (identity.OwnerId < 0 || identity.OwnerId >= context.Owners.Length
                || (!identity.IsManifest
                    && (identity.KeyIndex < 0
                        || identity.KeyIndex >= context.Owners[identity.OwnerId].KeyCount))) {
                context.Failures.Record(
                    "attribution",
                    identity,
                    -1,
                    "Final Scan key is outside the owned key catalog.");
                continue;
            }
            if (!TryDecodeValue(value.Span, out var decoded, out var detail)) {
                context.Failures.Record("well-formed", identity, -1, $"Final Scan: {detail}");
                continue;
            }
            if (decoded.OwnerId != identity.OwnerId || decoded.KeyIndex != identity.KeyIndex) {
                context.Failures.Record(
                    "attribution",
                    identity,
                    decoded.Version,
                    $"Final Scan value names owner={decoded.OwnerId}, key={decoded.KeyIndex}.");
                continue;
            }
            if (!HasValidManifestEntries(context, identity, decoded, "Final Scan"))
                continue;

            if (!scannedKeys.Add(identity)) {
                context.Failures.Record(
                    "scan-completeness",
                    identity,
                    decoded.Version,
                    "Final Scan returned the key more than once.");
            }
        }
        context.Counters.IncrementScans();

        foreach (var expectedKey in expectedScanKeys)
            if (!scannedKeys.Contains(expectedKey)) {
                var snapshot = context.Owners[expectedKey.OwnerId].Snapshot;
                var version = expectedKey.IsManifest
                    ? snapshot.ManifestVersion
                    : snapshot.Versions[expectedKey.KeyIndex];
                context.Failures.Record(
                    "scan-completeness",
                    expectedKey,
                    version,
                    "Final Scan omitted a live key.");
            }
    }

    private async Task WriteData(
        RunContext context,
        OwnerState owner,
        int keyIndex,
        Random random)
    {
        var version = owner.Versions[keyIndex] + 1;
        var value = CreateDataValue(owner.OwnerId, keyIndex, version, PickPayloadLength(random));
        var compactCallsAtStart = context.Counters.CompactCalls;
        owner.SetPending(PendingKind.Write, keyIndex);
        await context.Store.Set(owner.DataKeys[keyIndex].Bytes, value);
        owner.AcknowledgeWrite(keyIndex, version);
        context.Counters.IncrementDataWrites();
        var write = new WriteObservation(
            version,
            "Set",
            compactCallsAtStart,
            context.Counters.CompactCalls);
        await AssertOwnerValue(context, owner.DataKeys[keyIndex], write);
    }

    private async Task WriteBatch(RunContext context, OwnerState owner, Random random)
    {
        var count = Math.Min(owner.KeyCount, random.Next(2, 5));
        var selected = new HashSet<int>();
        while (selected.Count < count)
            selected.Add(random.Next(owner.KeyCount));
        var items = new List<BatchWrite>(count);
        var updates = new (KvasarKey Key, KvasarValue? Value)[count];
        var updateIndex = 0;
        foreach (var keyIndex in selected) {
            var version = owner.Versions[keyIndex] + 1;
            var value = CreateDataValue(owner.OwnerId, keyIndex, version, PickPayloadLength(random));
            items.Add(new BatchWrite(keyIndex, version));
            updates[updateIndex++] = (owner.DataKeys[keyIndex].Bytes, value);
        }

        var compactCallsAtStart = context.Counters.CompactCalls;
        owner.SetPending(PendingKind.Write, -2);
        await context.Store.SetMany(updates);
        owner.AcknowledgeBatch(items);
        context.Counters.AddDataWrites(items.Count);
        context.Counters.IncrementSetManyCalls();
        var compactCallsAtAcknowledgement = context.Counters.CompactCalls;
        foreach (var item in items) {
            var write = new WriteObservation(
                item.Version,
                "SetMany",
                compactCallsAtStart,
                compactCallsAtAcknowledgement);
            await AssertOwnerValue(context, owner.DataKeys[item.KeyIndex], write);
        }
    }

    private async Task DeleteData(RunContext context, OwnerState owner, int keyIndex)
    {
        owner.SetPending(PendingKind.Delete, keyIndex);
        await WriteManifest(context, owner, keyIndex);
        await context.Store.Set(owner.DataKeys[keyIndex].Bytes, null);
        owner.AcknowledgeDelete(keyIndex);
        context.Counters.IncrementDeletes();
        await AssertOwnerAbsent(context, owner.DataKeys[keyIndex], owner.Versions[keyIndex]);
    }

    private async Task WriteManifest(RunContext context, OwnerState owner, int excludedKeyIndex)
    {
        var isDeleteBarrier = owner.PendingKind == PendingKind.Delete;
        if (!isDeleteBarrier)
            owner.SetPending(PendingKind.Manifest, -1);
        var version = owner.ManifestVersion + 1;
        var value = CreateManifestValue(owner, version, excludedKeyIndex);
        var compactCallsAtStart = context.Counters.CompactCalls;
        await context.Store.Set(owner.ManifestKey.Bytes, value);
        owner.AcknowledgeManifest(version, isDeleteBarrier);
        context.Counters.IncrementManifestWrites();
        var write = new WriteObservation(
            version,
            isDeleteBarrier ? "delete-barrier manifest Set" : "manifest Set",
            compactCallsAtStart,
            context.Counters.CompactCalls);
        await AssertOwnerValue(context, owner.ManifestKey, write);
    }

    private static async Task AssertOwnerValue(
        RunContext context,
        KeyRef key,
        WriteObservation write)
    {
        var value = await context.Store.Get(key.Bytes);
        context.Counters.AddGets(1);
        if (value is null) {
            context.Failures.Record(
                "owner-monotonic",
                key.Identity,
                write.Version,
                $"Owner read returned absent after acknowledged {write.Operation}; "
                + $"explicitCompactions={write.CompactCallsAtStart}"
                + $"->{write.CompactCallsAtAcknowledgement}"
                + $"->{context.Counters.CompactCalls}.");
            return;
        }
        if (!TryDecodeValue(value.Value.Span, out var decoded, out var detail)) {
            context.Failures.Record(
                "well-formed",
                key.Identity,
                write.Version,
                $"Owner read: {detail}");
            return;
        }
        if (decoded.OwnerId != key.Identity.OwnerId || decoded.KeyIndex != key.Identity.KeyIndex) {
            context.Failures.Record(
                "attribution",
                key.Identity,
                decoded.Version,
                $"Owner read value names owner={decoded.OwnerId}, key={decoded.KeyIndex}.");
            return;
        }
        if (!HasValidManifestEntries(context, key.Identity, decoded, "Owner read"))
            return;

        if (decoded.Version != write.Version) {
            context.Failures.Record(
                "owner-monotonic",
                key.Identity,
                decoded.Version,
                $"Owner acknowledged {write.Operation} version={write.Version}; "
                + $"explicitCompactions={write.CompactCallsAtStart}"
                + $"->{write.CompactCallsAtAcknowledgement}"
                + $"->{context.Counters.CompactCalls}.");
        }
    }

    private static async Task AssertOwnerAbsent(
        RunContext context,
        KeyRef key,
        long lastVersion)
    {
        var value = await context.Store.Get(key.Bytes);
        context.Counters.AddGets(1);
        if (value is null)
            return;

        var version = TryDecodeValue(value.Value.Span, out var decoded, out _)
            ? decoded.Version
            : -1;
        context.Failures.Record(
            "delete-finality",
            key.Identity,
            version,
            $"Owner deleted the key after version={lastVersion}, but it immediately reappeared.");
    }

    private static void AssertFinalValue(
        RunContext context,
        KeyRef key,
        KvasarValue? value,
        long expectedVersion)
    {
        if (value is null) {
            context.Failures.Record(
                "no-lost-update",
                key.Identity,
                expectedVersion,
                "Final read returned absent.");
            return;
        }
        if (!TryDecodeValue(value.Value.Span, out var decoded, out var detail)) {
            context.Failures.Record(
                "well-formed",
                key.Identity,
                expectedVersion,
                $"Final read: {detail}");
            return;
        }
        if (decoded.OwnerId != key.Identity.OwnerId || decoded.KeyIndex != key.Identity.KeyIndex) {
            context.Failures.Record(
                "attribution",
                key.Identity,
                decoded.Version,
                $"Final read value names owner={decoded.OwnerId}, key={decoded.KeyIndex}.");
            return;
        }
        if (!HasValidManifestEntries(context, key.Identity, decoded, "Final read"))
            return;

        if (decoded.Version != expectedVersion) {
            context.Failures.Record(
                "no-lost-update",
                key.Identity,
                decoded.Version,
                $"Final expected version={expectedVersion}.");
        }
    }

    private async Task ReadSingle(ReaderState reader, Random random)
    {
        var key = reader.Context.Keys[random.Next(reader.Context.Keys.Length)];
        var ownerSnapshot = reader.Context.Owners[key.Identity.OwnerId].Snapshot;
        var value = await reader.Context.Store.Get(key.Bytes);
        reader.Context.Counters.AddGets(1);
        var observed = ObserveKnownResult(reader, key, value, ownerSnapshot);
        if (observed is { IsManifest: true } manifest)
            await ValidateManifest(reader, key, manifest);
    }

    private async Task ReadMany(ReaderState reader, Random random)
    {
        var count = Math.Min(12, reader.Context.Keys.Length);
        var selected = new HashSet<int>();
        while (selected.Count < count)
            selected.Add(random.Next(reader.Context.Keys.Length));
        var keys = new KeyRef[count];
        var kvasarKeys = new KvasarKey[count];
        var ownerSnapshots = new OwnerSnapshot[count];
        var index = 0;
        foreach (var selectedIndex in selected) {
            var key = reader.Context.Keys[selectedIndex];
            keys[index] = key;
            kvasarKeys[index] = key.Bytes;
            ownerSnapshots[index] = reader.Context.Owners[key.Identity.OwnerId].Snapshot;
            index++;
        }

        var values = await reader.Context.Store.GetMany(kvasarKeys);
        reader.Context.Counters.IncrementGetManyCalls();
        reader.Context.Counters.AddGets(values.Length);
        var manifests = new List<ObservedValue>();
        for (var i = 0; i < values.Length; i++) {
            var observed = ObserveKnownResult(reader, keys[i], values[i], ownerSnapshots[i]);
            if (observed is { IsManifest: true } manifest)
                manifests.Add(new ObservedValue(keys[i], manifest));
        }
        foreach (var manifest in manifests)
            await ValidateManifest(reader, manifest.Key, manifest.Value);
    }

    private async Task ReadScan(ReaderState reader)
    {
        var startSnapshots = new OwnerSnapshot[reader.Context.Owners.Length];
        for (var ownerId = 0; ownerId < startSnapshots.Length; ownerId++)
            startSnapshots[ownerId] = reader.Context.Owners[ownerId].Snapshot;
        var compactCallsAtStart = reader.Context.Counters.CompactCalls;
        var yielded = new HashSet<KeyIdentity>();
        var manifests = new List<ObservedValue>();

        await foreach (var (key, value) in reader.Context.Store.Scan()) {
            var observed = ObserveScanValue(reader, key, value);
            if (observed is null)
                continue;
            if (!yielded.Add(observed.Value.Key.Identity)) {
                reader.Context.Failures.Record(
                    "scan-completeness",
                    observed.Value.Key.Identity,
                    observed.Value.Value.Version,
                    $"Reader={reader.ReaderIndex} Scan returned the key more than once.");
            }
            if (observed.Value.Value.IsManifest)
                manifests.Add(observed.Value);
        }
        reader.Context.Counters.IncrementScans();

        var endSnapshots = new OwnerSnapshot[reader.Context.Owners.Length];
        for (var ownerId = 0; ownerId < endSnapshots.Length; ownerId++)
            endSnapshots[ownerId] = reader.Context.Owners[ownerId].Snapshot;
        var scanObservation = new ScanObservation(
            startSnapshots,
            endSnapshots,
            yielded,
            compactCallsAtStart,
            reader.Context.Counters.CompactCalls);
        AssertScanCompleteness(reader, scanObservation);
        foreach (var manifest in manifests)
            await ValidateManifest(reader, manifest.Key, manifest.Value);
    }

    private static void AssertScanCompleteness(
        ReaderState reader,
        ScanObservation observation)
    {
        for (var ownerId = 0; ownerId < observation.StartSnapshots.Length; ownerId++) {
            var start = observation.StartSnapshots[ownerId];
            var end = observation.EndSnapshots[ownerId];
            var manifestKey = reader.Context.Owners[ownerId].ManifestKey;
            if (start.ManifestVersion > 0 && !observation.Yielded.Contains(manifestKey.Identity)) {
                reader.Context.Failures.Record(
                    "scan-completeness",
                    manifestKey.Identity,
                    start.ManifestVersion,
                    $"Reader={reader.ReaderIndex} Scan omitted a manifest that is never deleted; "
                    + $"manifest={start.ManifestVersion}->{end.ManifestVersion}, "
                    + $"revision={start.Revision}->{end.Revision}, "
                    + $"pending={start.PendingKind}/{start.PendingKeyIndex}"
                    + $"->{end.PendingKind}/{end.PendingKeyIndex}, "
                    + $"explicitCompactions={observation.CompactCallsAtStart}"
                    + $"->{observation.CompactCallsAtEnd}, yielded={observation.Yielded.Count}.");
            }
            for (var keyIndex = 0; keyIndex < start.IsLive.Length; keyIndex++) {
                var wasLive = start.IsLive[keyIndex];
                var hadPendingDelete = start.IsDeletePending(keyIndex);
                var wasDeletedDuringScan = start.DeleteEpochs[keyIndex] != end.DeleteEpochs[keyIndex]
                    || end.IsDeletePending(keyIndex);
                if (!wasLive || hadPendingDelete || wasDeletedDuringScan)
                    continue;

                var key = reader.Context.Owners[ownerId].DataKeys[keyIndex];
                if (!observation.Yielded.Contains(key.Identity)) {
                    reader.Context.Failures.Record(
                        "scan-completeness",
                        key.Identity,
                        start.Versions[keyIndex],
                        $"Reader={reader.ReaderIndex} Scan omitted a key live before start and not deleted during it; "
                        + $"version={start.Versions[keyIndex]}->{end.Versions[keyIndex]}, "
                        + $"deleteEpoch={start.DeleteEpochs[keyIndex]}->{end.DeleteEpochs[keyIndex]}, "
                        + $"revision={start.Revision}->{end.Revision}, "
                        + $"pending={start.PendingKind}/{start.PendingKeyIndex}"
                        + $"->{end.PendingKind}/{end.PendingKeyIndex}, "
                        + $"explicitCompactions={observation.CompactCallsAtStart}"
                        + $"->{observation.CompactCallsAtEnd}, yielded={observation.Yielded.Count}.");
                }
            }
        }
    }

    private async Task ValidateManifest(
        ReaderState reader,
        KeyRef manifestKey,
        DecodedValue manifest)
    {
        if (manifest.ManifestEntries is null)
            return;

        var observedValues = new DecodedValue?[manifest.ManifestEntries.Length];
        for (var i = 0; i < manifest.ManifestEntries.Length; i++) {
            var entry = manifest.ManifestEntries[i];
            var key = reader.Context.Owners[manifestKey.Identity.OwnerId].DataKeys[entry.KeyIndex];
            var ownerSnapshot = reader.Context.Owners[key.Identity.OwnerId].Snapshot;
            var value = await reader.Context.Store.Get(key.Bytes);
            reader.Context.Counters.AddGets(1);
            observedValues[i] = ObserveKnownResult(reader, key, value, ownerSnapshot);
        }

        var currentSnapshot = reader.Context.Owners[manifestKey.Identity.OwnerId].Snapshot;
        var currentValue = await reader.Context.Store.Get(manifestKey.Bytes);
        reader.Context.Counters.AddGets(1);
        var currentManifest = ObserveKnownResult(reader, manifestKey, currentValue, currentSnapshot);
        if (currentManifest is null || currentManifest.Value.Version != manifest.Version)
            return;

        for (var i = 0; i < manifest.ManifestEntries.Length; i++) {
            var entry = manifest.ManifestEntries[i];
            var key = reader.Context.Owners[manifestKey.Identity.OwnerId].DataKeys[entry.KeyIndex];
            var observed = observedValues[i];
            if (observed is null) {
                reader.Context.Failures.Record(
                    "write-order-visibility",
                    key.Identity,
                    entry.Version,
                    $"Reader={reader.ReaderIndex} observed manifest={manifest.Version}, but the named key was absent.");
            }
            else if (observed.Value.Version < entry.Version) {
                reader.Context.Failures.Record(
                    "write-order-visibility",
                    key.Identity,
                    observed.Value.Version,
                    $"Reader={reader.ReaderIndex} manifest={manifest.Version} requires version>={entry.Version}.");
            }
        }
    }

    private static DecodedValue? ObserveKnownResult(
        ReaderState reader,
        KeyRef key,
        KvasarValue? value,
        OwnerSnapshot before)
    {
        var after = reader.Context.Owners[key.Identity.OwnerId].Snapshot;
        var isStable = before.Revision == after.Revision;
        if (value is null) {
            if (key.Identity.IsManifest) {
                if (isStable && before.ManifestVersion > 0) {
                    reader.Context.Failures.Record(
                        "write-order-visibility",
                        key.Identity,
                        before.ManifestVersion,
                        $"Reader={reader.ReaderIndex} stable manifest read returned absent.");
                }
            }
            else if (isStable && before.IsLive[key.Identity.KeyIndex]
                && !before.IsDeletePending(key.Identity.KeyIndex)) {
                reader.Context.Failures.Record(
                    "owner-visibility",
                    key.Identity,
                    before.Versions[key.Identity.KeyIndex],
                    $"Reader={reader.ReaderIndex} stable live key read returned absent.");
            }
            return null;
        }

        if (!TryDecodeValue(value.Value.Span, out var decoded, out var detail)) {
            reader.Context.Failures.Record(
                "well-formed",
                key.Identity,
                -1,
                $"Reader={reader.ReaderIndex}: {detail}");
            return null;
        }
        if (decoded.OwnerId != key.Identity.OwnerId || decoded.KeyIndex != key.Identity.KeyIndex) {
            reader.Context.Failures.Record(
                "attribution",
                key.Identity,
                decoded.Version,
                $"Reader={reader.ReaderIndex} value names owner={decoded.OwnerId}, key={decoded.KeyIndex}.");
            return null;
        }
        if (!HasValidManifestEntries(reader.Context, key.Identity, decoded, $"Reader={reader.ReaderIndex}"))
            return null;

        AssertReaderMonotonic(reader, key.Identity, decoded.Version);
        if (key.Identity.IsManifest) {
            if (isStable && before.ManifestVersion > decoded.Version) {
                reader.Context.Failures.Record(
                    "owner-monotonic",
                    key.Identity,
                    decoded.Version,
                    $"Reader={reader.ReaderIndex} owner acknowledged manifest={before.ManifestVersion}.");
            }
        }
        else if (isStable) {
            var keyIndex = key.Identity.KeyIndex;
            if (!before.IsLive[keyIndex] && !before.IsWritePending(keyIndex)) {
                reader.Context.Failures.Record(
                    "delete-finality",
                    key.Identity,
                    decoded.Version,
                    $"Reader={reader.ReaderIndex} key reappeared while owner state was stably deleted.");
            }
            else if (before.IsLive[keyIndex] && !before.IsDeletePending(keyIndex)
                && decoded.Version < before.Versions[keyIndex]) {
                reader.Context.Failures.Record(
                    "owner-monotonic",
                    key.Identity,
                    decoded.Version,
                    $"Reader={reader.ReaderIndex} owner acknowledged version={before.Versions[keyIndex]}.");
            }
        }
        return decoded;
    }

    private static ObservedValue? ObserveScanValue(
        ReaderState reader,
        KvasarKey key,
        KvasarValue value)
    {
        if (!TryParseKey(key.Span, out var identity)) {
            reader.Context.Failures.Record(
                "well-formed",
                KeyIdentity.Unknown,
                -1,
                $"Reader={reader.ReaderIndex} Scan returned unknown key {Hex(key.Span)}.");
            return null;
        }
        if (identity.OwnerId < 0 || identity.OwnerId >= reader.Context.Owners.Length
            || (!identity.IsManifest
                && (identity.KeyIndex < 0
                    || identity.KeyIndex >= reader.Context.Owners[identity.OwnerId].KeyCount))) {
            reader.Context.Failures.Record(
                "attribution",
                identity,
                -1,
                $"Reader={reader.ReaderIndex} Scan key is outside the owned key catalog.");
            return null;
        }
        if (!TryDecodeValue(value.Span, out var decoded, out var detail)) {
            reader.Context.Failures.Record(
                "well-formed",
                identity,
                -1,
                $"Reader={reader.ReaderIndex} Scan: {detail}");
            return null;
        }
        if (decoded.OwnerId != identity.OwnerId || decoded.KeyIndex != identity.KeyIndex) {
            reader.Context.Failures.Record(
                "attribution",
                identity,
                decoded.Version,
                $"Reader={reader.ReaderIndex} Scan value names owner={decoded.OwnerId}, key={decoded.KeyIndex}.");
            return null;
        }
        if (!HasValidManifestEntries(
                reader.Context,
                identity,
                decoded,
                $"Reader={reader.ReaderIndex} Scan"))
            return null;

        AssertReaderMonotonic(reader, identity, decoded.Version);
        var keyRef = identity.IsManifest
            ? reader.Context.Owners[identity.OwnerId].ManifestKey
            : reader.Context.Owners[identity.OwnerId].DataKeys[identity.KeyIndex];
        return new ObservedValue(keyRef, decoded);
    }

    private static void AssertReaderMonotonic(
        ReaderState reader,
        KeyIdentity identity,
        long version)
    {
        var slot = identity.KeyIndex + 1;
        var lastVersion = reader.LastSeenVersions[identity.OwnerId][slot];
        if (version < lastVersion) {
            reader.Context.Failures.Record(
                "reader-monotonic",
                identity,
                version,
                $"Reader={reader.ReaderIndex} previously observed version={lastVersion}.");
        }
        if (version > lastVersion)
            reader.LastSeenVersions[identity.OwnerId][slot] = version;
    }

    private static bool HasValidManifestEntries(
        RunContext context,
        KeyIdentity identity,
        DecodedValue decoded,
        string source)
    {
        if (!decoded.IsManifest)
            return true;

        var keyCount = context.Owners[identity.OwnerId].KeyCount;
        foreach (var entry in decoded.ManifestEntries!) {
            if (entry.KeyIndex < keyCount)
                continue;

            context.Failures.Record(
                "well-formed",
                identity,
                decoded.Version,
                $"{source} manifest names out-of-range key={entry.KeyIndex}, keyCount={keyCount}.");
            return false;
        }
        return true;
    }

    private static KeyRef[] BuildKeyCatalog(OwnerState[] owners)
    {
        var keys = new KeyRef[owners.Length * (owners[0].KeyCount + 1)];
        var index = 0;
        foreach (var owner in owners) {
            foreach (var key in owner.DataKeys)
                keys[index++] = key;
            keys[index++] = owner.ManifestKey;
        }
        return keys;
    }

    private static byte[] CreateDataValue(
        int ownerId,
        int keyIndex,
        long version,
        int payloadLength)
    {
        var value = new byte[HeaderSize + payloadLength];
        var payload = value.AsSpan(HeaderSize);
        for (var i = 0; i < payload.Length; i++)
            payload[i] = unchecked((byte)(ownerId * 29 + keyIndex * 47 + version * 61 + i * 13));
        WriteHeader(value, ownerId, keyIndex, version);
        return value;
    }

    private static byte[] CreateManifestValue(
        OwnerState owner,
        long version,
        int excludedKeyIndex)
    {
        var entryCount = 0;
        for (var keyIndex = 0; keyIndex < owner.KeyCount; keyIndex++)
            if (owner.IsLive(keyIndex) && keyIndex != excludedKeyIndex)
                entryCount++;
        var value = new byte[HeaderSize + sizeof(int) + entryCount * ManifestEntrySize];
        var payload = value.AsSpan(HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(payload, entryCount);
        var offset = sizeof(int);
        for (var keyIndex = 0; keyIndex < owner.KeyCount; keyIndex++) {
            if (!owner.IsLive(keyIndex) || keyIndex == excludedKeyIndex)
                continue;

            BinaryPrimitives.WriteInt32LittleEndian(payload[offset..], keyIndex);
            BinaryPrimitives.WriteInt64LittleEndian(payload[(offset + sizeof(int))..], owner.Versions[keyIndex]);
            offset += ManifestEntrySize;
        }
        WriteHeader(value, owner.OwnerId, -1, version);
        return value;
    }

    private static void WriteHeader(byte[] value, int ownerId, int keyIndex, long version)
    {
        var span = value.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, ownerId);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], keyIndex);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], version);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], value.Length - HeaderSize);
        var checksum = XxHash3.HashToUInt64(span[HeaderSize..]);
        BinaryPrimitives.WriteUInt64LittleEndian(span[20..], checksum);
    }

    private static bool TryDecodeValue(
        ReadOnlySpan<byte> value,
        out DecodedValue decoded,
        out string detail)
    {
        decoded = default;
        if (value.Length < HeaderSize) {
            detail = $"value length={value.Length} is smaller than header={HeaderSize}; bytes={Hex(value)}";
            return false;
        }

        var ownerId = BinaryPrimitives.ReadInt32LittleEndian(value);
        var keyIndex = BinaryPrimitives.ReadInt32LittleEndian(value[4..]);
        var version = BinaryPrimitives.ReadInt64LittleEndian(value[8..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(value[16..]);
        var expectedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(value[20..]);
        if (ownerId < 0 || keyIndex < -1 || version <= 0
            || payloadLength < 0 || payloadLength != value.Length - HeaderSize) {
            detail =
                $"invalid header owner={ownerId}, key={keyIndex}, version={version}, "
                + $"payloadLength={payloadLength}, actualLength={value.Length}; bytes={Hex(value)}";
            return false;
        }

        var payload = value[HeaderSize..];
        var actualChecksum = XxHash3.HashToUInt64(payload);
        if (actualChecksum != expectedChecksum) {
            detail =
                $"checksum mismatch expected={expectedChecksum:X16}, actual={actualChecksum:X16}; "
                + $"bytes={Hex(value)}";
            return false;
        }

        ManifestEntry[]? entries = null;
        if (keyIndex == -1) {
            if (payload.Length < sizeof(int)) {
                detail = $"manifest payload length={payload.Length} has no entry count; bytes={Hex(value)}";
                return false;
            }
            var entryCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
            if (entryCount < 0 || payload.Length != sizeof(int) + entryCount * ManifestEntrySize) {
                detail =
                    $"manifest entryCount={entryCount}, payloadLength={payload.Length}; bytes={Hex(value)}";
                return false;
            }
            entries = new ManifestEntry[entryCount];
            var seen = new HashSet<int>();
            var offset = sizeof(int);
            for (var i = 0; i < entries.Length; i++) {
                var entryKeyIndex = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
                var entryVersion =
                    BinaryPrimitives.ReadInt64LittleEndian(payload[(offset + sizeof(int))..]);
                if (entryKeyIndex < 0 || entryVersion <= 0 || !seen.Add(entryKeyIndex)) {
                    detail =
                        $"manifest entry={i}, key={entryKeyIndex}, version={entryVersion}; bytes={Hex(value)}";
                    return false;
                }
                entries[i] = new ManifestEntry(entryKeyIndex, entryVersion);
                offset += ManifestEntrySize;
            }
        }

        decoded = new DecodedValue(ownerId, keyIndex, version, entries);
        detail = "";
        return true;
    }

    private static bool TryParseKey(ReadOnlySpan<byte> key, out KeyIdentity identity)
    {
        identity = KeyIdentity.Unknown;
        if (key.Length < 4 || key[0] != (byte)'o'
            || !IsAsciiDigit(key[1]) || !IsAsciiDigit(key[2]) || key[3] != (byte)'-')
            return false;

        var ownerId = (key[1] - (byte)'0') * 10 + key[2] - (byte)'0';
        if (key.Length == 12 && key[4..].SequenceEqual("manifest"u8)) {
            identity = new KeyIdentity(ownerId, -1);
            return true;
        }
        if (key.Length != 9 || key[4] != (byte)'k'
            || !IsAsciiDigit(key[5]) || !IsAsciiDigit(key[6])
            || !IsAsciiDigit(key[7]) || !IsAsciiDigit(key[8]))
            return false;

        var keyIndex = (key[5] - (byte)'0') * 1000
            + (key[6] - (byte)'0') * 100
            + (key[7] - (byte)'0') * 10
            + key[8] - (byte)'0';
        identity = new KeyIdentity(ownerId, keyIndex);
        return true;
    }

    private static int PickPayloadLength(Random random)
        => random.Next(4) switch {
            0 => random.Next(24, 161),
            1 => random.Next(300, 461),
            2 => random.Next(700, 1301),
            _ => random.Next(1700, 3001),
        };

    private static int DeriveSeed(int baseSeed, int role, int index)
    {
        unchecked {
            var value = (uint)baseSeed;
            value ^= (uint)role * 0x9E37_79B9u;
            value ^= (uint)(index + 1) * 0x85EB_CA6Bu;
            value ^= value >> 16;
            value *= 0x7FEB_352Du;
            value ^= value >> 15;
            return (int)value;
        }
    }

    private static string ModeName(bool isEncrypted)
        => isEncrypted ? "encrypted" : "plaintext";

    private static string Hex(ReadOnlySpan<byte> value)
    {
        const int maxBytes = 24;
        var length = Math.Min(value.Length, maxBytes);
        var builder = new StringBuilder($"len={value.Length} [");
        for (var i = 0; i < length; i++)
            builder.Append(value[i].ToString("X2", null));
        return builder.Append(']').ToString();
    }

    private static bool IsAsciiDigit(byte value)
        => value is >= (byte)'0' and <= (byte)'9';

    // Nested types

    private sealed class RunContext(
        KvasarStore store,
        RunSettings settings,
        OwnerState[] owners,
        KeyRef[] keys,
        FailureSink failures,
        RunCounters counters,
        int runSeed)
    {
        public KvasarStore Store { get; } = store;
        public RunSettings Settings { get; } = settings;
        public OwnerState[] Owners { get; } = owners;
        public KeyRef[] Keys { get; } = keys;
        public FailureSink Failures { get; } = failures;
        public RunCounters Counters { get; } = counters;
        public int RunSeed { get; } = runSeed;
    }

    private sealed class ReaderState
    {
        public RunContext Context { get; }
        public int ReaderIndex { get; }
        public long[][] LastSeenVersions { get; }

        public ReaderState(RunContext context, int readerIndex)
        {
            Context = context;
            ReaderIndex = readerIndex;
            LastSeenVersions = new long[context.Owners.Length][];
            for (var ownerId = 0; ownerId < LastSeenVersions.Length; ownerId++)
                LastSeenVersions[ownerId] = new long[context.Owners[ownerId].KeyCount + 1];
        }
    }

    private sealed class OwnerState
    {
        private readonly long[] _versions;
        private readonly bool[] _isLive;
        private readonly long[] _deleteEpochs;
        private OwnerSnapshot _snapshot;
        private long _revision;

        public int OwnerId { get; }
        public int KeyCount => DataKeys.Length;
        public KeyRef[] DataKeys { get; }
        public KeyRef ManifestKey { get; }
        public long[] Versions => _versions;
        public long ManifestVersion { get; private set; }
        public PendingKind PendingKind { get; private set; }
        public OwnerSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public OwnerState(int ownerId, int keyCount)
        {
            OwnerId = ownerId;
            DataKeys = new KeyRef[keyCount];
            for (var keyIndex = 0; keyIndex < keyCount; keyIndex++) {
                var identity = new KeyIdentity(ownerId, keyIndex);
                DataKeys[keyIndex] = new KeyRef(
                    identity,
                    Encoding.ASCII.GetBytes($"o{ownerId:D2}-k{keyIndex:D4}"));
            }
            ManifestKey = new KeyRef(
                new KeyIdentity(ownerId, -1),
                Encoding.ASCII.GetBytes($"o{ownerId:D2}-manifest"));
            _versions = new long[keyCount];
            _isLive = new bool[keyCount];
            _deleteEpochs = new long[keyCount];
            _snapshot = CreateSnapshot(-1);
        }

        public bool IsLive(int keyIndex) => _isLive[keyIndex];

        public void SetPending(PendingKind kind, int keyIndex)
        {
            PendingKind = kind;
            Publish(keyIndex);
        }

        public void AcknowledgeWrite(int keyIndex, long version)
        {
            _versions[keyIndex] = version;
            _isLive[keyIndex] = true;
            PendingKind = PendingKind.None;
            Publish(-1);
        }

        public void AcknowledgeBatch(List<BatchWrite> items)
        {
            foreach (var item in items) {
                _versions[item.KeyIndex] = item.Version;
                _isLive[item.KeyIndex] = true;
            }
            PendingKind = PendingKind.None;
            Publish(-1);
        }

        public void AcknowledgeDelete(int keyIndex)
        {
            _isLive[keyIndex] = false;
            _deleteEpochs[keyIndex]++;
            PendingKind = PendingKind.None;
            Publish(-1);
        }

        public void AcknowledgeManifest(long version, bool isDeleteBarrier)
        {
            ManifestVersion = version;
            if (!isDeleteBarrier)
                PendingKind = PendingKind.None;
            Publish(isDeleteBarrier ? Snapshot.PendingKeyIndex : -1);
        }

        private void Publish(int pendingKeyIndex)
        {
            _revision++;
            Volatile.Write(ref _snapshot, CreateSnapshot(pendingKeyIndex));
        }

        private OwnerSnapshot CreateSnapshot(int pendingKeyIndex)
            => new(
                (long[])_versions.Clone(),
                (bool[])_isLive.Clone(),
                (long[])_deleteEpochs.Clone(),
                ManifestVersion,
                PendingKind,
                pendingKeyIndex,
                _revision);
    }

    private sealed class FailureSink(int seed, string mode)
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly ConcurrentDictionary<string, int> _countsByInvariant = new();
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public string[] Messages => _messages.ToArray();

        public void Record(
            string invariant,
            KeyIdentity identity,
            long version,
            string detail)
        {
            Interlocked.Increment(ref _count);
            var invariantCount = _countsByInvariant.AddOrUpdate(invariant, 1, static (_, count) => count + 1);
            if (invariantCount > MaxFailuresPerInvariant)
                return;

            _messages.Enqueue(
                $"seed={seed} mode={mode} invariant={invariant} owner={identity.OwnerId} "
                + $"key={identity.KeyIndex} version={version} {detail}");
        }
    }

    private sealed class RunCounters
    {
        private long _dataWrites;
        private long _deletes;
        private long _manifestWrites;
        private long _setManyCalls;
        private long _gets;
        private long _getManyCalls;
        private long _scans;
        private long _compactCalls;

        public long DataWrites => Volatile.Read(ref _dataWrites);
        public long Deletes => Volatile.Read(ref _deletes);
        public long ManifestWrites => Volatile.Read(ref _manifestWrites);
        public long SetManyCalls => Volatile.Read(ref _setManyCalls);
        public long Gets => Volatile.Read(ref _gets);
        public long GetManyCalls => Volatile.Read(ref _getManyCalls);
        public long Scans => Volatile.Read(ref _scans);
        public long CompactCalls => Volatile.Read(ref _compactCalls);

        public void IncrementDataWrites() => Interlocked.Increment(ref _dataWrites);
        public void AddDataWrites(int count) => Interlocked.Add(ref _dataWrites, count);
        public void IncrementDeletes() => Interlocked.Increment(ref _deletes);
        public void IncrementManifestWrites() => Interlocked.Increment(ref _manifestWrites);
        public void IncrementSetManyCalls() => Interlocked.Increment(ref _setManyCalls);
        public void AddGets(int count) => Interlocked.Add(ref _gets, count);
        public void IncrementGetManyCalls() => Interlocked.Increment(ref _getManyCalls);
        public void IncrementScans() => Interlocked.Increment(ref _scans);
        public void IncrementCompactCalls() => Interlocked.Increment(ref _compactCalls);
    }

    private sealed record OwnerSnapshot(
        long[] Versions,
        bool[] IsLive,
        long[] DeleteEpochs,
        long ManifestVersion,
        PendingKind PendingKind,
        int PendingKeyIndex,
        long Revision)
    {
        public bool IsDeletePending(int keyIndex)
            => PendingKind == PendingKind.Delete && PendingKeyIndex == keyIndex;

        public bool IsWritePending(int keyIndex)
            => PendingKind == PendingKind.Write
                && (PendingKeyIndex == keyIndex || PendingKeyIndex == -2);
    }

    private readonly record struct RunReport(string ResultLine, string[] Failures);
    private readonly record struct KeyRef(KeyIdentity Identity, byte[] Bytes);
    private readonly record struct KeyIdentity(int OwnerId, int KeyIndex)
    {
        public static readonly KeyIdentity Unknown = new(-1, -1);
        public bool IsManifest => KeyIndex == -1;
    }

    private readonly record struct BatchWrite(int KeyIndex, long Version);
    private readonly record struct WriteObservation(
        long Version,
        string Operation,
        long CompactCallsAtStart,
        long CompactCallsAtAcknowledgement);
    private readonly record struct ManifestEntry(int KeyIndex, long Version);
    private readonly record struct DecodedValue(
        int OwnerId,
        int KeyIndex,
        long Version,
        ManifestEntry[]? ManifestEntries)
    {
        public bool IsManifest => KeyIndex == -1;
    }

    private readonly record struct ObservedValue(KeyRef Key, DecodedValue Value);
    private readonly record struct ScanObservation(
        OwnerSnapshot[] StartSnapshots,
        OwnerSnapshot[] EndSnapshots,
        HashSet<KeyIdentity> Yielded,
        long CompactCallsAtStart,
        long CompactCallsAtEnd);

    private enum PendingKind
    {
        None,
        Write,
        Delete,
        Manifest,
    }
}
