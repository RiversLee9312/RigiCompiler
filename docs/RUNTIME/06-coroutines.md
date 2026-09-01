# 协程 / Executor / Task / Alarm（§17–§21）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 17. 原生协程、Executor 与 Worker

Rigi 从 `main` 开始就在协程中执行。协程（Coroutine）是语言的逻辑执行单元；Executor 是协程绑定的调度域（默认终身不变，唯一例外是 §18.4 的 Task.executor 显式换绑）；Worker 是 Executor 内部实际运行用户代码的操作系统线程的抽象（内部 API，不暴露给用户，§17.4）。

### 17.1 核心不变量

- 每个 Coroutine 在创建时确定一个 Executor，默认在整个生命周期内保持不变。唯一例外是 Task.executor 显式换绑通道（§18.4）：换绑在下一个恢复点生效，当前执行段内永不迁移；换绑生效前该 Coroutine 仍属原 Executor。
- Coroutine 只能选择 Executor，不能选择具体 Worker。
- Worker 对程序透明；程序不得依赖 Worker 身份或挂起前后的线程一致性。
- 任意时刻，同一个 Coroutine 最多只能由一个 Worker 执行。
- Coroutine 挂起后不再占用 Worker；恢复时重新进入所属 Executor 的逻辑待执行协程池，并可由该 Executor 的任意 Worker 取走。
- 同一 Executor 的全部 Worker 使用相同调度策略，并从同一个逻辑 Runnable Set 获取工作。

“共享待执行协程池”只是一项语义约束。实现可以使用单队列、分片队列、per-worker 本地队列、局部缓存或 work stealing；这些差异不得被 Rigi 程序观察，也不得改变上述不变量。

### 17.2 run-to-suspension

Coroutine 采用 run-to-suspension。一个 Worker 开始执行某个 Coroutine 后，持续执行到以下边界之一：

- `await` 一个尚未终止的 Task；
- 裸 `yield`；
- `yield PollingAlarm`；
- `yield EventAlarm`；
- 正常返回；
- 未处理异常；
- 进入取消终态。

编译器和运行时不在普通语句、循环回边或函数调用之间暗中插入协程轮换点。操作系统仍可抢占 Worker 线程，但 OS 抢占不会使该 Worker 在同一个 Rigi 执行段中改为执行另一个 Coroutine。

普通函数和普通 lambda 在当前 Coroutine 内执行，并可以使当前 Coroutine `await` 或 `yield`；`async` 的意义是“调用时另建 Coroutine”，而不是“允许函数体挂起”。

参考 VM（`BilVm`）额外提供**实现级**指令步数上限：CLI `vm --max-steps <N>`（正整数；缺省不限制）。每条 BIL 指令计 1 步，超过时以 `VmStepLimitException` 受控终止（退出码 1）。此上限不是语言语义，Native 实现不必提供。

### 17.3 状态机

Coroutine 的公开语义状态为：

```text
Created
  ↓
Runnable
  ↓
Running
  ├──→ Suspended ──→ Runnable
  ├──→ Completed
  ├──→ Failed
  └──→ Cancelled
```

- `Created`：运行时实体已建立但尚未发布。
- `Runnable`：已进入所属 Executor 的逻辑可运行集合。
- `Running`：当前由某个 Worker 执行。
- `Suspended`：正在等待 Task、Alarm 或其他可挂起同步源，不占用 Worker。
- `Completed`：正常结束并保存结果。
- `Failed`：以未处理异常结束并保存异常。
- `Cancelled`：进入取消终态。

三个终止状态均不可再次恢复。运行时必须以原子状态转换保证一个 Coroutine 不会被重复发布或同时运行。

### 17.4 运行时内分层：Rigi 世界与 native 原语

协程运行时的实现分两层：调度策略与生命周期逻辑全部在 Rigi 世界表达，native 只保留不能再降的原语。VM 与 native 两宿主共享同一份 Rigi 调度逻辑。

**Rigi 世界**（`core.coroutine` 及其内部实现）：

