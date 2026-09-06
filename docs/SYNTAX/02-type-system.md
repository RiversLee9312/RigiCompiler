# 类型系统（§3）

类型声明可带 `unsafe`，与访问级别、`shared` 等声明修饰符的排列顺序无关。
使用该类型的构造与危险成员需要显式 unsafe 上下文；类型修饰符不会自动开启
成员实现的 unsafe 权限。安全类型可私有持有 unsafe 类型，但安全公开签名不能
经返回值、参数、字段或泛型实参暴露它。字段本身不能声明 `unsafe`。

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 3. 类型系统

### 3.1 类型层级

```
Any
├── Object
│   ├── Nullable\<T>
│   ├── Box\<T extends ValueType>   // 语法上属于 Object、Native 层无独立 Box TypeSheet 的系统特权载体，见 RUNTIME.md
│   ├── Span\<T extends ValueType>  // 内建 class，引用语义，见 §3.1.2 / RUNTIME.md §5
│   ├── SharedSpan\<T extends ValueType> // shared class 变体；元素须非 rich 或 shared rich
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
    ├── Wrapper                     // 所有 wrapper 的基类；wrapper 恒为 rich struct，见 §14.9
    │   └── ... (所有 wrapper)
    └── ... (所有 struct)
```

`String` 与 `Wrapper` 都在 `ValueType` 分支下：前者是不含托管引用的非 rich 值类型，后者是 rich 值类型。二者的定位理由分别见 §3.1.2 与 §14.9。

### 3.1.1 `rich` 与 `shared` 类型修饰符

Rigi 将值类型按是否允许携带托管对象引用分为普通 ValueType 与 rich ValueType，并将对象按是否允许跨协程共享分为 local object 与 shared object。`rich` 和 `shared` 都是**类型声明修饰符**，不是变量或引用位置修饰符。

- `rich` 仅用于 `struct`（包括 `enum struct`）与 `wrapper`。未标记 `rich` 的 struct 不得直接或间接持有任何 Object，也不得内嵌 rich struct。
- `wrapper` 恒为 rich struct，`rich` 由声明形式隐含，源码中不再显式书写（见 §14.9）。
- `shared` 可用于 `class`、`interface`，或与 `rich` 一起用于 struct，或用于 `wrapper`。`shared struct` 而没有 `rich` 是编译错误。
- 未标记 `shared` 的 class 实例是 local object，只属于创建它的 Coroutine，不得在 Coroutine 之间共享。
- 标记 `shared` 的 class 实例是 shared object，可以被多个 Coroutine 引用；`shared` 只表示共享资格和相应的生命周期管理，不自动使对象字段操作具备线程安全。
- `shared rich struct` 仍然是值类型，复制与装箱继续采用值语义/unique ownership；`shared` 表示它的字段闭包可以安全地进入 shared object graph。

