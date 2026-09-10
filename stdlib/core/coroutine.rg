// Rigi 标准库：core.coroutine 协程类型面（MW11c，SYNTAX §4.5/§7.5、
// RUNTIME §17–§20）。
// MW11c 架构转向已落地：Task/Dispatcher 调度逻辑在 Rigi 世界（§17.4），
// native 只留 Worker/协程句柄/定时器/同步锁/TLS/时钟原语。VM 与 native
// 共享本文件：冷 Task / TaskState / Executor 换绑 / Timer/`sleep` /
// 语言级 Mutex（判定在 tryEnter/releaseNext；挂起由 VM 方法 hook 或
// native CoroutineSplit 改写 enter）均已接线。
//   - Task/Task\<TReturn> 是具体 shared class（§18.2）：构造只存 body
//     不执行（冷 Task，§18.4）；同名不同元数合法共存（SYNTAX §15.3）。
//   - state 访问器：pub get + priv set——写通道是运行时内部约定
//     （markSuspended/markRunnable/complete/fail；用户侧只读）。
//   - executor 访问器：未启动读写启动预设、已启动写入为换绑（§18.4）。
//   - TaskState 六 case 投影（§18.2：Running 并入 Runnable）；isRunning
//     只读字段仅 Runnable/Suspended 为 true。
//   - Mutex 是语言级异步互斥锁（§19.6）：Lock 为嵌套持有令牌（§9.5），
//     无公开构造入口；runSynchronously 两变体的 try/finally 为 Rigi
//     真体（await body() = invoke.indirect async $$call）；acquire 经
//     enter 挂起、release 经 releaseNext + publish。
//   - Timer : EventAlarm 构造即排程（§19.5）；RepeatOption 三 case。
//   - Executor 基类保持 abstract；三个内置 Executor 是 pub shared
//     singleton class（§20.1），Worker 懒建。
//   - PollingAlarm.isReady 是同步探测方法（§19.2）；EventAlarm 由事件
//     源 callback 通知（§19.3）。
//   - CoroutineLocal\<TValue\> 是 per-coroutine 上下文的唯一机制
//     （§20.2）：具体 shared class；实例作进程稳定键（const id =
//     new CoroutineLocal\<T>()）；作用域绑定 withValue；get() 返回
//     TValue?（可选构造默认值；无绑定且无默认 → null）；spawn/
//     冷启动默认继承调用方当前有效绑定。
//   - sleep 是 Rigi 包装（§19.4）：SleepAlarm 构造即排程，响铃经
//     native 定时器记录 + 导出符号 rigi_dispatch_publish 重发布。
// native 原语面（§17.4，rigi_rt，rigi_ 前缀）：
//   Worker 创建/销毁/入队唤醒/park、协程句柄 create/resume/destroy/
//   lane/current、定时器创建、EventAlarm waiter 登记（alarm_wait）、
//   用户 EventAlarm 默认底座（event_create_sticky/signal，L8）、
//   PollingAlarm 轮询状态（poll_arm/pending/schedule/clear）、同步
//   Mutex（仅供 Dispatcher 内部队列一致性，不得跨挂起点持有，与语言级
//   异步 Mutex 严格区分）、TLS 当前上下文、rigi_time_now 时钟。
//   CoroutineLocal 栈（coro_local_push/pop/get/inherit）、
//   私有 native 桥接以 i64 承载 token/帧/fn；shared 状态持 Carrige，
//   local CoroutineHandle 经 IDisposable 管理 NativeRc 强引用。
namespace core.coroutine

// CoroutineHandle 是 NativeRcHandle 的首个落地实现：操作面只存在于
// 当前 Coroutine 的 local object；CoroutineCarrige 是无操作能力的
// shared 弱票据。任何 shared Rigi 状态只保存 Carrige，不保存裸 token。
priv interface ICoroutineHandle {
    func lane(): i32
    func setLane(lane: i32)
    func resume(): i64
    func inheritLocals()
    func armPoll()
    func pollPending(): bool
    func schedulePoll()
    func clearPoll()
    func retire()
}

priv class CoroutineHandle : core.native.NativeRcHandle\<CoroutineCarrige> implements ICoroutineHandle {
    priv var token: i64

    internal init(_ -> token)

    pub override func carry(): CoroutineCarrige {
        if (token == (0 as i64)) {
            throw new core.IllegalStateException("CoroutineHandle 已释放")
        }
        return new CoroutineCarrige(token)
    }

    pub override func dispose() {
        if (token != (0 as i64)) {
            const held = token
            token = (0 as i64)
            rigi_native_rc_release(held)
        }
    }

    pub override func lane(): i32 { return rigi_coroutine_get_lane(token) }
    pub override func setLane(lane: i32) { rigi_coroutine_set_lane(token, lane) }
    pub override func resume(): i64 { return rigi_coroutine_resume(token) }
    pub override func inheritLocals() { rigi_coro_local_inherit(token) }
    pub override func armPoll() { rigi_poll_arm(token) }
    pub override func pollPending(): bool { return (rigi_poll_pending(token) != 0) }
    pub override func schedulePoll() { rigi_poll_schedule(token) }
    pub override func clearPoll() { rigi_poll_clear(token) }

    // 释放 Dispatcher/执行引擎持有的初始强引用；本 Handle 自己的强引用
    // 仍由 dispose() 交还，因而 retire 后本调用段内 payload 仍然有效。
    pub override func retire() { rigi_coroutine_destroy(token) }
}

priv shared class CoroutineCarrige implements core.native.ICarrige {
    priv const token: i64
    internal init(_ -> token)

    pub override func retain(): Any? {
        if (rigi_native_rc_retain(token) == 0) { return null }
        return new CoroutineHandle(token) as Any
    }
}

// 唯一的 Carrige→Handle 恢复点。调用者必须检查 null；成功结果由
// seq using 接管，禁止显式 retain/release 配对散落在业务逻辑中。
priv func retainCoroutine(carrige: CoroutineCarrige): CoroutineHandle? {
    const retained = carrige.retain()
    if (retained == null) { return null }
    return retained as CoroutineHandle
}

