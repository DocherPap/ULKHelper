using System.Numerics;
using ULKHelper.Bunnies;
using ULKHelper.Fates;
internal static class BunnyChecks
{
    public static void Run(Action<bool,string> check,IReadOnlyList<IslandMap> islands,Lumina.GameData game)
    {
        check(BunnyRules.CanRecordHint(true,57,TimeSpan.FromMilliseconds(20)),"兔子：仅用状态内新鲜位置接收系统提示");
        check(!BunnyRules.CanRecordHint(true,10,TimeSpan.Zero)&&!BunnyRules.CanRecordHint(true,56,TimeSpan.Zero),"兔子：玩家频道或默语仿造文本不进入筛选");
        check(!BunnyRules.CanRecordHint(false,57,TimeSpan.Zero)&&!BunnyRules.CanRecordHint(true,57,TimeSpan.FromSeconds(1)),"兔子：状态外和过期位置不接收提示");
        var points=BunnyCatalog.Load();
        check(points.Length==142&&points.Count(p=>p.Territory==763)==54&&points.Count(p=>p.Territory==795)==58&&points.Count(p=>p.Territory==827)==30,"兔子：三岛142个固定点位全部导入");
        check(points.All(p=>p.Territory!=732)&&points.Select(p=>p.Id).Distinct().Count()==142,"兔子：常风无挖宝点、点位ID唯一");
        check(points.All(p=>p.X is >=1 and <=42&&p.Y is >=1 and <=42),"兔子：所有参考坐标在地图范围内");
        foreach(var map in islands.Where(m=>m.Territory!=732))
            check(points.Where(p=>p.Territory==map.Territory).All(p=>Vector2.Distance(MapProjection.WorldToMap(MapProjection.MapToWorld(new(p.X,p.Y),map),map),new(p.X,p.Y))<.001f),map.Name+"兔点投影往返含丰水偏移");
        check(BunnyRules.Parse("在非常遥远的地方……")==BunnyDistance.FarAway&&BunnyRules.Parse("在有些遥远的地方……")==BunnyDistance.Far,"兔子：用户远距离提示");
        check(BunnyRules.Parse("在比较近的地方……")==BunnyDistance.Near&&BunnyRules.Parse("在非常近的地方！")==BunnyDistance.VeryNear&&BunnyRules.Parse("就在这附近！")==BunnyDistance.Here,"兔子：用户近距离提示");
        check(BunnyRules.Parse("财宝好像是在东北方向很近的地方！")==BunnyDistance.VeryNear&&BunnyRules.Parse("财宝好像是在北方向不远的地方！")==BunnyDistance.Near,"兔子：实际国服近距离文本");
        check(BunnyRules.Parse("财宝好像是在西方向稍远的地方！")==BunnyDistance.Far&&BunnyRules.Parse("财宝好像是在东方向很远的地方！")==BunnyDistance.FarAway,"兔子：实际国服远距离文本");
        check(BunnyRules.Parse("发现了财宝！！")==BunnyDistance.Here&&BunnyRules.Parse("这不是线索")==null,"兔子：发现财宝和未知提示");
        var now=DateTimeOffset.UtcNow;
        foreach(var (band,min,max) in new[]{(BunnyDistance.Here,0f,5f),(BunnyDistance.VeryNear,5f,30f),(BunnyDistance.Near,30f,80f),(BunnyDistance.Far,80f,150f),(BunnyDistance.FarAway,150f,float.PositiveInfinity)})
        {
            var clue=new BunnyClue(Vector3.Zero,band,"测试",now);
            check(BunnyRules.Matches(new(min,900,0),clue,0)&&(!float.IsFinite(max)||BunnyRules.Matches(new(max,0,0),clue,0)),"兔子距离边界 "+band);
        }
        var names=new[]{"北","东北","东","东南","南","西南","西","西北"};
        for(var i=0;i<8;i++)
        {
            var direction=(BunnyDirection)i;
            check(BunnyRules.ParseDirection("财宝好像是在"+names[i]+"方向很远的地方！")==direction,"兔子八方向解析 "+names[i]);
            var angle=i*MathF.PI/4;
            var target=new Vector3(MathF.Sin(angle)*100,0,-MathF.Cos(angle)*100);
            check(BunnyRules.MatchesDirection(target,Vector3.Zero,direction,0)&&!BunnyRules.MatchesDirection(-target,Vector3.Zero,direction,0),"兔子地图轴及反向排除 "+names[i]);
        }
        check(BunnyRules.ParseDirection("正北方向")==BunnyDirection.North&&BunnyRules.ParseDirection("西侧")==BunnyDirection.West&&BunnyRules.ParseDirection("无方位提示")==null,"兔子：正方向、侧方与未知文本");
        Vector3 Bearing(float degrees)=>new(MathF.Sin(degrees*MathF.PI/180)*100,0,-MathF.Cos(degrees*MathF.PI/180)*100);
        check(BunnyRules.MatchesDirection(Bearing(22.5f),Vector3.Zero,BunnyDirection.North,0)&&BunnyRules.MatchesDirection(Bearing(-22.5f),Vector3.Zero,BunnyDirection.North,0)&&!BunnyRules.MatchesDirection(Bearing(23),Vector3.Zero,BunnyDirection.North,0),"兔子：扇区两侧边界和正北角度环绕");
        check(BunnyRules.MatchesDirection(Bearing(25),Vector3.Zero,BunnyDirection.North)&&!BunnyRules.MatchesDirection(Bearing(40),Vector3.Zero,BunnyDirection.North),"兔子：方位边界6米容差且不扩展到相邻中心");
        check(BunnyRules.MatchesDirection(new(3,0,0),Vector3.Zero,BunnyDirection.North)&&!BunnyRules.MatchesDirection(new(float.NaN,0,0),Vector3.Zero,BunnyDirection.North),"兔子：近处方位容差与无效坐标");
        var northClue=new BunnyClue(Vector3.Zero,BunnyDistance.Far,"北方向稍远的地方",now,BunnyDirection.North);
        check(BunnyRules.Matches(new(0,0,-100),northClue)&&!BunnyRules.Matches(new(100,0,0),northClue)&&!BunnyRules.Matches(new(0,0,-200),northClue),"兔子：方向和距离同时满足而非二选一");
        check(BunnyRules.Matches(new(3,0,0),northClue with {Distance=BunnyDistance.Here}),"兔子：就在附近不依赖方位");
        var map0=islands.Single(m=>m.Territory==827);
        var compass=new BunnySearch([new(827,"N",20,18),new(827,"E",22,20),new(827,"S",20,22),new(827,"W",18,20)]);
        var centre=MapProjection.MapToWorld(new(20,20),map0);
        compass.Begin(map0);compass.Add(new(centre,BunnyDistance.Far,"财宝好像是在北方向稍远的地方！",now));
        check(compass.Candidates.SetEquals(new[]{"N"})&&compass.Clues[0].Direction==BunnyDirection.North,"兔子：同距离四点按文本方向仅选正北");
        compass.Add(new(centre,BunnyDistance.Far,"财宝好像是在东方向稍远的地方！",now.AddSeconds(1)));
        check(compass.Conflict&&compass.Unique()==null,"兔子：连续方位矛盾不插旗");
        compass.Undo();check(compass.Candidates.SetEquals(new[]{"N"}),"兔子：撤销重算方向和距离的交集");
        var fake=new[]{new BunnyPoint(827,"A",10,10),new BunnyPoint(827,"B",12,10),new BunnyPoint(827,"C",14,10),new BunnyPoint(763,"D",10,10)};
        var model=new BunnySearch(fake);model.Begin(map0);
        check(model.Candidates.Count==3&&model.Unique()==null,"兔子：开始只取本岛且未记录时不插旗");
        var a=MapProjection.MapToWorld(new(10,10),map0);
        model.Add(new(a,BunnyDistance.Far,"第一条",now));
        check(model.Candidates.SetEquals(new[]{"B"}),"兔子：按当前位置和距离筛选唯一点");
        var unique=model.Unique();check(unique?.Id=="B","兔子：唯一候选可提交旗标");model.Marked(unique!);
        check(model.Unique()==null,"兔子：同轮相同唯一点不会重复插旗");
        model.Add(new(a,BunnyDistance.Here,"矛盾线索",now.AddSeconds(1)));
        check(model.Conflict&&model.Candidates.Count==0&&model.Unique()==null,"兔子：矛盾线索保持空集并禁止猜测旗标");
        model.Undo();check(!model.Conflict&&model.Candidates.SetEquals(new[]{"B"}),"兔子：撤销重算所有有效线索");
        model.End();var before=model.Clues.Count;check(!model.Add(new(a,BunnyDistance.Here,"结束后",now.AddSeconds(2)))&&model.Clues.Count==before,"兔子：状态结束后不再监听");
        check(!model.Active&&model.Clues.Count==0&&model.Flagged==null&&!model.Conflict&&model.Candidates.SetEquals(new[]{"A","B","C"})&&model.Unique()==null,"兔子：状态结束清空线索及旗标记录并恢复本岛全部候选");
        model.Begin(map0);model.Add(new(a,BunnyDistance.Here,"下一轮",now.AddSeconds(3)));
        check(model.Candidates.SetEquals(new[]{"A"})&&model.Unique()?.Id=="A","兔子：下一轮仅使用新线索");
        model.Add(new(a,BunnyDistance.Far,"新矛盾",now.AddSeconds(4)));model.End();
        check(!model.Conflict&&model.Clues.Count==0&&model.Candidates.Count==3,"兔子：矛盾状态结束也恢复全部点位");
        model.Clear();check(model.Candidates.Count==0&&model.Clues.Count==0&&model.Flagged==null,"兔子：切区清除本轮记录和旗标所有权");
        model.Begin(map0);model.Add(new(a,BunnyDistance.Here,"重复",now));
        check(!model.Add(new(a,BunnyDistance.Here,"重复",now.AddMilliseconds(100))),"兔子：同条短时间重复消息去重");
        var uncertain=new BunnyClue(Vector3.Zero,BunnyDistance.VeryNear,"边界",now);
        check(BunnyRules.Matches(new(34,0,0),uncertain)&&!BunnyRules.Matches(new(37,0,0),uncertain),"兔子：地图取整保留6米误差而非过度排除");
        check(BunnyLootCatalog.All.Length==9&&BunnyLootCatalog.All.All(l=>l.Items.Distinct().Count()==l.Items.Length),"兔子：三岛三等级特色掉落分组完整");
        check(BunnyLootCatalog.All.SelectMany(l=>l.Items).All(id=>game.Excel.GetSheet<Lumina.Excel.Sheets.Item>()!.GetRow(id).Name.ExtractText().Length>0),"兔子：全部特色物品ID有本机中文名称");
        check(game.Excel.GetSheet<Lumina.Excel.Sheets.Status>()!.GetRow(1531).Name.ExtractText()=="指引财宝","兔子：本机状态1531核实");
        check(Enumerable.Range(9002,5).All(id=>game.Excel.GetSheet<Lumina.Excel.Sheets.LogMessage>()!.GetRow((uint)id).LogKind.RowId==57),"兔子：本机提示频道57核实");
    }
}
