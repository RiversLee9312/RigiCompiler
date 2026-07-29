# Latte 语言语法参考

## 1. 基本规则

### 1.1 语句终止与续行

Latte 不使用分号。顶层和代码块中的换行通常就是语句终止符。

`()` 与 `[]` 所包围的分组、调用参数表和索引参数表尚未闭合时，其中的换行按空白处理，可以自然续行。`{}` 作为函数体、控制流体、lambda/`seq` 块或类型体时**不会**吞掉内部换行；块中的各条语句仍由换行分隔。换言之，自动续行来自尚未闭合的符号结构，而不是“只要位于任意大括号内就忽略换行”。

### 1.2 注释

```latte
// 单行注释
/* 多行注释 */
/** 文档注释（Java 风格） */
```

### 1.3 普通运算符没有优先级

Latte **没有普通运算符优先级**。当一个表达式中出现多个普通一元/二元运算符，而其运算树不能由括号唯一确定时，编译器直接报错；编译器不会借用其他语言的优先级表进行猜测。

```latte
// 编译错误：未指定运算树
var result = 1 + 2 * 3

// 正确：显式指定
var result = 1 + (2 * 3)
var result = (1 + 2) * 3

// 编译错误：连续的一元运算没有用括号声明嵌套关系
var value = -await foo().bar[0]

// 正确
var value = -(await foo().bar[0])
```

调用、索引、成员路径、安全调用和 wrapper 路径不通过普通运算符优先级表解析；它们属于下节定义的符号表达式与路径表达式。

### 1.4 符号表达式与路径表达式

Latte 将调用参数表 `()` 和索引参数表 `[]` 视为**符号表达式（SymbolExpression）**的一部分，而不是参与优先级竞争的后缀运算符。符号表达式可以递归嵌套：

```latte
// 外层是索引 SymbolExpression，内层是调用 SymbolExpression a(1, 2)
a(1, 2)[2]

// 外层是调用 SymbolExpression，内层是索引 SymbolExpression a[0]
a[0](1, 2)
```

多个完整的符号表达式节点可以由路径连接符组成一个**路径表达式（PathExpression）**：

- `.`：普通成员路径；
- `?.`：安全成员路径；
- `:`：wrapper 访问路径。

路径严格按照源码顺序从左到右结合，并在普通运算符解析之前整体形成。安全调用属于路径表达式本身，不是一元运算符。

例如：

```latte
foo().bar[0]?.length:MyWrapper
```

是一条路径表达式，含四个符号表达式节点：

```text
foo()  .  bar[0]  ?.  length  :  MyWrapper
```

其中 `foo()`、`bar[0]`、`length`、`MyWrapper` 分别是完整节点；`bar[0]` 内部又是一个索引符号表达式。整条路径在更外层的一元/二元表达式中被视为单一操作数。

---

## 2. 变量声明

使用 `const`（不可变）和 `var`（可变）声明变量，类型标注写在冒号后面，支持类型推断。

```latte
const name: String = "Hello"
const inferred = "World"       // 类型推断为 String
var count: i32 = 0
var x = 42                     // 类型推断为 i32
```

---

## 3. 类型系统

### 3.1 类型层级

```
Any
├── Object
│   ├── Nullable\<T>
│   ├── Box\<T extends ValueType>   // 语法上属于 Object、Native 层无独立 Box TypeSheet 的系统特权载体，见 RUNTIME.md
│   └── ... (所有 class)
└── ValueType
    ├── Enum
    │   └── ... (所有 enum struct)
    ├── i8, i16, i32, i64
    ├── u8, u16, u32, u64
    ├── float, double
    ├── bool, char
    ├── String                      // 非 rich 值类型，见 §3.1.2
    ├── Type\<T>
    ├── Span\<T extends ValueType>
    ├── Wrapper                     // 所有 wrapper 的基类；wrapper 恒为 rich struct，见 §14.9
    │   └── ... (所有 wrapper)
    └── ... (所有 struct)
```

`String` 与 `Wrapper` 都在 `ValueType` 分支下：前者是不含托管引用的非 rich 值类型，后者是 rich 值类型。二者的定位理由分别见 §3.1.2 与 §14.9。

### 3.1.1 `rich` 与 `shared` 类型修饰符

Latte 将值类型按是否允许携带托管对象引用分为普通 ValueType 与 rich ValueType，并将对象按是否允许跨协程共享分为 local object 与 shared object。`rich` 和 `shared` 都是**类型声明修饰符**，不是变量或引用位置修饰符。

- `rich` 仅用于 `struct`（包括 `enum struct`）与 `wrapper`。未标记 `rich` 的 struct 不得直接或间接持有任何 Object，也不得内嵌 rich struct。
- `wrapper` 恒为 rich struct，`rich` 由声明形式隐含，源码中不再显式书写（见 §14.9）。
- `shared` 可用于 `class`，或与 `rich` 一起用于 struct，或用于 `wrapper`。`shared struct` 而没有 `rich` 是编译错误。
- 未标记 `shared` 的 class 实例是 local object，只属于创建它的 Coroutine，不得在 Coroutine 之间共享。
- 标记 `shared` 的 class 实例是 shared object，可以被多个 Coroutine 引用；`shared` 只表示共享资格和相应的生命周期管理，不自动使对象字段操作具备线程安全。
- `shared rich struct` 仍然是值类型，复制与装箱继续采用值语义/unique ownership；`shared` 表示它的字段闭包可以安全地进入 shared object graph。

```latte
pub struct Point {
    pub var x: float
    pub var y: float
}

pub rich struct LocalEntry {
    pub var owner: User
    pub var position: Point
}

pub shared class SharedUser {
    pub const id: i64
}

pub shared rich struct SharedEntry {
    pub var owner: SharedUser
    pub var position: Point
}
```

**非 rich struct 的封闭性**：非 rich struct（包括非 rich `enum struct`）不得标记 `open`，也不得标记 `abstract`，因而不可能拥有子类型。这既是它「不含托管引用」这一事实的自然结果，也封死了「声明一个 rich 或 shared 的子类型来放宽基类闭包」这条绕过路径。可被继承的 struct 必须自身是 rich struct 并显式标记 `open`。

字段闭包规则：

| 持有者类型 | 允许持有的 Object | 允许内嵌的 ValueType |
|---|---|---|
| 非 rich struct | 不允许 | 仅非 rich ValueType |
| rich struct | local object、shared object | 所有 ValueType |
| shared rich struct | 仅 shared object | 非 rich ValueType、shared rich ValueType |
| wrapper（非 shared） | local object、shared object | 所有 ValueType |
| shared wrapper | 仅 shared object | 非 rich ValueType、shared rich ValueType |
| local class | local object、shared object | 所有 ValueType |
| shared class | 仅 shared object | 非 rich ValueType、shared rich ValueType |

这些限制递归应用于字段、继承得到的字段、泛型实参所展开的字段和编译器生成的隐藏字段（包括 §14.9 的 wrapper 隐藏字段）。由此保证：从任意 shared class、shared rich struct 或 shared wrapper 出发，沿字段递归遍历，不可能到达 local object 或非 shared rich 值。

**`rich` 与 `shared` 的传染性（单向）**：

- 基类为 `shared` 时，子类必须为 `shared`；
- 基类为 `rich` 时，子类必须为 `rich`。

反向不成立：`shared` 子类可以继承非 `shared` 基类，`rich` 子类可以继承非 `rich` 基类。这类子类必须让**包括继承字段在内的完整字段闭包**满足上表——基类若持有 local object 字段，把子类声明为 `shared` 并不能使该继承合法，编译器直接拒绝。因此一个类型的 rich/shared 属性只能由它自己的声明决定，不能从基类推断；编译器按声明保守判定，安全性由闭包检查兜底。

**共享安全类型（shared-safe type）**：满足以下任一条件的类型是共享安全类型——

- shared class；
- shared rich struct、shared wrapper；
- 非 rich ValueType（全部基元类型、`String`、`Type\<T>`、`Span\<T>`、非 rich struct 与非 rich enum struct）；
- `Nullable\<T>`，且 `T` 本身是共享安全类型（见 §3.1.2）。

共享安全类型是「可以离开单个 Coroutine 的所有权域」的完整白名单。跨 Coroutine 传递时：

- shared object 可以共享引用；
- 非 rich ValueType 按值复制；
- shared rich ValueType 按值复制，并对其内部 shared object 引用执行同步引用计数操作；
- local object 与非 shared rich ValueType 不得跨 Coroutine 边界。

**逃逸闸门**：非共享安全的值只能存在于单个 Coroutine 的栈与其 local object 图中。编译器在两处静态拦截它逃逸：

