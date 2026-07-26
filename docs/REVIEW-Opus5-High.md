# ActualLab.Kvasar — code review

Reviewer: Claude Opus 5 · Date: 2026-07-25 · Commit: `ab2fc34` (main)

Scope: `src/ActualLab.Kvasar` (all modules), read against `docs/SPEC.md` and `docs/DESIGN.md`.
Findings marked **reproduced** were confirmed by running tests in a throwaway git worktree; that
worktree has been removed and `main` is untouched. The repro tests are reproduced verbatim in the
appendix of this document.

---

## Critical — one root cause, two failure modes (both reproduced)

### C1. `ScanFrom`'s torn-tail `yield break` aborts the whole scan, not just the current segment

`src/ActualLab.Kvasar/Internal/Log/SegmentSet.cs:264`

```csharp
foreach (var (segId, st) in _states.OrderBy(kv => kv.Key)) {
    ...
    while (p < len) {
        var read = await TryReadAt(st, p, cancellationToken).ConfigureAwait(false);
        if (read.IsFound) { ... }
        else {
            var nextPage = (p / _pageSize + 1) * _pageSize;
            if (p % _pageSize != 0 && nextPage <= len)
                p = nextPage;       // page-end padding: skip to the next page
            else
                yield break;        // torn tail: stop   <-- exits the OUTER foreach too
        }
    }
}
```

The `yield break` terminates the entire iterator, so an unparseable record in segment *N* silently
discards segments *N+1…* — i.e. all the **newest** data. Truncating the tail is only a meaningful
recovery action for the *last* segment.

**Reachability.** This is reachable through the documented deferred-flush crash mode
(`FlushDelay` defaults to 0.5 s, so it is the default mode). Whole-page loss usually ends a segment
cleanly — `PagedSegment.Open` floors `PageCount`, `len` drops, and the walk terminates normally.
But a **multi-page record** (`SegmentSet.AppendMultiPage`) starts at a page boundary and occupies
whole pages; if its continuation pages are lost, its declared length runs past `len`, `TryReadAt`
returns not-found, and `p % _pageSize == 0` selects the `yield break` branch.

Note this is *not* the wipe-and-recreate path: every surviving page still authenticates, so no
`KvasarCorruptException` is raised and `KvasarStore.Open` never wipes.

### C2. Consequence A — index rebuild loses every later segment

`KvasarStore.LoadIndex` (`KvasarStore.cs:643-647`) falls back to `SegmentSet.ScanAll` whenever the
`.kidx` is missing or unusable. With C1 in play the rebuild stops at the damaged segment.

**Reproduced.** Write one multi-page record plus 200 small keys spilling across 26 segments,
truncate segment 1 to whole pages, delete `.kidx`, reopen:

```
Expected found to be greater than 100 because only 0/200 keys survived;
later segments were never scanned, but found 0 (difference of -100).
```

**0 of 200 keys recovered**, with segments 2…26 fully intact and independently readable.

### C3. Consequence B — compaction then deletes the intact segments

`KvasarStore.TryCompactOne` (`KvasarStore.cs:602-620`):

```csharp
await foreach (var (loc, view, recordLength) in _segments.ScanAll(cancellationToken)...) {
    if (loc.SegmentId != target || view.IsTombstone)
        continue;
    ...
    pending.Add(...);
}
await _segments.Flush(false, cancellationToken)...;
foreach (var p in pending) { ... }
_segments.RemoveSegment(target);      // unconditional
```

If `ScanAll` never reaches `target`, `pending` is empty — but `RemoveSegment(target)` still runs.
And once C2 has emptied the index, `SegmentSet.SeedAccountingFromIndex` gives every sealed segment
`LiveBytes = 0` / `DeadBytes = gross`, so every one of them qualifies as a compaction target.

**Reproduced.** Same setup, then a single `store.Compact()`:

```
Expected segmentsAfter to be 26 because compaction deleted 25 intact segments
it never scanned, but found 1 (difference of -25).
```

**26 segments → 1.** Silent, total, unrecoverable data destruction from one lost page.

SPEC §9 requires "a segment is deleted only after no index entry references it"; the code never
checks that invariant, it only infers it from a scan that can end early.

