# Rigi BIL 标准

> 状态：规范草案 1.1  
> BIL：Basic Intermediate Language（基础中介语言）

本文档定义 Rigi 编译器 frontend 与 Middleware 之间的标准中间表示 BIL。

本文档只规定 BIL 的程序结构、类型规则、符号模型、指令语义、验证规则和文本表示。Rigi 表层语言规则以 `SYNTAX.md` 为准；Native 运行时表示与行为以 `RUNTIME.md` 为准。

---

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

## 4. 文件与程序集结构

一个 BIL 文本文件按以下逻辑顺序组成：

```bil
BIL "1.1"

Metadata {
    ...
}

Resources {
    ...
}

LocalSymbols {
    ...
}

ExternalSymbols {
    ...
}

fn(com.example::Owner$method(value:.i32)@.void) {
    ...
}
```

各段的物理顺序应当保持上述顺序。解析器可以允许空段，但标准生成器应当输出全部段。

### 4.1 `Metadata`

`Metadata` 保存程序集级非执行信息，例如：

- BIL 版本；
- 源模块名；
- 编译器版本；
- 调试信息索引；
- feature flags；
- 依赖程序集标识。

Metadata 不得被普通 BIL 指令读取。需要在程序执行中使用的数据必须放入 `Resources`。

### 4.2 `Resources`

BIL 指令中不得直接出现用户字面量。所有字面值和静态表必须在 `Resources` 中声明，再通过 `load`、结构化指令或 `hint` 引用。

资源是不可变值。加载资源产生对应 Rigi 值的语义副本；具体是否复制、共享或常量折叠由 Middleware 决定。

### 4.3 `LocalSymbols`

`LocalSymbols` 声明当前程序集定义的类型及其成员。

### 4.4 `ExternalSymbols`

`ExternalSymbols` 声明当前程序集使用、但由其他程序集定义的符号。外部符号必须包含验证调用和访问所需的完整语义签名，不包含 Native 地址或目标 ABI 信息。

---

## 5. 词法规则与符号名称

### 5.1 本地标识符

资源、block、参数和局部变量使用 BIL 本地标识符。本地标识符可以包含：

```text
A-Z a-z 0-9 _ -
```

本地标识符：

- 不得为空；
- 在所属作用域内必须唯一；
- 普通用户标识符不得以 `.` 开头；
- 以 `.` 开头的参数名和局部名由编译器保留，例如 `.this`、`.return`、`.generic.T`、`.vargs.args` 和 `.kwargs.args`。
- 以 `.` 前缀段保留的成员名还包括：`.proxy.`（wrapper 类型内的 proxy 模板成员名，见 §8.4 `wrapper-proxy`）；方法名 `call???` 是 `core::Any` 的内建方法名（`?` 非标识符字符，用户源码不可声明，见 §15.5 / §22.5）。wrapper 隐藏存储的命名约定见 §5.3——该符号**不**出现于 BIL 文本（存储合成归 Middleware）。
- 编译器合成的隐藏类型名以 `..` 前缀保留：`..lambda..UUID`（lambda 隐藏类，见 `SYNTAX.md` §5.2）、`..cell..UUID`（统一 cell 存储的隐藏子类，见 `SYNTAX.md` §5.2 / §14.3）与 `..companion.UUID`（静态方法被 Method wrapper 修饰时合成的 singleton companion，见 §8.7）；用户源码不可声明同名类型。
- 编译器合成的保留方法名以 `..` 前缀保留：`..init.wrapper`（实体 wrapper 初始化方法，见 §9.7）——用户源码不可声明同名方法。

`Resources` 和 block 可以继续使用 `R_Message`、`entry` 等本地名称；该规则不适用于类型、字段、方法和运算符等语言符号。

### 5.2 canonical 语言符号

类型、字段、全局变量、全局常量、方法、运算符、getter 和 setter **不得**使用 `Type_X`、`F_X`、`M_X` 等 BIL 私有别名。它们直接使用 `SYNTAX.md` 与 `RUNTIME.md` 规定的 canonical symbol：

```text
类型：
命名空间::类名[.内部类名...]

方法：
命名空间::[可能有的类名[.内部类名...]]$[.static.]方法名([参数名:参数类型,...])@返回值类型

字段 / 全局变量 / 全局常量：
命名空间::[可能有的类名[.内部类名...]]#[.static.]名称@字段类型

运算符：
命名空间::类名[.内部类名...]$$运算符名称([参数名:参数类型,...])@返回值类型

getter：
命名空间::[可能有的类名[.内部类名...]]$[.static].get.名称@字段类型

setter：
命名空间::[可能有的类名[.内部类名...]]$[.static].set.名称@字段类型
```

`.static.` 只出现在真正的 static 成员上。Singleton 实例成员不因此获得 `.static.`。

canonical symbol 中的参数名称、泛型 hidden argument 和可变参数 hidden argument 必须遵循第 7 节以及 `SYNTAX.md` / `RUNTIME.md` 的规定。符号的类型部分使用 BIL 类型引用，因此可以出现闭合泛型、`.generic<...>` 等形式。

### 5.3 wrapper 存储命名约定（Middleware ABI）

BIL 文本**不**声明 wrapper 隐藏字段。Middleware 为宿主合成 wrapper 实例存储时，字段名称部分约定为：

```text
[可能有的类型全名]#.wrapper.wrapper类型全称
```

其中 `wrapper类型全称` 是 wrapper 的 canonical 完整类型名，不得使用短名或本地别名。加上命名空间与字段类型后，完整 canonical 字段符号为：

```text
命名空间::[可能有的类型全名]#.wrapper.wrapper类型全称@<wrapper 类型引用>
```

例如类型 `com.example::Service` 被 `core.logging::Logged` 修饰时：

```text
com.example::Service#.wrapper.core.logging::Logged@core.logging::Logged
```

该命名约定是 Middleware 合成存储与诊断显示的 ABI 约定，不是 BIL 字段声明形态。用户不可声明同名字段，也不得绕过 `get.wrapper` / `get.wrapper.field` / `set.wrapper.field` 访问该存储。若同一实体存在多个 wrapper，每个 wrapper 以自己的完整类型名形成唯一字段名称。解析时，`#` 分隔 owner 与字段名，最后一个 `@` 分隔字段名与字段类型；`.wrapper.` 之后、最终 `@` 之前的内容整体视为 wrapper 完整类型名。宿主上的 wrapper **应用标记**见 §8.3.1。

### 5.4 注释

BIL 支持：

```bil
// 单行注释
/* 多行注释 */
```

### 5.5 大小写

关键字、opcode、修饰符和内建类型名区分大小写。标准形式全部使用小写；canonical symbol 保留源码声明的大小写。

### 5.6 指令前导点

标准指令 opcode 不带前导点：

```bil
load res(R_Message) $value
invoke.noret fn(core::Console$.static.println(value:.string)@.void) [$value]
```

旧文本中的 `.load`、`.invoke` 等形式属于 legacy spelling；兼容解析器可以接受，但标准生成器不得输出。

---

## 6. 类型系统

### 6.1 类型引用

每个参数、局部变量、字段、方法返回值和资源都必须具有一个 BIL 类型引用。

类型引用可以是：

- 固定内建类型；
- 用户 canonical 类型符号；
- 闭合泛型类型；
- 由运行时 typeid 指定的泛型类型引用；
- 编译器专用的 capability/handle 类型。

### 6.2 固定内建类型

标准内建类型包括：

```text
.void
.i8 .i16 .i32 .i64
.u8 .u16 .u32 .u64
.f32 .f64
.bool
.char
.string
.any
.object
.valuetype
.breakid
```

其中：

- `.void` 只能用作无结果方法的返回类型，不得声明普通变量；
- `.breakid` 是结构化控制 capability，不是普通整数和值类型；
- `.any`、`.object`、`.valuetype` 是 Rigi 根类型的标准 BIL 别名；
- `.string` 是**非 rich 值类型**（`SYNTAX.md` §3.1.2），赋值兼容与复制按值类型规则处理，不属于 `.object` 分支。它的物理表示是运行时特权裸缓冲区；BIL 与 BIL VM 一律按值语义（深拷贝）理解 `.string`，不得假设任何共享缓冲区、驻留或 copy-on-write 优化的存在——与「BIL 不得假设特定 GC 模型」同理。

### 6.3 标准类型构造

标准类型构造包括：

```text
.array<T>
.map<TKey, TValue>
.pair<TFirst, TSecond>
.nullable<T>
.cell<T>
.readonly_cell<T>
.typeid<TBound>
.fieldid<TOwner, TValue, instance|static>
.generic<TYPEID_PLACE>
```

说明：

- `.array<T>` 对应源码层的 `Array\<T>`；
- `.map<K,V>` 对应标准运行时 Map；
- `.pair<A,B>` 对应标准 Pair；
- `.nullable<T>` 对应 `Nullable\<T>`；
- `.cell<T>` 对应标准库 `core::Cell\<T>`（抽象基类，抽象 `getValue`/`setValue`）的特权拼写，与 `.array<T>`、`.nullable<T>` 同类；
- `.readonly_cell<T>` 对应标准库 `core::ReadonlyCell\<T>`（抽象基类，仅抽象 `getValue`）的特权拼写；
- `.cell` / `.readonly_cell` 的 `T` 递归按类型构造规则解析；用途为闭包捕获与 wrapper 值统一 cell 存储（`SYNTAX.md` §5.2 / §14.3）；**基类抽象化后 BIL 中不再被直接 `new`**——实际 cell 对象恒为 `..cell..UUID` 隐藏子类实例（`.type` 声明 `extends .cell<T>` / `.readonly_cell<T>`）；读写经普通 `invoke` 虚派发 `getValue`/`setValue`，无专用指令；
- 特权拼写的定位 = Middleware 激进优化识别点（消除 cell 间接/直读槽位等）；验证规则不变——即使不做特判、按普通类烘焙也可正确工作；
- 共享安全性为 passthrough：`.cell<T>` / `.readonly_cell<T>` 的共享安全性等同于 `T`（与 Box 同例，需特殊判定，不按普通 class 闭包表）；
- `.typeid<TBound>` 是 BIL 中具类型边界的运行时类型句柄，对应源码 `Type\<TBound>` 的语义；
- 未写边界的 `.typeid` 等价于 `.typeid<.any>`；
- `.fieldid` 必须携带足以验证间接访问的签名；
- `.generic<...>` 表示“由指定 typeid 位置描述的实际类型”。

示例：

```bil
.args {
    .generic.TElement = .typeid<.any>,
    value = .generic<$.generic.TElement>
}
```

### 6.4 类型严格相等

以下情况不构成 BIL 类型相等：

- 派生类与基类；
- 实现类与接口；
- `T` 与 `T?`；
- `i32` 与 `i64`；
- `Array\<Dog>` 与 `Array\<Animal>`；
- `Box\<T>` 语义投影与普通 Object 类型；
- 具有不同 typeid 来源的两个 `.generic<...>`，除非验证器可以证明其 typeid 恒等。

需要改变视图或类型时必须使用 `cast` 或其他明确指令。

### 6.5 赋值兼容

`set.var`、`set.field` 和普通方法参数传递默认要求源类型与目标类型严格相同。

source-level 子类型赋值必须由 frontend 生成显式 `cast`，即使该 cast 在 Middleware 中最终只改写视图 typeid 或被优化消除。

### 6.6 特权类型

`Box\<T>`、`Span\<T>` 等编译器/运行时特权类型在 BIL 中是正常语义类型，但 BIL 不复制其 Native 物理布局。

BIL VM 应使用抽象值语义实现这些类型；Middleware 依照 `RUNTIME.md` 选择胖值、裸 buffer 或其他物理表示。

---

## 7. 泛型与可变参数的规范签名

BIL 函数签名必须按照 `SYNTAX.md` 和 `RUNTIME.md` 的具化规则生成隐藏参数。

隐藏参数名称以 `.` 开头，普通源码参数不得使用这些名称。

### 7.1 隐藏参数命名与类型

| 源声明形态 | BIL 隐藏参数名 | BIL 参数类型 |
|---|---|---|
| 固定泛型参数 `T` | `.generic.T` | `.typeid` |
| 位置可变泛型参数 `TArgs...` | `.generic.TArgs` | `.array<.typeid>` |
| 具名可变泛型参数 `named TArgs...` | `.generic.TArgs` | `.map<.string, .typeid>` |
| 位置值可变参数 `args...` | `.vargs.args` | `.array<.any>` |
| 具名值可变参数 `named args...` | `.kwargs.args` | `.array<.pair<.string, .any>>` |

