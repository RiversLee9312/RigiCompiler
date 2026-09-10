using System.Runtime.CompilerServices;

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
            // MW12b：global_exceptions.rg 的 printErr 声明用
            // @NativeSymbol("print_err)（rigi_ 直拼命中 shim.c 的
            // rigi_print_err；Console.rg 的 printErr 键保留），两键同实现
            hooks.Register("rigi_rt", "print_err", PrintErr);
            hooks.Register("rigi_rt", "any_to_string", ToStringHook);
            hooks.Register("rigi_rt", "any_hash", HashHook);
            // MW11d-D：对象身份原语（交接 §14 listener 身份键）

            hooks.Register("rigi_rt", "place_same_target", PlaceSameTarget);
            hooks.Register("rigi_rt", "handle_make", HandleMake);
            hooks.Register("rigi_rt", "handle_target", (ctx, args) =>
                new VmAny(HandleObject(args[0]).HandleTarget!));
            hooks.Register("rigi_rt", "handle_is_mutable", (ctx, args) =>
                new VmBool(HandleObject(args[0]).HandleMutable));
            hooks.Register("rigi_rt", "handle_kind", (ctx, args) =>
                new VmI32(HandleObject(args[0]).HandleKind));
            hooks.Register("rigi_rt", "handle_type_is_value", (ctx, args) =>
                new VmBool(ctx.IsValueType(((VmTypeId)args[0]).TypeSymbol)));
            hooks.Register("rigi_rt", "alloc_array", AllocArray);
            // span_alloc 与 shared_span_alloc 共用此键；Invoke 按 callee 分流
            hooks.Register("rigi_rt", "span_alloc",
                (ctx, args) => AllocSpan(ctx, args, shared: false));
            // MW11c 棒5a：make_sleep_alarm 面删除（sleep 迁 Rigi 实现——
            // SleepAlarm 构造经 rigi_timer_create 排程）；新增协程句柄
            // lane/当前协程三面（stdlib Dispatcher.publish/executor
            // setter/spawn-into 的 Rigi 体会调用）
            hooks.Register("rigi_rt", "coroutine_current",
                (ctx, args) => ctx.Dispatch.CoroutineCurrent(args));
            hooks.Register("rigi_rt", "coroutine_get_lane",
                (ctx, args) => ctx.Dispatch.CoroutineGetLane(args));
            hooks.Register("rigi_rt", "coroutine_set_lane",
                (ctx, args) => ctx.Dispatch.CoroutineSetLane(args));
            // MW11c 棒4a（RUNTIME §17.4）：native 原语面真实现，全部由
            // VmDispatch 承载（Worker 线程/sem 交接/协程句柄 resume/同步
            // Mutex/TLS/时钟；timer 回调语义归棒4b）。hook 键 =
            // @NativeSymbol 短名（C 符号 = rigi_ + 短名，与 rigi_rt
            // 导出一一对应）
            hooks.Register("rigi_rt", "worker_parallelism",
                (ctx, args) => new VmI32(VmDispatch.ComputeParallelism()));
            hooks.Register("rigi_rt", "worker_create",
                (ctx, args) => ctx.Dispatch.WorkerCreate(args));
            hooks.Register("rigi_rt", "worker_destroy",
                (ctx, args) => ctx.Dispatch.WorkerDestroy(args));
            hooks.Register("rigi_rt", "worker_enqueue",
                (ctx, args) => ctx.Dispatch.WorkerEnqueue(args));
            hooks.Register("rigi_rt", "worker_park",
                (ctx, args) => ctx.Dispatch.WorkerPark(args));
            hooks.Register("rigi_rt", "coroutine_create",
                (ctx, args) => ctx.Dispatch.CoroutineCreate(args));
            hooks.Register("rigi_rt", "coroutine_resume",
                (ctx, args) => ctx.Dispatch.CoroutineResume(args));
            hooks.Register("rigi_rt", "coroutine_destroy",
                (ctx, args) => ctx.Dispatch.CoroutineDestroy(args));
            hooks.Register("rigi_rt", "native_rc_retain",
                (ctx, args) => ctx.Dispatch.NativeRcRetain(args));
            hooks.Register("rigi_rt", "native_rc_release",
                (ctx, args) => ctx.Dispatch.NativeRcRelease(args));
            hooks.Register("rigi_rt", "timer_create",
                (ctx, args) => ctx.Dispatch.TimerCreate(args));
            hooks.Register("rigi_rt", "timer_cancel",
                (ctx, args) => ctx.Dispatch.TimerCancel(args));
            hooks.Register("rigi_rt", "timer_destroy",
                (ctx, args) => ctx.Dispatch.TimerDestroy(args));
            // L8：用户 EventAlarm 直继子类默认底座两面（stdlib
            // EventAlarm.ensureHandle/signal 的 native 声明；rigi_rt
            // worker.c 手动事件粘滞形态镜像，§19.3）
            hooks.Register("rigi_rt", "event_create_sticky",
                (ctx, args) => ctx.Dispatch.EventCreateSticky(args));
            hooks.Register("rigi_rt", "event_signal",
                (ctx, args) => ctx.Dispatch.EventSignal(args));
            hooks.Register("rigi_rt", "sync_mutex_create",
                (ctx, args) => ctx.Dispatch.SyncMutexCreate(args));
            hooks.Register("rigi_rt", "sync_mutex_acquire",
                (ctx, args) => ctx.Dispatch.SyncMutexAcquire(args));
            hooks.Register("rigi_rt", "sync_mutex_release",
                (ctx, args) => ctx.Dispatch.SyncMutexRelease(args));
            hooks.Register("rigi_rt", "tls_current_context",
                (ctx, args) => ctx.Dispatch.TlsCurrentContext(args));
            hooks.Register("rigi_rt", "time_now",
                (ctx, args) => ctx.Dispatch.TimeNow(args));
            hooks.Register("rigi_rt", "coro_local_push",
                (ctx, args) => ctx.Dispatch.CoroLocalPush(args));
            hooks.Register("rigi_rt", "coro_local_pop",
                (ctx, args) => ctx.Dispatch.CoroLocalPop(args));
            hooks.Register("rigi_rt", "coro_local_get",
                (ctx, args) => ctx.Dispatch.CoroLocalGet(args));
            hooks.Register("rigi_rt", "coro_local_inherit",
                (ctx, args) => ctx.Dispatch.CoroLocalInherit(args));
            // MW12b §25.2：GlobalExceptionHandler 注册表三面（rigi_rt
            // gexc.c 同语义镜像；VM 侧处理器本体直接持 VmValue——无共享
            // 安全闸门问题，注册表留在 hook 宿主上）。undisposed 事件
            // 通道见 VmDisposal.cs/VmObject 终结器 + BilVm.Run 收尾派发
            //（B2 已接：注册与 dispatch 在 VM 均真实生效）
            hooks.Register("rigi_rt", "gexc_register_handler",
                (ctx, args) => hooks.GexcRegisterHandler(args));
            hooks.Register("rigi_rt", "gexc_handler_count",
                (ctx, args) => hooks.GexcHandlerCount(args));
            hooks.Register("rigi_rt", "gexc_handler_at",
                (ctx, args) => hooks.GexcHandlerAt(args));
            // 注：core.time 与 core.coroutine 的 rigi_time_now 声明均显式
            // @NativeSymbol("time_now")（缺省名键 rigi_time_now 曾在此
            // 兜底注册，time.rg 补注解后为死键已删——§22.5 表外键拒绝）
            hooks.Register("rigi_rt", "i64_to_string", I64ToString);
            hooks.Register("rigi_rt", "u64_to_string", U64ToString);
            hooks.Register("rigi_rt", "f32_to_string", F32ToString);
            hooks.Register("rigi_rt", "f64_to_string", F64ToString);
            hooks.Register("rigi_rt", "bool_to_string", BoolToString);
            hooks.Register("rigi_rt", "char_to_string", CharToString);
            // MW11c 棒4b（§18.4）：冷 Task 启动壳——spawn-into/一次性
            // 判定/IllegalStateException 由 VmDispatch.StartCold 承载
            // （gate 临界区 + 引擎建协程，Rigi 体无法自举的部分）
            hooks.RegisterMethod("core.coroutine::Task$startCold",
                (ctx, args) => ctx.Dispatch.StartCold(args));
            hooks.RegisterMethod("core.coroutine::Task<TReturn>$startCold",
                (ctx, args) => ctx.Dispatch.StartCold(args));
            // MW11c 棒4b（§19.6）：Mutex 的挂起/唤醒引擎动作——判定逻辑
            // （tryEnter/releaseNext）留在 Rigi 层，hook 承载「判定 +
            // 挂起同临界区」与 waiter 发布
            hooks.RegisterMethod("core.coroutine::Mutex$enter",
                (ctx, args) => ctx.Dispatch.MutexEnter(args));
            hooks.RegisterMethod("core.coroutine::Mutex$release",
                (ctx, args) => ctx.Dispatch.MutexRelease(args));
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

        // any_hash（Map 键判等，用户裁定）：任意胖值取 i64 哈希——String 按
        // 内容（.NET string 哈希）、标量按值（数值取载荷、f64 按位型、bool/
        // char 按值）、enum case 按 case 符号；对象/其余引用值按身份
        //（RuntimeHelpers.GetHashCode）；null/装箱 null 固定 0。同一宿主内
        // 同值必同哈希（同内容 VmString 两实例相等、同一对象两次调用相等）；
        // VM 与 native 两宿主数值不要求一致
        private static VmValue HashHook(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1)
            {
                throw new VmException("any_hash 需要恰好 1 个参数");
            }
            var value = arguments[0] is VmAny any ? any.Payload : arguments[0];
            if (value is VmNullable nullable) value = nullable.Value is VmNull ? null : nullable.Value;
            var hash = value switch
            {
                null => 0L,
                VmNull => 0L,
                VmString text => text.Value.GetHashCode(),
                VmEnum single => single.CaseSymbol.GetHashCode(),
                VmI8 payload => payload.Value,
                VmI16 payload => payload.Value,
                VmI32 payload => payload.Value,
                VmI64 payload => payload.Value,
                VmU8 payload => payload.Value,
                VmU16 payload => payload.Value,
                VmU32 payload => payload.Value,
                VmU64 payload => unchecked((long)payload.Value),
                VmF32 payload => BitConverter.SingleToInt32Bits(payload.Value),
                VmF64 payload => BitConverter.DoubleToInt64Bits(payload.Value),
                VmBool payload => payload.Value ? 1L : 0L,
                VmChar payload => payload.Value,
                // 对象/数组/span 等引用值：宿主身份哈希（进程内稳定）
                _ => RuntimeHelpers.GetHashCode(value),
            };
            return new VmI64(hash);
        }

        private static VmValue PlaceSameTarget(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 2) throw new VmException("place_same_target 需要 2 个参数");
            var left = arguments[0] is VmAny leftAny ? leftAny.Payload : arguments[0];
            var right = arguments[1] is VmAny rightAny ? rightAny.Payload : arguments[1];
            return new VmBool(ReferenceEquals(left, right));
        }

        private static VmObject HandleObject(VmValue value)
        {
            if (value is VmAny any) value = any.Payload;
            if (value is not VmObject { TypeRef: ".handle", HandleTarget: not null } handle)
                throw new VmException("Handle 内部能力无效");
            return handle;
        }

        private static VmValue HandleMake(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 4 || arguments[0] is not VmTypeId { TypeSymbol: ".handle" }
                || arguments[2] is not VmI32 kind || arguments[3] is not VmBool mutable)
                throw new VmException("Handle 内部构造参数无效");
            var target = arguments[1] is VmAny any ? any.Payload : arguments[1];
            if (target is not VmObject { IsValueType: false } && target is not VmArray)
                throw new VmException("Handle 目标必须为对象或稳定 Cell");
            return new VmAny(new VmObject(".handle", false)
            {
                HandleTarget = target,
                HandleMutable = mutable.Value,
                HandleKind = kind.Value,
            });
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

        // §22.5 make_sleep_alarm 面已随 MW11c 棒5a 删除：sleep 迁 Rigi
        // 实现（VmEventAlarm 同步退役；sleep → SleepAlarm →
        // rigi_timer_create → VmTimerRecord 通道）

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

        // ===== MW12b：全局异常处理器注册表（rigi_rt gexc.c 镜像）=====
        // 注册序 = 下标序；Any 胖值原样持存（VM 对象引用即本体）
        private readonly List<VmValue> _gexcHandlers = new List<VmValue>();

        private VmValue GexcRegisterHandler(IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1)
            {
                throw new VmException("gexc_register_handler 需要恰好 1 个参数");
            }
            _gexcHandlers.Add(arguments[0]);
            return new VmI64(_gexcHandlers.Count - 1);
        }

        private VmValue GexcHandlerCount(IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 0)
            {
                throw new VmException("gexc_handler_count 不接受参数");
            }
            return new VmI64(_gexcHandlers.Count);
        }

        private VmValue GexcHandlerAt(IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmI64 index)
            {
                throw new VmException("gexc_handler_at 需要恰好 1 个 i64 参数");
            }
            if (index.Value < 0 || index.Value >= _gexcHandlers.Count)
            {
                throw new VmException("gexc_handler_at 下标越界：" + index.Value);
            }
            return _gexcHandlers[(int)index.Value];
        }
    }
}
