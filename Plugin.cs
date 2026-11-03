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
    private bool disposed;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IFramework framework,
        IClientState clientState, IPlayerState playerState, IDataManager dataManager, IGameInventory gameInventory,
        IAddonLifecycle addonLifecycle, IPluginLog log, IGameGui gameGui, IObjectTable objects,
        IFateTable fates, ICondition conditions, IChatGui chat, ITextureProvider textures, ISigScanner scanner)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        inventoryTracker = new(pluginInterface, framework, clientState, playerState, dataManager, gameInventory, addonLifecycle, log, gameGui);
        automation = new(pluginInterface, framework, clientState, objects, fates, conditions, chat, gameGui, log);
        IReadOnlyList<WeatherZone> weatherZones = [];
        string? weatherError = null;
        try { weatherZones = WeatherCatalogLoader.Load(dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!); }
        catch (Exception ex)
        {
            weatherError = "读取天气数据失败，请检查本机游戏数据并重新加载插件。";
            log.Error(ex, "加载优雷卡天气表失败");
        }
        fateTracker = new(dataManager, framework, clientState, objects, fates, gameGui, log, scanner, playerState, chat);
        mainWindow = new(inventoryTracker, automation, weatherZones, fateTracker, new GameMapTextures(textures), weatherError);
        windows.AddWindow(mainWindow);
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开优雷卡助手。",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
    }

    private void OnCommand(string command, string arguments) => OpenMainUi();

    private void OpenMainUi() => mainWindow.IsOpen = true;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        commandManager.RemoveHandler(CommandName);
        windows.RemoveAllWindows();
        automation.Dispose();
        fateTracker.Dispose();
        inventoryTracker.Dispose();
    }
}
