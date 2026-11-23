using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace ULKHelper.Automation;

internal static class LeaderChatHistory
{
    // CN API 15: read-only RaptureLogModule.GetLogMessageDetail managed-copy overload.
    // All native access stays within this framework-thread call; no pointers are retained.
    public static unsafe IReadOnlyList<LeaderChatRecord> ReadLatest(PlayerIdentity leader, Func<SeString, MapDestination?> coordinate)
    {
        var module = RaptureLogModule.Instance();
        if (module == null) return [];
        var count = module->LogMessageCount;
        var records = new List<LeaderChatRecord>();
        for (var offset = 0; offset < LeaderListenerHelper.HistoryLookback && offset < count; offset++)
        {
            if (!module->GetLogMessageDetail(count - 1 - offset, out var senderBytes, out var contentBytes,
                out var kind, out _, out _, out var timestamp)) continue;
            if ((XivChatType)kind is not (XivChatType.Say or XivChatType.Yell or XivChatType.Shout)) continue;
            var sender = SeString.Parse(senderBytes).Payloads.OfType<PlayerPayload>().FirstOrDefault();
            if (sender == null || sender.World.RowId == 0
                || sender.PlayerName != leader.Name || sender.World.RowId != leader.HomeWorld) continue;
            var content = SeString.Parse(contentBytes);
            records.Add(new(new(sender.PlayerName, sender.World.RowId), ((XivChatType)kind).ToString(),
                content.TextValue, coordinate(content), DateTimeOffset.FromUnixTimeSeconds(timestamp)));
        }
        return records;
    }
}
