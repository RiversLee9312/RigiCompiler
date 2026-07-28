# Latte Compiler 进度报告

> **进度对齐标准**：本文档是项目进度的**唯一权威来源**。
> 每完成一个里程碑（新增 ParserLayer、落地一项机制、完成一次语法迁移）必须更新本文档；
> 更新时保持文档结构不变，并在「里程碑历史」追加一段。
> 计划与分工见 `compiler/frontend/PARSER_ROADMAP.md`；本文档只记录「现状」。

**报告日期**: 2026-07-28
**当前阶段**: Parser/PDA 大扫除（架构重构）完成（M23）；AST 结构标注与 Validator 重写完成（M24）；Lexer 修复（除法/EOF/注释）与 fuzz 基建完成（M25）；日志系统与 AST JSONL 序列化完成（M26）；CLI 插件化重构（help/compile/test）完成（M27）；Lexer 位置修复 + AST Source Span + ASTVisitor 统一遍历完成（M28）；AST 容器重构（基类共有 Children/Annotations 删除，语义字段 + wrapper 挂载接口）完成（M29）；Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档幽灵清理完成（M30）；前端大修（M31）完成；多行字符串（M32）完成；值块统一（M33：if/switch 表达式分支体与 lambda 体统一为代码块、switch 语句形态、lambda 裸 return 编译错误、seq 默认标签迁移 `_`）完成；**技术债清扫（M34）完成：字符字面量（CharLexerLayer + CharToken + CharLiteralASTNode）、复合赋值 10 运算符（CompoundAssignmentExpressionASTNode）、`is` 右侧 enum case（TypeCheck 双字段互斥）、wrapper `.name` 保留参数名、import `{}` 单标识符禁令规则化报错**；**下一步**：语义分析、BIL 输出
**测试总计**: 895/895 通过 (100%) + Lexer fuzz 6000/6000（30 个套件，`dotnet run -- test --all` 单命令全量）
**版本控制**: Git `main` 分支（2026-07-17 首次提交）

---

## 1. 里程碑总览

| # | 里程碑 | 状态 | 完成日期 | 测试 |
|---|--------|------|----------|------|
| M1 | P0 核心基础（字面量/类型引用/变量声明） | ✅ | 2026-07-17 | 28/28 |
| M2 | 结果传递机制（IResultProducer/IResultConsumer） | ✅ | 2026-07-17 | 含于各套件 |
| M3 | 泛型语法迁移 `\<...>`（文档 + Lexer + SymbolLayer） | ✅ | 2026-07-17 | 18/18 |
| M4 | GenericParametersParserLayer（roadmap #22） | ✅ | 2026-07-17 | 21/21 |
| M5 | 表达式后缀链 + ArgumentListParserLayer（roadmap #4 大部分） | ✅ | 2026-07-17 | 54/54 |
| M6 | ParameterListParserLayer（roadmap #5） | ✅ | 2026-07-17 | 14/14 |
| M7 | P2 语句系统核心（CodeBlock/if 语句/循环/return/赋值） | ✅ | 2026-07-18 | 42/42 |
| M8 | P1 收尾（Lambda/if/switch 表达式 + typeOf/as/is） | ✅ | 2026-07-18 | 39/39 |
| M9 | TryCatchFinallyParserLayer（roadmap #10） | ✅ | 2026-07-26 | 9/9 |
| M10 | SeqBlockParserLayer（roadmap #11，含表达式形态） | ✅ | 2026-07-26 | 17/17 |
| M11 | throw 语句 | ✅ | 2026-07-26 | 10/10 |
| M12 | CoroutineOps：await/yield（roadmap #12） | ✅ | 2026-07-26 | 13/13 |
| M13 | P3 类型声明解析基础（DeclarationParserLayer 重构） | ✅ | 2026-07-26 | 16/16 |
| M14 | 统一声明层：全局/成员/嵌套共用一套 infra | ✅ | 2026-07-26 | 36/36 |
| M15 | 声明泛型参数接入统一声明层（类型/函数/operator） | ✅ | 2026-07-26 | 15/15 |
| M16 | 属性访问器 getter/setter（§9.4 三类位置，roadmap #23） | ✅ | 2026-07-26 | 17/17 |
| M17 | enum struct 的 `[]` case 列表（固定/参数化 case、显式判别值） | ✅ | 2026-07-26 | 10/10 |
| M18 | init 参数映射（`_ -> field`，SYNTAX §9.3，roadmap #18 收尾） | ✅ | 2026-07-26 | 7/7 |
| M19 | `like` 委托（§9.6）+ `ext` 扩展成员（§4.4）—— **P3/P4 收官** | ✅ | 2026-07-26 | 9/9 |
| M20 | P5 起步：wrapper 主体（@ 注解 + `.proxy.*` 代理成员 + 前导点 enum case） | ✅ | 2026-07-26 | 23/23 |
| M21 | 模块系统 import（§15.2）+ wrapper 路径访问（`:`，§14.1/§3） | ✅ | 2026-07-26 | 18/18 |
| M22 | namespace 声明（§15.1）—— **P5 收官** | ✅ | 2026-07-26 | 7/7 |
| M23 | Parser/PDA 大扫除：TokenDisposition、施工目标协议、ExpressionRootASTNode、EOF 正式化、AST 完整性验证、测试基础设施 | ✅ | 2026-07-26 | 425/425（22 套件） |
| M24 | AST 结构标注（ChildAstNode/ParentAstNode/AstCarrier）+ Validator 重写 + 删除 ASTNodeType + 5 个父子指针 bug 修复 | ✅ | 2026-07-26 | 430/430（23 套件） |
| M25 | Lexer 修复：SlashLexerLayer（除法/注释分流）+ Lexer 输出 EOF + 注释集中跳过 + fuzz 基建 | ✅ | 2026-07-26 | 453/453 + fuzz 6000（24 套件） |
| M26 | 日志系统（Logger 分级 + verbose 默认关闭 + JSONL 落盘）+ AST JSONL 序列化诊断 | ✅ | 2026-07-26 | 498/498 + fuzz 6000（26 套件） |
| M27 | CLI 插件化重构：`<COMMAND> [--sub-cmd...]`（help/compile/test）+ CommandLineMask + 帮助程序生成 + 交互菜单删除 | ✅ | 2026-07-27 | 544/544 + fuzz 6000（27 套件） |
| M28 | Lexer 位置修复（offset/列号/EOF 冲刷/token 头/sourceName 单源化）+ AST Source Span（ISpanReceiver 层 span 回填）+ ASTVisitor 统一遍历 + Validator span 检查与类型审计 | ✅ | 2026-07-27 | 556/556 + fuzz 6000（27 套件） |
| M29 | AST 容器重构：基类共有 `Children`/`Annotations` 删除；语义字段（`Declarations`/`Statements`/`Members`）+ wrapper 挂载接口（`IWrapperAttachable` + Entity/Method/Value 三分类） | ✅ | 2026-07-27 | 27 套件全绿（用例无增删）+ fuzz 6000 |
| M30 | `Core/Utilities.cs` 拆分（Token/Keywords/异常/AST 基类归位 7 文件）+ ASTVisitor 遍历可重载（VisitNode/EnumerateChildren virtual）+ 文档幽灵清理（FRONTEND_TYPES 修订、FRONTEND_ARCHITECTURE 删除、ROADMAP 头注） | ✅ | 2026-07-27 | 27 套件全绿（用例无增删）+ fuzz 6000 |
| M31 | 前端大修：全量 review 驱动的 40+ 项修复（Span 左闭右开、Lexer 块注释重写、续行规则、位运算符、0b/0o/下划线字面量、Keywords 大扫除、修饰符/标识符校验、JSONL v2 + 反序列化器、测试基建统一） | ✅ | 2026-07-28 | 746/746 + fuzz 6000（29 套件） |
| M32 | 多行字符串 `"""`：SYNTAX §3.3 规范定稿（Swift 风格严格多行）+ QuoteLexerLayer 引号分流 + MultilineStringLexerLayer 两阶段施工 + 转义表单源化 + 插值标记词法期判定（`\${` 误报修复；Parser/AST 经 StringToken 复用近零改动） | ✅ | 2026-07-28 | 790/790 + fuzz 6000（30 套件） |
| M33 | 值块统一：if/switch 表达式分支体与 lambda 体统一为代码块（多语句 + `return@_`/named 取值）、switch 语句形态（新 SwitchStatementASTNode）、lambda 体内裸 return 编译错误（allowBareReturn 全链传染）、seq 匿名默认标签 `seq`→`_` | ✅ | 2026-07-28 | 838/838 + fuzz 6000（30 套件） |
| M34 | 技术债清扫：字符字面量（CharLexerLayer + CharToken + CharLiteralASTNode）、复合赋值 10 运算符（CompoundAssignmentExpressionASTNode）、`is` 右侧 enum case（TypeCheck TargetType/TargetCase 双字段互斥）、wrapper `.name` 保留参数名、import `{}` 单标识符禁令规则化报错 | ✅ | 2026-07-28 | 895/895 + fuzz 6000（30 套件） |

---

## 2. 当前可解析语法

