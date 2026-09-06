// Rigi 标准库：纯语言消息队列；安全 AtomicList 保存消息，Mutex 统一线性化。
// MessageQueue 保留五 API，Reader/Receiver/Messenger 组合 capability 与 Task。
namespace core.messaging

import core.serialization.Serializable

// capability 种类；枚举不能由用户构造任意 code。
pub enum struct QueueHandleType {
    pub const code: i32
    priv init(_ -> code)
}[
    Reader(0),
    Owner(1),
    Sender(2)
]

// 直接对象 capability；所有可变状态由同一队列 Mutex 保护。
// 释放后清除 queue 链接，外界保留旧 capability 不会继续钉住消息日志。
pub shared class QueueHandle\<TCapability with core.serialization.Serializable> {
    priv const gate: core.coroutine.Mutex
    priv var queue: QueueState\<TCapability>?
    priv const kind: QueueHandleType
    priv const reader: QueueReaderState\<TCapability>?
    priv init(state: QueueState\<TCapability>, type: QueueHandleType, cursor: QueueReaderState\<TCapability>?) {
        gate = state.gate
        queue = state
        kind = type
        reader = cursor
    }
    pub static func create_queue\<TMessage with core.serialization.Serializable>(): QueueHandle\<TMessage> {
        return new QueueHandle\<TMessage>(new QueueState\<TMessage>(), QueueHandleType.Owner, null)
    }
    pub static func add_queue_handle\<TMessage with core.serialization.Serializable>(
        source: QueueHandle\<TMessage>, type: QueueHandleType
    ): QueueHandle\<TMessage> {
        const lock = await source.gate.acquire()
        try {
            if (source.queue == null) { throw new core.IllegalStateException(mqError(-1)) }
            const q = (source.queue as QueueState\<TMessage>)
            if (type.code == 1) { throw new core.IllegalStateException(mqError(-3)) }
            if (type.code == 2) {
                if (source.kind.code == 0) { throw new core.IllegalStateException(mqError(-4)) }
                if (q.sealed()) { throw new core.IllegalStateException(mqError(-9)) }
                q.senders = (q.senders + (1 as i64))
                return new QueueHandle\<TMessage>(q, type, null)
            }
            if (type.code != 0) { throw new core.IllegalStateException(mqError(-1)) }
            if (source.kind.code == 2) { throw new core.IllegalStateException(mqError(-5)) }
            const reader = new QueueReaderState\<TMessage>(q.tail)
            reader.segment = q.lastSegment()
            reader.following = q.readers
            q.readers = reader
            return new QueueHandle\<TMessage>(q, type, reader)
        } finally(e) { source.gate.release(lock) }
    }
    pub static func release_queue_handle\<TMessage with core.serialization.Serializable>(handle: QueueHandle\<TMessage>) {
        const lock = await handle.gate.acquire()
        try {
            if (handle.queue == null) { throw new core.IllegalStateException(mqError(-2)) }
            const q = (handle.queue as QueueState\<TMessage>)
            handle.queue = null
            if (handle.kind.code == 1) { q.ownerAlive = false }
            if (handle.kind.code == 2) { q.senders = (q.senders - (1 as i64)) }
            if (handle.reader != null) { q.removeReader((handle.reader as QueueReaderState\<TMessage>)) }
            if (q.sealed()) { q.signal() }
            q.reclaim()
        } finally(e) { handle.gate.release(lock) }
    }
    pub static async func post\<TMessage with core.serialization.Serializable>(
        handle: QueueHandle\<TMessage>, item: TMessage
    ) {
        // 保持旧复制异常顺序，即使没有 reader 也必须验证默认拒环。
        const copy = core.serialization.deepCopy\<TMessage>(item)
        const lock = await handle.gate.acquire()
        try {
            if (handle.queue == null) { throw new core.IllegalStateException(mqError(-1)) }
            if (handle.kind.code != 2) { throw new core.IllegalStateException(mqError(-6)) }
            const q = (handle.queue as QueueState\<TMessage>)
            if (q.sealed()) { throw new core.IllegalStateException(mqError(-8)) }
            if (q.readers != null) { q.append(copy) }
            q.tail = (q.tail + (1 as i64))
            if (q.readers == null) {
                q.head = q.tail
                q.base = q.tail
            }
            q.signal()
        } finally(e) { handle.gate.release(lock) }
    }
    pub static async func next\<TMessage with core.serialization.Serializable>(
        handle: QueueHandle\<TMessage>
    ): QueueItem\<TMessage> {
        var reader: QueueReaderState\<TMessage>? = null
        const entered = await handle.gate.acquire()
        try {
            if (handle.queue == null) { throw new core.IllegalStateException(mqError(-1)) }
            if (handle.kind.code != 0) { throw new core.IllegalStateException(mqError(-7)) }
            reader = (handle.reader as QueueReaderState\<TMessage>)
            if ((reader as QueueReaderState\<TMessage>).inNext) { throw new core.IllegalStateException(mqError(-10)) }
            (reader as QueueReaderState\<TMessage>).inNext = true
        } finally(e) { handle.gate.release(entered) }
        const r = (reader as QueueReaderState\<TMessage>)
        try {
            while (true) {
                const lock = await handle.gate.acquire()
                try {
                    if (handle.queue == null) { throw new core.IllegalStateException(mqError(-1)) }
                    const q = (handle.queue as QueueState\<TMessage>)
                    if (r.cursor < q.tail) {
                        const value = q.read(r)
                        r.cursor = (r.cursor + (1 as i64))
                        q.reclaim()
                        return new QueueItem\<TMessage>(false, value)
                    }
                    if (q.sealed()) { return new QueueItem\<TMessage>(true, null) }
                    // 先占空闲 wake，再解队列锁。signal-before-await 会把锁变空闲；
                    // 否则第二次 acquire 排队。token 只释放一次，没有丢唤醒窗口。
                    r.waitToken = await r.wake.acquire()
                } finally(e) { handle.gate.release(lock) }
                const wakeLock = await r.wake.acquire()
                try {
                    // 醒来只重试队列状态；release 与 post 都可以唤醒。
                } finally(e) { r.wake.release(wakeLock) }
            }
        } finally(e) {
            const lock = await handle.gate.acquire()
            try {
                r.signal()
                r.inNext = false
            } finally(e) { handle.gate.release(lock) }
        }
        return new QueueItem\<TMessage>(true, null)
    }
}

