using System.Linq;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Storage;

/// <summary>
/// Unwinds a workload the instant its crash point is reached. The device has already been crashed by
/// then, and every later device operation throws this too, so nothing on the way out can reach it.
/// </summary>
public sealed class CrashSignalException : Exception
{
    public CrashSignalException()
        : base("The crash point was reached.")
    { }

    public CrashSignalException(string? message)
        : base(message)
    { }

    public CrashSignalException(string? message, Exception? innerException)
        : base(message, innerException)
    { }
}

/// <summary>
/// What <see cref="CrashHarness"/> tries at each crash point, and the ceiling on the total number of
/// cases — crash points are subsampled evenly to fit under <see cref="MaxCases"/>.
/// </summary>
public sealed record CrashHarnessOptions
{
    public static readonly CrashMode[] AllModes = Enum.GetValues<CrashMode>();
    public static readonly CrashMode[] DefaultModes = [CrashMode.LoseAll, CrashMode.Torn, CrashMode.Reorder];

    public IReadOnlyList<CrashMode> Modes { get; init; } = DefaultModes;
    public int SeedCount { get; init; } = 3;
    public bool MustTestProcessKill { get; init; } = true;
    public int MaxCases { get; init; } = 3000;
}

/// <summary>
/// What a <see cref="CrashHarness"/> run actually executed. <see cref="CaseCount"/> is the number that
/// matters: a harness silently degenerating to a handful of cases is the failure mode to watch for.
/// </summary>
public sealed record CrashHarnessReport(
    int CaseCount,
    int CrashPointCount,
    int OperationCount,
    int VariantCount);

/// <summary>
/// One execution of a workload against a fake device: the <see cref="IStorageBackend"/> to use, a place
/// to record what the workload is about to do, and the setup/crash-window boundary.
/// </summary>
public sealed class CrashRun<TNote>
{
    private readonly CrashController _controller;
    private readonly List<TNote> _notes = [];

    public FakeStorageBackend Backend { get; }
    public IStorageBackend Storage { get; }
    public IReadOnlyList<TNote> Notes => _notes;

    internal CrashRun(FakeStorageBackend backend, CrashController controller)
    {
        Backend = backend;
        _controller = controller;
        Storage = new CrashStorageBackend(backend, controller);
    }

    public void Note(TNote note)
    {
        // Call this *before* the device operation it describes: an operation that reaches its crash point
        // still happened, so a note taken afterwards would go missing exactly when it matters most.
        _notes.Add(note);
    }

    public void ArmCrashPoints()
    {
        // Everything issued up to here is setup, not a candidate crash point.
        _controller.Arm();
    }
}

/// <summary>
/// A crashed device plus the notes the workload got to record before it died. <see cref="ToString"/> is
/// the reproduction recipe: crash point, mode and seed identify the case exactly.
/// </summary>
public sealed class CrashOutcome<TNote>(
    FakeStorageBackend backend,
    IReadOnlyList<TNote> notes,
    int crashPoint,
    CrashMode? mode,
    int seed)
{
    public FakeStorageBackend Backend { get; } = backend;
    public IReadOnlyList<TNote> Notes { get; } = notes;
    public int CrashPoint { get; } = crashPoint;
    // Null means a process kill: the OS and its page cache survive, so nothing unflushed is lost.
    public CrashMode? Mode { get; } = mode;
    public int Seed { get; } = seed;
    public bool IsProcessKill => Mode is null;
    public bool HasFsyncFailure { get; internal set; }

    public override string ToString()
        => $"crashPoint={CrashPoint}, mode={ModeName}, seed={Seed}";

    private string ModeName => Mode is { } mode ? mode.ToString() : "ProcessKill";
}

/// <summary>
/// The Level A crash driver (<c>docs/DESIGN-Durability.md</c> §9): runs a workload against a
/// <see cref="FakeStorageBackend"/>, then re-runs it once per (crash point, crash mode, seed), crashing
/// the device at that exact operation boundary and handing the wreckage to an invariant check.
/// </summary>
public static class CrashHarness
{
    public static async Task<CrashHarnessReport> Run<TNote>(
        Func<CrashRun<TNote>, Task> workload,
        Func<CrashOutcome<TNote>, Task> verify,
        CrashHarnessOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(verify);
        options ??= new CrashHarnessOptions();

        var (operationCount, wasArmed) = await Probe(workload).ConfigureAwait(false);
        if (operationCount == 0)
            throw new InvalidOperationException("The workload issued no device operation, so it has no crash point.");

        var variants = SelectVariants(options);
        var maxPoints = Math.Max(1, options.MaxCases / variants.Count);
        var crashPoints = SelectCrashPoints(operationCount, maxPoints);
        foreach (var crashPoint in crashPoints)
            foreach (var (mode, seed) in variants)
                await RunCase(
                        new FakeStorageBackend(), workload, verify, crashPoint, mode, seed, wasArmed)
                    .ConfigureAwait(false);

        return new CrashHarnessReport(
            crashPoints.Count * variants.Count,
            crashPoints.Count,
            operationCount,
            variants.Count);
    }