具名泛型类型图与具名值参数包承担不同职责：

- `.generic.TArgs` 保存名称到实际 typeid 的映射；
- `.kwargs.args` 保存名称到统一 `Any` 值槽的映射/有序 pair 序列。

对于 `named TValues...` 与 `named values...` 成对出现的声明，两者都必须出现在规范签名中。

### 7.2 参数顺序

BIL 的规范参数顺序为：

1. `.this`，若存在 receiver；
2. 固定泛型隐藏参数，按声明顺序；
3. 泛型可变参数包，若存在；
4. 普通固定值参数，按声明顺序；
5. 位置值可变参数包，若存在；
6. 具名值可变参数包，若存在。

该顺序是 BIL 文本和 BIL VM 的**语义调用顺序**，不是 Native ABI。

### 7.3 receiver

实例方法具有：

```bil
.this = TYPE
```

扩展函数/扩展字段也使用 `.this` 表示被扩展值的语义 receiver。Middleware 可以将其 lower 为普通第一个参数或其他实现形式。

### 7.4 具名普通参数

普通具名调用在 frontend 中完成名称解析、默认参数填充和重排。进入 BIL 后，固定参数按被调用签名顺序排列。

参数名仍保存在方法签名与 canonical symbol 中，但 `invoke` 不再进行名称匹配。

### 7.5 泛型类型引用

函数体中对泛型参数 `T` 的值类型引用应使用：

```text
.generic<$.generic.T>
```

泛型类型声明中的实例字段可以引用该类型保存的泛型 typeid 字段：

```text
.generic<field(com.example::Box#.generic.T@.typeid)>
```

该字段如何物理保存属于 Middleware 和 Runtime；BIL 只要求 typeid 在语义上可取得。

---

## 8. 符号模型

### 8.1 符号引用

BIL 不为语言符号再分配独立的 Type/Field/Method ID。类型和成员直接以 canonical symbol 作为稳定身份。

标准参数表达式为：

```bil
fn(METHOD_SYMBOL)
field(FIELD_SYMBOL)
case(ENUM_TYPE_SYMBOL.CaseName)
type(TYPE_SYMBOL)
blk(BLOCK_ID)
res(RESOURCE_ID)
$VariableName
```

示例：

```bil
type(com.example::Service)
field(com.example::Service#name@.string)
fn(com.example::Service$load(id:.i64)@com.example::User)
fn(com.example::Number$$plus(another:com.example::Number)@com.example::Number)
fn(com.example::Service$.get.name@.string)
case(com.example::RequestResult.Failed)
```

`fn(...)` 与 `field(...)` 内的字符串必须已经是完整 canonical symbol；解析器和验证器不得依赖额外 owner 前缀补全。类型、字段类型、参数类型和返回类型也是符号身份的一部分。

### 8.2 类型声明

类型声明的规范形式为：

```bil
.type TYPE_SYMBOL = kind [generic(T1, out T2, in T3)] [extends BASE_TYPE]
    [implements INTERFACE_TYPE, ...]
    [modifiers...] {
    ...
}
```

`generic(...)` 子句（S9e/M96）：泛型参数按源码声明序列出，型变参数保留 `out` 或
`in` 前缀；函数泛型参数不携带型变。约束是编译期概念，BIL 不携带约束信息。泛型
类型的 canonical 签名（字段/方法类型中的 `.generic<...>`）经 §7.5 与隐藏参数
（§7.1）关联到这些名称。调用签名的类型兼容检查消费该方向：`out` 递归检查实际
实参可赋给期望实参，`in` 反向检查，未标注参数严格相等。

例如：

```bil
.type com.example::Service = class pub {
    ...
}

.type com.example::Box = class generic(T) pub open {
    .field com.example::Box#item@.generic<$.generic.T> pub
    ...
}
```

`kind` 为：

```text
class
struct
enum-struct
interface
wrapper
```

类型修饰符可以包括：

```text
pub protected internal priv
open abstract singleton
rich shared
wrapped(WRAPPER_TYPE_REF)
```

`wrapped(WRAPPER_TYPE_REF)` 可重复出现，声明序 = outer→inner 应用序；语义见 §8.3.1。

修饰符合法性必须与 `SYNTAX.md` 一致。例如：

- `rich` 仅适用于 struct/enum struct 和 wrapper；
- `wrapper` 类型**必须**显式带 `rich`——源码中 `rich` 由 `wrapper` 声明形式隐含且禁止书写，但 BIL 是显式 IR，不做该隐含（`SYNTAX.md` §14.9）；
- `shared` class、`shared rich` struct 与 `shared` wrapper 的闭包必须合法；
- 非 rich struct 与非 rich `enum-struct` 不得带 `open` 或 `abstract`；
- `enum-struct` 不得 `open`；
- `singleton` 类型必须同时带 `shared`；
- `abstract` 与 `singleton` 的组合必须合法。

### 8.3 字段声明

字段符号表示源码和语言语义中的逻辑字段/属性，不要求一定对应一块直接存储。字段声明直接使用 canonical 字段符号：

```bil
.field FIELD_SYMBOL [modifiers...]
.static-field FIELD_SYMBOL [modifiers...]
```

例如：

```bil
.field com.example::Service#name@.string pub const readable
.static-field com.example::Service#.static.instanceCount@.i64 internal var readable writable
```

`.field` 要求符号中不含 `.static.`；`.static-field` 要求符号中含 `.static.`。canonical symbol 的 `@字段类型` 是字段声明类型，不得再在声明右侧重复定义另一类型。

字段修饰符可以包括：

```text
pub protected internal priv
const var
ext
backing computed
readable writable
compiler-generated
wrapped(WRAPPER_TYPE_REF)
```

`wrapped(WRAPPER_TYPE_REF)` 可重复出现，声明序 = outer→inner 应用序；语义见 §8.3.1。

字段符号可以关联：

- 直接 backing storage；
- 编译器生成 getter/setter；
- 用户 getter/setter；
- extension field；
- wrapper getter/setter 链。

BIL 的 `get.field` / `set.field` 始终引用逻辑字段 canonical symbol，不引用 Native offset。

#### 8.3.1 wrapper 应用标记

类型声明与字段声明可携带修饰符 `wrapped(WRAPPER_TYPE_REF)`（可重复；声明序 = outer→inner 应用序）。这是 frontend 对 Middleware 的 **wrapper 应用标记**——标明该类型/字段实例在创建时安装了哪些 wrapper；BIL 文本不声明对应的隐藏存储（存储合成归 Middleware，命名约定见 §5.3）。

```bil
.type com.example::Service = class pub wrapped(core.logging::Logged) {
    .field com.example::Service#name@.string pub
    ...
}

.field com.example::Service#config@com.example::Config
    pub wrapped(core.logging::Logged)
```

规则：

- `WRAPPER_TYPE_REF` 必须是 wrapper 类型引用；
- 同一声明上可出现多个 `wrapped(...)`，顺序即 outer→inner；
- **应用 init 实参**不写在 `wrapped(W)` 修饰符上：由宿主实体的合成方法 `..init.wrapper` 体内的 `new.wrapper.*` 指令承载（§9.7 / §14.5）；若 `..init.wrapper` 自身有参数，创建宿主对象须用 `new.wrapped` 家族把这些参数前缀传入（§14.4）；
- 局部变量上的 wrapper 应用标记由 cell 隐藏子类的 `value` 字段 `wrapped(W)` 承载（`.vars` 无新语法；见 §9.3 注记与 `SYNTAX.md` §14.3 统一 cell 存储）；静态/全局字段的 BIL 声明类型投影为 cell 子类、不再在字段槽投 `wrapped(W)`（避免双份隐藏存储；wrapper 标记挂在子类 `value` 字段上）。cell 子类上 `@W(args)` 的实参成为该 cell 类型 `..init.wrapper` 的参数（M109b 生成职责）。

普通 backing 字段（`backing` / `compiler-generated` 等）与本标记无关，按 §8.3 字段修饰符表照常使用。

### 8.4 方法声明

方法声明直接使用完整 canonical 方法符号：

```bil
.method METHOD_SYMBOL [modifiers...]
.static-method METHOD_SYMBOL [modifiers...]
```

例如：

```bil
.method com.example::Service$load(id:.i64)@com.example::User pub
.static-method com.example::App$.static.main(args:.array<.string>)@.i32 pub entrypoint
.method com.example::Number$$plus(another:com.example::Number)@com.example::Number pub operator(plus)
.method com.example::Service$.get.name@.string pub getter(com.example::Service#name@.string)
.method com.example::Service$.set.name@.string priv setter(com.example::Service#name@.string)
```

`.method` 要求普通实例/全局方法符号；`.static-method` 要求符号中含 `.static.`。运算符使用 `$$运算符名称`；getter/setter 使用 `$[.static].get.` / `$[.static].set.`。构造函数使用普通方法 canonical 形式中的方法名 `init`，返回类型写 `.void`。

方法修饰符可以包括：

```text
pub protected internal priv
static ext override abstract
async entrypoint
init
native
symbol("NATIVE_SYMBOL_NAME")
lib("NATIVE_LIBRARY_NAME")
operator(OPERATOR_NAME)
getter(FIELD_SYMBOL)
setter(FIELD_SYMBOL)
enum-case(CASE_SYMBOL)
wrapper-proxy(PROXY_KIND)
```

`wrapper-proxy(PROXY_KIND)` 标记 wrapper 类型声明内的 `.proxy.*` 成员 fn（**proxy 模板**：P3 模板态绑定产物，体内可出现 `invoke fn(..inner)` / `get.self`）。`PROXY_KIND` 取两值之一：

- `specific`：specific proxy 模板（命中成员名的特定代理）；
- `wildcard`：wildcard proxy 模板（类别唯一通配代理）。

`.proxy.` 前缀成员名（§5.1）与本修饰符双向一致：带 `.proxy.` 名的方法必须带本修饰符，带本修饰符的方法名必须以 `.proxy.` 开头；`kind` 与成员形状类别一致（specific ↔ 具名 `.proxy.<成员>` / `.proxy.get.<名>` 等；wildcard ↔ `.proxy.*` / `.proxy.get.*` / `.proxy.set.*` / `.proxy.opr.*`）。同一方法不得重复携带本修饰符。

烘焙（特化合成、inner 链接、原始体替换、存储合成）整体归 Middleware；frontend 只发射 proxy 模板与应用标记，不在 BIL 文本中合成特化/原始体/路由 fn。

`call???` 是 `core::Any` 的 native 内建方法（§22.5 hook），frontend 不为其产 fn 定义（内建无 body 先例，同 `native`）；降级调用点见 §15.5。

运算符、getter、setter 和 enum case 的实现可以拥有 method body，但其调用点在 BIL 中仍使用对应的语义指令；只有普通显式方法调用或规范要求的动态 fallback 使用 `invoke`。

`native` 方法声明由运行时原生方法面提供实现（`SYNTAX.md` §4.6、`RUNTIME.md` §26）：

- `native` 声明**不得**拥有对应的方法 body（`fn` 定义）；
- `symbol("...")` 与 `lib("...")` 必须与 `native` 同时出现且各恰好一次，参数为字符串字面量，分别给出原生符号名与原生库标识；
- `native` 方法的调用点与普通方法相同（`invoke` / `invoke.noret`），实现侧经 §22.5 的内建 hook 或 Middleware 的原生链接解析。

#### 8.4.1 全局函数与全局字段声明

不属于任何类型的全局函数与全局字段，其声明以裸 `.method` / `.field` 形式直接出现在 `LocalSymbols` / `ExternalSymbols` 段内，不包裹在 `.type` 中：

```bil
LocalSymbols {
    .method $main()@.i32 pub entrypoint
}
```

段内条目顺序：类型声明与裸成员声明按生成器输出顺序排列；验证器不得要求裸成员必须位于类型声明之前或之后。

### 8.5 enum case 声明

case 使用源码类型限定形式：

```bil
.case ENUM_TYPE_SYMBOL.CaseName(PARAM_NAME: PARAM_TYPE, ...)
    [discriminant RESOURCE_OR_AUTO]
```

例如：

