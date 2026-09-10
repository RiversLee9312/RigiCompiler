# Wrapper（修饰器）（§14）

> 本文件是 [SYNTAX.md](../SYNTAX.md)（Rigi 语言语法参考）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 14. Wrapper（修饰器）

### 14.1 概述

Wrapper 是绑定到被修饰实体生命周期的**值**，类似 Python 装饰器 + Java 注解的混合体。

- 继承链：`MyWrapper` → `Wrapper` → `ValueType`
- wrapper 默认是**非 rich struct**，保持值语义与宿主绑定的生命周期；需要持有对象字段时显式声明 `rich wrapper`
- 可选标记 `shared`，这会同时放宽可修饰的目标、收紧自身字段闭包（见 §14.9）
- 三种目标（互斥）：`.Entity`、`.Method`、`.Value`
- 嵌套顺序：按声明顺序从外向里
- 通过 `:` 运算符访问：`obj:MyWrapper`（链式 `obj:A:B` 表示"obj 的修饰器 A 的修饰器 B"），该表达式是**只读的存储位置**——只能作成员访问的接收者，不可整体赋值也不可整体取值（见 §14.5）

### 14.2 实体修饰器（Entity Wrapper）

修饰 class、interface、wrapper、struct（含 enum struct）。非 rich 宿主只能内嵌非 rich wrapper，见 §14.9。

```rigi
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
        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)
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
        inner(symbol=symbol, value=value)
    }

    operator .proxy.opr.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(
        symbol: String,
        namedArgs: named TNamedArgs...,
        unnamedArgs: TUnnamedArgs...
    ): TReturn {
        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)
    }
}
```

- `TTarget` 泛型参数可访问被修饰对象的类型；不写约束时可以是任何合法的 wrapper 目标类型，可用 `extends`/`supers` 进一步缩窄。
- `self` 关键字访问被修饰的对象实例（类型为 `TTarget`）。
- `this` 仍为 wrapper 自身实例。
- `.proxy.*`、`.proxy.get.*`、`.proxy.set.*`、`.proxy.opr.*` 不再是可声明多个并按 pattern/优先级竞争的代理；它们分别是普通方法、getter、setter、operator 类别的唯一 universal fallback。
- 同一个 Entity Wrapper 对每一类别只能实现零个或一个 wildcard proxy；重复声明同类别 wildcard 是编译错误。
- 四类 wildcard 的泛型与参数形状是编译器规定的 canonical shape，不能通过额外约束或部分参数 pattern 把它缩窄为只吃某些签名。需要特殊处理某个已知成员时使用 specific proxy；需要在 universal fallback 内进一步分类时显式检查 `symbol`。
- Entity wrapper 至多声明一个泛型参数（恰一个时即 `TTarget` 角色、`self` 的类型来源；零个时 proxy 体内引用 `self` 是编译错误）；Value/Method wrapper 不得声明 wrapper 级泛型参数（proxy 方法自身的泛型参数不受此限）。
- specific proxy 的形状（参数名/参数类型/返回类型）必须与被代理成员**全等**（wrapper 泛型参数代入后判定；`.proxy.get.<名>`/`.proxy.set.<名>` 的 `value` 参数类型 = 字段类型）；形状不匹配的 specific proxy 是编译错误。四类 wildcard 按上例的 canonical shape 逐参数校验。
- **get 类别代理不得调用 `inner(...)`。** `.proxy.get.<名>` / `.proxy.get.*` 的 `value` 参数**就是**内层已经算好的结果。get 链是值从内向外的只读变换管线：backing →（用户 getter）→ 内层 `proxy.get` → 外层 `proxy.get` → 使用点；每一环基于 `value` 返回（可能变换后的）新值，不存在「向内传参继续求值」的 inner。这与 set/call/operator 类别不同（它们的 `inner(...)` 是向内的下一环调用）。此禁令是读取路径只读性的设计保证，不是实现缺陷——get 代理因此无法借 inner 向内层发起额外调用或触发写操作，读取路径除 proxy 自身日志类副作用外不改变被读对象状态。BIL 层 `invoke fn(..inner)` 出现在 get 派发上下文中非法（VM 抛异常）。
- wrapper 实例由 `@W(...)` 应用在**宿主创建时**安装：frontend 为宿主合成 `..init.wrapper`（体内 `new.wrapper.*`，应用实参经该方法参数 / `new.wrapped` 前缀传入；见 `BIL_STANDARD.md` §9.7 / §14.4 / §14.5），Middleware/VM 在实体 init 之前自动调用之，结果写入宿主的 Middleware 合成隐藏存储（命名约定 `BIL_STANDARD.md` §5.3）；此后不可替换（§14.5）。**安装时机与闭包（新 init 原则，§9.3/§9.7 修订）**：VM 只调用**实际类型**的 `..init.wrapper`；它安装**继承闭包全部** wrapper——本类的 Entity/Field/Method 应用 + 基类继承来的全部应用（含基类未被 override 方法的 Method wrapper；§14.9 重申的同定义 Entity 应用按派生覆盖去重、只安装一次）——并在其后调用闭包全部 `..init.field.*`（字段初始值）；两者都**早于任何基类 init 体**。因此基类 init 体/字段初始值表达式里读写字段时，继承来的 wrapper 已安装、读链正常命中（不存在「init 时 wrapper 尚未安装」的窗口）。

