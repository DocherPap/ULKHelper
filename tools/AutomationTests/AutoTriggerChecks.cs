using System.Numerics;
using ULKHelper.Automation;

internal static class AutoTriggerChecks
{
    public static void Run(Action<bool,string> check)
    {
        check(AutoTriggerRules.AttackMagia(20,19,false) && AutoTriggerRules.AttackMagia(20,19,true),"魔素：低级怪所有职业5攻");
        check(AutoTriggerRules.AttackMagia(20,22,true) && !AutoTriggerRules.AttackMagia(20,20,false),"魔素：同级至高2级坦克攻、其他防");
        check(!AutoTriggerRules.AttackMagia(20,23,true) && !AutoTriggerRules.AttackMagia(20,23,false),"魔素：高3级全部5防");
        var roughMesh=new Mesh{GroundTest=p=>p.X>1?p:null};
        check(AutoTriggerRules.Ground(roughMesh,Vector3.Zero,false)!=null,"触发地面：原落点不可走时寻找5米内替代点");
        check(AutoTriggerRules.Ground(new Mesh{GroundTest=p=>null},Vector3.Zero,false)==null,"触发地面：区域完全无网格时拒绝移动");
        check(AutoTriggerRules.Capacity(20,[19,18,1]) == 3, "触发：低等级允许3个");
        check(AutoTriggerRules.Capacity(20,[20]) == 2 && AutoTriggerRules.Capacity(20,[22]) == 2, "触发：同级至高2级允许2个");
        check(AutoTriggerRules.Capacity(20,[19,23]) == 1, "触发：混选取最严格限制");
        check(new ushort[]{1365,1402,1403,1404,1423}.All(id => AutoTriggerRules.DefaultMode(id) == TriggerMode.Recorded), "触发：五种特殊NM默认记录路线");
        var points = new[] {new Vector3(80,0,0), new Vector3(20,0,0), new Vector3(40,0,0)};
        var order = AutoTriggerRules.Order(Vector3.Zero,points);
        check(order.SequenceEqual(points.OrderBy(p => p.X)), "触发：近邻和2-opt优化已知点位顺序");
        check(AutoTriggerRules.Order(Vector3.Zero,[Vector3.One,Vector3.One,new(float.NaN,0,0)]).Count==1,"触发：点位去重并拒绝非法坐标");
        var time=DateTimeOffset.UtcNow;
        var world=new WorldContext(732,414,0,42);
        var frame=new TriggerFrame(world,Vector3.Zero,true,false,false,false,20,[],new HashSet<ushort>());
        var plan=new TriggerPlan(1332,1,new(732,414,Vector3.Zero,"测试NM"),TriggerMode.Normal,[]);
        Fixture New() => new();
        using(var f=New())
        {
            check(!f.Helper.Start([plan],frame with { Level=null },1,1,time),"触发：未知元素等级拒绝开始");
            check(!f.Helper.Start([plan],frame with { InCombat=true },1,1,time),"触发：战斗中拒绝新任务");
            check(!f.Helper.Start([plan,plan],frame,1,1,time),"触发：拒绝重复计划");
            check(!f.Helper.Start([plan with{Mode=TriggerMode.Recorded}],frame,1,1,time),"触发：无记录路线不执行特殊拉怪");
            check(!f.Helper.Start([plan,plan with{FateId=1348,Level=23}],frame,1,1,time),"触发：执行层再次校验混选限制");
            check(f.Combat.Prepares==0,"触发：校验失败不操作战斗插件");
            check(f.Helper.Start([plan],frame,1,1,time),"触发：有效计划显式启动");
            f.Helper.Tick(frame with{World=world with{Territory=763}},time.AddSeconds(1));
            check(!f.Helper.Active,"触发：切区立即停止");
        }
        using(var f=New())
        {
            f.Helper.Start([plan],frame,1,1,time);
            f.Helper.Tick(frame with{Safe=false},time.AddSeconds(1));
            check(!f.Helper.Active && f.Mesh.Moves==0,"触发：死亡或占用不移动");
        }
        using(var f=New())
        {
            f.Helper.Start([plan],frame,21,7,time);
            f.Helper.Tick(frame,time.AddHours(2).AddSeconds(1));
            check(!f.Helper.Active,"触发：无限数量仍受2小时会话限制");
        }
        using(var f=New())
        {
            f.Helper.Start([plan],frame,1,1,time);
            f.Helper.Tick(frame with{ActiveFates=new HashSet<ushort>{1332}},time.AddSeconds(1));
            check(!f.Helper.Active && f.Combat.Enables==0 && f.Mesh.Moves==0,"触发：FATE出现停止刷怪，不攻击NM");
        }
        check(AutoTriggerRules.WaveSizes(25, 10).SequenceEqual([8, 8, 9])
            && AutoTriggerRules.WaveSizes(20, 10).SequenceEqual([10, 10])
            && AutoTriggerRules.WaveSizes(25, int.MaxValue).SequenceEqual([25])
            && !AutoTriggerRules.WaveSizes(0, 10).Any(), "触发：向上取整算波次，平均分配并将余数放在后面");

        TriggerFrame Step(Fixture f, TriggerFrame state, ref int tick)
        {
            f.Helper.Tick(state, time.AddMilliseconds(++tick * 250));
            return f.Mesh.Current.Count > 0 ? state with { Position = f.Mesh.Last } : state;
        }

        TriggerFrame StartPull(Fixture f, TriggerFrame state, ref int tick)
        {
            for (var i = 0; i < 80 && f.Helper.Active && f.Combat.TargetId == 0; i++)
                state = Step(f, state, ref tick);
            return state;
        }

        TriggerFrame RunWave(Fixture f, TriggerFrame state, int expected, ref int tick)
        {
            var pulled = new HashSet<ulong>();
            var mobs = state.Mobs.ToArray();
            for (var i = 0; i < expected; i++)
            {
                for (var wait = 0; wait < 12 && (!f.Combat.Enabled || f.Combat.Fighting || pulled.Contains(f.Combat.TargetId)); wait++)
                    state = Step(f, state, ref tick);
                var index = Array.FindIndex(mobs, m => m.Id == f.Combat.TargetId && !m.Dead && !m.Aggro);
                if (!f.Combat.Enabled || f.Combat.Fighting || index < 0)
                    throw new Exception($"未按计划拉到第{i + 1}只怪：{f.Helper.Status}");
                pulled.Add(mobs[index].Id);
                mobs[index] = mobs[index] with { Aggro = true, Engaged = true };
                state = Step(f, state with { Mobs = mobs, InCombat = true }, ref tick);
            }
            state = Step(f, state, ref tick);
            check(f.Combat.Fighting && f.Combat.Approach && f.Mesh.Current.Count == 0, $"触发：拉够{expected}只后切换Auto，保留BMR靠近并释放vnavmesh");
            var highest = mobs.Where(m => pulled.Contains(m.Id)).MaxBy(m => m.Hp)!;
            check(f.Combat.TargetId == highest.Id, "触发：切入击杀时选本波已有仇恨的最高血量目标");
            return Step(f, state with
            {
                InCombat = false,
                Mobs = mobs.Select(m => pulled.Contains(m.Id) ? m with { Dead = true } : m).ToArray(),
            }, ref tick);
        }

        using (var f = New())
        {
            var tick = 0;
            var mob = new TriggerMob(321, 1332, new(5, 0, 0), false, false);
            var away = frame with { Position = new(-30, 0, 0), Mobs = [mob], CanMountForTravel = true };
            f.Helper.Start([plan], away, 1, 1, time);
            Step(f, away, ref tick);
            check(f.Mount.Mounts == 1 && f.Mesh.Moves == 0, "普通触发：脱战行程先召唤坐骑");
            var mounted = away with { Mounted = true };
            for (var i = 0; i < 4; i++) mounted = Step(f, mounted, ref tick);
            check(f.Mesh.Moves == 1 && f.Mount.Dismounts == 1 && !f.Combat.Enabled,
                "普通触发：骑乘到点后先下坐骑，再准备拉怪");
            Step(f, mounted with { Mounted = false }, ref tick);
            check(f.Combat.Enabled && f.Combat.TargetId == mob.Id, "普通触发：确认下坐骑后开始拉怪");
        }
        using (var f = New())
        {
            var tick = 0;
            var away = frame with { Position = new(-30, 0, 0) };
            f.Helper.Start([plan], away, 1, 1, time);
            for (var i = 0; i < 3; i++) Step(f, away, ref tick);
            check(f.Mesh.Current.Count > 0, "普通触发：已发出前往中心的路线");
            Step(f, frame with { Position = new(4, 12, 0) }, ref tick);
            check(f.Helper.Active && f.Mesh.Current.Count == 0, "普通触发：水平5米内忽略高度差并结束本段");
            f.Helper.Tick(frame, time.AddSeconds(20));
            check(f.Helper.Active && f.Mesh.Moves == 1 && f.Helper.Status.Contains("等待刷新"), "普通触发：无怪时原地等待，不重跑路线");
        }
        using (var f = New())
        {
            var tick = 0;
            var mob = new TriggerMob(321, 1332, Vector3.Zero, false, false);
            var away = frame with { Position = new(-30, 0, 0), Mobs = [mob] };
            f.Helper.Start([plan], away, 1, 1, time);
            for (var i = 0; i < 3; i++) Step(f, away, ref tick);
            f.Mesh.Current.Clear();
            Step(f, away, ref tick);
            check(f.Helper.Active && f.Combat.TargetId == mob.Id && f.Mesh.Moves == 1, "普通触发：路线提前结束后直接拉怪");
        }
        using (var f = New())
        {
            var tick = 0;
            var first = new TriggerMob(501, 1332, new(5, 0, 0), false, false, Hp: 100);
            var other = new TriggerMob(502, 1332, new(8, 0, 0), false, false, Hp: 100);
            var loaded = frame with { Mobs = [first, other] };
            f.Helper.Start([plan], loaded, 10, 1, time);
            loaded = StartPull(f, loaded, ref tick);
            var far = loaded with { Position = new(40, 0, 0), InCombat = true, Mobs = [first with { Aggro = true }, other] };
            Step(f, far, ref tick);
            Step(f, far, ref tick);
            check(f.Combat.Fighting && f.Combat.TargetId == first.Id, "普通触发：本波没有附近目标时提前进入击杀");
            var clear = far with { InCombat = false, Mobs = [first with { Dead = true }, other] };
            for (var i = 0; i < 4; i++) Step(f, clear, ref tick);
            check(f.Mesh.Current.Count > 0 && f.Mesh.Last == other.Position, "普通触发：脱战后直达较远的匹配怪");
            Step(f, clear with { Position = new(35, 0, 0) }, ref tick);
            check(f.Mesh.Current.Count == 0 && f.Combat.TargetId == other.Id && !f.Combat.Fighting,
                "普通触发：接近途中发现可拉目标即停并开始下一波");
        }
        using (var f = New())
        {
            var near = plan with { Centre = plan.Centre with { Position = new(15,0,0), Label = "最近" } };
            var far = plan with { FateId = 1348, Centre = plan.Centre with { Position = new(90,0,0), Label = "较远" } };
            var tick = 0;
            f.Helper.Start([far, near], frame, 10, 1, time);
            Step(f, frame, ref tick);
            check(f.Helper.Status.StartsWith("最近"), "触发：优先处理最近FATE，忽略加入顺序");
            Step(f, frame with { ActiveFates = new HashSet<ushort> { near.FateId } }, ref tick);
            Step(f, frame, ref tick);
            check(f.Helper.Active && f.Helper.Status.StartsWith("较远"), "触发：最近目标出现后才处理下一项");
        }
        using (var f = New())
        {
            var tick = 0;
            var mobs = Enumerable.Range(1, 25).Select(i =>
                new TriggerMob((ulong)i, 1332, new(i / 2, 0, 1), false, false, Hp: (uint)i * 100)).ToArray();
            var loaded = frame with { Position = new(-30, 0, 0), Mobs = mobs };
            f.Helper.Start([plan], loaded, 10, 1, time);
            loaded = Step(f, loaded, ref tick);
            loaded = Step(f, loaded, ref tick);
            check(f.Mesh.Last == Vector3.Zero && f.Combat.TargetId == 0, "普通触发：先前往FATE中心，前往中心途中不开循环");
            loaded = StartPull(f, loaded, ref tick);
            check(f.Mesh.Moves == 1 && f.Combat.Enabled && f.Combat.Approach && !f.Combat.Fighting,
                "普通触发：到达中心后直接开启BMR和Manual");
            foreach (var size in new[] { 8, 8, 9 }) loaded = RunWave(f, loaded, size, ref tick);
            check(loaded.Mobs.All(m => m.Dead) && !f.Combat.Enabled && f.Mesh.Moves == 1,
                "普通触发：同位置多个对象也分别入波，三波之间不追加路线");
            Step(f, loaded with { ActiveFates = new HashSet<ushort> { 1332 } }, ref tick);
            check(!f.Helper.Active && f.Combat.Ends == 1 && f.Helper.Status.Contains("待机"),
                "触发：发现目标FATE后结束且恢复插件设置");
        }
        using (var f = New())
        {
            var tick = 0;
            var loaded = frame with { Mobs = Enumerable.Range(1, 50)
                .Select(i => new TriggerMob((ulong)i, 1332, new(3,0,0), false, false, Hp: (uint)i)).ToArray() };
            f.Helper.Start([plan], loaded, 20, 1, time);
            loaded = StartPull(f, loaded, ref tick);
            foreach (var size in new[] { 16, 17, 17 }) loaded = RunWave(f, loaded, size, ref tick);
            check(!f.Helper.Active && !f.Combat.Enabled && f.Combat.Ends == 1 && f.Helper.Status.Contains("达到放弃数量"),
                "触发：累计50个仇恨目标活到死，达到放弃数量后停止");
        }
        using (var f = New())
        {
            var tick = 0;
            var first = new TriggerMob(321, 1332, new(20,0,0), false, false);
            var later = new TriggerMob(322, 1332, new(25,0,0), false, false, Hp: 100);
            var ignored = new TriggerMob(323, 1332, new(26,0,0), false, false, Engaged: true);
            var loaded = frame;
            f.Helper.Start([plan], loaded, 10, 1, time);
            for (var i = 0; i < 7; i++) loaded = Step(f, loaded, ref tick);
            loaded = StartPull(f, loaded with { Mobs = [first, later, ignored] }, ref tick);
            loaded = RunWave(f, loaded, 2, ref tick);
            check(loaded.Mobs.Count(m => m.Dead) == 2 && !loaded.Mobs.Single(m => m.Id == ignored.Id).Dead,
                "普通触发：等待中新出现的怪进入缓存，其他人已交战的怪不入波");
        }
        using (var f = New())
        {
            var tick = 0;
            var first = new TriggerMob(401, 1332, new(5, 0, 0), false, false, Hp: 100);
            var blocked = new TriggerMob(402, 1332, new(10, 0, 0), false, false, Hp: 100);
            var next = new TriggerMob(403, 1332, new(15, 0, 0), false, false, Hp: 100);
            var loaded = frame with { Mobs = [first, blocked, next] };
            f.Helper.Start([plan], loaded, 10, 1, time);
            loaded = StartPull(f, loaded, ref tick);
            var pulling = loaded with { InCombat = true, Mobs = [first with { Aggro = true }, blocked, next] };
            Step(f, pulling, ref tick);
            check(f.Combat.TargetId == blocked.Id, "触发：建立仇恨后选中下一个近目标");
            f.Helper.Tick(pulling, time.AddSeconds(8));
            check(f.Helper.Active && f.Helper.Status.Contains("跳过拉怪无进展"), "触发：目标未接近且血量未下降时跳过");
            f.Helper.Tick(pulling, time.AddSeconds(9));
            check(f.Combat.TargetId == next.Id && !f.Combat.Fighting, "触发：跳过卡住目标后继续本波拉怪");
        }
        using (var f = New())
        {
            var tick = 0;
            var mob = new TriggerMob(321, 1332, new(20,0,0), false, false);
            var loaded = frame with { Mobs = [mob] };
            f.Helper.Start([plan], loaded, 1, 1, time);
            loaded = StartPull(f, loaded, ref tick);
            loaded = Step(f, loaded with { Mobs = [] }, ref tick);
            check(f.Helper.Status.Contains("击杀 0/50"), "触发：对象卸载不计击杀");
            f.Helper.Stop();
            check(!f.Helper.Active && !f.Combat.Enabled && f.Combat.Ends == 1, "触发：手动停止清理自有控制");
        }
        using(var f=New())
        {
            f.Helper.Start([plan],frame,1,1,time);
            f.Helper.Tick(frame with{Mobs=[new(5,1332,Vector3.Zero,true,true)]},time.AddMilliseconds(250));
            check(f.Helper.Status.Contains("击杀 0/50"),"触发：首次看见尸体不计入放弃数量");
        }
        using(var f=New())
        {
            var special=plan with{Mode=TriggerMode.Recorded,Route=[Vector3.Zero,new(20,0,0),new(50,0,0)]};
            f.Helper.Start([special],frame,1,1,time);
            f.Helper.Tick(frame,time.AddMilliseconds(250));
            check(f.Mount.Mounts==1 && f.Mesh.Moves==0,"触发：特殊路线先确认骑乘");
            var mounted=frame with{Mounted=true};
            f.Helper.Tick(mounted,time.AddMilliseconds(500));
            f.Helper.Tick(mounted,time.AddMilliseconds(750));
            check(f.Combat.Enables==0,"触发：骑乘路过不执行攻击");
            var aggro=mounted with{InCombat=true,Mobs=[new(123,1332,new(2,0,0),false,true)]};
            f.Helper.Tick(aggro,time.AddSeconds(1));
            f.Helper.Tick(aggro,time.AddMilliseconds(1250));
            check(f.Mesh.Last==new Vector3(50,0,0) && f.Mount.Dismounts==0,"触发：特殊达到拉怪上限仍先前往记录终点");
            var arrived=aggro with{Position=new(50,0,0)};
            f.Helper.Tick(arrived,time.AddMilliseconds(1500));
            f.Helper.Tick(arrived,time.AddMilliseconds(1750));
            check(f.Mount.Dismounts==1,"触发：抵达记录终点后下坐骑");
            f.Helper.Tick(arrived with{Mounted=false},time.AddSeconds(2));
            check(f.Combat.Enables==1,"触发：确认下坐骑才开始特殊击杀");
            var dead=arrived with{Mounted=false,InCombat=false,Mobs=[new(123,1332,new(50,0,0),true,true)]};
            f.Helper.Tick(dead,time.AddMilliseconds(2250));
            f.Helper.Tick(dead,time.AddMilliseconds(2500));
            check(f.Mount.Mounts==2 && !f.Combat.Enabled,"触发：特殊击杀结束关闭循环并重新上坐骑");
            var returning=dead with{Mounted=true,Mobs=[]};
            f.Helper.Tick(returning,time.AddMilliseconds(2750));
            f.Helper.Tick(returning,time.AddSeconds(3));
            check(f.Mesh.Last==Vector3.Zero,"触发：特殊击杀后返回记录起点");
            var home=returning with{Position=Vector3.Zero};
            f.Helper.Tick(home,time.AddMilliseconds(3250));
            f.Helper.Tick(home,time.AddMilliseconds(3500));
            check(f.Helper.Status.Contains("等待刷新") && f.Mesh.Current.Count==0,"触发：回到起点后无怪则待机");
            f.Helper.Tick(home with{Mobs=[new(234,1332,new(10,0,0),false,false)]},time.AddMilliseconds(3750));
            f.Helper.Tick(home with{Mobs=[new(234,1332,new(10,0,0),false,false)]},time.AddSeconds(4));
            check(f.Helper.Active && f.Combat.Enables==1,"触发：重新出现触发怪后开始下一次骑乘路线，不攻击拉怪");
            f.Helper.Stop();
            f.Helper.Tick(arrived,time.AddSeconds(5));
            check(!f.Combat.Enabled,"触发：停止后不恢复特殊任务");
        }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly Mesh Mesh=new(); public readonly Mount Mount=new(); public readonly Combat Combat=new();
        public readonly AutoTriggerHelper Helper;
        public Fixture()=>Helper=new(Mesh,Mount,Combat);
        public void Dispose()=>Helper.Stop();
    }
    private sealed class Mesh : INavigationMesh
    {
        public bool Ready=>true; public bool Busy=>false; public List<Vector3> Current=[];
        public IReadOnlyList<Vector3> Route=>Current; public int Moves; public Vector3 Last;
        public Func<Vector3,Vector3?> GroundTest=p=>p;
        public Vector3? Ground(Vector3 position,bool unknownHeight)=>GroundTest(position);
        public Task<List<Vector3>> FindPath(Vector3 from,Vector3 to,CancellationToken cancellation) {Last=to;return Task.FromResult(new List<Vector3>{to});}
        public void Move(List<Vector3> route){Moves++;Current=route;}
        public void Stop()=>Current.Clear();
    }
    private sealed class Mount : IArrivalActions
    {
        public int Mounts,Dismounts;
        bool IArrivalActions.Mount(){Mounts++;return true;}
        public bool Dismount(){Dismounts++;return true;}
    }
    private sealed class Combat : ITriggerCombat
    {
        public int Prepares,Enables,Begins,Ends; public ulong TargetId; public bool Enabled, Approach, Fighting;
        public List<bool> Modes=[];
        public string? BeginTrigger(bool needsApproach){Begins++;return null;}
        public void EndTrigger(){Ends++;}
        public bool PrepareTarget(ulong id,bool fighting,IReadOnlyList<ulong>? eligible=null){Modes.Add(fighting);Fighting=fighting;return Target(id);}
        public string? PrepareStart(){Prepares++;return null;}
        public bool Target(ulong id){TargetId=id;return true;}
        public bool EnableStationary(){Enables++;return Enabled=true;}
        public bool EnableTrigger(bool approach){Approach=approach;return EnableStationary();}
        public bool Disable(){Enabled=false;Approach=false;return true;}
        public bool StationaryHealthy=>Enabled;
    }
}
