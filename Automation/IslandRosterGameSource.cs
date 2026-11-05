using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace ULKHelper.Automation;

// EndRequest is used to wait for all server pages, not a guessed delay or partial list.
internal sealed unsafe class IslandRosterGameSource(IGameInteropProvider interop, IDataManager data, IObjectTable objects) : IIslandRosterSource, IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EndRequestDelegate(InfoProxyInterface* self);
    private Hook<EndRequestDelegate>? hook;
    private int completed, requestBaseline, consumed;
    private bool inFlight;
    private DateTimeOffset requestAt;
    private (byte Request, uint Count)? cachedRevision;
    private IslandMember[] socialMembers = [];
    public IReadOnlyList<IslandMember> SocialMembers => socialMembers;
    public string Diagnostics { get; private set; } = "尚未刷新玩家名单。";
    public string NativeDiagnostics()
    {
        var proxy = InfoProxyContentMember.Instance();
        return proxy == null ? "ContentMember 代理不可用。"
            : $"ContentMember 原生条数={proxy->EntryCount} 容量={proxy->DataSize} 请求编号={proxy->CurrentRequestId}/{proxy->NextRequestId} 显示标志={proxy->DisplayFlags}；社交缓存={socialMembers.Length}。";
    }
    private void EndRequest(InfoProxyInterface* self)
    {
        hook!.Original(self);
        try
        {
            if (self == (InfoProxyInterface*)InfoProxyContentMember.Instance()) Interlocked.Increment(ref completed);
        }
        catch { } // Never let optional bookkeeping throw across a native callback.
    }
    public bool Request()
    {
        if (inFlight && Volatile.Read(ref completed) == requestBaseline && DateTimeOffset.UtcNow - requestAt < TimeSpan.FromSeconds(30)) return false;
        var proxy = InfoProxyContentMember.Instance();
        if (proxy == null) return false;
        if (hook == null)
        {
            hook = interop.HookFromAddress<EndRequestDelegate>((nint)(*(nint**)proxy)[10], EndRequest);
            try { hook.Enable(); }
            catch { hook.Dispose(); hook = null; throw; }
        }
        requestBaseline = Volatile.Read(ref completed);
        var nextRequest = proxy->NextRequestId;
        var accepted = proxy->RequestData();
        inFlight = accepted || proxy->NextRequestId != nextRequest;
        requestAt = DateTimeOffset.UtcNow;
        return inFlight;
    }
    public IReadOnlyList<IslandMember>? TakeCompleted(uint territory)
    {
        var revision = Volatile.Read(ref completed);
        if (revision == consumed) return null;
        consumed = revision;
        inFlight = false;
        var proxy = InfoProxyContentMember.Instance();
        return ReadMembers(proxy, territory, $"玩家名单回调 #{revision}");
    }

    // Observe native cache changes even if the social window is closed and no completion fired.
    // Unconfirmed pages can supply UI candidates but cannot supply a combat headcount.
    public void ReadChangedList(uint territory)
    {
        if (territory is not (732 or 763 or 795 or 827)) return;
        var proxy = InfoProxyContentMember.Instance();
        if (proxy == null || proxy->EntryCount == 0) return;
        var revision = (proxy->CurrentRequestId, proxy->EntryCount);
        if (cachedRevision == revision) return;
        var members = ReadMembers(proxy, null, "游戏玩家名单缓存");
        if (members == null) return;
        cachedRevision = revision;
        socialMembers = members.ToArray();
    }

    private IReadOnlyList<IslandMember>? ReadMembers(InfoProxyContentMember* proxy, uint? territory, string source)
    {
        if (proxy == null || proxy->EntryCount is 0 or > 200 || proxy->CharData == null || proxy->DataSize < proxy->EntryCount)
        {
            Diagnostics = $"{source}原生数据不可用（条目数 {(proxy == null ? 0 : proxy->EntryCount)}）。";
            return null;
        }
        var result = new List<IslandMember>();
        var characters = proxy->CharDataSpan;
        var members = proxy->MemberData;
        var missingName = 0;
        var missingWorld = 0;
        var wrongTerritory = 0;
        var self = objects.LocalPlayer;
        var selfNameMatches = false;
        var locations = new HashSet<(ushort Member, ushort Character)>();
        for (var i = 0; i < proxy->EntryCount; i++)
        {
            var person = characters[i];
            var member = members[i];
            locations.Add((member.Location, person.Location));
            var name = person.NameString;
            if (string.IsNullOrWhiteSpace(name)) { missingName++; continue; }
            selfNameMatches |= self != null && name == self.Name.TextValue;
            if (person.HomeWorld == 0) { missingWorld++; continue; }
            if (territory is { } expected && member.Location != expected && person.Location != expected
                && (member.Location != 0 || person.Location != 0)) { wrongTerritory++; continue; }
            var world = data.GetExcelSheet<Lumina.Excel.Sheets.World>()!.GetRow(person.HomeWorld).Name.ExtractText();
            result.Add(new(new(name, person.HomeWorld), world, member.EntityId));
        }
        var includesSelf = self != null && result.Any(p => p.Identity.Name == self.Name.TextValue && p.Identity.HomeWorld == self.HomeWorld.RowId);
        Diagnostics = $"{source}：原生 {proxy->EntryCount}，有效 {result.Count}，空名 {missingName}，无原始服务器 {missingWorld}，区域排除 {wrongTerritory}，自己姓名匹配={selfNameMatches}，包含自己={includesSelf}，区域值={string.Join(",", locations.Take(8))}。";
        return includesSelf ? result : null;
    }
    public void Reset()
    {
        consumed = Volatile.Read(ref completed);
        socialMembers = [];
        var proxy = InfoProxyContentMember.Instance();
        cachedRevision = proxy == null || proxy->EntryCount == 0 ? null : (proxy->CurrentRequestId, proxy->EntryCount);
        Diagnostics = "尚未刷新当前副本的玩家名单。";
    }
    public void Dispose() { hook?.Dispose(); hook = null; }
}
