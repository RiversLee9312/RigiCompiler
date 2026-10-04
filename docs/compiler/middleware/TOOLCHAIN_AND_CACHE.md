# Middleware 工具链、运行时编译与缓存

> 本文完整承接旧 Middleware 架构的相应章节；原编号用于契约定位。入口：[Middleware 架构索引](MIDDLEWARE_ARCHITECTURE.md)。

## 1. 工具链、目标、缓存与链接身份

工具链调用默认设十分钟期限，超时终止本次子进程树并报告受控错误；双路输出读取也受期限约束。`--verbose`/日志记录所用 clang 的路径与 SHA256，运行时 bitcode 缓存身份包含该摘要。部署方可通过 `RIGI_LLVM_SHA256` 钉住可信 clang 内容；未设置钉值时，`--toolchain`、`RIGI_LLVM` 和本机工具链仍属于用户显式信任输入，内容指纹本身不证明发行来源。

**运行时目标与缓存防线**：libLLVM 的 `LlvmHost.HostTriple` 是生成 LLVM 模块的唯一目标三元组来源；`rigi_rt` 现场 clang 编译和最终 clang 驱动链接都显式传入该模块目标（`--target=<triple>`）；Windows clang 会从本机 VS 自动补 MSVC 版本，编 bitcode 时另以 `-Xclang -triple -Xclang <triple>` 钉住 cc1 的精确模块目标，仍由驱动发现 VS/SDK 头文件与 CRT。不能依赖 clang 自身默认 vendor，也不能通过改写 bitcode 文本掩盖编译目标。缓存身份包含有序完整 clang 编译参数（含目标、特性宏、libuv 头目录）、clang 内容 SHA256、全部内嵌源及生成的 unity 翻译单元；不同目标不复用旧缓存。生成模块显式采用同一 TargetMachine 的 data layout；缓存命中与新编译产物均在合并前解析真实 bitcode，核对 triple 及 libLLVM 宿主 TargetMachine 实际 data layout；不相容、无法解析或目标工具链编译失败必须明确拒绝，不能吞掉 LLVM 合并 warning 继续产物。此处比较的是 LLVM 目标布局，外部 CRT/系统库仍由目标 clang 驱动发现；若未来启用不同 CPU/ABI 选项，必须同步纳入编译参数、缓存身份和目标机布局检查。


**whole-program 对象缓存**：缓存边界为合并 runtime 并经过 `default<O2>` 后、最终链接前的 `.o`；查询位于 MwPipeline/IR 构建前。完整 `BilWriter.Write(module)`（保持声明、资源、metadata、函数顺序）与 schema、后端 ABI、build kind、实际 compiler/binding/libLLVM 内容身份、真实目标/data layout、generic CPU/空 features、PIC/默认 code model、object ABI、runtime 编译身份共同构成完整 SHA256 key。`--out` 与临时路径不进入对象 key；`--emit-ll` 保持 merge 前 IR、`--emit-obj` 保持 opt 前对象语义，因此这两类诊断请求绕对象快路。Gate/BilVerifier 与「本地 entrypoint 声明实际具有函数体」的入口门槛恒执行；可变 BilModule 不得并发回写，同一实例的请求串行消费。

runtime 每次用请求级子进程环境快照及同一完整目标/特性参数做 `clang -E -P`，将实际预处理字节（包括系统与 libuv 头文件、环境搜索路径影响）纳入身份，miss 从同一快照以 `-x cpp-output` 和原 codegen 参数编译。私有源目录在预处理展开前用 `-ffile-prefix-map` 映射为 `rigi_rt`，快照输入名固定为 `runtime.i`；不事后改写预处理文本。Linux 工具链身份核对 clang 动态依赖闭包、driver config 与有效 cc1 参数；额外插件/profile/PCH/module 输入或无法确认的依赖闭包安全绕缓存。Windows 的依赖闭包尚未实测确认，当前正常 fresh compile 并绕过 runtime/object 缓存；SHA pin 不因绕缓存而失效。

