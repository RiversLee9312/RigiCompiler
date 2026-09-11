// Rigi 标准库：纯语言消息队列；安全 AtomicList 保存消息，Mutex 统一线性化。
// MessageQueue 独占队列机制；Reader/Receiver/Messenger 只做外部封装。
namespace core.messaging

import core.serialization.Serializable

pub shared class QueueItem\<shared T> {
    pub var isEos: bool
    pub var item: T?
    pub init(_ -> isEos, _ -> item)
}

// reader 记录不反向引用队列。空读时使用一次性粘滞事件等待（#15）：
// 每轮等待新建一个 EventAlarm 底座，post/close 在队列闸内 fire 唤醒。
// 相比 Mutex 占位-重取协议，yield EventAlarm 在宿主侧一次登记直达，
// 不经 acquire 子协程接力，调度段数大幅下降（VM 宿主丢消息的直接
// 成因是泵消费链超时，见 #15 报告）。
priv shared class QueueReaderState\<shared TMessage with core.serialization.Serializable> {
    pub var cursor: i64
    pub var segment: QueueLogSegment\<TMessage>?
    pub var inNext: bool
    pub var active: bool
    // 等待握手：waiting=true 表示「下一轮等待已武装」；wake 事件在队列
    // 闸内成对重置。fire（粘滞）与武装在同一闸内原子，先 fire 后 yield
    // 由粘滞语义兜底（已触发事件的 yield 立即返回，无丢失窗口）。
    pub var waiting: bool
    pub var wake: QueueWakeup
    pub var following: QueueReaderState\<TMessage>?
    pub init(position: i64) {
        cursor = position
        segment = null
        inNext = false
        active = true
        waiting = false
        wake = new QueueWakeup()
        following = null
    }
    pub func signal() {
        if (waiting) {
            waiting = false
            wake.fire()
        }
    }
}

// 手动粘滞事件底座：EventAlarm.signal 为 protected，经本类 fire 暴露
// 给队列内部唤醒路径（每轮等待换新实例，粘滞不跨轮残留）。
priv shared class QueueWakeup : core.coroutine.EventAlarm {
    pub func fire() {
        signal()
    }
}

// 有界消息段；每段复用安全 AtomicList。日志不把整个积压放进一个
// 巨型数组，避免临时引用释放反复把同一超阈数组计入 GC 候选债务。
priv shared class QueueLogSegment\<shared TMessage with core.serialization.Serializable> {
    pub const base: i64
    pub var count: i64
    pub const log: core.AtomicList\<TMessage>
    pub var following: QueueLogSegment\<TMessage>?
    pub init(position: i64) {
        base = position
        count = (0 as i64)
        log = core.AtomicList.fromList\<TMessage>(new core.collections.List\<TMessage>())
        following = null
    }
}

