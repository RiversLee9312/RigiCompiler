# Parser 驱动、施工协议与层边界

语法由 [语言规范](../../SYNTAX.md) 定义；本专题解释代码中的 Parser 调度与维护边界。表达式专项见 [expressions.md](expressions.md)，节点和 Span 见 [ast.md](ast.md)，Token 输入见 [lexer.md](lexer.md)。

## 层栈与状态机

Parser 主循环维护一个 Layer 栈，每个 token 交给栈顶 Layer 处理。核心协议在 `Parser/Parser.cs`：

**每个 Layer 内部用状态机驱动**（`private enum State` + switch），状态转换处要写注释。
模块化原则："Delegate, don't implement" —— 框架层（如 `ExpressionParserLayer`）负责识别、路由、路径施工与运算符处理；可复用的独立语法结构委托给专门 Layer。每个 Layer 职责清楚、可独立测试，协议定义见下节。

## 施工目标协议

Parser 分为**控制流系统**与 **AST 施工系统**：

- **控制流系统**：`Parser` 主循环只负责 Layer 栈与 Token 调度；
  `ParserLayerResult` 为 `Continue`（单例）/ `PushLayer(layer, TokenDisposition)` /
  `PopLayer(TokenDisposition)`；`TokenDisposition.Consume` 表示当前 token 已消费、
  `Replay` 表示原样交给新栈顶重新处理；Continue 消费并继续本层，
  PushLayer 委托子层，PopLayer 归还控制权。
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

## 施工规则

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
6. **EOF 是正式 Token**（`EndOfFileToken`）：由 Lexer 在输出 token 列表末尾
   追加（Parser 对绕过 Lexer 的调用方保持追加兼容），只由
   RootParserLayer 消费；非 Root 层遇 EOF：结构完整 → Pop(Replay) 上交，
   不完整 → 抛 "Unexpected end of file"。禁止用换行伪装 EOF。
7. **新 Layer 必须有独立测试**（`TestRootParserLayer` 驱动，见 [DEVELOPMENT.md](../../../DEVELOPMENT.md) 测试策略）。
8. **注释由 Parser 主循环统一跳过**：CommentToken 不参与语法，
   分发时直接跳过；各 Layer 不再自行处理注释。

## 上下文传递

**allowBareReturn 传染**：lambda 是裸 return 边界（SYNTAX §5.1）——
`CodeBlockParserLayer` 构造标记 `allowBareReturn`（默认 true）为 false 时，
遇无 @标签 return 抛 ParserException。lambda 体一律下传 false；标记沿施工链
向所有嵌套代码块与表达式深处传染（If/Switch/Loop/TryCatch/Seq/
VariableDeclaration/ArgumentList/TypeOf/Expression 各层逐一传递）——
**新 Layer 若创建 CodeBlockParserLayer 或 ExpressionParserLayer，必须同样
接收并传递该标记**；if/switch 表达式分支体不是 lambda 边界，继承父上下文标记。

## 模块清单

### 专门 Layer

