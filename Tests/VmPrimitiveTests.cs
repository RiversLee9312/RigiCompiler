using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
            ("TestFsAppendOsAtomic", TestFsAppendOsAtomic),
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

        // ===== fs 追加原语（§4.5.6 系统追加，修复回归）：VM 追加面必须
        // 用 OS 追加专用句柄（Windows 仅 FILE_APPEND_DATA / Linux
        // O_APPEND），写入位置由单次系统调用内原子选择——不能用用户态
        // Seek(End)+Write 模拟（.NET FileMode.Append 两平台都不是系统
        // 追加，写走显式 offset；两步之间其他写入者可增长文件致覆盖，
        // playground/vm_append_atomic/ 探针固定交错复现旧失败）。本用例
        // 钉：外部增长后接当时末尾、双句柄屏障交错共存、双句柄并发编
        // 号记录每条恰好一次、getLength 实时 / Flush(true) / 关闭释放面。
        // 不承诺跨进程单笔大 write 的原子性（契约 §4.5.6）。
        // 生产面：VmDispatch.OpenAppendStream / AppendWriteCore（internal
        // 直驱；FsOpen/FsWriteBody 的挂起集成由 e2e fs_filestream 覆盖）
        private static void TestFsAppendOsAtomic()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "rigi_vm_append_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // ① 外部增长后追加：落当时末尾，前缀与外部数据不覆盖
                //（修复前 FileMode.Append 的 Write 落打开时位置、覆盖
                // 外部数据——探针 P1 实证）
                var p1 = Path.Combine(dir, "a.bin");
                File.WriteAllBytes(p1, Encoding.ASCII.GetBytes("ANCHOR-"));
                var rc = VmDispatch.OpenAppendStream(p1, out var opened);
                TestHarness.CheckTrue("追加流打开成功",
                    rc == 0 && opened != null, "rc=" + rc);
                using var s1 = opened!;
                using (var ext = new FileStream(p1, FileMode.Open,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                    1))
                {
                    ext.Seek(0, SeekOrigin.End);
                    ext.Write(new byte[] { 0xE1, 0xE2, 0xE3, 0xE4, 0xE5 });
                }
                TestHarness.CheckTrue("追加句柄 getLength 实时",
                    s1.Length == 12, "len=" + s1.Length);
                var written = VmDispatch.AppendWriteCore(s1,
                    Encoding.ASCII.GetBytes("A1"), 2);
                TestHarness.CheckTrue("外部增长后追加全量写出", written == 2,
                    "n=" + written);
                s1.Flush(true);   // flush 持久化面对追加专用句柄可用
                var bytes1 = ReadAllShared(p1);
                TestHarness.Check("追加落在当时末尾", bytes1.Length.ToString(),
                    "14");
                var text1 = Encoding.ASCII.GetString(bytes1);
                TestHarness.CheckTrue("前缀与外部数据不被覆盖",
                    text1.StartsWith("ANCHOR-") && bytes1[7] == 0xE1
                        && bytes1[11] == 0xE5,
                    text1.Replace("\0", "."));

                // ①b Linux 权限回归（§4.5.5：普通文件创建 mode 0666 受
                // umask）：同进程同 umask 下，追加专用句柄**创建**的文件
                // mode 必须与普通 FileStream(FileMode.CreateNew) 创建的
                // 完全一致——锁住 Append 不额外丢 group/other 位（曾误
                // 写 0o600：常量 0x180≠0x1B6，注释与值矛盾无校验故漏）。
                // 用对照法而不改 umask：umask 是进程属性，测试并行共享
                // 父进程，禁止全局改（Windows 无 Unix mode，跳过）
                if (!OperatingSystem.IsWindows())
                {
                    var pAppendCreated = Path.Combine(dir, "mode_append.bin");
                    var rcMode = VmDispatch.OpenAppendStream(pAppendCreated,
                        out var modeStream);
                    TestHarness.CheckTrue("权限段追加句柄打开成功",
                        rcMode == 0 && modeStream != null, "rc=" + rcMode);
                    using (modeStream!)
                    {
                        var pCtrl = Path.Combine(dir, "mode_ctrl.bin");
                        using (var cf = new FileStream(pCtrl,
                            FileMode.CreateNew, FileAccess.Write,
                            FileShare.Read, 1)) { }
                        var appendMode =
                            File.GetUnixFileMode(pAppendCreated);
                        var ctrlMode = File.GetUnixFileMode(pCtrl);
                        TestHarness.Check("追加创建 mode 与普通创建一致（同 umask）",
                            appendMode.ToString(), ctrlMode.ToString());
                    }
                }

                // ② 双句柄固定屏障交错：A 先「到位」（旧 Seek+Write 两步
                // 的 A 此时已 Seek 到末尾、位置即将过期），B 整笔写完放行
                // A——旧实现下 A 覆盖 B 记录前 4 字节（探针 P2 复现）；
                // 系统调用内选位必须双记录共存
                var p2 = Path.Combine(dir, "b.bin");
                File.WriteAllBytes(p2, Encoding.ASCII.GetBytes("ANCHOR-"));
                var rcA = VmDispatch.OpenAppendStream(p2, out var openedA);
                var rcB = VmDispatch.OpenAppendStream(p2, out var openedB);
                TestHarness.CheckTrue("双句柄打开成功",
                    rcA == 0 && rcB == 0 && openedA != null && openedB != null,
                    $"A={rcA} B={rcB}");
                using var sa = openedA!;
                using var sb = openedB!;
                var aReady = new ManualResetEventSlim(false);
                var bDone = new ManualResetEventSlim(false);
                Exception? threadError = null;
                var threadA = new Thread(() =>
                {
                    try
                    {
                        aReady.Set();
                        bDone.Wait();     // 等 B 整笔写完才落 A 的笔
                        if (VmDispatch.AppendWriteCore(sa,
                            Encoding.ASCII.GetBytes("AAAA"), 4) != 4)
                        {
                            throw new IOException("A 短写");
                        }
                    }
                    catch (Exception ex) { threadError = ex; }
                });
                var threadB = new Thread(() =>
                {
                    try
                    {
                        aReady.Wait();
                        if (VmDispatch.AppendWriteCore(sb,
                            Encoding.ASCII.GetBytes("BBBBBBBB"), 8) != 8)
                        {
                            throw new IOException("B 短写");
                        }
                    }
                    finally { bDone.Set(); }
                });
                threadA.Start();
                threadB.Start();
                threadA.Join();
                threadB.Join();
                TestHarness.CheckTrue("屏障交错无线程异常",
                    threadError == null, threadError?.Message ?? "");
                TestHarness.Check("屏障交错双记录共存无覆盖",
                    Encoding.ASCII.GetString(ReadAllShared(p2)),
                    "ANCHOR-BBBBBBBBAAAA");

                // ③ 双句柄并发编号记录：各 8 条定长记录（每条恰一次系统
                // 写），Barrier 每条对齐放大交错；断言最终长度、每条编号
                // 恰好出现一次、既有前缀完好
                var p3 = Path.Combine(dir, "c.bin");
                File.WriteAllBytes(p3, Encoding.ASCII.GetBytes("ANCHOR-"));
                rcA = VmDispatch.OpenAppendStream(p3, out var opened3a);
                rcB = VmDispatch.OpenAppendStream(p3, out var opened3b);
                TestHarness.CheckTrue("并发段双句柄打开成功",
                    rcA == 0 && rcB == 0 && opened3a != null && opened3b != null,
                    $"A={rcA} B={rcB}");
                using var s3a = opened3a!;
                using var s3b = opened3b!;
                var gate = new Barrier(2);
                var writeErrors = 0;
                void WriterLoop(FileStream stream, char tag)
                {
                    try
                    {
                        for (var i = 0; i < 8; i++)
                        {
                            gate.SignalAndWait();
                            var record = Encoding.ASCII.GetBytes(
                                tag.ToString() + i + ";");
                            if (VmDispatch.AppendWriteCore(stream, record,
                                record.Length) != record.Length)
                            {
                                Interlocked.Increment(ref writeErrors);
                            }
                        }
                    }
                    catch
                    {
                        Interlocked.Increment(ref writeErrors);
                    }
                }
                var writerA = new Thread(() => WriterLoop(s3a, 'A'));
                var writerB = new Thread(() => WriterLoop(s3b, 'B'));
                writerA.Start();
                writerB.Start();
                writerA.Join();
                writerB.Join();
                TestHarness.CheckTrue("并发编号记录全部全量写出",
                    writeErrors == 0, "错误数=" + writeErrors);
                var final = ReadAllShared(p3);
                TestHarness.Check("并发记录最终长度", final.Length.ToString(),
                    "55");
                var finalText = Encoding.ASCII.GetString(final);
                TestHarness.CheckTrue("既有前缀完好",
                    finalText.StartsWith("ANCHOR-"), finalText);
                for (var t = 0; t < 2; t++)
                {
                    var tag = (char)('A' + t);
                    for (var i = 0; i < 8; i++)
                    {
                        var record = tag + i.ToString() + ";";
                        var first = finalText.IndexOf(record,
                            StringComparison.Ordinal);
                        TestHarness.CheckTrue("记录 " + record + " 恰好一次",
                            first >= 0 && finalText.IndexOf(record,
                                first + 1, StringComparison.Ordinal) < 0,
                            "pos=" + first);
                    }
                }

                // ④ 关闭后句柄释放：全部流已 Dispose，目录可整体删除
                //（句柄未放漏则文件不再锁定——资源不泄漏钉子）
                Directory.Delete(dir, true);
                TestHarness.CheckTrue("关闭后资源释放（目录可清理）",
                    !Directory.Exists(dir));
            }
            finally
            {
                // 兜底清理（首删已断言；残留仅异常路径）
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // 共享读（追加句柄保持打开期间的 FileShare 兼容读取；
        // File.ReadAllBytes 默认 FileShare.Read 与打开的写句柄冲突）
        private static byte[] ReadAllShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16);
            var buffer = new byte[fs.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0) { break; }
                read += n;
            }
            return buffer;
        }
    }
}
