using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Paging;

public class PagedFileTests
{
    private const int PageSize = 512;
    private const uint FormatVer = 3;
    private const int HeaderSize = 64;

    public static IEnumerable<object[]> Overheads => new[] {
        new object[] { 0 },  // no-op / identity cipher
        new object[] { 16 }, // AES-GCM-like on-disk overhead
    };

    [Theory]
    [MemberData(nameof(Overheads))]
    public async Task CreateAppendReopenRoundTrips(int overhead)
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(overhead);
        var pages = MakePages(5);
        var onDiskPageSize = PageSize + overhead;

        await using (var pf = await PagedFile.Create(file, 11, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            pf.FileId.Should().Be(11u);
            pf.PageSize.Should().Be(PageSize);
            pf.PageCount.Should().Be(0);
            pf.ResumePageId.Should().Be(0);
            pf.CommitLength.Should().Be(HeaderSize);
            pf.CommittedPageCount.Should().Be(0);

            for (var i = 0; i < pages.Count; i++)
                (await pf.AppendPage(pages[i])).Should().Be(i);
            pf.PageCount.Should().Be(pages.Count);
            pf.Length.Should().Be(HeaderSize + (long)pages.Count * onDiskPageSize);

            for (var i = 0; i < pages.Count; i++)
                (await pf.GetPage(i)).ToArray().Should().Equal(pages[i]);
            await pf.FlushToDisk();
            pf.MarkCommitted().Should().Be(pf.Length);
        }

        // Reopen with a cold cache: bytes must come back through decrypt.
        await using (var pf = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20))) {
            pf.FileId.Should().Be(11u);
            pf.PageSize.Should().Be(PageSize);
            pf.PageCount.Should().Be(pages.Count);
            pf.CommittedPageCount.Should().Be(pages.Count);

            for (var i = 0; i < pages.Count; i++) {
                (await pf.GetPage(i)).ToArray().Should().Equal(pages[i]);
                var copy = new byte[PageSize];
                await pf.ReadPage(i, copy);
                copy.Should().Equal(pages[i]);
            }
        }
    }

    // The P0 regression test: PagedSegment floored PageCount on open, so the page id of a torn trailing
    // page was re-issued to the next append — and the AES-GCM nonce is f(fileSalt, pageId), so that
    // reuse leaks the XOR of two plaintexts. PagedFile rounds up instead, burning the id.
    [Fact]
    public async Task NeverRewindsPageIdsPastATornTail()
    {
        const int overhead = 16;
        const int onDiskPageSize = PageSize + overhead;
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(overhead);
        var pages = MakePages(4);

        await using (var pf = await PagedFile.Create(file, 3, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in pages)
                await pf.AppendPage(page);
            await pf.FlushToDisk();
        }

        // Crash mid-append: page 3 is half there.
        await file.Truncate(HeaderSize + 3L * onDiskPageSize + onDiskPageSize / 2);
        var commitLength = HeaderSize + 3L * onDiskPageSize;

        var newPage = MakePages(1)[0];
        long newPageId;
        await using (var pf = await PagedFile.Open(
            file, factory, FormatVer, new PageCache(1 << 20), commitLength)) {
            pf.PageCount.Should().Be(4);          // ceil: the torn page still owns its id
            pf.CommittedPageCount.Should().Be(3); // ... but is not committed data
            pf.ResumePageId.Should().Be(4);

            newPageId = await pf.AppendPage(newPage);
            newPageId.Should().BeGreaterThan(3);  // strictly past every id ever issued before
            await pf.FlushToDisk();
        }

        await using (var pf = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20))) {
            (await pf.GetPage(newPageId)).ToArray().Should().Equal(newPage);
            for (var i = 0; i < 3; i++)
                (await pf.GetPage(i)).ToArray().Should().Equal(pages[i]);

            // The burned page is dead space: present, unreferenced, and unauthenticated.
            var act = async () => await pf.GetPage(3);
            await act.Should().ThrowAsync<KvasarCorruptException>();
        }
    }

    [Fact]
    public async Task OpenWithoutCommitLengthFloorsCommittedPagesButCeilsPageIds()
    {
        const int overhead = 16;
        const int onDiskPageSize = PageSize + overhead;
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(overhead);

        await using (var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in MakePages(3))
                await pf.AppendPage(page);
            await pf.Flush();
        }
        await file.Truncate(HeaderSize + 2L * onDiskPageSize + 7);

        await using var reopened = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20));
        reopened.PageCount.Should().Be(3);
        reopened.CommittedPageCount.Should().Be(2);
        reopened.CommitLength.Should().Be(HeaderSize + 2L * onDiskPageSize);
        reopened.ResumePageId.Should().Be(3);
    }

    [Fact]
    public async Task ReopenAfterCleanCloseResumesAtTheNextPageId()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(3);

        await using (var pf = await PagedFile.Create(file, 5, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in pages)
                await pf.AppendPage(page);
            await pf.FlushToDisk();
            pf.MarkCommitted();
        }

        await using var reopened = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20));
        reopened.PageCount.Should().Be(3);
        reopened.ResumePageId.Should().Be(3);
        (await reopened.AppendPage(MakePages(1)[0])).Should().Be(3);
    }

    [Fact]
    public async Task StagedPagesAreReadableBeforeFlush()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(3);
        var cache = new PageCache(1 << 20);

        await using var pf = await PagedFile.Create(file, 2, PageSize, factory, FormatVer, cache);
        foreach (var page in pages)
            await pf.AppendPage(page);
        file.Length.Should().Be(HeaderSize); // nothing written yet

        // Evicting the cache must not lose a staged page: _pendingPlain is the second place to look.
        cache.DropSegment(2);
        for (var i = 0; i < pages.Count; i++) {
            pf.TryGetCachedPage(i, out var staged).Should().BeTrue();
            staged.ToArray().Should().Equal(pages[i]);
            (await pf.GetPage(i)).ToArray().Should().Equal(pages[i]);
        }

        await pf.Flush();
        file.Length.Should().Be(HeaderSize + (long)pages.Count * (PageSize + 16));
    }

    [Fact]
    public async Task WriteBehindBatchesPagesIntoOneWrite()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);

        await using var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
        var writeCount = file.WriteCount; // the header write
        foreach (var page in MakePages(64))
            await pf.AppendPage(page);
        file.WriteCount.Should().Be(writeCount);

        await pf.Flush();
        file.WriteCount.Should().Be(writeCount + 1);
    }

    [Fact]
    public async Task FlushDoesNotForceButFlushToDiskDoes()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);

        await using var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
        await pf.AppendPage(MakePages(1)[0]);
        await pf.Flush();
        file.FlushToDiskCount.Should().Be(0);

        await pf.FlushToDisk();
        file.FlushToDiskCount.Should().Be(1);
    }

    [Fact]
    public async Task CachedPagesAreServedWithoutIo()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(4);

        await using (var pf = await PagedFile.Create(file, 9, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in pages)
                await pf.AppendPage(page);
            await pf.FlushToDisk();
        }

        await using var reopened = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20));
        var readCount = file.ReadCount;
        (await reopened.GetPage(2)).ToArray().Should().Equal(pages[2]);
        file.ReadCount.Should().BeGreaterThan(readCount);

        readCount = file.ReadCount;
        for (var i = 0; i < 4; i++)
            (await reopened.GetPage(2)).ToArray().Should().Equal(pages[2]);
        file.ReadCount.Should().Be(readCount);
    }

    [Fact]
    public async Task PrefetchWarmsTheCache()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(6);

        await using (var pf = await PagedFile.Create(file, 4, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in pages)
                await pf.AppendPage(page);
            await pf.FlushToDisk();
        }

        await using var reopened = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20));
        await reopened.Prefetch(0, pages.Count);
        var readCount = file.ReadCount;
        for (var i = 0; i < pages.Count; i++)
            (await reopened.GetPage(i)).ToArray().Should().Equal(pages[i]);
        file.ReadCount.Should().Be(readCount);
    }

    [Fact]
    public async Task CommittedExtentIsIndependentOfPhysicalLength()
    {
        const int onDiskPageSize = PageSize + 16;
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(5);

        await using (var pf = await PagedFile.Create(file, 6, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in pages)
                await pf.AppendPage(page);
            await pf.FlushToDisk();
        }

        var commitLength = HeaderSize + 2L * onDiskPageSize;
        await using var reopened = await PagedFile.Open(
            file, factory, FormatVer, new PageCache(1 << 20), commitLength);
        reopened.CommitLength.Should().Be(commitLength);
        reopened.CommittedPageCount.Should().Be(2);
        reopened.PageCount.Should().Be(5);
        reopened.Length.Should().Be(HeaderSize + 5L * onDiskPageSize);

        // Physically present pages past the extent stay readable — they are just not committed data.
        (await reopened.GetPage(4)).ToArray().Should().Equal(pages[4]);

        reopened.MarkCommitted().Should().Be(reopened.Length);
        reopened.CommittedPageCount.Should().Be(5);
    }

    [Fact]
    public async Task MarkCommittedRejectsStagedPages()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);

        await using var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
        await pf.AppendPage(MakePages(1)[0]);
        var act = () => pf.MarkCommitted();
        act.Should().Throw<InvalidOperationException>();

        await pf.Flush();
        pf.MarkCommitted().Should().Be(pf.Length);
    }

    [Fact]
    public async Task RecycleStampsFreshSaltAndRestartsPageIds()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(4);
        var cache = new PageCache(1 << 20);

        await using var pf = await PagedFile.Create(file, 8, PageSize, factory, FormatVer, cache);
        foreach (var page in pages)
            await pf.AppendPage(page);
        await pf.FlushToDisk();
        pf.MarkCommitted();
        var oldSalt = SegmentHeader.Read(file.Snapshot()).FileSalt;
        var oldPage0 = file.Snapshot().AsSpan(HeaderSize, PageSize).ToArray();

        await pf.Recycle(9);
        pf.FileId.Should().Be(9u);
        pf.PageCount.Should().Be(0);
        pf.ResumePageId.Should().Be(0);
        pf.CommitLength.Should().Be(HeaderSize);
        pf.CommittedPageCount.Should().Be(0);
        file.Length.Should().Be(HeaderSize);

        var header = SegmentHeader.Read(file.Snapshot());
        header.SegmentId.Should().Be(9u);
        header.FileSalt.Should().NotEqual(oldSalt);

        // Pages written before recycling are gone, ids restart, and the same plaintext now seals
        // differently because the salt — and therefore the nonce space — changed.
        var act = async () => await pf.GetPage(0);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await pf.AppendPage(pages[0])).Should().Be(0);
        await pf.Flush();
        file.Snapshot().AsSpan(HeaderSize, PageSize).ToArray().Should().NotEqual(oldPage0);
        (await pf.GetPage(0)).ToArray().Should().Equal(pages[0]);
    }

    [Fact]
    public async Task RecycleDropsCachedPages()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(2);
        var cache = new PageCache(1 << 20);

        await using var pf = await PagedFile.Create(file, 12, PageSize, factory, FormatVer, cache);
        await pf.AppendPage(pages[0]);
        await pf.FlushToDisk();

        // Same file id on purpose: the cache key is unchanged, so only an explicit drop can prevent
        // the previous life's page 0 from being served for the new one.
        await pf.Recycle(12);
        await pf.AppendPage(pages[1]);
        (await pf.GetPage(0)).ToArray().Should().Equal(pages[1]);
        cache.TryGet(12, 0, out var cached).Should().BeTrue();
        cached.Should().Equal(pages[1]);
    }

    [Fact]
    public async Task OpenWithWrongFormatVerThrows()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);

        await using (var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            await pf.AppendPage(MakePages(1)[0]);
            await pf.Flush();
        }

        var act = async () => await PagedFile.Open(file, factory, FormatVer + 1, new PageCache(1 << 20));
        await act.Should().ThrowAsync<KvasarCorruptException>();
    }

    [Fact]
    public async Task OpenRejectsCommitLengthBeyondTheFile()
    {
        const int onDiskPageSize = PageSize + 16;
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);

        await using (var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            await pf.AppendPage(MakePages(1)[0]);
            await pf.Flush();
        }

        var act = async () => await PagedFile.Open(
            file, factory, FormatVer, new PageCache(1 << 20), HeaderSize + 2L * onDiskPageSize);
        await act.Should().ThrowAsync<KvasarCorruptException>();
    }

    [Fact]
    public async Task OpenRejectsUnalignedCommitLength()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);

        await using (var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            await pf.AppendPage(MakePages(1)[0]);
            await pf.Flush();
        }

        var act = async () => await PagedFile.Open(
            file, factory, FormatVer, new PageCache(1 << 20), HeaderSize + 17);
        await act.Should().ThrowAsync<KvasarCorruptException>();
    }

    [Fact]
    public async Task AppendPageRejectsWrongLength()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(0);

        await using var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
        var act = async () => await pf.AppendPage(new byte[PageSize - 1]);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetPageOutOfRangeThrows()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(0);

        await using var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20));
        await pf.AppendPage(MakePages(1)[0]);
        var act = async () => await pf.GetPage(1);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task DisposeFlushesStagedPages()
    {
        var file = new PagedFileTestFile();
        var factory = new FakePageCipherFactory(16);
        var pages = MakePages(2);

        await using (var pf = await PagedFile.Create(file, 1, PageSize, factory, FormatVer, new PageCache(1 << 20))) {
            foreach (var page in pages)
                await pf.AppendPage(page);
        }
        file.Length.Should().Be(HeaderSize + 2L * (PageSize + 16));

        await using var reopened = await PagedFile.Open(file, factory, FormatVer, new PageCache(1 << 20));
        (await reopened.GetPage(1)).ToArray().Should().Equal(pages[1]);
    }

    // Private methods

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

    // Nested types

    // A private in-memory IStorageFile, named to avoid colliding with the shared storage fakes.
    // Writes past the end zero-fill the gap, matching what a real file does — which is exactly the
    // situation the never-rewind rule creates when it skips a torn page.
    private sealed class PagedFileTestFile : IStorageFile
    {
        private readonly Lock _lock = new();
        private byte[] _bytes = [];
        private int _length;

        public long Length {
            get { lock (_lock) return _length; }
        }
        public int ReadCount { get; private set; }
        public int WriteCount { get; private set; }
        public int FlushToDiskCount { get; private set; }

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock) {
                ReadCount++;
                if (offset >= _length)
                    return new ValueTask<int>(0);

                var count = (int)Math.Min(buffer.Length, _length - offset);
                _bytes.AsSpan((int)offset, count).CopyTo(buffer.Span);
                return new ValueTask<int>(count);
            }
        }

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            lock (_lock) {
                WriteCount++;
                var end = (int)offset + buffer.Length;
                Grow(end);
                buffer.Span.CopyTo(_bytes.AsSpan((int)offset));
                _length = Math.Max(_length, end);
            }
            return default;
        }

        public ValueTask FlushToDisk()
        {
            lock (_lock)
                FlushToDiskCount++;
            return default;
        }

        public ValueTask Truncate(long length)
        {
            lock (_lock) {
                Grow((int)length);
                if (length < _length)
                    _bytes.AsSpan((int)length, _length - (int)length).Clear();
                _length = (int)length;
            }
            return default;
        }

        public ValueTask DisposeAsync() => default;

        public byte[] Snapshot()
        {
            lock (_lock)
                return _bytes.AsSpan(0, _length).ToArray();
        }

        private void Grow(int minLength)
        {
            if (_bytes.Length >= minLength)
                return;

            var bytes = new byte[Math.Max(minLength, Math.Max(1024, _bytes.Length * 2))];
            _bytes.AsSpan(0, _length).CopyTo(bytes);
            _bytes = bytes;
        }
    }
}