```bil
.case com.example::RequestResult.Success() discriminant auto
.case com.example::RequestResult.Failed(errorCode:.i32) discriminant res(R_FailedCase)
```

case 名称在其 enum 内唯一。`case(...)` 引用中必须包含完整 enum 类型符号，不能仅写 `.Failed`；源码中的省略类型写法已经由 frontend 解析完成。

`enum-struct` 的普通 `init` 不得作为 `new` 目标。所有 enum 值必须通过 `new.case` 创建。

判别值（S11 注记，2026-08-05）：`discriminant` 的资源必须是整数标量资源
（§19.1），取值非负且在 enum 内唯一；`discriminant auto` 表示编译器按声明序
从 `0` 开始分配（`RUNTIME.md` §16.4）。判别字段宽度 u16/u32 由编译器按
`RUNTIME.md` §16.1 选择（全部判别值可用 u16 表示且自动编号数量不超 u16
容量用 u16，否则 u32）——宽度是布局内部细节，case 声明与 `type.is.case` /
`new.case` 指令均不暴露判别字段符号。

### 8.6 ExternalSymbols 完整性

外部类型和成员可以省略方法体与私有实现信息，但必须提供：

- canonical 类型与成员符号；
- 类型种类与继承/接口关系；
- 影响类型验证的修饰符；
- 字段类型、static 属性、可读写性和可见性；
- 方法完整 BIL 签名、async 属性与调用类别；
- 运算符精确签名；
- enum case 精确签名；
- 泛型约束与 hidden argument 形态。

### 8.7 `..companion.UUID` singleton（Method wrapper 壳体）

静态方法被 Method wrapper 修饰时，frontend（M109b）为**每个被修饰的静态方法**合成一个 companion singleton 类型，UUID 为该静态方法自己的稳定 UUID：

```bil
.type ..companion.<UUID> = class singleton shared pub compiler-generated {
    .method ..companion.<UUID>$<原方法简单名>(...)@Ret pub compiler-generated
        [原方法上的 wrapped(W) 应用标记改挂到本实例方法]
}
```

约定：

- 类型名保留前缀 `..companion.`（§5.1）；`UUID` 段不得为空，且在模块内唯一；
- 类型必须是 `class`，且同时带 `singleton` 与 `shared`（§8.2 singleton 规则）；建议带 `compiler-generated`；
- companion 内生成**实例**方法：方法简单名**沿用原静态方法简单名**（不另加 `..wrapped.` 前缀——companion 类型已隔离命名空间）；该方法承接原静态方法体，并被原 Method wrapper 修饰（`wrapped(W)` 挂在 companion 方法声明上，若 BIL 方法侧暂不投 `wrapped` 则由 Middleware 按 companion 合成契约识别）；
- 原 owner 上保留**同名静态壳体方法**：签名与源码静态方法一致；fn 体仅：取得 companion singleton 实例 → `invoke` / `invoke.noret` 其对应实例方法（实参转发）→ `ret`（有返回时）；
- 用户源码不可声明 `..companion.*` 类型。

> **注记（调用时机）**：companion 的 wrapper 安装走 companion 类型自身的 `..init.wrapper`（若有参则创建 companion 用 `new.wrapped`；singleton 构造时机归 Middleware）。壳体静态方法不负责安装 wrapper。

---

## 9. 函数、参数、局部变量与 block

### 9.1 函数定义

```bil
fn(com.example::Owner$method(value:.i32)@.void) {
    .args {
        ...
    }

    .vars {
        ...
    }

    .block entry entrypoint {
        ...
    }

    .block helper {
        ...
    }
}
```

函数定义必须对应一个 `LocalSymbols` 方法声明。

### 9.2 `.args`

`.args` 重申函数体可引用的语义参数：

```bil
.args {
    .return = RETURN_TYPE,
    .this = OWNER_TYPE,
    .generic.T = .typeid,
    arg0 = TYPE,
    .vargs.args = .array<.any>,
    .kwargs.options = .array<.pair<.string, .any>>
}
```

规则：

- `.return` 必须与方法声明一致；
- void 方法写 `.return = .void`；
- `.this` 仅在签名需要 receiver 时出现；
- 参数名称和顺序必须与方法符号的规范签名一致；
- 参数在函数入口处视为已赋值。

### 9.3 `.vars`

```bil
.vars {
    .i32 counter,
    com.example::User user,
    .typeid runtimeType,
    .breakid loopToken
}
```

规则：

- 局部变量在当前函数内唯一；
- 参数和局部变量使用同一 `$name` 引用形式；
- 普通局部变量在首次读取前必须被明确赋值；
- `.breakid` 只能由 `loop`、`loop.rev` 或 `switch` 绑定；
- `.breakid` 不得由 `load`、`set.var`、参数传入、字段写入、数组写入或普通方法返回产生。

> **注记**：局部变量上的 wrapper 应用标记（源码 `@W(...)` 注解于局部声明）由 cell 隐藏子类的 `value` 字段 `wrapped(W)` 承载（`.vars` 无新语法；统一 cell 存储见 `SYNTAX.md` §5.2 / §14.3 与 §8.3.1）。应用 init 实参见 §9.7 / §14.4 / §14.5。

### 9.4 block

BIL block 是结构化代码 region，不是 LLVM basic block。

每个函数：

- 必须恰有一个 `entrypoint` block；
- block ID 必须唯一；
- block 不能接受独立参数；
- block 共享函数的参数和局部变量；
- block 正常执行到末尾时，返回到引用它的结构化指令；
- entrypoint block 不得正常落到末尾，必须显式 `ret` 或以异常/其他终止流程结束。

### 9.5 block 引用限制

`call`、`if`、`loop`、`switch` 和 `try` 引用的 block：

- 必须存在；
- 必须位于当前函数；
- 不得引用其他函数的 block；
- 不得通过资源或整数伪造。

### 9.6 block 修饰符

标准 block 修饰符为：

```text
entrypoint
volatile
```

`volatile` 表示该 block 内可观察操作的源码顺序必须被保留，不得进行改变其 volatile 语义的重排。具体 LLVM volatile/atomic lowering 由 Middleware 决定。

`atomic[$lock]` 不是当前 Rigi 语法或 BIL 标准的一部分，不得出现在标准 BIL 中。

### 9.7 `..init.wrapper`（实体 wrapper 初始化方法）

编译器为「带有 wrapper 应用、需要在创建时安装 wrapper」的每个实体（class/struct/enum-struct/wrapper 类型本身，或 cell 隐藏子类等合成类型）至多生成**一个**实例方法，保留名：

```text
..init.wrapper
```

canonical 形态示例：

```bil
.method com.example::Service$..init.wrapper(level:.string)@.void
    priv compiler-generated

fn(com.example::Service$..init.wrapper(level:.string)@.void) {
    .args {
        .return = .void,
        .this = com.example::Service,
        level = .string
    }
    .block entry entrypoint {
        new.wrapper.entity type(core.logging::Logged) [$level]
        ret
    }
}
```

规则：

- **保留名**：方法简单名精确为 `..init.wrapper`（§5.1）；用户源码不可声明；
- **每实体至多一个**（同一 owner 类型上不得重载或重复声明）；
- **返回类型**必须为 `.void`；
- **实例方法**（符号不得含 `.static.`；`.args` 含 `.this = OWNER`）；
- **可见性 / 修饰符**：`priv` + `compiler-generated`（仿合成 fn 惯例；验证器要求二者均在）；
- **允许参数**：当 wrapper 应用带 init 实参、或 cell 子类需把字段/局部上 `@W(args)` 的实参传入时，这些值成为本方法的规范序参数；参数名由 frontend 分配（稳定、唯一）；无 init 实参时参数列表可为空；
- **方法体**：仅允许普通数据/控制流指令，以及 §14.5 的三条 `new.wrapper.*` 指令（安装本实体相关 wrapper）；不得 `new` 本实体（防递归构造约定由 frontend 遵守）；
- **调用时机（规范注记）**：本方法在实体 **init 之前**由 Middleware/VM **自动调用**；frontend **无法介入**调用时机，也不得在普通用户方法中显式 `invoke` 本方法（验证器可对非合成调用点给出诊断，Middleware 以自动调用为准）。有参时，调用方通过 §14.4 `new.wrapped` 把前缀实参传入构造路径，由运行时转交给本方法。

**M109b 生成职责（本步不实现 frontend，仅定契约）**：

- 类型声明带 `wrapped(W)`（Entity）或成员字段带 `wrapped(W)`（字段-Value）时，在 owner 类型上合成 `..init.wrapper`，体内按 outer→inner 对每个应用发对应 `new.wrapper.entity` / `new.wrapper.field`；
- 方法带 Method wrapper 时：静态方法走 §8.7 companion；实例方法在 owner 的 `..init.wrapper` 内发 `new.wrapper.method`（或按 Middleware 约定在方法首次绑定前安装——以 §14.5 指令语义为准，frontend 在 M109b 按应用表发射）；
- cell 子类：`value` 字段上每个 `wrapped(W)` 的应用实参提升为 cell 类型 `..init.wrapper` 的参数；`new ..cell..UUID(...)` / `new.wrapped` 在变量初始化点传入这些实参。

---

## 10. 操作数与求值规则

### 10.1 指令只接受变量与符号表达式

普通指令操作数只能是：

- `$variable`；
- `fn(...)`；
- `field(...)`；
- `type(...)`；
- `case(...)`；
- `blk(...)`；
- `res(...)`；
- 结构位置中的 `none`。

用户字面量不得直接出现。

### 10.2 从左到右求值

当一条指令包含多个可能产生可观察行为的语义输入时，其语义顺序按操作数从左到右确定。

大多数 BIL 操作数已经是局部变量，因此 frontend 应在前置指令中显式完成复杂表达式求值。

### 10.3 结果变量

一条产生结果的指令必须把结果写入显式目标变量。目标变量的声明类型必须严格满足该指令的结果约束。

写入已初始化变量等价于覆盖旧值。ValueType 复制、引用 acquire/release、Box unique 数据复制及销毁由 Middleware 按 `RUNTIME.md` 实现。

---

## 11. 运算指令

### 11.1 统一规则

除比较指令另有规定外，二元运算指令必须满足：

```text
type(OPR1) == type(OPR2)
```

结果类型由以下精确键唯一确定：

```text
opcode + operand type + declared result type
```

验证器必须确认该类型存在唯一精确运算实现，或该组合是内建 intrinsic。

这不是 source-level overload ranking；不存在隐式转换、候选优先级或“最佳匹配”。

### 11.2 算术运算

```bil
add OPR1 OPR2 RESULT
sub OPR1 OPR2 RESULT
mul OPR1 OPR2 RESULT
div OPR1 OPR2 RESULT
opposite OPR RESULT
```

对应 Rigi 运算符：

| BIL | Rigi operator |
|---|---|
| `add` | `plus` |
| `sub` | `minus` |
| `mul` | `times` |
| `div` | `div` |
| `opposite` | `opposite` |

对于内建整数/浮点类型，Middleware 可以直接生成 LLVM 算术指令。对于用户类型，Middleware 按精确类型选择唯一运算实现。

内建整数算术（`add`/`sub`/`mul`/`opposite`）溢出行为为**二进制补码回绕**
（two's-complement wraparound）：不产生异常、不饱和、不截断诊断；有符号与
无符号类型同例。VM 参考实现（§22）必须逐字复现该行为。浮点类型遵循
IEEE 754（产生 inf/NaN，不抛异常）。

`add` 作用于两个 `.string` 操作数时是**内建字符串拼接**（Rigi `String` 的 `+`）：按值语义产出一个新字符串，VM 内建执行，不属于 `rigi_rt` 原生方法面（RUNTIME §26）。

### 11.3 逻辑运算

```bil
and OPR1 OPR2 RESULT
or OPR1 OPR2 RESULT
not OPR RESULT
```

这些指令表示**两个输入均已求值**的类型驱动逻辑运算。

内建 `.bool` 的 source-level `and` / `or` 具有短路语义，因此 frontend 不得把短路表达式简单生成为一条 `and` / `or`。它必须使用 `if` 与临时变量表达条件求值。

只有以下情况可以直接发出 `and` / `or`：

- 对非短路的用户重载运算；
- frontend 已经证明两侧均应求值的规范化形式。

### 11.4 位运算

