# Rigi 语义分析与 BIL 生成架构（中端）

> **状态**: 架构定稿
> **定位**: 本文档规定 AST → BIL 之间全部编译阶段（下称**中端**）的架构：
> 阶段划分、数据结构、数据流向、诊断模型、代码组织与验收方式。
>
> **文档分工**：源语言合法性以 `SYNTAX.md` 为准；运行时可观察行为以
> `RUNTIME.md` 为准；BIL 编码与验证规则以 `BIL_STANDARD.md` 为准。
> 本文档只规定中端**内部**如何组织，不重新定义上述三者的语义。


> `super(...)` 在 P3 绑定为独立 Bound/Lowered 调用标记，候选来自直接 BaseType
> 并复用 OverloadResolution；P4b 固定发 `invoke fn(..super)`，由 Middleware
> 解析为直接基类原始实现。frontend 不生成 `..create`，它仅属于 Middleware/VM
> 生命周期阶段。

---

## 1. 定位与总体管线

中端的输入是前端产物：经 `ASTIntegrityValidator` 验证的 `RootASTNode`
（每编译单元多个源文件，每文件一棵）。输出是符合 `BIL_STANDARD.md` 的
BIL 模块（内存对象模型 + 文本序列化）。

`BIL_STANDARD.md` §3.3「frontend 不变量」是中端的**需求清单**：名称解析、
访问控制、类型推断、重载解析、默认参数填充、具名参数重排、泛型约束检查、
smart cast、rich/shared 闭包检查、async 共享安全、extension 注册、
wrapper 形状校验与应用标记、语法糖规范化——全部必须在中端完成。
wrapper **烘焙**（派发链合成、inner 链接、原始体替换、隐藏存储/router
体）归 Middleware（BIL §23 边界；见 §5.2），不在中端完成。

总体管线为四个 pass、两棵树、一个符号图：

```text
RootASTNode ×N（语法树，只读）
    ↓ P1 声明收集（DeclarationCollector）
    ↓ P2 声明解析（DeclarationResolver）
符号对象图（驻留的 SemanticSymbol 图，全局唯一）
    ↓ P3 函数体分析（Binder）
BoundTree（带类型语义树；BoundNode.Syntax → ASTNode）
    ↓ P4a 降级重写（Lowerer，树到树，可多个 rewriter）
LoweredTree（脱糖树；LoweredNode.Origin → BoundNode）
    ↓ P4b 发射（BilEmitter，线性化）
BilModule（BIL 内存对象模型；指令.Origin → LoweredNode?，可空）
    ↓ BilWriter
BIL 文本
```

核心决策（修改须重新过一遍取舍）：

1. **P3 与 P4 严格分离**。P3 产出完整的带类型语义结果，P4 只做机械翻译。
   每一步可独立验收，防止分析与发射互相渗透形成不可测试的整体。
2. **Roslyn 风格独立 Bound Tree，且 P3/P4 各一棵**。AST 保持只读
   （AST 无 annotations 挂点，这是刻意设计，不走回头路）；
   语义信息全部活在 BoundTree/LoweredTree 与符号图上。
3. **P4a 与 P4b 分离（LoweredTree 存在的理由）**：emit 之前要做的
   lowering 相当深——async/await 直接物化为 BIL §17 指令，由
   Middleware 降为状态机（见 §7），using 物化为清理记录与 try/finally 路径等；同时避免
   P4 内部实现与 BIL 对象模型（及未来 BIL verifier/VM）互相捆绑，
   保持关注点分离。
4. **符号是驻留对象图**，引用相等即身份相等；canonical symbol 字符串
   只是序列化投影（§4.4）。
5. **诊断可恢复且可累积**，覆盖 P1–P4 全部阶段（§8）。

---

## 2. Pass 职责分配

`BIL_STANDARD.md` §3.3 清单逐项归属如下。原则：**声明间的事实归 P1/P2，
函数体内的事实归 P3，形态变换归 P4**。

