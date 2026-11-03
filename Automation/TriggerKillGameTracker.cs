using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ULKHelper.Fates;

namespace ULKHelper.Automation;

internal sealed class TriggerKillGameTracker(IObjectTable objects, IFateTable fates, IDataManager data)
{
    private TriggerKillLedger? ledger;
    public IReadOnlyList<TriggerKillCount> Snapshot => ledger?.Snapshot ?? [];
    public void Reset() => ledger?.Reset();
    public IReadOnlyList<TriggerKillEvent> Update(uint territory, DateTimeOffset now)
    {
        if (territory is not (732 or 763 or 795 or 827)) return [];
        ledger ??= new(TriggerMobCatalog.Resolve(data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>()!
            .Select(n => (n.RowId, n.Singular.ExtractText()))));
        var lives = objects.OfType<IBattleNpc>().Where(n => n.Address != 0 && n.MaxHp > 0)
            .Select(n => new TriggerNpcLife(n.GameObjectId, n.NameId, n.Level, n.IsDead || n.CurrentHp == 0)).ToArray();
        var active = fates.Where(f => f != null && f.TerritoryType.RowId == territory
            && f.State is FateState.Preparing or FateState.Running or FateState.Ending).Select(f => f.FateId).ToHashSet();
        return ledger.Observe(territory, lives, active, now);
    }
}
