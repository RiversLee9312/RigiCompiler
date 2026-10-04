# Middleware 协程降级

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

## 6. 协程降级

- 按 [中端 async/清理设计](../semantic/ASYNC_CLOSURES_AND_CLEANUP.md) §7.2模型切
  state：每个可达挂起点的下一条可执行语句为唯一恢复 state；
- continuation frame 内容：state、跨挂起局部/参数/临时/`.return` 槽、活动异常
  /try/循环/using 清理状态、Executor 与 CoroutineLocal 上下文、精确根映射
  （frame 是堆对象，按 refMap 描述，走 §4 的 ARC，无需栈根设施）。当前物理
  frame 是 state + SavedSlots + Tasked 的 $mw.task / Plain 的可选 $mw.result；
  try/循环/using 的续行由 CFG、state 和存活槽表达，Executor lane 与 CoroutineLocal
  绑定栈存放在协程句柄上，随它迁移，不要求为所有逻辑上下文另加 frame 字段；
- eager spawn、Task waiter 原子登记、与 fence 的交互按
  [中端 async/清理设计](../semantic/ASYNC_CLOSURES_AND_CLEANUP.md) §7.2 的 Task/yield/GC fence 节；
- 不用 llvm.coro（理由见 §2）。

### Rigi 调度与 Native 原语的当前分工

协程运行时已从历史「C 侧重实现」转为
「**Rigi 世界重实现 + native 只留原语**」（RUNTIME §17.4）：

- Task/Task\<TReturn\> 完全用 Rigi 实现（`core.coroutine`，具体 shared
  class），与 Dispatcher 交互解决生命周期钩子；Dispatcher 调度逻辑用 Rigi
  实现（runnable 队列、publish/next、quiescence、未观察失败清单、alarm
  集成、Polling 探测表）；Worker = Rigi 世界里 OS 线程的抽象（内部 API，
  不暴露给用户），线程体 = 入口 fn 指针进 Dispatcher 循环；Executor 为
  singleton 门面（RUNTIME §20.1），持 Dispatcher + Worker 配置。VM 与
  native 共享同一份 Rigi 调度逻辑——VM 重构其执行模型配合（Worker = 跑
  Dispatcher BIL 的解释线程，resume 钩子嵌套驱动用户协程栈）。
- **编译侧与公共协议**：CoroutineSplit 状态机（MIR/Emit
  编译侧 stub/resume/frame 合成类型）、checked-flag EH（§8）、ARC/region
  （§4）、alarm 定时原语底座与 libuv 获取链（§2）。
- **运行时边界**：Task 终态、waiter、Executor 队列和 quiescence/drain 已由
  Rigi 实现承担，rigi_rt 只提供 §4.8 原语面（Worker/协程句柄/定时器/同步
  Mutex/TLS/时钟/失败注册表）。VM 的宿主 hook 只承载平台能力与嵌套解释执行，
  不另保留一份 C 风格 Task/Executor 调度实现。
- **生成代码交互点**：spawn stub → 建 Task 对象 + 协程句柄 +
  Dispatcher.publish；await → Task 方法返回决策码（快路径读终态/冷启动/
  登记 waiter），Suspend 码才走编译侧挂起；DONE → Task.complete/fail
  Rigi 方法；yield → Dispatcher 重排当前任务。
- **冷 Task**（RUNTIME §18.4）：spawn-into 复用 Task 对象建协程，Task↔
  协程 1:1；`run`/`executor` 预设与换绑、TaskState 投影由 Rigi 侧 Task
  方法承载；多 Worker 由 Rigi Dispatcher 配置与 Native Worker 原语提供。
  Task 内部 cancel 决策存在，但 await 的 CANCELLED 分支仍防御不可达，不把它
  宣称为完整公开取消 API。body 绑定双通道
  （CoroutineSplitPass 棒5a）：构造点静态类型具体 → `$mw.coldtask.*`
  工厂预建 frame/句柄（`$$call` 沿 extends 链解析，frame/resume 取
  声明宿主的 split 产物，与 VM `FindCallTarget` 拍平 sheet 含继承槽
  同语义）；静态类型不透明（AsyncAction/AsyncFunc 槽）→ 保留真 init，
  启动时 `bindColdBody` 改写为 type.is 链 + `$mw.bindcold.*` 动态绑定。
  布局前的 `ConstructedCallCollector` 沿已到达 Task 的实际 body 字段继续
  收集闭合调用类型；该调用尚未出现在默认 BIL 方法体中，不能仅扫描该体。
  同样，属性读取须按访问器规则分析 getter，才能覆盖其中创建的泛型对象。
  两者均沿实际调用与存储事实传播，不因类型出现就扫描其全部成员。

