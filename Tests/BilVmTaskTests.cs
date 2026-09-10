using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// MW11c 棒4b：VM 侧冷 Task 启动通道 / Executor 绑定与换绑 /
    /// TaskState 投影 / Mutex / Timer 语义套件（RUNTIME §17.1/§18.2/
    /// §18.4/§19.5/§19.6/§20.1，SYNTAX §4.5）。对拍级：每语义至少一正
    /// 用例，进程内全管线（编译 → BIL → VM）断言可观察行为；Worker
    /// 身份类断言经 VmDispatch.ResumeLog 测试缝（确定性设计：只断言
    /// 「段 → Worker」映射，不断言交错次序）。
    /// </summary>
    public static class BilVmTaskTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "BilVmTask", Cases, sectionTitle: "BilVmTask");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestColdTaskConstructedNotExecuted", TestColdTaskConstructedNotExecuted),
            ("TestAwaitColdTaskStartsAndJoins", TestAwaitColdTaskStartsAndJoins),
            ("TestColdTaskRunThenAwait", TestColdTaskRunThenAwait),
            ("TestRunOnStartedTaskThrows", TestRunOnStartedTaskThrows),
            ("TestConcurrentAwaitStartSingleStart", TestConcurrentAwaitStartSingleStart),
            ("TestRunOnComputeExecutor", TestRunOnComputeExecutor),
            ("TestExecutorPresetThenRun", TestExecutorPresetThenRun),
            ("TestExecutorRebindNextResumePoint", TestExecutorRebindNextResumePoint),
            ("TestEagerSpawnInheritsExecutor", TestEagerSpawnInheritsExecutor),
            ("TestTaskStateTransitions", TestTaskStateTransitions),
            ("TestMutexMutualExclusion", TestMutexMutualExclusion),
            ("TestMutexFifoWakeOrder", TestMutexFifoWakeOrder),
            ("TestMutexRunSynchronously", TestMutexRunSynchronously),
            ("TestMutexIllegalReleaseThrows", TestMutexIllegalReleaseThrows),
            ("TestTimerSingleShotAndSticky", TestTimerSingleShotAndSticky),
            ("TestTimerRepeatExhaustionSticky", TestTimerRepeatExhaustionSticky),
            ("TestTimerInfiniteRepeat", TestTimerInfiniteRepeat),
            ("TestTimerRepeatZeroThrows", TestTimerRepeatZeroThrows),
            ("TestTimerSchedulePastImmediate", TestTimerSchedulePastImmediate),
            ("TestCrossExecutorCombination", TestCrossExecutorCombination),
            ("TestOpaqueColdTaskBody", TestOpaqueColdTaskBody),
            ("TestCoroutineLocalGetDefaultAndNull", TestCoroutineLocalGetDefaultAndNull),
            ("TestCoroutineLocalWithValueNested", TestCoroutineLocalWithValueNested),
            ("TestCoroutineLocalSpawnInherit", TestCoroutineLocalSpawnInherit),
            ("TestStepBudgetStopsWorkers", TestStepBudgetStopsWorkers),
        };

        // ===== 辅助 =====

        private static void TestStepBudgetStopsWorkers()
        {
            foreach (var executor in new[] { "MainExecutor", "ComputeExecutor", "IOExecutor" })
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(
                    "import core.coroutine.*\n" +
                    "pub func main(): i32 {\n" +
                    "    const task = new Task(func{async () -> { while (true) { yield } }})\n" +
                    "    task.run(new " + executor + "())\n" +
                    "    await task\n    return 0\n}\n");
                TestHarness.CheckTrue(executor + " 预算用例编译通过", !unit.Diagnostics.HasErrors);
                if (unit.Diagnostics.HasErrors) continue;
                var result = BilVm.Run(module, maxSteps: 100_000);
                TestHarness.CheckTrue(executor + " 超限返回而非遗留 Worker 等待",
                    result.Exception is VmStepLimitException, result.Exception?.ToString() ?? "无异常");
            }
        }

        private static BilVmResult Run(string source)
        {
            return RunVm(source).Result;
        }

        private static (BilVmResult Result, VmDispatch Dispatch) RunVm(string source)
        {
            try
            {
                var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
                TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                        d => $"{d.Phase}: {d.Message}")));
                if (unit.Diagnostics.HasErrors)
                {
                    return (new BilVmResult("", "", null,
                        new VmException("编译失败，跳过 VM")), null!);
                }
                var vm = new BilVm(module) { TraceResumes = true };
                var result = vm.Run();
                return (result, vm.LastContext!.Dispatch);
            }
            catch (Exception exception)
            {
                TestHarness.CheckTrue("全管线无诊断", false, exception.ToString());
                return (new BilVmResult("", "", null,
                    new VmException(exception.Message, inner: exception)), null!);
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

        private static void CheckStdout(string label, BilVmResult result,
            params string[] expectedLines)
        {
            var expected = string.Join("\n", expectedLines) + "\n";
            TestHarness.CheckTrue(label, result.Stdout == expected,
                "期望 <" + expected.Replace("\n", "\\n") + "> 实际 <"
                + result.Stdout.Replace("\n", "\\n") + ">");
        }

        // 协程句柄 → 恢复段 Worker 序列断言（ResumeLog 测试缝）
        private static void CheckResumeWorkers(string label, VmDispatch dispatch,
            long coroutineHandle, params long[] expectedWorkers)
        {
            if (!dispatch.ResumeLog.TryGetValue(coroutineHandle, out var log))
            {
                TestHarness.CheckTrue(label, false, "协程 " + coroutineHandle + " 无恢复记录");
                return;
            }
            long[] snapshot;
            lock (log)
            {
                snapshot = log.ToArray();
            }
            TestHarness.CheckTrue(label,
                snapshot.SequenceEqual(expectedWorkers),
                "期望 [" + string.Join(",", expectedWorkers) + "] 实际 ["
                + string.Join(",", snapshot) + "]");
        }

        // Compute 内允许跨 Worker 迁移；换绑只要求第一个恢复段在 Main。
        private static void CheckComputeResumes(string label, VmDispatch dispatch,
            long handle, bool startsOnMain)
        {
            var log = dispatch.ResumeLog[handle];
            long[] snapshot;
            lock (log) snapshot = log.ToArray();
            var workers = WorkerHandles(dispatch).Where(w => w != 0).ToHashSet();
            TestHarness.CheckTrue(label, snapshot.Length == 2
                && (startsOnMain ? snapshot[0] == 0 : workers.Contains(snapshot[0]))
                && workers.Contains(snapshot[1]),
                "实际 [" + string.Join(",", snapshot) + "]");
        }

        // 全部非 main 协程的恢复段都不落在主 Worker（0）——跨 Executor
        // 综合用例（两 Task 分属 Compute/IO，不断言具体 lane 分配）
        private static void CheckNonMainOffMain(string label, VmDispatch dispatch,
            int expectedCoroutines)
        {
            var others = dispatch.ResumeLog.Keys
                .Where(h => h != dispatch.MainHandle).ToArray();
            TestHarness.CheckTrue(label + "（协程数）",
                others.Length == expectedCoroutines,
                "期望 " + expectedCoroutines + " 实际 [" + string.Join(",", others) + "]");
            foreach (var handle in others)
            {
                long[] snapshot;
                lock (dispatch.ResumeLog[handle])
                {
                    snapshot = dispatch.ResumeLog[handle].ToArray();
                }
                TestHarness.CheckTrue(label + "（协程 " + handle + " 全部段离主）",
                    snapshot.Length > 0 && snapshot.All(w => w != 0),
                    "实际 [" + string.Join(",", snapshot) + "]");
            }
        }

        // 唯一非 main 协程的句柄（单协程用例定位）
        private static long SoleNonMainHandle(VmDispatch dispatch)
        {
            var others = dispatch.ResumeLog.Keys
                .Where(h => h != dispatch.MainHandle).ToArray();
            TestHarness.CheckTrue("恰一个非 main 协程", others.Length == 1,
                "实际 [" + string.Join(",", others) + "]");
            return others.Length == 1 ? others[0] : -1;
        }

        private static long[] WorkerHandles(VmDispatch dispatch)
        {
            return dispatch.RegisteredWorkers.OrderBy(h => h).ToArray();
        }

        // ===== A. 冷 Task 启动通道（§18.4）=====

        // 构造只存 body 不执行：副作用后置观测（println 前值为 0）
        private static void TestColdTaskConstructedNotExecuted()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub shared class Box { pub var value: i32 = 0 }\n" +
                "pub func main(): i32 {\n" +
                "    const box = new Box()\n" +
                "    const t = new Task(func{async () -> {\n" +
                "        box.value = 41\n" +
                "    } })\n" +
                "    core.io.Console.println(\"before=${box.value}\")\n" +
                "    t.run()\n" +
                "    await t\n" +
                "    core.io.Console.println(\"after=${box.value}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("冷 Task 构造不执行", result);
            CheckStdout("副作用后置（run 前未执行、run 后已执行）", result,
                "before=0", "after=41");
        }

        // 首次 await 冷 Task：当前 Executor 启动并等待，结果回传
        private static void TestAwaitColdTaskStartsAndJoins()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        yield sleep(5)\n" +
                "        return@_ 42\n" +
                "    } })\n" +
                "    const r = await t\n" +
                "    return r\n" +
                "}\n");
            CheckOk("await 冷 Task", result);
            CheckI32("await 冷 Task 自动启动并 join（结果回传）", result, 42);
        }

        // run() 显式启动 + 终态 isRunning 投影
        private static void TestColdTaskRunThenAwait()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> { return@_ 7 } })\n" +
                "    core.io.Console.println(\"pre=${t.state.isRunning}\")\n" +
                "    t.run()\n" +
                "    const r = await t\n" +
                "    core.io.Console.println(\"post=${t.state.isRunning}\")\n" +
                "    return r\n" +
                "}\n");
            CheckOk("run() 启动", result);
            CheckI32("run() 后 await 取回结果", result, 7);
            CheckStdout("isRunning 投影（启动前 false / 终态后 false）", result,
                "pre=false", "post=false");
        }

        // 已启动（含热 Task）run 抛 IllegalStateException，可 catch
        private static void TestRunOnStartedTaskThrows()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "async func hot(): i32 { return 1 }\n" +
                "pub func main(): i32 {\n" +
                "    const cold = new Task\\<i32>(func{async (): i32 -> { return@_ 2 } })\n" +
                "    cold.run()\n" +
                "    try {\n" +
                "        cold.run()\n" +
                "        core.io.Console.println(\"cold-nothrow\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        core.io.Console.println(\"cold-caught\")\n" +
                "    }\n" +
                "    const h = hot()\n" +
                "    try {\n" +
                "        h.run()\n" +
                "        core.io.Console.println(\"hot-nothrow\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        core.io.Console.println(\"hot-caught\")\n" +
                "    }\n" +
                "    const r = await cold\n" +
                "    const s = await h\n" +
                "    return (r + s)\n" +
                "}\n");
            CheckOk("重复 run 抛异常", result);
            CheckStdout("冷/热 Task 重复 run 均抛 IllegalStateException（可 catch）", result,
                "cold-caught", "hot-caught");
            CheckI32("任务结果不受影响", result, 3);
        }

        // 并发 await-start：只有一个完成 spawn-into（body 恰执行一次），
        // 竞争输家不抛、按普通 waiter 等到同一终态
        private static void TestConcurrentAwaitStartSingleStart()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub shared class Counter { pub var bodyRuns: i32 = 0 }\n" +
                "async func joiner(t: Task\\<i32>): i32 {\n" +
                "    yield sleep(10)\n" +
                "    return await t\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Counter()\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        c.bodyRuns = (c.bodyRuns + 1)\n" +
                "        yield sleep(30)\n" +
                "        return@_ 42\n" +
                "    } })\n" +
                "    const a = joiner(t)\n" +
                "    const b = joiner(t)\n" +
                "    const x = await a\n" +
                "    const y = await b\n" +
                "    core.io.Console.println(\"runs=${c.bodyRuns}\")\n" +
                "    return (x + y)\n" +
                "}\n");
            CheckOk("并发 await-start", result);
            CheckStdout("body 恰执行一次（spawn-into 单启动语义）", result, "runs=1");
            CheckI32("两个 waiter 都等到同一终态", result, 84);
        }

        // ===== B. Executor 绑定与换绑（§17.1/§18.4/§20.1）=====

        // run(executor:) 指定启动：协程全部恢复段落在 Compute Worker
        // （非 0 句柄），main（句柄 1）留在主 Worker
        private static void TestRunOnComputeExecutor()
        {
            var (result, dispatch) = RunVm(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        yield sleep(10)\n" +
                "        return@_ 7\n" +
                "    } })\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    const r = await t\n" +
                "    return r\n" +
                "}\n");
            CheckOk("Compute 启动", result);
            CheckI32("Compute 上跑计算 Task、join 回 Main 取结果", result, 7);
            if (dispatch == null)
            {
                return;
            }
            var workers = WorkerHandles(dispatch);
            TestHarness.CheckTrue("Compute Worker 池按可用并行度懒建",
                workers.Length == (VmDispatch.ComputeParallelism() + 1) && workers[0] == 0,
                "workers=[" + string.Join(",", workers) + "]");
            CheckComputeResumes("冷 Task 两恢复段都在 Compute Worker 集合",
                dispatch, SoleNonMainHandle(dispatch), false);
            CheckResumeWorkers("main 留在主 Worker", dispatch, dispatch.MainHandle, 0, 0);
        }

        // executor 预设写入（未启动）后 run()：以预设 Executor 启动
        private static void TestExecutorPresetThenRun()
        {
            var (result, dispatch) = RunVm(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> { return@_ 9 } })\n" +
                "    t.executor = new IOExecutor()\n" +
                "    t.run()\n" +
                "    return await t\n" +
                "}\n");
            CheckOk("预设后 run", result);
            CheckI32("预设 IOExecutor 启动并 join", result, 9);
            if (dispatch == null)
            {
                return;
            }
            var workers = WorkerHandles(dispatch);
            TestHarness.CheckTrue("IO Worker 懒建", workers.Length == 2 && workers[0] == 0,
                "workers=[" + string.Join(",", workers) + "]");
            CheckResumeWorkers("冷 Task 在 IO Worker 启动",
                dispatch, SoleNonMainHandle(dispatch), workers[1]);
        }

        // 已启动写 executor = 换绑：下一恢复点生效。冷 Task 先 run()（主
        // Executor），body 挂起 sleep 期间 main 换绑 Compute——恢复段落在
        // Compute Worker，结果照常回传
        private static void TestExecutorRebindNextResumePoint()
        {
            var (result, dispatch) = RunVm(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        yield sleep(30)\n" +
                "        return@_ 7\n" +
                "    } })\n" +
                "    t.run()\n" +
                "    t.executor = new ComputeExecutor()\n" +
                "    return await t\n" +
                "}\n");
            CheckOk("换绑", result);
            CheckI32("换绑后 join 结果不变", result, 7);
            if (dispatch == null)
            {
                return;
            }
            var workers = WorkerHandles(dispatch);
            TestHarness.CheckTrue("换绑触发 Compute Worker 懒建",
                workers.Length == (VmDispatch.ComputeParallelism() + 1) && workers[0] == 0,
                "workers=[" + string.Join(",", workers) + "]");
            CheckComputeResumes("首段主 Worker、换绑后恢复段在 Compute Worker 集合",
                dispatch, SoleNonMainHandle(dispatch), true);
        }

        // §18.1 继承：Compute 上的协程 eager spawn 的子协程继承 Compute
        private static void TestEagerSpawnInheritsExecutor()
        {
            var (result, dispatch) = RunVm(
                "import core.coroutine.*\n" +
                "async func child(): i32 {\n" +
                "    yield sleep(5)\n" +
                "    return 5\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        const c = child()\n" +
                "        return@_ ((await c) + 1)\n" +
                "    } })\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    return await t\n" +
                "}\n");
            CheckOk("跨 Executor 继承", result);
            CheckI32("子协程结果经 Compute 父协程回传 Main", result, 6);
            if (dispatch == null)
            {
                return;
            }
            var workers = WorkerHandles(dispatch);
            TestHarness.CheckTrue("Compute Worker 懒建", workers.Length == (VmDispatch.ComputeParallelism() + 1),
                "workers=[" + string.Join(",", workers) + "]");
            CheckNonMainOffMain("冷 Task 与其 eager 子协程都继承/落在 Compute Worker 集合",
                dispatch, 2);
        }

        // ===== C. TaskState 投影（§18.2）=====

        // 六态迁移点断言：Created →（run/publish）Runnable →（竞争
        // Mutex 挂起）Suspended →（终态）Completed / Failed；isRunning
        // 仅 Runnable/Suspended 为 true。Cancelled 无公开产生通道（本棒
        // 无 cancel API），不观测。
        // 挂起快照用 Mutex 竞争而非 sleep(20)/sleep(60)：并行套件下
        // ThreadPool 饥饿会让短 sleep 墙钟交错，子 Task 在快照前已完成
        private static void TestTaskStateTransitions()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "func name(s: TaskState): String {\n" +
                "    if (s is .Created) { return \"created\" }\n" +
                "    if (s is .Runnable) { return \"runnable\" }\n" +
                "    if (s is .Suspended) { return \"suspended\" }\n" +
                "    if (s is .Completed) { return \"completed\" }\n" +
                "    if (s is .Failed) { return \"failed\" }\n" +
                "    return \"cancelled\"\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Mutex()\n" +
                "    const held = await m.acquire()\n" +
                "    const t = new Task(func{async () -> {\n" +
                "        const l = await m.acquire()\n" +
                "        m.release(l)\n" +
                "    } })\n" +
                "    core.io.Console.println(\"s0=${name(t.state)}/${t.state.isRunning}\")\n" +
                "    t.run()\n" +
                "    core.io.Console.println(\"s1=${name(t.state)}/${t.state.isRunning}\")\n" +
                "    yield\n" +
                "    core.io.Console.println(\"s2=${name(t.state)}/${t.state.isRunning}\")\n" +
                "    m.release(held)\n" +
                "    await t\n" +
                "    core.io.Console.println(\"s3=${name(t.state)}/${t.state.isRunning}\")\n" +
                "    const f = new Task(func{async () -> {\n" +
                "        throw new core.RuntimeException(\"b\")\n" +
                "    } })\n" +
                "    f.run()\n" +
                "    try {\n" +
                "        await f\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        core.io.Console.println(\"boom\")\n" +
                "    }\n" +
                "    core.io.Console.println(\"s4=${name(f.state)}/${f.state.isRunning}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("TaskState 迁移", result);
            CheckStdout("Created/Runnable/Suspended/Completed/Failed 迁移与 isRunning 投影",
                result,
                "s0=created/false", "s1=runnable/true", "s2=suspended/true",
                "s3=completed/false", "boom", "s4=failed/false");
        }

        // ===== D. Mutex（§19.6）=====

        // 互斥性：临界区内持锁挂起让出（锁跨挂起持有），竞争者不得进入
        // ——否则日志交织成 "[a[b..." 形态
        private static void TestMutexMutualExclusion()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub shared class Log { pub var order: String = \"\" }\n" +
                "async func critical(m: Mutex, log: Log, tag: String) {\n" +
                "    const l = await m.acquire()\n" +
                "    log.order = (log.order + (\"[\" + tag))\n" +
                "    yield sleep(20)\n" +
                "    log.order = (log.order + (tag + \"]\"))\n" +
                "    m.release(l)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Mutex()\n" +
                "    const log = new Log()\n" +
                "    const a = critical(m, log, \"a\")\n" +
                "    const b = critical(m, log, \"b\")\n" +
                "    await a\n" +
                "    await b\n" +
                "    core.io.Console.println(log.order)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Mutex 互斥", result);
            CheckStdout("两协程临界区不交织（锁跨挂起持有）", result, "[aa][bb]");
        }

        // FIFO 唤醒序：主持锁期间两竞争者按到达序排队，释放后按
        // FIFO 依次取得（handoff 无 barging）
        private static void TestMutexFifoWakeOrder()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub shared class Log { pub var order: String = \"\" }\n" +
                "async func worker(m: Mutex, tag: String, log: Log) {\n" +
                "    const l = await m.acquire()\n" +
                "    log.order = (log.order + tag)\n" +
                "    m.release(l)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Mutex()\n" +
                "    const log = new Log()\n" +
                "    const gate = await m.acquire()\n" +
                "    const a = worker(m, \"a\", log)\n" +
                "    const b = worker(m, \"b\", log)\n" +
                "    yield sleep(20)\n" +
                "    m.release(gate)\n" +
                "    await a\n" +
                "    await b\n" +
                "    core.io.Console.println(log.order)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Mutex FIFO", result);
            CheckStdout("等待者按 FIFO 序唤醒", result, "ab");
        }

        // runSynchronously：泛型变体返回 body 结果；body 抛异常时
        // finally 语义释放（后续 acquire 可成）
        private static void TestMutexRunSynchronously()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const m = new Mutex()\n" +
                "    const r = await m.runSynchronously\\<i32>(\n" +
                "        func{async (): i32 -> { return@_ 42 } })\n" +
                "    try {\n" +
                "        await m.runSynchronously(func{async () -> {\n" +
                "            throw new core.RuntimeException(\"x\")\n" +
                "        } })\n" +
                "        core.io.Console.println(\"nothrow\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        core.io.Console.println(\"caught\")\n" +
                "    }\n" +
                "    const s = await m.runSynchronously\\<i32>(\n" +
                "        func{async (): i32 -> { return@_ 1 } })\n" +
                "    core.io.Console.println(\"s=${s}\")\n" +
                "    return (r + s)\n" +
                "}\n");
            CheckOk("runSynchronously", result);
            CheckStdout("body 异常传播且锁已释放", result, "caught", "s=1");
            CheckI32("泛型变体返回值 + 异常后锁可再取得", result, 43);
        }

        // 非法 release：他锁令牌 / 重复释放均抛 IllegalStateException
        // （可 catch），且失败 release 不释放锁（m2 后续可正常释放）
        private static void TestMutexIllegalReleaseThrows()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const m1 = new Mutex()\n" +
                "    const m2 = new Mutex()\n" +
                "    const l = await m1.acquire()\n" +
                "    try {\n" +
                "        m2.release(l)\n" +
                "        core.io.Console.println(\"bad-nothrow\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        core.io.Console.println(\"bad-caught\")\n" +
                "    }\n" +
                "    m1.release(l)\n" +
                "    try {\n" +
                "        m1.release(l)\n" +
                "        core.io.Console.println(\"dup-nothrow\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        core.io.Console.println(\"dup-caught\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("非法 release", result);
            CheckStdout("他锁令牌/重复释放抛 IllegalStateException", result,
                "bad-caught", "dup-caught");
        }

        // ===== E. Timer（§19.5）=====

        // 单次延迟触发；响铃后恒 signaled（再次 yield 立即具备重新
        // 发布条件，仍结束当前执行段）
        private static void TestTimerSingleShotAndSticky()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Timer((20 as i64))\n" +
                "    core.io.Console.println(\"before\")\n" +
                "    yield t\n" +
                "    core.io.Console.println(\"ring1\")\n" +
                "    yield t\n" +
                "    core.io.Console.println(\"ring2\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("单次 Timer", result);
            CheckStdout("延迟触发 + 耗尽后恒 signaled", result,
                "before", "ring1", "ring2");
        }

        // Repeat(2)：两响各发布当前 waiter，耗尽后恒 signaled（第三次
        // yield 立即通过），总迭代计数 = 3
        private static void TestTimerRepeatExhaustionSticky()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Timer((10 as i64), .Repeat(2))\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 3) {\n" +
                "        yield t\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckOk("Repeat(2)", result);
            CheckI32("两响后耗尽恒 signaled（第三次立即通过）", result, 3);
        }

        // InfiniteRepeat：每次响铃发布 waiter 并自动重排，多次 yield
        // 各等到一次响铃
        private static void TestTimerInfiniteRepeat()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const t = new Timer((10 as i64), .InfiniteRepeat)\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 3) {\n" +
                "        yield t\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckOk("InfiniteRepeat", result);
            CheckI32("无限重复基本响铃（三次 yield 各等一响）", result, 3);
        }

        // Repeat(0)：pub init 参数洞校验抛 IllegalStateException（可
        // catch；stdlib Rigi 真体，两宿主同形）
        private static void TestTimerRepeatZeroThrows()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        const bad = new Timer((1 as i64), .Repeat(0))\n" +
                "        core.io.Console.println(\"nothrow\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        core.io.Console.println(\"caught\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Repeat(0)", result);
            CheckStdout("Repeat(0) init 抛 IllegalStateException", result, "caught");
        }

        // schedule(ringTime)：过去时刻（now）立即触发
        private static void TestTimerSchedulePastImmediate()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    yield Timer.schedule(core.time.DateTime.now())\n" +
                "    core.io.Console.println(\"fired\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("schedule 过去时刻", result);
            CheckStdout("过去 ringTime 立即触发", result, "fired");
        }

        // ===== F. 跨 Executor 综合（§20.1）=====

        // Compute 上跑计算 Task、IO 上跑定时 Task，join 回 Main 断言
        // 结果；两侧 Worker 懒建
        private static void TestCrossExecutorCombination()
        {
            var (result, dispatch) = RunVm(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    const c = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 40\n" +
                "    } })\n" +
                "    c.run(new ComputeExecutor())\n" +
                "    const io = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        yield new Timer((20 as i64))\n" +
                "        return@_ 2\n" +
                "    } })\n" +
                "    io.run(new IOExecutor())\n" +
                "    return ((await c) + (await io))\n" +
                "}\n");
            CheckOk("跨 Executor 综合", result);
            CheckI32("Compute 计算 + IO 定时 join 回 Main", result, 42);
            if (dispatch == null)
            {
                return;
            }
            var workers = WorkerHandles(dispatch);
            TestHarness.CheckTrue("Compute 池与 IO Worker 均懒建",
                workers.Length == (VmDispatch.ComputeParallelism() + 2) && workers[0] == 0,
                "workers=[" + string.Join(",", workers) + "]");
            CheckNonMainOffMain("两 Task 都不在 Main Worker 执行", dispatch, 2);
        }

        // 不透明 AsyncAction 槽：wrap 转发后冷启动仍 1:1 spawn-into
        private static void TestOpaqueColdTaskBody()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "import core.io.Console\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "func wrapI(body: core.AsyncFunc\\<i32>): Task\\<i32> {\n" +
                "    return new Task\\<i32>(body)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const t = wrap(func{async () -> { Console.println(\"opaque\") }})\n" +
                "    await t\n" +
                "    const n = await wrapI(func{async (): i32 -> 9})\n" +
                "    Console.println(n.toString())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("不透明 body 冷 Task", result);
            CheckStdout("wrap(AsyncAction)/wrap(AsyncFunc) spawn-into", result,
                "opaque", "9");
        }

        private static void TestCoroutineLocalGetDefaultAndNull()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    const def = new CoroutineLocal\\<String>(\"def\")\n" +
                "    Console.println((id.get() == null).toString())\n" +
                "    Console.println(def.get() as String)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("CoroutineLocal 默认/null", result);
            CheckStdout("无默认 get 为 null；有默认返回默认值", result,
                "true", "def");
        }

        private static void TestCoroutineLocalWithValueNested()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    await id.withValue(\"hi\", func{async () -> {\n" +
                "        Console.println(id.get() as String)\n" +
                "        await id.withValue(\"nest\", func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        Console.println(id.get() as String)\n" +
                "    }})\n" +
                "    Console.println((id.get() == null).toString())\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("CoroutineLocal withValue 嵌套", result);
            CheckStdout("压栈/弹栈与嵌套有效顶", result,
                "hi", "nest", "hi", "true");
        }

        private static void TestCoroutineLocalSpawnInherit()
        {
            var result = Run(
                "import core.coroutine.*\n" +
                "import core.io.Console\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    await id.withValue(\"x\", func{async () -> {\n" +
                "        const spawned = func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }}\n" +
                "        await spawned()\n" +
                "        const t = new Task(func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        await t\n" +
                "        const u = wrap(func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        await u\n" +
                "    }})\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("CoroutineLocal spawn 继承", result);
            CheckStdout("eager/冷/不透明冷 Task 均继承有效顶", result,
                "x", "x", "x");
        }
    }
}
