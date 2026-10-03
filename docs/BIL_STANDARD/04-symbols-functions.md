# 符号模型 / 函数、参数、局部变量与 block（§8–§9）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

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

`generic(...)` 子句：泛型参数按源码声明序列出，型变参数保留 `out` 或
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
rich shared unsafe
wrapped(WRAPPER_TYPE_REF)
```

`wrapped(WRAPPER_TYPE_REF)` 可重复出现，声明序 = outer→inner 应用序；语义见 §8.3.1。

修饰符合法性必须与 `SYNTAX.md` 一致。例如：

- `rich` 仅适用于 struct/enum struct 和 wrapper；
- `wrapper` 默认非 rich，仅显式带 `rich` 时才能持有普通对象字段或 rich 值；`get.self` 读取独立宿主参数，不属于普通字段，也不参与 rich 闭包（`SYNTAX.md` §14.9）；
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

BIL 的 `get.field` / `set.field` 在使用点始终引用逻辑字段 canonical symbol，不引用 Native offset。setter 体内对 backing 的一切读写（进入时隐含 `backing = value`、自动 setter 体、体内对 `value` 的多次读/写）统一引用保留字段符号 `..value`——实例 `Hero#..value@.i32`、静态 `Config#.static...value@.i32`（`.static.` 标记保留，名字段为 `..value`）、全局 `app::#..value@.i32`、cell 隐藏子类 `..cell..UUID#..value@.i32`。`..value` 是「当前 setter 所服务字段的 backing 存储」的约定别名（§5.1）；frontend 不为它发 `.field` 声明。getter 体保持引用原逻辑字段符号（如 `Hero#hp@.i32`）——只读性由此保证。

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
- 局部变量上的 wrapper 应用标记由 cell 隐藏子类的 `value` 字段 `wrapped(W)` 承载（`.vars` 无新语法；见 §9.3 注记与 `SYNTAX.md` §14.3 统一 cell 存储）；静态字段的 BIL 声明类型投影为 cell 子类、全局字段的 cell 子类即 singleton（不再发字段槽声明），静态/全局均不在字段槽投 `wrapped(W)`（避免双份隐藏存储；wrapper 标记挂在子类 `value` 字段上）。cell 子类上 `@W(args)` 的实参成为该 cell 类型 `..init.wrapper` 的参数。

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
async entrypoint unsafe
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

`entrypoint` 标记程序入口 fn（SYNTAX §17：源码侧为 `@EntryPoint` 内建注解或全局命名空间裸 `main` 约定）。同一模块允许**多个**方法带 `entrypoint`（多文件合并后尤其如此）；运行前选择规则：恰一个时自动选中，多个时必须经 `vm --entry-point <符号>` 显式指定（符号必须命中带 `entrypoint` 修饰的成员），零个即运行前错误。

`wrapper-proxy(PROXY_KIND)` 标记 wrapper 类型声明内的 `.proxy.*` 成员 fn（**proxy 模板**：模板态绑定产物，体内可出现 `invoke fn(..inner)` / `get.self`）。`PROXY_KIND` 取两值之一：

- `specific`：specific proxy 模板（命中成员名的特定代理）；
- `wildcard`：wildcard proxy 模板（类别唯一通配代理）。

`.proxy.` 前缀成员名（§5.1）与本修饰符双向一致：带 `.proxy.` 名的方法必须带本修饰符，带本修饰符的方法名必须以 `.proxy.` 开头；`kind` 与成员形状类别一致（specific ↔ 具名 `.proxy.<成员>` / `.proxy.get.<名>` 等；wildcard ↔ `.proxy.*` / `.proxy.get.*` / `.proxy.set.*` / `.proxy.opr.*`）。同一方法不得重复携带本修饰符。

烘焙（特化合成、inner 链接、原始体替换、存储合成）整体归 Middleware；frontend 只发射 proxy 模板与应用标记，不在 BIL 文本中合成特化/原始体/路由 fn。