// MQ 的唯一实现对象。锁、日志、reader 注册、水位回收和
// sealed/EOS 规则都封装在这里；外层对象不直接操作这些细节。
priv shared class MessageQueue\<shared TMessage with core.serialization.Serializable> {
    priv const gate: core.coroutine.Mutex
    priv var first: QueueLogSegment\<TMessage>?
    priv var last: QueueLogSegment\<TMessage>?
    priv var base: i64
    priv var head: i64
    priv var tail: i64
    priv var accepting: bool
    priv var readers: QueueReaderState\<TMessage>?
    internal init() {
        gate = new core.coroutine.Mutex()
        first = null
        last = null
        base = (0 as i64)
        head = (0 as i64)
        tail = (0 as i64)
        accepting = true
        readers = null
    }

    internal async func post(item: TMessage) {
        // 没有 reader 时也必须先执行深复制的安全校验。
        const copy = core.serialization.deepCopy\<TMessage>(item)
        const lock = await gate.acquire()
        try {
            if (not accepting) { throw new core.IllegalStateException(mqError(-8)) }
            if (readers != null) { append(copy) }
            tail = (tail + (1 as i64))
            if (readers == null) {
                head = tail
                base = tail
            }
            signal()
        } finally(e) { gate.release(lock) }
    }

    internal func createReader(): Reader\<TMessage> {
        const lock = await gate.acquire()
        try {
            const reader = new QueueReaderState\<TMessage>(tail)
            reader.segment = last
            reader.following = readers
            readers = reader
            return new MessageQueueReader\<TMessage>(this, reader)
        } finally(e) { gate.release(lock) }
    }

    internal async func next(reader: QueueReaderState\<TMessage>): QueueItem\<TMessage> {
        const entered = await gate.acquire()
        try {
            if (not reader.active) { throw new core.IllegalStateException(mqError(-1)) }
            if (reader.inNext) { throw new core.IllegalStateException(mqError(-10)) }
            reader.inNext = true
        } finally(e) { gate.release(entered) }
        try {
            while (true) {
                const lock = await gate.acquire()
                try {
                    if (not reader.active) { throw new core.IllegalStateException(mqError(-1)) }
                    if (reader.cursor < tail) {
                        const value = read(reader)
                        reader.cursor = (reader.cursor + (1 as i64))
                        reclaim()
                        return new QueueItem\<TMessage>(false, value)
                    }
                    if (not accepting) { return new QueueItem\<TMessage>(true, null) }
                    // 武装一次性粘滞事件后在闸外 yield 直挂（#15）：fire 与
                    // 武装同闸原子；先 fire 后 yield 由粘滞语义兜底（已触发
                    // 的 yield 立即返回重新检查，无丢失窗口）。
                    reader.wake = new QueueWakeup()
                    reader.waiting = true
                } finally(e) { gate.release(lock) }
                yield (reader.wake as core.coroutine.EventAlarm)
            }
        } finally(e) {
            const lock = await gate.acquire()
            try {
                reader.signal()
                reader.inNext = false
            } finally(e) { gate.release(lock) }
        }
        return new QueueItem\<TMessage>(true, null)
    }

    internal func closeReader(reader: QueueReaderState\<TMessage>) {
        const lock = await gate.acquire()
        try {
            if (not reader.active) { return }
            reader.active = false
            removeReader(reader)
            reclaim()
        } finally(e) { gate.release(lock) }
    }

    internal func close() {
        const lock = await gate.acquire()
        try {
            if (not accepting) { return }
            accepting = false
            signal()
            reclaim()
        } finally(e) { gate.release(lock) }
    }

    // 所有段链接、段内计数和 reader 缓存都由外层队列 gate 保护。
    priv func append(item: TMessage) {
        if (last == null) {
            const segment = new QueueLogSegment\<TMessage>(tail)
            first = segment
            last = segment
        }
        if ((last as QueueLogSegment\<TMessage>).count == (64 as i64)) {
            const segment = new QueueLogSegment\<TMessage>(tail)
            (last as QueueLogSegment\<TMessage>).following = segment
            last = segment
        }
        const segment = (last as QueueLogSegment\<TMessage>)
        await segment.log.add(item)
        segment.count = (segment.count + (1 as i64))
    }
    priv func read(reader: QueueReaderState\<TMessage>): TMessage? {
        if (reader.segment == null) { reader.segment = first }
        while (true) {
            const segment = (reader.segment as QueueLogSegment\<TMessage>)
            if (reader.cursor < (segment.base + segment.count)) {
                return await segment.log.getAtIndex((reader.cursor - segment.base))
            }
            reader.segment = segment.following
        }
        return null
    }
    priv func signal() {
        var current = readers
        while (current != null) {
            const r = (current as QueueReaderState\<TMessage>)
            r.signal()
            current = r.following
        }
    }
    // 最慢 live reader 决定绝对水位；整段消费完立即断链。
    // 先更新 reader 缓存，已关闭 reader 或旧段都不钉住历史前缀。
    priv func reclaim() {
        var mark = tail
        var current = readers
        while (current != null) {
            const r = (current as QueueReaderState\<TMessage>)
            if (r.cursor < mark) { mark = r.cursor }
            if (r.segment != null) {
                const segment = (r.segment as QueueLogSegment\<TMessage>)
                if ((r.cursor >= (segment.base + segment.count)) and (segment.following != null)) {
                    r.segment = segment.following
                }
            }
            current = r.following
        }
        head = mark
        if (head == tail) {
            current = readers
            while (current != null) {
                const r = (current as QueueReaderState\<TMessage>)
                r.segment = null
                current = r.following
            }
        }
        while (first != null) {
            const segment = (first as QueueLogSegment\<TMessage>)
            if ((segment.base + segment.count) > head) { break }
            first = segment.following
            segment.following = null
        }
        if (first == null) { last = null }
        base = if (first == null) { tail } else { (first as QueueLogSegment\<TMessage>).base }
    }
    priv func removeReader(target: QueueReaderState\<TMessage>) {
        const expectedReader = (target as Object)
        var previous: QueueReaderState\<TMessage>? = null
        var current = readers
        while (current != null) {
            const r = (current as QueueReaderState\<TMessage>)
            var matches = false
            const candidate = (r as Object)
            seq using(const expected = placeOf expectedReader) {
                seq using(const actual = placeOf candidate) { matches = (expected == actual) }
            }
            if (matches) {
                if (previous == null) { readers = r.following }
                else { (previous as QueueReaderState\<TMessage>).following = r.following }
                r.following = null
                r.segment = null
                r.signal()
                return
            }
            previous = r
            current = r.following
        }
    }
}