```latte
// 字面量
42, 0xFF, 100L, 3.14, 0.1f, "Hello ${x}", """多行字符串""", true, null, 'A', '\n'

// 类型引用（含 \< 泛型、嵌套、可空）
i32, String?, List\<T>, Map\<K,V>, List\<Map\<String, i32>>?

// 变量声明（含完整初始化表达式）
var x = 42
const name: String = "Hello"
var v = foo(1, name = 2)
var v = foo().bar[0]
var v = new User(id = 42)
var v = a.b\<i32>(x)
var r = 1 + (2 * 3)          // 无优先级规则已强制：1 + 2 * 3 报错

// 类型操作（is/supers/with 检查，as/as? 转换，typeOf）
obj is String, obj supers Animal, obj with Serializable
obj as String, obj as? String
var t = typeOf(box)
result is .Failed          // is 右侧 enum case（§12.3，M34；as/supers/with 右侧仍只收类型）

// if / switch 表达式（分支体统一为代码块，M33：单表达式分支隐式取值是
// 「块内恰好一条 ExpressionStatement」的语义规则；多语句分支 return@_ / named 取值）
var r = if (x > 0) { x } else { opposite(x) }          // 必须有 else
var r = if (x > 0) named check {
    seq { return@check x }                             // named + return@标签 穿透内层块
} else {
    return@_ opposite(x)                               // 匿名分支体默认标签是 _
}
var r = switch(expr) {
    (1) -> { "one" }                                   // 值匹配
    (_ > 10) -> {                                      // 模式匹配（_ 引用 expr）
        logBig(expr)
        return@_ "big"                                 // 多语句分支体显式取值
    }
    default -> { "other" }                             // 必须有 default
}
var r = switch(expr) named match { (1) -> { return@match 1 } default -> { return@match 0 } }

// switch 语句（M33：语句形态，结果值被丢弃；分支体为完整代码块，必须有 default）
switch(expr) {
    (1) -> { handleOne() }
    (_ > 10) -> {
        logBig(expr)
        handleBig()
    }
    default -> { handleOther() }
}

// Lambda（含泛型、async、trailing；体为单表达式或多语句块，M33）
var f = func{(x: i32): i32 -> (x + 1)}
var f = func{(width: TSize)\<TSize extends Size>: TSize -> width}
var loader = async func{(id: i32): SharedUser -> loadUserNow(id)}
list.map{(item: String): i32 -> item.length}           // 脱糖为调用实参
var f = func{(x: i32): i32 -> {                        // 多语句块体
    const doubled = (x * 2)
    return@_ doubled                                   // 块体必须显式 return@；裸 return 是编译错误
}}
var f = func{(x: i32): i32 -> named calc { return@calc (x * 2) }}

// 代码块与语句（语句以换行或 } 结束）
{
    var x = 1
    x = (1 + 2)                                        // 赋值
    x += 1                                             // 复合赋值（M34：+=/-=/*=//=/<<=/>>=/>>>=/&=/|=/^=）
    foo().field = v
    return x                                           // return / return@_ value
    break@outer                                        // break/continue[@标签]
}

// if 语句（else 可选，支持 else if 链）
if (x > 0) { foo() } else if (y > 0) { bar() } else { baz() }

// 循环（for-each / 范围 / while / do-while / named 标签）
for (item in collection) { print(item) }
for (i in 0 to 10) named outer { break@outer }
while (condition) { doSomething() }
do { doSomething() } while (condition)

// try-catch-finally（SYNTAX.md §8）：完整异常处理系统
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

// throw 语句：抛出异常
throw new IOException("File not found")
throw getError()
if (invalid) {
    throw new ValidationError()
}

// seq 块（SYNTAX.md §6）：作用域/using 资源管理/named 标签/表达式形态
seq {
    var temp = compute()
}

volatile seq {
    // volatile 操作
}

seq using(const file = new File("path"))
using(var stream = new FileInputStream(file))
named readFile {
    process(stream)
}

// seq 作为表达式（return@_/return@标签，匿名默认标签为 _，M33）
const result = seq {
    const ac = a * c
    const discriminant = (b * b) - (4.0 * ac)
    return@_ sqrt(discriminant)  // 返回值
}

var r = seq named calc {
    return@calc getValue()
}

// await/yield 协程操作（SYNTAX.md §7.5）
const user = await loadUser(42)
await flushLogs()
yield                         // 裸 yield
yield sleep(1000)             // 带 alarm

// 类型声明（class/interface/struct/wrapper + 修饰符/继承/implements/嵌套）
pub open class Dog : Animal implements Drawable, Serializable {
    pub var name: String
    pub init(x: i32) {}
    pub func speak(): String { return "Woof!" }
    pub class Inner {}                 // 嵌套类型，与顶层同一路径
}
pub rich struct Entry {}
pub shared rich struct SharedEntry {}
wrapper Logged {}

// like 委托（§9.6，仅 class）与 ext 扩展成员（§4.4，限定名 Type.member）
pub class Apple : Fruit like pear {
    pub var pear: Pear = Pear()
}
pub ext func String.reversed(): String { ... }
pub ext var String.isEmpty: bool { get(_: _) { return (this.length == 0) } }

// enum struct 的 [case 列表]（§12：固定/参数化 case、_ 参数洞、显式判别值）
pub enum struct RequestResult {
    pub const errorCode: i32
    pub init(code: i32)
}[
    Success(-1) -> 0,
    Failed(errorCode = _) -> 1
]

// 全局字段与全局函数（与类成员走同一条解析路径）
pub const MAX: i32
var counter: i32
func add(a: i32, b: i32): i32 { return (a + b) }
pub static func helper()

// 属性访问器（§9.4：类字段/全局变量/栈上变量三类位置同一条路径）
var width: i32 {
    pub get(value: _) { return value }       // backing field + 自定义体
    priv set(value: _) { log(value) }
} = 100
var height: i32 {
    pub get                                  // 编译器生成实现（无参无体）
    priv set
} = 200
var area: i32 { get(_: _) { return (width * height) } }   // 计算属性（无 backing field）

// @ 注解 / wrapper 应用（§14.5：可叠加，挂所有声明；含编译器内建 @WrapperTarget）
@WrapperTarget(.Entity)
pub wrapper Logged\<TTarget extends Object> { ... }
@WrapperTarget(.Value)
pub wrapper Clamped { ... }
@Logged("DEBUG")
@Serializable()
pub class MyService { ... }
@Timed()
pub func heavyComputation(): i32 { ... }
@Clamped(0, 100)
var health: i32 = 50

// wrapper proxy 成员（§14.2：specific + 四类 wildcard；同类 wildcard 唯一，§14.6）
operator .proxy.doSomething(arg: i32): String { ... }       // specific 方法代理
operator .proxy.opr.plus(another: TTarget): TTarget { ... } // specific 运算符代理
operator .proxy.get.name\<TField>(value: TField): TField { ... }
operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(symbol: String, ...): TReturn { ... }
operator .proxy.get.*\<TValue>(symbol: String, value: TValue): TValue { ... }
operator .proxy.set.*\<TValue>(symbol: String, value: TValue) { ... }
operator .proxy.opr.*\<named TNamedArgs..., ...>(...): TReturn { ... }
operator .proxy.call(.name: String, args: named Any...): Any { ... }  // method canonical + .name 保留参数名（§14.4，M34）

// 前导点 enum case 引用（§12：固定/参数化 case）
const result: RequestResult = .Success
const failed: RequestResult = .Failed(404)

// import（§15.2：单个/多个/全部三种形态；多个导入共享前缀路径）
import core.collections.List
import core.collections.{List, Map}
import core.collections.*

// namespace 声明（§15.1：顶层单行声明）
namespace com.example.myapp

// wrapper 路径访问（§14.1/§3：与成员访问同属路径后缀链，链式左结合）
var logger = service:Logged
var w = obj:A:B
var l = foo().bar[0]?.length:MyWrapper
var t = service:Logged.level

// 声明上的泛型参数（类型/函数/operator，含型变/约束/可变参数）
class Container\<TElement> { ... }
class Cache\<out TElement extends Comparable> { ... }
func transform\<TInput, TResult>(input: TInput): TResult { ... }
func update\<named TValues... with Serializable>(configs: named TValues...): bool { ... }
pub operator plus\<TAnother extends Addable>(another: TAnother): V { ... }

// 函数形参列表（已接入 func/operator/init 声明）
(a: i32, b: String = "x", rest: named i32...)

// init 参数映射（§9.3：_ 同名映射 / 显式名 / 默认值 / 与普通参数混合）
pub class Point {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
    pub init(_ -> x = 0, _ -> y = 0)
    pub init(horizontal: i32 -> x, vertical: i32 -> y)
}
```

---

## 3. 组件状态详表

| 组件 | 状态 | 测试 | 说明 |
|------|------|------|------|
| LiteralParserLayer | ✅ | 34/34 | 全部字面量（M31：0b/0o/下划线补齐，`3.` 报错）；字符字面量 Lexer 明确报错；多行字符串经 StringToken 复用零改动接入（M32） |
| MultilineStringLexerLayer（+ QuoteLexerLayer 分流） | ✅ | 43/43（M32 新套件） | SYNTAX §3.3 Swift 风格严格多行：开界换行剥除、闭界独占行定缩进基准、转义与单行一致（StringEscape 单源）、两阶段施工（按行缓冲 + 闭界时剥缩进/转义）、插值标记词法期判定（`\${` 不误报） |
| TypeReferenceParserLayer | ✅ | 17/17 | M31 重写为真实套件（独立层驱动 + 集成 + 结构断言） |
| VariableDeclarationParserLayer | ✅ | 17/17 | Initializer 经 ExpressionRootASTNode 直挂；访问器块委托 PropertyAccessorParserLayer（M16）；M31 保留字/标识符校验 |
| ExpressionParserLayer | ✅ | 106/106 | roadmap #4 全部落地；前导点 enum case（M20）、wrapper 路径访问 `:`（M21）；M31：位运算符 `<<`/`&`/`\|`/`^`、`in` 移除、insideParens 续行、复合赋值明确报错、span 含关键字 |
| ArgumentListParserLayer | ✅ | 12/12（M31 新套件） | 位置/具名/混合实参；M31：续行、空索引拒绝 |
| LambdaExpressionParserLayer | ✅ | 37/37 | roadmap #21 提前落地；体双形态（M33）：单表达式 / 多语句块体（named 标签、裸 return 边界） |
| SwitchStatementParserLayer | ✅ 两种形态 | 35/35（SwitchExpression 套件） | 表达式 + 语句形态（M33，新 SwitchStatementASTNode）；分支体统一代码块、named 标签、两形态强制 default |
| TypeOfExpressionParserLayer | ✅ | 7/7 | typeOf(expr) |
| CodeBlockParserLayer | ✅ | 29/29 | 语句识别与分发；return/break/continue 内联子状态；@ 注解声明分发（M20）；switch 语句路由与 allowBareReturn 裸 return 检查（M33） |
| IfStatementParserLayer | ✅ 两种模式 | 含于各套件 | 表达式模式强制 else；语句模式 else 可选 + else if 链；表达式分支体统一代码块 + named 标签（M33） |
| LoopParserLayer | ✅ | 15/15 | for-each/范围/while/do-while/named 标签 |
| TryCatchFinallyParserLayer | ✅ | 9/9 | roadmap #10；多 catch 子句、finally(e)、嵌套 try |
| SeqBlockParserLayer | ✅ | 17/17 | roadmap #11；volatile/using/named；语句+表达式双形态 |
| ThrowStatement（内联） | ✅ | 10/10 | throw expression；配合 try-catch 构成完整异常系统 |
| CoroutineOps（await/yield） | ✅ | 13/13 | roadmap #12；await 一元前缀运算符，yield 语句 |
| GenericParametersParserLayer | ✅ | 21/21 | 声明/约束/型变/可变参数；已接入类型/函数/operator 声明（M15） |
| ParameterListParserLayer | ✅ | 14/14 | 普通/默认/可变/具名可变；已接入 func/operator/init 声明；init 参数映射 `_ -> field`（M18，allowMapping 开关） |
| PathParserLayer | ✅ | 16/16（M31 新套件） | 符号路径 + `\<` 泛型实参；M31：尾点/双点/未闭合泛型报错（allowVariadicDots 保留 `...`） |
| RootParserLayer | ✅ | 含于各套件 | 顶层分发（声明统一委托 DeclarationParserLayer） |
| DeclarationParserLayer | ✅ 统一声明层 | 94/94（TypeDeclaration 套件） | 任何位置任何声明的唯一入口：全局/成员/嵌套共用一套状态机；声明泛型参数（M15）、enum `[]` case 列表（M17）、like 委托与 ext 限定名（M19）、@ 注解与 wrapper `.proxy.*` 代理成员（M20）已接入 |
| PropertyAccessorParserLayer | ✅ | 17/17 | §9.4 访问器块 `{ get... set... }`；backing field 判定与 get/set 一致性校验；三类定义位置经 VariableDeclaration 汇聚 |
| ImportParserLayer | ✅ | 14/14 | §15.2 三种形态（单个/`.{}` 多个/`.*` 全部）；前缀路径复用 PathParserLayer（M21 重建） |
| NamespaceParserLayer | ✅ | 7/7 | §15.1 顶层单行声明；路径复用 PathParserLayer（M22） |
| ASTIntegrityValidator | ✅ | 含于各套件 | Parse 成功后自动验证 AST 不变量（M23）；M24 重写为 Attribute 驱动遍历；M28 基于 ASTVisitor 统一遍历重写 + span 校验与类型审计；M31：Required 子节点校验、基类链字段审计、[AstCarrier] 递归审计；失败抛 CompilerInternalException |
| ASTIntegrityValidatorTests | ✅ | 10/10 | 手工构造 AST 直调 Validate：合法树通过 + 结构破坏/span 破坏/类型审计违规拒绝（M24/M28） |
| ASTVisitor | ✅ | 含于 Validator/Serializer 套件 | 统一 AST 遍历基建（AST/ASTVisitor.cs）：[ChildAstNode] 子节点枚举唯一实现，Validator 与 Serializer 共用（M28） |
| LexerFuzzTests | ✅ | 32/32 + fuzz 6000 | Slash/EOF/注释固定用例 + 位置精确性用例（M28；M31 起左闭右开）+ 块注释吞字符/跨行分段/`\r\n` 归一/字符字面量报错用例（M31）+ 纯随机/结构化/变异 fuzz（固定种子，不变量含 sourceName/offset/范围不颠倒）+ Parser 注释跳过集成（M25） |
| TokenDispositionTests | ✅ | 4/4 | Push/Pop × Consume/Replay 四组合协议测试（M23） |
| Logger | ✅ | 7/7（LoggerTests） | 统一日志出口（Core/Logger.cs）：Verbose/Warning/Error 三级；控制台默认只显示 Warning+，`--verbose` 子命令放开 Verbose；`--log-to PATH` 全量（含 Verbose）JSONL 落盘（M26） |
| AstJsonlSerializer | ✅ | 86/86 | AST 树 JSONL 序列化 v2（M31：carrier 记录化、Nullable 标量、先过滤再取值、循环保护）+ `AstJsonlDeserializer` 完整反序列化（字段名键控、产物过 Validator、往返逐行一致）；`compile --dump-ast PATH` 输出（M26）；M33 新结构（值块/switch 语句/lambda 块体）经反射驱动零改动接入 |
| CommandLine | ✅ | 46/46（CommandLineParserTests） | CLI 内核（Core/CommandLine.cs + Core/Commands.cs）：CommandLineMask 自描述元数据驱动解析与 help 生成；`<COMMAND> [--sub-cmd...]` 结构（compile/test/help），交互菜单已删（M27） |

---

## 4. 关键架构决策（摘要）

