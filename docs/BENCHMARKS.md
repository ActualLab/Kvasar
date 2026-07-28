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
16384 for `--scenario chat`), and `--durability Flushed|Buffered` (Kvasar only; default `Flushed`).
Keys are 50 bytes; values random. `--scenario chat` runs the cold-start scenario below (two dataset
configurations, one table each) and ignores the sweep's sizing args.

> Run it on a quiesced machine. A concurrent `dotnet test` inflated every engine's numbers by
> 20–60% in one run — SQLCipher's included, which is how the contamination was spotted.

### Data-format-2 framing spot check

The authenticated page frame consumes 8 bytes of the existing plaintext page: usable record payload is
4088 B at the 4 KiB default (−0.195%), 16,376 B at 16 KiB (−0.049%), and 504 B at the 512 B test
minimum (−1.563%). The physical page stride and 16-byte AES-GCM overhead do not change. This is too small
to change the 4 KiB/16 KiB page-size guidance below; boundary-sized records should use the effective
payload rather than raw `PageSize` when predicting spill.

A same-session 20k-key/128-byte spot check used 4 KiB pages, 8 lookup threads, 200k lookups, and
`Flushed` durability. The pre-revision sample was 339.8k Set/s and 9,310.9k Get/s; the median of three
format-2 samples was 338.4k Set/s and 9,236.7k Get/s (−0.4% and −0.8%). This short run is a regression
check, not a replacement for the full sweep: individual lookup samples varied enough that only the
absence of a material hot-path regression is supported.

The round-5 incarnation check repeated that spot check after `Get` began sampling both slot cache ids
before its index probe and validating the selected id before returning a cached record. The AES-GCM
median was 342.0k Set/s and 7,058.5k Get/s, with 0.9 µs p50 and 3.2 µs p99. The three lookup samples
ranged from 6,665.7k to 9,329.0k Get/s, so this confirms the guarded path remains fast but is too noisy
to assign a precise cost to the added checks.

## Final verification (post round-6)

Spot check at `0469e5e`, after six review rounds, data format 2 and the read-lease work. **Single runs on
a machine that had been running agents all session**, so treat these as a no-material-regression check
rather than as a replacement for the multi-run tables below — the deltas sit inside the run-to-run spread
those tables already record.

Durability-matched (Kvasar `Flushed`, SQLite `wal_checkpoint(TRUNCATE)`):

| Scenario | SQLCipher | Kvasar (AES-GCM) | Speedup |
|---|--:|--:|--:|
| Chat cold start, 12 MB | 51.2 ms | **8.1 ms** | 6.3x |
| Chat cold start, 25 MB | 113.3 ms | **10.7 ms** | 10.6x |
| Sweep 128 B — writes / lookups | 136.4 k/s / 121 k/s | **501.6 / 7,831** | 3.7x / 65x |
| Sweep 1 KB — writes / lookups | 52.5 / 105.3 | **269.6 / 336.4** | 5.1x / 3.2x |
| Sweep 4 KB — writes / lookups | 18.8 / 74.4 | **79.9 / 114.0** | 4.2x / 1.5x |
| Startup hydration, 1 KB / 4 KB | 762.7 / 2,532.8 ms | **195.1 / 708.7 ms** | 3.9x / 3.6x |

Chat cold start was 7.7 / 11.3 ms when the tables below were measured, so the correctness work — page
framing, incarnation read leases, commit-window authentication — cost nothing measurable at this
resolution. The 8-byte page frame costs 0.195% of payload at 4 KiB pages and 0.049% at 16 KiB.

## Representative results

Measured 2026-07-27 on an AMD Ryzen 9 9950X3D (32 logical cores), Windows 11 Pro 24H2
(build 26100.8875), .NET runtime 10.0.8 and SDK 10.0.204. Before the first run, a 10-second sample
showed 4.0% average CPU use, 5.3% peak CPU use, and effectively no disk activity. Editor, browser,
chat, and agent processes remained open but quiescent.

The sweep uses N = 100,000 keys, 8 lookup threads, and 500k random lookups. Kvasar uses AES-256-GCM
(encrypted, like SQLCipher). Higher is better except the millisecond and microsecond columns.

