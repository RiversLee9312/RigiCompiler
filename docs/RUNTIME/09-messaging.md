# §27 消息传递（MessageQueue / Reader / Receiver / Messenger）

> 序列化证明体系见 SYNTAX §20；协程/Executor/Task 见 §17–§20。

## 27.1 分层总览

消息队列完全由 stdlib/core/messaging.rg 的 Rigi 对象实现。私有
MessageQueue 封装日志、游标、深复制、等待与 EOS；私有 MessageQueueReader
实现公开的抽象 Reader。Messenger 负责发送侧封装，Receiver 只组合
Reader 和 listener 派发，不操作队列内部状态。VM / Native 仅提供
通用 Atomic 容器、同步与协程原语，没有 MQ 注册表。

## 27.2 MessageQueue 传输模型（实例方法）

MessageQueue\<shared T with core.serialization.Serializable> 是私有 shared
实现类，通过 post、createReader、next、closeReader、close 实例方法
管理队列。不存在 QueueHandle、QueueHandleType、QueueEndpoint 或静态
队列句柄 API，外部代码只能使用 Messenger、Reader、Receiver。

QueueItem 的 isEos: bool 单独标识结束，item: T? 承载消息。不能以
item == null 判定 EOS，因为消息类型可以允许 null。消息泛型同时满足
显式 shared 和序列化约束，用户与标准库适用同一规则。

### 27.2.1 操作权限与封装

发送只能通过 Messenger；Reader 只有读取、分支、建立 Receiver 和销毁
操作。私有实现状态不从公开签名泄漏。泛型身份严格保留，不能因为布局
相同就转换不同消息类型，也不能转换成裸泛型模板。

已销毁 Reader 的读取、分支抛 IllegalStateException；重复 dispose 幂等。
关闭后的 Messenger 不允许建立新 Reader/Receiver 或发送消息。

### 27.2.2 生命周期与 EOS

Messenger.dispose() 关闭生产侧：OPEN → SEALED，不可重新开启。已有
Reader 各自排空已接受消息，随后每次 next() 都返回 EOS。关闭 Messenger
不销毁已有 Reader/Receiver，它们仍须分别 dispose。Rigi 对象引用管理
队列存储内存，没有额外 native 句柄计数。

### 27.2.3 Broadcast 存储与 cursor

每个 Reader 有独立游标。新建与 branch() 均从当前队尾起订，不复制源
游标；已有 Reader 可在关闭生产侧后 branch，新分支立即观察 EOS。
日志采用每段至多 64 条消息的 AtomicList 段链。最慢存活 Reader 的
水位越过整段后断开旧段；释放 Reader 清除缓存并重算水位。全部排空
或没有 Reader 时清空段链，后续发送保持绝对序号连续。

### 27.2.4 顺序与接受点

每个队列在同一 Mutex 内建立唯一追加顺序，所有 Reader 观察相同顺序。
await send 完成表示快照已接受，不等待读取或 listener 执行。
入队前深复制，每个 Reader 读取时再次深复制，避免发送源与各读者之间
产生消息别名。默认拒绝循环引用，不隐式启用 graph mode。

### 27.2.5 单 outstanding next

同一 Reader 同时最多一个未完成 next()，重复请求抛 IllegalStateException。
要独立并发消费应 branch。等待登记、异常退出、销毁与唤醒均受队列锁
协调；Reader 销毁会唤醒挂起读取并走错误路径退出。

### 27.2.6 VM / Native parity

双端执行同一 Rigi 队列实现，约定相同的广播顺序、快照隔离、分支、
接受点、销毁、错误文本与 EOS。端到端用例覆盖多段积压、水位回收、
挂起读取销毁、重复 next、关闭排空及跨执行器多生产者广播。

## 27.3 Reader（接收侧核心抽象，§12）

Reader\<shared TMessage with core.serialization.Serializable> 是 public
shared abstract class，实现 IDisposable。抽象操作为 async next()、
branch()、dispose()；具体 createReceiver() 包装 branch()，不消费源
游标。内置实现只能通过 Messenger 或现有 Reader/Receiver 获得。
dispose 只释放当前订阅，不级联 Messenger、兄弟 Reader 或派生 Receiver。

## 27.4 Receiver（Reader 的 push View，§13–§15）

Receiver\<shared TMessage with core.serialization.Serializable> 实现
IDisposable，构造接收 Reader 并取得其所有权。pump 循环 next，在 EOS
或 dispose 时退出；dispose 释放内部 Reader 并唤醒挂起读取。

addListener/removeListener 以 callback 对象身份登记，重复添加幂等，
删除未登记对象无操作。身份比较使用安全 Place，并用 using 释放。
setExecutor 为每个 listener 指定 Executor，未登记时抛异常；
getExecutor 对未登记对象返回默认 IOExecutor。默认使用 IOExecutor，
可显式切到 ComputeExecutor；均经冷 Task + run 派发，native 不执行
MQ callback。listener 表在锁内取快照、锁外派发。
createReader() 从内部 Reader branch，dispose 不影响这些独立 Reader。

## 27.5 Messenger（发送侧封装，§16）

Messenger\<shared TMessage with core.serialization.Serializable> 是 public
shared class，实现 IDisposable。无参构造建立私有 MessageQueue，
async send(message) 转发 post；createReader() 创建独立订阅；只读
receiver 属性懒创建并缓存 Receiver。dispose 幂等并关闭生产侧，
不级联已有订阅。Reader/Receiver 没有派生 Messenger 或发送消息的 API。

## 27.6 对象身份

listener 比较使用 placeOf 和 Place equality，不暴露地址或整数身份键。

## 27.7 与协程底座的复用（交叉引用）

空 next 在队列锁内占用 Reader 固定 wake Mutex 并保存令牌，解队列锁
后再次 acquire 挂起。post/close/closeReader 在锁内摘除并释放令牌，
早于等待的 signal 也不会丢失；恢复后释放第二次 acquire 的令牌并重新
检查队列。多个 post 不会重复释放同一令牌。

## 27.8 非目标（§29 落点）

不包含 bounded/priority queue、actor、RPC、重试、ack、持久化、
分布式路由、serializer 插件、运行时反射 serializer、Temporary
closure wire 编码、新 Sendable/Transportable 标记、native Receiver
或 native Executor policy。

## 27.9 泛型与运行时边界

shared T 是源码显式约束，不是按 stdlib 类名授予的许可。闭合泛型保持
精确身份；泛型函数中的隐藏 lambda/cell 与冷 Task body 沿实际调用、
构造、字段数据流收集布局，不能用泛型擦除转换补救遗漏。
Task 与 Task<T> 共享原生协程资源时使用 CoroutineHandle/CoroutineCarrige，
遵循 §26.1 的 NativeRcHandle 生命周期契约。

## 27.10 可选压力入口与观测口径

tools/Run-MqStress.ps1 提供可选压力入口，百万负载不进入 test --all。
脚本默认先 build，-UseExistingBuild 只用于已构建阶段。计时与 RSS 仅
覆盖最终 exe，watchdog 管理完整进程树；非零退出或 stderr 视为失败。
RSS 不能代替日志回收证据，应结合 live bytes/gates 和退出 MEMTRACK。
队列 API 变化时，压力语料必须与标准库同步。
