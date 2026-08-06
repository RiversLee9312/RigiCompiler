# Latte 语义分析与 BIL 生成架构（中端）

> **状态**: 架构定稿 1.0（2026-07-29 讨论定稿）
> **定位**: 本文档规定 AST → BIL 之间全部编译阶段（下称**中端**）的架构：
> 阶段划分、数据结构、数据流向、诊断模型、代码组织与验收方式。
>
> **文档分工**：源语言合法性以 `SYNTAX.md` 为准；运行时可观察行为以
> `RUNTIME.md` 为准；BIL 编码与验证规则以 `BIL_STANDARD.md` 为准。
> 本文档只规定中端**内部**如何组织，不重新定义上述三者的语义。
> 里程碑计划见同目录 `SEMANTIC_ROADMAP.md`；进度现状见 `docs/PROGRESS_REPORT.md`。

---

## 1. 定位与总体管线

中端的输入是前端产物：经 `ASTIntegrityValidator` 验证的 `RootASTNode`
（每编译单元多个源文件，每文件一棵）。输出是符合 `BIL_STANDARD.md` 的
BIL 模块（内存对象模型 + 文本序列化）。

`BIL_STANDARD.md` §3.3「frontend 不变量」是中端的**需求清单**：名称解析、
访问控制、类型推断、重载解析、默认参数填充、具名参数重排、泛型约束检查、
smart cast、rich/shared 闭包检查、async 共享安全、extension/wrapper 静态
组合链、语法糖规范化——全部必须在中端完成，Middleware 一概不做。

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

核心决策（均为 2026-07-29 讨论定稿，修改须重新过一遍取舍）：

1. **P3 与 P4 严格分离**。P3 产出完整的带类型语义结果，P4 只做机械翻译。
   每一步可独立验收，防止分析与发射互相渗透形成不可测试的整体。
2. **Roslyn 风格独立 Bound Tree，且 P3/P4 各一棵**。AST 保持只读
   （M29 后 AST 无 annotations 挂点，这是刻意设计，不走回头路）；
   语义信息全部活在 BoundTree/LoweredTree 与符号图上。
3. **P4a 与 P4b 分离（LoweredTree 存在的理由）**：emit 之前要做的
   lowering 相当深——async/await 物化为对标准库 Task 实现的调用
   （见 §7）、using 物化为清理记录与 try/finally 路径等；同时避免
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
| wrapper 适用性与静态组合链确定 | P2 | `@WrapperTarget` 类别 × 目标声明 |
| extension 目标注册 | P2 | `ext` 成员挂到目标类型符号 |
| canonical symbol 定形 | P2 | 符号图建成即可打印（§4.4） |
| 名称解析（表达式内） | P3 | 作用域链：块 → 参数 → 成员 → 全局 → import |
| 类型推断（`var` / 字面量 / 表达式类型） | P3 | |
| 重载解析（source-level overload ranking） | P3 | 唯一一处做 ranking 的地方（BIL §3.3） |
| 运算 / getter / setter / 索引 / 构造的精确签名规范化 | P3 | |
| 默认参数填充、具名参数重排 | P3 | BoundCall 已是规范参数序 |
| 泛型约束检查（使用侧实参） | P3 | |
| smart cast 分析 | P3 | 结果记录在 BoundTree，显式 cast 由 P4 物化 |
| 访问控制检查（使用点） | P3 | |
| definite assignment / 所有路径显式返回 | P3 | BIL §21.4 要求 frontend 保证 |
| async 边界共享安全检查 | P3 | SYNTAX §4.5 五项闸门：receiver / 参数 / TResult / 捕获 / 泛型实参 |
| M33 值块隐式取值 | P3 | 「块内恰好一条 ExpressionStatement」判定为取值形态 |
| 语法糖规范化（全部脱糖） | P4a | 清单见 §6.1 |
| 短路展开、smart cast / 子类型赋值的显式 `cast` 插入 | P4a | BIL §3.1/§6.5/§11.3 |
| async/await/yield 物化、using 物化 | P4a | 深度 lowering，见 §7 |
| 隐藏参数物化（`.generic.T` / `.vargs` / `.kwargs`） | P4a/P4b | 规范签名见 BIL §7 |
| 表达式线性化、临时变量物化、`.vars` 收集 | P4b | |
| Resources 提取（字面量 → `res(...)`） | P4b | BIL §4.2：指令不得内联字面量 |
| block 结构生成（if/loop/switch/try） | P4b | BIL §16 结构化控制流 |

