using ActualLab.Kvasar.Internal;

namespace ActualLab.Kvasar.Tests.Index;

internal static class HashIndexTestExt
{
    public static void UpsertUniqueHash(this HashIndex index, ulong keyHash, Locator locator, int length)
    {
        var cursor = index.Probe(keyHash);
        while (cursor.MoveNext(out var previousLocator, out _)) {
            if (cursor.CurrentHash != keyHash)
                continue;
            if (!index.Set(keyHash, locator, length, previousLocator))
                throw new InvalidOperationException("The unique-hash entry changed without a concurrent writer.");
            return;
        }
        var keyId = keyHash == HashIndex.Empty ? 1UL : keyHash;
        index.Add(keyHash, keyId, locator, length);
    }

    public static bool TryGetUniqueHash(
        this HashIndex index, ulong keyHash, out Locator locator, out int length)
    {
        var cursor = index.Probe(keyHash);
        while (cursor.MoveNext(out locator, out length)) {
            if (cursor.CurrentHash == keyHash)
                return true;
        }
        locator = default;
        length = 0;
        return false;
    }
}
