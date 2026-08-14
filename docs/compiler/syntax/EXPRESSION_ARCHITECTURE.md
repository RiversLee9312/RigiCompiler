# ExpressionParserLayer 架构设计

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
  │  ├─ 符号？        → 创建 PathExpressionASTNode（首段符号名；后缀链就地生长）
  │  ├─ 括号？        → 创建 GroupExpressionASTNode，递归 ExpressionParserLayer(group.InnerExpression)
  │  └─ 一元运算符？  → 创建 UnaryExpressionASTNode + 递归 ExpressionParserLayer(unary.Operand)
  │
  ├─ 处理运算符（框架职责）
  │  ├─ 一元           → UnaryExpressionASTNode
  │  ├─ 二元           → BinaryExpressionASTNode（每层最多消费一个，见「无优先级规则」）
  │  └─ > 系列重组     → >=、>>、>>>（Lexer 不合并 > 系列）
  │
  └─ 处理后缀链（SYNTAX.md §1.4：路径表达式在运算符之前整体形成——
     │                一条完整路径链恰一个 PathExpressionASTNode）
     ├─ (  → 给当前段/首段追加 Call 后缀，实参委托 ArgumentListParserLayer
     ├─ [  → 给当前段/首段追加 Index 后缀，实参委托 ArgumentListParserLayer
     ├─ .  → 追加路径段（Connector = Dot）
     ├─ ?. → 追加路径段（Connector = SafeDot）
     ├─ :  → 追加路径段（Connector = Colon，SYNTAX §14.1）
     ├─ \< → 泛型实参（挂到当前段/首段 GenericArguments，委托 TypeReferenceParserLayer）
     ├─ {  → trailing lambda：追加 Call 后缀，lambda 为唯一实参
     └─ new 的 ( → 填充 NewExpressionASTNode.Arguments（不生成路径后缀）
```

**统一路径形态**：表达式位置的符号引用、调用、索引、成员访问
（含 `?.`）、wrapper 访问（`:`）统一施工为单个 `PathExpressionASTNode`
（首段 + 段序列，段带泛型实参与调用/索引后缀）；非符号起点的表达式
（分组、字面量、调用结果）遇路径后缀时包装为路径的表达式底座
（`Head.Expression`）。「首段是局部变量/命名空间/类型」与各段语义
（实例成员/静态成员/wrapper）是**语义上色**问题，全部归 P3 Binder；
语法层只表达 §1.4 的形态事实。`PathParserLayer` 继续服务**类型引用**
与 **import 路径**的符号解析（纯静态路径，不参与本统一）。

## 施工目标协议

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

**已删除的机制**（禁止恢复）：
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

### 2. 路径起点与路径段施工

符号起点不再委托子 Layer：本层直接创建 `PathExpressionASTNode` 并填入
首段符号名，后续 `.`/`?.`/`:`/`\<`/`(`/`[` 全部由本层状态机就地追加
路径段或后缀（一条完整路径链恰一个节点）：

```csharp
var path = new PathExpressionASTNode();
path.Head.Name = word.Content;
currentExpression = path;
state = State.PrimaryParsed;
```

**PathParserLayer 的职责边界**：
- **类型引用**的符号路径（`List\<Map\<String, i32>>`）与 **import 路径**
  ——纯静态路径，继续由它解析（Symbol 形态）
- 表达式位置的路径不再经过它；泛型实参统一走 TypeReferenceParserLayer
  （能力取并集：中段泛型的可空实参 `a.b\<String?>.c` 合法化）

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
var callSuffix = new PathSuffixASTNode(owner) { Kind = PathSuffixKind.Call };
SuffixesOf(owner).Add(callSuffix);           // owner = 当前路径段/首段
return new ParserLayerResult.PushLayer(
    new ArgumentListParserLayer(
        callSuffix.Arguments, ArgumentListParserLayer.BracketKind.Round, callSuffix),
    TokenDisposition.Consume);
```

- 位置实参：`foo(1, x)`
- 具名实参：`foo(name = 42)`（"标识符后跟 `=`" 判别；否则按位置实参继续表达式）
- 每个 `ArgumentASTNode` 在表达式解析前创建并入列，
  实参表达式由 ExpressionParserLayer 直接附加到 `argument.Value` Root
- 具名判别退化的符号作为**未挂载 seed** 传给 ExpressionParserLayer
  （父层已施工出的局部结构，不是返回值）

## 运算符处理与无优先级规则

Rigi **没有运算符优先级**（SYNTAX.md §1.3）：未括号化的多个运算符必须报错。

实现方式（每层表达式实例的两个开关）：

- `allowBinaryOperator`：右操作数层与一元表达式结果置 `false`
- `allowPrefixUnary`：一元操作数层置 `false`

```rigi
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

```rigi
foo(1, name = 2)
```

流程：
```
ExpressionParserLayer（target = 某 ExpressionRootASTNode）
  → 符号 foo → 创建 PathExpressionASTNode（Head.Name = foo）
  → 后缀 ( → 首段追加 Call 后缀，委托 ArgumentListParserLayer
    → 实参 1：ArgumentASTNode 入列，委托 ExpressionParserLayer(arg.Value) → IntLiteral
    → 实参 name = 2：具名判别（name 后是 =），同上
  → 表达式完整：target.Attach(PathExpressionASTNode)
```

### 解析后缀链

```rigi
foo().bar\<i32>(x)
```

流程：
```
foo        → PathExpression（Head.Name = foo）
( )        → 首段追加 Call 后缀
.          → 追加路径段 .bar
\<i32>     → 泛型实参挂到 .bar 段（TypeReferenceParserLayer 解析）
(x)        → .bar 段追加 Call 后缀（实参 x）
```

### 解析二元表达式

```rigi
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

### 专门 Layer
| Layer | 职责 | 状态 |
|-------|------|------|
| LiteralParserLayer | 字面量解析（AttachLiteral 到 LiteralExpressionASTNode） | ✅ |
| PathParserLayer | 符号路径 + `\<` 泛型实参（收窄为类型引用与 import 路径专用；表达式路径由 ExpressionParserLayer 就地施工） | ✅ |
| TypeReferenceParserLayer | 类型引用 | ✅ |
| ArgumentListParserLayer | 调用/索引/构造实参列表 | ✅ |
| GenericParametersParserLayer | 泛型参数列表 `\<...>`（声明侧） | ✅ |
| ParameterListParserLayer | 函数形参列表 `(...)`（声明侧） | ✅ |
| LambdaExpressionParserLayer | Lambda（完整/泛型/async/trailing；体双形态：单表达式/块） | ✅ |
| IfStatementParserLayer | if 表达式（强制 else）/ if 语句（分支体为代码块） | ✅ |
| SwitchStatementParserLayer | switch 表达式 + switch 语句（强制 default，分支体为代码块） | ✅ |
| TypeOfExpressionParserLayer | typeOf 表达式 | ✅ |
| SeqBlockParserLayer | seq 块（语句 + 表达式双形态） | ✅ |

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