1. **全局字段与静态字段**的类型必须是共享安全类型。全局变量、全局常量、静态字段与它们的 getter/setter 支持类型都适用本条——全局存储不属于任何 Coroutine，因此不得承载 local object 或非 shared rich 值。`singleton` class 的实例存储同样是全局存储，因此 **singleton class 必须标记 `shared`**；需要「每协程一个实例」时使用 `core.coroutine.CoroutineLocal\<TValue>`（见 `RUNTIME.md` §20.2），而不是非 shared singleton。
2. **`async` 边界**（详见 §4.5）：async 函数与 async lambda 的 receiver、参数、返回值（即 Task 结果类型）、捕获变量与泛型实参都必须是共享安全类型。

栈上的 `var`/`const`、非 static 的实例字段不受本闸门约束——它们的所有权随宿主，宿主自身已由字段闭包规则约束。

### 3.1.2 编译器特权类型与运行时表示

Latte 不要求每一个源码类型节点都一一对应一个普通 Native 对象类型或独立 `TypeSheet`。少数内建抽象由编译器与运行时共同提供特权 lowering；它们在语法、类型检查和泛型约束中表现为正常类型，但物理表示可以绕过普通用户类型的对象模型。

- `Box\<T extends ValueType>` 在语法类型层级中属于 `Object`，可以进入 `Object`/`Any` 多态位置并满足相应约束；但它不是普通 class，不生成 Box 对象头、Box identity 或独立的 `Box\<T>` TypeSheet。Box 槽中的 typeid 始终是底层实际 ValueType `T` 的 typeid，Native 表示与复制/销毁规则见 `RUNTIME.md` §4。
- `Span\<T extends ValueType>` 是编译器与运行时共同实现的连续原生缓冲区后门，不按普通泛型容器的 16 字节元素槽布局；其索引、步长与 GC 扫描均使用内建 lowering。
- `String` 是**非 rich ValueType**：它不持有托管引用，`refMap` 恒为空，因此可以自由出现在全局/静态字段与 async 边界上（见 §3.1.1），无需任何 shared 标注。它的字符数据位于编译器与运行时管理的特权裸缓冲区中，不是普通 Object 字段。
  - **复制语义按值深拷贝**：`var b = a` 在语义上产生一份独立的字符数据。实现可以引入对用户完全透明的 copy-on-write 或不可变共享优化，但**源码语义、类型检查与用户代码一律不得假设这些优化存在**——正如 BIL 永远不得假设某种 GC 模型或 GC 行为。任何可观察到共享的行为都是实现缺陷，而不是可依赖的特性。
  - `String` 不可被继承，也不可被 wrapper 修饰（非 rich struct 的通用规则，见 §14.9）。
- `Nullable\<T>` 属于 `Object` 分支，但其 shared 属性由 `T` 推导而非由声明给出：`T` 是共享安全类型时，`Nullable\<T>` 也是共享安全类型。这个特权只属于 `Nullable\<T>`，因为它由 `T?` 隐式生成、用户无法声明它的 shared 变体。显式书写的库容器（`Array\<T>`、`Map\<K, V>` 等）不适用本规则——需要跨协程时应当选用相应的 shared 容器类型。
- 其他由规范明确标记为内建、编译器生成或系统特权的机制，也可以拥有普通用户类型不能声明或复制的 lowering、布局或派发规则。

这些特权只属于语言规范明确列出的内建机制。用户声明的 class、struct、interface 或 wrapper 不能通过源码复制其布局、身份、派发或生命周期规则。

### 3.2 基本类型

| 类型 | 说明 | 层级 |
|------|------|------|
| `i8`/`i16`/`i32`/`i64` | 有符号整数 | ValueType |
| `u8`/`u16`/`u32`/`u64` | 无符号整数 | ValueType |
| `float`/`double` | 浮点数 | ValueType |
| `bool` | 布尔值 | ValueType |
| `char` | 字符 | ValueType |
| `String` | 字符串（非 rich 值类型，值语义深拷贝，见 §3.1.2） | ValueType |
| `Type\<T>` | 运行时类型（typeid 的封装） | ValueType |
| `Span\<T extends ValueType>` | 连续、无装箱的缓冲区视图（见 RUNTIME.md） | ValueType |

### 3.3 字面量

```latte
// 整数（默认 i32）
42
1_000_000        // 下划线分隔（不允许连续下划线或下划线开头）
0xFF             // 十六进制
0b1010           // 二进制
0o777            // 八进制

// 整数后缀
100L             // i64
100S             // i16
100B             // i8
100U             // u32
100UL            // u64
100US            // u16
100UB            // u8

// 浮点数（默认 double）
3.14
0.1f             // float

// 字符串
"Hello ${expr}"          // 字符串插值
"""
多行字符串
"""

// 布尔
true
false

// 字符
'A'
'\n'        // 转义与字符串同一套（\' \\ \n \t 等），未知转义是编译错误
```

字符字面量规则：单引号内必须恰好是一个字符或一个转义序列，类型为 `char`（§3.2）；空（`''`）或多于一个字符（`'ab'`）是编译错误。

多行字符串（`"""`）是编译期处理的严格多行形式（Swift 风格）：

- 开界 `"""` 后必须紧跟换行，该换行不属于内容；`"""hello` 这类开界同行写法是编译错误。
- 闭界 `"""` 必须独占一行：它前面只允许空白字符，闭界前的换行不属于内容。闭界出现在内容行中间（前面已有非空白内容）是编译错误——内容中的三引号须写成 `\"""`。
- 缩进剥除：闭界行的前导空白量 N 是剥除基准，每个内容行的前 N 个字符必须均为空白并被剥除；前导空白不足 N 的非空内容行是编译错误。全空白的内容行剥除后输出空行。
- 转义与单行字符串相同（`\n`、`\t`、`\\`、`\"`、`\$` 等），未知转义是编译错误；剥除缩进先于转义处理，行内单个 `"` 或 `""` 免转义。
- 内容中的换行恒为 `\n`（行尾归一在词法入口完成）；`${}` 插值与单行字符串一致；`\$` 转义的字面 `$` 不构成插值引导（单行/多行相同）。

```latte
var text = """
    line1
    line2 with "quotes" and ${interp}
    """
// 等价于单行写法 "line1\nline2 with \"quotes\" and ${interp}"
```

### 3.4 空安全

类型默认非空，`T?` 表示可空类型（底层为 `Nullable\<T>`，`Object` 子类）。

由于 `Nullable\<T>` 是 `Object` 子类，可空的值类型会被装箱；已经可空的值不会被再次装箱，语法上也不允许对 `Nullable\<T>` 再次施加 `?`（不存在 `T??`）。

`Nullable\<T>` 的 shared 属性由 `T` 推导：`T` 是共享安全类型时 `Nullable\<T>` 也是，因此 `String?`、`SharedUser?` 可以出现在全局字段与 async 边界上，而 `LocalUser?` 不可以（见 §3.1.1、§3.1.2）。

```latte
var name: String? = null

// 安全调用
name?.length

// 安全调用 + 空值回退（替代 Kotlin 的 ?:）
name?.length if? 0

// 安全 let
name?.let{(it: String) -> doSomething(it)}

// 安全类型转换
obj as? String
```

### 3.5 类型转换与类型检查

```latte
// 类型检查
obj is String         // obj 是否为 String 或其子类
obj supers Animal     // obj 的类型是否为 Animal 的基类

// 强制转换（失败抛出 core.CastException）
obj as String

// 安全转换（失败返回 null）
obj as? String
```

支持智能转换（smart cast）：`is` 检查后，在对应分支中自动转换类型。

转换优先级：源类型的 `castTo` → 目标类型的 `castFrom`（前者不存在或抛异常时才尝试后者）。

```latte
// 自定义类型转换（定义在源类型上）
operator castTo\<TTarget>(): TTarget { ... }

// 自定义类型转换（定义在目标类型上）
operator castFrom\<TSource>(obj: TSource): TSource { ... }
```

### 3.6 泛型

泛型参数使用 `T` 前缀 + 描述性名称的驼峰命名法（如 `TResult`、`TAnother`、`TElement`）。

**泛型列表语法：一律以 `\<` 开启、以 `>` 闭合。** 无论是泛型声明（类型参数列表）还是泛型使用（类型实参列表），都必须写作 `Name\<...>` 的形式：

```latte
// 声明：类型、函数、wrapper 的类型参数列表
class Container\<TElement> { ... }
func transform\<TInput, TResult>(input: TInput): TResult { ... }

// 使用：类型引用、泛型调用
var list: List\<i32>
var map: Map\<String, i32>
var sorted = myList.sort\<i32>()
Span.alloc\<f32>(1000)
```

`\<` 是两个独立字符（反斜杠 + 小于号），不是一个新符号。这样设计的原因：

- `<` 在 Latte 中**只**是比较运算符，与泛型列表不存在词法歧义（`a < b` 永远是比较，`a\<b>` 永远是泛型）；
- 解析器无需回溯或前瞻即可区分泛型与比较，也不依赖空格等脆弱约定；
- 与 Latte"一切显式"的设计哲学一致：泛型边界显式标注，正如运算顺序必须显式加括号。

