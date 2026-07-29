# Latte 编译器项目指南

> **用途**: 为 Claude Code 和开发者提供 Latte 编译器项目的完整上下文。
>
> **文档分工（避免漂移，必须遵守）**：
> - **稳定架构规则与开发约定** → `AGENTS.md`（本文件引用它，不复制）
> - **进度、测试数量、当前阶段** → `docs/PROGRESS_REPORT.md`（唯一权威）
> - **语言语法** → `docs/SYNTAX.md`（最权威）
>
> 本文件只保存两类内容：① Latte 语言的核心设计决策（不易漂移）；
> ② 常见问题与答案（Q&A）。其余一律以引用为准。

**项目名**: LatteCompiler
**语言**: C# (.NET 8.0)
**版本控制**: Git（main 分支，2026-07-17 首次提交）

---

## 1. 项目概述

### 1.1 什么是 Latte

Latte 是一门现代的、类型安全的编程语言，设计目标：

- **完全具化的泛型**：运行时类型信息完全保留（reified），不擦除
- **协程为核心**：从 `main` 开始的原生协程支持
- **灵活的类型系统**：值类型/引用类型分离，`rich`/`shared` 修饰符
- **Wrapper 系统**：类似 Python 装饰器 + Java 注解的强大修饰器机制
- **无运算符优先级**：所有运算必须用括号明确指定，避免歧义

### 1.2 编译器架构与当前进度

```
Latte Source Code (.latte)
    ↓
Frontend (Lexer + Parser) ✅ 已完成（含 Parser/PDA 大扫除）
    ↓
Semantic Analyzer            ← 当前阶段
    ↓
BIL (Basic Intermediate Language)
    ↓
Middleware (LLVM IR Generator)
    ↓
LLVM Toolchain
    ↓
Native Executable
```

**当前进度、组件状态与测试数量**：见 `docs/PROGRESS_REPORT.md`（唯一权威来源）。

### 1.3 Parser 架构规范

Parser 的架构原则（层栈 + 状态机、TokenDisposition、施工目标协议、
ExpressionRootASTNode、EOF 正式化、Layer 拆分标准、简洁优先三问等）
**统一维护在 `AGENTS.md` §4**，本文件不复制——请以该节为准。

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
| `rich` | **仅 struct / enum struct / wrapper**<br>❌ 不能用于 class<br>（wrapper 恒 rich，隐含不写） | 允许值类型持有引用<br>**但仍是值语义**（类似 `unique_ptr`） | 类型**声明**时 |
| `shared` | class、rich struct、wrapper | 允许跨协程共享<br>提供线程安全基础 | 类型**声明**时 |

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

// ❌ 错误：wrapper 恒为 rich struct，不得显式书写
rich wrapper MyWrapper { }   // 编译错误（冗余修饰）

// ✅ 正确：使用类型时不写修饰符
var point: Point3D = Point3D(1.0, 2.0)   // 不写 rich
var logger: Logger = new Logger()        // 不写 shared
```

**关键点**：
- `rich` ≠ 引用计数共享（不是 `shared_ptr`）
- `rich` = 值类型可以持有引用，但仍是 **unique ownership**（类似 `unique_ptr`）
- `rich` 和 `shared` 是**类型声明修饰符**，不是类型引用修饰符
- 使用类型时（变量声明、函数参数等）永远不写 `rich`/`shared`
- 传染是**单向**的：基类 rich/shared ⇒ 子类必须同标，反向可收紧
- 非 rich struct 不得 `open`/`abstract`，因此没有子类型
- 逃逸闸门两处：全局/静态字段、async 边界（详见 SYNTAX §3.1.1 / §4.5）

### 2.2 类型系统层级

```
Any
├── Object (引用类型)
│   ├── Nullable\<T>               // shared 属性由 T 推导
│   ├── Box\<T extends ValueType>  // 系统特权，无独立 TypeSheet
│   └── ... (所有 class)
└── ValueType (值类型)
    ├── Enum
    ├── i8, i16, i32, i64, u8, u16, u32, u64
    ├── float, double, bool, char
    ├── String                     // 非 rich 值类型，值语义深拷贝
    ├── Type\<T>
    ├── Span\<T extends ValueType>  // 连续无装箱缓冲区
    ├── Wrapper                    // 所有 wrapper 的基类，恒 rich struct
    └── ... (所有 struct)
```

**关键区别**：
- ValueType：按值传递，栈分配（或 Box）
- Object：按引用传递，堆分配
- Box：系统特权，让 ValueType 进入 Object 多态而不失去值语义
- `String` 与 `Wrapper` 都在 ValueType 分支（2026-07-29 规范修订）——
  前者非 rich、可自由跨越 async 与全局存储边界，后者恒 rich、
  生命周期绑定被修饰实体

---

## 3. 代码库结构与开发工作流

代码库结构、构建与运行方式、测试策略、添加新 Parser 功能的标准流程、
代码规范——统一见 `AGENTS.md`（§2 构建与运行、§3 代码库结构、
§5 测试策略、§6 代码规范与开发约定）。

常用命令：

```bash
dotnet build                        # 编译
dotnet run -- test --all            # 全量测试（CI 入口；任意失败非零退出）
dotnet run -- test --run 5          # 单个测试套件（编号见 dotnet run -- test 菜单）
dotnet run -- compile --file a.latte --parse-only   # 只解析，AST JSONL 输出到 stdout
dotnet run -- help                  # 全部命令帮助（help compile.file 看单个子命令）
# 诊断子命令（compile/test 共有）：--verbose（控制台 verbose）、
#   --log-to run.jsonl（全量日志 JSONL 落盘）、--dump-ast ast.jsonl（AST 写文件）
```

Git 约定：项目已在 Git 版本控制下（`main` 分支）；完成阶段性功能后提交，
保持小步提交；提交前确保 `dotnet build` 通过且 `test --all` 无失败；
`git commit` 等变更操作需用户确认后执行。

进度对齐：每完成一个里程碑必须立即更新 `docs/PROGRESS_REPORT.md`
（在「里程碑历史」**顶部**追加新段落，倒序）。

---

## 4. 常见问题与答案（Q&A）

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
    return@_ product / denominator
}
```