`call???` 是 `core::Any` 的 native 内建方法（§22.5 hook），frontend 不为其产 fn 定义（内建无 body 先例，同 `native`）；降级调用点见 §15.5。

运算符、getter、setter 和 enum case 的实现可以拥有 method body，但其调用点在 BIL 中仍使用对应的语义指令；只有普通显式方法调用或规范要求的动态 fallback 使用 `invoke`。`getter(FIELD_SYMBOL)` / `setter(FIELD_SYMBOL)` 把该方法标为指定逻辑字段的访问器：setter 体内 backing 读写引用保留字段 `..value`（§8.3 / §13.3）；getter 体仍引用 `FIELD_SYMBOL` 本身。

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

声明的参数表是 case 的**洞签名**（参数洞的调用方契约——参数化 case 的源码调用点按它传参）；固定 case 没有参数洞，参数表为空。case 模板中的固定实参不在声明中携带——`new.case`（§14.3）的实参是「固定实参 + 洞实参」按 init 参数序的组合，运行时按组合实参匹配 case 绑定的 init。

`enum-struct` 的普通 `init` 不得作为 `new` 目标。所有 enum 值必须通过 `new.case` 创建。

判别值：`discriminant` 的资源必须是整数标量资源
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

### 8.7 companion singleton（静态问题统一收敛）

静态方法被 Method wrapper 修饰、或静态字段被 Value wrapper 修饰时，frontend 为**每个声明类**合成一个 companion singleton 类型——同一类的全部静态方法与全部静态字段共用一个 companion；该 singleton 是声明类的**嵌套类**（canonical 形态 `命名空间::外层...companion`，无 UUID），companion 自身 static 成员递归同理（每个声明类各有一个自己的 companion，嵌套在各自类里）：

```bil
.type <外层>...companion = class singleton shared pub compiler-generated {
    .field <外层>...companion#<静态字段名>@<cell 类型> pub var
    .method <外层>...companion$<原方法简单名>(...)@Ret pub compiler-generated
        [原方法上的 wrapped(W) 应用标记改挂到本实例方法]
    .method <外层>...companion$init()@.void pub init
}
```

约定：

- companion 是声明类的嵌套类，类型名段为保留名 `..companion`（§5.1 `..` 前缀保留，用户源码不可声明）；
- 类型必须是 `class`，且同时带 `singleton` 与 `shared`（§8.2 singleton 规则）；建议带 `compiler-generated`；
- **静态 Method wrapper**：companion 内生成**实例**方法——方法简单名**沿用原静态方法简单名**（不另加 `..wrapped.` 前缀——companion 类型已隔离命名空间）；该方法承接原静态方法体，并被原 Method wrapper 修饰（`wrapped(W)` 挂在 companion 方法声明上，若 BIL 方法侧不投 `wrapped` 则由 Middleware 按 companion 合成契约识别）；原 owner 上保留**同名静态壳体方法**——签名与源码静态方法一致，fn 体仅：取得 companion singleton 实例 → `invoke` / `invoke.noret` 其对应实例方法（实参转发）→ `ret`（有返回时）；
- **静态 Value wrapper**：字段的 cell 存储对象成为 companion 的实例字段（`#<名>@<cell 类型>`）；companion 的 `init` 里求值字段初始化表达式并构造 cell（走 `new.wrapped` / 普通 `new`，经 cell 自身 `..init.wrapper` 安装 wrapper）；源码对 `C.field` 的读写访问路径改写为「companion 单例实例 → 其实例字段（cell）→ getValue/setValue」，place 访问 `C.field:W.x` 的 `get.wrapper.field`/`set.wrapper.field` 以该 cell 字段为 HOST_FIELD。这些初始化表达式与 `..globals.init` 同样适用源码层禁令：不得直接引用其它全局/静态字段（`SYNTAX.md` §9.3）。

