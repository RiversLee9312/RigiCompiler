# LatteCompiler Parser/PDA 重构规范

## 0. 目标与范围

本次重构在 P5 完成后进行。

本次重构只调整 Parser 的内部架构、AST 构造协议、Token 流转协议和测试基础设施，不修改 Latte 的任何既有语法与语义。

重构后的 Parser 必须满足以下总目标：

1. Parser 主循环只负责 Layer 栈和 Token 调度。
2. Layer 之间只传递控制权，不传递 AST 返回值。
3. 每个 Layer 在创建时就获得明确的施工目标。
4. AST 数据流只能由父层指向子层，不允许子层向父层回传结果。
5. Token 的消费和重放必须使用具名枚举，不允许使用布尔值。
6. 表达式位置统一使用 `ExpressionRootASTNode` 作为稳定挂载点。
7. EOF 必须成为正式 Token，不得继续伪装成换行。
8. 解析成功后，AST 必须满足可自动检查的不变量。

本文中的规范词含义如下：

- **必须**：违反即属于架构错误。
- **禁止**：任何 Layer 都不得采用。
- **应当**：除非有明确且记录在设计文档中的理由，否则必须遵守。
- **可以**：允许采用，但不是架构要求。

---

# 1. 最终架构模型

重构后的 Parser 分成两个互相独立的系统。

## 1.1 控制流系统

控制流系统由以下内容组成：

- `Parser`
- `IParserLayer`
- Parser Layer 栈
- `ParserLayerResult`
- `TokenDisposition`

它只回答以下问题：

- 当前由哪个 Layer 接收 Token；
- 当前 Token 是否已经消费；
- 是否压入一个子 Layer；
- 是否弹出当前 Layer；
- 当前 Token 是否需要交给新的栈顶 Layer 重新处理。

控制流系统禁止理解任何 AST 节点类型。

`Parser` 主循环禁止：

- 调用 `GetResult()`；
- 调用 `OnChildResult()`；
- 检查 Layer 是否实现某个 AST 结果接口；
- 保存“最近一次 AST 结果”；
- 把 AST 节点从子层转交给父层；
- 根据 AST 类型决定 Token 调度行为。

## 1.2 AST 施工系统

AST 施工系统由各个 Parser Layer 和传入 Layer 的目标节点组成。

它只回答以下问题：

- 当前 Layer 被允许施工哪个节点；
- 当前 Layer 可以向哪个节点附加子节点；
- 子 Layer 应当获得哪个施工目标；
- 新解析出的结构最终挂到 AST 的哪个位置。

数据流必须严格保持单向：

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

`PopLayer` 只表示控制权归还，不携带任何 AST 数据。

---

# 2. TokenDisposition 的最终定义

采用以下枚举：

```csharp
public enum TokenDisposition
{
    Consume,
    Replay
}
```

`ParserLayerResult` 修改为：

```csharp
public abstract record ParserLayerResult
{
    public sealed record PopLayer(
        TokenDisposition Disposition
    ) : ParserLayerResult;

    public sealed record PushLayer(
        IParserLayer LayerToPush,
        TokenDisposition Disposition
    ) : ParserLayerResult;

    public sealed record Continue : ParserLayerResult
    {
        private Continue() { }

        public static readonly ParserLayerResult Instance =
            new Continue();
    }
}
```

标准调用形式为：

```csharp
new ParserLayerResult.PushLayer(
    layer,
    TokenDisposition.Replay
);

new ParserLayerResult.PopLayer(
    TokenDisposition.Consume
);
```

允许使用较短的单行形式：

```csharp
new ParserLayerResult.PushLayer(layer, TokenDisposition.Replay);
new ParserLayerResult.PopLayer(TokenDisposition.Consume);
```

## 2.1 精确语义

### Continue

```csharp
ParserLayerResult.Continue.Instance
```

含义必须固定为：

1. 当前 Layer 保持在栈顶；
2. 当前 Token 已经由当前 Layer 消费；
3. Parser 前进到下一个 Token。

`Continue` 不得支持 `Replay`。

同一个 Layer 不得通过 `Continue` 请求重新处理当前 Token。需要让其他 Layer 处理当前 Token 时，必须使用 `PushLayer(..., Replay)` 或 `PopLayer(Replay)`。

### PushLayer + Consume

```csharp
new PushLayer(child, TokenDisposition.Consume)
```

含义为：

1. 当前 Layer 已经消费当前 Token；
2. 子 Layer 被压入栈；
3. 子 Layer 从下一个 Token 开始解析。

典型用途：

- 父层消费了语法前缀；
- 子层只负责前缀之后的内容。

例如父层已经消费 `new`：

