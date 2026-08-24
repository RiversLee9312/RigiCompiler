# 协程 / Executor / Task / Alarm（§17–§21）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 17. 原生协程、Executor 与 Worker

Rigi 从 `main` 开始就在协程中执行。协程（Coroutine）是语言的逻辑执行单元；Executor 是协程永久绑定的调度域；Worker 是 Executor 内部实际运行用户代码的操作系统线程。

### 17.1 核心不变量

- 每个 Coroutine 在创建时确定一个 Executor，并在整个生命周期内保持不变。
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

---

## 18. `async` 调用与 Task

### 18.1 eager spawn

每次调用 async 函数或 async lambda 时，调用方执行：

1. 求值 receiver、泛型参数和全部实参；
2. 创建 Coroutine 及其 Task 句柄；
3. 确定并永久绑定目标 Executor；未显式指定时继承调用方 Coroutine 的 Executor；
4. 将 Coroutine 从 `Created` 转换为 `Runnable` 并发布到目标 Executor；
5. 向调用方返回 Task。

async 函数体不作为调用方当前执行段的一部分运行。实参求值期间的异常直接发生在调用方；进入 async 函数体之后的未处理异常记录进 Task。

在多 Worker Executor 上，新 Coroutine 可能在调用表达式返回 Task 前就被另一个 Worker 取走执行。Task 是热任务句柄，不是惰性计算。

### 18.2 Task 类型

- 无结果异步调用返回 `core.coroutine.Task`；
- 有结果异步调用返回 `core.coroutine.Task\<TResult>`。

Task 是运行时内建的 shared Object，因为句柄、终态和 waiter 列表都可能同时被多个 Coroutine 访问。async receiver、实参、capture 与 `TResult` 必须满足 shared 闭包：shared Object 可共享，非 rich/shared rich ValueType 按值复制，local Object 与非 shared rich ValueType 不得跨边界。

一个 Task 对应一次异步调用的终态，并可由多个等待者、多次 `await`；这不会重新执行 async 函数。Task 保存以下三类终态：

- 成功：保存 `TResult` 或无结果完成标记；
- 失败：保存未处理异常；
- 取消：保存取消状态。

丢弃 Task 句柄只表示调用方不再同步或观察它，不会取消对应 Coroutine。Executor/运行时活跃协程表必须持有该 Coroutine 直到终态，因此直接调用 async 函数并忽略返回值即可实现 fork/fire-and-forget。

### 18.3 `await`

求值 `await taskExpression` 后：

- Task 已成功：立即取得结果；
- Task 已失败：在 await 点重新抛出保存的异常；
- Task 已取消：在 await 点传播取消；
- Task 尚未终止：把当前 Coroutine 登记为 waiter，转入 `Suspended`，并把 Worker 归还给 Executor。

Task 进入终态后，每个 waiter 都重新发布到 **waiter 自己永久绑定的 Executor**，而不是 Task 所属 Coroutine 的 Executor。若 Task 在 await 时已经终止，await 不要求实际挂起。

Task 终态发布与 await 成功恢复之间建立同步关系：被等待 Coroutine 在终止前完成的写入，在 await 返回后对等待者可见。

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

`sleep` 是 Rigi 层包装：它调用私有 native 声明
`make_sleep_alarm(milliseconds: i64): EventAlarm`（§26 / `BIL_STANDARD.md`
§22.5 hook 表）创建 Alarm。

返回运行时内部的 EventAlarm 子类。它把 deadline 注册到系统时钟树；时钟到期时 signal 该 Alarm，并由 Alarm 将等待 Coroutine 发布回原 Executor。

`sleep` 的等待不占用 Worker，也不调用阻塞当前 Worker 的系统 sleep。计时基于单调时钟；到达 deadline 只表示 Coroutine 重新可运行，实际继续执行时间仍取决于 Executor 调度。

任何带 Alarm 的 `yield` 都会结束当前 run-to-suspension 执行段，即使 Alarm 在执行 yield 时已经就绪或已触发。

---

## 20. 内置 Executor 与 CoroutineLocal

### 20.1 Executor 层级

公共基类：

```text
core.coroutine.Executor
```

内置 Executor：

```text
core.coroutine.MainExecutor
core.coroutine.ComputeExecutor
core.coroutine.IOExecutor
```

所有 Executor 都遵守 §17 的统一不变量。每个 Executor 拥有一个或多个 Worker；同一 Executor 的所有 Worker 共享相同调度策略与同一逻辑 Runnable Set。具体 Worker 数量、队列结构、work stealing 和扩缩容策略属于实现细节，除标准库另有明示外不得成为程序语义。

`main` 根 Coroutine 默认绑定 MainExecutor。新 Coroutine 未显式选择 Executor 时继承创建方的 Executor。跨 Executor 执行不会迁移当前 Coroutine，而是在目标 Executor 上创建新的 Coroutine，并通过 Task/await 同步。

### 20.2 `CoroutineLocal\<TValue>`

```text
core.coroutine.CoroutineLocal\<TValue>
```

CoroutineLocal 的绑定存储在 Coroutine 的上下文中，跟随 Coroutine 跨 Worker 迁移。其读取结果不得依赖当前 Worker 或 OS Thread，因此不能使用普通 ThreadLocal 来实现公共语义。

Coroutine 挂起、重新发布以及换 Worker 恢复时，必须看到同一份 CoroutineLocal 上下文。Worker 私有缓存若存在，只能作为不可观察的实现优化。

CoroutineLocal 是「每协程一个实例」的**唯一**机制。`singleton` class 的实例存储属于全局存储，因此按 `SYNTAX.md` §3.1.1 必须标记 `shared`，恒为进程内唯一的 shared object；singleton 不承担 per-coroutine 语义。

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

shared 只允许对象跨 Coroutine 可达，并不使共享可变字段自动同步；用户数据竞争仍需标准库原子、锁、Channel 等同步原语处理。

---
