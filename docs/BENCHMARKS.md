# Kvasar — benchmarks vs SQLCipher

Kvasar's goal is to be **faster than SQLite + SQLCipher** on ActualChat's client-cache workload
(read-dominant, batched writes, ~25 MB hot dataset). This compares Kvasar against a faithful
replica of ActualChat's `SQLiteBatchingKvasBackend` (encrypted `items(Key TEXT PK, Value BLOB)`,
WAL, `synchronous=normal`, `insert or replace`, one connection per reader thread).

Kvasar's storage stack is **fully async** (SPEC §4); the SQLite baseline stays **synchronous**, because
sqlite-net is synchronous by design and wrapping it in `Task.Run` would add thread-pool overhead and
misrepresent the comparison. So this measures async Kvasar against SQLite as it actually ships.

## How to run

```
dotnet run -c Release --project benchmarks/ActualLab.Kvasar.Benchmarks -- \
    --n 100000 --value sweep --threads 8 --lookups 500000 --engines both
```
Args: `--scenario sweep|chat` (default `sweep`), `--n` key count, `--value <bytes|sweep>`
(sweep = 128/1024/4096), `--threads` lookup threads, `--lookups` total random lookups,
`--engines kvasar|sqlite|both`, `--pagesize <bytes>` (Kvasar page size; default 4096 for the sweep,
16384 for `--scenario chat`).
Keys are 50 bytes; values random. `--scenario chat` runs the cold-start scenario below (two dataset
configurations, one table each) and ignores the sweep's sizing args.

> Run it on a quiesced machine. A concurrent `dotnet test` inflated every engine's numbers by
> 20–60% in one run — SQLCipher's included, which is how the contamination was spotted.

## Representative results

Machine: 32 logical cores, .NET 10, Windows, N = 100,000 keys, 8 lookup threads, 500k random lookups.
Kvasar uses AES-256-GCM (encrypted, like SQLCipher). Higher is better except ms columns.

Re-measured after the v2 storage rewrite (superblock + two-slot data/index files, total compaction).
The previous numbers, taken against the segment model, are kept in the comparison below.

### Value = 128 B (12.8 MB — fits the page cache, like the ~25 MB hot set)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **628** | **18.7** | 31.3 | 146 | **6,278** | **1.0** | **1.7** |
| Kvasar (no-enc) | 996 | 18.6 | 12.7 | 103 | 7,942 | 0.8 | 1.5 |
| SQLCipher | 130 | 26.5 | 1.5 | **139** | 149 | 49.6 | 99.4 |

### Value = 1 KB (102 MB — exceeds the 64 MB cache)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **311** | **137** | 10.7 | **180** | **349** | **10.9** | **62.4** |
| Kvasar (no-enc) | 614 | 137 | 9.4 | 86 | 371 | 10.2 | 61.0 |
| SQLCipher | 54.7 | 144 | 1.2 | 719 | 117 | 66.2 | 134 |

### Value = 4 KB (410 MB — far exceeds cache; value ≥ the default page size)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| Kvasar (AES-GCM), 4 KB pages | 101 | 822 | 9.9 | 682 | 88.4 | 52.1 | 183 |
| **Kvasar (AES-GCM), 16 KB pages** | **200** | **547** | 29.4 | **288** | **136** | **27.3** | 113 |
| Kvasar (no-enc), 16 KB pages | 217 | 546 | 12.7 | 240 | 117 | 29.5 | 150 |
| SQLCipher | 20.7 | **468** | 1.8 | 2,648 | 73.7 | 102 | 176 |

### v1 (segments) → v2 (superblock), Kvasar AES-GCM only

| | Write k/s | Startup ms | Lookup k/s |
|---|--:|--:|--:|
| 128 B | 399 → **628** (+57%) | 121 → 146 (**+20% worse**) | 8,653 → 6,278 (**−27%**) |
| 1 KB | 233 → **311** (+33%) | 185 → **180** (−3%) | 386 → 349 (−10%) |
| 4 KB @16 KB pages | 122 → **200** (+64%) | 318 → **288** (−9%) | 171 → 136 (**−21%**) |

**Writes are the clear win** — no segment rolling, no per-write checkpoint bookkeeping, one hash per
key in `SetMany` instead of three, and a flush loop armed on the clean⇒dirty edge rather than ticking.

