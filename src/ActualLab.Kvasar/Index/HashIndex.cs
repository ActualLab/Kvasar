using System.Threading;

namespace ActualLab.Kvasar.Internal;

/// <summary>
/// In-RAM open-addressing hash index (Layer 3, §6): maps a keyed 64-bit key hash to a record
/// <see cref="Locator"/> plus a length hint. Lock-free readers, single writer (§7); the whole table is
/// one immutable <see cref="Table"/> snapshot swapped atomically on resize (copy-on-write).
/// </summary>
public sealed class HashIndex
{
    // Reserved locator sentinels. Empty == Locator.None (packed 0). Tombstone is a value a real
    // locator can never take (SegmentId==Offset==uint.MaxValue is out of range for the log).
    internal const ulong Empty = 0UL;
    internal const ulong Tombstone = ulong.MaxValue;

    private const double MaxLoadFactor = 0.7;
    private const int MinCapacity = 8;

    // Single writer owns these; readers never touch them. `_used` counts non-empty slots
    // (live + tombstones); `_count` counts live slots. Both drive resize/tombstone reclamation.
    private int _count;
    private int _used;
    private readonly int _initialCapacity;

    // The current table snapshot. `volatile` gives release on write / acquire on read of the
    // reference itself; readers grab it once per lookup (see Probe).
    private volatile Table _table;

    public int Count => Volatile.Read(ref _count);

    public HashIndex(int initialCapacity = 1024)
    {
        _initialCapacity = CeilPow2(Math.Max(initialCapacity, MinCapacity));
        _table = new Table(_initialCapacity);
    }

    // --- Readers (lock-free) ---------------------------------------------------------------------

    // Fingerprint-only match (top 16 bits); the caller must full-key-verify each candidate.
    public ProbeCursor Probe(ulong keyHash)
        => new(_table, keyHash); // single acquire-read of the table reference for the whole probe

    public bool TryGetFirst(ulong keyHash, out Locator loc, out int length)
    {
        var cursor = Probe(keyHash);
        while (cursor.MoveNext(out loc, out length)) {
            if (cursor.CurrentHash == keyHash)
                return true;
        }
        loc = default;
        length = 0;
        return false;
    }

    // --- Writer (single-threaded) ----------------------------------------------------------------

    public void Set(ulong keyHash, Locator loc, int length)
    {
        var packed = loc.Packed;
        // Enforced, not asserted: a Debug.Assert vanishes in Release, and writing a sentinel into a live
        // slot makes the run terminate early — silently orphaning every later key in that chain.
        if (packed is Empty or Tombstone)
            throw new ArgumentOutOfRangeException(nameof(loc), "Locator collides with a reserved sentinel.");

        var t = _table;
        Scan(t, keyHash, out var live, out var insert, out var insertIsEmpty);
        if (live >= 0) {
            // Update in place: same slot/hash, only the locator (and length hint) change.
            // Publish length first, then release-write the locator.
            t.Lengths[live] = length;
            Volatile.Write(ref t.Locators[live], packed);
            return;
        }

        // New key. Grow (or rehash to drop tombstones) before taking a fresh empty slot.
        if (insertIsEmpty && _used + 1 > t.Threshold) {
            Rehash(CapacityFor(_count + 1));
            t = _table;
            Scan(t, keyHash, out _, out insert, out insertIsEmpty); // fresh table => empty target
        }

        t.Hashes[insert] = keyHash;
        t.Lengths[insert] = length;
        Volatile.Write(ref t.Locators[insert], packed); // release: hash/length already written
        _count++;
        if (insertIsEmpty)
            _used++; // reusing a tombstone doesn't add a non-empty slot
    }

    public bool Remove(ulong keyHash, Locator expectedLoc)
        => RemoveCore(keyHash, expectedLoc.Packed, matchLocator: true);

    public void Clear()
    {
        _count = 0;
        _used = 0;
        _table = new Table(_initialCapacity); // release-publishes the empty table
    }

    public void BulkLoad(ReadOnlySpan<IndexEntry> entries)
    {
        // Skips tombstones; on a duplicate KeyHash the last entry wins.
        var live = 0;
        foreach (var e in entries) {
            if (!e.IsTombstone)
                live++;
        }

        var t = new Table(CapacityFor(live));
        var used = 0;
        var count = 0;
        foreach (var e in entries) {
            if (e.IsTombstone)
                continue;
            // Entries come straight from the unvalidated .kidx, so a corrupt one can carry a sentinel
            // locator (a zero-filled tail decodes as packed 0); dropping it beats corrupting the table.
            var packed = e.Locator.Packed;
            if (packed is Empty or Tombstone)
                continue;
            InsertOrUpdate(t, e.KeyHash, packed, (int)e.Length, ref count, ref used);
        }

        _count = count;
        _used = used;
        _table = t; // release-publishes all the freshly built arrays
    }