- **Task**：生命周期、状态投影（TaskState，§18.2）、终态保存与 waiter 列表；与 Dispatcher 交互完成启动、终态发布与等待登记。
- **Dispatcher**：调度逻辑主体——runnable 队列、publish/next、quiescence 判定、未观察失败清单、Alarm 集成与 Polling 探测表。
- **Executor**：面向用户的 singleton 门面（§20.1），持有 Dispatcher 实例与 Worker 配置。
- **Worker**：Rigi 世界里 OS 线程的抽象，内部 API，不向用户暴露。Worker 线程体 = 以入口 fn 指针进入 Dispatcher 循环；native 侧经 fn 指针回调 Rigi 代码（沿用既有 probe/resume 先例）。

**native 世界**（`rigi_rt` 原语面）只提供：

- **Worker 原语**：创建/销毁、入队任务与跨线程唤醒、park；
- **协程句柄原语**：`create(resumeFn, frame)` / `resume(handle)`（返回执行段归宿）/ `destroy`；
- **定时器原语**：Timer 与 `sleep` 的时钟底座（§19.4/§19.5）；
- **同步 Mutex 原语**：供 Dispatcher 内部队列一致性使用的同步锁。它与语言级异步 `Mutex`（§19.6）是两个东西：前者只服务运行时内部、在任何路径上都不得跨挂起点持有；
- **TLS 当前上下文原语**；
- **时钟原语**：`core.time.DateTime.now()` 的底座（§19.7）。

这层划分不改变 §17.1–§17.3 的任何语义不变量：调度逻辑用 Rigi 表达不等于语义可由用户覆写——Dispatcher 与 Worker 的成员不对公共面暴露，用户可见的仍是 Task/Executor/Alarm/Mutex/Timer 类型面。

---

## 18. `async` 调用与 Task

### 18.1 eager spawn

每次调用 async 函数或 async lambda 时，调用方执行：

1. 求值 receiver、泛型参数和全部实参；
2. 创建 Coroutine 及其 Task 句柄；
3. 确定并绑定目标 Executor（终身性与换绑例外见 §17.1）；未显式指定时继承调用方 Coroutine 的 Executor；
4. 将 Coroutine 从 `Created` 转换为 `Runnable` 并发布到目标 Executor；
5. 向调用方返回 Task。

async 函数体不作为调用方当前执行段的一部分运行。实参求值期间的异常直接发生在调用方；进入 async 函数体之后的未处理异常记录进 Task。

在多 Worker Executor 上，新 Coroutine 可能在调用表达式返回 Task 前就被另一个 Worker 取走执行。Task 是热任务句柄，不是惰性计算。

### 18.2 Task 类型

- 无结果异步调用返回 `core.coroutine.Task`；
- 有结果异步调用返回 `core.coroutine.Task\<TResult>`。

Task 是 `core.coroutine` 的具体 shared class（非 abstract，Rigi 世界实现，§17.4），因为句柄、终态和 waiter 列表都可能同时被多个 Coroutine 访问。async receiver、实参、capture 与 `TResult` 必须满足 shared 闭包：shared Object 可共享，非 rich/shared rich ValueType 按值复制，local Object 与非 shared rich ValueType 不得跨边界。

一个 Task 对应一次异步调用的终态，并可由多个等待者、多次 `await`；这不会重新执行 async 函数。Task 保存以下三类终态：

- 成功：保存 `TResult` 或无结果完成标记；
- 失败：保存未处理异常；
- 取消：保存取消状态。

丢弃 Task 句柄只表示调用方不再同步或观察它，不会取消对应 Coroutine。Executor/运行时活跃协程表必须持有该 Coroutine 直到终态，因此直接调用 async 函数并忽略返回值即可实现 fork/fire-and-forget。

Task 的公开状态投影是 `core.coroutine.TaskState` 六 case 枚举：`Created` / `Runnable` / `Suspended` / `Completed` / `Failed` / `Cancelled`。`Running` 与 `Runnable` 对用户不可区分（Worker 身份透明，§17.1），投影合并为 `Runnable`。只读成员 `isRunning` 仅当状态为 `Runnable` 或 `Suspended`（即已启动未终止）时为 `true`，其余为 `false`。API 形状见 `SYNTAX.md` §4.5。

