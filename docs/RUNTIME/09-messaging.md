# §27 消息传递（MessageQueue / Reader / Receiver / Messenger）

> MW11d 落地。本章描述消息传输的运行时模型与语言层高层 API。
> 序列化证明体系（@Serializable/@Temporary/Parcel）见 SYNTAX §20；
> 协程/Executor/Task 底座见 §17–§20。

## 27.1 分层总览

```text
VM / Native：仅执行安全 Atomic 容器与协程基础原语
    ↓
MessageQueue      mechanism：句柄生命周期、capability 校验、深复制、
                  broadcast 存储/cursor、post/next 异步等待、EOS
    ↓
Reader            接收侧 canonical pull 抽象
    ↓
Receiver          Reader 的 push View + listener 派发 policy

Messenger         发送 capability 的语言层对象（持 Owner）
```

核心原则：队列日志、capability、cursor、EOS 与 listener/Executor 管理均由单份 Rigi 标准库实现（`stdlib/core/messaging.rg`），没有 MQ 专用 native/VM 注册表。
高层策略（bounded/priority/drop/batch/debounce/ack）**故意不实现**——
未来需要时在 Rigi 层组合，native 不做通用 broker。

## 27.2 MessageQueue 传输模型（五原语）

`core.messaging.MessageQueue`（Rigi 无 static class，以 pub class + 仅静态
方法近似）的公开面即交接五方法：

```rigi
MessageQueue.create_queue\<T with Serializable>(): QueueHandle\<T>
MessageQueue.add_queue_handle\<T with Serializable>(source, type): QueueHandle\<T>
MessageQueue.release_queue_handle\<T with Serializable>(handle)
MessageQueue.post\<T with Serializable>(handle, item)        // async
MessageQueue.next\<T with Serializable>(handle): Task<QueueItem\<T>>  // async
```

QueueHandle\<T> 是直接 shared 对象 capability。构造与状态均为私有，
没有可读写 id/type；释放时断开 queue 链接，旧对象不能复活队列。
`QueueItem\<T>`：`isEos: bool` + `item: T?`——EOS 只由 `isEos` 判定
（T 可能允许 null，不得用 `item == null` 判 EOS）。

post 先深复制，安全 AtomicList 再保存独立消息；每个 next 取得独立复制。
默认拒绝循环引用，MQ 不隐式开启 graph mode。所有状态改变在同一队列
Mutex 临界区线性化，普通 func 也可 await，五 API 签名保持不变。

消息日志由有界 AtomicList 段组成。reader 保存绝对序号和当前段缓存，
跨段时向前推进，branch 从当前队尾开始。最慢 live reader 的水位越过
整段后立即断开旧段；释放 reader 同时清除其缓存，全部排空后清空段链。
分段避免单个巨大 backing 数组因临时引用释放而反复超过 GC 候选门槛，
不改变 GC 规则或消息复制边界。

### 27.2.1 capability 矩阵（Rigi 单份校验）

| 源句柄 | 派生 Owner | 派生 Sender | 派生 Reader | post | next |
|---|---:|---:|---:|---:|---:|
| Owner | ✗ | ✓ | ✓ | ✗ | ✗ |
| Sender | ✗ | ✓ | ✗ | ✓ | ✗ |
| Reader | ✗ | ✗ | ✓ | ✗ | ✓ |

违规一律抛 `core.IllegalStateException`（双端同文，错误文本由 Rigi 包装层
统一给出）。其它句柄安全不变量（§22 落点）：句柄类型与操作匹配检查；
已释放句柄不可用；重复释放确定性失败；Owner 唯一（`create_queue` 返回
唯一 Owner，不可再派生）；sealed 队列不可再派生 Sender；句柄泛型消息
类型与底层队列一致（类型系统在调用面强制）。

### 27.2.2 生命周期与 EOS

没有 `destroy_queue`：队列生命周期由句柄引用计数管理，最后一个句柄释放
时回收运行时存储。**sealed（生产侧永久封闭）条件**：

```text
Owner 已释放 且 Sender 数量 == 0   →   OPEN → SEALED（不可复活）
```

sealed 后：不允许 post、不允许派生 Sender；已接受的消息继续存在，每个
Reader 独立 drain；Reader 消费到自己应见的队尾后 `next()` 返回 EOS。
不同 Reader 的 cursor 独立，EOS 观察时点各自不同。

### 27.2.3 Broadcast 存储与 cursor

底层是 multi-sender / multi-reader / broadcast：append-only 消息日志 +
每 Reader 独立 cursor。一个 Reader 的 `next()` 不消耗、不推进其它
Reader。迟来 Reader（branch/新建）从队尾起订。消息回收由最慢存活
Reader 的 cursor watermark 决定；Reader 释放 = 不再要求队列为它保留
历史。物理日志采用绝对 base/head/tail 与每段最多 64 条消息的 AtomicList 段链。
最慢存活 Reader 的水位越过整段后，迭代断开已消费段；Reader 跨段时更新缓存，
释放时清除缓存。完全 drain 或无 Reader 时清空段链，不逐条删除队首。

