using Dalamud.Game.ClientState.Conditions;
using System.Diagnostics;
using ULKHelper.Fates;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ULKHelper.Automation;

internal sealed partial class AutomationHelpers
{
    private RemoteCommandPipe? remotePipe;
    private enum RemotePhase { WorldTravel, Rodney, Choosing, Entering, Waiting, Fighting, Settling, Leaving }
    private sealed record RemoteRun(uint World, uint InitialIsland, ushort InitialFate, ulong Player, DateTimeOffset Deadline)
    {
        public uint Island = InitialIsland;
        public ushort Fate = InitialFate, ApproachFate;
        public bool ApproachPending;
        public int FateStart;
        public bool PartyRequest, AutoParty;
        public IDalamudPlugin? WorldTravelProvider;
        public string FateName = "", Result = "";
        public string? FateOutcome;
        public readonly Queue<(DateTimeOffset Time, string Text)> Rewards = [];
        public TravelMountHelper? Mount;
        public RemotePhase Phase;
        public WorldContext Origin, IslandContext;
        public DateTimeOffset NextScan, PhaseDeadline, SettlementAt;
    }

    private readonly IDalamudPluginInterface discordPlugin;
    private DiscordTarget pendingDiscordTarget;
    private DateTimeOffset pendingDiscordUntil;
    private int remoteResultId;
    private string remoteResult = "";
    private bool discordPartyRequest, discordAutoParty;

    public void SetDiscordParty(bool request, bool auto) => commands.Enqueue(() =>
    {
        discordPartyRequest = request;
        discordAutoParty = auto;
    });

    private void RecordDiscordReward(IChatMessage message)
    {
        if (remoteRun is not { Phase: RemotePhase.Settling or RemotePhase.Fighting } run
            || message.LogKind is not (XivChatType.Item or XivChatType.LootNotice or XivChatType.SystemMessage)) return;
        var content = SeString.Parse(message.OriginalMessage.Data.Span);
        var line = content.TextValue.Trim();
        if (line.Length > 140 || !content.Payloads.OfType<ItemPayload>().Any()
            || !line.StartsWith("获得了", StringComparison.Ordinal)) return;
        var receivedAt = DateTimeOffset.UtcNow;
        commands.Enqueue(() =>
        {
            if (!ReferenceEquals(remoteRun, run)) return;
            if (run.Rewards.Count >= 32) run.Rewards.Dequeue();
            run.Rewards.Enqueue((receivedAt, line.ReplaceLineEndings(" ")));
        });
    }

    private RemoteRun? remoteRun;
    private string remoteStatus = "暂无 Discord 任务。";

    private Process? discordBridge;
    private string discordBridgeStatus = "";
    private string discordBridgeDirectory = "";
    private readonly string discordCredentialsPath;
    private bool discordCredentialsSaved;
    private ulong discordBotId;
    private DateTimeOffset discordLastSeen;

    public DiscordSnapshot DiscordSnapshot => new(remotePipe != null, RemoteActive, discordBotId, discordLastSeen, CurrentRemoteStatus, discordBridgeStatus, discordPartyRequest, discordAutoParty, discordCredentialsSaved);

    public void ClearDiscordCredentials() => commands.Enqueue(() =>
    {
        try
        {
            File.Delete(discordCredentialsPath);
            discordCredentialsSaved = false;
            discordBridgeStatus = "已清除本机 Discord 凭据。";
        }
        catch (Exception) { discordBridgeStatus = "无法清除本机 Discord 凭据。"; }
    });

