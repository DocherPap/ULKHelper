using Lumina;
using Lumina.Data;
using Lumina.Excel.Sheets;
using ULKHelper.Data;
using ULKHelper.Inventory;

try
{
    using var game = new GameData(args[0], new LuminaOptions { DefaultExcelLanguage = Language.ChineseSimplified });
    var items = game.Excel.GetSheet<Item>()!;
    var catalog = GameCatalogLoader.Load(items, game.Excel.GetSheet<SpecialShop>()!);
    var checks = 0;
    void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }
    Check(catalog.Stages.Count == 132 && catalog.RelevantIds.Count == 665, "Local item resolution coverage");
    Check(catalog.Recipes.Count == 652, "Recipe coverage");
    Check(items.Where(i => i.Name.ExtractText().Contains("（复制品）", StringComparison.Ordinal))
        .All(i => !catalog.RelevantIds.Contains(i.RowId)), "Replica gear must not count as an upgrade");

    var ledger = new OwnershipLedger();
    ledger.Activate(123);
    Check(!ledger.HasData && ledger.Sources.All(s => s.UpdatedAt == null), "Unopened sources are unknown");
    var crystal = catalog.Materials["乱属性水晶"];
    var inputs = new Dictionary<uint, int> { [crystal] = 10 };
    ledger.Replace("bags", "背包", inputs);
    inputs[crystal] = 999;
    Check(ledger.Counts[crystal] == 10, "Snapshots must copy their input dictionaries");
    ledger.Replace("saddle", "鸟包", new() { [crystal] = 5 });
    ledger.Replace("retainer:A:bags", "雇员甲", new() { [crystal] = 12 });
    ledger.Replace("retainer:B:bags", "雇员乙", new() { [crystal] = 7 });
    Check(ledger.Counts[crystal] == 34, "Counts must sum all source quantities and retain both retainers");
    ledger.Replace("bags", "背包", new());
    Check(ledger.Counts[crystal] == 24 && ledger.Profile.Sources["bags"].UpdatedAt != null, "A loaded empty source replaces old contents");
    var revision = ledger.Revision;
    ledger.Replace("bags", "背包", new());
    Check(ledger.Revision == revision, "Unchanged polling must not invalidate the projection");

    var weapon = StatisticsCatalog.Equipment.Single(c => c.Key == "weapon-WAR");
    var weaponIds = catalog.Stages[weapon.Key];
    ledger.Replace("equipped", "穿戴", new() { [weaponIds[9]] = 1, [weaponIds[3]] = 1 });
    var projection = new InventoryProjection(ledger, catalog);
    Check(projection.OwnedStages(weapon).SequenceEqual(new[] { 3, 9 }), "Both physically owned stages must remain bright");
    Check(projection.CompletedStage(weapon) == 9, "Higher upgrades infer earlier progress");
    var summary = projection.Summarize([weapon]).ToDictionary(m => m.Material.Name);
    Check(summary["乱属性水晶"].Owned == 24 && summary["乱属性水晶"].Spent == 1300, "Stock and inferred spending must be separate");
    Check(summary["涌火水晶"].Spent == 650 && summary["丰水水晶"].Spent == 0, "Do not infer future upgrade costs");
    Check(summary["丰水水晶"].Remaining == 350, "Remaining requirement excludes current stock");
    Check(projection.Summarize([weapon, weapon]).SequenceEqual(projection.Summarize([weapon])), "Duplicated job display must not duplicate budgets");
    ledger.Replace("equipped", "穿戴", new());
    Check(projection.CompletedStage(weapon) == -1, "Removing gear updates inferred progress");

    var pld = StatisticsCatalog.Equipment.Single(c => c.Key == "weapon-PLD");
    var pldIds = catalog.Components[pld.Key];
    ledger.Replace("bags", "背包", new() { [pldIds[13]] = 1, [pldIds[13 + 15]] = 1 });
    var pldSummary = projection.Summarize([pld]).ToDictionary(m => m.Material.Name);
    Check(pldSummary["丰水水晶"].Spent == 350 && pldSummary["优雷卡的断片"].Spent == 0,
        "Sword and shield paired recipes must be counted once");
    Check(pldSummary["乱属性水晶"].Spent == 1300, "Recipe prerequisite closure across all islands");

    var folder = Path.GetFullPath(Path.Combine("artifacts", "inventory-tests", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "profile.json");
    ledger.Save(path);
    var saved = OwnershipLedger.Read(path, 123)!;
    var restored = new OwnershipLedger();
    restored.Activate(123, saved);
    Check(restored.HasData && restored.Counts[pldIds[13]] == 1, "Cache restores inventory");
    Check(restored.Sources.All(s => !s.ReadThisSession), "Cache must not pretend to be a fresh read");
    restored.Activate(456);
    Check(!restored.HasData && restored.Counts.Count == 0, "Character switching must clear all previous quantities");
    var rejected = false;
    try { OwnershipLedger.Read(path, 456); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "A different character's cache must be rejected");
    ledger.RemoveRetainersExcept(new HashSet<string> { "retainer:A:" });
    Check(!ledger.Profile.Sources.ContainsKey("retainer:B:bags") && ledger.Profile.Sources.ContainsKey("retainer:A:bags"), "Removed retainers must not remain owned");
    var corrupt = Path.Combine(folder, "corrupt.json");
    File.WriteAllText(corrupt, "not JSON");
    rejected = false;
    try { OwnershipLedger.Read(corrupt, 123); } catch (System.Text.Json.JsonException) { rejected = true; }
    Check(rejected && File.ReadAllText(corrupt) == "not JSON", "Invalid caches are preserved for recovery");

    var totals = new InventoryProjection(new OwnershipLedger(), catalog).Summarize(StatisticsCatalog.Equipment).ToDictionary(m => m.Material.Name);
    Check(totals["乱属性水晶"].Total == 64500 && totals["丰水水晶"].Total == 6580 && totals["优雷卡的断片"].Total == 2431,
        "Real recipe budgets must agree with the established full catalog");
    Console.WriteLine($"PASS: {checks} inventory/catalog/cache assertions; 132 tracks, 665 relevant IDs, 652 recipe outputs.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