```csharp
return new PushLayer(
    new TypeReferenceParserLayer(newExpression.Type),
    TokenDisposition.Consume
);
```

### PushLayer + Replay

```csharp
new PushLayer(child, TokenDisposition.Replay)
```

含义为：

1. 当前 Layer 没有消费当前 Token；
2. 子 Layer 被压入栈；
3. 当前 Token 原样交给子 Layer。

典型用途：

- 父层只识别出应当委托；
- 当前 Token 属于子语法结构。

例如表达式层看到字面量起始 Token：

```csharp
return new PushLayer(
    new LiteralParserLayer(literalExpression),
    TokenDisposition.Replay
);
```

### PopLayer + Consume

```csharp
new PopLayer(TokenDisposition.Consume)
```

含义为：

1. 当前 Layer 已经消费当前 Token；
2. 当前 Layer 被弹出；
3. 父 Layer 从下一个 Token 恢复解析。

典型用途：

- 当前 Token 是本层拥有的闭合符；
- 当前 Layer 消费闭合符后完成。

例如参数列表层消费 `)` 后结束：

```csharp
return new PopLayer(TokenDisposition.Consume);
```

### PopLayer + Replay

```csharp
new PopLayer(TokenDisposition.Replay)
```

含义为：

1. 当前 Layer 没有消费当前 Token；
2. 当前 Layer 被弹出；
3. 当前 Token 原样交给父 Layer。

典型用途：

- 当前 Token 是本层的终止信号；
- 但该 Token 实际属于父层。

例如表达式层遇到逗号，而逗号属于参数列表：

```csharp
return new PopLayer(TokenDisposition.Replay);
```

## 2.2 旧布尔值的机械映射

现有代码必须按照以下规则机械替换：

```text
shouldKeepToken == false  → TokenDisposition.Consume
shouldKeepToken == true   → TokenDisposition.Replay
```

不得根据方法名、调用位置或个人理解重新解释。

例如：

```csharp
new PopLayer(false)
```

必须转换为：

```csharp
new PopLayer(TokenDisposition.Consume)
```

而：

```csharp
new PushLayer(layer, true)
```

必须转换为：

```csharp
new PushLayer(layer, TokenDisposition.Replay)
```

TokenDisposition 重构阶段不得顺便修改语法行为。该阶段只进行机械替换和命名改善。

---

# 3. 彻底删除 Layer 返回值

以下接口必须删除：

```csharp
IResultProducer
IResultConsumer
```

以下方法必须全部删除：

```csharp
GetResult()
OnChildResult(...)
```

以下字段和同类机制必须全部删除：

```csharp
Action<ASTNode?>? pendingResultHandler
```

Parser 主循环中以下逻辑必须删除：

```csharp
if (popped is IResultProducer producer &&
    stack.TryPeek(out var parentLayer) &&
    parentLayer is IResultConsumer consumer)
{
    consumer.OnChildResult(producer.GetResult(), popped);
}
```

EOF 收尾代码中的相同逻辑也必须删除。

## 3.1 禁止用其他形式恢复返回值

删除结果接口后，禁止引入以下替代机制：

```csharp
Action<ASTNode>
Action<ExpressionASTNode>
Func<ASTNode>
Func<ExpressionASTNode>
TaskCompletionSource<ASTNode>
ParserLayerContext.LastResult
ParserLayerContext.CurrentResult
Parser.LastParsedNode
```

也禁止：

- 子 Layer 保存父 Layer 引用，并直接调用父 Layer 方法；
- 子 Layer 通过 `ParserLayerContext` 查找父节点；
- 子 Layer 将结果写入全局临时字段；
- 父 Layer 在子 Layer 弹出后查询子 Layer 内部字段；
- `PopLayer` 增加 AST 参数；
- 给 `ParserLayerResult` 增加泛型结果；
- 通过事件、委托或 continuation 回传 AST。

判断标准只有一个：

> 子 Layer 完成时，父 Layer 不需要从子 Layer 手中取回任何数据。

如果父 Layer 在子 Layer 弹出后仍需要调用某个“获取结果”的方法，说明重构没有完成。

---

# 4. 施工目标协议

每个 Parser Layer 的构造函数必须接收一个明确、强类型的施工目标。

允许的施工目标只有两类。

## 4.1 具体施工节点

父 Layer 已经知道具体 AST 类型，因此由父 Layer 创建节点，并把该节点传给子 Layer。

例如：

```csharp
var typeReference = new TypeReferenceASTNode(parameterNode);
parameterNode.Type = typeReference;

return new PushLayer(
    new TypeReferenceParserLayer(typeReference),
    TokenDisposition.Replay
);
```

子 Layer 只能填充传入的 `typeReference`。