| 职责 | 归属 | 说明 |
|---|---|---|
| 全局符号表建立 | P1 | 只扫声明骨架，不进函数体 |
| 重复声明检查 | P1 | 同名类型/成员冲突 |
| 类型引用解析（TypeReference → TypeSymbol） | P2 | 含泛型实参递归解析 |
| 继承图 / implements 图 / 循环继承检查 | P2 | |
| 修饰符合法性（rich 仅 struct、shared struct 必 rich 等） | P2 | 对照 SYNTAX §3.1.1；含「非 rich struct 不得 open/abstract」「singleton 必 shared」「wrapper 不得显式写 rich」 |
| rich / shared 字段闭包检查 | P2 | SYNTAX §3.1.1 闭包表（含 wrapper 两行），递归应用 |
| rich / shared 单向传染检查 | P2 | 基类 rich/shared ⇒ 子类必须同标；反向靠继承字段闭包重校验兜底 |
| 全局/静态字段的共享安全闸门 | P2 | SYNTAX §3.1.1 闸门 1：全局变量/常量、静态字段及其访问器类型 |
| wrapper 目标矩阵检查 | P2 | SYNTAX §14.9：宿主可内嵌性 + shared 目标矩阵 A–D + interface 实现者传染 |
| 泛型约束检查（声明侧） | P2 | 约束自身良构 |
| 泛型型变声明位置检查 | P2 | `VarianceChecker`：类型泛型参数的读/写极性、嵌套 invariant 容器、getter/setter 与基类/interface 位置 |
| wrapper 适用性与应用登记 | P2 | `@WrapperTarget` 类别 × 目标声明；形状校验 + AppliedWrappers/WrapperApplication 登记（Freeze 前零合成符号；烘焙归 Middleware，见 §5.2） |
| wrapper 继承闭包检查 | P2 | 间接基类/interface、override 方法与 accessor 必须显式重复 wrapper 定义/实参/顺序 |
| extension 目标注册 | P2 | `ext` 成员挂到目标类型符号 |
| canonical symbol 定形 | P2 | 符号图建成即可打印（§4.4） |
| 名称解析（表达式内） | P3 | 作用域链：块 → 参数 → 成员 → 全局 → import |
| 类型推断（`var` / 字面量 / 表达式类型） | P3 | |
| 重载解析（source-level overload ranking） | P3 | 唯一一处做 ranking 的地方（BIL §3.3） |
| 运算 / getter / setter / 索引 / 构造的精确签名规范化 | P3 | |
| 默认参数填充、具名参数重排 | P3 | BoundCall 已是规范参数序 |
| 泛型约束检查（使用侧实参） | P3 | |
| 构造泛型类型型变赋值 | P3 | `SymbolLookup.IsAssignable` 按 `out`/`in` 递归比较实参，invariant 保持严格相等 |
| smart cast 分析 | P3 | 结果记录在 BoundTree，显式 cast 由 P4 物化 |
| 访问控制检查（使用点） | P3 | |
| definite assignment / 所有路径显式返回 | P3 | BIL §21.4 要求 frontend 保证 |
| async 边界共享安全检查 | P3 | SYNTAX §4.5 五项闸门：receiver / 参数 / TResult / 捕获 / 泛型实参 |
| 值块隐式取值 | P3 | 「块内恰好一条 ExpressionStatement」判定为取值形态 |
| 语法糖规范化（全部脱糖） | P4a | 清单见 §6.1 |
| 短路展开、smart cast / 子类型赋值的显式 `cast` 插入 | P4a | BIL §3.1/§6.5/§11.3 |
| async/await/yield 物化、using 物化 | P4a | 深度 lowering，见 §7 |
| 隐藏参数物化（`.generic.T` / `.vargs` / `.kwargs`） | P4a/P4b | 规范签名见 BIL §7 |
| 表达式线性化、临时变量物化、`.vars` 收集 | P4b | |
| Resources 提取（字面量 → `res(...)`） | P4b | BIL §4.2：指令不得内联字面量 |
| block 结构生成（if/loop/switch/try） | P4b | BIL §16 结构化控制流 |

P1 与 P2 分开的原因：Rigi 声明可以互相前向引用，必须先收齐全部名字
再解析类型引用。P2 结束后符号图**冻结**——P3/P4 只读它，不再写入
（局部变量符号除外，它们归属各自函数的分析结果）。

每个 pass 的失败策略：诊断累积、尽量继续（§8）；但存在 Error 级诊断时
**不进入下一个 pass 的发射性工作**——P4 只接受无错的 BoundTree。

---

## 3. 编译单元模型

- 一次 `compile` 调用处理一个**编译单元**（未来对应一个程序集 / 一个
  BIL 文件）：多个 `.rg` 源文件 + bootstrap 符号 + `core.rg` 声明。
- P1/P2 面向整个编译单元一次性执行（跨文件前向引用因此天然成立）；
  P3 以**函数体**（含字段/全局变量初始化器、enum case 判别值等表达式体）
  为独立分析单位，函数间诊断互不阻断。
- 编译单元内声明的符号进 BIL `LocalSymbols`；被引用但来自 bootstrap /
  core 的符号进 `ExternalSymbols`（BIL §4.3/§4.4/§8.6）。

---

## 4. 符号对象图（P1/P2 产物）

### 4.1 符号家族

语义符号基类为 `SemanticSymbol`，派生（按需增补，遵守简洁三问）：

```text
SemanticSymbol
├── NamespaceSymbol
├── TypeSymbol            // class/struct/enum-struct/interface/wrapper + 内建
├── GenericParameterSymbol
├── FieldSymbol           // 含全局变量/常量；backing/computed/ext 以属性区分
├── MethodSymbol          // 含 init、operator、getter/setter、全局函数、ext
├── EnumCaseSymbol
├── ParameterSymbol
└── LocalSymbol           // 函数体局部变量（P3 产生，挂在函数分析结果上）
```

**命名注意**：语法侧已有 `Symbol` / `SymbolElement` / `SymbolASTNode`
（`AST/SymbolNodes.cs`，表示源码路径），语义符号一律用 `SemanticSymbol`
家族名称，禁止混用。语义期对语法 `Symbol` 的原地规范化使用其
`DeepClone()`（该方法即为此预留）。

### 4.2 驻留（interning）

- 每个声明实体在整个编译单元中**恰有一个**符号实例；引用相等即身份相等。
  比较符号一律 `ReferenceEquals` / `==`，禁止按名字字符串比较身份。
