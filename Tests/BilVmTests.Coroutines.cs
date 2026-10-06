using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Coroutines 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestAsyncAwaitResult()
        {
            var result = Run(
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = add(41)\n" +
                "    var n = await t\n" +
                "    return n\n" +
                "}\n");
            CheckOk("async await 取值", result);
            CheckI32("await add(41)", result, 42);
            var none = Run(
                "async func ping() { }\n" +
                "pub func main(): i32 {\n" +
                "    await ping()\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("await 无结果 Task", none);
            CheckI32("void Task 之后", none, 1);
        }

        private static void TestAwaitExceptionAndCompleted()
        {
            var caught = Run(
                "async func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 3\n" +
                "    }\n" +
                "}\n");
            CheckOk("await 异常传播", caught);
            CheckI32("await 点 catch", caught, 3);
            var twice = Run(
                "async func quick(): i32 { return 5 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = quick()\n" +
                "    var a = await t\n" +
                "    var b = await t\n" +
                "    return a + b\n" +
                "}\n");
            CheckOk("await 已完成 Task", twice);
            CheckI32("二次 await 不重跑", twice, 10);
        }

        private static void TestForkJoinAndFireAndForget()
        {
            var join = Run(
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "pub func main(): i32 {\n" +
                "    var a = add(1)\n" +
                "    var b = add(2)\n" +
                "    var c = add(3)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    return ((x + y) + z)\n" +
                "}\n");
            CheckOk("fork/join", join);
            CheckI32("1+1 + 2+1 + 3+1", join, 9);
            var ghost = Run(
                "async func ghost() {\n" +
                "    throw new core.RuntimeException(\"bg\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    ghost()\n" +
                "    return 0\n" +
                "}\n");
            CaseAssertions.CheckTrue("fire-and-forget 后台异常经 quiescence 可见",
                ghost.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("RuntimeException"),
                ghost.Exception?.ToString() ?? "<null>");
            CaseAssertions.CheckTrue("fire-and-forget 入口仍返回",
                ghost.ReturnValue is VmI32 n && n.Value == 0,
                ghost.ReturnValue?.ToStandardText() ?? "<null>");
        }

        private static void TestYieldForms()
        {
            var bare = Run(
                "pub func main(): i32 {\n" +
                "    yield\n" +
                "    return 4\n" +
                "}\n");
            CheckOk("裸 yield", bare);
            CheckI32("裸 yield 后终结", bare, 4);
            var sleep = Run(
                "import core.coroutine.*\n" +
                "pub func main(): i32 {\n" +
                "    yield sleep(5)\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("yield EventAlarm/sleep", sleep);
            CheckI32("sleep 后恢复", sleep, 1);
            var poll = Run(
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool { return ready }\n" +
                "}\n" +
                "async func arm(f: Flip) {\n" +
                "    yield\n" +
                "    f.ready = true\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flip()\n" +
                "    arm(f)\n" +
                "    yield f\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("yield PollingAlarm", poll);
            CheckI32("翻牌后恢复", poll, 1);
        }

        // Phase 2.6（RUNTIME §19.2 语义纠偏）：isReady 是普通 Rigi 代码，
        // 允许 await/yield——探测挂起即继续等待，唤醒后在等待协程自己的
        // 恢复块内完成探测；返回 false 才退回等待，true 就绪续行；探测
        // 抛出视为发生在 yield 点（词法 try/catch 可捕获）。修复前毛边：
        // await 唤醒绕过就绪判定（false 也续行、_pollingAlarm 残留）、
        // 裸 yield 唤醒重复压帧（isReady 副作用翻倍、旧帧滞留）。
        private static void TestPollingProbeResumeSemantics()
        {
            // ① tainted isReady（经 AtomicStruct.load → Atomic.load 的
            // await mutex.acquire，探测中途真实挂起）：返回 false 必须退回
            // 等待，第 3 次探测才就绪续行——修复前 1 次探测即错误续行
            var slow = Run(
                "import core.coroutine.*\n" +
                "pub shared class SlowPoll : PollingAlarm {\n" +
                "    pub var probes: i64 = 0L\n" +
                "    pub override func isReady(): bool {\n" +
                "        probes = probes + 1L\n" +
                "        const t = new core.AtomicStruct\\<i64>(0L)\n" +
                "        const z = t.load()\n" +
                "        return probes >= 3L\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const s = new SlowPoll()\n" +
                "    yield s\n" +
                "    if (s.probes == 3L) { return 7 }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("tainted isReady 探测挂起恢复链", slow);
            CheckI32("探测 false 退回等待、第 3 次就绪（probes==3）", slow, 7);

            // ② 裸 yield in isReady：挂起即继续等待，唤醒后续跑滞留探测帧
            // 至完成——修复前重复压帧，isReady 被执行两次
            var yieldOnce = Run(
                "import core.coroutine.*\n" +
                "pub shared class YieldOnce : PollingAlarm {\n" +
                "    pub var probes: i64 = 0L\n" +
                "    pub var first: bool = true\n" +
                "    pub override func isReady(): bool {\n" +
                "        probes = probes + 1L\n" +
                "        if (first) {\n" +
                "            first = false\n" +
                "            yield\n" +
                "        }\n" +
                "        return true\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const y = new YieldOnce()\n" +
                "    yield y\n" +
                "    if (y.probes == 1L) { return 5 }\n" +
                "    return 2\n" +
                "}\n");
            CheckOk("裸 yield in isReady 恢复链", yieldOnce);
            CheckI32("滞留帧续跑、isReady 只执行一次（probes==1）", yieldOnce, 5);

            // ③ 探测挂起后中途抛出：失败点在 yield 点——词法 try/catch
            // 可捕获；捕获后轮询状态清理，协程沿 catch 续行
            var boom = Run(
                "import core.coroutine.*\n" +
                "pub shared class BoomPoll : PollingAlarm {\n" +
                "    pub var probes: i64 = 0L\n" +
                "    pub override func isReady(): bool {\n" +
                "        probes = probes + 1L\n" +
                "        const t = new core.AtomicStruct\\<i64>(0L)\n" +
                "        const z = t.load()\n" +
                "        if (probes >= 2L) {\n" +
                "            throw new core.RuntimeException(\"boom-probe\")\n" +
                "        }\n" +
                "        return false\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const b = new BoomPoll()\n" +
                "    var r: i32 = 0\n" +
                "    try {\n" +
                "        yield b\n" +
                "        r = 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        r = 2\n" +
                "    }\n" +
                "    if (b.probes == 2L) { r = r + 10 }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("探测中途异常经挂起恢复后抛出", boom);
            CheckI32("异常落 yield 点被 catch（第 2 次探测，返回 12）", boom, 12);

            // ④ untainted isReady（纯同步）：廉价路径语义不变——首次
            // false 同样退回等待，翻牌后下次探测就绪（探测 ≥ 2 次）
            var tally = Run(
                "import core.coroutine.*\n" +
                "pub shared class TallyPoll : PollingAlarm {\n" +
                "    pub var probes: i64 = 0L\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool {\n" +
                "        probes = probes + 1L\n" +
                "        return ready\n" +
                "    }\n" +
                "}\n" +
                "async func armTally(t: TallyPoll) {\n" +
                "    yield\n" +
                "    t.ready = true\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const t = new TallyPoll()\n" +
                "    armTally(t)\n" +
                "    yield t\n" +
                "    if (t.probes >= 2L) { return 9 }\n" +
                "    return 3\n" +
                "}\n");
            CheckOk("untainted isReady 同步探测", tally);
            CheckI32("同步探测 false 退回等待、翻牌后就绪（probes>=2）", tally, 9);
        }

        private static void TestConcurrentPrintLines()
        {
            var result = Run(
                "async func say(msg: String) {\n" +
                "    core.io.Console.println(msg + \"\\n\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    say(\"one\")\n" +
                "    say(\"two\")\n" +
                "    say(\"three\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("并发 print", result);
            CaseAssertions.CheckTrue("行 one 完整出现", result.Stdout.Contains("one\n"),
                result.Stdout);
            CaseAssertions.CheckTrue("行 two 完整出现", result.Stdout.Contains("two\n"),
                result.Stdout);
            CaseAssertions.CheckTrue("行 three 完整出现", result.Stdout.Contains("three\n"),
                result.Stdout);
        }

        private static void TestAwaitThroughTryFinally()
        {
            var ok = Run(
                "async func pause(): i32 {\n" +
                "    yield\n" +
                "    return 9\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var n = await pause()\n" +
                "        return n\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("await 穿越 try/finally", ok);
            CaseAssertions.Check("恢复后 finally", ok.Stdout, "fin\n");
            CheckI32("finally 后返回", ok, 9);
            var boom = Run(
                "async func boom(): i32 {\n" +
                "    yield\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 2\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("await 异常穿越 finally", boom);
            CaseAssertions.Check("异常路径 finally", boom.Stdout, "fin\n");
            CheckI32("catch 返回", boom, 2);
        }

        private static void TestCoroutineStressForkJoin()
        {
            var result = Run(
                "async func tree(n: i32): i32 {\n" +
                "    if (n <= 0) {\n" +
                "        return 1\n" +
                "    }\n" +
                "    var left = tree(n - 1)\n" +
                "    var right = tree(n - 1)\n" +
                "    var a = await left\n" +
                "    var b = await right\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var root = tree(7)\n" +
                "    var n = await root\n" +
                "    return n\n" +
                "}\n");
            CheckOk("100+ 协程 fork/join", result);
            CheckI32("tree(7) = 128", result, 128);
        }

    }
}
