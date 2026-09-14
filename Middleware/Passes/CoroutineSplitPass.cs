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
    /// SEMANTIC_ARCHITECTURE §7.2，原 ASYNC_LOWERING_DESIGN §3.1）：对每个
    /// IsAsync fn（含无挂起点的 async fn，统一处理）：
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
    /// 类型是具体闭包类）改写为 $mw.coldtask.* 工厂调用——工厂预建
    /// body 的 $$call frame + cohandle 存 coldHandle（构造不继承
    /// CoroutineLocal；继承在 spawnIntoLocked 启动时）。不透明
    /// AsyncAction/AsyncFunc 槽跳过工厂，启动时 bindColdBody 经
    /// type.is 链调 $mw.bindcold.* 动态 spawn-into。
    /// ⑥ B-1 栈式跨界（SYNTAX §11，语义对齐 VM 帧栈模型）：非 async
    ///    fn 的挂起点经 taint 分析沿直调图反向传染调用方；tainted
    ///    普通 fn 状态机化（裸 frame 模式：无 spawn stub/Task 包装，
    ///    frame 带 $mw.result 结果槽，所有权归调用方；resume fn
    ///    IsPlainResume，传播垫尾 release + ret FAILED 沿链上传）；
    ///    tainted→tainted 直调改写为「建 callee frame + 落参 +
    ///    MirResumeCall 下钻 + 四码分流（SUSPENDED/YIELDED 上传 /
    ///    DONE 读 $mw.result 续行 / FAILED 重抛 pending）」，调用点
    ///    本身是调用方的挂起点（state 恢复块重取 callee frame 再下
    ///    钻）。tainted main 走 Task 包装 split（spawn stub 保留原
    ///    符号，ret 热 Task）+ $mw.main.settle 合成，rigi_entry 改
    ///   「调 stub 发布主协程 → drain → settle 取结果/重抛失败」
    ///   （对齐 VM BilVm.Run：main 作为协程发布进 Dispatcher）。
    ///    B-2 全组合收口：传染边全集 = 直调/super/值类型运算符/
    ///    虚·interface·class 运算符派发（闭包内任一实现 tainted
    ///    整点升级）/new init；虚派发挂起点 = 全闭包类臂动态分流
    ///   （最深派生优先 type.is 链，tainted 臂走协议、其余落原调
    ///    用，EmitVirtualCallSplit）；init = 空 init 分配 + init
    ///    frame 下钻（EmitInitSplit）；§7.2 类级 typeid 落参合成
    ///   （PlanArgDrops）；plain fn 内 yield Alarm 放开（EmitPoll-
    ///    Gate plain 失败尾）。R2 残留边界清偿：R2-a 泛型宿主虚
    ///    派发（臂条件 = 模板空壳 + 全部闭合构造 sheet OR 链，类
    ///    级 typeid 落参从接收者实例隐藏字段运行期读取）；R2-b
    ///    $$call/invoke.indirect（callable 闭包同虚派发臂动态分
    ///    流；闭包枚举改布局计划表直查——PlanKey 消同名不同元数
    ///    模板撞键）；R2-c new.indirect × tainted class init（精
    ///    确 sheet 臂：IsTypeId ∧ 派生排除，EmitNewIndirectSplit；
    ///    值类型 init 无挂起协议补闸受控拒绝）；R2-d MirNewObject
    ///    补 try 异常边（构造抛出同 fn 捕获双端对齐）。保留边界：
    ///    proxy/wrapper 烘焙链（$.wrapped./$.mwrapped./$mw.）可达
    ///    的 tainted fn、泛型占位实参 new.indirect × tainted
    ///    init、嵌套占位构造的类级 typeid 实参。
    /// ⑦ 自检：split 后无残留 MirAwait/MirYieldBare/MirYieldAlarm
    ///    与未改写 tainted 调用；每挂起点恰一恢复 state；frame 字段
    ///    与保存槽集合一致。
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
        // EventAlarm 时钟底座句柄字段（VM TryAwaitTimer 读取/懒建回写；
        // L8 起 native yield 分流改经 ensureHandle 调用，本常量保留供
        // VM 镜像拼写与测试钉住）
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
        // B-1：FAILED 只用于 plain resume（tainted 普通 fn）的链式上传
        // ——pending 已置位，调用方调用点 FAILED 臂 MirTakePending 重抛；
        // Task 包装 resume 的失败恒吸收进 Task FAILED（ret DONE），
        // workerLoop 只消费 0/1/2，3 不出现在协程句柄 resume 面
        public const int PlainResumeFailedCode = 3;

        // plain tainted fn frame 的结果字段槽名（调用方 DONE 臂读取）
        public const string ResultSlotName = "$mw.result";

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
                Publish = DispatcherFn("publishNative"),
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

        // EventAlarm 成员通道符号（L8：ensureHandle 懒建默认底座——
        // 用户直继子类 handle==0 时补手动事件粘滞形态，§19.3）
        internal static MwMemberSymbol EventAlarmFn(MwContext context, string name)
        {
            return (context.Symbols.FindType(EventAlarmCanonical)?.Members
                .FirstOrDefault(m => m.Canonical.StartsWith(
                    EventAlarmCanonical + "$" + name + "(",
                    System.StringComparison.Ordinal)))
                ?? throw new CompilerInternalException(
                    "stdlib 缺少 EventAlarm 通道: " + name);
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
            // B-1：taint 分析先行（栈式跨界，SYNTAX §11）——非 async fn
            // 的挂起点沿直调图反向传染调用方；tainted 普通 fn 走裸
            // frame split，tainted main 走 Task 包装 split + 根驱动
            var tainted = TaintAnalysis(context, mir);
            // 快照遍历（split 向模块追加 resume/init 合成 fn）
            var asyncFns = mir.Functions.Where(f => f.IsAsync).ToList();
            var entrypoint = mir.Functions.FirstOrDefault(f => f.IsEntrypoint);
            // 阶段 1：plain tainted fn 的 frame 预注册（挂起点/活性/
            // 保存槽/frame 类型+init）——tainted→tainted 调用点的
            // callee frame 类型先于一切 resume 合成就绪（递归链安全）
            var plainPlans = new List<SplitPlan>();
            foreach (var fn in mir.Functions.ToList())
            {
                if (!fn.IsAsync && !fn.IsEntrypoint
                    && tainted.Contains(fn.Symbol.Canonical))
                {
                    plainPlans.Add(PrepareSplit(context, mir, fn, tainted,
                        SplitMode.Plain));
                }
            }
            // 阶段 2a：async fn split（现状路径；体内的 tainted 直调点
            // 同走调用协议——async 调用方无需传染但调用点必须改写）
            foreach (var fn in asyncFns)
            {
                ExecuteSplit(context, mir,
                    PrepareSplit(context, mir, fn, tainted, SplitMode.Tasked));
            }
            // 阶段 2b：tainted main → Task 包装 split + $mw.main.settle
            //（rigi_entry 根驱动：调 stub 发布主协程 → drain → settle
            // 取结果/重抛失败，对齐 VM BilVm.Run 的 main 协程化）
            if (entrypoint != null && tainted.Contains(entrypoint.Symbol.Canonical))
            {
                ExecuteSplit(context, mir,
                    PrepareSplit(context, mir, entrypoint, tainted, SplitMode.Tasked));
                SynthesizeMainSettle(context, mir, entrypoint);
            }
            // 阶段 2c：plain tainted fn split（原符号改陷阱 stub——
            // 全部调用点已协议化，直调残留属内部错误）
            foreach (var plan in plainPlans)
            {
                ExecuteSplit(context, mir, plan);
            }
            // 冷 Task 构造重写（§18.4；split 之后——需要 resume fn 已合成）
            RewriteColdTaskConstructions(context, mir);
            RewriteBindColdBodies(context, mir);
            // 自检①：split 后无残留 lowering 层协程指令与未改写的
            // tainted 直调（残留 = taint 闭包漏网，属内部错误）。
            // 豁免 .vdflt 默认臂——虚派发挂起点故意保留的原调用
            //（vtable/iMap 动态派发到非 tainted 实现；其静态目标是
            // tainted 声明属正常）
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    if (block.Id.Contains(".vdflt", System.StringComparison.Ordinal))
                    {
                        continue;
                    }
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is MirAwait or MirYieldBare or MirYieldAlarm
                            || IsMutexEnter(inst)
                            || (inst is MirCall residualCall
                                && tainted.Contains(residualCall.Target.Canonical))
                            || (inst is MirSuperCall residualSuper
                                && tainted.Contains(residualSuper.Target.Canonical))
                            || (inst is MirNewObject residualNew
                                && residualNew.Init != null
                                && tainted.Contains(residualNew.Init.Canonical)))
                        {
                            throw new CompilerInternalException(
                                "CoroutineSplit 自检失败：taint 闭包漏网，残留挂起点/未改写 tainted 调用: "
                                + fn.Symbol.Canonical);
                        }
                    }
                }
            }
        }
        // ===== B-1 taint 分析 =====
        // 种子 = 含挂起点（await/yield/Mutex.enter）的非 async fn；沿
        // 调用边反向传染调用方至不动点（链根恒为 main 或 async fn）。
        // B-2 传染边全集：直调 / super（恒直调）/ 值类型宿主运算符
        //（直调形态）/ 虚·interface·class 运算符派发（闭包内任一实
        // 现 tainted 则整点升级——运行期目标静态不可钉死）；new init
        // 经构造点协议收编。R2-b：invoke.indirect（$$call 闭包）同
        // 虚派发口径传染；R2-c：new.indirect 按「tainted class init
        // × 静态实参形精确匹配」传染（值类型 init 一律受控拒绝）。
        private HashSet<string> TaintAnalysis(MwContext context, MirModule mir)
        {
            var tainted = new HashSet<string>(System.StringComparer.Ordinal);
            var byCanonical = new Dictionary<string, MirFunction>(
                System.StringComparer.Ordinal);
            foreach (var fn in mir.Functions)
            {
                byCanonical[fn.Symbol.Canonical] = fn;
            }
            var queue = new Queue<string>();
            // R2-c：new.indirect 保守边（运行期目标由 typeid 决定）
            // 不再一刀切拒绝——class init 由调用点臂协议收编
            //（EmitNewIndirectSplit）；值类型 init 无挂起协议（frame
            // .this 借用形态与 sret/原地构造路径不兼容），凡 tainted
            // 即拒（此前静态 new 形态静默语义错位——native exit 5
            // 无输出 vs VM 正常——补闸）
            foreach (var fn in mir.Functions)
            {
                if (!fn.IsAsync
                    && fn.Blocks.SelectMany(b => b.Instructions).Any(inst =>
                        inst is MirAwait or MirYieldBare or MirYieldAlarm
                        || IsMutexEnter(inst))
                    && tainted.Add(fn.Symbol.Canonical))
                {
                    queue.Enqueue(fn.Symbol.Canonical);
                }
            }
            while (queue.Count > 0)
            {
                var calleeCanonical = queue.Dequeue();
                var calleeFn = byCanonical[calleeCanonical];
                RejectUnsupportedTaintedShape(calleeFn);
                if (calleeFn.Symbol.HasKeyword(BilKeyword.Init)
                    && calleeFn.Symbol.Owner?.Declaration.Kind != BilTypeKind.Class)
                {
                    // R2-c：值类型 init 无挂起协议（见上注）
                    throw new MwNotSupportedException(
                        "B-2 暂不支持含挂起点的值类型 init（值类型构造路径无挂起协议）: "
                        + calleeCanonical);
                }
                foreach (var caller in mir.Functions)
                {
                    // async 调用方本身即状态机化（调用点在 split 时改写），
                    // 无需传染；tainted 集合只收需要新状态机化的普通 fn

                    if (caller.IsAsync || tainted.Contains(caller.Symbol.Canonical))
                    {
                        continue;
                    }
                    var infect = false;
                    foreach (var block in caller.Blocks)
                    {
                        foreach (var inst in block.Instructions)
                        {
                            switch (inst)
                            {
                                case MirCall call
                                    when call.Target.Canonical == calleeCanonical:
                                    // 直调形态（显式 invoke 直调 / 值
                                    // 类型宿主运算符）静态唯一目标→
                                    // 传染；虚/interface 派发形态由
                                    // 下方闭包规则统一覆盖
                                    var direct = call.OperatorDispatch
                                        ? Binding.ImplBinder.BindOperatorCall(call.Target)
                                        : Binding.ImplBinder.BindCall(call.Target);
                                    if (direct is Binding.DirectCallBinding)
                                    {
                                        infect = true;
                                    }
                                    break;
                                case MirSuperCall superCall
                                    when superCall.Target.Canonical == calleeCanonical:
                                    // B-2：super 已解析为唯一基类实现
                                    // （恒直调），同直调协议传染
                                    infect = true;
                                    break;
                                case MirNewObject newObject
                                    when newObject.Init?.Canonical == calleeCanonical:
                                    // B-2：含挂起点的 init 经构造点协
                                    // 议收编（EmitInitSplit——分配与
                                    // init 下钻分离）
                                    infect = true;
                                    break;
                            }
                            // 虚/interface/class 运算符派发点：目标集合
                            // 静态不唯一——闭包内任一实现 tainted 则整
                            // 点升级（调用方传染；站点协议化见
                            // EmitVirtualCallSplit）
                            if (!infect && inst is MirCall anyCall
                                && IsVirtualDispatchSite(anyCall)
                                && ClosurePairsOf(context, anyCall.Target,
                                    anyCall.OperatorDispatch)
                                    .Any(p => tainted.Contains(p.ImplCanonical)))
                            {
                                infect = true;
                            }
                            // R2-b：invoke.indirect（callable 协议
                            // $$call 虚调用）同虚派发口径——$$call 闭
                            // 包内任一实现 tainted 则调用方传染（站
                            // 点协议化复用 EmitVirtualCallSplit 臂）
                            if (!infect && inst is MirInvokeIndirect invoke
                                && ClosurePairsOf(context,
                                    IndirectCallOperatorOf(context, caller, invoke),
                                    operatorDispatch: false)
                                    .Any(p => tainted.Contains(p.ImplCanonical)))
                            {
                                infect = true;
                            }
                            // R2-c：new.indirect——模块内 tainted
                            // class init 重载与站点静态实参形精确
                            // 匹配（argc + 逐物化 sheet 代入的
                            // canonical 恒等）则调用方传染（站点
                            // 协议化见 EmitNewIndirectSplit）
                            if (!infect && inst is MirNewIndirect newIndirect
                                && IndirectInitRelevant(context, caller,
                                    newIndirect, tainted))
                            {
                                infect = true;
                            }
                        }
                    }
                    if (infect && tainted.Add(caller.Symbol.Canonical))
                    {
                        queue.Enqueue(caller.Symbol.Canonical);
                    }
                }
            }
            RejectSyntheticSpineTainted(context, mir, tainted);
            // Phase 2.6（§19.2 语义纠偏）：PollingAlarm.isReady 允许含挂起
            // 点——撤销原 #08 止血拒绝（RejectTaintedPollingIsReady，76e304c）。
            // tainted 探测不再走 $mw.poll_probe 同步虚派发（vtable 槽指向
            // ReplaceWithTrap 陷阱的旧风险），改经恢复块站点协议臂下钻
            //（PreparePollProbeSites / EmitPollGate 双路径）
            return tainted;
        }

        // R3：合成入口脊柱直调站点（非 MIR、不可协议化）的 taint 补闸。
        // rigi_entry stub 同步直调 ..globals.init（main 前）、singleton
        // 急切 get fn（同前）与 gexc drain 的 GlobalExceptionHandler.
        // dispatch（Dispatcher 关停后）；未捕获异常 reporter 同步虚调
        // getMessage（同在后段）。这些站点运行在调度器启动前/关停后，
        // 挂起点无泵可恢复——tainted 即 ReplaceWithTrap 后脊柱照调
        // 必崩（实证：trap 0x80000003 / AV），受控拒绝
        private static void RejectSyntheticSpineTainted(MwContext context, MirModule mir,
            HashSet<string> tainted)
        {
            // 先扫 getMessage 根因：脊柱 fn（如 dispatch 空注册表分支
            // printErr(exc.getMessage())）常因虚调 tainted override 被
            // 传染，报根因比报脊柱更准
            foreach (var fn in mir.Functions)
            {
                if (tainted.Contains(fn.Symbol.Canonical) && IsExceptionGetMessage(context, fn))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点的 core::Exception.getMessage override："
                        + "native 未捕获异常 reporter 同步虚调 getMessage: "
                        + fn.Symbol.Canonical);
                }
            }
            foreach (var fn in mir.Functions)
            {
                if (!tainted.Contains(fn.Symbol.Canonical))
                {
                    continue;
                }
                var canonical = fn.Symbol.Canonical;
                if (canonical.StartsWith("$..globals.init(", System.StringComparison.Ordinal))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点（含经 lambda/indirect 传染）的全局初始值设定项："
                        + "native 入口脊柱在调度器启动前同步直调 ..globals.init: " + canonical);
                }
                if (canonical.StartsWith("core::GlobalExceptionHandler$.static.dispatch(",
                        System.StringComparison.Ordinal))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点的全局异常处理器：native gexc drain 在 "
                        + "Dispatcher 关停后同步直调 dispatch: " + canonical);
                }
                if (canonical.Contains(".mw.singleton.get(", System.StringComparison.Ordinal))
                {
                    throw new MwNotSupportedException(
                        "R3 暂不支持含挂起点的 singleton init：native 入口脊柱在调度器"
                        + "启动前急切同步构造 singleton: " + canonical);
                }
            }
        }

        // tainted fn 是否 core::Exception 派生链上的 getMessage override
        //（reporter 脊柱虚调目标集）
        private static bool IsExceptionGetMessage(MwContext context, MirFunction fn)
        {
            if (fn.Symbol.SignatureKey != "getMessage()")
            {
                return false;
            }
            var owner = fn.Symbol.Owner;
            for (var depth = 0; owner != null && depth < 64; depth++)
            {
                if (owner.Canonical == "core::Exception")
                {
                    return true;
                }
                if (owner.Declaration.ExtendsType is not { } extendsRef)
                {
                    return false;
                }
                owner = context.Symbols.FindTypeByRef(BilVerificationContext.StripTypeArguments(
                    MwTypeKey.Normalize(extendsRef)));
            }
            return false;
        }
        // 虚派发站点判定（MirCall 的运行期目标非静态唯一）：显式
        // invoke 经 BindCall、intrinsic 运算符经 BindOperatorCall；
        // class 虚/interface iMap 两种形态
        private static bool IsVirtualDispatchSite(MirCall call)
        {
            var binding = call.OperatorDispatch
                ? Binding.ImplBinder.BindOperatorCall(call.Target)
                : Binding.ImplBinder.BindCall(call.Target);
            return binding is Binding.VirtualCallBinding
                or Binding.InterfaceCallBinding;
        }

        // R2-b：invoke.indirect 的静态 $$call 目标解析（MIR 期
        // 同 EmitIndirectInvoke 口径——实参/结果类型取调用点局部
        // 静态类型，沿 extends 链唯一匹配；查不到属 Gate 漏检）
        private static MwMemberSymbol IndirectCallOperatorOf(MwContext context,
            MirFunction fn, MirInvokeIndirect invoke)
        {
            var argTypes = new List<string>(invoke.Args.Count);
            foreach (var arg in invoke.Args)
            {
                if (arg is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("invoke.indirect 实参非局部");
                }
                argTypes.Add(fn.FindLocal(local.Name).Type.Canonical);
            }
            var resultType = invoke.Result != null
                ? fn.FindLocal(invoke.Result).Type.Canonical
                : null;
            return Binding.ImplBinder.BindIndirectCall(context.Symbols,
                invoke.CallTargetType.Canonical, argTypes, resultType,
                context.Module.Functions) is Binding.IndirectCallBinding binding
                ? binding.CallOperator
                : throw new CompilerInternalException(
                    "invoke.indirect 的非预期绑定形态: " + invoke.CallTargetType.Canonical);
        }

        // 虚/interface 派发的「类 → 槽实现」闭包对（调用点动态分流
        // 与传染判定共用；静态知识，按目标 canonical 缓存）。镜像
        // MirReachability.AddVirtualEdges/AddInterfaceEdges 的查询口
        // 径，但保留每类实现身份（分流臂需要）
        private readonly Dictionary<string,
            List<(TypeLayoutPlan ClassPlan, string ImplCanonical)>> _closureCache = new(
                System.StringComparer.Ordinal);

        private List<(TypeLayoutPlan ClassPlan, string ImplCanonical)> ClosurePairsOf(
            MwContext context, MwMemberSymbol target, bool operatorDispatch)
        {
            if (_closureCache.TryGetValue(target.Canonical, out var cached))
            {
                return cached;
            }
            var pairs = new List<(TypeLayoutPlan, string)>();
            var binding = operatorDispatch
                ? Binding.ImplBinder.BindOperatorCall(target)
                : Binding.ImplBinder.BindCall(target);
            var query = context.DispatchQuery;
            // 宿主槽表按 PlanKey 查询（同名不同元数模板 canonical
            // 撞键——core::Func<1>/Func<2> 共享 "core::Func" 裸键，
            // 先登记者占用；R2-b 合并模块形态实证槽表错配致闭包
            // 为空）。VirtualCallEmitter.VirtualSlotOf 同口径
            var ownerSlots = target.Owner == null
                ? null : query?.GetVTableSlots(GenericAbi.PlanKey(target.Owner));
            if (query != null && ownerSlots != null)
            {
                var slot = -1;
                for (var i = 0; i < ownerSlots.Count; i++)
                {
                    if (ownerSlots[i] == target.Canonical)
                    {
                        slot = i;
                        break;
                    }
                }
                if (slot >= 0)
                {
                    // R2-b：闭包枚举直走布局计划表（不经字符串查
                    // 询口）——① 同名不同元数模板 canonical 撞键
                    //（core::Func<1>/Func<2> 共享 "core::Func"），
                    // 派生判定按模板声明逐级比对（arity 正确）；
                    // ② 跳过构造计划（臂类键恒为模板 canonical，
                    // 构造实例沿构造基链命中模板臂——R2-a）
                    var ownerTemplate = target.Owner!;
                    switch (binding)
                    {
                        case Binding.VirtualCallBinding:
                            foreach (var plan in context.Layout!.Plans)
                            {
                                if (plan.Kind != TypeLayoutKind.Class
                                    || Layout.ConstructedTypeCollector.IsConstructed(
                                        plan.Symbol.Canonical)
                                    || !DerivesFromTemplate(plan, ownerTemplate)
                                    || slot >= plan.VTableSlots.Count)
                                {
                                    continue;
                                }
                                pairs.Add((plan,
                                    plan.VTableSlots[slot]));
                            }
                            break;
                        case Binding.InterfaceCallBinding:
                            foreach (var plan in context.Layout!.Plans)
                            {
                                if (plan.Kind != TypeLayoutKind.Class
                                    || Layout.ConstructedTypeCollector.IsConstructed(
                                        plan.Symbol.Canonical))
                                {
                                    continue;
                                }
                                var imap = plan.IMap;
                                var slots = plan.VTableSlots;
                                foreach (var (ifaceType, baseOffset) in imap)
                                {
                                    if ((ifaceType == target.Owner!.Canonical
                                            || context.Symbols.FindTypeByRef(ifaceType)
                                                == target.Owner)
                                        && baseOffset + slot < slots.Count)
                                    {
                                        pairs.Add((plan,
                                            slots[baseOffset + slot]));
                                    }
                                }
                            }
                            break;
                    }
                }
            }
            _closureCache.Add(target.Canonical, pairs);
            return pairs;
        }

        // R2-b：arity 正确的模板派生判定——沿 BasePlan 链按
        // 声明身份比对；闭合基类保留模板声明，且不同元数声明各自独立。
        // 不能用闭合 PlanKey 与模板 PlanKey 相等来判定继承关系。
        private static bool DerivesFromTemplate(TypeLayoutPlan plan, MwTypeSymbol ownerTemplate)
        {
            for (var current = plan; current != null; current = current.BasePlan)
            {
                if (current.Symbol.Declaration == ownerTemplate.Declaration)
                {
                    return true;
                }
            }
            return false;
        }

        // ===== R2-c：new.indirect × tainted class init 臂协议 =====

        // 模块内全部 class init 重载（懒建缓存；声明级形参类型 =
        // CanonicalSignature 形参段，与 DynamicNewEmitter.CollectInits
        // 同源）
        private List<IndirectInitOverload>? _classInitOverloads;

        private List<IndirectInitOverload> ClassInitOverloads(MwContext context,
            MirModule mir)
        {
            if (_classInitOverloads != null)
            {
                return _classInitOverloads;
            }
            var list = new List<IndirectInitOverload>();
            foreach (var fn in mir.Functions)
            {
                if (!fn.Symbol.HasKeyword(BilKeyword.Init)
                    || fn.Symbol.Owner == null
                    || fn.Symbol.Owner.Declaration.Kind != BilTypeKind.Class)
                {
                    continue;
                }
                var template = context.Symbols.FindTypeByRef(fn.Symbol.Owner.Canonical)
                    ?? fn.Symbol.Owner;
                var signature = CanonicalSignature.Parse(fn.Symbol.Canonical);
                var declParams = new List<string>(signature.Parameters.Count);
                foreach (var parameter in signature.Parameters)
                {
                    declParams.Add(parameter.TypeRef);
                }
                // 宿主声明级 ..init.wrapper（字段初始值缝合，可空；
                // DynamicNewEmitter.CollectInits 同钥匙）
                var wrapper = context.Symbols.FindMember(
                    template.Declaration.Symbol + "$..init.wrapper()@.void");
                list.Add(new IndirectInitOverload
                {
                    InitFn = fn,
                    HostTemplate = template,
                    DeclParamTypeRefs = declParams,
                    Wrapper = wrapper,
                });
            }
            _classInitOverloads = list;
            return list;
        }

        // 重载的全部物化 sheet × 代入后形参列：非泛型宿主 = 本类计
        // 划；泛型宿主 = 模块内全部闭合构造计划（开放模板 sheet 的
        // 分发器恒 ret null——运行期不会选中任何 init，不入臂）。
        // 形参代入镜像 DynamicNewEmitter.CollectInits（Substitute +
        // Normalize）
        private static List<(TypeLayoutPlan Plan, List<string> ParamTypes)> SheetsOf(
            MwContext context, IndirectInitOverload overload)
        {
            if (overload.Sheets != null)
            {
                return overload.Sheets;
            }
            var sheets = new List<(TypeLayoutPlan, List<string>)>();
            if (context.Layout != null)
            {
                foreach (var plan in context.Layout.Plans)
                {
                    if (plan.Kind != TypeLayoutKind.Class)
                    {
                        continue;
                    }
                    var canonical = plan.Symbol.Canonical;
                    bool isSheetOfHost;
                    if (overload.HostTemplate.Declaration.GenericParameters.Count == 0)
                    {
                        isSheetOfHost = canonical == overload.HostTemplate.Canonical;
                    }
                    else
                    {
                        isSheetOfHost =
                            Layout.ConstructedTypeCollector.IsConstructed(canonical)
                            && GenericAbi.IsClosedConstructed(canonical)
                            && BilVerificationContext.StripTypeArguments(
                                MwTypeKey.Normalize(canonical))
                                == overload.HostTemplate.Canonical;
                    }
                    if (!isSheetOfHost)
                    {
                        continue;
                    }
                    var subst = Layout.ConstructedTypeCollector.BuildSubstitution(
                        canonical, plan.Symbol.Declaration);
                    var paramTypes = new List<string>(overload.DeclParamTypeRefs.Count);
                    foreach (var declParam in overload.DeclParamTypeRefs)
                    {
                        paramTypes.Add(MwTypeKey.Normalize(
                            Layout.ConstructedTypeCollector.Substitute(declParam, subst)));
                    }
                    sheets.Add((plan, paramTypes));
                }
            }
            overload.Sheets = sheets;
            return sheets;
        }

        // 站点静态实参形（argSheets 物化同源——发射期按实参局部静
        // 态类型取 ArgToken）
        private static List<string> IndirectStaticArgTypes(MirFunction fn,
            MirNewIndirect inst)
        {
            var argTypes = new List<string>(inst.Args.Count);
            foreach (var arg in inst.Args)
            {
                if (arg is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("new.indirect 实参非局部");
                }
                argTypes.Add(fn.FindLocal(local.Name).Type.Canonical);
            }
            return argTypes;
        }

        private static bool IndirectArgsMatch(IReadOnlyList<string> siteArgTypes,
            IReadOnlyList<string> paramTypes)
        {
            if (siteArgTypes.Count != paramTypes.Count)
            {
                return false;
            }
            for (var i = 0; i < siteArgTypes.Count; i++)
            {
                if (MwTypeKey.Normalize(siteArgTypes[i]) != paramTypes[i])
                {
                    return false;
                }
            }
            return true;
        }

        // 站点相关性：存在 tainted class init 重载的某个物化 sheet
        // 与站点静态实参形精确匹配 → true（调用方传染/站点协议化）。
        // 占位实参（argSheets 运行期物化）且 argc 撞上任一 tainted
        // 重载时匹配不可静态判定 → 受控拒绝
        private bool IndirectInitRelevant(MwContext context, MirFunction fn,
            MirNewIndirect inst, HashSet<string> tainted)
        {
            var overloads = ClassInitOverloads(context,
                context.Mir
                    ?? throw new CompilerInternalException("CoroutineSplit 要求 Mir 已挂载"));
            var argTypes = IndirectStaticArgTypes(fn, inst);
            var hasPlaceholder = argTypes.Any(t =>
                t.Contains(".generic<", System.StringComparison.Ordinal));
            var argcCollision = false;
            foreach (var overload in overloads)
            {
                if (!tainted.Contains(overload.InitFn.Symbol.Canonical))
                {
                    continue;
                }
                foreach (var (_, paramTypes) in SheetsOf(context, overload))
                {
                    if (paramTypes.Count != argTypes.Count)
                    {
                        continue;
                    }
                    argcCollision = true;
                    if (!hasPlaceholder && IndirectArgsMatch(argTypes, paramTypes))
                    {
                        return true;
                    }
                }
            }
            if (argcCollision && hasPlaceholder)
            {
                throw new MwNotSupportedException(
                    "B-2 暂不支持泛型占位实参的 new.indirect 与含挂起点 init 同模块"
                    + "（实参 sheet 匹配运行期不可判定）: " + fn.Symbol.Canonical);
            }
            return false;
        }

        // 臂的派生排除 sheet：物化类计划中严格派生自臂 sheet 的全
        // 部 sheet（精确化——typeid 命中派生 sheet 时其自身分发器
        // 决定选择（无匹配即 NoSuchMethod），不得落本臂）
        private static List<string> DerivedSheetsOf(MwContext context,
            TypeLayoutPlan armPlan)
        {
            var sheets = new List<string>();
            if (context.Layout == null)
            {
                return sheets;
            }
            foreach (var plan in context.Layout.Plans)
            {
                if (plan.Kind != TypeLayoutKind.Class
                    || ReferenceEquals(plan, armPlan)
                    || plan.Symbol.Canonical == armPlan.Symbol.Canonical)
                {
                    continue;
                }
                for (var current = plan.BasePlan; current != null; current = current.BasePlan)
                {
                    if (ReferenceEquals(current, armPlan))
                    {
                        sheets.Add(plan.Symbol.Canonical);
                        break;
                    }
                }
            }
            return sheets;
        }

        // R2-c 站点协议信息回填（PrepareSplit 内、活性分析之前——
        // callee frame 槽须进 fn.Locals）
        private void PrepareIndirectInitSites(MwContext context, MirModule mir,
            MirFunction fn, List<SuspensionPoint> points, HashSet<string> tainted)
        {
            foreach (var point in points)
            {
                if (point.IndirectInit == null)
                {
                    continue;
                }
                var inst = (MirNewIndirect)point.Inst;
                var site = point.IndirectInit;
                if (inst.TypeId is not MirLocalOperand typeIdLocal)
                {
                    throw new CompilerInternalException(
                        "new.indirect 挂起点 typeid 非局部: " + fn.Symbol.Canonical);
                }
                site.TypeIdLocal = typeIdLocal.Name;
                site.TargetLocal = inst.Target;
                site.ExcTarget = inst.ExcTarget;
                site.Original = inst;
                var argTypes = IndirectStaticArgTypes(fn, inst);
                var armIndex = 0;
                foreach (var overload in ClassInitOverloads(context, mir))
                {
                    if (!tainted.Contains(overload.InitFn.Symbol.Canonical))
                    {
                        continue;
                    }
                    foreach (var (plan, paramTypes) in SheetsOf(context, overload))
                    {
                        if (!IndirectArgsMatch(argTypes, paramTypes))
                        {
                            continue;
                        }
                        var callee = overload.InitFn;
                        var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(
                            callee.Symbol.Canonical);
                        var calleeLocal = "$mw.callee." + point.State + "." + armIndex;
                        armIndex++;
                        fn.AddLocal(new MirLocal(calleeLocal,
                            MirType.Of(frameCanonical)));
                        var entry = new CallSiteInfo
                        {
                            Callee = callee,
                            CalleeLocal = calleeLocal,
                            CalleeFrameCanonical = frameCanonical,
                            ResumeSymbol = ProxyBakeSupport.SyntheticMember(
                                "$mw.resume." + callee.Symbol.Canonical, owner: null),
                            ResultFieldSymbol = null,
                        };
                        // 落参实参 = .this（臂内新建对象落定 Target
                        // 槽）+ 用户实参；宿主构造形态 = 臂 sheet（闭
                        // 合构造 → 类级 typeid 常量合成）
                        var withThis = new List<MirOperand>
                        {
                            new MirLocalOperand(inst.Target),
                        };
                        withThis.AddRange(inst.Args);
                        PlanArgDrops(context, fn, entry, callee, withThis,
                            MwTypeKey.Normalize(plan.Symbol.Canonical));
                        site.Arms.Add(new IndirectInitArm
                        {
                            AllocType = plan.Symbol,
                            SheetCanonical = plan.Symbol.Canonical,
                            InitWrapper = overload.Wrapper,
                            ExclusionSheets = DerivedSheetsOf(context, plan),
                            Impl = entry,
                        });
                    }
                }
                if (site.Arms.Count == 0)
                {
                    throw new CompilerInternalException(
                        "new.indirect 挂起点无命中臂（相关性判定与建臂不一致）: "
                        + fn.Symbol.Canonical);
                }
            }
        }

        // 不可协议化形态拒绝：wrapper/proxy 烘焙产物（$.wrapped./
        // $.mwrapped./$mw. 前缀合成 fn）经 wrapper 派发链/方法地址
        // 间接触达——router/trampoline 的通配 ABI 与值包转发形态无
        // 挂起协议插点（运行期目标集随 wrapper 实例符号表动态决
        // 定，静态闭包不可枚举），保留受控拒绝。R2-b 起 $$call 闭
        // 包体不再拒绝（invoke.indirect 调用点动态分流协议覆盖，
        // 同虚派发臂机制——EmitVirtualCallSplit）
        private static void RejectUnsupportedTaintedShape(MirFunction fn)
        {
            var canonical = fn.Symbol.Canonical;
            // 文本分派是普通 MIR 调用组成的封闭 if 链，没有 wrapper
            // router 的动态包 ABI，可由既有直调/虚调挂起协议完整切分。
            if ((canonical.StartsWith("$mw.", System.StringComparison.Ordinal)
                    && canonical != BuiltinToStringDispatchPass.DispatchCanonical
                    && canonical != BuiltinToStringDispatchPass.HashDispatchCanonical)
                || canonical.Contains("$..init.", System.StringComparison.Ordinal)
                || canonical.Contains(ProxyBakeSupport.WrappedInfix,
                    System.StringComparison.Ordinal)
                || canonical.Contains(ProxyBakeSupport.MwrappedInfix,
                    System.StringComparison.Ordinal))
            {
                throw new MwNotSupportedException(
                    "B-2 暂不支持 proxy/wrapper 烘焙链可达的含挂起点 fn: " + canonical);
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
            // B-1：tainted→tainted 直调挂起点（Inst 为 MirCall 时非空）
            internal CallSiteInfo? CallSite;
            // B-2：虚/interface/运算符派发挂起点（目标集合动态分流）
            internal VirtualSiteInfo? Virtual;
            // B-2：含挂起点 init 的构造挂起点（Inst 为 MirNewObject）
            internal InitSiteInfo? InitSite;
            // R2-c：new.indirect × tainted class init 的构造挂起点
            internal IndirectInitSiteInfo? IndirectInit;
            // Phase 2.6：PollingAlarm 探测站点（闭包内存在 tainted isReady
            // 实现时非空）——探测经恢复块站点协议臂下钻，支持 isReady
            // 中途挂起；全 untainted 闭包为 null（$mw.poll_probe 廉价路径）
            internal PollProbeSiteInfo? ProbeSite;
        }

        // Phase 2.6：yield-alarm 探测站点协议信息（PreparePollProbeSites
        // 回填；镜像 VirtualSiteInfo 的臂结构，站点固定为 isReady(alarm)
        // 单参虚派发）。ProbeState 是探测挂起子状态：探测 fn（isReady
        // 状态机）中途挂起时本层 frame 写入该 state，重发布恢复走专用
        // 恢复块直落探测调用块下钻——区别于「未就绪退回等待后重排」
        // 的再次首探（state 保持 N 重入 poll gate）
        private sealed class PollProbeSiteInfo
        {
            internal int ProbeState;
            internal string AlarmSlot = "";   // 探测帧接收者（alarm 槽，恒活跃）
            // tainted isReady 实现臂（InheritanceDepth 深→浅；每实现
            // 一套 CallSiteInfo，TypeRefs 为臂 type.is 目标集，Depth 为
            // 排序键——排完序后仅作占位）
            internal List<(CallSiteInfo Impl, List<string> TypeRefs, int Depth)>
                Arms = new();
            // untainted 联合臂 type.is 目标集（全 tainted 闭包为空——
            // 命中走 $mw.poll_probe 同步廉价路径；分流 miss = 闭包外
            // 类型，运行期不可达）
            internal List<string> UntaintedTypeRefs = new();
        }

        // R2-c：new.indirect × tainted class init 的构造点协议信息。
        // native 槽 0 分发器不继承 init（实证：派生类无自声明 init
        // 时 new.indirect 抛 NoSuchMethod，双端一致）——运行期
        // typeid 只有恰好是 tainted 重载宿主类（的某个闭合构造）
        // sheet 时才可能选中该 init，故臂条件 = 精确 sheet 匹配：
        // IsTypeId(臂 sheet) ∧ ¬IsTypeId(各派生物化 sheet)。命中
        // 臂：以臂 sheet 的静态构造形态 MirNewObject 空 init 分配
        //（init.wrapper 原位缝合字段初始值）→ Target 槽落定并回存
        // 本层 frame → init frame（.this = 新建对象）下钻；DONE 直
        // 落原后继（结果即 Target 槽）。全部臂未命中 → 默认臂落原
        // MirNewIndirect（同步分发器路径——运行期目标必非 tainted
        // init 或 NoSuchMethod，语义保持）
        private sealed class IndirectInitSiteInfo
        {
            internal string TypeIdLocal = "";        // typeid 槽（分流链复读，强制活跃）
            internal string TargetLocal = "";        // 原指令结果槽（臂内分配落点 + 回存 frame）
            internal MirBlock? ExcTarget;            // 原指令异常边
            internal MirNewIndirect Original = null!; // 默认臂复用原指令
            internal List<IndirectInitArm> Arms = new();
        }

        // 单条 new.indirect 臂：一个物化 sheet × 一个 tainted init 重载
        private sealed class IndirectInitArm
        {
            internal MwTypeSymbol AllocType = null!;  // 臂 sheet 的构造形态（MirNewObject 分配用）
            internal string SheetCanonical = "";
            internal MwMemberSymbol? InitWrapper;     // 宿主声明级 ..init.wrapper（可空）
            internal List<string> ExclusionSheets = new(); // 派生物化 sheet（精确化排除项）
            internal CallSiteInfo Impl = null!;
        }

        // R2-c：模块内 class init 重载描述（tainted 判定在调用点）；
        // 声明级形参类型按物化 sheet 逐份代入（镜像 DynamicNewEmitter
        // .CollectInits 的匹配语义：argc + ArgToken(canonical) 恒等）
        private sealed class IndirectInitOverload
        {
            internal MirFunction InitFn = null!;
            internal MwTypeSymbol HostTemplate = null!;
            internal List<string> DeclParamTypeRefs = new();
            internal MwMemberSymbol? Wrapper;
            internal List<(TypeLayoutPlan Plan, List<string> ParamTypes)>? Sheets;
        }

        // 含挂起点 init 的构造点协议信息：分配与 init 下钻分离——
        // head 用合成空 init 完成分配（init.wrapper 缝合字段初始值
        // 保持原位），Target 槽先落定；init frame 的 .this = 新建对
        // 象，恢复后 DONE 直落原后继（结果即 Target 槽本身）
        private sealed class InitSiteInfo
        {
            internal CallSiteInfo Site = null!;
            internal string TargetLocal = "";
            internal MirNewObject Original = null!;
        }

        // tainted 直调点的 callee 协议信息（PrepareCallSites 回填；
        // FrameType/FrameInit 延迟到 EmitCallSplit 解析——plain frame
        // 预注册完成后才存在，递归调用链安全）
        private sealed class CallSiteInfo
        {
            internal MirFunction Callee = null!;
            internal string CalleeLocal = "";        // 调用方 resume fn 内的 callee frame 槽
            internal string CalleeFrameCanonical = "";
            internal MwMemberSymbol ResumeSymbol = null!;
            internal string? ResultFieldSymbol;      // callee 非 void 时的 $mw.result 字段符号
            // B-2：实参→callee frame 落参计划（§7.2 隐藏参数感知：
            // 类级 typeid 从调用约定剔除——按宿主构造形态合成常量
            // typeid 或转抄调用方同名 .generic.* 局部）
            internal List<ArgDrop> Drops = new();
        }

        // 单条落参：直落 = 调用点实参槽；TypeIdConst = MirGetTypeId
        // 常量 typeid；CallerTypeId = 调用方 .generic.* 局部转抄；
        // ReceiverTypeIdOwner/Parameter = 运行期读取接收者真实泛型实参
        //（#..generic.）读取（泛型宿主虚派发臂——静态构造形态被
        // 接收者 cast 剥成裸模板时，真实构造实参恒在实例头隐藏槽）
        private sealed class ArgDrop
        {
            internal string FrameFieldSymbol = "";
            internal MirOperand? Operand;
            internal MirType? OperandTargetType;
            internal string? TypeIdTypeRef;
            internal string? CallerTypeIdLocal;
            internal string? ReceiverTypeIdOwner;
            internal string? ReceiverTypeIdParameter;
            internal MirOperand? ReceiverTypeIdOperand;
        }

        // B-2 虚派发挂起点：闭包全类臂（最深派生优先——臂条件
        // type.is 是子类判定，浅类臂不得遮蔽深类）。tainted 实现臂
        // 走协议（建对应 frame 下钻）；非 tainted 实现臂落原调用块
        //（普通虚派发，对齐 VM 可观察行为）；默认臂（闭包外类型/
        // null 接收者）同为原调用（NRE 语义保持）
        private sealed class VirtualSiteInfo
        {
            internal string ReceiverLocal = "";      // 接收者槽（分流链复读，强制活跃）
            internal string? Result;                 // 原调用结果槽
            internal MirBlock? ExcTarget;            // 原调用异常边
            internal MirInst OriginalCall = null!;   // 默认臂/非 tainted 臂复用原指令（MirCall/MirInvokeIndirect）
            internal List<VirtualArm> Arms = new();  // 全闭包类臂（最深派生优先）
        }

        private sealed class VirtualArm
        {
            internal int InheritanceDepth;
            internal CallSiteInfo? Impl;             // 非 tainted 实现为 null（落原调用）
            // R2-a：臂条件 type.is 目标集——首元素恒为类 PlanKey
            //（非泛型 = 唯一元素；泛型类 = 模板空壳 + 模块内全部闭
            // 合构造 sheet：实例头是构造 sheet 且其基链不含模板空
            // 壳，单模板键判定恒 miss；开放占位 new 的实例仍携模
            // 板空壳，故模板键保留在首位）
            internal List<string> TypeRefs = null!;
        }

        // split 模式：Tasked = Task 包装（async fn 与 tainted main——
        // frame 带 $mw.task，终态走 Task complete/fail 序列）；Plain =
        // 裸 frame（tainted 普通 fn——无 Task，结果写 $mw.result 由
        // 调用方 DONE 臂读取，传播垫尾 release + ret FAILED 沿链上传）
        private enum SplitMode { Tasked, Plain }

        // split 计划（PrepareSplit 产出，ExecuteSplit 消费；两相分离
        // 让 plain frame 类型先于一切 resume 合成完成注册）
        private sealed class SplitPlan
        {
            internal MirFunction Fn = null!;
            internal SplitMode Mode;
            internal List<SuspensionPoint> Points = null!;
            internal List<MirLocal> SavedSlots = null!;
            internal HashSet<string> ParamNames = null!;
            internal string FrameCanonical = "";
            internal MwTypeSymbol FrameType = null!;
            internal MirType FrameMirType = null!;
            internal MwMemberSymbol FrameInit = null!;
            internal string StateFieldSymbol = "";
            internal string? TaskFieldSymbol;
            internal string? TaskTypeRef;
            internal string? TaskConstructionRef;
            internal string? ResultFieldSymbol;
            internal MwMemberSymbol ResumeSymbol = null!;
        }

        private List<SuspensionPoint> CollectSuspensionPoints(MwContext context,
            MirFunction fn, HashSet<string> tainted)
        {
            var points = new List<SuspensionPoint>();
            foreach (var block in fn.Blocks)
            {
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    var inst = block.Instructions[i];
                    // B-1：直调 tainted fn 的调用点同为挂起点（callee
                    // 挂起沿链上传，本 fn 须在此 state 恢复下钻）；
                    // B-2：super 调用与直调形态运算符同为静态唯一目
                    // 标收编；虚/interface/class 运算符派发点闭包内
                    // 任一实现 tainted 则整点升级（动态分流协议）；
                    // Mutex.enter 优先判（其目标永不 tainted）
                    var isPoint = inst is MirAwait or MirYieldBare or MirYieldAlarm
                        || IsMutexEnter(inst)
                        || (inst is MirCall call && !call.OperatorDispatch
                            && tainted.Contains(call.Target.Canonical)
                            && Binding.ImplBinder.BindCall(call.Target)
                                is Binding.DirectCallBinding)
                        || (inst is MirCall operatorCall && operatorCall.OperatorDispatch
                            && tainted.Contains(operatorCall.Target.Canonical)
                            && Binding.ImplBinder.BindOperatorCall(operatorCall.Target)
                                is Binding.DirectCallBinding)
                        || (inst is MirSuperCall superCall
                            && tainted.Contains(superCall.Target.Canonical))
                        || (inst is MirNewObject newObject
                            && newObject.Init != null
                            && tainted.Contains(newObject.Init.Canonical));
                    var point = isPoint
                        ? new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                        }
                        : null;
                    if (point == null && inst is MirCall dispatchCall
                        && !IsMutexEnter(dispatchCall)
                        && IsVirtualDispatchSite(dispatchCall)
                        && ClosurePairsOf(context, dispatchCall.Target,
                            dispatchCall.OperatorDispatch)
                            .Any(p => tainted.Contains(p.ImplCanonical)))
                    {
                        point = new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                            Virtual = new VirtualSiteInfo(),
                        };
                    }
                    // R2-b：invoke.indirect 挂起点——$$call 闭包内任
                    // 一实现 tainted 则整点升级（接收者 = CallTarget，
                    // 虚派发臂协议同 MirCall 形态）
                    if (point == null && inst is MirInvokeIndirect invoke
                        && ClosurePairsOf(context,
                            IndirectCallOperatorOf(context, fn, invoke),
                            operatorDispatch: false)
                            .Any(p => tainted.Contains(p.ImplCanonical)))
                    {
                        point = new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                            Virtual = new VirtualSiteInfo(),
                        };
                    }
                    // R2-c：new.indirect 挂起点——模块内 tainted
                    // class init 与站点静态实参形精确匹配则整点升
                    // 级（精确 sheet 臂协议）
                    if (point == null && inst is MirNewIndirect newIndirect
                        && IndirectInitRelevant(context, fn, newIndirect, tainted))
                    {
                        point = new SuspensionPoint
                        {
                            Block = block,
                            InstIndex = i,
                            State = points.Count + 1,
                            Inst = inst,
                            IndirectInit = new IndirectInitSiteInfo(),
                        };
                    }
                    if (point != null)
                    {
                        points.Add(point);
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
                                        // new/cast 等在发射期隐式读取 typeid，MIR
                                        // 显式操作数的 def/use 看不到这些依赖。
                                        .Where(l => live.Contains(l.Name)
                                            || l.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
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
                // B-1：tainted 调用点的 callee frame 槽恒活跃——恢复块
                // 重回调用块经 MirResumeCall 再下钻（frame 从调用方
                // frame 的 callee 槽恢复）
                if (point.CallSite != null
                    && !point.LiveAfter.Contains(point.CallSite.CalleeLocal))
                {
                    point.LiveAfter.Add(point.CallSite.CalleeLocal);
                }
                // B-2：init 构造点的 init frame 槽恒活跃（同直调口径）
                if (point.InitSite != null
                    && !point.LiveAfter.Contains(point.InitSite.Site.CalleeLocal))
                {
                    point.LiveAfter.Add(point.InitSite.Site.CalleeLocal);
                }
                // B-2：虚派发点的接收者（恢复分流链复读）与各 tainted
                // 实现的 callee frame 槽恒活跃
                if (point.Virtual != null)
                {
                    if (!point.LiveAfter.Contains(point.Virtual.ReceiverLocal))
                    {
                        point.LiveAfter.Add(point.Virtual.ReceiverLocal);
                    }
                    foreach (var arm in point.Virtual.Arms)
                    {
                        if (arm.Impl != null
                            && !point.LiveAfter.Contains(arm.Impl.CalleeLocal))
                        {
                            point.LiveAfter.Add(arm.Impl.CalleeLocal);
                        }
                    }
                }
                // R2-c：new.indirect 点的 typeid 槽（恢复分流链复
                // 读）、Target 槽（臂内分配落点 + 回存）与各臂
                // callee frame 槽恒活跃
                if (point.IndirectInit != null)
                {
                    if (!point.LiveAfter.Contains(point.IndirectInit.TypeIdLocal))
                    {
                        point.LiveAfter.Add(point.IndirectInit.TypeIdLocal);
                    }
                    if (!point.LiveAfter.Contains(point.IndirectInit.TargetLocal))
                    {
                        point.LiveAfter.Add(point.IndirectInit.TargetLocal);
                    }
                    foreach (var arm in point.IndirectInit.Arms)
                    {
                        if (!point.LiveAfter.Contains(arm.Impl.CalleeLocal))
                        {
                            point.LiveAfter.Add(arm.Impl.CalleeLocal);
                        }
                    }
                }
                // Phase 2.6：探测站点各 tainted isReady 实现的 callee
                // frame 槽恒活跃——探测挂起后恢复块重回探测调用块再下钻
                //（frame 从本层 frame 的探测槽恢复）；alarm 槽已由上方
                // yield-alarm 段保证
                if (point.ProbeSite != null)
                {
                    foreach (var arm in point.ProbeSite.Arms)
                    {
                        if (!point.LiveAfter.Contains(arm.Impl.CalleeLocal))
                        {
                            point.LiveAfter.Add(arm.Impl.CalleeLocal);
                        }
                    }
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
            MirNewValue newValue => newValue.ExcTarget,
            MirGetField getField => getField.ExcTarget,
            MirAwait awaitInst => awaitInst.ExcTarget,
            MirYieldAlarm yieldAlarm => yieldAlarm.ExcTarget,
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
                // G4：占位运算符的运行期派发节点（操作数按借用计）
                case MirGenericBinaryOp genericBinary:
                    AddOperandUse(genericBinary.Left, live);
                    AddOperandUse(genericBinary.Right, live);
                    break;
                case MirGenericUnaryOp genericUnary:
                    AddOperandUse(genericUnary.Operand, live);
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
                case MirGetClassTypeArgument argument:
                    AddOperandUse(argument.Receiver, live);
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
                // B-1：resume 直调借用 callee frame（所有权归调用方）
                case MirResumeCall resumeCall:
                    AddOperandUse(new MirLocalOperand(resumeCall.FrameSlot), live);
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
            MirGenericBinaryOp genericBinary => genericBinary.Target,
            MirGenericUnaryOp genericUnary => genericUnary.Target,
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
            MirGetClassTypeArgument argument => argument.Target,
            MirWrapNullable wrap => wrap.Target,
            MirUnwrapNullable unwrap => unwrap.Target,
            MirBoxAny box => box.Target,
            MirUnboxAny unbox => unbox.Target,
            MirCast cast => cast.Target,
            MirTakePending takePending => takePending.TargetLocal,
            MirAwait awaitInst => awaitInst.ResultSlot,
            MirCoroutineCreate create => create.HandleSlot,
            MirResumeCall resumeCall => resumeCall.CodeSlot,
            MirFailureLoad failureLoad => failureLoad.OutFatSlot,
            _ => null,
        };

        // ===== split 主流程（两相：PrepareSplit 收集/注册 frame →
        // ExecuteSplit 合成 resume + 改写原 fn）=====

        private SplitPlan PrepareSplit(MwContext context, MirModule mir, MirFunction fn,
            HashSet<string> tainted, SplitMode mode)
        {
            var points = CollectSuspensionPoints(context, fn, tainted);
            // B-1：tainted 直调点建 callee 协议信息 + callee frame 合成
            // 槽（先入 fn.Locals，活性分析/保存槽集/resume 局部表同源）
            PrepareCallSites(context, mir, fn, points, tainted, mode);
            // B-2：虚派发挂起点建分流臂协议信息（同理先入 fn.Locals）
            PrepareVirtualCallSites(context, mir, fn, points, tainted);
            // R2-c：new.indirect 挂起点建精确 sheet 臂协议信息（同理）
            PrepareIndirectInitSites(context, mir, fn, points, tainted);
            // Phase 2.6：yield-alarm 探测站点建臂（闭包内存在 tainted
            // isReady 实现时）——探测 callee frame 槽同理先入 fn.Locals
            PreparePollProbeSites(context, mir, fn, points, tainted);
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

            var plan = new SplitPlan
            {
                Fn = fn,
                Mode = mode,
                Points = points,
                SavedSlots = savedSlots,
                ParamNames = paramNames,
                FrameCanonical = SyntheticTypePlanner.FrameCanonicalOf(fn.Symbol.Canonical),
            };
            var frameSlots = savedSlots.Select(l => (l.Name, l.Type)).ToList();
            if (mode == SplitMode.Tasked)
            {
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
                frameSlots.Add((TaskSlotName, MirType.Of(taskConstructionRef)));
                plan.TaskTypeRef = taskTypeRef;
                plan.TaskConstructionRef = taskConstructionRef;
                plan.TaskFieldSymbol = TaskFieldSymbolOf(plan.FrameCanonical,
                    taskConstructionRef);
            }
            else
            {
                // B-1 裸 frame：非 void 时追加 $mw.result 结果字段
                //（调用方 DONE 臂读取；无 $mw.task——TaskState 投影是
                // Task 持有者的可观察性，plain fn 无 Task 可投影）
                if (!fn.ReturnType.IsVoid)
                {
                    frameSlots.Add((ResultSlotName, fn.ReturnType));
                    plan.ResultFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                        plan.FrameCanonical, ResultSlotName, fn.ReturnType.Canonical);
                }
            }
            plan.FrameType = SyntheticTypePlanner.EnsureFrameType(context,
                plan.FrameCanonical, frameSlots);
            plan.FrameMirType = MirType.Of(plan.FrameCanonical);
            plan.StateFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                plan.FrameCanonical, SyntheticTypePlanner.StateFieldName, I32.Canonical);

            // frame 空 init（裸 ret；字段零值由 rigi_alloc 清零承担）
            plan.FrameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(plan.FrameCanonical))
                ?? throw new CompilerInternalException("frame 空 init 未随类型注册: " + plan.FrameCanonical);
            var thisParam = new MirLocal(".this", plan.FrameMirType);
            mir.AddFunction(new MirFunction(plan.FrameInit, MirType.Of(".void"),
                new List<MirLocal> { thisParam }, new List<MirLocal> { thisParam },
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirRet(null)),
                }, false));

            plan.ResumeSymbol = ProxyBakeSupport.SyntheticMember(
                "$mw.resume." + fn.Symbol.Canonical, owner: null);
            return plan;
        }

        // B-1：tainted 直调点协议信息回填（PrepareSplit 内、活性分析
        // 之前——callee frame 槽须进 fn.Locals 才被保存槽集收编）。
        // B-2：plain 模式的 yield Alarm 已支持（EmitPollGate 失败尾
        // 有 plain 分叉——pending 保持置位 ret FAILED 沿链上传）；
        // super 调用与显式/直调运算符同为静态唯一目标收编（同协议）；
        // §7.2 隐藏参数（类级 typeid 剔除）经落参计划合成
        private void PrepareCallSites(MwContext context, MirModule mir, MirFunction fn,
            List<SuspensionPoint> points, HashSet<string> tainted, SplitMode mode)
        {
            foreach (var point in points)
            {
                // 虚派发挂起点由 PrepareVirtualCallSites 建臂（直调
                // 协议不介入——同一调用点只能一种协议）
                if (point.Virtual != null)
                {
                    continue;
                }
                IReadOnlyList<MirOperand> args;
                MwMemberSymbol target;
                string? hostConstructedRef = null;
                switch (point.Inst)
                {
                    case MirCall call when !IsMutexEnter(call):
                        args = call.Args;
                        target = call.Target;
                        hostConstructedRef = call.HostConstructedRef;
                        break;
                    case MirSuperCall superCall:
                        args = superCall.Args;
                        target = superCall.Target;
                        break;
                    case MirNewObject newObject:
                        // 含挂起点的 init：落参实参 = .this（新建对象
                        // 槽）+ 用户构造实参；宿主构造形态即 new 的
                        // 类型（类级 typeid 合成恒可判定）
                        var withThis = new List<MirOperand>
                        {
                            new MirLocalOperand(newObject.Target),
                        };
                        withThis.AddRange(newObject.Args);
                        args = withThis;
                        // 挂起点判定（taint 分析）已保证 Init 非空
                        //（Init = null 的无 init 构造无体可 taint）
                        target = newObject.Init!;
                        hostConstructedRef = newObject.Type.Canonical;
                        break;
                    default:
                        continue;
                }
                var callee = mir.Functions.FirstOrDefault(f =>
                    f.Symbol.Canonical == target.Canonical)
                    ?? throw new CompilerInternalException(
                        "tainted callee 不在模块函数表: " + target.Canonical);
                var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(
                    callee.Symbol.Canonical);
                var calleeLocal = "$mw.callee." + point.State;
                fn.AddLocal(new MirLocal(calleeLocal, MirType.Of(frameCanonical)));
                point.CallSite = new CallSiteInfo
                {
                    Callee = callee,
                    CalleeLocal = calleeLocal,
                    CalleeFrameCanonical = frameCanonical,
                    ResumeSymbol = ProxyBakeSupport.SyntheticMember(
                        "$mw.resume." + callee.Symbol.Canonical, owner: null),
                    ResultFieldSymbol = callee.ReturnType.IsVoid
                        ? null
                        : SyntheticTypePlanner.FrameFieldSymbol(frameCanonical,
                            ResultSlotName, callee.ReturnType.Canonical),
                };
                PlanArgDrops(context, fn, point.CallSite, callee, args,
                    hostConstructedRef);
                if (point.Inst is MirNewObject originalNew)
                {
                    point.InitSite = new InitSiteInfo
                    {
                        Site = point.CallSite,
                        TargetLocal = originalNew.Target,
                        Original = originalNew,
                    };
                    point.CallSite = null;
                }
            }
        }

        // 含挂起点 init 的合成空 init（模块级按类型懒建；对齐 frame/
        // Task 空 init 先例——字段零值由 rigi_alloc 清零承担，字段初
        // 始值由 ..init.wrapper 在构造点原位缝合）。.this 类型用模板
        // 声明形（MIR fn 模板共享；类级 typeid 由 prologue 从对象头
        // 自取，空体不用）
        private readonly Dictionary<string, MwMemberSymbol> _emptyCtorInits = new(
            System.StringComparer.Ordinal);

        private MwMemberSymbol EmptyCtorInit(MwContext context, MirModule mir,
            MwTypeSymbol type)
        {
            var template = context.Symbols.FindTypeByRef(type.Canonical) ?? type;
            var declarationRef = template.Canonical;
            if (_emptyCtorInits.TryGetValue(declarationRef, out var cached))
            {
                return cached;
            }
            var symbol = ProxyBakeSupport.SyntheticMember(
                declarationRef + "$.mw.emptyinit()@.void", template);
            var thisParam = new MirLocal(".this", MirType.Of(declarationRef));
            mir.AddFunction(new MirFunction(symbol, MirType.Of(".void"),
                new List<MirLocal> { thisParam }, new List<MirLocal> { thisParam },
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirRet(null)),
                }, false));
            _emptyCtorInits.Add(declarationRef, symbol);
            return symbol;
        }

        // B-2 虚派发挂起点协议信息回填：闭包全类臂（最深派生优先
        // 排序——type.is 子类判定下浅类臂不得遮蔽深类）；tainted
        // 实现臂建 callee frame 槽 + 落参计划（每实现一套，frame
        // 类型随实现不同）。R2-a：泛型宿主闭包放开（臂条件 OR 链
        // 见 ArmTypeRefsOf）；R2-b：invoke.indirect 站点（$$call
        // 闭包）同机制——接收者 = CallTarget，落参实参 = receiver
        // + 调用点实参
        private void PrepareVirtualCallSites(MwContext context, MirModule mir,
            MirFunction fn, List<SuspensionPoint> points, HashSet<string> tainted)
        {
            foreach (var point in points)
            {
                if (point.Virtual == null)
                {
                    continue;
                }
                MwMemberSymbol target;
                IReadOnlyList<MirOperand> callArgs;
                MirOperand receiverOperand;
                string? siteResult;
                MirBlock? siteExcTarget;
                string? hostConstructedRef;
                bool operatorDispatch;
                switch (point.Inst)
                {
                    case MirCall call:
                        target = call.Target;
                        callArgs = call.Args;
                        receiverOperand = call.Args.Count > 0
                            ? call.Args[0]
                            : throw new CompilerInternalException(
                                "虚派发挂起点缺接收者实参: " + call.Target.Canonical);
                        siteResult = call.Result;
                        siteExcTarget = call.ExcTarget;
                        hostConstructedRef = call.HostConstructedRef;
                        operatorDispatch = call.OperatorDispatch;
                        break;
                    case MirInvokeIndirect invoke:
                        // R2-b：callable 协议——静态目标 = 沿 extends
                        // 链解析的 $$call 成员；闭包臂同虚派发
                        target = IndirectCallOperatorOf(context, fn, invoke);
                        var withReceiver = new List<MirOperand> { invoke.CallTarget };
                        withReceiver.AddRange(invoke.Args);
                        callArgs = withReceiver;
                        receiverOperand = invoke.CallTarget;
                        siteResult = invoke.Result;
                        siteExcTarget = invoke.ExcTarget;
                        hostConstructedRef = null;
                        operatorDispatch = false;
                        break;
                    default:
                        throw new CompilerInternalException(
                            "虚派发挂起点的非预期指令形态: " + point.Inst.GetType().Name);
                }
                // R2-a：泛型宿主闭包放开——臂条件 type.is 以模板键
                // 判定类身份（泛型共享体实例头即模板 sheet 族，构造
                // 实参不影响子类判定）；tainted 实现的类级 typeid 落
                // 参由 PlanArgDrops 的实例隐藏字段回退供给
                var pairs = ClosurePairsOf(context, target, operatorDispatch);
                if (receiverOperand is not MirLocalOperand receiver)
                {
                    throw new CompilerInternalException(
                        "虚派发挂起点接收者非局部: " + target.Canonical);
                }
                point.Virtual.ReceiverLocal = receiver.Name;
                point.Virtual.Result = siteResult;
                point.Virtual.ExcTarget = siteExcTarget;
                point.Virtual.OriginalCall = point.Inst;
                // 每 distinct tainted 实现一套协议信息
                var implEntries = new Dictionary<string, CallSiteInfo>(
                    System.StringComparer.Ordinal);
                var armIndex = 0;
                foreach (var (classPlan, implCanonical) in pairs)
                {
                    CallSiteInfo? entry = null;
                    if (tainted.Contains(implCanonical))
                    {
                        if (!implEntries.TryGetValue(implCanonical, out entry))
                        {
                            var callee = mir.Functions.FirstOrDefault(f =>
                                f.Symbol.Canonical == implCanonical)
                                ?? throw new CompilerInternalException(
                                    "tainted 虚实现不在模块函数表: " + implCanonical);
                            var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(
                                implCanonical);
                            var calleeLocal = "$mw.callee." + point.State + "."
                                + armIndex;
                            armIndex++;
                            fn.AddLocal(new MirLocal(calleeLocal,
                                MirType.Of(frameCanonical)));
                            entry = new CallSiteInfo
                            {
                                Callee = callee,
                                CalleeLocal = calleeLocal,
                                CalleeFrameCanonical = frameCanonical,
                                ResumeSymbol = ProxyBakeSupport.SyntheticMember(
                                    "$mw.resume." + implCanonical, owner: null),
                                ResultFieldSymbol = callee.ReturnType.IsVoid
                                    ? null
                                    : SyntheticTypePlanner.FrameFieldSymbol(
                                        frameCanonical, ResultSlotName,
                                        callee.ReturnType.Canonical),
                            };
                            PlanArgDrops(context, fn, entry, callee, callArgs,
                                hostConstructedRef,
                                // R2-a：虚派发臂的运行期 typeid 回退
                                // 源 = 接收者实例（臂命中即 is-a 宿主，
                                // 隐藏字段恒在）
                                receiverOperand);
                            implEntries.Add(implCanonical, entry);
                        }
                    }
                    point.Virtual.Arms.Add(new VirtualArm
                    {
                        InheritanceDepth = DepthOf(classPlan),
                        Impl = entry,
                        TypeRefs = ArmTypeRefsOf(context, classPlan),
                    });
                }
                // 按实际布局基类链排序，闭合祖先和同名不同元数不再丢失。
                point.Virtual.Arms.Sort((a, b) => b.InheritanceDepth.CompareTo(a.InheritanceDepth));
                if (point.Virtual.Arms.All(a => a.Impl == null))
                {
                    throw new CompilerInternalException(
                        "虚派发挂起点闭包内无 tainted 实现: " + target.Canonical);
                }
            }
        }

        // Phase 2.6：yield-alarm 探测站点建臂（PrepareSplit 内、活性分析
        // 之前——探测 callee frame 槽须进 fn.Locals 才被保存槽集收编）。
        // §19.2 语义纠偏：isReady 是普通 Rigi 代码、允许 await/yield——
        // 闭包内存在 tainted 实现时探测改经恢复块站点协议下钻（探测中途
        // 允许挂起、未就绪退回等待）；全 untainted 闭包保持 $mw.poll_probe
        // 同步虚派发廉价路径（ProbeSite 置空）。tainted 实现的 isReady 由
        // 主流程阶段 1 预注册 frame/resume 符号——本方法先于一切 resume
        // 体合成执行，递归安全（PrepareVirtualCallSites 同口径）
        private void PreparePollProbeSites(MwContext context, MirModule mir,
            MirFunction fn, List<SuspensionPoint> points, HashSet<string> tainted)
        {
            var isReady = context.Symbols.FindMember(PollProbeIsReadyCanonical)
                ?? throw new CompilerInternalException(
                    "stdlib PollingAlarm.isReady 符号缺失: " + PollProbeIsReadyCanonical);
            var probeStateBase = points.Count > 0 ? points.Max(p => p.State) : 0;
            foreach (var point in points)
            {
                if (point.Inst is not MirYieldAlarm yieldAlarm)
                {
                    continue;
                }
                var pairs = ClosurePairsOf(context, isReady, operatorDispatch: false);
                var site = new PollProbeSiteInfo
                {
                    AlarmSlot = yieldAlarm.AlarmSlot,
                };
                var implEntries = new Dictionary<string, CallSiteInfo>(
                    System.StringComparer.Ordinal);
                foreach (var (classPlan, implCanonical) in pairs)
                {
                    if (!tainted.Contains(implCanonical))
                    {
                        // untainted 联合臂 type.is 目标集（R2-a 单臂多
                        // 目标口径：模板键 + 闭合构造 sheet 全收）
                        site.UntaintedTypeRefs.AddRange(ArmTypeRefsOf(context, classPlan));
                        continue;
                    }
                    if (implEntries.ContainsKey(implCanonical))
                    {
                        continue;
                    }
                    var callee = mir.Functions.FirstOrDefault(f =>
                        f.Symbol.Canonical == implCanonical)
                        ?? throw new CompilerInternalException(
                            "tainted isReady 实现不在模块函数表: " + implCanonical);
                    var frameCanonical = SyntheticTypePlanner.FrameCanonicalOf(
                        implCanonical);
                    var calleeLocal = "$mw.probe.callee." + point.State + "."
                        + implEntries.Count;
                    fn.AddLocal(new MirLocal(calleeLocal, MirType.Of(frameCanonical)));
                    var entry = new CallSiteInfo
                    {
                        Callee = callee,
                        CalleeLocal = calleeLocal,
                        CalleeFrameCanonical = frameCanonical,
                        ResumeSymbol = ProxyBakeSupport.SyntheticMember(
                            "$mw.resume." + implCanonical, owner: null),
                        // isReady 恒返回 bool（非 void）——$mw.result 字段
                        // 由 PrepareSplit plain 分支追加
                        ResultFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                            frameCanonical, ResultSlotName,
                            callee.ReturnType.Canonical),
                    };
                    // 落参：isReady 实例方法单参 .this = alarm 槽
                    PlanArgDrops(context, fn, entry, callee,
                        new MirOperand[] { new MirLocalOperand(site.AlarmSlot) },
                        hostConstructedRef: null, runtimeTypeIdReceiver: null);
                    implEntries.Add(implCanonical, entry);
                    site.Arms.Add((entry, ArmTypeRefsOf(context, classPlan),
                        DepthOf(classPlan)));
                }
                if (site.Arms.Count == 0)
                {
                    // 闭包内无 tainted 实现：保持廉价路径
                    continue;
                }
                // 臂按基类链深→浅排序（type.is 是子类判定，浅类臂不得
                // 遮蔽深类——PrepareVirtualCallSites 同口径）
                site.Arms.Sort((a, b) => b.Item3.CompareTo(a.Item3));
                site.ProbeState = ++probeStateBase;
                point.ProbeSite = site;
            }
        }

        // R2-a：臂条件 type.is 目标集——非泛型类 = [类 canonical]；
        // 泛型类 = [含元数的模板 PlanKey] + 同声明的闭合构造 canonical
        //（布局计划表枚举；实例头 sheet 只可能是模板空壳或某个已
        // 物化闭合构造，构造链基链覆盖派生类实例——臂只需枚举臂
        // 类自身的构造，派生构造实例沿其构造基链命中）
        private static List<string> ArmTypeRefsOf(MwContext context,
            TypeLayoutPlan classPlan)
        {
            var refs = new List<string> { GenericAbi.PlanKey(classPlan.Symbol) };
            if (context.Layout == null)
            {
                return refs;
            }
            foreach (var plan in context.Layout.Plans)
            {
                var canonical = plan.Symbol.Canonical;
                if (canonical == classPlan.Symbol.Canonical
                    || !Layout.ConstructedTypeCollector.IsConstructed(canonical))
                {
                    continue;
                }
                if (plan.Symbol.Declaration == classPlan.Symbol.Declaration)
                {
                    refs.Add(canonical);
                }
            }
            return refs;
        }

        private static int DepthOf(TypeLayoutPlan plan)
        {
            var depth = 0;
            for (var ancestor = plan.BasePlan; ancestor != null; ancestor = ancestor.BasePlan)
                depth++;
            return depth;
        }

        // §7.2 落参计划：MIR 调用点实参恒不含类级 typeid（class 宿主
        // 被调方自取 / 值类型宿主发射侧合成直传）——实参按「形参剔除
        // 类级 typeid」位序对 zip；被剔除的类级 typeid 按宿主构造形态
        // 合成（闭合实参 → MirGetTypeId 常量；外层占位 → 调用方同名
        // .generic.* 局部转抄；其余形态受控拒绝）
        private static void PlanArgDrops(MwContext context, MirFunction fn,
            CallSiteInfo site, MirFunction callee, IReadOnlyList<MirOperand> args,
            string? hostConstructedRef, MirOperand? runtimeTypeIdReceiver = null)
        {
            var expected = new List<MirLocal>();
            var hiddenTypeIds = new List<MirLocal>();
            foreach (var parameter in callee.Parameters)
            {
                if (GenericAbi.IsClassLevelTypeId(context.Symbols, callee.Symbol, parameter.Name))
                {
                    hiddenTypeIds.Add(parameter);
                }
                else
                {
                    expected.Add(parameter);
                }
            }
            if (args.Count != expected.Count)
            {
                throw new MwNotSupportedException(
                    "B-2 暂不支持实参与可见形参不对应的 tainted 调用（未知隐藏参数形态）: "
                    + callee.Symbol.Canonical);
            }
            for (var i = 0; i < args.Count; i++)
            {
                site.Drops.Add(new ArgDrop
                {
                    FrameFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                        site.CalleeFrameCanonical, expected[i].Name,
                        expected[i].Type.Canonical),
                    Operand = args[i],
                    OperandTargetType = expected[i].Type,
                });
            }
            if (hiddenTypeIds.Count == 0)
            {
                return;
            }
            var owner = callee.Symbol.Owner
                ?? throw new CompilerInternalException(
                    "类级 typeid 形参缺宿主: " + callee.Symbol.Canonical);
            // 宿主构造形态：优先调用点携带（G1 HostConstructedRef），
            // 否则取接收者（首个实参 .this）静态类型；值类型接收者
            // 经 cast/copy 适配成裸模板时沿产出链回溯（同
            // ConcreteColdBodyType 先例）
            var hostRef = hostConstructedRef;
            if (hostRef == null && args.Count > 0
                && args[0] is MirLocalOperand receiver)
            {
                hostRef = fn.FindLocal(receiver.Name).Type.Canonical;
                if (owner.Declaration.GenericParameters.Count > 0
                    && (!Layout.ConstructedTypeCollector.IsConstructed(hostRef)
                        || BilVerificationContext.StripTypeArguments(
                            MwTypeKey.Normalize(hostRef)) != owner.Canonical))
                {
                    hostRef = ResolveConstructedRefBackward(fn, receiver.Name,
                        owner.Canonical) ?? hostRef;
                }
            }
            var substitution = hostRef != null
                && Layout.ConstructedTypeCollector.IsConstructed(hostRef)
                && BilVerificationContext.StripTypeArguments(
                    MwTypeKey.Normalize(hostRef)) == owner.Canonical
                ? Layout.ConstructedTypeCollector.BuildSubstitution(
                    MwTypeKey.Normalize(hostRef), owner.Declaration)
                : null;
            if (substitution == null)
            {
                // 泛型 class 宿主虚派发臂回退（R2-a）：静态构造形态
                // 被接收者 cast 剥成裸模板不可知时，类级 typeid 改从
                // 接收者实例隐藏 typeid 字段运行期读取（class 宿主
                // 对象头恒藏构造实参 typeid；值类型宿主无对象头，
                // 维持受控拒绝）
                if (runtimeTypeIdReceiver == null
                    || owner.Declaration.Kind != BilTypeKind.Class)
                {
                    throw new MwNotSupportedException(
                        "B-2 暂不支持宿主构造形态静态不可知的 tainted 调用（类级 typeid 无法合成）: "
                        + callee.Symbol.Canonical);
                }
                foreach (var parameter in hiddenTypeIds)
                {
                    var drop = new ArgDrop
                    {
                        FrameFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                            site.CalleeFrameCanonical, parameter.Name,
                            parameter.Type.Canonical),
                    };
                    // 嵌套类外层宿主链 GP（review-20260910 #02）：class
                    // 实例只物化自身隐藏 typeid 槽，外层 GP 无接收者来源
                    // ——落 Any 常量（被调方 prologue 同口径兜底）
                    if (owner.Declaration.GenericParameters.Contains(
                        parameter.Name.Substring(".generic.".Length)))
                    {
                        drop.ReceiverTypeIdOwner = GenericAbi.PlanKey(owner);
                        drop.ReceiverTypeIdParameter =
                            parameter.Name.Substring(".generic.".Length);
                        drop.ReceiverTypeIdOperand = runtimeTypeIdReceiver;
                    }
                    else
                    {
                        drop.TypeIdTypeRef = "core::Any";
                    }
                    site.Drops.Add(drop);
                }
                return;
            }
            foreach (var parameter in hiddenTypeIds)
            {
                // 嵌套类外层宿主链 GP（review-20260910 #02）：自身构造
                // 形态实参只覆盖自身 GP，外层 GP 无替换来源——落 Any
                // 常量（被调方 prologue 同口径兜底）
                if (!substitution.TryGetValue(parameter.Name.Substring(".generic.".Length),
                        out var typeArg))
                {
                    site.Drops.Add(new ArgDrop
                    {
                        FrameFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                            site.CalleeFrameCanonical, parameter.Name,
                            parameter.Type.Canonical),
                        TypeIdTypeRef = "core::Any",
                    });
                    continue;
                }
                var drop = new ArgDrop
                {
                    FrameFieldSymbol = SyntheticTypePlanner.FrameFieldSymbol(
                        site.CalleeFrameCanonical, parameter.Name,
                        parameter.Type.Canonical),
                };
                if (GenericAbi.TryPlaceholderName(typeArg, out var placeholder))
                {
                    // 外层占位 → 调用方同名 .generic.* 局部转抄（该局部
                    // 恒入保存槽集——.generic.* 恒活跃规则）
                    var callerLocal = ".generic." + placeholder;
                    if (!fn.TryFindLocal(callerLocal, out _))
                    {
                        throw new MwNotSupportedException(
                            "B-2 暂不支持类级 typeid 占位在调用方无同名局部的 tainted 调用: "
                            + callee.Symbol.Canonical);
                    }
                    drop.CallerTypeIdLocal = callerLocal;
                }
                else if (!typeArg.Contains(".generic<", System.StringComparison.Ordinal))
                {
                    drop.TypeIdTypeRef = typeArg;
                }
                else
                {
                    // class 实例已经保存完整实参身份；嵌套开放类型也从
                    // 接收者读取，不能按调用方同名 T 猜测或退化为 Any。
                    if (owner.Declaration.Kind != BilTypeKind.Class || args.Count == 0)
                        throw new MwNotSupportedException(
                            "B-2 嵌套开放类级 typeid 缺少 class 接收者: " + callee.Symbol.Canonical);
                    drop.ReceiverTypeIdOwner = GenericAbi.PlanKey(owner);
                    drop.ReceiverTypeIdParameter = parameter.Name.Substring(".generic.".Length);
                    drop.ReceiverTypeIdOperand = runtimeTypeIdReceiver ?? args[0];
                }
                site.Drops.Add(drop);
            }
        }

        // 接收者槽的构造形态沿产出链回溯（cast/copy 适配会剥成裸模
        // 板——泛型身份在源槽上；同 ConcreteColdBodyType 跳链先例，
        // 8 跳环保护）
        private static string? ResolveConstructedRefBackward(MirFunction fn,
            string localName, string ownerCanonical)
        {
            var slot = localName;
            for (var hop = 0; hop < 8; hop++)
            {
                string? copiedFrom = null;
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
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
                    return null;
                }
                var sourceType = fn.FindLocal(copiedFrom).Type.Canonical;
                if (Layout.ConstructedTypeCollector.IsConstructed(sourceType)
                    && BilVerificationContext.StripTypeArguments(
                        MwTypeKey.Normalize(sourceType)) == ownerCanonical)
                {
                    return sourceType;
                }
                slot = copiedFrom;
            }
            return null;
        }

        private void ExecuteSplit(MwContext context, MirModule mir, SplitPlan plan)
        {
            var fn = plan.Fn;
            string FieldOf(MirLocal local) => SyntheticTypePlanner.FrameFieldSymbol(
                plan.FrameCanonical, local.Name, local.Type.Canonical);

            var resumeFn = BuildResumeFunction(context, mir, plan, FieldOf);
            mir.AddFunction(resumeFn);
            if (plan.Mode == SplitMode.Tasked)
            {
                if (IsZeroArgAsyncCall(fn))
                {
                    var thisLocal = fn.Parameters.FirstOrDefault(p => p.Name == ".this")
                        ?? throw new CompilerInternalException(
                            "0 参 $$call 缺 .this: " + fn.Symbol.Canonical);
                    _coldBinds.Add(new ColdBindEntry
                    {
                        OwnerCanonical = fn.Symbol.Owner!.Canonical,
                        CallCanonical = fn.Symbol.Canonical,
                        ResumeSymbol = plan.ResumeSymbol,
                        FrameCanonical = plan.FrameCanonical,
                        FrameType = plan.FrameType,
                        FrameInit = plan.FrameInit,
                        TaskTypeRef = plan.TaskTypeRef!,
                        TaskConstructionRef = plan.TaskConstructionRef!,
                        ThisType = thisLocal.Type,
                    });
                }
                ReplaceWithStub(context, mir, fn, plan.FrameType, plan.FrameMirType,
                    plan.FrameInit, plan.ResumeSymbol, plan.TaskTypeRef!,
                    plan.TaskFieldSymbol!, FieldOf);
            }
            else
            {
                ReplaceWithTrap(mir, fn);
            }

            // 自检②③：state 分发表项恰覆盖 入口+挂起点；frame 字段 =
            // state + 保存槽 + $mw.task（Tasked）/ $mw.result?（Plain）
            var layoutPlan = context.Layout!.Find(plan.FrameCanonical)
                ?? throw new CompilerInternalException("frame 布局计划缺失: " + plan.FrameCanonical);
            var expected = plan.SavedSlots.Count + 1
                + (plan.Mode == SplitMode.Tasked ? 1 : 0)
                + (plan.ResultFieldSymbol != null ? 1 : 0);
            if (layoutPlan.Fields.Count != expected)
            {
                throw new CompilerInternalException(
                    $"CoroutineSplit 自检失败：{fn.Symbol.Canonical} frame 字段数 {layoutPlan.Fields.Count} ≠ 保存槽 {plan.SavedSlots.Count} + state + task/result");
            }
        }

        // plain tainted fn 的原符号陷阱 stub：全部调用点已协议化
        //（taint 闭包 + 调用点改写），直调残留属内部错误——陷阱体
        // 仅作防御（模块内符号保留，Emit 零特例）
        private static void ReplaceWithTrap(MirModule mir, MirFunction fn)
        {
            var trap = new MirFunction(fn.Symbol, fn.ReturnType, fn.Parameters,
                new List<MirLocal>(fn.Parameters),
                new List<MirBlock>
                {
                    new MirBlock("entry", new List<MirInst>(), new MirUnreachable()),
                }, fn.IsEntrypoint);
            var index = mir.FunctionList.IndexOf(fn);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "CoroutineSplit：tainted fn 不在模块函数表: " + fn.Symbol.Canonical);
            }
            mir.FunctionList[index] = trap;
        }

        // ===== B-1 根驱动：$mw.main.settle 合成 =====
        // tainted main 的 Task 包装 split 后，rigi_entry 在 drain 至
        // quiescence 之后调本 fn 取 main 结果/重抛 main 失败（对齐 VM
        // BilVm.Run 的 main.Failure 优先汇总）：failureNodeId != 0 →
        // MirFailureLoad 取异常 + MirThrow（pending 置位，rigi_entry
        // 收进 entry.exc 进 reporter）；否则解包 result 字段返回
        //（void main 恒 0）
        public static string MainSettleCanonicalOf(string taskTypeRef) =>
            "$mw.main.settle(task:" + taskTypeRef + ")@.i32";

        private void SynthesizeMainSettle(MwContext context, MirModule mir,
            MirFunction mainFn)
        {
            var taskTypeRef = TaskTypeRefOf(mainFn.ReturnType);
            var symbol = ProxyBakeSupport.SyntheticMember(
                MainSettleCanonicalOf(taskTypeRef), owner: null);
            var taskParam = new MirLocal("task", MirType.Of(taskTypeRef));
            var settle = new MirFunction(symbol, I32,
                new List<MirLocal> { taskParam }, new List<MirLocal> { taskParam },
                new List<MirBlock>(), false);
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(settle, prefix, type);
            var taskOp = new MirLocalOperand("task");
            var nodeId = Fresh("$mw.settle.nid.", I64);
            var zero = Fresh("$mw.settle.z.", I64);
            var isFail = Fresh("$mw.settle.isf.", Bool);
            settle.AddBlock(new MirBlock("entry", new List<MirInst>
            {
                new MirGetField(taskOp, TaskField(taskTypeRef, "failureNodeId", ".i64"),
                    nodeId),
                new MirLoadResource(ProxyWildcardAbi.AddI64Resource(context, 0), zero),
                new MirBinaryIntrinsic(BilBinaryOp.CmpNe, new MirLocalOperand(nodeId),
                    new MirLocalOperand(zero), I64, I64, Bool, isFail),
            }, new MirCondBranch(new MirLocalOperand(isFail), "mw.settle.fail",
                "mw.settle.ok")));

            var okInsts = new List<MirInst>();
            var okResult = Fresh("$mw.settle.r.",
                mainFn.ReturnType.IsVoid ? I32 : mainFn.ReturnType);
            if (!mainFn.ReturnType.IsVoid)
            {
                var rn = Fresh("$mw.settle.rn.",
                    MirType.Of(".nullable<" + mainFn.ReturnType.Canonical + ">"));
                okInsts.Add(new MirGetField(taskOp,
                    TaskField(taskTypeRef, "result",
                        ".nullable<" + mainFn.ReturnType.Canonical + ">"), rn));
                okInsts.Add(new MirUnwrapNullable(new MirLocalOperand(rn),
                    mainFn.ReturnType, okResult));
            }
            else
            {
                okInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, 0), okResult));
            }
            settle.AddBlock(new MirBlock("mw.settle.ok", okInsts,
                new MirRet(new MirLocalOperand(okResult))));

            var exc = Fresh("$mw.settle.exc.", Any);
            settle.AddBlock(new MirBlock("mw.settle.fail", new List<MirInst>
            {
                new MirFailureLoad(nodeId, exc),
                new MirThrow(new MirLocalOperand(exc), null),
            }, new MirRetThrow()));
            mir.AddFunction(settle);
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
            var identity = GenericTaskSheetIdentity(taskTypeRef, template);
            // 状态机切分会引入 BIL 中尚未出现的 Task<R>（例如被污染的
            // 同步返回函数）。为该实际构造补布局，不能依赖发射器回退模板。
            if (GenericAbi.IsClosedConstructed(identity) && context.Layout != null)
                ConstructedLayout.ResolveConstructed(identity, context.Symbols, context.Layout,
                    new HashSet<string>(System.StringComparer.Ordinal),
                    new HashSet<string>(context.Module.Functions.Select(f => f.Symbol),
                        System.StringComparer.Ordinal));
            return new MwTypeSymbol(identity, template);
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
        // plan.Mode = Tasked：frame 带 $mw.task，挂起/恢复做 TaskState
        // 投影（markSuspended/markRunnable），DONE 尾走 Task 终态序列；
        // plan.Mode = Plain（B-1 tainted 普通 fn）：无 Task——投影跳过，
        // DONE 尾只写 $mw.result + ret DONE（frame 所有权归调用方，
        // 不做最终 release），失败经 RcInjection plain 垫尾 ret FAILED
        private MirFunction BuildResumeFunction(MwContext context, MirModule mir,
            SplitPlan plan, System.Func<MirLocal, string> fieldOf)
        {
            var fn = plan.Fn;
            var points = plan.Points;
            var savedSlots = plan.SavedSlots;
            var paramNames = plan.ParamNames;
            var stateFieldSymbol = plan.StateFieldSymbol;
            var taskFieldSymbol = plan.TaskFieldSymbol;
            var taskTypeRef = plan.TaskTypeRef;
            // 局部表：frame 参数 + 原 fn 全部局部副本（同名同型；参数在
            // resume fn 是普通局部，state 0 从 frame 恢复）
            var frameParam = new MirLocal(FrameParamName, plan.FrameMirType);
            var locals = new List<MirLocal> { frameParam };
            foreach (var local in fn.Locals)
            {
                locals.Add(new MirLocal(local.Name, local.Type));
            }
            var resumeFn = new MirFunction(plan.ResumeSymbol, I32,
                new List<MirLocal> { frameParam }, locals, new List<MirBlock>(),
                false, isCoroutineResume: true,
                isPlainResume: plan.Mode == SplitMode.Plain);
            if (plan.Mode == SplitMode.Plain) resumeFn.RestoredEntrySource = fn.Symbol.Canonical;
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
                if (point.ProbeSite != null)
                {
                    // Phase 2.6：探测挂起子状态——isReady 状态机中途挂起
                    // 后重发布恢复的专用入口（区别于未就绪退回等待后的
                    // 再次首探：那条路 state 保持 N 重入 poll gate）
                    itemTargets.Add("mw.state." + point.ProbeSite.ProbeState);
                    tableElements.Add(point.ProbeSite.ProbeState.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                }
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
            // 或 yield-alarm 探测块 / 调用块（tainted 直调点）/ 原后继
            //（裸 yield / Mutex.enter）
            foreach (var point in points)
            {
                var restoreInsts = new List<MirInst>();
                EmitRestore(restoreInsts, point.LiveAfter);
                string resumeTarget;
                if (point.CallSite != null || point.InitSite != null)
                {
                    // B-1：重回调用块——callee frame 已从本 frame 的
                    // callee 槽恢复，MirResumeCall 再下钻
                    resumeTarget = CallBlockId(point);
                }
                else if (point.Virtual != null)
                {
                    // B-2：重回恢复分流链（type.is 重判——接收者已从
                    // frame 恢复，运行期类型不变必命中同臂）直落对应
                    // 实现的调用块再下钻
                    resumeTarget = VirtualResumeDispatchId(point);
                }
                else if (point.IndirectInit != null)
                {
                    // R2-c：重回恢复分流链（IsTypeId 重判——typeid
                    // 已从 frame 恢复，值不变必命中同臂）直落对应
                    // 臂的调用块再下钻
                    resumeTarget = IndirectInitResumeDispatchId(point);
                }
                else if (point.Inst is MirAwait)
                {
                    resumeTarget = WaitBlockId(point);
                }
                else if (point.Inst is MirYieldAlarm)
                {
                    // 棒5a：先经轮询判位分流——EventAlarm 响铃恢复直续
                    // 原后继；PollingAlarm 重发布恢复先探测
                    resumeTarget = PollGateBlockId(point);
                    // TaskState 投影仅 Tasked（plain 无 Task 可投影）
                    if (taskFieldSymbol != null)
                    {
                        EmitRestoreMark(context, restoreInsts, frameOp,
                            taskFieldSymbol, taskTypeRef!, Fresh);
                    }
                    resumeFn.AddBlock(new MirBlock("mw.state." + point.State, restoreInsts,
                        new MirBranch(resumeTarget)));
                    if (point.ProbeSite != null)
                    {
                        // Phase 2.6：探测挂起子状态 N' 恢复块——活跃槽
                        //（含探测 callee frame）从本层 frame 恢复后重回
                        // 探测恢复分流链，isReady 从挂起点续跑至完成再做
                        // 一次性就绪判定（对齐 VM 恢复式探测语义）
                        var probeRestore = new List<MirInst>();
                        EmitRestore(probeRestore, point.LiveAfter);
                        if (taskFieldSymbol != null)
                        {
                            EmitRestoreMark(context, probeRestore, frameOp,
                                taskFieldSymbol, taskTypeRef!, Fresh);
                        }
                        resumeFn.AddBlock(new MirBlock(
                            "mw.state." + point.ProbeSite.ProbeState, probeRestore,
                            new MirBranch(PollProbeResumeDispatchId(point))));
                    }
                    EmitPollGate(context, mir, resumeFn, fn, point, frameOp,
                        stateFieldSymbol, taskFieldSymbol, taskTypeRef, syms,
                        EmitSave);
                    continue;
                }
                else
                {
                    // 裸 yield / Mutex.enter：恢复直落原后继
                    resumeTarget = ContBlockId(point);
                }
                // TaskState 投影是 Task 持有者的可观察性；plain resume
                // 无 Task 可投影（对齐 VM：无 TaskObject 的协程不投影）
                if (taskFieldSymbol != null)
                {
                    EmitRestoreMark(context, restoreInsts, frameOp,
                        taskFieldSymbol, taskTypeRef!, Fresh);
                }
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
                    RewriteReturn(context, mir, resumeFn, block, plan, frameOp, syms);
                    resumeFn.AddBlock(block);
                    continue;
                }
                var segmentStart = 0;
                var currentId = block.Id;
                foreach (var point in blockPoints)
                {
                    var headInsts = block.Instructions
                        .Take(point.InstIndex).Skip(segmentStart).ToList();
                    if (point.CallSite != null)
                    {
                        EmitCallSplit(context, resumeFn, fn, point, currentId,
                            headInsts, frameOp, stateFieldSymbol, taskFieldSymbol,
                            taskTypeRef, syms, EmitSave, I32Const, Fresh);
                    }
                    else if (point.InitSite != null)
                    {
                        EmitInitSplit(context, mir, resumeFn, fn, point, currentId,
                            headInsts, frameOp, stateFieldSymbol, taskFieldSymbol,
                            taskTypeRef, syms, EmitSave, I32Const, Fresh);
                    }
                    else if (point.Virtual != null)
                    {
                        EmitVirtualCallSplit(context, resumeFn, fn, point, currentId,
                            headInsts, frameOp, stateFieldSymbol, taskFieldSymbol,
                            taskTypeRef, syms, EmitSave, I32Const, Fresh);
                    }
                    else if (point.IndirectInit != null)
                    {
                        EmitNewIndirectSplit(context, mir, resumeFn, fn, point,
                            currentId, headInsts, frameOp, stateFieldSymbol,
                            taskFieldSymbol, taskTypeRef, syms, EmitSave, I32Const,
                            Fresh);
                    }
                    else if (point.Inst is MirAwait awaitInst)
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
                RewriteReturn(context, mir, resumeFn, tail, plan, frameOp, syms);
                resumeFn.AddBlock(tail);
            }
            return resumeFn;
        }

        private static string WaitBlockId(SuspensionPoint point) =>
            point.Block.Id + ".wait" + point.State;

        private static string ContBlockId(SuspensionPoint point) =>
            point.Block.Id + ".cont" + point.State;

        private static string CallBlockId(SuspensionPoint point) =>
            point.Block.Id + ".call" + point.State;

        private static string VirtualResumeDispatchId(SuspensionPoint point) =>
            point.Block.Id + ".call" + point.State + ".vrdisp";

        private static string IndirectInitResumeDispatchId(SuspensionPoint point) =>
            point.Block.Id + ".call" + point.State + ".nrdisp";

        private static string PollGateBlockId(SuspensionPoint point) =>
            point.Block.Id + ".pollgate" + point.State;

        // Phase 2.6：探测挂起子状态的恢复分流链块（type.is 重判直落对应
        // 实现的探测调用块）
        private static string PollProbeResumeDispatchId(SuspensionPoint point) =>
            PollGateBlockId(point) + ".pdisp";

        // Phase 2.6：探测站点臂块 id（首入建帧 / 双入口调用块 / 就绪判定）
        private static string ProbeNewBlockId(string gateId, int arm) =>
            gateId + ".pnew" + arm;

        private static string ProbeCallBlockId(string gateId, int arm) =>
            gateId + ".pcall" + arm;

        private static string ProbeDoneBlockId(string gateId, int arm) =>
            gateId + ".pdone" + arm;

        // B-1 tainted→tainted 直调改写（调用点是调用方的挂起点；对齐
        // VM 栈式模型——挂起的是整条帧链，恢复沿链逐层下钻）：
        //   head（原位置）：建 callee frame → 实参按形参序落 callee
        //     frame 字段 → 存活跃槽（含 callee 槽）+ state=N（先于下
        //     钻——callee 挂起时本层 frame 必须已可恢复）→ 落调用块；
        //   调用块（首入与恢复共用）：MirResumeCall 原生栈下钻 callee
        //     resume → 四码分流（0=SUSPENDED/1=YIELDED 上传同码 /
        //     2=DONE 读 callee frame.$mw.result 续行 / 3=FAILED 取
        //     pending 沿原 ExcTarget 重抛）。
        // callee frame 所有权归本层（callee 槽持 +1；callee resume 借
        // 用约定不做最终 release）
        private void EmitCallSplit(MwContext context, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var info = point.CallSite!;
            // B-2：MirCall / MirSuperCall 同协议（静态唯一目标）
            var (result, excTarget) = point.Inst switch
            {
                MirCall call => (call.Result, call.ExcTarget),
                MirSuperCall superCall => (superCall.Result, superCall.ExcTarget),
                _ => throw new CompilerInternalException(
                    "EmitCallSplit 非调用挂起点: " + point.Inst.GetType().Name),
            };
            var callId = CallBlockId(point);
            var doneId = callId + ".done";
            var propId = callId + ".prop";
            var failId = callId + ".fail";
            var calleeOp = new MirLocalOperand(info.CalleeLocal);
            var frameType = context.Symbols.FindType(info.CalleeFrameCanonical)
                ?? throw new CompilerInternalException(
                    "tainted callee frame 未预注册: " + info.CalleeFrameCanonical);
            var frameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(info.CalleeFrameCanonical))
                ?? throw new CompilerInternalException(
                    "tainted callee frame init 缺失: " + info.CalleeFrameCanonical);

            headInsts.Add(new MirNewObject(frameType, null, frameInit,
                new List<MirOperand>(), info.CalleeLocal));
            // §7.2 落参计划（B-2）：直落实参 / 常量 typeid 合成 /
            // 调用方 .generic.* 局部转抄
            foreach (var drop in info.Drops)
            {
                if (drop.Operand != null)
                {
                    headInsts.Add(new MirSetField(drop.Operand, calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.TypeIdTypeRef != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.ReceiverTypeIdOwner != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand!,
                        drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else
                {
                    headInsts.Add(new MirSetField(
                        new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                        drop.FrameFieldSymbol));
                }
            }
            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(callId)));

            var code = fresh("$mw.call.code.", I32);
            var codeTable = new BilSwitchTableResource(
                "$mw.coroutine.call." + _resourceCounter++, ".i32",
                new[] { "0", "1", "2", "3" });
            context.Module.Resources.Add(codeTable);
            resumeFn.AddBlock(new MirBlock(callId, new List<MirInst>
            {
                new MirResumeCall(info.ResumeSymbol, info.CalleeLocal, code),
            }, new MirSwitch(new MirLocalOperand(code), codeTable,
                new[] { propId, propId, doneId, failId }, "mw.state.bad")));

            // DONE：借用 callee frame 读 $mw.result 续行
            var doneInsts = new List<MirInst>();
            if (result != null)
            {
                if (info.ResultFieldSymbol == null)
                {
                    throw new CompilerInternalException(
                        "tainted 调用有结果槽但 callee 无 $mw.result: "
                        + info.Callee.Symbol.Canonical);
                }
                doneInsts.Add(new MirGetField(calleeOp, info.ResultFieldSymbol,
                    result));
            }
            resumeFn.AddBlock(new MirBlock(doneId, doneInsts,
                new MirBranch(ContBlockId(point))));

            // SUSPENDED/YIELDED 上传：frame 已写完，直返同码（Tasked
            // 调用方先投影自身 Task Suspended——VM 无 TaskObject 的
            // 协程不投影，plain 调用方同跳过）
            var propInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(propId, propInsts,
                new MirRet(new MirLocalOperand(code))));

            // FAILED：pending 已在 TLS（callee plain 垫尾保持置位），
            // 取走重抛沿原 ExcTarget（对齐普通调用抛出语义；null 由
            // RcInjection 进本 fn 传播垫继续上传/吸收）
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), excTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                excTarget != null
                    ? (MirTerminator)new MirBranch(excTarget.Id)
                    : new MirRetThrow()));
        }

        // B-2 含挂起点 init 的构造改写（对齐 VM New 语义——分配 →
        // init.wrapper 缝合 → init 调用；init 挂起 = 整条帧链挂起）：
        //   head（原位置）：MirNewObject 分配（init.wrapper 原位缝合
        //     + 合成空 init——真 init 不在此跑）→ Target 槽落定 →
        //     建 init frame（.this = Target）+ 落参 → 存活跃槽 +
        //     state=N → 落调用块；
        //   调用块（首入与恢复共用）：MirResumeCall 下钻 init resume
        //     → 四码分流（0/1 上传 / 2 DONE 直落原后继——结果即
        //     Target 槽本身，无 $mw.result 可读 / 3 取 pending 沿原
        //     MirNewObject.ExcTarget 重抛——半构造对象随本层托管槽
        //     配平释放，对齐「init 内抛出释放新建实例」口径）。
        private void EmitInitSplit(MwContext context, MirModule mir, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var initSite = point.InitSite!;
            var info = initSite.Site;
            var original = initSite.Original;
            var callId = CallBlockId(point);
            var doneId = callId + ".done";
            var propId = callId + ".prop";
            var failId = callId + ".fail";
            var calleeOp = new MirLocalOperand(info.CalleeLocal);
            var frameType = context.Symbols.FindType(info.CalleeFrameCanonical)
                ?? throw new CompilerInternalException(
                    "tainted init frame 未预注册: " + info.CalleeFrameCanonical);
            var frameInit = context.Symbols.FindMember(
                SyntheticTypePlanner.FrameInitCanonicalOf(info.CalleeFrameCanonical))
                ?? throw new CompilerInternalException(
                    "tainted init frame init 缺失: " + info.CalleeFrameCanonical);

            // 分配（空 init；init.wrapper/WrapperArgs/ExcTarget 原位）
            headInsts.Add(new MirNewObject(original.Type, original.InitWrapper,
                EmptyCtorInit(context, mir, original.Type), new List<MirOperand>(),
                original.Target, original.WrapperArgs, original.ExcTarget));
            // init frame：.this = 新建对象 + 用户实参/类级 typeid 落参
            headInsts.Add(new MirNewObject(frameType, null, frameInit,
                new List<MirOperand>(), info.CalleeLocal));
            foreach (var drop in info.Drops)
            {
                if (drop.Operand != null)
                {
                    headInsts.Add(new MirSetField(drop.Operand, calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.TypeIdTypeRef != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else if (drop.ReceiverTypeIdOwner != null)
                {
                    var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                    headInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand!,
                        drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                    headInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                        drop.FrameFieldSymbol));
                }
                else
                {
                    headInsts.Add(new MirSetField(
                        new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                        drop.FrameFieldSymbol));
                }
            }
            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(callId)));

            var code = fresh("$mw.call.code.", I32);
            var codeTable = new BilSwitchTableResource(
                "$mw.coroutine.call." + _resourceCounter++, ".i32",
                new[] { "0", "1", "2", "3" });
            context.Module.Resources.Add(codeTable);
            resumeFn.AddBlock(new MirBlock(callId, new List<MirInst>
            {
                new MirResumeCall(info.ResumeSymbol, info.CalleeLocal, code),
            }, new MirSwitch(new MirLocalOperand(code), codeTable,
                new[] { propId, propId, doneId, failId }, "mw.state.bad")));

            // DONE：结果即 Target 槽（head 已落定），直落原后继
            resumeFn.AddBlock(new MirBlock(doneId, new List<MirInst>(),
                new MirBranch(ContBlockId(point))));

            // SUSPENDED/YIELDED 上传（同直调口径）
            var propInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(propId, propInsts,
                new MirRet(new MirLocalOperand(code))));

            // FAILED：pending 已在 TLS，取走重抛沿原构造异常边
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), original.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                original.ExcTarget != null
                    ? (MirTerminator)new MirBranch(original.ExcTarget.Id)
                    : new MirRetThrow()));
        }

        // R2-c new.indirect × tainted class init 的构造改写（分发点
        // 本身成为调用方挂起点；对齐 VM New 语义——分配 → init.wrapper
        // 缝合 → init 调用；init 挂起 = 整条帧链挂起）：
        //   head（原位置）：存活跃槽 + state=N → 精确 sheet 分流链；
        //   分流链（首入/恢复共用生成器）：臂条件 IsTypeId(臂 sheet)
        //     ∧ ¬IsTypeId(各派生排除 sheet)——分发器不继承 init，
        //     排除派生后等价精确相等（typeid 恢复后不变必命中同臂）；
        //   臂（首入）：以臂构造形态 MirNewObject 空 init 分配
        //     （init.wrapper 原位缝合字段初始值）→ Target 槽落定并
        //     回存本层 frame（head 的 emitSave 先于分流执行）→ 建
        //     init frame（.this = Target）+ 落参 → callee 槽回存；
        //   调用块（首入与恢复共用）：MirResumeCall 下钻 → 四码分流
        //     （0/1 上传 / 2 DONE 直落原后继——结果即 Target 槽 /
        //     3 取 pending 沿原 MirNewIndirect.ExcTarget 重抛——半
        //     构造对象随本层托管槽配平释放）；
        //   默认臂：原 MirNewIndirect 直落续行（同步分发器路径——
        //     运行期目标必非 tainted init 或 NoSuchMethod，语义保持）。
        // init frame 所有权归本层（callee 槽持 +1；resume 借用约定
        // 不做最终 release）
        private void EmitNewIndirectSplit(MwContext context, MirModule mir,
            MirFunction resumeFn, MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var site = point.IndirectInit!;
            var callId = CallBlockId(point);
            var dispatchId = callId + ".ndisp";
            var defaultId = callId + ".ndflt";
            var propSuspendId = callId + ".nprops";
            var propYieldId = callId + ".npropy";
            var failId = callId + ".nfail";
            var typeIdOp = new MirLocalOperand(site.TypeIdLocal);
            var targetOp = new MirLocalOperand(site.TargetLocal);
            var callerFrameCanonical = resumeFn.FindLocal(FrameParamName).Type.Canonical;
            var targetFrameField = SyntheticTypePlanner.FrameFieldSymbol(
                callerFrameCanonical, site.TargetLocal,
                fn.FindLocal(site.TargetLocal).Type.Canonical);

            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts,
                new MirBranch(dispatchId)));

            var armBlockIds = new List<(string New, string Call, string Done)>();
            for (var k = 0; k < site.Arms.Count; k++)
            {
                armBlockIds.Add((callId + ".nnew" + k, callId + ".ncall" + k,
                    callId + ".ndone" + k));
            }

            // 分流链（首入/恢复共用生成器；resume=true 时臂直落调
            // 用块、默认臂防御不可达——挂起前提即首入命中某臂，
            // typeid 不变）
            void EmitDispatchChain(string chainId, string checkPrefix, bool resume)
            {
                for (var k = 0; k < site.Arms.Count; k++)
                {
                    var arm = site.Arms[k];
                    var checkId = k == 0 ? chainId : checkPrefix + (k - 1) + ".next";
                    var hitId = resume ? armBlockIds[k].Call : armBlockIds[k].New;
                    var missId = k + 1 < site.Arms.Count
                        ? checkPrefix + k + ".next"
                        : (resume ? "mw.state.bad" : defaultId);
                    var armCond = fresh("$mw.nchk.", Bool);
                    resumeFn.AddBlock(new MirBlock(checkId, new List<MirInst>
                    {
                        new MirTypeCheck(MirTypeCheckKind.IsTypeId, typeIdOp,
                            arm.SheetCanonical, null, armCond),
                    }, new MirCondBranch(new MirLocalOperand(armCond),
                        arm.ExclusionSheets.Count == 0 ? hitId : checkId + ".x0",
                        missId)));
                    for (var x = 0; x < arm.ExclusionSheets.Count; x++)
                    {
                        var exCond = fresh("$mw.nchk.", Bool);
                        var isLast = x + 1 >= arm.ExclusionSheets.Count;
                        resumeFn.AddBlock(new MirBlock(checkId + ".x" + x,
                            new List<MirInst>
                            {
                                new MirTypeCheck(MirTypeCheckKind.IsTypeId, typeIdOp,
                                    arm.ExclusionSheets[x], null, exCond),
                            }, new MirCondBranch(new MirLocalOperand(exCond), missId,
                                isLast ? hitId : checkId + ".x" + (x + 1))));
                    }
                }
            }
            EmitDispatchChain(dispatchId, callId + ".nc", resume: false);
            EmitDispatchChain(IndirectInitResumeDispatchId(point), callId + ".nrc",
                resume: true);

            // 默认臂：原 new.indirect 直落续行
            resumeFn.AddBlock(new MirBlock(defaultId,
                new List<MirInst> { site.Original },
                new MirBranch(ContBlockId(point))));

            // 每臂：分配 + 建 init frame + 落参（首入）→ 调用块四码
            for (var k = 0; k < site.Arms.Count; k++)
            {
                var arm = site.Arms[k];
                var ids = armBlockIds[k];
                var impl = arm.Impl;
                var calleeOp = new MirLocalOperand(impl.CalleeLocal);
                var frameType = context.Symbols.FindType(impl.CalleeFrameCanonical)
                    ?? throw new CompilerInternalException(
                        "tainted init frame 未预注册: " + impl.CalleeFrameCanonical);
                var frameInit = context.Symbols.FindMember(
                    SyntheticTypePlanner.FrameInitCanonicalOf(impl.CalleeFrameCanonical))
                    ?? throw new CompilerInternalException(
                        "tainted init frame init 缺失: " + impl.CalleeFrameCanonical);
                var newInsts = new List<MirInst>
                {
                    // 分配（空 init；init.wrapper/异常边原位——字段初
                    // 始值缝合与半构造抛出语义同静态构造点）
                    new MirNewObject(arm.AllocType, arm.InitWrapper,
                        EmptyCtorInit(context, mir, arm.AllocType),
                        new List<MirOperand>(), site.TargetLocal, null,
                        site.ExcTarget),
                    // Target 回存本层 frame（head 的 emitSave 先于分
                    // 流执行，槽位尚为旧值——恢复块从本层 frame 恢复）
                    new MirSetField(targetOp, frameOp, targetFrameField),
                    new MirNewObject(frameType, null, frameInit,
                        new List<MirOperand>(), impl.CalleeLocal),
                };
                foreach (var drop in impl.Drops)
                {
                    if (drop.Operand != null)
                    {
                        newInsts.Add(new MirSetField(drop.Operand, calleeOp,
                            drop.FrameFieldSymbol));
                    }
                    else if (drop.TypeIdTypeRef != null)
                    {
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                        newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                            calleeOp, drop.FrameFieldSymbol));
                    }
                    else if (drop.ReceiverTypeIdOwner != null)
                    {
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand!,
                            drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                        newInsts.Add(new MirSetField(new MirLocalOperand(tid), calleeOp,
                            drop.FrameFieldSymbol));
                    }
                    else
                    {
                        newInsts.Add(new MirSetField(
                            new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                            drop.FrameFieldSymbol));
                    }
                }
                // callee 槽回存本层 frame（同 Target 回存理由）
                newInsts.Add(new MirSetField(calleeOp, frameOp,
                    SyntheticTypePlanner.FrameFieldSymbol(callerFrameCanonical,
                        impl.CalleeLocal, impl.CalleeFrameCanonical)));
                resumeFn.AddBlock(new MirBlock(ids.New, newInsts,
                    new MirBranch(ids.Call)));

                var code = fresh("$mw.call.code.", I32);
                var codeTable = new BilSwitchTableResource(
                    "$mw.coroutine.call." + _resourceCounter++, ".i32",
                    new[] { "0", "1", "2", "3" });
                context.Module.Resources.Add(codeTable);
                resumeFn.AddBlock(new MirBlock(ids.Call, new List<MirInst>
                {
                    new MirResumeCall(impl.ResumeSymbol, impl.CalleeLocal, code),
                }, new MirSwitch(new MirLocalOperand(code), codeTable,
                    new[] { propSuspendId, propYieldId, ids.Done, failId },
                    "mw.state.bad")));

                // DONE：结果即 Target 槽（臂内已落定），直落原后继
                resumeFn.AddBlock(new MirBlock(ids.Done, new List<MirInst>(),
                    new MirBranch(ContBlockId(point))));
            }

            // SUSPENDED/YIELDED 上传（各臂调用块共用两块——上传码
            // 是常量；Tasked 调用方先投影自身 Task Suspended）
            void EmitPropBlock(string blockId, int resumeCode)
            {
                var propInsts = new List<MirInst>();
                if (taskFieldSymbol != null)
                {
                    EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                        taskTypeRef!, "markSuspended", fresh);
                }
                var propCode = fresh("$mw.code.", I32);
                propInsts.Add(i32Const(resumeCode, propCode));
                resumeFn.AddBlock(new MirBlock(blockId, propInsts,
                    new MirRet(new MirLocalOperand(propCode))));
            }
            EmitPropBlock(propSuspendId, ResumeSuspended);
            EmitPropBlock(propYieldId, ResumeYielded);

            // FAILED（共用）：pending 已在 TLS（callee plain 垫尾保
            // 持置位），取走重抛沿原构造异常边
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), site.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                site.ExcTarget != null
                    ? (MirTerminator)new MirBranch(site.ExcTarget.Id)
                    : new MirRetThrow()));
        }

        // B-2 虚/interface/class 运算符派发挂起点改写（调用点动态分
        // 流；对齐 VM 帧栈模型——运行期目标是谁，挂起/恢复语义就与
        // 直调该目标完全一致）：
        //   head（原位置）：存活跃槽（含接收者与各 callee 槽）+
        //     state=N（先于下钻）→ 落首入分流链；
        //   首入分流链（vdisp）：闭包全类臂最深派生优先 type.is 判
        //     ——tainted 实现臂 → 建该实现 frame + 落参 → 调用块下
        //     钻；非 tainted 实现臂 → 原调用块（普通虚派发）；默认
        //     臂（闭包外/null 接收者）→ 原调用块（NRE 语义保持）；
        //   调用块（首入与恢复共用，每实现一块）：MirResumeCall 下
        //     钻 → 四码分流（0/1 上传 / 2 读该实现 frame.$mw.result
        //     续行 / 3 取 pending 沿原 ExcTarget 重抛）；
        //   恢复分流链（vrdisp，state N 恢复块落点）：同序 type.is
        //     重判直落调用块（frame 不重建——首入已建）；默认臂防御
        //     不可达（挂起前提即首入命中 tainted 臂）。
        // callee frame 所有权归本层（各 callee 槽持 +1；resume 借用
        // 约定不做最终 release）
        private void EmitVirtualCallSplit(MwContext context, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, string headId,
            List<MirInst> headInsts, MirLocalOperand frameOp, string stateFieldSymbol,
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave,
            System.Func<int, string, MirLoadResource> i32Const,
            System.Func<string, MirType, string> fresh)
        {
            var site = point.Virtual!;
            var callId = CallBlockId(point);
            var dispatchId = callId + ".vdisp";
            var defaultId = callId + ".vdflt";
            var propSuspendId = callId + ".vprops";
            var propYieldId = callId + ".vpropy";
            var failId = callId + ".vfail";
            var receiverOp = new MirLocalOperand(site.ReceiverLocal);

            emitSave(headInsts, point.LiveAfter);
            var stateConst = fresh("$mw.state.c.", I32);
            headInsts.Add(i32Const(point.State, stateConst));
            headInsts.Add(new MirSetField(new MirLocalOperand(stateConst), frameOp,
                stateFieldSymbol));
            resumeFn.AddBlock(new MirBlock(headId, headInsts,
                new MirBranch(dispatchId)));

            // 每 tainted 实现的协议块 id（臂序即 site.Arms 序——最深
            // 派生优先；同实现多类共用一个协议块组）
            var implBlockIds = new Dictionary<CallSiteInfo, (string New, string Call,
                string Done)>();
            var nextImpl = 0;
            foreach (var arm in site.Arms)
            {
                if (arm.Impl != null && !implBlockIds.ContainsKey(arm.Impl))
                {
                    implBlockIds.Add(arm.Impl, (callId + ".vnew" + nextImpl,
                        callId + ".vcall" + nextImpl, callId + ".vdone" + nextImpl));
                    nextImpl++;
                }
            }

            // 分流链（首入/恢复共用生成器；resume=true 时 tainted 臂
            // 直落调用块、默认臂防御不可达）。R2-a：单臂多 type.is
            // 目标（泛型类 = 模板空壳 + 各闭合构造 sheet）——同臂内
            // 逐目标 OR，任一命中即进臂
            void EmitDispatchChain(string chainId, string checkPrefix, bool resume)
            {
                for (var k = 0; k < site.Arms.Count; k++)
                {
                    var arm = site.Arms[k];
                    var checkId = k == 0 ? chainId : checkPrefix + (k - 1) + ".next";
                    string hitId;
                    string missId;
                    if (arm.Impl != null)
                    {
                        hitId = resume
                            ? implBlockIds[arm.Impl].Call
                            : implBlockIds[arm.Impl].New;
                    }
                    else
                    {
                        // 恢复链上的非 tainted 臂不可达（挂起前提即首
                        // 入命中 tainted 臂，接收者类型不变）——防御
                        hitId = resume ? "mw.state.bad" : defaultId;
                    }
                    missId = k + 1 < site.Arms.Count
                        ? checkPrefix + k + ".next"
                        : (resume ? "mw.state.bad" : defaultId);
                    for (var t = 0; t < arm.TypeRefs.Count; t++)
                    {
                        var cond = fresh("$mw.vchk.", Bool);
                        var isLast = t + 1 >= arm.TypeRefs.Count;
                        var thisCheckId = t == 0 ? checkId : checkId + ".t" + t;
                        resumeFn.AddBlock(new MirBlock(thisCheckId, new List<MirInst>
                        {
                            new MirTypeCheck(MirTypeCheckKind.Is, receiverOp,
                                arm.TypeRefs[t], null, cond),
                        }, new MirCondBranch(new MirLocalOperand(cond), hitId,
                            isLast ? missId : checkId + ".t" + (t + 1))));
                    }
                }
            }
            EmitDispatchChain(dispatchId, callId + ".vc", resume: false);
            EmitDispatchChain(VirtualResumeDispatchId(point), callId + ".vrc",
                resume: true);

            // 默认臂/非 tainted 臂：原调用直落续行（vtable/iMap 动态
            // 派发——运行期目标必非 tainted，否则必中上方臂）
            resumeFn.AddBlock(new MirBlock(defaultId,
                new List<MirInst> { site.OriginalCall },
                new MirBranch(ContBlockId(point))));

            // 每 tainted 实现：建 frame + 落参（首入）→ 调用块四码
            foreach (var (impl, ids) in implBlockIds)
            {
                var calleeOp = new MirLocalOperand(impl.CalleeLocal);
                var frameType = context.Symbols.FindType(impl.CalleeFrameCanonical)
                    ?? throw new CompilerInternalException(
                        "tainted 虚实现 frame 未预注册: " + impl.CalleeFrameCanonical);
                var frameInit = context.Symbols.FindMember(
                    SyntheticTypePlanner.FrameInitCanonicalOf(impl.CalleeFrameCanonical))
                    ?? throw new CompilerInternalException(
                        "tainted 虚实现 frame init 缺失: " + impl.CalleeFrameCanonical);
                var newInsts = new List<MirInst>
                {
                    new MirNewObject(frameType, null, frameInit,
                        new List<MirOperand>(), impl.CalleeLocal),
                };
                foreach (var drop in impl.Drops)
                {
                    if (drop.Operand != null)
                    {
                        var operand = drop.Operand;
                        if (operand is MirLocalOperand local)
                        {
                            var sourceType = resumeFn.FindLocal(local.Name).Type;
                            var targetType = drop.OperandTargetType ?? sourceType;
                            if (sourceType.Canonical != targetType.Canonical
                                && (TypeLayout.IsGenericPlaceholder(sourceType)
                                    || TypeLayout.IsGenericPlaceholder(targetType)))
                            {
                                // 协议臂写具体 callee frame 前显式编组；不能把胖值直接写进标量字段。
                                var converted = fresh("$mw.call.arg.", targetType);
                                newInsts.Add(TypeLayout.IsGenericPlaceholder(targetType)
                                    ? TypeLayout.ClassifySlot(context.Layout, sourceType) == ManagedSlotKind.FatReference
                                        ? new MirCopyLocal(operand, converted)
                                        : new MirBoxAny(operand, converted)
                                    : new MirCast(operand, converted, false, targetType.Canonical, null, site.ExcTarget));
                                operand = new MirLocalOperand(converted);
                            }
                        }
                        newInsts.Add(new MirSetField(operand, calleeOp,
                            drop.FrameFieldSymbol));
                    }
                    else if (drop.TypeIdTypeRef != null)
                    {
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                        newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                            calleeOp, drop.FrameFieldSymbol));
                    }
                    else if (drop.ReceiverTypeIdOwner != null)
                    {
                        // R2-a：类级 typeid 运行期取自接收者实例隐
                        // 藏字段（首入块执行，接收者槽仍活跃）
                        var tid = fresh("$mw.tid.arg.", MirType.Of(".typeid"));
                        newInsts.Add(new MirGetClassTypeArgument(drop.ReceiverTypeIdOperand ?? receiverOp,
                            drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                        newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                            calleeOp, drop.FrameFieldSymbol));
                    }
                    else
                    {
                        newInsts.Add(new MirSetField(
                            new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                            drop.FrameFieldSymbol));
                    }
                }
                // callee 槽回存本层 frame：head 的 emitSave 在分流/建
                // frame 之前执行（槽位尚为 null），恢复块从本层 frame
                // 恢复 callee 槽下钻——必须在此把新建 frame 落进保存槽
                var callerFrameCanonical = resumeFn.FindLocal(FrameParamName)
                    .Type.Canonical;
                newInsts.Add(new MirSetField(calleeOp, frameOp,
                    SyntheticTypePlanner.FrameFieldSymbol(callerFrameCanonical,
                        impl.CalleeLocal, impl.CalleeFrameCanonical)));
                resumeFn.AddBlock(new MirBlock(ids.New, newInsts,
                    new MirBranch(ids.Call)));

                var code = fresh("$mw.call.code.", I32);
                var codeTable = new BilSwitchTableResource(
                    "$mw.coroutine.call." + _resourceCounter++, ".i32",
                    new[] { "0", "1", "2", "3" });
                context.Module.Resources.Add(codeTable);
                resumeFn.AddBlock(new MirBlock(ids.Call, new List<MirInst>
                {
                    new MirResumeCall(impl.ResumeSymbol, impl.CalleeLocal, code),
                }, new MirSwitch(new MirLocalOperand(code), codeTable,
                    new[] { propSuspendId, propYieldId, ids.Done, failId },
                    "mw.state.bad")));

                // DONE：借用 callee frame 读 $mw.result 续行
                var doneInsts = new List<MirInst>();
                if (site.Result != null)
                {
                    if (impl.ResultFieldSymbol == null)
                    {
                        throw new CompilerInternalException(
                            "tainted 虚调用有结果槽但实现无 $mw.result: "
                            + impl.Callee.Symbol.Canonical);
                    }
                    var sourceType = impl.Callee.ReturnType;
                    var targetType = resumeFn.FindLocal(site.Result).Type;
                    if (sourceType.Canonical != targetType.Canonical
                        && (TypeLayout.IsGenericPlaceholder(sourceType)
                            || TypeLayout.IsGenericPlaceholder(targetType)))
                    {
                        var raw = fresh("$mw.call.result.", sourceType);
                        doneInsts.Add(new MirGetField(calleeOp, impl.ResultFieldSymbol, raw));
                        doneInsts.Add(TypeLayout.IsGenericPlaceholder(targetType)
                            ? new MirBoxAny(new MirLocalOperand(raw), site.Result)
                            : new MirCast(new MirLocalOperand(raw), site.Result, false,
                                targetType.Canonical, null, site.ExcTarget));
                    }
                    else doneInsts.Add(new MirGetField(calleeOp, impl.ResultFieldSymbol,
                        site.Result));
                }
                resumeFn.AddBlock(new MirBlock(ids.Done, doneInsts,
                    new MirBranch(ContBlockId(point))));
            }

            // SUSPENDED/YIELDED 上传（各实现调用块共用两块——上传码
            // 是常量，无需引用各调用块的 code 局部）：frame 已写完，
            // 直返常量码（Tasked 调用方先投影自身 Task Suspended）
            void EmitPropBlock(string blockId, int resumeCode)
            {
                var propInsts = new List<MirInst>();
                if (taskFieldSymbol != null)
                {
                    EmitTaskMark(context, propInsts, frameOp, taskFieldSymbol,
                        taskTypeRef!, "markSuspended", fresh);
                }
                var propCode = fresh("$mw.code.", I32);
                propInsts.Add(i32Const(resumeCode, propCode));
                resumeFn.AddBlock(new MirBlock(blockId, propInsts,
                    new MirRet(new MirLocalOperand(propCode))));
            }
            EmitPropBlock(propSuspendId, ResumeSuspended);
            EmitPropBlock(propYieldId, ResumeYielded);

            // FAILED（共用）：pending 已在 TLS（callee plain 垫尾保持
            // 置位），取走重抛沿原 ExcTarget
            var exc = fresh("$mw.call.exc.", Any);
            var failInsts = new List<MirInst>
            {
                new MirTakePending(exc),
                new MirThrow(new MirLocalOperand(exc), site.ExcTarget),
            };
            resumeFn.AddBlock(new MirBlock(failId, failInsts,
                site.ExcTarget != null
                    ? (MirTerminator)new MirBranch(site.ExcTarget.Id)
                    : new MirRetThrow()));
        }


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
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
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

            resumeFn.AddBlock(new MirBlock(headId, headInsts, new MirBranch(waitId)));

            // wait：acquire → 冷启动分支 → 寄存
            var gate = fresh("$mw.await.gate.", I64);
            var hasRuntime = fresh("$mw.await.runtime.", Bool);
            var waitInsts = new List<MirInst>
            {
                new MirGetField(taskOp, gateField, gate),
                new MirCall(syms.MutexAcquire,
                    new List<MirOperand> { new MirLocalOperand(gate) }, null),
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "hasRuntime"),
                    new List<MirOperand> { taskOp }, hasRuntime),
            };
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                new MirCondBranch(new MirLocalOperand(hasRuntime), regId, coldId)));

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
            resumeFn.AddBlock(new MirBlock(coldGoId, new List<MirInst>
            {
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "spawnIntoLocked"),
                    new List<MirOperand> { taskOp }, null),
                new MirCall(syms.DispatcherGet, new List<MirOperand>(), dispC),
                new MirCall(syms.NoteSpawn,
                    new List<MirOperand> { new MirLocalOperand(dispC) }, null),
                new MirCall(TaskFn(context, awaitedTaskTypeRef, "publishRuntime"),
                    new List<MirOperand> { taskOp }, null),
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
            //（plain resume 无 Task——跳过投影，对齐 VM 无 TaskObject
            // 协程口径）
            var suspendInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, suspendInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
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
                    resultType, awaitInst.ResultSlot, awaitInst.ExcTarget));
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
            string? taskFieldSymbol, string? taskTypeRef, RuntimeSyms syms,
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
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, waitInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
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
            string stateFieldSymbol, string? taskFieldSymbol, string? taskTypeRef,
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

            // EventAlarm：闸内登记 waiter；已触发（粘滞）→ 自重排。
            // L8：先经 ensureHandle 取底座句柄——用户直继子类
            // handle==0 时懒建手动事件粘滞底座（VM TryAwaitTimer
            // 同口径懒建），此后 rigi_alarm_wait 恒收非 0 句柄
            var ah = fresh("$mw.yield.ah.", I64);
            var curE = fresh("$mw.yield.cur.", I64);
            var rc = fresh("$mw.yield.rc.", I32);
            var rcZero = fresh("$mw.yield.rcz.", I32);
            var notRegistered = fresh("$mw.yield.sig.", Bool);
            var eventInsts = new List<MirInst>
            {
                new MirCall(EventAlarmFn(context, "ensureHandle"),
                    new List<MirOperand> { new MirLocalOperand(yieldAlarm.AlarmSlot) }, ah),
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

            // 已登记：等响铃，结束执行段（TaskState 投影仅 Tasked——
            // plain 无 Task 可投影，对齐 VM 无 TaskObject 协程口径）
            var registeredInsts = new List<MirInst>();
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, registeredInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", fresh);
            }
            resumeFn.AddBlock(new MirBlock(yieldId + ".registered",
                registeredInsts, resumeRet(ResumeSuspended, registeredInsts)));
        }

        // PollingAlarm 恢复闸（yield-alarm 的 state N 恢复块落点）：
        // poll_pending 判位——EventAlarm 响铃恢复（pending=0）直续原
        // 后继；PollingAlarm 重发布恢复（pending=1）先探测。
        // Phase 2.6 双路径（§19.2 语义纠偏——isReady 允许 await/yield）：
        // - 探测闭包含 tainted 实现（point.ProbeSite 非空）：经恢复块
        //   站点协议臂下钻——首入按 alarm 运行期类型分流建探测 frame、
        //   MirResumeCall 调 isReady 状态机；探测中途挂起（SUSPENDED/
        //   YIELDED）写探测子状态 ProbeState 退回等待，唤醒后经专用
        //   恢复块下钻续跑至完成再做一次性就绪判定（ready → poll_clear
        //   + 续行；not → poll_schedule + ret SUSPENDED，state 保持 N）；
        //   FAILED 沿 yield 点词法 try/catch 失败尾。untainted 联合臂
        //   保持 $mw.poll_probe 同步廉价路径（目标必非 tainted，虚派发
        //   安全）；分流 miss = 闭包外类型，防御不可达。
        // - 全 untainted 闭包：现状 $mw.poll_probe 同步虚派发（ready/
        //   not/异常三路 switch）。异常（-1，pending 已置位）→ 失败尾
        //   分叉：Tasked = 失败终态序列（Task FAILED，await 点重抛——
        //   对齐 VM ProbePolling 的 yield 点失败口径）；B-2 Plain =
        //   ret FAILED（pending 保持置位沿链上传，调用方调用点 FAILED
        //   臂取走重抛——对齐 VM 帧栈逐层展开口径；release 序列由
        //   RcInjection 标准 ret 出口配平）
        private void EmitPollGate(MwContext context, MirModule mir, MirFunction resumeFn,
            MirFunction fn, SuspensionPoint point, MirLocalOperand frameOp,
            string stateFieldSymbol, string? taskFieldSymbol, string? taskTypeRef,
            RuntimeSyms syms,
            System.Action<List<MirInst>, IReadOnlyList<string>> emitSave)
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

            if (point.ProbeSite is { } probe)
            {
                var parkId = gateId + ".park";
                var cheapId = probeId + ".cheap";
                var pdispId = PollProbeResumeDispatchId(point);

                // 首入探测分流链（深→浅）与恢复分流链共用生成器：链上
                // 逐臂逐 type.is 目标 OR（任一命中即进臂）。首入命中
                // tainted 臂 → new 块建探测 frame；命中 untainted 联合
                // 臂 → $mw.poll_probe 廉价路径；miss = 闭包外类型——
                // vtable 派发目标必在闭包内，防御不可达。恢复链命中
                // tainted 臂 → 直落调用块（探测 frame 从本层 frame 槽
                // 恢复）；untainted 臂不可达（同步探测无挂起点，探测
                // 挂起前提即首入命中 tainted 臂）——防御 mw.state.bad
                void EmitProbeDispatchChain(string chainId, string checkPrefix,
                    bool resume)
                {
                    // untainted 联合尾链首（仅首入链存在；全 tainted 闭包
                    // 无尾——末臂 miss 直落防御不可达）
                    var hasTail = !resume && probe.UntaintedTypeRefs.Count > 0;
                    var tailId = probe.Arms.Count == 0
                        ? chainId
                        : checkPrefix + (probe.Arms.Count - 1) + ".next";
                    for (var k = 0; k < probe.Arms.Count; k++)
                    {
                        var (entry, typeRefs, _) = probe.Arms[k];
                        var checkId = k == 0 ? chainId : checkPrefix + (k - 1) + ".next";
                        var hitId = resume
                            ? ProbeCallBlockId(gateId, k)
                            : ProbeNewBlockId(gateId, k);
                        var missId = k + 1 < probe.Arms.Count
                            ? checkPrefix + k + ".next"
                            : hasTail ? tailId : "mw.state.bad";
                        for (var t = 0; t < typeRefs.Count; t++)
                        {
                            var cond = Fresh("$mw.pchk.", Bool);
                            var isLast = t + 1 >= typeRefs.Count;
                            var thisCheckId = t == 0 ? checkId : checkId + ".t" + t;
                            resumeFn.AddBlock(new MirBlock(thisCheckId,
                                new List<MirInst>
                                {
                                    new MirTypeCheck(MirTypeCheckKind.Is,
                                        new MirLocalOperand(alarmSlot), typeRefs[t],
                                        null, cond),
                                }, new MirCondBranch(new MirLocalOperand(cond), hitId,
                                    isLast ? missId : checkId + ".t" + (t + 1))));
                        }
                    }
                    if (!hasTail)
                    {
                        return;
                    }
                    // 首入链 untainted 联合尾：单臂多 type.is 目标 OR
                    var tailRefs = probe.UntaintedTypeRefs;
                    for (var t = 0; t < tailRefs.Count; t++)
                    {
                        var cond = Fresh("$mw.pchk.", Bool);
                        var isLast = t + 1 >= tailRefs.Count;
                        var thisCheckId = t == 0 ? tailId : tailId + ".t" + t;
                        resumeFn.AddBlock(new MirBlock(thisCheckId,
                            new List<MirInst>
                            {
                                new MirTypeCheck(MirTypeCheckKind.Is,
                                    new MirLocalOperand(alarmSlot), tailRefs[t],
                                    null, cond),
                            }, new MirCondBranch(new MirLocalOperand(cond), cheapId,
                                isLast ? "mw.state.bad" : tailId + ".t" + (t + 1))));
                    }
                }

                // 首入链（probeId = 链首）
                EmitProbeDispatchChain(probeId, gateId + ".pc", resume: false);

                // 每 tainted 实现：建探测 frame + 落参（首入）→ 调用块
                for (var k = 0; k < probe.Arms.Count; k++)
                {
                    var entry = probe.Arms[k].Impl;
                    var callId = ProbeCallBlockId(gateId, k);
                    var newId = ProbeNewBlockId(gateId, k);
                    var doneId = ProbeDoneBlockId(gateId, k);
                    var calleeOp = new MirLocalOperand(entry.CalleeLocal);
                    var frameType = context.Symbols.FindType(entry.CalleeFrameCanonical)
                        ?? throw new CompilerInternalException(
                            "tainted isReady frame 未预注册: " + entry.CalleeFrameCanonical);
                    var frameInit = context.Symbols.FindMember(
                        SyntheticTypePlanner.FrameInitCanonicalOf(entry.CalleeFrameCanonical))
                        ?? throw new CompilerInternalException(
                            "tainted isReady frame init 缺失: " + entry.CalleeFrameCanonical);
                    var newInsts = new List<MirInst>
                    {
                        new MirNewObject(frameType, null, frameInit,
                            new List<MirOperand>(), entry.CalleeLocal),
                    };
                    foreach (var drop in entry.Drops)
                    {
                        if (drop.Operand != null)
                        {
                            var operand = drop.Operand;
                            if (operand is MirLocalOperand local
                                && drop.OperandTargetType is { } targetType)
                            {
                                var sourceType = resumeFn.FindLocal(local.Name).Type;
                                if (sourceType.Canonical != targetType.Canonical
                                    && (TypeLayout.IsGenericPlaceholder(sourceType)
                                        || TypeLayout.IsGenericPlaceholder(targetType)))
                                {
                                    var converted = Fresh("$mw.probe.arg.", targetType);
                                    newInsts.Add(TypeLayout.ClassifySlot(context.Layout,
                                        sourceType) == ManagedSlotKind.FatReference
                                        ? new MirCopyLocal(operand, converted)
                                        : new MirBoxAny(operand, converted));
                                    operand = new MirLocalOperand(converted);
                                }
                            }
                            newInsts.Add(new MirSetField(operand, calleeOp,
                                drop.FrameFieldSymbol));
                        }
                        else if (drop.TypeIdTypeRef != null)
                        {
                            var tid = Fresh("$mw.probe.tid.", MirType.Of(".typeid"));
                            newInsts.Add(new MirGetTypeId(drop.TypeIdTypeRef, tid));
                            newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                                calleeOp, drop.FrameFieldSymbol));
                        }
                        else if (drop.ReceiverTypeIdOwner != null)
                        {
                            var tid = Fresh("$mw.probe.tid.", MirType.Of(".typeid"));
                            newInsts.Add(new MirGetClassTypeArgument(
                                drop.ReceiverTypeIdOperand ?? new MirLocalOperand(alarmSlot),
                                drop.ReceiverTypeIdOwner, drop.ReceiverTypeIdParameter!, tid));
                            newInsts.Add(new MirSetField(new MirLocalOperand(tid),
                                calleeOp, drop.FrameFieldSymbol));
                        }
                        else
                        {
                            newInsts.Add(new MirSetField(
                                new MirLocalOperand(drop.CallerTypeIdLocal!), calleeOp,
                                drop.FrameFieldSymbol));
                        }
                    }
                    // callee 槽回存本层 frame：探测挂起后 N' 恢复块从本层
                    // frame 恢复该槽下钻（EmitCallSplit 同口径）
                    emitSave(newInsts, new[] { entry.CalleeLocal });
                    resumeFn.AddBlock(new MirBlock(newId, newInsts,
                        new MirBranch(callId)));

                    var code = Fresh("$mw.poll.code.", I32);
                    var codeTable = new BilSwitchTableResource(
                        "$mw.coroutine.pollprobe." + _resourceCounter++, ".i32",
                        new[] { "0", "1", "2", "3" });
                    context.Module.Resources.Add(codeTable);
                    resumeFn.AddBlock(new MirBlock(callId, new List<MirInst>
                    {
                        new MirResumeCall(entry.ResumeSymbol, entry.CalleeLocal, code),
                    }, new MirSwitch(new MirLocalOperand(code), codeTable,
                        new[] { parkId, parkId, doneId, failId }, "mw.state.bad")));

                    // DONE：一次性就绪判定——探测返回 true → 就绪续行；
                    // false → 退回等待（未就绪分支与廉价路径 waitId 共用）。
                    // 探测完成即脱离子状态：state 写回 N（就绪续行后的
                    // 下一个挂起点会覆写；未就绪重排恢复必须重回 poll
                    // gate 再次首探，而非探测恢复链）
                    var doneInsts = new List<MirInst>();
                    var doneState = Fresh("$mw.poll.st.", I32);
                    doneInsts.Add(new MirLoadResource(
                        ProxyWildcardAbi.AddI32Resource(context, point.State),
                        doneState));
                    doneInsts.Add(new MirSetField(new MirLocalOperand(doneState),
                        frameOp, stateFieldSymbol));
                    var rdy = Fresh("$mw.poll.rdy.", Bool);
                    doneInsts.Add(new MirGetField(calleeOp,
                        entry.ResultFieldSymbol!, rdy));
                    resumeFn.AddBlock(new MirBlock(doneId, doneInsts,
                        new MirCondBranch(new MirLocalOperand(rdy), readyId,
                            waitId)));
                }

                // 探测中途挂起（isReady 状态机 SUSPENDED/YIELDED 上传）：
                // 挂起即继续等待——写探测子状态 N'（恢复入口直落探测恢复
                // 分流链续跑）+ ret SUSPENDED。poll_pending 保持置位；
                // 唤醒源是 isReady 内部的挂起源（await Task/EventAlarm/
                // 裸 yield 重发布），polling 退避扫描的重复重发布无害
                //（N' 恢复 → 探测幂等续跑）。TaskState 投影仅 Tasked
                var parkInsts = new List<MirInst>();
                var parkState = Fresh("$mw.poll.st.", I32);
                parkInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, probe.ProbeState),
                    parkState));
                parkInsts.Add(new MirSetField(new MirLocalOperand(parkState),
                    frameOp, stateFieldSymbol));
                if (taskFieldSymbol != null)
                {
                    EmitTaskMark(context, parkInsts, frameOp, taskFieldSymbol,
                        taskTypeRef!, "markSuspended", Fresh);
                }
                var parkCode = Fresh("$mw.code.", I32);
                parkInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, ResumeSuspended),
                    parkCode));
                resumeFn.AddBlock(new MirBlock(parkId, parkInsts,
                    new MirRet(new MirLocalOperand(parkCode))));

                // 恢复分流链（N' 恢复块落点；probeId 已被首入链占用，
                // pdispId 即链首）
                EmitProbeDispatchChain(pdispId, gateId + ".prc", resume: true);

                // untainted 联合臂：$mw.poll_probe 同步廉价路径（现状
                // probeId 内容；运行期目标必非 tainted——虚派发安全）
                var pr = Fresh("$mw.poll.pr.", I32);
                // probe 内用户 isReady 抛出时，普通 MirCall 的 pending 检查须
                // 直接落探测失败臂；否则会绕到 resume 函数级传播垫，跳过
                // yield 点保存的词法 try/catch。
                var probeFailTarget = new MirBlock(failId, new List<MirInst>(),
                    new MirUnreachable());
                var probeTable = new BilSwitchTableResource(
                    "$mw.coroutine.probe." + _resourceCounter++, ".i32",
                    new[] { "-1", "0", "1" });
                context.Module.Resources.Add(probeTable);
                resumeFn.AddBlock(new MirBlock(cheapId, new List<MirInst>
                {
                    new MirCall(EnsurePollProbe(context, mir),
                        new List<MirOperand> { new MirLocalOperand(alarmSlot) }, pr,
                        probeFailTarget),
                }, new MirSwitch(new MirLocalOperand(pr), probeTable,
                    new[] { failId, waitId, readyId }, "mw.state.bad")));
            }
            else
            {
                // 全 untainted 闭包：廉价路径（现状形态）
                var pr = Fresh("$mw.poll.pr.", I32);
                // probe 内用户 isReady 抛出时，普通 MirCall 的 pending 检查须
                // 直接落探测失败臂；否则会绕到 resume 函数级传播垫，跳过
                // yield 点保存的词法 try/catch。
                var probeFailTarget = new MirBlock(failId, new List<MirInst>(),
                    new MirUnreachable());
                var probeTable = new BilSwitchTableResource(
                    "$mw.coroutine.probe." + _resourceCounter++, ".i32",
                    new[] { "-1", "0", "1" });
                context.Module.Resources.Add(probeTable);
                resumeFn.AddBlock(new MirBlock(probeId, new List<MirInst>
                {
                    new MirCall(EnsurePollProbe(context, mir),
                        new List<MirOperand> { new MirLocalOperand(alarmSlot) }, pr,
                        probeFailTarget),
                }, new MirSwitch(new MirLocalOperand(pr), probeTable,
                    new[] { failId, waitId, readyId }, "mw.state.bad")));
            }

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
            // TaskState 投影仅 Tasked（plain 无 Task 可投影）
            if (taskFieldSymbol != null)
            {
                EmitTaskMark(context, waitInsts, frameOp, taskFieldSymbol,
                    taskTypeRef!, "markSuspended", Fresh);
            }
            var waitCode = Fresh("$mw.code.", I32);
            waitInsts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddI32Resource(context, ResumeSuspended), waitCode));
            resumeFn.AddBlock(new MirBlock(waitId, waitInsts,
                new MirRet(new MirLocalOperand(waitCode))));

            // 探测异常（pending 已置位）：Tasked 走失败终态序列（与
            // RcInjection resume 垫尾同构——统一走 EmitFailTerminal）；
            // B-2 Plain 走链式上传（ret FAILED——与 RcInjection plain
            // 垫尾同口径，托管槽 release 由标准 ret 出口配平）
            var curF = Fresh("$mw.poll.cur.", I64);
            var failInsts = new List<MirInst>
            {
                new MirCall(syms.CoroutineCurrent, new List<MirOperand>(), curF),
                new MirCall(syms.PollClear,
                    new List<MirOperand> { new MirLocalOperand(curF) }, null),
            };
            MirTerminator failRet;
            var yieldInst = (MirYieldAlarm)point.Inst;
            if (yieldInst.ExcTarget != null)
            {
                var exc = Fresh("$mw.poll.exc.", Any);
                failInsts.Add(new MirTakePending(exc));
                failInsts.Add(new MirThrow(new MirLocalOperand(exc), yieldInst.ExcTarget));
                failRet = new MirBranch(yieldInst.ExcTarget.Id);
            }
            else if (taskFieldSymbol != null)
            {
                failRet = EmitFailTerminal(context, mir, resumeFn, failInsts,
                    taskFieldSymbol, taskTypeRef!, syms);
            }
            else
            {
                var failCode = Fresh("$mw.code.", I32);
                failInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddI32Resource(context, PlainResumeFailedCode),
                    failCode));
                failRet = new MirRet(new MirLocalOperand(failCode));
            }
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

        // 终态与 await 登记共用 Task.gate：状态迁移和 waiter 排空必须
        // 原子完成，否则其他 Worker 可在排空后才把 waiter 写入旧队列。
        // publishAll 留在锁外，避免恢复者等待同一 gate 时阻塞发布路径。
        private static void EmitTerminalPublish(MwContext context, MirFunction fn,
            List<MirInst> insts, string task, string taskTypeRef, bool failed,
            RuntimeSyms syms, System.Func<string, MirType, string> fresh,
            System.Func<string, MirType, string> freshManaged)
        {
            var gate = fresh("$mw.done.gate.", I64);
            insts.Add(new MirGetField(new MirLocalOperand(task),
                TaskField(taskTypeRef, "gate", ".i64"), gate));
            insts.Add(new MirCall(syms.MutexAcquire,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
            var drained = freshManaged("$mw.drained.",
                MirType.Of(".array<core.coroutine::CoroutineCarriage>"));
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, failed ? "fail" : "complete"),
                new List<MirOperand> { new MirLocalOperand(task) }, drained));
            insts.Add(new MirCall(syms.MutexRelease,
                new List<MirOperand> { new MirLocalOperand(gate) }, null));
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
        // → noteTerminal → MirCoroutineDone → ret DONE。
        // B-1 Plain 分叉：裸 frame 无 Task——非 void 写 $mw.result
        // 字段（调用方 DONE 臂读取）→ 直 ret DONE（无终态序列/无
        // MirCoroutineDone——frame 所有权归调用方，本出口不做最终
        // release）
        private void RewriteReturn(MwContext context, MirModule mir, MirFunction resumeFn,
            MirBlock block, SplitPlan plan, MirLocalOperand frameOp, RuntimeSyms syms)
        {
            if (block.Terminator is not MirRet ret)
            {
                return;
            }
            var insts = block.InstructionList;
            string Fresh(string prefix, MirType type) =>
                ProxyWildcardAbi.FreshLocal(resumeFn, prefix, type);
            if (plan.Mode == SplitMode.Plain)
            {
                if (ret.Value != null && plan.ResultFieldSymbol != null)
                {
                    insts.Add(new MirSetField(ret.Value, frameOp,
                        plan.ResultFieldSymbol));
                }
                block.Terminator = ResumeDoneTerminator(context, resumeFn, insts, Fresh);
                return;
            }
            var taskFieldSymbol = plan.TaskFieldSymbol!;
            var taskTypeRef = plan.TaskTypeRef!;
            var original = plan.Fn;
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
            insts.Add(new MirCall(TaskFn(context, taskTypeRef, "attachRuntimeNative"),
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

        // 沿 extends 链找 $$call 成员（环保护；外部/缺失基类即止）。
        // 与 VM FindCallTarget 拍平 sheet（含继承槽）同语义
        private static MwMemberSymbol? FindCallAlongHierarchy(MwContext context,
            MwTypeSymbol type)
        {
            var guard = new HashSet<string>(System.StringComparer.Ordinal);
            var current = type;
            while (guard.Add(current.Canonical))
            {
                var call = current.Members.FirstOrDefault(m =>
                    m.Canonical.Contains("$$call(", System.StringComparison.Ordinal));
                if (call != null)
                {
                    return call;
                }
                if (current.Declaration.ExtendsType is not { } baseRef
                    || context.Symbols.FindTypeByRef(baseRef) is not
                        { IsExternal: false } baseType)
                {
                    return null;
                }
                current = baseType;
            }
            return null;
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
                hitInsts.Add(new MirCall(TaskFn(context, taskDecl, "attachCold"),
                    new List<MirOperand> { thisOp, new MirLocalOperand(handle) }, null));
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
        private bool IsRealTaskInit(MwMemberSymbol? init)
        {
            // L7：Init = null（无 init 声明零参 new）必非 Task init
            if (init == null)
            {
                return false;
            }
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
            // 闭包的 async $$call → split 产物（frame 类型 + resume fn）。
            // $$call 沿 extends 链解析（与 VM FindCallTarget 拍平 sheet
            // 含继承槽同语义：Sub : Base : AsyncAction 的 $$call 声明在
            // Base，frame/resume 也属 Base 的 split 产物）
            var closureType = context.Symbols.FindType(bodyType.Canonical)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 静态类型不透明（native 半场暂不支持）: "
                    + bodyType.Canonical);
            var call = FindCallAlongHierarchy(context, closureType)
                ?? throw new MwNotSupportedException(
                    "冷 Task 的 body 缺少 $$call 实现: " + bodyType.Canonical);
            // frame 的 .this 槽类型 = $$call 声明宿主（继承命中时为基类），
            // 落字段符号必须按声明宿主拼写而非构造点静态类型
            var callThisCanonical = call.Owner?.Canonical ?? bodyType.Canonical;
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
                        callThisCanonical)),
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
                new MirCall(TaskFn(context, taskTypeRef, "attachCold"),
                    new List<MirOperand> { new MirLocalOperand(task),
                        new MirLocalOperand(handle) }, null),
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
