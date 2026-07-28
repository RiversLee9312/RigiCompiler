# Frontend 数据类型说明

本文档描述 LatteCompiler Frontend 阶段**实际使用**的数据类型（与当前代码一致，2026-07-27 修订）。

## 1. 位置信息

- **CharPosition**: 字符位置（行号、列号、偏移量），`Lexer/Lexer.cs`。
  行号 1 起始；列号 1 起始（换行算当前行最后一列）；`offset` 为 0 起始字符索引（M28 修复恒 0 bug）
- **CharRange**: 字符范围（起始位置、结束位置、源文件名），`Parser/Parser.cs`。
  `sourceName` 是源文件名的唯一来源（M28 起 `CharPosition` 不再携带）

## 2. Token 类型（词法分析器输出）

位于 `Lexer/Tokens.cs`（M30 起；符号常量 `Notations` 在 `Lexer/Notations.cs`）。
**Token**: 抽象基类，含 `Content`、`Type`、`CharRange`。

| Token 类型 | TokenType | 说明 |
|-----------|-----------|------|
| **WordToken** | Word | 单词：标识符、关键字、数字字面量片段 |
| **StringToken** | String | 字符串字面量（含插值原文） |
| **NotationToken** | Notation | 符号：单字符（`(`、`.`、`<` 等）或多字符（`==`、`->`、`<=` 等） |
| **CommentToken** | Comment | 注释（Parser 主循环统一跳过，不参与语法） |
| **LineBreakToken** | LineBreak | 换行（Latte 的语句终止符） |
| **EndOfFileToken** | EndOfFile | 文件结束（M25 起为正式 token）：Lexer 在输出末尾追加，只由 RootParserLayer 消费 |

注意：

- 关键字**不是**独立 Token 类型——以 WordToken 形式出现，由 Parser 比对 `Keywords` 常量识别（`Parser/Keywords.cs`）。
- `>` 系列（`>=`、`>>`、`>>>`）**不合并**为单个 token（嵌套泛型闭合需要独立 `>`）；由 ExpressionParserLayer 在运算符状态下重组。
- Lexer 不理解语义：`3.14` 输出 `Word "3"` + `Notation "."` + `Word "14"`，由 LiteralParserLayer 组合。

## 3. AST 节点类型（语法分析器输出）

**ASTNode**: 抽象基类（`Parent` 指针、`Span`），`AST/ASTNode.cs`（M30 起）。
子节点容器不是基类共有字段（M29）：各节点以语义明确的 [ChildAstNode] 字段自持
（`RootASTNode.Declarations`、`CodeBlockASTNode.Statements`、类型节点 `Members`）。
`Span`（`CharRange?`，M28）是节点的源码范围：层目标由 Parser 主循环按 token 流经
`ISpanReceiver.ReceiveSpan` 回填，层内自建节点由所在层显式设置，
`ExpressionRootASTNode` 未显式设置时透明继承内容表达式的 span。
节点类型一律用 CLR 类型判断（原 ASTNodeType 枚举已删除）；装子节点的字段/属性以
`[ChildAstNode]` 标注、父指针以 `[ParentAstNode]` 标注，携带 ASTNode 的非节点对象
（如 import 列表项 ImportItem struct）以 `[AstCarrier]` 标注——ASTVisitor（`AST/ASTVisitor.cs`，M28）
以统一实现反射遍历（遍历骨架与子节点枚举均为 virtual 可重载，M30），
ASTIntegrityValidator 校验父子指针一致性与 Span 合法性，
并审计「装 ASTNode 却未标注」的成员（M24/M28）。

### 3.1 根与符号（`AST/ASTNode.cs` / `AST/SymbolNodes.cs` / `AST/ImportNodes.cs`，M30）
- **RootASTNode**: 根节点（顶层条目挂 `Declarations`，M29）
- **SymbolASTNode**: 符号节点（`symbol: Symbol`）
- **ImportASTNode**: import 声明（列表项为 `[AstCarrier]` struct **ImportItem**）
- 符号结构：**Symbol**（`elements: SymbolElementSet`）→ **SymbolElement**（`name` + `generics: SymbolSet`）

### 3.2 字面量（`AST/LiteralNodes.cs`）
- **IntLiteralASTNode**（Value、IntType、IsHex；IntType 枚举：I32/I64/I16/I8/U32/U64/U16/U8）
- **FloatLiteralASTNode**（Value、IsFloat）
- **StringLiteralASTNode**（Value、HasInterpolation）
- **BoolLiteralASTNode**、**NullLiteralASTNode**

（字符字面量未实现，见 `../../PROGRESS_REPORT.md` §6。）

### 3.3 类型引用（`AST/TypeNodes.cs`）
- **TypeReferenceASTNode**: TypeSymbol（SymbolASTNode）、IsNullable
- 类型上的泛型实参挂在 `TypeSymbol.symbol.elements[*].generics`