```rigi
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

这些限制递归应用于字段、继承得到的字段、泛型实参所展开的字段、编译器生成的隐藏字段，以及 Middleware 合成的 wrapper 隐藏存储（§14.9）。由此保证：从任意 shared class、shared rich struct 或 shared wrapper 出发，沿字段递归遍历，不可能到达 local object 或非 shared rich 值。

**泛型实例化点闭包检查**：`rich`/`shared` 属性属于类型及其布局闭包，泛型实例化后仍必须满足。每次调用方填入泛型实参（类型引用实例化、泛型调用显式实参），编译器对构造类型**自身**重跑闭包表：持有者分类取定义，字段类型代入实参后按上表直接判定，嵌套构造（如 `Box\<Wrap\<User>>`）递归到内层用户构造。因此 `struct Wrap\<T> { var v: T }` 以 `Wrap\<User>`（User 为 class）实例化是编译错误（非 rich struct 经实参持有 Object），诊断定位到填入点并注明经哪个实参引入。同理，shared 持有者经实参持 local 字段、泛型类型代入后的静态字段（本条下方闸门 1）与 async 成员签名（§4.5 闸门 2/3）的共享安全性，也在填入点统一收口。实参仍含未代入泛型参数时（泛型声明体内）跳过，由外层代入后再查。

**`rich` 与 `shared` 的传染性（单向）**：

- 基类为 `shared` 时，子类必须为 `shared`；
- 基类为 `rich` 时，子类必须为 `rich`；
- 接口继承 `shared` 接口时，派生接口必须为 `shared`；
- 非 `shared` class 不得实现 `shared` 接口（struct 本来就不能实现接口）；
- 声明了 `async` 成员的接口必须标记 `shared`——经接口类型调用 async 成员时，receiver 的静态类型就是接口本身，`shared` 是它通过 §4.5 闸门 1 的前提。

反向不成立：`shared` 子类可以继承非 `shared` 基类，`rich` 子类可以继承非 `rich` 基类，`shared` class 也可以实现非 `shared` 接口。这类子类必须让**包括继承字段在内的完整字段闭包**满足上表——基类若持有 local object 字段，把子类声明为 `shared` 并不能使该继承合法，编译器直接拒绝。因此一个类型的 rich/shared 属性只能由它自己的声明决定，不能从基类推断；编译器按声明保守判定，安全性由闭包检查兜底。

**共享安全类型（shared-safe type）**：满足以下任一条件的类型是共享安全类型——

- shared class、shared interface（含 `SharedSpan\<T>`）；
- shared rich struct、shared wrapper；
- 非 rich ValueType（全部基元类型、`String`、`Type\<T>`、非 rich struct 与非 rich enum struct）；
- `Nullable\<T>`，且 `T` 本身是共享安全类型（见 §3.1.2）。`T` 为泛型参数时按其 `extends` 界链推导（界为外层型参则递归；环界保守视为非共享安全）。

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

Rigi 不要求每一个源码类型节点都一一对应一个普通 Native 对象类型或独立 `TypeSheet`。少数内建抽象由编译器与运行时共同提供特权 lowering；它们在语法、类型检查和泛型约束中表现为正常类型，但物理表示可以绕过普通用户类型的对象模型。

- `Box\<T extends ValueType>` 在语法类型层级中属于 `Object`，可以进入 `Object`/`Any` 多态位置并满足相应约束；但它不是普通 class，不生成 Box 对象头、Box identity 或独立的 `Box\<T>` TypeSheet。Box 槽中的 typeid 始终是底层实际 ValueType `T` 的 typeid，Native 表示与复制/销毁规则见 `RUNTIME.md` §4。
- `Span\<T extends ValueType>` 是**内建 class**（Object 分支，引用语义）：连续原生缓冲区后门，不按普通泛型容器的 16 字节元素槽布局；复制与传参共享同一 buffer。索引、步长与 GC 扫描均使用内建 lowering（见 `RUNTIME.md` §5）。
- `SharedSpan\<T>` 是 `Span\<T>` 的 shared class 变体，布局相同；元素约束收紧为「非 rich 或 shared rich ValueType」。Mutex 等同步原语由未来版本接入。
- `String` 是**非 rich ValueType**：它不持有托管引用，`refMap` 恒为空，因此可以自由出现在全局/静态字段与 async 边界上（见 §3.1.1），无需任何 shared 标注。它的字符数据位于编译器与运行时管理的特权裸缓冲区中，不是普通 Object 字段。
  - **复制语义按值深拷贝**：`var b = a` 在语义上产生一份独立的字符数据。实现可以引入对用户完全透明的 copy-on-write 或不可变共享优化，但**源码语义、类型检查与用户代码一律不得假设这些优化存在**——正如 BIL 永远不得假设某种 GC 模型或 GC 行为。任何可观察到共享的行为都是实现缺陷，而不是可依赖的特性。
  - `String` 不可被继承，也不可被 wrapper 修饰（非 rich struct 的通用规则，见 §14.9）。
- `Nullable\<T>` 属于 `Object` 分支，但其 shared 属性由 `T` 推导而非由声明给出：`T` 是共享安全类型时，`Nullable\<T>` 也是共享安全类型。内建 `Array\<T>` 同样按元素推导共享安全，但不提供并发同步；普通 `core.collections.List/Map` 不适用本规则。需要线性化并发操作时选用 `core.AtomicArray/AtomicList/AtomicMap`，API 与元素能力见 §20.7。
- `Cell\<T>` / `ReadonlyCell\<T>`（§5.2 / §15.3）的物理表示是编译器特权：用户源码不可见、不可直接声明或 `new` 基类；实际实例恒为编译器逐变量合成的隐藏子类（同 Box——物理表示属编译器特权，见统一 cell 存储）。
- 其他由规范明确标记为内建、编译器生成或系统特权的机制，也可以拥有普通用户类型不能声明或复制的 lowering、布局或派发规则。

这些特权只属于语言规范明确列出的内建机制。用户声明的 class、struct、interface 或 wrapper 不能通过源码复制其布局、身份、派发或生命周期规则。

`placeOf` 是前缀关键字，`placeOf(x)` 的紧邻括号伪调用语法不合法；`placeOf (x)` 或注释分隔的分组操作数仍可解析，稳定存储要求由语义阶段检查。

`placeOf operand` 返回 local `core.Place\<T>`，实现 `IDisposable` 并保留目标身份。Object 使用对象自身身份；值类型的稳定局部变量、参数或全局存储复用捕获 Cell，常量使用 ReadonlyCell，临时值被拒绝。`==` / `!=` 比较目标身份；`dispose()` 释放 Place 自己的持有，不释放其他 Place/Handle 的持有。当前普通实例字段、未落地 companion 静态字段与复杂索引不支持稳定提升，必须给出诊断。

`Place.expose(): Handle\<T>` 是 unsafe 能力升级入口。`Handle\<T>` 与可写变体 `MutableHandle\<T>` 是 unsafe shared object，可保留 local T，但没有普通 Rigi T 字段；两者不实现 `IDisposable`，按普通 shared ARC 生命周期释放隐藏目标。`load(): T` 对 Object 返回对象引用，对 Cell 返回正常值副本。只有逻辑 T 是 ValueType 且目标为可写 Cell 时，`asMutable()` 成功；Object、ReadonlyCell 或 `Handle\<Any>` 均抛 `core.ImmutablePlaceException`。`MutableHandle.store(T)` 写回同一 Cell。用户不能构造、继承或借 native 声明伪造 Handle。

`core.Atomic\<T>` 是 unsafe shared object，仅私有持有 Handle 与 Mutex，允许 local T。unsafe `init(T)`、`load(): T`、`mutate(Func\<T,T>)` 通过稳定参数/局部 Place 建立能力。load/mutate 是普通同步方法，内部取得异步 Mutex 后在 finally 释放；mutate 仅在回调成功返回后替换 Handle，回调抛错保留旧值（不回滚用户另行 unsafe 修改的对象）。普通回调可挂起，锁仍保持。

`core.AtomicStruct\<T extends ValueType>` 是安全 shared 门面，公开 safe `init(T)`、`load(): T`、`store(T)`；store 内部通过私有 Atomic 的回调替换值，不向用户提供 mutate，也不暴露 Atomic/Handle/Cell。

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
| `Span\<T extends ValueType>` | 连续、无装箱的缓冲区对象（内建 class，引用语义，见 RUNTIME.md §5） | Object |
| `SharedSpan\<T extends ValueType>` | Span 的 shared class 变体（元素须非 rich 或 shared rich；Mutex 未来） | Object |

### 3.3 字面量

```rigi
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
3.14e5           // 科学计数法（= 314000）
3.14e-5          // 指数可带 +/- 符号
2e3              // 无小数点形态（= 2000）