```bil
bin.and OPR1 OPR2 RESULT
bin.or OPR1 OPR2 RESULT
bin.xor OPR1 OPR2 RESULT
bin.not OPR RESULT
shift.left VALUE BITS RESULT
shift.right VALUE BITS RESULT
shift.right.unsigned VALUE BITS RESULT
```

标准 BIL 仍要求 `VALUE` 与 `BITS` 的类型严格相同。若源码运算符声明接受不同的 `TBits`，frontend 必须先规范化为满足 BIL 规则的类型，或在无法等价规范化时使用已解析的普通方法调用表示该显式实现。

legacy opcode：

```text
movl → shift.left
movr → shift.right
```

标准生成器不得输出 `movl` / `movr`。

### 11.5 比较运算

```bil
cmp.eq OPR1 OPR2 RESULT_BOOL
cmp.ne OPR1 OPR2 RESULT_BOOL
cmp.lt OPR1 OPR2 RESULT_BOOL
cmp.le OPR1 OPR2 RESULT_BOOL
cmp.gt OPR1 OPR2 RESULT_BOOL
cmp.ge OPR1 OPR2 RESULT_BOOL
```

规则：

- 两个操作数类型必须严格相同；
- 结果变量必须为 `.bool`；
- `cmp.eq` / `cmp.ne` 使用 `equals` 语义；
- 排序比较使用 `compareTo` 与 `core.ComparisonResult` 语义；
- `cmp.ne` 可以由 `cmp.eq` + `not` 实现；
- `<`、`<=`、`>`、`>=` 可以由精确 `compareTo` 结果实现。

legacy opcode：

```text
cmp.bg  → cmp.gt
cmp.beq → cmp.ge
```

---

## 12. 转换、wrapper 与运行时类型指令

### 12.1 强制转换

```bil
cast SOURCE RESULT type(TARGET_TYPE)
cast.indirect SOURCE RESULT TYPEID_VAR
```

`cast` 表示 Rigi 的强制转换语义，包括：

1. 源类型的 `castTo`；
2. 目标类型的 `castFrom`；
3. 内建引用视图转换和数值转换。

内建引用视图转换涵盖：

- 值类型到 `Object`/`Any` 分支的装箱视图（`RUNTIME.md` §4）；
- 派生类到基类/接口的视图改写（无数据移动）；
- `T` 到 `.nullable<T>` 的装箱视图，以及 `.nullable<T>` 到 `T` 的展开——后者在源为 `null` 时抛 `core.CastException`（Rigi 层 `nullableVar as T` 即此语义）。

优先级与失败行为必须与 `SYNTAX.md` 一致。失败抛出 `core.CastException`。

`cast.indirect` 的 `TYPEID_VAR` 必须为适合 RESULT 静态边界的 `.typeid<TBound>`。

### 12.2 安全转换

```bil
cast.safe SOURCE RESULT type(TARGET_TYPE)
cast.safe.indirect SOURCE RESULT TYPEID_VAR
```

结果类型必须为对应目标类型的 nullable 形式。转换失败时产生 `null`，不得抛出 `core.CastException`。

frontend 也可以使用 `type.is` + `if` + `cast` 生成等价形式。

### 12.3 类型检查

```bil
type.is VALUE type(TARGET_TYPE) RESULT_BOOL
type.is.indirect VALUE TYPEID_VAR RESULT_BOOL

type.supers VALUE type(TARGET_TYPE) RESULT_BOOL
type.supers.indirect VALUE TYPEID_VAR RESULT_BOOL

type.with VALUE type(WRAPPER_TYPE) RESULT_BOOL
type.with.indirect VALUE TYPEID_VAR RESULT_BOOL
```

结果必须为 `.bool`。语义分别对应 Rigi 的 `is`、`supers` 和 `with`，实际 TypeSheet 查询规则由 `RUNTIME.md` 定义。

enum case 判别检查（`S11`，2026-08-05 定稿；对应 Rigi 的 `value is .Case`，语义由 `RUNTIME.md` §16.3 定义）：

```bil
type.is.case VALUE case(CASE_SYMBOL) RESULT_BOOL
```

比较 VALUE 的隐藏判别字段与该 case 编译期判别常量的整数相等性——不是子类型检查，不访问 `TypeSheet.baseTypeId`，不比较任何用户字段，不改变 VALUE 的静态类型；`typeOf` 语义不变（始终返回该 enum struct 的类型）。

规则：

- CASE_SYMBOL 必须是 `ENUM_TYPE_SYMBOL.CaseName` 形态，且以 `ENUM_TYPE_SYMBOL` 为 owner（即 §8.5 的 case 声明，不能是裸 `.CaseName`）；
- VALUE 的严格静态类型必须等于 case 的 enum 类型；
- RESULT_BOOL 必须为 `.bool`；
- 判别字段宽度（u16/u32，`RUNTIME.md` §16.1）是布局内部细节：该指令不暴露判别字段符号，Middleware 按 case 的编译期判别常量与 enum 宽度执行整数比较。

### 12.4 取得 wrapper 值

```bil
get.wrapper VALUE type(WRAPPER_TYPE) RESULT
get.wrapper.indirect VALUE WRAPPER_TYPEID_VAR RESULT
get.wrapper.field OBJECT field(HOST_FIELD) type(WRAPPER_TYPE) RESULT
```

语义对应路径表达式 `value:WrapperType`：取得绑定到 VALUE 的指定 wrapper 实例。

规则：

- WRAPPER_TYPE 必须是 wrapper 类型；
- VALUE 的精确类型必须具有该 wrapper；
- RESULT 类型必须严格等于 WRAPPER_TYPE；
- 不存在对应 wrapper 时的行为必须与语言/运行时 wrapper 规则一致；
- 该指令不等价于普通字段读取。

`get.wrapper.field`（字段-Value 应用形态）：从属主对象 OBJECT 上挂在
HOST_FIELD 的 Value wrapper 应用取得 wrapper 值拷贝（随后可对 RESULT 发
普通 `invoke` / `get.array` / `get.field`）。

规则：

- HOST_FIELD 必须是实例字段，且带 `wrapped(WRAPPER_TYPE)` 应用标记（§8.3.1）；
- OBJECT 的严格静态类型必须可赋值到 HOST_FIELD 的 owner；
- WRAPPER_TYPE 必须是 wrapper 类型；RESULT 类型必须严格等于 WRAPPER_TYPE；
- 该指令只产值拷贝，不写入字段应用存储。

> **S11 定稿注记（2026-08-05；M84 补字段-Value；读侧统一为值拷贝）**：
> `obj:Wrapper` 是只读 place（`SYNTAX.md` §14.5），不能整体取值；因此
> `get.wrapper` / `get.wrapper.field` 在源码可达路径上无直接对应物，保留为
> lowering/VM 内部能力——**全部成员读取**统一为值拷贝后普通指令：
> Entity 应用 = `get.wrapper` + 普通 `get.field`/`invoke`/`get.array`；
> 字段-Value 应用 = `get.wrapper.field` + 普通指令；嵌套 wrapper 链逐层
> 物化后继续普通 `get.field`。成员**写入**与 proxy 体内 `this` 的原地写
> 使用 §13.3 的 `set.wrapper.field`。字段-Value 应用写寻址编码为已有链
> 元素相邻对 `field(HOST_FIELD), wrapper(W)`（HOST_FIELD 带 `wrapped(W)`，
> §8.3.1；同 owner 多字段同 W 由此区分）；Entity 类型应用仍为单独
> `wrapper(W)`。深层写穿 `place.a.b... = rhs` 在 frontend P4a 展开为多个
> 现有 get/set 系列（**不新增**专用深写 opcode；最外层必要写回复用
> `set.wrapper.field`，普通值类型中间层反向写回仍发 `set.field`，不把
> 整条深路径压进单条超长链）。`obj:W = ...` 整体赋值是源码层编译错误，
> BIL 不需要写入指令。存储由 Middleware 合成（命名约定 §5.3）；应用标记
> 见 §8.3.1。

### 12.5 取得宿主实例（proxy 模板）

```bil
get.self RESULT
```

仅 proxy 模板 fn（带 `wrapper-proxy`，§8.4）体内合法。语义对应源码层 `self`：取被修饰宿主实例。

规则：

- 必须出现在 `wrapper-proxy(specific|wildcard)` 标记的方法体内；出现在其他 fn 内非法；
- RESULT 类型必须严格等于该模板 fn 所属 wrapper 的 `TTarget` 泛型参数（Entity wrapper 恰一泛型参数时的代入结果）；
- 零泛型参数的 wrapper 模板内出现本指令即非法；
- 该指令不读取字段，不产生 wrapper 值拷贝。

### 12.6 取得 typeid

```bil
getid.type type(TYPE_SYMBOL) TARGET_TYPEID
getid.var VALUE TARGET_TYPEID
getid.field field(FIELD_SYMBOL) TARGET_FIELDID
```

规则：

- `getid.type` 返回指定类型的运行时 typeid；
- `getid.var` 返回值的实际运行时类型，Object 使用对象实际类型而非仅静态视图；
- `getid.field` 返回携带字段签名的 `.fieldid<...>`。

---

## 13. 值、变量、字段与索引指令

### 13.1 资源加载

```bil
load res(RESOURCE_ID) TARGET
```

资源的声明类型必须与 TARGET 类型严格相同。

### 13.2 局部变量读取与写入

```bil
get.var SOURCE_VAR TARGET_VAR
set.var SOURCE_VAR TARGET_VAR
```

规则：

- `get.var` 表示对逻辑局部变量/全局变量的 getter 读取；
- `set.var` 表示对逻辑可变变量的 setter 写入；
- 源值与目标逻辑变量类型必须严格相同；
- 对普通无 getter/setter 的临时变量，这些操作可以 lower 为普通复制；
- 对带 getter/setter 或 Value Wrapper 的变量，Middleware 按精确变量类型与符号元数据执行对应语义；
- 向 `const` 或不可写变量执行 `set.var` 非法。

普通 `$temp` 作为其他指令的输入表示读取一个已经物化的临时/参数值。frontend 对源码中带 getter 的变量读取应显式生成 `get.var`，不得通过裸 `$var` 绕过 getter。

### 13.3 实例字段

```bil
get.field OBJECT TARGET field(FIELD_SYMBOL)
set.field SOURCE OBJECT field(FIELD_SYMBOL)
```

验证规则：

- FIELD_SYMBOL 必须表示实例字段；
- OBJECT 的严格静态类型必须能访问该字段（含沿继承链访问基类声明的字段）；
- `get.field` 的 TARGET 类型必须严格等于字段声明类型；字段声明类型是宿主编泛型参数（或含宿主编泛型参数）时，按「OBJECT 严格静态类型到字段宿主的构造实参」替换后判定严格相等；
- `set.field` 的 SOURCE 类型必须严格等于字段声明类型（泛型情形同上前款）；
- `set.field` 要求字段可写；
- 访问权限必须合法。

`get.field` / `set.field` 不预先降为 `invoke`。Middleware 根据精确 owner 类型、字段符号和访问类别选择：

- 直接字段 load/store；
- getter/setter；
- interface 字段访问器；
- extension field；
- wrapper specific/wildcard proxy；
- runtime dynamic fallback；
- GC/ARC 屏障与共享域操作。

wrapper 隐藏存储写后门（对应 `obj:Wrapper.field` 写入、proxy 体内
`this.field` 原地写；**读取**一律走 §12.4 值拷贝 + 普通 `get.field`，
不经本指令）：

```bil
set.wrapper.field SOURCE OBJECT CHAIN_ELEM... field(INNER_FIELD)
```

- `set.wrapper.field`：wrapper 隐藏存储**写入后门**（**不是**普通 `set.field`）。CHAIN 必须表达 wrapper 存储寻址——至少含一个 `wrapper(W)`（Entity = `wrapper(W)`；字段-Value = 相邻 `field(HOST_FIELD)+wrapper(W)`）。普通值类型中间层的反向写回由 frontend lowering 生成普通 `set.field`，不走本指令。

链元素 `CHAIN_ELEM` 取两态之一（**不**新增第三态 opcode/操作数类）：

- `field(FIELD)`：沿普通实例字段下钻；或作为字段-Value 应用对的 `HOST_FIELD`（见下）；
- `wrapper(WRAPPER_TYPE_REF)`：沿宿主的 wrapper 应用下钻到该 wrapper 的隐藏存储（应用标记见 §8.3.1；存储由 Middleware 合成，命名约定 §5.3），再继续后续链元素或末段字段访问。

