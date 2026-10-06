# VM验证与实现边界

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](BIL_VM_DESIGN.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 8. 测试策略

以**执行断言**测试形态（`../../legacy/SEMANTIC_ROADMAP.md` §S14）：跑出结果/异常与预期比对，
持续验证 §21.9「VM 可执行性」。

基建：`Tests/` 的 BilVm provider 由独立 TUnit 宿主发现，每个动作在同宿主
隔离 worker 中执行。`CompilerTestTools`、`BilTestHarness` 与 VM 测试工具提供
「源码 → 编译 → BIL → 运行」端到端 helper，捕获 stdout/stderr/异常；
`CaseAssertions` 在请求 scope 记录真实断言，入口和资源协议见
[DEVELOPMENT.md](../../../DEVELOPMENT.md)。

CI 按 suite/稳定 ID 将 provider 精确分配给独立 runner，最终证明全目录互斥且完整；
分片不拆 VM 单方法的共享生命周期。同一机器的独立全量宿主仍串行，
每个 runner 内的 CPU/内存授予继续由共享资源预算控制。

**时序不变性纪律**（真并发 Executor 的必然要求）：

- 断言必须是时序不变量：单协程程序可断言 stdout 全文；多协程程序用
  `await` 建立同步点后断言最终状态。
- 并发 print 只允许断言「每行完整出现」（行级原子），禁止断言行序。
- `BilVm.Run(module)` 返回前等待 quiescence（全部协程达终态），测试在
  屏障后断言。

## 9. 指令与执行行为覆盖类别

1. **入口与 hello world**：值模型、VmContext、Executor/Coroutine 基建、
   `load`/`get.var`/`set.var`/内建运算/`invoke`/`ret`、`hint` no-op、
   rigi_rt hook → hello world 端到端真实 stdout。
2. **对象与数据**：`new`/`new.case`/`new.wrapped*`、`get/set.field(.static)`、
   `get/set.array`、enum 身份与 payload、`get.self`。
3. **indirect 与运行时类型**：核对 §6 的模型、
   verifier 与 VM 一致性，覆盖 `cast(.safe)(.indirect)`、`type.*(.indirect)`、
   `get.wrapper(.field)(.indirect)`、`getid.*`、`new.indirect`、
   `invoke.indirect(.noret)`。
4. **结构化控制流**：`call blk`、`if`、`loop`/`loop.rev`、`break`/`continue`
   （`.breakid`）、`switch`、`try`、`throw`。
5. **协程**：eager spawn 全语义、`await`、`yield`（裸/PollingAlarm/
   EventAlarm）、Task 终态传播（成功/异常/取消的 VM 内部分）、quiescence。

## 10. 明确不做

- 不模拟 §22.1 列出的物理机制。
- 不做性能优化（无指令缓存、无内联缓存、无特化）；可读性优先。
- 不实现 GC/ARC；VM 值的生命周期托管给 .NET GC。
- 新 hook 必须同步 §22.5 的分类契约、stdlib native 声明与本文 §7 目录；
  不以任意 native 声明获得宿主执行能力。
- 不为 VM 改变 BIL 指令语义；发现规范歧义时先修规范或提决议，不在
  VM 内私自解释。

消息队列由标准库单份 Rigi 代码执行：安全 AtomicList 持消息，队列 Mutex 保护 capability、cursor、水位与EOS；VM 不维护消息注册表或专用队列 hook。listener 以 Place 比较对象身份并由 Task.run(executor) 派发，参见 RUNTIME §27。