- 构造泛型类型（如 `List\<i32>`）同样驻留：同一 `(泛型定义, 实参列表)`
  必得同一实例（经编译单元级 cache）。`T?` 即构造类型 `Nullable\<T>`，
  不设独立的 nullable 表示（SYNTAX §3.4）。
- 符号图允许构造期两阶段（P1 建壳、P2 填内容），P2 结束后不可变。

### 4.3 bootstrap 与 core.rg（混合策略）

类型层级根与基元类型无处用源码声明，采用**硬编码 bootstrap + core.rg
声明文件**的混合：

- **硬编码 bootstrap**：`Any`、`Object`、`ValueType`、`Enum`、`Wrapper`、
  SYNTAX §3.2 全部基本类型（`i8`–`u64`、`float`/`double`、`bool`、`char`、
  `String`、`Type\<T>`、`Span\<T>`）、`Nullable\<T>`、`Box\<T>`，以及
  编译器特权关系（SYNTAX §3.1.2：`Box\<T> <: Object` 为内建事实、
  Span 的特权 lowering 标记等）。这些由 `BootstrapSymbols` 在符号图
  初始化时直接构造。注意三条容易搞错的层级事实（2026-07-29 规范修订）：
  `String` 与 `Wrapper` 都在 `ValueType` 分支下（`String` 非 rich、
  `Wrapper` 恒 rich）；`Nullable\<T>` 的 shared 属性由 `T` 推导而不是
  查声明修饰符；`Wrapper` 是全部 wrapper 声明的隐式基类。
- **core.rg**：其余标准库表层（`core::Console`、`core.coroutine::Task`
  / `Executor` / Alarm 家族、`core::IDisposable`、异常类型、
  `core.ComparisonResult` 等）以 Rigi 声明文件形式随编译器载入，
  用自己的前端解析后走同一条 P1/P2 路径。这同时构成前端的常驻回归测试。
- 划分原则：**类型系统与编译器本身依赖的进 bootstrap；只有语义分析的
  "用户"才依赖的进 core.rg**。基元类型上的运算符集合属于 bootstrap
  的一部分（BIL §11 的 intrinsic 键空间）。

### 4.4 canonical symbol 是投影，不是身份

`BIL_STANDARD.md` §5.2 的 canonical symbol 字符串格式是符号图的
**序列化投影**：实现为符号图上的打印函数（`CanonicalSymbolPrinter`），
供 BIL 发射、诊断消息与派发链诊断工具（RUNTIME §15）共用。中端内部
**任何地方不得**以 canonical 字符串做身份比较或查找键（BIL 发射之后
的世界才以字符串为身份）。

---

## 5. BoundTree（P3 产物）

### 5.1 节点设计

- 基类 `BoundNode`，回指字段 `Syntax: ASTNode`（必填；合成节点指向
  最近的语法来源）。表达式基类 `BoundExpression` 额外携带
  `Type: TypeSymbol`（分析定型后的严格类型）。
- 节点按语义命名（`BoundCall`、`BoundFieldAccess`、`BoundConversion`、
  `BoundBlock`……），子类集合以 P3 的分析需要为准，不与 AST 节点
  一一对应——例如 AST 的 `MemberAccess` 在 P3 分化为
  字段访问 / getter 访问 / 方法组等不同 bound 形态。
- **AST 只读**：P3 不修改 AST 任何字段、不重挂 Parent。
  `ExpressionRootASTNode` 是透明容器，BoundTree 不为它建节点。
- 分析结果以**函数体**为单位：`BoundFunctionBody { MethodSymbol,
  Locals, BoundBlock }`。声明侧初始化器同样产出 BoundFunctionBody
  （归属编译器合成的初始化方法符号）。

### 5.2 P3 必须落实的语义规则（易漏清单）

- **值块隐式取值**：if/switch 表达式分支体与部分求值
  位置的代码块，「块内恰好一条 ExpressionStatement」即隐式取值；
  多语句块须有 `return@_` / `return@标签`。判定在 P3 完成并显式记录在
  bound 节点上（P4 不再看语法形态）。
- **无运算符优先级**是前端已保证的事实：`BinaryExpression` 树无需
  也不得再平衡。
- smart cast：P3 只做**分析与标记**（某表达式在某区域内可视为窄化类型），
  显式 `cast` 指令由 P4a 物化。
- definite assignment 与「所有路径显式返回」在 P3 报错（BIL §21.4
  的对应义务在这里兑现，而不是等 BIL verifier 兜底）。