**字段-Value 应用**编码为相邻对 `field(HOST_FIELD), wrapper(W)`：HOST_FIELD
必须是实例字段且带 `wrapped(W)`（§8.3.1）；当前位置转为 W 的隐藏存储。
同 owner 上多个字段应用同一 W 时，靠不同的 `field(HOST_FIELD)` 区分。
**Entity 类型应用**仍为单独的 `wrapper(W)`（当前位置类型声明带 `wrapped(W)`）。

首段可以是 `field(...)` 或 `wrapper(WRAPPER_TYPE)`。例如：

```bil
set.wrapper.field $v $obj wrapper(core.logging::Logged) field(core.logging::Logged#level@.i32)
set.wrapper.field $v $hero field(com.example::Hero#mp@.i32) wrapper(core.clamp::Clamped) field(core.clamp::Clamped#min@.i32)
```

读侧对应形态（值拷贝，§12.4）：

```bil
get.wrapper $obj type(core.logging::Logged) $w
get.field $w $x field(core.logging::Logged#level@.i32)
get.wrapper.field $hero field(com.example::Hero#hp@.i32) type(core.clamp::Clamped) $w
get.field $w $m field(core.clamp::Clamped#min@.i32)
```

规则：

- 链至少含一个链元素，末段必须是 `field(INNER_FIELD)`（被写的目标字段）；
- 相邻 `field(F), wrapper(W)` 且 F 声明带 `wrapped(W)` 时视为**字段应用对**：OBJECT/当前位置必须可赋值到 F 的 owner，F 必须是实例字段，位置转为 W；F 带其它 `wrapped(*)` 但不匹配 W 则非法；
- 单独的 `wrapper(WRAPPER_TYPE)` 为**类型应用**：要求当前位置静态类型的类型声明具有该 wrapper 应用标记（§8.3.1；外部/无法解析的类型引用可降级）；
- 普通 `field(FIELD)`（非字段应用对之首）要求 FIELD 是当前位置静态类型可访问的实例字段，位置转为该字段类型；
- `set.wrapper.field` 的 SOURCE 类型必须严格等于末段 INNER_FIELD 的字段类型，且 INNER_FIELD 可写；链必须含至少一个 `wrapper(...)`（wrapper 后门形态，禁止用纯 field 链冒充普通字段写入）；
- 语义为对嵌套位置做**原地写入**（经 wrapper 元素时不产生 wrapper 值拷贝）；
  只读 place 整体不可赋值，故不存在「对整个 place 写回」的指令形态；
- proxy 体内 `this` 的成员写与 `obj:Wrapper` 同构：OBJECT 取宿主（`.this` 或具体对象），链以 `wrapper(该模板所属 wrapper 类型)` 起。

### 13.4 静态字段

```bil
get.field.static TARGET type(OWNER_TYPE) field(FIELD_SYMBOL)
set.field.static SOURCE type(OWNER_TYPE) field(FIELD_SYMBOL)
```

FIELD_SYMBOL 必须表示 OWNER_TYPE 的 static 字段，值类型必须严格匹配。

Singleton 的实例字段不得因为 owner 为 singleton 而使用 static 指令；是否 static 由字段声明决定。

### 13.5 间接字段

```bil
get.field.indirect OBJECT TARGET FIELDID_VAR
set.field.indirect SOURCE OBJECT FIELDID_VAR

get.field.static.indirect TARGET TYPEID_VAR FIELDID_VAR
set.field.static.indirect SOURCE TYPEID_VAR FIELDID_VAR
```

`FIELDID_VAR` 必须携带完整 owner、值类型和 instance/static 类别。验证器必须据此检查 TARGET/SOURCE 类型。

运行时字段 ID 不得是无签名的裸整数。

### 13.6 索引读取与写入

```bil
get.array COLLECTION INDEX RESULT
set.array COLLECTION INDEX ELEMENT
```

这些指令表示 Rigi 的 `getAtIndex` / `setAtIndex` 语义，不预先降为方法调用。

验证器使用严格三元组查询：

```text
get.array: collection type + index type + result type
set.array: collection type + index type + element type
```

必须存在唯一精确实现，且不得插入隐式转换。

Middleware 可将其 lower 为：

- `Array\<T>` 统一胖值槽访问；
- `Span\<T>` 原生 stride 地址计算；
- 用户索引运算符；
- wrapper 运算符代理；
- bounds check 与 runtime helper。

---

## 14. 构造指令

### 14.1 静态普通构造

```bil
new type(TYPE_SYMBOL) RESULT [ARG_0, ARG_1, ...]
```

规则：

- TYPE_SYMBOL 必须是可普通构造的具体 canonical 类型符号；
- 参数已经完成默认值填充与具名参数重排；
- 泛型构造所需的 `.generic.<Name>` hidden arguments 已按第 7 节放入参数列表；
- 参数类型必须与一个 init 的规范 BIL 签名严格匹配；
- 验证器必须确认该精确签名唯一；
- RESULT 类型必须严格等于构造结果类型；
- abstract 类型和 enum struct 不得使用该指令。

Middleware 根据目标类型和精确参数类型选择 init；不执行 source-level overload ranking。

### 14.2 动态普通构造

```bil
new.indirect TYPEID_VAR RESULT [ARG_0, ARG_1, ...]
```

语义对应 `new typeValue(...)` 和泛型 `T()`。运行时按实际 typeid 的 init 表解析严格匹配的构造入口。

目标为 abstract 类型、enum struct 或无匹配 init 时抛出 `core.NoSuchMethodException`。

### 14.3 enum case 构造

```bil
new.case type(ENUM_TYPE) case(ENUM_TYPE.CaseName) RESULT [ARG_0, ARG_1, ...]
```

规则：

- ENUM_TYPE 必须是 enum struct；
- case 符号必须以 ENUM_TYPE 为 owner；
- 参数类型必须严格匹配 case 入口；
- RESULT 必须严格为 ENUM_TYPE；
- 固定 case 使用空参数列表；
- 该指令是 enum 值的唯一标准构造形式。

enum struct **没有零值**。enum 类型的存储位置（`.vars` 条目、字段、数组
元素）在被读取前必须已经由 `new.case` / `new.wrapped.case` 构造写入：

- frontend 经 DA 与 init 编织保证该不变量；
- 验证器拒绝「声明了 enum struct 字段、但宿主类型存在未在全路径写入该
  字段的 init」的模块（§21.8）；局部变量读前未写由既有 DA 规则拒绝；
- VM / 运行时读到未构造的 enum 槽一律按宿主错误处理，不存在「默认
  case」兜底。

推论：`.array<enum struct>` 的批量零初始化与本条冲突——
`arrayOf\<T>(size)`（`RUNTIME.md` §26）在 T 为 enum struct 时由 frontend
在泛型实例化点拒绝（编译期诊断）；`arrayOfElements\<T>(elements...)`
不受限（元素逐项显式给出）。

### 14.4 `new.wrapped` 家族（有参 `..init.wrapper` 的实体构造）

当目标类型声明了**带参数**的 `..init.wrapper`（§9.7）时，创建该类型实例**必须**使用本家族指令；无参 `..init.wrapper` 或未声明 `..init.wrapper` 的类型**禁止**使用本家族，仍用普通 `new` / `new.case`（§14.1 / §14.3）。

#### 14.4.1 普通构造

```bil
new.wrapped type(TYPE_SYMBOL) RESULT [WRAPPER_ARG_0, ...] [INIT_ARG_0, ...]
```

**形态定稿理由**：与 `new type(TYPE) RESULT [ARGS]` 同构，仅在 RESULT 后增加**第二个**实参列表。两个相邻 `[...]` 列表在词法上无二义（无需查表即可分段）；第一表 = `..init.wrapper` 参数（声明序），第二表 = 命中的 `init` 重载实参（已完成默认值填充与具名重排，同 §14.1）。平铺单表会在未知 `..init.wrapper` 元数时无法分段，故不采用。

规则：

- TYPE_SYMBOL / RESULT / abstract / enum-struct 限制同 §14.1（enum-struct 不得用本指令，改用 §14.4.2）；
- TYPE 必须恰好声明一个 `..init.wrapper`，且其规范参数列表**非空**；
- `WRAPPER_ARG_*` 个数与类型必须与该 `..init.wrapper` 签名**严格匹配**（复用 §3.3 调用映射后的规范序；hidden generic 规则同普通 invoke）；
- `INIT_ARG_*` 必须与 TYPE 上唯一匹配的 `init` 规范签名严格匹配（同 §14.1）；
- 与普通 `new` **互斥**：有参 `..init.wrapper` 的类型上出现 `new` → 非法；无参或未声明 `..init.wrapper` 的类型上出现 `new.wrapped` → 非法。

语义：运行时先分配实例，以 `WRAPPER_ARG_*` 调用 `..init.wrapper`（实体 init 之前，§9.7），再以 `INIT_ARG_*` 调用命中的 `init`。

#### 14.4.2 enum case 构造

```bil
new.wrapped.case type(ENUM_TYPE) case(ENUM_TYPE.CaseName) RESULT
    [WRAPPER_ARG_0, ...] [CASE_ARG_0, ...]
```

规则：在 §14.3 之上叠加与 §14.4.1 相同的 wrapper 前缀实参与互斥规则（`new.case` ↔ `new.wrapped.case`）。

### 14.5 wrapper 初始化指令（仅 `..init.wrapper` 体内）

以下三条指令**只能**出现在方法简单名为 `..init.wrapper` 的 fn 体内（§9.7）；其它 fn 中出现一律非法。语义：按 ARGS 调用 wrapper 类型的 init 重载，将结果写入 Middleware 合成的隐藏存储（命名约定 §5.3）；**无结果变量**（不产生可流动的 wrapper 值）。

```bil
new.wrapper.field field(FIELD_SYMBOL) type(WRAPPER_TYPE) [ARG_0, ...]
new.wrapper.method fn(METHOD_SYMBOL) type(WRAPPER_TYPE) [ARG_0, ...]
new.wrapper.entity type(WRAPPER_TYPE) [ARG_0, ...]
```

操作数序（对齐 `get.wrapper` / `field(...)` / `fn(...)` / `type(...)` 既有拼写）：

| 指令 | 操作数 |
|------|--------|
| `new.wrapper.field` | `field(FIELD)` → `type(WRAPPER_TYPE)` → `[ARGS]` |
| `new.wrapper.method` | `fn(METHOD)` → `type(WRAPPER_TYPE)` → `[ARGS]` |
| `new.wrapper.entity` | `type(WRAPPER_TYPE)` → `[ARGS]` |

规则：

- `WRAPPER_TYPE` 必须是 wrapper 类型引用；
- `ARGS` 必须与 `WRAPPER_TYPE` 上唯一匹配的 `init` 规范签名严格匹配（§3.3 / §14.1 同一套严格匹配；含默认值填充后的规范序）；
- `new.wrapper.field`：`FIELD` 必须是当前 `..init.wrapper` 宿主类型（或沿继承可见）的实例字段，且该字段声明带 `wrapped(WRAPPER_TYPE)`（§8.3.1）；
- `new.wrapper.method`：`METHOD` 必须是当前宿主上的方法符号（实例或静态——静态方法的 Method wrapper 通常改挂 companion，见 §8.7；本指令用于仍挂在宿主上的方法应用）；
- `new.wrapper.entity`：当前宿主类型声明必须带 `wrapped(WRAPPER_TYPE)`；
- 同一 `..init.wrapper` 体内，对同一目标（同一 FIELD / 同一 METHOD / entity×同一 W）的重复初始化非法（验证器可检静态重复）；
- 指令不读/写普通局部结果；ARGS 中的变量按 §21.4 DA 规则须已赋值。

---

## 15. 方法调用

### 15.1 直接调用

```bil
invoke fn(METHOD_SYMBOL) RESULT [ARG_0, ARG_1, ...]
invoke.noret fn(METHOD_SYMBOL) [ARG_0, ARG_1, ...]
```

规则：

- 参数列表必须与方法的完整规范 BIL 参数签名逐项严格相同；`.return` 是结果描述，不是调用实参；
- 参数列表包含 `.this`、泛型 hidden args、普通参数和 vararg/kwarg 包；
- 值类型方法的 `.this` 实参**别名**调用点 place（不产生拷贝），方法体内经 `.this` 的字段写原地生效（`SYNTAX.md` §10）；引用类型 `.this` 为普通引用传递；
- 返回方法使用 `invoke`，无返回方法使用 `invoke.noret`；
- RESULT 类型必须等于调用表达式类型；
- 调用顺序不代表 Native ABI。