闭合符保持单个 `>`：`\<` 已无歧义地开启了泛型语境，其后的 `>` 只可能是闭合符。嵌套泛型的连续闭合写作 `>>`，如 `List\<Map\<String, i32>>`。

Latte 的泛型在语义和运行时类型信息上都保持**具化（reified）**。实现采用单份共享 Native 代码体、隐式 typeid 传递与统一胖值槽，而不是为每组类型实参生成一份单态化机器码。共享代码体不等于类型擦除：实际泛型类型始终随 typeid 存在，可以直接用于 `TElement()`、`is`、`supers`、`with`、`typeOf` 与运行时构造（详见 RUNTIME.md）。ValueType 进入统一泛型/动态槽位时由系统特权 `Box` 表示按尺寸内联或间接保存。

```latte
// 类/struct 泛型
class Container\<TElement> { ... }

// 函数泛型
func transform\<TInput, TResult>(input: TInput): TResult { ... }

// 约束（extends 和 supers 位置可交换）
func process\<TItem extends Comparable, Serializable supers BaseType>(item: TItem): TItem { ... }

// with 约束：要求类型被指定 wrapper 修饰
func dump\<TItem with Serializable>(item: TItem) { ... }

// 型变（同 Kotlin 的 in/out）
class Producer\<out TElement> { ... }
class Consumer\<in TElement> { ... }

// 具化泛型：可以直接当作类型使用
func create\<TResult>(): TResult {
    return TResult()
}
```

### 3.7 运行时类型与反射（`typeOf` / `Type\<T>` / `new` / `with`）

泛型机制在运行时始终携带 typeid（见 RUNTIME.md），因此以下反射能力对所有代码默认可用，无需特殊标注。

**`Type\<T>`**：基本类型之一（`struct`），承载一个运行时类型（本质是对 typeid 的封装）。

**`typeOf`**：取得某个值或类型的运行时类型，返回 `Type\<T>`。

```latte
var box = Box(12, 12, 24)
var t = typeOf(box)              // t: Type\<Box>
```

**`new`**：显式发起一次普通类型构造。其目标可以是静态类型符号，也可以是一个 `Type\<T>` 值；静态类型也可以继续使用 `TypeName(...)` 作为简写。

```latte
const file = new File("./mydoc")

var t = typeOf(box)
var another = new t(12, 12, 24)  // 按 t 所指类型的 init 构造
```

- 对静态普通类型，`new TypeName(...)` 与 `TypeName(...)` 具有相同构造语义；对运行时 `Type\<T>` 值必须使用 `new value(...)`。
- 泛型参数仍可直接写作 `TResult()`，其底层与动态 `new` 使用同一套 typeid 构造机制。
- `enum struct` 是明确例外：无论 init 的可见性如何，都不能通过 `EnumType(...)`、`new EnumType(...)`、`new enumTypeValue(...)` 或泛型 `T()` 直接构造；只能使用其具名 case 入口（见 §12）。
- 当目标类型非静态具体时，init 的重载解析在运行期完成；若目标为抽象类型、enum struct 或找不到匹配的 init，抛出 `core.NoSuchMethodException`。

**在 `is` / `supers` 中使用 `Type\<T>` 值**：`Type\<T>` 的值可当作类型出现在 `is` / `supers` 右侧。

```latte
var t = typeOf(box)
if (obj is t) { ... }
if (obj supers t) { ... }
```

**`with`**：判断某类型是否被指定 wrapper 修饰，可用作运算符或泛型约束（见 §3.6）。

```latte
if (obj with Serializable) { ... }
```

---

## 4. 函数

### 4.1 函数声明

```latte
pub func add(a: i32, b: i32): i32 {
    return a + b
}

// 默认参数
pub func greet(name: String = "World"): String {
    return "Hello ${name}"
}
```

必须使用显式 `return`，不支持隐式返回。

### 4.2 函数调用与具名参数

```latte
greet()
greet(name="Latte")    // 具名参数用 = 而非 :
```

### 4.3 可变参数

```latte
// 位置可变参数
pub func sum(numbers: i32...): i32 { ... }

// 关键字可变参数（类似 Python 的 **kwargs）
pub func config(options: named String...): Config { ... }

// 关键字可变参数 + with 约束（运行时以 Map\<String, typeid> 描述，见 RUNTIME.md）
pub func update\<named TValues... with Serializable>(configs: named TValues...): bool { ... }

// 调用时自动完成类型填充
update(isDarkMode = true, userName = "Andy")
```

### 4.4 扩展函数与扩展字段

使用 `ext` 修饰符：

```latte
pub ext func String.reversed(): String { ... }
pub ext var String.isEmpty: bool { get(_: _) { ... } }
```

### 4.5 `async` 函数与 Task

`async` 是函数修饰符，表示**每次调用该函数时都会立即创建并发布一个新的协程**。`async` 不表示“函数体才可以挂起”：普通函数也运行在当前协程中，因此同样可以执行 `await` 和 `yield`；区别仅在于普通函数调用继续使用当前协程，而 `async` 函数调用创建另一个协程。

```latte
pub async func loadUser(id: i32): SharedUser {
    const response = await requestUser(id)
    return response.user
}

pub async func flushLogs() {
    // 无返回值
}
```

异步函数声明中的返回类型是函数体最终产生的结果类型；调用表达式的类型由编译器改写为：

| 异步函数声明 | 调用表达式类型 |
|---|---|
| `async func f(): TResult` | `core.coroutine.Task\<TResult>` |
| `async func f()` | `core.coroutine.Task` |

```latte
const userTask: core.coroutine.Task\<SharedUser> = loadUser(42)
const flushTask: core.coroutine.Task = flushLogs()
```

调用是 eager 的：协程在调用时启动，不会等到第一次 `await` 才启动。直接丢弃返回的 Task 即表示启动任务后不与其同步，可用于 fork/fire-and-forget：

```latte
flushLogs()              // 启动后继续执行
const user = await loadUser(42)
```

每个新协程在创建时永久绑定一个 `core.coroutine.Executor`。未显式指定时继承当前协程的 Executor；程序只能选择 Executor，不能选择其中的 Worker。Executor 的具体选择接口由 `core.coroutine` API 提供。

async 调用会把一批值从当前协程送进新协程，因此以下**五处**的类型都必须是 §3.1.1 定义的共享安全类型（shared class、shared rich struct/wrapper、非 rich ValueType，以及 `T` 共享安全的 `Nullable\<T>`）：

1. **receiver**：实例方法的 `this`，扩展方法的 `.this`；
2. **参数**：全部形参，含默认参数、具名参数与可变参数展开后的每一个实参类型；
3. **返回值**：即 Task 的结果类型 `TResult`；
4. **捕获变量**：async lambda 从外层作用域捕获的每一个变量；
5. **泛型实参**：async 函数/lambda 的每一个泛型实参——具化泛型下 typeid 与实际值一同跨越边界，因此同样受闸门约束。

编译器在 async 声明处检查 2、3、5 的声明类型，在 async 调用点检查 1、2、5 的实际类型，在 async lambda 处检查 4。违反者为编译错误，不存在运行时补救。

```latte
pub shared class SharedUser { pub const id: i64 }
pub class LocalUser { pub var name: String }

pub async func ok(id: i32, name: String): SharedUser { ... }     // ✅ 全部共享安全
pub async func bad(user: LocalUser) { ... }                       // ❌ 参数是 local object
pub async func alsoBad(): LocalUser { ... }                       // ❌ Task 结果是 local object
```

---

## 5. Lambda 表达式

### 5.1 Lambda 语法

```latte
// 完整形式
func{(x: i32, y: i32): i32 -> (x + y)}

// 带泛型
func{(width: TSize, height: TSize)\<TSize extends Size>: TSize -> ... }

// 带修饰符
pub func{(x: i32): i32 -> (x + 1)}

// 多语句体：用 return@ 显式产出返回值（匿名体的默认标签是 _）
func{(x: i32): i32 -> {
    const doubled = (x * 2)
    return@_ doubled
}}

// named 命名后可用 return@标签 穿透内层匿名块（named 写在 -> 之后、体之前）
func{(x: i32): i32 -> named calc {
    seq {
        return@calc (x * 2)
    }
}}
```

规则：
- 体为单表达式时，该表达式即返回值（隐式取值，无需 `return@`）
- 体为多语句代码块时，所有执行路径都必须显式 `return@_` 或 `return@标签` 产出值——规则同 §6.1；落到块尾而没有 `return@` 是编译错误
- lambda 体内不允许裸 `return`：lambda 不是外层函数的值块，裸 `return` 的指向会含糊（返回 lambda 自身还是穿透外层函数），一律显式写 `return@`

### 5.2 Trailing Lambda

