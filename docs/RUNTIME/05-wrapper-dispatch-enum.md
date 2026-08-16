# Wrapper 派发管线 / 诊断工具 / enum struct（§14–§16）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 14. Wrapper 派发管线

**静态组合**：实体修饰器在语言语义上把 wrapper 逻辑按声明序从内到外嵌套进方法派发（替换 `inner`），因此天然骑 vtable。运行时**不能**增删、重排或禁用 wrapper。烘焙动作（逐应用特化、inner 链接、原始体替换，以及 `call???` router 体合成）由 Middleware 在合法 lowering 时完成（边界见 `BIL_STANDARD.md` §23）；frontend（编译器）产物携带标记与 wrapper 安装契约，不合成派发链符号、不替换原始方法体：

- (a) 声明上的 wrapper 应用标记（BIL 修饰符 `wrapped(W)`）；应用 **init 实参**由宿主 `..init.wrapper` + `new.wrapper.*` / 有参时 `new.wrapped` 家族承载（`BIL_STANDARD.md` §8.3.1 / §9.7 / §14.4 / §14.5）——Middleware/VM 在实体 init **之前**自动调用 `..init.wrapper`；
- (b) proxy 模板 fn——wrapper 类型的成员 fn，带 `wrapper-proxy(specific|wildcard)` 修饰符（`BIL_STANDARD.md` §8.4），体内的 `inner` / `self` 以占位指令表达（`invoke fn(..inner)` 见 `BIL_STANDARD.md` §15.4，`get.self` 见 `BIL_STANDARD.md` §12）。`fn(..inner)` 调用操作数显式携带待转发的可变泛型包（`.generic.<Pack>` 前置）、wildcard 保留首参（`symbol` / `.name`）与值包（`.kwargs.*` / `.vargs.*` 随后，按声明序）；Middleware 烘焙下一环时消费这些操作数（解包/shim/特化链接），frontend 不展开；
- (c) 未声明方法的降级调用点 = 对 `core::Any$call???` 的普通 `invoke`（见 §14.2）；
- (d) 静态方法被 Method wrapper 修饰、静态字段被 Value wrapper 修饰时的 companion singleton（声明类的嵌套类 `..companion`，无 UUID）与静态壳体——所有 singleton（含 companion）由 VM/Middleware 在 main 开始执行前**急切初始化**（`BIL_STANDARD.md` §8.7）。

最终内联仍归 Middleware。

**wrapper 值的表示**：wrapper 恒为 rich struct（`SYNTAX.md` §14.9），因此它是一个带 typeid 的胖值，而不是独立的堆对象——没有对象头、没有对象身份、不作为独立 GC 节点被追踪；其内部托管引用字段照常经 `refMap` 参加 acquire/release。

**实例字段 / Entity 形态**（宿主内嵌）：wrapper 实例存放在宿主的 Middleware 合成隐藏存储中（命名约定见 `BIL_STANDARD.md` §5.3；存储布局是 Middleware 的实现职责，BIL 文本不再声明隐藏字段），因此：

- 宿主类型必须允许内嵌 rich struct；非 rich struct 不能被修饰，这是编译期不变量，运行时无需检查。
- 非 shared wrapper 可能持有 local object，所以只能出现在非 shared 宿主中；shared wrapper 走 microSGC 路径。

**局部 / 静态 / 全局形态**（统一 cell 存储，`SYNTAX.md` §5.2 / §14.3）：被 Value wrapper 修饰的局部变量与静态/全局字段，值由编译器逐变量合成的 cell 隐藏子类盛装——子类 `extends .cell<T>` / `.readonly_cell<T>`，自持 `pub value` 字段并带 `wrapped(W)` 标记。Middleware 的烘焙识别契约 = 「继承 Cell 族 + 字段 wrapper 标记」；即使不做 Cell 特判、按普通类烘焙也可正确工作（`getValue`/`setValue` 是普通虚调用，`get.wrapper.field`/`set.wrapper.field` 走既有字段-Value 应用机制），`.cell`/`.readonly_cell` 特权拼写的特判仅供激进优化（消除 cell 间接/直读槽位等）。栈帧（或静态槽）持有的是 **cell 对象引用**；wrapper 状态内嵌于 cell 实例子类 `value` 字段的隐藏存储，生命周期与栈值/静态槽一致——非 shared wrapper 因此可合法出现在栈帧与局部 cell 路径上，而不必依赖宿主类型内嵌。**静态字段**的 cell 存储落在 companion 实例上（`BIL_STANDARD.md` §8.7），cell 构造与 wrapper 安装由 companion 的 `init` 完成（VM/Middleware main 前急切初始化）；**局部** cell 的构造时机在声明点；**全局**字段的 cell 隐藏子类即 singleton（cell 自身为共享单例，字段初值在 cell 单例的 `init` 里求值），与静态字段同由 VM/Middleware 在 main 前急切初始化。

