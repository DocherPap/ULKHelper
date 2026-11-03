using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

if (args.FirstOrDefault() == "--chat-api")
{
    foreach (var name in Enum.GetNames<Dalamud.Game.Text.XivChatType>())
        Console.WriteLine($"CHAT {name}={(int)Enum.Parse<Dalamud.Game.Text.XivChatType>(name)}");
    foreach (var property in typeof(Dalamud.Game.Text.XivChatEntry).GetProperties()) Console.WriteLine("ENTRY " + property);
    foreach (var method in typeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMap).GetMethods().Where(m => m.Name.Contains("Flag"))) Console.WriteLine("MAP " + method);
    foreach (var type in new[] { typeof(FFXIVClientStructs.FFXIV.Client.UI.UIModule), typeof(FFXIVClientStructs.FFXIV.Client.System.String.Utf8String),
        typeof(Dalamud.Game.Text.SeStringHandling.SeStringBuilder) })
    {
        Console.WriteLine("TYPE " + type.FullName);
        foreach (var method in type.GetMethods().Where(m => m.Name.Contains("Chat") || m.Name.Contains("Sound") || m.Name.Contains("String") || m.Name.Contains("Text") || m.Name.Contains("MapLink") || m.Name.Contains("From") || m.Name is "Instance" or "Dtor" or "Ctor" or "Destroy")) Console.WriteLine(method);
    }
    return;
}

if (args.FirstOrDefault() == "--zone-image")
{
    using var file = File.OpenRead(args[1]);
    using var pe = new System.Reflection.PortableExecutable.PEReader(file);
    var code = pe.GetSectionData(".text").GetContent().ToArray();
    var source = File.ReadAllText("Crowdsource/ZoneServerIdReader.cs");
    foreach (System.Text.RegularExpressions.Match pattern in System.Text.RegularExpressions.Regex.Matches(source, "const string (\\w+) = \"([^\"]+)\""))
    {
        var bytes = pattern.Groups[2].Value.Split(' ').Select(s => s == "??" ? (byte?)null : Convert.ToByte(s, 16)).ToArray();
        var matches = new List<int>();
        for (var p = 0; p <= code.Length - bytes.Length; p++)
        {
            if (code[p] != bytes[0]) continue;
            var match = true;
            for (var i = 1; i < bytes.Length; i++) if (bytes[i] is { } b && code[p + i] != b) { match = false; break; }
            if (match) matches.Add(p);
        }
        Console.WriteLine($"{pattern.Groups[1]} matches={matches.Count}");
        if (pattern.Groups[1].Value == "OffsetPattern" && matches.Count == 1)
            Console.WriteLine($"Field offset=0x{BitConverter.ToInt32(code, matches[0] + 3):X}");
    }
    return;
}

if (args.FirstOrDefault() == "--zone-api")
{
    var assembly = typeof(InventoryManager).Assembly;
    foreach (var type in assembly.GetTypes().Where(t => t.Name.Contains("ContentReply") || t.Name == "GameMain"
        || t.Name == "PublicContentDirector" || t.Name == "PublicContentEureka" || t.Name == "ContentFinder"))
    {
        Console.WriteLine("TYPE " + type.FullName);
        foreach (var f in type.GetFields().Where(f => f.Name != "Addresses" && !f.Name.Contains("VirtualTable"))) Console.WriteLine(f);
        foreach (var m in type.GetMethods().Where(m => !m.IsSpecialName && m.DeclaringType == type)) Console.WriteLine(m);
    }
    return;
}

if (args.FirstOrDefault() == "--map-api")
{
    foreach (var type in new[] { typeof(Lumina.Excel.Sheets.Map), typeof(Lumina.Excel.Sheets.Fate),
        typeof(ITextureProvider), typeof(Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap),
        typeof(Lumina.Data.Files.TexFile) })
    {
        Console.WriteLine("TYPE " + type.FullName);
        foreach (var property in type.GetProperties()) Console.WriteLine(property);
        foreach (var method in type.GetMethods().Where(m => !m.IsSpecialName)) Console.WriteLine(method);
    }
    return;
}

if (args.FirstOrDefault() == "--vnav-api")
{
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "installedPlugins", "vnavmesh", "1.2.3.14");
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        var file = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(file) ? Assembly.LoadFrom(file) : null;
    };
    var assembly = Assembly.LoadFrom(Path.Combine(directory, "vnavmesh.dll"));
    foreach (var type in new[] { "Navmesh.NavmeshManager", "Navmesh.NavmeshQuery", "Navmesh.AsyncMoveRequest" })
    {
        var t = assembly.GetType(type);
        Console.WriteLine(t);
        if (t == null) continue;
        foreach (var m in t.GetMethods().Where(m => m.Name.Contains("Path") || m.Name.Contains("Point") || m.Name == "MoveTo")) Console.WriteLine(m);
    }
    return;
}

if (args.FirstOrDefault() == "--automation-api")
{
    var apiTypes = new[] { typeof(Dalamud.Game.Chat.IChatMessage), typeof(IChatGui), typeof(Lumina.Excel.Sheets.Fate), typeof(WeatherRate), typeof(Weather), typeof(TerritoryType), typeof(IChatGui.OnChatMessageDelegate), typeof(IChatGui.OnHandleableChatMessageDelegate),
        typeof(Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload), typeof(Dalamud.Game.Text.SeStringHandling.Payloads.PlayerPayload),
        typeof(Dalamud.Game.ClientState.Fates.IFate), typeof(Dalamud.Game.ClientState.Fates.FateState),
        typeof(IObjectTable), typeof(IGameGui), typeof(IClientState), typeof(Dalamud.Utility.MapUtil) };
    foreach (var t in apiTypes)
    {
        Console.WriteLine("TYPE " + t.FullName);
        if (t.IsEnum) { Console.WriteLine(string.Join(", ", Enum.GetNames(t))); continue; }
        foreach (var p in t.GetProperties()) Console.WriteLine(p);
        foreach (var m in t.GetMethods().Where(m => !m.IsSpecialName)) Console.WriteLine(m);
        foreach (var c in t.GetConstructors()) Console.WriteLine(c);
    }
    return;
}

