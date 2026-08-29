# RigiCompiler 开发指南

> 构建、运行、测试与开发约定；架构与代码库结构见 [architecture.md](architecture.md)；本文件由 AGENTS.md §2/§5/§6/§7/§8 拆分而来。

## 构建与运行

### 2.1 构建

```bash
dotnet build        # 在项目根目录执行；当前 0 错误、0 警告
dotnet clean
```

唯一配置文件是 `RigiCompiler.csproj`。依赖纪律：原则上纯 BCL、无第三方依赖——**唯一豁免是 Middleware 的 LLVMSharp.Interop + libLLVM（锁定 LLVM 20）**（选型裁决见 `docs/compiler/middleware/MIDDLEWARE_ARCHITECTURE.md` §2）。注意：libLLVM 原生资产经 runtime.json 传递、只在带 RID 时解析，csproj 已显式引用 win-x64/linux-x64 两个 runtime 包以支持无 RID 的 `dotnet build`/`dotnet run` 开发回路。csproj 另开 `AllowUnsafeBlocks`，仅限 `Middleware/Emit/LlvmBitcode.cs` 的 libLLVM 指针编组封装使用。新增依赖前必须先讨论并同步本文档；**优先复用现有的、高质量且久经验证的轮子（仓库内设施优先，外部库须成熟可靠），不重复造轮子**。另有 `RigiCompiler.sln`。

Middleware 的 C 工具链（MW1 起编译 rigi_rt 与 lld 链接所需）：CI 用 runner 预装 clang/lld；开发机 PATH 优先，缺则跑 `pwsh tools/Fetch-LlvmToolchain.ps1`（钉版官方 20.1.2 选择性部件，缓存 `tools/.llvm/`，gitignored）。详见 `MIDDLEWARE_ARCHITECTURE.md` §2 链接器/rigi_rt 编译行。`native --out` 走全链：clang 驱动（`-fuse-ld=lld`）链接 CRT 出可执行文件；`--emit-obj`/`--emit-ll` 免工具链（中间产物与合并 rigi_rt 前的黄金快照）。rigi_rt 源改动经内容哈希缓存自动重编，无需手工清理。

`RIGI_RT_MEMTRACK=1` 打开 rigi_rt 内置堆台账：进程退出时未释放块即 stderr 报告并以 exit 1 失败。NativeE2E 对拍跑产物进程时默认开启（泄漏即该用例失败）；日常 `native --out` 不设此变量。

### 2.2 运行

CLI 结构为 `<COMMAND> [--sub-cmd [args...]...]`，顶层 COMMAND 五个：`compile` / `test` / `vm` / `native` / `help`。裸 `dotnet run` 等价于 `help`。

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
dotnet run -- vm --file a.bil a.core.bil ...         # 加载执行 BIL（§17.2：须传入全部切片还原完整模块）
dotnet run -- vm --file a.bil a.core.bil --entry-point <符号>   # 多 entrypoint 时显式选入口
dotnet run -- native --file a.bil --out app.exe        # BIL → 原生可执行（clang 驱动 lld 链接 CRT，需 C 工具链）
dotnet run -- native --file a.bil --emit-obj a.o       # 免工具链：进程内发射目标文件
dotnet run -- native --file a.bil --emit-ll a.ll       # 免工具链：合并 rigi_rt 前的 .ll 黄金快照
dotnet run -- help                       # 全部 COMMAND 与子命令概览（文本由注册表程序生成）
dotnet run -- help compile               # 单个 COMMAND 详情
dotnet run -- help compile.file          # 单个子命令详情（子命令名不带 -- 前缀）
```

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
- **本机 WSL 的 dotnet 路径（环境事实）**：WSL Ubuntu 的 PATH 上是 apt 装的 .NET 8（`dotnet-sdk-8.0`，**不满足**本项目 net10.0）；.NET 10 SDK 由 dotnet-install 脚本装在 **`~/.dotnet`（不在 PATH）**。WSL 侧构建/测试前先导出：

```bash
export DOTNET_ROOT=$HOME/.dotnet && export PATH=$HOME/.dotnet:$PATH
```

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

---

## 项目原则

1. **文档驱动** —— 先理解 SYNTAX.md，再写代码
2. **测试驱动** —— 每个 ParserLayer 都有对应测试
3. **模块化** —— 每个 Layer 职责单一，委托而非大包大揽
4. **渐进式** —— 渐进推进，不跳步
5. **不要猜测** —— 不确定时查文档
6. **简洁优先** —— 写代码时始终自问：这个真的有必要存在吗？有没有更简洁更优雅的方法？可不可以复用已有的轮子（比如已有的 Layer）？不要自己造轮子（详见 architecture.md §4.5）