**place 访问**（两形态共用）：

- 路径表达式 `value:WrapperType` 与 proxy 体内的 `this` 都是对 wrapper 隐藏存储的**原地访问**，从不复制。源码层 `value:WrapperType` 是只读 place（`SYNTAX.md` §14.5）：既不能被整体赋值，也不能被整体取出，因此运行时不存在脱离宿主独立存活的 wrapper 值，也不为 wrapper 提供任何别名或共享机制。wrapper place 写侧链指令操作数是已有两态 `field(F)|wrapper(W)`（而非编译器合成的隐藏字段符号；字段-Value 应用 = 相邻 `field(HOST_FIELD)+wrapper(W)`——局部/静态时 `HOST_FIELD` 即 cell 子类的 `value` 字段；Entity 应用 = `wrapper(W)`）。frontend lowering 在成员**读取**/调用/索引读路径上经 `get.wrapper` / `get.wrapper.field` 取得**值拷贝**，再发普通 `get.field`/`invoke`/`get.array`（与原地写路径分离；BIL §12.4）；wrapper 字段原地写走 `set.wrapper.field`；深层写穿由 frontend 展开为多次现有 get/set（最外层必要写回复用 `set.wrapper.field`，普通值中间反向写回仍发 `set.field`），不新增专用深写指令、也不把整条深路径压进单条超长链。隐藏存储不可用普通字段寻址。

### 14.1 四类唯一 wildcard proxy

Entity Wrapper 可以分别实现以下四种 universal wildcard；每一类别在同一个 wrapper 中只能出现零个或一个：

```rigi
operator .proxy.*<named TNamedArgs..., TUnnamedArgs..., TReturn>(
    symbol: String,
    namedArgs: named TNamedArgs...,
    unnamedArgs: TUnnamedArgs...
): TReturn

operator .proxy.get.*<TValue>(
    symbol: String,
    value: TValue
): TValue

operator .proxy.set.*<TValue>(
    symbol: String,
    value: TValue
)

operator .proxy.opr.*<named TNamedArgs..., TUnnamedArgs..., TReturn>(
    symbol: String,
    namedArgs: named TNamedArgs...,
    unnamedArgs: TUnnamedArgs...
): TReturn
```

wildcard 体内 `inner(...)` 必须写**全形状**（保留首参在 inner 中显式传递，不在 ABI 隐式承担）：
- `.proxy.*` / `.proxy.opr.*`：`inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)`；
- `.proxy.get.*` / `.proxy.set.*`：`inner(symbol=symbol, value=value)`。

这些 wildcard 不是可重复声明并按泛型 pattern 竞争的 overload，而是四个操作类别各自唯一的 fallback handler。其参数和泛型形状由编译器固定；同一 wrapper 内重复实现同类别 wildcard 是编译错误。

派发顺序为：

- 跨 wrapper：按声明序 outer → inner 嵌套；
- 同 wrapper 内：匹配 specific proxy 时使用 specific，否则使用对应类别的唯一 wildcard；
- specific 与同层 wildcard 是择一关系；调用 `inner(...)` 后，下一层独立重复该选择；
- 不存在 wildcard 重叠、pattern specificity 或 `@ProxyPriority`。

### 14.2 单一 `call???` slot

`call???` 定义在 `Any`（万物基类）上，因此是每个对象 vtable 中一个**固定 offset 的 slot**，不涉及动态向 vtable 增加条目；继承链经 vtable 正常解析：

```rigi
call???<TResult, named TNamedArgs..., TUnnamedArgs...>(
    symbol: String,
    namedArgs: named TNamedArgs...,
    unnamedArgs: TUnnamedArgs...
): TResult
```

- `call???` 是 bootstrap 内建方法：bootstrap 声明 + VM 内建 hook 实现（`BIL_STANDARD.md` §22.5 方法 hook，按方法符号命中）。默认实现（请求未被任何 wrapper 路由时）由该 hook 提供，直接抛 `core.NoSuchMethodException`，含可按配置启用的 log 代码——**不是**编译器生成的 body。
- 方法、getter、setter、operator 在 lowering 后本质上都是方法请求。运行时只保留这一个 slot；对已有声明成员的命中烘焙，以及 `call???` 按 canonical `symbol` 判定类别并转入 `.proxy.*`、`.proxy.get.*`、`.proxy.set.*` 或 `.proxy.opr.*` 的类别路由体，均由 Middleware 合成，不另外设置 `get???`、`set???`、`opr???`。类别路由作为 Middleware 插入点的架构预留（`SYNTAX.md` §14.7 末条语义不变，执行主体为 Middleware）。
- 跨模块编译调用方时，若被调用成员已有普通实现则走正常 vtable slot；需要 fallback 时交给 `call???`，而 `call???` 自身仍经 vtable 解决继承。
- 给已有方法增加 specific proxy 后，只需重编译被修饰模块并由 Middleware 重新烘焙，使原 vtable slot 指向新的 wrapped body；调用方无需因 wrapper 变化而重编译。

