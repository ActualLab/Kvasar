using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;
using ActualLab.Kvasar.Tests.Storage;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Seeded crashes across repeated workloads and recoveries against one in-memory file set.
/// Set KVASAR_CRASH_FUZZ_DEEP=1 for the 5-cycle, 6-point, all-mode matrix.
/// Set KVASAR_CRASH_FUZZ_SEED to replay one exact scenario seed.
/// </summary>
[Trait("Category", "Slow")]
public sealed class MultiCycleCrashFuzzTests : IDisposable
{
    private sealed record RunSettings(
        int CycleCount,
        int PointCount,
        int SeedCount,
        int OperationCount,
        int BaseSeed,
        IReadOnlyList<CrashMode?> Modes,
        bool IsDeep,
        bool HasConfiguredSeed);

    private const uint FormatVersion = KvasarConstants.DataFormatVersion;
    private const int PageSize = 512;
    private const int KeyCount = 16;
    private const int ReservedKeyCount = 4;
    private const int DefaultSeed = 0x4D43_465A;

    private static readonly CrashMode?[] DefaultModes =
        [null, CrashMode.LoseAll, CrashMode.Torn, CrashMode.Reorder];
    private static readonly int[] TailLengths =
        [137, PageSize + KvasarConstants.GcmTagSize, (PageSize + KvasarConstants.GcmTagSize) + 137];

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "kvasar-multicycle-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _encryptionKey = new byte[32];
    private readonly ITestOutputHelper _output;

    public MultiCycleCrashFuzzTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
        for (var i = 0; i < _encryptionKey.Length; i++)
            _encryptionKey[i] = unchecked((byte)(i * 19 + 7));
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
    public async Task RepeatedCrashesPreserveRecoveryInvariants()
    {
        var settings = ReadSettings();
        var coverage = new Coverage();
        var clock = Stopwatch.StartNew();
        for (var seedIndex = 0; seedIndex < settings.SeedCount; seedIndex++) {
            var scenarioSeed = settings.HasConfiguredSeed
                ? settings.BaseSeed
                : DeriveSeed(settings.BaseSeed, 101, seedIndex);
            for (var modeIndex = 0; modeIndex < settings.Modes.Count; modeIndex++)
                for (var pointIndex = 0; pointIndex < settings.PointCount; pointIndex++)
                    await RunScenario(settings, coverage, scenarioSeed, modeIndex, pointIndex)
                        .ConfigureAwait(false);
        }
        clock.Stop();

        var scenarioCount = settings.SeedCount * settings.Modes.Count * settings.PointCount;
        _output.WriteLine(
            $"RESULT baseSeed={settings.BaseSeed} deep={settings.IsDeep} cycles={settings.CycleCount} "
            + $"crashPoints={settings.PointCount} modes={settings.Modes.Count} seeds={settings.SeedCount} "
            + $"scenarios={scenarioCount} cycleRuns={coverage.CycleRuns} crashEvents={coverage.CrashEvents} "
            + $"workloadOperations={coverage.MinWorkloadOperations}-{coverage.MaxWorkloadOperations} "
            + $"recoveryOperations={coverage.MinRecoveryOperations}-{coverage.MaxRecoveryOperations} "
            + $"elapsedMs={clock.Elapsed.TotalMilliseconds:F0}");
    }

    // Private methods

