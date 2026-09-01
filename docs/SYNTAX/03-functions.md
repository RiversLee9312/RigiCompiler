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
2. **类型适用性**：每个实参的类型可赋给对应形参类型（含 `T → Nullable\<T>` 装箱视图；`null` 字面量实参要求对应形参为 `Nullable\<T>`）。
3. **最具体胜出**：候选 A 优于候选 B，当且仅当对每个实参，A 对应形参的类型都可赋给 B 对应形参的类型，且至少在一个实参上严格成立（反向不可赋值）。若存在唯一不劣于其他所有候选的候选，解析命中；候选间互不占优时为二义性编译错误；过滤后无适用候选同样为编译错误。若最具体比较后仍有多个候选互不严格占优，本次调用**填充默认值形参个数更少者**优先；仍相同为二义。

实例方法与 `ext` 扩展方法同池参与解析（继承链上的同名方法同池；可见性检查在 §16 访问控制独立进行）。构造函数（init）的重载解析与函数调用同一规则。

**泛型方法**：泛型方法（带泛型参数列表的 func/operator）的固定泛型实参**可显式书写，也可由值实参推断**。显式优先：写了 `\<...>` 则只按显式实参匹配，不与推断混用。

```rigi
func transform\<TInput, TResult>(input: TInput): TResult { ... }
var r = transform\<i32, String>(42)     // ✅ 显式实参
var id = identity(42)                   // ✅ 从值实参推断 T = i32
func make\<T>(): T { ... }
var x = make()                          // ❌ T 只出现在返回类型，无法推断
```

- 泛型实参按类型引用解析（§3.6 使用侧约束检查同步进行）；**显式**实参个数必须与固定泛型参数列表一致（泛型可变参数除外，见 §4.3），不一致为编译错误。
- **候选池规则**：
  - 调用**带**显式泛型实参时，候选池仅保留固定泛型参数个数与实参个数匹配的泛型方法（普通方法不参与）；元数不符仍报「expects N type argument(s), got M」。
  - 调用**不带**显式泛型实参时，非泛型方法、全可变泛型包方法，以及带固定泛型参数且**推断成功**的方法同台参与；推断失败的固定泛型候选被排除。过滤后候选池为空且存在泛型同名方法时，报「无法从实参推断泛型实参」诊断（不再要求「必须显式给出泛型实参」）。
- **推断规则**（仅方法级固定泛型参数；类型级构造器如 `new List()` 省略实参不做推断）：
  - 以值实参的静态类型对形参类型模式逐位匹配：裸 `T` 绑定该实参类型；构造模式（`Array\<T>`、`Pair\<K,V>`、`T?`、`Func\<...>`、用户泛型类型等）递归下钻（含沿基类链命中同定义构造，以便预绑后的 lambda 隐藏类对上 `Func\<...>`）。
  - 同一固定泛型参数在多处绑定必须一致（引用相等），冲突即该候选推断失败。
  - 推断失败：某固定参数未在任何值形参中出现（只出现在返回类型）；绑定冲突；实参是 null 字面量且该参数无其他绑定来源；形参模式与实参类型结构不匹配。null 字面量不参与绑定，但另一处成功绑定则通过。
  - lambda 实参：若预绑已给出静态类型（隐藏类或其 `Func\<...>` 基类）则按上款匹配；否则该候选推断失败（限制，不做额外 Hack）。
  - 推断成功后必须过约束检查（extends/supers/with，§3.6）；违反则排除该候选，池空时回放约束诊断。
- 泛型方法按代入后的签名（形参与返回类型以显式或推断实参替换泛型参数）参与三步 ranking，与非泛型候选同台；最具体胜出语义不变。代入后两候选形参类型同等具体时，走既有平局打破（本次调用填充默认值更少者优先）；仍相同为二义——**非泛型并不自动压过推断泛型**。代入后边界残留未替换泛型参数时，按「约束边界含未替换泛型参数跳过使用侧检查」处理（§3.6）。
- 推断出的固定实参与显式实参同槽（调用点物化 typeid，BIL §7 无单态化）。
- 泛型 `operator` 的**运算符位置**（`a + b`、`v += w`、一元、比较等）无 `\<T>` 书写位，固定泛型参数由操作数静态类型按上款推断；推断成功则与非泛型 operator 同台 ranking。名字形式（`a.plus\<TAnother>(b)` / `a.plus(b)`）与普通方法同规则——接收者是泛型参数时同样走 §3.6 / §13.3 的有效成员类型（`T extends B` 按 `B` 查找，无约束按 `Any`），不是「`T` 无成员表」的特例。泛型 init 不存在（init 不得声明泛型参数列表）。
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

**泛型可变参数调用规则**：泛型可变参数包（`TArgs...` / `named TValues...`）的类型实参**不显式书写**，由调用点对应值实参的静态类型**推导**——包内实参个数与类型均来自调用点，这是可变参数包的固有形态。固定泛型参数按 §4.2 可显式或由非包值实参推断；「固定+可变」混合形态下固定部分允许推断、包照旧推导：

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

`async` 是函数派发契约的一部分：override 签名匹配要求 `async` 修饰符一致（§9.2.1），sync 成员与 async 成员互不覆写、也不允许互相静默隐藏。

```rigi
const userTask: core.coroutine.Task\<SharedUser> = loadUser(42)
const flushTask: core.coroutine.Task = flushLogs()
```

调用是 eager 的：协程在调用时启动，不会等到第一次 `await` 才启动。直接丢弃返回的 Task 即表示启动任务后不与其同步，可用于 fork/fire-and-forget：

