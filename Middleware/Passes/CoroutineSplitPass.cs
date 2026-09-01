using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// 协程状态机改造（MW11a 棒2 起，MIDDLEWARE_ARCHITECTURE §6 /
    /// ASYNC_LOWERING_DESIGN §3.1）：对每个 IsAsync fn（含无挂起点的
    /// async fn，统一处理）：
    /// ① 活性分析——以 MirAwait/MirYieldBare/MirYieldAlarm/Mutex.enter
    ///    为挂起点，反向数据流求各挂起点恢复后仍活跃的具名局部；类级
    ///    .generic.* 局部恒视为活跃；参数全量进 frame；yield-alarm 的
    ///    alarm 槽恒活跃（PollingAlarm 恢复块要先探测）。
    /// ② frame 类型合成——state(i32) + $mw.task（自身 Task 胖引用，棒5a）
    ///    + 各保存槽（精确 MirType），经 SyntheticTypePlanner 注册。
    /// ③ 原 fn 改写为 spawn stub（保留原符号，调用点零改动）：new
    ///    frame → 参数落 frame → new Task（合成空 init，无用户 init——
    ///    对齐 VM AllocateObject + attachRuntime 通道）→ frame.task 回填
    ///    → MirCoroutineCreate（rigi_coroutine_create）→
    ///    rigi_coro_local_inherit（从启动方当前协程拷有效顶）→
    ///    lane 继承（Dispatcher.laneOfCurrent）→ attachRuntime →
    ///    noteSpawn → publish → ret 热 Task。
    /// ④ 合成 resume fn（$mw.resume.&lt;fn canonical&gt;，frame 胖引用 →
    ///    i32 RigiResumeCode）：entry switch(frame.state) 分发；await 改写
    ///    为「gate 临界区内 tryStart/spawn-into 冷启动分支 +
    ///    registerWaiter 决策码」四路 switch（0=挂起 ret SUSPENDED /
    ///    1=读 result 字段解包续行 / 2=读 native 失败注册表重抛 /
    ///    3=CANCELLED 防御 MirUnreachable）；裸 yield = Dispatcher.
    ///    publish 自重排 + ret YIELDED；yield-alarm = MirTypeCheck 分流
    ///   （Polling：poll_arm + 自重排 ret SUSPENDED，恢复块先经
    ///    $mw.poll_probe 探测 / Event：rigi_alarm_wait 闸内登记，已触发
    ///    则自重排——执行段均结束）；Mutex.enter = gate 临界区内
    ///    tryEnter，竞争则存 frame + markSuspended + ret SUSPENDED
    ///   （FIFO handoff 恢复直落原后继）；MirRet 出口改写为「写 result
    ///    字段（Task&lt;T&gt;）→ complete() → publishAll → noteTerminal
    ///    → MirCoroutineDone → ret DONE」。
    /// ⑤ 冷 Task 构造重写（棒5a，§18.4）：`new Task(body)`（body 静态
    ///    类型是具体闭包类）改写为 $mw.coldtask.* 工厂调用——工厂预建
    ///    body 的 $$call frame + cohandle 存 coldHandle（构造不继承
    ///    CoroutineLocal；继承在 spawnIntoLocked 启动时）。不透明
    ///    AsyncAction/AsyncFunc 槽跳过工厂，启动时 bindColdBody 经
    ///    type.is 链调 $mw.bindcold.* 动态 spawn-into。
    /// ⑥ 自检：split 后无残留 MirAwait/MirYieldBare/MirYieldAlarm；每
    ///    挂起点恰一恢复 state；frame 字段与保存槽集合一致。
    /// split 在 RcInjection 之前：生成的全部代码由 RcInjection 统一 ARC
    /// 配平，本 pass 不插 acquire/release。读：Mir + Layout + Symbols；
    /// 写：替换 stub、追加 resume/init/工厂合成 fn、注册 frame 类型。
    /// </summary>
    public sealed class CoroutineSplitPass : IMwStage
    {
        // resume fn 的 frame 参数名（RcInjection 识别借用约定的凭据）
        public const string FrameParamName = "$mw.frame";
        // frame 的自身 Task 字段槽名（DONE 尾/传播垫经此取回 Task 对象）
        public const string TaskSlotName = "$mw.task";
        // probe fn 的 alarm 参数名（RcInjection 借用约定凭据）
        public const string ProbeParamName = "$mw.alarm";
        // $mw.poll_probe 合成 fn canonical（模块级唯一，懒建一次——
        // $mw.named.lookup 先例）。棒5a 起是普通 MIR 调用目标（native
        // 侧不再回调——探测由恢复块直调），IsPollProbe 仅作 RcInjection
        // 垫尾分叉（ret -1）与参数借用约定凭据
        public const string PollProbeCanonical =
            "$mw.poll_probe(alarm:core.coroutine::PollingAlarm)@.i32";
        public const string PollingAlarmCanonical = "core.coroutine::PollingAlarm";
        public const string EventAlarmCanonical = "core.coroutine::EventAlarm";
        // probe fn 虚派发目标（VM VmPolling.IsReadySymbol 同拼写）
        public const string PollProbeIsReadyCanonical =
            "core.coroutine::PollingAlarm$isReady()@.bool";
        // EventAlarm 时钟底座句柄字段（yield EventAlarm 分流读取）
        public const string EventAlarmHandleField =
            "core.coroutine::EventAlarm#handle@.i64";
        public const string DispatcherCanonical = "core.coroutine::Dispatcher";
        public const string MutexCanonical = "core.coroutine::Mutex";
        public const string MutexEnterPrefix = "core.coroutine::Mutex$enter(";
        public const string MutexGateField = "core.coroutine::Mutex#gate@.i64";

        // RigiResumeCode（rigi_rt/cohandle.h 口径）：挂起/让渡/终态
        private const int ResumeSuspended = 0;
        private const int ResumeYielded = 1;
        private const int ResumeDone = 2;

        // Task.registerWaiter 决策码（stdlib coroutine.rg 契约）：
        // 0=已登记（挂起）/1=已成功/2=已失败/3=已取消
        private const int AwaitRegistered = 0;
        private const int AwaitCompleted = 1;
        private const int AwaitFailed = 2;
        private const int AwaitCancelled = 3;

        private static readonly MirType I32 = MirType.Of(".i32");
        private static readonly MirType I64 = MirType.Of(".i64");
        private static readonly MirType Bool = MirType.Of(".bool");
        private static readonly MirType Any = MirType.Of(".any");

        public string Name => "CoroutineSplit";

        private int _resourceCounter;

        // ===== Rigi 世界运行时通道符号（棒5a；按名前缀+宿主解析，不
        // 硬编码返回类型拼写）=====
        internal sealed class RuntimeSyms
        {
            internal MwMemberSymbol MutexAcquire = null!;
            internal MwMemberSymbol MutexRelease = null!;
            internal MwMemberSymbol CoroutineCurrent = null!;
            internal MwMemberSymbol CoroutineSetLane = null!;
            internal MwMemberSymbol AlarmWait = null!;
            internal MwMemberSymbol PollArm = null!;
            internal MwMemberSymbol PollPending = null!;
            internal MwMemberSymbol PollSchedule = null!;
            internal MwMemberSymbol PollClear = null!;
            internal MwMemberSymbol FailureRecord = null!;
            internal MwMemberSymbol CoroLocalInherit = null!;
            internal MwMemberSymbol DispatcherGet = null!;
            internal MwMemberSymbol NoteSpawn = null!;
            internal MwMemberSymbol NoteTerminal = null!;
            internal MwMemberSymbol Publish = null!;
            internal MwMemberSymbol PublishAll = null!;
            internal MwMemberSymbol LaneOfCurrent = null!;
        }

        private RuntimeSyms? _syms;

        internal static RuntimeSyms ResolveRuntime(MwContext context, MirModule mir)
        {
            // 顶层 native / 模块级助手：Owner=null，挂 GlobalMembers；
            // canonical 形如 core.coroutine::$name(...)（`$` 是无主
            // 成员分隔符，不是名为 `$` 的 companion 类型）
            MwMemberSymbol Global(string namePrefix) =>
                FindCoroutineGlobal(context, namePrefix)
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 native 原语声明: core.coroutine::$" + namePrefix);
            // Dispatcher 成员：名前缀 + 参数个数
            MwMemberSymbol DispatcherFn(string name) =>
                (context.Symbols.FindType(DispatcherCanonical)?.Members.FirstOrDefault(m =>
                    m.Canonical.StartsWith(DispatcherCanonical + "$" + name + "(",
                        System.StringComparison.Ordinal)))
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 Dispatcher 通道: " + name);
            var syms = new RuntimeSyms
            {
                MutexAcquire = Global("rigi_sync_mutex_acquire("),
                MutexRelease = Global("rigi_sync_mutex_release("),
                CoroutineCurrent = Global("rigi_coroutine_current("),
                CoroutineSetLane = Global("rigi_coroutine_set_lane("),
                AlarmWait = Global("rigi_alarm_wait("),
                PollArm = Global("rigi_poll_arm("),
                PollPending = Global("rigi_poll_pending("),
                PollSchedule = Global("rigi_poll_schedule("),
                PollClear = Global("rigi_poll_clear("),
                FailureRecord = Global("rigi_failure_record("),
                CoroLocalInherit = Global("rigi_coro_local_inherit("),
                NoteSpawn = DispatcherFn("noteSpawn"),
                NoteTerminal = DispatcherFn("noteTerminal"),
                Publish = DispatcherFn("publish"),
                PublishAll = DispatcherFn("publishAll"),
                LaneOfCurrent = DispatcherFn("laneOfCurrent"),
            };
            // Dispatcher singleton get fn（SingletonLowering 已合成）
            var getCanonical = SingletonPlanner.GetFnCanonicalOf(DispatcherCanonical);
            syms.DispatcherGet = mir.Functions.FirstOrDefault(f =>
                f.Symbol.Canonical == getCanonical)?.Symbol
                ?? throw new CompilerInternalException(
                    "Dispatcher singleton get fn 未合成: " + getCanonical);
            return syms;
        }

        private RuntimeSyms Syms(MwContext context, MirModule mir) =>
            _syms ??= ResolveRuntime(context, mir);

        // 顶层（Owner=null）core.coroutine 成员：按 canonical 前缀取
        internal static MwMemberSymbol? FindCoroutineGlobal(MwContext context,
            string namePrefix) =>
            context.Symbols.GlobalMembers.FirstOrDefault(m =>
                m.Canonical.StartsWith("core.coroutine::$" + namePrefix,
                    System.StringComparison.Ordinal));

        // Task 声明前缀（Task / Task<TReturn> 两套同构声明）
        internal static string TaskPrefixOf(string taskTypeRef) =>
            taskTypeRef.StartsWith("core.coroutine::Task<", System.StringComparison.Ordinal)
                ? "core.coroutine::Task<TReturn>"
                : "core.coroutine::Task";

        // await 交互点的 Task 类型取自被 await 槽，不是当前协程自身返回
        // Task（void Task 的 run() await Task<i32> 的 add() 时两套声明
        // 字段不同：result 只在 Task<TReturn> 上）
        private static string AwaitedTaskTypeRef(MirFunction fn, MirAwait awaitInst)
        {
            var canonical = fn.FindLocal(awaitInst.TaskSlot).Type.Canonical;
            if (!canonical.StartsWith("core.coroutine::Task",
                    System.StringComparison.Ordinal))
            {
                throw new CompilerInternalException(
                    "await 槽不是 Task 类型: " + awaitInst.TaskSlot + " → " + canonical);
            }
            return canonical;
        }

        // 同名不同元数：FindType 裸键只留 arity-0；泛型 Task 走 FindTypeByRef
        internal static MwTypeSymbol RequireTaskType(MwContext context, string taskTypeRef)
        {
            var prefix = TaskPrefixOf(taskTypeRef);
            return context.Symbols.FindTypeByRef(prefix)
                ?? throw new CompilerInternalException("Task 类型符号缺失: " + prefix);
        }

        internal static MwMemberSymbol TaskFn(MwContext context, string taskTypeRef,
            string name)
        {
            var prefix = TaskPrefixOf(taskTypeRef);
            return RequireTaskType(context, taskTypeRef).Members.FirstOrDefault(m =>
                m.Canonical.StartsWith(prefix + "$" + name + "(",
                    System.StringComparison.Ordinal))
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 Task 通道: " + prefix + "$" + name);
        }

        // Task 字段符号（declaration 形态；FieldEmitter 按宿主段查布局计划）
        internal static string TaskField(string taskTypeRef, string name,
            string typeCanonical) =>
            TaskPrefixOf(taskTypeRef) + "#" + name + "@" + typeCanonical;

        internal const string ExceptionCanonical = "core::Exception";

        // frame 的 Task 字段符号（槽类型 = Task 构造形态 canonical）
        internal static string TaskFieldSymbolOf(string frameCanonical,
            string taskTypeCanonical) =>
            SyntheticTypePlanner.FrameFieldSymbol(frameCanonical, TaskSlotName,
                taskTypeCanonical);

        // resume fn 的 frame.task 字段符号还原（RcInjection 传播垫用：
        // 垫在 split 之后合成，从 frame 布局计划按槽名找回）
        internal static string TaskFieldSymbolOfResume(MwContext context, MirFunction fn,
            out string taskTypeRef)
        {
            var frameCanonical = fn.FindLocal(FrameParamName).Type.Canonical;
            var plan = context.Layout?.Find(frameCanonical)
                ?? throw new CompilerInternalException(
                    "resume frame 布局计划缺失: " + frameCanonical);
            foreach (var field in plan.Fields)
            {
                if (field.Symbol.Contains("#" + TaskSlotName + "@",
                        System.StringComparison.Ordinal))
                {
                    taskTypeRef = field.Symbol.Substring(
                        field.Symbol.LastIndexOf('@') + 1);
                    return field.Symbol;
                }
            }
            throw new CompilerInternalException(
                "resume frame 缺 $mw.task 字段: " + frameCanonical);
        }

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("CoroutineSplit 要求 Mir 已挂载");
            // 快照遍历（split 向模块追加 resume/init 合成 fn）
            var asyncFns = mir.Functions.Where(f => f.IsAsync).ToList();
            foreach (var fn in asyncFns)
            {
                SplitFunction(context, mir, fn);
            }
            // 冷 Task 构造重写（§18.4；split 之后——需要 resume fn 已合成）
            RewriteColdTaskConstructions(context, mir);
            RewriteBindColdBodies(context, mir);
            // 自检①：split 后无残留 lowering 层协程指令。非 async fn
            // 携带挂起点（如 main 直接 await）属 VM 栈式跨界语义，
            // 棒2 起受控拒绝
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is MirAwait or MirYieldBare or MirYieldAlarm
                            || IsMutexEnter(inst))
                        {
                            throw new MwNotSupportedException(
                                $"MW11a 棒2 暂不支持非 async fn 的挂起点（await/yield/Mutex.enter）: {fn.Symbol.Canonical}");
                        }
                    }
                }
            }
        }

        // ===== 挂起点与活性分析 =====

        // 挂起点描述：所在块、块内指令序、state 号（1 起；0 = 原入口）
        private sealed class SuspensionPoint
        {
            internal MirBlock Block = null!;
            internal int InstIndex;
            internal int State;
            internal MirInst Inst = null!;
            // 恢复后仍活跃的槽（活性分析回填，保 fn.Locals 序）
            internal List<string> LiveAfter = new();
        }

        private static List<SuspensionPoint> CollectSuspensionPoints(MirFunction fn)
        {
            var points = new List<SuspensionPoint>();
            foreach (var block in fn.Blocks)
            {
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    if (block.Instructions[i] is MirAwait or MirYieldBare or MirYieldAlarm
                        || IsMutexEnter(block.Instructions[i]))
                    {
                        points.Add(new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = block.Instructions[i],
                        });
                    }
                }
            }
            return points;
        }

        // 反向数据流活性分析：CFG 边 = 终结符边 + 全部可抛指令的
        // ExcTarget 异常边（try 派发垫/逃逸垫里的清理状态同样跨挂起，
        // 漏边会把 finally/catch 所需槽漏出 frame）
        private static void AnalyzeLiveness(MirFunction fn, List<SuspensionPoint> points)
        {
            var successors = new Dictionary<MirBlock, List<MirBlock>>();
            var byId = new Dictionary<string, MirBlock>(System.StringComparer.Ordinal);
            foreach (var block in fn.Blocks)
            {
                byId[block.Id] = block;
            }
            foreach (var block in fn.Blocks)
            {
                var edges = new List<MirBlock>();
                void AddEdge(string? targetId)
                {
                    if (targetId != null && byId.TryGetValue(targetId, out var target)
                        && !edges.Contains(target))
                    {
                        edges.Add(target);
                    }
                }
                switch (block.Terminator)
                {
                    case MirBranch branch:
                        AddEdge(branch.Target);
                        break;
                    case MirCondBranch cond:
                        AddEdge(cond.ThenTarget);
                        AddEdge(cond.ElseTarget);
                        break;
                    case MirSwitch sw:
                        foreach (var target in sw.ItemTargets)
                        {
                            AddEdge(target);
                        }
                        AddEdge(sw.DefaultTarget);
                        break;
                }
                foreach (var inst in block.Instructions)
                {
                    if (ExcTargetOf(inst) is { } exc)
                    {
                        AddEdge(exc.Id);
                    }
                }
                successors[block] = edges;
            }

            // 逐块 def/use 前缀（反向扫描用）；块内逐指令定点计算
            var liveIn = new Dictionary<MirBlock, HashSet<string>>();
            foreach (var block in fn.Blocks)
            {
                liveIn[block] = new HashSet<string>(System.StringComparer.Ordinal);
            }
            var pointByBlock = new Dictionary<MirBlock, List<SuspensionPoint>>();
            foreach (var point in points)
            {
                if (!pointByBlock.TryGetValue(point.Block, out var list))
                {
                    list = new List<SuspensionPoint>();
                    pointByBlock[point.Block] = list;
                }
                list.Add(point);
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var bi = fn.Blocks.Count - 1; bi >= 0; bi--)
                {
                    var block = fn.Blocks[bi];
                    var live = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (var succ in successors[block])
                    {
                        live.UnionWith(liveIn[succ]);
                    }
                    AddTerminatorUses(block.Terminator, live);
                    // 块内反向扫：挂起点先记录 live-after（指令之后的活跃
                    // 集），再按 def/use 回推
                    for (var i = block.Instructions.Count - 1; i >= 0; i--)
                    {
                        var inst = block.Instructions[i];
                        if (pointByBlock.TryGetValue(block, out var blockPoints))
                        {
                            foreach (var point in blockPoints)
                            {
                                if (point.InstIndex == i)
                                {
                                    point.LiveAfter = fn.Locals
                                        .Where(l => live.Contains(l.Name))
                                        .Select(l => l.Name).ToList();
                                }
                            }
                        }
                        ApplyDefUse(inst, live);
                    }
                    if (!live.SetEquals(liveIn[block]))
                    {
                        liveIn[block] = live;
                        changed = true;
                    }
                }
            }
            foreach (var point in points)
            {
                // await 的 task 槽强制活跃：恢复块重回 wait 块重新登记/
                // 快路径（task 从 frame 恢复）
                if (point.Inst is MirAwait awaitInst
                    && !point.LiveAfter.Contains(awaitInst.TaskSlot))
                {
                    point.LiveAfter.Add(awaitInst.TaskSlot);
                }
                // 棒5a：yield-alarm 的 alarm 槽恒活跃——PollingAlarm
                // 恢复块要先经 $mw.poll_probe 探测（alarm 从 frame 恢复）
                if (point.Inst is MirYieldAlarm yieldAlarm
                    && !point.LiveAfter.Contains(yieldAlarm.AlarmSlot))
                {
                    point.LiveAfter.Add(yieldAlarm.AlarmSlot);
                }
                // Mutex.enter 的 receiver 恒活跃：恢复后 acquire 续行读
                // this.gate 构造 Lock
                if (point.Inst is MirCall enterCall && IsMutexEnter(enterCall)
                    && enterCall.Args.Count > 0
                    && enterCall.Args[0] is MirLocalOperand mutexThis
                    && !point.LiveAfter.Contains(mutexThis.Name))
                {
                    point.LiveAfter.Add(mutexThis.Name);
                }
            }
        }

        private static bool IsMutexEnter(MirInst inst) =>
            inst is MirCall call
            && call.Target.Canonical.StartsWith(MutexEnterPrefix,
                System.StringComparison.Ordinal);

        internal static MwMemberSymbol MutexFn(MwContext context, string name) =>
            (context.Symbols.FindType(MutexCanonical)?.Members.FirstOrDefault(m =>
                m.Canonical.StartsWith(MutexCanonical + "$" + name + "(",
                    System.StringComparison.Ordinal)))
            ?? throw new CompilerInternalException("stdlib 缺少 Mutex 通道: " + name);

        private static MirBlock? ExcTargetOf(MirInst inst) => inst switch
        {
            MirCall call => call.ExcTarget,
            MirSuperCall superCall => superCall.ExcTarget,
            MirInvokeIndirect invoke => invoke.ExcTarget,
            MirInnerCall inner => inner.ExcTarget,
            MirThrow throwInst => throwInst.ExcTarget,
            MirBinaryIntrinsic binary => binary.ExcTarget,
            MirCast cast => cast.ExcTarget,
            MirUnboxAny unbox => unbox.ExcTarget,
            MirSetArray setArray => setArray.ExcTarget,
            MirNewIndirect newIndirect => newIndirect.ExcTarget,
            MirNewObject newObject => newObject.ExcTarget,
            MirGetField getField => getField.ExcTarget,
            MirAwait awaitInst => awaitInst.ExcTarget,
            _ => null,
        };

        private static void AddOperandUse(MirOperand? operand, HashSet<string> into)
        {
            if (operand is MirLocalOperand local)
            {
                into.Add(local.Name);
            }
        }

        private static void AddOperandUses(IEnumerable<MirOperand> operands, HashSet<string> into)
        {
            foreach (var operand in operands)
            {
                AddOperandUse(operand, into);
            }
        }

        private static void AddTerminatorUses(MirTerminator terminator, HashSet<string> into)
        {
            switch (terminator)
            {
                case MirRet ret:
                    AddOperandUse(ret.Value, into);
                    break;
                case MirCondBranch cond:
                    AddOperandUse(cond.Condition, into);
                    break;
                case MirSwitch sw:
                    AddOperandUse(sw.Selector, into);
                    break;
            }
        }

        // 单指令 def/use 回推：live = (live − def) ∪ use
        private static void ApplyDefUse(MirInst inst, HashSet<string> live)
        {
            if (DefOf(inst) is { } def)
            {
                live.Remove(def);
            }
            switch (inst)
            {
                case MirCopyLocal copy:
                    AddOperandUse(copy.Source, live);
                    break;
                case MirBinaryIntrinsic binary:
                    AddOperandUse(binary.Left, live);
                    AddOperandUse(binary.Right, live);
                    break;
                case MirUnaryIntrinsic unary:
                    AddOperandUse(unary.Operand, live);
                    break;
                case MirCall call:
                    AddOperandUses(call.Args, live);
                    break;
                case MirSuperCall superCall:
                    AddOperandUses(superCall.Args, live);
                    break;
                case MirInnerCall inner:
                    AddOperandUses(inner.Args, live);
                    break;
                case MirInvokeIndirect invoke:
                    AddOperandUse(invoke.CallTarget, live);
                    AddOperandUses(invoke.Args, live);
                    break;
                case MirNewObject newObject:
                    AddOperandUses(newObject.Args, live);
                    AddOperandUses(newObject.WrapperArgs, live);
                    break;
                case MirNewValue newValue:
                    AddOperandUses(newValue.Args, live);
                    AddOperandUses(newValue.WrapperArgs, live);
                    break;
                case MirNewCase newCase:
                    AddOperandUses(newCase.Args, live);
                    break;
                case MirNewIndirect newIndirect:
                    AddOperandUse(newIndirect.TypeId, live);
                    AddOperandUses(newIndirect.Args, live);
                    break;
                case MirTypeCheck typeCheck:
                    AddOperandUse(typeCheck.Value, live);
                    AddOperandUse(typeCheck.TargetTypeId, live);
                    break;
                case MirIsCase isCase:
                    AddOperandUse(isCase.Value, live);
                    break;
                case MirGetField getField:
                    AddOperandUse(getField.Object, live);
                    break;
                case MirGetWrapper getWrapper:
                    AddOperandUse(getWrapper.Host, live);
                    break;
                case MirGetWrapperField getWrapperField:
                    AddOperandUse(getWrapperField.Host, live);
                    break;
                case MirGetWrapperAddr getWrapperAddr:
                    AddOperandUse(getWrapperAddr.Host, live);
                    break;
                case MirGetWrapperFieldAddr getWrapperFieldAddr:
                    AddOperandUse(getWrapperFieldAddr.Host, live);
                    break;
                case MirGetWrapperMethodAddr getWrapperMethodAddr:
                    AddOperandUse(getWrapperMethodAddr.Host, live);
                    break;
                case MirSetWrapperField setWrapperField:
                    AddOperandUse(setWrapperField.Source, live);
                    AddOperandUse(setWrapperField.Host, live);
                    break;
                case MirNewWrapper newWrapper:
                    AddOperandUse(newWrapper.Host, live);
                    AddOperandUses(newWrapper.Args, live);
                    break;
                case MirSetStatic setStatic:
                    AddOperandUse(setStatic.Source, live);
                    break;
                case MirSetField setField:
                    AddOperandUse(setField.Source, live);
                    AddOperandUse(setField.Object, live);
                    break;
                case MirGetArray getArray:
                    AddOperandUse(getArray.Collection, live);
                    AddOperandUse(getArray.Index, live);
                    break;
                case MirSetArray setArray:
                    AddOperandUse(setArray.Collection, live);
                    AddOperandUse(setArray.Index, live);
                    AddOperandUse(setArray.Element, live);
                    break;
                case MirNewArray newArray:
                    AddOperandUses(newArray.Elements, live);
                    break;
                case MirGetTypeIdVar getTypeIdVar:
                    AddOperandUse(getTypeIdVar.Value, live);
                    break;
                case MirWrapNullable wrap:
                    AddOperandUse(wrap.Source, live);
                    break;
                case MirUnwrapNullable unwrap:
                    AddOperandUse(unwrap.Source, live);
                    break;
                case MirBoxAny box:
                    AddOperandUse(box.Source, live);
                    break;
                case MirUnboxAny unbox:
                    AddOperandUse(unbox.Source, live);
                    break;
                case MirCast cast:
                    AddOperandUse(cast.Source, live);
                    AddOperandUse(cast.TargetTypeId, live);
                    break;
                case MirThrow throwInst:
                    AddOperandUse(throwInst.Exception, live);
                    break;
                case MirAwait awaitInst:
                    AddOperandUse(new MirLocalOperand(awaitInst.TaskSlot), live);
                    break;
                case MirYieldAlarm yieldAlarm:
                    AddOperandUse(new MirLocalOperand(yieldAlarm.AlarmSlot), live);
                    break;
                // 棒5a：create 借用 frame（move 语义归 RcInjection）
                case MirCoroutineCreate create:
                    AddOperandUse(new MirLocalOperand(create.FrameSlot), live);
                    break;
                case MirFailureLoad failureLoad:
                    AddOperandUse(new MirLocalOperand(failureLoad.NodeIdSlot), live);
                    break;
            }
        }

        private static string? DefOf(MirInst inst) => inst switch
        {
            MirLoadResource load => load.Target,
            MirCopyLocal copy => copy.Target,
            MirBinaryIntrinsic binary => binary.Target,
            MirUnaryIntrinsic unary => unary.Target,
            MirCall call => call.Result,
            MirSuperCall superCall => superCall.Result,
            MirInvokeIndirect invoke => invoke.Result,
            MirInnerCall inner => inner.Result,
            MirNewObject newObject => newObject.Target,
            MirNewValue newValue => newValue.Target,
            MirNewCase newCase => newCase.Target,
            MirNewIndirect newIndirect => newIndirect.Target,
            MirTypeCheck typeCheck => typeCheck.Target,
            MirIsCase isCase => isCase.Target,
            MirGetField getField => getField.Target,
            MirGetWrapper getWrapper => getWrapper.Target,
            MirGetWrapperField getWrapperField => getWrapperField.Target,
            MirGetWrapperAddr getWrapperAddr => getWrapperAddr.Target,
            MirGetWrapperFieldAddr getWrapperFieldAddr => getWrapperFieldAddr.Target,
            MirGetWrapperMethodAddr getWrapperMethodAddr => getWrapperMethodAddr.Target,
            MirGetSelf getSelf => getSelf.Target,
            MirGetStatic getStatic => getStatic.Target,
            MirGetArray getArray => getArray.Target,
            MirNewArray newArray => newArray.Target,
            MirGetTypeId getTypeId => getTypeId.Target,
            MirGetTypeIdVar getTypeIdVar => getTypeIdVar.Target,
            MirWrapNullable wrap => wrap.Target,
            MirUnwrapNullable unwrap => unwrap.Target,
            MirBoxAny box => box.Target,
            MirUnboxAny unbox => unbox.Target,
            MirCast cast => cast.Target,
            MirTakePending takePending => takePending.TargetLocal,
            MirAwait awaitInst => awaitInst.ResultSlot,
            MirCoroutineCreate create => create.HandleSlot,
            MirFailureLoad failureLoad => failureLoad.OutFatSlot,
            _ => null,
        };

        // ===== split 主流程 =====

        private void SplitFunction(MwContext context, MirModule mir, MirFunction fn)
        {
            var points = CollectSuspensionPoints(fn);
            AnalyzeLiveness(fn, points);

            // 保存槽集 = 全部参数 ∪ 类级 .generic.* 局部（恒活跃）∪ 各
            // 挂起点 live-after 并集，保 fn.Locals 序（frame 字段序确定性）
            var paramNames = new HashSet<string>(fn.Parameters.Select(p => p.Name),
                System.StringComparer.Ordinal);
            var liveUnion = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var point in points)
            {
                liveUnion.UnionWith(point.LiveAfter);
            }
            var savedSlots = fn.Locals.Where(local =>
                paramNames.Contains(local.Name)
                || local.Name.StartsWith(".generic.", System.StringComparison.Ordinal)
                || liveUnion.Contains(local.Name)).ToList();

            // 棒5a：frame 追加 $mw.task 字段（自身 Task 胖引用——DONE 尾/
            // 传播垫/恢复块经此取回 Task；取代旧面 rigi_task_current）
            var taskTypeRef = TaskTypeRefOf(fn.ReturnType);
            // 返回 T 的泛型 async：仍是 Task<TReturn> 族（有 result 字段）。
            // 不得回退无元数 Task——可见区已不同（result），且 await 按
            // Task<TReturn> 读 gate/handle
            var taskConstructionRef = TypeLayout.IsGenericPlaceholder(
                MirType.Of(taskTypeRef))
                ? TaskPrefixOf(taskTypeRef)
                : taskTypeRef;
            var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(fn.Symbol.Canonical);
            var frameSlots = savedSlots.Select(l => (l.Name, l.Type)).ToList();
            frameSlots.Add((TaskSlotName, MirType.Of(taskConstructionRef)));
            var frameType = SyntheticTypePlanner.EnsureFrameType(context, frameCanonical,
                frameSlots);
            var frameMirType = MirType.Of(frameCanonical);
            var stateFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(frameCanonical,
                SyntheticTypePlanner.StateFieldName, I32.Canonical);
            string FieldOf(MirLocal local) => SyntheticTypePlanner.FrameFieldSymbol(
                frameCanonical, local.Name, local.Type.Canonical);
            var taskFieldSymbol = TaskFieldSymbolOf(frameCanonical, taskConstructionRef);

            // frame 空 init（裸 ret；字段零值由 rigi_alloc 清零承担）
            var initSymbol = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(frameCanonical))
                ?? throw new CompilerInternalException("frame 空 init 未随类型注册: " + frameCanonical);
            var thisParam = new MirLocal(".this", frameMirType);
            mir.AddFunction(new MirFunction(initSymbol, MirType.Of(".void"),
                new List<MirLocal> { thisParam }, new List<MirLocal> { thisParam },
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirRet(null)),
                }, false));

            var resumeSymbol = ProxyBakeSupport.SyntheticMember(
                "$mw.resume." + fn.Symbol.Canonical, owner: null);

            var resumeFn = BuildResumeFunction(context, mir, fn, points, savedSlots,
                paramNames, frameMirType, stateFieldSymbol, taskFieldSymbol,
                taskTypeRef, FieldOf, resumeSymbol);
            mir.AddFunction(resumeFn);
            if (IsZeroArgAsyncCall(fn))
            {
                var thisLocal = fn.Parameters.FirstOrDefault(p => p.Name == ".this")
                    ?? throw new CompilerInternalException(
                        "0 参 $$call 缺 .this: " + fn.Symbol.Canonical);
                _coldBinds.Add(new ColdBindEntry
                {
                    OwnerCanonical = fn.Symbol.Owner!.Canonical,
                    CallCanonical = fn.Symbol.Canonical,
                    ResumeSymbol = resumeSymbol,
                    FrameCanonical = frameCanonical,
                    FrameType = frameType,
                    FrameInit = initSymbol,
                    TaskTypeRef = taskTypeRef,
                    TaskConstructionRef = taskConstructionRef,
                    ThisType = thisLocal.Type,
                });
            }
            ReplaceWithStub(context, mir, fn, frameType, frameMirType, initSymbol,
                resumeSymbol, taskTypeRef, taskFieldSymbol, FieldOf);

            // 自检②③：state 分发表项恰覆盖 入口+挂起点；frame 字段 =
            // state + 保存槽 + $mw.task
            var plan = context.Layout!.Find(frameCanonical)
                ?? throw new CompilerInternalException("frame 布局计划缺失: " + frameCanonical);
            if (plan.Fields.Count != savedSlots.Count + 2)
            {
                throw new CompilerInternalException(
                    $"CoroutineSplit 自检失败：{fn.Symbol.Canonical} frame 字段数 {plan.Fields.Count} ≠ 保存槽 {savedSlots.Count} + state + task");
            }
        }

        private static string TaskTypeRefOf(MirType returnType) =>
            returnType.IsVoid
                ? "core.coroutine::Task"
                : "core.coroutine::Task<" + returnType.Canonical + ">";

        // Task 合成空 init（模块级懒建，按声明形态缓存；字段零值由
        // rigi_alloc 清零承担——singleton 空 init 同口径）。gate/body
        // 等由 attachRuntime/工厂随后填充
        private readonly Dictionary<string, MwMemberSymbol> _taskEmptyInits = new(
            System.StringComparer.Ordinal);

        private MwMemberSymbol TaskEmptyInit(MwContext context, MirModule mir,
            string taskTypeRef)
        {
            var declarationRef = TaskPrefixOf(taskTypeRef);
            if (_taskEmptyInits.TryGetValue(declarationRef, out var cached))
            {
                return cached;
            }
            var type = RequireTaskType(context, taskTypeRef);
            var symbol = ProxyBakeSupport.SyntheticMember(
                declarationRef + "$init()@.void", type);
            var thisParam = new MirLocal(".this", MirType.Of(declarationRef));
            mir.AddFunction(new MirFunction(symbol, MirType.Of(".void"),
                new List<MirLocal> { thisParam }, new List<MirLocal> { thisParam },
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirRet(null)),
                }, false));
            _taskEmptyInits.Add(declarationRef, symbol);
            return symbol;
        }

        // Task 构造类型符号：闭合构造经 MwTypeSymbol 包装（CallVisitors
        // 同口径）。模板 Canonical 是裸 `core.coroutine::Task`（同名不同
        // 元数共用声明符号）——TypeSheetFor 精确命中 arity-0 的 void Task
        // sheet 后，对象头/vtable/gate 全错（void gate@72 vs Task<T>
        // gate@88）。泛型 Task 必须包装带实参的 identity。
        private static MwTypeSymbol TaskTypeSymbolOf(MwContext context,
            string taskTypeRef)
        {
            var template = RequireTaskType(context, taskTypeRef);
            if (template.Declaration.GenericParameters.Count == 0)
            {
                return template;
            }
            return new MwTypeSymbol(GenericTaskSheetIdentity(taskTypeRef, template),
                template);
        }

        // 声明形 Task<TReturn> 会让 Layout.Find 命中模板后 WriteHiddenTypeIds
        // 对实参名取 sheet 崩；改写为占位构造。已是占位/闭合构造则原样。
        private static string GenericTaskSheetIdentity(string taskTypeRef,
            MwTypeSymbol template)
        {
            var normalized = MwTypeKey.Normalize(taskTypeRef);
            if (normalized == template.Canonical
                || normalized == TaskPrefixOf(taskTypeRef))
            {
                var param = template.Declaration.GenericParameters[0];
                return template.Canonical + "<.generic<$.generic." + param + ">>";
            }
            return normalized;
        }

        // ===== ④ resume fn 合成 =====

        private MirFunction BuildResumeFunction(MwContext context, MirModule mir,
            MirFunction fn, List<SuspensionPoint> points, List<MirLocal> savedSlots,
            HashSet<string> paramNames, MirType frameMirType, string stateFieldSymbol,
            string taskFieldSymbol, string taskTypeRef,
            System.Func<MirLocal, string> fieldOf, MwMemberSymbol resumeSymbol)
        {
            // 局部表：frame 参数 + 原 fn 全部局部副本（同名同型；参数在
            // resume fn 是普通局部，state 0 从 frame 恢复）
            var frameParam = new MirLocal(FrameParamName, frameMirType);
            var locals = new List<MirLocal> { frameParam };
            foreach (var local in fn.Locals)
            {
                locals.Add(new MirLocal(local.Name, local.Type));
            }
            var resumeFn = new MirFunction(resumeSymbol, I32,
                new List<MirLocal> { frameParam }, locals, new List<MirBlock>(),
                false, isCoroutineResume: true);
            var frameOp = new MirLocalOperand(FrameParamName);
            var syms = Syms(context, mir);

            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);
            MirLoadResource I32Const(int value, string target) =>
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, value), target);

            // 保存/恢复序列（保 savedSlots 序；frame 字段显式读写）
            void EmitSave(List<MirInst> insts, IReadOnlyList<string> slotNames)
            {
                foreach (var name in slotNames)
                {
                    var local = fn.FindLocal(name);
                    insts.Add(new MirSetField(new MirLocalOperand(name), frameOp,
                        fieldOf(local)));
                }
            }

            void EmitRestore(List<MirInst> insts, IReadOnlyList<string> slotNames)
            {
                foreach (var name in slotNames)
                {
                    var local = fn.FindLocal(name);
                    insts.Add(new MirGetField(frameOp, fieldOf(local), name));
                }
            }

            MirTerminator ResumeRet(int code, List<MirInst> insts)
            {
                var codeLocal = Fresh("$mw.code.", I32);
                insts.Add(I32Const(code, codeLocal));
                return new MirRet(new MirLocalOperand(codeLocal));
            }

            // entry：读 state → MirSwitch 分发（state 0=原入口，N=各恢复
            // 块；default=损坏防御不可达）。块 id 取 mw.entry——原 fn 首块
            // 同名 "entry" 且整体迁入本 fn，同名会在 Emit 块表撞键
            var stateLocal = Fresh("$mw.state.", I32);
            var itemTargets = new List<string> { "mw.state.0" };
            var tableElements = new List<string> { "0" };
            foreach (var point in points)
            {
                itemTargets.Add("mw.state." + point.State);
                tableElements.Add(point.State.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            var stateTable = new BilSwitchTableResource(
                "$mw.coroutine.state." + _resourceCounter++, ".i32", tableElements);
            context.Module.Resources.Add(stateTable);
            resumeFn.AddBlock(new MirBlock("mw.entry", new List<MirInst>
            {
                new MirGetField(frameOp, stateFieldSymbol, stateLocal),
            }, new MirSwitch(new MirLocalOperand(stateLocal), stateTable, itemTargets,
                "mw.state.bad")));
            resumeFn.AddBlock(new MirBlock("mw.state.bad", new List<MirInst>(),
                new MirUnreachable()));

            // state 0：恢复参数 + 类级 .generic.* 局部（初段的输入）后进原
            // 入口块；非参数局部此态未定义，不恢复
            var entryRestore = savedSlots
                .Where(l => paramNames.Contains(l.Name)
                    || l.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                .Select(l => l.Name).ToList();
            var state0Insts = new List<MirInst>();
            EmitRestore(state0Insts, entryRestore);
            var entryBlockId = fn.Blocks[0].Id;
            resumeFn.AddBlock(new MirBlock("mw.state.0", state0Insts,
                new MirBranch(entryBlockId)));

            // 挂起点恢复块（state N）：恢复活跃槽 → 重回 wait 块（await）
            // 或 yield-alarm 探测块 / 原后继（裸 yield）
            foreach (var point in points)
            {
                var restoreInsts = new List<MirInst>();
                EmitRestore(restoreInsts, point.LiveAfter);
                string resumeTarget;
                if (point.Inst is MirAwait)
                {
                    resumeTarget = WaitBlockId(point);
                }
                else if (point.Inst is MirYieldAlarm)
                {
                    // 棒5a：先经轮询判位分流——EventAlarm 响铃恢复直续
                    // 原后继；PollingAlarm 重发布恢复先探测
                    resumeTarget = PollGateBlockId(point);
                    EmitRestoreMark(context, restoreInsts, frameOp,
                        taskFieldSymbol, taskTypeRef, Fresh);
                    resumeFn.AddBlock(new MirBlock("mw.state." + point.State, restoreInsts,
                        new MirBranch(resumeTarget)));
                    EmitPollGate(context, mir, resumeFn, fn, point, frameOp,
                        taskFieldSymbol, taskTypeRef, syms);
                    continue;
                }
                else
                {
                    // 裸 yield / Mutex.enter：恢复直落原后继
                    resumeTarget = ContBlockId(point);
                }
                EmitRestoreMark(context, restoreInsts, frameOp,
                    taskFieldSymbol, taskTypeRef, Fresh);
                resumeFn.AddBlock(new MirBlock("mw.state." + point.State, restoreInsts,
                    new MirBranch(resumeTarget)));
            }

            // 原体块迁入 + 挂起点改写 + MirRet 出口改写
            var pointsByBlock = points.GroupBy(p => p.Block)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.InstIndex).ToList());
            foreach (var block in fn.Blocks)
            {
                if (!pointsByBlock.TryGetValue(block, out var blockPoints))
                {
                    RewriteReturn(context, mir, resumeFn, block, fn, frameOp,
                        taskFieldSymbol, taskTypeRef, syms);
                    resumeFn.AddBlock(block);
                    continue;
                }
                var segmentStart = 0;
                var currentId = block.Id;
                foreach (var point in blockPoints)
                {
                    var headInsts = block.Instructions
                        .Take(point.InstIndex).Skip(segmentStart).ToList();
                    if (point.Inst is MirAwait awaitInst)
                    {
                        EmitAwaitSplit(context, resumeFn, fn, point, awaitInst,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef,
                            syms, EmitSave, I32Const, Fresh,
                            ResumeRet);
                    }
                    else if (point.Inst is MirYieldAlarm yieldAlarm)
                    {
                        EmitYieldAlarmSplit(context, resumeFn, point, yieldAlarm,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef,
                            EmitSave, ResumeRet, syms, Fresh);
                    }
                    else if (point.Inst is MirCall enterCall && IsMutexEnter(enterCall))
                    {
                        EmitMutexEnterSplit(context, resumeFn, point, enterCall,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef, syms, EmitSave,
                            I32Const, Fresh, ResumeRet);
                    }
                    else
                    {
                        EmitYieldSplit(context, resumeFn, point, currentId, headInsts,
                            frameOp, stateFieldSymbol, EmitSave, ResumeRet, syms, Fresh);
                    }
                    segmentStart = point.InstIndex + 1;
                    currentId = ContBlockId(point);
                }
                // 尾段：挂起点之后的剩余指令 + 原终结符
                var tailInsts = block.Instructions.Skip(segmentStart).ToList();
                var tail = new MirBlock(currentId, tailInsts, block.Terminator);
                RewriteReturn(context, mir, resumeFn, tail, fn, frameOp,
                    taskFieldSymbol, taskTypeRef, syms);
                resumeFn.AddBlock(tail);
            }
            return resumeFn;
        }

        private static string WaitBlockId(SuspensionPoint point) =>
            point.Block.Id + ".wait" + point.State;

        private static string ContBlockId(SuspensionPoint point) =>
            point.Block.Id + ".cont" + point.State;

        private static string PollGateBlockId(SuspensionPoint point) =>
            point.Block.Id + ".pollgate" + point.State;

        // await 改写（棒5a 新交互点，对齐 VM VmDispatch.Await 语义）：
        // wait 块 = acquire gate →（handle==0 冷 Task：tryStart 一次性
        // 判定 → 赢家 spawnIntoLocked + noteSpawn + publish）→ 先存活跃
        // 槽 + state=N（临界区内——发布安全的前提：waiter 被排空重发布
        // 时 frame 必已写完）→ registerWaiter（当前协程句柄）→ release
        // → 四路 switch（0=挂起 ret SUSPENDED / 1=读 result 字段解包续行
        // / 2=native 失败注册表取异常沿原 ExcTarget 重抛 / 3=防御不可达）。
        // 恢复块重回 wait 块走终态快路径（task 槽在 frame）
        private void EmitAwaitSplit(MwContext context, MirFunction resumeFn, MirFunction fn,
            SuspensionPoint point, MirAwait awaitInst, string headId, List<MirInst> headInsts,
            MirLocalOperand frameOp, string stateFieldSymbol,
            string taskFieldSymbol, string taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet)
        {
            var awaitedTaskTypeRef = AwaitedTaskTypeRef(fn, awaitInst);
            var code = fresh("$mw.await.code.", I32);
            var waitId = WaitBlockId(point);
            var coldId = waitId + ".cold";
            var coldGoId = waitId + ".coldgo";
            var regId = waitId + ".reg";
            var suspendId = waitId + ".suspend";
            var doneId = waitId + ".done";
            var failId = waitId + ".fail";
            var cancelId = waitId + ".cancel";
            var taskOp = new MirLocalOperand(awaitInst.TaskSlot);
            var gateField = TaskField(awaitedTaskTypeRef, "gate", ".i64");
            var handleField = TaskField(awaitedTaskTypeRef, "handle", ".i64");

            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(waitId)));

            // wait：acquire → 冷启动分支 → 寄存
            var gate = fresh("$mw.await.gate.", I64);
            var th = fresh("$mw.await.th.", I64);
            var zeroH = fresh("$mw.await.z.", I64);
            var isCold = fresh("$mw.await.cold.", Bool);
            var waitInsts = new List<MirInst>
            {
                new MirGetField(taskOp, gateField, gate),
                new MirCall(syms.MutexAcquire,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
                new MirGetField(taskOp, handleField, th),
                new MirLoadResource(ProxyWildcardAbi.AddI64Resource(context, 0), zeroH),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(th),
                    new MirLocalOperand(zeroH), I64, I64, Bool, isCold),
            };
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                new MirCondBranch(new MirLocalOperand(isCold), coldId, regId)));

            // 冷 Task 首次 await（§18.3/§18.4）：同一临界区内一次性判定
            // ——赢家 spawn-into（当前 Executor）+ noteSpawn + publish，
            // 输家按普通 waiter 登记（不抛）
            var st = fresh("$mw.await.st.", I32);
            var stZero = fresh("$mw.await.stz.", I32);
            var stGo = fresh("$mw.await.stgo.", Bool);
            resumeFn.AddBlock(new MirBlock(coldId, new List<MirInst>
            {
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "tryStart"),
                    new List<MirOperand> { taskOp }, st),
                i32Const(0, stZero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(st),
                    new MirLocalOperand(stZero), I32, I32, Bool, stGo),
            }, new MirCondBranch(new MirLocalOperand(stGo), coldGoId, regId)));

            var dispC = fresh("$mw.disp.", MirType.Of(DispatcherCanonical));
            var h2 = fresh("$mw.await.h2.", I64);
            resumeFn.AddBlock(new MirBlock(coldGoId, new List<MirInst>
            {
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "spawnIntoLocked"),
                    new List<MirOperand> { taskOp }, null),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispC),
                new MirCall(syms.NoteSpawn,
                    new List<MirOperand> { new MirLocalOperand(dispC) }, null),
                new MirGetField(taskOp, handleField, h2),
                new MirCall(syms.Publish,
                    new List<MirOperand> { new MirLocalOperand(dispC),
                        new MirLocalOperand(h2) }, null),
            }, new MirBranch(regId)));

            // reg：存活跃槽 + state=N（临界区内，见上注释）→
            // registerWaiter → release → 四路 switch
            var regInsts = new List<MirInst>();
            emitSave(regInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            regInsts.Add(i32Const(point.State, stateConst));
            regInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            var cur = fresh("$mw.await.cur.", I64);
            regInsts.Add(new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur));
            regInsts.Add(new MirCall(TaskFn(context, awaitedTaskTypeRef, "registerWaiter"),
                new List<MirOperand> { taskOp, new MirLocalOperand(cur) }, code));
            regInsts.Add(new MirCall(syms.MutexRelease,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            var awaitTable = new BilSwitchTableResource(
                "$mw.coroutine.await." + _resourceCounter++, ".i32",
                new[]
                {
                    AwaitRegistered.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AwaitCompleted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AwaitFailed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    AwaitCancelled.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            context.Module.Resources.Add(awaitTable);
            resumeFn.AddBlock(new MirBlock(regId, regInsts,
                new MirSwitch(new MirLocalOperand(code), awaitTable,
                    new[] { suspendId, doneId, failId, cancelId }, "mw.state.bad")));

            // 挂起：frame 已在临界区内写完，投影 Suspended，直返 SUSPENDED
            var suspendInsts = new List<MirInst>();
            EmitTaskMark(context, suspendInsts, frameOp, taskFieldSymbol,
                taskTypeRef, "markSuspended", fresh);
            resumeFn.AddBlock(new MirBlock(suspendId, suspendInsts,
                resumeRet(ResumeSuspended, suspendInsts)));

            // 成功：读 result 字段（nullable<T>）解包回结果槽
            var doneInsts = new List<MirInst>();
            if (awaitInst.ResultSlot != null)
            {
                var resultType = fn.FindLocal(awaitInst.ResultSlot).Type;
                var rn = fresh("$mw.await.rn.",
                    MirType.Of(".nullable<" + resultType.Canonical + ">"));
                doneInsts.Add(new MirGetField(taskOp,
                    TaskField(awaitedTaskTypeRef, "result",
                        ".nullable<" + resultType.Canonical + ">"), rn));
                doneInsts.Add(new MirUnwrapNullable(new MirLocalOperand(rn),
                    resultType, awaitInst.ResultSlot));
            }
            resumeFn.AddBlock(new MirBlock(doneId, doneInsts,
                new MirBranch(ContBlockId(point))));

            // 失败：native 失败注册表按节点 id 取异常（+1 随 out 移交），
            // 沿原 MirAwait 的 ExcTarget 重抛；null 留 RcInjection 进垫
            var nodeId = fresh("$mw.await.nid.", I64);
            var outFat = fresh("$mw.await.out.", Any);
            var failInsts = new List<MirInst>
            {
                new MirGetField(taskOp, TaskField(awaitedTaskTypeRef, "failureNodeId", ".i64"),
                    nodeId),
                new MirFailureLoad(nodeId, outFat),
                new MirThrow(new MirLocalOperand(outFat), awaitInst.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                awaitInst.ExcTarget != null
                    ? (MirTerminator)new MirBranch(awaitInst.ExcTarget.Id)
                    : new MirRetThrow()));

            resumeFn.AddBlock(new MirBlock(cancelId, new List<MirInst>(),
                new MirUnreachable()));
        }

        // Mutex.enter 改写（§19.6；对齐 VM MutexEnter）：gate 临界区内
        // tryEnter 判定；0=立即取得，release 后落原后继；1=已登记 FIFO
        // 队尾——临界区内写完 frame + markSuspended，再 release，ret
        // SUSPENDED。恢复块直落原后继（FIFO handoff 已移交锁，不重入
        // tryEnter）。enter 空体仅供 VM 方法 hook；native 本改写替换调用。
        private void EmitMutexEnterSplit(MwContext context, MirFunction resumeFn,
            SuspensionPoint point, MirCall enterCall, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string taskFieldSymbol, string taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet)
        {
            var mutexOp = enterCall.Args[0];
            var enterId = point.Block.Id + ".mxenter" + point.State;
            var gotId = enterId + ".got";
            var waitId = enterId + ".wait";
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(enterId)));

            var gate = fresh("$mw.mx.gate.", I64);
            var cur = fresh("$mw.mx.cur.", I64);
            var code = fresh("$mw.mx.code.", I32);
            var zero = fresh("$mw.mx.z.", I32);
            var got = fresh("$mw.mx.got.", Bool);
            var enterInsts = new List<MirInst>
            {
                new MirGetField(mutexOp, MutexGateField, gate),
                new MirCall(syms.MutexAcquire,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur),
                new MirCall(MutexFn(context, "tryEnter"),
                    new List<MirOperand> { mutexOp, new MirLocalOperand(cur) }, code),
                i32Const(0, zero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(code),
                    new MirLocalOperand(zero), I32, I32, Bool, got),
            };
            resumeFn.AddBlock(new MirBlock(enterId, enterInsts,
                new MirCondBranch(new MirLocalOperand(got), gotId, waitId)));

            var gotInsts = new List<MirInst>
            {
                new MirCall(syms.MutexRelease,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
            };
            resumeFn.AddBlock(new MirBlock(gotId, gotInsts,
                new MirBranch(ContBlockId(point))));

            var waitInsts = new List<MirInst>();
            emitSave(waitInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            waitInsts.Add(i32Const(point.State, stateConst));
            waitInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            EmitTaskMark(context, waitInsts, frameOp, taskFieldSymbol,
                taskTypeRef, "markSuspended", fresh);
            waitInsts.Add(new MirCall(syms.MutexRelease,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                resumeRet(ResumeSuspended, waitInsts)));
        }

        // 裸 yield 改写（棒5a，对齐 VM YieldBare → Publish）：head →
        // 挂起段（存活跃槽 + state=N + Dispatcher.publish 自重排 +
        // ret YIELDED）；恢复块落原后继
        private void EmitYieldSplit(MwContext context, MirFunction resumeFn,
            SuspensionPoint point, string headId, List<MirInst> headInsts,
            MirLocalOperand frameOp, string stateFieldSymbol,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh)
        {
            var yieldId = point.Block.Id + ".yield" + point.State;
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(yieldId)));
            var insts = new List<MirInst>();
            emitSave(insts, point.LiveAfter);
            // state 常量化存 frame（恢复块经 switch(frame.state) 到达）
            var stateConst = fresh("$mw.state.c.", I32);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, point.State), stateConst));
            insts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            // Dispatcher.publish(当前协程)：自重排回所属 lane（lane 从
            // cohandle 槽读，§19.1 只保证重新经过一次调度决策）
            var cur = fresh("$mw.yield.cur.", I64);
            var disp = fresh("$mw.disp.", MirType.Of(DispatcherCanonical));
            insts.Add(new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur));
            insts.Add(new MirCall(syms.DispatcherGet, new List<MirOperand>(), disp));
            insts.Add(new MirCall(syms.Publish,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(cur) }, null));
            resumeFn.AddBlock(new MirBlock(yieldId, insts,
                resumeRet(ResumeYielded, insts)));
        }

        // 带 Alarm 的 yield 改写（棒5a）：head → 挂起段（存活跃槽 +
        // state=N + MirTypeCheck 分流）：
        //   PollingAlarm：poll_arm（退避复位）+ 自重排 + ret SUSPENDED
        //    ——恢复块（pollgate）先探测，未就绪 poll_schedule 再挂；
        //   EventAlarm：rigi_alarm_wait 闸内登记（§19.3 原子握手）——
        //    未触发 ret SUSPENDED 等响铃；已触发自重排（执行段仍结束，
        //    对齐 VM「已触发仍 Publish」口径）。
        private void EmitYieldAlarmSplit(MwContext context, MirFunction resumeFn,
            SuspensionPoint point, MirYieldAlarm yieldAlarm,
            string headId, List<MirInst> headInsts, MirLocalOperand frameOp,
            string stateFieldSymbol, string taskFieldSymbol, string taskTypeRef,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, List<MirInst>, MirTerminator> resumeRet,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh)
        {
            var yieldId = point.Block.Id + ".yield" + point.State;
            var pollId = yieldId + ".poll";
            var eventId = yieldId + ".event";
            var signaledId = yieldId + ".signaled";
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(yieldId)));

            var insts = new List<MirInst>();
            emitSave(insts, point.LiveAfter);
            var stateConst = ProxyWildcardAbi.FreshLocal(resumeFn, "$mw.state.c.", I32);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, point.State), stateConst));
            insts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            // 运行时分类：PollingAlarm sheet 物化 + type.is.indirect
            var tid = fresh("$mw.tid.poll.", MirType.Of(".typeid"));
            var isPoll = fresh("$mw.ispoll.", Bool);
            insts.Add(new MirGetTypeId(PollingAlarmCanonical, tid));
            insts.Add(new MirTypeCheck(MirTypeCheckKind.Is,
                new MirLocalOperand(yieldAlarm.AlarmSlot), null,
                new MirLocalOperand(tid), isPoll));
            resumeFn.AddBlock(new MirBlock(yieldId, insts,
                new MirCondBranch(new MirLocalOperand(isPoll), pollId, eventId)));

            // PollingAlarm：arm + 自重排（VM YieldAlarm polling 段
            //  Publish 同口径；探测在恢复块进行）
            var curP = fresh("$mw.yield.cur.", I64);
            var dispP = fresh("$mw.disp.", MirType.Of(DispatcherCanonical));
            var pollInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curP),
                new MirCall(syms.PollArm,
                    new List<MirOperand> { new MirLocalOperand(curP) }, null),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispP),
                new MirCall(syms.Publish,
                    new List<MirOperand> { new MirLocalOperand(dispP),
                        new MirLocalOperand(curP) }, null),
            };
            resumeFn.AddBlock(new MirBlock(pollId, pollInsts,
                resumeRet(ResumeSuspended, pollInsts)));

            // EventAlarm：闸内登记 waiter；已触发（粘滞）→ 自重排
            var ah = fresh("$mw.yield.ah.", I64);
            var curE = fresh("$mw.yield.cur.", I64);
            var rc = fresh("$mw.yield.rc.", I32);
            var rcZero = fresh("$mw.yield.rcz.", I32);
            var notRegistered = fresh("$mw.yield.sig.", Bool);
            var eventInsts = new List<MirInst>
            {
                new MirGetField(new MirLocalOperand(yieldAlarm.AlarmSlot),
                    EventAlarmHandleField, ah),
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curE),
                new MirCall(syms.AlarmWait,
                    new List<MirOperand> { new MirLocalOperand(ah),
                        new MirLocalOperand(curE) }, rc),
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 0), rcZero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpEq, new MirLocalOperand(rc),
                    new MirLocalOperand(rcZero), I32, I32, Bool, notRegistered),
            };
            resumeFn.AddBlock(new MirBlock(eventId, eventInsts,
                new MirCondBranch(new MirLocalOperand(notRegistered), signaledId,
                    yieldId + ".registered")));

            // 已触发：自重排后结束执行段（§19.4 末条：带 Alarm 的 yield
            // 恒结束当前执行段）
            var curS = fresh("$mw.yield.cur.", I64);
            var dispS = fresh("$mw.disp.", MirType.Of(DispatcherCanonical));
            var signaledInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curS),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispS),
                new MirCall(syms.Publish,
                    new List<MirOperand> { new MirLocalOperand(dispS),
                        new MirLocalOperand(curS) }, null),
            };
            resumeFn.AddBlock(new MirBlock(signaledId, signaledInsts,
                resumeRet(ResumeSuspended, signaledInsts)));

            // 已登记：等响铃，结束执行段
            var registeredInsts = new List<MirInst>();
            EmitTaskMark(context, registeredInsts, frameOp, taskFieldSymbol,
                taskTypeRef, "markSuspended", fresh);
            resumeFn.AddBlock(new MirBlock(yieldId + ".registered",
                registeredInsts, resumeRet(ResumeSuspended, registeredInsts)));
        }

        // PollingAlarm 恢复闸（yield-alarm 的 state N 恢复块落点）：
        // poll_pending 判位——EventAlarm 响铃恢复（pending=0）直续原
        // 后继；PollingAlarm 重发布恢复（pending=1）先经 $mw.poll_probe
        // 探测：ready → poll_clear + 续行；not → poll_schedule（退避
        // 重排程）+ ret SUSPENDED（frame 未变，state 保持 N）；异常
        //（-1，pending 已置位）→ 失败终态序列（Task FAILED，await 点
        // 重抛——对齐 VM ProbePolling 的 yield 点失败口径）
        private void EmitPollGate(MwContext context, MirModule mir, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, MirLocalOperand frameOp,
            string taskFieldSymbol, string taskTypeRef, RuntimeSyms syms)
        {
            var gateId = PollGateBlockId(point);
            var probeId = gateId + ".probe";
            var readyId = gateId + ".ready";
            var waitId = gateId + ".wait";
            var failId = gateId + ".fail";
            var alarmSlot = ((MirYieldAlarm)point.Inst).AlarmSlot;

            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);

            var cur = Fresh("$mw.poll.cur.", I64);
            var pend = Fresh("$mw.poll.pend.", I32);
            var pendOne = Fresh("$mw.poll.p1.", I32);
            var isPending = Fresh("$mw.poll.isp.", Bool);
            resumeFn.AddBlock(new MirBlock(gateId, new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur),
                new MirCall(syms.PollPending,
                    new List<MirOperand> { new MirLocalOperand(cur) }, pend),
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 0), pendOne),
                new MirBinaryIntrinsic(BilBinaryOp.CmpNe, new MirLocalOperand(pend),
                    new MirLocalOperand(pendOne), I32, I32, Bool, isPending),
            }, new MirCondBranch(new MirLocalOperand(isPending), probeId,
                ContBlockId(point))));

            var pr = Fresh("$mw.poll.pr.", I32);
            var probeTable = new BilSwitchTableResource(
                "$mw.coroutine.probe." + _resourceCounter++, ".i32",
                new[] { "-1", "0", "1" });
            context.Module.Resources.Add(probeTable);
            resumeFn.AddBlock(new MirBlock(probeId, new List<MirInst>
            {
                new MirCall(EnsurePollProbe(context, mir),
                    new List<MirOperand> { new MirLocalOperand(alarmSlot) }, pr),
            }, new MirSwitch(new MirLocalOperand(pr), probeTable,
                new[] { failId, waitId, readyId }, "mw.state.bad")));

            // ready：解除轮询状态 → 原后继续行
            var curR = Fresh("$mw.poll.cur.", I64);
            resumeFn.AddBlock(new MirBlock(readyId, new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curR),
                new MirCall(syms.PollClear,
                    new List<MirOperand> { new MirLocalOperand(curR) }, null),
            }, new MirBranch(ContBlockId(point))));

            // not ready：退避重排程（VM SchedulePoll 同口径）→ 挂起
            var curW = Fresh("$mw.poll.cur.", I64);
            var waitInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curW),
                new MirCall(syms.PollSchedule,
                    new List<MirOperand> { new MirLocalOperand(curW) }, null),
            };
            EmitTaskMark(context, waitInsts, frameOp, taskFieldSymbol,
                taskTypeRef, "markSuspended", Fresh);
            var waitCode = Fresh("$mw.code.", I32);
            waitInsts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, ResumeSuspended), waitCode));
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                new MirRet(new MirLocalOperand(waitCode))));

            // 探测异常（pending 已置位）：失败终态序列（与 RcInjection
            // resume 垫尾同构——统一走 EmitFailTerminal）
            var failInsts = new List<MirInst>();
            var failRet = EmitFailTerminal(context, mir, resumeFn, failInsts,
                taskFieldSymbol, taskTypeRef, syms);
            resumeFn.AddBlock(new MirBlock(failId, failInsts, failRet));
        }

        // 失败终态序列（DONE 垫尾/探测失败尾共用，棒5a；对齐 VM
        // OnTerminal 的 fail 通道）：take pending（.any，+1 move）→
        // native 失败注册表登记（+1 拷贝持有，节点 id）→ 写
        // task.failureNodeId → fail()（终态迁移 + waiter 排空）→
        // publishAll → noteTerminal(failed=true) → MirCoroutineDone →
        // ret DONE。RcInjection 追加托管槽 release + frame 最终 release
        //（DONE 出口）。padManagedOut 非空时收集本序列新建的托管局部
        //（RcInjection 传播垫场景：垫在 releaseOrder 冻结后合成，自带
        // 局部须显式释放）；正常 split 期调用传 null（局部在
        // RcInjection 分类前已入 fn.Locals，统一配平）。返回终结符
        internal static MirTerminator EmitFailTerminal(MwContext context, MirModule mir,
            MirFunction fn, List<MirInst> insts, string taskFieldSymbol,
            string taskTypeRef, RuntimeSyms syms, List<string>? padManagedOut = null)
        {
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(fn, prefix, type);
            string FreshManaged(string prefix, MirType type)
            {
                var name = Fresh(prefix, type);
                padManagedOut?.Add(name);
                return name;
            }
            var exc = FreshManaged("$mw.exc.", Any);
            insts.Add(new MirTakePending(exc));
            var nodeId = Fresh("$mw.nid.", I64);
            insts.Add(new MirCall(syms.FailureRecord,
                new List<MirOperand> { new MirLocalOperand(exc) }, nodeId));
            var task = FreshManaged("$mw.task.", MirType.Of(
                FieldTypeOfTaskField(taskFieldSymbol)));
            insts.Add(new MirGetField(new MirLocalOperand(FrameParamName),
                taskFieldSymbol, task));
            insts.Add(new MirSetField(new MirLocalOperand(nodeId),
                new MirLocalOperand(task),
                TaskField(taskTypeRef, "failureNodeId", ".i64")));
            EmitTerminalPublish(context, fn, insts, task, taskTypeRef,
                failed: true, syms, Fresh, FreshManaged);
            return ResumeDoneTerminator(context, fn, insts, Fresh);
        }

        // frame.task 字段符号的槽类型段（$mw.task@ 之后）
        private static string FieldTypeOfTaskField(string taskFieldSymbol)
        {
            var at = taskFieldSymbol.LastIndexOf('@');
            return taskFieldSymbol.Substring(at + 1);
        }

        // TaskState 投影（§18.2）：挂起点 markSuspended / 恢复点
        // markRunnable。frame.$mw.task 即当前协程的 Task 对象
        private static void EmitTaskMark(MwContext context,
            List<MirInst> insts, MirLocalOperand frameOp, string taskFieldSymbol,
            string taskTypeRef, string name, System.Func<string, MirType, string> fresh)
        {
            var task = fresh("$mw.ts.", MirType.Of(FieldTypeOfTaskField(taskFieldSymbol)));
            insts.Add(new MirGetField(frameOp, taskFieldSymbol, task));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, name),
                new List<MirOperand> { new MirLocalOperand(task) }, null));
        }

        private static void EmitRestoreMark(MwContext context,
            List<MirInst> insts, MirLocalOperand frameOp, string taskFieldSymbol,
            string taskTypeRef, System.Func<string, MirType, string> fresh) =>
            EmitTaskMark(context, insts, frameOp, taskFieldSymbol,
                taskTypeRef, "markRunnable", fresh);

        // 终态共用尾段：complete/fail → publishAll → noteTerminal
        private static void EmitTerminalPublish(MwContext context, MirFunction fn,
            List<MirInst> insts, string task, string taskTypeRef, bool failed,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh,
            System.Func<string, MirType, string> freshManaged)
        {
            var drained = freshManaged("$mw.drained.", MirType.Of(".array<.i64>"));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, failed ? "fail" : "complete"),
                new List<MirOperand> { new MirLocalOperand(task) }, drained));
            var disp = freshManaged("$mw.disp.", MirType.Of(DispatcherCanonical));
            insts.Add(new MirCall(syms.DispatcherGet, new List<MirOperand>(), disp));
            insts.Add(new MirCall(syms.PublishAll,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(drained) }, null));
            var cur = fresh("$mw.done.cur.", I64);
            var failedConst = fresh("$mw.done.fl.", Bool);
            insts.Add(new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), cur));
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddBoolResource(context, failed), failedConst));
            insts.Add(new MirCall(syms.NoteTerminal,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(cur), new MirLocalOperand(failedConst) }, null));
            insts.Add(new MirCoroutineDone());
        }

        private static MirTerminator ResumeDoneTerminator(MwContext context, MirFunction fn,
            List<MirInst> insts, System.Func<string, MirType, string> fresh)
        {
            var doneCode = fresh("$mw.code.", I32);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, ResumeDone), doneCode));
            return new MirRet(new MirLocalOperand(doneCode));
        }

        // $mw.poll_probe 模块级合成（懒建一次，$mw.named.lookup 先例）：
        // 普通 MIR fn（alarm 胖引用 → i32）：isReady 虚派发 + bool 双
        // 分支转 i32（1 ready / 0 not）；isReady 抛异常走 ExcTarget 进
        // 函数级传播垫，RcInjection 第二垫尾分叉（IsPollProbe 标记，仿
        // IsCoroutineResume）= release 配平 + ret -1 + pending 保持置位
        //（恢复块 poll 失败尾取走走 yield 点失败路径）。参数借用约定
        //（frame 恢复的 alarm 槽持有 +1，probe 不动计数）
        private static MwMemberSymbol EnsurePollProbe(MwContext context, MirModule mir)
        {
            foreach (var existing in mir.Functions)
            {
                if (existing.Symbol.Canonical == PollProbeCanonical)
                {
                    return existing.Symbol;
                }
            }
            var isReady = context.Symbols.FindMember(PollProbeIsReadyCanonical)
                ?? throw new CompilerInternalException(
                    "stdlib PollingAlarm.isReady 符号缺失: " + PollProbeIsReadyCanonical);
            var symbol = ProxyBakeSupport.SyntheticMember(PollProbeCanonical, owner: null);
            var alarmParam = new MirLocal(ProbeParamName, MirType.Of(PollingAlarmCanonical));
            var fn = new MirFunction(symbol, I32, new List<MirLocal> { alarmParam },
                new List<MirLocal> { alarmParam }, new List<MirBlock>(), false,
                isPollProbe: true);
            var ready = ProxyWildcardAbi.FreshLocal(fn, "$mw.pp.rdy.", MirType.Of(".bool"));
            fn.AddBlock(new MirBlock("entry", new List<MirInst>
            {
                new MirCall(isReady,
                    new List<MirOperand> { new MirLocalOperand(ProbeParamName) }, ready),
            }, new MirCondBranch(new MirLocalOperand(ready), "mw.pp.ready", "mw.pp.wait")));
            var one = ProxyWildcardAbi.FreshLocal(fn, "$mw.pp.one.", I32);
            fn.AddBlock(new MirBlock("mw.pp.ready", new List<MirInst>
            {
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 1), one),
            }, new MirRet(new MirLocalOperand(one))));
            var zero = ProxyWildcardAbi.FreshLocal(fn, "$mw.pp.zero.", I32);
            fn.AddBlock(new MirBlock("mw.pp.wait", new List<MirInst>
            {
                new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 0), zero),
            }, new MirRet(new MirLocalOperand(zero))));
            mir.AddFunction(fn);
            return symbol;
        }

        // DONE 尾（棒5a；resume fn 的 MirRet 出口改写，对齐 VM
        // OnTerminal 的 complete 通道）：Task<T> 先把结果写 result
        // 字段（nullable 包装——终态保存与 waiter 读取以 Task.gate
        // 临界区建立 happens-before，§18.3）→ complete() → publishAll
        // → noteTerminal → MirCoroutineDone → ret DONE
        private void RewriteReturn(MwContext context, MirModule mir, MirFunction resumeFn,
            MirBlock block, MirFunction original, MirLocalOperand frameOp,
            string taskFieldSymbol, string taskTypeRef, RuntimeSyms syms)
        {
            if (block.Terminator is not MirRet ret)
            {
                return;
            }
            var insts = block.InstructionList;
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);
            var task = Fresh("$mw.task.", MirType.Of(FieldTypeOfTaskField(taskFieldSymbol)));
            insts.Add(new MirGetField(frameOp, taskFieldSymbol, task));
            if (ret.Value != null && !original.ReturnType.IsVoid)
            {
                // 终态结果写 Task<T>.result（nullable<T> 包装）
                var rn = Fresh("$mw.done.rn.",
                    MirType.Of(".nullable<" + original.ReturnType.Canonical + ">"));
                insts.Add(new MirWrapNullable(ret.Value, original.ReturnType, rn));
                insts.Add(new MirSetField(new MirLocalOperand(rn),
                    new MirLocalOperand(task),
                    TaskField(taskTypeRef, "result",
                        ".nullable<" + original.ReturnType.Canonical + ">")));
            }
            EmitTerminalPublish(context, resumeFn, insts, task, taskTypeRef,
                failed: false, syms, Fresh, Fresh);
            block.Terminator = ResumeDoneTerminator(context, resumeFn, insts, Fresh);
        }

        // ===== ③ spawn stub 改写（棒5a 新交互点，对齐 VM
        // VmDispatch.Spawn 序：建 Task + attachRuntime → noteSpawn →
        // publish；lane = 继承调用方（laneOfCurrent——协程只在绑定
        // Executor 的 Worker 上运行，§17.1））=====

        private void ReplaceWithStub(MwContext context, MirModule mir, MirFunction fn,
            MwTypeSymbol frameType, MirType frameMirType, MwMemberSymbol initSymbol,
            MwMemberSymbol resumeSymbol, string taskTypeRef,
            string taskFieldSymbol, System.Func<MirLocal, string> fieldOf)
        {
            var syms = Syms(context, mir);
            var taskType = MirType.Of(taskTypeRef);
            var stubLocals = new List<MirLocal>(fn.Parameters);
            var stub = new MirFunction(fn.Symbol, taskType, fn.Parameters, stubLocals,
                new List<MirBlock>(), fn.IsEntrypoint, isAsync: true);
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(stub, prefix, type);
            var frame = Fresh("$mw.frame.", frameMirType);
            var outTask = Fresh("$mw.task.", taskType);

            var insts = new List<MirInst>
            {
                // frame = new 合成类型（合成空 init；字段随后逐槽落）
                new MirNewObject(frameType, null, initSymbol,
                    new List<MirOperand>(), frame),
            };
            // 参数/类级 typeid 落 frame 字段（stub 是原实例方法，Emit
            // prologue 正常——类级 .generic.* 局部由 prologue 从 .this 装入）
            foreach (var parameter in fn.Parameters)
            {
                insts.Add(new MirSetField(new MirLocalOperand(parameter.Name),
                    new MirLocalOperand(frame), fieldOf(parameter)));
            }
            var frameOp = new MirLocalOperand(frame);
            // Task 对象：合成空 init（无用户 init——gate/body 冷通道用不
            // 到，attachRuntime 懒建 gate 兜底；对齐 VM AllocateObject）
            // NewObject 用原 taskTypeRef（占位构造 Task<.generic<T>>），
            // 不得用声明形/裸模板——后者 TypeSheetFor 命中 void Task
            insts.Add(new MirNewObject(TaskTypeSymbolOf(context, taskTypeRef),
                null, TaskEmptyInit(context, mir, taskTypeRef),
                new List<MirOperand>(), outTask));
            // frame.task = task（DONE 尾/恢复块取回自身 Task 的通道）
            insts.Add(new MirSetField(new MirLocalOperand(outTask), frameOp,
                taskFieldSymbol));
            // 协程句柄创建（frame +1 move 进续体——RcInjection 免配平）
            var handle = Fresh("$mw.handle.", I64);
            insts.Add(new MirCoroutineCreate(frame, resumeSymbol, handle));
            // CoroutineLocal：从启动方当前协程拷有效顶（eager spawn）
            insts.Add(new MirCall(syms.CoroLocalInherit,
                new List<MirOperand> { new MirLocalOperand(handle) }, null));
            // lane 继承（§18.1 第 3 步）→ attachRuntime → noteSpawn → publish
            var disp = Fresh("$mw.disp.", MirType.Of(DispatcherCanonical));
            var lane = Fresh("$mw.lane.", I32);
            insts.Add(new MirCall(syms.DispatcherGet, new List<MirOperand>(), disp));
            insts.Add(new MirCall(syms.LaneOfCurrent,
                new List<MirOperand> { new MirLocalOperand(disp) }, lane));
            insts.Add(new MirCall(syms.CoroutineSetLane,
                new List<MirOperand> { new MirLocalOperand(handle),
                    new MirLocalOperand(lane) }, null));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, "attachRuntime"),
                new List<MirOperand> { new MirLocalOperand(outTask),
                    new MirLocalOperand(handle) }, null));
            insts.Add(new MirCall(syms.NoteSpawn,
                new List<MirOperand> { new MirLocalOperand(disp) }, null));
            insts.Add(new MirCall(syms.Publish,
                new List<MirOperand> { new MirLocalOperand(disp),
                    new MirLocalOperand(handle) }, null));
            stub.AddBlock(new MirBlock("entry", insts,
                new MirRet(new MirLocalOperand(outTask))));

            var index = mir.FunctionList.IndexOf(fn);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "CoroutineSplit：原 fn 不在模块函数表: " + fn.Symbol.Canonical);
            }
            mir.FunctionList[index] = stub;
        }

        // ===== ⑤ 冷 Task 构造重写（§18.4；棒5a）=====
        // `new Task(body)` / `new Task<T>(body)`（body 静态类型 = 具体
        // 闭包类）→ $mw.coldtask.* 工厂调用：工厂预建 body $$call 的
        // frame（.this = body）+ cohandle 存 coldHandle + gate 构造即建
        //（对齐真实 init 的 gate 前置），spawn-into 由 Task.startCold/
        // spawnIntoLocked（Rigi 体）在启动时复用 coldHandle。
        // 构造不拷 CoroutineLocal（冷构造 ≠ 启动；继承在 spawnIntoLocked）。
        // body 静态类型不透明（AsyncAction/AsyncFunc 接口形态）跳过工厂，
        // 保留真实 init（coldHandle 保持 0），启动时 bindColdBody 动态绑定。
        private void RewriteColdTaskConstructions(MwContext context, MirModule mir)
        {
            foreach (var fn in mir.Functions.ToList())
            {
                foreach (var block in fn.Blocks)
                {
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        if (insts[i] is not MirNewObject newObject
                            || newObject.Args.Count != 1
                            || !IsRealTaskInit(newObject.Init))
                        {
                            continue;
                        }
                        var bodySlot = ((MirLocalOperand)newObject.Args[0]).Name;
                        var bodyType = ConcreteColdBodyType(context, fn, bodySlot);
                        if (IsOpaqueCallableType(bodyType))
                        {
                            continue;
                        }
                        var factory = EnsureColdTaskFactory(context, mir,
                            newObject.Type, bodyType);
                        insts[i] = new MirCall(factory, newObject.Args,
                            newObject.Target, newObject.ExcTarget);
                    }
                }
            }
        }

        // 冷 Task body 的静态槽常是 AsyncFunc<TReturn> 占位（init 形参），
        // 工厂要的是具体 lambda 类（才能找 $$call）。优先取产出该槽的
        // MirNewObject 类型；否则回退槽上声明（已是具体类时）
        private static MirType ConcreteColdBodyType(MwContext context, MirFunction fn,
            string bodySlot)
        {
            var slot = bodySlot;
            for (var hop = 0; hop < 8; hop++)
            {
                string? copiedFrom = null;
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is MirNewObject produced && produced.Target == slot)
                        {
                            return MirType.Of(produced.Type.Canonical);
                        }
                        if (inst is MirCast cast && cast.Target == slot
                            && cast.Source is MirLocalOperand fromCast)
                        {
                            copiedFrom = fromCast.Name;
                        }
                        if (inst is MirCopyLocal copy && copy.Target == slot
                            && copy.Source is MirLocalOperand fromCopy)
                        {
                            copiedFrom = fromCopy.Name;
                        }
                    }
                }
                if (copiedFrom == null)
                {
                    break;
                }
                slot = copiedFrom;
            }
            var declared = fn.FindLocal(bodySlot).Type;
            if (IsOpaqueCallableType(declared)
                || context.Symbols.FindType(declared.Canonical) != null)
            {
                return declared;
            }
            throw new MwNotSupportedException(
                "冷 Task 的 body 静态类型不透明（native 半场暂不支持）: "
                + declared.Canonical);
        }

        // AsyncAction / AsyncFunc 接口形态：构造点看不到唯一闭包类
        private static bool IsOpaqueCallableType(MirType type)
        {
            var canonical = type.Canonical;
            return canonical == "core::AsyncAction"
                || canonical.StartsWith("core::AsyncAction<",
                    System.StringComparison.Ordinal)
                || canonical.StartsWith("core::AsyncFunc<",
                    System.StringComparison.Ordinal);
        }

        // 0 参 async $$call（lambda / AsyncAction 子类）：可作为冷 Task body
        private static bool IsZeroArgAsyncCall(MirFunction fn)
        {
            if (fn.Symbol.Owner == null
                || !fn.Symbol.Canonical.Contains("$$call(",
                    System.StringComparison.Ordinal))
            {
                return false;
            }
            var hasThis = false;
            foreach (var parameter in fn.Parameters)
            {
                if (parameter.Name == ".this")
                {
                    hasThis = true;
                    continue;
                }
                if (parameter.Name.StartsWith(".generic.",
                    System.StringComparison.Ordinal))
                {
                    continue;
                }
                return false;
            }
            return hasThis;
        }

        private static bool IsVoidTaskType(string taskTypeRef) =>
            !taskTypeRef.StartsWith("core.coroutine::Task<",
                System.StringComparison.Ordinal);

        private sealed class ColdBindEntry
        {
            internal string OwnerCanonical = "";
            internal string CallCanonical = "";
            internal MwMemberSymbol ResumeSymbol = null!;
            internal string FrameCanonical = "";
            internal MwTypeSymbol FrameType = null!;
            internal MwMemberSymbol FrameInit = null!;
            internal string TaskTypeRef = "";
            internal string TaskConstructionRef = "";
            internal MirType ThisType = null!;
        }

        private readonly List<ColdBindEntry> _coldBinds = new();
        private readonly Dictionary<string, MwMemberSymbol> _bindColdHelpers = new(
            System.StringComparer.Ordinal);

        // Task.bindColdBody / Task<TReturn>.bindColdBody：按模块内已
        // split 的 0 参 async $$call 做 type.is 链，命中则调 $mw.bindcold.*
        // 建 frame + 句柄写入 coldHandle；全不中抛 IllegalStateException
        private void RewriteBindColdBodies(MwContext context, MirModule mir)
        {
            RewriteBindColdBody(context, mir, "core.coroutine::Task", voidTask: true);
            RewriteBindColdBody(context, mir, "core.coroutine::Task<TReturn>",
                voidTask: false);
        }

        private void RewriteBindColdBody(MwContext context, MirModule mir,
            string taskDecl, bool voidTask)
        {
            var prefix = taskDecl + "$bindColdBody(";
            var original = mir.Functions.FirstOrDefault(f =>
                f.Symbol.Canonical.StartsWith(prefix, System.StringComparison.Ordinal));
            if (original == null)
            {
                return;
            }
            var candidates = _coldBinds.Where(e => IsVoidTaskType(e.TaskTypeRef) == voidTask)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }
            var thisType = original.Parameters[0].Type;
            var rewritten = new MirFunction(original.Symbol, original.ReturnType,
                original.Parameters, new List<MirLocal>(original.Parameters),
                new List<MirBlock>(), original.IsEntrypoint);
            string Fresh(string namePrefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(rewritten, namePrefix, type);
            var bodyType = TaskBodyMirTypeOf(context, taskDecl);
            var body = Fresh("$mw.bind.body.", bodyType);
            var thisOp = new MirLocalOperand(".this");
            var entryInsts = new List<MirInst>
            {
                new MirGetField(thisOp, TaskBodyFieldOf(context, taskDecl), body),
            };
            var blocks = new List<MirBlock>();
            var bodyOp = new MirLocalOperand(body);
            for (var k = 0; k < candidates.Count; k++)
            {
                var candidate = candidates[k];
                var checkId = k == 0 ? "entry" : "mw.bc.chk." + k;
                var hitId = "mw.bc.hit." + k;
                var nextId = k + 1 < candidates.Count
                    ? "mw.bc.chk." + (k + 1)
                    : "mw.bc.miss";
                var checkInsts = k == 0 ? entryInsts : new List<MirInst>();
                var cond = Fresh("$mw.bc.is.", Bool);
                checkInsts.Add(new MirTypeCheck(MirTypeCheckKind.Is, bodyOp,
                    candidate.OwnerCanonical, null, cond));
                blocks.Add(new MirBlock(checkId, checkInsts,
                    new MirCondBranch(new MirLocalOperand(cond), hitId, nextId)));

                var hitInsts = new List<MirInst>();
                var typed = Fresh("$mw.bc.t.", candidate.ThisType);
                hitInsts.Add(new MirCopyLocal(bodyOp, typed));
                var helper = EnsureBindColdHelper(context, mir, candidate, thisType);
                var handle = Fresh("$mw.bc.h.", I64);
                hitInsts.Add(new MirCall(helper,
                    new List<MirOperand> { thisOp, new MirLocalOperand(typed) },
                    handle));
                hitInsts.Add(new MirSetField(new MirLocalOperand(handle), thisOp,
                    TaskField(taskDecl, "coldHandle", ".i64")));
                blocks.Add(new MirBlock(hitId, hitInsts, new MirRet(null)));
            }

            var missInsts = new List<MirInst>();
            EmitThrowIllegalState(context, rewritten, missInsts,
                "冷 Task body 无法 spawn-into：无匹配闭包");
            blocks.Add(new MirBlock("mw.bc.miss", missInsts, new MirRetThrow()));
            foreach (var block in blocks)
            {
                rewritten.AddBlock(block);
            }
            var index = mir.FunctionList.IndexOf(original);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "bindColdBody 不在模块函数表: " + original.Symbol.Canonical);
            }
            mir.FunctionList[index] = rewritten;
        }

        private static MirType TaskBodyMirTypeOf(MwContext context, string taskTypeRef)
        {
            var field = TaskBodyFieldOf(context, taskTypeRef);
            var at = field.LastIndexOf('@');
            return MirType.Of(at < 0 ? field : field.Substring(at + 1));
        }

        private MwMemberSymbol EnsureBindColdHelper(MwContext context, MirModule mir,
            ColdBindEntry entry, MirType taskThisType)
        {
            var key = entry.TaskTypeRef + "|" + entry.OwnerCanonical;
            if (_bindColdHelpers.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var factoryCanonical = "$mw.bindcold." + key + "()@.i64";
            var symbol = ProxyBakeSupport.SyntheticMember(factoryCanonical, owner: null);
            var taskParam = new MirLocal("task", taskThisType);
            var bodyParam = new MirLocal("body", entry.ThisType);
            var helper = new MirFunction(symbol, I64,
                new List<MirLocal> { taskParam, bodyParam },
                new List<MirLocal> { taskParam, bodyParam },
                new List<MirBlock>(), false);
            string Fresh(string namePrefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(helper, namePrefix, type);
            var frame = Fresh("$mw.frame.", MirType.Of(entry.FrameCanonical));
            var handle = Fresh("$mw.handle.", I64);
            var insts = new List<MirInst>
            {
                new MirNewObject(entry.FrameType, null, entry.FrameInit,
                    new List<MirOperand>(), frame),
                new MirSetField(new MirLocalOperand("body"), new MirLocalOperand(frame),
                    SyntheticTypePlanner.FrameFieldSymbol(entry.FrameCanonical, ".this",
                        entry.ThisType.Canonical)),
                new MirSetField(new MirLocalOperand("task"), new MirLocalOperand(frame),
                    TaskFieldSymbolOf(entry.FrameCanonical, entry.TaskConstructionRef)),
                new MirCoroutineCreate(frame, entry.ResumeSymbol, handle),
            };
            helper.AddBlock(new MirBlock("entry", insts,
                new MirRet(new MirLocalOperand(handle))));
            mir.AddFunction(helper);
            _bindColdHelpers.Add(key, symbol);
            return symbol;
        }

        private static void EmitThrowIllegalState(MwContext context, MirFunction fn,
            List<MirInst> insts, string message)
        {
            var msg = ProxyWildcardAbi.FreshLocal(fn, "$mw.bc.msg.",
                ProxyWildcardAbi.StringType);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddStringResource(context, message), msg));
            var excType = context.Symbols.FindTypeByRef("core::IllegalStateException")
                ?? throw new CompilerInternalException("core::IllegalStateException 类型缺失");
            MwMemberSymbol? init = null;
            foreach (var member in excType.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count == 1
                    && signature.Parameters[0].Name == "text")
                {
                    init = member;
                    break;
                }
            }
            if (init == null)
            {
                throw new CompilerInternalException(
                    "core::IllegalStateException 缺 init(text: String)");
            }
            var initWrapper = context.Symbols.FindMember(
                excType.Canonical + "$..init.wrapper()@.void");
            var exc = ProxyWildcardAbi.FreshLocal(fn, "$mw.bc.exc.",
                MirType.Of(excType.Canonical));
            insts.Add(new MirNewObject(excType, initWrapper, init,
                new List<MirOperand> { new MirLocalOperand(msg) }, exc));
            insts.Add(new MirThrow(new MirLocalOperand(exc), null));
        }

        // 真实 Task init 判定（排除本 pass 合成的空 init）
        private bool IsRealTaskInit(MwMemberSymbol init)
        {
            if (_taskEmptyInits.Values.Any(s => s.Canonical == init.Canonical))
            {
                return false;
            }
            var owner = init.Owner?.Canonical;
            return (owner == "core.coroutine::Task"
                || owner == "core.coroutine::Task<TReturn>")
                && init.HasKeyword(BilKeyword.Init);
        }

        private readonly Dictionary<string, MwMemberSymbol> _coldTaskFactories = new(
            System.StringComparer.Ordinal);

        private MwMemberSymbol EnsureColdTaskFactory(MwContext context, MirModule mir,
            MwTypeSymbol taskType, MirType bodyType)
        {
            var taskTypeRef = taskType.Canonical;
            var key = taskTypeRef + "|" + bodyType.Canonical;
            if (_coldTaskFactories.TryGetValue(key, out var cached))
            {
                return cached;
            }
            // 闭包的 async $$call → split 产物（frame 类型 + resume fn）
            var closureType = context.Symbols.FindType(bodyType.Canonical)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 静态类型不透明（native 半场暂不支持）: "
                    + bodyType.Canonical);
            var call = closureType.Members.FirstOrDefault(m =>
                m.Canonical.Contains("$$call(", System.StringComparison.Ordinal))
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 缺少 $$call 实现: " + bodyType.Canonical);
            var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(call.Canonical);
            var frameType = context.Symbols.FindType(frameCanonical)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 不是 async 闭包（无 split frame）: "
                    + call.Canonical);
            var resumeSymbol = ProxyBakeSupport.SyntheticMember(
                "$mw.resume." + call.Canonical, owner: null);
            var frameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(frameCanonical))
                ?? throw new CompilerInternalException("frame 空 init 缺失: " + frameCanonical);
            var factoryCanonical = "$mw.coldtask." + key + "()@" + taskTypeRef;
            var symbol = ProxyBakeSupport.SyntheticMember(factoryCanonical, owner: null);
            var bodyParam = new MirLocal("body", bodyType);
            var factory = new MirFunction(symbol, MirType.Of(taskTypeRef),
                new List<MirLocal> { bodyParam }, new List<MirLocal> { bodyParam },
                new List<MirBlock>(), false);
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(factory, prefix, type);
            var frame = Fresh("$mw.frame.", MirType.Of(frameCanonical));
            var task = Fresh("$mw.task.", MirType.Of(taskTypeRef));
            var gate = Fresh("$mw.gate.", I64);
            var handle = Fresh("$mw.handle.", I64);
            var bodyOp = new MirLocalOperand("body");
            var smutexCreate = FindCoroutineGlobal(context, "rigi_sync_mutex_create(")
                ?? throw new CompilerInternalException("stdlib 缺 rigi_sync_mutex_create");
            var bodyField = TaskBodyFieldOf(context, taskTypeRef);
            var taskConstructionRef = TypeLayout.IsGenericPlaceholder(
                MirType.Of(taskTypeRef))
                ? TaskPrefixOf(taskTypeRef)
                : taskTypeRef;
            var insts = new List<MirInst>
            {
                // frame = new $$call frame；frame..this = body 闭包
                new MirNewObject(frameType, null, frameInit, new List<MirOperand>(), frame),
                new MirSetField(bodyOp, new MirLocalOperand(frame),
                    SyntheticTypePlanner.FrameFieldSymbol(frameCanonical, ".this",
                        bodyType.Canonical)),
                // Task 对象（合成空 init）+ body/gate/coldHandle 落字段
                new MirNewObject(TaskTypeSymbolOf(context, taskTypeRef),
                    null, TaskEmptyInit(context, mir, taskTypeRef),
                    new List<MirOperand>(), task),
                // resume DONE 经 frame.$mw.task 取回自身 Task（与 eager
                // stub 同通道；缺此字段则 complete 空引用崩）
                new MirSetField(new MirLocalOperand(task), new MirLocalOperand(frame),
                    TaskFieldSymbolOf(frameCanonical, taskConstructionRef)),
                new MirSetField(bodyOp, new MirLocalOperand(task), bodyField),
                new MirCall(smutexCreate, new List<MirOperand>(), gate),
                new MirSetField(new MirLocalOperand(gate), new MirLocalOperand(task),
                    TaskField(taskTypeRef, "gate", ".i64")),
                new MirCoroutineCreate(frame, resumeSymbol, handle),
                new MirSetField(new MirLocalOperand(handle), new MirLocalOperand(task),
                    TaskField(taskTypeRef, "coldHandle", ".i64")),
            };
            factory.AddBlock(new MirBlock("entry", insts,
                new MirRet(new MirLocalOperand(task))));
            mir.AddFunction(factory);
            _coldTaskFactories.Add(key, symbol);
            return symbol;
        }

        // Task.body 字段符号（按简单名查——AsyncAction/AsyncFunc<TReturn>
        // 承载拼写随声明漂移）
        private static string TaskBodyFieldOf(MwContext context, string taskTypeRef)
        {
            var prefix = TaskPrefixOf(taskTypeRef);
            var type = RequireTaskType(context, taskTypeRef);
            foreach (var member in type.Members)
            {
                if (member.Declaration.Kind == BilMemberKind.Field
                    && member.Canonical.Contains("#body@", System.StringComparison.Ordinal))
                {
                    return member.Canonical;
                }
            }
            throw new CompilerInternalException("Task 缺 body 字段: " + prefix);
        }
    }
}
