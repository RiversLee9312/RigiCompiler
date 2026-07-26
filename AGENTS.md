# LatteCompiler 项目指南（AGENTS.md）

> **用途**: 为 AI 编码代理提供 Latte 编译器项目的完整上下文。读者默认对本项目一无所知。
> 本文件与 `CLAUDE.md` 并存，内容以实际代码为准（已验证日期：2026-07-18）。

**项目名**: LatteCompiler
**语言**: C#（.NET 8.0，控制台程序，`Nullable` 与 `ImplicitUsings` 已启用）
**开发阶段**: 早期 —— 编译器前端（Lexer + Parser）已完成（roadmap P0–P5 全部落地），下一阶段：语义分析、BIL 输出
**版本控制**: Git（`main` 分支，2026-07-17 首次提交，工作树干净；无 CI/CD）

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
Latte 源码 (.latte) → Frontend (Lexer + Parser) ✅ 完成 → 语义分析 ← 当前阶段
                    → BIL (Basic Intermediate Language)
                    → Middleware (LLVM IR Generator)
                    → LLVM 工具链 → 原生可执行文件
```

**当前进度**：编译器前端（Lexer + Parser）已完成。已可解析字面量、类型引用、变量声明（含 getter/setter 属性访问器）、完整表达式（含 Lambda、if/switch 表达式、typeOf/as/is、seq 表达式形态、await、前导点 enum case 引用、wrapper 路径访问 `:`）、完整语句系统（代码块、if、循环、try-catch-finally、seq、throw、yield、return/break/continue、赋值）、泛型参数列表、函数形参列表（含 init `_ -> field` 参数映射）、统一声明层（全局字段/函数、class/interface/struct/wrapper 声明、成员方法与 init/operator、继承与 implements、like 委托、ext 限定名、嵌套类型、声明上的泛型参数、enum struct 的 `[]` case 列表）、wrapper 主体（`@` 注解/wrapper 应用、`@WrapperTarget(.X)` 类型标识、`.proxy.*` 代理成员）、模块系统（import §15.2 三种形态、namespace 声明 §15.1）。尚无语义分析、无代码生成、无 BIL 输出。

---

## 2. 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、5 个 nullable 警告（Core/Utilities.cs，不影响功能）
dotnet clean
```

唯一配置文件是 `LatteCompiler.csproj`（无 NuGet 第三方依赖，纯 BCL）。另有 `LatteCompiler.sln`。

### 2.2 运行

`Program.cs` 是交互式入口，启动后显示菜单：

```
1. Parse file              → 输入 .latte 文件路径，打印 token 列表和 AST
2. Run Literal tests
3. Run TypeReference tests
4. Run VariableDeclaration tests
5. Run Expression tests
6. Run Generic parsing tests
7. Run GenericParameters tests
8. Run ParameterList tests
9. Run Lambda expression tests
10. Run if expression tests
11. Run switch expression tests
12. Run typeOf expression tests
13. Run CodeBlock tests
14. Run Loop tests
15. Run TryCatchFinally tests
16. Run SeqBlock tests
17. Run ThrowStatement tests
18. Run CoroutineOps tests
19. Run TypeDeclaration tests
20. Run PropertyAccessor tests
21. Run Import tests
22. Run Namespace tests
```

非交互运行示例：

```bash
echo "2" | dotnet run        # 运行字面量测试
echo "8" | dotnet run        # 运行形参列表测试
```

---

## 3. 代码库结构

