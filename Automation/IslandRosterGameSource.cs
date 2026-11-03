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
    private int completed, baseline;
    private bool inFlight;
    private DateTimeOffset requestAt;
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
        if (inFlight && Volatile.Read(ref completed) == baseline && DateTimeOffset.UtcNow - requestAt < TimeSpan.FromSeconds(30)) return false;
        var proxy = InfoProxyContentMember.Instance();
        if (proxy == null) return false;
        if (hook == null)
        {
            hook = interop.HookFromAddress<EndRequestDelegate>((nint)(*(nint**)proxy)[10], EndRequest);
            try { hook.Enable(); }
            catch { hook.Dispose(); hook = null; throw; }
        }
        baseline = Volatile.Read(ref completed);
        inFlight = proxy->RequestData();
        requestAt = DateTimeOffset.UtcNow;
        return inFlight;
    }
    public IReadOnlyList<IslandMember>? TakeCompleted(uint territory)
    {
        if (!inFlight || Volatile.Read(ref completed) == baseline) return null;
        var proxy = InfoProxyContentMember.Instance();
        if (proxy == null || proxy->EntryCount is 0 or > 200 || proxy->CharData == null || proxy->DataSize < proxy->EntryCount) return null;
        var result = new List<IslandMember>();
        var characters = proxy->CharDataSpan;
        var members = proxy->MemberData;
        for (var i = 0; i < characters.Length; i++)
        {
            var person = characters[i];
            var member = members[i];
            if (person.HomeWorld == 0 || string.IsNullOrWhiteSpace(person.NameString)
                || member.Location != territory && person.Location != territory && (member.Location != 0 || person.Location != 0)) continue;
            var world = data.GetExcelSheet<Lumina.Excel.Sheets.World>()!.GetRow(person.HomeWorld).Name.ExtractText();
            result.Add(new(new(person.NameString, person.HomeWorld), world, member.EntityId));
        }
        if (result.Count == 0) return null;
        var self = objects.LocalPlayer;
        if (self == null || !result.Any(p => p.Identity.Name == self.Name.TextValue && p.Identity.HomeWorld == self.HomeWorld.RowId)) return null;
        inFlight = false;
        return result;
    }
    public void Dispose() { hook?.Dispose(); hook = null; }
}
