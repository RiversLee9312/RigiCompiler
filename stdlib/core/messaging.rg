// Rigi 标准库：core.messaging 消息队列传输层（MW11d-C）。
// MessageQueue 是运行时内部传输原语（交接 §7），Rigi 无 static class，
// 以 pub class + 仅静态方法近似；用户代码应经 Phase D 的 Reader/
// Messenger 使用，不要直接依赖本面。
// 五个公开方法签名保持交接 §7；序列化在 Rigi 层完成（post 先
// toParcel，next 后 fromParcel）。native/VM 只搬运 Parcel。
namespace core.messaging

import core.serialization.Serializable
import core.serialization.Parcel
import core.serialization.fromParcel

// 句柄能力种类（交接 §8.1）。code 与 native/VM 矩阵常量对齐。
pub enum struct QueueHandleType {
    pub const code: i32
    priv init(_ -> code)
}[
    Reader(0),
    Owner(1),
    Sender(2)
]

// 不透明 capability token（交接 §8）。id 进程内不复用。
// 交接草稿为 struct；落地为 shared class——native 尚不支持泛型值类型
// 构造（Middleware/Mir CallVisitors/FlowBuilder 的 MW5 受控拒绝），
// class 形态语义不变（capability 由 id 承载，与对象身份无关）。
pub shared class QueueHandle\<TMessage with core.serialization.Serializable> {
    pub var id: i64
    pub var type: QueueHandleType
    pub init(_ -> id, _ -> type)
}

// next 的返回物（交接 §10）：EOS 只由 isEos 判定，不用 item==null。
// shared class：async 返回类型须共享安全（§4.5 闸门）；交接草稿的
// struct 形态同样受泛型值类型构造限制，T 的 shared 安全性在实例化点
// 重跑（§3.1.1 声明侧跳过、构造点检查）。
pub shared class QueueItem\<T> {
    pub var isEos: bool
    pub var item: T?
    pub init(_ -> isEos, _ -> item)
}

// 队列「消息可得」EventAlarm：包装 native/VM 自动复位事件句柄。
// yield 走既有 EventAlarm 握手（rigi_alarm_wait / TryAwaitTimer）。
priv shared class MessageAvailableAlarm : core.coroutine.EventAlarm {
    pub init(nativeHandle: i64) {
        handle = nativeHandle
    }
}

// 运行时内部传输原语。公开方法体在 Rigi；私有 native 为 parcel 形态 hook。
pub class MessageQueue {
    pub static func create_queue\<TMessage with core.serialization.Serializable>(): QueueHandle\<TMessage> {
        const id = mq_create()
        return new QueueHandle\<TMessage>(id, QueueHandleType.Owner)
    }

    pub static func add_queue_handle\<TMessage with core.serialization.Serializable>(
        source: QueueHandle\<TMessage>,
        type: QueueHandleType
    ): QueueHandle\<TMessage> {
        const id = mq_add(source.id, (type.code as i64))
        if (id <= (0 as i64)) {
            throw new core.IllegalStateException(mqError((id as i32)))
        }
        return new QueueHandle\<TMessage>(id, type)
    }

    pub static func release_queue_handle\<TMessage with core.serialization.Serializable>(
        handle: QueueHandle\<TMessage>
    ) {
        const code = mq_release(handle.id)
        if (code != 0) {
            throw new core.IllegalStateException(mqError(code))
        }
    }

    // 完成点 = 深复制入队可见（交接 §17）。不等待任何 reader。
    pub static async func post\<TMessage with core.serialization.Serializable>(
        handle: QueueHandle\<TMessage>,
        item: TMessage
    ) {
        const parcel = item:Serializable.toParcel()
        const code = mq_post(handle.id, parcel as Any)
        if (code != 0) {
            throw new core.IllegalStateException(mqError(code))
        }
    }

