# 定位 / 规范用语 / 核心不变量（§1–§3）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 1. 定位与编译边界

Rigi 的标准编译流水线为：

```text
Rigi source
    ↓ compiler frontend
BIL
    ↓ compiler Middleware
LLVM IR
    ↓ LLVM toolchain
Native executable
```

BIL 是一种：

- 平台无关的语义 IR；
- 严格类型化的 IR；
- 使用具名局部变量的非 SSA IR；
- 使用结构化代码块、禁止任意跳转的 IR；
- 可由 C# 编写的 BIL VM 独立执行与验证的 IR。

BIL 描述程序“执行什么语义”，不描述该语义在目标平台上“如何物理实现”。

### 1.1 BIL 明确不规定的内容

下列内容属于 Middleware、Native Runtime 或 LLVM 工具链，不属于 BIL：

- 寄存器与栈参数分配；
- Native calling convention；
- `sret`、参数拆分、返回值位置等调用 ABI；
- LLVM 类型与 LLVM 指令的具体选择；
- 对象头、胖引用、Box 裸数据块、TypeSheet 的物理布局；
- 字段物理偏移、vtable offset、iMap 和 refMap 的具体表示；
- ARC/GC 指令插入与 ownership fence；
- 协程 frame、continuation 和状态机的物理布局；
- 异常处理采用 landing pad、返回码或其他机器机制；
- 目标相关的原子指令、对齐、endian 和 target feature。

### 1.2 BIL 必须保留的语义

BIL 必须完整保留以下信息：

- Rigi 语义类型；
- 类型、字段、方法、enum case 和资源的符号身份；
- 泛型具化所需的 typeid 参数；
- 位置可变参数与具名可变参数的规范化参数包；
- 运算、字段访问、变量访问、索引访问、构造和转换等语言级操作；
- 结构化控制流；
- 异常、`await` 和 `yield` 的可观察语义；
- source-level `async` 调用的 eager spawn 语义；
- `rich`、`shared`、可见性和其他影响合法性的类型属性；
- frontend 已经确定的符号与精确类型。

---

## 2. 规范用语

本文档中的“必须”“不得”“应当”“可以”分别对应规范性要求：

- **必须 / MUST**：实现若不满足即不兼容本标准；
- **不得 / MUST NOT**：实现若执行该行为即不兼容本标准；
- **应当 / SHOULD**：实现原则上应满足，除非存在明确且可说明的理由；
- **可以 / MAY**：实现可自行选择。

除非特别说明，“类型相同”均指 BIL 类型引用经过规范化后的**严格相等**，不是子类型兼容、可转换或隐式提升。

---

## 3. BIL 的核心不变量

### 3.1 严格类型

BIL 不执行隐式类型转换。

对于需要严格相同类型的操作，frontend 必须在产生该操作前显式插入 `cast` 或其他转换操作。Middleware 不得为修复非法 BIL 而自行插入语言级隐式转换。

例如：

```bil
// $a: .i32
// $b: .i64
// 非法：两个操作数类型不同
add $a $b $result
```

合法形式：

```bil
cast $a $a64 type(.i64)
add $a64 $b $result
```

### 3.2 类型驱动操作

BIL 的运算符、getter、setter 和索引操作不是预先降级后的普通函数调用。

Middleware 根据以下精确信息确定唯一实现：

```text
操作类别
+ 操作数的严格类型
+ 结果的严格类型
+ 已解析的字段或其他符号身份
```

Middleware 可以将该操作实现为：

- 一条或多条 LLVM intrinsic 指令；
- 对精确运算符实现的调用；
- 对 getter/setter 的调用；
- vtable/interface 派发；
- wrapper 代理链；
- runtime helper；
- 被完全优化消除的操作。

上述差异不改变 BIL 指令本身的语义分类。

### 3.3 frontend 不变量

合法 BIL 必须已经完成：

- 名称解析；
- 访问控制检查；
- 类型推断；
- 普通显式方法调用的目标符号解析；
- 运算、getter/setter、索引与构造操作的精确输入/结果签名规范化（但不必绑定到实现 METHOD_SYMBOL）；
- 默认参数填充；
- 具名参数重排；
- 泛型约束检查；
- smart cast 分析；
- `rich` / `shared` 字段闭包检查；
- async 边界的共享安全检查；
- extension 目标解析，以及 wrapper 适用性与静态组合链确定；
- 语法糖规范化。

Middleware 不重新执行 source-level overload ranking。对于运算、getter/setter、索引和静态构造等类型驱动操作，Middleware 根据 frontend 已确定的精确输入/结果签名进行**唯一实现查询**；该查询可以选中某个 overload，但不包含隐式转换、候选排序或最佳匹配。

### 3.4 frontend 规范化但不丢失的源码结构

以下源码结构通常不拥有同名 BIL 指令，而由 frontend 规范化为本标准已有操作：

- 安全调用 `?.`：`type.is` / nullable 检查 + `if`（nullable 检查 = `cmp.eq`/`cmp.ne` 与 `null type(T)` 资源，§19.1/§11.5）；
- `if?` 空值回退：nullable 检查 + `if`（同上；非空分支的取值是 `.nullable<T>` → `T` 的显式 `cast`，§12.1）；
- smart cast：条件检查 + 显式 `cast`；
- `seq` 与 `return@`：结构化 block、结果临时变量与 `call`；
- pattern switch：多个 `if` 或嵌套结构化判断；
- 解构声明：精确字段/索引读取；
- `using`：初始化 + `try/finally` 清理路径。

这种规范化不得改变 `SYNTAX.md` 或 `RUNTIME.md` 规定的可观察语义。

### 3.5 无任意跳转

BIL 不提供 `jmp`、条件 `jmp` 或任意 basic-block branch。

跨 block 的执行只能通过本标准定义的结构化指令完成：

- `call`；
- `if`；
- `loop` / `loop.rev`；
- `switch`；
- `try`。

所有被引用 block 必须属于当前函数。

---
