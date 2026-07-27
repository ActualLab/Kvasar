namespace ActualLab.Kvasar.Internal;

/// <summary>
/// In-RAM open-addressing hash index (Layer 3, §6): maps a keyed 64-bit key hash to one or more record
/// <see cref="Locator"/> values plus length hints. Lock-free readers, single writer (§7); the whole table
/// is one immutable <see cref="Table"/> snapshot swapped atomically on resize (copy-on-write).
/// </summary>
public sealed class HashIndex
{
    // Reserved locator sentinels. Empty == Locator.None (packed 0), unreachable because FileId is
    // 1-based. Tombstone is a value a real locator can never take (FileId would have to exceed
    // Locator.MaxFileId).
    internal const ulong Empty = 0UL;
    internal const ulong Tombstone = ulong.MaxValue;

    private const double MaxLoadFactor = 0.7;
    private const int MaxCapacity = 1 << 30;
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

    // --- Writer (single-threaded) ----------------------------------------------------------------

    public bool Set(ulong keyHash, Locator loc, int length, Locator previousLoc)
    {
        RequireLiveLocator(loc);
        var previousPacked = previousLoc.Packed;
        if (previousPacked == Empty)
            throw new ArgumentOutOfRangeException(nameof(previousLoc));

        var t = _table;
        var mask = t.Mask;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var i = (int)(keyHash & (ulong)mask);
        while (true) {
            var packed = locators[i];
            if (packed == Empty)
                return false;
            if (packed != Tombstone && hashes[i] == keyHash && packed == previousPacked) {
                t.Lengths[i] = length;
                Volatile.Write(ref t.Locators[i], loc.Packed);
                return true;
            }
            i = (i + 1) & mask;
        }
    }

    public void Add(ulong keyHash, ulong keyId, Locator loc, int length)
    {
        RequireLiveLocator(loc);
        if (keyId == Empty)
            throw new ArgumentOutOfRangeException(nameof(keyId));
        Insert(keyHash, keyId, loc.Packed, length);
    }

    public bool Remove(ulong keyHash, Locator expectedLoc)
        => RemoveCore(keyHash, expectedLoc.Packed);

    public void Clear()
    {
        _count = 0;
        _used = 0;
        _table = new Table(_initialCapacity); // release-publishes the empty table
    }

    public void BulkLoad(ReadOnlySpan<IndexEntry> entries)
    {
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
            if (packed is Empty or Tombstone || e.KeyId == Empty)
                continue;
            InsertOrUpdate(t, e.KeyHash, e.KeyId, packed, (int)e.Length, ref count, ref used);
        }

