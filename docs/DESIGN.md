# Kvasar — internal design & module contracts

This document freezes the internal architecture so modules can be built independently and compose
without rework. Read it together with `docs/SPEC.md` (the product spec). Where this doc and SPEC
disagree on an internal detail, **this doc wins** (SPEC wins on externally-visible behavior).

Targets: **net10.0** by default and **net10.0;net9.0** with `UseMultitargeting=true`, pure managed,
no native deps. Namespace root `ActualLab.Kvasar`.
Coding style: follow the ActualLab conventions (file-scoped namespaces, `var`, expression-bodied
members, mixed brace style — Allman for types/methods/ctors, K&R everywhere else, 4-space indent,
120-col lines, LF, minimal comments, no `Async` suffix). Private fields `_camelCase`; private
static readonly / const `PascalCase`.

## Already-written contracts (do NOT modify these files)
- `KvasarOptions`, `KvasarStats`, `IndexEncryption`, `KvasarValueKind`, `KvasarCorruptException`
- `KvasarKey`, `KvasarValue` (+ `KvasarKeyExt`/`KvasarValueExt`) — the public key/value structs;
  they wrap `ReadOnlyMemory<byte>` and carry the conversions, so the internals below stay on raw memory
- `Crypto/IKeyHasher`, `Crypto/IKeyDerivation`, `Crypto/IPageCipher` (+ `IPageCipherFactory`)
- `KvasarConstants`, `Internal/Locator`, `Internal/IndexEntry`, `Internal/RecordFlags`, `Internal/Varint`

## Key invariants / semantics (read carefully)

### Offsets & the logical page stream
A data file `<base>.N.kdat` on disk = a fixed **64-byte plaintext header**
(`KvasarConstants.SegmentHeaderSize`) followed by a sequence of **encrypted pages**, each
`PageSize + cipher.Overhead` bytes on disk.

Each decrypted data page begins with the 8-byte authenticated frame described under M3. Upper
layers work in the logical stream formed by concatenating the remaining
`PagePayloadSize = PageSize - 8` record-data areas: offset `O` means page
`pageId = O / PagePayloadSize`, in-page offset `O % PagePayloadSize`. The physical mapping (past the
header, times `PageSize+Overhead`) is **private to the paging layer**. `Locator.Offset` is such a
logical offset. This keeps records independent of the on-disk header size and of `Overhead`.

The data-format-3 superblock `<base>.kvs` is exactly 1536 bytes:
```
offset  size  field
0       36    magic, format tag, KCV nonce and KCV tag
36      476   reserved, zero
512     512   authenticated commit slot 0
1024    512   authenticated commit slot 1
```
Slot contents are unchanged from data format 2. The 512-byte header sector separates the two slot
sectors, so one torn sector cannot damage both.

### Zero-copy read path
Decrypted pages are cached as immutable `byte[]`. A value that fits within a single page is returned
as a `ReadOnlyMemory<byte>` **slice into the cached page buffer** — no copy. Cache eviction just
drops the reference; a `ReadOnlyMemory` a caller holds keeps the buffer alive (pure GC lifetime).
Never mutate a decrypted page buffer in place after it is published to the cache.

### Concurrency
Multi-reader, single-writer. Readers never lock and never block on the writer. The single writer
serializes all mutations. Publication of an index slot is the linearization point: write record
bytes to a sealed/appended page first, then publish the locator with release semantics.

### Cancellation (writes are uninterruptible)
**Read paths take a `CancellationToken`; write paths take none** — `AppendPage`, `SealTail`,
`Flush`, slot recycling, delta append and checkpoint write all run to
completion. Cancelling a write is not a safe operation on a self-describing append-only log: a
half-written multi-page record leaves a header claiming more bytes than exist, so recovery reads
the records appended after it as that record's tail, and a torn entry in the buffered `.kidx` delta
stream shifts every entry behind it into a garbage locator. `KvasarStore`'s public write methods
stay cancellable for the *caller* by awaiting an uncancellable `*Locked` body through
`Task.WaitAsync(cancellationToken)`: the token abandons the wait, the body still finishes and
releases the write lock. Compaction's copy pass is the one exception — it only adds records, so
stopping it early leaves reclaimable dead bytes, never a dangling locator.

