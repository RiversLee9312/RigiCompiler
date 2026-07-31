# Latte 语义分析与 BIL 生成路线图

> **用途**: 中端（AST → BIL）的里程碑计划。架构定稿见同目录
> `SEMANTIC_ARCHITECTURE.md`；本文档只管「计划」，进度现状一律记
> `docs/PROGRESS_REPORT.md`（落地时在其里程碑历史领取全局 M 编号）。
>
> 编号 S0–S14 是**计划序号**，近细远粗：S0–S7 已细化到文件级施工
> 清单（S0–S6 于 2026-07-31 细化，S7 于 2026-07-31 细化为 S7a–S7f），
> S8 以后随进展再细化。允许并行的地方已注明；
> 未注明的按序推进，不跳步。

---

## 总览与依赖

```text
S0 诊断基建 ─┐
S1 符号图内核 ┴→ S2 P1 声明收集 → S3 P2 声明解析 ─┐
S4 BIL 对象模型 + Writer（与 S1–S3 并行）─────────┤
                                                  ├→ S5 P3 最小闭环
                                                  └→ S6 P4 最小闭环（端到端 hello world）
S7 控制流全套 → S8 P3 完整化 → S9 泛型 → S10 core.latte
→ S11 wrapper/extension/enum → S12 BIL verifier
→ S13 async lowering 专项（含 BIL_STANDARD 修订）→ S14 BIL VM
```

验收总原则（用户定稿）：**BIL 模型/writer → lowering 最小闭环 →
verifier → VM**。VM 落地后测试从形态断言升级为执行断言。

---

## S0 诊断基建

- `Semantic/Diagnostics.cs`：
  - `enum DiagnosticSeverity { Error, Warning }`；
  - `enum DiagnosticPhase { P1, P2, P3, P4 }`；
  - `Diagnostic { Severity, Phase, Span: CharRange?, Message }`
    （暂不建错误码编号体系，ARCHITECTURE §8）；
  - `DiagnosticBag`：全编译单元单实例、只追加；`HasErrors` 门槛判定；
    可遍历供断言。
- `Tests/TestHarness.cs` 扩展：`CheckSemanticError(label, bag, msgPart)`
  ——断言存在消息含 msgPart 的 Error 诊断（沿用消息子串惯例）。
- **验收**：`Tests/DiagnosticsTests.cs` 套件（累积多条、多错不互断、
  HasErrors 门槛、Span/Phase 携带）注册进 `TestRunner`。

## S1 符号图内核 + bootstrap

- `Semantic/Symbols/` 四个文件：
  - `SemanticSymbol.cs`：基类（引用相等即身份）+ 最小派生集
    `NamespaceSymbol / TypeSymbol / FieldSymbol / MethodSymbol /
    ParameterSymbol / GenericParameterSymbol`（EnumCaseSymbol/LocalSymbol
    等后续里程碑按需增补，过简洁三问）。`TypeSymbol` 携带：类型种类
    （class/struct/enum-struct/interface/wrapper/内建）、BaseType、
    IsRich/IsShared、GenericParameters、成员表；构造泛型类型 =
    `(TypeDefinition, TypeArguments)`；`T?` 即构造类型 `Nullable\<T>`，
    不设独立 nullable 表示（SYNTAX §3.4）。
  - `SymbolGraph.cs`：符号图容器 = bootstrap 注册入口 + 构造类型
    驻留 cache（同 `(定义, 实参列表)` 必同实例）+ P2 结束 Freeze。
  - `BootstrapSymbols.cs`：SYNTAX §3.1 层级（注意 `Nullable\<T>` /
    `Box\<T>` 在 Object 分支，`String`/`Wrapper` 在 ValueType 分支，
    `Wrapper` 恒 rich）+ §3.2 基本类型 + `Type\<T>` /
    `Span\<T extends ValueType>` / `Box\<T extends ValueType>` 的约束与
    特权标记（`Box\<T> <: Object` 为内建事实、`Nullable\<T>` 的 shared
    按 T 推导）+ 基元运算符 intrinsic 键空间（BIL §11）。
  - `CanonicalSymbolPrinter.cs`：符号图 → BIL §5.2 五形态字符串；
    签名中的类型部分走 BIL 类型引用投影（基元 → `.i32` 等固定别名、
    `Nullable\<T>` → `.nullable<T>`、用户类型 → canonical）——
    该投影即 Bil 模型的类型引用承载形式（字符串，S4 依此对接）。
