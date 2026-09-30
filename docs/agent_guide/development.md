# RigiCompiler 开发指南

> 构建、运行、测试与开发约定；架构与代码库结构见 [architecture.md](architecture.md)；本文件由 AGENTS.md §2/§5/§6/§7/§8 拆分而来。

## 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、0 警告
dotnet clean
```

唯一配置文件是 `RigiCompiler.csproj`。依赖纪律：原则上纯 BCL、无第三方依赖——豁免两类，新增前必须先讨论并同步本文档：**编译器托管侧是 Middleware 的 LLVMSharp.Interop + libLLVM（锁定 LLVM 20）**（选型裁决见 `docs/compiler/middleware/MIDDLEWARE_ARCHITECTURE.md` §2）；**native 产物运行时侧是 libuv（协程事件底座，MW11b 起）与 mimalloc（GC 分配器，GC Phase 2 起）两个静态链接 C 库**（均经 tools/ 下 Fetch 脚本钉版预取，见下文）。注意：libLLVM 原生资产经 runtime.json 传递、只在带 RID 时解析，csproj 已显式引用 win-x64/linux-x64 两个 runtime 包以支持无 RID 的 `dotnet build`/`dotnet run` 开发回路。csproj 另开 `AllowUnsafeBlocks`，仅限 `Middleware/Emit/LlvmBitcode.cs` 的 libLLVM 指针编组封装使用。新增依赖前必须先讨论并同步本文档；**优先复用现有的、高质量且久经验证的轮子（仓库内设施优先，外部库须成熟可靠），不重复造轮子**。另有 `RigiCompiler.sln`。

Middleware 的 C 工具链（MW1 起编译 rigi_rt 与 lld 链接所需）：CI 用 runner 预装 clang/lld；开发机 PATH 优先，缺则跑 `pwsh tools/Fetch-LlvmToolchain.ps1`（钉版官方 20.1.2 选择性部件，缓存 `tools/.llvm/`，gitignored）。详见 `MIDDLEWARE_ARCHITECTURE.md` §2 链接器/rigi_rt 编译行。`native --out` 走全链：clang 驱动（`-fuse-ld=lld`）链接 CRT 出可执行文件；`--emit-obj`/`--emit-ll` 免工具链（中间产物与合并 rigi_rt 前的黄金快照）。rigi_rt 源改动经内容哈希缓存自动重编，无需手工清理。

libuv 静态库（MW11b 起协程 Alarm 族事件底座）：CI 双平台 job 各跑一步 `tools/Fetch-Libuv.ps1`（runner 预装 cmake）；开发机跑 `pwsh tools/Fetch-Libuv.ps1`（钉版 GitHub tag v1.52.1 源码 + SHA256 校验，cmake 现场构建静态库，缓存 `tools/.libuv/<rid>/`，gitignored，幂等 / `-Force` 重建）。native 解析顺序 `--libuv-dir` → 环境变量 `RIGI_LIBUV` → `tools/.libuv/<rid>` → 编译器 exe 旁 `.libuv/<rid>`；命中时 `--out` 链接行追加静态库 + 平台系统库、rigi_rt 带 `-DRIGI_HAS_LIBUV=1` 编译；未命中为编译期明确拒绝（review-20260910 用户裁定：旧行为是运行期 abort）。详见 `MIDDLEWARE_ARCHITECTURE.md` §2 事件/定时底座行。

mimalloc 静态库（GC Phase 2 起 rigi_rt track 台账的底层分配面，替代 UCRT malloc/free 对）：CI 双平台 job 各跑一步 `tools/Fetch-Mimalloc.ps1`（**须在 publish 之前**）；开发机跑 `pwsh tools/Fetch-Mimalloc.ps1`（钉版官方预编译包 v2.5.1 + SHA256 校验，提取静态库与头文件，缓存 `tools/.mimalloc/<rid>/`，gitignored，幂等 / `-Force` 重建；官方 release assets 自 v3.x 起附带预编译产物，无需现场构建）。native 解析顺序 `--mimalloc-dir` → 环境变量 `RIGI_MIMALLOC` → `tools/.mimalloc/<rid>` → 编译器 exe 旁 `.mimalloc/<rid>`；`--out` 链接行恒追加静态库 + 平台系统库（win advapi32 / linux pthread+dl），未命中为编译期明确拒绝——`rigi_track_malloc/free` 是 rigi_rt 对象分配唯一收口，缺 mimalloc 符号必然链接失败。rigi_rt 侧仅 extern 声明 `mi_malloc_aligned`/`mi_free`，不经头文件；对象 16B 对齐与 memtrack 零泄漏口径不变。

`RIGI_RT_MEMTRACK=1` 打开 rigi_rt 内置堆台账：进程退出时未释放块即 stderr 报告并以 exit 1 失败。NativeE2E 对拍跑产物进程时默认开启（泄漏即该用例失败）；日常 `native --out` 不设此变量。

### 2.2 运行

CLI 结构为 `<COMMAND> [--sub-cmd [args...]...]`，顶层 COMMAND 六个：`compile` / `test` / `vm` / `native` / `run` / `help`。裸 `dotnet run` 等价于 `help`。

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
dotnet run -- run --file a.rg                          # 一键编译并运行（--target 缺省 vm：BIL VM 直接执行）
dotnet run -- run --file a.rg --target native          # 经 LLVM 管线产临时可执行后运行（产物 stdout/stderr 透传、
                                                     #   退出码透传、stdin 继承用户终端；临时 exe 无论成败用完即删）
dotnet run -- run --file a.rg --target vm --max-steps 1000000 --entry-point <符号>   # VM 步数上限与多入口选择（与 vm 命令同语义）
dotnet run -- vm --file a.bil a.core.bil ...         # 加载执行 BIL（§17.2：须传入全部切片还原完整模块）
dotnet run -- vm --file a.bil a.core.bil --entry-point <符号>   # 多 entrypoint 时显式选入口
dotnet run -- native --file a.bil --out app.exe        # BIL → 原生可执行（clang 驱动 lld 链接 CRT，需 C 工具链）
dotnet run -- native --file a.bil --emit-obj a.o       # 免工具链：进程内发射目标文件
dotnet run -- native --file a.bil --emit-ll a.ll       # 免工具链：合并 rigi_rt 前的 .ll 黄金快照
dotnet run -- help                       # 全部 COMMAND 与子命令概览（文本由注册表程序生成）
dotnet run -- help compile               # 单个 COMMAND 详情
dotnet run -- help compile.file          # 单个子命令详情（子命令名不带 -- 前缀）
```

