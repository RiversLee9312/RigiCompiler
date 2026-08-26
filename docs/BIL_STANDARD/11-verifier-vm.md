# BIL 验证器 / BIL VM 语义要求（§21–§22）

> 本文件是 [BIL_STANDARD.md](../BIL_STANDARD.md)（BIL 标准）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 21. BIL 验证器

验证器必须拒绝任何违反本节规则的 BIL。验证可以分阶段进行，但最终结果必须等价。

### 21.1 词法与语法验证

检查：

- 版本号受支持；
- 本地标识符字符合法，canonical symbol 符合第 5.2 节语法；
- 段结构合法；
- opcode 与操作数数量合法；
- 括号、数组和 block 结构闭合；
- 保留名称未被用户声明。

### 21.2 符号验证

检查：

- 所有 canonical 类型/字段/方法/case 符号以及 RESOURCE/BLOCK 引用可解析；
- canonical 限定正确；
- local symbol 不重复；
- external symbol 签名完整；
- 方法 body 与声明一一对应（`native` 声明除外：`native` 方法不得存在方法 body，且必须恰好各带一个 `symbol("...")` 与 `lib("...")` 修饰符）；
- `core::Any$call???` 为预定义内建方法符号（§15.5 / §22.5）——无 LocalSymbols/ExternalSymbols 声明、无 fn 定义，可被 `invoke` 引用；
- proxy 模板 fn（名以 `.proxy.` 开头，§5.1）必须声明在 wrapper 类型内，且与 `wrapper-proxy` 修饰符双向一致（见 §21.8）；
- `..init.wrapper`（§9.7）：每 owner 至多一个；返回 `.void`；实例方法；必须 `priv` + `compiler-generated`；
- `..init.field.<名>`（§9.7）：返回 `.void` 的零参实例方法；必须 `priv` + `compiler-generated`；与基类同族同名方法构成虚派发族（字段 override，见 `SYNTAX.md` §9.2.1）；
- `..globals.init`（§8.4.1 / `SYNTAX.md` §9.3）：编译器合成的全局/静态字段初始值 fn；返回 `.void`、无参数、无 `.this`；必须带 `compiler-generated`；VM 在 singleton 初始化之后、main 之前同步执行。源码层初值表达式不得直接引用其它全局/静态字段（frontend 编译期拒绝，`SYNTAX.md` §9.3）；函数调用属逃逸口，验证器不追踪；
- `..companion` 类型（§8.7）：必须是 `class` 且带 `singleton` + `shared`；类型名保留名（声明类的嵌套类）；
- 保留字段 `..value`（§5.1 / §8.3）不得作为用户 `.field` 声明出现；`get.field` / `set.field`（含 `.static` 变体）引用 `..value` 仅当当前 fn 带 `setter(F)` 且 owner/类型/static 匹配，或当前 fn 是 cell 隐藏子类的 `getValue` / `setValue`；之外拒绝；
- entrypoint 唯一且签名符合 `SYNTAX.md`。

### 21.3 类型验证

检查：

- 所有变量和资源有类型；
- 标准类型构造（`.array` / `.map` / `.pair` / `.nullable` / `.cell` / `.readonly_cell` / `.typeid` / `.fieldid` / `.generic`）实参可解析且元数合法；
- 指令源/目标类型严格满足规则；
- 不存在隐式数值提升或子类型赋值；
- 运算实现按精确类型唯一；
- 位运算（`bin.and`/`bin.or`/`bin.xor`/`bin.not`/`shift.*`）的内建标量操作数仅允许整数族（§11.4）；
- getter/setter/index 实现按精确类型唯一；
- direct invoke 签名完全匹配；`invoke.indirect` / `invoke.indirect.noret` 按 §15.3：`OBJECT_VAR` 静态类型恰有一个与实参/返回形态严格匹配的 `$$call`（含 async 时结果为 `Task\<TResult>` / `Task`；泛型 `$$call` 的 typeid/包前缀与 §15.1 同构校验）；
- cast 目标合法；
- new/init 和 enum case 签名合法；
- await/yield 类型合法；
- `get.self` / `invoke fn(..inner)` / `invoke.noret fn(..inner)` 仅出现在 proxy 模板 fn 内，且类型规则见 §12.5 / §15.4；
- `get.wrapper` / `get.wrapper.field` 类型规则见 §12.4（后者要求 HOST_FIELD 带 `wrapped(W)`、OBJECT 可赋值到字段 owner、RESULT = W）；
- `set.wrapper.field` 链元素合法（`field` / `wrapper` 两态；字段应用对 `field(HOST_FIELD)+wrapper(W)` 要求 HOST_FIELD 带 `wrapped(W)`；类型应用 `wrapper(W)` 要求当前位置类型声明带对应应用标记 §8.3.1；链必须含 wrapper 元素）；
- `new.wrapper.field` / `new.wrapper.method` / `new.wrapper.entity`（§14.5）仅允许出现在 `..init.wrapper` fn 体内；WRAPPER_TYPE 与 ARGS 匹配 wrapper init；field/method/entity 目标与 `wrapped(W)` 标记一致；
- `new.wrapped` / `new.wrapped.case`（§14.4）：第一实参表匹配目标类型 `..init.wrapper` 签名且该签名非空；第二实参表匹配 init / case；与普通 `new` / `new.case` 互斥（有参 `..init.wrapper` 必须用 wrapped 家族，否则禁止）。

