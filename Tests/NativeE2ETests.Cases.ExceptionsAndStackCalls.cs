using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // 原编号 306..344 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateExceptionsAndStackCallsCases() => new (string Label, Action Run)[]
        {
            Case("finally 在异常路径执行后外层捕获",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"x\")\n" +
                "        } catch (_: core.IOException) {\n" +
                "            Console.println(\"no\")\n" +
                "        } finally(_) {\n" +
                "            Console.println(\"fin\")\n" +
                "        }\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 8\n" +
                "}\n"),
            Case("finally(e) 槽语义：无异常 null、异常非空",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        Console.println(\"t1\")\n" +
                "    } finally(e) {\n" +
                "        if (e == null) { Console.println(\"null\") }\n" +
                "        else { Console.println(\"exc\") }\n" +
                "    }\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"boom\")\n" +
                "        } finally(e) {\n" +
                "            if (e == null) { Console.println(\"null2\") }\n" +
                "            else { Console.println(\"exc:\" + e.getMessage()) }\n" +
                "        }\n" +
                "    } catch (_: core.Exception) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "    return 3\n" +
                "}\n"),
            Case("catch 内 rethrow 被外层捕获",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.IOException(\"inner\")\n" +
                "        } catch (e: core.IOException) {\n" +
                "            Console.println(\"c1:\" + e.getMessage())\n" +
                "            throw new core.RuntimeException(\"re\")\n" +
                "        }\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"c2:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 6\n" +
                "}\n"),
            Case("break/continue 穿越 finally",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        i = (i + 1)\n" +
                "        try {\n" +
                "            if (i == 2) { break }\n" +
                "            continue\n" +
                "        } finally(_) {\n" +
                "            Console.println(\"f\")\n" +
                "        }\n" +
                "        Console.println(\"tail\")\n" +
                "    }\n" +
                "    return i\n" +
                "}\n"),
            Case("return 穿越 finally（退出码 42）",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 42\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n"),
            Case("throw 跨函数传播捕获",
                "import core.io.Console\n" +
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        boom()\n" +
                "        return 0\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("嵌套 try：内层未命中、外层类型命中",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.IOException(\"io\")\n" +
                "        } catch (_: core.CastException) {\n" +
                "            Console.println(\"no\")\n" +
                "        }\n" +
                "        return 0\n" +
                "    } catch (e: core.IOException) {\n" +
                "        Console.println(\"io:\" + e.getMessage())\n" +
                "        return 5\n" +
                "    }\n" +
                "}\n"),
            Case("自定义异常类多态 getMessage",
                "import core.io.Console\n" +
                "pub open class MyException : core.RuntimeException {\n" +
                "    pub init(text: String) { message = text }\n" +
                "    pub override func getMessage(): String { return \"custom:\" + message }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyException(\"boom\")\n" +
                "        return 0\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 9\n" +
                "    }\n" +
                "}\n"),
            // ===== MW9b-G：native 守卫点抛真异常（与 VM 同型同消息，
            // 可被 try/catch 捕获）对拍——除零/cast/拆箱/new.indirect/
            // 数组·Span 越界写六守卫 + 未捕获 reporter 新格式 =====
            Case("try/catch 捕获整数除零",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    try {\n" +
                "        return (x / z)\n" +
                "    } catch (e: core.DividedByZeroException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获 as 拆箱失败",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = 42 as Any\n" +
                "    try {\n" +
                "        var s = a as String\n" +
                "        Console.println(s)\n" +
                "        return 0\n" +
                "    } catch (e: core.CastException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获占位 cast 失败",
                "import core.io.Console\n" +
                "pub func conv\\<T>(x: Any): T { return x as T }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var s = conv\\<String>(42 as Any)\n" +
                "        Console.println(s)\n" +
                "        return 0\n" +
                "    } catch (e: core.CastException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 6\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获 new.indirect 无匹配 init",
                "import core.io.Console\n" +
                "pub class OnlyI32 {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var t = typeOf(OnlyI32)\n" +
                "        var o = new t(true)\n" +
                "        return 0\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 4\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获数组写越界",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    try {\n" +
                "        a[5] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            Case("try/catch 捕获 Span 写越界",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = spanOf\\<i32>(3)\n" +
                "    try {\n" +
                "        s[(0 - 1)] = 1\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n"),
            // review-20260910 #13：越界写发生在被调方（用户 setAtIndex 算子
            // 帧）时，异常边须跨帧传播到调用方 try——IndexOperatorLowering
            // 改写 MirSetArray→MirCall 必须透传 ExcTarget
            Case("try/catch 捕获被调方算子帧的数组写越界",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Ring2 {\n" +
                "    priv var slots: Array\\<i64>\n" +
                "    pub init(cap: i32) { slots = arrayOf\\<i64>(cap) }\n" +
                "    pub operator setAtIndex(index: i32, value: i64) { slots[index] = value }\n" +
                "}\n" +
                "pub func writeThrough(r: Ring2, i: i32) {\n" +
                "    r[i] = 9L\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = new Ring2(3)\n" +
                "    try {\n" +
                "        writeThrough(r, 9)\n" +
                "        return 0\n" +
                "    } catch (e: core.OutOfBoundException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 9\n" +
                "    }\n" +
                "}\n"),
            // 未捕获：两侧同为「{类型全名}: {message}」+ exit 1；
            // nativeNeedle 断 reporter 新格式全名前缀
            FailCase("用户 throw 未捕获顶层格式",
                "pub func main(): i32 {\n" +
                "    throw new core.RuntimeException(\"boom\")\n" +
                "}\n", "boom", "core::RuntimeException: boom"),
            FailCase("除零未捕获顶层格式",
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    return (x / z)\n" +
                "}\n", "整数除以零", "core::DividedByZeroException: 整数除以零"),
            FailCase("数组越界写未捕获顶层格式",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[9] = 2\n" +
                "    return 0\n" +
                "}\n", "数组下标越界", "core::OutOfBoundException: 数组下标越界：9（长度 3）"),
            // review-20260910 #03：§12.1 用户自定义转换 castFrom/castTo
            //（VM 运行时分派 + native 编译期重写双通道同语义）
            Case("自定义转换 castFrom（目标类型 operator）",
                "import core.io.Console\n" +
                "pub struct Dur {\n" +
                "    pub var ms: i64\n" +
                "    pub init(_ -> ms)\n" +
                "    pub operator castFrom\\<TSource>(raw: TSource): Dur {\n" +
                "        return new Dur((raw as i64))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const d = (750L as Dur)\n" +
                "    if (d.ms != 750L) { return 1 }\n" +
                "    Console.println(\"castFrom ok\")\n" +
                "    return 0\n" +
                "}\n"),
            Case("自定义转换 castTo（源类型 operator）",
                "import core.io.Console\n" +
                "pub struct Dur {\n" +
                "    pub var ms: i64\n" +
                "    pub init(_ -> ms)\n" +
                "}\n" +
                "pub struct Ticks {\n" +
                "    pub var v: i64\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator castTo\\<TTarget>(): TTarget {\n" +
                "        return (new Dur((v * 2L)) as TTarget)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const d = (new Ticks(21L) as Dur)\n" +
                "    if (d.ms != 42L) { return 1 }\n" +
                "    Console.println(\"castTo ok\")\n" +
                "    return 0\n" +
                "}\n"),
            // DateTime.now()（core.time 的 rigi_time_now → rigi_rt time_now）：
            // 窗口断言用固定历史常数（2001-09-09 起毫秒），不对拍墙钟字面量
            Case("DateTime.now 时钟原语窗口",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var now = core.time.DateTime.now()\n" +
                "    if (now.stamp.milliseconds > 1000000000000L) {\n" +
                "        Console.println(\"time now ok\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // DateTime.now 亚毫秒单采样（§4.9.4：time_now_parts 一次 UTC
            // 采样写 12 字节小端 i64 毫秒 + i32 毫秒外纳秒余量，不拆两
            // 次采样）。VM/native 共用 e2e 语料断言纳秒余量范围、毫秒
            // 窗口与年份窗口，避免复制两份测试源码
            Case("DateTime.now 亚毫秒单采样对拍", SerializationGraphCorpus("time_now_precision")),
            // ===== MW11a 棒3 协程对拍（VM 母本移植；B-1 起 main 与
            // 其同步调用链可直接挂起——本组保留「await 收进 async run()
            // 体内」写法作回归，main 直接 await 形态见下方 B-1 组；
            // stdout 打印只放 await/join 之后的数据依赖确定位置，不断言
            // 并发交错序）=====
            // ① fork/join 取值（VM TestForkJoinAndFireAndForget join 母本）：
            // 三路 spawn → run 内 await → 求和打印 9
            Case("协程 fork/join 取值",
                "import core.io.Console\n" +
                "async func add(n: i32): i32 { return n + 1 }\n" +
                "async func run() {\n" +
                "    var a = add(1)\n" +
                "    var b = add(2)\n" +
                "    var c = add(3)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    Console.println(((x + y) + z).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ② await 已完成 Task + 二次 await 同结果（VM
            // TestAwaitExceptionAndCompleted twice 母本）：打印 10
            Case("协程二次 await 同结果",
                "import core.io.Console\n" +
                "async func quick(): i32 { return 5 }\n" +
                "async func run() {\n" +
                "    var t = quick()\n" +
                "    var a = await t\n" +
                "    var b = await t\n" +
                "    Console.println((a + b).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ③ async 抛异常 → await 点重抛 → try/catch 捕获打印（VM
            // TestAwaitExceptionAndCompleted caught 母本）
            Case("协程 await 异常重抛捕获",
                "import core.io.Console\n" +
                "async func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "async func run() {\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ④ fire-and-forget：spawn 后不 await，main return 后协程仍
            // 被 drain 等待执行完成，其打印出现在 stdout
            Case("协程 fire-and-forget drain 等待",
                "import core.io.Console\n" +
                "async func bg() {\n" +
                "    yield\n" +
                "    Console.println(\"bg-done\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    bg()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑤ 未观察失败（FailCase）：fire-and-forget 抛异常 → main
            // 正常返回 0 后 drain 到终态 → 进程 exit 1 + stderr
            // 「{类型全名}: {message}」（VM 侧查消息关键字，native 侧查
            // reporter 全名前缀格式）
            FailCase("协程未观察失败顶层格式",
                "async func ghost() {\n" +
                "    throw new core.RuntimeException(\"bg\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    ghost()\n" +
                "    return 0\n" +
                "}\n", "bg", "core::RuntimeException: bg"),
            // ⑥ 裸 yield：两协程各 yield 后完成 join，断言最终求和值
            //（交错序不断言）
            Case("协程裸 yield join",
                "import core.io.Console\n" +
                "async func step(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n\n" +
                "}\n" +
                "async func run() {\n" +
                "    var a = step(20)\n" +
                "    var b = step(22)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    Console.println((x + y).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑦ await 穿越 try/finally 双路径（VM
            // TestAwaitThroughTryFinally 母本）：正常返回与异常路径
            // finally 均执行（单协程内顺序确定）
            Case("协程 await 穿越 try/finally 双路径",
                "import core.io.Console\n" +
                "async func pause(): i32 {\n" +
                "    yield\n" +
                "    return 9\n" +
                "}\n" +
                "async func boom(): i32 {\n" +
                "    yield\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "async func run() {\n" +
                "    try {\n" +
                "        var n = await pause()\n" +
                "        Console.println(\"ok:\" + n.toString())\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin1\")\n" +
                "    }\n" +
                "    try {\n" +
                "        await boom()\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin2\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== B-1 栈式跨界（SYNTAX §11：main 以及由它同步调用
            // 的普通 fn 可以直接 await/yield）：非 async 挂起点全链
            // 支持，对拍 VM =====
            // B-1① main 直接 await（await 目标真挂起——yield 强制跨
            // 执行段）：stdout 42 + 退出码 42
            Case("栈式跨界 main 直接 await",
                "import core.io.Console\n" +
                "async func add(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = add(41)\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return r\n" +
                "}\n"),
            // B-1② main → 单层同步 fn（内含 await）：形参/局部跨
            // 挂起保存（acc 在 await 后仍须正确），stdout 45
            Case("栈式跨界 同步 fn 内 await 参数局部保持",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func compute(a: i32, b: i32): i32 {\n" +
                "    var acc = a + b\n" +
                "    var t = slow(acc)\n" +
                "    var r = await t\n" +
                "    return r + acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = compute(10, 5)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-1③ 终态快路径：await 已完成 Task 不挂起（无 yield）
            Case("栈式跨界 main await 已完成 Task",
                "import core.io.Console\n" +
                "async func quick(): i32 { return 5 }\n" +
                "pub func main(): i32 {\n" +
                "    var t = quick()\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return r\n" +
                "}\n"),
            // B-1④ 异常跨链：tainted callee 恢复后抛出 → FAILED 沿
            // 调用点原 ExcTarget 进 main 的 try 派发垫捕获
            Case("栈式跨界 同步 fn 恢复后抛出被 main 捕获",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func risky(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    if (r > 100) {\n" +
                "        throw new core.RuntimeException(\"too-big\")\n" +
                "    }\n" +
                "    return r\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return risky(60)\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n"),
            // B-1⑤ main 失败链路（FailCase）：tainted callee 未捕获
            // 异常 → main Task FAILED → settle 重抛 → 顶层 reporter
            FailCase("栈式跨界 main 失败顶层格式",
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func boom(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    throw new core.RuntimeException(\"deep\")\n" +
                "    return r\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return boom(3)\n" +
                "}\n", "deep", "core::RuntimeException: deep"),
            // ===== B-2 全组合收口 =====
            // B-2① async fn 体直调 tainted 普通 fn：整条栈同步挂起，
            // 结果是裸返回值（不是 Task——栈式语义），求和 62
            Case("栈式跨界 async 调 tainted 裸返回",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func work(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    return r + 1\n" +
                "}\n" +
                "async func run(): i32 {\n" +
                "    var x = work(10)\n" +
                "    var y = work(20)\n" +
                "    return x + y\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2② Task.State 投影可观察性：async 调用方的栈悬在
            // tainted 链内时其 Task 投影 Suspended（prop 臂 markSuspended
            // 与 VM 对齐）；观察协程 yield 让步后读 state
            Case("栈式跨界 async 调用方 Task.State 投影",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func tag(s: TaskState): String {\n" +
                "    if (s is .Created) { return \"created\" }\n" +
                "    if (s is .Runnable) { return \"runnable\" }\n" +
                "    if (s is .Suspended) { return \"suspended\" }\n" +
                "    if (s is .Completed) { return \"completed\" }\n" +
                "    if (s is .Failed) { return \"failed\" }\n" +
                "    return \"cancelled\"\n" +
                "}\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func work(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    return r + 1\n" +
                "}\n" +
                "async func run(): i32 {\n" +
                "    return work(10)\n" +
                "}\n" +
                "async func watch(t: Task\\<i32>) {\n" +
                "    yield\n" +
                "    Console.println(\"mid:\" + tag(t.state))\n" +
                "    var r = await t\n" +
                "    Console.println(\"end:\" + tag(t.state))\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    var w = watch(t)\n" +
                "    await w\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2③ 三层嵌套 tainted 链（main→level1→level2→level3→
            // await）：每级调用点都是调用方的挂起点，acc 跨层保持 14
            Case("栈式跨界 三层嵌套挂起链",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "func level3(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    return r + 1\n" +
                "}\n" +
                "func level2(n: i32): i32 {\n" +
                "    var acc = level3(n) + 1\n" +
                "    return acc\n" +
                "}\n" +
                "func level1(n: i32): i32 {\n" +
                "    var acc = level2(n) + 1\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = level1(10)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2④ 直接递归 tainted fn：每层 yield 挂起整链，恢复沿
            // 链逐层下钻（down(5)=15）
            Case("栈式跨界 直接递归逐层挂起",
                "import core.io.Console\n" +
                "func down(n: i32): i32 {\n" +
                "    if (n <= 0) {\n" +
                "        return 0\n" +
                "    }\n" +
                "    yield\n" +
                "    return n + down(n - 1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = down(5)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑤ 互递归 tainted fn（isOdd/isEven 经 yield 互挂）：
            // taint 不动点闭包覆盖环
            Case("栈式跨界 互递归挂起",
                "import core.io.Console\n" +
                "func isOdd(n: i32): bool {\n" +
                "    if (n <= 0) {\n" +
                "        return false\n" +
                "    }\n" +
                "    yield\n" +
                "    return isEven(n - 1)\n" +
                "}\n" +
                "func isEven(n: i32): bool {\n" +
                "    if (n <= 0) {\n" +
                "        return true\n" +
                "    }\n" +
                "    yield\n" +
                "    return isOdd(n - 1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = isOdd(7)\n" +
                "    var b = isEven(8)\n" +
                "    Console.println(a.toString() + b.toString())\n" +
                "    return 0\n" +
                "}\n"),

        };

    }
}