pub shared class QueueItem\<T> {
    pub var isEos: bool
    pub var item: T?
    pub init(_ -> isEos, _ -> item)
}

// reader 记录不反向引用队列。空读时使用固定 wake gate，避免为每条消息
// 创建运行时事件；waitToken 只能在队列锁内设置、摘取和释放。
priv shared class QueueReaderState\<TMessage with core.serialization.Serializable> {
    pub var cursor: i64
    pub var segment: QueueLogSegment\<TMessage>?
    pub var inNext: bool
    pub const wake: core.coroutine.Mutex
    pub var waitToken: core.coroutine.Mutex.Lock?
    pub var following: QueueReaderState\<TMessage>?
    pub init(position: i64) {
        cursor = position
        segment = null
        inNext = false
        wake = new core.coroutine.Mutex()
        waitToken = null
        following = null
    }
    pub func signal() {
        if (waitToken != null) {
            const token = (waitToken as core.coroutine.Mutex.Lock)
            waitToken = null
            wake.release(token)
        }
    }
}

// 有界消息段；每段复用安全 AtomicList。日志不把整个积压放进一个
// 巨型数组，避免临时引用释放反复把同一超阈数组计入 GC 候选债务。
priv shared class QueueLogSegment\<TMessage with core.serialization.Serializable> {
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

priv shared class QueueState\<TMessage with core.serialization.Serializable> {
    pub const gate: core.coroutine.Mutex
    priv var first: QueueLogSegment\<TMessage>?
    priv var last: QueueLogSegment\<TMessage>?
    pub var base: i64
    pub var head: i64
    pub var tail: i64
    pub var ownerAlive: bool
    pub var senders: i64
    pub var readers: QueueReaderState\<TMessage>?
    pub init() {
        gate = new core.coroutine.Mutex()
        first = null
        last = null
        base = (0 as i64)
        head = (0 as i64)
        tail = (0 as i64)
        ownerAlive = true
        senders = (0 as i64)
        readers = null
    }
    pub func sealed(): bool {
        return ((not ownerAlive) and (senders == (0 as i64)))
    }
    pub func lastSegment(): QueueLogSegment\<TMessage>? { return last }
    // 所有段链接、段内计数和 reader 缓存都由外层队列 gate 保护。
    pub func append(item: TMessage) {
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
    pub func read(reader: QueueReaderState\<TMessage>): TMessage? {
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
    pub func signal() {
        var current = readers
        while (current != null) {
            const r = (current as QueueReaderState\<TMessage>)
            r.signal()
            current = r.following
        }
    }
    // 最慢 live reader 决定绝对水位；整段消费完立即断链。
    // 先更新 reader 缓存，释放的 capability 或旧段都不钉住历史前缀。
    pub func reclaim() {
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
    pub func removeReader(target: QueueReaderState\<TMessage>) {
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

// 五 API 保留原有签名；capability 自身封装私有状态和校验。
pub class MessageQueue {
    pub static func create_queue\<TMessage with core.serialization.Serializable>(): QueueHandle\<TMessage> {
        return QueueHandle.create_queue\<TMessage>()
    }
    pub static func add_queue_handle\<TMessage with core.serialization.Serializable>(source: QueueHandle\<TMessage>, type: QueueHandleType): QueueHandle\<TMessage> {
        return QueueHandle.add_queue_handle\<TMessage>(source, type)
    }
    pub static func release_queue_handle\<TMessage with core.serialization.Serializable>(handle: QueueHandle\<TMessage>) {
        QueueHandle.release_queue_handle\<TMessage>(handle)
    }
    pub static async func post\<TMessage with core.serialization.Serializable>(handle: QueueHandle\<TMessage>, item: TMessage) {
        await QueueHandle.post\<TMessage>(handle, item)
    }
    pub static async func next\<TMessage with core.serialization.Serializable>(handle: QueueHandle\<TMessage>): QueueItem\<TMessage> {
        return await QueueHandle.next\<TMessage>(handle)
    }
}

// 违规消息文本：Rigi 包装层抛 IllegalStateException，双端同文。
priv func mqError(code: i32): String {
    if (code == -1) {
        return "MessageQueue: 句柄已释放或不存在"
    }
    if (code == -2) {
        return "MessageQueue: 句柄重复释放"
    }
    if (code == -3) {
        return "MessageQueue: 不能派生 Owner"
    }
    if (code == -4) {
        return "MessageQueue: 不能从该句柄派生 Sender"
    }
    if (code == -5) {
        return "MessageQueue: 不能从该句柄派生 Reader"
    }
    if (code == -6) {
        return "MessageQueue: 该句柄不能 post"
    }
    if (code == -7) {
        return "MessageQueue: 该句柄不能 next"
    }
    if (code == -8) {
        return "MessageQueue: 队列已 sealed，不能 post"
    }
    if (code == -9) {
        return "MessageQueue: 队列已 sealed，不能派生 Sender"
    }
    if (code == -10) {
        return "MessageQueue: 同一 Reader 同时只能有一个 outstanding next"
    }
    return "MessageQueue: 非法操作"
}

// ===== Reader / Receiver / Messenger 高层 API =====
// 分层纪律（§20）：传输机制在 MessageQueue 五原语；cursor 管理、listener
// 派发、Executor 路由全部在本文件 Rigi 层组合，callback 经 Task.run 执行。
// 单向 capability（§16.1）：Reader/Receiver 无任何派生 Sender 的 API——
// 结构性保证（无相应方法），不依赖运行时检查。

// 接收侧 canonical pull 抽象（§12）。包一条 Reader capability 句柄；
// EOS 由 next 返回的 QueueItem.isEos 判定（§10/§25）。
pub shared class Reader\<TMessage with core.serialization.Serializable> implements core.IDisposable {
    priv const handle: QueueHandle\<TMessage>
    priv const disposeGate: core.coroutine.Mutex
    priv var disposed: bool

    // 包装既有 Reader capability 句柄（调用方让渡句柄所有权给本对象）
    pub init(_ -> handle) {
        disposed = false
        disposeGate = new core.coroutine.Mutex()
    }

    // 下一条消息：转发 MessageQueue.next（单 outstanding 纪律见 §24）
    pub async func next(): QueueItem\<TMessage> {
        return (await MessageQueue.next(handle))
    }

    // 同队列新独立 cursor（§12.1）：Reader 句柄派生新 Reader 句柄，
    // 不复用源 cursor——新 Reader 从队尾起订（迟来 reader 语义）
    pub func branch(): Reader\<TMessage> {
        return new Reader\<TMessage>(
            MessageQueue.add_queue_handle(handle, QueueHandleType.Reader))
    }

    // §13.1：Receiver 基于 branch 出的新 Reader（不消费源 cursor）
    pub func createReceiver(): Receiver\<TMessage> {
        return new Receiver\<TMessage>(this.branch())
    }

    // §12.2：只释放自己的 handle；幂等（重复 dispose 不再释放 capability）
    pub override func dispose() {
        const lock = await disposeGate.acquire()
        try {
            if (disposed) { return }
            disposed = true
            MessageQueue.release_queue_handle(handle)
        } finally(e) { disposeGate.release(lock) }
    }
}

// listener 登记表项（priv：只服务 Receiver 内部；id = callback 对象身份）
priv shared class ListenerEntry\<TMessage with core.serialization.Serializable> {
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

// 非泛型冷 Task 壳：start 在当前 lane 起泵；dispatch 在指定 Executor
// 起单次调用（§15.1 派发通道 = 冷 Task + run(executor)）
priv shared class AsyncShell {
    pub static func start(invoker: AsyncInvoker) {
        const t = new core.coroutine.Task(new InvokeAction(invoker))
        t.run()
    }

    pub static func dispatch(invoker: AsyncInvoker, executor: core.coroutine.Executor) {
        const t = new core.coroutine.Task(new InvokeAction(invoker))
        t.run(executor)
    }
}

// Receiver pump 的泛型适配：把 invoke 桥接到 Receiver.pumpLoop
//（pumpLoop 自身是 async——eager 热 Task 在泵 lane 起跑）
priv shared class PumpAdapter\<TMessage with core.serialization.Serializable> : AsyncInvoker {
    priv const target: Receiver\<TMessage>
    pub init(_ -> target)

    pub override func invoke() {
        target.pumpLoop()
    }
}

// 单次 listener 派发的泛型适配：invoke = 以消息调 callback。
// message 字段为裸 GP——shared class 的 GP 字段声明侧跳过、构造点
// 重跑共享安全闭包检查（§3.1.1，QueueItem\<T> 同例）
priv shared class ListenerCall\<TMessage with core.serialization.Serializable> : AsyncInvoker {
    priv const callback: core.AsyncAction\<TMessage>
    priv const message: TMessage
    pub init(_ -> callback, _ -> message)

    pub override func invoke() {
        callback(message)
    }
}

// Reader 的 push View：独立 cursor、listener 对象身份与默认 IOExecutor。
pub shared class Receiver\<TMessage with core.serialization.Serializable> implements core.IDisposable {
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
                callback, new core.coroutine.IOExecutor())
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
            return new core.coroutine.IOExecutor()
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

// 发送 capability 的语言层对象（§16）：持 Owner + 内部 Sender。
// 无 receive→send 逆升（§16.1 结构性保证）
pub shared class Messenger\<TMessage with core.serialization.Serializable> implements core.IDisposable {
    priv const owner: QueueHandle\<TMessage>
    priv const sender: QueueHandle\<TMessage>
    priv var receiverCache: Receiver\<TMessage>?
    priv const gate: core.coroutine.Mutex
    priv var disposed: bool

    pub init() {
        owner = MessageQueue.create_queue\<TMessage>()
        sender = MessageQueue.add_queue_handle\<TMessage>(owner, QueueHandleType.Sender)
        receiverCache = null
        gate = new core.coroutine.Mutex()
        disposed = false
    }

    // §17：完成点 = queue 已接受深复制快照（transport 接受点，不等待
    // 任何 reader/listener）。dispose 后 send 报「句柄已释放」
    pub async func send(message: TMessage) {
        await MessageQueue.post(sender, message)
    }

    // §16：懒建——Owner 派生 Reader → createReceiver
    pub var receiver: Receiver\<TMessage> {
        pub get(_: _) {
            const lock = await gate.acquire()
            try {
              if (receiverCache == null) {
                // 直接包 Owner 派生的新 Reader（所有权让渡给 Receiver）
                // ——不能 createReader().createReceiver()：中介 Reader
                // 无人 dispose 会把句柄/对象漏掉（memtrack 实证）
                receiverCache = new Receiver\<TMessage>(this.createReader())
              }
              return (receiverCache as Receiver\<TMessage>)
            } finally(e) { gate.release(lock) }
        }
    }

    // §16：Owner 派生 Reader 包装（迟来 reader 从队尾起订）
    pub func createReader(): Reader\<TMessage> {
        return new Reader\<TMessage>(
            MessageQueue.add_queue_handle\<TMessage>(owner, QueueHandleType.Reader))
    }

    // §16：释放 Sender + Owner → 生产侧 sealed → readers drain 后 EOS。
    // 幂等；不级联 dispose 已派生的 Reader/Receiver（它们有自己的句柄）
    pub override func dispose() {
        const lock = await gate.acquire()
        var first = false
        try {
            if (not disposed) {
                disposed = true
                first = true
            }
        } finally(e) {
            gate.release(lock)
        }
        if (first) {
            MessageQueue.release_queue_handle(sender)
            MessageQueue.release_queue_handle(owner)
        }
    }
}