| Layer | 职责 |
|-------|------|
| LiteralParserLayer | 字面量解析（AttachLiteral 到 LiteralExpressionASTNode），含数值组合与插值段委托 |
| PathParserLayer | 符号路径 + `\<` 泛型实参（类型引用、import、namespace 与注解名等静态符号路径；表达式路径由 ExpressionParserLayer 就地施工） |
| TypeReferenceParserLayer | 完整类型引用、可空标记与符号泛型实参 |
| ArgumentListParserLayer | 调用/索引/构造实参列表 |
| GenericParametersParserLayer | 泛型参数列表 `\<...>`（声明侧） |
| ParameterListParserLayer | 函数形参列表 `(...)`（声明侧） |
| LambdaExpressionParserLayer | Lambda（完整/async/trailing；体双形态：单表达式/块） |
| IfStatementParserLayer | if 表达式（强制 else）/ if 语句（分支体为代码块） |
| SwitchStatementParserLayer | switch 表达式 + switch 语句（强制 default，分支体为代码块） |
| TypeOfExpressionParserLayer | typeOf 表达式 |
| SeqBlockParserLayer | seq 块（语句 + 表达式双形态），含 using/volatile/unsafe 上下文 |
| RootParserLayer | 文件顶层声明、import、namespace、字面量测试入口、换行与 EOF |
| DeclarationParserLayer | 全局/成员声明、类型/可调用骨架、修饰符和 wrapper 注解 |
| VariableDeclarationParserLayer | 变量、解构、类型标注、初始化与属性访问器 |
| PropertyAccessorParserLayer | get/set 访问器及 backing field 一致性 |
| CodeBlockParserLayer | 块内语句分发及 return/throw/yield/循环控制等内联状态 |
| ImportParserLayer / NamespaceParserLayer | import 条目与 namespace 声明 |
| LoopParserLayer / TryCatchFinallyParserLayer | 循环及异常处理 |
| ExpressionParserLayer | 表达式起点、路径状态、组合与专门层委托 |

## 复用与增量设计

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

## 设计收益与扩展边界

### ✅ 模块化

- 每个 Layer 职责单一
- 易于测试和维护
- 新增表达式能力时，独立且可复用的语法结构由专门 Layer 承担；起点路由、节点模型、遍历与测试须同步更新

### ✅ 可复用

- LiteralParserLayer 既用于顶层也用于表达式
- TypeReferenceParserLayer 用于变量、new、泛型实参、形参类型等
- ArgumentListParserLayer 统一服务调用、索引与构造

### ✅ 可扩展

- 添加表达式类型时必须同步框架路由、AST 节点及测试；可复用层避免重复实现既有语法
- 施工目标协议支持模块化扩展；当前 Parser 层由父层显式构造，并无动态 Parser 插件注册机制

### ✅ 清晰

- 职责边界明确
- 数据流严格单向（父→子），无隐藏回传
- AST 不变量由机器自动验证

---

**设计原则**: "Delegate, don't implement" - 可复用的独立语法结构优先委托
**核心思想**: ExpressionParserLayer 负责路由、路径与组合施工；
Layer 栈只传递控制权，AST 经 ExpressionRootASTNode 获得稳定挂载位置

## 入口、终止与独立层测试

`Parser.Parse(List<Token>)` 持有新建 RootASTNode，底层是 RootParserLayer；Context 仅提供当前位置、最近消费位置、日志/告警与错误，不暴露全局 Root。可选 entryLayer 重载在 Root 上压入指定层；正式 Root 仍按自身规则处理剩余输入。

ParseCore 对末尾已有 EOF 的列表直接使用；绕过 Lexer 的手工列表缺 EOF 时复制后追加零宽 EOF，不修改调用方原列表。主循环先统一跳过 CommentToken，既不分发也不更新 lastConsumedRange；Replay 不推进 offset，Continue 与 Consume 才推进。EOF 正常层层 Replay 上交，最后被垫底层消费，输入结束时栈必须仅剩一个垫底层，否则 Unexpected End。标准 Parse 然后设置 Root span 并运行 ASTIntegrityValidator；测试专用 baseLayer/entryLayer 重载只驱动协议，不自动验证测试目标。

独立 Layer 测试采用 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)`。TestRootParserLayer 只接受 EOF；被测层提前结束或留有普通 Token 时立即报错。目标节点由测试自行创建并检查，不能只靠漂亮打印或 AST 描述字符串断言施工正确。表达式/字面量/路径/类型/实参回归与结构断言见 Tests/ 中对应 Parser 套件；[TestRootParserLayer.cs](../../../Tests/TestRootParserLayer.cs) 是严格垫底实现。

## 异常类

- **LexerException**、**ParserException**（`Core/Exceptions.cs`）——用户源码的词法/语法错误
- **CompilerInternalException**（`Core/Exceptions.cs`）——编译器内部错误
  （AST 完整性校验失败等「不可能发生」的状态，与用户语法错误严格区分）
