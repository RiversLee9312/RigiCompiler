# Async Lowering 专项设计

> 范围：async/await/yield/using、lambda 闭包与局部访问器；不进入 BIL VM。
> **闭包捕获形态以 SYNTAX §5.2 为准**（全 Cell 化）。

## 1. 裁决与边界

本设计以 `SYNTAX.md` §4.5、§5.3、§6.2、§7.5，`RUNTIME.md` §17--§25 和
`BIL_STANDARD.md` §17/§22 为准。BIL 已经拥有强类型 `await`/`yield` 指令，
因此不把它们改写为普通 stdlib 调用，也不删除 BIL §17。

异步 lowering 如下：

1. `await Task<T>` 发 `await $task $result`，结果变量严格为 `T`。
2. `await Task` 发 `await $task`，仅作为语句完成点；在要求值的位置报 P3 诊断。
3. `yield`、`yield PollingAlarm`、`yield EventAlarm` 直接发 BIL `yield` 指令。
4. async 调用继续用带 `async` 修饰的方法 `invoke` 表示 eager spawn，不新增 spawn 指令。

frontend 不自己拆函数、合成 Coroutine frame 或改变函数 ABI。Middleware
消费合法 BIL 后，才可把含挂起点的函数降为状态机。这样不依赖尚未稳定的
闭包/frame ABI，同时保留 BIL VM 的抽象解释空间。

## 2. 分层职责

| 层 | 责任 |
|---|---|
| P3 | await/yield 定型、挂起点收窄失效、lambda 捕获分析、局部访问器的符号归属与可访问性 |
| P4a | `using` 清理路径编织；后续闭包/frame 提升的语义重写；不把 await/yield 改为调用 |
| P4b | 直接发 `await`/`yield`；async `invoke` 保留；发射 closure/访问器所需 BIL 形态 |
| BIL verifier | 复核 §17 的 Task/Alarm 类型、结果类型、挂起点的 DA/控制流不变量 |
| Middleware | eager spawn、Task waiter、Alarm 注册、continuation、状态机和 GC ownership fence |
| BIL VM | 解释同一 BIL §17 语义 |

P4 不得重新解析 Task 成员、重载或类型；P3 必须在 Bound 节点上保留已解析的 Task
结果类型、Alarm 类别和所有捕获符号。

## 3. 状态机与 continuation

### 3.1 Middleware 降级模型

Middleware 对每个可达挂起点把函数切为 state：入口为 `state 0`，每个 `await` 或
`yield` 后的下一条可执行语句为唯一恢复 state。结构化 `try/finally`、循环、值块和
`using` 清理路径不改变此原则；其活动控制状态必须随 continuation 保存。

continuation 的逻辑内容为：

- 程序计数 state；
- 所有跨挂起点仍可能读取、或为 finally/dispose/异常传播所需的局部、参数、临时值和 `.return` 槽；
- 活动异常/try 状态、循环/值块控制状态和 `using` 清理游标；
- 当前 Coroutine 的 Executor、CoroutineLocal 上下文与等待登记；
- 精确 GC 根映射，含 Object 引用与 rich ValueType/Box 的 refMap 信息。

局部仅在挂起点后仍可能读取时进入 frame，其余保持普通临时值。state 编号、frame 字段
顺序、对象布局和 Native ABI 都是 Middleware 内部细节，不进入 BIL 文本。

### 3.2 Task 与 eager spawn

async `invoke` 的操作数先按既有 BIL 左到右规则求值。Middleware 随后创建 Task 和
Coroutine、复制/获取已由 P3 共享安全闸门许可的 receiver/参数/捕获、绑定 Executor，
并原子发布 `Created -> Runnable`，最后返回热 Task。函数体异常记录为 Task 失败，绝不
回流为调用表达式的同步异常；实参求值异常仍属于调用方。

`await` 在 Task 已终止时不创建 continuation；未终止时先完整写入 continuation，再原子
登记 waiter 并转为 Suspended。终态与登记须避免丢失唤醒。恢复永远发布到等待者自己的
Executor，并在恢复点取得成功值、重抛保存异常或传播取消。

### 3.3 yield

裸 `yield` 保存 continuation 后执行 `Running -> Runnable`。`yield Alarm` 保存
continuation 后注册 PollingAlarm/EventAlarm 等待；即使 Alarm 已就绪或已触发，也必须
结束当前执行段。PollingAlarm 的 `isReady()` 只能由调度侧同步调用；EventAlarm callback
只发布 waiter，不执行 Rigi 用户代码。

