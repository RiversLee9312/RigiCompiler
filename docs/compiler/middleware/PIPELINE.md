# Middleware 管线、绑定与代码组织

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

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

- **WrapperBaking 群**（§5；插槽在 AccessorLowering 之后、
  RcInjection 之前，五个小改写 pass——FieldProxyBaking → MethodProxyBaking
  → ProxyBaking → CallWildcardLowering → SingletonLowering，共享设施
  ProxyBakeSupport / ProxyWildcardAbi）
- **RcInjection**（§4.2，安全攸关）
- **CoroutineSplit**（§6）
- **CellElim（预留，当前默认管线未注册独立 pass）**：`.cell<T>`/`.readonly_cell<T>` 特权拼写识别（BIL §6 约定），
  消除可证的 cell 间接
- **Devirt（预留，当前默认管线未注册独立 pass）**：静态可证的虚调用 → 直接调用

### MW5 布局与 ABI

字段偏移、对齐、Box 物理形态、TypeSheet/vtable/iMap/refMap 的发射计划、调用
约定（typeid 隐藏参数位置、胖引用传参与返回、sret、vargs/kwargs 包形态）。
布局决策在此定稿，MW6 只消费。ABI 常数落点：数组前缀 32B 在 `TypeLayout`；
TypeSheet/TypeInfo 字段序在 `TypeSheetAbi`（镜像 `arc.h`）；C 边界 out 首参
与内联值类型返回形态在 `CallAbi`。Emit 只填 LLVM 类型、函数指针与常量。

### MW6 LLVM 模块构建与发射

MIR + 布局计划 → LLVM 模块（LLVMSharp 进程内构建）。进程内完成
`LLVMVerifyModule` 校验、新 PM 字符串管线优化（`LLVMRunPasses`）与目标文件
发射；.ll 文本经 PrintModule 产出，作调试与黄金快照产物。外部步骤包括 clang 编译 rigi_rt、clang 驱动 lld 链接 CRT；静态库产品另由
archiver 归档。Emit/工具链持有外部资源，由 CLI 尾部编排，不注册为 IMwStage。

---

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
├── Passes/                 # MIR 改写 pass（IndexOperator / Accessor：内部类隔离各 case，默认非 CRTP；MW10 wrapper 烘焙五 pass——FieldProxyBaking / MethodProxyBaking / ProxyBaking / CallWildcardLowering / SingletonLowering + 共享设施 ProxyBakeSupport / ProxyWildcardAbi；MW11a CoroutineSplitPass：async fn 状态机改造 stub+resume；MW11b 增 MirYieldAlarm 切分 + $mw.poll_probe 合成；BuiltinToStringDispatch / WrapperSelfParameter；RcInjection：CFG 分析内核，非逐指令翻译）
├── Layout/                 # LayoutEngine 瘦驱动 + ClassLayout / ValueTypeLayout / VTablePlanner / RefMapBuilder / ConstructedLayout / LayoutShells / HiddenStoragePlanner / WrapperAbi；MW11a SyntheticTypePlanner：协程 frame 合成类型通道（pass 期注册 Symbols + Layout 计划表）；TypeLayout：canonical → LLVM 类型唯一映射点（引用槽按 RUNTIME §2 胖引用 128-bit/16 字节对齐建模）+ 数组前缀 ABI；TypeSheetAbi / CallAbi：TypeSheet 字段序与调用约定描述符。非翻译 visitor
├── Emit/                   # ModuleBuilder 瘦驱动（MIR→LLVM 翻译 pass；rigi_entry 合成 = VM BilVm.Run 语义：singletons/globals.init/main → Dispatcher.workerLoop → 失败汇总）+ LlvmEmitEnvironment/Context 组合根 + LlvmEmitDispatchers 唯一 switch + 簇 CRTP（*Emitter；new 归 NewEmitter，native 归 NativeCallEmitter，虚/接口归 VirtualCallEmitter，getid 归 TypeIdEmitter，Nullable 归 NullableEmitter，协程专用节点 Create/FailureLoad/Done/ResumeCall 归 CoroutineEmitter）+ LlvmBitcode / ObjectEmitter
├── Toolchain/              # ToolchainResolver（--toolchain → RIGI_LLVM → tools/.llvm/<rid> → PATH）、LibuvResolver、MimallocResolver、ToolchainIdentity、NativeLinkRecord、LinkerThreads、ExternalProcess
├── Cache/                  # ArtifactCache / NativeObjectIdentity：完整目录发布、稳定锁与对象身份
├── Runtime/                # RigiRtBuilder：rigi_rt 源 EmbeddedResource 内嵌 → 内容哈希缓存 → clang -emit-llvm -c 编成 bitcode（unity build）
└── Cli/                    # native 驱动（--file/--out/--emit-obj/--emit-ll/--toolchain/--libuv-dir/--mimalloc-dir/--link；L6 起 --link 追加非 rigi_rt 库链接输入）



```

### 当前默认阶段序与读写边界

`MwPipeline.CreateDefault()` 的顺序是：`LayoutStage` → `MirBuildStage` →
`IndexOperatorLoweringPass` → `AccessorLoweringPass` → `FieldProxyBakingPass` →
`MethodProxyBakingPass` → `ProxyBakingPass` → `CallWildcardLoweringPass` →
`SingletonLoweringPass` → `BuiltinToStringDispatchPass` → `WrapperSelfParameterPass` →
`CoroutineSplitPass` → `RcInjectionPass`。MW1–MW6 是职责标签，不是这个方法的执行序。

初始 Layout 只依赖符号图，必须先生成 vtable/iMap 计划，供 MirReachability 求解派发
闭包；MIR 改写之后合成的 coroutine frame 等通过 SyntheticTypePlanner 注册进 Symbols
和 Layout。WrapperSelfParameter 在切分前统一 wrapper self 参数形态；全部新分派、
烘焙体和状态机随后统一接受协程分析与 ARC，禁止越过 RcInjection 追加未配平体。

相关章节：[wrapper](WRAPPER_BAKING.md)、[协程](COROUTINE_LOWERING.md)、[ARC](MEMORY_MANAGEMENT.md)、[LLVM/runtime 构建与缓存](TOOLCHAIN_AND_CACHE.md)。