### stub、resume、frame 与跨普通调用挂起

- **stub/resume 分工**（CoroutineSplitPass，在 wrapper/singleton/内建分派和
  wrapper self 归一之后、RcInjection 之前）：async fn 拆为 spawn stub（原符号，
  调用点仍调用它）：new frame → 参数/类级 typeid 落 frame 字段 → new Task
  （泛型任务使用真实构造身份）→ frame.$mw.task 回挂 → MirCoroutineCreate 建句柄
  → inherit CoroutineLocal 有效顶与调用方 lane → Task.attachRuntimeNative
  等 Rigi 方法写入运行时 → Dispatcher.noteSpawn/publish
  → ret 热 Task。合成 resume fn 为 `$mw.resume.<fn canonical>`，MIR 签名
  frame 胖引用 → i32，entry `switch(frame.state)` 分发；state 0 为原入口，N 为
  各挂起点恢复块。await 在 Task.gate 临界区内检查 hasRuntime，冷任务 tryStart
  的赢家 spawnIntoLocked/noteSpawn/publishRuntime，随后保存活跃槽、state=N，
  registerWaiter 返回 0/1/2/3 决策，release gate 后四路 switch：已登记挂起
  （ret SUSPENDED=0）/成功读 nullable result 解包续行/失败按 failureNodeId
  经 MirFailureLoad 取异常沿原 ExcTarget 重抛/CANCELLED 防御性 MirUnreachable。
  waiter 登记与完整 frame 保存必须在同一临界区，不能先让另一 Worker 恢复半写 frame。
  恢复块重回 wait，再走终态快路径。裸 yield 保存 frame 并调用 Dispatcher.publish
  重排，ret YIELDED=1；成功出口写 Task<T>.result，gate 内 complete 与 waiter
  排空，锁外 publishAll/noteTerminal，再 MirCoroutineDone + ret DONE=2。失败
  传播垫用 MirTakePending 移走异常，经失败注册表持有并写 failureNodeId，gate
  内 fail 排空 waiter，锁外发布，最终 DONE；RcInjection 释放全部托管槽与
  frame。未捕获异常归宿 Task FAILED，不跨协程帧传播；pending 通道沿用 §8。
- **frame 合成类型**（SyntheticTypePlanner）：内部 class
  `$mw.frame.<fn canonical>`（state i32 + 保存槽平铺、托管槽进 refMap、
  vtable 仅槽 0 init 分发器占位），pass 期注册进 Symbols + Layout 计划表，
  Emit 的 TypeSheet/refMap 发射零特例消费；frame 经 rigi_alloc 分配（零
  初始化由 memset 承担，RcInjection 不变量依赖）。
- **Emit**（CoroutineEmitter + ModuleBuilder）：普通 MirCall 交 CallEmitter
  消费；专用节点为 MirCoroutineCreate（函数指针物化与 frame payload）、
  MirFailureLoad（失败表到 out 胖值）、MirCoroutineDone（纯终态标记，无机器
  指令）、MirResumeCall（plain 下钻）。Emit 不插 acquire/release，move 由
  RcInjection 配平。resume 有意特判 C ABI `i32(ptr)` 匹配 RigiResumeFn，
  entry prologue 把裸 ptr 重构为 frame 胖引用落 `$mw.frame` 槽；probe 不使用
  此 native callback ABI。开放 Task<T 占位> 不能回退无元数 Task sheet：两种
  Task 的真实字段/gate 偏移不同，TaskTypeSymbolOf/GenericTaskSheetIdentity 保留
  泛型构造身份，切分引入的新闭合 Task<R> 经 ConstructedLayout 补布局；声明形
  Task<TReturn> 归一为占位构造而非用类型参数名伪造具体 sheet。
