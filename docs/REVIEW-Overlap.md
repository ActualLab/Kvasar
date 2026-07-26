# Kvasar — review cross-validation

Every issue raised by the four review passes, merged into one ID space and ordered by priority.
**38 distinct issues**; **29 (76%) were found by exactly one agent**; **none was found by more than
two**.

> **Scope: the code as of `ab2fc34`.** All four passes read that commit. Anything introduced by
> `8c31b30` (`KvasarKey`/`KvasarValue`, `KvasarOptions.Version`, uninterruptible writes, the new
> `CancellationTests`) is **excluded** — the three cold-read agents could not have found defects in
> code that didn't exist yet, and counting those items inflates the working session's coverage
> against reviews that never had the chance. Seven such items were dropped; see the bottom.
> Verification of the surviving items was still done against HEAD (`5c982d0`), where all of them
> are still present.

> Numbers encode priority order and shift whenever the priority model is revised — the
> **ShortTitle is the stable identifier**. See the mapping at the bottom.

## Agents

| Col | Source | Agent |
|---|---|---|
| **H+O** | [`TODO.md`](TODO.md) + [`example-session.md`](example-session.md) | Human + Claude Opus 5 — a ~40-minute interactive session: the author reading the code, Opus 5 answering questions. Scoped to the public API and the calls made from it, not the whole codebase |
| **O5** | [`REVIEW-Opus5-High.md`](REVIEW-Opus5-High.md) | Claude Opus 5, high effort, cold read |
| **F5** | [`REVIEW-Fable5-High.md`](REVIEW-Fable5-High.md) | Fable 5, high effort, cold read |
| **G5** | [`REVIEW-Gpt5Sol-High.md`](REVIEW-Gpt5Sol-High.md) | GPT-5 (Sol), high effort, cold read |

`X` = found and stated as its own finding. `(x)` = partial — suspected but not confirmed, a subset of
the issue, or noted only as pre-existing/known. Blank = not raised.

## Priority model

Kvasar is a **regenerable cache**, so loss is recoverable by definition — a miss just triggers an
upstream lookup. Severity therefore comes from three factors, in this order:

1. **Failure kind.** *Serves garbage* (bytes the caller never stored, returned as valid — no
   downstream defense) ≫ *serves stale/resurrected* (a real value that should be gone) ≫
   *reports missing* (costs one lookup).
2. **Volume.** Scales all three. A miss on 0.01% of keys is noise; a miss on 90% means the cache
   has stopped being a cache — refetch storm, cold-start latency.
3. **Persistence.** Survives restart ≫ confined to the current session. A wipe-and-rebuild is a
   *fix*, not a failure.

**Exceptions are not a mitigation.** `Get` and `Set` are the load-bearing API, and at the caller's
boundary neither surfaces failure: a throw from `Get` is indistinguishable from a miss (the app
refetches), and a throw from `Set` is ignored — the app proceeds as if the write landed. So
"fails loudly" collapses to "fails silently", and *every* defect lands in one of the two kinds
above. Only two escapes exist: `Open` failing outright, and a wipe-and-rebuild — the latter being
the recovery path, not a failure.

**Price each issue standalone; let dependents raise the root cause.** Where issue B only reaches its
worst outcome because issue A puts the store in the required state, B is rated on what it does *by
itself*. A is then rated on its own effect **plus the union of everything all its dependents
unlock** — the combined outcome lands on the root, once, and is not re-charged to any dependent.
Charging the composed severity to both double-counts it and misdirects effort: it makes the
amplifier look as urgent as the trigger, when fixing the trigger alone defuses both.

The bump is not automatic. If the dependents' combined effect is small — or the root already tops
the scale on its own — the root's priority doesn't move. The relationship is still worth recording,
because it drives fix order. Amplifiers are marked **⟵ amplifies I*n*** below.

Consequences worth stating, because they cut against the usual instincts: a silent, permanent
**miss** is not automatically severe — small volume makes it near-noise. Any path that can serve
**garbage** is top-tier regardless of volume. An issue that a restart clears drops a full tier.
And throwing instead of returning wrong data buys nothing on its own — it only helps when the
throw prevents the store from *reaching* the wrong-data state.

Security sits outside this model: the store's reason to exist is that it's encrypted, and no
recovery path undoes a disclosure. A crypto break is P0 by default, regardless of how it scores on
the cache-availability axes.

