using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using ULKHelper.Data;
using ULKHelper.Windows;
using CabinetRow = Lumina.Excel.Sheets.Cabinet;

namespace ULKHelper.Inventory;

internal sealed unsafe class InventoryTracker : IStatisticsProvider, IDisposable
{
    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IDataManager data;
    private readonly IGameInventory gameInventory;
    private readonly IAddonLifecycle addons;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly OwnershipLedger ledger = new();
    private readonly object sync = new();
    private ResolvedCatalog? catalog;
    private InventoryProjection? projection;
    private (uint CabinetId, uint ItemId)[] cabinetItems = [];
    private Dictionary<uint, uint[]> outfits = new();
    private long nextScan, nextSave, nextErrorLog;
    private ulong activeRetainer;
    private long retainerReadyAt;
    private int refreshRequested = 1;
    private bool cacheWritable = true;
    private string error = "";
    public long Revision { get { lock (sync) return ledger.Revision; } }
    public bool HasData { get { lock (sync) return catalog != null && ledger.HasData; } }
    public IReadOnlyList<SourceSnapshot> Sources
    {
        get
        {
            lock (sync) return ledger.Sources.Select(s => new SourceSnapshot
            {
                Key = s.Key, Label = s.Label, UpdatedAt = s.UpdatedAt, ReadThisSession = s.ReadThisSession,
            }).ToArray();
        }
    }
    public string StatusText { get { lock (sync) return GetStatusText(); } }
    private string GetStatusText() => error.Length > 0 ? error : ledger.Profile.ContentId == 0 ? "请登录角色以获取库存"
        : !HasData ? "库存尚未获取" : Sources.Any(s => s.UpdatedAt == null)
            ? "已计入已获取库存 · 部分来源未获取" : "已计入各来源库存";