pub shared class Task {
    priv const body: core.AsyncAction

    // 冷 Task：只存 body 不执行（§18.4）；gate 在构造即建（启动/await
    // 竞争临界区前置，attachRuntime 对热 Task 的懒建通道保留兜底）
    pub init(_ -> body) {
        gate = rigi_sync_mutex_create()
    }

    pub var state: TaskState {
        pub get
        // 写通道为运行时内部约定：用户不可写；VM/Dispatcher 经内部通道
        // 直写 backing（MW11c 棒4 落地）
        priv set
    } = .Created

    pub var executor: Executor? {
        pub get
        // 未启动写入 = 启动预设；已启动写入 = 换绑（§18.4，下一恢复点
        // 生效）：桥同步 cohandle lane 槽（native 半场；VM 侧
        // set_lane hook 为空操作——VM 直接读 executor backing 换算 lane）
        pub set(value: _) {
            if (handle != null) {
                const lane = laneOfExecutor(value)
                if (lane >= 0) {
                    const retained = retainCoroutine(handle as CoroutineCarrige)
                    if (retained != null) {
                        seq using(const nativeHandle = retained as CoroutineHandle) {
                            nativeHandle.setLane(lane)
                        }
                    }
                }
            }
        }
    }

    // 以「executor 预设值 ?? 当前 Executor」启动（§18.4）
    pub func run() {
        startCold()
    }

    // 先设预设再启动（§18.4）
    pub func run(executor: Executor) {
        this.executor = executor
        startCold()
    }

    // 启动协作壳（§18.4）：VM 侧由方法 hook 接管（一次性判定 +
    // spawn-into 建协程绑定本对象 + 发布到「预设 ?? 当前 Executor」，
    // 临界区持 gate；重复启动抛 core.IllegalStateException）。
    // native 半场真体（棒5a）：gate 临界区内 tryStart + spawn-into
    //（复用 coldHandle：具体闭包在构造工厂预建；不透明 AsyncAction/
    // AsyncFunc 槽则启动时 bindColdBody 动态 spawn-into），临界区外
    // noteSpawn + publish
    priv func startCold() {
        rigi_sync_mutex_acquire(gate)
        if (tryStart() != 0) {
            rigi_sync_mutex_release(gate)
            throw new core.IllegalStateException(
                "Task 只允许启动一次：对已完成启动的 Task 调用 run")
        }
        spawnIntoLocked()
        const started = coldHandle as CoroutineCarrige
        rigi_sync_mutex_release(gate)
        const dispatcher = new Dispatcher()
        dispatcher.noteSpawn()
        dispatcher.publish(started)
    }

    // spawn-into 临界区段（棒5a native 半场；桥持 gate 调用）：
    // coldHandle==0（不透明 body，构造未预建句柄）→ bindColdBody
    // 按实际闭包类动态建 frame/句柄；随后从启动方当前协程继承
    // CoroutineLocal 有效顶（冷构造 ≠ 启动，继承在此刻而非
    // coroutine_create）；lane = 预设 ?? 当前 Worker lane（§18.4）
    // → cohandle 置 lane → attachRuntime 挂句柄。
    // await 冷启动分支（生成代码）在同一 gate 临界区内直调本方法；
    // VM 不走本方法（VmDispatch.SpawnInto 直建协程并在引擎侧继承）
    priv func spawnIntoLocked() {
        if (coldHandle == null) {
            bindColdBody()
        }
        const carrige = coldHandle as CoroutineCarrige
        const retained = retainCoroutine(carrige)
        if (retained == null) {
            throw new core.IllegalStateException("冷 Task 的 CoroutineHandle 已释放")
        }
        seq using(const nativeHandle = retained as CoroutineHandle) {
            nativeHandle.inheritLocals()
            var lane = laneOfExecutor(executor)
            if (lane < 0) { lane = new Dispatcher().laneOfCurrent() }
            nativeHandle.setLane(lane)
        }
        attachRuntime(carrige)
    }

    // 不透明冷 body 的动态 spawn-into（CoroutineSplit 在 MIR 层改写为
    // 模块内 0 参 async $$call 的 type.is 链 + $mw.bindcold.* 助手）。
    // 默认体：无匹配闭包时抛——split 未改写或全不中走这里
    priv func bindColdBody() {
        throw new core.IllegalStateException(
            "冷 Task body 无法 spawn-into：无匹配闭包")
    }

    // ===== MW11c 棒4a 运行时通道（VM/native 桥专用，§17.4）=====
    // 以下成员不进公共面：句柄/临界区/waiter 链是 Task 生命周期的
    // Rigi 世界表达。临界区纪律：registerWaiter/complete/fail/cancel
    // 的调用方（运行时桥）必须持有 this.gate 的 sync mutex 临界区——
    // 「登记 waiter」与「挂起协程」须在同一临界区内原子完成（挂起机制
    // 属执行引擎，桥在临界区内代为执行），对齐旧 VM VmTask._gate 不变量。
    priv var handle: CoroutineCarrige? = null
    priv var gate: i64 = (0 as i64)
    // 状态机的内部判定投影（enum struct 等值比较开销/形态未定型，
    // 临界区内以 i32 快读；与 pub state 恒同步迁移）
    priv var stateCode: i32 = 0
    priv var observed: bool = false
    priv var waiters: CoroutineCarrigeQueue? = null
    // ===== MW11c 棒5a native 半场字段 =====
    // coldHandle：构造重写（$mw.coldtask.* 工厂）预建的协程句柄，
    // spawn-into 复用（Task↔协程 1:1，§18.4）；VM 不用（VM 直建）
    priv var coldHandle: CoroutineCarrige? = null
    // failureNodeId：native 失败注册表节点 id（0 = 未登记/已摘除——
    // 异常由 native 隐藏 refMap 槽持有，不增加 shared class 的 local
    // Exception 语言字段；VM 半场恒 0，registerWaiter 跳过 native 标记）。
    // await 仅放报告引用；Task 隐藏拥有槽保留到对象回收
    priv var failureNodeId: i64 = (0 as i64)

    // 热 Task 运行时附着（eager spawn，§18.1）：运行时桥建对象后调用，
    // 先于任何 resume/终态路径（gate 是后续一切临界区的前置；冷 Task
    // 已在构造时建好 gate，懒建仅兜底）
    priv func attachRuntime(coroutine: CoroutineCarrige) {
        handle = coroutine
        if (gate == (0 as i64)) { gate = rigi_sync_mutex_create() }
        state = .Runnable
        stateCode = 1
    }

    priv func attachRuntimeNative(coroutine: i64) {
        attachRuntime(new CoroutineCarrige(coroutine))
    }

    // 冷启动一次性判定（§18.4；桥持 gate 临界区内调用）：0 = 本次启动
    // 成立（已投影 Runnable，桥随后 spawn-into）；1 = 已启动（run 通道
    // 抛 IllegalStateException；await 通道竞争输家按普通 waiter 等待）
    priv func tryStart(): i32 {
        if (stateCode != 0) { return 1 }
        state = .Runnable
        stateCode = 1
        return 0
    }

    // TaskState 投影迁移（§18.2；桥在挂起点/发布点调用，终态三通道
    // complete/fail/cancel 不受影响——它们只在引擎确认终态后触发）
    priv func markSuspended() {
        state = .Suspended
        stateCode = 2
    }

    priv func markRunnable() {
        state = .Runnable
        stateCode = 1
    }

    // await 入口决策（§18.3）：终态快读 / 未终态登记 waiter。
    // 返回 0=已登记（桥把当前协程在同一临界区内挂起）；1=已成功；
    // 2=已失败；3=已取消
    priv func registerWaiter(waiter: i64): i32 {
        observed = true
        if (stateCode == 3) { return 1 }
        if (stateCode == 4) {
            // 观察即放掉 native 未观察报告引用（native 半场；VM
            // 侧 failureNodeId 恒 0 跳过）。先登记 waiter 再失败时
            // observed 已为 true，仍须 drop
            if (failureNodeId != (0 as i64)) {
                rigi_failure_drop(failureNodeId)
                // 不清零：await 失败快路径还要按节点 id 读异常
            }
            return 2
        }
        if (stateCode == 5) { return 3 }
        if (waiters == null) { waiters = new CoroutineCarrigeQueue() }
        (waiters as CoroutineCarrigeQueue).push(new CoroutineCarrige(waiter))
        return 0
    }

    // 终态迁移 + waiter 排空（桥在临界区外逐个重新发布到 waiter 自己
    // 的 Executor，§18.3）；返回被排空的 waiter 句柄数组
    priv func complete(): Array\<CoroutineCarrige> {
        state = .Completed
        stateCode = 3
        return takeWaiters()
    }

    priv func fail(): Array\<CoroutineCarrige> {
        state = .Failed
        stateCode = 4
        // 已有 waiter / 已观察：登记时 observed 已置位，失败节点不得
        // 留在未观察清单（VM UnobservedFailure 按 Task.observed 过滤）
        if (observed and (failureNodeId != (0 as i64))) {
            rigi_failure_drop(failureNodeId)
            // 不清零：waiter 恢复后仍按节点 id 读异常
        }
        return takeWaiters()
    }

    priv func cancel(): Array\<CoroutineCarrige> {
        state = .Cancelled
        stateCode = 5
        return takeWaiters()
    }

    priv func takeWaiters(): Array\<CoroutineCarrige> {
        if (waiters == null) { return core.collections.arrayOf\<CoroutineCarrige>(0) }
        const drained = (waiters as CoroutineCarrigeQueue).drainToArray()
        waiters = null
        return drained
    }

    // 编译器生成的 cold factory 唯一写入口：裸 token 在本同步调用内
    // 立即封装成 Carrige，不进入 shared 字段。
    priv func attachCold(coroutine: i64) {
        coldHandle = new CoroutineCarrige(coroutine)
    }

    priv func publishRuntime() {
        if (handle == null) {
            throw new core.IllegalStateException("Task 尚未附着 CoroutineHandle")
        }
        new Dispatcher().publish(handle as CoroutineCarrige)
    }

    priv func hasRuntime(): bool { return (handle != null) }
}

