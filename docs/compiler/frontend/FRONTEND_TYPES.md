# Frontend 数据类型说明

本文档描述 LatteCompiler Frontend 阶段**实际使用**的数据类型（与当前代码一致，2026-07-17 修订）。

## 1. 位置信息

- **CharPosition**: 字符位置（源文件名、行号、列号、偏移量），`Lexer/Lexer.cs`
- **CharRange**: 字符范围（起始位置、结束位置、源文件名），`Parser/Parser.cs`

## 2. Token 类型（词法分析器输出）

位于 `Core/Utilities.cs`。**Token**: 抽象基类，含 `Content`、`Type`、`CharRange`。

| Token 类型 | TokenType | 说明 |
|-----------|-----------|------|
| **WordToken** | Word | 单词：标识符、关键字、数字字面量片段 |
| **StringToken** | String | 字符串字面量（含插值原文） |
| **NotationToken** | Notation | 符号：单字符（`(`、`.`、`<` 等）或多字符（`==`、`->`、`<=` 等） |
| **CommentToken** | Comment | 注释 |
| **LineBreakToken** | LineBreak | 换行（Latte 的语句终止符） |

注意：

- 关键字**不是**独立 Token 类型——以 WordToken 形式出现，由 Parser 比对 `Keywords` 常量识别。
- `>` 系列（`>=`、`>>`、`>>>`）**不合并**为单个 token（嵌套泛型闭合需要独立 `>`）；由 ExpressionParserLayer 在运算符状态下重组。
- Lexer 不理解语义：`3.14` 输出 `Word "3"` + `Notation "."` + `Word "14"`，由 LiteralParserLayer 组合。

## 3. AST 节点类型（语法分析器输出）

**ASTNode**: 抽象基类（`parent` 指针、`NodeType`），`Core/Utilities.cs`。

### 3.1 根与符号（`Core/Utilities.cs`，待逐步迁出）
- **RootASTNode**: 根节点（`Children: List<ASTNode>`）
- **SymbolASTNode**: 符号节点（`symbol: Symbol`）
- **ImportASTNode**: import 声明
- **AcquisitionExpressionASTNode**: 老式获取表达式（待评估去留）
- 符号结构：**Symbol**（`elements: SymbolElementSet`）→ **SymbolElement**（`name` + `generics: SymbolSet`）

### 3.2 字面量（`AST/LiteralNodes.cs`）
- **IntLiteralASTNode**（Value、IntType、IsHex；IntType 枚举：I32/I64/I16/I8/U32/U64/U16/U8）
- **FloatLiteralASTNode**（Value、IsFloat）
- **StringLiteralASTNode**（Value、HasInterpolation）
- **CharLiteralASTNode**（占位，未实现）
- **BoolLiteralASTNode**、**NullLiteralASTNode**

### 3.3 类型引用（`AST/TypeNodes.cs`）
- **TypeReferenceASTNode**: TypeSymbol（SymbolASTNode）、IsNullable
- 类型上的泛型实参挂在 `TypeSymbol.symbol.elements[*].generics`

### 3.4 声明（`AST/DeclarationNodes.cs`）
- **VariableDeclarationASTNode**: IsConst、Name、TypeAnnotation?、Initializer?
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
| **UnaryExpressionASTNode** | Operator、Operand、IsPrefix | 一元运算 |
| **LiteralExpressionASTNode** | LiteralNode | 字面量包装（使字面量成为表达式） |
| **SymbolReferenceASTNode** | Symbol（SymbolASTNode） | 符号引用/纯符号路径（含泛型实参） |
| **GroupExpressionASTNode** | InnerExpression | 括号分组 |
| **NewExpressionASTNode** | Type、Arguments | new 构造 |
| **CallExpressionASTNode** | Callee、Arguments | 函数调用 |
| **IndexExpressionASTNode** | Object、Indices | 索引访问 |
| **MemberAccessASTNode** | Object、MemberName、IsSafeAccess、GenericArguments | 成员访问（底座为表达式时） |
| **ArgumentASTNode** | Name?、Value | 调用/索引/构造实参（可具名） |

## 4. 符号表（语义分析器使用，`Core/FrontendTypesExtension.cs`）

- **SymbolTable**: `Define(SymbolInfo)`、`Resolve(string)`、`ResolveInCurrentScope(string)`
- **SymbolKind** 枚举：Variable、Parameter、Function、Class、Struct、Interface、Enum、Wrapper、Field、Method
- **SymbolInfo** 子类（已实现）：**VariableSymbolInfo**、**FunctionSymbolInfo**、**ClassSymbolInfo**、**StructSymbolInfo**

注意与 AST 的 `Symbol`（Utilities.cs）区分：前者是语义阶段已解析的符号定义，后者是源码中的符号引用（详见 `FRONTEND_ARCHITECTURE.md` §4.1）。

## 5. 类型系统（类型检查器使用，`Core/FrontendTypesExtension.cs`）

**TypeInfo** 抽象基类：`IsCompatibleWith`（子类型兼容）、`IsStrictlyEqual`（BIL 严格相等）、`IsValueType`/`IsObjectType`/`IsShared`/`IsRich`。

已实现的子类：

- **PrimitiveTypeInfo**（PrimitiveKind：i8..u64/float/double/bool/char/string）
- **ClassTypeInfo**（IsShared）
- **StructTypeInfo**（IsRich、IsShared）
- **FunctionTypeInfo**
- **ErrorTypeInfo**（错误恢复）、**UnknownTypeInfo**（类型推断）

辅助枚举：**IntegerSuffix**（None/L/S/B/U/UL/US/UB）。

接口/枚举/泛型/Nullable/Array/Map/Pair/Span/TypeOf 等 TypeInfo 子类尚未实现，将随语义分析阶段补充。

## 6. 异常类

- **LexerException**（`Core/Utilities.cs`）
- **ParserException**（`Core/Utilities.cs`）
- **SemanticException**、**TypeCheckException**（`Core/FrontendTypesExtension.cs`）

## 7. 使用流程

```
Source Code
    ↓
[Lexer] → List<Token>
    ↓
[Parser] → AST (RootASTNode)                    ← 当前阶段（P1 大部分完成）
    ↓
[Symbol Table Builder] → SymbolTable + Symbol annotations
    ↓
[Type Checker] → TypeInfo annotations
    ↓
[Semantic Analyzer] → Validated AST
    ↓
[BIL Generator] → BIL
```

## 8. 关键设计决策

1. **可变 class + 公有字段**：AST 节点为可变 class、公有字段，配合层栈式 Parser 的原地构建与结果回填
2. **Node 与 Info 分离**：AST 节点（*ASTNode）表示源码结构，TypeInfo/SymbolInfo 表示语义信息
3. **双轨符号系统**：AST 的 Symbol（源码引用）与语义阶段的 SymbolInfo（已解析定义）分离
4. **严格类型检查**：TypeInfo 同时支持 `IsCompatibleWith`（子类型）和 `IsStrictlyEqual`（BIL 需要）
5. **Rich/Shared 建模**：在类型系统层面直接支持 rich 和 shared 属性

## 9. 下一步

- ✅ 词法分析（Lexer）
- ✅ 语法分析（Parser，P1 大部分完成）
- ⏳ 符号表构建（Symbol Table Builder）
- ⏳ 类型检查（Type Checker）
- ⏳ 语义分析（Semantic Analyzer）
