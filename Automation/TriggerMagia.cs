using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace ULKHelper.Automation;
// Uses documented game commands and read-only EurekaState. No guessed UI callbacks.
internal sealed class TriggerMagia(IObjectTable objects)
{
    private byte[]? before, written;
    private ulong player;
    private DateTimeOffset next;
    private int restoreAttempts;
    private (bool Attack,uint Name)? selected;
    public string? Error { get; private set; }
    private static unsafe byte[]? Read()
    {
        var framework=EventFramework.Instance();if(framework==null)return null;
        var director=EventFramework.GetPublicContentDirectorByType(PublicContentDirectorType.Eureka);
        if(director==null)return null;
        var e=(PublicContentEureka*)director;
        return [e->State.Fire,e->State.Ice,e->State.Wind,e->State.Earth,e->State.Lightning,e->State.Water];
    }
    public bool Begin()
    {
        Error=null;restoreAttempts=0;selected=null;before=Read();written=before?.ToArray();player=objects.LocalPlayer?.GameObjectId??0;
        if(before==null){Error="无法读取当前岛魔素板。";before=written=null;return false;}
        return true;
    }
    public bool Apply(bool attack,uint name)
    {
        if(before==null)return false;
        if(selected==(attack,name))return true;
        var current=Read();if(current==null)return false;
        if(DateTimeOffset.UtcNow<next)return true;
        // Target must already be selected; game resolves its element for this command.
        if(!Send(attack?"/magiaattack":"/magiadefense")){Error="魔素板指令不可用。";return false;}
        next=DateTimeOffset.UtcNow.AddSeconds(3);
        written=Read()??current;selected=(attack,name);
        return true;
    }
    public void Restore()
    {
        var current=Read();
        if(before==null)return;
        if(objects.LocalPlayer is not {IsDead:false}){Forget();return;}
        if(player!=objects.LocalPlayer?.GameObjectId||current==null){before=written=null;return;}
        if(current.SequenceEqual(before)){before=written=null;return;}
        if(DateTimeOffset.UtcNow<next)return;
        if(restoreAttempts++>=6){before=written=null;return;}
        Send("/magiaright");next=DateTimeOffset.UtcNow.AddSeconds(3);
    }
    private static unsafe bool Send(string command)
    {
        var module=FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();if(module==null)return false;
        var text=FFXIVClientStructs.FFXIV.Client.System.String.Utf8String.FromString(command);
        if(text==null)return false;
        try{module->ProcessChatBoxEntry(text,0,false);return true;}finally{text->Dtor(true);}
    }
    public bool Pending=>before!=null;
    public void Forget(){before=written=null;restoreAttempts=0;selected=null;}
}
