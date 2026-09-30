# 确定性资源管理与 native 互操作（§25–§26）

> 本文件是 [RUNTIME.md](../RUNTIME.md)（Rigi 运行时设计）的章节拆分，§ 编号与原文件一致；总目录与章节索引见该索引文档。

## 25. 确定性资源管理：`IDisposable`、`using` 与全局泄漏异常

Rigi 明确不支持 finalizer，也不允许运行时在对象回收阶段调用任意用户终结逻辑。对象内存由 microGC/microSGC/macroGC 管理；文件、句柄、流、锁封装等外部资源则由 `core.IDisposable` 确定性管理。

概念接口为：

```rigi
pub interface IDisposable {
    func dispose()
}
```

### 25.1 `using` 的 lowering

`seq using(...) ... named ... {}` 中的每个资源绑定由编译器 lowering 为与该 `seq` 词法退出绑定的清理记录：

- 初始化按源码顺序进行；
- 只有成功完成初始化的绑定才进入清理栈；
- 退出 `seq` 时按逆序调用 `dispose()`；
- 正常返回、`return@`、裸 `return`、异常展开及其他离开作用域的控制流共享同一清理路径；
- `await`/`yield` 只保存并挂起当前 Coroutine 状态，不触发清理，资源记录随 Coroutine frame 保留；
- `dispose()` 本身是普通函数，可以 `await` 或 `yield`。清理中的挂起保存当前 disposal 调用、逆序清理进度和尚未处理的资源；恢复后继续同一清理路径；
- 外层函数的 return、异常继续传播或 Coroutine 的终态发布，必须等待已建立的 `using` 清理全部完成，不能通过丢弃 frame 绕过可挂起 disposal。

`using` 提供的是编译器保证的确定性调用，不依赖引用计数何时归零，也不依赖 macroGC 是否运行。可挂起只延长清理路径，不降低“每个 `IDisposable` 必须由用户代码负责 dispose”的强制要求。

### 25.2 销毁时的强制检查

`TypeSheet.typeFlags` 的 `DISPOSABLE` 位标识类型是否实现 `core.IDisposable`；对应 Object 的生命周期元数据中带有运行时可检查的 disposal 状态。无论对象最终由 microGC、microSGC 还是 macroGC 销毁，运行时都必须在释放其内存前检查该状态：

- 已由用户代码直接或通过 `using` 调用 `dispose()`：正常继续销毁；
- 从未调用 `dispose()`：立即产生全局的 undisposed-resource 异常事件。

若 `dispose()` 正在挂起，对象及清理记录仍由对应 Coroutine frame 保持，尚未进入对象销毁检查；运行时不能把“正在执行可挂起 disposal”误当成未负责的遗失资源。真正到达 microGC、microSGC 或 macroGC 销毁点而 disposal 状态仍表明从未调用 `dispose()` 的对象必须爆炸上报。

这一检查是错误检测机制，不是隐式清理机制：

- GC **绝不**替对象调用 `dispose()`；
- 不建立 finalization queue；
- 不延迟对象释放来等待用户终结逻辑；
- 不允许对象复活；
- 上报后仍按正常内存生命周期完成销毁。

该异常不绑定到“恰好触发最后一次 release 或 macroGC”的普通用户调用栈，因此普通 `try/catch` 不能接住。它只能通过 `core.GlobalExceptionHandler` 提供的全局处理方法接收。运行时应至少携带对象实际类型；实现还可以附加创建位置、最后释放位置等诊断信息。

**MW12b 定稿形态**：

- **API 面**（stdlib `core/global_exceptions.rg`，namespace `core`）：
  ```rigi
  pub class GlobalExceptionHandler {
      pub static func register(handler: core.Action\<core.Exception>)
      pub static func dispatch(exc: core.Exception)
  }
  ```
  事件载荷类型为 `core.UndisposedResourceException : RuntimeException`，唯一 init `init(resourceType: String)`，message 模板「对象在销毁前从未调用 dispose()：${resourceType}」；`resourceType` 是违规对象的**实际类型全名**。处理器注册表存 native（rigi_rt `gexc.c` 三面 `gexc_register_handler/handler_count/handler_at`，+1 持有，注册序=下标序）——SYNTAX §3.1.1 共享安全闸门禁止静态字段持 local `Action`。
