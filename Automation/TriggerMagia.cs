using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace ULKHelper.Automation;
// Uses documented game commands and read-only EurekaState. No guessed UI callbacks.
internal sealed class TriggerMagia(ICommandManager commands, IObjectTable objects)
{
    private byte[]? before, written;
    private ulong player;
    private DateTimeOffset next;
    private int restoreAttempts;
    public string? Error { get; private set; }
    private static unsafe byte[]? Read()
    {
        var framework=EventFramework.Instance();if(framework==null)return null;
        var director=framework->GetPublicContentDirectorByType(PublicContentDirectorType.Eureka);
        if(director==null)return null;
        var e=(PublicContentEureka*)director;
        return [e->State.Fire,e->State.Ice,e->State.Wind,e->State.Earth,e->State.Lightning,e->State.Water];
    }
    public bool Begin()
    {
        Error=null;before=Read();written=before?.ToArray();player=objects.LocalPlayer?.GameObjectId??0;
        if(before==null||before.Max()!=5){Error="魔素板需要预先将五颗魔石集中在同一属性；当前接口仅能旋转，无法在野外重新分配。";before=written=null;return false;}
        return true;
    }
    public bool Apply(bool attack)
    {
        if(before==null)return false;
        var current=Read();if(current==null)return false;
        if(DateTimeOffset.UtcNow<next)return true;
        // Target must already be selected; game resolves its element for this command.
        if(!commands.ProcessCommand(attack?"/magiaattack":"/magiadefense")){Error="魔素板指令不可用。";return false;}
        next=DateTimeOffset.UtcNow.AddSeconds(3);
        written=Read()??current;
        return true;
    }
    public void Restore()
    {
        var current=Read();
        if(before==null)return;
        if(player!=objects.LocalPlayer?.GameObjectId||current==null){before=written=null;return;}
        if(current.SequenceEqual(before)){before=written=null;return;}
        if(DateTimeOffset.UtcNow<next)return;
        if(restoreAttempts++>=6){before=written=null;return;}
        commands.ProcessCommand("/magiaright");next=DateTimeOffset.UtcNow.AddSeconds(3);
    }
    public bool Pending=>before!=null;
    public void Forget(){before=written=null;restoreAttempts=0;}
}