    public static async Task<int> CountCrashPoints<TNote>(
        FakeStorageBackend backend,
        Func<CrashRun<TNote>, Task> workload)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(workload);

        var (operationCount, _) = await Probe(workload, backend.Clone()).ConfigureAwait(false);
        return operationCount;
    }

    public static async Task RunCase<TNote>(
        Func<CrashRun<TNote>, Task> workload,
        Func<CrashOutcome<TNote>, Task> verify,
        int crashPoint,
        CrashMode? mode,
        int seed)
        => await RunCase(
                new FakeStorageBackend(), workload, verify, crashPoint, mode, seed)
            .ConfigureAwait(false);

    public static async Task RunCase<TNote>(
        FakeStorageBackend backend,
        Func<CrashRun<TNote>, Task> workload,
        Func<CrashOutcome<TNote>, Task> verify,
        int crashPoint,
        CrashMode? mode,
        int seed)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(workload);
        ArgumentNullException.ThrowIfNull(verify);

        var (_, wasArmed) = await Probe(workload, backend.Clone()).ConfigureAwait(false);
        await RunCase(
                backend, workload, verify, crashPoint, mode, seed, wasArmed)
            .ConfigureAwait(false);
    }

    // Private methods

    private static async Task RunCase<TNote>(
        FakeStorageBackend backend,
        Func<CrashRun<TNote>, Task> workload,
        Func<CrashOutcome<TNote>, Task> verify,
        int crashPoint,
        CrashMode? mode,
        int seed,
        bool mustWaitForArm)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(crashPoint, 1);

        var controller = new CrashController(backend, crashPoint, mode, seed, mustWaitForArm);
        var run = new CrashRun<TNote>(backend, controller);
        var outcome = new CrashOutcome<TNote>(backend, run.Notes, crashPoint, mode, seed);
        try {
            try {
                await workload(run).ConfigureAwait(false);
            }
            catch (CrashSignalException) {
                // Expected: the device was crashed the moment the crash point was reached.
            }
            if (!controller.IsCrashed)
                throw new InvalidOperationException("The workload ended before reaching its crash point.");

            outcome.HasFsyncFailure = controller.HasFsyncFailure;
            await verify(outcome).ConfigureAwait(false);
        }
        catch (Exception e) {
            var modeArgument = mode is { } value ? $"CrashMode.{value}" : "null";
            throw new InvalidOperationException(
                $"Crash case [{outcome}] failed: {e.Message}{Environment.NewLine}"
                + $"Reproduce it with CrashHarness.RunCase(workload, verify, {crashPoint}, {modeArgument}, {seed}).",
                e);
        }
    }

    private static async Task<(int OperationCount, bool WasArmed)> Probe<TNote>(
        Func<CrashRun<TNote>, Task> workload)
        => await Probe(workload, new FakeStorageBackend()).ConfigureAwait(false);

    private static async Task<(int OperationCount, bool WasArmed)> Probe<TNote>(
        Func<CrashRun<TNote>, Task> workload,
        FakeStorageBackend backend)
    {
        var controller = new CrashController(backend, 0, null, 0, false);
        await workload(new CrashRun<TNote>(backend, controller)).ConfigureAwait(false);
        return (controller.Count, controller.WasArmed);
    }

    private static List<(CrashMode? Mode, int Seed)> SelectVariants(CrashHarnessOptions options)
    {
        var variants = new List<(CrashMode? Mode, int Seed)>();
        if (options.MustTestProcessKill)
            variants.Add((null, 0));
        foreach (var mode in options.Modes) {
            // LoseAll drops every volatile write unconditionally, so extra seeds would repeat one case.
            var seedCount = mode is CrashMode.LoseAll or CrashMode.LyingFsync or CrashMode.FailingFsync
                ? 1
                : Math.Max(1, options.SeedCount);
            for (var seed = 0; seed < seedCount; seed++)
                variants.Add((mode, seed));
        }
        if (variants.Count == 0)
            throw new ArgumentException(
                "No crash mode and no process kill: there is nothing to test.", nameof(options));

        return variants;
    }

    private static List<int> SelectCrashPoints(int operationCount, int maxPoints)
    {
        if (operationCount <= maxPoints)
            return Enumerable.Range(1, operationCount).ToList();
        if (maxPoints <= 1)
            return [operationCount];

        // Evenly spaced, always including the first and the last boundary.
        var crashPoints = new List<int>(maxPoints);
        for (var i = 0; i < maxPoints; i++) {
            var crashPoint = 1 + (int)((long)i * (operationCount - 1) / (maxPoints - 1));
            if (crashPoints.Count == 0 || crashPoints[^1] != crashPoint)
                crashPoints.Add(crashPoint);
        }
        return crashPoints;
    }
}

