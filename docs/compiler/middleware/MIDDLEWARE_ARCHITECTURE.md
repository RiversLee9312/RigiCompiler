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
`rigi_rt`（C，bitcode 合并 + 链接）的原生可执行文件。Middleware 与 `rigi_rt` 均为**本仓库**的
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
    ↓ 进程内 verify / 优化 / 目标文件发射；rigi_rt 经 clang 编成 LLVM bitcode，
       进程内合并进模块参与统一优化
    ↓ clang 驱动（-fuse-ld=lld）链接 CRT
原生可执行文件
```

核心决策（修改须重新过一遍取舍）：

工具链调用默认设十分钟期限，超时终止本次子进程树并报告受控错误；双路输出读取也受期限约束。`--verbose`/日志记录所用 clang 的路径与 SHA256，运行时 bitcode 缓存身份包含该摘要。部署方可通过 `RIGI_LLVM_SHA256` 钉住可信 clang 内容；未设置钉值时，`--toolchain`、`RIGI_LLVM` 和本机工具链仍属于用户显式信任输入，内容指纹本身不证明发行来源。

LLVM 元数据全局名保留完整 canonical，由 LLVM API 处理文本引号转义，不将泛型逗号和类型名中的点折叠成同一字符。

内部 `MirGetClassTypeArgument` 按含元数的宿主布局键读取真实实例实参：普通类读取已规划的隐藏 typeid 槽，固定 ABI 数组读取前缀中的元素 sheet；不把数组伪装成具有普通隐藏字段的类。泛型函数的 coroutine frame 名虽包含原函数签名，但 frame 声明本身是固定布局的非泛型类，是否开放必须按声明判定。

1. **实现语言 C#（目标平台 .NET 10 LTS），复用 `Bil/` 生态**。Reader、对象模型、
   Verifier 已在仓库内且对中端零依赖；Middleware 是唯一新增依赖 `Bil/` 的组件。
   保持纯 BCL、无第三方依赖的仓库纪律。
2. **经 LLVMSharp 进程内构建 LLVM 模块，锁定 LLVM 20**。绑定层最新跟随上游
   20.1.x，libLLVM 原生库经 NuGet runtime 包按 RID 分发（win-x64/linux-x64
   均有），免工具链安装；校验、新 PM 优化管线、目标文件发射全部进程内完成，
   保留的外部进程只有 clang（rigi_rt 现场编译 + 驱动 lld 链接）。.ll 文本仍由
   PrintModule 产出，作调试与黄金
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
5. **运行时逻辑沉在 C 库 `rigi_rt`**：ARC、macroGC、协程/Executor/Alarm、
   native shim。Middleware 生成的 .ll 只发射对这些运行时面的调用；rigi_rt
   经 clang 编成 LLVM bitcode 进程内合并进模块（§2），运行时面随统一优化
   管线内联。

---

### 1.1 Handle 隐藏布局与 Cell 虚槽

安全 Atomic 集合使用普通 Rigi 源码持锁回调与逐元素序列化复制。
静态泛型工厂帧只含方法级 typeid；开放 class 的 new 在 MIR 保留实际
泛型映射（例如工厂 E 到宿主 T），分配 sheet 可复用模板，实例隐藏
typeid 仍须写入。可挂起调用的嵌套开放 class 实参从接收者隐藏字段
取得 typeid，不按调用方同名型参猜测。协程 frame 名注册与 MirType
使用相同 canonical 归一，包含多参数泛型返回类型时也不产生双键。

`BuiltinToStringDispatchPass` 在 CoroutineSplit 前为 Any/Object 的
对象文本覆写生成实际类型分派；命中现有虚槽后走普通 MIR 调用，
未命中才使用默认 native 文本 helper。普通 Map 的泛型对象键因而
遵循用户 toString 覆写，分派中的挂起与引用生命周期仍走统一后续 pass。
同一 pass 以同构通道为 `hash` 生成 `$mw.any.hash` 分派链（§4.8
`any_hash` 面； CoroutineSplit 白名单同步放行该合成 fn），无任何
override 时不合成链、调用点直调 helper。

`==`/`!=` 的 Any 默认 equals 臂（equals-or-hash 判等链，用户裁定）：
未声明 `equals` 的类型比较直调合成默认体 `core::Any$$equals`（双虚调
`hash`，体内 `Any$hash` invoke 经 FlowBuilder 重定向 +
`$mw.any.hash` 分派链得 override 感知）——静态左操作数在
UserOperatorLowering 直调（MirReachability 对两合成 fn 恒收编）、
泛型占位左操作数在 GenericOpEmitter 候选臂全落空后的末臂直调。
已知边界：静态链无 `equals` 而运行期实际类型（子类静默 hiding 再定义）
有 `equals` 时，native 直调默认体、VM 按实际类型派发用户 `equals`，
分歧仅限该组合（Map 主路径泛型臂两端一致）；后续可用
`$mw.any.equals` 双操作数通道闭合。

同步 callable 若可挂起，CoroutineSplit 的动态实现协议臂在写入具体 callee frame 前使用 MIR cast/box 适配参数；DONE 从具体结果字段先读到同类型临时槽，再转换到调用点类型。全部临时槽仍由既有生命周期 pass 管理，禁止把开放胖值直接写入具体标量/struct 字段或反向读取。

Handle 固定 `.handle` 布局为 16B 对象头、16B 隐藏 target 胖引用、16B kind/mutable 元数据；只将 target 放入 refMap，不加入可枚举 Fields。普通 ARC 析构和 macroGC trace/teardown 共用该唯一扫描项，保证正常销毁与环收集恰好释放一次，禁止额外 native release 面。

Cell/ReadonlyCell 的 `getValue/setValue` 与同步 Func/Action 的 `$$call` 虚槽统一使用胖值 ABI。`FatValueSlotAbi` 按真实继承和签名为槽生成适配器，复用普通参数编组、装拆箱和 ARC 临时销毁；封闭与开放泛型调用均使用同一槽约定，callable 的全部普通参数逐项适配，非 void 返回统一为胖值。同名 Cell 重载、接口独立段与 async callable 保留自身 ABI。适配器把异常 pending 原样传回普通调用点的异常边；异常出口从函数全局值类型读取真实返回类型，不能从 opaque pointer 推断。

## 2. 外部依赖与选型裁决

| 部件 | 选型 | 理由 / 备选 |
|---|---|---|
| LLVM 集成 | **LLVMSharp 进程内**（绑定 20.1.x + libLLVM 20 NuGet runtime 包）；**锁定 LLVM 20** | 无 GC 设施需求使 C API 天花板不咬人（§4.1）；绑定与原生包同大版本对齐。Ubiquity.NET 排除（仅 win-x64）。注：runtime 包仅含 libLLVM 共享库；无 RID 的 `dotnet build`/`dotnet run` 开发回路需显式引用 runtime 包（csproj 已办） |
| 链接器 | **lld，经 clang 驱动（-fuse-ld=lld）；CRT 发现交 clang**；获取链定稿：CI 用 GitHub runner 预装（windows-latest: `C:\Program Files\LLVM`，ubuntu-latest: `/usr/bin/ld.lld`），不耗额外 action 额度；开发机 PATH 优先，缺则 `tools/Fetch-LlvmToolchain.ps1` 下载官方 20.1.2 选择性部件缓存 `tools/.llvm/`（gitignored，SHA256 钉版校验） | 编译产物为 .o；链接是唯一保留的外部步骤之一；runner 预装版本漂移可容忍（lld 只链接自产 .o 与 rigi_rt）；native 驱动解析顺序 `--toolchain` → `RIGI_LLVM` → `tools/.llvm/` → PATH |
| GC 引擎底座 | 教学级 Bacon-Rajan C 模板改造 | 候选底座 `rjungemann/turmeric` gc.c（MIT，纯 C、可剥离）；教学参照 `fitzgen/bacon-rajan-cc`（Rust，注释最全）；语义对照 Nim `lib/system/orc.nim`（位打包、rootIdx、自适应阈值）。论文并发版（Red/Orange/transfer buffer）无限期推迟 |
| 分配器 | 首版用 CRT malloc；mimalloc（MIT）为后续可选替换 | GC 主堆自研；分配器层与 GC 解耦，可后换 |
| rigi_rt 编译 | **clang 现场编译**（MW1 起；获取链与 lld 同：CI 用 runner 预装 clang，开发机 PATH 优先、缺则 Fetch-LlvmToolchain.ps1 钉版缓存）。产物形态：LLVM bitcode（`-emit-llvm -c`，unity build）+ EmbeddedResource 内嵌源 + 内容哈希缓存；Emit 阶段 `LLVMLinkModules2` 进程内合并进模块，运行时面经统一优化管线内联——C 写的 access helper 由此获得零成本内联 | 预编译 .lib/.a 入库排除（双平台二进制漂移与审查成本）；源码即真相，与本仓库同纪律；clang 编 .c 需 CRT 头文件——Windows 自动探测已装 VS/SDK，Linux 用系统 glibc 头文件 |
| 事件/定时底座 | **libuv**（MIT，静态链接）；获取链定稿（MW11b）：官方无预编译二进制，渠道②=钉版源码 + 本地构建——`tools/Fetch-Libuv.ps1` 下载 GitHub tag v1.52.1 源码 tarball（SHA256 钉版校验；dist.libuv.org 的 dist tarball 是 autotools 形态无顶层 CMakeLists.txt，弃用），cmake 最小配置（`-DBUILD_TESTING=OFF` + Release，win 优先 tools/.llvm clang-cl+Ninja、缺退 VS 生成器；linux clang/cc）构建静态库，缓存 `tools/.libuv/<rid>/`（`lib/uv_a.lib` | `lib/libuv_a.a` + `include/` + VERSION.txt，gitignored，CI 双平台 job 各跑一步）。解析顺序 `--libuv-dir` → `RIGI_LIBUV` → `tools/.libuv/<rid>` → 编译器 exe 旁 `.libuv/<rid>`；`native --out` 命中时链接行追加静态库全路径 + 系统库（win：psapi user32 advapi32 iphlpapi userenv ws2_32 dbghelp ole32 shell32；linux：pthread dl），未命中按现状降级（Alarm 面 abort）；rigi_rt 命中时带 `-I<include>` + `-DRIGI_HAS_LIBUV=1` 编译（参数入内容哈希；C 侧 uv 用法一律包 `#ifdef RIGI_HAS_LIBUV`） | 跨平台事件循环 + 定时器 + 线程池 + 同步原语一体；win-x64（IOCP）/linux-x64（epoll）均一等公民；每 Worker 一个 loop，EventAlarm/sleep 以其为底座；Worker 唤醒走 `uv_async_send` |
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
  派发；VirtualSlotOf 精确命中布局计划的 canonical 或 SignatureKey，声明序回退已随构造类型具化计划移除）；
