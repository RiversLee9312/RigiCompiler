# Latte Compiler 进度报告

> **进度对齐标准**：本文档是项目进度的**唯一权威来源**。
> 每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须更新本文档；
> 更新时保持文档结构不变，并在「里程碑历史」追加一段。
> 计划与分工见 `compiler/syntax/PARSER_ROADMAP.md` 与 `compiler/semantic/SEMANTIC_ROADMAP.md`；本文档只记录「现状」。

**报告日期**: 2026-08-03
**当前阶段**: **中端（语义分析 + BIL 生成）阶段** —— M35 为中端的开篇里程碑：架构定稿（`compiler/semantic/SEMANTIC_ARCHITECTURE.md`）+ 路线图 S0–S14（`compiler/semantic/SEMANTIC_ROADMAP.md`）+ 语言规范修订（shared/rich/wrapper/String）；M36 落地 S0 诊断基建（`Semantic/Diagnostics.cs` + `CheckSemanticError`），同批完成 ROADMAP 文件级细化（S0–S6）；M37 落地 S1 符号图内核（`Semantic/Symbols/` 四文件 + bootstrap 硬编码 + `CanonicalSymbolPrinter`）；M38 落地 S4 BIL 对象模型 + BilWriter（`Bil/` 五文件，§19 黄金示例逐行一致）；M39 落地 S2 P1 声明收集（`Semantic/CompilationUnit.cs` + `Semantic/DeclarationCollector.cs`，符号图首个真实消费者）；M40 落地 S3 P2 声明解析（`Semantic/DeclarationResolver.cs`，七个子任务全部落地）；M41 落地 S5 P3 最小闭环（`Semantic/Binder.cs` + `Semantic/Bound/` 节点集 + `Semantic/NameResolver.cs` 名字解析共享设施提取 + `Tests/BoundDescribe.cs`，AST → BoundTree）；M42 完成**路径表达式统一**重构（SYNTAX §1.4 忠实落地：表达式位置的符号/调用/索引/成员/wrapper 后缀链统一为单一 `PathExpressionASTNode`，原五节点删除，语义上色全部归 P3）；M43 落地 **native 函数机制**（SYNTAX §4.6：`native` 修饰符 + `@NativeLibrary`/`@NativeSymbol` 内建注解；P1 建壳 + P2 `CheckNativeDeclarations` 全规则校验；BIL §8.4 `native symbol(...) lib(...)` 声明形态 + §8.4.1 全局裸条目 + §21.5 VM 内建 hook 表；RUNTIME §26 `latte_rt` shim 约定）与 **stdlib 内嵌源机制**（`Semantic/StdlibSources.cs` + `stdlib/core/Console.latte`：core.io::Console 的 native print/printErr + Latte 层 println，与用户源同走 P1–P4），同批落地 Binder 宿主类型成员查找、符号 Accessibility（§16）与 Bil 符号段裸条目模型；M44 落地 S6 P4 最小闭环（`Lowering/`：Lowered 节点集 + Lowerer P4a 恒等重写 + BilEmitter P4b 发射，**中端四 pass 全通——hello world 端到端出合法 BIL 文本**），并以 CLI `--emit-bil`/`--sema-only` 接线收官 S6；M45 落地 S7a P4 基础发射补齐（Lowered 节点补齐八类 + Lowerer 覆盖 S5 全部 Bound 节点 + BilEmitter 新发射 set.var/get/set.field.static/§11 运算/带返回值 invoke/new + §18.1 标量资源全形态 + `Tests/LoweredDescribe.cs` 与 LowererTests 套件，**P3 能绑定的全部 Bound 节点均已端到端过 P4**）；M46 落地 S7b（**if 语句/表达式 + 值块 + 短路 and/or + 复合赋值，P3/P4 同步**：P3 新增 BoundIfStatement/BoundValueBlock/BoundIfExpression/BoundReturnValueStatement/BoundCompoundAssignmentExpression 五节点与值块标签栈 return@ 绑定、definite assignment 分支合并、GuaranteesReturn 双分支升级；P4a Lowerer session 化（前置语句机制 + 合成局部 `.sN`）落地短路展开/值块降级与 if 转换/复合赋值脱糖；P4b BilEmitter 多 block 与 §16.2 if 指令发射）；M47 落地 S7c-1（**while/do-while/break/continue 三 pass 落地** + 循环协议定稿（SYNTAX §7.3：范围循环半开 [a,b)、to 即 EnumerateInRange、IEnumerable 双接口）：P3 新增 BoundLoop（施工壳）/BoundLoopControl 与循环标签栈、definite assignment 循环两规则（while 后 = before、do-while 后 = 体尾）、值块内 break/continue 穿透（GuaranteesValueReturn 扩展）、return@ 隔循环边界拦截；P4a Lowerer 循环降级（条件求值移入 Judge 块 + 合成 bool 条件局部 .sN + 合成 .breakid 局部 .bN——LocalSymbol.Type 可空方案 + BoundLoop → BreakId 映射栈）；P4b BilEmitter 发射 loop/loop.rev（§16.3/§16.4 三 block）与 break/continue（§16.5）+ .vars 的 .breakid 条目（§9.3））；M48 落地 S7c-2（**实例成员最小闭环 + core.collections 迭代协议 + for 双形态统一脱糖**：P3 落地 this（宿主统一 method.Owner，含 ext 目标类型）/实例成员链上色（沿 BaseType 链 + 接口 receiver + ext 注册成员）/裸名实例成员补 this/for 双形态（范围循环 = EnumerateInRange ext operator 实例调用 + for-each 协议判定，协议三方法符号挂 BoundLoop，循环变量 const）；P4a for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext + Body 头=current）；P4b 开闸实例方法 fn（.args 的 .this，§9.2/§7.3）/实例 invoke（receiver 首实参）/get.field/set.field（§13.3）/init/operator §8.4 声明形态 + EmitBuiltinExtMembers（内建类型 ext 成员 §8.4.1 裸条目）；stdlib 三源（.bootstrap.latte 基元自举 + core/collections.latte 双接口与 RangeI32/RangeEnumeratorI32）全量同走 P1–P4）；M49 落地 S7d（**switch 语句/表达式 + throw，P3/P4 同步**：异常根 `core.Exception` 定稿进 bootstrap（`IsOpen`，具体子类归 S10 stdlib）；P3 新增 BoundSwitchStatement/BoundSwitchExpression/BoundThrowStatement 六节点 + switch 占位 `_` 栈（BindPath 单段 `_` 命中栈顶）+ 值匹配/pattern 显式分类（值匹配限编译期常量且类型严格相等、pattern 必须 bool）+ throw 异常根 IsAssignable 检查 + GuaranteesReturn/GuaranteesValueReturn 终止口径扩展与 DA 分支合并复用；P4a 常量 switch 恒等降级 + pattern 链降级（selector 物化 `.sN` + 嵌套 if 链 + 合成 cmp.eq 条件）+ switch 表达式结果局部，同批修复 M46 else-if 链值块编织 miscompile（TransformStatements 重写为 continuation 编织）；P4b 发射 switch 指令（§16.6 五操作数 + `switch0-itemN`/`switch0-default` 块 id + `.vars` .breakid 条目）+ §18.4 `switch-table<T>` 单行资源（同 header+元素序列跨 fn 去重）+ throw（§16.9 单操作数）——三形态 CLI 端到端逐行核对一致）；M50 落地 S7e（cast 最小闭环提前自 S8 + try/catch/finally + seq，P3/P4 同步：BoundCast/BoundTry/BoundCatchClause/BoundSeq 双形态六节点 + catch 类型 IsAssignable 到 Exception；P4a LoweredCast/LoweredTry（ExceptionSlot 合成 + catch 头 cast 编织）/LoweredSeqBlock + seq 表达式脱糖 + try-finally 部分终止编织拦截；P4b 发射 cast/cast.safe（§12.1/§12.2）+ call blk(seqN)（§16.1，volatile → §9.6 block 修饰符）+ try 四操作数指令（§16.7）+ §18.5 catch-table 多行资源——**SYNTAX §7 控制流全部贯通**）；M51 落地 S7f-1 字符串插值（spec 定稿 SYNTAX §3.8 toString 机制/插值语义 + RUNTIME §26 原生方法面 toString + BIL §21.5 hook/§11.2 string add 内建拼接；Lexer StringToken RawContent 定位底稿 + Parser StringInterpolationSplitter（配平截取 + 子词法/子解析 + span rebase 精确回源）+ AST 插值段节点；bootstrap String.Add 开放 + Any.toString 承诺/Object open native 默认实现；P3 绑定即规范化（非 String 段包 toString 调用 + 左结合 + 链，P4 零新增节点）；P4a 子类型 cast 物化五位置（receiver/实参/初始化/赋值/return，ARCH §6.1 首个落地）——插值端到端出合法 BIL）；M52 收官 S7f（`?.` 安全调用 + `if?` 空值回退 + 解构声明：nullable BIL 语义定稿（§18.1 null 资源类型即 .nullable\<T\>、§12.1 装箱/展开、§13.3 泛型宿主字段替换判定）；BoundSafeAccess/占位叶子 + P4a 物化/unwrap/wrap 脱糖；`if?` Parser 重组 + P3 严格定型；core.Pair 进 .bootstrap.latte + 构造类型成员查找 ConstructedFrom 回退与泛型字段最小替换（S9 前置）——**S7f 四项全部端到端出合法 BIL**）；M53 前端回补（用户决策的插值架构重构：「Parser 侧拆分」改为「Lexer 层栈嵌套」——字符串层遇 `${` 压基础层正常词法，驱动按 token 层大括号计数配平弹回；StringInterpolationSplitter 与 RawContent 全部删除，SYNTAX §3.8 单行宿主引号限制解除，AST 与 P3/P4 零改动）；M54 细化 S8 为 S8a–S8f（ROADMAP）并落地 S8a（is/supers/with + typeOf 三 pass——SYNTAX §3.5/§3.7 右侧双形态定稿（is/supers/with 先类型后值、with 静态目标必须 wrapper、不做静态不可能性拒绝；typeOf 值/类型双形态先值后类型）+ §3.5 castFrom 笔误修正 + §9.2 补 override 行；BoundTypeCheckExpression（Kind 三态 + TargetType/TargetValue 互斥双槽）/BoundTypeOfExpression 两节点 + Binder 不落袋试探双形态解析（reportErrors: false）；P4a 恒等重写；BilEmitter 首次发射 §12.3 type.is/type.supers/type.with（含三 .indirect 动态形态）与 §12.5 getid.var/getid.type——**六种形态全部端到端出合法 BIL**，is .Case 归 S11 落归口诊断）；M55 完成**中端三树 visitor 化重构**（S8b 前置架构重构，用户决策：Binder/Lowerer/BilEmitter 三个 session 巨石按 CRTP visitor 协议全部重写——静态 Visit 统一入口 + Enter/Exit 生命周期模板 + 双协议 + context 方言接口视图 + 类别分派器 + 结构 visitor 簇级分文件，行为零变化、测试零改动，`Semantic/Binding/` + `Lowering/Rewriters/` + `Lowering/Emitting/` 新组织；同批完成 S8b smart cast 语言规则专项定稿 `compiler/semantic/SMART_CAST_DESIGN.md`）；M56 落地 S8b smart cast 三 pass 全通（SYNTAX §3.5 完整规则 + §3.4 null 判等 + FlowState 收窄事实表 + P4a 物化，SmartCastTests 55 用例新套件）；M57 完成 **BIL 生成全模型对象化重构**（用户决策：Bil/ 从「opcode 字符串 + 位置操作数列表」迁移为强类型模型——指令子类族（拼写/操作数序/多行排版由类固定）+ `BilSpellings` 拼写唯一定义点 + 枚举化种类/修饰符/标量类型 + switch-table/catch-table 专用资源类 + blk/res 操作数持对象引用，BilWriter 删除 opcode switch，行为零变化——黄金文本逐字节一致）；M58 落地 **BIL 验证器 BilVerifier**（用户决策：不推进语言特性、回补质量基建，提前自路线图 S12——`Bil/` 五新文件按 §20 类别覆盖 §20.1–20.8 静态可判子集，配套 `Tests/BilTestHarness.cs` 基建与 BilVerifierTests 新套件，BilEmitterTests/BilWriterTests 全量迁移至验证器框架，CLI `--emit-bil` 接入验证——产出非法即报错不落盘）；M59 落地 S8c（**索引访问 + 实例成员完整化三 pass 全通**：getAtIndex/setAtIndex 运算符绑定（BoundIndexExpression 读/写形态分型）+ 赋值/复合赋值 place 扩展 + PathVisitors 重构表达式底座链泛化（解开全部 S8 归口诊断），多参数索引定稿为编译错误（SYNTAX §13.2 同步）；P4a LoweredIndexExpression 恒等 + P4b §13.6 get.array/set.array 发射 + BilVerifier 严格三元组查询（§6.4 精确匹配）——索引读写与底座链端到端出合法 BIL）。下一步 S8d（重载解析 + 默认参数 + 具名参数，纯 P3）。
前端里程碑回顾：Parser/PDA 大扫除（M23）、AST 结构标注与 Validator 重写（M24）、Lexer 修复与 fuzz 基建（M25）、日志与 AST JSONL（M26）、CLI 插件化（M27）、Lexer 位置与 AST Span（M28）、AST 容器重构（M29）、Utilities 拆分（M30）、前端大修（M31）、多行字符串（M32）、值块统一（M33）、技术债清扫（M34）。
**测试总计**: 2119/2119 通过 (100%) + Lexer fuzz 6000/6000（42 个套件，`dotnet run -- test --all` 单命令全量）
**版本控制**: Git `main` 分支（2026-07-17 首次提交）

---

## 1. 里程碑总览

| # | 里程碑 | 状态 | 完成日期 | 测试 |
|---|--------|------|----------|------|
| M1 | P0 核心基础（字面量/类型引用/变量声明） | ✅ | 2026-07-17 | 28/28 |
| M2 | 结果传递机制（IResultProducer/IResultConsumer） | ✅ | 2026-07-17 | 含于各套件 |
| M3 | 泛型语法迁移 `\<...>`（文档 + Lexer + SymbolLayer） | ✅ | 2026-07-17 | 18/18 |
| M4 | GenericParametersParserLayer（roadmap #22） | ✅ | 2026-07-17 | 21/21 |
| M5 | 表达式后缀链 + ArgumentListParserLayer（roadmap #4 大部分） | ✅ | 2026-07-17 | 54/54 |
| M6 | ParameterListParserLayer（roadmap #5） | ✅ | 2026-07-17 | 14/14 |
| M7 | P2 语句系统核心（CodeBlock/if 语句/循环/return/赋值） | ✅ | 2026-07-18 | 42/42 |
| M8 | P1 收尾（Lambda/if/switch 表达式 + typeOf/as/is） | ✅ | 2026-07-18 | 39/39 |
| M9 | TryCatchFinallyParserLayer（roadmap #10） | ✅ | 2026-07-26 | 9/9 |
| M10 | SeqBlockParserLayer（roadmap #11，含表达式形态） | ✅ | 2026-07-26 | 17/17 |
| M11 | throw 语句 | ✅ | 2026-07-26 | 10/10 |
| M12 | CoroutineOps：await/yield（roadmap #12） | ✅ | 2026-07-26 | 13/13 |
| M13 | P3 类型声明解析基础（DeclarationParserLayer 重构） | ✅ | 2026-07-26 | 16/16 |
| M14 | 统一声明层：全局/成员/嵌套共用一套 infra | ✅ | 2026-07-26 | 36/36 |
| M15 | 声明泛型参数接入统一声明层（类型/函数/operator） | ✅ | 2026-07-26 | 15/15 |
| M16 | 属性访问器 getter/setter（§9.4 三类位置，roadmap #23） | ✅ | 2026-07-26 | 17/17 |
| M17 | enum struct 的 `[]` case 列表（固定/参数化 case、显式判别值） | ✅ | 2026-07-26 | 10/10 |
| M18 | init 参数映射（`_ -> field`，SYNTAX §9.3，roadmap #18 收尾） | ✅ | 2026-07-26 | 7/7 |
| M19 | `like` 委托（§9.6）+ `ext` 扩展成员（§4.4）—— **P3/P4 收官** | ✅ | 2026-07-26 | 9/9 |
| M20 | P5 起步：wrapper 主体（@ 注解 + `.proxy.*` 代理成员 + 前导点 enum case） | ✅ | 2026-07-26 | 23/23 |
| M21 | 模块系统 import（§15.2）+ wrapper 路径访问（`:`，§14.1/§3） | ✅ | 2026-07-26 | 18/18 |
| M22 | namespace 声明（§15.1）—— **P5 收官** | ✅ | 2026-07-26 | 7/7 |
| M23 | Parser/PDA 大扫除：TokenDisposition、施工目标协议、ExpressionRootASTNode、EOF 正式化、AST 完整性验证、测试基础设施 | ✅ | 2026-07-26 | 425/425（22 套件） |
| M24 | AST 结构标注（ChildAstNode/ParentAstNode/AstCarrier）+ Validator 重写 + 删除 ASTNodeType + 5 个父子指针 bug 修复 | ✅ | 2026-07-26 | 430/430（23 套件） |
| M25 | Lexer 修复：SlashLexerLayer（除法/注释分流）+ Lexer 输出 EOF + 注释集中跳过 + fuzz 基建 | ✅ | 2026-07-26 | 453/453 + fuzz 6000（24 套件） |
| M26 | 日志系统（Logger 分级 + verbose 默认关闭 + JSONL 落盘）+ AST JSONL 序列化诊断 | ✅ | 2026-07-26 | 498/498 + fuzz 6000（26 套件） |
| M27 | CLI 插件化重构：`<COMMAND> [--sub-cmd...]`（help/compile/test）+ CommandLineMask + 帮助程序生成 + 交互菜单删除 | ✅ | 2026-07-27 | 544/544 + fuzz 6000（27 套件） |
| M28 | Lexer 位置修复（offset/列号/EOF 冲刷/token 头/sourceName 单源化）+ AST Source Span（ISpanReceiver 层 span 回填）+ ASTVisitor 统一遍历 + Validator span 检查与类型审计 | ✅ | 2026-07-27 | 556/556 + fuzz 6000（27 套件） |
| M29 | AST 容器重构：基类共有 `Children`/`Annotations` 删除；语义字段（`Declarations`/`Statements`/`Members`）+ wrapper 挂载接口（`IWrapperAttachable` + Entity/Method/Value 三分类） | ✅ | 2026-07-27 | 27 套件全绿（用例无增删）+ fuzz 6000 |
| M30 | `Core/Utilities.cs` 拆分（Token/Keywords/异常/AST 基类归位 7 文件）+ ASTVisitor 遍历可重载（VisitNode/EnumerateChildren virtual）+ 文档幽灵清理（FRONTEND_TYPES 修订、FRONTEND_ARCHITECTURE 删除、ROADMAP 头注） | ✅ | 2026-07-27 | 27 套件全绿（用例无增删）+ fuzz 6000 |
| M31 | 前端大修：全量 review 驱动的 40+ 项修复（Span 左闭右开、Lexer 块注释重写、续行规则、位运算符、0b/0o/下划线字面量、Keywords 大扫除、修饰符/标识符校验、JSONL v2 + 反序列化器、测试基建统一） | ✅ | 2026-07-28 | 746/746 + fuzz 6000（29 套件） |
| M32 | 多行字符串 `"""`：SYNTAX §3.3 规范定稿（Swift 风格严格多行）+ QuoteLexerLayer 引号分流 + MultilineStringLexerLayer 两阶段施工 + 转义表单源化 + 插值标记词法期判定（`\${` 误报修复；Parser/AST 经 StringToken 复用近零改动） | ✅ | 2026-07-28 | 790/790 + fuzz 6000（30 套件） |
| M33 | 值块统一：if/switch 表达式分支体与 lambda 体统一为代码块（多语句 + `return@_`/named 取值）、switch 语句形态（新 SwitchStatementASTNode）、lambda 体内裸 return 编译错误（allowBareReturn 全链传染）、seq 匿名默认标签 `seq`→`_` | ✅ | 2026-07-28 | 838/838 + fuzz 6000（30 套件） |
| M34 | 技术债清扫：字符字面量（CharLexerLayer + CharToken + CharLiteralASTNode）、复合赋值 10 运算符（CompoundAssignmentExpressionASTNode）、`is` 右侧 enum case（TypeCheck TargetType/TargetCase 双字段互斥）、wrapper `.name` 保留参数名、import `{}` 单标识符禁令规则化报错 —— **前端阶段收官** | ✅ | 2026-07-28 | 895/895 + fuzz 6000（30 套件） |
| M35 | **中端阶段开篇**：语义分析与 BIL 生成架构定稿（四 pass + 双 Bound Tree + 驻留符号图）+ 路线图 S0–S14 + 语言规范修订（String 归非 rich 值类型、wrapper 恒 rich struct 且 `obj:Wrapper` 为只读 place、共享安全类型与两条逃逸闸门、rich/shared 单向传染、非 rich struct 不得 open/abstract） | ✅ 文档 | 2026-07-29 | 895/895 + fuzz 6000（30 套件，纯文档无增删） |
| M36 | S0 诊断基建（中端第一段代码）：`Semantic/Diagnostics.cs`（Diagnostic + DiagnosticBag 可恢复诊断模型）+ `CheckSemanticError` 入 TestHarness + ROADMAP S0–S6 文件级细化 | ✅ | 2026-07-31 | 910/910 + fuzz 6000（31 套件） |
| M37 | S1 符号图内核：`Semantic/Symbols/`（SemanticSymbol 家族 + SymbolGraph 驻留 + BootstrapSymbols 硬编码层级/基元/intrinsic 键空间 + CanonicalSymbolPrinter 五形态 + BIL 类型引用投影） | ✅ | 2026-07-31 | 977/977 + fuzz 6000（33 套件） |
| M38 | S4 BIL 对象模型 + BilWriter：`Bil/` 五文件（Module/Resources/Symbols/Function/Instructions + Writer，对中端零依赖、字符串身份、§17 协程暂缓），§19 黄金示例逐行一致 | ✅ | 2026-07-31 | 983/983 + fuzz 6000（34 套件） |
| M39 | S2 P1 声明收集：`Semantic/CompilationUnit.cs` + `Semantic/DeclarationCollector.cs`（符号壳 + namespace 驻留合并 + import 登记 + ext 待注册 + 重复声明诊断） | ✅ | 2026-07-31 | 1066/1066 + fuzz 6000（35 套件） |
| M40 | S3 P2 声明解析：`Semantic/DeclarationResolver.cs`（类型引用解析 + ErrorType 毒化、继承/implements 图与双环检测、修饰符合法性、rich/shared 字段闭包与单向传染、共享安全闸门、泛型约束声明侧、ext 注册 + wrapper 适用性矩阵）+ Parser 两处越权拦截移交 P2 + 约束裸名参数 Parser 修复 + bootstrap 注册 Core.Types | ✅ | 2026-07-31 | 1204/1204 + fuzz 6000（36 套件） |
| M41 | S5 P3 最小闭环：`Semantic/Binder.cs` + `Semantic/Bound/` 节点集（AST → BoundTree）+ `Semantic/NameResolver.cs`（P2/P3 名字解析共享设施提取）+ LocalSymbol + `Tests/BoundDescribe.cs` | ✅ | 2026-07-31 | 1294/1294 + fuzz 6000（37 套件） |
| M42 | 路径表达式统一（SYNTAX §1.4）：表达式位置五节点（SymbolReference/Call/Index/MemberAccess/WrapperAccess）删除，统一为 `PathExpressionASTNode`（首段 + 段 + 后缀）；ExpressionParserLayer 后缀链重写、Binder BindPath 适配、171 用例快照迁移 | ✅ | 2026-07-31 | 1295/1295 + fuzz 6000（37 套件） |
| M43 | native 函数机制（SYNTAX §4.6 + BIL §8.4/§8.4.1/§21.5 + RUNTIME §26）+ stdlib 内嵌源载入（`Semantic/StdlibSources.cs` + `stdlib/core/Console.latte`）+ Binder 宿主成员查找 + 符号 Accessibility + Bil 段裸条目模型 | ✅ | 2026-07-31 | 含于全量（38 套件） |
| M44 | S6 P4 最小闭环：`Lowering/Lowered/` 节点集 + `Lowering/Lowerer.cs`（P4a 恒等重写）+ `Lowering/BilEmitter.cs`（P4b 发射）——hello world 端到端出合法 BIL 文本，中端四 pass 全通；CLI `--emit-bil`/`--sema-only` 接线收官 S6 | ✅ | 2026-07-31 | 1393/1393 + fuzz 6000（39 套件） |
| M45 | S7a P4 基础发射补齐：Lowered 节点补齐八类（局部声明/表达式语句/赋值 + 字段引用/二元/一元/带返回值调用/new）、Lowerer 覆盖 S5 全部 Bound 节点、BilEmitter 新发射（set.var、get/set.field.static、§11 运算单点映射、invoke、new、§18.1 标量资源全形态）+ `Tests/LoweredDescribe.cs` + LowererTests 套件 | ✅ | 2026-08-01 | 1439/1439 + fuzz 6000（40 套件） |
| M46 | S7b if 语句/表达式 + 值块 + 短路 and/or + 复合赋值（P3/P4 同步）：Bound 五节点（BoundIfStatement/BoundValueBlock/BoundIfExpression/BoundReturnValueStatement/BoundCompoundAssignmentExpression）+ 值块标签栈 return@ 绑定 + definite assignment 分支合并（before∪(setT∩setF)）+ GuaranteesReturn 双分支升级；Lowerer session 化（前置语句机制 + 合成局部 `.sN` + 短路展开 + 值块降级与 if 转换 + 复合赋值脱糖）；BilEmitter 多 block（§16.2 `if $c blk blk`、无 else 用 none、block id `if0-then` 形态、分支块落尾不补 ret） | ✅ | 2026-08-01 | 1514/1514 + fuzz 6000（40 套件） |
| M47 | S7c-1 while/do-while/break/continue（P3/P4 同步）+ 循环协议定稿（SYNTAX §7.3/§13.2/§15.3）：BoundLoop（施工壳，循环标签栈）/BoundLoopControl + DA 循环两规则（while 后 = before、do-while 后 = 体尾）+ 值块穿透（GuaranteesValueReturn 扩展，BIL §16.5）+ return@ 隔循环边界拦截；Lowerer 循环降级（条件求值移入 Judge 块 + 合成 bool 条件局部 `.sN` + 合成 .breakid 局部 `.bN`——LocalSymbol.Type 可空方案 + BoundLoop → BreakId 映射栈；break/continue 真跳转对 if 转换零改动）；BilEmitter 发射 loop/loop.rev（§16.3/§16.4 三 block，`loop0-body`/`loop0-judge` 块 id）与 break/continue（§16.5）+ `.vars` 的 `.breakid` 条目（§9.3，Type null 投影） | ✅ | 2026-08-01 | 1567/1567 + fuzz 6000（40 套件） |
| M48 | S7c-2 实例成员最小闭环 + core.collections 迭代协议 + for 双形态（P3/P4 同步）：BoundThis/BoundInstanceCall/BoundFieldAccess 三节点 + 实例链上色（沿 BaseType 链 + 接口 receiver + ext 注册成员，宿主统一 method.Owner）+ 裸名实例成员补 this + for 绑定（范围 = EnumerateInRange ext operator 调用、协议判定含「自身即构造」分支、协议三方法挂 BoundLoop、循环变量 const）；for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext + Body 头=current）；BilEmitter 开闸 .this（§9.2/§7.3）/实例 invoke receiver 首参/get.field/set.field（§13.3）/init/operator §8.4 声明形态 + EmitBuiltinExtMembers（内建类型 ext 成员 §8.4.1 裸条目）；stdlib 三源（.bootstrap 基元自举 + collections 双接口/RangeI32/RangeEnumeratorI32）全量过 P1–P4 | ✅ | 2026-08-01 | 1634/1634 + fuzz 6000（40 套件） |
| M49 | S7d switch 语句/表达式 + throw（P3/P4 同步）+ 异常根定稿（`core.Exception` 进 bootstrap，IsOpen，具体子类归 S10）：BoundSwitchStatement/BoundSwitchExpression/BoundThrowStatement 六节点 + switch 占位 `_` 栈 + 值匹配/pattern 显式分类（常量限定 + 类型严格相等 / pattern 必须 bool）+ throw IsAssignable 到 Exception + GuaranteesReturn 终止口径扩展；P4a 常量 switch 恒等 + pattern 降级嵌套 if 链（selector 物化 `.sN` + 合成 cmp.eq 条件）+ switch 表达式结果局部，同批修复 M46 else-if 链值块编织 miscompile（TransformStatements → continuation 编织）；BilEmitter 发射 switch 指令（§16.6 五操作数 + `switch0-itemN`/`switch0-default` 块 id + .breakid 条目）+ §18.4 switch-table 单行资源（同表跨 fn 去重）+ throw（§16.9） | ✅ | 2026-08-01 | 1688/1688 + fuzz 6000（40 套件） |
| M50 | S7e cast 最小闭环 + try/catch/finally + seq（P3/P4 同步，cast 提前自 S8）：BoundCastExpression（as/as?）+ BoundTryStatement/BoundCatchClause + BoundSeqStatement/BoundSeqExpression 六节点（含 BoundValueBlock.IsVolatile）+ catch 类型 IsAssignable 到 Exception + 语句 seq 不压值块栈；P4a LoweredCastExpression/LoweredTryStatement（ExceptionSlot 合成 + catch 头 cast 编织）/LoweredSeqBlock 四节点 + seq 表达式脱糖（合成结果局部 + 前置 seq 块）+ try-finally 部分终止编织拦截；BilEmitter 发射 cast/cast.safe（§12.1/§12.2）+ call blk(seqN)（§16.1，volatile → §9.6 block 修饰符）+ try 四操作数指令（§16.7）+ §18.5 catch-table 多行资源——**SYNTAX §7 控制流全部贯通** | ✅ | 2026-08-01 | 1780/1780 + fuzz 6000（40 套件） |
| M51 | S7f-1 字符串插值 + toString 机制 + String 拼接开放 + 子类型 cast 物化（P1–P4 全链路）：spec 定稿（SYNTAX §3.8 toString/插值语义 + RUNTIME §26 原生方法面 toString + BIL §21.5 hook/§11.2 string add）；Lexer StringToken.RawContent/MultilineIndent/IsMultiline 定位底稿；AST StringInterpolationPart（[AstCarrier] 段级字面量子结构/表达式 Root 互斥双字段）；Parser StringInterpolationSplitter（RawContent 一体化扫描 + 配平截取 + 子词法/子解析 + span rebase 精确回源）；bootstrap String.Add + Any.toString 承诺/Object open native 默认实现；P3 绑定即规范化（非 String 段包 toString 调用 + 左结合 + 链，P4 零新增节点）；P4a 子类型 cast 物化五位置（receiver/实参/初始化/赋值/return——ARCH §6.1 首个落地） | ✅ | 2026-08-01 | 1821/1821 + fuzz 6000（40 套件） |
| M52 | S7f 收官：`?.` 安全调用 + `if?` 空值回退 + 解构声明（P3/P4 同步 + Parser 回补）+ nullable BIL 语义定稿（§18.1 null 资源类型即 .nullable\<T\>——null 检查 = cmp.ne + null 资源；§12.1 nullable 装箱/展开；§13.3 泛型宿主字段替换判定）：BoundSafeAccess/占位叶子 + P4a 物化/unwrap/wrap 脱糖；`if?` Parser 重组（中缀 if + ?）+ P3 严格定型 + 延迟求值脱糖；core.Pair 进 .bootstrap.latte + Parser 解构分支 + P3 构造类型成员查找 ConstructedFrom 回退与泛型字段最小替换（S9 前置）+ P4a 物化/逐字段读取——**S7f 四项全部端到端出合法 BIL** | ✅ | 2026-08-01 | 1872/1872 + fuzz 6000（40 套件） |
| M53 | 插值词法帧机制（前端回补，用户决策的架构重构）：插值解析从「Parser 侧拆分」改为「Lexer 层栈嵌套」——字符串层遇 `${` 压基础层正常词法，驱动按 token 层大括号计数配平弹回（字符串/字符/注释内容天然豁免）；InterpolationStart/End 标记 token + 驱动插值帧栈 + StringLexerLayer 挂起 `$` 判定 + MultilineStringLexerLayer 段结算与闭界统一回填 + LiteralParserLayer 段序列状态机；StringInterpolationSplitter（~280 行）与 RawContent/MultilineIndent/IsMultiline 全部删除；SYNTAX §3.8 单行宿主引号限制解除（`"a${"b"}c"` 合法）；AST 与 P3/P4 零改动 | ✅ | 2026-08-01 | 1878/1878 + fuzz 6000（40 套件） |
| M54 | S8 细化（S8a–S8f 进 ROADMAP）+ S8a is/supers/with/typeOf 三 pass（P3/P4 同步）+ SYNTAX §3.5/§3.7 右侧双形态定稿（is/supers/with 先按类型引用解析、失败按值绑定且必须 Type\<T\>、同名类型优先；with 静态目标必须 wrapper；不做静态不可能性拒绝；typeOf 双形态——值形态定型 Type\<操作数静态类型\>、单段裸名先值后类型）+ §3.5 castFrom 笔误修正 + §9.2 补 override 行：BoundTypeCheckExpression（Kind 三态 + TargetType/TargetValue 互斥双槽，恒 bool）/BoundTypeOfExpression（Operand/TargetType 互斥）两节点 + Binder 不落袋试探双形态解析（ResolveSymbolPath reportErrors: false + ErrorTypeSymbol 显式排除）+ 动态形态 Type\<T\> 校验 + DA 未赋值检查；Lowered 同构两节点恒等重写；BilEmitter 首次发射 §12.3 type.is/type.supers/type.with（含三 .indirect 动态形态）与 §12.5 getid.var/getid.type（Bil/ 零改动——通用 opcode 模型）；is .Case 归 S11 落归口诊断——**六种形态全部端到端出合法 BIL** | ✅ | 2026-08-01 | 1924/1924 + fuzz 6000（40 套件） |
| M55 | **中端三树 visitor 化重构**（S8b 前置，用户决策的架构重构）：Binder（2890 行）/Lowerer（1331 行）/BilEmitter（1045 行）三个 session 巨石全部 visitor 化——CRTP 协议基类（静态 Visit 唯一入口 + Enter/Exit 生命周期模板，栈压弹 finally 固化）+ 双协议（Visit→TResult? 上行合成 / VisitInto 壳填充）+ context 方言（同一函数级状态对象的接口视图，Environment 只读共享）+ 类别分派器唯一 switch + 结构 visitor 簇级分文件；否定超大 partial（状态污染）；停线重写 + Binder 先行全链验证后复制（Lowerer/BilEmitter）；协议 v2 修正（scope/expectedType 下传参）；FlowState 提取（DA 的家，S8b 收窄表预留）；行为零变化（三树各自完成时 40 套件 1924 全绿，BilEmitter 黄金文本逐字节一致，测试零改动，0 新警告）；同批完成 S8b smart cast 专项定稿（SMART_CAST_DESIGN.md：Q1=B 含 const 字段收窄/Q2=A guard/Q3=A and-or-not/Q4=A switch 占位；发现 null 判等前置缺口） | ✅ | 2026-08-03 | 1924/1924 + fuzz 6000（40 套件） |
| M56 | S8b smart cast 三 pass 全通（M55 定稿落地）：SYNTAX §3.5 完整规则 + §3.4 null 判等段；FieldSymbol.IsConst + const 字段赋值检查（init 豁免，兑现 M41 技术债）；null 判等绑定（装箱 cast §12.1 + §11.5 合规）；FlowState 收窄事实表（NarrowKey 根+const 字段链，纯交集合并——收窄非单调与 DA 区分）+ ConditionFactsExtractor（真/假边事实对）+ BoundSmartCastExpression 标记；收窄应用点（if guard 反向传播（DA 不变）/and-or 右侧上下文/while 体真边+体赋值根剔除/switch `(_ is T)` selector 收窄/赋值失效/字段链稳定判定）；P4a 物化 LoweredCastExpression（P4b 零新增）；SmartCastTests 新套件（注册表 #41） | ✅ | 2026-08-03 | 1979/1979 + fuzz 6000（41 套件） |
| M57 | **BIL 生成全模型对象化重构**（用户决策：去魔法 string）：`BilInstruction` 从 opcode 字符串 + 位置操作数列表改为强类型子类族（`Bil/BilInstructions.cs` 基类 + Compute/Data/ControlFlow 三指令文件按规范章节划分——opcode 拼写、操作数个数/类型/顺序、switch/try 多行排版由类固定；§11 `BilBinaryOp`/`BilUnaryOp`、§12.3 `BilTypeCheckKind` 枚举）；`BilSpellings` 全部拼写唯一定义点 + 六枚举（`BilTypeKind`/`BilMemberKind`/`BilBlockModifier`/`BilAccessibility`/`BilKeyword`/`BilScalarType`）+ `BilModifier` 子类族（访问/关键字/operator(名)/symbol(...)/lib(...)）；`BilSwitchTableResource`/`BilCatchTableResource` 专用资源类（header/元素自渲染，EmittingFacility 字符串拼接删除）；`BilBlockOperand`/`BilResourceOperand` 持对象引用（悬空 blk/res 引用不可构造）；BilWriter 删除 opcode switch（指令自渲染 WriteTo）；P4b 值发射契约 string → `BilVariableOperand`；行为零变化（黄金文本逐字节一致、CLI 样例 diff 字节一致、测试用例数不变） | ✅ | 2026-08-03 | 1979/1979 + fuzz 6000（41 套件） |
| M58 | **BIL 验证器 BilVerifier + BIL 测试迁移**（用户决策：回补质量基建，提前自 S12）：`Bil/` 五新文件按 §20 类别 partial 分文件（§20.1–20.8 静态可判子集；防误报降级——.generic< 跳过/基名兼容/IsAssignableTo 链判定/保守 DA；预定义符号表收技术债 #16；entrypoint 结构化终止；双 switch default 防腐化）+ `Tests/BilTestHarness.cs`（EmitBilUnit 共享 + 验证器断言 + res 重编号形状黄金）+ BilVerifierTests 新套件（全管线正例零错误 + §20 逐类负例）+ BilEmitterTests 全量迁移（私有渲染器与全模块黄金删除）+ BilWriterTests 自足模块补验证 + CLI `--emit-bil` 验证接线（非法不落盘） | ✅ | 2026-08-03 | 2074/2074 + fuzz 6000（42 套件） |
| M59 | S8c 索引访问 + 实例成员完整化（P3/P4 同步）：BoundIndexExpression（读绑 getAtIndex/写绑 setAtIndex）+ SymbolLookup.FindInstanceOperators（BaseType 链 + ConstructedFrom 回退 + Kind/参数个数过滤）+ PathVisitors 重构（表达式底座绑定/首段后缀折叠/段后缀折叠/`this[i]`/容器末段后缀，解开全部 S8 归口诊断）+ 赋值/复合赋值 place 扩展；多参数索引 `a[i, j]` 定稿为编译错误（SYNTAX §13.2 同步）；P4a LoweredIndexExpression 恒等降级 + P4b §13.6 get.array/set.array 发射（新 SetArrayInstruction）+ BilVerifier 严格三元组查询（无候选/无精确匹配/多命中逐级诊断）；全部扩既有套件（Binder/Lowerer/BilEmitter/BilVerifier 四套件加用例，注册表未动） | ✅ | 2026-08-03 | 2119/2119 + fuzz 6000（42 套件） |

