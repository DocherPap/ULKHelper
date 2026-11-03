using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Text;
using ULKHelper.Fates;

namespace ULKHelper.GameInterop;

internal static class FateChatSender
{
    // Framework-thread only. The game expands <flag> into its canonical clickable map link.
    public static unsafe bool Send(IslandMap island, FateNotification notification, FateNotificationSettings settings, IChatGui chat)
    {
        if (!FateNotificationText.Valid(settings)) return false;
        if (!float.IsFinite(notification.Position.X) || !float.IsFinite(notification.Position.Y)) return false;
        var debug = settings.Channel == FateNotificationChannel.Debug;
        var world = MapProjection.MapToWorld(notification.Position, island);
        if (debug)
        {
            var text = new SeStringBuilder();
            var parts = settings.Template.Split("<位置>", StringSplitOptions.None);
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) text.Append(SeString.CreateMapLink(island.Territory, island.MapId, (int)(world.X * 1000), (int)(world.Z * 1000)));
                text.AddText(parts[i].Replace("<名字>", notification.Fate.Seed.ShortName, StringComparison.Ordinal));
            }
            if (text.Encode().Length > 500) return false;
            chat.Print(new XivChatEntry { Type = XivChatType.Debug, Message = text.Build(), Silent = true });
            return true;
        }
        var command = FateNotificationText.FlagCommand(settings, notification.Fate.Seed.ShortName);
        if (Encoding.UTF8.GetByteCount(command) > 500) return false;
        var module = UIModule.Instance();
        if (module == null) return false;
        var message = Utf8String.FromString(command);
        if (message == null) return false;
        try
        {
            if (command.Contains("<flag>", StringComparison.OrdinalIgnoreCase))
            {
                var map = AgentMap.Instance();
                if (map == null) return false;
                map->SetFlagMapMarker(island.Territory, island.MapId, world);
                if (map->FlagMarkerCount == 0) return false;
            }
            module->ProcessChatBoxEntry(message, 0, false);
            return true;
        }
        finally { message->Dtor(true); }
    }
}
