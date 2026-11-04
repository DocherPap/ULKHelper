using System.Numerics;
using Dalamud.Bindings.ImGui;
using ULKHelper.Bunnies;
using ULKHelper.Fates;
using ULKHelper.Automation;

namespace ULKHelper.Windows;
internal sealed class BunnyPage(IBunnyTracker tracker,Func<uint,string> itemName,SettingsStore? settings=null)
{
    private bool showPoints=settings?.Get("bunny.showPoints",true)??true,showExcluded=settings?.Get("bunny.showExcluded",true)??true;
    private int manualHint,manualDirection;
    private long lastResetVersion=tracker.Snapshot.ResetVersion;
    private readonly BunnyPoint[] all=BunnyCatalog.Load();
    private readonly HashSet<ushort> selectedBunnyFates = [];
    private int digLimit = Math.Max(0, settings?.Get("bunny.digLimit", 0) ?? 0);
    public bool ConsumeReset()
    {
        if(lastResetVersion==tracker.Snapshot.ResetVersion)return false;
        lastResetVersion=tracker.Snapshot.ResetVersion;manualHint=manualDirection=0;return true;
    }
    public void DrawVisibilityToggle()
    {
        ImGui.Checkbox("挖宝点位",ref showPoints);
        settings?.Put("bunny.showPoints",showPoints);
    }
    public void DrawSummary(IslandMap map, IAutomationHelpers? automation = null)
    {
        var state=tracker.Snapshot;var points=all.Count(p=>p.Territory==map.Territory);
        var current=state.Territory==map.Territory;
        var bunnyFates = map.Fates.Where(f => f.Seed.Bunny).ToArray();
        var running = automation?.BunnyAutomationActive == true;
        if (automation != null && (bunnyFates.Length > 0 || running))
        {
            var selected = bunnyFates.Select(f => f.Seed.Id).Where(selectedBunnyFates.Contains).ToArray();
            var limitWidth = ImGui.GetFontSize() * 4;
            var limitLabelWidth = ImGui.CalcTextSize("挖个").X + ImGui.GetStyle().ItemSpacing.X * 3;
            ImGui.BeginDisabled(!running && (!current || selected.Length == 0));
            if (ImGui.Button(running ? "停止自动挖宝" : "自动挖宝",
                new Vector2(Math.Max(70, ImGui.GetContentRegionAvail().X - limitWidth - limitLabelWidth), 0)))
                automation.SetBunnyAutomation(map.Territory, selected, !running, digLimit);
            ImGui.EndDisabled();
            ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("挖");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(limitWidth);
            ImGui.BeginDisabled(running);
            if (ImGui.InputInt("##BunnyLimit", ref digLimit, 0, 0))
            {
                digLimit = Math.Max(0, digLimit);
                settings?.Put("bunny.digLimit", digLimit);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("0 表示不限数量。仅统计系统确认挖出的宝箱。");
            ImGui.SameLine(); ImGui.TextUnformatted("个");
            ImGui.BeginDisabled(running);
            for (var i = 0; i < bunnyFates.Length; i++)
            {
                var fate = bunnyFates[i];
                if (i > 0) ImGui.SameLine();
                var enabled = selectedBunnyFates.Contains(fate.Seed.Id);
                if (ImGui.Checkbox($"{fate.Level}级兔子##{fate.Seed.Id}", ref enabled))
                {
                    if (enabled) selectedBunnyFates.Add(fate.Seed.Id);
                    else selectedBunnyFates.Remove(fate.Seed.Id);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(fate.Name);
            }
            ImGui.EndDisabled();
            ImGui.TextUnformatted($"已挖 {automation.BunnyFoundCount}/{(digLimit == 0 ? "∞" : digLimit.ToString())} · {automation.BunnyAutomationStatus}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(automation.BunnyAutomationStatus);
        }
        else ImGui.TextUnformatted("自动挖宝（幸福兔）");
        if(points==0){ImGui.TextWrapped("本岛没有幸福兔挖宝点位。");return;}
        ImGui.TextUnformatted($"固定点位 {points} · 候选 {(current&&state.Clues.Count>0?state.Candidates.Count:points)}");
        if(state.Conflict)ImGui.PushStyleColor(ImGuiCol.Text,new Vector4(1,.4f,.35f,1));
        var status = current ? state.Status : "获得指引财宝后自动监听。";
        ImGui.TextUnformatted(status);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(status);
        if(state.Conflict)ImGui.PopStyleColor();
        ImGui.Checkbox("显示已排除点",ref showExcluded);ImGui.SameLine();
        ImGui.BeginDisabled(!current||!state.Active);
        if(ImGui.SmallButton("撤销上条"))tracker.Undo();ImGui.SameLine();if(ImGui.SmallButton("重置推断"))tracker.Reset();
        ImGui.EndDisabled();ImGui.SameLine();
        if(ImGui.SmallButton("线索 / 掉落"))ImGui.OpenPopup("##BunnyDetails");
        if(current&&state.Clues.LastOrDefault() is {} clue&&ImGui.IsItemHovered())ImGui.SetTooltip(clue.Text);
        ImGui.SetNextWindowSize(new Vector2(410,440),ImGuiCond.Appearing);
        if(ImGui.BeginPopup("##BunnyDetails"))
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextWrapped("胡萝卜提示按方向＋距离筛选。地图正北为基准，边界含6米容差；剩1点自动插旗。状态结束自动重置。");
            ImGui.BeginDisabled(!current||!state.Active);
            if(ImGui.CollapsingHeader("手动补记提示"))
            {
                ImGui.SetNextItemWidth(-1);ImGui.Combo("##BunnyHint",ref manualHint,new[]{"非常遥远：150米以上","有些遥远：80–150米","比较近：30–80米","非常近：5–30米","就在附近：5米以内"},5);
                ImGui.SetNextItemWidth(-1);ImGui.Combo("##BunnyDirection",ref manualDirection,new[]{"无方向（仅按距离）","北","东北","东","东南","南","西南","西","西北"},9);
                if(ImGui.Button("按当前位置记录"))tracker.Manual((BunnyDistance)manualHint,manualDirection==0?null:(BunnyDirection)(manualDirection-1));
            }
            ImGui.EndDisabled();
            if(current&&state.Clues.Count>0&&ImGui.CollapsingHeader($"本轮线索（{state.Clues.Count}）"))
                foreach(var c in state.Clues.Reverse())ImGui.TextWrapped($"{c.Time.ToLocalTime():HH:mm:ss} {c.Text}\n位置 {MapProjection.WorldToMap(c.Position,map).X:0.0}, {MapProjection.WorldToMap(c.Position,map).Y:0.0}");
            ImGui.Separator();ImGui.TextUnformatted("兔箱特色掉落");
            foreach(var type in Enum.GetValues<BunnyChest>())
            {
                ImGui.TextColored(type switch{BunnyChest.Bronze=>new Vector4(.8f,.55f,.3f,1),BunnyChest.Silver=>new Vector4(.8f,.85f,.9f,1),_=>new Vector4(1,.85f,.2f,1)},type switch{BunnyChest.Bronze=>"青铜宝箱",BunnyChest.Silver=>"白银宝箱",_=>"黄金宝箱"});
                foreach(var id in BunnyLootCatalog.All.First(l=>l.Territory==map.Territory&&l.Chest==type).Items)ImGui.TextWrapped("• "+itemName(id));
            }
            ImGui.TextWrapped("经典特色奖励；部分已有其他获取途径。橙点为候选，灰点为排除，绿点为唯一候选。");
            ImGui.PopTextWrapPos();ImGui.EndPopup();
        }
        settings?.Put("bunny.showPoints",showPoints);settings?.Put("bunny.showExcluded",showExcluded);
    }
    // Uses the FATE map projection, clipping, zoom and pan; no second map or input surface.
    public void DrawOverlay(IslandMap map,Vector2 pos,float side,float zoom,Vector2 uvMin,bool hover,bool dragged)
    {
        if(!showPoints)return;
        var state=tracker.Snapshot;var points=all.Where(p=>p.Territory==map.Territory).ToArray();
        Vector2 At(Vector2 p)=>pos+(MapProjection.ToUv(p,map.SizeFactor)-uvMin)*side*zoom;
        var draw=ImGui.GetWindowDrawList();var current=map.Territory==state.Territory;
        if(current&&state.Clues.LastOrDefault() is {} clue)
        {
            var origin=At(MapProjection.WorldToMap(clue.Position,map));var range=BunnyRules.Range(clue.Distance);
            var scale=map.SizeFactor/100f/2048*side*zoom;
            if(float.IsFinite(range.Max))draw.AddCircle(origin,(range.Max+BunnyRules.Tolerance)*scale,0xD0FFFF40,96,2);
            if(range.Min>BunnyRules.Tolerance)draw.AddCircle(origin,(range.Min-BunnyRules.Tolerance)*scale,0xD0FFFF40,96,2);
            if(clue.Direction is {} direction&&clue.Distance!=BunnyDistance.Here)
            {
                var angle=(int)direction*MathF.PI/4;var length=float.IsFinite(range.Max)?range.Max+BunnyRules.Tolerance:300;
                var spread=MathF.PI/8+MathF.Asin(Math.Min(1,BunnyRules.Tolerance/Math.Max(range.Min,30)));
                foreach(var edge in new[]{angle-spread,angle+spread})draw.AddLine(origin,origin+new Vector2(MathF.Sin(edge),-MathF.Cos(edge))*length*scale,0xD0FFFF40,2);
                draw.AddLine(origin,origin+new Vector2(MathF.Sin(angle),-MathF.Cos(angle))*length*scale,0xA0FFFF40,1);
            }
            draw.AddCircleFilled(origin,4,0xFFFFFF40);
        }
        BunnyPoint? tooltip=null;
        for(var i=0;i<points.Length;i++)
        {
            var p=points[i];var candidate=!current||state.Clues.Count==0||state.Candidates.Contains(p.Id);
            if(!candidate&&!showExcluded)continue;
            var mark=At(new(p.X,p.Y));var unique=current&&state.Candidates.Count==1&&state.Candidates.Contains(p.Id);
            var color=unique?0xFF55FF55u:candidate?0xFF20CFFFu:0x70888888u;
            draw.AddCircleFilled(mark,(unique?7:candidate?4:2)*.6f,color);
            if(candidate&&zoom>=2)draw.AddText(mark+new Vector2(7,-7),0xFFFFFFFF,(i+1).ToString());
            if(hover&&Vector2.Distance(mark,ImGui.GetIO().MousePos)<9)tooltip=p;
        }
        if(tooltip!=null&&!dragged){ImGui.BeginTooltip();ImGui.TextUnformatted($"参考挖宝点 ({tooltip.X:0.0}, {tooltip.Y:0.0})");ImGui.EndTooltip();}
    }
}