---

## 2. 当前可解析语法

```latte
// 字面量
42, 0xFF, 100L, 3.14, 0.1f, "Hello ${x}", """多行字符串""", true, null, 'A', '\n'

// 类型引用（含 \< 泛型、嵌套、可空）
i32, String?, List\<T>, Map\<K,V>, List\<Map\<String, i32>>?

// 变量声明（含完整初始化表达式）
var x = 42
const name: String = "Hello"
var v = foo(1, name = 2)
var v = foo().bar[0]
var v = new User(id = 42)
var v = a.b\<i32>(x)
var r = 1 + (2 * 3)          // 无优先级规则已强制：1 + 2 * 3 报错

// 类型操作（is/supers/with 检查，as/as? 转换，typeOf）
obj is String, obj supers Animal, obj with Serializable
obj as String, obj as? String
var t = typeOf(box)
result is .Failed          // is 右侧 enum case（§12.3，M34；as/supers/with 右侧仍只收类型）

// if / switch 表达式（分支体统一为代码块，M33：单表达式分支隐式取值是
// 「块内恰好一条 ExpressionStatement」的语义规则；多语句分支 return@_ / named 取值）
var r = if (x > 0) { x } else { opposite(x) }          // 必须有 else
var r = if (x > 0) named check {
    seq { return@check x }                             // named + return@标签 穿透内层块
} else {
    return@_ opposite(x)                               // 匿名分支体默认标签是 _
}
var r = switch(expr) {
    (1) -> { "one" }                                   // 值匹配
    (_ > 10) -> {                                      // 模式匹配（_ 引用 expr）
        logBig(expr)
        return@_ "big"                                 // 多语句分支体显式取值
    }
    default -> { "other" }                             // 必须有 default
}
var r = switch(expr) named match { (1) -> { return@match 1 } default -> { return@match 0 } }

// switch 语句（M33：语句形态，结果值被丢弃；分支体为完整代码块，必须有 default）
switch(expr) {
    (1) -> { handleOne() }
    (_ > 10) -> {
        logBig(expr)
        handleBig()
    }
    default -> { handleOther() }
}

// Lambda（含泛型、async、trailing；体为单表达式或多语句块，M33）
var f = func{(x: i32): i32 -> (x + 1)}
var f = func{(width: TSize)\<TSize extends Size>: TSize -> width}
var loader = async func{(id: i32): SharedUser -> loadUserNow(id)}
list.map{(item: String): i32 -> item.length}           // 脱糖为调用实参
var f = func{(x: i32): i32 -> {                        // 多语句块体
    const doubled = (x * 2)
    return@_ doubled                                   // 块体必须显式 return@；裸 return 是编译错误
}}
var f = func{(x: i32): i32 -> named calc { return@calc (x * 2) }}

// 代码块与语句（语句以换行或 } 结束）
{
    var x = 1
    x = (1 + 2)                                        // 赋值
    x += 1                                             // 复合赋值（M34：+=/-=/*=//=/<<=/>>=/>>>=/&=/|=/^=）
    foo().field = v
    return x                                           // return / return@_ value
    break@outer                                        // break/continue[@标签]
}

// if 语句（else 可选，支持 else if 链）
if (x > 0) { foo() } else if (y > 0) { bar() } else { baz() }

// 循环（for-each / 范围 / while / do-while / named 标签）
for (item in collection) { print(item) }
for (i in 0 to 10) named outer { break@outer }
while (condition) { doSomething() }
do { doSomething() } while (condition)

// try-catch-finally（SYNTAX.md §8）：完整异常处理系统
try {
    riskyOperation()
} catch (e: IOException) {
    handleIO(e)
} catch (_: RuntimeException) {
    // 丢弃异常变量
} finally(e) {
    // e 为异常或 null
    cleanup()
}

// throw 语句：抛出异常
throw new IOException("File not found")
throw getError()
if (invalid) {
    throw new ValidationError()
}

// seq 块（SYNTAX.md §6）：作用域/using 资源管理/named 标签/表达式形态
seq {
    var temp = compute()
}

volatile seq {
    // volatile 操作
}

seq using(const file = new File("path"))
using(var stream = new FileInputStream(file))
named readFile {
    process(stream)
}

// seq 作为表达式（return@_/return@标签，匿名默认标签为 _，M33）
const result = seq {
    const ac = a * c
    const discriminant = (b * b) - (4.0 * ac)
    return@_ sqrt(discriminant)  // 返回值
}

var r = seq named calc {
    return@calc getValue()
}

// await/yield 协程操作（SYNTAX.md §7.5）
const user = await loadUser(42)
await flushLogs()
yield                         // 裸 yield
yield sleep(1000)             // 带 alarm

// 类型声明（class/interface/struct/wrapper + 修饰符/继承/implements/嵌套）
pub open class Dog : Animal implements Drawable, Serializable {
    pub var name: String
    pub init(x: i32) {}
    pub func speak(): String { return "Woof!" }
    pub class Inner {}                 // 嵌套类型，与顶层同一路径
}
pub rich struct Entry {}
pub shared rich struct SharedEntry {}
wrapper Logged {}

// like 委托（§9.6，仅 class）与 ext 扩展成员（§4.4，限定名 Type.member）
pub class Apple : Fruit like pear {
    pub var pear: Pear = Pear()
}
pub ext func String.reversed(): String { ... }
pub ext var String.isEmpty: bool { get(_: _) { return (this.length == 0) } }

// enum struct 的 [case 列表]（§12：固定/参数化 case、_ 参数洞、显式判别值）
pub enum struct RequestResult {
    pub const errorCode: i32
    pub init(code: i32)
}[
    Success(-1) -> 0,
    Failed(errorCode = _) -> 1
]

// 全局字段与全局函数（与类成员走同一条解析路径）
pub const MAX: i32
var counter: i32
func add(a: i32, b: i32): i32 { return (a + b) }
pub static func helper()

// 属性访问器（§9.4：类字段/全局变量/栈上变量三类位置同一条路径）
var width: i32 {
    pub get(value: _) { return value }       // backing field + 自定义体
    priv set(value: _) { log(value) }
} = 100
var height: i32 {
    pub get                                  // 编译器生成实现（无参无体）
    priv set
} = 200
var area: i32 { get(_: _) { return (width * height) } }   // 计算属性（无 backing field）

// @ 注解 / wrapper 应用（§14.5：可叠加，挂所有声明；含编译器内建 @WrapperTarget）
@WrapperTarget(.Entity)
pub wrapper Logged\<TTarget> { ... }
@WrapperTarget(.Value)
pub wrapper Clamped { ... }
@Logged("DEBUG")
@Serializable()
pub class MyService { ... }
@Timed()
pub func heavyComputation(): i32 { ... }
@Clamped(0, 100)
var health: i32 = 50

// wrapper proxy 成员（§14.2：specific + 四类 wildcard；同类 wildcard 唯一，§14.6）
operator .proxy.doSomething(arg: i32): String { ... }       // specific 方法代理
operator .proxy.opr.plus(another: TTarget): TTarget { ... } // specific 运算符代理
operator .proxy.get.name\<TField>(value: TField): TField { ... }
operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, ...): TReturn { ... }
operator .proxy.get.*\<TValue>(symbol: String, value: TValue): TValue { ... }
operator .proxy.set.*\<TValue>(symbol: String, value: TValue) { ... }
operator .proxy.opr.*\<named TNamedArgs..., ...>(...): TReturn { ... }
operator .proxy.call(.name: String, args: named Any...): Any { ... }  // method canonical + .name 保留参数名（§14.4，M34）

// 前导点 enum case 引用（§12：固定/参数化 case）
const result: RequestResult = .Success
const failed: RequestResult = .Failed(404)

// import（§15.2：单个/多个/全部三种形态；多个导入共享前缀路径）
import core.collections.List
import core.collections.{List, Map}
import core.collections.*

// namespace 声明（§15.1：顶层单行声明）
namespace com.example.myapp

// wrapper 路径访问（§14.1/§3：与成员访问同属路径后缀链，链式左结合）
var logger = service:Logged
var w = obj:A:B
var l = foo().bar[0]?.length:MyWrapper
var t = service:Logged.level

// 声明上的泛型参数（类型/函数/operator，含型变/约束/可变参数）
class Container\<TElement> { ... }
class Cache\<out TElement extends Comparable> { ... }
func transform\<TInput, TResult>(input: TInput): TResult { ... }
func update\<named TValues... with Serializable>(configs: named TValues...): bool { ... }
pub operator plus\<TAnother extends Addable>(another: TAnother): V { ... }

// 函数形参列表（已接入 func/operator/init 声明）
(a: i32, b: String = "x", rest: named i32...)

// init 参数映射（§9.3：_ 同名映射 / 显式名 / 默认值 / 与普通参数混合）
pub class Point {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
    pub init(_ -> x = 0, _ -> y = 0)
    pub init(horizontal: i32 -> x, vertical: i32 -> y)
}
```

---

## 3. 组件状态详表

| 组件 | 状态 | 测试 | 说明 |
|------|------|------|------|
| LiteralParserLayer | ✅ | 78/78 | 全部字面量（M31：0b/0o/下划线补齐，`3.` 报错）；字符字面量（M34：CharLexerLayer + CharToken + CharLiteralASTNode）；多行字符串经 StringToken 复用零改动接入（M32）；插值段序列状态机（M53：InterpolationStart 委托 ExpressionParserLayer 就地填充段 Root，allowBareReturn 传染）+ 结构/span/JSONL 往返 + 错误路径（M51/M53） |
| MultilineStringLexerLayer（+ QuoteLexerLayer 分流） | ✅ | 48/48（M32 新套件） | SYNTAX §3.3 Swift 风格严格多行：开界换行剥除、闭界独占行定缩进基准、转义与单行一致（StringEscape 单源）、两阶段施工（按行缓冲 + 闭界时剥缩进/转义）、插值标记词法期判定（`\${` 不误报）；M53 插值词法帧机制：累积阶段挂起 $ 判定 + 段结算产出（原文暂存保序）+ 闭界统一回填（剥缩进 + 转义；词法先于解析全量完成，回填天然安全）+ 首段 span 修正；无插值时保持单 token 与含闭界 span 行为 |
| TypeReferenceParserLayer | ✅ | 17/17 | M31 重写为真实套件（独立层驱动 + 集成 + 结构断言） |
| VariableDeclarationParserLayer | ✅ | 25/25 | Initializer 经 ExpressionRootASTNode 直挂；访问器块委托 PropertyAccessorParserLayer（M16）；M31 保留字/标识符校验；解构声明三分支状态（M52，SYNTAX §18：var (a, b) = pair，DestructureNames 互斥字段） |
| ExpressionParserLayer | ✅ | 137/137 | roadmap #4 全部落地；前导点 enum case（M20）、wrapper 路径访问 `:`（M21）；M31：位运算符 `<<`/`&`/`\|`/`^`、`in` 移除、insideParens 续行、复合赋值 10 运算符（M34）、span 含关键字；**M42 路径表达式统一**：后缀链就地施工单一 `PathExpressionASTNode`（首段 + 段 + 后缀，原 SymbolReference/Call/Index/MemberAccess/WrapperAccess 五节点删除）；M52 中缀 `if` 重组为 `if?` 空值回退运算符（IfNullFallbackSeen 态，参照 as? 模式） |
| ArgumentListParserLayer | ✅ | 12/12（M31 新套件） | 位置/具名/混合实参；M31：续行、空索引拒绝 |
| LambdaExpressionParserLayer | ✅ | 37/37 | roadmap #21 提前落地；体双形态（M33）：单表达式 / 多语句块体（named 标签、裸 return 边界） |
| SwitchStatementParserLayer | ✅ 两种形态 | 35/35（SwitchExpression 套件） | 表达式 + 语句形态（M33，新 SwitchStatementASTNode）；分支体统一代码块、named 标签、两形态强制 default |
| TypeOfExpressionParserLayer | ✅ | 7/7 | typeOf(expr) |
| CodeBlockParserLayer | ✅ | 29/29 | 语句识别与分发；return/break/continue 内联子状态；@ 注解声明分发（M20）；switch 语句路由与 allowBareReturn 裸 return 检查（M33） |
| IfStatementParserLayer | ✅ 两种模式 | 含于各套件 | 表达式模式强制 else；语句模式 else 可选 + else if 链；表达式分支体统一代码块 + named 标签（M33） |
| LoopParserLayer | ✅ | 15/15 | for-each/范围/while/do-while/named 标签 |
| TryCatchFinallyParserLayer | ✅ | 9/9 | roadmap #10；多 catch 子句、finally(e)、嵌套 try |
| SeqBlockParserLayer | ✅ | 17/17 | roadmap #11；volatile/using/named；语句+表达式双形态 |
| ThrowStatement（内联） | ✅ | 10/10 | throw expression；配合 try-catch 构成完整异常系统 |
| CoroutineOps（await/yield） | ✅ | 13/13 | roadmap #12；await 一元前缀运算符，yield 语句 |
| GenericParametersParserLayer | ✅ | 21/21 | 声明/约束/型变/可变参数；已接入类型/函数/operator 声明（M15） |
| ParameterListParserLayer | ✅ | 14/14 | 普通/默认/可变/具名可变；已接入 func/operator/init 声明；init 参数映射 `_ -> field`（M18，allowMapping 开关） |
| PathParserLayer | ✅ | 16/16（M31 新套件） | 符号路径 + `\<` 泛型实参；M31：尾点/双点/未闭合泛型报错（allowVariadicDots 保留 `...`）；M42 起收窄为**类型引用与 import 路径**专用（表达式路径由 ExpressionParserLayer 就地施工） |
| RootParserLayer | ✅ | 含于各套件 | 顶层分发（声明统一委托 DeclarationParserLayer） |
| DeclarationParserLayer | ✅ 统一声明层 | 94/94（TypeDeclaration 套件） | 任何位置任何声明的唯一入口：全局/成员/嵌套共用一套状态机；声明泛型参数（M15）、enum `[]` case 列表（M17）、like 委托与 ext 限定名（M19）、@ 注解与 wrapper `.proxy.*` 代理成员（M20）已接入 |
| PropertyAccessorParserLayer | ✅ | 17/17 | §9.4 访问器块 `{ get... set... }`；backing field 判定与 get/set 一致性校验；三类定义位置经 VariableDeclaration 汇聚 |
| ImportParserLayer | ✅ | 14/14 | §15.2 三种形态（单个/`.{}` 多个/`.*` 全部）；前缀路径复用 PathParserLayer（M21 重建） |
| NamespaceParserLayer | ✅ | 7/7 | §15.1 顶层单行声明；路径复用 PathParserLayer（M22） |
| ASTIntegrityValidator | ✅ | 含于各套件 | Parse 成功后自动验证 AST 不变量（M23）；M24 重写为 Attribute 驱动遍历；M28 基于 ASTVisitor 统一遍历重写 + span 校验与类型审计；M31：Required 子节点校验、基类链字段审计、[AstCarrier] 递归审计；失败抛 CompilerInternalException |
| ASTIntegrityValidatorTests | ✅ | 10/10 | 手工构造 AST 直调 Validate：合法树通过 + 结构破坏/span 破坏/类型审计违规拒绝（M24/M28） |
| ASTVisitor | ✅ | 含于 Validator/Serializer 套件 | 统一 AST 遍历基建（AST/ASTVisitor.cs）：[ChildAstNode] 子节点枚举唯一实现，Validator 与 Serializer 共用（M28） |
| LexerFuzzTests | ✅ | 32/32 + fuzz 6000 | Slash/EOF/注释固定用例 + 位置精确性用例（M28；M31 起左闭右开）+ 块注释吞字符/跨行分段/`\r\n` 归一/字符字面量报错用例（M31）+ 纯随机/结构化/变异 fuzz（固定种子，不变量含 sourceName/offset/范围不颠倒）+ Parser 注释跳过集成（M25） |
| TokenDispositionTests | ✅ | 4/4 | Push/Pop × Consume/Replay 四组合协议测试（M23） |
| Logger | ✅ | 7/7（LoggerTests） | 统一日志出口（Core/Logger.cs）：Verbose/Warning/Error 三级；控制台默认只显示 Warning+，`--verbose` 子命令放开 Verbose；`--log-to PATH` 全量（含 Verbose）JSONL 落盘（M26） |
| AstJsonlSerializer | ✅ | 86/86 | AST 树 JSONL 序列化 v2（M31：carrier 记录化、Nullable 标量、先过滤再取值、循环保护）+ `AstJsonlDeserializer` 完整反序列化（字段名键控、产物过 Validator、往返逐行一致）；`compile --dump-ast PATH` 输出（M26）；M33 新结构（值块/switch 语句/lambda 块体）经反射驱动零改动接入 |
| CommandLine | ✅ | 50/50（CommandLineParserTests） | CLI 内核（Core/CommandLine.cs + Core/Commands.cs）：CommandLineMask 自描述元数据驱动解析与 help 生成；`<COMMAND> [--sub-cmd...]` 结构（compile/test/help），交互菜单已删（M27）；compile 子命令含 `--parse-only`/`--dump-ast`/`--emit-bil`/`--sema-only`（M44 接入语义管线与 BIL 发射，诊断经 Logger 走 stderr、有 Error 退出码 1） |
| DiagnosticBag（中端，S0） | ✅ | 15/15（DiagnosticsTests） | 中端可恢复诊断基建（M36，`Semantic/Diagnostics.cs`）：`Diagnostic{Severity/Phase/Span?/Message}` + `DiagnosticBag`（全编译单元单实例、只追加、HasErrors 阶段推进门槛）；`CheckSemanticError` 断言入 TestHarness |
| SymbolGraph（中端，S1） | ✅ | 45/45（SymbolGraphTests） | 语义符号图内核（M37，`Semantic/Symbols/`）：SemanticSymbol 家族（Namespace/Type/Field/Method/Parameter/GenericParameter，引用相等即身份）、构造泛型驻留 cache（同 (定义, 实参) 必同实例、T? = Nullable\<T>）、Freeze 机制；BootstrapSymbols 硬编码 SYNTAX §3.1 层级 + §3.2 基本类型 + 特权关系（Box\<T\> <: Object、Nullable shared 按 T 推导）+ 基元 intrinsic 键空间（BIL §11） |
| CanonicalSymbolPrinter（中端，S1） | ✅ | 22/22（CanonicalSymbolPrinterTests） | 符号图 → BIL §5.2 canonical 字符串（M37）：类型/方法/字段/运算符/getter/setter 五形态 + BIL 类型引用投影（固定别名 > 标准构造 > canonical/闭合泛型，null 返回 → .void）；打印串对照 §5.2/§8.1/§19 示例逐条断言 |
| BilModel + BilWriter（中端，S4） | ✅ | 9/9（BilWriterTests） | BIL 对象模型与文本生成（M38，`Bil/` 五文件）：Module/Metadata/Resources（§18 全形态）/类型与成员声明（§8）/Function/.args/.vars/Block（§9）/指令与操作数（§10–§16，§17 协程暂缓）；对中端零依赖、字符串身份、Origin 以 object? 占位；writer 只输出标准 spelling、全段输出、§19 黄金示例逐行一致（含 wrapper 隐藏字段续行形态）；M43 起符号段允许 §8.4.1 裸成员条目（BilSymbolSectionEntry）；**M57 全模型对象化重构**（去魔法 string）：指令强类型子类族（`BilInstructions.cs` 基类 + Compute/Data/ControlFlow 三文件——opcode 拼写/操作数序/switch/try 多行排版由类固定）+ `BilSpellings` 拼写唯一定义点 + 六枚举（BilTypeKind/BilMemberKind/BilBlockModifier/BilAccessibility/BilKeyword/BilScalarType）+ BilModifier 子类族 + BilSwitchTableResource/BilCatchTableResource 专用资源类（header/元素自渲染）+ blk/res 操作数持对象引用；BilWriter 删除 opcode switch（指令自渲染 WriteTo）；行为零变化（黄金文本逐字节一致） |
| BilVerifier（中端，提前自 S12） | ✅ | 56/56（BilVerifierTests） | BIL 验证器（M58，`Bil/BilVerifier*.cs` 五文件，对 Semantic/AST 零依赖）：消费 BilModule 对象模型，覆盖 §20.1–20.8 静态可判子集（§20.9 VM 语义除外）——词法语法/符号（fn↔声明一一对应、native 规则、static 一致性、资源类型可解析）/类型（逐指令严格相等，读写分类唯一表）/保守 DA/控制流（entrypoint 结构化终止、块成员资格、token 作用域、结构环拒绝）/breakid capability/声明侧修饰符矩阵；防误报降级（.generic< 跳过、基名大小写不敏感、IsAssignableTo extends/implements 链、查不到声明降级通过）；预定义符号表对齐 BootstrapSymbols（内建不声明决策的可解析性闭合，收技术债 #16）；指令双 switch default 防腐化 |
| DeclarationCollector（中端 P1，S2） | ✅ | 83/83（DeclarationCollectorTests） | 声明收集（M39）：`Semantic/CompilationUnit.cs`（多源文件 + DiagnosticBag + SymbolGraph）+ `Semantic/DeclarationCollector.cs`（DeclarationCollector + DeclarationCollection + FileContext）——类型/变量/可调用/参数/泛型参数符号壳（默认基类建壳即定、rich/shared/static 只读标记位）、namespace 逐段驻留与跨文件合并、import 上下文登记、ext 拆名待注册、重复声明诊断（类型/变量同名、方法 P1 文本级签名，重载不误报）；getter/setter 与 enum case 壳按需增补（S8/S11） |
| DeclarationResolver（中端 P2，S3） | ✅ | 179/179（DeclarationResolverTests） | 声明解析（M40，`Semantic/DeclarationResolver.cs`）：类型引用解析（泛型参数 → NestedTypes → namespace 父链 → 全局 → imports → core 隐式查找序；T?→Nullable\<T\>；失败绑 ErrorTypeSymbol 毒化静默）；init 映射参数沿字段类型；继承/implements 图（种类匹配、open/abstract 可继承性、class/interface 双环检测）；修饰符合法性（Parser 的 rich/shared/open 即死拦截与重复/互斥校验移交于此，可恢复诊断）；rich/shared 单向传染 + 字段闭包七行表（直接分类违规即报、放行才展开泛型实参递归）；共享安全闸门（全局/静态/ext静态）；泛型约束声明侧（Target 必本声明泛型参数、with 边界必 wrapper）；ext 注册（Owner 改写挂目标类型）+ wrapper 适用性（@WrapperTarget、§14.9 矩阵 A/B/D、interface 实现者传染）；M43 增补 native 声明校验子任务（SYNTAX §4.6 全规则 + @NativeLibrary/@NativeSymbol 解析写符号 + wrapper 应用检查豁免）与 Accessibility 写符号（§16，BIL 发射与 S8 消费）；结束 Freeze 符号图 |
| Binder（中端 P3，S5） | ✅ | 302/302（BinderTests） | 函数体分析（M41，`Semantic/Binder.cs` + `Semantic/Bound/` + `Semantic/NameResolver.cs`）：分析单位 BoundFunctionBody{Method, Locals, BoundBlock}；字面量定型（null 走可空上下文）、var 推断、LocalSymbol、二元/一元 bootstrap intrinsic 键查询（结果类型维度：比较 bool、余同操作数）、赋值与 definite assignment 最小版、无重载直接调用（具名实参归位规范参数序）、new/init 匹配、return 所有路径显式返回检查；值/调用查找序 块 → 参数 → **宿主类型成员（M43 落地，沿 BaseType 链；字段裸名归后续）** → 命名空间链字段/函数 → 通配 import；多段路径 = 容器 + 末段成员（首段命中局部/参数判实例路径暂拒）；IsAssignable（严格相等/可空提升/BaseType 链/直接 interface，显式 cast 归 P4a）；S7b 增补 if 语句（else if 链包单语句 BoundBlock）/if 表达式（BoundValueBlock 值块：M33 隐式取值判定、显式 return@ 标签栈解析、产值类型统一、GuaranteesValueReturn）/复合赋值（读语义 unassigned + intrinsic 检查）+ definite assignment 分支合并（before∪(setT∩setF)）+ GuaranteesReturn 双分支 if 升级；S7c-1 增补 while/do-while 循环绑定（BoundLoop 施工壳 + 循环标签栈解析 break/continue 标签：无标签栈顶/named 从内向外、循环外与未定义标签诊断；for 报 not supported yet (S7c-2)；条件 bool 检查与 if 共用 CheckBoolCondition）+ DA 循环两规则（while 后 = before、do-while 后 = 体尾集合）+ 值块内 break/continue 穿透（GuaranteesValueReturn 视其为路径终止，BIL §16.5 动态结构作用域）+ return@ 隔循环边界拦截（值块栈记 LoopDepth，脱糖无法表达跳出中间循环）；S7c-2 增补 this（宿主统一 method.Owner——ext 方法 Owner = 目标类型；静态上下文诊断）+ 实例成员链上色（实例方法调用/实例字段访问：receiver 静态类型沿 BaseType 链查找，接口 receiver 查接口成员，ext 注册成员同路径；`?.`/wrapper `:` 段仍归口 S7f/S11）+ 裸名实例成员补 this（FindField/FindMethods 宿主链改 method.Owner）+ for 双形态（范围循环 = EnumerateInRange ext operator 实例调用 + for-each 协议判定（实现 core.collections.IEnumerable\<TItem\>，含「Iterable 类型自身即构造」分支；协议三方法符号挂 BoundLoop——P4 不做名字分析；循环变量 const 只读默认；DA for 后 = before）；访问控制（priv/protected）检查不做（归 S8，命中即放行）；S7d 增补 switch 语句/表达式绑定（BoundSwitchStatement/BoundSwitchExpression/BoundThrowStatement：switch 占位 `_` 栈——BindPath 单段 `_` 命中栈顶 selector 回指占位、值匹配限编译期常量且类型严格等于 selector 类型、pattern 必须 bool、分支产值类型统一与值块构造名区分 if/switch、GuaranteesReturn 终止口径扩展（throw 与全分支 return 的 switch 视为终止）、DA 分支合并复用 if 基建）+ throw 绑定（IsAssignable 到 bootstrap 异常根 core.Exception）；S7e 增补 cast 绑定（BoundCastExpression：as 结果即目标类型、as? 结果 Nullable\<T\>——P3 定型 P4 不再区分包装，可转性不做静态拒绝：as 失败是运行时 core.CastException、castTo/castFrom 名字分析归后续，ErrorType 毒化静默）+ try/catch/finally 绑定（BoundTryStatement/BoundCatchClause：catch 类型 IsAssignable 到 Exception、catch 变量与 finally(e) 变量 const 只读且命中即 assigned——finally 变量类型 Nullable\<Exception\>，DA 合并 = before∪(try∩全 catch)∪finally）+ seq 双形态（BoundSeqStatement 直通 BindBlock、不压值块标签栈——return@ 指向语句 seq 报未定义标签；BoundSeqExpression 复用 BindValueBlock 值块语义 + IsVolatile 置位 + 必须产值检查；using 拦截归 S13）；S7f-1 增补字符串插值绑定（BindStringInterpolation：字面量段复用字面量机器、非 String 段包 toString() 实例调用（Any 承诺沿 BaseType 链查最近声明）、全 String 段左结合 + 链（String.Add intrinsic——bootstrap 同步开放 "a"+"b"），绑定即规范化、P4 零新增节点）；S7f 收官增补 `?.`（BindInstanceChain SafeDot 分派：receiver 必须 Nullable\<T\>、段经 BoundSafeAccessReceiverExpression 占位叶子在非空 T 上绑定、结果不二次包装；普通段遇可空 receiver 专门诊断）+ `if?`（BindNullFallback：左 Nullable\<T\>、右 IsAssignable 到 T、结果恒 T）+ 解构（BindDestructuring：沿 BaseType 链找 core.Pair 构造、名字数恒 2、分量类型 = 构造实参；构造类型成员查找 ConstructedFrom 回退（FindInstanceField/FindInstanceMethods/FindField/FindMethods 统一）+ 泛型字段类型最小替换 SubstituteFieldType——S9 前置特判）；其余控制流/成员访问/重载/泛型等遇之报 P3 诊断（归 S8–S13） |
| StdlibSources（中端，S6/S10 机制最小子集） | ✅ | 47/47（StdlibSourcesTests） | stdlib 内嵌源载入（M43，`Semantic/StdlibSources.cs`）：`stdlib/**/*.latte` 以 EmbeddedResource 内嵌、编译时取出解析为 RootASTNode 注入编译单元（sourceName 为 `<stdlib>/...` 映射形，含点开头文件名反推），与用户源同走 P1–P4；M48 起三源（按逻辑名 Ordinal 排序）：`stdlib/.bootstrap.latte`（基元自举源，SYNTAX §15.3：ext operator i32.EnumerateInRange）、`stdlib/core/Console.latte`（core.io::Console：priv static native print/printErr + pub static println）、`stdlib/core/collections.latte`（core.collections：IEnumerable\<T\>/IEnumerator\<T\> 双接口 + RangeI32/RangeEnumeratorI32 范围循环实现）；M52 起 .bootstrap.latte 增 namespace core 头与 core.Pair\<TKey, TValue\> 自举声明（SYNTAX §18 解构协议根，编译器按 canonical 名硬编码参照） |
| Lowerer + BilEmitter（中端 P4a/P4b，S6+S7a+S7b+S7c+S7d+S7e+S7f） | ✅ | 157/157（BilEmitterTests）+ 139/139（LowererTests） | P4（M44/M45，`Lowering/`）：`Lowered/` 节点集（S6 五类 + S7a 补齐八类：局部声明/表达式语句/赋值/字段引用/二元/一元/带返回值调用/new，Origin 必填回指 BoundNode，LoweredExpression.Type 透传不冗余）+ `Lowerer`（S5 全部 Bound 节点恒等重写 + S7b 起 session 化：前置语句机制 + 合成局部 `.sN`（BIL §5.1 编译器保留名）+ bool 短路 and/or 按 §11.3 展开为 if 块、值块降级与 if 转换、复合赋值脱糖为前置赋值，新增 LoweredIfStatement/LoweredConstantExpression 节点、合成节点 Origin 指最近语法来源；S7c-1 循环降级：LoweredLoop/LoweredLoopControl 新节点、条件求值移入 Judge 块（条件内短路/if 表达式前置语句随块走）、合成 bool 条件局部 .sN + 合成 .breakid 局部 .bN（LocalSymbol.Type 可空方案——null 仅限 .breakid capability，emitter 侧 .vars 投影 .breakid §9.3）、BoundLoop → BreakId 映射栈、break/continue 真跳转对 if 转换零改动；S7c-2 实例成员恒等降级（LoweredThis/LoweredInstanceCall（Type 自带——for 脱糖合成节点 Origin 是语句）/LoweredFieldAccess 三节点）+ for 脱糖（前置 iterate() 写合成枚举器局部（GetConstructedType(IEnumerator, TItem)）+ Judge=moveNext + Body 头=current——产物复用 LoweredLoop，P4b 零新增；协议三方法取 P3 挂在 BoundLoop 的产物）；S7d switch/throw 降级（LoweredSwitch（全值匹配恒等，携 .breakid 合成局部 + DefaultBody）/LoweredSwitchCase/LoweredThrowStatement 三节点、Any(IsPattern) 分流——pattern 链降级：selector 物化 `.sN` 前置 + 递归嵌套 if 链、值分支条件 = 合成 cmp.eq（Origin 指 match 常量，`LoweredBinaryExpression` 可选显式 Type——透传会是错的）、switch 表达式结果局部 `.sN` + 前置语句；同批修复 M46 else-if 链值块编织 miscompile——TransformStatements 重写为 continuation 编织：终止分支织空 continuation、非终止分支织 rest）；S7e cast/try/seq 降级（LoweredCastExpression（Type 自带——as? 的 Nullable 结果 P3 已定型）/LoweredTryStatement/LoweredTryCatch/LoweredSeqBlock 四节点：try 合成 ExceptionSlot——finally(e) 时 slot 即 finally 变量、否则合成 `.sN`（Nullable\<Exception\>）；有名 catch 体头编织「变量 = cast slot」合成赋值（Origin 指 BoundCatchClause）；seq 语句/volatile 恒等、seq 表达式脱糖为前置 seq 块写合成结果局部 + 原位置读局部；try+finally 部分终止的值块编织拦截——TransformWithContinuation 深处触发 transformFailed 标记、诊断落袋后跳过函数体），未覆盖节点 P4 Error + 跳过函数体）+ `BilEmitter`（LoweredTree → BilModule：LocalSymbols 全量平铺含全局裸条目与 native/entrypoint 修饰符、extends 与种类默认基类相同则省略、Resources §18.1 标量全形态提取去重（string/int 系列/bool/char/f32/f64/null type(...)）、fn 定义 .args/.vars/单 entry block、临时变量 `.t0` 前缀、set.var/get/set.field.static/§11 运算单点映射/invoke/new 发射、void 末尾补 ret、S7b 多 block（§16.2 `if $c blk(then) blk(else)`、无 else 用 none、block id `if0-then` 形态无点号、分支块落尾不补 ret、合成 bool 常量与字面量同键去重）、S7c-1 循环发射（§16.3/§16.4 `loop`/`loop.rev` 操作数序 cond/body/none/judge/breakid、块 id `loop0-body`/`loop0-judge` 递增、§16.5 `break`/`continue` 携 breakid、.vars 的 .breakid 条目）、S7c-2 实例发射开闸（实例方法 fn 定义 .args 插 .this = OwnerType（§9.2/§7.3，ext 同形态）、实例 invoke receiver 首实参（接口方法 canonical 分派归 Middleware）、get.field/set.field（§13.3）、this → $.this 零指令、init/operator §8.4 声明形态（init / operator(名) / ext 修饰符）、EmitBuiltinExtMembers——内建类型 ext 成员以 §8.4.1 裸条目输出）、S7d switch/throw 发射（§16.6 `switch` 五操作数 selector/res(表)/[blk item 表]/blk(default)/breakid、块 id `switch0-itemN`/`switch0-default`、§18.4 `switch-table<T>` 单行资源同（header, 元素序列）跨 fn 去重——RegisterSwitchTable 复用 resourceKeys 字典与 RenderLiteral 渲染、§16.9 `throw` 单操作数）、S7e cast/try/seq 发射（§12.1/§12.2 `cast`/`cast.safe` 三操作数 SOURCE RESULT type(TARGET_TYPE)、§16.1 `call blk(seqN)` 不建栈帧 + volatile → §9.6 block 修饰符、§16.7 `try` 四操作数 blk(body)/$slot/res(表)/blk(finally)|none——块 id `try0-body`/`try0-catchN`/`try0-finally`、§18.5 `catch-table` 多行资源元素 `type(T) -> blk(...)` 保序——RegisterCatchTable 复用 resourceKeys 同序列去重）、Origin 塞 LoweredNode）；hello world 黄金输出逐行一致 + S7a/S7b/S7c-1/S7d 各形态 fn 指令与多 block 精确比对 + Origin 调试链断言；S7f-1 增补子类型 cast 物化（ARCH §6.1 首个落地：EnsureDeclaredType 统一五位置——实例/void 调用 receiver（≠ 方法宿主的装箱/基类视图，如 i32 调 Any.toString）、调用/new 实参（≠ 形参）、局部初始化、赋值、return；类型相同/ErrorType/非 TypeSymbol 直通，插值经 P3 绑成 toString/+ 链后恒等降级；hello world 黄金文本两处 return 严格化为显式 cast，BIL §6.5 合规）；S7f 收官增补 `?.`/if?/解构脱糖（LowerSafeAccess：物化 receiver .sN + 结果局部前置 null + if(cmp.ne recv, null) + 占位映射栈替换 unwrap cast + wrap cast，嵌套逐层命中；LowerNullFallback：物化左侧 + if/else 双分支（unwrap/回退）延迟求值；LowerDestructuring：pair 物化 + 逐字段读取——LoweredConstantExpression 扩展 null 常量，BilEmitter 发射 RegisterNullResource 统一 null 资源（§18.1 类型语义 .nullable\<T\>，与 cmp.ne 满足 §11.5）；LoweredFieldAccessExpression/LoweredLocalDeclarationStatement origin 放宽 BoundNode 承载合成路径）；`Tests/LoweredDescribe.cs` 为唯一 Lowered 树描述器 |

