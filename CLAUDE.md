# Latte 编译器项目指南

> **用途**: 为 Claude Code 和开发者提供 Latte 编译器项目的完整上下文

**项目名**: LatteCompiler  
**语言**: C# (.NET 8.0)  
**开发阶段**: 早期 - Parser 实现中（P0 完成，P1 大部分完成，已进入 P2 准备阶段）  
**版本控制**: Git（main 分支，2026-07-17 首次提交）  
**文档版本**: 2026-07-17

---

## 1. 项目概述

### 1.1 什么是 Latte

Latte 是一门现代的、类型安全的编程语言，设计目标：

- **完全具化的泛型**：运行时类型信息完全保留（reified），不擦除
- **协程为核心**：从 `main` 开始的原生协程支持
- **灵活的类型系统**：值类型/引用类型分离，`rich`/`shared` 修饰符
- **Wrapper 系统**：类似 Python 装饰器 + Java 注解的强大修饰器机制
- **无运算符优先级**：所有运算必须用括号明确指定，避免歧义

### 1.2 编译器架构

```
Latte Source Code (.latte)
    ↓
Frontend (Parser + Semantic Analyzer) ← 当前阶段
    ↓
BIL (Basic Intermediate Language)
    ↓
Middleware (LLVM IR Generator)
    ↓
LLVM Toolchain
    ↓
Native Executable
```

**当前进度**: P0 完成，P1 大部分完成（表达式后缀链、泛型参数、形参列表、结果传递机制、泛型 `\<` 语法迁移），下一步进入 P2 语句系统。

---

## 2. 关键设计决策与约束

### 2.1 核心语言特性（必须理解）

#### ⚠️ 无运算符优先级（极其重要！）

**这是 Latte 与其他语言最大的区别**

```latte
// ❌ 错误：歧义，编译失败
var result = 1 + 2 * 3

// ✅ 正确：必须用括号
var result = 1 + (2 * 3)    // = 7
var result = (1 + 2) * 3    // = 9
```

**影响**：
- Parser 设计大幅简化（无需优先级表）
- 表达式解析直接检查括号结构
- 遇到未括号化的多个运算符必须报错

#### ⚠️ 泛型列表必须以 `\<` 开启（2026-07-17 语法修订）

泛型的声明与使用统一写作 `Name\<...>`（反斜杠 + 小于号开启，`>` 闭合）：

```latte
class Container\<TElement> { ... }      // 声明
var list: List\<i32>                     // 类型引用
var sorted = myList.sort\<i32>()         // 泛型调用
var map: List\<Map\<String, i32>>        // 嵌套闭合写 >>
```

**关键点**：
- `<` 只属于比较运算符：`a < b` 是比较，`a\<b>` 是泛型，词法层面零歧义
- Lexer 不合并 `>` 系列（`>=`/`>>`/`>>>` 由 ExpressionParserLayer 在运算符状态下重组）
- BIL 自身的 `.array<T>` 等语法不受影响（BIL 无 `<` 运算符）
- 详见 `docs/SYNTAX.md` §3.6

#### ⚠️ rich 和 shared 的正确理解

**这两个修饰符经常被误解，必须牢记：**

| 修饰符 | 适用对象 | 用途 | 使用位置 |
|--------|---------|------|----------|
| `rich` | **仅 struct 和 enum struct**<br>❌ 不能用于 class | 允许值类型持有引用<br>**但仍是值语义**（类似 `unique_ptr`） | 类型**声明**时 |
| `shared` | 所有类型 | 允许跨协程共享<br>提供线程安全基础 | 类型**声明**时 |

```latte
// ✅ 正确：在类型声明时使用
rich struct Point3D {     // 允许持有引用的值类型
    x: f64
    y: f64
    data: SomeObject      // 可以持有 Object
}

shared class Logger {     // 可跨协程共享的引用类型
    buffer: String
}

// ❌ 错误：class 不能用 rich
rich class MyClass { }    // 编译错误！

// ✅ 正确：使用类型时不写修饰符
var point: Point3D = Point3D(1.0, 2.0)   // 不写 rich
var logger: Logger = new Logger()        // 不写 shared
```

