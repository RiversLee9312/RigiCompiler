# BIL VM 设计

> 依据：`BIL_STANDARD.md` §21.9 / §22（VM 可执行性与语义要求）、`RUNTIME.md`
> §17–§19（Coroutine / Executor / Task / Alarm）、`../../legacy/SEMANTIC_ROADMAP.md` §S14。
> 定位：**行为参考实现**。优先级：可维护性 > 代码易读性 > 行为正确性；
> 明确不以性能为目标。Native 实现（Middleware/LLVM）与本 VM 必须在
> §22.2 列出的全部可观察行为上一致。

## 1. 裁决与边界

本 VM 是 BIL 文本/模型的抽象解释器：

- 不模拟 §22.1 列出的任何物理机制（胖引用位布局、对齐、Box 裸块、ARC/GC、
  LLVM calling convention）；TypeSheet/vtable/iMap 不模拟其**物理位布局**，
  但方法派发统一建立在逻辑等价的 VmTypeSheet（拍平 vtable+iMap）抽象上
  （见 §3.4）。
- 不做 source-level overload ranking（§22.3）；运算实现查询键恒为
  `opcode + 精确操作数类型 + 精确结果类型`。
- 不把 `get.field` / `set.field` / `get.array` / `set.array` 改写为普通调用
  （§22.4）；它们是独立语义操作，按精确类型与符号元数据执行
  getter/setter/operator/wrapper 行为。
- native 调用只经 §22.5 内建 hook 表执行；表外 `(lib, symbol)` 拒绝执行并报错。
- `hint`（§18）恒为 no-op。

与 §22.5 hook 表的接线方式：hook 点是 **native 函数声明层**（被
`@NativeLibrary`/`@NativeSymbol` 标注的声明，如 `core.io::Console.print`），
不是 Rigi 层包装函数。`Console.println` 等 Rigi 层包装走正常 BIL 解释路径，
只有真正 native 的那一次 `invoke` 命中 hook 表。

## 2. 目录与文件布局

```
Bil/
├── BilVm.cs                      # 入口：装载 BilModule、建 hook 表、启动 main、quiescence 屏障
├── BilComputeInstructions.cs     # 既有家族文件，指令类上新增 Execute（见 §5）
├── BilDataInstructions.cs        # 同上；indirect 缺失形态的模型也在此补齐（见 §6）
├── BilControlFlowInstructions.cs # 同上
├── BilCoroutineInstructions.cs   # 同上
└── Vm/
    ├── VmContext.cs              # 执行期上下文：模块、类型解析、静态字段存储、hook 表、stdout/stderr 汇
    ├── VmExecutor.cs             # 多 Worker Executor，包装 System.Threading.ThreadPool
    ├── VmCoroutine.cs            # Coroutine：调用帧链 + 块执行栈 + 逻辑状态机
    ├── VmTask.cs                 # Task 句柄：终态 + waiter 列表（shared 内建对象）
    ├── VmAlarm.cs                # PollingAlarm / EventAlarm 的 VM 表示与注册
    ├── VmException.cs            # 语言级异常的 VM 承载（包装异常对象 VmValue）
    ├── VmHooks.cs                # §22.5 hook 表：rigi_rt print/printErr/any_to_string + core::Any$call???
    ├── VmTypeSheet.cs            # 逻辑 TypeSheet：拍平 vtable+iMap 与统一方法派发（§3.4）
    └── Values/
        ├── VmValue.cs            # 抽象基类 + 精确标量子类型（见 §3）
        ├── VmObject.cs           # 引用类型实例：运行时类型引用 + 字段字典
        ├── VmEnum.cs             # enum case 身份 + payload
        ├── VmArray.cs            # .array<T>
        ├── VmTypeId.cs           # typeid 值 = 类型符号引用（.generic<T> 同构）
        └── VmFieldId.cs          # fieldid 值 = 字段符号引用
```

`Bil/Vm/` 对中端（Semantic/Lowering）零依赖，与 Bil/ 既有纪律一致：BIL 生态
自洽，输入仅为 `BilModule`。

## 3. 值模型（§22.1）

### 3.1 精确标量，独立表示

