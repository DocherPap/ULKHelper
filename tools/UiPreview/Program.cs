using System.Drawing.Imaging;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using ULKHelper.Data;
using ULKHelper.Windows;
using ULKHelper.Inventory;
using ULKHelper.Automation;
using ULKHelper.Weather;
using ULKHelper.Fates;
using LayoutSettings = ULKHelper.Windows.LayoutSettings;

// Renders the actual page with the installed ImGui library, without a game process.
unsafe class Program
{
    static void Main(string[] args)
    {
        if (args.Contains("--imgui-api"))
        {
            foreach (var method in typeof(ImGui).GetMethods().Where(m => m.Name.Contains("IniSettings") || m.Name.Contains("TableGetColumnWidth"))) Console.WriteLine(method);
            return;
        }
        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.IniFilename = null;
        io.DisplaySize = new Vector2(1280, 780);
        io.DeltaTime = 1f / 60;
        var mapCharacters = "";
        if (args.Contains("--map") || args.Contains("--automation"))
        {
            using var glyphGame = new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack", new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
            mapCharacters = string.Concat(glyphGame.Excel.GetSheet<Lumina.Excel.Sheets.Fate>()!.Where(f => f.EurekaFate == 1).Select(f => f.Name.ExtractText()));
        }
        if(args.Contains("--logos"))
        {
            using var glyphGame=new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack",new Lumina.LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified});
            var c=ULKHelper.Logos.LogosCatalog.Load();
            mapCharacters+=string.Concat(c.Actions.Select(a=>glyphGame.Excel.GetSheet<Lumina.Excel.Sheets.Action>()!.GetRow(a.Action).Name.ExtractText()));
            mapCharacters+=string.Concat(c.Shards.SelectMany(s=>s.Mnemes.Append(s.Item)).Select(id=>glyphGame.Excel.GetSheet<Lumina.Excel.Sheets.Item>()!.GetRow(id).Name.ExtractText()));
        }
        var characters = Directory.GetFiles("Windows", "*.cs").Concat(Directory.GetFiles("Data", "*.cs")).Concat(Directory.GetFiles("Inventory", "*.cs")).Concat(Directory.GetFiles("Automation", "*.cs")).Concat(Directory.GetFiles("Weather", "*.cs")).Concat(Directory.GetFiles("Fates", "*.cs"))
            .Append("tools/WeatherTests/Program.cs").Append("tools/UiPreview/Program.cs").SelectMany(File.ReadAllText).Concat(mapCharacters).Where(c => c > 255).Distinct().Order().ToArray();
        var ranges = new List<ushort> { 0x20, 0xFF };
        foreach (var character in characters) { ranges.Add(character); ranges.Add(character); }
        ranges.Add(0);
        byte* pixels;
        int textureWidth, textureHeight;
        fixed (ushort* glyphs = ranges.ToArray())
        {
            io.Fonts.AddFontFromFileTTF("C:/Windows/Fonts/msyh.ttc", 17, ImFontConfigPtr.Null, glyphs);
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &textureWidth, &textureHeight);
        }
        var textures = new List<AtlasTexture>();
        for (var index = 0; index < io.Fonts.Textures.Size; index++)
        {
            io.Fonts.GetTexDataAsRGBA32(index, &pixels, &textureWidth, &textureHeight);
            var bytes = new byte[textureWidth * textureHeight * 4];
            Marshal.Copy((nint)pixels, bytes, 0, bytes.Length);
            textures.Add(new(bytes, textureWidth, textureHeight));
            io.Fonts.SetTexID(index, new ImTextureID((ulong)index + 1));
        }
        using var logosGame = new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack",new Lumina.LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified});
        using var logosService=new ULKHelper.Logos.LogosService();
        var logosPage=new LogosPage(id=>logosGame.Excel.GetSheet<Lumina.Excel.Sheets.Item>()!.GetRow(id).Name.ExtractText(),id=>logosGame.Excel.GetSheet<Lumina.Excel.Sheets.Action>()!.GetRow(id).Name.ExtractText(),()=>new LogosWorld(1167,"沃仙曦染","陆行鸟"),logosService,null);
        if(args.Contains("--logos-recipes"))SetField(logosPage,"section",1);
        var page = new ItemStatisticsPage(args.Contains("--live") ? new LiveFixtureProvider() : null);
        var automationFixture = new AutomationFixture();
        var debugPage = new DebugTestsPage(automationFixture, new ULKHelper.SettingsStore(Path.GetFullPath("artifacts/ui/debug-settings.json")));
        IReadOnlyList<WeatherZone> weatherZones = [];
        FateMapPage? fateMapPage = null;
        if (args.Contains("--weather") || args.Contains("--map") || args.Contains("--automation"))
        {
            using var game = new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack", new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
            weatherZones = WeatherCatalogLoader.Load(game.Excel.GetSheet<Lumina.Excel.Sheets.TerritoryType>()!);
            if (args.Contains("--map") || args.Contains("--automation"))
            {
                var maps = FateCatalogLoader.Load(game.Excel.GetSheet<Lumina.Excel.Sheets.TerritoryType>()!, game.Excel.GetSheet<Lumina.Excel.Sheets.Fate>()!);
                var textureSource = new MapFixtureTextures();
                foreach (var map in maps)
                {
                    var tex = game.GetFile<Lumina.Data.Files.TexFile>(map.TexturePath)!;
                    var bytes = tex.ImageData.ToArray();
                    // Lumina's A8R8G8B8 image is BGRA in memory; the rasterizer expects RGBA.
                    for (var p = 0; p < bytes.Length; p += 4) (bytes[p], bytes[p + 2]) = (bytes[p + 2], bytes[p]);
                    textures.Add(new(bytes, tex.Header.Width, tex.Header.Height));
                    textureSource.Handles[map.TexturePath] = new ImTextureID((ulong)textures.Count);
                }
                fateMapPage = new(new MapFixtureProvider(maps), textureSource, weatherZones, automationFixture);
                SetField(fateMapPage, "follow", false);
                if (args.Contains("--pagos")) SetField(fateMapPage, "islandIndex", 1);
                if (args.Contains("--pyros")) SetField(fateMapPage, "islandIndex", 2);
                if (args.Contains("--hydatos")) SetField(fateMapPage, "islandIndex", 3);
                if (args.Contains("--map-selected") || args.Contains("--add-trigger")) SetField(fateMapPage, "selected", (ushort)1332);
                if (args.Contains("--trigger-list"))
                {
                    var plan = (TriggerSelection)GetField(fateMapPage, "triggerSelection");
                    var map = maps[(int)GetField(fateMapPage, "islandIndex")];
                    plan.SetIsland(map.Territory);
                    foreach (var point in map.Fates.Where(f => !f.Seed.Bunny).Take(3)) plan.Add(point);
                }
                if (args.Contains("--map-pan")) SetField(fateMapPage, "zoom", 2f);
                if (args.Contains("--debug-notify"))
                {
                    SetField(fateMapPage, "notificationInitialized", true);
                    SetField(fateMapPage, "notificationEnabled", true);
                    SetField(fateMapPage, "notificationChannel", (int)FateNotificationChannel.Debug);
                }
            }
        }
        var automation = new AutomationPage(automationFixture, fateMapPage);
        var weatherPage = new WeatherPage(weatherZones);
        if (args.Contains("--weather-long")) SetField(weatherPage, "countIndex", 2);
        if (args.Contains("--utc")) SetField(weatherPage, "utc", true);
        if (args.Contains("--job")) SetField(page, "byJob", true);
        if (args.Contains("--overall")) SetField(page, "overall", true);
        if (args.Contains("--unknown")) SetField(page, "showExamples", false);
        if (args.Contains("--empty")) Array.Fill((bool[])GetField(page, "selectedJobs"), false);
        if (args.Contains("--one-job"))
        {
            var jobs = (bool[])GetField(page, "selectedJobs");
            Array.Fill(jobs, false);
            jobs[0] = true;
        }
        if (args.Contains("--small")) io.DisplaySize = new Vector2(1000, 520);
        if (args.Contains("--large")) io.DisplaySize = new Vector2(1600, 1000);
        typeof(ItemStatisticsPage).GetMethod("RebuildPreview", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        ValidateCatalog();
        for (var frame = 0; frame < 8; frame++)
        {
            if(args.Contains("--logos-recipes"))
            {
                io.AddMousePosEvent(-100,-100);
                if(frame==3)io.AddMouseButtonEvent(0,true);if(frame==4)io.AddMouseButtonEvent(0,false);
            }
            if (args.Contains("--start-trigger"))
            {
                io.AddMousePosEvent(40, 483);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--debug-read") || args.Contains("--debug-mount"))
            {
                io.AddMousePosEvent(65, args.Contains("--debug-mount") ? 281 : 111);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--settings-roundtrip"))
            {
                io.AddMousePosEvent(frame >= 4 ? 280 : 236, 350);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 5) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--click-fate-go") || args.Contains("--click-entrance"))
            {
                io.AddMousePosEvent(args.Contains("--click-entrance") ? 70 : 878, args.Contains("--click-entrance") ? 166 : 285);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--click-target-leader") || args.Contains("--open-history"))
            {
                io.AddMousePosEvent(io.DisplaySize.X * .65f, args.Contains("--open-history") ? 469 : 233);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--click-job")) io.AddMousePosEvent(20, 124);
            if (args.Contains("--click-follow") || args.Contains("--open-mount"))
            {
                io.AddMousePosEvent(io.DisplaySize.X * .65f, args.Contains("--open-mount") ? 301 : 72);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--add-trigger"))
            {
                io.AddMousePosEvent(125, 735);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--automation-map-click"))
            {
                io.AddMousePosEvent(258, 396);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--click-filter")) io.AddMousePosEvent(20, 182);
            if (args.Contains("--map-click") || args.Contains("--map-scroll"))
            {
                io.AddMousePosEvent(262, 467);
                if (args.Contains("--map-click"))
                {
                    if (frame == 3) io.AddMouseButtonEvent(0, true);
                    if (frame == 4) io.AddMouseButtonEvent(0, false);
                }
                if (args.Contains("--map-scroll") && frame == 3) io.AddMouseWheelEvent(0, 1);
            }
            if (args.Contains("--map-pan"))
            {
                io.AddMousePosEvent(frame < 4 ? 350 : 400, frame < 4 ? 500 : 525);
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 5) io.AddMouseButtonEvent(0, false);
            }
            if (args.Contains("--hover-material")) io.AddMousePosEvent(1080, 220);
            if (args.Contains("--hover-equipment")) io.AddMousePosEvent(330, 220);
            if (args.Contains("--click-job") || args.Contains("--click-filter"))
            {
                if (frame == 3) io.AddMouseButtonEvent(0, true);
                if (frame == 4) io.AddMouseButtonEvent(0, false);
            }
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(io.DisplaySize);
            ImGui.Begin("优雷卡助手", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar);
            var tabWidth = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemInnerSpacing.X * 5) / 6;
            if (ImGui.BeginTabBar("##MainPages"))
            {
                ImGui.SetNextItemWidth(tabWidth);
                if (ImGui.BeginTabItem("物品统计", args.Contains("--automation") || args.Contains("--weather") || args.Contains("--map") || args.Contains("--debug") || args.Contains("--logos") ? 0 : ImGuiTabItemFlags.SetSelected))
                {
                    page.Draw();
                    ImGui.EndTabItem();
                }
                ImGui.SetNextItemWidth(tabWidth);
                if (ImGui.BeginTabItem("天气预报", args.Contains("--weather") ? ImGuiTabItemFlags.SetSelected : 0))
                {
                    if (ImGui.BeginChild("##WeatherPageContent", Vector2.Zero)) weatherPage.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
                ImGui.SetNextItemWidth(tabWidth);
                if (ImGui.BeginTabItem("地图 / FATE", args.Contains("--map") ? ImGuiTabItemFlags.SetSelected : 0))
                {
                    if (ImGui.BeginChild("##MapPageContent", Vector2.Zero)) fateMapPage?.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
                ImGui.SetNextItemWidth(tabWidth);
                if (ImGui.BeginTabItem("文理技能",args.Contains("--logos")?ImGuiTabItemFlags.SetSelected:0))
                {
                    if(ImGui.BeginChild("##LogosPreview",Vector2.Zero))logosPage.Draw();
                    ImGui.EndChild();ImGui.EndTabItem();
                }
                ImGui.SetNextItemWidth(tabWidth);
                if (ImGui.BeginTabItem("自动化", args.Contains("--automation") ? ImGuiTabItemFlags.SetSelected : 0))
                {
                    if (ImGui.BeginChild("##AutomationPageContent", Vector2.Zero)) automation.Draw();
                    ImGui.EndChild();
                    ImGui.EndTabItem();
                }
                ImGui.SetNextItemWidth(tabWidth);
                if (ImGui.BeginTabItem("调试测试", args.Contains("--debug") ? ImGuiTabItemFlags.SetSelected : 0))
                {
                    if (ImGui.BeginChild("##DebugContent", Vector2.Zero)) debugPage.Draw();
                    ImGui.EndChild(); ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            ImGui.End();
            ImGui.Render();
        }
        if (args.Contains("--start-trigger") && automationFixture.TriggerPlans?.Count != 3) throw new Exception("Trigger start input failed");
        if (args.Contains("--click-job") && !(bool)GetField(page, "byJob")) throw new Exception("Job radio input failed");
        if (args.Contains("--debug-read") && automationFixture.LastDebugAction != SoloDebugAction.Inspect) throw new Exception("Debug inspect button input failed");
        if (args.Contains("--debug-mount") && automationFixture.LastDebugAction != SoloDebugAction.Mount) throw new Exception("Debug mount button input failed");
        if (args.Contains("--click-fate-go") && automationFixture.PointGoal?.Territory != 732) throw new Exception("FATE travel button input failed");
        if (args.Contains("--click-entrance") && !automationFixture.EntranceRequested) throw new Exception("Entrance travel button input failed");
        if (args.Contains("--settings-roundtrip"))
        {
            var settingsPath = Path.GetFullPath("artifacts/ui/preview-settings.json");
            var settings = new ULKHelper.SettingsStore(settingsPath);
            page.Save(settings); weatherPage.Save(settings); fateMapPage?.Save(settings, "map"); LayoutSettings.Capture(settings); settings.Flush(true);
            var reloaded = new ULKHelper.SettingsStore(settingsPath);
            var reloadedPage = new ItemStatisticsPage(); reloadedPage.Load(reloaded);
            if ((bool)GetField(page,"byJob") != (bool)GetField(reloadedPage,"byJob") || !((bool[])GetField(page,"selectedJobs")).SequenceEqual((bool[])GetField(reloadedPage,"selectedJobs"))) throw new Exception("Page preferences roundtrip failed");
            if (!reloaded.Get("layout.tables", "").Contains("[Table]"))
            { File.WriteAllText("artifacts/ui/preview-layout.ini", ImGui.SaveIniSettingsToMemory()); throw new Exception("Own table layout persistence failed"); }
            LayoutSettings.Restore(reloaded);
            Console.WriteLine("Page preferences and own table layout JSON roundtrip passed.");
        }
        if (args.Contains("--click-follow") && !automationFixture.Snapshot.Following) throw new Exception("Follow button input failed");
        if (args.Contains("--click-target-leader") && automationFixture.Snapshot.Leader?.Name != "当前目标示例") throw new Exception("Current target leader button input failed");
        if (args.Contains("--add-trigger") && !((TriggerSelection)GetField(fateMapPage!, "triggerSelection")).Ids.Contains((ushort)1332)) throw new Exception("Trigger plan add input failed");
        if (args.Contains("--automation-map-click") && (ushort)GetField(fateMapPage!, "selected") != 1332) throw new Exception("Embedded map marker input failed");
        if (args.Contains("--click-filter") && ((bool[])GetField(page, "selectedJobs"))[0]) throw new Exception("Job checkbox input failed");
        if (args.Contains("--map-click") && (ushort)GetField(fateMapPage!, "selected") != 1332) throw new Exception("Map marker click failed");
        if (args.Contains("--map-scroll") && (float)GetField(fateMapPage!, "zoom") <= 1) throw new Exception("Map wheel zoom failed");
        if (args.Contains("--map-pan") && ((Vector2)GetField(fateMapPage!, "centre") == new Vector2(.5f)
            || (ushort)GetField(fateMapPage!, "selected") != 0)) throw new Exception("Map pan or drag selection failed");
        Directory.CreateDirectory("artifacts/ui");
        var path = Path.GetFullPath(Path.Combine("artifacts/ui", args.FirstOrDefault(a => a.EndsWith(".png")) ?? "statistics.png"));
        Render(path, ImGui.GetDrawData(), textures);
        Console.WriteLine($"Rendered {path}; {ImGui.GetDrawData().TotalVtxCount} vertices");
        ImGui.DestroyContext();
    }

    static object GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    sealed class MapFixtureTextures : IMapTextures
    {
        public readonly Dictionary<string, ImTextureID> Handles = new();
        public ImTextureID? Get(string path, out string? error) { error = null; return Handles.GetValueOrDefault(path); }
    }

    sealed class MapFixtureProvider : IFateMapProvider
    {
        public IReadOnlyList<IslandMap> Islands { get; }
        public FateMapSnapshot Snapshot { get; }
        public MapFixtureProvider(IReadOnlyList<IslandMap> islands)
        {
            Islands = islands;
            var now = DateTimeOffset.UtcNow;
            var ledger = new FateLedger(islands);
            foreach (var island in islands)
            {
                ledger.Record(island.Fates[0].Seed.Id, 30, now);
                ledger.Record(island.Fates[1].Seed.Id, 130, now);
            }
            ledger.Observe([new(1332, now.AddMinutes(-3), now.AddMinutes(12), 42, FateActivity.Running)], now);
            Snapshot = new(732, new Vector2(19, 25), ledger.Snapshot, "离线预览：示例活动/计时记录，非真实实例。");
        }
        public void Mark(FateDefinition fate) { }
        public void RecordAppearance(ushort id, double minutesAgo) { }
        public void ClearRecord(ushort id) { }
        public void ConfigureNotifications(FateNotificationSettings settings) { }
    }

    sealed class AutomationFixture : IAutomationHelpers
    {
        public IReadOnlyList<TriggerPlan>? TriggerPlans;
        public void StartTrigger(IReadOnlyList<TriggerPlan> plans, int batch, int maxKills) => TriggerPlans = plans;
        public AutoTriggerSnapshot TriggerSnapshot => new(false, "离线模拟：尚未开始自动触发。", 20,
            [new(1332, new(-350,0,20)),new(1332,new(-330,0,10)),new(1348,new(300,0,300))], new Dictionary<ushort,Vector3[]>());
        public SoloDebugAction? LastDebugAction;
        public SoloDebugSnapshot DebugSnapshot => new(false,"示例：等待测试。","区域=732 地图=414\nvnavmesh Ready=True；现场状态为离线模拟。",
            [new(2415,"港口营地"), new(2429,"废弃实验室"), new(2436,"风蚀小屋")], []);
        public void RunDebug(SoloDebugAction action, uint crystalId = 0) => LastDebugAction = action;
        public MapDestination? PointGoal;
        public bool EntranceRequested;
        public void NavigateTo(MapDestination destination) => PointGoal = destination;
        public void GoToEntrance() => EntranceRequested = true;
        public AutomationSnapshot Snapshot { get; private set; } = new(
            Enumerable.Range(0, 30).Select(i => new PlayerEntry(new($"示例玩家{i + 1:00}", 100), "示例服务器", new(i, 0, 0), i * 12)).ToArray(),
            new("示例玩家02", 100), true, "已在地图标记目的地。", new(1, 2, new(200, 0, 0), "禁地优雷卡 常风之地 ( 25.0, 30.0 )"),
            [new(DateTimeOffset.Now, "小怪击杀", "仙人掌 · 仙人花 +2", "本轮本地观测累计12只"),
             new(DateTimeOffset.Now, "Shout", "示例车头：准备出发 (25.0, 30.0)", "已在地图标记目的地。"),
             new(DateTimeOffset.Now, "Yell", "示例车头：等待一下", "已记录，未发现坐标。")], false, "等待车头的新坐标。", null,
            [new(1, "陆行鸟"), new(5, "独角兽"), new(99, "示例不可移动坐骑", true)], 1,
            KillCounts: [new(1332,"仙人掌","仙人花",12),new(1348,"章鱼","海祭司",3)]);
        public void SelectLeader(PlayerIdentity identity) { }
        public void SelectCurrentTargetAsLeader() => Snapshot = Snapshot with { Leader = new("当前目标示例", 100) };
        public void Listen(bool enabled) { }
        public void Navigate() { }
        public void Stop() { }
        public void Follow(bool enabled) => Snapshot = Snapshot with { Following = enabled, Navigating = enabled };
        public void SelectMount(uint id) => Snapshot = Snapshot with { SelectedMount = id };
    }

    // Exercises the production data-provider UI without accessing the game or a real character.
    sealed class LiveFixtureProvider : IStatisticsProvider
    {
        public long Revision => 1;
        public bool HasData => true;
        public string StatusText => "已计入已获取库存 · 部分来源未获取";
        public IReadOnlyList<SourceSnapshot> Sources { get; } = OwnershipLedger.BaseSources.Select((s, i) =>
            new SourceSnapshot { Key = s.Key, Label = s.Label, ReadThisSession = i < 3,
                UpdatedAt = i < 4 ? DateTimeOffset.UtcNow : null }).Concat(new[]
            {
                new SourceSnapshot { Key = "retainer:fixture:bags", Label = "雇员 示例（背包）" },
                new SourceSnapshot { Key = "retainer:fixture:gear", Label = "雇员 示例（穿戴）" },
            }).ToArray();
        public int[] OwnedStages(EquipmentCard card) => StatisticsPreview.GetOwnedStages(card, true);
        public int CompletedStage(EquipmentCard card) => OwnedStages(card).DefaultIfEmpty(-1).Max();
        public MaterialPreview[] Summarize(IEnumerable<EquipmentCard> cards) => StatisticsPreview.Summarize(cards, true);
    }

    static void ValidateCatalog()
    {
        var cards = StatisticsCatalog.Equipment;
        if (cards.Length != 132 || cards.Select(c => c.Key).Distinct().Count() != cards.Length)
            throw new Exception("Catalog count or duplicate key error");
        foreach (var card in cards)
        {
            if (card.Jobs.Any(j => !StatisticsCatalog.Jobs.Any(o => o.Key == j))) throw new Exception("Unknown job");
            if (StatisticsPreview.GetOwnedStages(card, true).Any(s => s < 0 || s >= card.Stages.Length)) throw new Exception("Invalid preview stage");
        }
        var totals = StatisticsPreview.Summarize(cards, false).ToDictionary(v => v.Material.Name, v => v.Total);
        if (totals["乱属性水晶"] != 64500 || totals["常风水晶"] != 11250 || totals["涌火水晶"] != 11150 ||
            totals["丰水水晶"] != 6580 || totals["优雷卡的断片"] != 2431 || totals["神秘印章"] != 10 || totals["大马士革织物"] != 6)
            throw new Exception("Catalog material totals mismatch");
        var repeated = StatisticsPreview.Summarize(cards.Concat(cards), true);
        var once = StatisticsPreview.Summarize(cards, true);
        if (!repeated.SequenceEqual(once)) throw new Exception("Shared equipment counted more than once");
        Console.WriteLine("Catalog verified: 15 weapons, 75 artifact pieces, 42 elemental/special pieces; shared totals deduplicated.");
    }

    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    sealed record AtlasTexture(byte[] Pixels, int Width, int Height);

    static void Render(string path, ImDrawDataPtr data, List<AtlasTexture> textures)
    {
        var width = (int)data.DisplaySize.X;
        var height = (int)data.DisplaySize.Y;
        var output = new byte[width * height * 4];
        for (var i = 0; i < output.Length; i += 4) { output[i] = 24; output[i + 1] = 22; output[i + 2] = 20; output[i + 3] = 255; }
        for (var li = 0; li < data.CmdListsCount; li++)
        {
            var list = new ImDrawListPtr(data.CmdLists[li]);
            for (var ci = 0; ci < list.CmdBuffer.Size; ci++)
            {
                var cmd = list.CmdBuffer[ci];
                if (cmd.UserCallback != null) continue;
                var texture = textures[(int)cmd.TextureId.Handle - 1];
                var texWidth = texture.Width;
                var texHeight = texture.Height;
                for (var idx = 0; idx < cmd.ElemCount; idx += 3)
                {
                    var a = list.VtxBuffer[(int)(list.IdxBuffer[(int)cmd.IdxOffset + idx] + cmd.VtxOffset)];
                    var b = list.VtxBuffer[(int)(list.IdxBuffer[(int)cmd.IdxOffset + idx + 1] + cmd.VtxOffset)];
                    var c = list.VtxBuffer[(int)(list.IdxBuffer[(int)cmd.IdxOffset + idx + 2] + cmd.VtxOffset)];
                    var area = Cross(b.Pos - a.Pos, c.Pos - a.Pos);
                    if (Math.Abs(area) < .0001f) continue;
                    var minX = Math.Max(0, (int)Math.Max(cmd.ClipRect.X, MathF.Floor(Math.Min(a.Pos.X, Math.Min(b.Pos.X, c.Pos.X)))));
                    var maxX = Math.Min(width, (int)Math.Min(cmd.ClipRect.Z, MathF.Ceiling(Math.Max(a.Pos.X, Math.Max(b.Pos.X, c.Pos.X)))));
                    var minY = Math.Max(0, (int)Math.Max(cmd.ClipRect.Y, MathF.Floor(Math.Min(a.Pos.Y, Math.Min(b.Pos.Y, c.Pos.Y)))));
                    var maxY = Math.Min(height, (int)Math.Min(cmd.ClipRect.W, MathF.Ceiling(Math.Max(a.Pos.Y, Math.Max(b.Pos.Y, c.Pos.Y)))));
                    for (var y = minY; y < maxY; y++)
                        for (var x = minX; x < maxX; x++)
                        {
                            var point = new Vector2(x + .5f, y + .5f);
                            var u = Cross(b.Pos - point, c.Pos - point) / area;
                            var v = Cross(c.Pos - point, a.Pos - point) / area;
                            var w = 1 - u - v;
                            if (u < 0 || v < 0 || w < 0) continue;
                            var uv = a.Uv * u + b.Uv * v + c.Uv * w;
                            var tx = Math.Clamp((int)(uv.X * texWidth), 0, texWidth - 1);
                            var ty = Math.Clamp((int)(uv.Y * texHeight), 0, texHeight - 1);
                            var sample = (ty * texWidth + tx) * 4;
                            var alpha = (((a.Col >> 24) * u + (b.Col >> 24) * v + (c.Col >> 24) * w) / 255) * texture.Pixels[sample + 3] / 255;
                            var pixel = (y * width + x) * 4;
                            for (var channel = 0; channel < 3; channel++)
                            {
                                var shift = channel * 8;
                                var col = ((a.Col >> shift & 255) * u + (b.Col >> shift & 255) * v + (c.Col >> shift & 255) * w) * texture.Pixels[sample + channel] / 255;
                                var bgra = pixel + 2 - channel;
                                output[bgra] = (byte)(col * alpha + output[bgra] * (1 - alpha));
                            }
                        }
                }
            }
        }
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(output, 0, bits.Scan0, output.Length);
        bitmap.UnlockBits(bits);
        bitmap.Save(path, ImageFormat.Png);
    }
}