**关键点**：
- `rich` ≠ 引用计数共享（不是 `shared_ptr`）
- `rich` = 值类型可以持有引用，但仍是**unique ownership**（类似 `unique_ptr`）
- `rich` 和 `shared` 是**类型声明修饰符**，不是类型引用修饰符
- 使用类型时（变量声明、函数参数等）永远不写 `rich`/`shared`

### 2.2 Parser 架构原则

#### 模块化与职责分离

**正确的模块化原则**："Delegate, don't implement"

```
✅ 正确：
ExpressionParserLayer (框架)
  ├─ 识别表达式类型
  ├─ 委托给专门的 Layer:
  │   ├── LiteralParserLayer (字面量)
  │   ├── PathParserLayer (符号/调用)
  │   └── TypeReferenceParserLayer (类型)
  └─ 处理运算符（框架职责）

❌ 错误：
ExpressionParserLayer 直接实现所有表达式的解析逻辑
```

**每个 ParserLayer 必须**：
- 职责单一
- 可独立测试
- 通过委托复用其他 Layer
- 使用状态机驱动

### 2.3 类型系统层级

```
Any
├── Object (引用类型)
│   ├── String
│   ├── Nullable\<T>
│   ├── Box\<T extends ValueType>  // 系统特权，无独立 TypeSheet
│   └── ... (所有 class)
└── ValueType (值类型)
    ├── Enum
    ├── i8, i16, i32, i64, u8, u16, u32, u64
    ├── float, double, bool, char
    ├── Type\<T>
    ├── Span\<T extends ValueType>  // 连续无装箱缓冲区
    └── ... (所有 struct)
```

**关键区别**：
- ValueType：按值传递，栈分配（或 Box）
- Object：按引用传递，堆分配
- Box：系统特权，让 ValueType 进入 Object 多态而不失去值语义

---

## 3. 代码库结构

```
LatteCompiler/
├── AST/                  # AST 节点定义
│   ├── LiteralNodes.cs      # 6 种字面量节点
│   ├── TypeNodes.cs         # 类型引用节点
│   ├── DeclarationNodes.cs  # 声明节点（变量/泛型参数/形参）
│   └── ExpressionNodes.cs   # 表达式节点（含调用/索引/成员/实参）
├── Parser/               # Parser 层实现
│   ├── Parser.cs              # 核心协议 + IResultProducer/IResultConsumer
│   ├── RootParserLayer.cs     # 顶层入口分发
│   ├── LiteralParserLayer.cs
│   ├── TypeReferenceParserLayer.cs
│   ├── VariableDeclarationParserLayer.cs
│   ├── ExpressionParserLayer.cs   # 表达式框架（运算符 + 后缀链）
│   ├── PathParserLayer.cs         # 符号路径（含 \< 泛型实参）
│   ├── ArgumentListParserLayer.cs # 调用/索引/构造实参列表
│   ├── GenericParametersParserLayer.cs # 泛型参数列表 \<...>
│   ├── ParameterListParserLayer.cs     # 函数形参列表 (...)
│   ├── DeclarationParserLayer.cs / ImportParserLayer.cs
│   └── CodeBlockParserLayer.cs    # 骨架
├── Lexer/                # 词法分析
│   ├── Lexer.cs
│   └── LexerLayers.cs
├── Core/                 # 基础设施
│   ├── Utilities.cs         # Token、Keywords、Helper、部分 AST 基类
│   └── FrontendTypesExtension.cs
├── Tests/                # 测试（自研控制台模式，非测试框架）
│   ├── LiteralParserTests.cs
│   ├── TypeReferenceParserTests.cs
│   ├── VariableDeclarationTests.cs
│   ├── ExpressionParserTests.cs
│   ├── GenericParsingTests.cs
│   ├── GenericParametersTests.cs
│   └── ParameterListTests.cs
├── docs/                 # 文档
│   ├── SYNTAX.md            # **语言语法规范**（权威）
│   ├── RUNTIME.md           # 运行时模型
│   ├── BIL_STANDARD.md      # BIL 中间语言规范
│   ├── PROGRESS_REPORT.md   # 进度报告（**进度唯一权威**）
│   ├── RICH_SHARED_CLARIFICATION.md  # rich/shared 澄清
│   └── compiler/frontend/   # 编译器前端实现文档
│       ├── PARSER_ROADMAP.md       # Parser 实现路线图（计划）
│       ├── EXPRESSION_ARCHITECTURE.md  # 表达式架构设计
│       ├── FRONTEND_ARCHITECTURE.md    # 前端架构
│       └── FRONTEND_TYPES.md           # 前端数据类型
├── CLAUDE.md / AGENTS.md # AI 代理项目指南
└── Program.cs            # 程序入口
```