- **P0** — a crypto break, or: permanent **and** (serves wrong data or takes out most of the store).
- **P1** — permanent, but miss-kind or bounded volume; or growth that ends in total unavailability.
- **P2** — bounded volume, deterministic enough to be caught in development, or cleared by a
  restart; missing guards on destructive paths; verification gaps.
- **P3** — hygiene, API shape, perf nits, docs, notes.

---

## Cross-validation matrix

| # | Issue | Pri | H+O | O5 | F5 | G5 |
|---|---|:---:|:---:|:---:|:---:|:---:|
| 1 | **I1-StaleCleanMarkerNonceReuse** | P0 | | **X** | | |
| 2 | **I2-ScanAbortsLaterSegments** | P0 | | **X** | | |
| 3 | **I3-KidxDeltaTailMisalignment** | P0 | | | **X** | |
| 4 | **I4-TombstoneResurrection** | P0 | | (x) | **X** | |
| 5 | **I5-CompactNeverCalled** | P1 | **X** | | | |
| 6 | **I6-SmallSegmentsNeverCompacted** | P1 | **X** | | | |
| 7 | **I7-SegmentPerKilledSession** | P1 | **X** | | | |
| 8 | **I8-KidxRenameWithoutFsync** | P1 | **X** | | | |
| 9 | **I9-WrongKeyAcceptedWhenActiveEmpty** | P1 | | | | **X** |
| 10 | **I10-CheckpointHwmOutrunsLog** | P1 | | | **X** | **X** |
| 11 | **I11-CompactionUnfsyncedBeforeDelete** | P1 | | **X** | (x) | |
| 12 | **I12-KidxCheckpointUnauthenticated** | P1 | (x) | | | **X** |
| 13 | **I13-NoSmallSegmentMerge** | P1 | **X** | | | |
| 14 | **I14-NoParentDirFsync** | P1 | **X** | | | |
| 15 | **I15-AppleFullFsyncUnverified** | P1 | **X** | | | |
| 16 | **I16-OversizedValueKeepsOldValue** | P1 | | **X** | | |
| 17 | **I17-CompactionDeletesUnscannedSegment** ⟵ *amplifies I2* | P2 | | **X** | | |
| 18 | **I18-SegmentCountEqualsFdCount** ⟵ *amplifies I5–I7* | P2 | **X** | | | |
| 19 | **I19-UnknownValueKindAccepted** | P2 | **X** | | | |
| 20 | **I20-RemoveSegmentThrowsOrphans** | P2 | | | **X** | |
| 21 | **I21-FullSizeTornPageWipesStore** | P2 | | | **X** | |
| 22 | **I22-ArbitraryCompactionVictim** | P2 | | **X** | | |
| 23 | **I23-NoKeySizeCap** | P2 | | | **X** | |
| 24 | **I24-SegmentBytesOverflow** | P2 | | **X** | **X** | |
| 25 | **I25-FlushFsyncsEverySegment** | P2 | **X** | | | |
| 26 | **I26-CompactionDecryptsWholeStore** | P2 | **X** | (x) | | |
| 27 | **I27-GetManySequential** | P2 | | | **X** | |
| 28 | **I28-ReadCancellationSwallowed** | P2 | | | **X** | **X** |
| 29 | **I29-HashCollisionEviction** | P2 | **X** | | | (x) |
| 30 | **I30-DurabilityUntestable** | P2 | **X** | | | |
| 31 | **I31-WipeDeletesUnrelatedFiles** | P3 | | | | **X** |
| 32 | **I32-SetManyDoubleHash** | P3 | | **X** | | |
| 33 | **I33-UndisposedFields** | P3 | | **X** | (x) | |
| 34 | **I34-UnalignedIndexEntry** | P3 | | **X** | | |
| 35 | **I35-PageSizeProbeDocLie** | P3 | | **X** | | |
| 36 | **I36-BlockingKidxFsync** | P3 | | **X** | | |
| 37 | **I37-StoreLockIOExceptionMapping** | P3 | | | **X** | |
| 38 | **I38-SegmentRecycling** | P3 | **X** | | | |
| | **Totals** (X + x) | | **15** | **14** | **12** | **6** |

### Totals by priority