pub shared class Task\<TReturn> {
    priv const body: core.AsyncFunc\<TReturn>

    // 冷 Task：只存 body 不执行（§18.4）；gate 构造即建（同 Task）
    pub init(_ -> body) {
        gate = rigi_sync_mutex_create()
    }

    pub var state: TaskState {
        pub get
        priv set
    } = .Created

    pub var executor: Executor? {
        pub get
        pub set(value: _) {
            if (handle != null) {
                const lane = laneOfExecutor(value)
                if (lane >= 0) {
                    const retained = retainCoroutine(handle as CoroutineCarrige)
                    if (retained != null) {
                        seq using(const nativeHandle = retained as CoroutineHandle) {
                            nativeHandle.setLane(lane)
                        }
                    }
                }
            }
        }
    } = null

    pub func run() {
        startCold()
    }

    pub func run(executor: Executor) {
        this.executor = executor
        startCold()
    }

    // VM 侧由方法 hook 接管（同 Task.startCold）；native 半场真体
    //（同 Task.startCold/spawnIntoLocked 注释）
    priv func startCold() {
        rigi_sync_mutex_acquire(gate)
        if (tryStart() != 0) {
            rigi_sync_mutex_release(gate)
            throw new core.IllegalStateException(
                "Task 只允许启动一次：对已完成启动的 Task 调用 run")
        }
        spawnIntoLocked()
        const started = coldHandle as CoroutineCarrige
        rigi_sync_mutex_release(gate)
        const dispatcher = new Dispatcher()
        dispatcher.noteSpawn()
        dispatcher.publish(started)
    }

    // spawn-into 临界区段（同 Task.spawnIntoLocked）
    priv func spawnIntoLocked() {
        if (coldHandle == null) {
            bindColdBody()
        }
        const carrige = coldHandle as CoroutineCarrige
        const retained = retainCoroutine(carrige)
        if (retained == null) {
            throw new core.IllegalStateException("冷 Task 的 CoroutineHandle 已释放")
        }
        seq using(const nativeHandle = retained as CoroutineHandle) {
            nativeHandle.inheritLocals()
            var lane = laneOfExecutor(executor)
            if (lane < 0) { lane = new Dispatcher().laneOfCurrent() }
            nativeHandle.setLane(lane)
        }
        attachRuntime(carrige)
    }

    // 不透明冷 body 的动态 spawn-into（同 Task.bindColdBody）
    priv func bindColdBody() {
        throw new core.IllegalStateException(
            "冷 Task body 无法 spawn-into：无匹配闭包")
    }

    // ===== MW11c 棒4a 运行时通道（与 Task 同纪律，§17.4）=====
    priv var handle: CoroutineCarrige? = null
    priv var gate: i64 = (0 as i64)
    priv var stateCode: i32 = 0
    priv var observed: bool = false
    priv var waiters: CoroutineCarrigeQueue? = null
    // ===== MW11c 棒5a native 半场字段（语义注释见 Task 同名成员）=====
    // result：终态结果（native 半场——DONE 尾写入后调 complete()；
    // VM 终态存 VmCoroutine，本字段恒 null）。await 成功快路径读本
    // 字段解包。TReturn 经 shared 闭包约束（§18.2）
    priv var result: TReturn? = null
    priv var failureNodeId: i64 = (0 as i64)
    priv var coldHandle: CoroutineCarrige? = null

    priv func attachRuntime(coroutine: CoroutineCarrige) {
        handle = coroutine
        if (gate == (0 as i64)) { gate = rigi_sync_mutex_create() }
        state = .Runnable
        stateCode = 1
    }

    priv func attachRuntimeNative(coroutine: i64) {
        attachRuntime(new CoroutineCarrige(coroutine))
    }

    // 冷启动一次性判定（同 Task.tryStart；桥持 gate 临界区）
    priv func tryStart(): i32 {
        if (stateCode != 0) { return 1 }
        state = .Runnable
        stateCode = 1
        return 0
    }

    priv func markSuspended() {
        state = .Suspended
        stateCode = 2
    }

    priv func markRunnable() {
        state = .Runnable
        stateCode = 1
    }

    // 返回 0=已登记；1=已成功；2=已失败；3=已取消（桥持 gate 临界区）
    priv func registerWaiter(waiter: i64): i32 {
        observed = true
        if (stateCode == 3) { return 1 }
        if (stateCode == 4) {
            // 观察即放掉 native 未观察报告引用（native 半场；VM
            // 侧 failureNodeId 恒 0 跳过）。先登记 waiter 再失败时
            // observed 已为 true，仍须 drop
            if (failureNodeId != (0 as i64)) {
                rigi_failure_drop(failureNodeId)
                // 不清零：await 失败快路径还要按节点 id 读异常
            }
            return 2
        }
        if (stateCode == 5) { return 3 }
        if (waiters == null) { waiters = new CoroutineCarrigeQueue() }
        (waiters as CoroutineCarrigeQueue).push(new CoroutineCarrige(waiter))
        return 0
    }

    priv func complete(): Array\<CoroutineCarrige> {
        state = .Completed
        stateCode = 3
        return takeWaiters()
    }

    priv func fail(): Array\<CoroutineCarrige> {
        state = .Failed
        stateCode = 4
        // 已有 waiter / 已观察：登记时 observed 已置位，失败节点不得
        // 留在未观察清单（VM UnobservedFailure 按 Task.observed 过滤）
        if (observed and (failureNodeId != (0 as i64))) {
            rigi_failure_drop(failureNodeId)
            // 不清零：waiter 恢复后仍按节点 id 读异常
        }
        return takeWaiters()
    }

    priv func cancel(): Array\<CoroutineCarrige> {
        state = .Cancelled
        stateCode = 5
        return takeWaiters()
    }

    priv func takeWaiters(): Array\<CoroutineCarrige> {
        if (waiters == null) { return core.collections.arrayOf\<CoroutineCarrige>(0) }
        const drained = (waiters as CoroutineCarrigeQueue).drainToArray()
        waiters = null
        return drained
    }

    priv func attachCold(coroutine: i64) {
        coldHandle = new CoroutineCarrige(coroutine)
    }

    priv func publishRuntime() {
        if (handle == null) {
            throw new core.IllegalStateException("Task 尚未附着 CoroutineHandle")
        }
        new Dispatcher().publish(handle as CoroutineCarrige)
    }

    priv func hasRuntime(): bool { return (handle != null) }
}