P1 与 P2 分开的原因：Latte 声明可以互相前向引用，必须先收齐全部名字
再解析类型引用。P2 结束后符号图**冻结**——P3/P4 只读它，不再写入
（局部变量符号除外，它们归属各自函数的分析结果）。

每个 pass 的失败策略：诊断累积、尽量继续（§8）；但存在 Error 级诊断时
**不进入下一个 pass 的发射性工作**——P4 只接受无错的 BoundTree。

---

## 3. 编译单元模型

- 一次 `compile` 调用处理一个**编译单元**（未来对应一个程序集 / 一个
  BIL 文件）：多个 `.latte` 源文件 + bootstrap 符号 + `core.latte` 声明。
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

### 4.3 bootstrap 与 core.latte（混合策略）

类型层级根与基元类型无处用源码声明，采用**硬编码 bootstrap + core.latte
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
- **core.latte**：其余标准库表层（`core::Console`、`core.coroutine::Task`
  / `Executor` / Alarm 家族、`core::IDisposable`、异常类型、
  `core.ComparisonResult` 等）以 Latte 声明文件形式随编译器载入，
  用自己的前端解析后走同一条 P1/P2 路径。这同时构成前端的常驻回归测试。
- 划分原则：**类型系统与编译器本身依赖的进 bootstrap；只有语义分析的
  "用户"才依赖的进 core.latte**。基元类型上的运算符集合属于 bootstrap
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

- **值块隐式取值（M33 遗留义务）**：if/switch 表达式分支体与部分求值
  位置的代码块，「块内恰好一条 ExpressionStatement」即隐式取值；
  多语句块须有 `return@_` / `return@标签`。判定在 P3 完成并显式记录在
  bound 节点上（P4 不再看语法形态）。
- **无运算符优先级**是前端已保证的事实：`BinaryExpression` 树无需
  也不得再平衡。
- smart cast：P3 只做**分析与标记**（某表达式在某区域内可视为窄化类型），
  显式 `cast` 指令由 P4a 物化。
- definite assignment 与「所有路径显式返回」在 P3 报错（BIL §21.4
  的对应义务在这里兑现，而不是等 BIL verifier 兜底）。
- wrapper proxy 体按 (proxy × 目标成员) 组合逐组绑定（M81 定稿，
  ROADMAP S11b）：`self` 绑定为宿主角色（类型 = wrapper 泛型参数
  代入结果），`inner` 绑定为对下一环符号的普通调用，proxy 体内
  `this` 重写为只读 place（BoundWrapperAccessExpression）——三者在
  非 proxy 语境出现是编译错误；同一 proxy 声明体跨组合的诊断按
  (proxy, span, message) 去重。

---

## 6. LoweredTree 与发射（P4）

### 6.1 P4a：降级重写（Lowerer）

树到树重写，输入无错 BoundTree，输出 LoweredTree。基类 `LoweredNode`，
回指字段 `Origin: BoundNode`（必填）。可以由多个顺序 rewriter 组成；
每个 rewriter 职责单一、可独立测试。

脱糖清单（对照 BIL §3.4，加上本项目定稿的深度 lowering）：

