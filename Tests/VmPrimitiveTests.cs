using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// MW11c 棒4a VM 原语级单元测试（RUNTIME §17.4）：直接驱动
    /// VmDispatch 的原语钩子实现——Worker 启停/sem 交接协议/协程句柄
    /// create-resume-destroy 三段式（0=SUSPENDED/1=YIELDED/2=DONE，对齐
    /// rigi_rt RigiResumeCode）/同步 Mutex 互斥/TLS 当前上下文/时钟，
    /// 另加主 Worker 死锁显败兜底（无 runnable 且无在途唤醒源 ⇒ 诊断
    /// 抛出而非无限 park）。
    /// </summary>
    public static class VmPrimitiveTests
    {
        public static int RunAll() => ParallelSuiteRunner.RunAll(Spec);

        public static int RunWithArgs(IReadOnlyList<string> args) =>
            ParallelSuiteRunner.RunWithArgs(Spec, args);

        private static ParallelSuiteRunner.SuiteSpec Spec => new(
            "VmPrimitive", Cases, sectionTitle: "VmPrimitive");

        private static readonly (string Label, Action Run)[] Cases =
        {
            ("TestSyncMutexMutualExclusion", TestSyncMutexMutualExclusion),
            ("TestWorkerEnqueueParkHandoff", TestWorkerEnqueueParkHandoff),
            ("TestWorkerCreateRunDestroy", TestWorkerCreateRunDestroy),
            ("TestCoroutineResumeSegments", TestCoroutineResumeSegments),
            ("TestCoroutineDestroyRequiresTerminal", TestCoroutineDestroyRequiresTerminal),
            ("TestTlsAndTimeNow", TestTlsAndTimeNow),
            ("TestDeadlockDiagnosis", TestDeadlockDiagnosis),
        };

        private const string ModuleSource =
            "pub func entry(w: i64): i32 {\n" +
            "    core.io.Console.println(\"worker-ran\")\n" +
            "    return 0\n" +
            "}\n" +
            "pub func done(): i32 { return 7 }\n" +
            "pub func yielder(): i32 {\n" +
            "    yield\n" +
            "    return 9\n" +
            "}\n" +
            "pub func suspender(): i32 {\n" +
            "    yield core.coroutine.sleep(100000)\n" +
            "    return 1\n" +
            "}\n" +
            "pub func main(): i32 { return 0 }\n";

        private static VmContext NewContext()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(ModuleSource);
            TestHarness.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            var context = new VmContext(module);
            context.InitializeSingletons();
            context.InvokeGlobalInitializers();
            return context;
        }

        private static BilFunction Fn(VmContext context, string symbolPrefix)
        {
            return context.FindRuntimeFunction(symbolPrefix)
                ?? throw new VmException("缺 fn " + symbolPrefix);
        }

        private static VmValue[] Args(params long[] values)
        {
            return values.Select(v => (VmValue)new VmI64(v)).ToArray();
        }

        // ===== 同步 Mutex 原语：跨线程互斥 =====
        private static void TestSyncMutexMutualExclusion()
        {
            var dispatch = NewContext().Dispatch;
            var mutex = ((VmI64)dispatch.SyncMutexCreate(Array.Empty<VmValue>())).Value;
            var counter = 0;
            var threads = new Thread[2];
            for (var t = 0; t < 2; t++)
            {
                threads[t] = new Thread(() =>
                {
                    for (var i = 0; i < 10000; i++)
                    {
                        dispatch.SyncMutexAcquire(Args(mutex));
                        counter++;
                        dispatch.SyncMutexRelease(Args(mutex));
                    }
                });
                threads[t].Start();
            }
            foreach (var thread in threads)
            {
                thread.Join();
            }
            TestHarness.Check("互斥计数完整", counter.ToString(), "20000");
        }

        // ===== sem 交接协议（主 Worker 队列直驱）：入队→park 出队；
        // 空唤醒返 0；park 阻塞至入队 =====
        private static void TestWorkerEnqueueParkHandoff()
        {
            var dispatch = NewContext().Dispatch;
            dispatch.WorkerEnqueue(Args(0, 42));
            var token = ((VmI64)dispatch.WorkerPark(Args(0))).Value;
            TestHarness.Check("入队即出队", token.ToString(), "42");
            dispatch.WorkerEnqueue(Args(0, 0));
            token = ((VmI64)dispatch.WorkerPark(Args(0))).Value;
            TestHarness.Check("唤醒无任务返 0", token.ToString(), "0");
            var poster = new Thread(() =>
            {
                Thread.Sleep(50);
                dispatch.WorkerEnqueue(Args(0, 99));
            });
            poster.Start();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            token = ((VmI64)dispatch.WorkerPark(Args(0))).Value;
            watch.Stop();
            TestHarness.Check("park 阻塞后取到任务", token.ToString(), "99");
            TestHarness.CheckTrue("park 确实阻塞过", watch.ElapsedMilliseconds >= 30,
                watch.ElapsedMilliseconds + "ms");
            poster.Join();
        }

        // ===== Worker 启停：真线程跑入口 fn 的 BIL 后退出；destroy 收编 =====
        private static void TestWorkerCreateRunDestroy()
        {
            var context = NewContext();
            var dispatch = context.Dispatch;
            var entryToken = dispatch.RegisterEntry(Fn(context, "$entry("));
            var handle = ((VmI64)dispatch.WorkerCreate(Args(entryToken))).Value;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!context.Stdout.Contains("worker-ran")
                && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            TestHarness.CheckTrue("Worker 线程跑了入口 fn（BIL 解释）",
                context.Stdout.Contains("worker-ran"), context.Stdout);
            dispatch.WorkerDestroy(Args(handle));
            TestHarness.CheckTrue("入口失败清单为空",
                dispatch.WorkerFailures.IsEmpty,
                dispatch.WorkerFailures.Count + " 起");
        }

        // ===== 协程句柄三段式：YIELDED(1) → DONE(2)；挂起 SUSPENDED(0) =====
        private static void TestCoroutineResumeSegments()
        {
            var context = NewContext();
            var dispatch = context.Dispatch;
            // DONE：一段到底
            var doneSpec = dispatch.RegisterSpec(Fn(context, "$done("),
                Array.Empty<VmValue>());
            var done = ((VmI64)dispatch.CoroutineCreate(Args(0, doneSpec))).Value;
            TestHarness.Check("终态段 DONE",
                ((VmI64)dispatch.CoroutineResume(Args(done))).Value.ToString(),
                VmDispatch.ResumeDone.ToString());
            // YIELDED → DONE：裸 yield 重发布后再取回
            var yieldSpec = dispatch.RegisterSpec(Fn(context, "$yielder("),
                Array.Empty<VmValue>());
            var yielder = ((VmI64)dispatch.CoroutineCreate(Args(0, yieldSpec))).Value;
            TestHarness.Check("裸 yield 段 YIELDED",
                ((VmI64)dispatch.CoroutineResume(Args(yielder))).Value.ToString(),
                VmDispatch.ResumeYielded.ToString());
            TestHarness.Check("yield 后再取回 DONE",
                ((VmI64)dispatch.CoroutineResume(Args(yielder))).Value.ToString(),
                VmDispatch.ResumeDone.ToString());
            TestHarness.CheckTrue("destroy 终态句柄",
                dispatch.CoroutineDestroy(Args(done)) == VmVoid.Instance, "");
            // SUSPENDED：yield sleep 长闹钟挂起
            var suspendSpec = dispatch.RegisterSpec(Fn(context, "$suspender("),
                Array.Empty<VmValue>());
            var suspender = ((VmI64)dispatch.CoroutineCreate(Args(0, suspendSpec))).Value;
            TestHarness.Check("yield Alarm 段 SUSPENDED",
                ((VmI64)dispatch.CoroutineResume(Args(suspender))).Value.ToString(),
                VmDispatch.ResumeSuspended.ToString());
        }

        // ===== destroy 拒绝未终态句柄 =====
        private static void TestCoroutineDestroyRequiresTerminal()
        {
            var context = NewContext();
            var dispatch = context.Dispatch;
            var spec = dispatch.RegisterSpec(Fn(context, "$yielder("),
                Array.Empty<VmValue>());
            var handle = ((VmI64)dispatch.CoroutineCreate(Args(0, spec))).Value;
            dispatch.CoroutineResume(Args(handle));   // YIELDED（Runnable）
            try
            {
                dispatch.CoroutineDestroy(Args(handle));
                TestHarness.CheckTrue("未终态 destroy 应拒绝", false, "未抛错");
            }
            catch (VmException exception)
            {
                TestHarness.CheckTrue("未终态 destroy 清晰拒绝",
                    exception.Message.Contains("未终态"), exception.Message);
            }
        }

        // ===== TLS 当前上下文（主线程 = 0，对齐 rigi_rt）与时钟 =====
        private static void TestTlsAndTimeNow()
        {
            var dispatch = NewContext().Dispatch;
            TestHarness.Check("主线程 TLS 上下文 = 0",
                ((VmI64)dispatch.TlsCurrentContext(Array.Empty<VmValue>())).Value.ToString(), "0");
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var now = ((VmI64)dispatch.TimeNow(Array.Empty<VmValue>())).Value;
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            TestHarness.CheckTrue("time_now 在实时钟窗口内",
                now >= before && now <= after + 50,
                now + " 不在 [" + before + ", " + after + "]");
        }

        // ===== 死锁显败：live>0 且无 runnable 且无在途唤醒源 ⇒ 主 Worker
        // park 抛清晰诊断（不得无限 park，对齐 native 死锁诊断口径）=====
        private static void TestDeadlockDiagnosis()
        {
            var context = NewContext();
            var dispatch = context.Dispatch;
            // 只加 live 债务、不发布任何 runnable：live=1 悬空
            var noteSpawn = context.FindRuntimeFunction(
                "core.coroutine::Dispatcher$noteSpawn(")!;
            dispatch.InvokeIsolated(noteSpawn, new VmValue[]
            {
                context.GetSingleton("core.coroutine::Dispatcher")!,
            });
            try
            {
                dispatch.WorkerPark(Args(0));
                TestHarness.CheckTrue("死锁应显败", false, "park 无限阻塞未诊断");
            }
            catch (VmException exception)
            {
                TestHarness.CheckTrue("死锁诊断抛出",
                    exception.Message.Contains("死锁"), exception.Message);
            }
        }
    }
}
