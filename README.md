# ActualLab.Kvasar

**Kvasar** is a small, embedded, **encrypted key-value store for .NET** — pure managed, with
**zero native dependencies**. It follows the *fastest-path Bitcask model*: an in-RAM hash index over
an append-only, encrypted, paged log.

It exists to replace SQLite + SQLCipher as the on-device persistence engine for ActualChat's
client-side caches. Two reasons:

1. **No native library treadmill.** SQLCipher is a native dependency, and native dependencies mean
   Google Play's 16 KB page-size mandate, NDK upgrades, alignment rules, and macOS notarization —
   forever. Kvasar is managed code plus one BCL-adjacent NuGet package.
2. **It's much faster for this workload.** A client cache is read-dominant with batched writes, which
   is exactly where a B-tree + SQL VM is the wrong shape. See the numbers below.

Kvasar is *not* a database. It has no queries, no transactions, no secondary indexes, and no
cross-key atomicity — just per-key `Get`/`Set` over binary keys and values. That narrowness is what
buys the speed.

## Performance vs SQLite + SQLCipher

Measured against a faithful replica of ActualChat's `SQLiteBatchingKvasBackend` (encrypted, WAL,
`synchronous=normal`, one connection per reader thread). 100k keys, 50-byte keys, 8 reader threads,
Kvasar with AES-256-GCM so both sides are encrypted. Full methodology and raw tables in
[`docs/BENCHMARKS.md`](docs/BENCHMARKS.md).

| Value size | Batched writes | Startup hydration | Point reads | Read p99 |
|---|--:|--:|--:|--:|
| 128 B (fits cache — the target scenario) | **3.0×** | **1.2×** | **73×** | 1.3 µs vs 117 µs |
| 1 KB | **4.3×** | **4.0×** | **3.7×** | 43 µs vs 141 µs |
| 4 KB (16 KB pages) | **6.1×** | **7.8×** | **2.3×** | 64 µs vs 172 µs |

A warm read is one hash probe, one already-decrypted page, and a zero-copy slice — roughly **0.7 µs**,
versus a B-tree descent through the SQLite VM. Where SQLite wins: opening a connection is trivially
cheap (though Kvasar still wins *startup*, which is open + full hydration), and SQLite's file is
~20% smaller for 4 KB values.

## How it works

Three layers, each independently testable:

- **Paging** (`Paging/`) — the `.klog` segment files: a plaintext 64-byte header followed by
  AES-256-GCM encrypted pages, with a sharded byte-budgeted LRU cache of decrypted pages. Reads are
  zero-copy slices into those cached, immutable pages.
- **Log** (`Internal/Log/`) — records appended to the active segment; old segments are compacted by
  segment GC. Values that fit a page never span one, which is what keeps reads zero-copy.
- **Index** (`Index/`) — an in-RAM open-addressing map from a keyed 64-bit key hash to a record
  locator. Keys live on disk, not in the index, so index memory is **independent of key length**
  (~16–24 B/entry). Persisted to `.kidx` as a checkpoint plus a delta tail, so opening is O(index)
  rather than O(data).

**Concurrency:** lock-free readers, single writer. The writer never blocks readers — it publishes a
record by release-writing a 64-bit locator that readers acquire-read, and it always seals a page
before publishing a locator into it, so readers only ever see immutable pages.

**Async throughout.** SQLite is synchronous for historical reasons; Kvasar is not. All disk I/O is
positional and async, with a `CancellationToken` on every path. It returns `ValueTask`, and `Get` is
deliberately *not* an `async` method — a cache hit completes synchronously with no state machine, no
allocation and no thread hop.

## Durability and crash recovery

`Set` returns only after the record's bytes have reached the OS, so anything acknowledged survives a
process kill. The `.kidx` index is a lazily-written *hint*, never the source of truth: on open, the
store loads the checkpoint and replays the log past its high-water mark, so a stale or torn index
costs a little startup time, never data.

Everything else degrades to wipe-and-recreate rather than an exception, because the store backs a
*regenerable* cache: a wrong key, a format-version mismatch, or unreadable state discards the files
and starts clean. A torn trailing page from an interrupted write is truncated away, and that segment
is never appended to again (its page nonces must not be reused).

This is tested by killing a real child process mid-write at randomized points and asserting every
acknowledged record comes back — see `tests/.../Store/ProcessCrashRecoveryTests.cs` — plus randomized
fault injection over truncated logs, torn index tails, and leftover temp files.

## Security model

AES-256-GCM per page under a caller-supplied 32-byte master key, with per-store subkeys derived via
HKDF-SHA256 and a fresh random salt per segment file. Every page is authenticated, so tampering is
detected on read. Keys are hashed with keyed SipHash-2-4 so the `.kidx` leaks nothing about them.

What it does **not** defend against: an attacker who can replace a whole segment file with an older
copy of itself (nothing binds a segment to the store — see the limitations in
[`docs/DESIGN.md`](docs/DESIGN.md)), or anyone who has the master key.

## API

```csharp
await using var store = await KvasarStore.Open(new KvasarOptions {
    BasePath = "/path/to/cache/CCC",
    EncryptionKey = key32, // 32 bytes
});

await store.Set(key, value);                       // value == null => delete
var value = await store.Get(key);                  // ReadOnlyMemory<byte>? — null = miss
await foreach (var (k, v) in store.Scan()) { … }   // enumerate all (unordered)
await store.Flush();
```

Keys and values are binary (`ReadOnlyMemory<byte>`); string and typed conversions belong in the
caller. `KvasarOptions` also exposes `PageSize`, `PageCacheBytes`, compaction thresholds, and
pluggable hasher/KDF — see [`docs/SPEC.md`](docs/SPEC.md) §4.

**Tune `PageSize` to your value size.** It is the highest-leverage knob: values larger than a page
can't stay single-page, which costs both space and I/O. Moving 4 KB values from 4 KB to 16 KB pages
is +56% writes, −31% file size, and −51% startup.

## Status

Working and tested — not yet released. The library multi-targets **net10.0** (default) and
**net9.0**, is AOT- and trimming-safe, and depends on exactly one package (`System.IO.Hashing`).

Known limitations are tracked honestly in [`docs/DESIGN.md`](docs/DESIGN.md), including two worth
knowing up front: two distinct keys sharing a full 64-bit hash collapse to one entry (~2⁻⁶⁴ under the
default keyed hasher; it never returns wrong data, and a regenerable cache self-heals), and an
in-flight read racing compaction can return a spurious miss.

## Building

```bash
Build.cmd                          # or: dotnet build ActualLab.Kvasar.slnx -c Release
Run-Tests.cmd                      # full suite
Run-Benchmarks.cmd                 # vs SQLCipher; run on an idle machine
Build.cmd -p:UseMultitargeting=true # validate net10.0 and net9.0
```

## Layout

```
src/ActualLab.Kvasar/          Library (one dependency: System.IO.Hashing)
  Crypto/                      AES-GCM page cipher, SipHash-2-4, HKDF-SHA256
  Paging/                      Encrypted paged segments + LRU page cache
  Internal/Log/                Append-only record log, segments, compaction
  Index/                       In-RAM hash index + .kidx persistence
tests/ActualLab.Kvasar.Tests/  Unit, property, concurrency, fuzz, crash-recovery
tools/ActualLab.Kvasar.CrashWorker/  Child process killed mid-write by the crash tests
benchmarks/                    Kvasar vs sqlite-net-sqlcipher
docs/                          SPEC.md (product spec), DESIGN.md (internals), BENCHMARKS.md
```

## License

MIT — see [LICENSE](LICENSE).
