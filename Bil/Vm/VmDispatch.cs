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

    internal sealed partial class VmDispatch
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

        // ===== fs 追加互操作常量（FileSystem.Platform 分文件使用；标量 + byte[] + string，
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
    }
}