// 字符串
"Hello ${expr}"          // 字符串插值（非 String 段按 toString 转换后拼接，§3.8）
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

浮点字面量支持科学计数法形态：指数标记 `e`/`E` 后可带可选的 `+`/`-` 符号，其后必须至少有一位十进制指数数字（如 `3.14e5`、`3.14e-5`、`2e3`），可与 `f`/`F` 后缀组合（`1.5e3f`）；`e`/`E` 后无合法指数数字（如 `3.14e-`、`3.14e+x`）是编译错误。

整数字面量后可直接跟成员访问：`7.twice()` 中 `.` 后继是标识符时按路径表达式解析（等价 `(7).twice()`，含扩展成员）；`.` 后继是数字时仍是浮点字面量（`7.5`）；`.` 后既非数字也非标识符（如 `3.` 收尾）是编译错误。

**负号折叠**：一元负号 `-` 直接作用于整数字面量时（之间只允许空白/换行），负号并入字面量参与定型与解析期范围检查，由此各符号整数类型的下界可以直接书写（`-2147483648` 是合法 i32、`-128B` 是合法 i8、`-9223372036854775808L` 是合法 i64）；超出下界（`-2147483649`）是编译错误，无符号类型不允许负值（`-1U` 是编译错误）。负号与字面量之间有括号或其他语法介入时不折叠：字面量按正数区间检查（`-(2147483648)`、二元减号右侧的 `a - 2147483648` 均因超 i32 上限报错），负号保持为一元运算符 `opposite`（如 `-x`、`-(5)`、`-1.5`）。

字符字面量规则：单引号内必须恰好是一个字符或一个转义序列，类型为 `char`（§3.2）；空（`''`）或多于一个字符（`'ab'`）是编译错误。

多行字符串（`"""`）是编译期处理的严格多行形式（Swift 风格）：

- 开界 `"""` 后必须紧跟换行，该换行不属于内容；`"""hello` 这类开界同行写法是编译错误。
- 闭界 `"""` 必须独占一行：它前面只允许空白字符，闭界前的换行不属于内容。闭界出现在内容行中间（前面已有非空白内容）是编译错误——内容中的三引号须写成 `\"""`。
- 缩进剥除：闭界行的前导空白量 N 是剥除基准，每个内容行的前 N 个字符必须均为空白并被剥除；前导空白不足 N 的非空内容行是编译错误。全空白的内容行剥除后输出空行。
- 转义与单行字符串相同（`\n`、`\t`、`\\`、`\"`、`\$` 等），未知转义是编译错误；剥除缩进先于转义处理，行内单个 `"` 或 `""` 免转义。
- 内容中的换行恒为 `\n`（行尾归一在词法入口完成）；`${}` 插值与单行字符串一致；`\$` 转义的字面 `$` 不构成插值引导（单行/多行相同）。