**未声明普通方法的降级规则**（对应 `SYNTAX.md` §14.7）：静态类型无匹配声明方法且 wrapper 链中存在 `.proxy.*` 时，frontend 发射对 `core::Any$call???` 的普通 `invoke`（携带 canonical symbol）；实参按统一胖值 ABI 传递，返回值在调用点按期望类型转换，不符抛 `core.CastException`。frontend **不**合成任何 router / 降级链符号。

上文的 `call???` 泛型签名是**逻辑签名**——`call???` 的规范签名实质化为非泛型胖值签名 `(symbol: String, namedArgs: Array\<Pair\<String, Any\>\>, unnamedArgs: Array\<Any\>): Any`（`BIL_STANDARD.md` §15.4）。泛型 typeid 包不单独传递：每个 `Any` 胖值自描述 typeid（§2），wildcard proxy 体可在包元素上直接做 `is`/`as` 检查；`TResult` 的角色由调用点的 cast 物化承担（`BIL_STANDARD.md` §12.1，不符抛 `core.CastException`）。frontend 降级调用点发射 `invoke core::Any$call???`；被 wrapper 命中的宿主上的类别路由体由 Middleware 按 vtable 语义合成（链末落到 `Any.call???` 的 VM hook 默认实现）。

### 14.3 canonical symbol ABI

`symbol` 是编译器生成并传递的完整调用身份：

```text
类型：
命名空间::类名[.子类名...]

方法：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static.]方法名([参数名:参数类型,...])@返回值类型

字段 / 全局变量 / 全局常量：
命名空间::[可能有的类名[.可能有的子类名...]]#[.static.]名称@字段类型

运算符：
命名空间::类名[.可能有的子类名...]$$运算符名称([参数名:参数类型,...])@返回值类型

getter / setter：
命名空间::[可能有的类名[.可能有的子类名...]]$[.static].get.名称@字段类型
命名空间::[可能有的类名[.可能有的子类名...]]$[.static].set.名称@字段类型
```

`.static.` 仅用于静态方法、静态字段及其访问器；Singleton 实例成员不因类型是 singleton 而自动成为 static symbol。

泛型与可变参数被规范化为保留名称的隐藏参数：

- 类型声明的 `out`/`in` 方向保留在 BIL `.type generic(...)` 元数据中；它只影响
  构造类型的赋值兼容，不改变 hidden typeid 参数的顺序或 ABI；
- 单个泛型 `T` → `.generic.T: Type`；
- 匿名可变泛型 `TArgs...` → `.generic.TArgs: Array\<Type>`；
- 具名可变泛型 `named TArgs...` → `.generic.TArgs: Array\<Pair\<String, Type>>`；
- 匿名值可变参数 `args...` → `.vargs.args: Array\<Any>`；
- 具名值可变参数 `named args...` → `.kwargs.args: Array\<Pair\<String, Any>>`。

这些 hidden arguments 与 canonical symbol 一起保留实际泛型 typeid、值参数包及其名称，不需要为动态 forwarding 再建立第二套类型擦除协议。以 `.` 开头的 hidden 参数名由编译器保留。

未声明方法的降级请求没有声明侧泛型参数名可展开为 hidden argument；调用点的显式泛型实参按书写序编码在方法名后的 `<...>` 段，使用 canonical 类型引用（例如 `Service$fetch<.i32,.string>(.i32)@.any`）。该段属于 `symbol` 字符串，不新增 `call???` 的 hidden 参数或 BIL invoke 操作数。

---

## 15. 派发链诊断工具

编译器需提供诊断能力：给定一个调用点，打印其解析出的完整 wrapper 派发链——即 **Middleware 将要烘焙的链**，包括跨 wrapper 的 outer→inner 顺序、每层命中的 specific 或对应类别唯一 wildcard、proxy 模板声明的 canonical symbol，以及是否具备降级到 `call???` 的资格。这是随实现一并提供的编译器功能，而非事后补充的调试手段——§1 提到的“派发链可能有多层”这一复杂度，靠这个工具而非靠用户记忆来管理。