- **派发时机**：入口收尾统一派发——native 由生成代码 entry stub 在 main/drain 之后、失败汇总之前循环 `rigi_gexc_take` 逐条真构造异常并调 `dispatch`；VM 由 `BilVm.Run` 在同一时点经 C# 终结器（`VmObject.DisposedMarked` 未标记且类型 implements `IDisposable` → 入队）+ `GC.Collect`/`WaitForPendingFinalizers` 后逐条派发。每次 `dispatch` 调用后做 pending 检查，处理器自身抛异常走正常失败汇总。
- **默认行为**：注册表为空时 `dispatch` 打印默认 stderr 行 `core::UndisposedResourceException: 对象在销毁前从未调用 dispose()：<类型全名>`。**进程继续，退出码不变**。
- **晚到事件**：native 侧 `globals_cleanup`（静态槽释放）与 GC 终轮收集阶段入队的事件不经用户处理器，由 `rigi_gexc_flush_default` 在 atexit 打印同文本默认行；VM 侧静态槽/单例保持根住、不模拟退出清理，因而不产生晚到事件。

这一分工保持三类生命周期彼此独立：

```text
托管内存：microGC / microSGC / macroGC
外部资源：dispose() / using
遗漏检测：destruction-time global exception
```

---

## 26. native 互操作与 `rigi_rt`

`native` 函数（`SYNTAX.md` §4.6）把 Rigi 调用路由到运行时原生方法面。原生方法面由一个 C 编写的 shim 库提供，库标识为 `rigi_rt`：它把 libc 风格的 C 函数包装为 Rigi 调用约定下的可调用入口，并负责 Rigi 值（如 `String` 的 native 表示）与 C 类型之间的转换。

- **调用约定**：fastcall；精确的寄存器/栈分配、胖值槽传递与 `String` 布局规则由 Middleware 定义。
- **第一版原生方法面**只有五个函数，不提供可变参数：
  - `print(text: String)`：把字符串写入标准输出；
  - `printErr(text: String)`：把字符串写入标准错误；
  - `any_to_string(value: Any): String`：`SYNTAX.md` §3.8 的 `toString` 内建承载——内建基本类型（数值/`bool`/`char`）返回标准文本（`String` 的 `toString` 即自身，不经此路由）；未覆写 `toString` 的对象返回其类型 canonical 名。它只经标准库 `.bootstrap.rg` 的文件级私有 native 全局声明暴露：`Any` 上声明 open `toString(): String`（全类型承诺，自带实现），`Object` 提供 open `override` 默认实现；二者的实现体由编译器合成为「装箱接收者后调用 `any_to_string`」的小函数，用户代码不直接调用 `any_to_string`。用户类型 `override` 后经普通虚派发执行自身实现，不再命中原生面。
  - `any_hash(value: Any): i64`：`SYNTAX.md` §3.8.1 的 `hash` 内建承载（Map 键判等）——`String` 按内容哈希（FNV-1a 64 over data 字节）、标量按值（payload 8 字节 FNV-1a）、对象与堆值按 payload（堆指针）FNV-1a（身份，不直接返回裸指针）、`null` 固定 `0`。同一进程内同值必同哈希；VM hook 已统一为同一 FNV-1a 64（review-20260910），标量/字符串数值两宿主一致，对象身份值两宿主不可比（地址 vs 宿主对象序号）；哈希不保证分布均匀，允许碰撞。与 `any_to_string` 同构：只经标准库 `.bootstrap.rg` 的文件级私有 native 全局声明暴露（`Any` open `hash(): i64` 全类型承诺 + `Object` open `override` 默认实现，实现体由编译器合成为「装箱接收者后调用 `any_hash`」的小函数），用户代码不直接调用 `any_hash`；用户类型 `override hash` 后经普通虚派发执行自身实现，不再命中原生面。
  - `alloc_array(typeid, size)`：分配元素零值初始化的 `Array\<T>`（T 由泛型 hidden typeid 物化，传参形态见 §10）。它只经标准库的私有 native 声明暴露：`Array\<T>` 的合法构造入口是 stdlib 的 `arrayOf\<T>(size)` 与 `arrayOfElements\<T>(elements...)`（后者在 Rigi 层把元素逐项放入），用户代码不直接调用 `alloc_array`。两个入口签名分离（长度 vs 元素包），不存在 `i32` 长度与 `i32` 元素的重载混淆。T 为 enum struct 时 `arrayOf` 由 frontend 在泛型实例化点拒绝（`BIL_STANDARD.md` §14.3「enum 无零值」）。**元素读写语义（Q6，`SYNTAX.md` §13.2）**：`a[i]` 读取语义上走 `getAtIndex`（返回 `T?`），实现上由编译器直发 `BIL_STANDARD.md` §13.6 `get.array`——界内得 `Nullable\<T\>` 包装的元素、**越界读取得 `null` 而非 trap**；`a[i] = v` 写入仍收非空 `T`，越界写入抛可捕获 `core.OutOfBoundException`（MW9b 起；此前为运行时 trap/abort）。
  - `timer_create` / `alarm_wait`：`sleep` 与 `Timer` 的时钟底座（§19.3/§19.4/§19.5）。`sleep` 构造内部 `SleepAlarm`，不另暴露 `make_sleep_alarm`。用户代码不直接调用。
