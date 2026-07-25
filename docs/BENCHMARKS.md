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
Keys are 50 bytes; values random. `--scenario chat` runs the cold-start scenario below and ignores
the sweep's sizing args.

> Run it on a quiesced machine. A concurrent `dotnet test` inflated every engine's numbers by
> 20–60% in one run — SQLCipher's included, which is how the contamination was spotted.

## Representative results

Machine: 32 logical cores, .NET 10, Windows, N = 100,000 keys, 8 lookup threads, 500k random lookups.
Kvasar uses AES-256-GCM (encrypted, like SQLCipher). Higher is better except ms columns.

### Value = 128 B (12.8 MB — fits the page cache, like the ~25 MB hot set)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **399** | **19.3** | 28.4 | **121** | **8,653** | **0.7** | **1.3** |
| Kvasar (no-enc) | 493 | 19.2 | 13.9 | 85 | 8,529 | 0.7 | 3.2 |
| SQLCipher | 134 | 26.5 | 3.9 | 140 | 119 | 66.5 | 117 |

### Value = 1 KB (102 MB — exceeds the 64 MB cache)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **233** | 141 | 16.0 | **185** | **386** | **10.9** | **43.0** |
| Kvasar (no-enc) | 334 | 141 | 13.7 | 115 | 471 | 8.6 | 36.8 |
| SQLCipher | 54 | 144 | 1.3 | 743 | 105 | 69.0 | 141 |

### Value = 4 KB (410 MB — far exceeds cache; value ≥ the default page size)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| Kvasar (AES-GCM), 4 KB pages | 77.9 | 822 | 40.5 | 649 | 137 | 29.8 | 81.7 |
| **Kvasar (AES-GCM), 16 KB pages** | **122** | **564** | 35.4 | **318** | **171** | **20.0** | **63.7** |
| Kvasar (no-enc), 16 KB pages | 168 | 563 | 21.4 | 209 | 182 | 16.8 | 59.2 |
| SQLCipher | 19.8 | **468** | 1.4 | 2,498 | 75.0 | 101 | 172 |

