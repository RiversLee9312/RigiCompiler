namespace RigiCompiler.Bil.Vm
{
    // §22.5 native hook 表（BIL_VM_DESIGN §7 / RUNTIME.md §26）：
    // (lib, symbol) 表：rigi_rt print / printErr / toString / alloc_array /
    // make_sleep_alarm，表外拒绝执行；单次 print 调用加锁原子写入。
    // 方法 hook 表：core::Any$call??? 按方法符号命中（无 (lib, symbol) 对）。

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
            IReadOnlyList<VmValue> arguments)
        {
            if (!_table.TryGetValue((library, symbol), out var hook))
            {
                throw new VmNativeHookException(library, symbol);
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
            hooks.Register("rigi_rt", "toString", ToStringHook);
            hooks.Register("rigi_rt", "alloc_array", AllocArray);
            hooks.Register("rigi_rt", "make_sleep_alarm", MakeSleepAlarm);
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

        private static VmValue ToStringHook(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1)
            {
                throw new VmException("toString 需要恰好 1 个参数");
            }
            return new VmString(arguments[0].ToStandardText());
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