The harness uses `KvasarDurability.Flushed` by default and calls `Flush()` at the end. SQLite runs
`wal_checkpoint(TRUNCATE)`, so the default comparison is durability-matched. `--durability Buffered`
changes only Kvasar: it makes no `FlushToDisk` call, while SQLite still checkpoints. The Buffered
results below isolate the cost of the durability match; they are not the headline comparison.

These results include the round-2 correctness fixes. Opening Kvasar now authenticates the candidate
generation's commit-window pages and verifies an HMAC over the `.kidx` prefix. That work makes bare
`Open` more expensive, but closes authentication gaps. `GetMany` now hashes and probes each key once.

Each sweep cell is the per-cell median of two complete invocations for 128 B and 1 KB. The disk-bound
4 KB configuration was run three times at each Kvasar page size. The SQLCipher 4 KB row is the median
of all six samples because Kvasar's page-size argument does not affect SQLite.

### Value = 128 B (12.8 MB — fits the page cache, like the ~25 MB hot set)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **491.0** | **18.7** | 38.2 | 141.0 | **8,964.4** | **0.8** | **1.4** |
| Kvasar (no-enc) | 674.3 | 18.6 | 16.0 | 99.9 | 10,884.8 | 0.5 | 2.4 |
| SQLCipher | 126.6 | 26.5 | **1.4** | 141.1 | 120.0 | 66.3 | 123.5 |

### Value = 1 KB (102 MB — exceeds the 64 MB cache)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| **Kvasar (AES-GCM)** | **241.7** | **137.1** | 16.6 | **167.6** | **311.2** | **12.2** | **79.3** |
| Kvasar (no-enc) | 338.8 | 136.6 | 13.0 | 96.7 | 362.5 | 9.7 | 70.0 |
| SQLCipher | 45.0 | 143.8 | **1.7** | 746.7 | 105.1 | 69.0 | 146.8 |

### Value = 4 KB (410 MB — far exceeds cache; value ≥ the default page size)
| Engine | Write k/s | File MB | Open ms | Startup ms* | Lookup k/s | p50 µs | p99 µs |
|---|--:|--:|--:|--:|--:|--:|--:|
| Kvasar (AES-GCM), 4 KB pages | 65.0 | 822.4 | 13.3 | 638.0 | 113.9 | 39.9 | 137.7 |
| **Kvasar (AES-GCM), 16 KB pages** | **135.6** | 546.9 | 37.5 | **337.0** | **132.9** | **29.3** | **132.8** |
| Kvasar (no-enc), 16 KB pages | 158.3 | 546.4 | 14.7 | 257.4 | 135.7 | 26.8 | 132.1 |
| SQLCipher | 17.6 | **467.8** | **1.8** | 2,478.0 | 74.3 | 101.2 | 184.6 |