## Module APIs to implement

Each module lives under `src/ActualLab.Kvasar/<Folder>/`. Implement exactly these public surfaces
(you may add `internal` helpers in the same folder). All types are in namespace
`ActualLab.Kvasar.Crypto` (crypto) or `ActualLab.Kvasar.Internal` (everything else) unless noted.

### M1 — Crypto (`Crypto/`)
Implements the three crypto interfaces + factories + default singletons.
- `SipHash24Hasher : IKeyHasher` — SipHash-2-4. `IsKeyed=true`, `SecretSize=16`. `Hash(key, secret)`
  uses the 16-byte `secret` as the SipHash key. Pure managed, correct SipHash-2-4.
- `XxHash3Hasher : IKeyHasher` — wraps `System.IO.Hashing.XxHash3`. `IsKeyed=false`, `SecretSize=0`.
- `KeyHashers` (static) — `SipHash24` and `XxHash3` singleton instances.
- `HkdfSha256KeyDerivation : IKeyDerivation` — HKDF-SHA256 via `System.Security.Cryptography.HKDF`.
- `KeyDerivations` (static) — `HkdfSha256` singleton.
- `AesGcmPageCipher : IPageCipher` + `AesGcmPageCipherFactory : IPageCipherFactory`.
  - Factory holds the 32-byte page key and `formatVer` (uint). `Overhead = GcmTagSize (16)`.
  - `Create(fileSalt)` derives a per-file nonce base and returns a cipher bound to it.
  - Per page: **deterministic** 12-byte nonce = `f(pageId, fileSalt)` (e.g. HKDF/SHA over
    salt+pageId, or AES-CTR of pageId) — MUST be unique per (fileSalt, pageId) and never stored.
    AAD = `pageId(8 bytes LE) || formatVer(4 bytes LE)`. On-disk page = ciphertext(PageSize) then
    16-byte tag. Decrypt throws `KvasarCorruptException` on tag mismatch (tamper/wrong key).
  - Thread-safety: `Encrypt` is writer-only (single-threaded). `Decrypt` is called concurrently by
    readers — must be safe (create/rent per-call `AesGcm` or guard appropriately; `AesGcm` instances
    are not thread-safe for concurrent use).
- `NoopPageCipher : IPageCipher` + `NoopPageCipherFactory` — `Overhead=0`, copy plain↔onDisk. Used
  when `KvasarOptions.DisableEncryption` is set (tests/benchmarks).

### M2 — Paging / Layer 1 (`Paging/`)
- `SegmentHeader` formats and validates the 64-byte plaintext header: magic, data format, page size,
  file salt, and flags. Data format 3 defines flag bit 0 as AES-GCM pages; every other bit is zero.
  A requested encryption mode that disagrees with this flag is a configuration error, not corruption.
- `PageCache` stores immutable decrypted `PageSize`-byte pages by the store-assigned
  `(fileId, pageId)` incarnation key.
- `PagedFile` owns one fixed `.N.kdat` slot. Its physical page mapping is
  `64 + pageId * (PageSize + cipher.Overhead)`. It uses positional asynchronous I/O, stages encrypted
  writes, exposes a zero-allocation cache-hit read, and rounds a torn physical tail upward so a nonce
  can never be reused.
- Recycling a slot truncates it to the header, chooses a new salt and cache incarnation, and resets
  its page sequence. No valid superblock candidate may still reference the recycled incarnation.

### M3 — Record log / Layer 2 (`Log/`)
Owns the record format, authenticated page framing, and two fixed data slots.

Record bytes are unchanged:
`recordLen: varint (bytes after this field)`, `flags: u8`, `valueKind: u8`, `keyLen: varint`,
`key: keyLen bytes`, `value: rest`. A tombstone has no value.

Each decrypted `PageSize`-byte data page has this exact format:
```
offset  size  field
0       1     flags: bit 0 = continuation, bit 1 = hasRecordStart
1       3     reserved, all zero
4       4     firstRecordOffset: signed little-endian offset in recordData, or -1
8       P-8   recordData
```
No other flag bits are valid. `hasRecordStart` is equivalent to `firstRecordOffset >= 0`.
A page without `continuation` must have its first record at offset 0. A continuation page may have
no new record or may name its first new record at an offset greater than zero. Because the frame is
part of the encrypted plaintext, AES-GCM authenticates it and caller bytes cannot occupy it.

