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
        // 原编号 422..460 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateGcAndCollectionsCases() => new (string Label, Action Run)[]
        {
            // 环中对象未 dispose：局部环 main 结束时成候选（阈值不到不
            // 触发），shutdown 终轮收集兜底 → 晚到事件不经用户处理器，
            // 由 C 侧 atexit flush 默认打印（同一文本）。VM 侧 .NET GC
            // 一轮即收环，事件走 Run 收尾 drain 的默认分支——stderr
            // needle 同文本（事件时机两宿主天然不同，只断言文本）
            NativeErrCase("mw12b_undisposed_in_cycle",
                "import core.io.Console\n" +
                "pub class Node implements core.IDisposable {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    a.next = b\n" +
                "    b.next = a\n" +
                "    Console.println(\"cycled\")\n" +
                "    return 0\n" +
                "}\n",
                "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：Node"),
            // MW12b VM 半场（mw12b2）：非 main 帧局部 undisposed 对象——
            // 辅助 fn 帧弹出后对象脱根 → 默认 stderr 打印；覆盖「销毁检查
            // 不限于 main 帧」的帧根释放路径，stderr needle 双宿主同文本
            NativeErrCase("mw12b2_undisposed_default_vm",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "func leak() {\n" +
                "    const r = new Res()\n" +
                "    Console.println(\"leaked\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    leak()\n" +
                "    return 0\n" +
                "}\n",
                "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：Res"),
            // ===== MW12c：macroGC 循环回收与泄漏检查套件 =====
            // 口径：VM 对拍 stdout/退出码 + 产物恒带 RIGI_RT_MEMTRACK=1，
            // memtrack 零泄漏即「环被收掉」。默认阈值（1MiB）下局部环靠
            // shutdown 终轮收集兜底；低阈值例走 EnvCase 触发 mid-run pass
            // 双对象环：函数作用域释放后成纯环，终轮收集兜底
            Case("mw12c_cycle_two",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    a.next = b\n" +
                "    b.next = a\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"two dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 自环：单对象 next 指向自身，作用域释放后终轮收集
            Case("mw12c_cycle_self",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var s = new Node()\n" +
                "    s.next = s\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"self dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // mid-run 触发：RIGI_RT_GC_THRESHOLD=1024，5000 轮双对象环
            // → 多轮 pass（fence 慢路径真实触发），stdout 对拍 + 零泄漏
            EnvCase("mw12c_cycle_midrun",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var pad: i32\n" +
                "    pub init() { pad = 0 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var n = 0\n" +
                "    while (i < 5000) {\n" +
                "        var a = new Node()\n" +
                "        var b = new Node()\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        a.pad = i\n" +
                "        n = (n + 1)\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    if (n == 5000) { Console.println(\"midrun ok\") }\n" +
                "    return 0\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "1024" }),
            // 超长环：10 万节点链表首尾相接——收集器显式 trace 栈防深
            // 递归（退化成递归这里会爆栈），终轮收集后零泄漏
            Case("mw12c_cycle_long_chain",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeRing() {\n" +
                "    var head = new Node()\n" +
                "    var prev = head\n" +
                "    var i = 1\n" +
                "    while (i < 100000) {\n" +
                "        var cur = new Node()\n" +
                "        prev.next = cur\n" +
                "        prev = cur\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    prev.next = head\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeRing()\n" +
                "    Console.println(\"ring dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 环带外部活引用：低阈值 mid-run pass 后继续使用活引用
            // （打印经环边到达的对端字段）→ 存活对象不误收；断开活引用
            // 后环脱根 → 终轮收掉
            EnvCase("mw12c_cycle_live_ref",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "}\n" +
                "func makeCycle(): Node {\n" +
                "    var a = new Node(7)\n" +
                "    var b = new Node(8)\n" +
                "    a.next = b\n" +
                "    b.next = a\n" +
                "    return a\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var keep: Node? = makeCycle()\n" +
                "    var i = 0\n" +
                "    while (i < 3000) {\n" +
                "        var x = new Node(i)\n" +
                "        var y = new Node(i)\n" +
                "        x.next = y\n" +
                "        y.next = x\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    var t = keep?.next?.tag if? -1\n" +
                "    Console.println(t.toString())\n" +
                "    keep = null\n" +
                "    Console.println(\"released\")\n" +
                "    return 0\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "1024" }),
            // 经数组元素成环：a → peers 数组 → b → peers 数组，收集器
            // 数组 32B 前缀元素走查必须命中（漏走=泄漏）
            Case("mw12c_cycle_through_array",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var peers: Array\\<Node>?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var arr = arrayOf\\<Node>(2)\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    arr[0] = a\n" +
                "    arr[1] = b\n" +
                "    a.peers = arr\n" +
                "    b.peers = arr\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"array cycle dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 环边经过 tag1 盒：rich struct（含 class 引用字段）装箱成
            // Any 存进另一对象字段——盒内递归走查漏走=泄漏、误走=UAF
            Case("mw12c_cycle_through_box",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var payload: Any?\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub rich struct Ref {\n" +
                "    pub var target: Node?\n" +
                "    pub init(_ -> target)\n" +
                "}\n" +
                "func makeCycle() {\n" +
                "    var a = new Node()\n" +
                "    var b = new Node()\n" +
                "    var r = new Ref(a)\n" +
                "    b.payload = (r as Any)\n" +
                "    a.payload = b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    makeCycle()\n" +
                "    Console.println(\"box cycle dropped\")\n" +
                "    return 0\n" +
                "}\n"),
            // 跨协程 shared 环（§4.7）：两个 async 协程各持一端互指，
            // await 完成后读取对端字段（数据依赖确定位置），run 帧弹出
            // 后环脱根 → 终轮收集
            Case("mw12c_cycle_shared_cross_coroutine",
                "import core.io.Console\n" +
                "pub shared class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "}\n" +
                "async func holdA(a: Node, b: Node) {\n" +
                "    a.next = b\n" +
                "    yield\n" +
                "}\n" +
                "async func holdB(a: Node, b: Node) {\n" +
                "    b.next = a\n" +
                "    yield\n" +
                "}\n" +
                "async func run() {\n" +
                "    var a = new Node(1)\n" +
                "    var b = new Node(2)\n" +
                "    var ta = holdA(a, b)\n" +
                "    var tb = holdB(a, b)\n" +
                "    await ta\n" +
                "    await tb\n" +
                "    var x = a.next?.tag if? -1\n" +
                "    Console.println(x.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 混合压力：环 + 普通垃圾 + 字符串 + 数组，低阈值长跑
            // （10 万级），sum 双宿主对拍 + 零泄漏
            EnvCase("mw12c_stress_mixed",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var i = 0\n" +
                "    var sum = 0\n" +
                "    while (i < 100000) {\n" +
                "        var a = new Node(i)\n" +
                "        var b = new Node((i + 1))\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        var s = (\"n\" + i.toString())\n" +
                "        var arr = arrayOf\\<String>(2)\n" +
                "        arr[0] = s\n" +
                "        arr[1] = \"x\"\n" +
                "        var t = arr[0] if? \"\"\n" +
                "        if (t == s) { sum = (sum + 1) }\n" +
                "        if ((i & 3) == 0) { sum = (sum + a.tag) } else { sum = (sum + b.tag) }\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    Console.println(sum.toString())\n" +
                "    return 0\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "512" }),
            // Phase 1.3 pin-before-sub 并发回归：4 个 Worker Task 对同一
            // may-cycle shared 8 节点环入口高频 acquire/release（打击在册
            // 对象 PURPLE 快路径与 pin 登记路径），同时各自 churn 局部双
            // 节点环（登记/终态免锁析构/pass 账本归还全路径）；64KiB 低
            // 阈值强制 GC pass 与 mutator 并发交错。断言：校验和（双宿主
            // 对拍）+ 退出码 + memtrack 零泄漏（MemtrackEnv 恒在）——
            // 「登记到已 free 指针」或终态误判在此形态下必现崩溃/泄漏
            EnvCase("mw13_shared_cycle_concurrent_release",
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "import core.coroutine.*\n" +
                "pub shared class SNode {\n" +
                "    pub var next: SNode?\n" +
                "    pub var v: i64\n" +
                "    pub init(_ -> v) {\n" +
                "        next = null\n" +
                "    }\n" +
                "}\n" +
                "const cwTasks: i32 = 4\n" +
                "const cwRounds: i32 = 50000\n" +
                "pub async func cycleWorker(n: SNode?, rounds: i32, id: i32): i64 {\n" +
                "    var acc = 0L\n" +
                "    var r = 0\n" +
                "    while (r < rounds) {\n" +
                "        var t = n\n" +
                "        acc = (acc + (t?.next?.v if? 0L))\n" +
                "        t = null\n" +
                "        var a = new SNode((id as i64))\n" +
                "        var b = new SNode((id as i64))\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        r = (r + 1)\n" +
                "    }\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var first = new SNode(0L)\n" +
                "    var prev = first\n" +
                "    var i = 1\n" +
                "    while (i < 8) {\n" +
                "        var cur = new SNode((i as i64))\n" +
                "        prev.next = cur\n" +
                "        prev = cur\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    prev.next = first\n" +
                "    const headRef: SNode? = first\n" +
                "    var head: SNode? = first\n" +
                "    const handles = arrayOf\\<Task\\<i64>>(cwTasks)\n" +
                "    var k = 0\n" +
                "    while (k < cwTasks) {\n" +
                "        const id = k\n" +
                "        const t = new Task\\<i64>(func{async (): i64 -> await cycleWorker(headRef, cwRounds, id)})\n" +
                "        t.run(new ComputeExecutor())\n" +
                "        handles[k] = t\n" +
                "        k = (k + 1)\n" +
                "    }\n" +
                "    var chk = 0L\n" +
                "    k = 0\n" +
                "    while (k < cwTasks) {\n" +
                "        chk = (chk + (await (handles[k] as Task\\<i64>)))\n" +
                "        k = (k + 1)\n" +
                "    }\n" +
                "    head = null\n" +
                "    const expect = ((cwTasks as i64) * (cwRounds as i64))\n" +
                "    if (chk == expect) {\n" +
                "        Console.println(\"pin-concurrent ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    Console.println(\"pin-concurrent bad\")\n" +
                "    return 1\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "65536" }),
            // ===== Phase 3d-1：local 会计非原子化 + per-协程候选账本 +
            // 属主协作收集回归 =====
            // 协程内 local 环在非挂起循环（纯 while churn）下被债务触发
            // 收掉：release 非终态登记属主账本（非原子 rc）→ 债务超阈值
            // 就地跑属主收集（三阶段局部变体）→ memtrack 零泄漏（账本
            // 收干 + 协程终态兜底）+ 校验和双宿主对拍（环被收不影响
            // 已读出的值）
            EnvCase("mw13d1_local_cycle_coroutine_churn",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) {\n" +
                "        next = null\n" +
                "    }\n" +
                "}\n" +
                "async func churn(rounds: i32): i32 {\n" +
                "    var acc = 0\n" +
                "    var r = 0\n" +
                "    while (r < rounds) {\n" +
                "        var a = new Node(r)\n" +
                "        var b = new Node(r)\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        acc = (acc + (a.next?.v if? 0))\n" +
                "        r = (r + 1)\n" +
                "    }\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = new Task\\<i32>(func{async (): i32 -> await churn(5000)})\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    var r = await t\n" +
                "    const expect = 12497500\n" +
                "    if (r == expect) {\n" +
                "        Console.println(\"co churn ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    Console.println(\"co churn bad\")\n" +
                "    return 1\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "1024" }),
            // 协程内 local 环在 yield 循环下被收：挂起/恢复不改变 release
            // 热路径的债务触发语义（挂起点顺手小回收为 3d-2）；断言同上
            EnvCase("mw13d1_local_cycle_yield_loop",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub class Node {\n" +
                "    pub var next: Node?\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) {\n" +
                "        next = null\n" +
                "    }\n" +
                "}\n" +
                "async func churn(rounds: i32): i32 {\n" +
                "    var acc = 0\n" +
                "    var r = 0\n" +
                "    while (r < rounds) {\n" +
                "        var a = new Node(r)\n" +
                "        var b = new Node(r)\n" +
                "        a.next = b\n" +
                "        b.next = a\n" +
                "        acc = (acc + (a.next?.v if? 0))\n" +
                "        if ((r & 63) == 0) { yield }\n" +
                "        r = (r + 1)\n" +
                "    }\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = new Task\\<i32>(func{async (): i32 -> await churn(5000)})\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    var r = await t\n" +
                "    const expect = 12497500\n" +
                "    if (r == expect) {\n" +
                "        Console.println(\"co yield ok\")\n" +
                "        return 0\n" +
                "    }\n" +
                "    Console.println(\"co yield bad\")\n" +
                "    return 1\n" +
                "}\n",
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "1024" }),
            // ===== imap/getAtIndex 回归（Bug1 接口 iMap 恒模板键 /
            // Bug2a 泛型占位数组元素运行时 stride / Bug2b 接口派发
            // 结果拆箱）=====
            // ① for-in over List<i32> 求和（最小 imap 派发路径）
            Case("for-in List<i32> 求和",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    list.add(2)\n" +
                "    list.add(3)\n" +
                "    var sum: i32 = 0\n" +
                "    for (x in list) {\n" +
                "        sum = (sum + x)\n" +
                "    }\n" +
                "    Console.println(\"sum=${sum}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ② wrapper + for-in（imap_repro 母本）：打印 done 3
            Case("wrapper + for-in List<i32>",
                "import core.collections.*\n" +
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamped {\n" +
                "    pub var min: i32\n" +
                "    pub var max: i32\n" +
                "    pub init() {\n" +
                "        min = 0\n" +
                "        max = 100\n" +
                "    }\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) {\n" +
                "        var v = (value as i32)\n" +
                "        if ((v > max)) { v = max }\n" +
                "        if ((v < min)) { v = min }\n" +
                "        inner((v as TValue))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    list.add(2)\n" +
                "    list.add(3)\n" +
                "    @Clamped\n" +
                "    var health: i32 = 50\n" +
                "    for (x in list) {\n" +
                "        health = x\n" +
                "    }\n" +
                "    core.io.Console.println(\"done ${health}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ③ while + getAtIndex 同步正确性（imap_a4 母本）：done 3
            Case("while + getAtIndex 同步",
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    list.add(2)\n" +
                "    list.add(3)\n" +
                "    var health: i32 = 50\n" +
                "    var i: i64 = 0L\n" +
                "    var ic: i64 = list.length\n" +
                "    while ((i < ic)) {\n" +
                "        var x = list.getAtIndex(i)\n" +
                "        health = (x if? 0)\n" +
                "        i = (i + 1L)\n" +
                "    }\n" +
                "    core.io.Console.println(\"done ${health}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ④ async 跨挂起 getAtIndex（imap_repro_b 母本；main 只
            // spawn + return 常量）：ok 7 -5 42 r=1
            Case("async 跨挂起 getAtIndex",
                "import core.collections.*\n" +
                "async func one(): i32 { return 1 }\n" +
                "async func run(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(7)\n" +
                "    list.add(-5)\n" +
                "    list.add(42)\n" +
                "    var t = one()\n" +
                "    var r = await t\n" +
                "    var a = list.getAtIndex(0L)\n" +
                "    var b = list.getAtIndex(1L)\n" +
                "    var c = list.getAtIndex(2L)\n" +
                "    if (((a if? -1) != 7)) { return 11 }\n" +
                "    if (((b if? -1) != -5)) { return 12 }\n" +
                "    if (((c if? -1) != 42)) { return 13 }\n" +
                "    core.io.Console.println(\"ok ${(a if? -1)} ${(b if? -1)} ${(c if? -1)} r=${r}\")\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑤ 元素类型矩阵：i64/bool/String/自定义 class
            // add → getAtIndex 往返
            Case("List 元素类型矩阵往返",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub class Item {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var li = new List\\<i64>()\n" +
                "    li.add(9000000000L)\n" +
                "    var lb = new List\\<bool>()\n" +
                "    lb.add(true)\n" +
                "    var ls = new List\\<String>()\n" +
                "    ls.add(\"hi\")\n" +
                "    var lc = new List\\<Item>()\n" +
                "    lc.add(new Item(5))\n" +
                "    var n = (li.getAtIndex(0L) if? 0L)\n" +
                "    var b = (lb.getAtIndex(0L) if? false)\n" +
                "    var s = (ls.getAtIndex(0L) if? \"\")\n" +
                "    var it = lc.getAtIndex(0L)\n" +
                "    var iv = (it?.v if? -1)\n" +
                "    Console.println(\"${n} ${b} ${s} ${iv}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ⑥ getAtIndex 越界得 null（if? 兜底分支）
            Case("getAtIndex 越界 null 兜底",
                "import core.collections.*\n" +
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var list = new List\\<i32>()\n" +
                "    list.add(1)\n" +
                "    var x = (list.getAtIndex(5L) if? -1)\n" +
                "    var y = (list.getAtIndex(0L) if? -1)\n" +
                "    Console.println(\"${x} ${y}\")\n" +
                "    return 0\n" +
                "}\n"),
            // ===== L6：非 rigi_rt native 库 FFI 端到端（RUNTIME §26 库解析
            // 留白定稿：C 符号 = @NativeSymbol 原文，链接输入走 native
            // --link）。VM 侧无此 hook（§22.5 表外拒绝是定稿行为），本组
            // 为 native-only 证明：现场 clang 编最小 C 源出目标文件，链接后
            // 断言 stdout/退出码字面量 =====
            NativeOnlyCase("FFI 用户库标量与 String 入参",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigiffi\")\n" +
                "@NativeSymbol(\"rigiffi_add\")\n" +
                "native func ffiAdd(a: i32, b: i32): i32\n" +
                "@NativeLibrary(\"rigiffi\")\n" +
                "@NativeSymbol(\"rigiffi_mul\")\n" +
                "native func ffiMul(a: i64, b: i64): i64\n" +
                "@NativeLibrary(\"rigiffi\")\n" +
                "@NativeSymbol(\"rigiffi_len\")\n" +
                "native func ffiLen(text: String): i64\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(ffiAdd(20, 22).toString())\n" +
                "    Console.println(ffiMul(6L, 7L).toString())\n" +
                "    Console.println(ffiLen(\"hello\").toString())\n" +
                "    return ffiAdd(1, 2)\n" +
                "}\n",
                "typedef struct { const char *data; long long len; } rigi_string;\n" +
                "int rigiffi_add(int a, int b) { return a + b; }\n" +
                "long long rigiffi_mul(long long a, long long b) { return a * b; }\n" +
                "long long rigiffi_len(const rigi_string *s) { return s->len; }\n",
                "42\n42\n5\n", 3),
            // bool 参数 C 边界 = i8 槽（NativeCallEmitter zext i1）
            NativeOnlyCase("FFI 用户库 bool 与 f64",
                "import core.io.Console\n" +
                "@NativeLibrary(\"rigiffi2\")\n" +
                "@NativeSymbol(\"rigiffi_pick\")\n" +
                "native func ffiPick(flag: bool, x: double): double\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(ffiPick(true, 1.5).toString())\n" +
                "    Console.println(ffiPick(false, 1.5).toString())\n" +
                "    return 0\n" +
                "}\n",
                "double rigiffi_pick(signed char flag, double x) { return flag ? x : -x; }\n",
                "1.5\n-1.5\n", 0),
            Case("Place 对象身份与 Cell 复用",
                "class Item { pub var n: i32 = 1 }\n" +
                "pub func main(): i32 {\n" +
                " const x = new Item()\n var n: i32 = 1\n const fixed: i32 = 4\n" +
                " seq using(const a = placeOf x) using(const b = placeOf x) {\n" +
                "  core.io.Console.println((a == b).toString())\n }\n" +
                " seq using(const a = placeOf n) using(const b = placeOf n) {\n" +
                "  n = 9\n core.io.Console.println((a == b).toString())\n" +
                "  core.io.Console.println(n.toString())\n a.dispose()\n" +
                "  core.io.Console.println((a == b).toString())\n }\n" +
                " seq using(const a = placeOf fixed) using(const b = placeOf fixed) {\n" +
                "  core.io.Console.println((a == b).toString())\n }\n return 0\n}"),
            Case("Handle 生命周期与值写回",
                "pub func main(): i32 {\n var n: i32 = 3\n" +
                " unsafe seq using(const p = placeOf n) {\n const h = p.expose()\n" +
                " p.dispose()\n core.io.Console.println(h.load().toString())\n" +
                " const m = h.asMutable()\n m.store(9)\n" +
                " core.io.Console.println(n.toString())\n" +
                " core.io.Console.println(h.load().toString())\n }\n return 0\n}"),
            Case("Handle 对象只读与泛型能力边界", HandleBoundarySource),
            // 3b-δ1：load 借用化（handle_target 返回裸引用）行为对拍
            Case("Handle load 借用槽传播链", HandleBorrowChainSource),
            Case("Handle 跨协程借用 load", HandleBorrowCoroutineSource),
            Case("Cell 用户覆写开放封闭接口与重载 ABI", CellSlotSource),
            Case("Handle rich value 与异常所有权", HandleRichSource),
            EnvCase("Handle 隐藏边循环回收", HandleCycleSource,
                new Dictionary<string, string> { ["RIGI_RT_GC_THRESHOLD"] = "128" }),
            Case("Atomic 值对象异常与安全门面", AtomicSource),
            Case("Atomic 基础初始化与读取", "pub func main(): i32 { unsafe seq { const a = new Atomic\\<i32>(3)\n return a.load() } }") ,
            Case("Atomic 值更新", "pub func main(): i32 { unsafe seq { const a = new Atomic\\<i32>(3)\n a.mutate(func{ (old:i32):i32 -> old + 4 })\n return a.load() } }") ,
            Case("AtomicStruct 安全更新", "pub func main(): i32 { const a = new AtomicStruct\\<i32>(3)\n a.store(7)\n return a.load() }") ,
            // review-20260910 Phase 2.5：安全门面一步式 RMW（转发私有 Atomic.mutate）
            Case("AtomicStruct 并发mutate自增无丢失", AtomicStructMutateConcurrentSource, maxSteps: 50_000_000),
            // atomicfix 回归（atomic-455-gcoff）：同用例在纯 ARC 对照（GC 关）
            // 下跑。历史缺陷（rigi_gc_local_after_release !alive 分支把
            // 「plain 减后值==1」误判终态 + CoroutineHandle plain 会计被
            // Task/Dispatcher 链跨协程并发触碰）在该环境 100% 复现
            // 0xC0000005，是修复最灵敏的回归探针；RIGI_RT_MEMTRACK 由
            // EnvCase 基线注入，顺带锁定退出零泄漏口径。
            EnvCase("AtomicStruct 并发mutate自增无丢失（GC_OFF 纯ARC对照）",
                AtomicStructMutateConcurrentSource,
                new Dictionary<string, string> { ["RIGI_RT_GC_OFF"] = "1" },
                maxSteps: 50_000_000),
            Case("AtomicStruct mutate尾随lambda形态", AtomicStructMutateTrailingLambdaSource),
            Case("AtomicStruct mutate回调抛错保留旧值", AtomicStructMutateThrowKeepsOldValueSource),
            Case("Atomic nullable 值与对象往返", AtomicNullableSource),

        };

    }
}