## 4. GC fence 与 frame 发布

把值写入 Coroutine frame、Task 终态、waiter 链、closure 环境或 using 清理记录均可能
建立/移除托管引用。Middleware 必须把引用槽写入、对应 acquire/release 与候选元数据更新
放在 `RUNTIME.md` §23 定义的同一个 ownership region 中。

顺序要求：

1. continuation/frame 的所有根槽先初始化并完成 acquire；
2. 在同一或后续受 fence 保护的 region 内发布 waiter/Coroutine 状态；
3. 只有发布成功后才允许当前 Worker 放弃执行权；
4. 恢复时先原子取得唯一运行权，再读取 frame 与清理/异常状态；
5. frame、Task 终态和清理记录的最后一次释放也走 ownership region。

若进入 ownership region 时 macroGC 已处于 STARTING/PROCESSING，使用运行时内部
`GCAlarm` 走 §23 双重检查；这是不可见挂起，不产生源码/BIL `yield`，但 continuation
仍须保存 Coroutine-owned `cFlag` 状态。

## 5. await/yield/using lowering

### 5.1 Bound 与 BIL

- 新增 `BoundAwaitExpression`：Task 操作数、是否有结果、`TResult?`；P3 只接受精确的
  `core.coroutine.Task` 或 `Task<T>`，必要的子类型视图转换仍由既有 cast 规则先物化。
- 新增 `BoundYieldStatement`：无 Alarm 或已定型的 PollingAlarm/EventAlarm 操作数。
- Lowered 层保持同构节点；发射器生成 BIL §17，不能退化为 `invoke`。
- smart cast 在 await/yield 后清除当前函数的收窄事实；DA 的“已赋值”事实不因挂起而清除。
- `await Task` 只能出现在语句位置；`await Task<T>` 可作表达式或语句（语句位置丢弃结果）。

### 5.2 using

`using` 不引入 BIL 指令。支持语句形态 `seq using(...)`：P3 固化资源局部、
无参 `dispose` 符号和初始化器，P4a 为每个成功初始化的资源建立逆序清理结构，并用
嵌套 `try/finally` 编织初始化异常前缀、正常落尾、`return`、`throw`、`break` 与
`continue` 路径。using 资源槽不可重赋值，async/open/abstract dispose 暂时拒绝，避免
fire-and-forget 或动态派发绕过清理完成语义。`return@`/seq-exit 穿越 using 清理路径
已不再依赖 continuation 编织（Stage B：StructuredExitRouting 的 route local +
dispatcher relay 天然穿越 try/finally，原「表达式 using 与部分终止编织」限制消除）。

普通 `dispose()` 内部可含 BIL `await`/`yield`。当前每个资源独立的 finally 结构把挂起点
留在对应清理块内；Middleware 必须保存当前 dispose 调用和清理进度，外层 return、
异常传播或 Task 终态在清理完成前不得发布。只保守拒绝 async/open/abstract
dispose；完整动态派发与可挂起清理游标不在本节范围。无论如何不新增独立 BIL 清理 opcode。

## 6. lambda 闭包与局部访问器

> **捕获形态以 SYNTAX §5.2 为准**：**被捕获变量一律 Cell 化**（`const` → `ReadonlyCell<T>`，`var` → `Cell<T>`），
> 仅 `this` 与 lambda 自身参数例外（this 作普通字段；自身参数不捕获）。理由：值 wrapper
> 对 get 的代理行为意味着按值拷贝会冻结 proxy 结果。

普通 lambda 和局部访问器采用同一 closure 机制（隐藏类捕获字段 + Cell），不各造一套。
P3 对每个 lambda 记录按符号身份排序的捕获集；捕获在 lambda 求值点经构造函数传入，只
发生一次。实现要点：

- 隐藏类 `..lambda..UUID` 继承 `core::Func`/`Action`/`AsyncFunc`/`AsyncAction`；
  调用走 `invoke.indirect` → 对象虚调用 `$$call`（callable 协议）。
- 被捕获局部/参数存储改 cell（.vars 投影 + 参数 prologue `.c.<名>`）；读写走
  getValue/setValue；BIL `.cell<T>`/`.readonly_cell<T>`。
- async lambda 的 receiver/参数/结果/捕获/泛型实参继续受 shared-safe 五项闸门；
  不共享安全的 cell/捕获在 P3 报错。
