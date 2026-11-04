using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ULKHelper.Fates;
using ULKHelper.Weather;

namespace ULKHelper.Automation;

internal sealed unsafe class CommonJourney(IDalamudPluginInterface plugin, INavigationMesh mesh,
    IClientState client, IObjectTable objects, IGameGui gui, IDataManager data, ICondition conditions,
    IArrivalActions mounts, WeatherZone? pagosWeather, Action<string> notify) : IDisposable
{
    internal const uint Pagos = 763;
    private const uint Blitzring = 36121, Fog = 4;
    private const uint Kugane = 628, Rodney = 1024517, Potion = 22306;
    private const int Quantity = 99, UnitPrice = 300;
    private const string VendorName = "调查队的补给负责人";
    private static readonly InventoryType[] CarriedRingContainers =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.EquippedItems, InventoryType.ArmoryRings,
    ];
    private static readonly Vector3 RodneyPosition = new(-133.147f, -5.02026f, 149.283f);
    private static readonly Dictionary<uint, Vector2> Vendors = new()
    {
        [732] = new(19f, 32.1f), [763] = new(4.1f, 24.3f), [795] = new(16.7f, 23.8f), [827] = new(19.8f, 13.9f),
    };
    private readonly DestinationNavigationHelper walk = new(mesh);
    private readonly IslandTravelHelper islandWalk = new(mesh, new IslandCrystalGameAdapter(objects, gui, data), mounts);
    public bool Transferring => islandWalk.Transferring;
    private enum Stage { City, Pier, Walk, Interact, Menu, Enter, Purchase }
    private Stage stage;
    private WorldContext origin;
    private uint destinationIsland;
    private string islandName = "";
    private bool purchase, requestedPier, ownsLifestream, confirmed, commenced, openedIslandMenu;
    private ulong actor;
    private DateTimeOffset began, stageAt;
    private DateTimeOffset nextDismount, nextDialogueAction;
    private int inventoryBefore;
    private uint gilBefore;
    private uint purchaseShopId;
    public bool Active { get; private set; }
    public bool CheckPagosRing { get; set; }
    public string Status { get; private set; } = "请选择常用功能。";

    public void Start(CommonAction action, WorldContext world, DateTimeOffset now)
    {
        if (Active) return;
        if (HasDialog() || conditions[ConditionFlag.InDutyQueue])
        { Status = "请先关闭当前对话、商店或排队。"; return; }
        purchase = action == CommonAction.Potion;
        (destinationIsland, islandName) = action switch
        {
            CommonAction.Anemos => (732u, "常风之地"),
            CommonAction.Pagos => (Pagos, "恒冰之地"),
            CommonAction.Pyros => (795u, "涌火之地"),
            CommonAction.Hydatos => (827u, "丰水之地"),
            _ => (0u, ""),
        };
        if (purchase && !Vendors.ContainsKey(world.Territory)) { Status = "请在优雷卡岛内购买。"; return; }
        if (!purchase && Vendors.ContainsKey(world.Territory)) { Status = "请先离开当前优雷卡岛。"; return; }
        if (!CheckPagosEntry(now)) return;
        origin = world; began = now; actor = 0;
        requestedPier = ownsLifestream = confirmed = commenced = openedIslandMenu = false;
        nextDismount = now;
        nextDialogueAction = now;
        Active = true;
        if (purchase)
        {
            if (InventoryManager.Instance() is null || InventoryManager.Instance()->GetGil() < Quantity * UnitPrice)
            { Stop("购买99个恢复药需要29700金币。"); return; }
            BeginWalk(world, now);
        }
        else ChangeStage(Stage.City, now, "准备前往黄金港。");
    }

    public void Tick(WorldContext world, DateTimeOffset now)
    {
        if (!Active) return;
        try
        {
            if (now - began >= TimeSpan.FromMinutes(5)) { Stop("常用功能超时。"); return; }
            var loading = conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51];
            if (world.PlayerId != origin.PlayerId && !(loading && world.PlayerId == 0))
            { Stop("角色变化，已停止。"); return; }
            if (loading || objects.LocalPlayer == null)
            {
                if (!Transferring && stage is not (Stage.City or Stage.Pier or Stage.Enter)) Stop("地图变化，已停止。");
                return;
            }
            var player = objects.LocalPlayer;
            if (!client.IsLoggedIn || player.IsDead || conditions[ConditionFlag.WatchingCutscene])
            { Stop("角色状态不允许继续。"); return; }
            if (conditions[ConditionFlag.InCombat] && stage != Stage.Walk) return;
            if (stage == Stage.Enter && world.Territory == destinationIsland)
            { Stop($"已进入{islandName}。"); return; }
            if (purchase && world != origin)
            { Stop("副本或地图已变化。"); return; }
            if (stage != Stage.City && world.Territory != (purchase ? origin.Territory : Kugane))
            { Stop("进入了非预期地图。"); return; }
            if (player.IsCasting && stage is Stage.City or Stage.Pier or Stage.Interact) return;

            switch (stage)
            {
                case Stage.City:
                    if (world.Territory == Kugane)
                    {
                        ownsLifestream = false;
                        ChangeStage(Stage.Pier, now, "前往第一防波堤。");
                    }
                    else if (!ownsLifestream)
                    {
                        if (world.Territory != origin.Territory || LifestreamBusy())
                        { Stop("传送条件不满足，或Lifestream正在处理其他任务。"); return; }
                        if (!plugin.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport").InvokeFunc(111, 0))
                        { Stop("无法传送黄金港，请检查解锁状态。"); return; }
                        ownsLifestream = true;
                        Status = "等待黄金港传送完成。";
                    }
                    else if (now - stageAt >= TimeSpan.FromSeconds(60)) Stop("传送黄金港超时。");
                    break;
                case Stage.Pier:
                    if (NavigationRules.Distance(player.Position, RodneyPosition) <= 35 && (!ownsLifestream || !LifestreamBusy()))
                    {
                        ownsLifestream = false;
                        BeginWalk(world, now);
                    }
                    else if (!requestedPier)
                    {
                        if (LifestreamBusy()) { Stop("Lifestream正在处理其他任务。"); return; }
                        plugin.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction("第一防波堤");
                        requestedPier = ownsLifestream = true;
                    }
                    else if (now - stageAt >= TimeSpan.FromSeconds(45)) Stop("第一防波堤传送未确认。");
                    break;
                case Stage.Walk:
                    if (purchase)
                    {
                        islandWalk.Tick(world, player.Position, !HasDialog() || islandWalk.Interacting || islandWalk.Transferring,
                            false, conditions[ConditionFlag.Mounted], player.IsCasting, [], now, conditions[ConditionFlag.InCombat]);
                        if (islandWalk.Active) { Status = islandWalk.Status; break; }
                        if (islandWalk.TakeArrival() == null) { Stop(islandWalk.Status); return; }
                    }
                    else
                    {
                        walk.Tick(world, player.Position, !HasDialog(), [], now);
                        if (walk.Active) break;
                        if (walk.TakeArrival() == null) { Stop(walk.Status); return; }
                    }
                    ChangeStage(Stage.Interact, now, "已到达，正在确认目标NPC。");
                    break;
                case Stage.Interact:
                    if (now - stageAt > TimeSpan.FromSeconds(12)) { Stop("未在交互距离内找到目标NPC。"); return; }
                    var npc = FindNpc(player.Position);
                    if (npc == null || Vector3.Distance(npc.Position, player.Position) > 4) break;
                    if (!purchase && destinationIsland == 0) { Stop("已到达罗德尼面前。"); return; }
                    if (conditions[ConditionFlag.Mounted])
                    {
                        if (now >= nextDismount)
                        {
                            mounts.Dismount();
                            nextDismount = now.AddSeconds(3);
                        }
                        break;
                    }
                    if (HasDialog() || TargetSystem.Instance() == null) { Stop("已有其他对话。"); return; }
                    var target = TargetSystem.Instance();
                    target->SetHardTarget((GameObject*)npc.Address);
                    if ((ulong)target->GetTargetObjectId() != npc.GameObjectId) { Stop("无法确认选中的NPC。"); return; }
                    actor = npc.GameObjectId;
                    target->InteractWithObject((GameObject*)npc.Address);
                    ChangeStage(Stage.Menu, now, "等待NPC菜单。");
                    break;
                case Stage.Menu:
                    if (now - stageAt > TimeSpan.FromSeconds(20)) { Stop("NPC对话超时。" + DialogueDetail()); return; }
                    if (!OwnTarget()) { Stop("NPC目标已改变。"); return; }
                    if (now < nextDialogueAction) break;
                    if (purchase)
                    {
                        if (Addon("Shop") is not null) { Buy(now); break; }
                        if (!confirmed && TrySelectOption(text => text is "购买" or "购买道具" or "购买物品" or "购买消耗品"))
                        {
                            confirmed = true;
                            nextDialogueAction = now.AddMilliseconds(900);
                            Status = "已选择购买，等待商店打开。";
                            break;
                        }
                    }
                    else if (TrySelectOption(IsIslandOption, () => CheckPagosEntry(now)))
                    {
                        confirmed = false;
                        nextDialogueAction = now.AddMilliseconds(600);
                        ChangeStage(Stage.Enter, now, $"已选择{islandName}，等待进岛。");
                    }
                    else if (Active && !openedIslandMenu && TrySelectOption(text => text.Trim() is "进入禁地优雷卡" or "前往禁地优雷卡"))
                    {
                        openedIslandMenu = true;
                        stageAt = now;
                        nextDialogueAction = now.AddMilliseconds(900);
                        break;
                    }
                    if (Active && stage == Stage.Menu && AdvanceTalk(purchase ? VendorName : "罗德尼"))
                        nextDialogueAction = now.AddMilliseconds(400);
                    break;
                case Stage.Enter:
                    if (now - stageAt >= TimeSpan.FromSeconds(90)) { Stop("未确认进入所选岛屿。" + DialogueDetail()); return; }
                    if (now < nextDialogueAction) break;
                    if (!confirmed && ConfirmPrompt(text => text.Contains(islandName, StringComparison.Ordinal)))
                    {
                        confirmed = true;
                        nextDialogueAction = now.AddMilliseconds(900);
                        break;
                    }
                    if (!commenced && ConfirmDuty()) { commenced = true; break; }
                    if (OwnTarget() && AdvanceTalk("罗德尼")) nextDialogueAction = now.AddMilliseconds(400);
                    break;
                case Stage.Purchase:
                    VerifyPurchase(now);
                    break;
            }
        }
        catch (Exception ex) { Stop("常用功能失败：" + ex.GetBaseException().Message); }
    }

    private void BeginWalk(WorldContext world, DateTimeOffset now)
    {
        var player = objects.LocalPlayer!;
        var npc = FindNpc(player.Position);
        var position = npc?.Position ?? RodneyPosition;
        if (purchase && npc == null)
        {
            var map = data.GetExcelSheet<Lumina.Excel.Sheets.Map>().GetRow(world.Map);
            position = MapProjection.MapToWorld(Vendors[world.Territory],
                new(world.Territory, world.Map, "", "", map.SizeFactor, map.OffsetX, map.OffsetY, []));
        }
        if (npc != null && Vector3.Distance(player.Position, npc.Position) <= 3)
        { ChangeStage(Stage.Interact, now, "确认目标NPC。"); return; }
        var point = NavigationRules.StandOff(player.Position, position, 1.5f);
        var destination = new MapDestination(world.Territory, world.Map, point, purchase ? VendorName : "罗德尼");
        if (purchase)
        {
            islandWalk.Start(destination, world, player.Position, [], now,
                waitWithoutFate: false, arrivalRadius: 1.5f, unknownHeight: npc == null);
            if (!islandWalk.Active) { Stop(islandWalk.Status); return; }
        }
        else
        {
            walk.Start(destination, world, player.Position, [], now, false, arrivalRadius: 1.5f, unknownHeight: false);
            if (!walk.Active) { Stop(walk.Status); return; }
        }
        ChangeStage(Stage.Walk, now, purchase ? "前往补给负责人。" : "前往罗德尼。");
    }

    private IGameObject? FindNpc(Vector3 player) => objects
        .Where(o => o.Address != 0 && o.IsTargetable && o.ObjectKind == ObjectKind.EventNpc
            && (purchase ? o.Name.TextValue == VendorName : o.BaseId == Rodney && o.Name.TextValue == "罗德尼"))
        .MinBy(o => Vector3.DistanceSquared(o.Position, player));

    private bool OwnTarget() => actor != 0 && TargetSystem.Instance() != null
        && (ulong)TargetSystem.Instance()->GetTargetObjectId() == actor;

    private bool LifestreamBusy() => plugin.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();

    private bool CheckPagosEntry(DateTimeOffset now)
    {
        if (!CheckPagosRing || destinationIsland != Pagos) return true;
        if (pagosWeather == null)
        {
            Stop("无法读取恒冰天气，已停止自动参加。");
            return false;
        }

        var until = now.AddHours(3);
        var periods = (int)((until - WeatherForecastHelper.PeriodStart(now)).TotalSeconds
            / WeatherForecastHelper.PeriodSeconds) + 1;
        var fog = WeatherForecastHelper.GetForecast(pagosWeather, now, periods)
            .FirstOrDefault(period => period.WeatherId == Fog);
        if (fog == null) return true;

        var inventory = InventoryManager.Instance();
        var unread = false;
        foreach (var type in CarriedRingContainers)
        {
            var container = inventory == null ? null : inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded || container->Items == null || container->Size is < 1 or > 1000)
            {
                unread = true;
                continue;
            }
            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null || item->GetBaseItemId() != Blitzring || item->GetQuantity() == 0) continue;
                var timing = fog.Start <= now ? "当前为薄雾" : $"预计 {fog.Start.ToLocalTime():HH:mm} 出现薄雾";
                var message = $"恒冰之地{timing}，检测到你携带或装备了闪袭指环，已取消自动参加。";
                Stop(message);
                notify(message);
                return false;
            }
        }
        if (!unread) return true;
        Stop("恒冰之地三小时内有薄雾，但无法确认随身库存，已停止自动参加。");
        return false;
    }

    private bool IsIslandOption(string text)
    {
        text = text.Trim().TrimEnd('。', '！', '？');
        foreach (var prefix in new[] { "进入", "前往", "参加", "探索" })
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            text = text[prefix.Length..].TrimStart();
            break;
        }
        // Rodney wraps the full duty name in quotation marks after “进入”.
        if (text.Length >= 2 && (text[0], text[^1]) is ('“', '”') or ('"', '"') or ('「', '」'))
            text = text[1..^1].Trim();
        const string title = "禁地优雷卡";
        if (text.StartsWith(title, StringComparison.Ordinal))
            text = text[title.Length..].TrimStart(' ', '　', ':', '：', '·');
        return text == islandName;
    }

    private AtkUnitBase* Addon(string name)
    {
        var addon = gui.GetAddonByName(name);
        if (!addon.IsNull && addon.IsVisible && addon.IsReady) return (AtkUnitBase*)addon.Address;
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return null;
        var units = &manager->AllLoadedUnitsList;
        for (var i = 0; i < Math.Min((int)units->Count, units->Entries.Length); i++)
        {
            var unit = units->Entries[i].Value;
            if (unit != null && unit->IsVisible && unit->IsReady && unit->NameString == name) return unit;
        }
        return null;
    }

    private bool HasDialog() => Addon("SelectString") != null || Addon("SelectIconString") != null || Addon("SelectYesno") != null
        || Addon("Talk") != null || Addon("Shop") != null || Addon("ContentsFinderConfirm") != null;

    private bool TrySelectOption(Func<string, bool> matches, Func<bool>? canSubmit = null)
    {
        if (Addon("SelectYesno") != null || Addon("ContentsFinderConfirm") != null) return false;
        var menu = Addon("SelectString");
        var iconMenu = Addon("SelectIconString");
        if ((menu == null) == (iconMenu == null)) return false;
        var popup = menu != null ? &((AddonSelectString*)menu)->PopupMenu.PopupMenu
            : &((AddonSelectIconString*)iconMenu)->PopupMenu.PopupMenu;
        if (popup->EntryNames == null || popup->List == null || popup->EntryCount is < 1 or > 32) return false;
        var selected = -1;
        for (var i = 0; i < popup->EntryCount; i++)
        {
            if (!matches(SeString.Parse(popup->EntryNames[i].AsSpan()).TextValue.Trim())) continue;
            if (selected >= 0) return false;
            selected = i;
        }
        return selected >= 0 && !popup->List->GetItemDisabledState(selected) && (canSubmit == null || canSubmit())
            && Callback(menu != null ? menu : iconMenu, [selected]);
    }

    private bool AdvanceTalk(string speaker)
    {
        if (Addon("SelectString") != null || Addon("SelectIconString") != null || Addon("SelectYesno") != null
            || Addon("Shop") != null || Addon("ContentsFinderConfirm") != null) return false;
        var talk = Addon("Talk");
        var stage = AtkStage.Instance();
        if (talk == null || stage == null || stage->AtkEventTarget.VirtualTable == null
            || !VisibleText(talk).Contains(speaker)) return false;
        AtkEvent dialogue = default;
        dialogue.Listener = &talk->AtkEventListener;
        dialogue.Target = &stage->AtkEventTarget;
        dialogue.State.StateFlags = (AtkEventStateFlags)0x84;
        AtkEventData eventData = default;
        var id = talk->Id;
        foreach (var type in new[] { AtkEventType.MouseDown, AtkEventType.MouseClick, AtkEventType.MouseUp })
        {
            var current = Addon("Talk");
            if (current != talk || current->Id != id) break;
            current->ReceiveEvent(type, 0, &dialogue, &eventData);
        }
        return true;
    }

    private static List<string> VisibleText(AtkUnitBase* unit)
    {
        var result = new List<string>();
        var nodes = &unit->UldManager;
        if (nodes->NodeList == null) return result;
        for (var i = 0; i < Math.Min((int)nodes->NodeListCount, 512); i++)
        {
            var node = nodes->NodeList[i];
            if (node == null || node->Type != NodeType.Text || !node->IsVisible()) continue;
            var ancestor = node->ParentNode;
            for (var depth = 0; ancestor != null && ancestor->IsVisible() && depth < 64; depth++)
                ancestor = ancestor->ParentNode;
            if (ancestor != null) continue;
            var text = SeString.Parse(((AtkTextNode*)node)->NodeText.AsSpan()).TextValue.Trim();
            if (text.Length > 0) result.Add(text);
        }
        return result;
    }

    private string DialogueDetail()
    {
        foreach (var name in new[] { "SelectYesno", "ContentsFinderConfirm", "SelectIconString", "SelectString", "Talk", "Shop" })
        {
            var unit = Addon(name);
            if (unit != null) return $" 当前窗口 {name}：{string.Join(" / ", VisibleText(unit).Take(8))}";
        }
        return "";
    }

    private bool ConfirmPrompt(Func<string, bool> matches)
    {
        var confirm = (AddonSelectYesno*)Addon("SelectYesno");
        return confirm != null && confirm->PromptText != null
            && matches(SeString.Parse(confirm->PromptText->NodeText.AsSpan()).TextValue)
            && ClickButton((AtkUnitBase*)confirm, confirm->YesButton);
    }

    private bool ConfirmDuty()
    {
        var addon = (AddonContentsFinderConfirm*)Addon("ContentsFinderConfirm");
        return addon != null && VisibleText((AtkUnitBase*)addon).Any(text => text.Contains(islandName, StringComparison.Ordinal))
            && ClickButton((AtkUnitBase*)addon, addon->CommenceButton);
    }

    private static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || button->OwnerNode == null || !button->IsEnabled || !button->OwnerNode->AtkResNode.IsVisible()) return false;
        var entry = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var i = 0; entry != null && i < 32; i++, entry = entry->NextEvent)
        {
            if (entry->State.EventType != AtkEventType.ButtonClick || entry->Listener != &addon->AtkEventListener
                || entry->Target == null || entry->Target->VirtualTable == null || entry->Param > int.MaxValue) continue;
            addon->ReceiveEvent(AtkEventType.ButtonClick, (int)entry->Param, entry);
            return true;
        }
        return false;
    }

    private static bool Callback(AtkUnitBase* addon, ReadOnlySpan<int> args)
    {
        var values = stackalloc AtkValue[args.Length];
        for (var i = 0; i < args.Length; i++) values[i].SetInt(args[i]);
        return addon->FireCallback((uint)args.Length, values, true);
    }

    private void Buy(DateTimeOffset now)
    {
        var shop = Addon("Shop");
        var proxy = ShopEventHandler.AgentProxy.Instance();
        var inventory = InventoryManager.Instance();
        if (shop == null || proxy == null || proxy->Handler == null || proxy->AddonId != shop->Id
            || inventory == null || Addon("SelectYesno") != null)
        { Stop("无法确认当前补给商店。"); return; }
        var handler = proxy->Handler;
        var matches = new List<int>();
        if (handler->VisibleItemsCount is < 1 or > 60 || handler->ItemsCount is < 1 or > 60 || handler->BuybackTabActive)
        { Stop("无法确认商店商品列表。"); return; }
        for (var i = 0; i < handler->VisibleItemsCount; i++)
        {
            var index = handler->VisibleItems[i];
            if (index < 0 || index >= handler->ItemsCount) continue;
            var item = handler->Items[index];
            if (item.ItemId == Potion && item.PriceBuy == UnitPrice && !item.IsHQ) matches.Add(i);
        }
        gilBefore = inventory->GetGil();
        inventoryBefore = inventory->GetInventoryItemCount(Potion);
        if (matches.Count != 1 || gilBefore < Quantity * UnitPrice)
        { Stop("恢复药售价、商品条目或金币不足，已停止购买。"); return; }
        purchaseShopId = shop->Id;
        if (!Callback(shop, [0, matches[0], Quantity])) { Stop("购买指令未提交。"); return; }
        confirmed = false;
        ChangeStage(Stage.Purchase, now, "已提交购买99个恢复药，等待验证。");
    }

    private void VerifyPurchase(DateTimeOffset now)
    {
        var inventory = InventoryManager.Instance();
        if (inventory != null && inventory->GetInventoryItemCount(Potion) - inventoryBefore == Quantity
            && (long)gilBefore - inventory->GetGil() == Quantity * UnitPrice)
        { Stop("已购买99个优雷卡恢复药。"); return; }
        if (now - stageAt >= TimeSpan.FromSeconds(15)) { Stop("未确认购买结果，请查看背包和商店。" + DialogueDetail()); return; }
        if (confirmed) return;
        var proxy = ShopEventHandler.AgentProxy.Instance();
        if (proxy == null || proxy->AddonId != purchaseShopId || proxy->Handler == null || proxy->Handler->TransactionItemId != Potion
            || proxy->Handler->TransactionItemCount != Quantity) return;
        if (ConfirmPrompt(text => text.Contains("优雷卡恢复药", StringComparison.Ordinal)
            && text.Contains("99", StringComparison.Ordinal))) confirmed = true;
    }

    private void ChangeStage(Stage next, DateTimeOffset now, string status)
    { stage = next; stageAt = now; Status = status; }

    public void Stop(string status)
    {
        Active = false;
        walk.Stop();
        if (islandWalk.Active) islandWalk.Stop();
        if (ownsLifestream)
        {
            try { if (LifestreamBusy()) plugin.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
            catch { }
        }
        ownsLifestream = false;
        Status = status;
    }

    public void Dispose() { Stop("插件卸载。"); walk.Dispose(); islandWalk.Dispose(); }
}