每种内建精确类型有独立的 `VmValue` 子类型，**不允许**统一装箱为
`long`/`double` 而丢失精确类型——§22.3 的运算查询键要求精确类型可分辨：

| BIL 类型 | VM 表示 |
|---|---|
| `.i8` `.i16` `.i32` `.i64` | `VmI8(sbyte)` `VmI16(short)` `VmI32(int)` `VmI64(long)` |
| `.u8` `.u16` `.u32` `.u64` | `VmU8(byte)` `VmU16(ushort)` `VmU32(uint)` `VmU64(ulong)` |
| `.f32` / `.f64` | `VmF32(float)` / `VmF64(double)` |
| `.bool` / `.char` | `VmBool(bool)` / `VmChar(char)` |
| `.string` | `VmString(string)`；非 rich 值类型，赋值/传参按值语义理解（§6.2），C# string 不可变性天然满足 |
| `.void` | `VmVoid.Instance` 单例，仅作占位 |

### 3.2 复合与运行时类型值

- `VmObject`：运行时类型引用（指向 BilModule 中的类型声明，canonical 符号名
  为身份）+ 字段字典（字段符号 → VmValue）。class 为引用语义。
- 值类型（struct / 内建值类型）：赋值、传参、返回时**深拷贝**；`VmObject`
  对 struct 实例同样适用但拷贝语义不同，由类型声明的 rich/struct 属性区分。
- `VmEnum`：case 符号身份 + payload 值；身份比较按符号引用相等。
- `VmArray`：元素精确类型 + `VmValue[]`。
- `.any` 胖值：`VmAny(VmTypeId typeid, VmValue payload)`，自描述。
- `VmTypeId`：**typeid 即类型符号引用**；`.typeid<TBound>` 与 `.generic<...>`
  的运行时值都是它（`.generic<T>` 物化后就是 typeid）。`getid.type` /
  `getid.var` / `getid.field` 产生对应符号引用值。
- `.nullable<T>`：引用类型的 null 用 `VmNull.Instance`；值类型 nullable 用
  存在位包装。`VmBox<T>` 槽（`.cell<T>` / `.readonly_cell<T>`）按 Box 语义共享。
- `.breakid`：结构化控制 capability，VM 内为指向目标块执行帧的不透明句柄。

### 3.3 运算实现查询（§22.3）

- 操作数为内建精确类型 → 语言规定的 primitive 语义（在家族基类中按
  精确类型分派，见 §5）。
- 操作数为用户类型 → 按精确类型解析到对应 operator fn，以普通调用语义执行
  （复用调用基建，不改变指令语义）。

### 3.4 方法派发：逻辑 TypeSheet（拍平 vtable + iMap）

方法派发（虚方法 / 接口方法 / callable `$$call` / `fn(..super)`）统一建立在
`Bil/Vm/VmTypeSheet.cs` 的逻辑 TypeSheet 抽象上——`RUNTIME.md` §6–§9 的
加载期拍平等价物：

- 每个类型声明一张 sheet（按声明 key 记忆化，可重入锁保护，多 Worker 并发
  构建）；槽序 = 继承槽 → 自有槽 → 各接口段。克隆式构建天然保证「继承中
  相同方法保持相同 vtable offset」（§7 不变量），派生 sheet 自带继承槽与
  iMap（§9 拍平，调用期不上溯）。
- 槽按归一化签名键匹配（名称 + 参数类型序列 + 返回类型；泛型参数按出现序
  归一为位置占位，参数名不参与）；有体成员替换全部同签名槽的实现（含继承
  来的接口段槽）。abstract/init/ext 成员与除 `$$call` 外的运算符不进 vtable
  （callable 协议例外，SYNTAX §9.2.1）。泛型基类/接口（`D : B<i32>`、
  `C : IFace<i32>`）的槽匹配按 extends/implements 实参代入——克隆基类槽
  与构建接口段时把槽的签名源按 `{T_i → arg_i}` 代入再重新归一化（
  `m(x:#0)` → `m(x:.i32)`；转发形态 `D<T2> : B<T2>` 代入后归一化不变），
  子类 `override m(x: i32)` 因而同 key 替换而非追加新槽；`SlotSymbol`/
  `ImplSymbol` 与 `OffsetBySymbol` 键保持声明级原样。多级链每跳各按直接
  extends 构造代入（`E : D<i32>` ← `D<X> : B<X>` 逐跳 T→X→i32）。