- **GC 类设施（如 GCAlarm）不属于本表面，也不进 stdlib 与 VM**：BIL 明确规定不得对 GC 机制与实现作任何假设（`BIL_STANDARD.md` §1.1/§22.1），此类设施是 Middleware 的内部实现细节，没有任何跨层可见形态。
- **BIL VM 不链接原生库**：VM 对 `(lib, symbol)` 命中 `BIL_STANDARD.md` §22.5 内建 hook 表的 native 调用直接执行内建行为，因此在没有 Middleware 与 `rigi_rt` 实现的环境下也能完整执行程序。
- 标准库在 Rigi 层封装原生方法面（如 `core.io::Console.println` 调用 `print`），用户代码不直接依赖 `rigi_rt`；格式化、插值等逻辑全部在 Rigi 层演进，不进入原生方法面。

### 26.1 NativeRcHandle 与跨协程搬运

`core.native.NativeRcHandle\<TCarriage extends ICarriage>` 是 local 抽象类，
实现 IDisposable，规定 `carry(): TCarriage` 与 `dispose()`。具体类将
原生 token 保存在私有字段，资源操作通过该 local 封装执行。调用方
用 using/dispose 管理持有期，不手动调用 native retain/release。

`core.native.ICarriage` 是 shared 接口，仅提供 `retain(): Any?`，**不实现
IDisposable**。具体 Carriage 是弱搬运票据，不拥有资源强引用，也不提供
操作资源的能力。跨协程先 carry 并传递 Carriage，目标协程 retain 后
必须判空，再做具体 Handle 的运行期检查转换，最后用 using 管理返回
的 local Handle。retain 成功延长资源寿命；资源已经销毁时返回 null。

原生注册表将 token 映射到 payload、强引用计数与析构函数。token 单调
分配且不复用，避免旧票据指向新资源。retain 与最终 release 在同一锁内
线性化，计数归零先移除条目，再在锁外析构，防止复活并允许嵌套释放。
查询 payload 的调用者必须在整个操作期间持有强引用；查询本身不延长
生命周期。注册表不改变 Rigi 泛型身份或对象内存布局规则。

CoroutineHandle / CoroutineCarriage 是内部示例：Task 与 Task<T> 保持独立
的语言类型和布局，只共同持有 CoroutineCarriage。调度器保留运行所需
原生强引用，协程清理完成后释放；Carriage 不保活已结束且没有其它
持有者的资源。MQ 不使用 NativeRcHandle 或原生队列句柄。

### 26.2 FS 原语层（rigi_rt `fs.c`，施工块 7-2）

`core.fs` 的 OS 操作经私有 native 原语与 VM hook 接入（STDLIB §4.5.9），
两侧同键同语义（错误分类、EOF 语义、挂起行为），公共面在 stdlib
`core/fs/primitives.rg`（全部 internal，7-3/7-4/7-5/7-6 组装）：

- **句柄生命周期**：文件/目录句柄包进 rigi_rt 侧结构（fd/HANDLE + 在途
  槽），经 NativeRc 注册表持有（§26.1 同一底座）；Rigi 层
  `FileHandle`/`DirHandle` 强引用 + `FileCarriage`/`DirCarriage` 弱搬运。
  close 不设独立原语——dispose 释放最后强引用触发析构回调关闭句柄
  （close 错误不上报：持久化错误归 flush 面，§4.5.6）。
- **同步/挂起分界**：read/write/flush 等待数据传输或持久化（可能任意
  久），挂起两段式——start 把阻塞系统调用卸载到现起 detached 后台线程
  （绝不占 Compute Worker，§3.3），返回 0 = 已卸载；Rigi 层 yield 一次性
  EventAlarm 挂起，完成登记（结果登记 happens-before 事件触发，恢复必见
  结果）后 rigi_event_signal 唤醒，take 取结果。open/seek/tell/
  getLength/setLength/stat/lstat/realpath/mkdir/rmdir/unlink/rename/
  diropen/dirread 是命名空间/元数据操作，本地文件系统上通常快速返回，
  同步直调。挂起期间句柄由 FileHandle 强引用保活、缓冲区借用（§3.2，
  stdin Span 借用先例）；同一句柄同类同时刻至多一个在途操作，冲突属
  违反 §4.4 契约，宿主面防御诊断（native abort / VM 抛错）。
