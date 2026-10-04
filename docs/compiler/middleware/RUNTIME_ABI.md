# Middleware 与 rigi_rt 的运行时 ABI

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

### 4.8 运行时面（当前 C ABI）

运行时面的实际 C 名和参数见 RuntimeFaces、Emit 的 DeclareHelperFace 声明及
rigi_rt 头文件；表中内建 shorthand 对应 rigi_ 前缀 C 符号。普通 runtime 面表和
专用 helper 分工不同，不应把协程原语虚构为 RuntimeFaces 的字符串编组面。

| 面 | 语义 |
|---|---|
| `rigi_alloc(desc)` | 按 TypeSheet 描述符分配对象头 + payload |
| `rigi_acquire_local/shared(p)` / `rigi_release_local/shared(p)` | 对象 RC 增减，两类 release 均统一走原子候选协议；公开 release 是 void 并在终态执行析构，内部 rigi_gc_release_shared 返回终态信号供 ARC 消费。非零 release 完成候选登记、债务累计、阈值检查与通知（§4.6） |
| `rigi_ref_acquire` / `rigi_ref_release` | 值语义四面族·胖引用槽：按 tag 分派对象/堆值/内联；生成代码只见此对 |
| `rigi_value_acquire` / `rigi_value_release` | 值语义四面族·值类型：按 TypeSheet.refMap 走查内部胖引用/String 槽 |
| `rigi_string_acquire` / `rigi_string_release` / `rigi_string_new` | String 槽 ARC（块头 `{atomic u32 rc, u32 reserved}`，data=块+8；字面量 rc=`0xFFFFFFFF` 永生） |
| `rigi_region_enter` / `rigi_region_exit` | RUNTIME §23.3 cFlag 协议（MW12 已落地：OS 线程槽 cFlag 注册表 + region_enter 内阻塞等平台事件，无双检挂起路径）；支持嵌套，单个 ARC 面自行包裹，生成代码用外层 region 覆盖复合槽位变更 |
| `rigi_track_malloc` / `rigi_track_free` / `rigi_mem_report` | 台账三面：`RIGI_RT_MEMTRACK=1` 时跟踪堆块，进程退出未清零即 stderr + exit 1 |
| `string_concat` 等内建面 | String 内建 `+` 等特权操作的实现（String 字符数据是特权裸缓冲区，非托管引用，RUNTIME §4） |
| `i64_to_string` / `u64_to_string` / `f64_to_string` / `f32_to_string` / `bool_to_string` / `char_to_string` | 标量标准文本（StringOut 首参；any_to_string 的格式化底座；窄整数在 any_to_string 内按符号性 widen 到 i64/u64；f64/f32 为 Ryu 最短往返 + .NET 默认呈现） |
| `any_to_string` | 任意胖值标准文本（StringOut 首参 + Any 槽指针）：内建标量走对应 to_string 面；`core::String`（tag1）拷贝裸块；其余（tag2 对象 / 大 struct 等）取 TypeInfo.name。不虚调 toString（防默认体递归；override 经方法虚派发，不经本面） |
| `any_hash` | 任意胖值 i64 哈希（Any 槽指针入参；SYNTAX §3.8.1 Map 键判等）：tag1 String 对 data 字节取 FNV-1a 64（内容）；tag0 标量对 payload 8 字节取 FNV-1a 64（按值）；tag2 对象与 tag1 非 String 堆值对 payload（堆指针）取 FNV-1a 64（身份，不直接返回裸指针）；null 固定 0。同一进程内同值必同哈希；VM hook 已统一为同一 FNV-1a 64（review-20260910），标量/字符串数值两宿主一致。不虚调 hash（防默认体递归；override 经 `$mw.any.hash` 合成分派链走方法虚派发，不经本面） |
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


## 11. rigi_rt 代码组织

```text
rigi_rt/                    # 本仓库顶层目录（C，EmbeddedResource 内嵌，clang 现场编 bitcode 合并进模块）
├── shim.c                  # MW1 最小面：rigi_string {data,len} UTF-8 / rigi_print / rigi_print_err / rigi_string_concat / main → rigi_entry
├── string_rc.c             # String ARC：块头 rc + data=块+8、IMMORTAL 字面量、region 自包含
├── span.c                  # MW7b：rigi_span_alloc，复用 alloc_contiguous（数组同构布局）
├── memtrack.c              # 台账：RIGI_RT_MEMTRACK=1 时跟踪 mi_malloc_aligned/mi_free 包装分配，退出未清零 exit 1
├── arc.c/.h                # microGC / microSGC、值语义四面族、region 协议、对象头
├── macrogc.c/.h            # MW12 Bacon-Rajan 三阶段收集器（显式 trace 栈）、候选账本、
│                             #   GC 常驻线程 + fence（region_enter 双重检查）+ 阈值/终轮兜底；
│                             #   诊断 env：RIGI_RT_GC_THRESHOLD/OFF/TRACE
├── gexc.c/.h               # MW12b §25.2 全局异常通道：undisposed 事件队列 + 处理器注册表
│                             #   + atexit flush（drain/dispatch 晚到规则）
├── coroutine.h             # MW11c 瘦身：RigiFatRef / RigiResumeCode 共享 ABI 类型（旧 C 调度面已删）
├── cohandle.c/.h           # 协程句柄原语：create/resume/destroy + lane + PollingAlarm 轮询状态
├── shell.c/.h              # 3b-β Handle 壳：shellID 注册表（全局索引 + 属主分组，单自旋锁）、
│                             #   capability 归零转移、NativeRc 释放消息、teardown 过户（RUNTIME §28）
├── worker.c/.h             # Worker 原语：OS 线程/入队/park/同步 Mutex/定时器/TLS/主 Worker 收尾
│                             #   + L8 rigi_event_create_sticky（用户 EventAlarm 直继子类默认底座：
│                             #   粘滞形态，signal 恒置已触发并归还 armed，幂等；stdlib
│                             #   EventAlarm.ensureHandle 懒建，yield 分流改经 ensureHandle 取柄）
├── failreg.c               # 未观察失败注册表（Task 失败异常 native 承载；shared class 不得持 local Exception）
└── eh.c/.h                 # MW9a checked-flag 便携异常传输：TLS pending 槽三面（rigi_exc_raise/pending/take）+ 顶层 reporter（rigi_type_name_of/rigi_exc_halt），不使用平台原生 EH
```

相关章节：[内存 §4](MEMORY_MANAGEMENT.md)、[ABI §7](LAYOUT_AND_ABI.md)、[异常 §8](EXCEPTIONS.md)、[协程 §6](COROUTINE_LOWERING.md)。