- 可静态消除者（不可观察 copy/Box/temp）→ 标记消除。

不重跑 source-level overload ranking（§3.3）；查询不含隐式转换、候选排序或
最佳匹配。devirtualization 所需的 vtable/iMap 知识在此层沉淀。

### MW3 MIR 构造

BIL 结构化块 → CFG 基本块。输入已结构化，输出天然 reducible CFG：**确定性直译，
无需 Relooper/Stackifier**。具名局部变量 → alloca 槽；SSA 提升交给 LLVM
mem2reg。MIR 保留 BIL 类型与符号身份，直到 MW6 发射前不做类型擦除。BIL §16.7
try 的 catch-table 形式在本层展开为 EH 边与 pad 块（机制见 §8）。

### MW4 MIR pass 群

语言语义 pass 全在此层（§9 归属表），pass 之间以 MIR 为唯一交换物。

**翻译 pass 纪律**（与中端 P3/P4 同构，按 pass 切开）：每个指令翻译
`IMwStage` 对应一个翻译 visitor——内部**唯一 switch**（Dispatcher）把指令
种类分到处理类。BIL→MIR 与 MIR→LLVM 的处理类是 CRTP（静态 `Visit` 唯一
入口，`Enter`/`Exit` `finally` 配对）。小改写 pass（IndexOperator /
Accessor）用 **内部类** 隔离各 case，默认不上 CRTP——只有出现栈类生命
周期才升 CRTP。Layout 不是翻译 visitor（类型闭包求解）；RcInjection
不是逐指令翻译（全函数 CFG 配平）。新增 MIR 指令：消费它的翻译 pass
的 Dispatcher 加一行 + 一个处理类，禁止往 `MirBuilder` / `ModuleBuilder`
/ `CallEmitter` 堆分支。MW10 wrapper 烘焙按同一纪律拆成多个 pass，不收
成单 `WrapperBakingPass`。插槽：AccessorLowering 之后、RcInjection 之前。

关键 pass：

- **WrapperBaking 群**（§5；MW10 已收口：插槽在 AccessorLowering 之后、
  RcInjection 之前，五个小改写 pass——FieldProxyBaking → MethodProxyBaking
  → ProxyBaking → CallWildcardLowering → SingletonLowering，共享设施
  ProxyBakeSupport / ProxyWildcardAbi）
- **RcInjection**（§4.2，安全攸关）
- **CoroutineSplit**（§6）
- **CellElim**：`.cell<T>`/`.readonly_cell<T>` 特权拼写识别（BIL §6 约定），
  消除可证的 cell 间接
- **Devirt**：静态可证的虚调用 → 直接调用

### MW5 布局与 ABI

字段偏移、对齐、Box 物理形态、TypeSheet/vtable/iMap/refMap 的发射计划、调用
约定（typeid 隐藏参数位置、胖引用传参与返回、sret、vargs/kwargs 包形态）。
布局决策在此定稿，MW6 只消费。ABI 常数落点：数组前缀 32B 在 `TypeLayout`；
TypeSheet/TypeInfo 字段序在 `TypeSheetAbi`（镜像 `arc.h`）；C 边界 out 首参
与内联值类型返回形态在 `CallAbi`。Emit 只填 LLVM 类型、函数指针与常量。

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

**MW7a 落地形态**：不变量——每个托管槽在每个 CFG 出口恰好配对 acquire/release，
region 面内自包含（内部无挂起点、无可抛 Rigi 异常的调用）。决策层只插入两条
MIR 指令 `MirAcquireSlot` / `MirReleaseSlot`；发射层按槽分类落到
ref/value/string 面。四规则：（1）入口对胖引用/String 参数 acquire（值类型
`.this` 豁免，其 +1 由落槽建立，避免与本规则双计）；（2）托管 `CopyLocal`
展开为 Release–Copy–Acquire 三段式，dst==src 删除；（3）产出类指令前置
Release 目标槽；（4）ret 块将返回值迁入合成局部 `$mw.ret` 后按登记序
Release 其余托管槽（出口序列不含 `$mw.ret`）。region 协议由运行时面自身
enter/exit，pass 不另插 region 指令。

### 4.3 两级 ARC

- **microGC**（local Object）：原子计数。同一 Coroutine 的执行仍串行，
  但失败 Task 保存的异常及其 local 字段图可被多个 waiter 同时持有。
  local/shared 的静态共享限制与对象身份不变；计数采用与 microSGC
  相同的原子增减和候选登记协议，不改变用户字段的并发读写语义。
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

> **MW12 落地形态（macrogc.c/.h 已收口）**：颜色/候选索引打包在对象头
> `packedFlags`——颜色 2bit + 候选账本索引 24bit（[8..32)），bit2 兼作
> §25 disposed 位；候选账本是动态数组 + 自旋锁；全局原子债务默认阈值
> 1MiB（`RIGI_RT_GC_THRESHOLD` 环境变量覆盖）；白色集合**两段式清理**——
> 先攒批 → 对存活子引用补偿计数 → 先全量走边、后统一 `track_free`（同批
> 白色对象的头在清理期互相可读）；收集期间 `gc_in_collect` + fence 冻结
> 双保险禁止二次登记；诊断旋钮 `RIGI_RT_GC_OFF=1` / `RIGI_RT_GC_TRACE=1`。

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

> **MW12 落地形态**：GC 协程的 native 承载是**常驻专用线程**
> （CreateThread/pthread_create 双平台薄封装，不依赖 libuv，不经 Rigi
> Dispatcher 通道）；`GCWakeAlarm`/`GCAlarm` 降级为双平台手动复位事件
> （Win32 Event / pthread condvar），§19.3 sticky/幂等语义保持，不进
> stdlib/VM hook 表。§23.2 六步握手原样执行（STARTING 独占 CAS → fence →
> 自旋排空 cFlag，30s 超时亮红灯 abort → PROCESSING → 染色清理 → fence →
> IDLE + 触发 GCAlarm）；清空 pending + 债务复查仍超阈值立即再来一轮。
> shim.c 启动序：main → `rigi_gc_init` → `rigi_entry`；atexit LIFO 注册序
> mem_report → gexc_flush → gc_shutdown → globals_cleanup（执行序：
> globals_cleanup 释放静态槽 → gc_shutdown 终轮收集兜底 → gexc_flush 打印
> 晚到 undisposed 事件 → mem_report 零泄漏报告）。

### 4.7 ownership fence（RUNTIME §23 保留）

macroGC 维持全局性：进入 pass 即冻结**全部**托管引用 acquire/release（local 与
shared），单一 pass 可安全处理跨 Coroutine 的混合候选闭包（local→shared 边）；
跨协程 shared 环**支持回收**。§23.6 放行不变（分配与纯值执行不停）。

> **MW12 落地形态**：§23.3 隐藏 yield GCAlarm 的 native 降级 = `region_enter`
> 内**阻塞等平台事件**——region 内禁止挂起点 + 同步函数无法挂起，阻塞 OS
> 线程是唯一直译；阻塞期间协程不迁移。cFlag 用 OS 线程槽（懒认领的 256 槽
> cache-line 独占注册表）即满足 §23.1 身份要求。单个 ARC 面自行进入 region；
> codegen 对“RC 更新 + 引用槽写入”的复合操作再发射一层可嵌套的外层 region，
> 确保 macroGC 观察不到计数和引用图不一致的中间态；RcInjection 生成的托管
> 局部 `release → copy → acquire` 三元组也由 Emit 合并进同一个外层 region。

### 4.8 运行时面（C ABI 草案）

rigi_rt 导出（命名待定，形态固定）：

