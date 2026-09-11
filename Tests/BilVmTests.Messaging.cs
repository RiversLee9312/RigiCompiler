// MW11d-C：MessageQueue 传输层 VM 语义钉死（§28 电池——NativeE2E 对拍
// 只能锁双端分歧，双端同错需本套件的精确 stdout 断言兜底）
namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // 冒烟全链：create/post/next/EOS/release + 深复制快照（post 完成后
        // 改源对象不影响已入队消息）
        private static void TestMessageQueueSmokeSemantics()
        {
            var result = Run(
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.Messenger\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub var text: String\n" +
                "    pub init(_ -> n, _ -> text)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = new Messenger\\<Msg>()\n" +
                "    const sender = owner\n" +
                "    const reader = owner.createReader()\n" +
                "    const live = new Msg(7, \"hello\")\n" +
                "    await sender.send(live)\n" +
                "    live.n = 0\n" +
                "    live.text = \"mutated\"\n" +
                "    var item = await reader.next()\n" +
                "    var m = (item.item as Msg)\n" +
                "    Console.println(m.n.toString())\n" +
                "    Console.println(m.text)\n" +
                "    await sender.send(new Msg(8, \"a\"))\n" +
                "    await sender.send(new Msg(9, \"b\"))\n" +
                "    item = await reader.next()\n" +
                "    m = (item.item as Msg)\n" +
                "    Console.println(m.n.toString())\n" +
                "    item = await reader.next()\n" +
                "    m = (item.item as Msg)\n" +
                "    Console.println(m.n.toString())\n" +
                "    sender.dispose()\n" +
                "    item = await reader.next()\n" +
                "    if (item.isEos) { Console.println(\"EOS\") }\n" +
                "    reader.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("MessageQueue 冒烟全链", result);
            TestHarness.Check("MessageQueue 冒烟 stdout 精确",
                result.Stdout, "7\nhello\n8\n9\nEOS\nok\n");
        }

        // 复杂生命周期场景在 VM 上另作字面量断言，避免双端同错漏检。
        private static void TestMessageQueueCapabilityMatrix()
        {
            var result = Run(NativeE2ETests.MessagingLifecycleSource);
            CheckOk("MessageQueue OOP 生命周期", result);
            TestHarness.Check("MessageQueue 生命周期精确输出", result.Stdout, "mq-lifecycle-ok\n");
        }

        // §9.2/§25 EOS 状态机：Owner 活+0 Sender 不 EOS（挂起后被 post 唤醒）、
        // Owner 去+Sender 活不 EOS、双去后 sealed 可 drain 后 EOS、不可复活
        //（sealed 后派生 Sender 拒绝）
        private static void TestMessageQueueLifetimeEos()
        {
            var result = Run(
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
                // 无 Sender 时 next 挂起而非 EOS；随后建 Sender post 唤醒之。
                // §18.1：直接调用 async 函数是 eager 热 Task，须以
                // new Task(func{async ...}) 造冷 Task 才能 run()
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
                // Owner 释放但 Sender 活：不 sealed 不 EOS（post 仍工作）
                "    await sender.send(new Msg(2))\n" +
                "    await sender.send(new Msg(3))\n" +
                // Sender 释放 → sealed；已入队消息可 drain，随后 EOS
                "    sender.dispose()\n" +
                "    const d1 = await reader.next()\n" +
                "    Console.println(((d1.item as Msg).n).toString())\n" +
                "    const d2 = await reader.next()\n" +
                "    Console.println(((d2.item as Msg).n).toString())\n" +
                "    const tail = await reader.next()\n" +
                "    if (tail.isEos) { Console.println(\"EOS\") }\n" +
                // sealed 后不可复活：派生 Sender 拒绝
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
                "}\n");
            CheckOk("MessageQueue EOS 状态机", result);
            TestHarness.Check("MessageQueue EOS stdout 精确",
                result.Stdout,
                "woken 1\n" +
                "2\n" +
                "3\n" +
                "EOS\n" +
                "MessageQueue: 队列已 sealed，不能 post\n" +
                "ok\n");
        }

        // §11/§12 broadcast：两 reader 独立见全部新消息、读速互不影响、
        // 新 reader 从队尾起、释放一个 reader 不影响兄弟。a3=3：ra 尚
        // 未消费消息 3（独立 cursor 语义：每 reader 见全部消息，不跳读）
        private static void TestMessageQueueBroadcast()
        {
            var result = Run(
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
                // A 快 B 慢：A 读两条不影响 B
                "    const a1 = await ra.next()\n" +
                "    const a2 = await ra.next()\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                "    Console.println(((a2.item as Msg).n).toString())\n" +
                "    const b1 = await rb.next()\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                // 迟来 reader 从队尾起：只见新消息
                "    const rc = owner.createReader()\n" +
                "    await sender.send(new Msg(3))\n" +
                "    const c1 = await rc.next()\n" +
                "    Console.println(((c1.item as Msg).n).toString())\n" +
                // 释放 B 不影响 A/C
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
                "}\n");
            CheckOk("MessageQueue broadcast 独立 cursor", result);
            TestHarness.Check("MessageQueue broadcast stdout 精确",
                result.Stdout, "1\n2\n1\n3\n3\n4\nok\n");
        }

        // §17 接受点 + §18 顺序 + §24 单 outstanding next
        private static void TestMessageQueueAcceptanceAndOutstanding()
        {
            var result = Run(
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
                // 接受点：无 reader 也完成（不等待任何接收侧）
                "    await sender.send(new Msg(1))\n" +
                "    Console.println(\"post accepted no reader\")\n" +
                "    const reader = owner.createReader()\n" +
                // 单 sender 顺序 A/B/C
                "    await sender.send(new Msg(1))\n" +
                "    await sender.send(new Msg(2))\n" +
                "    await sender.send(new Msg(3))\n" +
                "    const i1 = await reader.next()\n" +
                "    const i2 = await reader.next()\n" +
                "    const i3 = await reader.next()\n" +
                "    Console.println((((i1.item as Msg).n).toString() + ((i2.item as Msg).n).toString()) + ((i3.item as Msg).n).toString())\n" +
                // 同一 reader 并发 next 违规（t1 冷 Task 挂起中，t2 enter 被拒）。
                // 时序握手：t1 的 next 须先 enter 并入眠，t2 才构成违规——
                // run() 在 t1.run() 后裸 yield 一圈，让 t1（及其内层 eager
                // next 协程）先于 t2 的 enter 在 Worker 上跑到挂起点
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
                // 收尾 sealed → t1 收 EOS
                "    sender.dispose()\n" +
                "    const eos = await t1\n" +
                "    if (eos.isEos) { Console.println(\"EOS\") }\n" +
                "    reader.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("MessageQueue 接受点/顺序/单 outstanding", result);
            TestHarness.Check("MessageQueue 接受点 stdout 精确",
                result.Stdout,
                "post accepted no reader\n" +
                "123\n" +
                "MessageQueue: 同一 Reader 同时只能有一个 outstanding next\n" +
                "EOS\n" +
                "ok\n");
        }
        // ===== MW11d-D：Receiver/Messenger 高层 API 电池（交接 §28
        // Receiver Executor 棒 + §12–§16 生命周期隔离）=====

        // §15：默认 listener Executor = IOExecutor（写死）；setExecutor/
        // getExecutor 往返；一条消息多 listener 各自 Executor；深复制
        // 快照（send 后改源对象不影响 listener 所见）。stdout 只由主
        // 协程按固定序打印（listener 跨 lane 执行序不作断言）
        private static void TestReceiverExecutorRouting()
        {
            var result = Run(
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
                "    const l1 = func{async (m: Msg) -> { sink.a = m.n } }\n" +
                "    const l2 = func{async (m: Msg) -> { sink.b = m.n } }\n" +
                "    recv.addListener(l1)\n" +
                "    recv.addListener(l2)\n" +
                // 默认 = ComputeExecutor（review-20260910 起：长回调不反压泵）
                "    if (recv.getExecutor(l1) is ComputeExecutor) {\n" +
                "        Console.println(\"default compute\")\n" +
                "    }\n" +
                "    recv.setExecutor(l2, new ComputeExecutor())\n" +
                "    if (recv.getExecutor(l2) is ComputeExecutor) {\n" +
                "        Console.println(\"set compute\")\n" +
                "    }\n" +
                // 未注册 callback 的 getExecutor → 默认 ComputeExecutor
                "    const l3 = func{async (m: Msg) -> { sink.a = 0 } }\n" +
                "    if (recv.getExecutor(l3) is ComputeExecutor) {\n" +
                "        Console.println(\"unregistered compute\")\n" +
                "    }\n" +
                // setExecutor 未注册 → 拒绝（无歧义，§14）
                "    try {\n" +
                "        recv.setExecutor(l3, new MainExecutor())\n" +
                "        Console.println(\"FAIL setExecutor unregistered\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // 深复制：send 完成后改源对象，listener 见快照 7
                "    const live = new Msg(7)\n" +
                "    await msgr.send(live)\n" +
                "    live.n = 0\n" +
                "    var spins: i32 = 0\n" +
                "    while (((sink.a != 7) or (sink.b != 7)) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    if ((sink.a == 7) and (sink.b == 7)) {\n" +
                "        Console.println(\"routed snapshot\")\n" +
                "    } else {\n" +
                "        Console.println(\"FAIL routing\")\n" +
                "    }\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Receiver Executor 路由", result);
            TestHarness.Check("Receiver Executor stdout 精确",
                result.Stdout,
                "default compute\n" +
                "set compute\n" +
                "unregistered compute\n" +
                "Receiver.setExecutor：listener 未注册\n" +
                "routed snapshot\n" +
                "ok\n");
        }

        // §12.1/§13.1：createReceiver 不消费源 Reader cursor（branch 出
        // 独立 cursor，迟来从队尾起订）；Receiver.createReader 独立于
        // Receiver 当前内部 cursor
        private static void TestReceiverReaderCursorIndependence()
        {
            var result = Run(
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
                "    const reader = msgr.createReader()\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    await msgr.send(new Msg(2))\n" +
                "    const sink = new Sink()\n" +
                // createReceiver = branch（队尾起订）：不见 1/2
                "    const recv = reader.createReceiver()\n" +
                "    recv.addListener(func{async (m: Msg) -> { sink.got = m.n } })\n" +
                "    await msgr.send(new Msg(3))\n" +
                "    var spins: i32 = 0\n" +
                "    while ((sink.got != 3) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"recv got \" + sink.got.toString()))\n" +
                // 源 reader cursor 未被消费：1/2/3 依序可读
                "    const i1 = await reader.next()\n" +
                "    const i2 = await reader.next()\n" +
                "    const i3 = await reader.next()\n" +
                "    Console.println((((i1.item as Msg).n).toString() + ((i2.item as Msg).n).toString()) + ((i3.item as Msg).n).toString())\n" +
                // Receiver.createReader 独立 cursor（队尾起订）
                "    const rc = recv.createReader()\n" +
                "    await msgr.send(new Msg(4))\n" +
                "    const i4 = await rc.next()\n" +
                "    Console.println(((i4.item as Msg).n).toString())\n" +
                "    const i5 = await reader.next()\n" +
                "    Console.println(((i5.item as Msg).n).toString())\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    reader.dispose()\n" +
                "    rc.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Receiver/Reader cursor 独立性", result);
            TestHarness.Check("cursor 独立 stdout 精确",
                result.Stdout, "recv got 3\n123\n4\n4\nok\n");
        }

        // §13.2 生命周期隔离两条：dispose Receiver 不影响源 Reader；
        // dispose Receiver 不影响它 createReader 出去的 Reader
        //（dispose 与挂起 pump 的竞态走「句柄释放唤醒 + 静默停泵」）
        private static void TestReceiverDisposeIsolation()
        {
            var result = Run(
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
                // 第一条：A 的 Receiver  dispose 后 A 仍可读
                "    const ra = msgr.createReader()\n" +
                "    const recvA = ra.createReceiver()\n" +
                "    recvA.addListener(func{async (m: Msg) -> { } })\n" +
                "    recvA.dispose()\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    const a1 = await ra.next()\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                // 第二条：Receiver dispose 后其 createReader 出的 rb 仍可读
                "    const recvB = msgr.receiver\n" +
                "    const rb = recvB.createReader()\n" +
                "    recvB.dispose()\n" +
                "    await msgr.send(new Msg(2))\n" +
                "    const b1 = await rb.next()\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                "    msgr.dispose()\n" +
                // ra cursor 尚有消息 2 未读（§25：先 drain 后 EOS）
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
                "}\n");
            CheckOk("Receiver dispose 隔离", result);
            TestHarness.Check("dispose 隔离 stdout 精确",
                result.Stdout, "1\n2\n2\nEOS\nok\n");
        }

        // §14 listener identity：同位点工厂两实例是两个独立 listener
        //（对象身份键；toString 同位点相等不能当身份——playground 实证）。
        // add 重复注册幂等；removeListener 按身份只摘自己
        private static void TestReceiverListenerIdentity()
        {
            var result = Run(
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
                "func makeListener(sink: Sink, slot: i32): core.AsyncAction\\<Msg> {\n" +
                "    return func{async (m: Msg) -> {\n" +
                "        if (slot == 1) {\n" +
                "            sink.a = m.n\n" +
                "        } else {\n" +
                "            sink.b = m.n\n" +
                "        }\n" +
                "    } }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    const l1 = makeListener(sink, 1)\n" +
                "    const l2 = makeListener(sink, 2)\n" +
                "    recv.addListener(l1)\n" +
                "    recv.addListener(l1)\n" +
                "    recv.addListener(l2)\n" +
                "    await msgr.send(new Msg(5))\n" +
                "    var spins: i32 = 0\n" +
                "    while (((sink.a != 5) or (sink.b != 5)) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"both \" + (sink.a + sink.b).toString()))\n" +
                "    recv.removeListener(l1)\n" +
                "    await msgr.send(new Msg(6))\n" +
                "    spins = 0\n" +
                "    while ((sink.b != 6) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    if ((sink.a == 5) and (sink.b == 6)) {\n" +
                "        Console.println(\"removed one\")\n" +
                "    } else {\n" +
                "        Console.println(\"FAIL remove\")\n" +
                "    }\n" +
                "    msgr.dispose()\n" +
                "    yield sleep(20)\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Receiver listener 身份", result);
            TestHarness.Check("listener 身份 stdout 精确",
                result.Stdout, "both 10\nremoved one\nok\n");
        }

        // §16/§17/§25：Messenger send → receiver listener 收到；
        // Messenger dispose → sealed → receiver drain 后 EOS 停泵
        //（pump 退出 = 进程 quiescence 干净落幕）
        private static void TestMessengerDisposeEosStopsPump()
        {
            var result = Run(
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
                "    pub var sum: i32\n" +
                "    pub init() { sum = 0 }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    recv.addListener(func{async (m: Msg) -> { sink.sum = sink.sum + m.n } })\n" +
                "    await msgr.send(new Msg(1))\n" +
                "    await msgr.send(new Msg(2))\n" +
                // dispose 不撤回已接受消息（§25）：sealed 后仍 drain
                "    msgr.dispose()\n" +
                "    var spins: i32 = 0\n" +
                "    while ((sink.sum != 3) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"drained \" + sink.sum.toString()))\n" +
                "    recv.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Messenger dispose → EOS 停泵", result);
            TestHarness.Check("EOS 停泵 stdout 精确",
                result.Stdout, "drained 3\nok\n");
        }

        // 泵 lane 回归（#泵IO）：Receiver 泵经 AsyncShell.start 固定起在
        // IOExecutor lane（不占主 lane）。多生产者并发 send 时消息全量
        // 投递、main 协程与泵并行不互卡；dispose 后 main 正常退出
        //（quiescence 干净落幕，§17.4）——泵 lane 迁移不得改变投递与
        // 退出语义，只消除与主 lane 的串行争用。
        private static void TestReceiverPumpIoLaneFullDelivery()
        {
            var result = Run(
                "import core.io.Console\n" +
                "import core.collections.*\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.*\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "pub shared class Sink {\n" +
                "    pub var count: i32\n" +
                "    pub init() { count = 0 }\n" +
                "}\n" +
                "async func produce(msgr: Messenger\\<Msg>, rounds: i32) {\n" +
                "    var i = 0\n" +
                "    while (i < rounds) {\n" +
                "        await msgr.send(new Msg(i))\n" +
                "        yield\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "}\n" +
                "async func run() {\n" +
                "    const msgr = new Messenger\\<Msg>()\n" +
                "    const recv = msgr.receiver\n" +
                "    const sink = new Sink()\n" +
                "    recv.addListener(func{async (m: Msg) -> { sink.count = sink.count + 1 } })\n" +
                "    const producers = arrayOf\\<Task>(3)\n" +
                "    var i = 0\n" +
                "    while (i < 3) {\n" +
                "        const t = new Task(func{async () -> { await produce(msgr, 5) }})\n" +
                "        t.run(new ComputeExecutor())\n" +
                "        producers[i] = t\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    i = 0\n" +
                "    while (i < 3) {\n" +
                "        await (producers[i] as Task)\n" +
                "        i = i + 1\n" +
                "    }\n" +
                "    var spins: i32 = 0\n" +
                "    while ((sink.count != 15) and (spins < 400)) {\n" +
                "        yield sleep(5)\n" +
                "        spins = spins + 1\n" +
                "    }\n" +
                "    Console.println((\"delivered \" + sink.count.toString()))\n" +
                "    recv.dispose()\n" +
                "    msgr.dispose()\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("泵上 IO lane 全量投递", result);
            TestHarness.Check("泵上 IO lane stdout 精确",
                result.Stdout, "delivered 15\nok\n");
        }
        // #回调定时器 回归（VM 字面量钉死）：listener 回调体内含定时器挂起
        //（yield sleep 80ms）经 Receiver 冷 Task 派发后必须全部恢复。到达
        // 检测按消息唯一 cell 一次性写入——共享计数器 RMW（load 与 store
        // 隔着 AtomicStruct 异步 Mutex 挂起点）在并行回调下丢更新，曾被
        // 误诊为「协程不恢复」；与 NativeE2ETests.
        // ReceiverListenerTimerResumeSource 同源对拍。
        private static void TestReceiverListenerTimerSuspendResume()
        {
            var result = Run(NativeE2ETests.ReceiverListenerTimerResumeSource);
            CheckOk("listener 定时器挂起恢复全量投递", result);
            TestHarness.Check("listener 定时器挂起恢复 stdout 精确",
                result.Stdout, "listener-sleep-ok\n");
        }
    }
}
