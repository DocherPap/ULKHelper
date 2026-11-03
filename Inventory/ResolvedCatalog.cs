using ULKHelper.Data;

namespace ULKHelper.Inventory;

internal sealed record ItemDefinition(uint Id, string Name, uint Level, uint Category, bool Eureka);
internal sealed record UpgradeRecipe(uint[] Outputs, uint[] Precursors, Dictionary<string, int> Materials)
{
    public string Key => string.Join(",", Outputs.Order());
}

internal sealed class ResolvedCatalog
{
    public Dictionary<string, uint[]> Stages { get; } = new();
    public Dictionary<string, uint[]> Components { get; } = new();
    public Dictionary<string, uint> Materials { get; } = new();
    public Dictionary<uint, UpgradeRecipe> Recipes { get; } = new();
    public HashSet<uint> RelevantIds { get; } = [];
    public Dictionary<string, UpgradeRecipe[]> Plans { get; } = new();

    public ResolvedCatalog(IEnumerable<ItemDefinition> definitions)
    {
        var items = definitions.ToArray();
        var byName = items.GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.ToArray());
        uint Find(string name) => byName.TryGetValue(name, out var matches) && matches.Length == 1
            ? matches[0].Id : throw new InvalidDataException($"物品名称未能唯一匹配：{name}");
        foreach (var material in StatisticsCatalog.Materials) Materials[material.Name] = Find(material.Name);
        Dictionary<string, uint> weapons = new()
        {
            ["PLD"] = 2, ["WAR"] = 3, ["DRK"] = 87, ["WHM"] = 9, ["SCH"] = 98,
            ["AST"] = 89, ["MNK"] = 1, ["SAM"] = 96, ["DRG"] = 5, ["NIN"] = 84,
            ["BRD"] = 4, ["MCH"] = 88, ["BLM"] = 7, ["SMN"] = 10, ["RDM"] = 97,
        };
        uint[] levels = [335, 340, 345, 355, 360, 365, 370, 375, 380, 385, 390, 395, 400, 405, 405];
        uint[] Weapon(uint category) => levels.Select((level, stage) =>
            items.Single(i => i.Eureka && i.Category == category && i.Level == level &&
                (level != 405 || i.Name.EndsWith(stage == 14 ? "·改" : "·优雷卡", StringComparison.Ordinal))).Id).ToArray();
        foreach (var card in StatisticsCatalog.Equipment)
        {
            uint[] ids;
            if (card.Category == EquipmentCategory.Weapon) ids = Weapon(weapons[card.Jobs[0]]);
            else if (card.SpecialArmor) ids = [Find(card.Name)];
            else if (card.Category == EquipmentCategory.Artifact)
                ids = new[] { "", "+1", "+2", "·常风" }.Select(s => Find(card.Name + s)).ToArray();
            else ids = new[] { "", "+1", "+2" }.Select(s => Find(card.Name + s)).ToArray();
            Stages[card.Key] = ids;
            Components[card.Key] = card.Key == "weapon-PLD" ? [.. ids, .. Weapon(11)] : ids;
            RelevantIds.UnionWith(Components[card.Key]);
        }
        RelevantIds.UnionWith(Materials.Values);
    }

    public void SetRecipes(IEnumerable<UpgradeRecipe> recipes)
    {
        foreach (var recipe in recipes)
            foreach (var id in recipe.Outputs)
            {
                if (Recipes.TryGetValue(id, out var previous) && previous != recipe &&
                    (previous.Key != recipe.Key || !previous.Materials.OrderBy(p => p.Key).SequenceEqual(recipe.Materials.OrderBy(p => p.Key))))
                    throw new InvalidDataException($"装备 {id} 存在不同强化配方，不能确定材料总量。");
                Recipes[id] = recipe;
            }
        foreach (var card in StatisticsCatalog.Equipment)
        {
            var plan = Components[card.Key].Select(id => Recipes.TryGetValue(id, out var recipe) ? recipe
                : throw new InvalidDataException($"装备 {id} 未找到强化配方。")).DistinctBy(r => r.Key).ToArray();
            var expected = card.Stages.SelectMany(s => s.Costs).GroupBy(c => c.Material).ToDictionary(g => g.Key, g => g.Sum(c => c.Quantity));
            var actual = plan.SelectMany(r => r.Materials).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Value));
            if (!expected.OrderBy(p => p.Key).SequenceEqual(actual.OrderBy(p => p.Key)))
                throw new InvalidDataException($"{card.Name} 的本地配方与目录不一致：{string.Join(";", actual.Select(p => $"{p.Key}={p.Value}"))}");
            Plans[card.Key] = plan;
        }
    }

    public HashSet<string> CompletedRecipes(IReadOnlyDictionary<uint, int> inventory)
    {
        var pending = new Queue<uint>(inventory.Where(p => p.Value > 0).Select(p => p.Key));
        var visited = new HashSet<uint>();
        var completed = new HashSet<string>();
        while (pending.TryDequeue(out var id))
        {
            if (!visited.Add(id) || !Recipes.TryGetValue(id, out var recipe)) continue;
            completed.Add(recipe.Key);
            foreach (var precursor in recipe.Precursors) pending.Enqueue(precursor);
        }
        return completed;
    }
}