- **施工目标协议**（M23 大扫除）：Parser Layer 栈只传递控制权；父 Layer 在 Push 前确定施工目标（具体节点或 ExpressionRootASTNode 等附加目标），子 Layer 原地施工或向目标附加节点；Pop 不传递任何数据。原 `IResultProducer`/`IResultConsumer`/`pendingResultHandler` 已全部删除
- **TokenDisposition**：Push/Pop 的 token 处置使用具名枚举（Consume/Replay），替代原 `bool shouldKeepToken`
- **ExpressionRootASTNode**：Syntax AST 中所有表达式位置的统一稳定挂载点；一次性 Attach、禁止替换；ASTNode.Parent 只能设置一次、**禁止任何形式重挂**（无 reparent）；「归属后知」场景以创建时归属即定的容器承载（ExpressionStatementASTNode 双 Root 槽、LoopStatementASTNode.RangeTo）或延迟一次性 AttachTo（注解）；解析成功后经 `ASTIntegrityValidator` 自动验证不变量
- **EOF 正式 Token**：`EndOfFileToken` 由 Lexer 在输出末尾追加（M25；Parser 仅对绕过 Lexer 的调用方保持追加兼容），只由 RootParserLayer 消费；非 Root 层遇 EOF 要么 Pop(Replay) 层层上交，要么报 "Unexpected end of file"
- **注释集中跳过**（M25）：CommentToken 由 Parser 主循环分发时统一跳过，各 Layer 不再自行处理；行注释不再吞掉结尾换行（回流由 Base 层产出 LineBreakToken）
- **SlashLexerLayer**（M25）：`/`、`/=`、`//`、`/*` 统一分流入口；输入结束以虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException
- **泛型语法 `\<...>`**：`<` 仅作小于号；Lexer 不合并 `>` 系列，`>=`/`>>`/`>>>` 由表达式层重组（详见 `SYNTAX.md` §3.6）
- **表达式后缀链**：纯符号路径保持 PathParserLayer 的 Symbol 形态；`(`/`[`/`.`/`?.`/`\<`/`:` 后缀由 ExpressionParserLayer 链接，底座为表达式时才产生 MemberAccessASTNode
- **独立 Layer 可测性**：`Parser.Parse(tokens, baseLayer, entryLayer)` + `TestRootParserLayer`（只接受 EOF）支持任意 Layer 独立驱动测试，且拒绝被测 Layer 漏消费 token
- **统一声明层**（M14，依据 SYNTAX.md §14.8）：canonical symbol 的类名段可为空、`.static.` 只是标记位，因此全局函数与成员方法结构同构——`DeclarationParserLayer` 一套状态机覆盖全局/成员/嵌套任何声明；`CallableDeclarationASTNode` 单节点覆盖 func/operator/init；成员挂各节点语义容器（M29 起：`RootASTNode.Declarations` / `CodeBlockASTNode.Statements` / 类型节点 `Members`；基类共有 `Children` 已删除）
- **日志系统**（M26）：`Core/Logger` 是唯一日志出口（Verbose/Warning/Error）；Lexer/Parser 的 ContextImpl 经 Logger 输出，禁止直接 `Console.WriteLine`；控制台门槛默认 Warning+，`--verbose` 子命令放开 Verbose；`--log-to` 把全量日志（含 Verbose）以 JSONL 落盘，文件不过滤级别，便于 grep 诊断
- **AST JSONL 序列化**（M26）：`AstJsonlSerializer` 复用 Validator 的 [ChildAstNode] 反射下钻，深度优先每节点一行（id/parent/via/type/fields），`compile --dump-ast` 输出，供结构诊断
- **CLI 插件化**（M27）：用法 `<COMMAND> [--sub-cmd [args...]...]`，COMMAND 为 help/compile/test；每个 COMMAND 与 --sub-cmd 都是插件，暴露 `CommandLineMask`（名称/描述/参数个数/互斥）自描述元数据；解析器与 `help` 文本完全由 Mask 注册表数据驱动、程序生成；交互菜单已删除
- **位置信息单源化**（M28）：`CharRange.sourceName` 是源名唯一来源（`CharPosition` 不再携带）；`CharPosition.offset` 为 0 起始字符索引（修复恒 0 bug）；换行算当前行最后一列（修复第二行起列号 +1）；token 头跳过空白（修复缩进行 token Start 落在前导空格）；EOF 冲刷帧占虚拟位置（修复 EOF 处 token End 少算/倒置）
- **AST Source Span**（M28）：`ASTNode.Span`（`CharRange?`）记录节点源码范围；层目标由 Parser 主循环按 token 流计算、经 `ISpanReceiver.ReceiveSpan` 在层弹出时回填（`??=` 只填空），层内自建节点由所在层显式设置（创建记 Start、完成封 End，经 `ParserLayerContext.GetPreviousLocation()`）；`ExpressionRootASTNode` 透明继承内容表达式的 span；Validator 校验每节点 span 非空、sourceName 非空、End 不早于 Start
- **ASTVisitor 统一遍历**（M28）：`AST/ASTVisitor.cs` 是 [ChildAstNode] 子节点枚举的唯一实现，ASTIntegrityValidator 与 AstJsonlSerializer 共用（via 统一为 `member[i]`/`member[i](Carrier.Field)` 格式）；Validator 新增类型审计——装 ASTNode 的成员（字段/自动属性）必须带 [ChildAstNode]/[ParentAstNode]，[ChildAstNode] 标在非 AST 成员上同样拒绝
- **AST 容器语义化**（M29）：`ASTNode` 基类只保留 `Parent`/`Span`，共有 `Children`/`Annotations` 删除——顶层条目挂 `RootASTNode.Declarations`、块语句挂 `CodeBlockASTNode.Statements`、类型成员挂各类型节点 `Members`（均标 [ChildAstNode]，Validator/ASTVisitor/Serializer 零改动）；注解列表仅 7 种声明节点持有，经 `IWrapperAttachable` 访问，并按 SYNTAX §14 三类目标以 `IEntity/IMethod/IValueWrapperAttachable` 分类标记（挂载校验留待语义阶段）；`DeclarationParserLayer` 构造函数改收 `(parent, targetList)`
- **Span 左闭右开**（M31）：所有 `CharRange`（token 与 AST 节点 span）统一为 `[Start, End)`——Start 指向首个字符，End 指向最后一个字符的下一位置；相邻 token 首尾相接，EOF 为零宽范围
- **续行规则**（M31，SYNTAX §1.1）：`()`/`[]` 未闭合时换行按空白处理——实参/索引/形参列表层全状态跳过；`ExpressionParserLayer.insideParens` 括号语境（分组/实参/条件/迭代）在结构等待态透明化换行，右操作数与一元操作数继承
- **数字字面量单源**（M31）：`Parser/NumericLiteral.cs` 是进制（0x/0b/0o）/下划线/后缀判定与解析的唯一实现，Literal/Root/Expression 三层共用
- **JSONL v2 与往返**（M31）：AST JSONL 字段名键控不依赖顺序；carrier（ImportItem）记录化（独立产行、标量字段入 fields）；`AstJsonlDeserializer` 完整反序列化（三阶段：类型定位/实例挂接/回填，产物强制过 Validator），Parse→Serialize→Deserialize→Serialize 往返逐行一致
- **测试基建单源**（M31）：`Tests/AstDescribe.cs`（统一 AST 描述器）与 `Tests/TestHarness.cs`（统一驱动+断言）是全部套件的唯一描述/驱动实现；断言对象约定：除查的就是命令行/日志/token 流/层协议行为的套件外，一律断言 AST 树产物（描述串 + 结构断言）
- **值块统一与裸 return 边界**（M33）：if/switch 表达式分支体与 lambda 体统一为 CodeBlockASTNode——「单表达式分支隐式取值」是「块内恰好一条 ExpressionStatement」的语义规则，解析层无特判（取值留待语义阶段）；多语句值块经 `return@_`（匿名默认标签）/ `return@标签`（`named` 命名）取值；lambda 是裸 return 边界——`CodeBlockParserLayer.allowBareReturn` 标记沿施工链（if/循环/try/seq/switch 子块与表达式深处的分支体）全链传染，lambda 体一律下传 false，遇无 @标签 return 抛 ParserException；副作用：值块内的 if/switch 一律按语句分发，作值须写 `return@_ if ...`

---

## 5. 下一步计划

**Parser/PDA 大扫除（M23）已完成**：控制流系统与 AST 施工系统分离，
施工目标协议、ExpressionRootASTNode、EOF 正式化、AST 完整性验证全部落地。

**AST 结构标注与 Validator 重写（M24）已完成**：结构关系以
[ChildAstNode]/[ParentAstNode]/[AstCarrier] 显式标注，Validator 改为
Attribute 驱动并新增父子指针一致性校验，借此修复 5 个历史结构 bug；
ASTNodeType 枚举删除，节点类型判断全面改用 CLR 类型。

**日志系统与 AST JSONL 序列化（M26）已完成**：Logger 统一日志出口、
verbose 默认关闭（`--verbose` 子命令打开），`--log-to` 全量 JSONL 落盘；
AST 树可经 `compile --dump-ast` 序列化为 JSONL 供诊断。

**CLI 插件化重构（M27）已完成**：交互菜单删除，用法统一为
`<COMMAND> [--sub-cmd [args...]...]`（help/compile/test 三个 COMMAND）；
选项以 `CommandLineMask` 自描述、插件化注册，帮助文本程序生成；
CI 入口改为 `dotnet run -- test --all`。

**Lexer 位置修复 + AST Source Span + ASTVisitor（M28）已完成**：
Lexer 的 offset/列号/token 头/EOF 冲刷位置全部修复，sourceName 单源化到
CharRange；每个 AST 节点携带源码范围 Span（层目标由主循环经 ISpanReceiver
回填、层内节点显式设置、ExpressionRoot 透明继承），JSONL 输出 span 键；
Validator 与 Serializer 遍历统一为 ASTVisitor，Validator 新增 span 校验与
「未标注 AST 成员」类型审计。

**AST 容器重构（M29）已完成**：`ASTNode` 基类共有的 `Children`/`Annotations`
删除，只留 `Parent`/`Span`；子节点容器下放为语义字段——
`RootASTNode.Declarations`、`CodeBlockASTNode.Statements`、5 个类型节点各自
`Members`；注解列表仅 7 种声明节点持有，按 SYNTAX §14 三类 wrapper 目标
抽象为 `IWrapperAttachable` + `IEntity/IMethod/IValueWrapperAttachable` 接口。

**Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档清理（M30）已完成**：
`Core/Utilities.cs` 按语义拆为 7 个文件（`Lexer/Tokens.cs`、`Lexer/Notations.cs`、
`Parser/Keywords.cs`、`Core/Exceptions.cs`、`AST/ASTNode.cs`、`AST/SymbolNodes.cs`、
`AST/ImportNodes.cs`），同命名空间纯搬移、零调用点改动；ASTVisitor 的
`VisitNode`/`EnumerateChildren` 改为 virtual（默认仍走 [ChildAstNode] 反射）；
文档中已删除代码的引用（FrontendTypesExtension 全系、AcquisitionExpressionASTNode、
CharLiteralASTNode 等）与过时表述已清理，`FRONTEND_ARCHITECTURE.md` 删除。

**前端大修（M31）已完成**：全量 review 驱动的 40+ 项修复——测试基建统一
（AstDescribe/TestHarness，21 套件迁移 + 2 新套件）、Span 左闭右开、
Lexer 块注释重写（吞字符修复 + 换行不吞）、续行规则、位运算符、
0b/0o/下划线字面量（NumericLiteral 单源）、Keywords 大扫除（幽灵词清除 +
保留字补齐）、修饰符组合与标识符合法性校验、JSONL v2（carrier 记录化）+
完整反序列化器往返无损、Validator Required 子节点与基类链审计、
Logger 改走 stderr。详见「里程碑历史」M31 段落。

**多行字符串（M32）已完成**：SYNTAX §3.3 多行字符串规范定稿
（Swift 风格严格多行：开界 `"""` 后换行剥除、闭界独占一行且其缩进为剥除基准、
转义与单行一致），Lexer 新增 `QuoteLexerLayer` 引号分流（`"`/`""`/`"""` 统一入口，
与 `SlashLexerLayer` 同模式）与 `MultilineStringLexerLayer` 两阶段施工
（原文按行缓冲，闭合时先剥缩进再统一转义），转义表收编为 `StringEscape` 单源；
插值标记改词法期判定（`StringToken.HasInterpolation`，修复 `\${` 误报）；
Parser/AST 经 `StringToken` 复用近零改动。

**值块统一（M33）已完成**：if/switch 表达式分支体与 lambda 体统一为
CodeBlockASTNode（多语句 + `return@_`/`named` 取值，单表达式分支隐式取值规则不变、
下沉为语义规则）；switch 语句形态落地（新 `SwitchStatementASTNode`，两形态强制
default）；lambda 体内裸 return 成为编译错误（`allowBareReturn` 标记全链传染）；
seq 匿名默认标签从 `seq` 迁移为 `_`（纯注释与快照迁移，解析层无特判）。
详见「里程碑历史」M33 段落。

**技术债清扫（M34）已完成**：字符字面量落地（`CharLexerLayer` 三态状态机 +
`CharToken` + `CharLiteralASTNode`，转义复用 `StringEscape` 单源）；复合赋值
10 运算符（`CompoundAssignmentExpressionASTNode`，ExpressionParserLayer 遇
op+`=` 在左操作数 Attach 前定形构造，红线合规）；`is` 右侧 enum case
（`TypeCheckExpressionASTNode` 的 `TargetType`/`TargetCase` 双字段互斥，
仅 `is` 允许 `.`，switch 模式匹配同路径通吃）；wrapper `.name` 保留参数名
（ParameterListParserLayer `DotNameExpected` 状态，Name 原样存 `.name`）；
import `{}` 列表项单标识符禁令规则化报错（SYNTAX §15.2）。详见「里程碑历史」
M34 段落。

**下一阶段**：语义分析、BIL 输出（见 `../BIL_STANDARD.md`）

---

## 6. 技术债务与已知限制

1. namespace 的唯一性与位置约束（应在文件首部）未校验，留待语义阶段
2. 实参位置的 `a.b` 存在 MemberAccess/Symbol 双形态（具名判别 seed 路径与其他位置 AST 形状不同，语义分析需双路径处理；统一留待语义阶段）
3. `3.`/`3.foo` 在 M31 起为编译错误（点后缺数字；`3.foo` 形态规范未定义，需要成员访问时请写 `(3).foo`）
4. 值块取值规则（M33 起解析层无特判）：「多语句值块所有路径必须显式 return@、落到块尾即编译错误」「单 ExpressionStatement 块隐式取值」「if/switch 表达式分支体类型一致」均留待语义阶段校验
5. 复合赋值的语义推导（`a op= b` 按 §13.2 从对应运算符自动展开/调用）留待语义/后端阶段；M34 起解析层已接受全部 10 个运算符
6. `.name` 保留参数名（M34 起解析层接受，Name 原样存 `.name`）的上下文约束（仅 method wrapper canonical 形态可用）与语义规范化留待语义阶段
7. 编译 0 警告（大扫除消除了原 `Core/Utilities.cs` 的 nullable 警告）