```latte
list.map{(item: String): i32 -> item.length}
```

### 5.3 `async` Lambda

Lambda 可以使用 `async` 修饰。调用 async lambda 与调用 async 函数相同：立即创建新协程并返回 Task。

```latte
const loader = async func{(id: i32): SharedUser -> loadUserNow(id)}
const task: core.coroutine.Task\<SharedUser> = loader(42)
const user = await task
```

普通 lambda 在当前协程中执行；async lambda 在新协程中执行。

---

## 6. `seq` 块（语句块）

`seq` 块等同于 C 的 `{}`，用于创建作用域、限制变量生命周期，不是 lambda。裸 `return`（不带 `@标签`）在 `seq` 块内仍然透传给外层函数，直接参与外层函数的控制流，语义与普通 `{}` 一致：

```latte
pub func process(): i32 {
    seq {
        var temp = computeSomething()
        if (temp > 100) {
            return temp   // 跳出 process，而不是跳出 seq
        }
    }
    // temp 在此处不可见
    return 0
}

// 带修饰符
volatile seq {
    // 此块中的操作标记为 volatile
}
```

### 6.1 作为表达式：返回值与 `named`

`seq` 的一个重要用途是缓解"没有运算符优先级"（见 §1.3）带来的书写负担——用 `seq` 可以近乎零成本地拆出大量命名中间变量，避免把一条表达式硬塞进一堆括号里。为此 `seq` 支持：

- **返回值**：`seq { ... }` 可以整体作为表达式使用，通过 `return@` 语法从块内部产生结果值。
- **命名**：同 §7.4 的循环标签，用 `named` 给 `seq` 块起名，配合 `return@名字` 精确指定从哪一层 `seq` 返回。
- **默认标签**：未显式 `named` 的值块（`seq` 块、if/switch 表达式分支体），其隐式默认标签就是 `_`——`return@_ value` 表示"从最内层这个匿名值块返回 `value`"。

```latte
const result = seq {
    const ac = a * c
    const discriminant = (b * b) - (4.0 * ac)
    const delta = sqrt(discriminant)
    const numerator = (-b) - delta
    const denominator = 2.0 * a

    return@_ numerator / denominator  // 未命名时，默认标签就是 _，指最内层这个匿名 seq
}
```

规则：

- `return@名字` / `return@_` 与裸 `return` 是两回事：前者结束对应的值块并把值作为该块表达式的结果；后者始终穿透值块、直接结束外层函数。
- 当 `seq` 被当作表达式使用时，块内所有执行路径都必须显式 `return@` 出一个值——同 §4.1 函数不支持隐式返回的规则，落到块尾而没有 `return@` 是编译错误。
- 仅作语句使用（不取值）的 `seq` 不受此限制，可以像原来一样不写任何 `return@`。

### 6.2 `using` 资源绑定

Latte 不提供 finalizer。需要确定性释放外部资源的类型实现 `core.IDisposable`：

```latte
pub interface IDisposable {
    func dispose()
}
```

`using` 是 `seq` 的资源绑定子句。可以在 `seq` 与可选的 `named` 之间放置一个或多个 `using(...)`；每个 `using` 内必须是一条单行的 `const` 或 `var` 声明并完成初始化，其结果类型必须实现 `core.IDisposable`。

```latte
seq using(const file = new File("./mydoc"))
using(const stream = new FileInputStream(file))
using(var reader = new StreamReader(stream))
named readFile {
    // Some Logic
}
```

也可以省略 `named`：

```latte
seq using(const resource = openResource()) {
    use(resource)
}
```

语义规则：

- 各个 `using` 绑定按源码顺序从左到右初始化；后一个初始化器可以引用前面已经建立的绑定。
- 离开该 `seq` 时，编译器按声明的逆序调用 `dispose()`；上例的顺序为 `reader` → `stream` → `file`。
- 正常落到块尾、`return@_`/`return@名字`、穿透外层函数的裸 `return`、异常传播以及其他离开该块的控制流都必须执行清理。
- 若某个初始化器抛出异常，只清理此前已经成功初始化的资源。
- `await` 或 `yield` 只挂起 Coroutine，并不离开 `seq`；资源继续保存在该 Coroutine 的执行状态中，直到最终退出作用域。
- `dispose()` 是普通函数，因此可以执行 `await` 或 `yield`。若逆序清理中的某次 `dispose()` 挂起，清理栈、当前资源和尚未清理的资源继续保存在 Coroutine frame 中；恢复后从同一清理进度继续。
- 外层函数的正常 `return`、异常传播或其他终止流程，只有在所有已建立的 `using` 清理完成后才真正继续完成；可挂起的清理不会被跳过。

如果一个实现 `core.IDisposable` 的对象在销毁前从未调用 `dispose()`，无论销毁来自编译器生成的 ARC 路径还是 macroGC，运行时都会立即触发只能由 `core.GlobalExceptionHandler` 提供的机制接收的全局异常。GC 绝不代替用户隐式调用 `dispose()`；普通 `try/catch` 不能拦截该异常。详见 `RUNTIME.md`。

---

## 7. 控制流

### 7.1 条件

```latte
// if 语句
if (condition) {
    ...
}

// if-else 表达式（作为表达式时必须有 else）
var result = if (x > 0) { x } else { opposite(x) }

// if 表达式：多语句分支体，用 return@ 显式产出分支值
var result = if (x > 0) {
    logPositive(x)
    return@_ x                  // 匿名分支体的默认标签是 _
} else {
    return@_ opposite(x)
}

// 用 named 给 if 表达式起名后，return@标签 可穿透内层匿名块精确指定返回目标
var result = if (x > 0) named check {
    seq {
        return@check x          // 从最内层 seq 直接跳出到 check
    }
} else {
    return@check opposite(x)
}
```

if 表达式规则：
- 必须有 `else` 分支
- 分支体为单表达式时，该表达式即分支值（隐式取值，无需 `return@`）
- 分支体含多条语句时，所有执行路径都必须显式 `return@_`（匿名）或 `return@标签`（`if (cond) named 标签` 命名后）产出值——规则同 §6.1；落到块尾而没有 `return@` 是编译错误

### 7.2 switch

switch 有两种形态：表达式形态与语句形态。两者共用同一套匹配规则，区别只在出现位置与分支体：

```latte
// 表达式形态：switch 出现在表达式位置，产出值
var result = switch(expr) {
    (1) -> { "one" }
    (2) -> { "two" }
    (_ > 10) -> { "big" }           // pattern match：使用 _ 引用 expr 的值
    (_ == (3 + 4)) -> { "seven" }   // pattern match
    default -> { "other" }
}

// 语句形态：switch 出现在语句位置，结果值被丢弃
switch(expr) {
    (1) -> { handleOne() }
    (_ > 10) -> {
        logBig(expr)
        handleBig()
    }
    default -> { handleOther() }
}

// 表达式形态：多语句分支体用 return@ 显式产出分支值；named 命名后可用 return@标签
var result = switch(expr) named match {
    (1) -> { return@_ "one" }
    (_ > 10) -> {
        logBig(expr)
        return@match "big"
    }
    default -> { return@match "other" }
}
```

规则：
- 不含 `_` 的分支为值匹配（value match），要求为编译期常量
- 含 `_` 的分支为模式匹配（pattern match），`_` 代表被检查的表达式的值，最终结果必须为 `bool`
- 两种形态都必须有 `default` 分支
- 语句形态的分支体是完整代码块，可写多条语句
- 表达式形态的分支体取值规则同 if 表达式（§7.1）：单表达式分支隐式取值；多语句分支体必须显式 `return@_`（匿名）或 `return@标签`（`switch (expr) named 标签` 命名后）产出值，落到块尾而没有 `return@` 是编译错误

### 7.3 循环

```latte
// for-each
for (item in collection) {
    ...
}

// 范围循环
for (i in 0 to 10) {
    ...
}

// while
while (condition) {
    ...
}

// do-while
do {
    ...
} while (condition)
```

大括号不可省略。

### 7.4 带标签的循环

使用 `named` 关键字声明标签：

```latte
for (i in 0 to 10) named outer {
    for (j in 0 to 10) named inner {
        if (someCondition) {
            break@outer
        }
        continue@inner
    }
}

while (true) named loop {
    ...
    break@loop
}

do named loop {
    ...
} while (condition)
```

### 7.5 `await` 与 `yield`

`await` 是一元运算符，用于等待 `core.coroutine.Task` 或 `core.coroutine.Task\<TResult>`：

```latte
await flushLogs()
const user = await loadUser(42)
const result = await aTaskExpression
```

- `await Task\<TResult>` 在任务成功完成后产生 `TResult`。
- `await Task` 只等待任务完成，不产生值。
- 任务失败时，`await` 在当前位置重新抛出任务保存的异常。
- 任务尚未完成时，当前协程挂起并释放 Worker；任务结束后，当前协程重新进入其原本所属 Executor 的待执行协程池。
- 任务已经结束时，`await` 可以直接取得终态，不要求发生实际挂起。

