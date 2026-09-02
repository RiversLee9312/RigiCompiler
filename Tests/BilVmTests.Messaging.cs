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
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub var text: String\n" +
                "    pub init(_ -> n, _ -> text)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    const live = new Msg(7, \"hello\")\n" +
                "    await MessageQueue.post(sender, live)\n" +
                "    live.n = 0\n" +
                "    live.text = \"mutated\"\n" +
                "    var item = await MessageQueue.next(reader)\n" +
                "    var m = (item.item as Msg)\n" +
                "    Console.println(m.n.toString())\n" +
                "    Console.println(m.text)\n" +
                "    await MessageQueue.post(sender, new Msg(8, \"a\"))\n" +
                "    await MessageQueue.post(sender, new Msg(9, \"b\"))\n" +
                "    item = await MessageQueue.next(reader)\n" +
                "    m = (item.item as Msg)\n" +
                "    Console.println(m.n.toString())\n" +
                "    item = await MessageQueue.next(reader)\n" +
                "    m = (item.item as Msg)\n" +
                "    Console.println(m.n.toString())\n" +
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    item = await MessageQueue.next(reader)\n" +
                "    if (item.isEos) { Console.println(\"EOS\") }\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
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

        // §8.1 capability 矩阵全 16 行：违规抛 IllegalStateException 且
        // 消息文本精确（双端同文靠 Rigi 层统一翻译，native/VM 同错误码）
        private static void TestMessageQueueCapabilityMatrix()
        {
            var result = Run(
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                // Owner 不可派生（矩阵行 1）
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Owner)\n" +
                "        Console.println(\"FAIL owner->owner\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // Owner→Sender / Owner→Reader 合法
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    Console.println(\"owner derive ok\")\n" +
                // Owner 不可 post / next
                "    try {\n" +
                "        await MessageQueue.post(owner, new Msg(1))\n" +
                "        Console.println(\"FAIL owner post\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    try {\n" +
                "        const bad = await MessageQueue.next\\<Msg>(owner)\n" +
                "        Console.println(\"FAIL owner next\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // Sender→Sender 合法；Sender→Reader/Owner 拒绝
                "    const sender2 = MessageQueue.add_queue_handle\\<Msg>(sender, QueueHandleType.Sender)\n" +
                "    Console.println(\"sender derive sender ok\")\n" +
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(sender, QueueHandleType.Reader)\n" +
                "        Console.println(\"FAIL sender->reader\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(sender, QueueHandleType.Owner)\n" +
                "        Console.println(\"FAIL sender->owner\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // Sender 可 post 不可 next
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    Console.println(\"sender post ok\")\n" +
                "    try {\n" +
                "        const bad = await MessageQueue.next\\<Msg>(sender)\n" +
                "        Console.println(\"FAIL sender next\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // Reader→Reader 合法；Reader→Sender/Owner 拒绝
                "    const reader2 = MessageQueue.add_queue_handle\\<Msg>(reader, QueueHandleType.Reader)\n" +
                "    Console.println(\"reader derive reader ok\")\n" +
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(reader, QueueHandleType.Sender)\n" +
                "        Console.println(\"FAIL reader->sender\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(reader, QueueHandleType.Owner)\n" +
                "        Console.println(\"FAIL reader->owner\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // Reader 不可 post 可 next（收刚才 sender 的 Msg(1)）
                "    try {\n" +
                "        await MessageQueue.post(reader, new Msg(2))\n" +
                "        Console.println(\"FAIL reader post\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    const got = await MessageQueue.next(reader)\n" +
                "    Console.println(((got.item as Msg).n).toString())\n" +
                // 重复释放 / 已释放再用
                "    MessageQueue.release_queue_handle(sender2)\n" +
                "    try {\n" +
                "        MessageQueue.release_queue_handle(sender2)\n" +
                "        Console.println(\"FAIL double release\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    try {\n" +
                "        await MessageQueue.post(sender2, new Msg(3))\n" +
                "        Console.println(\"FAIL use after release\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // 收尾：全释放防看门狗
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
                "    MessageQueue.release_queue_handle(reader2)\n" +
                "    Console.println(\"ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    run()\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("MessageQueue capability 矩阵全 16 行", result);
            TestHarness.Check("MessageQueue capability stdout 精确",
                result.Stdout,
                "MessageQueue: 不能派生 Owner\n" +
                "owner derive ok\n" +
                "MessageQueue: 该句柄不能 post\n" +
                "MessageQueue: 该句柄不能 next\n" +
                "sender derive sender ok\n" +
                "MessageQueue: 不能从该句柄派生 Reader\n" +
                "MessageQueue: 不能派生 Owner\n" +
                "sender post ok\n" +
                "MessageQueue: 该句柄不能 next\n" +
                "reader derive reader ok\n" +
                "MessageQueue: 不能从该句柄派生 Sender\n" +
                "MessageQueue: 不能派生 Owner\n" +
                "MessageQueue: 该句柄不能 post\n" +
                "1\n" +
                "MessageQueue: 句柄重复释放\n" +
                "MessageQueue: 句柄已释放或不存在\n" +
                "ok\n");
        }

        // §9.2/§25 EOS 状态机：Owner 活+0 Sender 不 EOS（挂起后被 post 唤醒）、
        // Owner 去+Sender 活不 EOS、双去后 sealed 可 drain 后 EOS、不可复活
        //（sealed 后派生 Sender 拒绝）
        private static void TestMessageQueueLifetimeEos()
        {
            var result = Run(
                "import core.io.Console\n" +
                "import core.serialization.Serializable\n" +
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "import core.messaging.QueueItem\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                // 无 Sender 时 next 挂起而非 EOS；随后建 Sender post 唤醒之。
                // §18.1：直接调用 async 函数是 eager 热 Task，须以
                // new Task(func{async ...}) 造冷 Task 才能 run()
                "    const pending = new Task\\<QueueItem\\<Msg>>(func{async (): QueueItem\\<Msg> -> {\n" +
                "        return@_ (await MessageQueue.next\\<Msg>(reader))\n" +
                "    } })\n" +
                "    pending.run()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    const woken = await pending\n" +
                "    if (woken.isEos) {\n" +
                "        Console.println(\"FAIL early eos\")\n" +
                "    } else {\n" +
                "        Console.println((\"woken \" + ((woken.item as Msg).n).toString()))\n" +
                "    }\n" +
                // Owner 释放但 Sender 活：不 sealed 不 EOS（post 仍工作）
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    await MessageQueue.post(sender, new Msg(2))\n" +
                "    await MessageQueue.post(sender, new Msg(3))\n" +
                // Sender 释放 → sealed；已入队消息可 drain，随后 EOS
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    const d1 = await MessageQueue.next(reader)\n" +
                "    Console.println(((d1.item as Msg).n).toString())\n" +
                "    const d2 = await MessageQueue.next(reader)\n" +
                "    Console.println(((d2.item as Msg).n).toString())\n" +
                "    const tail = await MessageQueue.next(reader)\n" +
                "    if (tail.isEos) { Console.println(\"EOS\") }\n" +
                // sealed 后不可复活：派生 Sender 拒绝
                "    try {\n" +
                "        const bad = MessageQueue.add_queue_handle\\<Msg>(reader, QueueHandleType.Sender)\n" +
                "        Console.println(\"FAIL revive\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
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
                "MessageQueue: 不能从该句柄派生 Sender\n" +
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
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                "    const ra = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    const rb = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    await MessageQueue.post(sender, new Msg(2))\n" +
                // A 快 B 慢：A 读两条不影响 B
                "    const a1 = await MessageQueue.next(ra)\n" +
                "    const a2 = await MessageQueue.next(ra)\n" +
                "    Console.println(((a1.item as Msg).n).toString())\n" +
                "    Console.println(((a2.item as Msg).n).toString())\n" +
                "    const b1 = await MessageQueue.next(rb)\n" +
                "    Console.println(((b1.item as Msg).n).toString())\n" +
                // 迟来 reader 从队尾起：只见新消息
                "    const rc = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                "    await MessageQueue.post(sender, new Msg(3))\n" +
                "    const c1 = await MessageQueue.next(rc)\n" +
                "    Console.println(((c1.item as Msg).n).toString())\n" +
                // 释放 B 不影响 A/C
                "    MessageQueue.release_queue_handle(rb)\n" +
                "    await MessageQueue.post(sender, new Msg(4))\n" +
                "    const a3 = await MessageQueue.next(ra)\n" +
                "    Console.println(((a3.item as Msg).n).toString())\n" +
                "    const c2 = await MessageQueue.next(rc)\n" +
                "    Console.println(((c2.item as Msg).n).toString())\n" +
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    MessageQueue.release_queue_handle(ra)\n" +
                "    MessageQueue.release_queue_handle(rc)\n" +
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
                "import core.messaging.MessageQueue\n" +
                "import core.messaging.QueueHandleType\n" +
                "import core.messaging.QueueItem\n" +
                "import core.coroutine.*\n" +
                "@Serializable\n" +
                "pub shared class Msg {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "}\n" +
                "async func run() {\n" +
                "    const owner = MessageQueue.create_queue\\<Msg>()\n" +
                "    const sender = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Sender)\n" +
                // 接受点：无 reader 也完成（不等待任何接收侧）
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    Console.println(\"post accepted no reader\")\n" +
                "    const reader = MessageQueue.add_queue_handle\\<Msg>(owner, QueueHandleType.Reader)\n" +
                // 单 sender 顺序 A/B/C
                "    await MessageQueue.post(sender, new Msg(1))\n" +
                "    await MessageQueue.post(sender, new Msg(2))\n" +
                "    await MessageQueue.post(sender, new Msg(3))\n" +
                "    const i1 = await MessageQueue.next(reader)\n" +
                "    const i2 = await MessageQueue.next(reader)\n" +
                "    const i3 = await MessageQueue.next(reader)\n" +
                "    Console.println((((i1.item as Msg).n).toString() + ((i2.item as Msg).n).toString()) + ((i3.item as Msg).n).toString())\n" +
                // 同一 reader 并发 next 违规（t1 冷 Task 挂起中，t2 enter 被拒）。
                // 时序握手：t1 的 next 须先 enter 并入眠，t2 才构成违规——
                // run() 在 t1.run() 后裸 yield 一圈，让 t1（及其内层 eager
                // next 协程）先于 t2 的 enter 在 Worker 上跑到挂起点
                "    const t1 = new Task\\<QueueItem\\<Msg>>(func{async (): QueueItem\\<Msg> -> {\n" +
                "        return@_ (await MessageQueue.next\\<Msg>(reader))\n" +
                "    } })\n" +
                "    t1.run()\n" +
                "    yield\n" +
                "    const t2 = MessageQueue.next(reader)\n" +
                "    try {\n" +
                "        const bad = await t2\n" +
                "        Console.println(\"FAIL outstanding\")\n" +
                "    } catch (e: core.IllegalStateException) {\n" +
                "        Console.println(e.getMessage())\n" +
                "    }\n" +
                // 收尾 sealed → t1 收 EOS
                "    MessageQueue.release_queue_handle(owner)\n" +
                "    MessageQueue.release_queue_handle(sender)\n" +
                "    const eos = await t1\n" +
                "    if (eos.isEos) { Console.println(\"EOS\") }\n" +
                "    MessageQueue.release_queue_handle(reader)\n" +
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
                // 默认 = IOExecutor（§15.2）
                "    if (recv.getExecutor(l1) is IOExecutor) {\n" +
                "        Console.println(\"default io\")\n" +
                "    }\n" +
                "    recv.setExecutor(l2, new ComputeExecutor())\n" +
                "    if (recv.getExecutor(l2) is ComputeExecutor) {\n" +
                "        Console.println(\"set compute\")\n" +
                "    }\n" +
                // 未注册 callback 的 getExecutor → 默认 IOExecutor
                "    const l3 = func{async (m: Msg) -> { sink.a = 0 } }\n" +
                "    if (recv.getExecutor(l3) is IOExecutor) {\n" +
                "        Console.println(\"unregistered io\")\n" +
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
                "default io\n" +
                "set compute\n" +
                "unregistered io\n" +
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
    }
}
