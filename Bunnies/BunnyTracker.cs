using Dalamud.Game.Chat;
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
    private readonly IPluginLog log;private readonly BunnyPoint[] points=BunnyCatalog.Load();
    private readonly BunnySearch search;
    private readonly ConcurrentQueue<(Frame Frame,BunnyClue Clue)> incoming=new();
    private readonly ConcurrentQueue<Action> commands=new();
    private sealed record Frame(uint Territory,uint Map,ulong Character,Vector3 Position,DateTimeOffset Time,bool Buff,long Generation);
    private long generation,resetVersion;private int flagAttempts;private DateTimeOffset nextFlag;private bool currentBuff;
    private volatile Frame? lastFrame;
    private DateTimeOffset buffSeen;private bool wasBuff;private (uint Territory,uint Map,uint Instance,ulong Player,uint Room) context;
    private volatile BunnySnapshot snapshot=new(0,false,null,[],new HashSet<string>(),[],"等待幸福兔状态。");
    public BunnySnapshot Snapshot=>snapshot;
    public string Describe()=> $"兔子挖宝：{snapshot.Status} 活跃={snapshot.Active} 候选={snapshot.Candidates.Count}/{snapshot.Points.Count} 线索={snapshot.Clues.Count} 矛盾={snapshot.Conflict} 已旗标={snapshot.Flagged}\n最后提示={snapshot.Clues.LastOrDefault()?.Text}";
    public BunnyTracker(IFramework framework,IClientState client,IObjectTable objects,IPlayerState player,IChatGui chat,IFateMapProvider maps,IPluginLog log)
    {this.framework=framework;this.client=client;this.objects=objects;this.player=player;this.chat=chat;this.maps=maps;this.log=log;search=new(points);framework.Update+=Tick;chat.ChatMessage+=Chat;}
    private void Begin(IslandMap map){generation++;flagAttempts=0;nextFlag=default;search.Begin(map);}
    public void Undo()=>commands.Enqueue(search.Undo);
    public void Reset()=>commands.Enqueue(()=>{var map=maps.Islands.FirstOrDefault(m=>m.Territory==client.TerritoryType);if(map!=null&&currentBuff)Begin(map);else search.Clear();});
    public void Manual(BunnyDistance distance,BunnyDirection? direction=null)=>commands.Enqueue(()=>{if(objects.LocalPlayer is {} p&&currentBuff)search.Add(new(p.Position,distance,"手动记录提示",DateTimeOffset.UtcNow,direction));});
    private void Chat(IChatMessage message)
    {
        // Local LogMessage rows 9002–9006 have LogKind 57. Never accept player speech/echo spoofing.
        var frame=lastFrame;var now=DateTimeOffset.UtcNow;
        if(frame==null||!BunnyRules.CanRecordHint(frame.Buff,(int)message.LogKind,now-frame.Time))return;
        var text=SeString.Parse(message.OriginalMessage.Data.Span).TextValue;
        var distance=BunnyRules.Parse(text);if(distance==null)return;
        incoming.Enqueue((frame,new(frame.Position,distance.Value,text,now,BunnyRules.ParseDirection(text))));
    }
    private unsafe void Tick(IFramework update)
    {
        try
        {
            var now=DateTimeOffset.UtcNow;var local=objects.LocalPlayer;
            var world=(client.TerritoryType,client.MapId,client.Instance,client.IsLoggedIn?player.ContentId:0,maps.Snapshot.RoomId);
            if(world!=context)
            {generation++;context=world;search.Clear();wasBuff=false;lastFrame=null;while(incoming.TryDequeue(out _)){}while(commands.TryDequeue(out _)){};}
            var map=maps.Islands.FirstOrDefault(m=>m.Territory==client.TerritoryType);
            var buff=client.IsLoggedIn&&player.ContentId!=0&&local is {IsDead:false}&&map!=null&&map.Territory is 763 or 795 or 827&&local.StatusList.Any(s=>s.StatusId==1531);
            currentBuff=buff;
            // End before consuming queued clues/setting flags: delayed hints belong to the finished hunt.
            if(!buff&&wasBuff){generation++;resetVersion++;flagAttempts=0;nextFlag=default;search.End();}
            if(buff&&!wasBuff)Begin(map!);
            if(buff)buffSeen=now;
            while(commands.TryDequeue(out var action))action();
            lastFrame=local==null?null:new(client.TerritoryType,client.MapId,player.ContentId,local.Position,now,buff,generation);
            while(incoming.TryDequeue(out var hint))
                if(hint.Frame.Generation==generation&&hint.Frame.Territory==client.TerritoryType&&hint.Frame.Character==player.ContentId&&(buff||now-buffSeen<TimeSpan.FromSeconds(1))&&now-hint.Clue.Time<TimeSpan.FromSeconds(2)){
                    if(hint.Clue.Distance!=BunnyDistance.Here&&hint.Clue.Direction==null)search.Unrecognized(hint.Clue.Text);
                    else search.Add(hint.Clue);
                }
            if(flagAttempts<3&&now>=nextFlag&&search.Unique() is {} unique&&map!=null&&local is {IsDead:false}&&(buff||now-buffSeen<TimeSpan.FromSeconds(1)))
            {
                flagAttempts++;nextFlag=now.AddSeconds(1);
                var agent=AgentMap.Instance();
                if(agent!=null){agent->SetFlagMapMarker(map.Territory,map.MapId,MapProjection.MapToWorld(new(unique.X,unique.Y),map));if(agent->FlagMarkerCount>0)search.Marked(unique);}
                if(flagAttempts>=3&&search.Flagged!=unique.Id)search.FlagFailed();
            }
            wasBuff=buff;
            snapshot=new(client.TerritoryType,search.Active,local?.Position,points.Where(p=>p.Territory==client.TerritoryType).ToArray(),search.Candidates,search.Clues,search.Status,search.Conflict,search.Flagged,resetVersion);
        }
        catch(Exception ex){search.Clear("读取兔子状态失败，已清空本轮推断。");lastFrame=null;log.Warning(ex,"兔子挖宝状态更新失败");}
    }
    public void Dispose(){framework.Update-=Tick;chat.ChatMessage-=Chat;lastFrame=null;search.Clear();while(incoming.TryDequeue(out _)){}while(commands.TryDequeue(out _)){};}
}
