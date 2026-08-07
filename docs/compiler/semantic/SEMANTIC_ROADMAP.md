# Latte 语义分析与 BIL 生成路线图

> **用途**: 中端（AST → BIL）的里程碑计划。架构定稿见同目录
> `SEMANTIC_ARCHITECTURE.md`；本文档只管「计划」，进度现状一律记
> `docs/PROGRESS_REPORT.md`（落地时在其里程碑历史领取全局 M 编号）。
>
> 编号 S0–S14 是**计划序号**，近细远粗：S0–S8 已细化到文件级施工
> 清单（S0–S6 于 2026-07-31 细化，S7 于 2026-07-31 细化为 S7a–S7f，
> S8 于 2026-08-01 细化为 S8a–S8f），S9 以后随进展再细化。
> 允许并行的地方已注明；
> 未注明的按序推进，不跳步。

> 已完成的 super 全链：P3 仅在 override/init 的直接 BaseType 上解析，P4 使用
> `fn(..super)`；wrapper 继承检查与 variance 已在 M96 补齐。`..create` 仅为
> Middleware/VM 生命周期步骤，frontend 不生成。

---

## 总览与依赖

```text
S0 诊断基建 ─┐
S1 符号图内核 ┴→ S2 P1 声明收集 → S3 P2 声明解析 ─┐
S4 BIL 对象模型 + Writer（与 S1–S3 并行）─────────┤
                                                  ├→ S5 P3 最小闭环
                                                  └→ S6 P4 最小闭环（端到端 hello world）
S7 控制流全套 → S8 P3 完整化 → S9 泛型 → S10 core.latte（✅ 2026-08-05 M74）
→ S11 wrapper/extension/enum → S12 BIL verifier（✅ 已提前至 M58，见下）
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
    Metadata 条目（§4.1）、Resource（§4.2/§19 资源字面量形态）。
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
- **验收**：`Tests/BilWriterTests.cs`——手工构造 BIL §20 完整示例
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
    修饰符（BIL §8.4），无 fn 定义；BIL VM 经 §22.5 内建 hook 执行
    （S14 验收）。
- **验收**：`main + 字面量 + core.io::Console.println + ret` 的
  `.latte` 源码经 `compile --emit-bil` 产出与 BIL §20 示例同级的合法
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
  字面量资源补齐 §19.1 标量全形态（bool/char/f32/f64/null `type(...)`）；
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

2026-08-01 协议定稿（已落 SYNTAX §7.3/§13.2/§15.3）：范围循环半开
`[a, b)`、步长 +1；`a to b` 即 `a.EnumerateInRange(b)`（T 上的实例
运算符，`this` 即 start）；`IEnumerable\<T\>`/`IEnumerator\<T\>` 为
core.collections 的 C# 风格双接口；基元实现落 `stdlib/.bootstrap.latte`
（SDK 自举源，默认参与编译）。据此拆两步：

**S7c-1（while / do-while / break / continue）**

- P3：`BoundLoop`（While/DoWhile；引用相等即身份）+ `BoundLoopControl`
  （break/continue，循环标签栈解析，循环外诊断）；**值块内 break/continue
  允许穿透**（GuaranteesValueReturn 视命中外层循环者为路径终止，BIL
  §16.5 动态结构作用域合法）；DA（while 后 = before、do-while 后 = body
  尾集合）与 GuaranteesReturn（循环保守 false，`while (true)` 留口）。
- P4a：`LoweredLoop{IsRev, Judge, Condition（合成 bool 局部）, Body,
  Enumerator?, BreakId（合成 .breakid 局部 .bN）}` + `LoweredLoopControl`；
  条件求值移入 Judge 块。
- P4b：`loop`/`loop.rev`（§16.3/§16.4，块 id `loop0-body`/`loop0-judge`）
  + `break`/`continue`（§16.5）+ `.vars` 的 `.breakid` 条目。
- **验收**：while/do-while/嵌套标签循环端到端多 block BIL + 各诊断用例。

**S7c-2（实例成员最小闭环 + IEnumerable + for 双形态）**

- P3：`BoundThisExpression`（路径首段 `this` 特判，静态上下文诊断）+
  `BoundInstanceCallExpression` + `BoundFieldAccessExpression`；BindPath
  实例链上色（沿 BaseType 链 + 接口成员；ext 成员同路径——P2 已注册到
  目标类型，BIL §7.3 同用 `.this`）；`for (i in a to b)` 绑定为
  EnumerateInRange 实例调用、for-each 做 `IEnumerable\<TItem\>` 实现判定；
  循环变量按 const 处理（只读默认，规范未明登记）。访问控制检查仍归 S8。
- P4a：for 统一脱糖为 `iterate()` 前置 + `LoweredLoop`（Judge =
  `moveNext()`、Body 头 = `item = current()`）——复用 S7c-1 发射零新增。
- P4b 开闸：实例方法 fn 定义（`.args` 首条 `.this = OwnerType`）、
  `invoke` receiver 首实参、`get.field`/`set.field`（§13.3）、
  operator/init 的 §8.4 声明形态。
- stdlib：`stdlib/core/collections.latte`（双接口 + `RangeEnumeratorI32`
  **class**——SYNTAX §10 struct 不得实现接口；泛型 `RangeEnumerator\<T\>`
  留 S9）+ `stdlib/.bootstrap.latte`（`pub ext operator
  i32.EnumerateInRange`）。早验证：ext+operator 前端组合、点开头文件
  内嵌匹配。
- **验收**：this/实例调用/实例字段绑定与发射用例 + `for (i in 0 to 3)`
  端到端合法 BIL（stdlib 两文件同走 P1–P4）。

### S7d switch 语句/表达式 + throw

- P3：`BoundSwitch`（值匹配 case 常量判定 vs 含 `_` 的 pattern 分支
  分类，结果显式记录）；throw 绑定（异常根类型兼容性——异常根
  **已定稿进 bootstrap**（M49，2026-08-01：`core.Exception`，
  IsOpen 可继承，与 Object/ValueType 层级根同列；具体异常子类归
  S10 stdlib，边界清单已同步））；
- P4a：pattern 分支降级为嵌套条件（§16.6 规则）；switch 表达式 →
  结果临时变量；
- P4b：`switch` 指令 + `switch-table` 资源（§19.4）+ `throw`（§16.9）；
- **验收**：常量表 switch 端到端 + pattern 降级 `LoweredDescribe`
  用例 + throw 用例。

### S7e try/catch/finally + seq（含 `return@`）

- P3：try/多 catch/finally(e) 绑定；seq 块（语句/表达式双形态）与
  `return@标签` 全链解析（P3 标签作用域）；`GuaranteesReturn` 覆盖
  try/catch 路径；
- P4a：`seq` 与 `return@` → 结构化 block + 结果临时变量 + `call`
  （§3.4）；
- P4b：`try` 指令 + `catch-table` 资源（§19.5）+ `call blk(...)`
  （§16.1）；
- **验收**：try/catch/finally(e) 端到端 + seq 表达式取值脱糖
  `LoweredDescribe` 用例。
- **完成注记（M50，2026-08-01）**：已落地。同批按用户决策把 cast
  （as/as?）最小闭环提前自 S8（「没法脱离 cast 实现其他功能」）——
  P3 定型（as 结果即目标类型、as? 结果 `Nullable\<T\>`，可转性不做
  静态拒绝：as 失败是运行时 core.CastException）+ P4b `cast`/
  `cast.safe` 发射（§12.1/§12.2）；S8 的 `is`/`typeOf`/`supers`/
  `with`/smart cast 仍归 S8 不动。

### S7f 剩余脱糖

`?.` 安全调用、`if?` 空值回退、解构声明、字符串插值（§3.4 逐条，
`LoweredDescribe` 每规则至少一用例）；using 物化不在本步（与 async
lowering 在 S13 汇合，ARCH §7）。

## S8 P3 完整化

范围：`is`/`supers`/`with` 与 `typeOf` 三 pass、smart cast 分析、
索引访问与实例成员完整化、重载解析（source-level ranking 唯一
落点）、默认参数填充、具名参数重排、访问控制检查、getter/setter
绑定（三类位置）、castTo/castFrom 名字分析、async 边界五项闸门
（SYNTAX §4.5：receiver / 参数 / TResult / 捕获 / 泛型实参，
分析侧；lowering 在 S13）。

2026-08-01 细化为六步，按序推进。沿用 S7 确立的推进原则：P3 与
P4 同步落地——P3 绑得出来的形态，同一步内 P4 必须能发射，端到端
`--emit-bil` 可验证（S8d/S8e/S8f 为纯 P3 步，无 P4 面，逐步注明）：

### S8a is / supers / with + typeOf 三 pass

- P3：`is`/`supers`/`with` 右侧双形态绑定——名字先按类型引用
  解析，失败再按值绑定（值必须是 `Type\<T>`，同名时类型优先）；
  `with` 类型引用必须 wrapper；`is`/`supers` 不做静态不可能性
  拒绝（SYNTAX §3.5 右侧解析规则随本步定稿落地）+ `typeOf`
  双形态——操作数先按值绑定（取运行时实际类型，返回
  `Type\<T静态\>`），无法绑为值且可解析为类型引用时取类型形态
  （SYNTAX §3.7 随本步定稿落地）；`is .Case`（enum 判别比较）
  归 S11，本步落 P3 归口诊断；
- P4a：恒等重写（无脱糖）；
- P4b 首次发射：BIL §12.3（`type.is`/`type.supers`/`type.with` +
  三 `.indirect` 形态）与 §12.5（`getid.var`/`getid.type`）；
- **验收**：is/supers/with 双形态与 typeOf 双形态端到端合法 BIL
  （含 `.indirect` 与 `getid` 发射用例）+ `is .Case` 归口诊断用例。

### S8b smart cast 分析 ✅（M56，2026-08-03）

> **已完成**：定稿（M55）→ SYNTAX §3.5 完整
> 规则 + §3.4 null 判等段落地；P3 收窄分析（FlowState 收窄表 +
> ConditionFactsExtractor + BoundSmartCastExpression 标记）与 P4a 物化
> （LoweredCastExpression）三 pass 全通；SmartCastTests 55 用例
> （注册表 #41）+ CLI 端到端三样例 BIL 核对。详见 PROGRESS_REPORT
> M56 段落。落地偏差（Q4 澄清：分支体内 `_` 不可用是 §7.2 既有语义，
> 覆盖 selector 可收窄场景；var 根允许——赋值失效覆盖；访问器判定
> 归 S8e 细化）已并入 SYNTAX §3.5 与 PROGRESS_REPORT M56 段落。

- ~~规范前置：SYNTAX §3.5 现仅一句话（「`is` 检查后在对应分支中
  自动转换类型」），动工前必须先专项定稿——分支语义、失效规则、
  null 收窄、与 `?.` / `if?` 的交互；enum case `is` 明确不触发
  smart cast；~~
- P3：只做分析与标记（ARCH §5.2），结果记录在 BoundTree；另含
  `if?` / `?.` 已落地形态与 smart cast 的统一性核查；
- P4a：显式 `cast` 物化（ARCH §6.1，复用 M51 EnsureDeclaredType
  模式）；P4b 复用 S7e `cast` 发射，零新增；
- **验收**：定稿规则逐条的 BoundTree 标记断言 + 物化 cast 的
  `LoweredDescribe` 用例 + 统一性核查结论落 PROGRESS_REPORT。

### S8c 索引访问 + 实例成员完整化 ✅（M59，2026-08-03）

> **已完成**（M59）：getAtIndex/setAtIndex 运算符绑定（读形态 Type =
> 返回类型、写形态 Type = 元素形参类型）+ 赋值/复合赋值 place 扩展 +
> 表达式底座链泛化（PathVisitors 重构，解开全部 S8 归口诊断——
> `(a+b).c`/`foo().c`/`new X().c`/`foo()?.bar`/段后缀折叠/`this[i]`/
> 容器末段成员后缀）；多参数索引 `a[i, j]` 定稿为编译错误（签名固定
> 单 TIndex 参数），具名索引实参与普通调用同规则（均已同步
> SYNTAX §13.2）；P4a LoweredIndexExpression 恒等降级；P4b §13.6
> get.array/set.array 发射 + BilVerifier 严格三元组查询（§6.4
> 精确匹配，禁止隐式转换）。详见 PROGRESS_REPORT M59 段落。

- P3 索引：`getAtIndex`/`setAtIndex` 运算符绑定（SYNTAX §13.2；
  `Binder.FindInstanceOperator`（:1409）模式可复用）+ 赋值 place
  扩展（`a[i] = x`）；多参数索引 `a[i, j]` 是否合法需本步定稿；
- P3 实例成员完整化：表达式底座路径绑定（`(a+b).c` / `foo().c` /
  `foo()?.bar` 等），解开 Binder.cs 现行全部 S8 归口诊断
  （:1610/:1652/:1665/:2102/:2177）；
- P4b：`get.array`/`set.array` 发射（BIL §13.6，验证器严格三元组
  查询——collection/index/result 精确匹配，无隐式转换）；
- **验收**：索引读写（含赋值 place）与表达式底座链端到端合法
  BIL + 多参数索引定稿结论同步 SYNTAX §13.2。

### S8d 重载解析 + 默认参数 + 具名参数（纯 P3，无 P4 面）✅（M60，2026-08-04）

> **已完成**（M60）：SYNTAX §4.2 重载解析规则定稿（三步：结构过滤 →
> 类型适用性 → 最具体胜出 + 默认值填充数平局打破；实例/ext 同池；
> 泛型/可变参数归口）；P3 新设施 `OverloadResolution`（静默结构映射
> TryMapArguments + 无目标类型实参预绑（null 字面量占位，落定以胜者
> 形参类型定型）+ IsApplicable/IsBetter + Materialize 规范序落定）；
> 默认参数三件套（ParameterSymbol.DefaultValue/IsVariadic/
> IsNamedVariadic + P1 填充 + P2 顺序检查）与声明点绑定（BindingDriver
> 阶段 1 + BindContext.IsDefaultValueContext 隔离形参与 this +
> BindEnvironment.ParameterDefaults 记忆化按需绑定——前向依赖
> `f(a = h())` 先于 h 声明经调用点查表递归触发，声明顺序不影响语义，
> in-flight 集合拦截依赖环）；调用/init/索引读三处接 Resolve（
> MatchSingleCandidate 删除）；同批修复位置实参静默覆盖具名占位
> （统一 Duplicate 诊断）。写模式索引 operator 重载仍归口（RHS 类型
> 在赋值侧才可知）。详见 PROGRESS_REPORT M60 段落。

- ~~规范前置：重载规则目前欠定，动工前先补 SYNTAX~~；
- ~~P3：source-level ranking 唯一落点（BIL §3.3——之后各层不再
  ranking）+ 默认参数填充 + 具名参数重排；`BoundCall` 必须已是
  规范参数序（ARCH §2），P4 不再重排~~；
- **验收**：ranking 规则逐条用例 + 默认/具名参数绑定的
  `BoundDescribe` 断言（规范参数序形态）——BinderTests 新
  TestDefaultParameters + TestOverloadResolution 两组达成。

### S8e 访问控制 + getter/setter + override 检查（纯 P3，无 P4 面）✅（M63，2026-08-04）

> **已完成**（M63，方案 A）：SYNTAX §16.1 可见性判定规则定稿
> （private 顶层=同文件/成员=声明类型及嵌套递归、protected=子类或
> 同包（同命名空间驻留实例）、internal 单编译单元恒可见、接口成员
> 默认 pub、bootstrap 硬编码符号统一 Public）+ §9.4.1 访问器绑定语义
> （修饰符白名单仅访问级别、可见性=显式 ?? 字段级别、backing 形态
> value 别名（getter 只读/setter 隐含 `backing = value`）、自动访问器
> 体合成、const+set 拒绝、带访问器字段不收窄）+ §9.2.1 override 配套
> （三标记仅普通成员方法、覆写目标存在且 open/abstract（接口成员天然
> 可覆写）、禁止静默隐藏、abstract 位置与体、具体类待实现成员、
> new abstract 拒绝）。符号六槽（MethodSymbol.IsOpen/IsAbstract/
> IsOverride/HasBody + FieldSymbol.Getter/Setter/HasBackingStorage）+
> SourceFile 文件身份；P1 访问器壳（不进容器 Methods 表——声明发射
> 由字段槽驱动）；P2 AccessChecker 共享设施 + 声明侧接入 +
> AccessorChecker/OverrideChecker 两新阶段（构造宿主签名 Substitute
> 代入——stdlib 双接口协议依赖）；P3 使用点检查（候选过滤先于
> ranking）+ 访问器读写检查与体绑定（Bound 节点形态不变——BIL
> get.field/set.field 承载）+ 局部访问器归口 S11；P4 声明段小开闸
> （BIL §8.3/§8.4 已定稿形态：getter(FIELD)/setter(FIELD)/backing/
> computed/override/abstract 投影）+ BilVerifier §21.8 增补。
> 落地偏差：① 局部 var/const 访问器归 S11（需闭包抬升，超体量）；
> ② 接口默认实现隐式继承（§11 显式委托语法归后续）；③ 带访问器
> 字段必须显式类型标注（与无标注字段类型推断不共存）；④ ext 字段 +
> 访问器路径已通无端到端样例。详见 PROGRESS_REPORT M63 段落。

- ~~P3 访问控制检查（使用点，SYNTAX §16；符号 Accessibility 已于
  M43 写入，Binder.cs:2242/:2276 注释明示归本步）~~；
- ~~P3 getter/setter 绑定（三类位置，SYNTAX §9.4；
  `MethodSymbol.Kind` Getter/Setter 已备）~~；
- ~~顺带落地 §9.2 修饰符表 `override` 行的配套检查规则~~；
- **验收**：各级可见性越界诊断用例 + 三类位置 getter/setter
  绑定用例 + `override` 配套检查用例——BinderTests.Access 新
  TestAccessControl/TestAccessors/TestOverride 三组达成（局部位置
  归口 S11，见上偏差①）。

### S8f castTo/castFrom 名字分析 + async 边界五项闸门（纯 P3，无 P4 面）✅（M66，2026-08-05）

> **已完成**（M66）：SYNTAX §3.5 转换优先级 + §4.5 五项闸门落地——
> P1 `MethodSymbol.IsAsync`；P2 新 `ConversionOperatorChecker`
> （castTo/castFrom 声明形状：零参数/恰一参数/必声明返回类型）与
> 新 `AsyncGateChecker`（声明侧闸门 2 参数/3 返回值/5 泛型约束边界
> 共享安全 + async 仅函数收口——init/operator/类型声明拒绝，置于
> GenericConstraintChecker 后）；P3 `BoundCastExpression.Conversion`
> 槽 + `SymbolLookup.FindConversionOperator`（单泛型参数代入签名
> 匹配，宿主泛型按不适用回退）+ CastVisitor 三级转换优先级（源
> castTo → 目标 castFrom → 内建，as? 同分析）+ 新 `Binding/AsyncGates.cs`
> 调用点闸门 1/2（BoundTree 后置遍历单落点）+ 新 `LambdaVisitor`
> 闸门 4（async lambda 捕获 AST 级扫描）。落地偏差（技术债 #23）：
> ① 转换分析只记录不重写（P4 仍发 cast，运行时按 §12.1 分派）；
> ② castFrom 调用形态（实例 operator 无目标实例的 receiver 语义）
> 归 lowering/运行时定稿；③ 多泛型参数/宿主泛型参数按不适用回退；
> ④ 闸门 5 调用点实际实参检查归 S9；⑤ lambda 捕获扫描为粗粒度
> （体内声明名全量排除，先引用后声明形态保守漏报）；⑥ async
> lambda 自身形参/返回类型随 lambda 绑定（S13）落查；⑦ 闸门 2
> 调用点检查为防御性兜底（静态不可达违反——shared 单向传染保证）。
> 详见 PROGRESS_REPORT M66 段落。

- P3 castTo/castFrom 名字分析（SYNTAX §3.5 转换优先级：源类型
  `castTo` → 目标类型 `castFrom`；BIL §12.1 语义第 1、2 条）；
- P3 async 边界五项闸门（SYNTAX §4.5：receiver / 参数 / TResult /
  捕获 / 泛型实参，仅分析侧；lowering 归 S13）；
- **验收**：castTo/castFrom 优先级与失败诊断用例 + 五项闸门
  逐项违反诊断用例。

> 边界注记（M50）：`as`/`as?` 最小闭环已提前至 S7e 落地（P3 定型 +
> cast/cast.safe 发射）；本里程碑剩余的 cast 相关工作为 smart cast
> 分析与 castTo/castFrom 名字分析。

## S9 泛型（✅ 2026-08-05 M67–M73 全部落地，S9 收官）

reified 泛型全链：使用侧约束检查、构造类型驻留完善、
`.generic.*` hidden args 物化（BIL §7 规范签名与参数序）、
`.generic<...>` 类型引用发射、泛型 new/调用。

2026-08-05 细化为六步（S9a–S9f），按序推进。沿用 S7/S8 确立的推进
原则：P3 与 P4 同步落地——P3 绑得出来的形态，同一步内 P4 必须能发射，
端到端 `--emit-bil` 可验证（S9a–S9d 为纯 P3 步，无 P4 面，逐步注明；
hidden args 物化集中落 S9e）。

**已定稿的规范前提**（M67，2026-08-05，全部落 SYNTAX §3.6/§4.2/§4.3）：
① 固定泛型参数**必须显式实参**（零推导，Latte 一切显式哲学）；
② 带显式实参 → 候选池仅泛型方法（按泛型实参个数匹配过滤）；不带 →
泛型方法不参与候选，仅剩泛型候选时诊断「需要显式泛型实参」；
③ 泛型 operator 按名字调用同规则，运算符位置（`a + b`）不参与；
④ 使用侧约束满足判定：extends = 实参 <: 边界、supers = 反向、
with = 查类型 `AppliedWrappers`（P2 已登记，含 interface 传染），
边界含未替换泛型参数时跳过检查；⑤ 泛型可变参数（`TArgs...`/
`named TArgs...`）的类型实参由对应值实参的类型**推导**（包固有形态，
不显式书写；与①不冲突——①仅限固定泛型参数）。

### S9a 泛型参数函数体内放行（纯 P3，无 P4 面）

解开全部「使用侧泛型归口 S9」gate，使泛型函数体（形参/局部声明/
返回类型为 `GenericParameterSymbol`）可完整绑定：

- `Semantic/Binding/` 16 处 gate 逐一解开（现状清单见 M67 细化注记）：
  PathVisitors（:61 泛型实参、:168 形参引用、:309/:686 字段读取后、
  :571/:602/:618 索引运算符签名）、TypeReferences（:26 函数体内类型
  引用）、CallVisitors（:80/:230 调用返回类型）、TypeCheckVisitors
  （:79 is/supers/with 静态目标、:217 typeOf 类型形态）、
  DeclarationVisitors（:113 解构分量、:337 return 兼容判定）、
  LoopVisitors（:154 范围 for、:241/:254 for-each 元素）、
  BindingDriver（:125 参数默认值、:166 must-return 级联跳过撤销）；
- 类型参数在表达式/类型位置按引用相等身份使用（定义级签名发射
  `.generic<$.generic.T>` 已就绪，S9a 零 P4 新增）；
- 修复语句位置泛型调用静默丢实参漏洞：`ExpressionStatementVisitor`
  → `CallForm.TryGet` 不查 `GenericArguments`，`foo\<i32>(1);` 静默
  丢弃 `<i32>` 正常绑定——补检查并归口（同 PathVisitors 诊断）；
- **验收**：BinderTests 新组（`func gf\<T>(x: T): T { return x }` 全
  放行形态、`var y: T`、`x is T`、字段读取后泛型类型、泛型返回调用
  链）+ BoundDescribe 断言 + 语句位置泛型调用诊断用例。

### S9b 泛型调用绑定（纯 P3，无 P4 面）

SYNTAX §4.2 定稿规则落地：

- OverloadResolution 泛型路径：显式泛型实参解析（复用 NameResolver
  类型引用解析，含毒化传播）→ 形参/返回类型 `Substitute` 代入 →
  三步 ranking（结构过滤/类型适用性/最具体胜出）沿用 → 胜者
  Materialize；候选池按①—③规则过滤；
- `BoundCall` 携带泛型实参列表（新增槽或节点形态，S9e P4 透传）；
- **使用侧约束检查共享设施**（ConstraintChecker 或等价：extends/
  supers/with，SYNTAX §3.6 ④）——覆盖三处实例化点：类型引用
  （`var x: Box\<i32>`，实参须满足声明约束）、泛型调用实参、泛型
  new 实参；失败诊断定位到实参；
- **验收**：BinderTests TestGenericCalls 新组（显式实参调用/个数
  不匹配/需要显式实参/代入后 ranking/约束违反）+ OverloadResolution
  泛型用例。

### S9c 泛型 new（纯 P3，无 P4 面）

- NewVisitor 补 `ConstructedFrom` 回退：构造类型在定义级查 init
  （修复 `new Box\<i32>(1)` 误报 `Type 'Box' has no constructor`——
  构造类型成员表恒空，:374 未回退）；
- init 形参类型 Substitute 代入 + M60 重载解析规则复用（构造类型
  目标的 init 按代入后签名参与解析）；泛型定义不可构造诊断保留；
- 泛型 new 实参的使用侧约束检查（S9b 设施复用）；
- **验收**：BinderTests 泛型 new 用例（带参/零参/init 代入/无匹配
  init 诊断）+ 端到端路径打通（P4 恒等透传，发射形态归 S9e）。

### S9d 泛型可变参数（纯 P3，无 P4 面）

SYNTAX §4.3 定稿规则落地：

- `TArgs...`/`named TArgs...` 调用绑定：类型实参由对应值实参的
  类型推导（⑤；位置包 ← 位置实参、具名包 ← 具名值实参），逐实参
  做约束检查；包语义在 Bound 层以 `.generic.TArgs` 形态表达
  （array\<typeid\>/map\<string, typeid\> 的抽象值）；
- 与值可变参数（`.vargs.args`/`.kwargs.args`）成对出现的组合形态
  定稿核对（BIL §7.2 顺序：固定泛型 → 泛型可变包 → 普通参数 →
  值可变包）；
- 调用点值实参的类型化与归位（具名包 = 实参名 + 类型对）；
- **验收**：BinderTests 泛型可变参数用例（位置/具名/约束违反/
  与普通参数混合）。

### S9e hidden args 物化（P4a + P4b）

BIL §7 落地，泛型端到端出合法 BIL：

- P4a：LoweredCall/LoweredNew 透传泛型实参（恒等重写，无脱糖）；
- P4b fn 定义：EmittingDriver `.args` 按 §7.2 规范序插入
  `.generic.T = .typeid`（固定泛型按声明序 → `.generic.TArgs`
  array/map 包）——.return 在前、.this 次之（§9.2/§7.3），普通
  参数随后；泛型类型定义 `.type` 声明补 `generic(...)` 子句
  （`Bil/BilSymbols.cs` :143 占位兑现）；
- P4b 调用点：invoke 实参前置泛型实参——静态实参 `getid.type
  type(...)` 物化（§12.5 已发射过）、嵌套泛型调用转发 `$.generic.T`
  引用、可变包以 `.array`/`.map` 资源构造 + 逐项 getid.type；
  new 指令形态核对（§14.1 第一操作数已含类型实参，Middleware 据
  其取 typeid，勿重复物化隐藏实参）；
- BilVerifier：保持 `.generic<` 降级（M58 防误报优先），§21.7
  `.generic.*` 参数序检查已就位零改动；
- **验收**：BilEmitterTests 端到端（泛型函数调用/泛型 new/泛型
  函数体内局部与运算/嵌套泛型调用转发）+ 黄金形状断言。

### S9f stdlib 泛型化 + 技术债勾销

- `stdlib/core/collections.latte` 泛型化：`RangeEnumerator\<T\>`/
  `Range\<T\>`（勾销技术债 #15④；S7c-2 起 RangeEnumeratorI32 具体
  形态替身退役），for 范围循环走泛型路径端到端；
- 技术债勾销：#18② is/supers/with 动态形态值路径带泛型实参；
  #22⑥ 构造宿主覆写签名比对的泛型精确性（M63 归口）；#23③ 转换
  运算符多泛型参数/宿主泛型参数（按不适用回退判定复核）；#23④
  async 闸门 5 调用点实际实参检查（S9b 后泛型实参静态可知，接
  `AsyncGates` 调用点 1/2 同落点）；
- SemanticsFuzzTests 泛型形态更新（S9 归口 → 新诊断/成功路径）；
- **验收**：43 套件 + fuzz 6000 + 语义 fuzz 3000 全绿 + CLI
  `--emit-bil` 端到端样例核对。

### S9g 型变检查与构造类型赋值（✅ 2026-08-07，M96）

- P1/P2 将 `GenericVariance` 写入 `GenericParameterSymbol`；`out`/`in` 只允许
  类型声明泛型参数，函数、方法和 operator 泛型参数拒绝型变；
- 新 `VarianceChecker` 检查字段读写、getter/setter、方法参数/返回值以及嵌套泛型
  实参的协变/逆变位置；可变字段和 invariant 容器将型变参数收紧为 invariant；
- P3 `SymbolLookup.IsAssignable` 对同一泛型定义的构造类型按 `out`/`in` 方向递归
  比较实参，未标注型变保持严格相等，并沿基类/interface 代入路径消费该规则；
- 验收：声明侧非法位置、getter/setter 读写方向、Producer/Consumer 构造类型赋值
  与反向拒绝用例。

## S10 core.latte 载入机制 ✅（2026-08-05 M74 落地）

> **已完成**（M74）：载入机制本体早已在 S6 落地（EmbeddedResource 内嵌 +
> 编译单元注入 + `native` 语法）；本里程碑完成 stdlib 文件扩充与
> bootstrap/core.latte 边界定稿。用户决策四件套：① 类型名唯一性按
> 「名 + 泛型元数」判定（`Task` 与 `Task\<TResult\>` 同名共存，对齐 C#
> 先例，落 P1 重复检测 + NameResolver 查找分流）；② coroutine 运行时面
> 以 Latte 自举声明 + 最小 native API（`sleep`/`PollingAlarm.isReady`），
> native 返回类型放宽至用户引用类型（§4.6 修订，FFI ABI 归 Middleware）；
> ③ 异常子类清单（RuntimeException/IOException/CastException/
> NoSuchMethodException）+ message 挂根（bootstrap Exception 程序化携带
> protected message + pub native getMessage）+ 子类自持 init + toString
> 不覆写；④ P3 async 调用返回类型改写提前落地（SYNTAX §4.5 表兑现——
> 调用点类型 = Task\<T\>/Task，await 仍归 S13）。P4b 同步：async 方法声明
> 发射 §8.4 async 修饰符 + 语句位置 async 调用发 invoke 而非 invoke.noret
> （§15.2 fire-and-forget）+ BilVerifier 预定义符号表补 getMessage/message
> + async invoke 结果形态校验（§21.3）。边界定稿落 SYNTAX §15.3/§8.1。
> 兼作前端常驻回归（StdlibSourcesTests 六源结构断言）。

core 声明文件随编译器载入（自举解析 → 同一条 P1/P2 路径）、
bootstrap 与 core.latte 边界定稿。**载入机制本身已提前至 S6 落地**
（EmbeddedResource 内嵌 + 编译单元注入，含 `native` 函数语法与
`core.io::Console` 最小文件）；本里程碑剩余工作为 stdlib 文件扩充
（`core.coroutine::Task`/`Executor` 家族、异常具体子类（异常根
`core.Exception` 已于 S7d/M49 定稿进 bootstrap）、`IDisposable` 等）
与 bootstrap/core.latte 边界定稿。兼作前端常驻回归测试。

## S11 wrapper / extension / enum struct

wrapper 静态组合链 lowering（specific/wildcard proxy、`call???` 降级、
`.wrapper.` 隐藏字段，BIL §5.3/§15.4）、ext 成员调用（`.this` receiver）、
enum case（`new.case`、`is .Case` 判别比较、判别值分配）、
派发链诊断工具（RUNTIME §15，编译器必备功能而非事后补充）。

> **规范定稿（M75，2026-08-05）**：S11 的 BIL 侧三处定稿已落地
> （`BIL_STANDARD.md`，ARCHITECTURE §7.1 缺口已兑现）——① §12.3 增补
> `type.is.case`（enum 判别比较，RUNTIME §16.3 承载：非子类型检查、
> VALUE 严格等于 case 的 enum 类型、case 必须带完整 enum 前缀、结果
> .bool、判别宽度 u16/u32 为布局内部细节）；② §12.4 修订 + §13.3 增补
> 嵌套字段 place 形态（当时读/写共用一条嵌套链指令，**后来读侧统一为
> `get.wrapper`/`get.wrapper.field` 值拷贝 + 普通 `get.field`，写侧保留
> `set.wrapper.field`**；`get.wrapper` 保留为 lowering/VM 内部能力；
> `obj:W = ...` 仍是源码层编译错误，无整体写回指令）；③ §8.5/§19.1
> 判别值注记（整数标量资源、非负唯一、auto 按声明序从 0、宽度按
> RUNTIME §16.1）。BIL 模型（`IsCaseInstruction`/`SetWrapperFieldInstruction`
> 等）与 BilVerifier §21.3 校验同步落地（BilWriterTests 黄金 +
> BilVerifierTests 手工模块正负例）。后续施工
> 按序推进：~~P3 wrapper place 绑定与只读禁令（解 PathVisitors 两处
> Colon 归口 + 全拦截面）~~（**✅ M79 已落地**）→ ~~enum case 全链~~
> （**✅ M77 已落地**）→ ~~ext 收尾~~（**✅ M80 已落地**）→ 局部访问器
> 解归口（**用户决策 2026-08-06：路线 C——随 S13 lambda 闭包机制落地，
> 移出 S11 序列**，捕获语义随之开放；决策由 PROGRESS_REPORT 技术债
> #22① 承载，M83 起 HANDOVER.md 按约定删除）→ proxy 烘焙 lowering
> 与派发链诊断工具（**M81 已细化为 S11a–S11g，见下**）。
>
> **S11 proxy 烘焙细化（M81，2026-08-06，纯文档里程碑）**：proxy
> 烘焙 lowering 与派发链诊断工具细化为 S11a–S11g 七子步，规范定稿
> 随批落地（SYNTAX §4.4/§14.2/§16.1 + BIL §8.4 + RUNTIME §14/§15）。
> **烘焙形态定稿（用户决策 2026-08-06）**：① 声明侧烘焙——wrapper
> 逻辑编译期进入被修饰成员的方法体、骑 vtable（RUNTIME §14 字面
> 语义），调用点零改动；② proxy 特化体为带 `wrapper-proxy(PROXY_KIND)`
> 修饰符的独立合成 fn（BIL §8.4），编译器不做文本内联，最终内联归
> Middleware；③ 特化按 (proxy × 目标成员) 组合在 **P2** 合成符号
> （Freeze 前），P3 对 proxy 声明体**逐组合绑定**（语境：宿主类型
> 代入 wrapper 泛型参数 + `inner` = 下一环符号 + proxy 体内 `this`
> 重写为 BoundWrapperAccessExpression），P4 不承载 wrapper 语义——
> 烘焙产物在 BIL 层即普通 fn 与 invoke 链。
>
> - **S11a（P2 形状校验与符号合成）**：proxy 成员形状校验
>   （specific 四类与 wildcard 四类按 SYNTAX §14.2 定稿的
>   canonical shape 与目标成员全等判定；@WrapperTarget 类别 ×
>   proxy 类别匹配矩阵；Entity wrapper 至多一泛型参数，恰一 =
>   TTarget 角色）；wrapper 应用实参登记（AppliedWrappers 元素
>   升级为携带 init 实参的记录，宿主构造安装用）；`.wrapper.` 隐藏
>   字段 FieldSymbol 合成（挂宿主类型，interface 传染落到
>   实现者）；派发链计算（被修饰成员 → outer→inner
>   [(wrapper 应用, 命中 specific|wildcard proxy 符号)]）+
>   逐组合特化 MethodSymbol 与原始体符号合成（wrapper 泛型
>   代入在此完成，M79 遗留「TTarget 显形」落地）。
>   **验收**：DeclarationResolverTests 形状负例 +
>   CanonicalSymbolPrinter 符号黄金。（**✅ M82 已落地**，
>   2026-08-06，PROGRESS_REPORT 详录——48 新用例；落地修订：
>   Entity wrapper 泛型元数由「恰一」放宽为「至多一」（纯状态
>   wrapper 与既有 fixture 兼容，`self` 仅在恰一时可用）；
>   「必须实现 get/call」不强制执行（§14.5 纯状态用法合法）；
>   暂缓项：Value/Method wrapper 链、无访问器字段拦截、
>   interface 实现者链继承/override 链、合成符号的 BIL 发射
>   由 LocalSymbolEmitters/EmittingDriver 闸门跳过归 S11c/S11d）
> - **S11b（P3 proxy 体绑定）**：`self` = 宿主角色的 this
>   （类型 = TTarget 代入结果）；`inner` 绑定为对下一环符号的
>   普通调用（下一环 = 内层特化或原始体符号；wildcard 最内环 =
>   解包 shim）；proxy 体内 `this` 重写为
>   BoundWrapperAccessExpression（与使用点 `obj:W` 同构，
>   BIL §13.3）；转发壳（被修饰成员原名 fn 的 body：invoke
>   最外层特化）与 wildcard 解包 shim 的 BoundFunctionBody
>   合成；proxy 体诊断按 (proxy, span, message) 去重。
>   **验收**：BinderTests self/inner/this 形态与负例。
>   （**✅ M83 已落地**，2026-08-06，PROGRESS_REPORT 详录——
>   31 新用例；落地形态：BindingDriver 阶段 2 分流 + 阶段 2.5
>   三件套（转发壳/特化体/解包 shim），wildcard 解包 shim 符号
>   随 S11a 同批 P2 合成（`.proxy.unwrap.<序>.<键>` 双包参）；
>   落地注记：① wildcard 前奏的包类型 `Array\<Any\>` 是烘焙链
>   内部约定（§14.8 名值对 ABI 归 call??? 的 S11e）；② 可变
>   参数成员不拦截建链（包展开 Bound 层无表达，技术债 #27⑦）；
>   ③ proxy 声明泛型参数的体内类型引用代入暂缓（GenericSubstitution
>   槽预留，#27⑧ 归本路线图的 S11g 复核）；④ P4 侧 LoweringDriver/
>   EmittingDriver 跳过合成 fn 与转发壳——`--emit-bil` 对含链源码
>   由 §21.2 拦截不落盘（S11d 开闸解除））
> - **S11c（P4a/P4b wrapper place 成员访问，解 M79 归口）**：
>   BoundWrapperAccessExpression 作 receiver——成员读 =
>   get.wrapper 值拷贝 + get.field、成员写 = set.wrapper.field、
>   方法调用 receiver = get.wrapper 值拷贝（BIL §12.4 注记/
>   §13.3）；使用点与 proxy 体内共用同一 lowering 路径。
>   **验收**：M79 P4 归口用例转正 + `obj:W` 读/写/调用三形态端到端。
>   （**✅ M84 已落地**，2026-08-06，PROGRESS_REPORT 详录——37 新用例；
>   落地形态：P3 节点携带命中应用记录（Application 槽）+ 新设施
>   `WrapperPlaceLowering` 按应用类别分派；复合赋值读写分离 + 宿主
>   单次求值共享；**读侧后统一为** Materialize + 普通 get.field，
>   写侧 `set.wrapper.field`；落地注记：深层写穿（`place.a.b`）/索引写/
>   字段-Value 调用与索引/局部与静态存储合成显式归口，归 S11g 复核。
>   **M91 已部分消解**：字段-Value 方法调用/索引读经 `get.wrapper.field`
>   值拷贝；字段应用寻址复用 `field(HOST_FIELD),wrapper(W)`；深层纯字段
>   写穿由 P4a 展开为正向 get + 叶写 + 按值类型边界反向 set，不新增
>   专用 opcode。索引写与局部/静态存储继续显式归口）
> - **S11d（P4b 合成 fn 发射，烘焙端到端）**：Bil 模型增补
>   `wrapper-proxy(PROXY_KIND)` 修饰符（PROXY_KIND 取值定稿
>   BIL §8.4）；特化 fn / 原始体 fn / 转发壳平铺发射；
>   BilVerifier 适配（合成保留名放行、修饰符校验）。
>   **验收**：specific 与 wildcard 声明侧烘焙端到端出合法
>   BIL（invoke 原名 → 特化链 → 原始体）。（**✅ M85 已落地**，
>   2026-08-06，PROGRESS_REPORT 详录——33 新用例；落地形态：
>   `BilProxyKind` 四态 + `BilWrapperProxyModifier`；
>   LocalSymbolEmitters 闸门改分流（烘焙产物发射、proxy 声明
>   模板不进 BIL）+ 修饰符投影（specific/wildcard/original）；
>   双驱动闸门删除（特化/原始体/转发壳/解包 shim 平铺）；
>   BilVerifier §21.8（保留名 ↔ 修饰符双向校验 + kind ↔ 名段
>   一致）；同批修复 wildcard 具名包 ABI 类型不符（§14.7
>   `Array\<Pair\<String, Any\>\>` 两处同改）；get 访问器链
>   同批端到端）
> - **S11e（`call???` 降级全链，SYNTAX §14.7 + BIL §15.4）**：
>   P3 使用点降级判定（静态类型未声明方法 + wrapper 链存
>   `.proxy.*`）与胖值 ABI 打包（实参装箱 + canonical symbol
>   资源 + 泛型 typeid 包）；call??? router 合成 fn（按 symbol
>   路由到 proxy 特化）；bootstrap `Any.call???` 默认实现
>   （抛 NoSuchMethodException）；返回值调用点转换（不符抛
>   CastException）。**验收**：未声明方法降级端到端样例。
>   （**✅ M86 已落地**，2026-08-06，PROGRESS_REPORT 详录——
>   69 新用例；落地修订：泛型逻辑签名实质化为**非泛型胖值
>   签名** `(symbol: String, namedArgs: Array\<Pair\<String,
>   Any\>\>, unnamedArgs: Array\<Any\>): Any`（三合成符号统一，
>   独立泛型 typeid 包取消——Any 胖值自描述 typeid；结构性
>   必然：双泛型包/双值包/包整体转发无 Bound 层表达）；
>   落地形态：router = 宿主成员 `call???`（wrapper-proxy(
>   router)）+ 逐应用降级特化 `.proxy.<序>.???`（零前奏形参
>   直通）+ Any.call??? 体合成 throw NoSuchMethodException +
>   inner 自动补 symbol（合成具名实参）+ 类型兼容豁免五位置
>   骑 §6.5 cast 物化（Any→T 不符抛 CastException）；请求
>   symbol 格式定稿 SYNTAX §14.8 末段；遗留五项登记技术债
>   #28，归 S11g 复核；**M89 已收口 #28③④**：降级资格只读遍历
>   receiver/BaseType/Interfaces 传递闭包；if?/throw/复合赋值/索引写
>   位置补 P3 豁免与 P4a §6.5 cast 物化；**M92 已收口 #28①**：显式
>   泛型实参按 canonical 类型引用编码在降级请求方法名后的 `<...>` 段，
>   P3 复用使用点解析/访问检查，三参胖值 ABI 不变。#28② 维持 SYNTAX
>   §14.7 既定错误行为）
> - **S11f（派发链诊断工具，RUNTIME §15）**：CLI 子命令
>   `compile --file a.latte --explain-dispatch`（用户决策
>   形态）；报告编译单元全部烘焙链（被修饰成员 outer→inner
>   每层命中 specific|wildcard + canonical symbol）与降级路由
>   （存 `.proxy.*` 的类型）；调用点级过滤留扩展。数据源 =
>   S11a 符号产物 + CanonicalSymbolPrinter（ARCH §4.4）。
>   （**✅ M87 已落地**，2026-08-06，PROGRESS_REPORT 详录——
>   新套件 20 + CLI 互斥 4；落地形态：`Semantic/DispatchExplainer.cs`
>   按类型 canonical 名 Ordinal 分组输出 applied/member 链/
>   downgrade 段，无产物明示 `(no dispatch chains)`；CLI 与
>   `--parse-only`/`--emit-bil`/`--sema-only` 互斥，P1–P3 后写
>   stdout）
> - **S11g（复核收尾）**：M79 遗留复核（泛型参数 receiver 的
>   with 约束 place `param:W`；泛型 wrapper 实参代入 S11a
>   落地后回归）+ 技术债 #26 代码落地（ext 泛型目标元数/歧义
>   诊断 + priv/protected ext 可见性按声明位置修订）+ #27⑦⑧
>   （可变参数成员链与 inner/转发壳泛型包转发、proxy 声明泛型
>   参数的体内类型引用代入）+ 规范交叉引用清理。**M90 已收口
>   #27⑦**：Bound/Lowered 显式携带可变泛型包，P4b 按声明序前置
>   `.generic.<Pack>` 到 `invoke fn(..inner)` 值实参列表；可变成员参与 proxy
>   匹配，解包与烘焙仍归 Middleware。**M91 已收口 M84 两项**：
>   字段-Value 调用/索引读与深层纯字段写穿落地；局部/静态存储按
>   用户裁决等待 `.args/.vars` 应用标记 + init 实参 ABI，保持 P4 诊断。
>   **M92 已收口 #28①**：降级调用的显式泛型实参进入 symbol `<...>` 段；
>   #28② 按既定规范维持错误行为。
>
> **wrapper place 绑定与只读禁令（M79，2026-08-06，PROGRESS_REPORT
> 详录）**：`BoundWrapperAccessExpression`（Receiver + Wrapper，Type =
> Wrapper 定义——只作成员访问接收者，永不作路径绑定结果产出）+
> BindWrapperSegment 双源同池查找（字段/局部符号 AppliedWrappers +
> 宿主类型 AppliedWrappers 构造回退定义）+ 只读禁令全拦截面（链末无
> 后缀 Colon 段按赋值/取值一处收口全部逃逸路径）+ 容器路径 Colon
> 切分（`Type.staticField:W`）+ 局部变量 wrapper 应用 P3 登记
> （LocalSymbol.AppliedWrappers，矩阵 C 恒合法）+ P4 显式归口（发射
> 归 proxy 烘焙）。**遗留**：泛型参数 receiver 的 with 约束 place
> （`param:W`）与泛型 wrapper 实参代入（TTarget 显形）归 proxy 烘焙
> 复核。
>
> **ext 收尾（M80，2026-08-06，PROGRESS_REPORT 详录）**：P4b 修复——
> `EmitBuiltinExtMembers` 随迁访问器声明（内建 ext 字段 + 访问器此前
> 被 §21.2 拒绝落盘，SYNTAX §4.4 示例形态实测复现）；P2 两闸门——
> ext 字段禁注 interface（§11 成员禁令 ext 路径收口）+ ext 实例字段
> 同受 §3.1.1 闭包表（`FieldClosureChecker.CheckExtensionField`）；
> 端到端样例五组勾销技术债 #22④；priv/protected ext 可见性、ext
> static 明文、ext 泛型目标登记技术债 #26 待裁决。
>
> **enum case 全链（M77，2026-08-05，PROGRESS_REPORT 详录）**：
> EnumCaseSymbol 家族（Owner/Discriminant + ResolvedInit/HoleParameters
> 模板槽，P3 声明点落定）+ TypeSymbol.Cases + PrintCase；P2
> EnumCaseResolver（洞独占性/case 名复核/判别值落定）；P3 声明点
> 模板绑定（init 选择 + 固定实参绑定 + 洞 pub 规则；泛型 enum 归口）+
> BoundEnumCaseExpression/BoundTypeCheckExpression.IsCase + 使用侧三形态
> （裸 `.Case`/`.Case(args)`/`is .Case`）；P4 `.case` 声明（洞签名 +
> 判别值 res/auto）+ new.case/type.is.case 发射——端到端出合法 BIL。
> 同批裁决落地 §9.3 init 映射赋值合成（无体 init 产 fn 定义 + 有体
> 前插，stdlib Pair 潜伏 bug 同愈）。**遗留**：泛型 enum case 归口、
> switch 值匹配位置保持常量限定（固定 case 不作值匹配常量）。

wrapper 值语义落地要点（2026-07-29 规范修订）：wrapper 是 rich struct
值而非对象，`obj:Wrapper` 与 proxy 体内 `this` 都是宿主隐藏字段的
**原地访问**。`obj:Wrapper` 是只读 place：P3 需在此拒绝整体赋值与
整体取值（作实参/返回值/推断源皆非法，SYNTAX §14.5）。BIL §12.4 目前
只有值语义的 `get.wrapper`，缺只读 place 形态——本里程碑同时定稿并
补入 BIL 指令（见 ARCHITECTURE §7.1）。

## S12 BIL verifier

`Bil/BilVerifier.cs`：BIL §21 全部检查（词法/符号/类型/definite
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

`Bil/BilVm.cs`：BIL §22 抽象值语义解释器。落地后新增执行断言
测试形态（跑出结果/异常与预期比对），并持续验证 §21.9
「VM 可执行性」。native 调用经 §22.5 内建 hook 表执行
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
