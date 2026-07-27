# Kvasar — review round 2

Two independent cold-read reviews of the post-rewrite code, merged into one ID space and ordered by
severity. **23 distinct issues**; **18 (78%) were found by exactly one agent**, matching round 1's
one-agent-only rate almost exactly.

> **Scope: the code as of `72787a9`** (post `DESIGN-Durability.md` rewrite — the five-file layout with
> the authenticated superblock). Both agents read the same commit, the same design docs, and the four
> round-1 review docs, and were asked to mark each finding **NEW** or **KNOWN**.

> Numbers encode priority order and will shift if the priority model is revised — the **short title is
> the stable identifier**.

## Agents

| Col | Agent | Notes |
|---|---|---|
| **C** | Claude Opus 5, high effort, cold read | Built clean on net10.0 + net9.0 (0 warnings); wrote two throwaway repros under `tmp/` to verify R2 and R9 empirically |
| **X** | Codex (GPT-5.x), high effort, cold read | Static review only — its Release build never started (MSBuild denied access to its temp dir under the read-only sandbox) |

`X` = found and stated as its own finding. `(x)` = partial — a subset of the issue, or noted only in
passing. Blank = not raised.

**Verified by the consolidating pass**: R1, R2, R3, R4 and R10 were re-checked against source before
being written down here (see "Verification" at the bottom). The rest are reported as the agents
stated them.

## Priority model

Unchanged from [`REVIEW-Overlap.md`](REVIEW-Overlap.md): Kvasar is a regenerable cache, so severity
comes from **failure kind** (*serves garbage* ≫ *serves stale* ≫ *reports missing*), scaled by
**volume**, scaled by **persistence** (survives restart ≫ session-local). Exceptions are not a
mitigation — a throw from `Get` is indistinguishable from a miss, and a throw from `Set` is ignored.

---

## Summary table

