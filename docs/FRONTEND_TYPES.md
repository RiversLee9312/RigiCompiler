# Frontend 数据类型说明

本文档描述 LatteCompiler Frontend 阶段使用的所有数据类型。

## 1. 基础类型

### 1.1 位置信息
- **CharPosition**: 字符位置（源文件名、行号、列号、偏移量）
- **CharRange**: 字符范围（起始位置、结束位置）

## 2. Token 类型（词法分析器输出）

### 2.1 基础 Token
- **Token**: 所有 Token 的基类
- **KeywordToken**: 关键字
- **IdentifierToken**: 标识符
- **OperatorToken**: 运算符
- **DelimiterToken**: 分隔符
- **LineBreakToken**: 换行符
- **EndOfFileToken**: 文件结束
- **WhitespaceToken**: 空白字符
- **CommentToken**: 注释

### 2.2 字面量 Token
- **IntLiteralToken**: 整数字面量（带后缀：L/S/B/U/UL/US/UB）
- **FloatLiteralToken**: 浮点数字面量（f 后缀）
- **StringLiteralToken**: 字符串字面量
- **CharLiteralToken**: 字符字面量
- **BoolLiteralToken**: 布尔字面量
- **NullLiteralToken**: null 字面量

## 3. AST 节点类型（语法分析器输出）

### 3.1 根节点
- **RootASTNode**: 包含所有顶层声明

### 3.2 顶层声明
- **NamespaceNode**: 命名空间声明
- **ImportNode**: 导入声明

### 3.3 类型声明
- **ClassDeclNode**: 类声明（可以是 local 或 shared）
- **StructDeclNode**: 结构体声明（可以是 rich/shared）
- **InterfaceDeclNode**: 接口声明
- **EnumStructDeclNode**: 枚举结构体声明
- **WrapperDeclNode**: Wrapper 声明（Entity/Method/Value）
- **EnumCaseNode**: 枚举 case 声明

### 3.4 成员声明
- **FieldDeclNode**: 字段声明（包含 getter/setter）
- **MethodDeclNode**: 方法声明
- **ConstructorDeclNode**: 构造函数（init）
- **OperatorDeclNode**: 运算符重载
- **PropertyAccessorsNode**: 属性访问器（getter/setter）

### 3.5 类型节点
- **PrimitiveTypeNode**: 基本类型（i8/i16/i32/i64/u8/u16/u32/u64/float/double/bool/char/string）
- **NamedTypeNode**: 命名类型（类、结构体、接口等）
- **NullableTypeNode**: 可空类型（T?）
- **GenericTypeRefNode**: 泛型类型引用
- **ArrayTypeNode**: 数组类型
- **MapTypeNode**: Map 类型
- **PairTypeNode**: Pair 类型
- **TypeOfTypeNode**: Type\<T> 类型
- **SpanTypeNode**: Span\<T> 类型

### 3.6 语句节点
- **BlockNode**: 代码块
- **VarDeclNode**: 变量声明
- **ExpressionStatementNode**: 表达式语句
- **IfNode**: if 语句
- **SwitchNode**: switch 语句
- **ForNode**: for 循环
- **WhileNode**: while 循环
- **DoWhileNode**: do-while 循环
- **BreakNode** / **ContinueNode**: 循环控制
- **ReturnNode**: 返回语句
- **ThrowNode**: 抛出异常
- **TryCatchFinallyNode**: 异常处理
- **YieldNode**: 协程挂起
- **SeqBlockNode**: seq 块（带 using）

### 3.7 表达式节点
#### 字面量
- **IntLiteralExpr**: 整数
- **FloatLiteralExpr**: 浮点数
- **StringLiteralExpr**: 字符串
- **CharLiteralExpr**: 字符
- **BoolLiteralExpr**: 布尔值
- **NullLiteralExpr**: null

#### 运算符
- **BinaryOpExpr**: 二元运算（算术、逻辑、位运算、比较、is/supers/with/as）
- **UnaryOpExpr**: 一元运算（opposite/not/bitwiseNot/await）

#### 调用和访问
- **CallExpr**: 函数调用
- **IndexExpr**: 索引访问
- **MemberAccessExpr**: 成员访问
- **SafeMemberAccessExpr**: 安全成员访问（?.）
- **WrapperAccessExpr**: Wrapper 访问（:）

