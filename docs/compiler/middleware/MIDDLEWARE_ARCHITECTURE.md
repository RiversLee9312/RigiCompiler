# Rigi Middleware 架构索引（BIL → 原生）

本索引按原章节编号导航。BIL 编码/门禁以 [BIL 规范](../../BIL_STANDARD.md)
为准，运行时可观察语义以 [RUNTIME](../../RUNTIME.md) 为准，源语言合法性
以 [SYNTAX](../../SYNTAX.md) 为准；专题描述实现组织、取舍与边界。

## 1. 定位与总体管线

[定位与总体管线正文](OVERVIEW.md)；[旧 §1.1 Handle/Cell 布局](LAYOUT_AND_ABI.md)；[工具链/缓存](TOOLCHAIN_AND_CACHE.md)；[C 库/入口 ABI](LAYOUT_AND_ABI.md)。

## 2. 外部依赖与选型裁决

[外部依赖与选型裁决正文](TOOLCHAIN_AND_CACHE.md)。

## 3. 内部层次与职责

[内部层次与职责正文](PIPELINE.md)。

## 4. 内存管理架构

[内存管理架构正文](MEMORY_MANAGEMENT.md)；[§4.8 运行时 ABI](RUNTIME_ABI.md)。

## 5. wrapper 烘焙

[wrapper 烘焙正文](WRAPPER_BAKING.md)。

## 6. 协程降级

[协程降级正文](COROUTINE_LOWERING.md)。

## 7. 泛型、调用与 ABI

[泛型、调用与 ABI正文](LAYOUT_AND_ABI.md)。

## 8. 异常机制

[异常机制正文](EXCEPTIONS.md)。

## 9. 优化 pass 归属表

[优化 pass 归属表正文](OVERVIEW.md)。

## 10. 验证策略

[验证策略正文](VALIDATION.md)。

## 11. 代码组织

[代码组织正文](PIPELINE.md)；[rigi_rt 代码组织](RUNTIME_ABI.md)。

## 12. 阶段划分

[阶段划分正文](VALIDATION.md)（当前实现覆盖与优化边界；不记录里程碑进度）。

### 1.1 Handle 隐藏布局与 Cell 虚槽

完整正文见 [布局与 ABI](LAYOUT_AND_ABI.md)。

### 4.1 原则：编译器 ARC 即全部内存安全

见 [原则：编译器 ARC 即全部内存安全](MEMORY_MANAGEMENT.md)。

### 4.2 RcInjection（安全攸关 pass）

见 [RcInjection（安全攸关 pass）](MEMORY_MANAGEMENT.md)。

### 4.3 两级 ARC

见 [两级 ARC](MEMORY_MANAGEMENT.md)。

### 4.4 胖引用写非原子化（RUNTIME §3）

见 [胖引用写非原子化（RUNTIME §3）](MEMORY_MANAGEMENT.md)。

### 4.5 macroGC：全局候选循环收集

见 [macroGC：全局候选循环收集](MEMORY_MANAGEMENT.md)。

### 4.6 触发与执行体

见 [触发与执行体](MEMORY_MANAGEMENT.md)。

### 4.7 ownership fence（RUNTIME §23 保留）

见 [ownership fence（RUNTIME §23 保留）](MEMORY_MANAGEMENT.md)。

### 4.8 运行时面（C ABI 草案）

此标题仅保留旧链接 fragment；当前 ABI 见 [运行时面](RUNTIME_ABI.md)，已不是命名草案。

### 4.9 对象头与 refMap

见 [对象头与 refMap](MEMORY_MANAGEMENT.md)。

### MW1 符号与类型表

完整职责见 [管线与绑定](PIPELINE.md)。

### MW2 实现绑定（ImplBinding）

完整职责见 [管线与绑定](PIPELINE.md)。

### MW3 MIR 构造

完整职责见 [管线与绑定](PIPELINE.md)。

### MW4 MIR pass 群

完整职责见 [管线与绑定](PIPELINE.md)。

### MW5 布局与 ABI

完整职责见 [管线与绑定](PIPELINE.md)。

### MW6 LLVM 模块构建与发射

完整职责见 [管线与绑定](PIPELINE.md)。
