using System.Text;

namespace ULKHelper.Automation;

internal sealed record DebugRecordingSnapshot(bool Active, bool Saving, string Status, string? Path = null, string? Text = null)
{
    public static readonly DebugRecordingSnapshot Empty = new(false, false, "尚未开始记录。");
}

internal sealed class DebugRecording(string directory)
{
    private const int MaxEntries = 12000;
    private readonly Dictionary<string, string> previous = new();
    private readonly StringBuilder buffer = new();
    private DateTimeOffset started;
    private int entries;
    private volatile DebugRecordingSnapshot snapshot = DebugRecordingSnapshot.Empty;
    public DebugRecordingSnapshot Snapshot => snapshot;

    public void LoadLatest()
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var path = Directory.EnumerateFiles(directory, "session-*.txt").OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault();
            if (path != null) snapshot = new(false, false, "已载入最近一次记录。", path, File.ReadAllText(path));
        }
        catch (Exception ex) { snapshot = new(false, false, "无法读取最近记录：" + ex.Message); }
    }

    public void Start(DateTimeOffset now, string version)
    {
        if (snapshot.Active || snapshot.Saving) return;
        previous.Clear();
        buffer.Clear();
        entries = 0;
        started = now;
        snapshot = snapshot with { Active = true, Status = "正在记录调试事件。" };
        Add(now, $"开始记录 | ULKHelper {version}");
    }

    public void Change(DateTimeOffset now, string key, string value)
    {
        if (!snapshot.Active || previous.TryGetValue(key, out var old) && old == value) return;
        previous[key] = value;
        Add(now, $"{key} | {value}");
    }

    public void Add(DateTimeOffset now, string message)
    {
        if (!snapshot.Active) return;
        buffer.AppendLine($"{now:yyyy-MM-dd HH:mm:ss.fff zzz} | {message}");
        if (++entries >= MaxEntries) Finish(now, "达到记录条数上限");
    }

    public void CheckLimit(DateTimeOffset now)
    {
        if (snapshot.Active && now - started >= TimeSpan.FromMinutes(30))
            Finish(now, "达到30分钟记录上限");
    }

    public void Finish(DateTimeOffset now, string reason)
    {
        if (!snapshot.Active) return;
        buffer.AppendLine($"{now:yyyy-MM-dd HH:mm:ss.fff zzz} | 结束记录 | {reason}");
        var report = buffer.ToString();
        var path = Path.Combine(directory, $"session-{started.UtcDateTime:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
        snapshot = new(false, true, "正在保存记录。", null, report);
        buffer.Clear();
        previous.Clear();
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(path + ".tmp", report, Encoding.UTF8);
                File.Move(path + ".tmp", path);
                snapshot = new(false, false, "已保存记录。", path, report);
            }
            catch (Exception ex) { snapshot = new(false, false, "记录保存失败，仍可复制：" + ex.Message, null, report); }
        });
    }
}