> **注记（调用时机）**：所有 singleton（含 companion 与全局 wrapped 字段的 cell 单例——见 §14.3）由 VM/Middleware 在 main 开始执行前**急切初始化**——构造 → init 跑完（companion 的 `init` 即完成 cell 构造与 wrapper 安装，Method wrapper 安装走 companion 自身 `..init.wrapper`；全局字段的初值表达式在 cell 单例的 `init` 里求值）。运行期 `new type(singleton)` 返回该单例唯一实例（不再重跑 init）；壳体静态方法不负责安装 wrapper。**初始化无序**：不得假设任何 singleton 的初始化顺序，任一 singleton 的 `init` 里访问另一 singleton（`new` 或经字段路径）按需递归触发其构造与初始化，且 init 副作用恰好一次；初始化循环（直接或间接触发自身构造）抛带循环链的异常。

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
- `.breakid` 只能由 `loop`、`loop.rev`、`switch`、`call`、`if` 或 `try` 绑定（§16.5：所有结构化 child-region 指令的 region-exit capability）；
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
- entrypoint block 不得正常落到末尾，必须显式 `ret` 或以异常/其他终止流程结束。终止判定按结构化指令递归：`ret`/`throw` 终止；`if` 要求双分支都在且全终止；`switch` 要求 item 与 default 全终止；`try` 要求 body 与全部 catch handler 终止；`loop` 可能零次执行，不算终止；`call blk` 仅当被调块自身终止且 region 无外向逃逸（region 内 `break`/`continue` 的目标 breakid 全为该 region 内部结构指令所建——命中被调块自身或更外层 breakid 的路径会落回 `call` 续点）才算终止。

### 9.5 block 引用限制

`call`、`if`、`loop`、`switch` 和 `try` 引用的 block：

- 必须存在；
- 必须位于当前函数；
- 不得引用其他函数的 block；
- 不得通过资源或整数伪造。

### 9.6 block 修饰符

`unsafe` 可修饰类型、方法和 block，不能修饰字段。方法的 `unsafe` 为函数体
提供危险操作上下文；block 的 `unsafe` 覆盖其中引用的子块。验证器按结构化
可达路径传播权限，同一子块若另从安全路径到达，仍须单独通过校验。
`invoke`/`invoke.noret` 调用 unsafe 方法或 unsafe 类型声明的成员，以及
`new`/`new.wrapped` 调用 unsafe 构造时必须具备该权限。`new.indirect` 的
typeid 静态界可确定具体类型时，按相同类型与精确 init 签名检查；
`invoke.indirect`/`invoke.indirect.noret` 可确定匹配的 `operator call` 声明时，
同样检查方法及其声明类型。未知泛型或缺失外部声明仍沿用既有动态协议，
不因此一律要求 unsafe。类型上的 unsafe 不会隐式修改各方法体的权限。

标准 block 修饰符为：

```text
entrypoint
volatile
unsafe
```

`volatile` 表示该 block 内可观察操作的源码顺序必须被保留，不得进行改变其 volatile 语义的重排。具体 LLVM volatile/atomic lowering 由 Middleware 决定。

`atomic[$lock]` 不是当前 Rigi 语法或 BIL 标准的一部分，不得出现在标准 BIL 中。

### 9.7 `..init.wrapper`（实体 wrapper 初始化方法）与 `..init.field.*`（字段初始化器方法）

编译器为「带有 wrapper 应用、需要在创建时安装 wrapper」或「继承闭包（含自身）存在带声明初始值的实例字段」的每个实体（class/struct/enum-struct/wrapper 类型本身，或 cell 隐藏子类等合成类型）至多生成**一个**实例方法，保留名：

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
- **闭包缝合（新 init 原则）**：类型级 `..init.wrapper` 的体内依次是 ① 继承闭包（基→本）全部 wrapper 安装（本类与基类的 Entity/Field/Method 应用；同 wrapper 定义的 Entity 应用按派生覆盖去重）② 闭包全部 `..init.field.<名>` 的调用（基→本、声明序；同名字段一族只调基类最早声明符号，虚派发选中最高派生实现）。**不生成对基类 `init` 的 super 调用**——基类字段初值与 wrapper 由本缝合覆盖，基类用户 init 体的链式调用仍由用户/合成 init 体内的 `super(...)` 决定（`SYNTAX.md` §9.3）；
- **调用时机（规范注记）**：本方法在实体 **init 之前**由 Middleware/VM **自动调用**，且只调用**实际类型**（分配类型）的 `..init.wrapper`（不沿继承重找、不重复调用基类的）；frontend **无法介入**调用时机，也不得在普通用户方法中显式 `invoke` 本方法（验证器可对非合成调用点给出诊断，Middleware 以自动调用为准）。有参时，调用方通过 §14.4 `new.wrapped` 把前缀实参传入构造路径，由运行时转交给本方法。

