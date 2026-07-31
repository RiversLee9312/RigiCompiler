# LatteCompiler 项目指南（AGENTS.md）

> **用途**: 为 AI 编码代理提供 Latte 编译器项目的完整上下文。读者默认对本项目一无所知。
> 本文件与 `CLAUDE.md` 并存，内容以实际代码为准（已验证日期：2026-07-28）。

**项目名**: LatteCompiler
**语言**: C#（.NET 8.0，控制台程序，`Nullable` 与 `ImplicitUsings` 已启用）
**开发阶段**: 中端（语义分析 + BIL 生成）阶段 —— 编译器前端（Lexer + Parser）已完成（roadmap P0–P5 全部落地，M23–M34 大扫除与多轮修复）；中端 M35 完成架构定稿（`docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md`）与路线图 S0–S14（`SEMANTIC_ROADMAP.md`，S0–S6 已细化到文件级施工清单）+ 语言规范修订（shared/rich/wrapper/String）；M36 落地 S0 诊断基建（`Semantic/Diagnostics.cs`）、M37 落地 S1 符号图内核（`Semantic/Symbols/` + bootstrap + `CanonicalSymbolPrinter`）、M38 落地 S4 BIL 对象模型 + BilWriter（`Bil/`，§19 黄金示例逐行一致）、M39 落地 S2 P1 声明收集（`Semantic/CompilationUnit.cs` + `Semantic/DeclarationCollector.cs`）；下一步 S3 P2 声明解析 → S5/S6 P3/P4 最小闭环
**版本控制**: Git（`main` 分支，2026-07-17 首次提交，工作树干净；CI 见 `.github/workflows/ci.yml`）

---

## 1. 项目概述

Latte 是一门现代的、类型安全的编程语言，本仓库是它的编译器。语言设计目标：

- **完全具化的泛型**：运行时类型信息完全保留（reified），不擦除
- **协程为核心**：从 `main` 开始的原生协程支持
- **值类型/引用类型分离**，`rich`/`shared` 类型声明修饰符
- **Wrapper 系统**：类似 Python 装饰器 + Java 注解的修饰器机制
- **无运算符优先级**：所有运算必须用括号明确指定（见 §4.1）

编译器目标架构：

```
Latte 源码 (.latte) → Frontend (Lexer + Parser) ✅ 完成（含大扫除重构） → 语义分析 ← 当前阶段
                    → BIL (Basic Intermediate Language)
                    → Middleware (LLVM IR Generator)
                    → LLVM 工具链 → 原生可执行文件
```

**当前进度**：编译器前端（Lexer + Parser）已完成，且经过一次彻底的架构大扫除（见 §4.7）：控制流系统与 AST 施工系统严格分离，Layer 之间只传递控制权不传递 AST 数据。已可解析字面量、类型引用、变量声明（含 getter/setter 属性访问器）、完整表达式（含 Lambda（单表达式/多语句块体 + named）、if/switch 表达式（分支体为代码块，`return@_`/named 取值）、typeOf/as/is、seq 表达式形态、await、前导点 enum case 引用、wrapper 路径访问 `:`）、完整语句系统（代码块、if、switch 语句、循环、try-catch-finally、seq、throw、yield、return/break/continue、赋值；lambda 体内裸 return 为编译错误）、泛型参数列表、函数形参列表（含 init `_ -> field` 参数映射）、统一声明层（全局字段/函数、class/interface/struct/wrapper 声明、成员方法与 init/operator、继承与 implements、like 委托、ext 限定名、嵌套类型、声明上的泛型参数、enum struct 的 `[]` case 列表）、wrapper 主体（`@` 注解/wrapper 应用、`@WrapperTarget(.X)` 类型标识、`.proxy.*` 代理成员）、模块系统（import §15.2 三种形态、namespace 声明 §15.1）。**中端三条基建线已就位（M36–M38）**：可恢复诊断模型（Diagnostic/DiagnosticBag）、符号图内核（驻留 + bootstrap + canonical 打印）、BIL 对象模型 + 文本生成；尚无 P1–P4 pass 实现（声明收集/解析、Binder、Lowering），无 BIL 发射。

