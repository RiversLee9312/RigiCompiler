# LatteCompiler 项目指南（AGENTS.md）

> **用途**: 为 AI 编码代理提供 Latte 编译器项目的完整上下文。读者默认对本项目一无所知。
> 本文件与 `CLAUDE.md` 并存，内容以实际代码为准（已验证日期：2026-07-26）。

**项目名**: LatteCompiler
**语言**: C#（.NET 8.0，控制台程序，`Nullable` 与 `ImplicitUsings` 已启用）
**开发阶段**: 早期 —— 编译器前端（Lexer + Parser）已完成（roadmap P0–P5 全部落地），Parser/PDA 大扫除（架构重构）已完成；下一阶段：语义分析、BIL 输出
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

**当前进度**：编译器前端（Lexer + Parser）已完成，且经过一次彻底的架构大扫除（见 §4.7）：控制流系统与 AST 施工系统严格分离，Layer 之间只传递控制权不传递 AST 数据。已可解析字面量、类型引用、变量声明（含 getter/setter 属性访问器）、完整表达式（含 Lambda、if/switch 表达式、typeOf/as/is、seq 表达式形态、await、前导点 enum case 引用、wrapper 路径访问 `:`）、完整语句系统（代码块、if、循环、try-catch-finally、seq、throw、yield、return/break/continue、赋值）、泛型参数列表、函数形参列表（含 init `_ -> field` 参数映射）、统一声明层（全局字段/函数、class/interface/struct/wrapper 声明、成员方法与 init/operator、继承与 implements、like 委托、ext 限定名、嵌套类型、声明上的泛型参数、enum struct 的 `[]` case 列表）、wrapper 主体（`@` 注解/wrapper 应用、`@WrapperTarget(.X)` 类型标识、`.proxy.*` 代理成员）、模块系统（import §15.2 三种形态、namespace 声明 §15.1）。尚无语义分析、无代码生成、无 BIL 输出。

---

## 2. 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、0 警告
dotnet clean
```

唯一配置文件是 `LatteCompiler.csproj`（无 NuGet 第三方依赖，纯 BCL）。另有 `LatteCompiler.sln`。

### 2.2 运行

`Program.cs` 是交互式入口，启动后显示菜单（选项 1 为解析文件，2–23 为各测试套件，最后一个选项为 TokenDisposition 协议测试）。

非交互运行示例：

```bash
echo "2" | dotnet run        # 运行字面量测试
echo "8" | dotnet run        # 运行形参列表测试
```

**单命令全量测试（CI 入口，推荐）**：

```bash
dotnet run -- --test-all    # 自动运行全部套件；任意失败返回非零退出码并列出失败套件名
```

---

## 3. 代码库结构

```
LatteCompiler/
├── Program.cs                # 入口：交互菜单 + 文件解析流程 + --test-all
├── LatteCompiler.csproj      # net8.0，Exe，Nullable enable
├── AST/                      # AST 节点定义（按类别分文件）
│   ├── LiteralNodes.cs          # 字面量节点（LiteralASTNode 基类 + Int/Float/String/Bool/Null 等）
│   ├── TypeNodes.cs             # 类型引用节点
│   ├── DeclarationNodes.cs      # 声明节点（变量声明等）
│   ├── ExpressionNodes.cs       # 表达式节点（含 ExpressionRootASTNode 挂载点）
│   ├── StatementNodes.cs        # 语句节点（代码块/if/循环/return/赋值等）
│   └── ASTIntegrityValidator.cs # AST 完整性验证器（Parse 成功后自动运行）
├── Parser/                   # Parser 层实现（每层一个文件）
│   ├── Parser.cs                # 核心协议：IParserLayer、ParserLayerResult、
│   │                            #   TokenDisposition、ParserLayerContext、Parser 主循环
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
│   ├── LoopParserLayer.cs               # 循环（for/while/do-while/named 标签）
│   ├── SeqBlockParserLayer.cs           # seq 块（volatile/using/named，语句+表达式双形态）
│   ├── TryCatchFinallyParserLayer.cs    # try/多 catch/finally(e)
│   ├── PropertyAccessorParserLayer.cs   # 属性访问器块 { get... set... }（§9.4）
│   ├── NamespaceParserLayer.cs          # namespace 声明（§15.1）
│   └── DeclarationParserLayer.cs        # 统一声明层：全局/成员/嵌套任何声明（P3）
├── Lexer/                    # 词法分析
│   ├── Lexer.cs                 # Tokenize(TextReader/string) 入口
│   └── LexerLayers.cs
├── Core/                     # 基础设施
│   ├── Utilities.cs             # Token 定义（含 EndOfFileToken）、Keywords、Helper（打印工具）、
│   │                            #   以及部分未迁出的 AST 基类/节点（ASTNode、RootASTNode、
│   │                            #   SymbolASTNode、ImportASTNode 等）
│   └── FrontendTypesExtension.cs
├── Tests/                    # 自研控制台测试（非 xUnit/NUnit，见 §5）
│   ├── TestRunner.cs            # --test-all 全量入口（套件注册表 + 退出码）
│   ├── TestRootParserLayer.cs   # 独立 Layer 测试垫底层（只接受 EOF）
│   └── TokenDispositionTests.cs # Token 流转协议测试（四种组合）
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
6. **EOF 是正式 Token**（`EndOfFileToken`）：由 Parser 在输入本地副本末尾追加，
   只由 RootParserLayer 消费；非 Root 层遇 EOF：结构完整 → Pop(Replay) 上交，
   不完整 → 抛 "Unexpected end of file"。禁止用换行伪装 EOF。
