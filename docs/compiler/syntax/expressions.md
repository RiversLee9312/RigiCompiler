# 表达式 Parser：起点、路径与运算符

施工目标协议见 [parser.md](parser.md)，ExpressionRoot 的归属与完整性规则见 [ast.md](ast.md)，词法输入见 [lexer.md](lexer.md)。本页保留完整的委托片段和逐步解析例；这些片段展示协议，实际实现还要设置 Span 并传播上下文标记。

## 设计原则

### 职责边界

**ExpressionParserLayer 负责表达式起点识别、路径施工、运算符组合与嵌套调度；字面量、类型引用、实参表和独立结构化表达式委托专门 Layer。**

职责：

1. 识别表达式的起点类型
2. **委托给专门的 Layer** 进行具体解析
3. 处理运算符（一元、二元）及其限制（无优先级规则）
4. 处理后缀链（调用、索引、成员访问、泛型实参）
5. 管理表达式的组合

### 设计理由

将所有字面量、类型引用、参数表与块结构细节塞入 ExpressionParserLayer，会使状态机承担过多职责。可复用的独立结构仍应委托；路径状态与运算符包装则由本层直接施工，不能把“委托”误解为本层完全不实现解析逻辑。

## 架构图

```
ExpressionParserLayer (通用框架，构造时接收唯一挂载目标 ExpressionRootASTNode)
  │
  ├─ 识别表达式起点
  │  ├─ 字面量？      → 创建 LiteralExpressionASTNode，委托 LiteralParserLayer（原地 AttachLiteral）
  │  ├─ new？         → 创建 NewExpressionASTNode，委托 TypeReferenceParserLayer（原地填充 Type）
  │  ├─ 符号？        → 创建 PathExpressionASTNode（首段符号名；后缀链就地生长）
  │  ├─ 括号？        → 创建 GroupExpressionASTNode，递归 ExpressionParserLayer(group.InnerExpression)
  │  ├─ .Case？       → 创建 EnumCaseExpressionASTNode
  │  ├─ placeOf？     → 创建 PlaceOfExpressionASTNode，递归受限操作数层
  │  ├─ func/if/switch/typeOf/seq？ → 创建目标并委托各专门层
  │  └─ 一元运算符？  → 创建 UnaryExpressionASTNode + 递归 ExpressionParserLayer(unary.Operand)
  │
  ├─ 处理运算符（框架职责）
  │  ├─ 一元           → UnaryExpressionASTNode
  │  ├─ 二元           → BinaryExpressionASTNode（每层最多消费一个，见「无优先级规则」）
  │  ├─ 复合赋值       → CompoundAssignmentExpressionASTNode
  │  ├─ is/as/supers/with → 类型检查/转换节点，右侧委托 TypeReferenceParserLayer
  │  ├─ if?            → BinaryExpressionASTNode（空值回退）
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
（分组、字面量、enum case 等）遇路径后缀时包装为路径的表达式底座
（`Head.Expression`）。「首段是局部变量/命名空间/类型」与各段语义
（实例成员/静态成员/wrapper）是**语义上色**问题，全部归 P3 Binder；
语法层只表达 §1.4 的形态事实。`PathParserLayer` 继续服务**类型引用**
与 **import 路径**的符号解析，也服务 namespace 与注解名等静态符号路径；这些
位置保留 Symbol 形态，不参与表达式路径统一。

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
- namespace 与注解名同样复用静态 PathParserLayer
- 表达式位置的路径不再经过它；泛型实参统一走 TypeReferenceParserLayer
  （类型与表达式路径都支持完整类型实参：中段泛型的可空实参 `a.b\<String?>.c` 合法）

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
- Round 调用/构造允许空列表，Square 索引的空列表 `a[]` 拒绝；尾逗号允许，
  实参开头的逗号拒绝。具名名字必须是合法标识符，不能是数字词或保留字。
- 具名判别层与实参表达式层允许括号内换行，表达式层设置 `insideParens=true`；
  `allowBareReturn` 继承宿主，不因进入实参而重置。

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

相邻判定比较前一个 Token.End.offset 与当前 Token.Start.offset；`a > = b`
不会拼成 `>=`，`a + = b` 不会拼成 `+=`。中间空白或注释造成位置间隙，
不能因为 Parser 跳过注释而误合并。Lexer 已合并的 `<<`、`<=` 等天然相邻。

复合赋值全集为 `+= -= *= /= %= <<= >>= >>>= &= |= ^=`，共 11 个。
先重组 `>>` / `>>>`，再吸收相邻 `=`。构造 CompoundAssignmentExpressionASTNode，
把原未挂载表达式一次 Attach 到 Target，右侧委托 Value 的 Root；
Operator 存基础运算符，不含等号。复合赋值可出现在表达式位置，语句位置用
ExpressionStatement 包装；普通 `=` 是 CodeBlock 层的赋值语句分支。

一元前缀包括 `-`、`!`（位非）、`not`、`await`，没有一元 `+`、后缀递增递减或
`~` 一元运算。二元固定符号包括算术、比较、位运算及 `and`/`or`；
`in` 只由 LoopParserLayer 在循环头处理。中缀 `if` 进入 IfNullFallbackSeen，
要求下一 Token 为 `?`，组合成 `if?` 空值回退；不完整组合报错。

### 负号折叠与数值字面量

前缀负号先进入 MinusCandidate，读到非科学计数整数词后进入 MinusIntCandidate；
下一 Token 非点或 EOF 时按 `NumericLiteral.TryParseInt(..., negative:true)` 折叠为
负值整数字面量，不生成 Unary 节点。Span 从负号覆盖到整数词末尾，因此
`-2147483648` 能按 i32 完整有符号下界验证；`2147483648` 超界，`-1U` 不能为负。
这里是字面量规则，`-5 + x` 的负整数不等于禁止直接接二元的 `-x` 一元结果。

`-x`、`-(5)`、`-1.5`、`-2e3` 保持普通一元负。已读 `-3` 后遇 `.`，
把 `3` 作为 `seedNumericWord` 交给操作数表达式，再由 LiteralParserLayer 组合浮点，
当前点 Token Replay；不会退回字符流或重挂 Root。整型折叠与普通一元限制不同，
如 `- -5` 的内层是负整数字面量，而 `- -x` 仍违反连续一元规则。

LiteralParserLayer 用统一 NumericLiteral 处理 0x/0b/0o、类型后缀与下划线、
范围检查；浮点状态组合整数词、点、分数词及 e/E 指数（指数正负号可独立 Token）。
浮点解析拒绝非有限值与 float 转换溢出。整数后点的后继若是标识符，
通过 LiteralExpression.MemberAccessDotConsumed/Range 交还表达式层，
`7.twice()` 按 `(7).twice()` 的路径处理，不把它强行读成浮点。

## 结构化起点、类型操作与状态边界

`func` 委托 LambdaExpressionParserLayer；async 标记在 lambda 头内部，
写作 `func{async (...)...}`，`async func` 不能作为表达式起点。
完整 lambda 与 trailing lambda 共享节点/层，Body（单表达式）与 BlockBody
（多语句块）互斥；无 ReturnType 为 void。单表达式的取值与 void 表达式语句语义
由 Binder 决定；多语句返回值用 `return@_` 或 `return@标签`，lambda 体禁止裸 return。

`if` / `switch` 在 Initial 识别为结构化表达式，委托同时支持语句形态的专门层；
if 表达式强制 else，switch 两种形态都强制 default。分支均为 CodeBlock，
恰好单 ExpressionStatement 的隐式取值由语义阶段处理；多语句用带标签 return。
`seq`/`volatile`/`unsafe` 起点委托 SeqBlockParserLayer，语句与表达式共用块，
含 using 绑定与标签；匿名取值标签为 `_`。

`typeOf(expr)` 委托 TypeOfExpressionParserLayer。`placeOf operand` 由本层构造
专用 PlaceOfExpressionASTNode 并下钻受限表达式；禁止连续前缀或直接续接二元，
稳定存储/对象身份在 Binder 判别。`placeOf(x)` 的紧邻括号被拒绝，
`placeOf (x)` 或注释隔开的分组操作数按普通表达式处理。

`as`/`as?` 生成 CastExpressionASTNode，`is`/`supers`/`with`
生成 TypeCheckExpressionASTNode，右侧是类型引用，不走普通二元右表达式层。
`as?` 的安全标记由本层吸收。前导点 `.Success` 生成 EnumCaseExpressionASTNode；
参数化 `.Failed(404)` 由路径底座和 Call 后缀组合，而非 case 节点自带实参。
`result is .Failed` 填入 TargetCase，只有 is 接受这种右侧，
TargetCase 与 TargetType 互斥，case 的期望 enum 类型由语义阶段验证。

路径泛型必须先读反斜杠再读 `<`，完整实参交给 TypeReferenceParserLayer。
实参挂到路径当前首段/末段；刚闭合时相邻的额外 `>` 直接报错，
`foo\<i32>>(x)` 不能默认为比较/移位，空白隔开的 `foo\<i32> > x` 才允许比较。
非符号起点先包装为 Head.Expression；已经是路径时直接继续原节点，不把调用结果
反复包装成另一棵碎裂路径。

`insideParens` 在分组和调用/索引实参中为 true，并传给二元右侧与一元操作数；
仅 Initial、PrimaryParsed、OperatorSeen、MinusCandidate、MinusIntCandidate、
Completed 状态把换行视作空白，不把所有词法间隙一概豁免。泛型、成员名等
尚未完成的状态依照各自语法检查。裸 return 上下文沿委托传递见 [parser.md](parser.md)。

完成时先封当前表达式 Span，再唯一 `target.Attach(currentExpression)` 后 Pop(Replay)，
终止 Token 交回父层。EOF 在 Completed 或无需右括号的 PrimaryParsed 可收尾；
MinusIntCandidate 先确认折叠再收尾，其他未完成状态报 Unexpected end of file。

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
