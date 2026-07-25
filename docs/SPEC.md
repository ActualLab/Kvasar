# Kvasar — Specification (draft v0.2)

Encrypted, file-system-based key-value store for .NET — **fastest-path Bitcask model**:
an in-RAM hash index over an append-only, encrypted, paged log.

> Status: **draft for review.** Items marked **[DECISION]** are open; each states a
> recommendation. Nothing is implemented yet. v0.2 narrows the design to the specific
> scenario below and supersedes the earlier B+tree/LSM/transactional exploration.

---

## 1. Purpose & context

Kvasar is a small, embedded, encrypted key-value store in pure managed .NET — **no
native libraries** — built to replace SQLite + SQLCipher as the on-device persistence
engine for ActualChat's client-side caches.

- **Immediate trigger:** Google Play's 16 KB page-size warning against
  `libe_sqlcipher.so` (NDK r21; no free 16 KB/NDK-r28 SQLCipher build exists).
- **Strategic goal:** leave the native-library compliance treadmill permanently — a
  pure-managed engine can never trip a 16 KB / NDK / alignment / notarization check.

### The specific scenario we target
Exactly what the ActualChat KVAS backend needs (`IBatchingKvasBackend`), nothing more:

| Consumer | File | Size (power user) | Notes |
|----------|------|-------------------|-------|
| `SQLiteRemoteComputedCache` (Fusion `IRemoteComputedCache`) | `CCC.db3` | **~25 MB** | hot path; startup hydration |
| `LocalSettings` KVAS backend | `LocalSettings.db3` | sub-MB | small settings blobs |

Both are **regenerable** (today deleted & recreated on any init failure) and
**read-dominant** with batched writes. That's the sweet spot for Bitcask.

---

## 2. Goals / non-goals

### Goals
- Pure managed, zero native deps. AOT- & trimming-safe. All MAUI targets + `net9.0`.
- **Fastest possible** point reads and batched writes for our scenario.
- Encryption at rest (AES-256-GCM), caller-supplied 32-byte key.
- **Values on disk**, index in RAM as a **key-length-independent `hash → location` map**.
- Zero-copy reads (`ReadOnlyMemory<byte>` slices into cached, immutable pages).
- Comfortable for ~100 MB / ~10⁶ entries (hot dataset ~25 MB).
- Drop-in `IBatchingKvasBackend` adapter for ActualChat.

### Non-goals (for this version)
- **No transactions** — single-key operations only (atomic per §4.3); no multi-key atomicity.
- Not a networked server; embedded, in-process only.
- Not SQL/relational/document. v1 values are opaque bytes (typed values may come later, §4.3).
- No secondary indexes, **no ordered keyspace, no pattern/regex queries** — a hash index; `Scan()`
  returns everything (unordered), callers filter client-side.
- No cross-process access. Single process, single store instance per file set.
- No strict ACID durability; losing the last few writes on power loss is fine (cache).
- No cross-device file portability (each device owns its store).

### Positioning
An embedded, persistent, encrypted **"basic Redis"** slice: fast KV with per-key
atomicity. Networking (`ActualLab.Rpc`) and Fusion `[ComputeMethod]` change-tracking are
**possible future layers**, not part of this version (§16).

---

## 3. Backend contract (from `IBatchingKvasBackend`, verified against `BatchingKvas`)

```csharp
ValueTask<byte[]?[]> GetMany(string[] keys, CancellationToken ct);       // positional, null = miss
ValueTask<(string Key, byte[] Value)[]> ListAllEntries(CancellationToken ct); // real keys
Task SetMany(List<(string Key, byte[]? Value)> updates, CancellationToken ct); // null Value = delete
Task Clear(CancellationToken ct);
```
- **`GetMany` is concurrent** (multiple reader workers, batches ≤64). Positional; `null` = miss.
- **`SetMany` is single-writer** (`LazyWriter`, one batch at a time, ≤64, ~250 ms debounce),
  **retried on throw** ⇒ operations must be idempotent. Duplicate keys in a batch: last wins.
- **`ListAllEntries` returns real keys** ⇒ full keys must be recoverable (they live on disk).
- **`Flush`** must persist prior writes before `ListAllEntries`/`Clear` observe them.

---

## 4. Public API