\* Startup ms = open + full `ListAllEntries`/`Scan` (the client cache's launch-time hydration).
Open ms = just reopening the store (Kvasar: load `.kidx` + seed accounting; SQLite: open connection).

### The measured cost of `Flushed`

The table below compares the shipping AES-GCM path. SQLCipher is omitted because `--durability`
does not change it. Buffered improves writes materially once values exceed the page-cache-sized case.

| Value size | Flushed write k/s | Buffered write k/s | Buffered gain |
|---|--:|--:|--:|
| 128 B | 491.0 | 531.9 | 8% |
| 1 KB | 241.7 | 310.2 | 28% |
| 4 KB, 4 KB pages | 65.0 | 106.4 | 64% |

Data format 3 adds the `.kvs` durability barrier that makes the commit record itself survive under
`Flushed`. A focused 2026-07-27 measurement used 250 AES-GCM commits per sample, five samples,
`FlushDelay = 0`, 4 KiB pages, and 128-byte values. The same production store was run through a
backend wrapper that suppressed only `.kvs` `FlushToDisk`, reproducing the former data-only barrier:

| Per-commit barriers | Median time |
|---|---:|
| `.kdat` + `.kvs` | 1,644.2 µs |
| `.kdat` only | 861.5 µs |
| **Added `.kvs` barrier** | **782.7 µs** |

The five added-barrier samples imply about 0.78 ms per commit on this Windows machine. This cost is
why `Buffered` remains the library default; the benchmark CLI deliberately defaults to `Flushed` for
its durability-matched SQLCipher comparison.

The 4 KB write rows are unstable: AES-GCM ranged from 40.7–70.4 k/s at 4 KB pages and
75.8–143.3 k/s at 16 KB pages. Startup at 16 KB pages was stable at 333.8–338.4 ms, but the default
page-size startup ranged from 591.3–975.4 ms. AES-GCM lookup spread was 8% at 128 B, 1% at 1 KB,
26% at 4 KB pages, and 6% at 16 KB pages. The no-encryption 128 B lookup was also unstable at
8,622.9–13,146.7 k/s. Treat the 4 KB write and default-page startup figures as order-of-magnitude
results; the page-size direction is repeatable, but its exact percentage is not.

## Takeaways

Kvasar wins every metric at every value size except bare `Open` and on-disk size at 4 KB. Startup
hydration at 128 B is a tie within the run-to-run spread.

- **Point reads — Kvasar wins decisively.** ~**75×** faster when the hot data fits the page cache
  (the target ~25 MB scenario), narrowing to 3.0× at 1 KB and 1.8× at 4 KB with 16 KB pages. Tail
  latency is dramatically better (p99 1.4–133 µs vs SQLCipher's 124–185 µs) — one hash probe + one
  cached page decrypt + zero-copy slice vs. a B-tree descent through the SQLite VM.
- **Batched writes — Kvasar wins 3.9–7.7×** at a page size suited to the value size under the
  durability-matched default. The 4 KB write magnitude is unstable, but every 16 KB-page sample beat
  every 4 KB-page sample.
- **Startup hydration — tied at 128 B, 4.5× faster at 1 KB, and 7.4× at 4 KB with 16 KB pages.**
  SQLite defers work that Kvasar does eagerly; at a small dataset there is not enough deferred work
  for Kvasar's eager index load to repay its more expensive authenticated open.
- **On-disk size.** Smaller than SQLCipher for small values. At 4 KB with 4 KB pages it is ~1.75×
  larger, because a value ≥ the page can't stay single-page and rounds up to a multi-page run;
  **16 KB pages cut that to 546.9 MB (−34%)**, 17% larger than SQLite's 467.8 MB.

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

The harness drains its `LazyWriter`, configures Kvasar with `KvasarDurability.Flushed`, calls
`store.Flush()`, and runs `wal_checkpoint(TRUNCATE)` for SQLite. Each cell below is the median of
three invocation medians, with five cold starts per invocation. `min–max` is the envelope of all
15 cold starts. Lower is better everywhere. Machine details are the same as the sweep above.

### Config A — 12 MB dataset, 1,000 reads (500 tiles + 500 misc)

| Engine | Stack | MB | Total ms | min–max | Open | Read | Flush | Reads | keys/op | Hit | Writes | p50 µs | p99 µs |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| SQLCipher | BatchingKvas | 15.6 | **54.0** | 48.0–69.9 | 1.7 | 39.9 | 9.2 | 375 | 2.67 | 0.0% | 2 | 249.9 | 674.5 |
| **Kvasar AES-GCM** | **plain** | 14.6 | **7.7** | 7.3–10.3 | 3.4 | 3.0 | 1.4 | 1000 | 1.00 | — | 111 | 5.4 | 85.6 |
| Kvasar no-enc | plain | 14.6 | 7.5 | 6.5–8.1 | 3.1 | 2.9 | 1.4 | 1000 | 1.00 | — | 111 | 5.0 | 84.8 |
| Kvasar AES-GCM | BatchingKvas | 14.6 | 9.2 | 8.6–10.9 | 3.3 | 4.3 | 1.8 | 716 | 1.40 | 0.0% | 2 | 24.9 | 180.7 |

### Config B — 25 MB dataset, 2,000 reads (1,000 tiles + 1,000 misc)

| Engine | Stack | MB | Total ms | min–max | Open | Read | Flush | Reads | keys/op | Hit | Writes | p50 µs | p99 µs |
|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|
| SQLCipher | BatchingKvas | 32.4 | 123.9 | 113.9–146.5 | 1.4 | 108.8 | 15.4 | 750 | 2.67 | 0.0% | 3 | 344.1 | 741.2 |
| **Kvasar AES-GCM** | **plain** | 30.5 | **11.3** | 10.2–17.7 | 3.9 | 5.5 | 1.6 | 2000 | 1.00 | — | 182 | 4.3 | 84.6 |
| Kvasar no-enc | plain | 30.5 | 10.9 | 10.3–11.9 | 4.3 | 4.9 | 1.6 | 2000 | 1.00 | — | 182 | 2.0 | 75.8 |
| Kvasar AES-GCM | BatchingKvas | 30.5 | 14.0 | 12.3–17.1 | 3.6 | 7.6 | 1.7 | 1488 | 1.34 | 0.0% | 3 | 22.6 | 144.2 |

### Durability cost in the chat scenario

This isolates the shipping AES-GCM/plain row. Buffered skips Kvasar's disk flush; SQLCipher remains
checkpointed and is therefore not repeated in the comparison.

| Config | Flushed total | Buffered total | Flushed flush | Buffered flush | Total cost of matching durability |
|---|--:|--:|--:|--:|--:|
| A · 12 MB | 7.7 ms | 7.2 ms | 1.4 ms | 0.3 ms | 0.5 ms (7%) |
| B · 25 MB | 11.3 ms | 10.0 ms | 1.6 ms | 0.4 ms | 1.3 ms (13%) |

The three Flushed invocation medians varied from 7.7–8.2 ms for Kvasar plain in config A and
10.9–11.5 ms in config B. SQLCipher varied from 50.5–54.1 ms and 122.6–130.2 ms. Individual cold
starts had wider tails, which the table envelopes retain. Differences under about 1 ms on Kvasar
rows are noise; the AES-GCM and no-encryption totals overlap in both configurations.

This scenario remains an important `GetMany` regression guard. The batching layer averages only
1.34–1.40 keys per backend call here, so the prefetch path must remain gated for small batches. The
sweep always issues full 64-key batches and cannot expose that behavior.

### Takeaways

- **Cold start is 7.0× faster at 12 MB (54.0 → 7.7 ms) and 11.0× at 25 MB
  (123.9 → 11.3 ms).** Every phase but `Open` improves: the read burst is 39.9 → 3.0 ms and
  108.8 → 5.5 ms, while the durability-matched flush is 9.2 → 1.4 ms and 15.4 → 1.6 ms.
  Bare `Open` is slower because Kvasar authenticates and loads its index up front.
- **The gap widens with load, because only SQLCipher scales with it.** Per read, SQLCipher costs
  39.9 µs (A) and 54.4 µs (B); Kvasar costs 3.0 µs and 2.8 µs. Doubling the dataset and the burst
  costs SQLCipher 129% and Kvasar 47%.
- **Kvasar's cold start is dominated by fixed cost, not by data.** Halving the workload only takes it
  from 11.3 to 7.7 ms (−32%, not −50%), because `Open` barely moves; the read burst is the only part
  that halves.
- **Kvasar needs no layer above it.** `FlushDelay` absorbs what `LazyWriter` was for, and the read
  path is overhead: the same store behind `BatchingKvas` costs 9.2 vs 7.7 ms (A) and 14.0 vs 11.3 ms
  (B) — despite issuing *fewer* backend calls (716 vs 1,000; 1,488 vs 2,000). A channel hop plus a
  `TaskCompletionSource` per key plus worker dispatch costs more than the read it amortizes, and
  Kvasar's page cache already makes the 256-entry LRU redundant.
- **Unbatched writes are no longer a problem.** Plain Kvasar issues 111/182 single-record `Set`s
  where the harness issues 2–3 `SetMany`s: with `FlushDelay > 0` a `Set` appends into the unsealed
  tail instead of sealing a page per record.
- **Encryption is free on this workload** — AES-GCM and no-enc land within the run-to-run spread of
  each other in both configurations.
- **Read batching barely engages at this concurrency** — 1.34–1.40 keys/call for Kvasar vs 2.67
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
| Write k/s | 65.0 | **135.6** (+109%) |
| File MB | 822.4 | **546.9** (−34%) |
| Startup ms | 638.0 | **337.0** (−47%) |
| Lookup k/s | 113.9 | **132.9** (+17%) |
| p50 µs | 39.9 | **29.3** (−27%) |

Rule of thumb: **`PageSize` should comfortably exceed your typical value size**, so values stay
single-page (zero-copy reads, no multi-page run) and each async I/O carries more payload. The write
percentage uses the medians above; its exact magnitude is unstable, as the reported ranges show.

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
