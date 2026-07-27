# Kvasar — review round 3

A second cold-read pass by two independent agents, run **after** the 22 round-2 fixes landed and
deliberately aimed at *what those fixes introduced or left behind* rather than at the codebase at large.
**14 distinct findings**; both agents independently hit the same P0 from opposite directions, which is
what made it worth chasing.

> **Scope: the code as of `707b88c`** (all of [`REVIEW-R2.md`](REVIEW-R2.md) fixed, suite green at 440).
> Both agents read the round-2 findings, the full `git diff 72787a9..HEAD -- src/`, and the current
> source. `72787a9` is the pre-fix baseline throughout.

## Agents

| Col | Agent | Notes |
|---|---|---|
| **C** | Claude Opus 5, high effort | Built and ran the suite 3×; wrote a standalone reproducer under `tmp/` that runs against both revisions |
| **X** | Codex (GPT-5.x), high effort | Static review only — its build was again denied a temp directory by the sandbox |

## Summary

| # | Short title | Sev | C | X | Status |
|---|---|---|:-:|:-:|---|
| **C1 / X1** | A burned page makes the store unadoptable → full wipe | **P0** | X | X | **fixed** `099ed50` |
| **C2** | `ScanFrom` resyncs mid-record, then abandons the replay | P2 | X | | **fixed** `5c33b02` |
| **C4** | An unreadable record reads as "key absent" | P2 | X | | **fixed** `5c33b02` |
| **X2** | `Scan` uses the 64-bit hash as an identity check | **P3** | | X | **fixed** + regression test |
| **C3** | Fallback adoption decrypts the whole store at open | P2 | X | | open |
| **C5 / X4** | Derived keys survive a failed `Open` and the wipe-retry | P3 | X | X | **fixed** |
| **C6** | A write racing `DisposeAsync` can start a compaction | P3 | X | | **fixed** |
| **C7** | `IndexLog`'s internal `Read` overload writes, and shadows | P3 | X | | **fixed** |
| **C8** | `matchKeyId` violates the boolean naming rule | P3 | X | | **fixed** |
| **C9** | Three `HashIndex` members still implement the pre-R12 collapse | P3 | X | | **fixed** (deleted) |
| **C10 / X5** | Docs still describe the pre-R12 collision contract | P3 | X | X | **fixed** |
| **X3** | `Recycle` strands the previous page cipher unzeroized | P3 | | X | open — see below |
| **X6** | `SetByKeyId` sits below its callees | P3 | | X | **fixed** with C8/C9 |

---

## C1 / X1 — the P0

Two rules, each locally correct, contradicted each other.

**§5.2.1 (never-rewind).** `PagedFile.Open` rounds `PageCount` **up** past a torn trailing page so that
page's id — and therefore its GCM nonce — is never re-issued. `MarkCommitted` then publishes
`PagePosition(PageCount)`, so the committed extent permanently covers a page that *cannot* authenticate.
This is deliberate and is pinned by three tests (`TornTailBurnsItsPageIdsInsteadOfReusingThem`,
`CommittedExtentIsIndependentOfPhysicalLength`, `UncleanShutdownNeverReusesAPageIdItMayHaveLost`).

**R5 (authenticate the commit window).** `AuthenticateCommitWindow` fell back to `fromOffset = 0`
whenever there was no comparable predecessor — no older candidate, or one naming a different data slot.
That is precisely the §5.2 step-3 fallback the `Buffered` default relies on. Authenticating from 0 walks
straight into the burned page, so **once any tail had ever been torn the fallback could never succeed
again**: both generations rejected → `!isAdopted` → `WipeFiles()` → an empty store.

**X1** is the same collision seen from the other end: `PagedFile.Open` bounded the extent by
`wholePagesLength`, so it rejected the very extent `MarkCommitted` writes for a burned page. Once a
second commit put that extent in the other slot, nothing was adoptable.

