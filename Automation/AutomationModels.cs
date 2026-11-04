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
    void StartTrigger(IReadOnlyList<TriggerPlan> plans, int batch, int maxKills) { }
    void RecordTriggerPoint(ushort fateId, bool reset = false) { }
    void ClearTriggerRoute(ushort fateId) { }
    void SelectLeader(PlayerIdentity identity);
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
