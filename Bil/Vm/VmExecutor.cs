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

        // 统一唤醒发布入口：三处异步唤醒路径（VmTask/VmEventAlarm 的 waiter
        // 发布、SchedulePoll 的一次性 timer）经此唤醒挂起协程。
        // Publish 只在三个 CAS（Created/Suspended/Running→Runnable）全失败时
        // 抛 VmException，正确性论证：
        // - CAS 全失败 ⇒ 失败时刻状态 ∈ {Runnable, 终态}；若此后读到
        //   Suspended 且纪元未变 ⇒ 本纪元唤醒登记已被消费且无任何成功发布
        //   （Runnable→Suspended 必经 Running，Running→Suspended 必递增纪元，
        //   终态为吸收态）⇒ 唤醒确定丢失：Fail 留证，否则协程将永久
        //   Suspended，_live 永不清空，WaitQuiescence 死锁且无任何证据。
        // - 纪元已变 ⇒ 协程曾被恢复并再次挂起（新纪元有自己的登记）⇒
        //   本次是 stale 唤醒，benign。
        // - 取消等竞态使 Publish 失败时状态为终态 ⇒ benign。
        // benign 竞态静默容忍（Bil/Vm 无 Logger，证据只经 BilVmResult 通道）。
        public void PublishWakeup(VmCoroutine coroutine, long registeredEpoch, string source)
        {
            try
            {
                Publish(coroutine);
            }
            catch (VmException exception)
            {
                HandleWakeupPublishFailure(coroutine, registeredEpoch, source, exception);
            }
        }

        // Publish 失败后的处置；internal 供单元测试直驱四态
        // （仍 Suspended / 终态 / Runnable / 纪元已变）。
        internal void HandleWakeupPublishFailure(VmCoroutine coroutine, long registeredEpoch,
            string source, VmException exception)
        {
            if (!IsWakeupLost(coroutine.State, coroutine.WakeupEpoch, registeredEpoch))
            {
                return;
            }
            // Fail 对终态幂等（直接返回）；对当前 Suspended 安全置 Failed，
            // 经 NotifyTerminal → _failed → BilVm.Run 的 UnobservedFailure 带出。
            coroutine.Fail(new VmException(
                "调度器丢失唤醒（" + source + "）：协程仍 Suspended 但唤醒登记已消费，Publish 失败："
                + exception.Message));
        }

        // 丢失唤醒判定：仍 Suspended 且纪元 == 登记纪元 ⇒ 本纪元登记已被
        // 消费而协程未被成功发布，唤醒丢失；其余（Runnable/Running/终态、
        // 纪元已变）均为 benign 竞态。
        internal static bool IsWakeupLost(VmCoroutineState state, long currentEpoch,
            long registeredEpoch)
        {
            return state == VmCoroutineState.Suspended && currentEpoch == registeredEpoch;
        }

        // 退避是实现选择，不是语言语义：空闲时避免对 isReady 忙等。
        // 调用点（ProbePolling）已通过 TrySuspend 转入 Suspended，
        // 此处捕获的纪元即本次挂起纪元，随闭包存入一次性 timer 回调。
        public void SchedulePoll(VmCoroutine coroutine)
        {
            var delay = coroutine.TakePollDelay();
            _idle.Reset();
            var epoch = coroutine.WakeupEpoch;
            var timer = new Timer(_ => PublishWakeup(coroutine, epoch, "VmExecutor.SchedulePoll"),
                null, delay, Timeout.Infinite);
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