7. **新 Layer 必须有独立测试**（`TestRootParserLayer` 驱动，见 §5）。

解析成功后 `ASTIntegrityValidator` 自动验证 AST 不变量（Root 均已填充、
Expression.Parent 指向 Root、节点无共享、Parent 链无环、switch default 规则），
失败抛 `CompilerInternalException`（内部编译器错误，与用户语法错误区分）。

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

- **不使用任何测试框架**。测试是 `Tests/` 下的静态类，每个类提供 `public static int RunAll()`（返回失败用例数），通过 `Program.cs` 菜单触发，或由 `Tests/TestRunner.cs` 统一驱动。
- **全量入口**：`dotnet run -- --test-all` 自动运行全部套件，任意失败返回非零退出码并列出失败套件名——这是 CI 与提交前验证的标准方式（CI 配置见 `.github/workflows/ci.yml`）。
- 测试模式：每个用例把一小段 Latte 源码字符串依次过 `Lexer.Tokenize` → `Parser.Parse`，然后把得到的 AST 节点描述成字符串与期望比对，控制台打印 `[PASS]`/`[FAIL]`，结尾汇总 `N passed, M failed`。
- **AST 结构断言**：表达式类测试除字符串快照外，还应断言结构性事实（Root 是否存在/已填充、Expression 的具体类型、Parent 链、子 Root 填充、无节点共享）——快照不能作为唯一验证方式。
- **独立 Layer 测试**：经 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)` 驱动。`TestRootParserLayer` 只接受 EOF——被测 Layer 提前结束或漏消费普通 token 会立即失败，能发现 Layer 边界问题。
- **约定：每新增一个 ParserLayer，必须在 `Tests/` 添加对应测试类，并在 `Program.cs` 菜单与 `TestRunner` 注册表各注册一处。**
- 测试数量与通过状态等易变数字只记录在 `docs/PROGRESS_REPORT.md`，本文件不保存。

验证改动（已验证可用）：

```bash
dotnet build
dotnet run -- --test-all    # 全量；或：echo "5" | dotnet run --no-build（单个套件）
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
4. 在 `Parser/` 新建 ParserLayer（实现 `IParserLayer`，构造函数接收明确施工目标，见 §4.7）
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

- 项目已在 **Git 版本控制**下（`main` 分支）：执行 `git commit` 等变更操作前先获得用户确认；提交前确保 `dotnet build` 通过且 `dotnet run -- --test-all` 无失败。
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
