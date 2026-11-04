using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
if(args.FirstOrDefault()=="--bunny-loot"){
 using var cn=new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack",new Lumina.LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified});
 using var en=new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack",new Lumina.LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.English});
 var names=new[]{"Pagos Sage","Pagos Spring Water","Happy Bunny Card","Stuffed Happy Bunny","Wooden Hakutaku Eye","Burning Hakutaku Eye","Earthen Hakutaku Eye","Damp Hakutaku Eye","Copycat Bulb","Modern Aesthetics - Form and Function","Archaeodemon Horns","Eldthurs Horn","Aethertight Flask","Aetherially Conductive Plate","Absolute Lance","Penthesilea's Spear","Eurekan Petrel Horn"};
 foreach(var r in cn.Excel.GetSheet<Item>()!.Where(r=>r.Name.ExtractText().Contains("恒冰")&&r.Name.ExtractText().Contains("水")))Console.WriteLine($"WATER {r.RowId} {r.Name}");
 foreach(var id in new uint[]{23348,23349,23027,24219,24630,24820,24904,24233,23049,23229,23213,23214,23215,23217,24338,24858,24998,24007,24008,24009,24010,24011,24013,24014})Console.WriteLine($"ITEM {id} {cn.Excel.GetSheet<Item>()!.GetRow(id).Name}");
 Console.WriteLine("LOGTYPES "+string.Join(";",typeof(LogMessage).GetProperties().Select(p=>p.ToString())));return;}
if(args.FirstOrDefault()=="--bunny-data"){
 using var game=new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack",new Lumina.LuminaOptions{DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified});
 foreach(var r in game.Excel.GetSheet<Lumina.Excel.Sheets.Status>()!.Where(r=>r.Name.ExtractText().Contains("兔")||r.Name.ExtractText().Contains("幸福")||r.Description.ExtractText().Contains("兔")||r.Name.ExtractText().Contains("财宝")))Console.WriteLine($"STATUS {r.RowId} {r.Name} {r.Description}");
 foreach(var r in game.Excel.GetSheet<EventItem>()!.Where(r=>r.Name.ExtractText().Contains("萝卜")))Console.WriteLine($"EVENTITEM {r.RowId} {r.Name}");
 foreach(var r in game.Excel.GetSheet<LogMessage>()!.Where(r=>r.Text.ExtractText().Contains("财宝")||r.Text.ExtractText().Contains("胡萝卜")||r.Text.ExtractText().Contains("遥远")||r.Text.ExtractText().Contains("比较近")||r.Text.ExtractText().Contains("就在这")||r.Text.ExtractText().Contains("非常近")||r.Text.ExtractText().Contains("幸福兔")))Console.WriteLine($"LOG {r.RowId} Kind={r.LogKind.RowId} {r.Text}");
 foreach(var t in new[]{typeof(Dalamud.Game.Text.XivChatType),typeof(FFXIVClientStructs.FFXIV.Client.Game.ActionManager)})foreach(var e in t.IsEnum?Enum.GetNames(t).Where(n=>n.Contains("System")||n.Contains("Message")):Array.Empty<string>())Console.WriteLine(e);
 return;}
if(args.FirstOrDefault()=="--logos-native"){
 foreach(var t in typeof(InventoryManager).Assembly.GetTypes().Where(t=>t.Name.Contains("Logos")||t.Name.Contains("Eureka")||t.Name.Contains("Magia")))
 {Console.WriteLine("TYPE "+t.FullName);foreach(var f in t.GetFields().Where(f=>!f.Name.Contains("Virtual")))Console.WriteLine(f);foreach(var p in t.GetProperties())Console.WriteLine(p);}
 return;}
if(args.FirstOrDefault()=="--magia-api"){
 foreach(var t in new[]{typeof(FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework),typeof(FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.PublicContentEureka),typeof(FFXIVClientStructs.FFXIV.Client.Game.UI.UIState)})foreach(var m in t.GetMethods().Where(m=>m.Name.Contains("Instance")||m.Name.Contains("PublicContent")||m.Name.Contains("Eureka")))Console.WriteLine(t.Name+" "+m);return;}