    private static readonly string[] WatchedAddons =
    [
        "Inventory", "InventoryLarge", "InventoryExpansion", "ArmouryBoard", "Character",
        "InventoryBuddy", "InventoryBuddy2", "Cabinet", "CabinetWithdraw", "MiragePrismPrismBox",
        "RetainerList", "RetainerGrid", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2",
        "RetainerGrid3", "RetainerGrid4", "RetainerGrid5", "RetainerGrid6", "RetainerCharacter", "RetainerSellList",
    ];
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private static readonly InventoryType[] Armoury =
    [
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
    ];
    private static readonly InventoryType[] RetainerBags =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    ];

    public InventoryTracker(IDalamudPluginInterface pi, IFramework framework, IClientState client, IPlayerState player,
        IDataManager data, IGameInventory gameInventory, IAddonLifecycle addons, IPluginLog log, IGameGui gameGui)
    {
        this.pi = pi; this.framework = framework; this.client = client; this.player = player;
        this.data = data; this.gameInventory = gameInventory; this.addons = addons; this.log = log;
        this.gameGui = gameGui;
        framework.Update += Update;
        gameInventory.InventoryChangedRaw += InventoryChanged;
        addons.RegisterListener(AddonEvent.PostSetup, WatchedAddons, AddonChanged);
        addons.RegisterListener(AddonEvent.PreFinalize, WatchedAddons, AddonChanged);
    }

    private void InventoryChanged(IReadOnlyCollection<InventoryEventArgs> changes) => Interlocked.Exchange(ref refreshRequested, 1);
    private void AddonChanged(AddonEvent type, AddonArgs args) => Interlocked.Exchange(ref refreshRequested, 1);
    private string CachePath => Path.Combine(pi.GetPluginConfigDirectory(), "inventory", $"{ledger.Profile.ContentId:X16}.json");

    private void Update(IFramework _)
    {
        lock (sync) UpdateCore();
    }

    private void UpdateCore()
    {
        var cid = client.IsLoggedIn && player.IsLoaded ? player.ContentId : 0;
        if (cid != ledger.Profile.ContentId)
        {
            Save();
            cacheWritable = true;
            ledger.Activate(cid);
            activeRetainer = 0;
            error = "";
            nextScan = 0;
            if (cid != 0)
            {
                try { ledger.Activate(cid, OwnershipLedger.Read(CachePath, cid)); }
                catch (Exception ex)
                {
                    cacheWritable = false;
                    error = "库存缓存读取失败，已保留原文件；当前会话继续读取";
                    log.Error(ex, "ULKHelper inventory cache could not be loaded");
                }
            }
        }
        if (cid == 0) return;
        var now = Environment.TickCount64;
        if (Volatile.Read(ref refreshRequested) != 0 && nextScan > now + 250) nextScan = now + 250;
        if (now < nextScan) return;
        Interlocked.Exchange(ref refreshRequested, 0);
        nextScan = now + 750;
        try
        {
            if (catalog == null) InitializeCatalog();
            Scan(now);
            if (cacheWritable) error = "";
            if (now >= nextSave) { Save(); nextSave = now + 5000; }
        }
        catch (Exception ex)
        {
            if (now >= nextErrorLog)
            {
                error = "库存读取遇到错误，请查看 /xllog；保留上次有效数据";
                log.Error(ex, "ULKHelper inventory scan failed");
                nextErrorLog = now + 30000;
            }
            nextScan = now + 3000;
        }
    }

    private void InitializeCatalog()
    {
        var resolved = GameCatalogLoader.Load(data.GetExcelSheet<Item>(), data.GetExcelSheet<SpecialShop>());
        cabinetItems = data.GetExcelSheet<CabinetRow>().Where(r => resolved.RelevantIds.Contains(r.Item.RowId))
            .Select(r => (r.RowId, r.Item.RowId)).ToArray();
        outfits = data.GetExcelSheet<MirageStoreSetItem>().Select(r => (r.RowId, Items: new[]
        {
            r.MainHand.RowId, r.OffHand.RowId, r.Head.RowId, r.Body.RowId, r.Hands.RowId, r.Legs.RowId,
            r.Feet.RowId, r.Earrings.RowId, r.Necklace.RowId, r.Bracelets.RowId, r.Ring.RowId,
        })).Where(r => r.Items.Any(resolved.RelevantIds.Contains)).ToDictionary(r => r.RowId, r => r.Items);
        catalog = resolved;
        projection = new(ledger, resolved);
    }

    private void Scan(long now)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null) return;
        ReadContainers(inventory, "bags", "背包", Bags);
        ReadContainers(inventory, "equipped", "穿戴装备", [InventoryType.EquippedItems]);
        ReadContainers(inventory, "armoury", "兵装库", Armoury);
        ReadContainers(inventory, "saddle", "陆行鸟背包", [InventoryType.SaddleBag1, InventoryType.SaddleBag2]);
        ReadContainers(inventory, "premium-saddle", "高级陆行鸟背包", [InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2]);
        ReadCabinet();
        ReadDresser();
        ReadRetainers(inventory, now);
    }

    private void ReadContainers(InventoryManager* manager, string key, string label, InventoryType[] types)
    {
        // Publish the group atomically only when every requested page is loaded.
        var items = new Dictionary<uint, int>();
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null || container->Size < 0 || container->Size > 1000) return;
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null) return;
                Add(items, item->GetBaseItemId(), checked((int)item->GetQuantity()));
            }
        }
        ledger.Replace(key, label, items);
    }

    private void Add(Dictionary<uint, int> items, uint rawId, int quantity)
    {
        if (quantity <= 0 || rawId == 0) return;
        var (id, kind) = ItemUtil.GetBaseId(rawId);
        if (kind == ItemKind.EventItem || !catalog!.RelevantIds.Contains(id)) return;
        items[id] = checked(items.GetValueOrDefault(id) + quantity);
    }

    private void ReadCabinet()
    {
        var ui = UIState.Instance();
        if (ui == null || !ui->Cabinet.IsCabinetLoaded()) return;
        var items = new Dictionary<uint, int>();
        foreach (var (row, item) in cabinetItems)
            if (ui->Cabinet.IsItemInCabinet(row)) items[item] = 1;
        ledger.Replace("cabinet", "收藏柜", items);
    }

    private void ReadDresser()
    {
        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded) return;
        var items = new Dictionary<uint, int>();
        var ids = mirage->PrismBoxItemIds;
        for (var index = 0; index < ids.Length; index++)
        {
            var (id, kind) = ItemUtil.GetBaseId(ids[index]);
            if (kind == ItemKind.EventItem || id == 0) continue;
            if (!outfits.TryGetValue(id, out var pieces)) { Add(items, id, 1); continue; }
            for (var slot = 0; slot < pieces.Length; slot++)
                if (catalog!.RelevantIds.Contains(pieces[slot]) && mirage->IsSetSlotUnlocked((uint)index, slot)) Add(items, pieces[slot], 1);
        }
        ledger.Replace("dresser", "幻化柜", items);
    }

    private void ReadRetainers(InventoryManager* inventory, long now)
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady) { activeRetainer = 0; return; }
        var prefixes = new HashSet<string>();
        foreach (var retainer in manager->Retainers)
        {
            if (retainer.RetainerId == 0) continue;
            var prefix = $"retainer:{retainer.RetainerId:X16}:";
            prefixes.Add(prefix);
            ledger.EnsureSource(prefix + "bags", $"雇员 {retainer.NameString}（背包）");
            ledger.EnsureSource(prefix + "gear", $"雇员 {retainer.NameString}（穿戴）");
            ledger.EnsureSource(prefix + "market", $"雇员 {retainer.NameString}（货架）");
        }
        var roster = gameGui.GetAddonByName("RetainerList");
        if (prefixes.Count > 0 || !roster.IsNull && roster.IsVisible)
            ledger.RemoveRetainersExcept(prefixes);
        var order = ItemOrderModule.Instance();
        var selected = order == null ? 0 : order->ActiveRetainerId;
        // LastSelectedRetainerId alone remains set after dismissal; require the active sorter too.
        if (selected == 0 || selected != manager->LastSelectedRetainerId) { activeRetainer = 0; return; }
        if (activeRetainer != selected)
        {
            activeRetainer = selected;
            retainerReadyAt = now + 1500;
            return;
        }
        if (now < retainerReadyAt) return;
        var active = manager->GetActiveRetainer();
        if (active == null || active->RetainerId != selected) return;
        var key = $"retainer:{selected:X16}:";
        ReadContainers(inventory, key + "bags", $"雇员 {active->NameString}（背包）", RetainerBags);
        ReadContainers(inventory, key + "gear", $"雇员 {active->NameString}（穿戴）", [InventoryType.RetainerEquippedItems]);
        var market = gameGui.GetAddonByName("RetainerSellList");
        if (!market.IsNull && market.IsVisible)
            ReadContainers(inventory, key + "market", $"雇员 {active->NameString}（货架）", [InventoryType.RetainerMarket]);
    }

    public int[] OwnedStages(EquipmentCard card)
    {
        lock (sync) return HasData ? projection!.OwnedStages(card) : [];
    }
    public int CompletedStage(EquipmentCard card)
    {
        lock (sync) return HasData ? projection!.CompletedStage(card) : -1;
    }
    public MaterialPreview[] Summarize(IEnumerable<EquipmentCard> cards)
    {
        lock (sync) return catalog == null ? MaterialRequirements.WithoutInventory(cards) : projection!.Summarize(cards);
    }

    private void Save()
    {
        if (!cacheWritable || !ledger.Dirty || ledger.Profile.ContentId == 0) return;
        try { ledger.Save(CachePath); }
        catch (Exception ex)
        {
            error = "库存已更新，但缓存保存失败，请查看 /xllog";
            log.Error(ex, "ULKHelper inventory cache save failed");
        }
    }

    public void Dispose()
    {
        framework.Update -= Update;
        gameInventory.InventoryChangedRaw -= InventoryChanged;
        addons.UnregisterListener(AddonEvent.PostSetup, WatchedAddons, AddonChanged);
        addons.UnregisterListener(AddonEvent.PreFinalize, WatchedAddons, AddonChanged);
        lock (sync) Save();
    }
}