**Lookups regressed 10–27%, and 128 B startup is 20% worse** — enough that Kvasar now *loses* startup
to SQLCipher at 128 B (146 ms vs 139 ms), the one cell in these tables where it does. Candidate
causes, in decreasing confidence: `Scan` must now re-hash each record's key to confirm it is the one
its index entry named (DESIGN-Durability §14.2 — slots are recycled in place, so a stale locator can
decode as a genuine *different* record, which the segment model made impossible by unlinking);
`IndexEntry` grew 21 → 24 bytes, so an index pass touches ~14% more memory; and recovery now rotates
the index whenever it replays. The first is a correctness fix and is not negotiable. **The rest is
unprofiled — treat these as hypotheses, not findings**, and note the lookup column moves ±20% between
otherwise identical runs, so the −27% is a direction, not a measurement.

> ### ⚠ These numbers are not durability-comparable
>
> `KvasarOptions.Durability` now defaults to `Buffered`, and `Flush(fsync: true)` **ignores the flag**
> (durability is a store-level property in v2, see `DESIGN-Durability.md` §2). So Kvasar no longer
> fsyncs at the end of a run while SQLite still runs `wal_checkpoint(TRUNCATE)`.
>
> That breaks this file's own rule — *"Both stacks end fully durable… Neither side is credited for
> work it merely deferred."* It shows up most in the chat scenario, where Kvasar's flush went
> 2.8 ms → 0.2 ms, which is most of that scenario's apparent gain. **The harness needs a
> `--durability` switch and a re-run at `Flushed` before the comparison is fair again.** Tracked in
> `TODO.md` § P5.

