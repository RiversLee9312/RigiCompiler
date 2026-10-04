# VM执行、调度与生命周期

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](BIL_VM_DESIGN.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 2. 目录与文件布局

```
Bil/
├── BilVm.cs                      # 入口：装载 BilModule、建 hook 表、启动 main、quiescence 屏障
├── BilComputeInstructions.cs     # 既有家族文件，指令类提供 Execute（见 §5）
├── BilDataInstructions.cs        # 同上；含 direct/indirect 数据与调用形态（见 §6）
├── BilControlFlowInstructions.cs # 同上
├── BilCoroutineInstructions.cs   # 同上
└── Vm/
    ├── VmContext.cs              # 执行期上下文：模块、类型解析、静态字段存储、hook 表、stdout/stderr 汇
    ├── VmDispatch*.cs            # Rigi Dispatcher/Task 的宿主原语与解释器桥，按职责 partial 分文件
    ├── VmCoroutine.cs            # Coroutine：调用帧链 + 块执行栈 + 逻辑状态机
    ├── VmAlarm.cs                # PollingAlarm / EventAlarm 的 VM 表示与注册
    ├── VmException.cs            # 语言级异常的 VM 承载（包装异常对象 VmValue）
    ├── VmHooks.cs                # §22.5 hook 表：rigi_rt 注册表与 Task/Mutex/Any 方法 hook；完整键目录见 §7
    ├── VmTypeSheet.cs            # 逻辑 TypeSheet：拍平 vtable+iMap 与统一方法派发（§3.4）
    └── Values/
        ├── VmValue.cs            # 抽象基类 + 精确标量子类型（见 §3；VmChar 为 Unicode scalar）
        ├── VmObject.cs           # 引用类型实例：运行时类型引用 + 字段字典
        ├── VmEnum.cs             # enum case 身份 + payload
        ├── VmArray.cs            # .array<T>
        ├── VmTypeId.cs           # typeid 值 = 类型符号引用（.generic<T> 同构）
        ├── VmFieldId.cs          # fieldid 值 = 字段 canonical 符号值
        ├── VmSpan.cs             # Span/SharedSpan 缓冲与切片视图
        ├── VmBreakId.cs          # 指向活动 region 的结构化退出 capability
        └── VmWrapperReceiver.cs  # wrapper receiver 与隐藏字段宿主视图
```

`stdlib/core/coroutine.rg`（仓库 stdlib/）承载 Task/Task<T> 的终态与 waiter、
Dispatcher/Executor 的 lane 调度策略；它不属于 Bil/Vm 目录。
`VmContext` 的 Symbols/Members/Indexing/Generics/GenericArguments/Operators/
Initialization/Exceptions/Disposal partial 文件按职责拆开；`VmDispatch` 的
TaskBridge/Await/Coroutines/Workers/Mutex/TimeAndEvents/NativeResources/
StandardInput/FileSystem partial 文件共享同一实例与句柄注册表。
`VmTypeOps` 负责运行期类型/转换，`VmWrapperDispatch` 负责 proxy 链，
`VmDisposal` 负责资源销毁和事件派发。

`Bil/Vm/` 对中端（Semantic/Lowering）零依赖，与 Bil/ 既有纪律一致：BIL 生态
自洽，输入仅为 `BilModule`。

## 4. 执行模型（RUNTIME §17–§19）

### 4.1 显式 Step 循环，真并发 Executor

执行器是**显式 Step 循环**（模拟 CPU），不是递归解释：

- `VmCoroutine` 持有：调用帧链（函数调用栈，帧 = 局部变量槽 + 返回点）+
  块执行栈（结构化 region 的执行状态：指令游标、loop 迭代状态、try 处理表）
  + 逻辑状态（`Created/Runnable/Running/Suspended/终止态`，原子转换，§17）。
  栈是堆分配的数据结构，挂起**不需要快照任何东西**。
- `VmDispatch` 提供后台 `Thread` Worker、队列与 `SemaphoreSlim` transport，
  调度策略由解释执行的 Rigi `Dispatcher`/Executor 决定（**真实多 Worker**）：
  工作项 = 一个 Coroutine 的一段执行。Worker 取到后跑 Step 循环直到该
  Coroutine：`ret`/未捕获异常（终态）、`await` 未完成 Task（登记 waiter，
  转 Suspended，Worker 归还，§18.3）、`yield`（转 Runnable 重新发布，§19）、
  `await` Alarm（登记 Alarm 挂起，ready 后重新发布）。
- async `invoke` 严格按 §18.1 eager spawn：求实参 → 建 Coroutine/Task →
  绑定调用方当前有效 Executor（入口走 Main，冷 Task 可预绑定并在首次启动时选取）→
  转 Runnable 发布 → 返回 Task。**新 Coroutine 可能在 invoke 返回前已被
  另一 Worker 取走**——不以任何全局锁串行化执行。
- **实现级步数上限**（非语言语义，Native 不必提供）：`BilVm.Run(module, maxSteps)`
  与 CLI `vm --max-steps <N>`。缺省不限制（`maxSteps = 0` 或不传）。每执行
  一条 BIL 指令计 1 步（`VmCoroutine.Step` 入口，含嵌套 Step 循环：proxy 链 /
  singleton init / isReady 探测 / wrapper 派发）。超过时抛 `VmStepLimitException`
  （`VmException` 子类，无 ExceptionObject），当前协程 Failed，CLI 将消息写
  stderr 并以退出码 1 终止——受控错误，不是崩溃。非法 N（非正整数）为用法错误，
  退出码 2。

### 4.2 同步与 happens-before

- Rigi `Task.complete/fail/cancel/registerWaiter` 的判定与状态访问由
  `VmDispatch.Await/OnTerminal` 在同一 Task gate（`SemaphoreSlim`）临界区执行；
  gate 的释放/获取建立
  §18.3 要求的「终止前写入对 await 返回后可见」。
- **协程执行单所有者**：`VmDispatch.ResumeSegment` 的「`Runnable→Running` 转换 +
  `SettleAfterResume` + Step 循环」整体在该协程的 `SyncRoot` 锁内；锁外
  只做状态发布（CAS + 入队）。handoff 因此被串行化——持锁 worker 的循环
  退出条件 `State != Running` 只可能由它自己的挂起动作造成（唯一能置
  Running 的转换在锁内），结构上消灭「旧 worker 尚未退出、新 worker 已
  接手」的双执行窗口。`lock` 同线程可重入，Step 内的嵌套 Step 循环
  （isReady 探测、singleton init、proxy 链同步推进）自然安全。
- Task 的 waiter 注册、冷启动判定与 `TrySuspend` 在同一 gate 临界区内，
  避免「终态已发布、waiter 刚登记」的丢失唤醒。`OnTerminal` 在 gate 内
  执行 Rigi 终态迁移并取出 waiter 列表，释放 gate 后再逐个发布；用户协程
  的执行所有权仍由 `SyncRoot` 单独保护，不能以持有 Task gate 代替。
- 唤醒发布统一走 `VmDispatch.Publish`：以 CAS 从 Created/Suspended/Running
  转 Runnable，已 Runnable/终态的重复或 stale 发布视为 benign；随后由
  Rigi Dispatcher 根据最新 Executor lane 入队，队列 transport 保证入队即唤醒。
  不再使用旧 `PublishWakeup` 的挂起纪元协议。Rigi 发布抛错时立即 `Fail`
  留下“调度器丢失唤醒”证据，否则 Runnable 协程会使 quiescence 永久等待。
- 静态字段存储、hook 表 stdout/stderr 写入各自加锁；单次 `print` 调用原子。两路各自以原始字节按调用顺序累积：`stdout_write`/`stderr_write` 不在单次调用边界解码，允许同通道后续调用续写 UTF-8 多字节序列；`print`/`printErr` 将文本编码为 UTF-8 后写入对应的同一缓冲。`BilVm.Run` 在完成调度与事件派发（包括异常收尾）后，从各通道字节快照生成结果文本，各自仅作一次 UTF-8 替换式解码；不得提前显示分片残字节、把两路合并或对另一通道的字节续写。未要求两路跨通道的统一顺序；同一通道的单次追加受锁保护。
- 原子性契约仅到「单次 native print 调用」为止：需要行级原子的包装
  （如 stdlib `Console.println`）必须在 Rigi 层先拼好整行、只发一次
  native print——两次 print 之间 VM 不提供任何不交错保证。调度由真实
  多 Worker 执行，测试可以触发交错，但不能依赖特定交错；行原子性的
  回归防护以「lowering 后只含一次 native print」
  的 BIL 形状断言为主、双协程实跑「每行完整」为辅。
- 跨协程的输出交错顺序是真实非确定性，VM 不做任何排序保证。

### 4.3 try/throw/using

- `throw` 抛出 `VmException`（包装语言级异常对象），沿块执行栈与调用帧链
  逐层展开，按 catch-table 资源匹配、执行 finally。
- `using` 清理已由 P4a 编织为 try/finally 形态，VM 只需正确实现
  try completion 语义，using 清理顺序（§22.2）自然成立。

### 4.4 Run 入口、完成屏障与结果

`BilVm.Run` 先急切初始化 singleton，再执行全局/静态初始化器，随后
选择唯一 entrypoint 或显式 `entryPoint`，并验证入口只能无参数或接收
`.array<.string>`。宿主 argv 的 UTF-16 必须组成完整 Unicode scalar，
校验后构造 `VmArray`；无参数入口忽略 argv。

完整 stdlib 模块通过 `Spawn` 发布 main，调用线程本身成为主 Worker，
解释 `Dispatcher$workerLoop` 至 quiescence；无 Dispatcher 的直建单元测试
模块使用 `RunStandalone`。完成后收集并派发 undisposed 事件，再汇总
main 失败、未观察后台失败和清理派发失败；结果分别返回 stdout/stderr、
返回值与异常。入口解析错误仍可直接抛出；运行期调度错误作为结果异常
返回，保留已经缓冲的输出。步数超限另走 `StopAfterStepLimit` 收尾通道。

### 4.5 运行期资源生命周期

运行期资源生命周期：活动协程由调度登记册强持，终态先登记 `_completedStrong` 与弱项，再摘活动强项；
`destroy` 或 Task 原生资源回收配对摘除终态强项，保护 fire-and-forget 在
OnTerminal→retire 期间不因 AOT GC 提前收簇而使迟到 await/retire 句柄失效；Task 与尚待结算的 waiter 强持结果状态，已观察失败和已消费启动规格撤出登记。VmObject 为通用原生gate保存独立原子所有权快照，终结器仅操作线程安全登记册及 SemaphoreSlim，绝不读解释器槽。ResumeLog 默认关闭，仅测试显式开启。死锁检测以全执行段 active 计数和活动 epoch 校验扫描一致性，包含终态唤醒收尾窗口。