`DataLog` exposes `PagePayloadSize = PageSize - 8`; all locators and record-length arithmetic use
that logical payload geometry. A single-page record is still returned as a zero-copy slice of a cached
page. Multi-page records are split only across record-data areas. The mutable tail tracks the same frame
state that will be sealed with it.

Recovery walks each page once. It yields the known record starts from the frame and, after an unreadable
header page, resumes only at a later authenticated `firstRecordOffset`. Continuation data is never
decoded as a possible header. There is no tiling grammar and no candidate probing.

### M4 — Hash index / Layer 3 (`Index/`)
In-RAM open-addressing table; lock-free readers, single writer.

Each slot stores a full `KeyHash`, a `KeyId`, a packed locator, and record length. `KeyId == 0` is
invalid. New logical key incarnations receive `_nextKeyId++`; overwrite and compaction relocation
preserve the existing id. Delete followed by reinsertion mints a new id. The next counter value is
stored in every superblock state, and recovery also raises it above every id loaded from the adopted
index. Exhaustion fails instead of wrapping.

Readers probe by hash and use `(KeyHash, KeyId)` plus the full on-disk key where a caller key exists.
Publication remains a release write of the packed locator; readers take a single table snapshot and
acquire-read the locator. Resize is copy-on-write.

### M5 — Index persistence `.kidx` (`Index/`)
The index layout is independently versioned; it is at **4**, which introduced the block-chained commit
MAC below. Data format 3 does not change it. Each persisted `IndexEntry` is exactly 32 bytes:
```
offset  size  field
0       8     KeyHash
8       8     PackedLocator
16      8     KeyId
24      4     record Length
28      1     record Flags
29      3     zero padding
```
The 64-byte index header carries magic, data format, index layout version, entry count, data commit
length, and two generation-parity HMAC slots. A checkpoint is followed by fixed-size deltas.
Invalid, stale, absent, or unauthenticated index data is discarded and rebuilt from `.kdat`.

`IndexMac` computes those tags as a **block-chained HMAC-SHA256** (`Index/IndexMac.cs`): the body — the
checkpoint region plus the committed delta tail — is cut into fixed 16 KiB blocks, and each block's step
is a one-shot HMAC over a 48-byte typed prefix (step kind, payload length, block index, previous chain
value) followed by that block's bytes. The commit tag is a third step kind over the chain value and the
open trailing block, truncated to 16 bytes. Three properties fall out of that shape:

- **Committing is bounded work.** A tag covers at most one block, so commit cost stops scaling with the
  index — the whole point, since commits run under the store's single write lock. Load still costs one
  pass over the prefix, paying only the per-block HMAC restart.
- **No running digest is ever peeked.** Android's `Mac` cannot be cloned, so `IncrementalHash`'s
  `GetCurrentHash` — a "hash so far without ending the computation" — is not portable there. Every step
  here is one-shot, which is also the fastest path on every other platform.
- **Per-entry work stays managed.** Appending a delta memcpy's 32 bytes into the open block; the crypto
  call happens once per 16 KiB. On Android that turns one JNI round-trip per cached key into one per
  ~500 keys.