| Agent | P0 | P1 | P2 | P3 | Total | Unique to them |
|---|---:|---:|---:|---:|---:|---:|
| **H+O** Human + Opus 5 | 0 | 8 | 6 | 1 | **15** | 12 |
| **O5** Opus 5 | 3 | 2 | 4 | 5 | **14** | 9 |
| **F5** Fable 5 | 2 | 2 | 6 | 2 | **12** | 6 |
| **G5** GPT-5 Sol | 0 | 3 | 2 | 1 | **6** | 2 |
| *union* | *4* | *12* | *14* | *8* | ***38*** | |

No agent found more than **15 of 38 (39%)**. Nine issues were found by two agents
(I4, I10, I11, I12, I24, I26, I28, I29, I33); the other 29 by one.

**Every P0 came from a dedicated review pass.** The H+O session found none, and *coverage doesn't
explain it*: only I2 lives deep in `SegmentSet`'s internals, outside a top-down read from the API.
The other three sit in `KvasarStore` itself, and two are in code that session demonstrably read —
it reported I7 four lines from I1's stale-marker check in `Initialize`, and I5, I6 and I26 all
describe the very method that contains I4.

So the gap is the *kind* of reading, not the amount. Asking "what does this not handle" over
`TryCompactOne` yields "nothing calls it", "the thresholds exclude small segments", "it decrypts
too much". Asking "what breaks when this runs" over the same method yields the resurrection bug.
Both passes read it; only one was walking failure paths.

---

## Issue reference

`Chk` = I re-read the cited code at `5c982d0` and the described shape is present (**✔**), or the
claim is behavioral/environmental and rests on the agent's repro rather than a static read (**~**).

### P0 — crypto break, or permanent + (wrong data or most of the store gone)

| ID | What | Site | Chk |
|---|---|---|:---:|
| **I1-StaleCleanMarkerNonceReuse** | `.clean` is written on every graceful close but consumed only when `FlushDelay > 0`, so it degrades from "*last* run closed cleanly" to "*some* run did" — skipping the safety roll and reusing a `(fileSalt, pageId)` GCM nonce, which leaks the XOR of two plaintext pages. Ranked first because nothing recovers it: no restart, wipe, or reinstall un-discloses bytes already on disk | `KvasarStore.cs:151` vs `:406-416` | ✔ |
| **I2-ScanAbortsLaterSegments** | `ScanFrom`'s torn-tail `yield break` exits the **outer** segment loop, so one unparseable record discards every later (newer) segment — a `.kidx`-less rebuild recovered **0 of 200 keys**. Permanent: the empty rebuild is then checkpointed. **Carries the combined impact of I17**, which turns the same trigger into physical deletion of 25 intact segments | `SegmentSet.cs:264` | ✔ |
| **I3-KidxDeltaTailMisalignment** | The only reachable **garbage-serving** path: after a torn delta write, `OpenDeltaStream` seeks to raw EOF, so every later delta is misaligned. Random hashes/locators enter the index and a random locator can parse as a plausible record, leaking a fabricated `(key, value)` pair out of `Scan`. Self-propagating — the misalignment never heals, and the next checkpoint persists the garbage | `KvasarStore.cs:599` | ✔ |
| **I4-TombstoneResurrection** | Compaction drops tombstones and deletes their segment while the key's original record survives in an earlier one; any later log rebuild replays it and the **deleted key comes back with its old value**. Permanent, and independently reachable — with `IndexEncryption.On` (or `Auto` + a non-keyed hasher) `.kidx` is never persisted, so every open rebuilds via `ScanAll` | `KvasarStore.cs:649` | ~ |

### P1 — permanent, but miss-kind or bounded volume; or growth ending in total unavailability

I5–I7 are one cluster and are listed first because they compound: nothing reclaims space, small
segments are immortal, and every killed session mints another one. Their terminal state — the store
no longer opening at all — arrives through **I18**, and is priced here, in the roots.