### 18.3 `await`

求值 `await taskExpression` 后：

- Task 已成功：立即取得结果；
- Task 已失败：在 await 点重新抛出保存的异常；
- Task 已取消：在 await 点传播取消；
- Task 尚未启动（冷 Task，§18.4）：在当前 Executor 启动该 Task，并按未终止流程等待；多个 Coroutine 并发启动同一冷 Task 时，竞争输家不抛异常，按普通 waiter 等待终态；
- Task 尚未终止：把当前 Coroutine 登记为 waiter，转入 `Suspended`，并把 Worker 归还给 Executor。

Task 进入终态后，每个 waiter 都重新发布到 **waiter 自己绑定的 Executor**（绑定与换绑规则见 §17.1/§18.4），而不是 Task 所属 Coroutine 的 Executor。若 Task 在 await 时已经终止，await 不要求实际挂起。

Task 终态发布与 await 成功恢复之间建立同步关系：被等待 Coroutine 在终止前完成的写入，在 await 返回后对等待者可见。

### 18.4 冷 Task 与启动通道

`new Task(body)` / `new Task\<TReturn\>(body)` 只保存 body，不执行——这是冷 Task（API 形状见 `SYNTAX.md` §4.5）。冷 Task 尚未拥有 Coroutine，状态为 `Created`。首次启动经 **spawn-into** 完成：复用该 Task 对象建立 Coroutine 并发布，Task 与 Coroutine 保持 1:1，不另建句柄。

- **启动通道**：`run()`（以「executor 预设值 ?? 当前 Executor」启动）、`run(executor)`（先设预设再启动）、`await`（§18.3：在当前 Executor 启动并等待）。Task 只允许启动一次；对已启动 Task（含 eager spawn 返回的热 Task）调用 `run` 抛 `core.IllegalStateException`。多个 Coroutine 并发 await/start 同一冷 Task 时，只有一个完成 spawn-into，其余不抛异常，按普通 waiter 等待终态。
- **executor 预设与换绑**：未启动时，`executor` 的读写对象是启动预设；已启动后写入 `executor` 是**换绑**——该 Task 对应 Coroutine 在下一个恢复点起归入新 Executor，当前执行段内永不迁移（§17.1）。换绑不重建 Coroutine，与 spawn（跨 Executor = 目标侧新建 Coroutine，§20.1）是两条不同的通道。
- **与 eager spawn 的关系**：直接调用 async 函数/lambda 仍是 eager spawn 热 Task（§18.1），语义不变；冷 Task 是显式控制启动时机与目标 Executor 的通道。推荐写法见 `SYNTAX.md` §4.5。

---

## 19. `yield` 与 Alarm

### 19.1 裸 `yield`

裸 `yield` 执行：

```text
Running → Runnable
```

当前执行段结束，Coroutine 重新进入所属 Executor 的逻辑 Runnable Set，Worker 返回调度器。`yield` 只保证重新经过一次调度决策，不保证一定换到另一个 Coroutine，不保证 FIFO，也不保证另一个任务至少执行一次。

### 19.2 `PollingAlarm`

`core.coroutine.PollingAlarm` 定义同步探测方法：

```rigi
pub func isReady(): bool
```

执行 `yield alarm` 且静态/运行时类型为 PollingAlarm 时：

1. 当前 Coroutine 保存 continuation，进入 `Suspended(PollingAlarm)`；
2. Executor 将它登记到逻辑 polling 等待集合；
3. 每当调度器再次给该等待任务一次调度机会时，由某个 Worker 调用一次 `alarm.isReady()`；
4. 返回 `false`：不恢复用户 continuation，继续处于 PollingAlarm 等待；
5. 返回 `true`：调度器原子地取得该 Coroutine 的执行权，使其转为 `Running`，并在本次调度机会中直接从 `yield` 后继续执行；
6. 抛出异常：该异常被视为发生在 yield 点，使 Coroutine 进入失败传播流程。

