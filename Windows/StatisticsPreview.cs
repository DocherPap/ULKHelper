using ULKHelper.Data;

namespace ULKHelper.Windows;

internal static class MaterialRequirements
{
    internal static MaterialPreview[] WithoutInventory(IEnumerable<EquipmentCard> equipment)
    {
        var totals = new Dictionary<string, int>();
        foreach (var card in equipment.DistinctBy(card => card.Key))
            foreach (var cost in card.Stages.SelectMany(stage => stage.Costs))
                totals[cost.Material] = totals.GetValueOrDefault(cost.Material) + cost.Quantity;

        return StatisticsCatalog.Materials.Where(material => totals.ContainsKey(material.Name))
            .Select(material => new MaterialPreview(material, 0, 0, totals[material.Name])).ToArray();
    }
}

internal sealed record MaterialPreview(MaterialOption Material, int Owned, int Spent, int Total)
{
    internal int Remaining => Total - Spent;
    internal int Deficit => Math.Max(0, Remaining - Owned);
}