### 21.4 definite assignment

检查：

- 参数入口已赋值；
- 普通局部变量在读取前已赋值；
- 所有结构化路径合并时满足读取条件；
- loop condition 在每次读取前由 judge block 赋值；
- 结果变量不会在失败路径上被错误认为已赋值。
- **loop.rev 出口**（与 §21.8 同口径）：体正常落到底与各 `break` 本 region
  出环点及 `continue` 至条件的交集——`break` 跳过的赋值不计入循环后；
  有 finally 时出环点叠 finally 赋值。正向 loop 出口仍取进入态（body 可能零次）。

分支合并口径：if 双分支独立分析、出口取交集；switch 恒执行且仅执行
一个分支（default 恒在），出口取全分支（含 default）交集（与 §21.8
「switch 全分支交」一致），分支臂内的写入对 switch 之后的读取可见。
该交集依赖不变量：发射器绝不在臂内 break 之后发射死代码（与 if 分支
同款既有依赖）——手写 BIL 若在臂内 break 后再写变量，DA 按本文件
「零误报优先、漏报可接受」口径漏报，属有意为之；若未来开放手写/
第三方 BIL 后端，须把该不变量升级为显式检查。

route dispatcher 分组（消费 §18.1 `rigi.seq-route` hint）：DA 到达经校验
的 hint 位置时（hint 紧随 if/switch/call blk/loop/loop.rev），为每条前
驱边维护独立已赋值集并标注组号（边 break 前 route 的最后常量写入值，
未写为 0）。尾链逐条分析：每条链节「cmp.eq route 常量C + if」把 C 组
边从流中剥离、其合并态（交集）作为跳转目标块的进入态，其余边沿 else
边进入下一链节；末链节的 none 落尾（0 组续点：region 块内后续指令或
region 正常结束回到 call blk 续点）仅由 0 组边的合并态流入。relay 目
标块因此只携带本组边的精确状态（逃逸臂上写入的外层结果局部沿 relay
链精确传播）；0 组为空时落尾边静态不可达。校验失败的 hint 被忽略时
退回保守全合并。

### 21.5 控制流验证

检查：

- 仅允许结构化 block 引用；
- 不存在 `jmp`；
- block 均在当前函数；
- entry block 不正常落到末尾；
- `ret` 类型正确；
- break/continue token 来源与作用域正确（token 可源自 loop/loop.rev/switch/call/if/try，见 §16.5）；
- continue 只引用 loop/loop.rev token（不引用 switch/call/if/try token）；
- catch/finally table 合法；
- 递归 block call 若被允许，必须能够由实现安全执行；实现可以选择拒绝无法证明有界的直接结构递归。

### 21.6 `.breakid` capability 验证

验证器必须追踪 BREAK_ID 的创建结构与作用域。

`.breakid`：

- 只能由 loop/loop.rev/switch/call/if/try 绑定（§16.5 推广的 region-exit capability）；
- 每次绑定产生唯一 token；
- 对应变量不得被二次普通赋值；
- 不得复制；
- 不得比较；
- 不得作为方法参数/返回值；
- 不得存入字段、数组、Any、Box 或资源。

### 21.7 泛型与参数包验证

检查：

- hidden argument 名称符合第 7 节；
- 顺序符合规范；
- fixed generic 参数数目正确；
- positional/named generic 包类型正确；
- vargs/kwargs 包类型正确；
- `.generic<...>` 引用的 typeid 位置可见且已赋值；
- 泛型约束在 frontend 输出中已满足；
- runtime 动态 new/is/supers/with 的边界合法。

### 21.8 可见性与类型属性验证

检查：

