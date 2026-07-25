using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Paging;

// Covers the write-behind staging buffer and the readahead path added to amortize per-I/O async cost.
// The invariant under test: staging must never change what a reader observes, only when bytes hit disk.
public class PagedSegmentWriteBehindTests
{
    private const int PageSize = 4096;
    private const uint FormatVer = 1;
    // MaxPendingBytes is 1 MiB, so a 4 KiB page size stages 256 pages before an automatic flush.
    private const int PagesPerFlush = 256;

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public async Task StagedPagesAreReadableBeforeFlush(int overhead)
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(overhead);
        var pages = MakePages(4);
        try {
            // A tiny cache guarantees the staged plaintext is evicted, so reads must fall back to the
            // staging map rather than to disk (where the bytes do not exist yet).
            using var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(0));
            foreach (var page in pages)
                await seg.AppendPage(page);

            seg.PageCount.Should().Be(pages.Count);
            for (var i = 0; i < pages.Count; i++)
                (await seg.GetPage(i)).ToArray().Should().Equal(pages[i]);
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task StagedPagesSurviveDisposeWithoutFlush()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(3);
        try {
            using (var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
                foreach (var page in pages)
                    await seg.AppendPage(page);
            } // No explicit Flush: Dispose must still persist the staged pages.

            using var reopened = await PagedSegment.Open(path, factory, FormatVer, new PageCache(1 << 20));
            reopened.PageCount.Should().Be(pages.Count);
            for (var i = 0; i < pages.Count; i++)
                (await reopened.GetPage(i)).ToArray().Should().Equal(pages[i]);
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task AppendsBeyondStagingCapacityFlushAutomatically()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(16);
        var count = PagesPerFlush + 5; // forces at least one automatic mid-append flush
        var pages = MakePages(count);
        try {
            using (var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
                foreach (var page in pages)
                    await seg.AppendPage(page);
                seg.PageCount.Should().Be(count);
                await seg.Flush(fsync: false);
            }

            // Every page must be present and correct, including the ones written by the automatic flush.
            using var reopened = await PagedSegment.Open(path, factory, FormatVer, new PageCache(1 << 20));
            reopened.PageCount.Should().Be(count);
            for (var i = 0; i < count; i++)
                (await reopened.GetPage(i)).ToArray().Should().Equal(pages[i]);
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task FileByteLengthCountsStagedPages()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(16);
        try {
            using var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
            var pages = MakePages(3);
            foreach (var page in pages)
                await seg.AppendPage(page);

            var expected = KvasarConstants.SegmentHeaderSize + 3L * (PageSize + 16);
            seg.FileByteLength.Should().Be(expected, "staged pages must not make the store look smaller");
            await seg.Flush(fsync: false);
            seg.FileByteLength.Should().Be(expected, "flushing must not double-count");
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task PrefetchWarmsCacheAndToleratesOutOfRange()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(8);
        try {
            using (var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
                foreach (var page in pages)
                    await seg.AppendPage(page);
                await seg.Flush(fsync: false);
            }

            var cache = new PageCache(1 << 20);
            using var reopened = await PagedSegment.Open(path, factory, FormatVer, cache);
            await reopened.Prefetch(0, pages.Count);

            // Prefetched pages must be resident, so the synchronous no-I/O probe now succeeds.
            for (var i = 0; i < pages.Count; i++) {
                reopened.TryGetCachedPage(i, out var cached).Should().BeTrue();
                cached.ToArray().Should().Equal(pages[i]);
            }

            // Best-effort: out-of-range or oversized requests must be silent no-ops, never throws.
            await reopened.Prefetch(pages.Count + 100, 16);
            await reopened.Prefetch(0, pages.Count * 10);
            await reopened.Prefetch(-1, 4);
            await reopened.Prefetch(0, 0);
        }
        finally {
            TryDelete(path);
        }
    }

    [Fact]
    public async Task PrefetchOverCorruptedPagesDoesNotThrow()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(16);
        try {
            using (var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
                foreach (var page in MakePages(4))
                    await seg.AppendPage(page);
                await seg.Flush(fsync: false);
            }
            // Corrupt page 2's trailer — the part FakePageCipher authenticates, so decrypt must reject it.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write)) {
                fs.Seek(KvasarConstants.SegmentHeaderSize + 2L * (PageSize + 16) + PageSize, SeekOrigin.Begin);
                fs.WriteByte(0xFF);
                fs.WriteByte(0xFE);
            }

            using var reopened = await PagedSegment.Open(path, factory, FormatVer, new PageCache(1 << 20));
            // Prefetch must swallow the bad page: it only warms the cache, so the normal read path stays
            // the single place that decides what corruption means.
            await reopened.Prefetch(0, 4);

            var act = async () => await reopened.GetPage(2);
            await act.Should().ThrowAsync<KvasarCorruptException>("the real read must still surface corruption");
            (await reopened.GetPage(0)).ToArray().Should().HaveCount(PageSize);
        }
        finally {
            TryDelete(path);
        }
    }

    private static List<byte[]> MakePages(int count)
    {
        var rnd = new Random(4242);
        var pages = new List<byte[]>(count);
        for (var i = 0; i < count; i++) {
            var page = new byte[PageSize];
            rnd.NextBytes(page);
            pages.Add(page);
        }
        return pages;
    }

    private static string NewPath()
        => Path.Combine(Path.GetTempPath(), "kvasar-wb-" + Guid.NewGuid().ToString("N") + ".klog");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }
}
