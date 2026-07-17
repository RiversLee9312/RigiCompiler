# Frontend 类型系统说明

## 项目结构

LatteCompiler 的 Frontend 类型系统分为以下几个文件：

### 1. **Utilities.cs** - 基础类型
包含项目最初定义的基础类型：
- `Token` (基类) - 词法单元
  - `WordToken` - 单词
  - `CommentToken` - 注释
  - `StringToken` - 字符串
  - `LineBreakToken` - 换行
  - `NotationToken` - 符号
- `ASTNode` (基类) - AST 节点基类
  - `RootASTNode` - 根节点
  - `SymbolASTNode` - 符号节点
  - `ImportASTNode` - 导入节点
  - `AcquisitionExpressionASTNode` - 获取表达式节点
- `Symbol` - 符号定义（用于 AST）
- `LexerException`, `ParserException` - 异常类

### 2. **Lexer.cs** - 词法分析器相关
包含：
- `CharPosition` - 字符位置（行、列、偏移量）
- `ILexerLayer` - 词法分析器层接口
- `LexerLayerContext` - 词法分析器上下文
- `LexerLayerResult` - 层处理结果

### 3. **Parser.cs** - 语法分析器相关
包含：
- `CharRange` - 字符范围（起始、结束位置）
- `IParserLayer` - 语法分析器层接口
- `ParserLayerContext` - 语法分析器上下文
- `ParserLayerResult` - 层处理结果

### 4. **FrontendTypesExtension.cs** - 类型系统扩展（新增）
这个文件扩展了现有系统，添加了完整的语义分析支持：

#### 4.1 整数后缀
```csharp
public enum IntegerSuffix
{
    None, L, S, B, U, UL, US, UB
}
```

#### 4.2 符号表（SymbolTable）
用于语义分析阶段的作用域管理：
```csharp
public class SymbolTable
{
    void Define(SymbolInfo symbol);
    SymbolInfo? Resolve(string name);
    SymbolInfo? ResolveInCurrentScope(string name);
}
```

**注意**：`SymbolTable` 中的 `SymbolInfo` 与 `Utilities.cs` 中的 `Symbol` 是不同的：
- `Utilities.Symbol` - 用于 AST 阶段，表示源码中的符号引用
- `FrontendTypesExtension.SymbolInfo` - 用于语义分析，表示已解析的符号定义

#### 4.3 符号信息（SymbolInfo）
```csharp
public enum SymbolKind
{
    Variable, Parameter, Function, Class, Struct, 
    Interface, Enum, Wrapper, Field, Method
}

public abstract class SymbolInfo { ... }
├── VariableSymbolInfo
├── FunctionSymbolInfo
├── ClassSymbolInfo
└── StructSymbolInfo
```

#### 4.4 类型系统（TypeInfo）
完整的类型检查支持：

```csharp
public abstract class TypeInfo
{
    bool IsCompatibleWith(TypeInfo other);   // 子类型兼容性
    bool IsStrictlyEqual(TypeInfo other);    // BIL 严格相等
    bool IsValueType, IsObjectType;
    bool IsShared, IsRich;                   // Latte 特有
}

├── PrimitiveTypeInfo           // i8, i32, f64, bool, etc.
├── ClassTypeInfo               // class (可以是 shared)
├── StructTypeInfo              // struct (可以是 rich/shared)
├── FunctionTypeInfo            // 函数类型
├── ErrorTypeInfo               // 错误恢复用
└── UnknownTypeInfo             // 类型推断用
```

#### 4.5 异常类
```csharp
public class SemanticException : Exception { ... }
public class TypeCheckException : SemanticException { ... }
```

## 编译管道

```
Source Code (.latte)
    ↓
[Lexer] 
    - 使用 CharPosition 跟踪位置
    - 输出 List<Token> (WordToken, StringToken 等)
    ↓
[Parser]
    - 使用 CharRange 跟踪范围
    - 使用 IParserLayer 分层解析
    - 输出 ASTNode 树 (RootASTNode, SymbolASTNode 等)
    ↓
[符号表构建器] （待实现）
    - 遍历 AST
    - 构建 SymbolTable
    - 为每个声明创建 SymbolInfo
    ↓
[类型检查器] （待实现）
    - 使用 SymbolTable 解析类型引用
    - 为每个表达式标注 TypeInfo
    - 检查类型兼容性
    - 验证 rich/shared 闭包规则
    ↓
[语义验证器] （待实现）
    - 访问控制检查
    - async 边界检查
    - 其他语义规则
    ↓
[BIL 生成器] （待实现）
    - AST + TypeInfo → BIL
```

## 关键设计决策

### 1. 双轨符号系统
- **AST 阶段的 Symbol（Utilities.cs）**：表示源码中出现的符号引用，尚未解析
- **语义阶段的 SymbolInfo（FrontendTypesExtension.cs）**：表示已解析的符号定义，包含完整类型信息

### 2. 保留旧系统
保留了 Utilities.cs 中的原有类型定义，新增的类型作为扩展添加，避免破坏现有代码。

### 3. 类型检查双模式
TypeInfo 提供两种比较方法：
- `IsCompatibleWith` - 用于源码级别的类型检查（允许子类型）
- `IsStrictlyEqual` - 用于 BIL 生成（BIL 要求严格类型相等）

### 4. Rich/Shared 原生支持
在类型系统层面直接支持 Latte 的 `rich` 和 `shared` 修饰符：
- `ClassTypeInfo.IsShared` - 类是否可跨协程共享
- `StructTypeInfo.IsRich` - 结构体是否可持有对象引用
- `StructTypeInfo.IsShared` - 结构体是否可安全进入共享图

## 下一步工作

### 已完成 ✅
- 基础 Token 系统
- 分层 Lexer 架构
- 分层 Parser 架构  
- 符号表数据结构
- 类型系统数据结构
- Parser 核心组件（字面量、类型引用、变量声明、表达式框架与后缀链、
  泛型参数列表、函数形参列表、实参列表、结果传递机制）

### 待实现 ⏳
1. **完善 Parser**
   - P2 语句系统（CodeBlock、if/while/for/return）
   - P3 类型声明、P4 函数声明

2. **符号表构建器**
   - 遍历 AST 收集所有声明
   - 构建作用域链
   - 处理导入和命名空间

3. **类型检查器**
   - 类型推断
   - 泛型具化
   - 子类型验证

4. **语义验证器**
   - Rich/shared 闭包验证
   - Async 边界检查
   - 访问控制检查

## 编译状态

✅ **编译成功** - 0 个错误，4 个警告（nullable 相关，位于 `Core/Utilities.cs`，不影响功能）

## 使用示例

```csharp
// 符号表
var symbolTable = new SymbolTable();
var varSymbol = new VariableSymbolInfo(
    "count", 
    new PrimitiveTypeInfo(PrimitiveKind.I32), 
    isConst: false, 
    range
);
symbolTable.Define(varSymbol);

// 类型检查
var i32Type = new PrimitiveTypeInfo(PrimitiveKind.I32);
var i64Type = new PrimitiveTypeInfo(PrimitiveKind.I64);
bool compatible = i32Type.IsCompatibleWith(i64Type); // false

// Rich/Shared 检查
var sharedClass = new ClassTypeInfo("User", new(), isShared: true);
bool canShare = sharedClass.IsShared; // true
```