    private async Task RunScenario(
        RunSettings settings,
        Coverage coverage,
        int scenarioSeed,
        int modeIndex,
        int pointIndex)
    {
        var backend = new FakeStorageBackend();
        var basePath = Path.Combine(_directory, "store");
        var model = new OracleModel();
        var sequence = new List<string>();
        await Initialize(backend, basePath, model, scenarioSeed).ConfigureAwait(false);

        for (var cycle = 0; cycle < settings.CycleCount; cycle++) {
            var workloadMode = SelectMode(settings, modeIndex, cycle, 0);
            var workloadSeed = DeriveSeed(scenarioSeed, 211 + cycle, 0);
            var workload = new Func<CrashRun<WorkloadNote>, Task>(
                run => RunWorkload(run, basePath, workloadSeed, cycle, settings.OperationCount));
            var workloadOperationCount = await CrashHarness.CountCrashPoints(backend, workload)
                .ConfigureAwait(false);
            var workloadPoint = SelectCrashPoint(
                workloadOperationCount, pointIndex, settings.PointCount);
            WorkloadNote[] workloadNotes = [];
            await CrashHarness.RunCase(
                    backend,
                    workload,
                    outcome => {
                        workloadNotes = outcome.Notes.ToArray();
                        return Task.CompletedTask;
                    },
                    workloadPoint,
                    workloadMode,
                    DeriveSeed(scenarioSeed, 307 + cycle, pointIndex))
                .ConfigureAwait(false);
            coverage.ObserveWorkload(workloadOperationCount);
            var transition = model.Apply(workloadNotes);

            await AppendTornTail(
                    backend, basePath, TailLengths[cycle % TailLengths.Length],
                    DeriveSeed(scenarioSeed, 401 + cycle, pointIndex))
                .ConfigureAwait(false);

            var recoveryPoint = 0;
            CrashMode? recoveryMode = null;
            if (cycle > 0) {
                recoveryMode = SelectMode(settings, modeIndex, cycle, 1);
                var recovery = new Func<CrashRun<int>, Task>(
                    run => CrashDuringRecovery(run, basePath));
                var recoveryOperationCount = await CrashHarness.CountCrashPoints(backend, recovery)
                    .ConfigureAwait(false);
                recoveryPoint = SelectCrashPoint(
                    recoveryOperationCount,
                    (pointIndex + cycle) % settings.PointCount,
                    settings.PointCount);
                await CrashHarness.RunCase(
                        backend,
                        recovery,
                        _ => Task.CompletedTask,
                        recoveryPoint,
                        recoveryMode,
                        DeriveSeed(scenarioSeed, 503 + cycle, pointIndex))
                    .ConfigureAwait(false);
                coverage.ObserveRecovery(recoveryOperationCount);
            }

            sequence.Add(
                $"c{cycle}:workload(point={workloadPoint},mode={ModeName(workloadMode)},"
                + $"ops=[{FormatNotes(workloadNotes)}]),tail={TailLengths[cycle % TailLengths.Length]},"
                + $"recovery(point={recoveryPoint},mode={ModeName(recoveryMode)})");
            var context = FailureContext(
                scenarioSeed,
                cycle,
                workloadPoint,
                workloadMode,
                recoveryPoint,
                recoveryMode,
                sequence);
            await RecoverAndVerify(backend, basePath, model, transition, context).ConfigureAwait(false);
            coverage.CycleRuns++;
            if (cycle == 0)
                await RunIdleSession(backend, basePath).ConfigureAwait(false);
        }
    }

    private async Task Initialize(
        FakeStorageBackend backend,
        string basePath,
        OracleModel model,
        int scenarioSeed)
    {
        await using var store = await KvasarStore.Open(Options(basePath, backend)).ConfigureAwait(false);
        for (var i = 0; i < KeyCount; i++) {
            var key = Key(i);
            var value = NewValue(scenarioSeed, -1, i, 96);
            await store.Set(Utf8(key), value).ConfigureAwait(false);
            model.AddInitial(key, value);
        }
        await store.Flush().ConfigureAwait(false);
    }

