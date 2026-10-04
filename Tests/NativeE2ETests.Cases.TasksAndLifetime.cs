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
        // 原编号 400..421 的有序用例；与主文件共享同一类型、字段及生命周期。
        private static (string Label, Action Run)[] CreateTasksAndLifetimeCases() => new (string Label, Action Run)[]
        {
            Case("协程不透明 body 冷 Task",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "func wrapI(body: core.AsyncFunc\\<i32>): Task\\<i32> {\n" +
                "    return new Task\\<i32>(body)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const t = wrap(func{async () -> { Console.println(\"opaque\") }})\n" +
                "    await t\n" +
                "    const n = await wrapI(func{async (): i32 -> 9})\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 冷 Task body 的 $$call 沿 extends 链继承（Sub 自身无
            // $$call）：VM 拍平 sheet 解析；native 工厂/bindColdBody
            // 链同语义（MW11c 棒5a 残余面收口）
            Case("协程冷 Task body 继承 $$call",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "pub shared abstract class Act : core.AsyncAction {\n" +
                "    pub override async operator call() {\n" +
                "        Console.println(\"inherited\")\n" +
                "    }\n" +
                "}\n" +
                "pub shared class Sub : Act {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub shared abstract class ActI : core.AsyncFunc\\<i32> {\n" +
                "    pub override async operator call(): i32 {\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n" +
                "pub shared class SubI : ActI {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const t = new Task(new Sub())\n" +
                "    await t\n" +
                "    const u = wrap(new Sub())\n" +
                "    await u\n" +
                "    const n = await new Task\\<i32>(new SubI())\n" +
                "    Console.println(n.toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 CoroutineLocal withValue/get",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "async func run() {\n" +
                "    const id = new CoroutineLocal\\<String>()\n" +
                "    const def = new CoroutineLocal\\<String>(\"def\")\n" +
                "    Console.println((id.get() == null).toString())\n" +
                "    Console.println(def.get() as String)\n" +
                "    await id.withValue(\"hi\", func{async () -> {\n" +
                "        Console.println(id.get() as String)\n" +
                "        await id.withValue(\"nest\", func{async () -> {\n" +
                "            Console.println(id.get() as String)\n" +
                "        }})\n" +
                "        Console.println(id.get() as String)\n" +
                "    }})\n" +
                "    Console.println((id.get() == null).toString())\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("协程 CoroutineLocal spawn 继承",
                "import core.io.Console\n" +
                "import core.coroutine.*\n" +
                "func wrap(body: core.AsyncAction): Task {\n" +
                "    return new Task(body)\n" +
                "}\n" +
                "async func run() {\n" +
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
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            Case("序列化平铺标量深复制",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Mix {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub var f: double = 0.0\n" +
                "    pub var b: bool = false\n" +
                "    pub var c: char = 'x'\n" +
                "    pub var s: String = \"\"\n" +
                "    pub init(_ -> n, _ -> f, _ -> b, _ -> c, _ -> s)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Mix(1, 2.5, true, 'a', \"hi\")\n" +
                "    var copy = deepCopy\\<Mix>(src)\n" +
                "    src.n = 9\n" +
                "    src.s = \"bye\"\n" +
                "    if (((((copy.n == 1) and (copy.f == 2.5)) and (copy.b == true)) and (copy.c == 'a')) and (copy.s == \"hi\")) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化嵌套对象图独立副本",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Inner {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "@Serializable\n" +
                "pub class Outer {\n" +
                "    pub var a: Inner\n" +
                "    pub var b: Inner\n" +
                "    pub init(x: Inner) { a = x\n        b = x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var kid = new Inner(7)\n" +
                "    var src = new Outer(kid)\n" +
                "    var copy = deepCopy\\<Outer>(src)\n" +
                "    copy.a.n = 3\n" +
                "    if (((src.a.n == 7) and (copy.b.n == 7)) and (copy.a.n == 3)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化集合字段快照不变量",
                "import core.serialization.*\n" +
                "import core.collections.*\n" +
                "@Serializable\n" +
                "pub class Box {\n" +
                "    pub var nums: Array\\<i32>\n" +
                "    pub var names: List\\<String> = new List\\<String>()\n" +
                "    pub var ages: Map\\<String, i32> = new Map\\<String, i32>()\n" +
                "    pub init(_ -> nums)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var nums = arrayOfElements\\<i32>(1, 2)\n" +
                "    var src = new Box(nums)\n" +
                "    src.names.add(\"a\")\n" +
                "    src.ages.set(\"k\", 4)\n" +
                "    var copy = deepCopy\\<Box>(src)\n" +
                "    src.nums[0] = 99\n" +
                "    src.names.add(\"b\")\n" +
                "    src.ages.set(\"k\", 5)\n" +
                "    const cn = copy.nums[0]\n" +
                "    const cl = copy.names.getAtIndex(0L)\n" +
                "    const cm = copy.ages.tryGet(\"k\")\n" +
                "    if (((((cn if? 0) == 1) and ((cl if? \"\") == \"a\")) and ((cm if? 0) == 4)) and (copy.names.length == 1L)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化 Temporary 往返新 resume",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Host {\n" +
                "    pub var n: i32 = 0\n" +
                "    @Temporary((func{ (): i32 -> {\n" +
                "        return@_ (n * 2)\n" +
                "    }} as core.Func\\<i32>))\n" +
                "    pub var derived: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Host(5)\n" +
                "    var copy = deepCopy\\<Host>(src)\n" +
                "    src.n = 9\n" +
                "    src.derived = 1\n" +
                "    if (((copy.n == 5) and (copy.derived == 10)) and (src.derived == 1)) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            Case("序列化 with Serializable 泛型 clone",
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "pub class Marked {\n" +
                "    pub var n: i32 = 0\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "func clone\\<T with Serializable>(x: T): T {\n" +
                "    return deepCopy\\<T>(x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var src = new Marked(11)\n" +
                "    var copy = clone\\<Marked>(src)\n" +
                "    src.n = 0\n" +
                "    if (copy.n == 11) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            // @SerializationBase 是独立能力，不合成 Serializable 代理。
            Case("序列化 @SerializationBase 独立能力",
                "func identity\\<T with SerializationBase>(value: T): T { return value }\n" +
                "pub func main(): i32 {\n" +
                "    var src = new core.collections.List\\<i32>()\n" +
                "    src.add(7)\n" +
                "    var copy = identity(src)\n" +
                "    if ((copy.getAtIndex(0L) as i32) == 7) {\n" +
                "        return 42\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n"),
            // ===== MW11d-C MessageQueue 传输层对拍（冒烟）=====：
            // create/post/next/EOS/release 全链；深复制经 Parcel 往返
            // （post 完成后改源对象不影响已入队消息）；负载 shared class
            // + @Serializable（async 边界共享安全 §4.5 + 可复制 §3.1）
            Case("MessageQueue 冒烟：post/next/EOS/release 全链",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "@Serializable\n" +
                "pub shared class Greeting {\n" +
                "    pub var code: i32\n" +
                "    pub var text: String\n" +
                "    pub init(_ -> code, _ -> text)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = new Messenger\\<Greeting>()\n" +
                "    const sender = owner\n" +
                "    const reader = owner.createReader()\n" +
                "    const live = new Greeting(7, \"hello\")\n" +
                "    await sender.send(live)\n" +
                "    live.code = 0\n" +
                "    live.text = \"mutated\"\n" +
                "    var item = await reader.next()\n" +
                "    if (item.isEos) { Console.println(\"FAIL eos\") }\n" +
                "    var g = (item.item as Greeting)\n" +
                "    Console.println(g.code.toString())\n" +
                "    Console.println(g.text)\n" +
                "    await sender.send(new Greeting(8, \"a\"))\n" +
                "    await sender.send(new Greeting(9, \"b\"))\n" +
                "    item = await reader.next()\n" +
                "    g = (item.item as Greeting)\n" +
                "    Console.println(g.code.toString())\n" +
                "    item = await reader.next()\n" +
                "    g = (item.item as Greeting)\n" +
                "    Console.println(g.code.toString())\n" +
                "    sender.dispose()\n" +
                "    item = await reader.next()\n" +
                "    if (item.isEos) { Console.println(\"EOS\") }\n" +
                "    reader.dispose()\n" +
                "    Console.println(\"smoke ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11d-Cb MessageQueue 对拍补齐（§28 电池）=====：
            // 与 Tests/BilVmTests.Messaging.cs 同款 Rigi 源；VM 为参考
            // 实现，native 须逐字节同 stdout/退出码
            // §11/§12 broadcast：两 reader 独立 cursor 见全部新消息、读速
            // 互不影响、新 reader 从队尾起、释放一个 reader 不影响兄弟
            Case("MessageQueue 对拍：broadcast 双 reader 独立 cursor",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = new Messenger\\<Msg>()\n" +
                "    const sender = owner\n" +
                "    const ra = owner.createReader()\n" +
                "    const rb = owner.createReader()\n" +
                "    await sender.send(new Msg(1))\n" +
                "    await sender.send(new Msg(2))\n" +
                "    const a1 = await ra.next()\n" +
                "    const a2 = await ra.next()\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                "    Console.println(((a2.item as Msg).n).toString())\n" +
                "    const b1 = await rb.next()\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                "    const rc = owner.createReader()\n" +
                "    await sender.send(new Msg(3))\n" +
                "    const c1 = await rc.next()\n" +
                "    Console.println(((c1.item as Msg).n).toString())\n" +
                "    rb.dispose()\n" +
                "    await sender.send(new Msg(4))\n" +
                "    const a3 = await ra.next()\n" +
                "    Console.println(((a3.item as Msg).n).toString())\n" +
                "    const c2 = await rc.next()\n" +
                "    Console.println(((c2.item as Msg).n).toString())\n" +
                "    sender.dispose()\n" +
                "    ra.dispose()\n" +
                "    rc.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // §9.2/§25 EOS 状态机：冷 Task next 挂起（Owner 活+0 Sender
            // 不 EOS）→ post 唤醒；Owner 去+Sender 活不 EOS；双去 sealed
            // 可 drain 后 EOS；sealed 后派生 Sender 拒绝（不可复活）
            Case("MessageQueue 对拍：EOS 状态机（挂起唤醒/sealed/drain/复活拒绝）",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "import core.messaging.QueueItem\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = new Messenger\\<Msg>()\n" +
                "    const reader = owner.createReader()\n" +
                // 冷 Task（§18.4）：直接调 async 函数是热 Task，须
                // new Task(func{async ...}) 才能 run()
                "    const pending = new Task\\<QueueItem\\<Msg>>(func{async (): QueueItem\\<Msg> -> {\n" +
                "        return@_ (await reader.next())\n" +
                "    } })\n" +
                "    pending.run()\n" +
                "    const sender = owner\n" +
                "    await sender.send(new Msg(1))\n" +
                "    const woken = await pending\n" +
                "    if (woken.isEos) {\n" +
                "        Console.println(\"FAIL early eos\")\n" +
                "    } else {\n" +
                "        Console.println((\"woken \" + ((woken.item as Msg).n).toString()))\n" +
                "    }\n" +
                "    await sender.send(new Msg(2))\n" +
                "    await sender.send(new Msg(3))\n" +
                "    sender.dispose()\n" +
                "    const d1 = await reader.next()\n" +
                "    Console.println(((d1.item as Msg).n).toString())\n" +
                "    const d2 = await reader.next()\n" +
                "    Console.println(((d2.item as Msg).n).toString())\n" +
                "    const tail = await reader.next()\n" +
                "    if (tail.isEos) { Console.println(\"EOS\") }\n" +
                "    try {\n" +
                "        await sender.send(new Msg(4))\n" +
                "        Console.println(\"FAIL revive\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    reader.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // §17 接受点（无 reader 也完成）+ §18 单 sender 顺序 +
            // §24 单 outstanding next 违规捕获。t1.run() 后裸 yield
            // 一圈：让 t1 内层 next 先 enter 入眠，t2 的 enter 才违规
            Case("MessageQueue 对拍：接受点+顺序+outstanding 违规捕获",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "import core.messaging.QueueItem\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = new Messenger\\<Msg>()\n" +
                "    const sender = owner\n" +
                "    await sender.send(new Msg(1))\n" +
                "    Console.println(\"post accepted no reader\")\n" +
                "    const reader = owner.createReader()\n" +
                "    await sender.send(new Msg(1))\n" +
                "    await sender.send(new Msg(2))\n" +
                "    await sender.send(new Msg(3))\n" +
                "    const i1 = await reader.next()\n" +
                "    const i2 = await reader.next()\n" +
                "    const i3 = await reader.next()\n" +
                "    Console.println((((i1.item as Msg).n).toString() + ((i2.item as Msg).n).toString()) + ((i3.item as Msg).n).toString())\n" +
                "    const t1 = new Task\\<QueueItem\\<Msg>>(func{async (): QueueItem\\<Msg> -> {\n" +
                "        return@_ (await reader.next())\n" +
                "    } })\n" +
                "    t1.run()\n" +
                "    yield\n" +
                "    const t2 = reader.next()\n" +
                "    try {\n" +
                "        const bad = await t2\n" +
                "        Console.println(\"FAIL outstanding\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    sender.dispose()\n" +
                "    const eos = await t1\n" +
                "    if (eos.isEos) { Console.println(\"EOS\") }\n" +
                "    reader.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 违规直抛（不捕获）：Reader 上 post → IllegalStateException
            // 穿透 await 到 run 协程顶层（VM 未观察失败；native 顶层
            // reporter + exit 1）
            FailCase("MessageQueue 违规：封存后发送直抛",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = new Messenger\\<Msg>()\n" +
                "    const reader = owner.createReader()\n" +
                "    owner.dispose()\n" +
                "    reader.dispose()\n" +
                "    await owner.send(new Msg(1))\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n",
                "队列已 sealed"),
            // 重复释放：墓碑诊断直抛（同步路径，main 内直接触发）
            Case("MessageQueue：重复 dispose 幂等",
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const owner = new Messenger\\<Msg>()\n" +
                "    const sender = owner\n" +
                "    sender.dispose()\n" +
                "    sender.dispose()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW11d-D Reader/Receiver/Messenger 高层 API 对拍 =====：
            // 与 Tests/BilVmTests.Messaging.cs 同款源；VM 参考，native
            // 逐字节同 stdout/退出码
            // Messenger 端到端 happy path：receiver 懒建 + listener 收
            // 消息 + 深复制快照（send 后改源对象）+ dispose → EOS 停泵
            Case("Messenger 对拍：send→receiver listener 深复制快照 + EOS 停泵",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub shared class Sink {\n" +
                "    pub var got: i32\n" +
                "    pub init() { got = 0 }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    recv.addListener(func{async (m: Msg) -> {\n" +
                "        sink.got = m.n\n" +
                "    } })\n" +
                "    const live = new Msg(7)\n" +
                "    await msgr.send(live)\n" +
                "    live.n = 0\n" +
                "    var spins: i32 = 0\n" +
                "    while ((sink.got != 7) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"snapshot \" + sink.got.toString()))\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 一条消息多 listener：listener 默认 ComputeExecutor（多 Worker 池，
            // 回调真并发——打印顺序在两个宿主上都不可假设），故 listener 只写
            // sink 状态，打印集中在 run() 单协程内按固定序产出；
            // removeListener 后只剩后者
            Case("Receiver 对拍：多 listener 注册序派发 + removeListener",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub shared class Sink {\n" +
                "    pub var a: i32\n" +
                "    pub var b: i32\n" +
                "    pub init() {\n" +
                "        a = 0\n" +
                "        b = 0\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    const l1 = func{async (m: Msg) -> {\n" +
                "        sink.a = m.n\n" +
                "    } }\n" +
                "    const l2 = func{async (m: Msg) -> {\n" +
                "        sink.b = m.n\n" +
                "    } }\n" +
                "    recv.addListener(l1)\n" +
                "    recv.addListener(l2)\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    var spins: i32 = 0\n" +
                "    while (((sink.a != 1) or (sink.b != 1)) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"L1:\" + sink.a.toString()))\n" +
                "    Console.println((\"L2:\" + sink.b.toString()))\n" +
                "    recv.removeListener(l1)\n" +
                "    await msgr.send(new Msg(2))\n" +
                "    spins = 0\n" +
                "    while ((sink.b != 2) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"L2:\" + sink.b.toString()))\n" +
                "    if (sink.a == 1) {\n" +
                "        Console.println(\"l1 removed\")\n" +
                "    } else {\n" +
                "        Console.println(\"FAIL l1\")\n" +
                "    }\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // 生命周期隔离：Reader.createReceiver 不消费源 cursor；
            // Receiver dispose 不影响其 createReader 出的 Reader
            Case("Receiver 对拍：cursor 独立 + dispose 隔离",
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const ra = msgr.createReader()\n" +
                "    const recvA = ra.createReceiver()\n" +
                "    recvA.addListener(func{async (m: Msg) -> { } })\n" +
                "    recvA.dispose()\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    const a1 = await ra.next()\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                "    const recvB = msgr.receiver\n" +
                "    const rb = recvB.createReader()\n" +
                "    recvB.dispose()\n" +
                "    await msgr.send(new Msg(2))\n" +
                "    const b1 = await rb.next()\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                "    msgr.dispose()\n" +
                "    const a2 = await ra.next()\n" +
                "    Console.println(((a2.item as Msg).n).toString())\n" +
                "    const eos = await ra.next()\n" +
                "    if (eos.isEos) { Console.println(\"EOS\") }\n" +
                "    ra.dispose()\n" +
                "    rb.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n"),
            // ===== MW12b §25.2：IDisposable 销毁时强制检查 + 全局异常通道 =====
            // 局部 IDisposable 对象未 dispose，作用域结束销毁 → 入队 →
            // drain → 空注册表默认分支 stderr 打印（退出码不变；VM 半场
            // B2 已接，stderr needle 双宿主同文本断言）
            NativeErrCase("mw12b_undisposed_default",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub var tag: i32\n" +
                "    pub init(t: i32) { tag = t }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = new Res(1)\n" +
                "    Console.println(\"made \" + r.tag.toString())\n" +
                "    return 0\n" +
                "}\n",
                "core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：Res"),
            // 正常 using 清理：dispose 进入即置位 → 无事件（双宿主
            // stderr 均无默认打印），stdout 双宿主对拍一致
            NativeErrCase("mw12b_disposed_ok",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { Console.println(\"disposed\") }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    seq using(const r = new Res()) {\n" +
                "        Console.println(\"in\")\n" +
                "    }\n" +
                "    Console.println(\"out\")\n" +
                "    return 0\n" +
                "}\n",
                "UndisposedResourceException", needlePresent: false),
            // 注册 GlobalExceptionHandler 处理器：drain 逐条调 dispatch →
            // 处理器打印 got:<类型名> 到 stdout。VM 半场 B2 已接事件通道
            // （VM 终结器入队 → Run 收尾 drain → dispatch），双宿主 stdout
            // 全对拍（made 行序先于 got 行——两宿主 drain 都在 main 之后）；
            // 有注册处理器不走默认分支，双宿主 stderr 无默认打印
            NativeErrCase("mw12b_undisposed_handler",
                "import core.io.Console\n" +
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.GlobalExceptionHandler.register(func{ (e: core.Exception) -> {\n" +
                "        if (e is core.UndisposedResourceException) {\n" +
                "            Console.println(\"got:\" +\n" +
                "                (e as core.UndisposedResourceException).resourceType)\n" +
                "        }\n" +
                "    } })\n" +
                "    const r = new Res()\n" +
                "    Console.println(\"made\")\n" +
                "    return 0\n" +
                "}\n",
                "UndisposedResourceException", needlePresent: false),

        };

    }
}
