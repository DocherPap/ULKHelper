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
    private readonly LogosCatalog catalog;
    private ulong character;
    private DateTimeOffset next;
    private Dictionary<uint,int>? memories;
    private volatile LogosOwnership snapshot=LogosOwnership.Empty;
    public LogosOwnership Snapshot=>snapshot;
    public LogosInventoryReader(IFramework framework,IObjectTable objects,IPlayerState player,IGameGui gui,LogosCatalog catalog)
    {this.framework=framework;this.objects=objects;this.player=player;this.gui=gui;this.catalog=catalog;framework.Update+=Tick;}
    private unsafe void Tick(IFramework _)
    {
        if(DateTimeOffset.UtcNow<next)return;next=DateTimeOffset.UtcNow.AddSeconds(1);
        if(player.ContentId!=character){character=player.ContentId;memories=null;snapshot=LogosOwnership.Empty;}
        if(character==0||objects.LocalPlayer==null){memories=null;snapshot=LogosOwnership.Empty;return;}
        try
        {
            var counts=new Dictionary<uint,int?>();
            var manager=InventoryManager.Instance();
            var bags=new Dictionary<uint,int>();var loaded=manager!=null;
            if(manager!=null)foreach(var type in new[]{InventoryType.Inventory1,InventoryType.Inventory2,InventoryType.Inventory3,InventoryType.Inventory4})
            {
                var bag=manager->GetInventoryContainer(type);
                if(bag==null||!bag->IsLoaded||bag->Items==null||bag->Size<0||bag->Size>1000){loaded=false;break;}
                for(var i=0;i<bag->Size;i++)
                {
                    var item=bag->GetInventorySlot(i);if(item==null){loaded=false;break;}
                    var id=item->GetBaseItemId();if(catalog.Shards.Any(s=>s.Item==id))bags[id]=bags.GetValueOrDefault(id)+(int)item->GetQuantity();
                }
            }
            foreach(var s in catalog.Shards)counts[s.Item]=loaded?bags.GetValueOrDefault(s.Item):null;
            var visible=gui.GetAddonByName("EurekaMagiciteItemShardList");
            var fresh=false;
            if(!visible.IsNull&&visible.IsVisible)
            {
                var atk=RaptureAtkModule.Instance();
                var array=atk==null?null:atk->AtkArrayDataHolder.GetNumberArrayData((int)NumberArrayType.EurekaLogosShardList);
                if(array!=null&&array->IntArray!=null)
                {
                    var parsed=LogosStockDecoder.Decode(array->Span,catalog.Shards.SelectMany(s=>s.Mnemes).ToHashSet());
                    if(parsed!=null)
                    {
                        var synthesis=gui.GetAddonByName("EurekaMagiciteItemSynthesis");
                        if(!synthesis.IsNull&&synthesis.IsVisible)
                        {
                            var unit=(AtkUnitBase*)synthesis.Address;
                            if(unit->AtkValuesCount>=21)for(var i=15;i<=20;i++)
                            {
                                var v=unit->AtkValues[i];
                                var id=v.Type==AtkValueType.UInt?v.UInt:v.Type==AtkValueType.Int&&v.Int>0?(uint)v.Int:0;
                                if(parsed.ContainsKey(id))parsed[id]++;
                            }
                        }
                        memories=parsed;fresh=true;
                    }
                }
            }
            foreach(var m in catalog.Shards.SelectMany(s=>s.Mnemes).Distinct())counts[m]=memories?.GetValueOrDefault(m);
            snapshot=new(counts,$"碎晶：{(loaded?"当前背包":"未获取")}；记忆：{(fresh?"本次已更新":memories!=null?"本次角色缓存":"请打开游戏文理碎晶列表获取")}；存储技能数量未获取。");
        }
        catch { snapshot=new(snapshot.Items,"库存暂不可读，显示本次角色上次快照；可重新打开文理碎晶列表。"); }
    }
    public void Dispose()=>framework.Update-=Tick;
}