// 队列对象的状态违规统一抛 IllegalStateException。
priv func mqError(code: i32): String {
    if (code == -1) {
        return "MessageQueue: Reader 已释放"
    }
    if (code == -8) {
        return "MessageQueue: 队列已 sealed，不能 post"
    }
    if (code == -10) {
        return "MessageQueue: 同一 Reader 同时只能有一个 outstanding next"
    }
    return "MessageQueue: 非法操作"
}

// ===== Reader / Receiver / Messenger 外层 API =====
// MessageQueue 封装传输机制与 cursor 管理；Receiver 只组合 Reader，
// Messenger 只组合 MessageQueue，callback 经 Task.run 执行。

// 接收侧 pull 对象；游标仅传给队列，所有状态操作由队列负责。
pub shared abstract class Reader\<shared TMessage with core.serialization.Serializable> implements core.IDisposable {
    pub abstract async func next(): QueueItem\<TMessage>
    pub abstract func branch(): Reader\<TMessage>
    pub abstract override func dispose()

    pub func createReceiver(): Receiver\<TMessage> {
        return new Receiver\<TMessage>(this.branch())
    }
}

// 私有实现把队列和游标封装起来；公开 Reader 不接收可伪造的内部状态。
priv shared class MessageQueueReader\<shared TMessage with core.serialization.Serializable> : Reader\<TMessage> {
    priv const queue: MessageQueue\<TMessage>
    priv const cursor: QueueReaderState\<TMessage>
    priv const disposeGate: core.coroutine.Mutex
    priv var disposed: bool

    internal init(_ -> queue, _ -> cursor) {
        disposed = false
        disposeGate = new core.coroutine.Mutex()
    }

    // 下一条消息：转发 MessageQueue.next（单 outstanding 纪律见 §24）
    pub override async func next(): QueueItem\<TMessage> {
        return (await queue.next(cursor))
    }

    // 新 Reader 从当前队尾起订，不复用源 cursor。
    pub override func branch(): Reader\<TMessage> {
        const lock = await disposeGate.acquire()
        try {
            if (disposed) { throw new core.IllegalStateException(mqError(-1)) }
            return queue.createReader()
        } finally(e) { disposeGate.release(lock) }
    }

    // 幂等注销自己的游标并唤醒挂起的 next。
    pub override func dispose() {
        const lock = await disposeGate.acquire()
        try {
            if (disposed) { return }
            disposed = true
            queue.closeReader(cursor)
        } finally(e) { disposeGate.release(lock) }
    }
}

// listener 登记表项（priv：只服务 Receiver 内部；id = callback 对象身份）
priv shared class ListenerEntry\<shared TMessage with core.serialization.Serializable> {
    pub var callback: core.AsyncAction\<TMessage>
    pub var executor: core.coroutine.Executor
    pub init(_ -> callback, _ -> executor)
}