### 14.3 值修饰器（Value Wrapper）

修饰字段或栈上变量（`var`/`const`）。修饰实例字段时，wrapper 存放在宿主类型的 Middleware 合成隐藏存储中，因此宿主必须能够内嵌该 wrapper 的实际值类型（见 §14.9）；修饰栈上变量与静态/全局字段时，值统一由编译器合成的 cell 隐藏子类盛装（§5.2 同一机制），wrapper 应用标记挂在子类的 `value` 字段上（BIL `wrapped(W)`）；**静态字段**的 cell 存储落地在声明类的 companion singleton 实例上（与静态 Method wrapper 同一 companion，见 §14.4），cell 构造与 wrapper 安装由 companion 的 `init` 完成、VM/Middleware 在 main 前急切初始化；**局部** cell 在声明点构造；**全局**字段的 cell 隐藏子类即 singleton（cell 自身为共享单例，字段初值表达式在 cell 单例的 `init` 里求值），与静态字段同由 VM/Middleware 在 main 前急切初始化。对变量类型没有额外的宿主内嵌要求。字段同时带 Value wrapper 与用户 getter/setter 时，使用点写路径为 wrapper set 链（outer→inner）→ setter → backing（setter 体内 `value` 在 BIL 中为保留字段 `..value`，直写 backing）；读路径为 backing → getter → wrapper get 链（inner→outer）。构造期 init 体内写不绕 wrapper 链，但带 setter 时仍经 setter 应用（§9.4.1）。

```rigi
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

**`.proxy.get` 不得调用 `inner(...)`。** `value` 参数即内层已算好的结果（读路径：backing → 用户 getter → 内层 `proxy.get` → 外层 `proxy.get` → 使用点）；get 链上没有「向内传参继续求值」的 inner。这是读取路径只读性的设计保证（与 §14.2 Entity get 代理同一条约束），不是实现缺陷——get 代理因此无法借 inner 向内层发起额外调用或触发写操作。违反时 BIL `invoke fn(..inner)` 非法（VM 抛异常；见 `BIL_STANDARD.md` §15.4）。

### 14.4 方法修饰器（Method Wrapper）

修饰 lambda 或方法。

```rigi
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

    // 通配符 + 可变参数（"至少有前面这些参数的方法"）。
    // .name 是编译器保留的带点参数名（用户无法伪造），运行时值为
    // 实际执行的完整 BIL 方法符号（虚/接口派发后的实现槽，而非
    // 调用点静态符号）：普通方法如 Service$fetch(id:.i32)@.string；
    // 经 Base 静态类型调用 Child.work 时为 Child$work()@.i32；
    // lambda 场景为 ..lambda..UUID$$call(x:.i32)@.i32。
    operator .proxy.call(.name: String, args: named Any...): Any {
        return inner(.name, args)
    }
}

// 使用：方法
@Timed()
pub func heavyComputation(): i32 { ... }

// 使用：lambda（annotation 写在 lambda 头内部，见 §5.1）
var f = func{ @Timed() async (x: i32): i32 -> { ... }}