// Counts device operations and fires the crash at the chosen boundary. A crash point is the boundary
// *behind* an operation: the operation lands, and only then does the device die.
internal sealed class CrashController
{
    private readonly FakeStorageBackend _backend;
    private readonly int _crashPoint;
    private readonly CrashMode? _mode;
    private readonly int _seed;
    private bool _isArmed;
    private int _count;

    public bool IsCrashed { get; private set; }
    public bool HasFsyncFailure { get; private set; }
    public bool WasArmed { get; private set; }
    public int Count => _count;

    public CrashController(
        FakeStorageBackend backend, int crashPoint, CrashMode? mode, int seed, bool mustWaitForArm)
    {
        _backend = backend;
        _crashPoint = crashPoint;
        _mode = mode;
        _seed = seed;
        // A workload that arms explicitly has a setup phase, and the probe run is what discovers that.
        // Until it arms, its operations are neither counted nor crashable.
        _isArmed = !mustWaitForArm;
    }

    public void Arm()
    {
        WasArmed = true;
        _isArmed = true;
        _count = 0;
    }

    public void ThrowIfCrashed()
    {
        if (IsCrashed)
            throw new CrashSignalException();
    }

    public void PrepareFlushToDisk()
    {
        if (!_isArmed || _count + 1 != _crashPoint)
            return;
        if (_mode is CrashMode.LyingFsync or CrashMode.FailingFsync)
            _backend.FlushFaultMode = _mode;
    }

    public bool IsSelectedFailingFsync()
        => _isArmed && _count + 1 == _crashPoint && _mode == CrashMode.FailingFsync;

    public void OnFlushToDiskFailure(IOException error)
    {
        _count++;
        _backend.FlushFaultMode = null;
        _backend.PowerLoss(CrashMode.FailingFsync, _seed);
        HasFsyncFailure = true;
        IsCrashed = true;
        throw new CrashSignalException("FlushToDisk failed at the selected crash point.", error);
    }

    public void OnOperation()
    {
        if (!_isArmed || ++_count != _crashPoint)
            return;

        _backend.FlushFaultMode = null;
        if (_mode is { } crashMode)
            _backend.PowerLoss(crashMode, _seed);
        else
            _backend.ProcessKill();
        IsCrashed = true;
        throw new CrashSignalException();
    }
}

internal sealed class CrashStorageBackend(FakeStorageBackend backend, CrashController controller) : IStorageBackend
{
    public async ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
    {
        controller.ThrowIfCrashed();
        return new CrashStorageFile(await backend.Open(path, cancellationToken).ConfigureAwait(false), controller);
    }

    public bool Exists(string path)
        => backend.Exists(path);

    public void Delete(string path)
    {
        controller.ThrowIfCrashed();
        backend.Delete(path);
    }

    public string[] ListFiles(string directoryPath, string searchPattern)
        => backend.ListFiles(directoryPath, searchPattern);
}

internal sealed class CrashStorageFile(IStorageFile file, CrashController controller) : IStorageFile
{
    public long Length => file.Length;

    public ValueTask DisposeAsync()
    {
        // Deliberately not forwarded: disposal runs while a crash unwinds the workload, and the fake's
        // files hold nothing that needs releasing.
        return default;
    }

    public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        => file.Read(offset, buffer, cancellationToken);

    public async ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
    {
        controller.ThrowIfCrashed();
        await file.Write(offset, buffer).ConfigureAwait(false);
        controller.OnOperation();
    }

    public async ValueTask FlushToDisk()
    {
        controller.ThrowIfCrashed();
        controller.PrepareFlushToDisk();
        try {
            await file.FlushToDisk().ConfigureAwait(false);
        }
        catch (IOException e) {
            if (!controller.IsSelectedFailingFsync())
                throw;

            controller.OnFlushToDiskFailure(e);
        }
        controller.OnOperation();
    }

    public async ValueTask Truncate(long length)
    {
        controller.ThrowIfCrashed();
        await file.Truncate(length).ConfigureAwait(false);
        controller.OnOperation();
    }
}