| 面 | 语义 |
|---|---|
| `alloc(desc)` | 按 TypeSheet 描述符分配对象头 + payload |
| `acquire(p)` / `release(p) → bool` | RC 增减；release 归零返真（调用方据此析构），减至非零时完成候选登记 + 债务累计 + 阈值检查 + 通知（§4.6） |
| `rigi_ref_acquire` / `rigi_ref_release` | 值语义四面族·胖引用槽：按 tag 分派对象/堆值/内联；生成代码只见此对 |
| `rigi_value_acquire` / `rigi_value_release` | 值语义四面族·值类型：按 TypeSheet.refMap 走查内部胖引用/String 槽 |
| `rigi_string_acquire` / `rigi_string_release` / `rigi_string_new` | String 槽 ARC（块头 `{atomic u32 rc, u32 reserved}`，data=块+8；字面量 rc=`0xFFFFFFFF` 永生） |
| `rigi_region_enter` / `rigi_region_exit` | RUNTIME §23.3 cFlag 协议（MW12 已落地：OS 线程槽 cFlag 注册表 + region_enter 内阻塞等平台事件，无双检挂起路径）；支持嵌套，单个 ARC 面自行包裹，生成代码用外层 region 覆盖复合槽位变更 |
| `rigi_track_malloc` / `rigi_track_free` / `rigi_mem_report` | 台账三面：`RIGI_RT_MEMTRACK=1` 时跟踪堆块，进程退出未清零即 stderr + exit 1 |
| `string_concat` 等内建面 | String 内建 `+` 等特权操作的实现（String 字符数据是特权裸缓冲区，非托管引用，RUNTIME §4） |
| `i64_to_string` / `u64_to_string` / `f64_to_string` / `f32_to_string` / `bool_to_string` / `char_to_string` | 标量标准文本（StringOut 首参；any_to_string 的格式化底座；窄整数在 any_to_string 内按符号性 widen 到 i64/u64；f64/f32 为 Ryu 最短往返 + .NET 默认呈现） |
| `any_to_string` | 任意胖值标准文本（StringOut 首参 + Any 槽指针）：内建标量走对应 to_string 面；`core::String`（tag1）拷贝裸块；其余（tag2 对象 / 大 struct 等）取 TypeInfo.name。不虚调 toString（防默认体递归；override 经方法虚派发，不经本面） |
| `any_hash` | 任意胖值 i64 哈希（Any 槽指针入参；§3.8.1 Map 键判等）：tag1 String 对 data 字节取 FNV-1a 64（内容）；tag0 标量对 payload 8 字节取 FNV-1a 64（按值）；tag2 对象与 tag1 非 String 堆值对 payload（堆指针）取 FNV-1a 64（身份，不直接返回裸指针）；null 固定 0。同一进程内同值必同哈希；VM hook 已统一为同一 FNV-1a 64（review-20260910），标量/字符串数值两宿主一致。不虚调 hash（防默认体递归；override 经 `$mw.any.hash` 合成分派链走方法虚派发，不经本面） |
| `box_*` | Box 运行时面 |
| `rigi_span_alloc` | Span/SharedSpan 分配（与数组同构：32B 前缀 + 原生 stride 内联元素；TypeSheet 区分 Span vs SharedSpan）。元素访问内联无独立面；析构复用数组走查（`RIGI_TYPE_ARRAY`） |
| `rigi_try_cast` | 动态 cast（占位目标）：胖引用 typeid+payload + 目标 TypeSheet* + 两枚 out i64；is 命中改写视图 typeid；数值互转对齐 VM；失败返 0 |
| `rigi_cast_f64_to_int` | 浮点→整数（`i64(f64, i32 kind)`；NaN→0，溢出饱和到 32/64 位宽再截断；对齐 C# unchecked conv） |
| `rigi_type_is` / `rigi_type_is_indirect` | 胖引用实际类型是否为目标或其子类（协变）；tag2 取对象头 sheet，tag0/tag1 掩码 typeid；接口走 TypeInfo.ifaceClosure |
| `rigi_type_supers` / `rigi_type_supers_indirect` | 实际类型是否为目标的基类（逆变）；沿 target.baseTypeId 链，并查 target.TypeInfo.ifaceClosure（多 implements 父接口） |
| `rigi_type_with` / `rigi_type_with_indirect` | 实际类型（及基类/接口闭包）的 TypeInfo.wrappers 是否含目标 wrapper sheet |
| `rigi_typeof` | 取胖引用实际 TypeSheet*（typeOf 值形态）；tag2 取对象头 sheet，tag0/tag1 掩码 typeid |
| `rigi_exc_raise` / `rigi_exc_pending` / `rigi_exc_take` | checked-flag 异常传输三面（§8）：raise = 异常对象 acquire+1 写线程局部 pending 槽；pending = 借用查询（不动计数）；take = 取走并清槽（+1 所有权随返回值移交） |
| `rigi_type_name_of` / `rigi_exc_halt` | 顶层未捕获 reporter（§8）：obj→对象头 typeId→TypeInfo.name 诊断名拷出（借用语义）+ exit(1) 收尾（noreturn） |
| `rigi_mark_disposed` | §25.2 disposal 标记（对象头 packedFlags bit2，原子 or）；Emit 在「iMap 中 IDisposable.dispose 槽目标」函数（烘焙后身份，async stub 含）prologue 发射 |
| `rigi_gexc_report_undisposed` / `rigi_gexc_take` / `rigi_gexc_flush_default` | §25.2 全局异常通道队列三面：销毁检查（`rigi_dispose_check`，rigi_destruct 双路径 + macroGC teardown 共用）发现未 dispose 即入队（payload=违规对象实际 TypeSheet*）；entry stub 在 drain 后、失败汇总前循环 take（StringOut 借用语义）统一派发；atexit 晚到事件（globals_cleanup/GC 终轮）按默认文本打印 |
| `rigi_gexc_register_handler` / `rigi_gexc_handler_count` / `rigi_gexc_handler_at` | `core.GlobalExceptionHandler` 处理器注册表三面（RigiFatRef +1 持有，注册序=下标序；§3.1.1 共享安全闸门禁止静态字段持 local Action，注册表沉 native） |
| 协程原语面族 | **MW11c 定稿形态（RUNTIME §17.4）**：调度逻辑在 Rigi 世界（`core.coroutine` Dispatcher/Task/Worker），rigi_rt 只留原语——Worker（创建/销毁/入队 + 跨线程唤醒/park）、协程句柄（`create(resumeFn, frame)` / `resume(handle)`→执行段归宿 / `destroy`）、定时器、同步 Mutex（仅 Dispatcher 内部队列一致性，与语言级异步 `Mutex` 是两个东西）、TLS 当前上下文、时钟（`core.time.DateTime.now()` 底座）、未观察失败注册表（`rigi_failure_record/get/drop/take_unobserved`）。native 经 fn 指针回调 Rigi（resume / `rigi_dispatcher_entry` / `rigi_dispatch_publish` / `rigi_alarm_ring`）。生成代码交互点：spawn stub → 建 Task + 句柄 + Dispatcher.publish；await → Task 方法决策码；DONE → complete/fail；yield → Dispatcher 重排。均无 String 编组，Emit 经 `DeclareHelperFace` 直接声明 |
| GC Alarm 族 | 内部平台事件形态（MW12 已落地）：GCWakeAlarm/GCAlarm = 双平台手动复位事件（Win32 Event / pthread condvar），§19.3 sticky/幂等语义保持；不经 Dispatcher 通道、不进 RuntimeFaces，无跨层可见形态 |

注：运行时面一律经 bitcode 合并进模块参与优化；频繁调用的面（如胖引用 access
helper）由优化管线内联，必要时以 `noinline` 标注例外。

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
- 隐藏存储 `.wrapper.<wrapper 类型全称>` 合成（BIL §5.3 ABI 约定；`WrapperAbi` +
  `HiddenStoragePlanner` 把 Entity/字段-Value/Method 槽写入宿主布局并进 refMap；
  wrapper 类型本身按内联值布局，rich 由源码显式声明）。槽身份对齐 VM 隐藏键
  （`VmContext.HiddenEntityKey/FieldKey/MethodKey`）：Entity 键仅含 wrapper
  精确 TypeRef——子类重申同 ref 不另开物理槽，槽恒归首次声明（最基类）偏移、
  随 basePlan 原名逐层拷入，重申的安装与环读经发射期 extends 下探同归该槽；
  Field/Method 键含字段/方法符号，天然归声明类唯一。内存路径已落地：
  `get.wrapper` / `get.wrapper.field` 从隐藏槽值拷贝，`set.wrapper.field` 沿链
  GEP 后写内层字段，`new.wrapper.*` 在 `..init.wrapper` 的 `.this` 上调 wrapper
  init 安装；wrapper 布局偏移 0 为私有宿主回指（不进 refMap），内联值宿主
  用相对偏移保证复制安全，`get.self` 解码后才产生普通值。环 receiver 槽是原地访问（SYNTAX §14.5）：各
  trampoline 经 `MirGetWrapper` / `MirGetWrapperMethodAddr` 取最外环隐藏槽
  地址后调首环，wrapper 状态跨调用持久；
- pass 群构成（均为小改写 pass，读写集与排序见 MwPipeline）：
  - **ProxyBakingPass**：Entity 方法/运算符链——specific 环特化 + inner 链接
    （下一环为 wildcard 时具体实参打包胖值 ABI）；wildcard 环特化 canonical
    模板（标量 typeid 代入、包占位按 Any 擦除），环内 `MirInnerCall` 改写为
    动态分派块（symbol 命中 → 解包直进下一环，否则调 router 重路由）；
    原名槽改 trampoline（首环 wildcard 时打包 symbol/包）；
  - **FieldProxyBakingPass**：字段 get/set 链唯一拦截点，Value（字段自身
    wrapped）与 Entity（宿主类型 wrapped）双源——写链 outer→inner 逐环特化
    `.proxy.set`，读链终态后自内向外逐环变值；Entity 面按字段名 specific
    优先、wildcard 兜底、None 跳过；字段同时双 wrapped 时只跑字段链（VM
    短路语义对齐）；
  - **MethodProxyBakingPass**：Method wrapper `.proxy.call` 链——应用事实源
    为 `..init.wrapper` 体安装指令；原始方法体外移为 `$.mwrapped.` 中缀，
    实现槽 fn 替换为 trampoline。排在 ProxyBaking 之前是组合关键：Entity
    烘焙把 method trampoline 整体外移为 `$.wrapped.` 并换 Entity
    trampoline，天然形成 Entity→Method→raw 三层；绕过全链的旁路落点指向
    `$.mwrapped.` 最深层原始体；
  - **CallWildcardLoweringPass**：`call???` 降级——前端对静态类型未声明且
    链上有 `.proxy.*` 的调用恒发 `invoke core::Any$call???`，本 pass 改写为
    模块级分发 fn `$mw.call???.dispatch`：对候选宿主按继承深度（子类
    wrapper 集优先）做 `rigi_type_is` if 链，命中调该宿主 entry 环链
    （外层→内层特化的 `$mw.call???.entry.<层>`，末环落 router）；全不中抛
    `core.NoSuchMethodException`；
  - **SingletonLoweringPass**：singleton 三态 get fn 与 `new type(singleton)`
    改写（机制见 §7 singleton 段）；
