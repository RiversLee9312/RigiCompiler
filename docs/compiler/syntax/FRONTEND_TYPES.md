# Frontend 数据类型说明

本文档描述 RigiCompiler Frontend 阶段**实际使用**的数据类型（与当前代码一致）。

## 1. 位置信息

- **CharPosition**: 字符位置（行号、列号、偏移量），`Lexer/Lexer.cs`。
  行号 1 起始；列号 1 起始（换行算当前行最后一列）；`offset` 为 0 起始字符索引
- **CharRange**: 字符范围（起始位置、结束位置、源文件名），`Parser/Parser.cs`。
  `sourceName` 是源文件名的唯一来源（`CharPosition` 不再携带）

## 2. Token 类型（词法分析器输出）

位于 `Lexer/Tokens.cs`（符号常量 `Notations` 在 `Lexer/Notations.cs`）。
**Token**: 抽象基类，含 `Content`、`Type`、`CharRange`。

| Token 类型 | TokenType | 说明 |
|-----------|-----------|------|
| **WordToken** | Word | 单词：标识符、关键字、数字字面量片段 |
| **StringToken** | String | 字符串字面量（含插值原文） |
| **CharToken** | Char | 字符字面量（`'a'`）：`Value` 为转义展开后的字符，无插值概念 |
| **NotationToken** | Notation | 符号：单字符（`(`、`.`、`<` 等）或多字符（`==`、`->`、`<=` 等） |
| **CommentToken** | Comment | 注释（Parser 主循环统一跳过，不参与语法） |
| **LineBreakToken** | LineBreak | 换行（Rigi 的语句终止符） |
| **EndOfFileToken** | EndOfFile | 文件结束（为正式 token）：Lexer 在输出末尾追加，只由 RootParserLayer 消费 |

注意：

- 关键字**不是**独立 Token 类型——以 WordToken 形式出现，由 Parser 比对 `Keywords` 常量识别（`Parser/Keywords.cs`）。
- `>` 系列（`>=`、`>>`、`>>>`）**不合并**为单个 token（嵌套泛型闭合需要独立 `>`）；由 ExpressionParserLayer 在运算符状态下重组。
- Lexer 不理解语义：`3.14` 输出 `Word "3"` + `Notation "."` + `Word "14"`，由 LiteralParserLayer 组合。

## 3. AST 节点类型（语法分析器输出）

**ASTNode**: 抽象基类（`Parent` 指针、`Span`），`AST/ASTNode.cs`。
子节点容器不是基类共有字段：各节点以语义明确的 [ChildAstNode] 字段自持
（`RootASTNode.Declarations`、`CodeBlockASTNode.Statements`、类型节点 `Members`）。
`Span`（`CharRange?`）是节点的源码范围：层目标由 Parser 主循环按 token 流经
`ISpanReceiver.ReceiveSpan` 回填，层内自建节点由所在层显式设置，
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
节点类型一律用 CLR 类型判断（原 ASTNodeType 枚举已删除）；装子节点的字段/属性以
`[ChildAstNode]` 标注、父指针以 `[ParentAstNode]` 标注，携带 ASTNode 的非节点对象
（如 import 列表项 ImportItem struct）以 `[AstCarrier]` 标注——ASTVisitor（`AST/ASTVisitor.cs`）
以统一实现反射遍历（遍历骨架与子节点枚举均为 virtual 可重载），
ASTIntegrityValidator 校验父子指针一致性与 Span 合法性，
并审计「装 ASTNode 却未标注」的成员。

### 3.1 根与符号（`AST/ASTNode.cs` / `AST/SymbolNodes.cs` / `AST/ImportNodes.cs`）
- **RootASTNode**: 根节点（顶层条目挂 `Declarations`）
- **SymbolASTNode**: 符号节点（`symbol: Symbol`）
- **ImportASTNode**: import 声明（列表项为 `[AstCarrier]` struct **ImportItem**）
- 符号结构：**Symbol**（`elements: SymbolElementSet`）→ **SymbolElement**（`name` + `generics: SymbolSet`）