- **入口与 MainExecutor**：rigi_entry 对齐 BilVm.Run——singleton 急切初始化
  → DAG globals.init → main；同步 main 直接调用，pending 收进 entry.exc
  （不立即报告）；Dispatcher.workerLoop(0) drain 至 quiescence，fire-and-forget
  也等待，随后 rigi_main_worker_shutdown。无任务的 workerLoop 在 quiescent
  快检后返回。失败汇总仍是 main 失败优先于未观察失败，走 typed reporter。
  tainted main 的 stub/settle 见下一段，main argv 桥接根见 [布局与 ABI](LAYOUT_AND_ABI.md)。
  旧 rigi_root_begin/end、rigi_executor_run、rigi_spawn/task_wait/complete/fail/yield
  面及旧 MirSpawn/MirTaskWait/MirTaskComplete/MirTaskFail/MirYieldCall 节点已删除。
- **非 async fn 挂起点全链支持**（B-1 起，VM 栈式跨界语义对齐）：
  taint 分析沿调用图反向标记「可达挂起点」fn（tainted）；tainted
  普通 fn 状态机化（裸 frame：`state` + 保存槽 + `$mw.result`，无
  Task 包装，frame 所有权归调用方；resume fn `IsPlainResume`，传播
  垫尾 release + ret FAILED(3) 沿链上传）；tainted→tainted 直调
  改写为建 callee frame + 落参 + `MirResumeCall` 原生栈下钻 +
  四码分流（SUSPENDED/YIELDED 上传 / DONE 读 `$mw.result` 续行 /
  FAILED 取 pending 沿原 ExcTarget 重抛）；tainted main 走 Task
  包装 split + `$mw.main.settle`，rigi_entry 改「调 stub 发布主
  协程 → drain → settle 取结果/重抛失败」（对齐 VM `BilVm.Run`
  的 main 协程化）。
- **B-2 全组合收口**：① taint 传染边全集 = 直调 / super（恒直调）
  / 值类型宿主运算符（直调形态）/ 虚·interface·class 运算符派发
  （闭包内任一实现 tainted 则整点升级）/ new init。② 虚/interface
  派发挂起点 = 调用点动态分流：闭包全类臂（最深派生优先——臂条
  件 type.is 是子类判定，浅类臂不得遮蔽深类），tainted 实现臂走
  直调协议（每实现一套 frame/落参/调用块；callee 槽在臂内建后回
  存本层 frame——head 的 emitSave 先于分流执行），非 tainted 实
  现臂与默认臂（闭包外/null 接收者）落原调用（普通虚派发，NRE
  语义保持）；恢复经同序恢复分流链直落调用块（frame 不重建）。
  ③ super 调用与直调运算符同直调协议。④ 含挂起点的 init：构造
  点分配与 init 下钻分离——head 用合成空 init 分配（init.wrapper
  原位缝合字段初始值）→ Target 槽先落定 → init frame（.this =
  新建对象）下钻；DONE 直落原后继（结果即 Target 槽）。⑤ §7.2
  隐藏参数落参：实参按「形参剔除类级 typeid」位序 zip；被剔除
  的类级 typeid 按宿主构造形态合成（闭合实参 → MirGetTypeId 常
  量；外层占位 → 调用方同名 .generic.* 局部转抄；值类型接收者被
  cast/copy 剥成裸模板时沿产出链回溯构造形态）。⑥ plain fn 内
  yield Alarm 放开：恢复闸失败尾 plain 分叉（pending 保持置位
  ret FAILED 沿链上传，对齐 VM 帧栈逐层展开；Tasked 仍走 Task
  FAILED 终态序列）。  ⑦ using dispose 可挂起（§17.3）：dispose
  虚派发臂同②协议化。R2 残留边界清偿：⑧ 泛型宿主虚派发链
  挂起点——臂条件 type.is 扩展为「模板空壳 + 模块内全部闭合
  构造 sheet」OR 链（泛型实例头是构造 sheet，其基链不含模板
  空壳，单模板键判定恒 miss；开放占位 new 的实例携模板空壳
  故模板键保留首位）；tainted 实现的类级 typeid 落参改从接收
  者实例隐藏 typeid 字段（#..generic.）运行期读取（class 宿主
  对象头恒藏构造实参 typeid，静态构造形态被接收者 cast 剥成
  裸模板亦不影响）。⑨ $$call/invoke.indirect——callable 协议
  闭包按②同机制动态分流（接收者 = CallTarget；静态目标 =
  BindIndirectCall 沿 extends 链解析的 $$call 成员）；闭包枚
  举改布局计划表直查 + PlanKey 派生判定（同名不同元数模板
  canonical 撞键——core::Func\<1\>/Func\<2\> 共享裸键，字符串
  查询口径实证槽表错配致闭包为空）。⑩ new.indirect × tainted
  class init——分发点本身成为调用方挂起点：实证 native 槽 0
  分发器不继承 init（派生类无自声明 init 时 new.indirect 抛
  NoSuchMethod，双端一致），故臂条件 = 精确 sheet 匹配
  （MirTypeCheckKind.IsTypeId 原值直判 ∧ 派生物化 sheet 排除
  链）；命中臂以臂构造形态空 init 分配（init.wrapper 原位缝
  合）→ Target 槽落定回存 frame → init frame 下钻，DONE 直落
  原后继；相关性按「站点静态实参形 ↔ 重载逐物化 sheet 代入
  形参」精确判定（argc + canonical 恒等，镜像分发器 ArgToken
  匹配语义）。⑪ MirNewObject 补 try 异常边（CallVisitors 降
  new 时填充 CurrentExcTarget——历史「pending 推迟到下一检
  查点」形态消除；init 下钻 FAILED 沿该 fn 的 try 异常边走，
  同 fn try 内挂起 init 抛出双端捕获点对齐；同步 init 抛出同
  获立即传播语义）。保留的受控拒绝边界（消息文本已改准确 +
  MiddlewareTests 负例钉住）：proxy/wrapper 烘焙链
  （$.wrapped./$.mwrapped./$mw. 前缀合成 fn）可达的 tainted fn
  ——router/trampoline 通配 ABI 的值包转发形态无挂起协议插
  点，运行期目标集随 wrapper 实例符号表动态决定，静态闭包不
  可枚举；含挂起点的值类型 init——值类型构造路径无挂起协议
  （frame .this 借用形态与 sret/原地构造不兼容；此前静态 new
  形态静默语义错位——native exit 5 无输出 vs VM 正常——补
  闸）；泛型占位实参的 new.indirect × tainted init（实参
  sheet 运行期物化，匹配不可静态判定）；实参与可见形参不对
  应的未知隐藏参数形态；嵌套占位构造的类级 typeid 实参。
