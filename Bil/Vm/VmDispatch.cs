using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    // MW11c 棒4a（RUNTIME §17.4）：VM 宿主的「native 原语 + 调度桥」。
    //
    // 分层：调度策略与 Task 生命周期逻辑在 Rigi 世界（core.coroutine 的
    // Dispatcher/Task，由 VM 解释执行）；本类只提供不能再降的原语实现
    // （Worker 线程/sem 交接/协程句柄 resume/同步 Mutex/TLS/时钟）与
    // 「VM 引擎 ⇄ Rigi 调度逻辑」的粘合（同步解释 Rigi 方法的桥）。
    //
    // VM hook 契约（与 rigi_rt 同形状，token 语义为 VM 私有约定）：
    // - 句柄/帧/fn 指针统一 i64 承载：协程句柄、Worker 句柄、sync mutex
    //   句柄、timer 句柄均为本类注册表键；frame token 是「协程规格表」
    //   键（RegisterSpec）；entryFn token 是「入口 fn 表」键
    //   （RegisterEntry），token 0 = 约定入口 Dispatcher$workerLoop
    //   （native 侧 0 属编译器 bug，VM 侧 0 为合法约定值——棒5 切 native
    //   调度时统一）。
    // - Worker 句柄 0 = 主 Worker（调用 BilVm.Run 的线程），对齐 rigi_rt
    //   「主线程 TLS 返回 0」口径；rigi_worker_park(0)/enqueue(0,·) 操作
    //   主 Worker 的内建队列。
    // - rigi_coroutine_resume 在当前线程内嵌套解释目标协程到下一个
    //   挂起点/终态；返回码对齐 rigi_rt RigiResumeCode：0=SUSPENDED /
    //   1=YIELDED / 2=DONE；VM 扩展 -1=SKIPPED（stale 唤醒 benign：
    //   协程已不在 Runnable）。
    // - rigi_timer_* 棒4a 仅登记/取消/销毁（回调语义归棒4b）。
    //
    // 重入核查：解释器可变状态全部按协程隔离（VmCoroutine.CallStack 等），
    // VmContext 跨线程只读（符号表）或已加锁（stdout/singleton 登记表）；
    // AccountStep 用 Interlocked。resume 钩子嵌套解释用户协程不触碰
    // 调用方协程的帧链，用户协程抛出的 C# 异常在 resume 内捕获并转为
    // 任务失败路径，绝不逃逸进调用方解释帧。

    // Worker 记录：sem 交接协议对齐 rigi_rt worker.c——入队 = 锁内尾插
    // → sem 释放；park = sem 等待 → 锁内头出（空 = 唤醒无任务，返回 0）。
    internal sealed class VmWorker
    {
        public long Handle { get; }
        public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(0);
        public readonly Queue<long> Queue = new Queue<long>();
        public Thread? Thread;
        public volatile bool StopRequested;

        public VmWorker(long handle)
        {
            Handle = handle;
        }
    }

    internal sealed class VmDispatch
    {
        internal const long ResumeSuspended = 0;
        internal const long ResumeYielded = 1;
        internal const long ResumeDone = 2;
        internal const long ResumeSkipped = -1;

        private readonly VmContext _context;
        private long _nextHandle;
        private readonly ConcurrentDictionary<long, VmCoroutine> _coroutines = new();
        private int _activeSegments;
        private long _activityEpoch;
        private readonly ConcurrentDictionary<long, WeakReference<VmCoroutine>> _completedCoroutines = new();
        // 终态协程强登记册：终态后从 _coroutines 摘除的协程统一在此强持有
        // 到摘除点——裸协程（rigi_coroutine_* 原语直驱面）由 destroy 摘除
        // 配对；Task-backed 协程由 Task 对象回收时的 NativeResourceRelease
        // 终结器摘除（fire-and-forget 下 Task 与协程同簇不可达，弱引用在
        // GC 后失效会让 retire/迟到 await 误报「协程句柄失效」——AOT 宿主
        // 实测触发，CoreCLR GC 时点晚不暴露；review-20260910 回归）
        private readonly ConcurrentDictionary<long, VmCoroutine> _completedStrong = new();
        private readonly ConcurrentDictionary<long, VmWorker> _workers = new();
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _mutexes = new();
        private readonly ConcurrentDictionary<long, VmTimerRecord> _timers = new();
        private readonly ConcurrentDictionary<long, (BilFunction Fn, VmValue[] Args)> _specs = new();
        private readonly ConcurrentDictionary<long, BilFunction> _entries = new();
        private readonly ConcurrentDictionary<long, VmCoroutine> _failed = new();
        private readonly object _nativeRcGate = new object();
        private readonly Dictionary<long, ulong> _nativeRcStrong = new();

        private const string TaskCoroutineHiddenKey = "$vm.coroutine.native-rc";

        // Rigi 运行时 fn 解析缓存（模块装载后惰性解析；HasDispatcher=false
        // 时退化为纯状态机——供空模块单元测试直驱引擎）
        private BilFunction? _workerLoopFn;
        private BilFunction? _publishFn;
        private BilFunction? _noteSpawnFn;
        private BilFunction? _noteTerminalFn;
        private bool _fnsResolved;
        private readonly object _fnLock = new object();

        // 当前线程附着的 Worker 句柄（0 = 主线程/未附着，对齐 rigi_rt TLS）
        [ThreadStatic] private static long s_currentWorker;

        // 当前线程正在执行的协程（棒4b：方法 hook/原语钩子里的桥逻辑
        // 需要「当前 Executor」——startCold 的预设兜底与 spawn-into 的
        // 继承绑定；ResumeSegment/InvokeIsolated/Worker 入口以
        // 保存/恢复配对维护，嵌套桥调用不串扰）
        [ThreadStatic] private static VmCoroutine? s_currentCoroutine;

        internal static VmCoroutine? CurrentCoroutine => s_currentCoroutine;

        // 测试缝线：协程恢复点落在哪个 Worker（句柄 → 有序 Worker 序列）。
        // 换绑/多 Executor 用例的确定性观测装置（单所有者不变量保证同一
        // 协程的追加不并发）
        internal readonly ConcurrentDictionary<long, List<long>> ResumeLog = new();
        internal bool TraceResumes { get; set; }

        private void NoteResume(VmCoroutine coroutine)
        {
            if (!TraceResumes) return;
            var list = ResumeLog.GetOrAdd(coroutine.Handle, _ => new List<long>());
            lock (list)
            {
                list.Add(s_currentWorker);
            }
        }

        // 在途唤醒源计数（死锁诊断用）：VM 侧 Alarm/轮询 timer 持有一个
        // 「必将发生唤醒」的债务标记，触发/释放时归还（WakeupMarker 幂等）
        private int _pendingWakeups;

        internal void ArmWakeup() => Interlocked.Increment(ref _pendingWakeups);

        internal void DisarmWakeup() => Interlocked.Decrement(ref _pendingWakeups);

        // 唤醒源债务标记：每个定时器一个，保证恰好归还一次
        internal sealed class WakeupMarker
        {
            private readonly VmDispatch _dispatch;
            private int _armed = 1;

            public WakeupMarker(VmDispatch dispatch)
            {
                _dispatch = dispatch;
                dispatch.ArmWakeup();
            }

            public void Disarm()
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    _dispatch.DisarmWakeup();
                }
            }
        }

        // 测试缝线：注入 Rigi 侧发布失败，验证「丢失唤醒留证」路径
        // （实例级，套件并行安全）
        internal bool FailPublishesForTest;

        // 测试缝：main 协程句柄（BilVm.Run 在 spawn 后写入）与已登记
        // Worker 句柄集（0 = 主 Worker；多 Executor 用例定位用）
        internal long MainHandle { get; set; }

        internal IReadOnlyCollection<long> RegisteredWorkers => _workers.Keys.ToArray();

        // 原语级 worker 入口失败证据（棒4a 无用户诊断通道，测试断言用）
        internal readonly ConcurrentQueue<Exception> WorkerFailures = new();

        public VmDispatch(VmContext context)
        {
            _context = context;
            // 主 Worker 恒存在（句柄 0）：主线程即 Worker
            _workers[0] = new VmWorker(0);
        }

        private long NewHandle() => Interlocked.Increment(ref _nextHandle);

        // ===== Rigi fn 解析 =====

        private void EnsureFnsResolved()
        {
            if (_fnsResolved) return;
            lock (_fnLock)
            {
                if (_fnsResolved) return;
                _workerLoopFn = _context.FindRuntimeFunction(
                    "core.coroutine::Dispatcher$workerLoop(");
                _publishFn = _context.FindRuntimeFunction(
                    "core.coroutine::Dispatcher$publishNative(");
                _noteSpawnFn = _context.FindRuntimeFunction(
                    "core.coroutine::Dispatcher$noteSpawn(");
                _noteTerminalFn = _context.FindRuntimeFunction(
                    "core.coroutine::Dispatcher$noteTerminal(");
                _fnsResolved = true;
            }
        }

        // 模块含 Dispatcher 调度逻辑（完整 stdlib 形态）；否则退化为
        // 纯状态机（CAS 仍执行，Rigi 侧发布/计数跳过——空模块单元测试）
        internal bool HasDispatcher
        {
            get { EnsureFnsResolved(); return _workerLoopFn != null; }
        }

        private VmValue DispatcherInstance()
        {
            return _context.GetSingleton("core.coroutine::Dispatcher")
                ?? throw new VmException("core.coroutine::Dispatcher singleton 未初始化");
        }

        // Task 运行时 fn/字段的符号前缀：非泛型 Task 与 Task<TReturn>
        // 两套同构声明（BIL 泛型以声明形态 TReturn 落盘）
        private static string TaskPrefixOf(string typeRef)
        {
            if (typeRef.StartsWith("core.coroutine::Task<", StringComparison.Ordinal))
            {
                return "core.coroutine::Task<TReturn>";
            }
            return "core.coroutine::Task";
        }

        private BilFunction TaskFn(string prefix, string name)
        {
            return _context.FindRuntimeFunction(prefix + "$" + name + "(")
                ?? throw new VmException("stdlib 缺少 " + prefix + "$" + name);
        }

        private static long ReadI64Field(IVmFieldHost host, string symbol)
        {
            return host.TryReadField(symbol, out var value) && value is VmI64 number
                ? number.Value
                : 0;
        }

        private static long CoroutineTokenOf(VmValue value)
        {
            if (value is not VmObject carriage
                || carriage.TypeRef != "core.coroutine::CoroutineCarriage"
                || !carriage.TryReadField(
                    "core.coroutine::CoroutineCarriage#token@.i64", out var token)
                || token is not VmI64 number)
            {
                return 0;
            }
            return number.Value;
        }

        private static long TaskCoroutineToken(VmObject taskObject)
        {
            return taskObject.TryReadHidden(TaskCoroutineHiddenKey, out var value)
                && value is VmI64 number ? number.Value : 0;
        }

        private static void AttachTaskCoroutineToken(VmObject taskObject, long token)
        {
            taskObject.WriteHidden(TaskCoroutineHiddenKey, new VmI64(token));
        }

        private static bool ReadBoolField(IVmFieldHost host, string symbol)
        {
            return host.TryReadField(symbol, out var value) && value is VmBool flag
                && flag.Value;
        }

        // ===== 同步解释桥（ConstructSingleton/ProbePolling 先例）=====

        // ad-hoc 协程同步跑完一个 Rigi fn（不得挂起；用于无当前协程的
        // 线程：主线程启动序列、timer 线程、worker 线程入口）
        internal VmValue? InvokeIsolated(BilFunction function, VmValue[] args)
        {
            var coroutine = new VmCoroutine(this);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(function, args, null, _context);
            var previous = s_currentCoroutine;
            s_currentCoroutine = coroutine;
            try
            {
                RunToReturn(coroutine);
            }
            finally
            {
                s_currentCoroutine = previous;
            }
            if (coroutine.Failure != null)
            {
                throw coroutine.Failure;
            }
            return coroutine.Result;
        }

        private void RunToReturn(VmCoroutine coroutine)
        {
            while (coroutine.CallStack.Count > 0
                && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(_context);
            }
            if (coroutine.State == VmCoroutineState.Suspended)
            {
                throw new VmException("运行时 fn 意外挂起（调度原语不得 await/yield）");
            }
        }

        // 在当前运行中的协程上同步嵌套解释一个 Rigi fn（ProbePolling
        // 同形态：压帧 → Step 到原深度 → 从结果槽取值）
        internal VmValue? InvokeOn(VmCoroutine coroutine, BilFunction function,
            VmValue[] args, string resultSlot)
        {
            var depth = coroutine.CallStack.Count;
            coroutine.PushFrame(function, args, resultSlot, _context);
            while (coroutine.CallStack.Count > depth
                && coroutine.State == VmCoroutineState.Running)
            {
                coroutine.Step(_context);
            }
            if (coroutine.State != VmCoroutineState.Running)
            {
                throw new VmException("运行时 fn 导致协程意外离开 Running");
            }
            return coroutine.CurrentFrame.Slots.TryGetValue(resultSlot, out var value)
                ? value
                : VmVoid.Instance;
        }

        private void Invoke(BilFunction function, VmValue[] args, VmCoroutine? caller,
            string resultSlot = "$.dispatch.ret")
        {
            if (caller != null)
            {
                InvokeOn(caller, function, args, resultSlot);
                return;
            }
            InvokeIsolated(function, args);
        }

        // ===== 调度桥：spawn / publish / await / 终态 =====

        // §18.1 eager spawn：建协程 + Rigi Task 对象（attachRuntime 装
        // 句柄/gate/状态）→ noteSpawn（live 先增）→ 发布
        internal VmCoroutine Spawn(BilFunction function, IReadOnlyList<VmValue> args,
            VmCoroutine? caller)
        {
            EnsureFnsResolved();
            if (_noteSpawnFn == null || _publishFn == null)
            {
                throw new VmException("stdlib 缺少 core.coroutine::Dispatcher 运行时通道");
            }
            var coroutine = new VmCoroutine(this);
            coroutine.PushFrame(function, args, null, _context);
            // §18.1 第 3 步：未显式指定时继承调用方 Coroutine 的 Executor
            coroutine.BoundExecutor = caller == null ? null : EffectiveExecutorOf(caller);
            var handle = RegisterCoroutine(coroutine);
            var typeRef = VmContext.TaskTypeRef(VmContext.FunctionResultType(function));
            var taskObject = _context.AllocateObject(typeRef);
            coroutine.AttachTaskObject(taskObject);
            AttachTaskCoroutineToken(taskObject, handle);
            var prefix = TaskPrefixOf(typeRef);
            Invoke(TaskFn(prefix, "attachRuntimeNative"),
                new VmValue[] { taskObject, new VmI64(handle) }, caller);
            InheritLocals(coroutine, caller);
            Invoke(_noteSpawnFn!, new VmValue[] { DispatcherInstance() }, caller);
            Publish(coroutine, "eager spawn");
            return coroutine;
        }

        // ===== 棒4b：冷 Task 启动通道（§18.4）=====

        // spawn-into：复用既有 Task 对象建协程（Task↔协程 1:1，不另建
        // 句柄）。前置：调用方已持 task gate 临界区且 tryStart 判定本次
        // 启动成立（state 已投影 Runnable）。body 闭包经 callable 协议
        // 虚派发解析 $$call 实现 fn（与 invoke.indirect 同通道），直接
        // 压帧运行其 async 体（不再经 EagerSpawn——那会另建 Task）
        internal VmCoroutine SpawnInto(VmObject taskObject, string prefix, VmCoroutine? caller)
        {
            EnsureFnsResolved();
            if (_noteSpawnFn == null || _publishFn == null)
            {
                throw new VmException("stdlib 缺少 core.coroutine::Dispatcher 运行时通道");
            }
            var bodySymbol = FieldSymbolOf(taskObject.TypeRef, "body");
            if (!taskObject.TryReadField(bodySymbol, out var body) || body is VmNull)
            {
                throw new VmException("冷 Task 缺少 body：" + taskObject.TypeRef);
            }
            var callSymbol = _context.FindCallTarget(VmTypeOps.ActualType(body),
                Array.Empty<VmValue>())
                ?? throw new VmException("冷 Task body 没有 $$call 实现：" + body.TypeRef);
            var function = _context.FindFunction(callSymbol)
                ?? throw new VmException("冷 Task body 实现缺少 fn 定义：" + callSymbol);
            var coroutine = new VmCoroutine(this);
            coroutine.PushFrame(function, new VmValue[] { body }, null, _context);
            var handle = RegisterCoroutine(coroutine);
            coroutine.AttachTaskObject(taskObject);
            AttachTaskCoroutineToken(taskObject, handle);
            // 目标 Executor：预设 ?? 当前 Executor（§18.4）
            coroutine.BoundExecutor = ReadExecutorField(taskObject, prefix)
                ?? (caller == null ? null : EffectiveExecutorOf(caller));
            Invoke(TaskFn(prefix, "attachRuntimeNative"),
                new VmValue[] { taskObject, new VmI64(handle) }, caller);
            InheritLocals(coroutine, caller);
            Invoke(_noteSpawnFn!, new VmValue[] { DispatcherInstance() }, caller);
            Publish(coroutine, "spawn-into");
            return coroutine;
        }

        // run()/run(executor:) 的方法 hook（Task$startCold）：gate 临界区内
        // tryStart 一次性判定——已启动（含热 Task）抛 IllegalStateException
        // （走真 init 通道，可 catch）；成立则 spawn-into
        internal VmValue StartCold(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmObject taskObject)
            {
                throw new VmException("startCold 需要 Task receiver");
            }
            var prefix = TaskPrefixOf(taskObject.TypeRef);
            var caller = s_currentCoroutine;
            var gate = EnsureGate(taskObject, prefix);
            var mutex = _mutexes[gate];
            mutex.Wait();
            try
            {
                var tryStart = TaskFn(prefix, "tryStart");
                var code = ReadDecision(caller != null
                    ? InvokeOn(caller, tryStart, new VmValue[] { taskObject },
                        "$.dispatch.ret")
                    : InvokeIsolated(tryStart, new VmValue[] { taskObject }));
                if (code != 0)
                {
                    throw IllegalState(caller,
                        "Task 只允许启动一次：对已完成启动的 Task 调用 run");
                }
                SpawnInto(taskObject, prefix, caller);
            }
            finally
            {
                mutex.Release();
            }
            return VmVoid.Instance;
        }

        private static int ReadDecision(VmValue? value)
        {
            return value is VmI32 number
                ? number.Value
                : throw new VmException("运行时判定 fn 未返回 i32");
        }

        // 语言级 IllegalStateException（stdlib 真 init 通道；无协程上下文
        // 时回落直写字段路径，与 NoSuchMethod 两变体同形态）
        private VmException IllegalState(VmCoroutine? coroutine, string message)
        {
            return coroutine != null
                ? _context.LanguageException(coroutine, "core::IllegalStateException",
                    new VmValue[] { new VmString(message) }, new[] { ".string" }, message)
                : _context.LanguageException("core::IllegalStateException", message);
        }

        // 按字段简单名查字段符号（泛型承载形态如 AsyncFunc<TReturn> 的
        // 拼写随声明漂移，运行时桥只锁简单名）
        private string FieldSymbolOf(string typeRef, string simpleName)
        {
            foreach (var field in _context.CollectInstanceFields(typeRef))
            {
                if (BilVerificationContext.TryParseFieldSymbol(field.Symbol,
                        out _, out _, out _)
                    && VmContext.FieldSimpleName(field.Symbol) == simpleName)
                {
                    return field.Symbol;
                }
            }
            throw new VmException("类型缺少字段 " + simpleName + "：" + typeRef);
        }

        // 有效 Executor（§17.1/§18.4）：Task.executor 预设/换绑字段 ??
        // 创建时继承的默认绑定 ?? MainExecutor（null 表示）
        private VmObject? EffectiveExecutorOf(VmCoroutine coroutine)
        {
            var taskObject = coroutine.TaskObject;
            if (taskObject != null)
            {
                var preset = ReadExecutorField(taskObject, TaskPrefixOf(taskObject.TypeRef));
                if (preset != null)
                {
                    return preset;
                }
            }
            return coroutine.BoundExecutor;
        }

        private static VmObject? ReadExecutorField(VmObject taskObject, string prefix)
        {
            return taskObject.TryReadField(
                    prefix + "#executor@.nullable<core.coroutine::Executor>", out var value)
                && value is VmObject executor
                ? executor
                : null;
        }

        // Executor → Dispatcher lane（0=Main / 1=Compute / 2=IO；未知
        // 用户派生 Executor 落 Main——§20.1 只有三个内置 Executor 有
        // 独立调度域）
        internal int LaneOf(VmCoroutine coroutine)
        {
            var executor = EffectiveExecutorOf(coroutine);
            if (executor == null)
            {
                return 0;
            }
            return executor.TypeRef switch
            {
                "core.coroutine::ComputeExecutor" => 1,
                "core.coroutine::IOExecutor" => 2,
                _ => 0,
            };
        }

        // 统一发布入口（旧 VmExecutor.Publish/PublishWakeup 合并形态）：
        // 引擎侧先 CAS 进 Runnable；三连失败 = 已被并发发布或已终态
        // ⇒ benign 跳过（对齐旧 benign 竞态口径，不再配纪元判定——
        // 「入队即唤醒」由队列 transport 保证不丢，残留的丢失通道只剩
        // Rigi 侧发布抛错，转为 Fail 留证，对齐旧 HandleWakeupPublishFailure
        // 的留证语义）
        internal void Publish(VmCoroutine coroutine, string source)
        {
            if (coroutine.Handle == 0)
            {
                return;  // ad-hoc 协程不进调度（防御）
            }
            if (!coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable)
                && !coroutine.TryTransition(VmCoroutineState.Suspended, VmCoroutineState.Runnable)
                && !coroutine.TryTransition(VmCoroutineState.Running, VmCoroutineState.Runnable))
            {
                return;  // benign：已在 Runnable / 已终态
            }
            try
            {
                if (FailPublishesForTest)
                {
                    throw new VmException("注入的发布失败（测试缝线）");
                }
                if (!HasDispatcher)
                {
                    return;  // 空模块单元测试形态：纯状态机
                }
                // TaskState 投影（§18.2）：spawn/publish → Runnable
                NoteRunnable(coroutine);
                // lane 在发布时读取协程最新绑定（§18.4 换绑于下一恢复点
                // 生效）。棒5a：lane 换算移入 Rigi Dispatcher.publish
                //（经 coroutine_get_lane hook 回调 LaneOf），本桥只传
                // 句柄——双端同一换算点
                InvokeIsolated(_publishFn!,
                    new VmValue[] { DispatcherInstance(), new VmI64(coroutine.Handle) });
            }
            catch (Exception exception)
            {
                try
                {
                    coroutine.Fail(new VmException(
                        "调度器丢失唤醒（" + source + "）：协程已 Runnable 但发布失败："
                        + exception.Message));
                }
                catch
                {
                    // 留证路径自身失败（Dispatcher 已坏）：不再升级，
                    // 协程状态机已脱离 Runnable 由终态兜底
                }
            }
        }

        // ===== TaskState 投影桥（§18.2，棒4b）=====

        // 挂起点投影：协程已 CAS 进 Suspended 后调用（Await 的 gate 临界
        // 区内、EventAlarm/Timer 的 TryAwait 锁内、轮询挂起、Mutex 竞争
        // 挂起）。终态三通道（complete/fail/cancel）不经此路径
        internal void NoteSuspended(VmCoroutine coroutine)
        {
            NoteState(coroutine, "markSuspended");
        }

        // 发布点投影：协程已 CAS 进 Runnable 后调用（Publish 统一入口）
        internal void NoteRunnable(VmCoroutine coroutine)
        {
            NoteState(coroutine, "markRunnable");
        }

        private void NoteState(VmCoroutine coroutine, string name)
        {
            var taskObject = coroutine.TaskObject;
            if (taskObject == null || !HasDispatcher)
            {
                return;  // ad-hoc 协程 / 空模块单元测试：无投影对象
            }
            InvokeIsolated(TaskFn(TaskPrefixOf(taskObject.TypeRef), name),
                new VmValue[] { taskObject });
        }

        // ===== 棒4b：Mutex 异步互斥锁桥（§19.6）=====

        private BilFunction MutexFn(string name)
        {
            return _context.FindRuntimeFunction("core.coroutine::Mutex$" + name + "(")
                ?? throw new VmException("stdlib 缺少 core.coroutine::Mutex$" + name);
        }

        private static long ReadMutexGate(VmObject mutexObject)
        {
            return ReadI64Field(mutexObject, "core.coroutine::Mutex#gate@.i64");
        }

        // acquire 的竞争挂起（Mutex$enter 方法 hook）：tryEnter 判定与
        // TrySuspend 在同一 gate 临界区内原子完成（对齐 await 纪律）
        internal VmValue MutexEnter(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmObject mutexObject)
            {
                throw new VmException("Mutex.enter 需要 Mutex receiver");
            }
            var coroutine = s_currentCoroutine
                ?? throw new VmException("Mutex.acquire 需在协程上下文调用");
            if (coroutine.Handle == 0)
            {
                throw new VmException("Mutex.acquire 不支持在运行时初始化上下文"
                    + "（ad-hoc 协程无调度句柄）");
            }
            var mutex = _mutexes[ReadMutexGate(mutexObject)];
            mutex.Wait();
            try
            {
                var code = ReadDecision(InvokeOn(coroutine, MutexFn("tryEnter"),
                    new VmValue[] { mutexObject, new VmI64(coroutine.Handle) },
                    "$.dispatch.ret"));
                if (code != 0)
                {
                    if (!coroutine.TrySuspend())
                    {
                        throw new VmException("Mutex.acquire 竞争挂起时协程不在 Running");
                    }
                    NoteSuspended(coroutine);
                }
            }
            finally
            {
                mutex.Release();
            }
            return VmVoid.Instance;
        }

        // release（Mutex$release 方法 hook）：临界区内 releaseNext 判定
        // （令牌校验失败由 Rigi 侧抛 IllegalStateException），临界区外
        // 发布被唤醒的队首 waiter（FIFO handoff：锁所有权已随判定移交）
        internal VmValue MutexRelease(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmObject mutexObject)
            {
                throw new VmException("Mutex.release 需要 (Mutex, Lock)");
            }
            var caller = s_currentCoroutine;
            var mutex = _mutexes[ReadMutexGate(mutexObject)];
            long next;
            mutex.Wait();
            try
            {
                var releaseNext = MutexFn("releaseNext");
                var value = caller != null
                    ? InvokeOn(caller, releaseNext, args.ToArray(), "$.dispatch.ret")
                    : InvokeIsolated(releaseNext, args.ToArray());
                // Rigi 侧令牌校验抛 IllegalStateException 时结果槽为空：
                // 异常在 InvokeOn 的 Step 循环内已按 caller 的 try/catch
                // 展开（或置 pending 待展开）——直接返回，不覆盖其传播
                next = value == null ? 0 : CoroutineTokenOf(value);
                if (next == 0)
                {
                    return VmVoid.Instance;
                }
            }
            finally
            {
                mutex.Release();
            }
            if (next != 0 && _coroutines.TryGetValue(next, out var waiter))
            {
                Publish(waiter, "Mutex.release");
            }
            return VmVoid.Instance;
        }

        // ===== 棒4b：Timer 时钟底座（§19.5）=====

        // Timer 运行时记录：Rigi Timer 对象的 VM 侧镜像（handle 字段 →
        // 本记录）。EventAlarm 语义（§19.3 粘滞/原子握手）按 RepeatOption
        // 扩展：响铃 = 发布当前全部 waiter；未耗尽的重复闹钟清 signaled
        // 重排下一次；耗尽后恒 signaled（后续 yield 立即具备重新发布
        // 条件，仍结束当前执行段）
        private sealed class VmTimerRecord
        {
            public readonly object Gate = new object();
            public List<VmCoroutine> Waiters = new List<VmCoroutine>();
            public bool Signaled;
            // 剩余响铃次数：-1 = 无限（InfiniteRepeat）
            public long RingsRemaining;
            public long IntervalMs;
            public Timer? DotNetTimer;
            // 唤醒债务（死锁判定的在途唤醒源）：创建即借，耗尽/取消/
            // 销毁时归还（幂等）
            public WakeupMarker? Marker;
        }

        // §18.3 await：Rigi Task.registerWaiter 决策 + 同一 gate 临界区
        // 内挂起（对齐旧 VmTask._gate 不变量）；终态快读不挂起
        internal void Await(VmCoroutine coroutine, VmValue taskValue, string? resultSlot)
        {
            if (taskValue is not VmObject taskObject
                || !(taskObject.TypeRef == "core.coroutine::Task"
                    || taskObject.TypeRef.StartsWith("core.coroutine::Task<",
                        StringComparison.Ordinal)))
            {
                throw new VmException("await 操作数不是 Task：" + taskValue.TypeRef);
            }
            var prefix = TaskPrefixOf(taskObject.TypeRef);
            var gate = EnsureGate(taskObject, prefix);
            var mutex = _mutexes[gate];
            int code;
            mutex.Wait();
            try
            {
                var handle = TaskCoroutineToken(taskObject);
                if (handle == 0)
                {
                    // 冷 Task 首次 await（§18.3/§18.4）：同一临界区内做
                    // 一次性启动判定——赢家 spawn-into（当前 Executor 启动），
                    // 输家不抛、按普通 waiter 登记等待
                    var started = ReadDecision(InvokeOn(coroutine,
                        TaskFn(prefix, "tryStart"), new VmValue[] { taskObject },
                        "$.dispatch.ret"));
                    if (started == 0)
                    {
                        SpawnInto(taskObject, prefix, coroutine);
                        handle = TaskCoroutineToken(taskObject);
                        if (handle == 0)
                        {
                            throw new VmException("spawn-into 后 Task 仍无协程句柄");
                        }
                    }
                    else
                    {
                        // 并发竞争窗口：tryStart 判定已启动但 attachRuntime
                        // 尚未写句柄不可能发生（同一 gate 临界区串行），
                        // 防御性重读
                        handle = TaskCoroutineToken(taskObject);
                    }
                }
                code = (int)((VmI32)(InvokeOn(coroutine,
                    TaskFn(prefix, "registerWaiter"),
                    new VmValue[] { taskObject, new VmI64(coroutine.Handle) },
                    "$.dispatch.ret") ?? throw new VmException(
                        "registerWaiter 未返回判定码"))).Value;
                if (code == 0)
                {
                    coroutine.MarkAwaiting(handle, resultSlot, RequireCoroutine(handle));
                    if (!coroutine.TrySuspend())
                    {
                        throw new VmException("await 时协程不在 Running");
                    }
                    NoteSuspended(coroutine);
                }
                else
                {
                    _ = handle;  // 终态快读路径在临界区外结算
                }
            }
            finally
            {
                mutex.Release();
            }
            if (code != 0)
            {
                ApplyTaskOutcome(coroutine,
                    RequireCoroutine(TaskCoroutineToken(taskObject)),
                    code, resultSlot);
            }
        }

        // 冷 Task 的 gate 懒建（棒4b 前仅防御；热 Task 的 gate 由
        // attachRuntime 在发布前建好）
        private long EnsureGate(VmObject taskObject, string prefix)
        {
            var gate = ReadI64Field(taskObject, prefix + "#gate@.i64");
            if (gate != 0)
            {
                return gate;
            }
            var created = SyncMutexCreate();
            taskObject.WriteField(prefix + "#gate@.i64", new VmI64(created));
            return created;
        }

        private VmCoroutine RequireCoroutine(long handle)
        {
            return (_coroutines.TryGetValue(handle, out var coroutine)
                || _completedStrong.TryGetValue(handle, out coroutine)
                || (_completedCoroutines.TryGetValue(handle, out var weak)
                    && weak.TryGetTarget(out coroutine)))
                ? coroutine
                : throw new VmException("协程句柄失效：" + handle);
        }

        // 恢复点结算：被等待协程已终态（completer 先写终态再发布），
        // 按旧 ApplyTaskOutcome 语义取结果/重抛/传播取消
        internal bool SettleAwait(VmCoroutine coroutine, long handle, string? resultSlot)
        {
            var target = RequireCoroutine(handle);
            var code = target.State switch
            {
                VmCoroutineState.Completed => 1,
                VmCoroutineState.Failed => 2,
                VmCoroutineState.Cancelled => 3,
                _ => throw new VmException("恢复时 Task 仍未终态"),
            };
            ApplyTaskOutcome(coroutine, target, code, resultSlot);
            return coroutine.State == VmCoroutineState.Running;
        }

        private void ApplyTaskOutcome(VmCoroutine coroutine, VmCoroutine target,
            int code, string? resultSlot)
        {
            _failed.TryRemove(target.Handle, out _);
            switch (code)
            {
                case 1:
                    if (resultSlot != null)
                    {
                        coroutine.WriteVar(resultSlot,
                            (target.Result ?? VmVoid.Instance).Copy());
                    }
                    break;
                case 2:
                    coroutine.Complete(VmCompletion.Throw(
                        target.Failure ?? new VmException("Task 失败")));
                    break;
                case 3:
                    coroutine.Complete(VmCompletion.Throw(new VmException("Task 已取消")));
                    break;
                default:
                    throw new VmException("未知 registerWaiter 返回码：" + code);
            }
        }

        // 协程终态统一 choke point（旧 NotifyTerminal + VmTask.Complete/
        // Fail 合并形态）：释放轮询 timer → Rigi Task 终态迁移 + waiter
        // 排空（gate 临界区）→ 逐个发布 waiter → Dispatcher noteTerminal
        internal void OnTerminal(VmCoroutine coroutine)
        {
            coroutine.DisposePollTimer();
            if (coroutine.Handle == 0)
            {
                return;  // ad-hoc 协程不进调度台账
            }
            var failed = coroutine.Failure != null;
            if (failed)
            {
                _failed[coroutine.Handle] = coroutine;
            }
            var taskObject = coroutine.TaskObject;
            if (taskObject != null && HasDispatcher)
            {
                var prefix = TaskPrefixOf(taskObject.TypeRef);
                var gate = ReadI64Field(taskObject, prefix + "#gate@.i64");
                var name = coroutine.State == VmCoroutineState.Completed ? "complete"
                    : coroutine.State == VmCoroutineState.Cancelled ? "cancel" : "fail";
                VmValue? drained;
                var mutex = _mutexes[gate];
                mutex.Wait();
                try
                {
                    drained = InvokeIsolated(TaskFn(prefix, name),
                        new VmValue[] { taskObject });
                }
                finally
                {
                    mutex.Release();
                }
                if (drained is VmArray waiters)
                {
                    for (var i = 0; i < waiters.Length; i++)
                    {
                        var waiterToken = CoroutineTokenOf(waiters.GetAt(i));
                        if (waiterToken != 0
                            && _coroutines.TryGetValue(waiterToken, out var waiter))
                        {
                            Publish(waiter, "Task.ResumeWaiters");
                        }
                    }
                }
            }
            if (HasDispatcher)
            {
                InvokeIsolated(_noteTerminalFn!, new VmValue[]
                {
                    DispatcherInstance(), new VmI64(coroutine.Handle),
                    new VmBool(failed),
                });
            }
            // 活动/排队/挂起协程仍在强登记册；终态结果由 Task 保活。
            // 先发布弱项再摘强项，迟到 await 不存在查找空窗。
            // 终态协程一律进强登记册（review-20260910 #AOT句柄）：不单是
            // 裸协程——Task-backed 协程在 fire-and-forget（async 直调不
            // await 不存 Task）下「Task 保活」前提不成立：Task 对象与协程
            // 同簇即刻不可达，AOT GC 在 OnTerminal→retire 窗口内回收簇后，
            // workerLoop 的 retire/迟到 await 走 RequireCoroutine 只剩死弱
            // 项，误报「协程句柄失效」（AOT 实测触发；CoreCLR GC 时点晚
            // 不暴露）。摘除配对：destroy（CoroutineDestroy）释放引擎初始
            // 强引用时闭合 [终态, destroy] 窗口；Task 对象回收时的
            // NativeResourceRelease 终结器兜底摘残留条目，与 GC 时点解耦
            _completedStrong[coroutine.Handle] = coroutine;
            _completedCoroutines[coroutine.Handle] = new WeakReference<VmCoroutine>(coroutine);
            _coroutines.TryRemove(coroutine.Handle, out _);
            if (taskObject != null && ReadBoolField(taskObject,
                TaskPrefixOf(taskObject.TypeRef) + "#observed@.bool"))
                _failed.TryRemove(coroutine.Handle, out _);
        }

        // 未被任何 await 观察的失败（fire-and-forget；对齐旧
        // VmExecutor.UnobservedFailure 语义）
        internal VmException? UnobservedFailure
        {
            get
            {
                foreach (var pair in _failed)
                {
                    var task = pair.Value.TaskObject;
                    if (task == null)
                    {
                        continue;
                    }
                    var prefix = TaskPrefixOf(task.TypeRef);
                    if (!ReadBoolField(task, prefix + "#observed@.bool"))
                    {
                        return pair.Value.Failure;
                    }
                }
                return null;
            }
        }

        // 主 Worker 循环：主线程同步解释 Dispatcher$workerLoop(0)，
        // park 阻塞至 quiescence（live==0 且队列空）
        internal void RunMainLoop()
        {
            EnsureFnsResolved();
            InvokeIsolated(_workerLoopFn!, new VmValue[]
            {
                DispatcherInstance(), new VmI64(0),
            });
        }

        // 无 Dispatcher 模块（直建 BilModule 的单元测试）降级通道：
        // 主线程同步直跑 main 至终态；裸 yield = 原地继续（单线程等价
        // 语义：重新经过一次调度决策而不换协程，§19.1 不保证切换）
        internal VmCoroutine RunStandalone(BilFunction entry, VmValue[] args)
        {
            var coroutine = new VmCoroutine(this);
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running);
            coroutine.PushFrame(entry, args, null, _context);
            try
            {
                while (true)
                {
                    while (coroutine.State == VmCoroutineState.Running
                        && coroutine.CallStack.Count > 0)
                    {
                        coroutine.Step(_context);
                    }
                    if (coroutine.State == VmCoroutineState.Runnable)
                    {
                        coroutine.TryTransition(
                            VmCoroutineState.Runnable, VmCoroutineState.Running);
                        continue;
                    }
                    return coroutine;
                }
            }
            catch (Exception exception)
            {
                coroutine.Fail(exception);
                return coroutine;
            }
        }

        // PollingAlarm 退避调度（旧 VmExecutor.SchedulePoll 平移；
        // 退避是实现选择不是语言语义）。唤醒债务：回调发布完成后归还
        // （先发布再归还，死锁判定无竞态误判）；协程终态提前释放时经
        // DisposePollTimer 同口归还
        internal void SchedulePoll(VmCoroutine coroutine)
        {
            var delay = coroutine.TakePollDelay();
            var marker = new WakeupMarker(this);
            var timer = new Timer(_ =>
            {
                try
                {
                    Publish(coroutine, "VmDispatch.SchedulePoll");
                }
                finally
                {
                    marker.Disarm();
                }
            }, null, delay, Timeout.Infinite);
            coroutine.AttachTimer(timer, marker);
        }

        // ===== 句柄注册表（内部，测试与原语钩子共用）=====

        internal long RegisterCoroutine(VmCoroutine coroutine)
        {
            var handle = NewHandle();
            _coroutines[handle] = coroutine;
            lock (_nativeRcGate)
            {
                _nativeRcStrong.Add(handle, 1);
            }
            coroutine.AttachHandle(handle);
            return handle;
        }

        // 协程规格表：rigi_coroutine_create 的 frame token（VM 契约）
        internal long RegisterSpec(BilFunction function, VmValue[] args)
        {
            var handle = NewHandle();
            _specs[handle] = (function, args);
            return handle;
        }

        // 入口 fn 表：rigi_worker_create 的 entryFn token（VM 契约）
        internal long RegisterEntry(BilFunction function)
        {
            var handle = NewHandle();
            _entries[handle] = function;
            return handle;
        }

        // ===== 原语钩子实现（VmHooks 经 context.Dispatch 转调）=====

        internal static int ComputeParallelism()
        {
            var setting = Environment.GetEnvironmentVariable("RIGI_COMPUTE_WORKERS");
            return int.TryParse(setting, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var count)
                && count is >= 1 and <= 254 ? count : Math.Clamp(Environment.ProcessorCount, 1, 254);
        }

        internal VmValue WorkerCreate(IReadOnlyList<VmValue> args)
        {
            var entryToken = RequireI64("rigi_worker_create", args, 0);
            BilFunction function;
            VmValue[] functionArgs;
            var handle = NewHandle();
            if (entryToken == 0)
            {
                // VM 约定入口：Dispatcher$workerLoop(workerHandle)
                EnsureFnsResolved();
                function = _workerLoopFn
                    ?? throw new VmException("stdlib 缺少 Dispatcher$workerLoop");
                functionArgs = new VmValue[] { DispatcherInstance(), new VmI64(handle) };
            }
            else
            {
                function = _entries.TryGetValue(entryToken, out var entry)
                    ? entry
                    : throw new VmException("rigi_worker_create：未知 entryFn token "
                        + entryToken);
                functionArgs = new VmValue[] { new VmI64(handle) };
            }
            var worker = new VmWorker(handle);
            _workers[handle] = worker;
            // 线程选型：new Thread（后台）而非 ThreadPool——Worker 循环是
            // 长驻阻塞形态（park 占住线程），进 ThreadPool 会耗尽池线程；
            // IsBackground 保证进程退出不受残余 Worker 拖累
            var thread = new Thread(() =>
            {
                s_currentWorker = handle;
                var coroutine = new VmCoroutine(this);
                coroutine.TryTransition(
                    VmCoroutineState.Created, VmCoroutineState.Running);
                coroutine.PushFrame(function, functionArgs, null, _context);
                s_currentCoroutine = coroutine;
                try
                {
                    RunToReturn(coroutine);
                }
                catch (Exception exception)
                {
                    WorkerFailures.Enqueue(exception);
                }
                finally
                {
                    s_currentCoroutine = null;
                    s_currentWorker = 0;
                }
            })
            {
                IsBackground = true,
                Name = "rigi-worker-" + handle,
            };
            worker.Thread = thread;
            thread.Start();
            return new VmI64(handle);
        }

        internal VmValue WorkerDestroy(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_worker_destroy", args, 0);
            if (handle == 0 || !_workers.TryRemove(handle, out var worker))
            {
                throw new VmException("rigi_worker_destroy：未知 Worker " + handle);
            }
            // 对齐 rigi_rt：stop 置位 + 唤醒（令牌 0 = 退出检查点）+ join
            worker.StopRequested = true;
            Enqueue(worker, 0);
            worker.Thread?.Join();
            return VmVoid.Instance;
        }

        internal VmValue WorkerEnqueue(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_worker_enqueue", args, 0);
            var task = RequireI64("rigi_worker_enqueue", args, 1);
            if (!_workers.TryGetValue(handle, out var worker))
            {
                throw new VmException("rigi_worker_enqueue：未知 Worker " + handle);
            }
            Enqueue(worker, task);
            return VmVoid.Instance;
        }

        internal VmValue WorkerPark(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_worker_park", args, 0);
            if (!_workers.TryGetValue(handle, out var worker))
            {
                throw new VmException("rigi_worker_park：未知 Worker " + handle);
            }
            // sem 等待 → 锁内头出；空 = 唤醒无任务（退出检查点）返回 0。
            // 主 Worker（句柄 0）带死锁显败兜底（对齐 native 死锁诊断
            // abort 口径）：live>0 且无 runnable 且无在途唤醒源时无限
            // park 只会冻死进程，改为清晰诊断抛出（经 workerLoop →
            // RunMainLoop → BilVm.Run 顶层 → 非零退出）
            while (!worker.Semaphore.Wait(100))
            {
                _context.CheckStepLimit();
                if (handle == 0 && IsDeadlocked())
                {
                    if (Environment.GetEnvironmentVariable("RIGI_VM_DEADLOCK_TRACE") == "1")
                    {
                        foreach (var entry in _coroutines)
                        {
                            lock (entry.Value.SyncRoot)
                                Console.Error.WriteLine($"[vm-deadlock] {entry.Key} {entry.Value.State} "
                                    + string.Join(" <- ", entry.Value.CallStack.Select(frame => frame.Function.Symbol)));
                        }
                    }
                    throw new VmException(
                        "VM 调度死锁：存在未终态协程，但无 runnable 任务且无"
                        + "在途唤醒源（Alarm/轮询定时器），调度器无法继续推进");
                }
            }
            lock (worker.Queue)
            {
                return new VmI64(worker.Queue.Count > 0 ? worker.Queue.Dequeue() : 0);
            }
        }

        // 死锁判定（仅主 Worker park 间隙调用）：Dispatcher live>0 且
        // 各 lane 队列皆空且无在途唤醒源、且无 Running/Runnable 协程
        // （棒4b 多 Worker：其余 Worker 上正在跑/待跑的协程是潜在唤醒
        // 源——Task waiter 由目标协程终态发布、Mutex waiter 由持锁者
        // release 发布，都链回 Running/Runnable 协程）。在途唤醒源的
        // 归还发生在唤醒发布完成之后（Signal/poll 回调先发布再归还），
        // 故无竞态误判
        private bool IsDeadlocked()
        {
            var epoch = Interlocked.Read(ref _activityEpoch);
            if (Volatile.Read(ref _activeSegments) != 0) return false;
            if (Volatile.Read(ref _pendingWakeups) > 0)
            {
                return false;
            }
            foreach (var pair in _coroutines)
            {
                if (pair.Value.State is VmCoroutineState.Running
                    or VmCoroutineState.Runnable)
                {
                    return false;
                }
            }
            var dispatcher = _context.GetSingleton("core.coroutine::Dispatcher");
            if (dispatcher is not IVmFieldHost host)
            {
                return false;
            }
            if (!host.TryReadField("core.coroutine::Dispatcher#live@.i32", out var liveValue)
                || liveValue is not VmI32 live || live.Value <= 0)
            {
                return false;
            }
            foreach (var lane in new[] { "mainQueue", "computeQueue", "ioQueue" })
            {
                if (host.TryReadField("core.coroutine::Dispatcher#" + lane
                        + "@core.coroutine::CoroutineCarriageQueue", out var queueValue)
                    && queueValue is IVmFieldHost queueHost
                    && queueHost.TryReadField("core.coroutine::CoroutineCarriageQueue#count@.i32",
                        out var countValue)
                    && countValue is VmI32 count && count.Value > 0)
                {
                    return false;
                }
            }
            foreach (var pair in _workers)
            {
                lock (pair.Value.Queue)
                {
                    if (pair.Value.Queue.Count > 0)
                    {
                        return false;
                    }
                }
            }
            return Volatile.Read(ref _activeSegments) == 0
                && Interlocked.Read(ref _activityEpoch) == epoch
                && Volatile.Read(ref _pendingWakeups) == 0;
        }

        private static void Enqueue(VmWorker worker, long task)
        {
            lock (worker.Queue)
            {
                worker.Queue.Enqueue(task);
            }
            worker.Semaphore.Release();
        }

        internal VmValue CoroutineCreate(IReadOnlyList<VmValue> args)
        {
            // resumeFn token 在 VM 侧不承载语义（VM 直接解释 BIL，无需
            // 状态机 fn 指针）；frame token = 协程规格表键
            var frame = RequireI64("rigi_coroutine_create", args, 1);
            if (!_specs.TryRemove(frame, out var spec))
            {
                throw new VmException("rigi_coroutine_create：未知 frame token " + frame);
            }
            var coroutine = new VmCoroutine(this);
            coroutine.PushFrame(spec.Fn, spec.Args, null, _context);
            // 对齐 native cohandle 语义（句柄无内部状态机，create 后即可
            // resume）：VM 侧直接置 Runnable；后续若经 Dispatcher.publish
            // 发布，其三连 CAS 对 Runnable 幂等 benign
            coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable);
            return new VmI64(RegisterCoroutine(coroutine));
        }

        // 在当前线程内嵌套解释目标协程的一个执行段（复用 Step 机制）；
        // 用户协程抛出的 C# 异常在此捕获并转任务失败路径，不逃逸进
        // 调用方解释帧
        internal VmValue CoroutineResume(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_coroutine_resume", args, 0);
            return new VmI64(ResumeSegment(RequireCoroutine(handle)));
        }

        internal long ResumeSegment(VmCoroutine coroutine)
        {
            Interlocked.Increment(ref _activeSegments);
            Interlocked.Increment(ref _activityEpoch);
            try
            {
            // 协程锁串行化（单所有者不变量：同一协程任意时刻至多一个
            // Worker）：转换 + settle + Step 循环整体在锁内（旧
            // VmExecutor.Execute 同口径；C# lock 同线程可重入，Step 内的
            // 嵌套 Step 循环自然安全）
            lock (coroutine.SyncRoot)
            {
                if (!coroutine.TryTransition(
                        VmCoroutineState.Runnable, VmCoroutineState.Running))
                {
                    return ResumeSkipped;
                }
                var previous = s_currentCoroutine;
                s_currentCoroutine = coroutine;
                NoteResume(coroutine);
                try
                {
                    if (!coroutine.SettleAfterResume(_context))
                    {
                        return ResumeSuspended;
                    }
                    while (coroutine.State == VmCoroutineState.Running)
                    {
                        coroutine.Step(_context);
                    }
                    return coroutine.State switch
                    {
                        VmCoroutineState.Suspended => ResumeSuspended,
                        VmCoroutineState.Runnable => ResumeYielded,
                        _ => ResumeDone,
                    };
                }
                catch (Exception exception)
                {
                    coroutine.Fail(exception);
                    return ResumeDone;
                }
                finally
                {
                    s_currentCoroutine = previous;
                }
            }

            }
            finally
            {
                Interlocked.Increment(ref _activityEpoch);
                Interlocked.Decrement(ref _activeSegments);
            }
        }

        internal VmValue CoroutineDestroy(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_coroutine_destroy", args, 0);
            var coroutine = RequireCoroutine(handle);
            if (coroutine.State is not (VmCoroutineState.Completed
                or VmCoroutineState.Failed or VmCoroutineState.Cancelled))
            {
                throw new VmException("rigi_coroutine_destroy：协程未终态（状态 "
                    + coroutine.State + "）");
            }
            NativeRcReleaseCore(handle);
            // 摘终态强登记册条目（review-20260910 #AOT句柄）：destroy 释放
            // 引擎初始强引用（_nativeRcStrong 归零）即 native cohandle 销毁
            // 点，登记册条目生命周期 = [终态, destroy]，强持有窗口在此闭合。
            // 摘除后的迟到查询安全：resume/lane/二次 destroy 走 Rigi 侧
            // retainCoroutine 先行（token 已摘 → benign null）；await 结算
            // 的 target 由 Task.TaskRuntimeState 双向链或 waiter 的
            // _awaitTarget 根住，弱引用不失效
            _completedStrong.TryRemove(handle, out _);
            return VmVoid.Instance;
        }

        internal VmValue NativeRcRetain(IReadOnlyList<VmValue> args)
        {
            var token = RequireI64("rigi_native_rc_retain", args, 0);
            lock (_nativeRcGate)
            {
                if (!_nativeRcStrong.TryGetValue(token, out var strong))
                {
                    return new VmI32(0);
                }
                if (strong == ulong.MaxValue)
                {
                    throw new VmException("NativeRc 强引用计数溢出");
                }
                _nativeRcStrong[token] = strong + 1;
                return new VmI32(1);
            }
        }

        internal VmValue NativeRcRelease(IReadOnlyList<VmValue> args)
        {
            NativeRcReleaseCore(RequireI64("rigi_native_rc_release", args, 0));
            return VmVoid.Instance;
        }

        private void NativeRcReleaseCore(long token)
        {
            lock (_nativeRcGate)
            {
                if (!_nativeRcStrong.TryGetValue(token, out var strong) || strong == 0)
                {
                    throw new VmException("NativeRc release 收到无效或已释放 token");
                }
                if (strong > 1)
                {
                    _nativeRcStrong[token] = strong - 1;
                    return;
                }
                _nativeRcStrong.Remove(token);
                // 施工块 7-2：fs 句柄的归零析构（关闭流/目录枚举器 + 摘
                // 除记录；锁序 _nativeRcGate → _fsGate 与 FsOpen 一致）。
                // close 错误不上报——持久化错误归 flush 面（§4.5.6），与
                // native 侧 NativeRc 析构回调同口径
                lock (_fsGate)
                {
                    if (_fsFiles.TryGetValue(token, out var file))
                    {
                        _fsFiles.Remove(token);
                        if (file.Resource is FileStream stream)
                        {
                            stream.Dispose();
                        }
                        else if (file.Resource is DirState dir)
                        {
                            dir.Enumerator.Dispose();
                        }
                    }
                }
            }
        }

        internal long SyncMutexCreate()
        {
            var handle = NewHandle();
            // SemaphoreSlim(1,1)：非重入、跨线程，对齐 rigi_rt 同步
            // Mutex 原语（不得跨挂起点持有）
            _mutexes[handle] = new SemaphoreSlim(1, 1);
            return handle;
        }

        // CLR 终结器线程仅操作并发登记册和独占资源，不进入解释器。
        internal void ReleaseOwnedResources(long gate, long coroutine)
        {
            if (gate != 0 && _mutexes.TryRemove(gate, out var mutex)) mutex.Dispose();
            if (coroutine != 0) _completedCoroutines.TryRemove(coroutine, out _);
            if (coroutine != 0) _completedStrong.TryRemove(coroutine, out _);
        }

        internal VmValue SyncMutexCreate(IReadOnlyList<VmValue> args)
        {
            return new VmI64(SyncMutexCreate());
        }

        internal VmValue SyncMutexAcquire(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_sync_mutex_acquire", args, 0);
            while (!_mutexes[handle].Wait(100)) _context.CheckStepLimit();
            return VmVoid.Instance;
        }

        // 预算耗尽是解释器终止，不再执行 Rigi 终态回调（它们也需要步数）。
        // 阻塞 Worker 通过 CheckStepLimit 自行退出，再解除计时器对上下文的根。
        internal void StopAfterStepLimit()
        {
            foreach (var worker in _workers.Values)
                if (worker.Thread != null && worker.Thread != Thread.CurrentThread)
                    worker.Thread.Join();
            foreach (var coroutine in _coroutines.Values) coroutine.DisposePollTimer();
            foreach (var record in _timers.Values)
                lock (record.Gate) record.DotNetTimer?.Dispose();
        }

        internal VmValue SyncMutexRelease(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_sync_mutex_release", args, 0);
            _mutexes[handle].Release();
            return VmVoid.Instance;
        }

        internal VmValue TlsCurrentContext(IReadOnlyList<VmValue> args)
        {
            return new VmI64(s_currentWorker);
        }

        // ===== 棒5a：协程句柄 lane/当前协程三面（stdlib Rigi 体调用）=====

        // 当前协程句柄（native 半场 TLS 槽的 VM 对偶；无当前协程返 0）
        internal VmValue CoroutineCurrent(IReadOnlyList<VmValue> args)
        {
            return new VmI64(s_currentCoroutine?.Handle ?? 0);
        }

        // cohandle lane 槽的 VM 对偶：lane 恒由 executor 字段/继承绑定
        // 现算（LaneOf），不另存槽——get hook 直读，set hook 为空操作
        //（stdlib executor setter 写 executor 字段即完成 VM 侧换绑）
        internal VmValue CoroutineGetLane(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_coroutine_get_lane", args, 0);
            // 声明返回 i32（stdlib coroutine.rg）：必须返 VmI32——返
            // VmI64 会让 publish 体的 lane == 1 比较抛类型错，异常穿越
            // 未释放的 gate 临界区后 OnTerminal 重入同一闸造成死锁
            return new VmI32(LaneOf(RequireCoroutine(handle)));
        }

        internal VmValue CoroutineSetLane(IReadOnlyList<VmValue> args)
        {
            _ = RequireI64("rigi_coroutine_set_lane", args, 0);
            if (args.Count <= 1 || args[1] is not VmI32)
            {
                throw new VmException("rigi_coroutine_set_lane：参数 1 需要 i32");
            }
            return VmVoid.Instance;
        }

        // CoroutineLocal（§20.2）：绑定栈在 VmCoroutine 上；无当前协程时
        // get 返 null（与 native 零值同口径），push/pop 属 withValue 内
        // 部通道，无当前协程即编译器/标准库 bug。
        internal VmValue CoroLocalPush(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2)
            {
                throw new VmException("coro_local_push 需要 key 与 value");
            }
            var current = s_currentCoroutine
                ?? throw new VmException("coro_local_push 无当前协程");
            current.LocalPush(args[0], args[1]);
            return VmVoid.Instance;
        }

        internal VmValue CoroLocalPop(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1)
            {
                throw new VmException("coro_local_pop 需要 key");
            }
            var current = s_currentCoroutine
                ?? throw new VmException("coro_local_pop 无当前协程");
            current.LocalPop(args[0]);
            return VmVoid.Instance;
        }

        internal VmValue CoroLocalGet(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1)
            {
                throw new VmException("coro_local_get 需要 key");
            }
            var current = s_currentCoroutine;
            if (current == null)
            {
                return VmNull.Instance;
            }
            return current.LocalGet(args[0]);
        }

        internal VmValue CoroLocalInherit(IReadOnlyList<VmValue> args)
        {
            var child = RequireI64("rigi_coro_local_inherit", args, 0);
            var current = s_currentCoroutine;
            if (current == null)
            {
                return VmVoid.Instance;
            }
            RequireCoroutine(child).InheritLocalsFrom(current);
            return VmVoid.Instance;
        }

        private static void InheritLocals(VmCoroutine child, VmCoroutine? caller)
        {
            if (caller != null)
            {
                child.InheritLocalsFrom(caller);
            }
        }

        internal VmValue TimeNow(IReadOnlyList<VmValue> args)
        {
            return new VmI64(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        // 与 native rigi_time_now_parts 同 ABI：一次 UTC 采样写 i64 ms
        // 与 i32 毫秒外 ns（小端）；旧 time_now 仍只返回 i64 毫秒。
        internal VmValue TimeNowParts(IReadOnlyList<VmValue> args)
        {
            var output = RequireFsSpan("time_now_parts", args, 0);
            if (args.Count != 1 || output.Length < 12)
                throw new VmException("time_now_parts 需要至少 12 字节 Span<u8>");
            long ms;
            int ns;
            if (OperatingSystem.IsWindows())
            {
                // 单次 FILETIME 时间源：DateTime.UtcNow.Ticks 为 100ns
                // 单位；差值可能为负，向下取整至毫秒再留下非负余数。
                var unix100 = DateTime.UtcNow.Ticks - 621355968000000000L;
                ms = Math.DivRem(unix100, 10000L, out var rem100);
                if (rem100 < 0)
                {
                    ms -= 1;
                    rem100 += 10000L;
                }
                ns = (int)(rem100 * 100L);
            }
            else if (OperatingSystem.IsLinux())
            {
                // CLOCK_REALTIME=0；同一次 timespec 的秒和纳秒不能拆开
                // 采样。tv_nsec 是 0..999999999，tv_sec 可为负。
                if (ClockGetTimeMonotonic(0, out var ts) != 0)
                    throw new InvalidOperationException(
                        "time_now_parts clock_gettime(CLOCK_REALTIME) 失败");
                if (ts.TvNsec < 0 || ts.TvNsec >= 1000000000L
                    || ts.TvSec < long.MinValue / 1000L + 1
                    || ts.TvSec > long.MaxValue / 1000L - 1)
                    throw new InvalidOperationException("time_now_parts 宿主时刻范围异常");
                ms = checked((ts.TvSec * 1000L) + (ts.TvNsec / 1000000L));
                ns = (int)(ts.TvNsec % 1000000L);
            }
            else throw new NotSupportedException("time_now_parts 暂不支持该宿主");
            WriteFsI64Le(output, ms);
            WriteFsI32Le(output, 8, ns);
            return VmVoid.Instance;
        }

        // 施工块 6-3（§4.9.4）：VM 与 rigi_rt 同语义的单调时钟读数
        // （纳秒）。刻意不用宿主 Stopwatch.GetTimestamp——其时间源计入
        // 整机睡眠，与契约「排除睡眠」语义不同（§4.9.4「不直接继承
        // 语义不同的宿主便利 API」）：Windows P/Invoke
        // QueryUnbiasedInterruptTimePrecise、Linux P/Invoke
        // clock_gettime(CLOCK_MONOTONIC)（man7：不计入挂起），与
        // rigi_rt worker.c rigi_monotonic_now_ns 逐项对齐。i64 纳秒
        // ~292 年量级，实际不可能触达，不设额外溢出分支
        internal VmValue MonotonicNow(IReadOnlyList<VmValue> args)
        {
            return new VmI64(MonotonicNowNanos());
        }

        internal static long MonotonicNowNanos()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    QueryUnbiasedInterruptTimePrecise(out var unbiasedTime);
                    return (long)(unbiasedTime * 100UL);
                }
                catch (EntryPointNotFoundException)
                {
                    // Precise 变体要求 Windows 10 1607+；缺失时退回
                    // 非 Precise（kernel32，0.5ms 更新批处理；单调、
                    // 排除睡眠语义不变，对齐 rigi_rt 回退路径）
                    QueryUnbiasedInterruptTime(out var unbiasedTime);
                    return (long)(unbiasedTime * 100UL);
                }
            }
            if (OperatingSystem.IsLinux())
            {
                // CLOCK_MONOTONIC = 1（<time.h>；man7：不计入系统挂起）
                var rc = ClockGetTimeMonotonic(1, out var ts);
                if (rc != 0)
                {
                    throw new InvalidOperationException(
                        "clock_gettime(CLOCK_MONOTONIC) 失败（环境异常）");
                }
                return (ts.TvSec * 1000000000L) + ts.TvNsec;
            }
            throw new NotSupportedException("单调时钟原语暂不支持该平台");
        }

        // QueryUnbiasedInterruptTimePrecise 实际导出在 kernelbase.dll
        // （kernel32 不导出 Precise 变体；非 Precise 的
        // QueryUnbiasedInterruptTime 才在 kernel32）
        [System.Runtime.InteropServices.DllImport("kernelbase.dll")]
        private static extern bool QueryUnbiasedInterruptTimePrecise(
            out ulong unbiasedTime);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryUnbiasedInterruptTime(
            out ulong unbiasedTime);

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PosixTimespec
        {
            public long TvSec;
            public long TvNsec;
        }

        [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "clock_gettime")]
        private static extern int ClockGetTimeMonotonic(int clockId, out PosixTimespec timespec);
        // 棒4b：Timer 原语真排程（§19.5）。VM 契约：ctx 形参承载
        // repeatCount（0=NoRepeat / -1=InfiniteRepeat / n=有限次数）；
        // callbackFn 在 VM 侧不承载语义（响铃 = 发布记录内 waiter）。
        // owner 仅登记语义——waiter 恢复恒发布到 waiter 自己绑定的
        // Executor（§18.3），响铃回调在 .NET ThreadPool 线程触发后走
        // 统一 Publish 通道。delay<=0 立即触发（沿用 sleep(<=0) 口径）
        internal VmValue TimerCreate(IReadOnlyList<VmValue> args)
        {
            var delay = RequireI64("rigi_timer_create", args, 1);
            var repeatMs = RequireI64("rigi_timer_create", args, 2);
            var repeatCount = RequireI64("rigi_timer_create", args, 4);
            var record = new VmTimerRecord
            {
                RingsRemaining = repeatCount == 0 ? 1 : repeatCount,
                IntervalMs = repeatMs,
                // 唤醒债务先借出再武装定时器（先注入/借债再 Arm 的配对
                // 纪律：回调可能立即触发，归还必在借债之后）
                Marker = new WakeupMarker(this),
            };
            var handle = NewHandle();
            _timers[handle] = record;
            record.DotNetTimer = new Timer(_ => RingTimer(handle), null,
                Math.Max(delay, 0), Timeout.Infinite);
            return new VmI64(handle);
        }

        // 响铃：发布当前全部 waiter；重复闹钟未耗尽则清 signaled 重排
        // 下一次，耗尽后恒 signaled 并归还唤醒债务
        private void RingTimer(long handle)
        {
            if (!_timers.TryGetValue(handle, out var record))
            {
                return;  // 已销毁：stale 回调 benign
            }
            List<VmCoroutine> waiters;
            bool rearm;
            lock (record.Gate)
            {
                waiters = record.Waiters;
                record.Waiters = new List<VmCoroutine>();
                if (record.RingsRemaining > 0)
                {
                    record.RingsRemaining--;
                }
                rearm = record.RingsRemaining != 0;
                record.Signaled = !rearm;
            }
            foreach (var waiter in waiters)
            {
                Publish(waiter, "Timer.ring");
            }
            if (rearm)
            {
                lock (record.Gate)
                {
                    record.DotNetTimer?.Change(record.IntervalMs, Timeout.Infinite);
                }
            }
            else
            {
                record.Marker?.Disarm();
            }
        }

        // yield EventAlarm（§19.3/§19.5，棒5a 起 Timer 与 sleep 的
        // SleepAlarm 统一本通道）：注册/触发原子握手——signaled 则
        // 返回 false（调用方结束执行段并重新发布）；否则登记 waiter 并
        // 在同一锁内挂起。handle 字段在 EventAlarm 基类上；L8 起
        // handle==0（用户直继子类无事件源）懒建手动事件粘滞底座并
        // 回写（native EventAlarm.ensureHandle → rigi_event_create_sticky
        // 同口径）
        internal bool TryAwaitTimer(VmObject alarmObject, VmCoroutine waiter)
        {
            var handle = ReadI64Field(alarmObject,
                "core.coroutine::EventAlarm#handle@.i64");
            if (handle == 0)
            {
                handle = EnsureEventBase(alarmObject);
            }
            if (!_timers.TryGetValue(handle, out var record))
            {
                throw new VmException("EventAlarm 句柄失效：" + handle);
            }
            lock (record.Gate)
            {
                if (record.Signaled)
                {
                    return false;
                }
                if (!waiter.TrySuspend())
                {
                    throw new VmException("yield Timer 时协程不在 Running");
                }
                record.Waiters.Add(waiter);
                // 投影必须在 Gate 内完成：锁外 RingTimer 可能已经
                // Publish→NoteRunnable，再 markSuspended 会把已 Runnable
                // 的 Task 打回 Suspended
                NoteSuspended(waiter);
            }
            return true;
        }

        internal VmValue TimerCancel(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_timer_cancel", args, 0);
            if (_timers.TryGetValue(handle, out var record))
            {
                lock (record.Gate)
                {
                    record.DotNetTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    record.RingsRemaining = 0;
                    record.Signaled = true;
                }
                record.Marker?.Disarm();
            }
            return VmVoid.Instance;
        }

        internal VmValue TimerDestroy(IReadOnlyList<VmValue> args)
        {
            var handle = RequireI64("rigi_timer_destroy", args, 0);
            if (_timers.TryRemove(handle, out var record))
            {
                lock (record.Gate)
                {
                    record.DotNetTimer?.Dispose();
                    record.DotNetTimer = null;
                    record.RingsRemaining = 0;
                    record.Signaled = true;
                }
                record.Marker?.Disarm();
            }
            return VmVoid.Instance;
        }

        // 粘滞 EventAlarm 复用定时器等待登记与唤醒债务。
        private long EventCreateCore()
        {
            var handle = NewHandle();
            _timers[handle] = new VmTimerRecord
            {
                Marker = new WakeupMarker(this),
            };
            return handle;
        }



        // L8：用户直继 EventAlarm 子类默认底座懒建（native EventAlarm.
        // ensureHandle → rigi_event_create_sticky 同口径）：lock 内双检
        // + 回写 handle 字段——并发首触只建一枚（§19.3 握手前置：底座
        // 唯一性本身须原子）；记录为粘滞形态
        private long EnsureEventBase(VmObject alarmObject)
        {
            lock (_timers)
            {
                var existing = ReadI64Field(alarmObject,
                    "core.coroutine::EventAlarm#handle@.i64");
                if (existing != 0)
                {
                    return existing;
                }
                var handle = EventCreateCore();
                alarmObject.WriteField("core.coroutine::EventAlarm#handle@.i64",
                    new VmI64(handle));
                return handle;
            }
        }

        // L8：rigi_event_create_sticky hook 承载（stdlib EventAlarm.
        // ensureHandle 的 native 声明；粘滞形态）
        internal VmValue EventCreateSticky(IReadOnlyList<VmValue> args) =>
            new VmI64(EventCreateCore());

        // L8：rigi_event_signal 镜像（stdlib EventAlarm.signal 的 native
        // 声明）：粘滞形态触发——闸内恒置 Signaled（终态，重复 signal
        // 幂等）+ 归还唤醒债务 + 排空 waiter，闸外逐个发布（native
        // rigi_event_signal 同序；signal 前写入对恢复协程可见，§21）
        internal VmValue EventSignal(IReadOnlyList<VmValue> args)
        {
            EventSignalCore(RequireI64("rigi_event_signal", args, 0));
            return VmVoid.Instance;
        }

        // event_signal 的宿主内核心（stdin 后台读线程经此触发 §19.3
        // 唤醒，与 Rigi 层 signal 同一通道；线程形态先例 = RingTimer/
        // SchedulePoll 的 .NET 线程池回调 → Publish → InvokeIsolated）
        private void EventSignalCore(long handle)
        {
            if (!_timers.TryGetValue(handle, out var record))
            {
                return;
            }
            List<VmCoroutine> waiters;
            lock (record.Gate)
            {
                waiters = record.Waiters;
                record.Waiters = new List<VmCoroutine>();
                record.Signaled = true;
                record.Marker?.Disarm();
            }
            foreach (var waiter in waiters)
            {
                Publish(waiter, "EventAlarm.signal");
            }
        }

        // ===== B2-4b2：标准输入异步读（stdlib core/io/stdstreams.rg 的
        // stdin_read_start/stdin_read_take 双宿主同语义）=====
        // 「启动即返」卸载形态（对齐 native shim.c stdin 原语）：把阻塞
        // 读交给专用后台线程（绝不在 Worker/解释线程上同步阻塞 stdin），
        // 读完成写回 Span 并登记结果后经 EventSignalCore 触发事件——
        // 挂起协程由 §19.3 原子握手唤醒（先 signal 后 yield 由粘滞语义
        // 兜底）。EOF 粘滞全局短路（0 = EOF，跨包装对象共享同一 stdin）。

        // stdin 状态闸：EOF 粘滞旗标、在途读互斥（同一时刻至多一个读，
        // 契约禁止共享位置并发读）、take 结果槽。每宿主实例一份（stdin
        // 全局唯一语义不跨宿主实例共享）
        private readonly object _stdinGate = new object();
        private Stream? _stdinStream;
        private bool _stdinEof;
        private bool _stdinInflight;
        private int _stdinResult;

        internal VmValue StdinReadStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 4 || args[1] is not VmI32 offset
                || args[2] is not VmI32 count || args[3] is not VmI64 wake)
            {
                throw new VmException("stdin_read_start 需要 (Span<u8>, i32, i32, i64)");
            }
            var value = args[0] is VmAny any ? any.Payload : args[0];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("stdin_read_start 第一参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("stdin_read_start 区间越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            lock (_stdinGate)
            {
                if (_stdinEof)
                {
                    return new VmI32(1);  // EOF 粘滞短路（Rigi 层直接返回 0）
                }
                if (_stdinInflight)
                {
                    throw new VmException("stdin 已有在途读（共享位置并发读违反契约）");
                }
                _stdinInflight = true;
            }
            // 专用后台线程（非线程池——阻塞读可能占住线程任意久；
            // IsBackground 保证进程退出不被拖累）。Span 挂起期间借用
            //（§4.4）：调用协程挂起持有引用，后台线程写入无回收风险
            var reader = new Thread(() => StdinReadBody(span, start, length, wake.Value))
            {
                IsBackground = true,
                Name = "rigi-stdin",
            };
            reader.Start();
            return new VmI32(0);
        }

        // 后台读线程体：阻塞读一次（Stream.Read 返回 0 = EOF），写回
        // Span 后在闸内登记结果，再触发唤醒事件（登记先于 signal——
        // 恢复协程必见结果）。stdin 不可用（已关闭/无效句柄）抛异常
        // 按 EOF 处理——对齐 native 面 read 的 EBADF→EOF 口径：进程
        // 无 stdin 即「已关闭」（§4.4 EOF 路径确定性）
        private void StdinReadBody(VmSpan span, int start, int length, long wake)
        {
            int n;
            byte[] buffer = new byte[length];
            try
            {
                // 原始字节流（不经 Console.In 的 TextReader——避免其
                // 内部缓冲预读吞掉后续字节）
                _stdinStream ??= Console.OpenStandardInput();
                n = _stdinStream.Read(buffer, 0, length);
                if (n < 0)
                {
                    n = 0;
                }
            }
            catch
            {
                n = 0;
            }
            for (var i = 0; i < n; i++)
            {
                span.Elements[start + i] = new VmU8(buffer[i]);
            }
            lock (_stdinGate)
            {
                if (n == 0)
                {
                    _stdinEof = true;
                }
                _stdinResult = n;
                _stdinInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue StdinReadTake(IReadOnlyList<VmValue> args)
        {
            lock (_stdinGate)
            {
                // take 只发生在事件唤醒之后：signal 前结果已在闸内登记，
                // 未登记即到取属时序 bug（防御诊断，正常路径不可达）
                if (!_stdinEof && _stdinInflight)
                {
                    throw new VmException("stdin_read_take：结果未就绪（时序 bug）");
                }
                return new VmI32(_stdinResult);
            }
        }

        // ===== 施工块 7-2：core.fs native 原语层（stdlib core/fs/
        // primitives.rg 的 fs_open/fs_read_start/fs_read_take 原语面
        // 双宿主同语义；rigi_rt fs.c 镜像）=====
        // 句柄：token 统一经 NewHandle 分配（与协程/定时器/事件同一
        // 空间）；记录 Dictionary<long, VmFsFile>。retain/release 复用
        // _nativeRcStrong 通用计数表（NativeRcRetain 对 fs token 天然
        // 兼容），release 归零时在 NativeRcReleaseCore 内关闭流并摘除
        // 记录（锁序 _nativeRcGate → _fsGate，与 FsOpen 一致）。
        // 错误：.NET 异常 → 与 rigi_rt fs.c 同一归一码表（MapFsError，
        // 与 Rigi 层 fsErrKind 三方同值）；分类映射在 Rigi 层。
        private readonly object _fsGate = new object();
        private readonly Dictionary<long, VmFsFile> _fsFiles = new();

        // Resource 是 FileStream（文件句柄）或 DirState（目录句柄）；
        // read/write/flush 三类在途槽互相独立（同一句柄同类同时刻至多
        // 一个在途——契约禁止同流并发/重入（§4.4），冲突诊断抛错）
        private sealed class VmFsFile
        {
            public required object Resource { get; init; }
            // 追加模式（fs_open 带 FAppend|FCreate）：§4.5.6「系统追加
            // 机制使每次写入始终到达当时末尾」。句柄本身必须是 OS 追加
            // 专用形态（Windows 仅 FILE_APPEND_DATA 的句柄 / Linux
            // O_APPEND fd，见 FsOpen→OpenAppendStream）——.NET 的
            // FileMode.Append 只是「打开时定位一次末尾」的普通写句柄
            //（Windows GENERIC_WRITE 含 FILE_WRITE_DATA，Linux fdinfo
            // 实测无 O_APPEND），不能只凭 FileMode 名字推断追加语义；
            // 写线程体据此走 AppendWriteCore（单次系统调用内由内核选位，
            // 绝不 Seek(End)+Write——两步用户态非原子，其他进程可在
            // 两步之间增长文件致本笔覆盖）
            public bool AppendMode;
            public bool ReadInflight;
            public int ReadResult;
            public bool WriteInflight;
            public int WriteResult;
            public bool FlushInflight;
            public int FlushResult;
        }

        // 目录句柄状态：diropen 预热首条目（native FindFirstFileW 同口径
        // ——打开即验证，空目录合法）；结束后持续返回结束
        private sealed class DirState
        {
            public required IEnumerator<FileSystemInfo> Enumerator { get; init; }
            public FileSystemInfo? Pending;
            public bool Finished;
        }

        private static FileStream RequireFsStream(VmFsFile file, string hook)
        {
            if (file.Resource is not FileStream stream)
            {
                throw new VmException(hook + "：句柄不是文件（目录句柄不可用）");
            }
            return stream;
        }

        private static DirState RequireFsDir(VmFsFile file, string hook)
        {
            if (file.Resource is not DirState dir)
            {
                throw new VmException(hook + "：句柄不是目录（文件句柄不可用）");
            }
            return dir;
        }

        // .NET 异常 → 归一错误码（与 rigi_rt fs.c 的 rigi_fs_map_winerr
        // /rigi_fs_map_errno 及 Rigi 层 fsErrKind 三方同表；Windows 的
        // IOException HResult 低 16 位是 Win32 错误码——IO 错误族的宿主
        // 形态；Unix 的 IOException HResult 低 16 位是 errno，走同值的
        // errno 归一表。UnauthorizedAccessException 由调用点先判「目录
        // 当文件开」补救，到达本表即按权限不足归类）
        private static int MapFsError(Exception ex)
        {
            switch (ex)
            {
                case FileNotFoundException:
                case DirectoryNotFoundException:
                    return 2;   // NotFound
                case UnauthorizedAccessException:
                    return 13;  // PermissionDenied
                case NotSupportedException:
                    return 38;  // Unsupported
                case ArgumentException:
                    return 22;  // InvalidPath
                case OutOfMemoryException:
                    return 12;
                case IOException io:
                    return OperatingSystem.IsWindows()
                        ? MapFsWin32(io.HResult & 0xFFFF)
                        : MapFsErrno(io.HResult & 0xFFFF);
                default:
                    return 1000; // Other
            }
        }

        // Unix errno → 归一码（与 rigi_rt fs.c rigi_fs_map_errno 同表同值
        // ——EDQUOT 归 NoSpace、ETXTBSY 归 SharingViolation、EOPNOTSUPP
        // 归 Unsupported，注释同源）
        private static int MapFsErrno(int e)
        {
            switch (e)
            {
                case 2: return 2;    // ENOENT
                case 1:             // EPERM
                case 13: return 13; // EACCES
                case 17: return 17; // EEXIST
                case 18: return 18; // EXDEV
                case 20: return 20; // ENOTDIR
                case 21: return 21; // EISDIR
                case 22: return 22; // EINVAL
                case 26: return 26; // ETXTBSY ≈ 共享冲突
                case 28: return 28; // ENOSPC
                case 122: return 28; // EDQUOT 配额满按 NoSpace
                case 30: return 30; // EROFS
                case 31: return 31; // EMLINK
                case 36: return 36; // ENAMETOOLONG
                case 39: return 39; // ENOTEMPTY
                case 40: return 40; // ELOOP（链接循环，不伪装 NotFound）
                case 9: return 9;   // EBADF
                case 12: return 12; // ENOMEM
                case 38: return 38; // ENOSYS
                case 95: return 38; // EOPNOTSUPP
                case 5: return 5;   // EIO
                default: return 1000;
            }
        }

        private static int MapFsWin32(int err)
        {
            switch (err)
            {
                case 2:   // ERROR_FILE_NOT_FOUND
                case 3:   // ERROR_PATH_NOT_FOUND
                case 15:  // ERROR_INVALID_DRIVE
                    return 2;
                case 5:   // ERROR_ACCESS_DENIED
                case 1314: // ERROR_PRIVILEGE_NOT_HELD
                case 998: // ERROR_NOACCESS
                    return 13;
                case 80:  // ERROR_FILE_EXISTS
                case 183: // ERROR_ALREADY_EXISTS
                    return 17;
                case 17:  // ERROR_NOT_SAME_DEVICE
                    return 18;
                case 267: // ERROR_DIRECTORY
                    return 20;
                case 145: // ERROR_DIR_NOT_EMPTY
                    return 39;
                case 112: // ERROR_DISK_FULL
                case 39:  // ERROR_HANDLE_DISK_FULL
                    return 28;
                case 32:  // ERROR_SHARING_VIOLATION
                case 33:  // ERROR_LOCK_VIOLATION
                    return 26;
                case 206: // ERROR_FILENAME_EXCED_RANGE
                case 111: // ERROR_BUFFER_OVERFLOW
                    return 36;
                case 123: // ERROR_INVALID_NAME
                case 87:  // ERROR_INVALID_PARAMETER
                case 131: // ERROR_NEGATIVE_SEEK
                    return 22;
                case 6:   // ERROR_INVALID_HANDLE
                    return 9;
                case 14:  // ERROR_OUTOFMEMORY
                    return 12;
                case 50:  // ERROR_NOT_SUPPORTED
                    return 38;
                case 1920: // ERROR_CANT_ACCESS_FILE（断链的跟随打开）
                    return 2;
                case 29:  // ERROR_WRITE_FAULT
                case 30:  // ERROR_READ_FAULT
                case 31:  // ERROR_GEN_FAILURE
                    return 5;
                default:
                    return 1000;
            }
        }

        // i64 小端写入出参 Span（与 rigi_rt fs.c rigi_fs_write_i64le
        // 同布局；out 长度由 Rigi 层保证 ≥ 8，元素恒 VmU8）
        private static void WriteFsI64Le(VmSpan span, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                span.Elements[i] = new VmU8((byte)(value >> (i * 8)));
            }
        }

        // fs_open（同步直调，Linux x64 用 O_NONBLOCK + 已打开 fd 的
        // fstat 防 FIFO 无对端占住 Worker）：flags 位定义与 rigi_rt
        // fs.c RIGI_FS_F_* 逐位一致；
        // bufferSize 1 = 近无缓冲（对齐 native 直读直写语义）；FileShare
        // ReadWrite|Delete = §4.5.6「默认允许其他进程读写，以及平台支持
        // 的重命名或删除」
        internal VmValue FsOpen(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 4 || args[1] is not VmI32 flags
                || args[2] is not VmI32 mode)
            {
                throw new VmException("fs_open 需要 (String, i32, i32, Span<u8>)");
            }
            var pathValue = args[0] is VmAny pathAny ? pathAny.Payload : args[0];
            if (pathValue is not VmString path)
            {
                throw new VmException("fs_open 第一参数必须是 String");
            }
            var outValue = args[3] is VmAny outAny ? outAny.Payload : args[3];
            if (outValue is not VmSpan outSpan || outSpan.ElementType != ".u8")
            {
                throw new VmException("fs_open 第四参数必须是 Span<u8>");
            }
            const int FRead = 0x1, FAppend = 0x4, FCreate = 0x8,
                FTruncate = 0x10, FCreateNew = 0x20;
            var f = flags.Value;
            FileMode fileMode;
            if ((f & FCreateNew) != 0) { fileMode = FileMode.CreateNew; }
            else if ((f & FCreate) != 0 && (f & FTruncate) != 0)
            {
                fileMode = FileMode.Create;
            }
            else if ((f & FCreate) != 0 && (f & FAppend) != 0)
            {
                fileMode = FileMode.Append;
            }
            else if ((f & FCreate) != 0) { fileMode = FileMode.OpenOrCreate; }
            else { fileMode = FileMode.Open; }
            var access = (f & FRead) != 0 ? FileAccess.Read : FileAccess.Write;
            FileStream? stream = null;
            var registered = false;
            try
            {
                if (fileMode == FileMode.Append)
                {
                    // 追加专用句柄保留内核 O_APPEND 原子性。
                    var rc = OpenAppendStream(path.Value, out stream);
                    if (rc != 0) { return new VmI32(rc); }
                }
                else if (IsFsLinuxX64())
                {
                    // Linux FIFO 的只读/只写 open 均可能等待对端；用
                    // O_NONBLOCK 打开，再按已打开 fd 判定普通文件。
                    var rc = OpenLinuxRegularStream(path.Value, f, mode.Value,
                        out stream);
                    if (rc != 0) { return new VmI32(rc); }
                }
                else
                {
                    if (OperatingSystem.IsLinux())
                    {
                        // 其它 Linux 架构的 openat/fstat ABI 尚未验证，
                        // 不能回退到可能同步等待 FIFO 的 FileStream。
                        return new VmI32(-FsErrnoEnosys);
                    }
                    stream = new FileStream(path.Value, fileMode, access,
                        FileShare.ReadWrite | FileShare.Delete, 1);
                }
                long token;
                lock (_nativeRcGate)
                {
                    token = NewHandle();
                    _nativeRcStrong[token] = 1;
                    lock (_fsGate)
                    {
                        _fsFiles[token] = new VmFsFile
                        {
                            Resource = stream!, // rc==0 蕴含非 null
                            AppendMode = fileMode == FileMode.Append,
                        };
                    }
                }
                registered = true;
                WriteFsI64Le(outSpan, token);
                return new VmI32(0);
            }
            catch (UnauthorizedAccessException)
            {
                // 目录当文件开：与 rigi_fs_open 的 Windows 补救同口径
                //（.NET 对目录开 FileStream 抛 UnauthorizedAccessException；
                // 失败路径补查，仅错误路径非「先查询再打开」）
                if (Directory.Exists(path.Value)) { return new VmI32(-21); }
                return new VmI32(-13);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
            finally
            {
                // 登记失败（异常/返回错误）时句柄不放漏——正常/异常路径
                // 都不泄漏 OS 资源
                if (!registered && stream != null) { stream.Dispose(); }
            }
        }

        private static bool IsFsLinuxX64() => OperatingSystem.IsLinux()
            && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64;

        // Linux x64 普通文件入口：非阻塞 openat 防 FIFO 等待，随后仅凭
        // fd 的完整 fstat 判型。FIFO 无读者/AF_UNIX socket 的 ENXIO
        // 没有 fd；失败路径用 O_PATH 新句柄判型，其余错误保留原 errno。
        private static int OpenLinuxRegularStream(string path, int flags,
            int mode, out FileStream? stream)
        {
            stream = null;
            const int fWrite = 0x2, fCreate = 0x8, fTruncate = 0x10,
                fCreateNew = 0x20;
            var write = (flags & fWrite) != 0;
            var oflags = (write ? FsO_WRONLY : 0) | FsO_NONBLOCK;
            if ((flags & fCreateNew) != 0) { oflags |= FsO_CREAT | FsO_EXCL; }
            else
            {
                if ((flags & fCreate) != 0) { oflags |= FsO_CREAT; }
                if ((flags & fTruncate) != 0) { oflags |= FsO_TRUNC; }
            }
            int fd;
            try
            {
                fd = OpenAt(FsAtFdcwd, path, oflags, mode);
                if (fd < 0)
                {
                    var e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (e == FsErrnoEnxio
                        && ProbeLinuxNonRegular(path)) { return -FsWrongType; }
                    return -MapFsErrno(e);
                }
            }
            catch (DllNotFoundException) { return -FsErrnoEnosys; }
            catch (EntryPointNotFoundException) { return -FsErrnoEnosys; }
            return WrapLinuxRegularFd(fd, write ? FileAccess.Write : FileAccess.Read,
                out stream);
        }

        private static bool ProbeLinuxNonRegular(string path)
        {
            // 仅 ENXIO 失败时另开 O_PATH 句柄，既可判 socket 也可判
            // FIFO；不能用 pathname stat 代替句柄校验，也不能当作
            // 原读写句柄使用。打开/查询失败保留原始 ENXIO。
            int fd;
            try { fd = OpenAt(FsAtFdcwd, path, FsO_PATH | FsO_CLOEXEC, 0); }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            if (fd < 0) { return false; }
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                (IntPtr)fd, ownsHandle: true);
            try
            {
                if (fstat(fd, out var stat) != 0) { return false; }
                var kind = stat.Mode & FsSIfmt;
                return kind != FsSIfreg && kind != FsSIfdir;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        private static int WrapLinuxRegularFd(int fd, FileAccess access,
            out FileStream? stream)
        {
            stream = null;
            var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                (IntPtr)fd, ownsHandle: true);
            var transferred = false;
            try
            {
                if (fstat(fd, out var stat) != 0)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                var kind = stat.Mode & FsSIfmt;
                if (kind == FsSIfdir) { return -21; }
                if (kind != FsSIfreg) { return -FsWrongType; }
                var current = Fcntl(fd, FsFGetfl, 0);
                if (current < 0 || Fcntl(fd, FsFSetfl,
                    current & ~FsO_NONBLOCK) < 0)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                // 仅构造成功才将 SafeFileHandle 所有权交给 FileStream。
                stream = new FileStream(handle, access, 1);
                transferred = true;
                return 0;
            }
            catch (DllNotFoundException) { return -FsErrnoEnosys; }
            catch (EntryPointNotFoundException) { return -FsErrnoEnosys; }
            finally { if (!transferred) { handle.Dispose(); } }
        }

        // 打开 OS 追加专用文件流（§4.5.6「系统追加机制」的 VM 面唯一
        // 收口）。证据基线（playground/vm_append_atomic/ 探针实证）：
        // .NET FileMode.Append 两平台都不是系统追加——Windows 句柄
        // GENERIC_WRITE 含 FILE_WRITE_DATA，写经 NtWriteFile 提交显式
        // ByteOffset（落到句柄位置而非当时末尾）；Linux fdinfo flags 无
        // O_APPEND（02100001），ctor 仅 Seek 一次、写走 pwrite 显式
        // offset。因此：
        //   Windows：CreateFileW 仅申请 FILE_APPEND_DATA（不含
        //     FILE_WRITE_DATA）+ FILE_READ_ATTRIBUTES（getLength 查
        //     FileStandardInformation 所需；不放开内容读）+ SYNCHRONIZE
        //     ——内核契约：无 FILE_WRITE_DATA 的 FILE_APPEND_DATA 句柄
        //     上 WriteFile 忽略句柄当前位置、每笔写落当时末尾；
        //     FlushFileBuffers/Flush(true) 对该句柄可用（flush 持久化
        //     面）。OPEN_ALWAYS = 不存在则创建、存在不截断；
        //   Linux：openat(AT_FDCWD, O_WRONLY|O_CREAT|O_APPEND|
        //   O_NONBLOCK, 0666)，非阻塞打开防 FIFO 无读者等待，随后按
        //   已打开 fd 判型并清掉 O_NONBLOCK；O_APPEND 保留，确保每次
        //   write(2) 在系统调用内原子落在当时末尾；
        //   创建权限 0o666（rw-rw-rw-）受 umask（§4.5.5，对齐 .NET
        //   FileMode.Append 的 Unix 默认与 native 面）。
        //   openat 与 open 同为 variadic（POSIX：mode 为可选变参）。
        //   当前 libc.so.6 的带 mode openat 入口仅在 Linux x64 验证；
        //   未验证平台（macOS/freebsd x64、Linux 非 x64 等）经平台闸门
        //   明确报 Unsupported（-38），不在代码里对其它架构的 variadic
        //   ABI 行为做必对/必错断言；libc.so.6 缺失或入口缺失也受控归
        //   Unsupported，不漏宿主异常出流契约。
        // 两平台返回值均由 AppendWriteCore 的系统调用内选位兑现。
        // 失败返回 -归一码（MapFsWin32/MapFsErrno 同表；Windows 目录
        // 当文件开是 ACCESS_DENIED，失败路径补查目录归 -21）
        // 契约：返回 0 时 stream 必非 null，非 0 时 stream 恒 null
        //（int 返回不适用 NotNullWhen(true)——该属性仅约定 bool 返回；
        // 调用点以 rc==0 分支为准）
        internal static int OpenAppendStream(string path,
            out FileStream? stream)
        {
            stream = null;
            if (OperatingSystem.IsWindows())
            {
                var handle = CreateFileW(path,
                    FsWinFileAppendData | FsWinFileReadAttributes
                        | FsWinSynchronize,
                    FsWinShareReadWriteDelete, IntPtr.Zero, FsWinOpenAlways,
                    FsWinFileAttributeNormal, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (err == FsWinErrorAccessDenied
                        && Directory.Exists(path))
                    {
                        return -21; // 目录当文件开（IsDir，与 native 同码）
                    }
                    return -MapFsWin32(err);
                }
                try
                {
                    stream = new FileStream(handle, FileAccess.Write, 1);
                    return 0;
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            // 非 Windows 仅限定 Linux x64（glibc）：libc.so.6 仅 glibc
            // 提供，带 mode 的 openat 入口只在 Linux x64 验证过——其余
            // 系统/架构（macOS、freebsd x64、Linux 非 x64 等）显式
            // Unsupported（-38），不把全部非 Windows 当 Linux，也不对
            // 未验证平台的 variadic ABI 行为做断言
            if (!OperatingSystem.IsLinux()
                || System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
            {
                return -FsErrnoEnosys; // Unsupported（=ENOSYS 归一同值）
            }
            int fd;
            try
            {
                fd = OpenAt(FsAtFdcwd, path,
                    FsO_WRONLY | FsO_CREAT | FsO_APPEND | FsO_NONBLOCK,
                    FsCreateMode0666);
                if (fd < 0)
                {
                    var e = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    if (e == FsErrnoEnxio && ProbeLinuxNonRegular(path))
                    {
                        return -FsWrongType;
                    }
                    return -MapFsErrno(e);
                }
            }
            catch (DllNotFoundException)
            {
                return -FsErrnoEnosys; // libc.so.6 缺失：受控 Unsupported
            }
            catch (EntryPointNotFoundException)
            {
                return -FsErrnoEnosys; // 入口缺失：受控 Unsupported
            }
            return WrapLinuxRegularFd(fd, FileAccess.Write, out stream);
        }

        // 追加写核心（§4.5.6：写入位置由 OS 在本系统调用内原子选择，
        // 到当时末尾）。返回实际写出字节数（允许短写，Rigi 层循环补齐
        // ——与 native 面单次系统调用形态一致）；错误抛 IOException
        //（HResult 低 16 位 = Win32 错误码/errno，由 MapFsError 归一）。
        // Windows WriteFile 无 overlapped = 同步句柄当前位置语义，而
        // append-only 句柄由内核强制落 EOF；Linux write(2) 由 O_APPEND
        // 强制落 EOF；EINTR（信号中断）重试——不是流错误
        internal static int AppendWriteCore(FileStream stream, byte[] buffer,
            int length)
        {
            var handle = stream.SafeFileHandle;
            if (OperatingSystem.IsWindows())
            {
                if (!WriteFile(handle, buffer, length, out var written,
                    IntPtr.Zero))
                {
                    throw new IOException("fs_write",
                        System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error());
                }
                return written;
            }
            // SafeFileHandle 租借：DangerousGetHandle 取原始 fd 的调用
            // 期间显式 AddRef/Release——阻断 JIT 在原生 write 执行期间
            // 把句柄判不可达而提前终结关闭 fd 的窗口（SafeFileHandle
            // 官方模式；Windows 分支不需此步——WriteFile 直接收
            // SafeFileHandle，编组层自动保活）
            var refAdded = false;
            try
            {
                handle.DangerousAddRef(ref refAdded);
                var fd = (int)handle.DangerousGetHandle();
                long n;
                do
                {
                    n = Write(fd, buffer, (IntPtr)length);
                } while (n < 0 && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == FsErrnoEintr);
                if (n < 0)
                {
                    throw new IOException("fs_write",
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                }
                return (int)n;
            }
            finally
            {
                if (refAdded) { handle.DangerousRelease(); }
            }
        }

        // ===== fs 追加互操作（仅上两函数使用；标量 + byte[] + string，
        // 无 struct 编组、无 unsafe——winnt.h 常量见逐条注释）=====
        private const int FsWinFileAppendData = 0x0004;   // FILE_APPEND_DATA
        private const int FsWinFileReadAttributes = 0x0080; // FILE_READ_ATTRIBUTES
        private const int FsWinSynchronize = 0x00100000;  // SYNCHRONIZE
        private const int FsWinShareReadWriteDelete = 0x7; // FILE_SHARE_READ|WRITE|DELETE
        private const int FsWinOpenAlways = 4;            // OPEN_ALWAYS
        private const int FsWinFileAttributeNormal = 0x80; // FILE_ATTRIBUTE_NORMAL
        private const int FsWinErrorAccessDenied = 5;     // ERROR_ACCESS_DENIED
        private const int FsAtFdcwd = -100;               // AT_FDCWD（openat 相对 cwd）
        private const int FsO_WRONLY = 0x1;
        private const int FsO_CREAT = 0x40;
        private const int FsO_EXCL = 0x80;
        private const int FsO_TRUNC = 0x200;
        private const int FsO_APPEND = 0x400;             // 0o2000
        private const int FsO_NONBLOCK = 0x800;           // 0o4000，仅打开阶段
        private const int FsO_CLOEXEC = 0x80000;          // 0o2000000，补查句柄不泄漏
        private const int FsO_PATH = 0x200000;            // 0o10000000，仅 ENXIO 补查
        private const int FsFGetfl = 3, FsFSetfl = 4;     // fcntl F_GETFL/F_SETFL
        private const uint FsSIfmt = 0xF000;              // S_IFMT
        private const uint FsSIfifo = 0x1000, FsSIfdir = 0x4000,
            FsSIfreg = 0x8000;
        private const int FsErrnoEnxio = 6;
        private const int FsWrongType = 1002;             // 自定义归一码 → WrongType
        private const int FsCreateMode0666 = 0x1B6;       // 0o666=438（rw-rw-rw-，受 umask；§4.5.5）
        private const int FsErrnoEintr = 4;               // EINTR
        private const int FsErrnoEinval = 22;             // EINVAL（宿主不认识不替换原语 → Unsupported）
        private const int FsErrnoEnosys = 38;             // ENOSYS（Unsupported 归一码同值）
        private const uint FsRenameNoReplace = 1u;        // RENAME_NOREPLACE（<linux/fs.h>；不依赖 _GNU_SOURCE）

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
            string lpFileName, int dwDesiredAccess, int dwShareMode,
            IntPtr lpSecurityAttributes, int dwCreationDisposition,
            int dwFlagsAndAttributes, IntPtr hTemplateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool WriteFile(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer,
            int nNumberOfBytesToWrite, out int lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        // openat 与 open 同为 variadic（POSIX：mode 为可选变参）。
        // 带 mode 入口仅在 Linux x64 + glibc 验证；调用点由
        // OpenAppendStream/OpenLinuxRegularStream 的平台闸门与异常捕获
        // 受控捕获约束（缺库/缺入口归 Unsupported，不漏宿主异常）
        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int dirfd, string pathname,
            int flags, int mode);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "fcntl", SetLastError = true)]
        private static extern int Fcntl(int fd, int command, int value);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "write", SetLastError = true)]
        private static extern long Write(int fd, byte[] buf, IntPtr count);

        // renameat2（Linux；glibc ≥ 2.28 导出 renameat2 符号）：固定参数
        // 原型，无 openat 的 variadic ABI 假设，不设架构闸门（与 native
        // 面 SYS_renameat2 无架构限制同口径）；路径为 NUL 结尾字节串，
        // CharSet.Ansi 在 Unix 编组层即 UTF-8，与 OpenAt 先例同口径。
        // AT_FDCWD 双路径在单次系统调用内以同一 cwd 基准解析
        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "renameat2", SetLastError = true)]
        private static extern int RenameAt2(int oldDirFd, string oldPath,
            int newDirFd, string newPath, uint flags);

        // Unix Replace 非目录源只允许系统 rename：不可使用 File.Move 的
        // EXDEV 复制+删除回退（违背 §4.5.7 跨设备报错）。
        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "rename", SetLastError = true)]
        private static extern int RenameUnix(string oldPath, string newPath);

        // renameat2(RENAME_NOREPLACE) helper：返回 0 成功 / -归一码。
        // 内核在单次系统调用内原子完成「目标不存在检查 + 移动」裁决
        //（§4.5.7 系统保证，不用 exists + rename 模拟），文件、真实目
        // 录、符号链接/断链条目一视同仁（rename 系不跟随末段链接）。
        // errno 读取紧跟失败：ENOSYS/EINVAL/EOPNOTSUPP（内核或文件系
        // 统不提供该保证，EINVAL 对齐 native 特判——宿主不认识该原语
        // 不伪装成路径错）→ Unsupported；EXDEV → CrossDevice（不退化
        // 复制删除）；其余 MapFsErrno 同表。错误原样报告、不隐式重试
        // 或回退（与现有 native 面同口径；本任务不重写异步 IO 层）。
        // 缺库/缺入口受控 Unsupported
        private static int RenameNoReplaceLinux(string src, string dst)
        {
            try
            {
                if (RenameAt2(FsAtFdcwd, src, FsAtFdcwd, dst,
                        FsRenameNoReplace) == 0)
                {
                    return 0;
                }
                var errno = System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error();
                if (errno == FsErrnoEinval || errno == FsErrnoEnosys)
                {
                    return -FsErrnoEnosys;
                }
                return -MapFsErrno(errno); // EOPNOTSUPP 95 → 38 同表
            }
            catch (DllNotFoundException)
            {
                return -FsErrnoEnosys; // libc.so.6 缺失：受控 Unsupported
            }
            catch (EntryPointNotFoundException)
            {
                return -FsErrnoEnosys; // 入口缺失：受控 Unsupported
            }
        }

        // Replace 非目录源：普通 rename 原子覆盖文件/链接；跨设备原样
        // EXDEV，绝不经 BCL File.Move 的复制+删除回退。仅 Linux 可用。
        private static int RenameReplaceFileLinux(string src, string dst)
        {
            try
            {
                if (RenameUnix(src, dst) == 0)
                {
                    return 0;
                }
                var errno = System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error();
                if (errno == 20 || errno == 21 || errno == 39)
                {
                    // 仅在系统调用失败后分类目录目标，不以查询保证安全。
                    try
                    {
                        var attrs = File.GetAttributes(dst);
                        if ((attrs & FileAttributes.Directory) != 0
                            && (attrs & FileAttributes.ReparsePoint) == 0)
                        {
                            return -21;
                        }
                    }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException or ArgumentException)
                    {
                        // 并发目标改变时保留原始系统错误。
                    }
                }
                return -MapFsErrno(errno);
            }
            catch (DllNotFoundException)
            {
                return -FsErrnoEnosys;
            }
            catch (EntryPointNotFoundException)
            {
                return -FsErrnoEnosys;
            }
        }

        // fs_read_start（挂起读启动即返，stdin 先例直复刻）：把阻塞读
        // 交给专用后台线程（绝不在 Worker/解释线程上同步阻塞），完成
        // 写回 Span 并登记结果后经 EventSignalCore 触发事件——挂起协程
        // 由 §19.3 原子握手唤醒。EOF 不粘滞（§4.5.6：读到当前 EOF 返回
        // 0，之后再次读取可以看到新增内容），每轮 start 都真读
        internal VmValue FsReadStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 5 || args[0] is not VmI64 handle
                || args[2] is not VmI32 offset || args[3] is not VmI32 count
                || args[4] is not VmI64 wake)
            {
                throw new VmException(
                    "fs_read_start 需要 (i64, Span<u8>, i32, i32, i64)");
            }
            var value = args[1] is VmAny any ? any.Payload : args[1];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("fs_read_start 第二参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("fs_read_start 区间越界：offset=" + start
                    + " count=" + length + " 长度=" + span.Length);
            }
            VmFsFile file;
            FileStream stream;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out file!))
                {
                    throw new VmException("fs_read_start 无效句柄");
                }
                if (file.ReadInflight)
                {
                    throw new VmException(
                        "fs_read_start：已有在途读（并发/重入违反契约）");
                }
                stream = RequireFsStream(file, "fs_read_start");
                file.ReadInflight = true;
            }
            // 专用后台线程（非线程池——阻塞读可能占住线程任意久；
            // IsBackground 保证进程退出不被拖累）。Span 挂起期间借用
            //（§3.2：调用协程挂起持有引用，后台线程写入无回收风险——
            // stdin Span 借用先例）
            var reader = new Thread(
                () => FsReadBody(file, stream, span, start, length, wake.Value))
            {
                IsBackground = true,
                Name = "rigi-fs-read",
            };
            reader.Start();
            return new VmI32(0);
        }

        // 后台读线程体：阻塞读一次（Stream.Read 返回 0 = 当前 EOF），
        // 写回 Span 后在闸内登记结果，再触发唤醒事件（登记先于
        // signal——恢复协程必见结果；native 面同序）
        private void FsReadBody(VmFsFile file, FileStream stream,
            VmSpan span, int start, int length, long wake)
        {
            int n;
            var buffer = new byte[length];
            try
            {
                n = stream.Read(buffer, 0, length);
                if (n < 0) { n = 0; }
            }
            catch (Exception ex)
            {
                // I/O 失败登记负归一码（Rigi 层映射 FileSystemException；
                // 与 native 面「结果登记先于事件触发」同序）
                lock (_fsGate)
                {
                    file.ReadResult = -MapFsError(ex);
                    file.ReadInflight = false;
                }
                EventSignalCore(wake);
                return;
            }
            for (var i = 0; i < n; i++)
            {
                span.Elements[start + i] = new VmU8(buffer[i]);
            }
            lock (_fsGate)
            {
                file.ReadResult = n;
                file.ReadInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue FsReadTake(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_read_take 需要 (i64)");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_read_take 无效句柄");
                }
                // take 只发生在事件唤醒之后：signal 前结果已在闸内登记，
                // 未登记即到取属时序 bug（防御诊断，正常路径不可达）
                if (file.ReadInflight)
                {
                    throw new VmException("fs_read_take：结果未就绪（时序 bug）");
                }
                return new VmI32(file.ReadResult);
            }
        }

        // ===== fs 原语族阶段 2（写/flush 挂起 + 定位/长度/信息查询/
        // 创建删除/移动/目录枚举同步；rigi_rt fs.c 镜像，双宿主同语义）=====

        // i32 小端写入出参 Span（dirread/realpath 的 meta 协议）
        private static void WriteFsI32Le(VmSpan span, int offset, int value)
        {
            for (var i = 0; i < 4; i++)
            {
                span.Elements[offset + i] =
                    new VmU8((byte)(value >> (i * 8)));
            }
        }

        private static VmString RequireFsString(string hook,
            IReadOnlyList<VmValue> args, int index)
        {
            if (args.Count <= index)
            {
                throw new VmException(hook + "：参数不足");
            }
            var v = args[index] is VmAny any ? any.Payload : args[index];
            if (v is not VmString s)
            {
                throw new VmException(hook + "：参数 " + index
                    + " 必须是 String");
            }
            return s;
        }

        private static VmSpan RequireFsSpan(string hook,
            IReadOnlyList<VmValue> args, int index)
        {
            if (args.Count <= index)
            {
                throw new VmException(hook + "：参数不足");
            }
            var v = args[index] is VmAny any ? any.Payload : args[index];
            if (v is not VmSpan s || s.ElementType != ".u8")
            {
                throw new VmException(hook + "：参数 " + index
                    + " 必须是 Span<u8>");
            }
            return s;
        }

        private long RequireFsHandle(string hook, IReadOnlyList<VmValue> args)
        {
            if (args.Count < 1 || args[0] is not VmI64 handle)
            {
                throw new VmException(hook + "：第一参数需要 i64 句柄");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.ContainsKey(handle.Value))
                {
                    throw new VmException(hook + " 无效句柄");
                }
            }
            return handle.Value;
        }

        // fs_write_start（挂起写，read 同款两段式）：buffer[start..start+
        // count) 卸载到专用后台线程，完成后写回登记并触发事件。
        // 非追加 = Stream.Write 全量语义（返回写满的 count）；追加 =
        // AppendWriteCore 单次系统调用（允许短写，两者 Rigi 层 7-3 流层
        // 均循环补齐——正常本地文件写实际全量）
        internal VmValue FsWriteStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 5 || args[0] is not VmI64 handle
                || args[2] is not VmI32 offset || args[3] is not VmI32 count
                || args[4] is not VmI64 wake)
            {
                throw new VmException(
                    "fs_write_start 需要 (i64, Span<u8>, i32, i32, i64)");
            }
            var value = args[1] is VmAny any ? any.Payload : args[1];
            if (value is not VmSpan span || span.ElementType != ".u8")
            {
                throw new VmException("fs_write_start 第二参数必须是 Span<u8>");
            }
            var start = offset.Value;
            var length = count.Value;
            if (start < 0 || length < 0 || start > span.Length - length)
            {
                throw new VmException("fs_write_start 区间越界：offset="
                    + start + " count=" + length + " 长度=" + span.Length);
            }
            VmFsFile file;
            FileStream stream;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out file!))
                {
                    throw new VmException("fs_write_start 无效句柄");
                }
                if (file.WriteInflight)
                {
                    throw new VmException(
                        "fs_write_start：已有在途写（并发/重入违反契约）");
                }
                stream = RequireFsStream(file, "fs_write_start");
                file.WriteInflight = true;
            }
            var writer = new Thread(
                () => FsWriteBody(file, stream, span, start, length,
                    wake.Value))
            {
                IsBackground = true,
                Name = "rigi-fs-write",
            };
            writer.Start();
            return new VmI32(0);
        }

        // 后台写线程体：阻塞写一次（全量或抛），写回登记后触发事件
        //（登记先于 signal——恢复协程必见结果，native 面同序）
        private void FsWriteBody(VmFsFile file, FileStream stream,
            VmSpan span, int start, int length, long wake)
        {
            var buffer = new byte[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = ((VmU8)span.Elements[start + i]).Value;
            }
            int n;
            try
            {
                if (file.AppendMode)
                {
                    // §4.5.6 系统追加：写入位置由 OS 在本系统调用内原子
                    // 选择到当时末尾（append-only 句柄/O_APPEND，见
                    // OpenAppendStream 证据注释）——不能用用户态
                    // Seek(End)+Write 模拟：两步之间其他进程/句柄可增长
                    // 文件，本笔会落在过期位置覆盖其数据（探针
                    // playground/vm_append_atomic 固定交错复现）。返回
                    // 实际写出字节（允许短写，Rigi 层循环补齐）
                    n = AppendWriteCore(stream, buffer, length);
                }
                else
                {
                    stream.Write(buffer, 0, length);
                    n = length;
                }
            }
            catch (Exception ex)
            {
                lock (_fsGate)
                {
                    file.WriteResult = -MapFsError(ex);
                    file.WriteInflight = false;
                }
                EventSignalCore(wake);
                return;
            }
            lock (_fsGate)
            {
                file.WriteResult = n;
                file.WriteInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue FsWriteTake(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_write_take 需要 (i64)");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_write_take 无效句柄");
                }
                if (file.WriteInflight)
                {
                    throw new VmException(
                        "fs_write_take：结果未就绪（时序 bug）");
                }
                return new VmI32(file.WriteResult);
            }
        }

        // fs_flush_start（挂起 flush）：Flush(flushToDisk: true) 等价
        // FlushFileBuffers（§4.5.6 系统持久化刷新，非库缓冲提交）
        internal VmValue FsFlushStart(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle
                || args[1] is not VmI64 wake)
            {
                throw new VmException("fs_flush_start 需要 (i64, i64)");
            }
            VmFsFile file;
            FileStream stream;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out file!))
                {
                    throw new VmException("fs_flush_start 无效句柄");
                }
                if (file.FlushInflight)
                {
                    throw new VmException(
                        "fs_flush_start：已有在途 flush（并发/重入违反契约）");
                }
                stream = RequireFsStream(file, "fs_flush_start");
                file.FlushInflight = true;
            }
            var flusher = new Thread(() => FsFlushBody(file, stream,
                wake.Value))
            {
                IsBackground = true,
                Name = "rigi-fs-flush",
            };
            flusher.Start();
            return new VmI32(0);
        }

        private void FsFlushBody(VmFsFile file, FileStream stream, long wake)
        {
            int rc;
            try
            {
                stream.Flush(true);
                rc = 0;
            }
            catch (Exception ex)
            {
                rc = -MapFsError(ex);
            }
            lock (_fsGate)
            {
                file.FlushResult = rc;
                file.FlushInflight = false;
            }
            EventSignalCore(wake);
        }

        internal VmValue FsFlushTake(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 1 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_flush_take 需要 (i64)");
            }
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_flush_take 无效句柄");
                }
                if (file.FlushInflight)
                {
                    throw new VmException(
                        "fs_flush_take：结果未就绪（时序 bug）");
                }
                return new VmI32(file.FlushResult);
            }
        }

        // fs_seek：whence 0=Begin 1=Current 2=End（SeekOrigin 同值）；out
        // 写新绝对位置。结果位置为负 → 宿主错误归 22（Rigi 层按范围错误
        // 抛 OutOfBoundException，§4.5.9）
        internal VmValue FsSeek(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 4 || args[0] is not VmI64 handle
                || args[1] is not VmI64 offset || args[2] is not VmI32 whence)
            {
                throw new VmException("fs_seek 需要 (i64, i64, i32, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_seek", args, 3);
            var w = whence.Value;
            if (w < 0 || w > 2)
            {
                return new VmI32(-22);
            }
            try
            {
                var stream = RequireFsStream(FsFileOf(handle.Value, "fs_seek"),
                    "fs_seek");
                var newPos = stream.Seek(offset.Value, (SeekOrigin)w);
                WriteFsI64Le(outSpan, newPos);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        private VmFsFile FsFileOf(long handle, string hook)
        {
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle, out var file))
                {
                    throw new VmException(hook + " 无效句柄");
                }
                return file;
            }
        }

        internal VmValue FsTell(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_tell 需要 (i64, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_tell", args, 1);
            try
            {
                var stream = RequireFsStream(FsFileOf(handle.Value, "fs_tell"),
                    "fs_tell");
                WriteFsI64Le(outSpan, stream.Position);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        internal VmValue FsGetLength(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_get_length 需要 (i64, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_get_length", args, 1);
            try
            {
                var stream = RequireFsStream(
                    FsFileOf(handle.Value, "fs_get_length"), "fs_get_length");
                WriteFsI64Le(outSpan, stream.Length);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // fs_set_length：缩短截断/增长补零，成功后游标保持不变——即使已
        // 在新末尾之后（§4.5.6 明文契约）。增长部分显式写零：不假定宿主
        // SetLength 保证扩展区域内容（.NET 依赖 NTFS 语义，FAT 不保证）
        internal VmValue FsSetLength(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 2 || args[0] is not VmI64 handle
                || args[1] is not VmI64 length)
            {
                throw new VmException("fs_set_length 需要 (i64, i64)");
            }
            if (length.Value < 0)
            {
                return new VmI32(-22); // 负长度：Rigi 层先行拦截的防御
            }
            try
            {
                var stream = RequireFsStream(
                    FsFileOf(handle.Value, "fs_set_length"), "fs_set_length");
                var pos = stream.Position;
                var oldLen = stream.Length;
                if (length.Value > oldLen)
                {
                    stream.Seek(oldLen, SeekOrigin.Begin);
                    var zeros = new byte[4096];
                    var remain = length.Value - oldLen;
                    while (remain > 0)
                    {
                        var chunk = (int)Math.Min(zeros.Length, remain);
                        stream.Write(zeros, 0, chunk);
                        remain -= chunk;
                    }
                }
                stream.SetLength(length.Value);
                stream.Seek(pos, SeekOrigin.Begin);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or NotSupportedException or ArgumentException
                or OutOfMemoryException or ObjectDisposedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // DateTime → (epoch 毫秒 i64, 纳秒余量 i32) 小端写入（Ticks 为
        // 0 = Windows 零 FILETIME「不可得」→ 哨兵；1970 前的负值按向下
        // 取整分解保证纳秒余量恒非负——TimeStamp 的 0..999999 约束）
        private static void WriteFsTimeLe(VmSpan span, int offset, DateTime t)
        {
            long ms;
            int ns;
            if (t.Ticks == 0)
            {
                ms = long.MinValue;
                ns = 0;
            }
            else
            {
                var epoch100 = t.Ticks - 621355968000000000L;
                ms = Math.DivRem(epoch100, 10000, out var rem);
                if (rem < 0)
                {
                    ms -= 1;
                    rem += 10000;
                }
                ns = (int)(rem * 100);
            }
            WriteFsI64LeAt(span, offset, ms);
            WriteFsI32Le(span, offset + 8, ns);
        }

        // 定点 i64 小端写入（与 WriteFsI64Le 同布局，offset 变体）
        private static void WriteFsI64LeAt(VmSpan span, int offset, long value)
        {
            for (var i = 0; i < 8; i++)
            {
                span.Elements[offset + i] = new VmU8((byte)(value >> (i * 8)));
            }
        }

        // stat 结构 48 字节小端（布局 = rigi_rt fs.c RIGI_FS_STAT_SIZE：
        // kind i32 / length i64 / 三组 (毫秒 i64 + 纳秒 i32)）
        private static void WriteFsStatLe(VmSpan span, int kind, long length,
            DateTime mtime, DateTime atime, DateTime birth)
        {
            WriteFsI32Le(span, 0, kind);
            WriteFsI64LeAt(span, 4, length);
            WriteFsTimeLe(span, 12, mtime);
            WriteFsTimeLe(span, 24, atime);
            WriteFsTimeLe(span, 36, birth);
        }

        // POSIX 秒/纳秒保持原始精度，负 epoch 也采用向下取整。
        private static void WriteFsPosixTimeLe(VmSpan span, int offset,
            long seconds, long nanos)
        {
            var ms = checked(seconds * 1000 + nanos / 1_000_000);
            WriteFsI64LeAt(span, offset, ms);
            WriteFsI32Le(span, offset + 8, (int)(nanos % 1_000_000));
        }

        // Linux x64 主体必须是同一次 stat/lstat 快照：类型、时间与身份
        // 来自同一结构。statx 只是辅助 birth 查询；二次路径查询若换了
        // inode/设备或无 BTIME mask，不覆盖成功主体，也绝不借 ctime 代替。
        private static int FsLinuxStat(string path, bool follow, VmSpan output)
        {
            if (path.Length == 0 || path.Contains('\0')) { return -22; }
            // 两次查询共用调用时的 cwd 基准；不规范化 ..，以免穿越链接。
            var absolute = Path.IsPathFullyQualified(path) ? path
                : Path.Combine(Environment.CurrentDirectory, path);
            try
            {
                var rc = follow ? FsLibcStat(absolute, out var body)
                    : FsLibcLstat(absolute, out body);
                if (rc != 0)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                var mode = body.Mode & 0xF000U;
                var kind = mode == 0xA000 ? 2 : mode == 0x4000 ? 1
                    : mode == 0x8000 ? 0 : 3;
                WriteFsI32Le(output, 0, kind);
                WriteFsI64LeAt(output, 4, kind == 0 ? (long)body.Size : -1);
                WriteFsPosixTimeLe(output, 12, (long)body.MtimSec,
                    (long)body.MtimNsec);
                WriteFsPosixTimeLe(output, 24, (long)body.AtimSec,
                    (long)body.AtimNsec);
                WriteFsI64LeAt(output, 36, long.MinValue);
                WriteFsI32Le(output, 44, 0);
                try
                {
                    // libc statx(2)；不可用/失败只表示 birth 不可得。
                    if (FsLibcStatx(-100, absolute, follow ? 0 : 0x100,
                            0x800, out var extra) == 0
                        && (extra.Mask & 0x800) != 0
                        && extra.DevMajor == FsLinuxDevMajor(body.Dev)
                        && extra.DevMinor == FsLinuxDevMinor(body.Dev)
                        && extra.Ino == body.Ino
                        && extra.BirthNsec < 1_000_000_000)
                    {
                        WriteFsPosixTimeLe(output, 36, extra.BirthSec,
                            extra.BirthNsec);
                    }
                }
                catch (DllNotFoundException) { /* 无 statx：仅 birth 不可得 */ }
                catch (EntryPointNotFoundException) { /* 旧 libc：同上 */ }
                return 0;
            }
            catch (DllNotFoundException) { return -38; }
            catch (EntryPointNotFoundException) { return -38; }
        }

        // Linux dev_t 的 glibc major/minor 位展开（sys/sysmacros.h）。
        private static uint FsLinuxDevMajor(ulong dev) =>
            (uint)(((dev >> 8) & 0xfff) | ((dev >> 32) & ~0xfffUL));
        private static uint FsLinuxDevMinor(ulong dev) =>
            (uint)((dev & 0xff) | ((dev >> 12) & ~0xffUL));

        // Linux 内核 struct statx：固定 256 字节，不能用字段前缀 out
        // 参数接收（内核会写满完整结构）。偏移由 C sizeof/offsetof 探针
        // 实测：mask@0、ino@32、btime.tv_sec@80、tv_nsec@88、
        // dev_major@136、dev_minor@140；仅用于已核实的 Linux x64。
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Explicit, Size = 256)]
        internal struct FsLinuxX64Statx
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public uint Mask;
            [System.Runtime.InteropServices.FieldOffset(32)] public ulong Ino;
            [System.Runtime.InteropServices.FieldOffset(80)] public long BirthSec;
            [System.Runtime.InteropServices.FieldOffset(88)] public uint BirthNsec;
            [System.Runtime.InteropServices.FieldOffset(136)] public uint DevMajor;
            [System.Runtime.InteropServices.FieldOffset(140)] public uint DevMinor;
        }

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "stat", SetLastError = true)]
        private static extern int FsLibcStat(
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
            string path, out FsLinuxX64Stat stat);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "lstat", SetLastError = true)]
        private static extern int FsLibcLstat(
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
            string path, out FsLinuxX64Stat stat);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "statx", SetLastError = true)]
        private static extern int FsLibcStatx(int dirfd,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)]
            string path, int flags, uint mask, out FsLinuxX64Statx statx);

        // fs_stat（跟随末段链接）/ fs_lstat（只查询末段链接本身）：File
        // .GetAttributes 不跟随末段（Win32 GetFileAttributesW 语义；Unix
        // .NET 6+ lstat 语义，symlink 报 ReparsePoint 位）——lstat 直接
        // 采用；stat 遇末段链接经 ResolveLinkTarget(final) 解析（断链 →
        // NotFound）。时间/长度经 FileSystemInfo（同为不跟随末段语义）。
        // Windows 侧 reparse tag 不细分（junction/symlink/未知统一 Link
        // 提示）；native 侧细分（junction/symlink → Link、未知 → Other）
        // ——语料不构造未知 reparse 对象，公共契约不受影响
        private VmValue FsStatImpl(IReadOnlyList<VmValue> args, bool follow,
            string hook)
        {
            var path = RequireFsString(hook, args, 0).Value;
            var outSpan = RequireFsSpan(hook, args, 1);
            try
            {
                if (OperatingSystem.IsLinux()
                    && System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture == System.Runtime.InteropServices
                            .Architecture.X64)
                {
                    return new VmI32(FsLinuxStat(path, follow, outSpan));
                }
                var attrs = File.GetAttributes(path);
                var current = path;
                if (follow && (attrs & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo link = Directory.Exists(path)
                        ? new DirectoryInfo(path)
                        : new FileInfo(path);
                    var final = link.ResolveLinkTarget(
                        returnFinalTarget: true);
                    if (final == null)
                    {
                        return new VmI32(-2); // 断链：跟随目标不可达
                    }
                    current = final.FullName;
                    attrs = File.GetAttributes(current);
                }
                var isReparse = (attrs & FileAttributes.ReparsePoint) != 0;
                var isDir = (attrs & FileAttributes.Directory) != 0;
                var kind = isReparse ? 2 : isDir ? 1 : 0;
                FileSystemInfo fsi = isDir
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                fsi.Refresh();
                var length = kind == 0 && fsi is FileInfo fi ? fi.Length : -1;
                WriteFsStatLe(outSpan, kind, length, fsi.LastWriteTimeUtc,
                    fsi.LastAccessTimeUtc, OperatingSystem.IsWindows()
                        ? fsi.CreationTimeUtc : DateTime.MinValue);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        internal VmValue FsStat(IReadOnlyList<VmValue> args)
        {
            return FsStatImpl(args, follow: true, "fs_stat");
        }

        internal VmValue FsLstat(IReadOnlyList<VmValue> args)
        {
            return FsStatImpl(args, follow: false, "fs_lstat");
        }

        // fs_realpath：FullName/GetFullPath 仅词法绝对化，不能解析中间
        // 目录链接，也可能错误地在链接之前消解「..」。先以一次 cwd 固定
        // 相对路径的调用基准（只组合、不整理分量），再交给系统解析完整
        // 路径：Windows 已打开句柄最终路径 / Linux libc realpath。
        // 成功时 out 为严格 UTF-8，meta[0..4) 为所需字节数；容量不足仍
        // 返回正哨兵 2，错误用负归一码，不能将循环/权限伪装 NotFound。
        internal VmValue FsRealpath(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_realpath", args, 0).Value;
            var outSpan = RequireFsSpan("fs_realpath", args, 1);
            var metaSpan = RequireFsSpan("fs_realpath", args, 2);
            try
            {
                if (path.Length == 0 || path.Contains('\0'))
                {
                    return new VmI32(-22);
                }
                var absolute = Path.IsPathFullyQualified(path)
                    ? path : Path.Combine(Environment.CurrentDirectory, path);
                var rc = FsResolveRealpath(absolute, out var resolved);
                if (rc != 0) { return new VmI32(rc); }
                byte[] bytes;
                try
                {
                    bytes = new System.Text.UTF8Encoding(false, true)
                        .GetBytes(resolved!);
                }
                catch (System.Text.EncoderFallbackException)
                {
                    return new VmI32(-1001); // 无法无损表达的名称
                }
                if (bytes.Length > outSpan.Length)
                {
                    WriteFsI32Le(metaSpan, 0, bytes.Length);
                    return new VmI32(2);
                }
                for (var i = 0; i < bytes.Length; i++)
                {
                    outSpan.Elements[i] = new VmU8(bytes[i]);
                }
                WriteFsI32Le(metaSpan, 0, bytes.Length);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException or NotSupportedException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // 返回 0/负归一码；系统调用期间由 SafeFileHandle 编组保活句柄。
        // GetFinalPathNameByHandle 的 DOS 卷名结果携内部 \\?\ 前缀，
        // 还原为用户可表达的本地盘符/UNC 路径（与 native fs.c 同形态）。
        private static int FsResolveRealpath(string absolute,
            out string? resolved)
        {
            resolved = null;
            if (OperatingSystem.IsWindows())
            {
                // 与 native fs.c 一致：长的完整盘符/UNC 路径在内部加
                // Win32 扩展前缀，绝不把该前缀作为结果路径返回。
                var openPath = absolute;
                if (absolute.Length >= 248)
                {
                    var normalized = absolute.Replace('/', '\\');
                    openPath = normalized.StartsWith(@"\\", StringComparison.Ordinal)
                        ? @"\\?\UNC\" + normalized.Substring(2)
                        : @"\\?\" + normalized;
                }
                using var handle = CreateFileW(openPath,
                    FsWinFileReadAttributes, FsWinShareReadWriteDelete,
                    IntPtr.Zero, 3 /* OPEN_EXISTING */,
                    0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS：目录 */,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                // 返回值是所需字符数（不足时含 NUL）；句柄始终保持打开。
                var count = GetFinalPathNameByHandleW(handle, IntPtr.Zero, 0, 0);
                if (count == 0)
                {
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                if (count >= 32768) { return -36; }
                while (true)
                {
                    var buffer = new char[checked((int)count + 1)];
                    var written = GetFinalPathNameByHandleW(handle, buffer,
                        (uint)buffer.Length, 0);
                    if (written == 0)
                    {
                        return -MapFsWin32(System.Runtime.InteropServices.Marshal
                            .GetLastWin32Error());
                    }
                    if (written >= buffer.Length)
                    {
                        if (written >= 32768) { return -36; }
                        count = written;
                        continue;
                    }
                    var result = new string(buffer, 0, (int)written);
                    if (result.StartsWith(@"\\?\UNC\",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = @"\\" + result.Substring(8);
                    }
                    else if (result.StartsWith(@"\\?\",
                        StringComparison.Ordinal))
                    {
                        resolved = result.Substring(4);
                    }
                    else
                    {
                        resolved = result;
                    }
                    return 0;
                }
            }
            if (!OperatingSystem.IsLinux()) { return -38; }
            try
            {
                // libc 分配的 realpath(path,NULL) 只能由 libc free 释放，
                // 绝不交给 VM/native 的追踪分配器；不使用 PtrToStringUTF8
                // 的替换解码，非法 UTF-8 必须归名称编码错误。
                var input = new System.Text.UTF8Encoding(false, true)
                    .GetBytes(absolute + "\0");
                var result = FsLibcRealpath(input, IntPtr.Zero);
                if (result == IntPtr.Zero)
                {
                    return -MapFsErrno(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                }
                try
                {
                    var length = FsLibcStrlen(result);
                    if (length > int.MaxValue - 1) { return -36; }
                    var bytes = new byte[(int)length];
                    System.Runtime.InteropServices.Marshal.Copy(result, bytes,
                        0, bytes.Length);
                    try
                    {
                        resolved = new System.Text.UTF8Encoding(false, true)
                            .GetString(bytes);
                        return 0;
                    }
                    catch (System.Text.DecoderFallbackException)
                    {
                        return -1001;
                    }
                }
                finally { FsLibcFree(result); }
            }
            catch (System.Text.EncoderFallbackException) { return -1001; }
            catch (DllNotFoundException) { return -38; }
            catch (EntryPointNotFoundException) { return -38; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            IntPtr buffer, uint length, uint flags);

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            [System.Runtime.InteropServices.Out] char[] buffer,
            uint length, uint flags);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "realpath", SetLastError = true)]
        private static extern IntPtr FsLibcRealpath(byte[] path, IntPtr resolved);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "strlen")]
        private static extern UIntPtr FsLibcStrlen(IntPtr value);

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "free")]
        private static extern void FsLibcFree(IntPtr value);

        // fs_mkdir：只创建末级，父级须存在，已存在报错（§4.5.5）；权限
        // 位 Windows 忽略（正常继承的安全描述符），Unix 走 .NET 默认
        //（0777 & umask，与 native mkdir(mode & 0777) 同宿主规则）
        internal VmValue FsMkdir(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_mkdir", args, 0).Value;
            try
            {
                Directory.CreateDirectory(path);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // fs_rmdir：只删真实空目录，不跟随末段链接删目标（§4.5.5）
        internal VmValue FsRmdir(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_rmdir", args, 0).Value;
            try
            {
                Directory.Delete(path, recursive: false);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // Windows 目录链接须由 RemoveDirectoryW 删除条目，DeleteFileW
        // 只适于文件（含文件符号链接）。属性与 tag 只用于选择非递归调用，
        // 不把未知 reparse 标签擅自归 Link；系统删除调用负责最终成败。
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool FsGetFileAttributeTag(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            int infoClass, out FsFileAttributeTagInfo info, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            EntryPoint = "RemoveDirectoryW", SetLastError = true)]
        private static extern bool FsRemoveDirectory(string path);

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct FsFileAttributeTagInfo
        {
            public uint FileAttributes;
            public uint ReparseTag;
        }

        // fs_unlink：文件/链接删除（§4.5.5），真实目录报 IsDirectory。
        internal VmValue FsUnlink(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_unlink", args, 0).Value;
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if ((attrs & FileAttributes.ReparsePoint) == 0)
                        return new VmI32(-21);
                    if (OperatingSystem.IsWindows())
                    {
                        using var handle = CreateFileW(path,
                            FsWinFileReadAttributes, FsWinShareReadWriteDelete,
                            IntPtr.Zero, 3 /* OPEN_EXISTING */,
                            0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */,
                            IntPtr.Zero);
                        if (handle.IsInvalid)
                            return new VmI32(-MapFsWin32(System.Runtime.InteropServices
                                .Marshal.GetLastWin32Error()));
                        if (!FsGetFileAttributeTag(handle, 9 /* FileAttributeTagInfo */,
                            out var tag, 8))
                            return new VmI32(-MapFsWin32(System.Runtime.InteropServices
                                .Marshal.GetLastWin32Error()));
                        // MOUNT_POINT=junction，SYMLINK=目录符号链接。
                        if (tag.ReparseTag != 0xA0000003
                            && tag.ReparseTag != 0xA000000C)
                            return new VmI32(-21);
                        // 查询句柄先关闭以免影响按路径删除；删除调用不跟随末段。
                        handle.Dispose();
                        if (!FsRemoveDirectory(path))
                            return new VmI32(-MapFsWin32(System.Runtime.InteropServices
                                .Marshal.GetLastWin32Error()));
                        return new VmI32(0);
                    }
                }
                File.Delete(path);
                return new VmI32(0);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        private static extern bool FsSetFileRenameInfoEx(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            int infoClass, byte[] info, uint size);

        // 只为 Windows 普通文件源 Replace→末段 MOUNT_POINT 分流：路径探测
        // 不锁定目标；最终仍由一次系统 rename 裁决，不能预删后再移动。
        private static int? FsTryReplaceWindowsJunction(string src, string dst)
        {
            static string NativePath(string path)
            {
                // 与 native fs.c 一致：仅绝对长盘符/UNC 路径加内部前缀。
                if (path.Length < 248 || !Path.IsPathFullyQualified(path))
                    return path;
                var normalized = path.Replace('/', '\\');
                return normalized.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? normalized
                    : normalized.StartsWith(@"\\", StringComparison.Ordinal)
                        ? @"\\?\UNC\" + normalized.Substring(2)
                        : @"\\?\" + normalized;
            }

            var targetPath = NativePath(dst);
            using (var target = CreateFileW(targetPath, FsWinFileReadAttributes,
                FsWinShareReadWriteDelete, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */,
                IntPtr.Zero))
            {
                if (target.IsInvalid)
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                if (!FsGetFileAttributeTag(target, 9 /* FileAttributeTagInfo */,
                    out var targetTag, 8))
                    return -MapFsWin32(System.Runtime.InteropServices.Marshal
                        .GetLastWin32Error());
                if ((targetTag.FileAttributes & 0x410 /* DIRECTORY|REPARSE */)
                    != 0x410)
                    return -38; // 查询后换型/标签不确定，不能借该分支覆盖。
                if (targetTag.ReparseTag == 0xA000000C /* SYMLINK */)
                    return null; // 原文件链接路径保持原有 File.Move 行为。
                if (targetTag.ReparseTag != 0xA0000003 /* MOUNT_POINT */)
                    return -38; // 未知目录 reparse 标签不可猜为 Link。
            }

            using var source = CreateFileW(NativePath(src),
                0x00010000 | FsWinFileReadAttributes /* DELETE|READ_ATTRIBUTES */,
                FsWinShareReadWriteDelete, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x00200000 /* OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (source.IsInvalid)
                return -MapFsWin32(System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error());
            if (!FsGetFileAttributeTag(source, 9 /* FileAttributeTagInfo */,
                out var sourceTag, 8))
                return -MapFsWin32(System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error());
            if ((sourceTag.FileAttributes & 0x410 /* DIRECTORY|REPARSE */) != 0)
                return null; // 链接源/目录源仍由旧入口按原类型处理。

            byte[] name;
            try
            {
                name = new System.Text.UnicodeEncoding(false, false, true)
                    .GetBytes(NativePath(dst));
            }
            catch (System.Text.EncoderFallbackException)
            {
                return -1001; // 名称编码不可无损表达。
            }
            // FILE_RENAME_INFO_EX：x64 字段偏移 0/8/16/20，x86 为
            // 0/4/8/12；尾部含 NUL，FileNameLength 只记有效 UTF-16 字节。
            var fileNameOffset = IntPtr.Size == 8 ? 20 : 12;
            if (name.Length > int.MaxValue - fileNameOffset - sizeof(char))
                return -36;
            var info = new byte[fileNameOffset + name.Length + sizeof(char)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                info.AsSpan(0, 4), 3 /* REPLACE_IF_EXISTS|POSIX_SEMANTICS */);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                info.AsSpan(fileNameOffset - 4, 4), name.Length);
            name.CopyTo(info, fileNameOffset);
            bool moved;
            int err;
            try
            {
                moved = FsSetFileRenameInfoEx(source, 22 /* FileRenameInfoEx */,
                    info, (uint)info.Length);
                err = moved ? 0 : System.Runtime.InteropServices.Marshal
                    .GetLastWin32Error(); // 必须在关闭句柄/任何补查之前捕获。
            }
            catch (DllNotFoundException) { return -38; }
            catch (EntryPointNotFoundException) { return -38; }
            if (moved) return 0;
            if (err is 1 or 50 or 87 /* INVALID_FUNCTION / NOT_SUPPORTED / INVALID_PARAMETER */)
                return -38;
            if (err == FsWinErrorAccessDenied)
            {
                try
                {
                    var attrs = File.GetAttributes(dst);
                    if ((attrs & FileAttributes.Directory) != 0
                        && (attrs & FileAttributes.ReparsePoint) == 0)
                        return -21; // 竞态换成真实目录：系统拒绝，仍归 IsDirectory。
                }
                catch (FileNotFoundException) { /* 保留原系统错误。 */ }
                catch (DirectoryNotFoundException) { /* 保留原系统错误。 */ }
            }
            return -MapFsWin32(err);
        }

        // fs_rename：replace = 0 → NoReplace（Windows MoveFileEx 不带
        // REPLACE_EXISTING = 系统不替换保证；Unix/Linux 在路径捕获后、
        // 按源类型分支之前统一走 renameat2(RENAME_NOREPLACE) 系统保证
        //——文件、真实目录、符号链接/断链条目（末段不跟随）一视同仁，
        // 不用 exists + rename 模拟（§4.5.7）；.NET File.Move/
        // Directory.Move 的 Unix 实现是 lstat 前检 + rename（BCL 源码
        // 自注 checks are not atomic），不能当保证）；1 → Replace（覆
        // 盖仅限文件/链接条目，目录目标报错，§4.5.7）。目录移动两模式
        // 都要求目标不存在（不允许目录覆盖或合并）
        internal VmValue FsRename(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 3 || args[2] is not VmI32 replace)
            {
                throw new VmException("fs_rename 需要 (String, String, i32)");
            }
            var src = RequireFsString("fs_rename", args, 0).Value;
            var dst = RequireFsString("fs_rename", args, 1).Value;
            try
            {
                // Unix NoReplace：不查询源类型，直接交系统原子裁决——
                // 不 lstat/exists 锁定目标。两路径单次系统调用同一
                // AT_FDCWD 基准解析（相对路径同 cwd，与 native 同口径）。
                // 非 Linux Unix（macOS 等）无统一不替换原语：受控
                // Unsupported，不以近似模拟承担契约（缺库/缺入口/内核
                // 或文件系统不支持由 RenameNoReplaceLinux 同口径归一）
                if (replace.Value == 0 && !OperatingSystem.IsWindows())
                {
                    if (!OperatingSystem.IsLinux())
                    {
                        return new VmI32(-FsErrnoEnosys);
                    }
                    return new VmI32(RenameNoReplaceLinux(src, dst));
                }
                var srcAttrs = File.GetAttributes(src);
                var sourceIsDirectory = (srcAttrs & FileAttributes.Directory) != 0
                    && (srcAttrs & FileAttributes.ReparsePoint) == 0;
                if (replace.Value != 0 && !OperatingSystem.IsWindows())
                {
                    if (!OperatingSystem.IsLinux())
                    {
                        return new VmI32(-FsErrnoEnosys);
                    }
                    if (sourceIsDirectory)
                    {
                        // 目录源 Replace 仍须原子阻止目标替换：Directory.Move
                        // 在 Unix 先检查再 rename，不能提供系统保证。
                        var rc = RenameNoReplaceLinux(src, dst);
                        if (rc == -17 && Directory.Exists(dst))
                        {
                            return new VmI32(-21);
                        }
                        return new VmI32(rc);
                    }
                    return new VmI32(RenameReplaceFileLinux(src, dst));
                }
                if (sourceIsDirectory)
                {
                    Directory.Move(src, dst); // Windows 既有路径
                    return new VmI32(0);
                }
                if (replace.Value != 0 && OperatingSystem.IsWindows())
                {
                    // 不存在目标沿用 File.Move；只检查末段目录 reparse
                    // 候选，避免改变普通文件、文件链接与 NoReplace 行为。
                    FileAttributes dstAttrs;
                    try { dstAttrs = File.GetAttributes(dst); }
                    catch (FileNotFoundException) { dstAttrs = 0; }
                    catch (DirectoryNotFoundException) { dstAttrs = 0; }
                    if ((dstAttrs & (FileAttributes.Directory
                        | FileAttributes.ReparsePoint))
                        == (FileAttributes.Directory | FileAttributes.ReparsePoint))
                    {
                        var special = FsTryReplaceWindowsJunction(src, dst);
                        if (special.HasValue) return new VmI32(special.Value);
                    }
                }
                File.Move(src, dst, overwrite: replace.Value != 0);
                return new VmI32(0);
            }
            catch (UnauthorizedAccessException)
            {
                // Windows Replace 遇目录目标（MoveFileEx 失败形态）：
                // 失败路径补查归 IsDirectory（native 同口径）
                if (replace.Value != 0 && Directory.Exists(dst))
                {
                    return new VmI32(-21);
                }
                return new VmI32(-13);
            }
            catch (Exception ex) when (ex is IOException
                or ArgumentException or OutOfMemoryException)
            {
                return new VmI32(-MapFsError(ex));
            }
        }

        // fs_diropen：打开即验证（native FindFirstFileW 同口径；空目录
        // 合法，不存在/非目录在打开面报错）。OS 打开目录的时机是 BCL
        // 实现细节：可发生在 DirectoryInfo/GetEnumerator 建立期（.NET 10
        // 实证双平台缺失/非目录多在 GetEnumerator 抛出，Unix 构造期即
        // openat），也可推迟到首次 MoveNext——三段同属一次「打开」的生
        // 命周期，共用同一受控异常边界（边界不变量，不按平台特判）。
        // 失败收尾：未建枚举器不释放（不空解引用），已建就地释放；只
        // 有打开+预热全部成功才分配 token 并登记，登记中途失败回滚登
        // 计并释放，绝不发布半开句柄
        internal VmValue FsDirOpen(IReadOnlyList<VmValue> args)
        {
            var path = RequireFsString("fs_diropen", args, 0).Value;
            var outSpan = RequireFsSpan("fs_diropen", args, 1);
            FileSystemInfo? first = null;
            IEnumerator<FileSystemInfo>? enumerator = null;
            try
            {
                enumerator = new DirectoryInfo(path)
                    .EnumerateFileSystemInfos().GetEnumerator();
                if (enumerator.MoveNext())
                {
                    first = enumerator.Current;
                }
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException or ArgumentException
                or OutOfMemoryException)
            {
                // 枚举器 Dispose 是纯句柄关闭不上抛（BCL 单异常模型），
                // 不遮盖主错误
                enumerator?.Dispose();
                // 路径是文件：.NET 对文件路径枚举的异常形态不固定（与
                // native ERROR_DIRECTORY→20 不一致），失败路径补查归
                // NotDirectory（open 同口径，仅错误路径补查）
                if (!Directory.Exists(path) && File.Exists(path))
                {
                    return new VmI32(-20);
                }
                return new VmI32(-MapFsError(ex));
            }
            long token = 0;
            try
            {
                lock (_nativeRcGate)
                {
                    token = NewHandle();
                    _nativeRcStrong[token] = 1;
                    lock (_fsGate)
                    {
                        _fsFiles[token] = new VmFsFile
                        {
                            Resource = new DirState
                            {
                                Enumerator = enumerator,
                                Pending = first,
                                Finished = first == null,
                            },
                        };
                    }
                }
            }
            catch
            {
                // 登记中途失败（仅 OOM 类）：回滚半登记与强引用计数并
                // 释放枚举器，异常原样上抛，不发布无主 token
                lock (_nativeRcGate)
                {
                    _nativeRcStrong.Remove(token);
                }
                lock (_fsGate)
                {
                    _fsFiles.Remove(token);
                }
                enumerator.Dispose();
                throw;
            }
            WriteFsI64Le(outSpan, token);
            return new VmI32(0);
        }

        // fs_dirread：每次交付一个条目（跳过 "."/".."——.NET 枚举器本就
        // 不产 "."/".."；不排序不递归，含隐藏项）。meta[0..4) 名称字节
        // 数、[4..8) kind 提示、[8..12) 提示有效标志；返回 0 条目 / 1
        // 结束（此后持续 1）/ 2 out 不足 / < 0 -归一码。后续条目的
        // MoveNext 是同步元数据操作（与 native readdir 同口径），在闸内
        // 完成
        internal VmValue FsDirRead(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 3 || args[0] is not VmI64 handle)
            {
                throw new VmException("fs_dirread 需要 (i64, Span, Span)");
            }
            var outSpan = RequireFsSpan("fs_dirread", args, 1);
            var metaSpan = RequireFsSpan("fs_dirread", args, 2);
            DirState state;
            lock (_fsGate)
            {
                if (!_fsFiles.TryGetValue(handle.Value, out var file))
                {
                    throw new VmException("fs_dirread 无效句柄");
                }
                state = RequireFsDir(file, "fs_dirread");
                if (state.Finished && state.Pending == null)
                {
                    return new VmI32(1);
                }
                FileSystemInfo cur;
                if (state.Pending != null)
                {
                    cur = state.Pending;
                    state.Pending = null;
                }
                else
                {
                    try
                    {
                        if (!state.Enumerator.MoveNext())
                        {
                            state.Finished = true;
                            return new VmI32(1);
                        }
                        cur = state.Enumerator.Current!;
                    }
                    catch (Exception ex) when (ex is IOException
                        or UnauthorizedAccessException or ArgumentException
                        or OutOfMemoryException)
                    {
                        return new VmI32(-MapFsError(ex));
                    }
                }
                var attrs = cur.Attributes;
                var kind = (attrs & FileAttributes.ReparsePoint) != 0 ? 2
                    : (attrs & FileAttributes.Directory) != 0 ? 1 : 0;
                var bytes = System.Text.Encoding.UTF8.GetBytes(cur.Name);
                if (bytes.Length > outSpan.Length)
                {
                    WriteFsI32Le(metaSpan, 0, bytes.Length);
                    return new VmI32(2);
                }
                for (var i = 0; i < bytes.Length; i++)
                {
                    outSpan.Elements[i] = new VmU8(bytes[i]);
                }
                WriteFsI32Le(metaSpan, 0, bytes.Length);
                WriteFsI32Le(metaSpan, 4, kind);
                WriteFsI32Le(metaSpan, 8, 1);
                return new VmI32(0);
            }
        }

        // ===== fs_same_file（施工块 7-6）：系统文件身份比较 =====
        // 与 rigi_rt fs.c rigi_fs_same_file 双宿主同语义：按已打开句柄
        // 的系统文件身份（Windows 卷序列号 + 64 位文件索引；
        // Linux st_dev + st_ino）判定两句柄是否同一文件——自复制拒绝
        // 的判定面（§4.5.7），比较不涉及路径文本。out[0] 写 1/0；返回
        // 0；< 0 = -归一码（身份查询失败按 MapFsError 映射；Linux 仅
        // x64 开放 fstat 身份读取，未验证架构/入口缺失——如旧 glibc 无
        // fstat 导出——/libc 缺失一律归 -38 Unsupported：身份不可取得
        // 时上层必须收到错误而不是「不同」，否则自复制检查形同虚设）
        internal VmValue FsSameFile(IReadOnlyList<VmValue> args)
        {
            if (args.Count != 3 || args[0] is not VmI64 ha
                || args[1] is not VmI64 hb)
            {
                throw new VmException("fs_same_file 需要 (i64, i64, Span<u8>)");
            }
            var outSpan = RequireFsSpan("fs_same_file", args, 2);
            var streamA = RequireFsStream(FsFileOf(ha.Value, "fs_same_file"),
                "fs_same_file");
            var streamB = RequireFsStream(FsFileOf(hb.Value, "fs_same_file"),
                "fs_same_file");
            bool same;
            // SafeFileHandle 租借（AppendWriteCore 同口径）：Windows 侧
            // P/Invoke 直接收 SafeFileHandle（marshaler 调用期间自动保
            // 活）；Linux 侧取原始 fd 前显式 DangerousAddRef，finally
            // 归还——两次 fstat 与全部错误/Unsupported 返回分支都被覆盖，
            // 查询失败绝不放行截断（§4.5.7）
            var safeA = streamA.SafeFileHandle;
            var safeB = streamB.SafeFileHandle;
            var keepA = false;
            var keepB = false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetFileInformationByHandle(safeA, out var ia)
                        || !GetFileInformationByHandle(safeB, out var ib))
                    {
                        return new VmI32(-MapFsWin32(
                            System.Runtime.InteropServices.Marshal
                                .GetLastWin32Error()));
                    }
                    same = ia.VolumeSerialNumber == ib.VolumeSerialNumber
                        && ia.FileIndexHigh == ib.FileIndexHigh
                        && ia.FileIndexLow == ib.FileIndexLow;
                }
                else if (OperatingSystem.IsLinux()
                    && System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture == System.Runtime.InteropServices
                            .Architecture.X64)
                {
                    // 仅 Linux x64 开放 fstat 身份读取（ABI 实证限定的
                    // 生产平台，见 FsLinuxX64Stat 注释）；其余宿主（非
                    // Linux、非 x64）身份不可取得 → Unsupported，绝不猜
                    // 测身份、更不按「不同」继续截断（§4.5.7）
                    safeA.DangerousAddRef(ref keepA);
                    safeB.DangerousAddRef(ref keepB);
                    var fdA = (int)safeA.DangerousGetHandle().ToInt64();
                    var fdB = (int)safeB.DangerousGetHandle().ToInt64();
                    if (fstat(fdA, out var statA) != 0)
                    {
                        return new VmI32(-MapFsErrno(
                            System.Runtime.InteropServices.Marshal
                                .GetLastWin32Error()));
                    }
                    if (fstat(fdB, out var statB) != 0)
                    {
                        return new VmI32(-MapFsErrno(
                            System.Runtime.InteropServices.Marshal
                                .GetLastWin32Error()));
                    }
                    // 只读身份字段（st_dev@0、st_ino@8，完整 144 字节镜像
                    // 的身份前缀），其余字段不参与判定
                    same = statA.Dev == statB.Dev && statA.Ino == statB.Ino;
                }
                else
                {
                    return new VmI32(-38);
                }
            }
            catch (System.EntryPointNotFoundException)
            {
                // 宿主无身份查询入口（如 glibc < 2.33 无 fstat 导出）：
                // 不能判定 → Unsupported（不猜测身份，§4.5.7 不模拟）
                return new VmI32(-38);
            }
            catch (System.DllNotFoundException)
            {
                // 宿主无 libc.so.6（musl 等）：身份入口缺失同 Unsupported，
                // 不漏宿主异常出流契约（OpenAppendStream 同口径）
                return new VmI32(-38);
            }
            finally
            {
                // 租借归还：条件化（未 AddRef 的平台分支/失败路径不受影响）
                if (keepA) { safeA.DangerousRelease(); }
                if (keepB) { safeB.DangerousRelease(); }
            }
            outSpan.Elements[0] = new VmU8(same ? (byte)1 : (byte)0);
            return new VmI32(0);
        }

        // Win32 FILETIME 官方形态：两个 32 位 DWORD、4 字节对齐——不能
        // 用 long 充当：托管 Sequential 默认按自然对齐把 long 放 8 的
        // 倍数偏移，会把整个后段字段错位（本结构曾因此把
        // VolumeSerialNumber 放到 32、FileIndexHigh/Low 压到 48/52，
        // 而 native 只写 52 字节——FileIndexLow 读到未初始化尾隙，身份
        // 判定不可信且不保证复现）。internal 供测试断言布局
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FsFileTime
        {
            public uint LowDateTime;   // dwLowDateTime @0
            public uint HighDateTime;  // dwHighDateTime @4
        } // 8 字节，对齐 4

        // Win32 BY_HANDLE_FILE_INFORMATION 托管镜像。ABI 真值（Windows
        // SDK 10.0.26100.0 头经 clang -target x86_64-pc-windows-msvc
        // 探针实测，playground/fs_copy_identity/win_abi_truth.log）：
        // sizeof=52、align=4；dwFileAttributes@0、三个 FILETIME@4/12/20、
        // dwVolumeSerialNumber@28、nFileSizeHigh/Low@32/36、
        // nNumberOfLinks@40、nFileIndexHigh/Low@44/48。全字段 4 对齐无
        // 隐式填充，Marshal.SizeOf/OffsetOf 由 VmFsIdentityTests 机械
        // 锁定（源头与 rigi_rt fs.c 同一 Win32 定义，双宿主一致）
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FsByHandleInfo
        {
            public uint FileAttributes;       // @0
            public FsFileTime CreationTime;   // @4
            public FsFileTime LastAccessTime; // @12
            public FsFileTime LastWriteTime;  // @20
            public uint VolumeSerialNumber;   // @28（身份：卷序列号）
            public uint FileSizeHigh;         // @32
            public uint FileSizeLow;          // @36
            public uint NumberOfLinks;        // @40
            public uint FileIndexHigh;        // @44（身份：64 位文件索引高半）
            public uint FileIndexLow;         // @48（身份：低半）
        } // 52 字节，对齐 4

        // 直接收 SafeFileHandle：marshaler 在调用期间自动保活句柄，
        // 不经 DangerousGetHandle 裸指针（租借口径见 FsSameFile）
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle hFile,
            out FsByHandleInfo info);

        // POSIX glibc x86_64 struct stat 完整镜像（fstat 唯一合法接收
        // 面）。ABI 真值（WSL Ubuntu glibc 2.39 + gcc 13.3 -m64 实测
        // #include <sys/stat.h>，playground/fs_copy_identity/
        // linux_stat_truth.log）：sizeof=144、align=8；st_dev@0、
        // st_ino@8、st_nlink@16、st_mode@24、st_uid@28、st_gid@32、
        // __pad0@36、st_rdev@40、st_size@48、st_blksize@56、
        // st_blocks@64、st_atim@72、st_mtim@88、st_ctim@104、glibc
        // 保留@120..136。历史教训：只声明 16 字节前缀吃 144 字节写入 =
        // 栈越界破坏（身份读垃圾 → 同文件误判「不同」→ 自复制未拒 →
        // 源截断归零，probe_fstat_wsl.log）；只放大缓冲不声明字段仍是
        // ABI 猜测，均不可接受。本镜像全字段/保留字段逐字节对应、无
        // 隐式填充，Marshal.SizeOf/OffsetOf 由 VmFsIdentityTests 机械
        // 锁定；仅在已核实的 Linux x64 生产平台传入 fstat（native 面
        // rigi_rt 用真 C struct stat，无此层）
        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct FsLinuxX64Stat
        {
            public ulong Dev;        // st_dev @0（设备号——身份）
            public ulong Ino;        // st_ino @8（inode——身份）
            public ulong Nlink;      // st_nlink @16
            public uint Mode;        // st_mode @24
            public uint Uid;         // st_uid @28
            public uint Gid;         // st_gid @32
            public uint Pad0;        // __pad0 @36（原结构对齐保留）
            public ulong Rdev;       // st_rdev @40
            public ulong Size;       // st_size @48
            public ulong Blksize;    // st_blksize @56
            public ulong Blocks;     // st_blocks @64
            public ulong AtimSec;    // st_atim.tv_sec @72
            public ulong AtimNsec;   // st_atim.tv_nsec @80
            public ulong MtimSec;    // st_mtim.tv_sec @88
            public ulong MtimNsec;   // st_mtim.tv_nsec @96
            public ulong CtimSec;    // st_ctim.tv_sec @104
            public ulong CtimNsec;   // st_ctim.tv_nsec @112
            public ulong Reserved0;  // __glibc_reserved[0] @120
            public ulong Reserved1;  // __glibc_reserved[1] @128
            public ulong Reserved2;  // __glibc_reserved[2] @136
        } // 144 字节，对齐 8（36→40 的空隙与原 __pad0 一致）

        [System.Runtime.InteropServices.DllImport("libc.so.6",
            EntryPoint = "fstat", SetLastError = true)]
        private static extern int fstat(int fd, out FsLinuxX64Stat stat);

        private static long RequireI64(string hook, IReadOnlyList<VmValue> args, int index)
        {
            if (args.Count <= index || args[index] is not VmI64 value)
            {
                throw new VmException(hook + "：参数 " + index + " 需要 i64");
            }
            return value.Value;
        }
    }
}