| 源结构 | 降级形态 |
|---|---|
| 安全调用 `?.`、`if?` 空值回退 | nullable 检查 + 条件结构 |
| smart cast 标记 | 显式 `cast`（BIL §3.1） |
| source-level 子类型赋值 / 传参 | 显式 `cast`（BIL §6.5） |
| 内建 `bool` 短路 `and` / `or` | 条件结构 + 临时变量（BIL §11.3） |
| 复合赋值（`+=` 等 10 种） | 读取 + 基础运算 + 写回 |
| `seq` 与 `return@` | 结构化 block + 结果临时变量 |
| pattern switch（含 `_` 分支） | 常量表 switch / 嵌套条件（BIL §16.6） |
| 解构声明 | 精确字段/索引读取 |
| `using` | 初始化 + 清理记录 + try/finally 路径（RUNTIME §25.1） |
| wrapper place 成员访问（`obj:W.f`、`obj:W.m()`，S11c） | 读 = `get.wrapper` 值拷贝 + `get.field`；写 = `set.field.embedded`；调用 receiver = 值拷贝（BIL §12.4 注记/§13.3） |
| 未声明方法的 wrapper 降级（S11e，SYNTAX §14.7） | `call???` 胖值 `invoke`（BIL §15.4） |
| 字符串插值 | 拼接/格式化调用链 |
| trailing lambda、`TypeName(...)` 简写等 | 规范调用形态 |
| async/await/yield | 物化为标准库 Task 机制调用（§7，专项设计） |
| 隐藏参数（`.generic.*` / `.vargs.*` / `.kwargs.*`） | 按 BIL §7 规范签名显式化 |

降级**不得改变** `SYNTAX.md` / `RUNTIME.md` 规定的可观察语义
（求值顺序、getter/setter/operator/wrapper 调用顺序、异常路径）。

> **wrapper 烘焙的 pass 归属（M81 定稿，ROADMAP S11a–S11g）**：
> wrapper 派发分析（specific/wildcard 命中、链路计算、特化符号与
> `.wrapper.` 隐藏字段合成）是**符号级**工作，全部落在 P2（Freeze
> 前）；P3 对 proxy 声明体逐组合绑定（`self`/`inner`/`this` 语义，
> 合成转发壳与解包 shim）；P4 不承载 wrapper 语义——特化 fn、
> 原始体 fn 与转发壳在 LoweredTree/BIL 层就是普通函数与 `invoke`
> 链，P4 唯一的 wrapper 专属工作是 place 成员访问的 embedded/
> 值拷贝降级（上表 S11c 行）与声明段平铺。最终内联归 Middleware。

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
    ├── BilVerifier  （BIL §21，后续里程碑）
    └── BilVm        （BIL §22，后续里程碑）
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

**定稿方向（2026-07-29）**：async/await/yield 在 P4a 物化为对标准库
Task 实现的调用；Task 实现进一步调用 stdlib 标准要求 Middleware 暴露的
Native 方法。**Middleware 完全不关心上层异步模型**。`using` 同理在
P4a 物化。RUNTIME.md §17–§21/§25 规定的可观察语义不变，变的只是
实现层次归属（Middleware → 中端 lowering + stdlib + Native hooks）。

该方向与现行 `BIL_STANDARD.md`（草案 1.1）存在正面冲突，以下条目
**待修订**（修订是独立里程碑，本文档不代改规范）：

- §1.1：「协程 frame、continuation 和状态机的物理布局」不再属于
  Middleware，改属 stdlib + Native hooks；
- §1.2：「异常、`await` 和 `yield` 的可观察语义」「source-level `async`
  调用的 eager spawn 语义」改由 lowering 产物 + stdlib 保证，
  不再是 BIL 指令层语义；
- §17（协程指令 `await` / `yield`）：删除或降级为非标准扩展；
- §15.2（async 方法调用的 Task 结果规则）：随 async 物化方式改写；
- §21.3/§21.8/§22.2/§23 中涉及 await/yield/async 的验证与 lowering
  条目：同步清理；