### 3.2 字面量（`AST/LiteralNodes.cs`）
- **IntLiteralASTNode**（Value（decimal，128 位十进制，可精确覆盖 u64 全范围）、IntType、Base；IntType 枚举：I32/I64/I16/I8/U32/U64/U16/U8；
  Base 为 LiteralIntBase 枚举（替代 IsHex 布尔）：Decimal/Hex/Binary/Octal，对应 0x/0b/0o 前缀；
  负号折叠（SYNTAX §3.3）：一元 `-` 直接作用于整数字面量时 Value 即负值（不产生 Unary 节点），
  解析期范围检查按目标类型完整有符号区间（`NumericLiteral.TryParseInt` 的 negative 语境））
- **FloatLiteralASTNode**（Value、IsFloat）
- **StringLiteralASTNode**（Value、HasInterpolation）
- **CharLiteralASTNode**（Value；SYNTAX §3.3：单引号内恰好一个字符或一个转义序列）
- **BoolLiteralASTNode**、**NullLiteralASTNode**

### 3.3 类型引用（`AST/TypeNodes.cs`）
- **TypeReferenceASTNode**: TypeSymbol（SymbolASTNode）、IsNullable
- 类型上的泛型实参挂在 `TypeSymbol.symbol.elements[*].generics`

### 3.4 声明（`AST/DeclarationNodes.cs`）
- **VariableDeclarationASTNode**: Modifiers、IsConst、Name、TypeAnnotation?、Initializer?（局部变量/字段/全局变量同一节点）
- **CallableDeclarationASTNode**: Modifiers、Kind（CallableKind: Func/Operator/Init）、Name、GenericParameters?、Parameters、ReturnType?、Body?（全局函数/方法/静态方法/运算符/构造函数同一节点）
- **ClassDeclarationASTNode**: Modifiers、ClassName、GenericParameters?、BaseClass?、Interfaces（成员挂 `Members`）
- **InterfaceDeclarationASTNode**: Modifiers、InterfaceName、GenericParameters?、BaseInterfaces
- **StructDeclarationASTNode**: Modifiers、StructName、GenericParameters?、BaseStruct?、Interfaces
- **EnumStructDeclarationASTNode**: Modifiers、EnumName、GenericParameters?、Cases（`[]` case 列表）
- **EnumCaseASTNode**: CaseName、Arguments、DiscriminantValue?
- **WrapperDeclarationASTNode**: Modifiers、WrapperName、GenericParameters?（`@WrapperTarget` 类型标识与 `.proxy.*` 代理成员）
- **wrapper 挂载接口**：`IWrapperAttachable`（`Annotations` 属性）+ `IEntity/IMethod/IValueWrapperAttachable` 三个分类标记接口；Variable→Value、Callable→Method、5 个类型节点（含 enum struct）→Entity；5 个类型节点的成员容器均为 `Members`
- **GenericParameterListASTNode**: Parameters、Constraints
- **GenericParameterASTNode**: Name、Variance（GenericVariance: None/Out/In）、IsVariadic、IsNamedVariadic
- **GenericConstraintASTNode**: Target、Kind（GenericConstraintKind: Extends/Supers/With）、Bound
- **ParameterListASTNode**: Parameters
- **ParameterASTNode**: Name、Type、IsVariadic、IsNamedVariadic、DefaultValue?

### 3.5 表达式（`AST/ExpressionNodes.cs`）
基类 **ExpressionASTNode**，子类：

