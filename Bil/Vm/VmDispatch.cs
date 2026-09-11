using System.Collections.Concurrent;

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
                if (strong == 1) _nativeRcStrong.Remove(token);
                else _nativeRcStrong[token] = strong - 1;
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
            var handle = RequireI64("rigi_event_signal", args, 0);
            if (!_timers.TryGetValue(handle, out var record))
            {
                return VmVoid.Instance;
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
            return VmVoid.Instance;
        }

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
