# RigiCompiler 开发指南

> 构建、运行、测试与开发约定；架构与代码库结构见 [architecture.md](architecture.md)；本文件由 AGENTS.md §2/§5/§6/§7/§8 拆分而来。

## 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、0 警告
dotnet clean
```

生产项目配置为 `RigiCompiler.csproj`，独立测试项目配置为 `Tests/TUnit/RigiCompiler.Tests.csproj`；实际仓库根包含 `.git` 与 solution（当前环境 `/workspace/RigiCompiler`）。依赖纪律：原则上纯 BCL、无第三方依赖——豁免以下用途，新增前必须先讨论并同步本文档：**编译器托管侧是 Middleware 的 LLVMSharp.Interop + libLLVM（锁定 LLVM 20）**（选型裁决见 `docs/compiler/middleware/MIDDLEWARE_ARCHITECTURE.md` §2）；**模块配置侧是 YamlDotNet 18.1.0（仅 RepresentationModel 手工严格 schema 解析，禁止反射 Deserializer）**；**native 产物运行时侧是 libuv（协程事件底座，MW11b 起）与 mimalloc（GC 分配器，GC Phase 2 起）两个静态链接 C 库**（均经 tools/ 下 Fetch 脚本钉版预取，见下文）。注意：libLLVM 原生资产经 runtime.json 传递、只在带 RID 时解析，csproj 已显式引用 win-x64/linux-x64 两个 runtime 包以支持无 RID 的 `dotnet build`/`dotnet run` 开发回路。csproj 另开 `AllowUnsafeBlocks`，仅限 `Middleware/Emit/LlvmBitcode.cs` 的 libLLVM 指针编组封装使用。新增依赖前必须先讨论并同步本文档；**优先复用现有的、高质量且久经验证的轮子（仓库内设施优先，外部库须成熟可靠），不重复造轮子**。独立测试项目使用锁定版本 TUnit，传递依赖为 Microsoft.Testing.Platform 与 TRX 扩展；不加入 Microsoft.NET.Test.Sdk/Coverlet，也不把框架引用传递进生产项目。`global.json` 选择 .NET 10 与 MTP runner，solution 同时构建生产和测试项目。另有 `RigiCompiler.sln`。

Middleware 的 C 工具链（MW1 起编译 rigi_rt 与 lld 链接所需）：CI 用 runner 预装 clang/lld；开发机 PATH 优先，缺则跑 `pwsh tools/Fetch-LlvmToolchain.ps1`（钉版官方 20.1.2 选择性部件，缓存 `tools/.llvm/`，gitignored）。详见 `MIDDLEWARE_ARCHITECTURE.md` §2 链接器/rigi_rt 编译行。`native --out` 走全链：clang 驱动（`-fuse-ld=lld`）链接 CRT 出可执行文件；`--emit-obj`/`--emit-ll` 免工具链（中间产物与合并 rigi_rt 前的黄金快照）。rigi_rt 实际预处理快照、工具链内容或 codegen 参数变化会自动换缓存身份。普通 native/module 请求缓存合并 runtime 后的 whole-program O2 对象，命中跳过 Middleware/IR/O2/object，仍按当前链接输入 relink；诊断产物请求绕快路。缓存根可用 `RIGI_CACHE_ROOT` 独立覆盖，缓存 I/O 故障自动退化，不需要清用户缓存；身份与锁顺序详见 Middleware 架构指南。

libuv 静态库（MW11b 起协程 Alarm 族事件底座）：CI 双平台 job 各跑一步 `tools/Fetch-Libuv.ps1`（runner 预装 cmake）；开发机跑 `pwsh tools/Fetch-Libuv.ps1`（钉版 GitHub tag v1.52.1 源码 + SHA256 校验，cmake 现场构建静态库，缓存 `tools/.libuv/<rid>/`，gitignored，幂等 / `-Force` 重建）。native 解析顺序 `--libuv-dir` → 环境变量 `RIGI_LIBUV` → `tools/.libuv/<rid>` → 编译器 exe 旁 `.libuv/<rid>`；命中时 `--out` 链接行追加静态库 + 平台系统库、rigi_rt 带 `-DRIGI_HAS_LIBUV=1` 编译；未命中为编译期明确拒绝（review-20260910 用户裁定：旧行为是运行期 abort）。详见 `MIDDLEWARE_ARCHITECTURE.md` §2 事件/定时底座行。

mimalloc 静态库（GC Phase 2 起 rigi_rt track 台账的底层分配面，替代 UCRT malloc/free 对）：CI 双平台 job 各跑一步 `tools/Fetch-Mimalloc.ps1`（**须在 publish 之前**）；开发机跑 `pwsh tools/Fetch-Mimalloc.ps1`（钉版官方预编译包 v2.5.1 + SHA256 校验，提取静态库与头文件，缓存 `tools/.mimalloc/<rid>/`，gitignored，幂等 / `-Force` 重建；官方 release assets 自 v3.x 起附带预编译产物，无需现场构建）。native 解析顺序 `--mimalloc-dir` → 环境变量 `RIGI_MIMALLOC` → `tools/.mimalloc/<rid>` → 编译器 exe 旁 `.mimalloc/<rid>`；`--out` 链接行恒追加静态库 + 平台系统库（win advapi32 / linux pthread+dl），未命中为编译期明确拒绝——`rigi_track_malloc/free` 是 rigi_rt 对象分配唯一收口，缺 mimalloc 符号必然链接失败。rigi_rt 侧仅 extern 声明 `mi_malloc_aligned`/`mi_free`，不经头文件；对象 16B 对齐与 memtrack 零泄漏口径不变。

`RIGI_RT_MEMTRACK=1` 打开 rigi_rt 内置堆台账：进程退出时未释放块即 stderr 报告并以 exit 1 失败。NativeE2E 对拍跑产物进程时默认开启（泄漏即该用例失败）；日常 `native --out` 不设此变量。

### 2.2 运行

CLI 结构为 `<COMMAND> [--sub-cmd [args...]...]`，顶层 COMMAND 六个：`compile` / `test` / `vm` / `native` / `module` / `help`。裸 `dotnet run` 等价于 `help`。

```bash
dotnet run -- test --all                 # CoreCLR 全量（迭代用；提交前验证见 §2.3，不能只跑本命令）
dotnet run -- test                       # 打印测试套件菜单（编号 + 名称）
dotnet run -- test --run 1 7             # 按编号运行指定套件（字面量 + 形参列表）
dotnet run -- compile --file a.rg                    # 编译（语义分析 P1–P3 + 诊断输出；无后端子命令时只到语义）
dotnet run -- compile --file a.rg --parse-only       # 只解析，AST 以 JSONL 输出到 stdout
dotnet run -- compile --file a.rg --parse-only --dump-ast ast.jsonl   # AST JSONL 写文件
dotnet run -- compile --file a.rg --sema-only        # 只跑语义分析（P1–P3），输出诊断后结束
dotnet run -- compile --file a.rg --emit-bil a.bil   # 语义通过后发射 BIL（先经 BilVerifier 验证）；
                                                     #   §17.2 按命名空间切分：全局写 a.bil，其余写 a.<ns>.bil