`yield` 是只能单独出现的语句，不能作为表达式、参数或返回值使用。它有三种形式：

```latte
yield                         // 主动结束当前执行段，重新参与调度
yield pollingAlarm            // 等待 PollingAlarm 就绪
yield eventAlarm              // 等待 EventAlarm 通知
yield sleep(1000)             // 基于 EventAlarm 的非阻塞睡眠
```

裸 `yield` 使当前协程从 Running 回到 Runnable，并重新经过一次所属 Executor 的调度决策；它不保证一定切换到另一个协程，也不保证 FIFO 顺序。

`core.coroutine.PollingAlarm` 提供：

```latte
pub func isReady(): bool
```

执行 `yield pollingAlarm` 后，当前协程挂起。每当 Executor 再次给该等待任务一次调度机会时，运行时调用一次 `isReady()`：返回 `false` 时继续等待，返回 `true` 时才恢复 `yield` 后的代码。`isReady()` 必须同步、线程安全且不得阻塞或挂起。

`core.coroutine.EventAlarm` 由事件源通过 callback 通知 Executor。执行 `yield eventAlarm` 后，协程保持挂起，直到 EventAlarm 发出通知；callback 只负责把协程重新发布到原 Executor，不直接执行用户代码。

标准睡眠函数为：

```latte
core.coroutine.sleep(milliseconds: i32): core.coroutine.EventAlarm
```

它返回一个内部 EventAlarm 子类，事件源为系统时钟树。因此 `yield sleep(1000)` 不会阻塞 Worker 线程。

任何形式的 `yield` 都会结束当前 run-to-suspension 执行段；即使给出的 Alarm 已经就绪，恢复也要重新经过 Executor 调度。

---

## 8. 异常处理

```latte
try {
    riskyOperation()
} catch (e: IOException) {
    handleIO(e)
} catch (_: RuntimeException) {
    // 丢弃异常变量
} finally(e) {
    // e 为 try/catch 中抛出的异常，无异常时为 null
    cleanup()
}
```

---

## 9. 类

### 9.1 类声明

```latte
pub open class Animal {
    pub var name: String
    priv var age: i32

    pub init(_ -> name, _ -> age)

    pub func speak(): String {
        return "..."
    }
}

class Dog : Animal implements Comparable {
    pub init(_ -> name, _ -> age) {
        // ...
    }

    override func speak(): String {
        return "Woof!"
    }
}

pub shared class SharedSession {
    pub var owner: SharedUser
    pub var cursor: Point
}
```

未标记 `shared` 的 class 实例是 local object；标记 `shared` 的 class 实例是 shared object。shared class 的全部继承字段和直接字段只能形成 §3.1.1 所定义的共享闭包；`shared` 按 §3.1.1 单向传染——shared 基类的子类必须 shared，而 shared 子类可以继承非 shared 基类，前提是继承来的字段同样满足共享闭包。`shared` 不等同于锁、原子或 actor isolation；并发读写共享可变字段仍需要显式同步。

### 9.2 修饰符

| 修饰符 | 作用 |
|--------|------|
| `open` | 允许 class 或 rich struct 被继承；`enum struct` 与非 rich struct 明确禁止使用 |
| `abstract` | 抽象（天然 open，与 open 互斥）；非 rich struct 禁止使用 |
| `singleton` | 单例（类似 Kotlin 的 `object`）；实例存储为全局存储，因此必须同时标记 `shared`（§3.1.1） |
| `pub` | 公开访问 |
| `protected` | 子类与同包可见 |
| `internal` | 模块内访问 |
| `priv` | 私有访问（显式，与默认一致） |
| （无） | private（默认） |
| `static` | 静态方法/字段 |
| `rich` | 允许 struct 直接或间接持有 Object；仅适用于 struct/enum struct（wrapper 恒为 rich，不显式书写） |
| `shared` | 将 class 声明为可跨协程共享的对象类型，或将 rich struct / wrapper 声明为可进入共享图的值类型 |
| `async` | 调用时创建新协程并返回 Task；仅适用于函数和 lambda |

### 9.3 构造函数（`init`）

```latte
pub class Point {
    pub var x: i32
    pub var y: i32

    // 参数直接映射到字段（_ 表示参数名与字段名相同）
    pub init(_ -> x, _ -> y)

    // 带默认值
    pub init(_ -> x = 0, _ -> y = 0)

    // 混合：映射参数 + 普通参数
    pub init(_ -> x, _ -> y, label: String) {
        // label 不映射到字段，在函数体中使用
    }

    // 显式参数名
    pub init(horizontal: i32 -> x, vertical: i32 -> y)
}
```

参数映射语法：`[modifier...] param_name[:type] -> field_name [=default_value]`
- `param_name` 为 `_` 时，参数名自动与字段名相同
- `type` 省略时，沿用字段的类型

### 9.4 属性（getter/setter）

getter/setter 可以在以下所有位置定义：类/struct 的字段、全局变量、栈上的 `var` 和 `const`。

```latte
var width: i32 {
    pub get(value: _) {
        return value
    }
    priv set(value: _) {
        // ...
    }
} = 100

// 只定义访问控制，让编译器生成实现
var height: i32 {
    pub get
    priv set
} = 200

// 栈上变量也可以定义
pub func example() {
    var localCounter: i32 {
        get(value: _) { return value }
        set(value: _) { log("set to ${value}") }
    } = 0
}
```

- `value` 参数：表示需要编译器生成 backing field
- `_` 参数：表示不需要 backing field（计算属性）
- get 和 set 在是否需要 backing field 上必须保持一致

### 9.5 内部类

```latte
pub class Outer {
    pub class Inner { ... }
    pub singleton class Companion { ... }   // 类似 Java 静态内部类
}
```

### 9.6 委托（`like`）

```latte
pub class Apple : Fruit like pear {
    pub var pear: Pear = Pear()
    // 将 Fruit 接口的实现委托给 pear 字段
}
```

---

## 10. struct

普通 struct 是不含托管对象引用的 ValueType：

```latte
pub struct Vector2 {
    pub var x: float
    pub var y: float

    pub init(_ -> x, _ -> y)

    pub operator plus(another: Vector2): Vector2 {
        return Vector2(x=(this.x + another.x), y=(this.y + another.y))
    }
}
```

需要让 struct 持有 Object 或内嵌其他 rich struct 时，必须使用 `rich`：

```latte
pub rich struct Entry {
    pub var owner: User
    pub var metadata: Metadata
}
```

需要让 rich struct 安全进入 shared object graph 时，同时使用 `shared rich`：

```latte
pub shared rich struct SharedEntry {
    pub var owner: SharedUser
    pub var location: Vector2
}
```

规则：

- struct 是值类型（`ValueType` 子类），复制、参数传递和装箱继续遵守值语义。
- 非 rich struct 不得直接或间接持有 Object，也不得内嵌 rich struct；其 `refMap` 恒为空。
- rich struct 可以持有 local/shared object 和任意 ValueType。
- shared rich struct 只能持有 shared object、shared rich ValueType 和非 rich ValueType。
- `shared` 不能单独修饰非 rich struct。
- rich/shared 属性属于类型及其布局闭包，泛型实例化和继承后仍必须满足 §3.1.1 的规则。
- **非 rich struct 不得标记 `open` 或 `abstract`**，因此不可能拥有子类型；可被继承的 struct 必须是标记 `open` 的 rich struct。struct 只能继承 struct，不能继承 class，不能实现接口。
- 继承遵守 §3.1.1 的单向传染：rich 基类的子类必须 rich，shared 基类的子类必须 shared；反向可以收紧（非 shared 基类可以有 shared 子类），前提是包括继承字段在内的完整闭包合法。
- `enum struct` 是 struct 的封闭特例：不能标记为 `open`，不能继承用户声明的 struct，也不能被其他类型继承；其固定继承链为 `具体 enum → Enum → ValueType`。
- class 不能继承 struct，struct 不能继承 class。
- 非 rich struct 不能被任何 wrapper 修饰，其字段与实例方法也不能挂载 wrapper（见 §14.9）。

---

## 11. interface

```latte
pub interface Drawable {
    func draw(canvas: Canvas)

    // 默认实现
    func debugDraw(canvas: Canvas) {
        draw(canvas)
    }
}

pub class Circle : Shape implements Drawable {
    // 必须显式实现或显式委托默认实现
    pub override func draw(canvas: Canvas) { ... }

    // 显式使用接口的默认实现
    override func debugDraw(canvas: Canvas) -> Drawable
}
```

子类必须：
- 显式实现自己的版本，或
- 显式指定使用哪个接口的默认实现：`override func method() -> InterfaceName`

---

## 12. enum struct

