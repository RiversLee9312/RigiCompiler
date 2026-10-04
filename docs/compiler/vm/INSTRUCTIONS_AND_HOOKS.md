# VM指令与宿主Hook

> 章节号沿用原总览，便于既有引用核对。跨专题的 § 引用可通过[架构索引](BIL_VM_DESIGN.md)定位；语言、运行时与 BIL 语义仍以相应规范为准。

## 5. 指令分发：家族基类 + Execute

每个指令子类实现 `Execute(VmContext, VmCoroutine)`，同类指令同住
既有家族文件（BilComputeInstructions.cs 等）；适合继承共享的计算/类型
家族使用基类，数据与调用指令也可直接继承 `BilInstruction` 并复用
`BilDataExecution` 等家族设施。共同职责：

- 操作数解析（`$var` 读写当前帧变量槽、`res(...)` 从模块资源装载字面量/
  switch-table/catch-table、`fn(...)`/`type(...)`/`field(...)`/`case(...)`
  符号解析、indirect 解引用）。
- 结果写回当前帧变量槽。
- 二元/一元内建运算基类：取操作数 → 按精确类型 primitive 分派（§3.3）→ 写回。
- 字段访问基类：静态目标解析 + getter/setter/wrapper 行为序列（§22.4）；
  indirect 形态只多一步 fieldid/typeid 值解引用，其余共用。

指令分发靠虚方法，不靠中央 opcode switch，与 BilWriter 自渲染纪律一致。
`VmContext`、`VmDispatch` 已按职责使用 partial 分文件；各分文件共享同一
实例所有权与生命周期，不能另造不一致的状态机。

## 6. indirect 指令与类型驱动访问

indirect 是表达正常 Rigi 程序（泛型、lambda、运行时类型驱动访问）的必需品，
模型、文本渲染/读取、Verifier 与 VM 需共同保持同一形态。当前实现：

| 指令 | 规范位置 | 模型现状 |
|---|---|---|
| `type.is.indirect` / `type.supers.indirect` / `type.with.indirect` | §12.3 | ✅ `IndirectTypeCheckInstruction` |
| `invoke.indirect` / `invoke.indirect.noret` | §15.3 | ✅ 已有（lambda `$$call` 虚调用） |
| `cast.indirect` / `cast.safe.indirect` | §12.2 | `CastIndirectInstruction`（TYPEID_VAR 解引用 + `.typeid<TBound>` 边界） |
| `get.wrapper.indirect` | §12.4 | `GetWrapperIndirectInstruction` |
| `getid.field` | §12.6 | `GetIdFieldInstruction` |
| `get.field.indirect` / `set.field.indirect` | §13.5 | `GetFieldIndirectInstruction` / `SetFieldIndirectInstruction`（FIELDID_VAR 解引用） |
| `get.field.static.indirect` / `set.field.static.indirect` | §13.5 | `GetFieldStaticIndirectInstruction` / `SetFieldStaticIndirectInstruction`（TYPEID_VAR + FIELDID_VAR） |
| `new.indirect` | §14 | `NewIndirectInstruction`（TYPEID_VAR 目标构造） |

变更任何 indirect 形态时同步核对 BilVerifier 逐指令校验（§21），
保证 `--emit-bil` 后置自检与 reader/writer/VM 一致。

## 7. native hook 表（§22.5）

| (lib, symbol) | 行为 |
|---|---|
| `rigi_rt` / `print` | 写 stdout（加锁，单次调用原子） |
| `rigi_rt` / `printErr` | 写 stderr（同上） |
| `rigi_rt` / `print_err` | `printErr` 别名键（MW12b：`core.GlobalExceptionHandler` 的 native 声明经 `rigi_` 直拼命中 shim.c `rigi_print_err`；两键同实现） |
| `rigi_rt` / `gexc_register_handler` / `gexc_handler_count` / `gexc_handler_at` | MW12b §25.2 `core.GlobalExceptionHandler` 处理器注册表三面（VM 侧注册表存 VmHooks，注册序=下标序；dispatch 空注册表走默认 `print_err` 文本，与 native atexit flush 一致） |
| `rigi_rt` / `any_to_string` | §3.8 标准文本；未覆写者为 canonical 类型名（toString 成员方法不再直接 hook——其默认实现来自 .intrinsics.rg 的普通方法体，经该源码的 priv 全局 native `any_to_string` 触达本 hook） |
| `rigi_rt` / `any_hash` | §3.8.1 i64 哈希（Map 键判等，用户裁定扩充，同 `any_to_string` 的 stdlib native 面形态）：String 按内容、标量按值、对象按身份、null 固定 0；仅同一宿主内同值必同哈希，标量和字符串哈希数值与 Native 一致，对象身份哈希两宿主不可比（hash 成员方法同样不直接 hook——默认实现来自 .intrinsics.rg 的普通方法体，经该源码的 priv 全局 native `any_hash` 触达本 hook） |
| `rigi_rt` / `time_now` | §17.4 时钟原语：返回自 1970/1/1 00:00 UTC 起毫秒（i64）。`core.time.DateTime.now()` 与 `core.coroutine` 的 `rigi_time_now` 声明均显式 `@NativeSymbol("time_now")` 命中本键 |
| `rigi_rt` / `host_is_windows` | 施工块 7-1（STDLIB §4.5.2/§4.5.9，D5）宿主平台判定私有原语：非 0 = Windows。`core.fs` Path 平台路径词法校验内部使用（公共平台信息 API 继续后置）；VM 侧 `OperatingSystem.IsWindows()` 同语义镜像，native 侧 `_WIN32` 编译期判定 |
| `rigi_rt` / `alloc_array` | 零值初始化 `.array<T>`；T 为 enum struct 按宿主错误（§14.3） |
| `rigi_rt` / `timer_create` | 时钟底座句柄；`sleep`/`Timer` 经 stdlib 构造调用（RUNTIME §19.4/§19.5）。旧 `make_sleep_alarm` 已删除 |
| （方法 hook）`core::Any$call???` | 按 symbol 路由；无路由抛 `core::NoSuchMethodException` |

