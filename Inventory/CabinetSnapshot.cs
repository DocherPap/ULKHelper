namespace ULKHelper.Inventory;

// Cabinet RowIds address individual bits, as verified in the local CN IsItemInCabinet implementation.
internal static class CabinetSnapshot
{
    public static Dictionary<uint, int>? Read(bool loaded, ReadOnlySpan<byte> unlocks,
        IReadOnlyList<(uint CabinetId, uint ItemId)> relevant)
    {
        if (!loaded || relevant.Count == 0) return null;
        foreach (var item in relevant)
            if (item.CabinetId / 8 >= (uint)unlocks.Length) return null;
        var items = new Dictionary<uint, int>();
        foreach (var item in relevant)
            if ((unlocks[(int)(item.CabinetId / 8)] & (1 << (int)(item.CabinetId % 8))) != 0)
                items[item.ItemId] = 1;
        return items;
    }
}