```rigi
var text = """
    line1
    line2 with "quotes" and ${interp}
    """
// 等价于单行写法 "line1\nline2 with \"quotes\" and ${interp}"
```

### 3.4 空安全

类型默认非空，`T?` 表示可空类型（底层为 `Nullable\<T>`，`Object` 子类）。

由于 `Nullable\<T>` 是 `Object` 子类，可空的值类型会被装箱；已经可空的值不会被再次装箱，语法上也不允许对 `Nullable\<T>` 再次施加 `?`（不存在 `T??`）。

`Nullable\<T>` 的 shared 属性由 `T` 推导：`T` 是共享安全类型时 `Nullable\<T>` 也是，因此 `String?`、`SharedUser?` 可以出现在全局字段与 async 边界上，而 `LocalUser?` 不可以（见 §3.1.1、§3.1.2）。非空值可赋给对应可空类型（装箱视图：`i32 → i32?`、无约束 `T → T?`）；`T extends B` 时 `T` 也可赋给 `B?`（先按界代入再装箱），但无约束 `T` 不能赋给无关类型的 `U?`。

```rigi
var name: String? = null

// 安全调用
name?.length

// 安全调用 + 空值回退（替代 Kotlin 的 ?:）
name?.length if? 0

// 索引读取（§13.2：getAtIndex 一律返回 T?，越界得 null）
arr[0] if? -1

// 安全 let
name?.let{(it: String) -> doSomething(it)}

// 安全类型转换
obj as? String
```

`?.` 与 `if?` 的定型规则：

- `?.` 的接收者必须是可空类型 `T?`；其后的成员段在非空类型 `T` 上解析。结果的类型：成员类型本身可空时原样保留，否则包装为对应的可空类型（不二次包装）。可空值不提供隐式成员访问——`a?.b.c` 中 `.c` 作用在可空结果上是编译错误，须逐段标注（`a?.b?.c`）。语句位置允许经 `?.` 调用无返回（void）方法（如 `h?.bang()`）：接收者为 `null` 时整体不调用、不产生值，故该形态不能用作值；async 无返回方法调用点类型为 `Task` 句柄（§4.5），走常规值路径不在此列。`T` 为泛型参数时同样适用：段在 `T` 的有效成员类型上解析（§3.6）。
- `if?` 的左操作数必须是可空类型 `T?`（含 `T` 为泛型参数的 `T?`）；右操作数（空值回退值）必须可赋值到 `T`，整个表达式的类型为 `T`。右操作数延迟求值：左操作数非空时不对其求值。

**null 判等**：`==`/`!=` 的一侧为 `null` 字面量、另一侧类型为 `T0` 时合法，结果为 `bool`（`null` 定型为 `Nullable\<T0>`，按引用/值判等）。`T0` 非可空时（如 `i32 == null`）不报错不警告，运行期恒 `false`/`true`（与 §3.5「不做静态不可能性拒绝」口径一致）。`null == null`（两侧皆无锚定类型）是编译错误。null 判等是 smart cast 的收窄来源之一（§3.5）。

### 3.5 类型转换与类型检查

```rigi
// 类型检查
obj is String         // obj 是否为 String 或其子类
obj supers Animal     // obj 的类型是否为 Animal 的基类

// 强制转换（失败抛出 core.CastException）
obj as String

// 安全转换（失败返回 null）
obj as? String
```

`is` / `supers` / `with` 的右侧解析规则：

- 右侧可以是类型引用，也可以是 `Type\<T>` 值（动态类型测试）。名字先按类型引用解析；解析失败再按值绑定，值必须是 `Type\<T>` 类型，否则编译错误。值与类型同名时类型优先。
- `with` 右侧为类型引用时必须是 wrapper 类型，否则编译错误。
- `is` / `supers` 不做静态不可能性拒绝：静态可判定恒为 `false` 的写法不报错也不警告，结果在运行期得出（与 `as` / `as?` 的口径一致——可转性不做静态拒绝，失败抛出 `core.CastException`）。

**智能转换（smart cast）**：`is` 检查或 null 判等为真的控制流分支中，编译器自动将被检查值视为收窄后的类型，无需显式 `as`。