dotnet run -- compile --file a.rg --explain-dispatch # 派发链诊断报告（烘焙链 + 降级路由，RUNTIME §15）
dotnet run -- module --init --root app               # 创建 schema 1 模板，显式依赖 stdlib
dotnet run -- module --run --root app                # 默认 debug profile，在 VM 中自动构建后运行
dotnet run -- module --run --root app --profile release -- first "two words" # native profile 与原样程序 argv
dotnet run -- module --publish --root app --profile debug # 发布 YAML type 指定的真实 Native 产品
dotnet run -- module --bundle --root app --output app.zip # 先发布，再打包 ZIP
dotnet run -- module --install app.zip --root client      # 安装依赖到 client
dotnet run -- vm --file a.bil a.core.bil ...         # 加载执行 BIL（§17.2：须传入全部切片还原完整模块）
dotnet run -- vm --file a.bil a.core.bil --entry-point <符号>   # 多 entrypoint 时显式选入口
dotnet run -- native --file a.bil --out app.exe        # BIL → 原生可执行（clang 驱动 lld 链接 CRT，需 C 工具链）
dotnet run -- native --file a.bil --emit-obj a.o       # 免工具链：进程内发射目标文件
dotnet run -- native --file a.bil --emit-ll a.ll       # 免工具链：合并 rigi_rt 前的 .ll 黄金快照
dotnet run -- help                       # 全部 COMMAND 与子命令概览（文本由注册表程序生成）
dotnet run -- help compile               # 单个 COMMAND 详情
dotnet run -- help compile.file          # 单个子命令详情（子命令名不带 -- 前缀）
```

`module --run` 从根目录的 `module.yaml` 选择默认或显式命名 profile，按依赖 DAG 消费独立 API/BIL，仅编译本模块源码；target 决定 VM 或 native。配置 `entry` 使用完整 BIL canonical，缺省要求唯一标记入口。入口返回 i32 时原样转递退出码；无参数入口忽略程序 argv，`main(args: Array<String>)` 接收 `--` 后的所有参数（不含宿主 argv0）。构建失败不启动程序，诊断走 stderr，stdout 留给程序。顶层 `run` 已由 `module` 替换；低级 `compile`、`vm`、`native` 仍可直接操作源码或 BIL。

schema 1 配置要求 `name`、语义版本 `version` 与 `type`，拒绝未知/重复字段、YAML
别名及越界路径。`type` 为 `executable`、`static-library` 或 `dyn-library`；`source`
默认为 `['**/*.rg']`，相对自身 `source/`。`profiles` 的名字由配置声明，`target` 为
`vm` 或 `native`；初始化模板显式提供默认 `debug`（VM）和 `release`（Native）。
`product` 可指定模块根内相对目录，缺省为 `product/<profile>`；`entry` 可放在根
或 profile 内，profile 优先，且只能选择当前模块自己的函数。

`--publish` 和 `--bundle` 均生成真实 Native 产品，profile 的 VM target 仅影响
`--run`。库不能 `--run`。可执行产品为 `app`（Windows 为 `app.exe`）；库为
`lib<name>.a` / `lib<name>.so`（Windows 为 `<name>.lib` / `<name>.dll`）。
所有产品及资源放在 `PRODUCT/modules/<name>/<version>`。库配置通过完整 BIL
canonical 指定 C 名称到本模块实现的映射，例如：

```yaml
schema: 1
name: arithmetic
version: 1.0.0
type: static-library
dependencies: [{name: stdlib, version: 1.0.0}]
exports:
  arithmetic_add: 'arithmetic::$add(a:.i32,b:.i32)@.i32'