// Task 公开状态投影（§18.2）：Running 与 Runnable 对用户不可区分，
// 合并为 Runnable；isRunning 仅 Runnable/Suspended（已启动未终止）为 true
pub enum struct TaskState {
    pub const isRunning: bool
    priv init(_ -> isRunning)
}[
    Created(false),
    Runnable(true),
    Suspended(true),
    Completed(false),
    Failed(false),
    Cancelled(false)
]

pub shared abstract class Executor {
}

pub shared singleton class MainExecutor : Executor {
}

pub shared singleton class ComputeExecutor : Executor {
}

pub shared singleton class IOExecutor : Executor {
}

pub shared abstract class PollingAlarm {
    pub abstract func isReady(): bool
}

// EventAlarm（§19.3）：粘滞一次性事件。棒5a 形态：waiter 登记/触发
// 原子握手在 native 定时器记录闸内（rigi_alarm_wait / 响铃回调——与
// VM VmTimerRecord/VmEventAlarm 的取舍同构：waiter 是协程句柄，不经
// Rigi 对象传达），handle = native 时钟底座句柄（rigi_timer_create
// 响铃形态）。L8：handle == 0 的用户直继子类不再拒绝——ensureHandle
// 懒建 MW11d 手动事件粘滞形态作默认底座（rigi_event_create_sticky；
// VM 侧 TryAwaitTimer 同口径懒建），事件源经 protected signal() 触发
pub shared abstract class EventAlarm {
    internal var handle: i64 = (0 as i64)

    // 懒建默认底座（L8）：无事件源的用户直继子类（handle == 0）在首个
    // yield/signal 时补手动事件粘滞底座；并发首触互斥在 Dispatcher 闸内
    // 双检（§19.3 注册/触发原子握手的前置：底座唯一性本身须原子）。
    // 仅生成代码 yield EventAlarm 分流与本类 signal 调用
    internal func ensureHandle(): i64 {
        if (handle != (0 as i64)) { return handle }
        return new Dispatcher().ensureEventBase(this)
    }

    // 事件源触发入口（§19.3）：粘滞、幂等——无 waiter 时置已触发
    //（迟到 yield 立即具备重新发布条件）；有 waiter 时在同一闸内原子
    // 排空并逐个发布回 waiter 自己的 Executor。重复调用幂等
    protected func signal() {
        rigi_event_signal(ensureHandle())
    }
}

// sleep 的运行时内部 EventAlarm 子类（§19.4）：构造即把 deadline
// 排程到时钟底座（单次响铃；owner = 当前 Worker——主线程为 0）
priv shared class SleepAlarm : EventAlarm {
    pub init(milliseconds: i64) {
        handle = rigi_timer_create(rigi_tls_current_context(), milliseconds,
            (0 as i64), (0 as i64), (0 as i64))
    }
}

// 语言级异步互斥锁（§19.6）：锁可跨挂起点持有、非重入；与 §17.4 的
// native 同步 Mutex 原语（仅 Dispatcher 内部、不得跨挂起点）严格区分。
// FIFO 等待队列与判定逻辑在 Rigi 层（gate 同步锁仅护内部一致性）；
// 挂起/唤醒：VM 经 enter/release 方法 hook；native 经 CoroutineSplit
// 改写 enter + 本类 release 真体（判定与挂起在同一 gate 临界区内）
pub shared class Mutex {
    // 持有令牌：acquire 的返回物，防无锁释放；无公开构造入口。
    // ownerToken = 属主 Mutex 的 gate 句柄（由强 owner 保证存活期身份），
    // 字段默认可见性（编译单元内）供 Mutex.releaseNext 校验
    pub shared class Lock {
        // 令牌仍可被使用时保活属主，禁止原生 gate 地址被回收后复用。
        priv const owner: Mutex
        internal var ownerToken: i64
        internal var released: bool

        protected init(owner: Mutex) {
            this.owner = owner
            ownerToken = owner.gate
            released = false
        }
    }

    priv var gate: i64
    priv var held: bool
    priv var waiters: CoroutineCarrigeQueue?

    pub init() {
        gate = rigi_sync_mutex_create()
        held = false
        waiters = null
    }

    // 锁空闲立即取得；竞争挂起进 FIFO 等待队列（不占 Worker）
    pub async func acquire(): Lock {
        enter()
        return new Lock(this)
    }

    // VM 桥接管（方法 hook）：gate 临界区内 tryEnter 判定，竞争则把
    // 当前协程挂起到 FIFO 队尾。native：CoroutineSplit 把本调用改写为
    // 同纪律的挂起点（空体不执行）
    priv func enter() {
    }

    // 取得判定（桥持 gate 临界区调用）：0=立即取得；1=已登记队尾
    // （桥随后在同一临界区内挂起当前协程）。非重入：持锁中再 acquire
    // 一律排队
    priv func tryEnter(waiter: i64): i32 {
        if (not held) {
            held = true
            return 0
        }
        if (waiters == null) { waiters = new CoroutineCarrigeQueue() }
        (waiters as CoroutineCarrigeQueue).push(new CoroutineCarrige(waiter))
        return 1
    }

    // 同步释放：令牌校验 + 排空队首 waiter 发布。VM 经方法 hook 走同
    // 序（判定在临界区内、publish 在临界区外）；native 跑本真体。
    // 令牌校验失败会从 releaseNext 抛出——gate 必须在 finally 解开，
    // 否则 linux 上持锁 Mutex 析构会 abort（win 侧 uv_mutex 较宽容）
    pub func release(lock: Lock) {
        rigi_sync_mutex_acquire(gate)
        var next: CoroutineCarrige? = null
        try {
            next = releaseNext(lock)
        } finally(e) {
            rigi_sync_mutex_release(gate)
        }
        if (next != null) {
            const dispatcher = new Dispatcher()
            dispatcher.publish(next as CoroutineCarrige)
        }
    }

    // 释放判定（桥持 gate 临界区调用）：令牌不属于此 Mutex 或已释放 →
    // 抛 core.IllegalStateException。返回被唤醒的队首 waiter 句柄
    // （0=无 waiter，锁转空闲；有 waiter 则锁所有权直接移交队首，
    // held 保持 true——FIFO handoff，无 barging 窗口）
    priv func releaseNext(lock: Lock): CoroutineCarrige? {
        if (lock.released or (lock.ownerToken != gate)) {
            throw new core.IllegalStateException(
                "Mutex.release：令牌不属于此 Mutex 或已释放")
        }
        lock.released = true
        if (waiters == null) {
            held = false
            return null
        }
        const next = (waiters as CoroutineCarrigeQueue).tryPop()
        if (next == null) { held = false }
        return next
    }

    // acquire → 执行 body 一次 → finally 语义释放（异常安全）
    pub async func runSynchronously(body: core.AsyncAction) {
        const lock = await acquire()
        try {
            await body()
        } finally(e) {
            release(lock)
        }
    }

    pub async func runSynchronously\<TReturn>(body: core.AsyncFunc\<TReturn>): TReturn {
        const lock = await acquire()
        try {
            return await body()
        } finally(e) {
            release(lock)
        }
    }
}