- **触发**：`x is T` 为真时 x 收窄为 T（含非空蕴含——`x: String?` 经 `x is String` 收窄为 `String`）；`x != null` 为真 / `x == null` 为假时 x 从 `T?` 收窄为 T。`and`/`or`/`not` 按短路语义组合（`and` 右侧以左真为上下文、`or` 右侧以左假为上下文——`(x is String) and (x.length > 0)` 中 `x.length` 在收窄后的 String 上解析）。条件为假且所在分支终止（return/throw 等）时，收窄对后续语句生效（guard 模式：`if (x == null) { return }` 之后 x 收窄为 T）。
- **目标**：局部变量与参数（`var` 重新赋值后收窄失效，含复合赋值）；`const` 字段（无自定义访问器、receiver 为 `this`/const 局部/参数/const 字段稳定链；构造方法 `init` 体内除外）。`var` 字段、带访问器的属性、任意方法调用结果均不可收窄。
- **不触发**：`supers`、`with`、动态目标 `x is t`（`Type\<T>` 值）、`is .Case`；`is` 检查为假的分支（类型系统无「非 T」差类型）。
- **失效**：`var` 局部/参数被重新赋值；字段稳定链上任一 `var` 环节被赋值。
- **switch**：`(_ is T)` 分支体内 `_` 收窄为 T（selector 本身是可收窄目标时同收窄）。
- **循环**：`while` 条件为真的收窄在循环体内有效（体内赋值照常失效）；循环结束后收窄不保留；`do-while` 体内不带条件收窄。
- **边界**：收窄不跨 `await`/`yield` 挂起点；**被 lambda 捕获的变量**（自声明处起）一律不再参与 smart cast（收窄失效，见 §5.2）。
- **`?.` 与 `if?` 不产生收窄**：`a?.b` 不使 a 变为非空（`a?.b != null` 亦不反推 a 非空）；`x if? y` 是值级回退表达式，无收窄区域。两者与 smart cast 正交互补。

> 注记：`is .Case`（enum case 判别，§12.3）编译为隐藏判别字段的整数比较（`RUNTIME.md` §16.3），不是类型检查；它不触发 smart cast——判别匹配不会改变值的静态类型（§12.3）。

转换优先级：源类型的 `castTo` → 目标类型的 `castFrom`（前者不存在或抛异常时才尝试后者）。

```rigi
// 自定义类型转换（定义在源类型上）
operator castTo\<TTarget>(): TTarget { ... }

// 自定义类型转换（定义在目标类型上）
class Celsius {
    operator castFrom\<TSource>(obj: TSource): Celsius { ... }
}
```

### 3.6 泛型

泛型参数使用 `T` 前缀 + 描述性名称的驼峰命名法（如 `TResult`、`TAnother`、`TElement`）。

**泛型列表语法：一律以 `\<` 开启、以 `>` 闭合。** 无论是泛型声明（类型参数列表）还是泛型使用（类型实参列表），都必须写作 `Name\<...>` 的形式：

```rigi
// 声明：类型、函数、wrapper 的类型参数列表
class Container\<TElement> { ... }
func transform\<TInput, TResult>(input: TInput): TResult { ... }

// 使用：类型引用、泛型调用
var list: List\<i32>
var map: Map\<String, i32>
var sorted = myList.sort\<i32>()
spanOf\<f32>(1000)
```

`\<` 是两个独立字符（反斜杠 + 小于号），不是一个新符号。这样设计的原因：

- `<` 在 Rigi 中**只**是比较运算符，与泛型列表不存在词法歧义（`a < b` 永远是比较，`a\<b>` 永远是泛型）；
- 解析器无需回溯或前瞻即可区分泛型与比较，也不依赖空格等脆弱约定；
- 与 Rigi"一切显式"的设计哲学一致：泛型边界显式标注，正如运算顺序必须显式加括号。

闭合符保持单个 `>`：`\<` 已无歧义地开启了泛型语境，其后的 `>` 只可能是闭合符。嵌套泛型的连续闭合写作 `>>`，如 `List\<Map\<String, i32>>`。

Rigi 的泛型在语义和运行时类型信息上都保持**具化（reified）**。实现采用单份共享 Native 代码体、隐式 typeid 传递与统一胖值槽，而不是为每组类型实参生成一份单态化机器码。共享代码体不等于类型擦除：实际泛型类型始终随 typeid 存在，可以直接用于 `TElement()`、`is`、`supers`、`with`、`typeOf` 与运行时构造（详见 RUNTIME.md §10）。ValueType 进入统一泛型/动态槽位时由系统特权 `Box` 表示按尺寸内联或间接保存。

**类级 typeid 只在实例上**：类型声明的类型参数在构造时写入实例隐藏字段。因此静态成员（方法/字段）不得使用所属类型上的类型参数（签名与体内都算）；经构造类型访问静态成员（`Box\<i32>.count()`）一律非法；裸名访问不碰 T 的静态成员（`Box.count()`）合法。需要按 T 构造值时用方法级泛型工厂（`BoxFactory.zeroOf\<T>(): Box\<T>`）。Rigi 无 `static class`：`singleton` 与仅含静态成员的类型都不得声明类型参数。详见 §9.2.3。