| ID | What | Site | Chk |
|---|---|---|:---:|
| **I5-CompactNeverCalled** | **Nothing ever calls `Compact()`** — only three tests. Not on open, not on a write threshold, not on a timer. An overwrite-heavy cache grows monotonically forever | `KvasarStore.cs:289` | ✔ |
| **I6-SmallSegmentsNeverCompacted** | `DeadBytes ≥ 4 MiB` **and** `dead/total ≥ 0.5` multiply into a hard **8 MiB size floor**, so a smaller segment is permanently ineligible even at 100% dead | `KvasarStore.cs:633-635` | ✔ |
| **I7-SegmentPerKilledSession** | An unclean open rolls to a fresh segment whenever anything was written, so on mobile growth is **per launch**, not per 16 MiB — and I6 makes those files immortal | `KvasarStore.cs:412-414` | ✔ |
| **I8-KidxRenameWithoutFsync** | `.kidx` checkpoint is `WriteAllBytes` + `Move` with **no fsync of the temp** — the ext4 delayed-allocation hole; a partially-written index parses zeroed entries as valid and silently drops keys. Miss-kind, but the volume can be large and it is permanent | `IndexFile.cs:86` | ✔ |
| **I9-WrongKeyAcceptedWhenActiveEmpty** | `Discover` authenticates page 0 of the *active* segment only, so an empty active segment means a **wrong master key is accepted**: the `.kidx` is trusted, lookups silently miss, and later writes mix keys across segments — a state that gets worse and never heals | `SegmentSet.cs:460` | ✔ |
| **I10-CheckpointHwmOutrunsLog** | `WriteCheckpoint` snapshots `ActiveLogicalHwm` **without flushing the log**, so a deferred-mode checkpoint can stamp an HWM past the durable `.klog`; recovery trusts it and never rescans, so an already-*flushed* value becomes permanently unreachable | `KvasarStore.cs:610` | ✔ |
| **I11-CompactionUnfsyncedBeforeDelete** | Compaction deletes the source segment after a **non-fsynced** `Flush(false)` (and without flushing `_kidxDelta`), so a power loss can persist the unlink without the copies — one segment's worth, permanently | `KvasarStore.cs:660→666` | ✔ |
| **I12-KidxCheckpointUnauthenticated** | The `.kidx` checkpoint has **no integrity check** (no MAC/checksum, no validation against the log); a structurally valid bit flip is accepted and the real record below the HWM is never rediscovered | `IndexFile.cs:92` | ✔ |
| **I13-NoSmallSegmentMerge** | Nothing **merges small sealed segments** — compaction only ever drains one victim into the active tail. Feeds the I5–I7 cluster | `KvasarStore.cs:629` | ✔ |
| **I14-NoParentDirFsync** | **No parent-directory fsync** after creating a `.klog`, so a new segment's dirent isn't guaranteed durable and `Flush(true)` can lose an acknowledged write (Unix only; needs libc P/Invoke) | `SegmentSet.cs:416` | ✔ |
| **I15-AppleFullFsyncUnverified** | `RandomAccess.FlushToDisk` likely maps to `fsync`, not `F_FULLFSYNC`, so on iOS/macOS `Flush(true)` may not survive a power cut **at all** — which would void every durability claim on Apple platforms. Unverified | `PagedSegment.cs:234` | ~ |
| **I16-OversizedValueKeepsOldValue** | `Set` with an oversized value **silently keeps the old value** (`Locator.None` → `Publish` early-returns), so the key serves **stale data permanently**. `OversizedValueThrows` doesn't help — a throw from `Set` is ignored by the caller, which then believes the new value is stored. Stale-kind (above every miss-kind item above it) but low volume, hence bottom of P1 | `KvasarStore.cs:457→469` | ✔ |

### P2 — bounded volume, caught in development, cleared by a restart, or a missing guard

The first two entries are **amplifiers**: each is rated on what it does alone, with the composed
outcome charged to its root cause. Both still need fixing — an amplifier left in place re-arms the
full failure the moment anything reintroduces the trigger.

