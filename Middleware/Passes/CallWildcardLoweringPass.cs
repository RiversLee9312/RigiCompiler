using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// call??? 降级改写（MW10 刀4）：把 MirCall 目标为
    /// <c>core::Any$call???</c> 的调用（前端对静态类型未声明且链上有
    /// .proxy.* 的方法调用所发，§14.2/§15.6）改写为模块级分发 fn
    /// <c>$mw.call???.dispatch(receiver:.any,symbol:.string,namedArgs,
    /// unnamedArgs)@.any</c>。分发 fn 对候选宿主（继承闭包含 .proxy.*
    /// wildcard 的类型，按继承深度从深到浅——子类 wrapper 集优先命中，
    /// VM CollectEntityWrappers(实际类型) 同口径）做 rigi_type_is 实际
    /// 类型判定 if 链，命中调该宿主的 entry 链首环；全不中抛
    /// core.NoSuchMethodException（VM CallWildcard hook 同文案）。
    /// entry 链：每宿主按外层→内层把 wildcard .proxy.* 模板特化为
    /// <c>T$.mw.call???.entry.&lt;层&gt;</c> 环（TReturn 擦除 .any——
    /// §14.7 call symbol 返回段恒 .any；包 typeid 剔除，刀3a 设施复用），
    /// 环内 MirInnerCall 无 fast-path 原地改写（无原始方法可比）：有内
    /// 一层 entry 环 → 透传三包直调；末环 → router(T, fromLayer=层+1)
    ///（由 ProxyBakingPass 预建，VM RerouteWildcardInner 自下一层起搜
    /// proxy 同口径；手写 BIL 的 get/set/opr 类别符号落 router miss 抛
    /// NoSuchMethod——前端本不产出此类 call???）。inner 显式携带的
    /// symbol/包操作数为准（模板可改写 symbol 重路由）。读：MIR +
    /// Symbols + BIL 模板 fn；写：追加合成 fn、原地替换 call??? 调用点。
    /// 小改写 pass，不上 CRTP。
    /// </summary>
    public sealed class CallWildcardLoweringPass : IMwStage
    {
        public string Name => "CallWildcardLowering";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("CallWildcardLowering 要求 Mir 已挂载");
            // 无 call??? 调用点：零产物（entry 环/dispatch/router 预建全免）
            if (!ProxyBakingPass.HasCallWildcardSites(mir))
            {
                return;
            }
            var index = WrapperApplicationIndex.Build(context.Symbols);
            var bilBySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bilBySymbol[bilFn.Symbol] = bilFn;
            }

            // 候选宿主：闭包含 .proxy.* wildcard 的类型，深度从深到浅
            //（同深度按 canonical 字典序，产物确定）
            var candidates = new List<Candidate>();
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                var wrappers = index.EntityWrappers(type.Canonical);
                var wildcardLayers = new List<(int Layer, string Wrapper)>();
                for (var i = 0; i < wrappers.Count; i++)
                {
                    var wrapper = context.Symbols.FindTypeByRef(wrappers[i])
                        ?? throw new CompilerInternalException(
                            "Entity wrapper 类型缺失: " + wrappers[i]);
                    if (ProxyMatcher.FindProxy(wrapper, ".proxy.*",
                            BilProxyKind.Wildcard) != null)
                    {
                        wildcardLayers.Add((i, wrappers[i]));
                    }
                }
                if (wildcardLayers.Count > 0)
                {
                    candidates.Add(new Candidate(type, wildcardLayers,
                        InheritanceDepth(context, type)));
                }
            }
            candidates.Sort((a, b) =>
            {
                var byDepth = b.Depth.CompareTo(a.Depth);
                return byDepth != 0
                    ? byDepth
                    : System.StringComparer.Ordinal.Compare(a.Type.Canonical, b.Type.Canonical);
            });

            foreach (var candidate in candidates)
            {
                candidate.EntryFirst = BuildEntryChain(context, mir, bilBySymbol, candidate);
            }
            var dispatch = BuildDispatch(context, candidates);
            mir.AddFunction(dispatch);
            RewriteCallSites(mir, dispatch.Symbol);
        }

        // 继承深度（extends 跳数，环保护；外部/缺失基类即止）
        private static int InheritanceDepth(MwContext context, MwTypeSymbol type)
        {
            var depth = 0;
            var current = type;
            var guard = new HashSet<string>(System.StringComparer.Ordinal) { type.Canonical };
            while (current.Declaration.ExtendsType is { } baseRef
                && context.Symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                && guard.Add(baseType.Canonical))
            {
                depth++;
                current = baseType;
            }
            return depth;
        }

        // ===== entry 环链 =====

        // 自内向外逐层特化（内层先建，外层 inner 直调之）；返回最外层环
        private static MirFunction BuildEntryChain(MwContext context, MirModule mir,
            Dictionary<string, BilFunction> bilBySymbol, Candidate candidate)
        {
            MirFunction? nextRing = null;
            string? nextWrapper = null;
            for (var k = candidate.WildcardLayers.Count - 1; k >= 0; k--)
            {
                var (layer, wrapperRef) = candidate.WildcardLayers[k];
                var ring = SpecializeEntry(context, bilBySymbol, candidate.Type, wrapperRef, layer);
                // 末环（最内层）inner → router(T, 层+1)；其余 → 内一环
                RewriteEntryInners(context, ring, candidate.Type, nextRing, nextWrapper,
                    layer + 1);
                IndexOperatorLoweringPass.RewriteFunction(context, ring);
                AccessorLoweringPass.RewriteFunction(context, ring);
                FieldProxyBakingPass.RewriteFunction(context, ring);
                mir.AddFunction(ring);
                nextRing = ring;
                nextWrapper = wrapperRef;
            }
            return nextRing!;
        }

        // entry 环特化：wildcard 模板 TReturn 擦除 .any（§14.7/§14.8）、
        // 包 typeid 剔除；具体（非泛型）返回类型受控拒绝（call??? 胖值
        // ABI 恒以 .any 承载结果）
        private static MirFunction SpecializeEntry(MwContext context,
            Dictionary<string, BilFunction> bilBySymbol, MwTypeSymbol hostType,
            string wrapperRef, int layer)
        {
            var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("烘焙找不到 wrapper: " + wrapperRef);
            var proxy = ProxyMatcher.FindProxy(wrapper, ".proxy.*", BilProxyKind.Wildcard)
                ?? throw new CompilerInternalException(
                    "entry 环找不到 wildcard proxy 模板: " + wrapperRef);
            if (!bilBySymbol.TryGetValue(proxy.Canonical, out var bilFn))
            {
                throw new MwNotSupportedException(
                    "call??? entry 环 proxy 模板无 fn 体: " + proxy.Canonical);
            }
            ProxyWildcardAbi.ClassifyGenericParams(bilFn, ".any",
                out var extraSubst, out var erasedPacks);
            var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, wrapperRef, wrapper,
                hostType.Canonical, out var ringReturn, extraSubst, erasedPacks);
            if (!ringReturn.IsAny)
            {
                throw new MwNotSupportedException(
                    "call??? entry 环模板返回类型须为泛型 TReturn（擦除 .any，受控拒绝）: "
                    + proxy.Canonical);
            }
            var symbol = ProxyBakeSupport.SyntheticMember(
                hostType.Canonical + "$.mw.call???.entry." + layer
                + "(symbol:.string,namedArgs:" + ProxyWildcardAbi.NamedPackType.Canonical
                + ",unnamedArgs:" + ProxyWildcardAbi.UnnamedPackType.Canonical + ")@.any",
                proxy.Owner);
            return new MirFunction(symbol, ringReturn, body.Parameters, body.Locals,
                body.Blocks, false);
        }

        // 环内 MirInnerCall 原地改写（无 fast-path、无 CFG 变动）：
        //   有内一层 entry 环 → 取该层 wrapper 槽地址（MirGetWrapperAddr，
        //   刀3c 原地访问）透传 symbol/具名/位置三包直调；
        //   末环 → router(T, fromLayer)($mw.inner.host, symbol, named,
        //   unnamed) 重路由（命中已声明成员 → 自 fromLayer 起其链/原始体；
        //   未命中 → router miss 抛 NoSuchMethod）
        // inner 显式携带的 symbol/包操作数为准（模板可改写 symbol 重路由），
        // 与 VM ResolveInner/RerouteWildcardInner 同口径
        private static void RewriteEntryInners(MwContext context, MirFunction ring,
            MwTypeSymbol hostType, MirFunction? nextRing, string? nextWrapper, int routerFromLayer)
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
                    "call??? entry 环值形参形状不支持（受控拒绝）: " + ring.Symbol.Canonical);
            }
            var hostOp = new MirLocalOperand(ProxyBakeSupport.InnerHostLocal);
            var temp = 0;
            foreach (var block in ring.Blocks)
            {
                var insts = block.InstructionList;
                for (var i = 0; i < insts.Count; i++)
                {
                    if (insts[i] is not MirInnerCall inner)
                    {
                        continue;
                    }
                    // inner 实参尾段 [symbol, named, unnamed]（刀3a 同口径：
                    // symbolIndex = Count - 值形参数；包恒居末两位）
                    var symbolOp = inner.Args[inner.Args.Count - valueParameterCount];
                    MirOperand namedOp;
                    MirOperand unnamedOp;
                    var insertAt = i;
                    if (valueParameterCount >= 3)
                    {
                        namedOp = inner.Args[inner.Args.Count - 2];
                        unnamedOp = inner.Args[inner.Args.Count - 1];
                    }
                    else
                    {
                        // 模板省略包形参：补空包（刀3a 同口径）
                        var namedLocal = ProxyWildcardAbi.FreshLocal(ring, "$mw.cw.named.",
                            ProxyWildcardAbi.NamedPackType);
                        var unnamedLocal = ProxyWildcardAbi.FreshLocal(ring, "$mw.cw.unnamed.",
                            ProxyWildcardAbi.UnnamedPackType);
                        var pack = new List<MirInst>();
                        ProxyWildcardAbi.EmitEmptyNamedPack(pack, namedLocal);
                        pack.Add(new MirNewArray(ProxyWildcardAbi.UnnamedPackType,
                            System.Array.Empty<MirOperand>(), unnamedLocal));
                        insts.InsertRange(i, pack);
                        i += pack.Count;
                        namedOp = new MirLocalOperand(namedLocal);
                        unnamedOp = new MirLocalOperand(unnamedLocal);
                    }
                    if (nextRing != null)
                    {
                        var w = "$mw.cw.w." + temp++;
                        ring.AddLocal(new MirLocal(w, MirType.Of(nextWrapper!)));
                        var callArgs = ProxyBakingPass.WildcardRingCallArgs(nextRing,
                            new MirLocalOperand(w), symbolOp, namedOp, unnamedOp);
                        insts[i] = new MirGetWrapperAddr(hostOp, nextWrapper!, w);
                        insts.Insert(i + 1, new MirCall(nextRing.Symbol, callArgs,
                            inner.Result, inner.ExcTarget));
                        i++;
                    }
                    else
                    {
                        var router = ProxyBakingPass.RouterSymbol(hostType, routerFromLayer);
                        insts[i] = new MirCall(router,
                            new List<MirOperand> { hostOp, symbolOp, namedOp, unnamedOp },
                            inner.Result, inner.ExcTarget);
                    }
                }
            }
        }

        // ===== 模块级分发 fn =====

        // $mw.call???.dispatch(receiver:.any, symbol:.string, namedArgs,
        // unnamedArgs)@.any：候选类型 if 链（rigi_type_is 实际类型判定，
        // receiver 为胖值）→ 命中取宿主槽地址调 entry 链首环；全不中抛
        // NoSuchMethod（VM CallWildcard hook 同文案）
        private static MirFunction BuildDispatch(MwContext context, List<Candidate> candidates)
        {
            var symbol = ProxyBakeSupport.SyntheticMember(
                "$mw.call???.dispatch(receiver:.any,symbol:.string,namedArgs:"
                + ProxyWildcardAbi.NamedPackType.Canonical + ",unnamedArgs:"
                + ProxyWildcardAbi.UnnamedPackType.Canonical + ")@.any", owner: null);
            var parameters = new List<MirLocal>
            {
                new MirLocal("receiver", ProxyWildcardAbi.AnyType),
                new MirLocal("symbol", ProxyWildcardAbi.StringType),
                new MirLocal("namedArgs", ProxyWildcardAbi.NamedPackType),
                new MirLocal("unnamedArgs", ProxyWildcardAbi.UnnamedPackType),
            };
            var fn = new MirFunction(symbol, ProxyWildcardAbi.AnyType, parameters,
                new List<MirLocal>(parameters), new List<MirBlock>(), false);
            var receiverOp = new MirLocalOperand("receiver");
            var symbolOp = new MirLocalOperand("symbol");
            var namedOp = new MirLocalOperand("namedArgs");
            var unnamedOp = new MirLocalOperand("unnamedArgs");

            var blocks = new List<MirBlock>();
            for (var k = 0; k < candidates.Count; k++)
            {
                var candidate = candidates[k];
                var checkId = k == 0 ? "entry" : "mw.cd.chk." + k;
                var hitId = "mw.cd.hit." + k;
                var nextId = k + 1 < candidates.Count ? "mw.cd.chk." + (k + 1) : "mw.cd.miss";
                var checkInsts = new List<MirInst>();
                var cond = ProxyWildcardAbi.FreshLocal(fn, "$mw.cd.is.", ProxyWildcardAbi.BoolType);
                checkInsts.Add(new MirTypeCheck(MirTypeCheckKind.Is, receiverOp,
                    candidate.Type.Canonical, null, cond));
                blocks.Add(new MirBlock(checkId, checkInsts,
                    new MirCondBranch(new MirLocalOperand(cond), hitId, nextId)));

                var hitInsts = new List<MirInst>();
                var typed = ProxyWildcardAbi.FreshLocal(fn, "$mw.cd.t.",
                    MirType.Of(candidate.Type.Canonical));
                // typeis 命中后比特兼容（class 引用与 .any 同胖引用形态，
                // 刀3a EmitUnboxFromAny 引用直通同口径）
                hitInsts.Add(new MirCopyLocal(receiverOp, typed));
                var firstWrapper = candidate.WildcardLayers[0].Wrapper;
                var w = ProxyWildcardAbi.FreshLocal(fn, "$mw.cd.w.", MirType.Of(firstWrapper));
                hitInsts.Add(new MirGetWrapperAddr(new MirLocalOperand(typed), firstWrapper, w));
                var result = ProxyWildcardAbi.FreshLocal(fn, "$mw.cd.r.", ProxyWildcardAbi.AnyType);
                var callArgs = ProxyBakingPass.WildcardRingCallArgs(candidate.EntryFirst!,
                    new MirLocalOperand(w), symbolOp, namedOp, unnamedOp);
                hitInsts.Add(new MirCall(candidate.EntryFirst!.Symbol, callArgs, result));
                blocks.Add(new MirBlock(hitId, hitInsts,
                    new MirRet(new MirLocalOperand(result))));
            }

            var missInsts = new List<MirInst>();
            if (candidates.Count == 0)
            {
                // 零候选：entry 直接落 miss（VM 无 wrapper 即落 hook 默认抛）
                blocks.Add(new MirBlock("entry", missInsts, new MirRetThrow()));
                missInsts = blocks[0].InstructionList;
                ProxyBakingPass.EmitThrowNoSuchMethod(context, fn, missInsts, symbolOp);
            }
            else
            {
                ProxyBakingPass.EmitThrowNoSuchMethod(context, fn, missInsts, symbolOp);
                blocks.Add(new MirBlock("mw.cd.miss", missInsts, new MirRetThrow()));
            }
            foreach (var block in blocks)
            {
                fn.AddBlock(block);
            }
            return fn;
        }

        // 调用点改写：invoke core::Any$call??? → invoke dispatch（实参序
        // 不变——[receiver(.any), symbol, namedArgs, unnamedArgs]，§15.6；
        // 结果槽/异常边原样保留，返回 .any 后的 cast 由前端既有指令承担）
        private static void RewriteCallSites(MirModule mir, MwMemberSymbol dispatch)
        {
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        if (insts[i] is MirCall call
                            && BilSpellings.IsCallWildcardMethod(call.Target.Canonical))
                        {
                            insts[i] = new MirCall(dispatch, call.Args, call.Result,
                                call.ExcTarget);
                        }
                    }
                }
            }
        }

        // 候选宿主：类型 + 闭包内 .proxy.* wildcard 层表 + 继承深度
        private sealed class Candidate
        {
            internal Candidate(MwTypeSymbol type,
                List<(int Layer, string Wrapper)> wildcardLayers, int depth)
            {
                Type = type;
                WildcardLayers = wildcardLayers;
                Depth = depth;
            }

            internal MwTypeSymbol Type { get; }
            internal List<(int Layer, string Wrapper)> WildcardLayers { get; }
            internal int Depth { get; }
            internal MirFunction? EntryFirst { get; set; }
        }
    }
}