    // 有消息/EOS 立即完成；空则 yield 队列 EventAlarm 后重试（交接 §24
    // 单 outstanding next：调用边界经 mq_next_enter/exit 在 native/VM
    // 登记——Rigi 侧全局可变状态须共享安全，无共享集合可用）。
    pub static async func next\<TMessage with core.serialization.Serializable>(
        handle: QueueHandle\<TMessage>
    ): QueueItem\<TMessage> {
        const entered = mq_next_enter(handle.id)
        if (entered != 0) {
            throw new core.IllegalStateException(mqError(entered))
        }
        try {
            while (true) {
                const status = mq_try_next(handle.id)
                if (status == 1) {
                    const boxed = mq_take(handle.id)
                    const parcel = boxed as Parcel
                    const value = fromParcel\<TMessage>(parcel)
                    return new QueueItem\<TMessage>(false, value)
                }
                if (status == 2) {
                    return new QueueItem\<TMessage>(true, null)
                }
                if (status < 0) {
                    throw new core.IllegalStateException(mqError(status))
                }
                const alarm = mq_alarm(handle.id)
                if (alarm == (0 as i64)) {
                    throw new core.IllegalStateException(mqError(-1))
                }
                yield new MessageAvailableAlarm(alarm)
            }
        } finally(e) {
            mq_next_exit(handle.id)
        }
        // 防御不可达（while(true) 内全部路径已 return/throw）
        return new QueueItem\<TMessage>(true, null)
    }

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_create")
    priv static native func mq_create(): i64

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_add")
    priv static native func mq_add(sourceId: i64, typeCode: i64): i64

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_release")
    priv static native func mq_release(handleId: i64): i32

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_post")
    priv static native func mq_post(handleId: i64, parcel: Any): i32

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_try_next")
    priv static native func mq_try_next(handleId: i64): i32

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_take")
    priv static native func mq_take(handleId: i64): Any

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_alarm")
    priv static native func mq_alarm(handleId: i64): i64

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_next_enter")
    priv static native func mq_next_enter(handleId: i64): i32