```rigi
// 类/struct 泛型
class Container\<TElement> { ... }

// 函数泛型
func transform\<TInput, TResult>(input: TInput): TResult { ... }

// 约束（extends 和 supers 位置可交换）
func process\<TItem extends Comparable, Serializable supers BaseType>(item: TItem): TItem { ... }

// with 约束：要求类型被指定 wrapper 修饰
func dump\<TItem with Serializable>(item: TItem) { ... }
```

**使用侧约束满足判定**：泛型实参（类型引用实例化、泛型调用显式实参、泛型 new 实参三处统一检查）必须满足对应泛型参数的约束，不满足为编译错误（诊断定位到实参）：

- `T extends B`：实参 `A` 满足 ⟺ `A` 可赋给 `B`（子类型/实现关系，`IsAssignable`）；`B` 是基本类型层级特权关系时同样适用（如 `Box\<i32>` 满足 `T extends ValueType`）。
- `T supers B`：实参 `A` 满足 ⟺ `B` 可赋给 `A`（反向）。
- `T with W`：实参 `A` 满足 ⟺ `W` 在 `A` 的 wrapper 应用集合中（编译期查类型的 `AppliedWrappers`，含 interface 传染结果；构造类型随定义传播）。`with` 约束在函数体内等价于一次 wrapper 应用：带 `with W` 约束的泛型参数 `param` 上写 `param:W` 是合法的 wrapper place（§14.5），只读禁令与应用语义同直接应用一致；wrapper 存储在实参宿主的隐藏存储中，编译器不为泛型参数合成任何存储。
- 约束边界不得引用同一声明泛型参数列表中的参数（含嵌套泛型实参位置）——`class C\<T1 extends T2, T2>` 是编译错误（声明侧专门诊断）。引用外层可见作用域的泛型参数（如泛型宿主类型的方法约束引用宿主的 `T`）不在此列：此时边界含未替换泛型参数，使用侧检查**跳过**（不做静态拒绝，由外层调用代入后自然满足）。实参为 `ErrorType` 时静默放行（毒化传播）。

**实例化点隐式限制**：除上述显式约束外，每次填入泛型实参还会对构造类型自身重跑与布局/共享安全相关的隐式限制（§3.1.1 字段闭包与静态字段闸门、§4.5 async 闸门 2/3——声明侧因泛型参数无法静态判定而跳过的部分），与显式约束同一检查通道、同一些填入点（声明侧类型标注、函数体内类型引用、泛型调用显式实参）。显式约束不满足即拒绝该类型引用；隐式限制违规落诊断后按可恢复模型继续编译。同一 （定义， 实参） 对在单次填入检查中只诊断一次。

**泛型参数的成员解析**：函数体内对类型为 `T` 的值做成员访问（方法、operator 名字调用、运算符位置、字段、索引）按**有效成员类型**进行，与 §13.3 交叉引用——

- `T extends B`：按 `B`（含 `B` 的基类链与接口闭包；构造界按已代入形态，界含外层宿主泛型参数时保留参数身份）。
- 无约束或只有 `supers` / `with`：按 `Any`（§3.8 承诺成员如 `toString` 可用）。`supers`/`with` 不提供普通成员保证。

结果类型按约束签名（宿主代入后）定型，不是 `T`：`T extends Addable` 时 `a + b` 的类型是 `Addable`，赋回 `: T` 报错。`T` 有 `extends B` 时，`T` 的值可赋给 `B`（及 `B` 的上界）；反向（`B` 赋给 `T`）一律不可。`T` 的值可赋给 `T?`（Nullable 装箱视图，§3.4——与具体类型 `i32 → i32?` 同口径）；`T extends B` 时也可赋给 `B?`（先走界代入再装箱，界为外层型参时保留其身份）；但无约束的 `T` 不能赋给另一型参的 `U?`。

```rigi
// 型变（同 Kotlin 的 in/out）
class Producer\<out TElement> { ... }
class Consumer\<in TElement> { ... }

// 具化泛型：可以直接当作类型使用
func create\<TResult>(): TResult {
    return TResult()
}
```

型变只适用于 class、interface、struct、enum struct 和 wrapper 的类型泛型参数；
函数、方法和 operator 的泛型参数必须使用默认 invariant。类型声明中的使用位置按
读写方向检查：

- `out T` 只能出现在结果位置、getter 返回值、`const` 字段以及 covariant 泛型
  实参中；方法参数、setter 参数、可变字段和 invariant 泛型实参中出现均为编译错误。
- `in T` 只能出现在方法参数、setter 参数以及 contravariant 泛型实参中；方法返回值、
  getter 返回值、字段和 invariant 泛型实参中出现均为编译错误。