| ID | What | Site | Chk |
|---|---|---|:---:|
| **I17-CompactionDeletesUnscannedSegment** ⟵ *amplifies I2* | `TryCompactOne` runs `RemoveSegment(target)` unconditionally, even when the scan never reached `target`. **Standalone this is a missing invariant check**: SPEC §9 requires "a segment is deleted only after no index entry references it", and the code infers that from a scan rather than asserting it. It cannot fire on its own — `TryCompactOne` has no try/catch, so cancellation, decrypt failure and `KvasarCorruptException` all propagate past `RemoveSegment`; only a *silent* early exit reaches it, and I2's `yield break` is the only one in the codebase. Composed with I2 it deleted 25 intact segments (26 → 1), and that impact is priced at I2 | `KvasarStore.cs:666` | ✔ |
| **I18-SegmentCountEqualsFdCount** ⟵ *amplifies I5–I7* | `Discover` opens **every** segment and holds each handle for the store's lifetime; open is also O(files) in syscalls. **Standalone this is a scaling limit, not a defect**: at the intended sizing (~7 segments per 100 MB) it's free, and even a 1 GB store is 64 descriptors — uncomfortable against iOS's low-hundreds `RLIMIT_NOFILE`, but bounded by store size. It becomes fatal only under I5–I7's unbounded growth, and that impact is priced there. Wants lazy open + an LRU of handles | `SegmentSet.cs:448-453` | ✔ |
| **I19-UnknownValueKindAccepted** | The value-kind byte is cast straight to the enum with no range check, so a corrupt or forward-version record decodes as `Raw` and is served as data (SPEC §4.3 says regenerate). Garbage-kind, but gated behind AES-GCM page auth, so barely reachable today | `RecordCodec.cs:92` | ✔ |
| **I20-RemoveSegmentThrowsOrphans** | Handles are opened without `FileShare.Delete`, so on Windows an in-flight read makes `File.Delete` fail *after* `_states.TryRemove` — compaction silently aborts (the caller ignores the throw) and leaves an orphaned segment that feeds the growth cluster | `SegmentSet.cs:330`, `PagedSegment.cs:72,94` | ✔ |
| **I21-FullSizeTornPageWipesStore** | A torn-but-**full-size** unauthenticatable trailing page makes `Decrypt` throw out of `ScanFrom`, so `Open` wipes the whole store instead of truncating the tail per SPEC §8. 100% loss — but one-time and self-healing, i.e. exactly the recoverable case | `SegmentSet.cs:254`→`PagedSegment.cs:289` | ✔ |
| **I22-ArbitraryCompactionVictim** | `SealedSegments()` yields in `ConcurrentDictionary` order, so the victim is arbitrary rather than oldest or deadest — which is what widens I4's window | `SegmentSet.cs:297` | ✔ |
| **I23-NoKeySizeCap** | `MaxValueBytes` bounds only the value; `GetRecordLength` sums key+value in `int`, so a ~2 GB key overflows to a negative length. Absurd input, deterministic failure | `RecordCodec.cs:15-19` | ✔ |
| **I24-SegmentBytesOverflow** | `SegmentBytes` is unvalidated against the 32-bit `Locator.Offset` — anything over 4 GiB throws `OverflowException` out of `Set` mid-append instead of being rejected at `Open`. Every write then fails silently, but it's deterministic and config-time, so it can't escape development | `SegmentSet.cs:357`, `:389` | ✔ |
| **I25-FlushFsyncsEverySegment** | `Flush(true)` fsyncs **every** segment, including sealed ones this process only read — N blocking syscalls and N blocked threadpool threads where 1 would do (wants `_hasUnsyncedWrites`) | `PagedSegment.cs:233`, `SegmentSet.cs:312` | ✔ |
| **I26-CompactionDecryptsWholeStore** | `TryCompactOne` walks `ScanAll` and filters to one victim, so draining K segments is O(K × whole log) of AES-GCM | `KvasarStore.cs:648` | ✔ |
| **I27-GetManySequential** | `GetMany` is a sequential per-key loop; SPEC §6.4 promises sort-by-locator + per-page batching, and this is the `IBatchingKvasBackend` hot path (a cold 64-key batch pays 64 random I/Os) | `KvasarStore.cs:196-198` | ✔ |
| **I28-ReadCancellationSwallowed** | `TryReadValue` and `Scan`'s filters catch everything but `KvasarCorruptException`. For `Get` this is nearly a no-op — the caller treats a throw as a miss anyway. The real half is `Scan`, which *completes normally* with silently partial results. Session-only | `KvasarStore.cs:444`, `:236` | ✔ |
| **I29-HashCollisionEviction** | Distinct keys with an identical 64-bit hash **evict each other** — `HashIndex` keys slots by hash alone. Permanent, but miss-kind and astronomically rare under SipHash-2-4 (reachable with a caller-supplied non-keyed hasher) | `HashIndex` / `EdgeCaseTests.cs:124` | ✔ |
| **I30-DurabilityUntestable** | Killing a process doesn't drop the OS page cache, so no durability claim (I8, I14, I15) is testable here without device-level fault injection (dm-flakey, VM power-cut) | `CrashFuzzTests`, `ProcessCrashRecoveryTests` | ✔ |