`enum struct` 是带有编译器隐藏判别字段的 ValueType。它既可以表达传统固定枚举值，也可以表达通过具名 case 接收运行时参数的枚举值。

```latte
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
```

基本规则：

- 固定继承链：`MyEnum` → `Enum` → `ValueType`。`enum struct` 不能标记为 `open`，不能继承用户声明的 struct，也不能被 class/struct/enum 继承。
- 可以有字段、方法和 init，但 init 只供编译器生成的 case 构造入口使用；enum 值不能由用户直接调用 init 创建。
- 枚举 case 在类型声明后的 `[]` 中定义；每个 case 都绑定到一个编译期已解析的 init 调用模板。
- case 名称在同一个 enum 中必须唯一。
- 省略 enum 类型名的 `.CaseName` 必须拥有一个已经确定 enum 静态类型的 receiver/期望类型上下文；编译器不会单凭 case 名反向猜测 enum 类型。

```latte
// 正确：赋值 receiver 已显式指定为 RequestResult
const result: RequestResult = .Success

// 正确：函数参数给出了期望类型
consumeResult(.Success)

// 编译错误：没有任何带类型的 receiver/期望类型
const inferred = .Success
```

### 12.1 固定 case 与参数化 case

case 模板中的普通实参在声明处固定；独占一个实参位置的 `_` 表示调用 case 时必须填入的参数洞。

```latte
pub enum struct RequestResult {
    pub const errorCode: i32

    pub init(_ -> errorCode) {
        // some code
    }
}[
    Success(-1),
    Failed(errorCode = _)
]
```

由此生成的使用形式为：

```latte
const success: RequestResult = .Success
const failed: RequestResult = .Failed(404)
const failedNamed: RequestResult = .Failed(errorCode = 404)
```

- `Success(-1)` 没有参数洞，因此 `.Success` 是固定 case。
- `Failed(errorCode = _)` 有一个 `i32` 参数洞，因此 `.Failed` 是参数化 case。
- `_` 的名称、类型和位置由它对应的 init 参数确定。
- `_` 必须独占一个实参位置；不允许写成 `someExpression(_)`。
- 无论参数取何值，同一个参数化 case 始终只有一个 case 身份。

### 12.2 init 可见性与 enum 构造限制

`enum struct` 的 init 可以使用 `priv`、`protected` 或 `pub`，但其可见性**不产生普通构造能力**：

- 无论 init 是否为 `pub`，源码都不能写 `RequestResult(...)` 直接调用它。
- 写 `new RequestResult(...)`、对保存 `Type\<RequestResult>` 的值使用 `new enumType(...)`，或让泛型 `T()` 在运行时解析到 `RequestResult`，同样是非法构造。
- enum 值始终只能通过 `[]` 中声明的具名 case 入口产生。

init 的 `pub` 含义是允许 case 把 init 的参数暴露为参数洞，从而形成可由调用方使用的参数化 case。绑定到非 `pub` init 的 case 必须是固定 case，不得包含 `_`：

```latte
pub enum struct TokenKind {
    pub const code: i32

    priv init(_ -> code)
}[
    Identifier(1),       // 正确：固定 case
    Number(2),           // 正确：固定 case
    // Custom(code = _)  // 编译错误：参数化 case 要求目标 init 为 pub
]
```

`enum struct` 不参与用户继承，因此 `protected` 不会扩展出任何“派生 enum case 模板”。所有 case 模板仍只声明在本 enum 紧随类型体的 `[]` 中；绑定到 `priv`/`protected` init 的 case 必须是固定模板，只有绑定到 `pub` init 的 case 才能向调用方暴露参数洞。无论访问级别如何，普通表达式都不能直接调用 enum init。

### 12.3 使用 `is` 匹配 case

`is` 的右侧可以是 enum case：

```latte
if (result is .Failed) {
    log(result.errorCode)
}
```

这只检查隐藏判别字段，不比较 payload，也不会改变值的静态类型。参数化 case 在 `is` 右侧不带参数；需要比较完整值时使用 `==`，需要附加 payload 条件时显式组合条件。

```latte
if ((result is .Failed) and (result.errorCode == 404)) {
    ...
}

const message = switch(result) {
    (_ is .Success) -> { "ok" }
    (_ is .Failed) -> { "failed: ${result.errorCode}" }
    default -> { "unknown case" }
}
```

即使源码中已经逐一列出当前声明的全部具名 case，enum 的 `switch` 作为表达式时仍然必须包含 `default`。编译器不以“当前 case 集合看似穷尽”为由删除这一要求；FFI、unsafe/raw memory、反序列化、跨版本 ABI 或其他 corner case 仍可能产生当前源码未声明的判别位模式。

### 12.4 显式判别值与稳定判别 ABI

默认情况下，case 的隐藏判别值由编译器分配，不保证在 case 增删、重排或重新 codegen 后保持不变。需要稳定判别值时，使用 `->`：

```latte
pub enum struct SteadyABIEnum {}[
    First -> 0,
    Second -> 2,
    Third -> 1
]
```

参数化 case 同样可以指定：

```latte
pub enum struct StableRequestResult {
    pub const errorCode: i32
    pub init(_ -> errorCode)
}[
    Success(-1) -> 0,
    Failed(errorCode = _) -> 1
]
```

规则：

- `->` 右侧必须是非负、唯一的编译期整数常量。
- 同一个 enum 的 case 要么全部显式指定判别值，要么全部由编译器分配，不能混用。
- 显式值固定 case 的判别身份；声明顺序仍只影响源码、反射和文档顺序。
- `->` 稳定的是判别值，不自动冻结用户字段的布局。增加字段、修改字段类型或使判别字段由 `u16` 扩展到 `u32` 仍属于 ABI 变化。
- 隐藏判别字段及其整数值不作为普通公开字段暴露。

---

## 13. 运算符

### 13.1 声明

使用 `operator` 关键字替代 `func`，固定的 camelCase 命名：

```latte
pub operator plus(another: MyType): MyType { ... }
pub operator plus\<TAnother extends Addable>(another: TAnother): MyType { ... }
```

### 13.2 固定运算符映射

#### 算术运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `+` | `plus` | `operator plus\<TAnother, TResult>(another: TAnother): TResult` |
| `-` | `minus` | `operator minus\<TAnother, TResult>(another: TAnother): TResult` |
| `*` | `times` | `operator times\<TAnother, TResult>(another: TAnother): TResult` |
| `/` | `div` | `operator div\<TAnother, TResult>(another: TAnother): TResult` |
| `-a`（一元） | `opposite` | `operator opposite\<TResult>(): TResult` |

#### 逻辑运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `a and b` | `and` | `operator and\<TAnother, TResult>(another: TAnother): TResult` |
| `a or b` | `or` | `operator or\<TAnother, TResult>(another: TAnother): TResult` |
| `not a`（一元） | `not` | `operator not\<TResult>(): TResult` |

逻辑运算使用 `and`/`or`/`not` 关键字，没有 `&&`/`||`。

**短路求值**：仅当 `and`/`or` **未被重载**（即作用于内建 `bool`）时才短路求值；一旦作用于重载了 `and`/`or` 的类型，两侧都会被求值（运算符方法的参数先求值再传入）。因此 `a and b` 是否短路取决于操作数的**静态类型**，编译器会在对可能重载的类型使用 `and`/`or` 时给出警告。

#### 位运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `<<` | `leftShift` | `operator leftShift\<TBits, TResult>(bits: TBits): TResult`（低位补 0） |
| `>>` | `rightShift` | `operator rightShift\<TBits, TResult>(bits: TBits): TResult`（高位补符号位） |
| `>>>` | `unsignedRightShift` | `operator unsignedRightShift\<TBits, TResult>(bits: TBits): TResult`（高位补 0） |
| `&` | `bitwiseAnd` | `operator bitwiseAnd\<TAnother, TResult>(another: TAnother): TResult` |
| `\|` | `bitwiseOr` | `operator bitwiseOr\<TAnother, TResult>(another: TAnother): TResult` |
| `!a`（一元） | `bitwiseNot` | `operator bitwiseNot\<TResult>(): TResult` |
| `^` | `bitwiseXor` | `operator bitwiseXor\<TAnother, TResult>(another: TAnother): TResult` |

#### 比较运算符

相等与排序分离为两个运算符：

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `==`/`!=` | `equals` | `operator equals\<TAnother>(another: TAnother): bool` |
| `<`/`>`/`<=`/`>=` | `compareTo` | `operator compareTo\<TAnother>(another: TAnother): core.ComparisonResult` |

- `!=` 由 `equals` 取反自动推导。
- `<`/`>`/`<=`/`>=` 由 `compareTo` 的结果推导。
- `core.ComparisonResult` 枚举值：`.Equal`、`.GreaterThanAnother`、`.LesserThanAnother`。
- 两者返回类型固定，因此不需要 `TResult`。仅需相等语义的类型只实现 `equals` 即可，无需具备全序。