- 同时存在 getter/setter 的字段是 invariant；普通 `var` 字段也是 invariant。
- 构造泛型类型之间的赋值遵循声明处方向：`Producer<Dog>` 可赋给
  `Producer<Animal>`，`Consumer<Animal>` 可赋给 `Consumer<Dog>`；未标注型变的
  泛型类型要求实参严格相同。型变递归穿透嵌套构造类型，但遇 invariant 参数即停止。

### 3.7 运行时类型与反射（`typeOf` / `Type\<T>` / `new` / `with`）

泛型机制在运行时始终携带 typeid（见 RUNTIME.md §10），因此以下反射能力对所有代码默认可用，无需特殊标注。类级类型参数的 typeid 挂在实例上，静态成员读不到——`T()` / `is T` / `typeOf(T)` 等在静态成员里使用所属类型的 `T` 与在签名里使用同样是编译错误（§9.2.3）；方法级 `func zeroOf\<T>()` 的 `T()` 由调用点传 typeid，合法。

**`Type\<T>`**：基本类型之一（`struct`），承载一个运行时类型（本质是对 typeid 的封装）。

**`typeOf`**：取得某个值或类型的运行时类型，返回 `Type\<T>`。操作数按两种形态解析：

- **值形态（常态）**：操作数先按值绑定，取值的运行时实际类型，返回 `Type\<T静态>`——`T` 是类型边界（`BIL_STANDARD.md` §6.3 `.typeid<TBound>` 语义），实际类型为其子类型亦属该边界。
- **类型形态**：操作数无法绑定为值、且可解析为类型引用时，返回该类型的 `Type\<T>`。

```rigi
var box = new Box(12, 12, 24)
var t = typeOf(box)              // t: Type\<Box>
```

**`new`**：显式发起一次普通类型构造。其目标可以是静态类型符号，也可以是一个 `Type\<T>` 值。

```rigi
const file = new File("./mydoc")

var t = typeOf(box)
var another = new t(12, 12, 24)  // 按 t 所指类型的 init 构造
```

- 普通类型的构造必须经 `new` 发起，`TypeName(...)` 不构成构造调用；对运行时 `Type\<T>` 值必须使用 `new value(...)`。
- 泛型参数仍可直接写作 `TResult()`，其底层与动态 `new` 使用同一套 typeid 构造机制。**零参形态 `T()` 在编译期按「T 符合约束的最大基类」判定**（extends 界；无约束或仅 `supers`/`with` 时最大基类是 `Any`）：
  - 最大基类是**内建标量**（整数/浮点/`bool`/`char`/`String`）时放行——运行期产该类型零值（`i32()` 得 0），编译器特殊处理；
  - 最大基类是**非 abstract 的 class** 且按使用点可见性存在可访问零参 `init` 时放行（init 继承原则：界未声明任何 init 时按其默认构造判定，默认构造须满足 §9.3 的字段定值赋值规则）；该 init 的每条初始化路径都已过 §9.3 DA 检查；
  - 其余一律编译错误「没有该方法」：`Any`/`Object`/接口/abstract 类没有可构造零参 init（**无约束 T 的 `T()` 因此一律编译错误**）；值类型界不放行（悲观假设——值类型的子类型布局不可静态穷举）；`enum struct` 恒不可构造（§12.2）。
  - 带实参形态 `T(args)` 不做静态判定，保持运行期解析。
- `enum struct` 是明确例外：无论 init 的可见性如何，都不能通过 `EnumType(...)`、`new EnumType(...)`、`new enumTypeValue(...)` 或泛型 `T()` 直接构造；只能使用其具名 case 入口（见 §12）。
- 动态 `new typeValue(...)`（对 `Type\<T>` 值构造）是唯一不受编译期界检查约束的形态，保持运行期解析（类比 wildcard proxy 的动态性）。当目标类型非静态具体时，init 的重载解析在运行期完成；若目标为抽象类型、enum struct 或找不到匹配的 init，抛出 `core.NoSuchMethodException`（编译期先按界检查，界没有可构造入口的在编译期直接报错，不会落到运行期）。
- **运行期路径不做 DA 哨兵**（前端静态检查原则 Q8）：动态 `new typeValue(...)` 与带实参 `T(args)` 在 VM 内**不**复检目标类型的字段定值义务（§9.3 DA 只覆盖静态 `new T(...)` 与零参 `T()` 的前端路径）。运行期只按 init 表匹配入口，匹配失败由 `NoSuchMethodException` 兜底；不插入「未定值非空字段」类哨兵。

**在 `is` / `supers` 中使用 `Type\<T>` 值**：`Type\<T>` 的值可当作类型出现在 `is` / `supers` 右侧。

```rigi
var t = typeOf(box)
if (obj is t) { ... }
if (obj supers t) { ... }
```

**`with`**：判断某类型是否被指定 wrapper 修饰，可用作运算符或泛型约束（见 §3.6）。

```rigi
if (obj with Serializable) { ... }
```

---

