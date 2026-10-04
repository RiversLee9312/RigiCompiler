# 方法调用（§15）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 15. 方法调用

### 15.1 直接调用

```bil
invoke fn(METHOD_SYMBOL) RESULT [ARG_0, ARG_1, ...]
invoke.noret fn(METHOD_SYMBOL) [ARG_0, ARG_1, ...]
```

规则：

- 参数列表必须与方法的完整规范 BIL 参数签名逐项严格相同；`.return` 是结果描述，不是调用实参；
- 参数列表包含 `.this`、泛型 hidden args、普通参数和 vararg/kwarg 包；
- 值类型方法的 `.this` 实参**别名**调用点 place（不产生拷贝），方法体内经 `.this` 的字段写原地生效（`SYNTAX.md` §10）；引用类型 `.this` 为普通引用传递；
- 返回方法使用 `invoke`，无返回方法使用 `invoke.noret`；
- RESULT 类型必须等于调用表达式类型；
- 调用顺序不代表 Native ABI。

### 15.2 async 方法调用

对带 `async` 修饰的方法使用同一 `invoke` opcode。

若方法声明体返回：

```text
TResult
```

则调用表达式结果必须为：

```text
core.coroutine.Task\<TResult>
```

无结果 async 方法的调用结果必须为：

```text
core.coroutine.Task
```

`invoke` async 方法必须具有 `RUNTIME.md` 规定的 eager spawn 语义。Middleware 不得把它实现为惰性 Task。

async receiver、参数和结果的 shared 闭包合法性应由 frontend 检查，并由 BIL verifier 复核可验证部分。

### 15.3 间接调用

```bil
invoke.indirect OBJECT_VAR RESULT [ARG_0, ARG_1, ...]
invoke.indirect.noret OBJECT_VAR [ARG_0, ARG_1, ...]
```

`OBJECT_VAR` 是静态类型声明了 `operator call` 的对象引用。间接调用 = 对该对象虚调用其 `$$call` 实现（通用 callable 协议；任何实现 `operator call` 的类型都可用，不限于 lambda 隐藏类）。

规则：

- `OBJECT_VAR` 的静态类型必须声明恰好一个与实参列表逐类型严格匹配、且返回形态匹配的 `$$call`（canonical 名 `$$call`，§8.4 operator 声明形态）；
- 参数与结果严格匹配该 `$$call` 签名（canonical 全等，同 §15.1 口径）；
- 有返回使用 `invoke.indirect`，无返回使用 `invoke.indirect.noret`；
- 若命中的 `$$call` 带 `async`（如 `core::AsyncFunc` / `core::AsyncAction` 子类的实现），按 §15.2 同一规则：使用 `invoke.indirect`（非 noret），结果为 `core.coroutine.Task\<TResult>` / `core.coroutine.Task`，eager spawn。

**泛型 ABI（与 §15.1 direct invoke 完全同构）**：

- 当命中的 `$$call` 为泛型方法时，其 fn `.args` 携带 `.generic.T = .typeid` 隐藏条目（固定泛型）与/或 `.generic.TArgs`/`.generic.TValues` 包形态（§7.1/§7.2）；
- 调用点实参列表前部平铺 typeid 前缀：固定泛型以 `getid.type type(...)` 物化的 `.typeid` 临时（嵌套转发时为 `$.generic.T`），泛型可变包以 `.array<.typeid<.any>>` / `.map<.string, .typeid<.any>>` 打包物化——序与形态同 direct invoke；
- 值实参（含 `.vargs.*`/`.kwargs.*` 包）紧随 typeid/包前缀之后；
- 验证器按被调 `$$call` 的 fn 定义 hidden 条目数跳过前缀并校验前缀类型，再比对普通参数与值包（缺/错 typeid 前缀非法）。

lambda 的 BIL 形态是普通 `new type(..lambda..HASH)` 构造 + `invoke.indirect`；BIL 没有 lambda 专属指令。

### 15.4 调用派发链下一环（proxy 模板）

```bil
invoke fn(..inner) RESULT [ARG_0, ARG_1, ...]
invoke.noret fn(..inner) [ARG_0, ARG_1, ...]
```

仅 proxy 模板 fn（带 `wrapper-proxy`，§8.4）体内合法。语义对应源码层 `inner(...)`：以普通 invoke 家族的保留目标 `fn(..inner)` 表达「调用派发链下一环」的占位；Middleware 烘焙时将该调用链接到下一环（下一特化、原始体或类别路由）。

规则：

- 必须出现在 `wrapper-proxy(specific|wildcard)` 标记的方法体内；出现在其他 fn 内非法；
- `fn(..inner)` 是保留目标，不是 canonical 方法符号，不查 `MethodSymbols`，也**不携带 receiver**；
- 操作数序对齐 §7.2 调用序子集：**先**模板 fn 声明中的**可变泛型包**隐藏参数（`.generic.<Pack>`，按声明序；固定泛型参数不出现在本列表——特化侧由 Middleware 自持），**再**源码层 `inner(...)` 的显式值实参（按声明序；wildcard 的保留首参 `symbol` / `.name` 显式携带，随后是 `.kwargs.*` / `.vargs.*` 包整体转发）。已声明成员上该保留首参的运行时值为实际执行的实现槽符号（虚/接口派发后，见 `SYNTAX.md` §14.4 / `RUNTIME.md` §14.3），不是调用点静态符号。例如 wildcard 模板：
  `invoke fn(..inner) $.t0 [$.generic.TNamedArgs, $.generic.TUnnamedArgs, $symbol, $.kwargs.namedArgs, $.vargs.unnamedArgs]`；
