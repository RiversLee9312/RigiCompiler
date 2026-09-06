// 确定积压、分段回收与 capability/监听器 churn；原生测试专用台账。
import core.messaging.*
import core.coroutine.*
import core.serialization.Serializable
import core.io.Console
@NativeLibrary("rigi_rt")
@NativeSymbol("mem_live_bytes")
priv native func liveBytes(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("mem_peak_bytes")
priv native func peakBytes(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_live_count")
priv native func gates(): i64
@NativeLibrary("rigi_rt")
@NativeSymbol("timer_live_bytes")
priv native func timerBytes(): i64
@Serializable
pub shared class Blob {
    pub var id: i32
    pub var text: String
    pub init(_ -> id, _ -> text)
}
func require(value: bool) {
    if (not value) { throw new core.RuntimeException("MQ资源压力断言失败") }
}
func sample(label: String) {
    Console.println((((((label + " bytes=") + liveBytes().toString()) + " gates=") + gates().toString()) + " timer-bytes=") + timerBytes().toString())
}
func expectReleased(handle: QueueHandle\<Blob>) {
    var denied = false
    try { MessageQueue.release_queue_handle(handle) }
    catch (e: core.IllegalStateException) { denied = true }
    require(denied)
}
func backlog() {
    var padding = "x"
    var i: i32 = 0
    while (i < 10) { padding = padding + padding
i = i + 1 }
    const owner = MessageQueue.create_queue\<Blob>()
    const sender = MessageQueue.add_queue_handle(owner, QueueHandleType.Sender)
    const fast = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    const slow = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    i = 0
    while (i < 10000) {
        await MessageQueue.post(sender, new Blob(i, i.toString() + padding))
        i = i + 1
    }
    sample("backlog=10000")
    const fullBytes = liveBytes()
    i = 0
    while (i < 6000) {
        require(((await MessageQueue.next(fast)).item as Blob).id == i)
        i = i + 1
    }
    MessageQueue.release_queue_handle(slow)
    const branch = MessageQueue.add_queue_handle(fast, QueueHandleType.Reader)
    sample("partial=4000")
    require(liveBytes() < (fullBytes - (4000000 as i64)))
    MessageQueue.release_queue_handle(owner)
    MessageQueue.release_queue_handle(sender)
    while (i < 10000) {
        require(((await MessageQueue.next(fast)).item as Blob).id == i)
        i = i + 1
    }
    require((await MessageQueue.next(fast)).isEos)
    require((await MessageQueue.next(branch)).isEos)
    MessageQueue.release_queue_handle(fast)
    MessageQueue.release_queue_handle(branch)
    // 仍持有全部已释放 capability 时取样，验证其不再钉住日志。
    sample("drained-held-capabilities")
    require(liveBytes() < (fullBytes - (8000000 as i64)))
    expectReleased(owner)
    expectReleased(sender)
    expectReleased(fast)
    expectReleased(slow)
    expectReleased(branch)
}
func churnOne() {
    const owner = MessageQueue.create_queue\<Blob>()
    const sender = MessageQueue.add_queue_handle(owner, QueueHandleType.Sender)
    const reader = MessageQueue.add_queue_handle(owner, QueueHandleType.Reader)
    const branch = MessageQueue.add_queue_handle(reader, QueueHandleType.Reader)
    MessageQueue.release_queue_handle(owner)
    await MessageQueue.post(sender, new Blob(1, "churn"))
    require(((await MessageQueue.next(reader)).item as Blob).id == 1)
    require(((await MessageQueue.next(branch)).item as Blob).id == 1)
    MessageQueue.release_queue_handle(sender)
    require((await MessageQueue.next(reader)).isEos)
    var denied = false
    try { MessageQueue.add_queue_handle(reader, QueueHandleType.Sender) }
    catch (e: core.IllegalStateException) { denied = true }
    require(denied)
    MessageQueue.release_queue_handle(reader)
    MessageQueue.release_queue_handle(branch)
}
pub shared class Counter {
    priv const gate: Mutex = new Mutex()
    priv var count: i32 = 0
    pub func add() {
        const token = await gate.acquire()
        try { count = count + 1 } finally(e) { gate.release(token) }
    }
    pub func read(): i32 {
        const token = await gate.acquire()
        try { return count } finally(e) { gate.release(token) }
    }
}
pub shared class Listener : core.AsyncAction\<Blob> {
    priv const counter: Counter
    pub init(_ -> counter)
    pub override async operator call(message: Blob) { counter.add() }
}
func listenerChurn() {
    const messenger = new Messenger\<Blob>()
    const receiver = messenger.receiver
    const isolated = messenger.createReader()
    const counter = new Counter()
    const first = new Listener(counter)
    const second = new Listener(counter)
    receiver.addListener(first)
    receiver.addListener(second)
    receiver.setExecutor(second, new ComputeExecutor())
    var i: i32 = 0
    while (i < 20) { await messenger.send(new Blob(i, "listener"))
i = i + 1 }
    while (counter.read() < 40) { yield sleep(1) }
    require(counter.read() == 40)
    receiver.removeListener(first)
    isolated.dispose()
    await messenger.send(new Blob(20, "remaining-listener"))
    while (counter.read() < 41) { yield sleep(1) }
    require(counter.read() == 41)
    receiver.dispose()
    messenger.dispose()
}
pub func main(): i32 {
    sample("start")
    backlog()
    sample("after-backlog")
    const baselineGates = gates()
    const baselineTimers = timerBytes()
    var steadyBytes: i64 = (0 as i64)
    var round: i32 = 0
    while (round < 5) {
        var i: i32 = 0
        while (i < 100) { churnOne()
i = i + 1 }
        i = 0
        while (i < 10) { listenerChurn()
i = i + 1 }
        yield sleep(20)
        sample("churn-round=" + round.toString())
        require(gates() < (baselineGates + (100 as i64)))
        // 容许仍存活的帧和属主异步 close；固定上限会捕获逐轮积累。
        require(timerBytes() < (baselineTimers + (16384 as i64)))
        // 第一轮完成池/监听器预热；后续固定4KiB余量，能捕获原先
        // 每轮1520B的已观察失败保留，不能随轮次抬高基线。
        if (round == 0) { steadyBytes = liveBytes() }
        else { require(liveBytes() <= (steadyBytes + (4096 as i64))) }
        round = round + 1
    }
    Console.println("resource-checks=passed peak-bytes=" + peakBytes().toString())
    return 0
}