---

## 7. 里程碑历史

### 2026-07-28 · M34 技术债清扫：字符字面量 / 复合赋值 / is enum case / `.name` 参数 / import 禁令

> 集中清扫 §6 技术债清单中的 5 项解析层缺口（规范依据：§3.3 字符规则定稿、
> §13.2 复合赋值既有、§12.3 is enum case 既有、§14.4 `.name` 既有、§15.2 禁令新增）。

- **字符字面量**：Lexer 新增 `CharLexerLayer`（三态状态机
  AwaitContent/EscapeSeen/ContentSeen，`BaseLexerLayer` 遇 `'` 分流，
  与 StringLexerLayer 同文件同风格）；新增 `CharToken`（TokenType.Char，
  char 无插值概念、不复用 StringToken）；AST 新增 `CharLiteralASTNode`；
  转义复用 `StringEscape` 单源；`''`/`'ab'`/未知转义/未闭合（含 EOF 冲刷帧
  虚拟换行）/反斜杠后换行均编译错误；JSONL 两端加 char ↔ 单字符字符串分支
  （往返必炸点：Deserializer 的 IsPrimitive 原走 GetInt64）；LexerFuzz 固定
  用例同步（原「'A' 未实现」错误断言反转）；RootParserLayer 顶层分派补
  `case CharToken`（ParseFirstDecl 必经之路）
- **复合赋值（§13.2，10 个全集）**：新增 `CompoundAssignmentExpressionASTNode`
  （Target/Operator/Value，Operator 不含 `=`）；`ExpressionParserLayer.
  HandleOperatorSeen` 在 pending 运算符后遇 `=` 且组合属全集时构造节点——
  左操作数仍在层内未挂载（Attach 只在 CompleteExpression 执行），一次性
  Attach 无替换、红线合规；`>>>=` 与 `>=`/`>>`/`>>>` 比较重组天然有序共存；
  非全集组合（如 `== =`）不再特判、落正常二元流程报错；复合赋值整体是
  表达式节点（普通赋值 `a = b` 维持 ExpressionStatement 双 Root 槽现状）；
  M31 的两个「尚未支持」错误用例反转为正例
- **`is` 右侧 enum case（§12.3）**：`TypeCheckExpressionASTNode` 改
  `TargetType`（可空，填充时创建——预创建会留无 span 空壳过不了 Validator）
  与 `TargetCase` 双字段互斥；`HandleTypeOperatorSeen` 仅 `is` 遇 `.` 分流
  走既有前导点逻辑（`EnumCaseExpressionASTNode` 加 parent 构造参数，
  归属即定）；`as`/`as?`/`supers`/`with` 右侧仍只收类型；switch 模式匹配
  `(_ is .Success)` 同路径通吃
- **wrapper `.name` 保留参数名（§14.4）**：`ParameterListParserLayer` 新增
  `DotNameExpected` 状态，`.` 后必须是标识符（`.123`/裸 `.` 报错）；
  `ParameterASTNode.Name` 原样存 `.name`（最小侵入，描述/JSONL 零改动）；
  解析层不限制上下文（语义阶段约束）；`operator .proxy.call(.name: String,
  args: named Any...): Any` canonical 全形解析通过
- **import `{}` 禁令（§15.2）**：`ImportParserLayer.OnAfterListItem` 遇 `.`
  报规则化错误（列表项只能是单标识符、不同子路径写多条 import）；
  顺手修正类注释与实际行为不符处（SymbolLayer 空名元素不残留）
- **测试**：895/895 + fuzz 6000（30 套件；Literal +23、Expression +28、
  ParameterList +5、TypeDeclaration +1、Import +1）

### 2026-07-28 · M33 值块统一：多语句分支体 + switch 语句形态 + lambda 裸 return 边界

> 依据 SYNTAX §5.1/§6.1/§7.1/§7.2 三项规范变更（值块统一）落地：
> 值块（seq 块、if/switch 表达式分支体、lambda 块体）形态与取值规则统一，
> 匿名默认标签为 `_`；lambda 体内裸 return 成为编译错误。

- **if/switch 表达式分支体统一为代码块**：`IfExpressionASTNode.ThenExpression/
  ElseExpression` → `ThenBody/ElseBody`（CodeBlockASTNode，创建即定）；
  `SwitchCaseASTNode.Body`、`SwitchExpressionASTNode.DefaultBody` 同样块化；
  分支体改由 `CodeBlockParserLayer` 施工。「单表达式分支隐式取值」下沉为
  「块内恰好一条 ExpressionStatement」的语义规则（解析层无特判，取值留待
  语义阶段）；多语句分支体经 `return@_`（匿名默认标签）/`return@标签` 取值
- **named 标签**：if/switch 头部 `)` 后、lambda `->` 后可写 `named 标识符`；
  `IfExpressionASTNode`/`SwitchExpressionASTNode`/`LambdaExpressionASTNode`
  新增 `Label string?`（写法参照 `SeqBlockParserLayer.HandleNamed`）
- **switch 语句形态**：新增 `SwitchStatementASTNode`（与 IfStatementASTNode
  对称，Selector/Cases/DefaultBody，分支体一律代码块）；
  `SwitchStatementParserLayer` 实现双模式（语句模式从 switch 关键字进入，
  表达式模式关键字已由 ExpressionParserLayer 消费）；
  `CodeBlockParserLayer.HandleStatementDispatch` 新增 SWITCH 路由；
  两形态统一强制 default（规范同步修订），Validator 的 default 规则
  对称覆盖新节点。副作用：语句位置的 switch 不再落成表达式语句
- **lambda 体双字段互斥**：`->` 后遇 `{` → 块形态（新 `BlockBody`
  CodeBlockASTNode），否则维持单表达式（`Body` ExpressionRoot 改可空，
  创建时定，参照 ExpressionStatementASTNode 双 Root 槽先例）；两字段互斥
- **lambda 体内裸 return 编译错误**：`CodeBlockParserLayer` 新增
  `allowBareReturn` 构造标记（默认 true），遇无 @标签 return 且标记为
  false 时抛 ParserException；标记沿施工链全链传染（lambda 体内的
  seq/if 语句块/循环体/try/switch/变量初始化/表达式深处的 if/switch
  表达式分支体——ExpressionParserLayer 及 If/Switch/Loop/TryCatch/Seq/
  VariableDeclaration/ArgumentList/TypeOf 各层逐一传递）；lambda 块体
  与单表达式体一律下传 false（lambda 是边界，内嵌 lambda 仍是 false）；
  if/switch 表达式分支体继承父上下文标记（不是 lambda 边界）
- **return@_ 迁移**：seq 匿名默认标签 `seq` → `_`（SYNTAX §6.1）；
  纯注释与测试快照迁移（标签只是字符串，解析层无特判）
- **行为变化（规范使然）**：值块内的 if/switch 一律按语句分发——
  `if (a) { if (b) {1} else {2} } else {3}` 的 then 分支是 IfStmt，
  内层 if 作分支值须写 `return@_ if (b) {1} else {2}`
- **同步面**：AstDescribe 描述器（If/Switch/Lambda/SwitchStmt 块化 + named）、
  AstJsonlSerializer/Deserializer（反射驱动，新节点/新字段零改动接入，
  往返套件补 switch 语句 + 多语句分支 + lambda 块体用例）
- **测试**：838/838 + fuzz 6000（30 套件；IfExpression/SwitchExpression/
  Lambda 套件迁移并扩充：多语句分支 return@_、named + return@标签、
  switch 语句形态（含多语句分支与边界交还）、lambda 块体、裸 return
  报错（直接/嵌套 seq/if/循环/switch/单表达式体分支/内嵌 lambda）；
  无新增测试类，用例全部加进现有套件）

### 2026-07-28 · M32 多行字符串 `"""`：规范定稿 + 全栈落地

> 技术债清理：SYNTAX §3.3 的多行字符串从「仅列示例」到规范定稿 + 实现。
> 规范语义经语言设计者确认（此前按「不猜测规范」原则挂起，见 M31 记录）。

- **规范定稿（SYNTAX §3.3）**：Swift 风格严格多行——开界 `"""` 后必须紧跟换行
  （剥除，同行写内容即编译错误）；闭界 `"""` 必须独占一行（其前仅空白，
  闭界前换行不属于内容）；闭界行缩进量 = 剥除基准，内容行前导空白不足即
  编译错误，全空白内容行输出空行；转义与单行同一套（剥除先于转义），
  行内 `"`/`""` 免转义，内容中的 `"""` 须写 `\"""`（闭界出现在内容行中间
  即编译错误）；内容换行恒为 `\n`（行尾归一在词法入口完成）；
  `${}` 插值与单行一致
- **Lexer 引号分流**：新增 `QuoteLexerLayer`——`"` 家族统一入口
  （与 `SlashLexerLayer` 同模式：按第二/三字符分流单行串 / 空串 `""` /
  多行 `"""`，字符串层以持有实例转发、同步弹出）；
  `BaseLexerLayer` 不再直推 `StringLexerLayer`
- **`MultilineStringLexerLayer` 两阶段施工**：原文按行缓冲（反斜杠只用于让
  `\"` 不参与引号计数，不展开转义），闭合时先剥缩进、再统一处理转义；
  反斜杠后紧跟真实换行立即报错（不支持行接续）；未闭合报
  "Unterminated multi-line string literal"（冲刷帧栈检查，单行串行为不变）
- **转义表单源化**：`StringEscape.TryProcess` 静态表供单行/多行共用，
  `StringLexerLayer` 内联 switch 收编
- **Parser/AST 近零改动**：多行字符串产出复用 `StringToken`，
  `StringLiteralASTNode`/JSONL 序列化路径完全不动
- **插值标记词法期判定（顺带修复）**：`StringToken.HasInterpolation` 由字符串层
  在转义处理时判定（未转义的 `$` 后紧跟 `{` 才算；`\$` 转义的字面 `$` 不构成
  插值引导，多行内 `${` 须同行相邻），`LiteralParserLayer` 不再用
  `Content.Contains("${")` 猜测——修复 `\${` 误报插值（单行串既有 bug，
  转义信息在 Content 拼装后已丢失，Parser 侧无法回补）
- **测试**：790/790 + fuzz 6000（30 套件；新增 MultilineString 套件 43 例：
  内容拼装 / 错误路径 / 引号分流回归 / token span / 插值标记 / AST 六组）

### 2026-07-28 · M31 前端大修：全量 review 驱动的 40+ 项修复

> 不改 Latte 语法；对 Lexer/Parser/AST/Core/Tests 五层做了一次全量 review
> （5 路并行 + 端到端复现验证），修复全部确认 bug 与改进建议，
> 并落地三项硬性要求：Span 左闭右开统一、JSONL 字段名键控 + 完整反序列化器、
> 测试基建统一。计划与执行的八个阶段：A 测试基建 → B Span → C Lexer →
> D 表达式/字面量 → E 声明层/关键字 → F Core/AST 基建 → G 文档 → H 回归。

- **测试基建统一（阶段 A，安全网先行）**：
  - 新建 `Tests/AstDescribe.cs`（统一 AST 描述器，替代 13+ 份分叉方言：
    `Access(obj, .m)`/`MemberAccess(obj.m)`、`var x = ...`/`Var(x = ...)` 等收敛为
    单一信息无损格式）与 `Tests/TestHarness.cs`（统一 Parse 驱动 +
    Check/CheckTrue/CheckParseError 断言与计数）
  - 21 个套件全部迁移到统一基建；断言对象约定：除查的就是命令行/日志/
    token 流/层协议行为的套件（Logger/CommandLineParser/LexerFuzz/TokenDisposition）
    外，一律断言 AST 树产物（描述串 + 结构断言）
  - 弱断言修复：`LiteralParserTests`（原只查节点类型名）与
    `VariableDeclarationTests`（原第一条件恒假）改为全串精确比对 + 结构断言；
    `TypeReferenceParserTests` 占位测试重写为 17 个真实用例；
    新增 `PathParserLayerTests`（16 例）、`ArgumentListParserLayerTests`（12 例），
    注册为套件 28/29；结构断言（Root 填充/Parent 链/无共享）普及到表达式类套件
  - `test --all` 退出码 clamp 到 255（防 Unix 8 位退出码回绕假绿）
- **Span 左闭右开统一（阶段 B）**：所有 `CharRange` 改为 `[Start, End)`——
  Start 指向首个字符，End 指向最后一个字符的下一位置
  （`Lexer.ContextImpl.PushToken` 经 `Advance` 计算；Parser 层 span 自然继承
  token 开区间；EOF 保持零宽）。相邻 token 首尾相接；Validator/序列化器/
  文档同步声明。位置断言全部更新（LexerFuzz.TestPositions）
