# Lowering与BIL发射

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](SEMANTIC_ARCHITECTURE.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 6. LoweredTree 与发射（P4）

### 6.1 P4a：降级重写（Lowerer）

树到树重写，输入无错 BoundTree，输出 LoweredTree。基类 `LoweredNode`，
回指字段 `Origin: BoundNode`（必填）。可以由多个顺序 rewriter 组成；
每个 rewriter 职责单一、可独立测试。

脱糖清单（对照 BIL §3.4，加上本项目的深度 lowering）：

| 源结构 | 降级形态 |
|---|---|
| 安全调用 `?.`、`if?` 空值回退 | nullable 检查 + 条件结构 |
| smart cast 标记 | 显式 `cast`（BIL §3.1） |
| source-level 子类型赋值 / 传参 | 显式 `cast`（BIL §6.5） |
| 内建 `bool` 短路 `and` / `or` | 条件结构 + 临时变量（BIL §11.3） |
| 复合赋值（`+=` 等 10 种） | 读取 + 基础运算 + 写回 |
| `seq` 与 `return@` | 结构化 block（region + `.breakid`）+ 结果临时变量 + `LoweredStructuredExit` 标记；P4a 末尾 StructuredExitRouting pass 展开为「写结果局部 + route 局部（i32，仅跨 region）+ break 当前 region」，需要 multiplex 的 region 后生成 dispatcher（读 route → relay 父 region） |
| pattern switch（含 `_` 分支） | 常量表 switch / 嵌套条件（BIL §16.6） |
| 解构声明 | 精确字段/索引读取 |
| `using` | 初始化 + 清理记录 + try/finally 路径（RUNTIME §25.1） |
| wrapper place 成员访问（`obj:W.f`、`obj:W.m()`） | **全部读取**统一值拷贝 + 普通指令：Entity = `get.wrapper` + `get.field`/`invoke`/`get.array`；字段-Value = `get.wrapper.field` + 普通指令；嵌套链逐层物化（BIL §12.4）。成员写（直接字段）= `set.wrapper.field`（Entity = `wrapper(W)`；字段-Value = `field(HOST_FIELD)+wrapper(W)`）；深层纯字段写穿 `place.a.b...` = P4a 多 get/set（正向 get + 叶写 + 反向 set；值类型中间写回，引用中间停止；最外层必要写回复用 `set.wrapper.field`，普通值中间反向写回仍发 `set.field`，**不新增**专用深写 opcode）；**局部/静态存储 = cell 根**（统一 cell 存储：读 = `get.wrapper.field $cell field(value) type(W)`，写 = `set.wrapper.field` 链 `field(value)+wrapper(W)`，复合赋值读写分离；静态字段值读写 = `get.field.static` 取 cell + getValue/setValue）；索引写仍归口 |
| 未声明方法的 wrapper 降级（SYNTAX §14.7） | `invoke core::Any$call???`（胖值 ABI；BIL §15.5） |
| 普通值类型中间链写穿（S1/g9，SYNTAX §10/§13.2） | 复用 wrapper 深写同一套 P4a 展开（正向 get + 叶写 + 反向 set；值类型中间写回、引用中间停止、不新增 opcode），链根泛化为任意可写 place（局部/参数/this）：字段链写 `r.origin.x = 7`、复合赋值 `r.origin.x += 1` 由 AssignmentRewriter/CompoundAssignmentRewriter 接管；值类型 receiver 方法调用（`r.origin.bumpX()`，含 struct 方法内 `this` 链）由 InstanceCallRewriter/CallStatementRewriter 物化 receiver 拷贝、调用后逐层写回可写 place（rvalue 根/只读中间不写回；写目标需写回但中间层 const/无 setter 时诊断） |
| 字符串插值 | 拼接/格式化调用链 |
| trailing lambda 等 | 规范调用形态 |
| async/await/yield | 直接发 BIL §17；状态机由 Middleware 降级（§7，专项设计） |
| 隐藏参数（`.generic.*` / `.vargs.*` / `.kwargs.*`） | 按 BIL §7 规范签名显式化 |

降级**不得改变** `SYNTAX.md` / `RUNTIME.md` 规定的可观察语义
（求值顺序、getter/setter/operator/wrapper 调用顺序、异常路径）。

> **route local 不做活跃性复用是有意设计（安全性取舍）**：
> StructuredExitRouting 为每个确实需要 multiplex non-local exit 的
> structural region 独立合成一个普通 i32 route 局部（`.sN`），进入
> region 前初始化 `0`，post-region dispatcher 之后其逻辑生命周期即
> 结束；route tag 各 region 私有、从 `1` 起编号、无函数级含义。
> **有意不做**跨 region 的活跃性分析与局部复用，理由：
> ① route 局部的全部含义只存在于「本 region 入口 → 本 region
> dispatcher」之间，复用必须证明两块 life range 不相交且 tag 命名
> 空间不混淆——而 region 结构会被后续 lowering 继续改变（using 包
> try、短路/安全访问合成 if），证明负担随之一再重估；一旦误判，
> 失败形态是 dispatcher 读到陈旧 tag、控制流走错目标，属于最恶劣
> 的静默语义错误。② BIL 的虚拟可变寄存器数量无限，多几个 `.sN`
> 对正确性与可读性零成本；`.vars` 体积与寄存器合并是 DCE /
> register allocation / coalescing 问题，按职责边界归 Middleware
> （BIL §23），frontend 不做。因此这不是技术债；改动此决策须重新
> 评估上述安全论证。
>
> 同 region 尾位 exit 省略冗余 break：exit 已处所属 region 末尾
> 位置（仅隔透明 LoweredBlock）时落尾与 break 落点完全相同，
> StructuredExitRouting 只写结果局部、不再发 break（tail-position
> 分析，isTail 随递归下传）；finally 块除外——finally 内 exit 必须
> 以 abrupt completion（break）覆盖 SavedCompletion，递归进
> FinallyBlock 时强制 isTail=false（落尾 Normal 会让 VM 恢复 body
> 的原 completion，语义错误）。

> **逃逸型/混合形态值块表达式的可用位置（现状记录）**：「体全路径
> 向外逃逸、自身不产值」的 seq/if/switch 表达式（逃逸型）与「产值
> 与逃逸并存」（混合型）现已端到端贯通（P3 定型取期望类型 +
> StructuredExitRouting 逃逸截断 + BIL §18.1 hint 供 verifier DA
> 分组）。可用位置：变量初始化、赋值右值、return@/return 值、调用
> 实参、条件位（while/do-while/if——条件恒 bool，以 bool 为期望类型
> 定型；逃逸条件的 judge 写回改写 false 字面量——写回是 loop 协议
> 结构部件必须存在、动态不可达故值任意；StructuredExitRouting 对
> Judge 块抑制逃逸 region 截断，写回不会被砍）。**仍拒绝的位置**
> （P3 诊断 `a type annotation is required`，清晰可操作）：for
> iterable 位、switch selector 位、二元运算操作数位、字符串插值段
> ——这些位置的类型取自表达式自身（鸡生蛋，无期望类型可传），解除
> 需要类型系统层的期望推导（如显式标注语法），属于类型推断的设计扩展方向而
> 非当前实现缺陷。

> **wrapper 烘焙的 pass 归属**：详见 §5.2
> 整段。摘要——P1 符号壳；P2 只形状校验 + 应用登记（Freeze 前零
> 合成符号）；P3 proxy 模板态绑定 + 使用点 place/降级判定；P4 发射
> 带 `wrapper-proxy(...)` 的模板 fn（`invoke fn(..inner)`/`get.self`）与
> place 读/写降级（上表）；**一切烘焙归 Middleware**（BIL §23）。

### 6.2 P4b：发射（BilEmitter）

LoweredTree → `BilModule` 的机械线性化。此时不再有任何语言级决策：

- 表达式树展平为指令序列，中间值物化为 `.vars` 临时变量
  （BIL §10.1：操作数只能是变量与符号表达式）；
- 字面量提取进 `Resources`，指令经 `load res(...)` 引用（BIL §4.2）；
- 控制流结构生成 block 与结构化指令（`if`/`loop`/`switch`/`try`，
  BIL §16），绝无任意跳转；
- 符号引用经 `CanonicalSymbolPrinter` 打印；
- `LocalSymbols` / `ExternalSymbols` 段按符号使用情况生成（§3）。

### 6.3 BIL 对象模型与工具层次（关注点分离）

```text
BilModule / BilFunction / BilBlock / Bil 指令 / BilResource …
    ├── BilWriter    （模型 → 标准 BIL 文本）
    ├── BilVerifier  （BIL §21）
    └── BilVm        （BIL §22）
```

- BIL 对象模型是**自足**的：不引用 BoundTree/LoweredTree/符号图的
  任何类型；`Origin: object?` 是**可空的纯调试附加值**，发射器通常写入 LoweredNode
  ——从 BIL 文本反序列化得到的模型 Origin 恒为 null，语义完全等价。
- 依赖方向单向：`P4b → BIL 模型`；verifier/VM 只依赖 BIL 模型，
  对中端一无所知。这保证 BIL 生态（writer/verifier/VM/
  文本 reader）可以独立于编译器演进，反之亦然。
- 完整调试链：`Bil指令.Origin → LoweredNode.Origin → BoundNode.Syntax
  → ASTNode.Span`，诊断与未来调试信息由此取得源位置。

---

### 7.1 wrapper 值语义与 BIL 形态

规范把 wrapper 定为默认非 rich、按需显式 rich 的值，`obj:Wrapper` 为**只读 place**
（SYNTAX §14.5/§14.9）。相关 BIL 缺口与归属如下：

- **§12.4 / §13.3 place 形态（读侧统一值拷贝）**：
  `get.wrapper` / `get.wrapper.field` 保留为 lowering/VM 内部能力，
  全部字段读经值拷贝后发普通 `get.field`；`set.wrapper.field` 为
  wrapper 隐藏存储写后门（非普通 `set.field`）。深层写穿**不新增**
  专用深写 opcode，由 P4a 展开为多个现有 get/set；最外层必要写回复用
  `set.wrapper.field`，普通值类型中间层反向写回仍发 `set.field`。
  源码层 `obj:W = ...` 仍是编译错误，BIL 不为整体赋值准备写入指令。
- **写链操作数**：两态 `field(F)|wrapper(W)`（不扩展新字节码类）；
  Entity 应用 = `wrapper(W)`；字段-Value 应用寻址 = 相邻
  `field(HOST_FIELD)+wrapper(W)`（HOST_FIELD 带 `wrapped(W)`）。
  隐藏存储由 Middleware 合成（命名约定 BIL §5.3；BIL 文本不再声明
  `.wrapper.` 隐藏字段——与 RUNTIME §14 一致）；隐藏存储不可用普通
  字段寻址。
- **统一 cell 存储**：lambda 捕获与局部/静态 Value wrapper
  共用同一机制——`core::Cell<T>`/`ReadonlyCell<T>` 为抽象基类；P3
  `CellClassFactory` 逐变量合成隐藏子类 `..cell..稳定摘要`（自持
  `pub value: T`，wrapper 应用以同 `WrapperApplication` 挂在该字段
  上，BIL 投影为字段 `wrapped(W)`）。局部/静态 place 以 **cell 根**
  分派：读 = `get.wrapper.field $cell field(value) type(W)`；写 =
  `set.wrapper.field` 链 `field(value)+wrapper(W)`；复合赋值读写分离；
  深写复用既有机制（cell 对象为终极宿主）。静态字段 BIL 声明
  类型投影为 cell 子类、字段槽不再投 `wrapped(W)`；cell 构造时机归
  Middleware（frontend 只生成与标注）。捕获侧 `.capture.*` 字段类型
  = cell 子类；已 cell 化变量按引用直接捕获不套第二层。P4a
  `ClosureStoragePlan` 通用化为 cell 存储计划；`CellStorageLowering`
  改写静态/全局 cell 读写；`WrapperPlaceLowering` 不含局部/静态处理。
- **proxy 模板占位指令**：模板 fn 体内 `inner` / `self` 分别
  发 `invoke fn(..inner)` / `get.self`；特化、inner 链接、原始体、router 体
  均不在 frontend 合成（§5.2）。
- **enum 判别**：§12.3 `type.is.case` + §8.5/§19.1 判别值资源与
  u16/u32 宽度规则仍有效。

其余机械同步：`.string` 归 ValueType 域（§6.2）、wrapper 默认非 rich，显式 rich 才具有 rich 值属性；声明还须满足
open/abstract/singleton 禁令及其余修饰符合法性条目（§8.2）。

---