`..init.field.<名>`（字段初始化器方法，新 init 原则）：编译器为每个**带声明初始值的实例字段**（class/struct/enum-struct/wrapper 同规则）在其声明类型上合成一个保留名族方法——子类字段 `override`（`SYNTAX.md` §9.2.1 字段覆写）时子类生成同族同名方法（写同一基类槽），与基类版本构成 BIL 虚派发族：

```text
..init.field.<字段名>
```

规则：返回 `.void` 的**零参实例方法**；`priv` + `compiler-generated`；方法体为该字段的初始值赋值（`set.field`——带 setter 的字段经 setter 应用，§9.4；const 字段的写入豁免见 §21.8）；用户源码不可声明；普通用户方法不得显式 `invoke`（调用点恒为 owner 闭包内各 `..init.wrapper`）。**写入点地位**：`..init.wrapper` / `..init.field.*` 是 §21.8 承认的构造期字段写入点——带声明初始值的字段在实体任何 init 体执行前已完成写入（对 init 体而言「进入时已赋值」）。

**生成职责**：

- 类型声明带 `wrapped(W)`（Entity）、成员字段带 `wrapped(W)`（字段-Value）、实例方法带 Method wrapper，或继承闭包（含自身）存在带声明初始值的实例字段时，在 owner 类型上合成 `..init.wrapper`；体内按「闭包缝合」条款发 `new.wrapper.*`（基→本、outer→inner）并调用闭包全部 `..init.field.*`；
- 方法带 Method wrapper 时：静态方法走 §8.7 companion；实例方法在 owner 的 `..init.wrapper` 内发 `new.wrapper.method`（或按 Middleware 约定在方法首次绑定前安装——以 §14.5 指令语义为准，frontend 按应用表发射）；
- cell 子类（非 singleton）：`value` 字段上每个 `wrapped(W)` 的应用实参提升为 cell 类型 `..init.wrapper` 的参数；`new ..cell..UUID(...)` / `new.wrapped` 在变量初始化点传入这些实参。
- **cell 子类 init 元数约定**（编译器按「初值是否依赖外界」选择）：
  - **0 元 `init()`**：初值不依赖外界的场景——命名空间级全局 wrapped 字段的 cell 单例（字段初值表达式在 `init` 体内求值并写入 `value` 字段）；未初始化 `var` 的空 cell 构造点也用 0 元 `init()`（体为空块，源级 DA 保证读前已赋值）。
  - **1 元 `init(value)`**：初值依赖外界的场景——函数内 `const`/`var` 局部（有初始化器；被捕获的局部同此）、被捕获的参数（函数序言以实参值构造）、静态 wrapped 字段（有初始化器）；外界先算完初值表达式，经 `value` 实参传入。静态 wrapped 字段的「外界」是宿主 companion 的 `init`——companion `init` 求值初始化器后以 1 元 `init(value)` 构造 cell。
  - **const 风味（ReadonlyCell 子类）**：`value` 为 `const` 字段，写 `value` 靠 init 豁免（§21.8），无 `setValue`。
  - **wrapper 安装契约**：构造时 VM 自动缝合 `..init.wrapper`（在实体 `init` 之前——见调用时机）；全局 cell 单例（带 `wrapped(W)` 时）的 `..init.wrapper` 为无参、wrapper init 实参在全局作用域绑定（与局部 cell 有参 `..init.wrapper` 经 `new.wrapped` 传参的形态不同）。

---