libLLVM 身份紧随首次真实加载捕获，来自 `Process.Modules` 的唯一实际加载文件，后续验证初始摘要及 Linux 映射 device/inode，不猜搜索目录里的库。compiler 的已加载程序集提供独立 `Location` 时，compiler/binding 同时核对已加载模块 MVID 与磁盘 PE，再保存不可变内容身份；即使 CoreCLR 的 Release runtimeconfig 关闭动态代码，仍分别使用 compiler DLL 与 LLVMSharp DLL，不能以 `IsDynamicCodeSupported` 推断 NativeAOT。无独立托管映像路径的 NativeAOT 使用实际映射的进程可执行文件，Linux 另核 `/proc/self/exe` 内容。托管 PE 验证失败不得退回 runtime 宿主摘要。映射删除、替换、不可读、多候选或身份未知均关闭快路。LLVM global context 与 SharedHostMachine 使用可重入 Monitor lease；短宿主查询/目标验证与完整 create/merge/O2/object/dispose 生命期均遵约，public LLVM helpers要求相同所有权。禁止在持有 LLVM lease 时等待缓存锁。

runtime/object 缓存分别采用每 key 进程 singleflight 与跨进程稳定 sibling `.locks/<完整key>.lock`，锁文件不随 entry 删除；锁内重新校验 digest、修复损坏 entry，将关闭的产物与 manifest 放在 staging 目录后 `Directory.Move` 完整发布。编译总写本次请求私有路径，缓存 I/O 失败正常继续 fresh compile，不吞语义/工具链诊断；对象命中先在锁内复制到请求路径，再释放锁链接。锁顺序为 runtime 准备及短 LLVM 校验结束 → object singleflight/filelock → MwPipeline → 完整 LLVM lease；不嵌套 runtime/object 文件锁。`RIGI_CACHE_ROOT` 可覆盖私有根，POSIX 目录模式为 0700。

每次请求仍解析 libuv/mimalloc/`--link` 并执行最终链接；静态库、lld、CRT 与完整链接参数属于链接记录，不进入对象 key。旁路遥测记录 native-object miss/hit/bypass 与 O2 scope；开启测量时额外记录 clang 真实 linker/CRT 计划和 Linux lld 实际选中的文件摘要。链接失败保留已经验证的对象。Windows 当前仅记录驱动计划与可读显式文件，默认 CRT 实际选择闭包尚未实测完整覆盖，不据此宣称 Windows 实际链接文件摘要齐全。


## 2. 外部依赖与选型裁决