- iMap（`InterfaceBase`）记录接口声明 key → 接口段基址；接口派发 = 段基址 +
  接口内相对 offset（§8）。owner 是预定义根（core::Exception 等不进符号段）
  时按签名在 receiver 实际类型槽防御扫描（getMessage 多态路径）。
- 统一入口 `VmContext.ResolveDispatch(staticSymbol, receiver)`：静态符号经
  owner 声明 sheet 换算 offset，再取 receiver 实际类型 sheet 的槽实现 fn；
  不可派发（无 receiver/static/全局符号）退回直查。`invoke.indirect` 的
  `$$call` 同样按 receiver 实际类型的 sheet 槽匹配实参后经 `InvokeValues`
  落地（保持 Method wrapper 链与 receiver 首参 ABI）。
- `fn(..super)`（BIL §15.5）在直接基类 sheet 上按同签名槽取实现后**直接压帧**
  ——super 非虚、绕过 wrapper 派发链与二次派发；init 内 super 按基类 init
  重载选目标（实参剥 `$.this` 与隐藏泛型前缀后比对）。基类存在同名不同签名
  重载时的精确选择需要调用点静态类型（BIL 不泄露 base canonical 名），VM 以
  同签名优先、唯一按实参个数匹配兜底。

## 4. 执行模型（RUNTIME §17–§19）

### 4.1 显式 Step 循环，真并发 Executor

执行器是**显式 Step 循环**（模拟 CPU），不是递归解释：

- `VmCoroutine` 持有：调用帧链（函数调用栈，帧 = 局部变量槽 + 返回点）+
  块执行栈（结构化 region 的执行状态：指令游标、loop 迭代状态、try 处理表）
  + 逻辑状态（`Created/Runnable/Running/Suspended/终止态`，原子转换，§17）。
  栈是堆分配的数据结构，挂起**不需要快照任何东西**。
- `VmExecutor` 包装 `System.Threading.ThreadPool`（**真实多 Worker**，方案 A）：
  工作项 = 一个 Coroutine 的一段执行。Worker 取到后跑 Step 循环直到该
  Coroutine：`ret`/未捕获异常（终态）、`await` 未完成 Task（登记 waiter，
  转 Suspended，Worker 归还，§18.3）、`yield`（转 Runnable 重新发布，§19）、
  `await` Alarm（登记 Alarm 挂起，ready 后重新发布）。
- async `invoke` 严格按 §18.1 eager spawn：求实参 → 建 Coroutine/Task →
  绑定 Executor（首版唯一默认 Executor，字段保留 `BoundExecutor`）→
  转 Runnable 发布 → 返回 Task。**新 Coroutine 可能在 invoke 返回前已被
  另一 Worker 取走**——不以任何全局锁串行化执行。

### 4.2 同步与 happens-before

- `VmTask` 的终态转换与 waiter 列表用同一把锁保护；锁的释放/获取天然建立
  §18.3 要求的「终止前写入对 await 返回后可见」。
- **协程执行单所有者**：`Execute` 的「`Runnable→Running` 转换 +
  `SettleAfterResume` + Step 循环」整体在该协程的 `SyncRoot` 锁内；锁外
  只做状态发布（CAS + 入队）。handoff 因此被串行化——持锁 worker 的循环
  退出条件 `State != Running` 只可能由它自己的挂起动作造成（唯一能置
  Running 的转换在锁内），结构上消灭「旧 worker 尚未退出、新 worker 已
  接手」的双执行窗口。`lock` 同线程可重入，Step 内的嵌套 Step 循环
  （isReady 探测、singleton init、proxy 链同步推进）自然安全。