---

## 4. 关键架构决策（摘要）

- **施工目标协议**（M23 大扫除）：Parser Layer 栈只传递控制权；父 Layer 在 Push 前确定施工目标（具体节点或 ExpressionRootASTNode 等附加目标），子 Layer 原地施工或向目标附加节点；Pop 不传递任何数据。原 `IResultProducer`/`IResultConsumer`/`pendingResultHandler` 已全部删除
- **TokenDisposition**：Push/Pop 的 token 处置使用具名枚举（Consume/Replay），替代原 `bool shouldKeepToken`
- **ExpressionRootASTNode**：Syntax AST 中所有表达式位置的统一稳定挂载点；一次性 Attach、禁止替换；ASTNode.Parent 只能设置一次、**禁止任何形式重挂**（无 reparent）；「归属后知」场景以创建时归属即定的容器承载（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）或延迟一次性 AttachTo（注解）；解析成功后经 `ASTIntegrityValidator` 自动验证不变量
- **EOF 正式 Token**：`EndOfFileToken` 由 Lexer 在输出末尾追加（M25；Parser 仅对绕过 Lexer 的调用方保持追加兼容），只由 RootParserLayer 消费；非 Root 层遇 EOF 要么 Pop(Replay) 层层上交，要么报 "Unexpected end of file"
- **注释集中跳过**（M25）：CommentToken 由 Parser 主循环分发时统一跳过，各 Layer 不再自行处理；行注释不再吞掉结尾换行（回流由 Base 层产出 LineBreakToken）
- **SlashLexerLayer**（M25）：`/`、`/=`、`//`、`/*` 统一分流入口；输入结束以虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException
- **泛型语法 `\<...>`**：`<` 仅作小于号；Lexer 不合并 `>` 系列，`>=`/`>>`/`>>>` 由表达式层重组（详见 `SYNTAX.md` §3.6）
- **表达式路径统一**（M42，SYNTAX §1.4）：表达式位置的符号引用、调用、索引、成员访问（含 `?.`）、wrapper 访问（`:`）统一施工为单一 `PathExpressionASTNode`——首段（符号名或表达式底座）+ 段序列（连接符 + 成员名 + 泛型实参 + 调用/索引后缀）；原 SymbolReference/Call/Index/MemberAccess/WrapperAccess 五节点删除。「首段身份」与各段语义（实例成员/静态成员/wrapper）是语义上色问题，全部归 P3；`PathParserLayer` 收窄为类型引用与 import 路径专用
- **独立 Layer 可测性**：`Parser.Parse(tokens, baseLayer, entryLayer)` + `TestRootParserLayer`（只接受 EOF）支持任意 Layer 独立驱动测试，且拒绝被测 Layer 漏消费 token
- **统一声明层**（M14，依据 SYNTAX.md §14.8）：canonical symbol 的类名段可为空、`.static.` 只是标记位，因此全局函数与成员方法结构同构——`DeclarationParserLayer` 一套状态机覆盖全局/成员/嵌套任何声明；`CallableDeclarationASTNode` 单节点覆盖 func/operator/init；成员挂各节点语义容器（M29 起：`RootASTNode.Declarations` / `CodeBlockASTNode.Statements` / 类型节点 `Members`；基类共有 `Children` 已删除）
- **日志系统**（M26）：`Core/Logger` 是唯一日志出口（Verbose/Warning/Error）；Lexer/Parser 的 ContextImpl 经 Logger 输出，禁止直接 `Console.WriteLine`；控制台门槛默认 Warning+，`--verbose` 子命令放开 Verbose；`--log-to` 把全量日志（含 Verbose）以 JSONL 落盘，文件不过滤级别，便于 grep 诊断
- **AST JSONL 序列化**（M26）：`AstJsonlSerializer` 复用 Validator 的 [ChildAstNode] 反射下钻，深度优先每节点一行（id/parent/via/type/fields），`compile --dump-ast` 输出，供结构诊断
- **CLI 插件化**（M27）：用法 `<COMMAND> [--sub-cmd [args...]...]`，COMMAND 为 help/compile/test；每个 COMMAND 与 --sub-cmd 都是插件，暴露 `CommandLineMask`（名称/描述/参数个数/互斥）自描述元数据；解析器与 `help` 文本完全由 Mask 注册表数据驱动、程序生成；交互菜单已删除
- **位置信息单源化**（M28）：`CharRange.sourceName` 是源名唯一来源（`CharPosition` 不再携带）；`CharPosition.offset` 为 0 起始字符索引（修复恒 0 bug）；换行算当前行最后一列（修复第二行起列号 +1）；token 头跳过空白（修复缩进行 token Start 落在前导空格）；EOF 冲刷帧占虚拟位置（修复 EOF 处 token End 少算/倒置）
- **AST Source Span**（M28）：`ASTNode.Span`（`CharRange?`）记录节点源码范围；层目标由 Parser 主循环按 token 流计算、经 `ISpanReceiver.ReceiveSpan` 在层弹出时回填（`??=` 只填空），层内自建节点由所在层显式设置（创建记 Start、完成封 End，经 `ParserLayerContext.GetPreviousLocation()`）；`ExpressionRootASTNode` 透明继承内容表达式的 span；Validator 校验每节点 span 非空、sourceName 非空、End 不早于 Start
- **ASTVisitor 统一遍历**（M28）：`AST/ASTVisitor.cs` 是 [ChildAstNode] 子节点枚举的唯一实现，ASTIntegrityValidator 与 AstJsonlSerializer 共用（via 统一为 `member[i]`/`member[i](Carrier.Field)` 格式）；Validator 新增类型审计——装 ASTNode 的成员（字段/自动属性）必须带 [ChildAstNode]/[ParentAstNode]，[ChildAstNode] 标在非 AST 成员上同样拒绝
- **AST 容器语义化**（M29）：`ASTNode` 基类只保留 `Parent`/`Span`，共有 `Children`/`Annotations` 删除——顶层条目挂 `RootASTNode.Declarations`、块语句挂 `CodeBlockASTNode.Statements`、类型成员挂各类型节点 `Members`（均标 [ChildAstNode]，Validator/ASTVisitor/Serializer 零改动）；注解列表仅 7 种声明节点持有，经 `IWrapperAttachable` 访问，并按 SYNTAX §14 三类目标以 `IEntity/IMethod/IValueWrapperAttachable` 分类标记（挂载校验留待语义阶段）；`DeclarationParserLayer` 构造函数改收 `(parent, targetList)`
- **Span 左闭右开**（M31）：所有 `CharRange`（token 与 AST 节点 span）统一为 `[Start, End)`——Start 指向首个字符，End 指向最后一个字符的下一位置；相邻 token 首尾相接，EOF 为零宽范围
- **续行规则**（M31，SYNTAX §1.1）：`()`/`[]` 未闭合时换行按空白处理——实参/索引/形参列表层全状态跳过；`ExpressionParserLayer.insideParens` 括号语境（分组/实参/条件/迭代）在结构等待态透明化换行，右操作数与一元操作数继承
- **数字字面量单源**（M31）：`Parser/NumericLiteral.cs` 是进制（0x/0b/0o）/下划线/后缀判定与解析的唯一实现，Literal/Root/Expression 三层共用
- **JSONL v2 与往返**（M31）：AST JSONL 字段名键控不依赖顺序；carrier（ImportItem）记录化（独立产行、标量字段入 fields）；`AstJsonlDeserializer` 完整反序列化（三阶段：类型定位/实例挂接/回填，产物强制过 Validator），Parse→Serialize→Deserialize→Serialize 往返逐行一致
- **测试基建单源**（M31）：`Tests/AstDescribe.cs`（统一 AST 描述器）与 `Tests/TestHarness.cs`（统一驱动+断言）是全部套件的唯一描述/驱动实现；断言对象约定：除查的就是命令行/日志/token 流/层协议行为的套件外，一律断言 AST 树产物（描述串 + 结构断言）
- **值块统一与裸 return 边界**（M33）：if/switch 表达式分支体与 lambda 体统一为 CodeBlockASTNode——「单表达式分支隐式取值」是「块内恰好一条 ExpressionStatement」的语义规则，解析层无特判（取值留待语义阶段）；多语句值块经 `return@_`（匿名默认标签）/ `return@标签`（`named` 命名）取值；lambda 是裸 return 边界——`CodeBlockParserLayer.allowBareReturn` 标记沿施工链（if/循环/try/seq/switch 子块与表达式深处的分支体）全链传染，lambda 体一律下传 false，遇无 @标签 return 抛 ParserException；副作用：值块内的 if/switch 一律按语句分发，作值须写 `return@_ if ...`

---

## 5. 下一步计划

**Parser/PDA 大扫除（M23）已完成**：控制流系统与 AST 施工系统分离，
施工目标协议、ExpressionRootASTNode、EOF 正式化、AST 完整性验证全部落地。

**AST 结构标注与 Validator 重写（M24）已完成**：结构关系以
[ChildAstNode]/[ParentAstNode]/[AstCarrier] 显式标注，Validator 改为
Attribute 驱动并新增父子指针一致性校验，借此修复 5 个历史结构 bug；
ASTNodeType 枚举删除，节点类型判断全面改用 CLR 类型。

**日志系统与 AST JSONL 序列化（M26）已完成**：Logger 统一日志出口、
verbose 默认关闭（`--verbose` 子命令打开），`--log-to` 全量 JSONL 落盘；
AST 树可经 `compile --dump-ast` 序列化为 JSONL 供诊断。

**CLI 插件化重构（M27）已完成**：交互菜单删除，用法统一为
`<COMMAND> [--sub-cmd [args...]...]`（help/compile/test 三个 COMMAND）；
选项以 `CommandLineMask` 自描述、插件化注册，帮助文本程序生成；
CI 入口改为 `dotnet run -- test --all`。

**Lexer 位置修复 + AST Source Span + ASTVisitor（M28）已完成**：
Lexer 的 offset/列号/token 头/EOF 冲刷位置全部修复，sourceName 单源化到
CharRange；每个 AST 节点携带源码范围 Span（层目标由主循环经 ISpanReceiver
回填、层内节点显式设置、ExpressionRoot 透明继承），JSONL 输出 span 键；
Validator 与 Serializer 遍历统一为 ASTVisitor，Validator 新增 span 校验与
「未标注 AST 成员」类型审计。

**AST 容器重构（M29）已完成**：`ASTNode` 基类共有的 `Children`/`Annotations`
删除，只留 `Parent`/`Span`；子节点容器下放为语义字段——
`RootASTNode.Declarations`、`CodeBlockASTNode.Statements`、5 个类型节点各自
`Members`；注解列表仅 7 种声明节点持有，按 SYNTAX §14 三类 wrapper 目标
抽象为 `IWrapperAttachable` + `IEntity/IMethod/IValueWrapperAttachable` 接口。

**Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档清理（M30）已完成**：
`Core/Utilities.cs` 按语义拆为 7 个文件（`Lexer/Tokens.cs`、`Lexer/Notations.cs`、
`Parser/Keywords.cs`、`Core/Exceptions.cs`、`AST/ASTNode.cs`、`AST/SymbolNodes.cs`、
`AST/ImportNodes.cs`），同命名空间纯搬移、零调用点改动；ASTVisitor 的
`VisitNode`/`EnumerateChildren` 改为 virtual（默认仍走 [ChildAstNode] 反射）；
文档中已删除代码的引用（FrontendTypesExtension 全系、AcquisitionExpressionASTNode、
CharLiteralASTNode 等）与过时表述已清理，`FRONTEND_ARCHITECTURE.md` 删除。

**前端大修（M31）已完成**：全量 review 驱动的 40+ 项修复——测试基建统一
（AstDescribe/TestHarness，21 套件迁移 + 2 新套件）、Span 左闭右开、
Lexer 块注释重写（吞字符修复 + 换行不吞）、续行规则、位运算符、
0b/0o/下划线字面量（NumericLiteral 单源）、Keywords 大扫除（幽灵词清除 +
保留字补齐）、修饰符组合与标识符合法性校验、JSONL v2（carrier 记录化）+
完整反序列化器往返无损、Validator Required 子节点与基类链审计、
Logger 改走 stderr。详见「里程碑历史」M31 段落。

**多行字符串（M32）已完成**：SYNTAX §3.3 多行字符串规范定稿
（Swift 风格严格多行：开界 `"""` 后换行剥除、闭界独占一行且其缩进为剥除基准、
转义与单行一致），Lexer 新增 `QuoteLexerLayer` 引号分流（`"`/`""`/`"""` 统一入口，
与 `SlashLexerLayer` 同模式）与 `MultilineStringLexerLayer` 两阶段施工
（原文按行缓冲，闭合时先剥缩进再统一转义），转义表收编为 `StringEscape` 单源；
插值标记改词法期判定（`StringToken.HasInterpolation`，修复 `\${` 误报）；
Parser/AST 经 `StringToken` 复用近零改动。

**值块统一（M33）已完成**：if/switch 表达式分支体与 lambda 体统一为
CodeBlockASTNode（多语句 + `return@_`/`named` 取值，单表达式分支隐式取值规则不变、
下沉为语义规则）；switch 语句形态落地（新 `SwitchStatementASTNode`，两形态强制
default）；lambda 体内裸 return 成为编译错误（`allowBareReturn` 标记全链传染）；
seq 匿名默认标签从 `seq` 迁移为 `_`（纯注释与快照迁移，解析层无特判）。
详见「里程碑历史」M33 段落。

**技术债清扫（M34）已完成**：字符字面量落地（`CharLexerLayer` 三态状态机 +
`CharToken` + `CharLiteralASTNode`，转义复用 `StringEscape` 单源）；复合赋值
10 运算符（`CompoundAssignmentExpressionASTNode`，ExpressionParserLayer 遇
op+`=` 在左操作数 Attach 前定形构造，红线合规）；`is` 右侧 enum case
（`TypeCheckExpressionASTNode` 的 `TargetType`/`TargetCase` 双字段互斥，
仅 `is` 允许 `.`，switch 模式匹配同路径通吃）；wrapper `.name` 保留参数名
（ParameterListParserLayer `DotNameExpected` 状态，Name 原样存 `.name`）；
import `{}` 列表项单标识符禁令规则化报错（SYNTAX §15.2）。详见「里程碑历史」
M34 段落。

**中端阶段开篇（M35）已完成——文档层**：M34 收官前端（Lexer + Parser），
M35 起项目进入**中端（语义分析 + BIL 生成）阶段**。本里程碑只交付文档：
架构定稿 `compiler/semantic/SEMANTIC_ARCHITECTURE.md`（P1 声明收集 /
P2 声明解析 / P3 Binder→BoundTree / P4a Lowerer→LoweredTree /
P4b BilEmitter→BilModule 四 pass 分工，Roslyn 风格双 Bound Tree、
驻留符号对象图、bootstrap 与 core.latte 边界、诊断模型）与路线图
`compiler/semantic/SEMANTIC_ROADMAP.md`（S0–S14，近细远粗）；
同批修订语言规范（String 归非 rich 值类型、wrapper 恒 rich struct、
共享安全类型与两条逃逸闸门、rich/shared 单向传染）。
详见「里程碑历史」M35 段落。

**S2 P1 声明收集（M39）已完成**：`Semantic/CompilationUnit.cs`（编译单元 =
多源文件 RootASTNode + 全局 DiagnosticBag + 唯一 SymbolGraph）+
`Semantic/DeclarationCollector.cs`（遍历声明骨架建符号壳，不进函数体；
namespace 逐段驻留跨文件合并、import 上下文登记、ext 拆名待注册、
重复声明诊断累积不中断）。符号模型按需增补：NamespaceSymbol 容器成员表、
TypeSymbol.NestedTypes、MethodSymbol/FieldSymbol.ExtTargetPath、
MethodSymbol.GenericParameters、SymbolGraph.GlobalNamespace + GetNamespace
驻留。详见「里程碑历史」M39 段落。

**S3 P2 声明解析（M40）已完成**：`Semantic/DeclarationResolver.cs`
（`DeclarationResolver.Resolve(unit, decls)` 入口 + 私有 ResolveSession）
落地七个子任务——类型引用解析（含 ErrorTypeSymbol 毒化静默）、
init 映射参数沿字段类型、继承/implements 图与双环检测、修饰符合法性、
rich/shared 单向传染与字段闭包七行表、共享安全闸门、泛型约束声明侧、
ext 注册与 wrapper 适用性（§14.9 矩阵 A/B/D）。Parser 两处越权即死拦截
（CreateTypeNode 的 rich/shared/open 校验、OnModifiers 的重复/互斥校验）
移交 P2 可恢复诊断；修复 Parser 约束裸名参数（`T extends Bound`）只进
Constraints 不进 Parameters 的历史 bug（现双注册）；bootstrap 全部内建类型
补登 `Core.Types`（裸名 `i32`/`String`/`Object` 可解析）。符号图结束
Freeze。详见「里程碑历史」M40 段落。

**S5 P3 最小闭环（M41）已完成**：`Semantic/Binder.cs` 以函数体为独立
分析单位产出 BoundTree（`Semantic/Bound/`：BoundNode.Syntax 必填回指、
BoundExpression.Type 定型、BoundFunctionBody{Method, Locals, BoundBlock}），
落地 S5 全部约定范围——字面量定型、var 推断、作用域链、intrinsic 键查询、
无重载直接调用（规范参数序）、new/init、return 与 definite assignment。
同批完成两件基建：P2 名字解析核心提取为 `Semantic/NameResolver.cs`
（P2/P3 按 Phase 各自实例化，P2 委托后 138 用例零回归）与
`Tests/BoundDescribe.cs`（唯一 bound 树描述器，仿 AstDescribe）。
发现两个前端事实并适配：括号产生 GroupExpressionASTNode（透明下钻）、
`c.m()` 是路径形态而非 MemberAccess（首段命中局部/参数判实例路径暂拒）。
详见「里程碑历史」M41 段落。

**路径表达式统一（M42）已完成**：SYNTAX §1.4 的语言观忠实落地——
表达式位置的符号/调用/索引/成员（含 `?.`）/wrapper（`:`）后缀链统一
施工为单一 `PathExpressionASTNode`（首段 + 段序列 + 后缀，原五节点
删除），语法层只表达形态事实，「首段身份/段语义」上色全部归 P3。
ExpressionParserLayer 后缀链重写（段/后缀就地生长 + 表达式底座包装 +
seed 改路径形态）、Binder 改为 BindPath 单点上色（消除 M41 的
「双形态双路径」妥协）、泛型实参统一走 TypeReference（中段可空实参
合法化，能力取并集）、171 用例快照迁移零行为回归（Binder 套件 90
用例未动即绿，验证 bound 产物与源码语义一致）。详见「里程碑历史」
M42 段落。

**stdlib 内嵌源（M43）已完成**：`Semantic/StdlibSources.cs` 把
`stdlib/**/*.latte` 以 EmbeddedResource 内嵌进程序集，编译时取出解析为
RootASTNode 注入编译单元，与用户源同走 P1–P4；首个内容
`stdlib/core/Console.latte`（core.io::Console：priv static native
print/printErr + pub static println，println 在 Latte 层包装）。

**S6 P4 最小闭环（M44）已完成**：`Lowering/` 落地——Lowered 节点集
（五类，Origin 必填回指 BoundNode）+ Lowerer（P4a 恒等重写，未覆盖
节点报 P4 Error 并跳过函数体）+ BilEmitter（P4b 发射：LocalSymbols
平铺、Resources 字面量提取、fn 定义与临时变量物化、Origin 调试链
接通）。**中端四 pass（P1/P2/P3/P4）全部打通**：hello world 从源码
走到合法 BIL 文本（黄金输出逐行比对）。详见「里程碑历史」M44 段落。

**S7a P4 基础发射补齐（M45）已完成**：P3（S5）能绑定的全部 Bound
节点过 P4——Lowered 补齐八类节点、Lowerer 恒等重写全覆盖、
BilEmitter 新发射 set.var / get/set.field.static / §11 运算（单点
opcode 映射）/ 带返回值 invoke / new / §18.1 标量资源全形态
（bool/char/f32/f64/null type(...)）；`Tests/LoweredDescribe.cs`
（唯一 Lowered 描述器）与 LowererTests 套件落地。详见「里程碑历史」
M45 段落。

**S7b（M46）已完成**：if 语句/表达式 + 值块 + 短路 and/or + 复合赋值，
P3/P4 同步落地——P3 新增五个 Bound 节点（BoundIfStatement/
BoundValueBlock/BoundIfExpression/BoundReturnValueStatement/
BoundCompoundAssignmentExpression）与值块标签栈 return@ 绑定、
definite assignment 分支合并、GuaranteesReturn 双分支升级；P4a
Lowerer session 化（前置语句机制 + 合成局部 `.sN`）落地短路展开、
值块降级与 if 转换、复合赋值脱糖；P4b BilEmitter 出多 block BIL
（§16.2 if 指令）。详见「里程碑历史」M46 段落。

**S7c-1（M47）已完成**：while/do-while/break/continue 三 pass 落地
（循环协议定稿随批：SYNTAX §7.3 范围循环半开 [a,b)、to 即
EnumerateInRange、IEnumerable 双接口）——P3 新增 BoundLoop（施工壳）
/BoundLoopControl 与循环标签栈、definite assignment 循环两规则、
值块内 break/continue 穿透、return@ 隔循环边界拦截；P4a 循环降级
（条件求值移入 Judge 块 + 合成 .breakid 局部 `.bN`，LocalSymbol.Type
可空方案）；P4b 发射 loop/loop.rev 三 block 与 break/continue +
.vars 的 .breakid 条目。详见「里程碑历史」M47 段落。

**S7c-2（M48）已完成**：实例成员最小闭环 + core.collections 迭代
协议 + for 双形态统一脱糖——P3 落地 this/实例成员链上色/裸名
实例成员补 this/for 绑定（范围循环 = EnumerateInRange ext operator
调用 + for-each 协议判定，协议三方法挂 BoundLoop，循环变量 const）；
P4a for 脱糖复用 LoweredLoop（前置 iterate + Judge=moveNext +
Body 头=current）；P4b 开闸 .this/实例 invoke/get.field/set.field/
init/operator §8.4 声明形态 + EmitBuiltinExtMembers；stdlib 三源
（.bootstrap 基元自举 + collections 双接口）全量过 P1–P4。
详见「里程碑历史」M48 段落。

**S7d（M49）已完成**：switch 语句/表达式 + throw 三 pass 落地——
异常根 `core.Exception` 定稿进 bootstrap（`IsOpen`，具体子类归
S10 stdlib）；P3 落地 switch 绑定（占位 `_` 栈 + 值匹配/pattern
显式分类 + 产值类型统一 + DA 分支合并复用）与 throw 绑定（
IsAssignable 到 Exception）；P4a 常量 switch 恒等 + pattern 链
降级（selector 物化 + 嵌套 if 链）；P4b 发射 switch 指令（§16.6）
+ §18.4 switch-table 单行资源（同表跨 fn 去重）+ throw（§16.9）；
同批修复 M46 else-if 链值块编织 miscompile（TransformStatements
重写为 continuation 编织）。详见「里程碑历史」M49 段落。

**S7e（M50）已完成**：cast 最小闭环（提前自 S8）+ try/catch/finally
+ seq 三 pass 落地——P3 落地 cast 绑定（as/as? 定型，可转性不做
静态拒绝）、try 绑定（catch 类型 IsAssignable 到 Exception、
catch/finally(e) 变量 const、DA 合并）与 seq 双形态（语句直通 /
表达式值块复用 + 必须产值）；P4a try 合成 ExceptionSlot + catch 头
cast 编织 + seq 表达式脱糖 + try-finally 部分终止编织拦截；P4b 发射
cast/cast.safe（§12.1/§12.2）+ call blk(seqN)（§16.1）+ try 四
操作数（§16.7）+ §18.5 catch-table 多行资源。SYNTAX §7 控制流
全部贯通。详见「里程碑历史」M50 段落。

**S8a（M54）已完成**：is/supers/with + typeOf 三 pass 落地（S8 已
细化为 S8a–S8f 进 ROADMAP）——规范定稿（SYNTAX §3.5/§3.7 右侧
双形态解析规则 + with wrapper 限定 + typeOf 双形态定型 + castFrom
笔误修正 + §9.2 补 override 行）；P3 新增 BoundTypeCheckExpression
/BoundTypeOfExpression 两节点与不落袋试探双形态解析（is/supers/with
先类型后值、typeOf 单段裸名先值后类型，reportErrors: false）；
P4a 恒等重写；P4b 首次发射 BIL §12.3（type.is/type.supers/
type.with + 三 .indirect 动态形态）与 §12.5（getid.var/getid.type）。
is .Case 归 S11 落归口诊断。详见「里程碑历史」M54 段落。

**M55 已完成（架构重构）**：中端三树 visitor 化——Binder/Lowerer/
BilEmitter 三个 session 巨石（2890/1331/1045 行）按用户方案全部
重写为 CRTP visitor 架构（协议基类 + 静态 Visit 统一入口 +
Enter/Exit 生命周期模板 + 双协议 + context 方言接口视图 + 类别
分派器唯一 switch + 结构 visitor 簇级分文件，`Semantic/Binding/`、
`Lowering/`、`Lowering/Rewriters/`、`Lowering/Emitting/` 新组织）；
FlowState 提取为独立组件（S8b 收窄表的家）。行为零变化（三树
各自完成时全量测试全绿，BIL 黄金文本逐字节一致，测试零改动）。
架构定稿与进度见 `compiler/semantic/VISITOR_REWRITE.md`；同批
完成 S8b smart cast 语言规则专项定稿（`compiler/semantic/
SMART_CAST_DESIGN.md`：Q1=B 含 const 字段收窄/Q2=A guard 模式/
Q3=A and-or-not 全支持/Q4=A switch 占位收窄，并发现 `x == null`
无法绑定的前置缺口）。详见「里程碑历史」M55 段落。

**S8b（M56）已完成**：smart cast 三 pass 全通——SYNTAX §3.5 完整
规则 + §3.4 null 判等落地；FieldSymbol.IsConst 前置与 const 赋值
检查（init 豁免）；FlowState 收窄事实表（NarrowKey 根+const 字段链，
纯交集合并）+ ConditionFactsExtractor 真/假边提取 +
BoundSmartCastExpression 标记；guard 反向传播/and-or 右侧上下文/
while 体真边+体赋值根剔除/switch `(_ is T)` selector 收窄/赋值失效/
字段链稳定判定全部落地；P4a 物化 LoweredCastExpression（P4b 零新增）。
SmartCastTests 55 用例 + CLI 端到端三样例 BIL 核对。
详见「里程碑历史」M56 段落。

**BIL 生成全模型对象化重构（M57）已完成**：Bil/ 从「opcode 字符串 +
位置操作数列表」迁移为强类型对象模型（用户决策：去魔法 string）——
指令子类族固定拼写/操作数序/多行排版、`BilSpellings` 拼写唯一定义点、
六枚举与 BilModifier 子类族、switch-table/catch-table 专用资源类、
blk/res 操作数持对象引用；BilWriter 删除 opcode switch；P4b 值发射
契约 string → BilVariableOperand；行为零变化（BIL 黄金文本逐字节一致）。
详见「里程碑历史」M57 段落。

**S8c（M59）已完成**：索引访问 + 实例成员完整化三 pass 全通——
BoundIndexExpression（读形态绑 getAtIndex、写形态绑 setAtIndex）+
FindInstanceOperators（BaseType 链 + ConstructedFrom 回退 +
Kind/参数个数过滤）+ PathVisitors 重构解开全部 S8 归口诊断
（表达式底座绑定/首段与段后缀折叠/`this[i]`/容器末段成员后缀）+
赋值与复合赋值 place 扩展；多参数索引 `a[i, j]` 定稿为编译错误、
具名索引实参与普通调用同规则（SYNTAX §13.2 已同步）；P4a
LoweredIndexExpression 恒等降级；P4b §13.6 get.array/set.array
发射 + BilVerifier 严格三元组查询（§6.4 精确匹配，禁止隐式转换）。
详见「里程碑历史」M59 段落。

**下一步**：ROADMAP S8d（重载解析 + 默认参数 + 具名参数——纯 P3 步，
无 P4 面；规范前置：重载规则动工前先补 SYNTAX；source-level
ranking 唯一落点（BIL §3.3）+ 默认参数填充 + 具名参数重排，
BoundCall 保持规范参数序）。
前端进入维护状态，仅在中端暴露缺口时回补。

---

## 6. 技术债务与已知限制

1. ~~实参位置的 `a.b` 存在 MemberAccess/Symbol 双形态~~（M42 已消除：表达式路径统一为 PathExpressionASTNode，语义上色归 P3 单点）
2. `3.`/`3.foo` 在 M31 起为编译错误（点后缺数字；`3.foo` 形态规范未定义，需要成员访问时请写 `(3).foo`）
3. ~~值块取值规则（M33 起解析层无特判）留待语义校验~~（M46 已落地 if 表达式值块：M33 隐式取值判定、显式 return@ 全路径检查（GuaranteesValueReturn）、产值类型统一；M49 已落地 switch 表达式分支体的同类校验；M50 已落地 seq 表达式的同类校验——复用 BindValueBlock + 必须产值检查）
4. ~~复合赋值的语义推导留待语义/后端阶段~~（M46 已落地：P3 绑定 BoundCompoundAssignmentExpression（读语义 unassigned 检查 + intrinsic 检查）、P4a 脱糖为前置赋值，表达式值为写回后值）；M34 起解析层接受全部 10 个运算符不变
5. `.name` 保留参数名（M34 起解析层接受，Name 原样存 `.name`）的上下文约束（仅 method wrapper canonical 形态可用）与语义规范化留待语义阶段
7. 编译 0 警告（大扫除消除了原 `Core/Utilities.cs` 的 nullable 警告）
8. BIL 待补（M35 登记，不属前端）：wrapper 改为 rich struct 后，`BIL_STANDARD.md` §12.4 缺**只读 place 的接收者形态**；async 协程指令 §17 待 S13 专项修订。两项均记于 `compiler/semantic/SEMANTIC_ARCHITECTURE.md` §7/§7.1，落地分别在 ROADMAP S11 / S13
9. P2 推断规则（M40 登记，规范未明写）：wrapper 缺 `@WrapperTarget` 即诊断（规范只定义了三类目标的标注形态）；init 映射 `_ -> field` 的目标字段无类型标注即诊断（沿字段类型无从谈起）。若后续规范给出默认行为，回到 DeclarationResolver 放宽
10. P2 边界（M40 登记）：无类型标注字段（`var x = expr`）的类型推断归 P3，其闭包/闸门判定需在 P3 补一轮复核；§14.9 矩阵 C 行（栈上局部变量的 Value wrapper 检查）归 P3；P1 文本级方法签名重复判定的签名级精确化（类型解析后判定真正重载冲突）留待后续里程碑
11. P3 边界（M41 登记，S5 最小闭环的已知留口）：无 init 零参 `new` 按「默认构造」放行（规范未明写默认构造规则）；~~全局字段作赋值目标的 const 判定缺「符号 → 声明 AST」反向映射~~（M56 已兑现：FieldSymbol.IsConst + init 豁免检查）；有默认值的形参在缺失时报 Missing argument（默认参数填充归 S8）；局部变量遮蔽参数/外层变量按放行处理（规范未明）；IsAssignable 的 interface 判定只看直接实现（接口继承链递归与数值提升规则待规范明确后收紧）
12. P4 边界（M44 登记，S6 最小闭环的已知留口）：~~实例方法（需 `.this` receiver，BIL §7.3）发射报 P4 Error 跳过（归 S8）~~（M48 S7c-2 已开闸 .this/实例 invoke，M59 S8c 补齐 §13.6 get.array/set.array 索引发射）；~~`Kind != Regular` 的方法成员（init/operator/getter/setter）符号段声明报 P4 Error 跳过（归 S8/S11）~~（init/operator §8.4 声明形态 M48 已开闸；getter/setter 归 S8e）；~~Resources 只提取 string 与整数字面量~~（§18.1 标量全形态已落地，M45；复合资源随需要增补）
13. P3/P4 边界（M46 登记，S7b 的已知留口）：① IntrinsicOpcode（M57 起为 MapBinaryOp/MapUnaryOp）的 And/Or 直接发射表项保留——仅 and/or 被用户重载的不短路场景合法（S8+），内建 bool 短路已走 P4a 展开；② 值块穿透 return@ 的 GuaranteesValueReturn 按「路径终止」处理（更精细的路径类型分析留待后续）；③ 值块 if 转换双终止丢弃语句中的 LocalSymbol 仍全量平铺进 .vars（无害，verifier 阶段再核）
14. P3/P4 边界（M47 登记，S7c-1 的已知留口）：① return@ 隔循环边界拦截为诊断（当前脱糖只写值块局部、无法表达跳出中间循环；若规范另有意图如自动生成 break 链，回到 Binder 放宽——拦截点在 BindReturn 的 LoopDepth 比较）；② definite assignment 对 break/continue 后的同块语句不做流处理（按顺序继续绑定，不截断不改 assigned；不精确方向为保守，do-while 体尾集合可能多算 break 后的赋值）；③ GuaranteesReturn 对循环保守 false（`while (true)` 无 break 恒循环特例留口）；④ do-while 条件内的赋值效果（条件表达式含复合赋值时）保守丢弃——循环后状态 = 体尾集合，不含条件求值效果
15. P3/P4 边界（M48 登记，S7c-2 的已知留口）：① 循环变量 const 为只读默认（规范未明——若定稿可写，回 Binder for 分支的 isConst: true 放宽）；② init `_ -> field` 映射 P3 不落隐式赋值（stdlib 以显式赋值 init 规避，映射语义化留待后续——BindBody 对映射参数无处理）；③ 接口方法 override 匹配校验与访问控制（priv/protected/internal）检查归 S8（命中即放行；override 修饰符不进 BIL 声明——符号模型无标记位，§8.4 修饰符为可选集）；④ 其余数值类型（i8–u64/float/double）的 EnumerateInRange 与泛型 RangeEnumerator\<T\> 留 S9/后续（stdlib 当前仅 i32）；⑤ `?.`（SafeDot）与 wrapper `:`（Colon）段在实例链上色中仍拦截（归 S7f/S11）；⑥ 实例字段 const 赋值判定同全局字段技术债（符号 → 声明 AST 反向映射缺失）
16. P3/P4 边界（M49 登记，S7d 的已知留口）：① 混合 switch（常量与 pattern case 共存）整体走 pattern 链降级（§16.6 允许「降为 if 或多个结构化判断」；纯常量形态才用 switch-table——「常量段用表 + pattern 段用链」的混合 lowering 留待需要时优化，分流点在 LowerSwitchCore 的 Any(IsPattern)）；② 内建非基元类型 core::Exception 经 new/类型引用投影进 BIL 文本但不进 LocalSymbols（内建不声明是 M38 架构决策——基元靠别名投影闭合，Exception 是首个被引用的内建 class；声明策略归 verifier（S12）前定稿——**M58 已落定**：LocalSymbols 仍不声明，可解析性由 BilVerifier 预定义符号表闭合（bootstrap 符号即 BIL 内建环境））
17. P3/P4 边界（M50 登记，S7e 的已知留口）：① try+finally 部分终止的值块编织拦截为 P4 Error（TransformWithContinuation 穿过 try-finally 且 rest 非空——完整支持需 finally 复制编织，留待需要时落地，拦截点在 WeaveContinuation 的 try 分支）；② 语句 seq 不压值块标签栈——return@ 指向语句 seq 报未定义标签（规范未明语句 seq 是否可作 return@ 目标；若规范放行，回 BindSeqStatement 补压栈）；③ catch 变量与 finally(e) 变量只读默认 const（规范未明——若定稿可写，回 BindTry 的 isConst: true 放宽）；④ core.CastException 未进 stdlib（as 失败是运行时 CastException，P3 对可转性不做静态拒绝；具体异常子类归 S10，与 M49 异常根注记同源）
18. P3 边界（M54 登记，S8a 的已知留口）：① typeOf 的多段类型形态（`typeOf(ns.Type)`）未支持——仅单段裸名操作数做值/类型不落袋分类，多段路径按值形态直通（报 cannot be used as a value；多段类型支持留待需要时落地，分类点在 BindTypeOf 的裸名判定）；② is/supers/with 右侧动态形态的值路径带泛型实参按未命中处理（使用侧泛型归 S9，拦截点在 BindTypeCheckTargetValue 入口）；③ typeOf 非裸名操作数一律值形态（`typeOf((expr))` 经透明分组等同值形态，符合 spec 先值后类型口径）
19. M55 架构重构备注：① 协议 v2 修正——绑定遍历的 `scope`（词法环境）与 `expectedType`（期望类型）是 CRTP 基类 v1 签名遗漏的固有下传参数，定稿三基类承载（VISITOR_REWRITE §3）；② Lowerer 多趟 rewriter 链（ARCH §6.1 远期愿景）记为演进方向——趟间契约需重定义，S8 收官或 S13 时评估；③ context 方言接口初期从粗（BindContext + IFlowContext 一角），随 S8b 收窄表落地按需 extract 切细；④ 旧 LowerSession 的 SafeReceivers 手工压弹无 finally 保护（深层异常时栈泄漏），visitor 化时已修固为 try/finally；⑤ ~~`x == null`/`x != null` 无法绑定~~（M56 已落地：BinaryVisitor null 判等特例 + 装箱 cast §12.1）
20. P3/P4 边界（M59 登记，S8c 的已知留口）：① 索引复合赋值 receiver/index 多重求值（同字段复合既有行为；单次求值需另立脱糖，属语言语义决策）；② 容器中间段带后缀（`ns.Foo().bar` 形态）仍未支持（保留 S8 归口诊断）；③ 值调用（`local(0)(1)` 等函数值调用）未支持（Call-on-value 报 "P3: calling a value is not supported yet (S8)"）；④ 读索引要求 getAtIndex、写索引要求 setAtIndex（只读/只写索引器按各自存在性检查）；⑤ `(a+b).c` 端到端未覆盖——语言尚无用户二元运算符重载（P3 未落地），链式形态已由 `cb[0].value`、`makeBag()[9]` 覆盖