| 部件 | 选型 | 理由 / 备选 |
|---|---|---|
| LLVM 集成 | **LLVMSharp 进程内**（绑定 20.1.2 + libLLVM 20 NuGet runtime 包）；**锁定 LLVM 20** | 无 GC 设施需求使 C API 天花板不咬人（§4.1）；绑定与原生包同大版本对齐。Ubiquity.NET 排除（仅 win-x64）。注：runtime 包仅含 libLLVM 共享库；无 RID 的 `dotnet build`/`dotnet run` 开发回路需显式引用 runtime 包（csproj 已办） |
| 链接器 | **lld，经 clang 驱动（-fuse-ld=lld）；CRT 发现交 clang**；获取链定稿：CI 用 GitHub runner 预装（windows-latest: `C:\Program Files\LLVM`，ubuntu-latest: `/usr/bin/ld.lld`），不耗额外 action 额度；开发机可使用 PATH 工具链；也可通过 `tools/Fetch-LlvmToolchain.ps1` 下载官方 20.1.2 选择性部件缓存 `tools/.llvm/`（gitignored，SHA256 钉版校验） | 编译产物为 .o；链接是唯一保留的外部步骤之一；runner 预装版本漂移可容忍（lld 只链接自产 .o 与 rigi_rt）；native 驱动解析顺序 `--toolchain` → `RIGI_LLVM` → `tools/.llvm/` → PATH |
| GC 引擎底座 | 教学级 Bacon-Rajan C 模板改造 | 候选底座 `rjungemann/turmeric` gc.c（MIT，纯 C、可剥离）；教学参照 `fitzgen/bacon-rajan-cc`（Rust，注释最全）；语义对照 Nim `lib/system/orc.nim`（位打包、rootIdx、自适应阈值）。论文并发版（Red/Orange/transfer buffer）无限期推迟 |
| 分配器 | mimalloc（MIT）为当前必需底座；rigi_track_malloc/free 调 mi_malloc_aligned/mi_free | GC 主堆自研；分配器层与 GC 解耦。16B 台账头、16B 对齐与零泄漏口径由包装层保持；缺失时 native 编译期拒绝 |
| rigi_rt 编译 | **clang 现场编译**（MW1 起；获取链与 lld 同：CI 用 runner 预装 clang，开发机可使用 PATH，或 Fetch-LlvmToolchain.ps1 钉版缓存；实际选择顺序见前行）。产物形态：LLVM bitcode（`-emit-llvm -c`，unity build）+ EmbeddedResource 内嵌源 + 内容哈希缓存；Emit 阶段 `LLVMLinkModules2` 进程内合并进模块，运行时面经统一优化管线内联——C 写的 access helper 由此获得零成本内联 | 预编译 .lib/.a 入库排除（双平台二进制漂移与审查成本）；源码即真相，与本仓库同纪律；clang 编 .c 需 CRT 头文件——Windows 自动探测已装 VS/SDK，Linux 用系统 glibc 头文件 |
| 事件/定时底座 | **libuv**（MIT，静态链接）；获取链定稿（MW11b）：官方无预编译二进制，渠道②=钉版源码 + 本地构建——`tools/Fetch-Libuv.ps1` 下载 GitHub tag v1.52.1 源码 tarball（SHA256 钉版校验；dist.libuv.org 的 dist tarball 是 autotools 形态无顶层 CMakeLists.txt，弃用），cmake 最小配置（`-DBUILD_TESTING=OFF` + Release，win 优先 tools/.llvm clang-cl+Ninja、缺退 VS 生成器；linux clang/cc）构建静态库，缓存 `tools/.libuv/<rid>/`（`lib/uv_a.lib` 或 `lib/libuv_a.a` + `include/` + VERSION.txt，gitignored，CI 双平台 job 各跑一步）。解析顺序 `--libuv-dir` → `RIGI_LIBUV` → `tools/.libuv/<rid>` → 编译器 exe 旁 `.libuv/<rid>`；`native --out` 命中时链接行追加静态库全路径 + 系统库（win：psapi user32 advapi32 iphlpapi userenv ws2_32 dbghelp ole32 shell32；linux：pthread dl），未命中在 native 编译期明确拒绝（C 侧 abort 桩仅为防御性底层构建形态，不是 CLI 降级路径）；rigi_rt 命中时带 `-I<include>` + `-DRIGI_HAS_LIBUV=1` 编译（参数入内容哈希；C 侧 uv 用法一律包 `#ifdef RIGI_HAS_LIBUV`） | 跨平台事件循环 + 定时器 + 线程池 + 同步原语一体；win-x64（IOCP）/linux-x64（epoll）均一等公民；每 Worker 一个 loop，EventAlarm/sleep 以其为底座；Worker 唤醒走 `uv_async_send` |
| 协程降级 | **自做状态机**，不用 `llvm.coro.*` | llvm.coro 跨版本 ABI 不保证兼容；frame 内精确根映射不可控（Rust 弃用先例）；RUNTIME §21 要求精确活跃引用映射 |
| native FFI | 编译期直接生成调用，**不用 libffi** | ABI 编译期已知；libffi 只服务运行时动态签名场景 |
| 可嵌入 GC 库 | **不采用** Boehm / MPS | Boehm 保守、非移动、位图粒度粗；MPS 重量、学习曲线陡；均不匹配「编译器握全 refMap」的精确模型 |

---

### mimalloc 获取与链接

`tools/Fetch-Mimalloc.ps1` 钉版官方包及 SHA256，缓存 `tools/.mimalloc/<rid>/`，
CI 在 publish 前预取。解析顺序 `--mimalloc-dir` → `RIGI_MIMALLOC` →
`tools/.mimalloc/<rid>` → 编译器旁 `.mimalloc/<rid>`；静态库为 Windows 的
`mimalloc.lib`、Linux 的 `libmimalloc.a`，布局含 `include/mimalloc.h`。
运行时 C 源以 extern 引用 mi_*，不需要向 clang 添加 mimalloc include。
可执行/动态产品追加静态库与 Windows advapi32 / Linux pthread、dl；静态产品由
宿主另行链接其 uv/mimalloc 依赖。NativeCommand 对 libuv、mimalloc 缺失均返回受控
编译失败，不生成依赖缺失的运行期 abort 产品。

相关章节：[总览/优化 §1、§9](OVERVIEW.md)、[内存 §4](MEMORY_MANAGEMENT.md)、[产品 C ABI §7](LAYOUT_AND_ABI.md)。
