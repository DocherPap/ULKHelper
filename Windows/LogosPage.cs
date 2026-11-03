using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using ULKHelper.Logos;
using System.Numerics;

namespace ULKHelper.Windows;
internal sealed class LogosPage(IDataManager data, IObjectTable objects, LogosService service, SettingsStore? settings)
{
    private HashSet<uint> favorites = settings?.Get("logos.favorites",Array.Empty<uint>()).ToHashSet() ?? [];
    private int scope;
    private string search="";
    private string Item(uint id)=>data.GetExcelSheet<Lumina.Excel.Sheets.Item>()!.GetRow(id).Name.ExtractText();
    public void Draw()
    {
        var world=objects.LocalPlayer?.CurrentWorld.Value;
        service.Poll(world?.RowId??0);
        ImGui.TextWrapped($"当前服务器：{world?.Name.ExtractText()??"未登录"}　大区：{world?.DataCenter.Value.Name.ExtractText()??"未知"}　区域：国服");
        ImGui.BeginDisabled(service.Busy||world==null);
        if(ImGui.Button("查询 / 刷新价格"))service.Refresh(world?.RowId??0);
        ImGui.EndDisabled();ImGui.SameLine();ImGui.TextWrapped(service.Status);
        if(service.Updated is {} updated)ImGui.TextDisabled($"获取时间：{updated:MM-dd HH:mm:ss}");
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##LogosSearch","搜索文理技能或碎晶",ref search,100);
        if(ImGui.BeginTabBar("##LogosParts"))
        {
            if(ImGui.BeginTabItem("碎晶鉴定"))
            {
                foreach(var shard in service.Catalog.Shards.Where(s=>search.Length==0||Item(s.Item).Contains(search)||s.Mnemes.Any(m=>Item(m).Contains(search))))
                {
                    ImGui.Separator();ImGui.TextUnformatted(Item(shard.Item));
                    var p=service.Prices.GetValueOrDefault(shard.Item);
                    ImGui.TextWrapped($"本服 {Price(p?.World)}　大区 {Price(p?.Dc)}　国服 {Price(p?.Region)}");
                    ImGui.TextWrapped("可鉴定："+string.Join("、",shard.Mnemes.Select(Item)));
                }
                ImGui.EndTabItem();
            }
            if(ImGui.BeginTabItem("技能合成"))
            {
                ImGui.SetNextItemWidth(200);ImGui.Combo("成本价格范围",ref scope,new[]{"当前服务器","当前大区","国服"},3);
                ImGui.TextWrapped("成本为每份记忆按一颗来源碎晶估算的采购基准，未计随机鉴定损耗；不是保底或期望成本。无报价显示未知。");
                foreach(var action in service.Catalog.Actions.OrderByDescending(a=>favorites.Contains(a.Action)).ThenBy(a=>a.Number))
                {
                    var name=data.GetExcelSheet<Lumina.Excel.Sheets.Action>()!.GetRow(action.Action).Name.ExtractText();
                    if(search.Length>0&&!name.Contains(search))continue;
                    ImGui.PushID((int)action.Action);ImGui.Separator();
                    if(ImGui.SmallButton(favorites.Contains(action.Action)?"★":"☆"))
                    {if(!favorites.Add(action.Action))favorites.Remove(action.Action);settings?.Put("logos.favorites",favorites.ToArray());}
                    ImGui.SameLine();ImGui.TextUnformatted($"{action.Number:00} {name}");
                    foreach(var recipe in action.Recipes)
                    {
                        var cost=service.Cost(recipe,scope);
                        ImGui.TextWrapped(string.Join(" + ",recipe.GroupBy(i=>i).Select(g=>$"{Item(g.Key)} ×{g.Count()}"))+ $"　≈ {(cost is {} n?n.ToString("N0"):"未知")} 金币");
                    }
                    ImGui.PopID();
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }
    private static string Price(int? value)=>value is {} n?$"{n:N0} 金币":"未知";
}
