# Kvasar — outstanding items

> **Largely superseded by [`DESIGN-Durability.md`](DESIGN-Durability.md) (2026-07-25).** That
> proposal replaces segments with a fixed five-file set governed by an authenticated superblock, and
> in doing so eliminates D1–D5, G1–G5 and C-cluster items *structurally* rather than fixing them
> individually — 23 of the 38 issues in [`REVIEW-Overlap.md`](REVIEW-Overlap.md) cease to exist,
> including all four P0s. The items below that survive it are A1–A4, C1, C2, P2, T1 and the doc
> list. Read the design doc first; treat the rest of this file as the pre-redesign record.

Everything known-open as of **2026-07-25**, with enough context to act without re-deriving it.
Nothing here is a build or test failure: the suite is green (298 pass, 1 skipped) at 94.2% line /
89.6% branch coverage. These are correctness, durability, growth and hygiene gaps found by reading
the code — see [`example-session.md`](example-session.md) for the one-screen version.

Effort tags: **(S)** a few lines · **(M)** a focused, contained change · **(L)** a design change with
a format or contract impact.

## Suggested order

1. `.kidx` fsync-before-rename (D1) — smallest fix, largest silent-loss window closed.
2. Per-segment `_hasUnsyncedWrites` (D2) — removes N−1 fsyncs and N−1 blocked threads per durable flush.
3. Compaction trigger policy + small-segment eligibility (G1, G2) — the unbounded-growth cluster.
4. Compaction scan scope (P1) — pure win, no contract change.
5. Segment recycling (D4) — subsumes most of the directory-durability problem.
6. Everything else as it fits.

---

## D — Durability

### D1. `IndexFile.WriteCheckpoint` renames without fsyncing the temp file (S)
`File.WriteAllBytesAsync(tmp, …)` then `File.Move(tmp, path, overwrite: true)`. The rename is
atomic w.r.t. *which* file you get, not w.r.t. its contents being on disk — the classic ext4
delayed-allocation hole. After power loss the new `.kidx` can be zero-length (safe: `Parse` rejects
it and the store rebuilds from the log) or **partially written** (not safe: zeroed `IndexEntry`
records parse as structurally valid entries with `KeyHash = 0` and a null locator, so those keys
vanish from the index and the HWM-based replay starts *after* them, so they are never recovered).
Fix: write the temp via a `FileStream`, `Flush(flushToDisk: true)`, dispose, then move.

### D2. `Flush(true)` fsyncs every segment, including untouched ones (S)
`SegmentSet.Flush` calls `PagedSegment.Flush(fsync)` on all segments; sealed ones that were fsynced
on a previous flush — or that this process only ever opened and read — get a pointless
`FlushToDisk`, each blocking a threadpool thread (there is no async fsync in .NET). Add a
`_hasUnsyncedWrites` flag to `PagedSegment`, set wherever bytes reach the handle (`FlushPending`,
`Dispose`'s synchronous write, and `Create`'s header write), cleared **after** a successful
`FlushToDisk` so a failed fsync never claims durability. Steady state becomes one fsync — the active
segment — instead of one per segment. Safe under the write lock: no appends can interleave.
Note this also makes the concurrent-flush change from this session mostly moot; keep it for the
just-rolled two-dirty-segment case, but the flag is where the win is.

### D3. No parent-directory flush after creating a `.klog` (M, needs P/Invoke on Unix)
`fsync` on a newly created file does not guarantee its directory entry is durable; POSIX requires
`fsync` on the parent directory fd. So `Flush(true)` can return after a write into a
freshly-created segment and that segment can be absent after a power cut — the acknowledged write is
lost. Bounded in practice (ext4 heuristics usually cover create-then-fsync, and its journal commits
every ~5 s), and it degrades into the unclean-shutdown path the store already handles, but it is not
guaranteed by anything. There is **no way to query** durability — only to force it with a barrier.
.NET refuses to open a directory as a `FileStream`/`SafeFileHandle`, so this needs
`[LibraryImport("libc")] open/fsync/close` (`O_RDONLY` is `0` on Linux and macOS). Adds no package
reference, but does put platform-conditional native interop in a library whose README sells
"pure-managed" — a positioning call, not a technical one. Windows needs nothing:
`FlushFileBuffers` forces the NTFS metadata journal.

### D4. Recycle segment files instead of deleting them (L)
The structural alternative to D3: when compaction drains a segment, don't `File.Delete` it —
truncate it to the header, stamp a **fresh random `fileSalt`** and the next segmentId, and hand the
slot back. Fresh salt ⇒ disjoint nonce space, so the "GCM nonce = f(fileSalt, pageId)" invariant
holds unchanged (this is the same argument `StartNewSegment` already relies on). After warmup the
store creates no files and unlinks none, so no directory metadata is ever in the durability path.
Two real costs: the filename stops being the segment id (`PagedSegment.Open` deliberately validates
name == header id today, so discovery must read headers instead), and a crash between truncate and
header write creates a new state `Discover` must treat as a free slot rather than throwing — today
an unreadable header wipes the entire store.

