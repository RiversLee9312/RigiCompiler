using System.Collections.Generic;

namespace RigiCompiler.Bil.Vm
{
    // Wrapper 派发（执行期即时衔接参考实现，BIL_VM_DESIGN §1 定位 /
    // RUNTIME §14 / SYNTAX §14.3）：派发链烘焙归 Middleware，本 VM 作为
    // 行为参考在执行期即时衔接。三类目标：
    //  - Value wrapper（字段声明自身带 wrapped(W)）：.proxy.get / .proxy.set
    //    值读写（§14.3，get 内层先、set 外层先）；
    //  - Entity wrapper（类型声明带 wrapped(W)）：成员方法 / 字段 get/set /
    //    运算符按 specific→wildcard→跳过 建 outer→inner 链（§14.2）；
    //  - call??? 降级路由（§14.2/§14.3）：按 canonical symbol 判定类别，
    //    遍历 wrapper 链找能路由的 proxy，链末落回 NoSuchMethodException。
    // 派发上下文栈让 invoke fn(..inner) 在执行期解析「下一环」。

    // 派发类别
    internal enum VmWrapperDispatchKind
    {
        Get,       // 字段读（value / entity）
        Set,       // 字段写（value / entity）
        Method,    // 实体成员方法
        Operator,  // 实体运算符
        Call,      // Method wrapper 的 .proxy.call（实例级隐藏存储）
    }

    // 派发上下文帧：描述「当前执行到 wrapper 链的哪一环」。invoke fn(..inner)
    // 消费栈顶帧解析下一环（下一 wrapper 的 proxy；链末为原始字段读写 /
    // 原始 fn / NoSuchMethodException）。
    internal sealed class VmWrapperDispatchFrame
    {
        public VmWrapperDispatchKind Kind { get; }
        public VmValue Host { get; }                     // 宿主对象（receiver / cell / 实体实例）
        public IReadOnlyList<string> Wrappers { get; }   // outer→inner wrapper 类型列表
        public int Index { get; }                        // 当前 wrapper 下标（0 = 最外层）
        public bool ValueWrapper { get; }                // 字段自身带 wrapped 的 Value 应用
        public bool IsCall { get; }                      // call??? 降级（链末抛 NoSuchMethod）
        public string? FieldSymbol { get; }              // get/set 场景字段符号
        public string? ElementType { get; }              // get/set 场景字段元素类型
        public string? MethodSymbol { get; }             // method/operator/call 原始 fn 符号
        public string? MemberName { get; }               // 方法名 / 字段名 / 运算符名
        public string? SymbolText { get; }               // wildcard .name/symbol = 实际执行的实现槽
        public string? ReturnType { get; }               // call 场景被代理方法返回类型（.generic.TReturn）
        public bool CurrentRingWildcard { get; }         // 当前环是否 wildcard（解析 inner 实参）

        public VmWrapperDispatchFrame(VmWrapperDispatchKind kind, VmValue host,
            IReadOnlyList<string> wrappers, int index, bool valueWrapper, bool isCall,
            string? fieldSymbol, string? elementType, string? methodSymbol,
            string? memberName, string? symbolText, string? returnType,
            bool currentRingWildcard)
        {
            Kind = kind;
            Host = host;
            Wrappers = wrappers;
            Index = index;
            ValueWrapper = valueWrapper;
            IsCall = isCall;
            FieldSymbol = fieldSymbol;
            ElementType = elementType;
            MethodSymbol = methodSymbol;
            MemberName = memberName;
            SymbolText = symbolText;
            ReturnType = returnType;
            CurrentRingWildcard = currentRingWildcard;
        }

        // 推进到下一环后的同构帧（Index/环类别更新）
        public VmWrapperDispatchFrame Advance(int index, bool wildcard)
        {
            return new VmWrapperDispatchFrame(Kind, Host, Wrappers, index, ValueWrapper,
                IsCall, FieldSymbol, ElementType, MethodSymbol, MemberName, SymbolText,
                ReturnType, wildcard);
        }
    }

    internal static class VmWrapperDispatch
    {
        // 同步推进 proxy fn 时的临时结果槽（调用方帧内；value proxy 是同步
        // fn，逐环推进互不重叠，固定名即可——与 ProbePolling 临时推进同例）
        internal const string ResultSlot = ".wrapper.result";

        internal const string GetProxyName = ".proxy.get";
        internal const string SetProxyName = ".proxy.set";
        internal const string CallProxyName = ".proxy.call";

        // 具名包（fat ABI）元素类型与 core::Pair 字段符号（§7.1/§14.7：
        // 具名包 = .array<core::Pair<.string, .any>>，字段符号用声明级占位名）
        internal const string NamedPackType = "core::Pair<.string, .any>";
        internal const string NamedPairType = "core::Pair<.string, .any>";
        internal const string PairKeyField = "core::Pair#key@.generic<$.generic.TKey>";
        internal const string PairValueField = "core::Pair#value@.generic<$.generic.TValue>";

        // ==================== Value wrapper 值读写（字段自身 wrapped） ====================

        // get 链入口：raw → 内层 B.get → 外层 A.get → 结果。
        // proxy fn 为同步 fn，用 ProbePolling 同款的显式 Step 推进取结果
        // （返回值 false 表示 proxy 内异常已致协程进入终态，调用方不再写结果）。
        internal static bool ApplyGetChain(VmContext context, VmCoroutine coroutine,
            VmValue host, string fieldSymbol, string elementType,
            IReadOnlyList<string> wrappers, VmValue raw, out VmValue result)
        {
            result = raw;
            for (var i = wrappers.Count - 1; i >= 0; i--)
            {
                var wrapper = wrappers[i];
                var proxy = context.FindWrapperProxy(wrapper, GetProxyName);
                if (proxy == null)
                {
                    throw new VmException("wrapper " + wrapper + " 缺少 .proxy.get："
                        + fieldSymbol);
                }
                var function = context.FindFunction(proxy);
                if (function == null)
                {
                    throw new VmException("找不到 proxy 模板 fn：" + proxy);
                }
                var instance = ReadFieldInstance(host, fieldSymbol, wrapper);
                var args = BuildProxyArgs(function, instance, elementType, symbolText: null,
                    new[] { result }, namedArgs: null, unnamedArgs: null);
                if (!RunToResult(context, coroutine, function, args, out var next))
                {
                    return false;
                }
                result = next;
            }
            return true;
        }

        // set 链入口：最外层 wrapper 的 .proxy.set 先行，inner 逐环向内，
        // 链末落点为 setter（无则直写 backing）（§13.3 / §14.3 / §15.4）
        internal static void ApplySetChain(VmContext context, VmCoroutine coroutine,
            VmValue host, string fieldSymbol, string elementType,
            IReadOnlyList<string> wrappers, VmValue value)
        {
            ApplySetChainAt(context, coroutine, host, fieldSymbol, elementType,
                wrappers, 0, value);
        }

