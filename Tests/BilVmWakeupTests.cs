using System;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BIL VM 唤醒丢失检测单元测试（BIL_VM_DESIGN §4.2 唤醒纪元纪律）：
    /// 直接驱动协程状态机（TryTransition/TrySuspend 为 public），覆盖
    /// PublishWakeup 失败处置的四态——① Suspended+纪元匹配 ⇒ Fail 留证
    /// （消息含「丢失唤醒」）；② 终态 ⇒ 无操作；③ Runnable ⇒ 无操作；
    /// ④ Suspended+纪元已变（stale 唤醒）⇒ 无操作。另附 TrySuspend 纪元
    /// 递增、Task/EventAlarm 登记发布链与端到端回归。
    /// </summary>
    public static class BilVmWakeupTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilVmWakeup");

            TestTrySuspendBumpsEpoch();
            TestIsWakeupLostDecision();
            TestWakeupLostFailsCoroutine();
            TestBenignTerminalNoOp();
            TestBenignRunnableNoOp();
            TestBenignStaleEpochNoOp();
            TestTaskWakeupPath();
            TestAlarmWakeupPath();
            TestEndToEndWakeupPaths();
            TestFailDisposesPollTimer();

            return TestHarness.Summary("BilVmWakeup");
        }

        // ===== 辅助（Tests 与 Bil 同程序集，internal 入口可直调） =====

        private static VmExecutor NewExecutor()
        {
            return new VmExecutor(new VmContext(new BilModule()));
        }

        private static VmCoroutine NewCoroutine(VmExecutor executor)
        {
            return new VmCoroutine(executor, "core.coroutine::Task");
        }

        // 驱动到 Running（挂起转换的唯一合法前置态）
        private static void MakeRunning(VmCoroutine coroutine)
        {
            TestHarness.CheckTrue("Created→Running 驱动",
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Running),
                "状态机驱动失败");
        }

        private static BilVmResult Run(string source)
        {
            try
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
                TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                        d => $"{d.Phase}: {d.Message}")));
                if (unit.Diagnostics.HasErrors)
                {
                    return new BilVmResult("", "", null,
                        new VmException("编译失败，跳过 VM"));
                }
                return BilVm.Run(module);
            }
            catch (Exception exception)
            {
                TestHarness.CheckTrue("全管线无诊断", false, exception.ToString());
                return new BilVmResult("", "", null,
                    new VmException(exception.Message, inner: exception));
            }
        }

        private static void CheckOk(string label, BilVmResult result)
        {
            TestHarness.CheckTrue(label + " 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
        }

        private static void CheckI32(string label, BilVmResult result, int expected)
        {
            TestHarness.CheckTrue(label,
                result.ReturnValue is VmI32 n && n.Value == expected,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // ===== 用例 =====

        // TrySuspend：CAS Running→Suspended 成功后纪元 +1；非 Running 拒绝且不动纪元
        private static void TestTrySuspendBumpsEpoch()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            TestHarness.CheckTrue("Created 时 TrySuspend 拒绝", !coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("拒绝时纪元不动", coroutine.WakeupEpoch == 0,
                "epoch=" + coroutine.WakeupEpoch);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("Running 时 TrySuspend 成功", coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("首次挂起纪元 1", coroutine.WakeupEpoch == 1,
                "epoch=" + coroutine.WakeupEpoch);
            TestHarness.CheckTrue("挂起后 Suspended", coroutine.State == VmCoroutineState.Suspended,
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("Suspended 时再 TrySuspend 拒绝", !coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("恢复→Running→再挂起纪元 2",
                coroutine.TryTransition(VmCoroutineState.Suspended, VmCoroutineState.Runnable)
                    && coroutine.TryTransition(VmCoroutineState.Runnable, VmCoroutineState.Running)
                    && coroutine.TrySuspend()
                    && coroutine.WakeupEpoch == 2,
                "epoch=" + coroutine.WakeupEpoch);
        }

        // 判定纯函数：仅「Suspended 且纪元匹配」为丢失，其余全 benign
        private static void TestIsWakeupLostDecision()
        {
            TestHarness.CheckTrue("Suspended+纪元匹配 ⇒ 丢失",
                VmExecutor.IsWakeupLost(VmCoroutineState.Suspended, 3, 3), "应为 true");
            TestHarness.CheckTrue("Suspended+纪元已变 ⇒ benign",
                !VmExecutor.IsWakeupLost(VmCoroutineState.Suspended, 4, 3), "应为 false");
            foreach (var state in new[]
            {
                VmCoroutineState.Created, VmCoroutineState.Runnable, VmCoroutineState.Running,
                VmCoroutineState.Completed, VmCoroutineState.Failed, VmCoroutineState.Cancelled,
            })
            {
                TestHarness.CheckTrue(state + "+纪元匹配 ⇒ benign",
                    !VmExecutor.IsWakeupLost(state, 3, 3), "应为 false");
            }
        }

        // ① 丢失唤醒：仍 Suspended 且纪元未变 ⇒ Fail 留证（消息含「丢失唤醒」+ 来源）
        private static void TestWakeupLostFailsCoroutine()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("挂起成功", coroutine.TrySuspend(), "状态 " + coroutine.State);
            var epoch = coroutine.WakeupEpoch;
            executor.HandleWakeupPublishFailure(coroutine, epoch, "单元测试源",
                new VmException("无法发布协程（当前状态 Runnable）"));
            TestHarness.CheckTrue("协程转 Failed", coroutine.State == VmCoroutineState.Failed,
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("失败证据留存", coroutine.Failure != null,
                "Failure 为 null");
            TestHarness.CheckTrue("消息含「丢失唤醒」",
                coroutine.Failure!.Message.Contains("丢失唤醒"), coroutine.Failure.Message);
            TestHarness.CheckTrue("消息含唤醒来源",
                coroutine.Failure.Message.Contains("单元测试源"), coroutine.Failure.Message);
            TestHarness.CheckTrue("Task 同步失败", coroutine.Task.State == VmTaskState.Failed,
                "task=" + coroutine.Task.State);
        }

        // ② 终态（Completed/Failed/Cancelled）⇒ 无操作，不注入失败证据
        private static void TestBenignTerminalNoOp()
        {
            foreach (var terminal in new[]
            {
                VmCoroutineState.Completed, VmCoroutineState.Failed, VmCoroutineState.Cancelled,
            })
            {
                var executor = NewExecutor();
                var coroutine = NewCoroutine(executor);
                TestHarness.CheckTrue("驱动终态 " + terminal,
                    coroutine.TryTransition(VmCoroutineState.Created, terminal),
                    "状态机驱动失败");
                executor.HandleWakeupPublishFailure(coroutine, 0, "单元测试源",
                    new VmException("无法发布协程（当前状态 " + terminal + "）"));
                TestHarness.CheckTrue(terminal + " 保持终态无操作",
                    coroutine.State == terminal, "状态 " + coroutine.State);
                TestHarness.CheckTrue(terminal + " 不注入 Failure",
                    coroutine.Failure == null, "Failure 被误设");
            }
        }

        // ③ Runnable ⇒ benign：走真实 PublishWakeup（Publish 对 Runnable 三 CAS 全失败抛出）
        private static void TestBenignRunnableNoOp()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            TestHarness.CheckTrue("驱动 Runnable",
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable),
                "状态机驱动失败");
            executor.PublishWakeup(coroutine, 0, "单元测试源");
            TestHarness.CheckTrue("Runnable 发布失败静默，状态不变",
                coroutine.State == VmCoroutineState.Runnable, "状态 " + coroutine.State);
            TestHarness.CheckTrue("Runnable 不注入 Failure", coroutine.Failure == null,
                "Failure 被误设");
        }

        // ④ Suspended+纪元已变（登记唤醒是旧纪元的）⇒ 无操作：新一轮挂起有自己的登记
        private static void TestBenignStaleEpochNoOp()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("旧纪元挂起", coroutine.TrySuspend(), "状态 " + coroutine.State);
            var staleEpoch = coroutine.WakeupEpoch;
            TestHarness.CheckTrue("恢复并新纪元挂起",
                coroutine.TryTransition(VmCoroutineState.Suspended, VmCoroutineState.Runnable)
                    && coroutine.TryTransition(VmCoroutineState.Runnable, VmCoroutineState.Running)
                    && coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("新纪元已递增", coroutine.WakeupEpoch != staleEpoch,
                "epoch=" + coroutine.WakeupEpoch);
            executor.HandleWakeupPublishFailure(coroutine, staleEpoch, "单元测试源",
                new VmException("无法发布协程（当前状态 Runnable）"));
            TestHarness.CheckTrue("stale 唤醒静默，仍 Suspended",
                coroutine.State == VmCoroutineState.Suspended, "状态 " + coroutine.State);
            TestHarness.CheckTrue("stale 唤醒不注入 Failure", coroutine.Failure == null,
                "Failure 被误设");
            TestHarness.CheckTrue("Task 未被误失败", coroutine.Task.State == VmTaskState.Pending,
                "task=" + coroutine.Task.State);
        }

        // VmTask 登记发布链：TryAwait 锁内挂起登记（纪元 1），Complete 经
        // PublishWakeup 成功发布（协程离开 Suspended）
        private static void TestTaskWakeupPath()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            var task = new VmTask("core.coroutine::Task<i32>");
            MakeRunning(coroutine);
            var suspended = task.TryAwait(coroutine, out var state, out _, out _);
            TestHarness.CheckTrue("Pending task 挂起登记", suspended, "state=" + state);
            TestHarness.CheckTrue("登记后 Suspended",
                coroutine.State == VmCoroutineState.Suspended, "状态 " + coroutine.State);
            TestHarness.CheckTrue("登记纪元为本次挂起", coroutine.WakeupEpoch == 1,
                "epoch=" + coroutine.WakeupEpoch);
            TestHarness.CheckTrue("await 已观察", task.WasObserved, "WasObserved=false");
            task.Complete(new VmI32(7));
            // 发布后线程池可能立即接管执行至终态，只断言离开 Suspended（稳定）
            TestHarness.CheckTrue("Complete 唤醒生效", coroutine.State != VmCoroutineState.Suspended,
                "状态 " + coroutine.State);
        }

        // VmEventAlarm 登记发布链：TryAwait 挂起登记，Signal 经 PublishWakeup 唤醒；
        // 触发后粘滞（后续 TryAwait 直接就绪）
        private static void TestAlarmWakeupPath()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            var alarm = new VmEventAlarm();
            MakeRunning(coroutine);
            TestHarness.CheckTrue("未触发 alarm 挂起登记", alarm.TryAwait(coroutine),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("登记后 Suspended",
                coroutine.State == VmCoroutineState.Suspended, "状态 " + coroutine.State);
            alarm.Signal();
            TestHarness.CheckTrue("Signal 唤醒生效", coroutine.State != VmCoroutineState.Suspended,
                "状态 " + coroutine.State);
            var second = NewCoroutine(executor);
            MakeRunning(second);
            TestHarness.CheckTrue("触发后粘滞就绪", !alarm.TryAwait(second),
                "second 状态 " + second.State);
            TestHarness.CheckTrue("粘滞分支不挂起",
                second.State == VmCoroutineState.Running, "状态 " + second.State);
        }

        // 终态释放轮询 timer：挂起协程持有 SchedulePoll 的在途 timer，
        // Fail 后经终态 choke point（NotifyTerminal）释放，不持有到触发
        private static void TestFailDisposesPollTimer()
        {
            var executor = NewExecutor();
            var coroutine = NewCoroutine(executor);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("挂起成功", coroutine.TrySuspend(), "状态 " + coroutine.State);
            executor.SchedulePoll(coroutine);
            TestHarness.CheckTrue("SchedulePoll 后有在途 timer", coroutine.HasPollTimer,
                "timer 未挂入");
            coroutine.Fail(new VmException("单元测试失败注入"));
            TestHarness.CheckTrue("Fail 后 timer 已释放", !coroutine.HasPollTimer,
                "timer 仍持有");
            TestHarness.CheckTrue("协程转 Failed", coroutine.State == VmCoroutineState.Failed,
                "状态 " + coroutine.State);
        }


        private static void TestEndToEndWakeupPaths()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "async func delay(base: i32, delta: i32): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return (base + delta)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = delay(40, 1)\n" +
                "    var b = delay(1, 0)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    yield sleep(2)\n" +
                "    return (x + y)\n" +
                "}\n");
            CheckOk("端到端唤醒通道", result);
            CheckI32("sleep + await fork/join 全链恢复", result, 42);
        }
    }
}