\* Startup ms = open + full `ListAllEntries`/`Scan` (the client cache's launch-time hydration).
Open ms = just reopening the store (Kvasar: load `.kidx` + seed accounting; SQLite: open connection).

## Takeaways

Kvasar wins every metric at every value size except bare `Open`, on-disk size at 4 KB, and — new in
v2 — startup at 128 B, where it is now marginally behind.

- **Point reads — Kvasar wins decisively.** ~**65×** faster when the hot data fits the page cache
  (the target ~25 MB scenario), narrowing to ~2–3.4× when the dataset is many times the cache. Tail
  latency is dramatically better (p99 1.4–99 µs vs SQLCipher's 108–166 µs) — one hash probe + one
  cached page decrypt + zero-copy slice vs. a B-tree descent through the SQLite VM.
- **Batched writes — Kvasar wins 4.7–8.6×** (at a page size suited to the value size), up from
  3.0–6.1× in v1.
- **Startup hydration — 0.96× (128 B, i.e. a slight loss), 4.1× (1 KB), 8.7× (4 KB at 16 KB pages).**
  The 128 B case is the honest weak spot: SQLite defers work that Kvasar does eagerly, and at a small
  dataset there is not enough deferred work for Kvasar's eager index load to pay for itself.
- **On-disk size.** Smaller than SQLCipher for small values. At 4 KB with 4 KB pages it is ~1.75×
  larger, because a value ≥ the page can't stay single-page and rounds up to a multi-page run;
  **16 KB pages cut that to 564 MB (−31%)**, close to SQLite's 468 MB.

## ActualChat cold start (`--scenario chat`)

The sweep measures the engines in isolation. This scenario measures what the app actually does at
launch: a phone opens its client cache and speculatively executes the compute methods that render the
UI, which hammers the cache in a short burst.

**Stacks.** Same dataset, same workload, same seeds — only the stack above the store differs, and
each engine runs in the stack it would actually ship in:

- **SQLCipher + `BatchingKvas`** — the full port of ActualChat's layer
  (`benchmarks/.../BatchingKvasHarness.cs`): a 256-entry LRU read cache, a `BatchProcessor` reader
  (batches of ≤64 keys, ≤4 workers, one `GetMany` per batch), and a single `LazyWriter`
  (**500 ms** debounce, 64-item flush → one `SetMany`). The SQLite side mirrors `DbHelpers.FindMany`
  — one `where Key in (select e.value from json_each(?) e)` query per call, one connection per
  reader worker — and stays synchronous, as it ships.
- **Kvasar + `plain`** — *no layer at all*: the 8 app threads call `store.Get`/`store.Set` directly.
  No read cache, no read batching, no external writer; write debouncing is
  `KvasarOptions.FlushDelay = 0.5 s`, which is why the harness's `LazyWriter` is set to the same
  500 ms — neither side is credited for deferring more than the other.

Kvasar behind `BatchingKvas` is also measured, as evidence that the layer is unnecessary in front of
it — not as a shipping configuration.

Kvasar uses `PageSize = 16 KB` and `PageCacheBytes = 16 MB`, a phone-sized budget (the sweep uses
64 MB). That 16 MB deliberately sits *above* config A's 12 MB dataset and *below* config B's 25 MB,
so the two configs bracket the point where the page cache stops being able to hold the working set.

**Dataset** (fixed seed, sized from a byte budget rather than hardcoded counts): 80% of the bytes are
chat tiles, 20% misc values. Two configurations are run, each with its own table:

```
===== Config A: 12 MB dataset, 500 tile + 500 misc reads =====
Dataset: 12.0 MB in 19,094 entries (tiles 9.6 MB = 80.0 %, misc 2.4 MB = 20.0 %)
  Tiles: 2,992 over 48 chats [L5: 1,816 (60.7 %), L20: 871 (29.1 %), L80: 305 (10.2 %)],
         50,900 chat entries, 50-byte keys
  Tile size: mean 3.09 KB, median 1.05 KB, p99 15.52 KB, max 17.08 KB
  Message text: mean 65.8 chars (log-normal, median 40, sigma 1.0), +120 B/entry overhead
  Misc: 16,102 entries, 49-byte keys, 100-byte mean value

===== Config B: 25 MB dataset, 1,000 tile + 1,000 misc reads =====
Dataset: 25.0 MB in 39,800 entries (tiles 20.0 MB = 80.0 %, misc 5.0 MB = 20.0 %)
  Tiles: 6,239 over 48 chats [L5: 3,793 (60.8 %), L20: 1,814 (29.1 %), L80: 632 (10.1 %)],
         105,805 chat entries, 50-byte keys
  Tile size: mean 3.08 KB, median 1.03 KB, p99 15.52 KB, max 18.65 KB
  Message text: mean 66.0 chars (log-normal, median 40, sigma 1.0), +120 B/entry overhead
  Misc: 33,561 entries, 49-byte keys, 100-byte mean value
```

Tiles follow the reader's `Long5To80` stack (layers 5/20/80, weighted 60/30/10 by count); keys look
like `ChatEntryReader.GetTile:{chatId}:{layer}:{start}`.

**Measured operation.** Populate, close, then time from a cold open: 8 app threads issue the burst
against **distinct keys** (1,000 keys in config A, 2,000 in config B, sampled without repetition and
split evenly across the threads), 10% of reads also `Set` a fresh same-size value, then flush.
Nothing is deliberately re-read: an app start renders distinct UI, and Fusion's compute cache dedupes
repeats upstream.

Both stacks end **fully durable** — the harness `LazyWriter` is drained and `wal_checkpoint(TRUNCATE)`
runs for SQLite; plain Kvasar ends on `store.Flush(fsync: true)`, which force-seals the tail rather
than waiting out the 500 ms debounce. Neither side is credited for work it merely deferred. Every
row is the median of 5 cold starts; `min–max` spans all 5.

Machine: 32 logical cores, .NET 10, Windows. Lower is better everywhere.

### Config A — 12 MB dataset, 1,000 reads (500 tiles + 500 misc)

| Engine | Stack | DB MB | **TOTAL ms** | min–max | Open | Read | Flush | Read calls | keys/call | Cache hit | Write calls | p50 µs | p99 µs |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| **SQLCipher** | BatchingKvas | 15.6 | **51.3** | 50.9–58.6 | 1.2 | 39.7 | 10.3 | 373 | 2.68 | 0.0% | 2 | 227 | 733 |
| **Kvasar (AES-GCM)** | **plain** | 14.6 | **5.3** | 5.3–5.8 | 2.3 | 2.8 | 0.2 | 1,000 | 1.00 | — | 111 | 5.6 | 78.6 |
| Kvasar (no-enc) | plain | 14.6 | 5.4 | 4.8–5.8 | 2.6 | 2.5 | 0.2 | 1,000 | 1.00 | — | 111 | 6.1 | 70.1 |
| Kvasar (AES-GCM) | BatchingKvas | 14.6 | 6.3 | 6.2–6.8 | 2.0 | 3.9 | 0.4 | 733 | 1.36 | 0.0% | 2 | 21.0 | 131 |

### Config B — 25 MB dataset, 2,000 reads (1,000 tiles + 1,000 misc)

| Engine | Stack | DB MB | **TOTAL ms** | min–max | Open | Read | Flush | Read calls | keys/call | Cache hit | Write calls | p50 µs | p99 µs |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| **SQLCipher** | BatchingKvas | 32.4 | **99.8** | 98.7–111.7 | 1.2 | 85.5 | 12.9 | 750 | 2.67 | 0.0% | 3 | 271 | 515 |
| **Kvasar (AES-GCM)** | **plain** | 30.5 | **8.6** | 8.4–13.5 | 2.7 | 5.4 | 0.3 | 2,000 | 1.00 | — | 182 | 15.3 | 70.4 |
| Kvasar (no-enc) | plain | 30.5 | 9.1 | 8.5–9.5 | 2.8 | 5.9 | 0.3 | 2,000 | 1.00 | — | 182 | 14.3 | 94.1 |
| Kvasar (AES-GCM) | BatchingKvas | 30.5 | 10.5 | 10.4–10.9 | 2.8 | 7.3 | 0.3 | 1,480 | 1.35 | 0.0% | 3 | 23.6 | 116 |

### v1 → v2, chat scenario (Kvasar AES-GCM, TOTAL ms)

| | v1 | v2 | |
|---|--:|--:|---|
| A · plain | 9.1 | **5.3** | −42% |
| A · `BatchingKvas` | 10.7 | **6.3** | −41% |
| B · plain | 12.6 | **8.6** | −32% |
| B · `BatchingKvas` | 15.3 | **10.5** | −31% |
| A / B · SQLCipher (control) | 52.3 / 102.3 | 51.3 / 99.8 | −2% |

Kvasar in its shipping configuration is **9.7×** SQLCipher (A) and **11.6×** (B), up from 5.7× and
8.1×. SQLCipher moving <3% across the same runs is the control that says the Kvasar deltas are real
and not machine noise.

**Read the gain net of the durability caveat above.** Flush dropped 2.8 → 0.2 ms (A) and 3.9 → 0.3
(B) because `Buffered` no longer fsyncs — that is most of the improvement. Netting it out, the honest
v2 gain here is roughly 9.1 → 8.1 ms (A), i.e. ~11%, not 42%.

**One regression was found and fixed by this scenario**, which is the argument for running it: the
`BatchingKvas` row first came back at 15.3 ms (A) / 32.5 ms (B), 43% and 114% *worse* than v1. Cause
was the I27 `GetMany` change issuing a ~1 MiB readahead per call — fine for the cold 64-key batch
SPEC §6.4 targets, pathological at the `keys/op ≈ 1.35` this layer actually produces. Prefetch is now
gated on batch size, and the row is better than v1. The sweep never showed it, because the sweep only
ever issues full 64-key batches.

**Run-to-run spread** (4 independent process runs of the whole scenario, TOTAL ms): SQLCipher
52.1–55.0 (A) / 102.3–107.2 (B); Kvasar AES-GCM plain 9.1–10.3 / 12.0–12.6; no-enc plain 8.5–12.8 /
11.4–12.2; Kvasar + `BatchingKvas` 9.9–16.8 / 14.6–15.9. So on the Kvasar rows differences under
~1.5 ms are noise — which specifically means the AES-GCM vs no-enc gap is noise, and `BatchingKvas`
vs `plain` is decisive only in config B by TOTAL (it is decisive in *both* configs on the read burst,
where the spread is much tighter).

### Takeaways

- **Cold start is 5.7× faster at 12 MB (52.3 → 9.1 ms) and 8.1× at 25 MB (102.3 → 12.6 ms).** Every
  phase but `Open` improves: the read burst 41.3 → 3.2 ms (13×) and 87.3 → 5.4 ms (16×), the durable
  flush 9.4 → 2.8 and 13.7 → 3.9 ms. Bare `Open` is slower (1.2–1.4 → ~3 ms) because Kvasar loads its
  index up front instead of deferring the work; it wins startup regardless.
- **The gap widens with load, because only SQLCipher scales with it.** Per read, SQLCipher costs
  41 µs (A) and 44 µs (B); Kvasar costs 3.2 µs and 2.7 µs — flat, and marginally *better* on the
  bigger dataset, where the fixed thread-ramp cost is amortized over more operations. Doubling the
  dataset and the burst costs SQLCipher +96% and Kvasar +38%.
- **Kvasar's cold start is dominated by fixed cost, not by data.** Halving the workload only takes it
  from 12.6 to 9.1 ms (−28%, not −50%), because `Open` (~3 ms) and the durable flush (~3 ms) barely
  move; the read burst is the only part that halves. This is why config A looks less than
  proportionally faster — it is not an anomaly, it is the read burst having shrunk to a quarter of the
  total.
- **Kvasar needs no layer above it.** `FlushDelay` absorbs what `LazyWriter` was for, and the read
  path is pure overhead: the same store behind `BatchingKvas` costs 10.7 vs 9.1 ms (A) and 15.3 vs
  12.6 ms (B), with the read burst at 4.8 vs 3.2 ms and 8.9 vs 5.4 ms — despite issuing *fewer*
  backend calls (699 vs 1,000; 1,478 vs 2,000). A channel hop plus a `TaskCompletionSource` per key
  plus worker dispatch costs more than the read it amortizes, and Kvasar's page cache already makes
  the 256-entry LRU redundant.
- **Unbatched writes are no longer a problem.** Plain Kvasar issues 111/182 single-record `Set`s
  where the harness issues 2–3 `SetMany`s, and still flushes in 2.8–3.9 ms: with `FlushDelay > 0` a
  `Set` appends into the unsealed tail instead of sealing a page per record.
- **Encryption is free on this workload** — AES-GCM and no-enc land within the run-to-run spread of
  each other in both configs (and in one run no-enc came out *slower*).
- **A page cache above the dataset buys median latency, not cold-start time.** Config A's 14.6 MB
  file fits the 16 MB page cache and its p50 is 5.7 µs — a decrypted-page hit; config B's 30.5 MB
  does not and its p50 is 17.9 µs. Raising `PageCacheBytes` to 64 MB confirms the mechanism: config
  B's p50 drops to 4.0 µs, while TOTAL does not improve (12.6 → 14.0 ms, i.e. no better and within
  the spread). A distinct-key burst can only hit on pages a *sibling* record already pulled in, so
  the misses that dominate the wall clock happen either way.
- **Read batching barely engages at this concurrency** — 1.35–1.43 keys/call for Kvasar vs 2.4–2.7
  for SQLCipher. Batches only form while a worker is busy, and Kvasar's workers are never busy long
  enough. Batching is a workaround for a slow backend.
- **The 256-entry LRU contributes nothing here (0.0% hit rate).** `BatchingKvas` populates the read
  cache on `Set` only, and no key is read twice, so it can never hit. That is the expected result for
  a distinct-key cold start, not a defect.
- **16 KB pages cost 1.5% on disk here** (30.50 MB vs 30.06 MB at 4 KB pages) — see below.

### On-disk size vs `PageSize` on this dataset

Config B's dataset — the same 25.20 MB of live records, populated identically, varying only
`PageSize`. (The 4 KB column also
shows what `FlushDelay` bought: the previous, pre-`FlushDelay` run of this dataset was 31.3 MB at
4 KB pages, because each 64-item `SetMany` sealed the tail — deferring the seal packs across batches.)

| `PageSize` | 4 KB | 8 KB | **16 KB** | 32 KB |
|---|--:|--:|--:|--:|
| File MB (AES-GCM) | 30.06 | 29.70 | **30.50** | 28.39 |
| Padding MB | 4.86 | 4.49 | **5.30** | 3.19 |

The size went **up** at 16 KB, which is not the direction the 4 KB-value sweep above shows, and the
curve is not monotonic. All of the difference is page padding; per-page AES-GCM overhead is 16 bytes
and thus *falls* with bigger pages (0.12 MB at 4 KB → 0.03 MB at 16 KB). The driver is the L80 tile
class: ~14.9 KB each, 10% of tiles but ~48% of tile bytes. A record only goes into the current page
if it fits the free space left in it, otherwise the tail is sealed and the remainder padded — so a
14.9 KB record almost always forces a seal at 16 KB pages, wasting the tail's free space *and* the
~1.5 KB left over after it. At 4/8 KB the same record spills into a multi-page run whose tail
remainder keeps packing; at 32 KB two of them fit per page. 16 KB is the local worst case for this
particular size mix, and 32 KB is both smaller and equally fast (its cold start matched 16 KB's
within the run-to-run spread) — the 16 KB configuration is kept here because it is the value the
sweep recommends generally.

## Tune `PageSize` to the value size

`PageSize` is the single highest-leverage knob, and it fixes two problems at once — I/O count and
on-disk bloat. At 4 KB values, moving from 4 KB to 16 KB pages:

| 4 KB values, AES-GCM | 4 KB pages | 16 KB pages |
|---|--:|--:|
| Write k/s | 77.9 | **122** (+56%) |
| File MB | 822 | **564** (−31%) |
| Startup ms | 649 | **318** (−51%) |
| Lookup k/s | 137 | **171** (+25%) |
| p50 µs | 29.8 | **20.0** (−33%) |

Rule of thumb: **`PageSize` should comfortably exceed your typical value size**, so values stay
single-page (zero-copy reads, no multi-page run) and each async I/O carries more payload.

## The async trade-off (measured)

Converting the storage stack from synchronous to async I/O was a deliberate design choice — SQLite is
synchronous for historical reasons and a client-side cache shouldn't block the caller's thread. The
cost is not free, and it is worth stating precisely:

- **The warm read path is untouched — slightly better.** `Get` and `PagedSegment.GetPage` are not
  `async` methods; a cache-resident single-page record returns an already-completed `ValueTask` with
  no state machine and no allocation. At 128 B: lookups went **6,774 → 7,270 k/s** and **p99 halved
  (2.9 → 1.6 µs)** versus the synchronous implementation.
- **Bulk I/O-bound paths regressed at first**, and the regression tracked **page-I/O count**, not
  record count (~3,200 page I/Os at 128 B vs ~100,000 at 4 KB). Working the deltas out per operation,
  both the write and the scan paths landed at roughly **~20 µs of extra cost per page I/O** —
  consistent across two independent paths, which is about what Windows overlapped-I/O completion
  costs versus a synchronous write into the OS cache. Writes fell 52–57% at 1 KB/4 KB and startup
  hydration roughly tripled.

The fix was not to undo the async conversion but to stop paying that fixed cost per 4 KiB page:

- **Write-behind** — appends stage into a ~1 MiB buffer, so a 64-record batch at 4 KiB values is one
  `WriteAsync` instead of 64. Safe because the store's seal-before-publish protocol already flushes
  before publishing a locator, so no reachable locator can point at an unwritten page.
- **Readahead** — sequential walks pull ~1 MiB of consecutive pages per read.
- **Log-order scanning** — `Scan` sorts the index snapshot by `(SegmentId, Offset)`. The snapshot is
  in *hash* order, which made a full scan a storm of random page faults; scan order is unspecified by
  SPEC §4, so this is free, and it is what makes readahead effective.

| vs. the old synchronous implementation | Write k/s | Startup ms |
|---|--:|--:|
| 128 B | 356 → **399** (+12%) | 97 → 121 |
| 1 KB | 178 → **233** (+31%) | 597 → **185** (3.2× better) |
| 4 KB, 4 KB pages | 58 → **77.9** (+34%) | 1,157 → **649** (1.8× better) |
| 4 KB, **16 KB pages** | 58 → **122** (2.1×) | 1,157 → **318** (3.6× better) |

Net: the async build now **beats the synchronous one on writes at every value size**, and on startup
everywhere except 128 B (121 vs 97 ms), while keeping the warm read path allocation-free — 128 B
lookups went 6,774 → 8,653 k/s and p99 2.9 → 1.3 µs. Non-blocking I/O turned out to cost nothing
once the per-operation overhead was amortized instead of paid per page.

## Earlier fixes worth keeping on record

**The write fix.** The first cut wrote each `.kidx` delta by opening/closing the file per entry — one
file open per write, capping writes at ~9 k/s. Holding the delta file open across the store's life
(buffered append, flushed on `Flush`/checkpoint) raised writes **~21×** with no durability change.

**Open-time fix (O(data) → O(index)).** The first cut decrypted the whole `.klog` on open to seed
live/dead accounting, so open scaled with data size (~2 s for a 410 MB store) and the `.kidx` fast
path saved nothing (Open ≈ full rebuild). Now open loads `.kidx`, decrypts only the post-checkpoint
gap (`ScanFrom(hwm)` — zero records after a graceful close), validates the key with a single-page
decrypt, and seeds accounting from the loaded index. Open dropped from 79/320/1998 ms to a roughly
flat **~15–37 ms** regardless of value size. Wrong-key / tamper is still caught on open (the
single-page validation decrypt), so a bad store still deterministically wipes-and-recreates.

> mmap note: memory-mapping the `.kidx` was considered but doesn't move open time — the `.kidx` read
> was already ~1 ms; the cost was the `.klog` decrypt, and the `.klog` is encrypted so mmap can't
> avoid the decrypt (SPEC §5.1). Eliminating the scan, not mmapping the index, was the fix.