    private async Task RunWorkload(
        CrashRun<WorkloadNote> run,
        string basePath,
        int workloadSeed,
        int cycle,
        int operationCount)
    {
        await using var store = await KvasarStore.Open(Options(basePath, run.Storage)).ConfigureAwait(false);
        run.ArmCrashPoints();
        var random = new Random(workloadSeed);
        for (var operation = 0; operation < operationCount; operation++) {
            var roll = random.Next(100);
            if (operation == 2 || roll < 14) {
                var note = WorkloadNote.Flush();
                run.Note(note);
                await store.Flush().ConfigureAwait(false);
                note.IsAcknowledged = true;
                continue;
            }
            if (operation == 5 || roll < 24) {
                var note = WorkloadNote.Compact();
                run.Note(note);
                await store.Compact().ConfigureAwait(false);
                note.IsAcknowledged = true;
                continue;
            }

            var keyIndex = ReservedKeyCount + random.Next(KeyCount - ReservedKeyCount);
            var key = Key(keyIndex);
            if (roll < 38) {
                var note = WorkloadNote.Delete(key);
                run.Note(note);
                await store.Set(Utf8(key), null).ConfigureAwait(false);
                note.IsAcknowledged = true;
                continue;
            }

            var length = random.Next(4) switch {
                0 => random.Next(20, 100),
                1 => random.Next(180, 420),
                2 => random.Next(500, 900),
                _ => random.Next(1000, 1500),
            };
            var value = NewValue(workloadSeed, cycle, operation, length);
            var setNote = WorkloadNote.Set(key, value);
            run.Note(setNote);
            await store.Set(Utf8(key), value).ConfigureAwait(false);
            setNote.IsAcknowledged = true;
        }
    }

    private async Task CrashDuringRecovery(CrashRun<int> run, string basePath)
    {
        run.ArmCrashPoints();
        await using var store = await KvasarStore.Open(Options(basePath, run.Storage)).ConfigureAwait(false);
    }

    private async Task AppendTornTail(
        FakeStorageBackend backend,
        string basePath,
        int length,
        int seed)
    {
        var path = await SelectActiveDataPath(backend, basePath).ConfigureAwait(false);
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        await using var file = await backend.Open(path).ConfigureAwait(false);
        await file.Write(file.Length, bytes).ConfigureAwait(false);
        await file.FlushToDisk().ConfigureAwait(false);
    }

    private async Task<string> SelectActiveDataPath(FakeStorageBackend backend, string basePath)
    {
        var read = await ReadSuperblock(backend, basePath).ConfigureAwait(false);
        if (read.States.Length != 0)
            return DataPath(basePath, read.States[0].DataSlot);

        return Enumerable.Range(0, 2)
            .Select(slot => DataPath(basePath, slot))
            .OrderByDescending(path => backend.Exists(path) ? backend.GetBytes(path).Length : -1)
            .First();
    }

    private async Task RecoverAndVerify(
        FakeStorageBackend backend,
        string basePath,
        OracleModel model,
        OracleTransition transition,
        string context)
    {
        var adoption = await InspectAdoption(backend, basePath).ConfigureAwait(false);
        KvasarStore? store = null;
        var openTask = async () => store = await KvasarStore
            .Open(Options(basePath, backend))
            .ConfigureAwait(false);
        await openTask.Should().NotThrowAsync($"{context}, invariant=open").ConfigureAwait(false);
        var openedStore = store!;
        await using (openedStore.ConfigureAwait(false)) {
            var contents = await Scan(openedStore, context).ConfigureAwait(false);
            var verificationContext =
                $"{context}, adoption={adoption.Summary}, recoveredCount={contents.Count}, "
                + $"actualFallbacks={openedStore.Stats.FallbackRecoveries}";
            if (model.Current.Count != 0
                && (adoption.HasSatisfiableSlot || adoption.HasAuthenticatingPrefix))
                contents.Should().NotBeEmpty(
                    $"{verificationContext}, invariant=no-wipe");

            openedStore.Stats.FallbackRecoveries.Should().Be(
                adoption.MustUseFallback ? 1 : 0,
                $"{verificationContext}, invariant=fallback-count, "
                + $"expectedFallback={adoption.MustUseFallback}");
            AssertNoFabricatedValues(contents, model, verificationContext);
            AssertCommittedDataSurvives(contents, transition, verificationContext);
            AssertMonotonic(contents, transition, verificationContext);
            model.Resync(contents);
        }
    }

    private async Task RunIdleSession(FakeStorageBackend backend, string basePath)
    {
        await using var store = await KvasarStore.Open(Options(basePath, backend)).ConfigureAwait(false);
    }

