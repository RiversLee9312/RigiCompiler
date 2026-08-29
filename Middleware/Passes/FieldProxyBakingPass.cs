using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// 字段读写 proxy 链烘焙（MW10 刀2 Value 面 + 刀3b Entity 面）：Value
    /// wrapper（字段自身 wrapped）与 Entity wrapper（宿主类型 wrapped）双源，
    /// 本 pass 是字段访问链的唯一拦截点。字段同时带 field wrapped(W) 与宿主
    /// wrapped(E) 时只跑 W 链（VM 现状短路语义，对齐保持）。
    /// Value 面：字段符号自身声明含 wrapped(W) 的 MirGetField/MirSetField
    /// 改写为 proxy 链。写链 outer→inner 逐环特化 .proxy.set 模板、inner
    /// 链接（下一环 receiver 经 MirGetWrapperField 从 InnerHostLocal 取），
    /// 链末落终态 fn（有用户 setter 则 MirCall，否则裸写 backing）；读链
    /// 终态（裸读或 MirCall 用户 getter）→ 环自内向外逐环变值。init 族 fn
    /// 内的写豁免 wrapper 链（仍经用户 setter）；读不豁免。cell 隐藏子类
    /// 的 getValue/setValue 整体壳化（链包在访问器体外侧，VM
    /// TryStartCellAccessorChain 同语义），使用点 invoke 不动。
    /// Entity 面：宿主类型 wrapped 且字段自身未 wrapped 的 MirGetField/
    /// MirSetField 改写为按字段名 specific（.proxy.get|set.&lt;名&gt;）优先、
    /// wildcard（.proxy.get|set.*）兜底、None 跳过的环链；环 receiver 经
    /// MirGetWrapper 取 Entity 隐藏槽。无层命中时按访问器兜底降级（有用户
    /// 访问器则调，否则保持裸访——AccessorLoweringPass 对宿主 wrapped
    /// 放行后此兜底归本 pass）。
    /// 读：Mir + Symbols + BIL 模板 fn；写：追加合成 fn、原地改写使用点
    /// 指令列表、替换 cell 访问器 fn。小改写 pass，不上 CRTP；共享设施
    /// 见 <see cref="ProxyBakeSupport"/>。
    /// </summary>
    public sealed class FieldProxyBakingPass : IMwStage
    {
        public string Name => "FieldProxyBaking";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("FieldProxyBaking 要求 Mir 已挂载");
            var state = new BakeState(context, mir);
            // 快照迭代：烘焙产物（环/终态/壳）不回流扫描
            foreach (var fn in new List<MirFunction>(mir.Functions))
            {
                UseSite.RewriteFunction(context, state, mir, fn, exemptField: null);
            }
        }

        // ProxyBakingPass 烘焙体的重跑钩子（与 IndexOperatorLoweringPass /
        // AccessorLoweringPass 的 RewriteFunction 同形态）：specific Entity
        // proxy 体内访问 wrapped 字段同样成链
        internal static void RewriteFunction(MwContext context, MirFunction fn)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("FieldProxyBaking 要求 Mir 已挂载");
            UseSite.RewriteFunction(context, new BakeState(context, mir), mir, fn,
                exemptField: null);
        }

        // ===== router get/set 分支钩子（刀3b：ProxyBakingPass.BuildRouter
        // 调用；本 pass 在 ProxyBakingPass 之前运行，字段链环已烘焙，此处
        // 经 Known 定名回收或按需补烘，不重复烘焙） =====

        // 宿主已声明实例字段的访问器路由项：symbol = H$.get|set.<名>@T
        // 形态（访问器 canonical）→ 字段符号与类别
        internal sealed class RouterFieldTarget
        {
            internal RouterFieldTarget(string accessorSymbol, string fieldSymbol, bool isSet)
            {
                AccessorSymbol = accessorSymbol;
                FieldSymbol = fieldSymbol;
                IsSet = isSet;
            }

            internal string AccessorSymbol { get; }
            internal string FieldSymbol { get; }
            internal bool IsSet { get; }
        }

        // 收集宿主的字段访问器路由项。字段自身 wrapped 的 W 短路不列
        //（Entity 链不跑，符号维持 miss NoSuchMethod——VM 对该形态经
        // 重路由仍可进 Entity 环链，属运行期逐名边角，native 不落分支）；
        // 静态字段不列（静态面保持受控拒绝）
        internal static List<RouterFieldTarget> CollectRouterFieldTargets(MwContext context,
            WrapperApplicationIndex index, MwTypeSymbol host)
        {
            var targets = new List<RouterFieldTarget>();
            foreach (var member in host.Members)
            {
                if (member.Declaration.Kind != BilMemberKind.Field
                    || member.Canonical.Contains("#.static.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                if (index.FieldWrappers(member.Canonical).Count > 0)
                {
                    continue;
                }
                if (ImplBinder.FindAccessor(context.Symbols, member.Canonical,
                        BilAccessorKind.Getter, "") is { } getter)
                {
                    targets.Add(new RouterFieldTarget(getter.Canonical, member.Canonical,
                        false));
                }
                if (ImplBinder.FindAccessor(context.Symbols, member.Canonical,
                        BilAccessorKind.Setter, "") is { } setter)
                {
                    targets.Add(new RouterFieldTarget(setter.Canonical, member.Canonical,
                        true));
                }
            }
            return targets;
        }

        // router 单字段分支发射：自 fromLayer 起进该字段的 Entity 环链
        //（VM RerouteWildcardInner 的 Get/Set 重路由同口径），结果写
        // outLocal（.any）。
        // get：终态旁路（accessorBypass = getter 的 $.wrapped. 体 / 原始
        // getter fn；null → 裸读 backing）产值 → 命中环按层升序推进变值
        //（VM AdvanceRing 层推进同序）→ 装箱 .any；
        // set：unnamed 包末位拆回字段值（VM ValueOfSetInner 同口径）→
        // 首个 Layer >= fromLayer 的命中环（环间 inner 已链接，链末终态
        // 旁路），无环直调终态 → .any 零值胖引用
        internal static void EmitRouterFieldBranch(MwContext context, MirModule mir,
            MirFunction fn, List<MirInst> insts, RouterFieldTarget target, int fromLayer,
            MwMemberSymbol? accessorBypass, MirOperand thisOp, MirOperand unnamedOp,
            string outLocal)
        {
            var state = new BakeState(context, mir);
            var (host, name, typeRef) = ChainBake.ParseField(target.FieldSymbol);
            var typeC = MirType.Of(typeRef).Canonical;
            var valueType = MirType.Of(typeRef);
            if (!target.IsSet)
            {
                var value = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fv.", valueType);
                if (accessorBypass != null)
                {
                    insts.Add(new MirCall(accessorBypass, new List<MirOperand> { thisOp },
                        value));
                }
                else
                {
                    insts.Add(new MirGetField(thisOp, target.FieldSymbol, value));
                }
                if (EntityChainBake.EnsureGetChain(context, mir, state, target.FieldSymbol)
                        is { } chain)
                {
                    foreach (var ring in chain.Rings)
                    {
                        if (ring.Layer < fromLayer)
                        {
                            continue;
                        }
                        var w = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fw.",
                            MirType.Of(ring.Wrapper));
                        insts.Add(new MirGetWrapperAddr(thisOp, ring.Wrapper, w));
                        var next = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fv.", valueType);
                        insts.Add(new MirCall(ring.Symbol,
                            new List<MirOperand>
                                { new MirLocalOperand(w), new MirLocalOperand(value) },
                            next));
                        value = next;
                    }
                }
                ProxyWildcardAbi.EmitBoxToAny(context, insts, new MirLocalOperand(value),
                    valueType, outLocal);
                return;
            }
            var length = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fl.",
                ProxyWildcardAbi.I32Type);
            insts.Add(new MirGetField(unnamedOp, TypeLayout.ArrayLengthField, length));
            var one = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fi.", ProxyWildcardAbi.I32Type);
            insts.Add(new MirLoadResource(ProxyWildcardAbi.AddI32Resource(context, 1), one));
            var packIndex = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fi.",
                ProxyWildcardAbi.I32Type);
            insts.Add(new MirBinaryIntrinsic(BilBinaryOp.Sub, new MirLocalOperand(length),
                new MirLocalOperand(one), ProxyWildcardAbi.I32Type, ProxyWildcardAbi.I32Type,
                ProxyWildcardAbi.I32Type, packIndex));
            var elem = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fe.", ProxyWildcardAbi.AnyType);
            insts.Add(new MirGetArray(unnamedOp, new MirLocalOperand(packIndex),
                ProxyWildcardAbi.UnnamedPackType, elem));
            var setValue = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fv.", valueType);
            ProxyWildcardAbi.EmitUnboxFromAny(context, insts, new MirLocalOperand(elem),
                setValue, valueType);
            EntityRing? first = null;
            if (EntityChainBake.EnsureSetChain(context, mir, state, target.FieldSymbol)
                    is { } entry)
            {
                foreach (var ring in entry.Rings)
                {
                    if (ring.Layer >= fromLayer)
                    {
                        first = ring;
                        break;
                    }
                }
            }
            if (first != null)
            {
                var w = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fw.",
                    MirType.Of(first.Wrapper));
                insts.Add(new MirGetWrapperAddr(thisOp, first.Wrapper, w));
                var args = new List<MirOperand> { new MirLocalOperand(w) };
                if (first.Kind == MwProxyMatchKind.Wildcard)
                {
                    var sym = ProxyWildcardAbi.FreshLocal(fn, "$mw.rt.fs.",
                        ProxyWildcardAbi.StringType);
                    insts.Add(new MirLoadResource(
                        ProxyWildcardAbi.AddStringResource(context, target.FieldSymbol), sym));
                    args.Add(new MirLocalOperand(sym));
                }
                args.Add(new MirLocalOperand(setValue));
                insts.Add(new MirCall(first.Symbol, args, null));
            }
            else
            {
                var terminal = EntityChainBake.EnsureSetTerminal(context, mir, state,
                    target.FieldSymbol, host, name, typeRef, typeC);
                insts.Add(new MirCall(terminal,
                    new List<MirOperand> { thisOp, new MirLocalOperand(setValue) }, null));
            }
            insts.Add(new MirLoadResource(ProxyWildcardAbi.AddNullAnyResource(context),
                outLocal));
        }

        // 烘焙状态：应用索引 + BIL 模板表 + mir 已知 canonical（去重回收）+
        // 链缓存 + 在烘 guard（proxy 模板体病态自指宿主同一 wrapped 字段时
        // 保持裸访——VM 同场景为运行期无限递归，两边都不承诺语义）
        private sealed class BakeState
        {
            internal BakeState(MwContext context, MirModule mir)
            {
                Index = WrapperApplicationIndex.Build(context.Symbols);
                BilBySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
                foreach (var bilFn in context.Module.Functions)
                {
                    BilBySymbol[bilFn.Symbol] = bilFn;
                }
                Known = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var fn in mir.Functions)
                {
                    Known.Add(fn.Symbol.Canonical);
                }
            }

            internal WrapperApplicationIndex Index { get; }
            internal Dictionary<string, BilFunction> BilBySymbol { get; }
            internal HashSet<string> Known { get; }
            internal Dictionary<string, MwMemberSymbol> SetEntries { get; } =
                new(System.StringComparer.Ordinal);
            internal Dictionary<string, GetChain> GetChains { get; } =
                new(System.StringComparer.Ordinal);
            // Entity 面链缓存（null = 无层命中，走访问器兜底）
            internal Dictionary<string, EntityGetChain?> EntityGetChains { get; } =
                new(System.StringComparer.Ordinal);
            internal Dictionary<string, EntitySetEntry?> EntitySetEntries { get; } =
                new(System.StringComparer.Ordinal);
            internal HashSet<string> InProgress { get; } =
                new(System.StringComparer.Ordinal);
        }

        // get 链烘焙产物：终态符号 + 环符号（与 FieldWrappers 同序 outer→inner）
        private sealed class GetChain
        {
            internal GetChain(MwMemberSymbol terminal, List<MwMemberSymbol> rings)
            {
                Terminal = terminal;
                Rings = rings;
            }

            internal MwMemberSymbol Terminal { get; }
            internal List<MwMemberSymbol> Rings { get; }
        }

        // ===== 链烘焙（每字段每 wrapper 每类别一个合成 fn） =====
        private static class ChainBake
        {
            internal static (string Host, string Name, string TypeRef) ParseField(
                string fieldSymbol)
            {
                if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol, out var owner,
                        out _, out var fieldType))
                {
                    throw new CompilerInternalException("字段符号不可解析: " + fieldSymbol);
                }
                var hash = fieldSymbol.IndexOf('#');
                var at = fieldSymbol.LastIndexOf('@');
                return (owner, fieldSymbol.Substring(hash + 1, at - hash - 1), fieldType);
            }

            // 终态：<宿主>$<字段>$.wrapped.set|get——链末落点（含 cell 壳的
            // 原访问器体外移形态），符号命名不含 `#`，对齐 ProxyBakingPass 先例
            internal static string SetTerminalCanonical(string host, string name, string typeC) =>
                host + "$" + name + ProxyBakeSupport.WrappedInfix + "set(value:" + typeC + ")@.void";

            internal static string GetTerminalCanonical(string host, string name, string typeC) =>
                host + "$" + name + ProxyBakeSupport.WrappedInfix + "get()@" + typeC;

            // 环：<wrapper>$.bake.<宿主>$<字段>$.set|$.get
            internal static string RingCanonical(string wrapperRef, string host, string name,
                string typeC, bool isSet) =>
                wrapperRef + ProxyBakeSupport.BakeInfix + host + "$" + name
                + (isSet ? "$.set(value:" + typeC + ")@.void" : "$.get(value:" + typeC + ")@"
                    + typeC);

            // set 链：outer→inner 逐环特化 + LinkInner 链接（环间经
            // MirGetWrapperField 取下一层 wrapper 实例），链末 inner 落终态。
            // terminalOverride：cell 壳化路径外移的原 setValue 体（VM 链末
            // 回调 setValue 同语义）；null → 合成终态（用户 setter / 裸写）
            internal static MwMemberSymbol EnsureSetChain(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol, MwMemberSymbol? terminalOverride = null)
            {
                if (state.SetEntries.TryGetValue(fieldSymbol, out var cached))
                {
                    return cached;
                }
                var (host, name, typeRef) = ParseField(fieldSymbol);
                var typeC = MirType.Of(typeRef).Canonical;
                var wrappers = state.Index.FieldWrappers(fieldSymbol);
                var entryCanonical = RingCanonical(wrappers[0], host, name, typeC, isSet: true);
                if (state.Known.Contains(entryCanonical))
                {
                    // 跨 BakeState 的钩子重跑路径：产物已在 mir，按定名回收
                    return state.SetEntries[fieldSymbol] = FindBaked(mir, entryCanonical);
                }
                var terminal = terminalOverride
                    ?? BuildSetTerminal(context, mir, state, fieldSymbol, host, name, typeRef,
                        typeC);
                state.InProgress.Add(fieldSymbol + "|set");
                try
                {
                    MwMemberSymbol nextCallee = terminal;
                    string? nextWrapper = null;
                    for (var i = wrappers.Count - 1; i >= 0; i--)
                    {
                        var baked = BakeRing(context, state, fieldSymbol, wrappers[i], host, name,
                            typeRef, typeC, isSet: true);
                        ProxyBakeSupport.LinkInner(baked, nextCallee, nextWrapper,
                            wrapperField: fieldSymbol);
                        IndexOperatorLoweringPass.RewriteFunction(context, baked);
                        AccessorLoweringPass.RewriteFunction(context, baked);
                        // proxy 模板体内访问宿主其他 wrapped 字段同样成链
                        UseSite.RewriteFunction(context, state, mir, baked, exemptField: null);
                        mir.AddFunction(baked);
                        state.Known.Add(baked.Symbol.Canonical);
                        nextCallee = baked.Symbol;
                        nextWrapper = wrappers[i];
                    }
                    return state.SetEntries[fieldSymbol] = nextCallee;
                }
                finally
                {
                    state.InProgress.Remove(fieldSymbol + "|set");
                }
            }

            // get 链：终态（裸读 backing / MirCall 用户 getter / cell 壳原
            // getValue 体）→ 环符号表（outer→inner 与 wrappers 对齐）；环体
            // 无 MirInnerCall，不需 LinkInner，调用序由使用点/壳自内向外排
            internal static GetChain EnsureGetChain(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol, MwMemberSymbol? terminalOverride = null)
            {
                if (state.GetChains.TryGetValue(fieldSymbol, out var cached))
                {
                    return cached;
                }
                var (host, name, typeRef) = ParseField(fieldSymbol);
                var typeC = MirType.Of(typeRef).Canonical;
                var wrappers = state.Index.FieldWrappers(fieldSymbol);
                var terminalCanonical = GetTerminalCanonical(host, name, typeC);
                if (state.Known.Contains(terminalCanonical))
                {
                    var rings = new List<MwMemberSymbol>(wrappers.Count);
                    foreach (var wrapperRef in wrappers)
                    {
                        rings.Add(FindBaked(mir,
                            RingCanonical(wrapperRef, host, name, typeC, isSet: false)));
                    }
                    return state.GetChains[fieldSymbol] =
                        new GetChain(FindBaked(mir, terminalCanonical), rings);
                }
                var terminal = terminalOverride
                    ?? BuildGetTerminal(context, mir, state, fieldSymbol, host, typeRef, typeC);
                state.InProgress.Add(fieldSymbol + "|get");
                try
                {
                    var rings = new List<MwMemberSymbol>(wrappers.Count);
                    foreach (var wrapperRef in wrappers)
                    {
                        var baked = BakeRing(context, state, fieldSymbol, wrapperRef, host, name,
                            typeRef, typeC, isSet: false);
                        IndexOperatorLoweringPass.RewriteFunction(context, baked);
                        AccessorLoweringPass.RewriteFunction(context, baked);
                        UseSite.RewriteFunction(context, state, mir, baked, exemptField: null);
                        mir.AddFunction(baked);
                        state.Known.Add(baked.Symbol.Canonical);
                        rings.Add(baked.Symbol);
                    }
                    return state.GetChains[fieldSymbol] = new GetChain(terminal, rings);
                }
                finally
                {
                    state.InProgress.Remove(fieldSymbol + "|get");
                }
            }

            // 合成 set 终态 fn：有用户 setter 则 MirCall（VM
            // FinishSetToBacking 首分支），否则裸写 backing。setter 被
            // 烘焙成 trampoline 时直引其最深层原始体——Method wrapper
            // 命中指 $.mwrapped.（刀6；Entity 烘焙的 $.wrapped. 此时是
            // method trampoline），否则指 Entity 烘焙的 $.wrapped.——链末
            // 落点必须绕过 wrapper 派发（VM InvokeResolved 旁路全链同
            // 口径），否则链末 setter 调用再经 .proxy.* 拦截与 VM 分歧
            internal static MwMemberSymbol BuildSetTerminal(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol, string host, string name, string typeRef,
                string typeC)
            {
                var canonical = SetTerminalCanonical(host, name, typeC);
                var symbol = ProxyBakeSupport.SyntheticMember(canonical, owner: null);
                var setter = ImplBinder.FindAccessor(context.Symbols, fieldSymbol,
                    BilAccessorKind.Setter, canonical);
                var callArgs = new List<MirOperand>
                    { new MirLocalOperand(".this"), new MirLocalOperand("value") };
                MirInst write;
                if (setter == null)
                {
                    write = new MirSetField(new MirLocalOperand("value"),
                        new MirLocalOperand(".this"), fieldSymbol);
                }
                else if (ProxyBakeSupport.WillBakeMethodWrapper(context, mir, setter,
                        out var mwrappedCanonical))
                {
                    write = new MirCall(
                        ProxyBakeSupport.SyntheticMember(mwrappedCanonical, owner: null),
                        callArgs, null);
                }
                else if (ProxyBakeSupport.WillBakeHostMethod(context, mir, state.Index, setter,
                        out var wrappedCanonical))
                {
                    write = new MirCall(
                        ProxyBakeSupport.SyntheticMember(wrappedCanonical, owner: null),
                        callArgs, null);
                }
                else
                {
                    write = new MirCall(setter, callArgs, null);
                }
                var parameters = new List<MirLocal>
                {
                    new MirLocal(".this", MirType.Of(host)),
                    new MirLocal("value", MirType.Of(typeRef)),
                };
                var fn = new MirFunction(symbol, MirType.Of(".void"), parameters,
                    new List<MirLocal>(parameters),
                    new List<MirBlock>
                    {
                        new MirBlock("entry", new List<MirInst> { write }, new MirRet(null)),
                    }, false);
                mir.AddFunction(fn);
                state.Known.Add(canonical);
                return symbol;
            }

            // 合成 get 终态 fn：有用户 getter 则 MirCall 产值，否则裸读
            // backing（VM GetField 路径 2 同序：getter 先算，链在其外）
            internal static MwMemberSymbol BuildGetTerminal(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol, string host, string typeRef, string typeC)
            {
                var canonical = GetTerminalCanonical(host, FieldName(fieldSymbol), typeC);
                var symbol = ProxyBakeSupport.SyntheticMember(canonical, owner: null);
                var getter = ImplBinder.FindAccessor(context.Symbols, fieldSymbol,
                    BilAccessorKind.Getter, canonical);
                var valueType = MirType.Of(typeRef);
                var insts = new List<MirInst>();
                if (getter != null)
                {
                    insts.Add(new MirCall(getter,
                        new List<MirOperand> { new MirLocalOperand(".this") }, "r"));
                }
                else
                {
                    insts.Add(new MirGetField(new MirLocalOperand(".this"), fieldSymbol, "r"));
                }
                var parameters = new List<MirLocal> { new MirLocal(".this", MirType.Of(host)) };
                var locals = new List<MirLocal>(parameters)
                {
                    new MirLocal("r", valueType),
                };
                var fn = new MirFunction(symbol, valueType, parameters, locals,
                    new List<MirBlock>
                    {
                        new MirBlock("entry", insts, new MirRet(new MirLocalOperand("r"))),
                    }, false);
                mir.AddFunction(fn);
                state.Known.Add(canonical);
                return symbol;
            }

            // 单环特化：查无名 .proxy.get/.set 模板 → BuildSpecializedBody
            //（extraSubst 代入方法级 TValue → 字段类型，隐藏 typeid 形参
            // 剔除出形参表）→ 命名环符号
            internal static MirFunction BakeRing(MwContext context, BakeState state,
                string fieldSymbol, string wrapperRef, string host, string name, string typeRef,
                string typeC, bool isSet)
            {
                var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                    ?? throw new CompilerInternalException("字段 wrapper 类型缺失: " + wrapperRef);
                var proxy = ProxyMatcher.FindValueProxy(wrapper, isSet)
                    ?? throw new MwNotSupportedException(
                        $"wrapper {wrapperRef} 缺少 .proxy.{(isSet ? "set" : "get")}："
                        + $"{fieldSymbol}（字段-Value 链烘焙期受控拒绝；VM 为运行期异常）");
                if (!state.BilBySymbol.TryGetValue(proxy.Canonical, out var bilFn))
                {
                    throw new MwNotSupportedException("proxy 模板无 fn 体: " + proxy.Canonical);
                }
                var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, wrapperRef,
                    wrapper, host, out var returnType, MethodTypeSubst(bilFn, typeRef));
                var symbol = ProxyBakeSupport.SyntheticMember(
                    RingCanonical(wrapperRef, host, name, typeC, isSet), proxy.Owner);
                return new MirFunction(symbol, returnType, body.Parameters, body.Locals,
                    body.Blocks, false);
            }

            // 方法级隐藏 typeid 形参 → 字段类型代入表（VM BuildProxyArgs
            // 同口径：每个 .generic.* typeid 形参都填 elementType；泛型包
            // 形态不是合法 Value proxy 形状，受控拒绝）
            internal static Dictionary<string, string>? MethodTypeSubst(BilFunction template,
                string fieldTypeRef)
            {
                Dictionary<string, string>? map = null;
                foreach (var arg in template.Args)
                {
                    if (!arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!TypeLayout.IsTypeIdCanonical(MwTypeKey.Normalize(arg.TypeRef)))
                    {
                        throw new MwNotSupportedException(
                            "Value proxy 模板的泛型包形参不支持: " + template.Symbol);
                    }
                    (map ??= new Dictionary<string, string>(System.StringComparer.Ordinal))[
                        arg.Name.Substring(".generic.".Length)] = fieldTypeRef;
                }
                return map;
            }

            internal static string FieldName(string fieldSymbol)
            {
                var hash = fieldSymbol.IndexOf('#');
                var at = fieldSymbol.LastIndexOf('@');
                return fieldSymbol.Substring(hash + 1, at - hash - 1);
            }

            internal static MwMemberSymbol FindBaked(MirModule mir, string canonical)
            {
                foreach (var fn in mir.Functions)
                {
                    if (fn.Symbol.Canonical == canonical)
                    {
                        return fn.Symbol;
                    }
                }
                throw new CompilerInternalException("烘焙产物丢失: " + canonical);
            }
        }

        // ===== Entity 字段链烘焙（刀3b：宿主类型 wrapped；环/终态定名与
        // Value 面互斥——W 短路 E 保证同一字段不同时落两面） =====

        // 逐层判定命中项（wrapper 引用 + 类别 + proxy 模板符号）
        private sealed class LayerMatch
        {
            internal LayerMatch(int layer, string wrapper, MwProxyMatchKind kind, MwMemberSymbol proxy)
            {
                Layer = layer;
                Wrapper = wrapper;
                Kind = kind;
                Proxy = proxy;
            }

            internal int Layer { get; }
            internal string Wrapper { get; }
            internal MwProxyMatchKind Kind { get; }
            internal MwMemberSymbol Proxy { get; }
        }

        // 已烘焙 Entity 环（层下标 + 符号 + wrapper 引用 + 类别）
        private sealed class EntityRing
        {
            internal EntityRing(int layer, MwMemberSymbol symbol, string wrapper,
                MwProxyMatchKind kind)
            {
                Layer = layer;
                Symbol = symbol;
                Wrapper = wrapper;
                Kind = kind;
            }

            internal int Layer { get; }
            internal MwMemberSymbol Symbol { get; }
            internal string Wrapper { get; }
            internal MwProxyMatchKind Kind { get; }
        }

        // Entity get 链产物：终态符号 + 命中环（与 EntityWrappers 同序
        // outer→inner，只含命中层）；环无 inner，调用序由使用点自内向外排
        private sealed class EntityGetChain
        {
            internal EntityGetChain(MwMemberSymbol terminal, List<EntityRing> rings)
            {
                Terminal = terminal;
                Rings = rings;
            }

            internal MwMemberSymbol Terminal { get; }
            internal List<EntityRing> Rings { get; }
        }

        // Entity set 链：命中环表（与 EntityWrappers 同序 outer→inner，
        // 只含命中层；环间 inner 已链接，链末落终态）。使用点入口 =
        // Rings[0]；router 分支按 fromLayer 取首个 Layer >= fromLayer 的环
        private sealed class EntitySetEntry
        {
            internal EntitySetEntry(List<EntityRing> rings)
            {
                Rings = rings;
            }

            internal List<EntityRing> Rings { get; }
            internal EntityRing Entry => Rings[0];
        }

        private static class EntityChainBake
        {
            // 逐层判定（ProxyMatcher 按字段名 specific 优先、wildcard 兜底，
            // None 跳过——VM VmWrapperDispatch.FindProxy 的 Get/Set 类别同
            // 口径）；全 None → null（调用方走访问器兜底）
            private static List<LayerMatch>? MatchLayers(MwContext context, BakeState state,
                string host, string name, bool isSet)
            {
                List<LayerMatch>? matches = null;
                var wrappers = state.Index.EntityWrappers(host);
                for (var layer = 0; layer < wrappers.Count; layer++)
                {
                    var wrapperRef = wrappers[layer];
                    var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                        ?? throw new CompilerInternalException(
                            "Entity wrapper 类型缺失: " + wrapperRef);
                    var proxy = ProxyMatcher.FindEntityFieldProxy(wrapper, name, isSet,
                        out var isWildcard);
                    if (proxy == null)
                    {
                        continue;
                    }
                    (matches ??= new List<LayerMatch>()).Add(new LayerMatch(layer, wrapperRef,
                        isWildcard ? MwProxyMatchKind.Wildcard : MwProxyMatchKind.Specific,
                        proxy));
                }
                return matches;
            }

            // get 链：终态（裸读 backing / MirCall 用户 getter——VM 使用点
            // getter 调用同口径，getter 调用本身仍经方法面拦截不归本链）
            // → 命中环表；全 None 返回 null
            internal static EntityGetChain? EnsureGetChain(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol)
            {
                if (state.EntityGetChains.TryGetValue(fieldSymbol, out var cached))
                {
                    return cached;
                }
                var (host, name, typeRef) = ChainBake.ParseField(fieldSymbol);
                var typeC = MirType.Of(typeRef).Canonical;
                var matches = MatchLayers(context, state, host, name, isSet: false);
                if (matches == null)
                {
                    return state.EntityGetChains[fieldSymbol] = null;
                }
                var terminalCanonical = ChainBake.GetTerminalCanonical(host, name, typeC);
                if (state.Known.Contains(terminalCanonical))
                {
                    // 跨 BakeState 的钩子重跑路径：产物已在 mir，按定名回收
                    var recovered = new List<EntityRing>(matches.Count);
                    foreach (var match in matches)
                    {
                        recovered.Add(new EntityRing(match.Layer,
                            ChainBake.FindBaked(mir, ChainBake.RingCanonical(match.Wrapper,
                                host, name, typeC, isSet: false)),
                            match.Wrapper, match.Kind));
                    }
                    return state.EntityGetChains[fieldSymbol] = new EntityGetChain(
                        ChainBake.FindBaked(mir, terminalCanonical), recovered);
                }
                var terminal = ChainBake.BuildGetTerminal(context, mir, state, fieldSymbol,
                    host, typeRef, typeC);
                state.InProgress.Add(fieldSymbol + "|get");
                try
                {
                    var rings = new List<EntityRing>(matches.Count);
                    foreach (var match in matches)
                    {
                        var baked = BakeRing(context, state, fieldSymbol, match, host, name,
                            typeRef, typeC, isSet: false);
                        IndexOperatorLoweringPass.RewriteFunction(context, baked);
                        AccessorLoweringPass.RewriteFunction(context, baked);
                        UseSite.RewriteFunction(context, state, mir, baked, exemptField: null);
                        mir.AddFunction(baked);
                        state.Known.Add(baked.Symbol.Canonical);
                        rings.Add(new EntityRing(match.Layer, baked.Symbol, match.Wrapper,
                            match.Kind));
                    }
                    return state.EntityGetChains[fieldSymbol] =
                        new EntityGetChain(terminal, rings);
                }
                finally
                {
                    state.InProgress.Remove(fieldSymbol + "|get");
                }
            }

            // set 链：outer→inner 逐环特化 + LinkSetInner 链接（环间经
            // MirGetWrapper 取 Entity 隐藏槽的下一层 wrapper 实例），链末
            // inner 落终态（用户 setter 旁路体 / 裸写 backing）；全 None
            // 返回 null
            internal static EntitySetEntry? EnsureSetChain(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol)
            {
                if (state.EntitySetEntries.TryGetValue(fieldSymbol, out var cached))
                {
                    return cached;
                }
                var (host, name, typeRef) = ChainBake.ParseField(fieldSymbol);
                var typeC = MirType.Of(typeRef).Canonical;
                var matches = MatchLayers(context, state, host, name, isSet: true);
                if (matches == null)
                {
                    return state.EntitySetEntries[fieldSymbol] = null;
                }
                var entryCanonical = ChainBake.RingCanonical(matches[0].Wrapper, host, name,
                    typeC, isSet: true);
                if (state.Known.Contains(entryCanonical))
                {
                    var recovered = new List<EntityRing>(matches.Count);
                    foreach (var match in matches)
                    {
                        recovered.Add(new EntityRing(match.Layer,
                            ChainBake.FindBaked(mir, ChainBake.RingCanonical(match.Wrapper,
                                host, name, typeC, isSet: true)),
                            match.Wrapper, match.Kind));
                    }
                    return state.EntitySetEntries[fieldSymbol] = new EntitySetEntry(recovered);
                }
                state.InProgress.Add(fieldSymbol + "|set");
                try
                {
                    var terminal = EnsureSetTerminal(context, mir, state,
                        fieldSymbol, host, name, typeRef, typeC);
                    MwMemberSymbol nextCallee = terminal;
                    string? nextWrapper = null;
                    var nextWildcard = false;
                    var rings = new List<EntityRing>(matches.Count);
                    for (var k = matches.Count - 1; k >= 0; k--)
                    {
                        var match = matches[k];
                        var baked = BakeRing(context, state, fieldSymbol, match, host, name,
                            typeRef, typeC, isSet: true);
                        LinkSetInner(context, mir, state, baked,
                            match.Kind == MwProxyMatchKind.Wildcard, match.Layer, nextCallee,
                            nextWrapper, nextWildcard, fieldSymbol, host, name, typeRef, typeC,
                            rings, terminal);
                        IndexOperatorLoweringPass.RewriteFunction(context, baked);
                        AccessorLoweringPass.RewriteFunction(context, baked);
                        UseSite.RewriteFunction(context, state, mir, baked, exemptField: null);
                        mir.AddFunction(baked);
                        state.Known.Add(baked.Symbol.Canonical);
                        nextCallee = baked.Symbol;
                        nextWrapper = match.Wrapper;
                        nextWildcard = match.Kind == MwProxyMatchKind.Wildcard;
                        rings.Insert(0, new EntityRing(match.Layer, baked.Symbol, match.Wrapper,
                            match.Kind));
                    }
                    return state.EntitySetEntries[fieldSymbol] = new EntitySetEntry(rings);
                }
                finally
                {
                    state.InProgress.Remove(fieldSymbol + "|set");
                }
            }

            // set 终态（Known 去重回收后委托 ChainBake 合成：用户 setter
            // 旁路体引用 / 裸写 backing）
            internal static MwMemberSymbol EnsureSetTerminal(MwContext context, MirModule mir,
                BakeState state, string fieldSymbol, string host, string name, string typeRef,
                string typeC)
            {
                var canonical = ChainBake.SetTerminalCanonical(host, name, typeC);
                return state.Known.Contains(canonical)
                    ? ChainBake.FindBaked(mir, canonical)
                    : ChainBake.BuildSetTerminal(context, mir, state, fieldSymbol, host, name,
                        typeRef, typeC);
            }

            // 单环特化：BuildSpecializedBody（extraSubst 代入方法级
            // TValue → 字段类型，隐藏 typeid 形参剔除）；wildcard get 环的
            // symbol 首参绑字段 canonical 资源（VM BuildProxyArgs 的
            // symbol=frame.FieldSymbol 同口径），wildcard set 环保留
            // symbol 形参做两参转发
            internal static MirFunction BakeRing(MwContext context, BakeState state,
                string fieldSymbol, LayerMatch match, string host, string name, string typeRef,
                string typeC, bool isSet)
            {
                var wrapper = context.Symbols.FindTypeByRef(match.Wrapper)
                    ?? throw new CompilerInternalException(
                        "Entity wrapper 类型缺失: " + match.Wrapper);
                if (!state.BilBySymbol.TryGetValue(match.Proxy.Canonical, out var bilFn))
                {
                    throw new MwNotSupportedException("proxy 模板无 fn 体: "
                        + match.Proxy.Canonical);
                }
                var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, match.Wrapper,
                    wrapper, host, out var returnType,
                    ChainBake.MethodTypeSubst(bilFn, typeRef));
                if (match.Kind == MwProxyMatchKind.Wildcard && !isSet)
                {
                    body = BindGetSymbol(context, body, fieldSymbol);
                }
                var symbol = ProxyBakeSupport.SyntheticMember(
                    ChainBake.RingCanonical(match.Wrapper, host, name, typeC, isSet),
                    match.Proxy.Owner);
                return new MirFunction(symbol, returnType, body.Parameters, body.Locals,
                    body.Blocks, false);
            }

            // wildcard get 环 symbol 首参绑字段 canonical 字符串资源：
            // 形参剔除（形参表只读，重建 fn），块首（MirGetSelf/getid
            // 物化之后）插 load res 就地绑定（VM BuildProxyArgs 的
            // symbol 实参同口径）
            private static MirFunction BindGetSymbol(MwContext context, MirFunction body,
                string fieldSymbol)
            {
                var parameters = new List<MirLocal>(body.Parameters);
                parameters.RemoveAll(p => p.Name == "symbol");
                var insts = body.Blocks[0].InstructionList;
                var insertAt = 0;
                while (insertAt < insts.Count
                    && insts[insertAt] is MirGetSelf or MirGetTypeId)
                {
                    insertAt++;
                }
                insts.Insert(insertAt, new MirLoadResource(
                    ProxyWildcardAbi.AddStringResource(context, fieldSymbol), "symbol"));
                return new MirFunction(body.Symbol, body.ReturnType, parameters, body.Locals,
                    body.Blocks, false);
            }

            // set 环 inner 链接（VM BuildRingInvokeArgs / ValueOfSetInner
            // 同口径）：落点为终态或 specific 环时只转发末位值实参；落点
            // 为 wildcard 环时转发 (symbol, value) 两参——当前环 specific
            // 时 symbol 补字段 canonical 资源，当前环 wildcard 时透传
            // symbol 形参。receiver：有下一层 wrapper 经 MirGetWrapper
            // 取 Entity 隐藏槽，否则直用 InnerHostLocal。
            // wildcard 环 inner 的跨字段重路由（MW10 遗留④，VM
            // RerouteWildcardInner Get/Set 分支同口径）：改写为运行期
            // 分派——symbol == 原字段 canonical 走静态已知路径（恒等链
            // 路形状不变），否则调分派辅助 fn 自下一层起逐名搜环
            //（remaining 层无任何 set proxy 时分派恒落原字段终态，与
            // 恒等路径等价，保持线性链接不引 CFG 分割）
            private static void LinkSetInner(MwContext context, MirModule mir, BakeState state,
                MirFunction baked, bool currentWildcard, int currentLayer,
                MwMemberSymbol nextCallee, string? nextWrapper, bool nextWildcard,
                string fieldSymbol, string host, string name, string typeRef, string typeC,
                IReadOnlyList<EntityRing> ringsSuffix, MwMemberSymbol terminal)
            {
                var hostOp = new MirLocalOperand(ProxyBakeSupport.InnerHostLocal);
                // wildcard 环才可能有 symbol 改写；剩余层有无可路由 set
                // proxy 决定是否需要运行期分派（无则任意 symbol 恒落原
                // 字段终态——VM FieldSymbol 不随改写变，同形）
                var reroute = currentWildcard
                    && EnumerateRerouteBranches(context, state, host, currentLayer + 1,
                        typeC).Count > 0;
                var temp = 0;
                var blockIndex = 0;
                while (blockIndex < baked.Blocks.Count)
                {
                    var block = baked.Blocks[blockIndex];
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        if (insts[i] is not MirInnerCall inner)
                        {
                            continue;
                        }
                        List<MirOperand> forward;
                        if (currentWildcard)
                        {
                            if (inner.Args.Count != 2
                                || inner.Args[0] is not MirLocalOperand symbolArg
                                || symbolArg.Name != "symbol")
                            {
                                throw new MwNotSupportedException(
                                    "wildcard set 环 inner 的 symbol 首参形状非法"
                                    + "（受控拒绝；BIL 验证器要求 $symbol 局部恒等形态）: "
                                    + baked.Symbol.Canonical);
                            }
                            forward = nextWildcard
                                ? new List<MirOperand> { inner.Args[0], inner.Args[1] }
                                : new List<MirOperand> { inner.Args[1] };
                            if (reroute)
                            {
                                // fast-path（恒等）+ 分派（改写）双分支；
                                // 续扫 cont 块（原 i+1 起指令）
                                block = SplitWildcardSetInner(context, mir, state, baked, block,
                                    i, inner, currentLayer, nextCallee, nextWrapper,
                                    nextWildcard, fieldSymbol, host, name, typeRef, typeC,
                                    ringsSuffix, terminal, hostOp, ref temp);
                                insts = block.InstructionList;
                                i = -1;
                                continue;
                            }
                        }
                        else
                        {
                            if (inner.Args.Count != 1)
                            {
                                throw new CompilerInternalException(
                                    "specific set 环 inner 实参形状非法: "
                                    + baked.Symbol.Canonical);
                            }
                            forward = new List<MirOperand> { inner.Args[0] };
                        }
                        var replacement = new List<MirInst>();
                        if (!currentWildcard && nextWildcard)
                        {
                            var symName = "$mw.esym." + temp++;
                            baked.AddLocal(new MirLocal(symName,
                                ProxyWildcardAbi.StringType));
                            replacement.Add(new MirLoadResource(
                                ProxyWildcardAbi.AddStringResource(context, fieldSymbol),
                                symName));
                            forward.Insert(0, new MirLocalOperand(symName));
                        }
                        EmitStaticSetInnerLink(baked, replacement, forward, nextCallee,
                            nextWrapper, inner.Result, inner.ExcTarget, hostOp, ref temp);
                        insts.RemoveAt(i);
                        insts.InsertRange(i, replacement);
                        i += replacement.Count - 1;
                    }
                    blockIndex++;
                }
            }

            // inner 静态链接落点发射（receiver 取址 + MirCall），
            // fast-path 块与线性路径共用
            private static void EmitStaticSetInnerLink(MirFunction baked, List<MirInst> insts,
                List<MirOperand> forward, MwMemberSymbol nextCallee, string? nextWrapper,
                string? result, MirBlock? excTarget, MirOperand hostOp, ref int temp)
            {
                MirOperand receiver = hostOp;
                if (nextWrapper != null)
                {
                    var wName = "$mw.inner.w." + temp++;
                    baked.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper)));
                    insts.Add(new MirGetWrapperAddr(hostOp, nextWrapper, wName));
                    receiver = new MirLocalOperand(wName);
                }
                var callArgs = new List<MirOperand>(forward.Count + 1) { receiver };
                callArgs.AddRange(forward);
                insts.Add(new MirCall(nextCallee, callArgs, result, excTarget));
            }

            // wildcard set 环 inner 的 CFG 分割：原块截断为 check（symbol
            // == 原字段 canonical 比较）→ fast（既有静态链接形状）/ slow
            //（box value 调分派辅助）→ cont（原剩余指令 + 原终结符）。
            // 返回 cont 块（调用方续扫其中的 MirInnerCall）
            private static MirBlock SplitWildcardSetInner(MwContext context, MirModule mir,
                BakeState state, MirFunction baked, MirBlock block, int i, MirInnerCall inner,
                int currentLayer, MwMemberSymbol nextCallee, string? nextWrapper,
                bool nextWildcard, string fieldSymbol, string host, string name, string typeRef,
                string typeC, IReadOnlyList<EntityRing> ringsSuffix, MwMemberSymbol terminal,
                MirOperand hostOp, ref int temp)
            {
                var insts = block.InstructionList;
                var dispatch = EnsureSetRerouteDispatch(context, mir, state, host,
                    currentLayer + 1, fieldSymbol, name, typeRef, typeC, ringsSuffix, terminal);
                // fast 块：与无分派路径完全同形的静态链接
                var forward = nextWildcard
                    ? new List<MirOperand> { inner.Args[0], inner.Args[1] }
                    : new List<MirOperand> { inner.Args[1] };
                var fastInsts = new List<MirInst>();
                EmitStaticSetInnerLink(baked, fastInsts, forward, nextCallee, nextWrapper,
                    inner.Result, inner.ExcTarget, hostOp, ref temp);
                // slow 块：value 装箱 .any 后调分派辅助（receiver = 宿主
                // .this 地址别名，与链上一致）
                var valueOp = inner.Args[1];
                var slowInsts = new List<MirInst>();
                var boxName = "$mw.sr.v." + temp++;
                baked.AddLocal(new MirLocal(boxName, ProxyWildcardAbi.AnyType));
                ProxyWildcardAbi.EmitBoxToAny(context, slowInsts, valueOp,
                    TypeOfOperand(baked, valueOp, MirType.Of(typeRef)), boxName);
                slowInsts.Add(new MirCall(dispatch,
                    new List<MirOperand>
                        { hostOp, inner.Args[0], new MirLocalOperand(boxName) },
                    inner.Result, inner.ExcTarget));
                // check：截断原块，追加 canonical 比对
                var litName = "$mw.sr.lit." + temp++;
                baked.AddLocal(new MirLocal(litName, ProxyWildcardAbi.StringType));
                var cmpName = "$mw.sr.eq." + temp++;
                baked.AddLocal(new MirLocal(cmpName, ProxyWildcardAbi.BoolType));
                var contInsts = insts.GetRange(i + 1, insts.Count - i - 1);
                var contTerminator = block.Terminator;
                insts.RemoveRange(i, insts.Count - i);
                insts.Add(new MirLoadResource(
                    ProxyWildcardAbi.AddStringResource(context, fieldSymbol), litName));
                insts.Add(new MirBinaryIntrinsic(BilBinaryOp.CmpEq, inner.Args[0],
                    new MirLocalOperand(litName), ProxyWildcardAbi.StringType,
                    ProxyWildcardAbi.StringType, ProxyWildcardAbi.BoolType, cmpName));
                var fastId = FreshBlockId(baked, "$mw.sr.fast.");
                var slowId = FreshBlockId(baked, "$mw.sr.slow.");
                var contId = FreshBlockId(baked, "$mw.sr.cont.");
                block.Terminator = new MirCondBranch(new MirLocalOperand(cmpName), fastId,
                    slowId);
                baked.AddBlock(new MirBlock(fastId, fastInsts, new MirBranch(contId)));
                baked.AddBlock(new MirBlock(slowId, slowInsts, new MirBranch(contId)));
                var cont = new MirBlock(contId, contInsts, contTerminator);
                baked.AddBlock(cont);
                return cont;
            }

            // 操作数静态类型（局部按声明类型；否则回落给定类型）
            private static MirType TypeOfOperand(MirFunction fn, MirOperand op, MirType fallback) =>
                op is MirLocalOperand lop && fn.TryFindLocal(lop.Name, out var local)
                    ? local.Type
                    : fallback;

            // 块 id 唯一化（pass 合成块，前缀 + 序号）
            private static string FreshBlockId(MirFunction fn, string prefix)
            {
                var n = 0;
                var taken = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var existing in fn.Blocks)
                {
                    taken.Add(existing.Id);
                }
                while (taken.Contains(prefix + n))
                {
                    n++;
                }
                return prefix + n;
            }

            // ===== MW10 遗留④：跨字段重路由的运行期分派 =====

            // 分派分支：specific = 命中层某同类型字段的 .proxy.set.<名>
            //（按字段 canonical 字符串比对）；wildcard = 命中层的
            // .proxy.set.*（任意名兜底，无条件分支，其后层不可达）
            private sealed class RerouteBranch
            {
                internal RerouteBranch(int layer, string wrapper, string? targetField)
                {
                    Layer = layer;
                    Wrapper = wrapper;
                    TargetField = targetField;
                }

                internal int Layer { get; }
                internal string Wrapper { get; }
                internal string? TargetField { get; }
            }

            // 自 fromLayer 起逐层枚举分派分支（VM NextRingProxy 的
            // specific 优先、wildcard 兜底、逐层推进同口径；wildcard 命中
            // 后更深层不可达，枚举即止）。仅原字段同类型字段入分支——
            // 跨类型改写 VM 为动态值直通，native 静态链无法保型，落
            // miss（分歧记入 MW10 报告）；静态字段不列（静态面受控拒绝
            // 先例）
            private static List<RerouteBranch> EnumerateRerouteBranches(MwContext context,
                BakeState state, string host, int fromLayer, string typeC)
            {
                var branches = new List<RerouteBranch>();
                var wrappers = state.Index.EntityWrappers(host);
                if (fromLayer >= wrappers.Count)
                {
                    return branches;
                }
                var hostType = context.Symbols.FindTypeByRef(host)
                    ?? throw new CompilerInternalException("Entity 宿主类型缺失: " + host);
                for (var layer = fromLayer; layer < wrappers.Count; layer++)
                {
                    var wrapper = context.Symbols.FindTypeByRef(wrappers[layer])
                        ?? throw new CompilerInternalException(
                            "Entity wrapper 类型缺失: " + wrappers[layer]);
                    foreach (var member in hostType.Members)
                    {
                        if (member.Declaration.Kind != BilMemberKind.Field
                            || member.Canonical.Contains("#.static.",
                                System.StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var (_, fieldName, fieldTypeRef) = ChainBake.ParseField(member.Canonical);
                        if (MirType.Of(fieldTypeRef).Canonical != typeC)
                        {
                            continue;
                        }
                        if (ProxyMatcher.FindProxy(wrapper, ".proxy.set." + fieldName,
                                BilProxyKind.Specific) != null)
                        {
                            branches.Add(new RerouteBranch(layer, wrappers[layer],
                                member.Canonical));
                        }
                    }
                    if (ProxyMatcher.FindProxy(wrapper, ".proxy.set.*", BilProxyKind.Wildcard)
                        != null)
                    {
                        branches.Add(new RerouteBranch(layer, wrappers[layer], null));
                        break;
                    }
                }
                return branches;
            }

            // 分派辅助 fn（每 (宿主, fromLayer, 原字段) 一个）：
            // H$.mw.srt.<fromLayer>.<名>(symbol,value:.any)@.void——
            // if 链按 symbol canonical 比对进 specific 分支（他字段环
            // 变体 / 原字段自身环）；wildcard 分支进原字段上下文的
            // wildcard 环（symbol 实参 = 原字段 canonical——VM
            // BuildRingInvokeArgs 传 frame.FieldSymbol 同口径）；miss
            // 落原字段终态（VM FinishSetToBacking 的 FieldSymbol 恒为
            // 原字段同口径）
            private static MwMemberSymbol EnsureSetRerouteDispatch(MwContext context,
                MirModule mir, BakeState state, string host, int fromLayer, string fieldSymbol,
                string name, string typeRef, string typeC,
                IReadOnlyList<EntityRing> ringsSuffix, MwMemberSymbol terminal)
            {
                var canonical = host + "$.mw.srt." + fromLayer + "." + name
                    + "(symbol:.string,value:.any)@.void";
                if (state.Known.Contains(canonical))
                {
                    return ChainBake.FindBaked(mir, canonical);
                }
                var branches = EnumerateRerouteBranches(context, state, host, fromLayer, typeC);
                var symbol = ProxyBakeSupport.SyntheticMember(canonical, owner: null);
                var parameters = new List<MirLocal>
                {
                    new MirLocal(".this", MirType.Of(host)),
                    new MirLocal("symbol", ProxyWildcardAbi.StringType),
                    new MirLocal("value", ProxyWildcardAbi.AnyType),
                };
                var fn = new MirFunction(symbol, MirType.Of(".void"), parameters,
                    new List<MirLocal>(parameters), new List<MirBlock>(), false);
                var thisOp = new MirLocalOperand(".this");
                var symbolOp = new MirLocalOperand("symbol");
                var valueOp = new MirLocalOperand("value");
                var valueType = MirType.Of(typeRef);
                var blocks = new List<MirBlock>();
                for (var k = 0; k < branches.Count; k++)
                {
                    var branch = branches[k];
                    var checkId = k == 0 ? "entry" : "mw.srt.chk." + k;
                    var caseId = "mw.srt.case." + k;
                    var nextId = "mw.srt.chk." + (k + 1);
                    var caseInsts = new List<MirInst>();
                    var w = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.w.",
                        MirType.Of(branch.Wrapper));
                    caseInsts.Add(new MirGetWrapperAddr(thisOp, branch.Wrapper, w));
                    List<MirOperand> callArgs;
                    MwMemberSymbol ring;
                    if (branch.TargetField != null)
                    {
                        // specific 分支：进该字段自 layer 起的环（原字段
                        // 自身 → 链上既有环；他字段 → 变体环，终态仍指
                        // 原字段）
                        var (fieldHost, fieldName, fieldTypeRef) =
                            ChainBake.ParseField(branch.TargetField);
                        ring = branch.TargetField == fieldSymbol
                            ? FindSuffixRing(ringsSuffix, branch.Layer)
                            : EnsureSpecificRingVariant(context, mir, state, fieldHost,
                                branch.Layer, branch.Wrapper, branch.TargetField, fieldName,
                                fieldTypeRef, typeC, fieldSymbol, name, typeRef, ringsSuffix,
                                terminal);
                        var v = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.v.",
                            MirType.Of(fieldTypeRef));
                        ProxyWildcardAbi.EmitUnboxFromAny(context, caseInsts, valueOp, v,
                            MirType.Of(fieldTypeRef));
                        callArgs = new List<MirOperand>
                            { new MirLocalOperand(w), new MirLocalOperand(v) };
                    }
                    else
                    {
                        // wildcard 分支：原字段上下文的 wildcard 环，
                        // symbol 实参 = 原字段 canonical 资源
                        ring = EnsureWildcardRingVariant(context, mir, state, host,
                            branch.Layer, branch.Wrapper, fieldSymbol, name, typeRef, typeC,
                            ringsSuffix, terminal);
                        var sym = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.s.",
                            ProxyWildcardAbi.StringType);
                        caseInsts.Add(new MirLoadResource(
                            ProxyWildcardAbi.AddStringResource(context, fieldSymbol), sym));
                        var v = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.v.", valueType);
                        ProxyWildcardAbi.EmitUnboxFromAny(context, caseInsts, valueOp, v,
                            valueType);
                        callArgs = new List<MirOperand>
                            { new MirLocalOperand(w), new MirLocalOperand(sym),
                                new MirLocalOperand(v) };
                    }
                    caseInsts.Add(new MirCall(ring, callArgs, null));
                    if (branch.TargetField != null)
                    {
                        var lit = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.lit.",
                            ProxyWildcardAbi.StringType);
                        var cmp = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.eq.",
                            ProxyWildcardAbi.BoolType);
                        blocks.Add(new MirBlock(checkId, new List<MirInst>
                        {
                            new MirLoadResource(
                                ProxyWildcardAbi.AddStringResource(context, branch.TargetField),
                                lit),
                            new MirBinaryIntrinsic(BilBinaryOp.CmpEq, symbolOp,
                                new MirLocalOperand(lit), ProxyWildcardAbi.StringType,
                                ProxyWildcardAbi.StringType, ProxyWildcardAbi.BoolType, cmp),
                        }, new MirCondBranch(new MirLocalOperand(cmp), caseId,
                            k + 1 < branches.Count ? nextId : "mw.srt.miss")));
                    }
                    else
                    {
                        // wildcard 兜底：无条件进环（其后层不可达）
                        blocks.Add(new MirBlock(checkId, new List<MirInst>(),
                            new MirBranch(caseId)));
                    }
                    blocks.Add(new MirBlock(caseId, caseInsts, new MirRet(null)));
                }
                // miss：原字段终态（value 拆箱回原字段类型）
                var missInsts = new List<MirInst>();
                var missValue = ProxyWildcardAbi.FreshLocal(fn, "$mw.srt.v.", valueType);
                ProxyWildcardAbi.EmitUnboxFromAny(context, missInsts, valueOp, missValue,
                    valueType);
                missInsts.Add(new MirCall(terminal,
                    new List<MirOperand> { thisOp, new MirLocalOperand(missValue) }, null));
                if (branches.Count == 0)
                {
                    blocks.Add(new MirBlock("entry", missInsts, new MirRet(null)));
                }
                else
                {
                    blocks.Add(new MirBlock("mw.srt.miss", missInsts, new MirRet(null)));
                }
                foreach (var block in blocks)
                {
                    fn.AddBlock(block);
                }
                mir.AddFunction(fn);
                state.Known.Add(canonical);
                return symbol;
            }

            // 原字段链上指定层的既有环（specific 分支 F == 原字段时用）
            private static MwMemberSymbol FindSuffixRing(IReadOnlyList<EntityRing> ringsSuffix,
                int layer)
            {
                foreach (var ring in ringsSuffix)
                {
                    if (ring.Layer == layer)
                    {
                        return ring.Symbol;
                    }
                }
                throw new CompilerInternalException("原字段链缺失层 " + layer + " 的环");
            }

            // 他字段 specific 环变体（每 (目标字段, 层, 原字段) 一个）：
            // 体 = 目标字段在 layer 层的 specific proxy 特化；inner 续跑
            // 自 layer+1 起按目标字段名 specific 优先、wildcard 兜底搜环
            //（VM AdvanceRing 的 MemberName=新名同口径），终态 = 原字段
            // 终态（VM FieldSymbol 恒为原字段同口径）
            private static MwMemberSymbol EnsureSpecificRingVariant(MwContext context,
                MirModule mir, BakeState state, string host, int layer, string wrapper,
                string fieldSymbolF, string nameF, string typeRefF, string typeC,
                string origFieldSymbol, string origName, string origTypeRef,
                IReadOnlyList<EntityRing> ringsSuffix, MwMemberSymbol terminal)
            {
                var canonical = wrapper + ProxyBakeSupport.BakeInfix + host + "$" + nameF
                    + "$.setr." + origName + "." + layer + "(value:" + typeC + ")@.void";
                if (state.Known.Contains(canonical))
                {
                    return ChainBake.FindBaked(mir, canonical);
                }
                var wrapperType = context.Symbols.FindTypeByRef(wrapper)
                    ?? throw new CompilerInternalException("Entity wrapper 类型缺失: " + wrapper);
                var proxy = ProxyMatcher.FindProxy(wrapperType, ".proxy.set." + nameF,
                        BilProxyKind.Specific)
                    ?? throw new CompilerInternalException(
                        "分派分支的 specific proxy 缺失: " + fieldSymbolF);
                var match = new LayerMatch(layer, wrapper, MwProxyMatchKind.Specific, proxy);
                var body = BakeRing(context, state, fieldSymbolF, match, host, nameF, typeRefF,
                    typeC, isSet: true);
                var baked = new MirFunction(
                    ProxyBakeSupport.SyntheticMember(canonical, proxy.Owner), body.ReturnType,
                    body.Parameters, body.Locals, body.Blocks, false);
                // 续跑落点：目标字段名的下一 specific 变体 / 原字段上下文
                // 的 wildcard 环 / 原字段终态
                MwMemberSymbol next = terminal;
                string? nextWrapper = null;
                var nextWildcard = false;
                var wrappers = state.Index.EntityWrappers(host);
                for (var l2 = layer + 1; l2 < wrappers.Count; l2++)
                {
                    var w2 = context.Symbols.FindTypeByRef(wrappers[l2])
                        ?? throw new CompilerInternalException(
                            "Entity wrapper 类型缺失: " + wrappers[l2]);
                    if (ProxyMatcher.FindProxy(w2, ".proxy.set." + nameF, BilProxyKind.Specific)
                        != null)
                    {
                        next = EnsureSpecificRingVariant(context, mir, state, host, l2,
                            wrappers[l2], fieldSymbolF, nameF, typeRefF, typeC, origFieldSymbol,
                            origName, origTypeRef, ringsSuffix, terminal);
                        nextWrapper = wrappers[l2];
                        break;
                    }
                    if (ProxyMatcher.FindProxy(w2, ".proxy.set.*", BilProxyKind.Wildcard) != null)
                    {
                        next = EnsureWildcardRingVariant(context, mir, state, host, l2,
                            wrappers[l2], origFieldSymbol, origName, origTypeRef, typeC,
                            ringsSuffix, terminal);
                        nextWrapper = wrappers[l2];
                        nextWildcard = true;
                        break;
                    }
                }
                // specific 环 inner：转发末位值实参（落 wildcard 时补
                // symbol = 原字段 canonical——VM 传 frame.FieldSymbol）
                LinkSetInner(context, mir, state, baked, false, layer, next, nextWrapper,
                    nextWildcard, origFieldSymbol, host, nameF, typeRefF, typeC, ringsSuffix,
                    terminal);
                IndexOperatorLoweringPass.RewriteFunction(context, baked);
                AccessorLoweringPass.RewriteFunction(context, baked);
                UseSite.RewriteFunction(context, state, mir, baked, exemptField: null);
                mir.AddFunction(baked);
                state.Known.Add(canonical);
                return baked.Symbol;
            }

            // 原字段上下文的 wildcard 环（每 (原字段, 层) 一个）：原字段
            // 链在该层命中 wildcard 则复用链上环；否则独立烘焙（体 =
            // 该层 .proxy.set.* 模板按原字段特化，inner 走 LinkSetInner
            // 的 fast-path + 分派同口径，fast-path 落原字段自下一层起
            // 的链/终态）
            private static MwMemberSymbol EnsureWildcardRingVariant(MwContext context,
                MirModule mir, BakeState state, string host, int layer, string wrapper,
                string fieldSymbol, string name, string typeRef, string typeC,
                IReadOnlyList<EntityRing> ringsSuffix, MwMemberSymbol terminal)
            {
                foreach (var ring in ringsSuffix)
                {
                    if (ring.Layer == layer && ring.Kind == MwProxyMatchKind.Wildcard)
                    {
                        return ring.Symbol;
                    }
                }
                var canonical = wrapper + ProxyBakeSupport.BakeInfix + host + "$" + name
                    + "$.setw." + layer + "(symbol:.string,value:" + typeC + ")@.void";
                if (state.Known.Contains(canonical))
                {
                    return ChainBake.FindBaked(mir, canonical);
                }
                var wrapperType = context.Symbols.FindTypeByRef(wrapper)
                    ?? throw new CompilerInternalException("Entity wrapper 类型缺失: " + wrapper);
                var proxy = ProxyMatcher.FindProxy(wrapperType, ".proxy.set.*",
                        BilProxyKind.Wildcard)
                    ?? throw new CompilerInternalException(
                        "分派分支的 wildcard proxy 缺失: " + fieldSymbol);
                var match = new LayerMatch(layer, wrapper, MwProxyMatchKind.Wildcard, proxy);
                var body = BakeRing(context, state, fieldSymbol, match, host, name, typeRef,
                    typeC, isSet: true);
                var baked = new MirFunction(
                    ProxyBakeSupport.SyntheticMember(canonical, proxy.Owner), body.ReturnType,
                    body.Parameters, body.Locals, body.Blocks, false);
                // fast-path 落点：原字段自 layer+1 起首个命中环 / 终态
                MwMemberSymbol next = terminal;
                string? nextWrapper = null;
                var nextWildcard = false;
                foreach (var ring in ringsSuffix)
                {
                    if (ring.Layer > layer)
                    {
                        next = ring.Symbol;
                        nextWrapper = ring.Wrapper;
                        nextWildcard = ring.Kind == MwProxyMatchKind.Wildcard;
                        break;
                    }
                }
                LinkSetInner(context, mir, state, baked, true, layer, next, nextWrapper,
                    nextWildcard, fieldSymbol, host, name, typeRef, typeC, ringsSuffix,
                    terminal);
                IndexOperatorLoweringPass.RewriteFunction(context, baked);
                AccessorLoweringPass.RewriteFunction(context, baked);
                UseSite.RewriteFunction(context, state, mir, baked, exemptField: null);
                mir.AddFunction(baked);
                state.Known.Add(canonical);
                return baked.Symbol;
            }
        }

        // ===== 使用点改写 =====
        private static class UseSite
        {
            internal static void RewriteFunction(MwContext context, BakeState state, MirModule mir,
                MirFunction fn, string? exemptField)
            {
                // cell getValue/setValue：链包在访问器体外侧（壳化），
                // 不走逐指令改写
                if (exemptField == null && CellShell.TryRewrite(context, state, mir, fn))
                {
                    return;
                }
                foreach (var block in fn.Blocks)
                {
                    var insts = block.InstructionList;
                    for (var i = 0; i < insts.Count; i++)
                    {
                        switch (insts[i])
                        {
                            case MirGetField get:
                                switch (Classify(context, state, fn, get.FieldSymbol,
                                    exemptField, "get"))
                                {
                                    case ChainSource.Value:
                                        i += RewriteGet(context, state, mir, fn, insts, i, get);
                                        break;
                                    case ChainSource.Entity:
                                        i += RewriteEntityGet(context, state, mir, fn, insts, i,
                                            get);
                                        break;
                                }
                                break;
                            case MirSetField set:
                                switch (Classify(context, state, fn, set.FieldSymbol,
                                    exemptField, "set"))
                                {
                                    case ChainSource.Value:
                                        i += RewriteSet(context, state, mir, fn, insts, i, set);
                                        break;
                                    case ChainSource.Entity:
                                        i += RewriteEntitySet(context, state, mir, fn, insts, i,
                                            set);
                                        break;
                                }
                                break;
                        }
                    }
                }
            }

            // 链源
            private enum ChainSource
            {
                None,
                Value,
                Entity,
            }

            // 拦截条件与链源判定：字段自身 wrapped → Value 链；宿主类型
            // wrapped 且字段自身未 wrapped → Entity 链（W 短路 E——VM 现状
            // 短路语义，对齐保持）。共通排除：豁免字段 + 在烘 + 当前 fn 是
            // 该字段访问器（访问器体内是 backing 直访——AccessorLowering
            // 已把 #..value@ 归一到真实字段，此处按修饰符重新识别；VM
            // IsGetterOf / TryResolveBackingValue 同语义）
            private static ChainSource Classify(MwContext context, BakeState state, MirFunction fn,
                string fieldSymbol, string? exemptField, string category)
            {
                if (fieldSymbol == exemptField
                    || state.InProgress.Contains(fieldSymbol + "|" + category))
                {
                    return ChainSource.None;
                }
                foreach (var modifier in fn.Symbol.Declaration.Modifiers)
                {
                    if (modifier is BilAccessorModifier accessor
                        && accessor.FieldSymbol == fieldSymbol)
                    {
                        return ChainSource.None;
                    }
                }
                if (state.Index.FieldWrappers(fieldSymbol).Count > 0)
                {
                    return ChainSource.Value;
                }
                // 宿主 wrapped 判定取字段声明宿主（与 ProxyBakingPass 按
                // 声明宿主烘焙同口径；VM 按实例实际类型收集，子类重申
                // 场景声明宿主与实例闭包一致）
                if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol, out var owner,
                        out _, out _)
                    && state.Index.EntityWrappers(owner).Count > 0)
                {
                    return ChainSource.Entity;
                }
                return ChainSource.None;
            }

            // get.field → 终态调用 + 环自内向外逐环变值（VM ApplyGetChain
            // 同序：内层先）；各环 receiver 经 MirGetWrapperField 取
            private static int RewriteGet(MwContext context, BakeState state, MirModule mir,
                MirFunction fn, List<MirInst> insts, int i, MirGetField inst)
            {
                var chain = ChainBake.EnsureGetChain(context, mir, state, inst.FieldSymbol);
                var wrappers = state.Index.FieldWrappers(inst.FieldSymbol);
                var valueType = MirType.Of(ChainBake.ParseField(inst.FieldSymbol).TypeRef);
                var added = new List<MirInst>();
                var prev = FreshLocal(fn, "$mw.vpr.", valueType);
                insts[i] = new MirCall(chain.Terminal,
                    new List<MirOperand> { inst.Object }, prev, inst.ExcTarget);
                for (var r = wrappers.Count - 1; r >= 0; r--)
                {
                    var w = FreshLocal(fn, "$mw.vpw.", MirType.Of(wrappers[r]));
                    added.Add(new MirGetWrapperFieldAddr(inst.Object, inst.FieldSymbol,
                        wrappers[r], w));
                    var result = r == 0 ? inst.Target : FreshLocal(fn, "$mw.vpr.", valueType);
                    added.Add(new MirCall(chain.Rings[r],
                        new List<MirOperand>
                            { new MirLocalOperand(w), new MirLocalOperand(prev) },
                        result, inst.ExcTarget));
                    prev = result;
                }
                insts.InsertRange(i + 1, added);
                return added.Count;
            }

            // set.field → MirGetWrapperField 取最外环 wrapper 实例 +
            // MirCall 最外环 baked set（环间 inner 已链接，链末落终态）。
            // init 族 fn 内的写豁免 wrapper 链（VM IsInitFunctionOf 同口径）：
            // 有用户 setter 降为 setter 调用（AccessorLowering 降级同形），
            // 否则保持裸写
            private static int RewriteSet(MwContext context, BakeState state, MirModule mir,
                MirFunction fn, List<MirInst> insts, int i, MirSetField inst)
            {
                if (InitExemption.Applies(fn.Symbol, inst.FieldSymbol))
                {
                    if (ImplBinder.FindAccessor(context.Symbols, inst.FieldSymbol,
                            BilAccessorKind.Setter, fn.Symbol.Canonical) is { } setter)
                    {
                        insts[i] = new MirCall(setter,
                            new List<MirOperand> { inst.Object, inst.Source }, null);
                    }
                    return 0;
                }
                var entry = ChainBake.EnsureSetChain(context, mir, state, inst.FieldSymbol);
                var wrappers = state.Index.FieldWrappers(inst.FieldSymbol);
                var w = FreshLocal(fn, "$mw.vpw.", MirType.Of(wrappers[0]));
                insts[i] = new MirGetWrapperFieldAddr(inst.Object, inst.FieldSymbol, wrappers[0],
                    w);
                insts.Insert(i + 1, new MirCall(entry,
                    new List<MirOperand> { new MirLocalOperand(w), inst.Source }, null));
                return 1;
            }

            private static string FreshLocal(MirFunction fn, string prefix, MirType type)
            {
                var n = 0;
                while (fn.TryFindLocal(prefix + n, out _))
                {
                    n++;
                }
                var name = prefix + n;
                fn.AddLocal(new MirLocal(name, type));
                return name;
            }

            // ===== Entity 面（宿主类型 wrapped；刀3b） =====

            // Entity get：逐层 specific/wildcard 命中即成链；无层命中按
            // 访问器兜底降级（有用户 getter 则 MirCall，否则保持裸读——
            // AccessorLowering 对宿主 wrapped 放行后此兜底归本 pass）
            private static int RewriteEntityGet(MwContext context, BakeState state, MirModule mir,
                MirFunction fn, List<MirInst> insts, int i, MirGetField inst)
            {
                if (EntityChainBake.EnsureGetChain(context, mir, state, inst.FieldSymbol)
                        is { } chain)
                {
                    return RewriteEntityGetChain(fn, insts, i, inst, chain);
                }
                if (ImplBinder.FindAccessor(context.Symbols, inst.FieldSymbol,
                        BilAccessorKind.Getter, fn.Symbol.Canonical) is { } getter)
                {
                    insts[i] = new MirCall(getter, new List<MirOperand> { inst.Object },
                        inst.Target, inst.ExcTarget);
                }
                return 0;
            }

            // Entity get 使用点：终态调用 + 环自内向外逐环变值（VM
            // ApplyEntityGetChain 同序：内层先）；各环 receiver 经
            // MirGetWrapper 取 Entity 隐藏槽（非字段槽）
            private static int RewriteEntityGetChain(MirFunction fn, List<MirInst> insts, int i,
                MirGetField inst, EntityGetChain chain)
            {
                var valueType = MirType.Of(ChainBake.ParseField(inst.FieldSymbol).TypeRef);
                var added = new List<MirInst>();
                var prev = FreshLocal(fn, "$mw.epr.", valueType);
                insts[i] = new MirCall(chain.Terminal, new List<MirOperand> { inst.Object },
                    prev, inst.ExcTarget);
                for (var r = chain.Rings.Count - 1; r >= 0; r--)
                {
                    var ring = chain.Rings[r];
                    var w = FreshLocal(fn, "$mw.epw.", MirType.Of(ring.Wrapper));
                    added.Add(new MirGetWrapperAddr(inst.Object, ring.Wrapper, w));
                    var result = r == 0 ? inst.Target : FreshLocal(fn, "$mw.epr.", valueType);
                    added.Add(new MirCall(ring.Symbol,
                        new List<MirOperand>
                            { new MirLocalOperand(w), new MirLocalOperand(prev) },
                        result, inst.ExcTarget));
                    prev = result;
                }
                insts.InsertRange(i + 1, added);
                return added.Count;
            }

            // Entity set：init 族 fn 内的写豁免 wrapper 链但仍经用户
            // setter（VM IsInitFunctionOf 同口径，刀2 InitExemption 复用）；
            // 非 init 写逐层命中成链，无层命中按访问器兜底
            private static int RewriteEntitySet(MwContext context, BakeState state, MirModule mir,
                MirFunction fn, List<MirInst> insts, int i, MirSetField inst)
            {
                if (!InitExemption.Applies(fn.Symbol, inst.FieldSymbol)
                    && EntityChainBake.EnsureSetChain(context, mir, state, inst.FieldSymbol)
                        is { } entry)
                {
                    return RewriteEntitySetChain(context, fn, insts, i, inst, entry);
                }
                if (ImplBinder.FindAccessor(context.Symbols, inst.FieldSymbol,
                        BilAccessorKind.Setter, fn.Symbol.Canonical) is { } setter)
                {
                    insts[i] = new MirCall(setter,
                        new List<MirOperand> { inst.Object, inst.Source }, null);
                }
                return 0;
            }

            // Entity set 使用点：MirGetWrapper 取最外命中环 wrapper 实例
            //（Entity 隐藏槽）+ MirCall 最外环（环间 inner 已链接，链末
            // 落终态）；wildcard 环首参 symbol = 字段 canonical 资源（VM
            // BuildRingInvokeArgs 链首 symbol=frame.FieldSymbol 同口径）
            private static int RewriteEntitySetChain(MwContext context, MirFunction fn,
                List<MirInst> insts, int i, MirSetField inst, EntitySetEntry entry)
            {
                var first = entry.Entry;
                var w = FreshLocal(fn, "$mw.epw.", MirType.Of(first.Wrapper));
                insts[i] = new MirGetWrapperAddr(inst.Object, first.Wrapper, w);
                var added = new List<MirInst>();
                var args = new List<MirOperand> { new MirLocalOperand(w) };
                if (first.Kind == MwProxyMatchKind.Wildcard)
                {
                    var sym = FreshLocal(fn, "$mw.eps.", ProxyWildcardAbi.StringType);
                    added.Add(new MirLoadResource(
                        ProxyWildcardAbi.AddStringResource(context, inst.FieldSymbol), sym));
                    args.Add(new MirLocalOperand(sym));
                }
                args.Add(inst.Source);
                added.Add(new MirCall(first.Symbol, args, null));
                insts.InsertRange(i + 1, added);
                return added.Count;
            }
        }

        // ===== init 族写豁免（VM VmContext.IsInitFunctionOf 同口径） =====
        private static class InitExemption
        {
            internal static bool Applies(MwMemberSymbol fn, string fieldSymbol)
            {
                var canonical = fn.Canonical;
                var dollar = canonical.IndexOf('$');
                if (dollar < 0)
                {
                    return false;
                }
                var owner = canonical.Substring(0, dollar);
                var rest = canonical.Substring(dollar + 1);
                if (rest.StartsWith(".static.", System.StringComparison.Ordinal))
                {
                    rest = rest.Substring(".static.".Length);
                }
                var open = rest.IndexOf('(');
                var name = open < 0 ? rest : rest.Substring(0, open);
                // ..init.wrapper / ..init.field.* 族无条件豁免（§9.7 修订：
                // 合成构造期写入方法不按 owner 限定）
                if (name == BilSpellings.InitWrapperMethodName
                    || name.StartsWith(BilSpellings.InitFieldMethodPrefix,
                        System.StringComparison.Ordinal))
                {
                    return true;
                }
                if (!BilVerificationContext.TryParseFieldSymbol(fieldSymbol, out var fieldOwner,
                        out _, out _)
                    || (owner != fieldOwner
                        && BilVerificationContext.StripTypeArguments(owner)
                            != BilVerificationContext.StripTypeArguments(fieldOwner)))
                {
                    return false;
                }
                return fn.HasKeyword(BilKeyword.Init);
            }
        }

        // ===== cell 访问器壳化（VM TryStartCellAccessorChain 同语义） =====
        private static class CellShell
        {
            internal static bool TryRewrite(MwContext context, BakeState state, MirModule mir,
                MirFunction fn)
            {
                if (!TryCellValueField(context, state, fn, out var fieldSymbol, out var isGetter))
                {
                    return false;
                }
                var (host, name, typeRef) = ChainBake.ParseField(fieldSymbol);
                var typeC = MirType.Of(typeRef).Canonical;
                var wrappers = state.Index.FieldWrappers(fieldSymbol);
                // 原访问器体整体外移为 $.wrapped. 终态 fn（终端体内自身
                // value 字段访问 = backing 直访，豁免成链；其他 wrapped
                // 字段访问仍成链）
                var terminalCanonical = isGetter
                    ? ChainBake.GetTerminalCanonical(host, name, typeC)
                    : ChainBake.SetTerminalCanonical(host, name, typeC);
                var terminalSymbol = ProxyBakeSupport.SyntheticMember(terminalCanonical,
                    owner: null);
                var terminal = new MirFunction(terminalSymbol, fn.ReturnType, fn.Parameters,
                    fn.Locals, fn.Blocks, false);

                var locals = new List<MirLocal>(fn.Parameters);
                var insts = new List<MirInst>();
                var thisOp = new MirLocalOperand(fn.Parameters[0].Name);
                MirTerminator terminator;
                if (isGetter)
                {
                    var chain = ChainBake.EnsureGetChain(context, mir, state, fieldSymbol,
                        terminalSymbol);
                    var prev = NewLocal(locals, "$mw.vpr.", fn.ReturnType);
                    insts.Add(new MirCall(terminalSymbol,
                        new List<MirOperand> { thisOp }, prev));
                    for (var r = wrappers.Count - 1; r >= 0; r--)
                    {
                        var w = NewLocal(locals, "$mw.vpw.", MirType.Of(wrappers[r]));
                        insts.Add(new MirGetWrapperFieldAddr(thisOp, fieldSymbol, wrappers[r],
                            w));
                        var result = NewLocal(locals, "$mw.vpr.", fn.ReturnType);
                        insts.Add(new MirCall(chain.Rings[r],
                            new List<MirOperand>
                                { new MirLocalOperand(w), new MirLocalOperand(prev) },
                            result));
                        prev = result;
                    }
                    terminator = new MirRet(new MirLocalOperand(prev));
                }
                else
                {
                    var entry = ChainBake.EnsureSetChain(context, mir, state, fieldSymbol,
                        terminalSymbol);
                    var w = NewLocal(locals, "$mw.vpw.", MirType.Of(wrappers[0]));
                    insts.Add(new MirGetWrapperFieldAddr(thisOp, fieldSymbol, wrappers[0], w));
                    insts.Add(new MirCall(entry,
                        new List<MirOperand>
                            { new MirLocalOperand(w), new MirLocalOperand(fn.Parameters[1].Name) },
                        null));
                    terminator = new MirRet(null);
                }
                var shell = new MirFunction(fn.Symbol, fn.ReturnType, fn.Parameters, locals,
                    new List<MirBlock> { new MirBlock("entry", insts, terminator) },
                    fn.IsEntrypoint);
                var list = mir.FunctionList;
                var index = list.IndexOf(fn);
                if (index < 0)
                {
                    throw new CompilerInternalException(
                        "FieldProxyBaking 找不到 cell 访问器 fn: " + fn.Symbol.Canonical);
                }
                list[index] = shell;
                mir.AddFunction(terminal);
                state.Known.Add(terminalCanonical);
                UseSite.RewriteFunction(context, state, mir, terminal, exemptField: fieldSymbol);
                return true;
            }

            // fn 是否为 value 字段带 wrapped 标记的 ..cell.. 隐藏子类
            // getValue/setValue（VM 口径：名 + ..cell.. 宿主 + #value@ 字段）
            private static bool TryCellValueField(MwContext context, BakeState state,
                MirFunction fn, out string fieldSymbol, out bool isGetter)
            {
                fieldSymbol = "";
                isGetter = false;
                var canonical = fn.Symbol.Canonical;
                var dollar = canonical.IndexOf('$');
                if (dollar < 0)
                {
                    return false;
                }
                var owner = canonical.Substring(0, dollar);
                if (!IsCellTypeRef(owner))
                {
                    return false;
                }
                var rest = canonical.Substring(dollar + 1);
                var open = rest.IndexOf('(');
                if (open < 0)
                {
                    return false;
                }
                var name = rest.Substring(0, open);
                if (name == "getValue")
                {
                    isGetter = true;
                }
                else if (name != "setValue")
                {
                    return false;
                }
                var type = context.Symbols.FindTypeByRef(owner);
                if (type == null)
                {
                    return false;
                }
                foreach (var member in type.Members)
                {
                    if (member.Declaration.Kind != BilMemberKind.Field
                        || !member.Canonical.StartsWith(owner + "#value@",
                            System.StringComparison.Ordinal)
                        || state.Index.FieldWrappers(member.Canonical).Count == 0)
                    {
                        continue;
                    }
                    fieldSymbol = member.Canonical;
                    return true;
                }
                return false;
            }

            // cell 隐藏子类判定（VM VmContext.IsCellTypeRef 同口径）
            private static bool IsCellTypeRef(string typeRef)
            {
                var name = typeRef;
                var generic = name.IndexOf('<');
                if (generic >= 0)
                {
                    name = name.Substring(0, generic);
                }
                var sep = name.LastIndexOf("::", System.StringComparison.Ordinal);
                if (sep >= 0)
                {
                    name = name.Substring(sep + 2);
                }
                return name.StartsWith("..cell..", System.StringComparison.Ordinal);
            }

            private static string NewLocal(List<MirLocal> locals, string prefix, MirType type)
            {
                var n = 0;
                while (Exists(locals, prefix + n))
                {
                    n++;
                }
                var name = prefix + n;
                locals.Add(new MirLocal(name, type));
                return name;
            }

            private static bool Exists(List<MirLocal> locals, string name)
            {
                foreach (var local in locals)
                {
                    if (local.Name == name)
                    {
                        return true;
                    }
                }
                return false;
            }
        }
    }
}