// 把「到点响铃」包装为可 yield 的 Alarm（§19.5）：构造即排程；粘滞、
// 注册/触发原子握手等 EventAlarm 语义（§19.3）原样适用
pub shared class Timer : EventAlarm {
    // 重复策略（§19.5）：NoRepeat 响铃一次后恒已触发；Repeat 有限重复，
    // 参数洞通道经 pub init 校验 repeatCount > 0（否则抛
    // core.IllegalStateException）；InfiniteRepeat 无限重复
    pub enum struct RepeatOption {
        pub const repeatCount: i32
        pub const isInfinite: bool

        // 固定模板通道（NoRepeat/InfiniteRepeat）：编译期常量无校验
        priv init(_ -> repeatCount, _ -> isInfinite)

        // 参数洞通道（Repeat）：repeatCount 必须 > 0（§19.5）
        pub init(count: i32) {
            if (count <= 0) {
                throw new core.IllegalStateException(
                    "Timer.RepeatOption.Repeat：repeatCount 必须 > 0")
            }
            repeatCount = count
            isInfinite = false
        }
    }[
        NoRepeat(0, false),
        Repeat(count = _),
        InfiniteRepeat(-1, true)
    ]

    priv const repeat: RepeatOption

    // 构造即排程：首次响铃时刻立即注册到时钟底座（§19.5）：owner =
    // TLS 当前 Worker（主线程为 0）；Repeat/InfiniteRepeat 以同一间隔
    // 重复（NoRepeat → repeat=0）。ctx 承载 repeatCount（0=NoRepeat，
    // -1=Infinite，n=有限次数）；callbackFn=0 = EventAlarm 响铃形态
    //（响铃 = native 闸内排空 waiter 并经导出符号 rigi_dispatch_publish
    // 重发布，VM 侧为 VmDispatch.RingTimer 同语义）
    pub init(delayMilliseconds: i64, repeat: RepeatOption = .NoRepeat) {
        this.repeat = repeat
        var repeatInterval: i64 = (0 as i64)
        if (repeat.repeatCount != 0) {
            repeatInterval = delayMilliseconds
        }
        handle = rigi_timer_create(rigi_tls_current_context(),
            delayMilliseconds, repeatInterval, (0 as i64), (repeat.repeatCount as i64))
    }

    // 便捷入口（§19.5）：语义等价 new Timer(ringTime - DateTime.now())
    pub static func schedule(ringTime: core.time.DateTime): EventAlarm {
        return new Timer((ringTime - core.time.DateTime.now()).totalMilliseconds)
    }
}

// per-coroutine 上下文（§20.2）：具体 shared class。键身份 = 本对象
// 的进程稳定身份（两枚 `new CoroutineLocal\<T>()` 是不同键；典型用法
// 是模块级 `const id = new CoroutineLocal\<String>()`）。绑定存在
// 协程句柄上，跟随 Coroutine 跨 Worker 迁移，不是 OS ThreadLocal。
// TValue 的共享安全约束与 Task\<TReturn>.result 相同（实例化点闭包）。
pub shared class CoroutineLocal\<TValue> {
    priv const hasDefault: bool
    priv const defaultValue: TValue?

    pub init() {
        hasDefault = false
        defaultValue = null
    }

    pub init(defaultValue: TValue) {
        hasDefault = true
        this.defaultValue = defaultValue
    }

    // 当前协程上本键的有效绑定；未绑定且有构造默认 → 默认值；
    // 未绑定且无默认 → null。无当前协程与未绑定同口径。
    pub func get(): TValue? {
        const boxed = rigi_coro_local_get(this as Any)
        if (boxed is TValue) {
            return (boxed as TValue)
        }
        if (hasDefault) {
            return defaultValue
        }
        return null
    }

    // 作用域绑定：压栈 → 执行 body → 无论成败弹栈。嵌套 withValue
    // 同键形成栈，get 自顶向下命中。body 经 invoke.indirect 派发。
    pub async func withValue(value: TValue, body: core.AsyncAction) {
        rigi_coro_local_push(this as Any, value as Any)
        try {
            await body()
        } finally(e) {
            rigi_coro_local_pop(this as Any)
        }
    }

    pub async func withValue\<TReturn>(value: TValue, body: core.AsyncFunc\<TReturn>): TReturn {
        rigi_coro_local_push(this as Any, value as Any)
        try {
            return await body()
        } finally(e) {
            rigi_coro_local_pop(this as Any)
        }
    }
}

// ===== MW11c 棒4a：Dispatcher 与内部队列（§17.4 Rigi 世界调度逻辑）=====

