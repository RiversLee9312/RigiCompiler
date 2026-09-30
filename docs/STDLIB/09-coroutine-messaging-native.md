# 各 NS 的最小能力：core.coroutine / core.messaging / core.native（§4.8 + §4.12）

> 本文件是 [STDLIB.md](../STDLIB.md)（Rigi 标准库 MVP 设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

### 4.8 `core.coroutine` 与 `core.messaging`

复用既有 API，不另造 Task、Promise、线程 API 或消息队列。集成要求：

- 流等待和清理使用现有挂起/唤醒路径，不改变 run-to-suspension。
- 普通调用链与虚调用中的等待正确传播，恢复后 local 状态保持有效。
- 普通集合适用于协程内部状态，跨协程使用既有 shared/复制/原子设施。
- JSON Serializer 不是 Messenger 的必要中间步骤，消息继续复用既有对象快照路径。
- 资源搬运不自动赋予共享游标并发操作能力，具体承诺由流实现定义。

### 4.12 `core.native`

沿用 NativeRcHandle/Carriage 资源模型，面向普通使用者的文件操作放在 `core.fs`，字节协议放在 `core.io`。
原生操作挂起时保活句柄和缓冲区，直至完成回调及清理结束；回调经已有调度协议恢复协程，不在任意系统线程直接运行用户解析器。