\* Startup ms = open + full `ListAllEntries`/`Scan` (the client cache's launch-time hydration).
Open ms = just reopening the store (Kvasar: load `.kidx` + seed accounting; SQLite: open connection).

## Takeaways

Kvasar wins every metric at every value size except bare `Open` (opening a SQLite connection is
trivially cheap — but it defers the work Kvasar has already done, which is why Kvasar still wins
*startup*) and on-disk size at 4 KB.

- **Point reads — Kvasar wins decisively.** ~**73×** faster when the hot data fits the page cache
  (the target ~25 MB scenario), narrowing to ~2–4× when the dataset is many times the cache. Tail
  latency is dramatically better (p99 1.3–64 µs vs SQLCipher's 117–172 µs) — one hash probe + one
  cached page decrypt + zero-copy slice vs. a B-tree descent through the SQLite VM.
- **Batched writes — Kvasar wins 3.0–6.1×** (at a page size suited to the value size).
- **Startup hydration — Kvasar wins 1.2× (128 B), 4.0× (1 KB), 7.8× (4 KB at 16 KB pages).**
- **On-disk size.** Smaller than SQLCipher for small values. At 4 KB with 4 KB pages it is ~1.75×
  larger, because a value ≥ the page can't stay single-page and rounds up to a multi-page run;
  **16 KB pages cut that to 564 MB (−31%)**, close to SQLite's 468 MB.

## ActualChat cold start (`--scenario chat`)

The sweep measures the engines in isolation. This scenario measures what the app actually does at
launch: a phone opens its ~25 MB client cache and speculatively executes the compute methods that
render the UI, which hammers the cache in a short burst.

**Stacks.** Same dataset, same workload, same seeds — only the stack above the store differs, and
each engine runs in the stack it would actually ship in:

- **SQLCipher + `BatchingKvas`** — the full port of ActualChat's layer
  (`benchmarks/.../BatchingKvasHarness.cs`): a 256-entry LRU read cache, a `BatchProcessor` reader
  (batches of ≤64 keys, ≤4 workers, one `GetMany` per batch), and a single `LazyWriter`
  (**500 ms** debounce, 64-item flush → one `SetMany`). The SQLite side mirrors `DbHelpers.FindMany`
  — one `where Key in (select e.value from json_each(?) e)` query per call, one connection per
  reader worker — and stays synchronous, as it ships.
- **Kvasar, plain** — *no layer at all*: the 8 app threads call `store.Get`/`store.Set` directly.
  No read cache, no read batching, no external writer; write debouncing is
  `KvasarOptions.FlushDelay = 0.5 s`, which is why the harness's `LazyWriter` is set to the same
  500 ms — neither side is credited for deferring more than the other.

Kvasar uses `PageSize = 16 KB` and `PageCacheBytes = 16 MB`, a phone-sized budget deliberately
*below* the dataset, so this is a genuine mixed hit/miss workload (the sweep uses 64 MB).

**Dataset** (fixed seed, sized from a 25 MB byte budget rather than hardcoded counts): 80% of the
bytes are chat tiles, 20% misc values.

```
Dataset: 25.0 MB in 39,800 entries (tiles 20.0 MB = 80.0 %, misc 5.0 MB = 20.0 %)
  Tiles: 6,239 over 48 chats [L5: 3,793 (60.8 %), L20: 1,814 (29.1 %), L80: 632 (10.1 %)],
         105,805 chat entries, 50-byte keys
  Tile size: mean 3.08 KB, median 1.03 KB, p99 15.52 KB, max 18.65 KB
  Message text: mean 66.0 chars (log-normal, median 40, sigma 1.0), +120 B/entry overhead
  Misc: 33,561 entries, 49-byte keys, 100-byte mean value
```

Tiles follow the reader's `Long5To80` stack (layers 5/20/80, weighted 60/30/10 by count); keys look
like `ChatEntryReader.GetTile:{chatId}:{layer}:{start}`.

**Measured operation.** Populate, close, then time from a cold open: 8 app threads issue 800 logical
`Get`s of **800 distinct keys** (400 tiles + 400 misc, sampled without repetition and split 100 per
thread), 10% of reads also `Set` a fresh same-size value, then flush. Nothing is deliberately
re-read: an app start renders distinct UI, and Fusion's compute cache dedupes repeats upstream.

Both stacks end **fully durable** — the harness `LazyWriter` is drained and `wal_checkpoint(TRUNCATE)`
runs for SQLite; plain Kvasar ends on `store.Flush(fsync: true)`, which force-seals the tail rather
than waiting out the 500 ms debounce. Neither side is credited for work it merely deferred. Every
row is the median of 5 cold starts.

Machine: 32 logical cores, .NET 10, Windows. Lower is better everywhere.

| Engine | Stack | DB MB | **TOTAL ms** | Open | Read | Flush | Read calls | keys/call | Cache hit | Write calls | p50 µs | p99 µs |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| **SQLCipher** | BatchingKvas | 32.4 | **45.7** | 1.2 | 36.8 | 7.5 | 300 | 2.67 | 0.0% | 2 | 282 | 595 |
| **Kvasar (AES-GCM), 16 KB pages** | **plain** | 30.5 | **11.3** | 5.0 | 2.3 | 3.9 | 800 | 1.00 | — | 85 | 21.0 | 68.5 |
| Kvasar (no-enc), 16 KB pages | plain | 30.5 | 11.3 | 5.5 | 2.3 | 3.5 | 800 | 1.00 | — | 85 | 18.2 | 67.2 |
| Kvasar (AES-GCM), 16 KB pages | BatchingKvas | 30.5 | 13.9 | 5.8 | 4.0 | 4.1 | 572 | 1.40 | 0.0% | 2 | 30.1 | 121 |
| Kvasar (AES-GCM), 16 KB pages | direct reads | 30.5 | 12.0 | 5.3 | 2.6 | 4.1 | 800 | 1.00 | 0.0% | 2 | 22.9 | 69.7 |

`plain` = no layer at all. `direct reads` = the harness with its read path bypassed but writes still
going through the `LazyWriter` — it isolates the read path from the write path. Run-to-run spread on
the Kvasar rows is ~±1 ms, so differences under that are noise.

### Takeaways

- **Cold start is ~4.0× faster** (45.7 → 11.3 ms), and every phase but `Open` improves: the read
  burst 36.8 → 2.3 ms (16×) and the durable flush 7.5 → 3.9 ms. Bare `Open` is slower (1.2 → 5.0 ms)
  because Kvasar loads its index up front instead of deferring the work; it wins startup anyway.
- **Kvasar needs no layer above it.** `FlushDelay` absorbs what `LazyWriter` was for, and the read
  path is pure overhead: the same store behind `BatchingKvas` costs 13.9 ms vs 11.3 ms plain, with
  the read burst at 4.0 ms vs 2.3 ms despite issuing *fewer* backend calls (572 vs 800). A channel
  hop plus a `TaskCompletionSource` per key plus worker dispatch costs more than the read it
  amortizes, and Kvasar's page cache already makes the 256-entry LRU redundant.
- **Unbatched writes are no longer a problem.** Plain Kvasar issues 85 single-record `Set`s where the
  harness issues 2 `SetMany`s, and still flushes in ~4 ms: with `FlushDelay > 0` a `Set` appends into
  the unsealed tail instead of sealing a page per record.
- **Encryption is nearly free on this workload** — AES-GCM and no-enc both finish in 11.3 ms.
- **Read batching barely engages at this concurrency** — 1.40 keys/call for Kvasar vs 2.67 for
  SQLCipher. Batches only form while a worker is busy, and Kvasar's workers are never busy long
  enough. Batching is a workaround for a slow backend.
- **The 256-entry LRU contributes nothing here (0.0% hit rate).** `BatchingKvas` populates the read
  cache on `Set` only, and no key is read twice, so it can never hit. That is the expected result for
  a distinct-key cold start, not a defect.
- **16 KB pages cost 1.5% on disk here** (30.50 MB vs 30.06 MB at 4 KB pages) — see below.

### On-disk size vs `PageSize` on this dataset

Same 25.20 MB of live records, populated identically, varying only `PageSize`. (The 4 KB column also
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
particular size mix, and 32 KB would be both smaller and equally fast (11.0 ms plain, AES-GCM) — the
16 KB configuration is kept here because it is the value the sweep recommends generally.

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