- wrapper proxy 声明体**模板态绑定**与 pass 归属（烘焙归 Middleware，
  BIL §23 边界）：
  - **P1**：符号壳（proxy 成员与普通成员同路径收集）。
  - **P2**：只保留形状校验（`ProxyShapeChecker`：元数/类别矩阵/
    canonical shape）与 wrapper 应用登记（`AppliedWrappers` /
    `WrapperApplication`，TTarget 代入显形、宿主构造安装用）。
    **不再**合成隐藏字段、派发链、特化符号或降级链——Freeze 前
    零合成符号。
  - **P3**：
    - **proxy 声明体模板态绑定**——绑定语境 = proxy 声明符号自身：
      wrapper 级泛型参数（`TTarget`）与 proxy 方法级泛型参数
      （`TReturn`/`TField` 等）按既有泛型参数路径直接解析
      （#27⑧ 随之闭环）；`self` 类型 = `TTarget` 泛型参数
      （Entity 恰一时；零个则引用 `self` 报错——SYNTAX §14.2）；
      `this` = wrapper 实例自身（模板 fn 的 `.this`，绑定宿主即
      wrapper 类型，不再重写为 `BoundWrapperAccessExpression`）；
      `inner(...)` 绑定为占位调用节点（实参正常绑定；期望形状 =
      specific 按 proxy 声明自身签名 / wildcard 按 canonical 形状
      去 symbol；返回类型 = proxy 声明返回类型）。**#27⑦ 包透传**：
      Bound 节点显式携带当前 proxy 方法声明序中的可变泛型包列表
      （`IsVariadic`/`IsNamedVariadic`；固定泛型不入列），不可仅在
      emitter 临时扫声明隐式推断。非 proxy 语境出现 `self`/`inner`
      是编译错误；诊断按 (proxy, span, message) 去重。
    - **使用点 wrapper place 绑定**原样保留：双源同池查找
      + 只读禁令全拦截面。
    - **局部/静态 wrapper 应用触发 cell 子类合成**（统一
      cell 存储，P3 合成例外）：`CellClassFactory` 逐变量合成
      隐藏子类 `..cell..UUID`（P2 仍 Freeze 前零合成；合成方法体
      经 `BindEnvironment.SyntheticCellBodies` 汇入 BindingDriver
      函数体列表走统一 P4 管线；静态/全局字段 cell 化在
      BindingDriver 阶段 1.6）。wrapper 应用标记挂在子类
      `value` 字段上（同 `WrapperApplication` 实例）。
    - **未声明方法降级判定**保留：candidates 空 + wrapper 链含
      `.proxy.*` → 产物改 `invoke core::Any$call???`（bootstrap
      声明 + VM hook）；`IsDowngradeCallResult`
      五位置豁免保留，判定改为引用相等 bootstrap `Any.call???`。
  - **P4**：发射 wrapper 类型的 proxy 成员为带
    `wrapper-proxy(specific|wildcard)` 修饰符的**模板 fn**
    （`inner` → `invoke fn(..inner)`、`self` → `get.self` 占位指令；
    `invoke fn(..inner)` 操作数 = 可变泛型包 `.generic.<Pack>` 前置 +
    显式值实参，BIL §15.4 / §7.2）；使用点 place 成员访问降级（读 =
    值拷贝 + 普通指令；写 = set.wrapper.field；局部/静态走 cell
    根分派）与声明段平铺仍归 P4（操作数见 §6.1）；cell 子类声明
    按方法体宿主归属收集发射（同 lambda 口径）。
  - **一切烘焙**（特化 / inner 链接 / 原始体替换 / 隐藏存储 /
    `call???` 类别路由体 / 包解包 shim / **静态 cell 构造时机**）
    **归 Middleware**（BIL §23 边界）。frontend 产物只携带标记：
    应用登记、proxy 模板 fn（含 `invoke fn(..inner)` 包透传操作数）、
    降级调用点对 `core::Any$call???` 的 `invoke`、cell 子类声明
    （含 `value` 字段 `wrapped(W)`）。

---

## 6. LoweredTree 与发射（P4）

### 6.1 P4a：降级重写（Lowerer）

树到树重写，输入无错 BoundTree，输出 LoweredTree。基类 `LoweredNode`，
回指字段 `Origin: BoundNode`（必填）。可以由多个顺序 rewriter 组成；
每个 rewriter 职责单一、可独立测试。

脱糖清单（对照 BIL §3.4，加上本项目的深度 lowering）：

| 源结构 | 降级形态 |
|---|---|
| 安全调用 `?.`、`if?` 空值回退 | nullable 检查 + 条件结构 |
| smart cast 标记 | 显式 `cast`（BIL §3.1） |
| source-level 子类型赋值 / 传参 | 显式 `cast`（BIL §6.5） |
| 内建 `bool` 短路 `and` / `or` | 条件结构 + 临时变量（BIL §11.3） |
| 复合赋值（`+=` 等 10 种） | 读取 + 基础运算 + 写回 |
| `seq` 与 `return@` | 结构化 block（region + `.breakid`）+ 结果临时变量 + `LoweredStructuredExit` 标记；P4a 末尾 StructuredExitRouting pass 展开为「写结果局部 + route 局部（i32，仅跨 region）+ break 当前 region」，需要 multiplex 的 region 后生成 dispatcher（读 route → relay 父 region） |
| pattern switch（含 `_` 分支） | 常量表 switch / 嵌套条件（BIL §16.6） |
| 解构声明 | 精确字段/索引读取 |
| `using` | 初始化 + 清理记录 + try/finally 路径（RUNTIME §25.1） |
| wrapper place 成员访问（`obj:W.f`、`obj:W.m()`） | **全部读取**统一值拷贝 + 普通指令：Entity = `get.wrapper` + `get.field`/`invoke`/`get.array`；字段-Value = `get.wrapper.field` + 普通指令；嵌套链逐层物化（BIL §12.4）。成员写（直接字段）= `set.wrapper.field`（Entity = `wrapper(W)`；字段-Value = `field(HOST_FIELD)+wrapper(W)`）；深层纯字段写穿 `place.a.b...` = P4a 多 get/set（正向 get + 叶写 + 反向 set；值类型中间写回，引用中间停止；最外层必要写回复用 `set.wrapper.field`，普通值中间反向写回仍发 `set.field`，**不新增**专用深写 opcode）；**局部/静态存储 = cell 根**（统一 cell 存储：读 = `get.wrapper.field $cell field(value) type(W)`，写 = `set.wrapper.field` 链 `field(value)+wrapper(W)`，复合赋值读写分离；静态字段值读写 = `get.field.static` 取 cell + getValue/setValue）；索引写仍归口 |
| 未声明方法的 wrapper 降级（SYNTAX §14.7） | `invoke core::Any$call???`（胖值 ABI；BIL §15.5） |
| 字符串插值 | 拼接/格式化调用链 |
| trailing lambda、`TypeName(...)` 简写等 | 规范调用形态 |
| async/await/yield | 直接发 BIL §17；状态机由 Middleware 降级（§7，专项设计） |
| 隐藏参数（`.generic.*` / `.vargs.*` / `.kwargs.*`） | 按 BIL §7 规范签名显式化 |