| 节点 | 关键字段 | 说明 |
|------|----------|------|
| **BinaryExpressionASTNode** | Left、Operator、Right | 二元运算 |
| **CompoundAssignmentExpressionASTNode** | Target、Operator（基础运算符）、Value | 复合赋值（§13.2，+= 等 10 个） |
| **UnaryExpressionASTNode** | Operator、Operand、IsPrefix | 一元运算（含 await） |
| **LiteralExpressionASTNode** | Literal | 字面量包装（AttachLiteral 一次性附加，使字面量成为表达式） |
| **PathExpressionASTNode** | Head、Segments | 路径表达式（§1.4）：符号/调用/索引/成员（含 `?.`）/wrapper（`:`）后缀链统一为单一路径节点 |
| **PathHeadASTNode** | Name? / Expression?（互斥）、GenericArguments、Suffixes | 路径首段：符号头或表达式底座；非 Expression 子类 |
| **PathSegmentASTNode** | Connector（Dot/SafeDot/Colon）、Name、GenericArguments、Suffixes | 路径段；非 Expression 子类 |
| **PathSuffixASTNode** | Kind（Call/Index）、Arguments | 调用 `()` / 索引 `[]` 后缀；非 Expression 子类 |
| **GroupExpressionASTNode** | InnerExpression | 括号分组 |
| **NewExpressionASTNode** | Type、Arguments | new 构造 |
| **ArgumentASTNode** | Name?、Value | 调用/索引/构造实参（可具名）；非 Expression 子类 |
| **LambdaExpressionASTNode** | IsAsync、Parameters、ReturnType、Label?、Body? / BlockBody?（互斥） | lambda（体双形态：单表达式 Body / 多语句块 BlockBody，块内禁裸 return） |
| **IfExpressionASTNode** | Condition、ThenBody、ElseBody、Label? | if 表达式（强制 else，分支体为代码块，取值 return@_ / return@标签） |
| **SwitchExpressionASTNode** | Selector、Cases、DefaultBody、Label? | switch 表达式（强制 default，DefaultBody 为 CodeBlockASTNode?） |
| **SwitchCaseASTNode** | Pattern、Body | case 分支（Body 为代码块）；非 Expression 子类 |
| **TypeOfExpressionASTNode** | Operand | typeOf(expr) |
| **CastExpressionASTNode** | Object、TargetType、IsSafe | as / as? 转换 |
| **TypeCheckExpressionASTNode** | Object、Operator、TargetType? / TargetCase?（互斥） | is / supers / with 检查；is 右侧可为 enum case（§12.3） |
| **EnumCaseExpressionASTNode** | CaseName | 前导点 enum case 引用（`.Success`） |
| **SeqBlockExpressionASTNode** | IsVolatile、UsingBindings、Label?、Body | seq 块（语句 + 表达式双形态，匿名默认标签 `_`） |

### 3.6 语句（`AST/StatementNodes.cs`）

| 节点 | 关键字段 | 说明 |
|------|----------|------|
| **CodeBlockASTNode** | Statements | `{ }` 代码块 |
| **IfStatementASTNode** | Condition、ThenBlock、ElseBranch? | if 语句（ElseBranch 为块或嵌套 if） |
| **SwitchStatementASTNode** | Selector、Cases、DefaultBody | switch 语句（分支体为代码块，强制 default；结果值被丢弃） |
| **LoopStatementASTNode** | Kind（LoopKind）、VariableName?、Iterable?、RangeTo?、Condition?、Label?、Body | for-each/范围/while/do-while（范围 = Iterable 起点 + RangeTo 终点） |
| **ReturnStatementASTNode** | Label?、Value? | return / return@label |
| **LoopControlStatementASTNode** | IsBreak、Label? | break / continue[@label] |
| **ExpressionStatementASTNode** | Expression、AssignValue? | 表达式开头的语句：纯表达式语句或赋值语句（合并，双 Root 槽创建时归属即定） |
| **TryCatchFinallyStatementASTNode** | TryBlock、CatchClauses、FinallyParameter?、FinallyBlock? | 异常处理 |
| **CatchClauseASTNode** | VariableName?、ExceptionType、Body | catch 子句（`_` 丢弃异常变量） |
| **UsingBindingASTNode** | IsConst、VariableName、Type?、Initializer | seq using 资源绑定 |
| **ThrowStatementASTNode** | Exception | throw 语句 |
| **YieldStatementASTNode** | Alarm? | yield / yield alarm |

## 4. 异常类

- **LexerException**、**ParserException**（`Core/Exceptions.cs`）——用户源码的词法/语法错误
- **CompilerInternalException**（`Core/Exceptions.cs`）——编译器内部错误
  （AST 完整性校验失败等「不可能发生」的状态，与用户语法错误严格区分）

## 5. 使用流程

```
Source Code
    ↓
[Lexer] → List<Token>
    ↓
[Parser] → AST (RootASTNode)
    ↓
[Semantic Analyzer] → Validated AST
    ↓
[BIL Generator] → BIL
```

## 6. 关键设计决策

1. **可变 class + 公有字段**：AST 节点为可变 class、公有字段，配合层栈式 Parser 的原地构建与施工目标协议
2. Parser 架构决策（层栈 + TokenDisposition + 施工目标协议、无优先级实现、简洁优先三问等）
   见 `../../../AGENTS.md` §4 与 `EXPRESSION_ARCHITECTURE.md`
