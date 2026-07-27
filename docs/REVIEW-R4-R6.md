# Kvasar — review rounds 4, 5 and 6

Rounds 2 and 3 have their own files ([`REVIEW-R2.md`](REVIEW-R2.md), [`REVIEW-R3.md`](REVIEW-R3.md)).
Rounds 4–6 are recorded together here because they form one arc: **each round reviewed the previous
round's fixes, and rounds 4 and 5 each found that those fixes had introduced a new P0.** Round 6 was the
first to find none.

Every round used two independent cold-read agents — Claude Opus 5 and Codex (GPT-5.x) — reviewing the
same diff without seeing each other's findings.

## The arc

| Round | Baseline | Scope | Found | Verdict |
|---|---|---|---|---|
| 4 | `72787a9` | the 22 round-2 fixes | 13 (1 P0, 2 P1) | not shippable |
| 5 | `5e40ba3` | the round-3/4 fixes + format v2 | 7 (1 P0, 1 P1) | not shippable |
| 6 | `ea1fdef` | the round-5 fixes | 8 (**0 P0, 0 P1**) | shippable |

Suite over the same span: **412 → 463 passing, 2 skipped → 0.**

## What each round found

### Round 4 — the round-2 fixes had introduced two P1s and hidden a removed guarantee
- **C1/X1 (P0)** — adoption authenticated a candidate's *whole* committed extent when it had no
  comparable predecessor. §5.2.1 guarantees an unauthenticatable burned page lives inside that extent, so
  once any tail had been torn the §5.2 step-3 fallback could never succeed: both generations rejected,
  `WipeFiles`, empty store. Reproduced at **0 of 60 keys surviving**.
- **C1 (P1)** — `Scan`'s new identity check required the snapshot's *locator* to still be live, but every
  ordinary mutation changes it. 39 live keys plus one `Compact()` yielded **1**.
- **C2 (P1)** — resynchronising after an unreadable header page probed the record's own continuation
  pages, so caller value bytes could be indexed as a record: `Get("never-written")` returned
  `"phantom-value"`. Round 3 had traded silent loss for silent fabrication.
- **C3** — the round-3 fix had replaced the authentication call with an unconditional `return default`,
  and `TODO.md`/`REVIEW-R3.md` then described the removed check as present-but-slow. *The notes recorded
  a missing guarantee as a cost.*

### Round 5 — the format revision was sound; its floor and an unrequested migrator were not
- **X1 (P0)** — the new `DataAuthenticationFloor` persisted the pre-burn extent while the high-water mark
  was the rounded-up post-burn one, so that generation's window was exactly the burned page. If a later
  superblock write tore and left it the sole valid slot, adoption rejected it and wiped. **This was the
  round-4 P0 class reintroduced by the round-4 fix's own successor.**
- **X2 (P1)** — `LegacyStoreImporter`, which no brief asked for, wiped the v1 files before the v2 import
  was durable, discarded every recoverable key on a single bad page when a v1 index was present (0/40
  survivors, versus 8/40 *without* an index), and was unreachable for any caller setting
  `KvasarOptions.Version` — which SPEC §13 instructs the sole consumer to do. Removed; 311 lines.
- Both reviewers independently failed to break the format-2 page framing itself.

### Round 6 — no P0/P1
- **C1 / R6-2 (P2)** — the fourth appearance of the burned-page rule: round 5 fixed the *write* side, but
  `AuthenticateCommitWindow` overrode the persisted floor with the predecessor's extent on the *read*
  side, so a torn-tail store never converged while only being read. No data loss; recovery work and index
  rotations on every open, self-healing on the first write past the burn.
- Five P3s: a wrong-key guard that missed versioned stores, a one-byte corruption that could make a store
  unopenable, and doc/style drift.

## Two things worth carrying forward

**The dominant defect class is a wipe, and it recurs.** Five rounds running, the most severe finding was
some path where recoverable data reached `WipeFiles` — usually because advisory or deliberately-damaged
state was treated as an integrity failure. The burned page alone defeated three consecutive fixes, each
of which looked correct in review.

**Property-based tests outperformed reading.** The two defects that four review rounds missed entirely —
`Scan` omitting live keys, and a read-your-own-writes false miss — were found by
`HighConcurrencyInvariantTests`, and the crash fuzzer found a candidate wipe on its first run. Both were
built *because* the reviews kept finding things the suite could not. Conversely, reviewers twice reported
impacts that proved wrong on execution: one claimed a duplicate-yield that was unreachable, and one P0
was falsified empirically by the other reviewer (see below). Prefer running to arguing.

## Where the reviewers disagreed, and how it was resolved

- **Round 5, the P0.** Codex traced it statically; Claude formed the same hypothesis, tested it, and
  reported 120/120 keys surviving. Reading the source settled it: Claude had falsified the *broader*
  claim (that every later commit's window spans the burn — false, later commits use the post-burn extent)
  but not Codex's narrower one, which needs a torn superblock write to leave that generation alone.
  **Codex was right.**
- **Round 6, the versioned-format-1 wrong-key case.** Codex rated it P0 — "the original file set is
  permanently deleted". Claude rated it P3. **Claude was right**: with the importer removed a format-1
  store is rebuilt under the *correct* key too, so a wrong key causes no differential loss. Codex's
  severity assumed files that would otherwise survive, which stopped being true when the importer went.

Neither agent was reliably more accurate than the other; the disagreements were resolved by reading the
code or running it, never by preferring a source.

## Still open

- **R7** — whether replaying a prior `.kdat` incarnation is in scope or already covered by `DESIGN.md`
  known-limitation 3. Deferred by decision.
- **Predecessorless authentication** — with no older superblock candidate and no usable floor there is
  nothing to bound a window against; stated rather than implied.
- Custom non-keyed / collision-prone `IKeyHasher`s are supported **best-effort only**: the tested and
  guaranteed configuration is a keyed hasher.