    private async Task<AdoptionOracle> InspectAdoption(FakeStorageBackend backend, string basePath)
    {
        var read = await ReadSuperblock(backend, basePath).ConfigureAwait(false);
        var hasSatisfiableSlot = false;
        var hasAdoptableSlot = false;
        var stateSummaries = new List<string>();
        for (var i = 0; i < read.States.Length; i++) {
            var state = read.States[i];
            var previous = i + 1 < read.States.Length ? read.States[i + 1] : default(SuperblockState?);
            if (!TryReadCurrentData(backend, basePath, state.DataSlot, out var data)) {
                stateSummaries.Add($"g{state.Generation}:slot={state.DataSlot},data=invalid");
                continue;
            }
            var isSatisfiable = IsSatisfiable(state, data.Header, data.Bytes.Length);
            if (!isSatisfiable) {
                stateSummaries.Add(
                    $"g{state.Generation}:slot={state.DataSlot},commit={state.DataCommitLength},"
                    + $"floor={state.DataAuthenticationFloor},physical={data.Bytes.Length},satisfiable=False");
                continue;
            }

            hasSatisfiableSlot = true;
            var isAdoptable = AuthenticatesCommitWindow(state, previous, data);
            stateSummaries.Add(
                $"g{state.Generation}:slot={state.DataSlot},commit={state.DataCommitLength},"
                + $"floor={state.DataAuthenticationFloor},physical={data.Bytes.Length},"
                + $"satisfiable=True,adoptable={isAdoptable}");
            if (isAdoptable)
                hasAdoptableSlot = true;
        }

        var hasCurrentHeader = false;
        var hasAuthenticatingPrefix = false;
        var dataSummaries = new List<string>();
        for (var slot = 0; slot < 2; slot++) {
            if (!TryReadCurrentData(backend, basePath, slot, out var data)) {
                dataSummaries.Add($"{slot}:invalid");
                continue;
            }

            hasCurrentHeader = true;
            var hasPrefix = AuthenticatesPage(data, 0);
            hasAuthenticatingPrefix |= hasPrefix;
            dataSummaries.Add($"{slot}:length={data.Bytes.Length},prefix={hasPrefix}");
        }
        var summary =
            $"status={read.Status},states=[{string.Join("|", stateSummaries)}],"
            + $"data=[{string.Join("|", dataSummaries)}],"
            + $"satisfiableSlot={hasSatisfiableSlot},authenticatingPrefix={hasAuthenticatingPrefix}";
        return new AdoptionOracle(
            hasSatisfiableSlot,
            hasAuthenticatingPrefix,
            !hasAdoptableSlot && hasCurrentHeader,
            summary);
    }

    private async Task<SuperblockReadResult> ReadSuperblock(
        FakeStorageBackend backend,
        string basePath)
    {
        if (!backend.Exists(basePath + ".kvs"))
            return new SuperblockReadResult(SuperblockStatus.Missing, []);

        await using var file = await backend.Open(basePath + ".kvs").ConfigureAwait(false);
        using var superblock = new Superblock(_encryptionKey, FormatVersion);
        return await superblock.Read(file).ConfigureAwait(false);
    }

    private bool TryReadCurrentData(
        FakeStorageBackend backend,
        string basePath,
        int slot,
        [NotNullWhen(true)] out DataImage? data)
    {
        var path = DataPath(basePath, slot);
        if (!backend.Exists(path)) {
            data = null;
            return false;
        }

        var bytes = backend.GetBytes(path);
        if (bytes.Length < KvasarConstants.SegmentHeaderSize) {
            data = null;
            return false;
        }

        try {
            var header = SegmentHeader.Read(bytes);
            var isCurrent = header.FormatVer == FormatVersion
                && header.PageSize == PageSize
                && header.Flags == KvasarConstants.EncryptedDataFileFlag;
            data = isCurrent ? new DataImage(bytes, header) : null;
            return isCurrent;
        }
        catch (KvasarCorruptException) {
            data = null;
            return false;
        }
    }