- `pub`、`protected`、`internal`、`priv` 访问合法；
- static/instance 指令形式正确；
- const 不被写入（构造期一次性赋值豁免——对齐 SYNTAX §9.3：init 方法体内写实例
  const 字段，以及编译器合成构造期写入方法族 `..init.wrapper` / `..init.field.*`
  体内写实例 const 字段均放行；静态字段写入仅编译器合成的 `..globals.init`
  豁免，其余静态写入不豁免）；
- abstract 不被构造；
- enum struct 不走普通 new；
- rich/shared 闭包与跨 Coroutine 规则合法（`.cell<T>` / `.readonly_cell<T>` 共享安全 passthrough：等同于 `T`，与 Box 同例，不按普通 class 闭包表）；
- async 调用的 receiver/参数/结果满足 shared 边界；
- `wrapper-proxy(PROXY_KIND)`（§8.4）只允许在 wrapper 类型内、名以 `.proxy.` 开头的方法上；此类方法必须带本修饰符；`PROXY_KIND` 仅 `specific` / `wildcard`，且与成员形状类别一致（specific ↔ 具名 proxy；wildcard ↔ `.*` 通配 proxy）；同一方法不得重复携带本修饰符；
- `wrapped(WRAPPER_TYPE_REF)`（§8.3.1）的 `WRAPPER_TYPE_REF` 必须是 wrapper 类型；可重复，顺序保留；
- `..init.wrapper` 声明与 fn 定义满足 §9.7（唯一性 / void / priv + compiler-generated / 非 static）；`..init.field.*` 与 `..globals.init` 同（§9.7 / §8.4.1 形状条款）；
- `..companion` 满足 §8.7（class + singleton + shared；壳体静态方法体形态由 frontend 保证，验证器检查 companion 类型结构）。
- enum struct 类型的实例字段：宿主类型的每个 init 必须在全部执行路径上对该字段发 `set.field`（先于任何读路径），否则拒绝模块（§14.3「enum 无零值」）。路径合并：if 双分支交、正向 loop 出口=进入态（body 可能零次）、`loop.rev` 出口=体正常落到底与各 `break` 本 region 出环点的交集（至少一次；`break` 跳过的 `set.field` 不计入）、switch 全分支交；`break` 是 region 机制（loop/if/switch/call/try 均可命中），出环点终止本块后续指令。有 finally 时体内 ret/throw/break 延后到 finally 之后才记离体（finally 赋值在 break 路径同样生效）。**例外（新 init 原则，§9.7）**：带声明初始值的字段——其写入点在 `..init.field.<名>`（由 `..init.wrapper` 在任何 init 体之前调用），对 init 体而言「进入时已赋值」，不计入本条的全路径写入与早读检查。

### 21.9 VM 可执行性验证

BIL VM 必须能够在不依赖 LLVM、Native ABI 和对象物理布局的情况下解释所有标准指令。

如果某个 BIL 扩展只能由特定 backend 执行而 VM 无法给出语义参考实现，则该扩展不得标记为标准 BIL 指令。

---

## 22. BIL VM 语义要求

### 22.1 抽象值模型

VM 可以使用 C# 对象、record、数组、字典或其他抽象数据结构表示 Rigi 值。

VM 不需要模拟：

- 128-bit 胖引用的位布局；
- 16 字节对齐；
- TypeSheet 内存结构；
- vtable/iMap offset；
- Box 裸数据块；
- ARC/GC 引用计数；
- LLVM calling convention。

### 22.2 必须一致的可观察行为

VM 与 Native 实现必须在以下方面一致：

- 返回值；
- 抛出的语言异常；
- 字段、数组和变量的可观察读写；
- getter/setter/operator/wrapper 的调用顺序；
- short-circuit 行为；
- try/catch/finally completion（含 `finally(e)`：进入 finally 时仅 Throw
  completion 把异常对象写入 EXCEPTION_VAR，Normal/Return/Break/Continue 等
  一切非 Throw completion 一律写 null，见 §16.7；以及 try BREAK_ID 的
  matching break 在 finally 完成后于 try 边界消费）；
- async eager spawn；
- await/yield 的逻辑状态变化；
- enum case 身份与 payload；
- typeOf/is/supers/with/cast 的结果；
- using 清理顺序。

### 22.3 运算实现查询

VM 必须使用与 Middleware 相同的确定键查询运算实现：

```text
opcode + exact operand type(s) + exact result type
```

对内建类型执行语言规定的 primitive 语义；对用户类型执行精确运算实现。VM 不执行 source-level overload ranking。