```

C 导出只接受有本地函数体的公开、同步、非泛型全局/静态函数，宿主类型也不得
开放泛型。参数/返回支持固定宽整数、float/double、bool 和 char，返回另支持 void。
对象、String、泛型、实例 receiver、可变参数、native import、挂起、任务发布及不能
确定目标的间接/虚/接口调用明确拒绝，包括构造器和 wrapper 安装器的隐式闭包。
生成 `<name>.h`；静态产品另附 `<name>.pc` 与独立 `native-dependencies` 归档，
宿主可设置自己的 `PKG_CONFIG_PATH` 后使用 `pkg-config --cflags --libs <name>`。
带空格路径仍作为完整参数消费；归档不嵌套其他归档。
内建标准库静态产品默认导出 `rigi_std_abs_i32`、`rigi_std_min_i32` 和
`rigi_std_max_i64`，均调用实际标准库实现。

C bool 为 `uint8_t`（输入非零为 true），char 为 `uint32_t` Unicode scalar。
每个最终映像有一份 Rigi runtime，初始化 GC、所有 singleton 与 DAG 全局初始化
一次；C API 不运行 Rigi main。所有 API 只能在同一宿主 OS 线程同步调用，库须
保持加载至进程退出，不支持 dlclose 或多个 runtime owner。未捕获异常打印真实
Rigi 类型及消息并终止进程，异常不跨 C 边界。Rigi 模块消费者导入接口/BIL，由
最终应用合并实现；不能同时链接这些自带 runtime 的 Native 库。

`hooks` 每项声明 `phase`（before/after-publish 或 before/after-install）、
`environment`（all/linux/windows）、单行 shell `command` 与可选 `inputs`。
环境匹配实际宿主，独立于 profile target。子进程工作目录为当前模块根，
`RIGI_SRC`、`RIGI_RES`、`RIGI_ARTIFACT` 指自身目录，`BUILD_ROOT`、`PRODUCT`
指入口共享根；不修改父进程环境。stdin 关闭，超时或非零退出使当前操作失败。

模块 ZIP 保持根 `module.yaml`、源码、资源、发布的 `artifact` 与声明的 hook 输入；文件顺序、时间戳固定。安装使用 `module --install <ZIP> --root <入口目录>`，落在入口的 `dependencies/<name>/<version>`；大小写冲突、路径越界、符号链接、特殊节点与不匹配的嵌入依赖身份均拒绝。完整验证和 before-install 在同父级临时目录中完成，原子提交后执行 after-install；失败删除本次新安装。同身份、同 ZIP 摘要的重复安装不覆盖文件，也不重复执行安装 hook；同版本不同内容明确拒绝。信任位不从 ZIP/receipt 恢复。

资源规则从每个模块自身 `resources` 读取，复制到共享 `PRODUCT/modules/<name>/<version>/<destination>`，文件/目录及大小写冲突在替换前检查。产品目录的锁覆盖 after-publish 与提交/回滚，发布失败恢复旧产品；安装与发布事务只管理自身目录，hook 的外部副作用不属于回滚范围。

诊断子命令（`compile` 与 `test` 共有，可组合）：

```bash
dotnet run -- test --all --verbose      # 控制台输出 verbose 级日志（默认只显示 Warning+）
dotnet run -- test --all --log-to run.jsonl   # 全量日志（含 verbose）以 JSONL 落盘
```

### 分阶段遥测与代表输入性能基线

性能测量默认关闭。设置 `RIGI_PROFILE_DIR=<目录>` 后，编译器只向该目录写每进程独立的 `metrics-<pid>-<uuid>.jsonl`，不改变 stdout/stderr；无需开启会逐 token 输出的 `--verbose`。JSONL 的 `scope` 记录包括 phase、父 scope ID、完成/失败状态、wall、当前进程 CPU 差值、托管 GC 分配差值、阶段结束 RSS 和**进程生命周期** RSS 高水位。后者不是阶段峰值；托管 GC 分配不包含 LLVM/C 原生堆。子工具另以 `child-process-lifetime` 记录；Linux 已退出进程的最终计数不可读时保留 `lastObservedChildCpuMilliseconds` / `observedChildLifetimePeakRssBytes`，采样间隔明确为 100ms，不把存活采样冒称最终计数。遥测覆盖文件 Lexer/Parser/AST 校验、stdlib、P1–P4b/Verifier、测试 suite、每个 Middleware stage、LLVM IR/runtime bitcode/merge/O2/object/final link 和外部进程。异常和受控非零结果均标为 failed；不可写的遥测目录静默禁写。

编译阶段的 `compiler-workers` 事件在 join 后记录 jobCount、grantedCpuSlots
和实际 activeWorkerPeak，并区分 started/completed job 数和失败/取消状态；
串行快路 leaseAcquired=false、grantedCpuSlots=0，workerLimit=1。
配置 jobs=N 不等于实际使用 N 个 worker。
worker 内的 scope 标为 `overlappingProcessCounters=true`，CPU/GC 分配是
重叠的进程区间，不能相加作为阶段成本。性能比较使用外层阶段 makespan、
CPU 和分配差值；endRss 是结束采样，processLifetimePeakRss 仍是生命周期
高水位。前端文件子 scope 同样只用于定位，不能将相互重叠的 wall 相加。
`CompilerParallel` 定向套件对拍完整 AST/Span、诊断原序、Bound/Lowered 描述、
cell 身份、BIL 字节与 VM 输出；保留真实多 worker 观测和失败/取消契约。

`RIGI_CACHE_ROOT=<私有根>` 下的 `runtime-cache`、`object-cache` 与 `module-cache` 分别缓存运行时、最终原生对象与单个模块的接口/BIL 配对产物；未设置时使用 LocalApplicationData/rigi 下的对应目录。缓存事件在真实查验边界记录。冷样本只控制指定内容缓存；不清用户缓存、不更改 HOME、不宣称清除了 OS 页缓存或 C# obj/bin 缓存。

独立模块的源码 selector 相对自身 `source/`，按配置顺序处理，每项命中路径按 ordinal 排序并去重。键包含原始源码字节与路径顺序、配置和实际 profile、编译器内容与模块 ABI、ModuleId/信任来源，以及依赖完整接口摘要与 API 摘要；完整接口中的 BIL 摘要保证 provider 默认值或固定表达式 helper 仅实现变化也使消费者失效。依赖采用入口选中 profile 的同名配置；没有同名配置时选依赖自身 default-profile。依赖的 `RIGI_SRC/RES/ARTIFACT` 指自身，`BUILD_ROOT/PRODUCT` 仍属于入口。

before-publish 每次请求都先执行，再固定源码和声明的 hook 输入；after-publish 每次请求也执行。两阶段的命令、实际宿主、声明输入字节和注入环境均参与键；任意 before-publish 没有声明输入时保守绕缓存。接口/BIL 作为一个 `module.rgi` 配对缓存，损坏会重建，语义失败不发布新条目；成功 receipt 仅在当前模块发布步骤和 after-publish 均成功后原子替换，旧 receipt 保留到该边界。源码仍存在时每次重新计算内容键；源码缺席时只消费自身 `artifact/<profile>/module.rgi` 的已发布产物，校验当前依赖和完整链接闭包，此状态记录为 prebuilt，与内容键 hit 分开。内建标准库信任只来自编译器 resolver，磁盘包及同名普通模块不能自授。

独立 BCL 工具位于 `tools/PerfBaseline`，主项目排除其 C# 文件，二者串行构建。只运行 profile 指定的代表程序和受影响测试；套件通过 `test --inventory` 的注册名动态解析编号。清单完整发现所有套件、可枚举用例、慢门控与 fuzz 种子/预算，旧单块套件保持 suite 粒度，`PassCount` 是断言数。通用 ParallelSuiteRunner 支持 `--suite-args list` 纯枚举和 `--suite-args label <精确标签...>`；未知标签整批返回 2，绝不回退全套。Native/E2E 保留现有按名/数值选择；profile 按已发现标签校验，E2E 子串会扩大选择时明确拒绝。Middleware 的 `COMP-003` 组从现有 case 数组派生统一标签列表（`test --run 57 --suite-args COMP-003`），只覆盖对象/runtime 缓存、LLVM 所有权与真实冷/hit/自愈/relink/诊断旁路；精确标签仍用 `--suite-args label <ExactLabel>`。CommandLineParser 的 `PERF-001` 组只执行纯解析及 inventory 契约；Binder/BilEmitter 的已注册定向组可通过 `groups` 选择，不能据此推断整个 suite 都已覆盖。

```bash
# Linux 命令均关闭 stdin 并设总看门狗；Windows 外层用 Watch-Command.ps1。
timeout --kill-after=10s 180s dotnet build </dev/null
timeout --kill-after=10s 120s dotnet build tools/PerfBaseline/PerfBaseline.csproj </dev/null
timeout --kill-after=10s 60s dotnet tools/PerfBaseline/bin/Debug/net10.0/PerfBaseline.dll self-test </dev/null
timeout --kill-after=10s 1000s dotnet tools/PerfBaseline/bin/Debug/net10.0/PerfBaseline.dll run tools/PerfBaseline/representative.json playground/perf-baseline-new </dev/null
```

profile 的 `compiler.fileName/arguments` 指定实际编译器（CoreCLR 可用 dotnet + DLL；AOT 可直接用 exe）；`repoRoot`、`timeoutSeconds`、`budgetSeconds`、`warmupRuns`、`hotRuns` 与 `environment` 明确实验条件。`commands` 接受任意构建/发布/编译/运行命令，使用参数数组，支持 `{repo}`、`{output}` 与单个 `{glob:路径模式}` 的有序展开；`inputs` 保存输入摘要（glob 展开的输入也逐一摘要），`toolchains` 保存工具版本与实际 PATH 解析产物摘要，`dependencyArtifacts` 可记录 libLLVM/libuv/mimalloc 等实际依赖产物摘要。代表 profile 的依赖路径为 Linux 布局，Windows/AOT 测量需按实际发布布局调整。`suites` 接受注册名以及互斥的 `labels`、`groups`、`range`，每条命令/套件可覆盖 hotRuns。所有输出目录必须为空；每个工作负载用独立私有内容缓存，固定冷一次、预热至少一次、hot 至少一次。可靠基线要求 hot ≥ 3；单样本标 exploratory，不能用于宣称提速。比较时应固定源码/编译器产物摘要、机器、配置、输入、完整参数、工具版本与采样模式。C# publish 可以在 commands 中设 `category: "csharp-publish"`、`fileName: "dotnet"`、`arguments: ["publish", "-c", "Release", "-r", "linux-x64", "-o", "{output}/publish"]`；不选择时清单明确记录 not-selected。

机器资源同时记录 .NET 有效 CPU 数、GC 可用内存预算、Linux host CPU 数与 /proc/meminfo，并按 /proc/self/cgroup 和 mountinfo 解析实际 cgroup CPU/内存限制；不可读取时标 unknown。已有采样可用 `machine-resources <输出文件>` 单独补录读取时点，无需重跑工作负载。

输出包括 manifest（机器、源码 HEAD/dirty 与文件摘要、编译器产物摘要、profile、env、工具版本、预算）、原始 `runs.jsonl`、每次 stdout/stderr 日志和阶段 JSONL、完整 discovered-inventory、此次 coverage 和 hot 样本 median/min/max。源码→BIL、BIL→native、native 运行、测试执行、C# build/publish 与端到端分项记录；没有选择的类别不伪报覆盖。工具只采启动进程的 CPU/RSS，不把其子进程开销算入父值；外部工具详见编译器 JSONL。Linux 工具以独立 setsid 进程组清理后代，包括根提前退出但后台仍持管道的情况；Windows 共用受管 launcher 通过 STARTUPINFOEX 的原子 Job/stdio 句柄名单启动，不支持 JOB_LIST 时挂起创建、归入 Job 后恢复。Linux 环境不能实测 Windows，此路径须由 Windows/CI 验证；Linux 无法限制主动 setsid 逃组的程序。

### 2.3 发布（Release = NativeAOT）

Release 配置发布为 **NativeAOT 原生单文件**（约 16 MB，免 dotnet 运行时）：

```bash
dotnet publish RigiCompiler.csproj -c Release -r linux-x64 -o publish/linux-x64   # 产物：publish/linux-x64/rigic
dotnet publish RigiCompiler.csproj -c Release -r win-x64 -o publish/win-x64
```

- **不支持跨 OS 交叉编译**：linux-x64 产物必须在 Linux（如 WSL）上构建；Linux 侧需 `dotnet-sdk-10.0` + `clang` + `zlib1g-dev`。
- **反射靠两份配置保住**：`ILLink.Roots.xml`（`preserve="all"`，保整程序集类型/成员元数据，供 `Assembly.GetTypes()`、`Activator.CreateInstance`、字段/属性反射使用）+ `JsonSerializerIsReflectionEnabledByDefault=true`（强开 STJ 反射序列化，AOT 下默认禁用）。AstJsonl 序列化/反序列化（`--dump-ast`/`--parse-only`）与 ASTIntegrityValidator 依赖它们，删掉会导致 AOT 产物运行时崩溃或静默丢数据。
- **性能注意**：AOT 无 JIT 的运行时优化（去虚拟化/PGO），重接口分派路径比 CoreCLR 慢约 3 倍——fuzz 类套件在 AOT 产物上明显更慢，日常全量测试建议仍用普通构建跑。
- **CI**：`.github/workflows/ci.yml` 按上述流程在 `windows-latest`（win-x64）与 `ubuntu-latest`（linux-x64，均为 amd64）双平台分别发布 AOT 产物并用产物跑全量测试（AOT 不支持跨 OS 交叉编译，只能按平台分别构建）。
- **提交前本地必须同口径**：在**本机已有的 Windows 与 Linux 环境**（本仓库开发机一般为 Windows 宿主 + WSL Ubuntu）各 `publish -c Release` 一次，并用产物跑 `test --all`。禁止只跑 `dotnet run -- test --all` 就提交——那是 CoreCLR 开发回路，不会覆盖 AOT 反射根、RID 原生库与无 JIT 路径。Linux 必须在 Linux 里 publish（不能在 Windows 上交叉编 linux-x64 AOT）。
- **本机 WSL 的 dotnet 路径（环境事实，2026-09-30 实测更新）**：WSL Ubuntu 的 PATH 上现为 **/usr/bin/dotnet = .NET SDK 10.0.112**（满足 net10.0，可直接用）；早期「PATH 是 apt 的 .NET 8、.NET 10 在 `~/.dotnet`」的布局已过时（`~/.dotnet` 仅剩旧 sentinel 残留）。若 `dotnet --version` 不是 10.x 再回退到导出 `DOTNET_ROOT=$HOME/.dotnet` 的旧手法。clang 18 在 /usr/bin/clang，zlib1g-dev 经 apt 装齐。
- **Linux 侧跑全量的工作目录必须在原生 Linux 文件系统上（如 WSL home 的 ext4），不要在 `/mnt/c`（9p/drvfs）里跑**：e2e 探针目录是相对 CWD 创建的；9p/drvfs 的 `renameat2(NOREPLACE)` 返回 ENOSYS，而 rigi_rt 对「不能提供原子不覆盖保证的宿主/文件系统」**刻意报 Unsupported、绝不回退 rename**（rigi_rt/fs.c 注释），于是 fs_copymove/fs_primitives/accept_dir_management 等用例在 /mnt/c 下必败、在 ext4 下全绿——这是环境限制不是产品缺陷。同理 lstat 对「文件/子路径」在 ext4 报 ENOTDIR（契约 §4.5 保留的宿主差异），9p 报 ENOENT 会让平台分支断言真空通过，失去覆盖意义。做法：`cd ~ && /mnt/c/.../publish/linux-x64/rigic test --all`（语料由输出/发布目录优先定位，与 CWD 无关）。

```bash
# Windows
dotnet publish RigiCompiler.csproj -c Release -r win-x64 -o publish/win-x64
./publish/win-x64/rigic.exe test --all