if (args.FirstOrDefault() == "--rsr-settings")
{
 var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"XIVLauncherCN/installedPlugins/RotationSolver/7.5.7.15");
 System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_,n)=>AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==n.Name) ?? (File.Exists(Path.Combine(dir,n.Name+".dll"))?Assembly.LoadFrom(Path.Combine(dir,n.Name+".dll")):null);
 foreach(var file in Directory.GetFiles(dir,"*.dll").Where(f=>Path.GetFileName(f)=="RotationSolver.dll"))
 {var a=Assembly.LoadFrom(file); Console.WriteLine("DIRECTSERVICE "+a.GetType("RotationSolver.Basic.Service",false));Type[] ts;try{ts=a.GetTypes();}catch(ReflectionTypeLoadException e){ts=e.Types.OfType<Type>().ToArray();}
 Console.WriteLine("SERVICES "+string.Join(",",ts.Where(t=>t.Name.Contains("Service")||t.Name.Contains("Svc")).Select(t=>t.FullName)));
 foreach(var t0 in ts.Where(t=>!t.FullName!.Contains("SourceGenerator"))) foreach(var f0 in t0.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Where(f=>f.FieldType.Name=="Configs"))Console.WriteLine("ROOT "+t0.FullName+" "+f0);
 foreach(var t0 in ts) foreach(var pp in t0.GetProperties(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Where(p=>p.Name.Contains("Config")))Console.WriteLine("ROOTPROP "+t0.FullName+" "+pp);
 foreach(var t in ts.Where(t=>t.FullName!.Contains("Config")||t.Name.Contains("Targeting")||t.Name=="DataCenter"||t.Name=="StateCommandType"||t.Name=="Service"||t.Name=="TargetHostileType"))
 {var props=t.GetProperties().Where(p=>p.Name.Contains("Target")||p.Name.Contains("Hostile")||p.Name.Contains("Config")||p.Name.Contains("State")||p.Name.Contains("Manual")).ToArray();
 if(props.Length>0||t.IsEnum){Console.WriteLine("TYPE "+t.FullName);foreach(var p in props)Console.WriteLine(p);foreach(var f in t.GetFields(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Where(f=>f.FieldType.Name=="Configs"))Console.WriteLine("CONFIG FIELD "+f);if(t.IsEnum)Console.WriteLine(string.Join(",",Enum.GetNames(t)));}}
 }
 return;
}
if (args.FirstOrDefault() == "--logos-data")
{
 using var game = new Lumina.GameData("D:/Program Files (x86)/FFXIV/game/sqpack",new Lumina.LuminaOptions { DefaultExcelLanguage=Lumina.Data.Language.ChineseSimplified });
 foreach(var r in game.Excel.GetSheet<EurekaAetherItem>()!)Console.WriteLine($"AETHER {r.RowId} {r.Name} {r.Singular}");
 foreach(var r in game.Excel.GetSheet<EurekaMagiciteItem>()!)Console.WriteLine($"MAGICITE {r.RowId} {r.Item.RowId} {r.Item.Value.Name} {r.EurekaMagiciteItemType.Value.Type}");
 foreach(var r in game.Excel.GetSheet<EurekaMagiaAction>()!)Console.WriteLine($"ACTION {r.RowId} {r.Action.RowId} {r.Action.Value.Name}");
 foreach(var r in game.Excel.GetSheet<TextCommand>()!.Where(r=>r.Command.ExtractText().Contains("magia")))Console.WriteLine($"COMMAND {r.RowId} {r.Command} {r.Alias}");
 return;
}
if (args.FirstOrDefault() == "--logos-types")
{
 foreach(var t in typeof(Lumina.Excel.Sheets.Item).Assembly.GetTypes().Where(t=>t.Name.Contains("Eureka") || t.Name.Contains("Logos"))) {Console.WriteLine("TYPE "+t.FullName);foreach(var p in t.GetProperties())Console.WriteLine(p);}
 foreach(var t in typeof(FFXIVClientStructs.FFXIV.Client.Game.InventoryManager).Assembly.GetTypes().Where(t=>t.Name.Contains("Eureka")||t.Name.Contains("Magia"))) {Console.WriteLine("NATIVE "+t.FullName);foreach(var f in t.GetFields())Console.WriteLine(f);foreach(var m in t.GetMethods().Where(m=>m.Name.Contains("Magia")||m.Name.Contains("Rotate")))Console.WriteLine(m);}
 return;
}





if (args.FirstOrDefault() == "--combat-api")
{
    Console.WriteLine("IPC " + typeof(Dalamud.Plugin.Ipc.ICallGateSubscriber<,>).GetMethod("InvokeAction"));
    foreach (var m in typeof(Dalamud.Plugin.IDalamudPluginInterface).GetMethods().Where(m => m.Name == "GetIpcSubscriber" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2)) Console.WriteLine("FACTORY " + m);
    foreach (var t in new[] { typeof(FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState), typeof(FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager), typeof(Lumina.Excel.Sheets.Mount), typeof(ITargetManager), typeof(ICommandManager) })
    {
        Console.WriteLine("TYPE " + t.FullName);
        foreach (var p in t.GetProperties()) Console.WriteLine(p);
        foreach (var m in t.GetMethods().Where(m => m.Name.Contains("Mount") || m.Name.Contains("Sync") || m.Name.Contains("Command"))) Console.WriteLine(m);
    }
    var pluginRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN/installedPlugins");
    var dirs = new[] { Path.Combine(pluginRoot, "BossModReborn/7.5.7.3"), Path.Combine(pluginRoot, "RotationSolver/7.5.7.13") };
    System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => {
        foreach (var d in dirs) { var file = Path.Combine(d, name.Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); }
        return null;
    };
    foreach (var file in new[] { Path.Combine(dirs[0], "BossModReborn.dll"), Path.Combine(dirs[1], "RotationSolver.dll") })
    {
        var a = Assembly.LoadFrom(file); Type[] inspectedTypes;
        try { inspectedTypes = a.GetTypes(); } catch (ReflectionTypeLoadException e) { inspectedTypes = e.Types.OfType<Type>().ToArray(); }
        foreach (var t in inspectedTypes.Where(t => t.FullName is "BossMod.AI.AIManager" or "BossMod.IPCProvider" or "RotationSolver.IPC.IPCProvider" or "RotationSolver.Basic.DataCenter" || t.Name == "StateCommandType"))
        {
            Console.WriteLine("PLUGIN " + t.FullName);
            foreach (var f in t.GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)) Console.WriteLine(f);
            foreach (var p in t.GetProperties(BindingFlags.Public|BindingFlags.Static).Where(p => p.Name is "State" or "IsManual")) Console.WriteLine(p);
            foreach (var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(m => m.Name is "AutorotationActive" or "ChangeOperatingMode" or "Register")) Console.WriteLine(m);
        }
    }
    return;
}