// CoroutineCarrige 环形队列：Dispatcher runnable 队列、Task/Mutex waiter
// 链共用。队列只保存 shared 搬运票据，不保存可操作句柄或裸 token。
priv shared class CoroutineCarrigeQueue {
    priv var items: Array\<CoroutineCarrige>
    priv var head: i32
    priv var count: i32

    pub init() {
        items = core.collections.arrayOf\<CoroutineCarrige>(8)
        head = 0
        count = 0
    }

    pub func isEmpty(): bool {
        return (count == 0)
    }

    pub func push(value: CoroutineCarrige) {
        if (count == items.length) { grow() }
        var slot = head + count
        if (slot >= items.length) { slot = slot - items.length }
        items[slot] = value
        count = count + 1
    }

    pub func tryPop(): CoroutineCarrige? {
        if (count == 0) { return null }
        const value = (items[head] as CoroutineCarrige)
        head = head + 1
        if (head == items.length) { head = 0 }
        count = count - 1
        return value
    }

    // 排空为数组（Task 终态的 waiter 发布用），队列复位
    pub func drainToArray(): Array\<CoroutineCarrige> {
        const result = core.collections.arrayOf\<CoroutineCarrige>(count)
        var i: i32 = 0
        while (i < count) {
            var slot = head + i
            if (slot >= items.length) { slot = slot - items.length }
            result[i] = (items[slot] as CoroutineCarrige)
            i = i + 1
        }
        head = 0
        count = 0
        return result
    }

    priv func grow() {
        const bigger = core.collections.arrayOf\<CoroutineCarrige>(items.length * 2)
        var i: i32 = 0
        while (i < count) {
            var slot = head + i
            if (slot >= items.length) { slot = slot - items.length }
            bigger[i] = (items[slot] as CoroutineCarrige)
            i = i + 1
        }
        items = bigger
        head = 0
    }
}

// Dispatcher：调度逻辑主体（§17.4）——runnable 队列（CarrigeQueue + sync
// mutex 临界区 + sem 交接协议）、live 计数/quiescence、未观察失败清单、
// Worker 循环。棒4b 形态：三 lane（§20.1 内置 Executor 恰好三个）——
// Main lane 恒投主 Worker（句柄 0，主线程）；Compute/IO lane 首次发布
// 时懒起 Worker（rigi_worker_create，Worker 线程跑同一 workerLoop）。
// live 全局共享：quiescence 是进程级退出判定（全部 Executor 的协程都
// 终态），非单 Executor 语义
priv shared singleton class Dispatcher {
    priv var gate: i64
    priv var mainQueue: CoroutineCarrigeQueue
    priv var computeQueue: CoroutineCarrigeQueue
    priv var ioQueue: CoroutineCarrigeQueue
    priv var live: i32
    // Compute/IO 的 Worker 句柄与懒建标记（gate 内完成登记后置 started，
    // 0 是合法主 Worker 句柄，故不能以 0 当「未建」哨兵）
    priv var computeWorkers: Array\<i64>
    priv var computeIdle: Array\<bool>
    priv var computeStarted: bool
    priv var ioWorker: i64
    priv var ioStarted: bool

    pub init() {
        gate = rigi_sync_mutex_create()
        mainQueue = new CoroutineCarrigeQueue()
        computeQueue = new CoroutineCarrigeQueue()
        ioQueue = new CoroutineCarrigeQueue()
        live = 0
        computeWorkers = core.collections.arrayOf\<i64>(0)
        computeIdle = core.collections.arrayOf\<bool>(0)
        computeStarted = false
        ioWorker = (0 as i64)
        ioStarted = false
    }

    // 新协程登记（发布前调用，live 先于入队递增，quiescence 判定才
    // 不会在「已 spawn 未 publish」窗口误判为零）。internal：Task
    // 启动壳与生成代码共用本通道
    internal func noteSpawn() {
        rigi_sync_mutex_acquire(gate)
        live = live + 1
        rigi_sync_mutex_release(gate)
    }

    // 终态登记：live 递减；归零时唤醒全部已建 Worker 做退出检查
    //（令牌 1 = 纯唤醒哑元；0=唤醒无任务，不得入队）
    internal func noteTerminal(handle: i64, failed: bool) {
        rigi_sync_mutex_acquire(gate)
        live = live - 1
        const empty = (live == 0)
        const wakeCompute = empty and computeStarted
        const wakeIo = empty and ioStarted
        const compute = computeWorkers
        const io = ioWorker
        rigi_sync_mutex_release(gate)
        if (empty) {
            rigi_worker_enqueue((0 as i64), (1 as i64))
            if (wakeCompute) {
                var i: i32 = 0
                while (i < compute.length) {
                    rigi_worker_enqueue((compute[i] as i64), (1 as i64))
                    i = i + 1
                }
            }
            if (wakeIo) { rigi_worker_enqueue(io, (1 as i64)) }
        }
    }

    // 未观察失败清单：native 半场在 rigi_rt 注册表（生成代码 DONE
    // 垫尾经 rigi_failure_record 登记、await 观察经 Task.
    // registerWaiter → rigi_failure_drop 标记观察、rigi_entry 汇总经
    // rigi_failure_take_unobserved 取走）；VM 半场在 VmDispatch.
    // _failed——双端各自承载，语义同口径（main 失败 > 未观察失败）

    // 当前线程所属 lane（0=Main / 1=Compute / 2=IO）：协程只在绑定
    // Executor 的 Worker 上运行（§17.1），故当前 Worker lane 即当前
    // 协程 lane——eager spawn 继承与 spawn-into 预设兜底据此换算。
    // internal：Task.spawnIntoLocked 共用
    internal func laneOfCurrent(): i32 {
        const worker = rigi_tls_current_context()
        rigi_sync_mutex_acquire(gate)
        var lane: i32 = 0
        if (computeStarted and (computeIndex(worker) >= 0)) {
            lane = 1
        } else {
            if (ioStarted and (worker == ioWorker)) {
                lane = 2
            }
        }
        rigi_sync_mutex_release(gate)
        return lane
    }

    // 发布 runnable 协程：临界区内尾插 + sem 交接唤醒。lane 从
    // cohandle 槽读取（发布时读最新绑定，§18.4 换绑于下一恢复点
    // 生效——本发布即「下一恢复点」）；Compute/IO 首次发布时懒起
    // Worker（§20.1 Worker 懒建）。internal：Task 启动壳/生成代码/
    // 导出符号 rigi_dispatch_publish 共用
    internal func publish(carrige: CoroutineCarrige) {
        const retained = retainCoroutine(carrige)
        if (retained == null) { return }
        var lane: i32 = 0
        seq using(const nativeHandle = retained as CoroutineHandle) {
            lane = nativeHandle.lane()
        }
        var worker = (0 as i64)
        rigi_sync_mutex_acquire(gate)
        if (lane == 1) {
            if (not computeStarted) {
                const count = rigi_worker_parallelism()
                computeWorkers = core.collections.arrayOf\<i64>(count)
                computeIdle = core.collections.arrayOf\<bool>(count)
                var i: i32 = 0
                while (i < count) {
                    computeWorkers[i] = rigi_worker_create((0 as i64))
                    computeIdle[i] = true
                    i = i + 1
                }
                computeStarted = true
            }
            computeQueue.push(carrige)
            // 空闲标记与队列由同一 gate 保护；一个发布只认领一枚空闲 Worker。
            worker = (-1 as i64)
            var i: i32 = 0
            while (i < computeWorkers.length) {
                if ((worker == (-1 as i64)) and (computeIdle[i] as bool)) {
                    computeIdle[i] = false
                    worker = (computeWorkers[i] as i64)
                }
                i = i + 1
            }
        } else {
            if (lane == 2) {
                if (not ioStarted) {
                    ioWorker = rigi_worker_create((0 as i64))
                    ioStarted = true
                }
                ioQueue.push(carrige)
                worker = ioWorker
            } else {
                mainQueue.push(carrige)
                worker = (0 as i64)
            }
        }
        rigi_sync_mutex_release(gate)
        if (worker != (-1 as i64)) { rigi_worker_enqueue(worker, (1 as i64)) }
    }

    // 仅在 gate 内调用；Worker 身份不成为用户语义。
    priv func computeIndex(worker: i64): i32 {
        var i: i32 = 0
        while (i < computeWorkers.length) {
            if ((computeWorkers[i] as i64) == worker) { return i }
            i = i + 1
        }
        return -1
    }

    // 完成执行段后原子地检查余项/登记空闲。若发布抢先入队则自唤醒，
    // 若登记抢先则发布者认领本 Worker，两个顺序均不会丢失唤醒。
    priv func prepareComputeWait(worker: i64) {
        rigi_sync_mutex_acquire(gate)
        const index = computeIndex(worker)
        var again = false
        if (index >= 0) {
            again = not computeQueue.isEmpty()
            computeIdle[index] = not again
        }
        rigi_sync_mutex_release(gate)
        if (again) { rigi_worker_enqueue(worker, (1 as i64)) }
    }

    // 终态 waiter 批量重发布（Task 终态桥在临界区外逐个发布到
    // waiter 自己的 Executor lane，§18.3；生成代码 DONE 尾调用）
    internal func publishAll(waiters: Array\<CoroutineCarrige>) {
        var i: i32 = 0
        while (i < waiters.length) {
            publish((waiters[i] as CoroutineCarrige))
            i = i + 1
        }
    }

    // Native/Middleware 回调边界：裸 token 在本同步调用内立即降为
    // Carrige，后续 shared 存储只看搬运票据。
    internal func publishNative(token: i64) {
        publish(new CoroutineCarrige(token))
    }

    // 用户 EventAlarm 默认底座懒建（L8，§19.3）：gate 临界区内双检 +
    // rigi_event_create_sticky 回写——并发首触只建一枚；仅 EventAlarm.
    // ensureHandle 调用（快路径不进场，竞争仅发生在首个 yield/signal）
    internal func ensureEventBase(alarm: EventAlarm): i64 {
        rigi_sync_mutex_acquire(gate)
        if (alarm.handle == (0 as i64)) {
            alarm.handle = rigi_event_create_sticky()
        }
        const result = alarm.handle
        rigi_sync_mutex_release(gate)
        return result
    }

    // 按 Worker 身份取本 lane 队首（0 = 空；Worker 只消费自己的 lane）
    priv func nextFor(worker: i64): CoroutineCarrige? {
        rigi_sync_mutex_acquire(gate)
        var carrige: CoroutineCarrige? = null
        if (computeStarted and (computeIndex(worker) >= 0)) {
            carrige = computeQueue.tryPop()
        } else {
            if (ioStarted and (worker == ioWorker)) {
                carrige = ioQueue.tryPop()
            } else {
                carrige = mainQueue.tryPop()
            }
        }
        rigi_sync_mutex_release(gate)
        return carrige
    }

    priv func quiescent(): bool {
        rigi_sync_mutex_acquire(gate)
        const result = ((live == 0) and mainQueue.isEmpty()) and (computeQueue.isEmpty() and ioQueue.isEmpty())
        rigi_sync_mutex_release(gate)
        return result
    }

    // Worker 循环（§17.4：Worker 线程体 = 以入口 fn 进入 Dispatcher
    // 循环）：quiescence 先检（主 Worker 在无协程程序直返，不 park——
    // rigi_entry 恒调用 workerLoop(0)）→ park 阻塞取唤醒 → 退出复检
    // → 出队 → resume 一个执行段（run-to-suspension）→ DONE(2) 销毁
    // 句柄（台账配对；VM 侧对象由 GC 托管，destroy hook 为校验性空
    // 操作）。SUSPENDED(0)/YIELDED(1) 的重发布已在挂起点完成。
    // live>0 且无 runnable 且无 alarm 时 park 永久阻塞——死锁显败：
    // native 由主 Worker 看门狗诊断 abort，VM 由 WorkerPark 的
    // IsDeadlocked 抛 VmException（双端判定内容同口径）
    priv func workerLoop(worker: i64) {
        while (true) {
            if (quiescent()) { return }
            rigi_worker_park(worker)
            if (quiescent()) { return }
            const carrige = nextFor(worker)
            if (carrige != null) {
                const retained = retainCoroutine(carrige as CoroutineCarrige)
                if (retained != null) {
                    seq using(const nativeHandle = retained as CoroutineHandle) {
                        const code = nativeHandle.resume()
                        if (code == (2 as i64)) { nativeHandle.retire() }
                    }
                }
            }
            prepareComputeWait(worker)
        }
    }
}