# WSL Ubuntu / 其它可用 Linux
dotnet publish RigiCompiler.csproj -c Release -r linux-x64 -o publish/linux-x64
./publish/linux-x64/rigic test --all
```

---

## 测试策略

- **测试入口并存**：独立 `Tests/TUnit/` 使用 TUnit source generator 与 MTP；框架依赖仅属于测试项目。`Tests/` 下尚未迁移的静态套件继续由 `TestRunner` 驱动。日常只跑受影响 case；双平台 NativeAOT 全量提交/CI 门禁保持有效。
- **全量入口（迭代）**：`dotnet run -- test --all` 自动运行全部套件，任意失败返回非零退出码并列出失败套件名。**提交前入口**是 §2.3 的双平台 NativeAOT publish 产物 `test --all`（与 CI 同口径），不是 `dotnet run`。NativeE2E 跑产物进程时设置 `RIGI_RT_MEMTRACK=1`，泄漏即 exit 1。
- **统一基建**：`Tests/AstDescribe.cs` 是唯一的 AST 描述器（Expr/Stmt/Block/Decl/Root/Type/Symbol 等），`Tests/TestHarness.cs` 是唯一的驱动与断言（ParseRoot/ParseBlock/ParseWithLayer/ParseFirstDecl + Check/CheckTrue/CheckParseError/Summary）。禁止在套件里再写私有 Describe*/Format* 副本与计数样板。
- **断言对象约定**：除查的就是命令行/日志/token 流/层协议行为的套件（Logger、CommandLineParser、LexerFuzz、TokenDisposition）外，一律断言 AST 树产物（AstDescribe 描述串 + 结构断言），不断言控制台输出文本。
- **AST 结构断言**：表达式类测试除描述串快照外，还应断言结构性事实（Root 是否存在/已填充、Expression 的具体类型、Parent 链、子 Root 填充、无节点共享）——快照不能作为唯一验证方式。
- **独立 Layer 测试**：经 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)` 驱动（`TestHarness.ParseWithLayer` 封装）。`TestRootParserLayer` 只接受 EOF——被测 Layer 提前结束或漏消费普通 token 会立即失败，能发现 Layer 边界问题。
- **约定：每新增一个 ParserLayer，必须在 `Tests/` 添加对应测试类，并在 `TestRunner` 注册表注册（`test` 菜单与 `test --run N` 的编号即注册表顺序）。**
- **fuzz 动态批次超时**：`SemanticsFuzz`/`StressFuzz` 全局索引按小批进入共享 pending，默认**不限时**等待（保留旧口径；caller cancellation 仍终止并排空进程树）；Semantics 需要时限时用 `--suite-args <from> <to> child-timeout-ms=<毫秒>` 显式给出（套件参数不能带 `--` 前缀，会被解析成 test 子命令）。
- **NativeE2E 定位与按名运行**：`test --run 58 --suite-args` 三选一——①`list`：只打印当前真实索引+Label 清单（不启动编译，无 clang 也可用；**定位/验收一律以此为准，禁止凭记忆猜索引**）；②首参为整数：保持原 `from to` 区间语义（含不完整区间的用法报错）；③其余非空参数：按 Label 子串过滤（OrdinalIgnoreCase Contains，父入口交给隔离 worker、[PASS]/[FAIL] 行自带标签），零匹配退出 2。示例：`--suite-args list`、`--suite-args 545 545`、`--suite-args Parcel 动态访问`。
- **挂死调试纪律（必须设超时）**：调试可能引入死锁/活锁/进程不退出的改动（调度器、线程、等待-唤醒协议、quiescence 类计数）时，**任何测试运行都必须带超时**，禁止裸跑无限等待：① 优先用单例进程内复现通道（`--suite-args <i> <i>`）逐条验证，先单例绿再跑并行；② 必须跑子进程/产物进程时显式给超时（套件的 `child-timeout-ms`、或 shell 层看门狗 `tools/Watch-Command.ps1`，见下条）；③ 运行被中止后先检查并结束残留的 rigic/dotnet 测试子进程（文件锁会干扰重跑），再继续——`pwsh tools/Watch-Command.ps1 -CleanupOrphans` 一键清扫。
- **shell 层看门狗（tools/Watch-Command.ps1，MW12 起常驻公共工具）**：任何可能挂死/留孤儿的命令（dotnet test、native 产物、手工探测）都必须经它带超时拉起：`pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList "run -- test --run 58" -TimeoutSeconds 900`。行为契约：子进程 stdout/stderr 原样穿透；正常结束时退出码 = 子进程退出码、job 关闭顺带清扫残留孙进程；超时灭整树并退出 **124**；参数校验失败退出 2；启动失败退出 127。`-ArgumentList` 是**原始参数字符串**（经 powershell.exe 命令行传参时逗号不会拆分数组，含空格的参数自行加双引号）。另注意 PowerShell 关键字参数模式下 `exit $p.ExitCode` 会被拆成 `$p` + 字面量 `.ExitCode`（透传退出码须先落局部变量），cmd 行内 `%ERRORLEVEL%` 在整行解析期展开（测上一条命令退出码须单独一行）。实现遵循下述四条铁律（1 stdin 断开、2 不用 `$p.Kill($true)`、3 Job Object 灭树、4 不求提权）。
- **macroGC 诊断 env 三旋钮（MW12 起）**：native 产物支持 `RIGI_RT_GC_THRESHOLD=<字节>`（债务阈值，默认 1MiB，调低可强制 mid-run 收集）、`RIGI_RT_GC_OFF=1`（纯 ARC 对照）、`RIGI_RT_GC_TRACE=1`（候选/收集全链路 stderr 追踪）；NativeE2E 的 `EnvCase` 构造器可按用例注入（与 MEMTRACK 合并）。收集器/ARC 问题排查时与 Watch-Command 组合使用。
- **子进程进程树管理（2026-08 rigic 孤儿事件经验，权威分析见用户侧调查报告）**：在本仓库的 shell（Windows PowerShell 5.1）里用 `Start-Process` 拉起 `dotnet run`/rigic 跑探测时，四条铁律——
  1. **stdin 必须断开**：rigic 默认继承一条通往宿主进程的 stdin 管道，会永远等输入（0 输出 0 CPU 全线程 Wait 的形态即此）。跑法必须带 `< NUL`（`cmd /c "dotnet run -- ... < NUL > out.txt 2> err.txt"`）或等价物。
  2. **看门狗不可用 `$p.Kill($true)`**：该重载是 .NET Core 3.0+ API，PS 5.1（.NET Framework CLR 4.0）没有——看门狗会抛 MethodException 自崩、零杀伤。PS 5.1 下用 `taskkill /PID $p.Id /T /F`（局限：父进程先死则 PPID 树断、杀不到孙进程）。
  3. **推荐 Job Object 根治**：`CreateJobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE(0x2000)` + `AssignProcessToJobObject`——后代出生即入 job，job 句柄随看门狗退出（无论正常/被杀）关闭时内核自动灭整树；PS 5.1 可经 Add-Type P/Invoke 使用（注意嵌套 struct 赋值须整体拷出改完再塞回）。C# 测试代码同理。
  4. **提权边界**：本机工具链若以管理员运行，残留孤儿只能提权清理；能不求管理员就不求。