### 3.1 关键文件说明

| 文件 | 用途 | 重要性 |
|------|------|--------|
| `docs/SYNTAX.md` | **语言语法规范** | ⭐⭐⭐ 最权威，有歧义时以此为准 |
| `docs/RUNTIME.md` | 运行时模型与实现细节 | ⭐⭐⭐ 理解类型系统必读 |
| `docs/compiler/frontend/PARSER_ROADMAP.md` | Parser 实现计划 | ⭐⭐ 了解开发进度 |
| `Core/Utilities.cs` | 所有 AST 节点和 Token 定义 | ⭐⭐⭐ 核心数据结构 |
| `Parser/RootParserLayer.cs` | Parser 入口 | ⭐⭐ 理解解析流程 |

---

## 4. 开发工作流

### 4.1 添加新的 Parser 功能

**标准流程**：

1. **阅读 SYNTAX.md** - 理解语法规范
2. **设计状态机** - 画出状态转换图
3. **创建 AST 节点** - 在 `AST/` 目录
4. **实现 ParserLayer** - 在 `Parser/` 目录
5. **编写测试** - 在 `Tests/` 目录
6. **集成到 RootParserLayer** - 添加入口
7. **编译验证** - `dotnet build`
8. **运行测试** - 验证功能

**示例**（添加 if 语句解析）：

```csharp
// 1. AST 节点 (AST/StatementNodes.cs)
public class IfStatementASTNode : ASTNode
{
    public ExpressionASTNode Condition;
    public ASTNode ThenBlock;
    public ASTNode? ElseBlock;
    // ...
}

// 2. Parser Layer (Parser/IfStatementParserLayer.cs)
public class IfStatementParserLayer : IParserLayer
{
    private enum State
    {
        Initial,          // 等待 if 关键字
        ConditionStart,   // 等待 (
        ConditionParsing, // 解析条件表达式
        ThenBlock,        // 解析 then 块
        ElseCheck,        // 检查是否有 else
        Completed
    }
    // ... 状态机实现
}

// 3. 测试 (Tests/IfStatementTests.cs)
public class IfStatementTests
{
    public static void TestBasicIf() { ... }
    public static void TestIfElse() { ... }
}
```

### 4.2 编译和测试

```bash
# 编译
cd C:\Users\SaRiv\source\repos\LatteCompiler\LatteCompiler
dotnet build

# 运行测试（bin\Debug\net8.0 目录下）
echo "2" | .\LatteCompiler.exe  # 字面量测试（15）
echo "3" | .\LatteCompiler.exe  # 类型引用测试（3）
echo "4" | .\LatteCompiler.exe  # 变量声明测试（10）
echo "5" | .\LatteCompiler.exe  # 表达式测试（54）
echo "6" | .\LatteCompiler.exe  # 泛型解析测试（18）
echo "7" | .\LatteCompiler.exe  # 泛型参数列表测试（21）
echo "8" | .\LatteCompiler.exe  # 函数形参列表测试（14）
```

### 4.3 Git 工作流

项目已在 Git 版本控制下（`main` 分支，2026-07-17 首次提交）。约定：

- 完成阶段性功能后提交，保持小步提交
- 提交前确保 `dotnet build` 通过且全部测试套件无 FAIL
- `git commit` 等变更操作需用户确认后执行