// ===== 冷 Task 非泛型壳（priv，文件内机制）=====
// 约束来源（playground 实证 ×2）：① 泛型宿主内的 lambda 隐藏类会被
// 外层 GP 参数化，冷 Task spawn-into（bindColdBody type.is 链）无匹配；
// ② stdlib 切片里的 lambda/cell 隐藏类符号在多重合并/BIL 往返下撞名
//（§21.2 符号重复）。两约束同解：不写 lambda，以手写 AsyncAction
// 子类经显式字段承载 invoker（非泛型、无隐藏类）。
// invoke 为同步方法：async 虚调用在 native VirtualCallEmitter 无通道
//（实证崩溃）；invoke 体内的 async 调用按 §18.1 eager 热 Task 在
// 当前 lane（= 派发冷 Task 的 Executor lane）起跑，quiescence 登记
// 由热 Task spawn 承担，冷 Task 只做 lane 迁移。
priv shared abstract class AsyncInvoker {
    pub abstract func invoke()
}

// 冷 Task body 的显式 AsyncAction 形态：new Task(new InvokeAction(...))
priv shared class InvokeAction : core.AsyncAction {
    priv const invoker: AsyncInvoker
    pub init(_ -> invoker)

    pub override async operator call() {
        invoker.invoke()
    }
}

// 非泛型冷 Task 壳：start 以 IOExecutor lane 起泵（泵协程脱离主 lane，
// 不与 main 协程串行争用——主 lane 上泵被 main 的调度段卡死时消息
// 吞吐骤降；IOExecutor 保持单 Worker，泵自身仍串行消费）；dispatch 在
// 指定 Executor 起单次调用（§15.1 派发通道 = 冷 Task + run(executor)）
priv shared class AsyncShell {
    pub static func start(invoker: AsyncInvoker) {
        const t = new core.coroutine.Task(new InvokeAction(invoker))
        t.run(new core.coroutine.IOExecutor())
    }

    pub static func dispatch(invoker: AsyncInvoker, executor: core.coroutine.Executor) {
        const t = new core.coroutine.Task(new InvokeAction(invoker))
        t.run(executor)
    }
}

// Receiver pump 的泛型适配：把 invoke 桥接到 Receiver.pumpLoop
//（pumpLoop 自身是 async——eager 热 Task 在泵 lane 起跑）
priv shared class PumpAdapter\<shared TMessage with core.serialization.Serializable> : AsyncInvoker {
    priv const target: Receiver\<TMessage>
    pub init(_ -> target)

    pub override func invoke() {
        target.pumpLoop()
    }
}

// 单次 listener 派发的泛型适配：invoke = 以消息调 callback。
// message 字段为裸 GP——shared class 的 GP 字段声明侧跳过、构造点
// 重跑共享安全闭包检查（§3.1.1，QueueItem\<T> 同例）
priv shared class ListenerCall\<shared TMessage with core.serialization.Serializable> : AsyncInvoker {
    priv const callback: core.AsyncAction\<TMessage>
    priv const message: TMessage
    pub init(_ -> callback, _ -> message)

    pub override func invoke() {
        callback(message)
    }
}

