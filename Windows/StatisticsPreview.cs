using ULKHelper.Data;

namespace ULKHelper.Windows;

// In-memory UI fixtures. No inventory services, caches, IPC or configuration writes.
internal static class StatisticsPreview
{
    internal static readonly Dictionary<string, int[]> OwnedStages = new()
    {
        ["weapon-PLD"] = [3, 9], ["weapon-WAR"] = [13], ["weapon-WHM"] = [14],
        ["weapon-SAM"] = [5], ["artifact-PLD-0"] = [0, 3], ["artifact-PLD-1"] = [2],
        ["artifact-PLD-4"] = [3], ["artifact-WHM-1"] = [1],
        ["elemental-防护-0"] = [2], ["elemental-防护-1"] = [1],
        ["elemental-治疗-4"] = [0, 2], ["special-防护"] = [0],
    };

    internal static readonly Dictionary<string, int> OwnedMaterials = new()
    {
        ["乱属性水晶"] = 842, ["常风水晶"] = 268, ["帕祖祖的羽毛"] = 2,
        ["结冰乱属性水晶"] = 12, ["恒冰水晶"] = 306, ["娄希的冰片"] = 1,
        ["涌火水晶"] = 428, ["彭忒西勒亚的火种"] = 3, ["丰水水晶"] = 176,
        ["水晶龙之鳞"] = 2, ["优雷卡的断片"] = 54, ["神秘印章"] = 1, ["大马士革织物"] = 2,
    };

    internal static int[] GetOwnedStages(EquipmentCard card, bool preview)
        => preview && OwnedStages.TryGetValue(card.Key, out var stages) ? stages : [];

    internal static MaterialPreview[] Summarize(IEnumerable<EquipmentCard> equipment, bool preview)
    {
        var totals = new Dictionary<string, int>();
        var spent = new Dictionary<string, int>();
        // Shared armor appears under multiple jobs, but is counted once by its stable UI key.
        foreach (var card in equipment.DistinctBy(c => c.Key))
        {
            var latest = GetOwnedStages(card, preview).DefaultIfEmpty(-1).Max();
            for (var stage = 0; stage < card.Stages.Length; stage++)
                foreach (var cost in card.Stages[stage].Costs)
                {
                    totals[cost.Material] = totals.GetValueOrDefault(cost.Material) + cost.Quantity;
                    if (stage <= latest)
                        spent[cost.Material] = spent.GetValueOrDefault(cost.Material) + cost.Quantity;
                }
        }

        return StatisticsCatalog.Materials.Where(m => totals.ContainsKey(m.Name)).Select(m =>
            new MaterialPreview(m, preview ? OwnedMaterials.GetValueOrDefault(m.Name) : 0,
                spent.GetValueOrDefault(m.Name), totals[m.Name])).ToArray();
    }
}

internal sealed record MaterialPreview(MaterialOption Material, int Owned, int Spent, int Total)
{
    internal int Remaining => Total - Spent;
    internal int Deficit => Math.Max(0, Remaining - Owned);
}