它不创建另一个 `TypeReferenceASTNode` 来替换目标，也不返回新的类型节点。

## 4.2 附加目标节点

父 Layer 只知道这里需要某类语法结构，但具体节点类型要由子 Layer 根据 Token 决定。

此时父 Layer 必须创建或选择一个明确的附加目标，并把它传给子 Layer。

例如表达式位置使用：

```csharp
ExpressionRootASTNode
```

代码块语句使用：

```csharp
CodeBlockASTNode
```

声明容器可以使用：

```csharp
RootASTNode
ClassDeclarationASTNode
StructDeclarationASTNode
```

子 Layer 根据 Token 创建具体节点，然后直接附加到传入的目标中。

## 4.3 施工权限边界

每个 Layer 只允许修改：

1. 构造函数中明确传入的目标节点；
2. 该 Layer 在目标节点之下新创建的后代节点；
3. 由该 Layer 创建并明确传给子 Layer 的施工目标。

Layer 禁止修改：

- 目标节点的父节点；
- 目标节点的兄弟节点；
- 通过 `context.GetRootNode()` 获得的任意全局位置；
- 不属于当前施工子树的 AST 节点；
- 其他 Layer 正在施工的节点。

即使某个节点引用在技术上可达，也不表示当前 Layer 有权修改。

## 4.4 构造函数规则

Layer 构造函数应当使用最具体的目标类型：

```csharp
public ParameterListParserLayer(
    ParameterListASTNode target
)
```

优于：

```csharp
public ParameterListParserLayer(
    ASTNode target
)
```

禁止为了复用而普遍使用 `ASTNode`，然后在构造函数中进行运行时类型判断。

只有一个 Layer 确实支持多种目标类型，并且这些类型属于同一个明确协议时，才允许使用接口或抽象基类。

构造函数参数应分为三类：

```text
target  ：施工目标
options ：解析模式与语法限制
seed    ：父层已经解析出的、由子层继续施工的局部结构
```

共享诊断、位置与日志服务继续通过 `ParserLayerContext` 提供。

---

# 5. ExpressionRootASTNode 的正式定义

`ExpressionRootASTNode` 是一个真实的 Syntax AST 节点。

它表示：

> 一个语法上要求出现表达式的位置，以及最终填入该位置的一棵表达式子树。

它不是临时回调对象，也不是 Parser 私有 slot。

它必须保留在 Syntax AST 中，但在语义分析和 BIL Lowering 中被视为透明容器。

建议实现如下：

```csharp
public sealed class ExpressionRootASTNode : ASTNode
{
    private ExpressionASTNode? expression;

    public ExpressionRootASTNode(ASTNode parent)
        : base(parent)
    {
    }

    public bool IsAttached => expression is not null;

    public ExpressionASTNode Expression =>
        expression ?? throw new InvalidOperationException(
            "ExpressionRootASTNode has no attached expression."
        );

    public void Attach(ExpressionASTNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (expression is not null)
        {
            throw new InvalidOperationException(
                "ExpressionRootASTNode already contains an expression."
            );
        }

        if (node.Parent is not null)
        {
            throw new InvalidOperationException(
                "The expression node is already attached to another AST node."
            );
        }

        node.AttachTo(this);
        expression = node;
    }

    public override ASTNodeType NodeType =>
        ASTNodeType.ValueExpressionRoot;
}
```

## 5.1 ExpressionRootASTNode 的强制不变量

每个 `ExpressionRootASTNode` 必须满足：

1. 最多只能调用一次 `Attach`。
2. 禁止替换已经附加的表达式。
3. 禁止附加已经拥有父节点的表达式。
4. 一个表达式节点只能属于一个 Root。
5. 成功解析后的必需表达式 Root 必须恰好包含一个表达式。
6. 可选表达式不存在时，使用 `null` Root 表示。
7. 成功解析后，禁止使用“非 null 但为空的 Root”表示表达式缺失。
8. Root 的父节点必须是拥有该表达式位置的真实 AST 节点。
9. Root 中表达式节点的父节点必须是该 Root。
10. Parser 失败时允许存在未填充 Root，但失败结果必须整体丢弃，不得进入后续阶段。

`ExpressionRootASTNode` 不提供：

```csharp
Replace(...)
Clear()
Detach()
SetExpression(...)
```

表达式组合不得依赖替换 Root 内容。

## 5.2 ASTNode 父节点应当只能设置一次

为了保证 Root 的不变量，`ASTNode.parent` 应从公开可写字段改为只读属性：

