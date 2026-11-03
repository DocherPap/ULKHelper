namespace ULKHelper.Crowdsource;

// One lookup per entry, including failed lookups. Poll only observes task completion, never HTTP.
internal sealed class EntryCrowdsourceLookup(Func<CrowdScope, CancellationToken, Task<CrowdSnapshot>> fetch) : IDisposable
{
    private CancellationTokenSource? cancellation;
    private Task<CrowdSnapshot>? pending;
    private DateTimeOffset nextAllowed;
    public bool Attempted { get; private set; }
    public string Status { get; private set; } = "等待进岛读取一次。";
    public void Reset()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        pending = null; Attempted = false; Status = "等待房间 ID，进岛只读取一次。";
    }
    public void Start(CrowdScope scope, DateTimeOffset now)
    {
        if (Attempted || now < nextAllowed) return;
        Attempted = true; nextAllowed = now.AddSeconds(6);
        cancellation = new(); Status = "正在读取进岛众包快照…";
        try { pending = fetch(scope, cancellation.Token); }
        catch (Exception ex) { Status = "进岛查询失败：" + ex.Message; }
    }
    public CrowdSnapshot? TakeCompleted()
    {
        if (pending is not { IsCompleted: true }) return null;
        var task = pending; pending = null;
        try
        {
            var result = task.GetAwaiter().GetResult();
            Status = $"进岛快照已读取 · {result.RetrievedAt.ToLocalTime():HH:mm:ss} · {result.Appearances.Count} 条发生记录";
            return result;
        }
        catch (OperationCanceledException) { Status = "进岛查询已取消或超时，本次不重试。"; return null; }
        catch (Exception ex) { Status = "进岛查询失败：" + ex.Message; return null; }
        finally { cancellation?.Dispose(); cancellation = null; }
    }
    public void Dispose() => Reset();
}