### 27.2.4 顺序与接受点

- **全局追加序**：每个队列建立唯一 enqueue 全序，所有 Reader/Receiver
  观察相同顺序；并发 sender 按各自 post 的线性化点先后入序。
- **接受点（§17）**：`await post/send` 完成 = 深复制已入队可见
  （transport-level acceptance）。不等待任何 Reader 读取、不等待任何
  listener 执行。

### 27.2.5 单 outstanding next

同一 Reader 句柄同时最多一个 outstanding `next()`；违规抛
IllegalStateException（同一 Reader 本质是顺序 cursor；要并发独立消费
请 `branch()`）。由队列锁保护的 reader.inNext 登记；异常与 release 路径均受同一锁协调。

### 27.2.6 VM / Native parity（§26 口径）

双端在以下可观察行为上逐字节一致并有 NativeE2E 对拍钉死：capability
校验与错误文本、Owner 唯一、派生规则、post 接受点、broadcast 顺序、
Reader 独立性、branch 语义、release 后行为、EOS 条件、深复制快照、
listener 最终看到的消息顺序。固定 wake Mutex 保证挂起的 next 在句柄释放时被唤醒走错误路径退出。

## 27.3 Reader（接收侧核心抽象，§12）

```rigi
pub shared class Reader\<TMessage with core.serialization.Serializable>
    implements core.IDisposable {
    pub init(_ -> handle)                        // 包装既有 Reader 句柄
    pub async func next(): QueueItem\<TMessage>
    pub func branch(): Reader\<TMessage>          // 同队列新独立 cursor
    pub func createReceiver(): Receiver\<TMessage> // = Receiver(this.branch())
    pub override func dispose()                   // 只释放自己的句柄（幂等）
}
```

- `branch()` 不是复制源 cursor——经 `add_queue_handle(Reader)` 建立新的
  独立订阅（迟来从队尾起订）。
- `createReceiver()` 不得消费源 cursor（§13.1）。
- dispose 不级联：不关闭 Messenger、兄弟 Reader、branch 出去的 Reader、
  或它 createReceiver 得到的 Receiver（§12.2）。

## 27.4 Receiver（Reader 的 push View，§13–§15）

```rigi
pub shared class Receiver\<TMessage with core.serialization.Serializable>
    implements core.IDisposable {
    pub init(reader: Reader\<TMessage>)   // 包装并立即起泵（让渡所有权）
    pub func addListener(callback: core.AsyncAction\<TMessage>)
    pub func removeListener(callback: core.AsyncAction\<TMessage>)
    pub func setExecutor(callback: core.AsyncAction\<TMessage>,
        executor: core.coroutine.Executor)
    pub func getExecutor(callback: core.AsyncAction\<TMessage>)
        : core.coroutine.Executor
    pub func createReader(): Reader\<TMessage>   // = internalReader.branch()
    pub override func dispose()
}
```

- 内部持独立 Reader + pump 协程：`next()` 循环 → listener 表查找 → 按
  listener 的 Executor 派发（冷 Task + `run(executor)` 既有通道）；
  EOS 或 dispose 停泵。dispose 与挂起 next 的竞态：reader 句柄释放会
  signal 挂起点，pump 捕获「句柄已释放」IllegalStateException 静默退出。
- **listener identity（§14）**：callback 对象自身即身份。统一擦除为 Object
  视图后，用临时 Place 相等比较实际 target；所有 Place 经 seq using dispose。
  重复注册幂等（同一 callback 只登记一次）；removeListener 未注册为
  无操作；setExecutor 未注册抛 IllegalStateException；getExecutor 未注册
  返回默认 IOExecutor。
- **Executor 路由（§15）**：每 listener 独立 Executor，**默认写死
  IOExecutor**（不做「捕获注册时 Executor」）。listener 明确承担持续
  CPU 重活时 `setExecutor(l, new ComputeExecutor())` 显式换。
- **native 层绝不直接执行 callback**（§15.1 架构天然满足：native 只见
  callback 只经 Rigi 层 Task + run(executor) 执行）。
- 生命周期隔离（§13.2）：dispose 一个 View 不级联兄弟——Receiver.dispose
  只释放内部 Reader；它 createReader 出去的 Reader 在队列活着时继续工作。
- 并发纪律：listener/Executor 表可被多 lane 触碰（listener 在 IO/Compute
  lane 跑、用户在 Main 调 addListener）——表访问经标准 Mutex 临界区保护，派发在
  临界区外做快照后逐个起冷 Task。

