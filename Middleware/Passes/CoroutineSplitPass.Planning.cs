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
    public sealed partial class CoroutineSplitPass : IMwStage
    {
        // Planning 职责；与主文件共享同一类型、字段及生命周期。

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
            AnalyzeLiveness(context, fn, points);

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
                    case MirCall call when !IsMutexEnter(context, call):
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

    }
}
