using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Pipeline;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Passes
{
    /// <summary>
    /// Method wrapper 烘焙（MW10 刀6，§14.4）：以 ..init.wrapper 体内的
    /// MirNewWrapper(Method) 安装指令为唯一事实源（BIL 方法声明不保留
    /// wrapped 修饰符），按实现槽方法符号建 .proxy.call 环链——
    /// specific 环（方法级泛型 TReturn 代入被代理方法返回类型，void 擦除
    /// 为 .any 胖值形态）+ wildcard 环（.name 首参 = 实现槽 canonical
    /// 字符串资源、args = 全实参按 named Any 具名包，恒等转发；改写
    /// .name 重路由受控拒绝——VM 已支持，后续补）。原始方法体外移为
    /// Host$.mwrapped.&lt;sig&gt;@&lt;ret&gt;（与 Entity 的 $.wrapped.
    /// 错开），原名 fn 替换为 trampoline：MirGetWrapperMethodAddr 取最
    /// 外环 wrapper 隐藏槽地址（刀3c 原地访问同口径，状态跨调用持久）→
    /// 调首环。覆盖面：实例/虚/接口方法（trampoline 在实现槽 fn，
    /// vtable/iMap 不动）、静态方法（companion 实例 fn 被 trampoline，
    /// 静态壳调它自然命中）、全局函数（..globals.host 实例 fn 同构）、
    /// lambda（..lambda..UUID$$call fn 被 trampoline，invoke.indirect
    /// 经 vtable 命中）。排除：init 族/抽象无体/静态壳/除 $$call 外的
    /// 运算符。本 pass 排在 FieldProxyBaking 之后、ProxyBaking 之前——
    /// Entity 烘焙随后把 method trampoline 整体外移为 $.wrapped. 并换
    /// Entity trampoline，天然形成 Entity→Method→raw 三层；绕过全链的
    /// 旁路落点（字段 set 终态等）指向 $.mwrapped. 最深层原始体（VM
    /// FinishSetToBacking/InvokeResolved 旁路同口径）。读：Mir +
    /// Symbols + BIL 模板 fn；写：追加合成 fn、替换原名 MirFunction。
    /// 小改写 pass，不上 CRTP。
    /// </summary>
    public sealed class MethodProxyBakingPass : IMwStage
    {
        public string Name => "MethodProxyBaking";

        public void Run(MwContext context)
        {
            var mir = context.Mir
                ?? throw new CompilerInternalException("MethodProxyBaking 要求 Mir 已挂载");
            var installs = CollectInstalls(mir);
            if (installs.Count == 0)
            {
                return;
            }
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

            foreach (var (methodCanonical, wrappers) in installs)
            {
                // 无 MIR 体（抽象/未达）不建链——VM 同场景链也无从发起
                if (!byCanonical.TryGetValue(methodCanonical, out var original))
                {
                    continue;
                }
                var member = context.Symbols.FindMember(methodCanonical)
                    ?? throw new CompilerInternalException(
                        "new.wrapper.method 的方法符号无驻留成员: " + methodCanonical);
                if (!IsBakeableTarget(member))
                {
                    continue;
                }
                var kinds = new List<MwProxyMatchKind>(wrappers.Count);
                var any = false;
                foreach (var wrapperRef in wrappers)
                {
                    var kind = MatchMethodCallProxy(context, wrapperRef);
                    kinds.Add(kind);
                    if (kind != MwProxyMatchKind.None)
                    {
                        any = true;
                    }
                }
                if (!any)
                {
                    continue;
                }
                BakeMethod(context, mir, bilBySymbol, member, original, wrappers, kinds);
            }
        }

        // ===== 安装收集（唯一事实源：..init.wrapper 体安装指令） =====

        // 方法 canonical → outer→inner wrapper 列表（安装指令序；多宿主
        // 闭包缝合重复安装同一实现槽方法时去重，首见序为准——§14.9 重申
        // 约束下各安装者列出的集合一致）
        internal static List<(string Method, List<string> Wrappers)> CollectInstalls(
            MirModule mir)
        {
            var order = new List<(string Method, List<string> Wrappers)>();
            var byMethod = new Dictionary<string, List<string>>(System.StringComparer.Ordinal);
            foreach (var fn in mir.Functions)
            {
                foreach (var block in fn.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is not MirNewWrapper { Kind: MirWrapperInstallKind.Method }
                            install || install.MethodSymbol == null)
                        {
                            continue;
                        }
                        if (!byMethod.TryGetValue(install.MethodSymbol, out var list))
                        {
                            list = new List<string>();
                            byMethod[install.MethodSymbol] = list;
                            order.Add((install.MethodSymbol, list));
                        }
                        var wrapperRef = install.WrapperType;
                        if (!list.Contains(wrapperRef))
                        {
                            list.Add(wrapperRef);
                        }
                    }
                }
            }
            return order;
        }

        // 可烘焙目标判定：实例普通方法（含 companion / ..globals.host /
        // lambda 隐藏类的实例 fn）；排除 init 族/ext/静态壳/泛型成员与
        // 除 $$call 外的运算符（$$call 是 callable 协议入口，VM
        // TryStartMethodWrapperChain 同口径放行）
        internal static bool IsBakeableTarget(MwMemberSymbol member)
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
            if (member.Canonical.IndexOf('<') >= 0)
            {
                return false;
            }
            var dollar = member.Canonical.IndexOf('$');
            if (dollar >= 0 && dollar + 1 < member.Canonical.Length
                && member.Canonical[dollar + 1] == '$')
            {
                return member.Canonical.Contains("$$call(", System.StringComparison.Ordinal);
            }
            return true;
        }

        // .proxy.call 逐层匹配：模板按名唯一（§14.1 择一），specific /
        // wildcard 按 .name 首参区分（VM VmWrapperDispatch.FindProxy 的
        // Call 类别同口径——BIL 修饰符 kind 对 .proxy.call 恒为
        // specific，不足为凭）；无 .proxy.call → None 透明跳过
        internal static MwProxyMatchKind MatchMethodCallProxy(MwContext context, string wrapperRef)
        {
            var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("Method wrapper 类型缺失: " + wrapperRef);
            var proxy = FindCallProxy(wrapper);
            if (proxy == null)
            {
                return MwProxyMatchKind.None;
            }
            var signature = CanonicalSignature.Parse(proxy.Canonical);
            return signature.Parameters.Count > 0 && signature.Parameters[0].Name == ".name"
                ? MwProxyMatchKind.Wildcard
                : MwProxyMatchKind.Specific;
        }

        // .proxy.call 模板查找（名唯一，kind 修饰符不足为凭——见
        // MatchMethodCallProxy 注）
        private static MwMemberSymbol? FindCallProxy(MwTypeSymbol wrapper) =>
            ProxyMatcher.FindProxy(wrapper, ".proxy.call", BilProxyKind.Specific)
            ?? ProxyMatcher.FindProxy(wrapper, ".proxy.call", BilProxyKind.Wildcard);

        // ===== 链烘焙 =====

        private static void BakeMethod(MwContext context, MirModule mir,
            Dictionary<string, BilFunction> bilBySymbol, MwMemberSymbol member,
            MirFunction original, List<string> wrappers, List<MwProxyMatchKind> kinds)
        {
            var hostCanonical = member.Owner?.Canonical
                ?? throw new CompilerInternalException(
                    "Method wrapper 目标无宿主类型: " + member.Canonical);
            // 原始体整体外移为 $.mwrapped.（独立中缀，与 Entity 的
            // $.wrapped. 错开；三层组合时它是最深层）
            var wrappedSymbol = ProxyBakeSupport.SyntheticMember(hostCanonical
                + ProxyBakeSupport.MwrappedInfix
                + member.SignatureKey + "@" + original.ReturnType.Canonical, owner: null);
            var wrappedFn = new MirFunction(wrappedSymbol, original.ReturnType,
                original.Parameters, original.Locals, original.Blocks, false);

            // 环自内向外逐环特化；nextXxx = 当前环的 inner 落点（终态或内一环）
            MwMemberSymbol nextCallee = wrappedSymbol;
            MirFunction? nextRingFn = null;
            string? nextWrapper = null;
            var nextKind = MwProxyMatchKind.None;
            MirFunction? outermostRing = null;
            string? outermostWrapper = null;
            var outermostKind = MwProxyMatchKind.None;
            for (var i = wrappers.Count - 1; i >= 0; i--)
            {
                var kind = kinds[i];
                if (kind == MwProxyMatchKind.None)
                {
                    continue;
                }
                var wrapperRef = wrappers[i];
                MirFunction baked;
                if (kind == MwProxyMatchKind.Specific)
                {
                    baked = SpecializeSpecific(context, bilBySymbol, member, original,
                        wrapperRef, hostCanonical);
                    if (nextKind == MwProxyMatchKind.Wildcard)
                    {
                        LinkInnerToWildcard(context, baked, member, original, nextRingFn!,
                            nextWrapper!);
                    }
                    else
                    {
                        LinkInnerConcrete(context, baked, member, nextCallee, nextWrapper,
                            nextKind == MwProxyMatchKind.Specific
                                ? nextRingFn!.ReturnType
                                : original.ReturnType);
                    }
                }
                else
                {
                    baked = SpecializeWildcard(context, bilBySymbol, member, original,
                        wrapperRef, hostCanonical);
                    RewriteWildcardInners(context, mir, baked, member, original, nextCallee,
                        nextRingFn, nextWrapper, nextKind);
                }
                IndexOperatorLoweringPass.RewriteFunction(context, baked);
                AccessorLoweringPass.RewriteFunction(context, baked);
                // proxy 体内访问 wrapped 字段同样成链（字段链面钩子）
                FieldProxyBakingPass.RewriteFunction(context, baked);
                mir.AddFunction(baked);
                nextCallee = baked.Symbol;
                nextRingFn = baked;
                nextWrapper = wrapperRef;
                nextKind = kind;
                outermostRing = baked;
                outermostWrapper = wrapperRef;
                outermostKind = kind;
            }

            var trampoline = outermostKind == MwProxyMatchKind.Wildcard
                ? BuildWildcardTrampoline(context, original, member, outermostRing!,
                    outermostWrapper!)
                : BuildSpecificTrampoline(original, member, nextCallee, outermostWrapper!);
            var list = mir.FunctionList;
            var index = list.IndexOf(original);
            if (index < 0)
            {
                throw new CompilerInternalException(
                    "MethodProxyBaking 找不到原名 fn: " + member.Canonical);
            }
            list[index] = trampoline;
            mir.AddFunction(wrappedFn);
        }

        // specific 环特化：形状校验（模板值形参名/类型与被代理方法全等）
        // → 方法级泛型（恰一返回占位 TReturn）代入 M 返回类型（void 擦除
        // 为 .any——void 无 TypeSheet 且环 ABI 以胖值承载结果，wildcard
        // 刀3a 同口径）→ BuildSpecializedBody
        private static MirFunction SpecializeSpecific(MwContext context,
            Dictionary<string, BilFunction> bilBySymbol, MwMemberSymbol member,
            MirFunction original, string wrapperRef, string hostCanonical)
        {
            var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("烘焙找不到 wrapper: " + wrapperRef);
            var proxy = FindCallProxy(wrapper)
                ?? throw new CompilerInternalException(
                    "Specific 命中后找不到 .proxy.call 模板: " + wrapperRef);
            if (!bilBySymbol.TryGetValue(proxy.Canonical, out var bilFn))
            {
                throw new MwNotSupportedException("proxy 模板无 fn 体: " + proxy.Canonical);
            }
            CheckSpecificShape(bilFn, original, proxy.Canonical, member.Canonical);
            // 方法级泛型分类：恰一非标量包占位（TReturn）→ 代入返回类型；
            // 包形态/多占位 → 受控拒绝（§14.4 specific 形态恰一泛型参数
            // 作返回类型，前端 ProxyCheckers 同口径）
            var extraSubst = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var arg in bilFn.Args)
            {
                if (!arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                var name = arg.Name.Substring(".generic.".Length);
                if (ProxyWildcardAbi.IsPackType(arg.TypeRef))
                {
                    throw new MwNotSupportedException(
                        "specific .proxy.call 泛型包占位不支持（受控拒绝）: " + proxy.Canonical);
                }
                extraSubst[name] = original.ReturnType.IsVoid
                    ? ".any"
                    : original.ReturnType.Canonical;
            }
            var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, wrapperRef, wrapper,
                hostCanonical, out var returnType,
                extraSubst.Count > 0 ? extraSubst : null);
            if (!original.ReturnType.IsVoid
                && returnType.Canonical != original.ReturnType.Canonical)
            {
                throw new MwNotSupportedException(
                    $"specific .proxy.call 返回类型与被代理方法不符（受控拒绝）: "
                    + proxy.Canonical + " → " + member.Canonical);
            }
            if (original.ReturnType.IsVoid && !returnType.IsVoid && !returnType.IsAny)
            {
                throw new MwNotSupportedException(
                    $"specific .proxy.call 返回类型与被代理方法不符（受控拒绝）: "
                    + proxy.Canonical + " → " + member.Canonical);
            }
            var symbol = ProxyBakeSupport.SyntheticMember(
                wrapper.Canonical + ProxyBakeSupport.BakeInfix + hostCanonical + "$"
                + member.SignatureKey + "@" + returnType.Canonical, proxy.Owner);
            return new MirFunction(symbol, returnType, body.Parameters, body.Locals,
                body.Blocks, false);
        }

        // specific 形状校验：模板值形参（排除 .this/.generic.*/.return）
        // 与被代理方法值形参 名/类型 全等（§14.4 镜像；前端 ProxyMatching
        // 已强制，此处对直构 BIL 受控拒绝）
        private static void CheckSpecificShape(BilFunction template, MirFunction original,
            string proxyCanonical, string memberCanonical)
        {
            var templateParams = new List<(string Name, string TypeRef)>();
            foreach (var arg in template.Args)
            {
                if (arg.Name == ".return" || arg.Name == ".this"
                    || arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                templateParams.Add((arg.Name, arg.TypeRef));
            }
            var methodParams = new List<(string Name, string Canonical)>();
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name == ".this"
                    || parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                methodParams.Add((parameter.Name, parameter.Type.Canonical));
            }
            if (templateParams.Count != methodParams.Count)
            {
                throw new MwNotSupportedException(
                    $"specific .proxy.call 形参与被代理方法不全等（受控拒绝）: "
                    + proxyCanonical + " → " + memberCanonical);
            }
            for (var i = 0; i < templateParams.Count; i++)
            {
                if (templateParams[i].Name != methodParams[i].Name
                    || MwTypeKey.Normalize(templateParams[i].TypeRef) != methodParams[i].Canonical)
                {
                    throw new MwNotSupportedException(
                        $"specific .proxy.call 形参与被代理方法不全等（受控拒绝）: "
                        + proxyCanonical + " → " + memberCanonical);
                }
            }
        }

        // wildcard 环特化：.generic 标量 typeid 代入成员返回类型（void
        // 擦除 .any）、包占位剔除；环 ABI 返回恒为 .any（§14.4 wildcard
        // 形态 (...): Any——具体非 Any 返回受控拒绝）
        private static MirFunction SpecializeWildcard(MwContext context,
            Dictionary<string, BilFunction> bilBySymbol, MwMemberSymbol member,
            MirFunction original, string wrapperRef, string hostCanonical)
        {
            var wrapper = context.Symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("烘焙找不到 wrapper: " + wrapperRef);
            var proxy = FindCallProxy(wrapper)
                ?? throw new CompilerInternalException(
                    "Wildcard 命中后找不到 .proxy.call 模板: " + wrapperRef);
            if (!bilBySymbol.TryGetValue(proxy.Canonical, out var bilFn))
            {
                throw new MwNotSupportedException("proxy 模板无 fn 体: " + proxy.Canonical);
            }
            var erasedReturn = original.ReturnType.IsVoid ? ".any" : original.ReturnType.Canonical;
            ProxyWildcardAbi.ClassifyGenericParams(bilFn, erasedReturn,
                out var extraSubst, out var erasedPacks);
            var body = ProxyBakeSupport.BuildSpecializedBody(context, bilFn, wrapperRef, wrapper,
                hostCanonical, out var ringReturn, extraSubst, erasedPacks);
            if (!ringReturn.IsAny)
            {
                throw new MwNotSupportedException(
                    "wildcard .proxy.call 模板返回类型须为 Any（受控拒绝）: " + proxy.Canonical);
            }
            var symbol = ProxyBakeSupport.SyntheticMember(
                wrapper.Canonical + ProxyBakeSupport.BakeInfix + hostCanonical + "$"
                + member.SignatureKey + "@" + ringReturn.Canonical, proxy.Owner);
            return new MirFunction(symbol, ringReturn, body.Parameters, body.Locals,
                body.Blocks, false);
        }

        // ===== inner 链接 =====

        // specific 环 inner → 具体落点（内一环 specific / 终态
        // $.mwrapped.）：下一环 receiver 经 MirGetWrapperMethodAddr 取槽
        // 地址（刀3c 原地访问同口径），终态 receiver = 宿主本体。下一落点
        // 返回 void 而 inner 带结果槽（void 擦除 .any 形态）时补 .any
        // 零值胖引用（ProxyBakingPass 链末 void 同口径）
        private static void LinkInnerConcrete(MwContext context, MirFunction baked,
            MwMemberSymbol member, MwMemberSymbol nextCallee, string? nextWrapper,
            MirType nextReturn)
        {
            var hostOp = new MirLocalOperand(ProxyBakeSupport.InnerHostLocal);
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
                    var insertAddr = false;
                    string? wName = null;
                    if (nextWrapper != null)
                    {
                        wName = "$mw.mw.inner.w." + temp++;
                        baked.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper)));
                        receiver = new MirLocalOperand(wName);
                        insertAddr = true;
                    }
                    var voidPad = nextReturn.IsVoid && inner.Result != null;
                    var args = new List<MirOperand>(inner.Args.Count + 1) { receiver };
                    args.AddRange(inner.Args);
                    var call = new MirCall(nextCallee, args, voidPad ? null : inner.Result,
                        inner.ExcTarget);
                    if (insertAddr)
                    {
                        insts[i] = new MirGetWrapperMethodAddr(hostOp, member.Canonical,
                            nextWrapper!, wName!);
                        insts.Insert(i + 1, call);
                        i++;
                    }
                    else
                    {
                        insts[i] = call;
                    }
                    if (voidPad)
                    {
                        insts.Insert(i + 1, new MirLoadResource(
                            ProxyWildcardAbi.AddNullAnyResource(context), inner.Result!));
                        i++;
                    }
                }
            }
        }

        // specific 环 inner → wildcard 下一环：具体实参按方法形参名打包
        // 具名包 + .name = 实现槽 canonical 资源（VM BuildRingInvokeArgs
        // 的 Call/nextWildcard 分支同口径）；环返回 .any 拆回 inner 结果
        // 槽声明类型（void 擦除 .any 时直通）
        private static void LinkInnerToWildcard(MwContext context, MirFunction baked,
            MwMemberSymbol member, MirFunction original, MirFunction nextRing,
            string nextWrapper)
        {
            var hostOp = new MirLocalOperand(ProxyBakeSupport.InnerHostLocal);
            var argNames = new List<string>();
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name != ".this"
                    && !parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    argNames.Add(parameter.Name);
                }
            }
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
                    if (inner.Args.Count != argNames.Count)
                    {
                        throw new CompilerInternalException(
                            "specific 环 inner 实参数与被代理方法签名不符: "
                            + baked.Symbol.Canonical);
                    }
                    var wName = "$mw.mw.inner.w." + temp++;
                    baked.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper)));
                    var symbolLocal = "$mw.mw.inner.sym." + temp++;
                    baked.AddLocal(new MirLocal(symbolLocal, ProxyWildcardAbi.StringType));
                    var namedLocal = "$mw.mw.inner.named." + temp++;
                    baked.AddLocal(new MirLocal(namedLocal, ProxyWildcardAbi.NamedPackType));
                    var pack = new List<MirInst>
                    {
                        new MirLoadResource(ProxyWildcardAbi.AddStringResource(context,
                            member.Canonical), symbolLocal),
                    };
                    var argTypes = new List<MirType>();
                    foreach (var parameter in original.Parameters)
                    {
                        if (parameter.Name != ".this"
                            && !parameter.Name.StartsWith(".generic.",
                                System.StringComparison.Ordinal))
                        {
                            argTypes.Add(parameter.Type);
                        }
                    }
                    ProxyWildcardAbi.EmitNamedPack(context, baked, pack, inner.Args, argTypes,
                        argNames, namedLocal, "$mw.mw.inner.pack." + temp++);
                    var callArgs = ProxyWildcardAbi.BuildInvokeArgs(nextRing,
                        new MirLocalOperand(wName), symbolLocal, null, namedLocal, null, null);
                    if (inner.Result != null
                        && baked.FindLocal(inner.Result).Type.Canonical
                            != ProxyWildcardAbi.AnyType.Canonical)
                    {
                        // 环返回 .any → 拆回 inner 结果槽声明类型
                        var anyResult = "$mw.mw.inner.rt." + temp++;
                        baked.AddLocal(new MirLocal(anyResult, ProxyWildcardAbi.AnyType));
                        pack.Add(new MirCall(nextRing.Symbol, callArgs, anyResult,
                            inner.ExcTarget));
                        ProxyWildcardAbi.EmitUnboxFromAny(context, pack,
                            new MirLocalOperand(anyResult), inner.Result,
                            baked.FindLocal(inner.Result).Type);
                    }
                    else
                    {
                        pack.Add(new MirCall(nextRing.Symbol, callArgs, inner.Result,
                            inner.ExcTarget));
                    }
                    insts[i] = new MirGetWrapperMethodAddr(hostOp, member.Canonical,
                        nextWrapper, wName);
                    insts.InsertRange(i + 1, pack);
                    i += pack.Count;
                }
            }
        }

        // wildcard 环 inner → 恒等转发（VM BuildRingInvokeArgs 的 Call/
        // ringIsWildcard 分支同口径）：.name 必须是本环 .name 形参原样
        // 转发——改写 .name 重路由受控拒绝（VM RerouteWildcardInner 已
        // 支持，后续补）。无 fast-path CFG 变动（.name 恒等 → 恒命中），
        // 原地改写为直线指令序列
        private static void RewriteWildcardInners(MwContext context, MirModule mir,
            MirFunction ring, MwMemberSymbol member, MirFunction original,
            MwMemberSymbol nextCallee, MirFunction? nextRingFn, string? nextWrapper,
            MwProxyMatchKind nextKind)
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
            // §14.4 wildcard 形态：(.name: String, args: named Any...)；
            // 手写 BIL 省略包形参时容忍（补空包，刀3a 同口径）
            if (valueParameterCount != 1 && valueParameterCount != 2)
            {
                throw new MwNotSupportedException(
                    "wildcard .proxy.call 环形参形状不支持（受控拒绝）: " + ring.Symbol.Canonical);
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
                    var symbolOp = inner.Args[inner.Args.Count - valueParameterCount];
                    if (symbolOp is not MirLocalOperand { Name: ".name" })
                    {
                        throw new MwNotSupportedException(
                            "Method wrapper wildcard 环改写 .name 重路由暂不支持"
                            + "（VM 已支持，后续补）: " + ring.Symbol.Canonical);
                    }
                    MirOperand namedOp;
                    var packInsts = new List<MirInst>();
                    if (valueParameterCount >= 2)
                    {
                        namedOp = inner.Args[inner.Args.Count - 1];
                    }
                    else
                    {
                        var namedLocal = ProxyWildcardAbi.FreshLocal(ring, "$mw.mw.named.",
                            ProxyWildcardAbi.NamedPackType);
                        ProxyWildcardAbi.EmitEmptyNamedPack(packInsts, namedLocal);
                        namedOp = new MirLocalOperand(namedLocal);
                    }

                    if (nextKind == MwProxyMatchKind.Wildcard)
                    {
                        // 透传 .name + 具名包直进内一 wildcard 环（环 ABI
                        // 两端皆 .any，结果直通）
                        if (namedOp is not MirLocalOperand namedLocalOp)
                        {
                            throw new MwNotSupportedException(
                                "wildcard .proxy.call 环具名包操作数非局部（受控拒绝）: "
                                + ring.Symbol.Canonical);
                        }
                        var wName = "$mw.mw.w." + temp++;
                        ring.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper!)));
                        var callArgs = ProxyWildcardAbi.BuildInvokeArgs(nextRingFn!,
                            new MirLocalOperand(wName), ".name", null, namedLocalOp.Name,
                            null, null);
                        packInsts.Add(new MirCall(nextRingFn!.Symbol, callArgs, inner.Result,
                            inner.ExcTarget));
                        insts[i] = new MirGetWrapperMethodAddr(hostOp, member.Canonical,
                            nextWrapper!, wName);
                        insts.InsertRange(i + 1, packInsts);
                        i += packInsts.Count;
                        continue;
                    }

                    // 内一环 specific / 终态：具名包按名还原具体实参
                    //（$mw.named.lookup 按 Pair key 命中，VM
                    // UnboxNamedArgs 同口径；缺名补 null）
                    var unpacked = ProxyWildcardAbi.EmitUnpackNamedArgs(context, mir, ring,
                        packInsts, namedOp, original);
                    var nextReturn = nextKind == MwProxyMatchKind.Specific
                        ? nextRingFn!.ReturnType
                        : original.ReturnType;
                    List<MirOperand> args;
                    if (nextKind == MwProxyMatchKind.Specific)
                    {
                        var wName = "$mw.mw.w." + temp++;
                        ring.AddLocal(new MirLocal(wName, MirType.Of(nextWrapper!)));
                        packInsts.Add(new MirGetWrapperMethodAddr(hostOp, member.Canonical,
                            nextWrapper!, wName));
                        args = new List<MirOperand> { new MirLocalOperand(wName) };
                    }
                    else
                    {
                        args = new List<MirOperand> { hostOp };
                    }
                    args.AddRange(unpacked);
                    if (inner.Result == null)
                    {
                        packInsts.Add(new MirCall(nextCallee, args, null, inner.ExcTarget));
                    }
                    else if (nextReturn.IsVoid)
                    {
                        // 落点 void：环 ABI 以 .any 承载结果——补零值胖引用
                        packInsts.Add(new MirCall(nextCallee, args, null, inner.ExcTarget));
                        packInsts.Add(new MirLoadResource(
                            ProxyWildcardAbi.AddNullAnyResource(context), inner.Result));
                    }
                    else if (nextReturn.IsAnyOrObject)
                    {
                        packInsts.Add(new MirCall(nextCallee, args, inner.Result,
                            inner.ExcTarget));
                    }
                    else
                    {
                        var concrete = ProxyWildcardAbi.FreshLocal(ring, "$mw.mw.rt.",
                            nextReturn);
                        packInsts.Add(new MirCall(nextCallee, args, concrete, inner.ExcTarget));
                        ProxyWildcardAbi.EmitBoxToAny(context, packInsts,
                            new MirLocalOperand(concrete), nextReturn, inner.Result);
                    }
                    insts.RemoveAt(i);
                    insts.InsertRange(i, packInsts);
                    i += packInsts.Count - 1;
                }
            }
        }

        // ===== trampoline（原名槽替换体） =====

        // 最外环 specific：取最外层 wrapper 隐藏槽地址
        //（MirGetWrapperMethodAddr，刀3c 原地访问同口径）→ 调首环 →
        // 按原签名返回（void 方法的首环返回 .any 擦除形态，结果丢弃）
        private static MirFunction BuildSpecificTrampoline(MirFunction original,
            MwMemberSymbol member, MwMemberSymbol firstRing, string outerWrapper)
        {
            var fn = new MirFunction(original.Symbol, original.ReturnType, original.Parameters,
                new List<MirLocal>(original.Parameters), new List<MirBlock>(),
                original.IsEntrypoint);
            var wrapperLocal = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.w.",
                MirType.Of(outerWrapper));
            var insts = new List<MirInst>
            {
                new MirGetWrapperMethodAddr(new MirLocalOperand(".this"), member.Canonical,
                    outerWrapper, wrapperLocal),
            };
            var callArgs = new List<MirOperand> { new MirLocalOperand(wrapperLocal) };
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name == ".this"
                    || parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                callArgs.Add(new MirLocalOperand(parameter.Name));
            }
            string? result = null;
            if (!original.ReturnType.IsVoid)
            {
                result = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.r.", original.ReturnType);
            }
            insts.Add(new MirCall(firstRing, callArgs, result));
            fn.AddBlock(new MirBlock("entry", insts,
                new MirRet(result == null ? null : new MirLocalOperand(result))));
            return fn;
        }

        // 最外环 wildcard：取槽地址 → 打包（.name = 实现槽 canonical
        // 资源、全实参按 named Any 具名包）→ 调首环 → .any 结果拆回原
        // 返回类型（void 丢弃——VM 链末 void 方法同语义）
        private static MirFunction BuildWildcardTrampoline(MwContext context,
            MirFunction original, MwMemberSymbol member, MirFunction firstRing,
            string outerWrapper)
        {
            var fn = new MirFunction(original.Symbol, original.ReturnType, original.Parameters,
                new List<MirLocal>(original.Parameters), new List<MirBlock>(),
                original.IsEntrypoint);
            var wrapperLocal = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.w.",
                MirType.Of(outerWrapper));
            var insts = new List<MirInst>
            {
                new MirGetWrapperMethodAddr(new MirLocalOperand(".this"), member.Canonical,
                    outerWrapper, wrapperLocal),
            };
            var symbolLocal = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.sym.",
                ProxyWildcardAbi.StringType);
            insts.Add(new MirLoadResource(
                ProxyWildcardAbi.AddStringResource(context, member.Canonical), symbolLocal));
            var namedLocal = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.named.",
                ProxyWildcardAbi.NamedPackType);
            var args = new List<MirOperand>();
            var argTypes = new List<MirType>();
            var argNames = new List<string>();
            foreach (var parameter in original.Parameters)
            {
                if (parameter.Name == ".this"
                    || parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                args.Add(new MirLocalOperand(parameter.Name));
                argTypes.Add(parameter.Type);
                argNames.Add(parameter.Name);
            }
            ProxyWildcardAbi.EmitNamedPack(context, fn, insts, args, argTypes, argNames,
                namedLocal, "$mw.mw.pack");
            var callArgs = ProxyWildcardAbi.BuildInvokeArgs(firstRing,
                new MirLocalOperand(wrapperLocal), symbolLocal, null, namedLocal, null, null);
            if (original.ReturnType.IsVoid)
            {
                insts.Add(new MirCall(firstRing.Symbol, callArgs, null));
                fn.AddBlock(new MirBlock("entry", insts, new MirRet(null)));
                return fn;
            }
            var anyResult = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.rt.", ProxyWildcardAbi.AnyType);
            insts.Add(new MirCall(firstRing.Symbol, callArgs, anyResult));
            var result = ProxyWildcardAbi.FreshLocal(fn, "$mw.mw.r.", original.ReturnType);
            ProxyWildcardAbi.EmitUnboxFromAny(context, insts, new MirLocalOperand(anyResult),
                result, original.ReturnType);
            fn.AddBlock(new MirBlock("entry", insts, new MirRet(new MirLocalOperand(result))));
            return fn;
        }
    }
}
