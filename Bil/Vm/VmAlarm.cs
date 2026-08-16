namespace RigiCompiler.Bil.Vm
{
    // PollingAlarm / EventAlarm（BIL_VM_DESIGN §4 / RUNTIME §19 / BIL_STANDARD §17.2）：
    // EventAlarm 是粘滞一次性事件；make_sleep_alarm 返回本类型并把 deadline 挂到时钟。
    // PollingAlarm 是用户对象（调 isReady）；本文件只放 EventAlarm 运行时表示。
    // 轮询退避是实现选择，不是语言语义——避免线程池空转忙等。

    public sealed class VmEventAlarm : VmValue
    {
        // waiter 登记项（纪元语义同 VmTask.WaiterEntry）：
        // 发布失败时以此区分唤醒丢失与 benign 竞态。
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
        private bool _signaled;
        private Timer? _timer;

        public override string TypeRef => "core.coroutine::EventAlarm";

        public override VmValue Copy() => this;

        public override string ToStandardText() => TypeRef;

        public static VmEventAlarm Sleep(long milliseconds)
        {
            var alarm = new VmEventAlarm();
            alarm.Arm(milliseconds);
            return alarm;
        }

        public void Arm(long milliseconds)
        {
            if (milliseconds <= 0)
            {
                Signal();
                return;
            }
            _timer = new Timer(_ => Signal(), null, milliseconds, Timeout.Infinite);
        }

        // 未触发：登记 waiter（连同唤醒纪元）、转 Suspended，返回 true。
        // 已触发：返回 false（调用方仍须结束当前执行段并重新发布）。
        public bool TryAwait(VmCoroutine waiter)
        {
            lock (_gate)
            {
                if (_signaled)
                {
                    return false;
                }
                if (!waiter.TrySuspend())
                {
                    throw new VmException("yield EventAlarm 时协程不在 Running");
                }
                _waiters.Add(new WaiterEntry(waiter, waiter.WakeupEpoch));
                return true;
            }
        }

        public void Signal()
        {
            List<WaiterEntry>? waiters = null;
            lock (_gate)
            {
                _signaled = true;
                waiters = new List<WaiterEntry>(_waiters);
                _waiters.Clear();
                var timer = _timer;
                _timer = null;
                timer?.Dispose();
            }
            if (waiters == null)
            {
                return;
            }
            foreach (var waiter in waiters)
            {
                waiter.Coroutine.BoundExecutor.PublishWakeup(
                    waiter.Coroutine, waiter.Epoch, "VmEventAlarm.Signal");
            }
        }
    }

    internal static class VmPolling
    {
        internal const string ReadySlot = ".vm.poll.ready";
        internal const string IsReadySymbol = "core.coroutine::PollingAlarm$isReady()@.bool";
        internal const int MaxBackoffMs = 32;
    }
}