- 源码语法：`inner(...)` 的调用形状 = proxy 函数自身的参数形状（wildcard 保留首参必须显式写出）；泛型包由 frontend 在 Bound/Lowered 层显式携带并在调用前置物化，Middleware 消费解包/烘焙；
- 带返回的模板用 `invoke fn(..inner)`，RESULT 类型必须严格等于该模板 fn 的声明返回类型；void 模板用 `invoke.noret fn(..inner)`；
- 值实参个数与类型必须与模板 fn 声明的（经源码 `inner` 规则过滤后的）显式实参一致；前置 `.generic.*` 操作数必须可解析为当前 fn `.args` 中已声明的同名隐藏参数。
- **get 派发上下文中不存在 inner。** get 链是值从内向外的只读变换管线：backing →（用户 getter）→ 内层 proxy.get → 外层 proxy.get → 使用点；每一环 proxy 的 `value` 参数**就是**内层已经算好的结果，proxy 基于它返回（可能变换后的）新值。不存在「向内传参继续求值」的 inner——这与 set/call/operator 类别不同（它们的 inner 是向内的下一环调用）。get 类别 proxy 模板体内出现 `invoke fn(..inner)` / `invoke.noret fn(..inner)` **非法**（VM 抛异常）。这是读取路径只读性的设计保证：get 代理无法借 inner 向内层发起额外调用或触发写操作。

### 15.5 直接基类调用

```bil
invoke fn(..super) RESULT [$.this, HIDDEN_GENERIC_ARGS..., NORMAL_ARGS...]
invoke.noret fn(..super) [$.this, HIDDEN_GENERIC_ARGS..., NORMAL_ARGS...]
```

`fn(..super)` 是保留目标，不是 canonical 方法符号，也不得声明为普通 fn。它只可由 override 或 init fn 体发出，交 Middleware 解析为直接基类的原始实现并绕过 wrapper 派发链。首实参必须精确为 `$.this`；随后按 §7.2 的隐藏泛型参数、普通值参数顺序排列。**类级** `.generic.*`（所属类型的类型参数）可由实现从 `$.this` 注入，调用点允许省略；方法级固定泛型仍须按声明序转发。init 必须使用 `invoke.noret`；override 的 invoke 形态与当前 fn 返回类型一致。frontend 不生成 `..create`：`..create` 仅是 Middleware/VM 的 create 生命周期阶段步骤，可与 super-init 和 init `_ -> inheritedField` 映射共存。

**super init 匹配（与 SYNTAX §9.2.2 对齐）**：init 体内的 `invoke.noret fn(..super)` 由 Middleware 在直接基类 init 重载中定位入口。frontend 已把普通实参 cast 到被解析 init 的形参声明类型；VM 按这些实参的 **BIL 静态类型**（变量声明类型）与形参 **严格相等**（`TypesEqual` / canonical 全等）验证——不是按对象头运行期 typeid 的可赋值性再 ranking。引用类型 upcast 不改写对象头 typeid。多个可赋值 init 重载的选择只发生在语义期。

### 15.6 wrapper 动态 fallback

对于静态类型上没有声明、但根据 `SYNTAX.md` 必须降级到 wildcard proxy 的普通方法请求，frontend 生成对 `core::Any$call???` 的普通 `invoke`。

已声明的：

- 运算；
- getter；
- setter；
- 索引操作；

仍使用各自 BIL 指令，不因最终可能经过 wrapper 路由而预先改写为 `invoke`。

`call???` 的规范签名是 `RUNTIME.md` §14.2 泛型逻辑签名的实质化——`(symbol: .string, namedArgs: .array<core::Pair<.string, .any>>, unnamedArgs: .array<.any>): .any`（非泛型；实参的装箱/拆包转换沿用 §12.1 cast 语义）。降级调用点 `invoke` 的目标**恒为** `core::Any$call???`（vtable 正常解析继承）；实参规范序 = receiver、symbol 字符串资源、具名包构造、位置包构造；返回值为 `.any` 胖值，调用点按期望类型插入一次 §12.1 cast（不符抛 `core.CastException`）。

frontend 判定降级资格（静态类型无声明方法且 wrapper 链含 `.proxy.*`）只读应用标记（§8.3.1）与 proxy 声明，不合成任何符号；被命中宿主的类别路由体由 Middleware 合成（`RUNTIME.md` §14.2）。`call???` 自身在 `.intrinsics.rg` 有普通源码默认 `throw` 体，frontend 正常发射 BIL fn；VM 的 wrapper/method hook 优先序与未路由行为见 §22.5，Native 链末使用源码默认体。

### 15.6 canonical symbol 与参数包

当调用 `call???` 或 wrapper wildcard 需要 canonical symbol 时：

- canonical symbol 格式遵循 `SYNTAX.md` / `RUNTIME.md`（未声明方法的降级请求 symbol 格式见 `SYNTAX.md` §14.8 末段——显式泛型实参在方法名后的 `<...>` 段按 canonical 类型引用编码，参数段只带调用点静态类型、返回段恒 `.any`）；
- `.generic.<Name>`、`.vargs.<Name>`、`.kwargs.<Name>` 采用第 7 节规定的名称；
- symbol 字符串存放在 `Resources` 中；
- 实际 hidden argument 值按方法规范签名传入。

proxy 模板体内 `invoke fn(..inner)` 的包透传（§15.4）与本节同源：可变泛型包以 `.generic.<Pack>` 前置操作数整体转发，值包以 `.vargs.<Name>` / `.kwargs.<Name>` 随显式实参转发；二者均由 Middleware 在烘焙下一环时消费（解包、shim、特化链接），frontend 不展开包元素。

---
