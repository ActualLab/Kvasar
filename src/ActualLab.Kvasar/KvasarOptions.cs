using ActualLab.Kvasar.Crypto;

namespace ActualLab.Kvasar;

/// <summary>
/// Configuration for opening a <see cref="KvasarStore"/> (§4). All crypto hooks have secure defaults,
/// resolved lazily when the store is opened (a <c>null</c> hook = the default).
/// </summary>
public sealed record KvasarOptions
{
    // The store derives <base>.NNN.klog, <base>.kidx, <base>.lock from this.
    public required string BasePath { get; init; }
    // 32-byte AES-256 master key.
    public required byte[] EncryptionKey { get; init; }
    // On-disk format version; a mismatch on open ⇒ wipe & recreate.
    public string FormatVersion { get; init; } = "1";
    // The caller's own data version (schema, serializer, cache generation, ...): bump it and the next
    // open wipes & recreates the store. Empty ⇒ unversioned.
    public string Version { get; init; } = "";
    // Plaintext page payload size; 0 ⇒ probe the FS cluster size (fallback 4 KiB). Power of two.
    public int PageSize { get; init; } = 0;
    public long PageCacheBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxValueBytes { get; init; } = 8 * 1024 * 1024;
    // Values ≤ this stay single-page (zero-copy). 0 ⇒ use the page size.
    public int MaxInlineValueBytes { get; init; } = 0;

    // Pluggable crypto — null selects the secure default (§5.3).
    // null ⇒ SipHash-2-4.
    public IKeyHasher? Hasher { get; init; }
    // null ⇒ HKDF-SHA256.
    public IKeyDerivation? Kdf { get; init; }
    // Auto encrypts unless Hasher is a keyed PRF.
    public IndexEncryption IndexEncryption { get; init; } = IndexEncryption.Auto;

    public long SegmentBytes { get; init; } = 16 * 1024 * 1024;
    // Defers sealing and the disk write by up to this long, so writes pack densely and go out in one I/O;
    // a crash then loses writes newer than the last flush. Zero makes every Set durable before it returns.
    // Values stay visible to readers immediately either way: only durability is delayed, never visibility.
    public TimeSpan FlushDelay { get; init; } = TimeSpan.FromSeconds(0.5);
    public double CompactionDeadRatio { get; init; } = 0.5;
    // Reclaimable bytes, not segment size: don't compact below this.
    public long CompactionMinBytes { get; init; } = 4 * 1024 * 1024;

    // When true, an oversized value throws instead of being skipped + logged (§12).
    public bool OversizedValueThrows { get; init; }
    // Test/benchmark hook: use the no-op page cipher (no encryption at rest).
    public bool DisableEncryption { get; init; }
}