    @NativeLibrary("rigi_rt")
    @NativeSymbol("mq_next_exit")
    priv static native func mq_next_exit(handleId: i64)
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

// ===== MW11d-D：Reader / Receiver / Messenger 高层 API（交接 §12–§16）=====
// 分层纪律（§20）：传输机制在 MessageQueue 五原语；cursor 管理、listener
// 派发、Executor 路由全部在本文件 Rigi 层组合。native 层绝不直接执行
// 用户 callback（§15.1 天然满足：native 只见 Parcel）。
// 单向 capability（§16.1）：Reader/Receiver 无任何派生 Sender 的 API——
// 结构性保证（无相应方法），不依赖运行时检查。

// 接收侧 canonical pull 抽象（§12）。包一条 Reader capability 句柄；
// EOS 由 next 返回的 QueueItem.isEos 判定（§10/§25）。
pub shared class Reader\<TMessage with core.serialization.Serializable> implements core.IDisposable {
    priv const handle: QueueHandle\<TMessage>
    priv var disposed: bool

    // 包装既有 Reader capability 句柄（调用方让渡句柄所有权给本对象）
    pub init(_ -> handle) {
        disposed = false
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

    // §12.2：只释放自己的 handle；幂等（重复 dispose 不再触达 native）
    pub override func dispose() {
        if (disposed) { return }
        disposed = true
        MessageQueue.release_queue_handle(handle)
    }
}

// listener 登记表项（priv：只服务 Receiver 内部；id = callback 对象身份）
priv shared class ListenerEntry\<TMessage with core.serialization.Serializable> {
    pub var id: i64
    pub var callback: core.AsyncAction\<TMessage>
    pub var executor: core.coroutine.Executor
    pub init(_ -> id, _ -> callback, _ -> executor)
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

// Reader 的 push View（§13）：内部持独立 Reader + pump 协程
//（next 循环 → listener 查找 → 按 listener 的 Executor 派发；
// EOS/dispose 停泵）。身份与路由（§14/§15）：callback 对象自身即
// identity（经 mq_object_id 原语取对象身份——playground 实证语言层
// 无 == 身份比较：lambda 隐藏类无 operator equals、toString 同位点
// 相等非身份）；默认 Executor = IOExecutor（§15.2 写死）。
pub shared class Receiver\<TMessage with core.serialization.Serializable> implements core.IDisposable {
    priv const internalReader: Reader\<TMessage>
    priv var entries: Array\<ListenerEntry\<TMessage>>
    priv var entryCount: i32
    priv var gate: i64
    priv var disposed: bool

    // 包装 Reader 并立即起泵（调用方让渡 reader 所有权给本对象）
    pub init(reader: Reader\<TMessage>) {
        internalReader = reader
        entries = core.collections.arrayOf\<ListenerEntry\<TMessage>>(4)
        entryCount = 0
        gate = mq_sync_create()
        disposed = false
        startPump()
    }

    // §14：callback 对象自身即 identity。重复注册幂等（同一 callback
    // 只登记一次，行为无歧义）。已 dispose 拒绝登记
    pub func addListener(callback: core.AsyncAction\<TMessage>) {
        const id = mq_object_id((callback as Any))
        mq_sync_acquire(gate)
        try {
            if (disposed) {
                throw new core.IllegalStateException(
                    "Receiver.addListener：Receiver 已 dispose")
            }
            if (findIndex(id) >= 0) { return }
            if (entryCount == entries.length) { growEntries() }
            entries[entryCount] = new ListenerEntry\<TMessage>(
                id, callback, new core.coroutine.IOExecutor())
            entryCount = entryCount + 1
        } finally(e) {
            mq_sync_release(gate)
        }
    }

    // §14：按 callback 身份摘除；未注册为无操作（幂等）
    pub func removeListener(callback: core.AsyncAction\<TMessage>) {
        const id = mq_object_id((callback as Any))
        mq_sync_acquire(gate)
        try {
            const index = findIndex(id)
            if (index < 0) { return }
            var i = index
            while (i < (entryCount - 1)) {
                entries[i] = (entries[(i + 1)] as ListenerEntry\<TMessage>)
                i = i + 1
            }
            entryCount = entryCount - 1
        } finally(e) {
            mq_sync_release(gate)
        }
    }

    // §15：listener 级 Executor 路由。未注册 callback 拒绝（无歧义）
    pub func setExecutor(callback: core.AsyncAction\<TMessage>, executor: core.coroutine.Executor) {
        const id = mq_object_id((callback as Any))
        mq_sync_acquire(gate)
        try {
            const index = findIndex(id)
            if (index < 0) {
                throw new core.IllegalStateException(
                    "Receiver.setExecutor：listener 未注册")
            }
            (entries[index] as ListenerEntry\<TMessage>).executor = executor
        } finally(e) {
            mq_sync_release(gate)
        }
    }

    // §15：已注册 → 其 Executor；未注册 → 默认 IOExecutor（§15.2）
    pub func getExecutor(callback: core.AsyncAction\<TMessage>): core.coroutine.Executor {
        const id = mq_object_id((callback as Any))
        mq_sync_acquire(gate)
        try {
            const index = findIndex(id)
            if (index >= 0) {
                return (entries[index] as ListenerEntry\<TMessage>).executor
            }
            return new core.coroutine.IOExecutor()
        } finally(e) {
            mq_sync_release(gate)
        }
    }

    // §13.1：内部 Reader 的 branch——状态独立于 Receiver 当前 cursor
    pub func createReader(): Reader\<TMessage> {
        return internalReader.branch()
    }

    // §13.2：只释放内部 Reader（不级联兄弟 View）。幂等；pump 协程
    // 经句柄释放的信号唤醒后走错误路径静默退出（见 pumpLoop 注释）
    pub override func dispose() {
        mq_sync_acquire(gate)
        var first = false
        try {
            if (not disposed) {
                disposed = true
                first = true
            }
        } finally(e) {
            mq_sync_release(gate)
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

    // pump 协程（§13）：next 循环 → EOS 停泵；dispose 与挂起 next 的
    // 竞态由 native「先信号后销毁」保证唤醒——醒来后 try_next/enter 报
    // 「句柄已释放」IllegalStateException，静默停泵。pump 是内部 reader
    // 的唯一消费者（单 outstanding next 天然满足）。
    // internal：PumpAdapter 的跨类回调通道（同模块可见，非公共契约）
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

    // 按注册序向每个 listener 派发：临界区内快照表（同步 Mutex 不得跨
    // 挂起点持有），临界区外为每个 listener 建 ListenerCall 并经
    // AsyncShell 以该 listener 的 Executor 起冷 Task（§15.1：callback
    // 只经 Rigi 层 Task + run(executor) 既有通道执行，native 不触达）
    priv func dispatch(message: TMessage) {
        mq_sync_acquire(gate)
        const snapshot = core.collections.arrayOf\<ListenerEntry\<TMessage>>(entryCount)
        var i: i32 = 0
        while (i < entryCount) {
            snapshot[i] = (entries[i] as ListenerEntry\<TMessage>)
            i = i + 1
        }
        const n = entryCount
        mq_sync_release(gate)
        i = 0
        while (i < n) {
            const entry = (snapshot[i] as ListenerEntry\<TMessage>)
            AsyncShell.dispatch(
                new ListenerCall\<TMessage>(entry.callback, message),
                entry.executor)
            i = i + 1
        }
    }

    priv func findIndex(id: i64): i32 {
        var i: i32 = 0
        while (i < entryCount) {
            if ((entries[i] as ListenerEntry\<TMessage>).id == id) { return i }
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
    priv var gate: i64
    priv var disposed: bool

    pub init() {
        owner = MessageQueue.create_queue\<TMessage>()
        sender = MessageQueue.add_queue_handle\<TMessage>(owner, QueueHandleType.Sender)
        receiverCache = null
        gate = mq_sync_create()
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
            mq_sync_acquire(gate)
            if (receiverCache == null) {
                // 直接包 Owner 派生的新 Reader（所有权让渡给 Receiver）
                // ——不能 createReader().createReceiver()：中介 Reader
                // 无人 dispose 会把句柄/对象漏掉（memtrack 实证）
                receiverCache = new Receiver\<TMessage>(this.createReader())
            }
            const result = (receiverCache as Receiver\<TMessage>)
            mq_sync_release(gate)
            return result
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
        mq_sync_acquire(gate)
        var first = false
        try {
            if (not disposed) {
                disposed = true
                first = true
            }
        } finally(e) {
            mq_sync_release(gate)
        }
        if (first) {
            MessageQueue.release_queue_handle(sender)
            MessageQueue.release_queue_handle(owner)
        }
    }
}

// ===== MW11d-D 内部原语（文件级 priv，不进公共面）=====
// native 同步 Mutex（§17.4）：Receiver/Messenger 内部表跨 lane 一致性
//（listener 在 IO/Compute lane 跑、用户在 Main 调 addListener）。
// coroutine.rg 的同符号声明是文件级 priv，按既有先例在本文件重声明
//（同 (lib, symbol) 映射同一 C 函数/VM hook；纪律不变：不得跨挂起点
// 持有，与语言级异步 Mutex 严格区分）。
@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_create")
priv native func mq_sync_create(): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_acquire")
priv native func mq_sync_acquire(mutex: i64)

@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_release")
priv native func mq_sync_release(mutex: i64)

// 对象身份原语（§14）：listener 身份键。playground 实证语言层无身份
// 比较通道（lambda 隐藏类无 operator equals；toString 同位点相等，非
// 身份）——身份是机制（mechanism）不是策略，故下沉为 rigi_rt/VM 原语
//（rigi_rt/stringfmt.c rigi_object_id：Any 槽 payload 即对象身份）
@NativeLibrary("rigi_rt")
@NativeSymbol("object_id")
priv native func mq_object_id(value: Any): i64