`run` 语义要点：完整走与 `compile` 相同的管线（P1–P4 + BilVerifier），**BIL 切片不落用户目录**（vm 目标全内存；native 目标仅临时目录承载临时 exe，无论成败用完即删）；`--target vm` 为默认（大小写不敏感），非法值退出码 2；一键运行语义下 **main 的 i32 返回值即 rigic 退出码**（两目标一致；VM 异常仍为 1；`vm` 命令本身恒 0，是 `run` 与它的唯一差异）；`--target native` 要求恰一个 entrypoint，`--entry-point`/`--max-steps` 暂仅 vm 目标支持（混用明确报错退出码 2，不静默忽略）；编译失败与 `compile` 同形态（诊断走 stderr + 非零退出码），不产生执行。

诊断子命令（`compile` 与 `test` 共有，可组合）：

```bash
dotnet run -- test --all --verbose      # 控制台输出 verbose 级日志（默认只显示 Warning+）
dotnet run -- test --all --log-to run.jsonl   # 全量日志（含 verbose）以 JSONL 落盘
```

### 2.3 发布（Release = NativeAOT）

Release 配置发布为 **NativeAOT 原生单文件**（约 16 MB，免 dotnet 运行时）：

```bash
dotnet publish -c Release -r linux-x64 -o publish/linux-x64   # 产物：publish/linux-x64/rigic
dotnet publish -c Release -r win-x64 -o publish/win-x64
```