    // RCS1242: `in` is mandated by the contract; IndexEntry's members are all readonly so the
    // by-ref pass makes no defensive copy here.
#pragma warning disable RCS1242
    public void Apply(in IndexEntry entry)
#pragma warning restore RCS1242
    {
        if (entry.IsTombstone) {
            RemoveCore(entry.KeyHash, 0, matchLocator: false);
            return;
        }
        // Same reasoning as BulkLoad: a corrupt .kidx entry must be dropped, not thrown on — Set now
        // rejects sentinels, and that exception would escape Open's wipe-and-recreate.
        var packed = entry.Locator.Packed;
        if (packed is Empty or Tombstone)
            return;
        Set(entry.KeyHash, entry.Locator, (int)entry.Length);
    }

    public IEnumerable<IndexEntry> Snapshot()
    {
        // Writer/quiescent only: unsafe to call while another thread mutates the index.
        var t = _table;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var lengths = t.Lengths;
        for (var i = 0; i < locators.Length; i++) {
            var packed = locators[i];
            if (packed is Empty or Tombstone)
                continue;
            yield return new IndexEntry {
                KeyHash = hashes[i],
                SegmentId = (uint)(packed >> 32),
                Offset = (uint)packed,
                Length = (uint)lengths[i],
                Flags = 0,
            };
        }
    }

    // --- Internals ------------------------------------------------------------------------------

    private bool RemoveCore(ulong keyHash, ulong expectedPacked, bool matchLocator)
    {
        var t = _table;
        var mask = t.Mask;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var i = (int)(keyHash & (ulong)mask);
        while (true) {
            var packed = locators[i];
            if (packed == Empty)
                return false; // end of run => not present
            if (packed != Tombstone && hashes[i] == keyHash) {
                if (matchLocator && packed != expectedPacked)
                    return false; // locator differs => compare-and-remove fails
                // Tombstone the slot: readers see the sentinel and keep probing (run stays intact).
                Volatile.Write(ref t.Locators[i], Tombstone);
                _count--; // slot stays occupied, so `_used` is unchanged
                return true;
            }
            i = (i + 1) & mask;
        }
    }

    // Locates, for `keyHash`, the live slot (if any) and the slot a fresh insert would take
    // (first tombstone in the chain, else the terminating empty slot). Writer-only, plain reads.
    private static void Scan(Table t, ulong keyHash, out int liveIndex, out int insertIndex, out bool insertIsEmpty)
    {
        var mask = t.Mask;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var i = (int)(keyHash & (ulong)mask);
        var firstTomb = -1;
        while (true) {
            var packed = locators[i];
            if (packed == Empty) {
                liveIndex = -1;
                insertIndex = firstTomb >= 0 ? firstTomb : i;
                insertIsEmpty = firstTomb < 0;
                return;
            }
            if (packed == Tombstone) {
                if (firstTomb < 0)
                    firstTomb = i;
            }
            else if (hashes[i] == keyHash) {
                liveIndex = i;
                insertIndex = i;
                insertIsEmpty = false;
                return;
            }
            i = (i + 1) & mask;
        }
    }

    // Insert/update while building a table off to the side (no publish barriers needed yet).
    private static void InsertOrUpdate(Table t, ulong keyHash, ulong packed, int length, ref int count, ref int used)
    {
        var mask = t.Mask;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var i = (int)(keyHash & (ulong)mask);
        while (true) {
            var cur = locators[i];
            if (cur == Empty) {
                hashes[i] = keyHash;
                locators[i] = packed;
                t.Lengths[i] = length;
                count++;
                used++;
                return;
            }
            if (hashes[i] == keyHash) { // last-writer-wins on duplicate hash
                locators[i] = packed;
                t.Lengths[i] = length;
                return;
            }
            i = (i + 1) & mask;
        }
    }

    // Rebuilds live slots into a new table of `newCapacity`, dropping tombstones, then swaps it in.
    // Readers keep using the old snapshot until the volatile publish below.
    private void Rehash(int newCapacity)
    {
        var old = _table;
        var t = new Table(newCapacity);
        var mask = t.Mask;
        var hashes = t.Hashes;
        var locators = t.Locators;
        var lengths = t.Lengths;
        var oldHashes = old.Hashes;
        var oldLocators = old.Locators;
        var oldLengths = old.Lengths;
        var used = 0;
        for (var j = 0; j < oldLocators.Length; j++) {
            var packed = oldLocators[j];
            if (packed is Empty or Tombstone)
                continue;
            var h = oldHashes[j];
            var i = (int)(h & (ulong)mask);
            while (locators[i] != Empty)
                i = (i + 1) & mask;
            hashes[i] = h;
            locators[i] = packed;
            lengths[i] = oldLengths[j];
            used++;
        }
        _used = used; // == _count (all remaining slots are live)
        _table = t;   // release: publishes every array write above to acquiring readers
    }