    private bool AuthenticatesCommitWindow(
        SuperblockState state,
        SuperblockState? previous,
        DataImage data)
    {
        var floor = state.DataAuthenticationFloor;
        if (previous is { } predecessor
            && predecessor.DataSlot == state.DataSlot
            && predecessor.DataCommitLength >= KvasarConstants.SegmentHeaderSize
            && predecessor.DataCommitLength <= state.DataCommitLength)
            floor = Math.Max(floor, predecessor.DataCommitLength);

        var onDiskPageSize = data.Header.PageSize + KvasarConstants.GcmTagSize;
        var fromPageId = (floor - KvasarConstants.SegmentHeaderSize) / onDiskPageSize;
        var toPageId = (state.DataCommitLength - KvasarConstants.SegmentHeaderSize) / onDiskPageSize;
        for (var pageId = fromPageId; pageId < toPageId; pageId++)
            if (!AuthenticatesPage(data, pageId))
                return false;

        return true;
    }

    private bool AuthenticatesPage(DataImage data, long pageId)
    {
        var onDiskPageSize = data.Header.PageSize + KvasarConstants.GcmTagSize;
        var offset = KvasarConstants.SegmentHeaderSize + (pageId * onDiskPageSize);
        if (offset < 0 || offset > data.Bytes.LongLength - onDiskPageSize)
            return false;

        var pageKey = new byte[KvasarConstants.PageKeySize];
        KeyDerivations.HkdfSha256.Derive(
            _encryptionKey, [], KvasarConstants.PageKeyInfo, pageKey);
        using var factory = new AesGcmPageCipherFactory(pageKey, FormatVersion);
        using var cipher = (AesGcmPageCipher)factory.Create(data.Header.FileSalt);
        try {
            cipher.Decrypt(
                pageId,
                data.Bytes.AsSpan((int)offset, onDiskPageSize),
                new byte[data.Header.PageSize]);
            return true;
        }
        catch (KvasarCorruptException) {
            return false;
        }
    }

    private static bool IsSatisfiable(
        SuperblockState state,
        SegmentHeader header,
        int physicalLength)
    {
        var onDiskPageSize = header.PageSize + KvasarConstants.GcmTagSize;
        var bodyLength = state.DataCommitLength - KvasarConstants.SegmentHeaderSize;
        return bodyLength >= 0
            && bodyLength % onDiskPageSize == 0
            && state.DataCommitLength <= physicalLength + onDiskPageSize;
    }

    private static async Task<Dictionary<string, byte[]>> Scan(KvasarStore store, string context)
    {
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await foreach (var (key, value) in store.Scan().ConfigureAwait(false)) {
            var text = Encoding.UTF8.GetString(key.Span);
            contents.TryAdd(text, value.ToArray()).Should().BeTrue(
                $"{context}, invariant=scan-unique, key={text}");
        }
        return contents;
    }

    private static void AssertNoFabricatedValues(
        IReadOnlyDictionary<string, byte[]> contents,
        OracleModel model,
        string context)
    {
        foreach (var (key, value) in contents) {
            model.History.TryGetValue(key, out var known).Should().BeTrue(
                $"{context}, invariant=no-fabricated-value, unknown key={key}");
            known!.Any(candidate => candidate.AsSpan().SequenceEqual(value)).Should().BeTrue(
                $"{context}, invariant=no-fabricated-value, key={key}, length={value.Length}");
        }
    }

    private static void AssertCommittedDataSurvives(
        IReadOnlyDictionary<string, byte[]> contents,
        OracleTransition transition,
        string context)
    {
        foreach (var (key, value) in transition.LastCommitted) {
            if (transition.TouchedAfterCommit.Contains(key))
                continue;

            contents.TryGetValue(key, out var actual).Should().BeTrue(
                $"{context}, invariant=committed-survives, key={key}");
            actual.Should().Equal(value, $"{context}, invariant=committed-survives, key={key}");
        }
    }

    private static void AssertMonotonic(
        IReadOnlyDictionary<string, byte[]> contents,
        OracleTransition transition,
        string context)
    {
        foreach (var key in transition.Previous.Keys) {
            if (transition.Touched.Contains(key))
                continue;

            contents.TryGetValue(key, out var actual).Should().BeTrue(
                $"{context}, invariant=monotonic, key={key}");
        }
    }

