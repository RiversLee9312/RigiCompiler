namespace RigiCompiler.Bil.Vm
{
    // §22.5 native hook 表（BIL_VM_DESIGN §7 / RUNTIME.md §26）：
    // (lib, symbol) 表：rigi_rt print / printErr / any_to_string / alloc_array /
    // span_alloc / make_sleep_alarm / i64|u64|f32|f64|bool|char_to_string，表外拒绝执行；单次 print
    // 调用加锁原子写入。
    // 方法 hook 表：core::Any$call??? 按方法符号命中（无 (lib, symbol) 对）。
    // toString 机制（SYNTAX §3.8 修订）：Any/Object 的 toString 成员方法不再
    // 直接 hook——它们的编译器合成实现体调用 .bootstrap.rg 的 priv 全局
    // native any_to_string，hook 天然挂在实现上（override 经虚派发执行
    // 用户实现，不触达 hook）。

    public sealed class VmHooks
    {
        public delegate VmValue Hook(VmContext context, IReadOnlyList<VmValue> arguments);

        private readonly Dictionary<(string Library, string Symbol), Hook> _table =
            new Dictionary<(string, string), Hook>();
        private readonly Dictionary<string, Hook> _methodTable =
            new Dictionary<string, Hook>(StringComparer.Ordinal);

        public void Register(string library, string symbol, Hook hook)
        {
            _table[(library, symbol)] = hook;
        }

        // 方法 hook 按「宿主$方法名」（签名段之前）索引：call??? 的 namedArgs
        // 参数类型随 stdlib core::Pair 在否而变（NamedPackType 回退），不按全签名匹配
        public void RegisterMethod(string methodSymbol, Hook hook)
        {
            _methodTable[MethodKeyOf(methodSymbol)] = hook;
        }

        public VmValue Invoke(VmContext context, string library, string symbol,
            IReadOnlyList<VmValue> arguments, string? calleeSymbol = null)
        {
            if (!_table.TryGetValue((library, symbol), out var hook))
            {
                throw new VmNativeHookException(library, symbol);
            }
            // span_alloc 同一 rigi 面：Span / SharedSpan 由 callee 符号区分
            if (symbol == "span_alloc")
            {
                return AllocSpan(context, arguments, IsSharedSpanAlloc(calleeSymbol));
            }
            return hook(context, arguments);
        }

        public bool TryInvokeMethod(VmContext context, string methodSymbol,
            IReadOnlyList<VmValue> arguments, out VmValue result)
        {
            if (_methodTable.TryGetValue(MethodKeyOf(methodSymbol), out var hook))
            {
                result = hook(context, arguments);
                return true;
            }
            result = VmVoid.Instance;
            return false;
        }

        private static string MethodKeyOf(string methodSymbol)
        {
            var paren = methodSymbol.IndexOf('(');
            return paren < 0 ? methodSymbol : methodSymbol.Substring(0, paren);
        }

        public static VmHooks CreateStandard()
        {
            var hooks = new VmHooks();
            hooks.Register("rigi_rt", "print", Print);
            hooks.Register("rigi_rt", "printErr", PrintErr);
            hooks.Register("rigi_rt", "any_to_string", ToStringHook);
            hooks.Register("rigi_rt", "alloc_array", AllocArray);
            // span_alloc 与 shared_span_alloc 共用此键；Invoke 按 callee 分流
            hooks.Register("rigi_rt", "span_alloc",
                (ctx, args) => AllocSpan(ctx, args, shared: false));
            hooks.Register("rigi_rt", "make_sleep_alarm", MakeSleepAlarm);
            hooks.Register("rigi_rt", "i64_to_string", I64ToString);
            hooks.Register("rigi_rt", "u64_to_string", U64ToString);
            hooks.Register("rigi_rt", "f32_to_string", F32ToString);
            hooks.Register("rigi_rt", "f64_to_string", F64ToString);
            hooks.Register("rigi_rt", "bool_to_string", BoolToString);
            hooks.Register("rigi_rt", "char_to_string", CharToString);
            hooks.RegisterMethod("core::Any$call???", CallWildcard);
            return hooks;
        }

        private static VmValue Print(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            context.WriteStdout(RequireString("print", arguments));
            return VmVoid.Instance;
        }

        private static VmValue PrintErr(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            context.WriteStderr(RequireString("printErr", arguments));
            return VmVoid.Instance;
        }

        // to_string 面族：native 已对齐 .NET ToString(InvariantCulture)，直接复用 ToStandardText
        private static VmValue I64ToString(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmI64)
            {
                throw new VmException("i64_to_string 需要恰好 1 个 i64 参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        private static VmValue U64ToString(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmU64)
            {
                throw new VmException("u64_to_string 需要恰好 1 个 u64 参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        private static VmValue F32ToString(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmF32)
            {
                throw new VmException("f32_to_string 需要恰好 1 个 f32 参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        private static VmValue F64ToString(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmF64)
            {
                throw new VmException("f64_to_string 需要恰好 1 个 f64 参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        private static VmValue BoolToString(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmBool)
            {
                throw new VmException("bool_to_string 需要恰好 1 个 bool 参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        private static VmValue CharToString(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmChar)
            {
                throw new VmException("char_to_string 需要恰好 1 个 char 参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        // any_to_string（§3.8）：任意胖值取标准文本——基元标准文本、
        // 未覆写 toString 的对象为类型 canonical 名（覆写者不经此 hook）
        private static VmValue ToStringHook(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1)
            {
                throw new VmException("any_to_string 需要恰好 1 个参数");
            }
            return new VmString(arguments[0].ToStandardText());
        }

        // §22.5 span_alloc：签名与 alloc_array 对照——hidden typeid（.generic.T 物化）+ size。
        // 元素零值初始化（ZeroOf）；T 为 enum struct 按宿主错误（§14.3 无零值，与 alloc_array 同口径）。
        // struct 元素走 ZeroOf → AllocateObject，VM 支持。
        // shared_span_alloc 同 NativeSymbol，由 callee 符号分流 IsShared。
        private static bool IsSharedSpanAlloc(string? calleeSymbol)
        {
            return calleeSymbol != null
                && calleeSymbol.Contains("shared_span_alloc", StringComparison.Ordinal);
        }

        private static VmValue AllocSpan(VmContext context, IReadOnlyList<VmValue> arguments,
            bool shared)
        {
            if (arguments.Count != 2 || arguments[0] is not VmTypeId typeId)
            {
                throw new VmException("span_alloc 需要 typeid + size");
            }
            var size = VmContext.RequireIndex(arguments[1]);
            if (size < 0)
            {
                throw new VmException("Span 长度不能为负");
            }
            if (context.IsEnumStruct(typeId.TypeSymbol))
            {
                throw new VmException("enum struct 无零值，不能 span_alloc："
                    + typeId.TypeSymbol);
            }
            return new VmSpan(typeId.TypeSymbol, size, context.ZeroOf(typeId.TypeSymbol),
                shared);
        }

        // §22.5 alloc_array：hidden typeid（.generic.T 物化）+ size。
        // 元素零值初始化；T 为 enum struct 按宿主错误（§14.3 无零值）。
        private static VmValue AllocArray(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 2 || arguments[0] is not VmTypeId typeId)
            {
                throw new VmException("alloc_array 需要 typeid + size");
            }
            var size = VmContext.RequireIndex(arguments[1]);
            if (size < 0)
            {
                throw new VmException("数组长度不能为负");
            }
            if (context.IsEnumStruct(typeId.TypeSymbol))
            {
                throw new VmException("enum struct 无零值，不能 alloc_array："
                    + typeId.TypeSymbol);
            }
            return new VmArray(typeId.TypeSymbol, size, context.ZeroOf(typeId.TypeSymbol));
        }

        // §22.5 make_sleep_alarm：i64 毫秒 → 粘滞 EventAlarm（RUNTIME §19.4）。
        // Timer 到期 signal 并重新发布 waiter；复用 VmEventAlarm。
        private static VmValue MakeSleepAlarm(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmI64 milliseconds)
            {
                throw new VmException("make_sleep_alarm 需要恰好 1 个 i64 参数");
            }
            return VmEventAlarm.Sleep(milliseconds.Value);
        }

        // §22.5 方法 hook（RUNTIME §14.2 / SYNTAX §14.7）：wrapper 降级请求的
        // 链末默认实现。派发链烘焙（specific/wildcard 特化合成）归 Middleware，
        // 本 VM 的行为参考即「未路由 → 抛 core::NoSuchMethodException」。
        // 实参序（BIL §15.6）：receiver(.any) + symbol(.string) + namedArgs + unnamedArgs。
        private static VmValue CallWildcard(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 4
                || arguments[0] is not VmAny
                || arguments[1] is not VmString symbol
                || arguments[2] is not VmArray
                || arguments[3] is not VmArray)
            {
                throw new VmException("call??? 需要 (this: .any, symbol: .string,"
                    + " namedArgs: .array, unnamedArgs: .array)");
            }
            throw context.NoSuchMethod("未路由的降级请求：" + symbol.Value);
        }

        private static string RequireString(string hookName, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmString text)
            {
                throw new VmException(hookName + " 需要 .string 参数");
            }
            return text.Value;
        }
    }
}
