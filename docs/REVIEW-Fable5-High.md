# ActualLab.Kvasar — code review findings (Fable 5)

Reviewed: full core library (`src/ActualLab.Kvasar`, ~3.2k lines) against `docs/SPEC.md` and
`docs/DESIGN.md`, at commit `ab2fc34` (2026-07-25).

What held up under re-examination: the seqlock probe (`ProbeCursor.MoveNext`), varint/record
bounds checking, the GCM nonce lifecycle (including cross-mode `FlushDelay` crash scenarios),
record-before-index ordering, the `TailSnapshot` publication, page-cache accounting, wipe
scoping, and the SipHash-2-4 implementation. The findings below are new issues.

---

## 1. Deleted keys resurrect after compaction — CONFIRMED with a failing test

**Severity: high (stale data served as live, not a benign miss).**

`TryCompactOne` drops tombstones (`KvasarStore.cs:603`, `view.IsTombstone → continue`) and then
deletes the segment. A tombstone masks older records of the same key in *earlier* segments —
records that are dead bytes but still physically present. Once the tombstone's segment is gone,
any index rebuild that scans the log re-applies the old record and the key comes back to life
with its pre-delete value.

Reachable three ways:

- **Deterministically** when `.kidx` isn't persisted — `IndexEncryption.On`, or `Auto` with a
  non-keyed hasher (`KvasarStore.cs:67`) — because *every* open rebuilds via `ScanAll`.
- Whenever `.kidx` is missing or unreadable at open (the `LoadIndex` fallback path).
- **Even on the fast path** after a crash: compaction flushes the log before `RemoveSegment`
  (`KvasarStore.cs:614`) but not the buffered `.kidx` delta stream. If the checkpoint predates
  the delete and the crash loses the tombstone delta, replay-from-HWM can't re-read the
  tombstone — its segment no longer exists.

**Verified empirically**: repro test at
`D:\Projects\ActualLab.Kvasar.review-wt\tests\ActualLab.Kvasar.Tests\Store\ResurrectionReproTests.cs`
fails with the victim key returning its old 400-byte value after `Compact()` + reopen
(`IndexEncryption.On`, durable mode, 512-byte pages, 8 KiB segments).

**Fix sketch**: the classic Bitcask rule — drop a tombstone only when compacting the oldest
surviving segment, otherwise copy it forward into the active segment; and flush `_kidxDelta`
before `RemoveSegment` to close the crash window.

## 2. A checkpoint can stamp an HWM ahead of the durable log

**Severity: medium (breaks the documented "anything already flushed survives" guarantee).**

DESIGN.md's own mandatory-flush-point list names `WriteCheckpoint`, but the implementation
doesn't flush: `WriteCheckpoint` (`KvasarStore.cs:571`) snapshots `ActiveLogicalHwm`, which
counts staged-unwritten pages (`PageCount` is incremented at staging time,
`PagedSegment.cs:221`) plus the in-RAM tail fill. When `MaybeCheckpoint` fires from a
deferred-mode `Set`/`SetMany`, the checkpoint can be written with an HWM the on-disk log hasn't
reached.

Failure mode: key K has an old, flushed value v1 and a fresh, unflushed value v2. A checkpoint
captures K→loc(v2) and HWM=X (past the durable log end). Crash before the flush tick. On
reopen, K's entry points into a region that was never written (miss), and v1's record is
pre-HWM so the gap replay never re-reads it — a durably-flushed value becomes unreachable.

**Fix**: `await _segments.Flush(false, ct)` at the top of `KvasarStore.WriteCheckpoint`, before
reading the HWM.

## 3. Torn `.kidx` delta tail → misaligned appends → garbage entries

**Severity: medium.**

`IndexFile.Parse` correctly drops a partial trailing delta, but `OpenDeltaStream`
(`KvasarStore.cs:560`) seeks to the raw end of file and appends there. A crash mid-delta is
likely: the 4096-byte `FileStream` buffer is not a multiple of the 21-byte `IndexEntry`, so
buffer-boundary flushes routinely end mid-entry. After such a crash, every subsequent delta is
written misaligned; the next load parses everything past the torn point at wrong offsets:

- Real deltas are lost — mostly healed by the HWM gap replay, *except* when compaction removed
  the segment (feeding finding 1).
- Garbage entries with random hashes/locators enter the index via `BulkLoad` and are persisted
  by the next checkpoint. A random locator can even parse as a plausible record and leak a
  garbage `(key, value)` pair out of `Scan`.

**Fix**: truncate on open — `SetLength(len - (len - HeaderSize) % EntrySize)`. This works
without knowing the checkpoint/delta split, since both regions use the same entry size.

## 4. Cancellation is swallowed on read paths

**Severity: medium-low (silent wrong results on cancellation).**

- `TryReadValue`'s filter (`KvasarStore.cs:404`) catches `OperationCanceledException`, so a
  cancelled `Get`/`GetMany` reports "key absent" instead of throwing.
- `Scan`'s filter (`KvasarStore.cs:172`) does the same — after cancellation every remaining
  `TryReadRecord` throws and is skipped, so the enumeration *completes normally with silently
  partial results*.

**Fix**: add `and not OperationCanceledException` to both exception filters.

## 5. `RemoveSegment` can throw and orphan the segment file

**Severity: low-medium (robustness; adjacent to documented limitation #5).**

Segment handles are opened without `FileShare.Delete` (`PagedSegment.cs:72,94`). A reader with
an in-flight `RandomAccess.ReadAsync` keeps the `SafeFileHandle` alive briefly past
`Dispose()`, so on Windows `File.Delete` in `SegmentSet.RemoveSegment` (`SegmentSet.cs:320`)
can throw a sharing violation — *after* `_states.TryRemove` — surfacing a spurious exception
from `Compact()` and orphaning an all-dead segment that the next open re-adopts.

**Fix**: open with `FileShare.Delete`, or tolerate delete failure and sweep stray dead segments
at open.

## 6. Minor

- **No `SegmentBytes` validation** against the 32-bit `Locator.Offset`: a value > ~4 GB
  produces a runtime `OverflowException` from `checked((uint)offset)` mid-append
  (`SegmentSet.cs:348`). Reject it in `Open`.
- **No key-size cap**: `MaxValueBytes` bounds only the value; `RecordCodec.GetRecordLength`
  sums key+value in `int`, so a ~2 GB key overflows into negative lengths. Add a key cap.
- **`GetMany` is a sequential per-key loop** (`KvasarStore.cs:127`) — SPEC §6.4 promises
  sort-by-locator + per-page batching, and this is exactly the `IBatchingKvasBackend` hot path
  (batches ≤64); a cold batch pays up to 64 random I/Os with no prefetch.
- **`StoreLock`** maps every `IOException` (bad path, disk full) to `KvasarLockException`
  "already open in this or another process" — misleading diagnostics (`StoreLock.cs:22`).
- **Gap replay treats a torn-but-full-size bad page as global corruption**: after power loss
  with out-of-order flush, the active segment can end with a full-size unauthenticatable page;
  `ScanFrom` → `GetPage` → `Decrypt` throws and `Open` wipes the whole store, vs SPEC §8's
  "truncate the torn tail, earlier data intact." Consider treating auth failure in the active
  segment's final pages as a torn tail.
- `_disposeCts` is never disposed (cosmetic).

---

## Notes

- Repro worktree: `D:\Projects\ActualLab.Kvasar.review-wt` (branchless, detached at `ab2fc34`);
  contains the failing `ResurrectionReproTests`. Remove with
  `git worktree remove --force ../ActualLab.Kvasar.review-wt` when done — or keep the test as
  the regression test for finding 1.
- Suggested fix order: #2, #3, #4 are small and low-risk; #1 needs a decision on the
  tombstone-retention rule (copy-forward vs oldest-segment-only drop).