- **验收**：`Tests/SymbolGraphTests.cs`（驻留同一引用、bootstrap 层级
  `i32 <: ValueType <: Any`、`String`/`Wrapper` 在 ValueType 分支、
  `Box\<T> <: Object`、shared-safe 推导）+
  `Tests/CanonicalSymbolPrinterTests.cs`（打印串对照 BIL §5.2/§8.1
  示例逐条比对），注册进 `TestRunner`。

## S2 P1 声明收集

- `Semantic/CompilationUnit.cs`：编译单元模型落地（多源文件
  `RootASTNode` 集合 + 全局 `DiagnosticBag` + `SymbolGraph`，
  ARCHITECTURE §3）。
- `Semantic/DeclarationCollector.cs`：遍历编译单元全部声明骨架
  （不进函数体）建命名空间/类型/成员/全局符号壳；namespace 嵌套与
  import 上下文登记；重复声明诊断（累积不中断）。
- **验收**：`Tests/DeclarationCollectorTests.cs`——跨文件前向引用
  可收集；同名冲突出诊断且不中断；嵌套类型/namespace 路径正确。

## S3 P2 声明解析

- `Semantic/DeclarationResolver.cs`，子任务按序：
  1. 类型引用解析（`TypeReferenceASTNode` → `TypeSymbol`，含泛型实参
     递归、`T?` → `Nullable\<T>`）；失败绑 `ErrorTypeSymbol` 毒化；
  2. 继承 / implements 图 + 循环继承诊断；
  3. 修饰符合法性（SYNTAX §3.1.1 / §9.2 / §10 / §14.9 / §16：rich 仅
     struct 与 wrapper、shared struct 必 rich、非 rich struct 不得
     open/abstract、enum struct 不得 open、singleton 必 shared、
     wrapper 不得显式写 rich、abstract×singleton…）；
  4. rich/shared 字段闭包检查（SYNTAX §3.1.1 闭包表七行含 wrapper，
     递归）+ 单向传染（基类 rich/shared ⇒ 子类同标；反向由继承字段
     闭包重校验兜底）；
  5. 共享安全类型判定 + 全局/静态字段闸门（SYNTAX §3.1.1 闸门 1；
     `Nullable\<T>` 按 `T` 推导，SYNTAX §3.1.2）；
  6. 泛型约束声明侧检查；
  7. `ext` 成员注册到目标类型；wrapper 适用性
     （`@WrapperTarget` × `IWrapperAttachable` 三分类）与目标矩阵
     （SYNTAX §14.9：宿主可内嵌性、shared 矩阵 A–D、interface 实现者
     传染），以及静态组合链。
- P2 结束冻结符号图（`SymbolGraph.Freeze`）。
- **验收**：`Tests/DeclarationResolverTests.cs`——每个子任务独立
  测试组；SYNTAX §3.1.1 闭包表与 §14.9 目标矩阵逐行有用例
  （合法 + 非法各一）。

## S4 BIL 对象模型 + BilWriter（可与 S1–S3 并行）

- `Bil/`（对中端零依赖；类型引用一律以 canonical/标准构造字符串承载，
  即 S1 `CanonicalSymbolPrinter` 的投影形式）：
  - `BilModule.cs`：BilModule（`BIL "1.1"` 版本头 + Metadata +
    Resources + LocalSymbols + ExternalSymbols + Functions，§4）、
    Metadata 条目（§4.1）、Resource（§4.2/§18 资源字面量形态）。
  - `BilSymbols.cs`：类型声明（§8.2）与成员声明（字段 §8.3 /
    方法 §8.4 / enum case §8.5）+ 修饰符（pub/priv/backing/
    compiler-generated/entrypoint 等）。
  - `BilFunction.cs`：函数定义（§9.1）/ `.args`（§9.2）/ `.vars`
    （§9.3）/ Block（§9.4–§9.6）。
  - `BilInstructions.cs`：指令模型——运算（§11）、转换与运行时类型
    （§12）、值/变量/字段/索引（§13）、构造（§14）、调用（§15）、
    结构化控制流（§16）；**协程指令 §17 暂缓**（S13 与
    ARCHITECTURE §7 待修订清单）。
  - `BilWriter.cs`：模型 → 标准 BIL 文本（只输出标准 spelling，
    不输出 legacy，§5.6）。
  - `Origin` 以 `object?` 占位（S6 接通后改 `LoweredNode?`——或从
    第一天就放 Lowering 侧扩展，实现时按简洁三问定）。