上表保留关键行为契约，并非全部注册键。完整 native 键来自
`Bil/Vm/VmHooks.cs` 的 `CreateStandard`，分类目录见下文；真正未注册的
`(lib, symbol)` 经 `VmHooks.Invoke` 抛 `VmNativeHookException`。新增标准
接口必须同步 BIL §22.5 分类、stdlib 声明与行为对拍，不因未列在精简表而拒绝已有接口。
GC 类设施（GCAlarm 等）永不进表：BIL 禁止对 GC 机制与实现作任何假设
（§1.1/§22.1），其为 Middleware 内部细节。

### 7.1 已注册 native 键分类目录

所有下列 native 键的 library 均为 `rigi_rt`。参数与返回 ABI 以对应
stdlib 私有 native 声明、`VmHooks.CreateStandard` 的适配和 §22.5 中的
具体条目为准；目录不授予用户声明同名私有方法的编译器特权。

- **文本、标准流与基础值**：`print`、`printErr`、`print_err`、`any_to_string`、`any_hash`、`alloc_array`、`text_copy_out`、`text_from_bytes`、`stdout_write`、`stderr_write`、`stdout_flush`、`stderr_flush`、`stdin_read_start`、`stdin_read_take`、`host_is_windows`、`i64_to_string`、`u64_to_string`、`f32_to_string`、`f64_to_string`、`bool_to_string`、`char_to_string`。
- **Place、Handle与内存视图**：`place_same_target`、`handle_make`、`handle_target`、`handle_as_mutable`、`handle_is_mutable`、`handle_kind`、`handle_type_is_value`、`span_alloc`、`span_u8_echo`。
- **文件系统**：`fs_open`、`fs_read_start`、`fs_read_take`、`fs_write_start`、`fs_write_take`、`fs_flush_start`、`fs_flush_take`、`fs_seek`、`fs_tell`、`fs_get_length`、`fs_set_length`、`fs_stat`、`fs_lstat`、`fs_realpath`、`fs_mkdir`、`fs_rmdir`、`fs_unlink`、`fs_rename`、`fs_diropen`、`fs_dirread`、`fs_same_file`。
- **Worker、协程与同步**：`coroutine_current`、`coroutine_get_lane`、`coroutine_set_lane`、`worker_parallelism`、`worker_create`、`worker_destroy`、`worker_enqueue`、`worker_park`、`coroutine_create`、`coroutine_resume`、`coroutine_destroy`、`native_rc_retain`、`native_rc_release`、`sync_mutex_create`、`sync_mutex_acquire`、`sync_mutex_release`、`tls_current_context`。
- **定时器、事件与时钟**：`timer_create`、`timer_cancel`、`timer_destroy`、`event_create_sticky`、`event_signal`、`time_now`、`time_now_parts`、`monotonic_now_ns`。
- **CoroutineLocal**：`coro_local_push`、`coro_local_pop`、`coro_local_get`、`coro_local_inherit`。
- **数学与随机**：`math_floor_f32`、`math_floor_f64`、`math_ceil_f32`、`math_ceil_f64`、`math_trunc_f32`、`math_trunc_f64`、`math_round_even_f32`、`math_round_even_f64`、`math_round_away_f32`、`math_round_away_f64`、`math_sqrt_f32`、`math_sqrt_f64`、`math_pow_f32`、`math_pow_f64`、`math_exp_f32`、`math_exp_f64`、`math_ln_f32`、`math_ln_f64`、`math_log2_f32`、`math_log2_f64`、`math_log10_f32`、`math_log10_f64`、`math_sin_f32`、`math_sin_f64`、`math_cos_f32`、`math_cos_f64`、`math_tan_f32`、`math_tan_f64`、`math_asin_f32`、`math_asin_f64`、`math_acos_f32`、`math_acos_f64`、`math_atan_f32`、`math_atan_f64`、`math_atan2_f32`、`math_atan2_f64`、`sys_random_u64`。
- **全局异常**：`gexc_register_handler`、`gexc_handler_count`、`gexc_handler_at`。

### 7.2 方法 Hook 与普通函数体的优先序

方法 Hook 使用独立表，以宿主 `$` 方法名（签名前缀）定位；可信完整
canonical/ABI 可经 `BilCompilerSymbols` 映射到机制键，普通私有同名方法
不能获得此权限。现有方法键为 `core::Any$call???`、
`core.coroutine::Task$startCold`、`core.coroutine::Task<TReturn>$startCold`、
`core.coroutine::Mutex$enter` 与 `core.coroutine::Mutex$release`。

`InvokeValues` 先处理 `..inner`/`..super`；`call???` 先尝试 wrapper wildcard
路由，再处理 native 声明和方法 Hook，随后进入普通 wrapper/函数派发。
`.intrinsics.rg` 中 `Any.call???` 的默认 throw 体按普通源码绑定并发射 BIL；
VM 已注册的方法 Hook 优先于这个函数体，无路由时同样抛
`core::NoSuchMethodException`。不能据旧实现断言 call??? 没有 BIL fn。

Task 冷启动与 Mutex 竞争判定继续解释 Rigi 方法；方法 Hook 只连接
无法以普通 Rigi 调用表达的启动/挂起/唤醒引擎动作，并维持同 gate 握手。
