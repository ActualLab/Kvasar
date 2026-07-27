using System.Buffers.Binary;
using System.Security.Cryptography;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Internal;

internal readonly record struct LegacyRecord(
    byte[] Key,
    byte[] Value,
    KvasarValueKind ValueKind);

internal static class LegacyStoreImporter
{
    public static async ValueTask<LegacyRecord[]?> TryRead(
        KvasarOptions options,
        IStorageBackend storage,
        string superblockPath,
        string[] dataPaths,
        string[] indexPaths,
        ReadOnlyMemory<byte> indexMacKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(options.Version)
            || !uint.TryParse(options.FormatVersion, out var formatVer)
            || formatVer != KvasarConstants.DataFormatVersion
            || !storage.Exists(superblockPath))
            return null;

        var superblockFile = await storage.Open(superblockPath, cancellationToken).ConfigureAwait(false);
        await using var superblockFileScope = superblockFile.ConfigureAwait(false);
        if (!await HasPreviousFormat(superblockFile, cancellationToken).ConfigureAwait(false))
            return null;

        using var superblock = new Superblock(
            options.EncryptionKey,
            KvasarConstants.PreviousDataFormatVersion,
            options.Kdf);
        var read = await superblock.Read(superblockFile, cancellationToken).ConfigureAwait(false);
        if (read.Status == SuperblockStatus.WrongKey)
            throw new KvasarKeyException(
                $"The store '{options.BasePath}' was created under a different encryption key.");
        if (read.Status != SuperblockStatus.Ok)
            return [];

        var cipherFactory = CreateCipherFactory(options);
        try {
            foreach (var state in read.States) {
                var records = await TryReadState(
                        storage,
                        dataPaths,
                        indexPaths,
                        state,
                        cipherFactory,
                        indexMacKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (records is not null)
                    return records;
            }
            return [];
        }
        finally {
            (cipherFactory as IDisposable)?.Dispose();
        }
    }