// Reader 的 push View：独立 cursor、listener 对象身份；未 setExecutor 的
// listener 默认 ComputeExecutor（多 Worker 池，回调并行且不反压泵；泵独占
// IO lane 单 Worker——review-20260910：早期默认 IOExecutor 会让长回调与
// 泵串行抢同一 Worker，吞吐崩塌）。
// 并行回调纪律（review-20260910 #回调定时器 插桩实证）：多 listener（或
// 同 listener 被并行派发的多消息）在真并行宿主下会交错执行。回调内对共享
// 状态做「load 后 store」的读改写组合会丢更新——两次调用之间隔着该共享对
// 象内部异步 Mutex 的挂起点，别的回调可趁隙写回。全量到达判定请用「每条
// 消息唯一 cell 一次性写入」或读改写一步完成的原子更新；单计数器 RMW 的
// 计数停滞会被误读成「回调未恢复」。
pub shared class Receiver\<shared TMessage with core.serialization.Serializable> implements core.IDisposable {
    priv const internalReader: Reader\<TMessage>
    priv var entries: Array\<ListenerEntry\<TMessage>>
    priv var entryCount: i32
    priv const gate: core.coroutine.Mutex
    priv var disposed: bool

    // 包装 Reader 并立即起泵（调用方让渡 reader 所有权给本对象）
    pub init(reader: Reader\<TMessage>) {
        internalReader = reader
        entries = core.collections.arrayOf\<ListenerEntry\<TMessage>>(4)
        entryCount = 0
        gate = new core.coroutine.Mutex()
        disposed = false
        startPump()
    }

    // §14：callback 对象自身即 identity。重复注册幂等（同一 callback
    // 只登记一次，行为无歧义）。已 dispose 拒绝登记
    pub func addListener(callback: core.AsyncAction\<TMessage>) {
        const lock = await gate.acquire()
        try {
            if (disposed) {
                throw new core.IllegalStateException(
                    "Receiver.addListener：Receiver 已 dispose")
            }
            if (findIndex(callback) >= 0) { return }
            if (entryCount == entries.length) { growEntries() }
            entries[entryCount] = new ListenerEntry\<TMessage>(
                callback, new core.coroutine.ComputeExecutor())
            entryCount = entryCount + 1
        } finally(e) {
            gate.release(lock)
        }
    }

    // §14：按 callback 身份摘除；未注册为无操作（幂等）
    pub func removeListener(callback: core.AsyncAction\<TMessage>) {
        const lock = await gate.acquire()
        try {
            const index = findIndex(callback)
            if (index < 0) { return }
            var i = index
            while (i < (entryCount - 1)) {
                entries[i] = (entries[(i + 1)] as ListenerEntry\<TMessage>)
                i = i + 1
            }
            entryCount = entryCount - 1
            // Array 的写入元素类型不接收 null；重建表排除旧尾槽引用。
            const retained = core.collections.arrayOf\<ListenerEntry\<TMessage>>(entries.length)
            i = 0
            while (i < entryCount) {
                retained[i] = (entries[i] as ListenerEntry\<TMessage>)
                i = i + 1
            }
            entries = retained
        } finally(e) {
            gate.release(lock)
        }
    }

    // §15：listener 级 Executor 路由。未注册 callback 拒绝（无歧义）
    pub func setExecutor(callback: core.AsyncAction\<TMessage>, executor: core.coroutine.Executor) {
        const lock = await gate.acquire()
        try {
            const index = findIndex(callback)
            if (index < 0) {
                throw new core.IllegalStateException(
                    "Receiver.setExecutor：listener 未注册")
            }
            (entries[index] as ListenerEntry\<TMessage>).executor = executor
        } finally(e) {
            gate.release(lock)
        }
    }

    // §15：已注册 → 其 Executor；未注册 → 默认 IOExecutor（§15.2）
    pub func getExecutor(callback: core.AsyncAction\<TMessage>): core.coroutine.Executor {
        const lock = await gate.acquire()
        try {
            const index = findIndex(callback)
            if (index >= 0) {
                return (entries[index] as ListenerEntry\<TMessage>).executor
            }
            return new core.coroutine.ComputeExecutor()
        } finally(e) {
            gate.release(lock)
        }
    }

    // §13.1：内部 Reader 的 branch——状态独立于 Receiver 当前 cursor
    pub func createReader(): Reader\<TMessage> {
        return internalReader.branch()
    }

    // §13.2：只释放内部 Reader（不级联兄弟 View）。幂等；pump 协程
    // 经句柄释放的信号唤醒后走错误路径静默退出（见 pumpLoop 注释）
    pub override func dispose() {
        const lock = await gate.acquire()
        var first = false
        try {
            if (not disposed) {
                disposed = true
                first = true
                entries = core.collections.arrayOf\<ListenerEntry\<TMessage>>(4)
                entryCount = 0
            }
        } finally(e) {
            gate.release(lock)
        }
        if (first) {
            internalReader.dispose()
        }
    }

    // 起泵：经非泛型壳 AsyncShell 建冷 Task（泛型宿主内的 lambda 会被
    // 外层 GP 参数化，冷 Task spawn-into 的 bindColdBody 链无匹配——
    // playground 实证），泛型回调藏进 PumpAdapter 对象
    priv func startPump() {
        AsyncShell.start(new PumpAdapter\<TMessage>(this))
    }

    // 内部 reader 是唯一消费者；release 在队列锁内唤醒，错误出口静默停泵。
    internal async func pumpLoop() {
        try {
            while (true) {
                const item = await internalReader.next()
                if (item.isEos) { return }
                dispatch((item.item as TMessage))
            }
        } catch (e: core.IllegalStateException) {
            // reader 句柄已释放（dispose 竞态）：静默停泵
        }
    }

    // 按注册序向每个 listener 派发：临界区内快照表（队列 Mutex 可跨
    // 挂起点持有），临界区外为每个 listener 建 ListenerCall 并经
    // AsyncShell 以该 listener 的 Executor 起冷 Task（§15.1：callback
    // 只经 Rigi 层 Task + run(executor) 既有通道执行，native 不触达）
    priv func dispatch(message: TMessage) {
        const lock = await gate.acquire()
        var snapshot = core.collections.arrayOf\<ListenerEntry\<TMessage>>(0)
        var n: i32 = 0
        var i: i32 = 0
        try {
            if (disposed) { return }
            n = entryCount
            snapshot = core.collections.arrayOf\<ListenerEntry\<TMessage>>(n)
            while (i < n) {
                const entry = (entries[i] as ListenerEntry\<TMessage>)
                snapshot[i] = new ListenerEntry\<TMessage>(entry.callback, entry.executor)
                i = i + 1
            }
        } finally(e) { gate.release(lock) }
        i = 0
        while (i < n) {
            const entry = (snapshot[i] as ListenerEntry\<TMessage>)
            AsyncShell.dispatch(
                new ListenerCall\<TMessage>(entry.callback, message),
                entry.executor)
            i = i + 1
        }
    }

    priv func findIndex(callback: core.AsyncAction\<TMessage>): i32 {
        // 擦除泛型 callable 的静态类型，Place 仍按实际对象 target 比较。
        const expectedCallback = (callback as Object)
        var i: i32 = 0
        while (i < entryCount) {
            const candidate = ((entries[i] as ListenerEntry\<TMessage>).callback as Object)
            seq using(const expected = placeOf expectedCallback) {
                seq using(const actual = placeOf candidate) {
                    if (expected == actual) { return i }
                }
            }
            i = i + 1
        }
        return -1
    }

    priv func growEntries() {
        const bigger = core.collections.arrayOf\<ListenerEntry\<TMessage>>((entries.length * 2))
        var i: i32 = 0
        while (i < entryCount) {
            bigger[i] = (entries[i] as ListenerEntry\<TMessage>)
            i = i + 1
        }
        entries = bigger
    }
}

