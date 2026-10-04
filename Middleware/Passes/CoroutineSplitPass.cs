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
    public sealed partial class CoroutineSplitPass : IMwStage
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

        private RuntimeSyms? _syms;

        internal const string ExceptionCanonical = "core::Exception";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("CoroutineSplit 要求 Mir 已挂载");
            // B-1：taint 分析先行（栈式跨界，SYNTAX §11）——非 async fn
            // 的挂起点沿直调图反向传染调用方；tainted 普通 fn 走裸
            // frame split，tainted main 走 Task 包装 split + 根驱动
            var tainted = TaintAnalysis(context, mir);
            // 必须在 plain tainted 被改写为同步签名陷阱之前拒绝 C 导出。
            context.NativeBuild.ValidateSynchronousClosure(mir, tainted, context);
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
                            || IsMutexEnter(context, inst)
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

        // 虚/interface 派发的「类 → 槽实现」闭包对（调用点动态分流
        // 与传染判定共用；静态知识，按目标 canonical 缓存）。镜像
        // MirReachability.AddVirtualEdges/AddInterfaceEdges 的查询口
        // 径，但保留每类实现身份（分流臂需要）
        private readonly Dictionary<string,
            List<(TypeLayoutPlan ClassPlan, string ImplCanonical)>> _closureCache = new(
                System.StringComparer.Ordinal);

        // ===== R2-c：new.indirect × tainted class init 臂协议 =====

        // 模块内全部 class init 重载（懒建缓存；声明级形参类型 =
        // CanonicalSignature 形参段，与 DynamicNewEmitter.CollectInits
        // 同源）
        private List<IndirectInitOverload>? _classInitOverloads;

        // 含挂起点 init 的合成空 init（模块级按类型懒建；对齐 frame/
        // Task 空 init 先例——字段零值由 rigi_alloc 清零承担，字段初
        // 始值由 ..init.wrapper 在构造点原位缝合）。.this 类型用模板
        // 声明形（MIR fn 模板共享；类级 typeid 由 prologue 从对象头
        // 自取，空体不用）
        private readonly Dictionary<string, MwMemberSymbol> _emptyCtorInits = new(
            System.StringComparer.Ordinal);

        // Task 合成空 init（模块级懒建，按声明形态缓存；字段零值由
        // rigi_alloc 清零承担——singleton 空 init 同口径）。gate/body
        // 等由 attachRuntime/工厂随后填充
        private readonly Dictionary<string, MwMemberSymbol> _taskEmptyInits = new(
            System.StringComparer.Ordinal);

        private readonly List<ColdBindEntry> _coldBinds = new();
        private readonly Dictionary<string, MwMemberSymbol> _bindColdHelpers = new(
            System.StringComparer.Ordinal);

        private readonly Dictionary<string, MwMemberSymbol> _coldTaskFactories = new(
            System.StringComparer.Ordinal);
    }
}