#### 其他
- **CastExpr**: 类型转换（as/as?）
- **LambdaExpr**: Lambda 表达式
- **NewExpr**: new 表达式
- **TypeOfExpr**: typeOf 表达式
- **IfExpr**: if 表达式
- **SwitchExpr**: switch 表达式
- **SeqExpr**: seq 表达式（带返回值）
- **NullCoalesceExpr**: 空值回退（if?）
- **DestructuringExpr**: 解构表达式
- **ParameterHoleExpr**: 参数洞（_）
- **PatternPlaceholderExpr**: 模式占位符（_）

## 4. 符号表（语义分析器使用）

### 4.1 SymbolTable
作用域管理器，支持：
- `Define(Symbol)`: 定义符号
- `Resolve(string)`: 解析符号（向上查找）
- `ResolveInCurrentScope(string)`: 仅当前作用域查找

### 4.2 符号类型
- **VariableSymbol**: 变量
- **ParameterSymbol**: 参数
- **FunctionSymbol**: 函数
- **ClassSymbol**: 类
- **StructSymbol**: 结构体
- **InterfaceSymbol**: 接口
- **EnumSymbol**: 枚举
- **EnumCaseSymbol**: 枚举 case
- **WrapperSymbol**: Wrapper
- **GenericParamSymbol**: 泛型参数
- **FieldSymbol**: 字段
- **MethodSymbol**: 方法

## 5. 类型系统（类型检查器使用）

### 5.1 TypeInfo 基类
所有类型的基类，提供：
- `IsCompatibleWith(TypeInfo)`: 兼容性检查（子类型关系）
- `IsStrictlyEqual(TypeInfo)`: 严格相等（BIL 需要）
- `IsValueType`: 是否为 ValueType
- `IsObjectType`: 是否为 Object
- `IsShared`: 是否为 shared
- `IsRich`: 是否为 rich

### 5.2 具体类型
- **PrimitiveTypeInfo**: 基本类型
- **ClassTypeInfo**: 类类型（带 IsShared）
- **StructTypeInfo**: 结构体类型（带 IsRich, IsShared）
- **InterfaceTypeInfo**: 接口类型
- **EnumTypeInfo**: 枚举类型
- **WrapperTypeInfo**: Wrapper 类型
- **GenericTypeInfo**: 泛型参数类型
- **NullableTypeInfo**: 可空类型
- **ArrayTypeInfo**: 数组类型
- **MapTypeInfo**: Map 类型
- **PairTypeInfo**: Pair 类型
- **SpanTypeInfo**: Span 类型
- **TypeOfTypeInfo**: Type\<T> 类型
- **FunctionTypeInfo**: 函数类型
- **ErrorTypeInfo**: 错误类型（用于错误恢复）
- **UnknownTypeInfo**: 未知类型（用于类型推断）

### 5.3 类型约束
- **ExtendsConstraintInfo**: extends 约束
- **SupersConstraintInfo**: supers 约束
- **WithConstraintInfo**: with 约束

## 6. 异常类

- **CompilerException**: 编译器异常基类
- **LexerException**: 词法分析异常
- **ParserException**: 语法分析异常
- **SemanticException**: 语义分析异常
- **TypeCheckException**: 类型检查异常

## 7. 使用流程

```
Source Code
    ↓
[Lexer] → List<Token>
    ↓
[Parser] → AST (RootASTNode)
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

1. **使用 C# record**: 所有数据类型都是不可变的 record，便于模式匹配和调试
2. **分离 Node 和 Info**: AST 节点（*Node）表示源码结构，TypeInfo 和 Symbol 表示语义信息
3. **严格类型检查**: TypeInfo 同时支持 `IsCompatibleWith`（子类型）和 `IsStrictlyEqual`（BIL 需要）
4. **完整位置信息**: 所有节点都带 CharRange，便于错误报告
5. **Rich/Shared 建模**: 在类型系统层面支持 rich 和 shared 属性

## 9. 下一步

现有数据类型已足够支持：
- ✅ 词法分析（Lexer）
- ✅ 语法分析（Parser）
- ⏳ 符号表构建（Symbol Table Builder）
- ⏳ 类型检查（Type Checker）
- ⏳ 语义分析（Semantic Analyzer）

可以开始实现具体的分析器组件了。