`isReady()` 必须同步、线程安全、可重复调用，不得执行 `await`/`yield`，也不得进行长期阻塞。连续探测可以由不同 Worker 执行。轮询频率不是语言保证；实现可使用退避、批量扫描或专用 polling 队列避免空闲时忙等。

### 19.3 `EventAlarm`

`core.coroutine.EventAlarm` 表示由外部事件 callback 唤醒的一次性、粘滞事件：事件发生后该实例保持已触发状态，避免事件在 waiter 注册前发生而丢失。

执行 `yield eventAlarm` 时：

1. 当前 Coroutine 与 EventAlarm 原子完成 waiter 注册；
2. 未触发时进入 `Suspended(EventAlarm)`；
3. 已触发时仍结束当前执行段，但立即具备重新发布条件；
4. 事件源触发时，callback 原子地标记 Alarm，并把 waiter 重新发布到各自所属 Executor；
5. callback 不直接恢复 continuation，也不执行用户 Rigi 代码。

注册和触发之间必须进行原子握手，保证并发发生时不丢失唤醒；同一个 waiter 最多只能被发布一次。EventAlarm 的重复触发是幂等的。

### 19.4 `sleep`

标准库函数：

```rigi
core.coroutine.sleep(milliseconds: i32): core.coroutine.EventAlarm
```

`sleep` 是 Rigi 层包装：它构造内部 `SleepAlarm`（`EventAlarm` 子类），构造即经
`rigi_timer_create` 把 deadline 排程到时钟底座（§17.4）。旧 `make_sleep_alarm`
native 面已删除。

返回运行时内部的 EventAlarm 子类。它把 deadline 注册到系统时钟树；时钟到期时 signal 该 Alarm，并由 Alarm 将等待 Coroutine 发布回原 Executor。

`sleep` 的等待不占用 Worker，也不调用阻塞当前 Worker 的系统 sleep。计时基于单调时钟；到达 deadline 只表示 Coroutine 重新可运行，实际继续执行时间仍取决于 Executor 调度。

任何带 Alarm 的 `yield` 都会结束当前 run-to-suspension 执行段，即使 Alarm 在执行 yield 时已经就绪或已触发。

### 19.5 `Timer` 与 `RepeatOption`

`core.coroutine.Timer` 是 `EventAlarm` 的具体子类，把「到点响铃」包装为可 `yield` 的 Alarm：

```rigi
pub shared class Timer : EventAlarm {
    pub init(delayMilliseconds: i64, repeat: RepeatOption = .NoRepeat)
    pub static func schedule(ringTime: DateTime): EventAlarm
}
```

- **构造即排程**：`new Timer(...)` 立即把首次响铃时刻注册到时钟底座，不需要额外的启动调用。
- `schedule(ringTime)` 是便捷入口，语义等价 `new Timer(ringTime - DateTime.now())`（时间类型见 §19.7）。
- 嵌套类型 `Timer.RepeatOption` 描述重复策略，共三个选项：
  - `NoRepeat`：不重复（repeatCount=0，isInfinite=false），响铃一次后进入已触发状态；
  - `Repeat(repeatCount)`：有限重复；构造入口校验 `repeatCount > 0`，否则抛 `core.IllegalStateException`；
  - `InfiniteRepeat`：无限重复（repeatCount=-1，isInfinite=true）。
- **重复闹钟语义**：每次响铃发布当前 waiter 并自动重排下一次；`Repeat` 耗尽后实例恒保持已触发（signaled）状态，后续 `yield` 立即具备重新发布条件。
- Timer 不改变 EventAlarm 的既有语义（§19.3）：粘滞、注册/触发原子握手、同一 waiter 最多发布一次、`yield` 必结束当前执行段（§19.4 末条）等条款原样适用。

### 19.6 `Mutex`（异步互斥锁）

`core.coroutine.Mutex` 是语言级异步互斥锁：

