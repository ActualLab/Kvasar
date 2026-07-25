# ActualLab.Kvasar

**Kvasar** is a small, embedded, **encrypted key-value store for .NET** — pure managed, with
**zero native dependencies**. It follows the *fastest-path Bitcask model*: an in-RAM hash index over
an append-only, encrypted, paged log.

It exists to replace SQLite + SQLCipher as the on-device persistence engine for ActualChat's
client-side caches, leaving the native-library compliance treadmill (Google Play 16 KB page size,
NDK, alignment, notarization) behind for good.

## Highlights

- **Pure managed**, AOT- & trimming-safe. Runs on `net9.0` and all MAUI targets.
- **Encryption at rest** — AES-256-GCM per page, caller-supplied 32-byte key.
- **Fastest-path reads/writes** for a read-dominant, batched-write cache workload:
  one hash probe → one (cached) page decrypt → **zero-copy** `ReadOnlyMemory<byte>` slice.
- **Values on disk, index in RAM** as a key-length-independent `hash → location` map
  (~16–24 B/entry, independent of key size).
- **Lock-free readers, single writer** — the writer never blocks readers.
- Per-key atomicity; **no transactions** (single-key ops only).
- Binary keys and values (`ReadOnlyMemory<byte>`); string/typed conversions live in the caller.

## Status

Under active development. See [`docs/SPEC.md`](docs/SPEC.md) for the product specification and
[`docs/DESIGN.md`](docs/DESIGN.md) for the internal architecture.

## API sketch

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

The API is fully async and cancellable — SQLite is synchronous for historical reasons, Kvasar is not.
It returns `ValueTask`, so a **cache hit completes synchronously**: no state machine, no allocation,
no thread hop on the hot read path.

## Layout

```
src/ActualLab.Kvasar/        Library
  Crypto/                    AES-GCM page cipher, SipHash-2-4, HKDF-SHA256
  Paging/                    Layer 1: encrypted paged store + LRU page cache
  Log/                       Layer 2: append-only record log + segments
  Index/                     Layer 3: in-RAM hash index + .kidx persistence
tests/ActualLab.Kvasar.Tests/
docs/                        SPEC.md, DESIGN.md
```

## License

MIT — see [LICENSE](LICENSE).
