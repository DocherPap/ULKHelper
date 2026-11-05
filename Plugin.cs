using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ULKHelper.Windows;
using ULKHelper.Inventory;
using ULKHelper.Automation;
using ULKHelper.Weather;
using ULKHelper.Fates;

namespace ULKHelper;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/ulk";
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windows = new("ULKHelper");
    private readonly MainWindow mainWindow;
    private readonly InventoryTracker inventoryTracker;
    private readonly AutomationHelpers automation;
    private readonly FateMapTracker fateTracker;
    private readonly Bunnies.BunnyTracker bunnies;
    private readonly Logos.LogosService logos = new();
    private readonly Logos.LogosInventoryReader logosInventory;
    private bool disposed;
    private readonly SettingsStore settings;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IFramework framework,
        IClientState clientState, IPlayerState playerState, IDataManager dataManager, IGameInventory gameInventory,
        IAddonLifecycle addonLifecycle, IPluginLog log, IGameGui gameGui, IObjectTable objects,
        IFateTable fates, ICondition conditions, IChatGui chat, ITextureProvider textures, ISigScanner scanner, IPartyList party,
        IGameInteropProvider interop, ITargetManager targets)
    {
        log.Information("ULKHelper构建：{Build}；加载路径：{Path}", typeof(Plugin).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "未知",typeof(Plugin).Assembly.Location);
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        settings = new(Path.Combine(pluginInterface.GetPluginConfigDirectory(), "settings.json"), ex => log.Warning(ex, "保存或读取设置失败"));
        inventoryTracker = new(pluginInterface, framework, clientState, playerState, dataManager, gameInventory, addonLifecycle, log, gameGui, conditions);
        IReadOnlyList<WeatherZone> weatherZones = [];
        string? weatherError = null;
        try { weatherZones = WeatherCatalogLoader.Load(dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!); }
        catch (Exception ex)
        {
            weatherError = "读取天气数据失败，请检查本机游戏数据并重新加载插件。";
            log.Error(ex, "加载优雷卡天气表失败");
        }
        automation = new(pluginInterface, framework, clientState, objects, fates, conditions, chat, gameGui, log, party, dataManager, interop, playerState, commandManager, targets, weatherZones.SingleOrDefault(zone => zone.TerritoryId == CommonJourney.Pagos), settings);
        logosInventory=new(framework,objects,playerState,gameGui,logos.Catalog);
        fateTracker = new(dataManager, framework, clientState, objects, fates, gameGui, log, scanner, playerState, chat, party, conditions, settings);
        automation.ElementalMaps = fateTracker;
        bunnies=new(framework,clientState,objects,playerState,chat,fateTracker,log,settings);
        automation.AttachBunnyTracker(bunnies);
        mainWindow = new(inventoryTracker, automation, weatherZones, fateTracker, new GameMapTextures(textures), weatherError, settings, new LogosPage(id=>dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()!.GetRow(id).Name.ExtractText(), id=>dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()!.GetRow(id).Name.ExtractText(), () => objects.LocalPlayer is {} player ? new LogosWorld(player.CurrentWorld.RowId,player.CurrentWorld.Value.Name.ExtractText(),player.CurrentWorld.Value.DataCenter.Value.Name.ExtractText()) : null, logos, settings,()=>logosInventory.Snapshot), new BunnyPage(bunnies,id=>dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()!.GetRow(id).Name.ExtractText(),settings));
        windows.AddWindow(mainWindow);
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开优雷卡助手；/ulk refreshplayers 刷新岛内名单；/ulk entrance 前往入口营地；/ulk stop 停止移动。",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
    }

    private void OnCommand(string command, string arguments)
    {
        if (arguments.Trim().Equals("refreshplayers", StringComparison.OrdinalIgnoreCase)) automation.RefreshPlayers();
        else if (arguments.Trim().Equals("entrance", StringComparison.OrdinalIgnoreCase)) automation.GoToEntrance();
        else if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) automation.Stop();
        else if (arguments.Trim().Equals("discord on", StringComparison.OrdinalIgnoreCase)) automation.EnableDiscord(true);
        else if (arguments.Trim().Equals("discord off", StringComparison.OrdinalIgnoreCase)) automation.EnableDiscord(false);

        else OpenMainUi();
    }

    private void OpenMainUi() => mainWindow.IsOpen = true;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        commandManager.RemoveHandler(CommandName);
        windows.RemoveAllWindows();
        bunnies.Dispose();
        logosInventory.Dispose();
        logos.Dispose();
        automation.Dispose();
        fateTracker.Dispose();
        inventoryTracker.Dispose();
        settings.Flush(true);
    }
}