Keys **and** values are **binary**, wrapped in the `KvasarKey` / `KvasarValue` readonly structs —
each is a `ReadOnlyMemory<byte>` plus implicit conversions from byte/char memory, `byte[]`, `char[]`
and `string` (chars are UTF-8 encoded), so a caller can pass a `string` key without writing an
adapter. A returned value is a **zero-copy slice into a cached page** (§6.3). `null` (a
`KvasarValue?`) means *absent*; a present value may be empty.

The API is **fully async** — SQLite is synchronous for historical reasons, Kvasar deliberately is
not: all disk I/O uses positional `RandomAccess.ReadAsync`/`WriteAsync` on handles opened with
`FileOptions.Asynchronous`, and a `CancellationToken` flows through every *read* path (§4.4).
`ValueTask` (not
`Task`) is used throughout so that the **cache-hit fast path completes synchronously with no state
machine and no allocation** (§6.3): `Get` is not an `async` method — it resolves a cached,
single-page record inline and only builds a state machine on a cache miss or a page-spanning record.
The one unavoidable blocking call is `fsync`, which has no async form in .NET; it is offloaded
rather than run on the caller's thread.

```csharp
namespace ActualLab.Kvasar;

public sealed class KvasarStore : IAsyncDisposable
{
    public static ValueTask<KvasarStore> Open(KvasarOptions options, CancellationToken ct = default);

    // --- core KV (all binary; KvasarKey / KvasarValue) ---
    public ValueTask<KvasarValue?> Get(KvasarKey key, CancellationToken ct = default);   // thread-safe, null = miss
    public ValueTask<KvasarValue?[]> GetMany(IReadOnlyList<KvasarKey> keys, CancellationToken ct = default); // positional
    public ValueTask Set(KvasarKey key, KvasarValue? value, CancellationToken ct = default); // value == null => delete
    public ValueTask SetMany(IReadOnlyList<(KvasarKey Key, KvasarValue? Value)> updates, CancellationToken ct = default); // last dup wins

    // --- enumeration & reset ---
    public IAsyncEnumerable<(KvasarKey Key, KvasarValue Value)> Scan(CancellationToken ct = default); // ALL (unordered)
    public ValueTask Clear(CancellationToken ct = default);              // wipe everything (fast reset)

    // --- lifecycle ---
    public Task Flush();                                                 // completes when durable; no ct
    public ValueTask Flush(bool fsync, CancellationToken ct = default);
    public ValueTask Compact(CancellationToken ct = default);
    public ValueTask DisposeAsync();

    public KvasarStats Stats { get; }                                    // sync; best-effort snapshot
}

public readonly struct KvasarKey : IEquatable<KvasarKey>
{
    public ReadOnlyMemory<byte> Memory { get; }
    public ReadOnlySpan<byte> Span { get; }
    public int Length { get; }
    public bool IsEmpty { get; }
    public byte[] ToArray();
    // implicit: ReadOnlyMemory<byte|char>, byte[], char[], string -> KvasarKey; KvasarKey -> ReadOnlyMemory/Span<byte>
    // KvasarKeyExt.AsString: UTF-8 decode
}

public readonly struct KvasarValue : IEquatable<KvasarValue>   // same shape as KvasarKey, plus:
{
    public KvasarValueKind Kind { get; }                                 // Raw in v1 (§4.3)
    public KvasarValue Require(KvasarValueKind kind);                    // throws on a kind mismatch
    // every conversion *out* of a value (operators, KvasarValueExt.AsString) goes through Require
}

public sealed record KvasarOptions
{
    public required string BasePath { get; init; }                       // -> <base>.klog / .kidx / .lock
    public required byte[] EncryptionKey { get; init; }                  // 32 bytes (AES-256)
    public string FormatVersion { get; init; } = "1";                    // on-disk format; mismatch => wipe & recreate
    public string Version { get; init; } = "";                           // caller's data version; mismatch => wipe & recreate
    public int  PageSize { get; init; } = 0;                             // 0 => probe FS cluster size (fallback 4 KiB)
    public long PageCacheBytes { get; init; } = 16 * 1024 * 1024;        // decrypted-page LRU budget
    public int  MaxValueBytes { get; init; } = 8 * 1024 * 1024;
    public int  MaxInlineValueBytes { get; init; } = 0;                 // 0 => PageSize; ≤ this stays single-page (zero-copy, §5.2)
    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(0.5); // 0 => every Set durable on return
    // Pluggable crypto — secure defaults (§5.3)
    public IKeyHasher      Hasher { get; init; } = KeyHashers.SipHash24;      // keyed PRF (default)
    public IKeyDerivation  Kdf    { get; init; } = KeyDerivations.HkdfSha256; // master key -> subkeys
    public IndexEncryption IndexEncryption { get; init; } = IndexEncryption.Auto; // encrypt .kidx iff Hasher isn't a keyed PRF
    public long SegmentBytes { get; init; } = 16 * 1024 * 1024;         // .klog segment roll size (§9)
    public double CompactionDeadRatio { get; init; } = 0.5;
    public long CompactionMinBytes { get; init; } = 4 * 1024 * 1024;
}

public enum IndexEncryption { Auto, On, Off }   // Auto: encrypt .kidx unless Hasher is a keyed PRF
```

