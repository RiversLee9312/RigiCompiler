# Middleware ARC、macroGC 与资源所有权

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

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

通用 last-use 与 cursor 式非拥有引用优化仍为预留方向，普通拥有槽保守计数。
当前已经支持协议必需的 move（如 MirCoroutineCreate 的 frame 交付、返回 out 槽）
及语言级 @NativeBorrow 借用槽传播；这些所有权契约不能省略，也不等于启用了
通用 last-use 优化。move 槽从本地 release 序列排除，frame 仅在 Tasked DONE
出口最终释放；Plain frame 由调用方持有，resume 借用。

**MW7a 落地形态**：不变量——每个托管槽在每个 CFG 出口恰好配对 acquire/release，
region 面内自包含（内部无挂起点、无可抛 Rigi 异常的调用）。决策层只插入两条
MIR 指令 `MirAcquireSlot` / `MirReleaseSlot`；发射层按槽分类落到
ref/value/string 面。四规则：（1）入口对胖引用/String 参数 acquire（值类型
`.this` 豁免，其 +1 由落槽建立，避免与本规则双计）；（2）托管 `CopyLocal`
展开为 Release–Copy–Acquire 三段式，dst==src 删除；（3）产出类指令前置
Release 目标槽；（4）ret 块将返回值迁入合成局部 `$mw.ret` 后按登记序
Release 其余托管槽（出口序列不含 `$mw.ret`）。Emit 层对 `$mw.ret` 的
交付即移动（richretrfix）：`MirRet` 值类型分支对隐藏 out 首参纯
memcpy、不再 release `$mw.ret`——tag1 盒槽（Nullable 装箱）acquire
有深拷回写副作用（`arc.c` `rigi_value_walk`），三段式交付会让 out 拿
到已释放块；契约详见 `TerminatorEmitter` 与 `ArcEmitter`
`EmitDestroyRichValue` 注释。region 协议由运行时面自身
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

> **Native 实现（macrogc.c/.h）**：颜色/候选索引打包在对象头
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
- **执行体的语言层抽象是 GC 协程**：独占内置 GC 执行容量，不向 stdlib
  暴露，用户协程不可进入；两轮之间等待 GCWakeAlarm，保证唤醒不与用户协程
  竞争调度容量。当前 Native 直接以专用 OS 线程承载该 collector，并不创建
  Rigi Dispatcher/GC Executor；内部平台事件保持 Alarm 的 sticky/幂等握手语义。
- GC 协程被唤醒后：执行 RUNTIME §23.2 握手（它是 collector 主体）→ 染色与
  清理（**pass 期间不得挂起**）→ 发布 `gcFlag=IDLE` → 触发 `GCAlarm` 唤醒
  ENTERING 协程 → 清 pending、复查债务（仍超阈值则立即再来一轮）→ 重新挂起
  于唤醒 Alarm。
- 生命周期：运行时初始化时创建，进程常驻。

> **Native 实现**：GC 协程的 native 承载是**常驻专用线程**
> （CreateThread/pthread_create 双平台薄封装，不依赖 libuv，不经 Rigi
> Dispatcher 通道）；`GCWakeAlarm`/`GCAlarm` 降级为双平台手动复位事件
> （Win32 Event / pthread condvar），§19.3 sticky/幂等语义保持，不进
> stdlib/VM hook 表。§23.2 六步握手原样执行（STARTING 独占 CAS → fence →
> 自旋排空 cFlag，30s 超时亮红灯 abort → PROCESSING → 染色清理 → fence →
> IDLE + 触发 GCAlarm）；Native 在发布 PROCESSING 后再次建立 seq_cst fence
> 并排空 cFlag，与 mutator 二次检查组成完整 store/load 握手，不能只依赖 x86
> 强内存序。清空 pending + 债务复查仍超阈值立即再来一轮。
> shim.c 启动序：main → `rigi_gc_init` → `rigi_entry`；atexit LIFO 注册序
> mem_report → gexc_flush → gc_shutdown → globals_cleanup（执行序：
> globals_cleanup 释放静态槽 → gc_shutdown 终轮收集兜底 → gexc_flush 打印
> 晚到 undisposed 事件 → mem_report 零泄漏报告）。

### 4.7 ownership fence（RUNTIME §23 保留）

