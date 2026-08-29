using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;
namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// proxy 烘焙共享设施（MW10）：合成符号、可烘焙判定、模板特化、
    /// inner 链接与 trampoline 构造。由 ProxyBakingPass（Entity 方法/
    /// 运算符 specific + wildcard 面）、FieldProxyBakingPass（字段
    /// get/set 面：字段-Value + Entity 双源）与 MethodProxyBakingPass
    ///（刀6 Method wrapper .proxy.call 面——应用事实源为
    /// ..init.wrapper 体安装指令）复用；wildcard 胖值 ABI 辅助见
    /// <see cref="ProxyWildcardAbi"/>。纯设施类，不挂 pipeline。
    /// </summary>
    internal static class ProxyBakeSupport
    {
        // 烘焙产物命名中缀（测试与后续各烘焙 pass 消费，保持 public）
        public const string WrappedInfix = "$.wrapped.";
        public const string BakeInfix = "$.bake.";
        // Method wrapper 原始体外移中缀（刀6；与 Entity 的 $.wrapped.
        // 错开——三层组合时 $.mwrapped. 是最深层原始体）
        public const string MwrappedInfix = "$.mwrapped.";

        // 特化体内承载宿主 .this 的 inner 局部名（LinkInner / 各烘焙 pass 消费）
        internal const string InnerHostLocal = "$mw.inner.host";

        // trampoline 专用局部名（仅 BuildTrampoline 内部使用）
        private const string TrampolineWrapperLocal = "$mw.w";
        private const string TrampolineResultLocal = "$mw.r";

        /// <summary>
        /// 构造合成成员符号（无源码声明的烘焙产物 fn 用）。
        /// 供 ProxyBakingPass 及后续各烘焙 pass 命名 wrapped/baked fn。
        /// </summary>
        internal static MwMemberSymbol SyntheticMember(string canonical, MwTypeSymbol? owner) =>
            new MwMemberSymbol(new BilSimpleMemberDeclaration(BilMemberKind.Method, canonical),
                owner, isExternal: false);

        /// <summary>
        /// 可烘焙宿主方法判定：排除 init/ext/static/..init；泛型方法
        ///（签名含 '&lt;' 占位）自遗6起可经 wildcard 环烘焙（specific
        /// 形状不可能匹配泛型成员——ProxyMatcher.MatchMethod 对泛型成员
        /// 只落 Wildcard/None）；泛型**运算符**仍排除（VM 侧运算符实参
        /// 不含 hidden typeid，链末 typeid 退化 .any——无对应恢复路径；
        /// wildcard 命中时由 ProxyBakingPass 受控拒绝）。$$call 是
        /// callable 协议入口（lambda 隐藏类覆写，VM
        /// TryStartMethodWrapperChain 同口径除外），不归 Entity 方法/
        /// 运算符链。供各烘焙 pass 筛选宿主成员。
        /// </summary>
        internal static bool IsBakeableHostMethod(MwMemberSymbol member)
        {
            if (member.Declaration.Kind != BilMemberKind.Method
                || member.HasKeyword(BilKeyword.Init)
                || member.HasKeyword(BilKeyword.Ext))
            {
                return false;
            }
            var key = member.SignatureKey;
            if (key.StartsWith(".static.", System.StringComparison.Ordinal)
                || key.StartsWith("..init", System.StringComparison.Ordinal))
            {
                return false;
            }
            // 泛型运算符排除（受控拒绝见 ProxyBakingPass.Run）；$$call
            // 排除，其余运算符可烘焙
            if (member.IsOperatorMember
                && member.Canonical.IndexOf('<', System.StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            return !member.Canonical.Contains("$$call(", System.StringComparison.Ordinal);
        }

        // 预判 ProxyBakingPass 是否会烘焙该宿主成员（可烘焙 + 有 MIR 体 +
        // 任一层 proxy 命中），命中时给出其 $.wrapped. 体定名符号
        //（host$wrapped<sigkey>@ret）。刀3b 消费：Entity 字段 set 链终态
        // 须绕过 trampoline 直调 $.wrapped. 体——VM FinishSetToBacking 的
        // InvokeResolved 旁路同口径；该 fn 由 ProxyBakingPass 随后按同一定
        // 名规则落地（定名确定性，先引后存合法）。
        internal static bool WillBakeHostMethod(MwContext context, MirModule mir,
            WrapperApplicationIndex index, MwMemberSymbol member, out string wrappedCanonical)
        {
            wrappedCanonical = "";
            if (member.Owner == null || !IsBakeableHostMethod(member))
            {
                return false;
            }
            MirFunction? original = null;
            foreach (var fn in mir.Functions)
            {
                if (fn.Symbol.Canonical == member.Canonical)
                {
                    original = fn;
                    break;
                }
            }
            if (original == null)
            {
                return false;
            }
            var any = false;
            foreach (var wrapperRef in index.EntityWrappers(member.Owner.Canonical))
            {
                var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                    ?? throw new CompilerInternalException("Entity wrapper 类型缺失: " + wrapperRef);
                if (ProxyMatcher.MatchMethod(wrapper, member) != MwProxyMatchKind.None)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                return false;
            }
            wrappedCanonical = member.Owner.Canonical + WrappedInfix
                + member.SignatureKey + "@" + original.ReturnType.Canonical;
            return true;
        }

        // 预判 MethodProxyBakingPass 是否会烘焙该方法（可烘焙 + 有 MIR
        // 体 + 安装指令中任一 wrapper 带 .proxy.call），命中时给出其
        // $.mwrapped. 最深层原始体定名符号（host$.mwrapped<sigkey>@ret）。
        // 刀6 消费：字段 set 终态等旁路落点须绕过 method trampoline
        // 直调最深层原始体——VM FinishSetToBacking/InvokeResolved 旁路
        // 全链（Entity + Method 双链）语义同口径；该 fn 由
        // MethodProxyBakingPass 随后按同一定名规则落地（定名确定性，
        // 先引后存合法）。
        internal static bool WillBakeMethodWrapper(MwContext context, MirModule mir,
            MwMemberSymbol member, out string mwrappedCanonical)
        {
            mwrappedCanonical = "";
            if (member.Owner == null || !MethodProxyBakingPass.IsBakeableTarget(member))
            {
                return false;
            }
            MirFunction? original = null;
            foreach (var fn in mir.Functions)
            {
                if (fn.Symbol.Canonical == member.Canonical)
                {
                    original = fn;
                    break;
                }
            }
            if (original == null)
            {
                return false;
            }
            var any = false;
            foreach (var (method, wrappers) in MethodProxyBakingPass.CollectInstalls(mir))
            {
                if (method != member.Canonical)
                {
                    continue;
                }
                foreach (var wrapperRef in wrappers)
                {
                    if (MethodProxyBakingPass.MatchMethodCallProxy(context, wrapperRef)
                        != MwProxyMatchKind.None)
                    {
                        any = true;
                        break;
                    }
                }
                break;
            }
            if (!any)
            {
                return false;
            }
            mwrappedCanonical = member.Owner.Canonical + MwrappedInfix
                + member.SignatureKey + "@" + original.ReturnType.Canonical;
            return true;
        }

        /// <summary>
        /// proxy 模板特化核心：MirBuilder.BuildFunction → BuildSubstitution
        /// 代入 → .this/局部/返回类型替换 → 确保 InnerHostLocal 局部 →
        /// 块首插 MirGetSelf。返回特化体（符号未定），returnType 带出
        /// 代入后的返回类型；调用方据此命名 baked 符号并 new MirFunction。
        /// 供 ProxyBakingPass.Specialize 及后续各烘焙 pass 复用。
        /// extraSubst（Value wrapper 刀2）：方法级泛型占位（如 TValue）→
        /// 具体类型的追加代入；每个命中名的 .generic.&lt;名&gt; 隐藏 typeid
        /// 形参从形参表剔除（调用点不再传 typeid 实参），局部保留并在
        /// 块首（MirGetSelf 之后）插 getid.type 就地物化，供体内
        /// cast/type.is 等仍按名引用该局部的指令使用。
        /// erasedTypePacks（wildcard 刀3a）：方法级泛型包占位
        ///（TNamedArgs/TUnnamedArgs，.array/.map 形态）的 .generic.&lt;名&gt;
        /// 隐藏形参与同名局部一并剔除——native 按 Any 擦除形态不物化
        /// typeid 包（VM BuildProxyArgs 恒填空包）；环内 MirInnerCall 由
        /// 调用方随后重写为动态分派块，包引用随之消失。
        /// </summary>
        internal static MirFunction BuildSpecializedBody(MwContext context, BilFunction template,
            string wrapperRef, MwTypeSymbol wrapper, string hostCanonical, out MirType returnType,
            IReadOnlyDictionary<string, string>? extraSubst = null,
            IReadOnlySet<string>? erasedTypePacks = null)
        {
            var built = MirBuilder.BuildFunction(context, template);
            var subst = ConstructedTypeCollector.BuildSubstitution(wrapperRef, wrapper.Declaration);
            if (extraSubst != null && extraSubst.Count > 0)
            {
                var merged = subst == null
                    ? new Dictionary<string, string>(System.StringComparer.Ordinal)
                    : new Dictionary<string, string>(subst, System.StringComparer.Ordinal);
                foreach (var pair in extraSubst)
                {
                    merged[pair.Key] = pair.Value;
                }
                subst = merged;
            }
            foreach (var local in built.Locals)
            {
                local.Type = local.Name == ".this"
                    ? MirType.Of(wrapperRef)
                    : MirType.Of(ConstructedTypeCollector.Substitute(local.Type.Canonical, subst));
            }
            returnType = MirType.Of(
                ConstructedTypeCollector.Substitute(built.ReturnType.Canonical, subst));
            if (!built.TryFindLocal(InnerHostLocal, out _))
            {
                built.AddLocal(new MirLocal(InnerHostLocal, MirType.Of(hostCanonical)));
            }
            built.Blocks[0].InstructionList.Insert(0, new MirGetSelf(InnerHostLocal));
            var hasSubst = extraSubst != null && extraSubst.Count > 0;
            var hasErased = erasedTypePacks != null && erasedTypePacks.Count > 0;
            if (hasSubst || hasErased)
            {
                var parameters = new List<MirLocal>(built.Parameters);
                var locals = new List<MirLocal>(built.Locals);
                var insertAt = 1;
                if (extraSubst != null)
                {
                    foreach (var pair in extraSubst)
                    {
                        var hidden = ".generic." + pair.Key;
                        if (!built.TryFindLocal(hidden, out _))
                        {
                            continue;
                        }
                        parameters.RemoveAll(p => p.Name == hidden);
                        built.Blocks[0].InstructionList.Insert(insertAt++,
                            new MirGetTypeId(pair.Value, hidden));
                    }
                }
                if (erasedTypePacks != null)
                {
                    foreach (var name in erasedTypePacks)
                    {
                        var hidden = ".generic." + name;
                        parameters.RemoveAll(p => p.Name == hidden);
                        locals.RemoveAll(l => l.Name == hidden);
                    }
                }
                built = new MirFunction(built.Symbol, returnType, parameters, locals,
                    built.Blocks, false);
            }
            return built;
        }

        /// <summary>
        /// 把特化体内的 MirInnerCall 改写为对下一棒 callee 的 MirCall
        ///（有下一层 wrapper 时先取下一环 receiver：Entity 面
        /// MirGetWrapperAddr / wrapperField 非空（字段-Value 面）时
        /// MirGetWrapperFieldAddr——刀3c：环 receiver 是宿主隐藏槽的就地
        /// 地址，非值拷贝，§14.5 原地访问）。
        /// 供 ProxyBakingPass 及后续各烘焙 pass 链接 inner 链。
        /// </summary>
        internal static void LinkInner(MirFunction baked, MwMemberSymbol nextCallee,
            string? nextWrapper, string? wrapperField = null)
        {
            var hostOp = new MirLocalOperand(InnerHostLocal);
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
                    MirOperand receiver = hostOp;
                    if (nextWrapper != null)
                    {
                        var wName = "$mw.inner.w." + temp++;
                        baked.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper)));
                        insts[i] = wrapperField == null
                            ? (MirInst)new MirGetWrapperAddr(hostOp, nextWrapper, wName)
                            : new MirGetWrapperFieldAddr(hostOp, wrapperField, nextWrapper, wName);
                        insts.Insert(i + 1, MakeInnerCall(nextCallee, new MirLocalOperand(wName),
                            inner));
                        i++;
                        continue;
                    }
                    insts[i] = MakeInnerCall(nextCallee, receiver, inner);
                }
            }
        }

        /// <summary>
        /// 由 MirInnerCall 构造普通 MirCall（receiver 作首实参）。
        /// 供 LinkInner 及后续各烘焙 pass 的 inner 改写使用。
        /// </summary>
        internal static MirCall MakeInnerCall(MwMemberSymbol callee, MirOperand receiver,
            MirInnerCall inner)
        {
            var args = new List<MirOperand>(inner.Args.Count + 1) { receiver };
            args.AddRange(inner.Args);
            return new MirCall(callee, args, inner.Result, inner.ExcTarget);
        }

        /// <summary>
        /// 构造原名槽 trampoline：取最外层 wrapper 隐藏槽地址（
        /// MirGetWrapperAddr，刀3c 原地访问）→ 调首棒 baked → 按原签名
        /// 返回。供 ProxyBakingPass 及后续各烘焙 pass 替换原名 fn。
        /// </summary>
        internal static MirFunction BuildTrampoline(MirFunction original, MwMemberSymbol baked,
            string outerWrapper)
        {
            var locals = new List<MirLocal>(original.Parameters);
            locals.Add(new MirLocal(TrampolineWrapperLocal, MirType.Of(outerWrapper)));
            string? resultName = null;
            if (!original.ReturnType.IsVoid)
            {
                resultName = TrampolineResultLocal;
                locals.Add(new MirLocal(resultName, original.ReturnType));
            }
            var callArgs = new List<MirOperand> { new MirLocalOperand(TrampolineWrapperLocal) };
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name == ".this"
                    || parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                callArgs.Add(new MirLocalOperand(parameter.Name));
            }
            var insts = new List<MirInst>
            {
                new MirGetWrapperAddr(new MirLocalOperand(".this"), outerWrapper,
                    TrampolineWrapperLocal),
                new MirCall(baked, callArgs, resultName),
            };
            var block = new MirBlock("entry", insts,
                new MirRet(resultName == null ? null : new MirLocalOperand(resultName)));
            return new MirFunction(original.Symbol, original.ReturnType, original.Parameters,
                locals, new List<MirBlock> { block }, original.IsEntrypoint);
        }
    }
}