```csharp
public abstract class ASTNode
{
    public ASTNode? Parent { get; private set; }

    protected ASTNode(ASTNode? parent)
    {
        Parent = parent;
    }

    internal void AttachTo(ASTNode parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (Parent is not null)
        {
            throw new InvalidOperationException(
                "AST node already has a parent."
            );
        }

        Parent = parent;
    }

    public abstract ASTNodeType NodeType { get; }
}
```

表达式节点在施工期间允许暂时没有父节点。

建议将 `ExpressionASTNode` 改为：

```csharp
public abstract class ExpressionASTNode : ASTNode
{
    protected ExpressionASTNode()
        : base(null)
    {
    }
}
```

具体表达式节点先作为未挂载施工子树创建，最终通过某个 `ExpressionRootASTNode.Attach()` 设置父节点。

---

# 6. Syntax AST 中表达式字段的统一规则

重构完成后，Syntax AST 中所有“表达式位置”必须使用 `ExpressionRootASTNode`，不得直接保存 `ExpressionASTNode`。

## 6.1 必需表达式

原本：

```csharp
public ExpressionASTNode Condition;
```

必须改为：

```csharp
public ExpressionRootASTNode Condition;
```

并在宿主节点构造时创建：

```csharp
Condition = new ExpressionRootASTNode(this);
```

## 6.2 可选表达式

原本：

```csharp
public ExpressionASTNode? Initializer;
```

必须改为：

```csharp
public ExpressionRootASTNode? Initializer;
```

只有语法中确实出现初始化表达式时，Parser 才创建 Root：

```csharp
var initializer = new ExpressionRootASTNode(variableNode);
variableNode.Initializer = initializer;

return new PushLayer(
    new ExpressionParserLayer(initializer),
    TokenDisposition.Replay
);
```

没有初始化表达式时：

```csharp
variableNode.Initializer = null;
```

## 6.3 表达式列表

每一个列表元素必须拥有独立 Root。

例如参数：

```csharp
public sealed class ArgumentASTNode : ASTNode
{
    public string? Name;
    public ExpressionRootASTNode Value;

    public ArgumentASTNode(ASTNode parent)
        : base(parent)
    {
        Value = new ExpressionRootASTNode(this);
    }
}
```

禁止多个列表元素共享同一个 Root。

## 6.4 必须迁移的表达式位置

至少包括：

- `VariableDeclarationASTNode.Initializer`
- `ParameterASTNode.DefaultValue`
- `BinaryExpressionASTNode.Left`
- `BinaryExpressionASTNode.Right`
- `UnaryExpressionASTNode.Operand`
- `GroupExpressionASTNode.InnerExpression`
- `ArgumentASTNode.Value`
- `CallExpressionASTNode.Callee`
- `IndexExpressionASTNode.Object`
- 所有索引参数
- `MemberAccessASTNode.Object`
- `LambdaExpressionASTNode.Body`
- `IfExpressionASTNode.Condition`
- `IfExpressionASTNode.ThenExpression`
- `IfExpressionASTNode.ElseExpression`
- `SwitchExpressionASTNode.Selector`
- `SwitchCaseASTNode.Pattern`
- `SwitchCaseASTNode.Body`
- `SwitchExpressionASTNode.DefaultBody`
- `TypeOfExpressionASTNode.Operand`
- `CastExpressionASTNode.Object`
- `RangeExpressionASTNode.From`
- `RangeExpressionASTNode.To`
- `TypeCheckExpressionASTNode.Object`
- `IfStatementASTNode.Condition`
- `LoopStatementASTNode.Iterable`
- `LoopStatementASTNode.Condition`
- `ReturnStatementASTNode.Value`
- `AssignStatementASTNode.Target`
- `AssignStatementASTNode.Value`
- `UsingBindingASTNode.Initializer`
- `ThrowStatementASTNode.Exception`
- `YieldStatementASTNode.Alarm`

重构完成后，在 `AST/` 目录搜索：

```text
ExpressionASTNode
```

除以下情况外，不应再出现直接表达式字段：

1. `ExpressionASTNode` 类本身；
2. `ExpressionRootASTNode` 内部保存的唯一表达式字段；
3. 表达式节点类型声明；
4. Parser 内部用于施工的局部变量。

## 6.5 GroupExpression 的 NodeType

当前 `GroupExpressionASTNode` 使用：

```csharp
ASTNodeType.ValueExpressionRoot
```

引入 `ExpressionRootASTNode` 后，`ValueExpressionRoot` 必须专属于 Root。

`GroupExpressionASTNode` 应增加独立枚举项：

```csharp
ASTNodeType.GroupExpression
```

禁止让 Group 与 Root 共用同一 NodeType。

---

# 7. ExpressionParserLayer 的最终协议

构造函数应为：

```csharp
public ExpressionParserLayer(
    ExpressionRootASTNode target,
    ExpressionASTNode? initialExpression = null
)
```

