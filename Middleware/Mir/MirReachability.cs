using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// 调用图可达性（MIR 构建的输入）：从入口 fn 出发沿 invoke 边收闭包，
    /// 产出 MirBuilder 的构建顺序。模块中不可达的 fn（如 bootstrap 预定义
    /// 符号的编译器合成体 toString——它们不进符号段，verifier 以硬编码环境
    /// 闭合）不建 MIR 也不进发射；这同时是模块级死代码消除。
    /// MW4 边规则：invoke 按 Binding 派发形态展开——直接调用到目标 fn；
    /// 虚调用到静态目标 + 全部 override 后代（vtable 完整性）；interface
    /// 调用到各实现类的段内实现；new type(X) 到匹配 init + ..init.wrapper
    /// + X 的全部 vtable 槽实现（消灭 null 槽，abstract 无体槽除外）；
    /// new.indirect 保守收模块内全部 init 族（含 stdlib core*）；
    /// fn(..super) 到解析后的基类实现。派发闭包经 IMwDispatchQuery 查询
    ///（Layout 实现；LayoutStage 排在 MirBuild 之前）。
    /// </summary>
    public static class MirReachability
    {
        // MW9b-G：恒可达白名单的 core 异常类型（stdlib core/exceptions.rg；
        // 抽象 Exception 无 init/getMessage 体，列入仅为成员扫描完备）。
        // MW12b：UndisposedResourceException（core/global_exceptions.rg）
        // 同族——entry stub 的 gexc drain 段经真 init 构造（发射期引用）
        private static readonly string[] CoreExceptionTypes =
        {
            "core::Exception",
            "core::RuntimeException",
            "core::IOException",
            "core::CastException",
            "core::NoSuchMethodException",
            "core::DividedByZeroException",
            "core::OutOfBoundException",
            "core::UndisposedResourceException",
        };

        // 可达 fn 的 canonical 序（入口优先，BFS 发现序）。
        // TentativeInitFamily = 仅经 new.indirect 保守边引入的 init 族，
        // MIR 构建失败时试探性跳过（其余 fn 仍响亮失败）。
        public static (IReadOnlyList<string> Order, IReadOnlySet<string> TentativeInitFamily)
            ResolveBuildOrder(MwContext context)
        {
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var order = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var tentative = new HashSet<string>(System.StringComparer.Ordinal);
            var queue = new Queue<(string Symbol, bool FromTentative)>();
            foreach (var bilFn in context.Module.Functions)
            {
                // 无符号段声明的 fn 是预定义符号的合成体（§9.1 verifier 以
                // 硬编码环境豁免），永不为入口；不可达则不建 MIR
                var member = context.Symbols.FindMember(bilFn.Symbol);
                if (member == null)
                {
                    continue;
                }
                if (member.HasKeyword(BilKeyword.Entrypoint)
                    // ..globals.init 不在任何 invoke 闭包内，但 rigi_entry
                    // stub 恒调用它（静态字段初值）：恒可达
                    || bilFn.Symbol.StartsWith("$..globals.init(", System.StringComparison.Ordinal))
                {
                    queue.Enqueue((bilFn.Symbol, false));
                }
            }
            // MW9b-G：core 异常类型的 init 族 + getMessage 恒可达——守卫点
            // 抛出（除零/cast/拆箱/new.indirect/越界写）是发射期引用，BIL
            // 级可达性收集看不到；顶层 reporter 的 getMessage 虚派发同理
            foreach (var typeCanonical in CoreExceptionTypes)
            {
                var type = context.Symbols.FindTypeByRef(typeCanonical);
                if (type == null)
                {
                    continue;
                }
                foreach (var member in type.Members)
                {
                    if (member.Declaration.Kind != BilMemberKind.Method)
                    {
                        continue;
                    }
                    if ((member.HasKeyword(BilKeyword.Init)
                            || IsInitFamilyName(member.Canonical)
                            || member.SignatureKey == "getMessage()")
                        && bySymbol.ContainsKey(member.Canonical))
                    {
                        queue.Enqueue((member.Canonical, false));
                    }
                }
            }
            // MW10 刀5：singleton 急切初始化（§8.7，VM InitializeSingletons
            // 同口径）——全部 singleton 类型的零参 init 与零参
            // ..init.wrapper 恒可达：rigi_entry 在 ..globals.init 之前
            // 逐个调 $.static.mw.singleton.get 合成 fn（首调即构造，走
            // 正常构造尾）；从不被 new 触达的 singleton（如只挂静态
            // Method wrapper 的 companion / 空闲 ..globals.host）也须
            // 可构造。get fn 本身是 MIR 期合成（无 BIL 体），无需入队
            foreach (var singletonCanonical in SingletonPlanner.Collect(context.Symbols))
            {
                var singletonType = context.Symbols.FindType(singletonCanonical);
                if (singletonType == null)
                {
                    continue;
                }
                foreach (var member in singletonType.Members)
                {
                    if (member.Declaration.Kind != BilMemberKind.Method)
                    {
                        continue;
                    }
                    var isZeroArgInit = member.HasKeyword(BilKeyword.Init)
                        && CanonicalSignature.Parse(member.Canonical).Parameters.Count == 0;
                    var isZeroArgWrapper = member.Canonical.EndsWith(
                        "$..init.wrapper()@.void", System.StringComparison.Ordinal);
                    if ((isZeroArgInit || isZeroArgWrapper)
                        && bySymbol.ContainsKey(member.Canonical))
                    {
                        queue.Enqueue((member.Canonical, false));
                    }
                }
            }
            // MW10：被应用 wrapper 的 proxy 模板 fn 自身不进 MIR（烘焙期
            // 特化后才以合成 fn 进发射），但模板体内的调用目标必须留在
            // 可达闭包——否则烘焙产物引用被剪枝的 fn（如 proxy 体内的
            // Console.println）会在发射期缺符号
            EnqueueProxyTemplateCallees(context, bySymbol, queue);
            // MW10 刀6：Method wrapper wildcard 环/trampoline 的具名包
            // 打包（Pair<String, Any> 逐项构造）引用 core::Pair 的 init
            // 族——无具名实参调用点的模块不会经普通边到达，按安装指令
            // 预入队
            EnqueueMethodWrapperPackSupport(context, bySymbol, queue);
            // MW10 刀6b：带 wildcard .proxy.call 的 Method wrapper 安装
            // 宿主——wildcard 环改写 .name 的 $.mw.mwr router 可直调宿主
            // extends 闭包任一可烘焙方法的原始体（含源码中未被直接调用
            // 的，VM RerouteWildcardInner/InvokeResolved 同口径可被路由
            // 到），其有体 fn 预入队
            EnqueueMethodWrapperRerouteHosts(context, bySymbol, queue);
            // MW10 刀3a：带 wildcard 方法/运算符 proxy（.proxy.* /
            // .proxy.opr.*）的 Entity 宿主——其全部可烘焙成员 fn 进可达
            // 闭包：wildcard 环 inner 重路由的 router 可直调任何成员的
            // 原始体（含源码中未被直接调用的，VM RerouteWildcardInner
            // 同口径可被路由到）
            EnqueueWildcardHostMembers(context, bySymbol, queue);
            // MW11c 棒5a：协程运行时段（§17.4 Rigi 世界）恒收编——
            // Dispatcher/Task 的桥方法由生成代码（split 产物 stub/
            // resume/DONE 尾）与 rigi_entry/导出符号引用，BIL 级可达性
            // 看不到这些边（与 MW9b-G 异常类型白名单同机制）。stdlib
            // 自身即含 async fn（Mutex.acquire 等），按内容门控无意义；
            // 无协程程序的 workerLoop 经 quiescent 先检直返，零挂起
            EnqueueCoroutineRuntime(context, bySymbol, queue);
            // MW12b §25.2：GlobalExceptionHandler 恒收编（有体方法全收，
            // 同协程运行时段粒度）——dispatch 由 rigi_entry stub 的 gexc
            // drain 段（生成代码）调用，register 体内 new List<...> 等边
            // 由 BFS 正常展开；BIL 级可达性看不到 stub 这条边
            EnqueueTypeMethods(context, bySymbol, queue, "core::GlobalExceptionHandler");
            while (queue.Count > 0)
            {
                var (symbol, fromTentative) = queue.Dequeue();
                if (!seen.Add(symbol))
                {
                    // 先经保守边入队、后经普通边到达：取消试探标记
                    if (!fromTentative)
                    {
                        tentative.Remove(symbol);
                    }
                    continue;
                }
                order.Add(symbol);
                if (fromTentative)
                {
                    tentative.Add(symbol);
                }
                var tentativeSink = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var edge in CallEdges(context, bySymbol[symbol], tentativeSink))
                {
                    if (bySymbol.ContainsKey(edge))
                    {
                        queue.Enqueue((edge, tentativeSink.Contains(edge)));
                    }
                }
            }
            return (order, tentative);
        }

        // MW11c 棒5a：协程运行时段类型清单（有体方法全收——成员自身的
        // 调用边由 BFS 正常展开）。Dispatcher 是 priv singleton（零参
        // init/wrapper 已由 singleton 急切初始化块收编）
        private static readonly string[] CoroutineRuntimeTypes =
        {
            "core.coroutine::Dispatcher",
            "core.coroutine::I64Queue",
            "core.coroutine::Task",
            "core.coroutine::Task<TReturn>",
            "core.coroutine::SleepAlarm",
        };

        private static void EnqueueCoroutineRuntime(MwContext context,
            Dictionary<string, BilFunction> bySymbol,
            Queue<(string Symbol, bool FromTentative)> queue)
        {
            foreach (var typeCanonical in CoroutineRuntimeTypes)
            {
                EnqueueTypeMethods(context, bySymbol, queue, typeCanonical);
            }
            // laneOfExecutor 模块级助手（Task executor setter/startCold
            // 调用——那些 fn 已在表内，边会随后展开；此处兜底防御；
            // 顶层 fn 在 GlobalMembers：core.coroutine::$laneOfExecutor）
            var laneHelper = context.Symbols.GlobalMembers.FirstOrDefault(m =>
                m.Canonical.StartsWith("core.coroutine::$laneOfExecutor(",
                    System.StringComparison.Ordinal));
            if (laneHelper != null && bySymbol.ContainsKey(laneHelper.Canonical))
            {
                queue.Enqueue((laneHelper.Canonical, false));
            }
        }

        // 类型全部有体方法入队（协程运行时段 / MW12b
        // GlobalExceptionHandler 共用的恒收编粒度；无 stdlib 的合成模块
        // 静默跳过）。Method + StaticMethod 双 kind（GlobalExceptionHandler
        // 全静态）
        private static void EnqueueTypeMethods(MwContext context,
            Dictionary<string, BilFunction> bySymbol,
            Queue<(string Symbol, bool FromTentative)> queue, string typeCanonical)
        {
            var type = context.Symbols.FindTypeByRef(typeCanonical);
            if (type == null)
            {
                return;   // 无 stdlib 的合成模块（单元测试形态）
            }
            foreach (var member in type.Members)
            {
                if (member.Declaration.Kind is BilMemberKind.Method
                        or BilMemberKind.StaticMethod
                    && bySymbol.ContainsKey(member.Canonical))
                {
                    queue.Enqueue((member.Canonical, false));
                }
            }
        }

        // 被应用 wrapper（类型/成员声明上 wrapped(W) 标记指向的类型，刀6
        // 起含 ..init.wrapper 体 new.wrapper.method 安装指令的 wrapper
        // 类型——BIL 方法声明不保留 wrapped 修饰符，Method wrapper 应用
        // 只存在于安装指令）的 proxy 模板 fn 调用边入队：模板自身不入队、
        // 不建 MIR
        private static void EnqueueProxyTemplateCallees(MwContext context,
            Dictionary<string, BilFunction> bySymbol,
            Queue<(string Symbol, bool FromTentative)> queue)
        {
            var applied = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                // 刀4：Entity 应用按继承闭包收集（§9.7 安装侧缝合同口径；
                // 合法前端 BIL 经 §14.9 重申后本类声明即全闭包）
                CollectWrappedClosure(context, type, applied);
                foreach (var member in type.Members)
                {
                    CollectWrappedRefs(member.Declaration.Modifiers, applied);
                }
            }
            foreach (var function in context.Module.Functions)
            {
                foreach (var block in function.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is NewWrapperMethodInstruction install)
                        {
                            applied.Add(MwTypeKey.Normalize(install.WrapperType.TypeRef));
                        }
                    }
                }
            }
            foreach (var wrapperRef in applied)
            {
                var wrapper = context.Symbols.FindTypeByRef(wrapperRef);
                if (wrapper == null)
                {
                    continue;
                }
                foreach (var member in wrapper.Members)
                {
                    var isProxy = false;
                    foreach (var modifier in member.Declaration.Modifiers)
                    {
                        if (modifier is BilWrapperProxyModifier)
                        {
                            isProxy = true;
                            break;
                        }
                    }
                    if (!isProxy || !bySymbol.TryGetValue(member.Canonical, out var template))
                    {
                        continue;
                    }
                    var tentativeSink = new HashSet<string>(System.StringComparer.Ordinal);
                    foreach (var edge in CallEdges(context, template, tentativeSink))
                    {
                        if (bySymbol.ContainsKey(edge))
                        {
                            queue.Enqueue((edge, tentativeSink.Contains(edge)));
                        }
                    }
                }
            }
        }

        private static void CollectWrappedRefs(IReadOnlyList<BilModifier> modifiers,
            HashSet<string> into)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    into.Add(MwTypeKey.Normalize(wrapped.WrapperTypeRef));
                }
            }
        }

        // 刀6：存在 wildcard .proxy.call（.name 首参形态）的 Method
        // wrapper 安装时，具名包打包引用的 core::Pair<String, Any> init
        // 族入队（trampoline/环链在 MIR 期合成，BIL 可达性看不到）
        private static void EnqueueMethodWrapperPackSupport(MwContext context,
            Dictionary<string, BilFunction> bySymbol,
            Queue<(string Symbol, bool FromTentative)> queue)
        {
            var needsPack = false;
            foreach (var function in context.Module.Functions)
            {
                foreach (var block in function.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is not NewWrapperMethodInstruction install)
                        {
                            continue;
                        }
                        var wrapper = context.Symbols.FindTypeByRef(install.WrapperType.TypeRef);
                        if (wrapper == null)
                        {
                            continue;
                        }
                        // wildcard 判定按 .name 首参（VM 口径；BIL kind
                        // 修饰符对 .proxy.call 恒为 specific）
                        var proxy = ProxyMatcher.FindProxy(wrapper, ".proxy.call",
                                BilProxyKind.Specific)
                            ?? ProxyMatcher.FindProxy(wrapper, ".proxy.call",
                                BilProxyKind.Wildcard);
                        if (proxy != null
                            && CanonicalSignature.Parse(proxy.Canonical).Parameters
                                is { Count: > 0 } ps
                            && ps[0].Name == ".name")
                        {
                            needsPack = true;
                        }
                    }
                }
            }
            if (!needsPack
                || context.Symbols.FindTypeByRef("core::Pair<.string, .any>") is not { } pair)
            {
                return;
            }
            foreach (var member in pair.Members)
            {
                if (member.HasKeyword(BilKeyword.Init)
                    && CanonicalSignature.Parse(member.Canonical).Parameters.Count == 2
                    && bySymbol.ContainsKey(member.Canonical))
                {
                    queue.Enqueue((member.Canonical, false));
                }
            }
            var initWrapper = pair.Canonical + "$..init.wrapper()@.void";
            if (bySymbol.ContainsKey(initWrapper))
            {
                queue.Enqueue((initWrapper, false));
            }
        }

        // 类型的 Entity wrapped(W) 继承闭包（本类 + extends 链祖先，环保护）
        private static void CollectWrappedClosure(MwContext context, MwTypeSymbol type,
            HashSet<string> into)
        {
            var guard = new HashSet<string>(System.StringComparer.Ordinal) { type.Canonical };
            CollectWrappedRefs(type.Declaration.Modifiers, into);
            var current = type;
            while (current.Declaration.ExtendsType is { } baseRef
                && context.Symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                && guard.Add(baseType.Canonical))
            {
                CollectWrappedRefs(baseType.Declaration.Modifiers, into);
                current = baseType;
            }
        }

        // wildcard Entity 宿主成员入队（刀3a）：宿主类型继承闭包（刀4：
        // 本类 + extends 链祖先）的 wrapped(W) 中任一 W 带 .proxy.*/.
        // proxy.opr.* wildcard proxy → 宿主全部可烘焙成员
        //（ProxyBakeSupport.IsBakeableHostMethod 同口径；Passes 层不
        // 反向引用，此处保留副本）的有体 fn 入队
        private static void EnqueueWildcardHostMembers(MwContext context,
            Dictionary<string, BilFunction> bySymbol,
            Queue<(string Symbol, bool FromTentative)> queue)
        {
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                var hasWildcardProxy = false;
                // 刀4：wrapper 应用按继承闭包判定（本类 + extends 链祖先）
                var applied = new HashSet<string>(System.StringComparer.Ordinal);
                CollectWrappedClosure(context, type, applied);
                foreach (var wrapperRef in applied)
                {
                    var wrapper = context.Symbols.FindTypeByRef(wrapperRef);
                    if (wrapper == null)
                    {
                        continue;
                    }
                    foreach (var wrapperMember in wrapper.Members)
                    {
                        var proxyName = ProxyMatcher.ProxyOperatorName(wrapperMember);
                        if (proxyName != ".proxy.*" && proxyName != ".proxy.opr.*")
                        {
                            continue;
                        }
                        foreach (var wm in wrapperMember.Declaration.Modifiers)
                        {
                            if (wm is BilWrapperProxyModifier { Kind: BilProxyKind.Wildcard })
                            {
                                hasWildcardProxy = true;
                                break;
                            }
                        }
                        if (hasWildcardProxy)
                        {
                            break;
                        }
                    }
                    if (hasWildcardProxy)
                    {
                        break;
                    }
                }
                if (!hasWildcardProxy)
                {
                    continue;
                }
                foreach (var member in type.Members)
                {
                    if (IsBakeableHostMember(member) && bySymbol.ContainsKey(member.Canonical))
                    {
                        queue.Enqueue((member.Canonical, false));
                    }
                }
            }
        }

        // MW10 刀6b：收集带 wildcard .proxy.call 的 Method wrapper 安装
        // 宿主（安装指令方法符号的 owner 类型），将其 extends 闭包全部
        // 可烘焙方法（MethodProxyBakingPass.IsBakeableTarget 同口径；
        // Passes 层不反向引用，此处保留副本）的有体 fn 入队——$.mw.mwr
        // router 的 if 链分支目标
        private static void EnqueueMethodWrapperRerouteHosts(MwContext context,
            Dictionary<string, BilFunction> bySymbol,
            Queue<(string Symbol, bool FromTentative)> queue)
        {
            var hosts = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var function in context.Module.Functions)
            {
                foreach (var block in function.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is not NewWrapperMethodInstruction install)
                        {
                            continue;
                        }
                        var wrapper = context.Symbols.FindTypeByRef(install.WrapperType.TypeRef);
                        if (wrapper == null)
                        {
                            continue;
                        }
                        // wildcard 判定按 .name 首参（VM 口径；BIL kind
                        // 修饰符对 .proxy.call 恒为 specific）
                        var proxy = ProxyMatcher.FindProxy(wrapper, ".proxy.call",
                                BilProxyKind.Specific)
                            ?? ProxyMatcher.FindProxy(wrapper, ".proxy.call",
                                BilProxyKind.Wildcard);
                        if (proxy == null
                            || CanonicalSignature.Parse(proxy.Canonical).Parameters
                                is not { Count: > 0 } ps
                            || ps[0].Name != ".name")
                        {
                            continue;
                        }
                        var member = context.Symbols.FindMember(install.Method.Symbol);
                        if (member?.Owner is { IsExternal: false } owner)
                        {
                            hosts.Add(owner.Canonical);
                        }
                    }
                }
            }
            foreach (var hostCanonical in hosts)
            {
                if (context.Symbols.FindType(hostCanonical) is not { } host)
                {
                    continue;
                }
                // extends 闭包（本类 + 祖先，环保护）
                var current = host;
                var guard = new HashSet<string>(System.StringComparer.Ordinal)
                {
                    host.Canonical,
                };
                while (true)
                {
                    foreach (var member in current.Members)
                    {
                        if (IsBakeableMethodWrapperTarget(member)
                            && bySymbol.ContainsKey(member.Canonical))
                        {
                            queue.Enqueue((member.Canonical, false));
                        }
                    }
                    if (current.Declaration.ExtendsType is not { } baseRef
                        || context.Symbols.FindTypeByRef(baseRef) is not
                            { IsExternal: false } baseType
                        || !guard.Add(baseType.Canonical))
                    {
                        break;
                    }
                    current = baseType;
                }
            }
        }

        // MethodProxyBakingPass.IsBakeableTarget 同口径（排除 init/ext/
        // static/..init/泛型成员与除 $$call 外的运算符；$$call 是
        // callable 协议入口，lambda 隐藏类方法符号在此放行）
        private static bool IsBakeableMethodWrapperTarget(MwMemberSymbol member)
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
            if (member.Canonical.IndexOf('<', System.StringComparison.Ordinal) >= 0)
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

        // ProxyBakeSupport.IsBakeableHostMethod 同口径（排除 init/ext/
        // static/..init/泛型运算符与 $$call；泛型方法自遗6起可经
        // wildcard 环烘焙，运算符可烘焙）
        private static bool IsBakeableHostMember(MwMemberSymbol member)
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
            if (member.IsOperatorMember
                && member.Canonical.IndexOf('<', System.StringComparison.Ordinal) >= 0)
            {
                return false;
            }
            return !member.Canonical.Contains("$$call(", System.StringComparison.Ordinal);
        }

        // 有参/零参 ..init.wrapper 边（VM TryFindInitWrapper 同口径：
        // 按名唯一）；无匹配不构成边（普通 new 在无参形态缺失时本就
        // 不调用；MirBuilder 构建期对确需者响亮失败）
        private static void AddInitWrapperEdge(MwContext context, string typeRef, int arity,
            List<string> edges)
        {
            if (context.Symbols.FindTypeByRef(typeRef) is not { } type)
            {
                return;
            }
            var wrapper = MirBuilder.FindInitWrapper(context.Symbols, type, arity);
            if (wrapper != null)
            {
                edges.Add(wrapper.Canonical);
            }
        }

        // fn 体内的全部可达边（BIL 的 block 平铺在 fn 级，无需递归遍历）
        private static IEnumerable<string> CallEdges(MwContext context, BilFunction fn,
            HashSet<string> tentativeSink)
        {
            var edges = new List<string>();
            // fn 局部类型表（super/new 的实参静态类型解析用）
            var localTypes = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var arg in fn.Args)
            {
                localTypes[arg.Name] = arg.TypeRef;
            }
            foreach (var varDecl in fn.Vars)
            {
                localTypes[varDecl.Name] = varDecl.TypeRef;
            }

            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    switch (inst)
                    {
                        case InvokeInstruction invoke:
                            AddCallEdges(context, fn, invoke.Method.Symbol,
                                ArgTypesOf(invoke.Arguments, localTypes), edges);
                            break;
                        case InvokeNoResultInstruction invokeNoResult:
                            AddCallEdges(context, fn, invokeNoResult.Method.Symbol,
                                ArgTypesOf(invokeNoResult.Arguments, localTypes), edges);
                            break;
                        case NewInstruction newInst:
                            AddNewEdges(context, newInst.Type.TypeRef, newInst.Arguments,
                                localTypes, edges);
                            break;
                        case NewWrappedInstruction newWrapped:
                            // new.wrapped（刀5）：init 实参匹配 init + 按
                            // wrapper 实参个数的有参 ..init.wrapper + vtable
                            // 槽闭包（与 NewInstruction 同族，wrapper 缝合
                            // fn 的边不可缺——companion init 体内构造 cell）
                            AddNewEdges(context, newWrapped.Type.TypeRef,
                                newWrapped.InitArguments, localTypes, edges);
                            AddInitWrapperEdge(context, newWrapped.Type.TypeRef,
                                newWrapped.WrapperArguments.Count, edges);
                            break;
                        case NewIndirectInstruction:
                            // 运行期目标不可静态知：保守把模块内全部类型的
                            // init 族收入可达闭包（用户 init / 默认合成 init /
                            // ..init.wrapper / ..init.field.*）；仅此边引入的
                            // 记入 tentativeSink，供 MIR 试探性跳过
                            var before = edges.Count;
                            AddAllInitFamilyEdges(context, edges);
                            for (var i = before; i < edges.Count; i++)
                            {
                                tentativeSink.Add(edges[i]);
                            }
                            break;
                        case NewCaseInstruction newCase:
                            AddNewEdges(context, newCase.Type.TypeRef, newCase.Arguments,
                                localTypes, edges);
                            break;
                        // L1：new.wrapped.case（§14.4.2）= new.case 边 +
                        // 有参 ..init.wrapper 边（与 NewWrappedInstruction
                        // 同族口径）
                        case NewWrappedCaseInstruction newWrappedCase:
                            AddNewEdges(context, newWrappedCase.Type.TypeRef,
                                newWrappedCase.CaseArguments, localTypes, edges);
                            AddInitWrapperEdge(context, newWrappedCase.Type.TypeRef,
                                newWrappedCase.WrapperArguments.Count, edges);
                            break;
                        case NewWrapperEntityInstruction newWrapper:
                            AddWrapperInstallEdges(context, newWrapper.WrapperType.TypeRef,
                                newWrapper.Arguments, localTypes, edges);
                            break;
                        case NewWrapperFieldInstruction newWrapperField:
                            AddWrapperInstallEdges(context, newWrapperField.WrapperType.TypeRef,
                                newWrapperField.Arguments, localTypes, edges);
                            break;
                        case NewWrapperMethodInstruction newWrapperMethod:
                            AddWrapperInstallEdges(context, newWrapperMethod.WrapperType.TypeRef,
                                newWrapperMethod.Arguments, localTypes, edges);
                            break;
                        case GetFieldInstruction getField:
                            AddAccessorEdge(context, fn, getField.Field.Symbol,
                                BilAccessorKind.Getter, edges);
                            break;
                        // L1：getid.field 的字段符号是 get/set.field.indirect
                        //（lowering 期静态解析）的字段来源——读写双向访问器
                        // 边保守并收（与直译 get/set.field 同口径）
                        case GetIdFieldInstruction getIdField:
                            AddAccessorEdge(context, fn, getIdField.Field.Symbol,
                                BilAccessorKind.Getter, edges);
                            AddAccessorEdge(context, fn, getIdField.Field.Symbol,
                                BilAccessorKind.Setter, edges);
                            break;
                        case SetFieldInstruction setField:
                            AddAccessorEdge(context, fn, setField.Field.Symbol,
                                BilAccessorKind.Setter, edges);
                            break;
                        case GetFieldStaticInstruction getStatic:
                            AddAccessorEdge(context, fn, getStatic.Field.Symbol,
                                BilAccessorKind.Getter, edges);
                            break;
                        case SetFieldStaticInstruction setStatic:
                            AddAccessorEdge(context, fn, setStatic.Field.Symbol,
                                BilAccessorKind.Setter, edges);
                            break;
                        case GetArrayInstruction getArray:
                            AddIndexOperatorEdge(context, localTypes, getArray.Array.Name,
                                isGet: true, edges);
                            break;
                        case SetArrayInstruction setArray:
                            AddIndexOperatorEdge(context, localTypes, setArray.Collection.Name,
                                isGet: false, edges);
                            break;
                        case InvokeIndirectInstruction invokeIndirect:
                            AddIndirectCallEdges(context, localTypes[invokeIndirect.CallTarget.Name],
                                ArgTypesOf(invokeIndirect.Arguments, localTypes),
                                localTypes[invokeIndirect.Target.Name], edges);
                            break;
                        case InvokeIndirectNoResultInstruction invokeIndirectNoResult:
                            AddIndirectCallEdges(context, localTypes[invokeIndirectNoResult.CallTarget.Name],
                                ArgTypesOf(invokeIndirectNoResult.Arguments, localTypes),
                                null, edges);
                            break;
                        // 遗1 用户运算符边：用户类型操作数的 intrinsic 经
                        // Binding 解析 operator fn，按宿主 Kind 展开（与
                        // invoke 同族口径；内建操作数族无边）
                        case BinaryIntrinsicInstruction binary:
                            AddUserOperatorEdges(context, binary.Op,
                                localTypes[binary.Left.Name], localTypes[binary.Right.Name],
                                edges);
                            break;
                        case UnaryIntrinsicInstruction unary:
                            AddUserOperatorEdges(context, unary.Op,
                                localTypes[unary.Operand.Name], edges);
                            break;
                        // MW11a：await/裸 yield 不产生调用边——操作数是
                        // 已有局部（Task 句柄/结果槽），无 invoke/new/
                        // 访问器目标；async fn 体经自身符号（split 后 =
                        // stub）可达，体内的调用边不变
                        case AwaitInstruction:
                            break;
                        // MW11b 棒3：带 Alarm 的 yield —— 运行时 Polling
                        // 探测回调是 MIR 期合成的 $mw.poll_probe（虚派发
                        // PollingAlarm.isReady），BIL 级不可见；须收编
                        // isReady 虚调用闭包（静态目标 + 全部 override
                        // 后代），否则用户子类的 isReady 实现无 MIR 可
                        // 发射。两分类 sheet（PollingAlarm/EventAlarm）
                        // 由 Layout 全类型计划覆盖，无需显式收编
                        case YieldInstruction yieldInst:
                            if (yieldInst.Alarm != null)
                            {
                                var isReady = context.Symbols.FindMember(
                                    Passes.CoroutineSplitPass.PollProbeIsReadyCanonical);
                                if (isReady != null
                                    && ImplBinder.BindCall(isReady) is VirtualCallBinding)
                                {
                                    AddVirtualEdges(context, isReady, edges);
                                }
                            }
                            break;
                    }
                }
            }
            return edges;
        }

        // 遗1 用户运算符边：解析不到（MIR 构建期会响亮失败）或内建
        // 操作数族时不构成边。G4：泛型占位左操作数（.generic< 形态）——
        // 静态不可解析，运行期按实际 typeid 派发到全部候选（与
        // GenericOpEmitter 的派发臂同一集合，ImplBinder 注释见）
        private static void AddUserOperatorEdges(MwContext context, BilBinaryOp op,
            string leftType, string rightType, List<string> edges)
        {
            if (ImplBinder.IsBuiltinBinaryOperand(leftType))
            {
                return;
            }
            if (leftType.Contains(".generic<", System.StringComparison.Ordinal))
            {
                foreach (var candidate in ImplBinder.CollectOperatorCandidates(
                    context.Symbols, ImplBinder.UserBinaryOperatorName(op)))
                {
                    AddOperatorMemberEdges(context, candidate, edges);
                }
                return;
            }
            var member = ImplBinder.FindUserBinaryOperator(context.Symbols, op,
                leftType, rightType);
            if (member != null)
            {
                AddOperatorMemberEdges(context, member, edges);
            }
        }

        private static void AddUserOperatorEdges(MwContext context, BilUnaryOp op,
            string operandType, List<string> edges)
        {
            if (ImplBinder.IsBuiltinUnaryOperand(operandType))
            {
                return;
            }
            if (operandType.Contains(".generic<", System.StringComparison.Ordinal))
            {
                foreach (var candidate in ImplBinder.CollectOperatorCandidates(
                    context.Symbols, ImplBinder.UserUnaryOperatorName(op)))
                {
                    AddOperatorMemberEdges(context, candidate, edges);
                }
                return;
            }
            var member = ImplBinder.FindUserUnaryOperator(context.Symbols, op, operandType);
            if (member != null)
            {
                AddOperatorMemberEdges(context, member, edges);
            }
        }

        private static void AddOperatorMemberEdges(MwContext context, MwMemberSymbol member,
            List<string> edges)
        {
            switch (member.Owner?.Declaration.Kind)
            {
                case BilTypeKind.Class:
                    AddVirtualEdges(context, member, edges);
                    break;
                case BilTypeKind.Interface:
                    AddInterfaceEdges(context, member, edges);
                    break;
                default:
                    edges.Add(member.Canonical);
                    break;
            }
        }

        // invoke 边：按 Binding 派发形态展开（native 面/外部声明不构成边）
        private static void AddCallEdges(MwContext context, BilFunction fn, string symbol,
            List<string> argTypes, List<string> edges)
        {
            if (symbol == BilSpellings.SuperReservedFunction)
            {
                edges.Add(MirBuilder.ResolveSuperCall(context, fn.Symbol, argTypes).Canonical);
                return;
            }
            // 目标不在符号段属预定义符号调用（MirBuilder 建 MIR 时受控拒绝）
            var member = context.Symbols.FindMember(symbol);
            if (member == null)
            {
                return;
            }
            switch (ImplBinder.BindCall(member))
            {
                case NativeDirectBinding:
                    break;
                case DirectCallBinding:
                    edges.Add(member.Canonical);
                    break;
                case VirtualCallBinding:
                    AddVirtualEdges(context, member, edges);
                    break;
                case InterfaceCallBinding:
                    AddInterfaceEdges(context, member, edges);
                    break;
            }
        }

        // invoke.indirect：按静态类型解析 $$call，再按宿主 Kind 走虚/接口闭包
        private static void AddIndirectCallEdges(MwContext context, string objectStaticType,
            List<string> argTypes, string? resultType, List<string> edges)
        {
            var binding = ImplBinder.BindIndirectCall(context.Symbols, objectStaticType,
                argTypes, resultType, context.Module.Functions);
            var callOperator = binding.CallOperator;
            if (callOperator.Owner == null)
            {
                return;
            }
            switch (callOperator.Owner.Declaration.Kind)
            {
                case BilTypeKind.Class:
                    AddVirtualEdges(context, callOperator, edges);
                    break;
                case BilTypeKind.Interface:
                    AddInterfaceEdges(context, callOperator, edges);
                    break;
            }
        }

        // 虚调用边：静态目标 + 全部 override 后代（同槽实现；vtable 完整性）
        private static void AddVirtualEdges(MwContext context, MwMemberSymbol target,
            List<string> edges)
        {
            edges.Add(target.Canonical);
            var query = context.DispatchQuery;
            var ownerSlots = query?.GetVTableSlots(target.Owner!.Canonical);
            if (ownerSlots == null)
            {
                return;
            }
            var slot = IndexOfSlot(ownerSlots, target.Canonical);
            if (slot < 0)
            {
                return;
            }
            foreach (var typeCanonical in query!.AllClassCanonicals())
            {
                if (!query.DerivesFrom(typeCanonical, target.Owner!.Canonical))
                {
                    continue;
                }
                var slots = query.GetVTableSlots(typeCanonical);
                if (slots != null)
                {
                    edges.Add(slots[slot]);
                }
            }
        }

        // interface 调用边：默认方法自身有 fn 体则入闭包；各实现类 iMap 段内实现
        private static void AddInterfaceEdges(MwContext context, MwMemberSymbol target,
            List<string> edges)
        {
            if (HasFunctionBody(context, target.Canonical))
            {
                edges.Add(target.Canonical);
            }
            var query = context.DispatchQuery;
            var ifaceSlots = query?.GetVTableSlots(target.Owner!.Canonical);
            if (ifaceSlots == null)
            {
                return;
            }
            var slot = IndexOfSlot(ifaceSlots, target.Canonical);
            if (slot < 0)
            {
                return;
            }
            foreach (var typeCanonical in query!.AllClassCanonicals())
            {
                var imap = query.GetIMap(typeCanonical);
                var slots = query.GetVTableSlots(typeCanonical);
                if (imap == null || slots == null)
                {
                    continue;
                }
                foreach (var (ifaceType, baseOffset) in imap)
                {
                    if (ifaceType == target.Owner!.Canonical
                        || context.Symbols.FindTypeByRef(ifaceType) == target.Owner)
                    {
                        edges.Add(slots[baseOffset + slot]);
                    }
                }
            }
        }

        // new.indirect 保守边：模块内全部类型的 init 族（含 stdlib core*）。
        // 运行期目标由 typeid 决定，静态无法收窄；宁可多留不可达 ctor。
        // 不可构建的 init 族由 MirBuilder 试探性跳过。
        private static void AddAllInitFamilyEdges(MwContext context, List<string> edges)
        {
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal
                    || type.Declaration.Kind is not (BilTypeKind.Class
                        or BilTypeKind.Struct or BilTypeKind.EnumStruct))
                {
                    continue;
                }
                foreach (var member in type.Members)
                {
                    if (member.Declaration.Kind != BilMemberKind.Method)
                    {
                        continue;
                    }
                    if (member.HasKeyword(BilKeyword.Init)
                        || IsInitFamilyName(member.Canonical))
                    {
                        edges.Add(member.Canonical);
                    }
                }
            }
        }

        private static bool IsInitFamilyName(string canonical)
        {
            var dollar = canonical.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            var rest = canonical.Substring(dollar + 1);
            return rest.StartsWith("..init.wrapper(", System.StringComparison.Ordinal)
                || rest.StartsWith(BilSpellings.InitFieldMethodPrefix, System.StringComparison.Ordinal);
        }

        // new.wrapper.*：wrapper 类型的 init + 其自身 ..init.wrapper
        //（嵌套安装）。宿主 ..init.wrapper 已由 AddNewEdges 收入。
        private static void AddWrapperInstallEdges(MwContext context, string wrapperTypeRef,
            IReadOnlyList<BilVariableOperand> arguments,
            Dictionary<string, string> localTypes, List<string> edges)
        {
            if (context.Symbols.FindTypeByRef(wrapperTypeRef) is not { } type)
            {
                return;
            }
            try
            {
                edges.Add(MirBuilder.ResolveInit(context.Symbols, type,
                    ArgTypesOf(arguments, localTypes), skipReceiver: 0,
                    constructedTypeRef: wrapperTypeRef).Canonical);
            }
            catch (MwNotSupportedException) when (arguments.Count == 0)
            {
            }
            var initWrapper = context.Symbols.FindMember(
                type.Canonical + "$..init.wrapper()@.void");
            if (initWrapper != null)
            {
                edges.Add(initWrapper.Canonical);
            }
        }

        // new type(X) 边：匹配 init + ..init.wrapper（+ class 的全部
        // vtable 槽实现——虚派发面闭包；abstract 无体槽在入队处按 fn 缺失
        // 过滤）；struct/enum 无 vtable，只到 init/init.wrapper
        private static void AddNewEdges(MwContext context, string typeRef,
            IReadOnlyList<BilVariableOperand> arguments,
            Dictionary<string, string> localTypes, List<string> edges)
        {
            if (context.Symbols.FindTypeByRef(typeRef) is not { } type
                || type.Declaration.Kind is not (BilTypeKind.Class
                    or BilTypeKind.Struct or BilTypeKind.EnumStruct))
            {
                // 不可解析/其他形态：MirBuilder 建 MIR 时受控拒绝，此处不构成边
                return;
            }
            try
            {
                edges.Add(MirBuilder.ResolveInit(context.Symbols, type,
                    ArgTypesOf(arguments, localTypes), skipReceiver: 0,
                    constructedTypeRef: typeRef).Canonical);
            }
            catch (MwNotSupportedException) when (arguments.Count == 0
                && SingletonPlanner.IsSingleton(type)
                && !SingletonPlanner.HasInitMember(type))
            {
                // 刀5：无 init 声明的 singleton（..globals.host 形态）——
                // 无 BIL 边（合成空 init 由 SingletonLoweringPass 落地
                // MIR，不占可达闭包）
            }
            catch (MwNotSupportedException) when (arguments.Count == 0
                && !SingletonPlanner.HasInitMember(type))
            {
                // 遗1/L7：无 init 声明 + 零实参（stdlib ComparisonResult
                // 形态 / 全链无 init 的子类零参 new）——无 init 边，仅
                // alloc + 可选 ..init.wrapper（VM TryFindInit 同口径）
            }
            var initWrapper = context.Symbols.FindMember(type.Canonical + "$..init.wrapper()@.void");
            if (initWrapper != null)
            {
                edges.Add(initWrapper.Canonical);
            }
            if (type.Declaration.Kind == BilTypeKind.Class)
            {
                var slots = context.DispatchQuery?.GetVTableSlots(MwTypeKey.Normalize(typeRef))
                    ?? context.DispatchQuery?.GetVTableSlots(type.Canonical);
                if (slots != null)
                {
                    edges.AddRange(slots);
                }
            }
        }

        // 字段访问边：字段带访问器时读/写经 getter/setter fn（VM
        // TryFindAccessor 同口径；excludingFn 传当前 fn，访问器体内对
        // 自身 backing 的直访不构成边）。实例访问器通常已被 vtable 槽
        // 覆盖，静态访问器无 vtable 兜底、必须靠此边
        private static void AddAccessorEdge(MwContext context, BilFunction fn,
            string fieldSymbol, BilAccessorKind kind, List<string> edges)
        {
            if (ImplBinder.FindAccessor(context.Symbols, fieldSymbol, kind,
                    fn.Symbol) is { } accessor)
            {
                edges.Add(accessor.Canonical);
            }
        }

        // 用户索引运算符边（内建 Array\<T\> 无边）
        private static void AddIndexOperatorEdge(MwContext context,
            Dictionary<string, string> localTypes, string collectionName, bool isGet,
            List<string> edges)
        {
            if (!localTypes.TryGetValue(collectionName, out var typeRef))
            {
                return;
            }
            var collectionType = MirType.Of(typeRef);
            if (Layout.TypeLayout.IsArray(collectionType))
            {
                return;
            }
            if (ImplBinder.FindIndexOperator(context.Symbols, typeRef, isGet) is { } method)
            {
                edges.Add(method.Canonical);
            }
        }

        private static bool HasFunctionBody(MwContext context, string symbol)
        {
            foreach (var function in context.Module.Functions)
            {
                if (function.Symbol == symbol)
                {
                    return true;
                }
            }
            return false;
        }

        private static int IndexOfSlot(IReadOnlyList<string> slots, string canonical)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] == canonical)
                {
                    return i;
                }
            }
            return -1;
        }

        private static List<string> ArgTypesOf(IReadOnlyList<BilVariableOperand> args,
            Dictionary<string, string> localTypes)
        {
            // 原始 BIL 类型引用（MirBuilder.ResolveInit 内部统一归一匹配）
            var types = new List<string>(args.Count);
            foreach (var arg in args)
            {
                types.Add(localTypes[arg.Name]);
            }
            return types;
        }
    }
}