---

## 7. 里程碑历史

### 2026-08-03 · M59 S8c 索引访问 + 实例成员完整化

> 路线图 S8c 落地：索引读写（getAtIndex/setAtIndex 运算符）与表达式
> 底座链（`(a+b).c`/`foo().c` 等）三 pass 全通，解开 PathVisitors 现行
> 全部 S8 归口诊断；多参数索引定稿为编译错误并同步 SYNTAX §13.2。

- **P3 索引绑定**（`Semantic/Bound/BoundExpressions.cs` +
  `Semantic/Binding/Visitors/PathVisitors.cs`）：新 Bound 节点
  `BoundIndexExpression`（Receiver/Index/Operator/Type——读形态绑
  getAtIndex（Type = 返回类型），写形态绑 setAtIndex（Type = 元素
  形参类型））；`SymbolLookup.FindInstanceOperators(type, name,
  parameterCount)` 新增（BaseType 链 + ConstructedFrom 回退 +
  Kind/参数个数过滤），原 FindInstanceOperator 以其重写（行为不变）；
  BindIndexAccess——nullable receiver 拒绝、0 候选报
  "does not define an index operator"、多候选按 S8d 重载措辞、
  读复用 CallFacility.BindArguments、写模式定稿诊断
- **P3 实例成员完整化**（PathVisitors 重构）：表达式底座绑定
  （`(a+b).c`/`foo().c`/`new X().c`/`foo()?.bar`）；首段后缀折叠
  FoldSuffixes（Index → BindIndexAccess；Call-on-value 报
  "P3: calling a value is not supported yet (S8)"）；段后缀折叠
  （`.foo()[0]`/`.foo[0]`/`a?.b[0]`）；`this[i]` 读写；容器末段
  成员后缀折叠（`Type.staticField[0]`）；CallVisitors 的
  CallForm.TryGet 收紧——Call 后缀必须在链尾（`foo().c` 由此落入
  「调用结果底座」折叠）
- **P3 赋值 place 扩展**：place switch 加 BoundIndexExpression 分支
  （DeclarationVisitors.cs：无 const 检查/MarkAssigned/收窄失效）；
  复合赋值 place（BinaryVisitors.cs：要求 2 参 setAtIndex 存在 +
  元素类型 intrinsic 检查）
- **定稿结论**：多参数索引 `a[i, j]` 是编译错误（§13.2 签名固定单
  TIndex 参数，诊断经实参个数检查产生）；具名索引实参与普通调用
  同规则（按形参名归位）；SYNTAX §13.2 已同步。附带行为变化：
  多段 `_.x`（switch 占位）从容器路径报错改善为 selector 成员访问；
  `this(...)` 诊断措辞变为 calling-a-value 款
- **P4a**（`Lowering/Lowered/LoweredExpressions.cs` +
  `Rewriters/ExpressionRewriters.cs`）：新 Lowered 节点
  `LoweredIndexExpression`（Receiver/Index，Type 透传，不携带
  Operator——§13.6 指令无符号操作数）；IndexRewriter 恒等降级；
  赋值无需新语句节点（Target 走表达式分派）；复合赋值既有脱糖对
  索引目标自然成立（receiver/index 多重求值，与字段复合既有行为
  一致）
- **P4b**（`Bil/BilDataInstructions.cs` + `Lowering/Emitting/`）：
  新指令 `SetArrayInstruction`（§13.6：Collection/Index/Element，
  "set.array"）；ValueEmitters 加 IndexAccessEmitter（get.array）；
  StatementEmitters 的 AssignmentEmitter 加 LoweredIndexExpression
  分支（set.array，求值序仿 set.field：ELEMENT 先物化）；
  EmitDispatchers 注册；operator 的 §8.4 声明发射 S7c-2 已开闸
  （`$$名` canonical + operator(名) 修饰符），本步零改动
- **BilVerifier**（`Bil/BilVerifier.Types.cs`）：ClassifyVariables 加
  set.array 分类（reads Collection/Index/Element）；GetArray 非
  `.array<T>` 分支从报错替换为 §13.6 严格三元组查询（新
  VerifyIndexOperator 辅助：collection 类型不可解析或 extends 链断
  → 防误报降级通过；链完整则查 `$$getAtIndex`（恰 1 参）/
  `$$setAtIndex`（恰 2 参）——无候选报错、无 §6.4 精确匹配报错
  （禁止隐式转换）、多精确命中报不唯一、get 唯一命中再验
  RESULT ≡ 返回类型）；SetArray 对称分支（.array<T> 时
  ELEMENT ≡ 元素 T）；Flow.cs 无需动
- **测试**：BinderTests 加 TestIndexAccess（12 正例 + 10 负例）；
  LowererTests 加 TestIndexLowering；BilEmitterTests 加
  TestIndexEmission（CheckBilValid + CheckFnShape 黄金 + operator
  声明形态结构断言）；BilVerifierTests 加索引正例 + 四负例
  （手工模块基建 IndexModule/IndexOperator/BothIndexOperators）——
  全部扩既有套件，注册表未动
- **验证**：build 0 错误 0 警告；42 套件全绿
  （`dotnet run -- test --all`）；CLI `--emit-bil` 端到端实测
  （用户类双 operator + 读/写/复合索引 → 合法 BIL，
  get.array/set.array 行形态正确）
- **留口**（技术债 #20）：① 索引复合赋值 receiver/index 多重求值
  （同字段复合既有行为；单次求值需另立脱糖，属语言语义决策）；
  ② 容器中间段带后缀（`ns.Foo().bar` 形态）仍未支持（保留 S8 归口
  诊断）；③ 值调用（`local(0)(1)` 等函数值调用）未支持；④ 读索引
  要求 getAtIndex、写索引要求 setAtIndex（只读/只写索引器按各自
  存在性检查）；⑤ `(a+b).c` 端到端未覆盖——语言尚无用户二元运算符
  重载（P3 未落地），链式形态已由 `cb[0].value`、`makeBag()[9]` 覆盖

### 2026-08-03 · M58 BIL 验证器 BilVerifier + BIL 测试迁移（提前自 S12）

> 用户决策：不推进语言特性（S8c 暂缓），回补质量基建——落地 BIL
> 验证器并把全部 BIL 测试迁移到验证器框架，方便随时检查产出是否
> 正确。`Bil/BilInstructions.cs` 文件头预留的「结构化理解归
> BilVerifier（S12）」提前兑现；验证器只依赖 `Bil/` 目录
> （ARCH §6.3 字符串身份决策不变）。

- **验证器本体**（`Bil/` 五新文件，消费 BilModule 对象模型——
  模型层已保证 opcode/操作数形态与 blk/res 无悬空引用，验证器管
  模型无法表达的检查）：`BilVerifier.cs`（入口 `Verify(BilModule)`
  → `IReadOnlyList<BilVerificationError>`（Rule 取 § 号 + Context
  定位 + Message）+ §20.1 词法语法）+ `BilVerificationContext.cs`
  （模块级资源/类型/成员符号索引、函数级变量类型环境、canonical
  符号解析（方法/字段签名按 <> 深度切分）、类型引用工具、
  预定义符号表）+ `BilVerifier.Symbols.cs`（§20.2 符号——fn↔
  LocalSymbols 一一对应（native/接口/abstract 豁免）、static
  标记一致性、资源类型引用可解析、discriminant 资源登记 +
  §20.7 参数包顺序与签名一致 + §20.8 声明侧修饰符矩阵）+
  `BilVerifier.Types.cs`（§20.3：指令读写变量位置分类唯一表 +
  逐指令类型 switch——严格相等按 §6.4，invoke/new/init 签名匹配，
  继承字段/方法宿主经 IsAssignableTo）+ `BilVerifier.Flow.cs`
  （§20.4 保守 DA + §20.5 控制流——entrypoint 唯一与结构化
  终止、块成员资格、token 作用域、结构环拒绝 + §20.6 breakid
  capability——绑定位唯一、禁止普通读写、continue 不指 switch
  token）。覆盖 §20.1–20.8 静态可判子集（§20.9 VM 语义除外）
- **防误报降级原则**（宁可漏报不可误报）：含 `.generic<` 的
  typeid 位置表达式不做严格匹配；类型兼容剥基名大小写不敏感
  （`.i32` ↔ `core::i32`、`.any` ↔ `core::Any`）；extends/
  implements 链判定（IsAssignableTo）；查不到声明一律降级通过；
  DA 保守（loop/switch/try 子块出口取进入态；loop condition
  按 §20.4 由 judge 赋值建模，进入循环不要求已赋值）
- **预定义符号表**（技术债 #16 收口）：bootstrap 符号
  （core::Any/Object/Exception 等 + Any/Object$toString）经
  CanonicalSymbolPrinter 投影进 BIL 文本但不进 LocalSymbols——
  M38「内建不声明」架构决策不变，可解析性由验证器内建环境闭合
  （预定义类型 23 + 方法 2，仅注入符号集合，不生成声明对象，
  声明侧规则对其降级通过）
- **entrypoint 结构化终止判定**：§9.4「不得落尾」按结构递归——
  全分支 return 的 switch/if/try 之后发射器不补 ret 是 M46–M50
  的既定合法形态，末指令为这些结构且全分支终止即视为终止
- **防腐化**：指令分派双 switch（读写分类 + 类型规则）default
  均报「验证器未覆盖指令类型」——Bil 新增指令子类时强制提醒
  补规则（惯例同 ASTIntegrityValidator 类型审计）
- **测试基建** `Tests/BilTestHarness.cs`：`EmitBilUnit` 全管线
  驱动（自 BilEmitterTests 私有提升共享，与 CLI 管线同序）+
  `CheckBilValid`/`CheckBilInvalid` 验证器断言 + **res 重编号
  形状黄金**（`CheckFnShape`/`CheckResShape`——资源名按出现
  顺序重编号 `res(#0)`/`res(#1)`，消除 stdlib 基线 R_0..R_4
  偏移这一最脆弱点；指令序列/操作数/.tN 编号/block id 仍逐字节
  锁定，回归精度不降级）
- **测试迁移**：新套件 `Tests/BilVerifierTests.cs`（56 用例——
  15 个全管线正例零错误 + 最小手工模块基线 + 24 个 §20 逐类
  负例）；`BilEmitterTests` 全量迁移（157 用例——私有
  RenderFn/RenderFnAllBlocks/RenderResources/EmitUnit 删除，
  全模块黄金改验证器 + 结构断言，fn 级紧凑黄金改形状黄金，
  每个用例前置 CheckBilValid，TestGoldenOutput 的排版锁职责
  归 BilWriterTests）；`BilWriterTests` 黄金保留（排版是本职），
  §19 两自足模块补验证，四个排版抽样用例注明不过验证器；
  TestUnsupportedNodes 夹具修补（手工 MethodSymbol 未经 P1
  收集，验证器正确抓到声明缺失——验证器价值的首个实证）
- **CLI**：`--emit-bil` 在 BilWriter 前接 BilVerifier——产出非法
  即逐条 Logger.Error 输出、不落盘、退出码 1（验证失败 =
  编译器 bug，响亮失败）
- **验证**：build 0 错误 0 警告（同批修复 StdlibSourcesTests
  既有 CS8602——M57 段登记的「不在本次范围」此项收口）；
  42 套件 2074/2074 + fuzz 6000 全绿；CLI 端到端 hello world
  样例 `--emit-bil` 通过验证正常落盘；中端行为零变化（验证器
  为纯增量检查层，P1–P4 与 BilWriter 路径零改动，BIL 黄金文本
  不受影响）

### 2026-08-03 · M57 BIL 生成全模型对象化重构（去魔法 string）

> 用户决策的架构重构：P4b 与 Bil 模型从「opcode 字符串 + 位置操作数
> 列表」迁移为强类型对象模型。动机——opcode 字面量散落 50+ 处
> （拼错编译照过）、操作数位置式零类型检查（§13.3 get/set.field
> 不对称序易写反）、BilWriter 按 opcode 字符串 switch 排版、
> 修饰符/种类/资源头字符串散落生成器侧、blk/res 裸 id 悬空引用
> 不可静态发现。行为零变化（同 M55 迁移铁律）。

- **指令模型**（`Bil/BilInstructions.cs` 基类 + `BilComputeInstructions.cs`
  §11–§12 + `BilDataInstructions.cs` §13–§15 + `BilControlFlowInstructions.cs`
  §16，按规范章节分文件仿 Bound/Lowered）：`BilInstruction` 抽象基类
  （Origin 保持 object? + Opcode/Operands internal 抽象 + WriteTo
  统一单行/多行排版——§16.6 switch/§16.7 try 续行形态由
  FirstLineOperandCount 声明）+ 强类型子类族：每种指令的 opcode
  拼写、操作数个数/类型/顺序由构造签名固定（§13.3 读取 OBJECT TARGET
  序 vs 写入 SOURCE OBJECT 序等不对称序编译期锁死）；§11 运算
  `BilBinaryOp`/`BilUnaryOp` 枚举（intrinsic 映射归 EmittingFacility
  MapBinaryOp/MapUnaryOp 单点）；§12.3 TypeCheck 双形态拆
  Direct/Indirect 两子类（.indirect 后缀派生）；可选 block 位置
  （if-else/loop-enum/try-finally）为可空 BilBlock 属性，null →
  none 操作数由 Operands 合成——生成方不再接触 none。不建模
  §13.5/§14.2/§15.2–15.4/§17（简洁优先，随各自里程碑按同模式增补）
- **拼写唯一定义点**：`Bil/BilSpellings.cs`——全部枚举 → 标准
  拼写映射集中一处（运算/类型检查/类型种类/成员关键字/block
  修饰符/访问/关键字修饰符/标量类型），模型与 writer 不再出现
  拼写字面量
- **声明与资源对象化**：`BilTypeKind`/`BilMemberKind`/`BilBlockModifier`/
  `BilAccessibility`/`BilKeyword`/`BilScalarType` 六枚举 +
  `BilModifier` 子类族（访问/关键字/operator(名)/symbol("...")/
  lib("...")——getter/setter/enum-case/wrapper-proxy 修饰符随
  S8e/S11/S14 增补）；`BilSwitchTableResource`（§18.4 selector
  类型引用自渲染 header）与 `BilCatchTableResource`（§18.5
  `BilCatchEntry` 类型化条目自渲染 `type(T) -> blk(id)`）——
  EmittingFacility 的 `"switch-table<...>"`/`"type(...) -> blk(...)"`
  字符串拼接删除；`BilCollectionResource` 保留通用形态（array/pair/
  map，测试与未来里程碑用）；Metadata 与标量资源的类型关键字同枚举化
- **操作数对象引用**：`BilBlockOperand` 持 `BilBlock`、
  `BilResourceOperand` 持 `BilResource`（悬空 blk/res 引用构造期
  即不可能）；EmitEnvironment 资源去重表按种类四分（标量/null/
  switch-table/catch-table），值为资源对象
- **P4b 契约类型化**：EmitValueDispatcher 与各值 emitter 产物
  string → `BilVariableOperand`（§10.1 物化契约）；NewTemp 返回
  变量操作数；"<error>" 占位同形态保留；EmittingDriver 补 ret
  判定 `is not RetInstruction`
- **BilWriter**：opcode switch 删除——指令自渲染（WriteTo），
  种类/修饰符经 BilSpellings/Render()；排版行为逐字节不变
- **验证**：build 0 错误（`Tests/StdlibSourcesTests.cs` 一处 CS8602
  为 M57 前既有警告——未触碰文件的 AST API  nullable 流分析，
  不在本次范围）；41 套件 1979/1979 + fuzz 6000 全绿
  （BilWriterTests/BilEmitterTests 用例数不变——构造与查询改强
  类型，黄金文本一字未动）；CLI 端到端 hello/switch 两样例迁移
  前后 `--emit-bil` 输出 diff 字节一致 + try/插值样例合法 BIL
  核对；Bil/ 对 Semantic/Lowering/AST 零依赖保持（ARCH §6.3
  字符串身份决策不变——canonical 符号仍以字符串承载于
  fn/type/field/case 操作数）

### 2026-08-03 · M56 S8b smart cast 三 pass 全通（M55 定稿落地）

> M55 的专项定稿（`SMART_CAST_DESIGN.md`）按既定实现映射落地：
> P3 分析与标记 + P4a 显式 cast 物化（P4b 零新增）。SYNTAX §3.5
> 「支持智能转换」一句正式扩写为完整规则，§3.4 补 null 判等。

- **规范落地**（`docs/SYNTAX.md`）：§3.5 smart cast 完整规则（触发
  表/目标安全规则/不触发清单/失效/guard/循环/switch/await-lambda
  边界先行）；§3.4 新增「null 判等」段（`==`/`!=` 一侧 null 字面量
  合法 bool、非 nullable 放行恒 false/true、`null == null` 报错、
  BIL = cmp + null 资源 §18.1）
- **前置**：`FieldSymbol.IsConst`（P1 收集写入）+ P3 const 字段
  赋值检查（init 构造方法体内 this 实例字段豁免——构造期一次性
  赋值；重复赋值精确检查留后续）——兑现 M41 技术债 11 的 const
  判定部分；null 判等绑定（BinaryVisitor 特例：null 定型
  Nullable\<T0\>、非空侧装箱视图 cast §12.1、两侧统一满足 §11.5
  严格相同）——兑现 M55 技术债 19⑤
- **P3 核心**：`FlowState` 收窄事实表（NarrowKey = 根（局部/参数/
  this/静态字段符号）+ const 字段链；**纯交集合并**——收窄非单调
  （失效移除键），before 中被分支失效的键不得复活，与 DA 的
  before∪∩ 规则相区分）+ `ConditionFactsExtractor`（绑定后 Bound
  条件 → 真/假边事实对：is 真边/null 判等双边/and∪∩/or∩∪/not
  翻转）+ `BoundSmartCastExpression` 标记节点（Type = 收窄类型，
  成员解析自然在其上进行）
- **收窄应用点**：if 语句（分支入口事实 + **guard 反向传播**——
  分支终止取对边流；DA 规则不变保行为兼容）与 if 表达式（无
  guard，纯交集）；and/or 右侧绑定上下文（`(x is String) and
  (x.length > 0)` 右侧在收窄类型上解析）；while（体入口真边 +
  「体赋值根」保守剔除——回边规则；出口不收窄）与 do-while/for
  （剔除体赋值根）；switch `(_ is T)` 分支体 selector 收窄（Q4——
  分支体内 `_` 不可用是 §7.2 既有语义，覆盖 selector 可收窄场景）；
  var 局部/参数赋值与复合赋值失效（含以其为根的字段链）；字段链
  稳定判定（每层 const + IsNarrowable + init 体内排除；var 根允许
  ——赋值失效覆盖；带访问器属性的排除判定归 S8e 细化）
- **P4a 物化**：`BoundSmartCastExpression → LoweredCastExpression`
  （isSafe: false——T? → T unwrap 与子类型收窄同属 §12.1 形态，
  复用 M51/M52 模式）；**P4b 零新增**（cast 发射 S7e 已备）
- **验证**：新套件 `SmartCastTests` 55 用例（注册表 #41——定稿
  规则逐条：is/null 判等/and-or-not/guard return+throw/失效/const
  字段/var 字段不收窄/while/do-while/switch 占位/不触发形态/分支
  合并/null 判等绑定/物化）；全量 41 套件 1979/1979 + fuzz 6000
  全绿；CLI 端到端三样例（guard、and 组合、switch 占位）逐行核对
  BIL（unwrap cast 物化 + null 资源 cmp + type.is 六形态）合法；
  开发期发现并修复两处收窄遗漏（多段路径头与调用链头 receiver
  未包装——测试驱动捕获）
- **文档**：SMART_CAST_DESIGN 落地偏差记录（Q4 澄清/var 根允许/
  访问器判定归 S8e）；ROADMAP S8b 标完成

### 2026-08-03 · M55 中端三树 visitor 化重构（Binder/Lowerer/BilEmitter）+ S8b smart cast 专项定稿

> S8b 前置架构重构（用户决策）：Binder（2890 行）/Lowerer（1331 行）/
> BilEmitter（1045 行）三个 session 巨石类全部 visitor 化——任何方法可碰
> 任何栈的状态污染面已构成维护性灾难风险。同批完成 S8b smart cast 的
> 语言规则专项定稿（`docs/compiler/semantic/SMART_CAST_DESIGN.md`），
> 落地本身归下一里程碑。

- **架构方案（用户提出，讨论定稿，`docs/compiler/semantic/VISITOR_REWRITE.md`）**：
  - **CRTP 协议基类**：所有结构 visitor 继承 `XxxVisitor<TSelf, TResult, TContext>`
    （`where TSelf : ..., new()`）；基类静态 `Visit` 为唯一入口——创建子类
    实例 + 模板化管理生命周期（Enter/Exit 配对，栈压/弹 finally 固化，
    杜绝手工配对泄漏）；外部调用者（上一层 visitor 或驱动器）经分派器路由
  - **双协议**：`Visit → TResult?`（上行合成，常态——Bound/Lowered 树
    自下而上组装）+ `VisitInto(shell)`（施工壳填充：值块/循环壳先建压栈
    给 return@/break 命中、体绑完回填）；否定「target+result 单签名统一」
    （90% 调用点会传无意义 target）
  - **context 方言**：CRTP 泛型参数指定上下文类型；`BindEnvironment`
    （只读，全编译期不变）固定共享；方言 = **同一函数级状态对象的接口
    视图**（禁止多方言对象状态分裂——DA/标签栈是跨 visitor 共享可变
    状态）；初期从粗（一个 BindContext + IFlowContext 一角），按需
    extract 切细。函数级状态对象化：每函数体 new 一个 BindContext/
    LowerContext/EmitContext——旧 session 的「防御性清空」由此消失
  - **类别分派器 + 结构 visitor 两层**：分派器（Expression/Statement/
    Block）是唯一 switch 所在（对应旧 BindExpression/BindStatement/
    EmitStatement/EmitValue）；结构 visitor 之间不直接互调，一律经
    分派器；簇级分文件（每文件 1–4 个 visitor，仿 Parser 层文件惯例）
  - **被否定的方案**：Roslyn 式超大 partial class（文件拆分但单类状态
    共享，污染面仍在）；60 个绑定器对象的组合式（互递归遍历下组合退化
    为全互联，样板倍增）；Lowerer 多趟 rewriter 链（ARCH §6.1 远期
    愿景）记为演进方向（趟间契约需重定义，值块编织区有 miscompile
    风险），S8 收官或 S13 时再评估
  - **迁移策略**：停线重写 + Binder 先行全链验证协议后复制（用户决策）；
    跨会话交接文档先行（两份设计底稿达「另一会话读文档即可续作」标准）
- **协议 v2 修正**（动工时发现）：绑定遍历有两个 v1 签名遗漏的固有
  下传参数——`scope`（当前词法环境，随块嵌套变化，等价 Parser 施工
  目标）与 `expectedType`（期望类型下传：null 字面量定型、return/
  赋值/实参目标类型传播，仅表达式消费）；定稿三基类（通用
  BinderVisitor/表达式 ExpressionVisitor 追加 expectedType/壳
  BinderShellVisitor）。Lowerer/Emit 侧无此两参（无词法作用域概念）
- **Binder 落地**（`Semantic/Binding/`：23 文件 3373 行，原 2890）：
  骨架（BinderVisitor 三基类/BindEnvironment/BindContext/IFlowContext/
  FlowState/Scope/Dispatchers/BoundAnalysis/BindingDriver）+ 共享设施
  （SymbolLookup 实例成员与泛型字段替换/MemberLookup 名字解析查找序/
  TypeReferences）+ `Visitors/` 11 簇（Literal/Declaration/Conditional/
  Loop/Switch/TrySeq/Binary/Path/Call/TypeCheck）。**FlowState 提取**
  （assigned DA 集 + Snapshot/Restore/MergeIfBranches/MergeBranches——
  S8b 收窄表的家，DA 与收窄同为流敏感事实同生命周期）；壳协议首验
  （ValueBlockShell 包装承载诊断构造名，Bound 节点零改动）；
  SwitchMatchContext 载荷 + visitor 实例字段记录压栈（任务局部状态
  对象化范例）；复合赋值迁移中修复三处保真偏差（毒化静默位置/诊断
  消息文本——「先完整读旧实现再写」纪律确立）
- **Lowerer 落地**（`Lowering/`：14 文件 1847 行，原 1331）：LoweredVisitor
  基类（无 scope/expectedType——Lowered 遍历无词法作用域）+
  LowerContext（outputStack 前置语句机制/值块/循环/switchTemps/
  safeReceivers 四映射栈/transformFailed）+ `Rewriters/` 8 簇。
  continuation 编织（TransformStatements 家族）归 ValueBlockFacility
  静态设施（LoweredTree 就地变换，非 visitor 遍历）；LoopRewriter/
  ForLoopRewriter 合成顺序保真（`.sN` 先于 `.bN`——SynthLocals 顺序即
  `.vars` 发射顺序，BIL 文本快照敏感，Enter 内按旧序合成）；旧代码
  SafeReceivers 无 finally 手工压弹修固为 try/finally（架构升级红利）
- **BilEmitter 落地**（`Lowering/`：9 文件 1474 行，原 1045）：EmitVisitor
  基类（签名带施工目标 `BilBlock target`——本树天然下行填充，三树中
  与协议贴合度最高）+ 分层上下文（EmitEnvironment 模块级：Module +
  ResourceKeys 跨 fn 去重；EmitContext 函数级：TempVars/各 block 计数）
  + Unit 占位（语句发射 TResult）+ `Emitting/` 3 簇
- **验证（行为零变化铁律）**：三树各自完成时 `test --all` 40 套件
  1924/1924 全绿（Binder 重写一次通过；BilEmitter 120 用例黄金文本
  逐字节一致）；build 0 错误、重写引入 0 新警告（StdlibSourcesTests
  CS8602 为 HEAD 预先存在）；**测试零改动**；旧参考文件（Legacy*.txt）
  全部删除。BilEmitter 由子代理执行、主代理抽查验收（协议一致性 +
  复杂 emitter 保真度 + 全量测试）
- **S8b smart cast 专项定稿**（`SMART_CAST_DESIGN.md`，2026-08-03 用户
  逐条拍板）：Q1=**B**（v1 即含字段收窄——仅 const 字段 + backing
  field 直访 + receiver 稳定链，var 字段别名赋值不可控不收窄，与
  Kotlin 同口径）/Q2=A（guard 模式：终止分支反向传播）/Q3=A（and/or/
  not 全支持，含 and/or 右侧绑定上下文）/Q4=A（switch pattern `_ is T`
  占位收窄）；无争议项（supers/with/动态 is 不触发、is 真边蕴含非空、
  else 边无差类型、while 体带真边 do-while 不带、lambda/await 规范先行
  实现归 S13）；**前置缺口发现**：`x == null`/`x != null` 现状无法绑定
  （BindBinary 同类型规则 + null 无上下文），§3.4 null 判等补规范+
  绑定随 S8b 落地；实现映射：FlowState 收窄表 + ConditionFactsVisitor
  （条件事实提取器，天然 visitor）+ BoundSmartCastExpression 标记 +
  P4a LoweredCastExpression 物化（P4b 零新增）

### 2026-08-01 · M54 S8 细化 + S8a（is/supers/with + typeOf 三 pass）

> S8「P3 完整化」按「近细远粗」约定细化为 S8a–S8f（SEMANTIC_ROADMAP，
> 2026-08-01）；S8a 落地类型谓词与运行时类型三 pass——BIL §12.3
> （`type.is`/`type.supers`/`type.with` + 三 `.indirect`）与 §12.5
> （`getid.var`/`getid.type`）首次发射。前端零改动（TypeCheck/
> TypeOf AST 节点 M8/M34 已备），Bil/ 零改动（通用 opcode 模型
> + BilOp.Type/Var 操作数直接承载新指令）。

- **规范定稿**（`docs/SYNTAX.md`）：
  - §3.5 新增「is/supers/with 的右侧解析规则」：右侧可为类型引用或
    `Type\<T>` 值（动态类型测试）——名字先按类型引用解析，失败再按
    值绑定且值必须承载 `Type\<T>`，同名时类型优先；`with` 类型引用
    必须 wrapper 类型；`is`/`supers` 不做静态不可能性拒绝（与 as 的
    可转性口径一致，`12 is String` 不报错）；
  - §3.5 注记：`is .Case` 是隐藏判别字段整数比较（RUNTIME §16.3）
    而非类型检查，不触发 smart cast；
  - §3.7 typeOf 补双形态定稿：操作数先按值绑定（常态，返回
    `Type\<T静态\>`，T 为类型边界——BIL §6.3 `.typeid<TBound>` 语义），
    无法绑为值且可解析为类型引用时取类型形态（`Type\<T\>`）；
  - 笔误修订：§3.5 castFrom 示例返回类型（TSource → 目标类型，
    示例包入宿主 `class Celsius`）；§9.2 修饰符表补 `override` 行
    （§19 关键字表本已有）。
- **P3**（`Semantic/Bound/BoundExpressions.cs` + `Semantic/Binder.cs`
  + `Tests/BoundDescribe.cs`）：
  - BoundTypeCheckExpression：Kind 三态（Is/Supers/With）+ Operand +
    TargetType（静态）/TargetValue（动态，Type 为 `Type\<T\>`）互斥
    双槽（构造时恰一个非 null，CompilerInternalException 守卫），
    Type 恒 bool；BoundTypeOfExpression：Operand（值形态）/TargetType
    （类型形态）互斥，Type = `Type\<T\>` 构造类型
    （SymbolGraph.GetConstructedType 驻留）；
  - BindTypeCheck：静态形态经 ResolveSymbolPath（reportErrors: false）
    不落袋试探（ErrorTypeSymbol 系 TypeSymbol 子类，显式排除——本步
    抓出的初版误编译缺陷）；`T?` 目标包 `Nullable\<T\>`；with 静态
    目标 Kind != Wrapper 诊断；动态形态 BindTypeCheckTargetValue
    不落袋纯查找（单段：局部（含 DA 未赋值检查）→ 参数 → FindField
    全链；多段：静默容器解析 + 末段字段），命中后正常构造值引用，
    类型必须 ConstructedFrom == TypeDefinition；
  - BindTypeOf：单段裸名操作数（非 this、无泛型/后缀/段）先值后
    类型不落袋分类，其余形态一律值形态；两不沾走 BindPath 自然报
    Undefined name；ErrorType 毒化静默；
  - `is .Case`（TargetCase 非 null）落 P3 归口诊断（S11）；
  - switch pattern 中 `_ is X` 自然贯通（占位 `_` 作 Operand 不特殊
    处理）。
- **P4a**（`Lowering/Lowered/LoweredExpressions.cs` + `Lowerer.cs`
  + `Tests/LoweredDescribe.cs`）：LoweredTypeCheckExpression/
  LoweredTypeOfExpression 同构两节点（Type 自带，仿
  LoweredCastExpression），恒等重写（TargetValue/Operand 递归降级，
  无脱糖）。
- **P4b**（`Lowering/BilEmitter.cs`）：EmitValue 加两 case（同 cast
  模式，NewTemp 物化 `.tN` + Origin 塞 LoweredNode）——静态
  `type.X VALUE type(TARGET) RESULT`，动态先物化 typeid 变量再
  `type.X.indirect VALUE TYPEID_VAR RESULT`；typeOf 值形态
  `getid.var VALUE RESULT`、类型形态 `getid.type type(SYMBOL) RESULT`；
  `Type\<T\>` 经 bilStandardConstructor 投影 `.typeid<T>` 进签名。
- **测试**：BinderTests +TestTypeCheck/TestTypeOf（双形态快照 +
  结构断言（符号 ReferenceEquals、互斥槽、恒 bool）+ 诊断四例）；
  LowererTests +TestTypeCheckLowering/TestTypeOfLowering（快照 +
  Origin 回指 + 递归降级断言）；BilEmitterTests +TestTypeCheckEmission
  /TestTypeOfEmission（六种形态黄金 BIL 逐行——先 `--emit-bil` 冒烟
  取真实输出核对 §12.3/§12.5 后落断言）。

### 2026-08-01 · M53 插值词法帧机制（前端回补：Lexer 层栈嵌套解析）

> 按用户决策对 M51 的插值前端做架构重构：**插值解析从「Parser 侧拆分」
> 改为「Lexer 层栈嵌套」**——字符串层遇 `${` 压基础层正常词法，驱动按
> token 层大括号计数配平弹回。StringInterpolationSplitter（配平扫描/
> 子词法/span rebase/子解析，~280 行）与 StringToken.RawContent/
> MultilineIndent/IsMultiline 全部删除；SYNTAX §3.8 的「单行宿主内插值
> 不得含未转义 `"`」限制解除。AST 与 P3/P4 全链零改动（段结构同构）。