if (args.FirstOrDefault() == "--travel-api")
{
    foreach (var t in new[] { typeof(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyContentMember), typeof(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyCommonList), typeof(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyPageInterface), typeof(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyInterface), typeof(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyContentMember.ContentMemberData), typeof(FFXIVClientStructs.FFXIV.Client.UI.AddonSelectString), typeof(FFXIVClientStructs.FFXIV.Client.UI.PopupMenu), typeof(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase) })
    {
        Console.WriteLine("TYPE " + t.FullName);
        foreach (var f in t.GetFields().Where(f => !f.Name.Contains("Virtual") && f.Name != "Addresses")) Console.WriteLine(f);
        foreach (var p in t.GetProperties()) Console.WriteLine(p);
        foreach (var m in t.GetMethods().Where(m => m.Name is "Instance" or "RequestData" or "FireCallback" or "Close")) Console.WriteLine(m);
    }
    return;
}

if (args.FirstOrDefault() == "--arrival-api")
{
    foreach (var type in typeof(InventoryManager).Assembly.GetTypes().Where(t => t.IsValueType))
        foreach (var field in type.GetFields().Where(f => f.Name.Contains("Elemental") || f.Name.Contains("Eureka") || f.Name.Contains("MountId"))) Console.WriteLine("MATCH " + type.FullName + " " + field);
    foreach (var t in new[] { typeof(Dalamud.Game.ClientState.Objects.Types.IBattleChara), typeof(Dalamud.Game.ClientState.Objects.Types.IBattleNpc), typeof(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter), typeof(IPartyList), typeof(Dalamud.Game.ClientState.Party.IPartyMember), typeof(Dalamud.Game.ClientState.Fates.IFate), typeof(Lumina.Excel.Sheets.Fate), typeof(Lumina.Excel.Sheets.GeneralAction), typeof(FFXIVClientStructs.FFXIV.Client.Game.Character.Character), typeof(FFXIVClientStructs.FFXIV.Client.Game.Character.CastInfo), typeof(FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState), typeof(FFXIVClientStructs.FFXIV.Client.Game.ActionManager) })
    {
        Console.WriteLine("TYPE " + t.FullName);
        foreach (var p in t.GetProperties()) Console.WriteLine(p);
        foreach (var f in t.GetFields().Where(f => !f.Name.Contains("Virtual") && f.Name != "Addresses")) Console.WriteLine(f);
        foreach (var m in t.GetMethods().Where(m => m.Name is "GetCastInfo" or "UseAction" or "GetActionStatus" or "Instance")) Console.WriteLine(m);
    }
    return;
}

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
    if (args.Contains("--common-eureka"))
    {
        var names = new HashSet<string> { "罗德尼", "调查队的补给员" };
        var npcs = game.Excel.GetSheet<ENpcResident>().Where(n => (names.Contains(n.Singular.ExtractText()) || n.Singular.ExtractText().Contains("补给"))).ToDictionary(n => n.RowId);
        foreach (var row in game.Excel.GetSheet<Level>().Where(r => npcs.ContainsKey(r.Object.RowId) && r.Territory.RowId is 628 or 732 or 763 or 795 or 827))
            Console.WriteLine($"{npcs[row.Object.RowId].Singular.ExtractText()} npc={row.Object.RowId} zone={row.Territory.RowId} map={row.Map.RowId} pos={row.X},{row.Y},{row.Z}");
        foreach (var row in game.Excel.GetSheet<Aetheryte>().Where(r => r.Territory.RowId == 628))
            Console.WriteLine($"Aetheryte {row.RowId} {row.PlaceName.ValueNullable?.Name.ExtractText()} {row.AethernetName.ValueNullable?.Name.ExtractText()}");
        foreach (var row in game.Excel.GetSheet<Item>().Where(r => r.Name.ExtractText() == "优雷卡恢复药"))
            Console.WriteLine($"Item {row.RowId} Price={row.PriceMid}");
        return;
    }
    if (args.Contains("--elemental-data"))
    {
        var names = new HashSet<string> { "常风元灵", "恒冰元灵", "涌火元灵", "丰水元灵" };
        foreach (var npc in game.Excel.GetSheet<BNpcName>().Where(n => names.Contains(n.Singular.ExtractText())))
            Console.WriteLine($"{npc.RowId} {npc.Singular.ExtractText()}");
        return;
    }
    if (args.Contains("--crystal-data"))
    {
        foreach (var id in new uint[] { 2415,2429,2436,2463,2474,2472,2473,2531,2540,2541,2542,2876,2891,2892 })
            Console.WriteLine($"{id}: {game.Excel.GetSheet<PlaceName>()!.GetRow(id).Name.ExtractText()}");
        return;
    }
    if (args.Contains("--arrival-data"))
    {
        foreach (var action in game.Excel.GetSheet<GeneralAction>()!)
            if (action.RowId < 30) Console.WriteLine($"GeneralAction {action.RowId} {action.Name.ExtractText()} action={action.Action.RowId}");
        foreach (var id in new uint[] { 1332, 1351, 1412, 1422 })
        { var row = game.Excel.GetSheet<Fate>()!.GetRow(id); Console.WriteLine($"Fate {id} {row.Name.ExtractText()} level={row.ClassJobLevel} max={row.ClassJobLevelMax}"); }
        return;
    }
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







