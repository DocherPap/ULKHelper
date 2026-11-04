namespace ULKHelper.Bunnies;
internal enum BunnyChest { Bronze, Silver, Gold }
internal sealed record BunnyLoot(uint Territory,BunnyChest Chest,uint[] Items);
// Notable Eureka-themed rewards, excluding gil/materia/general lockbox filler.
// Community reward tables and local Item verification: docs/BUNNY_TREASURE.md.
internal static class BunnyLootCatalog
{
    public static readonly BunnyLoot[] All=
    [
        new(763,BunnyChest.Bronze,[23348,23347]),
        new(763,BunnyChest.Silver,[23049,23229]),
        new(763,BunnyChest.Gold,[23213,23214,23215,23217,23027]),
        new(795,BunnyChest.Bronze,[24007,24008]),
        new(795,BunnyChest.Silver,[23049,24233,24338,24009]),
        new(795,BunnyChest.Gold,[24233,24219,24010,24011]),
        new(827,BunnyChest.Bronze,[24014,24011,24009]),
        new(827,BunnyChest.Silver,[24820,23049,24904,24013]),
        new(827,BunnyChest.Gold,[24998,24858,24630,24904,24010]),
    ];
}
