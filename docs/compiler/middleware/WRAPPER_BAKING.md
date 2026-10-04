# Middleware wrapper 烘焙

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。


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
- `..init.wrapper` 在实体 init 前自动调用；`..companion` singleton 壳体（声明类中的确定性嵌套名、无 UUID，BIL §8.7）随 singleton 急切初始化构造（§7）；
- inner 链接与可变泛型包的解包/shim 合成。
- **特化类型替换边界（槽/指令形态分裂陷阱，segvfix 实证）**：
  `ProxyBakeSupport.BuildSpecializedBody` 的类型代入只改写 `built.Locals`
  槽类型与返回类型，**不重写指令内嵌类型**（`MirGenericBinaryOp` 等自带
  `LeftType`/`RightType` 的指令按 BIL→MIR 转换期（`MirBuilder`）的模板
  占位类型残留）。模板期按占位展开、且发射期**按指令类型语义加载槽**的
  lowering 落进特化体即成「槽=具化类型、指令=占位」的分裂形态。已知裂
  口：同型 Nullable 判等全展开（`DataVisitors.NullableEqualityLowering`）
  对 `TField? != null`（`Temporary` proxy 的 `cached if? resumeStub()` 脱
  糖）产出占位 `MirGenericBinaryOp`，特化后配 `core::i32` 标量槽，发射
  期 `GenericOpEmitter` 的 `BuildExtractValue` 对非聚合在 LLVM 原生层
  SIGSEGV（编译器进程崩，无诊断）。两条防线：① nullness-only 形态
  （`x ==/!= null`，一侧为 null 常量物化局部，`FlowBuilder` 登记）不走
  展开——位比即语义（ImplBinder 的 Nullable 位比规则本就只服务该形
  态）；② `GenericOpEmitter` 发射前恒验操作数槽为 16B 胖聚合，形态不
  符响亮失败（`CompilerInternalException` 带函数与槽类型上下文）。新增
  「按占位生成、按指令类型消费」的 MIR lowering 时必须自查该组合。

### 路由类别与精确身份边界

当前 ProxyBakingPass.BuildRouter 已先枚举 FieldTargets 的 get/set 访问器 canonical，
再枚举实际有 MIR 原始体的非访问器方法/operator canonical；字段分支由
FieldProxyBakingPass.EmitRouterFieldBranch 处理，成员分支由 EmitRouteBranch 自
fromLayer 选择后续环或原始体。它不是只有方法类别的预留插槽，也不会把所有
get/set/operator 请求一律判 miss。字段候选只收宿主实例字段，跳过静态字段和
字段自身已有 Value wrappers 的字段，getter/setter 还须真实存在；这是 Entity
字段链与字段自身链短路分工的边界。缺少收集到的精确 canonical 才抛
NoSuchMethodException。源码 call??? 入口仍只针对静态未声明方法及合法 proxy 链，
不能因 router 已支持访问器/operator 就宣称前端自动为这些类别生成 call???。

相关章节：[singleton/ABI §7](LAYOUT_AND_ABI.md)、[pass 排序 §3](PIPELINE.md)、[挂起受控拒绝 §6](COROUTINE_LOWERING.md)。
