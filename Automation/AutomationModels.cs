namespace ULKHelper.Automation;

internal sealed record AutomationSnapshot(IReadOnlyList<PlayerEntry> Players, PlayerIdentity? Leader, bool Listening,
    string ListenerStatus, MapDestination? Destination, IReadOnlyList<LeaderMessage> Messages, bool Navigating, string NavigationStatus);

internal interface IAutomationHelpers
{
    AutomationSnapshot Snapshot { get; }
    void SelectLeader(PlayerIdentity identity);
    void Listen(bool enabled);
    void Navigate();
    void Stop();
}
