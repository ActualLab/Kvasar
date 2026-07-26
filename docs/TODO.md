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
implies. **Investigated 2026-07-25; deliberately postponed — the test stays skipped.**

#### What the code actually does

*Reads are already fan-out ready.* `Get`/`GetSlow` walk every candidate `ProbeCursor` yields, skip the
ones whose `CurrentHash` differs, and full-key-verify the rest on disk; `Scan` walks `Snapshot()` slot
by slot. Nothing on the read path assumes one slot per hash — so no read change is needed at all.

*The writer loses the fan-out, and not only in `HashIndex.Set`.* Five places collapse same-hash keys:

- `HashIndex.Scan` (behind `Set`) and `RemoveCore` both stop at the **first** live slot whose hash
  matches, so an update and a remove hit whichever colliding key got there first.
- `KvasarStore.Publish` and `ApplyLoaded` pick that victim with `TryGetFirst(h)` — first slot for the
  hash, no key check — for the index update **and** for the `OnSuperseded` dead-byte accounting.
- `TryCompactOne` calls a record dead when `TryGetFirst(h) != loc`, so with two slots the loser's live
  record is dropped by the next compaction (data loss, not just shadowing).
- `IndexFile.Parse` resolves checkpoint + delta tail through a `Dictionary<ulong, IndexEntry>` keyed by
  `KeyHash`, and `HashIndex.BulkLoad` is last-writer-wins per hash. Even a perfect in-RAM index
  collapses back to one entry per hash on the next open.

So **no** — fixing eviction in `Set` alone does not buy fan-out. `Set` can't even tell "same key" from
"same hash": the only datum that distinguishes two slots is the locator, which only the store knows how
to match against a key, and only by reading the record.

#### Options

**A. True fan-out.** `HashIndex` becomes locator-addressed: `Set(hash, oldLoc, newLoc, len)` (insert
when `oldLoc` is `None`), `Remove`/`Scan` probe past hash matches whose locator differs, plus a cheap
`Contains(hash, loc)` for compaction's liveness test. `Publish` must then decide *which* slot belongs
to the key it is writing, and only the key bytes on disk can tell it — a record read (page decrypt when
uncached) **per overwrite and per delete**, on a write path that today never reads. The `.kidx` must
also say which entry a delta supersedes: either widen `IndexEntry` with the superseded locator (+8 B on
a 21-byte entry, ~38%) or write two deltas per overwrite (a locator-matched remove + an add) and key
`Parse`'s resolution by `(hash, locator)`. Either way the delta stream changes meaning ⇒ a
`FormatVersion` bump ⇒ every existing store is wiped on upgrade. Touches `HashIndex`'s hot path, all
eight mutating index call sites in `KvasarStore`, and `IndexFile`.

**B. Make collisions unreachable rather than handled.** Widen the index hash to 128 bits: `IKeyHasher`
(public) returns 128 bits, `IndexEntry` grows 8 B, the table grows 8 B/slot, every hasher is
reimplemented. It doesn't satisfy §14's fan-out wording, but it removes the failure mode *including*
for attacker-chosen keys, which is the only realistic way in.

**C. Detect instead of silently losing.** Full-key-verify the single candidate at write time and
throw/log on a genuine collision. Pays A's per-overwrite read without fixing anything, and converts a
silent single-key loss into a hard failure for that key.

**D. Constrain the contract and say so.** Keys must have distinct 64-bit hashes under the configured
hasher. With the default keyed SipHash-2-4 the chance that *any* pair of n keys collides is ≈ n²/2⁶⁵ —
about 3·10⁻⁸ at n = 10⁶ — and an attacker can't aim without the store key. With the built-in unkeyed
`XxHash3`, collisions are constructible offline, so a store fed attacker-chosen keys can be made to
drop entries at will. Zero code, and it's the part that's currently under-documented.

#### Recommendation

**D now**; **B** if Kvasar ever ingests untrusted keys under a non-keyed hasher; **A** only inside a
change that already rewrites the `.kidx` format (it composes with D1's fsync-before-rename and I34's
entry padding) — not on its own, and not while `HashIndex` is being rewritten for other reasons. The
current behavior is a bounded single-key loss on a regenerable cache that self-heals on the next miss,
which does not justify a read per overwrite plus a format break.

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

### A2. `KvasarValue` equality/`GetHashCode` was speculative API — **removed**
`IEquatable<KvasarValue>`, `Equals`, `GetHashCode` and `==`/`!=` are gone; `KvasarKey` keeps its own
(it really is used as a dictionary key). Rationale: nothing in the library, tests or benchmarks
compared values; values are the large side of the store (up to `MaxValueBytes`, 8 MiB by default), so
a content `GetHashCode` is an O(n) trap that a `HashSet<KvasarValue>` or `Distinct()` would spring
invisibly; and adding an implementation back later is source-compatible, while removing one isn't.
The known cost: `a.Equals(b)` still compiles and now means `ValueType.Equals`, i.e. "same slice of the
same array" rather than "same bytes" — `a == b` no longer compiles at all, which is the louder half.
Callers wanting content equality write `a.Span.SequenceEqual(b.Span)`.

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

### P3. The flush loop is a fixed-period timer, not a deadline (S) — **DONE**
Fixed: the delay is armed on the clean⇒dirty edge, so an idle store costs zero wakeups. The
`CommitBytes` trigger from [`DESIGN-Durability.md`](DESIGN-Durability.md) §2.2 lands with the store
rewrite — without a superblock there is no commit window for it to bound. Original writeup:


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

### T1. Cancellation tests are timing-based, not deterministic — **done**
`Store/CancellationTests` now has three gate-driven tests alongside the timer-based smoke ones. A
`Gate` parks the writer at an exact point and the test thread cancels while it's parked, so the token
provably lands mid-write: `GatedMemory` (a `MemoryManager<byte>` behind the value) parks inside the
multi-page append itself, `GatedHasher` parks between the append and the index publish, and the third
test cancels a writer queued on the write lock. Each asserts the write is invisible while parked, then
that it landed whole (and survives a reopen) — or, for the queued writer, that nothing landed at all.
The one seam still missing is per-page injection: `IPageCipherFactory` isn't reachable from
`KvasarOptions`, so "cancel between page 3 and 4" can't be expressed through the public API.

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
