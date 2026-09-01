# Lambda 表达式（§5）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 5. Lambda 表达式

### 5.1 Lambda 语法

```rigi
// 完整形式
func{(x: i32, y: i32): i32 -> (x + y)}

// void 形态：省略返回类型 = 无返回值 lambda（基类为 core.Action 族，见 §5.2）
func{(x: i32) -> ... }

// async：标记写在 `{` 之后、参数列表之前（不是 `async func{...}`）
func{async (id: i32): SharedUser -> loadUserNow(id)}

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
- **lambda 不接受任何修饰符**（含 `pub`/`priv`/`static` 等）：修饰符对 lambda 无意义，书写即为编译错误
- **lambda 不支持泛型形参**（泛型 callable 请显式声明类型）；`func{(x: T)\<T>: T -> ...}` 形态为编译错误
- **省略返回类型 = 无返回值（void）lambda**，与普通函数省略返回类型即 void 对齐；基类为 `core.Action` / `core.AsyncAction` 族（见 §5.2）
- **参数个数最多 32 个**（硬性上限，与标准库 `Func`/`Action` 家族预生成的元数变种一致，见 §5.2）
- 体为单表达式时：有返回值则该表达式即返回值（隐式取值，无需 `return@`）；无返回值则为表达式语句语义
- 体为多语句代码块时：有返回值则所有执行路径都必须显式 `return@_` 或 `return@标签` 产出值——规则同 §6.1；落到块尾而没有 `return@` 是编译错误；无返回值时块尾自然结束即可
- lambda 体内不允许裸 `return`：lambda 不是外层函数的值块，裸 `return` 的指向会含糊（返回 lambda 自身还是穿透外层函数），一律显式写 `return@`

#### lambda 头内部 annotation（Method wrapper）

annotation 写在 **lambda 头内部**：`func{` 之后、`async`/形参列表之前。

```rigi
func{ @Timed /*以及其它 wrappers*/ async (x: i32): i32 -> {
    doSomething()
    const doubled = (x * 2)
    return@_ doubled
}}
```

规则：
- 可写多个 `@Name` / `@Name(args)`，声明序即 wrapper 嵌套序（outer → inner，§14.4）
- 语义 = **Method wrapper** 修饰该 lambda（等价于把同一组 annotation 写在 lambda 隐藏类的 `operator call` 上）；Value/Entity 目标 wrapper 在此处是编译错误
- annotation 写在 `var` 声明上不是 lambda wrapper：那会被认为修饰变量本身（Value wrapper 目标），目标类别不符时按 §14.9 报错
- **注解实参列表的紧贴书写**：`@Name(args)` 的 `(` 必须与注解名**紧邻**（无空白/注释）才解析为注解实参列表；`@Name (x: i32)`（中间有空白/注释）中的 `(` 按 lambda 形参列表解析（`@Name` 视为无实参注解）

```rigi
// 正例：( 紧邻 Timed —— @Timed("tag") 是带实参的 Method wrapper
func{ @Timed("tag") (x: i32): i32 -> x }

// 反例：( 与 Timed 之间有空白 —— (x: i32) 是 lambda 形参列表，@Timed 无实参
func{ @Timed (x: i32): i32 -> x }
```

### 5.2 对象模型

每个 lambda 在编译期生成一个**隐藏类**：

- **命名空间**与声明该 lambda 的位置相同；
- **类名**形如 `..lambda..UUID`（`UUID` 由编译器分配）——这是编译器生成的隐藏类型，用户源码永远写不出这个名字；
- **基类**按 lambda 形态四选一（均声明在标准库 `core` 中，为 `abstract class`，各含一个 abstract `operator call`；泛型参数 `TRet` 在最前）：

| 形态 | 基类 | shared |
|------|------|--------|
| 有返回值 | `core.Func\<TRet, T0, …>` | 否 |
| 无返回值 | `core.Action\<T0, …>` | 否 |
| 有返回值且 async | `core.AsyncFunc\<TRet, T0, …>` | **是**（`shared class`） |
| 无返回值且 async | `core.AsyncAction\<T0, …>` | **是**（`shared class`） |

标准库为每个家族预生成 **0 到 32 个参数**的元数变种，因此 lambda **最多 32 个参数**。

**静态类型与转换**：

- lambda 表达式的静态类型就是该隐藏类本身；
- 因名字不可书写，显式标注位置永远写基类（如 `Func\<i32, i32>` / `core.Func\<i32, i32>`）；
- 隐藏类 → 基类按普通隐式向上转换处理，无特例。

**值语义**：lambda 值是普通对象——可以存字段、作为返回值、随意传递，与其他对象值相同。

**调用约定（callable 协议）**：任何声明了 `operator call` 的类型的值都可以像函数一样被调用（`expr(args)`）。这是通用的 callable 协议，不是 lambda 特例；lambda 隐藏类通过覆写基类的 abstract `operator call` 接入该协议。

**捕获**（与 §14.3 wrapper 值存储共用**统一 cell 存储**机制）：

- lambda 在被求值时创建隐藏类对象；被捕获的外层变量通过构造函数以 cell 对象传入；
- `core.Cell\<T>` / `core.ReadonlyCell\<T>` 是**抽象基类**（仅抽象 `getValue`/`setValue`——ReadonlyCell 无 `setValue`；无 `value` 字段、无显式 init）；实际 cell 对象恒为编译器（P3）逐变量合成的隐藏子类 `..cell..UUID`（`..` 前缀用户不可名；与声明位置同命名空间；自持 `pub var value: T` 字段——const/ReadonlyCell 风味为 `pub const`；override `getValue`/`setValue` + `init()`/`init(value)`）；
- 被捕获的变量（除 `this` 与 lambda 自身参数外）一律 Cell 化：
  - 可变（`var`）捕获 → 继承 `core.Cell\<T>` 的隐藏子类；
  - 不可变（`const`）捕获 → 继承 `core.ReadonlyCell\<T>` 的隐藏子类；
  - **`this` 捕获不套 Cell**，直接作为普通字段；
- 隐藏类的 `.capture.*` 字段类型 = 该变量的 cell 隐藏子类（非抽象基类）；已被 wrapper 值 cell 化的变量按引用直接捕获，不套第二层 cell；
- 被捕获变量从**声明处起**整个生命周期的读写都经过 cell 的 `getValue`/`setValue`（定义级成员引用虚派发）；
- 之所以 `const` 也要 Cell 化：值 wrapper 对 get 的代理行为意味着按值拷贝会冻结 proxy 结果、脱钩 wrapper 状态，一律走 Cell 才能保持代理语义；
- lambda 内对 `const` 捕获的写入仍是编译错误（符号层检查）；
- **被 lambda 捕获的变量不再参与 smart cast**（收窄失效，见 §3.5）；
- lambda 内的赋值**不影响**外层 definite assignment（保守）；
- **for 循环变量**被捕获时按**每迭代新 cell**处理（各 lambda 见当迭代的值，见 §7.3）；**catch / finally(e) / using** 变量被捕获时在进入对应块时构造一个 cell（见 §8 / §6.2）。

**泛型上下文中的 lambda**（如 `func{(x: T) -> ...}` 捕获外层泛型 `T`）：lambda 自身不声明泛型形参（见 §5.1），隐藏类共享外层函数/声明类型链的泛型参数符号（同一符号对象挂进隐藏类 `GenericParameters`）；外层泛型实参的 typeid 在构造点经构造函数转发传入。

### 5.3 Trailing Lambda

```rigi
list.map{(item: String): i32 -> item.length}
```

### 5.4 `async` Lambda

async 标记写在 `{` 之后、参数列表之前：`func{async (...)...}`（**不是** `async func{...}`）。

```rigi
const loader = func{async (id: i32): SharedUser -> loadUserNow(id)}
const task: core.coroutine.Task\<SharedUser> = loader(42)
const user = await task
```

- async lambda 的静态类型是 `core.AsyncFunc\<…>` / `core.AsyncAction\<…>`（均为 **shared class**）的隐藏子类对象；
- 调用约定与 async 函数相同（§4.5）：立即创建新协程，调用表达式类型为 `core.coroutine.Task\<TResult>` / `core.coroutine.Task`；
- 普通 lambda 在当前协程中执行；async lambda 在新协程中执行；
- **shared 拦截**：因 Async 基类是 shared，非共享安全的捕获 / 参数 / 返回值在 async lambda 上是编译错误——与 §4.5 五项闸门一致（闸门 4 专查捕获；参数与返回值同闸门 2、3）。类型系统亦因 shared 基类天然拦截「非 shared-safe 的值无法被 async lambda 捕获」。
- **与 Task 构造衔接**：`core.AsyncAction` / `core.AsyncFunc\<TReturn\>` 正是 `core.coroutine.Task` / `Task\<TReturn\>` 的 `init` 形参类型（§4.5）——async lambda 隐藏类经普通向上转换即可作为实参：`new Task(func{async () -> ...})` 构造冷 Task、`new Task\<i32\>(func{async (x: i32): i32 -> ...})` 构造带结果的冷 Task；构造不执行 body，启动时机与目标 Executor 由 `run` / `executor` 控制。

---
