using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ULKHelper.Logos;
internal sealed class LogosInventoryReader : IDisposable
{
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IPlayerState player;
    private readonly IGameGui gui;
    private readonly HashSet<uint> shardIds, memoryIds, knownItems;
    private readonly LogosStockCache cache;
    private ulong character;
    private DateTimeOffset next;
    private volatile LogosOwnership snapshot=LogosOwnership.Empty;
    public LogosOwnership Snapshot=>snapshot;
    public LogosInventoryReader(IFramework framework, IObjectTable objects, IPlayerState player, IGameGui gui,
        LogosCatalog catalog, SettingsStore stockStore)
    {
        this.framework = framework; this.objects = objects; this.player = player; this.gui = gui;
        shardIds = catalog.Shards.Select(s => s.Item).ToHashSet();
        memoryIds = catalog.Shards.SelectMany(s => s.Mnemes).ToHashSet();
        knownItems = shardIds.Concat(memoryIds).ToHashSet();
        cache = new(stockStore, knownItems);
        framework.Update += Tick;
    }
    private unsafe void Tick(IFramework _)
    {
        var contentId = player.IsLoaded && objects.LocalPlayer != null ? player.ContentId : 0;
        if (contentId != character)
        {
            character = contentId;
            cache.Activate(character);
            snapshot = LogosOwnership.Empty;
            next = default;
        }
        var now = DateTimeOffset.UtcNow;
        if (now < next) return;
        next = now.AddSeconds(1);
        if (character == 0) { cache.Flush(); return; }
        try
        {
            var manager=InventoryManager.Instance();
            var bags=new Dictionary<uint,int>();var loaded=manager!=null;
            if(manager!=null)foreach(var type in new[]{InventoryType.Inventory1,InventoryType.Inventory2,InventoryType.Inventory3,InventoryType.Inventory4})
            {
                var bag=manager->GetInventoryContainer(type);
                if(bag==null||!bag->IsLoaded||bag->Items==null||bag->Size<0||bag->Size>1000){loaded=false;break;}
                for(var i=0;i<bag->Size;i++)
                {
                    var item=bag->GetInventorySlot(i);if(item==null){loaded=false;break;}
                    var id=item->GetBaseItemId();if(shardIds.Contains(id))bags[id]=bags.GetValueOrDefault(id)+(int)item->GetQuantity();
                }
            }
            if (loaded) cache.Update(shardIds.Select(id => KeyValuePair.Create(id, bags.GetValueOrDefault(id))));
            var visible=gui.GetAddonByName("EurekaMagiciteItemShardList");
            var fresh=false;
            if (!visible.IsNull && visible.IsVisible && visible.IsReady)
            {
                var atk=RaptureAtkModule.Instance();
                var array=atk==null?null:atk->AtkArrayDataHolder.GetNumberArrayData((int)NumberArrayType.EurekaLogosShardList);
                if(array!=null&&array->IntArray!=null)
                {
                    var parsed=LogosStockDecoder.Decode(array->Span,memoryIds);
                    if(parsed!=null)
                    {
                        var synthesis=gui.GetAddonByName("EurekaMagiciteItemSynthesis");
                        if (!synthesis.IsNull && synthesis.IsVisible && synthesis.IsReady)
                        {
                            var unit=(AtkUnitBase*)synthesis.Address;
                            if (unit->AtkValues != null && unit->AtkValuesCount >= 21) for(var i=15;i<=20;i++)
                            {
                                var v=unit->AtkValues[i];
                                var id=v.Type==AtkValueType.UInt?v.UInt:v.Type==AtkValueType.Int&&v.Int>0?(uint)v.Int:0;
                                if(parsed.ContainsKey(id))parsed[id]++;
                            }
                        }
                        cache.Update(parsed);
                        fresh = true;
                    }
                }
            }
            Publish(loaded, fresh);
        }
        catch { Publish(false, false); }
        finally { cache.Flush(); }
    }

    private void Publish(bool loaded, bool fresh)
    {
        var counts = knownItems.ToDictionary(id => id, cache.Get);
        var shards = loaded ? "当前背包" : shardIds.Any(id => counts[id] != null) ? "本地缓存" : "未获取";
        var memories = fresh ? "本次已更新" : memoryIds.Any(id => counts[id] != null) ? "本地缓存" : "请打开游戏文理碎晶列表获取";
        snapshot = new(counts, $"碎晶：{shards}；记忆：{memories}；存储技能数量未获取。");
    }

    public void Dispose()
    {
        framework.Update -= Tick;
        cache.Flush(force: true);
    }
}