```
LatteCompiler/
├── Program.cs                # 入口：交互菜单 + 文件解析流程
├── LatteCompiler.csproj      # net8.0，Exe，Nullable enable
├── AST/                      # AST 节点定义（按类别分文件）
│   ├── LiteralNodes.cs          # 字面量节点（Int/Float/String/Bool/Null 等）
│   ├── TypeNodes.cs             # 类型引用节点
│   ├── DeclarationNodes.cs      # 声明节点（变量声明等）
│   ├── ExpressionNodes.cs       # 表达式节点
│   └── StatementNodes.cs        # 语句节点（代码块/if/循环/return/赋值等）
├── Parser/                   # Parser 层实现（每层一个文件）
│   ├── Parser.cs                # 核心接口：IParserLayer、ParserLayerResult、
│   │                            #   IResultProducer / IResultConsumer、ParserLayerContext
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
│   ├── LambdaExpressionParserLayer.cs   # Lambda 表达式（含 async、trailing）
│   ├── IfStatementParserLayer.cs        # if 表达式 + if 语句（else if 链）
│   ├── SwitchStatementParserLayer.cs    # switch 表达式（语句模式待规范明确）
│   ├── TypeOfExpressionParserLayer.cs   # typeOf 表达式
│   ├── CodeBlockParserLayer.cs          # 代码块：语句识别与分发（return/break/continue/throw/yield 内联）
│   ├── LoopParserLayer.cs               # 循环（for/while/do-while/named 标签）
│   ├── SeqBlockParserLayer.cs           # seq 块（volatile/using/named，语句+表达式双形态）
│   ├── TryCatchFinallyParserLayer.cs    # try/多 catch/finally(e)
│   ├── PropertyAccessorParserLayer.cs   # 属性访问器块 { get... set... }（§9.4）
│   └── DeclarationParserLayer.cs        # 统一声明层：全局/成员/嵌套任何声明（P3）
├── Lexer/                    # 词法分析
│   ├── Lexer.cs                 # Tokenize(TextReader/string) 入口
│   └── LexerLayers.cs
├── Core/                     # 基础设施
│   ├── Utilities.cs             # Token 定义、Keywords、Helper（打印工具）、
│   │                            #   以及部分未迁出的 AST 基类/节点（ASTNode、RootASTNode、
│   │                            #   SymbolASTNode、ImportASTNode 等）
│   └── FrontendTypesExtension.cs
├── Tests/                    # 自研控制台测试（非 xUnit/NUnit，见 §5）
└── docs/                     # 设计与规范文档（全部为权威参考）
```

### 3.1 关键文件

| 文件 | 用途 | 重要性 |
|------|------|--------|
| `docs/SYNTAX.md` | **语言语法规范（最权威）** | ⭐⭐⭐ 有歧义时以此为准，不要猜语法 |
| `docs/RUNTIME.md` | 运行时模型与类型系统 | ⭐⭐⭐ |
| `docs/BIL_STANDARD.md` | BIL 中间语言规范 | ⭐⭐ |
| `docs/compiler/frontend/PARSER_ROADMAP.md` / `docs/PROGRESS_REPORT.md` | Parser 路线图与进度 | ⭐⭐ |
| `docs/compiler/frontend/EXPRESSION_ARCHITECTURE.md` / `docs/RICH_SHARED_CLARIFICATION.md` | 专项设计澄清 | ⭐⭐ |
| `Parser/Parser.cs` | 层栈式 Parser 的核心协议 | ⭐⭐⭐ |
| `Core/Utilities.cs` | Token/Keywords/AST 基类等核心数据结构 | ⭐⭐⭐ |

---

## 4. 核心设计决策（改动代码前必须理解）

### 4.1 ⚠️ Latte 没有运算符优先级

```latte
var result = 1 + 2 * 3       // ❌ 编译错误：歧义
var result = 1 + (2 * 3)     // ✅ 必须加括号
```

对 Parser 的影响：不需要优先级表；遇到未括号化的连续运算符必须报错。实现表达式相关功能时不要引入优先级概念。

### 4.2 ⚠️ `rich` / `shared` 是类型**声明**修饰符，不是类型引用修饰符

- `rich`：**仅用于 struct / enum struct**（class 不能用）。允许值类型持有引用，但仍是值语义、unique ownership（类似 `unique_ptr`，**不是** `shared_ptr`）。
- `shared`：所有类型可用，表示允许跨协程共享。
- 使用类型时（变量声明、函数参数）**永远不写** `rich`/`shared`。因此 `TypeReferenceParserLayer` 不处理它们；它们属于 class/struct 声明解析的职责。

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

### 4.4 Parser 架构：层栈 + 状态机

Parser 主循环维护一个 Layer 栈，每个 token 交给栈顶 Layer 处理。核心协议在 `Parser/Parser.cs`：

- `IParserLayer.ParseToken(token, context)` 返回 `ParserLayerResult`：
  - `Continue`（单例）：本层继续消费
  - `PushLayer(layer, shouldKeepToken)`：压入子 Layer（委托）
  - `PopLayer(shouldKeepToken)`：本层完成，弹栈
- 结果传递：`IResultProducer.GetResult()` 由产生结果的 Layer 实现；父层实现 `IResultConsumer.OnChildResult(...)` 接收子层结果。
- **每个 Layer 内部用状态机驱动**（`private enum State` + switch），状态转换处要写注释。
- 模块化原则："Delegate, don't implement" —— 框架层（如 `ExpressionParserLayer`）负责识别、路由、运算符处理；具体语法结构委托给专门 Layer。每个 Layer 职责单一、可独立测试。

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

---

## 5. 测试策略