- **Lexer 修复（阶段 C）**：
  - 块注释层重写：删除无规范依据的反斜杠转义机制（修复 `/* a*b */` 吞 `*`、
    `/* a\b */` 吞 `\`）；换行不吞——注释按行分段、换行以 LineBreakToken 入流
    （与行注释一致，两条语句间唯一的分隔换行在块注释内时语句分隔不丢失）
  - 字符字面量 `'` 由 Base 层明确报错（此前被静默当字符串收下）；
    多行字符串 `"""` 仍未实现（已知限制，规范语义未定义不猜测）
  - 行尾归一手写化：只把 `\r\n`/`\r` 归一为 `\n`（ReplaceLineEndings 此前会
    误伤字符串内的 `\f`/`\x85`/`\u2028`/`\u2029`）
  - 复合赋值策略统一：Lexer 不再合并 `*=`/`/=`（与 `>=` 同策略拆 token，
    将来 Parser 重组）；删除 `++`/`--`（规范不存在）与 `#` 命名误导常量
  - 未闭合字符串/块注释错误信息友好化（不再暴露内部层类名）
- **表达式与字面量修复（阶段 D）**：
  - 续行规则落地（SYNTAX §1.1）：`()`/`[]` 未闭合时换行按空白处理——
    调用/索引/形参列表全状态跳过换行；ExpressionParserLayer 新增
    `insideParens` 括号语境（分组/实参/条件/迭代表达式，右操作数与一元操作数继承）
  - 位运算符 `<<`/`&`/`|`/`^` 接入（`>>`/`>>>` 维持重组）；删除 `in` 二元运算符
    （只属于 for 循环头）；复合赋值遇 `=` 报「尚未支持，请展开为 a = a op b」
  - 数字字面量补全（SYNTAX §3.3）：`0b`/`0o` 前缀、下划线分隔
    （不连续/不开头结尾）；判定收敛为 `Parser/NumericLiteral.cs` 共享 helper
    （原三份规则不一致）；`IntLiteralASTNode.IsHex` 布尔改为
    `LiteralIntBase` 枚举（Decimal/Hex/Binary/Octal）
  - `3.`/`3.foo` 吞点修复：点后非数字明确报错（不再吞 `.` 伪装成员访问）；
    `a[]` 空索引拒绝（`foo()` 空参保持合法）；`throw` 后换行报错（与
    return/yield 行为一致）；`in` 从二元运算符移除后 `a in b` 在表达式位置报错
  - PathParserLayer 吞尾修复：`foo.` + 换行/EOF、`List\<i32` + EOF 报错
    （此前静默吞并）；`foo..bar` 双点报错（类型/参数语境经
    `allowVariadicDots` 保留 `...` 交还）；import 尾点报错自然获得
  - Span 一致性：表达式形态 if/switch/typeOf/lambda 的 span 含起始关键字；
    Loop/If 终态不消费换行（span 不拖尾换行符，无 else 的 if 经
    ElseCheckEntryEnd 封 End）；变量声明初始化后换行改 Replay（同语法
    不再两种 span）；EOF 规则 6 字面合规（结构完整态 Pop(Replay)）
- **声明层与关键字修复（阶段 E）**：
  - Keywords 大扫除（对齐 SYNTAX §19）：删除
    elif/foreach/when/case/private/public/final/extension/base 幽灵词
    （`private class Foo {}` 等不再被静默接受）；保留字数组补齐
    switch/return/break/continue/throw/yield/do/to/in/supers/with/typeOf/await/
    self/seq/using/import/namespace；import/namespace 移出声明路由数组
    （修复路由遮蔽）
  - 修饰符组合校验：rich 仅 struct/enum struct；shared 仅 class 或 rich struct；
    open 仅 class/struct（enum struct 明确禁止）；open+abstract 互斥；
    重复修饰符报错；`enum` 后必须 `struct`
  - 标识符合法性统一：`Keywords.IsIdentifierStart/IsIdentifier/IsReservedKeyword`
    共享实现，铺到变量名/参数名/类型名/callable 名/循环变量/catch 参数/
    using 绑定/enum case 名/成员名/符号路径元素/import 列表项
    （`class 123 {}`、`func f(123: i32)`、`for (123 in c)`、`var return = 5`、
    `foo(return = 5)` 全部报错；标签位置只查首字符——return@seq 合法）
  - 注解名空校验（`@(1)`/`@`+换行报错）；`named` 必须带 `...`；
    enum 判别值支持 0x 等进制与 long 范围（`DiscriminantValue` int?→long?）
- **Core/AST 基建（阶段 F）**：
  - Logger 控制台输出改走 stderr（诊断不污染 stdout——`compile --parse-only`
    的 JSONL 输出流纯净）
  - JSONL 序列化修复：int?/long? 等 Nullable 字段不再静默丢弃；
    严格先按声明类型过滤再取值（未填充属性不再被提前求值）；循环保护；
    字段枚举沿基类链（基类 private 字段反射盲区修复）
  - **JSONL v2 + 完整反序列化器**：carrier（ImportItem）记录化
    （独立产行、标量字段入 fields——修复 importAll 往返必丢）；
    新建 `AST/AstJsonlDeserializer.cs`：三阶段重建（类型定位/实例挂接
    （预创建子容器复用、carrier struct 回写）/fields 与 span 回填），
    字段名键控不依赖顺序，产物强制过 ASTIntegrityValidator；
    往返测试（Parse→Serialize→Deserialize→Serialize 逐行一致）12 例
  - Validator 补强：`[ChildAstNode(Required = true)]` 必需子节点校验
    （LiteralExpressionASTNode.literal、CatchClauseASTNode.ExceptionType）；
    类型审计沿基类链；[AstCarrier] 类型递归审计（carrier 装 ASTNode 的
    属性会逃出遍历，拒绝）
  - CompileCommand：CompilerInternalException 单独报告（与用户语法错误区分）；
    多文件 dump/parse-only 输出 `{"file":...}` 元记录分隔；全失败不再误报 dumped
  - import 多导入形态 SymbolElement 按引用共享改深拷贝（防语义阶段交叉污染）
- **记录在案的技术债务（本轮不改代码）**：实参位置 `a.b` 的
  MemberAccess/Symbol 双形态；`is` 右侧 enum case（§12.3）；method wrapper
  `.name` 保留参数（§14.4）；switch 语句形态（规范未定义）；多行字符串
  `"""`（规范语义未定义）；复合赋值语义实现（token 策略已统一）；
  三态合并与 ExpectNotation 提取经评估不更简洁，放弃
- **测试**：746/746 + fuzz 6000（29 套件；新增 Path/ArgumentList 两套件，
  AstJsonlSerializer 扩至 84 例含往返与格式 v2）

### 2026-07-27 · M30 Utilities.cs 拆分 + ASTVisitor 遍历可重载 + 文档幽灵清理

> 不改 Latte 语法；把 `Core/Utilities.cs` 按语义拆分为 7 个文件，
> ASTVisitor 遍历逻辑开放重载，并清理文档中已删除代码的引用与过时表述。

- **Utilities.cs 拆分**（同 `LatteCompiler` 命名空间纯搬移，零调用点改动，
  原文件删除）：
  - `Lexer/Tokens.cs`：Token 基类 + TokenType + 6 个 token 类
    （顺带修正 EndOfFileToken 上 M25 前的过时注释——EOF 现由 Lexer 追加）
  - `Lexer/Notations.cs`：符号常量；`Parser/Keywords.cs`：关键字常量
    （使用方 100% 在 Parser）
  - `Core/Exceptions.cs`：LexerException / ParserException
  - `AST/ASTNode.cs`：ASTNode 基类 + RootASTNode；
    `AST/SymbolNodes.cs`：Symbol 家族 + SymbolASTNode；
    `AST/ImportNodes.cs`：ImportASTNode + [AstCarrier] ImportItem
  - 原文件的幽灵 using（`System.Linq`/`System.Threading.Tasks`）随之清除
- **ASTVisitor 遍历可重载**：`VisitNode`（遍历骨架）与 `EnumerateChildren`
  （子节点来源）改为 protected virtual，默认仍走 [ChildAstNode] 反射；
  Validator/Serializer/测试零改动
- **文档幽灵清理**：
  - `docs/compiler/frontend/FRONTEND_TYPES.md` 全量修订：删除幽灵引用——
    `Core/FrontendTypesExtension.cs` 全系（SymbolTable/SymbolInfo/TypeInfo/
    SemanticException 等，代码已不存在）、AcquisitionExpressionASTNode、
    CharLiteralASTNode；修正过时表述（enum case 列表与 wrapper proxy 的
    「待实现」标注、阶段标记），文件位置更新到 M30 拆分后布局，
    Token 表补 EndOfFileToken 行
  - `docs/compiler/frontend/FRONTEND_ARCHITECTURE.md` 删除：全文围绕已删除的
    FrontendTypesExtension.cs，残余内容与 AGENTS.md §3、FRONTEND_TYPES 重复
  - `PARSER_ROADMAP.md` 头部加注：正文为大扫除前原始计划记录，代码草图勿照搬
  - `AGENTS.md`/`CLAUDE.md` 同步新文件布局，移除「Utilities.cs 残留」条目
- **测试**：27 套件全绿（用例无增删）+ fuzz 6000；build 0 错误 0 警告

### 2026-07-27 · M29 AST 容器重构：语义字段 + wrapper 挂载接口

> 不改 Latte 语法；纯 AST 结构重构。基类共有字段 `Children`/`Annotations`
> 删除，子节点容器改为各节点上语义明确的 [ChildAstNode] 字段；注解挂载
> 能力按 SYNTAX §14 三类 wrapper 目标抽象为接口。

- **基类瘦身**（`Core/Utilities.cs`）：`ASTNode` 只保留 `Parent`
  （[ParentAstNode]）与 `Span`；`Children`、`Annotations` 两个共有字段删除
- **语义容器字段**（均标 [ChildAstNode]；Validator/ASTVisitor/AstJsonlSerializer
  走 Attribute 反射，零改动）：
  - `RootASTNode.Declarations`：顶层条目（全局声明/import/namespace/顶层字面量）
  - `CodeBlockASTNode.Statements`：块内语句（局部声明同挂）
  - 5 个类型声明节点（Class/Interface/Struct/EnumStruct/Wrapper）各自的 `Members`
- **wrapper 挂载接口**（`AST/DeclarationNodes.cs`）：`IWrapperAttachable` 基接口
  （`Annotations` 属性）+ 三个分类标记接口——`IEntityWrapperAttachable`
  （5 个类型节点，§14.2）、`IMethodWrapperAttachable`（Callable，§14.4）、
  `IValueWrapperAttachable`（Variable，§14.3）；挂载合法性校验留待语义阶段
- **DeclarationParserLayer**：构造函数改收 `(ASTNode parent, List<ASTNode> target)`
  （parent 供节点 Parent 指针、target 供挂接；与 ArgumentListParserLayer 收
  目标列表的先例一致）；新增 `GetMembers` 集中 switch（仿 `GetModifiers`）；
  `AttachAnnotations` 改经 `IWrapperAttachable` 访问
- **测试**：19 个测试文件机械改名（`root.Declarations`/`block.Statements`/
  类型节点 `.Members`），无用例增删、无期望变化；AstJsonlSerializerTests 的
  via 期望串同步（`Children[0]` → `Declarations[0]`）；全部 27 套件 0 失败
  + fuzz 6000/6000

### 2026-07-27 · M28 Lexer 位置修复 + AST Source Span + ASTVisitor 统一遍历

> 不改 Latte 语法；修复 Lexer 位置计量的五个 bug，给每个 AST 节点挂上
> 源码范围 Span（JSONL 同步输出），并把 Validator 与 Serializer 的遍历
> 统一为 ASTVisitor 基建，Validator 新增 span 校验与类型审计。

- **Lexer 位置修复**（`Lexer/Lexer.cs`）：
  - `CharPosition.offset` 恒为 0 —— 主循环从未把字符索引写进位置（已修，
    0 起始字符索引）
  - 普通 token 的 `CharRange.sourceName` 从未设置（仅 EOF 有）；sourceName
    单源化到 `CharRange.sourceName`，`CharPosition.sourceName` 删除
  - 换行即切下一行 col 1 —— 第二行起所有列号 +1（已修：换行算当前行
    最后一列，下一行首字符 col 1）
  - 空白字符被记为 token 头 —— 缩进行 token 的 Start 落在前导空格上
    （已修：token 头跳过空白，换行除外——它是 LineBreakToken）
  - EOF 冲刷帧不占位置 —— EOF 处 Word/Slash 层 token 的 End 少算一个
    字符、换行后甚至范围倒置（已修：虚拟换行占末尾虚拟位置）