### 4.1 Operation rationale
- **`Get`/`GetMany`** — read core; `GetMany` positional, `null` = miss; called concurrently.
- **`Set`/`SetMany`** — write core; **delete = null value** (matches the backend's `null = delete`),
  so no separate remove primitive. `SetMany` applies in order, last dup wins.
- **`Scan()`** — unordered enumeration of **all** `(key, value)` pairs; this *is* `ListAllEntries`.
  Reads keys+values from disk (keys aren't in RAM) ⇒ O(n) disk, a rare op. **No server-side
  filtering:** callers wanting pattern/prefix selection enumerate and act per key themselves.
- No bulk/regex delete — use `Clear()` to wipe, or `Scan()` + per-key `Set(key, null)`.

### 4.2 Key & value semantics
- **Key identity = the raw key bytes** (the whole `KvasarKey.Memory` content). A `string` key is
  its UTF-8 encoding, so `"a"` and `"a"u8` are the same key.
- **Empty vs missing:** an empty value is a present value; a miss is `null`. Delete = `null`.
  Mind the difference between `KvasarValue?` = `null` (delete) and a `KvasarValue` built from a
  `null` array/string (a *present*, empty value) — the implicit conversions treat null like
  `ReadOnlyMemory<byte>` does.
- **Buffer ownership:** `Set` copies the key/value bytes it needs, so the caller may reuse its
  buffer right after the call. A `Get`/`Scan` result is a zero-copy slice into an immutable cached
  page and stays valid for as long as the caller holds it (GC-backed, §6.3) — copy it only to
  detach from the store's memory.

### 4.3 Value model & atomicity — forward-compatible
- **v1: binary keys + binary values** (`KvasarKey` / `KvasarValue`).
- **Value type tag:** every stored value carries a **1-byte type tag** (`KvasarValueKind`, surfaced
  as `KvasarValue.Kind`); v1 defines one — `Raw` (opaque bytes). Reserves room for Redis-style typed
  values later (counter, list, hash, set, …) **without a format break**; an unknown tag ⇒ corrupt ⇒
  regenerate. Only the store tags a value, so a caller can never write an unknown kind; conversions
  out of a `KvasarValue` assert the expected kind.
- **Atomicity: single-key operations are atomic** — a reader sees the complete old or new
  value, never a torn state (single-writer + immutable pages + atomic index publication, §7).
  **No transactions** across keys.

### 4.4 What a `CancellationToken` actually cancels
A write method's token cancels **only the wait for the single-writer lock** — the point before
anything has been mutated. Once the lock is held, `append → publish → checkpoint` runs to completion
uncancelled, and no token is passed down to the log, paging or index layers (their write methods
take none, so it can't be done by accident). Cancelling mid-write is not a safe operation on an
append-only log: a record's bytes are self-describing, so a half-written multi-page record makes
recovery read the records after it as its own tail, and a torn entry in the buffered `.kidx` delta
stream turns every entry after it into a garbage locator.

Fully cancellable: `Get`/`GetMany`/`Scan`, `Open`, and compaction's copy pass (it only *adds*
records — the index still points at the originals, so an abandoned pass leaves reclaimable dead
bytes, never a dangling locator).

---

## 5. Storage architecture (layered)

Encryption is a KV-agnostic hook; the KV logic never sees ciphertext.

```
  KvasarStore  (Get / Set / Scan / …)                    <- public API
      │  plaintext records
  ┌───┴───────────────────────────────────────┐
  │ Layer 3: in-RAM hash index (hash→location) │  key-length-independent; §6
  │ Layer 2: append-only record log            │  packs records into the page stream
  └───┬───────────────────────────────────────┘
      │  reads/writes *plaintext* pages
  ┌───┴───────────────────────────────────────┐
  │ Layer 1: encrypted paged store             │  fixed-size pages, LRU page cache,
  │          IPagedStore + IPageCipher         │  transparent AES-GCM per page
  └───┬───────────────────────────────────────┘
      │  positional file I/O (System.IO.RandomAccess)
     <base>.klog  (sequence of encrypted pages)
```

### Files
| File | Purpose |
|------|---------|
| `<base>.NNN.klog` | append-only **segments** of encrypted pages — source of truth (§9) |
| `<base>.kidx` | optional index snapshot for fast open (rebuildable; **unencrypted**, §6.5) |
| `<base>.lock` | single-writer advisory lock |

### 5.1 Layer 1 — encrypted paged store  **[core abstraction]**
Knows only fixed-size pages, nothing about keys/records.

```csharp
public interface IPagedStore : IDisposable {
    int  PageSize  { get; }                                 // usable *plaintext* payload per page
    long PageCount { get; }
    void ReadPage(long pageId, Span<byte> payload);         // cache hit, or read + decrypt
    long AppendPage(ReadOnlySpan<byte> payload);            // returns new pageId (writer only)
    void Flush(bool fsync = false);
}
public interface IPageCipher {                              // the encryption hook
    int  Overhead { get; }                                  // bytes added on disk (16-byte GCM tag)
    void Encrypt(long pageId, ReadOnlySpan<byte> plain,  Span<byte> onDisk);
    void Decrypt(long pageId, ReadOnlySpan<byte> onDisk, Span<byte> plain);
}
```
- On-disk page = `PageSize + Overhead`. Reads decrypt into a bounded **LRU page cache** of
  *plaintext* pages; upper layers never see ciphertext. A **no-op `IPageCipher`** gives an
  unencrypted store for tests/benchmarks and isolates crypto cost.
- **Page size** = FS **cluster size** of the store's volume (probed at open; fallback 4 KiB),
  power of two, **fixed for the store's life** (in the header). Cluster alignment ⇒ one page
  read = one allocation unit.
- **No mmap:** once pages are encrypted, mmap's zero-copy win is gone (every read decrypts
  into a fresh buffer). Use positional `System.IO.RandomAccess` (concurrent-read-safe).
- **Nonce — [DECISION]:** pages are append-only & **immutable once sealed**, so a page's
  bytes never change. ⇒ **deterministic GCM nonce = f(pageId, fileSalt)** (salt random per
  store, in header), **not stored on disk** (saves 12 B/page). AAD = `pageId || formatVer`.
  Safe *only* under immutability (nonce reuse breaks GCM) ⇒ sealed pages are never rewritten;
  compaction writes a **new file with a fresh salt**. *Alt:* stored random nonce (+12 B/page).

### 5.2 Layer 2 — append-only record log
Records are packed into page payloads; only the **active tail page** is mutable (buffered in
memory) until sealed & appended, after which it's immutable until compaction.

Record layout (plaintext, inside encrypted pages):
```
recordLen : varint   — bytes after this field
flags     : u8       — bit0: tombstone (delete)
valType   : u8       — value type tag (Raw=0; §4.3)
keyLen    : varint
key       : keyLen bytes            (binary)
value     : recordLen - (…) bytes   (absent for tombstone)
```
- An update **appends a new record** and repoints the index; the superseded record becomes
  **dead bytes** (compaction reclaims, §9). A delete appends a tombstone.
- **Packing (configurable).** Zero-copy requires a value to live within a single decrypted page.
  Values ≤ **`MaxInlineValueBytes`** (config; default = page size) are kept single-page — **padded** to
  the next page when they wouldn't fit the tail page's remainder (wastes ≤ ~1 page each) — and returned
  **zero-copy**. Larger values use a contiguous multi-page **run** returned copied / as
  `ReadOnlySequence<byte>`. The threshold trades padding waste against copy-on-read; the common
  small-entry case is always zero-copy.

### 5.3 File header & key privacy
File header (plaintext, non-secret): `magic "KVSR"`, `formatVer`, `pageSize`, `fileSalt(16)`,
`flags`. A magic/version/pageSize/key mismatch on open ⇒ wipe & recreate.

**Key privacy:** keys live inside the encrypted `.klog` pages (§5.2), so nothing about a key is
exposed at rest. The `.kidx` index (§6.5) stores only a **keyed hash** of each key (§6.1) — not
brute-forceable without the store key — so `.kidx` is safe **unencrypted**. No separate HMAC
record scheme is needed.

**Configurable crypto.** The page cipher (`IPageCipher`), key hasher (`IKeyHasher`), and key
derivation (`IKeyDerivation`) are pluggable via `KvasarOptions` with secure defaults —
**AES-256-GCM**, **SipHash-2-4** (keyed), **HKDF-SHA256** (master key → AES key + hash key; per-file
random salt). **Safety rule:** `.kidx` may be unencrypted only with a keyed-PRF hasher;
`IndexEncryption.Auto` enforces it (encrypts `.kidx` for any non-PRF hasher, e.g. xxHash3).

---

## 6. In-RAM hash index & read path (Bitcask)

### 6.1 The `hash → location` map
- In RAM: an **open-addressing hash table** whose entries are fixed-size and
  **key-length-independent**:
  `Entry = { uint16 Fingerprint; uint32 SegmentId; uint32 Offset; int Length; }` (~16 B;
  ~20–24 B effective at a ~0.7 load factor). `(SegmentId, Offset, Length)` locate the record's
  value in a `.klog` segment (§9).
- **RAM cost** ≈ 16–24 B × entries, *independent of key size*: ~a few MB for our 25 MB store,
  ~16 MB per 10⁶ entries. Full keys are **not** in RAM — they're on disk for verification,
  `Scan`/`ListAllEntries`, and rebuild.
- A point update/delete repoints/removes the entry; the old on-disk record becomes dead bytes.
- **Publication is the linearization point:** the entry is updated only after the record's
  bytes are in a sealed/appended page ⇒ concurrent readers never see a dangling locator.
- **Hash = keyed hash** (default **SipHash-2-4**, subkey from the KDF; pluggable via
  `KvasarOptions.Hasher`): a keyed PRF isn't computable without the store key ⇒ `.kidx` is safe
  **unencrypted** (§6.5) and the index resists hash-flooding. A non-PRF hasher (e.g. xxHash3) is
  allowed but forces `.kidx` encryption (§5.3). The in-RAM `Fingerprint` is its top 16 bits.

### 6.2 Lookup & collision handling
`Get(key)`: compute `h = hash(key)` → probe the table.
1. Match the in-RAM **fingerprint** first (top bits of `h`) → rejects non-matches with **no I/O**.
2. On a fingerprint hit, read the record and **compare the full key** (handles the
   astronomically-rare 64-bit hash collision correctly).
3. Continue open-addressing probes until match or empty slot (= miss).

### 6.3 Zero-copy read path  ← core to "fastest"
Decrypted pages are **immutable `byte[]` buffers**. On a hit, the value is returned as a
**`ReadOnlyMemory<byte>` slice into the cached page** — **no copy**, returned straight to the
caller. **Lifetime is pure GC:** a decrypted buffer is never mutated in place; cache eviction
just drops the reference, and any `ReadOnlyMemory` a caller holds keeps the buffer alive.

### 6.4 `GetMany` — batch by page
Sort the batch's locators by page, decrypt each hot page **once**, and slice out all
co-located values — amortized decryption + cache locality across the batch.

### 6.5 Index persistence & startup (`.kidx`) — the startup-cost path
Rebuild the index by reading the **index**, not the data. Under page encryption a header-only
scan of `.klog` still decrypts every value page, so we keep a dedicated **dense, unencrypted**
index file `.kidx` holding only fixed-size `{keyed-hash, location}` entries (~a few MB, not the
value log). It's safe in the clear because the hashes are keyed (§6.1) and reveal nothing about
keys without the store key; the only residual exposure is metadata (entry count, value sizes).

**`.kidx` layout** = a **checkpoint** (the live table) followed by an append-only **delta tail**,
both one homogeneous array of blittable entries:
```
IndexEntry (fixed, [StructLayout(Sequential)], ~24 B):
  keyHash   : u64  — keyed hash of the key (§6.1); fingerprint = its top bits
  segmentId : u32  — .klog segment (§9)
  offset    : u32  — offset within the segment
  length    : u32  — value/record length
  flags     : u8   — tombstone, etc.
```
The `.kidx` header records `magic/formatVer`, a link to `.klog`'s identity, and the **`.klog`
high-water-mark (HWM)** the file is consistent up to.

**Startup read:**
1. Validate `.kidx` header vs `.klog`.
2. **Load checkpoint** — the file *is* the array: `MemoryMarshal.Cast` the checkpoint region
   straight into the table's backing array (sized to the stored capacity). **No decryption, no
   parsing** — near-memcpy; `mmap`-able if we ever want it. *Fastest path.*
3. **Replay the delta tail** — fixed-size entries since the checkpoint, in order, last-writer-wins,
   tombstones remove.
4. **Scan `.klog` from HWM to end** — tiny gap of records written after the last `.kidx` update;
   apply them and truncate a torn tail.
5. **Fallback** — `.kidx` missing/invalid ⇒ full `.klog` scan once, then write a checkpoint.

Cost ≈ read the index (~few MB, **no decryption**) + near-memcpy + tiny tail scan ⇒ **single-digit
ms, scaling with index size not data size** (vs. ~15–30 ms to decrypt-scan a 25 MB `.klog`).

**Update strategy (cheap writes, fast reads):**
- **Per write:** append one fixed-size delta to `.kidx` (sequential, mirrors the `.klog` append;
  ~20 B regardless of value size) — **no whole-index rewrite per flush**.
- **Periodic checkpoint** (rewrite compact/blittable live table) bounds the delta tail. Triggers:
  graceful `DisposeAsync` (always), tail > ~50 % of live entries, and after data compaction
  (offsets change ⇒ index rewritten anyway).
- **Consistency:** write `.klog` record → then its `.kidx` delta; trust `.kidx` only up to the
  recovered `.klog` end and tail-scan the gap ⇒ `.kidx` needs **no fsync of its own** (lazy).

**[DECISION]** checkpoint form: blittable full array (fastest load, ~43 % empty-slot waste,
*recommended* for startup) vs compact live-list (smaller read, re-insert on load).
**[DECISION]** `.kidx` delta durability: lazy (recommended) vs fsync-with-flush.
**[DECISION]** encrypt `.kidx` too only if entry-count/value-size metadata is ever deemed sensitive.

### 6.6 Why this is fastest for our scenario
- **Reads:** one hash probe (no tree descent) + one page decrypt (cached) + zero-copy slice.
- **Writes:** append record + update one map entry — sequential, no page rewrites (only the
  tail page mutates before sealing).
- **RUM trade:** we spend **Memory** (the in-RAM map) to win **both Read and Write**
  amplification — the right call when "fastest" is the goal and the map is bounded and
  key-length-independent.
- **Not a B+tree / LSM:** a B+tree adds descent cost and per-commit path rewrites; an LSM adds
  read-amplification and compaction machinery. Both earn their keep only when the index can't
  fit in RAM or writes must scale huge — explicitly out of scope here.

---

## 7. Concurrency — lock-free readers, single writer (writer never blocks readers)
- **Multi-reader / single-writer** (matches `BatchingKvas`). **Readers never take a lock and never
  wait on the writer**, including during index growth.
- **Atomic locator publication.** Each slot's locator `(segmentId, offset, length)` is packed into a
  **single 64-bit word** and published with `Volatile.Write`; a reader `Volatile.Read`s it and so
  always sees a **fully-old-or-new** locator, never torn. (Relies on aligned 64-bit atomic writes —
  guaranteed on our ARM64/x64 targets.) The `Fingerprint` lives in a parallel `ushort[]` (16-bit reads
  are atomic); a stale fingerprint at worst costs one needless probe — correctness comes from the
  on-disk **full-key verify**.
- **Resize = copy-on-write swap.** To grow, the writer builds a new (larger) table off to the side and
  atomically swaps a single `Table` reference (`Volatile.Write`). Readers `Volatile.Read` the reference
  once per lookup and use that snapshot (old or new — both valid). No reader blocks on a rehash.
- **Record-before-index ordering.** The writer writes the record's bytes into a page **first**, then
  publishes the locator — so a reader either doesn't see the key yet or sees a locator pointing at
  fully-written bytes. Never a dangling/partial read.
- **Reads:** `System.IO.RandomAccess.Read` (positional, cursor-free, concurrent-safe) into the page
  cache; decrypted pages are immutable & GC-managed, so zero-copy results stay valid (§6.3).

## 8. Durability, recovery & corruption
- Relaxed by design (regenerable cache). `Flush(false)` = bytes to OS cache (survives app
  crash); `fsync` on graceful `DisposeAsync`/compaction.
- **Recovery scan:** walk `.klog` by `recordLen`; on a truncated/torn tail (crash), **truncate**
  there — earlier data intact. Value integrity is separately guaranteed by the page GCM tag.
- **Corruption** (bad magic/version/key, global auth failure, unreadable index) ⇒ the adapter
  (§13) deletes the file set and recreates — today's behavior. **Kvasar never throws an
  unrecoverable error to the app.** *Isolated* single-record issue ⇒ drop that key, keep the store.

## 9. Compaction (segment GC — *not* LSM leveling)
Overwrites and deletes leave dead records; we reclaim them with **Bitcask-style segment GC**.

- **Segmented log.** `.klog` is a series of immutable **sealed segments** plus one **active**
  segment taking appends; seal + roll when the active segment reaches `SegmentBytes` (~16 MiB).
  A locator is `(segmentId, offset, length)`. Each segment tracks `liveBytes`/`deadBytes`;
  overwriting or deleting a key decrements its old segment's `liveBytes`.
- **Trigger (async, background).** A background compactor selects sealed segments whose
  `deadBytes/segmentBytes ≥ CompactionDeadRatio`, merges their **live** records forward into the
  active/new segment, atomically repoints those keys in the index, then deletes the drained
  segments. One segment at a time ⇒ tiny pauses; also run when the app backgrounds.
- **Online for free.** The active segment keeps absorbing writes while sealed segments compact —
  no write-freeze or double-buffering (which a single-file full rewrite would require).
- **Write amplification ≈ `1/(1 − CompactionDeadRatio)`** (≈ 2× at 0.5) — far below leveled LSM's
  ~10–30×, because we never merge for sorted order.
- **Reader safety.** A segment is deleted only after no index entry references it; an in-flight
  reader keeps the segment handle open until its `RandomAccess.Read` completes (handle refcount /
  short grace period), and already-decrypted pages live in the GC-managed cache independent of the
  file handle.
- **Index (`.kidx`)** uses a different mechanism — a full **checkpoint rewrite** of the small,
  blittable live table (§6.5), triggered when the delta tail exceeds ~50 % of live entries. Same
  *trigger philosophy* (reclaim past a ratio, async), different *mechanism* (one snapshot, no segments).

> **Why not LSM leveling?** Leveled compaction bounds **read amplification** across sorted runs —
> a problem we don't have (in-RAM index ⇒ one probe → one location). It also *pays* ~T×levels write
> amplification for sorted order we never use. Segment GC gives lower write-amp, incremental async
> pauses, and yields the hint file (`.kidx`) for free.

**[DECISION]** `SegmentBytes` (~16 MiB) and `CompactionDeadRatio` (0.5) defaults.
**[DECISION]** segmented GC (recommended; online, incremental, scales) vs. single-file full-rewrite
(simplest; fine at ~25 MB but needs a write-freeze/double-buffer during rewrite).

## 10. Open / lifecycle
1. Acquire `<base>.lock` (advisory; single-process ⇒ fail-fast on contention).
2. Missing `.klog` ⇒ create with fresh header. Validate header; mismatch ⇒ wipe & recreate.
3. Load `.kidx` fast path, else scan-rebuild (§6.5). Compact if triggered.
`DisposeAsync`: `Flush(fsync:true)`, write `.kidx`, release lock/handles.

## 11. Versioning
`FormatVersion` (the on-disk format) and `Version` (the caller's own data version — schema,
serializer, cache generation) fold into the single on-disk `formatVer` tag stamped into every
segment header; that plus `pageSize`, any mismatch ⇒ wipe & recreate (safe: cache). This mirrors
what the SQLite backend did with its `(version)` row, minus the reserved key.
This is also the migration story **from SQLite** — first launch finds no `.klog`, starts empty,
caches repopulate. `.kidx` is always rebuildable and may be discarded across versions.

## 12. Error model
- Misses ⇒ `null`, never exceptions.
- Oversized value (> `MaxValueBytes`) ⇒ **[DECISION]** skip + log (recommended) vs. throw.
- Unrecoverable state ⇒ a single `KvasarCorruptException` the adapter catches to wipe & recreate.

## 13. ActualChat integration
- `KvasarBatchingKvasBackend : IBatchingKvasBackend` (Maui project, or `ActualChat.Core` if
  reused): `GetMany`→`GetMany`; `SetMany`→`SetMany`+`Flush`; `ListAllEntries`→`Scan()`;
  `Clear`→`Clear`. `string` keys convert to `KvasarKey` implicitly (UTF-8), and the adapter turns
  `KvasarValue` back into `byte[]` — the store itself stays pure binary. Pass the cache generation
  as `KvasarOptions.Version` (the `(version)` row's replacement) and wrap init in the existing
  delete-and-retry safety net.
- `MauiModule` swaps the SQLite-backed cache/KVAS for Kvasar-backed ones; `BasePath` from
  `FileSystem.CacheDirectory & "CCC"` / `AppDataDirectory & "LocalSettings"`; `EncryptionKey`
  from `MauiPreferences.DbEncryptionKey`.
- Remove `sqlite-net-sqlcipher` + `SQLitePCLRaw.bundle_e_sqlcipher` from `Maui.csproj` once both
  consumers migrate. **This closes the Google Play issue.**

## 14. Testing
- **Unit:** get/set/remove/overwrite/list/clear; positional `GetMany`; duplicate-in-batch; miss
  semantics; oversized value; hash-collision path (inject colliding keys).
- **Encryption:** wrong key ⇒ corrupt (no plaintext leak); tamper a byte ⇒ GCM auth fails.
- **Crash/torn-tail:** truncate `.klog` mid-record ⇒ recovery drops the tail, earlier data intact;
  interrupted compaction (`.tmp`) recovered on open.
- **Concurrency:** N readers + 1 writer ⇒ no torn reads, no lost committed writes.
- **Property-based:** random op sequences vs. an in-memory `Dictionary` oracle; reopen & re-verify.
- **Benchmark vs SQLCipher** on the real value-size distribution: open time, `Get` p50/p99,
  batched `GetMany`/`SetMany`, 25 MB open. Target: clearly faster on bulk random reads.
- **Regeneration:** simulate corruption ⇒ adapter wipes & rebuilds, app unaffected.

## 15. Open decisions (consolidated)
★ = load-bearing (shapes code, hard to change later); the rest have a recommended default.

**Cryptography**
1. ✔ **Hash fn ↔ `.kidx` encryption** — *resolved: configurable.* `Hasher` (default SipHash-2-4) +
   `IndexEncryption` (Auto/On/Off); Auto encrypts `.kidx` for non-PRF hashers (§5.3, §6.1).
2. ✔ **Key derivation** — *resolved: configurable* via `IKeyDerivation` (default HKDF-SHA256) (§5.3).
3. **Nonce** (§5.1): deterministic per-page (recommended) vs. stored random.
4. **AES-GCM impl:** `System.Security.Cryptography.AesGcm` (platform/hardware) — confirm, no custom crypto.

**In-RAM index**
5. ✔ **Concurrency & resize** — *resolved:* lock-free readers, single writer; atomic 64-bit
   packed-locator publish + COW table swap on resize; **writer never blocks readers** (§7).
6. **Probing & deletion:** linear / Robin-Hood probing; tombstone vs. backward-shift delete (impl detail).

**Log & record format**
7. ✔ **Packing / spanning** — *resolved: configurable* `MaxInlineValueBytes` (single-page zero-copy
   cap; larger ⇒ multi-page runs) (§5.2).
8. **Record integrity** (§8): page GCM tag + `recordLen` structural parse (recommended) vs. per-record CRC.

**Segmentation & compaction**
9. **Compaction** (§9): segmented GC (recommended) vs. single-file rewrite; `SegmentBytes` (~16 MiB),
   `CompactionDeadRatio` (0.5); reader-safe delete via handle refcount vs. grace period.
10. **`.kidx` checkpoint & durability** (§6.5): blittable full array (recommended) vs. compact list;
    lazy delta writes (recommended) vs. fsync.

**Durability & lifecycle**
11. **Fsync policy** (§8): fsync on graceful close + segment seal (recommended) vs. never (OS-only).
12. **Lock file** (§10): fail-fast on contention (recommended) vs. steal.

**Behavioral edges**
13. **Oversized values** (§12): skip + log (recommended) vs. throw; plus max key/value caps.

**Integration & packaging**
14. **Adapter placement & key encoding** (§13): Maui vs. `ActualChat.Core`; UTF-8 key encoding.
15. **Target frameworks:** `net9.0` only vs. `net8.0;net9.0`.
16. **Distribution:** project reference vs. published NuGet.

## 16. Future (out of scope now)
- **Networking:** expose the store over `ActualLab.Rpc`.
- **Reactivity:** Fusion `[ComputeMethod]` read APIs + a per-key change/invalidation hook so
  writes drive invalidations ("embeddable Redis with change tracking").
- **Typed values:** grow the value type tag (§4.3) into Redis-style structures + their ops.
- **Bounded-RAM index:** if a future use needs datasets whose index won't fit in RAM, revisit a
  disk-backed index (B+tree/Bε/LSM) behind the same `IPagedStore` — deliberately excluded here.
</content>
