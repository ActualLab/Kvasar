using System.Linq;
using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Storage;

// Level A crash coverage for proof steps (a), (b) and (c) of docs/DESIGN-Durability.md §6: the
// superblock is never flushed as part of a commit, so every one of these properties has to survive a
// crash landing in the middle of a slot write.
public class SuperblockCrashTests(ITestOutputHelper output)
{
    private const string Path = "kvasar/store.kvs";
    private const uint FormatVer = 3;
    private const int GenerationCount = 48;

    private static readonly byte[] MasterKey = MakeKey(0x11);
    private static readonly byte[] OtherKey = MakeKey(0x22);

    [Fact]
    public async Task RecoveredStateWasAlwaysWritten()
    {
        var report = await CrashHarness.Run<SuperblockEvent>(
            run => WriteGenerations(run, GenerationCount, 4),
            VerifyRecoveredState,
            new CrashHarnessOptions { SeedCount = 5 });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task CrashDuringAGenerationWriteLeavesThePreviousOneReadable()
    {
        // §6's fallback, and the reason the superblock needs no flush of its own: with every earlier
        // generation stable, the worst a torn or lost slot write can cost is one generation.
        var report = await CrashHarness.Run<SuperblockEvent>(
            run => WriteGenerations(run, GenerationCount, 1),
            VerifyPreviousGenerationSurvives,
            new CrashHarnessOptions {
                Modes = [CrashMode.LoseAll, CrashMode.Torn],
                SeedCount = 8,
                MustTestProcessKill = false,
            });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task ProcessKillLosesNoGeneration()
    {
        var report = await CrashHarness.Run<SuperblockEvent>(
            run => WriteGenerations(run, GenerationCount, 4),
            VerifyNothingIsLost,
            new CrashHarnessOptions { Modes = [], MustTestProcessKill = true });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(50);
    }

    [Fact]
    public async Task WrongKeyIsStillWrongKeyAfterAnyCrash()
    {
        // Corruption and a wrong key must never be confusable: one wipes the store, the other must not.
        var report = await CrashHarness.Run<SuperblockEvent>(
            run => WriteGenerations(run, GenerationCount, 4),
            VerifyWrongKeyIsReported,
            new CrashHarnessOptions { SeedCount = 5 });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(500);
    }

    // Private methods

    private static async Task WriteGenerations(CrashRun<SuperblockEvent> run, int count, int flushEvery)
    {
        var superblock = NewSuperblock();
        await using var file = await run.Storage.Open(Path);
        await superblock.Initialize(file);
        await file.FlushToDisk();
        run.ArmCrashPoints();

        for (var i = 1; i <= count; i++) {
            var state = NewState((ulong)i);
            run.Note(SuperblockEvent.Wrote(state));
            await superblock.Write(file, state);
            if (i % flushEvery != 0)
                continue;

            run.Note(SuperblockEvent.Flushed);
            await file.FlushToDisk();
        }
    }

    private static async Task VerifyRecoveredState(CrashOutcome<SuperblockEvent> outcome)
    {
        var result = await Read(outcome, MasterKey);
        // The header is stable before the crash window opens, so it always authenticates; that leaves
        // exactly two honest answers, and "a state nobody ever wrote" is not among them.
        result.Status.Should().BeOneOf(SuperblockStatus.Ok, SuperblockStatus.NoValidSlot);

        var written = WrittenStates(outcome.Notes);
        foreach (var state in result.States)
            written.Should().Contain(state, "the recovered state must be one the workload actually wrote");
        result.States.Select(x => x.Generation).Should().BeInDescendingOrder();

        var guaranteed = GuaranteedGeneration(outcome.Notes);
        if (guaranteed == 0)
            return;

        result.Status.Should().Be(SuperblockStatus.Ok);
        result.Newest!.Value.Generation.Should()
            .BeGreaterThanOrEqualTo(guaranteed, "generations must not go backwards past a stable slot");
    }

    private static async Task VerifyPreviousGenerationSurvives(CrashOutcome<SuperblockEvent> outcome)
    {
        var newestWritten = WrittenStates(outcome.Notes).Max(x => x.Generation);
        if (newestWritten < 2)
            return;

        var result = await Read(outcome, MasterKey);
        result.Status.Should().Be(SuperblockStatus.Ok);
        result.Newest!.Value.Generation.Should().BeGreaterThanOrEqualTo(newestWritten - 1);
        result.Newest!.Value.Generation.Should().BeLessThanOrEqualTo(newestWritten);
    }

    private static async Task VerifyNothingIsLost(CrashOutcome<SuperblockEvent> outcome)
    {
        var newestWritten = WrittenStates(outcome.Notes).Max(x => x.Generation);
        var result = await Read(outcome, MasterKey);
        result.Status.Should().Be(SuperblockStatus.Ok);
        result.Newest!.Value.Should().Be(NewState(newestWritten));
    }

    private static async Task VerifyWrongKeyIsReported(CrashOutcome<SuperblockEvent> outcome)
    {
        var result = await Read(outcome, OtherKey);
        result.Status.Should().Be(SuperblockStatus.WrongKey);
        result.States.Should().BeEmpty();
    }

    private static async Task<SuperblockReadResult> Read(CrashOutcome<SuperblockEvent> outcome, byte[] key)
    {
        await using var file = await outcome.Backend.Open(Path);
        return await new Superblock(key, FormatVer).Read(file);
    }

    private static HashSet<SuperblockState> WrittenStates(IReadOnlyList<SuperblockEvent> notes)
        => notes.Where(x => !x.IsFlush).Select(x => x.State).ToHashSet();

    // The newest generation that must still be readable: its slot write is stable, and no later write to
    // that same slot could have landed torn on top of it. A generation whose slot has since been written
    // again is not guaranteed — losing it is §6's fallback, not a violation.
    private static ulong GuaranteedGeneration(IReadOnlyList<SuperblockEvent> notes)
    {
        var stable = new ulong[Superblock.SlotCount];
        var pending = new ulong[Superblock.SlotCount];
        foreach (var note in notes) {
            if (note.IsFlush) {
                for (var slot = 0; slot < Superblock.SlotCount; slot++)
                    if (pending[slot] != 0) {
                        stable[slot] = pending[slot];
                        pending[slot] = 0;
                    }
                continue;
            }

            var target = (int)(note.State.Generation % Superblock.SlotCount);
            pending[target] = note.State.Generation;
            stable[target] = 0;
        }
        return Math.Max(stable[0], stable[1]);
    }

    private static Superblock NewSuperblock()
        => new(MasterKey, FormatVer);

    private static SuperblockState NewState(ulong generation)
        => new(generation, (byte)(generation % 2), (long)generation * 4096, 0, (long)generation * 24, 100, 20);

    private static byte[] MakeKey(byte seed)
    {
        var key = new byte[KvasarConstants.MasterKeySize];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(seed + i);
        return key;
    }

    // Nested types

    public sealed record SuperblockEvent(bool IsFlush, SuperblockState State)
    {
        public static readonly SuperblockEvent Flushed = new(true, default);

        public static SuperblockEvent Wrote(SuperblockState state)
            => new(false, state);
    }
}