- **Lexer**（`Lexer/Tokens.cs` + `Lexer/Lexer.cs` + `Lexer/LexerLayers.cs`）：
  - InterpolationStartToken（`${`）/ InterpolationEndToken（配平 `}` 改发）
    两标记 token；
  - 驱动插值帧栈（ContextImpl.interpolationDepths）：PushToken 单点维护
    ——开始标记压帧、帧内 `{`/`}` 记号计数（**token 层计数使字符串/
    字符/注释内容天然豁免**，它们不产生记号 token）、归零改发结束标记
    并请求弹帧根 Base 层；嵌套插值经开始标记递归压帧；EOF 帧未归零
    先于冲刷报 "Unclosed interpolation"；
  - StringLexerLayer：挂起 `$` 延迟判定（下一字符是 `{` 即引导，否则
    补入内容——`\$`/闭界/`\` 边界统一）+ 帧触发（段产出 + 开始标记 +
    压 BaseLexerLayer）+ 首段 span 修正（开界引号不属于段内容）与段尾
    修正（引导 `$` 不属于段内容）；
  - MultilineStringLexerLayer：累积阶段挂起 `$` 同款判定 + 段结算产出
    （原文暂存保序，**闭界确定缩进基准后统一回填**——词法先于解析
    全量完成，回填天然安全；仅首段的段首行参与剥缩进）+ 首段 span
    修正；无插值时保持单 token 与含闭界 span 的既有行为。
- **Parser**（`Parser/LiteralParserLayer.cs`）：段序列状态机
  （AwaitInterpolationStartOrDone / InterpolationEndExpected /
  AwaitSegmentOrStartOrDone）——InterpolationStart 建插值节点并委托
  ExpressionParserLayer 就地填充段 Root（主 Parser 流内正常委托，
  allowBareReturn 传染）；文本段解码后为空的跳过；普通单段路径不变。
- **删除**：`Parser/StringInterpolationSplitter.cs`（整文件）、
  StringToken.RawContent/MultilineIndent/IsMultiline/HasInterpolation
  （词法标记随帧机制退役；AST 的 HasInterpolation 由 Parser 设置）。
- **SYNTAX §3.8 修订**：插值表达式按普通 Latte 词法解析——任意字面量
  （嵌套字符串两态宿主均可）、嵌套 `{}`（lambda/seq）、注释、跨行
  （§1.1 续行规则）全允许；`"a${"b"}c"` 自此合法。
- **测试**：6 新增（单行嵌套字符串/嵌套插值/lambda 大括号/多段连续 +
  多行嵌套插值/lambda）+ 迁移——错误路径四例改帧机制消息（Unclosed
  interpolation/Unexpected token at start of expression/Expected '}' to
  close interpolation/嵌套字符串就近报）、词法断言改 token 流形态
  （InterpolationStartToken 存在性）、span 断言对齐段 span 修正
  （段不含引号与引导 `$`）。

### 2026-08-01 · M52 S7f 收官：`?.` 安全调用 + `if?` 空值回退 + 解构声明

> ROADMAP S7f 收官（剩余三项全部落地，P3/P4 同步 + Parser 回补），
> 同批定稿 nullable 的 BIL 语义空白（null 检查形态、装箱/展开 cast）
> 与泛型成员访问的最小规范条款。**S7f 四项（插值/`?.`/`if?`/解构）
> 全部端到端出合法 BIL**。

- **spec 修订**：BIL §18.1 定稿 `null type(T)` 资源的类型语义即
  `.nullable<T>`（null 检查 = `cmp.eq`/`cmp.ne` + null 资源，天然满足
  §11.5 严格相同，§3.4「nullable 检查」落地）；§12.1 补 nullable
  装箱/展开为内建引用视图转换（展开遇 null 抛 core.CastException——
  Latte 层 `nullableVar as T` 同语义）；§13.3 补泛型宿主字段访问的
  实参替换判定条款；SYNTAX §3.4 补 `?.`/`if?` 定型规则（`?.` 结果
  不二次包装、可空值无隐式成员访问须逐段标注；`if?` 左 Nullable\<T\>
  右可赋值到 T 结果恒 T、右侧延迟求值）。
- **`?.`（P3/P4）**：BoundSafeAccessExpression（Receiver/Placeholder/
  Access）+ BoundSafeAccessReceiverExpression 占位叶子（仿 S7d switch
  `_` 占位先例）；BindInstanceChain 分派 SafeDot 段——receiver 必须
  Nullable\<T\>，段在非空 T 上经占位绑定（字段/单调用后缀），结果
  类型成员已可空则原样否则包 Nullable；普通段遇可空 receiver 报
  「use '?.'」专门诊断；P4a LowerSafeAccess 脱糖（物化 receiver
  `.sN` + 结果局部前置 null + `if (cmp.ne recv, null)` + 占位映射栈
  替换为 unwrap cast + wrap cast——嵌套 `a?.b?.c` 逐层命中）。
- **`if?`（Parser 回补 + P3/P4）**：ExpressionParserLayer 中缀 `if`
  重组（IfNullFallbackSeen 态期待 `?`，参照 as? 模式；`a if? b if? c`
  按无优先级报错；HandleCompleted 同认 if）；BinaryExpressionASTNode
  Operator `"if?"`；P3 BindNullFallback（左 Nullable\<T\>、右
  IsAssignable 到 T、结果恒 T）；P4a LowerNullFallback（物化左侧 +
  `if (cmp.ne) { cast unwrap } else { 回退值 }`——延迟求值由 if 结构
  保证）。
- **解构（stdlib + Parser + P3/P4）**：core.Pair\<TKey, TValue\> 进
  `.bootstrap.latte`（namespace core 头 + open class + key/value
  字段，用户决策的自举位；编译器按 canonical 名硬编码参照，同 M48
  IEnumerable 先例）；Parser 解构分支（VariableDeclarationParserLayer
  三新状态，`(` 分流；DestructuringDeclarationASTNode 不新建——
  VariableDeclarationASTNode.DestructureNames 互斥字段，同 TypeCheck
  双槽先例）；P3 BindDestructuring（沿 BaseType 链找 Pair 构造、
  名字数恒 2、分量类型 = 构造实参特判）+ **构造类型成员查找
  ConstructedFrom 回退**（FindInstanceField/FindInstanceMethods/
  FindField/FindMethods 统一——构造器不复制成员列表的既有空白）+
  **泛型字段类型最小替换**（SubstituteFieldType：声明类型是宿主泛型
  参数时按 receiver 链构造实参定型——S9 前置特判，Pair 子类 init
  写基类字段因此可用）；P4a LowerDestructuring（pair 物化 `.sN` +
  逐字段读取，BIL §3.4「精确字段读取」；
  LoweredFieldAccessExpression/LoweredLocalDeclarationStatement origin
  放宽 BoundNode 承载合成路径）。
- **测试**：51 新增——Expression `if?` 重组（快照 + 优先级/缺 `?`
  错误）+ VariableDeclaration 解构（快照 + 结构 + 三错误路径）+
  Binder 三分区（`?.` 字段/方法/链式 + 两诊断；`if?` 单用/组合 + 两
  诊断；解构闭环 + init 替换断言 + 三诊断）+ Lowerer 三分区（`?.`
  字段/链式脱糖、if? 脱糖、解构脱糖快照）+ BilEmitter 三分区（
  null 资源/cmp.ne/unwrap/wrap cast 黄金 fn + 解构 get.field +
  init set.field）+ StdlibSources Pair 结构断言 + hello world 黄金
  文本 core::Pair 段迁移 + BoundDescribe/LoweredDescribe 新节点格式
  （SafeAccess/SafeReceiver/NullFallback/Destructuring/Const(null,T)）。

### 2026-08-01 · M51 S7f-1 字符串插值 + toString 机制 + String 拼接 + 子类型 cast 物化

> ROADMAP S7f 第一刀（字符串插值），P1–P4 全链路贯通；同批按用户
> 决策落地 **toString 机制 spec 定稿**（SYNTAX §3.8）与 **String 内建
> 拼接开放**（bootstrap Add intrinsic），并补齐 ARCH §6.1 规划已久的
> **子类型 cast 物化**（BIL §6.5）。

- **spec 修订**：SYNTAX 新增 §3.8（toString 全类型承诺挂 Any、基元
  标准文本、未覆写返回类型 canonical 名、插值 = 非 String 段 toString
  后拼接、单行宿主内插值表达式不得含未转义 `"`、表达式跨行同 §1.1
  续行规则）；RUNTIME §26 原生方法面扩为三个（`toString(value: Any):
  String` 内建承载：Any 接口承诺 + Object open 默认实现 native 路由，
  override 经虚派发绕开原生面）；BIL §21.5 hook 表加 `latte_rt.toString`
  + §11.2 `.string` 的 `add` 定义为内建字符串拼接。
- **Lexer**（`Lexer/Tokens.cs` + `Lexer/LexerLayers.cs`）：StringToken
  新增 RawContent（解码前源字符切片：单行逐字符含转义对、多行含缩进
  源行按 \n 拼接）/ MultilineIndent（闭界剥除基准）/ IsMultiline——
  插值拆分的定位底稿，span 可逐字符精确映射回源。
- **AST**（`AST/LiteralNodes.cs` + `AST/AstJsonlDeserializer.cs`）：
  StringInterpolationPart（[AstCarrier]，互斥双字段：Text = 段级
  LiteralExpression 子结构（与普通字符串字面量同构，P3/P4 复用字面量
  机器）/ Expression = ExpressionRootASTNode）+
  StringLiteralASTNode.InterpolationParts（null = 无插值）；JSONL 反
  序列化器支持 null 列表成员按需创建（GetOrCreateList）。
- **Parser**（`Parser/StringInterpolationSplitter.cs` 新文件 +
  `Parser/LiteralParserLayer.cs`）：RawContent 一体化扫描（转义对
  解码、多行行首剥缩进、未转义 `${` 引导）→ 表达式配平截取（嵌套
  `{}` 计数，字符串/字符/行注释/块注释内容不计）→ 子词法 → span
  rebase（首行加列偏、offset 平移）→ 子解析（私有 EOF 垫底层 +
  ExpressionParserLayer；allowBareReturn 按 M33 规则经
  LiteralParserLayer 传染）。单行宿主内插值含 `"` 在词法层即闭合
  宿主（语言固有限制，已写进 §3.8；嵌套字符串请用多行宿主）。
- **bootstrap**（`Semantic/Symbols/BootstrapSymbols.cs`）：String
  intrinsicOps 加 Add（`"a" + "b"` 与 `s += "b"` 同步开放）；
  Any.Methods 挂 toString 接口承诺（无体）+ Object.Methods 挂 open
  默认实现（native `latte_rt.toString`，符号直造同 Exception 先例）。
- **P3**（`Semantic/Binder.cs`）：BindStringInterpolation——绑定即
  规范化（参照 M48 `a to b` 先例）：字面量段走普通字面量绑定；非
  String 段包 toString() 实例调用（FindInstanceMethods 沿 BaseType 链
  静态绑定最近声明，运行期虚派发）；全 String 段左结合折叠为
  BoundBinaryExpression(Add) 链；段表达式正常参与绑定（未定义名照常
  诊断、void 段报「必须产值」）。**P4 对插值零新增节点**。
- **P4a**（`Lowering/Lowerer.cs`）：子类型 cast 物化（ARCH §6.1
  「source-level 子类型赋值/传参 → 显式 cast」首个落地）——统一
  EnsureDeclaredType：receiver（≠ 方法宿主，如 i32 调 Any.toString 的
  装箱 cast）/ 实参（≠ 形参）/ 局部初始化 / 赋值 / return 五位置；
  hello world 黄金文本两处 return 因此严格化（RangeI32 →
  IEnumerable\<i32\> 等显式 cast，BIL §6.5 合规）。
- **测试**：41 新增——Literal 插值分区（多段/单段/调用段 + 结构/span
  断言 + JSONL 往返 + 错误路径四例）+ MultilineString 多行插值（剥
  缩进共存/跨行表达式/嵌套字符串）+ Binder 插值分区（拼接链/toString
  包装/String + 开放/显式调用/void 段）+ Lowerer 插值与 cast 物化分区
  （装箱 cast/return/初始化三位置）+ BilEmitter 插值端到端（黄金
  fn 快照：cast .any + invoke Any.toString + add 链）；快照迁移
  `Str("...",interp)` → `StrInterp(...)`（拆分段序列描述）。

### 2026-08-01 · M50 S7e cast 最小闭环 + try/catch/finally + seq

> ROADMAP S7e 落地，P3/P4 同步推进；cast（as/as?）最小闭环按用户
> 决策提前自 S8（「没法脱离 cast 实现其他功能」）。try/catch/finally
> 与 seq 双形态全链路贯通——**自此 SYNTAX §7 控制流（if/switch/
> 循环/try/seq/throw）全部端到端过 P1–P4**。

- **P3 cast/try/seq 绑定**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：BoundCastExpression（as/as? 统一承载，IsSafe 区分；
    as 结果即目标类型、as? 结果 Nullable\<T\>——P3 定型，P4 不再
    区分包装）+ BoundTryStatement/BoundCatchClause +
    BoundSeqStatement/BoundSeqExpression + BoundValueBlock.IsVolatile；
  - cast：可转性不做静态拒绝（as 失败是运行时 core.CastException，
    castTo/castFrom 名字分析归后续）；ErrorType 毒化静默；
  - try：catch 类型 IsAssignable 到 bootstrap Exception；catch 变量
    与 finally(e) 变量 const 只读且命中即 assigned（finally 变量
    类型 Nullable\<Exception\>）；DA 合并 = before∪(try∩全 catch)
    ∪finally；
  - seq：语句形态直通 BindBlock（作用域/assigned 语义与裸块相同），
    不压值块标签栈（return@ 指向语句 seq 报未定义标签，规范未明，
    登记技术债）；表达式形态复用 BindValueBlock 值块语义（标签同源
    Label ?? "_"）+ IsVolatile 置位 + 必须产值检查；using 拦截归 S13；
  - GuaranteesReturn/GuaranteesValueReturn/EnumerateStatements 扩展
    覆盖 try/catch/finally 与 seq 块。
- **P4a 降级**（`Lowering/`）：LoweredCastExpression（Type 自带）/
  LoweredTryStatement/LoweredTryCatch/LoweredSeqBlock 四节点；
  try 合成 ExceptionSlot——finally(e) 时 slot 即 finally 变量、
  否则合成 `.sN`（Nullable\<Exception\>）；有名 catch 体头编织
  「变量 = cast slot」合成赋值（Origin 指 BoundCatchClause）；
  seq 语句/volatile 恒等、seq 表达式脱糖为前置 seq 块写合成结果
  局部 + 原位置读局部（复用 S7b session 前置语句基建）；
  try+finally 部分终止的值块编织拦截——TransformWithContinuation
  深处触发 transformFailed 标记（void 链路无法返回值传播），诊断
  落袋后 LowerValueBlock 放弃产物、函数体跳过。
- **P4b 发射**（`Lowering/BilEmitter.cs`）：`cast`/`cast.safe`
  三操作数 SOURCE RESULT type(TARGET_TYPE)（§12.1/§12.2，结果
  物化 .t 临时变量）；seq → `call blk(seqN)`（§16.1 不建栈帧，
  块落尾自然返回续 call 的下一条），volatile → §9.6 block 修饰符；
  `try` 四操作数 blk(body)/$slot/res(表)/blk(finally)|none
  （§16.7，块 id `try0-body`/`try0-catchN`/`try0-finally`）；
  §18.5 `catch-table` 多行资源（BilCollectionResource multiline，
  元素 `type(T) -> blk(...)` 保序——表序即匹配序，空 catch 列表
  出空表）——RegisterCatchTable 复用 resourceKeys 同元素序列去重；
  BilWriter 零改动（try 多行排版与多行资源打印已随 S4 就位）。
- **测试**：Binder 套件 221 → 270（TestCast/TestTry/TestSeq：as/as?
  定型/catch 类型兼容/catch 变量 const/seq 必须产值/using 拦截
  各负例 + 快照）；Lowerer 套件 84 → 112（TestCastLowering/
  TestTryLowering/TestSeqLowering/TestTryWeaving：slot 合成三形态/
  catch 头 cast Origin/脱糖形态/编织拦截）；BilEmitter 套件
  82 → 97（TestCastEmission/TestTryEmission/TestSeqEmission 黄金
  多 block 文本 + try 四操作数/call 单操作数/catch-table 保序与
  多行形态结构断言）；CLI 端到端样例（try/catch/finally(e) +
  seq 语句/表达式 + as/as?）逐行核对 §12.1/§12.2/§16.1/§16.7/§18.5
  一致。

### 2026-08-01 · M49 S7d switch 语句/表达式 + throw

> ROADMAP S7d 落地，P3/P4 同步推进：switch 语句/表达式（值匹配 +
> `_` pattern 双分类）与 throw 全链路贯通，异常根 `core.Exception`
> 定稿进 bootstrap（`IsOpen` 可继承——具体异常子类归 S10 stdlib，
> 边界注记同步 ROADMAP S7d/S10）。自此 SYNTAX §7 控制流仅剩
> try/catch/finally 与 seq（S7e）未通。同批修复 M46 遗留的
> else-if 链值块编织 miscompile。

- **异常根定稿**（`Semantic/Symbols/BootstrapSymbols.cs`）：
  `Exception` 属性（TypeKind.Class、baseType: Object、isBuiltin、
  `IsOpen = true`），注册进 Core.Types 列表——throw 兼容性检查的
  锚点；ROADMAP S7d「进 bootstrap 还是 stdlib 在本步定稿」的抉择
  落地为 bootstrap（与 Object/ValueType 层级根同列），S10 只收
  具体子类。
- **P3 switch/throw 绑定**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：BoundSwitchStatement/BoundSwitchCase/
    BoundSwitchExpression/BoundSwitchExpressionCase/
    BoundThrowStatement/BoundSwitchPlaceholderExpression（`_` 占位——
    Selector 回指 selector 表达式做嵌套消歧，Type = selector 类型）；
  - switch 绑定：switchSelectors 占位栈（BindPath 单段 `_` 命中
    栈顶）；BindSwitchMatch 值匹配/pattern 显式分类（值匹配限
    编译期常量且类型严格等于 selector 类型；pattern 必须 bool）；
    两形态强制 default（RequireSwitchDefault）；分支产值类型统一
    与 DA 分支合并复用 if 基建（MergeBranches = before ∪
    (∩全部分支)）；ContainsSwitchPlaceholder 走
    AstStructureReflection.EnumerateChildren 统一下钻；
  - throw 绑定：IsAssignable 到 bootstrap Exception，不兼容即诊断；
  - GuaranteesReturn/GuaranteesValueReturn 终止口径扩展：throw 与
    全分支（含 default）return 的 switch 视为终止；
    EnumerateStatements 递归 switch 分支体；
    BindValueBlock/CollectBranchValueType 加 construct 参数区分
    "if expression"/"switch expression" 消息。
- **P4a switch 降级**（`Lowering/`）：LoweredSwitch（全值匹配
  恒等，携 .breakid 合成局部 + DefaultBody）/LoweredSwitchCase/
  LoweredThrowStatement 三节点；LowerSwitchCore 以 Any(IsPattern)
  分流——常量形态恒等，pattern 形态走 LowerPatternSwitch（selector
  物化 `.sN` 前置 + switchTemps 映射栈）+ BuildPatternChain 递归
  嵌套 if 链（值分支条件 = 合成 cmp.eq，Origin 指 match 常量——
  LoweredBinaryExpression 加可选显式 Type 参数，透传会是错的）；
  switch 表达式结果局部 `.sN` + 前置语句复用 S7b session 基建；
  throw 恒等。
- **M46 miscompile 修复**（必要前置，同批落地）：else-if 链值块
  在 if 转换中后续分支错误覆盖结果局部（CLI 实证 c2 路径 .s0 被
  覆盖为 x）；TransformStatements 重写为 continuation 编织——
  TransformWithContinuation/WeaveContinuation/HasTerminatingPath/
  BlockTerminates（终止口径含 throw+switch），终止分支织空
  continuation、非终止分支织 rest、顶层值块写入与 throw 截断后续；
  LowererTests 加 TestElseIfChainTransform 回归。
- **P4b switch/throw 发射**（`Lowering/BilEmitter.cs`）：switchCount
  块编号（`switch0-itemN`/`switch0-default`）；switch 指令 §16.6
  五操作数（selector/res(表)/[blk item 表]/blk(default)/breakid，
  操作数序即 BilWriter 规范排版序）；RegisterSwitchTable——§18.4
  `switch-table<T>` 单行资源（BilCollectionResource，Multiline:
  false），元素经 RenderLiteral 复用 §18.1 渲染（表元素只进表不
  产标量资源），同（header, 元素序列）跨 fn 去重（复用
  resourceKeys 字典，TypeKeyword = "switch-table" 与标量键不冲突）；
  throw §16.9 单操作数（异常值物化后 `throw $e`，entry 块 throw
  终止不补 ret）。
- **测试**：Binder 套件 198 → 221（TestSwitch/TestThrow：值匹配
  常量限定/类型严格相等/pattern bool/产值统一/default 强制/throw
  兼容性各负例 + 快照）；Lowerer 套件 69 → 84（TestSwitchLowering/
  TestThrowLowering/TestElseIfChainTransform）；BilEmitter 套件
  66 → 82（TestSwitchEmission 黄金 + 跨 fn 同表去重/
  TestPatternSwitchEmission 无 switch 指令断言/TestThrowEmission）；
  CLI 端到端三样例（常量 switch/pattern switch 表达式/throw new
  core.Exception()）逐行核对 §16.6/§16.9/§18.4 一致。

### 2026-08-01 · M48 S7c-2 实例成员最小闭环 + IEnumerable + for 双形态

> ROADMAP S7c-2 落地，P3/P4 同步推进：实例成员最小闭环（this/
> 实例调用/实例字段访问）+ core.collections 迭代协议 stdlib +
> for 双形态统一脱糖。自此 Latte 的面向对象面（实例方法/实例字段/
> 构造）与第一种「库级协议驱动」的语言结构（for-each）全链路
> 贯通——stdlib 三源（.bootstrap 基元自举 + Console + collections）
> 全量同走 P1–P4，自身即最完整的端到端用例。

- **第 0 步早期验证**（四项，全部一次通过）：ext+operator Parser
  组合直接合法（无需动 Parser）；`.bootstrap.latte` 被 csproj 通配
  正常内嵌、LogicalName/sourceName 反推无异常（无需改 csproj）；
  §8.4 init/operator 声明形态读取（operator 用 `$$名` canonical +
  `operator(名)` 修饰符，init 用普通 canonical `$init@.void` +
  `init` 修饰符）；init `_ -> field` 映射 P3 无任何处理（stdlib
  以显式赋值 init 规避，技术债登记）；接口无体方法 P1/P2 零拦截
  （无需开闸）。
- **P3 实例成员与 for 绑定**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：BoundThisExpression/BoundInstanceCallExpression/
    BoundFieldAccessExpression；BoundLoop 扩展 For 路径（Condition
    可空——形态互斥注释约定；LoopVariable(const)/Iterable/协议
    三方法符号挂好，P4 不做名字分析，ARCH §11.3 纪律）；
    BoundCallStatement 加 Receiver?；
  - this：宿主统一取 method.Owner（普通成员 = 声明类型，ext 方法
    = 目标类型——AttachToExtTarget 后即定）；静态上下文诊断；
  - 实例链上色：receiver 静态类型沿 BaseType 链查找（接口
    receiver 查接口成员；ext 注册成员同路径——P2 已挂目标类型
    成员表）；裸名实例字段/方法补 this（FindField/FindMethods
    宿主链改 method.Owner）；MatchSingleCandidate 提取共享；
    ContainsGenericParameter 统一 S9 拦截；访问控制归 S8；
  - for 双形态：范围循环 a to b → 类型一致检查 + EnumerateInRange
    ext operator 实例调用（Iterable 节点）；for-each 协议判定——
    实现 core.collections.IEnumerable\<TItem\>（**含「Iterable 类型
    自身即构造」分支**：operator 返回类型的静态类型就是接口）；
    循环变量 const（只读默认，规范未明）；DA for 后 = before。
- **P4a for 脱糖**（`Lowering/`）：实例三节点恒等降级（
  **LoweredInstanceCallExpression 的 Type 自带**——for 脱糖合成
  节点的 Origin 是 BoundLoop 语句，无法走 Origin 透传）；for 统一
  脱糖：前置 `.e = iterable.iterate()`（枚举器类型经
  GetConstructedType(IEnumerator 定义, TItem)，定义取
  MoveNextMethod.Owner）+ LoweredLoop 复用（Judge =
  `.c = .e.moveNext()`、Body 头 = `item = .e.current()`、
  Enumerator = null）——P4b 零新增。
- **P4b 实例发射开闸**（`Lowering/BilEmitter.cs`）：实例方法 fn
  定义（.args 顺序 .return → **.this = OwnerType 投影** → 普通
  参数，§9.2/§7.3——ext 成员同以 .this 表示被扩展值）；实例
  invoke（receiver 求值作首实参；接口方法 canonical 引用，分派
  归 Middleware）；get.field/set.field（§13.3 操作数序）；
  this → `$.this` 零指令；init/operator §8.4 声明形态开闸
  （getter/setter 仍跳过）；**EmitBuiltinExtMembers**（定稿外
  新增）：内建类型（IsBuiltin 不声明）的 ext 成员以 §8.4.1 裸
  条目输出——否则其 fn 定义引用未声明符号（反射枚举
  BootstrapSymbols 公共 TypeSymbol 属性，新内建类型自动覆盖）。
- **stdlib 三源**（与用户源同走 P1–P4）：`.bootstrap.latte`
  （基元自举源，SYNTAX §15.3：ext operator i32.EnumerateInRange）
  + `core/collections.latte`（IEnumerable\<T\>/IEnumerator\<T\>
  双接口 + RangeEnumeratorI32（**started_ 标志防 start-1 下溢**）
  + **RangeI32**（定稿外新增——operator 声明返回 IEnumerable 而
  枚举器只实现 IEnumerator，类型不兼容且双接口语义要求每次
  iterate() 产独立枚举器；RangeI32 正是可重入的枚举源））。
  **形态区分**：Latte 源码一律点号路径（`core.collections.IEnumerable`，
  对齐 §13.2 的 `core.ComparisonResult` 惯例）；`::` 仅属
  canonical/BIL 侧（§14.8）。
- **测试**：Binder 168→198、Lowerer 58→69、BilEmitter 57→66、
  StdlibSources 30→47（「恰好 1 棵」重写为三源结构断言）；既有
  黄金基线按 stdlib 新资源段（R_0–R_4 固定）重算 10 处 + hello
  world 黄金全文重生（含 stdlib 六 fn：ext operator/init×2/
  moveNext/current/iterate）；全量 1634/1634 + fuzz 6000/6000
  （40 套件）。端到端验证：临时 `s7c2_check.latte`（范围循环
  println + RangeI32 for-each）经 `--emit-bil` 出双 loop 多
  block BIL（合成局部独立、iterate 前置、协议 invoke 接口
  canonical），逐行核对后已删。

### 2026-08-01 · M47 S7c-1 while/do-while/break/continue

> ROADMAP S7c-1 落地，P3/P4 同步推进，同批完成循环协议定稿
> （SYNTAX §7.3/§13.2/§15.3：范围循环半开 [a,b)、`to` 即
> EnumerateInRange、IEnumerable\<T\>/IEnumerator\<T\> 双接口、
> 基元实现归 SDK 自举源——for 双形态本身归 S7c-2）。本步交付
> while/do-while/break/continue 的全链路：循环是第一种「条件求值
> 必须重复执行」的结构，由此确立 Judge 块机制与 .breakid capability
> 的合成局部表达。

- **P3 循环绑定**（`Semantic/Bound/BoundStatements.cs` +
  `Semantic/Binder.cs`）：
  - 新节点：`BoundLoop{Kind, Label, Condition, Body}`（Kind 复用 AST
    LoopKind；**施工壳**——循环标签栈要求壳先于体绑定存在，体内
    break/continue 引用命中壳，仿 BoundValueBlock 先例）+
    `BoundLoopControl{IsBreak, Target}`（引用相等即身份）；
  - 循环标签栈（BindSession）：体绑定前压壳、绑完弹栈回填；
    break/continue 无标签命中栈顶（栈空报 outside of loop）、named
    从内向外查（未命中报未定义标签）；穿透值块命中外层循环合法
    （BIL §16.5 动态结构作用域）；for 报 not supported yet (S7c-2)；
  - 条件 bool 检查与 if 共用 CheckBoolCondition（加 construct 参数）；
  - definite assignment 循环两规则（S7c-1 定稿）：while 后 = before
    （体可能零次执行，条件赋值效果同保守）；do-while 后 = 体尾集合
    （体至少一次，条件在体后求值、其效果保守丢弃）；
  - GuaranteesValueReturn 扩展：末语句 BoundLoopControl 视为路径
    终止（该语句必以值块外循环为目标）；GuaranteesReturn 循环保守
    false（`while (true)` 特例留口，注释标记）；
  - **return@ 隔循环边界拦截**（定稿外新增）：值块栈记 LoopDepth，
    return@ 命中时当前循环更深即诊断——脱糖只写值块局部，无法
    表达跳出中间循环，放行会产语义错误的 BIL（S7c 技术债）。
- **P4a 循环降级**（`Lowering/`）：`LoweredLoop{IsRev, Judge,
  Condition, Body, Enumerator?, BreakId}` + `LoweredLoopControl
  {IsBreak, BreakId}` 新节点；
  - Judge 块：条件表达式经复用的 LowerAssignInNewBlock 在独立块
    上下文降级并写合成 bool 条件局部（.sN）——条件内短路/if
    表达式的前置语句天然落 Judge（§16.3：每次读取 CONDITION 前
    由 JUDGE_BLOCK 赋值）；
  - **.breakid 局部的 LocalSymbol 表达方案**：`LocalSymbol.Type`
    放宽为 `TypeSymbol?`（null 仅限 .breakid capability，§9.3 无
    对应 TypeSymbol），.bN 独立计数器命名；5 处连锁——emitter
    .vars 平铺 null → `.breakid`、Lowered 值引用透传 null →
    CompilerInternalException（capability 不可读）、Binder 构造
    Bound 值引用 `local.Type!`、两描述器 TypeShort 收可空显示
    `.breakid`；
  - BoundLoop → BreakId 映射栈（仿值块映射栈）；break/continue 是
    BIL 真跳转，穿透值块零展开——**对 if 转换与值块截断逻辑零
    改动**（块内其后指令自然不可达；IsValueBlockWrite 不误判）。
- **P4b 循环发射**（`Lowering/BilEmitter.cs`）：`loop`/`loop.rev`
  （§16.3/§16.4：操作数序 cond/body/none/judge/breakid，块 id
  `loop0-body`/`loop0-judge` 函数内递增，Enumerator 非 null 时
  `blk(loop0-enum)` 分支已写全、本步恒 none）+ `break`/`continue`
  （§16.5）+ `.vars` 的 `.breakid` 条目（§9.3）。
- **测试**：Binder 142→168、Lowerer 43→58、BilEmitter 45→57；全量
  1567/1567 + fuzz 6000/6000（40 套件）。端到端验证：临时
  `s7c1_check.latte`（while 累加 + continue/break@outer + do-while
  + println）经 `--emit-bil` 出 loop0/loop1 + if0/if1 多 block BIL，
  逐行核对 §16.3/§16.4 操作数序、§16.5 跳转、§9.3 `.breakid` 条目、
  §9.4 落尾规则后已删。

### 2026-08-01 · M46 S7b if 语句/表达式 + 值块 + 短路 and/or + 复合赋值

> ROADMAP S7b 落地，P3/P4 同步推进：P3 新增五个 Bound 节点与值块
> 标签栈 return@ 绑定、definite assignment 分支合并、GuaranteesReturn
> 双分支升级；P4a Lowerer 由静态恒等重写改为 session 化（前置语句
> 机制 + 合成局部 `.sN`），落地短路展开、值块降级与 if 转换、复合
> 赋值脱糖；P4b BilEmitter 出多 block BIL（§16.2 if 指令）。控制流
> 的第一种形态端到端全通。

- **P3 节点与绑定规则**（`Semantic/Bound/` + `Semantic/Binder.cs`）：
  - 新节点：`BoundIfStatement{Condition, ThenBlock, ElseBlock?}`
    （else if 链包成单语句 BoundBlock）/`BoundValueBlock{Label, Block,
    Type}`（值块：M33 隐式取值判定——单 ExpressionStatement 块隐式
    取值，多语句块必须显式 return@）/`BoundIfExpression{Condition,
    ThenValue, ElseValue}`（产值类型统一，双分支必须兼容）/
    `BoundReturnValueStatement{Label, Value}`（显式 return@ 取值）/
    `BoundCompoundAssignmentExpression{Op, Target, Value}`；
  - 值块标签栈：Binder 维护值块标签栈，return@/return@_ 沿栈解析
    目标值块，未命中报 P3 诊断；GuaranteesValueReturn 检查多语句
    值块所有路径显式取值（穿透 return@ 按「路径终止」处理）；
  - definite assignment 分支合并：if 后状态 = before ∪ (setThen ∩
    setElse)，无 else 分支 = before；GuaranteesReturn 升级识别双
    分支均终止的 if；
  - 复合赋值：读语义（unassigned 检查）+ intrinsic 键查询（同二元
    运算），写回目标必须是可写局部/字段。
- **P4a 脱糖与 if 转换**（`Lowering/Lowerer.cs`，静态类改实例
  session：前置语句列表 + 值块映射栈 + 合成局部计数器）：
  - 短路 and/or 按 §11.3 展开为 if 块：左操作数只求值一次，结果
    物化到合成局部 `.sN`（BIL §5.1 编译器保留名，与用户标识符零
    冲突）；
  - **算法决策——`LoweredConstantExpression` 的由来**：展开备选
    「`s = a` 再判 s」会让左操作数二次求值、副作用重复，故否决；
    引入常量节点承载 bool 字面量初始化（常量自带 Type，不走
    Origin 透传；S7b 仅 bool 一种形态）；
  - 值块降级与 if 转换：「值块写入」判定 = 赋值目标是值块映射栈
    中的 `.s` 局部（短路/if 表达式结果局部不在栈上，互不混淆）；
    单分支终止时把后续语句移入不终止分支末尾，无 else 则新建，
    双终止全丢弃；嵌套块递归处理；
  - 复合赋值脱糖为前置赋值语句（表达式值 = 写回后值）。
- **P4b 多 block 发射**（`Lowering/BilEmitter.cs`）：§16.2
  `if $c blk(then) blk(else)`，无 else 用 none 操作数；block id 取
  `if0-then`/`if0-else` 形态（无点号，与 `.entry` 区分）；分支块
  落尾不补 ret；合成 bool 常量与字面量同键去重进 Resources。
- **测试**：Binder 98→142、Lowerer 26→43、BilEmitter 31→45；全量
  1514/1514 + fuzz 6000/6000（40 套件）。端到端验证：临时
  `s7b_check.latte`（`var r = if ((x > 0)) { return@_ 1 } else {
  return@_ 2 }` + `(r > 0) and (x > 0)` + 无 else if 语句 + `x += r`）
  经 `--emit-bil` 出 if0/if1/if2 多 block BIL，人工逐行核对后已删。

### 2026-08-01 · M45 S7a P4 基础发射补齐

> ROADMAP S7a 落地：P3（S5）能绑定的全部 Bound 节点在本步过 P4——
> Lowered 节点补齐、Lowerer 恒等重写全覆盖、BilEmitter 新发射
> 赋值/运算/带返回值调用/new 与 §18.1 标量资源全形态。控制流的前置
> 就此就绪：没有赋值/运算/调用/new 的发射，任何控制流端到端用例都
> 写不出来。`Tests/LoweredDescribe.cs`（唯一 Lowered 树描述器，仿
> BoundDescribe）同步落地，供 S7b+ 脱糖用例消费。

- **`Lowering/Lowered/` 补齐八类节点**（字段仿对应 Bound 节点，Origin
  必填机制不变）：语句 `LoweredLocalDeclarationStatement{Local,
  Initializer?}` / `LoweredExpressionStatement{Expression}` /
  `LoweredAssignmentStatement{Target, Value}`；表达式
  `LoweredFieldReferenceExpression{Field}` /
  `LoweredBinaryExpression{Op, Left, Right}` /
  `LoweredUnaryExpression{Op, Operand}` /
  `LoweredCallExpression{Method, Arguments}` /
  `LoweredNewExpression{Init?, Arguments}`。
- **`Lowering/Lowerer.cs`**：分发覆盖 S5 全部 Bound 语句与表达式
  （六语句 + 七表达式），仍为恒等重写——bool 短路 and/or 等脱糖归
  S7b+ 独立 rewriter；未覆盖节点保持 P4 Error + 跳过函数体行为。
- **`Lowering/BilEmitter.cs` 新发射**（统一风格：表达式求值结果先物化
  到 `.t` 临时变量，再经 set.var 写入目标）：
  - 局部声明：无初始化器 → 无指令（.vars 已声明）；有初始化器 →
    求值 + `set.var $t $x`（§13.2）；赋值：局部目标 `set.var`、
    全局/static 字段目标 `set.field.static $src type(OWNER)
    field(FIELD)`（§13.4）；
  - 字段读取 `get.field.static $t type(OWNER) field(FIELD)`；OWNER
    投影：static 字段 = 宿主类型 canonical，命名空间全局字段 =
    命名空间全名（§13.4 未规定全局字段宿主形态，以命名空间全名
    投影，verifier（S12）再核）；根全局命名空间字段无宿主可投影——
    规范空白，报 P4 Error 而不发明语法；
  - §11 运算：`BilIntrinsicOp` → opcode 单点静态映射表（add/sub/mul/
    div/opposite/and/or/not/bin.*/shift.*/cmp.* 全 21 成员），形态
    `add $a $b $t` / `opposite $a $t`。**已知过渡**：内建 bool 短路
    and/or 当前直接发 and/or——§11.3 要求的「if + 临时变量」展开是
    S7b P4a 脱糖职责；
  - 带返回值调用 `invoke fn(CANONICAL) $t [args]`（§15.1）；new
    `new type(TYPE) $t [args]`（§14.1，init 选择归 Middleware，发射
    不写 init 符号）；表达式语句求值物化后结果丢弃；
  - §18.1 标量资源全形态：bool（`bool true`）、char（`char 'A'`，
    转义表同字符串外加单引号）、f32/f64（round-trip 格式保精度，
    f32 先收窄回 float 再打印避免双精度尾巴）、null（`null
    type(CANONICAL)`，CANONICAL = P3 定型的 Nullable\<T\> 之 T 的
    canonical，走 BilNullResource；去重键机制不变）。
- **测试**：`Tests/LoweredDescribe.cs`（唯一 Lowered 描述器，格式与
  BoundDescribe 对齐）+ `Tests/LowererTests.cs`（26 用例：每类节点
  Lowered 形态快照 + Origin 回指/字段与 init 符号引用相等结构断言 +
  未覆盖节点负例——S7a 后源码无法触达 default 分支，以测试私有
  Bound 子类注入模拟「未来节点」）；`Tests/BilEmitterTests.cs` 扩至
  31 用例（声明+初始化器、var 推断、赋值、算术+比较、一元、带返回值
  invoke、表达式语句、new、bool/f64/f32/char/null 资源、static 字段
  读写，均走 EmitUnit 全管线 + fn 指令/.vars/Resources 精确文本比对；
  原「二元运算未覆盖」负例已失效，改为实例方法 .this receiver 负例）；
  TestRunner 注册表尾部追加 ("Lowerer", LowererTests.RunAll)。

### 2026-07-31 · M44 S6 P4 最小闭环（端到端 hello world 出 BIL）

> ROADMAP S6 落地：P4 = Lowerer（P4a 恒等重写）+ BilEmitter（P4b 发射），
> 中端四 pass（P1 声明收集 / P2 声明解析 / P3 函数体分析 / P4 降级与发射）
> 全部打通——hello world 从 Latte 源码端到端产出合法 BIL 文本，
> 黄金输出与 BilWriter 排版逐行一致。本里程碑建立在 M43 stdlib 内嵌源
> （core.io::Console 与用户源同走 P1–P4）之上；CLI `--emit-bil` /
> `--sema-only` 接线在本里程碑收尾落地，S6 就此收官。

- **`Lowering/Lowered/`（三文件，仿 `Semantic/Bound/` 分文件风格）**：
  `LoweredNode{Origin: BoundNode}`（必填回指，ARCHITECTURE §6.1）+
  `LoweredExpression`（`Type` 直接透传 `((BoundExpression)Origin).Type`，
  不冗余存储）+ `LoweredStatement` + `LoweredFunctionBody{Method, Locals,
  Body}`（非 LoweredNode，仿 BoundFunctionBody）。节点只收 S6 最小集
  五类：`LoweredBlock` / `LoweredCallStatement{Method, Arguments}` /
  `LoweredReturnStatement{Value?}` / `LoweredLiteralExpression`（无额外
  字段，值经 Origin.Syntax 的 LiteralExpressionASTNode.Literal 取）/
  `LoweredValueReferenceExpression{Symbol}`——不镜像 Bound 全部节点，
  其余种类随 S7+ 脱糖落地增补。
- **`Lowering/Lowerer.cs`（P4a）**：`Lowerer.Lower(unit, bodies)` 静态
  入口，恒等重写五类节点（含嵌套 BoundBlock 与 return 值表达式）；
  遇未覆盖节点 → P4 Error 诊断（消息含 "not supported by minimal
  lowering (S7)"）并跳过整个函数体（不产出 LoweredFunctionBody）——
  函数体之间诊断互不阻断，与 P3 同原则。§6.1 脱糖清单（短路展开、
  cast 插入、复合赋值、字符串插值等）随 S7+ 逐项落地为独立 rewriter。
- **`Lowering/BilEmitter.cs`（P4b）**：`BilEmitter.Emit(unit, bodies,
  moduleName) → BilModule`，私有 EmitSession 承载全部状态：
  - **Metadata**：`module = string "<moduleName>"`（§4.1）。
  - **LocalSymbols**：从 GlobalNamespace 递归平铺（类型含 NestedTypes
    → 子命名空间 → 本空间全局字段/函数裸条目 §8.4.1）；跳过 IsBuiltin
    （bootstrap 基元经 .string/.i32 别名投影，不是符号引用）与 ErrorType。
    kind 映射五种类；修饰符 = 访问（全显式 pub/protected/internal/priv）
    + open/abstract/singleton + rich/shared（wrapper 恒 rich 也显式输出，
    §8.2）；**extends 与种类默认基类相同则省略**（确认 P2 行为：P1 建壳
    即填默认基类 class→Object / struct→ValueType / enum struct→Enum /
    wrapper→Wrapper，P2 仅覆盖显式继承），不同才输出
    `extends <PrintType>`；implements 逐个输出。方法成员：IsNative →
    `native symbol("...") lib("...")` 三件套（§8.4）；entrypoint 判定 =
    全局命名空间裸 main；`Kind != Regular`（init/operator/getter/setter）
    第一版报 P4 Error 跳过该成员。ExternalSymbols 本阶段恒为空段。
  - **Resources**：字面量提取（§4.2 指令不得内联字面量）——键 =
    (BIL 资源类型关键字, 字面量原文) 去重，名 = R_0/R_1... 按
    （bodies 顺序 + 树内先序）首次出现编号。字符串值取 Syntax 解码后
    Value，按 Lexer StringEscape 同集逆向重新转义为 BIL 原文
    （`\n` 输出为转义形态）；整数按 IntType 八值映射关键字（i32 等，
    §18.1 类型关键字无前导点）。float/bool/char/null 第一版报 P4 Error。
  - **Functions**：每 LoweredFunctionBody → BilFunction——`.args`
    （`.return` 在前，void 写 `.void`；非 static 实例方法需 `.this`，
    第一版报 P4 Error 跳过）；`.vars`（Locals 在前、临时变量在后）；
    单 `.block entry entrypoint`：LoweredCallStatement → 实参从左到右
    EmitValue 物化 → `invoke.noret fn(...) [$a, ...]`；return →
    `ret` / `ret $x`；嵌套块语句平铺。EmitValue：字面量 → 登记资源 +
    新临时变量（`.t0`/`.t1` 编译器保留前缀，§5.1 与用户变量零冲突）
    → `load res(R_k) $.tN`；值引用 → 直接 `Symbol.Name`。void 函数
    末尾无 ret 补 `ret`（§9.4：entrypoint block 不得落到末尾——
    stdlib println 体无显式 return）。每条指令 Origin = 对应
    LoweredNode（语句级；load 的 Origin = 字面量 LoweredNode），
    BilInstruction.Origin 保持 object? 不收窄（Bil 对中端零依赖，§6.3）。
  - 符号引用一律经 CanonicalSymbolPrinter 投影；有 P4 Error 时 Emit
    仍返回模块（§8 门槛：调用方不推进写盘，测试只跑无错路径）。
- **`Tests/BilEmitterTests.cs`（新套件，注册表 39 号）**：全管线
  helper（StdlibSources.ParseAll + 用户源组 CompilationUnit → P1 →
  P2 → P3 → P4a → P4b → BilWriter；用户源带文件名经
  `TestHarness.ParseRoot(code, sourceName)` 重载——注意是重载而非
  可选参数：方法组 `Select(TestHarness.ParseRoot)` 的类型推断依赖
  单签名）。覆盖：hello world 黄金输出逐行精确比对（Console 类型
  声明单行无 extends——与默认基类相同即省略的断言内嵌其中；两个
  fn 定义——println 实参直接 `$text` 无临时变量、末尾补 ret；main
  load/invoke.noret/load/ret）；Origin 调试链（invoke.noret.Origin
  is LoweredCallStatement → .Origin is BoundCallStatement → Syntax
  非空 → Span.sourceName == 用户文件名；load.Origin is
  LoweredLiteralExpression）；资源去重（相同字面量只登记一次、两处
  load 引用同一 R_1）；负例（`return (1 + 2)` → P4 Error 含
  "not supported"，main 无 fn 定义而 stdlib println 照常发射）。
  TestHarness 增 `Lines(...)` 黄金文本拼装（与 BilWriterTests 同源风格）。
- **CLI 接线（S6 收官，`Core/Commands.cs`）**：新增 `--emit-bil <路径>`
  （1 参）与 `--sema-only`（0 参）子命令（均与 `--parse-only` 互斥，
  注册进 `CompileCommand.SubCommands`，help 文本程序生成）；`Execute`
  的 `!parseOnly` 分支接入完整语义管线——stdlib（在前）+ 用户源组
  CompilationUnit → P1 → P2 → P3 → 诊断经 Logger 输出（格式
  `{sourceName}:{行}:{列} [{Phase}] {Message}`，走 stderr 不污染
  stdout）→ 有 Error 退出码 1 不发射；`--sema-only` 到此为止；
  无 Error 且 `--emit-bil` → P4 → BIL 文本写盘（UTF-8 无 BOM），
  moduleName 取首个 `--file` 文件名去扩展名。端到端实测：hello.latte
  发射产物与 BilEmitterTests 黄金输出逐行一致；P2/P3 错误路径
  诊断格式与退出码正确。CommandLineParser 套件增补子命令清单与
  互斥用例（46 → 50）。
- **未改动**：Bil/ 与 Semantic/ 现有文件零改动（依赖方向
  `Lowering → Semantic`、`Lowering → Bil` 单向保持）。

### 2026-07-31 · M43 native 函数机制 + stdlib 内嵌源载入

> 取代 ROADMAP S6 原「硬编码 `core::Console.println` external 符号」临时
> 措施（用户定稿）：Latte 获得正式的 native 函数语法，stdlib 以 .latte
> 源文件内嵌随编译器载入（S10 机制最小子集提前），println 在 Latte 层
> 包装运行时原生方法面 `latte_rt` 的 print/printErr；BIL VM 未来经
> 内建 hook 表直接执行，无需原生库即可跑通 hello world。四份规范文档
> 同步修订（SYNTAX / BIL_STANDARD / RUNTIME / SEMANTIC_ROADMAP）。

- **语言规范（`docs/SYNTAX.md` §4.6 新增）**：`native` 函数修饰符 +
  `@NativeLibrary("...")`（必填）/ `@NativeSymbol("...")`（可省，缺省
  取函数名）编译器内建注解（非 wrapper 体系）。规则：必须无函数体；
  成员形态必须 static；禁止 init/operator/getter/setter/async/泛型/
  重载；参数与返回类型白名单（§3.2 整数、浮点、bool、char、String）；
  注解实参必须各为一个字符串字面量。§9.2 修饰符表与 §19 关键字表
  同步收录 `native`。
- **BIL 规范（`docs/BIL_STANDARD.md`）**：§8.4 方法修饰符表新增
  `native` / `symbol("...")` / `lib("...")`（native 声明不得有 fn 定义、
  symbol/lib 必随 native 各出现一次）；§8.4.1 新增全局函数/全局字段
  的裸 `.method`/`.field` 段内声明形态（补规范空白）；§20.2 补 verifier
  条目；§21.5 新增 VM 内建 hook 表（`(latte_rt, print)` → stdout、
  `(latte_rt, printErr)` → stderr，表外组合拒绝执行）。
- **运行时约定（`docs/RUNTIME.md` §26 新增）**：native 互操作 =
  C 编写的 `latte_rt` shim 库（libc ↔ Latte 调用约定，暂定 fastcall，
  细则归 Middleware 阶段）；第一版原生方法面仅 `print`/`printErr`
  两个定参函数（不做可变参数 printf_s）；VM 不链接原生库、经内建
  hook 执行。
- **前端**：`Parser/Keywords.cs` 加 `NATIVE`（入 DeclarationDescriptors
  白名单）——无体函数与 `@注解` 路径前端本已具备（interface 无体方法
  先例），Parser 层零改动；TypeDeclaration 套件加 3 个 native 用例
  （95 → 98）。
- **符号与校验（P1/P2）**：`MethodSymbol.IsNative`（P1 建壳读标记位，
  仿 IsStatic）+ `NativeSymbol`/`NativeLibrary`（P2 读注解后填，仿
  WrapperTarget）；`DeclarationResolver` 新增 `CheckNativeDeclarations`
  子任务（挂 CheckModifiers 后）：§4.6 全规则校验 + 注解解析写符号
  （@NativeSymbol 缺省取函数名）；`CheckWrapperApplications` 为两个
  内建注解加豁免（否则误报 "is not a wrapper type"）。同批落地
  `SemanticSymbol.Accessibility`（Public/Protected/Internal/Private，
  P2 由声明修饰符写入、默认 Private 与 §16 一致）——BIL 发射
  （pub/priv 投影）与 S8 使用点访问控制的前置。DeclarationResolver
  套件 138 → 179（native 全规则正反用例 + Accessibility 组）。
- **Binder（P3）**：`FindMethods` 查找序补「宿主类型成员」一环
  （沿 BaseType 链，先于命名空间链，对齐 ARCHITECTURE §2 既定查找序
  「块 → 参数 → 成员 → 全局 → import」）——stdlib println 体内裸名
  调用同类静态方法 print 由此绑定；实例方法命中由 BindCallee 既有
  静态性检查自然拦截。Binder 套件 90 → 98（裸名静态调用 / 实例拦截 /
  类容器多段路径调用 / 遮蔽优先级）。
- **Bil 模型**：`BilSymbolSectionEntry` 抽象落地 §8.4.1
  （BilTypeDeclaration 与 BilMemberDeclaration 均归之），
  `LocalSymbols`/`ExternalSymbols` 放宽为段条目列表；BilWriter
  WriteSymbolSection 分派裸成员输出（段内一级缩进，类型体内输出
  逐字不变）；native 修饰符即字符串列表项（`"native"`,
  `"symbol(\"print\")"`, `"lib(\"latte_rt\")"`），模型零结构改动。
  BilWriter 套件 6 → 7。
- **stdlib 载入**：`stdlib/core/Console.latte`（core.io::Console：
  priv static native print/printErr（lib `latte_rt`）+ pub static
  println 两次 print 包装，避开 String 拼接依赖）经
  `<EmbeddedResource Include="stdlib/**/*.latte">` 内嵌；
  `Semantic/StdlibSources.cs` 的 `ParseAll()` 按逻辑名排序取出解析为
  RootASTNode（sourceName 映射 `<stdlib>/...`），CLI 与测试共用入口；
  stdlib 文件即前端常驻回归测试（新 StdlibSources 套件 30 断言，
  注册表 38 号）。

### 2026-07-31 · M42 路径表达式统一（SYNTAX §1.4 忠实落地）

> 用户拍板的架构重构：表达式位置的「符号引用/调用/索引/成员访问/
> wrapper 访问」五种 AST 节点统一为单一路径表达式节点——语法层只表达
> §1.4 的形态事实（一条完整路径链恰一个节点），「首段是什么」与各段
> 语义的上色全部归 P3。消除 M41 暴露的双形态债（`c.m()` 与
> `core.Console.println()` 同构导致 Binder 双路径处理，原技术债第 1 条）。

- **AST**（`AST/ExpressionNodes.cs`）：删除 `SymbolReferenceASTNode` /
  `CallExpressionASTNode` / `IndexExpressionASTNode` / `MemberAccessASTNode` /
  `WrapperAccessASTNode`；新增 `PathExpressionASTNode`（`Head` +
  `Segments`）/ `PathHeadASTNode`（符号名或表达式底座，互斥）/
  `PathSegmentASTNode`（`PathConnector{Dot,SafeDot,Colon}` + 成员名 +
  泛型实参 + 后缀）/ `PathSuffixASTNode`（`PathSuffixKind{Call,Index}` +
  实参）。前导点 `.Failed`（EnumCaseExpression）保持独立——不是路径。
- **Parser**：`ExpressionParserLayer` 后缀链重写——符号起点不再委托
  PathParserLayer，直接创建 PathExpression；`.`/`?.`/`:` 追加段、
  `(`/`[` 追加后缀、`\<` 挂当前段/首段、trailing lambda 脱糖为 Call
  后缀；非符号起点（分组/字面量/new/enum case）遇路径后缀经
  `EnsurePathExpression` 包装为表达式底座。WrapperNameExpected 状态
  并入 MemberNameExpected（连接符区分，错误消息保持原样）。
  `ArgumentListParserLayer` 具名判别 seed 改为路径形态（技术债第 1 条
  的「seed 双形态」一并消除）。Span 施工：首段记名/底座范围、段记
  连接符到名尾（泛型在 `>` 处扩）、后缀经 currentPathTail 生长封口。
- **能力并集**：表达式路径的泛型实参统一走 TypeReferenceParserLayer
  ——中段泛型的可空实参 `a.b\<String?>.c` 从语法错误变合法
  （原 Symbol 形态不支持可空、MemberAccess 支持，统一取并集）。
- **Binder 适配**：BindSymbolReference/BindCallee 双入口合并为
  `BindPath` 单点上色——表达式底座/泛型段/索引后缀/安全访问/wrapper
  段各自归口诊断（S7/S8/S9/S11）；纯调用形态经 `TryGetCallForm`
  判定（全 Dot 段 + 唯一 Call 后缀）走直接调用；纯值路径（无后缀）
  走单段/多段查找序。顺手修复 M41 遗留：一元 `+x` 前缀合法但
  Binder 抛内部异常（正号按恒等处理）。
- **测试**：`AstDescribe` 新 Path 格式（`Path(head, [.seg, ?.seg, :seg])`，
  段带 `<T>` 与 `(args)/[args]`）；171 用例期望串迁移（14 套件，
  全部为格式替换，零行为变化——结构断言/错误消息断言零修改）；
  **Binder 套件 90 用例未动即绿**——bound 产物与源码语义在重构前后
  完全一致，是对「纯形态重构」的最强验证。TypeOf/ASTIntegrity/
  ExpressionParser 三处手工构造 AST 适配新节点。
- **文档**：`EXPRESSION_ARCHITECTURE.md`（后缀链框架、统一路径段、
  两个施工流程示例、PathParserLayer 职责收窄）与 `FRONTEND_TYPES.md`
  （节点表）同步。

### 2026-07-31 · M41 S5 P3 最小闭环（BoundTree 起步）

> ROADMAP S5 落地：函数体分析（Binder）上线，AST → BoundTree。
> 中端四 pass 已通其三（P1/P2/P3），下一步 S6 出 BIL。

- **`Semantic/Binder.cs`**（`Binder.Bind(unit, decls) →
  IReadOnlyList<BoundFunctionBody>` 静态入口 + 私有 BindSession，
  全程可恢复诊断、函数体间互不阻断）：
  - **分析单位**：遍历声明骨架（五类类型声明递归 + CallableDeclaration
    有体者）逐函数绑定；每函数产物
    `BoundFunctionBody{MethodSymbol, Locals, BoundBlock}`
    （ARCHITECTURE §5.1）；全局字段初始化器/getter/setter 体 S5 不分析。
  - **BoundTree 节点集**（`Semantic/Bound/` 三文件）：BoundNode
    （Syntax 必填回指）/ BoundExpression（Type 定型）/ BoundStatement；
    字面量、BoundValueReference（LocalSymbol/ParameterSymbol 合一）、
    全局字段引用、二元/一元 intrinsic、BoundCallExpression（有值）/
    BoundCallStatement（void 调用语句，两节点分开保住 Type 非空契约）、
    BoundNewExpression、块/局部声明/表达式语句/赋值/return。
  - **定型与推断**：字面量按种类映射 bootstrap（IntType 八值、
    float/double、String/char/bool；null 走 expectedType 可空上下文）；
    var 推断、标注与初始化兼容检查（IsAssignable：严格相等、
    `T → Nullable\<T>` 可空提升、BaseType 链、直接 interface；
    ErrorType 毒化静默；显式 cast 物化归 P4a，BIL §6.5）。
  - **intrinsic 键查询**（BIL §11）：二元/一元运算符文本 →
    BilIntrinsicOp 全表映射；两操作数严格同型 + 操作数类型键集命中
    才合法；结果类型维度——比较 bool、余同操作数（bool and/or 在此
    仅定型，短路展开归 P4a，§11.3）。
  - **调用与构造**：无重载直接调用（候选按实参个数唯一匹配，
    多候选报「重载归 S8」）；具名实参按形参归位，产物即规范参数序
    （默认填充归 S8）；new 解析类型（class/struct 可构，interface/
    enum struct/wrapper 各自诊断）+ init 个数匹配。
  - **return 与 definite assignment**：返回类型兼容（void/非 void
    互斥诊断）；「所有路径显式返回」按末语句递归判定（S5 无控制流，
    if/loop 接入后扩展）；局部变量未赋值使用诊断、赋值即登记
    （赋值目标绑定走 forAssignment 免查——目标不是「使用」）。
  - **名字解析**：值/调用查找序为 块作用域链 → 参数 → 命名空间链
    字段/函数 → 通配 import 容器；多段路径 = 前 N-1 段容器
    （NameResolver）+ 末段成员；**首段命中局部/参数即实例成员路径，
    报「归 S8」诊断**——`c.m()` 在前端是路径形态（§1.4），与
    `core.Console.println()` 同构，只能靠绑定期首段解析区分。
  - **明确不做**（遇之报 P3 诊断而非崩溃）：控制流全家（S7）、
    成员访问/receiver（S8）、重载 ranking 与默认填充（S8）、泛型
    使用侧（S9）、enum case（S11）、字符串插值（S7）、await（S13）。
- **`Semantic/NameResolver.cs`（共享设施提取）**：P2 的名字解析核心
  （ResolveSymbolPath/ResolveDottedPath/ResolveFirstSegment/
  ApplyTypeArguments 等约 220 行）从 ResolveSession 私有方法提取为
  internal 设施，诊断按构造传入的 Phase 落袋；P2 改为委托
  （138 用例零回归），P3 以 DiagnosticPhase.P3 实例化复用——
  函数体内的类型引用（局部标注、new）与声明骨架同一条解析路径。
- **符号家族增补**：`LocalSymbol`（P3 产生，挂 BoundFunctionBody.
  Locals，不进符号图容器表、不受 Freeze 约束；参数仍归 ParameterSymbol）。
- **前端事实适配**（两件，测试暴露）：① 括号表达式产生
  `GroupExpressionASTNode`（无优先级语言里组合运算必括号）——Binder
  透明下钻不落节点；② 裸嵌套块 `{ ... }` 不是合法语句（前端只认
  固定位置的代码块）——嵌套作用域测试改为单层规则断言，BindBlock
  的嵌套能力留待 S7 控制流接入。
- **`Tests/BoundDescribe.cs`**：唯一 bound 树描述器（仿 AstDescribe，
  字面量值经 Syntax 回指取、定型类型短名 Nullable\<T\> → `T?`）；
  `Tests/BinderTests.cs`（90 用例，37 号套件）：11 组——字面量、
  局部声明、值引用、二元/一元运算、赋值、调用、new、return、
  作用域、诊断累积；含结构性事实断言（符号引用相等、Syntax 回指、
  Locals 独立）与三类诊断（类型不匹配/未定义名字/未赋值使用）用例。

### 2026-07-31 · M40 S3 P2 声明解析

> ROADMAP S3 落地：P1 的符号壳全部获得真实类型与合法身份。
> Parser 的语义即死拦截正式移交中端可恢复诊断，P1→P2 链路闭环。

- **`Semantic/DeclarationResolver.cs`**（约 1400 行）：
  `DeclarationResolver.Resolve(unit, decls)` 静态入口 + 私有
  `ResolveSession`（全程可恢复诊断，结束 `unit.Symbols.Freeze()`），
  七个子任务全部落地：
  1. **类型引用解析**：查找序为泛型参数（方法 → 宿主类型链）→ 宿主
     NestedTypes → 文件命名空间父链 → 全局命名空间 → imports →
     core 隐式；`T?` 脱糖 `Nullable\<T\>`；解析失败绑
     `ErrorTypeSymbol`（毒化静默——后续检查遇 ErrorType 一律放行，
     单点报错不级联）。
  2. **init 映射**（§9.3）：`_ -> field` 参数类型节点为空时沿字段
     解析后类型；字段无类型标注即诊断（推断规则，§6 第 9 条）。
  3. **继承/implements 图**：种类匹配（class 承 class、interface 实现
     interface、struct 承 struct）、open/abstract 可继承性、基 struct
     必 open rich、struct 禁 implements；class/interface 双环检测
     （沿基类链与接口集 DFS）。
  4. **修饰符合法性**：rich 仅 struct 系（含 enum struct）、shared 仅
     class/struct 系、open 仅 class/struct、open interface 合法、
     重复修饰符、访问互斥、async 仅函数、ext 限定名与全局位置——
     全部可恢复诊断。
  5. **单向传染 + 字段闭包**（§3.1.1 七行表）：HolderCategory ×
     FieldCategory 逐格判定；直接分类违规即报不展开，放行才展开泛型
     实参代入递归（Substitute + visited 去重）；
     `Nullable\<泛型参数\>` 分类 Unknown。
  6. **共享安全闸门**：全局变量/静态字段/ext 静态成员持非共享安全
     类型即诊断；ext 实例成员不闸门；含泛型参数实参跳过。
  7. **泛型约束 + ext 注册 + wrapper 适用性**：约束 Target 必须本声明
     泛型参数、with 边界必须 wrapper；ext 成员 Owner 改写挂目标类型；
     wrapper 缺 @WrapperTarget 即诊断（推断规则，§6 第 9 条）、
     类别匹配、宿主可内嵌性、§14.9 shared 矩阵 A/B/D、interface
     实现者传染；栈上变量 C 行归 P3。
- **Parser 两处越权拦截移交 P2**（ARCHITECTURE §2/§8 可恢复诊断分工）：
  `DeclarationParserLayer.CreateTypeNode` 的 M31 三条即死校验
  （rich 仅 struct 系 / shared 仅 class/struct 系 / open 仅 class/struct——
  误杀合法的 shared wrapper）与 `OnModifiers` 的重复修饰符、
  open×abstract 互斥校验全部删除，同规则在 P2 以诊断重现。
- **Parser 修复：约束裸名参数双注册**（`GenericParametersParserLayer.
  HandleTargetParsed`）：`T extends Bound` 形态的裸名 Target 此前只进
  Constraints 不进 Parameters，P2 无法解析参数符号；现裸名 Target 先
  `CommitParameterFromTarget` 再 `StartConstraint`（非裸名保留纯约束
  目标归 P2 诊断），新增 `IsBareIdentifier` helper（不抛错版
  TryConvertToParamName 同规则）。GenericParameters/Lambda/
  TypeDeclaration 三套件期望串同步（参数在前约束在后）。
- **bootstrap 补登 `Core.Types`**：19 个内建类型此前只有直造属性未入
  容器成员表，裸名 `i32`/`String`/`Object` 路径解析全部失败——
  `BootstrapSymbols` 末尾统一注册（Span/Box 约束写法同步适配新
  Constraints 列表模型）。
- **符号模型增补**（`Semantic/Symbols/`，S1 家族按需扩展）：
  `ErrorTypeSymbol`（SymbolGraph.ErrorType 单例）、
  `WrapperTargetKind{Entity,Value,Method}`、`GenericConstraintInfo`
  （复用 AST 的 GenericConstraintKind）、TypeSymbol 增
  Interfaces/WrapperTarget/AppliedWrappers/IsOpen/IsAbstract/IsSingleton、
  Field/Method/Parameter 类型字段放宽 `SemanticSymbol?`（泛型参数可作
  类型）、`GenericParameterSymbol.Constraint`→`Constraints` 列表 +
  IsVariadic/IsNamedVariadic、Field/MethodSymbol 增 AppliedWrappers 与
  `AttachToExtTarget`（Owner/Namespace 改 private set）。
- **测试**：`Tests/DeclarationResolverTests.cs`（138 用例、13 组：
  类型解析/init 映射/继承图/修饰符/传染/闭包七行逐行/闸门/约束/ext/
  wrapper 矩阵 A/B/D 逐格/Freeze），注册为 TestRunner 第 36 号套件。
- 全量：1204/1204 + fuzz 6000（36 套件）。

### 2026-07-31 · M39 S2 P1 声明收集

> ROADMAP S2 落地：符号图的首个真实消费者——编译单元全部声明骨架
> （不进函数体）建壳入库，跨文件前向引用就此成立。

- **`Semantic/CompilationUnit.cs`**：编译单元模型（ARCHITECTURE §3）——
  多源文件 `RootASTNode` 集合 + 全局 `DiagnosticBag` + 唯一 `SymbolGraph`。
- **`Semantic/DeclarationCollector.cs`**：`DeclarationCollector.Collect(unit)`
  静态入口 + P1 产物 `DeclarationCollection`（声明 AST 节点 → 符号映射
  `SymbolOf`、每文件 `FileContext`（命名空间 + import 列表）、
  ext 待注册列表 `PendingExtMembers`）。
- **建壳规则**：五种类型声明 → `TypeSymbol`（默认基类建壳即定——
  class→Object / struct→ValueType / enum struct→Enum / wrapper→Wrapper，
  保证 `IsValueTypeBranch` 构造期传播正确，显式基类留 P2 覆盖）；
  变量声明 → `FieldSymbol`；可调用声明 → `MethodSymbol`（Kind 映射 +
  参数/泛型参数壳）；rich/shared/static 只读标记位建壳，合法性检查
  一律归 P2；wrapper 恒 rich（§14.9）。getter/setter 与 enum case
  不建壳（符号家族按需增补，S8/S11）。
- **namespace**：`SymbolGraph.GlobalNamespace`（空名单例）+
  `GetNamespace` 逐段驻留——同路径必同实例，多文件同 namespace 声明
  天然合并；bootstrap 的 core 挂入全局命名空间树（用户 `namespace core.*`
  与 bootstrap 共享驻留路径）；`FullName` 拼段跳过空名父级。
  §15.1 唯一性与位置约束落地（P1 诊断：每文件至多一个 namespace、
  须先于任何类型/成员声明；§6 技术债务第 1 条勾销）。
- **ext 拆名登记**（§4.4）：限定名最后一段为成员名、前缀为
  `ExtTargetPath` 原文；ext 壳不进声明容器表，进 `PendingExtMembers`
  待 P2 解析注册到目标类型（`MethodSymbol`/`FieldSymbol` 增
  `ExtTargetPath` 属性）。
- **重复声明诊断**（累积不中断）：同容器类型同名、变量同名、
  方法 P1 文本级签名（同名 + 同参数名序列 + 同参数类型源码文本）
  全同必为重复——重载不误报，签名级精确判定依赖类型解析归 P2；
  重复符号不进容器表（保留第一个）但仍登记 `SymbolOf` 映射。
  收集期容器视图 `Scope` 借用宿主符号三张成员表 + 方法签名 key 表，
  生命周期 = 容器成员收集全程（修复：每声明新建临时 Scope 导致
  MethodKeys 随建随丢、方法重复检测失效）。
- **符号模型增补**（`Semantic/Symbols/`，S1 家族按需扩展）：
  `NamespaceSymbol` 容器成员表（ChildNamespaces/Types/Fields/Methods）、
  `TypeSymbol.NestedTypes`、`MethodSymbol.GenericParameters`。
- **测试**：`Tests/DeclarationCollectorTests.cs`（83 用例：全局/类型/
  嵌套/namespace/import/重复/ext/namespace 诊断/跨文件九组，
  符号断言一律引用相等，canonical 路径经 CanonicalSymbolPrinter
  验证），注册为 TestRunner 第 35 号套件。
- 全量：1066/1066 + fuzz 6000（35 套件）。

### 2026-07-31 · M38 S4 BIL 对象模型 + BilWriter

> ROADMAP S4 落地：BIL 生态基座（对中端零依赖）。中端三条基建线
> （S0 诊断 / S1 符号图 / S4 BIL 模型）至此全部就位。

- **`Bil/BilModule.cs`**：`BilModule`（`BIL "1.1"` 版本头 + Metadata +
  Resources + LocalSymbols + ExternalSymbols + Functions，§4 段顺序与
  全段输出）+ `BilMetadataEntry`（§4.1）+ 资源家族（§18 全形态：
  `BilScalarResource` 标量/raw、`BilNullResource`、`BilCollectionResource`
  array/pair/map/switch-table/catch-table 单行与多行排版）。
- **`Bil/BilSymbols.cs`**：类型声明（§8.2：kind/extends/implements/
  修饰符，多行续行形态；`generic(...)` 子句注记 S9 增补）+
  `BilSimpleMemberDeclaration`（.field/.static-field/.method/
  .static-method 共形态：keyword + canonical symbol + 修饰符，
  `ModifiersOnNextLine` 复现 §19 wrapper 示例续行）+
  `BilCaseDeclaration`（§8.5：参数段 + discriminant auto/res(R)）。
- **`Bil/BilFunction.cs`**：`BilFunction`（§9.1）+ `.args` 条目
  （名 = 类型，保序）+ `.vars` 条目（类型 名）+ `BilBlock`
  （§9.4–§9.6：id + entrypoint/volatile 修饰符）。
- **`Bil/BilInstructions.cs`**：`BilInstruction`（opcode + 操作数列表
  通用形态承载 §10–§16 全部标准指令；结构化理解留给 S12 verifier）
  + 操作数家族（§10.1 全集：`$var`/fn/field/type/case/blk/res/none/
  `[列表]`，none 单例）+ `BilOp` 便捷构造。协程指令 §17 暂缓
  （ARCHITECTURE §7 待修订清单，S13 专项）。`Origin` 以 `object?`
  占位（S6 接通后收窄为 `LoweredNode?`）。
- **`Bil/BilWriter.cs`**：模型 → 标准 BIL 文本（只输出标准 spelling，
  §5.6；4 空格缩进、段间空行、条目尾逗号、switch/try 规范多行排版；
  换行统一 `\n` 不随平台漂移）。
- **架构纪律**：`Bil/` 不引用 Semantic/Lowering/AST 任何类型——
  类型引用与符号一律以 canonical 字符串承载（字符串即 BIL 世界身份，
  S1 CanonicalSymbolPrinter 的投影即其来源）；verifier/VM 未来只依赖
  本目录。
- **测试**：`Tests/BilWriterTests.cs`（6 用例：§19 完整黄金示例逐行
  一致 + §19 wrapper 隐藏字段示例 + §18 资源全形态 + §8.2/§8.5 声明
  形态 + §10–§16 指令形态抽样 + Origin 默认 null；黄金文本经
  `Lines(...)` 显式拼 `\n`，autocrlf 免疫），注册为 TestRunner
  第 34 号套件。
- 全量：983/983 + fuzz 6000（34 套件）。

### 2026-07-31 · M37 S1 符号图内核 + bootstrap + CanonicalSymbolPrinter

> ROADMAP S1 落地：中端符号对象图的内核（SEMANTIC_ARCHITECTURE §4）。

- **`Semantic/Symbols/SemanticSymbol.cs`**：符号家族——基类
  `SemanticSymbol`（引用相等即身份，禁止按名字字符串比较）+
  `NamespaceSymbol`（多段路径嵌套）/ `TypeSymbol`（Kind 五类 +
  BaseType/IsRich/IsShared/IsBuiltin/IsValueTypeBranch +
  GenericParameters/Fields/Methods + 构造类型双字段
  ConstructedFrom/TypeArguments）/ `MethodSymbol`（MethodKind：
  Regular/Init/Operator/Getter/Setter）/ `FieldSymbol` /
  `ParameterSymbol` / `GenericParameterSymbol`（extends 约束）。
  构造期两阶段：P2 填充字段（BaseType/ReturnType/FieldType/Type/
  Constraint）internal set。
- **`Semantic/Symbols/SymbolGraph.cs`**：符号图容器——构造泛型类型
  驻留 cache（键 = (定义, 实参列表)，一律引用相等；同键必同实例；
  实参数组复制防外部改写）+ `GetNullable`（T? = Nullable\<T>，SYNTAX
  §3.4）+ `Freeze`（P2 结束冻结标记；驻留 cache 为幂等派生物不受限）。
- **`Semantic/Symbols/BootstrapSymbols.cs`**：硬编码 bootstrap
  （ARCHITECTURE §4.3）——SYNTAX §3.1 完整层级（Any/Object/ValueType/
  Enum/Wrapper 根 + i8–u64/float/double/bool/char/String 基元 +
  Type\<T\>/Span\<T\>/Nullable\<T\>/Box\<T\> 泛型内建，全部挂 `core`
  命名空间）；三条易错层级事实落实（String/Wrapper 在 ValueType 分支
  且 String 非 rich、Wrapper 恒 rich；Nullable/Box 在 Object 分支；
  Box\<T\> <: Object 经 BaseType 链直接表达）；`BilIntrinsicOp` 枚举 +
  每基元登记 intrinsic 集（BIL §11：整数=算术+位+比较、浮点=算术+比较、
  无符号无 Opposite、bool=逻辑+相等、char=比较全集、String=仅相等；
  char/String 边缘集注记 S5 按 §13.2 再核）；`IsSharedSafe()` 实现
  SYNTAX §3.1.1 白名单（含 Nullable 按 T 推导，经
  DerivesSharedSafetyFromTypeArgument 定义级特权标记，无字符串比较）。
- **`Semantic/Symbols/CanonicalSymbolPrinter.cs`**：BIL §5.2 五形态
  （类型 `ns::Outer.Inner` / 方法 `$[.static.]名(参:型,...)@返回` /
  字段 `#[.static.]名@型` / 运算符 `$$名` / getter-setter `$.get.名`
  `$.set.名`）+ BIL 类型引用投影（固定别名 `.i32`/`.f32`/`.any` 等 >
  标准构造 `.nullable<T>`/`.typeid<T>`/`.array<T>` > canonical/闭合
  泛型 `core::Box<.i32>`；null 返回类型 → `.void`；泛型参数实参 →
  `.generic<$.generic.T>` §7.5）。投影提示经 TypeSymbol.BilAlias /
  BilStandardConstructor 登记（core.latte 的 Array/Map/Pair 未来
  经同机制接入）。
- **测试**：`Tests/SymbolGraphTests.cs`（45 用例：驻留同一引用、
  bootstrap 层级、分支/rich 标记、shared-safe 推导、泛型约束、
  intrinsic 键空间、Freeze）+ `Tests/CanonicalSymbolPrinterTests.cs`
  （22 用例：§5.2/§8.1/§19 示例逐条对照 + 嵌套/构造/全局/泛型实参
  形态），注册为 TestRunner 第 32/33 号套件。
- 全量：977/977 + fuzz 6000（33 套件）。

### 2026-07-31 · M36 S0 诊断基建 + ROADMAP 文件级细化

> 中端第一段代码（ROADMAP S0 落地）；同批完成 `SEMANTIC_ROADMAP.md`
> 的 S0–S6 文件级施工清单细化。

- **ROADMAP 细化（先于代码）**：S0/S1/S4 细化为文件级施工清单（每个
  文件含哪些类型、对应哪个测试套件），S2/S3/S5/S6 细化到任务级，
  S7+ 保持远粗。细化中钉死的对接决策：**Bil 模型的类型引用以字符串
  承载**，即 S1 `CanonicalSymbolPrinter` 的 BIL 类型引用投影形式
  （基元 → `.i32` 等固定别名、`Nullable\<T>` → `.nullable<T>`、用户
  类型 → canonical）——S1 与 S4 因此无顺序依赖。
- **`Semantic/Diagnostics.cs`**（新增顶层目录 `Semantic/`）：
  `DiagnosticSeverity { Error, Warning }`、`DiagnosticPhase { P1–P4 }`、
  `Diagnostic { Severity, Phase, Span: CharRange?, Message }`（暂不建
  错误码编号体系，ARCHITECTURE §8）、`DiagnosticBag`（全编译单元单
  实例贯穿 P1–P4、只追加；`HasErrors` 阶段推进门槛；可遍历供断言）。
  与前端 LexerException/ParserException（单发即死）严格不同：中端
  诊断累积、尽量继续，一次编译报出尽可能多的错误。
- **`Tests/TestHarness.cs`**：新增 `CheckSemanticError(label, bag,
  msgPart)`——断言存在 Error 级且消息含片段的诊断（沿用消息子串
  惯例，与 CheckParseError 一致）。
- **测试**：新增 `Tests/DiagnosticsTests.cs` 套件（15 用例：空袋、
  Warning 不触发门槛 / Error 触发门槛、Severity/Phase/Span/Message
  字段携带、多错累积顺序保持、CheckSemanticError 命中），注册为
  TestRunner 第 31 号套件。
- 全量：910/910 + fuzz 6000（31 套件）。

### 2026-07-29 · M35 中端阶段开篇：语义分析架构定稿 + shared/rich/wrapper/String 规范修订

> **阶段转折点**：M1–M34 是前端（Lexer + Parser）阶段，随 M34 技术债清扫收官；
> M35 起项目进入**中端（语义分析 + BIL 生成）阶段**。本里程碑是中端的第一个
> 里程碑，交付物**全部是文档**——架构、路线图与作为其前提的语言规范修订；
> 中端代码从下一个里程碑（ROADMAP S0 诊断基建）开始写。
> 因此测试数量与前端组件状态相对 M34 无任何变化。

**一、中端架构定稿（新增 `docs/compiler/semantic/`）**

- `SEMANTIC_ARCHITECTURE.md`：四 pass 分工确定为 **P1 声明收集 → P2 声明解析
  → P3 Binder→BoundTree → P4a Lowerer→LoweredTree → P4b BilEmitter→BilModule**。
  用户拍板的五项：① P3 与 P4 分开（不合并为单遍）；② Roslyn 风格**独立的
  Bound Tree**（不在 AST 上挂语义字段）；③ 驻留符号对象图（同一符号同一引用，
  含构造泛型类型驻留）；④ bootstrap 硬编码 + core.latte 混合供给根类型；
  ⑤ 新错误模型（`Diagnostic` + `DiagnosticBag`，多错不互断）。
  另含 Origin 调试链（Bil→Lowered→Bound→AST.Span）与 BIL 待修订清单。
- `SEMANTIC_ROADMAP.md`：S0–S14 计划序号，近细远粗（S0–S6 已细化到验收标准）。
  验收总原则：**BIL 模型/writer → lowering 最小闭环 → verifier → VM**。
  **async 深度 lowering 归 S13**——async/await 物化为标准库 Task 调用，
  Task 再调 stdlib 要求 Middleware 暴露的 Native 方法，因此 Middleware 完全
  不关心上层异步模型（备选的浅 lowering 方案会让 P4 与 BIL/VM 捆绑，
  违反关注点分离，已否决）。
- 目录重命名：`docs/compiler/frontend/` → `docs/compiler/syntax/`
  （与新增的 `semantic/` 并列，三份前端文档随迁）。

**二、语言规范修订（用户拍板，作为中端检查项的前提）**

- **String 改为非 rich 值类型基元**（原属 Object 分支）：可观察语义是
  **严格深拷贝**；实现可引入用户透明的 CoW/驻留/native 计数优化，但源码语义、
  编译器分析、用户代码一律**不得假设其存在**——类比「BIL 永远不应假设 GC
  模型和 GC 行为」。因此 `struct Label { text: String }` 不需要 `rich`。
- **wrapper 恒为 rich struct**，对象树上 `MyWrapper → Wrapper → ValueType`：
  值语义、unique ownership，正是生命周期能绑定被修饰实体/方法/值的原因。
  `rich` 由 `wrapper` 声明形式隐含，**源码显式书写是编译错误**；BIL 作为
  显式 IR 反之**必须**显式带 `rich`。
- **`obj:Wrapper` 是只读 place**：只能作成员访问的接收者（读写字段、调用方法，
  一律原地作用于宿主持有的那份）；**不可整体赋值**（wrapper 只能由 `@W(...)`
  在宿主创建时安装）、**不可整体取值**（作实参/返回值/推断源皆非法）。
  两条禁令都直接来自「与宿主同生共死」的不变量，在语法层封死而非靠约定。
  wrapper 字段自身的可写性仍由 `var`/`const` 与可见性决定。
- 新增 SYNTAX §14.9「wrapper 的 rich/shared 规则与目标矩阵」：宿主可内嵌性
  （宿主必须能内嵌 rich struct ⇒ 非 rich struct、基元类型、String、Span、
  Type 不可被任何 wrapper 修饰）、shared wrapper 可修饰全部目标但字段受
  shared 约束、非 shared wrapper 只能修饰 A–D 四类非 shared 目标、
  interface 实现者传染校验。
- 新增**「共享安全类型」**统一概念（shared class ∪ shared rich struct/wrapper
  ∪ 非 rich ValueType ∪ T 共享安全的 `Nullable\<T>`），让两条逃逸闸门共用
  同一张白名单：① 全局/静态字段类型必须共享安全（singleton 因此必须 shared）；
  ② async 五项边界（receiver / 参数 / TResult / lambda 捕获 / 泛型实参）。
- **rich/shared 单向传染**：基类 rich/shared ⇒ 子类必须同标；反向可收紧
  （shared 子类可继承 local 基类），安全性由「含继承字段的完整闭包重校验」兜底。
- **非 rich struct（含非 rich enum struct）不得标记 `open`/`abstract`** ⇒
  不可能有子类型 ⇒ 封死「声明 rich/shared 子类型绕过闭包检查」的路径。
  **不引入 `final` 关键字**。

**三、同步与冲突处理**

- `RUNTIME.md`：三个运行时域的归属重划（String 入非 rich ValueType 域、
  全部 wrapper 入 rich ValueType 域、shared wrapper 入 shared rich 域）+
  新增「String 的表示」与「wrapper 值的表示」两段 + `CoroutineLocal\<TValue>`
  是 per-coroutine 语义的唯一机制。
- `BIL_STANDARD.md` 只做**机械同步**两处：`.string` 归 ValueType 域（§6.2）、
  §8.2 修饰符合法性 4 条 → 7 条。**指令集未动**：wrapper 从 Object 变 rich
  struct 后，§12.4 只有值语义的 `get.wrapper`，缺**只读 place 的接收者形态**
  （以宿主那份为 receiver、proxy 体内 `this`）——沿用 async 的既定做法定性为
  **待补而非待改**，记入 SEMANTIC_ARCHITECTURE §7.1 + ROADMAP S11。
- `CLAUDE.md` / `AGENTS.md` / `PARSER_ROADMAP.md` 示例与表格对齐；顺手修掉
  两处因本次修订而自相矛盾的旧示例（`rich struct { label: String }`、
  `pub open struct Point3D : Point`）。
- **前端零改动**：`Parser/Keywords.cs` 的 `DeclarationDescriptors` 已含
  RICH/SHARED/OPEN/ABSTRACT/SINGLETON，新规则全部是语义期 P2 的合法性检查
  （落 ROADMAP S3），Parser 只负责收修饰符。
- 测试：895/895 + fuzz 6000（30 套件，无增删——纯文档里程碑）。

### 2026-07-28 · M34 技术债清扫：字符字面量 / 复合赋值 / is enum case / `.name` 参数 / import 禁令

> 集中清扫 §6 技术债清单中的 5 项解析层缺口（规范依据：§3.3 字符规则定稿、
> §13.2 复合赋值既有、§12.3 is enum case 既有、§14.4 `.name` 既有、§15.2 禁令新增）。

- **字符字面量**：Lexer 新增 `CharLexerLayer`（三态状态机
  AwaitContent/EscapeSeen/ContentSeen，`BaseLexerLayer` 遇 `'` 分流，
  与 StringLexerLayer 同文件同风格）；新增 `CharToken`（TokenType.Char，
  char 无插值概念、不复用 StringToken）；AST 新增 `CharLiteralASTNode`；
  转义复用 `StringEscape` 单源；`''`/`'ab'`/未知转义/未闭合（含 EOF 冲刷帧
  虚拟换行）/反斜杠后换行均编译错误；JSONL 两端加 char ↔ 单字符字符串分支
  （往返必炸点：Deserializer 的 IsPrimitive 原走 GetInt64）；LexerFuzz 固定
  用例同步（原「'A' 未实现」错误断言反转）；RootParserLayer 顶层分派补
  `case CharToken`（ParseFirstDecl 必经之路）
- **复合赋值（§13.2，10 个全集）**：新增 `CompoundAssignmentExpressionASTNode`
  （Target/Operator/Value，Operator 不含 `=`）；`ExpressionParserLayer.
  HandleOperatorSeen` 在 pending 运算符后遇 `=` 且组合属全集时构造节点——
  左操作数仍在层内未挂载（Attach 只在 CompleteExpression 执行），一次性
  Attach 无替换、红线合规；`>>>=` 与 `>=`/`>>`/`>>>` 比较重组天然有序共存；
  非全集组合（如 `== =`）不再特判、落正常二元流程报错；复合赋值整体是
  表达式节点（普通赋值 `a = b` 维持 ExpressionStatement 双 Root 槽现状）；
  M31 的两个「尚未支持」错误用例反转为正例
- **`is` 右侧 enum case（§12.3）**：`TypeCheckExpressionASTNode` 改
  `TargetType`（可空，填充时创建——预创建会留无 span 空壳过不了 Validator）
  与 `TargetCase` 双字段互斥；`HandleTypeOperatorSeen` 仅 `is` 遇 `.` 分流
  走既有前导点逻辑（`EnumCaseExpressionASTNode` 加 parent 构造参数，
  归属即定）；`as`/`as?`/`supers`/`with` 右侧仍只收类型；switch 模式匹配
  `(_ is .Success)` 同路径通吃
- **wrapper `.name` 保留参数名（§14.4）**：`ParameterListParserLayer` 新增
  `DotNameExpected` 状态，`.` 后必须是标识符（`.123`/裸 `.` 报错）；
  `ParameterASTNode.Name` 原样存 `.name`（最小侵入，描述/JSONL 零改动）；
  解析层不限制上下文（语义阶段约束）；`operator .proxy.call(.name: String,
  args: named Any...): Any` canonical 全形解析通过
- **import `{}` 禁令（§15.2）**：`ImportParserLayer.OnAfterListItem` 遇 `.`
  报规则化错误（列表项只能是单标识符、不同子路径写多条 import）；
  顺手修正类注释与实际行为不符处（SymbolLayer 空名元素不残留）
- **测试**：895/895 + fuzz 6000（30 套件；Literal +23、Expression +28、
  ParameterList +5、TypeDeclaration +1、Import +1）

### 2026-07-28 · M33 值块统一：多语句分支体 + switch 语句形态 + lambda 裸 return 边界

> 依据 SYNTAX §5.1/§6.1/§7.1/§7.2 三项规范变更（值块统一）落地：
> 值块（seq 块、if/switch 表达式分支体、lambda 块体）形态与取值规则统一，
> 匿名默认标签为 `_`；lambda 体内裸 return 成为编译错误。

- **if/switch 表达式分支体统一为代码块**：`IfExpressionASTNode.ThenExpression/
  ElseExpression` → `ThenBody/ElseBody`（CodeBlockASTNode，创建即定）；
  `SwitchCaseASTNode.Body`、`SwitchExpressionASTNode.DefaultBody` 同样块化；
  分支体改由 `CodeBlockParserLayer` 施工。「单表达式分支隐式取值」下沉为
  「块内恰好一条 ExpressionStatement」的语义规则（解析层无特判，取值留待
  语义阶段）；多语句分支体经 `return@_`（匿名默认标签）/`return@标签` 取值
- **named 标签**：if/switch 头部 `)` 后、lambda `->` 后可写 `named 标识符`；
  `IfExpressionASTNode`/`SwitchExpressionASTNode`/`LambdaExpressionASTNode`
  新增 `Label string?`（写法参照 `SeqBlockParserLayer.HandleNamed`）
- **switch 语句形态**：新增 `SwitchStatementASTNode`（与 IfStatementASTNode
  对称，Selector/Cases/DefaultBody，分支体一律代码块）；
  `SwitchStatementParserLayer` 实现双模式（语句模式从 switch 关键字进入，
  表达式模式关键字已由 ExpressionParserLayer 消费）；
  `CodeBlockParserLayer.HandleStatementDispatch` 新增 SWITCH 路由；
  两形态统一强制 default（规范同步修订），Validator 的 default 规则
  对称覆盖新节点。副作用：语句位置的 switch 不再落成表达式语句
- **lambda 体双字段互斥**：`->` 后遇 `{` → 块形态（新 `BlockBody`
  CodeBlockASTNode），否则维持单表达式（`Body` ExpressionRoot 改可空，
  创建时定，参照 ExpressionStatementASTNode 双 Root 槽先例）；两字段互斥
- **lambda 体内裸 return 编译错误**：`CodeBlockParserLayer` 新增
  `allowBareReturn` 构造标记（默认 true），遇无 @标签 return 且标记为
  false 时抛 ParserException；标记沿施工链全链传染（lambda 体内的
  seq/if 语句块/循环体/try/switch/变量初始化/表达式深处的 if/switch
  表达式分支体——ExpressionParserLayer 及 If/Switch/Loop/TryCatch/Seq/
  VariableDeclaration/ArgumentList/TypeOf 各层逐一传递）；lambda 块体
  与单表达式体一律下传 false（lambda 是边界，内嵌 lambda 仍是 false）；
  if/switch 表达式分支体继承父上下文标记（不是 lambda 边界）
- **return@_ 迁移**：seq 匿名默认标签 `seq` → `_`（SYNTAX §6.1）；
  纯注释与测试快照迁移（标签只是字符串，解析层无特判）
- **行为变化（规范使然）**：值块内的 if/switch 一律按语句分发——
  `if (a) { if (b) {1} else {2} } else {3}` 的 then 分支是 IfStmt，
  内层 if 作分支值须写 `return@_ if (b) {1} else {2}`
- **同步面**：AstDescribe 描述器（If/Switch/Lambda/SwitchStmt 块化 + named）、
  AstJsonlSerializer/Deserializer（反射驱动，新节点/新字段零改动接入，
  往返套件补 switch 语句 + 多语句分支 + lambda 块体用例）
- **测试**：838/838 + fuzz 6000（30 套件；IfExpression/SwitchExpression/
  Lambda 套件迁移并扩充：多语句分支 return@_、named + return@标签、
  switch 语句形态（含多语句分支与边界交还）、lambda 块体、裸 return
  报错（直接/嵌套 seq/if/循环/switch/单表达式体分支/内嵌 lambda）；
  无新增测试类，用例全部加进现有套件）

### 2026-07-28 · M32 多行字符串 `"""`：规范定稿 + 全栈落地

> 技术债清理：SYNTAX §3.3 的多行字符串从「仅列示例」到规范定稿 + 实现。
> 规范语义经语言设计者确认（此前按「不猜测规范」原则挂起，见 M31 记录）。

- **规范定稿（SYNTAX §3.3）**：Swift 风格严格多行——开界 `"""` 后必须紧跟换行
  （剥除，同行写内容即编译错误）；闭界 `"""` 必须独占一行（其前仅空白，
  闭界前换行不属于内容）；闭界行缩进量 = 剥除基准，内容行前导空白不足即
  编译错误，全空白内容行输出空行；转义与单行同一套（剥除先于转义），
  行内 `"`/`""` 免转义，内容中的 `"""` 须写 `\"""`（闭界出现在内容行中间
  即编译错误）；内容换行恒为 `\n`（行尾归一在词法入口完成）；
  `${}` 插值与单行一致
- **Lexer 引号分流**：新增 `QuoteLexerLayer`——`"` 家族统一入口
  （与 `SlashLexerLayer` 同模式：按第二/三字符分流单行串 / 空串 `""` /
  多行 `"""`，字符串层以持有实例转发、同步弹出）；
  `BaseLexerLayer` 不再直推 `StringLexerLayer`
- **`MultilineStringLexerLayer` 两阶段施工**：原文按行缓冲（反斜杠只用于让
  `\"` 不参与引号计数，不展开转义），闭合时先剥缩进、再统一处理转义；
  反斜杠后紧跟真实换行立即报错（不支持行接续）；未闭合报
  "Unterminated multi-line string literal"（冲刷帧栈检查，单行串行为不变）
- **转义表单源化**：`StringEscape.TryProcess` 静态表供单行/多行共用，
  `StringLexerLayer` 内联 switch 收编
- **Parser/AST 近零改动**：多行字符串产出复用 `StringToken`，
  `StringLiteralASTNode`/JSONL 序列化路径完全不动
- **插值标记词法期判定（顺带修复）**：`StringToken.HasInterpolation` 由字符串层
  在转义处理时判定（未转义的 `$` 后紧跟 `{` 才算；`\$` 转义的字面 `$` 不构成
  插值引导，多行内 `${` 须同行相邻），`LiteralParserLayer` 不再用
  `Content.Contains("${")` 猜测——修复 `\${` 误报插值（单行串既有 bug，
  转义信息在 Content 拼装后已丢失，Parser 侧无法回补）
- **测试**：790/790 + fuzz 6000（30 套件；新增 MultilineString 套件 43 例：
  内容拼装 / 错误路径 / 引号分流回归 / token span / 插值标记 / AST 六组）

### 2026-07-28 · M31 前端大修：全量 review 驱动的 40+ 项修复

> 不改 Latte 语法；对 Lexer/Parser/AST/Core/Tests 五层做了一次全量 review
> （5 路并行 + 端到端复现验证），修复全部确认 bug 与改进建议，
> 并落地三项硬性要求：Span 左闭右开统一、JSONL 字段名键控 + 完整反序列化器、
> 测试基建统一。计划与执行的八个阶段：A 测试基建 → B Span → C Lexer →
> D 表达式/字面量 → E 声明层/关键字 → F Core/AST 基建 → G 文档 → H 回归。

- **测试基建统一（阶段 A，安全网先行）**：
  - 新建 `Tests/AstDescribe.cs`（统一 AST 描述器，替代 13+ 份分叉方言：
    `Access(obj, .m)`/`MemberAccess(obj.m)`、`var x = ...`/`Var(x = ...)` 等收敛为
    单一信息无损格式）与 `Tests/TestHarness.cs`（统一 Parse 驱动 +
    Check/CheckTrue/CheckParseError 断言与计数）
  - 21 个套件全部迁移到统一基建；断言对象约定：除查的就是命令行/日志/
    token 流/层协议行为的套件（Logger/CommandLineParser/LexerFuzz/TokenDisposition）
    外，一律断言 AST 树产物（描述串 + 结构断言）
  - 弱断言修复：`LiteralParserTests`（原只查节点类型名）与
    `VariableDeclarationTests`（原第一条件恒假）改为全串精确比对 + 结构断言；
    `TypeReferenceParserTests` 占位测试重写为 17 个真实用例；
    新增 `PathParserLayerTests`（16 例）、`ArgumentListParserLayerTests`（12 例），
    注册为套件 28/29；结构断言（Root 填充/Parent 链/无共享）普及到表达式类套件
  - `test --all` 退出码 clamp 到 255（防 Unix 8 位退出码回绕假绿）
- **Span 左闭右开统一（阶段 B）**：所有 `CharRange` 改为 `[Start, End)`——
  Start 指向首个字符，End 指向最后一个字符的下一位置
  （`Lexer.ContextImpl.PushToken` 经 `Advance` 计算；Parser 层 span 自然继承
  token 开区间；EOF 保持零宽）。相邻 token 首尾相接；Validator/序列化器/
  文档同步声明。位置断言全部更新（LexerFuzz.TestPositions）
- **Lexer 修复（阶段 C）**：
  - 块注释层重写：删除无规范依据的反斜杠转义机制（修复 `/* a*b */` 吞 `*`、
    `/* a\b */` 吞 `\`）；换行不吞——注释按行分段、换行以 LineBreakToken 入流
    （与行注释一致，两条语句间唯一的分隔换行在块注释内时语句分隔不丢失）
  - 字符字面量 `'` 由 Base 层明确报错（此前被静默当字符串收下）；
    多行字符串 `"""` 仍未实现（已知限制，规范语义未定义不猜测）
  - 行尾归一手写化：只把 `\r\n`/`\r` 归一为 `\n`（ReplaceLineEndings 此前会
    误伤字符串内的 `\f`/`\x85`/`\u2028`/`\u2029`）
  - 复合赋值策略统一：Lexer 不再合并 `*=`/`/=`（与 `>=` 同策略拆 token，
    将来 Parser 重组）；删除 `++`/`--`（规范不存在）与 `#` 命名误导常量
  - 未闭合字符串/块注释错误信息友好化（不再暴露内部层类名）
- **表达式与字面量修复（阶段 D）**：
  - 续行规则落地（SYNTAX §1.1）：`()`/`[]` 未闭合时换行按空白处理——
    调用/索引/形参列表全状态跳过换行；ExpressionParserLayer 新增
    `insideParens` 括号语境（分组/实参/条件/迭代表达式，右操作数与一元操作数继承）
  - 位运算符 `<<`/`&`/`|`/`^` 接入（`>>`/`>>>` 维持重组）；删除 `in` 二元运算符
    （只属于 for 循环头）；复合赋值遇 `=` 报「尚未支持，请展开为 a = a op b」
  - 数字字面量补全（SYNTAX §3.3）：`0b`/`0o` 前缀、下划线分隔
    （不连续/不开头结尾）；判定收敛为 `Parser/NumericLiteral.cs` 共享 helper
    （原三份规则不一致）；`IntLiteralASTNode.IsHex` 布尔改为
    `LiteralIntBase` 枚举（Decimal/Hex/Binary/Octal）
  - `3.`/`3.foo` 吞点修复：点后非数字明确报错（不再吞 `.` 伪装成员访问）；
    `a[]` 空索引拒绝（`foo()` 空参保持合法）；`throw` 后换行报错（与
    return/yield 行为一致）；`in` 从二元运算符移除后 `a in b` 在表达式位置报错
  - PathParserLayer 吞尾修复：`foo.` + 换行/EOF、`List\<i32` + EOF 报错
    （此前静默吞并）；`foo..bar` 双点报错（类型/参数语境经
    `allowVariadicDots` 保留 `...` 交还）；import 尾点报错自然获得
  - Span 一致性：表达式形态 if/switch/typeOf/lambda 的 span 含起始关键字；
    Loop/If 终态不消费换行（span 不拖尾换行符，无 else 的 if 经
    ElseCheckEntryEnd 封 End）；变量声明初始化后换行改 Replay（同语法
    不再两种 span）；EOF 规则 6 字面合规（结构完整态 Pop(Replay)）
- **声明层与关键字修复（阶段 E）**：
  - Keywords 大扫除（对齐 SYNTAX §19）：删除
    elif/foreach/when/case/private/public/final/extension/base 幽灵词
    （`private class Foo {}` 等不再被静默接受）；保留字数组补齐
    switch/return/break/continue/throw/yield/do/to/in/supers/with/typeOf/await/
    self/seq/using/import/namespace；import/namespace 移出声明路由数组
    （修复路由遮蔽）
  - 修饰符组合校验：rich 仅 struct/enum struct；shared 仅 class 或 rich struct；
    open 仅 class/struct（enum struct 明确禁止）；open+abstract 互斥；
    重复修饰符报错；`enum` 后必须 `struct`
  - 标识符合法性统一：`Keywords.IsIdentifierStart/IsIdentifier/IsReservedKeyword`
    共享实现，铺到变量名/参数名/类型名/callable 名/循环变量/catch 参数/
    using 绑定/enum case 名/成员名/符号路径元素/import 列表项
    （`class 123 {}`、`func f(123: i32)`、`for (123 in c)`、`var return = 5`、
    `foo(return = 5)` 全部报错；标签位置只查首字符——return@seq 合法）
  - 注解名空校验（`@(1)`/`@`+换行报错）；`named` 必须带 `...`；
    enum 判别值支持 0x 等进制与 long 范围（`DiscriminantValue` int?→long?）
- **Core/AST 基建（阶段 F）**：
  - Logger 控制台输出改走 stderr（诊断不污染 stdout——`compile --parse-only`
    的 JSONL 输出流纯净）
  - JSONL 序列化修复：int?/long? 等 Nullable 字段不再静默丢弃；
    严格先按声明类型过滤再取值（未填充属性不再被提前求值）；循环保护；
    字段枚举沿基类链（基类 private 字段反射盲区修复）
  - **JSONL v2 + 完整反序列化器**：carrier（ImportItem）记录化
    （独立产行、标量字段入 fields——修复 importAll 往返必丢）；
    新建 `AST/AstJsonlDeserializer.cs`：三阶段重建（类型定位/实例挂接
    （预创建子容器复用、carrier struct 回写）/fields 与 span 回填），
    字段名键控不依赖顺序，产物强制过 ASTIntegrityValidator；
    往返测试（Parse→Serialize→Deserialize→Serialize 逐行一致）12 例
  - Validator 补强：`[ChildAstNode(Required = true)]` 必需子节点校验
    （LiteralExpressionASTNode.literal、CatchClauseASTNode.ExceptionType）；
    类型审计沿基类链；[AstCarrier] 类型递归审计（carrier 装 ASTNode 的
    属性会逃出遍历，拒绝）
  - CompileCommand：CompilerInternalException 单独报告（与用户语法错误区分）；
    多文件 dump/parse-only 输出 `{"file":...}` 元记录分隔；全失败不再误报 dumped
  - import 多导入形态 SymbolElement 按引用共享改深拷贝（防语义阶段交叉污染）
- **记录在案的技术债务（本轮不改代码）**：实参位置 `a.b` 的
  MemberAccess/Symbol 双形态；`is` 右侧 enum case（§12.3）；method wrapper
  `.name` 保留参数（§14.4）；switch 语句形态（规范未定义）；多行字符串
  `"""`（规范语义未定义）；复合赋值语义实现（token 策略已统一）；
  三态合并与 ExpectNotation 提取经评估不更简洁，放弃
- **测试**：746/746 + fuzz 6000（29 套件；新增 Path/ArgumentList 两套件，
  AstJsonlSerializer 扩至 84 例含往返与格式 v2）

### 2026-07-27 · M30 Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档幽灵清理

> 不改 Latte 语法；把 `Core/Utilities.cs` 按语义拆分为 7 个文件，
> ASTVisitor 遍历逻辑开放重载，并清理文档中已删除代码的引用与过时表述。

- **Utilities.cs 拆分**（同 `LatteCompiler` 命名空间纯搬移，零调用点改动，
  原文件删除）：
  - `Lexer/Tokens.cs`：Token 基类 + TokenType + 6 个 token 类
    （顺带修正 EndOfFileToken 上 M25 前的过时注释——EOF 现由 Lexer 追加）
  - `Lexer/Notations.cs`：符号常量；`Parser/Keywords.cs`：关键字常量
    （使用方 100% 在 Parser）
  - `Core/Exceptions.cs`：LexerException / ParserException
  - `AST/ASTNode.cs`：ASTNode 基类 + RootASTNode；
    `AST/SymbolNodes.cs`：Symbol 家族 + SymbolASTNode；
    `AST/ImportNodes.cs`：ImportASTNode + [AstCarrier] ImportItem
  - 原文件的幽灵 using（`System.Linq`/`System.Threading.Tasks`）随之清除
- **ASTVisitor 遍历可重载**：`VisitNode`（遍历骨架）与 `EnumerateChildren`
  （子节点来源）改为 protected virtual，默认仍走 [ChildAstNode] 反射；
  Validator/Serializer/测试零改动
- **文档幽灵清理**：
  - `docs/compiler/frontend/FRONTEND_TYPES.md` 全量修订：删除幽灵引用——
    `Core/FrontendTypesExtension.cs` 全系（SymbolTable/SymbolInfo/TypeInfo/
    SemanticException 等，代码已不存在）、AcquisitionExpressionASTNode、
    CharLiteralASTNode；修正过时表述（enum case 列表与 wrapper proxy 的
    「待实现」标注、阶段标记），文件位置更新到 M30 拆分后布局，
    Token 表补 EndOfFileToken 行
  - `docs/compiler/frontend/FRONTEND_ARCHITECTURE.md` 删除：全文围绕已删除的
    FrontendTypesExtension.cs，残余内容与 AGENTS.md §3、FRONTEND_TYPES 重复
  - `PARSER_ROADMAP.md` 头部加注：正文为大扫除前原始计划记录，代码草图勿照搬
  - `AGENTS.md`/`CLAUDE.md` 同步新文件布局，移除「Utilities.cs 残留」条目
- **测试**：27 套件全绿（用例无增删）+ fuzz 6000；build 0 错误 0 警告

### 2026-07-27 · M29 AST 容器重构：语义字段 + wrapper 挂载接口

> 不改 Latte 语法；纯 AST 结构重构。基类共有字段 `Children`/`Annotations`
> 删除，子节点容器改为各节点上语义明确的 [ChildAstNode] 字段；注解挂载
> 能力按 SYNTAX §14 三类 wrapper 目标抽象为接口。

- **基类瘦身**（`Core/Utilities.cs`）：`ASTNode` 只保留 `Parent`
  （[ParentAstNode]）与 `Span`；`Children`、`Annotations` 两个共有字段删除
- **语义容器字段**（均标 [ChildAstNode]；Validator/ASTVisitor/AstJsonlSerializer
  走 Attribute 反射，零改动）：
  - `RootASTNode.Declarations`：顶层条目（全局声明/import/namespace/顶层字面量）
  - `CodeBlockASTNode.Statements`：块内语句（局部声明同挂）
  - 5 个类型声明节点（Class/Interface/Struct/EnumStruct/Wrapper）各自的 `Members`
- **wrapper 挂载接口**（`AST/DeclarationNodes.cs`）：`IWrapperAttachable` 基接口
  （`Annotations` 属性）+ 三个分类标记接口——`IEntityWrapperAttachable`
  （5 个类型节点，§14.2）、`IMethodWrapperAttachable`（Callable，§14.4）、
  `IValueWrapperAttachable`（Variable，§14.3）；挂载合法性校验留待语义阶段
- **DeclarationParserLayer**：构造函数改收 `(ASTNode parent, List<ASTNode> target)`
  （parent 供节点 Parent 指针、target 供挂接；与 ArgumentListParserLayer 收
  目标列表的先例一致）；新增 `GetMembers` 集中 switch（仿 `GetModifiers`）；
  `AttachAnnotations` 改经 `IWrapperAttachable` 访问
- **测试**：19 个测试文件机械改名（`root.Declarations`/`block.Statements`/
  类型节点 `.Members`），无用例增删、无期望变化；AstJsonlSerializerTests 的
  via 期望串同步（`Children[0]` → `Declarations[0]`）；全部 27 套件 0 失败
  + fuzz 6000/6000

### 2026-07-27 · M28 Lexer 位置修复 + AST Source Span + ASTVisitor 统一遍历

> 不改 Latte 语法；修复 Lexer 位置计量的五个 bug，给每个 AST 节点挂上
> 源码范围 Span（JSONL 同步输出），并把 Validator 与 Serializer 的遍历
> 统一为 ASTVisitor 基建，Validator 新增 span 校验与类型审计。

- **Lexer 位置修复**（`Lexer/Lexer.cs`）：
  - `CharPosition.offset` 恒为 0 —— 主循环从未把字符索引写进位置（已修，
    0 起始字符索引）
  - 普通 token 的 `CharRange.sourceName` 从未设置（仅 EOF 有）；sourceName
    单源化到 `CharRange.sourceName`，`CharPosition.sourceName` 删除
  - 换行即切下一行 col 1 —— 第二行起所有列号 +1（已修：换行算当前行
    最后一列，下一行首字符 col 1）
  - 空白字符被记为 token 头 —— 缩进行 token 的 Start 落在前导空格上
    （已修：token 头跳过空白，换行除外——它是 LineBreakToken）
  - EOF 冲刷帧不占位置 —— EOF 处 Word/Slash 层 token 的 End 少算一个
    字符、换行后甚至范围倒置（已修：虚拟换行占末尾虚拟位置）
- **AST Source Span**：`ASTNode.Span`（`CharRange?`）。填充分两级——
  层目标由 Parser 主循环按层栈跟踪 token 流计算，层弹出时经新接口
  `ISpanReceiver.ReceiveSpan` 回填（约定 `target.Span ??= span` 只填空；
  换行处弹出不拖尾换行符）；层内自建节点由所在层显式设置（创建记
  Start、完成经 `ParserLayerContext.GetPreviousLocation()` 封 End；
  ExpressionParserLayer 以后缀包装/运算符/完成三处封口）。
  `ExpressionRootASTNode` 覆写 Span getter 透明继承内容表达式
  - **JSONL**：`AstJsonlSerializer` 每节点一行新增 `span` 键
    （`{source,startLine,startCol,startOffset,endLine,endCol,endOffset}`）
  - **Validator 新检查**：每节点 span 非空、sourceName 非空、End 不早于
    Start；类型审计——装 ASTNode 的成员（字段/自动属性，经 backing 字段
    识别）必须带 [ChildAstNode]/[ParentAstNode]，[ChildAstNode] 标在非
    AST 成员上同样拒绝
- **ASTVisitor 统一遍历**（`AST/ASTVisitor.cs`）：[ChildAstNode] 子节点
  枚举的唯一实现（含 carrier 下钻、集合下标 via），ASTIntegrityValidator
  与 AstJsonlSerializer 各删一份重复反射；via 格式统一为
  `member[i]`/`member[i](Carrier.Field)`
- **测试**：LexerFuzzTests 新增位置精确性用例 3 例 + 不变量扩展
  （sourceName 非空、offset 不回退、范围不颠倒）；ASTIntegrityValidatorTests
  新增 span 破坏/类型审计用例 5 例；AstJsonlSerializerTests 新增 span 键
  结构断言 4 例；全量 556/556 + fuzz 6000 通过

### 2026-07-27 · M27 CLI 插件化重构（help/compile/test）+ 交互菜单删除

> 不改 Latte 语法；把 M26 的平铺 `--选项` 参数与用户交互菜单统一重构为
> `<COMMAND> [--sub-cmd [args...]...]` 结构，选项全部插件化、帮助程序生成。

- **用法定稿**：`dotnet run -- <COMMAND> [--sub-cmd [args...]...]`，
  COMMAND 三个——`compile`（`--file <路径...>`、`--parse-only`、
  `--dump-ast <路径>`、`--verbose`、`--log-to <路径>`）、`test`（`--all`、
  `--run [编号...]`、`--verbose`、`--log-to`）、`help`（无参概览；
  `help compile` 单 COMMAND；`help compile.file` 单个子命令，名不带 `--`）
- **CommandLineMask**（Core/CommandLine.cs）：选项自描述元数据——Name/
  Description/ArgsHint/MinArgs/MaxArgs（int.MaxValue 表任意个数）/
  MutuallyExclusive；解析器（`--x=v` 与空格两形态、个数/互斥/重复/游离
  参数校验）与 help 文本完全由注册表数据驱动，无手写帮助页
- **插件承载行为**：COMMAND 插件带 `Execute`（解析结果 → 退出码）；
  `--verbose`/`--log-to` 是 compile 与 test 共享的子命令插件类；
  `--all` 与 `--run` 互斥；`test` 裸用或 `--run` 无编号打印套件菜单
- **交互菜单删除**：Program.cs 从 304 行瘦身为 19 行薄入口（解析→分发→
  退出码）；`--test-all` 更名 `test --all`，`--enable-verbose` 更名
  `--verbose`；`--parse-only` 时 AST JSONL 默认输出到 stdout，
  `--dump-ast` 指定文件；编译错误走 stderr，单文件失败不阻断后续文件，
  退出码非零
- **CI 同步**：`.github/workflows/ci.yml` 改为 `dotnet run -- test --all`
- 测试：544/544（27 套件）+ fuzz 6000/6000；新增 CommandLineParserTests
  （46 用例：注册表完整性、COMMAND/子命令匹配、两形态、参数个数、互斥、
  游离参数、重复子命令）

### 2026-07-26 · M26 日志系统（Logger 分级 + JSONL 落盘）+ AST JSONL 序列化

> 不改 Latte 语法；基础设施大扫除：日志分级与分流（控制台/文件），
> 以及 AST 树的 JSONL 序列化诊断能力。

- **Core/Logger.cs**（唯一日志出口）：`Verbose/Warning/Error` 三级；
  控制台门槛默认 Warning+（verbose 默认关闭），`--enable-verbose` 放开；
  `--log-to PATH` 把**全量**日志（含 Verbose）以 JSONL 落盘
  （`{"ts","level","source","message"}` 每行一条，System.Text.Json 序列化），
  文件不过滤级别——诊断时从一大坨日志里 grep 所需
- **接入点收口**：Lexer/Parser 的 `ContextImpl.Log/LogWarning` 改经 Logger
  输出（此前直接 `Console.WriteLine`）；删除 LexerFuzzTests 的
  `Console.SetOut(TextWriter.Null)` 屏蔽 hack（verbose 默认关闭后无意义）
- **AST/AstJsonlSerializer.cs**：AST 树深度优先序列化为 JSONL，每节点一行
  `{id,parent,via,type,fields}`；遍历复用 Validator 的 [ChildAstNode] 反射
  下钻（含 private 字段、IEnumerable 下标、[AstCarrier] 展开）；fields 收集
  标量成员（Symbol 渲染为点分串），排除 Parent/索引器，先按声明类型过滤
  再取值以避开未填充 ExpressionRoot 的抛异常属性
- **Program.cs 参数解析**：从只认 `args[0]=="--test-all"` 扩为循环解析
  （`--test-all` 单独使用行为不变，CI 不受影响）；新增 `--enable-verbose`、
  `--log-to`、`--dump-ast`（支持 `--name=value` 与 `--name value` 两形态；
  未知参数/缺路径 stderr 提示 + 退出码 2）；`--dump-ast` 在交互菜单
  解析文件成功后写出 AST JSONL
- 测试：498/498（26 套件）+ fuzz 6000/6000；新增 LoggerTests（7 用例：
  JSONL 落盘与级别门控）与 AstJsonlSerializerTests（38 用例：JSON 合法性、
  id/parent 链一致性、via/fields 内容断言）

### 2026-07-26 · M25 Lexer 修复：SlashLexerLayer、Lexer EOF、注释集中跳过 + fuzz 基建

> 不改 Latte 语法；修复 Lexer 三个结构性缺陷（除法不可用、EOF 由 Parser
> 伪造、注释处理散落），并建立 Lexer fuzz 测试基建。

- **SlashLexerLayer**：`/`、`/=`、`//`、`/*` 统一分流入口——M25 前 Base 层
  见到 `/` 后若下一字符非注释开头直接报错，**除法与 `/=` 完全不可用**；
  注释形态转发给持有的注释层实例（Delegate, don't implement），
  注释层弹出时本层同步弹出
- **Lexer 输出 EOF**：`EndOfFileToken` 改由 `Lexer.Tokenize` 在输出末尾追加
  （零长度 CharRange，位于文件末尾），Parser 不再自行伪造（仅对绕过 Lexer
  的调用方保持追加兼容）；输入结束以虚拟换行冲刷帧（FlushLayers）驱动各层
  弹栈、不产生任何 token；冲刷后栈不收敛（未闭合字符串/块注释）即
  LexerException；顺带修复空输入 `content[0]` 越界与 `Tokenize(string)` 的
  AggregateException 包装（改 `GetAwaiter().GetResult()` 原样抛出）
- **注释集中跳过**：CommentToken 由 Parser 主循环分发时统一跳过，各
  ParserLayer 不再自行处理（RootParserLayer 的 Comment 分支已删）；
  行注释不再吞掉结尾换行（keepChar 回流，由 Base 层产出 LineBreakToken——
  此前行尾注释会让下一条语句粘行）
- **LexerFuzzTests**（固定用例 23 + fuzz 6000，固定种子可复现）：
  除法/注释/EOF 精确 token 序列断言；纯随机/结构化片段/合法源码变异三类
  fuzz 校验不变量（不崩——只允许 LexerException、EOF 存在且唯一、
  位置单调不回退）；Parser 集成验证注释任意位置不炸语法
- 测试：453/453（24 套件）+ fuzz 6000/6000

### 2026-07-26 · M24 AST 结构标注 + Validator 重写 + 删除 ASTNodeType + 父子指针 bug 修复

> 本次重构不改 Latte 语法；把 AST 结构关系从「约定」变为「显式标注」，
> Validator 改为 Attribute 驱动，并借此抓出并修复 5 个真实的父子指针 bug
> （根因均为「先解析后决定归属 → 事后搬家」，与大扫除的施工协议相违）。

- **结构标注 Attribute**（AST/ASTStructureAttributes.cs）：`[ChildAstNode]`
  标记装子节点的字段/属性（单节点/节点集合/carrier 集合，含 private 字段如
  ExpressionRootASTNode.expression）；`[ParentAstNode]` 标记父指针
  （ASTNode.Parent）；`[AstCarrier]` 标记携带 ASTNode 的非节点对象
  （ImportItem struct）——配合容纳它的成员上的 [ChildAstNode]，
  Validator 深入其公共字段完成子节点遍历
- **Validator 重写**：遍历只走 [ChildAstNode] 成员；新增父子指针一致性校验
  （每个子节点的 Parent 必须指向持有者，carrier 情形为持有集合的节点）；
  原「Expression.Parent 指向 Root」检查被通用校验覆盖；Root 未填充 /
  节点无共享 / Parent 链无环 / switch default 规则保留
- **彻底删除 ASTNodeType**：枚举本体、ASTNode.NodeType 抽象属性、约 50 处
  override、ASTNodeTypeExtensions（无任何使用）全部删除；节点类型一律用
  CLR 类型判断（is / GetType()）
- **无 reparent 原则**：AttachTo 保持一次性；「归属后知」场景一律改用
  创建时归属即定的结构，禁止任何形式的 Parent 重挂
- **修复 Validator 抓出的 5 个父子指针 bug**：
  1. Range 收养（LoopParserLayer）→ 删除 RangeExpressionASTNode，拍平为
     LoopStatementASTNode.Iterable（起点）+ RangeTo（终点，null = 非范围循环）
  2. Assign 收养（CodeBlockParserLayer）→ 删除 AssignStatementASTNode，
     表达式语句与赋值语句统一为 ExpressionStatementASTNode
     （Expression + AssignValue?，两个 Root 槽创建时 Parent 即定）
  3. 注解 Parent 指向声明的父容器 → 构造时 parent 为 null，声明节点创建时
     一次性 AttachTo（DeclarationParserLayer.AttachAnnotations 统一挂接点）
  4. 泛型约束 Target 搬家（GenericParametersParserLayer）→ StartConstraint
     把符号数据（Symbol 为纯数据）灌进 constraint 自带 Target 节点，
     不再挂接外部已建成节点
  5. 注解实参 Parent 指向父容器 → ArgumentListParserLayer 的 parentNode
     改传注解节点本身
- 测试：430/430（23 套件）；新增 ASTIntegrityValidatorTests（5 用例：
  合法树通过 + Parent 指错/carrier 指错/Root 未填充/节点共享拒绝）；
  各套件 Describe 类型分派更新，快照期望保持不变

### 2026-07-26 · M23 Parser/PDA 大扫除（架构重构，依据 great_clean_plan.md）

> 本次重构只调整 Parser 内部架构、AST 构造协议、Token 流转协议与测试基础设施，
> **不修改 Latte 的任何既有语法与语义**（417 个语法用例逐一保持原断言并通过）。

- **TokenDisposition**：`ParserLayerResult.PushLayer/PopLayer` 的 `bool shouldKeepToken`
  全部机械替换为具名枚举 `TokenDisposition.Consume/Replay`（约 260 处调用点）
- **彻底删除 Layer 返回值**：`IResultProducer`/`IResultConsumer`/`GetResult()`/
  `OnChildResult()`/`pendingResultHandler` 全部删除（grep 验收 0 处）；Parser 主循环
  不再包含任何 AST 结果传递逻辑，Layer 之间只传递控制权
- **施工目标协议**：每个 Layer 的构造函数接收明确、强类型的施工目标；
  父层创建/选择目标并传入子层构造函数，子层原地填充或向目标附加子节点；
  数据流严格单向（父→子），禁止任何形式的回传替代机制
- **ExpressionRootASTNode**：Syntax AST 中所有表达式位置的统一稳定挂载点
  （§6.4 清单 30+ 个字段全部迁移）；一次性 `Attach`、禁止替换、禁止附加已有父节点的
  表达式；可选表达式以 null Root 表示；`ASTNode.Parent` 改为只读（只能设置一次）；
  表达式经「未挂载子树包装」组合，ExpressionParser 只在表达式完成时 Attach 最终外层节点；
  赋值目标与范围起点经「Root 收养」转移逻辑归属（不搬家）
- **GroupExpression 独立 NodeType**：`ASTNodeType.GroupExpression`，
  `ValueExpressionRoot` 专属于 ExpressionRootASTNode
- **LiteralASTNode 基类**：6 个字面量节点统一继承；
  `LiteralExpressionASTNode.AttachLiteral` 一次性附加
- **EOF 正式化**：`EndOfFileToken`（TokenType.EndOfFile）由 Parser 在输入**本地副本**
  末尾追加（不再修改调用者列表）；只由 RootParserLayer 消费；非 Root 层遇 EOF：
  结构完整 → Pop(Replay) 层层上交，不完整 → "Unexpected end of file"；
  换行哨兵与 guard 收尾循环（`while stack.Count > 1 && guard++ < 64`）删除，
  解析结束强制 `stack.Count == 1`
- **ParserLayerContext 收缩**：删除 `GetRootNode()` 与 `ContextImpl.current`；
  Parser 直接持有 RootASTNode，Layer 无法经 Context 触碰全局根
- **AST 完整性验证**：新增 `ASTIntegrityValidator`（AST/ASTIntegrityValidator.cs），
  Parse 成功后自动运行：Root 均已填充、Expression.Parent 指向 Root、节点无共享、
  Parent 链无环、switch default 规则；失败抛 `CompilerInternalException`（内部错误，
  与用户语法错误区分）
- **测试基础设施**：
  - `dotnet run -- --test-all` 单命令全量（`TestRunner` 注册全部套件，
    任意失败非零退出码，输出失败套件名）；新增最小 CI（`.github/workflows/ci.yml`：
    `dotnet build` + `--test-all`）
  - `TestRootParserLayer`：独立 Layer 测试改为 `Parse(tokens, TestRoot, entryLayer)`
    驱动——被测 Layer 提前结束或漏消费普通 token 立即失败（14 处调用全部迁移）
  - `TokenDispositionTests`：假 Layer 验证 Push/Pop × Consume/Replay 四种组合的
    token 接收序列（4 用例，菜单 23）
  - 表达式套件新增 AST 结构断言（§13.4：Root 存在/已填充/Expression 类型/
    Parent 链/子 Root 填充/无共享，4 用例，字符串快照不再是唯一验证方式）
- **消除全部编译警告**（原 `Core/Utilities.cs` 的 nullable 警告随 `null!` 模式消失）
- 测试总数 417 → 425（+4 结构断言、+4 TokenDisposition），22 个套件全绿

### 2026-07-26 · M22 namespace 声明（§15.1）—— P5 收官
- 新增 `NamespaceParserLayer`（小 Layer，与 ImportParserLayer 同款结构）：
  `namespace` + 路径（复用 `PathParserLayer`）+ 换行收尾；空路径与路径后多余 token 报错；
  唯一性与位置约束（应在文件首部）留待语义阶段
- `RootParserLayer` 新增 `namespace` 分发（与 import 同款）；新增
  `NamespaceDeclarationASTNode` 与 `Keywords.NAMESPACE`
- 新增 NamespaceTests 7 用例（基本/与 import 组合/3 错误用例）并注册菜单 22；
  全量回归 2–22 无 FAIL
- 测试总数 410 → 417
- **P5 全部完成，Parser 前端规划（roadmap P0–P5）收官**；下一阶段：语义分析、BIL 输出

### 2026-07-26 · M21 模块系统 import + wrapper 路径访问（`:`）
- `ImportParserLayer` 重建（SYNTAX §15.2，roadmap P5）——三种规范形态：
  单个导入 / `.{A, B}` 多个导入（共享前缀，展开为独立完整路径 `ImportItem`）/ `.*` 全部导入；
  前缀路径复用 `PathParserLayer`（遇 `*`/`{`/换行弹出并交还，末尾空名元素统一清理——
  与 GenericParameters 层对 `...` 的既有处理同款）；旧骨架的 `as` alias 与逗号分隔
  多导入不在现行规范内，按规范移除
- wrapper 路径访问（SYNTAX §14.1/§3）：`:` 接入 `ExpressionParserLayer` 后缀链
  （与 `.`/`?.` 同框架——base 为任意路径表达式，如规范示例 `foo().bar[0]?.length:MyWrapper`；
  链式 `obj:A:B` 左结合逐层嵌套）；新增 `WrapperAccessASTNode`
  - 架构判断：`PathParserLayer` 遗留的 `ValuePath`/`AcquisitionExpressionASTNode` 模式
    仅支持纯符号 base，与后缀链架构不兼容，未接入（记入技术债务待清理）
- 新增 ImportTests 14 用例并注册菜单 21；Expression 71 → 75
  （+4：wrapper 访问基本/链式/规范 §3 完整路径/后续成员后缀）
- 测试总数 392 → 410

### 2026-07-26 · M20 P5 起步：wrapper 主体（@ 注解 + `.proxy.*` 代理成员 + 前导点 enum case）
- `@` 注解 / wrapper 应用（SYNTAX §14.5）：`@Name` / `@Name(args)`，可叠加，挂所有声明节点
  - `ASTNode` 基类新增 `Annotations` 列表（仿 M14 `Children` 上移先例）；
    新增 `AnnotationASTNode`（符号路径名 / HasArguments / Arguments）
  - `DeclarationParserLayer` 新增 `AnnotationName` 状态：注解名复用 `PathParserLayer`、
    实参复用 `ArgumentListParserLayer`；声明本体创建时统一挂接暂存注解
  - `RootParserLayer` 的 `@` 预留入口就此接通；`CodeBlockParserLayer` 新增同款 `@` 分发
    （栈上注解变量，§14.3）
  - 规范判断：`@WrapperTarget(.Entity/.Value/.Method)` 即 wrapper 类型标识（§14.2–14.4），
    与普通 wrapper 应用同一语法形态；roadmap 示例中的 `wrapper X entity` 后缀写法以规范为准，不实现
- 前导点 enum case 引用（§12）：`ExpressionParserLayer` 新增 `EnumCaseNameExpected` 状态 +
  `EnumCaseExpressionASTNode`；参数化 case 调用（`.Failed(404)`）由后缀链自然脱糖为 Call，零新增代码
- `.proxy.*` 代理成员（§14.2/§14.6）：operator 名允许 `.proxy.<category?>.<name|*>` 限定名
  （仅 wrapper 体内、仅 operator）；复用 `CallableNameDot` 状态拼接，首段必须为 `proxy`、
  wildcard `*` 必须收尾；wrapper 体结束时校验同类 wildcard 唯一（与 enum case 校验同一先例）
- 顺带修复：`CodeBlockASTNode` 隐藏了基类 `Children` 字段（M14 上移时的漏网之鱼）——
  以 `ASTNode` 静态类型挂入的声明在块视角下不可见；删除隐藏字段，`Children` 回归基类唯一来源
- 测试：TypeDeclaration 77 → 94（+17：@ 注解 7、proxy 10）、Expression 67 → 71（+4：enum case）、
  CodeBlock 27 → 29（+2：栈上注解变量）；全量回归 2–20 无 FAIL
- 测试总数 369 → 392

### 2026-07-26 · M19 like 委托 + ext 扩展成员 —— P3/P4 收官
- `like` 委托（§9.6，roadmap #13 剩余项）：统一声明层新增 LikeExpected/AfterLike
  两个状态，`class A : Base implements I like field {` 与省略基类的 `class A like x {`
  均可解析；仅 class 可委托（struct/interface/wrapper 报错）；AST 为
  `ClassDeclarationASTNode.LikeTarget`（可空字符串，不加节点）
- `ext` 扩展成员（§4.4）：补 `ext` 关键字（Keywords.EXT + DeclarationDescriptors）；
  限定名 `Type.member`（可多段路径）——Callable 侧在 ParamsExpected 加 `.` 分支
  （extSeen 门控，非 ext 自然落错误分支），var/const 侧经
  `VariableDeclarationParserLayer(allowExtension)` 同构支持；与 M16 会师：
  `pub ext var String.isEmpty: bool { get(_: _) { ... } }` 完整可解析
- 测试：TypeDeclaration 68 → 77（+9：like 规范形态/省略基类/2 错误、ext 函数/字段/2 错误）；
  全量回归 2–20 无 FAIL
- 测试总数 360 → 369
- **P3（#13–16）与 P4（#17–19）全部完成**；下一阶段 P5：wrapper 主体、模块系统、
  wrapper 路径访问（`:`）

### 2026-07-26 · M18 init 参数映射（_ -> field，SYNTAX §9.3）
- `ParameterListParserLayer` 增加 `allowMapping` 开关（默认 false，仅 init 传入 true）：
  `_ -> x`（同名映射）、`_ -> x = 0`（带默认值）、`horizontal: i32 -> x`（显式名 + 类型）、
  与普通参数混合；新增 MappedFieldExpected / AfterMappedField 两个状态
- `ParameterASTNode` 增加 `MappedFieldName`（可空）；映射参数省略类型时
  Type 保持空引用节点（沿用字段类型，语义阶段回填）
- 非 init 形参列表出现 `->` 自然落到既有错误分支（func/lambda/operator 均拒绝）
- 与 M17 会师：§12 Direction 规范示例（`priv init(_ -> degrees)` + `[case 列表]`）完整可解析
- 测试：TypeDeclaration 61 → 68（+7：§9.3 全形态、混合、struct/enum 集成、3 个错误用例）；
  全量回归 2–20 无 FAIL
- 测试总数 353 → 360
- 已知缺口：§9.3 语法行中的 `[modifier...]` 形参修饰符无任何规范示例，暂不支持（出现时按普通标识符报错）

### 2026-07-26 · M17 enum struct 的 [] case 列表（SYNTAX §12）
- 在统一 `DeclarationParserLayer` 内联扩展（不新建 Layer）：enum 体 `}` 后进入
  case 列表子状态机（EnumCaseListOpen/EnumCaseStart/EnumAfterName/EnumAfterArgs/
  EnumDiscriminant/EnumAfterCase 六个状态）
- 形态全覆盖：固定 case（`North(0)`、无参 `Red`）、参数化 case（`_` 参数洞 +
  具名实参 `Failed(errorCode = _)`）、显式判别值（`-> N`，§12.4）；
  case 实参复用 `ArgumentListParserLayer`（开括号由本层消费——与调用点既有约定一致）
- 解析期校验：case 名唯一、判别值唯一、「全显式或全分配」不得混用（§12.4）、
  判别值必须是非负整数字面量
- `[` 必须与 `}` 同行：换行即声明结束（与 callable 体 `{` 的既有约定一致），
  同时保证 EOF 哨兵下无 case 的 enum 能正常收敛
- 测试：TypeDeclaration 51 → 61（+10：固定/参数化/判别值/无 case 缺省 + 4 个错误用例）；
  全量回归 2–20 无 FAIL
- 测试总数 343 → 353

### 2026-07-26 · M16 属性访问器 getter/setter（§9.4，roadmap #23）
- 新增 `PropertyAccessorParserLayer` 解析变量声明后的 `{ get... set... }` 访问器块；
  三类定义位置（类/struct 字段、全局变量、栈上 var/const）不经任何改动即覆盖——
  它们早已统一汇聚到 `VariableDeclarationParserLayer`，只需在其 NameSeen/TypeSeen
  状态各加一个 `{` 分支（Delegate, don't implement）
- 访问器形态全覆盖：编译器生成（`pub get` 无参无体）、`(value: _) + 自定义体`
  （backing field）、`(_: _) + 自定义体`（计算属性）；访问器体复用 CodeBlockParserLayer
- 解析期校验：同块重复 get/set 报错、空块报错、`(_: _)` 与 `(value: _)` 混用报错
  （§9.4「get 和 set 在是否需要 backing field 上必须保持一致」）
- 自动访问器以换行或 `}` 收尾——与成员声明的换行分隔规则一致
- AST：新增 `PropertyAccessorASTNode`（Kind/HasBackingField/Body），
  `VariableDeclarationASTNode` 增加 Getter/Setter 两个可空字段，不加包装层
- 测试：新增 PropertyAccessorTests 17 用例（形态/跨行/三类位置/5 个错误用例），
  注册菜单选项 20；全量回归 2–20 无 FAIL
- 测试总数 326 → 343

### 2026-07-26 · M15 声明泛型参数接入统一声明层
- 类型/函数声明接入既有 `GenericParametersParserLayer`（roadmap P3 收尾第 1 项，不新建任何 Layer）：
  - Callable（func/operator/init 共用）：`ParamsExpected` 状态识别 `\`，push 泛型层后**保持原状态**，列表弹出后仍等待 `(`
  - 类型（class/interface/struct/enum struct/wrapper 共用）：`AfterTypeName` 状态识别 `\`，同样保持原状态，列表弹出后仍等待 `:` / `implements` / `{`
  - 两处接入均不新增状态机状态，与 LambdaExpressionParserLayer 的既有接入模式一致（简洁优先三问：复用已有轮子，不加状态）
  - 5 个类型节点的同名 `GenericParameters` 字段集中分派（新增 `SetGenericParameters`，与 `SetTypeName` 同一 pattern）
- 覆盖 SYNTAX §3.6 全形态：多参数、out/in 型变、extends/supers/with 约束、可变/具名可变参数、型变+约束组合；全局函数与成员方法/operator 同一条路径；泛型列表位置与继承子句的先后关系符合规范（`Name\<T> : Base implements I {}`）
- 测试：TypeDeclaration 36 → 51（+15：泛型类型/接口/struct/wrapper、泛型+继承+implements、全局与成员泛型函数、泛型 operator、约束、可变参数、型变+约束）；全量回归 2–19 无 FAIL
- 测试总数 311 → 326

### 2026-07-26 · M14 统一声明层：全局/成员/嵌套共用一套 infra
- 核心依据 SYNTAX.md §14.8：canonical symbol 的类名段可为空、`.static.` 只是标记位，因此"全局函数"与"成员方法"结构同构——三类位置合并为一条代码路径，而不是各造轮子
- `DeclarationParserLayer` 成为任何位置任何声明的唯一入口：
  - 全局字段 / 类字段 → 复用 `VariableDeclarationParserLayer`
  - 全局函数 / 方法 / static / operator / init → 同一套 Callable 状态，只改 `Kind`
  - 参数列表 → 复用 `ParameterListParserLayer`；返回类型 / 基类 / 接口 → 复用 `TypeReferenceParserLayer`；函数体 → 复用 `CodeBlockParserLayer`
  - 嵌套类型 → 类型体内递归 push 本层自身（与顶层同一路径）
- 配套简化（删冗余，不加轮子）：
  - 新增 `CallableDeclarationASTNode` 一个节点覆盖 func/operator/init（`CallableKind` 枚举）
  - 删除 `DeclarationASTNode` 包装层（其 type 枚举与 Declaration 指针只是对 C# 节点类型的重复表达）
  - 删除 5 个类型节点各自的 `CodeBlockASTNode Body` 字段，成员统一挂 `ASTNode.Children`（Children 从 RootASTNode 上移到基类）
  - RootParserLayer 的 var/const 专用分支合并进通用声明分支
- `Parser.cs` 修复 EOF 收尾：嵌套委托后父层仍需一个终止 token 才能收敛，原逻辑只喂一个哨兵便判定 `stack.Count > 1` → "Unexpected End"；改为反复喂哨兵直到栈收敛或无进展（带 guard 防死循环）
- 规范判断：interface 的 `: Base` 按 SYNTAX §11 是父接口，落 `BaseInterfaces` 而非 `Interfaces`
- 测试：TypeDeclaration 16 → 36（新增全局字段/全局函数、类成员、init/operator、继承与 implements 列表、三层嵌套类型）；全量回归 2–19 全绿（其中 5/6/9/10/11/12 由 EOF 修复恢复）
- 测试总数 275 → 311

### 2026-07-26 · 修复：TypeDeclaration 测试的 double-root 问题
- 根因：`Parser.Parse` 内部已压入 RootParserLayer，测试又把 `new RootParserLayer(root)` 当 entryLayer 传入，栈里出现两个永不 pop 的 root 层，循环结束时 `stack.Count > 1` 触发 "Unexpected End"
- 修复：测试改用 `parser.Parse(tokens)` 并取其返回值作为根节点（一行改动）
- 同提交把「简洁优先三问」原则写入 CLAUDE.md / AGENTS.md（含项目内已验证的复用范例表）
- 修完 TypeDeclaration 16/16 通过，套件 2–19 全量回归无失败

### 2026-07-26 · M13 P3 类型声明解析基础（DeclarationParserLayer 重构）
- 前置提交：5 个类型声明 AST 节点（Class/Interface/Struct/EnumStruct(+EnumCase)/Wrapper）+ 关键字补齐（pub/priv/open/abstract/singleton/shared/rich/implements/like/init/get/set 等）
- 扩展现有 `DeclarationParserLayer` 骨架（不新建 Layer）解析类型声明头部：修饰符 → 类型名 → 继承/接口 → 体
- 修饰符识别：pub, priv, open, abstract, singleton, shared, rich, static, override, async
- 更新关键字数组：DeclarationKeywords/TypeKeywords 添加 enum，DeclarationDescriptors 添加全部新修饰符
- 新增 TypeDeclarationTests 16 用例（简单声明、带修饰符、5 种类型），菜单注册选项 19
- 提交时测试未过（WIP），由随后的 double-root 修复转绿

### 2026-07-26 · M12 CoroutineOps：await/yield（roadmap #12，P2 完成）
- **await**：一元前缀运算符，在 ExpressionParserLayer 中处理（`IsPrefixUnaryOperator` 添加 `Keywords.AWAIT`）
- await 产生 UnaryExpressionASTNode，operator 为 "await"
- await 可在变量初始化、if 条件、return 等任意表达式位置使用
- **yield**：语句，在 CodeBlockParserLayer 中内联处理（类似 return/throw）
- YieldStatementASTNode：包含可选的 Alarm 表达式
- 裸 yield（不带表达式）：直接结束当前执行段
- yield alarm（带表达式）：委托 ExpressionParserLayer 解析 alarm
- 添加 Keywords.AWAIT 和 Keywords.YIELD
- 测试 262 → 275（+13：await 表达式、yield 语句、await+yield 组合、不同上下文）
- **P2 语句系统全部完成！**

### 2026-07-26 · M11 throw 语句
- 在 CodeBlockParserLayer 中内联处理 throw 语句（类似 return/break/continue）
- ThrowStatementASTNode：包含异常表达式，必须紧跟 throw 关键字
- throw 后委托 ExpressionParserLayer 解析异常表达式
- 状态机：throw 已读 → ThrowValue（解析表达式）→ StatementEnd
- 配合 try-catch-finally 构成完整异常处理系统
- 测试 252 → 262（+10：简单 throw、throw 表达式、throw 构造、配合 try-catch、不同上下文、错误用例）
- 异常处理系统完整：try-catch-finally（捕获）+ throw（抛出）

### 2026-07-26 · M10 SeqBlockParserLayer（roadmap #11，含表达式形态）
- SeqBlockExpressionASTNode：从 StatementNode 移至 ExpressionNode，继承自 ExpressionASTNode，实现语句+表达式双形态
- SeqBlockParserLayer 实现 IResultProducer，支持作为表达式返回节点（通过结果传递机制）
- ExpressionParserLayer 集成：在 Primary 状态识别 seq/volatile 关键字，委托给 SeqBlockParserLayer（shouldKeepToken: true）
- seq 作为表达式：`var result = seq { return@seq compute() }`、`var r = seq named calc { return@calc getValue() }`
- 架构洞察：seq/lambda/方法等的代码块共用 CodeBlockParserLayer 基建，解析逻辑统一
- 测试 249 → 252（+3：seq 作为表达式的 3 个用例）
- 完整覆盖：简单 seq、volatile、using（单个/多个/带类型）、named、组合、表达式形态、错误用例

### 2026-07-26 · M10 SeqBlockParserLayer（roadmap #11）
- SeqBlockParserLayer 完整实现：seq 块的作用域、volatile 修饰符、using 资源绑定、named 标签
- using 绑定支持多个资源，每个绑定为 `using(const/var name[:Type] = initializer)`
- using 绑定委托 VariableDeclarationParserLayer 的子集逻辑：类型标注委托 TypeReferenceParserLayer，初始化委托 ExpressionParserLayer
- seq 代码块委托 CodeBlockParserLayer 解析，支持所有已实现语句类型
- 新增 AST 节点：SeqBlockStatementASTNode、UsingBindingASTNode
- 新增 ASTNodeType：SeqBlockStatement、UsingBinding
- 新增关键字：seq、using、volatile
- CodeBlockParserLayer 集成 seq/volatile 语句分发
- 测试 235 → 249（+14：简单 seq、volatile、using 单个/多个/带类型、named、组合、4 个错误用例）
- 限制：seq 作为表达式（return@seq/return@label）需要在 ExpressionParserLayer 中集成，当前仅支持语句形态

### 2026-07-26 · M9 TryCatchFinallyParserLayer（roadmap #10）
- TryCatchFinallyParserLayer 完整实现：try 块、多个 catch 子句（异常变量可为 _ 表示丢弃）、finally(e) 参数（e 为异常或 null）
- catch 异常类型委托 TypeReferenceParserLayer 解析，支持完整类型引用（含泛型、可空）
- catch/finally 代码块委托 CodeBlockParserLayer 解析，支持所有已实现语句类型
- 新增 AST 节点：TryCatchFinallyStatementASTNode、CatchClauseASTNode
- 新增 ASTNodeType：TryCatchFinallyStatement、CatchClause
- 新增关键字：throw（常量定义，解析留待后续）
- CodeBlockParserLayer 集成 try 语句分发：try 关键字触发 TryCatchFinallyParserLayer
- 测试 226 → 235（+9：简单 try-catch、多 catch、丢弃变量、try-finally、try-catch-finally、嵌套 try、3 个错误用例）
- 验证通过：至少一个 catch 或一个 finally、catch 后必须有类型、finally 后必须有参数

### 2026-07-18 · M7 P2 语句系统核心（CodeBlock / if 语句 / 循环 / return / 赋值）
- CodeBlockParserLayer 重写：语句识别与分发中枢；语句以换行或 `}` 结束；var/const、if、for/while/do 委托专门层，return/break/continue 以内部子状态直接处理，表达式语句后跟 `=` 转为赋值
- IfStatementParserLayer 扩展语句模式：else 可选、支持 else if 链（ElseBranch 为块或嵌套 IfStatement）
- LoopParserLayer（roadmap #9）全形态：for-each/范围（`0 to 10` → RangeExpressionASTNode）/while/do-while/named 标签
- 新增 AST/StatementNodes.cs（CodeBlock/IfStatement/Loop/Return/LoopControl/Assign）；Keywords 新增 return/break/continue/to/do
- VariableDeclarationParserLayer 修复：`}` 可终止块内末语句（三个结束状态）
- 顺带修复：return 带值时 handler 捕获已置空字段的 NRE；@标签/named 标签增加标识符首字符校验（拒绝数字）
- 测试 184 → 226（+42：CodeBlock 27、Loop 15）

### 2026-07-18 · M8 P1 收尾（Lambda / if / switch 表达式 + typeOf/as/is）
- LambdaExpressionParserLayer（roadmap #21 提前落地）：完整/泛型/async lambda、trailing lambda（脱糖为以 lambda 为唯一实参的调用）；形参/泛型形参/返回类型分别复用 ParameterList/GenericParameters/TypeReference 层
- IfStatementParserLayer / SwitchStatementParserLayer（roadmap #7/#8 表达式模式）：if 表达式强制 else、switch 表达式强制 default；`_` 模式匹配按普通符号解析
- TypeOfExpressionParserLayer；is/supers/with/as/as? 改为专用 AST 节点（CastExpressionASTNode/TypeCheckExpressionASTNode），右侧委托 TypeReferenceParserLayer，is/as 不再按二元运算符处理；as? 安全转换标记在类型操作等待态消费
- 结构化 Layer 统一允许跨行书写：结构性等待状态跳过换行，表达式内部仍由 ExpressionParserLayer 按行终止
- 新增 ASTNodeType：IfExpression/SwitchExpression/TypeOfExpression/CastExpression/TypeCheckExpression；新增 Keywords：async/switch/typeOf
- 测试 145 → 184（+39：表达式套件 +13、Lambda 15、if 8、switch 6、typeOf 7）；roadmap #4 全部完成，P1 收官

### 2026-07-17 · M6 ParameterListParserLayer
- 形参全态：普通/默认/可变/具名可变；默认值委托 ExpressionParserLayer，类型委托 TypeReferenceParserLayer
- 修复 `...` 解析在符号末尾残留空名元素的问题（提交前清理）
- 测试 14/14；roadmap #5 完成

### 2026-07-17 · M5 表达式后缀链 + ArgumentListParserLayer
- 后缀链：调用 `(`、索引 `[`、成员 `.`、安全访问 `?.`、泛型实参 `\<`，任意链式组合
- 实参列表支持位置/具名/混合；new 构造参数接入
- 纯符号路径保持 Symbol 形态（存量测试零破坏）
- 表达式套件扩至 54/54；roadmap #4 大部分完成

### 2026-07-17 · M4 GenericParametersParserLayer
- 泛型声明全态：参数/out/in 型变/可变参数/extends/supers/with 约束
- 新增 `Parser.Parse(tokens, entryLayer)` 重载，任意 Layer 可独立测试
- 测试 21/21；roadmap #22 完成（P6 提前落地）

### 2026-07-17 · M3 泛型语法迁移 `\<...>`
- 语言修订：泛型列表统一 `\<` 开启、`>` 闭合；`<` 解放为小于号
- 文档全量迁移（SYNTAX/RUNTIME/BIL 引用/ROADMAP/活文档）
- Lexer 不合并 `>` 系列；表达式层重组 `>=`/`>>`/`>>>`
- SymbolLayer 以 `\` + `<` 进入泛型模式；嵌套泛型 `>>` 修复
- 测试 18/18（GenericParsingTests）

### 2026-07-17 · M2 结果传递机制
- IResultProducer/IResultConsumer 接口 + 弹层自动传递
- VariableDeclaration.Initializer 真正保存；ExpressionParserLayer 接入（字面量包装/分组/一元/二元右操作数回填）
- 无优先级规则强制：`1 + 2 * 3`、`not not x`、`-x + y` 均报错
- 顺带修复：二元 Completed 状态未处理、十六进制 `0xFF` 后缀误判（值为 15 的隐藏 bug）
- 表达式测试 29/29

### 2026-07-17 · M1 P0 核心基础
- LiteralParserLayer（15/15）、TypeReferenceParserLayer（3/3）、VariableDeclarationParserLayer（10/10）
- 层栈式 Parser 架构验证可行（PushLayer/PopLayer/Continue 协议）

---

**格式说明**：后续里程碑在「里程碑历史」**顶部**追加新段落（倒序），并同步更新 §1 总览、§2 可解析语法、§3 组件状态、§5 下一步、§6 技术债务。