其中：

- `target` 是本次表达式解析的唯一最终挂载位置；
- `target` 在创建 Layer 时必须为空；
- `initialExpression` 只用于父层已经消费了表达式起点的情况；
- `initialExpression` 必须是未挂载节点；
- `initialExpression.Parent` 必须为 `null`。

Layer 内部可以保留：

```csharp
private readonly ExpressionRootASTNode target;
private ExpressionASTNode? currentExpression;
```

但不再实现任何结果接口。

## 7.1 表达式施工过程

表达式层必须先在内部构造完整的未挂载表达式子树。

它不得在识别到第一个 Primary 时立刻把 Primary 附加到最终 Root。

原因是后续可能出现：

- 调用后缀；
- 索引后缀；
- 成员访问；
- trailing lambda；
- 一元或二元包装；
- 类型操作符。

最终 Root 只能附加最终的最外层表达式。

## 7.2 已知具体表达式类型时

表达式层识别到具体类型后，由表达式层创建具体节点，并将该节点传给专门 Layer 填充。

例如字面量：

```csharp
var literalExpression = new LiteralExpressionASTNode();
currentExpression = literalExpression;
state = State.PrimaryParsed;

return new PushLayer(
    new LiteralParserLayer(literalExpression),
    TokenDisposition.Replay
);
```

`LiteralParserLayer` 直接填充 `literalExpression`，不返回字面量节点。

建议增加：

```csharp
public abstract class LiteralASTNode : ASTNode
```

并让：

```csharp
IntLiteralASTNode
FloatLiteralASTNode
StringLiteralASTNode
CharLiteralASTNode
BoolLiteralASTNode
NullLiteralASTNode
```

继承 `LiteralASTNode`。

`LiteralExpressionASTNode` 使用一次性附加：

```csharp
public void AttachLiteral(LiteralASTNode literal)
```

而不是公开可写的：

```csharp
public ASTNode LiteralNode;
```

## 7.3 结构化表达式

对于 `if`、`switch`、`typeOf`、`lambda`、`seq`：

```csharp
var ifExpression = new IfExpressionASTNode();
currentExpression = ifExpression;
state = State.PrimaryParsed;

return new PushLayer(
    new IfStatementParserLayer(ifExpression),
    TokenDisposition.Consume
);
```

子 Layer 填充已经传入的 `ifExpression`。

子 Layer 完成后，父层已经持有该节点引用，因此无需接收结果。

## 7.4 括号表达式

`GroupExpressionASTNode` 内部持有 Root：

```csharp
public sealed class GroupExpressionASTNode : ExpressionASTNode
{
    public ExpressionRootASTNode InnerExpression { get; }

    public GroupExpressionASTNode()
    {
        InnerExpression = new ExpressionRootASTNode(this);
    }
}
```

表达式层创建 Group 后：

```csharp
var group = new GroupExpressionASTNode();
currentExpression = group;

return new PushLayer(
    new ExpressionParserLayer(group.InnerExpression),
    TokenDisposition.Consume
);
```

内层表达式直接附加到 `group.InnerExpression`。

## 7.5 表达式包装

调用、索引、成员访问和二元运算必须通过“未挂载子树包装”完成。

例如调用：

```csharp
var call = new CallExpressionASTNode();

call.Callee.Attach(
    currentExpression
        ?? throw context.RaiseError(
            "Call expression has no callee."
        )
);

currentExpression = call;
```

此时：

```text
currentExpression = CallExpression
CallExpression.Callee = 原表达式
```

`CallExpressionASTNode` 自身仍未挂入最终 Root，因此后续还可以继续被另一个表达式包装。

例如：

```latte
foo()().bar
```

每次包装都把旧的 `currentExpression` 附加到新包装节点内部的 Root，然后让新包装节点成为新的 `currentExpression`。

这个过程不需要：

- 修改已经填充的最终 Root；
- 替换 Root 内容；
- 重新设置已有父节点；
- 从子 Layer 接收结果。

## 7.6 二元表达式

二元表达式节点应定义为：

```csharp
public sealed class BinaryExpressionASTNode : ExpressionASTNode
{
    public ExpressionRootASTNode Left { get; }
    public ExpressionRootASTNode Right { get; }
    public string Operator { get; set; }

    public BinaryExpressionASTNode()
    {
        Left = new ExpressionRootASTNode(this);
        Right = new ExpressionRootASTNode(this);
        Operator = "";
    }
}
```

看到二元运算符后：

```csharp
var binary = new BinaryExpressionASTNode
{
    Operator = pendingOperator
};

binary.Left.Attach(
    currentExpression
        ?? throw context.RaiseError(
            "Binary expression has no left operand."
        )
);

currentExpression = binary;

return new PushLayer(
    new ExpressionParserLayer(binary.Right),
    TokenDisposition.Replay
);
```