### P3 — hygiene, API, docs

| ID | What | Site | Chk |
|---|---|---|:---:|
| **I31-WipeDeletesUnrelatedFiles** | `WipeFiles` matches any `<base>.*` ending in `.klog`, so wiping `cache` also deletes `cache.backup.klog`. Low impact in practice: a file in the store's own directory sharing its base name and the `.klog` suffix is almost certainly Kvasar's own | `KvasarStore.cs:793-800` | ✔ |
| **I32-SetManyDoubleHash** | `SetMany` hashes every key twice — once building `lastByHash`, again in the append loop | `KvasarStore.cs:271`, `:327` | ✔ |
| **I33-UndisposedFields** | `_disposeCts` and `_writeLock` are never disposed | `KvasarStore.cs:128-167` | ✔ |
| **I34-UnalignedIndexEntry** | `IndexEntry` is 21 bytes under `Pack = 1`, so `MemoryMarshal.Cast` yields unaligned `ulong KeyHash` reads; padding to 24 would be safer and marginally faster | `IndexEntry.cs:8` | ✔ |
| **I35-PageSizeProbeDocLie** | `KvasarOptions.PageSize` doc claims "0 ⇒ probe the FS cluster size"; `ResolvePageSize` just uses the 4 KiB default | `KvasarOptions.cs:20` vs `KvasarStore.cs:730` | ✔ |
| **I36-BlockingKidxFsync** | `KvasarStore.Flush(fsync)` blocks the caller's thread on `_kidxDelta.Flush(true)` while `_segments.Flush` correctly offloads its fsync | `KvasarStore.cs:373` | ✔ |
| **I37-StoreLockIOExceptionMapping** | `StoreLock` maps **every** `IOException` (bad path, disk full) to "already open in this or another process" | `StoreLock.cs:22` | ✔ |
| **I38-SegmentRecycling** | Recycle segment files instead of deleting them (truncate + fresh `fileSalt` + next id) — the structural alternative to I14 that also removes the create-then-acknowledge pattern | `SegmentSet.cs:321-332` | ✔ |

---

## Dependency chains

Two amplifier relationships exist in the set. In both, the amplifier is priced standalone and the
root absorbs the combined outcome.

| Root | Amplifier | Composed outcome (charged to the root) | Did the bump move the root? | Effect of fixing the root alone |
|---|---|---|---|---|
| **I2** ScanAbortsLaterSegments (P0) | **I17** CompactionDeletesUnscannedSegment (P2) | 26 segments → 1; 25 intact segments physically deleted | **No** — I2 is already P0 standalone (0/200 keys, permanent) | I17 can no longer fire at all: no other silent early exit exists |
| **I5–I7** growth cluster (P1) | **I18** SegmentCountEqualsFdCount (P2) | fd exhaustion; the store stops opening | **No** — the terminal state is a throw from `Open` plus a recoverable wipe, which doesn't clear the P0 bar | I18 reverts to a scaling note at the intended segment count |

So **the bump changes no tier here**, which is the expected outcome when the root is already at the
ceiling or the combined effect stays inside its current band. Both chains are recorded for fix
ordering, not because anything moved: fix I2 and I5–I7 first, then close the amplifiers so a future
regression can't re-arm them.

`I22-ArbitraryCompactionVictim` is adjacent but not an amplifier — it widens `I4`'s window rather
than being required for it, and `I4` is independently reachable via the no-`.kidx` rebuild path.

## Notes on the overlaps

