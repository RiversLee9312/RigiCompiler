namespace RigiCompiler.Bil.Vm
{
    // 多 Worker Executor（BIL_VM_DESIGN §4.1 / RUNTIME §17）：
    // 包装 System.Threading.ThreadPool；工作项 = 一个 Coroutine 的一段执行。
    // Worker 跑 Step 循环直到该协程离开 Running（终态 / 挂起 / yield 重发布）。

    public sealed class VmExecutor
    {
        public VmContext Context { get; }

        private int _inflight;
        private readonly HashSet<VmCoroutine> _live = new HashSet<VmCoroutine>();
        private readonly object _liveLock = new object();
        private readonly List<VmCoroutine> _failed = new List<VmCoroutine>();
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);

        public VmExecutor(VmContext context)
        {
            Context = context;
        }

        // 未被任何 await 观察的失败（fire-and-forget）；已 await 的失败由等待方处理。
        public VmException? UnobservedFailure
        {
            get
            {
                lock (_liveLock)
                {
                    foreach (var coroutine in _failed)
                    {
                        if (!coroutine.Task.WasObserved)
                        {
                            return coroutine.Failure;
                        }
                    }
                    return null;
                }
            }
        }

        public VmCoroutine Spawn(BilFunction function, IReadOnlyList<VmValue> arguments)
        {
            var taskType = VmContext.TaskTypeRef(VmContext.FunctionResultType(function));
            var coroutine = new VmCoroutine(this, taskType);
            coroutine.PushFrame(function, arguments, resultSlot: null);
            Track(coroutine);
            return coroutine;
        }

        public void Track(VmCoroutine coroutine)
        {
            lock (_liveLock)
            {
                _live.Add(coroutine);
                _idle.Reset();
            }
        }

        public void NotifyTerminal(VmCoroutine coroutine)
        {
            lock (_liveLock)
            {
                _live.Remove(coroutine);
                if (coroutine.Failure != null)
                {
                    _failed.Add(coroutine);
                }
                MaybeSetIdle();
            }
        }

        public void Publish(VmCoroutine coroutine)
        {
            if (!coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable)
                && !coroutine.TryTransition(VmCoroutineState.Suspended, VmCoroutineState.Runnable)
                && !coroutine.TryTransition(VmCoroutineState.Running, VmCoroutineState.Runnable))
            {
                throw new VmException("无法发布协程（当前状态 " + coroutine.State + "）");
            }
            _idle.Reset();
            Interlocked.Increment(ref _inflight);
            ThreadPool.QueueUserWorkItem(_ => Execute(coroutine));
        }

        // 退避是实现选择，不是语言语义：空闲时避免对 isReady 忙等。
        public void SchedulePoll(VmCoroutine coroutine)
        {
            var delay = coroutine.TakePollDelay();
            _idle.Reset();
            var timer = new Timer(_ =>
            {
                try
                {
                    Publish(coroutine);
                }
                catch (VmException)
                {
                }
            }, null, delay, Timeout.Infinite);
            coroutine.AttachTimer(timer);
        }

        public void WaitQuiescence()
        {
            _idle.Wait();
        }

        private void Execute(VmCoroutine coroutine)
        {
            try
            {
                if (!coroutine.TryTransition(VmCoroutineState.Runnable, VmCoroutineState.Running))
                {
                    return;
                }
                if (!coroutine.SettleAfterResume(Context))
                {
                    return;
                }
                while (coroutine.State == VmCoroutineState.Running)
                {
                    coroutine.Step(Context);
                }
            }
            catch (Exception exception)
            {
                coroutine.Fail(exception);
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
                lock (_liveLock)
                {
                    MaybeSetIdle();
                }
            }
        }

        private void MaybeSetIdle()
        {
            if (_live.Count == 0 && Volatile.Read(ref _inflight) == 0)
            {
                _idle.Set();
            }
        }
    }
}