- 测试数量与通过状态等易变数字不写入文档，以实际运行为准。

### 独立 TUnit 与隔离 case 协议

`Tests/CaseCatalog.cs` 是稳定 ID、suite/source/group/trait/LegacyRef 与单 input 驱动的唯一机器目录；`test --inventory` 的 `pilotCases` 输出同一映射。Lexer slash/comment、Parser 正负表达式、Semantic wrapper 负例、BIL scalar roundtrip/保留名负例、VM/native hello 逐 input 执行。旧 suite 未纳入该目录的部分继续如实报告既有 suite 或 provider 粒度。

TUnit 的 `MethodDataSource` 为每个目录项生成独立发现行，DisplayName 与稳定 ID 相同，Categories 包含 ID、suite、Pilot、group、trait。框架生成的 UID 含数据行序号，不作为持久 ID；精确选择使用稳定 ID category。发现清单可输出 JSON 并核对 displayName/traits：

```bash
dotnet build
dotnet test --project Tests/TUnit/RigiCompiler.Tests.csproj --no-build --list-tests
# 发布宿主同样支持发现 JSON；CoreCLR 可用 dotnet <测试DLL> --list-tests json。
# suite 类别选择：
dotnet test --project Tests/TUnit/RigiCompiler.Tests.csproj --no-build --treenode-filter '/*/*/*/*[Category=Lexer]' --minimum-expected-tests 2
# 精确稳定 ID + TRX：
dotnet test --project Tests/TUnit/RigiCompiler.Tests.csproj --no-build --treenode-filter '/*/*/*/*[Category=lexer.slash]' --minimum-expected-tests 1 --report-trx --results-directory TestResults --report-trx-filename impacted.trx
```

MTP 不能使用 VSTest 的 `--filter`。`--minimum-expected-tests` 是必需的零匹配防护；未知 ID 类别不会回退全套，零匹配返回非零。该固定 MTP 版本不支持 `--zero-tests-policy`。TRX 的测试数与 Harness 断言数分开：一条目录项是一条框架测试，内部可能执行多条断言。

`CaseOutcome` 状态为 Pass/Fail/Skip/Cancel，包含 assertions/failures/skipReason/diagnostics；JSON 用 `Utf8JsonWriter`/`JsonDocument` 编解码，带协议版本与 caseId，拒绝零断言 Pass、矛盾计数与无理由 Skip。每 case 重置 Harness/日志并捕获恢复日志状态；Lexer 保留其私有计数适配。异常、非零退出、缺失或矛盾 JSON 均失败。TUnit adapter 将 Fail 转成失败、Skip 转成正式 `Skip.Test(reason)`、Cancel 在 worker 灭树/排空之后调用当前测试 `Execution.Cancel()` 并抛框架 token 的取消异常；MTP/TRX 将取消计入失败，退出非零。

`CaseWorkerClient` 每请求创建唯一临时根，并让 child TMPDIR/TEMP/TMP 指向该根；结果文件与 stdout/stderr 分开。只执行 `test --worker --case-id <稳定ID> --result-file <路径> --spawned`，禁止旧 runner 二次展开；未知 ID 返回 2。worker 产物默认来自 `typeof(TestRunner).Assembly.Location`，可显式设置 `RIGI_TEST_RIGIC` 为真实 rigic EXE 或 DLL；DLL 使用匹配的 runtimeconfig/deps 经 `dotnet exec` 启动。AOT Location 为空时必须指定 override，不能把 TUnit 宿主当作 rigic。

框架 limiter 采用同一配置的 CPU 容量，真正启动时仍经过 `ResourceBudget.Shared` 的 CPU/内存/exclusive FIFO 授予；它只在当前父进程共享，不同 CLI/TUnit 宿主互不共享，CI 必须串行。`RIGI_JOBS` 是 1..实际有效 CPU 的十进制整数；`RIGI_TEST_MEMORY_MIB` 是 256..父宿主预留后的内存容量，非法/超额明确拒绝。资源探测复用性能工具的实际 cgroup membership/mount 解析并收紧可见祖先限制，还取 Linux affinity/cpuset 边界。

每个 child 分别注入 `DOTNET_PROCESSOR_COUNT`、`RIGI_JOBS`、`RIGI_COMPUTE_WORKERS`、`RIGI_LLD_THREADS` 与 `DOTNET_GCHeapHardLimit`，GC 只占其 lease 内存的一半，给 LLVM/C native heap 留余量；不更改父环境。native 内存权重保守预留，case outcome 的 execution 可记录实际进程组 RSS/CPU 采样，reservation 不是实际 RSS。CPU slots 不等于 OS 总线程数，真并发用例至少四 Compute，即使只有两物理 slots 也保留四线程共享；GC 与懒建 IO 另有线程。

legacy `test --run <编号...>`/All 共用动态跨 suite dispatcher；实际动作与 inventory 来自同一 provider，不反射。轻例小批、native 重例逐 case；Module 中声明 2048MiB 的实际重型 case 各用独立 worker，轻例在重型边界切批，单 worker 截止不因拆批而提高；未拆的单块如实报告 `suite-exit`，返回失败数转成退出契约断言。`--suite-args indices <全局序号...>` 选择稀疏 Semantics/Stress，严格校验预算内索引；范围、组、精确标签及旧 Native/E2e 名称选择整体先校验，未知项不回退全套。默认排除 slow gate，显式 label/name 可运行慢例。稳定 pilot ID 保持不变。

完整源码 E2e 与 BilVmStress 每输入独立 worker，避免多次编译或多轮 VM 回归在同一个截止内累加，原循环次数不变。普通 worker 的默认截止为九分钟；包含进程内 whole-program `default<O2>` 与对象发射的 legacy NativeE2E 使用四十五分钟的有限窗口。按真实冷编译整套成本及共享预算满载时的执行余量，Binder/BilEmitter/Lowerer 无参数完整整组分别使用六十、七十五、三十分钟。定向组、模块 case 与小 BIL pilot 仍用九分钟；调用方显式 `RunAsync(timeout: ...)` 优先。任务选择、稳定 ID 解码与直接 case 客户端共用同一默认截止策略；fuzz 保留既有默认不限时及显式覆盖。根等待、灭树与排空责任不随窗口改变。CLI 每个任务完成时输出一行进度，最终结果与计数仍按原选择顺序输出。

进程隔离使用性能工具同一受管 launcher：Linux `setsid` 建 session/group，负 pgid 灭树；Windows 原子 Job 或挂起归入 Job 后恢复，stdio 仅继承 child 端。根等待与管道排空共用截止；成功也清残留后代，root/drain 完成后只删除本次 TMP 请求根。Linux 无法容纳主动 setsid 逃组；Windows 不能由 Linux 环境冒称实测。case execution 的进程组 RSS/CPU 以 100ms 活体采样，是下界，最后可读 PID 累计 CPU 不是退出后最终计数；其他平台不可得值为 null。

native 链接配置 `RIGI_LLD_THREADS` 仅显式设置时严格接受 1..254，ELF/COFF 都使用 `-Wl,--threads=N`，未设置沿用工具默认；该参数进入 link record，不进入 O2 对象 key。测试 lease 会显式设置链接线程数，因此工具链、lld 和产物运行继承一致的子预算。

