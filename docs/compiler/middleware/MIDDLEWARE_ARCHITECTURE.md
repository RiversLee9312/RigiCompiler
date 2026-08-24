# Rigi Middleware 架构（BIL → 原生可执行）

> **状态**: 架构定稿
> **定位**: 本文档规定 BIL → 原生可执行文件之间全部阶段（下称 **Middleware**）的架构：
> 技术选型、内部层次、内存管理、实现绑定、wrapper 烘焙、协程降级、异常机制、
> ABI 与布局、优化归属、验证方式与阶段划分。
>
> **文档分工**：BIL 编码与验证规则以 `BIL_STANDARD.md` 为准（其 §23 是本文档的
> 边界契约）；运行时可观察行为以 `RUNTIME.md` 为准；源语言合法性以 `SYNTAX.md`
> 为准。本文档只规定 Middleware **内部**如何组织，不重新定义上述三者的语义。

---

## 1. 定位与总体管线

Middleware 的输入是符合 `BIL_STANDARD.md` 的 BIL 模块，输出是经 LLVM 工具链与
`rigi_rt` 静态库链接的原生可执行文件。Middleware 与 `rigi_rt` 均为**本仓库**的
顶层组成部分。

```text
BIL 文本
    ↓ BilReader（复用 Bil/ 生态）
BilModule（BIL 内存对象模型）
    ↓ BilVerifier 门禁（BIL §23：不得接受类型非法的 BIL）
    ↓ MW1 编译单元与符号表
    ↓ MW2 实现绑定（类型驱动操作的唯一实现查询）
    ↓ MW3 MIR 构造（结构化块 → CFG）
    ↓ MW4 MIR pass 群（wrapper 烘焙 / ARC 注入 / 协程状态机 / cell 消除 / devirt）
    ↓ MW5 布局与 ABI（TypeSheet/vtable/iMap/refMap 发射计划、字段偏移、调用约定）
    ↓ MW6 LLVM 模块构建（LLVMSharp 进程内）
    ↓ 进程内 verify / 优化 / 目标文件发射；lld（外部进程）链接 rigi_rt（C 静态库）
原生可执行文件
```

核心决策（修改须重新过一遍取舍）：

1. **实现语言 C#（目标平台 .NET 10 LTS），复用 `Bil/` 生态**。Reader、对象模型、
   Verifier 已在仓库内且对中端零依赖；Middleware 是唯一新增依赖 `Bil/` 的组件。
   保持纯 BCL、无第三方依赖的仓库纪律。
2. **经 LLVMSharp 进程内构建 LLVM 模块，锁定 LLVM 20**。绑定层最新跟随上游
   20.1.x，libLLVM 原生库经 NuGet runtime 包按 RID 分发（win-x64/linux-x64
   均有），免工具链安装；校验、新 PM 优化管线、目标文件发射全部进程内完成，
   唯一保留的外部进程是 lld 链接。.ll 文本仍由 PrintModule 产出，作调试与黄金
   快照产物。代价：绑定滞后上游（当前 22）——以锁版换绑定可用性；NativeAOT
   发布形态为「AOT exe + libLLVM 边车」。
3. **语言语义 pass 自做，通用优化全交 LLVM**。类型驱动操作绑定、wrapper 烘焙、
   ARC 注入、协程状态机、布局与 ABI 决策在 MIR 层完成；SSA 提升、常量传播、
   GVN、LICM、内联、向量化交给 LLVM 新 PM 管线。参照 Swift SIL / Rust MIR 的
   归属边界。
4. **无 GC roots、无 shadow stack、无 LLVM statepoint**。内存安全完全由编译器
   生成的 ARC acquire/release 调用保证；macroGC 是候选驱动的循环收集器
   （Bacon-Rajan），从 release 路径登记的候选出发做图染色，从不枚举栈/全局根
   （§4）。
5. **运行时逻辑沉在 C 静态库 `rigi_rt`**：ARC、macroGC、协程/Executor/Alarm、
   native shim。Middleware 生成的 .ll 只发射对这些运行时面的调用。

---

## 2. 外部依赖与选型裁决