- **AST Source Span**：`ASTNode.Span`（`CharRange?`）。填充分两级——
  层目标由 Parser 主循环按层栈跟踪 token 流计算，层弹出时经新接口
  `ISpanReceiver.ReceiveSpan` 回填（约定 `target.Span ??= span` 只填空；
  换行处弹出不拖尾换行符）；层内自建节点由所在层显式设置（创建记
  Start、完成经 `ParserLayerContext.GetPreviousLocation()` 封 End；
  ExpressionParserLayer 以后缀包装/运算符/完成三处封口）。
  `ExpressionRootASTNode` 覆写 Span getter 透明继承内容表达式
  - **JSONL**：`AstJsonlSerializer` 每节点一行新增 `span` 键
    （`{source,startLine,startCol,startOffset,endLine,endCol,endOffset}`）
  - **Validator 新检查**：每节点 span 非空、sourceName 非空、End 不早于
    Start；类型审计——装 ASTNode 的成员（字段/自动属性，经 backing 字段
    识别）必须带 [ChildAstNode]/[ParentAstNode]，[ChildAstNode] 标在非
    AST 成员上同样拒绝
- **ASTVisitor 统一遍历**（`AST/ASTVisitor.cs`）：[ChildAstNode] 子节点
  枚举的唯一实现（含 carrier 下钻、集合下标 via），ASTIntegrityValidator
  与 AstJsonlSerializer 各删一份重复反射；via 格式统一为
  `member[i]`/`member[i](Carrier.Field)`
- **测试**：LexerFuzzTests 新增位置精确性用例 3 例 + 不变量扩展
  （sourceName 非空、offset 不回退、范围不颠倒）；ASTIntegrityValidatorTests
  新增 span 破坏/类型审计用例 5 例；AstJsonlSerializerTests 新增 span 键
  结构断言 4 例；全量 556/556 + fuzz 6000 通过

### 2026-07-27 · M27 CLI 插件化重构（help/compile/test）+ 交互菜单删除

> 不改 Latte 语法；把 M26 的平铺 `--选项` 参数与用户交互菜单统一重构为
> `<COMMAND> [--sub-cmd [args...]...]` 结构，选项全部插件化、帮助程序生成。

- **用法定稿**：`dotnet run -- <COMMAND> [--sub-cmd [args...]...]`，
  COMMAND 三个——`compile`（`--file <路径...>`、`--parse-only`、
  `--dump-ast <路径>`、`--verbose`、`--log-to <路径>`）、`test`（`--all`、
  `--run [编号...]`、`--verbose`、`--log-to`）、`help`（无参概览；
  `help compile` 单 COMMAND；`help compile.file` 单个子命令，名不带 `--`）
- **CommandLineMask**（Core/CommandLine.cs）：选项自描述元数据——Name/
  Description/ArgsHint/MinArgs/MaxArgs（int.MaxValue 表任意个数）/
  MutuallyExclusive；解析器（`--x=v` 与空格两形态、个数/互斥/重复/游离
  参数校验）与 help 文本完全由注册表数据驱动，无手写帮助页
- **插件承载行为**：COMMAND 插件带 `Execute`（解析结果 → 退出码）；
  `--verbose`/`--log-to` 是 compile 与 test 共享的子命令插件类；
  `--all` 与 `--run` 互斥；`test` 裸用或 `--run` 无编号打印套件菜单
- **交互菜单删除**：Program.cs 从 304 行瘦身为 19 行薄入口（解析→分发→
  退出码）；`--test-all` 更名 `test --all`，`--enable-verbose` 更名
  `--verbose`；`--parse-only` 时 AST JSONL 默认输出到 stdout，
  `--dump-ast` 指定文件；编译错误走 stderr，单文件失败不阻断后续文件，
  退出码非零
- **CI 同步**：`.github/workflows/ci.yml` 改为 `dotnet run -- test --all`
- 测试：544/544（27 套件）+ fuzz 6000/6000；新增 CommandLineParserTests
  （46 用例：注册表完整性、COMMAND/子命令匹配、两形态、参数个数、互斥、
  游离参数、重复子命令）

### 2026-07-26 · M26 日志系统（Logger 分级 + JSONL 落盘）+ AST JSONL 序列化

> 不改 Latte 语法；基础设施大扫除：日志分级与分流（控制台/文件），
> 以及 AST 树的 JSONL 序列化诊断能力。

- **Core/Logger.cs**（唯一日志出口）：`Verbose/Warning/Error` 三级；
  控制台门槛默认 Warning+（verbose 默认关闭），`--enable-verbose` 放开；
  `--log-to PATH` 把**全量**日志（含 Verbose）以 JSONL 落盘
  （`{"ts","level","source","message"}` 每行一条，System.Text.Json 序列化），
  文件不过滤级别——诊断时从一大坨日志里 grep 所需
- **接入点收口**：Lexer/Parser 的 `ContextImpl.Log/LogWarning` 改经 Logger
  输出（此前直接 `Console.WriteLine`）；删除 LexerFuzzTests 的
  `Console.SetOut(TextWriter.Null)` 屏蔽 hack（verbose 默认关闭后无意义）
- **AST/AstJsonlSerializer.cs**：AST 树深度优先序列化为 JSONL，每节点一行
  `{id,parent,via,type,fields}`；遍历复用 Validator 的 [ChildAstNode] 反射
  下钻（含 private 字段、IEnumerable 下标、[AstCarrier] 展开）；fields 收集
  标量成员（Symbol 渲染为点分串），排除 Parent/索引器，先按声明类型过滤
  再取值以避开未填充 ExpressionRoot 的抛异常属性
- **Program.cs 参数解析**：从只认 `args[0]=="--test-all"` 扩为循环解析
  （`--test-all` 单独使用行为不变，CI 不受影响）；新增 `--enable-verbose`、
  `--log-to`、`--dump-ast`（支持 `--name=value` 与 `--name value` 两形态；
  未知参数/缺路径 stderr 提示 + 退出码 2）；`--dump-ast` 在交互菜单
  解析文件成功后写出 AST JSONL
- 测试：498/498（26 套件）+ fuzz 6000/6000；新增 LoggerTests（7 用例：
  JSONL 落盘与级别门控）与 AstJsonlSerializerTests（38 用例：JSON 合法性、
  id/parent 链一致性、via/fields 内容断言）

### 2026-07-26 · M25 Lexer 修复：SlashLexerLayer、Lexer EOF、注释集中跳过 + fuzz 基建

> 不改 Latte 语法；修复 Lexer 三个结构性缺陷（除法不可用、EOF 由 Parser
> 伪造、注释处理散落），并建立 Lexer fuzz 测试基建。

- **SlashLexerLayer**：`/`、`/=`、`//`、`/*` 统一分流入口——M25 前 Base 层
  见到 `/` 后若下一字符非注释开头直接报错，**除法与 `/=` 完全不可用**；
  注释形态转发给持有的注释层实例（Delegate, don't implement），
  注释层弹出时本层同步弹出
- **Lexer 输出 EOF**：`EndOfFileToken` 改由 `Lexer.Tokenize` 在输出末尾追加
  （零长度 CharRange，位于文件末尾），Parser 不再自行伪造（仅对绕过 Lexer
  的调用方保持追加兼容）；输入结束以虚拟换行冲刷帧（FlushLayers）驱动各层
  弹栈、不产生任何 token；冲刷后栈不收敛（未闭合字符串/块注释）即
  LexerException；顺带修复空输入 `content[0]` 越界与 `Tokenize(string)` 的
  AggregateException 包装（改 `GetAwaiter().GetResult()` 原样抛出）
- **注释集中跳过**：CommentToken 由 Parser 主循环分发时统一跳过，各
  ParserLayer 不再自行处理（RootParserLayer 的 Comment 分支已删）；
  行注释不再吞掉结尾换行（keepChar 回流，由 Base 层产出 LineBreakToken——
  此前行尾注释会让下一条语句粘行）
- **LexerFuzzTests**（固定用例 23 + fuzz 6000，固定种子可复现）：
  除法/注释/EOF 精确 token 序列断言；纯随机/结构化片段/合法源码变异三类
  fuzz 校验不变量（不崩——只允许 LexerException、EOF 存在且唯一、
  位置单调不回退）；Parser 集成验证注释任意位置不炸语法
- 测试：453/453（24 套件）+ fuzz 6000/6000

### 2026-07-26 · M24 AST 结构标注 + Validator 重写 + 删除 ASTNodeType + 父子指针 bug 修复

> 本次重构不改 Latte 语法；把 AST 结构关系从「约定」变为「显式标注」，
> Validator 改为 Attribute 驱动，并借此抓出并修复 5 个真实的父子指针 bug
> （根因均为「先解析后决定归属 → 事后搬家」，与大扫除的施工协议相违）。

- **结构标注 Attribute**（AST/ASTStructureAttributes.cs）：`[ChildAstNode]`
  标记装子节点的字段/属性（单节点/节点集合/carrier 集合，含 private 字段如
  ExpressionRootASTNode.expression）；`[ParentAstNode]` 标记父指针
  （ASTNode.Parent）；`[AstCarrier]` 标记携带 ASTNode 的非节点对象
  （ImportItem struct）——配合容纳它的成员上的 [ChildAstNode]，
  Validator 深入其公共字段完成子节点遍历
- **Validator 重写**：遍历只走 [ChildAstNode] 成员；新增父子指针一致性校验
  （每个子节点的 Parent 必须指向持有者，carrier 情形为持有集合的节点）；
  原「Expression.Parent 指向 Root」检查被通用校验覆盖；Root 未填充 /
  节点无共享 / Parent 链无环 / switch default 规则保留
- **彻底删除 ASTNodeType**：枚举本体、ASTNode.NodeType 抽象属性、约 50 处
  override、ASTNodeTypeExtensions（无任何使用）全部删除；节点类型一律用
  CLR 类型判断（is / GetType()）
- **无 reparent 原则**：AttachTo 保持一次性；「归属后知」场景一律改用
  创建时归属即定的结构，禁止任何形式的 Parent 重挂
- **修复 Validator 抓出的 5 个父子指针 bug**：
  1. Range 收养（LoopParserLayer）→ 删除 RangeExpressionASTNode，拍平为
     LoopStatementASTNode.Iterable（起点）+ RangeTo（终点，null = 非范围循环）
  2. Assign 收养（CodeBlockParserLayer）→ 删除 AssignStatementASTNode，
     表达式语句与赋值语句统一为 ExpressionStatementASTNode
     （Expression + AssignValue?，两个 Root 槽创建时 Parent 即定）
  3. 注解 Parent 指向声明的父容器 → 构造时 parent 为 null，声明节点创建时
     一次性 AttachTo（DeclarationParserLayer.AttachAnnotations 统一挂接点）
  4. 泛型约束 Target 搬家（GenericParametersParserLayer）→ StartConstraint
     把符号数据（Symbol 为纯数据）灌进 constraint 自带 Target 节点，
     不再挂接外部已建成节点
  5. 注解实参 Parent 指向父容器 → ArgumentListParserLayer 的 parentNode
     改传注解节点本身
- 测试：430/430（23 套件）；新增 ASTIntegrityValidatorTests（5 用例：
  合法树通过 + Parent 指错/carrier 指错/Root 未填充/节点共享拒绝）；
  各套件 Describe 类型分派更新，快照期望保持不变

### 2026-07-26 · M23 Parser/PDA 大扫除（架构重构，依据 great_clean_plan.md）

> 本次重构只调整 Parser 内部架构、AST 构造协议、Token 流转协议与测试基础设施，
> **不修改 Latte 的任何既有语法与语义**（417 个语法用例逐一保持原断言并通过）。

- **TokenDisposition**：`ParserLayerResult.PushLayer/PopLayer` 的 `bool shouldKeepToken`
  全部机械替换为具名枚举 `TokenDisposition.Consume/Replay`（约 260 处调用点）
- **彻底删除 Layer 返回值**：`IResultProducer`/`IResultConsumer`/`GetResult()`/
  `OnChildResult()`/`pendingResultHandler` 全部删除（grep 验收 0 处）；Parser 主循环
  不再包含任何 AST 结果传递逻辑，Layer 之间只传递控制权
- **施工目标协议**：每个 Layer 的构造函数接收明确、强类型的施工目标；
  父层创建/选择目标并传入子层构造函数，子层原地填充或向目标附加子节点；
  数据流严格单向（父→子），禁止任何形式的回传替代机制
- **ExpressionRootASTNode**：Syntax AST 中所有表达式位置的统一稳定挂载点
  （§6.4 清单 30+ 个字段全部迁移）；一次性 `Attach`、禁止替换、禁止附加已有父节点的
  表达式；可选表达式以 null Root 表示；`ASTNode.Parent` 改为只读（只能设置一次）；
  表达式经「未挂载子树包装」组合，ExpressionParser 只在表达式完成时 Attach 最终外层节点；
  赋值目标与范围起点经「Root 收养」转移逻辑归属（不搬家）
- **GroupExpression 独立 NodeType**：`ASTNodeType.GroupExpression`，
  `ValueExpressionRoot` 专属于 ExpressionRootASTNode
- **LiteralASTNode 基类**：6 个字面量节点统一继承；
  `LiteralExpressionASTNode.AttachLiteral` 一次性附加
- **EOF 正式化**：`EndOfFileToken`（TokenType.EndOfFile）由 Parser 在输入**本地副本**
  末尾追加（不再修改调用者列表）；只由 RootParserLayer 消费；非 Root 层遇 EOF：
  结构完整 → Pop(Replay) 层层上交，不完整 → "Unexpected end of file"；
  换行哨兵与 guard 收尾循环（`while stack.Count > 1 && guard++ < 64`）删除，
  解析结束强制 `stack.Count == 1`
