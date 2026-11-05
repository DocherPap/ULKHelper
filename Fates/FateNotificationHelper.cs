using System.Numerics;
using System.Text;

namespace ULKHelper.Fates;

internal enum FateNotificationChannel
{
    Shout, Yell, Say, Party, Alliance, FreeCompany, Echo, Debug, NoviceNetwork, PvPTeam,
    Linkshell1, Linkshell2, Linkshell3, Linkshell4, Linkshell5, Linkshell6, Linkshell7, Linkshell8,
    CrossLinkshell1, CrossLinkshell2, CrossLinkshell3, CrossLinkshell4, CrossLinkshell5, CrossLinkshell6, CrossLinkshell7, CrossLinkshell8,
}
internal sealed record FateNotificationSettings(bool Enabled, FateNotificationChannel Channel, string Template)
{
    public const string DefaultTemplate = "在<位置>发现<名字><se.1>";
    public static FateNotificationSettings Default => new(true, FateNotificationChannel.Echo, DefaultTemplate);
    public FateNotificationSettings ChangeFrom(FateNotificationSettings previous) => Channel == previous.Channel ? this : this with { Enabled = false };
}
internal sealed record FateNotification(FateDefinition Fate, Vector2 Position, DateTimeOffset DetectedAt);

internal static class FateNotificationText
{
    public static readonly string[] ChannelNames = ["喊话 /sh", "呼喊 /y", "说话 /s", "小队 /p", "团队 /a", "部队 /fc",
        "默语 /e", "调试默语", "新人频道 /b", "PvP 战队 /pt",
        "通信贝 1", "通信贝 2", "通信贝 3", "通信贝 4", "通信贝 5", "通信贝 6", "通信贝 7", "通信贝 8",
        "跨服通信贝 1", "跨服通信贝 2", "跨服通信贝 3", "跨服通信贝 4", "跨服通信贝 5", "跨服通信贝 6", "跨服通信贝 7", "跨服通信贝 8"];
    public static bool IsLocal(FateNotificationChannel channel) => channel is FateNotificationChannel.Echo or FateNotificationChannel.Debug;
    public static string Command(FateNotificationChannel channel) => channel switch
    {
        FateNotificationChannel.Shout => "/sh", FateNotificationChannel.Yell => "/y",
        FateNotificationChannel.Say => "/s", FateNotificationChannel.Party => "/p",
        FateNotificationChannel.Alliance => "/a", FateNotificationChannel.FreeCompany => "/fc",
        FateNotificationChannel.Echo => "/e", FateNotificationChannel.NoviceNetwork => "/b", FateNotificationChannel.PvPTeam => "/pt",
        >= FateNotificationChannel.Linkshell1 and <= FateNotificationChannel.Linkshell8 => $"/l{(int)channel - (int)FateNotificationChannel.Linkshell1 + 1}",
        >= FateNotificationChannel.CrossLinkshell1 and <= FateNotificationChannel.CrossLinkshell8 => $"/cwl{(int)channel - (int)FateNotificationChannel.CrossLinkshell1 + 1}",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
    public static bool Valid(FateNotificationSettings settings) => Enum.IsDefined(settings.Channel)
        && !string.IsNullOrWhiteSpace(settings.Template) && !settings.Template.Any(char.IsControl)
        && Encoding.UTF8.GetByteCount(settings.Template) <= 400;
    public static string Format(string template, string shortName, string position)
        => template.Replace("<位置>", position, StringComparison.Ordinal).Replace("<名字>", shortName, StringComparison.Ordinal);
    public static string FlagCommand(FateNotificationSettings settings, string shortName)
        => Command(settings.Channel) + " " + Format(settings.Template, shortName, "<flag>");
}

// Only live game observations enter this helper; crowdsource/manual history cannot send chat.
internal sealed class FateNotificationHelper(IReadOnlyList<IslandMap> islands)
{
    private readonly Dictionary<ushort, (DateTimeOffset? Start, bool Active)> seen = new();
    private readonly Queue<FateNotification> pending = new();
    private readonly Dictionary<ushort, DateTimeOffset> lastQueued = new();
    private bool needsBaseline = true;
    private DateTimeOffset baselineAt, nextSend;
    public FateNotificationSettings Settings { get; private set; } = FateNotificationSettings.Default;
    public string Status { get; private set; } = "通知未开启。";

    public void Configure(FateNotificationSettings value)
    {
        if (value == Settings) return;
        if (value.Enabled != Settings.Enabled) needsBaseline = true;
        Settings = value; pending.Clear();
        Status = !value.Enabled ? "通知未开启。" : FateNotificationText.Valid(value) ? "通知已开启，仅通知之后新出现的 FATE。" : "通知文本不能为空、含换行/控制字符或超过 400 字节。";
    }
    public void Reset()
    {
        seen.Clear(); pending.Clear(); lastQueued.Clear(); needsBaseline = true; baselineAt = default;
        Status = Settings.Enabled ? "等待进岛。" : "通知未开启。";
    }
    public void Observe(uint territory, IReadOnlyList<FateObservation> observations, DateTimeOffset now)
    {
        var baseline = needsBaseline;
        if (baseline) { baselineAt = now; needsBaseline = false; }
        var island = islands.FirstOrDefault(i => i.Territory == territory);
        if (island == null) return;
        var observedIds = new HashSet<ushort>();
        foreach (var observation in observations)
        {
            var definition = island.Fates.FirstOrDefault(f => f.Seed.Id == observation.Id);
            if (definition == null || definition.Seed.Bunny) continue;
            observedIds.Add(observation.Id);
            var active = observation.Activity != FateActivity.Ended;
            var known = seen.TryGetValue(observation.Id, out var previous);
            var appeared = active && (!known || !previous.Active &&
                (observation.Started == null || previous.Start == null || observation.Started > previous.Start));
            seen[observation.Id] = (observation.Started ?? previous.Start, active);
            if (!appeared || baseline || !Settings.Enabled || !FateNotificationText.Valid(Settings)) continue;
            // Newly streamed old events are not new spawns.
            if (observation.Started is { } actualStart && actualStart < baselineAt.AddSeconds(-2)) continue;
            if (lastQueued.TryGetValue(observation.Id, out var recent) && now - recent < TimeSpan.FromMinutes(1)) continue;
            if (pending.Count < 72)
            {
                pending.Enqueue(new(definition, observation.MapPosition ?? new(definition.Seed.X, definition.Seed.Y), now));
                lastQueued[observation.Id] = now;
            }
        }
        foreach (var (id, value) in seen.ToArray())
            if (!observedIds.Contains(id)) seen[id] = (value.Start, false);
    }

    public void SendNext(DateTimeOffset now, Func<FateNotification, FateNotificationSettings, bool> send)
    {
        if (!Settings.Enabled || !FateNotificationText.Valid(Settings) || now < nextSend) return;
        while (pending.TryDequeue(out var notification))
        {
            if (now - notification.DetectedAt > TimeSpan.FromSeconds(30) || !seen.TryGetValue(notification.Fate.Seed.Id, out var state) || !state.Active) continue;
            nextSend = now.AddSeconds(2);
            try
            {
                Status = send(notification, Settings)
                    ? $"已提交通知：{notification.Fate.Seed.ShortName}（{FateNotificationText.ChannelNames[(int)Settings.Channel]}）"
                    : "通知提交失败或内容过长，本条不重复发送。";
            }
            catch (Exception) { Status = "通知接口调用失败，本条不重复发送。"; }
            return;
        }
    }
}