    public void StartDiscordBridge(string owner, string token) => commands.Enqueue(() =>
    {
        owner = owner.Trim();
        token = token.Trim();
        var useSaved = owner.Length == 0 && token.Length == 0;
        if (useSaved)
        {
            try { (owner, token) = DiscordCredentials.Read(discordCredentialsPath); }
            catch (Exception)
            {
                discordBridgeStatus = "无法读取本机凭据，请重新输入用户 ID 和 Bot Token。";
                return;
            }
        }
        if (!ulong.TryParse(owner, out var id) || id == 0 || token.Length == 0 || token.Contains('\n') || token.Contains('\r'))
        {
            discordBridgeStatus = "请输入你的 Discord 用户 ID 和 Bot Token。";
            return;
        }
        if (discordBridge is { HasExited: false }) return;
        StopDiscordBridge();

        try
        {
            var script = Path.Combine(discordBridgeDirectory, "bridge.py");
            if (!File.Exists(script))
            {
                discordBridgeStatus = "找不到桥接脚本，请重新安装完整插件包。";
                return;
            }

            var runtime = Path.Combine(discordBridgeDirectory, "discord-python.txt");
            var paths = File.Exists(runtime) ? File.ReadAllLines(runtime) : [];
            var start = new ProcessStartInfo(paths.FirstOrDefault() ?? "py")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = discordBridgeDirectory,
            };
            start.ArgumentList.Add(script);
            if (paths.Length > 1) start.Environment["PYTHONPATH"] = paths[1];
            start.Environment["ULK_DISCORD_OWNER_ID"] = owner;
            start.Environment["ULK_DISCORD_TOKEN"] = token;
            try { discordBridge = Process.Start(start); }
            finally { start.Environment.Remove("ULK_DISCORD_TOKEN"); }
            OpenDiscordPipe();
            discordBridgeStatus = "正在连接 Discord，等待 Bot 心跳。";
            if (!useSaved)
            {
                try
                {
                    DiscordCredentials.Save(discordCredentialsPath, owner, token);
                    discordCredentialsSaved = true;
                }
                catch (Exception) { discordBridgeStatus = "桥接已启动，但无法保存本机 Discord 凭据。"; }
            }
        }
        catch (Exception)
        {
            StopDiscordBridge();
            discordBridgeStatus = "无法启动桥接，请检查 Python 和 Discord 桥接依赖。";
        }
    });

    private void StopDiscordBridge()
    {
        if (discordBridge is not { } bridge) return;
        discordBridge = null;
        try { if (!bridge.HasExited) bridge.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { bridge.Dispose(); }
    }

    private void OpenDiscordPipe()
    {
        remotePipe ??= new(DispatchRemote, ex => log.Error(ex, "Discord 本地管道失败"));
    }

    public void StopDiscord() => commands.Enqueue(() => StopRemote("已停止 Discord 任务。"));

    public void EnableDiscord(bool enabled) => commands.Enqueue(() =>
    {
        if (!enabled)
        {
            StopDiscordBridge();
            pendingDiscordTarget = default;
            discordBridgeStatus = "";
            remotePipe?.Dispose();
            remotePipe = null;
            discordBotId = 0;
            discordLastSeen = default;
            StopRemote("已关闭 Discord 接入。");
            chat.Print("已关闭 Discord 接入。");
        }
        else if (remotePipe == null)
        {
            OpenDiscordPipe();
            chat.Print("已开启 Discord 接入，请从机器人私聊发送「状态」确认连接。");
        }
    });

    private Task<string> DispatchRemote(string input, CancellationToken token)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue(() =>
        {
            if (token.IsCancellationRequested) return;
            try { reply.SetResult(HandleRemote(input)); }
            catch (Exception ex)
            {
                StopRemote("Discord 任务发生异常，已停止。");
                log.Error(ex, "Discord 任务失败");
                reply.SetResult(remoteStatus);
            }
        });
        return reply.Task;
    }

    private string CurrentRemoteStatus => remoteRun?.Phase switch
    {
                        RemotePhase.WorldTravel => "等待 Lifestream 跨服。",
            RemotePhase.Entering or RemotePhase.Rodney => commonJourney.Status,
            RemotePhase.Fighting => follow.Status,
            RemotePhase.Waiting when remoteRun.ApproachPending && navigation.Active => navigation.Status,
            _ => remoteStatus,
        };

    private string HandleRemote(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["bot", var botId] && ulong.TryParse(botId, out var id) && id != 0)
        {
            discordBotId = id;
            discordLastSeen = DateTimeOffset.UtcNow;
            if (discordBridgeStatus == "正在连接 Discord，等待 Bot 心跳。") discordBridgeStatus = "";
            return remoteResult.Length == 0 ? "OK" : remoteResult;
        }
        if (parts is ["ack", var resultId] && int.TryParse(resultId, out var ack))
        {
            if (ack == remoteResultId) remoteResult = "";
            return "OK";
        }
        switch (DiscordCommandRules.Normalize(input))
        {
            case "状态": return CurrentRemoteStatus;
            case "取消":
            case "停":
            case "停止":
            case "stop":
            case "cancel":
                const string stopped = "已停止插件当前的所有自动化任务，并清除待补充的 Discord 指令。";
                StopTasks(stopped);
                remoteStatus = stopped;
                return remoteStatus;
        }

        var weatherCommand = DiscordCommandRules.Normalize(input);
        if (weatherCommand.StartsWith("天气", StringComparison.Ordinal))
        {
            var alias = weatherCommand[2..];
            var island = DiscordCommandRules.Island(alias);
            if (alias.Length > 0 && island == 0) return "请发送：天气、天气风、天气冰、天气火或天气水。";
            try
            {
                return ULKHelper.Weather.WeatherReply.Build(ULKHelper.Weather.WeatherCatalogLoader.Load(
                    data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()), DateTimeOffset.UtcNow, island);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Discord 天气查询失败");
                return "暂时无法读取天气数据，请稍后再试。";
            }
        }
        var worlds = data.GetExcelSheet<Lumina.Excel.Sheets.World>().Where(w => DiscordCommandRules.ChineseWorld(w.RowId)).ToArray();
        var current = objects.LocalPlayer?.CurrentWorld.Value;
        var home = objects.LocalPlayer?.HomeWorld.Value;
        var worldAliases = worlds.Select(w => (w.Name.ExtractText(), w.RowId)).ToList();
        var woxian = worlds.FirstOrDefault(w => w.Name.ExtractText() == "沃仙曦染");
        if (woxian.RowId != 0) worldAliases.Add(("沃仙", woxian.RowId));
        foreach (var group in worlds.GroupBy(w => w.DataCenter.RowId))
        {
            var first = group.OrderBy(w => w.RowId).First();
            var chosen = home is { } original && group.Any(w => w.RowId == original.RowId) ? original.RowId
                : current is { } active && group.Any(w => w.RowId == active.RowId) ? active.RowId : first.RowId;
            var name = first.DataCenter.Value.Name.ExtractText();
            worldAliases.Add((name, chosen));
            foreach (var alias in name switch
            {
                "陆行鸟" => new[] { "鸟", "lxn" },
                "莫古力" => ["莫", "猪", "mgl"],
                "猫小胖" => ["猫", "mxp"],
                "豆豆柴" => ["柴", "ddc"],
                _ => Array.Empty<string>(),
            }) worldAliases.Add((alias, chosen));
        }
        var fateSheet = data.GetExcelSheet<Lumina.Excel.Sheets.Fate>();
        var rules = new DiscordCommandRules(worldAliases, id => fateSheet.GetRow(id).Name.ExtractText());
        var now = DateTimeOffset.UtcNow;
        if (now >= pendingDiscordUntil) pendingDiscordTarget = default;
        var matches = rules.Parse(input, remoteRun?.Island ?? pendingDiscordTarget.Territory);
        var normalized = DiscordCommandRules.Normalize(input);
        var command = normalized.StartsWith("进岛", StringComparison.Ordinal) ? normalized[2..] : normalized;
        if (remoteRun == null && command.StartsWith("鸟", StringComparison.Ordinal) && matches.Any(t => t.World != 0))
            matches = matches.Where(t => t.World != 0).ToArray();
        else if (remoteRun != null && command == "鸟" && matches.Any(t => t.Fate != 0))
            matches = matches.Where(t => t.Fate != 0).ToArray();
        if (matches.Length != 1)
            return matches.Length == 0 ? "未识别指令，或岛屿与 FATE 不一致。示例：沃仙冰kx。" : "指令有多个匹配，请补充岛名或使用全称。";

        var target = matches[0];
        if (remoteRun is { } running)
        {
            if (running.Phase is RemotePhase.Fighting or RemotePhase.Settling or RemotePhase.Leaving)
                return "当前任务已锁定目标，请先停止后再发新指令。";
            if (target.World != 0 && target.World != running.World) return "当前任务已锁定服务器。";
            running.Island = target.Territory == 0 ? running.Island : target.Territory;
            if (target.Fate != 0) running.Fate = target.Fate;
            return running.Fate != 0 ? "已记录目标 FATE，会直接前往目标点。" : "已记录岛屿。要打哪个 FATE？";
        }

        target = new(target.World, target.Territory == 0 ? pendingDiscordTarget.Territory : target.Territory,
            target.Fate == 0 ? pendingDiscordTarget.Fate : target.Fate);
        if (target.World == 0)
        {
            pendingDiscordTarget = target;
            pendingDiscordUntil = now.AddMinutes(15);
            return "哪个服务器？";
        }
        if (!CanMove()) return "请先登录并退出战斗或交互。";
        if (BunnyAutomationActive || SurveyActive || commonJourney.Active || trigger.Active || follow.Active
            || navigation.Active || combat.CleanupPending || conditions[ConditionFlag.InDutyQueue])
            return "已有游戏任务，请先结束当前任务。";
        if (!EnsureTravelMount()) return "无法读取已解锁的可移动坐骑，请在角色就绪后重试。";

        var world = Context();
        if (world.PlayerId == 0 || current == null) return "当前角色尚未就绪。";
        if (IsEureka(world.Territory) && (current.Value.RowId != target.World || world.Territory != target.Territory))
            return "请先退出当前优雷卡岛。";

        var run = new RemoteRun(target.World, target.Territory, target.Fate, world.PlayerId, now.AddMinutes(55))
        {
            Origin = world,
            PartyRequest = discordPartyRequest,
            AutoParty = discordAutoParty,
        };
        remoteRun = run;
        pendingDiscordTarget = default;
        if (run.World != current.Value.RowId)
        {
            var provider = discordPlugin.GetIpcSubscriber<IDalamudPlugin>("Lifestream.Instance").InvokeFunc();
            if (provider == null || discordPlugin.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()
                || !discordPlugin.GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById").InvokeFunc(run.World))
            {
                StopRemote("Lifestream 未接受跨服请求。");
                return remoteStatus;
            }
            run.WorldTravelProvider = provider;
            run.Phase = RemotePhase.WorldTravel;
            run.NextScan = now.AddSeconds(2);
            run.PhaseDeadline = now.AddMinutes(10);
        }
        else StartDiscordJourney(run, world, now);

        if (!RemoteActive) return remoteStatus;
        return run.Fate != 0 ? "已接单，进岛后直接前往指定 FATE。"
            : run.Island != 0 ? "已接单。要打哪个 FATE？不回复时只等待该岛的特殊 FATE。"
            : "已接单，先前往罗德尼。要打哪个 FATE，或要去哪个岛？";
    }

    private void StartDiscordJourney(RemoteRun run, WorldContext world, DateTimeOffset now)
    {
        if (run.Island != 0 && world.Territory == run.Island)
        {
            run.IslandContext = world;
            run.Phase = RemotePhase.Waiting;
            run.Mount = new TravelMountHelper(mounts);
            run.PhaseDeadline = now.AddMinutes(15);
            remoteStatus = "已进岛，监听目标 FATE（最长 15 分钟）。";
            return;
        }
        run.Phase = run.Island == 0 ? RemotePhase.Rodney : RemotePhase.Entering;
        commonJourney.Start(run.Island == 0 ? CommonAction.Entrance : DiscordCommandRules.Entry(run.Island), world, now);
        if (!commonJourney.Active) StopRemote(commonJourney.Status);
    }

    private void TickRemote(WorldContext world, bool loading, DateTimeOffset now)
    {
        if (discordBridge is { HasExited: true })
        {
            StopDiscordBridge();
            discordLastSeen = default;
            discordBridgeStatus = "桥接已退出，请检查 Bot Token、网络和桥接依赖。";
        }
        if (remoteRun is not { } run) return;
        if (now >= run.Deadline || objects.LocalPlayer?.IsDead == true
            || world.PlayerId != 0 && world.PlayerId != run.Player)
        {
            StopRemote("任务超时或角色变化，Discord 任务已停止。");
            return;
        }

        if (run.Phase == RemotePhase.WorldTravel)
        {
            if (now >= run.PhaseDeadline) { StopRemote("跨服超时。"); return; }
            if (now < run.NextScan) return;
            run.NextScan = now.AddSeconds(1);
            var provider = discordPlugin.GetIpcSubscriber<IDalamudPlugin>("Lifestream.Instance").InvokeFunc();
            if (!ReferenceEquals(provider, run.WorldTravelProvider))
            { StopRemote("Lifestream 提供者变化，已停止跨服任务。"); return; }
            if (discordPlugin.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc()) return;
            if (loading || !client.IsLoggedIn) return;
            if (!CanMove() || objects.LocalPlayer?.CurrentWorld.RowId != run.World)
            { StopRemote("未确认抵达目标服务器。"); return; }
            run.WorldTravelProvider = null;
            StartDiscordJourney(run, world, now);
            return;
        }

        if (!client.IsLoggedIn) { StopRemote("角色已登出。"); return; }
        if (run.Phase is RemotePhase.Rodney or RemotePhase.Entering)
        {
            if (commonJourney.Active || loading) return;
            if (run.Phase == RemotePhase.Entering)
            {
                if (world.Territory != run.Island) { StopRemote(commonJourney.Status); return; }
                StartDiscordJourney(run, world, now);
            }
            else
            {
                if (!commonJourney.ArrivedAtEntrance) { StopRemote(commonJourney.Status); return; }
                run.Origin = world;
                run.Phase = RemotePhase.Choosing;
                run.PhaseDeadline = now.AddMinutes(15);
                remoteStatus = "已到罗德尼，等待岛名或 FATE。";
            }
            return;
        }

        if (run.Phase == RemotePhase.Choosing)
        {
            if (world != run.Origin || now >= run.PhaseDeadline) { StopRemote("等待岛屿已结束。"); return; }
            if (run.Island != 0 && CanMove()) StartDiscordJourney(run, world, now);
            return;
        }

        if (run.Phase == RemotePhase.Leaving)
        {
            if (!loading && !IsEureka(world.Territory)) { StopRemote(run.Result + " 已离岛。"); return; }
            if (now >= run.PhaseDeadline) StopRemote(run.Result + " 未确认离岛，请手动退出。");
            return;
        }
        if (world != run.IslandContext)
        {
            StopRemote("已离开任务所在岛，Discord 任务已停止。");
            return;
        }
        if (run.Phase == RemotePhase.Settling)
        {
            if (now >= run.PhaseDeadline && CanMove()) RequestDiscordExit(run, now);
            return;
        }
        if (run.Phase == RemotePhase.Fighting)
        {
            ObserveDiscordFate(run, now);
            if (!follow.Active)
            {
                run.Phase = RemotePhase.Settling;
                if (run.SettlementAt == default) run.SettlementAt = now;
                run.PhaseDeadline = now.AddSeconds(8);
                remoteStatus = "等待 FATE 结算消息。";
            }
            return;
        }
        if (run.ApproachPending && (navigation.Transferring || navigation.Interacting)) return;
        if (now >= run.PhaseDeadline)
        {
            if (run.ApproachPending) navigation.Stop();
            run.ApproachPending = false;
            run.Result = "监听 15 分钟未接到目标 FATE。";
            run.Phase = RemotePhase.Settling;
            run.PhaseDeadline = now;
            return;
        }
        if (loading || now < run.NextScan || !CanMove() || objects.LocalPlayer is not { } local) return;
        if (!EnsureTravelMount()) return;
        if (!run.ApproachPending)
        {
            var mountResult = run.Mount!.Tick(conditions[ConditionFlag.Mounted], local.IsCasting, false, now);
            if (mountResult == TravelMountResult.Failed) { StopRemote("寻找 FATE 时无法召唤坐骑，已停止任务。"); return; }
            if (mountResult != TravelMountResult.Mounted) { remoteStatus = "寻找 FATE 前等待坐骑召唤完成。"; return; }
        }
        run.NextScan = now.AddSeconds(1);
        var fate = ReadFates(includePreparing: true).Where(f => NavigationRules.Finite(f.Position)
                && (run.Fate != 0 ? f.Id == run.Fate : DiscordCommandRules.Special(f.Id)))
            .OrderBy(f => NavigationRules.Distance(local.Position, f.Position)).ThenBy(f => f.Id).FirstOrDefault();
        if (fate == null)
        {
            ApproachDiscordFate(run, world, local.Position, now);
            return;
        }

        if (run.ApproachPending) navigation.Stop();
        run.ApproachPending = false;
        arrivalGame.Reset();
        if (!follow.Start(world, now, repeatFollowing: false)) { StopRemote(follow.Status); return; }

        run.Fate = fate.Id;
        run.FateName = fate.Name;
        run.FateStart = fate.StartedAt;
        usingFollow = true;
        run.Phase = RemotePhase.Fighting;
        if (run.PartyRequest && commonParty.Count() <= 1) SendGameCommand("/shout 111");
        follow.Receive(new(world.Territory, world.Map, fate.Position, fate.Name), fate);
    }

    private void ApproachDiscordFate(RemoteRun run, WorldContext world, System.Numerics.Vector3 player, DateTimeOffset now)
    {
        if (run.Fate == 0) return;
        if (run.ApproachFate == run.Fate)
        {
            if (!run.ApproachPending || navigation.Active) return;
            run.ApproachPending = false;
            remoteStatus = navigation.TakeArrival() != null
                ? "已到目标点，等待指定 FATE 出现。"
                : navigation.Status + " 继续监听指定 FATE。";
            return;
        }

        if (run.ApproachPending) navigation.Stop();
        run.ApproachPending = false;
        run.ApproachFate = run.Fate;
        var island = ElementalMaps?.Islands.FirstOrDefault(map => map.Territory == world.Territory && map.MapId == world.Map);
        var seed = island?.Fates.FirstOrDefault(f => f.Seed.Id == run.Fate)?.Seed;
        if (island == null || seed == null)
        {
            remoteStatus = "目标没有已核对的地图点位，继续监听指定 FATE。";
            return;
        }

        var centre = MapProjection.MapToWorld(new(seed.X, seed.Y), island);
        pointNm = false;
        usingFollow = false;
        navigation.Start(new(world.Territory, world.Map, centre, seed.ShortName), world, player, [], now,
            summonMount: EnsureTravelMount(), waitWithoutFate: false, arrivalRadius: 15, preferCrystals: true);
        run.ApproachPending = true;
        remoteStatus = $"正在前往{seed.ShortName}的地图点位。";
    }

    private void ObserveDiscordFate(RemoteRun run, DateTimeOffset now)
    {
        foreach (var fate in fates)
        {
            if (fate == null || fate.FateId != run.Fate || fate.TerritoryType.RowId != run.Island) continue;
            if (run.FateStart == 0 && fate.State == Dalamud.Game.ClientState.Fates.FateState.Running)
                run.FateStart = fate.StartTimeEpoch;
            if (fate.StartTimeEpoch != run.FateStart) continue;
            if (fate.State == Dalamud.Game.ClientState.Fates.FateState.Failed) run.FateOutcome = "FATE 失败";
            else if (fate.Progress == 100 && fate.State is Dalamud.Game.ClientState.Fates.FateState.Ending
                or Dalamud.Game.ClientState.Fates.FateState.Ended) run.FateOutcome = "FATE 已完成";
            if (run.FateOutcome != null && run.SettlementAt == default) run.SettlementAt = now;
        }
    }

    private void RequestDiscordExit(RemoteRun run, DateTimeOffset now)
    {
        ObserveDiscordFate(run, now);
        var rewards = run.Rewards.Where(r => r.Time >= run.SettlementAt.AddSeconds(-8)
            && r.Time <= run.SettlementAt.AddSeconds(12)).Select(r => r.Text).Take(10).ToArray();
        if (run.FateName != "") run.Result = $"{run.FateName}：{run.FateOutcome ?? "未读到明确的 FATE 结算"}。{follow.Status}"
            + (rewards.Length == 0 ? " 未捕获可确认的奖励消息。" : " 结算窗口内的个人获取消息：" + string.Join(" / ", rewards));
        if (!TryLeaveDiscordIsland(run.IslandContext))
        {
            StopRemote(run.Result + " 当前无法提交离岛，请手动退出。");
            return;
        }
        run.Phase = RemotePhase.Leaving;
        run.PhaseDeadline = now.AddSeconds(30);
        remoteStatus = "已提交离岛，等待区域变化。";
    }

    private unsafe bool TryLeaveDiscordIsland(WorldContext island)
    {
        if (Context() != island || !IsEureka(island.Territory) || !CanMove()
            || !EventFramework.CanLeaveCurrentContent()) return false;
        var module = AgentModule.Instance();
        if (module == null) return false;
        var finder = module->GetAgentByInternalId(AgentId.ContentsFinderMenu);
        if (finder == null) return false;
        // Local CN API 15: ContentsFinderMenu leave event uses one integer argument, zero.
        AtkValue argument = default;
        argument.SetInt(0);
        AtkValue result = default;
        finder->ReceiveEvent(&result, &argument, 1, 0);
        return true;
    }

    private bool RemotePartyActive =>
        remoteRun is { AutoParty: true, Phase: RemotePhase.Waiting or RemotePhase.Fighting };

    private bool RemoteActive =>
        remoteRun != null;

    private void StopRemote(string reason)
    {
        pendingDiscordTarget = default;
        if (remoteRun is not { } run) return;
        remoteRun = null;
        if (run.WorldTravelProvider is { } provider)
        {
            try
            {
                if (ReferenceEquals(provider, discordPlugin.GetIpcSubscriber<IDalamudPlugin>("Lifestream.Instance").InvokeFunc())
                    && discordPlugin.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
                    discordPlugin.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction();
            }
            catch (Exception ex) { log.Warning(ex, "无法确认 Lifestream 跨服任务已停止"); }
        }
        if (run.Phase is RemotePhase.Entering or RemotePhase.Rodney && commonJourney.Active) commonJourney.Stop(reason);
        if (run.Phase == RemotePhase.Fighting && follow.Active) follow.Stop(reason);
        if (run.ApproachPending && navigation.Active) navigation.Stop(reason);
        remoteStatus = reason;
        remoteResult = $"{++remoteResultId} {reason.ReplaceLineEndings(" ")}";
    }
}