```rigi
flushLogs()              // 启动后继续执行
const user = await loadUser(42)
```

每个新协程在创建时绑定一个 `core.coroutine.Executor`（默认终身不变，唯一例外是 Task.executor 显式换绑，`RUNTIME.md` §17.1/§18.4）。未显式指定时继承当前协程的 Executor；程序只能选择 Executor，不能选择其中的 Worker。内置 Executor（`MainExecutor` / `ComputeExecutor` / `IOExecutor`）是 `pub shared singleton class`（基类 `Executor` 保持 abstract），经 `new ComputeExecutor()` 等 singleton 构造表达式取得进程内唯一实例，Worker 懒建（`RUNTIME.md` §20.1）。

#### `core.coroutine.Task` API

`Task` / `Task\<TReturn\>` 是具体 shared class（非 abstract），支持显式构造：

```rigi
pub shared class Task {
    pub init(body: core.AsyncAction)
    pub func run()
    pub func run(executor: Executor)
    // state: TaskState（pub get；写通道受限，语义见下）
    // executor: Executor?（读写语义见下）
}

pub shared class Task\<TReturn\> {
    pub init(body: core.AsyncFunc\<TReturn\>)
    // run / state / executor 同上
}
```

- **构造只存 body 不执行**（冷 Task）；`run()` 以「executor 预设值 ?? 当前 Executor」启动，`run(executor)` 先设预设再启动。Task 只允许启动一次：对已启动 Task（含直接调用 async 函数/lambda 返回的热 Task）调用 `run` 抛 `core.IllegalStateException`（§8.1）。
- **首次启动是 spawn-into**：复用该 Task 对象建立协程，Task 与协程保持 1:1（`RUNTIME.md` §18.4）。多个协程并发 await/start 同一冷 Task 时，竞争输家不抛异常，按普通 waiter 等待终态。
- **`state`** 返回 `core.coroutine.TaskState`：`Created` / `Runnable` / `Suspended` / `Completed` / `Failed` / `Cancelled` 六 case（Running 与 Runnable 对用户不可区分，合并为 Runnable）；`TaskState` 的只读成员 `isRunning` 仅当状态为 Runnable/Suspended（即已启动未终止）时为 `true`。
- **`executor: Executor?`**：未启动时读取 = 预设或 `null`、写入 = 设预设；已启动后写入 = **换绑协程 Executor**——下一个恢复点生效，执行段内永不迁移（`RUNTIME.md` §17.1/§18.4）。
- **`await` 未启动的冷 Task** 等价于在当前 Executor 启动并等待（§7.5）；Task 终态语义不变。

`state` / `executor` 按 Rigi 访问器语法（§9.4）落地；本条只规定读写语义，不规定访问器拼写细节。

推荐两范式——需要指定 Executor 或推迟启动时用冷 Task 显式启动，其余直接 `await` async 调用：

```rigi
// 范式一：冷 Task 显式启动到指定 Executor
const task = new Task(func{async () -> computeHeavy()})
task.run(new ComputeExecutor())
await task

// 范式二：eager spawn，直接 await
const user = await loadUser(42)
```

带结果的冷 Task 同理：`new Task\<i32\>(func{async (x: i32): i32 -> compute(x)})` 经 `run` 启动后由 `await` 取得 `i32` 结果。

async 调用会把一批值从当前协程送进新协程，因此以下**五处**的类型都必须是 §3.1.1 定义的共享安全类型（shared class、shared interface、shared rich struct/wrapper、非 rich ValueType，以及 `T` 共享安全的 `Nullable\<T>`）：

1. **receiver**：实例方法的 `this`，扩展方法的 `.this`；
2. **参数**：全部形参，含默认参数、具名参数与可变参数展开后的每一个实参类型；
3. **返回值**：即 Task 的结果类型 `TResult`；
4. **捕获变量**：async lambda 从外层作用域捕获的每一个变量；
5. **泛型实参**：async 函数/lambda 的每一个泛型实参——具化泛型下 typeid 与实际值一同跨越边界，因此同样受闸门约束。

编译器在 async 声明处检查 2、3、5 的声明类型，在 async 调用点检查 1、2、5 的实际类型，在 async lambda 处检查 4。违反者为编译错误，不存在运行时补救。

接口可以声明 `async` 成员；此时接口本身必须标记 `shared`（§3.1.1），否则经接口类型调用时 receiver 的静态类型无法通过闸门 1。

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
- 参数类型仅限 §3.2 基本类型中的整数、浮点、`bool`、`char` 与 `String`，另放行 `Any`（统一胖值槽，VM 直传任意值——首例 `.bootstrap.rg` 的 `any_to_string(value: Any)`，§3.8）；不允许 Object、泛型参数、用户声明类型，也不允许可变参数；
- **返回类型**：允许 §3.2 基本类型中的整数、浮点、`bool`、`char` 与 `String`，也允许用户声明的**引用类型**（class/interface，如 `core.coroutine.sleep(...): EventAlarm`）；不允许值类型、泛型参数与可变参数。native 只负责声明运行时原生方法面的形状，FFI 参数/返回值 ABI 与 `rigi_rt` 的转换细节在 Middleware 阶段定稿（`RUNTIME.md` §26），编译器不做形状之外的检查；
- `@NativeLibrary("...")` 必填，给出原生库标识；`@NativeSymbol("...")` 可省，缺省时取函数名；两个注解的实参必须各为一个字符串字面量；
- `@NativeLibrary` / `@NativeSymbol` 是编译器内建注解，只允许出现在 native 函数声明上；它们不属于 wrapper 体系（§14），不产生 wrapper 组合链。

---
