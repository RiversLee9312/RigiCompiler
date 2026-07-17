# ExpressionParserLayer 架构设计

**日期**: 2026-07-17  
**版本**: 2.0 (模块化重构)

## 设计原则

### ✅ 正确的模块化原则

**ExpressionParserLayer 是一个通用框架，不直接实现具体表达式的解析逻辑。**

职责：
1. 识别表达式的起点类型
2. **委托给专门的 Layer** 进行具体解析
3. 处理运算符（一元、二元）
4. 管理表达式的组合

### ❌ 错误的设计（第一版）

第一版尝试在 ExpressionParserLayer 内部直接解析所有类型的表达式，违反了模块化原则。

## 架构图

```
ExpressionParserLayer (通用框架)
  │
  ├─ 识别表达式类型
  │  ├─ 字面量？      → 委托 LiteralParserLayer
  │  ├─ new？         → 委托 NewExpressionLayer
  │  ├─ 符号？        → 委托 PathParserLayer
  │  ├─ 括号？        → 递归 ExpressionParserLayer
  │  └─ 一元运算符？  → 创建 UnaryExpressionASTNode + 递归
  │
  └─ 处理运算符
     ├─ 二元运算符    → 创建 BinaryExpressionASTNode + 递归
     └─ 完成          → 返回结果
```

## 委托机制

### 1. 字面量解析

```csharp
// 识别字面量起点
if (IsLiteralStart(currentToken))
{
    // 创建临时容器
    var tempRoot = new RootASTNode();
    
    // 委托给 LiteralParserLayer
    return new ParserLayerResult.PushLayer(
        new LiteralParserLayer(tempRoot),
        true
    );
}
```

**优点**：
- LiteralParserLayer 已经完整实现字面量解析
- 无需重复实现
- 保持单一职责

### 2. 符号/路径解析

```csharp
if (currentToken is WordToken)
{
    var symbolExpr = new SymbolReferenceASTNode(parentNode);
    currentExpression = symbolExpr;

    // 委托给 PathParserLayer（自动处理 . ? () []）
    return new ParserLayerResult.PushLayer(
        new PathParserLayer(
            PathParserLayer.PathType.SymbolPath,
            symbolExpr.Symbol,
            lineBreakSensitive: true
        ),
        true
    );
}
```

**优点**：
- PathParserLayer 已经支持：
  - 成员访问 (`.`)
  - 泛型 (`<T>`)
  - 函数调用 (`()`)
  - 索引访问 (`[]`)
- 一次委托解决多个功能

### 3. new 表达式解析

```csharp
if (token.Content == Keywords.NEW)
{
    var newExpr = new NewExpressionASTNode(parentNode);
    currentExpression = newExpr;

    // 委托给 TypeReferenceParserLayer
    return new ParserLayerResult.PushLayer(
        new TypeReferenceParserLayer(newExpr.Type),
        false
    );
}
```

**优点**：
- 复用类型解析逻辑
- 支持泛型 new: `new List\<String>()`

### 4. 括号分组

```csharp
if (token.Content == "(")
{
    var groupExpr = new GroupExpressionASTNode(parentNode);
    
    // 递归调用自己
    return new ParserLayerResult.PushLayer(
        new ExpressionParserLayer(groupExpr),
        false
    );
}
```

**优点**：
- 自然的递归结构
- 处理任意嵌套

## 运算符处理

### 一元运算符（直接处理）

```csharp
var unaryExpr = new UnaryExpressionASTNode(parentNode)
{
    Operator = op,
    IsPrefix = true
};

// 递归解析操作数
return new ParserLayerResult.PushLayer(
    new ExpressionParserLayer(unaryExpr),
    false
);
```

### 二元运算符（直接处理）

```csharp
var binaryExpr = new BinaryExpressionASTNode(parentNode)
{
    Left = currentExpression,
    Operator = pendingOperator
};

// 递归解析右操作数
return new ParserLayerResult.PushLayer(
    new ExpressionParserLayer(binaryExpr),
    true
);
```

**为什么直接处理？**
- 运算符是表达式组合的核心逻辑
- 不涉及具体类型的解析
- 属于框架层的职责

## 重要特性：Latte 无运算符优先级

```latte
// ❌ 错误：歧义
var result = 1 + 2 * 3

// ✅ 正确：必须用括号
var result = 1 + (2 * 3)    // = 7
var result = (1 + 2) * 3    // = 9
```

**实现影响**：
- 简化了解析器设计
- 无需处理优先级表
- 遇到第二个运算符必须有括号

## 模块清单

### 已实现的专门 Layer
| Layer | 职责 | 状态 |
|-------|------|------|
| LiteralParserLayer | 字面量解析 | ✅ |
| PathParserLayer | 符号、成员访问、调用 | ✅ |
| TypeReferenceParserLayer | 类型引用 | ✅ |

### 需要实现的专门 Layer
| Layer | 职责 | 优先级 |
|-------|------|--------|
| NewExpressionLayer | new 表达式详细解析 | P1 |
| LambdaExpressionLayer | lambda 表达式 | P2 |
| ArrayLiteralLayer | 数组字面量 `[1, 2, 3]` | P2 |

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
| PathParserLayer / TypeReferenceParserLayer | 无需接口（直接原地写入目标节点） |

## 使用示例

### 解析简单表达式

```latte
42
```

流程：
```
ExpressionParserLayer
  → 识别字面量
  → 委托 LiteralParserLayer
  → 返回 IntLiteralASTNode(42)
```

### 解析函数调用

```latte
foo()
```

流程：
```
ExpressionParserLayer
  → 识别符号 'foo'
  → 委托 PathParserLayer
  → PathParserLayer 自动处理 ()
  → 返回 SymbolReferenceASTNode with call
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
    → 递归 ExpressionParserLayer
      → 识别字面量 2
    → 创建 BinaryExpressionASTNode(1, +, 2)
  → 包装为 GroupExpressionASTNode
```

### 解析 new 表达式

```latte
new List\<String>()
```

流程：
```
ExpressionParserLayer
  → 识别 new 关键字
  → 创建 NewExpressionASTNode
  → 委托 TypeReferenceParserLayer
    → 解析 List\<String>
  → 委托 PathParserLayer（处理参数列表）
  → 返回完整的 NewExpressionASTNode
```

## 优势总结

### ✅ 模块化
- 每个 Layer 职责单一
- 易于测试和维护
- 新增表达式类型只需添加新 Layer

### ✅ 可复用
- LiteralParserLayer 既用于顶层也用于表达式
- PathParserLayer 处理多种场景
- TypeReferenceParserLayer 用于变量、new、类型转换等

### ✅ 可扩展
- 添加新表达式类型不影响现有代码
- 委托机制天然支持插件式扩展

### ✅ 清晰
- 架构一目了然
- 职责边界明确
- 代码易读易懂

## 后续工作

### P1 阶段
1. 实现结果传递机制（方案 3）
2. 完善括号匹配逻辑
3. 实现 NewExpressionLayer 的参数列表解析

### P2 阶段
1. LambdaExpressionLayer
2. 数组字面量支持
3. 更多表达式类型

---

**设计原则**: "Delegate, don't implement" - 委托，而非直接实现
**核心思想**: ExpressionParserLayer 是指挥官，不是实干者
