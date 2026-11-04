using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace ULKHelper.Automation;

internal static class EurekaNpcLevel
{
    // Framework-thread only. ForayInfo is read immediately; never retain its native pointer.
    public static unsafe byte? Read(IBattleNpc npc)
    {
        if(npc.Address==0)return null;
        var info=((Character*)npc.Address)->GetForayInfo();
        return ULKHelper.Fates.TriggerMobCatalog.ElementalLevel(info==null ? null : info->Level);
    }
}
