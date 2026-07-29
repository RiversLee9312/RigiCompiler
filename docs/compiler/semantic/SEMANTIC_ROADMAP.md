# Latte 语义分析与 BIL 生成路线图

> **用途**: 中端（AST → BIL）的里程碑计划。架构定稿见同目录
> `SEMANTIC_ARCHITECTURE.md`；本文档只管「计划」，进度现状一律记
> `docs/PROGRESS_REPORT.md`（落地时在其里程碑历史领取全局 M 编号）。
>
> 编号 S0–S14 是**计划序号**，近细远粗：S0–S6 已细化到验收标准，
> S7 以后随进展再细化。允许并行的地方已注明；未注明的按序推进，不跳步。

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

- `Semantic/Diagnostics.cs`：`Diagnostic { Severity, Span?, Message, Phase }`
  + `DiagnosticBag`（见 ARCHITECTURE §8）。
- `TestHarness` 扩展：`CheckSemanticError(label, action/bag, msgPart)`
  一类断言；诊断按消息子串 + Span 断言。
- **验收**：独立测试套件（DiagnosticBag 累积、多错不互断、
  Error 门槛判定）注册进 `TestRunner`。

## S1 符号图内核 + bootstrap

- `Semantic/Symbols/`：`SemanticSymbol` 家族最小集
  （Namespace/Type/Field/Method/Parameter/GenericParameter）、
  驻留 cache（含构造泛型类型驻留）、`BootstrapSymbols`
  （ARCHITECTURE §4.3 硬编码清单：根类型（含 `Wrapper`）+
  SYNTAX §3.2 基本类型 + Nullable/Box/Span 特权关系 +
  基元运算符键空间）。
- `CanonicalSymbolPrinter`：符号图 → BIL §5.2 canonical 字符串。
- **验收**：驻留断言（同一引用）、bootstrap 层级断言
  （`i32 <: ValueType <: Any`、`String <: ValueType`、
  `Wrapper <: ValueType`、`Box\<T> <: Object` 等）、
  canonical 打印串对照 BIL §5.2/§8.1 的示例逐条比对。

## S2 P1 声明收集

- `DeclarationCollector`：遍历编译单元全部 `RootASTNode` 声明骨架
  （不进函数体），建立命名空间/类型/成员/全局符号壳；重复声明诊断。
- 编译单元模型落地（多文件一次收集，ARCHITECTURE §3）。
- **验收**：跨文件前向引用可收集；同名冲突出诊断且不中断；
  嵌套类型路径正确。

## S3 P2 声明解析

- `DeclarationResolver`，子任务按序：
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
- P2 结束冻结符号图。
- **验收**：每个子任务独立测试组；SYNTAX §3.1.1 闭包表与 §14.9 目标
  矩阵逐行有用例（合法 + 非法各一）。

## S4 BIL 对象模型 + BilWriter（可与 S1–S3 并行）

- `Bil/BilModel.cs`：Module/Metadata/Resources/LocalSymbols/
  ExternalSymbols/Function/`.args`/`.vars`/Block/指令集/资源类型，
  覆盖 BIL_STANDARD §4–§18 的标准形式（协程指令 §17 暂缓——
  见 S13 与 ARCHITECTURE §7 待修订清单）。
- `Bil/BilWriter.cs`：模型 → 标准 BIL 文本（只输出标准 spelling，
  不输出 legacy）。
- 对中端**零依赖**（`Origin` 字段类型此时可先以 `object?` 占位，
  S6 接通后改为 `LoweredNode?`——或从一开始就放 `Lowering` 侧扩展，
  实现时按简洁三问定）。
- **验收**：手工构造 BIL §19 完整示例的内存模型，`BilWriter` 输出与
  规范文本逐行一致（黄金文件）。

## S5 P3 最小闭环（BoundTree 起步）

- `Semantic/Bound/` 最小节点集 + `Binder`：字面量、局部变量声明与
  引用、算术/比较二元运算（bootstrap intrinsic 键查询）、赋值、
  块与作用域链、`var` 类型推断、无重载的直接函数调用、`new`、
  `return` + 所有路径显式返回检查、definite assignment 最小版。
- `BoundDescribe` + 测试套件。
- **验收**：上述每类表达式/语句的 bound 形态与定型类型断言；
  类型不匹配/未定义名字/未赋值使用三类诊断各有用例。

## S6 P4 最小闭环（端到端 hello world）

- `Lowering/`：`LoweredNode` 最小集 + `Lowerer`（此阶段近乎恒等重写）
  + `BilEmitter`（线性化、临时变量物化、Resources 提取、
  LocalSymbols/ExternalSymbols 生成）。
- CLI：`--emit-bil PATH` + `--sema-only`（仿 `DumpAstOption` 模板，
  注册进 `CompileCommand.SubCommands`）。
- 临时措施：`core::Console.println` 以硬编码 external 符号提供
  （S10 换正式 core.latte 机制）。
- **验收**：`main + 字面量 + println + ret` 的 `.latte` 源码经
  `compile --emit-bil` 产出与 BIL §19 示例同级的合法 BIL 文本
  （黄金文件对照）；Origin 调试链（Bil→Lowered→Bound→AST.Span）通。

## S7 控制流全套（P3 + P4 同步推进）

if 语句/表达式（含 M33 值块隐式取值）、循环四形态 + break/continue
标签（`.breakid` 发射）、switch 语句/表达式（常量表 + pattern 降级）、
try/catch/finally（catch-table）、seq（含表达式形态 + `return@`）、
throw、短路 and/or 展开、`?.` / `if?` / 复合赋值 / 解构 / 字符串插值
脱糖。BIL §3.4 每条规范化规则至少一个 `LoweredDescribe` 用例。

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
bootstrap 与 core.latte 边界定稿、S6 的硬编码 Console 临时措施移除。
兼作前端常驻回归测试。

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
「VM 可执行性」。

---

## 与前端的接口备忘

- 中端输入恒为过 `ASTIntegrityValidator` 的 `RootASTNode`；
  发现前端应保证而未保证的结构问题 → `CompilerInternalException`，
  不出用户诊断。
- `ASTIntegrityValidator` 明示不查「声明侧字段完整性」（如 init 参数
  映射的空 Type 是合法形态）——这类检查是 P2/P3 的职责，勿漏。
- 前端遗留给语义期的义务清单：M33 值块隐式取值判定、
  init `_ -> field` 参数映射解析、import/namespace 的模块语义。