macroGC 维持全局性：进入 pass 即冻结**全部**托管引用 acquire/release（local 与
shared），单一 pass 可安全处理跨 Coroutine 的混合候选闭包（local→shared 边）；
跨协程 shared 环**支持回收**。§23.6 放行不变（分配与纯值执行不停）。

> **Native 实现**：§23.3 隐藏 yield GCAlarm 的 native 降级 = `region_enter`
> 内**阻塞等平台事件**——region 内禁止挂起点 + 同步函数无法挂起，阻塞 OS
> 线程是唯一直译；阻塞期间协程不迁移。cFlag 用 OS 线程槽（懒认领的 256 槽
> cache-line 独占注册表）即满足 §23.1 身份要求。单个 ARC 面自行进入 region；
> codegen 对“RC 更新 + 引用槽写入”的复合操作再发射一层可嵌套的外层 region，
> 确保 macroGC 观察不到计数和引用图不一致的中间态；RcInjection 生成的托管
> 局部 `release → copy → acquire` 三元组也由 Emit 合并进同一个外层 region。


### 4.9 对象头与 refMap

对象头：typeid（→TypeSheet）+ RC 计数 + 颜色/候选索引位（位打包）。rich
ValueType/Box 裸数据块的内部引用按同一 refMap 机制扫描（RUNTIME §4/§8）。
128-bit 胖引用下 RC 元数据在**对象头**（不在引用槽）；引用槽写入时聚合进对象
头真计数。

---

### 原生资源终结与迟到清扫

通用原生资源所有权：TypeInfo.destroyNative 是无 GC fence 重入的资源终结槽。协程 Mutex、Task（含泛型）与 Dispatcher 按真实布局生成 gate 清零/释放回调，ARC 与 macroGC 白色清理均执行；同步锁登记册支持退出清扫后的迟到终结。Mutex.Lock 强持属主直到令牌自身回收。local/shared RC release 均使用 pin-before-sub 原子协议；PURPLE 在册持有账本 +1，
在册最后用户引用释放时锁内摘候选与归还账本引用，锁外递归析构；非在册终态
免账本锁。颜色/索引 CAS 保留并发 dispose 位。


### 候选强引用、终态与会计模式

PURPLE 在册 ⟹ rc = 外部引用数 U + 1；非在册 ⟹ rc = U。可能成环对象的 release
先原子取 pin，再减用户引用；成功登记把 pin 无偿转为账本引用，未登记归还 pin。
登记窗口对象必活。非在册 old==2（含本次 pin）是独占最后引用，归还 pin 后归零
即可析构；在册 old==2 是最后用户引用，锁内 detach、归还账本 +1 后归零。收集
在 fence 冻结后、markGray 前归还所有账本 pin，染色计数才表达真实外部引用。
终态析构前必须断言 rc==0；任何减后值==1 即析构都违反契约。

对象的 local/shared 会计模式位仍按类型静态初始化，Handle 壳过户和失败 Task
异常图发布前 promotion 单调翻 shared，并保留遍历数组、refMap、tag1 盒及环的
完整子图处理。当前 local acquire 也为原子 RMW，local release 直接转发
rigi_gc_release_shared，候选统一进入全局账本；per-协程局部登记入口停用，
历史实现封存在 #if 0。收干、promote detach 与账本结构释放仍保留，不能据其
函数存在宣称 mutator 局部协作收集已启用。停止局部路径的理由是 CoroutineHandle
和动态闭包 cell 可跨执行线程共享，属主独占前提不成立；仅将 rc 原子化也不能
防止 markGray 临时减量被并发 mutator 当成死亡。重新启用需重新证明全部对象
的独占前提，并通过默认、GC_OFF、低 GC_THRESHOLD 并发回归。

memtrack 底座为 mi_malloc_aligned(total,16)/mi_free，保留 16B 台账头；per-thread
TLS 独占槽以 64KiB 流量节拍并入全局计数，跨线程释放允许负局部余量。退出
join Worker 和 GC 线程后聚合 live blocks/bytes 才是零泄漏口径；peak 为并入点
采样，不保证捕获两次并入间短峰值，mimalloc 自身缓存不计入用户 live 账本。

相关章节：[运行时面 §4.8](RUNTIME_ABI.md)、[异常 §8](EXCEPTIONS.md)、[协程 §6](COROUTINE_LOWERING.md)、[验证 §10](VALIDATION.md)。