工具形态：CLI 子命令 `compile --file <src> --explain-dispatch`。数据源为编译器报告的**应用登记 × proxy 声明的形状匹配结果**（每层将命中 specific|wildcard 与 proxy 声明 canonical symbol）与**降级资格**（wrapper 链是否含 `.proxy.*`）。编译器不再合成烘焙符号，报告中的特化身份改为 proxy 模板声明的 canonical symbol；按调用点（源位置）过滤为预留扩展。

---

## 16. enum struct 的运行时表示

每个 `enum struct` 都是普通 ValueType 的封闭特例，并额外含有一个编译器生成、用户不可直接访问和修改的隐藏判别字段（discriminant）。它不能标记为 `open`，不能继承用户声明的 struct，也不能被其他 class/struct/enum 继承；固定继承链为 `具体 enum → Enum → ValueType`。判别字段随 enum 值一起内嵌、复制或进入 Box，并计入 `TypeSheet.typeSize`；所有 case 的实际运行时类型仍然是该 enum struct 本身。

Enum 没有对源码开放的普通构造入口。init 只作为编译器生成 case 入口的实现部件存在；无论 init 是否为 `pub`，都不能通过 `EnumType(...)`、`new EnumType(...)`、`new enumTypeValue(...)` 或泛型 `T()` 直接创建 enum 值。

### 16.1 判别字段宽度

判别字段只使用两种宽度：

- `u16`：全部具名 case 的判别值都能表示时使用；
- `u32`：存在无法由 `u16` 表示的 case 判别值，或自动编号 case 数量超过 `u16` 容量时自动扩展。

没有“自由构造值”及其保留判别值；`u16` 的全部 `0...65535` 均可用于具名 case。因此自动编号的 `u16` enum 最多容纳 65,536 个具名 case；再增加 case 时扩展为 `u32`。

判别宽度是类型布局和 ABI 的一部分。由 `u16` 扩展为 `u32` 可能改变 enum 自身及内嵌它的外层 ValueType/Object 的尺寸、对齐、字段偏移和 `Span\<T>` stride，因此属于 ABI-breaking change。

### 16.2 case 构造入口

`[]` 中的每个具名 case 编译为一个设置固定判别值的构造入口：

1. 创建未完成初始化的 enum 值；
2. 在用户 init 可观察 `this` 前写入该 case 的隐藏判别值；
3. 按 case 模板中的固定参数与调用时参数洞组成实参，调用编译期已经解析的 init；
4. 返回完整 enum 值。

没有参数洞的固定 case 可以物化为模块级只读值并按 ValueType 规则复制；含参数洞的 case 物化为静态 case factory。两者都属于 case 入口，而不是 init 的直接调用。

参数化 case 只有在其目标 init 为 `pub` 时才能生成对调用方可见的参数洞；绑定到 `priv`/`protected` init 的 case 必须是固定模板。enum 不参与用户继承，因此 `protected` 不扩展出派生 enum 的 case 模板；所有 case 模板都只声明在本 enum 的 `[]` 中。`pub` 授予的是“通过具名 case 传入参数”的资格，不授予直接构造 enum 的资格。

省略类型名的 `.Case` 在编译期必须拥有已确定 enum 类型的 receiver/期望类型；这不改变运行时布局，只决定编译器选择哪一个 case 入口。

### 16.3 `is .Case` 的实现

对 enum case 的模式检查：

```rigi
value is .Failed
```

编译为隐藏判别字段与 `Failed` 编译期判别常量的整数比较。它不是子类型检查，不访问 `TypeSheet.baseTypeId`，也不比较任何用户字段。`typeOf(value)` 始终返回该 enum struct 的类型。

即使编译器能够看见当前 enum 声明中的全部具名 case，enum `switch` 作为表达式时仍必须保留 `default`。运行时不把“安全源码只能通过 case 入口构造”提升为“任何来源的位模式都必然属于当前 case 集合”；FFI、unsafe/raw memory、反序列化与跨版本 ABI 等 corner case 可以把未知 discriminant 带入程序，`default` 为这些状态提供定义行为。

### 16.4 自动与显式判别值

未使用 `->` 时，编译器按声明顺序从 `0` 开始分配判别值；这些数值属于编译产物，不承诺跨源码重排或重新 codegen 稳定。

使用 `Case -> integer` 时，该整数成为 case 的稳定判别 ABI：

- 所有显式值必须唯一、非负、为编译期常量；
- 同一 enum 内必须全显式或全隐式；
- 编译器依据最大显式值选择 `u16` 或 `u32`；
- case 重排不会改变显式判别值；
- 后续加入超出原宽度的显式值会触发宽度扩展，仍然是 ABI-breaking change。

`->` 只稳定判别值；payload 字段布局由普通 struct 布局规则决定。

---