降级**不得改变** `SYNTAX.md` / `RUNTIME.md` 规定的可观察语义
（求值顺序、getter/setter/operator/wrapper 调用顺序、异常路径）。

> **route local 不做活跃性复用是有意设计（安全性取舍）**：
> StructuredExitRouting 为每个确实需要 multiplex non-local exit 的
> structural region 独立合成一个普通 i32 route 局部（`.sN`），进入
> region 前初始化 `0`，post-region dispatcher 之后其逻辑生命周期即
> 结束；route tag 各 region 私有、从 `1` 起编号、无函数级含义。
> **有意不做**跨 region 的活跃性分析与局部复用，理由：
> ① route 局部的全部含义只存在于「本 region 入口 → 本 region
> dispatcher」之间，复用必须证明两块 life range 不相交且 tag 命名
> 空间不混淆——而 region 结构会被后续 lowering 继续改变（using 包
> try、短路/安全访问合成 if），证明负担随之一再重估；一旦误判，
> 失败形态是 dispatcher 读到陈旧 tag、控制流走错目标，属于最恶劣
> 的静默语义错误。② BIL 的虚拟可变寄存器数量无限，多几个 `.sN`
> 对正确性与可读性零成本；`.vars` 体积与寄存器合并是 DCE /
> register allocation / coalescing 问题，按职责边界归 Middleware
> （BIL §23），frontend 不做。因此这不是技术债；改动此决策须重新
> 评估上述安全论证。
>
> 同 region 尾位 exit 省略冗余 break：exit 已处所属 region 末尾
> 位置（仅隔透明 LoweredBlock）时落尾与 break 落点完全相同，
> StructuredExitRouting 只写结果局部、不再发 break（tail-position
> 分析，isTail 随递归下传）；finally 块除外——finally 内 exit 必须
> 以 abrupt completion（break）覆盖 SavedCompletion，递归进
> FinallyBlock 时强制 isTail=false（落尾 Normal 会让 VM 恢复 body
> 的原 completion，语义错误）。

> **逃逸型/混合形态值块表达式的可用位置（现状记录）**：「体全路径
> 向外逃逸、自身不产值」的 seq/if/switch 表达式（逃逸型）与「产值
> 与逃逸并存」（混合型）现已端到端贯通（P3 定型取期望类型 +
> StructuredExitRouting 逃逸截断 + BIL §18.1 hint 供 verifier DA
> 分组）。可用位置：变量初始化、赋值右值、return@/return 值、调用
> 实参、条件位（while/do-while/if——条件恒 bool，以 bool 为期望类型
> 定型；逃逸条件的 judge 写回改写 false 字面量——写回是 loop 协议
> 结构部件必须存在、动态不可达故值任意；StructuredExitRouting 对
> Judge 块抑制逃逸 region 截断，写回不会被砍）。**仍拒绝的位置**
> （P3 诊断 `a type annotation is required`，清晰可操作）：for
> iterable 位、switch selector 位、二元运算操作数位、字符串插值段
> ——这些位置的类型取自表达式自身（鸡生蛋，无期望类型可传），解除
> 需要类型系统层的期望推导（如显式标注语法），属后续里程碑候选而
> 非缺陷。

> **wrapper 烘焙的 pass 归属**：详见 §5.2
> 整段。摘要——P1 符号壳；P2 只形状校验 + 应用登记（Freeze 前零
> 合成符号）；P3 proxy 模板态绑定 + 使用点 place/降级判定；P4 发射
> 带 `wrapper-proxy(...)` 的模板 fn（`invoke fn(..inner)`/`get.self`）与
> place 读/写降级（上表）；**一切烘焙归 Middleware**（BIL §23）。

### 6.2 P4b：发射（BilEmitter）

LoweredTree → `BilModule` 的机械线性化。此时不再有任何语言级决策：

- 表达式树展平为指令序列，中间值物化为 `.vars` 临时变量
  （BIL §10.1：操作数只能是变量与符号表达式）；
