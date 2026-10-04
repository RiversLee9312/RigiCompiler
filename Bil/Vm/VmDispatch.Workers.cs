using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // Workers 职责；与主文件共享同一类型、字段及生命周期。

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
            var dispatcher = _context.GetSingleton(_context.RuntimeSymbol("core.coroutine::Dispatcher"));
            if (dispatcher is not IVmFieldHost host)
            {
                return false;
            }
            if (!host.TryReadField(_context.RuntimeField("core.coroutine::Dispatcher#live@.i32"), out var liveValue)
                || liveValue is not VmI32 live || live.Value <= 0)
            {
                return false;
            }
            foreach (var lane in new[] { "mainQueue", "computeQueue", "ioQueue" })
            {
                if (host.TryReadField(_context.RuntimeField("core.coroutine::Dispatcher#" + lane
                        + "@core.coroutine::CoroutineCarriageQueue"), out var queueValue)
                    && queueValue is IVmFieldHost queueHost
                    && queueHost.TryReadField(_context.RuntimeField("core.coroutine::CoroutineCarriageQueue#count@.i32"),
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

    }
}
