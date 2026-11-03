using ULKHelper.Data;
using ULKHelper.Windows;

namespace ULKHelper.Inventory;

internal interface IStatisticsProvider
{
    long Revision { get; }
    bool HasData { get; }
    IReadOnlyList<SourceSnapshot> Sources { get; }
    string StatusText { get; }
    int[] OwnedStages(EquipmentCard card);
    int CompletedStage(EquipmentCard card);
    MaterialPreview[] Summarize(IEnumerable<EquipmentCard> cards);
}

internal sealed class InventoryProjection(OwnershipLedger ledger, ResolvedCatalog catalog)
{
    private long revision = -1;
    private HashSet<string> completed = [];
    private void Refresh()
    {
        if (revision == ledger.Revision) return;
        completed = catalog.CompletedRecipes(ledger.Counts);
        revision = ledger.Revision;
    }

    public int[] OwnedStages(EquipmentCard card) => catalog.Stages[card.Key].Select((id, stage) => (id, stage))
        .Where(p => ledger.Counts.GetValueOrDefault(p.id) > 0).Select(p => p.stage).ToArray();

    public int CompletedStage(EquipmentCard card)
    {
        Refresh();
        return catalog.Stages[card.Key].Select((id, stage) => (id, stage))
            .Where(p => catalog.Recipes.TryGetValue(p.id, out var recipe) && completed.Contains(recipe.Key))
            .Select(p => p.stage).DefaultIfEmpty(-1).Max();
    }

    public MaterialPreview[] Summarize(IEnumerable<EquipmentCard> cards)
    {
        Refresh();
        var recipes = cards.DistinctBy(c => c.Key).SelectMany(c => catalog.Plans[c.Key]).DistinctBy(r => r.Key).ToArray();
        var total = new Dictionary<string, int>();
        var spent = new Dictionary<string, int>();
        foreach (var recipe in recipes)
            foreach (var (material, quantity) in recipe.Materials)
            {
                total[material] = total.GetValueOrDefault(material) + quantity;
                if (completed.Contains(recipe.Key)) spent[material] = spent.GetValueOrDefault(material) + quantity;
            }
        return StatisticsCatalog.Materials.Where(m => total.ContainsKey(m.Name)).Select(m =>
            new MaterialPreview(m, ledger.Counts.GetValueOrDefault(catalog.Materials[m.Name]),
                spent.GetValueOrDefault(m.Name), total[m.Name])).ToArray();
    }
}
