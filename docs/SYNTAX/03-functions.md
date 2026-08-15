# 函数（§4）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 4. 函数

### 4.1 函数声明

```rigi
pub func add(a: i32, b: i32): i32 {
    return a + b
}

// 默认参数
pub func greet(name: String = "World"): String {
    return "Hello ${name}"
}
```

必须使用显式 `return`，不支持隐式返回。

### 4.2 函数调用、具名参数与重载解析

```rigi
greet()
greet(name="Rigi")    // 具名参数用 = 而非 :
```

**实参映射**：位置实参按源码顺序依次填充形参列表中第 1、2、… 个位置（与具名实参的穿插位置无关）；具名实参按形参名归位，`name` 必须存在于形参表；同一形参被填充多次（位置与具名冲突、或两个同名实参）是编译错误。实参表达式的求值序为**规范参数序**（形参声明序）而非源码书写序——绑定产物即按形参序排列，后端按序求值。

**默认参数**：形参可携带默认值 `name: Type = expr`。一旦某个形参声明了默认值，其后的所有形参也必须携带默认值（声明侧规则）。默认值表达式在**声明点作用域**绑定：看不到函数的形参与局部、无 `this`，可引用全局可见符号；其类型必须可赋给形参类型。调用缺省时该实参以默认值填充——语义上每次调用都重新求值默认值表达式。默认值表达式不允许包含局部声明（值块/lambda 内 `var` 等形态为编译错误）。

**重载解析**：允许同名方法/构造函数（init）共存，调用点在语义期完成 source-level ranking——这是全部候选排序的唯一落点，之后各层（BIL/Middleware/VM）不再执行 ranking（`BIL_STANDARD.md` §3.3）。解析按三步进行，前两步静默过滤、不产生诊断：

1. **结构过滤**：实参个数 ∈ [无默认值形参的个数, 形参总数]；具名实参名存在于形参表；同一形参不被重复填充。
2. **类型适用性**：每个实参的类型可赋给对应形参类型（含 `T → Nullable\<T\>` 装箱视图；`null` 字面量实参要求对应形参为 `Nullable\<T\>`）。
3. **最具体胜出**：候选 A 优于候选 B，当且仅当对每个实参，A 对应形参的类型都可赋给 B 对应形参的类型，且至少在一个实参上严格成立（反向不可赋值）。若存在唯一不劣于其他所有候选的候选，解析命中；候选间互不占优时为二义性编译错误；过滤后无适用候选同样为编译错误。若最具体比较后仍有多个候选互不严格占优，本次调用**填充默认值形参个数更少者**优先；仍相同为二义。

实例方法与 `ext` 扩展方法同池参与解析（继承链上的同名方法同池；可见性检查在 §16 访问控制独立进行）。构造函数（init）的重载解析与函数调用同一规则。

**泛型方法**：泛型方法（带泛型参数列表的 func/operator，按名字调用时）**必须显式给出全部泛型实参，不做从实参推导**——与 Rigi「一切显式」哲学一致：

```rigi
func transform\<TInput, TResult>(input: TInput): TResult { ... }
var r = transform\<i32, String>(42)     // ✅ 显式实参
var r = transform(42)                   // ❌ 编译错误：泛型方法需要显式泛型实参
```

- 泛型实参按类型引用解析（§3.6 使用侧约束检查同步进行）；实参个数必须与泛型参数列表一致（泛型可变参数除外，见 §4.3），不一致为编译错误。
- **候选池规则**：调用带显式泛型实参时，候选池仅保留泛型参数个数与实参个数匹配的泛型方法（普通方法不参与）；调用不带显式泛型实参时，泛型方法不参与候选——若过滤后无任何候选且存在泛型同名方法，报「泛型方法需要显式泛型实参」诊断。
- 泛型方法按显式实参代入后的签名（形参与返回类型以实参替换泛型参数）参与三步 ranking；代入后边界残留未替换泛型参数时，按「约束边界含未替换泛型参数跳过使用侧检查」处理（§3.6）。
- 泛型 `operator` 的**运算符位置**（`a + b`、索引等）不参与解析（运算符调用无泛型实参书写位置），是编译错误；其名字形式（`a.plus\<TAnother>(b)`）与普通方法同规则。泛型 init 不存在（init 不得声明泛型参数列表）。
- 带可变参数（§4.3）形参的方法不参与调用绑定，是编译错误（泛型可变参数见 §4.3）。

```rigi
func show(x: Any) { ... }
func show(x: String) { ... }
show("hi")              // String 更具体 → show(String)

func combine(a: String, b: Any) { ... }
func combine(a: Any, b: String) { ... }
combine("x", "y")       // 编译错误：二义，两个候选互不占优

func greet(name: String = "World", punct: String = "!"): String { ... }
greet()                 // = greet("World", "!")
greet("Rigi")          // = greet("Rigi", "!")
greet(punct="?")        // = greet("World", "?")
```

### 4.3 可变参数

```rigi
// 位置可变参数
pub func sum(numbers: i32...): i32 { ... }

// 关键字可变参数（类似 Python 的 **kwargs）
pub func config(options: named String...): Config { ... }

// 关键字可变参数 + with 约束（运行时以 Map\<String, typeid> 描述，见 RUNTIME.md）
pub func update\<named TValues... with Serializable>(configs: named TValues...): bool { ... }

// 调用时自动完成类型填充
update(isDarkMode = true, userName = "Andy")
```

**泛型可变参数调用规则**：泛型可变参数包（`TArgs...` / `named TValues...`）的类型实参**不显式书写**，由调用点对应值实参的静态类型**推导**——包内实参个数与类型均来自调用点，这是可变参数包的固有形态，与 §4.2「固定泛型参数必须显式实参」不冲突（① 仅限固定泛型参数）：