右操作数解析层直接将结果附加到 `binary.Right`。

父表达式层在子层弹出后只继续状态机，不读取任何返回值。

## 7.7 初始表达式 seed

当前 `ArgumentListParserLayer` 中，标识符可能先被消费，用于判定具名参数：

```latte
foo(name = value)
foo(name + 1)
```

当确定它不是具名参数时，可以创建未挂载的初始符号表达式：

```csharp
var initialSymbol = new SymbolReferenceASTNode();
initialSymbol.Symbol.symbol.elements.Add(
    new SymbolElement { name = pendingName }
);
```

然后：

```csharp
return new PushLayer(
    new ExpressionParserLayer(
        argument.Value,
        initialSymbol
    ),
    TokenDisposition.Replay
);
```

这不属于返回值机制。

它是父 Layer 将已经施工出的未挂载节点交给子 Layer 继续施工。

## 7.8 表达式完成

表达式层只能在完整表达式已经构造完成时执行：

```csharp
target.Attach(
    currentExpression
        ?? throw context.RaiseError(
            "Expression is incomplete."
        )
);
```

随后立即 Pop：

```csharp
return new PopLayer(TokenDisposition.Replay);
```

或者：

```csharp
return new PopLayer(TokenDisposition.Consume);
```

具体 disposition 由当前终止 Token 的所有权决定。

成功路径中，`target.Attach()` 必须且只能执行一次。

---

# 8. 其他 Layer 的返回值迁移规则

## 8.1 ParameterListParserLayer

默认值不得通过 `OnChildResult` 回填。

解析一个参数时，应当先创建当前参数节点：

```csharp
currentParameter = new ParameterASTNode(targetNode);
```

遇到默认值时：

```csharp
currentParameter.DefaultValue =
    new ExpressionRootASTNode(currentParameter);

return new PushLayer(
    new ExpressionParserLayer(
        currentParameter.DefaultValue
    ),
    TokenDisposition.Consume
);
```

表达式层完成后，默认值已经位于参数节点中。

参数列表层只需切换到等待逗号或右括号的状态。

## 8.2 ArgumentListParserLayer

每个参数节点必须在表达式解析前创建：

```csharp
currentArgument = new ArgumentASTNode(parentNode)
{
    Name = pendingName
};

targetList.Add(currentArgument);
```

然后：

```csharp
return new PushLayer(
    new ExpressionParserLayer(currentArgument.Value),
    TokenDisposition.Replay
);
```

不得保留：

```csharp
ExpressionASTNode? pendingValue
```

不得在表达式返回后再创建 `ArgumentASTNode`。

## 8.3 CodeBlockParserLayer

代码块层必须直接向传入的 `CodeBlockASTNode` 添加语句节点。

表达式语句改为向代码块添加 `ExpressionRootASTNode`：

```csharp
var expressionStatement =
    new ExpressionRootASTNode(targetBlock);

targetBlock.Children.Add(expressionStatement);

return new PushLayer(
    new ExpressionParserLayer(expressionStatement),
    TokenDisposition.Replay
);
```

代码块层不得等待表达式返回后再追加裸 `ExpressionASTNode`。

## 8.4 SeqBlockParserLayer

`SeqBlockParserLayer` 不再实现 `IResultProducer`。

无论 seq 出现在表达式位置还是语句位置，创建它的父 Layer 都必须持有其引用。

表达式位置：

```csharp
var seq = new SeqBlockExpressionASTNode();
currentExpression = seq;

return new PushLayer(
    new SeqBlockParserLayer(seq),
    TokenDisposition.Replay
);
```

语句位置：

```csharp
var seq = new SeqBlockExpressionASTNode(targetBlock);
targetBlock.Children.Add(seq);

return new PushLayer(
    new SeqBlockParserLayer(seq),
    TokenDisposition.Replay
);
```

`SeqBlockParserLayer` 始终只填充传入的 `seq`。

## 8.5 LiteralParserLayer

`LiteralParserLayer` 必须接收 `LiteralExpressionASTNode` 或其他明确的字面量附加目标。

它不得：

- 保存 `parsedLiteral` 供外部读取；
- 实现 `GetResult()`；
- 根据父节点是否为 Root 决定结果交付方式；
- 直接把结果挂到不明确的 `parent.Children`。

## 8.6 所有结构化 Layer

以下 Layer 均应只填充构造函数传入的目标：