- **错误归一**：C 侧只传归一错误码（类 errno 钉死值，Windows
  GetLastError 与 Linux errno 各自映射到同一套码；VM 侧 .NET 异常经
  Win32 HResult / errno 映射到同一套），映射 FileSystemErrorKind 的
  分类表在 Rigi 层（`fsErrKind`，三处同值；链接循环不伪装 NotFound，
  §4.5.3）。
- **路径编码**：String（UTF-8）传入；Windows 严格转 UTF-16 走 `_w` 族
  （非法序列拒绝 → InvalidNameEncoding，不替换不跳过，§4.5.2），绝对
  路径超长时内部加 `\\?\` 前缀（内部原生前缀不作用户路径语法扩展）；
  Linux 直接用 UTF-8 字节（目录名经严格 UTF-8 校验，无法无损表达的
  名称失败整个目录读取，§4.5.2）。变长输出（realpath/目录条目名）经
  out/meta 缓冲协议，不足回正数哨兵与所需长度，Rigi 层放大重试一次。
- **追加的系统保证**：Append 每次写入的位置由 OS 在该次写系统调用内
  原子选择到当时末尾——Windows 仅 FILE_APPEND_DATA（不含
  FILE_WRITE_DATA）的句柄上 WriteFile 忽略句柄当前位置、Linux
  O_APPEND fd 的 write(2) 落当时末尾；不能用用户态 Seek(End)+Write
  模拟（两步之间其他写入者可增长文件致本笔覆盖），也不能只在打开时
  定位一次末尾。注意 .NET FileMode.Append 不具备该语义（两平台都只是
  普通写句柄、写经显式 offset 提交），VM 侧经追加专用互操作打开并单
  系统调用写入（`Bil/Vm/VmDispatch.cs` 的 OpenAppendStream /
  AppendWriteCore，与 native 面同语义）；不承诺跨进程单笔大 write 或
  整行的原子性（§4.5.6）。
- **setLength 的实现义务**：缩短截断、增长补零、成功后游标保持不变
  （即使已在新末尾之后）——Windows `SetEndOfFile` 不定义扩展区域内容，
  由实现显式写零补齐，不假定宿主保证（§4.5.6）。
- **stat/lstat 创建时间的可信度**：Windows 保持 FILETIME/.NET 的可得时间；Linux x64 的 VM 从同一次 libc `stat/lstat` 主体快照取得类型、长度、mtime/atime 与 dev+ino，第二次 libc `statx(AT_FDCWD, path, follow ? 0 : AT_SYMLINK_NOFOLLOW, STATX_BTIME)` 只补 birth，且必须同时核 `stx_mask & STATX_BTIME`、dev major/minor 与 inode、合法纳秒。两次查询使用同一次 cwd 基准的原始路径（不词法消除符号链接前的 `..`）；身份变更/辅助入口缺失/权限错误/文件系统无 birth 仅将 birth 写不可得哨兵，不覆盖成功主体或伪造 ctime/mtime。.NET Unix `CreationTimeUtc` 可能回退，不能用于 birth；statx 纳秒直接拆为毫秒与纳秒余量（不经 DateTime 的 100ns 截断），其余平台不新增 birth 可得承诺。Linux x64 statx 互操作使用完整 256B ABI 镜像而非短前缀，偏移与结构大小用 C `sizeof/offsetof` 探针及 VM 布局断言锁定（§4.5.3）。
- **文件打开的目录拒绝**：Linux `open(O_RDONLY)` 可成功打开真实目录，
  不能以 open 成功代替文件类型验证；native `fs_open` 成功后按已打开
  fd 的 `fstat` 判目录并关闭句柄报 IsDirectory，`fstat` 出错保留原
  errno 且关闭句柄。不能用事先 stat 路径分类，以免与实际打开对象
  不一致；文件复制目录源同样在此打开边界受控拒绝（§4.5.7）。
- **rename 的不替换保证**：NoReplace 用系统机制（Windows `MoveFileExW`
  不带 REPLACE_EXISTING；Linux `renameat2(RENAME_NOREPLACE)`，VM 面经
  `Bil/Vm/VmDispatch.cs` 的 libc.so.6 固定参数 P/Invoke（AT_FDCWD 双路
  径同一次系统调用内同一 cwd 基准解析，glibc ≥ 2.28 导出；缺库/缺入口
  /EINVAL/ENOSYS/EOPNOTSUPP 受控归 Unsupported），native 面经
  `SYS_renameat2`），文件/目录/符号链接（含断链，末段不跟随）统一由内
  核原子「检查+移动」裁决；宿主或文件系统不能提供该保证时显式报
  Unsupported（如 WSL2 DrvFs/9P 挂载对 RENAME_NOREPLACE 返回 EINVAL，
  VM/native 同归一码），不以 exists + 覆盖 rename 查询模拟承担契约，
  也不跨文件系统退化复制删除（EXDEV → CrossDevice）；目录源的
  Replace 也使用系统 RENAME_NOREPLACE，防止目标在检查后变成空目录
  而被普通 rename 静默覆盖；Linux 目标已是目录归 IsDirectory，已是
  文件/链接归 AlreadyExists，Windows 维持既有宿主错误映射。文件/链接源的 Replace 保留系统覆盖 rename（不能
  回退到 NoReplace），VM 不用 Unix File.Move(overwrite:true)：该 BCL
  跨设备时会复制并删除源，违背 CrossDevice 契约。符号链接源末段按条目
  分类，目标查询仅用于系统调用失败后的错误归类，不能充当并发保证。
  源路径类型由事前 lstat/File.GetAttributes 分类；保证范围是调用期间
  该源路径仍为所判定条目类型，外部同时换掉源路径可使分支判定失效，
  不宣称具备跨进程源路径换型隔离（§4.5.7）。
- **diropen 打开面的异常边界（VM 侧 `Bil/Vm/VmDispatch.cs` FsDirOpen）**：
  OS 打开目录的时机是 BCL 实现细节——既可在 DirectoryInfo/GetEnumerator
  建立期（.NET 10 双平台实证：缺失/非目录多在 GetEnumerator 抛出，Unix
  构造期即 openat），也可推迟到首次 MoveNext；三段同属一次「打开」的生
  命周期，必须共用同一受控异常边界，不按平台特判（边界不变量）。失败
  路径绝不发布 token：未建枚举器不释放（不空解引用），已建就地释放
  （枚举器 Dispose 是纯句柄关闭不上抛，不遮盖主错误），登记中途失败
  回滚强引用计数与登记再释放；错误分类沿用 MapFsError + 文件挡路补查
  NotDirectory（§4.5.9 归一码出仓，宿主异常不漏出）。
- 双宿主一致性以 e2e 语料 `fs_primitives.rg`（NativeE2E「fs 原语对拍」
  复用）与 VM 机制套件 `Tests/VmFsNoReplaceTests.cs`（文件/目录/断链与
  链接条目、并发争用恰一成、跨设备 EXDEV）验证；NoReplace 的 Linux 生
  效范围取决于宿主文件系统（WSL2 DrvFs/9P 等不支持 RENAME_NOREPLACE 的
  挂载按契约报 Unsupported）。目录源 Replace 的 Linux 原生机制还由
  `Tests/NativeE2ETests.cs` 独立 C FFI 探针直接调用最终链接的
  `rigi_fs_rename`，验证已有空目录/文件/断链目标不覆盖、文件/链接
  原有覆盖与双线程目标争用（Barrier、超时和失败者源保持）；不以仅
  VM 通过替代原生保证。
- **rigi_rt 现场编译纪律（RigiRtBuilder unity 单翻译单元）**：glibc
  feature-test 宏（收口在 `RigiRtBuilder.RtFeatureMacros`）必须在任何
  系统头之前生效——`-std=c11` 下显式定义 `_POSIX_C_SOURCE` 会抑制
  glibc 默认派生的 `_DEFAULT_SOURCE`，`realpath`/`syscall`/`DT_*`
  （守卫 `__USE_MISC`）随之不可见；宏集合三处同源派生（clang 命令行
  `-D`、unity.c 前导 `#define`、内容哈希）。`-DNAME=VALUE` 转为
  `#define NAME VALUE` 时只替换首个分隔等号，值内的等号保持不变；无值
  `-DNAME` 的宏体为 `1`，显式空值保持空宏体。**编译参数、特性集合或
  unity 生成规则变化必须换缓存键**：实际生成的 unity 文本也纳入内容
  哈希，不能只哈希原始 C 文件与 `-D` 列表。旧 bitcode 不得复用；一切改动以真实 native 管线
  验证（WSL clang 现场编 rigi_rt 并**运行**产物），Windows 可编过不代
  表 Linux 面正确（realpath 的跨分配器配对（glibc malloc 指针流进
  `rigi_track_free` 即段错误）这类运行期缺陷只有真实运行才暴露）。
