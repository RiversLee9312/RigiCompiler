namespace RigiCompiler.Bil.Vm
{
    // Task 句柄（BIL_VM_DESIGN §4.2 / RUNTIME §18.2–§18.3）：
    // 终态 + waiter 列表；终态转换与 waiter 登记共用一把锁，
    // 锁的释放/获取建立「终止前写入对 await 返回后可见」。
    // V5：VmTask 本身是 VmValue（Task / Task<T> 运行时身份）。

    public enum VmTaskState
    {
        Pending,
        Succeeded,
        Failed,
        Cancelled,
    }

    public sealed class VmTask : VmValue
    {
        // waiter 登记项：协程 + 登记时的唤醒纪元（TrySuspend 成功后、
        // 锁内读取，即本次挂起纪元）；发布失败时以此区分唤醒丢失与
        // benign 竞态（见 VmExecutor.PublishWakeup）。
        private readonly struct WaiterEntry
        {
            public VmCoroutine Coroutine { get; }
            public long Epoch { get; }

            public WaiterEntry(VmCoroutine coroutine, long epoch)
            {
                Coroutine = coroutine;
                Epoch = epoch;
            }
        }

        private readonly object _gate = new object();
        private readonly List<WaiterEntry> _waiters = new List<WaiterEntry>();
        private VmTaskState _state = VmTaskState.Pending;
        private VmValue? _result;
        private VmException? _exception;
        private readonly string _typeRef;
        private bool _observed;

        public VmTask(string typeRef)
        {
            _typeRef = typeRef;
        }

        public bool WasObserved
        {
            get { lock (_gate) return _observed; }
        }

        public override string TypeRef => _typeRef;

        public override VmValue Copy() => this;

        public override string ToStandardText() => _typeRef;

        public VmTaskState State
        {
            get { lock (_gate) return _state; }
        }

        public VmValue? Result
        {
            get { lock (_gate) return _result; }
        }

        public VmException? Exception
        {
            get { lock (_gate) return _exception; }
        }

        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    return _state != VmTaskState.Pending;
                }
            }
        }

        public void Complete(VmValue result)
        {
            List<WaiterEntry>? waiters = null;
            lock (_gate)
            {
                if (_state != VmTaskState.Pending)
                {
                    return;
                }
                _state = VmTaskState.Succeeded;
                _result = result;
                waiters = DrainWaiters();
            }
            ResumeWaiters(waiters);
        }

        public void Fail(VmException exception)
        {
            List<WaiterEntry>? waiters = null;
            lock (_gate)
            {
                if (_state != VmTaskState.Pending)
                {
                    return;
                }
                _state = VmTaskState.Failed;
                _exception = exception;
                waiters = DrainWaiters();
            }
            ResumeWaiters(waiters);
        }

        public void Cancel()
        {
            List<WaiterEntry>? waiters = null;
            lock (_gate)
            {
                if (_state != VmTaskState.Pending)
                {
                    return;
                }
                _state = VmTaskState.Cancelled;
                waiters = DrainWaiters();
            }
            ResumeWaiters(waiters);
        }

        // 已终态返回 false 并写出终态（调用方立即取值，不得挂起）。
        // 未终态：在同一把锁下转 Suspended（TrySuspend，纪元 +1）并连同
        // 当前纪元登记 waiter，返回 true。
        public bool TryAwait(VmCoroutine waiter, out VmTaskState state, out VmValue? result,
            out VmException? exception)
        {
            lock (_gate)
            {
                _observed = true;
                if (_state != VmTaskState.Pending)
                {
                    state = _state;
                    result = _result;
                    exception = _exception;
                    return false;
                }
                if (!waiter.TrySuspend())
                {
                    throw new VmException("await 时协程不在 Running");
                }
                _waiters.Add(new WaiterEntry(waiter, waiter.WakeupEpoch));
                state = VmTaskState.Pending;
                result = null;
                exception = null;
                return true;
            }
        }

        public void Observe(out VmTaskState state, out VmValue? result, out VmException? exception)
        {
            lock (_gate)
            {
                state = _state;
                result = _result;
                exception = _exception;
            }
        }

        private List<WaiterEntry> DrainWaiters()
        {
            var waiters = new List<WaiterEntry>(_waiters);
            _waiters.Clear();
            return waiters;
        }

        private static void ResumeWaiters(List<WaiterEntry>? waiters)
        {
            if (waiters == null)
            {
                return;
            }
            foreach (var waiter in waiters)
            {
                waiter.Coroutine.BoundExecutor.PublishWakeup(
                    waiter.Coroutine, waiter.Epoch, "VmTask.ResumeWaiters");
            }
        }
    }
}