- `IfStatementParserLayer`
- `SwitchStatementParserLayer`
- `LambdaExpressionParserLayer`
- `TypeOfExpressionParserLayer`
- `LoopParserLayer`
- `TryCatchFinallyParserLayer`
- `PropertyAccessorParserLayer`
- `DeclarationParserLayer`
- `VariableDeclarationParserLayer`
- `GenericParametersParserLayer`
- `TypeReferenceParserLayer`
- `PathParserLayer`

这些 Layer 成功 Pop 时不产生任何结果。

---

# 9. EOF 正式化

必须新增：

```csharp
public sealed class EndOfFileToken : Token
{
    public override string Content
    {
        get => "";
        set { }
    }

    public override TokenType Type =>
        TokenType.EndOfFile;
}
```

`TokenType` 中增加：

```csharp
EndOfFile
```

## 9.1 Parser 不得修改调用者 Token 列表

禁止：

```csharp
tokens.Add(new LineBreakToken());
```

应创建本地副本：

```csharp
var input = new List<Token>(tokens)
{
    CreateEndOfFileToken(tokens)
};
```

EOF 的 `CharRange` 应是零长度范围，位置位于源文件最后一个 Token 的结束位置。

## 9.2 EOF 的所有权

EOF 只由 `RootParserLayer` 消费。

所有非 Root Layer 收到 EOF 时必须二选一：

### 当前语法结构已经完整

```csharp
return new PopLayer(TokenDisposition.Replay);
```

这样同一个 EOF 向下层层弹栈，最终交给 Root。

### 当前语法结构不完整

立即抛出：

```text
Unexpected end of file
```

不得：

- 消费 EOF 后静默结束；
- 把 EOF 当作换行；
- Push 新 Layer；
- 等待 Parser 再喂一个哨兵；
- 返回 Continue 试图越过 EOF。

## 9.3 删除 EOF 收尾循环

以下机制必须删除：

```csharp
while (stack.Count > 1 && guard++ < 64)
```

Parser 不再反复喂 `LineBreakToken`。

EOF 自身必须完成全部栈收敛。

解析结束时必须满足：

```csharp
stack.Count == 1
```

并且唯一剩余项是 Root Layer。

否则抛出内部 Parser 状态错误。

---

# 10. ParserLayerContext 的收缩

`ParserLayerContext` 应只提供：

- 当前 Token 位置；
- 错误报告；
- 警告；
- 日志；
- 未来的诊断收集器。

非 Root Layer 禁止通过 Context 获取 Root AST 并修改。

建议删除：

```csharp
GetRootNode()
```

Parser 可以直接持有：

```csharp
var root = new RootASTNode();
stack.Push(new RootParserLayer(root));
```

最后：

```csharp
return root;
```

这可以彻底阻止 Layer 绕过施工目标协议，从全局根节点偷渡 AST。

`ContextImpl.current` 若没有明确用途，也应删除。

---

# 11. Layer 的拆分标准

不得仅根据文件行数拆 Layer。

一个状态群只有同时满足以下条件时，才应提取为独立 Layer：

1. 它有明确的语法起点和结束边界；
2. 它能获得独立、强类型的施工目标；
3. 它拥有相对独立的状态集合；
4. 提取后父 Layer 不需要读取其内部状态；
5. 它需要被多个父 Layer 复用，或者提取后能显著降低父 Layer 的职责复杂度。

以下情况不应单独建立 Layer：

- 只消费一个固定关键字；
- 只有两三个线性状态；
- 没有独立施工目标；
- 只能通过回调把结果交还父层；
- 提取后仍需频繁修改父层字段。

因此仍然维持：

```text
return / throw / yield / break / continue
```

作为 `CodeBlockParserLayer` 的内联子状态，而不是每项建立独立 Layer。

---

# 12. AST 完整性验证

Parser 成功后、进入语义分析前，应运行一次 AST 完整性验证。

验证器至少检查：

1. 所有必需的 `ExpressionRootASTNode` 均已填充。
2. 所有可选 Root 若非 null，则必须已填充。
3. Root 中表达式的 `Parent` 正确指向 Root。
4. 所有 AST 节点最多只有一个父节点。
5. 不存在同一个表达式节点被两个 Root 引用。
6. 二元表达式左右 Root 均已填充。
7. 一元表达式 Operand 已填充。
8. 调用表达式 Callee 已填充。
9. 所有 Argument 的 Value 已填充。
10. `if` 表达式三个表达式位置均已填充。
11. `switch` 表达式必须满足既有 default 规则。
12. 不存在成功解析后仍处于“施工中”的节点。
13. 所有必需的类型引用、代码块和声明字段均已完成。
14. AST 的 Parent 链不存在环。

验证失败应抛出内部编译器错误，而不是普通用户语法错误。

---

# 13. 测试要求

## 13.1 TokenDisposition 测试