### 4.4 进度对齐标准（必须遵守）

- **`docs/PROGRESS_REPORT.md` 是项目进度的唯一权威来源**。不要新建单点完成报告/实现总结类文档（防止碎片化）。
- **更新时机**：每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须立即更新。
- **更新方式**：保持文档既有结构不变，同步刷新各节内容，并在「里程碑历史」**顶部**追加新段落（倒序）。
- **分工**：`PARSER_ROADMAP.md` 管「计划」（要做什么、怎么做），`PROGRESS_REPORT.md` 管「现状」（做到了什么）。计划调整改 ROADMAP，进度推进改 PROGRESS_REPORT。

---

## 5. 常见问题与答案（Q&A）

### Q1: 为什么 Latte 没有运算符优先级？

**A**: 这是刻意的设计决策，目的是：
- 消除优先级相关的 bug（如 `a & b == c` 的陷阱）
- 代码意图完全显式，提高可读性
- Parser 实现更简单
- 可以用 `seq` 块拆分复杂表达式，命名中间变量

```latte
// 与其写一堆括号：
var result = ((a + b) * (c - d)) / ((e + f) - g)

// 不如用 seq 拆分：
var result = seq {
    var sum1 = a + b
    var diff = c - d
    var product = sum1 * diff
    var sum2 = e + f
    var denominator = sum2 - g
    return@seq product / denominator
}
```

### Q2: rich 到底是什么？为什么这么容易理解错？

**A**: `rich` 常被误解为"引用计数共享"（类似 C++ `shared_ptr`），但实际是：

**正确理解**：
- `rich` 允许**值类型**持有引用（Object）
- 但它**仍然是值类型**，仍然是 unique ownership
- 类似 C++ 的 `unique_ptr<T>`，不是 `shared_ptr<T>`

```cpp
// C++ 类比
struct NonRichPoint {
    double x, y;
    // ❌ 不能有 std::string，不能有指针
};

struct RichPoint {
    double x, y;
    std::unique_ptr<std::string> label;  // ✅ 可以持有引用
    // 复制时 label 会移动（move），不是共享
};
```

```latte
// Latte 对应
struct Point {        // 普通 struct
    x: f64
    y: f64
    // ❌ 不能有 Object 字段
}

rich struct RichPoint {  // rich struct
    x: f64
    y: f64
    label: String        // ✅ 可以持有 Object
    // 复制时 label 是 unique 的，不是共享引用
}
```

**为什么容易错？**
- "rich" 这个词暗示"更丰富"，容易联想到引用计数
- 文档中提到"引用"，容易误解为共享引用
- 需要记住：**值语义 + 可持有引用 ≠ 引用计数共享**

### Q3: 什么时候用 `Array\<T>`，什么时候用 `Span\<T>`？

**A**:

| 场景 | 使用 | 原因 |
|------|------|------|
| 泛型容器、需要动态大小 | `Array\<T>` | 标准泛型容器 |
| 高性能数值计算、缓冲区 | `Span\<T>` | 无装箱、连续内存 |
| 跨函数传递可变数据 | `Array\<T>` | 引用语义 |
| FFI、大量值类型数据 | `Span\<T>` | 零开销抽象 |

```latte
// Array\<T>：通用泛型容器
var names: Array\<String> = ["Alice", "Bob"]
names.append("Charlie")

// Span\<T>：高性能同构数据
var buffer: Span\<f32> = Span.alloc\<f32>(1000)
for (i in 0 to 1000) {
    buffer[i] = sin(i * 0.01)  // 零开销访问
}
```

### Q4: Parser 设计时，什么应该委托，什么应该直接实现？

**A**:

**应该委托给专门 Layer**：
- 具体的语法结构（字面量、类型引用、new 表达式等）
- 已经有对应 Layer 的功能（PathParserLayer 处理符号）
- 可复用的解析逻辑

**应该直接实现**：
- 框架性的工作（识别、路由、组合）
- 运算符处理（属于表达式框架的核心职责）
- 状态转换逻辑