| 部件 | 选型 | 理由 / 备选 |
|---|---|---|
| LLVM 集成 | **LLVMSharp 进程内**（绑定 20.1.x + libLLVM 20 NuGet runtime 包）；**锁定 LLVM 20** | 无 GC 设施需求使 C API 天花板不咬人（§4.1）；绑定与原生包同大版本对齐。Ubiquity.NET 排除（仅 win-x64）。注：runtime 包仅含 libLLVM 共享库；无 RID 的 `dotnet build`/`dotnet run` 开发回路需显式引用 runtime 包（csproj 已办） |
| 链接器 | **lld**，外部进程；获取链 MW1 定稿（NuGet 无 lld 分发已核实，候选：LLVM 官方 release 二进制随仓库工具链按 RID 分发） | 编译产物为 .o；链接是唯一保留的外部步骤；届时同步 `.github/workflows/ci.yml` |
| GC 引擎底座 | 教学级 Bacon-Rajan C 模板改造 | 候选底座 `rjungemann/turmeric` gc.c（MIT，纯 C、可剥离）；教学参照 `fitzgen/bacon-rajan-cc`（Rust，注释最全）；语义对照 Nim `lib/system/orc.nim`（位打包、rootIdx、自适应阈值）。论文并发版（Red/Orange/transfer buffer）无限期推迟 |
| 分配器 | 首版用 CRT malloc；mimalloc（MIT）为后续可选替换 | GC 主堆自研；分配器层与 GC 解耦，可后换 |
| rigi_rt 编译 | **clang 现场编译**（MW1 起；驱动定位/发现 clang 的获取链与 lld 同批定稿，NuGet 无 clang 编译器分发已核实；CI 需安装 clang，届时同步 `.github/workflows/ci.yml`） | 预编译 .lib/.a 入库排除（双平台二进制漂移与审查成本）；源码即真相，与本仓库同纪律 |
| 事件/定时底座 | **libuv**（MIT，静态链接） | 跨平台事件循环 + 定时器 + 线程池 + 同步原语一体；win-x64（IOCP）/linux-x64（epoll）均一等公民；每 Worker 一个 loop，EventAlarm/sleep 以其为底座；Worker 唤醒走 `uv_async_send` |
| 协程降级 | **自做状态机**，不用 `llvm.coro.*` | llvm.coro 跨版本 ABI 不保证兼容；frame 内精确根映射不可控（Rust 弃用先例）；RUNTIME §21 要求精确活跃引用映射 |
| native FFI | 编译期直接生成调用，**不用 libffi** | ABI 编译期已知；libffi 只服务运行时动态签名场景 |
| 可嵌入 GC 库 | **不采用** Boehm / MPS | Boehm 保守、非移动、位图粒度粗；MPS 重量、学习曲线陡；均不匹配「编译器握全 refMap」的精确模型 |

---

## 3. 内部层次与职责

### MW1 符号与类型表

BIL canonical symbol → Middleware 符号图。登记类型、成员、泛型具化请求、
wrapper 应用标记与资源表。符号**驻留**（引用相等即身份相等），与中端符号图
同一纪律。

### MW2 实现绑定（ImplBinding）

BIL §3.2 类型驱动操作的**唯一实现查询**：操作类别 + 操作数严格类型 + 结果严格
类型 + 已解析符号身份 → 唯一实现形态：

- primitive 运算 → LLVM 指令选择；
- 用户 operator/getter/setter/索引 → 精确目标 fn（含 vtable 槽选择、interface
  派发）；
- 可静态消除者（不可观察 copy/Box/temp）→ 标记消除。

不重跑 source-level overload ranking（§3.3）；查询不含隐式转换、候选排序或
最佳匹配。devirtualization 所需的 vtable/iMap 知识在此层沉淀。

### MW3 MIR 构造

BIL 结构化块 → CFG 基本块。输入已结构化，输出天然 reducible CFG：**确定性直译，
无需 Relooper/Stackifier**。具名局部变量 → alloca 槽；SSA 提升交给 LLVM
mem2reg。MIR 保留 BIL 类型与符号身份，直到 MW6 发射前不做类型擦除。BIL §16.7
try 的 catch-table 形式在本层展开为 EH 边与 pad 块（机制见 §8）。

### MW4 MIR pass 群

语言语义 pass 全在此层（§9 归属表），pass 之间以 MIR 为唯一交换物。关键 pass：