- Alarm、多 Worker 与队列一致性由 Rigi Task/Dispatcher 方法和 Native 原语共同
  保障：Task 终态/await 使用同一 gate，Dispatcher 自持同步锁；C11 原子用于
  句柄身份、lane 和平台状态。旧 coroutine.c 调度实体已删除，不能再引用其锁。

### Alarm、Polling 探测与定时底座

- **yield Alarm 全链**：BIL→MIR 直译 MirYieldAlarm，仍不区分 Polling/Event；
  CoroutineSplit 保存 frame 与 state=N，再物化 PollingAlarm sheet 并发射
  MirTypeCheck 分流。PollingAlarm 调 poll_arm 复位退避，并经 Dispatcher.publish
  自重排，ret SUSPENDED；恢复块的 poll_pending 判位决定先探测还是续行。
  EventAlarm 先调用 ensureHandle 懒建底座，再 rigi_alarm_wait 闸内登记 waiter；
  未触发则等待，已触发即自重排，但均结束当前执行段。Event 恢复不重新登记，
  与 await 恢复重回 wait 的模式不同。旧 MirYieldAlarmCall 四参数面已删除，
  probe 与分类不再藏在 C 面中；类型身份仍由 getid.type/Layout 正确物化。
- **probe 合成 fn**（`$mw.poll_probe`，模块级懒建一次，$mw.named.lookup 先例）：
  MIR 签名 alarm 胖引用 → i32，体为 PollingAlarm.isReady 虚调用及 bool 双分支
  转 i32（1 ready/0 not）。IsPollProbe 使 RcInjection 的传播垫 release 配平后
  ret -1、pending 保持置位；恢复块直接调用 probe，返 -1 后沿 yield 的词法
  失败边取 pending：Tasked 完成 FAILED 终态，await 点重抛；Plain ret FAILED(3)
  沿调用链上传。probe 是普通 Rigi MIR 函数，保留实际 typeid 的胖引用参数，
  IsProbeAlarmParam 保持借用参数约定：入口不 acquire、出口不 release，其余
  托管局部仍由 RcInjection 配平。C 不回调 probe，也不再以 C 登记项 +1 和
  i32(ptr) 特判解释 alarm 生命周期；resume 中的 owning alarm 槽及保存到 frame
  的槽负责跨调用/挂起保活。