#### 索引运算符

| 运算符 | 名称 | 签名 |
|--------|------|------|
| `[]` 读取 | `getAtIndex` | `operator getAtIndex\<TElement, TIndex>(index: TIndex): TElement` |
| `[]` 赋值 | `setAtIndex` | `operator setAtIndex\<TElement, TIndex>(index: TIndex, element: TElement)` |

#### 通用规则

- `+=`/`-=`/`*=`/`/=`/`<<=`/`>>=`/`>>>=`/`&=`/`|=`/`^=` 从对应运算符自动推导
- 不可自定义新运算符名称

---

## 14. Wrapper（修饰器）

### 14.1 概述

Wrapper 是绑定到被修饰实体生命周期的**值**，类似 Python 装饰器 + Java 注解的混合体。

- 继承链：`MyWrapper` → `Wrapper` → `ValueType`
- wrapper 恒为 **rich struct**：值语义、unique ownership，因此生命周期可以直接绑定被修饰的实体、方法或值（类似 `unique_ptr`，而不是引用计数共享）；`rich` 由 `wrapper` 声明形式隐含，不显式书写
- 可选标记 `shared`，这会同时放宽可修饰的目标、收紧自身字段闭包（见 §14.9）
- 三种目标（互斥）：`.Entity`、`.Method`、`.Value`
- 嵌套顺序：按声明顺序从外向里
- 通过 `:` 运算符访问：`obj:MyWrapper`（链式 `obj:A:B` 表示"obj 的修饰器 A 的修饰器 B"），该表达式是**只读的存储位置**——只能作成员访问的接收者，不可整体赋值也不可整体取值（见 §14.5）

### 14.2 实体修饰器（Entity Wrapper）

修饰 class、interface、wrapper、rich struct（含 rich enum struct）。非 rich struct 不是合法目标，见 §14.9。

```latte
@WrapperTarget(.Entity)
pub wrapper Logged\<TTarget> {
    pub init(level: String = "INFO")

    // specific 方法代理
    operator .proxy.doSomething(arg: i32): String {
        log("calling doSomething")
        return inner(arg)   // 向内层传递
    }

    // specific 运算符代理
    operator .proxy.opr.plus(another: TTarget): TTarget {
        return inner(another)
    }

    // specific 字段 getter 代理
    operator .proxy.get.name\<TField>(value: TField): TField {
        return value
    }

    // specific 字段 setter 代理
    operator .proxy.set.name\<TField>(value: TField) {
        inner(modifiedValue)
    }

    // 四类 universal wildcard proxy：每类在同一个 wrapper 中最多实现一个
    operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(
        symbol: String,
        namedArgs: named TNamedArgs...,
        unnamedArgs: TUnnamedArgs...
    ): TReturn {
        log("calling ${symbol}")
        return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs)
    }

    operator .proxy.get.*\<TValue>(
        symbol: String,
        value: TValue
    ): TValue {
        return value
    }

    operator .proxy.set.*\<TValue>(
        symbol: String,
        value: TValue
    ) {
        inner(value)
    }

    operator .proxy.opr.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(
        symbol: String,
        namedArgs: named TNamedArgs...,
        unnamedArgs: TUnnamedArgs...
    ): TReturn {
        return inner(namedArgs=namedArgs, unnamedArgs=unnamedArgs)
    }
}
```

- `TTarget` 泛型参数可访问被修饰对象的类型；不写约束时可以是任何合法的 wrapper 目标类型，可用 `extends`/`supers` 进一步缩窄。
- `self` 关键字访问被修饰的对象实例（类型为 `TTarget`）。
- `this` 仍为 wrapper 自身实例。
- `.proxy.*`、`.proxy.get.*`、`.proxy.set.*`、`.proxy.opr.*` 不再是可声明多个并按 pattern/优先级竞争的代理；它们分别是普通方法、getter、setter、operator 类别的唯一 universal fallback。
- 同一个 Entity Wrapper 对每一类别只能实现零个或一个 wildcard proxy；重复声明同类别 wildcard 是编译错误。
- 四类 wildcard 的泛型与参数形状是编译器规定的 canonical shape，不能通过额外约束或部分参数 pattern 把它缩窄为只吃某些签名。需要特殊处理某个已知成员时使用 specific proxy；需要在 universal fallback 内进一步分类时显式检查 `symbol`。

### 14.3 值修饰器（Value Wrapper）

修饰字段或栈上变量（`var`/`const`）。修饰实例字段时，wrapper 存放在宿主类型的隐藏字段中，因此宿主必须能够内嵌 rich struct（见 §14.9）；修饰栈上变量时 wrapper 存放在栈帧中，对变量类型没有额外要求。

```latte
@WrapperTarget(.Value)
pub wrapper Clamped {
    pub var min: i32
    pub var max: i32

    pub init(_ -> min, _ -> max)

    // 必须至少实现 get（只实现 get 则只适用于只读变量）
    operator .proxy.get\<TValue>(value: TValue): TValue {
        // ...
        return value
    }

    // 实现 set 以支持可变变量
    operator .proxy.set\<TValue>(value: TValue) {
        inner(clampedValue)
    }
}

// 使用
@Clamped(0, 100)
var health: i32 = 50
```

### 14.4 方法修饰器（Method Wrapper）

修饰 lambda 或方法。

```latte
@WrapperTarget(.Method)
pub wrapper Timed {
    pub init()

    // 代理方法调用（参数名和类型都必须匹配）
    operator .proxy.call\<TReturn>(): TReturn {
        var start = now()
        var result = inner()
        log("took ${(now() - start)}ms")
        return result
    }

    // 通配符 + 可变参数（"至少有前面这些参数的方法"）
    operator .proxy.call(.name: String, args: named Any...): Any {
        return inner(args)
    }
}

// 使用
@Timed()
pub func heavyComputation(): i32 { ... }
```

### 14.5 使用 Wrapper

```latte
@Logged("DEBUG")
@Serializable()
pub class MyService {
    ...
}

// 访问 wrapper：obj:Logged 是宿主持有的那份 wrapper 的只读 place
var service = MyService()
service:Logged.level = "TRACE"       // ✅ 成员访问：原地作用于宿主那份
service:Logged.dump()                // ✅ 方法调用：receiver 是宿主那份

service:Logged = otherLogged         // ❌ 编译错误：wrapper place 不可被赋值
const snapshot = service:Logged      // ❌ 编译错误：wrapper 不能被整体取出
takeWrapper(service:Logged)          // ❌ 同上：不能作实参、返回值或推断源
```

**`obj:Wrapper` 是只读 place（readonly l-value）。** 它求值为绑定在 `obj` 上的那份 wrapper 存储位置本身，但只能出现在**成员访问的接收者位置**——读写其字段/属性、调用其方法，一律原地作用于宿主持有的那份，不产生副本。

它**不是**一个可以整体流动的值：

- 不可作为赋值目标（`obj:W = ...` 非法），wrapper 实例只能由 `@W(...)` 在宿主创建时安装；
- 不可出现在任何取值位置（赋给变量、作实参、作返回值、作类型推断源），因此**无法把 wrapper 从宿主里复制出来**。

理由与 §14.9 的「wrapper 恒为 rich struct」同源：wrapper 是 unique ownership 的值，生命周期与被修饰实体同生共死。允许整体取值就会造出一份脱离宿主而独立存活的 wrapper 实例，允许整体赋值就会在宿主生命周期内替换掉这份绑定——两者都直接破坏该不变量，所以在语法层封死，而不是靠约定。

wrapper 自身的字段可变性仍按普通规则由字段声明（`var`/`const`）与可见性决定；"只读"约束的是 `obj:Wrapper` 这个 place 整体，不是其成员。

proxy 方法体内的 `this` 同样是原地访问宿主持有的那份 wrapper，因此 `@Clamped(0, 100)` 这类可变 wrapper 状态在多次调用之间保持一致。

### 14.6 派发顺序与 wildcard 唯一性

当一个调用同时被多个 wrapper 命中时：

- **跨 wrapper**：按声明顺序从外到内（outer → inner）嵌套。
- **同一 wrapper 内**：匹配的 specific proxy 优先于对应类别的 wildcard proxy；二者是择一关系，不会在同一 wrapper 层同时执行。
- **同一 wrapper 内**：普通方法、getter、setter、operator 四个类别分别最多存在一个 wildcard proxy，因此不存在同类别 wildcard 的重叠、排序或 priority。
- specific proxy 或 wildcard proxy 调用 `inner(...)` 后，下一层 wrapper 独立重复同一套 specific → wildcard → 实体成员/下一层的选择。

`@ProxyPriority` 不再存在；编译器不进行 wildcard pattern 重叠分析，也不维护任何用户指定的数值优先级。

### 14.7 未声明方法的动态降级

