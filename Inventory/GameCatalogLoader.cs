using Lumina.Excel;
using Lumina.Excel.Sheets;
using ULKHelper.Data;

namespace ULKHelper.Inventory;

internal static class GameCatalogLoader
{
    internal static ResolvedCatalog Load(ExcelSheet<Item> items, ExcelSheet<SpecialShop> shops)
    {
        var result = new ResolvedCatalog(items.Select(i => new ItemDefinition(i.RowId, i.Name.ExtractText(),
            i.LevelItem.RowId, i.ItemUICategory.RowId, i.Description.ExtractText().Contains("禁地兵装", StringComparison.Ordinal))));
        var equipmentIds = result.Components.Values.SelectMany(ids => ids).ToHashSet();
        var names = result.Materials.ToDictionary(p => p.Value, p => p.Key);
        var recipes = new List<UpgradeRecipe>();
        foreach (var shop in shops)
            foreach (var entry in shop.Item)
            {
                var outputs = entry.ReceiveItems.Where(r => equipmentIds.Contains(r.Item.RowId)).Select(r => r.Item.RowId).ToArray();
                if (outputs.Length == 0) continue;
                var materials = entry.ItemCosts.Where(c => names.ContainsKey(c.ItemCost.RowId) && c.CurrencyCost > 0)
                    .GroupBy(c => names[c.ItemCost.RowId]).ToDictionary(g => g.Key, g => checked((int)g.Sum(c => c.CurrencyCost)));
                if (materials.Count == 0) continue;
                var precursors = entry.ItemCosts.Where(c => equipmentIds.Contains(c.ItemCost.RowId)).Select(c => c.ItemCost.RowId).ToArray();
                recipes.Add(new(outputs, precursors, materials));
            }
        var covered = recipes.SelectMany(r => r.Outputs).ToHashSet();
        // Weapon upgrades from Anemos onwards use custom exchanges absent from SpecialShop.
        // Their costs are the wiki-verified stage table; IDs and sword/shield pairing are local data.
        foreach (var card in StatisticsCatalog.Equipment.Where(c => c.Category == EquipmentCategory.Weapon))
        {
            var ids = result.Components[card.Key];
            for (var stage = 0; stage < card.Stages.Length; stage++)
            {
                if (covered.Contains(ids[stage])) continue;
                if (stage < 3) throw new InvalidDataException("常风起始强化配方缺失。");
                var outputs = card.Key == "weapon-PLD" ? new[] { ids[stage], ids[stage + 15] } : [ids[stage]];
                var previous = card.Key == "weapon-PLD" ? new[] { ids[stage - 1], ids[stage + 14] } : [ids[stage - 1]];
                recipes.Add(new(outputs, previous, card.Stages[stage].Costs.ToDictionary(c => c.Material, c => c.Quantity)));
            }
        }
        result.SetRecipes(recipes);
        return result;
    }
}
