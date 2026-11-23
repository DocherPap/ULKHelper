using System.Numerics;

namespace ULKHelper.Automation;

internal sealed record LeaderMessage(DateTimeOffset Time, string Channel, string Text, string Result);
internal sealed record LeaderChatRecord(PlayerIdentity Sender, string Channel, string Text, MapDestination? Destination, DateTimeOffset Time);

internal sealed class LeaderListenerHelper
{
    public const int HistoryLookback = 10;
    public static bool IsWaitDirective(string text) => text.Contains("原地", StringComparison.Ordinal)
        || text.Contains("等待", StringComparison.Ordinal) || text.Contains("等等", StringComparison.Ordinal);
    private readonly Queue<LeaderMessage> history = new();
    public PlayerIdentity? Leader { get; private set; }
    public bool Listening { get; private set; }
    public MapDestination? Destination { get; private set; }
    public IReadOnlyList<LeaderMessage> History => history.Reverse().ToArray();
    public void RecordActivity(DateTimeOffset time, string category, string text, string result)
    {
        history.Enqueue(new(time, category, text, result));
        while (history.Count > 50) history.Dequeue();
    }
    public string Status { get; private set; } = "请选择车头。";

    public void Select(PlayerIdentity identity)
    {
        Leader = identity;
        Listening = false;
        Destination = null;
        history.Clear();
        Status = "车头已选定，可开始监听。";
    }

    public bool RestoreCoordinate(IEnumerable<LeaderChatRecord> newestFirst, uint territory, uint map, Func<MapDestination, bool> mark)
    {
        foreach (var message in newestFirst.Take(HistoryLookback))
        {
            if (Leader != message.Sender || message.Destination is not { } destination
                || destination.Territory != territory || destination.Map != map || !NavigationRules.Finite(destination.Position)) continue;
            var restored = mark(destination);
            if (restored) Destination = destination;
            Status = restored ? "已从最近10条聊天记录取回车头坐标，可开始跟随。" : "历史坐标地图标记失败。";
            RecordActivity(message.Time, message.Channel, message.Text, Status);
            return restored;
        }
        Status = "车头已选定，最近10条聊天记录中没有本地图坐标。";
        return false;
    }

    public void SetListening(bool enabled, bool shoutOnly = false)
    {
        Listening = enabled && Leader != null;
        Status = Listening ? (shoutOnly ? "正在监听车头的喊话频道。" : "正在监听说话、呼喊、喊话频道。") : "监听已停止。";
    }

    public void Reset(string reason = "角色或地图变化，请重新选择车头。")
    {
        Listening = false; Destination = null; Leader = null; history.Clear();
        Status = reason;
    }

    public void ClearDestination(bool clearHistory = true)
    {
        Destination = null;
        if (clearHistory) history.Clear();
    }

    public bool Receive(PlayerIdentity sender, string channel, string text, MapDestination? link,
        uint territory, uint map, Vector3 player, DateTimeOffset time, Func<MapDestination, bool> mark, bool following = false)
    {
        if (!Listening || Leader != sender) return false;
        var result = "已记录，未发现坐标。";
        if (link != null)
        {
            if (!NavigationRules.Finite(link.Position)) result = "坐标无效，已跳过。";
            else if (link.Territory != territory || link.Map != map) result = "坐标不在当前地图，已跳过。";
            else if ((!following || Destination != null) && NavigationRules.Overlaps(following ? Destination!.Position : player, link.Position))
                result = "坐标在 25 米内，已跳过。";
            else if (mark(link)) { Destination = link; result = "已在地图标记目的地。"; }
            else result = "地图标记失败。";
        }
        RecordActivity(time, channel, text, result);
        Status = result;
        return true;
    }
}