---

## 2. 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、0 警告
dotnet clean
```

唯一配置文件是 `LatteCompiler.csproj`（无 NuGet 第三方依赖，纯 BCL）。另有 `LatteCompiler.sln`。

### 2.2 运行

CLI 结构为 `<COMMAND> [--sub-cmd [args...]...]`，顶层 COMMAND 三个：`compile` / `test` / `help`（M27 起，交互菜单已删除）。裸 `dotnet run` 等价于 `help`。

```bash
dotnet run -- test --all                 # 全量测试（CI 入口；任意失败非零退出码并列出失败套件名）
dotnet run -- test                       # 打印测试套件菜单（编号 + 名称）
dotnet run -- test --run 1 7             # 按编号运行指定套件（字面量 + 形参列表）
dotnet run -- compile --file a.latte                    # 编译（当前无后端，执行词法+语法解析）
dotnet run -- compile --file a.latte --parse-only       # 只解析，AST 以 JSONL 输出到 stdout
dotnet run -- compile --file a.latte --parse-only --dump-ast ast.jsonl   # AST JSONL 写文件
dotnet run -- help                       # 全部 COMMAND 与子命令概览（文本由注册表程序生成）
dotnet run -- help compile               # 单个 COMMAND 详情
dotnet run -- help compile.file          # 单个子命令详情（子命令名不带 -- 前缀）
```

诊断子命令（`compile` 与 `test` 共有，可组合）：

```bash
dotnet run -- test --all --verbose      # 控制台输出 verbose 级日志（默认只显示 Warning+）
dotnet run -- test --all --log-to run.jsonl   # 全量日志（含 verbose）以 JSONL 落盘
```

---

## 3. 代码库结构

```
LatteCompiler/
├── Program.cs                # 薄入口：命令行解析 → 分发 → 退出码（M27 起无交互菜单）
├── LatteCompiler.csproj      # net8.0，Exe，Nullable enable
├── AST/                      # AST 节点定义（按类别分文件）
│   ├── ASTNode.cs               # AST 节点基类 + RootASTNode（M30 迁出 Utilities.cs）
│   ├── SymbolNodes.cs           # 符号结构（Symbol/SymbolElement/SymbolASTNode）
│   ├── ImportNodes.cs           # import 声明节点（ImportASTNode + [AstCarrier] ImportItem）
│   ├── LiteralNodes.cs          # 字面量节点（LiteralASTNode 基类 + Int/Float/String/Bool/Null 等）
│   ├── TypeNodes.cs             # 类型引用节点
│   ├── DeclarationNodes.cs      # 声明节点（变量声明等）
│   ├── ExpressionNodes.cs       # 表达式节点（含 ExpressionRootASTNode 挂载点）
│   ├── StatementNodes.cs        # 语句节点（代码块/if/循环/return/赋值等）
│   ├── ASTIntegrityValidator.cs # AST 完整性验证器（Parse 成功后自动运行，[ChildAstNode]/[AstCarrier] 标注驱动；
│   │                            #   含 Span 校验与「未标注 AST 成员」类型审计，M28）
│   ├── ASTVisitor.cs            # 统一 AST 遍历基建（[ChildAstNode] 子节点枚举唯一实现，M28）
│   ├── AstJsonlSerializer.cs   # AST 树 JSONL 序列化 v2（carrier 记录化、字段名键控，--dump-ast 输出）
│   └── AstJsonlDeserializer.cs # JSONL → AST 完整反序列化（M31，产物强制过 Validator）
├── Parser/                   # Parser 层实现（每层一个文件）
│   ├── Parser.cs                # 核心协议：IParserLayer、ParserLayerResult、
│   │                            #   TokenDisposition、ParserLayerContext、Parser 主循环
│   ├── Keywords.cs              # 关键字常量（Lexer 不区分关键字，由 Parser 比对识别，M30）
│   ├── RootParserLayer.cs       # 解析入口层，负责识别顶层结构并委托
│   ├── LiteralParserLayer.cs    # 字面量
│   ├── TypeReferenceParserLayer.cs  # 类型引用（不含 rich/shared，见 §4.2）
│   ├── VariableDeclarationParserLayer.cs
│   ├── ExpressionParserLayer.cs # 表达式框架（识别 + 运算符 + 委托）
│   ├── PathParserLayer.cs       # 符号/路径/调用
│   ├── DeclarationParserLayer.cs / ImportParserLayer.cs / CodeBlockParserLayer.cs
│   ├── GenericParametersParserLayer.cs  # 泛型形参列表
│   ├── ParameterListParserLayer.cs      # 函数形参列表
│   ├── ArgumentListParserLayer.cs       # 调用实参列表
│   ├── LambdaExpressionParserLayer.cs   # Lambda 表达式（含 async、trailing；体双形态：单表达式/块，named）
│   ├── IfStatementParserLayer.cs        # if 表达式（分支体为代码块 + named）+ if 语句（else if 链）
│   ├── SwitchStatementParserLayer.cs    # switch 表达式 + switch 语句（分支体为代码块，强制 default）
│   ├── TypeOfExpressionParserLayer.cs   # typeOf 表达式
│   ├── LoopParserLayer.cs               # 循环（for/while/do-while/named 标签）
│   ├── SeqBlockParserLayer.cs           # seq 块（volatile/using/named，语句+表达式双形态）
│   ├── TryCatchFinallyParserLayer.cs    # try/多 catch/finally(e)
│   ├── PropertyAccessorParserLayer.cs   # 属性访问器块 { get... set... }（§9.4）
│   ├── NamespaceParserLayer.cs          # namespace 声明（§15.1）
│   └── DeclarationParserLayer.cs        # 统一声明层：全局/成员/嵌套任何声明（P3）
├── Lexer/                    # 词法分析
│   ├── Lexer.cs                 # Tokenize(TextReader/string) 入口
│   ├── Tokens.cs                # Token 定义（TokenType + Word/String/Notation/Comment/LineBreak/EndOfFile，M30）
│   ├── Notations.cs             # 符号常量（单字符/多字符记号，M30）
│   └── LexerLayers.cs
├── Core/                     # 基础设施
│   ├── Exceptions.cs            # LexerException / ParserException（用户源码错误，M30）
│   ├── CommandLine.cs           # CLI 内核：CommandLineMask（选项自描述元数据）、数据驱动解析器、
│   │                            #   注册表、帮助文本程序生成（M27）
│   ├── Commands.cs              # CLI 插件：compile/test/help 三个 COMMAND 及其 --sub-cmd（M27）
│   └── Logger.cs                # 唯一日志出口：Verbose/Warning/Error 分级；verbose 默认关闭，
│                                #   --verbose 开控制台 verbose，--log-to 全量 JSONL 落盘
├── Semantic/                 # 中端 P1–P3 + 符号图 + 诊断（M36 起，ARCHITECTURE §9）
│   ├── Diagnostics.cs           # 可恢复诊断模型（M36）：Diagnostic{Severity/Phase/Span?/Message}
│   │                            #   + DiagnosticBag（全编译单元单实例、只追加、HasErrors 门槛）
│   ├── CompilationUnit.cs       # 编译单元模型（M39）：多源文件 RootASTNode + DiagnosticBag + SymbolGraph
│   ├── DeclarationCollector.cs  # P1 声明收集（M39）：符号壳 + DeclarationCollection/FileContext
│   │                            #   + namespace 驻留合并 + import 登记 + ext 待注册 + 重复诊断
│   └── Symbols/                 # 符号图内核（M37，M39 增补容器成员表/全局命名空间驻留）：
│                                #   SemanticSymbol 家族（引用相等即身份）、SymbolGraph 构造泛型
│                                #   驻留 + Freeze、BootstrapSymbols、CanonicalSymbolPrinter
├── Bil/                      # BIL 生态（M38，对中端零依赖：字符串身份，不引用 Semantic/AST）
│   ├── BilModule.cs             # Module/Metadata/Resources（§4/§18 全形态）
│   ├── BilSymbols.cs            # 类型与成员声明（§8.2–§8.5）
│   ├── BilFunction.cs           # Function/.args/.vars/Block（§9）
│   ├── BilInstructions.cs       # 指令与操作数模型（§10–§16；§17 协程暂缓）+ Origin(object?) 占位
│   └── BilWriter.cs             # 模型 → 标准 BIL 文本（§19 黄金示例逐行一致）
├── Tests/                    # 自研控制台测试（非 xUnit/NUnit，见 §5）
│   ├── AstDescribe.cs           # 统一 AST 描述器（M31，全部套件共用）
│   ├── TestHarness.cs           # 统一驱动与断言基建（M31；M36 增 CheckSemanticError）
│   ├── TestRunner.cs            # test 命令驱动（套件注册表、菜单打印、按编号运行、退出码）
│   ├── TestRootParserLayer.cs   # 独立 Layer 测试垫底层（只接受 EOF）
│   ├── TokenDispositionTests.cs # Token 流转协议测试（四种组合）
│   ├── ASTIntegrityValidatorTests.cs # Validator 直调测试（合法树 + 结构破坏拒绝）
│   └── LexerFuzzTests.cs        # Lexer fuzz 测试（Slash/EOF/注释 + 6000 随机用例）
└── docs/                     # 设计与规范文档（全部为权威参考）
```

### 3.1 关键文件

| 文件 | 用途 | 重要性 |
|------|------|--------|
| `docs/SYNTAX.md` | **语言语法规范（最权威）** | ⭐⭐⭐ 有歧义时以此为准，不要猜语法 |
| `docs/RUNTIME.md` | 运行时模型与类型系统 | ⭐⭐⭐ |
| `docs/BIL_STANDARD.md` | BIL 中间语言规范 | ⭐⭐ |
| `docs/compiler/syntax/PARSER_ROADMAP.md` / `docs/PROGRESS_REPORT.md` | Parser 路线图与进度 | ⭐⭐ |
| `docs/compiler/syntax/EXPRESSION_ARCHITECTURE.md` | 表达式架构专项设计 | ⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md` | 语义分析与 BIL 生成架构（中端） | ⭐⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ROADMAP.md` | 语义分析路线图 | ⭐⭐ |
| `Parser/Parser.cs` | 层栈式 Parser 的核心协议 | ⭐⭐⭐ |
| `Lexer/Tokens.cs` / `Parser/Keywords.cs` / `AST/ASTNode.cs` | Token/关键字/AST 基类等核心数据结构（M30 拆分自原 `Core/Utilities.cs`） | ⭐⭐⭐ |

---

## 4. 核心设计决策（改动代码前必须理解）

### 4.1 ⚠️ Latte 没有运算符优先级

```latte
var result = 1 + 2 * 3       // ❌ 编译错误：歧义
var result = 1 + (2 * 3)     // ✅ 必须加括号
```

对 Parser 的影响：不需要优先级表；遇到未括号化的连续运算符必须报错。实现表达式相关功能时不要引入优先级概念。

### 4.2 ⚠️ `rich` / `shared` 是类型**声明**修饰符，不是类型引用修饰符

- `rich`：**仅用于 struct / enum struct / wrapper**（class 不能用）。允许值类型持有引用，但仍是值语义、unique ownership（类似 `unique_ptr`，**不是** `shared_ptr`）。wrapper 恒为 rich struct，`rich` 由声明形式隐含，显式书写是编译错误。
- `shared`：class、rich struct、wrapper 可用，表示允许跨协程共享；`singleton` class 必须 shared。
- 二者**单向传染**：基类 rich/shared ⇒ 子类必须同标，反向可收紧（详见 SYNTAX §3.1.1）。
- 使用类型时（变量声明、函数参数）**永远不写** `rich`/`shared`。因此 `TypeReferenceParserLayer` 不处理它们；它们属于 class/struct 声明解析的职责。
- 这些规则的**检查**全部属于语义期 P2（见 `docs/compiler/semantic/SEMANTIC_ROADMAP.md` S3），Parser 只负责收下修饰符。

### 4.3 ⚠️ 泛型列表必须以 `\<` 开启（2026-07-17 语法修订）

泛型的声明与使用统一写作 `Name\<...>`（反斜杠 + 小于号开启，`>` 闭合）：

```latte
class Container\<TElement> { ... }      // 声明
var list: List\<i32>                     // 使用
var sorted = myList.sort\<i32>()         // 泛型调用
var map: List\<Map\<String, i32>>        // 嵌套闭合写 >>
```

- `<` 只属于比较运算符：`a < b` 与 `a\<b>` 词法层面零歧义。
- Lexer 不合并 `>` 系列；`>=`/`>>`/`>>>` 由 `ExpressionParserLayer` 在运算符状态下重组相邻 token。
- BIL 自身的 `.array<T>` 等语法不受影响（BIL 用 `cmp.lt` 等指令，无 `<` 歧义）。
- 详见 `docs/SYNTAX.md` §3.6。

### 4.4 Parser 架构：层栈 + 状态机 + 施工目标协议

Parser 主循环维护一个 Layer 栈，每个 token 交给栈顶 Layer 处理。核心协议在 `Parser/Parser.cs`：

- `IParserLayer.ParseToken(token, context)` 返回 `ParserLayerResult`：
  - `Continue`（单例）：本层继续消费，token 已被本层吃掉
  - `PushLayer(layer, TokenDisposition)`：压入子 Layer（委托）
  - `PopLayer(TokenDisposition)`：本层完成，弹栈
- `TokenDisposition.Consume`：当前 token 已被本层消费，前进到下一个 token；
  `TokenDisposition.Replay`：当前 token 原样交给新的栈顶 Layer 重新处理。
- **每个 Layer 内部用状态机驱动**（`private enum State` + switch），状态转换处要写注释。
- 模块化原则："Delegate, don't implement" —— 框架层（如 `ExpressionParserLayer`）负责识别、路由、运算符处理；具体语法结构委托给专门 Layer。每个 Layer 职责单一、可独立测试。

### 4.7 ⚠️ 大扫除后的 Parser 架构规则（2026-07-26 重构，必须遵守）

重构后的 Parser 分为**控制流系统**与 **AST 施工系统**，两者严格分离：

1. **Layer 不返回 AST**。Layer 之间只传递控制权，不传递任何 AST 数据；
   `PopLayer` 只表示控制权归还。禁止任何形式的回传替代机制
   （回调、Context 字段、父层引用、全局临时字段、事件/委托等）。
2. **Layer 创建时必须获得施工目标**。构造函数接收明确、强类型的目标
   （具体施工节点，或 ExpressionRootASTNode/CodeBlockASTNode/RootASTNode 等附加目标），
   子层原地填充目标或向目标附加子节点；数据流严格单向（父→子）。
3. **Push/Pop 使用 TokenDisposition**（Consume/Replay），禁止布尔值；
   `Continue` 只表示"本层消费当前 token 并继续"，不支持 Replay。
4. **表达式位置统一使用 ExpressionRootASTNode** 作为稳定挂载点：
   一次性 `Attach`、禁止替换、禁止附加已有父节点的表达式；
   可选表达式用 null Root 表示，禁止"非 null 但为空的 Root"；
   `ASTNode.Parent` 只能设置一次。
5. **子 Layer 禁止修改施工目标之外的 AST**（父节点、兄弟节点、
   经 Context 获得的全局位置、其他 Layer 正在施工的节点）。
6. **EOF 是正式 Token**（`EndOfFileToken`）：由 Lexer 在输出 token 列表末尾
   追加（M25；Parser 对绕过 Lexer 的调用方保持追加兼容），只由
   RootParserLayer 消费；非 Root 层遇 EOF：结构完整 → Pop(Replay) 上交，
   不完整 → 抛 "Unexpected end of file"。禁止用换行伪装 EOF。
7. **新 Layer 必须有独立测试**（`TestRootParserLayer` 驱动，见 §5）。
8. **注释由 Parser 主循环统一跳过**（M25）：CommentToken 不参与语法，
   分发时直接跳过；各 Layer 不再自行处理注释。

解析成功后 `ASTIntegrityValidator` 自动验证 AST 不变量：遍历只走
`[ChildAstNode]` 标注的成员（`[AstCarrier]` 对象深入其公共字段），校验每个
子节点的 Parent 指向持有者，另含 Root 均已填充、节点无共享、Parent 链无环、
switch default 规则、**每节点 Span 合法（M28：非空、sourceName 非空、
End 不早于 Start）**、**类型审计（M28：装 ASTNode 的字段/自动属性必须带
[ChildAstNode]/[ParentAstNode] 标注）**；失败抛 `CompilerInternalException`（内部编译器错误，
与用户语法错误区分）。节点类型一律用 CLR 类型判断（无 ASTNodeType 枚举）。
「归属后知」的场景必须用创建时归属即定的结构承载
（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）
或延迟一次性 AttachTo（注解），**禁止任何形式的 Parent 重挂**。

**Span 施工（M28）**：每个 AST 节点都有源码范围 `ASTNode.Span`（`CharRange?`）。
约定：层目标节点由 Parser 主循环按 token 流计算 span，层弹出时经
`ISpanReceiver.ReceiveSpan` 回填（一律 `target.Span ??= span` 只填空）——
新 Layer 若有施工目标，应实现 `ISpanReceiver`；层内自建节点由所在层显式设置
（创建记 Start，完成经 `ParserLayerContext.GetPreviousLocation()` 封 End）；
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
**Span 统一为左闭右开 `[Start, End)`（M31 起）**：Start 指向首个字符，
End 指向最后一个字符的下一位置（token 与 AST 节点一致；EOF 为零宽范围）；
语句/声明的 span 不拖尾换行符到下一行（终态层不消费换行）。
Validator 与 AstJsonlSerializer 的 [ChildAstNode] 反射统一走
`AST/ASTVisitor.cs` 的 `AstStructureReflection`（M28），禁止再写第三份反射下钻。

**JSONL 往返（M31）**：`AstJsonlSerializer`（v2：carrier 记录化、字段名键控）
与 `AstJsonlDeserializer`（完整反序列化，产物强制过 Validator）构成往返；
消费方按字段名取值，不依赖字段顺序。

**allowBareReturn 传染（M33）**：lambda 是裸 return 边界（SYNTAX §5.1）——
`CodeBlockParserLayer` 构造标记 `allowBareReturn`（默认 true）为 false 时，
遇无 @标签 return 抛 ParserException。lambda 体一律下传 false；标记沿施工链
向所有嵌套代码块与表达式深处传染（If/Switch/Loop/TryCatch/Seq/
VariableDeclaration/ArgumentList/TypeOf/Expression 各层逐一传递）——
**新 Layer 若创建 CodeBlockParserLayer 或 ExpressionParserLayer，必须同样
接收并传递该标记**；if/switch 表达式分支体不是 lambda 边界，继承父上下文标记。

### 4.5 ⚠️ 简洁优先：新增代码前必须自问的三个问题

新增任何 AST 节点、Layer、状态或辅助方法之前，逐条回答：

1. **这个真的有必要存在吗？** 不服务当前需求的字段、状态、抽象一律不写。
2. **有没有更简洁更优雅的方法？** 能用现有状态机多一个分支解决的，不要新建一层。
3. **可不可以复用已有的轮子？** 先翻一遍 `Parser/` 下已有的 Layer，不要自己造轮子。

项目内已验证的复用范例：

| 特性 | 复用方式 | 没有做的事 |
|------|----------|-----------|
| `throw` / `yield` / `return` / `break` / `continue` | `CodeBlockParserLayer` 的内联子状态 | 各建一个 Layer |
| `await` | `ExpressionParserLayer.IsPrefixUnaryOperator` 加一个关键字 | 新建 AwaitParserLayer |
| `seq` 语句形态 + 表达式形态 | 共用同一套 `CodeBlockParserLayer` 基建 | 两套独立实现 |
| class/interface/struct/wrapper/enum 声明 | 扩展既有 `DeclarationParserLayer` 骨架 | 新建 ClassDeclarationParserLayer |

只有当职责确实独立、且需要被多个父层复用时，才新建 Layer。

### 4.6 Lexer 的特点

Lexer 只做简单字符识别，不理解语义。例如 `3.14` 会输出三个 token：`Word "3"`、`Notation "."`、`Word "14"` —— 由 `LiteralParserLayer` 的状态机组合成浮点字面量。不要在 Lexer 里加语义判断。

- 斜杠家族（`/`、`//`、`/*`）由专门的 `SlashLexerLayer` 分流（M25）；
- `EndOfFileToken` 由 `Lexer.Tokenize` 在输出末尾追加（M25）；输入结束时以
  虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException；