- inner 重路由与 call??? 复用同一 per(宿主, fromLayer) router：router if 链
  先覆盖字段访问器符号（自 fromLayer 起进该字段的 Entity get/set 环链，
  分支发射委托 FieldProxyBakingPass 钩子——字段链环已先行烘焙，无循环
  引用、不重复烘焙），再覆盖宿主全部可烘焙方法/运算符，链末 miss 抛
  `core.NoSuchMethodException`；
- `..init.wrapper` 在实体 init 前自动调用；`..companion` singleton 壳体（无名
  称 UUID，BIL §8.7）随 singleton 急切初始化构造（§7）；
- inner 链接与可变泛型包的解包/shim 合成。

## 6. 协程降级

- 按 `ASYNC_LOWERING_DESIGN.md` §3.1 模型切 state：每个可达挂起点的下一条可
  执行语句为唯一恢复 state；
- continuation frame 内容：state、跨挂起局部/参数/临时/`.return` 槽、活动异常
  /try/循环/using 清理状态、Executor 与 CoroutineLocal 上下文、精确根映射
  （frame 是堆对象，按 refMap 描述，走 §4 的 ARC，无需栈根设施）；
- eager spawn、Task waiter 原子登记、与 fence 的交互按 ASYNC §3–§4；
- 不用 llvm.coro（理由见 §2）。

**MW11c 架构转向（定稿目标形态）**：协程运行时从「C 侧重实现」转向
「**Rigi 世界重实现 + native 只留原语**」（RUNTIME §17.4）：

- Task/Task\<TReturn\> 完全用 Rigi 实现（`core.coroutine`，具体 shared
  class），与 Dispatcher 交互解决生命周期钩子；Dispatcher 调度逻辑用 Rigi
  实现（runnable 队列、publish/next、quiescence、未观察失败清单、alarm
  集成、Polling 探测表）；Worker = Rigi 世界里 OS 线程的抽象（内部 API，
  不暴露给用户），线程体 = 入口 fn 指针进 Dispatcher 循环；Executor 为
  singleton 门面（RUNTIME §20.1），持 Dispatcher + Worker 配置。VM 与
  native 共享同一份 Rigi 调度逻辑——VM 重构其执行模型配合（Worker = 跑
  Dispatcher BIL 的解释线程，resume 钩子嵌套驱动用户协程栈）。
- **保留**（MW11a/b 既有实现继续有效）：CoroutineSplit 状态机（MIR/Emit
  编译侧 stub/resume/frame 合成类型）、checked-flag EH（§8）、ARC/region
  （§4）、alarm 定时原语底座与 libuv 获取链（§2）。
- **重构**：rigi_rt 的 C 侧 Task 终态/waiter/Executor 队列/drain 逻辑 →
  Rigi 实现替换，rigi_rt 瘦身为 §4.8 协程原语面族（Worker/协程句柄/
  定时器/同步 Mutex/TLS/时钟）；VM 的 VmTask/VmExecutor 调度逻辑 → VM
  执行模型改造。
- **生成代码交互点（目标形态）**：spawn stub → 建 Task 对象 + 协程句柄 +
  Dispatcher.publish；await → Task 方法返回决策码（快路径读终态/冷启动/
  登记 waiter），Suspend 码才走编译侧挂起；DONE → Task.complete/fail
  Rigi 方法；yield → Dispatcher 重排当前任务。
- **冷 Task**（RUNTIME §18.4）：spawn-into 复用 Task 对象建协程，Task↔
  协程 1:1；`run`/`executor` 预设与换绑、TaskState 投影由 Rigi 侧 Task
  方法承载。多 Worker 与取消入口随本阶段定稿。body 绑定双通道
  （CoroutineSplitPass 棒5a）：构造点静态类型具体 → `$mw.coldtask.*`
  工厂预建 frame/句柄（`$$call` 沿 extends 链解析，frame/resume 取
  声明宿主的 split 产物，与 VM `FindCallTarget` 拍平 sheet 含继承槽
  同语义）；静态类型不透明（AsyncAction/AsyncFunc 槽）→ 保留真 init，
  启动时 `bindColdBody` 改写为 type.is 链 + `$mw.bindcold.*` 动态绑定。
  布局前的 `ConstructedCallCollector` 沿已到达 Task 的实际 body 字段继续
  收集闭合调用类型；该调用尚未出现在默认 BIL 方法体中，不能仅扫描该体。
  同样，属性读取须按访问器规则分析 getter，才能覆盖其中创建的泛型对象。
  两者均沿实际调用与存储事实传播，不因类型出现就扫描其全部成员。

**MW11a 已收口（中间形态：C 侧重实现，单线程垂直切片）**：

- **stub/resume 分工**（CoroutineSplitPass，SingletonLowering 之后、RcInjection
  之前）：async fn 拆为 spawn stub（原符号、调用点零改动：new frame → 参数/
  类级 typeid 落 frame 字段 → MirSpawn → ret 热 Task）+ 合成 resume fn
  （`$mw.resume.<fn canonical>`，MIR 签名 frame 胖引用 → i32，entry
  `switch(frame.state)` 分发；state 0 = 原入口，N = 各挂起点恢复块）。await
  改写为 MirTaskWait + 四路 switch（SUSPENDED 存活跃槽 ret 0 / COMPLETED
  解包续行 / FAILED 沿原 ExcTarget 重抛 / **CANCELLED 防御性
  MirUnreachable**——v1 无取消入口）；裸 yield 改写为存 frame +
  MirYieldCall + ret YIELDED；各 MirRet 出口改写为结果装箱 +
  MirTaskComplete + ret DONE；resume 传播垫尾（RcInjection 分叉）=
  MirTakePending + MirTaskFail + release 全托管槽 + frame 最终 release +
  ret DONE（未捕获异常归宿 Task FAILED，不跨协程帧传播）。
- **frame 合成类型**（SyntheticTypePlanner）：内部 class
  `$mw.frame.<fn canonical>`（state i32 + 保存槽平铺、托管槽进 refMap、
  vtable 仅槽 0 init 分发器占位），pass 期注册进 Symbols + Layout 计划表，
  Emit 的 TypeSheet/refMap 发射零特例消费；frame 经 rigi_alloc 分配（零
  初始化由 memset 承担，RcInjection 不变量依赖）。
- **Emit**（CoroutineEmitter + ModuleBuilder 特判）：五指令到面族的机械
  映射（Emit 不插 acquire/release，move 语义已由 RcInjection 配平）；
  resume fn 有意特判 C ABI 为 `i32(ptr)`（函数指针必须匹配
  RigiResumeFn），entry prologue 把裸 ptr 重构为 frame 胖引用落
  `$mw.frame` 槽；开放 `Task<T 占位>`（泛型 async fn / 泛型类 async 方法）
  的 typeid 物化回退无元数 Task sheet（可见区恒 16B 对象头，sheet 仅作
  运行时簿记身份）。
- **单线程 MainExecutor**：rigi_entry 对齐 VM `BilVm.Run`——
  `rigi_root_begin` → singletons → globals.init → main（同步直调）→ main
  pending 收进合成槽（不立即报告）→ `rigi_root_end` →
  `rigi_executor_run`（drain 至 quiescence，fire-and-forget 同被等待）→
  失败汇总（main 失败 > 未观察失败，同 MW9 顶层 reporter 出口）。
- **非 async fn 挂起点全链支持**（B-1 起，VM 栈式跨界语义对齐）：
  taint 分析沿调用图反向标记「可达挂起点」fn（tainted）；tainted
  普通 fn 状态机化（裸 frame：`state` + 保存槽 + `$mw.result`，无
  Task 包装，frame 所有权归调用方；resume fn `IsPlainResume`，传播
  垫尾 release + ret FAILED(3) 沿链上传）；tainted→tainted 直调
  改写为建 callee frame + 落参 + `MirResumeCall` 原生栈下钻 +
  四码分流（SUSPENDED/YIELDED 上传 / DONE 读 `$mw.result` 续行 /
  FAILED 取 pending 沿原 ExcTarget 重抛）；tainted main 走 Task
  包装 split + `$mw.main.settle`，rigi_entry 改「调 stub 发布主
  协程 → drain → settle 取结果/重抛失败」（对齐 VM `BilVm.Run`
  的 main 协程化）。