### 3.4 声明（`AST/DeclarationNodes.cs`）
- **VariableDeclarationASTNode**: Modifiers、IsConst、Name、TypeAnnotation?、Initializer?（局部变量/字段/全局变量同一节点）
- **CallableDeclarationASTNode**: Modifiers、Kind（CallableKind: Func/Operator/Init）、Name、GenericParameters?、Parameters、ReturnType?、Body?（全局函数/方法/静态方法/运算符/构造函数同一节点，M14）
- **ClassDeclarationASTNode**: Modifiers、ClassName、GenericParameters?、BaseClass?、Interfaces（成员挂 `Members`，M29）
- **InterfaceDeclarationASTNode**: Modifiers、InterfaceName、GenericParameters?、BaseInterfaces
- **StructDeclarationASTNode**: Modifiers、StructName、GenericParameters?、BaseStruct?、Interfaces
- **EnumStructDeclarationASTNode**: Modifiers、EnumName、GenericParameters?、Cases（`[]` case 列表，M17）
- **EnumCaseASTNode**: CaseName、Arguments、DiscriminantValue?
- **WrapperDeclarationASTNode**: Modifiers、WrapperName、GenericParameters?（`@WrapperTarget` 类型标识与 `.proxy.*` 代理成员，M20）
- **wrapper 挂载接口**（M29）：`IWrapperAttachable`（`Annotations` 属性）+ `IEntity/IMethod/IValueWrapperAttachable` 三个分类标记接口；Variable→Value、Callable→Method、5 个类型节点（含 enum struct）→Entity；5 个类型节点的成员容器均为 `Members`
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
| **LiteralExpressionASTNode** | LiteralNode | 字面量包装（使字面量成为表达式） |
| **SymbolReferenceASTNode** | Symbol（SymbolASTNode） | 符号引用/纯符号路径（含泛型实参） |
| **GroupExpressionASTNode** | InnerExpression | 括号分组 |
| **NewExpressionASTNode** | Type、Arguments | new 构造 |
| **CallExpressionASTNode** | Callee、Arguments | 函数调用 |
| **IndexExpressionASTNode** | Object、Indices | 索引访问 |
| **MemberAccessASTNode** | Object、MemberName、IsSafeAccess、GenericArguments | 成员访问（底座为表达式时） |
| **ArgumentASTNode** | Name?、Value | 调用/索引/构造实参（可具名）；非 Expression 子类 |
| **LambdaExpressionASTNode** | IsAsync、Parameters、GenericParameters?、ReturnType、Body | lambda（体为单表达式） |
| **IfExpressionASTNode** | Condition、ThenExpression、ElseExpression | if 表达式（强制 else，分支为单表达式） |
| **SwitchExpressionASTNode** | Selector、Cases、DefaultBody | switch 表达式（强制 default） |
| **SwitchCaseASTNode** | Pattern、Body | case 分支；非 Expression 子类 |
| **TypeOfExpressionASTNode** | Operand | typeOf(expr) |
| **CastExpressionASTNode** | Object、TargetType、IsSafe | as / as? 转换 |
| **TypeCheckExpressionASTNode** | Object、Operator、TargetType? / TargetCase?（互斥） | is / supers / with 检查；is 右侧可为 enum case（§12.3） |
| **SeqBlockExpressionASTNode** | IsVolatile、UsingBindings、Label?、Body | seq 块（语句 + 表达式双形态） |

### 3.6 语句（`AST/StatementNodes.cs`）

| 节点 | 关键字段 | 说明 |
|------|----------|------|
| **CodeBlockASTNode** | Statements | `{ }` 代码块 |
| **IfStatementASTNode** | Condition、ThenBlock、ElseBranch? | if 语句（ElseBranch 为块或嵌套 if） |
| **LoopStatementASTNode** | Kind（LoopKind）、VariableName?、Iterable?、RangeTo?、Condition?、Label?、Body | for-each/范围/while/do-while（范围 = Iterable 起点 + RangeTo 终点） |
| **ReturnStatementASTNode** | Label?、Value? | return / return@label |
| **LoopControlStatementASTNode** | IsBreak、Label? | break / continue[@label] |
| **ExpressionStatementASTNode** | Expression、AssignValue? | 表达式开头的语句：纯表达式语句或赋值语句（M24 合并，双 Root 槽创建时归属即定） |
| **TryCatchFinallyStatementASTNode** | TryBlock、CatchClauses、FinallyParameter?、FinallyBlock? | 异常处理 |
| **CatchClauseASTNode** | VariableName?、ExceptionType、Body | catch 子句（`_` 丢弃异常变量） |
| **UsingBindingASTNode** | IsConst、VariableName、Type?、Initializer | seq using 资源绑定 |
| **ThrowStatementASTNode** | Exception | throw 语句 |
| **YieldStatementASTNode** | Alarm? | yield / yield alarm |

## 4. 异常类

- **LexerException**、**ParserException**（`Core/Exceptions.cs`）——用户源码的词法/语法错误
- **CompilerInternalException**（`AST/ASTIntegrityValidator.cs`）——编译器内部错误
  （AST 完整性校验失败等「不可能发生」的状态，与用户语法错误严格区分）

## 5. 使用流程

```
Source Code
    ↓
[Lexer] → List<Token>                           ✅ 已完成
    ↓
[Parser] → AST (RootASTNode)                    ✅ 已完成（P0–P5，含 M23–M30 重构）
    ↓
[Semantic Analyzer] → Validated AST             ← 下一阶段
    ↓
[BIL Generator] → BIL
```

## 6. 关键设计决策

1. **可变 class + 公有字段**：AST 节点为可变 class、公有字段，配合层栈式 Parser 的原地构建与施工目标协议
2. Parser 架构决策（层栈 + TokenDisposition + 施工目标协议、无优先级实现、简洁优先三问等）
   见 `../../../AGENTS.md` §4 与 `EXPRESSION_ARCHITECTURE.md`

## 7. 下一步

- ✅ 词法分析（Lexer）
- ✅ 语法分析（Parser，P0–P5 全部完成）
- ⏳ 语义分析（Semantic Analyzer）
- ⏳ BIL 输出