### 15.2 async 方法调用

对带 `async` 修饰的方法使用同一 `invoke` opcode。

若方法声明体返回：

```text
TResult
```

则调用表达式结果必须为：

```text
core.coroutine.Task\<TResult>
```

无结果 async 方法的调用结果必须为：

```text
core.coroutine.Task
```

`invoke` async 方法必须具有 `RUNTIME.md` 规定的 eager spawn 语义。Middleware 不得把它实现为惰性 Task。

async receiver、参数和结果的 shared 闭包合法性应由 frontend 检查，并由 BIL verifier 复核可验证部分。

### 15.3 间接调用

```bil
invoke.indirect OBJECT_VAR RESULT [ARG_0, ARG_1, ...]
invoke.indirect.noret OBJECT_VAR [ARG_0, ARG_1, ...]
```

`OBJECT_VAR` 是静态类型声明了 `operator call` 的对象引用。间接调用 = 对该对象虚调用其 `$$call` 实现（通用 callable 协议；任何实现 `operator call` 的类型都可用，不限于 lambda 隐藏类）。

规则：

- `OBJECT_VAR` 的静态类型必须声明恰好一个与实参列表逐类型严格匹配、且返回形态匹配的 `$$call`（canonical 名 `$$call`，§8.4 operator 声明形态）；
- 参数与结果严格匹配该 `$$call` 签名（canonical 全等，同 §15.1 口径）；
- 有返回使用 `invoke.indirect`，无返回使用 `invoke.indirect.noret`；
- 若命中的 `$$call` 带 `async`（如 `core::AsyncFunc` / `core::AsyncAction` 子类的实现），按 §15.2 同一规则：使用 `invoke.indirect`（非 noret），结果为 `core.coroutine.Task\<TResult>` / `core.coroutine.Task`，eager spawn。

**泛型 ABI（与 §15.1 direct invoke 完全同构）**：

- 当命中的 `$$call` 为泛型方法时，其 fn `.args` 携带 `.generic.T = .typeid` 隐藏条目（固定泛型）与/或 `.generic.TArgs`/`.generic.TValues` 包形态（§7.1/§7.2）；
- 调用点实参列表前部平铺 typeid 前缀：固定泛型以 `getid.type type(...)` 物化的 `.typeid` 临时（嵌套转发时为 `$.generic.T`），泛型可变包以 `.array<.typeid<.any>>` / `.map<.string, .typeid<.any>>` 打包物化——序与形态同 direct invoke；
- 值实参（含 `.vargs.*`/`.kwargs.*` 包）紧随 typeid/包前缀之后；
- 验证器按被调 `$$call` 的 fn 定义 hidden 条目数跳过前缀并校验前缀类型，再比对普通参数与值包（缺/错 typeid 前缀非法）。

lambda 的 BIL 形态是普通 `new type(..lambda..UUID)` 构造 + `invoke.indirect`；BIL 没有 lambda 专属指令。

### 15.4 调用派发链下一环（proxy 模板）

```bil
invoke fn(..inner) RESULT [ARG_0, ARG_1, ...]
invoke.noret fn(..inner) [ARG_0, ARG_1, ...]
```

仅 proxy 模板 fn（带 `wrapper-proxy`，§8.4）体内合法。语义对应源码层 `inner(...)`：以普通 invoke 家族的保留目标 `fn(..inner)` 表达「调用派发链下一环」的占位；Middleware 烘焙时将该调用链接到下一环（下一特化、原始体或类别路由）。

规则：

- 必须出现在 `wrapper-proxy(specific|wildcard)` 标记的方法体内；出现在其他 fn 内非法；
- `fn(..inner)` 是保留目标，不是 canonical 方法符号，不查 `MethodSymbols`，也**不携带 receiver**；
- 操作数序对齐 §7.2 调用序子集：**先**模板 fn 声明中的**可变泛型包**隐藏参数（`.generic.<Pack>`，按声明序；固定泛型参数不出现在本列表——特化侧由 Middleware 自持），**再**源码层 `inner(...)` 的显式值实参（含 `.kwargs.*` / `.vargs.*` 包整体转发；wildcard 的 `symbol` 不重复出现——下一环 ABI 由 Middleware 知道）。例如 wildcard 模板：
  `invoke fn(..inner) $.t0 [$.generic.TNamedArgs, $.generic.TUnnamedArgs, $.kwargs.namedArgs, $.vargs.unnamedArgs]`；
- 源码语法不变：`inner(...)` 只写值实参；泛型包由 frontend 在 Bound/Lowered 层显式携带并在调用前置物化，Middleware 消费解包/烘焙；
- 带返回的模板用 `invoke fn(..inner)`，RESULT 类型必须严格等于该模板 fn 的声明返回类型；void 模板用 `invoke.noret fn(..inner)`；
- 值实参个数与类型必须与模板 fn 声明的（经源码 `inner` 规则过滤后的）显式实参一致；前置 `.generic.*` 操作数必须可解析为当前 fn `.args` 中已声明的同名隐藏参数。

### 15.5 直接基类调用

```bil
invoke fn(..super) RESULT [$.this, HIDDEN_GENERIC_ARGS..., NORMAL_ARGS...]
invoke.noret fn(..super) [$.this, HIDDEN_GENERIC_ARGS..., NORMAL_ARGS...]
```

`fn(..super)` 是保留目标，不是 canonical 方法符号，也不得声明为普通 fn。它只可由 override 或 init fn 体发出，交 Middleware 解析为直接基类的原始实现并绕过 wrapper 派发链。首实参必须精确为 `$.this`；随后按 §7.2 的隐藏泛型参数、普通值参数顺序排列。init 必须使用 `invoke.noret`；override 的 invoke 形态与当前 fn 返回类型一致。frontend 不生成 `..create`：`..create` 仅是 Middleware/VM 的 create 生命周期阶段步骤，可与 super-init 和 init `_ -> inheritedField` 映射共存。

### 15.6 wrapper 动态 fallback

对于静态类型上没有声明、但根据 `SYNTAX.md` 必须降级到 wildcard proxy 的普通方法请求，frontend 生成对 `core::Any$call???` 的普通 `invoke`。

已声明的：

- 运算；
- getter；
- setter；
- 索引操作；

仍使用各自 BIL 指令，不因最终可能经过 wrapper 路由而预先改写为 `invoke`。

`call???` 的规范签名是 `RUNTIME.md` §14.2 泛型逻辑签名的实质化——`(symbol: .string, namedArgs: .array<core::Pair<.string, .any>>, unnamedArgs: .array<.any>): .any`（非泛型；实参的装箱/拆包转换沿用 §12.1 cast 语义）。降级调用点 `invoke` 的目标**恒为** `core::Any$call???`（vtable 正常解析继承）；实参规范序 = receiver、symbol 字符串资源、具名包构造、位置包构造；返回值为 `.any` 胖值，调用点按期望类型插入一次 §12.1 cast（不符抛 `core.CastException`）。

frontend 判定降级资格（静态类型无声明方法且 wrapper 链含 `.proxy.*`）只读应用标记（§8.3.1）与 proxy 声明，不合成任何符号；被命中宿主的类别路由体由 Middleware 合成（`RUNTIME.md` §14.2）。`call???` 自身是 `core::Any` 的 native 内建方法，无 BIL fn 定义，行为见 §22.5。

### 15.6 canonical symbol 与参数包

当调用 `call???` 或 wrapper wildcard 需要 canonical symbol 时：

- canonical symbol 格式遵循 `SYNTAX.md` / `RUNTIME.md`（未声明方法的降级请求 symbol 格式定稿见 `SYNTAX.md` §14.8 末段——显式泛型实参在方法名后的 `<...>` 段按 canonical 类型引用编码，参数段只带调用点静态类型、返回段恒 `.any`）；
- `.generic.<Name>`、`.vargs.<Name>`、`.kwargs.<Name>` 采用第 7 节规定的名称；
- symbol 字符串存放在 `Resources` 中；
- 实际 hidden argument 值按方法规范签名传入。

proxy 模板体内 `invoke fn(..inner)` 的包透传（§15.4）与本节同源：可变泛型包以 `.generic.<Pack>` 前置操作数整体转发，值包以 `.vargs.<Name>` / `.kwargs.<Name>` 随显式实参转发；二者均由 Middleware 在烘焙下一环时消费（解包、shim、特化链接），frontend 不展开包元素。

---

## 16. 结构化控制流

### 16.1 block 调用

```bil
call blk(BLOCK_ID)
```

语义：

1. 执行目标 block；
2. 目标 block 正常到达末尾后返回；
3. 从 `call` 的下一条指令继续。

`call` 不创建函数调用栈帧，不涉及调用 ABI。目标 block 可以通过 `ret` 返回整个函数、通过 `throw` 传播异常，或执行合法的结构化退出。

### 16.2 条件

```bil
if CONDITION blk(TRUE_BLOCK) blk(FALSE_BLOCK)
```

规则：

- CONDITION 必须为 `.bool`；
- 只执行一个分支 block；
- 分支 block 正常结束后，从 `if` 的下一条指令继续；
- `FALSE_BLOCK` 可以写 `none`，表示条件为 false 时无操作。

### 16.3 正向循环

```bil
loop CONDITION blk(BODY_BLOCK) blk(ENUMERATOR_BLOCK) blk(JUDGE_BLOCK) BREAK_ID_VAR
```

执行顺序：

1. 绑定唯一 BREAK_ID 到 BREAK_ID_VAR；
2. 执行 JUDGE_BLOCK；
3. 读取 CONDITION；
4. false：结束循环；
5. true：执行 BODY_BLOCK；
6. 若正常完成，执行 ENUMERATOR_BLOCK；
7. 回到步骤 2。

`ENUMERATOR_BLOCK` 可以为 `none`，适用于普通 while。

CONDITION 必须为 `.bool`。JUDGE_BLOCK 必须在每次读取前保证 CONDITION 已赋值。

### 16.4 反向循环

```bil
loop.rev CONDITION blk(BODY_BLOCK) blk(ENUMERATOR_BLOCK) blk(JUDGE_BLOCK) BREAK_ID_VAR
```

执行顺序：

1. 绑定唯一 BREAK_ID；
2. 执行 BODY_BLOCK；
3. 若正常完成，执行 ENUMERATOR_BLOCK；
4. 执行 JUDGE_BLOCK；
5. 读取 CONDITION；
6. true：回到步骤 2；
7. false：结束循环。

用于 do-while 及其他先执行 body 的循环。`ENUMERATOR_BLOCK` 可以为 `none`。

### 16.5 break 与 continue

```bil
break BREAK_ID_VAR
continue BREAK_ID_VAR
```

规则：

- `break` 可以引用 loop、loop.rev 或 switch 创建的 BREAK_ID；
- `continue` 只能引用 loop/loop.rev 创建的 BREAK_ID；
- token 必须在当前动态结构作用域内有效；
- BREAK_ID 不得跨函数、存入字段/数组、传给普通方法或从资源加载；
- `continue` 正向循环跳到 ENUMERATOR_BLOCK，然后 JUDGE_BLOCK；
- `continue` 反向循环跳到 ENUMERATOR_BLOCK，然后 JUDGE_BLOCK。

### 16.6 switch

```bil
switch SELECTOR res(TABLE_RESOURCE)
    [blk(ITEM_0), blk(ITEM_1), ...]
    blk(DEFAULT_BLOCK)
    BREAK_ID_VAR
```

TABLE_RESOURCE 是与 SELECTOR 类型一致的不可变常量数组，元素数必须等于 item block 数。

语义：

- 按表中顺序使用 `cmp.eq` 语义匹配；
- 第一个匹配项对应 ITEM block；
- 无匹配时执行 DEFAULT_BLOCK；
- DEFAULT_BLOCK 可以为 `none`；
- item/default block 正常结束后继续 switch 后的下一条指令；
- `break BREAK_ID_VAR` 提前退出该 switch。

`SYNTAX.md` 中包含 `_` 的 pattern switch 分支不能直接存入常量表；frontend 必须把它们降为 `if` 或多个结构化判断。