Reproduced end to end — torn tail, a recovery cycle, then the canonical `Buffered` crash (the newest
commit's bytes never reached the device): **0 of 60 keys survived**. Encryption on is the discriminator;
with `DisableEncryption = true` nothing fails, because `NoopPageCipher` cannot fail a tag.

**Fixed** by making both rules agree:
- authentication is limited to the window a generation *adds*; below the previous committed extent a
  failing page is a read-time miss (§5.3), never grounds to reject a slot — and with no comparable
  predecessor there is no window to check, so the generation is adopted on the superblock's own
  authentication;
- `PagedFile.Open` tolerates an extent covering exactly one burned page.

Regression test: `ReviewRegressionTests.TwoRecoveryCyclesWithATornTailDoNotWipeTheStore`, plus
`ATornTailFollowedByTwoCommitsDoesNotWipeTheStore`. Note the first two attempts at this test **passed
without the fix** — they never forced the `previousState = null` fallback. A test that is not red
beforehand proves nothing; the committed one truncates to exactly the older generation's extent so the
newest is unadoptable and the fallback is genuinely exercised.

---

## Still open

### C3 (P2) — fallback adoption authenticates the whole extent
`KvasarStore.AuthenticateCommitWindow`. The C1 fix removes the *wipe*, but the remaining full-extent
path is still O(store) rather than O(index) whenever the two candidates name different data slots — for
example on the first open after a compaction switch commit. `DESIGN.md` limitation 2 advertises "Open is
O(index), not O(data)". Bound the fallback against a recorded known-good extent instead of defaulting to
the whole file. `BENCHMARKS.md` measures only the incremental path (Open 2.3 → 4.1 ms); the full-extent
path is unmeasured.

### X3 (P3) — `Recycle` strands the previous page cipher
`PagedFile.Recycle` replaces `_incarnation` without disposing the old cipher, so its page/nonce key
copies escape R17's zeroization. **Disposing there is not safe**: R1's fix depends on an in-flight
lock-free reader still being able to decrypt with the incarnation it captured, and an earlier attempt to
dispose at that point was rejected during the round-2 merge for exactly this reason. Closing it properly
needs reference counting or quiescence tracking, which is why it is recorded rather than patched.

### X2 — regression test added, and the finding's severity corrected down
Covered by `ScanIdentityTests.ARecycledSnapshotLocatorIsNotIdentifiedByItsHash`, which drives compaction
between `MoveNextAsync` calls rather than gating on storage I/O (the original agent-written test hung
because its compaction gate never armed).

**The reported impact was wrong and is corrected here.** X2 claimed a scan could "yield that key twice".
It cannot: each pending snapshot locator names a distinct offset, so distinct entries always resolve to
distinct records — a duplicate is unreachable by this mechanism. What actually happens, measured against
the pre-fix check with an all-colliding hasher, is that the recycled slot shifts every record down by
one, so each stale entry resolves onto its *neighbour*:

```
pre-fix  scan yields: k00, k02, k03, k04, k05, k06, k07   (k01 silently omitted)
```

So the harm is **omission plus mis-attribution**, not duplication — and because the yielded pair is
decoded wholesale from whatever record the locator lands on, the caller receives a self-consistent,
currently-live `(key, value)`. It just isn't the entry the snapshot named, and one live key vanishes from
a full scan. That is a real identity bug, but it is P3, not P2: no caller ever sees fabricated bytes.

The caller-observable discriminator the test asserts is that a snapshot-based scan must never return a
record written *after* the snapshot was taken. Pre-fix it returns one; post-fix it does not.

### A coverage gap worth naming
A P0 shipped through a green 440-test suite. What the suite did not have, and still should, is a crash
case that survives **two** recovery cycles with a torn tail and an idle session in between. That exact
sequence is what C1 needed, and nothing in the suite walked it.

---

## Cleared

Both agents independently confirmed: exactly one package reference (`System.IO.Hashing`); net10.0 +
net9.0 clean; no reachable AES-GCM nonce reuse (fresh salt per `Create`/`Recycle`, page ids monotone
within an incarnation, rounded up at open). Claude additionally traced and cleared the round-2 fix
interactions on the open/recovery path (`IndexLog.Open`'s clamp, `Recover`'s `==` test, the MAC parity
scheme and its ordering against the superblock write), the whole compaction path against interleaved
writes and deletes (R4+R11+R14+R12), the zero-copy view lifetime across R14's unlocked read → locked
apply boundary, `SeedAccounting`/`TryParseSlot` degrading rather than rejecting, and the flush loop's
clean⇒dirty edge for lost wakeups.
