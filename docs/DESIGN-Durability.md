# Kvasar — durability principles, file set, and commit protocol

Status: **implemented**. It supersedes the durability parts of [`DESIGN.md`](DESIGN.md) and the storage
model in [`SPEC.md`](SPEC.md) §5 / §9, both of which still describe the segment model and are now stale.
§14 records the four places this document turned out to be wrong or under-specified once `KvasarStore`
was actually built against it.

This document states the durability goal, enumerates every file Kvasar maintains and the rule that
governs it, gives the commit protocol, and **proves** the protocol delivers the goal. It ends with
what the design does to [`REVIEW-Overlap.md`](REVIEW-Overlap.md): **23 of 38 issues cease to exist**
(including all four P0s), 5 more are resolved inside the work, 10 are untouched — and what it costs.

The design uses **no native APIs**. No P/Invoke, no libc, no `fcntl`. That is not a constraint we
worked around; it is a consequence of picking the right goal, as §2 explains.

---

## 1. The goal

> **After any crash — process kill, OS kill, torn write, power loss — the store reopens at the state
> of some commit that completed. Never at a partial commit, never at a mix of two, never at a state
> that never existed.**

And explicitly **not** promised:

- That a completed commit survives power loss.
- That the *most recent* completed commit is the one you get.

Kvasar is a regenerable cache. A lost write costs one upstream lookup, and the cache is designed to
absorb exactly that. A *torn* state costs correctness, and no amount of upstream lookup repairs it.

So the property we need is **atomicity**, not durability. That distinction is the entire design.

## 2. Why atomicity is cheap and durability is not

Durability means "these bytes reached the medium", and forcing that is a per-commit blocking cost
that on Apple hardware needs `fcntl(F_FULLFSYNC)` — which .NET does not expose on iOS (verified in
§7), hence P/Invoke.

Atomicity means "the recovered state is one that was actually committed". It needs only that
recovery can **tell the difference** between a complete commit and an incomplete one. Kvasar can
already do that for free: every page is AES-GCM encrypted, so **every page is individually
authenticated**. A page that is missing, torn, or partially written fails its tag. We do not need
the filesystem to order anything for us if we can cryptographically detect what actually landed.

That is why the design needs no native code. We are not trying to buy a cheaper barrier; we are
avoiding the need for one.

One managed `FlushToDisk` per commit remains, because it makes the proof in §6 unconditional rather
than probabilistic, and it costs ~one syscall per `FlushDelay` tick. Even that is optional (§5.3).

### The durability setting

`Flush(bool fsync)` goes away — durability is a property of a store, not of a call site. `Flush()`
means **commit**.

The commit protocol (§5.1) contains exactly one durability call, so there is exactly one knob and
therefore exactly two levels:

```csharp
public enum KvasarDurability {
    Flushed = 0,  // default: one FlushToDisk on the data file per commit
    Buffered,     // no FlushToDisk at all; recovery validates instead (§5.3)
}
```

The superblock and the index are never flushed under either level — their correctness comes from
validation rather than ordering (§3.1, §3.3), so there is no third state to offer.

There is no `PowerLoss` level. On every platform `RandomAccess.FlushToDisk` already does the
strongest thing .NET offers (`FlushFileBuffers` / `F_FULLFSYNC` on macOS / `fsync` elsewhere), and
per §1 we do not build on top of it.

There is likewise no `Ordered` level, and the reason is worth recording because it is the crux of
the design. An earlier draft had one, meaning `fcntl(F_BARRIERFSYNC)` — a cheap Apple barrier
guaranteeing that writes before it reach media before writes after it, reachable only via P/Invoke.
The property that barrier was buying is that a superblock can never be adopted when the data it
references is absent. **That property is now unconditional**, supplied by recovery-time
authentication (§6c) rather than by a barrier, at both levels. So ordering stopped being a setting
and became a guarantee. The two remaining levels choose *retention* — how much recent work
survives — not ordering.

**Atomicity is identical under both levels** — the §6 proof covers each, and neither can produce a
torn state. They differ only in how much recent work survives, and in one asymmetry:

| Crash kind | `Flushed` | `Buffered` |
|---|---|---|
| app / process kill (page cache intact) | last commit | **identical — last commit** |
| OS crash / kernel panic | last commit, or one back | usually last commit, possibly further back |
| power loss | last commit or a few back | same, wider window |

Under `Flushed` everything before the last flush is stable, so no hole can exist below the committed
extent. Under `Buffered` nothing was ever made stable, so a hole *can* exist below it, and recovery
validates only the newest commit's window (§5.2 step 3) — an older hole is not caught at open and
surfaces later as a page failing authentication on read, i.e. as a miss. Neither level can serve
garbage.

### 2.1 Relationship to `FlushDelay`

`FlushDelay` controls how *often* a commit happens; durability controls what a commit *does*. In
this design they are genuinely orthogonal — which they are **not** today, and that difference is
load-bearing.

Today `FlushDelay` is a mode switch with three effects:

- `SealOrDefer` (`KvasarStore.cs:539`): `FlushDelay <= 0` seals the tail and flushes on every write;
  `> 0` publishes into the *unsealed* tail. Different page-packing semantics, not just timing.