Related, and pure-managed: **create the next segment eagerly at open** rather than at roll time. At
open nothing has been acknowledged yet, so losing that dirent costs nothing, and by the time a roll
consumes the file its entry has long since been committed. Removes the dangerous
create-then-immediately-acknowledge pattern without any interop.

### D5. Verify `F_FULLFSYNC` on Apple platforms (S, investigation)
Apple documents `fsync` as *not* forcing the drive to flush its write cache; that needs
`fcntl(F_FULLFSYNC)`. I believe .NET's `RandomAccess.FlushToDisk` uses plain `fsync` on Unix, which
would mean `Flush(true)` on iOS/macOS survives a process crash but not a power cut — a wider hole
than D3. Confirm against the runtime source before investing in D3/D4, and record the answer in
DESIGN.md either way.

---

## G — Space reclamation & file growth

### G1. Nothing ever calls `Compact()` (M — policy decision)
The only callers in the repo are three tests. Compaction is not on the open path, not on a write
threshold, not on a background timer. A host that never calls it never reclaims a byte: an
overwrite-heavy cache — which is exactly the ActualChat workload — grows monotonically. This is a
behavior regression against SQLite, which recycles freed pages without being asked. Decide a policy:
opportunistically at open with a work budget, after N bytes of superseded writes, or on the existing
flush loop's tick. At minimum, document it loudly — "call `Compact()` yourself or the store grows
forever" is not discoverable from the API.

### G2. Segments below 8 MiB can never be compacted (S)
`TryCompactOne` gates on `DeadBytes >= CompactionMinBytes` (4 MiB, absolute) **and**
`DeadBytes / SegmentBytes >= CompactionDeadRatio` (0.5). The two multiply into a hard floor: a
segment must be ≥ 8 MiB in total to ever qualify, so anything smaller is permanently ineligible even
at 100% dead. Make the absolute gate not apply below `CompactionMinBytes` — small segments should
qualify on the ratio alone. Draining them is nearly free, since their live bytes are small by
definition.

### G3. Nothing merges small sealed segments (M)
Compaction only ever drains a single victim into the active tail; there is no notion of merging two
sealed segments. Consider draining any sealed segment well under `SegmentBytes` regardless of dead
ratio — it buys back a file and a file descriptor for near-zero copy cost, which (see G5) is the
currency that actually runs out.

### G4. One new segment per killed session (M)
`Initialize` rolls to a fresh segment on an unclean open (no `.clean` marker) whenever the active
segment has ≥ 1 page. That roll is load-bearing — it prevents page-nonce reuse after unflushed pages
are lost — but on iOS/Android, where the OS kills backgrounded apps routinely, it fires roughly once
per session that wrote anything. Growth is therefore per launch, not per 16 MiB, and with G2 every
one of those small segments is immortal. Fixing G2/G3 makes them reclaimable; D4 makes them free.
Don't "fix" this by weakening the roll.

### G5. Segment count == open file-descriptor count (M)
`SegmentSet.Discover` opens *every* segment and `SegState` holds each `SafeFileHandle` for the
store's lifetime. With the intended sizing (~7 segments for a 100 MB store) that's fine; with G2+G4
it grows without bound, and iOS ships `RLIMIT_NOFILE` in the low hundreds. Open is also O(files) in
syscalls. If the file count can't be bounded structurally, segments need lazy open + an LRU of
handles.

---

## C — Correctness