    private KvasarOptions Options(string basePath, IStorageBackend backend)
        => new() {
            BasePath = basePath,
            EncryptionKey = _encryptionKey,
            PageSize = PageSize,
            PageCacheBytes = 32 * 1024,
            Durability = KvasarDurability.Flushed,
            FlushDelay = TimeSpan.FromHours(1),
            CommitBytes = long.MaxValue,
            CompactionMinBytes = long.MaxValue,
            StorageBackend = backend,
        };

    private static RunSettings ReadSettings()
    {
        var deepMode = Environment.GetEnvironmentVariable("KVASAR_CRASH_FUZZ_DEEP");
        var isDeep = string.Equals(deepMode, "1", StringComparison.Ordinal)
            || string.Equals(deepMode, "true", StringComparison.OrdinalIgnoreCase);
        var hasConfiguredSeed = int.TryParse(
            Environment.GetEnvironmentVariable("KVASAR_CRASH_FUZZ_SEED"),
            out var configuredSeed);
        var baseSeed = hasConfiguredSeed ? configuredSeed : DefaultSeed;
        var modes = isDeep
            ? CrashHarnessOptions.AllModes.Cast<CrashMode?>().Prepend(null).ToArray()
            : DefaultModes;
        return isDeep
            ? new RunSettings(5, 6, hasConfiguredSeed ? 1 : 4, 18, baseSeed, modes, true, hasConfiguredSeed)
            : new RunSettings(3, 3, hasConfiguredSeed ? 1 : 2, 8, baseSeed, modes, false, hasConfiguredSeed);
    }

    private static CrashMode? SelectMode(
        RunSettings settings,
        int modeIndex,
        int cycle,
        int phase)
        => settings.Modes[(modeIndex + (cycle * 2) + phase) % settings.Modes.Count];

    private static int SelectCrashPoint(int operationCount, int pointIndex, int pointCount)
    {
        if (operationCount <= 0)
            throw new InvalidOperationException("A crash phase must issue at least one storage operation.");
        if (pointCount == 1)
            return operationCount;

        return 1 + (int)((long)pointIndex * (operationCount - 1) / (pointCount - 1));
    }

    private static string FailureContext(
        int seed,
        int cycle,
        int workloadPoint,
        CrashMode? workloadMode,
        int recoveryPoint,
        CrashMode? recoveryMode,
        IReadOnlyList<string> sequence)
        => $"seed={seed}, cycle={cycle}, workloadCrashPoint={workloadPoint}, "
            + $"workloadMode={ModeName(workloadMode)}, recoveryCrashPoint={recoveryPoint}, "
            + $"recoveryMode={ModeName(recoveryMode)}, sequence={string.Join("; ", sequence)}";

    private static string FormatNotes(IEnumerable<WorkloadNote> notes)
        => string.Join(",", notes.Select(note => note.Kind switch {
            WorkloadKind.Set => $"set({note.Key},{note.Value!.Length},{note.IsAcknowledged})",
            WorkloadKind.Delete => $"delete({note.Key},{note.IsAcknowledged})",
            WorkloadKind.Flush => $"flush({note.IsAcknowledged})",
            WorkloadKind.Compact => $"compact({note.IsAcknowledged})",
            _ => throw new ArgumentOutOfRangeException(nameof(notes)),
        }));

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

    private static byte[] NewValue(int seed, int cycle, int operation, int length)
    {
        var value = new byte[length];
        new Random(DeriveSeed(seed, cycle + 701, operation)).NextBytes(value);
        return value;
    }

    private static string ModeName(CrashMode? mode)
        => mode is { } value ? value.ToString() : "ProcessKill";

    private static string DataPath(string basePath, int slot)
        => $"{basePath}.{slot}.kdat";

    private static string Key(int index)
        => $"key-{index:D2}";

    private static KvasarKey Utf8(string value)
        => Encoding.UTF8.GetBytes(value);

    // Nested types

    private sealed class Coverage
    {
        public int CycleRuns { get; set; }
        public int CrashEvents { get; private set; }
        public int MinWorkloadOperations { get; private set; } = int.MaxValue;
        public int MaxWorkloadOperations { get; private set; }
        public int MinRecoveryOperations { get; private set; } = int.MaxValue;
        public int MaxRecoveryOperations { get; private set; }