// 错误示范——annotation 在 var 声明上：会被认为修饰 var 变量本身
// （Value wrapper 目标），而 Timed 是 .Method 目标，因此编译报错
@Timed
var a = func{ async (x: i32): i32 -> { ... }}
```

`.name` 的运行时值是**实际执行的方法**的完整 BIL 符号（编译器保留，用户无法伪造）。虚调用 / 接口调用落到 override 时为子类或实现类型的实现槽符号，而不是调用点静态类型上的声明符号；未 override 与非虚调用则与声明侧符号一致。详见 §14.8。

### 14.5 使用 Wrapper

```rigi
@Logged("DEBUG")
@Serializable()
pub class MyService {
    ...
}

// 访问 wrapper：obj:Logged 是宿主持有的那份 wrapper 的只读 place
var service = new MyService()
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

wrapper 是与宿主绑定的值，生命周期与被修饰实体同生共死；这与是否声明 rich 无关。允许整体取值就会造出一份脱离宿主而独立存活的 wrapper 实例，允许整体赋值就会替换这份绑定，因此二者都被禁止。

wrapper 内的 `this` 遵守相同规则：只能用于成员访问、成员调用或索引，不能整体返回、赋值、传参、装箱或转换，也不能被闭包捕获。闭包中省略 `this` 的实例字段和方法访问、嵌套闭包捕获同样禁止；不捕获 wrapper 的闭包仍然合法。`self` 是独立的宿主引用机制，不是 wrapper 整体值。

泛型和运行时 `Type<T>` 也不能用于普通构造 wrapper；运行期解析到 wrapper 的普通构造必须抛出 `NoSuchMethodException`，只能由宿主安装机制创建。

wrapper 自身的字段可变性仍按普通规则由字段声明（`var`/`const`）与可见性决定；"只读"约束的是 `obj:Wrapper` 这个 place 整体，不是其成员。

对 place 上方法调用与索引**读**，编译器可取 wrapper 值拷贝作为 receiver（Entity 应用经 `get.wrapper`，字段-Value 应用经 `get.wrapper.field`；见 `BIL_STANDARD.md` §12.4），与字段原地读写路径分离。深层字段写穿 `place.a.b... = rhs` 在语义上等价于：正向逐字段读取并物化中间值、写叶、再对值类型中间层反向写回（遇引用类型中间层即停止；需要写回但 const/无 setter/不可见时诊断）。含索引或调用的深写目标非法。

proxy 方法体内的 `this` 同样是原地访问宿主持有的那份 wrapper，因此 `@Clamped(0, 100)` 这类可变 wrapper 状态在多次调用之间保持一致。

wrapper place 的接收者来源有三：字段/局部变量的应用（`@W` 标注）、宿主静态类型的应用（Entity wrapper 经类型声明标注），以及泛型参数的 `with W` 约束（§3.6——约束等价于一次应用，`param:W` 合法且语义相同）。

### 14.6 派发顺序与 wildcard 唯一性

当一个调用同时被多个 wrapper 命中时：

- **跨 wrapper**：按声明顺序从外到内（outer → inner）嵌套。
- **同一 wrapper 内**：匹配的 specific proxy 优先于对应类别的 wildcard proxy；二者是择一关系，不会在同一 wrapper 层同时执行。
- **同一 wrapper 内**：普通方法、getter、setter、operator 四个类别分别最多存在一个 wildcard proxy，因此不存在同类别 wildcard 的重叠、排序或 priority。
- specific proxy 或 wildcard proxy 调用 `inner(...)` 后，下一层 wrapper 独立重复同一套 specific → wildcard → 实体成员/下一层的选择。字段读写时该「实体成员」是访问器：写链末为 setter（无 setter 时直写 backing），读链头为 getter（getter 返回后再过 get 链）。get 类别代理（Entity 的 `.proxy.get.<名>` / `.proxy.get.*` 与 Value 的 `.proxy.get`）不得调用 `inner(...)`：get 链的值经 `value` 参数流入，没有向内的下一环（§14.2 / §14.3）。
- **`inner(...)` 的调用形状 = proxy 函数自身的参数形状**。specific proxy 的参数列表本身与被代理成员全等，inner 写全部值实参（含对 vargs/kwargs 包参数的具名/位置转发）；wildcard proxy 的保留首参（Entity 为 `symbol`，Method wrapper 为 `.name`）同样必须显式出现在 inner 实参中。模板 fn 上的可变泛型包（`TNamedArgs...` / `TUnnamedArgs...` 等）由编译器在 Bound/Lowered 层显式携带，并在 BIL `invoke fn(..inner)` 中按 §7.2 序**前置**为 `.generic.<Pack>` 操作数（保留首参与值包随后）；包解包与下一环烘焙归 Middleware（见 `BIL_STANDARD.md` §15.4）。固定泛型参数不出现在该调用操作数列表中。

