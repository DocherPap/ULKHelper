using System.Numerics;
using System.Text.Json;
using ULKHelper.Fates;

namespace ULKHelper.Bunnies;
internal sealed record BunnyPoint(uint Territory,string Id,float X,float Y,float? Height = null);
internal enum BunnyDirection { North, NorthEast, East, SouthEast, South, SouthWest, West, NorthWest }
internal enum BunnyDistance { FarAway, Far, Near, VeryNear, Here }
internal sealed record BunnyClue(Vector3 Position,BunnyDistance Distance,string Text,DateTimeOffset Time,BunnyDirection? Direction=null);
internal sealed record BunnySnapshot(uint Territory,bool Active,Vector3? Player,IReadOnlyList<BunnyPoint> Points,IReadOnlySet<string> Candidates,
    IReadOnlyList<BunnyClue> Clues,string Status,bool Conflict=false,string? Flagged=null,long ResetVersion=0,DateTimeOffset? LastTreasureAt=null);
internal interface IBunnyTracker
{
    BunnySnapshot Snapshot{get;}
    IReadOnlyList<BunnyPoint> Catalog => [];
    void Undo();void Reset();void Manual(BunnyDistance distance,BunnyDirection? direction=null);
}
internal static class BunnyCatalog
{
    public static BunnyPoint[] Load()
    {
        using var data=typeof(BunnyCatalog).Assembly.GetManifestResourceStream("ULKHelper.Bunnies.points.json")!;
        return JsonSerializer.Deserialize<BunnyPoint[]>(data)!;
    }
}
internal static class BunnyCalibration
{
    public static BunnyPoint? Match(IslandMap map, IReadOnlyList<BunnyPoint> original, Vector3 actual)
    {
        if (!float.IsFinite(actual.X) || !float.IsFinite(actual.Y) || !float.IsFinite(actual.Z)) return null;
        var position = MapProjection.WorldToMap(actual, map);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || position.X is < 1 or > 42 || position.Y is < 1 or > 42) return null;
        var nearby = original.Where(p => p.Territory == map.Territory)
            .Select(p => (Point: p, Distance: Distance(map, p, position)))
            .OrderBy(p => p.Distance).Take(2).ToArray();
        if (nearby.Length == 0 || nearby[0].Distance > BunnyRules.CandidateRadius
            || nearby.Length == 2 && nearby[1].Distance - nearby[0].Distance < 1) return null;
        return nearby[0].Point with { X = position.X, Y = position.Y, Height = actual.Y };
    }

    public static float Distance(IslandMap map, BunnyPoint point, Vector2 other)
        => Vector2.Distance(new(point.X, point.Y), other) * 2048 / (41 * (map.SizeFactor / 100f));
}

internal static class BunnyRules
{
    public static bool CanRecordHint(bool buff,int channel,TimeSpan frameAge) => buff&&channel==57&&frameAge>=TimeSpan.Zero&&frameAge<=TimeSpan.FromMilliseconds(500);
    public const float CandidateRadius = 5;
    public static (float Min,float Max) Range(BunnyDistance d)=>d switch
    {
        BunnyDistance.FarAway=>(150,float.PositiveInfinity),BunnyDistance.Far=>(80,150),
        BunnyDistance.Near=>(30,80),BunnyDistance.VeryNear=>(5,30),_=>(0,5)
    };
    public static BunnyDistance? Parse(string text)
    {
        // User terminology plus local LogMessage 9002–9006 (CN build 2026.10.06).
        if(text.Contains("发现了财宝")||text.Contains("就在这附近"))return BunnyDistance.Here;
        if(text.Contains("非常遥远")||text.Contains("很远的地方"))return BunnyDistance.FarAway;
        if(text.Contains("有些遥远")||text.Contains("稍远的地方"))return BunnyDistance.Far;
        if(text.Contains("比较近")||text.Contains("不远的地方"))return BunnyDistance.Near;
        if(text.Contains("非常近")||text.Contains("很近的地方"))return BunnyDistance.VeryNear;
        return null;
    }
    public static BunnyDirection? ParseDirection(string text)
    {
        // Composite names precede single cardinals; axes are map north, never player facing.
        var diagonal=System.Text.RegularExpressions.Regex.Match(text,@"(东北|东南|西南|西北)(?:方(?:向)?|边|侧)?",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(20));
        if(diagonal.Success)return diagonal.Value[..2] switch {"东北"=>BunnyDirection.NorthEast,"东南"=>BunnyDirection.SouthEast,"西南"=>BunnyDirection.SouthWest,_=>BunnyDirection.NorthWest};
        var cardinal=System.Text.RegularExpressions.Regex.Match(text,@"(?<![东南西北])正?([北东南西])\s*(?:方向|方|边|侧)",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(20));
        return cardinal.Success?cardinal.Groups[1].Value switch {"北"=>BunnyDirection.North,"东"=>BunnyDirection.East,"南"=>BunnyDirection.South,_=>BunnyDirection.West}:null;
    }
    public static bool MatchesDirection(Vector3 candidate, Vector3 origin, BunnyDirection direction, float radius = CandidateRadius)
        => Intersects(candidate - origin, 0, float.PositiveInfinity, direction, radius);

