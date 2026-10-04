# Rigi 语义分析与 BIL 生成架构（中端）

> **定位**: 本索引及所链接专题说明 AST → BIL 之间全部编译阶段（下称**中端**）的架构：
> 阶段划分、数据结构、数据流向、诊断模型、代码组织与验收方式。
>
> **文档分工**：源语言合法性以 `SYNTAX.md` 为准；运行时可观察行为以
> `RUNTIME.md` 为准；BIL 编码与验证规则以 `BIL_STANDARD.md` 为准。
> 本组专题只说明中端**内部**如何组织，不重新定义上述三者的语义。


> `super(...)` 在 P3 绑定为独立 Bound/Lowered 调用标记，候选来自直接 BaseType
> 并复用 OverloadResolution；P4b 固定发 `invoke fn(..super)`，由 Middleware
> 解析为直接基类原始实现。frontend 不生成 `..create`，它仅属于 Middleware/VM
> 生命周期阶段。

---

正文按主题完整迁移；本索引沿用原章节编号。下列链接直接定位相应专题正文。

## 1. 定位与总体管线

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#1-定位与总体管线)。

### 1.1 有界并行与发布屏障

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#11-有界并行与发布屏障)。

## 2. Pass 职责分配

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#2-pass-职责分配)。

## 3. 编译单元模型

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#3-编译单元模型)。

## 4. 符号对象图（P1/P2 产物）

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#4-符号对象图p1p2-产物)。

### 4.1 符号家族

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#41-符号家族)。

### 4.2 驻留（interning）

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#42-驻留interning)。

<a id="43-bootstrap-与-corerg混合策略"></a>

### 4.3 bootstrap 与标准库源码（混合策略）

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#43-bootstrap-与标准库源码混合策略)。

### 4.4 canonical symbol 是投影，不是身份

见 [PIPELINE_AND_SYMBOLS.md](PIPELINE_AND_SYMBOLS.md#44-canonical-symbol-是投影不是身份)。

## 5. BoundTree（P3 产物）

见 [BINDING.md](BINDING.md#5-boundtreep3-产物)。

### 5.1 节点设计

见 [BINDING.md](BINDING.md#51-节点设计)。

### 5.2 P3 必须落实的语义规则（易漏清单）

见 [BINDING.md](BINDING.md#52-p3-必须落实的语义规则易漏清单)。

## 6. LoweredTree 与发射（P4）

见 [LOWERING_AND_EMISSION.md](LOWERING_AND_EMISSION.md#6-loweredtree-与发射p4)。

### 6.1 P4a：降级重写（Lowerer）

见 [LOWERING_AND_EMISSION.md](LOWERING_AND_EMISSION.md#61-p4a降级重写lowerer)。

### 6.2 P4b：发射（BilEmitter）

见 [LOWERING_AND_EMISSION.md](LOWERING_AND_EMISSION.md#62-p4b发射bilemitter)。

### 6.3 BIL 对象模型与工具层次（关注点分离）

见 [LOWERING_AND_EMISSION.md](LOWERING_AND_EMISSION.md#63-bil-对象模型与工具层次关注点分离)。

<a id="7-深度-lowering-与-bil_standard-待修订清单"></a>

## 7. 深度 lowering 与跨层契约

见 [ASYNC_CLOSURES_AND_CLEANUP.md](ASYNC_CLOSURES_AND_CLEANUP.md#7-深度-lowering-与跨层契约)。

### 7.1 wrapper 值语义与 BIL 形态

见 [LOWERING_AND_EMISSION.md](LOWERING_AND_EMISSION.md#71-wrapper-值语义与-bil-形态)。

### 7.2 async/await/yield/using 与闭包 lowering（async 专项）

见 [ASYNC_CLOSURES_AND_CLEANUP.md](ASYNC_CLOSURES_AND_CLEANUP.md#72-asyncawaityieldusing-与闭包-loweringasync-专项)。

## 8. 诊断模型（P1–P4 通用）

见 [BINDING.md](BINDING.md#8-诊断模型p1p4-通用)。

## 9. 代码组织

见 [IMPLEMENTATION_AND_TESTING.md](IMPLEMENTATION_AND_TESTING.md#9-代码组织)。

## 10. 测试策略

见 [IMPLEMENTATION_AND_TESTING.md](IMPLEMENTATION_AND_TESTING.md#10-测试策略)。

## 11. 原则重申

见 [IMPLEMENTATION_AND_TESTING.md](IMPLEMENTATION_AND_TESTING.md#11-原则重申)。