- **不支持跨 OS 交叉编译**：linux-x64 产物必须在 Linux（如 WSL）上构建；Linux 侧需 `dotnet-sdk-10.0` + `clang` + `zlib1g-dev`。
- **反射靠两份配置保住**：`ILLink.Roots.xml`（`preserve="all"`，保整程序集类型/成员元数据，供 `Assembly.GetTypes()`、`Activator.CreateInstance`、字段/属性反射使用）+ `JsonSerializerIsReflectionEnabledByDefault=true`（强开 STJ 反射序列化，AOT 下默认禁用）。AstJsonl 序列化/反序列化（`--dump-ast`/`--parse-only`）与 ASTIntegrityValidator 依赖它们，删掉会导致 AOT 产物运行时崩溃或静默丢数据。
- **性能注意**：AOT 无 JIT 的运行时优化（去虚拟化/PGO），重接口分派路径比 CoreCLR 慢约 3 倍——fuzz 类套件在 AOT 产物上明显更慢，日常全量测试建议仍用普通构建跑。
- **CI**：`.github/workflows/ci.yml` 按上述流程在 `windows-latest`（win-x64）与 `ubuntu-latest`（linux-x64，均为 amd64）双平台分别发布 AOT 产物并用产物跑全量测试（AOT 不支持跨 OS 交叉编译，只能按平台分别构建）。
- **提交前本地必须同口径**：在**本机已有的 Windows 与 Linux 环境**（本仓库开发机一般为 Windows 宿主 + WSL Ubuntu）各 `publish -c Release` 一次，并用产物跑 `test --all`。禁止只跑 `dotnet run -- test --all` 就提交——那是 CoreCLR 开发回路，不会覆盖 AOT 反射根、RID 原生库与无 JIT 路径。Linux 必须在 Linux 里 publish（不能在 Windows 上交叉编 linux-x64 AOT）。
- **本机 WSL 的 dotnet 路径（环境事实，2026-09-30 实测更新）**：WSL Ubuntu 的 PATH 上现为 **/usr/bin/dotnet = .NET SDK 10.0.112**（满足 net10.0，可直接用）；早期「PATH 是 apt 的 .NET 8、.NET 10 在 `~/.dotnet`」的布局已过时（`~/.dotnet` 仅剩旧 sentinel 残留）。若 `dotnet --version` 不是 10.x 再回退到导出 `DOTNET_ROOT=$HOME/.dotnet` 的旧手法。clang 18 在 /usr/bin/clang，zlib1g-dev 经 apt 装齐。
- **Linux 侧跑全量的工作目录必须在原生 Linux 文件系统上（如 WSL home 的 ext4），不要在 `/mnt/c`（9p/drvfs）里跑**：e2e 探针目录是相对 CWD 创建的；9p/drvfs 的 `renameat2(NOREPLACE)` 返回 ENOSYS，而 rigi_rt 对「不能提供原子不覆盖保证的宿主/文件系统」**刻意报 Unsupported、绝不回退 rename**（rigi_rt/fs.c 注释），于是 fs_copymove/fs_primitives/accept_dir_management 等用例在 /mnt/c 下必败、在 ext4 下全绿——这是环境限制不是产品缺陷。同理 lstat 对「文件/子路径」在 ext4 报 ENOTDIR（契约 §4.5 保留的宿主差异），9p 报 ENOENT 会让平台分支断言真空通过，失去覆盖意义。做法：`cd ~ && /mnt/c/.../publish/linux-x64/rigic test --all`（语料经编译器内绝对路径定位，与 CWD 无关）。

```bash
# Windows
dotnet publish -c Release -r win-x64 -o publish/win-x64
./publish/win-x64/rigic.exe test --all

# WSL Ubuntu / 其它可用 Linux
dotnet publish -c Release -r linux-x64 -o publish/linux-x64
./publish/linux-x64/rigic test --all
```

---

## 测试策略