        public void ObserveWorkload(int operationCount)
        {
            CrashEvents++;
            MinWorkloadOperations = Math.Min(MinWorkloadOperations, operationCount);
            MaxWorkloadOperations = Math.Max(MaxWorkloadOperations, operationCount);
        }

        public void ObserveRecovery(int operationCount)
        {
            CrashEvents++;
            MinRecoveryOperations = Math.Min(MinRecoveryOperations, operationCount);
            MaxRecoveryOperations = Math.Max(MaxRecoveryOperations, operationCount);
        }
    }

    private sealed class OracleModel
    {
        private readonly HashSet<string> _unsettledKeys = new(StringComparer.Ordinal);

        public Dictionary<string, List<byte[]>> History { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Current { get; } = new(StringComparer.Ordinal);

        public void AddInitial(string key, byte[] value)
        {
            History[key] = [value];
            Current[key] = value;
        }

        public OracleTransition Apply(IReadOnlyList<WorkloadNote> notes)
        {
            var previous = Clone(Current);
            var working = Clone(Current);
            var lastCommitted = Clone(Current);
            var touched = new HashSet<string>(_unsettledKeys, StringComparer.Ordinal);
            var acknowledgedTouches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var note in notes) {
                if (note.Kind == WorkloadKind.Flush) {
                    if (note.IsAcknowledged) {
                        lastCommitted = Clone(working);
                        _unsettledKeys.ExceptWith(acknowledgedTouches);
                        acknowledgedTouches.Clear();
                    }
                    continue;
                }
                if (note.Kind == WorkloadKind.Compact)
                    continue;

                touched.Add(note.Key!);
                _unsettledKeys.Add(note.Key!);
                if (note.Value is { } value) {
                    if (!History.TryGetValue(note.Key!, out var known)) {
                        known = [];
                        History.Add(note.Key!, known);
                    }
                    known.Add(value);
                }
                if (!note.IsAcknowledged)
                    continue;

                acknowledgedTouches.Add(note.Key!);
                if (note.Value is null)
                    working.Remove(note.Key!);
                else
                    working[note.Key!] = note.Value;
            }
            return new OracleTransition(
                previous,
                lastCommitted,
                touched,
                new HashSet<string>(_unsettledKeys, StringComparer.Ordinal));
        }

        public void Resync(IReadOnlyDictionary<string, byte[]> contents)
        {
            Current.Clear();
            foreach (var (key, value) in contents)
                Current.Add(key, value);
        }

        private static Dictionary<string, byte[]> Clone(
            IReadOnlyDictionary<string, byte[]> source)
            => source.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
    }

    private sealed class WorkloadNote
    {
        public WorkloadKind Kind { get; }
        public string? Key { get; }
        public byte[]? Value { get; }
        public bool IsAcknowledged { get; set; }

        public static WorkloadNote Set(string key, byte[] value)
            => new(WorkloadKind.Set, key, value);

        public static WorkloadNote Delete(string key)
            => new(WorkloadKind.Delete, key, null);

        public static WorkloadNote Flush()
            => new(WorkloadKind.Flush, null, null);

        public static WorkloadNote Compact()
            => new(WorkloadKind.Compact, null, null);

        private WorkloadNote(WorkloadKind kind, string? key, byte[]? value)
        {
            Kind = kind;
            Key = key;
            Value = value;
        }
    }

    private sealed record OracleTransition(
        IReadOnlyDictionary<string, byte[]> Previous,
        IReadOnlyDictionary<string, byte[]> LastCommitted,
        IReadOnlySet<string> Touched,
        IReadOnlySet<string> TouchedAfterCommit);

    private sealed record DataImage(byte[] Bytes, SegmentHeader Header);

    private sealed record AdoptionOracle(
        bool HasSatisfiableSlot,
        bool HasAuthenticatingPrefix,
        bool MustUseFallback,
        string Summary);

    private enum WorkloadKind
    {
        Set = 0,
        Delete,
        Flush,
        Compact,
    }
}