- **B-2 全组合收口**：① taint 传染边全集 = 直调 / super（恒直调）
  / 值类型宿主运算符（直调形态）/ 虚·interface·class 运算符派发
  （闭包内任一实现 tainted 则整点升级）/ new init。② 虚/interface
  派发挂起点 = 调用点动态分流：闭包全类臂（最深派生优先——臂条
  件 type.is 是子类判定，浅类臂不得遮蔽深类），tainted 实现臂走
  直调协议（每实现一套 frame/落参/调用块；callee 槽在臂内建后回
  存本层 frame——head 的 emitSave 先于分流执行），非 tainted 实
  现臂与默认臂（闭包外/null 接收者）落原调用（普通虚派发，NRE
  语义保持）；恢复经同序恢复分流链直落调用块（frame 不重建）。
  ③ super 调用与直调运算符同直调协议。④ 含挂起点的 init：构造
  点分配与 init 下钻分离——head 用合成空 init 分配（init.wrapper
  原位缝合字段初始值）→ Target 槽先落定 → init frame（.this =
  新建对象）下钻；DONE 直落原后继（结果即 Target 槽）。⑤ §7.2
  隐藏参数落参：实参按「形参剔除类级 typeid」位序 zip；被剔除
  的类级 typeid 按宿主构造形态合成（闭合实参 → MirGetTypeId 常
  量；外层占位 → 调用方同名 .generic.* 局部转抄；值类型接收者被
  cast/copy 剥成裸模板时沿产出链回溯构造形态）。⑥ plain fn 内
  yield Alarm 放开：恢复闸失败尾 plain 分叉（pending 保持置位
  ret FAILED 沿链上传，对齐 VM 帧栈逐层展开；Tasked 仍走 Task
  FAILED 终态序列）。  ⑦ using dispose 可挂起（§17.3）：dispose
  虚派发臂同②协议化。R2 残留边界清偿：⑧ 泛型宿主虚派发链
  挂起点——臂条件 type.is 扩展为「模板空壳 + 模块内全部闭合
  构造 sheet」OR 链（泛型实例头是构造 sheet，其基链不含模板
  空壳，单模板键判定恒 miss；开放占位 new 的实例携模板空壳
  故模板键保留首位）；tainted 实现的类级 typeid 落参改从接收
  者实例隐藏 typeid 字段（#..generic.）运行期读取（class 宿主
  对象头恒藏构造实参 typeid，静态构造形态被接收者 cast 剥成
  裸模板亦不影响）。⑨ $$call/invoke.indirect——callable 协议
  闭包按②同机制动态分流（接收者 = CallTarget；静态目标 =
  BindIndirectCall 沿 extends 链解析的 $$call 成员）；闭包枚
  举改布局计划表直查 + PlanKey 派生判定（同名不同元数模板
  canonical 撞键——core::Func\<1\>/Func\<2\> 共享裸键，字符串
  查询口径实证槽表错配致闭包为空）。⑩ new.indirect × tainted
  class init——分发点本身成为调用方挂起点：实证 native 槽 0
  分发器不继承 init（派生类无自声明 init 时 new.indirect 抛
  NoSuchMethod，双端一致），故臂条件 = 精确 sheet 匹配
  （MirTypeCheckKind.IsTypeId 原值直判 ∧ 派生物化 sheet 排除
  链）；命中臂以臂构造形态空 init 分配（init.wrapper 原位缝
  合）→ Target 槽落定回存 frame → init frame 下钻，DONE 直落
  原后继；相关性按「站点静态实参形 ↔ 重载逐物化 sheet 代入
  形参」精确判定（argc + canonical 恒等，镜像分发器 ArgToken
  匹配语义）。⑪ MirNewObject 补 try 异常边（CallVisitors 降
  new 时填充 CurrentExcTarget——历史「pending 推迟到下一检
  查点」形态消除；init 下钻 FAILED 沿该 fn 的 try 异常边走，
  同 fn try 内挂起 init 抛出双端捕获点对齐；同步 init 抛出同
  获立即传播语义）。保留的受控拒绝边界（消息文本已改准确 +
  MiddlewareTests 负例钉住）：proxy/wrapper 烘焙链
  （$.wrapped./$.mwrapped./$mw. 前缀合成 fn）可达的 tainted fn
  ——router/trampoline 通配 ABI 的值包转发形态无挂起协议插
  点，运行期目标集随 wrapper 实例符号表动态决定，静态闭包不
  可枚举；含挂起点的值类型 init——值类型构造路径无挂起协议
  （frame .this 借用形态与 sret/原地构造不兼容；此前静态 new
  形态静默语义错位——native exit 5 无输出 vs VM 正常——补
  闸）；泛型占位实参的 new.indirect × tainted init（实参
  sheet 运行期物化，匹配不可静态判定）；实参与可见形参不对
  应的未知隐藏参数形态；嵌套占位构造的类级 typeid 实参。
- Alarm / 多 Worker / 取消入口随 MW11c 转向定稿（coroutine.c 内以
  「MW11c」标注加锁点，实体已按并发语义设计——C11 原子 CAS、Task 闸、
  Executor 锁）；按转向，这些 C 侧实体按本节首段保留/重构清单迁移进
  Rigi 世界。

**MW11b 已收口（中间形态：Alarm 族 + libuv 底座）**：

- **yield Alarm 全链**：lowering 直译 MirYieldAlarm（alarm 槽；运行时
  分类归面内 is 链，MIR 不区分 Polling/Event）→ CoroutineSplitPass 改写
  为存 frame + state=N + 两分类 sheet 经 getid.type 物化
  （`core.coroutine::PollingAlarm`/`EventAlarm`）+ MirYieldAlarmCall
  （alarm 槽地址 + 两 sheet + probe fn 地址四面实参）+ ret SUSPENDED；
  恢复块恢复活跃槽后落原后继**不重调面**（面内已登记 waiter/探测项，
  与 await 恢复块重回 wait 重调 MirTaskWait 的模式不同；EventAlarm 已
  触发时面内立即重发布，但当前执行段仍结束，对齐 VM YieldAlarm）。
- **probe 合成 fn**（`$mw.poll_probe`，模块级懒建一次，$mw.named.lookup
  先例）：MIR 签名 alarm 胖引用 → i32，本体 = PollingAlarm.isReady 虚
  派发 + bool 双分支转 i32（1 ready / 0 not）；isReady 抛异常走
  ExcTarget 进函数级传播垫，RcInjection 第二垫尾分叉（IsPollProbe
  标记，仿 IsCoroutineResume）= release 配平 + ret -1 + **pending 保持
  置位**（drain 探测轮 probe 返 -1 后 rigi_exc_take 取走，走 yield 点
  失败路径：Task FAILED → await 点重抛）。Emit 与 resume fn 共用
  `i32(ptr)` 签名特判机制；prologue 从 RigiFatRef\* 直接装载胖引用
  （保留实际 typeid 半，无需 sheet 重构）；alarm 参数借用约定（C 侧
  登记项持有 +1 至摘链，probe 不 acquire/release）。
- **可达性**：带 Alarm 的 yield 在 BIL 级收编 isReady 虚调用闭包
  （MirReachability：静态目标 + 全部 override 后代——probe 是 MIR 期
  合成，BIL 可达性不可见）；两分类 sheet 由 Layout 全类型计划覆盖，
  无需显式收编。
- **drain×uv 集成**（rigi_rt alarm.c/.h + coroutine.c）：uv loop 懒建
  （首个 alarm 需求时，普通程序零 uv 开销）；sleep EventAlarm 内部
  子类 sheet 由运行时合成（stdlib EventAlarm abstract 无构造入口），
  首次 rigi_yield_alarm 时懒补 baseTypeId 供用户侧 is 链；EventAlarm
  闸内「检查 signaled + 挂起 + 登记」原子握手（RUNTIME §19.3）；drain
  队列空转点先探测一轮 PollingAlarm，仍空则 UV_RUN_ONCE 阻塞至最近
  定时器（sleep 到期/探测退避唤醒），uv_loop_alive 为假不阻塞防空转；
  探测退避 1→32ms 指数（对齐 VM TakePollDelay），退避 timer 回调只
  承担唤醒，isReady 一律 drain 线程执行（callback 不执行用户代码）。
- **probe 失败路径 frame 释放**：协程 Suspended→FAILED 不再恢复，
  resume fn DONE 出口（frame 最终 release 点）永不执行——由探测轮
  失败分支代为最终 release（memtrack 抓获的 32B 泄漏修复）。
- 多 Worker / 取消入口随 MW11c 转向一并在 Rigi 世界定稿（见本节首段）。

## 7. 泛型、调用与 ABI

- reified 泛型：共享代码体 + typeid 隐藏参数（RUNTIME §1 既定取舍：不提供泛型
  热路径的单态化特化 pass）；typeid 参数的传递位置与求值顺序在 ABI 定稿；
- vargs/kwargs：包 = 单值胖引用，按 §7.2 序按位传递，无 shim——frontend
  打包（逐元素 BoxToAny），Middleware 直译；
- `invoke fn(..super)` → 直接基类原始实现；`..create` 仅属 Middleware/VM 生命
  周期阶段；
- `invoke.indirect` → callable 协议（`$$call` 虚调用；async `$$call` 同槽，结果为 `Task`/`Task<T>`，CoroutineSplit 改写目标 stub）；
- native 函数：直接生成对原生符号的调用——rigi_rt 面 C 名 = `rigi_` + symbol；
  非 rigi_rt 用户库（L6 起）C 名 = symbol 原文，链接输入经 `native --link`
  追加（RUNTIME §26 库解析留白的定稿）；返回用户引用类型的 FFI ABI
  在此定稿（SYNTAX §4.6 / RUNTIME §26 的留白）。