- 位置计量（M28 修复后）：`CharRange.sourceName` 是源名唯一来源
  （`CharPosition` 不携带）；`CharPosition.offset` 是 0 起始字符索引；
  行/列 1 起始，换行算当前行最后一列；token 头跳过空白字符；
  EOF 冲刷帧占一个末尾虚拟位置，保证冲刷 token 的 End 正确；
- **token 范围为左闭右开 [Start, End)**（M31 起）：End 是最后一个字符的下一位置；
- 块注释不吞字符、不吞换行（M31：按行分段，换行以 LineBreakToken 入流）；
  行尾归一只把 `\r\n`/`\r` 归一为 `\n`；
- 复合赋值（`+=`/`*=` 等）不合并 token（与 `>=` 同策略，Parser 遇 op+`=` 重组为
  CompoundAssignmentExpressionASTNode，M34）；字符字面量 `'` 已实现
  （M34：CharLexerLayer + CharToken + CharLiteralASTNode，转义复用 StringEscape）；
  多行字符串 `"""` 已实现（M32，SYNTAX §3.3：
  Swift 风格严格多行，QuoteLexerLayer 分流 `"`/`""`/`"""`，转义表 StringEscape 单源，
  插值标记词法期判定——`\$` 转义的字面 `$` 不构成插值引导）。