必须使用假的 Parser Layer 单独测试四种组合：

```text
Push + Consume
Push + Replay
Pop  + Consume
Pop  + Replay
```

测试必须验证：

- 哪个 Layer 接收当前 Token；
- Token offset 是否前进；
- 栈深是否正确；
- 同一个 Token 是否被重放；
- 不存在重复消费或漏消费。

## 13.2 返回值清除测试

重构完成后，仓库搜索结果必须满足：

```text
IResultProducer       0 处
IResultConsumer       0 处
GetResult(            0 处
OnChildResult(        0 处
pendingResultHandler  0 处
```

Parser 目录中还应检查：

```text
Action<ASTNode
Action<ExpressionASTNode
Func<ASTNode
Func<ExpressionASTNode
```

除明确与 Parser 结果无关的代码外，原则上应为 0 处。

## 13.3 独立 Layer 测试

当前 entryLayer 测试模式不得继续让 Root 无条件吞掉剩余 Token。

应增加专用 `TestRootParserLayer`：

- 测试输入末尾自动加入 EOF；
- 被测 Layer 必须在预期边界完成；
- 被测 Layer 提前 Pop 后，如果仍剩普通 Token，测试立即失败；
- 只有 EOF 可以在被测 Layer 完成后交给 Test Root；
- Test Root 只接受 EOF。

这样可以发现 Layer 过早结束和漏消费 Token 的问题。

## 13.4 AST 结构测试

表达式测试不再只比较最终字符串。

至少增加以下断言：

```text
Root 是否存在
Root 是否已填充
Root.Expression 的具体类型
Parent 链是否正确
必要的子 Root 是否已填充
不存在节点共享
```

字符串快照可以继续保留，但不能作为唯一验证方式。

## 13.5 全量测试入口

必须提供单命令全量测试：

```bash
dotnet run -- --test-all
```

要求：

- 自动运行全部测试；
- 任意失败返回非零退出码；
- 不需要交互式菜单；
- 输出通过数、失败数和失败测试名称。

交互式菜单可以保留，但不能作为 CI 的唯一入口。

应增加最简单的 CI，在每次提交或 Pull Request 时执行：

```bash
dotnet build
dotnet run -- --test-all
```

---

# 14. 文档规则

`AGENTS.md` 只保存稳定架构规则，不保存容易变化的测试数量和阶段数字。

`CLAUDE.md` 应引用 `AGENTS.md` 中的 Parser 架构规范，不复制一份可能漂移的版本。

进度、测试数量和当前阶段只由：

```text
docs/PROGRESS_REPORT.md
```

维护。

P5 后应在 `AGENTS.md` 中明确加入：

```text
1. Layer 不返回 AST。
2. Layer 创建时必须获得施工目标。
3. Push/Pop 使用 TokenDisposition。
4. 表达式位置统一使用 ExpressionRootASTNode。
5. 子 Layer 禁止修改施工目标之外的 AST。
6. EOF 是正式 Token。
7. 新 Layer 必须有独立测试。
```

---

# 15. 最终验收条件

只有同时满足以下条件，重构才算完成：

1. `PushLayer` 和 `PopLayer` 不再接受 bool。
2. 所有调用均使用 `TokenDisposition.Consume` 或 `Replay`。
3. `IResultProducer` 已删除。
4. `IResultConsumer` 已删除。
5. `GetResult()` 已删除。
6. `OnChildResult()` 已删除。
7. `pendingResultHandler` 已删除。
8. Parser 主循环不包含任何 AST 结果传递逻辑。
9. Layer 构造函数均具有明确施工目标。
10. 子 Layer 不通过回调、Context 或父 Layer 引用返回节点。
11. 所有表达式位置均使用 `ExpressionRootASTNode`。
12. 每个 Root 最多附加一次表达式。
13. ExpressionParser 只在表达式完成时附加最终外层节点。
14. 表达式包装过程不替换 Root 内容。
15. AST Parent 只能设置一次。
16. EOF 使用正式 `EndOfFileToken`。
17. 不再使用换行 Token 充当 EOF。
18. 不再存在 EOF guard 收尾循环。
19. 独立 Layer 测试会拒绝未消费的普通 Token。
20. 全量测试可以单命令运行。
21. 任意测试失败会返回非零退出码。
22. AST 完整性验证全部通过。
23. 现有 Latte 语法测试行为不发生变化。

重构完成后的核心模型应当能够用一句话完整描述：

> Parser Layer 栈只传递控制权；父 Layer 在 Push 前确定施工目标；子 Layer 原地施工或向目标附加节点；表达式通过 ExpressionRootASTNode 获得稳定挂载位置；Pop 不传递任何数据。
