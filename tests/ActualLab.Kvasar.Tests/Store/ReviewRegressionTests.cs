using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;
using ActualLab.Kvasar.Tests.Storage;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// Regression tests for review findings against the superblock store. Where the redesign made a
/// defect unrepresentable, the test pins the structural property that prevents it.
/// </summary>
public sealed class ReviewRegressionTests : IDisposable
{
    private const uint FormatVer = KvasarConstants.DataFormatVersion;
    private const int PageSize = 512;

    private static readonly string[] ClosedFileSet =
        ["store.kvs", "store.0.kdat", "store.1.kdat", "store.0.kidx", "store.1.kidx"];
    private static readonly string[] OpenFileSet = [.. ClosedFileSet, "store.lock"];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-review-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public ReviewRegressionTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 13 + 5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort cleanup */ }
    }

    // --- I1 StaleCleanMarkerNonceReuse (P0) --------------------------------

    [Fact]
    public async Task NoCleanMarkerIsWrittenAndTheFileSetSurvivesRepeatedUncleanOpens()
    {
        // I1: v1 wrote a <base>.clean marker on every graceful close but consumed it only when
        // FlushDelay > 0, so it decayed from "the last run closed cleanly" into "some run did" — the
        // safety roll was skipped and a (fileSalt, pageId) GCM nonce re-issued. The marker is gone;
        // nonce safety is the unconditional never-rewind rule instead (§5.2 step 6). The page-id half is
        // PagedFileCrashTests' job; what belongs here is that the marker never comes back, that the file
        // set stays fixed however often the store is reopened uncleanly, and that no unclean open rolls
        // the active file to a fresh one (I7's roll, whose only purpose was the same nonce safety).
        var options = Options();
        var salts = new List<string>();
        var tornTails = new List<(long Offset, byte[] Bytes)>();
        for (var round = 0; round < 5; round++) {
            await using (var store = await KvasarStore.Open(options)) {
                for (var i = 0; i < 40; i++)
                    await store.Set(K($"r{round}-k{i:D3}"), V(i, 200));
                await store.Flush();
                FileNames().Should().BeEquivalentTo(OpenFileSet);
            }
            FileNames().Should().BeEquivalentTo(ClosedFileSet);
            File.Exists(BasePath + ".clean").Should().BeFalse("the .clean marker is not part of the model");

            // What a killed writer leaves above the committed extent: a partial, unauthenticatable page.
            var state = await ReadSuperblock();
            var activePath = DataPath(state.DataSlot);
            salts.Add(FileSaltOf(activePath));
            var torn = NewBytes(137, round);
            tornTails.Add((new FileInfo(activePath).Length, torn));
            AppendBytes(activePath, torn);
        }

        salts.Distinct(StringComparer.Ordinal).Should().HaveCount(1,
            "an unclean open resumes above the torn tail instead of rolling to a file with a fresh salt");
        // Every torn tail is still byte-for-byte where it was left: its page id was burned rather than
        // re-issued, which is the property the .clean marker used to be responsible for.
        var activeData = await File.ReadAllBytesAsync(DataPath((await ReadSuperblock()).DataSlot));
        foreach (var (offset, bytes) in tornTails)
            activeData.AsSpan((int)offset, bytes.Length).ToArray().Should().Equal(bytes,
                "a page whose ciphertext survived must never be written under its nonce again");

        await using var reopened = await KvasarStore.Open(options);
        (await reopened.Get(K("r4-k039")))!.Value.ToArray().Should().Equal(V(39, 200));
    }

    // --- I2 ScanAbortsLaterSegments (P0) / §14.3 ---------------------------

    [Fact]
    public async Task AMidFileCorruptPageIsAMissForItsOwnKeysOnly()
    {
        // I2: in v1 one unparseable record ended the walk over *every* later segment, so a single bad
        // page discarded all the newer data behind it. With one data file and an index-driven open, a
        // page that fails its tag costs exactly the records on that page: every read path treats it as a
        // miss (§14.3), and the locators of the records after it are unaffected.
        const int total = 400;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var activePath = DataPath(state.DataSlot);
        var pageCount = PageCountOf(activePath);
        pageCount.Should().BeGreaterThan(40);
        CorruptPage(activePath, 8); // early, so almost every record sits *after* the damage

        await using (var store = await KvasarStore.Open(options)) {
            var missing = new List<int>();
            for (var i = 0; i < total; i++) {
                var got = await store.Get(K(i));
                if (got is null)
                    missing.Add(i);
                else
                    got.Value.ToArray().Should().Equal(V(i, 200));
            }
            missing.Should().NotBeEmpty("the corrupted page must really have broken something");
            missing.Should().HaveCountLessThan(8, "only the records on the broken page may be lost");
            missing.Max().Should().BeLessThan(total / 4, "nothing after the broken page may be discarded");

            // The scan path takes the same view: it skips the unreadable page and yields all the rest.
            var scanned = new List<string>();
            await foreach (var (key, value) in store.Scan()) {
                scanned.Add(key.AsString);
                value.Length.Should().Be(200);
            }
            scanned.Should().HaveCount(total - missing.Count);
        }
    }

    // --- I21 FullSizeTornPageWipesStore (P2) -------------------------------

    [Fact]
    public async Task AFullSizeUnauthenticatablePageDoesNotWipeTheStore()
    {
        // I21: a full-size page that cannot be authenticated used to throw out of the scan and reach
        // Open's wipe path, so one bad page cost 100% of the store. The walk now ends at that page and
        // recovery adopts the prefix, so the store survives with the records below it intact.
        const int total = 400;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var activePath = DataPath(state.DataSlot);
        var pageCount = PageCountOf(activePath);
        // Dropping the index is what forces the log walk — the path where the throw used to escape.
        foreach (var path in IndexPaths())
            File.Delete(path);
        CorruptPage(activePath, pageCount - 4);

        await using (var store = await KvasarStore.Open(options)) {
            var survivors = 0;
            for (var i = 0; i < total; i++) {
                if (await store.Get(K(i)) is { } got) {
                    got.ToArray().Should().Equal(V(i, 200));
                    survivors++;
                }
            }
            survivors.Should().BeGreaterThan(total / 2, "the store must not be wiped by one bad page");
            await store.Set(K("after"), V(1, 30));
            (await store.Get(K("after")))!.Value.ToArray().Should().Equal(V(1, 30));
        }
    }

    // --- I3 KidxDeltaTailMisalignment (P0) ---------------------------------

    [Fact]
    public async Task AnUncommittedFabricatedIndexDeltaNeverReachesScan()
    {
        const int total = 60;
        var options = Options();
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++) {
                var value = V(i, 60);
                await store.Set(K(i), value);
                oracle[$"k{i:D6}"] = value;
            }
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var indexPath = IndexPath(state.IndexSlot);
        var real = (await ReadIndex(state))!.Value.Entries[0];
        // A whole entry, so nothing about it is torn: it points at a real record under a hash no key has.
        var fabricated = real;
        fabricated.KeyHash ^= 0xA5A5_A5A5_A5A5_A5A5;
        AppendBytes(indexPath, MemoryMarshal.AsBytes<IndexEntry>([fabricated]).ToArray());

        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K("late"), V(7, 40));
            oracle["late"] = V(7, 40);
            await store.Flush();
        }

        await using (var store = await KvasarStore.Open(options)) {
            store.Stats.Entries.Should().Be(oracle.Count);
            var scanned = new List<string>();
            await foreach (var (key, value) in store.Scan()) {
                var name = key.AsString;
                oracle.Should().ContainKey(name);
                value.ToArray().Should().Equal(oracle[name]);
                scanned.Add(name);
            }
            scanned.Should().OnlyHaveUniqueItems();
            scanned.Should().BeEquivalentTo(oracle.Keys);
        }
    }

    [Fact]
    public async Task AnIndexPrefixShorterThanTheCommitRotatesInsteadOfAppendingOverAHole()
    {
        // §14.1 — I3 by its second route, and a live data-loss bug rather than a tidiness rule. When the
        // prefix recovery reads is shorter than the commit named, the replay covers the gap in RAM, but
        // appending the next session's deltas to that same file grows its length back past the commit
        // while its contents still have a hole. The open after that sees a long-enough prefix, skips the
        // replay and adopts an index missing every truncated entry. Rotating on "did recovery replay"
        // (not "was the open unclean") is what closes it.
        const int batchA = 120;
        const int batchB = 60;
        var options = Options();
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < batchA; i++) {
                var value = V(i, 50);
                await store.Set(K(i), value);
                oracle[$"k{i:D6}"] = value;
            }
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var indexPath = IndexPath(state.IndexSlot);
        var keptEntries = (state.IndexCommitLength - IndexLog.HeaderSize) / IndexLog.EntrySize / 2;
        keptEntries.Should().BeGreaterThan(0);
        Truncate(indexPath, IndexLog.HeaderSize + (keptEntries * IndexLog.EntrySize));

        await using (var store = await KvasarStore.Open(options)) {
            for (var i = batchA; i < batchA + batchB; i++) {
                var value = V(i, 50);
                await store.Set(K(i), value);
                oracle[$"k{i:D6}"] = value;
            }
            await store.Flush();
        }

        await using (var store = await KvasarStore.Open(options)) {
            foreach (var (name, value) in oracle) {
                var got = await store.Get(K(name));
                got.Should().NotBeNull($"'{name}' must survive an index prefix shorter than the commit named");
                got!.Value.ToArray().Should().Equal(value);
            }
        }
    }

    // --- I4 TombstoneResurrection (P0) -------------------------------------

    [Fact]
    public async Task CompactionLeavesNoRecordAnyDeletedKeyCouldBeResurrectedFrom()
    {
        // I4: v1 dropped a tombstone while the key's original record survived in an earlier segment, so
        // any later rebuild replayed it and the deleted key came back with its old value. Compaction is
        // total here, so after a pass the data files hold the live set and nothing else — there is no
        // earlier file left to resurrect anything from. Asserted on the raw bytes (encryption off) so
        // the claim is about what is on disk, not about what the index currently says.
        var options = Options(encrypt: false) with { CompactionMinBytes = 1 };
        var (live, deleted) = await BuildStoreWithDeletes(options);

        // Before: the deleted keys' records really are on disk — this is the state v1 rebuilt from.
        DataBytesContain(deleted[0]).Should().BeTrue();

        await using (var store = await KvasarStore.Open(options)) {
            await store.Compact();
            // A second pass, so the slot the first one drained is recycled (truncated) rather than
            // merely unreferenced: after this, neither data file holds a byte of the deleted keys.
            foreach (var name in live)
                await store.Set(K(name), V(1, 300));
            await store.Compact();
            await store.Flush();
        }

        foreach (var name in deleted)
            DataBytesContain(name).Should().BeFalse($"'{name}' must survive nowhere after a total compaction");

        await using (var reopened = await KvasarStore.Open(options)) {
            foreach (var name in deleted)
                (await reopened.Get(K(name))).Should().BeNull();
            foreach (var name in live)
                (await reopened.Get(K(name))).Should().NotBeNull();
        }
    }

    [Fact]
    public async Task DeletedKeysStayDeletedAfterACompactionAndARebuildFromData()
    {
        // The other half of I4: with no usable .kidx the store rebuilds by replaying the data, which is
        // exactly the path that resurrected the key in v1.
        var options = Options() with { CompactionMinBytes = 1 };
        var (live, deleted) = await BuildStoreWithDeletes(options);

        await using (var store = await KvasarStore.Open(options)) {
            await store.Compact();
            await store.Flush();
        }
        foreach (var path in IndexPaths())
            File.Delete(path);

        await using var rebuilt = await KvasarStore.Open(options);
        foreach (var name in deleted)
            (await rebuilt.Get(K(name))).Should().BeNull($"'{name}' was deleted before the compaction");
        foreach (var name in live)
            (await rebuilt.Get(K(name))).Should().NotBeNull($"'{name}' was never deleted");
    }

    /// <summary>
    /// Regression for C3, found by this suite and not by any review pass. A store that does not persist
    /// index entries used to read back <b>completely empty</b> on the next open: the entry-less
    /// checkpoint was stamped at the committed extent, so recovery replayed nothing and adopted it.
    /// An entry-less checkpoint is consistent with offset 0, so that is what it is stamped at now.
    /// Reached through <see cref="IndexEncryption.Auto"/> plus a non-keyed hasher — the other half of
    /// the condition, now that <see cref="IndexEncryption.On"/> is rejected outright (R15).
    /// </summary>
    [Fact]
    public async Task AnUnpersistedIndexStillRebuildsFromTheLog()
    {
        var options = Options() with {
            IndexEncryption = IndexEncryption.Auto,
            Hasher = KeyHashers.XxHash3,
        };
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < 50; i++)
                await store.Set(K(i), V(i, 100));
            await store.Flush();
            store.Stats.Entries.Should().Be(50);
        }

        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < 50; i++)
            (await reopened.Get(K(i)))!.Value.ToArray().Should().Equal(V(i, 100));
    }

    // --- R5 AdoptionAuthenticatesCommitWindow (P0) -------------------------

    [Fact]
    public async Task AnIndexLessRebuildRecoversPastACorruptPage_KnownGap()
    {
        // A rebuild is best-effort even though adoption validates the candidate commit window strictly.
        const int total = 400;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var state = await ReadSuperblock();
        foreach (var path in IndexPaths())
            File.Delete(path);
        CorruptPage(DataPath(state.DataSlot), 8);

        await using var reopened = await KvasarStore.Open(options);
        var missing = new List<int>();
        for (var i = 0; i < total; i++) {
            if (await reopened.Get(K(i)) is null)
                missing.Add(i);
        }
        missing.Should().HaveCountLessThan(8, "only the records on the broken page may be lost");
    }

    [Fact]
    public async Task ATornPageInTheNewestCommitWindowFallsBackToTheOlderSuperblock()
    {
        const int baselineCount = 40;
        const int newestCount = 40;
        var options = Options() with {
            FlushDelay = TimeSpan.FromHours(1),
            CommitBytes = long.MaxValue,
        };
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < baselineCount; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
            for (var i = baselineCount; i < baselineCount + newestCount; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        await using var superblockFile = await FileStorageBackend.Instance.Open(KvsPath);
        var read = await new Superblock(_key, FormatVer).Read(superblockFile);
        read.States.Should().HaveCount(2);
        var newest = read.States[0];
        var older = read.States[1];
        newest.DataSlot.Should().Be(older.DataSlot);
        newest.DataCommitLength.Should().BeGreaterThan(older.DataCommitLength);
        var firstAddedPage = (older.DataCommitLength - KvasarConstants.SegmentHeaderSize)
            / OnDiskPageSize(encrypt: true);
        CorruptPage(DataPath(newest.DataSlot), firstAddedPage);

        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < baselineCount; i++)
            (await reopened.Get(K(i)))!.Value.ToArray().Should().Equal(V(i, 200));
        for (var i = baselineCount; i < baselineCount + newestCount; i++)
            (await reopened.Get(K(i))).Should().BeNull("the newest generation must be rejected as a unit");
    }

    // --- I9 WrongKeyAcceptedWhenActiveEmpty (P1) ---------------------------

    [Fact]
    public async Task AWrongKeyOnAnEmptyStoreThrowsWithoutTouchingAByte()
    {
        // I9: v1 authenticated page 0 of the active segment, which passes vacuously when that segment is
        // empty — so a wrong key was accepted, the .kidx trusted, and later writes mixed keys across
        // segments. The .kvs key check value answers on the file rather than its contents (§3.1), and
        // the wrong-key answer is the one case Open must never route into wipe-and-recreate. The header
        // bytes are the proof: they are written exactly once, so an unchanged KCV nonce means no wipe.
        var options = Options();
        await using (await KvasarStore.Open(options)) { } // created, never written to
        var headerBefore = ReadHeader(KvsPath);

        var wrongKey = new byte[32];
        for (var i = 0; i < wrongKey.Length; i++)
            wrongKey[i] = (byte)(i * 3 + 200);
        var open = async () => await KvasarStore.Open(options with { EncryptionKey = wrongKey });
        await open.Should().ThrowAsync<KvasarKeyException>();
        ReadHeader(KvsPath).Should().Equal(headerBefore, "a wrong key must not recreate the store");

        // ... and the empty store is still a working store under the right key.
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K("a"), V(1, 20));
            await store.Flush();
        }
        ReadHeader(KvsPath).Should().Equal(headerBefore);
        await open.Should().ThrowAsync<KvasarKeyException>();
        await using (var store = await KvasarStore.Open(options))
            (await store.Get(K("a")))!.Value.ToArray().Should().Equal(V(1, 20));
    }

    // --- I10 CheckpointHwmOutrunsLog (P1) ----------------------------------

    [Fact]
    public async Task TheIndexConsistencyPointNeverExceedsTheDurableDataExtent()
    {
        // I10: v1 snapshotted the index HWM without flushing the log, so a deferred-mode checkpoint
        // could stamp an extent past the durable .klog — recovery trusted it, never rescanned, and an
        // already-flushed value became permanently unreachable. Checked on *every* checkpoint rather
        // than on the final state: the stamp is only sound if it never once outran the bytes the data
        // file had already received. Deferred flushing plus overwrite rounds, so the delta tail
        // outgrows its checkpoint and the rotation really fires.
        const int keyCount = 200;
        var watchdog = new IndexStampWatchdog(FileStorageBackend.Instance, PageSize, OnDiskPageSize(encrypt: true));
        var options = Options() with {
            StorageBackend = watchdog,
            FlushDelay = TimeSpan.FromMilliseconds(20),
        };
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i, 60));
            for (var pass = 0; pass < 6; pass++) {
                for (var i = 0; i < keyCount; i++)
                    await store.Set(K(i), V(i + pass, 60));
                await store.Flush(); // several commits, so the rotation gets a chance to fire
            }
        }

        watchdog.CheckpointCount.Should().BeGreaterThan(1, "the rotation has to fire for this to test anything");
        watchdog.Violations.Should().BeEmpty(
            "an index consistency point must never name data the file has not received");

        // ... and the consequence: replaying from that stamp really does recover everything.
        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < keyCount; i++)
            (await reopened.Get(K(i))).Should().NotBeNull($"k{i:D6} was committed before the checkpoint");
    }

    // --- I20 RemoveSegmentThrowsOrphans (P2) / §3.4 ------------------------

    [Fact]
    public async Task NothingIsCreatedOrUnlinkedWhileTheStoreIsOpen()
    {
        // I20: v1 unlinked a drained segment, so on Windows an in-flight read made File.Delete fail
        // *after* the segment left the map — compaction aborted silently and left an orphan. Nothing is
        // unlinked at all now: slots are recycled in place, and the only dirent mutations happen at
        // store creation (§3.4). Clear is the documented exception and is deliberately not exercised.
        var backend = new RecordingStorageBackend(FileStorageBackend.Instance);
        var options = Options(encrypt: false) with {
            StorageBackend = backend,
            CompactionMinBytes = 1024,
            CompactionDeadRatio = 0.3,
        };
        await using (var store = await KvasarStore.Open(options)) {
            backend.Reset();
            for (var round = 0; round < 4; round++) {
                for (var i = 0; i < 200; i++)
                    await store.Set(K(i), V(i + round, 200));
                await store.Compact();
                await store.Flush();
            }
            backend.DeletedPaths.Should().BeEmpty("a compaction recycles a slot, it never unlinks one");
            backend.OpenedPaths.Should().BeEmpty("the five-file set is opened once, at open");
        }

        // The counter is live rather than vacuous: the wipe path really does reach Delete.
        backend.Reset();
        await using (await KvasarStore.Open(options with { Version = "next" })) { }
        backend.DeletedPaths.Should().NotBeEmpty();
    }

    // --- I22 ArbitraryCompactionVictim (P2) --------------------------------

    [Fact]
    public async Task CompactionIsTotalSoThereIsNoVictimToPick()
    {
        // I22: v1 drained whichever segment ConcurrentDictionary order happened to yield, which is what
        // widened I4's window. There is no choice to get wrong now — one active file, and a pass moves
        // its whole live set into the other slot. The observable form of "total" is that a single pass
        // takes dead bytes to zero; a per-victim model can only ever take out one segment's worth.
        // The ratio is set out of reach so no commit auto-compacts: the pass under test is the explicit
        // one below, and the dead bytes it finds are all of them.
        var options = Options() with { CompactionMinBytes = 1, CompactionDeadRatio = 0.99 };
        await using var store = await KvasarStore.Open(options);
        for (var i = 0; i < 300; i++)
            await store.Set(K(i), V(i, 150));
        for (var pass = 0; pass < 3; pass++)
            for (var i = 0; i < 300; i++)
                await store.Set(K(i), V(i + pass, 150));

        store.Stats.DeadBytes.Should().BeGreaterThan(store.Stats.LiveBytes);
        await store.Compact();
        store.Stats.DeadBytes.Should().Be(0, "one pass reclaims the whole store, not one victim's worth");
        store.Stats.Entries.Should().Be(300);
        for (var i = 0; i < 300; i++)
            (await store.Get(K(i)))!.Value.ToArray().Should().Equal(V(i + 2, 150));

        await store.Compact(); // nothing left to do, and doing it anyway must change nothing
        store.Stats.DeadBytes.Should().Be(0);
        DataPaths().Should().HaveCount(2);

        // A second pass recycles — and therefore truncates — the slot the first one drained. Only a third
        // of the keys are rewritten in between, so the rest are still whatever the first pass left them
        // pointing at: anything it failed to move would go down with that slot.
        for (var i = 0; i < 300; i += 3)
            await store.Set(K(i), V(i + 9, 150));
        await store.Compact();
        await store.Flush();
        for (var i = 0; i < 300; i++)
            (await store.Get(K(i)))!.Value.ToArray().Should()
                .Equal(i % 3 == 0 ? V(i + 9, 150) : V(i + 2, 150));
    }

    // --- I24 SegmentBytesOverflow (P2) -------------------------------------

    [Fact]
    public void ALocatorAddressesFarPastTheOldFourGiBCap()
    {
        // I24: the packed locator gave the offset 32 bits, so a file over 4 GiB threw OverflowException
        // out of Set mid-append instead of being rejected at Open. 48 bits puts the cap at 256 TiB,
        // which no device reaches — the failure mode is gone rather than moved.
        Locator.OffsetBits.Should().BeGreaterThanOrEqualTo(48);
        const long past4GiB = 5L * 1024 * 1024 * 1024;
        var locator = new Locator(2, past4GiB);
        locator.Offset.Should().Be(past4GiB);
        Locator.FromPacked(locator.Packed).Should().Be(locator);
        new Locator(1, Locator.MaxOffset).Offset.Should().Be(Locator.MaxOffset);

        var act = () => new Locator(1, Locator.MaxOffset + 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task FailedSuperblockWritesKeepRetryingTheSameSlot()
    {
        var backend = new FakeStorageBackend();
        var options = Options(encrypt: false) with { StorageBackend = backend };
        var store = await KvasarStore.Open(options);
        await store.Set(K("kept"), V(1, 200));
        backend.ProcessKill();

        var before = await ReadSuperblock(backend);
        var writeOffsets = new List<long>();
        backend.WriteFailure = (path, offset, _) => {
            if (!string.Equals(path, KvsPath, StringComparison.Ordinal) || offset < Superblock.HeaderSize)
                return null;
            writeOffsets.Add(offset);
            return 16;
        };

        var set = async () => await store.Set(K("uncommitted"), V(2, 200));
        await set.Should().ThrowAsync<IOException>();
        var retry = async () => await store.Flush();
        await retry.Should().ThrowAsync<IOException>();
        await store.DisposeAsync();

        writeOffsets.Should().NotBeEmpty();
        writeOffsets.Distinct().Should().ContainSingle("every retry must overwrite the already-torn slot");
        var after = await ReadSuperblock(backend);
        after.Generation.Should().Be(before.Generation);
        after.DataSlot.Should().Be(before.DataSlot);
        backend.WriteFailure = null;
        await using var reopened = await KvasarStore.Open(options);
        (await reopened.Get(K("kept")))!.Value.ToArray().Should().Equal(V(1, 200));
    }

    [Fact]
    public async Task FailedSlotSwitchCommitProtectsTheReferencedDataSlot()
    {
        var backend = new FakeStorageBackend();
        var options = Options(encrypt: false) with {
            StorageBackend = backend,
            CompactionMinBytes = long.MaxValue,
        };
        var store = await KvasarStore.Open(options);
        for (var i = 0; i < 80; i++)
            await store.Set(K(i), V(i, 200));
        for (var i = 0; i < 80; i++)
            await store.Set(K(i), V(i + 1, 200));
        backend.ProcessKill();

        var state = await ReadSuperblock(backend);
        var referencedPath = DataPath(state.DataSlot);
        backend.WriteFailure = (path, offset, _) =>
            string.Equals(path, KvsPath, StringComparison.Ordinal) && offset >= Superblock.HeaderSize
                ? 16
                : null;

        var first = async () => await store.Compact();
        await first.Should().ThrowAsync<IOException>();
        var referencedBytes = backend.GetBytes(referencedPath);

        var overwrite = async () => await store.Set(K(0), V(100, 200));
        await overwrite.Should().ThrowAsync<IOException>();
        var second = async () => await store.Compact();
        await second.Should().ThrowAsync<IOException>();
        backend.GetBytes(referencedPath).Should().Equal(
            referencedBytes, "a failed safety commit must stop compaction before the referenced slot is recycled");
        await store.DisposeAsync();
    }

    [Fact]
    public async Task ATruncatedInactiveDataSlotDoesNotVetoAdoption()
    {
        const int keyCount = 80;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var inactivePath = DataPath(1 - state.DataSlot);
        Truncate(inactivePath, 0);

        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < keyCount; i++)
            (await reopened.Get(K(i)))!.Value.ToArray().Should().Equal(V(i, 200));
        new FileInfo(inactivePath).Length.Should().BeGreaterThanOrEqualTo(KvasarConstants.SegmentHeaderSize);
    }

    [Fact]
    public async Task RolledBackIndexDeltasAreOverwrittenAtTheCommittedExtent()
    {
        const int indexBufferBytes = 1 << 16;
        var keyCount = indexBufferBytes / IndexLog.EntrySize;
        await CrashHarness.RunCase<int>(
            async run => {
                var options = Options(encrypt: false) with {
                    StorageBackend = run.Storage,
                    FlushDelay = TimeSpan.FromHours(1),
                    CommitBytes = long.MaxValue,
                    CompactionMinBytes = long.MaxValue,
                };
                await using var store = await KvasarStore.Open(options);
                for (var i = 0; i < keyCount; i++)
                    await store.Set(K(i), V(i, 8));
                await store.Flush();

                run.ArmCrashPoints();
                for (var i = 0; i < keyCount; i++)
                    await store.Set(K(i), V(i + keyCount, 8));
            },
            async outcome => {
                var state = await ReadSuperblock(outcome.Backend);
                outcome.Backend.GetBytes(IndexPath(state.IndexSlot)).LongLength.Should()
                    .BeGreaterThan(state.IndexCommitLength);
                var options = Options(encrypt: false) with {
                    StorageBackend = outcome.Backend,
                    CompactionMinBytes = long.MaxValue,
                };
                await using (var recovered = await KvasarStore.Open(options)) {
                    for (var i = 0; i < keyCount; i++)
                        (await recovered.Get(K(i)))!.Value.ToArray().Should().Equal(V(i, 8));
                    await recovered.Set(K("next-generation"), V(1, 8));
                }

                await using var reopened = await KvasarStore.Open(options);
                for (var i = 0; i < keyCount; i++)
                    (await reopened.Get(K(i)))!.Value.ToArray().Should().Equal(V(i, 8));
                (await reopened.Get(K("next-generation")))!.Value.ToArray().Should().Equal(V(1, 8));
            },
            1,
            null,
            0);
    }

    [Fact]
    public async Task ReopeningACompactedStoreDoesNotRearmCompaction()
    {
        const int keyCount = 200;
        var setupOptions = Options() with { CompactionMinBytes = long.MaxValue };
        await using (var store = await KvasarStore.Open(setupOptions)) {
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i, 512));
            for (var round = 0; round < 6; round++)
                for (var i = 0; i < keyCount; i++)
                    await store.Set(K(i), V(i + round + 1, 512));
            await store.Compact();
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i + 100, 512));
            await store.Flush();
        }

        var options = setupOptions with {
            CompactionMinBytes = 1,
            CompactionDeadRatio = 2.0 / 3.0,
        };
        var before = await ReadSuperblock();
        KvasarStats reopenedStats;
        await using (var reopened = await KvasarStore.Open(options)) {
            reopenedStats = reopened.Stats;
            await reopened.Set(K(0), V(1000, 512));
        }
        var after = await ReadSuperblock();

        var reopenedTotal = reopenedStats.LiveBytes + reopenedStats.DeadBytes;
        reopenedStats.DeadBytes.Should().BePositive();
        ((double)reopenedStats.DeadBytes / reopenedTotal).Should().BeLessThan(options.CompactionDeadRatio);
        after.DataSlot.Should().Be(before.DataSlot, "the first write after reopen must not rewrite the live set");
    }

    [Fact]
    public async Task RecoveryConsumesPersistedAccounting()
    {
        const int keyCount = 25;
        var options = Options() with { CompactionMinBytes = long.MaxValue };
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i, 200));
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i + 1, 200));
            await store.Flush();
        }

        var persisted = await ReadSuperblock();
        persisted.LiveBytes.Should().BePositive();
        persisted.DeadBytes.Should().BePositive();

        await using var reopened = await KvasarStore.Open(options);
        reopened.Stats.LiveBytes.Should().Be(persisted.LiveBytes);
        reopened.Stats.DeadBytes.Should().Be(persisted.DeadBytes);
    }

    [Fact]
    public async Task AccountingThatCannotDescribeTheExtentDoesNotWipeTheStore()
    {
        // Every store written before the counters were consumed persisted DataLog.DeadBytes, which sums
        // *both* slots — so any store that had compacted carries a DeadBytes far above the active slot's
        // extent. Adoption must degrade to deriving the accounting rather than reject the generation.
        const int keyCount = 25;
        var options = Options() with { CompactionMinBytes = long.MaxValue };
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < keyCount; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        // Both slots, because a legacy store persisted the inflated value on *every* commit — one bad
        // slot alone would just fall back to the other and prove nothing.
        var persisted = await ReadSuperblock();
        await using (var file = await FileStorageBackend.Instance.Open(KvsPath)) {
            var superblock = new Superblock(_key, FormatVer);
            for (var i = 1ul; i <= 2ul; i++)
                await superblock.Write(file, persisted with {
                    Generation = persisted.Generation + i,
                    DeadBytes = persisted.DeadBytes + (100L * 1024 * 1024),
                });
        }

        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < keyCount; i++)
            (await reopened.Get(K(i))).Should().NotBeNull($"key {i} must survive an unusable accounting pair");
    }

    [Fact]
    public async Task TwoRecoveryCyclesWithATornTailDoNotWipeTheStore()
    {
        // C1: a torn tail burns its page id and the commit that follows stamps an extent covering it
        // (§5.2.1), so that page can never authenticate again. Adoption used to authenticate a candidate's
        // whole extent whenever there was no comparable predecessor — which is exactly the fallback the
        // Buffered default relies on — so the second unclean open rejected both generations and wiped.
        const int total = 60;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        // First unclean cycle: tear the tail, reopen (recovery commits an extent over the burned page),
        // write a little more, close.
        AppendBytes(DataPath((await ReadSuperblock()).DataSlot), NewBytes(137, 11));
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = total; i < total + 10; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        // Now the canonical Buffered crash §5.2 step 3 exists for: the newest commit's bytes never
        // reached the device, so that generation is rejected and adoption falls back to the older one --
        // the path that has no predecessor to bound the window against.
        SuperblockState newest, older;
        await using (var file = await FileStorageBackend.Instance.Open(KvsPath)) {
            var read = await new Superblock(_key, FormatVer).Read(file);
            read.States.Length.Should().Be(2, "the fallback needs a second adoptable generation");
            (newest, older) = (read.States[0], read.States[1]);
        }
        newest.DataSlot.Should().Be(older.DataSlot);
        var activePath = DataPath(newest.DataSlot);
        // Exactly the older generation's extent: the newest is unadoptable, the older is intact.
        using (var f = new FileStream(activePath, FileMode.Open, FileAccess.Write))
            f.SetLength(older.DataCommitLength);

        await using var reopened = await KvasarStore.Open(options);
        var survivors = 0;
        for (var i = 0; i < total; i++)
            if (await reopened.Get(K(i)) is not null)
                survivors++;
        survivors.Should().BeGreaterThan(total / 2,
            "falling back to the older generation must not wipe a store whose torn tail burned a page");
    }

    [Fact]
    public async Task ATornTailFollowedByTwoCommitsDoesNotWipeTheStore()
    {
        // X1 probe: PagedFile.Length rounds PageCount *up*, so after a torn tail MarkCommitted publishes
        // an extent past the physical end. Recovery force-commits whenever BurnedBytes > 0, and the R4
        // safety loop can land a second such commit in the other slot before any page extends the file.
        const int total = 120;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var activePath = DataPath(state.DataSlot);
        AppendBytes(activePath, NewBytes(137, 3)); // a partial trailing page

        await using (var store = await KvasarStore.Open(options)) {
            await store.Flush();
            await store.Flush();
        }

        // The extent legitimately covers the burned partial page (§5.2.1 keeps its id from being
        // re-issued), so it can exceed the physical length by up to one page — Open tolerates exactly
        // that. What must never happen is the store becoming unadoptable because of it.
        var after = await ReadSuperblock();
        var physical = new FileInfo(DataPath(after.DataSlot)).Length;
        after.DataCommitLength.Should().BeLessThanOrEqualTo(physical + PageSize + KvasarConstants.GcmTagSize);

        var previousStates = await ReadSuperblockStates();
        for (var round = 0; round < 2; round++) {
            await using (var reopened = await KvasarStore.Open(options)) {
                var survivors = 0;
                for (var i = 0; i < total; i++)
                    if (await reopened.Get(K(i)) is not null)
                        survivors++;
                survivors.Should().Be(total, "a torn tail must never cost the whole store");
            }

            var currentStates = await ReadSuperblockStates();
            currentStates.Should().HaveCount(2);
            currentStates[0].Generation.Should().Be(previousStates[0].Generation + 1,
                "a read-only reopen must adopt and confirm the newest generation");
            currentStates[1].Generation.Should().Be(previousStates[0].Generation);
            currentStates[1].DataCommitLength.Should().Be(previousStates[0].DataCommitLength,
                "confirming the newest generation must not rewind its committed extent");
            previousStates = currentStates;
        }
    }

    [Fact]
    public async Task BurnCommitRemainsAdoptableWhenTheNewestSuperblockSlotTears()
    {
        const int total = 60;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var committed = await ReadSuperblock();
        AppendBytes(DataPath(committed.DataSlot), NewBytes(137, 17));

        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K("after-burn"), V(1, 200));
            await store.Flush();
        }

        SuperblockState newest;
        SuperblockState burnCommit;
        await using (var file = await FileStorageBackend.Instance.Open(KvsPath)) {
            var read = await new Superblock(_key, FormatVer).Read(file);
            read.States.Should().HaveCount(2);
            (newest, burnCommit) = (read.States[0], read.States[1]);
        }
        newest.DataSlot.Should().Be(burnCommit.DataSlot);
        burnCommit.DataCommitLength.Should().BeGreaterThan(committed.DataCommitLength);
        burnCommit.DataAuthenticationFloor.Should().Be(burnCommit.DataCommitLength);
        newest.DataCommitLength.Should().BeGreaterThan(burnCommit.DataCommitLength);
        newest.DataAuthenticationFloor.Should().Be(burnCommit.DataCommitLength);

        CorruptSuperblockSlot((int)(newest.Generation % Superblock.SlotCount));
        await using (var file = await FileStorageBackend.Instance.Open(KvsPath)) {
            var read = await new Superblock(_key, FormatVer).Read(file);
            read.States.Should().ContainSingle();
            read.States[0].Generation.Should().Be(burnCommit.Generation);
        }

        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < total; i++)
            (await reopened.Get(K(i))).Should().NotBeNull(
                $"key {i} committed before the torn tail must survive adoption of the burn commit");
    }

    [Fact]
    public async Task ACorruptPageAfterASlotSwitchIsAuthenticatedNotAdopted()
    {
        // R4/C3: the C1 fix stopped authenticating entirely whenever the two superblock candidates named
        // different data slots — which is exactly the first open after a compaction switch, the §5.2
        // step-3 path the guarantee is for. That slot is provably clean (BeginCompaction truncated it and
        // the pass wrote every page it names), so the whole extent is checkable and a damaged page there
        // must reject the generation rather than be adopted unvalidated.
        const int total = 120;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            for (var i = 0; i < total; i += 2)
                await store.Set(K(i), V(i + 1, 200)); // dead bytes, so Compact actually runs
            await store.Compact();
            await store.Flush();
        }

        SuperblockState newest, older;
        await using (var file = await FileStorageBackend.Instance.Open(KvsPath)) {
            var read = await new Superblock(_key, FormatVer).Read(file);
            read.States.Length.Should().Be(2);
            (newest, older) = (read.States[0], read.States[1]);
        }
        newest.DataSlot.Should().NotBe(older.DataSlot, "the compaction must have switched slots");

        // Damage a page well inside the switched-to slot's committed extent.
        CorruptPage(DataPath(newest.DataSlot), 2);

        await using var reopened = await KvasarStore.Open(options);
        var survivors = 0;
        for (var i = 0; i < total; i++)
            if (await reopened.Get(K(i)) is not null)
                survivors++;
        survivors.Should().Be(total,
            "a damaged page in the switched-to slot must reject that generation so adoption falls back "
            + "to the intact older one, rather than adopting it unvalidated and serving misses");
    }

    [Fact]
    public async Task WrongKeyWithAnUnreadableSuperblockNeverWipesAuthenticData()
    {
        const int count = 30;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < count; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }
        var paths = new[] { KvsPath, DataPath(0), DataPath(1), IndexPath(0), IndexPath(1) };
        var snapshots = paths.ToDictionary(x => x, File.ReadAllBytes);
        var wrongKey = Enumerable.Range(101, 32).Select(x => (byte)x).ToArray();
        var cases = new (string Name, Action Damage)[] {
            ("deleted", () => File.Delete(KvsPath)),
            ("empty", () => File.WriteAllBytes(KvsPath, [])),
            ("bad magic", () => FlipFileByte(KvsPath, 0)),
        };

        foreach (var testCase in cases) {
            foreach (var path in paths)
                File.WriteAllBytes(path, snapshots[path]);
            testCase.Damage();
            var filesBefore = paths.ToDictionary(
                x => x,
                x => File.Exists(x) ? File.ReadAllBytes(x) : null);

            var open = async () => {
                await using var store = await KvasarStore.Open(options with { EncryptionKey = wrongKey });
            };
            await open.Should().ThrowAsync<KvasarKeyException>(testCase.Name);
            foreach (var (path, bytes) in filesBefore) {
                File.Exists(path).Should().Be(bytes is not null, testCase.Name);
                if (bytes is not null)
                    File.ReadAllBytes(path).Should().Equal(bytes, testCase.Name);
            }

            await using var recovered = await KvasarStore.Open(options);
            for (var i = 0; i < count; i++)
                (await recovered.Get(K(i))).Should().NotBeNull($"{testCase.Name}, key {i}");
        }
    }

    [Fact]
    public async Task FlushedCommitFlushesTheSuperblockAfterWritingItsSlot()
    {
        var backend = new FakeStorageBackend();
        var options = Options() with {
            StorageBackend = backend,
            Durability = KvasarDurability.Flushed,
        };
        await using var store = await KvasarStore.Open(options);
        backend.ResetFlushCounts();

        await store.Set(K(1), V(1, 200));

        backend.GetFlushCount(KvsPath).Should().Be(1);
        (backend.GetFlushCount(DataPath(0)) + backend.GetFlushCount(DataPath(1)))
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExplicitPageSizeMismatchIsRejectedWithoutWiping()
    {
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K(1), V(1, 200));
            await store.Flush();
        }
        var snapshots = DataPaths().ToDictionary(x => x, File.ReadAllBytes);

        var open = async () => {
            await using var store = await KvasarStore.Open(options with { PageSize = PageSize * 2 });
        };
        await open.Should().ThrowAsync<ArgumentException>();
        foreach (var (path, bytes) in snapshots)
            File.ReadAllBytes(path).Should().Equal(bytes);

        await using var recovered = await KvasarStore.Open(options);
        (await recovered.Get(K(1))).Should().NotBeNull();
    }

    [Fact]
    public async Task EncryptionModeMismatchIsRejectedWithoutWiping()
    {
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K(1), V(1, 200));
            await store.Flush();
        }
        var snapshots = DataPaths().ToDictionary(x => x, File.ReadAllBytes);

        var open = async () => {
            await using var store = await KvasarStore.Open(options with { DisableEncryption = true });
        };
        await open.Should().ThrowAsync<ArgumentException>();
        foreach (var (path, bytes) in snapshots)
            File.ReadAllBytes(path).Should().Equal(bytes);

        await using var recovered = await KvasarStore.Open(options);
        (await recovered.Get(K(1))).Should().NotBeNull();
    }

    [Fact]
    public async Task ExhaustedKeyIdentityInAdvisoryIndexFallsBackToReplay()
    {
        const int count = 20;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < count; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush();
        }

        var state = await ReadSuperblock();
        var snapshot = await ReadIndex(state);
        var entries = snapshot!.Value.Entries;
        entries[0].KeyId = ulong.MaxValue;
        var indexKey = new byte[KvasarConstants.IndexMacKeySize];
        KeyDerivations.HkdfSha256.Derive(_key, [], KvasarConstants.IndexMacKeyInfo, indexKey);
        long indexLength;
        await using (var index = await IndexLog.Open(
            await FileStorageBackend.Instance.Open(IndexPath(state.IndexSlot)),
            FormatVer,
            indexKey)) {
            var dataHeader = SegmentHeader.Read(ReadHeader(DataPath(state.DataSlot)));
            indexLength = await index.WriteCheckpoint(
                entries, snapshot.Value.DataCommitLength, dataHeader.FileSalt);
            await index.WriteCommitMac(state.Generation + 1);
            await index.WriteCommitMac(state.Generation + 2);
        }
        await using (var file = await FileStorageBackend.Instance.Open(KvsPath)) {
            using var superblock = new Superblock(_key, FormatVer);
            await superblock.Write(file, state with {
                Generation = state.Generation + 1,
                IndexCommitLength = indexLength,
            });
            await superblock.Write(file, state with {
                Generation = state.Generation + 2,
                IndexCommitLength = indexLength,
            });
        }

        await using var recovered = await KvasarStore.Open(options);
        for (var i = 0; i < count; i++)
            (await recovered.Get(K(i))).Should().NotBeNull($"key {i}");
    }

    // Private methods

    private string BasePath => Path.Combine(_dir, "store");
    private string KvsPath => BasePath + ".kvs";
    private string DataPath(int slot) => $"{BasePath}.{slot}.kdat";
    private string IndexPath(int slot) => $"{BasePath}.{slot}.kidx";
    private string[] DataPaths() => Directory.GetFiles(_dir, "store.*.kdat");
    private string[] IndexPaths() => Directory.GetFiles(_dir, "store.*.kidx");

    private KvasarOptions Options(bool encrypt = true) => new() {
        BasePath = BasePath,
        EncryptionKey = _key,
        DisableEncryption = !encrypt,
        PageSize = PageSize,
        FlushDelay = TimeSpan.Zero, // commit per write, so every fixture below is deterministic
    };

    private string[] FileNames()
        => [.. Directory.GetFiles(_dir).Select(Path.GetFileName)!];

    private async Task<SuperblockState> ReadSuperblock()
        => (await ReadSuperblockStates())[0];

    private async Task<SuperblockState[]> ReadSuperblockStates()
    {
        await using var file = await FileStorageBackend.Instance.Open(KvsPath);
        var read = await new Superblock(_key, FormatVer).Read(file);
        read.Status.Should().Be(SuperblockStatus.Ok);
        return read.States;
    }

    private async Task<SuperblockState> ReadSuperblock(FakeStorageBackend backend)
    {
        await using var file = await backend.Open(KvsPath);
        var read = await new Superblock(_key, FormatVer).Read(file);
        read.Status.Should().Be(SuperblockStatus.Ok);
        return read.Newest!.Value;
    }

    private async Task<IndexSnapshot?> ReadIndex(SuperblockState state)
    {
        var authenticationKey = new byte[KvasarConstants.IndexMacKeySize];
        KeyDerivations.HkdfSha256.Derive(_key, [], KvasarConstants.IndexMacKeyInfo, authenticationKey);
        await using var log = await IndexLog.Open(
            await FileStorageBackend.Instance.Open(IndexPath(state.IndexSlot)),
            FormatVer,
            authenticationKey);
        var dataHeader = SegmentHeader.Read(ReadHeader(DataPath(state.DataSlot)));
        return await log.Read(
            state.IndexCommitLength, state.Generation, dataHeader.FileSalt);
    }

    private static int OnDiskPageSize(bool encrypt)
        => PageSize + (encrypt ? KvasarConstants.GcmTagSize : 0);

    private static long PageCountOf(string dataPath)
        => (new FileInfo(dataPath).Length - KvasarConstants.SegmentHeaderSize) / OnDiskPageSize(encrypt: true);

    private static string FileSaltOf(string dataPath)
        => Convert.ToHexString(SegmentHeader.Read(ReadHeader(dataPath)).FileSalt);

    private static byte[] ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[KvasarConstants.SegmentHeaderSize];
        fs.ReadExactly(header);
        return header;
    }

    private static void CorruptPage(string dataPath, long pageId)
    {
        // Flips bytes inside the page's ciphertext, so it can never authenticate again.
        var offset = KvasarConstants.SegmentHeaderSize + (pageId * OnDiskPageSize(encrypt: true));
        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Position = offset + 16;
        var damage = new byte[32];
        Array.Fill(damage, (byte)0x5A);
        fs.Write(damage);
    }

    private void CorruptSuperblockSlot(int slot)
    {
        var offset = Superblock.HeaderSize + ((long)slot * Superblock.SlotSize);
        using var fs = new FileStream(KvsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Position = offset + Superblock.SlotSize - 1;
        var value = fs.ReadByte();
        fs.Position--;
        fs.WriteByte((byte)(value ^ 0x80));
    }

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
    }

    private static void FlipFileByte(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Position = offset;
        var value = fs.ReadByte();
        fs.Position--;
        fs.WriteByte((byte)(value ^ 0x80));
    }

    private static void Truncate(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        fs.SetLength(length);
    }

    private bool DataBytesContain(string keyName)
    {
        var needle = Encoding.UTF8.GetBytes(keyName);
        foreach (var path in DataPaths()) {
            if (Contains(File.ReadAllBytes(path), needle))
                return true;
        }
        return false;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++) {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    // Writes 200 distinctively-named keys and deletes every fourth one, leaving both the originals and
    // their tombstones on disk — the state a compaction then has to resolve.
    private async Task<(string[] Live, string[] Deleted)> BuildStoreWithDeletes(KvasarOptions options)
    {
        var live = new List<string>();
        var deleted = new List<string>();
        await using var store = await KvasarStore.Open(options);
        for (var i = 0; i < 200; i++) {
            var name = i % 4 == 0 ? $"doomed-key-{i:D4}" : $"kept-key-{i:D4}";
            await store.Set(K(name), V(i, 300));
            (i % 4 == 0 ? deleted : live).Add(name);
        }
        foreach (var name in deleted)
            await store.Set(K(name), null);
        await store.Flush();
        return ([.. live], [.. deleted]);
    }

    private static KvasarKey K(int i) => Encoding.UTF8.GetBytes($"k{i:D6}");
    private static KvasarKey K(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] V(int i, int size)
    {
        var value = new byte[size];
        for (var j = 0; j < size; j++)
            value[j] = (byte)(i * 31 + j * 7 + 11);
        return value;
    }

    private static byte[] NewBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // Nested types

    // Records the dirent mutations a workload causes, so "nothing is created or unlinked while the
    // store is open" can be asserted rather than reasoned about.
    private sealed class RecordingStorageBackend(IStorageBackend backend) : IStorageBackend
    {
        private readonly List<string> _deletedPaths = [];
        private readonly List<string> _openedPaths = [];

        public IReadOnlyList<string> DeletedPaths => _deletedPaths;
        public IReadOnlyList<string> OpenedPaths => _openedPaths;

        public void Reset()
        {
            _deletedPaths.Clear();
            _openedPaths.Clear();
        }

        public ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
        {
            lock (_openedPaths)
                _openedPaths.Add(path);
            return backend.Open(path, cancellationToken);
        }

        public bool Exists(string path)
            => backend.Exists(path);

        public void Delete(string path)
        {
            lock (_deletedPaths)
                _deletedPaths.Add(path);
            backend.Delete(path);
        }

        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);
    }

    // Watches every .kidx checkpoint go past and compares the data extent it stamps against the bytes
    // the .kdat files have actually received by then. That is the durable extent as the device sees it,
    // which is what makes the invariant checkable per commit instead of only on the final state.
    private sealed class IndexStampWatchdog(IStorageBackend backend, int pageSize, int onDiskPageSize)
        : IStorageBackend
    {
        private readonly Dictionary<string, long> _writtenEnds = new(StringComparer.Ordinal);
        private readonly List<string> _violations = [];

        public IReadOnlyList<string> Violations => _violations;
        public int CheckpointCount { get; private set; }

        public async ValueTask<IStorageFile> Open(string path, CancellationToken cancellationToken = default)
            => new WatchedFile(await backend.Open(path, cancellationToken), this, path);

        public bool Exists(string path)
            => backend.Exists(path);

        public void Delete(string path)
            => backend.Delete(path);

        public string[] ListFiles(string directoryPath, string searchPattern)
            => backend.ListFiles(directoryPath, searchPattern);

        internal void OnWrite(string path, long offset, ReadOnlySpan<byte> buffer)
        {
            lock (_violations) {
                if (path.EndsWith(".kdat", StringComparison.Ordinal)) {
                    _writtenEnds.TryGetValue(path, out var end);
                    _writtenEnds[path] = Math.Max(end, offset + buffer.Length);
                    return;
                }
                if (!path.EndsWith(".kidx", StringComparison.Ordinal) || offset != 0
                    || buffer.Length < IndexLog.HeaderSize
                    || !buffer[..4].SequenceEqual(KvasarConstants.KIdxMagic))
                    return;

                CheckpointCount++;
                var stamp = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(24, 8));
                var required = KvasarConstants.SegmentHeaderSize + (stamp / pageSize * onDiskPageSize);
                var written = _writtenEnds.Count == 0 ? 0 : _writtenEnds.Values.Max();
                if (required > written)
                    _violations.Add($"checkpoint stamped at {stamp} needs {required} data bytes, {written} written");
            }
        }
    }

    private sealed class WatchedFile(IStorageFile file, IndexStampWatchdog watchdog, string path) : IStorageFile
    {
        public long Length => file.Length;

        public ValueTask DisposeAsync()
            => file.DisposeAsync();

        public ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
            => file.Read(offset, buffer, cancellationToken);

        public ValueTask Write(long offset, ReadOnlyMemory<byte> buffer)
        {
            watchdog.OnWrite(path, offset, buffer.Span);
            return file.Write(offset, buffer);
        }

        public ValueTask FlushToDisk()
            => file.FlushToDisk();

        public ValueTask Truncate(long length)
            => file.Truncate(length);
    }
}
