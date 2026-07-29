# ExpressionParserLayer 架构设计

**日期**: 2026-07-26  
**版本**: 4.0（大扫除：TokenDisposition + 施工目标协议 + ExpressionRootASTNode，与当前代码一致）

## 设计原则

### ✅ 正确的模块化原则

**ExpressionParserLayer 是一个通用框架，不直接实现具体表达式的解析逻辑。**

职责：
1. 识别表达式的起点类型
2. **委托给专门的 Layer** 进行具体解析
3. 处理运算符（一元、二元）及其限制（无优先级规则）
4. 处理后缀链（调用、索引、成员访问、泛型实参）
5. 管理表达式的组合

### ❌ 错误的设计（第一版）

第一版尝试在 ExpressionParserLayer 内部直接解析所有类型的表达式，违反了模块化原则。

## 架构图

```
ExpressionParserLayer (通用框架，构造时接收唯一挂载目标 ExpressionRootASTNode)
  │
  ├─ 识别表达式起点
  │  ├─ 字面量？      → 创建 LiteralExpressionASTNode，委托 LiteralParserLayer（原地 AttachLiteral）
  │  ├─ new？         → 创建 NewExpressionASTNode，委托 TypeReferenceParserLayer（原地填充 Type）
  │  ├─ 符号？        → 创建 SymbolReferenceASTNode，委托 PathParserLayer（原地填充 Symbol）
  │  ├─ 括号？        → 创建 GroupExpressionASTNode，递归 ExpressionParserLayer(group.InnerExpression)
  │  └─ 一元运算符？  → 创建 UnaryExpressionASTNode + 递归 ExpressionParserLayer(unary.Operand)
  │
  ├─ 处理运算符（框架职责）
  │  ├─ 一元           → UnaryExpressionASTNode
  │  ├─ 二元           → BinaryExpressionASTNode（每层最多消费一个，见「无优先级规则」）
  │  └─ > 系列重组     → >=、>>、>>>（Lexer 不合并 > 系列）
  │
  └─ 处理后缀链（SYNTAX.md §1.4：在运算符之前整体形成）
     ├─ (  → 创建 CallExpressionASTNode（Callee.Attach 旧子树），委托 ArgumentListParserLayer
     ├─ [  → 创建 IndexExpressionASTNode（Object.Attach 旧子树），委托 ArgumentListParserLayer
     ├─ .  → 创建 MemberAccessASTNode（底座为表达式时）
     ├─ ?. → MemberAccessASTNode（IsSafeAccess = true）
     ├─ :  → 创建 WrapperAccessASTNode（SYNTAX §14.1）
     ├─ \< → 泛型实参（挂到 MemberAccess.GenericArguments；
     │        符号路径上的泛型由 PathParserLayer 直接解析）
     └─ new 的 ( → 填充 NewExpressionASTNode.Arguments（不生成 Call 节点）
```

**纯符号路径保持 Symbol 形态**：`a.b.c`、`a.b\<i32>` 由 PathParserLayer
整体解析为 `SymbolReferenceASTNode`；只有路径的底座是表达式（如 `foo().bar`、
`a[0].b`）时才产生 `MemberAccessASTNode`。

## 施工目标协议（大扫除后）

Parser 分为**控制流系统**与 **AST 施工系统**：

- **控制流系统**：`Parser` 主循环只负责 Layer 栈与 Token 调度；
  `ParserLayerResult` 为 `Continue`（单例）/ `PushLayer(layer, TokenDisposition)` /
  `PopLayer(TokenDisposition)`；`TokenDisposition.Consume` 表示当前 token 已消费、
  `Replay` 表示原样交给新栈顶重新处理。
- **AST 施工系统**：Layer 之间只传递控制权，不传递任何 AST 数据。
  父 Layer 在 Push 前创建或选择施工目标并传入子 Layer 构造函数；
  子 Layer 原地填充目标，或向目标附加子节点；`PopLayer` 不携带任何数据。

```text
父 Layer 创建或选择施工目标
            ↓
父 Layer 将施工目标传入子 Layer 构造函数
            ↓
子 Layer 原地填充目标，或向目标附加子节点
            ↓
子 Layer Pop
            ↓
父 Layer 恢复执行
```

**已删除的机制**（大扫除前存在，禁止恢复）：
`IResultProducer` / `IResultConsumer` / `GetResult()` / `OnChildResult()` /
`pendingResultHandler`，以及任何形式的回传替代（回调、Context 字段、父层引用等）。