- `Initialize` (`:406-416`): the flush loop, the `.clean` marker check, and the unclean-open segment
  roll **all exist only when `FlushDelay > 0`**. That gate is the root of **I1** — the marker is
  written on every graceful close but consumed only in this branch, so it degrades from "the last
  run closed cleanly" to "some run did".
- `FlushDelay = 0` does not fsync today: it calls `_segments.Flush(false)`, i.e. per-write OS
  visibility, not per-write durability.

In the new design recovery is unconditional and superblock-driven, and nonce safety comes from the
never-rewind rule (§5.2 step 6) rather than from a marker plus a roll. The second effect therefore
disappears — which is *how* I1 dies — and the two knobs compose cleanly:

| | `Flushed` | `Buffered` |
|---|---|---|
| `FlushDelay = 0.5s` (default) | lose ≤ 0.5 s on OS crash | lose ≤ 0.5 s on app kill; more on OS crash |
| `FlushDelay = 0` | fsync per write — strongest, slowest | commit per write, zero fsyncs |

**A commit costs more than a deferred flush does today**, and this is a real new cost: one superblock
slot write, plus sealing the tail page. Sealing is mandatory because pages are GCM-authenticated
units — half a page cannot be committed, and a sealed page cannot be reopened to pack more records
without reusing its `pageId` nonce, so the remainder is padded and becomes dead space.

At the default `FlushDelay` both costs are noise (one small write and ≤ one page of padding per
tick). At `FlushDelay = 0` they are a superblock write plus a full page per record — comparable to
what `SealOrDefer` already costs today, so not a regression, but it means **`FlushDelay` matters
more here than it does now, not less.**

Accordingly the superblock slot should be sized for cost, not for atomicity: ~512 B is ample, and a
torn slot is caught by its MAC, so hardware sector atomicity is not something the design relies on.

### 2.2 What triggers a commit

`RunFlushLoop` (`KvasarStore.cs:552`) is currently a fixed-period timer: it wakes every `FlushDelay`
regardless of activity, checks a dirty flag, and usually goes back to sleep. On a backgrounded
mobile app that is two pointless wakeups a second for the life of the process.

It should instead arm the delay on the **clean → dirty transition** — the delay measured from the
first write since the last commit:

```
while (!disposed) {
    await WhenDirty();               // no wakeups at all while clean
    await Task.Delay(FlushDelay);    // coalescing window
    await Commit();
}
```

The staleness bound is unchanged: a batch opening at t=0 commits at t=`FlushDelay`, so the *first*
write in a batch waits the full delay and every later one waits less. Worst-case staleness is
`FlushDelay` either way — the periodic form simply pays idle wakeups for nothing.

(The signal wants a `TaskCompletionSource`. CODING_STYLE routes those through
`TaskCompletionSourceExt`, which lives in `ActualLab.Core` and is covered by the zero-dependency
carve-out, so construct one directly here, named `_whenDirty` per the `WhenXxx` convention.)

**A byte-based trigger is also required**, and not merely as a durability nicety. Recovery step 3
(§5.2) authenticates the newest commit's window, whose cost is bounded by how much a single commit
can accumulate. Under a sustained burst `FlushDelay` alone does not bound that — half a second can
be hundreds of megabytes. So:

> a commit fires on **first-dirty + `FlushDelay`**, or on **uncommitted bytes ≥ `CommitBytes`**,
> whichever comes first.

`CommitBytes` around 8–16 MiB keeps step 3 to a few tens of milliseconds.

The highest-value trigger on mobile costs nothing and belongs in the host: call `Flush()` from the
platform's background/suspend notification. That makes the dominant crash — the OS killing a
backgrounded app — lose no writes at all, at either durability level.

## 3. The file set

Five files. **All created once, at store creation. None is ever created, renamed, or deleted while
the store is open.**

| File | Contents | Durability rule |
|---|---|---|
| `<base>.kvs` | superblock: two slots, each a complete commit record | never flushed; each slot self-authenticating |
| `<base>.0.kdat` `<base>.1.kdat` | data; one active, the other free or a compaction target | flushed once per commit; every page self-authenticating |
| `<base>.0.kidx` `<base>.1.kidx` | index; one active, the other free or a checkpoint target | **never flushed** — derivable from data (§3.3) |

### 3.1 The superblock — `<base>.kvs`

Two fixed-size slots, written alternately. Each slot is a complete, self-contained commit record:

```
magic, formatVer
generation         u64   monotonic; the highest valid slot wins
dataSlot           u8    which .kdat is active
dataCommitLength   u64   committed logical end of the active .kdat
indexSlot          u8    which .kidx is active
indexCommitLength  u64   how far the index is known consistent
liveBytes, deadBytes     accounting, drives the compaction trigger
MAC                      AES-GCM tag over all of the above, under the derived key
```

Slot for generation *G* is `G mod 2`, and this is **checked on read**, not merely followed on write:
an authenticated blob is position-independent, so a slot byte-copied into the other position would
otherwise authenticate happily. Recovery must not have to assume its own writer was correct.