```rigi
pub shared class Mutex {
    pub async func acquire(): Lock
    pub func release(lock: Lock)
    pub async func runSynchronously(body: core.AsyncAction)
    pub async func runSynchronously\<TReturn\>(body: core.AsyncFunc\<TReturn\>): TReturn

    pub shared class Lock { ... }   // 持有令牌，无公开构造入口
}
```

- `acquire()` 是 async：锁空闲时立即取得；竞争时当前 Coroutine 挂起进入等待队列（FIFO），不占用 Worker。Mutex 非重入。
- `release(lock)` 是同步方法；`Lock` 是 `acquire` 返回的持有令牌（嵌套类型），防无锁释放。
- `runSynchronously` 自动 acquire → 执行 body 一次 → 以 finally 语义释放（异常安全）；泛型变种返回 body 的结果。
- **锁可跨挂起点持有**——这是异步互斥锁语义。它与 §17.4 的 native 同步 Mutex 原语严格区分：后者只服务 Dispatcher 内部队列一致性，不得跨挂起点持有，也不对用户暴露。

### 19.7 时间类型：`core.time` 的 `TimeStamp` / `DateTime` / `TimeSpan`

`core.time` 命名空间提供支撑 Timer 的最小时间面：

- `pub struct TimeStamp`：时刻戳，两字段——
  - `milliseconds`（i64）：1970/1/1 00:00 UTC 起的毫秒数，负数表示该时刻之前；
  - `nanoseconds`（i32）：毫秒之外多出的纳秒数；换算到 1970 起总纳秒 =
    `milliseconds * 1_000_000 + nanoseconds`。该字段经 getter/setter 限制可设置范围
    （0..999_999，越界抛 `core.OutOfBoundException`）。
- `pub struct TimeSpan`：包 i64 毫秒；`fromMilliseconds(i64)` 构造入口、只读属性 `totalMilliseconds`、比较运算符。
- `pub struct DateTime`：包一个 `TimeStamp`；`pub static func now(): DateTime`（native 时钟原语底座，§17.4）、减法运算符（两个 `DateTime` 相减得 `TimeSpan`）、比较运算符。

---

## 20. 内置 Executor 与 CoroutineLocal

### 20.1 Executor 层级

公共基类：

```text
core.coroutine.Executor
```

内置 Executor 是 singleton class：

```rigi
pub shared singleton class MainExecutor : Executor { }
pub shared singleton class ComputeExecutor : Executor { }
pub shared singleton class IOExecutor : Executor { }
```

基类 `Executor` 保持 abstract；三个内置 Executor 经 `new ComputeExecutor()` 等 singleton 构造表达式取得进程内唯一实例（singleton 语义见 `SYNTAX.md` §3.1.1/§9）。Worker 懒建：singleton 初始化只记录 Executor 种类，首个任务发布时才创建对应 OS 线程（§17.4 的 Worker 原语）。

所有 Executor 都遵守 §17 的统一不变量。每个 Executor 拥有一个或多个 Worker；同一 Executor 的所有 Worker 共享相同调度策略与同一逻辑 Runnable Set。具体 Worker 数量、队列结构、work stealing 和扩缩容策略属于实现细节，除标准库另有明示外不得成为程序语义。

`main` 根 Coroutine 默认绑定 MainExecutor。新 Coroutine 未显式选择 Executor 时继承创建方的 Executor。spawn 语义下跨 Executor 执行不会迁移当前 Coroutine，而是在目标 Executor 上创建新的 Coroutine，并通过 Task/await 同步。唯一的迁移通道是已启动 Task 的 `executor` 显式换绑（§18.4）：下一个恢复点生效，执行段内不迁移。

### 20.2 `CoroutineLocal\<TValue>`

```text
core.coroutine.CoroutineLocal\<TValue>
```

`CoroutineLocal\<TValue>` 是具体 `shared class`（非 abstract）。**键身份是进程稳定的实例身份**：两枚 `new CoroutineLocal\<T>()` 是不同的键；典型用法是模块级常量

```text
const id = new CoroutineLocal\<String>()
```