- 位置泛型可变参数 `TArgs...`：类型实参 = 位置值实参的静态类型序列（BIL 以 `.generic.TArgs: Array\<typeid>` 承载）。
- 具名泛型可变参数 `named TValues...`：类型实参 = 具名值实参的「名称 → 静态类型」映射（BIL 以 `.generic.TValues: Map\<String, typeid>` 承载）。
- 推导出的每个类型实参必须满足对应泛型参数的约束（§3.6 使用侧检查逐实参进行，如 `with Serializable`）。
- 与值可变参数（`.vargs.args` / `.kwargs.args`）成对出现的声明，两者都出现在规范签名中（BIL §7.2 参数序：固定泛型 → 泛型可变包 → 普通参数 → 值可变包）。
- 值实参的类型推导发生在调用绑定期；值实参自身按值可变参数规则归位（位置包按序、具名包按名）。



### 4.4 扩展函数与扩展字段

使用 `ext` 修饰符：

```rigi
pub ext func String.reversed(): String { ... }
pub ext var String.isEmpty: bool { get(_: _) { ... } }
pub ext static func Config.makeDefault(): Config { ... }
```

ext 成员在语义期注册到目标类型，注册后与声明在目标类型体内的成员同规则：访问级别默认 private（供声明文件外使用须显式 `pub` 等访问修饰符，§16.1）；ext 实例字段受 rich/shared 闭包表约束（§3.1.1），ext 静态成员（static 字段/常量/方法均合法）受共享安全闸门约束；interface 不能持有字段，ext 字段注入 interface 同样是编译错误。

**ext 成员的可见性按声明位置判定而非目标类型**（§16.1）：顶层 ext 声明适用顶层规则（private = 仅声明文件可见，internal = 编译单元内可见）；ext 方法/访问器体不获得目标类型私有成员的访问特权（封装不因扩展而开口）。

ext 目标不得为泛型定义：裸名命中泛型定义是编译错误（缺少类型实参），同名不同元数多命中是歧义编译错误。「扩展隐式获得目标泛型参数」的形态不支持。

### 4.5 `async` 函数与 Task

`async` 是函数修饰符，表示**每次调用该函数时都会立即创建并发布一个新的协程**。`async` 不表示“函数体才可以挂起”：普通函数也运行在当前协程中，因此同样可以执行 `await` 和 `yield`；区别仅在于普通函数调用继续使用当前协程，而 `async` 函数调用创建另一个协程。

`init` 不能声明为 `async`（任何 init 都不允许是 async 的）。

```rigi
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

```rigi
const userTask: core.coroutine.Task\<SharedUser> = loadUser(42)
const flushTask: core.coroutine.Task = flushLogs()
```

调用是 eager 的：协程在调用时启动，不会等到第一次 `await` 才启动。直接丢弃返回的 Task 即表示启动任务后不与其同步，可用于 fork/fire-and-forget：

```rigi
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

```rigi
pub shared class SharedUser { pub const id: i64 }
pub class LocalUser { pub var name: String }

pub async func ok(id: i32, name: String): SharedUser { ... }     // ✅ 全部共享安全
pub async func bad(user: LocalUser) { ... }                       // ❌ 参数是 local object
pub async func alsoBad(): LocalUser { ... }                       // ❌ Task 结果是 local object
```

### 4.6 `native` 函数

`native` 函数声明一个由运行时原生方法面提供的函数：它没有 Rigi 函数体，调用经 BIL 中的 `native` 方法声明路由到原生实现（见 `BIL_STANDARD.md` §8.4 与 `RUNTIME.md` §26）。标准库用它封装 libc 风格的原生能力（如控制台输出），普通 Rigi 代码调用 native 函数与调用普通函数语法完全相同。

```rigi
namespace core.io

pub class Console {
    @NativeLibrary("rigi_rt")
    @NativeSymbol("print")
    priv static native func print(text: String)

    @NativeLibrary("rigi_rt")        // @NativeSymbol 缺省时取函数名
    priv static native func printErr(text: String)
}
```

规则：

- `native` 仅适用于函数；native 函数**不得**书写函数体；
- 作为类型成员声明时必须同时是 `static`；不得用于 `init`、`operator`、getter/setter；
- 不得与 `async` 组合；同一容器内不得与同名函数构成重载；允许声明泛型参数列表（generic native：hidden typeid 按 `RUNTIME.md` §10 传参形态物化，首例 `alloc_array`，`BIL_STANDARD.md` §22.5）；
- 参数类型仅限 §3.2 基本类型中的整数、浮点、`bool`、`char` 与 `String`；不允许 Object、泛型参数、用户声明类型，也不允许可变参数；
- **返回类型**：允许 §3.2 基本类型中的整数、浮点、`bool`、`char` 与 `String`，也允许用户声明的**引用类型**（class/interface，如 `core.coroutine.make_sleep_alarm(...): EventAlarm`）；不允许值类型、泛型参数与可变参数。native 只负责声明运行时原生方法面的形状，FFI 参数/返回值 ABI 与 `rigi_rt` 的转换细节在 Middleware 阶段定稿（`RUNTIME.md` §26），编译器不做形状之外的检查；
- `@NativeLibrary("...")` 必填，给出原生库标识；`@NativeSymbol("...")` 可省，缺省时取函数名；两个注解的实参必须各为一个字符串字面量；
- `@NativeLibrary` / `@NativeSymbol` 是编译器内建注解，只允许出现在 native 函数声明上；它们不属于 wrapper 体系（§14），不产生 wrapper 组合链。

---
