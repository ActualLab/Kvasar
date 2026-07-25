# Kvasar — review findings (session of 2026-07-25)

A snapshot of what one review session surfaced. The actionable list, with detail and file
pointers, lives in [`TODO.md`](TODO.md).

**Size:** 2.6k LOC source (38 files) · 5.1k LOC tests (26 classes) · 299 tests, 298 pass / 1 skipped.
**Coverage (coverlet, measured):** **94.2% line** (1572/1668) · **89.6% branch** (629/702).

### Fixed this session
1. **Cancellation could corrupt the log.** A token flowed from `Set(ct)` all the way into
   `RandomAccess.WriteAsync`. Cancelling mid multi-page append leaves a record header claiming more
   bytes than exist — recovery then swallows every record appended after it. Write paths now take no
   token at all; public writers stay cancellable via `Task.WaitAsync(ct)` (abandons the wait, the
   write completes).
2. **Serial fsync per segment** in `SegmentSet.Flush` — now overlapped.

### Open — durability
3. **`.kidx` checkpoint renames without fsyncing the temp file first.** Classic ext4
   delayed-allocation hole: after power loss the new name can carry unwritten contents, and a
   partially-zeroed index loads zeroed entries, silently dropping keys. ~2 lines.
   **Best value/effort here.**
4. **No parent-directory fsync on segment creation** — a new `.klog`'s dirent isn't guaranteed
   durable, so `Flush(true)` can lose an acknowledged write. Needs libc P/Invoke on Unix; Windows is
   already covered by `FlushFileBuffers`.
5. **`Flush(true)` fsyncs every segment**, including immutable ones never written this session — N
   syscalls and N blocked threadpool threads where 1 would do. Wants a per-segment
   `_hasUnsyncedWrites` flag.
6. **Unverified:** .NET's `FlushToDisk` likely calls `fsync`, not `F_FULLFSYNC`, so on iOS/macOS
   `Flush(true)` may not survive power loss *at all*. Confirm before spending effort on #4.

### Open — unbounded growth (the worst cluster)
7. **Nothing ever calls `Compact()`** — only tests do. It's not on the open path, not on a write
   threshold, not on a timer. Dead space is never reclaimed unless the host asks; SQLite recycled
   freed pages for free.
8. **Segments under 8 MiB can never be compacted.** The gates require 4 MiB dead *and* ≥50% dead, so
   anything smaller is permanently ineligible even at 100% dead. Nothing merges small segments either.
9. **One new segment per killed session.** Unclean open rolls to a fresh segment (page-nonce safety)
   whenever the previous session wrote anything — routine on mobile. With #8 those files are
   immortal, so file count grows per launch, not per 16 MiB.
10. **Segment count == open fd count** — `Discover` opens every segment and holds the handle for the
    store's lifetime. iOS `RLIMIT_NOFILE` is in the low hundreds, so #8 + #9 make that reachable.

### Open — performance
11. **Compaction decrypts the entire store per pass** (`ScanAll` filtered down to one victim), so
    draining K segments is O(K × whole log) of AES-GCM. Should be `ScanFrom(victim, 0)` with an
    early break.

**Caveats:** BENCHMARKS.md was not re-run after this session's hot-path changes. Items 3–6 are
reasoned, not verified — the existing crash tests kill a process, which preserves the OS page cache,
so none of them is testable without device-level fault injection (dm-flakey / VM power-cut).
