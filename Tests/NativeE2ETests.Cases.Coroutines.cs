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
        // 原编号 372..399 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateCoroutinesCases() => new (string Label, Action Run)[]
        {
            // R2-d㉝ 同 fn try 内构造抛出双端对齐：MirNewObject
            // 补 ExcTarget 异常边（历史「pending 推迟到下一检查点」
            // 形态消除）——① 同步 init 抛出立即沿 try 边捕获
            //（caught-sync）；② 挂起 init 恢复后 FAILED 沿构造异
            // 常边进本 fn 捕获（caught-fin），finally 次序对齐 VM
            Case("栈式跨界 try 内构造抛出双端对齐",
                "import core.io.Console\n" +
                "pub class SyncBoom {\n" +
                "    pub init(v: i32) {\n" +
                "        throw new core.RuntimeException(\"sync-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "pub class Boom {\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        throw new core.RuntimeException(\"boom-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "func makeSync(): i32 {\n" +
                "    try {\n" +
                "        var o = new SyncBoom(3)\n" +
                "        return 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught-sync\")\n" +
                "        return 2\n" +
                "    }\n" +
                "}\n" +
                "func makeFinally(): i32 {\n" +
                "    try {\n" +
                "        var o = new Boom(9)\n" +
                "        return 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught-fin\")\n" +
                "        return 2\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"finally\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(makeSync().toString())\n" +
                "    Console.println(makeFinally().toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊱ 泛型接口方法 iMap 派发：默认体的 fn 定义携带 §7.2
            // 方法级 typeid 隐藏参数，thunk fn 类型必须以 fn 体为准
            //（合成签名会漏 typeid 参数，调用约定错配）。① 无挂起点
            //（默认体路径，r=42）
            Case("泛型接口方法 iMap 派发（默认体）",
                "import core.io.Console\n" +
                "pub interface IMapper {\n" +
                "    func map\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IMapper {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "func runIt(m: IMapper): i32 {\n" +
                "    var r = m.map\\<i32>(42)\n" +
                "    return (r as i32)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m: IMapper = new IntBox()\n" +
                "    Console.println(\"r=\" + runIt(m).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ② 无挂起点（类 override 覆盖默认体，r=43）
            Case("泛型接口方法 iMap 派发（override）",
                "import core.io.Console\n" +
                "pub interface IMapper {\n" +
                "    func map\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IMapper {\n" +
                "    pub init() {}\n" +
                "    pub override func map\\<T>(x: T): T { return (((x as i32) + 1) as T) }\n" +
                "}\n" +
                "func runIt(m: IMapper): i32 {\n" +
                "    var r = m.map\\<i32>(42)\n" +
                "    return (r as i32)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m: IMapper = new IntBox()\n" +
                "    Console.println(\"r=\" + runIt(m).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ③ 含挂起点：async 调用方内 iMap 派发泛型接口方法
            //（统一切分路径同 emitter，调用约定一致）
            Case("泛型接口方法 iMap 派发（含挂起点）",
                "import core.io.Console\n" +
                "pub interface IMapper {\n" +
                "    func map\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "pub class IntBox implements IMapper {\n" +
                "    pub init() {}\n" +
                "}\n" +
                "async func one(): i32 { return 1 }\n" +
                "async func runIt(): i32 {\n" +
                "    var m: IMapper = new IntBox()\n" +
                "    var t = one()\n" +
                "    var k = await t\n" +
                "    var r = m.map\\<i32>((41 + k))\n" +
                "    Console.println(\"r=\" + (r as i32).toString())\n" +
                "    return 0\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = runIt()\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊲ MirNewValue 补 ExcTarget（同 R2-d MirNewObject
            // 口径）：值类型 init 同步抛出沿本 fn try 异常边捕获
            //（修复前 pending 推迟致异常逃逸出 fn，native exit=1
            // 而 VM 于 fn 内捕获）
            Case("值类型 init 同步抛出同 fn try 捕获",
                "import core.io.Console\n" +
                "pub struct VBoom {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) {\n" +
                "        throw new core.RuntimeException(\"vboom\")\n" +
                "    }\n" +
                "}\n" +
                "func make(): i32 {\n" +
                "    try {\n" +
                "        var o = new VBoom(3)\n" +
                "        return 1\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "        return 2\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(make().toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊳ open struct 继承链：占位接收者子类型盒拆箱（R1
            // 边界清偿）——ValueTypeLayout 补 struct 继承布局（基类
            // 字段前缀/尺寸/refMap/basePlan 链），拆箱守卫改 rigi_type_is
            // 协变链，写回原地补丁保身份（42/still-child 双端一致）
            Case("占位接收者：open struct 子类型盒变异写回",
                "import core.io.Console\n" +
                "pub open rich struct Base {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub func bump() { v = (v + 1) }\n" +
                "}\n" +
                "pub rich struct Child : Base {\n" +
                "    pub init(x: i32) { super(x) }\n" +
                "}\n" +
                "pub class Box2\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func useIt\\<T extends Base>(b: Box2\\<T>) { b.item.bump() }\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<Child>(new Child(41))\n" +
                "    useIt\\<Child>(b)\n" +
                "    Console.println(b.item.v.toString())\n" +
                "    if (b.item is Child) {\n" +
                "        Console.println(\"still-child\")\n" +
                "    } else {\n" +
                "        Console.println(\"sliced\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊴ 子类型盒 + 变异方法抛异常：异常路径不写回（VM copy
            // 语义——部分变异不可见）且身份保留（caught/41/still-child）
            Case("占位接收者：子类型盒异常路径不写回",
                "import core.io.Console\n" +
                "pub open rich struct Base {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub func boom() {\n" +
                "        v = 99\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "    }\n" +
                "}\n" +
                "pub rich struct Child : Base {\n" +
                "    pub init(x: i32) { super(x) }\n" +
                "}\n" +
                "pub class Box2\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "func useIt\\<T extends Base>(b: Box2\\<T>) {\n" +
                "    try {\n" +
                "        b.item.boom()\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<Child>(new Child(41))\n" +
                "    useIt\\<Child>(b)\n" +
                "    Console.println(b.item.v.toString())\n" +
                "    if (b.item is Child) {\n" +
                "        Console.println(\"still-child\")\n" +
                "    } else {\n" +
                "        Console.println(\"sliced\")\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R3-㊵ 值类型沿 open struct 继承链向上转换：cast 不再无条件
            // 抛 CastException（此前 EmitFail 判死），前缀切片数据对齐
            // VM（42）。已知残留差：经具体类型航点（Base 槽）后运行期
            // 身份不保留（native 内联值 ABI 槽按静态类型定尺寸；VM 值
            // 自我描述恒保身份）——故此用例只断言数据路径
            Case("open struct 值类型向上转换数据路径",
                "import core.io.Console\n" +
                "pub open rich struct Base {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub func bump() { v = (v + 1) }\n" +
                "}\n" +
                "pub rich struct Child : Base {\n" +
                "    pub init(x: i32) { super(x) }\n" +
                "}\n" +
                "pub class Holder\\<T> {\n" +
                "    pub var x: T\n" +
                "    pub init(v: T) { x = v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var h = new Holder\\<Base>(new Child(41))\n" +
                "    h.x.bump()\n" +
                "    Console.println(h.x.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // ⑧ async 无挂起点 fn（统一切分后直跑到底）
            Case("协程无挂起点 async fn",
                "import core.io.Console\n" +
                "async func straight(): i32 { return 7 }\n" +
                "async func run() {\n" +
                "    var t = straight()\n" +
                "    var n = await t\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑨ 泛型类 async 方法（类级 typeid 捕获进 frame）+ 泛型
            // async fn（方法级 typeid 跨挂起）
            Case("协程泛型类 async 方法 + 泛型 async fn",
                "import core.io.Console\n" +
                "shared class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) { v = x }\n" +
                "    pub async func get(): T {\n" +
                "        yield\n" +
                "        return v\n" +
                "    }\n" +
                "}\n" +
                "async func one(): i32 { return 1 }\n" +
                "async func echo\\<T>(x: T): T {\n" +
                "    var t = one()\n" +
                "    await t\n" +
                "    return x\n" +
                "}\n" +
                "async func run() {\n" +
                "    var b = new Box\\<String>(\"boxed\")\n" +
                "    var g = b.get()\n" +
                "    var s = await g\n" +
                "    var e = echo\\<String>(s + \"!\")\n" +
                "    var r = await e\n" +
                "    Console.println(r)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑩ 协程树（VM TestCoroutineStressForkJoin tree(7)=128
            // 母本）：断言最终求和值
            Case("协程树 tree(7)=128",
                "import core.io.Console\n" +
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
                "async func run() {\n" +
                "    var root = tree(7)\n" +
                "    var n = await root\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11b 棒3：yield Alarm 对拍（VM 基准已逐例实测；
            // 计时断言全部避免墙钟——sleep 毫秒只作唤醒源，断言落
            // stdout 次序/最终值；RIGI_RT_MEMTRACK=1 零泄漏口径）=====
            // ⑪ yield sleep 基础 + fork/join 求和 42（VM
            // BilVmWakeupTests sleep(1) 端到端母本）
            Case("协程 yield sleep fork/join 42",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func nap(n: i32): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return n + 1\n" +
                "}\n" +
                "async func run() {\n" +
                "    var a = nap(19)\n" +
                "    var b = nap(20)\n" +
                "    var c = nap(0)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    var z = await c\n" +
                "    Console.println(((x + y) + z).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑫ sleep(0) 立即触发仍结束执行段（对齐 VM Arm(<=0) →
            // Publish 语义）：立即触发 = 不死锁不挂起即恢复完成；跨
            // 协程交错次序在 VM ThreadPool 下不确定，打印只放同协程
            // 程序序（a1→a2）与 await 数据依赖（done 在终态后）确定处
            Case("协程 sleep(0) 立即触发仍完成",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func a(): i32 {\n" +
                "    Console.println(\"a1\")\n" +
                "    yield sleep(0)\n" +
                "    Console.println(\"a2\")\n" +
                "    return 3\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = a()\n" +
                "    var n = await t\n" +
                "    Console.println(\"done \" + n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑬ 双协程不同毫秒 sleep 的完成次序（10ms vs 300ms 给足
            // 余量；断言次序串，无墙钟断言）
            Case("协程双毫秒 sleep 完成次序",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func slow() {\n" +
                "    yield sleep(300)\n" +
                "    Console.println(\"slow\")\n" +
                "}\n" +
                "async func fast() {\n" +
                "    yield sleep(10)\n" +
                "    Console.println(\"fast\")\n" +
                "}\n" +
                "async func run() {\n" +
                "    slow()\n" +
                "    fast()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑭ PollingAlarm 翻牌（VM BilVmTests Flip 母本）两形态：
            // (a) 先翻牌后 yield——双端首探即中，isReady 探测计数恰 1
            //     锁死（次数语义）；(b) arm 协程裸 yield 后翻牌——覆盖
            //     未就绪→退避→就绪路径；探测节奏（每执行段末一轮 vs VM
            //     恢复点探测）是 §19.2 实现选择（VM 文档明言退避非语言
            //     语义），跨端次数不必一致，本形态只断言恢复事实
            Case("协程 PollingAlarm 首探即中",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub var probes: i32 = 0\n" +
                "    pub override func isReady(): bool {\n" +
                "        probes = probes + 1\n" +
                "        return ready\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var f = new Flip()\n" +
                "    f.ready = true\n" +
                "    yield f\n" +
                "    Console.println(\"polled \" + f.probes.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 PollingAlarm 退避翻牌",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Flip : PollingAlarm {\n" +
                "    pub var ready: bool = false\n" +
                "    pub override func isReady(): bool { return ready }\n" +
                "}\n" +
                "async func arm(f: Flip) {\n" +
                "    yield\n" +
                "    f.ready = true\n" +
                "}\n" +
                "async func run() {\n" +
                "    var f = new Flip()\n" +
                "    arm(f)\n" +
                "    yield f\n" +
                "    Console.println(\"polled\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== L8：用户 EventAlarm 直继子类默认底座（§19.3）=====
            // 无时钟底座的用户子类经 ensureHandle 懒建手动事件粘滞
            // 底座，protected signal() 为事件源触发入口；VM/native
            // 同源码对拍（断言落最终结果，不锁跨协程交错次序）
            // ⑭c signal 唤醒双 waiter：两 waiter 挂同一 Gate，opener
            //     sleep 后 open() 触发——waiter 登记与 signal 的先后
            //     序由粘滞兜底（先触发后登记也不丢）；和 13 次序无关
            Case("协程 用户EventAlarm signal 唤醒双 waiter",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Gate : EventAlarm {\n" +
                "    pub func fire() { signal() }\n" +
                "}\n" +
                "async func waiter(g: Gate, n: i32): i32 {\n" +
                "    yield\n" +
                "    yield g\n" +
                "    return n + 1\n" +
                "}\n" +
                "async func opener(g: Gate) {\n" +
                "    yield sleep(30)\n" +
                "    g.fire()\n" +
                "}\n" +
                "async func run() {\n" +
                "    var g = new Gate()\n" +
                "    var a = waiter(g, 1)\n" +
                "    var b = waiter(g, 10)\n" +
                "    opener(g)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    Console.println((x + y).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑭d 先 signal 后 yield 不丢（粘滞）：signal 时无 waiter
            //     → 恒置已触发；迟到 yield 立即具备重新发布条件（仍
            //     结束当前执行段）
            Case("协程 用户EventAlarm 先signal后yield不丢",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Gate : EventAlarm {\n" +
                "    pub func fire() { signal() }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var g = new Gate()\n" +
                "    g.fire()\n" +
                "    yield g\n" +
                "    Console.println(\"not lost\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑭e 重复 signal 幂等 + 二次 yield 仍粘滞（§19.3：实例
            //     保持已触发状态，重复触发幂等）
            Case("协程 用户EventAlarm 重复signal幂等粘滞",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Gate : EventAlarm {\n" +
                "    pub func fire() { signal() }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var g = new Gate()\n" +
                "    g.fire()\n" +
                "    g.fire()\n" +
                "    yield g\n" +
                "    g.fire()\n" +
                "    yield g\n" +
                "    Console.println(\"idempotent sticky\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑮ using 清理穿越 alarm yield（ASYNC §8 验收项）：正常
            // 路径 return 与异常路径 throw 的 dispose 均执行且次序正确
            //（dispose 先于 Task 终态 → 先于 await 续行打印）
            Case("协程 using 清理穿越 alarm yield 双路径",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() {\n" +
                "        Console.println(\"dispose \" + tag.toString())\n" +
                "    }\n" +
                "}\n" +
                "async func work(): i32 {\n" +
                "    seq using(const r = new Res(1)) {\n" +
                "        yield sleep(1)\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n" +
                "async func boom(): i32 {\n" +
                "    seq using(const r = new Res(2)) {\n" +
                "        yield sleep(1)\n" +
                "        throw new core.RuntimeException(\"x\")\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = work()\n" +
                "    var n = await t\n" +
                "    Console.println(n.toString())\n" +
                "    var b = boom()\n" +
                "    try {\n" +
                "        await b\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ⑯ isReady 抛异常 = yield 点失败（§19.2 第 6 条）：probe
            // ret -1（pending 保持置位）→ drain 取走 → Task FAILED →
            // await 点重抛捕获
            Case("协程 isReady 抛异常 await 重抛捕获",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Boom : PollingAlarm {\n" +
                "    pub init() { }\n" +
                "    pub override func isReady(): bool {\n" +
                "        throw new core.RuntimeException(\"probe\")\n" +
                "    }\n" +
                "}\n" +
                "async func waitIt() {\n" +
                "    var b = new Boom()\n" +
                "    yield b\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = waitIt()\n" +
                "    try {\n" +
                "        await t\n" +
                "        Console.println(\"miss\")\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11c 棒5a 阶段4：跨 Executor 对拍（VM
            // BilVmTaskTests.TestCrossExecutorCombination 母本）。包进
            // async run()（B-1 起 main 可直接 await，此处保留回归
            // 写法）。先 Main 冷 Task（无懒起 Worker），再 Compute/IO
            // 懒起；join 回 Main；memtrack 零泄漏 =====
            Case("协程冷 Task Main join",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 41\n" +
                "    } })\n" +
                "    t.run()\n" +
                "    const n = await t\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 ComputeExecutor 冷 Task join",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const t = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 41\n" +
                "    } })\n" +
                "    t.run(new ComputeExecutor())\n" +
                "    const n = await t\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程跨 Executor Compute+IO join",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const c = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        return@_ 40\n" +
                "    } })\n" +
                "    c.run(new ComputeExecutor())\n" +
                "    const io = new Task\\<i32>(func{async (): i32 -> {\n" +
                "        yield sleep(20)\n" +
                "        return@_ 2\n" +
                "    } })\n" +
                "    io.run(new IOExecutor())\n" +
                "    const a = await c\n" +
                "    const b = await io\n" +
                "                    Console.println((a + b).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 语言级 Mutex（§19.6）：VM 方法 hook / native CoroutineSplit
            // 改写 enter + Rigi release 真体。互斥：临界区内 yield 仍持
            // 锁，竞争者不得进入——否则日志交织成 "[a[b..." 形态
            Case("协程 Mutex 临界区不交织",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Log { pub var order: String = \"\" }\n" +
                "async func critical(m: Mutex, log: Log, tag: String) {\n" +
                "    const l = await m.acquire()\n" +
                "    log.order = (log.order + (\"[\" + tag))\n" +
                "    yield sleep(20)\n" +
                "    log.order = (log.order + (tag + \"]\"))\n" +
                "    m.release(l)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const m = new Mutex()\n" +
                "    const log = new Log()\n" +
                "    const a = critical(m, log, \"a\")\n" +
                "    const b = critical(m, log, \"b\")\n" +
                "    await a\n" +
                "    await b\n" +
                "    Console.println(log.order)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 Mutex 他锁令牌释放抛异常",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const m = new Mutex()\n" +
                "    const n = new Mutex()\n" +
                "    const l = await m.acquire()\n" +
                "    try {\n" +
                "        n.release(l)\n" +
                "        Console.println(\"no\")\n" +
                "    } catch (_: core.IllegalStateException) {\n" +
                "        Console.println(\"caught\")\n" +
                "    }\n" +
                "    m.release(l)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // invoke.indirect async $$call：AsyncFunc 经变量调用产 Task，
            // 再 await。Mutex.runSynchronously 同通道（vtable 闭包也会
            // 把这条路径收进任何 `new Mutex()` 的 native 模块）
            Case("协程 await async lambda",
                "import core.io.Console\n" +
                "async func run() {\n" +
                "    const f = func{async (): i32 -> 41}\n" +
                "    const n = await f()\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 Mutex.runSynchronously 取值",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const m = new Mutex()\n" +
                "    const n = await m.runSynchronously\\<i32>(func{async (): i32 -> 7})\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),

        };

    }
}