`@ProxyPriority` 不再存在；编译器不进行 wildcard pattern 重叠分析，也不维护任何用户指定的数值优先级。

### 14.7 未声明方法的动态降级

当对某个值调用其**静态类型上未声明**的方法，且该类型的 wrapper 链中存在普通方法类别的 `.proxy.*` 时，调用会**降级**为动态派发（编译为对统一 `call???` 的调用，见 `RUNTIME.md`）；否则为编译错误。

```rigi
// service 的静态类型上没有 fetchUserById，但存在 .proxy.*
service.fetchUserById(42)     // 降级为携带 canonical symbol 的 call??? 请求
```

- 一旦 wrapper 链中存在 `.proxy.*`，对该静态类型未声明方法的调用不再具有原成员声明提供的静态类型保证。
- 实参按统一胖值 ABI 传入；返回值在调用点按期望类型插入一次转换，不符则抛 `core.CastException`。
- 无任何 wildcard proxy 可路由请求时，最终落到 `Any.call???` 的默认实现并抛 `core.NoSuchMethodException`。
- getter、setter 和 operator 在编译器 lowering 后同样是方法请求；运行时仍只保留一个 `call???` slot，并由 Middleware 合成的路由体根据 `symbol` 将到达该入口的请求转入 `.proxy.get.*`、`.proxy.set.*` 或 `.proxy.opr.*`（`RUNTIME.md` §14.2）。这不要求为三类操作额外增加 `get???`、`set???` 或 `opr???` slot。

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

**已声明成员的 wildcard 保留首参（Method wrapper 的 `.name`、Entity/运算符 wildcard 的 `symbol`）填的是实际执行的方法符号**——即虚派发 / 接口派发落到的实现槽 canonical 符号，而不是调用点静态类型上的声明符号。经 `Base` 引用调用 `Child` 的 override 时，`.name` / `symbol` 为 `Child$work()@.i32`，不是 `Base$work()@.i32`；子类未 override 时为实现所在类型的符号（`Base$work()@.i32`）。非虚调用与调用点符号一致。lambda 的 `.name` 仍为隐藏类 `$$call` 合成符号。wildcard 经 `inner(.name, …)` / `inner(symbol=…)` 原样转发时下一环看到同一实现槽符号；proxy 体改写保留首参则按改写后的符号重路由，不再二次虚派发。未声明方法的降级请求（§14.7）仍由调用点按静态类型合成，不受本条约束。

未声明方法的降级请求（§14.7）没有声明位置与参数名可编码，其 symbol 由调用点合成：宿主前缀取 receiver 静态类型的定义级 canonical 名；若调用点存在显式泛型实参，则按书写序以 canonical 类型引用编码在方法名后的 `<...>` 段，无显式泛型实参时省略该段；参数段按调用点书写序——位置实参只写静态类型、具名实参写 `名:类型`；返回段恒为 `.any`（胖值 ABI 返回 `Any`，向期望类型的转换在调用点由编译器插入一次 cast，不符抛 `core.CastException`，见 `RUNTIME.md` §14.2）。例如 `service.fetchUserById(42)`（`service` 静态类型 `myapp::Service`）的请求 symbol 为 `myapp::Service$fetchUserById(.i32)@.any`；`service.fetchUserById\<i32, String>(42)` 则为 `myapp::Service$fetchUserById<.i32,.string>(.i32)@.any`。

### 14.9 wrapper 的 `rich`/`shared` 规则与目标矩阵

**wrapper 默认是非 rich struct，按需显式声明 rich。** wrapper 实例仍与被修饰实体、方法或值同生共死，不能作为普通值整体取出或替换；rich 只决定普通字段的持有能力。