The verifier is the writer: `IsMacValid` builds an `IndexMac` and runs the same `Append` over the whole
committed prefix, so the two paths cannot drift apart. Each tag binds the authentication context (the
active `.kdat` incarnation's `fileSalt`), the stable header fields, block position, and the exact
committed length — a truncated, extended, reordered or edited prefix fails.

## Integration points (owned by KvasarStore, not these modules)
- Deriving page key + hash key from the master key via `IKeyDerivation` and the `KvasarConstants`
  info labels; building `AesGcmPageCipherFactory`.
- Resolving option defaults (`Hasher ??= KeyHashers.SipHash24`, `Kdf ??= KeyDerivations.HkdfSha256`).
- Lock file, open/lifecycle, Get/GetMany/Set/SetMany/Scan/Clear/Flush/Compact/DisposeAsync, Stats.
- Wiring the background compactor.
- Normal open adopts authenticated superblock generations only. If none is adoptable, the last-resort
  path proves the key with a current-format `.kdat` page, adopts the longest contiguous authenticating
  prefix, rebuilds the index, rewrites the superblock, and records `KvasarStats.FallbackRecoveries`.
  A wrong key or caller-requested page/encryption geometry mismatch must escape without reaching wipe.

## Async I/O model

SQLite is synchronous for historical reasons; Kvasar deliberately is not. Every storage path is async
and cancellable, and the shape of that conversion is load-bearing:

- **`ValueTask` everywhere, not `Task`.** An `async ValueTask` method that completes without suspending
  never heap-allocates its state machine, so the warm path stays allocation-free.
- **Genuine sync fast paths.** `KvasarStore.Get` and `PagedFile.GetPage` are *not* `async` methods:
  they resolve a cache-resident, single-page record inline and return an already-completed `ValueTask`
  — no state machine, no allocation, no thread hop. Only a cache miss or a page-spanning record falls
  through to the awaited path (`GetSlow` / `ReadAndCache`). This matters because the cache-hit read is
  Kvasar's headline win over SQLCipher; paying state-machine cost on it would tax the hot path.
  `DataLog.TryReadRecordCached` is the cache-only probe underneath, and it is *conservative*: it
  returns false rather than a wrong answer, so the async path is always a correct fallback.
- **The writer lock is a `SemaphoreSlim(1, 1)`, not `lock`.** A `Monitor` can't be held across `await`,
  and the write path now awaits real I/O. Single-writer semantics are unchanged.
- **Async factories.** Constructors can't await, so `KvasarStore.Open`, `SegmentSet.Create`, and
  `PagedSegment.Create`/`Open` are static async factories.
- **No `out` parameters on async methods.** `TryReadRecord` returns `RecordRead` and `IndexFile.TryLoad`
  returns `IndexCheckpoint?` instead. The sync cache-only probes keep their `out` form.
- **`Span` → `Memory` at async boundaries.** Ref structs can't cross an `await`; spans are still used
  freely *within* a non-suspending region (e.g. encrypt/decrypt after the last await).
- **`fsync` is the one unavoidable blocking call.** .NET has no async `FlushToDisk`, so it is offloaded
  off the caller's thread rather than run inline.
- **`Stats` stays synchronous** — a best-effort, lock-free snapshot; the numbers are advisory
  (compaction/diagnostics) and never used for correctness.

### Deferred flush (`KvasarOptions.FlushDelay`, default 0.5 s)

This absorbs the one part of ActualChat's `BatchingKvas` worth keeping — its `LazyWriter` — and does it
better, because the store can delay *durability* without delaying *visibility*:

- `Set` appends and publishes to the in-RAM index synchronously, so the value is readable by every
  reader the instant it returns. Only sealing and the disk write are deferred, by up to `FlushDelay`.
  (A `LazyWriter` above the store can't manage this: it delays the write itself, so a key evicted from
  its 256-entry cache before the flush reads back *stale*.)
- The accepted failure mode is exactly and only this: **a crash loses writes newer than the last
  flush.** Anything already flushed survives, and the store always reopens consistent.
- Deferring the *seal* is what makes an unbatched `Set` cheap. Sealing per call pads a whole page per
  record — measured at ~21× amplification (16 MB of data → 340 MB on disk).
- Mandatory flush points that must never be deferred: before `RemoveSegment` in compaction (else records
  copied into the new segment die with the deleted source — real loss, not staleness), `WriteCheckpoint`,
  `Clear`, and `DisposeAsync`.
- Ordering rule: the log must never be *less* durable than the index. `.kidx` may lag freely — it's a
  hint replayed from the HWM — but if it ever led, it would point at records that don't exist.

Two hazards this introduced, both caught by the test suite and worth remembering:

1. **Tail visibility.** Publishing into the unsealed tail means readers slice a buffer the writer is
   still appending to. Individual bytes are stable (the writer only appends past `Fill`, and `SealTail`
   installs a *new* buffer), but `(buffer, fill, pageId)` must be read as one unit — reading them
   separately lets a concurrent seal swap the buffer mid-read, and the reader then slices the fresh
   empty one and reports a miss for a key that exists. Fixed by publishing an immutable `TailSnapshot`
   through a single volatile field. In durable mode this was unreachable, because the seal always
   preceded the publish.
2. **Page-id reuse ⇒ nonce reuse.** A crash can now lose whole unwritten pages, so `PageCount` comes
   back lower and appends would re-encrypt different data under an already-used `(fileSalt, pageId)`
   nonce. Recovery therefore rolls to a fresh segment (new random salt ⇒ disjoint nonce space) whenever
   it can't prove the previous run closed cleanly. A graceful close leaves a `<base>.clean` marker, so
   the common path doesn't strand a segment per launch.

### Amortizing per-I/O cost

Async file I/O has a materially higher fixed cost per operation than a synchronous write into the OS
cache (measured at roughly ~20 µs/page on Windows overlapped I/O — see `docs/BENCHMARKS.md`). Anything
that does one I/O per 4 KiB page therefore pays that cost thousands of times. Two mitigations, both of
which preserve existing semantics:

- **Write-behind** (`PagedSegment.AppendPage`) batches staged pages into one `WriteAsync` per ~1 MiB.
- **Readahead** (`PagedSegment.Prefetch`) pulls ~1 MiB of consecutive pages per read on sequential walks
  (`SegmentSet.ScanFrom` during recovery, and `KvasarStore.Scan`).

`KvasarStore.Scan` additionally sorts the index snapshot by `(SegmentId, Offset)` before reading. The
snapshot is in *hash* order, which makes a full scan a storm of random page faults; scan order is
unspecified by SPEC §4, so walking in log order is free and is what makes readahead effective.

## Deliverables per module
Correct, warning-clean C# against the contracts above, plus focused xUnit tests where the module is
independently testable (crypto vectors, varint round-trip, record codec round-trip, page cache
eviction, hash-index probe/resize, index-file round-trip). Put tests under
`tests/ActualLab.Kvasar.Tests/<Module>/`. Do not modify `.csproj` files or any file outside your
assigned folder(s).

---

## Known limitations & follow-ups (as implemented)

The v1 implementation is functionally complete and passes the full test suite (unit, encryption,
crash/torn-tail recovery, concurrency, and property-based vs. a `Dictionary` oracle). Two conscious
gaps remain, both correctness-neutral for the target scenario:

1. **64-bit hash-collision fan-out (§6.2).** The in-RAM index keys each slot by the full 64-bit
   keyed hash and stores no key bytes (the §6.1 key-length-independent design). Two *distinct* keys
   that share a full 64-bit hash therefore collapse onto one slot — the later write shadows the
   earlier key, which becomes unreachable (a single-key loss). The on-disk full-key verify still
   guarantees a lookup **never returns another key's value** (it returns the right value or `null`),
   so no *wrong* data is ever served, and a regenerable cache self-heals the lost entry on the next
   miss. Probability under the default keyed **SipHash-2-4** is ~2⁻⁶⁴ (negligible at the ~10⁶-entry
   target). It is more reachable only with a non-keyed 64-bit hasher (the built-in `XxHash3`) at very
   large key counts. A full fix (distinct same-hash slots + on-disk key disambiguation on the write
   path, mirrored in `.kidx` load) touches `HashIndex`, `IndexFile`, and the store write path and
   adds a read per colliding overwrite; deferred deliberately. Guarded by the skipped regression test
   `EdgeCaseTests.HashCollisionFanOut_KnownBug`. **Recommendation: keep the default SipHash-2-4.**

2. ~~**Open still decrypts the log.**~~ **Resolved.** Open is now O(index), not O(data): the fast path
   loads `.kidx`, decrypts only the post-checkpoint gap via `SegmentSet.ScanFrom(hwm)` (zero records
   after a graceful close), validates the key with a single-page decrypt, and seeds live/dead
   accounting from the loaded index (`SeedAccountingFromIndex`: LiveBytes = Σ entry lengths per
   segment; DeadBytes = segment gross − live, a slight over-count from varint prefixes + page padding
   that only makes compaction marginally more eager). Measured open dropped from ~2 s to ~20 ms for a
   410 MB / 100k-entry store. The full-log decrypt now happens only on the fallback path (a missing or
   unusable `.kidx`). Wrong-key/tamper is still caught on open by the single-page validation decrypt.

### Fixed by the code review (kept here as a record of the failure modes)

An adversarial review of every module found these; each now has a regression test in
`Store/HardeningTests.cs`, `Paging/PagedSegmentWriteBehindTests.cs`, or the per-module `*ReviewTests.cs`.

- **A checkpoint fired mid-batch permanently lost acknowledged writes** (critical; found by the crash
  fuzzer). `AppendDelta` checkpointed inline once the delta count crossed its threshold, and `SetMany`
  (and compaction) seal the *whole* batch before publishing any of it. A checkpoint taken from inside
  the publish loop therefore stamped an **end-of-batch HWM onto an index containing only the entries
  published so far**. The remaining entries' deltas went to a freshly reopened, never-fsynced buffer,
  and `LoadIndex` replays only `ScanFrom(hwm)` — from the end of the batch — so their records were
  never re-read. The writes were gone for good even though `SetMany` had returned and the `.klog` held
  them. Reproducer: 128 single `Set`s then one 24-key `SetMany` → 23 of 24 keys missing after an abort.
  Compaction had the same shape and was worse, since it also deletes the drained segment. Fixed by
  deferring the checkpoint (`MaybeCheckpoint`) until after the publish/repoint loop, where the index is
  once again consistent with the sealed log.
- **AES-GCM nonce reuse after a torn tail** (critical). `PagedSegment.Open` floors `PageCount`, discarding
  a half-written trailing page, so the next append reused that `pageId` — and the nonce is a pure function
  of `(fileSalt, pageId)`. Two different plaintexts under one `(key, nonce)` leaks their XOR and enables
  the GCM "forbidden attack" against the auth subkey. Fixed: a segment with a torn tail is never appended
  to; recovery starts a fresh segment, which gets a fresh random salt.
- **Unauthenticated `segmentId` bricked the store silently** (critical). The 64-byte header isn't in any
  AAD, so a flipped `segmentId` was adopted verbatim while `SegmentSet` kept keying by filename — every
  later write minted a locator for an unknown segment and read back as a miss, permanently. Fixed:
  `PagedSegment.Open` cross-checks the header id against the filename.
- **Corrupt input threw instead of being rejected**, defeating wipe-and-recreate (§12) and making a store
  permanently un-openable. All from casting an attacker/corruption-controlled `ulong` length to a signed
  type *before* bounds-checking it, so the negative result sailed past every guard into a throwing
  `Slice`: `IndexFile.Parse` (`checkpointCount * EntrySize` wrapping), `RecordCodec.TryParse` (body and
  key lengths), and `SegmentSet.TryReadAt`/`TryReadRecordCached`. All now bound before narrowing.
- **Torn triples in `ProbeCursor.MoveNext`.** The acquire on the locator pinned the *writes that preceded
  it*, not the slot's generation, so a slot recycled mid-read returned key A's locator with key B's hash
  and key C's length — measured at ~10% of writer ops under contention (412k mismatches in 10 s). Impact
  was masked by the on-disk full-key verify (a false miss, never wrong data), but the documented invariant
  was false and the obvious "skip the verify, collisions are 2⁻⁶⁴" optimization would have turned it into
  silent wrong data. Fixed with seqlock validation: re-read the locator after the parallel fields.
- **Unkillable spins.** `HashIndex.CeilPow2`/`CapacityFor` and `PageCache.RoundUpPow2` doubled an `int`
  past 2³⁰ to `int.MinValue` then `0`, leaving their loop conditions permanently true. Now bounded.
- **A sentinel locator could orphan a probe run.** `Locator.None` packs to the empty-slot sentinel; the
  only guard was a `Debug.Assert`, stripped in Release. `Set` now throws, and `BulkLoad`/`Apply` skip such
  entries (they come straight from the unvalidated `.kidx`, where a zero-filled tail decodes as packed 0).
- **Lifecycle.** `DisposeAsync` could leak the store lock for the process lifetime if the buffered `.kidx`
  dispose threw (disk full), because `_disposed` was already set so a retry no-oped — now a `finally`.
  Wipe-and-recreate also ran with the lock *released*, letting another process create a store we then
  deleted (on Unix the unlink succeeds silently) — the lock is now held across the wipe.
- Minor: `PageCache.Add` published into the LRU before the map (an OOM there drove the byte counter
  negative, permanently disabling eviction); `AesGcmPageCipher.Decrypt` only wrapped
  `AuthenticationTagMismatchException`, letting other `CryptographicException`s escape to the app;
  `Varint` accepted overlong 10-byte encodings; `TryReadExistingHeader` probed only `.001.klog`, so page
  size was mis-adopted after compaction deleted segment 1.

### Known limitations that remain

> **Stale below this line.** The storage layer described in this document — segments, `SegmentSet`,
> `PagedSegment`, the v1 `IndexFile`, the `.clean` marker — has been replaced by the model in
> [`DESIGN-Durability.md`](DESIGN-Durability.md): a superblock over two `.kdat` and two `.kidx` slots.
> Several limitations below are therefore gone with the code that caused them (notably 3 and 5, both
> segment-lifecycle artefacts). Read the durability design first; treat this file as the v1 record.

0. **Apple `F_FULLFSYNC` and the directory-entry gap — both resolved, and neither the way this file
   expected.** `SystemNative_FSync` *does* use `fcntl(F_FULLFSYNC)`, but only under `TARGET_OSX`,
   which is desktop macOS alone — iOS, tvOS and Mac Catalyst get plain `fsync`
   ([`DESIGN-Durability.md`](DESIGN-Durability.md) §7). It no longer matters: the v2 design buys
   *atomicity*, not durability, so nothing depends on reaching the medium (§1, §6c). The directory
   gap is likewise gone by construction rather than by `fsync` — all files are created once at store
   creation and never renamed or unlinked while open, so no dirent is ever in the durability path,
   and the worst case of losing one is "the store looks uninitialized", which is the accepted
   wipe-and-rebuild path, never a torn state (§3.4).

3. **Segment-file substitution / rollback.** Nothing binds a `.klog` to the store or to its own filename:
   the per-file salt travels *inside* the file, so replacing a whole segment with an older copy of itself
   authenticates perfectly. The filename cross-check above stops a flipped id, not a wholesale swap. A
   real fix binds a store id + segment id into the GCM AAD (an on-disk format change).
4. **Page-cache budget is a soft bound.** A shard never evicts its last entry, so residency has a floor of
   one page per *touched* shard; when `PageCacheBytes < shardCount * PageSize` the cache can exceed its
   budget several-fold (e.g. a 64 KiB budget on a 64-core box). Harmless at the 16/64 MiB defaults.
5. **`RemoveSegment` has no handle refcount or grace period** (SPEC §9 asks for one). A read already in
   flight when the compactor deletes that segment fails with `ObjectDisposedException`, which the store
   swallows as a miss — so `Get` can return `null` for a key that exists. Rare, and self-healing for a
   regenerable cache.
6. **The index never shrinks.** `Rehash` runs only on insert-driven growth, so after mass deletion the
   arrays and their tombstones stay resident until enough *new* keys trip the threshold.
7. **`BulkLoad` skips tombstones rather than applying them**, so it is only correct on a pre-resolved
   entry set. `IndexFile.Parse` guarantees that (last-writer-wins per `KeyHash`); feeding it a raw delta
   stream would resurrect deleted keys.
8. **Crypto hot-path cost.** `Decrypt` builds a fresh `AesGcm` per call (a key schedule) and derives the
   nonce with a full HMAC-SHA256 per page. A per-file nonce base combined with `pageId` would remove the
   HMAC *and* make uniqueness structural rather than probabilistic, but it changes the on-disk format.

Other deliberate deviations from SPEC's open decisions:
- Crypto subkeys are derived from the master key with an **empty HKDF salt** (distinct info labels
  per subkey); per-page nonce uniqueness comes from each segment's own random salt. The master key is
  already a uniformly-random 256-bit secret, so a store-level KDF salt adds nothing (§5.3).
- Encrypted `.kidx` is not implemented. `IndexEncryption.On` is rejected at `Open`; `Auto` with a
  non-keyed hasher does not persist `.kidx` and rebuilds the index from the log on open.
- Lock contention raises `KvasarLockException` (distinct from `KvasarCorruptException`) so a second
  opener never triggers wipe-and-recreate on a live store.
