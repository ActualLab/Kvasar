using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Paging;

public class PagedSegmentTests
{
    private const int PageSize = 512;
    private const uint FormatVer = 3;

    public static IEnumerable<object[]> Overheads => new[] {
        new object[] { 0 },  // no-op / identity cipher
        new object[] { 16 }, // AES-GCM-like on-disk overhead
    };

    [Theory]
    [MemberData(nameof(Overheads))]
    public async Task CreateAppendReopenRoundTrips(int overhead)
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(overhead);
        var pages = MakePages(5);
        try {
            var cache = new PageCache(1 << 20);
            using (var seg = await PagedSegment.Create(path, segmentId: 11, PageSize, factory, FormatVer, cache)) {
                seg.SegmentId.Should().Be(11u);
                seg.PageSize.Should().Be(PageSize);
                seg.PageCount.Should().Be(0);

                for (var i = 0; i < pages.Count; i++)
                    (await seg.AppendPage(pages[i])).Should().Be(i);
                seg.PageCount.Should().Be(pages.Count);
                seg.FileByteLength.Should().Be(
                    KvasarConstants.SegmentHeaderSize + (long)pages.Count * (PageSize + overhead));

                // Zero-copy read from the writer's cache.
                for (var i = 0; i < pages.Count; i++)
                    (await seg.GetPage(i)).ToArray().Should().Equal(pages[i]);
                await seg.Flush(fsync: true);
            }

            // Reopen with a cold cache: bytes must come back through decrypt.
            var cache2 = new PageCache(1 << 20);
            using (var seg = await PagedSegment.Open(path, factory, FormatVer, cache2)) {
                seg.SegmentId.Should().Be(11u);
                seg.PageSize.Should().Be(PageSize);
                seg.PageCount.Should().Be(pages.Count);

                for (var i = 0; i < pages.Count; i++) {
                    (await seg.GetPage(i)).ToArray().Should().Equal(pages[i]);
                    var copy = new byte[PageSize];
                    await seg.ReadPage(i, copy);
                    copy.Should().Equal(pages[i]);
                }
            }
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OpenWithWrongFormatVerThrows()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(16);
        try {
            var cache = new PageCache(1 << 20);
            using (var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, cache))
                await seg.AppendPage(MakePages(1)[0]);

            var act = async () => await PagedSegment.Open(path, factory, FormatVer + 1, new PageCache(1 << 20));
            await act.Should().ThrowAsync<KvasarCorruptException>();
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AppendPageRejectsWrongLength()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(0);
        try {
            using var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
            var act = async () => await seg.AppendPage(new byte[PageSize - 1]);
            await act.Should().ThrowAsync<ArgumentException>();
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetPageOutOfRangeThrows()
    {
        var path = NewPath();
        var factory = new FakePageCipherFactory(0);
        try {
            using var seg = await PagedSegment.Create(path, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
            await seg.AppendPage(MakePages(1)[0]);
            var act = async () => await seg.GetPage(1);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }
        finally {
            File.Delete(path);
        }
    }

    private static List<byte[]> MakePages(int count)
    {
        var random = new Random(count * 7919);
        var pages = new List<byte[]>(count);
        for (var i = 0; i < count; i++) {
            var page = new byte[PageSize];
            random.NextBytes(page);
            pages.Add(page);
        }
        return pages;
    }

    private static string NewPath()
        => Path.Combine(Path.GetTempPath(), $"kvasar-paging-{Guid.NewGuid():N}.klog");
}
