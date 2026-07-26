# Latte Compiler 进度报告

> **进度对齐标准**：本文档是项目进度的**唯一权威来源**。
> 每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须更新本文档；
> 更新时保持文档结构不变，并在「里程碑历史」追加一段。
> 计划与分工见 `compiler/frontend/PARSER_ROADMAP.md`；本文档只记录「现状」。

**报告日期**: 2026-07-26
**当前阶段**: P2 进行中（异常处理系统完成），P0、P1 已完成
**测试总计**: 262/262 通过 (100%)
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

---

## 2. 当前可解析语法

```latte
// 字面量
42, 0xFF, 100L, 3.14, 0.1f, "Hello ${x}", true, null

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

// if / switch 表达式（分支体当前为单表达式）
var r = if (x > 0) { x } else { opposite(x) }          // 必须有 else
var r = switch(expr) {
    (1) -> { "one" }                                   // 值匹配
    (_ > 10) -> { "big" }                              // 模式匹配（_ 引用 expr）
    default -> { "other" }                             // 必须有 default
}

// Lambda（含泛型、async、trailing）
var f = func{(x: i32): i32 -> (x + 1)}
var f = func{(width: TSize)\<TSize extends Size>: TSize -> width}
var loader = async func{(id: i32): SharedUser -> loadUserNow(id)}
list.map{(item: String): i32 -> item.length}           // 脱糖为调用实参

// 代码块与语句（语句以换行或 } 结束）
{
    var x = 1
    x = (1 + 2)                                        // 赋值
    foo().field = v
    return x                                           // return / return@seq value
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

// seq 作为表达式（return@seq/return@label）
const result = seq {
    const ac = a * c
    const discriminant = (b * b) - (4.0 * ac)
    return@seq sqrt(discriminant)  // 返回值
}

var r = seq named calc {
    return@calc getValue()
}

// 泛型参数列表（独立组件，待接入类型/函数声明）
\<TElement>, \<out T, in U>, \<named TValues... with Serializable>

// 函数形参列表（独立组件，待接入函数声明）
(a: i32, b: String = "x", rest: named i32...)
```

---

## 3. 组件状态详表

| 组件 | 状态 | 测试 | 说明 |
|------|------|------|------|
| LiteralParserLayer | ✅ | 15/15 | 全部字面量；字符字面量占位未实现 |
| TypeReferenceParserLayer | ✅ | 3/3 | 集成测试含于变量声明套件 |
| VariableDeclarationParserLayer | ✅ | 10/10 | Initializer 经结果传递保存 |
| ExpressionParserLayer | ✅ | 67/67 | roadmap #4 全部落地 |
| ArgumentListParserLayer | ✅ | 含于表达式套件 | 位置/具名/混合实参 |
| LambdaExpressionParserLayer | ✅ | 15/15 | roadmap #21 提前落地；体为单表达式 |
| SwitchStatementParserLayer | ✅ 表达式模式 | 6/6 | 语句模式待规范明确 |
| TypeOfExpressionParserLayer | ✅ | 7/7 | typeOf(expr) |
| CodeBlockParserLayer | ✅ | 27/27 | 语句识别与分发；return/break/continue 内联子状态 |
| IfStatementParserLayer | ✅ 两种模式 | 含于各套件 | 表达式模式强制 else；语句模式 else 可选 + else if 链 |
| LoopParserLayer | ✅ | 15/15 | for-each/范围/while/do-while/named 标签 |
| TryCatchFinallyParserLayer | ✅ | 9/9 | roadmap #10；多 catch 子句、finally(e)、嵌套 try |
| SeqBlockParserLayer | ✅ | 17/17 | roadmap #11；volatile/using/named；语句+表达式双形态 |
| ThrowStatement（内联） | ✅ | 10/10 | throw expression；配合 try-catch 构成完整异常系统 |
| GenericParametersParserLayer | ✅ | 21/21 | 声明/约束/型变/可变参数 |
| ParameterListParserLayer | ✅ | 14/14 | 普通/默认/可变/具名可变 |
| PathParserLayer | ✅ | 含于各套件 | 符号路径 + `\<` 泛型实参 |
| RootParserLayer | ✅ | 含于各套件 | 顶层分发 |
| DeclarationParserLayer / ImportParserLayer | ⚠️ 骨架 | - | 早期骨架，待 P3/P4 重建 |

---

## 4. 关键架构决策（摘要）

- **结果传递机制**：`IResultProducer`/`IResultConsumer` 可选接口，Parser 主循环在弹层时自动把子层结果递给父层（详见 `compiler/frontend/EXPRESSION_ARCHITECTURE.md`）
- **泛型语法 `\<...>`**：`<` 仅作小于号；Lexer 不合并 `>` 系列，`>=`/`>>`/`>>>` 由表达式层重组（详见 `SYNTAX.md` §3.6）
- **表达式后缀链**：纯符号路径保持 PathParserLayer 的 Symbol 形态；`(`/`[`/`.`/`?.`/`\<` 后缀由 ExpressionParserLayer 链接，底座为表达式时才产生 MemberAccessASTNode
- **独立 Layer 可测性**：`Parser.Parse(tokens, entryLayer)` 重载支持任意 Layer 独立驱动测试

---

## 5. 下一步计划

**P2 剩余部分**：
1. CoroutineOpsParserLayer（roadmap #12，await/yield/async）

**后续**：P3 类型声明（复用 GenericParametersParserLayer）、P4 函数声明（复用 ParameterListParserLayer + CodeBlockParserLayer）

---

## 6. 技术债务与已知限制

1. 字符字面量未实现（占位符）
2. wrapper 路径访问（`:`）未实现（留待 P5）
3. lambda 体与 if/switch 表达式分支体仍仅支持单表达式（CodeBlock 已落地，表达式分支的多语句接入留待后续）
4. switch 仅表达式模式（SYNTAX 未定义语句形态）
5. 复合赋值（`+=`/`-=` 等）未实现：Lexer 未合并这些 token，需重组机制
6. await/yield 协程操作未实现（roadmap #12）
7. 泛型参数/形参列表为独立组件，待 P3/P4 接入声明解析
8. DeclarationParserLayer / ImportParserLayer 为早期骨架，将在 P3/P4 重建
9. 4 个 nullable 编译警告（`Core/Utilities.cs`，不影响功能）

---

## 7. 里程碑历史

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