// Executor → Dispatcher lane（0=Main / 1=Compute / 2=IO；未知用户派生
// Executor 落 Main——§20.1 只有三个内置 Executor 有独立调度域）；
// null = 无预设（-1 哨兵，调用方落当前 lane）
priv func laneOfExecutor(executor: Executor?): i32 {
    if (executor == null) { return -1 }
    if (executor is ComputeExecutor) { return 1 }
    if (executor is IOExecutor) { return 2 }
    return 0
}

// ===== native 原语面（§17.4；MW11c 棒2 只声明，棒3 落地 rigi_rt）=====

// Worker 原语：环境可用并行度；覆盖参数范围由两宿主统一校验。
@NativeLibrary("rigi_rt")
@NativeSymbol("worker_parallelism")
priv native func rigi_worker_parallelism(): i32

// Worker 原语：创建/销毁/入队任务与跨线程唤醒/park
@NativeLibrary("rigi_rt")
@NativeSymbol("worker_create")
priv native func rigi_worker_create(entryFn: i64): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("worker_destroy")
priv native func rigi_worker_destroy(worker: i64)

@NativeLibrary("rigi_rt")
@NativeSymbol("worker_enqueue")
priv native func rigi_worker_enqueue(worker: i64, task: i64)

// 棒3 契约调整：棒2 声明无返回，但原语面无独立出队通道——park 是
// 唯一消费侧，故定为阻塞出队并返回任务令牌（0 = 唤醒无任务，
// destroy 的退出检查点）
@NativeLibrary("rigi_rt")
@NativeSymbol("worker_park")
priv native func rigi_worker_park(worker: i64): i64

// 协程句柄原语：create(resumeFn, frame)/resume（返回执行段归宿）/destroy
@NativeLibrary("rigi_rt")
@NativeSymbol("coroutine_create")
priv native func rigi_coroutine_create(resumeFn: i64, frame: i64): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("coroutine_resume")
priv native func rigi_coroutine_resume(handle: i64): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("coroutine_destroy")
priv native func rigi_coroutine_destroy(handle: i64)