协议契约覆盖 Harness/私有 Lexer 失败、显式 Skip、缺失结果的非零退出、超时、开始前与运行中取消，以及 Linux 根早退但后代持有管道。外部失败/TRX 验收可对单 ID 显式设置 `RIGI_TEST_PROBE=fail-harness|fail-lexer|skip|delay|crash`；取消同时设置 `RIGI_TEST_CANCEL_AFTER_MS`。这些门控不改变默认语义覆盖，不进入默认 CI 失败配置。

闭合泛型测试使用 `[GenerateGenericTest(typeof(int))]` 静态生成，发布后必须真实发现并执行 Generic 类别：

```bash
dotnet publish Tests/TUnit/RigiCompiler.Tests.csproj -c Release -r linux-x64 -o publish-tests/linux-x64
./publish-tests/linux-x64/RigiCompiler.Tests --treenode-filter '/*/*/*/*[Category=Generic]' --minimum-expected-tests 1 --report-trx --results-directory TestResults --report-trx-filename generic-aot.trx
```

TUnit AOT 宿主需要运行 worker 时指定 `RIGI_TEST_RIGIC`，并保留真实编译器产物的 sidecars；Generic 自身不依赖 worker。编译器程序集沿用既有反射根描述符；框架/测试宿主 AOT 分析与生产 AOT 警告应分别核对。

`TestCorpusPaths` 优先 `AppContext.BaseDirectory`；e2e/native 语料、mq 压力源和 C fixture 所需 runtime headers 随输出与 publish 复制。开发时允许源码回退；发布验收设置 `RIGI_TEST_CORPUS_ONLY_OUTPUT=1` 禁止回退，并在独立发布布局/不同 CWD 执行，缺少资产必须失败，不能借 checkout 掩盖漏复制。

### e2e 语料通道（`E2e` 套件）

git 跟踪的端到端语料测试：`.rg` 源文件经进程内全管线（编译 → BIL → VM 执行）断言行为，适合 bug 回归入库与端到端验收。驱动在 `Tests/e2e/cs/E2eCorpusTests.cs`，语料在 `Tests/e2e/rigi/`。

- **语料组织**：`rigi/*.rg` 为单文件用例（文件名即用例名）；`rigi/<目录>/*.rg` 为多文件用例（同目录即同组，全部 `.rg` 按文件名序一起编译）。
- **旁注约定**（写在组内任一 `.rg` 的注释行，按文件名序合并）：
  - `// expect-output: <一行 stdout>` —— 可多条，按序拼成期望 stdout（无此旁注 = 期望空输出）；
  - `// expect-exit: <整数>` —— 断言 main 返回的 i32 退出码（可省略）；
  - `// expect-error: <诊断子串>` —— 可多条；出现即负例：断言编译失败且每条子串都能在 Error 级诊断中命中。
- **加新用例**：正面用例放一个带 `expect-output`/`expect-exit` 的 `.rg`（须含 `pub func main(): i32`）；负向用例放带 `expect-error` 的 `.rg`；文件头注释注明对应 bug 编号与期望。无需改驱动或注册表。
- **调试单条**：`dotnet run -- test --run <E2e 编号> --suite-args <用例名子串...>` 按名过滤。
- 注意：Rigi 泛型列表以 `\<` 开启（如 `Holder\<i32?>`），语料里照写；println 等走 `core.io.Console`。

验证改动（已验证可用）：

```bash
dotnet build
dotnet run -- test --all    # 迭代全量；提交前改走 §2.3 双平台 publish 产物
```

---

## 代码规范与开发约定

- **命名**：标准 C# 约定（类/方法 PascalCase，局部变量与私有字段 camelCase）。
- **缩进**：4 空格。
- **注释语言**：中文。关键逻辑必须注释；状态机的状态含义与转换必须说明。
- **文档语言**：中文。`docs/` 下的规范文档是权威来源——**先读 SYNTAX.md 再写代码，不要凭其他语言的经验猜语法**（项目已因此返工过）。
- **思考语言**：为节省 token，思考一律使用中文；向子代理（subagent）下达任务时必须明确要求它也用中文思考。
- **禁止使用 AskUserQuestion**（harness 为 Kimi Code 时）：该工具有显示 bug，用户看不到第一个问题之后的后续问题。需要用户决策时，把问题整理好在回复正文中一次问完，然后停下来等待回答。
- 新代码应模仿相邻文件的风格；项目无 linter/格式化工具配置。
- 命名空间：主代码 `RigiCompiler`，测试 `RigiCompiler.Tests`。
- **日志**：Lexer/Parser 等编译器内部的日志一律走 `Core/Logger`（Verbose/Warning/Error），禁止直接 `Console.WriteLine`；verbose 默认关闭（`--verbose` 子命令打开控制台输出），`--log-to PATH` 把全量日志以 JSONL 落盘。控制台日志输出走 **stderr**——诊断不污染 stdout 的数据流（如 `compile --parse-only` 的 AST JSONL）。测试的报告输出（`[PASS]`/`[FAIL]` 等）不受此限。

### 子代理（subagent）委派规范

**git stash 备份（快照对纪律）**：工作区常有大量未提交变更（子代理产出同样暂不提交），一旦被子代理误删/误改，未跟踪文件无法用 git restore 恢复（playground/stress/ 误删事故已有先例，幸为跟踪文件得以恢复）。快照统一为「stash 对」：

```bash
git stash push -u -m "backup: <说明>" && git stash apply
```

push 后立即 apply 把工作区原样恢复，stash 条目留存为恢复点，`-u` 含未跟踪新文件。**主代理**：委派实施型子代理**之前与完成之后**各做一次快照。**实施型子代理**：**每个小阶段验证通过后必须立即做一次快照对**（消息 `<任务>: <阶段说明>`），便于分阶段回滚；遇误删/误改等意外时允许 `git stash apply stash@{N}` 恢复**自己创建**的快照条目自救（按消息前缀识别；apply 后条目保留，不 pop 不 drop）；其余 git 变更操作仍严禁（commit / pop / drop / restore / clean / checkout / reset 等）。**提交纪律**：提交前必须检查工作区无临时文件残留（`git status` 全量过一遍——playground/ 已入 .gitignore，但 `$null` 类 shell 误产文件与探测残留不得入库）；commit 完成后整条清理 stash 备份链（回滚由 commit 承担，stash 不再保留）。实施型子代理（会修改代码的）**绝对禁止并行使用，只允许串行委派**（安全红线：共享工作区，并发编辑与并发构建会互相破坏）；只有只读调研型子代理才允许并行。

**实施型任务提示词风格**（缺第 1 块曾致子代理陷入权限幻觉、空转整个上下文零产出）：

1. 开头「操作须知」块逐条写明：① 你拥有完整的文件读写/编辑/搜索/shell 工具，可直接修改仓库内任何文件，不要怀疑权限，直接动手；② 按实际宿主填写环境事实（OS、shell、实际仓库根、SDK/工具链与启动脚本），不得把历史 Windows/内层路径约定套到 Linux/bash 环境；③ 临时探测文件写到 playground/ 下，用完即删，且只删自己创建的文件，严禁批量删除 playground/ 下任何既有内容；④ shell 偶发网络/证书错误属抖动，直接重试。
2. 任务分阶段，每阶段写完立即 `dotnet build` 验证（0 错误 0 警告），不得一口气写完全部代码再编译；上下文宝贵，避免长篇内心独白，直接执行。
3. 给出明确的受影响验证选择与基线断言数（legacy suite/group/exact-label 或 TUnit 稳定 ID/category），断言数只增不减。遵守用户指定的验证范围；双平台 NativeAOT 全量提交/CI 原门禁保留，不在仅受影响验证的工作中擅自扩大范围。
4. 报告要求简洁：改动文件清单、关键决策、验证输出、意外与处理。
5. 明确要求：用中文思考、注释中文、严禁 git 变更操作（仅两个例外：① 按备份纪律在每个小阶段验证通过后执行快照对 `git stash push -u -m "<任务>: <阶段>" && git stash apply`；② 遇误删/误改等意外时可 `git stash apply stash@{N}` 恢复自己创建的快照条目自救，apply 后条目保留）。

### 添加新 Parser 功能的标准流程