- 字面量提取进 `Resources`，指令经 `load res(...)` 引用（BIL §4.2）；
- 控制流结构生成 block 与结构化指令（`if`/`loop`/`switch`/`try`，
  BIL §16），绝无任意跳转；
- 符号引用经 `CanonicalSymbolPrinter` 打印；
- `LocalSymbols` / `ExternalSymbols` 段按符号使用情况生成（§3）。

### 6.3 BIL 对象模型与工具层次（关注点分离）

```text
BilModule / BilFunction / BilBlock / Bil 指令 / BilResource …
    ├── BilWriter    （模型 → 标准 BIL 文本）
    ├── BilVerifier  （BIL §21）
    └── BilVm        （BIL §22）
```

- BIL 对象模型是**自足**的：不引用 BoundTree/LoweredTree/符号图的
  任何类型；`Origin: LoweredNode?` 是唯一例外，且为**可空的纯调试信息**
  ——从 BIL 文本反序列化得到的模型 Origin 恒为 null，语义完全等价。
- 依赖方向单向：`P4b → BIL 模型`；verifier/VM 只依赖 BIL 模型，
  对中端一无所知。这保证 BIL 生态（writer/verifier/VM/未来的
  文本 parser）可以独立于编译器演进，反之亦然。
- 完整调试链：`Bil指令.Origin → LoweredNode.Origin → BoundNode.Syntax
  → ASTNode.Span`，诊断与未来调试信息由此取得源位置。

---

## 7. 深度 lowering 与 BIL_STANDARD 待修订清单

**BIL §17 语义**：BIL §17 是标准协程语义，不删除也不降级为
普通 stdlib 调用。P3/P4 把 `await Task<T>/Task` 和 `yield`
直接落为强类型 BIL 指令，async 调用保持 §15.2 的 eager `invoke` 语义；`using`
仍由 P4a 编织为 `try/finally` 清理路径。

Middleware 是 BIL §17 的实现者，负责把可挂起函数 lower 为状态机、保存和恢复
continuation、注册 Task/Alarm waiter，并在 frame/Task/清理记录引用发布时遵守
`RUNTIME.md` §23 ownership fence。frame 布局、state 编号和 Native ABI 不属于 BIL，
也不以 stdlib 普通调用伪装。完整裁决、closure/局部访问器接入及 Middleware 保留
native 面见 `ASYNC_LOWERING_DESIGN.md`。

### 7.1 wrapper 值语义与 BIL 形态

规范把 wrapper 定为恒 rich struct，`obj:Wrapper` 为**只读 place**
（SYNTAX §14.5/§14.9）。相关 BIL 缺口与归属如下：

- **§12.4 / §13.3 place 形态（读侧统一值拷贝）**：
  `get.wrapper` / `get.wrapper.field` 保留为 lowering/VM 内部能力，
  全部字段读经值拷贝后发普通 `get.field`；`set.wrapper.field` 为
  wrapper 隐藏存储写后门（非普通 `set.field`）。深层写穿**不新增**
  专用深写 opcode，由 P4a 展开为多个现有 get/set；最外层必要写回复用
  `set.wrapper.field`，普通值类型中间层反向写回仍发 `set.field`。
  源码层 `obj:W = ...` 仍是编译错误，BIL 不为整体赋值准备写入指令。
- **写链操作数**：两态 `field(F)|wrapper(W)`（不扩展新字节码类）；
  Entity 应用 = `wrapper(W)`；字段-Value 应用寻址 = 相邻
  `field(HOST_FIELD)+wrapper(W)`（HOST_FIELD 带 `wrapped(W)`）。
  隐藏存储由 Middleware 合成（命名约定 BIL §5.3；BIL 文本不再声明
  `.wrapper.` 隐藏字段——与 RUNTIME §14 一致）；隐藏存储不可用普通
  字段寻址。
- **统一 cell 存储**：lambda 捕获与局部/静态 Value wrapper
  共用同一机制——`core::Cell<T>`/`ReadonlyCell<T>` 为抽象基类；P3
  `CellClassFactory` 逐变量合成隐藏子类 `..cell..UUID`（自持
  `pub value: T`，wrapper 应用以同 `WrapperApplication` 挂在该字段
  上，BIL 投影为字段 `wrapped(W)`）。局部/静态 place 以 **cell 根**
  分派：读 = `get.wrapper.field $cell field(value) type(W)`；写 =
  `set.wrapper.field` 链 `field(value)+wrapper(W)`；复合赋值读写分离；
  深写复用既有机制（cell 对象为终极宿主）。静态字段 BIL 声明
  类型投影为 cell 子类、字段槽不再投 `wrapped(W)`；cell 构造时机归
  Middleware（frontend 只生成与标注）。捕获侧 `.capture.*` 字段类型
  = cell 子类；已 cell 化变量按引用直接捕获不套第二层。P4a
  `ClosureStoragePlan` 通用化为 cell 存储计划；`CellStorageLowering`
  改写静态/全局 cell 读写；`WrapperPlaceLowering` 不含局部/静态处理。
- **proxy 模板占位指令**：模板 fn 体内 `inner` / `self` 分别
  发 `invoke fn(..inner)` / `get.self`；特化、inner 链接、原始体、router 体
  均不在 frontend 合成（§5.2）。
