using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using ActualLab.Kvasar.Internal;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar.Tests.Store;

/// <summary>
/// One test per defect from <c>docs/REVIEW-Overlap.md</c>, against the v2 (superblock) store. Where the
/// redesign made a defect unrepresentable, the test pins the <em>structural</em> property that makes it
/// so, so a future regression re-arms the test rather than silently reintroducing the bug.
/// </summary>
public sealed class ReviewRegressionTests : IDisposable
{
    private const uint FormatVer = 1; // KvasarOptions.FormatVersion "1" parses straight through
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
                await store.Flush(true);
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
            await store.Flush(true);
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
            await store.Flush(true);
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
    public async Task AFabricatedIndexDeltaNeverReachesScan()
    {
        // I3: v1 appended deltas at raw EOF, so a torn delta misaligned every later one and fed random
        // (hash, locator) pairs into the index — the only path in the review that could serve a
        // fabricated (key, value). Here the fabricated entry is handed to the store directly: it does
        // enter the index (the .kidx is never trusted, only replayed), but the read paths re-derive the
        // key's hash from the record they decoded and drop anything the entry misnamed (§14.2).
        const int total = 60;
        var options = Options();
        var oracle = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++) {
                var value = V(i, 60);
                await store.Set(K(i), value);
                oracle[$"k{i:D6}"] = value;
            }
            await store.Flush(true);
        }

        var state = await ReadSuperblock();
        var indexPath = IndexPath(state.IndexSlot);
        var real = (await ReadIndex(state.IndexSlot, state.IndexCommitLength))!.Value.Entries[0];
        // A whole entry, so nothing about it is torn: it points at a real record under a hash no key has.
        var fabricated = real;
        fabricated.KeyHash ^= 0xA5A5_A5A5_A5A5_A5A5;
        AppendBytes(indexPath, MemoryMarshal.AsBytes<IndexEntry>([fabricated]).ToArray());

        // A commit must name the fabricated entry for the next open to adopt it, so write once more.
        await using (var store = await KvasarStore.Open(options)) {
            await store.Set(K("late"), V(7, 40));
            oracle["late"] = V(7, 40);
            await store.Flush(true);
        }

        await using (var store = await KvasarStore.Open(options)) {
            store.Stats.Entries.Should().Be(oracle.Count + 1,
                "the fabricated entry is in the index — filtering it at read time is what the test checks");
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
            await store.Flush(true);
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
            await store.Flush(true);
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
            await store.Flush(true);
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
            await store.Flush(true);
        }
        foreach (var path in IndexPaths())
            File.Delete(path);

        await using var rebuilt = await KvasarStore.Open(options);
        foreach (var name in deleted)
            (await rebuilt.Get(K(name))).Should().BeNull($"'{name}' was deleted before the compaction");
        foreach (var name in live)
            (await rebuilt.Get(K(name))).Should().NotBeNull($"'{name}' was never deleted");
    }

    // --- Known defects found while writing this suite ----------------------

    /// <summary>
    /// Regression for C3, found by this suite and not by any review pass. A store that does not persist
    /// index entries — <see cref="IndexEncryption.On"/>, or <see cref="IndexEncryption.Auto"/> with a
    /// non-keyed hasher — used to read back <b>completely empty</b> on the next open: the entry-less
    /// checkpoint was stamped at the committed extent, so recovery replayed nothing and adopted it.
    /// An entry-less checkpoint is consistent with offset 0, so that is what it is stamped at now.
    /// </summary>
    [Fact]
    public async Task AnUnpersistedIndexStillRebuildsFromTheLog()
    {
        var options = Options() with { IndexEncryption = IndexEncryption.On };
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < 50; i++)
                await store.Set(K(i), V(i, 100));
            await store.Flush(true);
            store.Stats.Entries.Should().Be(50);
        }

        await using var reopened = await KvasarStore.Open(options);
        for (var i = 0; i < 50; i++)
            (await reopened.Get(K(i)))!.Value.ToArray().Should().Equal(V(i, 100));
    }

    /// <summary>
    /// KNOWN GAP, documented at <c>docs/DESIGN-Durability.md</c> §14.4: <c>DataLog.ScanFrom</c> ends the
    /// walk at a page that fails its tag, so on an open that has to replay — no usable <c>.kidx</c> — a
    /// single bad page mid-file still discards every record above it. That is I2's shape at a smaller
    /// volume: with the index intact the same damage costs one page's keys
    /// (<see cref="AMidFileCorruptPageIsAMissForItsOwnKeysOnly"/>), without it, 392 of 400.
    /// Closing it needs an authenticate-only pass that reports failure instead of stopping.
    /// </summary>
    [Fact(Skip = "Known gap (DESIGN-Durability.md §14.4): an index-less replay stops at the first bad page.")]
    public async Task AnIndexLessRebuildRecoversPastACorruptPage_KnownGap()
    {
        const int total = 400;
        var options = Options();
        await using (var store = await KvasarStore.Open(options)) {
            for (var i = 0; i < total; i++)
                await store.Set(K(i), V(i, 200));
            await store.Flush(true);
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
            await store.Flush(true);
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
                await store.Flush(true); // several commits, so the rotation gets a chance to fire
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
                await store.Flush(true);
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
        await store.Flush(true);
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
    public async Task AnAbsurdSegmentBytesIsNoLongerAConfigurationTrap()
    {
        // The same issue from the caller's side: SegmentBytes is inert now (there is one active data
        // file and compaction is total), so a value past the old 4 GiB locator cap cannot arm anything.
        var options = Options() with { SegmentBytes = 8L * 1024 * 1024 * 1024 };
        await using var store = await KvasarStore.Open(options);
        await store.Set(K("a"), V(1, 4000));
        await store.Flush(true);
        (await store.Get(K("a")))!.Value.ToArray().Should().Equal(V(1, 4000));
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
    {
        await using var file = await FileStorageBackend.Instance.Open(KvsPath);
        var read = await new Superblock(_key, FormatVer).Read(file);
        read.Status.Should().Be(SuperblockStatus.Ok);
        return read.Newest!.Value;
    }

    private async Task<IndexSnapshot?> ReadIndex(int slot, long validLength)
    {
        await using var log = await IndexLog.Open(
            await FileStorageBackend.Instance.Open(IndexPath(slot)), FormatVer);
        return await log.Read(validLength);
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

    private static void AppendBytes(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        fs.Write(bytes);
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
        await store.Flush(true);
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