1. 阅读 `docs/SYNTAX.md` 相关章节，理解规范与示例
2. 设计状态机（画出状态转换）
3. 在 `AST/` 对应文件中添加 AST 节点
4. 在 `Parser/` 新建 ParserLayer（实现 `IParserLayer`，构造函数接收明确施工目标，见 architecture.md §4.7）
5. 在 `Tests/` 添加测试类，在 `TestRunner` 注册表注册
6. 在 `RootParserLayer`（或相应父层）接入委托入口
7. `dotnet build` + `dotnet run -- test --run N`（对应套件）验证

### 中端（P3/P4）新增语法结构的标准流程

三树遍历统一为 CRTP visitor 协议：

1. 阅读规范章节与 `SEMANTIC_ARCHITECTURE.md` 对应 pass 职责（P3/P4 边界是纪律）
2. P3：`Semantic/Bound/` 加 Bound 节点 → `Semantic/Binding/Visitors/` 对应簇文件加结构 visitor（继承 `BinderVisitor`/`ExpressionVisitor`/`BinderShellVisitor` 三基类之一；栈类上下文压弹只写 Enter/Exit）→ `Dispatchers.cs` 注册一行
3. P4a：`Lowering/Lowered/` 加 Lowered 节点 → `Lowering/Rewriters/` 簇加 rewriter → `LowerDispatchers.cs` 注册一行
4. P4b：`Lowering/Emitting/` 簇加 emitter → `EmitDispatchers.cs` 注册一行
5. 测试：BoundDescribe/LoweredDescribe 加节点支持 + Binder/Lowerer/BilEmitter 三套件加用例
6. 迁移铁律：先完整读旧实现再写（诊断消息文本/毒化静默位置逐字保真）；合成局部顺序敏感（`.sN` 先于 `.bN`——SynthLocals 顺序即 `.vars` 发射顺序）

### 进度对齐标准（必须遵守）

- `docs/legacy/` 下的 `PROGRESS_REPORT.md` 与两份 ROADMAP 均为历史档案，不再更新；进度现状以代码与 git 历史为准。
- 不要新建单点完成报告/实现总结类文档。

---

## 注意事项与已知限制