- **WrapperBaking**（§5）
- **RcInjection**（§4.2，安全攸关）
- **CoroutineSplit**（§6）
- **CellElim**：`.cell<T>`/`.readonly_cell<T>` 特权拼写识别（BIL §6 约定），
  消除可证的 cell 间接
- **Devirt**：静态可证的虚调用 → 直接调用

### MW5 布局与 ABI

字段偏移、对齐、Box 物理形态、TypeSheet/vtable/iMap/refMap 的发射计划、调用
约定（typeid 隐藏参数位置、胖引用传参与返回、sret、vargs/kwargs 包形态）。
布局决策在此定稿，MW6 只消费。

### MW6 LLVM 模块构建与发射

MIR + 布局计划 → LLVM 模块（LLVMSharp 进程内构建）。进程内完成
`LLVMVerifyModule` 校验、新 PM 字符串管线优化（`LLVMRunPasses`）与目标文件
发射；.ll 文本经 PrintModule 产出，作调试与黄金快照产物。外部进程只剩 lld
链接。

---

## 4. 内存管理架构

### 4.1 原则：编译器 ARC 即全部内存安全

Rigi 运行时不存在传统意义的 GC roots：

- 局部变量、参数、临时值中的引用由 RcInjection 生成的 acquire/release 调用精确
  计数（Nim 生产验证的模型）；
- 协程 frame 是堆对象，被 Task/Coroutine/Executor 结构引用，其内部引用槽按
  refMap 计数（RUNTIME §21 的「ARC root slot」）；
- 静态字段是静态存储槽，写入路径同样生成 acquire/release；
- macroGC 只回答「候选闭包是否自我循环维持」——外部活跃性由 RC 计数本身表达
  （Bacon-Rajan 的固有性质），不需要根枚举。

因此 LLVM IR 中**不需要** statepoint / stackmap / shadow-stack 任何 GC 设施。
这也让 LLVM-C API 不提供 GC 设施构造入口的天花板对本项目不构成约束（§2 的
进程内绑定因此成立）。

### 4.2 RcInjection（安全攸关 pass）

对每个函数：

1. 识别所有托管引用槽（含 rich ValueType/Box 的内部引用字段，按 refMap 展开）；
2. 在引用建立/覆盖/销毁点插入 acquire/release 调用（运行时面见 §4.8）；
3. **acquire/release region 划分**：复合引用操作（如 rich 值整体复制）合并为同
   一 region；region 边界发射 RUNTIME §23.3 的 cFlag 进入/退出协议。region 是
   编译器生成的短代码，**内部不得含挂起点，也不得含可抛出 Rigi 异常的调用**
   （macroGC 排空的有界性与「unwind 不穿越 region」依赖此不变量，RUNTIME
   §23.2；运行时调用自身不抛异常）；
4. 异常与提前返回路径的 release 配对（所有 CFG 出口等价覆盖，含 §8 的 unwind
   边）。

正确性攸关：漏一对 acquire/release 即 UAF 或泄漏。验证见 §10。

第一版不做 last-use/move 优化（保守全计数）；move 语义与 cursor 式非拥有引用
作为后续优化项，pass 架构预留。

### 4.3 两级 ARC

- **microGC**（local Object）：非原子计数。同一 Coroutine 任意时刻最多一个
  Worker 执行（RUNTIME §22.1），串行性由语言模型保证。
- **microSGC**（shared Object）：**原子计数**。跨协程并发 acquire/release 同一
  shared 对象是合法程序，生命周期元数据必须自洽——`shared` 不担保的只是用户
  字段的线程安全（RUNTIME §21 末条）。

### 4.4 胖引用写非原子化（RUNTIME §3）

RUNTIME §3：胖引用槽读写**非原子**；多线程并发竞争同一引用槽属于用户数据竞争，
行为未定义（允许撕裂）。16 字节对齐因布局需要保留。LLVM 侧不依赖
`atomic i128` 的 target-feature 降级行为。

### 4.5 macroGC：全局候选循环收集

- 算法：Bacon-Rajan 同步三阶段（markGray / scan / collectWhite），显式 trace
  stack 防深递归爆栈；
- 候选登记：release 路径上计数减至非零的对象 → 登记候选 + 按对象体积累计债务
  （全局原子债务计数器，RUNTIME §22.3）；