- 锁序单向无环：协程锁内可取 `VmTask._gate`（`TryAwait`/`Observe`）与
  executor 的 `_liveLock`（`NotifyTerminal`）；反向唤醒路径（task/alarm
  完成线程的 `ResumeWaiters→PublishWakeup→Publish`、eager spawn 的
  `Publish(child)`）只 CAS + 入队，不取任何协程锁。
- 唤醒发布统一走 `PublishWakeup`（携带唤醒纪元）：全部挂起点经 `TrySuspend`
  原子递增纪元，唤醒方（Task/EventAlarm waiter、轮询 timer）登记时捕获；
  发布失败且协程仍 Suspended、纪元未变 ⇒ 判定调度器丢失唤醒，立即 `Fail`
  留证（否则协程永久 Suspended、quiescence 死锁且无证据）；其余失败均为
  benign 竞态（stale 唤醒 / 取消 / 已就绪），静默容忍。
- 静态字段存储、hook 表 stdout/stderr 写入各自加锁；单次 `print` 调用原子。
- 原子性契约仅到「单次 native print 调用」为止：需要行级原子的包装
  （如 stdlib `Console.println`）必须在 Rigi 层先拼好整行、只发一次
  native print——两次 print 之间 VM 不提供任何不交错保证。另注：VM
  调度是确定性的（eager spawn、无挂起点即跑完），测试无法真实触发
  交错，故行原子性的回归防护以「lowering 后只含一次 native print」
  的 BIL 形状断言为主、双协程实跑「每行完整」为辅。
- 跨协程的输出交错顺序是真实非确定性，VM 不做任何排序保证。

### 4.3 try/throw/using

- `throw` 抛出 `VmException`（包装语言级异常对象），沿块执行栈与调用帧链
  逐层展开，按 catch-table 资源匹配、执行 finally。
- `using` 清理已由 P4a 编织为 try/finally 形态，VM 只需正确实现
  try completion 语义，using 清理顺序（§22.2）自然成立。

## 5. 指令分发：家族基类 + Execute

每个指令子类实现 `Execute(VmContext, VmCoroutine)`；**一类指令共用一个基类、
同住一个既有家族文件**（BilComputeInstructions.cs 等），家族基类承载同类
指令的公共基建：

- 操作数解析（`$var` 读写当前帧变量槽、`res(...)` 从模块资源装载字面量/
  switch-table/catch-table、`fn(...)`/`type(...)`/`field(...)`/`case(...)`
  符号解析、indirect 解引用）。
- 结果写回当前帧变量槽。
- 二元/一元内建运算基类：取操作数 → 按精确类型 primitive 分派（§3.3）→ 写回。
- 字段访问基类：静态目标解析 + getter/setter/wrapper 行为序列（§22.4）；
  indirect 形态只多一步 fieldid/typeid 值解引用，其余共用。

不引入 partial class，不出现跨文件巨型状态机；BilWriter 纪律（无 opcode
switch）在 VM 侧同样保持——分发靠虚方法，不靠中央 switch。

## 6. indirect 系列：Bil 模型补全 + 提前实现

indirect 是表达正常 Rigi 程序（泛型、lambda、运行时类型驱动访问）的必需品，
**先于控制流全家实现**。规范已定义但 Bil 模型缺失的形态（实现时一并补齐
模型 + BilSpellings + BilWriter + BilVerifier 校验）：

| 指令 | 规范位置 | 模型现状 |
|---|---|---|
| `type.is.indirect` / `type.supers.indirect` / `type.with.indirect` | §12.3 | ✅ `IndirectTypeCheckInstruction` |
| `invoke.indirect` / `invoke.indirect.noret` | §15.3 | ✅ 已有（lambda `$$call` 虚调用） |
| `cast.indirect` / `cast.safe.indirect` | §12.2 | ❌ 待补（TYPEID_VAR 解引用 + `.typeid<TBound>` 边界） |
| `get.wrapper.indirect` | §12.4 | ❌ 待补 |
| `getid.field` | §12.6 | ❌ 待补（新增 `GetIdFieldInstruction`） |
| `get.field.indirect` / `set.field.indirect` | §13.5 | ❌ 待补（FIELDID_VAR 解引用） |
| `get.field.static.indirect` / `set.field.static.indirect` | §13.5 | ❌ 待补（TYPEID_VAR + FIELDID_VAR） |
| `new.indirect` | §14 | ❌ 待补（TYPEID_VAR 目标构造） |