## 表达式施工过程（ExpressionRootASTNode）

每个「语法上要求出现表达式的位置」由 `ExpressionRootASTNode` 作为稳定挂载点：

- 父层创建 Root（必需位置随宿主节点构造创建；可选位置出现时再创建，否则保持 null）；
- `ExpressionParserLayer` 构造时接收 Root 作为唯一最终挂载位置；
- 表达式层先在内部构造**未挂载**的完整子树（`currentExpression`）——
  后缀链与一元/二元包装通过「新包装节点.Root.Attach(旧子树)」逐层外卷；
- 表达式完整时执行且仅执行一次 `target.Attach(currentExpression)`，随后立即 Pop。

Root 的不变量（由 `ASTIntegrityValidator` 在 Parse 成功后自动验证）：
一次性 Attach、禁止替换、禁止附加已有父节点的表达式、
Root 中表达式的 Parent 必须指向 Root、节点无共享、Parent 链无环。

## 委托机制示例

### 1. 字面量解析

```csharp
// 本层创建 LiteralExpression 包装并作为当前主表达式，
// LiteralParserLayer 原地 AttachLiteral，无需回传
var literalExpr = new LiteralExpressionASTNode();
currentExpression = literalExpr;
state = State.PrimaryParsed;

return new ParserLayerResult.PushLayer(
    new LiteralParserLayer(literalExpr), TokenDisposition.Replay);
```

### 2. 符号/路径解析

```csharp
var symbolExpr = new SymbolReferenceASTNode();
currentExpression = symbolExpr;
state = State.PrimaryParsed;

return new ParserLayerResult.PushLayer(
    new PathParserLayer(
        PathParserLayer.PathType.SymbolPath,
        symbolExpr.Symbol,
        lineBreakSensitive: true
    ),
    TokenDisposition.Replay
);
```

**PathParserLayer 的职责边界**：
- 符号路径（`a.b.c`）
- 符号上的泛型实参（`a.b\<i32>`，`\` + `<` 进入泛型模式）
- **不处理**调用 `()`、索引 `[]`、安全访问 `?.` —— 这些由后缀链处理

### 3. new 表达式解析

```csharp
var newExpr = new NewExpressionASTNode();
currentExpression = newExpr;
state = State.PrimaryParsed;

