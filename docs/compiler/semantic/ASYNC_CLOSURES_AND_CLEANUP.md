# 异步、闭包与清理契约

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](SEMANTIC_ARCHITECTURE.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 7. 深度 lowering 与跨层契约

**BIL §17 语义**：BIL §17 是标准协程语义，不删除也不降级为
普通 stdlib 调用。P3/P4 把 `await Task<T>/Task` 和 `yield`
直接落为强类型 BIL 指令，async 调用保持 §15.2 的 eager `invoke` 语义；`using`
仍由 P4a 编织为 `try/finally` 清理路径。

Middleware 是 BIL §17 的实现者，负责把可挂起函数 lower 为状态机、保存和恢复
continuation、注册 Task/Alarm waiter，并在 frame/Task/清理记录引用发布时遵守
`RUNTIME.md` §23 ownership fence。frame 布局、state 编号和 Native ABI 不属于 BIL，
也不以 stdlib 普通调用伪装。完整裁决、closure/局部访问器接入及 Middleware 保留
native 面见本文 §7.2。

### 7.2 async/await/yield/using 与闭包 lowering（async 专项）

> 本节并入原 `docs/compiler/semantic/ASYNC_LOWERING_DESIGN.md`（2026-09-12 摘除，
> 其 §3.1 状态机模型曾被 MIDDLEWARE_ARCHITECTURE §6 与 CoroutineSplitPass 引用）。
> 规范以 SYNTAX §4.5/§5/§6.2/§7.5、RUNTIME §17–§25、BIL_STANDARD §17/§22 为准；
> 闭包捕获形态以 SYNTAX §5.2 为准（全 Cell 化）。

**裁决与分层。** BIL §17 是强类型协程语义，不删除也不改写为普通 stdlib 调用：

1. `await Task<T>` 发 `await $task $result`（结果变量严格为 `T`）；
2. `await Task` 发 `await $task`，仅作语句完成点，值位置报 P3 诊断；
3. `yield` / `yield PollingAlarm` / `yield EventAlarm` 直接发 BIL `yield`；
4. async 调用用带 `async` 修饰的方法 `invoke` 表示 eager spawn，不新增 spawn 指令。

frontend 不自己拆函数、合成 Coroutine frame 或改变函数 ABI；Middleware 消费合法
BIL 后才把含挂起点的函数降为状态机。分层职责：

| 层 | 责任 |
|---|---|
| P3 | await/yield 定型、挂起点收窄失效、lambda 捕获分析、局部访问器符号归属与可访问性 |
| P4a | `using` 清理路径编织；闭包存储提升的语义重写（Coroutine frame 归 Middleware）；不把 await/yield 改为调用 |
| P4b | 直接发 `await`/`yield`；async `invoke` 保留；发射 closure/访问器所需 BIL 形态 |
| BIL verifier | 复核 §17 的 Task/Alarm 类型、结果类型、挂起点 DA/控制流不变量 |
| Middleware | eager spawn、Task waiter、Alarm 注册、continuation、状态机、GC ownership fence |
| BIL VM | 解释同一 BIL §17 语义 |

P4 不得重新解析 Task 成员、重载或类型；P3 必须在 Bound 节点上保留已解析的
Task 结果类型、Alarm 类别与全部捕获符号。

**状态机与 continuation（Middleware 降级模型）。** 下面描述跨层语义契约，
具体状态/continuation 数据结构与 ABI 由 [Middleware 架构](../middleware/MIDDLEWARE_ARCHITECTURE.md)
定义；本节不要求每个逻辑项对应独立物理 frame 字段。 对每个可达挂起点把函数切为
state：入口 `state 0`，每个 `await`/`yield` 后的下一条可执行语句为唯一恢复
state；结构化 `try/finally`、循环、值块与 `using` 清理路径不改变此原则，其活动
控制状态随 continuation 保存。continuation 的逻辑内容：

- 程序计数 state；
- 跨挂起点仍可能读取、或为 finally/dispose/异常传播所需的局部、参数、临时值与 `.return` 槽；
- 活动异常/try 状态、循环/值块控制状态与 using 清理游标；
- 当前 Coroutine 的 Executor、CoroutineLocal 上下文与等待登记；
- 精确 GC 根映射（Object 引用与 rich ValueType/Box 的 refMap 信息）。

局部仅在挂起点后仍可能读取时进入 frame，其余保持普通临时值。state 编号、frame
字段顺序、对象布局与 Native ABI 都是 Middleware 内部细节，不进入 BIL 文本。

**Task 与 eager spawn。** async `invoke` 的操作数先按既有 BIL 左到右规则求值；
Middleware 随后创建 Task 与 Coroutine、复制/获取已由 P3 共享安全闸门许可的
receiver/参数/捕获、绑定 Executor 并原子发布 `Created → Runnable`，返回热 Task。
函数体异常记录为 Task 失败，绝不回流为调用表达式的同步异常；实参求值异常仍属
调用方。`await` 在 Task 已终止时不创建 continuation；未终止时先完整写入
continuation，再原子登记 waiter 并转为 Suspended，终态与登记须避免丢失唤醒。
恢复永远发布到等待者自己的 Executor，并在恢复点取得成功值、重抛保存异常或传播取消。

**yield。** 裸 `yield` 保存 continuation 后执行 `Running → Runnable`；
`yield Alarm` 保存 continuation 后注册 PollingAlarm/EventAlarm 等待；即使 Alarm
已就绪或已触发，也必须结束当前执行段。EventAlarm callback 只发布 waiter，不执行
Rigi 用户代码。PollingAlarm 的 isReady 探测按 §19.2（2026-09-12 设计决策）在等待协程
自己的恢复块内执行、允许挂起；实现按「isReady 是否含挂起点」分双路径——纯同步实现
走调度侧同步虚派发廉价路径，含挂起点实现经恢复块站点协议臂下钻（当前恢复协议）。

**GC fence 与 frame 发布。** 把值写入 Coroutine frame、Task 终态、waiter 链、
closure 环境或 using 清理记录均可能建立/移除托管引用；Middleware 必须把引用槽
写入、对应 acquire/release 与候选元数据更新放在 RUNTIME §23 定义的同一个
ownership region 中。顺序要求：

1. continuation/frame 的所有根槽先初始化并完成 acquire；
2. 在同一或后续受 fence 保护的 region 内发布 waiter/Coroutine 状态；
3. 只有发布成功后才允许当前 Worker 放弃执行权；
4. 恢复时先原子取得唯一运行权，再读取 frame 与清理/异常状态；
5. frame、Task 终态和清理记录的最后一次释放也走 ownership region。

若进入 ownership region 时 macroGC 已处于 STARTING/PROCESSING，使用运行时内部
`GCAlarm` 走 §23 双重检查；这是不可见挂起，不产生源码/BIL `yield`，但
continuation 仍须保存 Coroutine-owned `cFlag` 状态。

**await/yield/using 形态。**

- `BoundAwaitExpression`：Task 操作数、是否有结果、`TResult?`；P3 只接受精确的
  `core.coroutine.Task` 或 `Task<T>`，必要的子类型视图转换由既有 cast 规则先物化；
- `BoundYieldStatement`：无 Alarm 或已定型的 PollingAlarm/EventAlarm 操作数；
  Lowered 层同构，发射器生成 BIL §17，不退化为 `invoke`；
- smart cast 在 await/yield 后清除当前函数的收窄事实；DA 的「已赋值」事实不因
  挂起而清除；
- `await Task` 只能出现在语句位置；`await Task<T>` 可作表达式或语句（语句位丢弃
  结果）；
- `using` 不引入 BIL 指令：P3 固化资源局部、无参 `dispose` 符号与初始化器，P4a
  为每个成功初始化的资源建立逆序清理结构，并用嵌套 `try/finally` 编织初始化异常
  前缀、正常落尾、return/throw/break/continue 路径；using 资源槽不可重赋值；
  async/open/abstract dispose 保守拒绝（避免 fire-and-forget 或动态派发绕过清理
  完成语义）；`return@`/seq-exit 穿越 using 清理路径经 StructuredExitRouting 的
  route local + dispatcher relay 天然穿越 try/finally；
- 普通 `dispose()` 内可含 BIL `await`/`yield`：每个资源独立的 finally 结构把挂起
  点留在对应清理块内；Middleware 必须保存当前 dispose 调用与清理进度，外层
  return、异常传播或 Task 终态在清理完成前不得发布；不新增独立 BIL 清理 opcode。

**lambda 闭包与局部访问器。** 普通 lambda 与局部访问器共用同一 closure 机制
（隐藏类捕获字段 + Cell），不各造一套；被捕获变量一律 Cell 化（`const` →
`ReadonlyCell<T>`，`var` → `Cell<T>`），仅 `this`（普通字段）与 lambda 自身参数
例外——值 wrapper 对 get 的代理意味着按值拷贝会冻结 proxy 结果。P3 对每个
lambda 记录按符号身份排序的捕获集；捕获在 lambda 求值点经构造函数传入，只发生
一次：

- 隐藏类 `..lambda..稳定摘要` 继承 `core::Func`/`Action`/`AsyncFunc`/`AsyncAction`；
  调用走 `invoke.indirect` → 对象虚调用 `$$call`（callable 协议）；
- 被捕获局部/参数存储改 cell（.vars 投影 + 参数 prologue `.c.<名>`），读写走
  getValue/setValue；BIL `.cell<T>`/`.readonly_cell<T>`；
- async lambda 的 receiver/参数/结果/捕获/泛型实参继续受 shared-safe 五项闸门；
- 普通 lambda 可捕获 local object（仍在当前 Coroutine）；若其后作为 async lambda
  捕获或跨协程发布，P3 必须在发布边界拒绝；
- 循环变量/catch/finally(e)/using 变量被捕获（for 每迭代新 cell；
  catch/finally/using 进入块时构造）；
- 局部 `var`/`const` 访问器按路线 C：声明即 cell 化，`override getValue/setValue`
  体 = 用户访问器体；自由变量捕获进 cell（init 追加实参），复用
  `ClosureStoragePlan` ClosureField 路径；访问器不能越过词法存活期逃逸。

**stdlib 与 Middleware 保留 native 面。** `stdlib/core/coroutine.rg` 是源码可见的
类型面，不暴露 Coroutine、frame、waiter 或 GC fence；`Task`/`Task<T>` 是具体
shared class（pub init 冷 Task，SYNTAX §4.5），Executor 家族 singleton 门面
（RUNTIME §20.1）。Middleware 必须提供以下保留运行时面——下表名称是职责标签，不是现有 C 导出符号或 BIL
canonical symbol；具体实现由 Middleware 与运行时协调，不能当成用户可声明的 ABI：

| 保留面 | 消费者 | 语义 |
|---|---|---|
| `coroutine.spawn` | async `invoke` | 建 Task/Coroutine、绑定 Executor、发布 eager runnable |
| `task.await` | BIL `await` | 终态读取或 waiter 原子登记/恢复 |
| `coroutine.yield` | BIL `yield` | Runnable 重发布或 Alarm 等待登记 |
| `coroutine.complete/fail/cancel` | async state machine | 写 Task 终态并发布 waiter |
| `coroutine.frame` | state machine | 分配、扫描、保存、恢复 continuation |
| `gc.ownership-region` | 引用槽发布/回收 | 执行 RUNTIME §23 双检与 GCAlarm 等待 |
| `alarm.poll/event` | yield Alarm 与 `sleep` | Polling 探测、Event waiter 注册/触发 |

`sleep(i32)` 是 Rigi 层包装（构造内部 `SleepAlarm`，经 `rigi_timer_create` 排程，
RUNTIME §19.4）。`CoroutineLocal<TValue>`：具体 shared class，实例作进程稳定键；
`withValue` 作用域绑定；`get(): TValue?`（构造默认值可选；无绑定且无默认 →
null）；eager spawn 与冷 Task 启动默认继承调用方当前有效顶；实现通道是协程句柄
上的绑定栈（`rigi_coro_local_push/pop/get/inherit`）与 VM 同构栈，不是 OS
ThreadLocal。

**验收锚点。** P3/P4 以 Bound/Lowered/BIL 形态与 verifier 测试为主；Middleware
以集成测试验证 Task 终态、Executor 恢复、Alarm、using 清理与 GC fence。可挂起
finally/using 与异常 completion 是清理游标/异常覆盖的高危区，形态测试必须先固化；
闭包 cell 与 frame 同时持有 rich 值时，environment/frame 发布必须统一纳入
ownership region，避免重复 acquire/release 或遗漏 root map。

**仍开放的风险。** 取消语义已有运行时终态但缺源码取消 API——仅正确传播既有取消，
不新增取消入口。

---