    public static bool Matches(Vector3 candidate, BunnyClue clue, float radius = CandidateRadius)
    {
        var (min, max) = Range(clue.Distance);
        return Intersects(candidate - clue.Position, min, max,
            clue.Distance == BunnyDistance.Here ? null : clue.Direction, radius);
    }

    private static bool Intersects(Vector3 offset, float min, float max, BunnyDirection? direction, float radius)
    {
        var distance = new Vector2(offset.X, offset.Z).Length();
        if (!float.IsFinite(distance) || !float.IsFinite(radius) || radius < 0) return false;
        var angleOutside = 0f;
        if (direction is { } value)
        {
            var bearing = MathF.Atan2(offset.X, -offset.Z);
            var centre = (int)value * MathF.PI / 4;
            var angle = MathF.Abs(MathF.IEEERemainder(bearing - centre, MathF.Tau));
            angleOutside = MathF.Max(0, angle - MathF.PI / 8);
        }

        // Project onto the closest permitted bearing, then clamp along that ray to the distance band.
        var along = distance * MathF.Cos(angleOutside);
        var across = distance * MathF.Sin(angleOutside);
        var radialGap = along - Math.Clamp(along, min, max);
        return radialGap * radialGap + across * across <= radius * radius + 0.00001f;
    }
}
internal sealed class BunnySearch(BunnyPoint[] points)
{
    private readonly List<BunnyClue> clues=[];
    private HashSet<string> candidates=[];
    private IslandMap? island;
    private bool listening;
    public bool Active=>listening;
    public IReadOnlyList<BunnyClue> Clues=>clues.ToArray();
    public IReadOnlySet<string> Candidates=>candidates.ToHashSet();
    public bool Conflict {get;private set;}
    public string? Flagged {get;private set;}
    public string Status {get;private set;}="等待幸福兔状态。";
    public void Begin(IslandMap map)
    {
        island=map;listening=true;clues.Clear();Flagged=null;Recalculate();Status="已检测到指引财宝，等待使用胡萝卜。";
    }
    public void Clear(string status="角色或地图变化，已清空兔子线索。")
    {island=null;listening=false;clues.Clear();candidates.Clear();Flagged=null;Conflict=false;Status=status;}
    public void End()
    {
        listening=false;clues.Clear();Flagged=null;Recalculate();
        Status="幸福兔状态已结束，地图已重置，等待下一轮。";
    }
    public bool Add(BunnyClue clue)
    {
        if(!listening||island==null||!float.IsFinite(clue.Position.X)||!float.IsFinite(clue.Position.Z))return false;
        if(clues.Count>=60){Status="本轮已记录60条线索，请重置后继续。";return false;}
        if(clues.LastOrDefault() is {} old&&clue.Time-old.Time<TimeSpan.FromMilliseconds(300)&&clue.Text==old.Text)return false;
        clue=clue with {Direction=clue.Distance==BunnyDistance.Here?null:clue.Direction??BunnyRules.ParseDirection(clue.Text)};
        clues.Add(clue);Recalculate();
        Status=Conflict?"线索与固定点位矛盾，暂停自动旗标；可撤销上条或重置。":$"已记录{clues.Count}条线索，剩余{candidates.Count}个候选。";return true;
    }
    internal void ReplacePoints(BunnyPoint[] value) { points = value; Recalculate(); }
    internal void Recalculate()
    {
        candidates=island==null?[]:points.Where(p=>p.Territory==island.Territory&&clues.All(c=>BunnyRules.Matches(MapProjection.MapToWorld(new(p.X,p.Y),island),c))).Select(p=>p.Id).ToHashSet();
        Conflict=clues.Count>0&&candidates.Count==0;
    }
    public void Undo(){if(clues.Count==0)return;clues.RemoveAt(clues.Count-1);Recalculate();Status=$"已撤销，剩余{candidates.Count}个候选。";}
    public BunnyPoint? Unique()=>listening&&!Conflict&&clues.Count>0&&candidates.Count==1&&Flagged!=candidates.Single()
        ?points.First(p=>p.Id==candidates.Single()):null;
    public void Unrecognized(string text)=>Status="未识别提示中的方向，未使用本条线索："+text;
    public void FlagFailed()=>Status="点位已确定，但旗标设置3次未成功，请重置后重试。";
    public void Marked(BunnyPoint p){Flagged=p.Id;Status="已确定点位，并在游戏地图设置旗标。";}
}