**String ABI（MW7 定稿）**：不可变值类型，槽仍为 `{ i8* data, i64 len }` UTF-8。
字符数据是 ARC 计数缓冲：堆块 `{ atomic u32 rc, u32 reserved, data[] }`，槽内
`data` = 块基址 + 8；字面量永生 `rc = 0xFFFFFFFF`（acquire/release 均跳过）。
可观察语义仍为按值深拷贝；native 计数对用户不可见。Rigi 内部按值传
`{i8*, i64}`；C 边界（native 面与运行时面）一律经 `rigi_string*` 传递
（StringOut 出参置首参），规避 16 字节 struct 按值传递的 win-x64/SysV ABI
分歧。Nullable 统一 tag0（内联空/值）/ tag1（堆值）分派，与其它值类型同一套
胖引用槽，不另开 String 特例。

**.typeid 构造 sheet（MW8c-1 定稿）**：`.typeid<X>` 是存储目标 TypeSheet* 的
值类型（Rigi 投影 `Type<T>`），按构造 canonical（`core::Type<X>`，无界 =
`core::Type<core::Any>`）各自出 TypeSheet/TypeInfo，不再擦除单键。形态：
typeSize 8、FlagInlineValue、refMap/iMap/vTable/wrappers/ifaceClosure 皆空。
收集点含 fn 局部、getid/cast、type.is 静态目标；core::Type 无 stdlib 声明，
按 Span 先例合成。VM 对 `Type<X>`/`Type<Any>` 不变（无协变）→ `baseTypeId`
不指向无界成员。另内建 `.null` sheet（typeOf(null) 实际类型，typeSize 0）。

**typeid 与 C 边界胖引用**：BIL §7.2 六段序（`.this` → 固定泛型 typeid → 泛型包
→ 普通参数 → 值包 → 具名包）即泛型调用约定；typeid 的 LLVM 表示 = TypeSheet
指针（`getid.type` 物化）。String 与 16 字节胖引用的 C 边界一律 out 首参（Rigi
内部按值）：String 经 `rigi_string*`，用户引用经 16 字节对齐的胖引用槽指针。
RUNTIME §26 的调用约定留白在 x64 双平台上天然唯一（Win x64 / SysV AMD64）——
clang 编 `rigi_rt` 与 LLVM 生成代码各自 lowering 一致，无需显式 fastcall 标注。

**构造类型具化计划**：从模块收集闭合构造类型（new / fn .vars.args / getid.type
与 cast 目标 / 数组元素 / 代入后的 extends·implements / 内层构造实参；
MwTypeKey.Normalize 归一，环保护）。具化计划独立入表：字段复用模板
canonical（`.generic` → 16B 胖值槽入 refMap，具体类型照旧）；vtable 槽 =
模板 fn canonical；基类链沿代入后的构造基类递归。iMap 按代入后的构造接口具化生成（接口 sheet 引用具化接口空壳 sheet）。
**泛型值类型（struct/enum struct）同法具化**：字段/vtable 槽 0/refMap/enum
判别表复用模板计划（占位字段恒 16B 胖值槽，构造与模板布局同构），无对象头
隐藏 typeid 槽、无 iMap（值类型不参与虚/接口派发）；ifaceClosure 按构造
canonical 重生。构造 interface/wrapper 的 new 保留受控拒绝（语言层非法，
P3 已拒；且 native 对「无 init 声明 + 零实参」构造本就整体受控拒绝）。

**类级 typeid ABI（被调方自取 / 值类型直传对偶）**：泛型类在 16B 对象头之后、用户字段之前
为每个类级类型参数留 i64 TypeSheet 指针槽（继承时基类隐藏字段在前；
typeid 不是托管引用，不进 refMap）。`new` 站在 rigi_alloc 之后把构造实参
的 TypeSheet（或当前 fn 的 `.generic.*` 局部）写入隐藏字段，再调
`..init.wrapper` / init——init 实参不传类级 typeid。实例方法（含 init）
的 LLVM 调用约定剔除类级 `.generic.X`；entry prologue 从 `.this` 隐藏字段
装入该局部。方法级 typeid 仍由调用点按 §7.2 序物化传递。
**泛型值类型宿主无对象头可藏**：其实例成员 fn 的类级 `.generic.X` 参数
**保留在 LLVM 调用约定内**（.this 槽指针之后、普通参数之前），调用点按
接收者/构造目标的构造形态代入直传——闭合实参 = TypeSheet 常量，外层占位
= 当前 fn 的 `.generic.*` 局部；frontend 对方法接收者的「构造 → 裸模板」
擦除 cast 由 MIR 构建期溯源（FlowBuilder._erasedValueHosts）回解构造形态。
值类型静态成员无类级 typeid 实参（SYNTAX §9.2.3 本就不用；BIL 仍声明的
形参槽落 core::Any sheet 常量，对齐 VM AlignGenericHiddenArgs 缺省 .any）。

**泛型占位操作数运算（G4 定稿）**：`T extends Bound` 内的 `a + b` 族
（操作数静态类型本身为顶层 `.generic<...>` 占位）直译为 MirGenericBinaryOp /
MirGenericUnaryOp，发射期运行期派发（GenericOpEmitter，VM ExecuteBinary
同口径）：内建标量/String 按实际 sheet 逐臂求值优先；否则按左操作数实际
typeid 经 rigi_type_is 逐候选臂判定（候选 = 模块内全部同名 operator，
派生深度降序；普通形参再经右臂 type_is 校验）；!= 调 equals 取反、
</<=/>/>= 调 compareTo 按 ComparisonResult 判别映射 bool。全落空抛
core.NoSuchMethodException（VM VmException「没有用户 operator …」对应面）。
已知构造宿主即使含嵌套开放实参，也走普通 operator 解析、可达边和调用路径。
泛型 class 候选按已具化的闭合宿主 sheet 分臂，普通形参类型同步代入；类级
typeid 由 receiver 隐藏槽恢复，不把裸模板当作对象的构造身份。
边界：泛型值类型宿主的 operator 候选编译期受控拒绝；方法级泛型 typeid
注入仅支持普通形参恰为占位的精确形态，其余
注入 core::Any（VM 推断失败缺省同口径）；Entity wrapper 的 operator
代理链不经此面；接口声明的 operator 不在候选集（VM FindOperator 的宿主
集只含 extends 链，同口径）。

**TypeSheet 全局名**：无角括号的既有名保持不变；构造 canonical 的 `<,>`
转义为 `$` / `.`（空格删除），避免跨工具链引号差异。

**is / supers / with（MW5 c3）**：MIR 直译 `type.is` / `type.supers` / `type.with`（含 `.indirect`）为 `MirTypeCheck`；发射调 `rigi_type_*` helper（typeid+payload 两枚 i64 + 目标 TypeSheet*，返 i32 0/1）。实际类型：tag2 取对象头 TypeSheet，tag0/tag1 掩码胖引用 typeid。TypeInfo 形态 `{name: rigi_string, sheet*, wrappers**, wrapperCount, ifaceClosure**, ifaceClosureCount, nullableElement*, typeIdBound*, destroyNative(void*)}` 与 TypeSheet 成对发射，`typeInfoId` 回指；wrappers 来自声明 `BilWrappedModifier`；ifaceClosure 为传递 implements 闭包（含接口的父接口）。泛型占位目标（`.generic<$.generic.T>`）降为 typeid 局部（与 `.indirect` 同 helper）。接口默认方法（有 fn 体）进入实现类 iMap 槽（未 override 时指向接口方法）；MirReachability 补默认方法可达边。

**Any/Box ABI（RUNTIME §2/§4 定稿）**：胖引用 128-bit = `{typeid: i64（最高字节
tag）, payload: i64}`，16B 对齐。tag0（ValueType ≤8B）payload 内联值；tag1
（ValueType >8B，含 string 的 `{i8*,i64}` 与大 struct）payload = `rigi_malloc`
+ memcpy 的 unique 裸数据块（无对象头、无块内 typeid）；tag2（Object）payload =
对象指针。装箱 = `cast` 值类型 → `.any`/`.object`；拆箱检查 tag 与掩码后
TypeSheet 指针，不符抛 `core.CastException`（MW9b-G 换真异常，经
ExceptionEmitter 共享抛出辅助：alloc + 真 init + `rigi_exc_raise` + 沿 MIR
异常边传播；fromType = 发射期静态源类型名常量、toType = 目标 sheet 运行期
显示名，拼写对齐 VM 消息口径）。native `.any` 参数/返回经
16B 对齐槽指针传递（D6：C 边界 16B 胖值一律指针）。`any_to_string` 经该槽指针
读 `{typeid, payload}` 分派（标量面 / String 拷贝 / TypeInfo.name）。Box 复制的
acquire/release 不在此发射（E4：内存正确性 MW7 RcInjection 统一收口）。

**动态 new（MW8b/MW8c 定稿）**：vtable 槽 0 为 per-类型 `mw.init.dispatch`（if 链比 argc 与
形参 TypeSheet*）；class thunk 胖返回（复用 EmitAllocAndInit）；struct thunk 为 sret
`void(ptr %out, fat...)`（零初始化 → 可选 wrapper → init 原地生效；内联槽直传 result alloca，胖槽按 typeSize 走 tag0/tag1）；
标量/String 零参 T() 在 vTable 查找前比对内建零值 sheet 直产零值（argc>0 落 miss）。
vTable/分发器/匹配落空抛 `core.NoSuchMethodException`（MW9b-G 换真异常；thunk
内部 miss 构造异常 + raise + ret undef，pending 由调用点检查接力）。可达性对 `new.indirect` 保守收模块内全部 init 族（含 stdlib `core*`；经该保守边引入且 MIR 不可构建的 init 族试探性跳过，运行期抛 NoSuchMethodException）。
MW9b-G 起三占位 abort 面（`rigi_abort_divided_by_zero` / `rigi_abort_invalid_cast` /
`rigi_abort_no_such_method`）与数组越界写 abort 面（`rigi_abort_array_oob`）已退场：
除零/越界写分别改抛 `core.DividedByZeroException` / `core.OutOfBoundException`
（守卫指令挂 MIR 异常边，core 异常类型 init 族 + getMessage 经
MirReachability 恒可达白名单保证可发射）；保留 `rigi_abort_arithmetic_overflow`
（i64 MIN/-1 基础设施溢出失败，VM 基准非语言级异常）与
`rigi_abort_array_negative_length`（分配负长度）。