```csharp
// ✅ 好的设计
public class ExpressionParserLayer
{
    private ParserLayerResult HandleInitial(...)
    {
        if (IsLiteralToken(token))
            return DelegateLiteralParsing(...);  // 委托

        if (IsBinaryOperator(token))
            return HandleBinaryOperator(...);    // 直接处理

        if (token is WordToken)
            return DelegateSymbolParsing(...);   // 委托
    }
}

// ❌ 坏的设计
public class ExpressionParserLayer
{
    // 直接实现所有表达式类型的解析逻辑
    // 违反单一职责原则，难以维护
}
```

### Q5: 为什么 TypeReferenceParserLayer 不处理 rich/shared？

**A**: 因为 `rich` 和 `shared` 是**类型声明修饰符**，不是**类型引用修饰符**。

**职责分配**：
- `TypeReferenceParserLayer`：解析类型**使用**（变量声明、函数参数等）
- `ClassDeclarationParserLayer`：解析类型**定义**（包括 shared class）
- `StructDeclarationParserLayer`：解析类型**定义**（包括 rich struct）

```latte
// TypeReferenceParserLayer 处理这些：
var x: i32 = 42
var name: String? = null
var list: List\<String>
func process(data: MyStruct) { }

// ClassDeclarationParserLayer 处理这个：
shared class Logger { }

// StructDeclarationParserLayer 处理这些：
rich struct Point3D { }
shared rich struct SharedData { }
```

### Q6: 字面量解析时遇到 `3.14`，Lexer 会输出什么？

**A**: Lexer 会输出**三个 token**：`[Word "3"]`, `[Notation "."]`, `[Word "14"]`

**为什么？**
- Lexer 的职责是简单的字符识别，不理解复杂语义
- `.` 是 notation，Lexer 不知道它是浮点数还是成员访问
- Parser 使用状态机识别这是浮点数，组合成 `FloatLiteralASTNode`

```csharp
// LiteralParserLayer 的浮点数状态机
enum FloatParseState
{
    Initial,       // 等待整数部分
    IntegerPart,   // 已有整数部分
    DotSeen,       // 看到 .
    Complete       // 完成
}

// 处理流程：
// Token "3"    -> IntegerPart
// Token "."    -> DotSeen
// Token "14"   -> 组合成 3.14, Complete
```

### Q7: 实现新功能前应该先做什么？

**A**: **阅读 SYNTAX.md！**

永远不要猜测语法。正确的流程：

1. ✅ **阅读 SYNTAX.md** 相关章节
2. ✅ 理解语法规范和示例
3. ✅ 查看相关的 BIL/RUNTIME.md 了解语义
4. ✅ 设计状态机和 AST 节点
5. ✅ 实现并测试
6. ❌ ~~基于对其他语言的理解猜测~~
7. ❌ ~~凭直觉实现~~

**经验教训**：
- 本项目已经因为猜测 rich/shared 的用途多次返工
- 文档是权威来源，代码应该符合文档，不是相反

---

## 6. 当前进度与下一步

### 6.1 已完成 ✅

| 组件 | 功能 | 测试 |
|------|------|------|
| LiteralParserLayer | 所有字面量类型 | 15/15 (100%) |
| TypeReferenceParserLayer | 类型引用（含 `\<` 泛型、嵌套、可空） | 3/3 (100%) |
| VariableDeclarationParserLayer | 变量声明（Initializer 经结果传递保存） | 10/10 (100%) |
| ExpressionParserLayer | 运算符、括号分组、调用/索引/成员/泛型调用后缀链、new 构造参数 | 54/54 (100%) |
| ArgumentListParserLayer | 位置/具名实参列表 | （含于表达式测试） |
| GenericParametersParserLayer | 泛型参数列表 `\<...>`（声明/约束/型变/可变） | 21/21 (100%) |
| ParameterListParserLayer | 函数形参列表（普通/默认/可变/具名可变） | 14/14 (100%) |
| 结果传递机制 | IResultProducer/IResultConsumer + 弹层自动传递 | （含于各套件） |
| 泛型语法迁移 | `\<...>` 语法 + `<` 解放为小于号 | 18/18 (100%) |

