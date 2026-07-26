namespace ActualLab.Kvasar.Internal;

/// <summary>Repo-wide on-disk constants shared by the paging, log and index layers.</summary>
public static class KvasarConstants
{
    // File magics (4 ASCII bytes each).
    public static ReadOnlySpan<byte> KLogMagic => "KVSR"u8;
    public static ReadOnlySpan<byte> KIdxMagic => "KIDX"u8;
    public static ReadOnlySpan<byte> KSupMagic => "KSUP"u8;

    // Page sizing.
    public const int DefaultPageSize = 4096;
    public const int MinPageSize = 512;
    public const int MaxPageSize = 1 << 20; // 1 MiB

    // Segment file header is a fixed-size plaintext prefix; encrypted pages follow it.
    public const int SegmentHeaderSize = 64;

    // Record sizing. RecordCodec sums key + value + a <= 16-byte header in int, so both parts are
    // capped to keep that sum from wrapping into a negative length.
    public const int MaxKeyBytes = 64 * 1024;
    public const int MaxRecordValueBytes = int.MaxValue - MaxKeyBytes - 16;

    // Crypto sizing.
    public const int MasterKeySize = 32;      // AES-256 master key
    public const int PageKeySize = 32;        // AES-256 page key
    public const int SuperblockKeySize = 32;  // AES-256 superblock key
    public const int HashKeySize = 16;        // SipHash-2-4 key
    public const int FileSaltSize = 16;       // per-segment random salt
    public const int GcmTagSize = 16;         // AES-GCM tag (per-page overhead)
    public const int GcmNonceSize = 12;

    // KDF info labels (subkey separation).
    public static ReadOnlySpan<byte> PageKeyInfo => "kvasar:page-key/v1"u8;
    public static ReadOnlySpan<byte> HashKeyInfo => "kvasar:index-hash-key/v1"u8;
    public static ReadOnlySpan<byte> SuperblockKeyInfo => "kvasar:superblock-key/v1"u8;
}