### C1. Distinct keys with an identical 64-bit hash evict each other (L, pre-existing)
Tracked by the skipped `EdgeCaseTests.HashCollisionFanOut_KnownBug`. `HashIndex` keys slots by the
64-bit hash alone (full keys aren't in RAM by design, §6.1), so `Set`/`Scan`/`Remove` treat any two
same-hash keys as one entry and a later `Set` silently evicts the earlier one. On-disk full-key
verification prevents returning a *wrong* value but doesn't provide the collision fan-out §14
implies. Astronomically rare under SipHash-2-4; reachable with a caller-supplied hasher such as the
built-in xxHash3. Unskip the test when the index can disambiguate.

### C2. An unknown value-kind tag is not rejected (S)
SPEC §4.3 says an unknown tag ⇒ corrupt ⇒ regenerate, but `RecordCodec.TryParse` casts
`body[1]` straight to `KvasarValueKind` with no range check. Harmless today — the public API can't
produce a kind other than `Raw` (the kind-taking constructor is `internal`) — but it means a
corrupted or forward-version record decodes as `Raw` and is served as data rather than rejected.

---

## A — API & format leftovers (from this session's `KvasarKey`/`KvasarValue` work)

### A1. A null `string`/array converts to a *present empty value*, not a delete (done — documented)
`KvasarValue?` = `null` deletes; `KvasarValue` built from a null `string`/`byte[]`/`char[]` is a
present, empty value. That trap predates the change (the `ReadOnlyMemory<byte>?` comments in
`PropertyTests`/`CrashFuzzTests` are about exactly this), but the new implicit conversions widen it:
`string? v = MaybeNull(); store.Set(k, v)` now compiles and silently stores an empty value where the
author meant a delete. Options: drop the nullable-source conversions and require an explicit
`new KvasarValue(...)`, or keep them and make the XML doc say it in one line.
**Resolved:** the conversions stay (dropping them is a breaking API change); `KvasarValue`'s summary
and the comment above its conversion operators now state the behaviour, covered by
`KvasarValueTests.NullStringOrArrayIsAPresentEmptyValue`.

### A2. `KvasarValue` equality/`GetHashCode` is speculative API (S)
Added for symmetry with `KvasarKey`; nothing uses it, and `GetHashCode` over a multi-megabyte value
is a footgun with no current caller. `KvasarKey`'s equality is justified (dictionary keys).
Consider removing the value-side implementation.

### A3. `Kind` / `Require` are inert in v1 (no action, note only)
Only `Raw` exists and user code can't construct another kind, so `Require(Raw)` can never throw
through the public API. It's forward scaffolding for typed values (§4.3); keep it, but don't mistake
it for a live check.

### A4. `Version` is unrecoverable from disk (no action, note only)
`KvasarOptions.Version` is folded into the on-disk `formatVer` tag via FNV-1a, which is what makes it
free (no format change, authenticated as GCM AAD, no reserved key polluting `Scan`/`Stats`). The
trade: the previous version string can't be read back, so "migrate in place instead of wiping" is
permanently off the table. Fine for a regenerable cache; revisit if this store ever holds anything
that should be upgraded rather than rebuilt.

---

## P — Performance

### P1. Compaction decrypts the whole store on every pass (S)
`TryCompactOne` walks `_segments.ScanAll()` and filters to `loc.SegmentId != target`, so each pass
decrypts every page of every segment. `Compact()` loops passes, so draining K segments is
O(K × whole log) of AES-GCM. Use `ScanFrom(target, 0)` and break once the id exceeds the target.

### P3. The flush loop is a fixed-period timer, not a deadline (S)
`RunFlushLoop` (`KvasarStore.cs:552`) does `await Task.Delay(_flushDelay)` in an unconditional loop,
so it wakes every 500 ms for the life of the process whether or not anything was written. On a
backgrounded mobile app that is two pointless wakeups a second — a battery cost, not just untidiness.
It should arm the delay on the **clean → dirty transition** instead: `await WhenDirty()`, then
`Task.Delay(FlushDelay)`, then commit. Worst-case staleness is unchanged (`FlushDelay` either way,
since the first write in a batch waits the full delay and later ones wait less), so the periodic form
buys nothing for its wakeups. Found in this session, not by any of the four review passes; see
[`DESIGN-Durability.md`](DESIGN-Durability.md) §2.2, which also adds the byte-based commit trigger
that the recovery-validation bound actually requires.

### P2. Benchmarks not re-run after this session's hot-path changes (S)
CLAUDE.md requires re-running and updating [`BENCHMARKS.md`](BENCHMARKS.md) when a hot path changes.
`Set` now allocates the `*Locked` `Task` and passes a wider `KvasarValue?` (the `Kind` byte pushes
`Nullable<KvasarValue>` past `ReadOnlyMemory<byte>?`), and `SegmentSet.Flush` changed shape. Expected
to be noise against AES-GCM plus I/O, but that's a prediction, not a measurement.

---

## T — Testing & verification gaps

### T3. I28's fix has no test that isolates it (S, blocked on the store rewrite)
The fix is in (`KvasarStore.cs:235`, `:443` — the filters now let `OperationCanceledException`
through). The regression test is not, and the reason is worth recording: `PagedSegment.Prefetch`
already rethrows `OperationCanceledException`, and `Scan` calls it **outside** the try/catch, so a
cancelled scan throws via prefetch whether or not the filter is fixed. A test written the obvious way
passes against the unfixed code — verified by reverting the fix and watching it still pass. Isolating
the filter needs cancellation injected at the `TryReadRecord` call specifically, which becomes
straightforward once the store runs on `IStorageBackend` and the fake can inject it. Write it then.

### T1. Cancellation tests are timing-based, not deterministic (M)
`Store/CancellationTests` cancels on sub-millisecond timers, so on a fast machine the token may never
land mid-append — they're smoke tests, not proof that the uninterruptible-write contract holds. The
repo already has the fault-injection machinery (`FakePageCipher`, the crash-fuzz harness); the real
test injects cancellation deterministically at page N of a multi-page append.

### T2. No durability item (D1–D5) is testable in this suite (L)
`CrashFuzzTests`/`ProcessCrashRecoveryTests` kill a process, which does **not** drop the OS page
cache, so they cannot distinguish a durable write from a merely-written one. Verifying any fsync
claim needs device-level fault injection — `dm-flakey` on Linux or VM snapshot / power-cut harnesses.
Until then, the honest status in DESIGN.md is "reasoned, not verified".

---

## Docs

- Record the cancellation contract's caller-visible consequence: a cancelled write **may still land**,
  and the caller has no way to learn whether it did (SPEC §4.4 covers the rule, not this corollary).
- Add the directory-entry limitation (D3) and the Apple `F_FULLFSYNC` answer (D5) to DESIGN.md's
  known limitations, next to the segment-rollback entry.
- State the compaction trigger policy chosen in G1 in both SPEC §9 and the README.