A slot is also **range-checked** after it authenticates. A MAC proves integrity, not plausibility —
a slot written by a buggy writer can authenticate and still name `dataSlot = 7` or a negative
`dataCommitLength`. Such a slot is treated as invalid, exactly like one that fails its tag.

Ahead of the two slots sits a 64-byte **file header**, written once at store creation:

```
magic "KSUP", formatVer
kcvNonce   12 B   random, written once, never rewritten
kcvTag     16 B   AES-GCM tag over a fixed constant, under the superblock subkey, AAD = formatVer
```

That **key check value** exists to separate three cases a MAC alone cannot distinguish, because
authentication fails identically for all of them:

| Condition | Meaning | Action |
|---|---|---|
| file absent or zero-length | new store | create |
| bad magic, or `formatVer` mismatch | deliberate format/`Version` bump | wipe & recreate |
| header intact, **KCV fails** | **wrong master key** | **throw — never wipe** |
| KCV passes, neither slot authenticates | genuine corruption | wipe & rebuild |

The ordering matters: `formatVer` is checked *before* the KCV, so a deliberate `KvasarOptions.Version`
bump still wipes rather than being reported as a wrong key.

Without the KCV, "no valid slot" would collapse wrong-key into corruption, and §5.2's "wipe &
rebuild" would mean **a wrong key silently destroys an intact store** — worse than the I9 bug this
replaces. (That is also what the current code does: `SegmentSet.Discover` decrypts page 0 and lets
the throw reach the wipe path. So this is a fix, not a regression, but the redesign is where it gets
fixed.)

Three further properties come out of the slot design for free:

- **A torn superblock write is self-detecting.** A partially written slot fails its MAC and is
  discarded. This is why the superblock needs no flush of its own: if the newest slot is lost or
  torn, recovery falls back to the previous one, which names an earlier *complete* commit — exactly
  what §1 permits.
- **The wrong master key is caught at the first read**, before anything is trusted — and now
  distinguishably, per the table above. Strictly better than the current "decrypt page 0 of the
  active segment" check, which passes vacuously when that segment is empty (issue **I9**).
- **Committed extents are explicit byte offsets.** Nothing is ever inferred from file length. There
  is no torn-tail heuristic, no "parse until it stops making sense". Bytes past `dataCommitLength`
  are not data: not read, not parsed, not scanned.

That last point deserves emphasis, because three separate P0/P2 issues (**I2**, **I3**, **I21**) are
all the same defect — inferring validity from file length. An explicit committed extent makes the
whole class unrepresentable.

### 3.2 The data files — `<base>.N.kdat`

Append-only, page-structured, AES-GCM per page with nonce = f(`fileSalt`, `pageId`), as today.

Two rules govern them:

- **Page ids never rewind.** On open, appending resumes at the *physical* end of the file rounded up
  to a page boundary — **not** at the committed extent. See §5.2 for why this is the load-bearing
  rule for nonce safety.
- **A data file may be recycled only when no valid superblock slot references it.** With two slots
  that means one extra commit must pass after a compaction switches away from a file.
- **`PageCache` ids are assigned by the store, never read from the file header.** `PageCache` keys
  *decrypted* pages by `(fileId, pageId)`, and the header carrying `fileId` is unauthenticated
  plaintext. If two files' headers ever reported the same id, their cache entries would collide and
  one file's read would be served the other's plaintext — after decryption, so AES-GCM cannot catch
  it. `PagedSegment` guarded this by validating header id against file name; with fixed slot names
  that check is gone, so the store must mint ids itself. The cache is in-memory only, so a monotonic
  per-process counter suffices.

### 3.3 The index files — `<base>.N.kidx`

The index is a **pure function of the data prefix**. It exists only so that `Open` is O(index)
instead of O(log). It is outside the durability story, but not outside the authentication boundary:

- **It is never flushed.** Not per commit, not ever.
- **Its committed prefix is authenticated.** The header carries two HMAC-SHA256/128 tags, selected by
  superblock-generation parity, over the stable header fields plus the checkpoint and committed delta
  range. A missing prefix, old layout, or MAC failure makes the index absent; recovery replays data.

So a lost, torn, stale, or tampered index costs *replay time at open* and nothing else. This keeps the
important simplification: no `.kidx` fsync (**I8**), no blocking `.kidx` flush (**I36**), and no
HWM-outruns-log correctness dependency (**I10**). Authentication prevents a structurally parseable
corruption from suppressing replay (**I12**); it does not make the index authoritative.

The delta tail is kept: 21 bytes per write is much cheaper at open than replaying full records
(values included) from the data file, and on mobile *unclean* open is the common case, not the rare
one. Index files rotate rather than being appended after a gap:

- **Whenever recovery replayed anything, the index rotates.** Read the valid prefix, rebuild in RAM,
  write a fresh full checkpoint into the *other* `.kidx` slot, commit, free the old one.

Rotation means the index is only ever appended to contiguously, never after a hole — which is what
makes **I3** impossible rather than merely fixed. The cost is one full index write, paid only on the
opens that needed a replay.

