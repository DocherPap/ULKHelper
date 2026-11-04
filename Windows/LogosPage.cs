using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Logos;

namespace ULKHelper.Windows;
internal sealed record LogosWorld(uint Id,string Name,string Dc);
internal sealed class LogosPage(Func<uint,string> itemName, Func<uint,string> actionName, Func<LogosWorld?> currentWorld, LogosService service, SettingsStore? settings, Func<LogosOwnership>? ownership = null)
{
    private readonly HashSet<uint> favorites=settings?.Get("logos.favorites",Array.Empty<uint>()).ToHashSet()??[];
    private int scope;
    private int section=settings?.Get("logos.section",0)??0;
    private bool firstDraw=true;
    private string search="";
    private uint selectedShard,selectedAction;
    private LogosOwnership stock=LogosOwnership.Empty;
    private string Item(uint id)=>itemName(id);
    private string Owned(uint id)=>Item(id)+Quantity(stock.Items.GetValueOrDefault(id));
    private static string Quantity(int? n)=>n is {} count?$"（{count}）":"（未获取）";
    public void Draw()
    {
        var world=currentWorld();stock=ownership?.Invoke()??LogosOwnership.Empty;
        service.Poll(world?.Id??0);
        ImGui.TextWrapped($"当前服务器：{world?.Name??"未登录"}　大区：{world?.Dc??"未知"}　区域：国服");
        ImGui.BeginDisabled(service.Busy||world==null);
        if(ImGui.Button("查询 / 刷新价格"))service.Refresh(world?.Id??0);
        ImGui.EndDisabled();ImGui.SameLine();ImGui.TextWrapped(service.Status);
        if(service.Updated is {} updated)ImGui.TextDisabled($"获取时间：{updated:MM-dd HH:mm:ss}");
        ImGui.TextWrapped(stock.Status);
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##LogosSearch","搜索文理技能或碎晶",ref search,100);
        if(!ImGui.BeginTabBar("##LogosParts"))return;
        if(ImGui.BeginTabItem("碎晶鉴定",firstDraw&&section==0?ImGuiTabItemFlags.SetSelected:0)){section=0;Appraisal();ImGui.EndTabItem();}
        if(ImGui.BeginTabItem("技能合成",firstDraw&&section==1?ImGuiTabItemFlags.SetSelected:0)){section=1;Synthesis();ImGui.EndTabItem();}
        ImGui.EndTabBar();firstDraw=false;settings?.Put("logos.section",section);
    }
    private bool BeginColumns(string id)
    {
        LayoutSettings.RememberTable(id);
        if(!ImGui.BeginTable(id,2,ImGuiTableFlags.Resizable|ImGuiTableFlags.BordersInnerV))return false;
        ImGui.TableSetupColumn("选择",ImGuiTableColumnFlags.WidthStretch,.36f);
        ImGui.TableSetupColumn("详情",ImGuiTableColumnFlags.WidthStretch,.64f);
        ImGui.TableNextRow();ImGui.TableNextColumn();return true;
    }
    private void Appraisal()
    {
        var rows=service.Catalog.Shards.Where(s=>search.Length==0||Item(s.Item).Contains(search)||s.Mnemes.Any(m=>Item(m).Contains(search))).ToArray();
        if(!rows.Any(s=>s.Item==selectedShard))selectedShard=rows.FirstOrDefault()?.Item??0;
        if(!BeginColumns("##LogosAppraisalColumns"))return;
        if(ImGui.BeginChild("##ShardChoices",Vector2.Zero))foreach(var s in rows)
        {
            if(ImGui.Selectable(Owned(s.Item)+$"##Shard{s.Item}",selectedShard==s.Item))selectedShard=s.Item;
            var p=service.Prices.GetValueOrDefault(s.Item);
            ImGui.TextWrapped($"本服 {Price(p?.World)}\n大区 {Price(p?.Dc)}\n国服 {Price(p?.Region)}");ImGui.Separator();
        }
        ImGui.EndChild();ImGui.TableNextColumn();
        if(ImGui.BeginChild("##ShardResults",Vector2.Zero))
        {
            var s=rows.FirstOrDefault(s=>s.Item==selectedShard);
            if(s==null)ImGui.TextDisabled("无匹配碎晶。");
            else
            {
                ImGui.TextWrapped(Owned(s.Item)+" 可鉴定结果");
                Cards("##MnemeCards",s.Mnemes.Length,i=>ImGui.TextWrapped(Owned(s.Mnemes[i])));
            }
        }
        ImGui.EndChild();ImGui.EndTable();
    }
    private void Synthesis()
    {
        ImGui.SetNextItemWidth(180);ImGui.Combo("成本价格范围",ref scope,new[]{"当前服务器","当前大区","国服"},3);
        ImGui.TextWrapped("估价未计随机鉴定损耗。名称后数字为已读取记忆库存；合成后的技能存储数量未获取。");
        var rows=service.Catalog.Actions.Where(a=>search.Length==0||actionName(a.Action).Contains(search)).OrderByDescending(a=>favorites.Contains(a.Action)).ThenBy(a=>a.Number).ToArray();
        if(!rows.Any(a=>a.Action==selectedAction))selectedAction=rows.FirstOrDefault()?.Action??0;
        if(!BeginColumns("##LogosSynthesisColumns"))return;
        if(ImGui.BeginChild("##ActionChoices",Vector2.Zero))foreach(var a in rows)
        {
            ImGui.PushID((int)a.Action);
            if(ImGui.SmallButton(favorites.Contains(a.Action)?"★":"☆")){if(!favorites.Add(a.Action))favorites.Remove(a.Action);settings?.Put("logos.favorites",favorites.ToArray());}
            ImGui.SameLine();
            var memory=a.Recipes.FirstOrDefault(r=>r.Length==1);
            var count=memory==null?null:stock.Items.GetValueOrDefault(memory[0]);
            if(ImGui.Selectable($"{a.Number:00} {actionName(a.Action)}{Quantity(count)}",selectedAction==a.Action))selectedAction=a.Action;
            if(ImGui.IsItemHovered())ImGui.SetTooltip(memory==null?"未获取合成后的存储技能数量。":"括号为对应记忆库存，不包括已合成的存储技能。");
            ImGui.PopID();
        }
        ImGui.EndChild();ImGui.TableNextColumn();
        if(ImGui.BeginChild("##ActionRecipes",Vector2.Zero))
        {
            var a=rows.FirstOrDefault(a=>a.Action==selectedAction);
            if(a==null)ImGui.TextDisabled("无匹配技能。");
            else
            {
                ImGui.TextUnformatted($"{a.Number:00} {actionName(a.Action)} · {a.Recipes.Length} 种组合");
                var costs=a.Recipes.Select(r=>service.Cost(r,scope)).Where(c=>c!=null).ToArray();
                ImGui.TextWrapped(costs.Length==0?"暂无完整报价；下方列出每种组合缺少的报价。":$"已知报价中的最低采购基准：≈ {costs.Min():N0} 金币");
                Cards("##RecipeCards",a.Recipes.Length,i=>
                {
                    var recipe=a.Recipes[i];ImGui.TextUnformatted($"组合 {i+1}");
                    foreach(var g in recipe.GroupBy(m=>m))ImGui.TextWrapped($"{Owned(g.Key)} ×{g.Count()}");
                    var cost=service.Cost(recipe,scope);
                    ImGui.TextWrapped(cost is {} n?$"≈ {n:N0} 金币":"未知价格");
                    foreach(var missing in service.MissingPrices(recipe,scope))ImGui.TextWrapped("缺报价："+Item(missing));
                });
            }
        }
        ImGui.EndChild();ImGui.EndTable();
    }
    private static void Cards(string id,int count,Action<int> draw)
    {
        var cols=Math.Clamp((int)(ImGui.GetContentRegionAvail().X/220),1,3);
        if(!ImGui.BeginTable(id,cols,ImGuiTableFlags.SizingStretchSame|ImGuiTableFlags.BordersInner))return;
        for(var i=0;i<count;i++){ImGui.TableNextColumn();ImGui.PushID(i);draw(i);ImGui.Dummy(new(0,8));ImGui.PopID();}
        ImGui.EndTable();
    }
    private static string Price(int? n)=>n is {} value?$"{value:N0}":"未知";
}
