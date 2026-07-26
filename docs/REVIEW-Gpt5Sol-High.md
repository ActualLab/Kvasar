# Code Review Findings

Reviewed commit: `ab2fc34e36fbba754b8a13e933305e74abf6dbf0`

The review covered the public store API, paging, log recovery, index persistence, crypto integration,
concurrency paths, and the existing test suite. Five actionable issues were confirmed with targeted
regression probes.

## 1. [P1] Periodic checkpoints can outrun the deferred log

Location: [`src/ActualLab.Kvasar/KvasarStore.cs:571`](src/ActualLab.Kvasar/KvasarStore.cs#L571)

`WriteCheckpoint` snapshots the live index and records `ActiveLogicalHwm` without first sealing and
flushing the deferred log tail:

```csharp
await DisposeDeltaStream().ConfigureAwait(false);
var live = _index.Snapshot().ToArray();
var hwm = (_segments.ActiveSegmentId, checked((uint)_segments.ActiveLogicalHwm));
await IndexFile.WriteCheckpoint(_kidxPath, live, hwm, _formatVer, cancellationToken).ConfigureAwait(false);
```

With `FlushDelay > 0`, published records can still exist only in the in-memory tail. A periodic
checkpoint can therefore persist locators and an HWM beyond the physical `.klog`. After an abrupt
process death, recovery trusts that HWM and does not rescan the older records below it.

The targeted probe:

1. Wrote and explicitly flushed 100 keys.
2. Overwrote 15 keys without flushing, causing the delta threshold to trigger a checkpoint.
3. Snapshotted the live files to simulate abrupt process death.
4. Reopened the snapshot.

The flushed version of key 0 was returned as `null`. The unflushed overwrite was allowed to disappear,
but its older flushed value was required to survive.

`WriteCheckpoint` must ensure the log is at least as durable as the index before taking the snapshot
and HWM, for example by flushing `_segments` first. This is also required by the ordering rule in
`docs/DESIGN.md`.

## 2. [P1] A wrong encryption key is accepted when the active segment is empty

Location:
[`src/ActualLab.Kvasar/Internal/Log/SegmentSet.cs:448`](src/ActualLab.Kvasar/Internal/Log/SegmentSet.cs#L448)

Open authenticates only page 0 of the active segment:

```csharp
if (_active.PageCount > 0)
    _ = await _active.GetPage(0, cancellationToken).ConfigureAwait(false);
```

Recovery can naturally leave an empty active segment above older, non-empty sealed segments. For
example, an unclean deferred-flush open rolls to a fresh segment; if that store then closes without
another write, the fresh segment remains empty.

In this state, opening with a different master key performs no page authentication. The persisted
`.kidx` is loaded even though its hashes were produced with the previous derived hash key. Point
lookups then silently miss, and subsequent writes can create a store containing segments encrypted
under different keys.

The targeted probe created this state and reopened with a different key. The store retained
`Stats.Entries == 1` instead of detecting the bad key and returning a wiped, empty store.

Key validation should decrypt a page from any non-empty segment, not specifically the active one,
before trusting the persisted index.

## 3. [P1] Corrupted `.kidx` checkpoints cause silent data loss

Locations:

- [`src/ActualLab.Kvasar/Index/IndexFile.cs:92`](src/ActualLab.Kvasar/Index/IndexFile.cs#L92)
- [`src/ActualLab.Kvasar/KvasarStore.cs:629`](src/ActualLab.Kvasar/KvasarStore.cs#L629)

`IndexFile.Parse` checks the magic, format version, entry size, and structural lengths, but it does not
authenticate or checksum the checkpoint. It also does not validate the HWM, log identity, entry
hashes, or locators against the log.

A structurally valid bit flip in a checkpoint entry is accepted. Because `LoadIndex` scans the log
only after the stored HWM, the valid record below that HWM is never rediscovered.

The targeted probe:

1. Persisted two keys and closed cleanly.
2. Flipped one bit in the first checkpoint entry's `KeyHash`.
3. Reopened the store.

One of the two keys became permanently inaccessible even though its authenticated log record remained
intact.

The checkpoint needs an integrity mechanism, such as a keyed MAC or authenticated index format.
Alternatively, any unverified checkpoint must be validated against the log and rejected in favor of a
full rebuild when inconsistent.

## 4. [P1] `Clear` and corruption recovery can delete unrelated files

Location: [`src/ActualLab.Kvasar/KvasarStore.cs:741`](src/ActualLab.Kvasar/KvasarStore.cs#L741)

`WipeFiles` enumerates `<base>.*` and deletes every file whose extension is `.klog`:

```csharp
foreach (var file in Directory.EnumerateFiles(dir, name + ".*")) {
    var ext = Path.GetExtension(file);
    if (ext is ".klog" or ".kidx" or ".clean" || file.EndsWith(".kidx.tmp", StringComparison.Ordinal)
            || Path.GetFileName(file).EndsWith(".klog", StringComparison.Ordinal)) {
        try { File.Delete(file); }
        catch { /* best-effort */ }
    }
}
```

This is broader than segment discovery, which recognizes only numeric
`<base>.<segmentId>.klog` names. Consequently, wiping store `cache` also deletes unrelated files such
as `cache.backup.klog`.

A targeted `Clear` probe confirmed that `store.backup.klog` was deleted.

Wiping should reuse the numeric segment-name validation used by discovery and delete only:

- Exact numeric segment files.
- `<base>.kidx`.
- `<base>.kidx.tmp`.
- `<base>.clean`.

## 5. [P2] Read cancellation is converted into an ordinary cache miss

Locations:

- [`src/ActualLab.Kvasar/KvasarStore.cs:393`](src/ActualLab.Kvasar/KvasarStore.cs#L393)
- [`src/ActualLab.Kvasar/KvasarStore.cs:168`](src/ActualLab.Kvasar/KvasarStore.cs#L168)

`TryReadValue` catches every exception other than `KvasarCorruptException`:

```csharp
catch (Exception e) when (e is not KvasarCorruptException) {
    return null;
}
```

This includes `OperationCanceledException`, so a canceled cache-miss lookup returns `null` rather than
propagating cancellation. A targeted probe using a pre-canceled token on a known uncached page
completed normally instead of throwing.

`Scan` has the same problem around `TryReadRecord`.

Cancellation should be excluded from these catch filters. Prefer catching only the specific transient
exceptions caused by concurrent segment removal or disposal.

## Existing documented issue

The suite already contains one skipped regression:
`EdgeCaseTests.HashCollisionFanOut_KnownBug`. Distinct keys with the same full 64-bit hash evict one
another, causing silent loss of all but the latest colliding key. This is documented in
`docs/DESIGN.md` and was not counted among the five new findings above.

## Validation

The unmodified stock suite passed in Release mode on both supported frameworks:

| Framework | Passed | Failed | Skipped |
|---|---:|---:|---:|
| `net10.0` | 286 | 0 | 1 |
| `net9.0` | 286 | 0 | 1 |

All builds and probes ran in a disposable linked worktree. No source files in the shared checkout were
modified as part of the review.
