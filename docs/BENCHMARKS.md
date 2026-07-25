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
Args: `--n` key count, `--value <bytes|sweep>` (sweep = 128/1024/4096), `--threads` lookup threads,
`--lookups` total random lookups, `--engines kvasar|sqlite|both`, `--pagesize <bytes>` (Kvasar page
size, default 4096). Keys are 50 bytes; values random.

> Run it on a quiesced machine. A concurrent `dotnet test` inflated every engine's numbers by
> 20–60% in one run — SQLCipher's included, which is how the contamination was spotted.

## Representative results

Machine: 32 logical cores, .NET 10, Windows, N = 100,000 keys, 8 lookup threads, 500k random lookups.
Kvasar uses AES-256-GCM (encrypted, like SQLCipher). Higher is better except ms columns.

### Value = 128 B (12.8 MB — fits the page cache, like the ~25 MB hot set)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **328** | **19.3** | 28.9 | 169 | **7,270** | **0.8** | **1.6** |
| Kvasar (no-enc) | 428 | 19.2 | 15.6 | 130 | 5,375 | 1.1 | 3.1 |
| SQLCipher | 139 | 26.5 | 1.4 | **138** | 117 | 67.1 | 122 |

### Value = 1 KB (102 MB — exceeds the 64 MB cache)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **86** | 141 | 16.4 | 1,521 | **400** | **10.4** | **42.6** |
| Kvasar (no-enc) | 138 | 141 | 12.1 | 1,124 | 405 | 9.5 | 43.6 |
| SQLCipher | 51 | 144 | 1.4 | **740** | 104 | 69.4 | 142 |

### Value = 4 KB (410 MB — far exceeds cache; value ≥ the default page size)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| Kvasar (AES-GCM), 4 KB pages | 24.7 | 822 | 16.8 | 3,220 | 132 | 30.5 | 89.2 |
| **Kvasar (AES-GCM), 16 KB pages** | **72.3** | **564** | 36.5 | **2,264** | **157** | **21.0** | **82.8** |
| Kvasar (no-enc), 16 KB pages | 84.6 | 563 | 17.0 | 1,799 | 164 | 18.7 | 73.1 |
| SQLCipher | 21.7 | **468** | 1.4 | 2,924 | 72.7 | 103 | 183 |

\* Startup ms = open + full `ListAllEntries`/`Scan` (the client cache's launch-time hydration).
Open ms = just reopening the store (Kvasar: load `.kidx` + seed accounting; SQLite: open connection).

## Takeaways

- **Point reads — Kvasar wins decisively.** ~**62×** faster when the hot data fits the page cache
  (the target ~25 MB scenario), narrowing to ~2–4× when the dataset is many times the cache. Tail
  latency is dramatically better (p99 1.6–89 µs vs SQLCipher's 122–183 µs) — one hash probe + one
  cached page decrypt + zero-copy slice vs. a B-tree descent through the SQLite VM.
- **Batched writes — Kvasar wins 2.4–3.3×** (at a page size suited to the value size).
- **Startup hydration.** Kvasar wins at 4 KB (2,264 vs 2,924 ms) but **loses at 128 B and 1 KB** —
  see the async trade-off below. This regressed relative to the synchronous implementation.
- **On-disk size.** Smaller than SQLCipher for small values. At 4 KB with 4 KB pages it was ~1.75×
  larger, because a value ≥ the page can't stay single-page and rounds up to a multi-page run;
  **16 KB pages cut that to 564 MB (−31%)**, close to SQLite's 468 MB.

## Tune `PageSize` to the value size

`PageSize` is the single highest-leverage knob, and it fixes two problems at once — I/O count and
on-disk bloat. At 4 KB values, moving from 4 KB to 16 KB pages:

| 4 KB values, AES-GCM | 4 KB pages | 16 KB pages |
|---|--:|--:|
| Write k/s | 24.7 | **72.3** (2.9×) |
| File MB | 822 | **564** (−31%) |
| Startup ms | 3,220 | **2,264** (−30%) |
| Lookup k/s | 132 | **157** (+19%) |
| p50 µs | 30.5 | **21.0** (−31%) |

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
- **Bulk I/O-bound paths regressed**, and the regression tracks **page-I/O count**, not record count
  (~3,200 page I/Os at 128 B vs ~100,000 at 4 KB). Working the deltas out per operation, both the
  write and the scan paths land at roughly **~20 µs of extra cost per page I/O** — consistent across
  two independent paths, which is about what Windows overlapped-I/O completion costs versus a
  synchronous write into the OS cache.

| vs. the old synchronous implementation | Write k/s | Startup ms |
|---|--:|--:|
| 128 B | 356 → 328 (−8%) | 97 → 169 |
| 1 KB | 178 → 86 (−52%) | 597 → 1,521 |
| 4 KB, 4 KB pages | 58 → 24.7 (−57%) | 1,157 → 3,220 |
| 4 KB, **16 KB pages** | 58 → **72.3 (+25%)** | 1,157 → 2,264 |

The last row is the point: **larger pages amortize the fixed per-I/O cost**, and at 16 KB pages the
async build *beats* the old synchronous one on writes. Where bulk-write throughput at small page
sizes matters more than non-blocking behavior, the remaining lever is coalescing consecutive page
appends into a single `WriteAsync` (a write-behind buffer) — not yet implemented.

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