| # | Short title | Sev | C | X | Status | Resolution |
|---|---|---|:-:|:-:|---|---|
| **R1** | `Prefetch` captures the incarnation *after* the read | P0 | X | X | KNOWN symptom (C4) / **NEW root cause** | **fixed** `066d876` |
| **R2** | `IndexLog` resumes appending past the committed extent | P0 | X | | **NEW** | **fixed** `b688bac` |
| **R3** | A torn *inactive* slot header wipes the whole valid store | P0 | | X | **NEW** | **fixed** `d98e629` |
| **R4** | A failed slot-switch commit leaves the referenced slot recyclable | P0 | | X | **NEW** | **fixed** `d98e629` |
| **R5** | Adoption never authenticates the newest commit window | P0 | | X | KNOWN (I2 / T5) | **fixed** `db44e0a` ᵃ |
| **R6** | A cancelled `Set` returns while the store still reads caller buffers | P1 | X | X | **NEW** | **fixed** `f79ddd9` |
| **R7** | An older `.kdat` incarnation can be replayed intact | P1 | (x) | X | **NEW** — *disputed*, see note | **open** — deferred |
| **R8** | A corrupted-but-parseable `.kidx` is treated as authoritative | P1 | | X | KNOWN (G5 #3 / I12) | **fixed** `db44e0a` |
| **R9** | Reopen counts the drained slot as dead → spurious full compaction | P2 | X | | **NEW** | **fixed** `b688bac` ᵇ |
| **R10** | `_generation` advances even when the superblock write fails | P2 | X | | **NEW** | **fixed** `d98e629` |
| **R11** | Compaction drops index entries on *any* read exception | P2 | X | | **NEW** | **fixed** `f79ddd9` |
| **R12** | 64-bit hash collapse across seven index/write sites | P2 | X | X | KNOWN (C1 / I29) | **fixed** `f79ddd9` |
| **R13** | Wipe deletes unrelated numeric-suffix files | P2 | | X | KNOWN (G5 #4 / I31) | **fixed** `c3c2586` |
| **R14** | Total compaction holds the write lock for the whole copy | P2 | | X | KNOWN (P4) | **fixed** `73528ff` |
| **R15** | Public API that silently ignores what it is told | P2 | X | X | partly KNOWN (P5) | **fixed** `c3c2586` ᶜ |
| **R16** | Plaintext key/value returned to `ArrayPool` uncleared | P3 | X | | **NEW** | **fixed** `1280d20` |
| **R17** | Derived key material is never zeroized | P3 | X | X | **NEW** | **fixed** `1280d20` ᵈ |
| **R18** | `SuperblockState.LiveBytes`/`DeadBytes` written, never read | P3 | X | | **NEW** | **fixed** `b688bac` ᵇ |
| **R19** | Reads on a disposed store are inconsistent | P3 | X | | **NEW** | **fixed** `c3c2586` |
| **R20** | `GetMany` hashes and probes every key twice | P3 | X | | **NEW** | **fixed** `c3c2586` |
| **R21** | Unvalidated `KvasarOptions` numerics | P3 | X | | **NEW** | **fixed** `c3c2586` |
| **R22** | Crash-test worker processes can outlive a failed assertion | P3 | | X | KNOWN (T6) | **fixed** `1280d20` |
| **R23** | Style / hygiene deviations | P3 | X | | **NEW** | **fixed** `1280d20` |

**Both agents**: 5 (R1, R6, R12, R15, R17). **Claude only**: 10. **Codex only**: 8.

**Status as of 2026-07-27: 22 of 23 fixed**; R7 is open by decision, not by omission. Commit hashes above
are the branch commits; each was merged to `main`. Footnotes:

- ᵃ **R5** is fixed for the window a generation *adds*. The fallback path still authenticates the whole
  extent when two candidates name different data slots — recorded as **C3** in
  [`REVIEW-R3.md`](REVIEW-R3.md). Note also that R5's first implementation *caused* the round-3 P0.
- ᵇ **R9/R18** were fixed, but the implementation initially turned advisory accounting into an integrity
  gate in three places (`SeedAccounting` threw, `Superblock.Write` threw, `TryParseSlot` returned null).
  Any of those could reach `WipeFiles` through `TryAdopt`, and the `TryParseSlot` one would have wiped
  every already-on-disk store that had ever compacted, since `DataLog.DeadBytes` sums both slots. All
  three were reverted to degrade instead (`6c0a3a6`).
- ᶜ **R15** changed the benchmark to `KvasarDurability.Flushed`, which is why the numbers in
  [`BENCHMARKS.md`](BENCHMARKS.md) had to be re-measured rather than compared.
- ᵈ **R17** is fixed for store-lifetime keys. Two gaps were found afterwards: keys surviving a failed
  `Open` (**C5**, fixed) and the per-incarnation cipher stranded by `Recycle` (**X3**, still open —
  disposing it there races the R1 fix).

## Round 3

A second pass over the *fixed* code found 14 more findings, one of them a P0 caused by R5 interacting
with the never-rewind rule. See [`REVIEW-R3.md`](REVIEW-R3.md); 12 of those 14 are fixed, with C3 and X3
open.

---

# P0 — data loss, corruption, or serves-garbage

## R1. `PagedFile.Prefetch` captures the incarnation *after* the read

**C + X** · `src/ActualLab.Kvasar/Paging/PagedFile.cs:213-224` · KNOWN symptom, **NEW root cause**

The comment states the invariant and the code does the opposite:

```csharp
await _file.ReadExact(PagePosition(firstPageId), buffer.AsMemory(0, byteLength), ct);
// One incarnation for the whole run: a Recycle between the read and the decrypt would
// otherwise let these bytes be cached under the *new* id.
var inc = _incarnation;          // <-- read AFTER the await
```

If `Recycle` (`PagedFile.cs:303`) runs while the read is in flight, the **old** incarnation's bytes
are decrypted with the **new** cipher and inserted under the **new** `FileId`. Compare `ReadAndCache`
(`PagedFile.cs:331`), which captures `inc` *before* the read.

`PageCache.Add` keeps the first entry for a key (`PageCache.cs:61-66`), so the poisoned page is never
replaced: the compaction copier's genuine `AppendPage` for that `(fileId, pageId)` is silently
dropped, and every later reader of the recycled slot gets the previous incarnation's plaintext.
AES-GCM cannot catch it — decryption already happened.

**This is the operative cause of the open P0 recorded as `TODO.md` § C4** ("compaction can serve a
torn value assembled from two incarnations of a recycled slot", cause "still unidentified"). It
explains C4's sharpest clue — *the failures cluster on `encrypt: False`*: under `AesGcmPageCipher` the
cross-incarnation decrypt fails its tag and the outer `catch` swallows the run, so nothing is cached;
under `NoopPageCipher` the decrypt is a `CopyTo` and the stale page is cached permanently. It also
explains why C4 only surfaces through `GetMany` and `Scan` — the only *reader-side* callers of
`Prefetch` (`KvasarStore.cs:265`, `:302`).

**Fix**: move `var inc = _incarnation;` above the `ReadExact` and re-check `_incarnation == inc` after
the read, before the decrypt/`Add` loop, bailing out on mismatch. That closes both orderings.
Codex additionally recommends synchronizing `Recycle` against in-flight prefetches, which is the
stronger form `TODO.md` already proposes; worth doing, since `Prefetch`'s `onDisk` bound
(`PagedFile.cs:203`) is read at yet another point in time.

## R2. `IndexLog` resumes appending past the committed extent, so the next commit blesses rolled-back deltas

**C** · `src/ActualLab.Kvasar/Index/IndexLog.cs:64-74`, consumed at `KvasarStore.cs:553-557` · **NEW**

`IndexLog.Open` derives the next append offset purely from the **physical** file length,
whole-entry-aligned — it never consults `SuperblockState.IndexCommitLength`. `Recover` then tests:

```csharp
var isIndexComplete = snapshot is not null
    && _mustPersistIndex && indexLog.Length >= state.IndexCommitLength;
```

Deltas that were flushed to `.kidx` but never committed make the physical length **longer** than the
commit names, so `isIndexComplete` is `true`, no replay happens, **no rotation happens**
(`KvasarStore.cs:582`), and the store keeps appending immediately after that uncommitted region. The
next commit stamps `indexLog.Length`, which now covers it. The following open parses the whole thing
and last-writer-wins resolves the rolled-back deltas *over* the correct checkpoint entries.

`DESIGN-Durability.md` §3.3/§14.1 states the rule ("index files rotate rather than being appended
after a hole") but only guards the case where the prefix is *shorter* than the commit. The *longer*
case is unguarded. The data side has no equivalent gap — `PagedFile.Open` burns and accounts the
uncommitted physical tail, and `BurnedBytes > 0` forces the rotation.

Reproduced against the real `IndexLog`:

```
committed length after checkpoint       = 136
physical length after uncommitted deltas = 256
recovery parsed entries                  = 3    (correct — recovery ignored [136,256))
next append offset                       = 256  <-- should be 136
isIndexComplete would be                 = True
NEXT OPEN parsed entries                 = 5    (the 3 correct entries now carry bogus locators,
                                                 plus a phantom key)
```

**Two reachable triggers.**
1. *Write burst.* `IndexLog._pendingCapacity` is `65536/24 = 2730` entries; `PagedFile`'s data
   staging is ~1 MiB. Any commit window producing >2730 records averaging under ~380 B flushes the
   index deltas while the data pages stay in RAM. A kill there loses the data (correctly) but leaves
   the deltas on disk. Up to 2730 keys then read back as misses on the *second* open.
2. *Compaction* — the severe one. `CompactCore`'s repoint loop (`KvasarStore.cs:936`) appends one
   delta per relocated entry into the **old** index slot, past its committed length, before
   `Commit(true)` rotates. A kill between those flushes and the commit leaves the pre-compaction
   generation adopted with `isIndexComplete == true`, so no rotation. Every relocated key's entry
   then names the **abandoned compaction target slot**. Those records are still readable, so it looks
   healthy — until the next `BeginCompaction` recycles that slot (`DataLog.cs:355` →
   `PagedFile.Recycle` truncates it), at which point the copy loop finds every entry unreadable and
   calls `_index.Remove` on it. That is permanent loss of essentially the whole live set. (Chain 2 is
   reasoned from source, not reproduced end to end; chain 1's mechanism is reproduced.)

**Fix**: pass `state.IndexCommitLength` into `IndexLog.Open` and clamp
`_flushedLength = Math.Min(wholeEntryPhysicalLength, indexCommitLength)`. Additionally, change
`Recover`'s test to `indexLog.Length == state.IndexCommitLength` so divergence in *either* direction
forces the rotation §3.3 already specifies.

## R3. A torn *inactive*-slot header causes the entire valid store to be wiped

**X** · `Internal/Log/DataLog.cs:103-117`, `Paging/PagedFile.cs:358-364`, `KvasarStore.cs:498-522` · **NEW**

`WriteHeader` truncates the file to zero **before** writing the new header, and `Recycle` goes through
it. Meanwhile `DataLog.Open` opens and validates **both** data slots in one loop — including the
unreferenced inactive one — and any `KvasarCorruptException` propagates out of `OpenLogs`, so
`TryAdopt` returns `false` for that candidate. Since the inactive file is shared by both superblock
generations, *both* candidates are rejected, and `Initialize` takes the `!isAdopted` branch:
`WipeFiles()` + `CreateFresh()`.

**Failure scenario**: after the safety commit makes both superblock slots reference data slot A,
compaction begins recycling free slot B. A crash immediately after `Truncate(0)` leaves every
committed byte in A intact — and reopening throws the whole store away because B has no header.

**Fix**: open and validate the active slot first; treat an invalid inactive slot as disposable free
space and (re)create it lazily when compaction begins. Adoption of a valid superblock must never
depend on files no valid superblock references.

## R4. A failed slot-switch commit can make the still-referenced slot recyclable

**X** · `KvasarStore.cs:889`, `:948`, `:953`, `:767-779` · **NEW**

`CompactCore` sets `_isSlotSwitchPending = true` (`:948`) and switches the in-memory active slot
*before* committing the switch (`:953`). `Commit` advances `_generation` at `:767` **before** the
superblock write at `:768`, and only sets `_slotSwitchGeneration = _generation` at `:777` **after** it
succeeds. So if that write throws, `_generation` has advanced while `_slotSwitchGeneration` is stale
and `_isSlotSwitchPending` stays `true`.

The next compaction's safety guard tests only `if (_generation <= _slotSwitchGeneration)` (`:889`) —
now false — so it skips the extra commit and proceeds straight to `BeginCompaction()`, which recycles
what it believes is the free slot. But that slot is the pre-switch one, which every *valid on-disk*
superblock still names. A crash during that pass leaves no adoptable committed data, and initialization
wipes the store.

Note that `MustRotateIndex` (`:786`) *does* consult `_isSlotSwitchPending`; the compaction guard does
not.

**Fix**: do not recycle while `_isSlotSwitchPending`. The safety guard should commit in a loop until
both the switch commit and the further commit that displaces the older superblock reference have
succeeded. **Fixing R10 is a prerequisite** — R10 is what makes the generation counter lie here.

## R5. Adoption never authenticates the newest commit window

**X** · `KvasarStore.cs:512`, `:556`, `Internal/Log/DataLog.cs:248` · KNOWN (`REVIEW-Overlap.md` I2, `TODO.md` T5, `DESIGN-Durability.md` §14.4)

A complete index causes recovery to read **no data pages at all**; with an incomplete or absent index,
`ScanFrom` converts the first page authentication failure into a plain `yield break`. In both cases
`Recover` returns successfully instead of rejecting the generation and falling back to the older
superblock. A torn committed page is therefore accepted as part of the newest generation; on an
index-less rebuild every valid record after that page disappears, and recovery then checkpoints and
confirms the truncated view, making the loss persistent.

**Fix**: authenticate every page the candidate generation adds before adopting it; any auth failure
inside the committed extent rejects that superblock and triggers fallback. Rebuild scans should
separately skip damaged pages when reconstructing the best available cache.

---

# P1

## R6. A cancelled `Set`/`SetMany` returns while the store is still reading the caller's buffers

**C + X** · `KvasarStore.cs:334-339` (`Set`), `:341-361` (`SetMany`); contract at `docs/SPEC.md` §4.2 · **NEW**

The uninterruptible-write pattern is:

```csharp
await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
await SetLocked(key, value).WaitAsync(cancellationToken).ConfigureAwait(false);
```

`SetLocked` running to completion regardless of the token is deliberate and documented (SPEC §4.4).
But it can suspend **before** it copies the caller's bytes: `AppendOne` → `DataLog.Append` →
`AppendSinglePage` awaits `SealTail` (possibly a real `Flush`) *before* calling `RecordCodec.Encode`,
and `AppendMultiPage` awaits `SealTail` first too. If the token fires during that I/O,
`Task.WaitAsync` hands the caller an `OperationCanceledException` while `key.Memory` / `value.Memory`
— and, for `SetMany`, the updates collection itself — are still live to the store.

SPEC §4.2 promises the opposite: *"`Set` copies the key/value bytes it needs, so the caller may reuse
its buffer right after the call."* §4.4 documents that a cancelled write may still land, but never
that the caller must keep its buffers intact.

**Why it matters**: a caller that pools or reuses a `byte[]` — the natural reading of §4.2, and what a
batching layer does — and cancels a `SetMany` gets the mutated bytes encoded into the record: a
structurally valid, *correctly authenticated* record containing a mix of two values. That is the
serves-garbage class the priority model puts at the top. Codex adds a second shape: the key can be
appended under a hash computed from a different version of that key. `MemoryManager`-backed values are
worse still — the store dereferences memory the caller may already have disposed.

**Fix**: copy key/value (and the batch structure) into store-owned buffers synchronously *before* the
first await — cheap, since the record is bounded by `MaxValueBytes` and `AppendMultiPage` already
rents a buffer. Otherwise amend SPEC §4.2/§4.4 to say a cancelled write borrows the caller's buffers
until it actually completes, and give callers a way to observe that.

## R7. A previously captured data-file incarnation can be replayed intact — *disputed*

**X** (Claude reached the opposite conclusion) · `Paging/SegmentHeader.cs:55`, `Crypto/AesGcmPageCipher.cs:84`, `Internal/Superblock.cs:32` · **NEW**

**Codex's claim**: the file salt and segment id are unauthenticated header fields; the per-page AAD
covers only page id and format version; and the authenticated superblock stores the slot and extent
but not the active file's salt or incarnation identity. An attacker who captured an older `.kdat`
incarnation for the same key and format can therefore substitute it wholesale — if it is at least as
long as the current committed extent, every page tag validates under the replayed salt, and stale
records are served. Fix: bind an immutable file-incarnation identity and salt into the authenticated
superblock and the page crypto context.

**Claude's counter-position**: it explicitly examined this and classified it as already-documented —
`DESIGN.md` known-limitation 3 (file substitution / rollback), unchanged. Its reasoning: a tampered
salt on its own only causes auth failures, and the truncate-to-committed-length variant requires a
*persistent active* attacker, which is outside the stated at-rest threat model.

**Resolution needed.** The two agents agree on the mechanism and disagree on whether it is in scope.
The narrow question for the author: does §"known limitation 3" already cover *whole-file replay of a
prior incarnation of this same store*, or only substitution of a foreign file? If the former, R7 is
documentation-only; if the latter, it is a real gap. Both agents agree that preventing replay of the
*entire store* additionally requires an external monotonic trust anchor.

## R8. A structurally valid but corrupted plaintext index is treated as authoritative

**X** · `KvasarStore.cs:553`, `:556`, `:563` · KNOWN (`REVIEW-Gpt5Sol-High.md` #3, `REVIEW-Overlap.md` I12)

Index completeness is inferred solely from parse success and physical length. `.kidx` entries carry no
checksum or MAC, so bit flips in hashes or locators stay structurally valid; a "complete" index then
sets `replayFrom` to the data end and suppresses reconstruction from the authenticated data. A changed
hash makes a record unreachable; a locator redirected to an older record for the same key serves
stale data. Recovery then rotates or confirms the poisoned index rather than repairing it.

**Fix**: authenticate the committed index prefix, or validate each loaded entry against the
referenced authenticated record and replay the data extent on failure. Completeness must not rest on
byte count alone. (Note this compounds R2: R2 is how the byte count comes to lie in the first place.)

---

# P2

## R9. Reopening a compacted store counts the drained slot as dead, firing a full spurious compaction on the first write

**C** · `KvasarStore.cs:1064-1076` (`SeedAccounting`) → `Internal/Log/DataLog.cs:291-299` · **NEW**

`SeedAccounting` seeds **both** slots: `DataLog.SeedAccounting(slot, live)` sets
`DeadBytes = LogicalLength(st) - liveBytes`. The non-active slot still physically holds the previous
compaction's drained data (it is only truncated at the *next* `BeginCompaction`), so its entire
content is charged as dead. In-session this is handled — `CompactCore` calls
`_data.ResetAccounting(sourceSlot)` (`:947`) precisely because §14.5 says *"the drained slot must not
be counted… the store ping-pongs between slots forever"* — but the **open path has no equivalent**,
and the `SuperblockState.LiveBytes`/`DeadBytes` the commit protocol dutifully persists are never read
back (R18).

Reproduced end to end (200 keys × 512 B, 6 overwrite rounds, `FlushDelay = 0`):

```
before close: live=104090 dead=104090   ratio 0.50   (below the 0.667 trigger)
after reopen: live=104090 dead=2591078  ratio 0.961  (above the trigger)
after 1 write: live=104612 dead=0   file 2.7 MB -> 1.06 MB   <-- a full compaction ran
```

**Why it matters**: every launch of a store that has ever compacted pays a complete rewrite of the
live set on the *first* commit — synchronously under the write lock (R14), so the first write after
startup stalls for the whole pass. For the 25 MB `CCC.db3` target that is hundreds of ms of I/O and
25 MB of flash writes at every app start, on a platform whose whole point is that it kills and
restarts the app constantly. This is exactly the ping-pong `DESIGN-Durability.md` §14.5 forbids.

**Fix**: zero the inactive slot's `DeadBytes` at open (its bytes await recycling; they are not garbage
inside the active file), or seed accounting from the superblock's persisted counters — which is what
they were added for.

## R10. `_generation` advances even when the superblock write fails

**C** · `KvasarStore.cs:767-770`; same shape at `:578-579` in `Recover` · **NEW** · ⟵ *amplifies R4*

```csharp
_generation++;
await _superblock.Write(_superblockFile!, new SuperblockState(_generation, ...));
```

If `Write` throws (disk full, device removed), `_generation` stays incremented. Since the target slot
is `generation % 2`, the **retry now targets the other slot** — the one holding the last valid commit.
`DESIGN-Durability.md` §9 lists "a superblock is never written into the slot currently being relied
on" as one of three structural invariants; this path violates it.

**Failure scenario**: commit G fails mid-write, tearing slot `G%2` (which held G−2). Only G−1 is now
valid. The next commit writes G+1 into slot `(G+1)%2 == (G−1)%2` — the last-good slot. A crash during
*that* write leaves **neither** slot authenticating, and `Initialize` takes the `NoValidSlot` branch:
wipe and rebuild. Without the spurious increment, the retry would have rewritten the already-broken
slot and the fallback would have survived.

**Fix**: increment only after `Write` returns — use a local `var generation = _generation + 1;` for
the write and assign on success. This is also the prerequisite for R4.

## R11. Compaction deletes index entries for any record it cannot read, including on a transient I/O error

**C** · `KvasarStore.cs:905-922` · **NEW**

```csharp
catch (Exception ex) when (ex is not OperationCanceledException) {
    read = default; // an unauthenticatable page reads as a miss here too (§5.3)
}
...
if (!read.IsFound || view.IsTombstone || _hasher.Hash(view.Key.Span, _hashKey) != e.KeyHash) {
    _index.Remove(e.KeyHash, loc);
    continue;
}
```

The comment reasons about *unauthenticatable pages*, but the filter catches everything except
cancellation — `IOException`, `UnauthorizedAccessException`, a transient device read error. §5.3's
"a failing page surfaces as a miss" is a **read-path** rule; here the miss is escalated into a
**write**: the entry is deleted and the record is not copied forward, so the key is gone from the next
checkpoint and from the compacted file. On the read path the same failure would be retried on the next
`Get`; here it is made permanent. On mobile flash, transient read errors are precisely the failure
mode this store will see.

**Fix**: distinguish `KvasarCorruptException` (genuine — drop) from any other exception (abort the
pass, leave the index alone; compaction is opportunistic and will retry).

## R12. 64-bit hash collapse across seven index and write sites

**C + X** · `Index/HashIndex.cs:68`, `:170-191`, `KvasarStore.cs:352-357`, `:933`, `:982`, `:1057`, `Index/IndexLog.cs:170` · KNOWN (`TODO.md` C1, `REVIEW-Overlap.md` I29, `EdgeCaseTests.HashCollisionFanOut_KnownBug`)

`HashIndex.Set` treats the first matching hash as the existing key without comparing key bytes;
`RemoveCore` likewise stops at the first hash match. Two distinct keys sharing a 64-bit hash overwrite
or delete each other, and a later compaction can permanently discard the displaced record. Rare with
keyed SipHash, constructible with an unkeyed custom hasher.

**New detail from this round**: two collapse sites not in the existing write-up — `SetMany`'s
`Dictionary<ulong,int> lastByHash` dedup (`KvasarStore.cs:352`), which silently drops the earlier of
two colliding keys *within one batch*, and `IndexLog.Parse`'s `Dictionary<ulong, IndexEntry>`
(`IndexLog.cs:170`), which does the same across a reload. Seven sites total.

## R13. Wiping a store still deletes unrelated numeric-suffix files

**X** · `KvasarStore.cs:730`, `:737` · KNOWN (`REVIEW-Gpt5Sol-High.md` #4, `REVIEW-Overlap.md` I31)

The current layout owns only `.0.kdat`, `.1.kdat`, `.0.kidx`, `.1.kidx`, but `IsSlotSuffix` accepts
every numeric suffix, so a corruption reset, version change, or `Clear` also claims `cache.2.kdat`,
`cache.123.kidx`, and so on. **Fix**: match the four current slot filenames exactly; keep the broad
numeric match only for legacy file types that genuinely had unbounded ids.

## R14. Total compaction holds the write lock for the entire copy

**X** · `KvasarStore.cs:380`, `:899` · KNOWN (`TODO.md` P4)

`Compact` takes `_writeLock`, and `CompactCore` snapshots, reads, copies, seals, repoints and commits
before releasing it; auto-compaction uses the same path. Copying a large live cache stalls all writers
for tens to hundreds of ms, against the asynchronous-compaction design goal — and R9 makes this fire
at every launch. Removing the lock naively would lose writes, so it is currently correctness-critical.
**Fix**: route new writes to the compaction target and copy in bounded batches outside the global
lock, serializing only the final CAS and slot switch.

## R15. Public API that silently ignores what it is told

**C + X** · `KvasarStore.cs:369-376`, `:144-148`, `KvasarOptions.cs:35`, `:39`, `:43` · partly KNOWN (`TODO.md` P5)

Three cases:

- **`Flush(bool fsync, CancellationToken)` ignores `fsync` entirely** while durability defaults to
  `Buffered`. Documented in the comment, invisible at the call site — and existing callers, tests and
  the benchmark all still invoke `Flush(true)`, which the benchmark exposes as `FlushDurable`
  (`benchmarks/.../Engines.cs:70`). A caller expecting it to survive an OS crash can lose acknowledged
  writes, and the benchmark compares a non-fsynced Kvasar against an SQLite WAL checkpoint,
  overstating durability-matched throughput.
- **`KvasarOptions.SegmentBytes` is dead** but still `init`-able.
- **`IndexEncryption.On`** (and `Auto` with a non-keyed hasher) silently degrades to *"don't persist
  the index at all"* rather than encrypting it — the caller asked for a stronger property and got a
  slower store, with no signal.

**Fix**: `[Obsolete]` the `Flush(bool)` overload and `SegmentBytes`, expose the parameterless commit
API so callers choose store durability explicitly, and throw `NotSupportedException` from `Open` for
`IndexEncryption.On` (or at minimum surface it on `KvasarStats`). For durability-matched benchmarks,
configure `KvasarDurability.Flushed`.

---

# P3

## R16. Plaintext keys and values are returned to `ArrayPool` uncleared

**C** · `Internal/Log/DataLog.cs:430-449` · **NEW**

`AppendMultiPage` rents a buffer, encodes the full **plaintext** record (key + value) into it, and
returns it with `ArrayPool<byte>.Shared.Return(buf)` — no `clearArray: true`. The bytes stay in the
shared pool until a later renter overwrites them. For a library whose reason to exist is encryption at
rest, leaving decrypted user data in a process-wide pool weakens the in-memory posture. **Fix**:
`Return(buf, clearArray: true)` on the plaintext path; the ciphertext buffers in
`Prefetch`/`ReadAndDecrypt` don't need it.

## R17. Derived key material is never zeroized

**C + X** · `KvasarStore.cs:131-140`, `Crypto/AesGcmPageCipherFactory.cs:22`, `Crypto/AesGcmPageCipher.cs:19-21`, `Internal/Superblock.cs:117` · **NEW**

`pageKey`, `_hashKey`, `_nonceKey`, `AesGcmPageCipherFactory._pageKey` and `Superblock._key` are
`byte[]` that live for the store's lifetime and are never cleared on `DisposeAsync`; there is no
`CryptographicOperations.ZeroMemory` anywhere in the library. Managed arrays get copied by the GC and
land in process dumps and hibernation images. The master key is caller-owned so the store can't clear
it, but the *derived* subkeys are the store's own. **Fix**: `ZeroMemory` the derived keys in
`DisposeAsync` and give the cipher/factory a disposal path — without clearing the caller's original
array.

## R18. `SuperblockState.LiveBytes` / `DeadBytes` are written on every commit and never read

**C** · `Internal/Superblock.cs:309-310` (written), `:290-291` (parsed); no consumer · **NEW**

`DESIGN-Durability.md` §3.1 lists these as *"accounting, drives the compaction trigger"*. Nothing
reads them back — `Recover` reseeds from the index instead (`KvasarStore.cs:571`). They are the
natural fix for R9. **Fix**: consume them in `Recover`, or drop them from the slot format and the doc.

## R19. Reads on a disposed store are inconsistent

**C** · `KvasarStore.cs:203-221` (`Get`), `:276-286` (`Scan`), `:673-703` (`CloseFiles`) · **NEW**

`Get`/`GetMany` take no lock and check nothing, and `CloseLogs` deliberately leaves `_data` pointing at
the disposed log ("a disposed log answers a read with a miss instead of an NRE"). But the `PageCache`
is not dropped on dispose, so `TryReadRecordCached` can still serve a **fully valid value** from a
disposed store. `Scan` takes `_writeLock`, which is disposed by then, and throws
`ObjectDisposedException`. Three different answers to the same use-after-dispose. **Fix**: pick one —
all read paths throw, or all answer "miss" (which requires dropping the cache in `CloseLogs`).

## R20. `GetMany` hashes and probes every key twice

**C** · `KvasarStore.cs:238-251`, then `:271` · **NEW**

The ordering pass computes `_hasher.Hash(keys[i].Span, _hashKey)` and walks a `ProbeCursor` to resolve
each locator, discards the locator, then calls `Get(keys[index])`, which recomputes the hash and
re-walks the same probe run. `Array.Sort(order, static (a,b) => …)` also allocates a
`ComparisonComparer` per call. This is the `IBatchingKvasBackend` hot path (I27/§6.4) and the same
double-hash shape I32 removed from `SetMany` only. **Fix**: carry the resolved `(hash, locator)`
forward and inline the read + full-key verify (or add a private `Get(key, hash)` overload); sort with
a cached `IComparer<T>` or by packing `(packed, index)` into one `long`.

## R21. Unvalidated `KvasarOptions` numerics; a negative `MaxValueBytes` turns every `Set` into a delete

**C** · `KvasarStore.cs:69-76`, `:962-972` · **NEW**

Only `EncryptionKey` and `BasePath` are validated at `Open`. `MaxValueBytes`, `MaxInlineValueBytes`,
`CommitBytes`, `CompactionDeadRatio`, `CompactionMinBytes` and `PageCacheBytes` are not range-checked.
Sharpest consequence: a negative or zero `MaxValueBytes` makes `record.Length > _options.MaxValueBytes`
true for everything, so `AppendOne` converts **every** write into a tombstone — silently, since
`OversizedValueThrows` defaults to `false`. `PageCacheBytes = 0` gives shard 0 a negative budget.
There is also no key-size cap at the API boundary: a >64 KiB key surfaces as
`ArgumentOutOfRangeException(paramName: "keyLen")` from inside `RecordCodec`. **Fix**: validate these
alongside `EncryptionKey` in `Open`, and surface the cap as `KvasarConstants.MaxKeyBytes` with a named
argument check in `Set`/`SetMany`. (The codec-level cap itself is KNOWN — I23.)

## R22. Crash-test assertion failures can leave worker processes running

**X** · `tests/ActualLab.Kvasar.Tests/Store/ProcessCrashRecoveryTests.cs:116`, `:134` · KNOWN (`TODO.md` T6)

The worker is killed only after assertions and delays that can throw, and disposing `Process` does not
terminate the underlying process. A failed or cancelled test can leave a worker writing indefinitely
while holding the store lock and the output assemblies, contaminating later tests, builds and
benchmarks. **Fix**: kill + `WaitForExitAsync` in a `finally`, guarded by `HasExited`, with a bounded
cleanup timeout.

## R23. Style / hygiene deviations

**C** · **NEW** · all mechanical

- `Index/HashIndex.cs:1` — `using System.Threading;` is already a global implicit using.
- `Index/HashIndex.cs:283`, `Paging/PageCache.cs:125` — `private const int` declared between methods.
- `Internal/Log/RecordCodec.cs:1`, `Internal/Log/RecordView.cs:1` — `using ActualLab.Kvasar;` is
  redundant inside `namespace ActualLab.Kvasar.Internal`.
- `KvasarStore.cs:1111` — fully-qualified `System.Text.Encoding.UTF8`.
- `KvasarStore.cs:39` — `_cache` is `volatile` with a comment about lock-free readers, but it is
  write-only after assignment; nothing reads it.
- `Internal/Superblock.cs:182-195` — a private `ReadExact` near-duplicating `StorageFileExt.ReadExact`,
  differing only in throw-vs-bool. Worth a shared `TryReadExact`, per CLAUDE.md's "reuse before adding".

---

## Cleared by both agents

Both reviewers independently examined and found **no defect** in:

- **The dependency rule.** `src/ActualLab.Kvasar.csproj` references exactly `System.IO.Hashing`.
- **AES-GCM nonce reuse.** `nonce = HMAC-SHA256(HMAC-SHA256(pageKey, fileSalt), pageId)[..12]`,
  AAD = `pageId || formatVer`. Uniqueness within a file is probabilistic but far past any reachable
  page count; `Recycle` stamps a fresh 16-byte salt so incarnations have disjoint nonce spaces; and
  `PagedFile.Open` rounds `pageCount` **up** so a torn tail burns its id rather than re-issuing it.
  Neither agent could construct a reachable same-key/same-nonce rewrite path. (R7 is a *replay*
  concern, not a nonce concern.)

Claude additionally cleared, with no finding: the superblock slot-position/range/KCV checks,
torn-slot-as-`null` and `FixedTimeEquals`; `ProbeCursor`'s seqlock and `HashIndex` resize/tombstone
accounting (no unterminated probe run is possible while `_used <= 0.7·capacity`); `Locator`'s 16/48
packing and sentinel rejection; `Varint` overlong rejection; `RecordCodec`'s bound-before-narrow and
`Enum.IsDefined` kind check; the tail-buffer zero-copy lifetime (`SealTail` installs a fresh buffer and
only clears above `TailFill`); `ScanFrom` start alignment; the slot-recycling generation guard for both
rotations; and `ArrayPool`/`SemaphoreSlim`/file-handle lifetimes (no double-return, no leaked handle on
any `Open` failure path).

## Build

Claude ran `dotnet build ActualLab.Kvasar.slnx -c Release -p:UseMultitargeting=true` → net10.0 +
net9.0, **0 warnings, 0 errors**. All newer-BCL usage (`Enum.IsDefined<T>`,
`BitOperations.RoundUpToPowerOf2`, `ObjectDisposedException.ThrowIf`, `CancelAsync`, `Array.MaxLength`,
the C# 14 extension members in `KvasarKeyExt`/`KvasarValueExt`) is net9-clean.

Codex's build never started — MSBuild was denied access to its temp directory under the read-only
sandbox — so its findings are static-source only.

## Verification

R1, R2, R3, R4 and R10 were re-checked against source during consolidation and all hold:
`PagedFile.cs:213-217` does read `_incarnation` after the `await`; `IndexLog.Open` (`:64-72`) never
sees `IndexCommitLength` while `Recover` (`:556`) tests `>=`; `WriteHeader` (`:362-363`) truncates
before writing and `DataLog.Open` (`:103-117`) validates both slots into a failure that reaches
`TryAdopt`'s wipe path; and `Commit` increments `_generation` at `:767` before the write at `:768`,
setting `_slotSwitchGeneration` only at `:777`.

R2 and R9 additionally carry empirical repros from the reviewing agent. R7 is **disputed between the
two agents** and needs an author call on threat-model scope. Everything else is reported as stated.

## Suggested fix order — *executed*

All eight steps below were carried out in this order (R7 excluded by decision); the order held up, in
that R10 did have to precede R4 and R1 did close `TODO.md` C4. Kept as the record of how it was
sequenced.

1. **R10** (three-line change) — it is the prerequisite for R4 and closes a wipe path on its own.
2. **R1** (two-line change) — closes the open P0 `TODO.md` C4 has been chasing.
3. **R2** — clamp the index append point to the committed length; tighten `Recover`'s test to `==`.
4. **R3** — do not let an unreferenced slot veto adoption.
5. **R4** — gate recycling on `_isSlotSwitchPending`, once R10 lands.
6. **R9** / **R18** together — the persisted counters are the fix for the accounting bug.
7. **R6** — copy caller buffers before the first await (or amend the SPEC).
8. **R11**, then the P2/P3 tail.
