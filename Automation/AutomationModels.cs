using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal sealed record AutomationSnapshot(IReadOnlyList<PlayerEntry> Players, PlayerIdentity? Leader, bool Listening,
    string ListenerStatus, MapDestination? Destination, IReadOnlyList<LeaderMessage> Messages, bool Navigating, string NavigationStatus,
    IslandRosterSnapshot? Roster = null, IReadOnlyList<OwnedMount>? Mounts = null, uint SelectedMount = 0,
    bool Following = false, string FollowStatus = "尚未开始跟随伐木。", IReadOnlyList<TriggerKillCount>? KillCounts = null);

internal interface IAutomationHelpers
{
    AutomationSnapshot Snapshot { get; }
    AutoTriggerSnapshot TriggerSnapshot => AutoTriggerSnapshot.Empty;
    bool BunnyAutomationActive => false;
    string BunnyAutomationStatus => "尚未开始自动挖宝。";
    int BunnyFoundCount => 0;
    void SetBunnyAutomation(uint territory, IReadOnlyCollection<ushort> selected, bool enabled, int limit = 0) { }
    void StartTrigger(IReadOnlyList<TriggerPlan> plans, int batch, int maxKills, bool useRotation = true, bool useBmr = true) { }
    void RecordTriggerPoint(ushort fateId, bool newRoute = false) { }
    void SelectTriggerRoute(ushort fateId, int index) { }
    void RenameTriggerRoute(ushort fateId, int index, string name) { }
    void ClearTriggerRoute(ushort fateId) { }
    void SelectLeader(PlayerIdentity identity);
    void ClearLeader() { }
    void SelectCurrentTargetAsLeader() { }
    void Listen(bool enabled);
    void Navigate();
    void Stop();
    void RefreshPlayers() { }
    void SelectMount(uint id) { }
    void Follow(bool enabled) { }
    void NavigateTo(MapDestination destination) { }
    void GoToEntrance() { }
}
