using System.Linq;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Tests.Paging;

namespace ActualLab.Kvasar.Tests.Storage;

// Level A crash coverage for proof step (f) of docs/DESIGN-Durability.md §6: recovery resumes at or
// beyond the physical end of the data file, so a page id — and therefore an AES-GCM nonce — is never
// issued a second time for bytes that survived the crash. That is review issue I1, a P0 crypto break.
public class PagedFileCrashTests(ITestOutputHelper output)
{
    private const string DataPath = "kvasar/store.0.kdat";
    private const uint FormatVer = 3;
    private const int PageSize = 512;
    private const int Overhead = 16;
    private const int OnDiskPageSize = PageSize + Overhead;
    private const int Rounds = 16;
    private const int PagesPerRound = 3;

    [Fact]
    public async Task NoSurvivingPageIdIsEverIssuedTwice()
    {
        var report = await CrashHarness.Run<PagedFileEvent>(
            run => AppendPages(run, Rounds, PagesPerRound),
            VerifyPageIdsAreNeverReused,
            new CrashHarnessOptions { SeedCount = 6 });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task CommittedPagesStayReadableAfterAnyCrash()
    {
        var report = await CrashHarness.Run<PagedFileEvent>(
            run => AppendPages(run, Rounds, PagesPerRound),
            VerifyCommittedPagesDecrypt,
            new CrashHarnessOptions { SeedCount = 6 });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(500);
    }

    [Fact]
    public async Task ATornTailNeverFailsAReopenAndBecomesDeadSpace()
    {
        var report = await CrashHarness.Run<PagedFileEvent>(
            run => AppendPages(run, Rounds, PagesPerRound),
            VerifyTornTailIsDeadSpace,
            new CrashHarnessOptions {
                Modes = [CrashMode.Torn],
                SeedCount = 12,
                MustTestProcessKill = false,
            });

        output.WriteLine($"{report}");
        report.CaseCount.Should().BeGreaterThan(300);
    }

    // Private methods

    private static async Task AppendPages(CrashRun<PagedFileEvent> run, int rounds, int pagesPerRound)
    {
        var factory = new FakePageCipherFactory(Overhead);
        var cache = new PageCache(1 << 20);
        var pagedFile = await PagedFile.Create(
            await run.Storage.Open(DataPath), 1, PageSize, factory, FormatVer, cache);
        await pagedFile.FlushToDisk();
        var commitLength = pagedFile.MarkCommitted();
        run.ArmCrashPoints();

        try {
            for (var round = 0; round < rounds; round++) {
                await Append(pagesPerRound);
                await pagedFile.Flush(); // the staged bytes reach the file, and are still volatile there
                await Append(1);
                await pagedFile.FlushToDisk();
                commitLength = pagedFile.MarkCommitted();
                run.Note(new PagedFileEvent.Committed(commitLength));
                if (round % 2 == 0)
                    continue;

                // A fresh incarnation mid-workload: ids must keep moving forward across a reopen too.
                await pagedFile.DisposeAsync();
                pagedFile = await PagedFile.Open(
                    await run.Storage.Open(DataPath), factory, FormatVer, cache, commitLength);
            }
        }
        finally {
            await pagedFile.DisposeAsync();
        }
        return;

        async Task Append(int count) {
            for (var i = 0; i < count; i++) {
                var page = MakePage(pagedFile.PageCount);
                run.Note(new PagedFileEvent.Appended(pagedFile.PageCount, page));
                await pagedFile.AppendPage(page);
            }
        }
    }

    private static async Task VerifyPageIdsAreNeverReused(CrashOutcome<PagedFileEvent> outcome)
    {
        var file = await outcome.Backend.Open(DataPath);
        var survivingPageCount = SurvivingPageCount(file.Length);
        // A page id whose ciphertext is even partially on disk must never be issued again: the nonce is
        // f(fileSalt, pageId), so a second plaintext under that id would reuse it. Ids whose bytes the
        // crash took with them are a different matter — nothing observable was ever sealed under them.
        var atRisk = outcome.Notes
            .OfType<PagedFileEvent.Appended>()
            .Select(x => x.PageId)
            .Where(x => x < survivingPageCount)
            .ToHashSet();

        var pagedFile = await PagedFile.Open(
            file, NewCipherFactory(), FormatVer, new PageCache(1 << 20), LastCommitLength(outcome.Notes));
        try {
            pagedFile.ResumePageId.Should().BeGreaterThanOrEqualTo(survivingPageCount);
            await AppendAndCheck(pagedFile, 4);
            await pagedFile.FlushToDisk();
        }
        finally {
            await pagedFile.DisposeAsync();
        }

        // One more incarnation, so the accumulated set spans every life the file has had.
        pagedFile = await PagedFile.Open(
            await outcome.Backend.Open(DataPath), NewCipherFactory(), FormatVer, new PageCache(1 << 20));
        try {
            await AppendAndCheck(pagedFile, 2);
        }
        finally {
            await pagedFile.DisposeAsync();
        }
        return;

        async Task AppendAndCheck(PagedFile target, int count) {
            for (var i = 0; i < count; i++) {
                var pageId = await target.AppendPage(MakePage(-1 - i));
                atRisk.Should().NotContain(pageId, "a page id whose ciphertext survived was issued again");
                atRisk.Add(pageId);
            }
        }
    }

    private static async Task VerifyCommittedPagesDecrypt(CrashOutcome<PagedFileEvent> outcome)
    {
        var pages = outcome.Notes
            .OfType<PagedFileEvent.Appended>()
            .ToDictionary(x => x.PageId, x => x.Page);
        var commitLength = LastCommitLength(outcome.Notes);
        var file = await outcome.Backend.Open(DataPath);

        await using var pagedFile = await PagedFile.Open(
            file, NewCipherFactory(), FormatVer, new PageCache(1 << 20), commitLength);
        pagedFile.CommitLength.Should().Be(commitLength);
        for (var pageId = 0L; pageId < pagedFile.CommittedPageCount; pageId++)
            (await pagedFile.GetPage(pageId)).ToArray().Should()
                .Equal(pages[pageId], "a durable committed page must still decrypt");
    }

    private static async Task VerifyTornTailIsDeadSpace(CrashOutcome<PagedFileEvent> outcome)
    {
        var file = await outcome.Backend.Open(DataPath);
        // Deliberately without a committed extent: the floor/ceil split alone must absorb a torn tail.
        await using var pagedFile = await PagedFile.Open(file, NewCipherFactory(), FormatVer, new PageCache(1 << 20));
        pagedFile.PageCount.Should().BeGreaterThanOrEqualTo(pagedFile.CommittedPageCount);
        pagedFile.PageCount.Should().BeLessThanOrEqualTo(pagedFile.CommittedPageCount + 1);
        pagedFile.ResumePageId.Should().Be(pagedFile.PageCount);

        // The torn page is dead space: failing to authenticate is fine, being unreferenced is the point,
        // and neither may take the reopen down.
        for (var pageId = pagedFile.CommittedPageCount; pageId < pagedFile.PageCount; pageId++)
            try {
                _ = await pagedFile.GetPage(pageId);
            }
            catch (KvasarCorruptException) {
                // Expected: authentication is exactly what turns a torn page into dead space.
            }

        var resumePageId = pagedFile.ResumePageId;
        var page = MakePage(-1);
        var appendedId = await pagedFile.AppendPage(page);
        appendedId.Should().Be(resumePageId);
        await pagedFile.FlushToDisk();
        (await pagedFile.GetPage(appendedId)).ToArray().Should().Equal(page);
    }

    private static FakePageCipherFactory NewCipherFactory()
        => new(Overhead);

    private static long SurvivingPageCount(long fileLength)
    {
        var bodyLength = Math.Max(0, fileLength - KvasarConstants.SegmentHeaderSize);
        return (bodyLength + OnDiskPageSize - 1) / OnDiskPageSize;
    }

    private static long LastCommitLength(IReadOnlyList<PagedFileEvent> notes)
        => notes.OfType<PagedFileEvent.Committed>().LastOrDefault()?.CommitLength
            ?? KvasarConstants.SegmentHeaderSize;

    private static byte[] MakePage(long seed)
    {
        var page = new byte[PageSize];
        new Random((int)seed * 7919).NextBytes(page);
        return page;
    }

    // Nested types

    public abstract record PagedFileEvent
    {
        public sealed record Appended(long PageId, byte[] Page) : PagedFileEvent;
        public sealed record Committed(long CommitLength) : PagedFileEvent;
    }
}