**总计**: 145/145 测试通过 (100%)

**可解析的语法**：
```latte
// 字面量
42, 3.14, "Hello", true, null

// 类型引用
i32, String?, List\<T>, Map\<K,V>, List\<Map\<String, i32>>?

// 变量声明（含完整初始化表达式）
var x = 42
const name: String = "Hello"
var v = foo(1, name = 2)
var v = foo().bar[0]
var v = new User(id = 42)
var v = a.b\<i32>(x)
var r = 1 + (2 * 3)

// 泛型参数列表（独立组件，待接入类型/函数声明）
\<TElement>, \<out T, in U>, \<named TValues... with Serializable>

// 函数形参列表（独立组件，待接入函数声明）
(a: i32, b: String = "x", rest: named i32...)
```

### 6.2 下一步 ⏳

**P1 收尾**（表达式层剩余）：
- Lambda 表达式、if/switch 表达式、typeOf/as/is

**P2 语句系统**（下一主战场）：
1. CodeBlockParserLayer 完善（语句识别与分发）
2. if/else、while、for、return 语句
3. 赋值语句

**后续**: P3 类型声明（复用 GenericParametersParserLayer）、P4 函数声明（复用 ParameterListParserLayer）

---

## 7. 技术债务与注意事项

### 7.1 已知限制

1. **字符字面量未实现** - 有占位符
2. **wrapper 路径访问（`:`）未实现** - 留待 P5 Wrapper 阶段
3. **typeOf/as/is、Lambda、if/switch 表达式未实现** - P1 收尾项
4. **泛型参数/形参列表是独立组件** - 待 P3/P4 类型与函数声明接入

### 7.2 编译警告

- 4 个 nullable 相关警告（不影响功能）
- 位于 `Core/Utilities.cs`

### 7.3 代码规范

- **命名**: 遵循 C# 约定（PascalCase 类名，camelCase 字段）
- **缩进**: 4 空格
- **注释**: 关键逻辑必须注释，状态机转换必须说明
- **测试**: 每个 ParserLayer 必须有对应测试

---

## 8. 参考资源

### 8.1 必读文档

1. [docs/SYNTAX.md](docs/SYNTAX.md) - **最权威的语法规范**
2. [docs/RUNTIME.md](docs/RUNTIME.md) - 运行时模型和类型系统
3. [docs/compiler/frontend/PARSER_ROADMAP.md](docs/compiler/frontend/PARSER_ROADMAP.md) - Parser 实现计划
4. [docs/compiler/frontend/EXPRESSION_ARCHITECTURE.md](docs/compiler/frontend/EXPRESSION_ARCHITECTURE.md) - 表达式架构设计
5. [docs/RICH_SHARED_CLARIFICATION.md](docs/RICH_SHARED_CLARIFICATION.md) - rich/shared 澄清

### 8.2 外部资源

- [Claude Code 官方文档](https://claude.com/blog/using-claude-md-files)
- [CLAUDE.md 最佳实践](https://www.datacamp.com/tutorial/writing-the-best-claude-md)
- .NET 8.0 文档

---

## 9. 项目原则

1. **文档驱动** - 先理解 SYNTAX.md，再写代码
2. **测试驱动** - 每个功能都有测试
3. **模块化** - 每个 ParserLayer 职责单一
4. **渐进式** - 按 Roadmap 逐步实现，不跳步
5. **质量优先** - 宁可慢一点，不要留技术债
6. **不要猜测** - 不确定时查文档，不要凭直觉

---

## 10. 快速命令参考

```bash
# 编译项目
dotnet build

# 运行字面量测试
echo "2" | ./LatteCompiler.exe

# 运行类型引用测试
echo "3" | ./LatteCompiler.exe

# 运行变量声明测试
echo "4" | ./LatteCompiler.exe

# 清理构建
dotnet clean
```

---

**最后更新**: 2026-07-17  
**维护者**: Claude Code AI Assistant  
**项目状态**: 活跃开发中