- **enum 判别**：§12.3 `type.is.case` + §8.5/§19.1 判别值资源与
  u16/u32 宽度规则仍有效。

其余机械同步：`.string` 归 ValueType 域（§6.2）、wrapper 类型
声明必须显式带 `rich` 及若干修饰符合法性条目（§8.2）。

---

## 8. 诊断模型（P1–P4 通用）

前端的 `LexerException` / `ParserException`（单发即死）**保持不变**；
中端新开可恢复诊断：

- `Diagnostic { Severity, Span?, Message, Phase }`；`Severity` 至少
  `Error` / `Warning`。暂不建错误码编号体系（简洁三问），测试按
  消息子串断言（与现有 `CheckParseError` 惯例一致）。
- `DiagnosticBag`：全编译单元一个实例贯穿 P1–P4，各 pass 只追加。
  函数体之间、声明之间的错误互不阻断——一次编译报出尽可能多的错误。
- 阶段推进门槛：任一 pass 结束时存在 `Error` 即停止推进到 P4
  （P1→P2→P3 之间尽量继续，以最大化单次报错量；无法继续的连锁错误
  用「毒化」符号/类型抑制次生噪音——例如解析失败的类型引用绑定为
  `ErrorTypeSymbol`，后续用到它的检查静默通过）。
- `CompilerInternalException` 语义不变：编译器自身 bug，永不用于
  用户源码错误。
- CLI 出口：诊断经 `Core/Logger` 走 stderr，格式含
  `文件:行:列` 与 Phase 标记；stdout 数据流（`--emit-bil` 等）不受污染。

---

## 9. 代码组织

新增三个顶层目录（与既有 `AST/` `Parser/` `Lexer/` 并列）：

```text
Semantic/                  # P1–P3 + 符号图 + 诊断
├── Diagnostics.cs            # Diagnostic / DiagnosticBag / Severity
├── Symbols/                  # SemanticSymbol 家族、驻留 cache、
│                             #   BootstrapSymbols、CanonicalSymbolPrinter
├── DeclarationCollector.cs   # P1
├── DeclarationResolver.cs    # P2
├── Binder.cs                 # P3 瘦入口
├── Binding/                  # P3 visitor 化基建：
│   ├── BinderVisitor.cs         # CRTP 三基类（通用/ExpressionVisitor
│   │                            #   追加 expectedType/BinderShellVisitor 壳填充）
│   ├── BindEnvironment.cs       # 只读环境（unit/declarations/NameResolver/诊断）
│   ├── BindContext.cs           # 函数级状态组合根（组件化：Frame/Accessor/
│   │                            #   Labels/Flow/Locals 五成员；组件即方言）
│   ├── BindFunctionFrame.cs     # 只读函数帧（Method/FileCtx/DeclaringType/
│   │                            #   IsDefaultValueContext + HasThis/CanAccess）
│   ├── AccessorBodyState.cs     # 访问器体状态（value 别名：Field/IsSetter）
│   ├── BindLabelState.cs        # 控制流标签栈集（值块/循环/switch 占位/seq 标签
│   │                            #   四栈封装 + 命中查找领域方法）
│   ├── FlowState.cs             # DA 流分析（收窄表的家——同生命周期分叉合并）
│   ├── Scope.cs                 # 词法作用域链
│   ├── Dispatchers.cs           # 类别分派（Expression/Statement/Block 唯一 switch）
│   ├── BoundAnalysis.cs         # BoundTree 静态分析（GuaranteesReturn 等）
│   ├── BindingDriver.cs         # 声明骨架遍历 + 逐函数体启动
│   ├── SymbolLookup.cs          # 实例成员/泛型字段替换/IsAssignable 查询
│   ├── MemberLookup.cs          # 名字解析查找序（宿主→命名空间链→通配 import）
│   ├── TypeReferences.cs        # 函数体内类型引用解析
│   └── Visitors/                # 结构 visitor 簇（Literal/Declaration/Conditional/
│                                #   Loop/Switch/TrySeq/Binary/Path/Call/TypeCheck）
└── Bound/                    # BoundNode 家族（按类别分文件，仿 AST/）
Lowering/                  # P4
├── Lowered/                  # LoweredNode 家族
├── Lowerer.cs                # P4a 瘦入口
├── LoweredVisitor.cs         # P4a CRTP 基类
├── LowerEnvironment.cs       # 只读环境
├── LowerContext.cs           # 函数级组合根（Method/TransformFailed +
│                             #   Synth/Output/Targets 三组件）
├── SynthLocalFactory.cs      # 合成局部工厂（.sN/.bN 独立计数统一登记 + ReferenceTo）
├── LowerOutputState.cs       # 前置语句机制（输出列表栈封装）
├── LowerTargetState.cs       # 降级目标映射栈集（五栈封装 + 命中查找）
├── LowerDispatchers.cs       # 类别分派 + LowerBlockVisitor（输出列表压弹）
├── LoweringDriver.cs         # 逐函数体启动
├── LoweringFacility.cs       # LowerArguments/EnsureDeclaredType（cast 物化）
├── Rewriters/                # 结构 visitor 簇（Statement/Loop/Switch/TrySeq/
│                             #   ValueBlock/Expression/NullSafety/Destructuring）
├── BilEmitter.cs             # P4b 瘦入口
├── EmitVisitor.cs            # P4b CRTP 基类（签名带 BilBlock target 施工目标）
├── EmitEnvironment.cs        # 模块级（Module/四类资源去重表跨 fn 共享）
├── EmitContext.cs            # 函数级组合根（Function + Temps/BlockIds）
├── TempVarTable.cs           # 临时变量 .tN 工厂（自 EmittingFacility 收编）
├── BlockIdAllocator.cs       # 分支 block 编号分配器（if/loop/switch/seq/try）
├── EmitDispatchers.cs        # 类别分派（语句 Unit/值 BilVariableOperand）
├── EmittingDriver.cs         # 模块组装 + fn 定义发射
├── EmittingFacility.cs       # 资源登记/intrinsic 枚举映射/转义 共享辅助
└── Emitting/                 # 结构 visitor 簇（LocalSymbols/Statement/Value）
Bil/                       # BIL 生态（对中端零依赖）
├── BilModule.cs              # Module/Metadata/Resources（含 switch-table/
│                             #   catch-table 专用资源类）+ BilScalarType
├── BilSymbols.cs             # 类型与成员声明 + 种类枚举 + BilModifier 子类族
├── BilFunction.cs            # Function/.args/.vars/Block + BilBlockModifier
├── BilInstructions.cs        # 指令基类 + 操作数模型（blk/res 持对象引用）
├── BilComputeInstructions.cs # §11–§12 指令 + 运算/类型检查枚举
├── BilDataInstructions.cs    # §13–§15 指令
├── BilControlFlowInstructions.cs # §16 指令
├── BilSpellings.cs           # 枚举 → 标准拼写唯一定义点
├── BilWriter.cs              # 模型 → 标准 BIL 文本（指令自渲染，无 opcode switch）
├── BilVerifier.cs
└── BilVm.cs
```