**singleton 运行时（MW10 定稿）**：每个 singleton 类型合成三态取实例 fn
（合成静态槽 state：未构造/在途/就绪 + cache 胖引用槽；VM 的双表在 native
收敛为 state+cache 两槽，state==就绪 ⇔ cache 非空）；在途 = 构造环 → 抛
`core::RuntimeException`。全部 `new type(singleton)` 改写为 get 调用（实参
按 VM 口径丢弃——急切初始化保证用户代码执行时恒已构造）；无零参 init 者
合成空 init 凑齐构造尾，只有有参 init 者编译期受控拒绝（VM 急切初始化期
同口径必败）。`rigi_entry` 启动序：singleton 族急切初始化 →
`..globals.init` → main（此序经实证对齐 VM InitializeSingletons 启动序）；
并发 v1 简单标志（MW11 Worker 线程模型上线后复核原子性）。

## 8. 异常机制

**传输模型（MW9a 定稿）：checked-flag 便携异常传输，零平台 EH 指令。**
throw 不触发任何 unwind：异常对象 acquire +1 后写入线程局部 pending 槽
（`rigi_exc_raise`）；每个可抛调用返回后由生成代码查 pending
（`rigi_exc_pending`），非空即沿 MIR 异常边（ExcTarget）跳传播路径；捕获
点 `rigi_exc_take` 取走并清空槽（+1 所有权随返回值移交捕获方）。整条路径
只是普通调用 + 分支 + TLS 槽读写，win-x64/linux-x64 同一份实现。

**为何弃 landingpad/SEH（取舍记录）**：

- catch 匹配是 TypeSheet 的 `is` 判定（RUNTIME §12），不是 C++ RTTI 的
  type_info 匹配——平台 EH 的 catch 选择器语义与本语言对不上，personality
  里仍得自己跑 `is` 链，landingpad/SEH 只剩「找到 landing 点」一项职能；
- Rigi 调用的 native 中间帧只有 FFI 叶调用（无外来帧回调再入 Rigi 的栈
  形），unwind 无需穿越外来帧，平台 unwind 器的跨帧能力无消费方；
- ARC 配对在 MIR 层显式化（RcInjection 传播垫按 ret 出口同口径 release
  全部托管槽），异常路径的每次 acquire/release 可被 `RIGI_RT_MEMTRACK=1`
  台账全路径验证；平台 EH 的 cleanup 路径绕开 MIR，台账口径对不齐；
- 与协程挂起点天然统一：挂起/恢复点已是「调用返回后查标志位」形态，
  pending 检查复用同一 codegen 骨架，不引入第二套控制流；
- 双平台单实现：免去 Itanium landingpad 与 SEH catchswitch 两套发射与两
  个人格函数，NativeE2E 对拍覆盖面不因平台分裂。

MIR 保持 ExcTarget 双目标抽象（正常后继 / 异常边），checked-flag 只是
Emit 层的一种 lowering；未来若切原生 EH，改动封闭在 Emit（调用点改
invoke、传播垫改 landingpad/catchswitch），MIR 与 RcInjection 不变。

**MIR 构件（MW9a）**：

- `MirThrow`（指令）：throw 语句本体——RcInjection 配平后 raise 并入
  pending，随后沿异常边传播；
- `MirTakePending`（指令）：派发垫首指令，`rigi_exc_take` 出异常对象写入
  合成局部 `$mw.exc.N`；
- `MirRetThrow`（终止符）：传播垫出口——release 配平后返回调用方，
  pending 槽保持置位（checked-flag 跨帧传播的最后一棒）；
- `ExcTarget`（MirCall / MirInvokeIndirect / MirThrow 的异常边字段）：本
  词法上下文的异常落点，由 TryExpander 按 BIL §16.7 十步语义解析；为
  null 时由 RcInjection 改写指向函数级共享传播垫 `mw.propagate`。
  MW9b-G 扩面到守卫型可抛指令（MirBinaryIntrinsic 整数除零 /
  MirCast / MirUnboxAny / MirGetField 拆箱守卫 / MirSetArray 越界写 /
  MirNewIndirect 无匹配 init）——守卫命中由 ExceptionEmitter 共享抛出
  辅助构造真异常（alloc + 真 init + `rigi_exc_raise`）后 br 进同一
  异常边，与用户 throw 同路；
- 派发垫 `mw.try.N.dispatch`：try 入口侧——TakePending 后按 catch 表序
  走 `is` 链（表序即匹配序，保序语义），命中进 catch 前置垫，未命中走
  finally/外层；
- finally 单块双入口 + completion 路由器：一切离开 try 的 completion
  （normal / return / break / continue / throw）经前置垫记路由码，finally
  体执行后由路由器按码续解析落点；`finally(e)` 的 e 仅 Throw completion
  时写入异常对象，其余 completion 一律见 null（BIL §16.7）。

**顶层 reporter（MW9a 第 C 棒）**：`rigi_entry` 返回后 pending 非空即未捕
获异常——`rigi_type_name_of` 取诊断名 + 虚派发 `getMessage()`，stderr 打
印 `{类型全名}: {message}`，`rigi_exc_halt` 收尾 exit 1。

**全局异常通道 carve-out**：RUNTIME §25.2 undisposed-resource 等不绑定用
户调用栈的事件不经 checked-flag、不可 try/catch，走
`core.GlobalExceptionHandler` API 通道（**MW12b 已定稿**：API 在
stdlib/core/global_exceptions.rg，register/dispatch 静态二面 +
UndisposedResourceException；事件队列与处理器注册表沉 rigi_rt gexc.c；
entry stub 在 main/drain 后、失败汇总前循环 `rigi_gexc_take` 统一派发；
晚到事件——globals_cleanup 与 GC 终轮收集阶段入队——不经用户处理器，
由 `rigi_gexc_flush_default` 在 atexit 打印默认文本）。

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
   恰好配对）。rigi_rt 内置台账（`RIGI_RT_MEMTRACK=1`）为跨平台零泄漏口径，
   NativeE2E 全用例开启（泄漏即产物进程 exit 1）。
4. **黄金 .ll 快照**：黄金 BIL 用例的 PrintModule 产物快照比对，防发射回归。
5. **工具链契约测试**：LLVMSharp 绑定与 libLLVM 20 原生包的版本对齐冒烟、
   lld 链接冒烟，随 CI 跑（win-x64/linux-x64）。

## 11. 代码组织