待补形态同步落 BilVerifier 规则（§21 逐指令校验扩展），保证
`--emit-bil` 后置自检对新形态仍然完备。

## 7. native hook 表（§22.5）

| (lib, symbol) | 行为 |
|---|---|
| `rigi_rt` / `print` | 写 stdout（加锁，单次调用原子） |
| `rigi_rt` / `printErr` | 写 stderr（同上） |
| `rigi_rt` / `any_to_string` | §3.8 标准文本；未覆写者为 canonical 类型名（toString 成员方法不再直接 hook——其默认实现是编译器合成 fn，经 .bootstrap.rg 的 priv 全局 native `any_to_string` 触达本 hook） |
| `rigi_rt` / `alloc_array` | 零值初始化 `.array<T>`；T 为 enum struct 按宿主错误（§14.3） |
| `rigi_rt` / `make_sleep_alarm` | 粘滞 EventAlarm，单调时钟到期 signal（RUNTIME §19.4） |
| （方法 hook）`core::Any$call???` | 按 symbol 路由；无路由抛 `core::NoSuchMethodException` |

表外 `(lib, symbol)` 拒绝执行并报错；表只随 BIL 标准修订扩充
（`alloc_array` 与 `make_sleep_alarm` 即两次修订扩充，均经用户裁定）。
GC 类设施（GCAlarm 等）永不进表：BIL 禁止对 GC 机制与实现作任何假设
（§1.1/§22.1），其为 Middleware 内部细节。

## 8. 测试策略

以**执行断言**测试形态（`../../legacy/SEMANTIC_ROADMAP.md` §S14）：跑出结果/异常与预期比对，
持续验证 §21.9「VM 可执行性」。

基建：`Tests/` 新增 BilVm 测试套件（登记 TestRunner）；harness 提供
「源码 → 编译 → BIL → 运行」端到端 helper，捕获 stdout/stderr/异常。

**时序不变性纪律**（真并发 Executor 的必然要求）：

- 断言必须是时序不变量：单协程程序可断言 stdout 全文；多协程程序用
  `await` 建立同步点后断言最终状态。
- 并发 print 只允许断言「每行完整出现」（行级原子），禁止断言行序。
- `BilVm.Run(module)` 返回前等待 quiescence（全部协程达终态），测试在
  屏障后断言。

## 9. 实施切片

1. **V1 骨架 + hello world**：值模型、VmContext、Executor/Coroutine 基建、
   `load`/`get.var`/`set.var`/内建运算/`invoke`/`ret`、`hint` no-op、
   rigi_rt hook → hello world 端到端真实 stdout。
2. **V2 对象与数据**：`new`/`new.case`/`new.wrapped*`、`get/set.field(.static)`、
   `get/set.array`、enum 身份与 payload、`get.self`。
3. **V3 indirect 全家 + 运行时类型**：按 §6 补齐 Bil 模型缺失形态 +
   verifier；VM 实现 `cast(.safe)(.indirect)`、`type.*(.indirect)`、
   `get.wrapper(.field)(.indirect)`、`getid.*`、`new.indirect`、
   `invoke.indirect(.noret)`。
4. **V4 控制流全家**：`call blk`、`if`、`loop`/`loop.rev`、`break`/`continue`
   （`.breakid`）、`switch`、`try`、`throw`。
5. **V5 协程**：eager spawn 全语义、`await`、`yield`（裸/PollingAlarm/
   EventAlarm）、Task 终态传播（成功/异常/取消的 VM 内部分）、quiescence。

## 10. 明确不做

- 不模拟 §22.1 列出的物理机制。
- 不做性能优化（无指令缓存、无内联缓存、无特化）；可读性优先。
- 不实现 GC/ARC；VM 值的生命周期托管给 .NET GC。
- 不扩展 §22.5 hook 表（除非 BIL 标准修订）。
- 不为 VM 改变 BIL 指令语义；发现规范歧义时先修规范或提决议，不在
  VM 内私自解释。