// NativeRc 通用强引用面。只由具体 Carrige/Handle 实现调用；Carrige
// retain 失败返回空，Handle.dispose 幂等地交还成功取得的强引用。
@NativeLibrary("rigi_rt")
@NativeSymbol("native_rc_retain")
priv native func rigi_native_rc_retain(token: i64): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("native_rc_release")
priv native func rigi_native_rc_release(token: i64)

// 定时器原语：Timer 与 sleep 的时钟底座（§19.4/§19.5）。棒3 契约
// 调整：棒2 仅 deadline 一参，缺属主 Worker/重复间隔/响铃回调通道，
// 无从落地 §19.5 语义，扩为五参并补 cancel/destroy；回调在属主
// Worker 的 loop 线程触发（callbackFn = 0 为仅排程占位）
@NativeLibrary("rigi_rt")
@NativeSymbol("timer_create")
priv native func rigi_timer_create(owner: i64, delayMilliseconds: i64, repeatMilliseconds: i64, callbackFn: i64, ctx: i64): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("timer_cancel")
priv native func rigi_timer_cancel(timer: i64)

@NativeLibrary("rigi_rt")
@NativeSymbol("timer_destroy")
priv native func rigi_timer_destroy(timer: i64)

// 同步 Mutex 原语：仅供 Dispatcher 内部队列一致性（任何路径不得跨挂起
// 点持有；与语言级异步 Mutex 是两个东西）
@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_create")
priv native func rigi_sync_mutex_create(): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_acquire")
priv native func rigi_sync_mutex_acquire(mutex: i64)

@NativeLibrary("rigi_rt")
@NativeSymbol("sync_mutex_release")
priv native func rigi_sync_mutex_release(mutex: i64)

// TLS 当前上下文原语
@NativeLibrary("rigi_rt")
@NativeSymbol("tls_current_context")
priv native func rigi_tls_current_context(): i64

// 时钟原语：core.time.DateTime.now() 的底座（§19.7）
@NativeLibrary("rigi_rt")
@NativeSymbol("time_now")
priv native func rigi_time_now(): i64

// 棒5a：协程句柄附加面——当前协程（TLS；无当前协程返 0）与 lane 槽
//（eager spawn 继承/spawn-into 预设/executor 换绑写入；publish/唤醒
// 路径读取）
@NativeLibrary("rigi_rt")
@NativeSymbol("coroutine_current")
priv native func rigi_coroutine_current(): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("coroutine_get_lane")
priv native func rigi_coroutine_get_lane(handle: i64): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("coroutine_set_lane")
priv native func rigi_coroutine_set_lane(handle: i64, lane: i32)

// 棒5a：EventAlarm waiter 登记（§19.3 原子握手在 native 定时器记录闸
// 内）：1=已登记（未触发），0=已触发（调用方自行重发布，执行段仍
// 结束）。仅生成代码 yield EventAlarm 点调用
@NativeLibrary("rigi_rt")
@NativeSymbol("alarm_wait")
priv native func rigi_alarm_wait(alarm: i64, waiter: i64): i32

// L8：用户 EventAlarm 直继子类的默认底座两面（§19.3）——MW11d 手动
// 事件的粘滞形态（非 auto_reset：signal 恒置已触发并归还 armed，
// 迟到 yield 立即重发布；重复 signal 幂等）。仅 EventAlarm.
// ensureHandle/signal 调用
@NativeLibrary("rigi_rt")
@NativeSymbol("event_create_sticky")
priv native func rigi_event_create_sticky(): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("event_signal")
priv native func rigi_event_signal(event: i64)

// 棒5a：PollingAlarm 轮询状态（§19.2；退避 1→32ms 与 VM 同口径）。
// arm=yield 点登记（退避复位）；pending=恢复块判「先探测再续行」；
// schedule=探测未就绪排程退避重发布；clear=探测就绪/终态解除。仅
// 生成代码调用
@NativeLibrary("rigi_rt")
@NativeSymbol("poll_arm")
priv native func rigi_poll_arm(handle: i64)

@NativeLibrary("rigi_rt")
@NativeSymbol("poll_pending")
priv native func rigi_poll_pending(handle: i64): i32

@NativeLibrary("rigi_rt")
@NativeSymbol("poll_schedule")
priv native func rigi_poll_schedule(handle: i64)

@NativeLibrary("rigi_rt")
@NativeSymbol("poll_clear")
priv native func rigi_poll_clear(handle: i64)

// 棒5a：未观察失败注册表。登记 = 生成代码 DONE 垫尾经
// rigi_failure_record（split 发 MirCall，须进 stdlib 才能解析符号；
// C 形参是胖引用指针，Rigi 侧 any → NativeCallEmitter 16B 槽指针）；
// 摘除 = await 观察失败 Task 时由 registerWaiter 调
// rigi_failure_drop；汇总 = rigi_entry 的
// rigi_failure_take_unobserved（Emitter helper，不进 stdlib）。
// 注册表保留未观察报告引用；Task 的 native 隐藏 refMap 槽拥有异常，
// 不新增语言字段或放宽 shared class 字段限制
@NativeLibrary("rigi_rt")
@NativeSymbol("failure_record")
priv native func rigi_failure_record(exc: Any): i64

@NativeLibrary("rigi_rt")
@NativeSymbol("failure_drop")
priv native func rigi_failure_drop(nodeId: i64)

// CoroutineLocal 原语（§20.2）：绑定栈挂在当前协程句柄上。
// get 返回 Any（C 出参首槽胖引用，NativeCallEmitter 与 String/Any
// 返回同形态）；未绑定 / 无当前协程 → 零值，Rigi get() 再套默认或
// null。inherit 在 spawn/冷启动时从**当前**协程拷有效顶到 child
//（冷构造时的 coroutine_create 不继承——启动才拷）。
@NativeLibrary("rigi_rt")
@NativeSymbol("coro_local_push")
priv native func rigi_coro_local_push(key: Any, value: Any)

@NativeLibrary("rigi_rt")
@NativeSymbol("coro_local_pop")
priv native func rigi_coro_local_pop(key: Any)

@NativeLibrary("rigi_rt")
@NativeSymbol("coro_local_get")
priv native func rigi_coro_local_get(key: Any): Any

@NativeLibrary("rigi_rt")
@NativeSymbol("coro_local_inherit")
priv native func rigi_coro_local_inherit(child: i64)

// sleep 是 Rigi 层包装（§19.4）：棒5a 起迁 Rigi 实现——SleepAlarm
// 构造即把 deadline 排程到时钟底座（单次响铃 EventAlarm）；i32 →
// i64 显式 as。双端语义一致：VM 经 rigi_timer_create hook 建
// VmTimerRecord（原 make_sleep_alarm native 面已随 MW11c 转向删除）
pub func sleep(milliseconds: i32): EventAlarm {
    return new SleepAlarm((milliseconds as i64))
}