```text
Middleware/                 # 本仓库顶层目录（C#，.NET 10 LTS）
├── MwContext.cs            # 会话中枢（每模块一个，贯穿各层，逐层挂载产物）
├── MwNotSupportedException.cs # 未覆盖功能的统一内部异常
├── Gate/                   # BilReader 接线 + BilVerifier 门禁（多文件经 BilModuleMerger 合并）
├── Symbols/                # MW 符号图 / 类型表（canonical intern 驻留）
├── Binding/                # 实现绑定（ImplBinding 记录族 + ImplBinder 唯一实现查询）
├── Mir/                    # MIR 模型 + MirBuilder 瘦驱动（BIL→MIR 翻译 pass）+ FlowBuilder 组合根 + MirLowerDispatchers 唯一 switch + 簇 CRTP（ControlFlow/Call/Data/TypeOps；MW11a CoroutineVisitors：await/yield 直译；MW11b 增 MirYieldAlarm 直译）+ MirReachability + TryExpander.cs
├── Pipeline/               # IMwStage + MwPipeline 驱动器（线性阶段序；翻译 pass = visitor + 唯一 Dispatcher + 处理类）
├── Passes/                 # MIR 改写 pass（IndexOperator / Accessor：内部类隔离各 case，默认非 CRTP；MW10 wrapper 烘焙五 pass——FieldProxyBaking / MethodProxyBaking / ProxyBaking / CallWildcardLowering / SingletonLowering + 共享设施 ProxyBakeSupport / ProxyWildcardAbi；MW11a CoroutineSplitPass：async fn 状态机改造 stub+resume；MW11b 增 MirYieldAlarm 切分 + $mw.poll_probe 合成；RcInjection：CFG 分析内核，非逐指令翻译）
├── Layout/                 # LayoutEngine 瘦驱动 + ClassLayout / ValueTypeLayout / VTablePlanner / RefMapBuilder / ConstructedLayout / LayoutShells / HiddenStoragePlanner / WrapperAbi；MW11a SyntheticTypePlanner：协程 frame 合成类型通道（pass 期注册 Symbols + Layout 计划表）；TypeLayout：canonical → LLVM 类型唯一映射点（引用槽按 RUNTIME §2 胖引用 128-bit/16 字节对齐建模）+ 数组前缀 ABI；TypeSheetAbi / CallAbi：TypeSheet 字段序与调用约定描述符。非翻译 visitor
├── Emit/                   # ModuleBuilder 瘦驱动（MIR→LLVM 翻译 pass；rigi_entry 合成 = VM BilVm.Run 语义：singletons/globals.init/main → Dispatcher.workerLoop → 失败汇总）+ LlvmEmitEnvironment/Context 组合根 + LlvmEmitDispatchers 唯一 switch + 簇 CRTP（*Emitter；new 归 NewEmitter，native 归 NativeCallEmitter，虚/接口归 VirtualCallEmitter，getid 归 TypeIdEmitter，Nullable 归 NullableEmitter，MW11c 协程三指令归 CoroutineEmitter）+ LlvmBitcode / ObjectEmitter
├── Toolchain/              # ToolchainResolver（--toolchain → RIGI_LLVM → tools/.llvm/<rid> → PATH）/ ExternalProcess 外部进程封装
├── Runtime/                # RigiRtBuilder：rigi_rt 源 EmbeddedResource 内嵌 → 内容哈希缓存 → clang -emit-llvm -c 编成 bitcode（unity build）
└── Cli/                    # native 驱动（--file/--out/--emit-obj/--emit-ll/--toolchain/--libuv-dir/--link；L6 起 --link 追加非 rigi_rt 库链接输入）

rigi_rt/                    # 本仓库顶层目录（C，EmbeddedResource 内嵌，clang 现场编 bitcode 合并进模块）
├── shim.c                  # MW1 最小面：rigi_string {data,len} UTF-8 / rigi_print / rigi_print_err / rigi_string_concat / main → rigi_entry
├── string_rc.c             # String ARC：块头 rc + data=块+8、IMMORTAL 字面量、region 自包含
├── span.c                  # MW7b：rigi_span_alloc，复用 alloc_contiguous（数组同构布局）
├── memtrack.c              # 台账：RIGI_RT_MEMTRACK=1 时跟踪 malloc/free，退出未清零 exit 1
├── arc.c/.h                # microGC / microSGC、值语义四面族、region 协议、对象头
├── macrogc.c/.h            # MW12 Bacon-Rajan 三阶段收集器（显式 trace 栈）、候选账本、
│                             #   GC 常驻线程 + fence（region_enter 双重检查）+ 阈值/终轮兜底；
│                             #   诊断 env：RIGI_RT_GC_THRESHOLD/OFF/TRACE
├── gexc.c/.h               # MW12b §25.2 全局异常通道：undisposed 事件队列 + 处理器注册表
│                             #   + atexit flush（drain/dispatch 晚到规则）
├── coroutine.h             # MW11c 瘦身：RigiFatRef / RigiResumeCode 共享 ABI 类型（旧 C 调度面已删）
├── cohandle.c/.h           # 协程句柄原语：create/resume/destroy + lane + PollingAlarm 轮询状态
├── worker.c/.h             # Worker 原语：OS 线程/入队/park/同步 Mutex/定时器/TLS/主 Worker 收尾
│                             #   + L8 rigi_event_create_sticky（用户 EventAlarm 直继子类默认底座：
│                             #   粘滞形态，signal 恒置已触发并归还 armed，幂等；stdlib
│                             #   EventAlarm.ensureHandle 懒建，yield 分流改经 ensureHandle 取柄）
├── failreg.c               # 未观察失败注册表（Task 失败异常 native 承载；shared class 不得持 local Exception）
└── eh.c/.h                 # MW9a checked-flag 便携异常传输：TLS pending 槽三面（rigi_exc_raise/pending/take）+ 顶层 reporter（rigi_type_name_of/rigi_exc_halt），不使用平台原生 EH
```

## 12. 阶段划分

每阶段以可验证产物收口；VM 对拍与全量测试为通用验收。

| 阶段 | 内容 | 收口 |
|---|---|---|
| MW0 | 工程骨架：Gate（Reader + Verifier 门禁）、CLI、符号表、LLVMSharp 接线 | 非法 BIL 被拒；空模块进程内出 .o |
| MW1 | 最小垂直切片：标量 + String（内建 `+` → `string_concat` 面）+ native print + main → LLVM 模块 → .o → lld 链接最小 rigi_rt | hello world 与字符串拼接；VM 对拍打通 |
| MW2 | 标量与运算全量（BIL §11/§12、Resources 常量池） | 对拍套件 |
| MW3 | 控制流（if/loop/switch → CFG；alloca + mem2reg） | 对拍套件 |
| MW4 | 对象系统 I：布局（含胖引用槽与 access helper 面——C 编写、bitcode 合并、LLVM 内联）、字段、new、static | |
| MW5 | 调用与 ABI：invoke 族、typeid 隐藏参数、vargs/kwargs、FFI | |
| MW6 | 元数据与派发：TypeSheet/vtable/iMap/refMap、虚调用、interface | |
| MW7 | 值语义运行时 + ARC：Box 物化、RcInjection、region 协议发射（**MW7a 已收口**）；Span 物化（**MW7b 已收口**：Span=内建 class 定稿） | MW7a/MW7b：NativeE2E 全绿且台账零泄漏 |
| MW8 | 泛型运行时：`Type\<T\>`/typeOf/new、is/supers/with/cast（**MW8a/MW8b 已收口**；**MW8c 推进中**） | MW8a typeid 装箱；MW8b 动态 new 槽 0 分发器；MW8c-1 `.typeid<X>` 构造 sheet；MW8c-2 泛型占位 cast + 数值/String/struct 转换；MW8c-3 struct sret thunk + 标量零值 T() + VM 无匹配 init 必抛 |
| MW9 | 异常：try/catch/finally → checked-flag 便携传输（§8；**MW9a 已收口**：rigi_rt eh 三面 + reporter、MIR 构件（MirThrow/MirTakePending/MirRetThrow/ExcTarget）、TryExpander 十步展开、RcInjection 传播垫、Emit pending 检查、NativeE2E 捕获型对拍 13 例；**MW9b 已收口**：内置异常 message 模板源码化（stdlib init 重载）+ VM/native 构造全走真 init、VM 顶层格式对齐 `{类型全名}: {message}`、三占位 abort + 数组/Span 越界写 abort 全转真异常（守卫指令 ExcTarget 扩面 + ExceptionEmitter 共享抛出辅助 + core 异常恒可达白名单；除零策略换 Throw 实现）、SYNTAX §8.1 加第 6 异常类 `core.OutOfBoundException` + §8.2 未捕获进程行为） | 异常对拍套件 |
| MW10 | wrapper 烘焙全链（**已收口**：Entity/Value/Method 三类 proxy 链 + wildcard router 与 call??? 降级 + singleton 三态 get fn 与急切初始化，§5/§7） | |
| MW11 | 协程：状态机、Executor/Worker、Alarm（libuv 时钟底座）、Task、eager spawn。**MW11a/b** 曾以 C 侧重实现收口中间形态（单线程 drain + Alarm waiter）。**MW11c**：架构转向落地——Dispatcher/Task 调度在 Rigi 世界，rigi_rt 瘦身为 Worker/协程句柄/定时器/同步 Mutex/TLS/失败注册表原语；冷 Task、TaskState、executor 换绑、多 Worker 懒起、Timer/`sleep`、语言级 Mutex（VM 方法 hook；native 由 CoroutineSplit 改写 `enter` + Rigi `release` 真体，判定在 `tryEnter`/`releaseNext`） | ASYNC §8 集成测试 |
| MW11d | 序列化 + 消息全链（**已收口**）：`core.serialization`（@Serializable/@SerializationBase/@Temporary/@Terminal 四修饰器 + Parcel + toParcel/fromParcel/deepCopy 合成，SYNTAX §20）；纯 Rigi MessageQueue 五 API + 安全 AtomicList/Mutex + capability 矩阵 + EOS/broadcast/深复制双端对拍；Reader/Receiver/Messenger 高层 API + listener 身份（安全 Place 对象身份）+ Executor 路由（默认 IOExecutor），RUNTIME §27。泛型基建两处顺手补齐：闸门 2 裸 GP 实参与 receiver/类型实参同口径声明侧跳过；裸模板 new 隐藏 typeid 取当前 fn 的 `.generic.*` 局部 | BilVm Messaging 电池 + NativeE2E 对拍 + e2e 负例 |
| MW12 | macroGC：收集器、候选账本、GC 协程（native 常驻专用线程承载）、fence 激活、§25 检查（**已收口**：MW12a 收集器+fence 上线 / MW12b §25.2 全链 + GlobalExceptionHandler / MW12c 循环回收套件，全量 59 套件 0 failed） | NativeE2E mw12c 循环回收 9 例 + mw12b 4 例（含双宿主对拍）；顺带清偿两个既有 bug：FieldEmitter 借用字段读侧 ARC 失衡、Emit 临时 alloca 落非 entry 块栈泄漏（BuildEntryAlloca 统一） |
| MW13 | 优化收尾（move/cursor、CellElim 激进化）、工具链捆绑与发布 | |

注：

- RcInjection 的 region 协议从 MW7 起随代码生成常驻（廉价无操作），MW12 只需
  上线收集器与挂起路径，codegen 零变化。
- 字符串插值在 BIL 之前已由 frontend 降级为 `toString` + String 内建 `+`
  （SYNTAX §3.8），不构成 Middleware 的独立工作项；MW1 只依赖 String 类型与
  内建 `+`。

通用原生资源所有权：TypeInfo.destroyNative 是无 GC fence 重入的资源终结槽。协程 Mutex、Task（含泛型）与 Dispatcher 按真实布局生成 gate 清零/释放回调，ARC 与 macroGC 白色清理均执行；同步锁登记册支持退出清扫后的迟到终结。Mutex.Lock 强持属主直到令牌自身回收。shared RC 减量与非零候选登记在同一账本锁内，最后释放在锁外递归析构；颜色/索引 CAS 保留并发 dispose 位。