- **ParserLayerContext 收缩**：删除 `GetRootNode()` 与 `ContextImpl.current`；
  Parser 直接持有 RootASTNode，Layer 无法经 Context 触碰全局根
- **AST 完整性验证**：新增 `ASTIntegrityValidator`（AST/ASTIntegrityValidator.cs），
  Parse 成功后自动运行：Root 均已填充、Expression.Parent 指向 Root、节点无共享、
  Parent 链无环、switch default 规则；失败抛 `CompilerInternalException`（内部错误，
  与用户语法错误区分）
- **测试基础设施**：
  - `dotnet run -- --test-all` 单命令全量（`TestRunner` 注册全部套件，
    任意失败非零退出码，输出失败套件名）；新增最小 CI（`.github/workflows/ci.yml`：
    `dotnet build` + `--test-all`）
  - `TestRootParserLayer`：独立 Layer 测试改为 `Parse(tokens, TestRoot, entryLayer)`
    驱动——被测 Layer 提前结束或漏消费普通 token 立即失败（14 处调用全部迁移）
  - `TokenDispositionTests`：假 Layer 验证 Push/Pop × Consume/Replay 四种组合的
    token 接收序列（4 用例，菜单 23）
  - 表达式套件新增 AST 结构断言（§13.4：Root 存在/已填充/Expression 类型/
    Parent 链/子 Root 填充/无共享，4 用例，字符串快照不再是唯一验证方式）
- **消除全部编译警告**（原 `Core/Utilities.cs` 的 nullable 警告随 `null!` 模式消失）
- 测试总数 417 → 425（+4 结构断言、+4 TokenDisposition），22 个套件全绿

### 2026-07-26 · M22 namespace 声明（§15.1）—— P5 收官
- 新增 `NamespaceParserLayer`（小 Layer，与 ImportParserLayer 同款结构）：
  `namespace` + 路径（复用 `PathParserLayer`）+ 换行收尾；空路径与路径后多余 token 报错；
  唯一性与位置约束（应在文件首部）留待语义阶段
- `RootParserLayer` 新增 `namespace` 分发（与 import 同款）；新增
  `NamespaceDeclarationASTNode` 与 `Keywords.NAMESPACE`
- 新增 NamespaceTests 7 用例（基本/与 import 组合/3 错误用例）并注册菜单 22；
  全量回归 2–22 无 FAIL
- 测试总数 410 → 417
- **P5 全部完成，Parser 前端规划（roadmap P0–P5）收官**；下一阶段：语义分析、BIL 输出

### 2026-07-26 · M21 模块系统 import + wrapper 路径访问（`:`）
- `ImportParserLayer` 重建（SYNTAX §15.2，roadmap P5）——三种规范形态：
  单个导入 / `.{A, B}` 多个导入（共享前缀，展开为独立完整路径 `ImportItem`）/ `.*` 全部导入；
  前缀路径复用 `PathParserLayer`（遇 `*`/`{`/换行弹出并交还，末尾空名元素统一清理——
  与 GenericParameters 层对 `...` 的既有处理同款）；旧骨架的 `as` alias 与逗号分隔
  多导入不在现行规范内，按规范移除
- wrapper 路径访问（SYNTAX §14.1/§3）：`:` 接入 `ExpressionParserLayer` 后缀链
  （与 `.`/`?.` 同框架——base 为任意路径表达式，如规范示例 `foo().bar[0]?.length:MyWrapper`；
  链式 `obj:A:B` 左结合逐层嵌套）；新增 `WrapperAccessASTNode`
  - 架构判断：`PathParserLayer` 遗留的 `ValuePath`/`AcquisitionExpressionASTNode` 模式
    仅支持纯符号 base，与后缀链架构不兼容，未接入（记入技术债务待清理）
- 新增 ImportTests 14 用例并注册菜单 21；Expression 71 → 75
  （+4：wrapper 访问基本/链式/规范 §3 完整路径/后续成员后缀）
- 测试总数 392 → 410

### 2026-07-26 · M20 P5 起步：wrapper 主体（@ 注解 + `.proxy.*` 代理成员 + 前导点 enum case）
- `@` 注解 / wrapper 应用（SYNTAX §14.5）：`@Name` / `@Name(args)`，可叠加，挂所有声明节点
  - `ASTNode` 基类新增 `Annotations` 列表（仿 M14 `Children` 上移先例）；
    新增 `AnnotationASTNode`（符号路径名 / HasArguments / Arguments）
  - `DeclarationParserLayer` 新增 `AnnotationName` 状态：注解名复用 `PathParserLayer`、
    实参复用 `ArgumentListParserLayer`；声明本体创建时统一挂接暂存注解
  - `RootParserLayer` 的 `@` 预留入口就此接通；`CodeBlockParserLayer` 新增同款 `@` 分发
    （栈上注解变量，§14.3）
  - 规范判断：`@WrapperTarget(.Entity/.Value/.Method)` 即 wrapper 类型标识（§14.2–14.4），
    与普通 wrapper 应用同一语法形态；roadmap 示例中的 `wrapper X entity` 后缀写法以规范为准，不实现
- 前导点 enum case 引用（§12）：`ExpressionParserLayer` 新增 `EnumCaseNameExpected` 状态 +
  `EnumCaseExpressionASTNode`；参数化 case 调用（`.Failed(404)`）由后缀链自然脱糖为 Call，零新增代码
- `.proxy.*` 代理成员（§14.2/§14.6）：operator 名允许 `.proxy.<category?>.<name|*>` 限定名
  （仅 wrapper 体内、仅 operator）；复用 `CallableNameDot` 状态拼接，首段必须为 `proxy`、
  wildcard `*` 必须收尾；wrapper 体结束时校验同类 wildcard 唯一（与 enum case 校验同一先例）
- 顺带修复：`CodeBlockASTNode` 隐藏了基类 `Children` 字段（M14 上移时的漏网之鱼）——
  以 `ASTNode` 静态类型挂入的声明在块视角下不可见；删除隐藏字段，`Children` 回归基类唯一来源
- 测试：TypeDeclaration 77 → 94（+17：@ 注解 7、proxy 10）、Expression 67 → 71（+4：enum case）、
  CodeBlock 27 → 29（+2：栈上注解变量）；全量回归 2–20 无 FAIL
- 测试总数 369 → 392

### 2026-07-26 · M19 like 委托 + ext 扩展成员 —— P3/P4 收官
- `like` 委托（§9.6，roadmap #13 剩余项）：统一声明层新增 LikeExpected/AfterLike
  两个状态，`class A : Base implements I like field {` 与省略基类的 `class A like x {`
  均可解析；仅 class 可委托（struct/interface/wrapper 报错）；AST 为
  `ClassDeclarationASTNode.LikeTarget`（可空字符串，不加节点）
- `ext` 扩展成员（§4.4）：补 `ext` 关键字（Keywords.EXT + DeclarationDescriptors）；
  限定名 `Type.member`（可多段路径）——Callable 侧在 ParamsExpected 加 `.` 分支
  （extSeen 门控，非 ext 自然落错误分支），var/const 侧经
  `VariableDeclarationParserLayer(allowExtension)` 同构支持；与 M16 会师：
  `pub ext var String.isEmpty: bool { get(_: _) { ... } }` 完整可解析
- 测试：TypeDeclaration 68 → 77（+9：like 规范形态/省略基类/2 错误、ext 函数/字段/2 错误）；
  全量回归 2–20 无 FAIL
- 测试总数 360 → 369
- **P3（#13–16）与 P4（#17–19）全部完成**；下一阶段 P5：wrapper 主体、模块系统、
  wrapper 路径访问（`:`）

### 2026-07-26 · M18 init 参数映射（_ -> field，SYNTAX §9.3）
- `ParameterListParserLayer` 增加 `allowMapping` 开关（默认 false，仅 init 传入 true）：
  `_ -> x`（同名映射）、`_ -> x = 0`（带默认值）、`horizontal: i32 -> x`（显式名 + 类型）、
  与普通参数混合；新增 MappedFieldExpected / AfterMappedField 两个状态
- `ParameterASTNode` 增加 `MappedFieldName`（可空）；映射参数省略类型时
  Type 保持空引用节点（沿用字段类型，语义阶段回填）
- 非 init 形参列表出现 `->` 自然落到既有错误分支（func/lambda/operator 均拒绝）
- 与 M17 会师：§12 Direction 规范示例（`priv init(_ -> degrees)` + `[case 列表]`）完整可解析
- 测试：TypeDeclaration 61 → 68（+7：§9.3 全形态、混合、struct/enum 集成、3 个错误用例）；
  全量回归 2–20 无 FAIL
- 测试总数 353 → 360
- 已知缺口：§9.3 语法行中的 `[modifier...]` 形参修饰符无任何规范示例，暂不支持（出现时按普通标识符报错）

### 2026-07-26 · M17 enum struct 的 [] case 列表（SYNTAX §12）
- 在统一 `DeclarationParserLayer` 内联扩展（不新建 Layer）：enum 体 `}` 后进入
  case 列表子状态机（EnumCaseListOpen/EnumCaseStart/EnumAfterName/EnumAfterArgs/
  EnumDiscriminant/EnumAfterCase 六个状态）
- 形态全覆盖：固定 case（`North(0)`、无参 `Red`）、参数化 case（`_` 参数洞 +
  具名实参 `Failed(errorCode = _)`）、显式判别值（`-> N`，§12.4）；
  case 实参复用 `ArgumentListParserLayer`（开括号由本层消费——与调用点既有约定一致）
- 解析期校验：case 名唯一、判别值唯一、「全显式或全分配」不得混用（§12.4）、
  判别值必须是非负整数字面量
- `[` 必须与 `}` 同行：换行即声明结束（与 callable 体 `{` 的既有约定一致），
  同时保证 EOF 哨兵下无 case 的 enum 能正常收敛
- 测试：TypeDeclaration 51 → 61（+10：固定/参数化/判别值/无 case 缺省 + 4 个错误用例）；
  全量回归 2–20 无 FAIL
- 测试总数 343 → 353

### 2026-07-26 · M16 属性访问器 getter/setter（§9.4，roadmap #23）
- 新增 `PropertyAccessorParserLayer` 解析变量声明后的 `{ get... set... }` 访问器块；
  三类定义位置（类/struct 字段、全局变量、栈上 var/const）不经任何改动即覆盖——
  它们早已统一汇聚到 `VariableDeclarationParserLayer`，只需在其 NameSeen/TypeSeen
  状态各加一个 `{` 分支（Delegate, don't implement）
- 访问器形态全覆盖：编译器生成（`pub get` 无参无体）、`(value: _) + 自定义体`
  （backing field）、`(_: _) + 自定义体`（计算属性）；访问器体复用 CodeBlockParserLayer
- 解析期校验：同块重复 get/set 报错、空块报错、`(_: _)` 与 `(value: _)` 混用报错
  （§9.4「get 和 set 在是否需要 backing field 上必须保持一致」）
- 自动访问器以换行或 `}` 收尾——与成员声明的换行分隔规则一致
- AST：新增 `PropertyAccessorASTNode`（Kind/HasBackingField/Body），
  `VariableDeclarationASTNode` 增加 Getter/Setter 两个可空字段，不加包装层
- 测试：新增 PropertyAccessorTests 17 用例（形态/跨行/三类位置/5 个错误用例），
  注册菜单选项 20；全量回归 2–20 无 FAIL
- 测试总数 326 → 343

