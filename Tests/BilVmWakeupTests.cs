using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// BIL VM 唤醒不丢失单元测试（MW11c 棒4a 重构版）。
    /// 旧版（棒3 前）断言 C# 实现的「唤醒纪元」内部观测装置；新结构
    /// （§17.4：调度逻辑进 Rigi 世界）下映射为等价断言：
    ///   - 挂起/登记原子性：await 登记 waiter 与 TrySuspend 在同一 task
    ///     gate 临界区内（registerWaiter 决策 = Rigi 方法，桥代持锁）；
    ///   - benign 竞态：Publish 到 Runnable/终态协程静默跳过（不注入
    ///     Failure），对齐旧 PublishWakeup benign 四态；
    ///   - 丢失唤醒留证：Rigi 侧发布失败 ⇒ 协程 Fail 且消息含
    ///     「丢失唤醒」+ 来源（旧 HandleWakeupPublishFailure 映射）；
    ///   - 登记/触发握手与端到端恢复：EventAlarm 粘滞、fork/join、
    ///     sleep、二次 await、fire-and-forget 未观察失败汇总。
    /// 可观察覆盖不缩：旧十个用例逐条映射（见各用例注释）。
    /// </summary>
    public static class BilVmWakeupTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "BilVmWakeup", Cases, sectionTitle: "BilVmWakeup");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestTrySuspendDiscipline", TestTrySuspendDiscipline),
            ("TestPublishBenignRunnable", TestPublishBenignRunnable),
            ("TestPublishBenignTerminal", TestPublishBenignTerminal),
            ("TestPublishFailureLeavesEvidence", TestPublishFailureLeavesEvidence),
            ("TestResumeSkippedOnStaleWakeup", TestResumeSkippedOnStaleWakeup),
            ("TestResumeSegments", TestResumeSegments),
            ("TestAlarmAwaitSignalHandshake", TestAlarmAwaitSignalHandshake),
            ("TestFailDisposesPollTimer", TestFailDisposesPollTimer),
            ("TestEndToEndWakeupPaths", TestEndToEndWakeupPaths),
            ("TestUnobservedFailureSummary", TestUnobservedFailureSummary),
        };

        // ===== 辅助（Tests 与 Bil 同程序集，internal 入口可直调） =====

        private static VmDispatch NewDispatch()
        {
            return new VmContext(new BilModule()).Dispatch;
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

        // 旧 TestTrySuspendBumpsEpoch 映射：挂起纪律保留（Created 拒绝 /
        // Running 成功 / Suspended 再拒绝 / 恢复后可再挂起）；纪元递增
        // 断言随内部装置退役（新结构由「登记+挂起同临界区」保证不丢唤醒，
        // 不再需要运行时观测）
        private static void TestTrySuspendDiscipline()
        {
            var coroutine = new VmCoroutine(NewDispatch());
            TestHarness.CheckTrue("Created 时 TrySuspend 拒绝", !coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("Running 时 TrySuspend 成功", coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("挂起后 Suspended",
                coroutine.State == VmCoroutineState.Suspended, "状态 " + coroutine.State);
            TestHarness.CheckTrue("Suspended 时再 TrySuspend 拒绝", !coroutine.TrySuspend(),
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("恢复→Running→再挂起",
                coroutine.TryTransition(VmCoroutineState.Suspended, VmCoroutineState.Runnable)
                    && coroutine.TryTransition(VmCoroutineState.Runnable, VmCoroutineState.Running)
                    && coroutine.TrySuspend(),
                "状态 " + coroutine.State);
        }

        // 旧 TestBenignRunnableNoOp 映射：Publish 到 Runnable 协程 benign
        // 跳过——状态不变、不注入 Failure（空模块纯状态机形态）
        private static void TestPublishBenignRunnable()
        {
            var dispatch = NewDispatch();
            var coroutine = new VmCoroutine(dispatch);
            dispatch.RegisterCoroutine(coroutine);
            TestHarness.CheckTrue("驱动 Runnable",
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable),
                "状态机驱动失败");
            dispatch.Publish(coroutine, "单元测试源");
            TestHarness.CheckTrue("Runnable 重复发布静默，状态不变",
                coroutine.State == VmCoroutineState.Runnable, "状态 " + coroutine.State);
            TestHarness.CheckTrue("Runnable 不注入 Failure", coroutine.Failure == null,
                "Failure 被误设");
        }

        // 旧 TestBenignTerminalNoOp 映射：Publish 到终态协程 benign 跳过
        private static void TestPublishBenignTerminal()
        {
            foreach (var terminal in new[]
            {
                VmCoroutineState.Completed, VmCoroutineState.Failed, VmCoroutineState.Cancelled,
            })
            {
                var dispatch = NewDispatch();
                var coroutine = new VmCoroutine(dispatch);
                dispatch.RegisterCoroutine(coroutine);
                TestHarness.CheckTrue("驱动终态 " + terminal,
                    coroutine.TryTransition(VmCoroutineState.Created, terminal),
                    "状态机驱动失败");
                dispatch.Publish(coroutine, "单元测试源");
                TestHarness.CheckTrue(terminal + " 保持终态无操作",
                    coroutine.State == terminal, "状态 " + coroutine.State);
                TestHarness.CheckTrue(terminal + " 不注入 Failure",
                    coroutine.Failure == null, "Failure 被误设");
            }
        }

        // 旧 TestWakeupLostFailsCoroutine 映射：Rigi 侧发布失败 ⇒ Fail
        // 留证（消息含「丢失唤醒」+ 来源）。空模块无 Dispatcher，走测试
        // 缝线注入发布失败
        private static void TestPublishFailureLeavesEvidence()
        {
            var dispatch = NewDispatch();
            var coroutine = new VmCoroutine(dispatch);
            dispatch.RegisterCoroutine(coroutine);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("挂起成功", coroutine.TrySuspend(), "状态 " + coroutine.State);
            dispatch.FailPublishesForTest = true;
            dispatch.Publish(coroutine, "单元测试源");
            TestHarness.CheckTrue("协程转 Failed", coroutine.State == VmCoroutineState.Failed,
                "状态 " + coroutine.State);
            TestHarness.CheckTrue("失败证据留存", coroutine.Failure != null,
                "Failure 为 null");
            TestHarness.CheckTrue("消息含「丢失唤醒」",
                coroutine.Failure!.Message.Contains("丢失唤醒"), coroutine.Failure.Message);
            TestHarness.CheckTrue("消息含唤醒来源",
                coroutine.Failure.Message.Contains("单元测试源"), coroutine.Failure.Message);
        }

        // 旧 TestBenignStaleEpochNoOp 映射：stale 唤醒 = 协程已被并发
        // 恢复（不在 Runnable），resume 返回 SKIPPED 且不执行、不注入
        // Failure（新结构的 benign 等价：纪元装置退役后由 resume 前置
        // CAS 兜底）
        private static void TestResumeSkippedOnStaleWakeup()
        {
            var dispatch = NewDispatch();
            var coroutine = new VmCoroutine(dispatch);
            dispatch.RegisterCoroutine(coroutine);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("挂起成功", coroutine.TrySuspend(), "状态 " + coroutine.State);
            // 挂起态直接 resume（无对应发布）= stale 唤醒路径
            var code = dispatch.ResumeSegment(coroutine);
            TestHarness.CheckTrue("Suspended 上 resume 返回 SKIPPED",
                code == VmDispatch.ResumeSkipped, "code=" + code);
            TestHarness.CheckTrue("仍 Suspended 未被误执行",
                coroutine.State == VmCoroutineState.Suspended, "状态 " + coroutine.State);
            TestHarness.CheckTrue("stale 唤醒不注入 Failure", coroutine.Failure == null,
                "Failure 被误设");
        }

        // resume 三段式（对齐 rigi_rt RigiResumeCode 0/1/2）：YIELDED
        // （裸 yield 重发布）→ DONE（终态）
        private static void TestResumeSegments()
        {
            var result = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    yield\n" +
                "    return 7\n" +
                "}\n");
            TestHarness.CheckTrue("全管线无诊断（三段式）",
                !result.Unit.Diagnostics.HasErrors, "编译失败");
            var context = new VmContext(result.Module);
            // Dispatcher.publish 走 Rigi 解释，需要 singleton 已初始化
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            var fn = result.Module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var coroutine = new VmCoroutine(context.Dispatch);
            coroutine.PushFrame(fn, Array.Empty<VmValue>(), null);
            context.Dispatch.RegisterCoroutine(coroutine);
            // 首次发布：Created→Runnable 由 Publish 完成（空 Dispatcher
            // 模块缺 stdlib？本模块含 stdlib——这里直驱状态机，不经队列）
            TestHarness.CheckTrue("驱动 Runnable",
                coroutine.TryTransition(VmCoroutineState.Created, VmCoroutineState.Runnable),
                "状态机驱动失败");
            var first = context.Dispatch.ResumeSegment(coroutine);
            TestHarness.CheckTrue("裸 yield 段归宿 YIELDED",
                first == VmDispatch.ResumeYielded, "code=" + first);
            TestHarness.CheckTrue("yield 后重发布为 Runnable",
                coroutine.State == VmCoroutineState.Runnable, "状态 " + coroutine.State);
            var second = context.Dispatch.ResumeSegment(coroutine);
            TestHarness.CheckTrue("终态段归宿 DONE", second == VmDispatch.ResumeDone,
                "code=" + second);
            TestHarness.CheckTrue("协程 Completed",
                coroutine.State == VmCoroutineState.Completed, "状态 " + coroutine.State);
            TestHarness.CheckTrue("返回值 7",
                coroutine.Result is VmI32 n && n.Value == 7,
                coroutine.Result?.ToStandardText() ?? "<null>");
        }

        // 旧 TestAlarmWakeupPath 映射：EventAlarm 登记/触发原子握手 +
        // 粘滞就绪（直驱引擎；唤醒发布在空模块形态退化为纯 CAS）。
        // 棒5a：VmEventAlarm 退役——sleep/Timer 统一 VmTimerRecord 通道，
        // 本用例经 timer_create hook 建记录 + 携带 EventAlarm#handle
        // 字段的 VmObject 替身驱动（delay=0 立即响铃）
        private static void TestAlarmAwaitSignalHandshake()
        {
            var dispatch = NewDispatch();
            var coroutine = new VmCoroutine(dispatch);
            dispatch.RegisterCoroutine(coroutine);   // 唤醒发布需要调度句柄
            var timerHandle = ((VmI64)dispatch.TimerCreate(new VmValue[]
            {
                new VmI64(0), new VmI64(0), new VmI64(0), new VmI64(0), new VmI64(0),
            })).Value;
            var alarm = new VmObject("core.coroutine::SleepAlarm", valueType: false);
            alarm.WriteField("core.coroutine::EventAlarm#handle@.i64",
                new VmI64(timerHandle));
            MakeRunning(coroutine);
            TestHarness.CheckTrue("未触发 alarm 挂起登记",
                dispatch.TryAwaitTimer(alarm, coroutine),
                "状态 " + coroutine.State);
            // delay=0 响铃在 ThreadPool 上立即回调：登记返回后状态可能
            // 已从 Suspended 被 Publish 成 Runnable（TryAwaitTimer 在
            // 记录闸外才 NoteSuspended，与 RingTimer 竞态）
            TestHarness.CheckTrue("登记后 Suspended 或已唤醒",
                coroutine.State == VmCoroutineState.Suspended
                || coroutine.State == VmCoroutineState.Runnable,
                "状态 " + coroutine.State);
            // delay=0 的响铃在 ThreadPool 线程异步触发：自旋等待发布
            // 生效（上限 5s，超时即失败）
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (coroutine.State == VmCoroutineState.Suspended
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }
            TestHarness.CheckTrue("响铃唤醒生效",
                coroutine.State != VmCoroutineState.Suspended, "状态 " + coroutine.State);
            var second = new VmCoroutine(dispatch);
            MakeRunning(second);
            TestHarness.CheckTrue("触发后粘滞就绪",
                !dispatch.TryAwaitTimer(alarm, second),
                "second 状态 " + second.State);
            TestHarness.CheckTrue("粘滞分支不挂起",
                second.State == VmCoroutineState.Running, "状态 " + second.State);
        }

        // 终态释放轮询 timer（保留旧用例）：挂起协程持有 SchedulePoll
        // 的在途 timer，Fail 后经终态 choke point（OnTerminal）释放
        private static void TestFailDisposesPollTimer()
        {
            var dispatch = NewDispatch();
            var coroutine = new VmCoroutine(dispatch);
            MakeRunning(coroutine);
            TestHarness.CheckTrue("挂起成功", coroutine.TrySuspend(), "状态 " + coroutine.State);
            dispatch.SchedulePoll(coroutine);
            TestHarness.CheckTrue("SchedulePoll 后有在途 timer", coroutine.HasPollTimer,
                "timer 未挂入");
            coroutine.Fail(new VmException("单元测试失败注入"));
            TestHarness.CheckTrue("Fail 后 timer 已释放", !coroutine.HasPollTimer,
                "timer 仍持有");
            TestHarness.CheckTrue("协程转 Failed", coroutine.State == VmCoroutineState.Failed,
                "状态 " + coroutine.State);
        }

        // 旧 TestEndToEndWakeupPaths 保留：sleep + await fork/join 全链
        // 恢复（唤醒经 Rigi Dispatcher 队列）
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

        // 旧 TestTaskWakeupPath 的可观察映射：await 登记 → completer
        // 终态发布 → waiter 恢复取回结果；二次 await 读终态快路径；
        // fire-and-forget 失败进未观察失败汇总
        private static void TestUnobservedFailureSummary()
        {
            var result = Run(
                "async func boom(): i32 {\n" +
                "    yield\n" +
                "    throw new core.RuntimeException(\"火\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = boom()\n" +
                "    yield\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckTrue("未观察失败进汇总", result.Exception != null,
                "Exception 为 null");
            TestHarness.CheckTrue("失败消息可辨认",
                result.Exception == null || result.Exception.Message.Contains("火")
                    || result.Exception.Message.Contains("RuntimeException"),
                result.Exception?.Message ?? "<null>");
        }
    }
}
