# ExpressionParserLayer 架构设计

**日期**: 2026-07-17  
**版本**: 3.0（结果传递 + 后缀链，与当前代码一致）

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
ExpressionParserLayer (通用框架)
  │
  ├─ 识别表达式起点
  │  ├─ 字面量？      → 委托 LiteralParserLayer（包装为 LiteralExpressionASTNode）
  │  ├─ new？         → 创建 NewExpressionASTNode，委托 TypeReferenceParserLayer
  │  ├─ 符号？        → 委托 PathParserLayer（符号路径 + \< 泛型实参）
  │  ├─ 括号？        → 递归 ExpressionParserLayer（GroupExpressionASTNode）
  │  └─ 一元运算符？  → 创建 UnaryExpressionASTNode + 递归
  │
  ├─ 处理运算符（框架职责）
  │  ├─ 一元           → UnaryExpressionASTNode
  │  ├─ 二元           → BinaryExpressionASTNode（每层最多消费一个，见「无优先级规则」）
  │  └─ > 系列重组     → >=、>>、>>>（Lexer 不合并 > 系列）
  │
  └─ 处理后缀链（SYNTAX.md §1.4：在运算符之前整体形成）
     ├─ (  → 创建 CallExpressionASTNode，委托 ArgumentListParserLayer
     ├─ [  → 创建 IndexExpressionASTNode，委托 ArgumentListParserLayer
     ├─ .  → 创建 MemberAccessASTNode（底座为表达式时）
     ├─ ?. → MemberAccessASTNode（IsSafeAccess = true）
     ├─ \< → 泛型实参（挂到 MemberAccess.GenericArguments；
     │        符号路径上的泛型由 PathParserLayer 直接解析）
     └─ new 的 ( → 填充 NewExpressionASTNode.Arguments（不生成 Call 节点）
```

**纯符号路径保持 Symbol 形态**：`a.b.c`、`a.b\<i32>` 由 PathParserLayer
整体解析为 `SymbolReferenceASTNode`；只有路径的底座是表达式（如 `foo().bar`、
`a[0].b`）时才产生 `MemberAccessASTNode`。

## 委托机制

### 1. 字面量解析

```csharp
// 先创建字面量表达式包装，字面量节点由结果传递机制回填
var literalExpr = new LiteralExpressionASTNode(parentNode, null!);
currentExpression = literalExpr;
pendingResultHandler = result => literalExpr.LiteralNode = result!;

return new ParserLayerResult.PushLayer(new LiteralParserLayer(literalExpr), true);
```

**优点**：
- LiteralParserLayer 已经完整实现字面量解析
- 无需重复实现
- 保持单一职责

### 2. 符号/路径解析

```csharp
var symbolExpr = new SymbolReferenceASTNode(parentNode);
currentExpression = symbolExpr;

return new ParserLayerResult.PushLayer(
    new PathParserLayer(
        PathParserLayer.PathType.SymbolPath,
        symbolExpr.Symbol,
        lineBreakSensitive: true
    ),
    true
);
```

**PathParserLayer 的职责边界**：
- 符号路径（`a.b.c`）
- 符号上的泛型实参（`a.b\<i32>`，`\` + `<` 进入泛型模式）
- **不处理**调用 `()`、索引 `[]`、安全访问 `?.` —— 这些由后缀链处理

### 3. new 表达式解析

```csharp
var newExpr = new NewExpressionASTNode(parentNode);
currentExpression = newExpr;

// 类型委托 TypeReferenceParserLayer（原地写入 newExpr.Type）
return new ParserLayerResult.PushLayer(
    new TypeReferenceParserLayer(newExpr.Type),
    false  // 跳过 'new' token
);
```

构造参数列表由后缀链的 `(` 特判填充到 `newExpr.Arguments`：
`new User(id = 42)`、`new List\<i32>()`。

### 4. 实参列表解析

调用、索引、new 构造共用 `ArgumentListParserLayer`：

```csharp
var callExpr = new CallExpressionASTNode(parentNode) { Callee = currentExpression! };
currentExpression = callExpr;
return new ParserLayerResult.PushLayer(
    new ArgumentListParserLayer(callExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, parentNode),
    false
);
```

- 位置实参：`foo(1, x)`
- 具名实参：`foo(name = 42)`（"标识符后跟 `=`" 判别；否则按位置实参继续表达式）
- 实参表达式递归委托 ExpressionParserLayer

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

## 结果传递机制

### ✅ 已实现（P1 第一阶段）

采用方案 3 的扩展版：**显式结果接口 + 弹层时自动传递**。

```csharp
// Parser/Parser.cs
// 产生结果的 Layer 实现此接口
public interface IResultProducer
{
    ASTNode? GetResult();
}

// 接收子 Layer 结果的父 Layer 实现此接口
public interface IResultConsumer
{
    void OnChildResult(ASTNode? result, IParserLayer child);
}
```

**传递时机**：Parser 主循环在 `PopLayer` 时检查——被弹出的层若是
`IResultProducer`，且新的栈顶父层是 `IResultConsumer`，则自动调用
`consumer.OnChildResult(producer.GetResult(), popped)`。

**父层的使用模式**（以 ExpressionParserLayer 为例）：

```csharp
// 委托前：设置回填动作
pendingResultHandler = result => binaryExpr.Right = (ExpressionASTNode)result!;
return new ParserLayerResult.PushLayer(rightLayer, true);

// 子层弹出时：框架自动回调
public void OnChildResult(ASTNode? result, IParserLayer child)
{
    var handler = pendingResultHandler;
    pendingResultHandler = null;
    handler?.Invoke(result);
}
```

**优点**：
- 接口可选，不影响既有 Layer（如 RootParserLayer 无需改动）
- 类型安全，语义清晰
- 父层无需关心子层何时弹出，结果在下一个 token 处理前已就位

**已接入的 Layer**：
| Layer | 角色 |
|-------|------|
| LiteralParserLayer | IResultProducer（字面量） |
| ExpressionParserLayer | IResultProducer + IResultConsumer |
| VariableDeclarationParserLayer | IResultConsumer（保存 Initializer） |
| ArgumentListParserLayer | IResultConsumer（接收实参表达式） |
| ParameterListParserLayer | IResultConsumer（接收默认值表达式） |
| PathParserLayer / TypeReferenceParserLayer | 无需接口（直接原地写入目标节点） |

## 使用示例

### 解析函数调用

```latte
foo(1, name = 2)
```

流程：
```
ExpressionParserLayer
  → 委托 PathParserLayer 解析符号 foo → SymbolReferenceASTNode
  → 后缀 ( → 创建 CallExpressionASTNode
  → 委托 ArgumentListParserLayer
    → 实参 1：委托 ExpressionParserLayer → IntLiteral
    → 实参 name = 2：具名判别（name 后是 =），委托 ExpressionParserLayer
  → 返回 CallExpressionASTNode
```

### 解析后缀链

```latte
foo().bar\<i32>(x)
```

流程：
```
foo → SymbolReference（PathParserLayer）
( ) → Call(foo)
. → MemberAccess(Call, bar)
\<i32> → 泛型实参挂到 MemberAccess.GenericArguments
(x) → Call(MemberAccess, [x])
```

### 解析二元表达式

```latte
(1 + 2)
```

流程：
```
ExpressionParserLayer
  → 识别括号
  → 递归 ExpressionParserLayer
    → 识别字面量 1
    → 识别运算符 +
    → 递归 ExpressionParserLayer（allowBinaryOperator = false）
      → 识别字面量 2
    → 创建 BinaryExpressionASTNode(1, +, 2)
  → 包装为 GroupExpressionASTNode
```

## 模块清单

### 已实现的专门 Layer
| Layer | 职责 | 状态 |
|-------|------|------|
| LiteralParserLayer | 字面量解析 | ✅ |
| PathParserLayer | 符号路径 + `\<` 泛型实参 | ✅ |
| TypeReferenceParserLayer | 类型引用 | ✅ |
| ArgumentListParserLayer | 调用/索引/构造实参列表 | ✅ |
| GenericParametersParserLayer | 泛型参数列表 `\<...>`（声明侧） | ✅ |
| ParameterListParserLayer | 函数形参列表 `(...)`（声明侧） | ✅ |
| LambdaExpressionParserLayer | Lambda（完整/泛型/async/trailing） | ✅ M8 |
| IfStatementParserLayer | if 表达式（强制 else）/ if 语句 | ✅ M7/M8 |
| SwitchStatementParserLayer | switch 表达式（强制 default） | ✅ M8 |
| TypeOfExpressionParserLayer | typeOf 表达式 | ✅ M8 |
| SeqBlockParserLayer | seq 块（语句 + 表达式双形态） | ✅ M10 |

### 待实现的表达式能力
| 能力 | 优先级 | 说明 |
|------|--------|------|
| wrapper 路径访问 `:` | P5 | `obj:MyWrapper` |
| 数组字面量 `[1, 2, 3]` | P2 遗留 | 与索引 `[]` 的语境区分 |
| 表达式分支多语句体 | 后续 | lambda 体与 if/switch 分支体当前仅单表达式 |

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
- 委托机制天然支持插件式扩展

### ✅ 清晰
- 架构一目了然
- 职责边界明确
- 代码易读易懂

---

**设计原则**: "Delegate, don't implement" - 委托，而非直接实现
**核心思想**: ExpressionParserLayer 是指挥官，不是实干者
