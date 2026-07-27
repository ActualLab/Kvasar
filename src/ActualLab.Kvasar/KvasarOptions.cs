using ActualLab.Kvasar.Crypto;
using ActualLab.Kvasar.Internal.Storage;

namespace ActualLab.Kvasar;

/// <summary>
/// Configuration for opening a <see cref="KvasarStore"/> (§4). All crypto hooks have secure defaults,
/// resolved lazily when the store is opened (a <c>null</c> hook = the default).
/// </summary>
public sealed record KvasarOptions
{
    // The store derives its whole (fixed) file set from this: <base>.kvs, <base>.{0,1}.kdat,
    // <base>.{0,1}.kidx and <base>.lock.
    public required string BasePath { get; init; }
    // 32-byte AES-256 master key.
    public required byte[] EncryptionKey { get; init; }
    // On-disk format version; a mismatch on open ⇒ wipe & recreate.
    public string FormatVersion { get; init; } = "2";
    // The caller's own data version (schema, serializer, cache generation, ...): bump it and the next
    // open wipes & recreates the store. Empty ⇒ unversioned.
    public string Version { get; init; } = "";
    // Plaintext page payload size; 0 ⇒ the 4 KiB default. Power of two.
    public int PageSize { get; init; } = 0;
    public long PageCacheBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxValueBytes { get; init; } = 8 * 1024 * 1024;
    // Values ≤ this stay single-page (zero-copy). 0 ⇒ use the page's record-payload capacity.
    public int MaxInlineValueBytes { get; init; } = 0;

    // Pluggable crypto — null selects the secure default (§5.3).
    // null ⇒ SipHash-2-4.
    public IKeyHasher? Hasher { get; init; }
    // null ⇒ HKDF-SHA256.
    public IKeyDerivation? Kdf { get; init; }
    // Auto encrypts unless Hasher is a keyed PRF.
    public IndexEncryption IndexEncryption { get; init; } = IndexEncryption.Auto;

    // Unused. There is one active data file and compaction is total, so nothing rolls at a size
    // threshold (§4). Kept so existing callers still compile.
    [Obsolete(
        "SegmentBytes is unused; use CommitBytes to bound the commit and recovery window.")]
    public long SegmentBytes { get; init; } = 16 * 1024 * 1024;

    // What a commit does about durability. Atomicity holds under either value — recovery authenticates
    // every commit — so this only chooses how much recent work an OS-level crash can take.
    public KvasarDurability Durability { get; init; } = KvasarDurability.Buffered;
    // How long a commit waits after the FIRST write since the last one, batching everything in between.
    // Not a period: the timer is armed on the clean⇒dirty edge, so an idle store costs no wakeups.
    // Values stay visible to readers immediately regardless: only recoverability is delayed.
    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(0.5);
    // Commits early once this many uncommitted bytes accumulate, whatever FlushDelay says. This bounds
    // recovery, not just loss: reopening authenticates the newest commit's window, so an unbounded
    // window would mean an unbounded open.
    public long CommitBytes { get; init; } = 8 * 1024 * 1024;

    // Compact once dead/total reaches this. It sets the store's steady-state disk amplification:
    // at 2/3 a store holding L live bytes occupies up to 3L, and peaks at 4L while compacting.
    public double CompactionDeadRatio { get; init; } = 2.0 / 3.0;
    // Reclaimable bytes: don't bother compacting below this, so a small store doesn't churn.
    public long CompactionMinBytes { get; init; } = 1024 * 1024;

    // null ⇒ the real file system. Supplying one lets a host virtualize storage entirely — which is
    // also how the crash-consistency tests model a device write cache.
    public IStorageBackend? StorageBackend { get; init; }

    // When true, an oversized value throws instead of being skipped + logged (§12).
    public bool OversizedValueThrows { get; init; }
    // Test/benchmark hook: use the no-op page cipher (no encryption at rest).
    public bool DisableEncryption { get; init; }
}