- **验收**：`Tests/BilWriterTests.cs`——手工构造 BIL §19 完整示例
  （含 wrapper 隐藏字段示例）的内存模型，`BilWriter` 输出与规范文本
  逐行一致（黄金文件断言），注册进 `TestRunner`。

## S5 P3 最小闭环（BoundTree 起步）

- `Semantic/Bound/` 最小节点集（按类别分文件，仿 `AST/`）：
  `BoundNode`（`Syntax: ASTNode` 必填）/ `BoundExpression`
  （`Type: TypeSymbol`）+ 字面量、局部变量声明与引用、二元运算、
  赋值、块、调用、`new`、`return` 等节点。
- `Semantic/Binder.cs`：作用域链（块 → 参数 → 成员 → 全局 → import）、
  `var` 类型推断、bootstrap intrinsic 键查询、无重载直接函数调用、
  `new`、`return` + 所有路径显式返回检查、definite assignment 最小版；
  分析单位 = `BoundFunctionBody { MethodSymbol, Locals, BoundBlock }`。
- `Tests/BoundDescribe.cs`：唯一 bound 树描述器（仿 `AstDescribe`）。
- **验收**：`Tests/BinderTests.cs`——上述每类表达式/语句的 bound
  形态与定型类型断言 + 结构性事实；类型不匹配/未定义名字/未赋值
  使用三类诊断各有用例。

## S6 P4 最小闭环（端到端 hello world）

- `Lowering/`：`Lowered/LoweredNode.cs` 最小集（`Origin: BoundNode`
  必填）+ `Lowerer.cs`（此阶段近乎恒等重写）+ `BilEmitter.cs`
  （线性化、`.vars` 临时变量物化、Resources 提取、
  LocalSymbols/ExternalSymbols 生成）。
- Bil 模型 `Origin` 保持 `object?` 不收窄（Bil 对中端零依赖优先，
  发射时塞 `LoweredNode` 实例）；`LocalSymbols`/`ExternalSymbols` 段
  允许裸成员条目（全局函数声明，BIL §8.4.1）。
- CLI：`--emit-bil PATH` + `--sema-only`（仿 `DumpAstOption` 模板，
  注册进 `CompileCommand.SubCommands`；接入 `CompileCommand` 的
  `if (!parseOnly)` 分支）。
- **stdlib 最小载入（S10 机制的最小子集提前，2026-07-31 定稿，
  取代原「硬编码 `core::Console.println` external 符号」临时措施）**：
  - `native` 函数语法（SYNTAX §4.6）：`native` 修饰符 +
    `@NativeLibrary`/`@NativeSymbol` 内建注解；Parser 只加
    `Keywords.NATIVE`（无体函数与注解路径前端已具备）；P1 建壳读
    标记位（`MethodSymbol.IsNative`）、P2 新增 `CheckNativeDeclarations`
    子任务（注解解析填 `NativeSymbol`/`NativeLibrary` + SYNTAX §4.6
    全部规则校验，并在 wrapper 应用检查中为两个内建注解加豁免）；
  - `stdlib/core/Console.latte`（`core.io::Console`：priv static
    native `print`/`printErr`（lib `latte_rt`）+ pub static `println`
    包装）以 EmbeddedResource 内嵌载入，加入编译单元走同一
    P1/P2/P3 路径；
  - Binder 查找序补「宿主类型成员」一环（println 体内裸名调用同类
    静态方法，对齐 ARCH §2 既定查找序）；
  - native 成员声明进 LocalSymbols 带 `native symbol("...") lib("...")`
    修饰符（BIL §8.4），无 fn 定义；BIL VM 经 §21.5 内建 hook 执行
    （S14 验收）。
- **验收**：`main + 字面量 + core.io::Console.println + ret` 的
  `.latte` 源码经 `compile --emit-bil` 产出与 BIL §19 示例同级的合法
  BIL 文本（黄金文件对照）；Origin 调试链（Bil→Lowered→Bound→
  AST.Span）通。

## S7 控制流全套（P3 + P4 同步推进）

范围：if 语句/表达式（含 M33 值块隐式取值）、循环四形态 + break/continue
标签（`.breakid` 发射）、switch 语句/表达式（常量表 + pattern 降级）、
try/catch/finally（catch-table）、seq（含表达式形态 + `return@`）、
throw、短路 and/or 展开、`?.` / `if?` / 复合赋值 / 解构 / 字符串插值
脱糖。BIL §3.4 每条规范化规则至少一个 `LoweredDescribe` 用例。