## 27.5 Messenger（发送 capability 对象，§16）

```rigi
pub shared class Messenger\<TMessage with core.serialization.Serializable>
    implements core.IDisposable {
    pub init()
    pub async func send(message: TMessage)   // = post 语义（§17 接受点）
    pub var receiver: Receiver\<TMessage> { get }  // 懒建（Owner 派生 Reader
                                                   //   直接让渡给 Receiver）
    pub func createReader(): Reader\<TMessage>     // Owner 派生 Reader
    pub override func dispose()   // 释放 Sender+Owner → sealed → drain/EOS
}
```

**单向 capability（§16.1）**：Reader/Receiver 没有任何派生 Sender/
Messenger 的 API——结构性保证（无相应方法，编译期拒绝；e2e 负例
`mw11dd_reader_no_create_sender` 钉死）。dispose 不级联已派生的
Reader/Receiver（它们持自己的句柄）。

## 27.6 对象身份

listener 比较只使用安全 placeOf 和 Place equality，不暴露地址、不生成整数身份键。

## 27.7 与协程底座的复用（交叉引用）

- pump/listener 复用 Task.run(executor)，默认 IOExecutor，每 listener 可覆盖。
- 空 next 在队列锁内占用 reader 的固定 wake Mutex 并保存令牌，解队列锁后
  再 acquire 挂起。post/seal/release 在队列锁内摘除令牌并释放，早于等待
  的 signal 也不会丢失；恢复后释放二次 acquire 的令牌并重新检查队列。
- 同一 Reader 只有一个 outstanding next，多个 post 不会重复释放同一令牌。
- listener 表以标准 Mutex 保护；锁内复制 callback/executor 快照，锁外派发。

## 27.8 非目标（§29 落点）

当前实现明确不包含：bounded queue、priority、actor 框架、request/reply
RPC、自动重试、listener ack、持久化、durable broker、分布式路由、任意
serializer 插件、运行时反射 serializer、Temporary closure wire 编码、
新 Sendable/Transportable 标记、listener token 抽象、native Receiver、
native Executor policy。

## 27.9 泛型基础设施的两处平台注记（移交说明）

MW11d-D 落地时钉死的两个泛型运行时限定点（对库作者透明，改协程/泛型
发射时注意）：

1. **泛型宿主内的 lambda 不可作冷 Task body**：lambda 隐藏类被外层 GP
   参数化后，spawn-into 的 bindColdBody 链无匹配；且 stdlib 切片里的
   隐藏类符号在多重合并/往返下撞名。messaging.rg 的冷 Task 壳以手写
   `AsyncAction` 子类（显式字段承载、非泛型）规避。
2. **裸模板 new 的隐藏 typeid**：泛型 fn 内 `new Reader\<TMessage\>` 的
   构造实参代入为空（模板 canonical），隐藏 typeid 取当前 fn 的
   `.generic.*` 局部（VM/new 发射双端同口径）。

另：async 闸门 2（调用点实参共享安全）对裸泛型参数实参与 receiver/
泛型实参同口径跳过（声明侧不可判，构造点 `CheckInstantiationLimits`
重跑代入检查兜底）——泛型 async 基础设施（send→post、dispatch→
listener 的 GP 值转发）经此通道组合。

## 27.10 可选压力入口与观测口径

运行 pwsh tools/Run-MqStress.ps1 显式执行压力测试，默认四个 sender 各 62500 次 post、四个独立 reader，共 250000 次受理和 1000000 次 delivery。每个 reader 校验各 sender 连续序号，最后逐项比较四份完整顺序，排空后再次验证 EOS。使用 -PerSender 500 可缩小为 8000 次 delivery；百万负载不会进入 test --all。

pwsh tools/Run-MqStress.ps1 -Resources 单独验证确定积压：10000 条各自动态构造的约 1 KiB 字符串消息，快读者读取 6000 条后释放慢读者，验证部分 compact 后余下 4000 条，随后完全排空。采样后仍操作已释放 capability，保证回收观测时这些对象确实保持存活。五轮队列分支、seal/release、双 listener 与 executor 覆盖、view 释放隔离均有行为断言，运行期 gate 数量与字节台账另有回收断言。

入口默认先 build，以更新内嵌标准库和 C 运行时；-UseExistingBuild 仅供明确已构建的验证阶段。计时和 RSS 只覆盖最终 exe 执行，编译不计入；watchdog 关闭 stdin 并管理完整进程树。任意 stderr 或非零退出都视为失败，台账与测量写入 playground。报告的是批次耗时、delivery 吞吐与进程峰值 RSS，不声称单消息 p99；运行期 live bytes/gates 与退出 MEMTRACK 分别验证，不以 RSS 代替日志回收证据。