### Q2: rich 到底是什么？为什么这么容易理解错？

**A**: `rich` 常被误解为"引用计数共享"（类似 C++ `shared_ptr`），但实际是：

**正确理解**：
- `rich` 允许**值类型**持有引用（Object）
- 但它**仍然是值类型**，仍然是 unique ownership
- 类似 C++ 的 `unique_ptr<T>`，不是 `shared_ptr<T>`

```latte
struct Point {        // 普通 struct
    x: f64
    y: f64
    // ❌ 不能有 Object 字段
    // 也不得标记 open/abstract，因此没有子类型
}

rich struct RichPoint {  // rich struct
    x: f64
    y: f64
    owner: User          // ✅ 可以持有 Object
    // 复制时 owner 是 unique 的，不是共享引用
}
```

> ⚠️ 注意：`String` 是**非 rich 值类型**（2026-07-29 修订），因此
> `struct Label { text: String }` 不需要 `rich`。需要 `rich` 的是
> 持有真正 Object（class 实例）的场景。

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

### Q5: 为什么 TypeReferenceParserLayer 不处理 rich/shared？

**A**: 因为 `rich` 和 `shared` 是**类型声明修饰符**，不是**类型引用修饰符**。

**职责分配**：
- `TypeReferenceParserLayer`：解析类型**使用**（变量声明、函数参数等）
- `DeclarationParserLayer`：解析类型**定义**（class/struct/wrapper 声明，
  包括 rich/shared 修饰符；统一声明层覆盖全部类型关键字）

```latte
// TypeReferenceParserLayer 处理这些：
var x: i32 = 42
var name: String? = null
var list: List\<String>
func process(data: MyStruct) { }

// DeclarationParserLayer 处理这些：
shared class Logger { }
rich struct Point3D { }
shared rich struct SharedData { }
```

### Q6: 字面量解析时遇到 `3.14`，Lexer 会输出什么？

**A**: Lexer 会输出**三个 token**：`[Word "3"]`, `[Notation "."]`, `[Word "14"]`

**为什么？**
- Lexer 的职责是简单的字符识别，不理解复杂语义
- `.` 是 notation，Lexer 不知道它是浮点数还是成员访问
- Parser 使用状态机识别这是浮点数，组合成 `FloatLiteralASTNode`

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

## 5. 技术债务与注意事项

已知限制与技术债务的当前清单见 `docs/PROGRESS_REPORT.md` §6
（该清单随里程碑推进变化，本文件不复制）。

长期注意事项：

- 字符字面量未实现
- Verbose 调试日志默认关闭；需要时加 `--verbose` 子命令（控制台）或 `--log-to PATH`（全量 JSONL 落盘）

---

## 6. 参考资源

### 6.1 必读文档

1. [AGENTS.md](AGENTS.md) - **稳定架构规则与开发约定**（Parser 架构规范所在）
2. [docs/SYNTAX.md](docs/SYNTAX.md) - **最权威的语法规范**
3. [docs/PROGRESS_REPORT.md](docs/PROGRESS_REPORT.md) - **进度唯一权威来源**
4. [docs/RUNTIME.md](docs/RUNTIME.md) - 运行时模型和类型系统
5. [docs/compiler/syntax/PARSER_ROADMAP.md](docs/compiler/syntax/PARSER_ROADMAP.md) - Parser 实现计划
6. [docs/compiler/syntax/EXPRESSION_ARCHITECTURE.md](docs/compiler/syntax/EXPRESSION_ARCHITECTURE.md) - 表达式架构设计
7. [docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md](docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md) - 语义分析与 BIL 生成架构（中端）
8. [docs/compiler/semantic/SEMANTIC_ROADMAP.md](docs/compiler/semantic/SEMANTIC_ROADMAP.md) - 语义分析路线图

### 6.2 外部资源

- [Claude Code 官方文档](https://claude.com/blog/using-claude-md-files)
- .NET 8.0 文档

---

## 7. 项目原则

1. **文档驱动** - 先理解 SYNTAX.md，再写代码
2. **测试驱动** - 每个功能都有测试
3. **模块化** - 每个 ParserLayer 职责单一
4. **渐进式** - 按 Roadmap 逐步实现，不跳步
5. **质量优先** - 宁可慢一点，不要留技术债
6. **不要猜测** - 不确定时查文档，不要凭直觉
7. **简洁优先** - 写代码时始终自问：这个真的有必要存在吗？有没有更简洁更优雅的方法？可不可以复用已有的轮子（比如已有的 Layer）？不要自己造轮子（详见 AGENTS.md §4.5）

---

**最后更新**: 2026-07-26
**项目状态**: 活跃开发中