- 项目已在 **Git 版本控制**下（`main` 分支）：执行 `git commit` 等变更操作前先获得用户确认；提交前确保 `dotnet build` 通过，且已用 publish 配置在可用的 Windows 与 Linux（如 WSL Ubuntu）环境分别跑通 `test --all`（见 §2.3；禁止只靠 `dotnet run`）。
- Verbose 调试日志默认关闭，不再刷屏；需要时加 `--verbose` 子命令（控制台）或 `--log-to PATH`（全量 JSONL 落盘）。
- 无安全敏感面：本项目是本地控制台工具，不处理网络、凭据或用户隐私数据。唯一文件操作是 `Program.cs` 读取用户指定路径的 `.rg` 文件。
- **并发会计铁律（rigi_rt）**：rc 终态判定只认「减量后归零」（或 PURPLE detach 成功归还账本 +1 后归零），任何「减后值==1 即终态」的形态都是被实证过的 UAF 根因（atomicfix 修复，NativeE2E case 455）；`rigi_rt` 会计路径（arc.c/macrogc.c）改动必须过 NativeE2E GC_OFF 变体（「AtomicStruct 并发mutate自增无丢失（GC_OFF 纯ARC对照）」）+ 默认/GC_OFF/`RIGI_RT_GC_THRESHOLD=64` 三环境连跑全绿。
- **数组元素 ABI 配对铁律（Middleware Emit）**：数组元素槽布局由数组头 `elemSheet` **唯一决定**（String 槽恒为 `{data, len}` 走 string ARC；泛型胖引用槽只存 null/tag1 盒/tag2 对象）。静态具体类型上下文与泛型共享体上下文访问同一数组时，`get.array`/`set.array` 必须经 `elemSheet` 归一（`ArrayEmitter.LoadRuntimeElemProduce` / `StoreRuntimeElemFromFat` / `TryStoreViaTypeId` 的 String 归一分支 + `rigi_check_fat_ref` 形态守卫），**禁止假设 16 字节槽位都是 `{typeid, payload}` 胖引用形态**——违反即「一侧 string ARC 写、另一侧 ref ARC 读」，第一个 8 字节语义错位（曾致泛型枚举器 current 把 data 指针当 typeid 解引用，native 0xC0000005；回归语料 `Tests/e2e/rigi/array_string_abi.rg`）。泛型占位 cast 的 null 放行口径与 VM `VmTypeOps.IsReferenceLike` 对齐（占位放行、显式 `as` 拒绝、闭合 `.string` 仅返回值赋槽放行）。**守卫前提口径（jsonfix 实证）**：tag0 胖值 = **装箱标量的合法泛型形态**（tid 低 56 位 = 标量 TypeSheet，`FlagInlineValue` 必置位；payload = 位形）——引用擦除容器槽（elemSheet 为 `core::Any`/`Nullable` 族的 `.array<.any>` 载荷）由调用方静态路径写入装箱标量属合法形态，`rigi_check_fat_ref` 对 tag0 按 tid 自证放行（解引用查 `RIGI_TYPE_INLINE_VALUE`），仅不带该位（String 特化槽 `{data,len}` 被误当胖引用等）才 abort——**禁止**把「tag0 且 payload 非零」一律当错配（曾致 `arrayOfElements<Any?>(装箱标量)` 泛型构造环 abort，回归语料 `Tests/e2e/rigi/array_boxed_scalar.rg`）。**内联判定按 `FlagInlineValue` 而非 size 启发式**：泛型共享体（typeid 擦除）下「元素槽是否按值内联」必须读 elemSheet flags 的 `FlagInlineValue`（唯一口径 `ArrayEmitter.IsInlineElemSheet`，与 `DynamicNewEmitter.EmitThunkInvoke` 同判据；`core::ValueType` 内建 sheet 除外——静态分类是引用槽），**禁止用 `size≤8` 当内联判据**——>8B 的 struct（如 `TimeSpan` 72B，`@Serializable` 隐藏存储）槽布局是按值内联字节，曾因此被误判胖引用槽：`arrayOfElements<struct>` 把 `{sheet指针, box指针}` 当元素值落槽（native 元素损坏/VM 正常），泛型 `Array<T>` 形参读写读崩于 `rigi_check_fat_ref`（varargsfix 修复，回归语料 `Tests/e2e/rigi/array_inline_struct.rg`）。>8B 内联 struct 写槽按 refMapSize 二分：有嵌入引用走 rich copy（acquire 源盒内嵌引用/release 旧槽内容，`ArcEmitter.EmitCopyRichValueRuntimeSize`），无引用纯 memcpy；读侧重打包为 tag1 盒（`MallocDynamic` + memcpy + `EmitValueAcquireSheet`，新盒自有引用不过 Produce）。
- **协程帧编组 ABI 同构铁律（Middleware，typefix 实证）**：`Type<T>`/`.fieldid` 值**无论闭合还是开放占位**都是 8B 裸 sheet 指针 ABI（`ClassifySlot` 口径，与 `ClassifyElement` 一致——后者 typeid 判定必须先于泛型占位判定）。协程切分（CoroutineSplitPass）的 tainted 臂 frame 打包、DONE 返回臂、挂起点 Nullable wrap/unwrap 凡遇「值表示跨开放/闭合边界」必须按 **ABI 同构白名单**（typeid/fieldid/FatReference）恒等拷贝，`MirBoxAny` 只留给真正需要装箱的值类型；**禁止**把 typeid 装箱成 16B 胖值写进 8B 槽（opaque pointer 下 `BuildStore` 无点类型检查，16B 写穿 8B 槽、读取端静默截断第 0 字段 = 视图 sheet——typeNameOf(typeOf(x)) 经泛型形参转发输出 `core::Type<X>` 即此形态，回归语料 `Tests/e2e/rigi/typeid_frame_marshal.rg`）。防御锚点：`BoxEmitter.EmitBox` 拒非胖引用目标槽、`FieldEmitter.StoreAt` 拒聚合值入标量字段，编组尺寸/表示不符必须响亮失败（CompilerInternalException），不允许落 IR 或运行期才爆。
- **可空判等铁律（双宿主，nullablefix 实证）**：同型 `T?` 的 `==`/`!=` 语义 = **nullness 短路 + 解包内层判等**（SYNTAX §3.4 可空判等条）：双空 `true`、单空 `false`、双非空解包内层按 `T` 既有判等。VM 侧在 `BilComputeExecution.ExecuteBinary` CmpEq/CmpNe 前置解包 VmNullable（双非空才抵达派发）；native 侧**禁止**把可空判等交给 ImplBinder 的 `core::Nullable<` 胖值位比规则双非空臂（该规则只服务 `x == null` 形态——引用内层/堆盒 struct 双非空同值内容位比恒 `false`，native-String? 分歧根因），MIR 层经 `DataVisitors.NullableEqualityLowering` 展开（只将左右各自与 null 比较以判空；双非空 `MirUnwrapNullable` 两侧，再递归内层判等；`MirReachability.AddUserOperatorEdges` 的可空臂同构收编占位 T 动态 operator 候选，Any 默认 equals 由恒可达闭包收编）。**禁止**左右胖值位比命中就返回 true：同一对象的用户 `equals` 可返回 false；`!=` 仅合并后取反。泛型占位 T 解包目标须为 16B 胖槽，由 `NullableEmitter.UnwrapToSlot` 核验实际 sheet 并 `ProduceFatValue` 建立独立 ARC 所有权，`RcInjectionPass` 对产出旧值释放、退出时释放；`MirGenericBinaryOp` 只读该胖槽后 `BuildExtractValue` 按动态 typeid 派发。旧 NativeE2E 406 的崩溃是对非聚合擦除槽直接 `BuildExtractValue`，**不可**无条件删除此形态防御、不可把 8B 标量/TypeId 当 16B 胖值。泛型值类型宿主 operator 与 Entity wrapper 代理链仍是 `GenericOpEmitter` 各自既有边界，不因可空判等修复而宣称完全支持。**nullness-only 形态（`x ==/!= null`，一侧为 null 常量物化局部，`FlowBuilder.IsNullConstant` 登记）不走展开**——位比即语义（上述 ImplBinder Nullable 位比规则正只服务该形态）；展开只服务双变量同型判等，且该排除是 wrapper bake 特化体的安全前提（见下条铁律）。可空 unwrap 到 Any/Object 目标的 null（双零胖值）**放行**（zeroinitializer 入槽，对齐 VM `VmTypeOps.TryCast` 的 VmNull 口径），NullableEmitter Any 目标分支不得抛 CastException。回归语料 `Tests/e2e/rigi/nullable_equality.rg`。
- **递归栈预算联动（rigi_rt × NativeCommand，jsonfix 实证）**：native 自递归守卫 `rigi_stack_has_room`（shim.c）预算 **1 MiB**（线程首挂守卫点记栈基准，任一方向超预算即抛 `RuntimeException`），与链接器主线程栈保留联动（NativeCommand：win `-Wl,/STACK:8388608` = 8 MiB；linux 由宿主 ulimit 兜底）——**守卫预算必须恒小于真实栈保留**，调大预算前先确认 /STACK（序列化反射派发 `$fieldsOf` 等合成函数帧随注册类型线性增长，256 层递归环（json_read_nested 深度安全语料）实测 ≈2.3KiB/层 ≈586KiB，512KiB 旧预算已越线；新增注册类型会继续推高每层帧耗）。
- **返回值交付即移动契约（Middleware Emit × rigi_rt，richretrfix 实证）**：值类型返回的交付 = `RcInjectionPass` 让 `$mw.ret` 独立持有（release/copy/acquire 三段式；借用返回 C4 零义务纯 copy）→ `TerminatorEmitter` MirRet 值类型分支对隐藏 out 首参**纯 memcpy**（out 接管 `$mw.ret` 的 +1，交付后即 ret，`$mw.ret` 不再任何 release）。**禁止改回「acquire + memcpy + release」三段交付**：tag1 堆盒槽（`Nullable<T>` 装箱，`NullableEmitter.WrapFromSlot`）的 `rigi_ref_acquire` 有深拷回写副作用（`arc.c` `rigi_value_walk` 回写槽 payload——tag1 是唯一有回写副作用、且 release 即 free 的引用槽种类；STRING/tag2 的 acquire+release 相互抵消故旧实现曾侥幸正常）——memcpy 拷出「回写后的新块」而紧随的 release 又将该块 free，out 拿到悬垂块：返回含 Nullable 字段的 rich struct 必现 UAF（段错误/垃圾数据随堆布局浮动）。防御：`rigi_value_walk` release 走查对 tag1 槽清零（协议破约退化为确定性 null 槽 no-op）。回归语料 `Tests/e2e/rigi/rich_return_nullable/`（目录组，跨命名空间 + 同文件两形态；NativeE2E 末尾多文件对拍 Case 经 `RunCaseFiles` 复用）。
- **构建卫生（对拍/调研）**：VM↔native 对拍与缺陷复现一律用 `dotnet build -c Release` 的新产物（`bin/Release/net10.0/<rid>/rigic.exe`）并记录其哈希/构建时间；工作区存在大量未提交修改时，`publish/rigic.exe` 等历史产物可能与源码不同步——「探针全过/全绿」若以过期二进制得出即无效结论（nullablefix 调查实证：前任因过期构建把真实缺陷误判为 flaky）。
- **P4 写穿接管判定必须查 setter 使用点可见性（chainfix 实证）**：值类型 receiver 方法调用写穿（`WrapperPlaceLowering.TryValueReceiverCallTarget`）与写回构造（`CheckWritebackWritable`）共用同一谓词 `IsSetterVisibleFromContext`（按使用点文件/命名空间/宿主判 setter 可见性，§16.1）——访问器环「setter 存在但使用点不可见」（`priv set` 对外部使用点）不接管落普通路径（§10 只读 place 的 this 修改本就不生效），**禁止**把接管判定退回「setter 存在性」口径（曾致 `priv set` 属性链直调方法被 P4 误报 `'X' is inaccessible`，纯读表达式落 setter 诊断）；编译器自发的 receiver 写回任何环级失败静默放弃（`isReceiverWriteback` 通道），使用点可见性诊断只允许出现在用户显式赋值路径。回归语料 `Tests/e2e/rigi/accessor_chain_writeback.rg`（VM+native 对拍，含 pub set 突变外溢与只读不外溢双语义钉子）。
- **wrapper bake 特化 × 按占位展开指令的形态分裂铁律（Middleware，segvfix 实证）**：`ProxyBakeSupport.BuildSpecializedBody` 特化只替换局部槽类型（占位→具化，如 `TField`→`core::i32`）与返回类型，**不重写指令内嵌类型**——BIL→MIR 转换期（`MirBuilder`）按模板占位展开、发射期按指令类型语义加载槽的指令落进特化体即成「槽=具化标量、指令=占位」分裂。已知裂口：同型 Nullable 判等全展开对 `TField? != null`（`Temporary` proxy 的 `cached if? resumeStub()` 脱糖 nullness 检查）产出占位 `MirGenericBinaryOp`，`GenericOpEmitter.EmitBinary` 对 i32 标量槽 `BuildExtractValue`，libLLVM 原生层 SIGSEGV——**编译器进程崩**（win 0xC0000005 / linux 139，无任何诊断；曾致 NativeE2E 407/472/553 序列化 Temporary 系全崩）。两道防线：① Nullable 判等展开排除 nullness-only 形态（`FlowBuilder` null 常量局部登记，`x ==/!= null` 位比即语义）；② `GenericOpEmitter` 发射前恒验 G4 操作数槽为 16B 胖聚合 `{i64,i64}`，不符 `CompilerInternalException` 响亮失败（带函数/槽类型上下文，禁止放行落 IR）。新增「按占位生成、按指令类型消费」的 MIR lowering 必须自查与 bake 特化的组合。回归语料 `Tests/e2e/rigi/serialization_*`（Temporary resume）+ `Tests/e2e/rigi/nullable_equality.rg`（双变量展开路径钉子）。

---

## 项目原则

1. **文档驱动** —— 先理解 SYNTAX.md，再写代码
2. **测试驱动** —— 每个 ParserLayer 都有对应测试
3. **模块化** —— 每个 Layer 职责单一，委托而非大包大揽
4. **渐进式** —— 渐进推进，不跳步
5. **不要猜测** —— 不确定时查文档
6. **简洁优先** —— 写代码时始终自问：这个真的有必要存在吗？有没有更简洁更优雅的方法？可不可以复用已有的轮子（比如已有的 Layer）？不要自己造轮子（详见 architecture.md §4.5）