- 颜色/计数/候选索引位打包进对象头（参照 Nim rcShift 与模板 control block）；
- 遍历按 refMap/TypeSheet 驱动——refMap 必须精确，漏一条边即误回收；
- 白色集合先攒后统一清理；清理前对存活子引用补偿计数（模板既定的论文偏离，
  防析构副作用破坏遍历）；收集期间禁止析构路径二次登记候选；
- 回收前执行 RUNTIME §25 的 IDisposable 合约检查（只检查上报，绝不代跑
  dispose）。

### 4.6 触发与执行体

- **检测内嵌在 `release()` 实现内**：候选登记与债务累计后比较阈值；跨越时经
  原子 pending 标志**去重**后触发 `GCWakeAlarm`（RUNTIME §23.4；EventAlarm 语义：
  触发幂等、与 waiter 注册原子握手、不执行用户代码）。通知是运行时内部原子操作，不构成
  托管引用图变更；若实现上需要持有 GC 协程引用，按 §23.3 嵌套规则复用当前
  region。
- **执行体是 GC 协程**：运行时内部协程实体，绑定**运行时内置 GC Executor**
  （实现级，不对 stdlib 公共面暴露；自带 Worker，用户协程不可进入）。等价于
  专用 GC 线程，但调度、唤醒、发布全部复用 §17/§19 的 Executor/Alarm 机制；
  run-to-suspension 下唤醒延迟有界，不与用户协程竞争调度容量。
- GC 协程被唤醒后：执行 RUNTIME §23.2 握手（它是 collector 主体）→ 染色与
  清理（**pass 期间不得挂起**）→ 发布 `gcFlag=IDLE` → 触发 `GCAlarm` 唤醒
  ENTERING 协程 → 清 pending、复查债务（仍超阈值则立即再来一轮）→ 重新挂起
  于唤醒 Alarm。
- 生命周期：运行时初始化时创建，进程常驻。

### 4.7 ownership fence（RUNTIME §23 保留）

macroGC 维持全局性：进入 pass 即冻结**全部**托管引用 acquire/release（local 与
shared），单一 pass 可安全处理跨 Coroutine 的混合候选闭包（local→shared 边）；
跨协程 shared 环**支持回收**。§23.6 放行不变（分配与纯值执行不停）。

### 4.8 运行时面（C ABI 草案）

rigi_rt 导出（命名待定，形态固定）：

| 面 | 语义 |
|---|---|
| `alloc(desc)` | 按 TypeSheet 描述符分配对象头 + payload |
| `acquire(p)` / `release(p) → bool` | RC 增减；release 归零返真（调用方据此析构），减至非零时完成候选登记 + 债务累计 + 阈值检查 + 通知（§4.6） |
| `region_enter/region_exit` | RUNTIME §23.3 cFlag 协议（含双重检查与 GCAlarm 挂起路径） |
| `string_concat` 等内建面 | String 内建 `+` 等特权操作的实现（String 字符数据是特权裸缓冲区，非托管引用，RUNTIME §4） |
| `box_*` / `span_*` | Box/Span 运行时面 |
| `throw_raise` / unwind 面 | 异常抛出与 unwind 库交互（§8） |
| 协程七项保留面 | 见 `ASYNC_LOWERING_DESIGN.md` §7 表（coroutine.spawn / task.await / coroutine.yield / coroutine.complete/fail/cancel / coroutine.frame / gc.ownership-region / alarm.poll/event） |
| GC Alarm 族 | GCAlarm 与 GC 唤醒 Alarm 的创建与触发 |

### 4.9 对象头与 refMap

对象头：typeid（→TypeSheet）+ RC 计数 + 颜色/候选索引位（位打包）。rich
ValueType/Box 裸数据块的内部引用按同一 refMap 机制扫描（RUNTIME §4/§8）。
128-bit 胖引用下 RC 元数据在**对象头**（不在引用槽）；引用槽写入时聚合进对象
头真计数。

---

## 5. wrapper 烘焙

M88 边界：frontend 只携带标记，烘焙全归 Middleware。

- proxy 模板 fn（specific/wildcard）按应用标记烘焙为独立合成 fn，骑静态
  vtable；调用点 invoke 原名不改，链替换在烘焙中完成；