| Issue | Agents | What differed |
|---|---|---|
| **I4** TombstoneResurrection | O5 (x), F5 X | O5 reasoned it out under "Possible, not reproduced"; F5 confirmed it with a failing test |
| **I10** CheckpointHwmOutrunsLog | F5 X, G5 X | The only issue two agents independently reproduced |
| **I11** CompactionUnfsyncedBeforeDelete | O5 X, F5 (x) | F5 flagged only the `_kidxDelta` half of the same ordering gap |
| **I12** KidxCheckpointUnauthenticated | H+O (x), G5 X | H+O described the symptom as a consequence of I8's missing fsync; G5 named the missing integrity mechanism as the root |
| **I24** SegmentBytesOverflow | O5 X, F5 X | |
| **I26** CompactionDecryptsWholeStore | H+O X, O5 (x) | O5 raised it as a bonus inside its I17 fix, not as a finding |
| **I28** ReadCancellationSwallowed | F5 X, G5 X | |
| **I29** HashCollisionEviction | H+O X, G5 (x) | G5 listed it under "Existing documented issue", excluded from its five findings |
| **I33** UndisposedFields | O5 X, F5 (x) | F5 caught `_disposeCts` only, not `_writeLock` |

## Where each agent was strong

- **H+O (Human + Opus 5)** owns the growth cluster (I5–I7, I13, I18, I38) and the durability-policy
  items (I8, I14, I15, I30). What sets those apart isn't codebase familiarity — it's **product
  knowledge the other agents had no way to supply**: that the target is mobile, that iOS kills
  backgrounded apps and ships a
  low `RLIMIT_NOFILE`, that Apple's `fsync` may not be `F_FULLFSYNC`, that the workload is
  overwrite-heavy. Those facts are what turn "compaction is never triggered" from a nit into the
  largest cluster in the set. It found **zero P0s**, for the reason above: it was asking what the
  design doesn't cover, not what breaks when the code runs. 15 findings in 40 minutes against 14
  from a dedicated high-effort pass is a strong rate — the earlier 22 was inflated by items the
  other agents never had the chance to review.
- **O5 (Opus 5)** found 3 of the 4 P0s — the crypto break and the scan-abort root cause — plus its
  amplifier and the longest hygiene list. Strongest at both ends, thinnest in the middle.
- **F5 (Fable 5)** owns the `.kidx` write-path integrity story, including I3, the single
  garbage-serving path in the whole set, and was the only agent to confirm I4 with a failing test.
  Broadest P2 coverage.
- **G5 (GPT-5 Sol)** reported the fewest findings but the highest signal density: 6 findings, 5
  probe-verified, and almost no noise. I9 and I12 are things no other agent looked for.

## Excluded — postdate the reviewed commit

These were raised by the working session against code introduced in `8c31b30`, after all four
review passes read `ab2fc34`. They are real items, tracked in [`TODO.md`](TODO.md); they are simply
not comparable across agents, so they are kept out of the matrix.

| Item | Why out of scope |
|---|---|
| `NullConvertsToEmptyNotDelete` | `KvasarValue` implicit conversions — added in `8c31b30` |
| `SpeculativeValueEquality` | `KvasarValue` equality / `GetHashCode` — added in `8c31b30` |
| `InertValueKind` | `KvasarValue.Kind` / `Require` — added in `8c31b30` |
| `VersionUnrecoverable` | `KvasarOptions.Version` — added in `8c31b30` |
| `TimingBasedCancellationTests` | `Store/CancellationTests.cs` — added in `8c31b30` |
| `BenchmarksStale` | Staleness is caused by `8c31b30`'s hot-path changes |
| `DocsGaps` | Documents the cancellation contract and policy decisions from `8c31b30` |

## Already fixed (in `8c31b30`, after the reviews)

| ID | What | Found by |
|---|---|---|
| **F1-CancellableWriteCorruption** | A `CancellationToken` flowed from `Set(ct)` into `RandomAccess.WriteAsync`; cancelling a multi-page append left a record header claiming more bytes than exist, and recovery then swallowed every record after it. Write paths now take no token; public writers stay cancellable via `Task.WaitAsync(ct)` | H+O |
| **F2-SerialSegmentFsync** | `SegmentSet.Flush` fsynced segments serially — now overlapped (superseded by I25, which removes most of the fsyncs entirely) | H+O |

## Renumbering (previous revision → current)

Only I30 onward shifted, by one, as `TimingBasedCancellationTests` left the set.

| Was | Now |
|---|---|
| I1–I29 | unchanged |
| I30 `TimingBasedCancellationTests` | **removed** (out of scope) |
| I31 | I30 |
| I32–I39 | I31–I38 |
| I40–I45 | **removed** (out of scope) |