    // Both loops are capped: `cap <<= 1` overflows 2^30 to int.MinValue and then 0, after which the
    // condition stays true forever — an unkillable spin rather than an exception.
    private const int MaxCapacity = 1 << 30;

    private static int CapacityFor(int liveCount)
    {
        var cap = MinCapacity;
        while (cap < MaxCapacity && liveCount > (int)(cap * MaxLoadFactor))
            cap <<= 1;
        return cap;
    }

    private static int CeilPow2(int n)
    {
        var cap = MinCapacity;
        while (cap < MaxCapacity && cap < n)
            cap <<= 1;
        return cap;
    }

    /// <summary>Immutable snapshot of the table's parallel arrays + geometry (COW unit for resize).</summary>
    internal sealed class Table
    {
        public readonly ulong[] Hashes;   // full 64-bit keyed hash; top 16 bits are the fingerprint
        public readonly ulong[] Locators; // packed Locator; Empty(0)/Tombstone(~0) are sentinels
        public readonly int[] Lengths;    // record/value length hint
        public readonly int Mask;         // Capacity - 1
        public readonly int Threshold;    // resize when `_used` would exceed this

        public int Capacity => Locators.Length;

        public Table(int capacity)
        {
            Hashes = new ulong[capacity];
            Locators = new ulong[capacity];
            Lengths = new int[capacity];
            Mask = capacity - 1;
            Threshold = (int)(capacity * MaxLoadFactor);
        }
    }
}

/// <summary>
/// Allocation-free forward cursor over a fingerprint run in a <see cref="HashIndex"/> table snapshot.
/// Each <see cref="MoveNext"/> yields the next slot whose fingerprint matches the query, until an
/// empty slot ends the run. Ordering (§7): the locator is read with an acquire
/// (<see cref="Volatile.Read(ref ulong)"/>) before the parallel fields, mirroring the writer's release.
/// </summary>
public struct ProbeCursor
{
    private readonly ulong[] _hashes;
    private readonly ulong[] _locators;
    private readonly int[] _lengths;
    private readonly int _mask;
    private readonly ushort _fingerprint;
    private int _index;         // next slot to examine
    private ulong _currentHash; // full hash of the slot last returned by MoveNext

    internal ProbeCursor(HashIndex.Table table, ulong keyHash)
    {
        _hashes = table.Hashes;
        _locators = table.Locators;
        _lengths = table.Lengths;
        _mask = table.Mask;
        _fingerprint = (ushort)(keyHash >> 48);
        _index = (int)(keyHash & (ulong)table.Mask);
        _currentHash = 0;
    }

    // Full hash of the last MoveNext slot; the caller verifies against it (probe matches fingerprint only).
    public readonly ulong CurrentHash => _currentHash;

    public bool MoveNext(out Locator loc, out int length)
    {
        while (true) {
            var i = _index;
            // ACQUIRE: reading the locator first synchronizes-with the writer's release-write, so the
            // parallel-field reads below observe the fully-published slot (never a torn mix).
            var packed = Volatile.Read(ref _locators[i]);
            if (packed == HashIndex.Empty) {
                loc = default;
                length = 0;
                _currentHash = 0;
                return false; // terminating empty slot ends the run
            }
            if (packed == HashIndex.Tombstone) {
                _index = (i + 1) & _mask;
                continue; // removed slot: skip, but the run continues
            }

            // Seqlock validation. The acquire above only says "if I see this locator, I see the writes that
            // preceded it" — it does NOT pin the slot's generation, so a slot recycled to another key
            // between these reads would pair key A's locator with key B's hash/length. Re-reading the
            // locator (which the writer publishes last) confirms all three came from one generation.
            // All three are acquire loads so they cannot be reordered against each other.
            var h = Volatile.Read(ref _hashes[i]);
            var len = Volatile.Read(ref _lengths[i]);
            if (Volatile.Read(ref _locators[i]) != packed)
                continue; // slot changed under us: re-examine the same index

            _index = (i + 1) & _mask;
            if ((ushort)(h >> 48) != _fingerprint)
                continue; // fingerprint mismatch: reject with no I/O
            _currentHash = h;
            loc = Locator.FromPacked(packed);
            length = len;
            return true;
        }
    }
}