- 非 rich wrapper 的普通字段只能内嵌非 rich ValueType；`rich wrapper` 可以持有对象与 rich 值。源码与 BIL 都只按显式 `rich` 标记判定。
- wrapper 可以独立标记 `shared`。`shared wrapper` 仍非 rich；`shared rich wrapper` 的普通字段只能持有 shared object 与共享安全 ValueType。
- `self` 是 wrapper 调用约定中的独立宿主参数，不是字段，不占 wrapper 实例存储，也不参与 rich 持有闭包。读取 wrapper place 时绑定宿主，在需要 `self` 的代理调用中与 wrapper 状态 receiver 分开传递；源码中仍仅允许在 proxy 体内使用 `self`，不扩大到普通成员或构造体。调用帧负责宿主的有效期，异步帧必须保活宿主，不能把栈借用带过挂起点。它不授予任意转换、普通字段存储或跨协程逃逸的权限。
- 隐式填入的 `TTarget`/`TField` 与显式泛型实参同样接受字段闭包检查；宿主闭合实例化时再次代入隐藏 wrapper 存储。为拒绝无限展开的泛型存储图，补验最多展开 64 层、4096 个不同类型，超限报错而非跳过检查。
- wrapper 不能标记 `open`/`abstract`（rich struct 的继承规则另有约束时以 §10 为准），也不能标记 `singleton`。

**宿主可内嵌性（对全部三类 wrapper 生效）**：wrapper 实例存放在宿主的 Middleware 合成的隐藏存储中（命名约定 `BIL_STANDARD.md` §5.3），因此必须满足普通值的内嵌规则。由此：

- Entity wrapper 目标包含 class、interface、wrapper、struct、enum struct；
- **非 rich struct、非 rich enum struct 与非 rich wrapper 只能内嵌非 rich wrapper**；Entity、实例字段 Value 与实例方法 Method 应用均遵守本条；
- 内建类型的源码声明同样遵守上述规则，标记应用不得改变内建类型既定 ABI；
- 无实例字段、无代理、无嵌套 wrapper 存储且构造为空的非 rich wrapper 是纯标记，不需要实例存储；这是一条通用规则，不专属于 SerializationBase。固定 ABI 内建类型只接受这种纯标记应用，不能因源码声明可扩展而改变数值、String 或容器的物理布局。
- 修饰栈上变量、全局/静态字段、全局/静态方法时不涉及宿主内嵌，本条不适用；栈上变量与全局/静态字段上 Value wrapper 的存储形态见 §14.3（统一 cell 隐藏子类，非宿主内嵌）。

**shared 目标矩阵**：

| wrapper | 可修饰的目标 | 自身字段闭包 |
|---|---|---|
| `shared wrapper` / `shared rich wrapper` | 全部满足内嵌规则的目标（含 shared 类型、全局/静态成员） | 非 rich 仅非 rich 值；rich 按 shared 闭包收紧 |
| 非 shared `wrapper` / `rich wrapper` | 仅非 shared 目标（下表四类），且满足内嵌规则 | 非 rich 仅非 rich 值；rich 可持有 local object |

非 shared wrapper 可修饰的「非 shared 目标」是：

- **A. 方法**：不是全局方法或静态方法，且所属类型不是 shared；
- **B. 字段**：不是全局字段或静态字段，且所属类型不是 shared；
- **C. 栈上变量**：全部 `var`/`const` 局部变量（Value wrapper 存储形态 = 编译器合成的 cell 隐藏子类，见 §14.3）；
- **D. 类型**：非 shared 的类型。

其根据是 §3.1.1 的逃逸闸门：全局/静态存储与 shared 类型的字段闭包都不得触及 local object，而非 shared wrapper 的隐藏存储可能持有 local object。反过来，shared wrapper 修饰非 shared 目标始终合法——shared 闭包比 local 闭包更严，不会引入新的逃逸路径。

**interface 目标的传染校验**：interface 本身不产生实例，被修饰 interface 的 wrapper 实例落在每个实现者上。因此：

- 被修饰的 interface 的所有实现者必须自身满足对应 wrapper 的内嵌规则；
- 被**非 shared** wrapper 修饰的 interface **不得被 shared 类型实现**（否则 shared 实现者会获得一个可能持有 local object 的隐藏存储）。

这两条在实现者声明处检查并报错，而不是在 interface 声明处。

**wrapper 继承闭包**：wrapper 应用不是隐式传染，而是声明列表语义。子类型、子接口
以及 override 的普通方法和 getter/setter 必须在自己的声明处显式重复继承闭包中的
wrapper 应用；闭包沿间接基类和 interface 继续展开。重复声明必须保持 wrapper 定义、
应用实参和相对顺序一致，不能删除、替换或重排；额外 wrapper 可以追加。getter/setter
的应用挂在其字段声明上，getter 与 setter 的 override 检查分别进行。

---