### 16.7 try/catch/finally

标准形式：

```bil
try blk(TRY_BLOCK)
    EXCEPTION_VAR
    res(CATCH_TABLE)
    blk(FINALLY_BLOCK)
```

其中：

- EXCEPTION_VAR 必须是可容纳异常或 null 的类型；
- CATCH_TABLE 是按源码顺序排列的 `{ exception-type → block }` 表；
- FINALLY_BLOCK 可以为 `none`。

语义：

1. 执行 TRY_BLOCK；
2. 正常完成时，将 EXCEPTION_VAR 置为 null；
3. 抛出异常时，按顺序选择第一个兼容 catch 类型；
4. 命中时把异常写入 EXCEPTION_VAR 并执行 catch block；
5. catch 正常完成后，当前逃逸异常变为 null；
6. 未命中或 catch 再次抛出时，EXCEPTION_VAR 保存当前逃逸异常；
7. 执行 FINALLY_BLOCK；
8. finally 正常完成后，有逃逸异常则继续抛出，否则继续 try 后下一条指令；
9. finally 自身的 abrupt completion 覆盖此前待继续的 completion。

finally block 可读取 EXCEPTION_VAR，从而实现 `finally(e)` 中“无异常时为 null”的语义。

### 16.8 return

```bil
ret
ret VALUE
```

规则：

- `.return = .void` 的函数使用 `ret`；
- 非 void 函数使用 `ret VALUE`；
- VALUE 类型必须严格等于 `.return`；
- `ret` 退出整个函数，不只是当前 block；
- frontend 必须保证所有函数路径具有符合 `SYNTAX.md` 的显式返回。

### 16.9 throw

```bil
throw EXCEPTION
```

EXCEPTION 必须是 Rigi 异常根类型的兼容值。兼容性若需要视图转换，frontend 必须先生成 `cast`。

---

## 17. 协程指令

### 17.1 await

```bil
await TASK
await TASK RESULT
```

规则：

- `await TASK` 用于无结果 `core.coroutine.Task`；
- `await TASK RESULT` 用于 `Task\<TResult>`；
- RESULT 类型必须严格为 TResult；
- Task 成功时取得结果；
- Task 失败时在 await 点重新抛出保存异常；
- Task 取消时传播取消；
- Task 未完成时挂起当前 Coroutine，并在其原 Executor 恢复。
- Task 已终止时不要求实际挂起；未终止时必须先保存 continuation，再原子登记 waiter
  并转为 `Suspended`，避免终态与登记竞态丢失唤醒。
- waiter 终态恢复时重新发布到 waiter 自己永久绑定的 Executor，而不是 Task 所属 Coroutine
  的 Executor。

BIL 不规定 continuation frame 和 Worker 调度的物理实现。continuation frame、state 编号、
waiter 数据结构和 Native ABI 仍不属于 BIL；Middleware 可以将本指令降为状态机，但不得
改变上述可观察语义。

### 17.2 yield

```bil
yield
yield ALARM
```

规则：

- 裸 `yield` 结束当前 run-to-suspension 执行段并重新参与调度；
- `yield ALARM` 接受 `PollingAlarm` 或 `EventAlarm` 兼容值；
- 即使 Alarm 已就绪，带 Alarm 的 yield 仍结束当前执行段；
- 普通函数与普通 lambda 也可以包含 `await` / `yield`；
- `async` 只决定调用时是否创建新 Coroutine。

### 17.3 using 与可挂起清理

BIL 不定义单独的 `using` 指令。

frontend 必须把 `seq using(...)` 生成为结构化初始化、清理记录和 `try/finally` 路径，使：

- 初始化按源码顺序；
- 清理按逆序；
- return、异常、break 等离开路径均经过清理；
- await/yield 只挂起，不触发提前清理；
- `dispose()` 自身可 await/yield；
- 外层 completion 必须等待清理全部完成。
- 清理记录、当前 `dispose()` 调用和逆序清理游标属于 continuation 的活跃状态；finally
  中的挂起恢复后必须回到同一清理进度。BIL 不增加 `using` 或 cleanup opcode。

---

## 18. 提示指令

```bil
hint res(RESOURCE_ID)
```

`hint` 向 backend 提供一段可忽略的提示；RESOURCE_ID 的资源内容是一段 JSON 文本。

规则：

- `hint` 只能出现在 block 内；
- RESOURCE_ID 必须引用本模块已声明的 `string` 资源；
- 本标准不定义 JSON 内容的 schema，由生产方（frontend）与消费方（backend）另行约定；
- `hint` 无结果变量，不读写任何变量，不参与 definite assignment，不是终结指令，不影响控制流与异常传播；
- `hint` 仅是指令流中的位置标记，不附着于任何特定指令、block 或符号，位置含义由消费方按 JSON 内容自行解释。

**从模块中删除全部 `hint` 指令后，程序的 §22.2 可观察行为必须完全不变。**

VM 执行 `hint` 为 no-op。

Middleware 可以依据 `hint` 内容改进代码生成或产出附加元数据（如调试信息），也可以整体忽略；`hint` 内容不得影响可观察语义。`hint` 内容无法解析或不符合消费方预期时，消费方必须忽略该条 `hint`，不得因此拒绝编译。

---

## 19. Resources 文本格式

### 19.1 标量资源

规范形式：

```bil
Resources {
    R_Message = string "hello, world",
    R_Enabled = bool true,
    R_Count = i64 123,
    R_Ratio = f64 0.5,
    R_NullUser = null type(com.example::User)
}
```

整数与浮点资源必须显式写类型，避免解析器依赖源码默认字面量规则。

`null type(T)` 标注**元素类型** `T`，资源本身的类型为对应的 `.nullable<T>`——因此它可以直接与 `.nullable<T>` 变量做 `cmp.eq` / `cmp.ne` 比较而满足 §11.5 的类型严格相同规则，这就是 §3.4 所称「nullable 检查」的标准形态。

判别值资源（§8.5）：enum case 的 `discriminant res(R)` 使用非负整数标量资源（如 `R_Disc_0 = i32 0`）；其静态类型必须是非负整数标量，判别值与宽度检查由 frontend/verifier 按 `RUNTIME.md` §16.4/§16.1 执行。

### 19.2 数组、Pair 与 Map

```bil
R_Names = array<string> { "a", "b" }
R_Entry = pair<string, i64> { "count", 2 }
R_Map = map<string, i64> {
    "a" = 1,
    "b" = 2
}
```

复合资源的元素类型必须严格一致。

### 19.3 原始数据

```bil
R_DataHex = raw.hex x2FF2331C
R_DataBin = raw.bin b01010101
```

原始数据只表示不可变 byte sequence，不自动视为 typeid、fieldid 或 Native 地址。

### 19.4 switch table

```bil
R_Switch = switch-table<.i32> { 1, 2, 3 }
```

元素必须为编译期常量，且类型与 selector 严格相同。

### 19.5 catch table

```bil
R_Catches = catch-table {
    type(core::IOException) -> blk(catchIo),
    type(core::RuntimeException) -> blk(catchRuntime)
}
```

catch 顺序具有语义，不能重排。

---

## 20. 完整文本示例

```bil
BIL "1.1"

Metadata {
    module = string "com.example.app"
}

Resources {
    R_Hello = string "hello, world",
    R_Zero = i32 0
}

LocalSymbols {
    .type com.example::App = class pub {
        .static-method com.example::App$.static.main(args:.array<.string>)@.i32 pub entrypoint
    }
}

ExternalSymbols {
    .type core::Console = class pub {
        .static-method core::Console$.static.println(value:.string)@.void pub
    }
}

fn(com.example::App$.static.main(args:.array<.string>)@.i32) {
    .args {
        .return = .i32,
        args = .array<.string>
    }

    .vars {
        .string message,
        .i32 result
    }

    .block entry entrypoint {
        load res(R_Hello) $message
        invoke.noret fn(core::Console$.static.println(value:.string)@.void) [$message]
        load res(R_Zero) $result
        ret $result
    }
}
```

wrapper 应用标记与 proxy 模板示例：

```bil
LocalSymbols {
    .type com.example::Service = class pub wrapped(core.logging::Logged) {
        .field com.example::Service#name@.string pub
    }

    .type core.logging::Logged = wrapper generic(TTarget) rich {
        .method core.logging::Logged$.proxy.get.name(value:.string)@.string
            pub wrapper-proxy(specific)
    }
}
```

---

## 21. BIL 验证器

验证器必须拒绝任何违反本节规则的 BIL。验证可以分阶段进行，但最终结果必须等价。

### 21.1 词法与语法验证

检查：

- 版本号受支持；
- 本地标识符字符合法，canonical symbol 符合第 5.2 节语法；
- 段结构合法；
- opcode 与操作数数量合法；
- 括号、数组和 block 结构闭合；
- 保留名称未被用户声明。

### 21.2 符号验证

检查：

- 所有 canonical 类型/字段/方法/case 符号以及 RESOURCE/BLOCK 引用可解析；
- canonical 限定正确；
- local symbol 不重复；
- external symbol 签名完整；
- 方法 body 与声明一一对应（`native` 声明除外：`native` 方法不得存在方法 body，且必须恰好各带一个 `symbol("...")` 与 `lib("...")` 修饰符）；
- `core::Any$call???` 为预定义内建方法符号（§15.5 / §22.5）——无 LocalSymbols/ExternalSymbols 声明、无 fn 定义，可被 `invoke` 引用；
- proxy 模板 fn（名以 `.proxy.` 开头，§5.1）必须声明在 wrapper 类型内，且与 `wrapper-proxy` 修饰符双向一致（见 §21.8）；
- `..init.wrapper`（§9.7）：每 owner 至多一个；返回 `.void`；实例方法；必须 `priv` + `compiler-generated`；
- `..companion.*` 类型（§8.7）：必须是 `class` 且带 `singleton` + `shared`；类型名保留前缀；
- entrypoint 唯一且签名符合 `SYNTAX.md`。

### 21.3 类型验证

检查：

- 所有变量和资源有类型；
- 标准类型构造（`.array` / `.map` / `.pair` / `.nullable` / `.cell` / `.readonly_cell` / `.typeid` / `.fieldid` / `.generic`）实参可解析且元数合法；
- 指令源/目标类型严格满足规则；
- 不存在隐式数值提升或子类型赋值；
- 运算实现按精确类型唯一；
- getter/setter/index 实现按精确类型唯一；
- direct invoke 签名完全匹配；`invoke.indirect` / `invoke.indirect.noret` 按 §15.3：`OBJECT_VAR` 静态类型恰有一个与实参/返回形态严格匹配的 `$$call`（含 async 时结果为 `Task\<TResult>` / `Task`；泛型 `$$call` 的 typeid/包前缀与 §15.1 同构校验）；
- cast 目标合法；
- new/init 和 enum case 签名合法；
- await/yield 类型合法；
- `get.self` / `invoke fn(..inner)` / `invoke.noret fn(..inner)` 仅出现在 proxy 模板 fn 内，且类型规则见 §12.5 / §15.4；
- `get.wrapper` / `get.wrapper.field` 类型规则见 §12.4（后者要求 HOST_FIELD 带 `wrapped(W)`、OBJECT 可赋值到字段 owner、RESULT = W）；
- `set.wrapper.field` 链元素合法（`field` / `wrapper` 两态；字段应用对 `field(HOST_FIELD)+wrapper(W)` 要求 HOST_FIELD 带 `wrapped(W)`；类型应用 `wrapper(W)` 要求当前位置类型声明带对应应用标记 §8.3.1；链必须含 wrapper 元素）；
- `new.wrapper.field` / `new.wrapper.method` / `new.wrapper.entity`（§14.5）仅允许出现在 `..init.wrapper` fn 体内；WRAPPER_TYPE 与 ARGS 匹配 wrapper init；field/method/entity 目标与 `wrapped(W)` 标记一致；
- `new.wrapped` / `new.wrapped.case`（§14.4）：第一实参表匹配目标类型 `..init.wrapper` 签名且该签名非空；第二实参表匹配 init / case；与普通 `new` / `new.case` 互斥（有参 `..init.wrapper` 必须用 wrapped 家族，否则禁止）。

### 21.4 definite assignment

检查：

- 参数入口已赋值；
- 普通局部变量在读取前已赋值；
- 所有结构化路径合并时满足读取条件；
- loop condition 在每次读取前由 judge block 赋值；
- 结果变量不会在失败路径上被错误认为已赋值。

