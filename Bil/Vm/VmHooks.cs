namespace RigiCompiler.Bil.Vm
{
    // §22.5 native hook 表（BIL_VM_DESIGN §7 / RUNTIME.md §26）：
    // rigi_rt print / printErr / toString / alloc_array / make_sleep_alarm；
    // call??? 留后续切片。表外 (lib, symbol) 拒绝执行。单次 print 调用加锁原子写入。

    public sealed class VmHooks
    {
        public delegate VmValue Hook(VmContext context, IReadOnlyList<VmValue> arguments);

        private readonly Dictionary<(string Library, string Symbol), Hook> _table =
            new Dictionary<(string, string), Hook>();

        public void Register(string library, string symbol, Hook hook)
        {
            _table[(library, symbol)] = hook;
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

        public static VmHooks CreateStandard()
        {
            var hooks = new VmHooks();
            hooks.Register("rigi_rt", "print", Print);
            hooks.Register("rigi_rt", "printErr", PrintErr);
            hooks.Register("rigi_rt", "toString", ToStringHook);
            hooks.Register("rigi_rt", "alloc_array", AllocArray);
            hooks.Register("rigi_rt", "make_sleep_alarm", MakeSleepAlarm);
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