2026-07-31 细化为六步，按序推进；每步 P3 与 P4 同步落地（P3 绑得出来的
形态，同一步内 P4 必须能发射，端到端 `--emit-bil` 可验证）：

### S7a P4 基础发射补齐 + LoweredDescribe 基建

S5 已能绑定的全部 Bound 节点在本步过 P4（控制流的前置：没有
赋值/运算/带返回值调用/new 的发射，任何控制流端到端用例都写不出来）。

- `Lowering/Lowered/`：补齐对应节点（局部声明/赋值/表达式语句/
  二元/一元/带返回值调用/new），`Origin` 必填不变；
- `Lowering/Lowerer.cs`：分发覆盖 S5 全部 Bound 节点（仍为恒等重写）；
- `Lowering/BilEmitter.cs`：新发射 `set.var`（局部与全局静态字段赋值
  `set.field.static`）、§11 运算指令（`BilIntrinsicOp` → opcode 映射表
  单点）、`invoke`（带返回值）、`new`（§14.1，init 选择归 Middleware）；
  字面量资源补齐 §18.1 标量全形态（bool/char/f32/f64/null `type(...)`）；
- `Tests/LoweredDescribe.cs`：唯一 Lowered 树描述器（仿 BoundDescribe）；
- **验收**：`Tests/LowererTests.cs`（每类节点 Lowered 形态 +
  未覆盖诊断）+ `BilEmitterTests` 扩充（`var x = 1 + 2` 等含运算/赋值/
  new/调用的端到端文本比对），两套件注册进 `TestRunner`。

### S7b if 语句/表达式 + 短路 and/or + 复合赋值

- P3：`BoundIfStatement` / `BoundIfExpression`（else 缺失诊断）；
  **M33 值块隐式取值判定落地**（块内恰好一条 ExpressionStatement，
  ARCH §5.2 遗留义务，判定结果显式记录在 bound 节点）；多语句分支
  `return@_` / `return@标签` 绑定；definite assignment 升级为分支合并；
  `GuaranteesReturn` 升级为全路径（if/else 双支均保证才算）；
  复合赋值（`CompoundAssignmentExpressionASTNode`）绑定；
- P4a：内建 `bool` 短路 `and`/`or` → 条件结构 + 临时变量（BIL §11.3）；
  if 表达式 → 结果临时变量规范化；复合赋值 → 读 + 基础运算 + 写回；
- P4b：`if` 指令发射（§16.2）+ 函数多 block 生成机制（block 平铺、
  `blk(...)` 引用、`none` 空分支）；
- **验收**：`LoweredDescribe` 短路/复合赋值脱糖用例（§3.4 规则各一）+
  emitter 端到端（if 语句/表达式出合法多 block BIL）+ definite
  assignment 分支诊断用例。

### S7c 循环四形态 + break/continue

- P3：`BoundLoop`（for/while/do-while/named 标签）+ break/continue
  标签解析（循环外使用诊断）；for 的 RangeTo 语义按 SYNTAX 落地；
- P4b：`loop` / `loop.rev`（§16.3/§16.4）+ `break` / `continue`
  （§16.5）+ BREAK_ID 变量（`.vars` 内声明，capability 规则 §20.6
  由 verifier 复核，本步只保证发射形态合法）；
- **验收**：四形态循环 + 嵌套循环标签 break/continue 端到端用例。

### S7d switch 语句/表达式 + throw

- P3：`BoundSwitch`（值匹配 case 常量判定 vs 含 `_` 的 pattern 分支
  分类，结果显式记录）；throw 绑定（异常根类型兼容性——异常类型
  进 bootstrap 还是 stdlib 在本步定稿，S10 边界清单同步）；
- P4a：pattern 分支降级为嵌套条件（§16.6 规则）；switch 表达式 →
  结果临时变量；
- P4b：`switch` 指令 + `switch-table` 资源（§18.4）+ `throw`（§16.9）；
- **验收**：常量表 switch 端到端 + pattern 降级 `LoweredDescribe`
  用例 + throw 用例。

### S7e try/catch/finally + seq（含 `return@`）

- P3：try/多 catch/finally(e) 绑定；seq 块（语句/表达式双形态）与
  `return@标签` 全链解析（P3 标签作用域）；`GuaranteesReturn` 覆盖
  try/catch 路径；
- P4a：`seq` 与 `return@` → 结构化 block + 结果临时变量 + `call`
  （§3.4）；
- P4b：`try` 指令 + `catch-table` 资源（§18.5）+ `call blk(...)`
  （§16.1）；
