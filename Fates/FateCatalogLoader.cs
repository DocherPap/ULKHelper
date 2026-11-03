using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ULKHelper.Fates;

internal static class FateCatalogLoader
{
    public static IReadOnlyList<IslandMap> Load(ExcelSheet<TerritoryType> territories, ExcelSheet<Fate> fates)
    {
        if (FateSeeds.All.Select(s => s.Id).Distinct().Count() != FateSeeds.All.Length)
            throw new InvalidDataException("FATE 点位 ID 重复。");
        var expected = fates.Where(f => f.EurekaFate == 1).Select(f => f.RowId).ToHashSet();
        if (!expected.SetEquals(FateSeeds.All.Select(s => (uint)s.Id))) throw new InvalidDataException("优雷卡 FATE 点位与游戏表不一致。");
        var names = new[] { "常风之地", "恒冰之地", "涌火之地", "丰水之地" };
        var result = new List<IslandMap>();
        foreach (var (group, index) in FateSeeds.All.GroupBy(s => s.Territory).Select((g, i) => (g, i)))
        {
            var territory = territories.GetRow(group.Key);
            var map = territory.Map.Value;
            var code = map.Id.ExtractText();
            var definitions = group.Select(seed =>
            {
                var row = fates.GetRow(seed.Id);
                if (seed.X is < 1 or > 42 || seed.Y is < 1 or > 42 || seed.CooldownMinutes <= 0
                    || (row.Rule == 4) != seed.Bunny) throw new InvalidDataException($"FATE {seed.Id} 点位或类型无效。");
                return new FateDefinition(seed, row.Name.ExtractText(), row.ClassJobLevel);
            }).OrderBy(f => f.Seed.Bunny).ThenBy(f => f.Level).ThenBy(f => f.Seed.Id).ToArray();
            result.Add(new(group.Key, map.RowId, names[index], $"ui/map/{code}/{code.Replace("/", "")}_m.tex",
                map.SizeFactor, map.OffsetX, map.OffsetY, definitions));
        }
        return result;
    }
}
