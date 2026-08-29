using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// Entity 方法/运算符 proxy 烘焙（MW10 刀1 specific + 刀3a wildcard）：
    /// 按应用标记逐层记录 MatchKind（Specific/Wildcard/None），任一层命中
    /// 即建链——specific 环特化 + inner 链接（下一环为 wildcard 时把具体
    /// 实参打包成胖值 ABI）；wildcard 环特化 canonical 模板（.generic
    /// 标量 typeid 代入成员返回类型、包占位按 Any 擦除剔除），环内
    /// MirInnerCall 改写为动态分派块（symbol == 本成员 canonical → 解包
    /// 直进下一环，否则调 router(H, 下一层) 重路由）；原名槽改 trampoline
    ///（首环 wildcard 时打包 symbol/空 named 包/unnamed 装箱包）；router
    /// 按 (宿主, 层界) 合成，if 链先覆盖字段访问器符号（H$.get|set.<名>@T
    /// → 自 fromLayer 起进该字段的 Entity get/set 环链，分支发射委托
    /// <see cref="FieldProxyBakingPass"/> 钩子——字段链环在该 pass 已
    /// 烘焙，无循环引用、不重复烘焙），再覆盖宿主全部可烘焙方法/运算符，
    /// 链末 miss 抛 core.NoSuchMethodException。刀4：模块含 call???
    /// 调用点时为闭包含 .proxy.* wildcard 的宿主预建链末 router
    ///（fromLayer = 最内 wildcard 层 + 1，供
    /// <see cref="CallWildcardLoweringPass"/> entry 环末环落点）。
    /// 读：Mir + Symbols + BIL 模板 fn；写：追加合成 fn、替换原名
    /// MirFunction。小改写 pass，不上 CRTP。共享设施见
    /// <see cref="ProxyBakeSupport"/> / <see cref="ProxyWildcardAbi"/>。
    /// </summary>
    public sealed class ProxyBakingPass : IMwStage
    {
        public string Name => "ProxyBaking";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("ProxyBaking 要求 Mir 已挂载");
            var index = WrapperApplicationIndex.Build(context.Symbols);
            var byCanonical = new Dictionary<string, MirFunction>(System.StringComparer.Ordinal);
            foreach (var fn in mir.Functions)
            {
                byCanonical[fn.Symbol.Canonical] = fn;
            }
            var bilBySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bilBySymbol[bilFn.Symbol] = bilFn;
            }

            // 逐宿主收集：每个可烘焙成员的逐层 MatchKind；任一层命中
            //（且有 MIR 体）即建 job。router 覆盖宿主全部可烘焙成员
            //（含零命中成员——重路由落原名 fn）
            var hosts = new List<HostBake>();
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                var wrappers = index.EntityWrappers(type.Canonical);
                if (wrappers.Count == 0)
                {
                    continue;
                }
                var host = new HostBake(type, wrappers);
                host.FieldTargets.AddRange(
                    FieldProxyBakingPass.CollectRouterFieldTargets(context, index, type));
                foreach (var member in type.Members)
                {
                    if (!ProxyBakeSupport.IsBakeableHostMethod(member))
                    {
                        RejectGenericOperatorHit(context, wrappers, member);
                        continue;
                    }
                    var kinds = CollectLayers(context, wrappers, member);
                    byCanonical.TryGetValue(member.Canonical, out var original);
                    var plan = new MemberPlan(member, kinds, original);
                    host.Members.Add(plan);
                    if (original != null && HasAnyMatch(kinds))
                    {
                        host.Jobs.Add(new BakeJob(plan, original));
                    }
                }
                hosts.Add(host);
            }

            foreach (var host in hosts)
            {
                foreach (var job in host.Jobs)
                {
                    BakeMethod(context, mir, bilBySymbol, host, job);
                }
            }
            // 刀4：模块含 call??? 调用点时，为每个闭包含 .proxy.* wildcard
            // 的宿主预建 entry 链末环所需的 router（fromLayer = 最内
            // wildcard 层 + 1——VM RerouteWildcardInner 自当前环下一层起
            // 搜 proxy 同口径）；此时 HostBake 计划/环信息俱全，零重建成本
            if (HasCallWildcardSites(mir))
            {
                foreach (var host in hosts)
                {
                    var lastWildcard = -1;
                    for (var i = 0; i < host.Wrappers.Count; i++)
                    {
                        var wrapper = context.Symbols.FindTypeByRef(host.Wrappers[i])
                            ?? throw new CompilerInternalException(
                                "Entity wrapper 类型缺失: " + host.Wrappers[i]);
                        if (ProxyMatcher.FindProxy(wrapper, ".proxy.*",
                                BilProxyKind.Wildcard) != null)
                        {
                            lastWildcard = i;
                        }
                    }
                    if (lastWildcard >= 0)
                    {
                        host.RouterLayers.Add(lastWildcard + 1);
                    }
                }
            }
            foreach (var host in hosts)
            {
                BuildRouters(context, mir, host);
            }
            // 遗留12 任务②：super 绕过 wrapper 链（VM ResolveSuper 直接
            // 压帧、非虚同口径）——MirSuperCall 构建期解析为基类方法原名
            // 槽直调，被烘焙后原名槽 = trampoline，wrapper 链被误触发；
            // 此处（Method/Entity 烘焙均已落地）统一改写目标为最深层原始
            // 体符号
            RewriteSuperCallTargets(context, mir, index);
        }

        // super 调用目标改写：逐 fn 扫 MirSuperCall，目标成员被 wrapper
        // 烘焙时换为最深层原始体符号——Method wrapper 命中 → $.mwrapped.
        //（Entity×Method 三层组合的最深层），否则 Entity 命中 →
        // $.wrapped.（FieldProxyBakingPass set 终态旁路同口径、同一定名
        // 规则）；无 wrapper 烘焙（含 init 族等不可烘焙目标）不动
        private static void RewriteSuperCallTargets(MwContext context, MirModule mir,
            WrapperApplicationIndex index)
        {
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        if (insts[i] is not MirSuperCall super)
                        {
                            continue;
                        }
                        var member = context.Symbols.FindMember(super.Target.Canonical);
                        if (member == null)
                        {
                            continue;
                        }
                        string? rawCanonical = null;
                        if (ProxyBakeSupport.WillBakeMethodWrapper(context, mir, member,
                                out var mwrapped))
                        {
                            rawCanonical = mwrapped;
                        }
                        else if (ProxyBakeSupport.WillBakeHostMethod(context, mir, index,
                                member, out var wrapped))
                        {
                            rawCanonical = wrapped;
                        }
                        if (rawCanonical == null)
                        {
                            continue;
                        }
                        insts[i] = new MirSuperCall(
                            ProxyBakeSupport.SyntheticMember(rawCanonical, owner: null),
                            super.Args, super.Result, super.ExcTarget);
                    }
                }
            }
        }

        // 模块内是否存在 core::Any$call??? 调用点（刀4：无调用点则
        // entry 环/dispatch/router 预建全部跳过，零产物）
        internal static bool HasCallWildcardSites(MirModule mir)
        {
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is MirCall call
                            && BilSpellings.IsCallWildcardMethod(call.Target.Canonical))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // 泛型运算符经 wildcard 命中的受控拒绝（遗6 保守边界）：
        // VM 侧运算符实参不含 hidden typeid（InjectOperatorTypeIds 只在
        // 直调分支注入），链末无法恢复方法级 typeid（退化 .any）——
        // 无对应路径，拒绝而非静默直调（行为分歧且无声）
        private static void RejectGenericOperatorHit(MwContext context,
            IReadOnlyList<string> wrappers, MwMemberSymbol member)
        {
            if (!member.IsOperatorMember
                || member.Canonical.IndexOf('<', System.StringComparison.Ordinal) < 0)
            {
                return;
            }
            foreach (var wrapperRef in wrappers)
            {
                var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                    ?? throw new CompilerInternalException("Entity wrapper 类型缺失: " + wrapperRef);
                if (ProxyMatcher.MatchMethod(wrapper, member) != MwProxyMatchKind.None)
                {
                    throw new MwNotSupportedException(
                        "泛型运算符经 wildcard 烘焙暂不支持（受控拒绝）: " + member.Canonical);
                }
            }
        }

        // 逐层记录 MatchKind（任一层 Specific/Wildcard 都成链；None 透明跳过）
        private static List<MwProxyMatchKind> CollectLayers(MwContext context,
            IReadOnlyList<string> wrappers, MwMemberSymbol member)
        {
            var kinds = new List<MwProxyMatchKind>(wrappers.Count);
            foreach (var wrapperRef in wrappers)
            {
                var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                    ?? throw new CompilerInternalException("Entity wrapper 类型缺失: " + wrapperRef);
                kinds.Add(ProxyMatcher.MatchMethod(wrapper, member));
            }
            return kinds;
        }

        private static bool HasAnyMatch(List<MwProxyMatchKind> kinds)
        {
            foreach (var kind in kinds)
            {
                if (kind != MwProxyMatchKind.None)
                {
                    return true;
                }
            }
            return false;
        }

        // ===== 链烘焙 =====

        private static void BakeMethod(MwContext context, MirModule mir,
            Dictionary<string, BilFunction> bilBySymbol, HostBake host, BakeJob job)
        {
            var plan = job.Plan;
            var wrappedSymbol = ProxyBakeSupport.SyntheticMember(host.Type.Canonical
                + ProxyBakeSupport.WrappedInfix
                + plan.Member.SignatureKey + "@" + job.Original.ReturnType.Canonical, owner: null);
            var wrappedFn = new MirFunction(wrappedSymbol, job.Original.ReturnType,
                job.Original.Parameters, job.Original.Locals, job.Original.Blocks, false);
            plan.Terminal = wrappedSymbol;

            // 环自内向外逐环特化；nextXxx = 当前环的 inner 落点（终态或内一环）
            MwMemberSymbol nextCallee = wrappedSymbol;
            MirFunction? nextRingFn = null;
            string? nextWrapper = null;
            var nextKind = MwProxyMatchKind.None;
            MirFunction? outermostRing = null;
            string? outermostWrapper = null;
            var outermostKind = MwProxyMatchKind.None;
            for (var i = plan.Kinds.Count - 1; i >= 0; i--)
            {
                var kind = plan.Kinds[i];
                if (kind == MwProxyMatchKind.None)
                {
                    continue;
                }
                var wrapperRef = host.Wrappers[i];
                MirFunction baked;
                if (kind == MwProxyMatchKind.Specific)
                {
                    baked = Specialize(context, bilBySymbol, host.Type, plan.Member, wrapperRef);
                    if (nextKind == MwProxyMatchKind.Wildcard)
                    {
                        LinkInnerToWildcard(context, baked, job, nextRingFn!, nextWrapper!);
                    }
                    else
                    {
                        ProxyBakeSupport.LinkInner(baked, nextCallee, nextWrapper);
                    }
                }
                else
                {
                    baked = SpecializeWildcard(context, bilBySymbol, host.Type, job, wrapperRef);
                    RewriteWildcardInners(context, host, plan, baked, i,
                        nextCallee, nextRingFn, nextWrapper, nextKind);
                }
                IndexOperatorLoweringPass.RewriteFunction(context, baked);
                AccessorLoweringPass.RewriteFunction(context, baked);
                // proxy 体内访问 wrapped 字段同样成链（字段链面钩子）
                FieldProxyBakingPass.RewriteFunction(context, baked);
                mir.AddFunction(baked);
                plan.Rings.Add(new RingInfo(i, wrapperRef, kind, baked));
                nextCallee = baked.Symbol;
                nextRingFn = baked;
                nextWrapper = wrapperRef;
                nextKind = kind;
                outermostRing = baked;
                outermostWrapper = wrapperRef;
                outermostKind = kind;
            }

            var trampoline = outermostKind == MwProxyMatchKind.Wildcard
                ? BuildWildcardTrampoline(context, job.Original, outermostRing!, outermostWrapper!)
                : ProxyBakeSupport.BuildTrampoline(job.Original, nextCallee, outermostWrapper!);
            var list = mir.FunctionList;
            var index = list.IndexOf(job.Original);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "ProxyBaking 找不到原名 fn: " + plan.Member.Canonical);
            }
            list[index] = trampoline;
            mir.AddFunction(wrappedFn);
        }

        // 薄壳：查 specific proxy 模板 → 委托 ProxyBakeSupport 特化 → 命名 baked 符号
        private static MirFunction Specialize(MwContext context,
            Dictionary<string, BilFunction> bilBySymbol, MwTypeSymbol hostType,
            MwMemberSymbol member, string wrapperRef)
        {
            var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("烘焙找不到 wrapper: " + wrapperRef);
            var proxy = ProxyMatcher.FindSpecificMethodProxy(wrapper, member)
                ?? throw new CompilerInternalException(
                    "Specific 命中后找不到 proxy 模板: " + wrapperRef + " / " + member.Canonical);
            if (!bilBySymbol.TryGetValue(proxy.Canonical, out var bilFn))
            {
                throw new MwNotSupportedException("proxy 模板无 fn 体: " + proxy.Canonical);
            }
            var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, wrapperRef, wrapper,
                hostType.Canonical, out var returnType);
            var bakedSymbol = ProxyBakeSupport.SyntheticMember(
                wrapper.Canonical + ProxyBakeSupport.BakeInfix + hostType.Canonical + "$"
                + member.SignatureKey + "@" + returnType.Canonical,
                proxy.Owner);
            return new MirFunction(bakedSymbol, returnType, body.Parameters, body.Locals,
                body.Blocks, false);
        }

        // wildcard 环特化：.generic 标量 typeid 代入成员返回类型（void 擦除
        // 为 .any——void 无 TypeSheet 且环 ABI 以胖值承载结果；遗6：泛型
        // 占位返回 .generic<$.generic.T> 同擦 .any——VM 值自描述透传、
        // native 占位 ABI 即 16B 胖槽，与 .any 同形）、包占位剔除；
        // 具体（非泛型）模板的声明返回类型须与宿主成员一致
        private static MirFunction SpecializeWildcard(MwContext context,
            Dictionary<string, BilFunction> bilBySymbol, MwTypeSymbol hostType,
            BakeJob job, string wrapperRef)
        {
            var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("烘焙找不到 wrapper: " + wrapperRef);
            var isOperator = job.Plan.Member.Canonical.Contains("$$",
                System.StringComparison.Ordinal);
            var proxy = ProxyMatcher.FindProxy(wrapper,
                isOperator ? ".proxy.opr.*" : ".proxy.*", BilProxyKind.Wildcard)
                ?? throw new CompilerInternalException(
                    "Wildcard 命中后找不到 proxy 模板: " + wrapperRef + " / "
                    + job.Plan.Member.Canonical);
            if (!bilBySymbol.TryGetValue(proxy.Canonical, out var bilFn))
            {
                throw new MwNotSupportedException("proxy 模板无 fn 体: " + proxy.Canonical);
            }
            var memberReturn = job.Original.ReturnType;
            var erasedReturn = memberReturn.IsVoid
                || memberReturn.Canonical.Contains(".generic<", System.StringComparison.Ordinal)
                ? ".any" : memberReturn.Canonical;
            ProxyWildcardAbi.ClassifyGenericParams(bilFn, erasedReturn,
                out var extraSubst, out var erasedPacks);
            var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, wrapperRef, wrapper,
                hostType.Canonical, out var ringReturn, extraSubst, erasedPacks);
            if (ringReturn.Canonical != memberReturn.Canonical
                && !(erasedReturn == ".any" && ringReturn.IsAny))
            {
                throw new MwNotSupportedException(
                    $"wildcard 模板返回类型与宿主成员不符（受控拒绝）: {proxy.Canonical} → "
                    + job.Plan.Member.Canonical);
            }
            var symbol = ProxyBakeSupport.SyntheticMember(
                wrapper.Canonical + ProxyBakeSupport.BakeInfix + hostType.Canonical + "$"
                + job.Plan.Member.SignatureKey + "@" + ringReturn.Canonical, proxy.Owner);
            return new MirFunction(symbol, ringReturn, body.Parameters, body.Locals,
                body.Blocks, false);
        }

        // 首环 wildcard 的原名槽 trampoline：取最外层 wrapper 隐藏槽地址
        //（MirGetWrapperAddr，刀3c 原地访问）→ 打包 symbol/空 named 包/
        // unnamed 装箱包 → 调首环 → 按原签名返回（void 成员丢弃环的
        // .any 结果）
        private static MirFunction BuildWildcardTrampoline(MwContext context,
            MirFunction original, MirFunction firstRing, string outerWrapper)
        {
            var fn = new MirFunction(original.Symbol, original.ReturnType, original.Parameters,
                new List<MirLocal>(original.Parameters), new List<MirBlock>(),
                original.IsEntrypoint);
            var wrapperLocal = ProxyWildcardAbi.FreshLocal(fn, "$mw.w.",
                MirType.Of(outerWrapper));
            var insts = new List<MirInst>
            {
                new MirGetWrapperAddr(new MirLocalOperand(".this"), outerWrapper, wrapperLocal),
            };
            var callArgs = ProxyWildcardAbi.EmitWildcardTrampolineSetup(context, fn, insts,
                firstRing, original, original.Symbol.Canonical,
                new MirLocalOperand(wrapperLocal));
            string? result = null;
            if (!original.ReturnType.IsVoid)
            {
                result = ProxyWildcardAbi.FreshLocal(fn, "$mw.r.", original.ReturnType);
            }
            insts.Add(new MirCall(firstRing.Symbol, callArgs, result));
            fn.AddBlock(new MirBlock("entry", insts,
                new MirRet(result == null ? null : new MirLocalOperand(result))));
            return fn;
        }

        // specific 环 inner → wildcard 下一环：具体实参打包成
        // (symbol 资源, 空 named 包, unnamed 装箱包) 再调下一环
        //（VM BuildRingInvokeArgs 的 nextWildcard 分支同口径）
        private static void LinkInnerToWildcard(MwContext context, MirFunction baked,
            BakeJob job, MirFunction nextRing, string nextWrapper)
        {
            var hostOp = new MirLocalOperand(ProxyBakeSupport.InnerHostLocal);
            var concreteTypes = ProxyWildcardAbi.ConcreteArgTypesFromMethod(job.Original);
            var temp = 0;
            foreach (var block in baked.Blocks)
            {
                var insts = block.InstructionList;
                for (var i = 0; i < insts.Count; i++)
                {
                    if (insts[i] is not MirInnerCall inner)
                    {
                        continue;
                    }
                    if (inner.Args.Count != concreteTypes.Count)
                    {
                        throw new CompilerInternalException(
                            "specific 环 inner 实参数与宿主成员签名不符: " + baked.Symbol.Canonical);
                    }
                    var wName = "$mw.inner.w." + temp++;
                    baked.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper)));
                    var symbolLocal = "$mw.inner.sym." + temp++;
                    baked.AddLocal(new MirLocal(symbolLocal, ProxyWildcardAbi.StringType));
                    var namedLocal = "$mw.inner.named." + temp++;
                    baked.AddLocal(new MirLocal(namedLocal, ProxyWildcardAbi.NamedPackType));
                    var unnamedLocal = "$mw.inner.unnamed." + temp++;
                    baked.AddLocal(new MirLocal(unnamedLocal, ProxyWildcardAbi.UnnamedPackType));
                    var pack = new List<MirInst>
                    {
                        new MirLoadResource(ProxyWildcardAbi.AddStringResource(context,
                            job.Plan.Member.Canonical), symbolLocal),
                    };
                    ProxyWildcardAbi.EmitEmptyNamedPack(pack, namedLocal);
                    ProxyWildcardAbi.EmitUnnamedPack(context, baked, pack, inner.Args,
                        concreteTypes, unnamedLocal, "$mw.inner.pack." + temp++);
                    var callArgs = ProxyWildcardAbi.BuildInvokeArgs(nextRing,
                        new MirLocalOperand(wName), symbolLocal, null, namedLocal, unnamedLocal,
                        null);
                    pack.Add(new MirCall(nextRing.Symbol, callArgs, inner.Result,
                        inner.ExcTarget));
                    insts[i] = new MirGetWrapperAddr(hostOp, nextWrapper, wName);
                    insts.InsertRange(i + 1, pack);
                    i += pack.Count;
                }
            }
        }

        // ===== wildcard 环 inner → 动态分派块 =====

        // 环内 MirInnerCall 改写：
        //   if (symbol == "M canonical") → 解包直进 M 的下一环（或终态）
        //   else → router(H, 下一层)(symbol, namedArgs, unnamedArgs) 重路由
        // inner 显式携带的 symbol/包操作数为准（模板可改写 symbol 重路由），
        // 与 VM ResolveInner/RerouteWildcardInner 同口径
        private static void RewriteWildcardInners(MwContext context, HostBake host,
            MemberPlan plan, MirFunction ring, int layer, MwMemberSymbol nextCallee,
            MirFunction? nextRingFn, string? nextWrapper, MwProxyMatchKind nextKind)
        {
            var valueParameterCount = 0;
            foreach (var parameter in ring.Parameters)
            {
                if (parameter.Name != ".this"
                    && !parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    valueParameterCount++;
                }
            }
            if (valueParameterCount != 1 && valueParameterCount != 3)
            {
                throw new MwNotSupportedException(
                    "wildcard 环值形参形状不支持（受控拒绝）: " + ring.Symbol.Canonical);
            }
            var blockSeq = ring.Blocks.Count;
            foreach (var block in new List<MirBlock>(ring.Blocks))
            {
                var insts = block.InstructionList;
                for (var i = 0; i < insts.Count; i++)
                {
                    if (insts[i] is not MirInnerCall inner)
                    {
                        continue;
                    }
                    RewriteOneInner(context, host, plan, ring, layer, block, i, inner,
                        valueParameterCount, nextCallee, nextRingFn, nextWrapper, nextKind,
                        ref blockSeq);
                    break; // 续行块已外移，本块指令扫描到此为止
                }
            }
        }

        private static void RewriteOneInner(MwContext context, HostBake host, MemberPlan plan,
            MirFunction ring, int layer, MirBlock block, int pos, MirInnerCall inner,
            int valueParameterCount, MwMemberSymbol nextCallee, MirFunction? nextRingFn,
            string? nextWrapper, MwProxyMatchKind nextKind, ref int blockSeq)
        {
            // inner 实参尾段 [symbol, named, unnamed]（VM：symbolIndex =
            // Count - 值形参数；具名/位置包恒居末两位）
            var symbolOp = inner.Args[inner.Args.Count - valueParameterCount];
            MirOperand namedOp;
            MirOperand unnamedOp;
            var preInsts = new List<MirInst>();
            if (valueParameterCount >= 3)
            {
                namedOp = inner.Args[inner.Args.Count - 2];
                unnamedOp = inner.Args[inner.Args.Count - 1];
            }
            else
            {
                // 模板省略包形参：补空包（VM BuildProxyArgs 空包兜底同口径）
                var namedLocal = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.named.",
                    ProxyWildcardAbi.NamedPackType);
                var unnamedLocal = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.unnamed.",
                    ProxyWildcardAbi.UnnamedPackType);
                ProxyWildcardAbi.EmitEmptyNamedPack(preInsts, namedLocal);
                preInsts.Add(new MirNewArray(ProxyWildcardAbi.UnnamedPackType,
                    System.Array.Empty<MirOperand>(), unnamedLocal));
                namedOp = new MirLocalOperand(namedLocal);
                unnamedOp = new MirLocalOperand(unnamedLocal);
            }

            var insts = block.InstructionList;
            var cont = new MirBlock("mw.wc.cont." + blockSeq++,
                insts.GetRange(pos + 1, insts.Count - pos - 1), block.Terminator);
            var hit = new MirBlock("mw.wc.hit." + blockSeq++, new List<MirInst>(),
                new MirBranch(cont.Id));
            var miss = new MirBlock("mw.wc.miss." + blockSeq++, new List<MirInst>(),
                new MirBranch(cont.Id));
            insts.RemoveRange(pos, insts.Count - pos);
            insts.AddRange(preInsts);
            var lit = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.lit.", ProxyWildcardAbi.StringType);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddStringResource(context, plan.Member.Canonical), lit));
            var cmp = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.eq.", ProxyWildcardAbi.BoolType);
            insts.Add(new MirBinaryIntrinsic(BilBinaryOp.CmpEq, symbolOp,
                new MirLocalOperand(lit), ProxyWildcardAbi.StringType,
                ProxyWildcardAbi.StringType, ProxyWildcardAbi.BoolType, cmp));
            block.Terminator = new MirCondBranch(new MirLocalOperand(cmp), hit.Id, miss.Id);

            var hostOp = new MirLocalOperand(ProxyBakeSupport.InnerHostLocal);
            var hitInsts = hit.InstructionList;
            var missInsts = miss.InstructionList;

            // hit：直进本成员的下一环（wildcard 透传三包；specific/终态
            // 解包回具体签名）
            List<MirOperand> hitArgs;
            bool nextVoid;
            if (nextKind == MwProxyMatchKind.Wildcard)
            {
                var w = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.w.", MirType.Of(nextWrapper!));
                hitInsts.Add(new MirGetWrapperAddr(hostOp, nextWrapper!, w));
                hitArgs = WildcardRingCallArgs(nextRingFn!, new MirLocalOperand(w),
                    symbolOp, namedOp, unnamedOp);
                nextVoid = nextRingFn!.ReturnType.IsVoid;
            }
            else
            {
                if (nextKind == MwProxyMatchKind.Specific)
                {
                    var unpacked = ProxyWildcardAbi.EmitUnpackArgs(context, ring, hitInsts,
                        unnamedOp, plan.Original!, plan.Member);
                    var w = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.w.",
                        MirType.Of(nextWrapper!));
                    hitInsts.Add(new MirGetWrapperAddr(hostOp, nextWrapper!, w));
                    hitArgs = new List<MirOperand> { new MirLocalOperand(w) };
                    hitArgs.AddRange(unpacked);
                }
                else
                {
                    // 终态（遗6 泛型宿主成员）：方法级 typeid 随包解包、
                    // 类级 typeid 从宿主隐藏字段补齐，按 wrapped 体参数序
                    hitArgs = ProxyWildcardAbi.EmitTerminalArgs(context, ring, hitInsts,
                        hostOp, unnamedOp, plan.Original!, plan.Member);
                }
                nextVoid = plan.Original!.ReturnType.IsVoid;
            }
            hitInsts.Add(new MirCall(nextCallee, hitArgs,
                inner.Result != null && !nextVoid ? inner.Result : null, inner.ExcTarget));
            if (inner.Result != null && nextVoid)
            {
                // 下一落点返回 void：续行槽补 .any 零值胖引用（环 ABI 擦除形态）
                hitInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddNullAnyResource(context), inner.Result));
            }

            // miss：router 重路由（从新符号的下一层起重进链）
            var router = RouterSymbol(host.Type, layer + 1);
            host.RouterLayers.Add(layer + 1);
            string? routed = null;
            if (inner.Result != null)
            {
                routed = ProxyWildcardAbi.FreshLocal(ring, "$mw.wc.rt.", ProxyWildcardAbi.AnyType);
            }
            missInsts.Add(new MirCall(router,
                new List<MirOperand> { hostOp, symbolOp, namedOp, unnamedOp },
                routed, inner.ExcTarget));
            if (inner.Result != null)
            {
                ProxyWildcardAbi.EmitUnboxFromAny(context, missInsts,
                    new MirLocalOperand(routed!), inner.Result,
                    ring.FindLocal(inner.Result).Type);
            }

            ring.AddBlock(hit);
            ring.AddBlock(miss);
            ring.AddBlock(cont);
        }

        // 按 wildcard 环形参序拼调用实参（.this/symbol/.kwargs/.vargs 映射；
        // 特化后无 .generic 残留）。internal：刀4 entry 环 inner 链接复用
        internal static List<MirOperand> WildcardRingCallArgs(MirFunction ringFn,
            MirOperand wrapper, MirOperand symbol, MirOperand named, MirOperand unnamed)
        {
            var args = new List<MirOperand>();
            foreach (var parameter in ringFn.Parameters)
            {
                if (parameter.Name == ".this")
                {
                    args.Add(wrapper);
                }
                else if (parameter.Name == "symbol" || parameter.Name == ".name")
                {
                    args.Add(symbol);
                }
                else if (parameter.Name.StartsWith(".kwargs.", System.StringComparison.Ordinal))
                {
                    args.Add(named);
                }
                else if (parameter.Name.StartsWith(".vargs.", System.StringComparison.Ordinal))
                {
                    args.Add(unnamed);
                }
                else if (parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    throw new CompilerInternalException(
                        "wildcard 环残留 .generic 形参: " + ringFn.Symbol.Canonical);
                }
                else
                {
                    args.Add(new MirLocalOperand(parameter.Name));
                }
            }
            return args;
        }

        // ===== router（每 (宿主, 层界) 一个） =====

        // 命名：H$.mw.router.<fromLayer>(symbol,namedArgs,unnamedArgs)@.any
        //（internal：刀4 CallWildcardLoweringPass 的 entry 链末环落点复用）
        internal static MwMemberSymbol RouterSymbol(MwTypeSymbol host, int fromLayer) =>
            ProxyBakeSupport.SyntheticMember(host.Canonical + "$.mw.router." + fromLayer
                + "(symbol:.string,namedArgs:" + ProxyWildcardAbi.NamedPackType.Canonical
                + ",unnamedArgs:" + ProxyWildcardAbi.UnnamedPackType.Canonical + ")@.any",
                owner: null);

        private static void BuildRouters(MwContext context, MirModule mir, HostBake host)
        {
            if (host.RouterLayers.Count == 0)
            {
                return;
            }
            var layers = new List<int>(host.RouterLayers);
            layers.Sort();
            foreach (var layer in layers)
            {
                BuildRouter(context, mir, host, layer);
            }
        }

        // router 体：if 链按 symbol 字符串比对——先覆盖字段访问器符号
        //（H$.get|set.<名>@T 形态，刀3b：按 VM RerouteWildcardInner 语义
        // 自 fromLayer 起进该字段的 Entity get/set 环链，访问器成员不再
        // 落方法分支），再覆盖宿主全部可烘焙方法/运算符（声明序）：命中
        // → 自 fromLayer 起重进该成员链（有环）或直调原始 fn（$.wrapped.
        // 体 / 原名 fn——绝不调 trampoline，防重入外层）；miss → 抛
        // core.NoSuchMethodException
        private static void BuildRouter(MwContext context, MirModule mir, HostBake host,
            int fromLayer)
        {
            var symbol = RouterSymbol(host.Type, fromLayer);
            var parameters = new List<MirLocal>
            {
                new MirLocal(".this", MirType.Of(host.Type.Canonical)),
                new MirLocal("symbol", ProxyWildcardAbi.StringType),
                new MirLocal("namedArgs", ProxyWildcardAbi.NamedPackType),
                new MirLocal("unnamedArgs", ProxyWildcardAbi.UnnamedPackType),
            };
            var fn = new MirFunction(symbol, ProxyWildcardAbi.AnyType, parameters,
                new List<MirLocal>(parameters), new List<MirBlock>(), false);
            var thisOp = new MirLocalOperand(".this");
            var symbolOp = new MirLocalOperand("symbol");
            var namedOp = new MirLocalOperand("namedArgs");
            var unnamedOp = new MirLocalOperand("unnamedArgs");

            // 分支表：字段访问器分支（含旁路落点解析）在前，方法/运算符
            // 成员分支在后；访问器成员不进成员分支（其符号按字段链路由）
            var branches = new List<RouterBranch>();
            foreach (var target in host.FieldTargets)
            {
                MwMemberSymbol? bypass = null;
                foreach (var plan in host.Members)
                {
                    if (plan.Member.Canonical == target.AccessorSymbol)
                    {
                        bypass = plan.Terminal ?? plan.Original?.Symbol;
                        break;
                    }
                }
                branches.Add(new RouterBranch(target.AccessorSymbol, target, null, bypass));
            }
            foreach (var plan in host.Members)
            {
                if (plan.Original != null && !IsFieldAccessor(plan.Member))
                {
                    branches.Add(new RouterBranch(plan.Member.Canonical, null, plan, null));
                }
            }
            var blocks = new List<MirBlock>();
            for (var k = 0; k < branches.Count; k++)
            {
                var branch = branches[k];
                var checkId = k == 0 ? "entry" : "mw.rt.chk." + k;
                var caseId = "mw.rt.case." + k;
                var nextId = k + 1 < branches.Count ? "mw.rt.chk." + (k + 1) : "mw.rt.miss";
                var checkInsts = new List<MirInst>();
                var lit = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.lit.",
                    ProxyWildcardAbi.StringType);
                checkInsts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddStringResource(context, branch.Symbol), lit));
                var cmp = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.eq.", ProxyWildcardAbi.BoolType);
                checkInsts.Add(new MirBinaryIntrinsic(BilBinaryOp.CmpEq, symbolOp,
                    new MirLocalOperand(lit), ProxyWildcardAbi.StringType,
                    ProxyWildcardAbi.StringType, ProxyWildcardAbi.BoolType, cmp));
                blocks.Add(new MirBlock(checkId, checkInsts,
                    new MirCondBranch(new MirLocalOperand(cmp), caseId, nextId)));

                var caseInsts = new List<MirInst>();
                var outLocal = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.out.",
                    ProxyWildcardAbi.AnyType);
                if (branch.Field != null)
                {
                    FieldProxyBakingPass.EmitRouterFieldBranch(context, mir, fn, caseInsts,
                        branch.Field, fromLayer, branch.AccessorBypass, thisOp, unnamedOp,
                        outLocal);
                }
                else
                {
                    EmitRouteBranch(context, fn, caseInsts, branch.Plan!, fromLayer,
                        thisOp, symbolOp, namedOp, unnamedOp, outLocal);
                }
                blocks.Add(new MirBlock(caseId, caseInsts,
                    new MirRet(new MirLocalOperand(outLocal))));
            }

            // miss：抛 core.NoSuchMethodException（VM call??? 链末同文案
            // 「未路由的降级请求：」+ symbol）
            var missInsts = new List<MirInst>();
            if (branches.Count == 0)
            {
                // 零分支宿主：entry 直接落 miss
                blocks.Add(new MirBlock("entry", missInsts, new MirUnreachable()));
                missInsts = blocks[0].InstructionList;
            }
            EmitThrowNoSuchMethod(context, fn, missInsts, symbolOp);
            if (branches.Count == 0)
            {
                blocks[0].Terminator = new MirRetThrow();
            }
            else
            {
                blocks.Add(new MirBlock("mw.rt.miss", missInsts, new MirRetThrow()));
            }
            foreach (var block in blocks)
            {
                fn.AddBlock(block);
            }
            mir.AddFunction(fn);
        }

        // 字段访问器成员判定（BilAccessorModifier）：其符号（H$.get|set.
        // <名>@T）按 VM RerouteWildcardInner 语义路由进字段 Entity 环链，
        // 不落方法成员分支
        private static bool IsFieldAccessor(MwMemberSymbol member)
        {
            foreach (var modifier in member.Declaration.Modifiers)
            {
                if (modifier is BilAccessorModifier)
                {
                    return true;
                }
            }
            return false;
        }

        // router 分支：字段访问器（Field 非空，AccessorBypass 为旁路落点）
        // 或方法/运算符成员（Plan 非空）
        private sealed class RouterBranch
        {
            internal RouterBranch(string symbol, FieldProxyBakingPass.RouterFieldTarget? field,
                MemberPlan? plan, MwMemberSymbol? accessorBypass)
            {
                Symbol = symbol;
                Field = field;
                Plan = plan;
                AccessorBypass = accessorBypass;
            }

            internal string Symbol { get; }
            internal FieldProxyBakingPass.RouterFieldTarget? Field { get; }
            internal MemberPlan? Plan { get; }
            internal MwMemberSymbol? AccessorBypass { get; }
        }

        // router 单分支：symbol == X → 自 fromLayer 起首个命中环（wildcard
        // 透传三包 / specific 解包）或直调 X 原始 fn；结果装箱 .any，
        // void 落 .any 零值胖引用
        private static void EmitRouteBranch(MwContext context, MirFunction fn,
            List<MirInst> insts, MemberPlan plan, int fromLayer, MirOperand thisOp,
            MirOperand symbolOp, MirOperand namedOp, MirOperand unnamedOp, string outLocal)
        {
            RingInfo? ring = null;
            foreach (var candidate in plan.Rings)
            {
                if (candidate.Layer >= fromLayer && (ring == null || candidate.Layer < ring.Layer))
                {
                    ring = candidate;
                }
            }
            if (ring != null && ring.Kind == MwProxyMatchKind.Wildcard)
            {
                var w = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.w.", MirType.Of(ring.Wrapper));
                insts.Add(new MirGetWrapperAddr(thisOp, ring.Wrapper, w));
                var callArgs = WildcardRingCallArgs(ring.Fn, new MirLocalOperand(w),
                    symbolOp, namedOp, unnamedOp);
                EmitBoxedCall(context, fn, insts, ring.Fn.Symbol, callArgs, ring.ReturnType,
                    outLocal);
                return;
            }
            List<MirOperand> args;
            MwMemberSymbol target;
            MirType returnType;
            if (ring != null)
            {
                // specific 环入口：receiver = 该层 wrapper 隐藏槽地址（刀3c）
                var unpacked = ProxyWildcardAbi.EmitUnpackArgs(context, fn, insts, unnamedOp,
                    plan.Original!, plan.Member);
                var w = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.w.", MirType.Of(ring.Wrapper));
                insts.Add(new MirGetWrapperAddr(thisOp, ring.Wrapper, w));
                args = new List<MirOperand> { new MirLocalOperand(w) };
                args.AddRange(unpacked);
                target = ring.Fn.Symbol;
                returnType = ring.ReturnType;
            }
            else
            {
                // 无环直调：有烘焙用 $.wrapped. 体，否则原名 fn——绝不调
                // trampoline（防重入外层）。遗6 泛型宿主成员：方法级
                // typeid 随包解包、类级 typeid 从宿主隐藏字段补齐
                args = ProxyWildcardAbi.EmitTerminalArgs(context, fn, insts, thisOp,
                    unnamedOp, plan.Original!, plan.Member);
                target = plan.Terminal ?? plan.Original!.Symbol;
                returnType = plan.Original!.ReturnType;
            }
            EmitBoxedCall(context, fn, insts, target, args, returnType, outLocal);
        }

        // 调用 + 结果装箱 .any（void → .any 零值胖引用；.any → 直通；
        // 其余按值/引用形态 box/copy）
        private static void EmitBoxedCall(MwContext context, MirFunction fn,
            List<MirInst> insts, MwMemberSymbol target, List<MirOperand> args,
            MirType returnType, string outLocal)
        {
            if (returnType.IsVoid)
            {
                insts.Add(new MirCall(target, args, null));
                insts.Add(new MirLoadResource(ProxyWildcardAbi.AddNullAnyResource(context),
                    outLocal));
                return;
            }
            if (returnType.IsAnyOrObject)
            {
                insts.Add(new MirCall(target, args, outLocal));
                return;
            }
            var result = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.r.", returnType);
            insts.Add(new MirCall(target, args, result));
            ProxyWildcardAbi.EmitBoxToAny(context, insts, new MirLocalOperand(result),
                returnType, outLocal);
        }

        // 构造 core.NoSuchMethodException("未路由的降级请求：" + symbol)
        // 并抛出（传播出函数；init 族恒可达，MW9b-G 白名单同口径）。
        // internal：刀4 dispatch 全不中分支复用（VM CallWildcard hook
        // 与 TryStartCallChain 链末同文案）
        internal static void EmitThrowNoSuchMethod(MwContext context, MirFunction fn,
            List<MirInst> insts, MirOperand symbolOp)
        {
            var prefix = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.msg.",
                ProxyWildcardAbi.StringType);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddStringResource(context, "未路由的降级请求："), prefix));
            var msg = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.msg.", ProxyWildcardAbi.StringType);
            insts.Add(new MirBinaryIntrinsic(BilBinaryOp.Add, new MirLocalOperand(prefix),
                symbolOp, ProxyWildcardAbi.StringType, ProxyWildcardAbi.StringType,
                ProxyWildcardAbi.StringType, msg));
            var excType = context.Symbols.FindTypeByRef("core::NoSuchMethodException")
                ?? throw new CompilerInternalException("core::NoSuchMethodException 类型缺失");
            var init = FindInit(excType, "text")
                ?? throw new CompilerInternalException(
                    "core::NoSuchMethodException 缺 init(text: String)");
            var initWrapper = context.Symbols.FindMember(
                excType.Canonical + "$..init.wrapper()@.void");
            var exc = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.exc.",
                MirType.Of(excType.Canonical));
            insts.Add(new MirNewObject(excType, initWrapper, init,
                new List<MirOperand> { new MirLocalOperand(msg) }, exc));
            insts.Add(new MirThrow(new MirLocalOperand(exc), null));
        }

        // init(text: String) 解析（ExceptionEmitter.ResolveInit 同口径：
        // init(typeName) 同签名，以首形参名区分）
        private static MwMemberSymbol? FindInit(MwTypeSymbol type, string firstParamName)
        {
            foreach (var member in type.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count == 1
                    && signature.Parameters[0].Name == firstParamName)
                {
                    return member;
                }
            }
            return null;
        }

        // ===== 烘焙状态结构 =====

        private sealed class HostBake
        {
            internal HostBake(MwTypeSymbol type, IReadOnlyList<string> wrappers)
            {
                Type = type;
                Wrappers = wrappers;
            }

            internal MwTypeSymbol Type { get; }
            internal IReadOnlyList<string> Wrappers { get; }
            internal List<MemberPlan> Members { get; } = new();
            internal List<BakeJob> Jobs { get; } = new();
            internal HashSet<int> RouterLayers { get; } = new();
            // router get/set 分支目标（刀3b：字段访问器符号 → 字段 Entity 环链）
            internal List<FieldProxyBakingPass.RouterFieldTarget> FieldTargets { get; } = new();
        }

        private sealed class MemberPlan
        {
            internal MemberPlan(MwMemberSymbol member, List<MwProxyMatchKind> kinds,
                MirFunction? original)
            {
                Member = member;
                Kinds = kinds;
                Original = original;
            }

            internal MwMemberSymbol Member { get; }
            internal List<MwProxyMatchKind> Kinds { get; }
            internal MirFunction? Original { get; }
            internal MwMemberSymbol? Terminal { get; set; }
            // 已烘焙环（自内向外追加；Layer 为 wrapper 层下标）
            internal List<RingInfo> Rings { get; } = new();
        }

        private sealed class RingInfo
        {
            internal RingInfo(int layer, string wrapper, MwProxyMatchKind kind, MirFunction fn)
            {
                Layer = layer;
                Wrapper = wrapper;
                Kind = kind;
                Fn = fn;
            }

            internal int Layer { get; }
            internal string Wrapper { get; }
            internal MwProxyMatchKind Kind { get; }
            internal MirFunction Fn { get; }
            internal MirType ReturnType => Fn.ReturnType;
        }

        private readonly struct BakeJob
        {
            internal BakeJob(MemberPlan plan, MirFunction original)
            {
                Plan = plan;
                Original = original;
            }

            internal MemberPlan Plan { get; }
            internal MirFunction Original { get; }
        }
    }
}
