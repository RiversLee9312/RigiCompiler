namespace RigiCompiler.Bil.Vm
{
    // PollingAlarm / EventAlarm（BIL_VM_DESIGN §4 / RUNTIME §19 / BIL_STANDARD §17.2）：
    // EventAlarm 是粘滞一次性事件。MW11c 棒5a：VmEventAlarm 专用表示退役——
    // sleep 迁 Rigi 实现（SleepAlarm），与 Timer 统一走 VmDispatch 的
    // VmTimerRecord 通道（EventAlarm 基类 handle 字段）；本文件只剩
    // PollingAlarm 常量。轮询退避是实现选择，不是语言语义——避免线程池
    // 空转忙等。

    internal static class VmPolling
    {
        internal const string ReadySlot = ".vm.poll.ready";
        internal const string IsReadySymbol = "core.coroutine::PollingAlarm$isReady()@.bool";
        internal const int MaxBackoffMs = 32;
    }
}
