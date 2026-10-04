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
        // 原编号 345..371 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateStackDispatchCases() => new (string Label, Action Run)[]
        {
            // B-2⑥ 异常跨多层链 + try/finally：tainted 三层（top→
            // mid(try/finally)→deep(await 后抛)）——FAILED 沿链上传，
            // 每层传播垫配平释放，finally 在逐层展开时执行
            Case("栈式跨界 异常多层传播 try/finally",
                "import core.io.Console\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n * 2\n" +
                "}\n" +
                "func deep(n: i32): i32 {\n" +
                "    var t = slow(n)\n" +
                "    var r = await t\n" +
                "    if (r > 10) {\n" +
                "        throw new core.RuntimeException(\"deep-\" + r.toString())\n" +
                "    }\n" +
                "    return r\n" +
                "}\n" +
                "func mid(n: i32): i32 {\n" +
                "    try {\n" +
                "        return deep(n)\n" +
                "    } finally(_) {\n" +
                "        Console.println(\"fin-mid\")\n" +
                "    }\n" +
                "}\n" +
                "func top(n: i32): i32 {\n" +
                "    var v = mid(n)\n" +
                "    return v + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var r = top(8)\n" +
                "        Console.println(r.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑦ using 穿越 tainted fn 挂起点：seq using 的 dispose
            // 在恢复后续行时正常执行（RcInjection 托管槽跨挂起配平）
            Case("栈式跨界 using 穿越挂起点",
                "import core.io.Console\n" +
                "pub shared class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() {\n" +
                "        Console.println(\"dispose \" + tag.toString())\n" +
                "    }\n" +
                "}\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "func useIt(n: i32): i32 {\n" +
                "    seq using(const r = new Res(n)) {\n" +
                "        var t = slow(n)\n" +
                "        var v = await t\n" +
                "        return v * 10\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = useIt(3)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑧ void main 含挂起点：settle 恒 0（无 result 解包），
            // stdout 次序对齐 VM
            Case("栈式跨界 void main 裸 yield",
                "import core.io.Console\n" +
                "pub func main() {\n" +
                "    yield\n" +
                "    Console.println(\"void-main\")\n" +
                "}\n"),
            // B-2⑨ yield Alarm（EventAlarm/sleep）在 tainted 普通 fn：
            // B-2 起放开（plain 恢复闸无 TaskState 投影），sleep 唤醒
            // 后续行 42
            Case("栈式跨界 普通 fn yield sleep",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func nap(n: i32): i32 {\n" +
                "    yield sleep(1)\n" +
                "    return n + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = nap(41)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑩ yield PollingAlarm 在 tainted 普通 fn：arm 协程
            // 裸 yield 后翻牌——覆盖未就绪→退避（poll_schedule 再挂）
            // →就绪路径（plain 恢复闸无投影跳过）；探测节奏跨端不必
            // 一致（§19.2 退避非语言语义），只断言恢复事实
            Case("栈式跨界 普通 fn yield PollingAlarm 退避翻牌",
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
                "func waitFlip(f: Flip) {\n" +
                "    yield f\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = new Flip()\n" +
                "    arm(f)\n" +
                "    waitFlip(f)\n" +
                "    Console.println(\"recovered\")\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑪ isReady 抛异常经 plain 链上传（B-2 plain 失败尾：
            // probe ret -1 pending 保持置位 → 恢复闸 ret FAILED →
            // 调用点 FAILED 臂取走重抛 → main try/catch 捕获；对齐
            // VM 帧栈逐层展开口径）
            Case("栈式跨界 普通 fn isReady 异常沿链捕获",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared class Boom : PollingAlarm {\n" +
                "    pub init() { }\n" +
                "    pub override func isReady(): bool {\n" +
                "        throw new core.RuntimeException(\"probe\")\n" +
                "    }\n" +
                "}\n" +
                "func waitBoom(b: Boom) {\n" +
                "    yield b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Boom()\n" +
                "    try {\n" +
                "        waitBoom(b)\n" +
                "        Console.println(\"miss\")\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑫ Mutex 竞争挂起跨 tainted 链：普通 fn await
            // m.acquire()（enter 在 async acquire 体内——priv enter
            // 用户不可直调，语言级 plain fn 无法直含 Mutex.enter）；
            // 双 worker 竞争同锁，结果配平 14（memtrack 零泄漏）
            Case("栈式跨界 Mutex 竞争跨链配平",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func critical(m: Mutex, n: i32): i32 {\n" +
                "    var g = await m.acquire()\n" +
                "    yield\n" +
                "    var r = n * 2\n" +
                "    m.release(g)\n" +
                "    return r\n" +
                "}\n" +
                "async func worker(m: Mutex, n: i32): i32 {\n" +
                "    return critical(m, n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var m = new Mutex()\n" +
                "    var a = worker(m, 3)\n" +
                "    var b = worker(m, 4)\n" +
                "    var x = await a\n" +
                "    var y = await b\n" +
                "    Console.println((x + y).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑬ 虚调用链挂起点·混合闭包：Base.step 含 yield
            //（tainted），Derived.step 普通——调用点动态分流：Base
            // 实例走协议（7），Derived 实例落原虚调用（51）
            Case("栈式跨界 虚调用混合闭包动态分流",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Base(), 5)\n" +
                "    var b = drive(new Derived(), 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑭ 虚调用链挂起点·全 tainted 闭包：两个实现臂都走
            // 协议（最深派生优先 type.is 分流，各实现 frame 独立）
            Case("栈式跨界 虚调用全 tainted 闭包",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Base(), 5)\n" +
                "    var b = drive(new Derived(), 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑮ interface 调用链挂起点：iMap 闭包分流（Slow
            // tainted 走协议 7，Fast 普通落原调用 51）
            Case("栈式跨界 interface 调用动态分流",
                "import core.io.Console\n" +
                "pub interface IStepper {\n" +
                "    func step(n: i32): i32\n" +
                "}\n" +
                "pub class Slow implements IStepper {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Fast implements IStepper {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(s: IStepper, n: i32): i32 {\n" +
                "    return s.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Slow(), 5)\n" +
                "    var b = drive(new Fast(), 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑯ super 调用含挂起点的基类实现：super 恒直调（静态
            // 唯一目标），同直调协议；override 体内两次 super 调用
            Case("栈式跨界 super 调用基类挂起实现",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        var a = super(n)\n" +
                "        var b = super(n)\n" +
                "        return a + b\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Derived()\n" +
                "    var r = d.step(10)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑰ 异常经虚派发链上传：Base.step（tainted 臂）恢复
            // 后抛出 → FAILED 沿链 → main try/catch 捕获；Derived
            // 普通臂不受影响
            Case("栈式跨界 虚调用异常沿链捕获",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        if (n > 10) {\n" +
                "            throw new core.RuntimeException(\"base-\" + n.toString())\n" +
                "        }\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var r = drive(new Base(), 20)\n" +
                "        Console.println(r.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    var ok = drive(new Derived(), 5)\n" +
                "    Console.println(ok.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑱ async 调用方经虚派发链挂起：run 直调 tainted
            // drive（虚点），整条栈同步挂起，裸返回求和 58
            Case("栈式跨界 async 经虚链同步挂起",
                "import core.io.Console\n" +
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Base, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "async func run(): i32 {\n" +
                "    var x = drive(new Base(), 5)\n" +
                "    var y = drive(new Derived(), 5)\n" +
                "    return x + y\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = run()\n" +
                "    var r = await t\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑲ 含挂起点的 init：构造点分配与 init 下钻分离
            //（合成空 init 分配 + init.wrapper 原位缝合 + init frame
            // .this = 新建对象），恢复后字段写入可见 42
            Case("栈式跨界 init 内挂起",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) {\n" +
                "        yield\n" +
                "        v = x * 2\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n = new Node(21)\n" +
                "    Console.println(n.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2⑳ 字段初始值缝合 + init 挂起：init.wrapper 在挂起
            // init 之前原位跑完（v=5 先落，init 恢复后 v+x），跨
            // tainted 工厂 fn 两实例 15,25
            Case("栈式跨界 init 挂起字段初始值缝合",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var v: i32 = 5\n" +
                "    pub init(x: i32) {\n" +
                "        yield\n" +
                "        v = v + x\n" +
                "    }\n" +
                "}\n" +
                "func make(x: i32): Node {\n" +
                "    var n = new Node(x)\n" +
                "    return n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = make(10)\n" +
                "    var b = make(20)\n" +
                "    Console.println((a.v.toString() + \",\") + b.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉑ init 恢复后抛异常经构造链 FAILED 上传：tainted
            // 工厂 fn 内的构造点未捕获 → 沿调用点 FAILED 臂 → main
            // try/catch 捕获（caught:big-20 + 3）
            Case("栈式跨界 init 异常沿链捕获",
                "import core.io.Console\n" +
                "pub class Node {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) {\n" +
                "        yield\n" +
                "        if (x > 10) {\n" +
                "            throw new core.RuntimeException(\"big-\" + x.toString())\n" +
                "        }\n" +
                "        v = x\n" +
                "    }\n" +
                "}\n" +
                "func make(x: i32): Node {\n" +
                "    var n = new Node(x)\n" +
                "    return n\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a = make(20)\n" +
                "        Console.println(a.v.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    var ok = make(3)\n" +
                "    Console.println(ok.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉒ class 运算符 intrinsic 派发含挂起点：单类闭包
            // 臂走协议（operator plus 内 yield），求值 42
            Case("栈式跨界 运算符派发挂起",
                "import core.io.Console\n" +
                "pub class Acc {\n" +
                "    pub var v: i32\n" +
                "    pub init(x: i32) { v = x }\n" +
                "    pub operator plus(other: Acc): Acc {\n" +
                "        yield\n" +
                "        return new Acc(v + other.v)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Acc(10)\n" +
                "    var b = new Acc(32)\n" +
                "    var c = a + b\n" +
                "    Console.println(c.v.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉓ 泛型值类型方法的类级 typeid 落参合成（§7.2 隐
            // 藏参数）：宿主闭合构造 PairBox<i32> → MirGetTypeId 常
            // 量 typeid 落 callee frame，恢复后读字段 42
            Case("栈式跨界 泛型值类型方法隐藏参数",
                "import core.io.Console\n" +
                "pub struct PairBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) { v = x }\n" +
                "    pub func get(): T {\n" +
                "        yield\n" +
                "        return v\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new PairBox\\<i32>(40)\n" +
                "    var r = b.get()\n" +
                "    Console.println((r + 2).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // B-2㉔ using dispose 可挂起（BIL §17.3）：dispose 内
            // yield——正常 return 路径与异常展开路径的 dispose 均经
            // 虚派发臂协议化挂起/恢复，次序与 VM 对齐（dispose 先于
            // 续行/捕获打印）
            Case("栈式跨界 using dispose 可挂起双路径",
                "import core.io.Console\n" +
                "pub shared class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() {\n" +
                "        yield\n" +
                "        Console.println(\"dispose \" + tag.toString())\n" +
                "    }\n" +
                "}\n" +
                "async func slow(n: i32): i32 {\n" +
                "    yield\n" +
                "    return n + 1\n" +
                "}\n" +
                "func useIt(n: i32): i32 {\n" +
                "    seq using(const r = new Res(n)) {\n" +
                "        var t = slow(n)\n" +
                "        var v = await t\n" +
                "        return v * 10\n" +
                "    }\n" +
                "}\n" +
                "func boomIt(n: i32): i32 {\n" +
                "    seq using(const r = new Res(n + 100)) {\n" +
                "        var t = slow(n)\n" +
                "        var v = await t\n" +
                "        throw new core.RuntimeException(\"mid-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = useIt(3)\n" +
                "    Console.println(a.toString())\n" +
                "    try {\n" +
                "        var b = boomIt(4)\n" +
                "        Console.println(b.toString())\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R2-a㉕ 泛型宿主虚派发链挂起点：臂条件扩展为「模板空
            // 壳 + 模块内全部闭合构造 sheet」OR 链（泛型实例头是构
            // 造 sheet，其基链不含模板空壳）；类级 typeid 落参从接
            // 收者实例隐藏字段运行期读取（静态构造形态被接收者
            // cast 剥成裸模板）——Box<i32> 模板臂走协议 7、
            // DerivedBox<i32> 普通臂落原虚调用 51、IntBox（非泛型
            // 派生自 Box<i32>）经构造基链命中 106
            Case("栈式跨界 泛型宿主虚派发动态分流",
                "import core.io.Console\n" +
                "pub open class Box\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 1\n" +
                "    }\n" +
                "}\n" +
                "pub class DerivedBox\\<T> : Box\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        return n * 10\n" +
                "    }\n" +
                "}\n" +
                "pub class IntBox : Box\\<i32> {\n" +
                "    pub init() { }\n" +
                "    pub override func step(n: i32): i32 {\n" +
                "        yield\n" +
                "        return n + 100\n" +
                "    }\n" +
                "}\n" +
                "func drive(b: Box\\<i32>, n: i32): i32 {\n" +
                "    return b.step(n) + 1\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = drive(new Box\\<i32>(), 5)\n" +
                "    var b = drive(new DerivedBox\\<i32>(), 5)\n" +
                "    var c = drive(new IntBox(), 5)\n" +
                "    Console.println((((a.toString() + \",\") + b.toString()) + \",\") + c.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-a㉖ 泛型宿主挂起实现使用 T（类级 typeid 运行期读
            // 取实证）：id(v: T): T 的 .generic.T 落参取自接收者实
            // 例隐藏 typeid 字段——i32 与 String 两种构造各经模板
            // 臂协议恢复后正确拆箱/装箱
            Case("栈式跨界 泛型宿主挂起实现运行期 typeid",
                "import core.io.Console\n" +
                "pub open class Box\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func id(v: T): T {\n" +
                "        yield\n" +
                "        return v\n" +
                "    }\n" +
                "}\n" +
                "func driveId(b: Box\\<i32>, v: i32): i32 {\n" +
                "    return b.id(v) + 1\n" +
                "}\n" +
                "func driveStr(b: Box\\<String>, v: String): String {\n" +
                "    return b.id(v) + \"!\"\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(driveId(new Box\\<i32>(), 41).toString())\n" +
                "    Console.println(driveStr(new Box\\<String>(), \"ok\"))\n" +
                "    return 0\n" +
                "}\n"),
            // R2-b㉗ invoke.indirect 挂起点（callable 协议 $$call
            // 闭包动态分流，同虚派发臂机制）：含 yield 的 lambda 经
            // fn 值变量调用走 tainted 臂协议 42
            Case("栈式跨界 lambda 间接调用挂起",
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + 1\n" +
                "    }}\n" +
                "    var r = f(41)\n" +
                "    Console.println(r.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-b㉘ invoke.indirect 混合闭包：Func<i32,i32> 形参
            // 站点——tainted lambda（slow）走协议臂 7，普通 lambda
            //（fast）落默认臂原 invoke.indirect 51；async lambda
            // 回归（Task 通道不受影响）41
            Case("栈式跨界 间接调用混合闭包动态分流",
                "import core.io.Console\n" +
                "func apply(f: Func\\<i32, i32>, x: i32): i32 {\n" +
                "    return f(x) + 1\n" +
                "}\n" +
                "async func run() {\n" +
                "    var slow = func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + 1\n" +
                "    }}\n" +
                "    var fast = func{(x: i32): i32 -> (x * 10)}\n" +
                "    var a = apply(slow, 5)\n" +
                "    var b = apply(fast, 5)\n" +
                "    Console.println((a.toString() + \",\") + b.toString())\n" +
                "    const g = func{async (): i32 -> 41}\n" +
                "    const n = await g()\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // R2-b㉙ 捕获 lambda 挂起：捕获 delta 的两个闭包实例
            // 各自建 frame 下钻（callee frame 含 .capture.this 拥有引用
            // 字段），结果 15,105
            Case("栈式跨界 捕获 lambda 间接调用挂起",
                "import core.io.Console\n" +
                "func makeAdder(delta: i32): Func\\<i32, i32> {\n" +
                "    return func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + delta\n" +
                "    }}\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var add10 = makeAdder(10)\n" +
                "    var add100 = makeAdder(100)\n" +
                "    Console.println((add10(5).toString() + \",\") + add100(5).toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-c㉚ new.indirect × tainted class init（分发点本身
            // 成为调用方挂起点）：精确 sheet 臂（IsTypeId ∧ 派生排
            // 除）命中 → 空 init 分配 + init frame 下钻，恢复后 41
            Case("栈式跨界 new.indirect init 内挂起",
                "import core.io.Console\n" +
                "pub class Slow {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        x = v\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t = typeOf(Slow)\n" +
                "    var o = new t(41)\n" +
                "    Console.println(o.x.toString())\n" +
                "    return 0\n" +
                "}\n"),
            // R2-c㉛ new.indirect 混合目标矩阵：tainted init 臂（41）
            // / 默认臂普通 init（82）/ 派生类自声明 init 经派生排除
            // 落默认臂（141）/ 无匹配 init NoSuchMethod（nomatch）/
            // tainted init 恢复后抛出沿构造异常边被同 fn 捕获
            Case("栈式跨界 new.indirect 混合目标矩阵",
                "import core.io.Console\n" +
                "pub open class Slow {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        x = v\n" +
                "    }\n" +
                "}\n" +
                "pub class Fast {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v * 2 }\n" +
                "}\n" +
                "pub class Derived : Slow {\n" +
                "    pub init(v: i32) { x = v + 100 }\n" +
                "}\n" +
                "pub class NoInit : Slow {\n" +
                "}\n" +
                "pub class Boom {\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        throw new core.RuntimeException(\"boom-\" + v.toString())\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var t1 = typeOf(Slow)\n" +
                "    var o1 = new t1(41)\n" +
                "    Console.println(o1.x.toString())\n" +
                "    var t2 = typeOf(Fast)\n" +
                "    var o2 = new t2(41)\n" +
                "    Console.println(o2.x.toString())\n" +
                "    var t3 = typeOf(Derived)\n" +
                "    var o3 = new t3(41)\n" +
                "    Console.println(o3.x.toString())\n" +
                "    try {\n" +
                "        var t4 = typeOf(NoInit)\n" +
                "        var o4 = new t4(41)\n" +
                "        Console.println(o4.x.toString())\n" +
                "    } catch (e: core.NoSuchMethodException) {\n" +
                "        Console.println(\"nomatch\")\n" +
                "    }\n" +
                "    try {\n" +
                "        var t5 = typeOf(Boom)\n" +
                "        var o5 = new t5(7)\n" +
                "    } catch (e: core.RuntimeException) {\n" +
                "        Console.println(\"caught:\" + e.getMessage())\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n"),
            // R2-c㉜ new.indirect 在 async 调用方（Tasked 协议）+
            // 泛型宿主构造 sheet 臂（类级 typeid 按臂构造形态常量
            // 合成）——9 / 42
            Case("栈式跨界 new.indirect async 调用方与泛型宿主",
                "import core.io.Console\n" +
                "pub class Slow {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) {\n" +
                "        yield\n" +
                "        x = v\n" +
                "    }\n" +
                "}\n" +
                "pub class Box\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(x: T) {\n" +
                "        yield\n" +
                "        v = x\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    var t = typeOf(Slow)\n" +
                "    var o = new t(9)\n" +
                "    Console.println(o.x.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    var proto = new Box\\<i32>(1)\n" +
                "    var tb = typeOf(proto)\n" +
                "    var b: Box\\<i32> = new tb(42)\n" +
                "    Console.println(b.v.toString())\n" +
                "    return 0\n" +
                "}\n"),

        };

    }
}