- 隐藏存储 `.wrapper.<wrapper 类型全称>` 合成（BIL §5.3 ABI 约定）；
- `call???` 类别路由体合成（降级调用点恒 `invoke core::Any$call???`，类别细分
  插入点在 Middleware）；
- `..init.wrapper` 在实体 init 前自动调用；`..companion.UUID` singleton 壳体的
  构造时机；
- inner 链接与可变泛型包的解包/shim 合成。

## 6. 协程降级

- 按 `ASYNC_LOWERING_DESIGN.md` §3.1 模型切 state：每个可达挂起点的下一条可
  执行语句为唯一恢复 state；
- continuation frame 内容：state、跨挂起局部/参数/临时/`.return` 槽、活动异常
  /try/循环/using 清理状态、Executor 与 CoroutineLocal 上下文、精确根映射
  （frame 是堆对象，按 refMap 描述，走 §4 的 ARC，无需栈根设施）；
- eager spawn、Task waiter 原子登记、与 fence 的交互按 ASYNC §3–§4；
- 不用 llvm.coro（理由见 §2）。

## 7. 泛型、调用与 ABI

- reified 泛型：共享代码体 + typeid 隐藏参数（RUNTIME §1 既定取舍：不提供泛型
  热路径的单态化特化 pass）；typeid 参数的传递位置与求值顺序在 ABI 定稿；
- vargs/kwargs：规范化参数包的解包与 shim；
- `invoke fn(..super)` → 直接基类原始实现；`..create` 仅属 Middleware/VM 生命
  周期阶段；
- `invoke.indirect` → callable 协议（`$$call` 虚调用）；
- native 函数：直接生成对 `rigi_rt` shim 的调用；返回用户引用类型的 FFI ABI
  在此定稿（SYNTAX §4.6 / RUNTIME §26 的留白）。

## 8. 异常机制

BIL §16.7 的 try（catch-table 形式）在 MW3 展开为 EH 边与 pad 块，目标平台
机制：

- **linux-x64：Itanium 风格 landing pad**。`invoke` / `landingpad` / `resume`；
  personality 采用 Itanium C++ ABI 系人格函数；throw 经 rigi_rt 的 raise 面进入
  unwind 库。
- **win-x64：SEH**。LLVM 的 Windows EH 构造（`catchswitch` / `catchpad` /
  `cleanuppad`），personality `__CxxFrameHandler3`。

要点：

- MIR 是 try 结构展开的单一事实源：finally 的正常路径副本与 unwind 路径
  （cleanuppad / landing pad 清理路径）由同一结构生成，两份语义不得漂移；
- catch 子句的异常类型匹配由 TypeSheet 的 `is` 判定支撑（RUNTIME §12）；
- 抛出对象是 `core.Exception` 子类实例；throw 操作数与 unwind 上下文之间的
  所有权移交、以及 catch 捕获后的归属，属 RcInjection 与 EH 的交互细节，随
  MW9 定稿；
- region 不含可抛出点（§4.2 不变量），unwind 不穿越 region。

## 9. 优化 pass 归属表

| pass | 归属 |
|---|---|
| 类型驱动操作绑定 / devirt | Middleware（MW2/MW4） |
| wrapper 烘焙 / cell 消除 | Middleware |
| ARC 注入与 region 划分 | Middleware |
| 协程状态机 / EH 展开 | Middleware |
| 布局 / ABI / 元数据发射 | Middleware（决策），LLVM（消费） |
| 不可观察 copy/Box/temp 消除 | Middleware（语义可证者）+ LLVM |
| SSA 提升 / SROA | LLVM（mem2reg 等） |
| 常量传播折叠 / GVN / LICM / DCE | LLVM |
| 内联 | LLVM（Middleware 可以 BIL §18 hint 表达建议） |
| 向量化 / 目标相关优化 | LLVM |
| 别名/只读标注生成 | Middleware 生成（noalias/readonly 等），LLVM 消费 |

## 10. 验证策略

1. **门禁**：BilVerifier 全规则；非法 BIL 必须被拒（BIL §23）。
2. **VM 对拍**：同一 BIL 在 BIL VM（行为参考实现）与 native 产物上的可观察
   行为一致（stdout/异常/退出码）。仓库已有测试资产直接复用。