        _count = count;
        _used = used;
        _table = t; // release-publishes all the freshly built arrays
    }

    public IEnumerable<IndexEntry> Snapshot()
    {
        // Writer/quiescent only: unsafe to call while another thread mutates the index.
        var t = _table;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var keyIds = t.KeyIds;
        var lengths = t.Lengths;
        for (var i = 0; i < locators.Length; i++) {
            var packed = locators[i];
            if (packed is Empty or Tombstone)
                continue;
            yield return new IndexEntry {
                KeyHash = hashes[i],
                PackedLocator = packed,
                KeyId = keyIds[i],
                Length = (uint)lengths[i],
                Flags = 0,
            };
        }
    }

    // --- Internals ------------------------------------------------------------------------------

    private bool RemoveCore(ulong keyHash, ulong expectedPacked)
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
            if (packed != Tombstone && hashes[i] == keyHash && packed == expectedPacked) {
                // Tombstone the slot: readers see the sentinel and keep probing (run stays intact).
                Volatile.Write(ref t.Locators[i], Tombstone);
                _count--; // slot stays occupied, so `_used` is unchanged
                return true;
            }
            i = (i + 1) & mask;
        }
    }

    private void Insert(ulong keyHash, ulong keyId, ulong packed, int length)
    {
        var t = _table;
        Scan(t, keyHash, keyId, mustMatchKeyId: true, out var live, out var insert, out var insertIsEmpty);
        if (live >= 0)
            throw new InvalidOperationException("The key identity is already present.");
        if (insertIsEmpty && _used + 1 > t.Threshold) {
            Rehash(CapacityFor(_count + 1));
            t = _table;
            Scan(t, keyHash, keyId, mustMatchKeyId: true, out _, out insert, out insertIsEmpty);
        }
        t.Hashes[insert] = keyHash;
        t.KeyIds[insert] = keyId;
        t.Lengths[insert] = length;
        Volatile.Write(ref t.Locators[insert], packed);
        _count++;
        if (insertIsEmpty)
            _used++;
    }

    private static void Scan(
        Table t, ulong keyHash, ulong keyId, bool mustMatchKeyId,
        out int liveIndex, out int insertIndex, out bool insertIsEmpty)
    {
        var mask = t.Mask;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var keyIds = t.KeyIds;
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
            else if (hashes[i] == keyHash && (!mustMatchKeyId || keyIds[i] == keyId)) {
                liveIndex = i;
                insertIndex = i;
                insertIsEmpty = false;
                return;
            }
            i = (i + 1) & mask;
        }
    }

    private static void InsertOrUpdate(
        Table t, ulong keyHash, ulong keyId, ulong packed, int length,
        ref int count, ref int used)
    {
        var mask = t.Mask;
        var locators = t.Locators;
        var hashes = t.Hashes;
        var keyIds = t.KeyIds;
        var i = (int)(keyHash & (ulong)mask);
        while (true) {
            var cur = locators[i];
            if (cur == Empty) {
                hashes[i] = keyHash;
                keyIds[i] = keyId;
                locators[i] = packed;
                t.Lengths[i] = length;
                count++;
                used++;
                return;
            }
            if (hashes[i] == keyHash && keyIds[i] == keyId) {
                locators[i] = packed;
                t.Lengths[i] = length;
                return;
            }
            i = (i + 1) & mask;
        }
    }

    private static void RequireLiveLocator(Locator loc)
    {
        if (loc.Packed is Empty or Tombstone)
            throw new ArgumentOutOfRangeException(nameof(loc), "Locator collides with a reserved sentinel.");
    }

    // Rebuilds live slots into a new table of `newCapacity`, dropping tombstones, then swaps it in.
    // Readers keep using the old snapshot until the volatile publish below.
    private void Rehash(int newCapacity)
    {
        var old = _table;
        var t = new Table(newCapacity);
        var mask = t.Mask;
        var hashes = t.Hashes;
        var keyIds = t.KeyIds;
        var locators = t.Locators;
        var lengths = t.Lengths;
        var oldHashes = old.Hashes;
        var oldKeyIds = old.KeyIds;
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
            keyIds[i] = oldKeyIds[j];
            locators[i] = packed;
            lengths[i] = oldLengths[j];
            used++;
        }
        _used = used; // == _count (all remaining slots are live)
        _table = t;   // release: publishes every array write above to acquiring readers
    }

    // Both loops are capped: `cap <<= 1` overflows 2^30 to int.MinValue and then 0, after which the
    // condition stays true forever — an unkillable spin rather than an exception.
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
        public readonly ulong[] KeyIds;
        public readonly ulong[] Locators; // packed Locator; Empty(0)/Tombstone(~0) are sentinels
        public readonly int[] Lengths;    // record/value length hint
        public readonly int Mask;         // Capacity - 1
        public readonly int Threshold;    // resize when `_used` would exceed this

        public int Capacity => Locators.Length;

        public Table(int capacity)
        {
            Hashes = new ulong[capacity];
            KeyIds = new ulong[capacity];
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
    private readonly ulong[] _keyIds;
    private readonly ulong[] _locators;
    private readonly int[] _lengths;
    private readonly int _mask;
    private readonly ushort _fingerprint;
    private int _index;         // next slot to examine
    private ulong _currentHash; // full hash of the slot last returned by MoveNext
    private ulong _currentKeyId;

    internal ProbeCursor(HashIndex.Table table, ulong keyHash)
    {
        _hashes = table.Hashes;
        _keyIds = table.KeyIds;
        _locators = table.Locators;
        _lengths = table.Lengths;
        _mask = table.Mask;
        _fingerprint = (ushort)(keyHash >> 48);
        _index = (int)(keyHash & (ulong)table.Mask);
        _currentHash = 0;
        _currentKeyId = HashIndex.Empty;
    }

    // Full hash of the last MoveNext slot; the caller verifies against it (probe matches fingerprint only).
    public readonly ulong CurrentHash => _currentHash;
    internal readonly ulong CurrentKeyId => _currentKeyId;

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
                _currentKeyId = HashIndex.Empty;
                return false; // terminating empty slot ends the run
            }
            if (packed == HashIndex.Tombstone) {
                _index = (i + 1) & _mask;
                continue; // removed slot: skip, but the run continues
            }

            // Seqlock validation. The acquire above only says "if I see this locator, I see the writes that
            // preceded it" — it does NOT pin the slot's generation, so a slot recycled to another key
            // between these reads would pair key A's locator with key B's hash/length. Re-reading the
            // locator (which the writer publishes last) confirms the parallel fields came from one generation.
            var h = Volatile.Read(ref _hashes[i]);
            var keyId = Volatile.Read(ref _keyIds[i]);
            var len = Volatile.Read(ref _lengths[i]);
            if (Volatile.Read(ref _locators[i]) != packed)
                continue; // slot changed under us: re-examine the same index

            _index = (i + 1) & _mask;
            if ((ushort)(h >> 48) != _fingerprint)
                continue; // fingerprint mismatch: reject with no I/O
            _currentHash = h;
            _currentKeyId = keyId;
            loc = Locator.FromPacked(packed);
            length = len;
            return true;
        }
    }
}