### 21.5 控制流验证

检查：

- 仅允许结构化 block 引用；
- 不存在 `jmp`；
- block 均在当前函数；
- entry block 不正常落到末尾；
- `ret` 类型正确；
- break/continue token 来源与作用域正确；
- continue 不引用 switch token；
- catch/finally table 合法；
- 递归 block call 若被允许，必须能够由实现安全执行；实现可以选择拒绝无法证明有界的直接结构递归。

### 21.6 `.breakid` capability 验证

验证器必须追踪 BREAK_ID 的创建结构与作用域。

`.breakid`：

- 只能由 loop/loop.rev/switch 绑定；
- 每次绑定产生唯一 token；
- 对应变量不得被二次普通赋值；
- 不得复制；
- 不得比较；
- 不得作为方法参数/返回值；
- 不得存入字段、数组、Any、Box 或资源。

### 21.7 泛型与参数包验证

检查：

- hidden argument 名称符合第 7 节；
- 顺序符合规范；
- fixed generic 参数数目正确；
- positional/named generic 包类型正确；
- vargs/kwargs 包类型正确；
- `.generic<...>` 引用的 typeid 位置可见且已赋值；
- 泛型约束在 frontend 输出中已满足；
- runtime 动态 new/is/supers/with 的边界合法。

### 21.8 可见性与类型属性验证

检查：

- `pub`、`protected`、`internal`、`priv` 访问合法；
- static/instance 指令形式正确；
- const 不被写入（init 方法体内写实例 const 字段除外——构造期一次性赋值，
  对齐 SYNTAX §9.3；静态字段写入不豁免）；
- abstract 不被构造；
- enum struct 不走普通 new；
- rich/shared 闭包与跨 Coroutine 规则合法（`.cell<T>` / `.readonly_cell<T>` 共享安全 passthrough：等同于 `T`，与 Box 同例，不按普通 class 闭包表）；
- async 调用的 receiver/参数/结果满足 shared 边界；
- `wrapper-proxy(PROXY_KIND)`（§8.4）只允许在 wrapper 类型内、名以 `.proxy.` 开头的方法上；此类方法必须带本修饰符；`PROXY_KIND` 仅 `specific` / `wildcard`，且与成员形状类别一致（specific ↔ 具名 proxy；wildcard ↔ `.*` 通配 proxy）；同一方法不得重复携带本修饰符；
- `wrapped(WRAPPER_TYPE_REF)`（§8.3.1）的 `WRAPPER_TYPE_REF` 必须是 wrapper 类型；可重复，顺序保留；
- `..init.wrapper` 声明与 fn 定义满足 §9.7（唯一性 / void / priv + compiler-generated / 非 static）；
- `..companion.*` 满足 §8.7（class + singleton + shared；壳体静态方法体形态由 frontend 保证，验证器检查 companion 类型结构）。
- enum struct 类型的实例字段：宿主类型的每个 init 必须在全部执行路径上对该字段发 `set.field`（先于任何读路径），否则拒绝模块（§14.3「enum 无零值」）。

### 21.9 VM 可执行性验证

BIL VM 必须能够在不依赖 LLVM、Native ABI 和对象物理布局的情况下解释所有标准指令。

如果某个 BIL 扩展只能由特定 backend 执行而 VM 无法给出语义参考实现，则该扩展不得标记为标准 BIL 指令。

---

## 22. BIL VM 语义要求

### 22.1 抽象值模型

VM 可以使用 C# 对象、record、数组、字典或其他抽象数据结构表示 Rigi 值。

VM 不需要模拟：

- 128-bit 胖引用的位布局；
- 16 字节对齐；
- TypeSheet 内存结构；
- vtable/iMap offset；
- Box 裸数据块；
- ARC/GC 引用计数；
- LLVM calling convention。

### 22.2 必须一致的可观察行为

VM 与 Native 实现必须在以下方面一致：

- 返回值；
- 抛出的语言异常；
- 字段、数组和变量的可观察读写；
- getter/setter/operator/wrapper 的调用顺序；
- short-circuit 行为；
- try/catch/finally completion；
- async eager spawn；
- await/yield 的逻辑状态变化；
- enum case 身份与 payload；
- typeOf/is/supers/with/cast 的结果；
- using 清理顺序。

### 22.3 运算实现查询

VM 必须使用与 Middleware 相同的确定键查询运算实现：

```text
opcode + exact operand type(s) + exact result type
```

对内建类型执行语言规定的 primitive 语义；对用户类型执行精确运算实现。VM 不执行 source-level overload ranking。

### 22.4 字段与索引

VM 必须把 `get.field`、`set.field`、`get.array`、`set.array` 视为独立语义操作，并根据精确类型与符号元数据执行 getter/setter/operator/wrapper 行为。

不得为了实现方便而在 BIL 语义层把它们改写成与规范不同的普通调用顺序。

### 22.5 native 函数的内建 hook

VM 执行到对 `native` 方法声明的 `invoke` / `invoke.noret` 时，不寻找方法 body，而是按 `(lib, symbol)` 查询内建 hook 表并执行对应的内建行为。标准内建 hook 表：

| lib / 键 | symbol | 参数 | 行为 |
|---|---|---|---|
| `rigi_rt` | `print` | `text: .string` | 将字符串写入标准输出 |
| `rigi_rt` | `printErr` | `text: .string` | 将字符串写入标准错误 |
| `rigi_rt` | `alloc_array` | 泛型 hidden `.typeid`（经 `.generic.T` 物化）+ `size: .i32` | 分配并返回元素零值初始化的 `.array<T>`；T 为 enum struct 按宿主错误（§14.3 无零值）。仅供 stdlib `arrayOf`/`arrayOfElements` 系列的私有 native 声明调用，用户代码不可直达 |
| `rigi_rt` | `make_sleep_alarm` | `milliseconds: .i64` | 创建并返回 `core.coroutine::EventAlarm`：基于单调时钟、到期转 ready 的粘滞事件 Alarm（`RUNTIME.md` §19.3/§19.4），配合 §17 `yield ALARM` 实现非阻塞睡眠。仅供 stdlib `sleep` 的私有 native 声明调用，用户代码不可直达 |
| `rigi_rt` | `toString` | `value: .any` | 返回值的字符串表示（`SYNTAX.md` §3.8）：内建数值/`bool`/`char` 为标准文本；未覆写 `toString` 的对象为其类型 canonical 名 |
| （方法 hook） | `core::Any$call???` | 见 §15.5 胖值签名 | 按 `symbol` 路由 wrapper 请求；无路由命中抛 `core::NoSuchMethodException` |

`String` 的 `toString` 即值自身，不产生 native 调用；覆写了 `toString` 的类型经虚派发执行自身实现，不命中本表。`call???` 按方法符号命中本表（无 `(lib, symbol)` 对），无 BIL fn 定义。命中表之外的 `(lib, symbol)` 组合 VM 无法解释，必须拒绝执行并报错。该表只随 BIL 标准修订扩充；Middleware 的原生链接不受此表约束。

---

## 23. Middleware 合法 lowering 的边界

Middleware 可以：

- 把 primitive `add` lower 为 LLVM `add`/`fadd`；
- 把用户 `add` lower 为精确 operator body 调用；
- 把 `get.field` lower 为 offset load 或 getter 虚调用；
- 把 `set.field` lower 为 store、setter 和 ARC/GC 屏障；
- 把 `get.array` lower 为 Array 胖槽、Span stride 或用户索引运算；
- 把 `invoke` 的 BIL 参数签名转换为任意合法 Native ABI；
- 把 await/yield lower 为 coroutine state machine；
- 把 try lower 为目标异常机制；
- 消除不改变可观察语义的 copy、Box 和临时变量；
- 进行 inlining、devirtualization、constant folding 和 dead-code elimination。

Middleware 不得：

- 接受类型非法的 BIL 并猜测隐式转换；
- 重新进行 source-level overload ranking；
- 改变 getter/setter/operator/wrapper 的可观察顺序；
- 把内建 bool 短路表达式错误变为两侧都求值；
- 把 async 调用变为惰性启动；
- 绕过 using 清理；
- 允许 enum struct 通过普通 new 构造；
- 根据 Native 表示改变 BIL 类型语义。

---

## 24. 与 SYNTAX.md / RUNTIME.md 的职责关系

### 24.1 以 SYNTAX.md 为准的内容

- 源码语法；
- 普通方法调用的 source-level overload resolution；
- 运算符名称和语言含义；
- getter/setter、wrapper、extension、async、using 的源码规则；
- 类型、字段、方法、运算符、getter/setter 的 canonical symbol 命名；
- wrapper 存储命名约定（`.wrapper.<wrapper 类型全称>`，Middleware ABI，§5.3）；
- 泛型参数命名；
- 可变参数声明形态；
- 入口函数允许的源码签名；
- 访问修饰符和声明合法性。

### 24.2 以 RUNTIME.md 为准的内容

- reified generic 的 runtime typeid 语义；
- fixed/positional/named generic type 信息容器；
- Box、Span、胖引用和 TypeSheet；
- is/supers/with/cast 的运行时含义；
- wrapper 路由与 canonical symbol；
- async、Task、Coroutine、Executor、await、yield；
- rich/shared 与 GC；
- IDisposable 和销毁检查。

### 24.3 本文档独立规定的内容

- BIL 文本结构；
- 严格类型规则；
- BIL hidden argument 的规范签名；
- BIL opcode 与操作数形式；
- block 的结构化执行模型；
- BIL verifier；
- BIL VM 必须实现的抽象语义；
- BIL 与 Middleware 的边界。

若三份文档出现表述差异：

1. 源语言合法性以 `SYNTAX.md` 为准；
2. 运行时可观察行为以 `RUNTIME.md` 为准；
3. frontend 与 Middleware 之间的 IR 编码与验证以本文档为准；
4. 本文档不得重新定义与前两份文档冲突的语言或运行时语义。

---

## 25. Legacy BIL 迁移说明

旧 BIL 文本可按下表迁移：

| Legacy | 标准形式 |
|---|---|
| `.load` | `load` |
| `.invoke` | `invoke` |
| `Type_X` / `F_X` / `M_X` 及 `Type_X::M_X` | 第 5.2 节 canonical symbol；标准 BIL 不保留独立符号别名 |
| `movl` | `shift.left` |
| `movr` | `shift.right` |
| `cmp.bg` | `cmp.gt` |
| `cmp.beq` | `cmp.ge` |
| `getid.type TYPE` | `getid.type type(TYPE) TARGET` |
| `getid.var VAR` | `getid.var VAR TARGET` |
| `getid.field FIELD` | `getid.field field(FIELD) TARGET` |
| `switch TABLE ...` | `switch SELECTOR TABLE ...` |
| 旧 `try` 四 block 形式 | 第 16.7 节 catch-table 形式 |
| `exported` | `pub` |
| `private` | `priv` |
| `atomic[$lock]` block | 删除；非标准 |

兼容解析器可以读取 legacy spelling，但应当在内部规范化，并在重新输出时使用本标准形式。

---

## 26. 未来绝对不允许加入的扩展

- SSA 形式； （过于底层，这是Middleware的职责）
- 任意 CFG 分支； （过于底层，完全和BIL的设计理念背道而驰）
- 原子内存序指令； （应使用现有的block + volatile block修饰符，更底层的语义不允许在这个级别介入，这是Middleware的事情）
- SIMD/vector 类型； （过于底层，完全和BIL的设计理念背道而驰，而且应该使用标准库类型+native函数）
- unsafe pointer；（应使用标准库类型+native函数）
- generator/yield-value； （现有的指令已经足够）
- backend-specific intrinsic； （过于底层，完全和BIL的设计理念背道而驰，即使有极少数情况，也应使用统一的 hint 指令（§18））
- 调试器专用 scope/lifetime 指令； （应使用统一的 hint 指令（§18））
- profile-guided 元数据。 （应使用统一的 hint 指令（§18））

---

## 27. 未来可能加入的扩展

以下能力如果未来加入，必须通过 BIL 版本或 feature flag 明确声明，不得静默改变现有指令含义：

- tail call 语义；

扩展必须同时定义：

- 文本语法；
- 类型规则；
- verifier 规则；
- BIL VM 参考语义；
- 不属于 BIL 的 backend lowering 边界。