- **不使用任何测试框架**。测试是 `Tests/` 下的静态类，每个类提供 `public static int RunAll()`（返回失败用例数），由 `Tests/TestRunner.cs` 统一驱动（`test` 命令入口）。
- **全量入口（迭代）**：`dotnet run -- test --all` 自动运行全部套件，任意失败返回非零退出码并列出失败套件名。**提交前入口**是 §2.3 的双平台 NativeAOT publish 产物 `test --all`（与 CI 同口径），不是 `dotnet run`。NativeE2E 跑产物进程时设置 `RIGI_RT_MEMTRACK=1`，泄漏即 exit 1。
- **统一基建**：`Tests/AstDescribe.cs` 是唯一的 AST 描述器（Expr/Stmt/Block/Decl/Root/Type/Symbol 等），`Tests/TestHarness.cs` 是唯一的驱动与断言（ParseRoot/ParseBlock/ParseWithLayer/ParseFirstDecl + Check/CheckTrue/CheckParseError/Summary）。禁止在套件里再写私有 Describe*/Format* 副本与计数样板。
- **断言对象约定**：除查的就是命令行/日志/token 流/层协议行为的套件（Logger、CommandLineParser、LexerFuzz、TokenDisposition）外，一律断言 AST 树产物（AstDescribe 描述串 + 结构断言），不断言控制台输出文本。
- **AST 结构断言**：表达式类测试除描述串快照外，还应断言结构性事实（Root 是否存在/已填充、Expression 的具体类型、Parent 链、子 Root 填充、无节点共享）——快照不能作为唯一验证方式。
- **独立 Layer 测试**：经 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)` 驱动（`TestHarness.ParseWithLayer` 封装）。`TestRootParserLayer` 只接受 EOF——被测 Layer 提前结束或漏消费普通 token 会立即失败，能发现 Layer 边界问题。
- **约定：每新增一个 ParserLayer，必须在 `Tests/` 添加对应测试类，并在 `TestRunner` 注册表注册（`test` 菜单与 `test --run N` 的编号即注册表顺序）。**
- **fuzz 并行子进程超时**：`SemanticsFuzz` 区间 >100 例时切多子进程并行，父进程默认**不限时**等待（NativeAOT 产物比 CoreCLR 慢约 3 倍，固定预算会误杀）；需要时限时用 `--suite-args <from> <to> child-timeout-ms=<毫秒>` 显式给出（套件参数不能带 `--` 前缀，会被解析成 test 子命令）。
- **NativeE2E 定位与按名运行**：`test --run 58 --suite-args` 三选一——①`list`：只打印当前真实索引+Label 清单（不启动编译，无 clang 也可用；**定位/验收一律以此为准，禁止凭记忆猜索引**）；②首参为整数：保持原 `from to` 区间语义（含不完整区间的用法报错）；③其余非空参数：按 Label 子串过滤（OrdinalIgnoreCase Contains，进程内单例执行、[PASS]/[FAIL] 行自带标签），零匹配退出 2。示例：`--suite-args list`、`--suite-args 545 545`、`--suite-args Parcel 动态访问`。
- **挂死调试纪律（必须设超时）**：调试可能引入死锁/活锁/进程不退出的改动（调度器、线程、等待-唤醒协议、quiescence 类计数）时，**任何测试运行都必须带超时**，禁止裸跑无限等待：① 优先用单例进程内复现通道（`--suite-args <i> <i>`）逐条验证，先单例绿再跑并行；② 必须跑子进程/产物进程时显式给超时（套件的 `child-timeout-ms`、或 shell 层看门狗 `tools/Watch-Command.ps1`，见下条）；③ 运行被中止后先检查并结束残留的 rigic/dotnet 测试子进程（文件锁会干扰重跑），再继续——`pwsh tools/Watch-Command.ps1 -CleanupOrphans` 一键清扫。
- **shell 层看门狗（tools/Watch-Command.ps1，MW12 起常驻公共工具）**：任何可能挂死/留孤儿的命令（dotnet test、native 产物、手工探测）都必须经它带超时拉起：`pwsh tools/Watch-Command.ps1 -Command dotnet -ArgumentList "run -- test --run 58" -TimeoutSeconds 900`。行为契约：子进程 stdout/stderr 原样穿透；正常结束时退出码 = 子进程退出码、job 关闭顺带清扫残留孙进程；超时灭整树并退出 **124**；参数校验失败退出 2；启动失败退出 127。`-ArgumentList` 是**原始参数字符串**（经 powershell.exe 命令行传参时逗号不会拆分数组，含空格的参数自行加双引号）。另注意 PowerShell 关键字参数模式下 `exit $p.ExitCode` 会被拆成 `$p` + 字面量 `.ExitCode`（透传退出码须先落局部变量），cmd 行内 `%ERRORLEVEL%` 在整行解析期展开（测上一条命令退出码须单独一行）。实现遵循下述四条铁律（1 stdin 断开、2 不用 `$p.Kill($true)`、3 Job Object 灭树、4 不求提权）。
- **macroGC 诊断 env 三旋钮（MW12 起）**：native 产物支持 `RIGI_RT_GC_THRESHOLD=<字节>`（债务阈值，默认 1MiB，调低可强制 mid-run 收集）、`RIGI_RT_GC_OFF=1`（纯 ARC 对照）、`RIGI_RT_GC_TRACE=1`（候选/收集全链路 stderr 追踪）；NativeE2E 的 `EnvCase` 构造器可按用例注入（与 MEMTRACK 合并）。收集器/ARC 问题排查时与 Watch-Command 组合使用。
- **子进程进程树管理（2026-08 rigic 孤儿事件经验，权威分析见用户侧调查报告）**：在本仓库的 shell（Windows PowerShell 5.1）里用 `Start-Process` 拉起 `dotnet run`/rigic 跑探测时，四条铁律——
  1. **stdin 必须断开**：rigic 默认继承一条通往宿主进程的 stdin 管道，会永远等输入（0 输出 0 CPU 全线程 Wait 的形态即此）。跑法必须带 `< NUL`（`cmd /c "dotnet run -- ... < NUL > out.txt 2> err.txt"`）或等价物。
  2. **看门狗不可用 `$p.Kill($true)`**：该重载是 .NET Core 3.0+ API，PS 5.1（.NET Framework CLR 4.0）没有——看门狗会抛 MethodException 自崩、零杀伤。PS 5.1 下用 `taskkill /PID $p.Id /T /F`（局限：父进程先死则 PPID 树断、杀不到孙进程）。
  3. **推荐 Job Object 根治**：`CreateJobObject` + `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE(0x2000)` + `AssignProcessToJobObject`——后代出生即入 job，job 句柄随看门狗退出（无论正常/被杀）关闭时内核自动灭整树；PS 5.1 可经 Add-Type P/Invoke 使用（注意嵌套 struct 赋值须整体拷出改完再塞回）。C# 测试代码同理。
  4. **提权边界**：本机工具链若以管理员运行，残留孤儿只能提权清理；能不求管理员就不求。
- 测试数量与通过状态等易变数字不写入文档，以实际运行为准。

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

1. 开头「操作须知」块逐条写明：① 你拥有完整的文件读写/编辑/搜索/shell 工具，可直接修改仓库内任何文件，不要怀疑权限，直接动手；② 环境事实——Windows，无 cat/heredoc/tail/grep/wc，创建文件用写文件工具、搜索用搜索工具、管道收尾用 `| powershell -Command "$input | Select-Object -Last 5"`；③ 临时探测文件写到 playground/ 下，用完即删，且只删自己创建的文件，严禁批量删除 playground/ 下任何既有内容；④ shell 偶发网络/证书错误属抖动，直接重试。
2. 任务分阶段，每阶段写完立即 `dotnet build` 验证（0 错误 0 警告），不得一口气写完全部代码再编译；上下文宝贵，避免长篇内心独白，直接执行。
3. 给出明确的验证命令与基线断言数（`dotnet run -- test --run N` + `test --all`），断言数只增不减。
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