### 3.8 字符串转换（`toString`）与字符串插值

每个类型都拥有 `toString(): String`（承诺挂在类型层级根 `Any` 上——`Any.toString` 是 open、可被 override、自带实现的普通方法），可直接调用，也可经 `override` 覆写以定制文本表示。**泛型参数可经 Any 承诺访问 `toString`**：无约束或仅 `supers`/`with` 时有效成员类型就是 `Any`；字符串插值对非 `String` 段（含泛型参数）一律走该承诺（见 §3.6 成员解析与 §13.3）：

- **内建基本类型**（数值 / `bool` / `char` / `String`）的 `toString` 由内建实现提供：`String` 即自身；数值为标准十进制文本；`bool` 为 `"true"` / `"false"`；`char` 为单字符字符串。
- **未覆写的类型**由默认实现提供（`Object` 上的 open `override` 方法——它 override `Any.toString`，内建提供），返回该类型的 canonical 名（如 `"com.example::User"`）。
- `Any`/`Object` 的默认实现体是编译器合成的小函数：把接收者装箱为 `Any` 后调用 `any_to_string`。`any_to_string` 是标准库 `.bootstrap.rg` 里的文件级私有（`priv`）全局 `native` 函数（`@NativeLibrary("rigi_rt")` / `@NativeSymbol("any_to_string")`），是 toString 机制唯一的 native 触达点——用户代码不可直接调用它。
- 值类型调用 `toString` 时按 `RUNTIME.md` §4 装箱后进行虚派发；装箱与派发是 `BIL_STANDARD.md` §22 划给 VM/Middleware 的实现细节，源码层只需知道调用承诺成立。

字符串插值（§3.3）以 `toString` 定义：`${}` 内表达式的静态类型不是 `String` 时，先调用其 `toString()` 再参与拼接；拼接即 `String` 的内建 `+` 运算，按源码顺序从左到右结合。每个插值段只求值一次。

```rigi
var count = 3
var text = "count: ${count}, ok: ${(count > 0)}"   // "count: 3, ok: true"
```

插值表达式的词法规则：`${` 后表达式按普通 Rigi 词法解析，可以包含任意字面量（字符串/字符）、嵌套 `{}`（lambda 体、seq 块）与注释，括号配平由词法层完成；表达式跨行遵循与源文件一致的续行规则（括号未闭合时换行透明，§1.1）。嵌套字符串字面量在两态宿主中均可直接使用（`"a${"b"}c"` 合法）；未闭合的嵌套字面量按词法错误就近报告。

基元与默认实现均为内建行为：BIL VM 经 `BIL_STANDARD.md` §22.5 内建 hook（`rigi_rt` / `any_to_string`）执行，原生环境经 `RUNTIME.md` §26 的 `rigi_rt.any_to_string` 路由。

### 3.8.1 哈希承诺（`hash`）

每个类型都拥有 `hash(): i64`（承诺挂在类型层级根 `Any` 上——`Any.hash` 是 open、可被 override、自带实现的普通方法，`Object` 提供 open `override` 默认实现），可直接调用，也可经 `override` 覆写以定制哈希。与 `toString` 同构：泛型参数（无约束或仅 `supers`/`with`）可经 Any 承诺访问 `hash`。

默认实现语义（`Object` 版，经 `any_hash` 内建提供）：

- **`String`** 按内容哈希——内容相同的两个 `String` 哈希相等；
- **标量**（数值 / `bool` / `char`）按值哈希；
- **对象**（引用类型默认）按实例身份哈希——同一实例两次调用相等，不同实例（即使 `toString` 相同）哈希不同；
- **`null`** 固定为 `0`。

只承诺**同一宿主内**同值必同哈希；VM 宿主与原生宿主的哈希数值不要求一致（跨进程、跨宿主都不可持久化或比较哈希数值）。哈希不保证分布均匀，允许碰撞。典型用途是关联数组键判等：`core.collections.Map` 的键相等 = `==`（**equals-or-hash 判等链**，用户裁定）——键类型声明了 `operator equals` 走它（运行期最派生），未声明的类型走 `Any` 承诺的默认 `equals`（双虚调 `hash` 比较），hash 碰撞即判等，**绝不走 `toString`**。默认 `hash` 对对象是身份哈希，不同身份的对象键互不覆盖；值语义 `struct`/需要按字段判等的 `class` 键请 `override hash` 或实现 `operator equals`。

与 `toString` 机制同构：`Any`/`Object` 的默认实现体是编译器合成的小函数，装箱接收者后调用 `.bootstrap.rg` 的文件级私有全局 `native` 函数 `any_hash`（`@NativeLibrary("rigi_rt")` / `@NativeSymbol("any_hash")`）——用户代码不可直接调用它；用户类型 `override hash` 后经普通虚派发执行自身实现。

---