- 普通 lambda 允许捕获 local object（仍在当前 Coroutine）；若其后被作为 async
  lambda 的捕获或跨协程发布，P3 必须在发布边界拒绝。
- 循环变量/catch/finally(e)/using 变量被捕获（for 每迭代新 cell；
  catch/finally/using 进入块时构造）。

局部 `var`/`const` 访问器按路线 C 实现：声明即 cell 化，
`override getValue/setValue` 体 = 用户访问器体；自由变量捕获进 cell（init 追加
实参），复用 `ClosureStoragePlan` ClosureField 路径。访问器不能越过其词法
存活期逃逸。async lambda 不另设第二种闭包对象。

## 7. stdlib 与 Middleware native 面

`stdlib/core/coroutine.rg` 是源码可见的类型面，不暴露 Coroutine、frame、waiter 或
GC fence。`Task`/`Task<T>` 是具体 shared class（pub init 冷 Task 构造，
SYNTAX §4.5），Executor 家族为 singleton 门面（RUNTIME §20.1），另有
TaskState/Mutex/Timer 类型与 `sleep`；不以
扩充用户可调用 native 函数为首要前提。

Middleware 必须提供以下保留运行时面；这些是实现接口而非 BIL canonical symbol，也不得
被普通 Rigi `native` 声明伪造：

| 保留面 | 消费者 | 语义 |
|---|---|---|
| `coroutine.spawn` | async `invoke` | 建 Task/Coroutine、绑定 Executor、发布 eager runnable |
| `task.await` | BIL `await` | 终态读取或 waiter 原子登记/恢复 |
| `coroutine.yield` | BIL `yield` | Runnable 重发布或 Alarm 等待登记 |
| `coroutine.complete/fail/cancel` | async state machine | 写 Task 终态并发布 waiter |
| `coroutine.frame` | state machine | 分配、扫描、保存、恢复 continuation |
| `gc.ownership-region` | 引用槽发布/回收 | 执行 RUNTIME §23 双检与 GCAlarm 等待 |
| `alarm.poll/event` | yield Alarm 与 `sleep` | Polling 探测、Event waiter 注册/触发 |

`sleep(i32)` 是 Rigi 层包装：构造内部 `SleepAlarm`，经 `rigi_timer_create`
排程（RUNTIME §19.4）。旧 `make_sleep_alarm` native 面已删除。
`CoroutineLocal\<TValue>` 公开面已定稿（RUNTIME §20.2）：具体 shared class，
实例作进程稳定键；`withValue` 作用域绑定（非裸 set）；`get(): TValue?`
（可选构造默认值；无绑定且无默认 → null）；eager spawn 与冷 Task 启动
默认继承调用方当前有效顶。实现通道是协程句柄上的绑定栈
（`rigi_coro_local_push/pop/get/inherit`）与 VM `VmCoroutine` 同构栈，
不是 OS ThreadLocal。

## 8. 实施顺序与验收

1. P3/P4/BIL：await/yield 定型、挂起点收窄屏障、发射和 verifier。
2. using 语句形态：嵌套 try/finally 的逆序清理，覆盖正常、异常、return/break 与初始化失败。
   复杂 outer value-block continuation、动态/async dispose 与完整清理游标不在本节范围。
3. lambda P3：普通 lambda 参数/返回/符号级捕获与 async 闸门。
4. closure P4 / 对象模型：隐藏类 + Cell 捕获 + `invoke.indirect`→`$$call` +
   值块体降级 + 验证器 §15.3；`.methodid`/`getid.method` 路线已废除。
5. Middleware：async invoke eager spawn、Task/Alarm continuation、GC fence、状态机。

P3/P4 以 Bound/Lowered/BIL 形态和 verifier 测试为主；Middleware 以集成测试验证
Task 终态、Executor 恢复、Alarm、using 清理和 GC fence。BIL VM 对同一 BIL 指令的
执行断言由 VM 专项覆盖。

## 9. 未决风险

- 可挂起 finally/using 与异常 completion 最容易漏掉清理游标或覆盖异常，必须先固化嵌套
  try/finally/return/break 的形态测试。
- 闭包 cell 与 frame 同时持有 rich 值时，重复 acquire/release 或遗漏 root map 会破坏 ARC；
  environment/frame 发布必须统一纳入 ownership region。
- 循环/catch/finally(e)/using 变量捕获的 cell 创建点语义未定；括号形态 void 间接调用
  `(act)()` 仍未定。
- 取消语义已有运行时终态但缺少源码取消 API；仅正确传播既有取消，不新增取消入口。