或带默认值的 `new CoroutineLocal\<String>("fallback")`。不要为「每个协程一个槽」去 `new`——槽在协程上下文里，键是这份对象。

绑定存储在 Coroutine 的上下文中（native：协程句柄上的绑定栈；VM：`VmCoroutine` 上的同构栈），跟随 Coroutine 跨 Worker 迁移。其读取结果不得依赖当前 Worker 或 OS Thread，因此不能使用普通 ThreadLocal 来实现公共语义。

公开面是作用域绑定，不是裸 `set`：

- `withValue(value, body)` / `withValue\<TReturn>(value, body)`：把 `value` 压到当前协程本键的绑定栈，执行 `body`，在 `finally` 中弹栈。同键嵌套 `withValue` 形成栈，`get` 自顶向下命中。`body` 是 `AsyncAction` / `AsyncFunc\<TReturn>`，经 `invoke.indirect` 派发。
- `get(): TValue?`：当前协程上本键的有效绑定。未绑定且构造时提供了默认值 → 返回该默认；未绑定且无默认 → `null`。无当前协程与未绑定同口径。

`TValue` 的共享安全约束与 `Task\<TReturn>.result` 相同（实例化点 shared 闭包）。

新 Coroutine 默认**继承**创建方当前协程的有效顶（每个键一份当前值，不是整段父栈）：eager spawn 与冷 Task 首次 `run`/`await`（spawn-into）都从启动方当前协程拷贝。冷 `new Task(body)` 只存 body，不在构造时继承——继承发生在启动。同一协程内的 `executor` 换绑不另拷绑定（句柄未换）。

Coroutine 挂起、重新发布以及换 Worker 恢复时，必须看到同一份 CoroutineLocal 上下文。Worker 私有缓存若存在，只能作为不可观察的实现优化。

CoroutineLocal 是「每协程一份绑定映射」的**唯一**机制。`singleton` class 的实例存储属于全局存储，因此按 `SYNTAX.md` §3.1.1 必须标记 `shared`，恒为进程内唯一的 shared object；singleton 不承担 per-coroutine 语义。键对象本身可以是全局/静态的（进程稳定身份）；per-coroutine 的是该键在协程上下文里的绑定。

---

## 21. 协程、GC 与同步边界

运行中的 Coroutine、挂起 continuation、Task 终态、Alarm waiter 注册以及 CoroutineLocal 上下文都可能持有托管引用，因此属于 ARC root slot，或由可达运行时对象间接保持。Coroutine 挂起不得使其局部变量、异常、返回槽或等待对象被错误释放。

本节协程语义不规定实现采用 stackful 栈、分段栈或 stackless continuation；无论采用何种表示，都必须提供精确的活跃引用映射，并与 `TypeSheet`/`refMap` 对 Object 和 rich Box 内容的扫描协同工作。

胖引用读写非原子，并发竞争同一引用槽属用户数据竞争（§3）；对象生命周期由 §22 的 ARC 和 §23 的 macroGC fence 保证。以下运行时边界至少建立 happens-before：

- async Coroutine 发布前的调用参数/捕获初始化 → 新 Coroutine 开始执行；
- Task 对应 Coroutine 的终止前写入 → waiter 的 await 成功恢复；
- EventAlarm 的 signal 前写入 → 因该 signal 恢复的 Coroutine；
- Executor 将 Coroutine 从 Suspended 原子转换并发布为 Runnable → 该 Coroutine 后续 Running；
- macroGC 完成清理并把 `gcFlag` 发布为 `IDLE` → 因本轮 `GCAlarm` 恢复后重新进入 acquire/release 的 Coroutine。

§17.4 的 Rigi 世界/native 原语分层不改变本节任何边界：Dispatcher 的 publish/next、Timer 重排与 Mutex 唤醒全部经由上述同一批原子状态转换与 Alarm 握手完成；§22/§23 的 GC 交互不因新分层破例。

shared 只允许对象跨 Coroutine 可达，并不使共享可变字段自动同步；用户数据竞争仍需标准库原子、锁、Channel 等同步原语处理。

---