    private static async ValueTask<LegacyRecord[]?> TryReadState(
        IStorageBackend storage,
        string[] dataPaths,
        string[] indexPaths,
        SuperblockState state,
        IPageCipherFactory cipherFactory,
        ReadOnlyMemory<byte> indexMacKey,
        CancellationToken cancellationToken)
    {
        PagedFile? dataFile = null;
        try {
            var file = await storage.Open(dataPaths[state.DataSlot], cancellationToken).ConfigureAwait(false);
            var cache = new PageCache(16 * 1024 * 1024);
            dataFile = await PagedFile.Open(
                    file,
                    cipherFactory,
                    KvasarConstants.PreviousDataFormatVersion,
                    cache,
                    state.DataCommitLength,
                    1,
                    cancellationToken)
                .ConfigureAwait(false);
            var logicalLength = dataFile.CommittedPageCount * dataFile.PageSize;
            var indexed = await TryReadIndex(
                    storage,
                    indexPaths[state.IndexSlot],
                    state,
                    dataFile,
                    logicalLength,
                    indexMacKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (indexed is not null)
                return indexed;

            return await Scan(dataFile, logicalLength, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not KvasarKeyException) {
            return null;
        }
        finally {
            if (dataFile is not null)
                await dataFile.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask<LegacyRecord[]?> TryReadIndex(
        IStorageBackend storage,
        string indexPath,
        SuperblockState state,
        PagedFile dataFile,
        long logicalLength,
        ReadOnlyMemory<byte> indexMacKey,
        CancellationToken cancellationToken)
    {
        if (!storage.Exists(indexPath))
            return null;

        var index = await IndexLog.Open(
                await storage.Open(indexPath, cancellationToken).ConfigureAwait(false),
                KvasarConstants.PreviousDataFormatVersion,
                indexMacKey,
                state.IndexCommitLength,
                cancellationToken)
            .ConfigureAwait(false);
        await using var indexScope = index.ConfigureAwait(false);
        var snapshot = await index
            .Read(state.IndexCommitLength, state.Generation, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is not { } loaded
            || loaded.DataCommitLength < 0
            || loaded.DataCommitLength > logicalLength
            || loaded.Entries.Length == 0 && loaded.DataCommitLength == 0 && logicalLength != 0)
            return null;

        var records = new List<LegacyRecord>(loaded.Entries.Length);
        foreach (var entry in loaded.Entries) {
            if (entry.IsTombstone || entry.Locator.FileId != state.DataSlot + 1)
                continue;

            var read = await TryReadRecord(
                    dataFile,
                    entry.Locator.Offset,
                    logicalLength,
                    cancellationToken)
                .ConfigureAwait(false);
            if (read.IsFound && !read.View.IsTombstone)
                records.Add(Copy(read.View));
        }
        return records.ToArray();
    }

    private static async ValueTask<LegacyRecord[]> Scan(
        PagedFile dataFile,
        long logicalLength,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<KvasarKey, LegacyRecord>();
        var p = 0L;
        while (p < logicalLength) {
            int recordLength;
            try {
                recordLength = await TryReadRecordLength(
                        dataFile,
                        p,
                        logicalLength,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                break;
            }
            if (recordLength == 0) {
                p = (p / dataFile.PageSize + 1) * dataFile.PageSize;
                continue;
            }

            RecordRead read;
            try {
                read = await TryReadRecord(dataFile, p, logicalLength, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KvasarCorruptException) {
                read = default;
            }
            if (read.IsFound) {
                var key = new KvasarKey(read.View.Key.ToArray());
                if (read.View.IsTombstone)
                    records.Remove(key);
                else
                    records[key] = Copy(read.View);
            }
            p += recordLength;
        }
        return records.Values.ToArray();
    }

    private static async ValueTask<RecordRead> TryReadRecord(
        PagedFile dataFile,
        long offset,
        long logicalLength,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset >= logicalLength)
            return default;

        var pageSize = dataFile.PageSize;
        var pageId = offset / pageSize;
        var inPage = (int)(offset % pageSize);
        var firstPage = await dataFile.GetPage(pageId, cancellationToken).ConfigureAwait(false);
        if (!RecordCodec.TryReadHeader(firstPage.Span[inPage..], logicalLength - offset, out var totalLength))
            return default;
        if (inPage + totalLength <= pageSize)
            return RecordCodec.TryDecode(firstPage.Slice(inPage, totalLength), out var inline, out _)
                ? new RecordRead(true, inline, totalLength)
                : default;

        var buffer = new byte[totalLength];
        var copied = 0;
        var start = inPage;
        while (copied < totalLength) {
            var page = await dataFile.GetPage(pageId++, cancellationToken).ConfigureAwait(false);
            var count = Math.Min(page.Length - start, totalLength - copied);
            if (count <= 0)
                return default;
            page.Span.Slice(start, count).CopyTo(buffer.AsSpan(copied));
            copied += count;
            start = 0;
        }
        return RecordCodec.TryDecode(buffer, out var spanned, out _)
            ? new RecordRead(true, spanned, totalLength)
            : default;
    }

    private static async ValueTask<int> TryReadRecordLength(
        PagedFile dataFile,
        long offset,
        long logicalLength,
        CancellationToken cancellationToken)
    {
        var pageSize = dataFile.PageSize;
        var page = await dataFile.GetPage(offset / pageSize, cancellationToken).ConfigureAwait(false);
        var inPage = (int)(offset % pageSize);
        return RecordCodec.TryReadHeader(page.Span[inPage..], logicalLength - offset, out var totalLength)
            ? totalLength
            : 0;
    }

    private static async ValueTask<bool> HasPreviousFormat(
        IStorageFile file, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        return await file.TryReadExact(0, header, cancellationToken).ConfigureAwait(false)
            && header.AsSpan(0, 4).SequenceEqual(KvasarConstants.KSupMagic)
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) ==
                KvasarConstants.PreviousDataFormatVersion;
    }

    private static IPageCipherFactory CreateCipherFactory(KvasarOptions options)
    {
        if (options.DisableEncryption)
            return NoopPageCipherFactory.Instance;

        var pageKey = new byte[KvasarConstants.PageKeySize];
        try {
            (options.Kdf ?? KeyDerivations.HkdfSha256)
                .Derive(options.EncryptionKey, [], KvasarConstants.PageKeyInfo, pageKey);
            return new AesGcmPageCipherFactory(pageKey, KvasarConstants.PreviousDataFormatVersion);
        }
        finally {
            CryptographicOperations.ZeroMemory(pageKey);
        }
    }

    private static LegacyRecord Copy(RecordView view)
        => new(view.Key.ToArray(), view.Value.ToArray(), view.ValueKind);
}
