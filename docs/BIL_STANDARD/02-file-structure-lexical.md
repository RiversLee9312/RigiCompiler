# 文件与程序集结构 / 词法规则（§4–§5）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

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