---

## 5. 测试策略

- **不使用任何测试框架**。测试是 `Tests/` 下的静态类，每个类提供 `public static int RunAll()`（返回失败用例数），由 `Tests/TestRunner.cs` 统一驱动（`test` 命令入口）。
- **全量入口**：`dotnet run -- test --all` 自动运行全部套件，任意失败返回非零退出码并列出失败套件名——这是 CI 与提交前验证的标准方式（CI 配置见 `.github/workflows/ci.yml`）。
- **统一基建（M31）**：`Tests/AstDescribe.cs` 是唯一的 AST 描述器（Expr/Stmt/Block/Decl/Root/Type/Symbol 等），`Tests/TestHarness.cs` 是唯一的驱动与断言（ParseRoot/ParseBlock/ParseWithLayer/ParseFirstDecl + Check/CheckTrue/CheckParseError/Summary）。禁止在套件里再写私有 Describe*/Format* 副本与计数样板。
- **断言对象约定（M31）**：除查的就是命令行/日志/token 流/层协议行为的套件（Logger、CommandLineParser、LexerFuzz、TokenDisposition）外，一律断言 AST 树产物（AstDescribe 描述串 + 结构断言），不断言控制台输出文本。
- **AST 结构断言**：表达式类测试除描述串快照外，还应断言结构性事实（Root 是否存在/已填充、Expression 的具体类型、Parent 链、子 Root 填充、无节点共享）——快照不能作为唯一验证方式。
- **独立 Layer 测试**：经 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)` 驱动（`TestHarness.ParseWithLayer` 封装）。`TestRootParserLayer` 只接受 EOF——被测 Layer 提前结束或漏消费普通 token 会立即失败，能发现 Layer 边界问题。
- **约定：每新增一个 ParserLayer，必须在 `Tests/` 添加对应测试类，并在 `TestRunner` 注册表注册（`test` 菜单与 `test --run N` 的编号即注册表顺序）。**
- 测试数量与通过状态等易变数字只记录在 `docs/PROGRESS_REPORT.md`，本文件不保存。

验证改动（已验证可用）：

```bash
dotnet build
dotnet run -- test --all    # 全量；或：dotnet run -- test --run 5（单个套件）
```

---

## 6. 代码规范与开发约定

- **命名**：标准 C# 约定（类/方法 PascalCase，局部变量与私有字段 camelCase）。
- **缩进**：4 空格。
- **注释语言**：中文。关键逻辑必须注释；状态机的状态含义与转换必须说明。
- **文档语言**：中文。`docs/` 下的规范文档是权威来源——**先读 SYNTAX.md 再写代码，不要凭其他语言的经验猜语法**（项目已因此返工过）。
- **思考语言**：为节省 token，思考一律使用中文；向子代理（subagent）下达任务时必须明确要求它也用中文思考。
- **禁止使用 AskUserQuestion**（harness 为 Kimi Code 时）：该工具有显示 bug，用户看不到第一个问题之后的后续问题。需要用户决策时，把问题整理好在回复正文中一次问完，然后停下来等待回答。
- 新代码应模仿相邻文件的风格；项目无 linter/格式化工具配置。
- 命名空间：主代码 `LatteCompiler`，测试 `LatteCompiler.Tests`。
- **日志**：Lexer/Parser 等编译器内部的日志一律走 `Core/Logger`（Verbose/Warning/Error），禁止直接 `Console.WriteLine`；verbose 默认关闭（`--verbose` 子命令打开控制台输出），`--log-to PATH` 把全量日志以 JSONL 落盘。控制台日志输出走 **stderr**（M31 起）——诊断不污染 stdout 的数据流（如 `compile --parse-only` 的 AST JSONL）。测试的报告输出（`[PASS]`/`[FAIL]` 等）不受此限。

### 添加新 Parser 功能的标准流程

1. 阅读 `docs/SYNTAX.md` 相关章节，理解规范与示例
2. 设计状态机（画出状态转换）
3. 在 `AST/` 对应文件中添加 AST 节点
4. 在 `Parser/` 新建 ParserLayer（实现 `IParserLayer`，构造函数接收明确施工目标，见 §4.7）
5. 在 `Tests/` 添加测试类，在 `TestRunner` 注册表注册
6. 在 `RootParserLayer`（或相应父层）接入委托入口
7. `dotnet build` + `dotnet run -- test --run N`（对应套件）验证

### 进度对齐标准（必须遵守）

- **`docs/PROGRESS_REPORT.md` 是项目进度的唯一权威来源**。不要新建单点完成报告/实现总结类文档。
- **更新时机**：每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须立即更新。
- **更新方式**：保持文档既有结构不变，并在「里程碑历史」**顶部**追加新段落（倒序）。
- **分工**：`PARSER_ROADMAP.md` 管「计划」，`PROGRESS_REPORT.md` 管「现状」。计划调整改 ROADMAP，进度推进改 PROGRESS_REPORT。

---

## 7. 注意事项与已知限制

- 项目已在 **Git 版本控制**下（`main` 分支）：执行 `git commit` 等变更操作前先获得用户确认；提交前确保 `dotnet build` 通过且 `dotnet run -- test --all` 无失败。
- Verbose 调试日志默认关闭，不再刷屏；需要时加 `--verbose` 子命令（控制台）或 `--log-to PATH`（全量 JSONL 落盘）。
- 无安全敏感面：本项目是本地控制台工具，不处理网络、凭据或用户隐私数据。唯一文件操作是 `Program.cs` 读取用户指定路径的 `.latte` 文件。

---

## 8. 项目原则

1. **文档驱动** —— 先理解 SYNTAX.md，再写代码
2. **测试驱动** —— 每个 ParserLayer 都有对应测试
3. **模块化** —— 每个 Layer 职责单一，委托而非大包大揽
4. **渐进式** —— 按各阶段 ROADMAP（`docs/compiler/syntax/PARSER_ROADMAP.md`、`docs/compiler/semantic/SEMANTIC_ROADMAP.md`）逐步推进，不跳步
5. **不要猜测** —— 不确定时查文档
6. **简洁优先** —— 写代码时始终自问：这个真的有必要存在吗？有没有更简洁更优雅的方法？可不可以复用已有的轮子（比如已有的 Layer）？不要自己造轮子（详见 §4.5）
