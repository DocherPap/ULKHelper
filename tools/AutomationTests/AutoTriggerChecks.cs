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
        using(var f=New())
        {
            var mob=new TriggerMob(321,1332,new(20,0,0),false,false);
            var loaded=frame with{Mobs=[mob]};
            f.Helper.Start([plan],frame,1,1,time);
            f.Helper.Tick(loaded,time.AddMilliseconds(250));
            check(f.Combat.TargetId==321,"普通触发：发现目标立即直接选中");
            f.Helper.Tick(loaded,time.AddMilliseconds(500));
            f.Helper.Tick(loaded,time.AddMilliseconds(750));
            check(f.Combat.Enabled && f.Mesh.Current.Count>0,"普通触发：寻路尚未结束已启用Manual拉怪循环");
            f.Helper.Tick(loaded with{Position=new(18,0,0)},time.AddMilliseconds(1000));
            check(f.Combat.Enabled && f.Mesh.Current.Count==0,"普通触发：接近小怪后停止移动保持攻击");
        }
        using(var f=New())
        {
            var mob=new TriggerMob(123,1332,new(3,0,0),false,false);
            var loaded=frame with{Mobs=[mob]};
            f.Helper.Start([plan],frame,1,1,time);
            f.Helper.Tick(loaded,time.AddMilliseconds(250));
            f.Helper.Tick(loaded,time.AddMilliseconds(500));
            check(f.Combat.Enables==1 && f.Combat.TargetId==123,"触发：普通拉怪接近目标后启循环建立仇恨");
            var aggro=loaded with{InCombat=true,Mobs=[mob with{Aggro=true}]};
            f.Helper.Tick(aggro,time.AddMilliseconds(750));
            check(!f.Combat.Enabled,"触发：确认已拉到后关闭循环继续决策");
            f.Helper.Tick(aggro,time.AddSeconds(1));
            f.Helper.Tick(aggro,time.AddMilliseconds(1250));
            check(f.Combat.Enabled && f.Mesh.Current.Count==0,"触发：达到批次上限停止移动原地击杀");
            f.Helper.Tick(aggro with{Mobs=[mob with{Aggro=false}]},time.AddMilliseconds(1300));
            check(f.Combat.Enabled,"普通触发：怪物短暂切换目标仍击杀已拉到的小怪");
            f.Helper.Tick(frame with{Mobs=[mob with{Dead=true,Aggro=true}]},time.AddMilliseconds(1500));
            f.Helper.Tick(frame,time.AddMilliseconds(1750));
            check(f.Helper.Status.Contains("击杀 1/50"),"触发：仅计已拉怪活到死转换");
            f.Helper.Stop();
            check(!f.Combat.Enabled && !f.Helper.Active,"触发：手动停止清理自有控制");
            check(f.Combat.Begins==1 && f.Combat.Ends>=1,"触发：开始快照与结束还原接口配对");
            check(f.Combat.Modes.Contains(false) && f.Combat.Modes.Contains(true),"触发：向配置适配器传递拉怪与击杀两个阶段");
        }
        using(var f=New())
        {
            f.Helper.Start([plan],frame,1,1,time);
            var tick=0;
            for(var i=0;i<50;i++)
            {
                var mob=new TriggerMob((ulong)(100+i),1332,new(3,0,0),false,false);
                var loaded=frame with{Mobs=[mob]};
                f.Helper.Tick(loaded,time.AddMilliseconds(++tick*250));
                f.Helper.Tick(loaded,time.AddMilliseconds(++tick*250));
                var aggro=loaded with{InCombat=true,Mobs=[mob with{Aggro=true}]};
                f.Helper.Tick(aggro,time.AddMilliseconds(++tick*250));
                f.Helper.Tick(aggro,time.AddMilliseconds(++tick*250));
                f.Helper.Tick(aggro,time.AddMilliseconds(++tick*250));
                f.Helper.Tick(frame with{Mobs=[mob with{Dead=true,Aggro=true}]},time.AddMilliseconds(++tick*250));
            }
            check(!f.Helper.Active && f.Helper.Status.Contains("达到放弃数量"),"触发：50只未触发后结束，不再开始下一批");
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
        public int Prepares,Enables,Begins,Ends; public ulong TargetId; public bool Enabled;
        public List<bool> Modes=[];
        public string? BeginTrigger(){Begins++;return null;}
        public void EndTrigger(){Ends++;}
        public bool PrepareTarget(ulong id,bool fighting){Modes.Add(fighting);return Target(id);}
        public string? PrepareStart(){Prepares++;return null;}
        public bool Target(ulong id){TargetId=id;return true;}
        public bool EnableStationary(){Enables++;return Enabled=true;}
        public bool Disable(){Enabled=false;return true;}
        public bool StationaryHealthy=>Enabled;
    }
}