if (args.Length > 0)
{
    using var game = new Lumina.GameData(args[0], new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
    if (args.Contains("--chat-commands"))
    {
        foreach (var command in game.Excel.GetSheet<TextCommand>()!)
        {
            var values = typeof(TextCommand).GetProperties().Where(p => p.PropertyType == typeof(Lumina.Text.ReadOnly.ReadOnlySeString))
                .Select(p => p.GetValue(command)?.ToString() ?? "").ToArray();
            if (values.Any(v => v.Contains("新人频道") || v.Contains("新手频道") || System.Text.RegularExpressions.Regex.IsMatch(v, "^/(?:linkshell[1-8]|cwlinkshell[1-8]|novice|pvpteam|echo|cwl[1-8]|n|pvp)$")))
                Console.WriteLine(string.Join(" | ", values.Where(v => !v.Contains('\n'))));
        }
        return;
    }
    if (args.Contains("--map-data"))
    {
        foreach (var id in new uint[] { 732, 763, 795, 827 })
        {
            var territory = game.Excel.GetSheet<TerritoryType>()!.GetRow(id);
            var map = territory.Map.Value;
            var code = map.Id.ExtractText();
            var path = $"ui/map/{code}/{code.Replace("/", "")}_m.tex";
            var tex = game.GetFile<Lumina.Data.Files.TexFile>(path);
            Console.WriteLine($"{id}: map={map.RowId} code={code} scale={map.SizeFactor} offsets={map.OffsetX},{map.OffsetY} texture={path} exists={tex != null}");
            if (tex != null) foreach (var field in tex.GetType().GetFields()) Console.WriteLine($"  {field.Name}: {field.GetValue(tex)}");
        }
        return;
    }
    if (args.Contains("--weather-data"))
    {
        foreach (var t in game.Excel.GetSheet<TerritoryType>()!)
        {
            var name = t.PlaceName.Value.Name.ExtractText();
            if (!new[] { "常风之地", "恒冰之地", "涌火之地", "丰水之地" }.Any(name.Contains)) continue;
            var rates = t.WeatherRate.Value;
            Console.WriteLine($"{t.RowId} {t.Name.ExtractText()} {name} WeatherRate={rates.RowId}");
            for (var i = 0; i < rates.Rate.Count; i++)
                if (rates.Rate[i] > 0) Console.WriteLine($"  {rates.Weather[i].RowId} {rates.Weather[i].Value.Name.ExtractText()} {rates.Rate[i]}%");
        }
        return;
    }
    if (args.Contains("--eureka-fates"))
    {
        var eureka = game.Excel.GetSheet<Lumina.Excel.Sheets.Fate>()!.Where(f => f.EurekaFate == 1).ToArray();
        foreach (var group in eureka.GroupBy(f => f.Rule)) Console.WriteLine($"Eureka=1 Rule={group.Key} Count={group.Count()}");
        foreach (var f in eureka.Where(f => f.Rule != 1)) Console.WriteLine($"{f.RowId}\t{f.Name.ExtractText()}\tRule={f.Rule}");
        return;
    }
    var sheet = game.Excel.GetSheet<Item>()!;
    if (args.Contains("--verify"))
    {
        try
        {
            var catalog = ULKHelper.Inventory.GameCatalogLoader.Load(sheet, game.Excel.GetSheet<SpecialShop>()!);
            Console.WriteLine($"Verified {catalog.Stages.Count} equipment tracks; {catalog.RelevantIds.Count} item IDs; {catalog.Recipes.Count} recipe outputs.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        return;
    }
    foreach (var item in sheet)
    {
        var name = item.Name.ExtractText();
        if (item.Description.ExtractText().Contains("禁地兵装") || name.Contains("麒麟") || name.Contains("朱红") || name.Contains("水晶") && name.Contains("属性"))
            Console.WriteLine($"{item.RowId}\t{name}\t{item.LevelItem.RowId}\t{item.ItemUICategory.RowId}");
    }
    return;
}

var types = new[] { typeof(MirageManager), typeof(RetainerManager), typeof(InventoryContainer), typeof(InventoryItem),
    typeof(FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet), typeof(FFXIVClientStructs.FFXIV.Client.Game.UI.UIState),
    typeof(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentRetainer), typeof(IGameInventory), typeof(IPlayerState),
    typeof(IClientState), typeof(IDataManager), typeof(Item), typeof(Cabinet), typeof(MirageStoreSetItem), typeof(SpecialShop), typeof(SpecialShop.ItemStruct), typeof(SpecialShop.ItemStruct.ReceiveItemsStruct), typeof(SpecialShop.ItemStruct.ItemCostsStruct), typeof(FFXIVClientStructs.FFXIV.Client.UI.Misc.ItemOrderModule) };
foreach (var type in types)
{
    Console.WriteLine("TYPE " + type.FullName);
    foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        if (f.Name != "Addresses" && !f.Name.Contains("VirtualTable")) Console.WriteLine(f);
    foreach (var p in type.GetProperties()) Console.WriteLine(p);
    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        if (!m.IsSpecialName) Console.WriteLine(m);
    foreach (var e in type.GetEvents()) Console.WriteLine(e);
}