// 发送侧对象；封存、广播和读取机制全部由 MessageQueue 封装。
pub shared class Messenger\<shared TMessage with core.serialization.Serializable> implements core.IDisposable {
    priv const queue: MessageQueue\<TMessage>
    priv var receiverCache: Receiver\<TMessage>?
    priv const gate: core.coroutine.Mutex
    priv var disposed: bool

    pub init() {
        queue = new MessageQueue\<TMessage>()
        receiverCache = null
        gate = new core.coroutine.Mutex()
        disposed = false
    }

    // §17：完成点 = queue 已接受深复制快照（transport 接受点，不等待
    // 任何 reader/listener）。dispose 后 send 报队列已 sealed。
    pub async func send(message: TMessage) {
        await queue.post(message)
    }

    // 懒建 push view，将专用 Reader 的生命周期交给 Receiver。
    pub var receiver: Receiver\<TMessage> {
        pub get(_: _) {
            const lock = await gate.acquire()
            try {
              if (disposed) { throw new core.IllegalStateException("Messenger 已释放") }
              if (receiverCache == null) {
                receiverCache = new Receiver\<TMessage>(queue.createReader())
              }
              return (receiverCache as Receiver\<TMessage>)
            } finally(e) { gate.release(lock) }
        }
    }

    // 迟来 reader 从队尾起订。
    pub func createReader(): Reader\<TMessage> {
        const lock = await gate.acquire()
        try {
            if (disposed) { throw new core.IllegalStateException("Messenger 已释放") }
            return queue.createReader()
        } finally(e) { gate.release(lock) }
    }

    // 幂等封存队列；既有 Reader/Receiver 排空后得到 EOS。
    pub override func dispose() {
        const lock = await gate.acquire()
        try {
            if (not disposed) {
                queue.close()
                disposed = true
            }
        } finally(e) {
            gate.release(lock)
        }
    }
}