### 2026-07-26 · M15 声明泛型参数接入统一声明层
- 类型/函数声明接入既有 `GenericParametersParserLayer`（roadmap P3 收尾第 1 项，不新建任何 Layer）：
  - Callable（func/operator/init 共用）：`ParamsExpected` 状态识别 `\`，push 泛型层后**保持原状态**，列表弹出后仍等待 `(`
  - 类型（class/interface/struct/enum struct/wrapper 共用）：`AfterTypeName` 状态识别 `\`，同样保持原状态，列表弹出后仍等待 `:` / `implements` / `{`
  - 两处接入均不新增状态机状态，与 LambdaExpressionParserLayer 的既有接入模式一致（简洁优先三问：复用已有轮子，不加状态）
  - 5 个类型节点的同名 `GenericParameters` 字段集中分派（新增 `SetGenericParameters`，与 `SetTypeName` 同一 pattern）
- 覆盖 SYNTAX §3.6 全形态：多参数、out/in 型变、extends/supers/with 约束、可变/具名可变参数、型变+约束组合；全局函数与成员方法/operator 同一条路径；泛型列表位置与继承子句的先后关系符合规范（`Name\<T> : Base implements I {}`）
- 测试：TypeDeclaration 36 → 51（+15：泛型类型/接口/struct/wrapper、泛型+继承+implements、全局与成员泛型函数、泛型 operator、约束、可变参数、型变+约束）；全量回归 2–19 无 FAIL
- 测试总数 311 → 326

### 2026-07-26 · M14 统一声明层：全局/成员/嵌套共用一套 infra
- 核心依据 SYNTAX.md §14.8：canonical symbol 的类名段可为空、`.static.` 只是标记位，因此"全局函数"与"成员方法"结构同构——三类位置合并为一条代码路径，而不是各造轮子
- `DeclarationParserLayer` 成为任何位置任何声明的唯一入口：
  - 全局字段 / 类字段 → 复用 `VariableDeclarationParserLayer`
  - 全局函数 / 方法 / static / operator / init → 同一套 Callable 状态，只改 `Kind`
  - 参数列表 → 复用 `ParameterListParserLayer`；返回类型 / 基类 / 接口 → 复用 `TypeReferenceParserLayer`；函数体 → 复用 `CodeBlockParserLayer`
  - 嵌套类型 → 类型体内递归 push 本层自身（与顶层同一路径）
- 配套简化（删冗余，不加轮子）：
  - 新增 `CallableDeclarationASTNode` 一个节点覆盖 func/operator/init（`CallableKind` 枚举）
  - 删除 `DeclarationASTNode` 包装层（其 type 枚举与 Declaration 指针只是对 C# 节点类型的重复表达）
  - 删除 5 个类型节点各自的 `CodeBlockASTNode Body` 字段，成员统一挂 `ASTNode.Children`（Children 从 RootASTNode 上移到基类）
  - RootParserLayer 的 var/const 专用分支合并进通用声明分支
- `Parser.cs` 修复 EOF 收尾：嵌套委托后父层仍需一个终止 token 才能收敛，原逻辑只喂一个哨兵便判定 `stack.Count > 1` → "Unexpected End"；改为反复喂哨兵直到栈收敛或无进展（带 guard 防死循环）
- 规范判断：interface 的 `: Base` 按 SYNTAX §11 是父接口，落 `BaseInterfaces` 而非 `Interfaces`
- 测试：TypeDeclaration 16 → 36（新增全局字段/全局函数、类成员、init/operator、继承与 implements 列表、三层嵌套类型）；全量回归 2–19 全绿（其中 5/6/9/10/11/12 由 EOF 修复恢复）
- 测试总数 275 → 311

### 2026-07-26 · 修复：TypeDeclaration 测试的 double-root 问题
- 根因：`Parser.Parse` 内部已压入 RootParserLayer，测试又把 `new RootParserLayer(root)` 当 entryLayer 传入，栈里出现两个永不 pop 的 root 层，循环结束时 `stack.Count > 1` 触发 "Unexpected End"
- 修复：测试改用 `parser.Parse(tokens)` 并取其返回值作为根节点（一行改动）
- 同提交把「简洁优先三问」原则写入 CLAUDE.md / AGENTS.md（含项目内已验证的复用范例表）
- 修完 TypeDeclaration 16/16 通过，套件 2–19 全量回归无失败

### 2026-07-26 · M13 P3 类型声明解析基础（DeclarationParserLayer 重构）
- 前置提交：5 个类型声明 AST 节点（Class/Interface/Struct/EnumStruct(+EnumCase)/Wrapper）+ 关键字补齐（pub/priv/open/abstract/singleton/shared/rich/implements/like/init/get/set 等）
- 扩展现有 `DeclarationParserLayer` 骨架（不新建 Layer）解析类型声明头部：修饰符 → 类型名 → 继承/接口 → 体
- 修饰符识别：pub, priv, open, abstract, singleton, shared, rich, static, override, async
- 更新关键字数组：DeclarationKeywords/TypeKeywords 添加 enum，DeclarationDescriptors 添加全部新修饰符
- 新增 TypeDeclarationTests 16 用例（简单声明、带修饰符、5 种类型），菜单注册选项 19
- 提交时测试未过（WIP），由随后的 double-root 修复转绿

### 2026-07-26 · M12 CoroutineOps：await/yield（roadmap #12，P2 完成）
- **await**：一元前缀运算符，在 ExpressionParserLayer 中处理（`IsPrefixUnaryOperator` 添加 `Keywords.AWAIT`）
- await 产生 UnaryExpressionASTNode，operator 为 "await"
- await 可在变量初始化、if 条件、return 等任意表达式位置使用
- **yield**：语句，在 CodeBlockParserLayer 中内联处理（类似 return/throw）
- YieldStatementASTNode：包含可选的 Alarm 表达式
- 裸 yield（不带表达式）：直接结束当前执行段
- yield alarm（带表达式）：委托 ExpressionParserLayer 解析 alarm
- 添加 Keywords.AWAIT 和 Keywords.YIELD
- 测试 262 → 275（+13：await 表达式、yield 语句、await+yield 组合、不同上下文）
- **P2 语句系统全部完成！**

### 2026-07-26 · M11 throw 语句
- 在 CodeBlockParserLayer 中内联处理 throw 语句（类似 return/break/continue）
- ThrowStatementASTNode：包含异常表达式，必须紧跟 throw 关键字
- throw 后委托 ExpressionParserLayer 解析异常表达式
- 状态机：throw 已读 → ThrowValue（解析表达式）→ StatementEnd
- 配合 try-catch-finally 构成完整异常处理系统
- 测试 252 → 262（+10：简单 throw、throw 表达式、throw 构造、配合 try-catch、不同上下文、错误用例）
- 异常处理系统完整：try-catch-finally（捕获）+ throw（抛出）

### 2026-07-26 · M10 SeqBlockParserLayer（roadmap #11，含表达式形态）
- SeqBlockExpressionASTNode：从 StatementNode 移至 ExpressionNode，继承自 ExpressionASTNode，实现语句+表达式双形态
- SeqBlockParserLayer 实现 IResultProducer，支持作为表达式返回节点（通过结果传递机制）
- ExpressionParserLayer 集成：在 Primary 状态识别 seq/volatile 关键字，委托给 SeqBlockParserLayer（shouldKeepToken: true）
- seq 作为表达式：`var result = seq { return@seq compute() }`、`var r = seq named calc { return@calc getValue() }`
- 架构洞察：seq/lambda/方法等的代码块共用 CodeBlockParserLayer 基建，解析逻辑统一
- 测试 249 → 252（+3：seq 作为表达式的 3 个用例）
- 完整覆盖：简单 seq、volatile、using（单个/多个/带类型）、named、组合、表达式形态、错误用例

### 2026-07-26 · M10 SeqBlockParserLayer（roadmap #11）
- SeqBlockParserLayer 完整实现：seq 块的作用域、volatile 修饰符、using 资源绑定、named 标签
- using 绑定支持多个资源，每个绑定为 `using(const/var name[:Type] = initializer)`
- using 绑定委托 VariableDeclarationParserLayer 的子集逻辑：类型标注委托 TypeReferenceParserLayer，初始化委托 ExpressionParserLayer
- seq 代码块委托 CodeBlockParserLayer 解析，支持所有已实现语句类型
- 新增 AST 节点：SeqBlockStatementASTNode、UsingBindingASTNode
- 新增 ASTNodeType：SeqBlockStatement、UsingBinding
- 新增关键字：seq、using、volatile
- CodeBlockParserLayer 集成 seq/volatile 语句分发
- 测试 235 → 249（+14：简单 seq、volatile、using 单个/多个/带类型、named、组合、4 个错误用例）
- 限制：seq 作为表达式（return@seq/return@label）需要在 ExpressionParserLayer 中集成，当前仅支持语句形态

### 2026-07-26 · M9 TryCatchFinallyParserLayer（roadmap #10）
- TryCatchFinallyParserLayer 完整实现：try 块、多个 catch 子句（异常变量可为 _ 表示丢弃）、finally(e) 参数（e 为异常或 null）
- catch 异常类型委托 TypeReferenceParserLayer 解析，支持完整类型引用（含泛型、可空）
- catch/finally 代码块委托 CodeBlockParserLayer 解析，支持所有已实现语句类型
- 新增 AST 节点：TryCatchFinallyStatementASTNode、CatchClauseASTNode
- 新增 ASTNodeType：TryCatchFinallyStatement、CatchClause
- 新增关键字：throw（常量定义，解析留待后续）
- CodeBlockParserLayer 集成 try 语句分发：try 关键字触发 TryCatchFinallyParserLayer
- 测试 226 → 235（+9：简单 try-catch、多 catch、丢弃变量、try-finally、try-catch-finally、嵌套 try、3 个错误用例）
- 验证通过：至少一个 catch 或一个 finally、catch 后必须有类型、finally 后必须有参数

### 2026-07-18 · M7 P2 语句系统核心（CodeBlock / if 语句 / 循环 / return / 赋值）
- CodeBlockParserLayer 重写：语句识别与分发中枢；语句以换行或 `}` 结束；var/const、if、for/while/do 委托专门层，return/break/continue 以内部子状态直接处理，表达式语句后跟 `=` 转为赋值
- IfStatementParserLayer 扩展语句模式：else 可选、支持 else if 链（ElseBranch 为块或嵌套 IfStatement）
- LoopParserLayer（roadmap #9）全形态：for-each/范围（`0 to 10` → RangeExpressionASTNode）/while/do-while/named 标签
- 新增 AST/StatementNodes.cs（CodeBlock/IfStatement/Loop/Return/LoopControl/Assign）；Keywords 新增 return/break/continue/to/do
- VariableDeclarationParserLayer 修复：`}` 可终止块内末语句（三个结束状态）
- 顺带修复：return 带值时 handler 捕获已置空字段的 NRE；@标签/named 标签增加标识符首字符校验（拒绝数字）
- 测试 184 → 226（+42：CodeBlock 27、Loop 15）

### 2026-07-18 · M8 P1 收尾（Lambda / if / switch 表达式 + typeOf/as/is）
- LambdaExpressionParserLayer（roadmap #21 提前落地）：完整/泛型/async lambda、trailing lambda（脱糖为以 lambda 为唯一实参的调用）；形参/泛型形参/返回类型分别复用 ParameterList/GenericParameters/TypeReference 层
- IfStatementParserLayer / SwitchStatementParserLayer（roadmap #7/#8 表达式模式）：if 表达式强制 else、switch 表达式强制 default；`_` 模式匹配按普通符号解析
- TypeOfExpressionParserLayer；is/supers/with/as/as? 改为专用 AST 节点（CastExpressionASTNode/TypeCheckExpressionASTNode），右侧委托 TypeReferenceParserLayer，is/as 不再按二元运算符处理；as? 安全转换标记在类型操作等待态消费
- 结构化 Layer 统一允许跨行书写：结构性等待状态跳过换行，表达式内部仍由 ExpressionParserLayer 按行终止
- 新增 ASTNodeType：IfExpression/SwitchExpression/TypeOfExpression/CastExpression/TypeCheckExpression；新增 Keywords：async/switch/typeOf
- 测试 145 → 184（+39：表达式套件 +13、Lambda 15、if 8、switch 6、typeOf 7）；roadmap #4 全部完成，P1 收官

### 2026-07-17 · M6 ParameterListParserLayer
- 形参全态：普通/默认/可变/具名可变；默认值委托 ExpressionParserLayer，类型委托 TypeReferenceParserLayer
- 修复 `...` 解析在符号末尾残留空名元素的问题（提交前清理）
- 测试 14/14；roadmap #5 完成

### 2026-07-17 · M5 表达式后缀链 + ArgumentListParserLayer
- 后缀链：调用 `(`、索引 `[`、成员 `.`、安全访问 `?.`、泛型实参 `\<`，任意链式组合
- 实参列表支持位置/具名/混合；new 构造参数接入
- 纯符号路径保持 Symbol 形态（存量测试零破坏）
- 表达式套件扩至 54/54；roadmap #4 大部分完成

### 2026-07-17 · M4 GenericParametersParserLayer
- 泛型声明全态：参数/out/in 型变/可变参数/extends/supers/with 约束
- 新增 `Parser.Parse(tokens, entryLayer)` 重载，任意 Layer 可独立测试
- 测试 21/21；roadmap #22 完成（P6 提前落地）

### 2026-07-17 · M3 泛型语法迁移 `\<...>`
- 语言修订：泛型列表统一 `\<` 开启、`>` 闭合；`<` 解放为小于号
- 文档全量迁移（SYNTAX/RUNTIME/BIL 引用/ROADMAP/活文档）
- Lexer 不合并 `>` 系列；表达式层重组 `>=`/`>>`/`>>>`
- SymbolLayer 以 `\` + `<` 进入泛型模式；嵌套泛型 `>>` 修复
- 测试 18/18（GenericParsingTests）

### 2026-07-17 · M2 结果传递机制
- IResultProducer/IResultConsumer 接口 + 弹层自动传递
- VariableDeclaration.Initializer 真正保存；ExpressionParserLayer 接入（字面量包装/分组/一元/二元右操作数回填）
- 无优先级规则强制：`1 + 2 * 3`、`not not x`、`-x + y` 均报错
- 顺带修复：二元 Completed 状态未处理、十六进制 `0xFF` 后缀误判（值为 15 的隐藏 bug）
- 表达式测试 29/29

### 2026-07-17 · M1 P0 核心基础
- LiteralParserLayer（15/15）、TypeReferenceParserLayer（3/3）、VariableDeclarationParserLayer（10/10）
- 层栈式 Parser 架构验证可行（PushLayer/PopLayer/Continue 协议）

---

**格式说明**：后续里程碑在「里程碑历史」**顶部**追加新段落（倒序），并同步更新 §1 总览、§2 可解析语法、§3 组件状态、§5 下一步、§6 技术债务。