- stdlib 标准（尚不存在）需要新增：Task 实现依赖的 Native 方法面
  （协程 frame 分配、continuation 捕获、调度挂钩等）。

async lowering 的具体形态（状态机切分、continuation 表示、与
RUNTIME §23 GC fence 的交互）是**专项设计**，动工前须先出专项文档
（对标 `EXPRESSION_ARCHITECTURE.md` 的角色），列入 ROADMAP 后段。

### 7.1 wrapper 值语义修订带来的 BIL 缺口（2026-07-29）

规范修订把 wrapper 从 Object 改为恒 rich struct，并规定 `obj:Wrapper`
是**只读 place**：只能作成员访问的接收者，不可整体赋值、不可整体取值
（SYNTAX §14.5/§14.9）。现行 `BIL_STANDARD.md` §12.4 只有值语义的
`get.wrapper` / `get.wrapper.indirect`，**缺少 place 形态**：无法表达
「以宿主持有的那份 wrapper 为接收者读写其字段、调用其方法」，也无法
表达 proxy 体内 `this` 的原地访问。

- 缺口是**只读 place 的取址/接收者形态**，不是赋值形态——源码层
  `obj:W = ...` 是编译错误，BIL 侧不需要为它准备写入指令。
- `get.wrapper` 的值语义读取在源码可达路径上已无对应物（源码取不出
  整份 wrapper）。它是保留为 lowering/VM 内部能力，还是收窄为
  place 形态的一部分，与 §5.3 的 wrapper 隐藏字段命名一并确定。
- 因此**待补而非待改**：落在 ROADMAP S11（wrapper lowering），在此
  之前 BIL 不改。

> **S11 定稿（M75，2026-08-05）**：缺口已补入 `BIL_STANDARD.md` ——
> `get.wrapper` 保留为 lowering/VM 内部能力（只读 place 的成员读取 =
> 值拷贝 + `get.field`），新增 §13.3 嵌套字段访问指令
> `get.field.embedded` / `set.field.embedded`（承载 `obj:Wrapper.field`
> 写入与 proxy 体内 `this` 的原地访问，wrapper 方法逻辑编译期内联故
> 无 place receiver 问题）；§12.3 同步增补 `type.is.case`（enum 判别
> 比较，RUNTIME §16.3 承载）；§8.5/§19.1 定稿判别值资源与 u16/u32
> 宽度规则。P3/P4 消费见 ROADMAP S11。