return new ParserLayerResult.PushLayer(
    new TypeReferenceParserLayer(newExpr.Type),
    TokenDisposition.Consume  // 跳过 'new' token
);
```

构造参数列表由后缀链的 `(` 特判填充到 `newExpr.Arguments`：
`new User(id = 42)`、`new List\<i32>()`。

### 4. 实参列表解析

调用、索引、new 构造共用 `ArgumentListParserLayer`：

```csharp
var callExpr = new CallExpressionASTNode();
callExpr.Callee.Attach(currentExpression!);   // 未挂载子树包装
currentExpression = callExpr;
return new ParserLayerResult.PushLayer(
    new ArgumentListParserLayer(
        callExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, callExpr),
    TokenDisposition.Consume);
```

- 位置实参：`foo(1, x)`
- 具名实参：`foo(name = 42)`（"标识符后跟 `=`" 判别；否则按位置实参继续表达式）
- 每个 `ArgumentASTNode` 在表达式解析前创建并入列，
  实参表达式由 ExpressionParserLayer 直接附加到 `argument.Value` Root
- 具名判别退化的符号作为**未挂载 seed** 传给 ExpressionParserLayer
  （父层已施工出的局部结构，不是返回值）

## 运算符处理与无优先级规则

Latte **没有运算符优先级**（SYNTAX.md §1.3）：未括号化的多个运算符必须报错。

实现方式（每层表达式实例的两个开关）：

- `allowBinaryOperator`：右操作数层与一元表达式结果置 `false`
- `allowPrefixUnary`：一元操作数层置 `false`

```latte
var r = 1 + 2 * 3      // ❌ 报错：右操作数层不允许再消费二元运算符
var r = 1 + (2 * 3)    // ✅ 括号组是全新的表达式层，恢复全部能力
var v = not not x      // ❌ 报错：一元操作数层不允许连续一元
var v = -x + y         // ❌ 报错：一元结果不允许直接接二元
```

`>` 系列运算符（`>=`、`>>`、`>>>`）：Lexer 不合并 `>` 系列 notation
（嵌套泛型闭合 `List\<Map\<String, i32>>` 需要独立的 `>` token），
ExpressionParserLayer 在运算符状态下把相邻的 `>`、`=` 重组为对应运算符。

## 使用示例

### 解析函数调用

```latte
foo(1, name = 2)
```

流程：
```
ExpressionParserLayer（target = 某 ExpressionRootASTNode）
  → 创建 SymbolReferenceASTNode，委托 PathParserLayer 解析符号 foo
  → 后缀 ( → 创建 CallExpressionASTNode（Callee.Attach 符号子树）
  → 委托 ArgumentListParserLayer
    → 实参 1：ArgumentASTNode 入列，委托 ExpressionParserLayer(arg.Value) → IntLiteral
    → 实参 name = 2：具名判别（name 后是 =），同上
  → 表达式完整：target.Attach(CallExpressionASTNode)
```

### 解析后缀链

```latte
foo().bar\<i32>(x)
```

流程：
```
foo → SymbolReference（PathParserLayer）
( ) → Call(Callee.Attach foo)
. → MemberAccess(Object.Attach Call, bar)
\<i32> → 泛型实参挂到 MemberAccess.GenericArguments
(x) → Call(Callee.Attach MemberAccess, [x])
```

### 解析二元表达式

```latte
(1 + 2)
```

流程：
```
ExpressionParserLayer
  → 识别括号 → 创建 GroupExpressionASTNode
  → 递归 ExpressionParserLayer(group.InnerExpression)
    → 识别字面量 1
    → 识别运算符 +
    → 创建 BinaryExpressionASTNode：Left.Attach(1)
    → 递归 ExpressionParserLayer(binary.Right)（allowBinaryOperator = false）
      → 识别字面量 2
  → target.Attach(Group)
```

## 模块清单

### 已实现的专门 Layer
| Layer | 职责 | 状态 |
|-------|------|------|
| LiteralParserLayer | 字面量解析（AttachLiteral 到 LiteralExpressionASTNode） | ✅ |
| PathParserLayer | 符号路径 + `\<` 泛型实参 | ✅ |
| TypeReferenceParserLayer | 类型引用 | ✅ |
| ArgumentListParserLayer | 调用/索引/构造实参列表 | ✅ |
| GenericParametersParserLayer | 泛型参数列表 `\<...>`（声明侧） | ✅ |
| ParameterListParserLayer | 函数形参列表 `(...)`（声明侧） | ✅ |
| LambdaExpressionParserLayer | Lambda（完整/泛型/async/trailing；体双形态：单表达式/块） | ✅ M8/M33 |
| IfStatementParserLayer | if 表达式（强制 else）/ if 语句（分支体为代码块） | ✅ M7/M8/M33 |
| SwitchStatementParserLayer | switch 表达式 + switch 语句（强制 default，分支体为代码块） | ✅ M8/M33 |
| TypeOfExpressionParserLayer | typeOf 表达式 | ✅ M8 |
| SeqBlockParserLayer | seq 块（语句 + 表达式双形态） | ✅ M10 |

### 待实现的表达式能力
| 能力 | 优先级 | 说明 |
|------|--------|------|
| 数组字面量 `[1, 2, 3]` | P2 遗留 | 与索引 `[]` 的语境区分；注：SYNTAX.md 当前未定义数组字面量语法，实现前需先补充规范 |

## 优势总结

### ✅ 模块化
- 每个 Layer 职责单一
- 易于测试和维护
- 新增表达式类型只需添加新 Layer

### ✅ 可复用
- LiteralParserLayer 既用于顶层也用于表达式
- TypeReferenceParserLayer 用于变量、new、泛型实参、形参类型等
- ArgumentListParserLayer 统一服务调用、索引与构造

### ✅ 可扩展
- 添加新表达式类型不影响现有代码
- 施工目标协议天然支持插件式扩展

### ✅ 清晰
- 架构一目了然
- 职责边界明确
- 数据流严格单向（父→子），无隐藏回传
- AST 不变量由机器自动验证

---

**设计原则**: "Delegate, don't implement" - 委托，而非直接实现
**核心思想**: ExpressionParserLayer 是指挥官，不是实干者；
Layer 栈只传递控制权，AST 经 ExpressionRootASTNode 获得稳定挂载位置
