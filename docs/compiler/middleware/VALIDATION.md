# Middleware 验证与实现覆盖

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

## 10. 验证策略

1. **门禁**：BilVerifier 全规则；非法 BIL 必须被拒（BIL §23）。
2. **VM 对拍**：同一 BIL 在 BIL VM（行为参考实现）与 native 产物上的可观察
   行为一致（stdout/异常/退出码）。仓库已有测试资产直接复用。
3. **RC 正确性**（VM 对拍覆盖不到的领域）：设计验证手段包括为 native 产物挂 ASAN/Valgrind 跑
   套件（需对应仪器工具链，非当前默认 CI 的已运行证明）；引用图压力与模糊测试；RcInjection 结构化自检（每 region、每 CFG 出口
   恰好配对）。rigi_rt 内置台账（`RIGI_RT_MEMTRACK=1`）为跨平台零泄漏口径，
   NativeE2E 全用例开启（泄漏即产物进程 exit 1）。
4. **黄金 .ll 快照**：黄金 BIL 用例的 PrintModule 产物快照比对，防发射回归。
5. **工具链契约测试**：LLVMSharp 绑定与 libLLVM 20 原生包的版本对齐冒烟、
   lld 链接冒烟，随 CI 跑（win-x64/linux-x64）。


## 12. 实现覆盖与保留优化边界

原 MW 标签用于定位既有代码注释和职责，不记录里程碑进度。下表保留原各阶段
的功能边界与验证目标；实际 pass 注册以 [管线](PIPELINE.md) 为准。
VM 对拍与全量测试为通用验收。

| 原职责标签 | 功能边界 | 验证目标 |
|---|---|---|
| MW0 | 工程骨架：Gate（Reader + Verifier 门禁）、CLI、符号表、LLVMSharp 接线 | 非法 BIL 被拒；空模块进程内出 .o |
| MW1 | 最小垂直切片：标量 + String（内建 `+` → `string_concat` 面）+ native print + main → LLVM 模块 → .o → lld 链接最小 rigi_rt | hello world 与字符串拼接；VM 对拍打通 |
| MW2 | 标量与运算全量（BIL §11/§12、Resources 常量池） | 对拍套件 |
| MW3 | 控制流（if/loop/switch → CFG；alloca + mem2reg） | 对拍套件 |
| MW4 | 对象系统 I：布局（含胖引用槽与 access helper 面——C 编写、bitcode 合并、LLVM 内联）、字段、new、static | |
| MW5 | 调用与 ABI：invoke 族、typeid 隐藏参数、vargs/kwargs、FFI | |
| MW6 | 元数据与派发：TypeSheet/vtable/iMap/refMap、虚调用、interface | |
| MW7 | 值语义运行时 + ARC：Box 物化、RcInjection、region 协议发射（MW7a）；Span 物化（MW7b：Span=内建 class 定稿） | MW7a/MW7b：NativeE2E 对拍与台账零泄漏 |
| MW8 | 泛型运行时：`Type\<T\>`/typeOf/new、is/supers/with/cast（MW8a/MW8b/MW8c） | MW8a typeid 装箱；MW8b 动态 new 槽 0 分发器；MW8c-1 `.typeid<X>` 构造 sheet；MW8c-2 泛型占位 cast + 数值/String/struct 转换；MW8c-3 struct sret thunk + 标量零值 T() + VM 无匹配 init 必抛 |
| MW9 | 异常：try/catch/finally → checked-flag 便携传输（§8；MW9a：rigi_rt eh 三面 + reporter、MIR 构件（MirThrow/MirTakePending/MirRetThrow/ExcTarget）、TryExpander 十步展开、RcInjection 传播垫、Emit pending 检查、NativeE2E 捕获型对拍；MW9b：内置异常 message 模板源码化（stdlib init 重载）+ VM/native 构造全走真 init、VM 顶层格式对齐 `{类型全名}: {message}`、三占位 abort + 数组/Span 越界写 abort 全转真异常（守卫指令 ExcTarget 扩面 + ExceptionEmitter 共享抛出辅助 + core 异常恒可达白名单；除零策略换 Throw 实现）、SYNTAX §8.1 的异常类 `core.OutOfBoundException` + §8.2 未捕获进程行为） | 异常对拍套件 |
| MW10 | wrapper 烘焙全链（Entity/Value/Method 三类 proxy 链 + wildcard router 与 call??? 降级 + singleton 三态 get fn 与急切初始化，§5/§7） | |
| MW11 | 协程：状态机、Executor/Worker、Alarm（libuv 时钟底座）、Task、eager spawn。**MW11c**：架构转向落地——Dispatcher/Task 调度在 Rigi 世界，rigi_rt 瘦身为 Worker/协程句柄/定时器/同步 Mutex/TLS/失败注册表原语；冷 Task、TaskState、executor 换绑、多 Worker 懒起、Timer/`sleep`、语言级 Mutex（VM 方法 hook；native 由 CoroutineSplit 改写 `enter` + Rigi `release` 真体，判定在 `tryEnter`/`releaseNext`） | 协程集成测试 |
| MW11d | 序列化 + 消息全链：`core.serialization`（@Serializable/@SerializationBase/@Temporary/@Terminal 四修饰器 + Parcel + toParcel/fromParcel/deepCopy 合成，SYNTAX §20）；纯 Rigi MessageQueue 五 API + 安全 AtomicList/Mutex + capability 矩阵 + EOS/broadcast/深复制双端对拍；Reader/Receiver/Messenger 高层 API + listener 身份（安全 Place 对象身份）+ Executor 路由（默认 IOExecutor），RUNTIME §27。泛型契约：闸门 2 裸 GP 实参与 receiver/类型实参同口径声明侧跳过；裸模板 new 隐藏 typeid 取当前 fn 的 `.generic.*` 局部 | BilVm Messaging 电池 + NativeE2E 对拍 + e2e 负例 |
| MW12 | macroGC：收集器、候选账本、GC 协程（native 常驻专用线程承载）、fence 激活、§25 检查（MW12a 收集器+fence 上线 / MW12b §25.2 全链 + GlobalExceptionHandler / MW12c 循环回收套件） | NativeE2E mw12c 循环回收与 mw12b 用例（含双宿主对拍）；回归约束：FieldEmitter 借用字段读侧 ARC 必配平、Emit 临时 alloca 必位于 entry 块，避免循环栈泄漏（BuildEntryAlloca 统一） |
| 优化方向（原 MW13） | move/cursor、独立 CellElim/Devirt 与工具链捆绑；当前默认管线未启用这些独立优化 pass，勿从此表推断已实现 | 按语义等价与工具链发布契约验证 |

注：

- RcInjection 的 region 协议随代码生成常驻；当前已对接收集器与平台事件
  等待路径，生成的 acquire/release 对及复合 region 无需因 collector 承载形式改变。
- 字符串插值在 BIL 之前已由 frontend 降级为 `toString` + String 内建 `+`
  （SYNTAX §3.8），不构成 Middleware 的独立工作项；MW1 只依赖 String 类型与
  内建 `+`。

相关章节：[当前管线](PIPELINE.md)、[协程边界](COROUTINE_LOWERING.md)、[ARC/GC 不变量](MEMORY_MANAGEMENT.md)。