- **验收**：try/catch/finally(e) 端到端 + seq 表达式取值脱糖
  `LoweredDescribe` 用例。

### S7f 剩余脱糖

`?.` 安全调用、`if?` 空值回退、解构声明、字符串插值（§3.4 逐条，
`LoweredDescribe` 每规则至少一用例）；using 物化不在本步（与 async
lowering 在 S13 汇合，ARCH §7）。

## S8 P3 完整化

重载解析（source-level ranking 唯一落点）、默认参数填充、具名参数
重排、访问控制检查、getter/setter 绑定（三类位置）、smart cast 分析
（P4a 物化 cast）、`is`/`as`/`as?`/`typeOf`/`supers`/`with`、
async 边界五项闸门（SYNTAX §4.5：receiver / 参数 / TResult / 捕获 /
泛型实参，分析侧；lowering 在 S13）。

## S9 泛型

reified 泛型全链：使用侧约束检查、构造类型驻留完善、
`.generic.*` hidden args 物化（BIL §7 规范签名与参数序）、
`.generic<...>` 类型引用发射、泛型 new/调用。

## S10 core.latte 载入机制

core 声明文件随编译器载入（自举解析 → 同一条 P1/P2 路径）、
bootstrap 与 core.latte 边界定稿。**载入机制本身已提前至 S6 落地**
（EmbeddedResource 内嵌 + 编译单元注入，含 `native` 函数语法与
`core.io::Console` 最小文件）；本里程碑剩余工作为 stdlib 文件扩充
（`core.coroutine::Task`/`Executor` 家族、异常类型、`IDisposable` 等）
与 bootstrap/core.latte 边界定稿。兼作前端常驻回归测试。

## S11 wrapper / extension / enum struct

wrapper 静态组合链 lowering（specific/wildcard proxy、`call???` 降级、
`.wrapper.` 隐藏字段，BIL §5.3/§15.4）、ext 成员调用（`.this` receiver）、
enum case（`new.case`、`is .Case` 判别比较、判别值分配）、
派发链诊断工具（RUNTIME §15，编译器必备功能而非事后补充）。

wrapper 值语义落地要点（2026-07-29 规范修订）：wrapper 是 rich struct
值而非对象，`obj:Wrapper` 与 proxy 体内 `this` 都是宿主隐藏字段的
**原地访问**。`obj:Wrapper` 是只读 place：P3 需在此拒绝整体赋值与
整体取值（作实参/返回值/推断源皆非法，SYNTAX §14.5）。BIL §12.4 目前
只有值语义的 `get.wrapper`，缺只读 place 形态——本里程碑同时定稿并
补入 BIL 指令（见 ARCHITECTURE §7.1）。

## S12 BIL verifier

`Bil/BilVerifier.cs`：BIL §20 全部检查（词法/符号/类型/definite
assignment/控制流/`.breakid` capability/泛型/可见性），作为
`--emit-bil` 的默认后置自检 + 独立测试套件（合法模块通过 +
每类违规拒绝）。

## S13 async lowering 专项 + BIL_STANDARD 修订

先出专项设计文档（async/await/yield → 标准库 Task 机制调用的具体
形态：状态机切分、continuation 表示、eager spawn、与 RUNTIME §23
GC fence 的交互、stdlib 要求 Middleware 暴露的 Native 方法面），
同步修订 BIL_STANDARD（ARCHITECTURE §7 待修订清单逐条落实），
然后实现。`using` 可挂起清理与 async lowering 在此汇合。

## S14 BIL VM

`Bil/BilVm.cs`：BIL §21 抽象值语义解释器。落地后新增执行断言
测试形态（跑出结果/异常与预期比对），并持续验证 §20.9
「VM 可执行性」。native 调用经 §21.5 内建 hook 表执行
（`latte_rt` 的 `print`/`printErr` → stdout/stderr），hello world
端到端执行断言须产生真实输出，无需任何原生库。

---

## 与前端的接口备忘

- 中端输入恒为过 `ASTIntegrityValidator` 的 `RootASTNode`；
  发现前端应保证而未保证的结构问题 → `CompilerInternalException`，
  不出用户诊断。
- `ASTIntegrityValidator` 明示不查「声明侧字段完整性」（如 init 参数
  映射的空 Type 是合法形态）——这类检查是 P2/P3 的职责，勿漏。
- 前端遗留给语义期的义务清单：M33 值块隐式取值判定、
  init `_ -> field` 参数映射解析、import/namespace 的模块语义。
