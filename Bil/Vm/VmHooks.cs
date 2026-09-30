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
            // 3b-β：asMutable 壳共享派生（同 HandleTarget 的新 capability，
            // mutable 置位；VM 无壳计数，共享语义由对象字段直接仿真）
            hooks.Register("rigi_rt", "handle_as_mutable", (ctx, args) =>
            {
                var src = HandleObject(args[0]);
                return new VmAny(new VmObject(".handle", false)
                {
                    HandleTarget = src.HandleTarget,
                    HandleMutable = true,
                    HandleKind = src.HandleKind,
                });
            });
            // 3b-β 壳释放消息两面对拍 stub（shell_msg_try_take /
            // shell_handle_release）已随 3b-δ2 壳释放属主化删除：
            // coroutine.rg 的消息面退场，VM 无壳计数通道（capability
            // 由 GC 托管），无对应 hook 键。
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
            // B2-4a：Span<u8> native ABI 验证原语（stdlib core.native 的
            // rigi_span_u8_echo / spanU8Echo 包装）——见 SpanU8Echo
            hooks.Register("rigi_rt", "span_u8_echo", SpanU8Echo);
            // 块 3-2：core.text 字节原语（stdlib core/text/text.rg 的
            // rigi_text_copy_out / rigi_text_from_bytes 原生面双宿主同
            // 语义）——见 TextCopyOut / TextFromBytes
            hooks.Register("rigi_rt", "text_copy_out", TextCopyOut);
            hooks.Register("rigi_rt", "text_from_bytes", TextFromBytes);
            // B2-4b1：标准流原语（stdlib core/io/stdstreams.rg 的
            // stdout_write/stderr_write/stdout_flush/stderr_flush 原生面
            // 双宿主同语义）——见 StdStreamWrite/StdStreamFlush
            hooks.Register("rigi_rt", "stdout_write",
                (ctx, args) => StdStreamWrite(ctx, args, error: false));
            hooks.Register("rigi_rt", "stderr_write",
                (ctx, args) => StdStreamWrite(ctx, args, error: true));
            hooks.Register("rigi_rt", "stdout_flush", StdStreamFlush);
            hooks.Register("rigi_rt", "stderr_flush", StdStreamFlush);
            // B2-4b2：标准输入异步读原语（stdlib core/io/stdstreams.rg 的
            // stdin_read_start/stdin_read_take 原生面双宿主同语义）——
            // 阻塞读卸载专用后台线程，完成经 event_signal 唤醒挂起协程
            hooks.Register("rigi_rt", "stdin_read_start",
                (ctx, args) => ctx.Dispatch.StdinReadStart(args));
            hooks.Register("rigi_rt", "stdin_read_take",
                (ctx, args) => ctx.Dispatch.StdinReadTake(args));
            // 施工块 7-2（STDLIB §4.5.6/§4.5.9，D5）：core.fs 文件原语
            // 层（stdlib core/fs/primitives.rg 的 fs_open/fs_read_start/
            // fs_read_take 原生面双宿主同语义）——fs_open 同步直调；
            // 挂起读卸载专用后台线程，完成经 event_signal 唤醒挂起协程。
            // 键 = @NativeSymbol 短名（C 导出 rigi_fs_*）
            hooks.Register("rigi_rt", "fs_open",
                (ctx, args) => ctx.Dispatch.FsOpen(args));
            hooks.Register("rigi_rt", "fs_read_start",
                (ctx, args) => ctx.Dispatch.FsReadStart(args));
            hooks.Register("rigi_rt", "fs_read_take",
                (ctx, args) => ctx.Dispatch.FsReadTake(args));
            // 阶段 2 原语族：挂起写/flush（read 同款两段式）+ 同步定位/
            // 长度/信息查询/创建删除/移动/目录枚举（C 侧 rigi_fs_* 镜像）
            hooks.Register("rigi_rt", "fs_write_start",
                (ctx, args) => ctx.Dispatch.FsWriteStart(args));
            hooks.Register("rigi_rt", "fs_write_take",
                (ctx, args) => ctx.Dispatch.FsWriteTake(args));
            hooks.Register("rigi_rt", "fs_flush_start",
                (ctx, args) => ctx.Dispatch.FsFlushStart(args));
            hooks.Register("rigi_rt", "fs_flush_take",
                (ctx, args) => ctx.Dispatch.FsFlushTake(args));
            hooks.Register("rigi_rt", "fs_seek",
                (ctx, args) => ctx.Dispatch.FsSeek(args));
            hooks.Register("rigi_rt", "fs_tell",
                (ctx, args) => ctx.Dispatch.FsTell(args));
            hooks.Register("rigi_rt", "fs_get_length",
                (ctx, args) => ctx.Dispatch.FsGetLength(args));
            hooks.Register("rigi_rt", "fs_set_length",
                (ctx, args) => ctx.Dispatch.FsSetLength(args));
            hooks.Register("rigi_rt", "fs_stat",
                (ctx, args) => ctx.Dispatch.FsStat(args));
            hooks.Register("rigi_rt", "fs_lstat",
                (ctx, args) => ctx.Dispatch.FsLstat(args));
            hooks.Register("rigi_rt", "fs_realpath",
                (ctx, args) => ctx.Dispatch.FsRealpath(args));
            hooks.Register("rigi_rt", "fs_mkdir",
                (ctx, args) => ctx.Dispatch.FsMkdir(args));
            hooks.Register("rigi_rt", "fs_rmdir",
                (ctx, args) => ctx.Dispatch.FsRmdir(args));
            hooks.Register("rigi_rt", "fs_unlink",
                (ctx, args) => ctx.Dispatch.FsUnlink(args));
            hooks.Register("rigi_rt", "fs_rename",
                (ctx, args) => ctx.Dispatch.FsRename(args));
            hooks.Register("rigi_rt", "fs_diropen",
                (ctx, args) => ctx.Dispatch.FsDirOpen(args));
            hooks.Register("rigi_rt", "fs_dirread",
                (ctx, args) => ctx.Dispatch.FsDirRead(args));
            // 施工块 7-6：fs_same_file 系统文件身份比较（复制自复制拒绝
            // 的判定面，§4.5.7；C 侧 rigi_fs_same_file 镜像）
            hooks.Register("rigi_rt", "fs_same_file",
                (ctx, args) => ctx.Dispatch.FsSameFile(args));
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
            // DateTime 专用单次墙钟采样；旧 time_now i64 毫秒键不变。
            hooks.Register("rigi_rt", "time_now_parts",
                (ctx, args) => ctx.Dispatch.TimeNowParts(args));
            // 施工块 6-3（§4.9.4）：单调时钟原语——VM/native 同语义
            // （排除整机睡眠；Windows QueryUnbiasedInterruptTimePrecise
            // / Linux CLOCK_MONOTONIC），VM 侧不用宿主 Stopwatch
            // 时间源（计入睡眠，语义不同）。键 = @NativeSymbol 值
            // monotonic_now_ns（C 导出 rigi_monotonic_now_ns）
            hooks.Register("rigi_rt", "monotonic_now_ns",
                (ctx, args) => ctx.Dispatch.MonotonicNow(args));
            // 施工块 7-1（STDLIB §4.5.2/§4.5.9，D5）：宿主平台判定私有
            // 原语——core.fs Path 平台路径词法校验内部使用（公共平台
            // 信息 API 继续后置）。键 = @NativeSymbol 值 host_is_windows
            //（C 导出 rigi_host_is_windows，native 侧 _WIN32 编译期判定；
            // VM 同进程宿主判定，两形态同语义）
            hooks.Register("rigi_rt", "host_is_windows",
                (ctx, args) => new VmBool(OperatingSystem.IsWindows()));
            hooks.Register("rigi_rt", "coro_local_push",
                (ctx, args) => ctx.Dispatch.CoroLocalPush(args));
            hooks.Register("rigi_rt", "coro_local_pop",
                (ctx, args) => ctx.Dispatch.CoroLocalPop(args));
            hooks.Register("rigi_rt", "coro_local_get",
                (ctx, args) => ctx.Dispatch.CoroLocalGet(args));
            hooks.Register("rigi_rt", "coro_local_inherit",
                (ctx, args) => ctx.Dispatch.CoroLocalInherit(args));
            // 施工块 6-4（STDLIB §4.11.1–§4.11.3 / D7）：core.math 数学
            // 原语族（rigi_rt math.c 的 rigi_math_* 导出镜像；键 =
            // @NativeSymbol 短名）。取整为 IEEE 确定运算（Math/MathF
            // 对应 floor/ceil/truncate/round 精确一致）；sqrt 正确舍入
            // （Math.Sqrt/MathF.Sqrt 双端 IEEE 保证，float 走 MathF 独立
            // 精度路径，不先 double 再截）；超越函数两端同为 ≤4 ULP 宿主
            // 实现，特殊值分类按 §4.11.3 独立语料对拍（math_basic.rg）。
            hooks.Register("rigi_rt", "math_floor_f32",
                (ctx, args) => new VmF32(MathF.Floor(RequireF32("math_floor_f32", args))));
            hooks.Register("rigi_rt", "math_floor_f64",
                (ctx, args) => new VmF64(Math.Floor(RequireF64("math_floor_f64", args))));
            hooks.Register("rigi_rt", "math_ceil_f32",
                (ctx, args) => new VmF32(MathF.Ceiling(RequireF32("math_ceil_f32", args))));
            hooks.Register("rigi_rt", "math_ceil_f64",
                (ctx, args) => new VmF64(Math.Ceiling(RequireF64("math_ceil_f64", args))));
            hooks.Register("rigi_rt", "math_trunc_f32",
                (ctx, args) => new VmF32(MathF.Truncate(RequireF32("math_trunc_f32", args))));
            hooks.Register("rigi_rt", "math_trunc_f64",
                (ctx, args) => new VmF64(Math.Truncate(RequireF64("math_trunc_f64", args))));
            hooks.Register("rigi_rt", "math_round_even_f32",
                (ctx, args) => new VmF32(MathF.Round(RequireF32("math_round_even_f32", args))));
            hooks.Register("rigi_rt", "math_round_even_f64",
                (ctx, args) => new VmF64(Math.Round(RequireF64("math_round_even_f64", args))));
            hooks.Register("rigi_rt", "math_round_away_f32",
                (ctx, args) => new VmF32(MathF.Round(RequireF32("math_round_away_f32", args),
                    MidpointRounding.AwayFromZero)));
            hooks.Register("rigi_rt", "math_round_away_f64",
                (ctx, args) => new VmF64(Math.Round(RequireF64("math_round_away_f64", args),
                    MidpointRounding.AwayFromZero)));
            // 施工块 6-4 阶段 3：sqrt/pow 与超越函数原语（rigi_rt math.c
            // 的 libm 调用镜像；C# Math/MathF 对应，float 全走 MathF 独立
            // 精度路径）。特殊值分类双端按 §4.11.3 独立语料对拍。
            hooks.Register("rigi_rt", "math_sqrt_f32",
                (ctx, args) => new VmF32(MathF.Sqrt(RequireF32("math_sqrt_f32", args))));
            hooks.Register("rigi_rt", "math_sqrt_f64",
                (ctx, args) => new VmF64(Math.Sqrt(RequireF64("math_sqrt_f64", args))));
            hooks.Register("rigi_rt", "math_pow_f32",
                (ctx, args) => new VmF32(PowF32(RequireF32("math_pow_f32", args, 0),
                    RequireF32("math_pow_f32", args, 1))));
            hooks.Register("rigi_rt", "math_pow_f64",
                (ctx, args) => new VmF64(PowF64(RequireF64("math_pow_f64", args, 0),
                    RequireF64("math_pow_f64", args, 1))));
            hooks.Register("rigi_rt", "math_exp_f32",
                (ctx, args) => new VmF32(MathF.Exp(RequireF32("math_exp_f32", args))));
            hooks.Register("rigi_rt", "math_exp_f64",
                (ctx, args) => new VmF64(Math.Exp(RequireF64("math_exp_f64", args))));
            hooks.Register("rigi_rt", "math_ln_f32",
                (ctx, args) => new VmF32(MathF.Log(RequireF32("math_ln_f32", args))));
            hooks.Register("rigi_rt", "math_ln_f64",
                (ctx, args) => new VmF64(Math.Log(RequireF64("math_ln_f64", args))));
            hooks.Register("rigi_rt", "math_log2_f32",
                (ctx, args) => new VmF32(MathF.Log2(RequireF32("math_log2_f32", args))));
            hooks.Register("rigi_rt", "math_log2_f64",
                (ctx, args) => new VmF64(Math.Log2(RequireF64("math_log2_f64", args))));
            hooks.Register("rigi_rt", "math_log10_f32",
                (ctx, args) => new VmF32(MathF.Log10(RequireF32("math_log10_f32", args))));
            hooks.Register("rigi_rt", "math_log10_f64",
                (ctx, args) => new VmF64(Math.Log10(RequireF64("math_log10_f64", args))));
            hooks.Register("rigi_rt", "math_sin_f32",
                (ctx, args) => new VmF32(MathF.Sin(RequireF32("math_sin_f32", args))));
            hooks.Register("rigi_rt", "math_sin_f64",
                (ctx, args) => new VmF64(Math.Sin(RequireF64("math_sin_f64", args))));
            hooks.Register("rigi_rt", "math_cos_f32",
                (ctx, args) => new VmF32(MathF.Cos(RequireF32("math_cos_f32", args))));
            hooks.Register("rigi_rt", "math_cos_f64",
                (ctx, args) => new VmF64(Math.Cos(RequireF64("math_cos_f64", args))));
            hooks.Register("rigi_rt", "math_tan_f32",
                (ctx, args) => new VmF32(MathF.Tan(RequireF32("math_tan_f32", args))));
            hooks.Register("rigi_rt", "math_tan_f64",
                (ctx, args) => new VmF64(Math.Tan(RequireF64("math_tan_f64", args))));
            hooks.Register("rigi_rt", "math_asin_f32",
                (ctx, args) => new VmF32(MathF.Asin(RequireF32("math_asin_f32", args))));
            hooks.Register("rigi_rt", "math_asin_f64",
                (ctx, args) => new VmF64(Math.Asin(RequireF64("math_asin_f64", args))));
            hooks.Register("rigi_rt", "math_acos_f32",
                (ctx, args) => new VmF32(MathF.Acos(RequireF32("math_acos_f32", args))));
            hooks.Register("rigi_rt", "math_acos_f64",
                (ctx, args) => new VmF64(Math.Acos(RequireF64("math_acos_f64", args))));
            hooks.Register("rigi_rt", "math_atan_f32",
                (ctx, args) => new VmF32(MathF.Atan(RequireF32("math_atan_f32", args))));
            hooks.Register("rigi_rt", "math_atan_f64",
                (ctx, args) => new VmF64(Math.Atan(RequireF64("math_atan_f64", args))));
            hooks.Register("rigi_rt", "math_atan2_f32",
                (ctx, args) => new VmF32(MathF.Atan2(RequireF32("math_atan2_f32", args, 0),
                    RequireF32("math_atan2_f32", args, 1))));
            hooks.Register("rigi_rt", "math_atan2_f64",
                (ctx, args) => new VmF64(Math.Atan2(RequireF64("math_atan2_f64", args, 0),
                    RequireF64("math_atan2_f64", args, 1))));
            // 施工块 6-5（STDLIB §4.11.4 / D7）：core.math Random 无参构造的
            // 系统随机材料原语（rigi_rt random.c 的 rigi_sys_random_u64 镜像；
            // 键 = @NativeSymbol 短名）。把 8 字节 CSPRNG 材料按小端顺序写入
            // 传入 Span<u8>[0..8)，返回 0；失败返回 -1（Rigi 包装层换抛
            // core.IOException，不静默回退）。VM/native 只要求材料质量，
            // 不要求两端同种子（无参构造本就无可复现性承诺）。
            hooks.Register("rigi_rt", "sys_random_u64", SysRandomU64);
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

        // FNV-1a 64（与 rigi_rt stringfmt.c rigi_fnv1a64 逐位同式）：
        // offset basis 14695981039346656037、prime 1099511628211
        private static long Fnv1a64(byte[] data)
        {
            var hash = 14695981039346656037UL;
            foreach (var b in data)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return unchecked((long)hash);
        }

        // 标量按值的 8 字节载荷（与 native tag0 打包同口径：小位宽零扩展、
        // f32 位型零扩展、f64 位型）后取 FNV-1a
        private static long HashPayload(ulong payload) =>
            Fnv1a64(BitConverter.GetBytes(payload));

        // any_hash（Map 键判等，用户裁定）：任意胖值取 i64 哈希——双宿主统一
        // FNV-1a 64（review-20260910 会话用户裁定，对齐 RUNTIME §3.8.1/
        // 08-resources-native 的 native 口径）：String 按内容（UTF-8 字节）、
        // 标量按值（8 字节载荷）、enum case 按 case 符号 UTF-8 字节；对象/
        // 其余引用值按身份（RuntimeHelpers.GetHashCode 的 8 字节再 FNV——身份
        // 值两宿主本就不可比）；null/装箱 null 固定 0。同一宿主内同值必同
        // 哈希（同内容 VmString 两实例相等、同一对象两次调用相等）
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
                VmString text => Fnv1a64(System.Text.Encoding.UTF8.GetBytes(text.Value)),
                VmEnum single => Fnv1a64(System.Text.Encoding.UTF8.GetBytes(single.CaseSymbol)),
                VmI8 payload => HashPayload((byte)payload.Value),
                VmI16 payload => HashPayload((ushort)payload.Value),
                VmI32 payload => HashPayload((uint)payload.Value),
                VmI64 payload => HashPayload(unchecked((ulong)payload.Value)),
                VmU8 payload => HashPayload(payload.Value),
                VmU16 payload => HashPayload(payload.Value),
                VmU32 payload => HashPayload(payload.Value),
                VmU64 payload => HashPayload(payload.Value),
                VmF32 payload => HashPayload((uint)BitConverter.SingleToInt32Bits(payload.Value)),
                VmF64 payload => HashPayload(unchecked((ulong)BitConverter.DoubleToInt64Bits(payload.Value))),
                VmBool payload => HashPayload(payload.Value ? 1UL : 0UL),
                VmChar payload => HashPayload(payload.Value),
                // 对象/数组/span 等引用值：宿主身份哈希（进程内稳定）
                _ => HashPayload(unchecked((ulong)RuntimeHelpers.GetHashCode(value))),
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

        // 施工块 6-5：sys_random_u64——系统随机材料原语 VM 侧（与 rigi_rt
        // random.c 的 rigi_sys_random_u64 双宿主同语义）：向
        // buffer[0..8) 写入 8 字节 CSPRNG 材料（RandomNumberGenerator，
        // 小端顺序与 C 侧 BCryptGenRandom/getrandom 的字节序一致），成功
        // 返回 0、失败返回 -1（Rigi 包装层换抛 core.IOException；两端只
        // 要求材料质量，不要求同种子）。形态不符抛 VmException。
        private static VmValue SysRandomU64(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1)
            {
                throw new VmException("sys_random_u64 需要 (Span<u8>)");
            }
            var value = arguments[0] is VmAny any ? any.Payload : arguments[0];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("sys_random_u64 第一参数必须是 Span<u8>");
            }
            if (span.Length < 8)
            {
                throw new VmException("sys_random_u64 缓冲区不足 8 字节");
            }
            var bytes = new byte[8];
            try
            {
                System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            }
            catch (Exception)
            {
                return new VmI32(-1);
            }
            for (var i = 0; i < 8; i++)
            {
                span.Elements[i] = new VmU8(bytes[i]);
            }
            return new VmI32(0);
        }

        // B2-4a：span_u8_echo——Span<u8> native ABI 的 VM 侧回声原语（与
        // rigi_rt span.c 的 rigi_span_u8_echo 双宿主同语义）：对
        // buffer[offset .. offset+count) 逐字节 XOR 0xFF 写回原位置，返回
        // 处理字节数。u8 Span 元素恒为 VmU8（VmAny 装箱形态防御性拆包）；
        // 区间越界抛 VmException（对拍只覆盖界内行为，native 侧诊断 abort）
        private static VmValue SpanU8Echo(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 3
                || arguments[1] is not VmI32 offset
                || arguments[2] is not VmI32 count)
            {
                throw new VmException("span_u8_echo 需要 (Span<u8>, i32, i32)");
            }
            var value = arguments[0] is VmAny any ? any.Payload : arguments[0];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("span_u8_echo 第一参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("span_u8_echo 区间越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            for (var i = 0; i < length; i++)
            {
                var element = span.Elements[start + i];
                if (element is VmAny elementAny) element = elementAny.Payload;
                if (element is not VmU8 byteValue)
                {
                    throw new VmException("span_u8_echo 元素必须是 u8");
                }
                span.Elements[start + i] = new VmU8((byte)(byteValue.Value ^ 0xFF));
            }
            return new VmI32(length);
        }

        // 块 3-2：text_copy_out——String 的 UTF-8 字节段拷出原语 VM 侧
        //（与 rigi_rt text.c 的 rigi_text_copy_out 双宿主同语义）：把
        // src[srcOffset .. srcOffset+count) 拷入 dest[destOffset..)。
        // 成功返回 count（0 合法）；源段越界返回 -1、目标段越界返回 -2
        //（约定错误码——Rigi 侧包装层换抛 core.OutOfBoundException，
        // C 宿主同返回码）。src 字节长按 UTF-8 求值（String.length 同
        // 口径，禁用宿主 UTF-16 码元数）。形态不符抛 VmException。
        private static VmValue TextCopyOut(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 5
                || arguments[1] is not VmI64 srcOffset
                || arguments[3] is not VmI32 destOffset
                || arguments[4] is not VmI32 count)
            {
                throw new VmException("text_copy_out 需要 (String, i64, Span<u8>, i32, i32)");
            }
            var source = arguments[0] is VmAny sourceAny ? sourceAny.Payload : arguments[0];
            if (source is not VmString text)
            {
                throw new VmException("text_copy_out 第一参数必须是 String");
            }
            var value = arguments[2] is VmAny destAny ? destAny.Payload : arguments[2];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("text_copy_out 第三参数必须是 Span<u8>");
            }
            var start = srcOffset.Value;
            var length = count.Value;
            var srcLength = (long)System.Text.Encoding.UTF8.GetByteCount(text.Value);
            // 源段校验：负值/求和越界（srcLength - start 不下溢：start ≥ 0、
            // srcLength ≥ 0，均为 long 运算）
            if (start < 0 || length < 0 || start > srcLength - length)
            {
                return new VmI32(-1);
            }
            if (destOffset.Value < 0 || destOffset.Value > span.Length - length)
            {
                return new VmI32(-2);
            }
            // 源串整体编一次 UTF-8（VmString 承载宿主 UTF-16 文本；字节
            // 偏移按 UTF-8 口径切片），逐字节写入 Span（元素恒为 VmU8）
            var bytes = System.Text.Encoding.UTF8.GetBytes(text.Value);
            for (var i = 0; i < length; i++)
            {
                span.Elements[destOffset.Value + i] = new VmU8(bytes[start + i]);
            }
            return new VmI32(length);
        }

        // 块 3-2：text_from_bytes——字节重建 String 原语 VM 侧（与
        // rigi_rt text.c 的 rigi_text_from_bytes 双宿主同语义）：对
        // bytes[offset .. offset+count) 做严格 UTF-8（WFF）校验后按
        // UTF-8 解码重建 String。校验失败抛可捕获的 core::OutOfBound-
        // Exception（切片通道保证边界合法，正常路径不可达；native 侧
        // 同规则为诊断 abort——C 宿主无法抛语言级异常）。形态不符抛
        // VmException。
        private static VmValue TextFromBytes(VmContext context, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 3
                || arguments[1] is not VmI32 offset
                || arguments[2] is not VmI32 count)
            {
                throw new VmException("text_from_bytes 需要 (Span<u8>, i32, i32)");
            }
            var value = arguments[0] is VmAny any ? any.Payload : arguments[0];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("text_from_bytes 第一参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw context.LanguageException("core::OutOfBoundException",
                    "text_from_bytes 字节段越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                var element = span.Elements[start + i];
                if (element is VmAny elementAny) element = elementAny.Payload;
                if (element is not VmU8 byteValue)
                {
                    throw new VmException("text_from_bytes 元素必须是 u8");
                }
                bytes[i] = byteValue.Value;
            }
            // 严格 UTF-8 校验（与 C 侧 rigi_utf8_validate 同规则：结构
            // 完整 + 拒绝过长/代理区/> U+10FFFF）
            if (!TryValidateUtf8(bytes))
            {
                throw context.LanguageException("core::OutOfBoundException",
                    "text_from_bytes 非法 UTF-8 序列：offset=" + start
                    + " count=" + length);
            }
            return new VmString(System.Text.Encoding.UTF8.GetString(bytes));
        }

        // 严格 UTF-8（WFF）校验：结构完整 + 拒绝过长编码 + 拒绝代理区
        //（U+D800–DFFF）+ 拒绝 > U+10FFFF（与 rigi_rt text.c 的
        // rigi_utf8_validate 同规则，双宿主对「合法 UTF-8」判定一致；
        // 3-4 编解码器复用同一口径）
        private static bool TryValidateUtf8(byte[] data)
        {
            var i = 0;
            while (i < data.Length)
            {
                var b0 = data[i];
                if (b0 < 0x80) { i++; continue; }
                if (b0 < 0xC2) return false; // 续字节作首字节；C0/C1 过长
                if (b0 < 0xE0)
                { // 2 字节序列
                    if (i + 1 >= data.Length || (data[i + 1] & 0xC0) != 0x80) return false;
                    i += 2;
                    continue;
                }
                if (b0 < 0xF0)
                { // 3 字节序列：E0 后须 A0..BF（防过长）；ED 后须 80..9F（防代理）
                    if (i + 2 >= data.Length || (data[i + 1] & 0xC0) != 0x80) return false;
                    if (b0 == 0xE0 && data[i + 1] < 0xA0) return false;
                    if (b0 == 0xED && data[i + 1] > 0x9F) return false;
                    if ((data[i + 2] & 0xC0) != 0x80) return false;
                    i += 3;
                    continue;
                }
                if (b0 < 0xF5)
                { // 4 字节序列：F0 后须 90..BF；F4 后须 80..8F（封顶 U+10FFFF）
                    if (i + 3 >= data.Length || (data[i + 1] & 0xC0) != 0x80) return false;
                    if (b0 == 0xF0 && data[i + 1] < 0x90) return false;
                    if (b0 == 0xF4 && data[i + 1] > 0x8F) return false;
                    if ((data[i + 2] & 0xC0) != 0x80 || (data[i + 3] & 0xC0) != 0x80) return false;
                    i += 4;
                    continue;
                }
                return false; // F5..FF 非法首字节
            }
            return true;
        }

        // B2-4b1：stdout_write/stderr_write——把 buffer[offset..
        // offset+count) 原样追加到 VmContext 各自的字节汇（与
        // print/printErr 同一写通道，单次调用加锁原子），返回实际写出的
        // 字节数 = count（VM 侧不会失败）。允许 UTF-8 多字节序列跨 write
        // 边界；仅在生成最终输出文本时解码整个通道。
        // 形态校验与区间越界抛 VmException（native 侧同形态为诊断
        // abort——越界属编译器 bug，Rigi 包装层 checkRange 先行校验）
        private static VmValue StdStreamWrite(VmContext context,
            IReadOnlyList<VmValue> arguments, bool error)
        {
            if (arguments.Count != 3
                || arguments[1] is not VmI32 offset
                || arguments[2] is not VmI32 count)
            {
                throw new VmException("标准流 write 需要 (Span<u8>, i32, i32)");
            }
            var value = arguments[0] is VmAny any ? any.Payload : arguments[0];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("标准流 write 第一参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("标准流 write 区间越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                var element = span.Elements[start + i];
                if (element is VmAny elementAny) element = elementAny.Payload;
                if (element is not VmU8 byteValue)
                {
                    throw new VmException("标准流 write 元素必须是 u8");
                }
                bytes[i] = byteValue.Value;
            }
            if (error)
            {
                context.WriteStderr(bytes.AsSpan());
            }
            else
            {
                context.WriteStdout(bytes.AsSpan());
            }
            return new VmI32(length);
        }

        // B2-4b1：stdout_flush/stderr_flush——VM 侧无操作成功返回
        //（VM 无进程句柄，写汇即已完成；native 侧 fflush + 常规文件
        // 持久化刷新，见 shim.c rigi_stdio_flush_stream）
        private static VmValue StdStreamFlush(VmContext context,
            IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 0)
            {
                throw new VmException("标准流 flush 不接受参数");
            }
            return new VmI32(0);
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

        // 施工块 6-4：core.math 原语参数提取（f32/f64 按位宽严格判定；
        // index 变体供 pow/atan2 二元原语使用）
        private static float RequireF32(string hookName, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmF32 value)
            {
                throw new VmException(hookName + " 需要恰好 1 个 .f32 参数");
            }
            return value.Value;
        }

        private static float RequireF32(string hookName, IReadOnlyList<VmValue> arguments,
            int index)
        {
            if (index >= arguments.Count || arguments[index] is not VmF32 value)
            {
                throw new VmException(hookName + " 需要 .f32 参数（位置 " + index + "）");
            }
            return value.Value;
        }

        private static double RequireF64(string hookName, IReadOnlyList<VmValue> arguments)
        {
            if (arguments.Count != 1 || arguments[0] is not VmF64 value)
            {
                throw new VmException(hookName + " 需要恰好 1 个 .f64 参数");
            }
            return value.Value;
        }

        private static double RequireF64(string hookName, IReadOnlyList<VmValue> arguments,
            int index)
        {
            if (index >= arguments.Count || arguments[index] is not VmF64 value)
            {
                throw new VmException(hookName + " 需要 .f64 参数（位置 " + index + "）");
            }
            return value.Value;
        }

        // ===== 施工块 6-4：pow 的 fdlibm 移植（VM 侧）=====
        // Windows 宿主 Math.Pow（ucrt）误差可超 4 ULP（实测 pow(0.99,1000)
        // 距正确舍入 57 ULP），不满足 §4.11.3——按 D7 修正底层实现：与
        // rigi_rt math.c 同款移植（FreeBSD libm 现行 e_pow.c/e_powf.c，
        // fdlibm 血统；含 IEEE 754-2008 的 +-1**+-INF=1 修正；1**NaN=1）。
        // 全部 IEEE double/float 运算 + 位组装，双端行为一致；特殊值表
        // 与 Tests/e2e/rigi/math_basic.rg pow 节逐条对拍。
        // 原版权_notice（须保留）：Copyright (C) 2004/1993 by Sun
        // Microsystems, Inc.——Permission to use, copy, modify, and
        // distribute this software is freely granted, provided that this
        // notice is preserved.
        private static double PowF64(double x, double y)
        {
            const double
                bp0 = 1.0, bp1 = 1.5,
                dp_h0 = 0.0, dp_h1 = 5.84962487220764160156e-01,
                dp_l0 = 0.0, dp_l1 = 1.35003920212974897128e-08,
                zero = 0.0,
                half = 0.5,
                qrtr = 0.25,
                thrd = 3.3333333333333331e-01,
                one = 1.0,
                two = 2.0,
                two53 = 9007199254740992.0,
                huge = 1.0e300,
                tiny = 1.0e-300,
                L1 = 5.99999999999994648725e-01,
                L2 = 4.28571428578550184252e-01,
                L3 = 3.33333329818377432918e-01,
                L4 = 2.72728123808534006489e-01,
                L5 = 2.30660745775561754067e-01,
                L6 = 2.06975017800338417784e-01,
                P1 = 1.66666666666666019037e-01,
                P2 = -2.77777777770155933842e-03,
                P3 = 6.61375632143793436117e-05,
                P4 = -1.65339022054652515390e-06,
                P5 = 4.13813679705723846039e-08,
                lg2 = 6.93147180559945286227e-01,
                lg2_h = 6.93147182464599609375e-01,
                lg2_l = -1.90465429995776804525e-09,
                ovt = 8.0085662595372944372e-0017,
                cp = 9.61796693925975554329e-01,
                cp_h = 9.61796700954437255859e-01,
                cp_l = -7.02846165095275826516e-09,
                ivln2 = 1.44269504088896338700e+00,
                ivln2_h = 1.44269502162933349609e+00,
                ivln2_l = 1.92596299112661746887e-08;

            double z, ax, z_h, z_l, p_h, p_l;
            double y1, t1, t2, r, s, t, u, v, w;
            int i, j, k, yisint, n;
            int hx, hy, ix, iy;
            uint lx, ly;

            ulong xb = (ulong)BitConverter.DoubleToInt64Bits(x);
            hx = (int)(xb >> 32); lx = (uint)xb;
            ulong yb = (ulong)BitConverter.DoubleToInt64Bits(y);
            hy = (int)(yb >> 32); ly = (uint)yb;
            ix = hx & 0x7fffffff; iy = hy & 0x7fffffff;

            // y==zero: x**0 = 1
            if (((uint)iy | ly) == 0) return one;

            // x==1: 1**y = 1, even if y is NaN
            if (hx == 0x3ff00000 && lx == 0) return one;

            // y!=zero: result is NaN if either arg is NaN
            if ((uint)ix > 0x7ff00000u || ((uint)ix == 0x7ff00000u && lx != 0) ||
                (uint)iy > 0x7ff00000u || ((uint)iy == 0x7ff00000u && ly != 0))
                return x + y;

            // determine if y is an odd int when x < 0
            yisint = 0;
            if (hx < 0)
            {
                if ((uint)iy >= 0x43400000u) yisint = 2; // even integer y
                else if ((uint)iy >= 0x3ff00000u)
                {
                    k = (iy >> 20) - 0x3ff;   // exponent
                    if (k > 20)
                    {
                        j = (int)(ly >> (52 - k));
                        if (((uint)j << (52 - k)) == ly) yisint = 2 - (j & 1);
                    }
                    else if (ly == 0)
                    {
                        j = iy >> (20 - k);
                        if ((j << (20 - k)) == iy) yisint = 2 - (j & 1);
                    }
                }
            }

            // special value of y
            if (ly == 0)
            {
                if ((uint)iy == 0x7ff00000u)
                {   // y is +-inf
                    if (((uint)ix - 0x3ff00000u | lx) == 0)
                        return one;    // (-1)**+-inf is 1
                    else if ((uint)ix >= 0x3ff00000u) // (|x|>1)**+-inf = inf,0
                        return (hy >= 0) ? y : zero;
                    else              // (|x|<1)**-,+inf = inf,0
                        return (hy < 0) ? -y : zero;
                }
                if (iy == 0x3ff00000)
                {   // y is  +-1
                    if (hy < 0) return one / x; else return x;
                }
                if (hy == 0x40000000) return x * x; // y is  2
                if (hy == 0x3fe00000)
                {   // y is  0.5
                    if (hx >= 0)   // x >= +0
                        return Math.Sqrt(x);
                }
            }

            ax = Math.Abs(x);
            // special value of x
            if (lx == 0)
            {
                if (ix == 0x7ff00000 || ix == 0 || ix == 0x3ff00000)
                {
                    z = ax;          // x is +-0,+-inf,+-1
                    if (hy < 0) z = one / z; // z = (1/|x|)
                    if (hx < 0)
                    {
                        if (((uint)ix - 0x3ff00000u | (uint)yisint) == 0)
                        {
                            z = (z - z) / (z - z); // (-1)**non-int is NaN
                        }
                        else if (yisint == 1)
                            z = -z;      // (x<0)**odd = -(|x|**odd)
                    }
                    return z;
                }
            }

            n = (int)(((uint)hx >> 31) - 1u);

            // (x<0)**(non-int) is NaN
            if ((n | yisint) == 0) return (x - x) / (x - x);

            s = one; // s (sign of result -ve**odd) = -1 else = 1
            if ((n | (yisint - 1)) == 0) s = -one; // (-ve)**(odd int)

            // |y| is huge
            if ((uint)iy > 0x41e00000u)
            {   // if |y| > 2**31
                if ((uint)iy > 0x43f00000u)
                {   // if |y| > 2**64, must o/uflow
                    if ((uint)ix <= 0x3fefffffu) return (hy < 0) ? huge * huge : tiny * tiny;
                    if ((uint)ix >= 0x3ff00000u) return (hy > 0) ? huge * huge : tiny * tiny;
                }
                // over/underflow if x is not close to one
                if ((uint)ix < 0x3fefffffu) return (hy < 0) ? s * huge * huge : s * tiny * tiny;
                if ((uint)ix > 0x3ff00000u) return (hy > 0) ? s * huge * huge : s * tiny * tiny;
                // now |1-x| is tiny <= 2**-20, suffice to compute
                // log(x) by x-x^2/2+x^3/3-x^4/4
                t = ax - one;     // t has 20 trailing zeros
                w = (t * t) * (half - t * (thrd - t * qrtr));
                u = ivln2_h * t;  // ivln2_h has 21 sig. bits
                v = t * ivln2_l - w * ivln2;
                t1 = u + v;
                t1 = BitConverter.Int64BitsToDouble(
                    (long)((ulong)BitConverter.DoubleToInt64Bits(t1) & 0xffffffff00000000UL));
                t2 = v - (t1 - u);
            }
            else
            {
                double ss, s2, s_h, s_l, t_h, t_l;
                n = 0;
                // take care subnormal number
                if ((uint)ix < 0x00100000u)
                {
                    ax *= two53; n -= 53;
                    ix = (int)((ulong)BitConverter.DoubleToInt64Bits(ax) >> 32);
                }
                n += (ix >> 20) - 0x3ff;
                j = ix & 0x000fffff;
                // determine interval
                ix = j | 0x3ff00000;     // normalize ix
                if (j <= 0x3988E) k = 0;     // |x|<sqrt(3/2)
                else if (j < 0xBB67A) k = 1; // |x|<sqrt(3)
                else { k = 0; n += 1; ix -= 0x00100000; }
                ax = BitConverter.Int64BitsToDouble((long)(
                    ((ulong)BitConverter.DoubleToInt64Bits(ax) & 0xffffffffUL)
                    | ((ulong)(uint)ix << 32)));

                // compute ss = s_h+s_l = (x-1)/(x+1) or (x-1.5)/(x+1.5)
                u = ax - (k == 0 ? bp0 : bp1);      // bp[0]=1.0, bp[1]=1.5
                v = one / (ax + (k == 0 ? bp0 : bp1));
                ss = u * v;
                s_h = ss;
                s_h = BitConverter.Int64BitsToDouble(
                    (long)((ulong)BitConverter.DoubleToInt64Bits(s_h) & 0xffffffff00000000UL));
                // t_h=ax+bp[k] High
                t_h = zero;
                t_h = BitConverter.Int64BitsToDouble((long)(
                    (ulong)(uint)(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18)) << 32));
                t_l = ax - (t_h - (k == 0 ? bp0 : bp1));
                s_l = v * ((u - s_h * t_h) - s_h * t_l);
                // compute log(ax)
                s2 = ss * ss;
                r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
                r += s_l * (s_h + ss);
                s2 = s_h * s_h;
                t_h = 3 + s2 + r;
                t_h = BitConverter.Int64BitsToDouble(
                    (long)((ulong)BitConverter.DoubleToInt64Bits(t_h) & 0xffffffff00000000UL));
                t_l = r - ((t_h - 3) - s2);
                // u+v = ss*(1+...)
                u = s_h * t_h;
                v = s_l * t_h + t_l * ss;
                // 2/(3log2)*(ss+...)
                p_h = u + v;
                p_h = BitConverter.Int64BitsToDouble(
                    (long)((ulong)BitConverter.DoubleToInt64Bits(p_h) & 0xffffffff00000000UL));
                p_l = v - (p_h - u);
                z_h = cp_h * p_h;    // cp_h+cp_l = 2/(3*log2)
                z_l = cp_l * p_h + p_l * cp + (k == 0 ? dp_l0 : dp_l1);
                // log2(ax) = (ss+..)*2/(3*log2) = n + dp_h + z_h + z_l
                t = n;
                t1 = (((z_h + z_l) + (k == 0 ? dp_h0 : dp_h1)) + t);
                t1 = BitConverter.Int64BitsToDouble(
                    (long)((ulong)BitConverter.DoubleToInt64Bits(t1) & 0xffffffff00000000UL));
                t2 = z_l - (((t1 - t) - (k == 0 ? dp_h0 : dp_h1)) - z_h);
            }

            // split up y into y1+y2 and compute (y1+y2)*(t1+t2)
            y1 = y;
            y1 = BitConverter.Int64BitsToDouble(
                (long)((ulong)BitConverter.DoubleToInt64Bits(y1) & 0xffffffff00000000UL));
            p_l = (y - y1) * t1 + y * t2;
            p_h = y1 * t1;
            z = p_l + p_h;
            j = (int)((ulong)BitConverter.DoubleToInt64Bits(z) >> 32);
            i = (int)(uint)BitConverter.DoubleToInt64Bits(z);
            if (j >= 0x40900000)
            {               // z >= 1024
                if (((uint)j - 0x40900000u | (uint)i) != 0)   // if z > 1024
                    return s * huge * huge;   // overflow
                else
                {
                    if (p_l + ovt > z - p_h) return s * huge * huge; // overflow
                }
            }
            else if ((j & 0x7fffffff) >= 0x4090cc00)
            {   // z <= -1075
                if (((uint)j - 0xc090cc00u | (uint)i) != 0)   // z < -1075
                    return s * tiny * tiny;   // underflow
                else
                {
                    if (p_l <= z - p_h) return s * tiny * tiny; // underflow
                }
            }
            // compute 2**(p_h+p_l)
            i = j & 0x7fffffff;
            k = (i >> 20) - 0x3ff;
            n = 0;
            if (i > 0x3fe00000)
            {       // if |z| > 0.5, set n = [z+0.5]
                n = j + (0x00100000 >> (k + 1));
                k = ((n & 0x7fffffff) >> 20) - 0x3ff; // new k for n
                t = zero;
                t = BitConverter.Int64BitsToDouble((long)(
                    (ulong)(uint)(n & ~(0x000fffff >> k)) << 32));
                n = ((n & 0x000fffff) | 0x00100000) >> (20 - k);
                if (j < 0) n = -n;
                p_h -= t;
            }
            t = p_l + p_h;
            t = BitConverter.Int64BitsToDouble(
                (long)((ulong)BitConverter.DoubleToInt64Bits(t) & 0xffffffff00000000UL));
            u = t * lg2_h;
            v = (p_l - (t - p_h)) * lg2 + t * lg2_l;
            z = u + v;
            w = v - (z - u);
            t = z * z;
            t1 = z - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
            r = (z * t1) / (t1 - two) - (w + z * w);
            z = one - (r - z);
            j = (int)((ulong)BitConverter.DoubleToInt64Bits(z) >> 32);
            // sign bit of z is 0; sign bit of j indicates sign of exponent bias
            j += (int)((uint)n << 20);
            if ((j >> 20) <= 0) z = ScalbnF64(z, n); // subnormal output
            else z = BitConverter.Int64BitsToDouble((long)(
                ((ulong)BitConverter.DoubleToInt64Bits(z) & 0xffffffffUL)
                | ((ulong)(uint)j << 32)));
            return s * z;
        }

        private static float PowF32(float x, float y)
        {
            const float
                bp0 = 1.0f, bp1 = 1.5f,
                dp_h0 = 0.0f, dp_h1 = 5.84960938e-01f,
                dp_l0 = 0.0f, dp_l1 = 1.56322085e-06f,
                zero = 0.0f,
                half = 0.5f,
                qrtr = 0.25f,
                thrd = 3.33333343e-01f,
                one = 1.0f,
                two = 2.0f,
                two24 = 16777216.0f,
                huge = 1.0e30f,
                tiny = 1.0e-30f,
                L1 = 6.0000002384e-01f,
                L2 = 4.2857143283e-01f,
                L3 = 3.3333334327e-01f,
                L4 = 2.7272811532e-01f,
                L5 = 2.3066075146e-01f,
                L6 = 2.0697501302e-01f,
                P1 = 1.6666667163e-01f,
                P2 = -2.7777778450e-03f,
                P3 = 6.6137559770e-05f,
                P4 = -1.6533901999e-06f,
                P5 = 4.1381369442e-08f,
                lg2 = 6.9314718246e-01f,
                lg2_h = 6.93145752e-01f,
                lg2_l = 1.42860654e-06f,
                ovt = 4.2995665694e-08f,
                cp = 9.6179670095e-01f,
                cp_h = 9.6191406250e-01f,
                cp_l = -1.1736857402e-04f,
                ivln2 = 1.4426950216e+00f,
                ivln2_h = 1.4426879883e+00f,
                ivln2_l = 7.0526075433e-06f;

            float z, ax, z_h, z_l, p_h, p_l;
            float y1, t1, t2, r, s, sn, t, u, v, w;
            int i, j, k, yisint, n;
            int hx, hy, ix, iy, iw;

            hx = BitConverter.SingleToInt32Bits(x);
            hy = BitConverter.SingleToInt32Bits(y);
            ix = hx & 0x7fffffff; iy = hy & 0x7fffffff;

            // y==zero: x**0 = 1
            if (iy == 0) return one;

            // x==1: 1**y = 1, even if y is NaN
            if (hx == 0x3f800000) return one;

            // y!=zero: result is NaN if either arg is NaN
            if ((uint)ix > 0x7f800000u ||
                (uint)iy > 0x7f800000u)
                return x + y;

            // determine if y is an odd int when x < 0
            yisint = 0;
            if (hx < 0)
            {
                if ((uint)iy >= 0x4b800000u) yisint = 2; // even integer y
                else if ((uint)iy >= 0x3f800000u)
                {
                    k = (iy >> 23) - 0x7f;   // exponent
                    j = (int)((uint)iy >> (23 - k));
                    if ((j << (23 - k)) == iy) yisint = 2 - (j & 1);
                }
            }

            // special value of y
            if ((uint)iy == 0x7f800000u)
            {   // y is +-inf
                if ((uint)ix == 0x3f800000u)
                    return one;    // (-1)**+-inf is 1
                else if ((uint)ix > 0x3f800000u) // (|x|>1)**+-inf = inf,0
                    return (hy >= 0) ? y : zero;
                else              // (|x|<1)**-,+inf = inf,0
                    return (hy < 0) ? -y : zero;
            }
            if (iy == 0x3f800000)
            {   // y is  +-1
                if (hy < 0) return one / x; else return x;
            }
            if (hy == 0x40000000) return x * x; // y is  2
            if (hy == 0x3f000000)
            {   // y is  0.5
                if (hx >= 0)   // x >= +0
                    return MathF.Sqrt(x);
            }

            ax = MathF.Abs(x);
            // special value of x
            if (ix == 0x7f800000 || ix == 0 || ix == 0x3f800000)
            {
                z = ax;          // x is +-0,+-inf,+-1
                if (hy < 0) z = one / z; // z = (1/|x|)
                if (hx < 0)
                {
                    if (((uint)ix - 0x3f800000u | (uint)yisint) == 0)
                    {
                        z = (z - z) / (z - z); // (-1)**non-int is NaN
                    }
                    else if (yisint == 1)
                        z = -z;      // (x<0)**odd = -(|x|**odd)
                }
                return z;
            }

            n = (int)(((uint)hx >> 31) - 1u);

            // (x<0)**(non-int) is NaN
            if ((n | yisint) == 0) return (x - x) / (x - x);

            sn = one; // s (sign of result -ve**odd) = -1 else = 1
            if ((n | (yisint - 1)) == 0) sn = -one; // (-ve)**(odd int)

            // |y| is huge
            if ((uint)iy > 0x4d000000u)
            {   // if |y| > 2**27
                // over/underflow if x is not close to one
                if ((uint)ix < 0x3f7ffff6u) return (hy < 0) ? sn * huge * huge : sn * tiny * tiny;
                if ((uint)ix > 0x3f800007u) return (hy > 0) ? sn * huge * huge : sn * tiny * tiny;
                // now |1-x| is tiny <= 2**-20, suffice to compute
                // log(x) by x-x^2/2+x^3/3-x^4/4
                t = ax - 1;       // t has 20 trailing zeros
                w = (t * t) * (half - t * (thrd - t * qrtr));
                u = ivln2_h * t;  // ivln2_h has 16 sig. bits
                v = t * ivln2_l - w * ivln2;
                t1 = u + v;
                iw = BitConverter.SingleToInt32Bits(t1);
                t1 = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xfffff000u));
                t2 = v - (t1 - u);
            }
            else
            {
                float s2, s_h, s_l, t_h, t_l;
                n = 0;
                // take care subnormal number
                if ((uint)ix < 0x00800000u)
                {
                    ax *= two24; n -= 24;
                    ix = BitConverter.SingleToInt32Bits(ax);
                }
                n += (ix >> 23) - 0x7f;
                j = ix & 0x007fffff;
                // determine interval
                ix = j | 0x3f800000;     // normalize ix
                if (j <= 0x1cc471) k = 0;     // |x|<sqrt(3/2)
                else if (j < 0x5db3d7) k = 1; // |x|<sqrt(3)
                else { k = 0; n += 1; ix -= 0x00800000; }
                ax = BitConverter.Int32BitsToSingle(ix);

                // compute s = s_h+s_l = (x-1)/(x+1) or (x-1.5)/(x+1.5)
                u = ax - (k == 0 ? bp0 : bp1);      // bp[0]=1.0, bp[1]=1.5
                v = one / (ax + (k == 0 ? bp0 : bp1));
                s = u * v;
                s_h = s;
                iw = BitConverter.SingleToInt32Bits(s_h);
                s_h = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xfffff000u));
                // t_h=ax+bp[k] High
                iw = (int)(((uint)ix >> 1) & 0xfffff000u) | 0x20000000;
                t_h = BitConverter.Int32BitsToSingle(iw + 0x00400000 + (k << 21));
                t_l = ax - (t_h - (k == 0 ? bp0 : bp1));
                s_l = v * ((u - s_h * t_h) - s_h * t_l);
                // compute log(ax)
                s2 = s * s;
                r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
                r += s_l * (s_h + s);
                s2 = s_h * s_h;
                t_h = 3 + s2 + r;
                iw = BitConverter.SingleToInt32Bits(t_h);
                t_h = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xfffff000u));
                t_l = r - ((t_h - 3) - s2);
                // u+v = s*(1+...)
                u = s_h * t_h;
                v = s_l * t_h + t_l * s;
                // 2/(3log2)*(s+...)
                p_h = u + v;
                iw = BitConverter.SingleToInt32Bits(p_h);
                p_h = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xfffff000u));
                p_l = v - (p_h - u);
                z_h = cp_h * p_h;    // cp_h+cp_l = 2/(3*log2)
                z_l = cp_l * p_h + p_l * cp + (k == 0 ? dp_l0 : dp_l1);
                // log2(ax) = (s+..)*2/(3*log2) = n + dp_h + z_h + z_l
                t = n;
                t1 = (((z_h + z_l) + (k == 0 ? dp_h0 : dp_h1)) + t);
                iw = BitConverter.SingleToInt32Bits(t1);
                t1 = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xfffff000u));
                t2 = z_l - (((t1 - t) - (k == 0 ? dp_h0 : dp_h1)) - z_h);
            }

            // split up y into y1+y2 and compute (y1+y2)*(t1+t2)
            iw = BitConverter.SingleToInt32Bits(y);
            y1 = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xfffff000u));
            p_l = (y - y1) * t1 + y * t2;
            p_h = y1 * t1;
            z = p_l + p_h;
            j = BitConverter.SingleToInt32Bits(z);
            if (j > 0x43000000)               // if z > 128
                return sn * huge * huge;      // overflow
            else if (j == 0x43000000)
            {         // if z == 128
                if (p_l + ovt > z - p_h) return sn * huge * huge; // overflow
            }
            else if ((j & 0x7fffffff) > 0x43160000)   // z <= -150
                return sn * tiny * tiny;      // underflow
            else if (j == unchecked((int)0xc3160000))
            {         // z == -150
                if (p_l <= z - p_h) return sn * tiny * tiny;  // underflow
            }
            // compute 2**(p_h+p_l)
            i = j & 0x7fffffff;
            k = (i >> 23) - 0x7f;
            n = 0;
            if (i > 0x3f000000)
            {       // if |z| > 0.5, set n = [z+0.5]
                n = j + (0x00800000 >> (k + 1));
                k = ((n & 0x7fffffff) >> 23) - 0x7f; // new k for n
                t = BitConverter.Int32BitsToSingle(n & ~(0x007fffff >> k));
                n = ((n & 0x007fffff) | 0x00800000) >> (23 - k);
                if (j < 0) n = -n;
                p_h -= t;
            }
            t = p_l + p_h;
            iw = BitConverter.SingleToInt32Bits(t);
            t = BitConverter.Int32BitsToSingle(iw & unchecked((int)0xffff8000u));
            u = t * lg2_h;
            v = (p_l - (t - p_h)) * lg2 + t * lg2_l;
            z = u + v;
            w = v - (z - u);
            t = z * z;
            t1 = z - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
            r = (z * t1) / (t1 - two) - (w + z * w);
            z = one - (r - z);
            j = BitConverter.SingleToInt32Bits(z);
            // sign bit of z is 0; sign bit of j indicates sign of exponent bias
            j += (int)((uint)n << 23);
            if ((j >> 23) <= 0) z = ScalbnF32(z, n); // subnormal output
            else z = BitConverter.Int32BitsToSingle(j);
            return sn * z;
        }

        // pow 内部 scalbn（z 正、|n|≲1200/180；溢出/下溢/次正规路径）
        private static double ScalbnF64(double x, int n)
        {
            if (double.IsNaN(x)) { return x; }
            if (x == 0.0) { return x; }
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(x);
            int e = (int)((bits >> 52) & 0x7ffu);
            if (e == 0x7ff) { return x; }
            if (e == 0)
            {
                x = x * 9007199254740992.0; // 2^53
                bits = (ulong)BitConverter.DoubleToInt64Bits(x);
                e = (int)((bits >> 52) & 0x7ffu) - 53;
            }
            else
            {
                e -= 1023;
            }
            int ne = e + n;
            bits &= 0x800fffffffffffffUL;
            if (ne > 1023) { return x < 0.0 ? double.NegativeInfinity : double.PositiveInfinity; }
            if (ne < -1074) { return x < 0.0 ? -0.0 : 0.0; }
            if (ne < -1022)
            {
                bits |= (ulong)(ne + 1023 + 53) << 52;
                return BitConverter.Int64BitsToDouble((long)bits) * 1.1102230246251565e-16; // 2^-53
            }
            bits |= (ulong)(ne + 1023) << 52;
            return BitConverter.Int64BitsToDouble((long)bits);
        }

        private static float ScalbnF32(float x, int n)
        {
            if (float.IsNaN(x)) { return x; }
            if (x == 0.0f) { return x; }
            uint bits = (uint)BitConverter.SingleToInt32Bits(x);
            int e = (int)((bits >> 23) & 0xffu);
            if (e == 0xff) { return x; }
            if (e == 0)
            {
                x = x * 16777216.0f; // 2^24
                bits = (uint)BitConverter.SingleToInt32Bits(x);
                e = (int)((bits >> 23) & 0xffu) - 24;
            }
            else
            {
                e -= 127;
            }
            int ne = e + n;
            bits &= 0x807fffffu;
            if (ne > 127) { return x < 0.0f ? float.NegativeInfinity : float.PositiveInfinity; }
            if (ne < -149) { return x < 0.0f ? -0.0f : 0.0f; }
            if (ne < -126)
            {
                bits |= (uint)(ne + 127 + 24) << 23;
                return BitConverter.Int32BitsToSingle((int)bits) * 5.9604644775390625e-8f; // 2^-24
            }
            bits |= (uint)(ne + 127) << 23;
            return BitConverter.Int32BitsToSingle((int)bits);
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