3. **RC 正确性**（VM 对拍覆盖不到的领域）：native 产物挂 ASAN/Valgrind 跑全量
   套件；引用图压力与模糊测试；RcInjection 结构化自检（每 region、每 CFG 出口
   恰好配对）。
4. **黄金 .ll 快照**：黄金 BIL 用例的 PrintModule 产物快照比对，防发射回归。
5. **工具链契约测试**：LLVMSharp 绑定与 libLLVM 20 原生包的版本对齐冒烟、
   lld 链接冒烟，随 CI 跑（win-x64/linux-x64）。

## 11. 代码组织

```text
Middleware/                 # 本仓库顶层目录（C#，.NET 10 LTS，纯 BCL）
├── Gate/                   # BilReader 接线 + BilVerifier 门禁
├── Symbols/                # MW 符号图 / 类型表（驻留）
├── Binding/                # 实现绑定（唯一实现查询 / devirt 知识）
├── Mir/                    # MIR 模型 + 结构化块 → CFG 构造（含 EH 边）
├── Passes/                 # WrapperBaking / RcInjection / CoroutineSplit / CellElim / Devirt
├── Layout/                 # 布局与 ABI 决策、元数据发射计划
├── Emit/                   # LLVMSharp 模块构建 / .ll 打印 / 目标文件发射
└── Cli/                    # 驱动（输入 .bil，进程内 LLVM 管线，调 lld 链接 rigi_rt）

rigi_rt/                    # 本仓库顶层目录（C 静态库）
├── arc.c/.h                # microGC / microSGC、region 协议、对象头
├── macrogc.c/.h            # Bacon-Rajan 收集器（模板改造）、候选账本、GC 协程实体
├── coroutine.c/.h          # Coroutine / Executor / Worker / Alarm（含内置 GC Executor）
├── eh.c/.h                 # raise 与 unwind 交互（Itanium / SEH）
└── shim.c                  # libc 风格原生方法面（RUNTIME §26），libuv 底座
```

## 12. 阶段划分

每阶段以可验证产物收口；VM 对拍与全量测试为通用验收。

| 阶段 | 内容 | 收口 |
|---|---|---|
| MW0 | 工程骨架：Gate（Reader + Verifier 门禁）、CLI、符号表、LLVMSharp 接线 | 非法 BIL 被拒；空模块进程内出 .o |
| MW1 | 最小垂直切片：标量 + String（内建 `+` → `string_concat` 面）+ native print + main → LLVM 模块 → .o → lld 链接最小 rigi_rt | hello world 与字符串拼接；VM 对拍打通 |
| MW2 | 标量与运算全量（BIL §11/§12、Resources 常量池） | 对拍套件 |
| MW3 | 控制流（if/loop/switch → CFG；alloca + mem2reg） | 对拍套件 |
| MW4 | 对象系统 I：布局、字段、new、static | |
| MW5 | 调用与 ABI：invoke 族、typeid 隐藏参数、vargs/kwargs、FFI | |
| MW6 | 元数据与派发：TypeSheet/vtable/iMap/refMap、虚调用、interface | |
| MW7 | 值语义运行时 + ARC：Box/Span/胖引用、RcInjection、region 协议发射 | ASAN 全绿 |
| MW8 | 泛型运行时：`Type\<T\>`/typeOf/new、is/supers/with/cast | |
| MW9 | 异常：try/catch/finally → landing pad（linux-x64）/ SEH（win-x64） | 异常对拍套件 |
| MW10 | wrapper 烘焙全链（specific/wildcard/call???） | |
| MW11 | 协程：状态机、Executor/Worker、Alarm（libuv 底座）、Task、eager spawn | ASYNC §8 集成测试 |
| MW12 | macroGC：收集器、候选账本、GC 协程与内置 Executor、fence 激活、§25 检查 | 循环回收与泄漏检查套件 |
| MW13 | 优化收尾（move/cursor、CellElim 激进化）、工具链捆绑与发布 | |

注：

- RcInjection 的 region 协议从 MW7 起随代码生成常驻（廉价无操作），MW12 只需
  上线收集器与挂起路径，codegen 零变化。
- 字符串插值在 BIL 之前已由 frontend 降级为 `toString` + String 内建 `+`
  （SYNTAX §3.8），不构成 Middleware 的独立工作项；MW1 只依赖 String 类型与
  内建 `+`。