当对某个值调用其**静态类型上未声明**的方法，且该类型的 wrapper 链中存在普通方法类别的 `.proxy.*` 时，调用会**降级**为动态派发（编译为对统一 `call???` 的调用，见 `RUNTIME.md`）；否则为编译错误。

```latte
// service 的静态类型上没有 fetchUserById，但存在 .proxy.*
service.fetchUserById(42)     // 降级为携带 canonical symbol 的 call??? 请求
```

- 一旦 wrapper 链中存在 `.proxy.*`，对该静态类型未声明方法的调用不再具有原成员声明提供的静态类型保证。
- 实参按统一胖值 ABI 传入；返回值在调用点按期望类型插入一次转换，不符则抛 `core.CastException`。
- 无任何 wildcard proxy 可路由请求时，最终落到 `Any.call???` 的默认实现并抛 `core.NoSuchMethodException`。
- getter、setter 和 operator 在编译器 lowering 后同样是方法请求；运行时仍只保留一个 `call???` slot，并由编译器生成的 router 根据 `symbol` 将到达该入口的请求转入 `.proxy.get.*`、`.proxy.set.*` 或 `.proxy.opr.*`。这不要求为三类操作额外增加 `get???`、`set???` 或 `opr???` slot。

### 14.8 canonical symbol

wrapper wildcard 与 `call???` 接收的 `symbol: String` 是编译器生成的 canonical 调用身份，而不是仅包含成员短名的普通字符串。格式如下：

```text
类型：
命名空间::类名[.子类名...]

方法：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static.]方法名([参数名:参数类型,...])@返回值类型

字段 / 全局变量 / 全局常量：
命名空间::[可能有的类名[.可能有的子类名...]]#[.static.]名称@字段类型

运算符：
命名空间::类名[.可能有的子类名...]$$运算符名称([参数名:参数类型,...])@返回值类型

getter：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static].get.名称@字段类型

setter：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static].set.名称@字段类型
```

`.static.` 只用于静态方法、静态字段及其 getter/setter；Singleton 的语义与 static 的区别见 class/Singleton 规则，Singleton 实例成员不因类型为 singleton 而自动编码成 `.static.`。

泛型与可变参数在 canonical 请求中被编译器展开为保留名称的隐藏参数：

| 源声明形态 | canonical 隐藏参数 |
|---|---|
| 单个泛型参数 `T` | `.generic.T: Type` |
| 匿名可变泛型参数 `TArgs...` | `.generic.TArgs: Array\<Type>` |
| 具名可变泛型参数 `named TArgs...` | `.generic.TArgs: Array\<Pair\<String, Type>>` |
| 匿名值可变参数 `args...` | `.vargs.args: Array\<Any>` |
| 具名值可变参数 `named args...` | `.kwargs.args: Array\<Pair\<String, Any>>` |

这些以 `.` 开头的名称由编译器保留，普通源码参数不能声明同名标识符。canonical symbol 连同 hidden arguments 完整描述本次调用的类别、声明位置、static 属性、参数类型、泛型实参和返回类型；具体 Native 路由见 `RUNTIME.md` §14。

### 14.9 wrapper 的 `rich`/`shared` 规则与目标矩阵

**wrapper 恒为 rich struct。** 这是 wrapper 语义的基础而非实现细节：wrapper 实例必须与被修饰的实体、方法或值同生共死，因此它必须是 unique ownership 的值，而不是可被任意别名的引用类型。作为 rich struct，它既保有值语义，又可以持有 Object 字段。

- `rich` 由 `wrapper` 声明形式隐含，**源码中显式书写 `rich wrapper` 是编译错误**（冗余修饰）。BIL 作为显式 IR 不做此隐含，wrapper 类型声明的修饰符列表中必须显式含 `rich`（见 `BIL_STANDARD.md` §8.2）。
- wrapper 可以标记 `shared`，成为 shared rich 值：它的字段闭包按 §3.1.1 收紧为只能持有 shared object 与共享安全 ValueType，换来可以修饰任意目标的资格。
- wrapper 不能标记 `open`/`abstract`（rich struct 的继承规则另有约束时以 §10 为准），也不能标记 `singleton`。

**宿主可内嵌性（对全部三类 wrapper 生效）**：wrapper 实例存放在宿主的编译器生成隐藏字段中（`BIL_STANDARD.md` §5.3），因此宿主类型必须允许内嵌 rich struct。由此：

- 合法的 Entity wrapper 目标是 class、interface、wrapper、rich struct、rich enum struct；
- **非 rich struct 与非 rich enum struct 不能被任何 wrapper 修饰**，它们的字段不能挂 Value wrapper，实例方法也不能挂 Method wrapper；
- 因此全部基元类型、`String`、`Type\<T>`、`Span\<T>` 都不可被修饰；
- 修饰栈上变量、全局/静态字段、全局/静态方法时不涉及宿主内嵌，本条不适用。

**shared 目标矩阵**：

| wrapper | 可修饰的目标 | 自身字段闭包 |
|---|---|---|
| `shared wrapper` | 全部合法目标（含 shared 类型、全局/静态成员） | 按 §3.1.1 的 shared 闭包收紧 |
| 非 shared `wrapper` | 仅非 shared 目标（下表四类） | 按 §3.1.1 的 rich 闭包，可持有 local object |

非 shared wrapper 可修饰的「非 shared 目标」是：

- **A. 方法**：不是全局方法或静态方法，且所属类型不是 shared；
- **B. 字段**：不是全局字段或静态字段，且所属类型不是 shared；
- **C. 栈上变量**：全部 `var`/`const` 局部变量；
- **D. 类型**：非 shared 的类型。

其根据是 §3.1.1 的逃逸闸门：全局/静态存储与 shared 类型的字段闭包都不得触及 local object，而非 shared wrapper 的隐藏字段可能持有 local object。反过来，shared wrapper 修饰非 shared 目标始终合法——shared 闭包比 local 闭包更严，不会引入新的逃逸路径。

**interface 目标的传染校验**：interface 本身不产生实例，被修饰 interface 的 wrapper 实例落在每个实现者上。因此：

- 被修饰的 interface 的所有实现者必须自身是合法 wrapper 目标（class、rich struct、rich enum struct）；
- 被**非 shared** wrapper 修饰的 interface **不得被 shared 类型实现**（否则 shared 实现者会获得一个可能持有 local object 的隐藏字段）。

这两条在实现者声明处检查并报错，而不是在 interface 声明处。

---

## 15. 模块系统

### 15.1 命名空间声明

```latte
namespace com.example.myapp
```

### 15.2 导入

```latte
import core.collections.List             // 单个导入
import core.collections.{List, Map}      // 多个导入
import core.collections.*                // 全部导入
```

规则：
- `{}` 列表项只能是单标识符，不允许带路径——`import core.collections.{a.List}` 是编译错误。需要导入不同子路径的符号时写多条 `import` 语句。

---

## 16. 访问修饰符

| 关键字 | 可见性 |
|--------|--------|
| `pub` | 公开（所有地方可见） |
| `protected` | 子类和同包可见 |
| `internal` | 模块内可见（项目级别） |
| `priv` | 私有（当前类/文件内可见，显式） |
| （无） | private（默认，当前类/文件内可见） |

默认访问级别为 private。构造函数、对外可见的字段和方法都需要显式标注 `pub`。

---

## 17. 程序入口

```latte
// 无参数
pub func main(): i32 {
    return 0
}

// 带命令行参数
pub func main(args: Array\<String>): i32 {
    return 0
}

// 无返回值
pub func main() {
    ...
}
```

运行时从 `main` 开始就创建根协程，并默认将其绑定到 `core.coroutine.MainExecutor`。因此 `main` 以及由它同步调用的普通函数可以直接使用 `await` 和 `yield`，不需要给 `main` 添加 `async` 修饰符。

---

## 18. 解构声明

```latte
var (key, value) = pair   // pair 必须为 core.Pair\<TKey, TValue> 的子类
```

---

## 19. 关键字一览

### 声明关键字
`func`, `var`, `const`, `class`, `struct`, `interface`, `enum`, `wrapper`, `operator`, `init`, `namespace`, `import`

### 修饰符关键字
`pub`, `priv`, `protected`, `internal`, `open`, `abstract`, `singleton`, `static`, `ext`, `override`, `named`, `rich`, `shared`, `async`

### 控制流关键字
`if`, `else`, `switch`, `default`, `for`, `in`, `to`, `while`, `do`, `break`, `continue`, `return`, `yield`, `try`, `catch`, `finally`, `throw`

### 运算符关键字
`and`, `or`, `not`, `is`, `supers`, `as`, `with`, `new`, `typeOf`, `await`

### 类型关键字
`extends`, `supers`, `implements`, `like`, `with`

### 其他关键字
`this`, `self`, `inner`, `true`, `false`, `null`, `seq`, `using`