> **The trigger is "did recovery replay", not "was the open unclean".** An earlier draft said the
> latter, and it is a live data-loss bug — see §14.1. If the valid prefix recovery reads is *shorter*
> than the commit named, the store replays past it and then keeps appending deltas to that same file.
> Its length grows back past `indexCommitLength` while its contents still have a hole, so the next
> open sees a long-enough prefix, skips the replay, and adopts an index missing every truncated
> entry. That is **I3 by another route**, reached without a single torn delta write.

### 3.4 The directory itself

There is no managed way to fsync a directory, and we do not need one, because **the only dirent
mutations happen at store creation, before anything has been committed.**

If those dirents are lost to a crash, the store looks uninitialized at the next open, and Kvasar
creates it again. Nothing is lost, because nothing was committed. **The worst case of the directory
gap is precisely the wipe-and-rebuild path we already accept as the recovery path for a cache — not
a torn state.** That is the whole argument, and it is why D3/**I14** needs no `open(dir)` P/Invoke.

Renames are avoided for the same reason you flagged: a rename is a directory mutation, and directory
mutations are the one thing we cannot force. So the design has none.

## 4. What replaced the segments

Bitcask uses many segments so compaction can rewrite one at a time, bounding the write amplification
of a pass. That matters for a multi-terabyte server store. It does not matter here: a 100 MB cache
at 75% dead has a 25 MB live set, so rewriting *all* of it is a few hundred milliseconds of
sequential I/O, off the write path, once per threshold crossing.

The segment machinery that exists to avoid that cost is the direct source of **I2, I4, I5, I6, I7,
I13, I17, I18, I20, I22**. So: **one active data file, and compaction is total.**

Compaction being total is what kills tombstone resurrection (**I4**) outright — there is no "earlier
segment still holding the old record", so a tombstone can simply be dropped.

### Compaction, with two files

Trigger: `deadBytes / (liveBytes + deadBytes) >= CompactionDeadRatio` (default 0.75), checked at
commit. Runs asynchronously; the switch is one superblock write, so it is atomic and instantaneous
from a reader's perspective.

1. Active is A, threshold crossed. Under the write lock, truncate B to its header, stamp a fresh
   random `fileSalt`, and publish the in-progress pass so subsequent writes also append to B.
2. Read A without the write lock. Apply batches capped at 64 records or roughly 64 KiB under the
   lock (one record minimum): append them to B, seal the target tail, and compare-and-set their
   locators. Release and yield between batches so queued writers run.
3. An interleaved write appends its future canonical copy to B and its crash-safe copy to A, then
   publishes the A locator until the final switch. Commits during the pass therefore remain
   self-contained A commits; a crash or abort between batches reopens exactly as if the partial B
   copy never existed.
4. When the copier drains A, seal B, compare-and-set every interleaved write from its A locator to
   its B locator, rotate the index checkpoint, and commit `dataSlot = B` in the same serialized
   switch. A writer that superseded a copied record wins because either compare-and-set sees that
   the old locator changed.
5. A becomes recyclable one commit later, per §3.2.

If a pass aborts before the switch, successful batch relocations are compare-and-set back to their A
locators; any concurrent write or delete that already displaced one is left alone. Target-only
copies and their accounting are discarded. Only a `KvasarCorruptException` may remove an entry
during copying; every other failure rolls the pass back without changing the logical index.

The fresh `fileSalt` per recycle keeps nonce spaces disjoint. This is the same argument
`StartNewSegment` already relies on today.

Peak disk is **store size + live size** — 1.25× at the default threshold. That is the price of the
rotation model, and it is a knob.

### On the "mini-filesystem" alternative

You raised virtualizing into a page map plus a growing store with free-page reuse. Its real
advantage is incremental reclamation instead of all-or-nothing rewrites. I'd still argue against it
for v1:

- It requires a block allocator and free list, whose metadata then needs *its own* crash-consistency
  protocol. That does not remove a problem; it creates a second instance of the one we just solved.
- It trades sequential append for scattered page reuse — the wrong direction on mobile flash.
- The property that motivated it (no directory mutation while hot) is already delivered by the fixed
  five-file set, without an allocator.

## 5. The commit protocol

### 5.1 Commit

```
1. append this commit's data pages to the active .kdat
2. FlushToDisk(.kdat)                            ← the only durability call
3. write index deltas and the prefix MAC for generation G   (no flush)
4. write superblock slot (G mod 2) for generation G         (no flush)
```

One `FlushToDisk` per commit, on one file. Step 3 completes before step 4 is issued so the tag describes
the exact prefix named by the superblock. Neither index write is flushed: if the prefix or its tag does
not survive, recovery rejects the index and derives it again from data.

### 5.2 Recovery

```
1. read both superblock slots; discard any failing its MAC
   none valid  ⇒  store is absent or unopenable  ⇒  wipe & rebuild
2. G* := valid slot with the highest generation
3. verify the data pages in (L_{G*-1}, L_{G*}] authenticate
   any failure  ⇒  discard G*, retry with the other slot
   NB: a slot naming more data than the file holds makes PagedFile.Open throw
       KvasarCorruptException. Under Buffered that is the *expected* path, not corruption —
       recovery must catch it and fall back, never propagate it to the wipe path.
4. adopt G*: data is authoritative through L_{G*}
5. authenticate the index prefix named by G*; on absence, old layout, or MAC failure treat it as empty;
   otherwise load it and replay data from its stamp to L_{G*}; if anything was replayed, rotate the
   index (§3.3) — NOT "if the open was unclean" (§14.1)
6. resume appending at ceil(physicalLength / pageSize) — never at L_{G*}
```

Step 3 costs one commit window of authentication — bounded by `FlushDelay`, so a few MB at most.

Step 6 is the nonce-safety rule and deserves its own statement. A crash mid-append leaves pages
`P..P+k` physically present but uncommitted. Recovery rebuilds from the committed extent (which ends
before `P`) but resumes *writing* at `P+k+1`, whose nonce has never been used. Pages `P..P+k` are
unreferenced garbage, accounted dead, reclaimed by the next compaction.

### 5.2.1 The burned range must not end up inside a replay

A subtlety the crash harness caught, and the reason step 5 is ordered where it is. The burned pages
`P..P+k` sit *above* the old committed extent, but nothing stops a **later** commit extent from
spanning them: after recovery appends and commits, `dataCommitLength` covers `P..P+k` too, because a
commit extent is simply the file's page-aligned end. Replaying `[index.DataCommitLength,
dataCommitLength)` would then walk straight into a page that can never authenticate — a permanent,
unrecoverable hole below the commit extent, exactly what §3.1 promises cannot exist.

The rule that closes it:

> **The rotated index checkpoint is stamped at the resume offset**, not at the
> old committed length. So the replay range always starts *above* the burned pages.

This costs nothing — §3.3 already rotates the index whenever recovery replayed, so the only change is
which extent that checkpoint records. It is sound because a commit extent always ends on a page boundary
and a committed page was fully written before the superblock named it, so **a torn page is always
strictly above the last committed extent** — nothing committed is ever inside the burned range.

Two consequences worth stating:

- The store accounts `[committedEnd, resumeOffset)` as dead bytes, computable at open from
  `PagedFile`'s `CommittedPageCount` and `ResumePageId`.
- Nothing else ever reads the file sequentially: `Scan` and compaction reach records through index
  locators, so neither can walk into the gap.

This replaces both the `.clean` marker and the roll-to-a-fresh-segment-on-unclean-open. Both existed
to prevent nonce reuse; both are subsumed by an always-true rule instead of a marker file whose
write and consume conditions could drift apart. **I1** (the P0 crypto break) and **I7** (a new
segment per killed session) both die here, for the same reason.

### 5.3 The `Buffered` level — zero flushes

Step 2 can be dropped entirely. Recovery step 3 already *verifies* cryptographically what step 2
merely *arranges*, so the protocol remains sound: a commit whose data did not land fails validation
and recovery falls back.

What you lose is the unconditional guarantee in §6(b) — with no flush, a power loss could in
principle drop an old page while retaining a newer one, and step 3 only validates the newest
commit's window. The failure mode is a page that fails authentication on read, which surfaces as a
miss, not as garbage.

`Buffered` is genuinely attractive on mobile, where the dominant crash is the OS killing a
backgrounded app — a case in which the page cache is fully intact and *no* flush was ever needed. It
is offered, documented, and not the default.

Note what `Buffered` does **not** give up: ordering. Nothing in this protocol relies on the
filesystem ordering anything, at either level — a barrier merely *requests* a behaviour from the
storage stack, whereas recovery-time authentication *checks the outcome*. Consumer flash is known to
acknowledge cache flushes it did not perform; validation cannot be lied to in that way.

## 6. Proof

**Model.** Each file is a byte array. A `write` issued to the OS enters volatile state.
`FlushToDisk(f)` returns only after every write previously issued to `f` is stable. A crash
preserves all stable state and loses volatile state *arbitrarily*: any subset may survive, in any
order, and any individual write may be torn at any granularity. (A process kill without an OS crash
is the special case where nothing volatile is lost.)

**Given.** Superblock slots and data pages are each individually authenticated under a key the
attacker/corruption does not control, so a slot or page that is absent, torn, or partially written
fails its tag with overwhelming probability.

**Claim.** After recovery R (§5.2), the store's state equals its state at the completion of some
commit G\*, and every commit ≤ G\* is fully reflected.

**Proof.**

**(a) G\* names a real commit.** A slot passes its MAC only if written completely and unmodified.
So step 2 selects a generation that was actually issued, with a well-defined `dataCommitLength`
L_{G\*}. If no slot passes, step 1 wipes — which is a defined outcome, not a torn state.

**(b) The data G\* names is present.** `FlushToDisk(.kdat)` (step 2 of §5.1) *returned* before the
superblock write for G was *issued*. Everything through L_G was therefore stable at the moment the
superblock write began, and stable state is never lost. Hence: **if slot G survives, all data
through L_G survives.** No ordering guarantee from the filesystem is required — the flush's return
established the ordering by happening first in program order.

**(c) (b) is verified, not assumed.** Recovery step 3 independently authenticates the pages G\*
added. So the claim holds even where the flush assumption fails — notably on iOS under true power
loss, where `fsync` may not reach media, and generally on any device that acknowledges a flush it
did not perform. In that case step 3 rejects G\* and step 2 falls back to G\*−1, which is an
*earlier complete commit* — permitted by §1. This is also what makes `Buffered` (§5.3) sound, and it
is why the design needs no write barrier at either level.

**(d) Adopting L_{G\*} reflects exactly commits 1..G\*.** The data file is append-only and L is
monotonic in G, so the byte range [0, L_{G\*}) is precisely the union of all commits up to G\*, and
contains no bytes from any later commit.

**(e) The index cannot corrupt the result.** By §3.3 the index is a pure function of the data prefix
[0, L_{G\*}), and step 5 recomputes any part of it that is missing or inconsistent. No index state
can therefore produce a result that differs from the one implied by the data.

**(f) Nonce safety is preserved.** Step 6 resumes at or beyond the physical end of the file, so **no
`pageId` whose ciphertext survived the crash is ever issued again** under one `fileSalt`. Pages
between L_{G\*} and the physical end are unreferenced, because the index derives only from
[0, L_{G\*}) and §5.2.1 keeps the replay range above them.

The qualifier is load-bearing and the literal form would be false. When a crash drops a whole
trailing write, that ciphertext is *gone* — the file shrinks and those ids are re-issued. That is
safe precisely because nothing sealed under them survives to be XORed against, and it is what makes
the invariant testable: the harness computes the surviving set as
`ceil((physicalLength − headerSize) / onDiskPageSize)` and asserts against that, not against every id
ever handed out.

**(g) Compaction preserves the claim.** A compaction commit is an ordinary commit under §5.1, so
(a)–(f) apply unchanged. The recycling rule in §3.2 guarantees no file referenced by a surviving
slot is ever truncated, so the fallback in (c) always finds its data intact. ∎

**What the proof does not cover.** That the most recent commit survives — §1 declines it. And a
power loss under `Buffered` that drops an *old* page while keeping newer ones: authentication turns
that into a read failure (a miss), not into garbage, but the store may then reflect a state slightly
older than G\* for those keys. This is the one gap, it exists only under `Buffered`, and it is the
reason `Buffered` is not the default.

## 7. Platform notes

Verified against `dotnet/runtime`, `src/native/libs/System.Native/pal_io.c`:

```c
int32_t SystemNative_FSync(intptr_t fd) {
    int fileDescriptor = ToFileDescriptor(fd);
    int32_t result;
#ifdef TARGET_OSX
    while ((result = fcntl(fileDescriptor, F_FULLFSYNC)) < 0 && errno == EINTR);
    if (result >= 0) return result;
    // F_FULLFSYNC is not supported on all file systems and handle types ... Fall back to fsync.
#endif
    while ((result = fsync(fileDescriptor)) < 0 && errno == EINTR);
    return result;
}
```

`TARGET_OSX` is set only for desktop darwin — `configureplatform.cmake` gives iOS, tvOS and Mac
Catalyst separate branches. So:

| Platform | `RandomAccess.FlushToDisk` maps to | Covers |
|---|---|---|
| Windows | `FlushFileBuffers` | process kill, OS crash, power loss |
| macOS | `fcntl(F_FULLFSYNC)` | process kill, OS crash, power loss |
| Linux / Android | `fsync` | process kill, OS crash |
| iOS / Catalyst / tvOS | `fsync` | process kill, OS crash |

The gap — iOS under power loss — is closed by proof step (c), not by native code. **This is the
concrete payoff of choosing atomicity over durability: the one case .NET cannot express in managed
code is the one case the design does not depend on.**

## 8. The storage seam

All filesystem access goes through one interface — the only place the library touches files:

```csharp
public interface IStorageFile : IAsyncDisposable
{
    long Length { get; }
    ValueTask<int> Read(long offset, Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask Write(long offset, ReadOnlyMemory<byte> buffer);
    ValueTask FlushToDisk();
    ValueTask Truncate(long length);
}
```

Two implementations: the real one, and a fake that models a device — writes land in *volatile*
state, `FlushToDisk` promotes volatile→stable, and `Crash()` discards volatile state, optionally
reordering and tearing within it. The fake is exactly the §6 model, which is what lets the proof be
tested rather than merely asserted.

`Length` is deliberately **the store's own view, not the kernel's**: it counts writes that have been
*issued*, not only those that have completed, because the commit protocol needs to name an extent
before the write to it is awaited. A consequence worth stating: an external writer to the same file
would be invisible. `StoreLock` is what makes that acceptable.

## 9. Testing the invariants

**Level A — in-process fake device.** Where the coverage comes from. The §1 contract is a single
property and it fuzzes well:

> for every crash point in a workload, the recovered state equals the state at some commit N, and
> all commits ≤ N are fully reflected

Crash at every write boundary; assert. Milliseconds per case, thousands of cases, every CI run, no
privileges. The three structural invariants from §5.1 are asserted alongside it:

1. every byte a superblock generation references is written before that superblock is written;
2. a superblock is never written into the slot currently being relied on;
3. no file is truncated or reused while a valid slot references it.

**Level B — Docker, syscall-order assertions.** Verified working here with `--cap-add SYS_PTRACE`:

```
strace -f -e trace=write,pwrite64,fsync,fdatasync,openat,rename,unlink,ftruncate
```

Assert the same three invariants against a real trace. This closes the gap Level A cannot: that the
real backend emits what the fake models. It also mechanically verifies the claims of §3.4 — that no
`rename` or `unlink` occurs after store creation.

**Level C — Docker + FUSE.** Verified available here (`/dev/fuse` present, `fusermount3 3.18.2`;
needs `--device /dev/fuse --cap-add SYS_ADMIN --security-opt apparmor=unconfined`, plus
`MSYS_NO_PATHCONV=1` from Git Bash). A crash-simulating passthrough filesystem that honours `fsync`
and drops un-flushed data on command — real syscalls, real power-cut semantics, no kernel modules.

**Not available: `dm-flakey`.** Probed here — the WSL2 kernel (6.18.33.2) exposes only
`verity/striped/linear/error` and has no `/lib/modules`, so `modprobe dm_flakey` cannot work. It
would need a real Linux VM, and Level C is a better fit regardless: it models the device write cache,
where dm-flakey only injects I/O errors.

## 10. What this eliminates

**Structurally gone** — the code that produced them no longer exists:

| | | |
|---|---|---|
| I1 StaleCleanMarkerNonceReuse **P0** | I2 ScanAbortsLaterSegments **P0** | I3 KidxDeltaTailMisalignment **P0** |
| I4 TombstoneResurrection **P0** | I6 SmallSegmentsNeverCompacted | I7 SegmentPerKilledSession |
| I8 KidxRenameWithoutFsync | I9 WrongKeyAcceptedWhenActiveEmpty | I10 CheckpointHwmOutrunsLog |
| I11 CompactionUnfsyncedBeforeDelete | I12 KidxCheckpointUnauthenticated | I13 NoSmallSegmentMerge |
| I14 NoParentDirFsync | I15 AppleFullFsyncUnverified | I17 CompactionDeletesUnscannedSegment |
| I18 SegmentCountEqualsFdCount | I20 RemoveSegmentThrowsOrphans | I21 FullSizeTornPageWipesStore |
| I22 ArbitraryCompactionVictim | I25 FlushFsyncsEverySegment | I30 DurabilityUntestable |
| I36 BlockingKidxFsync | I38 SegmentRecycling | |

**I15** is in that list rather than in the one below because the design no longer depends on
`F_FULLFSYNC` on any platform — see §7.

**Resolved inside the redesign**, needing a small explicit fix: I5 (compaction trigger — now a
one-line policy at commit), I24 (widen `Locator` to `(slot:8, offset:56)`), I26 (compaction is
whole-store by construction, but runs once per threshold rather than once per pass per segment),
I31 (fixed filenames make the wipe glob exact), I34 (pad `IndexEntry` to 24 bytes while the format
is changing anyway).

**Untouched, still need their own fixes:** I16, I19, I23, I27, I28, I29, I32, I33, I35, I37.

## 11. Costs

- **A storage-layer rewrite.** `SegmentSet`, `PagedSegment`, `IndexFile` and `KvasarStore`'s
  open/flush/compact paths all change. Crypto (M1) and the record codec are untouched.
- **A format change.** `formatVer` bumps; existing stores are wiped. Cheap for a cache, but a
  one-way door for anyone already running v1.
- **Authentication work at open.** The normal path decrypts only the pages between the older and newer
  committed extents; a data-slot switch or missing older candidate requires the whole candidate extent.
  Index MAC verification is folded into the index read already required at open.
- **One small index write per commit.** The rolling HMAC avoids rescanning the index; committing writes
  one 16-byte generation-parity tag in its header.
- **Peak disk 1.25× store size** during compaction at the default threshold.
- **Compaction writes more per pass**, in exchange for running far less often and for not producing
  the segment-lifecycle bug family.

## 12. Open questions

1. **Default `CompactionDeadRatio`** — 0.75 as you suggested, or lower to reduce peak disk?
2. **Default durability** — `Flushed` (one flush) or `Buffered` (zero) on mobile? §5.3 argues
   `Buffered` is defensible there; I'd still ship `Flushed` as the default.
3. **File naming** — `<base>.kvs` / `<base>.N.kdat` / `<base>.N.kidx`, or keep `.klog`?
4. **Is `IStorageFile` public?** Public lets hosts supply a backend; `internal` keeps it unfrozen.
   `internal` for v1 unless you want the extension point.

## 13. Sequencing

1. `IStorageFile` seam + fake device + Level A harness against *current* behaviour — establishes the
   rig and a baseline without changing semantics.
2. `KvasarDurability`; `Flush(bool)` → `Flush()`.
3. Superblock + commit protocol + recovery; five-file model replaces segments.
4. Total compaction with the rotation model.
5. Level B/C Docker harnesses as the cross-check on 2–4.

Steps 1 and 2 are independent of everything still open in §12 and are not wasted under any outcome.

## 14. Corrections from the implementation

Four things this document got wrong or left under-specified. Each was found by a test, not by reading.

### 14.1 "Rotate the index on unclean recovery" is too narrow — rotate whenever recovery replayed

§3.3 ties index rotation to an *unclean* open, and §5.2.1 ties it to a burned range. Both are too narrow,
and the gap is a live data-loss bug: if the index prefix recovery read was **shorter than the commit
named** — the tail was lost, or a fault truncated the file — then recovery replays past it and the store
carries on appending deltas to that same file. Those new deltas land immediately after the surviving
ones, so the file's *length* grows back past the commit's `indexCommitLength` while its *contents* have a
hole. The next open sees a prefix as long as the commit named, concludes there is nothing to replay, and
adopts an index that is missing every entry the truncation removed.

This is **I3 by another route**: an index appended to after a gap. §3.3 already names the rule —
"index files rotate rather than being appended after a hole" — it just attaches it to the wrong trigger.
The condition is not "was the shutdown unclean" but **"did this recovery have to replay anything"**, which
is the same question as "is the prefix we read as long as the commit named". `CrashFuzzTests`' `TruncateIndex`
fault fails on every seed without it.

### 14.2 The switch is atomic only for readers that re-resolve through the index

§4: "the switch is one superblock write, so it is atomic and instantaneous from a reader's perspective."
That holds for a reader that resolves a key through the index on each read. It does **not** hold for one
holding a locator taken *before* the switch — `Scan`'s snapshot is exactly that.

In the segment model a stale locator was harmless: compaction unlinked the drained segment, so it
resolved to nothing. Two fixed slots recycle the drained file **in place**, so a stale locator can land
inside the slot's new contents and decode as a different, entirely valid record. AES-GCM cannot catch it
— the page is genuine, it just isn't the one the locator meant.

`Get`/`GetMany` were never exposed, because the full-key compare against the caller's bytes *is* this
check. `Scan` has no key to compare, so it must re-hash the decoded key against the index entry's
`KeyHash`; the compaction copy loop needs the same guard. `ConcurrencyTests.ScanDuringWrites` catches it
once compaction fires on its own trigger.

### 14.3 The never-rewind rule makes a failing page reachable from an ordinary read

§5.3 says a page that fails authentication "surfaces as a miss, not as garbage", and that is now
load-bearing rather than a remark. Rounding `pageCount` **up** past a torn tail puts burned page ids
*inside* the file's logical length, so an index entry that predates a truncation resolves to a page that
can never authenticate. Every read path — `Get`, `Scan` and compaction's copy loop — has to treat
`KvasarCorruptException` from a page decrypt as a miss. Only `Open` may still read it as "wipe".

### 14.4 Adoption authentication is separate from best-effort replay

Resolved. Before `Recover` reads the index or replays records, `TryAdopt` authenticates every page in
the candidate's added window `(L_previous, L_candidate]`. A page failure rejects the candidate and
tries the older superblock. If the candidates name **different data slots** — the first open after a
compaction switch — the pass authenticates the candidate's whole committed extent instead: that slot was
truncated to its header by `BeginCompaction` and restarted under a fresh salt, so no burned page can lie
below its extent and all of it is checkable. If there is **no older candidate at all** there is no floor
to bound a window against and the extent may legitimately contain a burned page, so nothing is
authenticated; that gap is recorded in `REVIEW-R4.md` rather than implied away.

`DataLog.ScanFrom` has a different contract: an index rebuild skips an unreadable record and
reconstructs the best available cache beyond it. A readable record header fixes the damaged record's
full span before its pages are decoded, so an interior page failure resumes at the record's end rather
than treating continuation bytes as a new record. If the header page itself is unreadable, replay probes
later page boundaries, but a decoded record alone is not enough to resume. Starting at that boundary,
records must decode back-to-back until the chain ends exactly on a page boundary or the remainder of its
final page is all-zero padding. Nothing from the candidate is yielded until that whole chain tiles; a
failure rejects the boundary and probing continues at the next page. Before allocating a spanning record
or walking its pages, replay also requires its declared length to fit the remaining replay extent and its
value-kind byte to be defined. This preserves §5.3's read-miss semantics for damage outside the
candidate's newly authenticated window without letting replay decide whether a superblock is adoptable,
without promoting an isolated record-shaped run in continuation bytes, and without quadratic copying
from candidates whose cheap header fields are already invalid.

### 14.5 Smaller notes

- **The drained slot must not be counted.** Its bytes sit on disk until the next `BeginCompaction`
  truncates them, but counting them as dead re-arms the trigger the compaction just satisfied and the
  store ping-pongs between slots forever. They are zeroed at the switch; the disk is reclaimed one
  compaction later. §4's "peak disk = store size + live size" already budgets for this.
- **Recovery confirms its adoption.** With two slots, rotating an index or recycling a data file right
  after recovery would clobber the generation below the one just adopted. Re-writing the adopted state
  as generation *G+1* (same extents, one 512-byte write) invalidates it first and makes both free slots
  immediately recyclable — cheaper than carrying a "which slot is still referenced" rule through open.
- **`Clear` is the one place that unlinks.** Recycling a slot would leave the cleared data on disk under
  a store whose caller just asked for it to be gone. A crash mid-`Clear` reads back as an uninitialized
  store, which §3.4 already accepts as a defined outcome.
