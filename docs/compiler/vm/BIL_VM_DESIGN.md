# BIL VM 设计

> 依据：`BIL_STANDARD.md` §21.9 / §22（VM 可执行性与语义要求）、`RUNTIME.md`
> §17–§19（Coroutine / Executor / Task / Alarm）。旧 `SEMANTIC_ROADMAP` §S14 仅为历史讨论。
> 定位：**行为参考实现**。行为正确性是参考实现前提；可维护性与代码易读性优先于性能；
> 明确不以性能为目标。Native 实现（Middleware/LLVM）与本 VM 必须在
> §22.2 列出的全部可观察行为上一致。


## 1. 裁决与边界

本 VM 是 BIL 文本/模型的抽象解释器：

- 不模拟 §22.1 列出的任何物理机制（胖引用位布局、对齐、Box 裸块、ARC/GC、
  LLVM calling convention）；TypeSheet/vtable/iMap 不模拟其**物理位布局**，
  但方法派发统一建立在逻辑等价的 VmTypeSheet（拍平 vtable+iMap）抽象上
  （见 §3.4）。
- 不做 source-level overload ranking（§22.3）；运算实现查询键恒为
  `opcode + 精确操作数类型 + 精确结果类型`。
- 不把 `get.field` / `set.field` / `get.array` / `set.array` 改写为普通调用
  （§22.4）；它们是独立语义操作，按精确类型与符号元数据执行
  getter/setter/operator/wrapper 行为。
- native 调用只经 §22.5 内建 hook 表执行；表外 `(lib, symbol)` 拒绝执行并报错。
- `hint`（§18）恒为 no-op。

与 §22.5 hook 表的接线方式：hook 点是 **native 函数声明层**（被
`@NativeLibrary`/`@NativeSymbol` 标注的声明，如 `core.io::Console.print`），
不是 Rigi 层包装函数。`Console.println` 等 Rigi 层包装走正常 BIL 解释路径，
只有真正 native 的那一次 `invoke` 命中 hook 表。

正文按主题完整迁移；本索引沿用原章节编号。下列链接直接定位相应专题正文。

## 2. 目录与文件布局

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#2-目录与文件布局)。

## 3. 值模型（§22.1）

见 [VALUES_AND_DISPATCH.md](VALUES_AND_DISPATCH.md#3-值模型221)。

### 3.1 精确标量，独立表示

见 [VALUES_AND_DISPATCH.md](VALUES_AND_DISPATCH.md#31-精确标量独立表示)。

### 3.2 复合与运行时类型值

见 [VALUES_AND_DISPATCH.md](VALUES_AND_DISPATCH.md#32-复合与运行时类型值)。

### 3.3 运算实现查询（§22.3）

见 [VALUES_AND_DISPATCH.md](VALUES_AND_DISPATCH.md#33-运算实现查询223)。

### 3.4 方法派发：逻辑 TypeSheet（拍平 vtable + iMap）

见 [VALUES_AND_DISPATCH.md](VALUES_AND_DISPATCH.md#34-方法派发逻辑-typesheet拍平-vtable--imap)。

## 4. 执行模型（RUNTIME §17–§19）

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#4-执行模型runtime-1719)。

### 4.1 显式 Step 循环，真并发 Executor

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#41-显式-step-循环真并发-executor)。

### 4.2 同步与 happens-before

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#42-同步与-happens-before)。

### 4.3 try/throw/using

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#43-trythrowusing)。

### 4.4 Run 入口、完成屏障与结果

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#44-run-入口完成屏障与结果)。

### 4.5 运行期资源生命周期

见 [EXECUTION_AND_LIFETIME.md](EXECUTION_AND_LIFETIME.md#45-运行期资源生命周期)。

## 5. 指令分发：家族基类 + Execute

见 [INSTRUCTIONS_AND_HOOKS.md](INSTRUCTIONS_AND_HOOKS.md#5-指令分发家族基类--execute)。

<a id="6-indirect-系列bil-模型补全--提前实现"></a>

## 6. indirect 指令与类型驱动访问

见 [INSTRUCTIONS_AND_HOOKS.md](INSTRUCTIONS_AND_HOOKS.md#6-indirect-指令与类型驱动访问)。

## 7. native hook 表（§22.5）

见 [INSTRUCTIONS_AND_HOOKS.md](INSTRUCTIONS_AND_HOOKS.md#7-native-hook-表225)。

### 7.1 已注册 native 键分类目录

见 [INSTRUCTIONS_AND_HOOKS.md](INSTRUCTIONS_AND_HOOKS.md#71-已注册-native-键分类目录)。

### 7.2 方法 Hook 与普通函数体的优先序

见 [INSTRUCTIONS_AND_HOOKS.md](INSTRUCTIONS_AND_HOOKS.md#72-方法-hook-与普通函数体的优先序)。

## 8. 测试策略

见 [TESTING_AND_BOUNDARIES.md](TESTING_AND_BOUNDARIES.md#8-测试策略)。

<a id="9-实施切片"></a>

## 9. 指令与执行行为覆盖类别

见 [TESTING_AND_BOUNDARIES.md](TESTING_AND_BOUNDARIES.md#9-指令与执行行为覆盖类别)。

## 10. 明确不做

见 [TESTING_AND_BOUNDARIES.md](TESTING_AND_BOUNDARIES.md#10-明确不做)。
