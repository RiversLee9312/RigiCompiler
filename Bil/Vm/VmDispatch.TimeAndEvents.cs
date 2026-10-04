using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace RigiCompiler.Bil.Vm
{
    internal sealed partial class VmDispatch
    {
        // TimeAndEvents 职责；与主文件共享同一类型、字段及生命周期。

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
                alarmObject.WriteField(_context.RuntimeField("core.coroutine::EventAlarm#handle@.i64"),
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

    }
}