- **不使用任何测试框架**。测试是 `Tests/` 下的静态类，每个类提供 `public static void RunAll()`，通过 `Program.cs` 菜单（选项 2–22）触发。
- 测试模式：每个用例把一小段 Latte 源码字符串依次过 `Lexer.Tokenize` → `Parser.Parse`，然后把得到的 AST 节点描述成字符串与期望比对，控制台打印 `[PASS]`/`[FAIL]`，结尾汇总 `N passed, M failed`。
- **约定：每新增一个 ParserLayer，必须在 `Tests/` 添加对应测试类，并在 `Program.cs` 菜单注册一个新选项。**
- 当前测试类（21 个）：`LiteralParserTests`、`TypeReferenceParserTests`、`VariableDeclarationTests`、`ExpressionParserTests`、`GenericParsingTests`、`GenericParametersTests`、`ParameterListTests`、`LambdaExpressionTests`、`IfExpressionTests`、`SwitchExpressionTests`、`TypeOfExpressionTests`、`CodeBlockTests`、`LoopTests`、`TryCatchFinallyTests`、`SeqBlockTests`、`ThrowStatementTests`、`CoroutineOpsTests`、`TypeDeclarationTests`、`PropertyAccessorTests`、`ImportTests`、`NamespaceTests`，合计 417 个用例，当前全部通过。

验证改动（已验证可用）：

```bash
dotnet build
echo "5" | dotnet run --no-build    # 按需替换菜单编号
```

---

## 6. 代码规范与开发约定

- **命名**：标准 C# 约定（类/方法 PascalCase，局部变量与私有字段 camelCase）。
- **缩进**：4 空格。
- **注释语言**：中文。关键逻辑必须注释；状态机的状态含义与转换必须说明。
- **文档语言**：中文。`docs/` 下的规范文档是权威来源——**先读 SYNTAX.md 再写代码，不要凭其他语言的经验猜语法**（项目已因此返工过）。
- 新代码应模仿相邻文件的风格；项目无 linter/格式化工具配置。
- 命名空间：主代码 `LatteCompiler`，测试 `LatteCompiler.Tests`。

### 添加新 Parser 功能的标准流程

1. 阅读 `docs/SYNTAX.md` 相关章节，理解规范与示例
2. 设计状态机（画出状态转换）
3. 在 `AST/` 对应文件中添加 AST 节点
4. 在 `Parser/` 新建 ParserLayer（实现 `IParserLayer`，必要时实现 `IResultProducer`/`IResultConsumer`）
5. 在 `Tests/` 添加测试类，在 `Program.cs` 菜单注册
6. 在 `RootParserLayer`（或相应父层）接入委托入口
7. `dotnet build` + 运行对应测试菜单项验证

### 进度对齐标准（必须遵守）

- **`docs/PROGRESS_REPORT.md` 是项目进度的唯一权威来源**。不要新建单点完成报告/实现总结类文档。
- **更新时机**：每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须立即更新。
- **更新方式**：保持文档既有结构不变，并在「里程碑历史」**顶部**追加新段落（倒序）。
- **分工**：`PARSER_ROADMAP.md` 管「计划」，`PROGRESS_REPORT.md` 管「现状」。计划调整改 ROADMAP，进度推进改 PROGRESS_REPORT。

---

## 7. 注意事项与已知限制

- 项目已在 **Git 版本控制**下（`main` 分支）：执行 `git commit` 等变更操作前先获得用户确认；提交前确保 `dotnet build` 通过且全部测试套件无 FAIL。
- `Core/Utilities.cs` 里仍残留部分 AST 节点定义（`RootASTNode`、`SymbolASTNode`、`ImportASTNode`、`AcquisitionExpressionASTNode` 等），新增节点优先放到 `AST/` 目录对应文件。
- 字符字面量（char literal）未实现，仅有占位。
- 输出含 VERBOSE 调试日志属正常现象。
- 无安全敏感面：本项目是本地控制台工具，不处理网络、凭据或用户隐私数据。唯一文件操作是 `Program.cs` 读取用户指定路径的 `.latte` 文件。

---

## 8. 项目原则

1. **文档驱动** —— 先理解 SYNTAX.md，再写代码
2. **测试驱动** —— 每个 ParserLayer 都有对应测试
3. **模块化** —— 每个 Layer 职责单一，委托而非大包大揽
4. **渐进式** —— 按 `docs/compiler/frontend/PARSER_ROADMAP.md` 逐步推进，不跳步
5. **不要猜测** —— 不确定时查文档
6. **简洁优先** —— 写代码时始终自问：这个真的有必要存在吗？有没有更简洁更优雅的方法？可不可以复用已有的轮子（比如已有的 Layer）？不要自己造轮子（详见 §4.5）
