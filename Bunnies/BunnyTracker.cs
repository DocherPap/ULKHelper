using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Objects.Enums;
using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ULKHelper.Fates;

namespace ULKHelper.Bunnies;
internal sealed class BunnyTracker : IBunnyTracker, IDisposable
{
    private readonly IFramework framework;private readonly IClientState client;private readonly IObjectTable objects;
    private readonly IPlayerState player;private readonly IChatGui chat;private readonly IFateMapProvider maps;
    private readonly IPluginLog log;
    private readonly SettingsStore settings;
    private readonly BunnyPoint[] originalPoints = BunnyCatalog.Load();
    private BunnyPoint[] points;
    private volatile IReadOnlyList<BunnyPoint> catalog = [];
    private readonly Dictionary<string, BunnyPoint> calibrated = new(StringComparer.Ordinal);
    private const string CalibrationKey = "bunny.calibratedPoints";
    private readonly Dictionary<ulong, (Vector3 Position, DateTimeOffset FirstSeen)> chests = [];
    private BunnyClue? discoveredChest;
    private readonly BunnySearch search;
    private readonly ConcurrentQueue<(Frame Frame,BunnyClue Clue)> incoming=new();
    private readonly ConcurrentQueue<Action> commands=new();
    private sealed record Frame(uint Territory,uint Map,ulong Character,Vector3 Position,DateTimeOffset Time,bool Buff,bool RecentBuff,long Generation);
    private long generation,resetVersion;private int flagAttempts;private DateTimeOffset nextFlag;private bool currentBuff;
    private volatile Frame? lastFrame;
    private DateTimeOffset buffSeen;private bool wasBuff;private (uint Territory,uint Map,uint Instance,ulong Player,uint Room) context;
    private DateTimeOffset? lastTreasureAt;
    private volatile BunnySnapshot snapshot=new(0,false,null,[],new HashSet<string>(),[],"等待幸福兔状态。");
    public BunnySnapshot Snapshot=>snapshot;
    public IReadOnlyList<BunnyPoint> Catalog => catalog;
    public BunnyTracker(IFramework framework,IClientState client,IObjectTable objects,IPlayerState player,IChatGui chat,IFateMapProvider maps,IPluginLog log,SettingsStore settings)
    {
        this.framework=framework;this.client=client;this.objects=objects;this.player=player;this.chat=chat;this.maps=maps;this.log=log;
        this.settings = settings;
        points = originalPoints.ToArray();
        foreach (var point in settings.Get<BunnyPoint[]>(CalibrationKey, []))
        {
            if (point == null || point.Height is not { } height || !float.IsFinite(height)
                || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || point.X is < 1 or > 42 || point.Y is < 1 or > 42) continue;
            var index = Array.FindIndex(originalPoints, p => p.Id == point.Id && p.Territory == point.Territory);
            var map = maps.Islands.FirstOrDefault(m => m.Territory == point.Territory);
            if (map == null || string.IsNullOrEmpty(point.Id)) continue;
            // Older saved calibrations already came from a uniquely observed chest.
            var precise = point with { Exact = true, Origin = point.Id.StartsWith("local-", StringComparison.Ordinal)
                ? BunnyPointOrigin.LocalDiscovery : BunnyPointOrigin.Calibrated };
            if (index < 0)
            {
                if (!point.Id.StartsWith("local-", StringComparison.Ordinal) || calibrated.ContainsKey(point.Id)) continue;
                points = [..points, precise];
            }
            else points[index] = BunnyCalibration.Prefer(points[index], precise);
            calibrated[point.Id] = index < 0 ? precise : points[index];
        }
        catalog = points.ToArray();
        search = new(points);
        framework.Update += Tick;
        chat.ChatMessage += Chat;
    }
    private void Begin(IslandMap map){generation++;flagAttempts=0;nextFlag=default;search.Begin(map);}
    public void Undo()=>commands.Enqueue(search.Undo);
    public void Reset()=>commands.Enqueue(()=>{var map=maps.Islands.FirstOrDefault(m=>m.Territory==client.TerritoryType);if(map!=null&&currentBuff)Begin(map);else search.Clear();});
    public void Manual(BunnyDistance distance,BunnyDirection? direction=null)=>commands.Enqueue(()=>{if(objects.LocalPlayer is {} p&&currentBuff)search.Add(new(p.Position,distance,"手动记录提示",DateTimeOffset.UtcNow,direction));});
    private void Chat(IChatMessage message)
    {
        // Local LogMessage rows 9002–9006 have LogKind 57. Never accept player speech/echo spoofing.
        var frame=lastFrame;var now=DateTimeOffset.UtcNow;
        if(frame==null||!BunnyRules.CanRecordHint(frame.Buff||frame.RecentBuff,(int)message.LogKind,now-frame.Time))return;
        var text=SeString.Parse(message.OriginalMessage.Data.Span).TextValue;
        var distance=BunnyRules.Parse(text);if(distance==null)return;
        if (!frame.Buff && (distance != BunnyDistance.Here || !text.Contains("发现了财宝", StringComparison.Ordinal))) return;
        incoming.Enqueue((frame,new(frame.Position,distance.Value,text,now,BunnyRules.ParseDirection(text))));
    }
    private unsafe void Tick(IFramework update)
    {
        try
        {
            var now=DateTimeOffset.UtcNow;var local=objects.LocalPlayer;
            var world=(client.TerritoryType,client.MapId,client.Instance,client.IsLoggedIn?player.ContentId:0,maps.Snapshot.RoomId);
            if(world!=context)
            {generation++;context=world;search.Clear();wasBuff=false;buffSeen=default;lastFrame=null;lastTreasureAt=null;discoveredChest=null;chests.Clear();while(incoming.TryDequeue(out _)){}while(commands.TryDequeue(out _)){};}
            var map=maps.Islands.FirstOrDefault(m=>m.Territory==client.TerritoryType);
            var buff=client.IsLoggedIn&&player.ContentId!=0&&local is {IsDead:false}&&map!=null&&map.Territory is 763 or 795 or 827&&local.StatusList.Any(s=>s.StatusId==1531);
            currentBuff=buff;
            // End before consuming queued clues/setting flags: delayed hints belong to the finished hunt.
            var startingHunt = buff && !wasBuff;
            if (startingHunt) { Begin(map!); chests.Clear(); discoveredChest = null; }
            if(buff)buffSeen=now;
            while(commands.TryDequeue(out var action))action();
            while(incoming.TryDequeue(out var hint))
                if(hint.Frame.Generation==generation&&hint.Frame.Territory==client.TerritoryType&&hint.Frame.Character==player.ContentId&&(buff||now-buffSeen<TimeSpan.FromSeconds(1))&&now-hint.Clue.Time<TimeSpan.FromSeconds(2)){
                    if (hint.Clue.Distance == BunnyDistance.Here && hint.Clue.Text.Contains("发现了财宝", StringComparison.Ordinal))
                    {
                        lastTreasureAt = hint.Clue.Time;
                        discoveredChest ??= hint.Clue;
                    }
                    if (!buff) continue;
                    if(hint.Clue.Distance!=BunnyDistance.Here&&hint.Clue.Direction==null)search.Unrecognized(hint.Clue.Text);
                    else search.Add(hint.Clue);
                }
            if (map != null && local is { IsDead: false } && (buff || discoveredChest != null))
                ObserveChest(map, now, startingHunt);
            if(!buff&&wasBuff){generation++;resetVersion++;flagAttempts=0;nextFlag=default;search.End();}
            lastFrame=local==null?null:new(client.TerritoryType,client.MapId,player.ContentId,local.Position,now,buff,
                now-buffSeen<TimeSpan.FromSeconds(1),generation);
            if(flagAttempts<3&&now>=nextFlag&&search.Unique() is {} unique&&map!=null&&local is {IsDead:false}&&buff)
            {
                flagAttempts++;nextFlag=now.AddSeconds(1);
                var agent=AgentMap.Instance();
                if(agent!=null){agent->SetFlagMapMarker(map.Territory,map.MapId,MapProjection.MapToWorld(new(unique.X,unique.Y),map));if(agent->FlagMarkerCount>0)search.Marked(unique);}
                if(flagAttempts>=3&&search.Flagged!=unique.Id)search.FlagFailed();
            }
            wasBuff=buff;
            snapshot=new(client.TerritoryType,search.Active,local?.Position,points.Where(p=>p.Territory==client.TerritoryType).ToArray(),search.Candidates,search.Clues,search.Status,search.Conflict,search.Flagged,resetVersion,lastTreasureAt);
        }
        catch(Exception ex){search.Clear("读取兔子状态失败，已清空本轮推断。");lastFrame=null;log.Warning(ex,"兔子挖宝状态更新失败");}
    }
    private void ObserveChest(IslandMap map, DateTimeOffset now, bool startingHunt)
    {
        // CN EObjName 2009530–2009532: gold, silver and bronze treasure coffers.
        foreach (var obj in objects)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.Treasure)
                || obj.BaseId is not (2009530 or 2009531 or 2009532) || !obj.IsTargetable) continue;
            var position = obj.Position;
            if (float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z))
                chests.TryAdd(obj.GameObjectId, (position, startingHunt ? DateTimeOffset.MinValue : now));
        }

        if (discoveredChest is not { } clue || now - clue.Time < TimeSpan.FromMilliseconds(750)) return;
        var matches = chests.Values.Where(c => c.FirstSeen >= clue.Time.AddSeconds(-1)
            && c.FirstSeen <= clue.Time.AddSeconds(2)
            && Vector2.Distance(new(c.Position.X, c.Position.Z), new(clue.Position.X, clue.Position.Z)) <= 8).Take(2).ToArray();
        if (matches.Length == 0 && now - clue.Time < TimeSpan.FromSeconds(2)) return;
        discoveredChest = null;
        if (matches.Length != 1)
        {
            log.Debug("幸福兔箱点未校准：符合发现时间与距离的箱体数 {Count}。", matches.Length);
            return;
        }
        var actual = matches[0].Position;
        var coordinate = MapProjection.WorldToMap(actual, map);
        if (coordinate.X is < 1 or > 42 || coordinate.Y is < 1 or > 42) return;
        var corrected = BunnyCalibration.Match(map, points, actual, now);
        if (corrected == null)
        {
            if (points.Any(p => p.Territory == map.Territory
                && BunnyCalibration.Distance(map, p, coordinate) <= BunnyRules.CandidateRadius)) return;
            corrected = new BunnyPoint(map.Territory, "local-" + Guid.NewGuid().ToString("N"), coordinate.X, coordinate.Y, actual.Y,
                Exact: true, Origin: BunnyPointOrigin.LocalDiscovery, ObservedAt: now);
            points = [..points, corrected];
        }
        var index = Array.FindIndex(points, p => p.Id == corrected.Id && p.Territory == corrected.Territory);
        if (index < 0) return;
        points[index] = calibrated[corrected.Id] = corrected;
        search.ReplacePoints(points);
        catalog = points.ToArray();
        settings.Put(CalibrationKey, calibrated.Values.ToArray());
        settings.Flush();
        log.Information("幸福兔箱点校准：岛 {Territory}，参考点 {Point}，实际地图坐标 {X:F3}, {Y:F3}。",
            corrected.Territory, corrected.Id, corrected.X, corrected.Y);
    }

    public void Dispose(){framework.Update-=Tick;chat.ChatMessage-=Chat;lastFrame=null;search.Clear();while(incoming.TryDequeue(out _)){}while(commands.TryDequeue(out _)){};}
}