        // wrapped cell 的 getValue/setValue 派发：wrapper 链在访问器外侧。
        // 使用点 invoke 的是基类 core::Cell$getValue/setValue，须先虚派发
        // 到 ..cell.. 子类 override，再用子类 owner 拼 value 字段。
        internal static bool TryStartCellAccessorChain(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out var isStatic, out _, out _)
                || isStatic)
            {
                return false;
            }
            var name = VmContext.MethodNameOf(methodSymbol);
            if (name != "getValue" && name != "setValue")
            {
                return false;
            }
            if (args.Count == 0)
            {
                return false;
            }
            var resolved = context.ResolveDispatch(methodSymbol, args[0]);
            if (resolved == null
                || !BilVerificationContext.TryParseMethodSymbol(resolved.Symbol,
                    out var owner, out _, out var parameters, out var returnType)
                || !VmContext.IsCellTypeRef(owner))
            {
                return false;
            }
            var elementType = name == "getValue"
                ? returnType
                : (parameters.Count > 0 ? parameters[0].TypeRef : "");
            if (string.IsNullOrEmpty(elementType) || elementType == ".void")
            {
                return false;
            }
            var valueFieldSymbol = owner + "#value@" + elementType;
            var wrappers = context.CollectWrappedWrappers(valueFieldSymbol);
            if (wrappers.Count == 0)
            {
                return false;
            }
            if (name == "setValue")
            {
                if (args.Count < 2)
                {
                    return false;
                }
                ApplySetChain(context, coroutine, args[0], valueFieldSymbol, elementType,
                    wrappers, args[1]);
                return true;
            }
            var temp = coroutine.RegisterPendingGetChain(resultSlot, args[0], valueFieldSymbol,
                elementType, wrappers, Array.Empty<string>());
            // 链已外置，直调 getValue 体，避免再次拦截
            BilInvokeExecution.InvokeResolved(context, coroutine, methodSymbol, args, temp);
            return true;
        }

        // Set 链末的写入值：常规字段 set 链（MethodSymbol 为空）的 wildcard
        // 全形状 inner 末位值实参即 value（specific 只有 value 一个值实参，
        // 二者同形取末位）；访问器调用被 .proxy.* 拦截后重路由而来的 Set 链
        //（MethodSymbol 是 $.set. 访问器符号）inner 走胖值 ABI——末位是
        // unnamed 包，写入值是包内最后一个元素（setter 的唯一位置参数；
        // 空包防御落 null），且包内元素是 VmAny 装箱，须拆为载荷值
        //（同 UnboxConcreteArgs 口径；恒等路径不受影响）
        private static VmValue ValueOfSetInner(VmWrapperDispatchFrame frame,
            IReadOnlyList<VmValue> innerArgs)
        {
            var last = innerArgs.Count > 0 ? innerArgs[innerArgs.Count - 1] : VmNull.Instance;
            if (frame.MethodSymbol != null && last is VmArray pack)
            {
                if (pack.Length == 0)
                {
                    return VmNull.Instance;
                }
                var element = pack.GetAt(pack.Length - 1);
                return element is VmAny any ? any.Payload : element;
            }
            return last;
        }

        // 链末：先调 setter（含 cell setValue），否则直写 backing。
        // 链末落点必须绕过 wrapper 派发链（InvokeResolved）——链已经跑完，
        // setter 再经实体/方法链拦截会重入成环（同 cell setValue 的
        // 「跳过 TryStartCellAccessorChain」先例）；构造期起自 setter
        // 调用拦截的重路由链 FieldSymbol 可能恢复失败，给清晰 VmException
        private static void FinishSetToBacking(VmContext context, VmCoroutine coroutine,
            VmValue host, string? fieldSymbol, VmValue value, string? resultSlot)
        {
            if (fieldSymbol == null)
            {
                throw new VmException("set 链末缺少字段符号（访问器重路由未恢复）："
                    + host.TypeRef);
            }
            if (context.TryFindAccessorDirect(fieldSymbol, BilAccessorKind.Setter,
                    out var setter))
            {
                BilInvokeExecution.InvokeResolved(context, coroutine, setter,
                    new[] { host, value }, resultSlot);
                return;
            }
            if (BilVerificationContext.TryParseFieldSymbol(fieldSymbol,
                    out var owner, out _, out var fieldType)
                && VmContext.IsCellTypeRef(owner))
            {
                var setValue = owner + "$setValue(value:" + fieldType + ")@.void";
                if (context.FindFunction(setValue) != null)
                {
                    // 跳过 TryStartCellAccessorChain，以免链末再入 set 链
                    BilInvokeExecution.InvokeResolved(context, coroutine, setValue,
                        new[] { host, value }, resultSlot);
                    return;
                }
            }
            WriteRawField(host, fieldSymbol, value.Copy());
            if (resultSlot != null)
            {
                coroutine.WriteVar(resultSlot, VmVoid.Instance);
            }
        }

        // value wrapper set 链逐环推进：index 处 wrapper 的 .proxy.set 先行，
        // 链末（index 越界）写原始字段
        private static void ApplySetChainAt(VmContext context, VmCoroutine coroutine,
            VmValue host, string fieldSymbol, string elementType,
            IReadOnlyList<string> wrappers, int index, VmValue value)
        {
            if (index >= wrappers.Count)
            {
                FinishSetToBacking(context, coroutine, host, fieldSymbol, value,
                    resultSlot: null);
                return;
            }
            var wrapper = wrappers[index];
            var proxy = context.FindWrapperProxy(wrapper, SetProxyName);
            if (proxy == null)
            {
                throw new VmException("wrapper " + wrapper + " 缺少 .proxy.set：" + fieldSymbol);
            }
            var function = context.FindFunction(proxy);
            if (function == null)
            {
                throw new VmException("找不到 proxy 模板 fn：" + proxy);
            }
            var instance = ReadFieldInstance(host, fieldSymbol, wrapper);
            coroutine.WrapperDispatch.Push(new VmWrapperDispatchFrame(
                VmWrapperDispatchKind.Set, host, wrappers, index, valueWrapper: true,
                isCall: false, fieldSymbol, elementType, methodSymbol: null,
                memberName: null, symbolText: null, returnType: null,
                currentRingWildcard: false));
            var args = BuildProxyArgs(function, instance, elementType, symbolText: null,
                new[] { value }, namedArgs: null, unnamedArgs: null);
            BilInvokeExecution.InvokeValues(context, coroutine, proxy, args, resultSlot: null);
        }

        // ==================== Entity wrapper 字段 get/set（类型自身 wrapped） ====================

        // Entity get 链：raw → 内层 → 外层，每层 specific .proxy.get.<名> 否则
        // wildcard .proxy.get.*，都无跳过该层（§14.1 择一关系）
        internal static bool ApplyEntityGetChain(VmContext context, VmCoroutine coroutine,
            VmValue host, string fieldSymbol, string elementType,
            IReadOnlyList<string> wrappers, VmValue raw, out VmValue result)
        {
            result = raw;
            var memberName = VmContext.FieldSimpleName(fieldSymbol);
            for (var i = wrappers.Count - 1; i >= 0; i--)
            {
                var proxy = FindProxy(context, wrappers[i], VmWrapperDispatchKind.Get,
                    memberName, out var wildcard);
                if (proxy == null)
                {
                    continue;
                }
                var function = context.FindFunction(proxy);
                if (function == null)
                {
                    throw new VmException("找不到 proxy 模板 fn：" + proxy);
                }
                var instance = ReadEntityInstance(host, wrappers[i]);
                var args = BuildProxyArgs(function, instance, elementType,
                    wildcard ? fieldSymbol : null, new[] { result }, null, null);
                if (!RunToResult(context, coroutine, function, args, out var next))
                {
                    return false;
                }
                result = next;
            }
            return true;
        }

        // Entity set 链入口（异步）：外层先，inner 逐环向内，链末写原始字段。
        // 无任何 proxy 可路由时返回 false（调用方直接写原始字段）
        internal static bool TryStartEntitySetChain(VmContext context, VmCoroutine coroutine,
            VmValue host, string fieldSymbol, string elementType,
            IReadOnlyList<string> wrappers, VmValue value, string? resultSlot)
        {
            var memberName = VmContext.FieldSimpleName(fieldSymbol);
            var frame = new VmWrapperDispatchFrame(VmWrapperDispatchKind.Set, host, wrappers,
                0, valueWrapper: false, isCall: false, fieldSymbol, elementType,
                methodSymbol: null, memberName, symbolText: fieldSymbol,
                returnType: null, currentRingWildcard: false);
            return StartRing(context, coroutine, frame, new[] { value }, resultSlot);
        }

        // ==================== Entity wrapper 成员方法 / 运算符 ====================

        // 方法派发：invoke fn(Host$m...) 且 Host 类型带 wrapped 标记 → 建链。
        // 无 wrapper 或无 proxy 可路由时返回 false（调用方走普通 invoke）
        internal static bool TryStartMethodChain(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (args.Count == 0)
            {
                return false;
            }
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out var isStatic, out _, out _) || isStatic)
            {
                return false;
            }
            // §14.2 Entity wrapper 绑在实体实例上：子类声明的 wrapper 须拦截
            // 从基类继承的方法。方法符号 owner 是声明型，漏掉子类应用；
            // 取 receiver 实际类型（§14.9 重申后子类列表已含继承闭包）。
            var wrappers = context.CollectEntityWrappers(VmTypeOps.ActualType(args[0]));
            if (wrappers.Count == 0)
            {
                return false;
            }
            var concrete = new List<VmValue>(args.Count - 1);
            for (var i = 1; i < args.Count; i++)
            {
                concrete.Add(args[i]);
            }
            var memberName = VmContext.MethodNameOf(methodSymbol);
            // 构造期方法（init / ..init.wrapper）不绕 wrapper 链：对它们的调用
            // 是构造协议自身的环节（PushConstructorTail 在新 init 原则下先执行
            // 实际类型的 ..init.wrapper——含闭包 wrapper 安装与字段初值——再
            // 进入 init 链，§9.7/§14.2）；wrapper 链只拦普通成员调用
            if (memberName == "init" || memberName == BilSpellings.InitWrapperMethodName
                || memberName == BilSpellings.ToParcelMethodName
                || memberName == BilSpellings.FromParcelMethodName)
            {
                return false;
            }
            // 虚/接口派发：wildcard 的 symbol 与链末原始 fn 都用实现槽符号
            //（SYNTAX §14.4 / §14.8：.name / symbol = 实际执行的方法）
            var dispatchSymbol = context.ResolveDispatchSymbol(methodSymbol, args[0]);
            var frame = new VmWrapperDispatchFrame(VmWrapperDispatchKind.Method, args[0],
                wrappers, 0, valueWrapper: false, isCall: false, fieldSymbol: null,
                elementType: null, dispatchSymbol, memberName, symbolText: dispatchSymbol,
                returnType: null, currentRingWildcard: false);
            return StartRing(context, coroutine, frame, concrete, resultSlot);
        }

        // 运算符派发：用户 operator（BinaryIntrinsicInstruction 分派）命中带
        // wrapped 宿主 → .proxy.opr.<名> / .proxy.opr.*。无 wrapper 或无 proxy
        // 可路由时返回 false（调用方走普通 operator invoke）
        internal static bool TryStartOperatorChain(VmContext context, VmCoroutine coroutine,
            string operatorSymbol, VmValue receiver, IReadOnlyList<VmValue> rhsArgs,
            string? resultSlot)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(operatorSymbol,
                    out _, out var isStatic, out _, out _) || isStatic)
            {
                return false;
            }
            // 与方法链同口径：运算符也按 receiver 实际类型收集 Entity wrapper
            var wrappers = context.CollectEntityWrappers(VmTypeOps.ActualType(receiver));
            if (wrappers.Count == 0)
            {
                return false;
            }
            var memberName = VmContext.MethodNameOf(operatorSymbol);
            // 与方法链同口径：虚/接口派发下 symbol 填实现槽符号
            var dispatchSymbol = context.ResolveDispatchSymbol(operatorSymbol, receiver);
            var frame = new VmWrapperDispatchFrame(VmWrapperDispatchKind.Operator, receiver,
                wrappers, 0, valueWrapper: false, isCall: false, fieldSymbol: null,
                elementType: null, dispatchSymbol, memberName, symbolText: dispatchSymbol,
                returnType: null, currentRingWildcard: false);
            return StartRing(context, coroutine, frame, rhsArgs, resultSlot);
        }

        // ==================== Method wrapper 的 .proxy.call（§14.4） ====================

        // Method wrapper 安装是运行时事实（frontend 在宿主 ..init.wrapper 内发
        // new.wrapper.method，静态方法经 companion 单例）：receiver 实例的隐藏
        // 存储里装着 (methodSymbol, wrapperType) → wrapper 实例。此处按安装序
        // outer→inner 查该实例的 method 隐藏存储，命中带 .proxy.call 的 wrapper
        // 则建链执行（.this = wrapper 实例原地、实参 = 原实参、fn(..inner) =
        // 下一环、链末 = 原始方法 fn）。无 wrapper 或无 proxy 可路由返回 false。
        internal static bool TryStartMethodWrapperChain(VmContext context, VmCoroutine coroutine,
            string methodSymbol, IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (args.Count == 0)
            {
                return false;
            }
            // 仅实例普通方法（静态方法经 companion 实例方法后天然走此处；
            // 运算符走 .proxy.opr.*、构造期 init/..init.wrapper 不绕链）
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out var isStatic, out _, out var returnType) || isStatic)
            {
                return false;
            }
            var memberName = VmContext.MethodNameOf(methodSymbol);
            // 运算符形态（$$plus 等）走 .proxy.opr.* 链，不是 Method wrapper；
            // 但 $$call 是 callable 协议入口（lambda 隐藏类覆写的 operator call），
            // 其 Method wrapper 应用走 .proxy.call（SYNTAX §14.4）——invoke.indirect
            // 解析到隐藏类 $$call 后进入本拦截点，与普通实例方法共用同一套链。
            if (IsOperatorSymbol(methodSymbol) && memberName != "call")
            {
                return false;
            }
            if (memberName == "init" || memberName == BilSpellings.InitWrapperMethodName
                || memberName == BilSpellings.ToParcelMethodName
                || memberName == BilSpellings.FromParcelMethodName)
            {
                return false;
            }
            var receiver = args[0];
            // bug O1：虚/接口派发下 invoke 带的是静态符号（Base$work /
            // Work$work），而子类 override 按 §14.9 重复声明 wrapper 后
            // 安装键是实现侧 canonical 符号（Child$work）。先按 receiver
            // 实际类型做虚派发取实现槽符号，再以它为键收集 wrapper 链
            //（RUNTIME §7 + §14.9）；super 路径不经此处，保持绕过。
            var dispatchSymbol = context.ResolveDispatchSymbol(methodSymbol, receiver);
            var wrappers = CollectMethodWrappers(context, receiver, dispatchSymbol);
            if (wrappers.Count == 0)
            {
                return false;
            }
            var concrete = new List<VmValue>(args.Count - 1);
            for (var i = 1; i < args.Count; i++)
            {
                concrete.Add(args[i]);
            }
            // 帧内 MethodSymbol 与 SymbolText 都用实现槽符号：环内 wrapper
            // 实例原地读（HiddenMethodKey）、链末原始 fn 落点、wildcard
            // .name 三者同口径——.name = 实际执行的方法（SYNTAX §14.4），
            // 非虚时 ResolveDispatchSymbol 原样返回调用点符号，行为不变。
            // inner 重路由若改写 .name，则以用户传入符号为准（见
            // RerouteWildcardInner），不在此二次虚派发。
            var frame = new VmWrapperDispatchFrame(VmWrapperDispatchKind.Call, receiver,
                wrappers, 0, valueWrapper: false, isCall: false, fieldSymbol: null,
                elementType: null, dispatchSymbol, memberName, symbolText: dispatchSymbol,
                returnType, currentRingWildcard: false);
            return StartRing(context, coroutine, frame, concrete, resultSlot);
        }

        // 运算符符号判定：$$ 双美元（§5.2 canonical 形态）即 operator
        private static bool IsOperatorSymbol(string symbol)
        {
            var dollar = symbol.IndexOf('$');
            return dollar >= 0 && dollar + 1 < symbol.Length && symbol[dollar + 1] == '$';
        }

        // 收集 receiver 实例上已安装的 Method wrapper（§14.4：new.wrapper.method
        // 写入 HiddenMethodKey，安装序即 ..init.wrapper 内的声明序 outer→inner）。
        // 隐藏键前缀 .wrapper.method:<methodSymbol>:<wrapperType>；按写入序取 wrapper
        private static List<string> CollectMethodWrappers(VmContext context, VmValue receiver,
            string methodSymbol)
        {
            var result = new List<string>();
            if (!VmObject.TryAsHost(receiver, out var host))
            {
                return result;
            }
            var prefix = VmContext.HiddenMethodKey(methodSymbol, "");
            foreach (var key in host.HiddenKeysInOrder)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                var wrapperType = key.Substring(prefix.Length);
                if (wrapperType.Length > 0)
                {
                    result.Add(wrapperType);
                }
            }
            return result;
        }

        // ==================== call??? 降级路由（§14.2/§14.3） ====================

        // 判定 call??? 是否应路由到 wrapper 链；能路由返回 true（已发起链），
        // 否则返回 false（调用方落回方法 hook 默认抛 NoSuchMethodException）。
        // 实参序（BIL §15.6）：receiver(.any) + symbol(.string) + namedArgs + unnamedArgs
        internal static bool TryStartCallChain(VmContext context, VmCoroutine coroutine,
            IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (args.Count != 4 || args[1] is not VmString symbol
                || args[2] is not VmArray namedArgs || args[3] is not VmArray unnamedArgs)
            {
                return false;
            }
            var receiver = args[0] is VmAny any ? any.Payload : args[0];
            if (!TryParseCallSymbol(symbol.Value, out var kind, out var memberName))
            {
                return false;
            }
            var wrappers = context.CollectEntityWrappers(VmTypeOps.ActualType(receiver));
            if (wrappers.Count == 0)
            {
                return false;
            }
            // 检查是否存在能路由的 proxy（否则维持默认抛）
            var probeIndex = 0;
            if (NextRingProxy(context, ref probeIndex, wrappers, kind, memberName,
                    out _) == null)
            {
                return false;
            }
            var frame = new VmWrapperDispatchFrame(kind, receiver, wrappers, 0,
                valueWrapper: false, isCall: true, fieldSymbol: null, elementType: null,
                methodSymbol: null, memberName, symbolText: symbol.Value,
                returnType: null, currentRingWildcard: false);
            // call 链以胖值 ABI 起步：无具体实参，只有 named/unnamed 包
            return StartCallRing(context, coroutine, frame, namedArgs, unnamedArgs, resultSlot);
        }

        // ==================== invoke fn(..inner) 执行期解析 ====================

        // 消费栈顶派发帧，推进到下一环。value proxy 的 inner 只转发值实参；
        // 方法/运算符 wildcard 的 inner 转发 [typePacks..., 保留首参, 值包...]
        // （§15.4 可变泛型包前置，保留首参显式携带，值包随后）。
        // wildcard 环的下一环路由以 inner 显式传入的 BIL 符号为准（不是帧内原符号）。
        internal static void ResolveInner(VmContext context, VmCoroutine coroutine,
            IReadOnlyList<VmValue> args, string? resultSlot)
        {
            if (coroutine.WrapperDispatch.Count == 0)
            {
                throw new VmException("invoke fn(..inner) 只能出现在 wrapper 派发上下文内");
            }
            var frame = coroutine.WrapperDispatch.Pop();
            if (frame.ValueWrapper)
            {
                if (frame.Kind != VmWrapperDispatchKind.Set)
                {
                    throw new VmException("fn(..inner) 当前仅支持 value wrapper 的 set 派发");
                }
                if (args.Count != 1)
                {
                    throw new VmException("value proxy 的 fn(..inner) 需要恰好 1 个值实参");
                }
                ApplySetChainAt(context, coroutine, frame.Host, frame.FieldSymbol!,
                    frame.ElementType!, frame.Wrappers, frame.Index + 1, args[0]);
                if (resultSlot != null)
                {
                    coroutine.WriteVar(resultSlot, VmVoid.Instance);
                }
                return;
            }
            // wildcard 环：inner 全形状显式携带保留首参（symbol / .name）。
            // 消费它并按该 BIL 符号重新路由下一环，支持 wildcard 转发不同符号。
            if (frame.CurrentRingWildcard)
            {
                frame = RerouteWildcardInner(context, coroutine, frame, args);
            }
            AdvanceRing(context, coroutine, frame, frame.Index + 1, args, resultSlot);
        }

        // 解析 wildcard 环 inner 的显式保留首参，按解析结果返回改写路由信息后的
        // 同构帧。当前 fn（invoke fn(..inner) 调用者）就是 wildcard proxy 模板 fn；
        // 其实参序 = [泛型包前置..., 值实参按声明序]（BIL §15.4/#27⑦）。
        private static VmWrapperDispatchFrame RerouteWildcardInner(VmContext context,
            VmCoroutine coroutine, VmWrapperDispatchFrame frame, IReadOnlyList<VmValue> innerArgs)
        {
            var function = coroutine.CurrentFrame.Function;
            // 值实参数量：排除 .return/.this/.generic.* 后，剩余按声明序的参数
            // 就是 inner 显式值实参段（wildcard 首参为 symbol 或 .name）。
            var valueParameterCount = 0;
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return" || arg.Name == ".this"
                    || arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    continue;
                }
                valueParameterCount++;
            }
            if (valueParameterCount <= 0 || innerArgs.Count < valueParameterCount)
            {
                throw new VmException("wildcard proxy 的 fn(..inner) 实参形状不完整："
                    + function.Symbol);
            }
            var symbolIndex = innerArgs.Count - valueParameterCount;
            if (innerArgs[symbolIndex] is not VmString symbolValue)
            {
                throw new VmException("wildcard proxy 的 fn(..inner) 保留首参必须是 .string："
                    + function.Symbol);
            }
            // Get/Set wildcard 的保留首参是字段符号（Host#field@T），不是 call???
            // 的 canonical 调用符号；按字段简单名重路由，类别保持 Get/Set。
            if (frame.Kind is VmWrapperDispatchKind.Get or VmWrapperDispatchKind.Set)
            {
                var fieldMemberName = VmContext.FieldSimpleName(symbolValue.Value);
                if (fieldMemberName.Length == 0)
                {
                    throw new VmException("wildcard proxy 的 fn(..inner) 传入了不可解析的字段符号："
                        + symbolValue.Value);
                }
                return new VmWrapperDispatchFrame(frame.Kind, frame.Host, frame.Wrappers,
                    frame.Index, frame.ValueWrapper, frame.IsCall, frame.FieldSymbol,
                    frame.ElementType, null, fieldMemberName, symbolValue.Value,
                    frame.ReturnType, frame.CurrentRingWildcard);
            }
            if (!TryParseCallSymbol(symbolValue.Value, out var nextKind, out var memberName))
            {
                throw new VmException("wildcard proxy 的 fn(..inner) 传入了不可解析的 BIL 符号："
                    + symbolValue.Value);
            }
            // Method wrapper 的 .proxy.call 链类别保持 Call（下一环仍按 Method
            // wrapper 链查找 .proxy.call），但目标方法符号以传入 .name 为准。
            // 用户原样转发时 .name 已是实现槽符号；此处不二次虚派发，
            // 以便 wildcard 可改写 .name 重路由到不同方法。
            if (frame.Kind == VmWrapperDispatchKind.Call)
            {
                return new VmWrapperDispatchFrame(frame.Kind, frame.Host, frame.Wrappers,
                    frame.Index, frame.ValueWrapper, frame.IsCall, frame.FieldSymbol,
                    frame.ElementType, symbolValue.Value, memberName, symbolValue.Value,
                    frame.ReturnType, frame.CurrentRingWildcard);
            }
            // Entity wrapper 的 wildcard：按解析出的类别（普通方法 / $$ 运算符 /
            // $get / $set）重路由下一环。MethodSymbol 同步改为传入符号（链末
            // Method/Operator/Get 用）；Get/Set 链末以字段符号为准——链可能
            // 起自访问器方法调用的拦截（构造期 setter 调用等），FieldSymbol
            // 原本为空，从访问器符号反查恢复（反查不到则保持空，链末落点
            // 给清晰 VmException 而非裸 .NET 异常）
            var routedField = frame.FieldSymbol;
            if (routedField == null
                && nextKind is VmWrapperDispatchKind.Get or VmWrapperDispatchKind.Set
                && context.TryFindFieldForAccessor(symbolValue.Value, out var recovered))
            {
                routedField = recovered;
            }
            return new VmWrapperDispatchFrame(nextKind, frame.Host, frame.Wrappers,
                frame.Index, frame.ValueWrapper, frame.IsCall, routedField,
                frame.ElementType, symbolValue.Value, memberName, symbolValue.Value,
                frame.ReturnType, frame.CurrentRingWildcard);
        }

        // ==================== 异步链推进 ====================

        // 启动某环（index=0 或推进后）：找 proxy（跳过无 proxy 的 wrapper）并
        // invoke；链末落点见 FinishChain。返回是否发起（false = 无 proxy 可路由）
        private static bool StartRing(VmContext context, VmCoroutine coroutine,
            VmWrapperDispatchFrame frame, IReadOnlyList<VmValue> ringArgs, string? resultSlot)
        {
            var index = 0;
            var proxy = NextRingProxy(context, ref index, frame.Wrappers, frame.Kind,
                frame.MemberName, out var wildcard);
            if (proxy == null)
            {
                return false;
            }
            var function = context.FindFunction(proxy)
                ?? throw new VmException("找不到 proxy 模板 fn：" + proxy);
            var instance = ReadInstance(context, frame, frame.Wrappers[index]);
            var args = BuildRingInvokeArgs(context, frame, function, instance, ringArgs,
                ringIsWildcard: false, nextWildcard: wildcard);
            coroutine.WrapperDispatch.Push(frame.Advance(index, wildcard));
            BilInvokeExecution.InvokeValues(context, coroutine, proxy, args, resultSlot);
            return true;
        }

        // call 链启动：以胖值 ABI 起步（无具体实参）
        private static bool StartCallRing(VmContext context, VmCoroutine coroutine,
            VmWrapperDispatchFrame frame, VmArray namedArgs, VmArray unnamedArgs,
            string? resultSlot)
        {
            var index = 0;
            var proxy = NextRingProxy(context, ref index, frame.Wrappers, frame.Kind,
                frame.MemberName, out var wildcard);
            if (proxy == null)
            {
                return false;
            }
            var function = context.FindFunction(proxy)
                ?? throw new VmException("找不到 proxy 模板 fn：" + proxy);
            var instance = ReadEntityInstance(frame.Host, frame.Wrappers[index]);
            // call??? 胖值 ABI 的 wildcard proxy 若用 .generic.TReturn 参与 cast，
            // 必须绑定类型 id——call symbol 的返回段恒为 .any（§14.7）。
            var callReturnType = WildcardReturnType(frame) ?? ".any";
            var args = BuildProxyArgs(function, instance, callReturnType, frame.SymbolText,
                concreteArgs: null, namedArgs, unnamedArgs);
            coroutine.WrapperDispatch.Push(frame.Advance(index, wildcard));
            BilInvokeExecution.InvokeValues(context, coroutine, proxy, args, resultSlot);
            return true;
        }

        // 从 index 起推进下一环（含跳过无 proxy 的 wrapper），链末落 FinishChain
        private static void AdvanceRing(VmContext context, VmCoroutine coroutine,
            VmWrapperDispatchFrame frame, int index, IReadOnlyList<VmValue> innerArgs,
            string? resultSlot)
        {
            var proxy = NextRingProxy(context, ref index, frame.Wrappers, frame.Kind,
                frame.MemberName, out var wildcard);
            if (proxy == null)
            {
                FinishChain(context, coroutine, frame, innerArgs, resultSlot);
                return;
            }
            var function = context.FindFunction(proxy)
                ?? throw new VmException("找不到 proxy 模板 fn：" + proxy);
            var instance = ReadInstance(context, frame, frame.Wrappers[index]);
            var args = BuildRingInvokeArgs(context, frame, function, instance, innerArgs,
                ringIsWildcard: frame.CurrentRingWildcard, nextWildcard: wildcard);
            coroutine.WrapperDispatch.Push(frame.Advance(index, wildcard));
            BilInvokeExecution.InvokeValues(context, coroutine, proxy, args, resultSlot);
        }

        // 链末落点：set → 原始字段写；method → 原始 fn（仍可套 Method wrapper）；
        // operator/call → 原始 fn；get → getter 本体（访问器调用被 .proxy.*
        // 拦截后重路由而来）；call??? 降级 → 抛
        private static void FinishChain(VmContext context, VmCoroutine coroutine,
            VmWrapperDispatchFrame frame, IReadOnlyList<VmValue> innerArgs, string? resultSlot)
        {
            if (frame.IsCall)
            {
                throw context.NoSuchMethod(coroutine, "未路由的降级请求：" + frame.SymbolText);
            }
            switch (frame.Kind)
            {
                case VmWrapperDispatchKind.Set:
                    FinishSetToBacking(context, coroutine, frame.Host, frame.FieldSymbol,
                        ValueOfSetInner(frame, innerArgs), resultSlot);
                    return;
                case VmWrapperDispatchKind.Get:
                    // 访问器调用拦截的重路由（inner 符号 = $.get.<名>）：链末调
                    // getter 本体——必须绕过 wrapper 派发链（InvokeResolved），
                    // 否则 getter 调用被同一实体 wildcard 再拦截成环；读结果
                    // 由使用点 pending get chain 继续过 .proxy.get.* 层（§14.2）
                    BilInvokeExecution.InvokeResolved(context, coroutine, frame.MethodSymbol!,
                        new[] { frame.Host }, resultSlot);
                    return;
                case VmWrapperDispatchKind.Method:
                    // 实体方法链末：Method wrapper 作为更内层仍可再绕（.proxy.call）
                    var methodConcrete = ConcreteOfInner(innerArgs, frame.CurrentRingWildcard);
                    var methodCallArgs = new List<VmValue>(methodConcrete.Count + 1) { frame.Host };
                    methodCallArgs.AddRange(methodConcrete);
                    if (TryStartMethodWrapperChain(context, coroutine, frame.MethodSymbol!,
                            methodCallArgs, resultSlot))
                    {
                        return;
                    }
                    BilInvokeExecution.InvokeResolved(context, coroutine, frame.MethodSymbol!,
                        methodCallArgs, resultSlot);
                    return;
                case VmWrapperDispatchKind.Operator:
                    var concrete = ConcreteOfInner(innerArgs, frame.CurrentRingWildcard);
                    var callArgs = new List<VmValue>(concrete.Count + 1) { frame.Host };
                    callArgs.AddRange(concrete);
                    BilInvokeExecution.InvokeResolved(context, coroutine, frame.MethodSymbol!,
                        callArgs, resultSlot);
                    return;
                case VmWrapperDispatchKind.Call:
                    // Method wrapper 链末：wildcard 环 inner 转发具名包（§14.4），
                    // specific 环直传具体实参
                    var callConcrete = ConcreteOfCallInner(context, frame, innerArgs);
                    var callCallArgs = new List<VmValue>(callConcrete.Count + 1) { frame.Host };
                    callCallArgs.AddRange(callConcrete);
                    BilInvokeExecution.InvokeResolved(context, coroutine, frame.MethodSymbol!,
                        callCallArgs, resultSlot);
                    return;
                default:
                    throw new VmException("未知派发类别链末：" + frame.Kind);
            }
        }

        // 依据当前环类别解析下一环的实参（specific 具体实参 / wildcard 胖值 ABI）
        private static IReadOnlyList<VmValue> BuildRingInvokeArgs(VmContext context,
            VmWrapperDispatchFrame frame, BilFunction function, VmValue instance,
            IReadOnlyList<VmValue> innerArgs, bool ringIsWildcard, bool nextWildcard)
        {
            if (frame.Kind is VmWrapperDispatchKind.Get or VmWrapperDispatchKind.Set)
            {
                // wildcard 全形状 inner 的 value 是声明序最后一个值实参
                // （specific 只有 value 一个值实参，二者同形取末位；访问器
                // 调用重路由而来的 Set 链经 ValueOfSetInner 解胖值包）。
                var value = frame.Kind == VmWrapperDispatchKind.Set
                    ? ValueOfSetInner(frame, innerArgs)
                    : (innerArgs.Count > 0 ? innerArgs[innerArgs.Count - 1] : VmNull.Instance);
                return BuildProxyArgs(function, instance, frame.ElementType,
                    nextWildcard ? frame.FieldSymbol : null, new[] { value }, null, null);
            }
            // Method wrapper 的 .proxy.call（§14.4）：specific 直传具体实参；
            // wildcard 传 .name + 具名包（单值包 .kwargs.args，§14.4 canonical）
            if (frame.Kind == VmWrapperDispatchKind.Call)
            {
                if (ringIsWildcard)
                {
                    var kwargs = innerArgs.Count >= 1 ? innerArgs[innerArgs.Count - 1]
                        : EmptyNamedPack();
                    if (nextWildcard)
                    {
                        return BuildProxyArgs(function, instance, null, frame.SymbolText,
                            null, kwargs, null);
                    }
                    return BuildProxyArgs(function, instance, null, null,
                        UnboxNamedArgs(context, frame, kwargs), null, null);
                }
                if (nextWildcard)
                {
                    return BuildProxyArgs(function, instance, null, frame.SymbolText,
                        null, BoxNamedArgs(context, frame, innerArgs), null);
                }
                return BuildProxyArgs(function, instance, frame.ReturnType, null,
                    innerArgs, null, null);
            }
            // method / operator
            if (ringIsWildcard)
            {
                var kwargs = innerArgs.Count >= 2 ? innerArgs[innerArgs.Count - 2]
                    : EmptyNamedPack();
                var vargs = innerArgs.Count >= 1 ? innerArgs[innerArgs.Count - 1]
                    : EmptyUnnamedPack();
                if (nextWildcard)
                {
                    return BuildProxyArgs(function, instance,
                        WildcardReturnType(frame), frame.SymbolText,
                        null, kwargs, vargs);
                }
                return BuildProxyArgs(function, instance, null, null,
                    UnboxConcreteArgs(vargs), null, null);
            }
            var concrete = innerArgs;
            if (nextWildcard)
            {
                return BuildProxyArgs(function, instance,
                    WildcardReturnType(frame), frame.SymbolText,
                    null, EmptyNamedPack(), BoxConcreteArgs(concrete));
            }
            return BuildProxyArgs(function, instance, null, null, concrete, null, null);
        }

        // wildcard proxy 的 .generic.TReturn 是标量 typeid（不是包）。其绑定值
        // 来自当前派发帧的 canonical symbol 返回段：Entity 方法/运算符为原始
        // 成员返回类型；call??? 降级为 .any（§14.7/§14.8）。
        private static string? WildcardReturnType(VmWrapperDispatchFrame frame)
        {
            if (string.IsNullOrEmpty(frame.SymbolText))
            {
                return null;
            }
            return BilVerificationContext.TryParseMethodSymbol(frame.SymbolText,
                out _, out _, out _, out var returnType)
                ? returnType
                : null;
        }

        // 链末 method/operator 的具体实参还原（wildcard 环内 inner 传胖值包）
        private static IReadOnlyList<VmValue> ConcreteOfInner(IReadOnlyList<VmValue> innerArgs,
            bool ringWasWildcard)
        {
            if (!ringWasWildcard)
            {
                return innerArgs;
            }
            var vargs = innerArgs.Count >= 1 ? innerArgs[innerArgs.Count - 1] : VmNull.Instance;
            return UnboxConcreteArgs(vargs);
        }

        // Method wrapper 链末具体实参还原：wildcard 环 inner 只转发具名包
        // （.proxy.call canonical `.name, args: named Any...`，§14.4）
        private static IReadOnlyList<VmValue> ConcreteOfCallInner(VmContext context,
            VmWrapperDispatchFrame frame, IReadOnlyList<VmValue> innerArgs)
        {
            if (!frame.CurrentRingWildcard)
            {
                return innerArgs;
            }
            var kwargs = innerArgs.Count >= 1 ? innerArgs[innerArgs.Count - 1] : EmptyNamedPack();
            return UnboxNamedArgs(context, frame, kwargs);
        }

        // 具体实参 → 具名包（Pair<String, Any>，按方法形参名配对，§14.4 fat ABI）
        private static VmArray BoxNamedArgs(VmContext context, VmWrapperDispatchFrame frame,
            IReadOnlyList<VmValue> concrete)
        {
            var array = new VmArray(NamedPackType, concrete.Count, VmNull.Instance);
            if (!BilVerificationContext.TryParseMethodSymbol(frame.MethodSymbol!,
                    out _, out _, out var parameters, out _))
            {
                return array;
            }
            for (var i = 0; i < concrete.Count && i < parameters.Count; i++)
            {
                var pair = context.AllocateObject(NamedPairType);
                pair.WriteField(PairKeyField, new VmString(parameters[i].Name));
                pair.WriteField(PairValueField, new VmAny(concrete[i]));
                array.SetAt(i, pair);
            }
            return array;
        }

        // 具名包 → 具体实参（按方法形参名序取回；缺名补 null）
        private static List<VmValue> UnboxNamedArgs(VmContext context, VmWrapperDispatchFrame frame,
            VmValue namedPack)
        {
            var result = new List<VmValue>();
            if (!BilVerificationContext.TryParseMethodSymbol(frame.MethodSymbol!,
                    out _, out _, out var parameters, out _))
            {
                return result;
            }
            var byName = ReadNamedPairs(namedPack);
            foreach (var (name, _) in parameters)
            {
                result.Add(byName.TryGetValue(name, out var value) ? value : VmNull.Instance);
            }
            return result;
        }

        // 读具名包为 名 → 值 表（Pair 元素 key/value 解包，VmAny → Payload）
        private static Dictionary<string, VmValue> ReadNamedPairs(VmValue namedPack)
        {
            var map = new Dictionary<string, VmValue>(StringComparer.Ordinal);
            if (namedPack is not VmArray array)
            {
                return map;
            }
            for (var i = 0; i < array.Length; i++)
            {
                if (array.GetAt(i) is VmObject pair
                    && pair.TryReadField(PairKeyField, out var key) && key is VmString keyStr
                    && pair.TryReadField(PairValueField, out var value))
                {
                    map[keyStr.Value] = value is VmAny any ? any.Payload : value;
                }
            }
            return map;
        }

        // ==================== proxy 查找 ====================

        // 依类别找 wrapper 的 specific / wildcard proxy（§14.1 择一关系）
        private static string? FindProxy(VmContext context, string wrapper,
            VmWrapperDispatchKind kind, string? memberName, out bool isWildcard)
        {
            isWildcard = false;
            // Method wrapper 只有一个 .proxy.call（specific/wildcard 按 .name 首参
            // 区分，§14.4），不经 specific 名前缀命中
            if (kind == VmWrapperDispatchKind.Call)
            {
                var callProxy = context.FindWrapperProxy(wrapper, ".proxy.call");
                if (callProxy == null)
                {
                    return null;
                }
                var callFn = context.FindFunction(callProxy);
                if (callFn != null)
                {
                    foreach (var arg in callFn.Args)
                    {
                        if (arg.Name == ".name")
                        {
                            isWildcard = true;
                            break;
                        }
                    }
                }
                return callProxy;
            }
            var (specificPrefix, wildcardName) = kind switch
            {
                VmWrapperDispatchKind.Method => (".proxy.", ".proxy.*"),
                VmWrapperDispatchKind.Operator => (".proxy.opr.", ".proxy.opr.*"),
                VmWrapperDispatchKind.Get => (".proxy.get.", ".proxy.get.*"),
                VmWrapperDispatchKind.Set => (".proxy.set.", ".proxy.set.*"),
                _ => (".proxy.", ".proxy.*"),
            };
            if (memberName != null)
            {
                var specific = context.FindWrapperProxy(wrapper, specificPrefix + memberName);
                if (specific != null)
                {
                    return specific;
                }
            }
            var wildcard = context.FindWrapperProxy(wrapper, wildcardName);
            if (wildcard != null)
            {
                isWildcard = true;
                return wildcard;
            }
            return null;
        }

        // 从 index 起找下一环 proxy（跳过无 proxy 的 wrapper）；无则返回 null
        private static string? NextRingProxy(VmContext context, ref int index,
            IReadOnlyList<string> wrappers, VmWrapperDispatchKind kind, string? memberName,
            out bool isWildcard)
        {
            isWildcard = false;
            while (index < wrappers.Count)
            {
                var proxy = FindProxy(context, wrappers[index], kind, memberName,
                    out isWildcard);
                if (proxy != null)
                {
                    return proxy;
                }
                index++;
            }
            return null;
        }

        // ==================== 实参构建 ====================

        // 依 proxy fn 的 .args 声明逐名构建调用实参（对 PushFrame 的按序匹配）：
        // .this → 实例；.generic.* → typeid / 空泛型包；symbol → 字符串；
        // .kwargs./.vargs. → 值包；其余普通参数按序取具体实参
        private static List<VmValue> BuildProxyArgs(BilFunction function, VmValue instance,
            string? elementType, string? symbolText, IReadOnlyList<VmValue>? concreteArgs,
            VmValue? namedArgs, VmValue? unnamedArgs)
        {
            var args = new List<VmValue>();
            var concreteIndex = 0;
            foreach (var arg in function.Args)
            {
                if (arg.Name == ".return")
                {
                    continue;
                }
                if (arg.Name == ".this")
                {
                    args.Add(instance);
                    continue;
                }
                if (arg.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    args.Add(IsPackType(arg.TypeRef) || elementType == null
                        ? EmptyTypePack() : new VmTypeId(elementType));
                    continue;
                }
                if (arg.Name == "symbol")
                {
                    args.Add(new VmString(symbolText ?? ""));
                    continue;
                }
                if (arg.Name == ".name")
                {
                    // Method wrapper 的 .proxy.call wildcard 首参（§14.4 保留名）
                    args.Add(new VmString(symbolText ?? ""));
                    continue;
                }
                if (arg.Name.StartsWith(".kwargs.", System.StringComparison.Ordinal))
                {
                    args.Add(namedArgs ?? EmptyNamedPack());
                    continue;
                }
                if (arg.Name.StartsWith(".vargs.", System.StringComparison.Ordinal))
                {
                    args.Add(unnamedArgs ?? EmptyUnnamedPack());
                    continue;
                }
                args.Add(concreteArgs != null && concreteIndex < concreteArgs.Count
                    ? concreteArgs[concreteIndex++] : VmNull.Instance);
            }
            return args;
        }

        private static bool IsPackType(string typeRef)
        {
            return typeRef.StartsWith(".array<", System.StringComparison.Ordinal)
                || typeRef.StartsWith("core::Array<", System.StringComparison.Ordinal)
                || typeRef.StartsWith(".map<", System.StringComparison.Ordinal)
                || typeRef.StartsWith("core::Map<", System.StringComparison.Ordinal);
        }

        private static VmArray EmptyTypePack() => new VmArray(".typeid<.any>", 0, VmNull.Instance);

        private static VmArray EmptyNamedPack() =>
            new VmArray("core::Pair<.string, .any>", 0, VmNull.Instance);

        private static VmArray EmptyUnnamedPack() => new VmArray(".any", 0, VmNull.Instance);

        // 具体实参 → 胖值 ABI 位置包（装箱为 .any）
        private static VmArray BoxConcreteArgs(IReadOnlyList<VmValue> concrete)
        {
            var array = new VmArray(".any", concrete.Count, VmNull.Instance);
            for (var i = 0; i < concrete.Count; i++)
            {
                array.SetAt(i, new VmAny(concrete[i]));
            }
            return array;
        }

        // 胖值 ABI 位置包 → 具体实参（VmAny → Payload）
        private static List<VmValue> UnboxConcreteArgs(VmValue unnamed)
        {
            var result = new List<VmValue>();
            if (unnamed is VmArray array)
            {
                for (var i = 0; i < array.Length; i++)
                {
                    var element = array.GetAt(i);
                    result.Add(element is VmAny any ? any.Payload : element);
                }
            }
            return result;
        }

        // ==================== symbol 类别判定（§14.3） ====================

        // 解析 call??? 的 canonical symbol 类别：method / get / set / opr 与成员名。
        // getter/setter 无参数段（$[.static].get.名@T），运算符含 $$，其余普通方法
        private static bool TryParseCallSymbol(string symbol, out VmWrapperDispatchKind kind,
            out string memberName)
        {
            kind = VmWrapperDispatchKind.Method;
            memberName = "";
            var dollar = symbol.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            var rest = symbol.Substring(dollar + 1);
            if (rest.StartsWith("$$", System.StringComparison.Ordinal))
            {
                rest = rest.Substring(1);
                var name = RestName(rest);
                if (name.Length == 0)
                {
                    return false;
                }
                kind = VmWrapperDispatchKind.Operator;
                memberName = name;
                return true;
            }
            if (rest.StartsWith(".static.", System.StringComparison.Ordinal))
            {
                rest = rest.Substring(".static.".Length);
            }
            if (rest.StartsWith(".get.", System.StringComparison.Ordinal))
            {
                var name = RestName(rest.Substring(".get.".Length));
                if (name.Length == 0)
                {
                    return false;
                }
                kind = VmWrapperDispatchKind.Get;
                memberName = name;
                return true;
            }
            if (rest.StartsWith(".set.", System.StringComparison.Ordinal))
            {
                var name = RestName(rest.Substring(".set.".Length));
                if (name.Length == 0)
                {
                    return false;
                }
                kind = VmWrapperDispatchKind.Set;
                memberName = name;
                return true;
            }
            var methodName = RestName(rest);
            if (methodName.Length == 0)
            {
                return false;
            }
            kind = VmWrapperDispatchKind.Method;
            memberName = methodName;
            return true;
        }

        // 取方法名/字段名/运算符名段（到 '(' 或 '@' 为止）
        private static string RestName(string rest)
        {
            var end = rest.Length;
            var paren = rest.IndexOf('(');
            if (paren >= 0 && paren < end)
            {
                end = paren;
            }
            var at = rest.IndexOf('@');
            if (at >= 0 && at < end)
            {
                end = at;
            }
            return rest.Substring(0, end);
        }

        // ==================== 存储读写 ====================

        // Value wrapper 隐藏存储（字段应用）原地读取
        private static VmValue ReadFieldInstance(VmValue host, string fieldSymbol, string wrapper)
        {
            if (!VmObject.TryAsHost(host, out var fieldHost))
            {
                throw new VmException("wrapper 派发目标不是对象：" + host.TypeRef);
            }
            if (!fieldHost.TryReadHidden(VmContext.HiddenFieldKey(fieldSymbol, wrapper),
                    out var instance))
            {
                throw new VmException("字段没有 wrapper 实例：" + wrapper + " @ " + fieldSymbol);
            }
            return new VmWrapperReceiver(instance, host);
        }

        // Entity wrapper 隐藏存储（类型应用）原地读取
        private static VmValue ReadEntityInstance(VmValue host, string wrapper)
        {
            if (!VmObject.TryAsHost(host, out var fieldHost))
            {
                throw new VmException("wrapper 派发目标不是对象：" + host.TypeRef);
            }
            if (!fieldHost.TryReadHidden(VmContext.HiddenEntityKey(wrapper), out var instance))
            {
                throw new VmException("实体没有 wrapper 实例：" + wrapper + " @ " + host.TypeRef);
            }
            return new VmWrapperReceiver(instance, host);
        }

        // Method wrapper 隐藏存储（方法应用）原地读取（§14.4：HiddenMethodKey）
        private static VmValue ReadMethodInstance(VmValue host, string methodSymbol, string wrapper)
        {
            if (!VmObject.TryAsHost(host, out var fieldHost))
            {
                throw new VmException("wrapper 派发目标不是对象：" + host.TypeRef);
            }
            if (!fieldHost.TryReadHidden(VmContext.HiddenMethodKey(methodSymbol, wrapper),
                    out var instance))
            {
                throw new VmException("方法没有 wrapper 实例：" + wrapper + " @ " + methodSymbol);
            }
            return new VmWrapperReceiver(instance, host);
        }

        // 依派发类别读取当前环 wrapper 实例（Entity 应用 vs 方法应用）
        private static VmValue ReadInstance(VmContext context, VmWrapperDispatchFrame frame,
            string wrapper)
        {
            return frame.Kind == VmWrapperDispatchKind.Call
                ? ReadMethodInstance(frame.Host, frame.MethodSymbol!, wrapper)
                : ReadEntityInstance(frame.Host, wrapper);
        }

        private static void WriteRawField(VmValue host, string fieldSymbol, VmValue value)
        {
            if (!VmObject.TryAsHost(host, out var fieldHost))
            {
                throw new VmException("wrapper 派发目标不是对象：" + host.TypeRef);
            }
            fieldHost.WriteField(fieldSymbol, value);
        }

        // 同步执行一个有返回值的 proxy fn 并取结果（显式 Step 推进，与
        // ProbePolling 的临时推进同例——get proxy 是同步 fn，不 await/yield）
        private static bool RunToResult(VmContext context, VmCoroutine coroutine,
            BilFunction function, IReadOnlyList<VmValue> args, out VmValue result)
        {
            result = VmNull.Instance;
            var depth = coroutine.CallStack.Count;
            coroutine.PushFrame(function, args, ResultSlot);
            while (coroutine.CallStack.Count > depth && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(context);
            }
            if (coroutine.State != VmCoroutineState.Running)
            {
                return false;
            }
            result = coroutine.ReadVar(ResultSlot);
            return true;
        }
    }
}
