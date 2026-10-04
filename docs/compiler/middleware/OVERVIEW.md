# Rigi Middleware 架构（BIL → 原生可执行）

入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。原章节号用于与专项正文和源码注释对照。

> **定位**: 本文档规定 BIL → 原生可执行文件之间全部阶段（下称 **Middleware**）的架构：
> 技术选型、内部层次、内存管理、实现绑定、wrapper 烘焙、协程降级、异常机制、
> ABI 与布局、优化归属、验证方式与验证边界。
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
    ↓ MW5 初始布局与 ABI（TypeSheet/vtable/iMap/refMap、字段偏移、调用约定）
    ↓ MW3 MIR 构造（结构化块 → CFG；可达派发闭包消费布局）
    ↓ MW4 MIR pass 群（wrapper 烘焙 / singleton / 内建分派 / 协程切分 / ARC）
       合成类型经 SyntheticTypePlanner 同步扩充符号与布局计划
    ↓ MW6 LLVM 模块构建（LLVMSharp 进程内）
    ↓ 进程内 verify / 优化 / 目标文件发射；rigi_rt 经 clang 编成 LLVM bitcode，
       进程内合并进模块参与统一优化
    ↓ clang 驱动（-fuse-ld=lld）链接 CRT
原生可执行文件
```

核心决策（修改须重新过一遍取舍）：

独立模块的私有 canonical 后缀是链接身份，保留名识别使用 `BilLogicalName`。
Wrapper proxy 匹配只在目标 wrapper 的实例成员及 proxy-kind 修饰符内进行，返回原
canonical；普通符号索引仍精确匹配。标准库协程机制通过可信接口转递的
`compiler.runtime.<逻辑完整 ABI 的 SHA256>` 绑定解析私有 Dispatcher、原语、方法和
字段。前缀协议只筛这些准确绑定，检查摘要键及唯一性，再查询完整 canonical；不能
从普通声明中挑选同逻辑名。无模块绑定的低级编译保留原精确拼写。VM 与 Native 的
singleton、调度、Task 字段和退出 drain 共用此绑定规则。


1. **实现语言 C#（目标平台 .NET 10 LTS），复用 `Bil/` 生态**。Reader、对象模型、
   Verifier 已在仓库内且对中端零依赖；Middleware 单向依赖 `Bil/`，不回依赖中端。
   项目以 BCL 为基础；后端豁免 LLVMSharp/libLLVM，模块配置另使用 YamlDotNet，
   native 运行时静态链接 libuv/mimalloc；依赖纪律见 [开发指南](../../../DEVELOPMENT.md)。
2. **经 LLVMSharp 进程内构建 LLVM 模块，锁定 LLVM 20**。项目绑定层与 native runtime 包固定为
   20.1.2，libLLVM 原生库经 NuGet runtime 包按 RID 分发（win-x64/linux-x64
   均有），进程内 IR 构建与对象发射不要求外部工具链；校验、新 PM 优化管线、目标文件发射全部进程内完成，
   保留的外部进程只有 clang（rigi_rt 现场编译 + 驱动 lld 链接）。.ll 文本仍由
   PrintModule 产出，作调试与黄金
   快照产物。代价：以锁版换绑定可用性，升级须同步校验绑定与原生包；NativeAOT
   发布形态为「AOT exe + libLLVM 边车」。
3. **语言语义 pass 自做，通用优化全交 LLVM**。类型驱动操作绑定、wrapper 烘焙、
   ARC 注入、协程状态机、布局与 ABI 决策在 MIR 层完成；SSA 提升、常量传播、
   GVN、LICM、内联、向量化交给 LLVM 新 PM 管线。参照 Swift SIL / Rust MIR 的
   归属边界。
4. **无 GC roots、无 shadow stack、无 LLVM statepoint**。内存安全完全由编译器
   生成的 ARC acquire/release 调用保证；macroGC 是候选驱动的循环收集器
   （Bacon-Rajan），从 release 路径登记的候选出发做图染色，从不枚举栈/全局根
   （§4）。
5. **ARC、macroGC 与原语沉在 C 库 `rigi_rt`**：内存会计、GC、Worker、协程句柄、
   Alarm/定时器、同步 Mutex/TLS、失败注册表和 native shim；Task/Dispatcher/Executor
   调度策略位于 Rigi 标准库。生成代码通过普通 Rigi 调用与最小原语共同交互；rigi_rt
   经 clang 编成 LLVM bitcode 进程内合并进模块（§2），运行时面随统一优化
   管线内联。

---


## 9. 优化 pass 归属表

| pass | 归属 |
|---|---|
| 类型驱动操作绑定 / devirt | Middleware（MW2/MW4；独立 Devirt pass 为预留方向） |
| wrapper 烘焙 / cell 消除 | Middleware（独立 CellElim pass 为预留方向） |
| ARC 注入与 region 划分 | Middleware |
| 协程状态机 / EH 展开 | Middleware |
| 布局 / ABI / 元数据发射 | Middleware（决策），LLVM（消费） |
| 不可观察 copy/Box/temp 消除 | Middleware（语义可证者）+ LLVM |
| SSA 提升 / SROA | LLVM（mem2reg 等） |
| 常量传播折叠 / GVN / LICM / DCE | LLVM |
| 内联 | LLVM（Middleware 可以 BIL §18 hint 表达建议） |
| 向量化 / 目标相关优化 | LLVM |
| 别名/只读标注生成 | Middleware 生成（noalias/readonly 等），LLVM 消费 |

相关章节：[管线](PIPELINE.md)、[布局/ABI](LAYOUT_AND_ABI.md)、[工具链/缓存](TOOLCHAIN_AND_CACHE.md)、[内存](MEMORY_MANAGEMENT.md)。