其余因本次修订产生的 BIL 变更都是机械同步，已直接落实到规范：
`.string` 归 ValueType 域（§6.2）、wrapper 类型声明必须显式带 `rich`
及若干修饰符合法性条目（§8.2）。

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
├── Binder.cs                 # P3 瘦入口（M55 起）
├── Binding/                  # P3 visitor 化基建（M55）：
│   ├── BinderVisitor.cs         # CRTP 三基类（通用/ExpressionVisitor
│   │                            #   追加 expectedType/BinderShellVisitor 壳填充）
│   ├── BindEnvironment.cs       # 只读环境（unit/declarations/NameResolver/诊断）
│   ├── BindContext.cs           # 函数级状态组合根（M65 组件化：Frame/Accessor/
│   │                            #   Labels/Flow/Locals 五成员；组件即方言）
│   ├── BindFunctionFrame.cs     # 只读函数帧（Method/FileCtx/DeclaringType/
│   │                            #   IsDefaultValueContext + HasThis/CanAccess）
│   ├── AccessorBodyState.cs     # 访问器体状态（S8e value 别名：Field/IsSetter）
│   ├── BindLabelState.cs        # 控制流标签栈集（值块/循环/switch 占位/seq 标签
│   │                            #   四栈封装 + 命中查找领域方法）
│   ├── FlowState.cs             # DA 流分析（S8b 收窄表的家——同生命周期分叉合并）
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
├── Lowerer.cs                # P4a 瘦入口（M55 起）
├── LoweredVisitor.cs         # P4a CRTP 基类
├── LowerEnvironment.cs       # 只读环境
├── LowerContext.cs           # 函数级组合根（M65：Method/TransformFailed +
│                             #   Synth/Output/Targets 三组件）
├── SynthLocalFactory.cs      # 合成局部工厂（.sN/.bN 独立计数统一登记 + ReferenceTo）
├── LowerOutputState.cs       # 前置语句机制（输出列表栈封装）
├── LowerTargetState.cs       # 降级目标映射栈集（五栈封装 + 命中查找）
├── LowerDispatchers.cs       # 类别分派 + LowerBlockVisitor（输出列表压弹）
├── LoweringDriver.cs         # 逐函数体启动
├── LoweringFacility.cs       # LowerArguments/EnsureDeclaredType（cast 物化）
├── Rewriters/                # 结构 visitor 簇（Statement/Loop/Switch/TrySeq/
│                             #   ValueBlock/Expression/NullSafety/Destructuring）
├── BilEmitter.cs             # P4b 瘦入口（M55 起）
├── EmitVisitor.cs            # P4b CRTP 基类（签名带 BilBlock target 施工目标）
├── EmitEnvironment.cs        # 模块级（Module/四类资源去重表跨 fn 共享，M57）
├── EmitContext.cs            # 函数级组合根（M65：Function + Temps/BlockIds）
├── TempVarTable.cs           # 临时变量 .tN 工厂（自 EmittingFacility 收编）
├── BlockIdAllocator.cs       # 分支 block 编号分配器（if/loop/switch/seq/try）
├── EmitDispatchers.cs        # 类别分派（语句 Unit/值 BilVariableOperand，M57）
├── EmittingDriver.cs         # 模块组装 + fn 定义发射
├── EmittingFacility.cs       # 资源登记/intrinsic 枚举映射/转义 共享辅助（M57）
└── Emitting/                 # 结构 visitor 簇（LocalSymbols/Statement/Value）
Bil/                       # BIL 生态（对中端零依赖）
├── BilModule.cs              # Module/Metadata/Resources（含 switch-table/
│                             #   catch-table 专用资源类）+ BilScalarType（M57）
├── BilSymbols.cs             # 类型与成员声明 + 种类枚举 + BilModifier 子类族（M57）
├── BilFunction.cs            # Function/.args/.vars/Block + BilBlockModifier（M57）
├── BilInstructions.cs        # 指令基类 + 操作数模型（blk/res 持对象引用，M57）
├── BilComputeInstructions.cs # §11–§12 指令 + 运算/类型检查枚举（M57）
├── BilDataInstructions.cs    # §13–§15 指令（M57）
├── BilControlFlowInstructions.cs # §16 指令（M57）
├── BilSpellings.cs           # 枚举 → 标准拼写唯一定义点（M57）
├── BilWriter.cs              # 模型 → 标准 BIL 文本（指令自渲染，无 opcode switch）
├── BilVerifier.cs            # 后续里程碑
└── BilVm.cs                  # 后续里程碑
```

文件粒度按实现时实际情况拆分；上表只钉死**目录边界与依赖方向**：
`Semantic → AST`；`Lowering → Semantic`；`Lowering → Bil`；
`Bil` 不依赖任何编译器内部目录。

**M55 visitor 化定稿**：
三树的遍历统一为 CRTP visitor 协议——静态 `Visit` 唯一入口（创建子类
实例 + Enter/Exit 生命周期模板，栈压/弹 finally 固化）、双协议
（`Visit → TResult?` 上行合成 / `VisitInto(shell)` 施工壳填充）、
context 方言（同一函数级状态对象的接口视图，Environment 只读共享；
M65 组件化落地——组合根 + 职责组件类，组件即方言）、
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
- **BilVm 落地后**：新增执行断言（跑出结果/异常与预期比对），
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