- **Phase 2.6 探测双路径**（§19.2 语义纠偏，2026-09-12 设计决策）：
  isReady 允许含挂起点——PreparePollProbeSites 对每个 yield-alarm 点
  判定 isReady 虚调用闭包，含 tainted 实现时该点走**恢复块站点协议
  臂**（EmitPollGate tainted 分支）：probe 块按 alarm 运行期类型分流
  （type.is 深→浅 + untainted 联合尾，miss 防御不可达）→ 建探测
  frame + MirResumeCall 调 isReady 状态机；探测中途挂起（SUSPENDED/
  YIELDED）写**探测挂起子状态 ProbeState**（entry switch 追加的专用
  恢复入口，直落探测调用块下钻续跑）+ ret SUSPENDED；DONE 读
  callee $mw.result 做一次性就绪判定（true → poll_clear + 续行，
  false → state 写回 N + poll_schedule 退回等待）；FAILED 沿 yield
  点词法 try/catch 失败尾。全 untainted 闭包保持 $mw.poll_probe 同步
  廉价路径（probeId 直调 + -1/0/1 三路 switch），两路径可观察语义
  一致（对齐 VM ProbePolling 恢复式探测）。原 76e304c 对 #08 的
  RejectTaintedPollingIsReady 受控拒绝随本节撤销。
- **可达性**：带 Alarm 的 yield 在 BIL 级收编 isReady 虚调用闭包
  （MirReachability：静态目标 + 全部 override 后代——probe 是 MIR 期
  合成，BIL 可达性不可见）；两分类 sheet 由 Layout 全类型计划覆盖，
  无需显式收编。
- **Dispatcher×uv 集成**（stdlib/core/coroutine.rg + rigi_rt worker.c/cohandle.c）：
  sleep 创建源码声明的内部 SleepAlarm 子类，Timer/SleepAlarm 通过 timer_create
  注册时钟原语；不再由 C 动态合成类型或在 rigi_yield_alarm 时补 baseTypeId。
  用户 EventAlarm 直继子类由 ensureHandle 在 handle==0 时懒建 sticky 底座。
  检查 signaled、登记 waiter 和触发使用原子握手（RUNTIME §19.3）。每 Worker
  自有 uv loop，Rigi workerLoop 的 park 经 Native uv 底座等待唤醒/定时器；
  worker_park 在连续 runnable 时先 UV_RUN_NOWAIT 泵一次已有 loop，防定时器饥饿；
  无令牌时按已有 loop 使用 UV_RUN_ONCE，非主 Worker 无 loop 则等 semaphore，
  主 Worker 按需懒建 loop 与死锁看门狗。Polling isReady
  在该协程恢复块执行，cohandle.c 的退避 timer 回调只重发布，不执行用户代码；
  退避为 1→32ms 指数。quiescence 由 Rigi 的 live 与各 lane 队列共同判定，
  live>0、无 runnable/alarm 则按死锁规则受控失败，不能把它当成功 drain。
- **probe 失败路径 frame 释放**：FAILED 终态后不会再正常恢复，必须经过与
  普通协程失败一致的终态序列和 RcInjection 释放；不能等待一个永不执行的
  正常 DONE 后继。历史外部探测轮代释放曾修复 32B 泄漏，但当前探测位于
  resume 恢复块，直接沿 Tasked 失败 DONE 标记或 Plain FAILED 返回进行配平。
  frame 的所有权仍区分 Tasked 最终释放与 Plain 由调用方持有，禁止重复释放。

当前 Polling 退避 timer 属调用线程附着的 Worker；已有活动 timer 的跨线程
清理（如换 lane 后在另一 Worker 终态）尚无受支持的所有权迁移协议，运行时
明确 abort，不能从一般 Task.executor 换绑能力推断这种平台资源组合也已支持。

相关章节：[布局/调用 ABI §7](LAYOUT_AND_ABI.md)、[ARC §4](MEMORY_MANAGEMENT.md)、[runtime 面 §4.8](RUNTIME_ABI.md)、[异常 §8](EXCEPTIONS.md)、[工具链选型 §2](TOOLCHAIN_AND_CACHE.md)。