文件粒度按实现时实际情况拆分；上表只钉死**目录边界与依赖方向**：
`Semantic → AST`；`Lowering → Semantic`；`Lowering → Bil`；
`Bil` 不依赖任何编译器内部目录。

**visitor 化**：
三树的遍历统一为 CRTP visitor 协议——静态 `Visit` 唯一入口（创建子类
实例 + Enter/Exit 生命周期模板，栈压/弹 finally 固化）、双协议
（`Visit → TResult?` 上行合成 / `VisitInto(shell)` 施工壳填充）、
context 方言（同一函数级状态对象的接口视图，Environment 只读共享；
组件化——组合根 + 职责组件类，组件即方言）、
类别分派器唯一 switch + 结构 visitor 簇级分文件。新增语法结构的
落点：对应簇文件新增 visitor 类 + 分派器注册一行。

CLI 接入：`CompileCommand` 的 `if (!parseOnly)` 分支；新增子命令仿
`DumpAstOption` 模板——预期为 `--emit-bil PATH`（发射 BIL 文本）与
`--sema-only`（只跑 P1–P3，用于诊断验收），注册进
`CompileCommand.SubCommands`。

---

## 10. 测试策略

沿用项目自研控制台测试（AGENTS §5），逐层验收：

- **符号图测试**：断言驻留（同一引用）、继承图、canonical 打印串
  （`CanonicalSymbolPrinter` 输出直接对照 BIL §5.2 的例子）。
- **P3 测试**：新建 `BoundDescribe`（仿 `AstDescribe` 的唯一描述器），
  断言 bound 树形态 + 类型定型结果 + 结构性事实；诊断测试断言
  `DiagnosticBag` 内容（消息子串 + Span），新增 `CheckSemanticError`
  类断言进 `TestHarness`。
- **P4 测试**：`LoweredDescribe` 断言脱糖形态（BIL §3.4 每条规则
  至少一个用例）；发射测试直接断言 `BilWriter` 文本（BIL 文本本身
  就是规范化的快照格式，无需再造描述器）。
- **端到端**：`compile --emit-bil` 对照 BIL §20 例子级别的黄金文件。
- **BilVm**：新增执行断言（跑出结果/异常与预期比对），
  测试从「形态断言」升级为「语义断言」；这也是 BIL_STANDARD §21.9
  「VM 可执行性」的持续验证。
- 每个新组件照旧在 `TestRunner` 注册表注册独立套件。

---

## 11. 原则重申

1. **文档驱动**：动一个语义规则前先读 SYNTAX/RUNTIME/BIL_STANDARD
   对应章节；三份文档冲突时按 BIL §24.3 的优先序，并把冲突记录进
   本文档 §7 这类待修订清单，不静默绕过。
2. **简洁三问**同样适用于中端：每个新 pass、新节点、新符号种类
   都要过「有必要吗 / 有更简单的吗 / 能复用吗」。
3. **P3/P4 边界是纪律**：P4 发现自己需要"再想一下类型/重载/名字"，
   说明 P3 缺信息——回去补 BoundTree，禁止在 P4 里就地分析。
4. **AST 只读、符号图 P2 后冻结、BIL 模型自足**——三条数据所有权
   规则违反任何一条都视为架构破坏。
