# Rigi Compiler Parser 实现路线图

> **版本**: 2.1  
> **创建日期**: 2026-07-17  
> **状态**: ✅ 全部完成（P0–P5 收官，M22 namespace 声明落地；进度现状见 ../legacy/PROGRESS_REPORT.md（已废弃，仅作历史编年史）；下一阶段：语义分析、BIL 输出）

> ⚠️ **阅读须知**：正文为原始计划记录，各组件的「状态」标记与代码草图反映的是
> 大扫除（M23）前的设计（如 Layer 回传结果、targetNode 回填等已被禁止的模式），
> 请勿照搬；现行架构以 `../../../AGENTS.md` §4 与 `EXPRESSION_ARCHITECTURE.md` 为准。

本文档详细描述 Rigi Parser 的完整实现路线图，包括所有需要实现的 ParserLayer 组件、它们的依赖关系、优先级以及详细的实现指导。

---

## 目录

1. [架构概览](#架构概览)
2. [实现优先级](#实现优先级)
3. [基础组件](#基础组件)
4. [表达式解析器](#表达式解析器)
5. [语句解析器](#语句解析器)
6. [类型声明解析器](#类型声明解析器)
7. [函数解析器](#函数解析器)
8. [类型系统解析器](#类型系统解析器)
9. [高级特性](#高级特性)
10. [测试策略](#测试策略)
11. [实现检查清单](#实现检查清单)

---

## 架构概览

### 当前架构

```
Parser (主解析器)
  ├── Stack<IParserLayer> layers (层栈)
  ├── Parse(List<Token> tokens) (入口方法)
  └── 循环处理每个 token，根据当前栈顶 layer 决定行为

IParserLayer (层接口)
  └── ParseToken(Token, ParserLayerContext) → ParserLayerResult
      ├── Continue (继续处理下一个 token)
      ├── PopLayer (弹出当前层)
      └── PushLayer (推入新层)

ParserLayerContext (上下文)
  ├── RaiseError(message) (抛出解析异常)
  └── 其他辅助方法
```

### 设计原则

1. **层次化**: 每个语法结构对应一个 ParserLayer
2. **状态机**: 每个 Layer 内部使用状态机处理复杂语法
3. **递归下降**: 通过 PushLayer/PopLayer 实现递归解析
4. **错误恢复**: 在适当位置提供错误恢复机制
5. **AST 构建**: 每个 Layer 负责构建对应的 AST 节点

---

## 实现优先级

### P0 - 核心基础 (必须首先实现)

这些组件是其他所有功能的基础，必须首先实现并测试通过。

#### 1. 字面量解析器 (`LiteralParserLayer.cs`)
**优先级**: P0  
**依赖**: 无  
**状态**: 未开始

**功能描述**:
解析所有类型的字面量值，包括：
- 整数字面量 (带后缀: L, S, B, U, UL, US, UB)
- 浮点数字面量 (f 后缀表示 float)
- 字符串字面量 (普通字符串和多行字符串)
- 字符字面量
- 布尔字面量 (true/false)
- null 字面量

**实现要点**:
```csharp
public class LiteralParserLayer : IParserLayer
{
    private ASTNode targetNode;  // 存储解析结果的目标节点
    
    public LiteralParserLayer(ASTNode target)
    {
        targetNode = target;
    }
    
    public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
    {
        // 1. 识别 token 类型
        // 2. 创建对应的字面量 AST 节点
        // 3. 将节点添加到 targetNode
        // 4. 返回 PopLayer 完成解析
    }
}
```

**需要创建的 AST 节点** (已有部分，需补充):
- `IntLiteralASTNode` (包含值和后缀信息)
- `FloatLiteralASTNode`
- `StringLiteralASTNode` (包含字符串插值信息)
- `CharLiteralASTNode`
- `BoolLiteralASTNode`
- `NullLiteralASTNode`

**测试用例**:
```rigi
42              // i32 默认
100L            // i64
0xFF            // 十六进制
3.14            // double 默认
0.1f            // float
"Hello"         // 字符串
"Hello ${x}"    // 字符串插值
true            // 布尔
null            // null
```

---

#### 2. 类型引用解析器 (`TypeReferenceParserLayer.cs`)
**优先级**: P0  
**依赖**: PathParserLayer (已实现)  
**状态**: 未开始

**功能描述**:
解析类型引用，包括：
- 基本类型 (i32, i64, String 等)
- 用户定义类型
- 泛型类型 `Container\<T>`
- 可空类型 `T?`
- 数组类型 (通过 Span\<T> 或 Array\<T>)
- ~~rich/shared 修饰符~~（已作废：rich/shared 是类型声明修饰符，不是类型引用，由 DeclarationParserLayer 处理，见 SYNTAX §3.1.1）

**实现要点**:
```csharp
public class TypeReferenceParserLayer : IParserLayer
{
    private enum ParserStage
    {
        BaseType,           // 解析基础类型名
        GenericArgs,        // 解析泛型参数
        Nullable,           // 处理 ? 后缀
        RichShared          // 处理 rich/shared 修饰符
    }
    
    private TypeASTNode targetNode;
    private ParserStage stage = ParserStage.BaseType;
    
    // 状态机实现...
}
```

**需要创建的 AST 节点**:
- `TypeReferenceASTNode` (基类)
  - `PrimitiveTypeNode`
  - `NamedTypeNode`
  - `GenericTypeNode`
  - `NullableTypeNode`

**测试用例**:
```rigi
i32                    // 基本类型
String                 // 对象类型
Container\<i32>        // 泛型类型
String?                // 可空类型
// 以下两条已作废：rich/shared 是类型声明修饰符（SYNTAX §3.1.1），
// 不属于类型引用，由 DeclarationParserLayer 处理与校验
```


---

### P1 - 表达式系统 (次优先)

#### 4. 表达式解析器 (`ExpressionParserLayer.cs`)
**优先级**: P1  
**依赖**: LiteralParserLayer, TypeReferenceParserLayer, PathParserLayer  
**状态**: ✅ 已完成（2026-07-18 M8 收尾，67/67 测试通过；`:` wrapper 访问待 P5）

**功能描述**:
解析所有类型的表达式。Rigi 没有运算符优先级，必须用括号明确指定运算顺序。

**关键设计**:
```csharp
public class ExpressionParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Primary,            // 主表达式(字面量、标识符、括号表达式)
        Path,               // 路径表达式 (., ?., :)
        Binary,             // 二元运算符
        Unary,              // 一元运算符
        Call,               // 调用表达式
        Index               // 索引表达式
    }
    
    // 重要: Rigi 要求运算符必须用括号明确优先级
    // 不能有隐式优先级
}
```

**需要创建的 AST 节点**:
- `ExpressionASTNode` (基类)
  - `BinaryOpASTNode` (运算符: +, -, *, /, and, or, is, as 等)
  - `UnaryOpASTNode` (运算符: -, not, await, opposite 等)
  - `CallExprASTNode` (函数调用)
  - `IndexExprASTNode` (索引访问)
  - `MemberAccessASTNode` (成员访问)
  - `SafeMemberAccessASTNode` (安全成员访问 `?.`)
  - `WrapperAccessASTNode` (Wrapper 访问 `:`)
  - `IfExprASTNode` (if 表达式)
  - `SwitchExprASTNode` (switch 表达式)
  - `LambdaASTNode`
  - `NewExprASTNode`
  - `TypeOfExprASTNode`
  - `CastExprASTNode` (as/as?)

**测试用例**:
```rigi
// 字面量
42
"hello"

// 路径表达式
obj.field
obj?.method()
obj:Wrapper

// 运算符 (必须用括号)
(a + b)
((a + b) * c)
(a + (b * c))

// 错误: 没有括号指定优先级
a + b * c        // 编译错误!

// 调用和索引
func(arg1, arg2)
array[0]
obj.method(x)

// Lambda
func{(x: i32): i32 -> (x + 1)}

// if 表达式
if (x > 0) { x } else { opposite(x) }

// switch 表达式
switch(value) {
    (1) -> { "one" }
    (2) -> { "two" }
    default -> { "other" }
}

// 类型操作
new User(id=42)
typeOf(obj)
obj as String
obj as? String
obj is String
```

---

#### 5. 参数列表解析器 (`ParameterListParserLayer.cs`)
**优先级**: P1  
**依赖**: TypeReferenceParserLayer, ExpressionParserLayer  
**状态**: ✅ 已完成（2026-07-17，14/14 测试通过）

**功能描述**:
解析函数参数列表，包括：
- 普通参数
- 具名参数
- 默认参数
- 可变参数 (...)
- 具名可变参数 (named ...)

**实现要点**:
```csharp
public class ParameterListParserLayer : IParserLayer
{
    private enum ParserStage
    {
        ParameterStart,     // 参数开始
        ParameterName,      // 参数名
        TypeAnnotation,     // 类型标注
        DefaultValue,       // 默认值
        Varargs,            // 可变参数
        NamedVarargs        // 具名可变参数
    }
}
```

**测试用例**:
```rigi
// 普通参数
func add(a: i32, b: i32): i32

// 默认参数
func greet(name: String = "World"): String

// 可变参数
func sum(numbers: i32...): i32

// 具名可变参数
func config(options: named String...): Config

// 泛型可变参数
func update\<named TValues... with Serializable>(configs: named TValues...): bool
```


---

### P2 - 语句系统

#### 6. 代码块解析器 (`CodeBlockParserLayer.cs`)
**优先级**: P2  
**依赖**: 所有语句解析器  
**状态**: ✅ 已完成（2026-07-18 M7 重写，27/27 测试通过；return/break/continue/throw/yield 为内联子状态）

**功能描述**:
解析 `{ }` 代码块，包含多个语句。

**实现要点**:
```csharp
public class CodeBlockParserLayer : IParserLayer
{
    private BlockASTNode blockNode;
    private bool expectClosingBrace = false;
    
    public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
    {
        // 1. 遇到 { 开始块
        // 2. 循环解析语句直到 }
        // 3. 每个语句可能是:
        //    - 变量声明
        //    - 表达式语句
        //    - 控制流语句 (if/while/for...)
        //    - return/throw/yield/await
    }
}
```

---

#### 7. If 语句解析器 (`IfStatementParserLayer.cs`)
**优先级**: P2  
**依赖**: ExpressionParserLayer, CodeBlockParserLayer  
**状态**: ✅ 已完成（2026-07-18 M7/M8，语句 + 表达式双模式，else if 链，测试含于各套件）

**功能描述**:
解析 if/else 语句和 if 表达式。

**实现要点**:
```csharp
public class IfStatementParserLayer : IParserLayer
{
    private enum ParserStage
    {
        If,                 // if 关键字
        Condition,          // 条件表达式
        ThenBlock,          // then 分支
        ElseKeyword,        // else 关键字
        ElseBlock           // else 分支
    }
    
    private bool isExpression;  // 是语句还是表达式
}
```

**测试用例**:
```rigi
// if 语句
if (x > 0) {
    print(x)
}

// if-else 语句
if (x > 0) {
    print("positive")
} else {
    print("non-positive")
}

// if 表达式 (必须有 else)
var result = if (x > 0) { x } else { opposite(x) }
```

---

#### 8. Switch 语句解析器 (`SwitchStatementParserLayer.cs`)
**优先级**: P2  
**依赖**: ExpressionParserLayer, CodeBlockParserLayer  
**状态**: ✅ 表达式模式已完成（2026-07-18 M8，6/6 测试通过）；语句模式已完成（M33，SYNTAX §7.2 语句形态，分支体为代码块）

**功能描述**:
解析 switch 表达式，支持值匹配和模式匹配。

**实现要点**:
```csharp
public class SwitchStatementParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Switch,             // switch 关键字
        Selector,           // 选择器表达式
        OpenBrace,          // {
        Cases,              // case 分支
        Default,            // default 分支
        CloseBrace          // }
    }
    
    private bool isExpression;
}
```

**测试用例**:
```rigi
var result = switch(expr) {
    (1) -> { "one" }                   // 值匹配
    (2) -> { "two" }
    (_ > 10) -> { "big" }              // 模式匹配: _ 代表值
    (_ == (3 + 4)) -> { "seven" }
    default -> { "other" }
}
```

---

#### 9. 循环解析器 (`LoopParserLayer.cs`)
**优先级**: P2  
**依赖**: ExpressionParserLayer, CodeBlockParserLayer  
**状态**: ✅ 已完成（2026-07-18 M7，15/15 测试通过）

**功能描述**:
解析所有类型的循环：
- for-each: `for (item in collection)`
- 范围循环: `for (i in 0 to 10)`
- while: `while (condition)`
- do-while: `do { } while (condition)`

**实现要点**:
```csharp
public class LoopParserLayer : IParserLayer
{
    private enum LoopType
    {
        ForEach,            // for (item in collection)
        ForRange,           // for (i in 0 to 10)
        While,              // while
        DoWhile             // do-while
    }
    
    private enum ParserStage
    {
        Keyword,            // for/while/do
        LoopVariable,       // 循环变量
        InKeyword,          // in 关键字
        Collection,         // 集合/范围
        Condition,          // 条件
        Body,               // 循环体
        WhileKeyword,       // do-while 的 while
        Label               // named 标签
    }
}
```

**测试用例**:
```rigi
// for-each
for (item in collection) {
    print(item)
}

// 范围循环
for (i in 0 to 10) {
    print(i)
}

// while
while (condition) {
    doSomething()
}

// do-while
do {
    doSomething()
} while (condition)

// 带标签
for (i in 0 to 10) named outer {
    for (j in 0 to 10) named inner {
        if (someCondition) {
            break@outer
        }
    }
}
```


---

#### 10. Try-Catch-Finally 解析器 (`TryCatchFinallyParserLayer.cs`)
**优先级**: P2  
**依赖**: CodeBlockParserLayer, TypeReferenceParserLayer  
**状态**: ✅ 已完成（2026-07-26 M9，9/9 测试通过；多 catch、finally(e)、嵌套 try）

**功能描述**:
解析异常处理结构。

**实现要点**:
```csharp
public class TryCatchFinallyParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Try,                // try 关键字
        TryBlock,           // try 块
        CatchKeyword,       // catch 关键字
        CatchType,          // 异常类型
        CatchVariable,      // 异常变量
        CatchBlock,         // catch 块
        FinallyKeyword,     // finally 关键字
        FinallyParameter,   // finally(e) 参数
        FinallyBlock        // finally 块
    }
}
```

**测试用例**:
```rigi
try {
    riskyOperation()
} catch (e: IOException) {
    handleIO(e)
} catch (_: RuntimeException) {
    // 丢弃异常变量
} finally(e) {
    // e 为异常或 null
    cleanup()
}
```

---

#### 11. Seq 块解析器 (`SeqBlockParserLayer.cs`)
**优先级**: P2  
**依赖**: CodeBlockParserLayer, VariableDeclarationParserLayer  
**状态**: ✅ 已完成（2026-07-26 M10，17/17 测试通过；语句 + 表达式双形态）

**功能描述**:
解析 seq 块，包括：
- 作用域块
- 带 using 的资源管理
- 带 named 的标签
- 带返回值的 seq 表达式

**实现要点**:
```csharp
public class SeqBlockParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Seq,                // seq 关键字
        Using,              // using 子句
        Named,              // named 标签
        Modifiers,          // volatile 等修饰符
        Block               // 代码块
    }
    
    private bool isExpression;      // 是否作为表达式使用
    private List<UsingBinding> usingBindings = new();
}
```

**测试用例**:
```rigi
// 简单作用域
seq {
    var temp = computeSomething()
    if (temp > 100) {
        return temp
    }
}

// 带 using
seq using(const file = new File("./mydoc"))
using(const stream = new FileInputStream(file))
{
    process(stream)
}

// 带 named
seq named myBlock {
    // ...
    return@myBlock value
}

// 作为表达式
const result = seq {
    const x = compute()
    return@_ (x * 2)
}

// volatile
volatile seq {
    // 操作保持源码顺序
}
```

---

#### 12. 协程操作解析器 (`CoroutineOpsParserLayer.cs`)
**优先级**: P2  
**依赖**: ExpressionParserLayer  
**状态**: ✅ 已完成（2026-07-26 M12，13/13 测试通过）——**未新建独立 Layer**：`await` 作为一元前缀运算符由 ExpressionParserLayer 处理，`yield` 作为语句由 CodeBlockParserLayer 内联处理（简洁优先原则的典型案例）

**功能描述**:
解析协程相关操作：
- `await` 表达式
- `yield` 语句
- `async` 修饰符

**实现要点**:
```csharp
public class CoroutineOpsParserLayer : IParserLayer
{
    private enum OperationType
    {
        Await,              // await 表达式
        Yield,              // yield 语句
        YieldWithAlarm      // yield alarm 语句
    }
}
```

**测试用例**:
```rigi
// await 表达式
await flushLogs()
const user = await loadUser(42)

// yield 语句
yield
yield pollingAlarm
yield eventAlarm
yield sleep(1000)

// async 函数
pub async func loadUser(id: i32): SharedUser {
    const response = await requestUser(id)
    return response.user
}
```


---

### P3 - 类型声明系统

> **⚠️ 计划修订（2026-07-26，M13/M14/M15）**：原计划为每种类型声明各建一个 Layer（#13–16），
> 实际依据 SYNTAX.md §14.8（canonical symbol 类名段可为空、`.static.` 只是标记位）改为
> **统一 `DeclarationParserLayer`**：全局/成员/嵌套任何声明共用一套状态机，
> func/operator/init 共用 `CallableDeclarationASTNode`。类型声明头部、成员（字段/方法/init/operator）、
> 继承与 implements、嵌套类型、声明泛型参数（M15）均已落地（TypeDeclaration 77/77）。
> 以下 #13–16 的「状态」按此修订标注，剩余工作并入各条目。

#### 13. Class 声明解析器 (`ClassDeclarationParserLayer.cs`)
**优先级**: P3  
**依赖**: DeclarationParserLayer (部分完成), TypeReferenceParserLayer, FunctionParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 落地（M13/M14，声明泛型参数 M15，`like` 委托 M19）；不再计划独立 Layer

**功能描述**:
解析类声明，包括：
- 修饰符 (pub/priv/open/abstract/singleton/shared)
- 继承 `: BaseClass`
- 接口实现 `implements Interface1, Interface2`
- 委托 `like field`
- 类体 (字段、方法、构造函数)

**实现要点**:
```csharp
public class ClassDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv/open/abstract/singleton/shared
        Class,              // class 关键字
        ClassName,          // 类名
        GenericParams,      // 泛型参数 <T>
        Colon,              // : 继承
        BaseClass,          // 基类
        Implements,         // implements 关键字
        Interfaces,         // 接口列表
        Like,               // like 委托
        ClassBody           // 类体
    }
}
```

**测试用例**:
```rigi
// 简单类
pub class User {
    pub var name: String
    pub var age: i32
    
    pub init(_ -> name, _ -> age)
}

// 带继承和接口
pub open class Dog : Animal implements Comparable {
    override func speak(): String {
        return "Woof!"
    }
}

// shared 类
pub shared class SharedSession {
    pub var owner: SharedUser
    pub var cursor: Point
}

// singleton
pub singleton class Config {
    pub const apiKey: String = "..."
}

// 泛型类
pub class Container\<TElement> {
    priv var items: Array\<TElement>
}

// 委托
pub class Apple : Fruit like pear {
    pub var pear: Pear = Pear()
}
```

---

#### 14. Struct 声明解析器 (`StructDeclarationParserLayer.cs`)
**优先级**: P3  
**依赖**: 类似 ClassDeclarationParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 落地（M13/M14，含 rich/shared 修饰符与继承；声明泛型参数 M15）

**功能描述**:
解析 struct 声明，包括 rich/shared 修饰符。

**实现要点**:
```csharp
public class StructDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv/open/rich/shared
        Struct,             // struct 关键字
        StructName,         // 结构体名
        GenericParams,      // 泛型参数
        Colon,              // : 继承
        BaseStruct,         // 基类 struct (仅 struct)
        StructBody          // 结构体体
    }
}
```

**测试用例**:
```rigi
// 普通 struct (不能持有 Object)
pub struct Vector2 {
    pub var x: float
    pub var y: float
    
    pub operator plus(another: Vector2): Vector2 {
        return Vector2(x=(this.x + another.x), y=(this.y + another.y))
    }
}

// rich struct (可持有 Object)
pub rich struct Entry {
    pub var owner: User
    pub var metadata: Metadata
}

// shared rich struct (可进入共享图)
pub shared rich struct SharedEntry {
    pub var owner: SharedUser
    pub var location: Vector2
}

// 带继承（open 仅 class 与 rich struct 可用；非 rich struct 不得 open/abstract。
//         rich 单向传染：基类 rich ⇒ 子类必须 rich）
pub open rich struct Point {
    pub var owner: User
}
pub rich struct Point3D : Point {
    pub var z: float
}
```

---

#### 15. Interface 声明解析器 (`InterfaceDeclarationParserLayer.cs`)
**优先级**: P3  
**依赖**: TypeReferenceParserLayer, FunctionParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 落地（M13/M14；`: Base` 按 SYNTAX §11 落 BaseInterfaces，带体方法即可解析的默认实现；声明泛型参数 M15）

**功能描述**:
解析接口声明，包括默认实现。

**实现要点**:
```csharp
public class InterfaceDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv
        Interface,          // interface 关键字
        InterfaceName,      // 接口名
        GenericParams,      // 泛型参数
        Colon,              // : 继承
        BaseInterfaces,     // 基接口
        InterfaceBody       // 接口体
    }
}
```

**测试用例**:
```rigi
pub interface Drawable {
    func draw(canvas: Canvas)
    
    // 默认实现
    func debugDraw(canvas: Canvas) {
        draw(canvas)
    }
}

pub interface Comparable\<T> {
    func compareTo(other: T): i32
}
```


---

#### 16. Enum Struct 声明解析器 (`EnumStructDeclarationParserLayer.cs`)
**优先级**: P3  
**依赖**: StructDeclarationParserLayer, InitDeclarationParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 落地（M13/M14 头部与体；M17 补齐 `[]` case 列表：固定 case / 参数化 case / 显式判别值，含唯一性与混用校验）

**功能描述**:
解析 enum struct 声明，包括：
- 字段和方法
- init 构造函数
- `[]` 内的 case 列表
- 固定 case 和参数化 case

**实现要点**:
```csharp
public class EnumStructDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv
        Enum,               // enum 关键字
        Struct,             // struct 关键字
        EnumName,           // enum 名
        EnumBody,           // enum 体 (字段、方法、init)
        CaseList            // [] 内的 case 列表
    }
}
```

**测试用例**:
```rigi
pub enum struct Direction {
    pub const degrees: i32
    
    priv init(_ -> degrees)
    
    pub func opposite(): Direction {
        return switch(this) {
            (_ is .North) -> { .South }
            (_ is .South) -> { .North }
            default -> { this }
        }
    }
}[
    North(0),
    South(180),
    East(90),
    West(270)
]

// 带参数洞的 case
pub enum struct RequestResult {
    pub const errorCode: i32
    
    pub init(_ -> errorCode)
}[
    Success(-1),
    Failed(errorCode = _)
]

// 使用
const success: RequestResult = .Success
const failed: RequestResult = .Failed(404)
```

---

### P4 - 函数和成员系统

> **⚠️ 计划修订（2026-07-26，M14–M19）**：func/operator/init 的基本声明（修饰符、名称、形参列表、
> 返回类型、函数体）已由统一 `DeclarationParserLayer` 一条路径覆盖，不再计划 #17–19 的独立 Layer；
> 声明泛型参数（M15）、init 参数映射（M18）与 `ext` 扩展成员（M19）亦已接入。
> **#17–19 全部完成。**

#### 17. 函数声明解析器 (`FunctionDeclarationParserLayer.cs`)
**优先级**: P4  
**依赖**: TypeReferenceParserLayer, ParameterListParserLayer, CodeBlockParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 覆盖（M14 基本形态；声明泛型参数 M15；`ext` 扩展函数 M19）

**功能描述**:
解析函数声明，包括：
- 修饰符 (pub/priv/static/ext/override/abstract/async)
- 函数名
- 泛型参数
- 参数列表
- 返回类型
- 函数体

**实现要点**:
```csharp
public class FunctionDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv/static/ext/override/abstract/async
        Func,               // func 关键字
        FunctionName,       // 函数名
        GenericParams,      // 泛型参数 <T, U>
        OpenParen,          // (
        Parameters,         // 参数列表
        CloseParen,         // )
        Colon,              // : 返回类型
        ReturnType,         // 返回类型
        FunctionBody        // 函数体 { }
    }
}
```

**测试用例**:
```rigi
// 简单函数
pub func add(a: i32, b: i32): i32 {
    return (a + b)
}

// 带默认参数
pub func greet(name: String = "World"): String {
    return "Hello ${name}"
}

// 泛型函数
pub func transform\<TInput, TResult>(input: TInput): TResult {
    // ...
}

// 带约束
pub func process\<TItem extends Comparable, Serializable supers BaseType>(item: TItem): TItem {
    // ...
}

// async 函数
pub async func loadUser(id: i32): SharedUser {
    const response = await requestUser(id)
    return response.user
}

// 扩展函数
pub ext func String.reversed(): String {
    // ...
}

// 可变参数
pub func sum(numbers: i32...): i32 {
    // ...
}

// 具名可变参数
pub func config(options: named String...): Config {
    // ...
}
```

---

#### 18. 构造函数解析器 (`InitDeclarationParserLayer.cs`)
**优先级**: P4  
**依赖**: ParameterListParserLayer, CodeBlockParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 覆盖（M14 CallableKind.Init + M18 `_ -> field` 参数映射：同名/显式名/默认值/混合形态，ParameterListParserLayer allowMapping 开关）

**功能描述**:
解析 init 构造函数，特别支持参数映射语法。

**实现要点**:
```csharp
public class InitDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv
        Init,               // init 关键字
        OpenParen,          // (
        Parameters,         // 参数列表 (包含 -> 映射)
        CloseParen,         // )
        InitBody            // init 体
    }
    
    // 特别处理: _ -> fieldName 映射语法
}
```

**测试用例**:
```rigi
pub class Point {
    pub var x: i32
    pub var y: i32
    
    // 参数直接映射 (_ 表示参数名与字段名相同)
    pub init(_ -> x, _ -> y)
    
    // 带默认值
    pub init(_ -> x = 0, _ -> y = 0)
    
    // 显式参数名
    pub init(horizontal: i32 -> x, vertical: i32 -> y)
    
    // 混合：映射参数 + 普通参数
    pub init(_ -> x, _ -> y, label: String) {
        // label 不映射到字段
    }
}
```

---

#### 19. 运算符重载解析器 (`OperatorDeclarationParserLayer.cs`)
**优先级**: P4  
**依赖**: FunctionDeclarationParserLayer  
**状态**: ⏳ 基本 operator 声明已由统一 DeclarationParserLayer 覆盖（M14，CallableKind.Operator，含形参与返回类型）

**功能描述**:
解析运算符重载声明。

**实现要点**:
```csharp
public class OperatorDeclarationParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // pub/priv
        Operator,           // operator 关键字
        OperatorName,       // 运算符名 (plus, minus, times 等)
        Parameters,         // 参数列表
        ReturnType,         // 返回类型
        OperatorBody        // 运算符体
    }
}
```

**测试用例**:
```rigi
pub struct Vector2 {
    pub operator plus(another: Vector2): Vector2 {
        return Vector2(
            x=(this.x + another.x),
            y=(this.y + another.y)
        )
    }
    
    pub operator opposite(): Vector2 {
        return Vector2(x=(opposite(this.x)), y=(opposite(this.y)))
    }
}
```


---

### P5 - Wrapper 系统

#### 20. Wrapper 声明解析器 (`WrapperDeclarationParserLayer.cs`)
**优先级**: P5  
**依赖**: ClassDeclarationParserLayer  
**状态**: ✅ 已由统一 DeclarationParserLayer 落地（M13/M14 头部；M20 主体）：wrapper 类型标识为 `@WrapperTarget(.Entity/.Value/.Method)` 注解（以 SYNTAX §14.2–14.4 为准，下方测试用例中的 `wrapper X entity` 后缀写法过时）、`.proxy.*` 代理成员（specific + 四类 wildcard、同类 wildcard 唯一性校验）；未新建独立 Layer

**功能描述**:
解析 Wrapper 声明，包括：
- Entity Wrapper
- Method Wrapper
- Value Wrapper
- Proxy 方法 (specific 和 wildcard)

**实现要点**:
```csharp
public class WrapperDeclarationParserLayer : IParserLayer
{
    private enum WrapperType
    {
        Entity,             // 实体 wrapper
        Method,             // 方法 wrapper
        Value               // 值 wrapper
    }
    
    private enum ParserStage
    {
        Modifiers,          // pub/priv
        Wrapper,            // wrapper 关键字
        WrapperName,        // wrapper 名
        WrapperType,        // 类型标识
        WrapperBody         // wrapper 体
    }
}
```

**测试用例**:
```rigi
// Entity Wrapper
pub wrapper Logged entity {
    // Specific proxy
    pub operator .proxy.log(msg: String) {
        logToFile(msg)
        inner.log(msg)
    }
    
    // Wildcard proxy
    pub operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(
        symbol: String,
        namedArgs: named TNamedArgs...,
        unnamedArgs: TUnnamedArgs...
    ): TReturn {
        logCall(symbol)
        return inner.call???(symbol, namedArgs, unnamedArgs)
    }
}

// Method Wrapper
pub wrapper Memoized method\<TInput, TResult> {
    priv var cache: Map\<TInput, TResult> = Map()
    
    pub func wrap(input: TInput): TResult {
        if (cache.contains(input)) {
            return cache[input]
        }
        const result = inner(input)
        cache[input] = result
        return result
    }
}

// Value Wrapper
pub wrapper Validated value\<T> {
    pub func get(): T {
        validate(inner)
        return inner
    }
    
    pub func set(value: T) {
        validate(value)
        inner = value
    }
}
```

---

### P6 - 高级特性

#### 21. Lambda 表达式解析器 (`LambdaExpressionParserLayer.cs`)
**优先级**: P6  
**依赖**: ParameterListParserLayer, ExpressionParserLayer, CodeBlockParserLayer  
**状态**: ✅ 已完成（2026-07-18 M8 提前落地，15/15 测试通过；完整/泛型/async/trailing，体为单表达式）

**功能描述**:
解析 lambda 表达式，包括：
- 完整形式: `func{(params): ReturnType -> body}`
- 带泛型: `func{(x: T)\<T>: T -> ...}`
- async lambda
- trailing lambda

**实现要点**:
```csharp
public class LambdaExpressionParserLayer : IParserLayer
{
    private enum ParserStage
    {
        Modifiers,          // async/pub
        Func,               // func 关键字
        OpenBrace,          // {
        OpenParen,          // (
        Parameters,         // 参数列表
        CloseParen,         // )
        GenericParams,      // <T, U>
        Colon,              // :
        ReturnType,         // 返回类型
        Arrow,              // ->
        Body,               // lambda 体
        CloseBrace          // }
    }
}
```

**测试用例**:
```rigi
// 简单 lambda
func{(x: i32): i32 -> (x + 1)}

// 带泛型
func{(width: TSize, height: TSize)\<TSize extends Size>: TSize -> 
    compute(width, height)
}

// async lambda
const loader = async func{(id: i32): SharedUser -> loadUserNow(id)}

// trailing lambda
list.map{(item: String): i32 -> item.length}
```

---

#### 22. 泛型参数解析器 (`GenericParametersParserLayer.cs`)
**优先级**: P6  
**依赖**: TypeReferenceParserLayer  
**状态**: ✅ 已完成（2026-07-17，语法已迁移至 `\<...>`，21/21 测试通过）

**功能描述**:
解析泛型参数声明和约束。

**实现要点**（实际实现）:
```csharp
public class GenericParametersParserLayer : IParserLayer
{
    private enum State
    {
        Initial,            // 等待 \
        BackslashSeen,      // 等待 <
        ClauseStart,        // 子句开头（标识符 / out / in / named）
        VarianceNameExpected, NamedNameExpected,
        PrefixParamSeen,    // 前缀路径参数名已读
        Dots1, Dots2,       // 前缀路径 ... 计数
        DotsAwaitThird,     // TypeRef 路径等待第三个 .
        VariadicDone,       // ... 已读完
        TargetParsed,       // 子句开头类型已解析（TypeRef 委托）
        BoundParsed,        // 约束 Bound 已解析
        Completed
    }
}
```

**说明**：子句中的类型（约束 Target/Bound、参数名候选）委托
TypeReferenceParserLayer 解析；`out`/`in`/`named` 前缀由本层直接消费。
AST 见 `AST/DeclarationNodes.cs`（GenericParameterListASTNode /
GenericParameterASTNode / GenericConstraintASTNode）。

**测试用例**:
```rigi
// 简单泛型
class Container\<TElement>

// 多个泛型参数
func transform\<TInput, TResult>(input: TInput): TResult

// 约束 (extends 和 supers 位置可交换)
func process\<TItem extends Comparable, Serializable supers BaseType>(item: TItem)

// with 约束
func dump\<TItem with Serializable>(item: TItem)

// 型变
class Producer\<out TElement>
class Consumer\<in TElement>

// 可变泛型参数
func update\<named TValues... with Serializable>(configs: named TValues...)
```

---

#### 23. 属性访问器解析器 (`PropertyAccessorParserLayer.cs`)
**优先级**: P6  
**依赖**: CodeBlockParserLayer  
**状态**: ✅ 已完成（2026-07-26 M16，17/17 测试通过；提前落地——三类定义位置经 VariableDeclarationParserLayer 统一接入，含 backing field 判定与 get/set 一致性校验）

**功能描述**:
解析 getter/setter 定义。

**实现要点**:
```csharp
public class PropertyAccessorParserLayer : IParserLayer
{
    private enum AccessorType
    {
        Getter,
        Setter
    }
    
    private enum ParserStage
    {
        Modifiers,          // pub/priv
        GetOrSet,           // get 或 set
        OpenParen,          // (
        ValueParameter,     // value 参数 (value: _ 或 _)
        CloseParen,         // )
        AccessorBody        // { } 或空 (编译器生成)
    }
}
```

**测试用例**:
```rigi
var width: i32 {
    pub get(value: _) {
        return value
    }
    priv set(value: _) {
        // ...
    }
} = 100

// 只定义访问控制
var height: i32 {
    pub get
    priv set
} = 200

// 计算属性 (无 backing field)
var area: i32 {
    get(_: _) {
        return (width * height)
    }
}

// 栈上变量的 getter/setter
pub func example() {
    var localCounter: i32 {
        get(value: _) { return value }
        set(value: _) { log("set to ${value}") }
    } = 0
}

// 扩展字段
pub ext var String.isEmpty: bool {
    get(_: _) { return (this.length == 0) }
}
```


---

## 测试策略

> ⚠️ 本节示例代码为 MSTest 风格（[TestClass]/Assert）的原始计划草图，已作废：
> 项目实际不使用任何测试框架，测试基建为自研控制台测试——`Tests/` 下静态类
> （每类 `public static int RunAll()`）由 `Tests/TestRunner.cs` 统一驱动，
> 断言走 `Tests/TestHarness.cs` 与 `Tests/AstDescribe.cs`。详见 `../../../AGENTS.md` §5。

### 单元测试

每个 ParserLayer 都应该有对应的单元测试：

```csharp
[TestClass]
public class LiteralParserLayerTests
{
    [TestMethod]
    public void TestIntLiteral()
    {
        var tokens = Lexer.Tokenize("42");
        var ast = Parser.Parse(tokens);
        Assert.IsInstanceOfType(ast, typeof(IntLiteralASTNode));
        Assert.AreEqual(42, ((IntLiteralASTNode)ast).Value);
    }
    
    [TestMethod]
    public void TestIntLiteralWithSuffix()
    {
        var tokens = Lexer.Tokenize("100L");
        var ast = Parser.Parse(tokens);
        Assert.IsInstanceOfType(ast, typeof(IntLiteralASTNode));
        Assert.AreEqual(IntType.I64, ((IntLiteralASTNode)ast).IntType);
    }
}
```

### 集成测试

测试完整的语法结构：

```csharp
[TestClass]
public class IntegrationTests
{
    [TestMethod]
    public void TestSimpleFunction()
    {
        var code = @"
            pub func add(a: i32, b: i32): i32 {
                return (a + b)
            }
        ";
        var tokens = Lexer.Tokenize(code);
        var ast = Parser.Parse(tokens);
        // 验证 AST 结构
    }
    
    [TestMethod]
    public void TestComplexClass()
    {
        var code = @"
            pub shared class User {
                pub var name: String
                pub var age: i32
                
                pub init(_ -> name, _ -> age)
                
                pub func greet(): String {
                    return ""Hello, ${this.name}""
                }
            }
        ";
        var tokens = Lexer.Tokenize(code);
        var ast = Parser.Parse(tokens);
        // 验证 AST 结构
    }
}
```

### 错误恢复测试

测试错误情况和错误恢复：

```csharp
[TestClass]
public class ErrorHandlingTests
{
    [TestMethod]
    [ExpectedException(typeof(ParserException))]
    public void TestMissingClosingBrace()
    {
        var code = "func test() { var x = 42";
        var tokens = Lexer.Tokenize(code);
        Parser.Parse(tokens);
    }
    
    [TestMethod]
    public void TestErrorRecovery()
    {
        var code = @"
            func test() {
                var x = 42 // 错误：缺少类型
                var y: i32 = 0 // 应该继续解析
            }
        ";
        // 测试是否能恢复并继续解析
    }
}
```

### 性能测试

测试大文件解析性能：

```csharp
[TestClass]
public class PerformanceTests
{
    [TestMethod]
    public void TestLargeFile()
    {
        var code = GenerateLargeFile(10000); // 生成 10000 行代码
        var stopwatch = Stopwatch.StartNew();
        var tokens = Lexer.Tokenize(code);
        var ast = Parser.Parse(tokens);
        stopwatch.Stop();
        Assert.IsTrue(stopwatch.ElapsedMilliseconds < 5000); // 应在 5 秒内完成
    }
}
```

---

## 实现检查清单

使用这个清单跟踪实现进度：

### P0 - 核心基础
- [x] 1. LiteralParserLayer
  - [x] 整数字面量
  - [x] 浮点数字面量
  - [x] 字符串字面量 (包括插值)
  - [x] 字符字面量（M34：CharLexerLayer + CharToken + CharLiteralASTNode）
  - [x] 布尔字面量
  - [x] null 字面量
  - [x] 单元测试（15/15）
  
- [x] 2. TypeReferenceParserLayer
  - [x] 基本类型
  - [x] 用户定义类型
  - [x] 泛型类型（已迁移 `\<` 语法，含嵌套）
  - [x] 可空类型
  - [x] 不处理 rich/shared 修饰符（类型声明修饰符，由 DeclarationParserLayer 处理，见 SYNTAX §3.1.1）
  - [x] 单元测试（3/3 + 集成于变量声明测试）
  
- [x] 3. VariableDeclarationParserLayer
  - [x] var/const 识别
  - [x] 类型标注
  - [x] 初始化表达式（经结果传递机制保存 Initializer）
  - [x] getter/setter（M16，PropertyAccessorParserLayer，见 P6 23 号）
  - [x] 单元测试（10/10）

### P1 - 表达式系统
- [x] 4. ExpressionParserLayer
  - [x] 字面量表达式
  - [x] 路径表达式（符号路径 + 调用/索引/成员/安全访问后缀链；`:` wrapper 访问待 P5）
  - [x] 二元运算符（含无优先级规则强制、> 系列重组 >=/>>/>>>）
  - [x] 一元运算符（含连续一元限制；await 前缀 M12）
  - [x] 调用表达式（位置/具名实参）
  - [x] 索引表达式
  - [x] Lambda 表达式（M8，见 #21）
  - [x] if 表达式（M8，强制 else）
  - [x] switch 表达式（M8，强制 default）
  - [x] 类型操作 (new/typeOf/as/is/as?/supers/with)（M8，is/as 系列为专用 AST 节点）
  - [x] seq 表达式形态（M10，return@_/return@label；匿名默认标签 seq→_ 见 M33）
  - [x] 单元测试（67/67）
  
- [x] 5. ParameterListParserLayer
  - [x] 普通参数
  - [x] 具名参数（具名可变 named T...）
  - [x] 默认参数
  - [x] 可变参数
  - [x] 具名可变参数
  - [x] 单元测试（14/14）

### P2 - 语句系统
- [x] 6. CodeBlockParserLayer（M7 重写）
  - [x] 代码块解析
  - [x] 语句识别和分发（return/break/continue 内联子状态，throw/yield 后续同样内联）
  - [x] 赋值语句（表达式语句后跟 `=`）
  - [x] 单元测试（27/27）
  
- [x] 7. IfStatementParserLayer（M7 语句 + M8 表达式）
  - [x] if 语句
  - [x] if-else 语句（else 可选，else if 链）
  - [x] if 表达式（强制 else）
  - [x] 单元测试（8/8 if 套件）
  
- [x] 8. SwitchStatementParserLayer（M8 表达式模式 + M33 语句模式）
  - [x] 值匹配
  - [x] 模式匹配（`_` 引用选择器值）
  - [x] default 分支（强制）
  - [x] switch 表达式
  - [x] switch 语句模式（M33，SYNTAX §7.2 语句形态，分支体为代码块）
  - [x] 单元测试（6/6）
  
- [x] 9. LoopParserLayer（M7）
  - [x] for-each 循环
  - [x] 范围循环（`0 to 10` → RangeExpressionASTNode）
  - [x] while 循环
  - [x] do-while 循环
  - [x] 带标签循环（named）
  - [x] break/continue（含 `@标签`）
  - [x] 单元测试（15/15）
  
- [x] 10. TryCatchFinallyParserLayer（M9）
  - [x] try 块
  - [x] catch 块（多 catch、`_` 丢弃异常变量）
  - [x] finally 块（finally(e)，e 为异常或 null）
  - [x] 单元测试（9/9）
  
- [x] 11. SeqBlockParserLayer（M10）
  - [x] 简单 seq 块
  - [x] using 子句（多资源绑定）
  - [x] named 标签
  - [x] seq 表达式（return@_/return@label，节点继承 ExpressionASTNode；匿名默认标签 seq→_ 见 M33）
  - [x] volatile 修饰符
  - [x] 单元测试（17/17）
  
- [x] 12. 协程操作（M12；未建独立 Layer，见上文状态说明）
  - [x] await 表达式（ExpressionParserLayer 前缀一元运算符）
  - [x] yield 语句（CodeBlockParserLayer 内联；裸 yield / yield alarm）
  - [x] async 修饰符（M8 随 lambda/声明修饰符识别）
  - [x] throw 语句（M11，CodeBlockParserLayer 内联，10/10）
  - [x] 单元测试（13/13）

### P3 - 类型声明系统（已改为统一 DeclarationParserLayer，见上文计划修订）
- [x] 13. class 声明（统一声明层，M13/M14）
  - [x] 基本类声明
  - [x] 修饰符（pub/priv/open/abstract/singleton/shared/static/override/async）
  - [x] 继承（`: BaseClass`）
  - [x] 接口实现（implements 多接口列表）
  - [x] 委托 (like)（M19）
  - [x] 类体（字段/方法/init/operator/嵌套类型）
  - [x] 声明泛型参数（M15）
  - [x] 单元测试（TypeDeclaration 77/77）
  
- [x] 14. struct 声明（统一声明层，M13/M14）
  - [x] 基本 struct 声明
  - [x] rich struct
  - [x] shared rich struct
  - [x] 继承（`: BaseStruct`）
  - [x] 声明泛型参数（M15）
  - [x] 单元测试（含于 TypeDeclaration 77/77）
  
- [x] 15. interface 声明（统一声明层，M13/M14）
  - [x] 接口声明（`: Base` → BaseInterfaces，SYNTAX §11）
  - [x] 默认实现（带体方法可解析）
  - [x] 声明泛型参数（M15）
  - [x] 单元测试（含于 TypeDeclaration 77/77）
  
- [x] 16. enum struct 声明（统一声明层，M13/M14/M17）
  - [x] enum struct 声明头部与体
  - [x] `[]` case 列表（M17）
  - [x] 固定 case（M17）
  - [x] 参数化 case（M17，`_` 参数洞 + 具名实参）
  - [x] 单元测试（含于 TypeDeclaration 61/61）

### P4 - 函数和成员系统（基本形态已由统一声明层覆盖，见上文计划修订）
- [~] 17. 函数声明（统一声明层，M14）
  - [x] 基本函数（修饰符/形参/返回类型/函数体/无体声明）
  - [x] 泛型函数（声明泛型参数 M15）
  - [x] async 函数（async 修饰符识别）
  - [x] 扩展函数（ext，M19）
  - [x] 可变参数函数（复用 ParameterListParserLayer）
  - [x] 单元测试（含于 TypeDeclaration 77/77）
  
- [x] 18. init 构造（统一声明层，M14 CallableKind.Init + M18 参数映射）
  - [x] 基本 init（普通形参 + 可选体）
  - [x] 参数映射（`_ -> field`，M18）
  - [x] 单元测试（含于 TypeDeclaration 68/68）
  
- [x] 19. operator 声明（统一声明层，M14，CallableKind.Operator）
  - [x] 二元运算符（如 `operator plus(o: V): V {}`）
  - [x] 一元运算符（同一路径）
  - [x] 单元测试（含于 TypeDeclaration 77/77）

### P5 - Wrapper 系统
- [x] 20. Wrapper 声明（统一声明层，M13/M14 头部 + M20 主体）
  - [x] Entity wrapper（`@WrapperTarget(.Entity)` 注解，M20）
  - [x] Method wrapper（`@WrapperTarget(.Method)` 注解，M20）
  - [x] Value wrapper（`@WrapperTarget(.Value)` 注解，M20）
  - [x] @ 注解 / wrapper 应用（§14.5：挂所有声明，含栈上变量，M20）
  - [x] Specific proxy（`.proxy.name` / `.proxy.opr.name` / `.proxy.get.name` / `.proxy.set.name`，M20）
  - [x] Wildcard proxy（`.proxy.*` 等四类 + 同类唯一性校验，M20）
  - [x] 前导点 enum case 引用（§12：`.Success` / `.Failed(404)`，M20）
  - [x] wrapper 路径访问（`:`，表达式侧后缀链，M21）
  - [x] 模块系统 import（§15.2 三种形态，ImportParserLayer 重建，M21）
  - [x] namespace 声明（§15.1，NamespaceParserLayer，M22）
  - [x] 单元测试（含于 TypeDeclaration 94/94、Expression 75/75、CodeBlock 29/29、Import 14/14、Namespace 7/7）

### P6 - 高级特性
- [x] 21. LambdaExpressionParserLayer（M8 提前落地）
  - [x] 基本 lambda
  - [x] 泛型 lambda
  - [x] async lambda
  - [x] trailing lambda（脱糖为调用实参）
  - [x] 单元测试（15/15）
  
- [x] 22. GenericParametersParserLayer
  - [x] 泛型参数声明
  - [x] extends 约束
  - [x] supers 约束
  - [x] with 约束
  - [x] 型变
  - [x] 单元测试（21/21 通过）
  
- [x] 23. PropertyAccessorParserLayer（M16 提前落地）
  - [x] getter
  - [x] setter
  - [x] 编译器生成的访问器
  - [x] 单元测试（17/17）

---

## 实现顺序建议

### 第一阶段：基础 (P0)
1. 先实现 `LiteralParserLayer`，可以独立测试
2. 实现 `TypeReferenceParserLayer`，复用 PathParserLayer
3. 实现 `VariableDeclarationParserLayer`，依赖前两者
4. **里程碑**：可以解析简单的变量声明

### 第二阶段：表达式 (P1)
1. 实现 `ParameterListParserLayer`
2. 实现 `ExpressionParserLayer` (最复杂的部分)
3. **里程碑**：可以解析复杂表达式

### 第三阶段：语句 (P2)
1. 完善 `CodeBlockParserLayer`
2. 实现 `IfStatementParserLayer`
3. 实现 `LoopParserLayer`
4. 实现其他控制流解析器
5. **里程碑**：可以解析完整的函数体

### 第四阶段：类型声明 (P3)
1. 实现 `ClassDeclarationParserLayer`
2. 实现 `StructDeclarationParserLayer`
3. 实现 `InterfaceDeclarationParserLayer`
4. 实现 `EnumStructDeclarationParserLayer`
5. **里程碑**：可以解析完整的类型声明

### 第五阶段：函数 (P4)
1. 实现 `FunctionDeclarationParserLayer`
2. 实现 `InitDeclarationParserLayer`
3. 实现 `OperatorDeclarationParserLayer`
4. **里程碑**：可以解析完整的 Rigi 程序

### 第六阶段：高级特性 (P5-P6)
1. 实现 `WrapperDeclarationParserLayer`
2. 实现 `LambdaExpressionParserLayer`
3. 实现 `GenericParametersParserLayer`
4. 实现 `PropertyAccessorParserLayer`
5. **里程碑**：完整的 Rigi 语法支持

---

## 常见陷阱和注意事项

### 1. 换行处理
- 记住 Rigi 使用换行作为语句终止符
- `()` 和 `[]` 内的换行被忽略
- `{}` 内的换行不被忽略

### 2. 运算符优先级
- Rigi **没有**运算符优先级
- 必须用括号明确指定运算顺序
- `a + b * c` 是编译错误
- `(a + (b * c))` 或 `((a + b) * c)` 才是合法的

### 3. 路径表达式
- 路径表达式 (., ?., :) 严格从左到右结合
- 在运算符解析之前整体形成
- 不参与运算符优先级竞争

### 4. 可空类型
- `T?` 不能再次施加 `?`
- 不存在 `T??`

### 5. Enum 构造
- enum struct 不能通过普通 `new` 或 `TypeName()` 构造
- 只能使用具名 case
- `.CaseName` 必须有明确的类型上下文

### 6. Getter/Setter
- get 和 set 在是否需要 backing field 上必须一致
- `value: _` 表示需要 backing field
- `_: _` 表示计算属性

### 7. Rich/Shared 闭包
- 非 rich struct 不能持有 Object
- shared 类型不能指向 local object
- 继承不得打破这些规则

### 8. Async 边界
- async 调用的参数必须满足共享闭包规则
- local object 不能跨 Coroutine 边界

---

## 下一步行动

**当前状态**（2026-07-26 更新）：
- ✅ P0 全部完成（字面量、类型引用、变量声明）
- ✅ P1 全部完成（表达式系统：后缀链、Lambda、if/switch 表达式、typeOf/as/is、seq 表达式形态）
- ✅ P2 全部完成（语句系统：代码块、if/循环、try-catch-finally、seq、throw、await/yield）
- ✅ P3 全部完成（M13–M19：统一声明层、声明泛型参数、getter/setter、enum case 列表、init 参数映射、like 委托、ext 扩展成员）
- ✅ P4 全部完成（#17–19 由统一声明层同步覆盖：func/init/operator 声明、泛型、参数映射、ext）
- ✅ P5 全部完成（M20 wrapper 主体、M21 import + wrapper 路径访问、M22 namespace 声明）
- 测试总计 417/417（菜单 2–22）

**下一步**（Parser 前端已收官，进入编译器下一阶段）：
1. 语义分析
2. BIL 输出（见 `../../BIL_STANDARD.md`）

**历史目标**（已过时，保留存档）：
1. ~~**立即开始**: 实现 `LiteralParserLayer`~~
2. ~~**第一周目标**: 完成 P0 所有组件~~ ✅
3. ~~**第一个月目标**: 完成 P0-P2，可以解析函数体~~ ✅（2026-07-26 达成）
4. **第二个月目标**: 完成 P3-P4，可以解析完整程序（P3 进行中）
5. **第三个月目标**: 完成 P5-P6，全部高级特性

---

## 参考资源

- [SYNTAX.md](../../SYNTAX.md) - Rigi 完整语法规范
- [FRONTEND_TYPES.md](./FRONTEND_TYPES.md) - 前端数据类型说明
- [BIL_STANDARD.md](../../BIL_STANDARD.md) - BIL 中间表示标准
- [RUNTIME.md](../../RUNTIME.md) - 运行时模型

---

**版本历史**:
- v1.0 (2026-07-17): 初始版本
- v1.1 (2026-07-17): 泛型语法迁移至 `\<...>`；#22 GenericParametersParserLayer 完成；检查清单同步实际进度
- v1.2 (2026-07-17): #5 ParameterListParserLayer 完成；#4 调用/索引/成员访问/泛型调用后缀链与 new 构造参数完成
- v1.3 (2026-07-26): P1/P2 全部完成（M7–M12：代码块、if/循环、try-catch-finally、seq、throw、await/yield、Lambda、if/switch 表达式、typeOf/as/is）；P3 计划修订——#13–19 不再各建独立 Layer，改为统一 DeclarationParserLayer（M13/M14）；检查清单与各组件状态同步；测试 311/311
- v1.4 (2026-07-26): M15 声明泛型参数接入统一声明层（类型/函数/operator，复用 GenericParametersParserLayer，不新增状态）；检查清单与「下一步」同步；测试 326/326
- v1.5 (2026-07-26): M16 #23 PropertyAccessorParserLayer 提前落地（§9.4 三类定义位置经 VariableDeclarationParserLayer 统一接入，backing field 判定与一致性校验）；测试 343/343
- v1.6 (2026-07-26): M17 enum struct `[]` case 列表落地（统一声明层内联子状态，#16 完成；固定/参数化 case、显式判别值、唯一性与混用校验）；测试 353/353
- v1.7 (2026-07-26): M18 init 参数映射 `_ -> field` 落地（#18 完成；ParameterListParserLayer 增 allowMapping 开关，同名/显式名/默认值/混合形态）；测试 360/360
- v1.8 (2026-07-26): M19 `like` 委托（#13 完成）+ `ext` 扩展成员落地，**P3/P4 全部完成**；检查清单与「下一步」同步至 P5；测试 369/369
- v1.9 (2026-07-26): M20 P5 起步——wrapper 主体落地（#20 完成）：@ 注解（wrapper 应用，类型标识以 SYNTAX §14 的 `@WrapperTarget(.X)` 为准）、`.proxy.*` 代理成员（specific + 四类 wildcard）、前导点 enum case 引用；顺带修复 CodeBlockASTNode 隐藏基类 Children 字段问题；测试 392/392
- v2.0 (2026-07-26): M21 模块系统 import（§15.2 三种形态，ImportParserLayer 重建）+ wrapper 路径访问（`:` 接入表达式后缀链，#20 全部完成）；新增 ImportTests（菜单 21）；测试 410/410
- v2.1 (2026-07-26): M22 namespace 声明（§15.1，NamespaceParserLayer）落地，**P5 收官、roadmap P0–P5 全部完成**；新增 NamespaceTests（菜单 22）；测试 417/417