### Suggested fixes

Two independent changes, either of which breaks the chain — both are worth making:

1. **`ScanFrom`**: replace `yield break` with a `break` out of the inner `while`, continuing to the
   next segment. Truncation semantics should apply per-segment (and really only to the last one).
2. **`TryCompactOne`**: scan only `target` instead of the whole log, and refuse to
   `RemoveSegment(target)` unless the scan actually reached and completed that segment. As a bonus
   this removes the current O(whole-log) decrypt paid to compact a single 16 MiB segment — today a
   410 MB store re-decrypts 410 MB per compaction pass.

---

## Medium

### M1. Compaction deletes the source segment after a non-fsynced flush

`KvasarStore.cs:614` → `await _segments.Flush(false, cancellationToken)`, then `:620` →
`_segments.RemoveSegment(target)`.

`docs/DESIGN.md` calls this exact point out as a mandatory flush point ("else records copied into
the new segment die with the deleted source — real loss, not staleness"), but `fsync: false` only
pushes bytes into the OS page cache. Nothing orders the copy-writes ahead of the unlink, so a power
loss between them can persist the delete without the copies. Should be `Flush(true)`.

### M2. The `.clean` marker is only consumed when `FlushDelay > 0`

`KvasarStore.cs:366-376`:

```csharp
if (_flushDelay > TimeSpan.Zero) {
    var closedCleanly = TryConsumeCleanMarker();
    if (!closedCleanly && _segments.ActiveSegmentPageCount > 0)
        await _segments.RollToNewSegment(cancellationToken)...;
    ...
}
```

`WriteCleanMarker()` (`:312`) runs on every graceful close, in both modes — but the consume/delete
is inside the deferred-mode branch. A durable-mode session therefore leaves the marker behind, and
it stops meaning "the *last* run closed cleanly" and starts meaning "*some* run did".

Failure sequence: durable run closes gracefully (marker written) → durable run crashes with pages
lost to a machine crash (marker never consumed, still present) → next open with `FlushDelay > 0`
sees the stale marker, skips `RollToNewSegment`, and appends at a `pageId` whose
`(fileSalt, pageId)` GCM nonce was already used. That is precisely the catastrophic keystream reuse
the marker was introduced to prevent (`DESIGN.md`, "Page-id reuse ⇒ nonce reuse").

Fix: consume/delete the marker on every open, unconditionally; keep the roll decision itself gated
on the mode.

---

## Low

- **`Set` with an oversized value silently keeps the old value.** `AppendOne` returns
  `Locator.None` (`KvasarStore.cs:418`) and `Publish` returns early on `loc.IsNone` (`:430`), so the
  key retains its *previous* value rather than being removed or left absent. SPEC §12's
  "skip + log" is ambiguous here, but silently serving stale data is the worst of the available
  options — and nothing is logged either.
- **`SegmentBytes` is unvalidated against the 32-bit `Locator.Offset`.** `checked((uint)offset)`
  (`SegmentSet.cs:348`, `:382`) throws `OverflowException` out of `Set` for any `SegmentBytes`
  above 4 GiB. Validate in `KvasarStore.Open` alongside the existing `PageSize` check.
- **`SealedSegments()` yields in `ConcurrentDictionary` order** (`SegmentSet.cs:294-302`), so
  `TryCompactOne` picks an effectively arbitrary target rather than the oldest or deadest segment.
- **`IndexEntry` is 21 bytes under `Pack = 1`**, so `MemoryMarshal.Cast` in `IndexFile.Parse`
  (`IndexFile.cs:121`, `:128`) yields `ulong KeyHash` fields at unaligned addresses. Fine on
  x64/ARM64; padding the struct to 24 bytes would be both safer and marginally faster.
- **`_disposeCts` and `_writeLock` are never disposed** in `KvasarStore.DisposeAsync`.
- **`KvasarOptions.PageSize` doc claims "0 ⇒ probe the FS cluster size (fallback 4 KiB)"**;
  `ResolvePageSize` (`KvasarStore.cs:684`) just uses `KvasarConstants.DefaultPageSize`. Either
  implement the probe or fix the comment.
- **`SetMany` hashes every key twice** — `KvasarStore.cs:209` building `lastByHash`, then `:217`
  re-hashing inside the append loop.
- **`KvasarStore.Flush(bool fsync)` blocks the caller's thread on `_kidxDelta.Flush(true)`**
  (`:269`) while `_segments.Flush` correctly offloads its fsync. Minor inconsistency.

---

## Possible, not reproduced

### P1. Tombstone resurrection after compaction

`TryCompactOne` drops tombstones (`KvasarStore.cs:603`) and then deletes their segment. But a
deleted key's *original* record can still be sitting in a different, surviving segment: `Publish`
removed its index entry when the tombstone was written, so compaction of the original's segment
would drop it — yet until that happens the record is still physically present in the log with
nothing recording that it was deleted.

If a `.kidx`-less rebuild (`ScanAll`) runs in that window, it replays the original and the key comes
back from the dead. Whether the window is actually open depends on compaction ordering, which is
arbitrary today (see the `SealedSegments()` note above).

My repro did not hit it — the segment holding the original was itself compacted, closing the window
— so this is a design concern to reason through rather than a confirmed defect.

---

## What holds up

Read adversarially and found sound:

- The seqlock validation in `ProbeCursor.MoveNext` (re-reading the locator after the parallel
  fields) genuinely closes the torn-triple hole, and the acquire/release pairing with
  `HashIndex.Set` is correct.
- `TailSnapshot` publication makes `(buffer, fill, pageId)` a single consistent read, and
  `SealTail`'s "clear above `_tailFill`, then install a *fresh* array" keeps every handed-out
  zero-copy slice immutable.
- The bound-before-narrow guards against attacker-controlled lengths are consistently applied
  (`RecordCodec.TryParse`, `SegmentSet.TryReadAt`/`TryReadRecordCached`, `IndexFile.Parse`).
- `PagedSegment`'s write-behind ordering is right: staged plaintext is dropped from `_pendingPlain`
  only *after* the bytes reach disk, so a concurrent reader always finds the page in one place or
  the other, never neither.
- `HashIndex` load-factor accounting guarantees a terminating empty slot, so no probe loop can spin
  forever; `CapacityFor`/`CeilPow2`/`RoundUpPow2` are all correctly capped.
- The HWM-replay design correctly rescues compaction repoints whose `.kidx` deltas were lost in a
  crash, because compaction appends past the checkpoint HWM.

---

## Appendix — repro tests

Drop into `tests/ActualLab.Kvasar.Tests/Store/AuditReproTests.cs`. `LostPagesInAnEarlySegmentDoNotHideLaterSegments`
and `CompactionDoesNotDeleteASegmentItNeverScanned` fail on `ab2fc34`;
`DeletedKeyResurrectsAfterTombstoneSegmentIsCompacted` passes (see P1).

```csharp
using System.IO;
using System.Text;

namespace ActualLab.Kvasar.Tests.Store;

public class AuditReproTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kvasar-audit-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = new byte[32];

    public AuditReproTests()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < _key.Length; i++)
            _key[i] = (byte)(i * 7 + 1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }

    private string BasePath => Path.Combine(_dir, "store");

    private KvasarOptions Options() => new() {
        BasePath = BasePath,
        EncryptionKey = _key,
        PageSize = 512,
        SegmentBytes = 8 * 1024,
        CompactionMinBytes = 1,
        CompactionDeadRatio = 0.1,
        FlushDelay = TimeSpan.Zero,
    };

    private static byte[] K(string s) => Encoding.UTF8.GetBytes(s);

    // A multi-page record whose continuation pages were lost (whole-page loss is the documented
    // deferred-flush crash mode) sits at a page boundary in a NON-final segment. ScanFrom's
    // `yield break` exits the whole enumeration, so every later segment is never scanned.
    [Fact]
    public async Task LostPagesInAnEarlySegmentDoNotHideLaterSegments()
    {
        var big = new byte[2000];   // > PageSize(512) => AppendMultiPage, page-aligned start
        var small = new byte[100];

        await using (var store = await KvasarStore.Open(Options())) {
            await store.Set(K("big"), big);            // multi-page run near the start of segment 1
            for (var i = 0; i < 200; i++)              // spill into several more segments
                await store.Set(K("k" + i), small);
            await store.Flush();
        }

        var segments = Directory.GetFiles(_dir, "store.*.klog").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        segments.Length.Should().BeGreaterThan(2, "the test needs several segments");

        // Truncate the FIRST segment to whole pages, dropping the tail of the multi-page record.
        // No torn page and no corrupt bytes: every surviving page still authenticates.
        const int onDiskPage = 512 + 16;
        using (var fs = new FileStream(segments[0], FileMode.Open, FileAccess.Write))
            fs.SetLength(64 + onDiskPage * 2L);

        foreach (var f in Directory.EnumerateFiles(_dir, "store.kidx*"))
            File.Delete(f);
        foreach (var f in Directory.EnumerateFiles(_dir, "store.clean"))
            File.Delete(f);

        await using (var store = await KvasarStore.Open(Options())) {
            var found = 0;
            for (var i = 0; i < 200; i++)
                if (await store.Get(K("k" + i)) is not null)
                    found++;
            found.Should().BeGreaterThan(100,
                $"only {found}/200 keys survived; later segments were never scanned");
        }
    }

    // Same shape, but the surviving-segment records are what compaction must carry forward:
    // if ScanAll stops early, `pending` is empty and RemoveSegment() still deletes the target.
    [Fact]
    public async Task CompactionDoesNotDeleteASegmentItNeverScanned()
    {
        var big = new byte[2000];
        var small = new byte[100];

        await using (var store = await KvasarStore.Open(Options())) {
            await store.Set(K("big"), big);
            for (var i = 0; i < 200; i++)
                await store.Set(K("k" + i), small);
            for (var i = 0; i < 200; i++)          // churn => dead bytes in the later segments
                await store.Set(K("k" + i), small);
            await store.Flush();
        }

        var segments = Directory.GetFiles(_dir, "store.*.klog").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        const int onDiskPage = 512 + 16;
        using (var fs = new FileStream(segments[0], FileMode.Open, FileAccess.Write))
            fs.SetLength(64 + onDiskPage * 2L);

        foreach (var f in Directory.EnumerateFiles(_dir, "store.kidx*"))
            File.Delete(f);
        foreach (var f in Directory.EnumerateFiles(_dir, "store.clean"))
            File.Delete(f);

        var segmentsBefore = Directory.GetFiles(_dir, "store.*.klog").Length;
        await using (var store = await KvasarStore.Open(Options())) {
            await store.Compact();
        }
        var segmentsAfter = Directory.GetFiles(_dir, "store.*.klog").Length;
        segmentsAfter.Should().Be(segmentsBefore,
            $"compaction deleted {segmentsBefore - segmentsAfter} intact segments it never scanned");
    }

    // Compaction reclaims the segment holding a key's TOMBSTONE while the segment holding that
    // key's original record survives. A later .kidx-less rebuild then replays the original alone.
    [Fact]
    public async Task DeletedKeyResurrectsAfterTombstoneSegmentIsCompacted()
    {
        var victim = K("victim");
        var payload = new byte[40];                 // packs densely => segment 1 stays ~all-live
        var opts = Options() with { CompactionDeadRatio = 0.5, CompactionMinBytes = 1024 };

        await using (var store = await KvasarStore.Open(opts)) {
            await store.Set(victim, payload);
            for (var i = 0; i < 120; i++)           // keep segment 1 mostly live => not a candidate
                await store.Set(K("keep" + i), payload);
            await store.Flush();
        }

        await using (var store = await KvasarStore.Open(opts)) {
            await store.Set(victim, null);          // tombstone lands in a later segment
            for (var i = 0; i < 600; i++)           // churn one key => that segment fills with dead bytes
                await store.Set(K("churn"), payload);
            await store.Flush();
            (await store.Get(victim)).Should().BeNull();
            await store.Compact();
            (await store.Get(victim)).Should().BeNull();
        }

        foreach (var f in Directory.EnumerateFiles(_dir, "store.kidx*"))
            File.Delete(f);

        await using (var store = await KvasarStore.Open(opts)) {
            (await store.Get(victim)).Should()
                .BeNull("a deleted key must not come back after its tombstone was compacted away");
        }
    }
}
```