### 22.4 字段与索引

VM 必须把 `get.field`、`set.field`、`get.array`、`set.array` 视为独立语义操作，并根据精确类型与符号元数据执行 getter/setter/operator/wrapper 行为。字段读写顺序必须与 §13.3 一致：

- `get.field` / `set.field` 引用 `..value`：直接读写当前 setter 对应字段的 backing 存储——不查访问器、不绕 wrapper 链；setter 上下文之外出现 `..value` 必须拒绝；
- getter 体内对自身字段的 `get.field`：直读 backing，不绕 wrapper 链；
- 使用点写：`set.field F` → 字段带 wrapper 时先走 wrapper set 链（outer→inner），链末调用 setter（无 setter 时直写存储）；setter 体内 `..value` 直写 backing；
- 使用点读：`get.field F` → 先调 getter，返回结果再过 wrapper get 链（inner→outer）写目标槽；
- 构造期豁免：init 体内对带 wrapper 字段的写不绕 wrapper 链，但带 setter 时仍调 setter；
- wrapped cell 的使用点 `invoke` getValue/setValue：wrapper 链外置（写：链末调 setValue；读：getValue 返回后过 get 链）。

不得为了实现方便而在 BIL 语义层把它们改写成与规范不同的普通调用顺序。

### 22.5 native 函数的内建 hook

VM 执行到对 `native` 方法声明的 `invoke` / `invoke.noret` 时，不寻找方法 body，而是按 `(lib, symbol)` 查询内建 hook 表并执行对应的内建行为。标准内建 hook 表：

| lib / 键 | symbol | 参数 | 行为 |
|---|---|---|---|
| `rigi_rt` | `print` | `text: .string` | 将字符串写入标准输出 |
| `rigi_rt` | `printErr` | `text: .string` | 将字符串写入标准错误 |
| `rigi_rt` | `alloc_array` | 泛型 hidden `.typeid`（经 `.generic.T` 物化）+ `size: .i32` | 分配并返回元素零值初始化的 `.array<T>`；T 为 enum struct 按宿主错误（§14.3 无零值）。仅供 stdlib `arrayOf`/`arrayOfElements` 系列的私有 native 声明调用，用户代码不可直达 |
| `rigi_rt` | `make_sleep_alarm` | `milliseconds: .i64` | 创建并返回 `core.coroutine::EventAlarm`：基于单调时钟、到期转 ready 的粘滞事件 Alarm（`RUNTIME.md` §19.3/§19.4），配合 §17 `yield ALARM` 实现非阻塞睡眠。仅供 stdlib `sleep` 的私有 native 声明调用，用户代码不可直达 |
| `rigi_rt` | `any_to_string` | `value: .any` | 返回值的字符串表示（`SYNTAX.md` §3.8）：内建数值/`bool`/`char` 为标准文本；未覆写 `toString` 的对象为其类型 canonical 名。仅供 stdlib `.bootstrap.rg` 的私有 native 全局声明调用，用户代码不可直达 |
| `rigi_rt` | `i64_to_string` | `value: .i64` | 标量标准文本（StringOut）；`any_to_string` 的格式化底座 |
| `rigi_rt` | `f64_to_string` | `value: .f64` | 同上（Ryu 最短往返 + .NET 默认呈现） |
| `rigi_rt` | `f32_to_string` | `value: .f32` | 同上 |
| `rigi_rt` | `bool_to_string` | `value: .bool` | 同上（`true`/`false`） |
| `rigi_rt` | `char_to_string` | `value: .char` | 同上（UTF-16 码元文本） |
| （方法 hook） | `core::Any$call???` | 见 §15.5 胖值签名 | 按 `symbol` 路由 wrapper 请求；无路由命中抛 `core::NoSuchMethodException` |

`String` 的 `toString` 即值自身，不产生 native 调用。`toString` 成员方法（`core::Any$toString` / `core::Object$toString`）不再直接 hook：它们是 open 普通方法，默认实现体由编译器合成为「装箱接收者后 `invoke` `.bootstrap.rg` 的 `priv` 全局 native `any_to_string`」的小 fn——hook 经该全局函数触达；覆写了 `toString` 的类型经虚派发执行自身实现，不命中本表。`call???` 按方法符号命中本表（无 `(lib, symbol)` 对），无 BIL fn 定义。命中表之外的 `(lib, symbol)` 组合 VM 无法解释，必须拒绝执行并报错。该表只随 BIL 标准修订扩充；Middleware 的原生链接不受此表约束。

---
