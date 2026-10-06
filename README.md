# RigiCompiler

Rigi 的 C# 编译器，面向 .NET 10，包含 Lexer/Parser、语义分析与 Lowering、BIL 模型/验证器、BIL VM 行为参考解释器，以及通过 LLVM 生成原生代码的 Middleware。

Rigi 采用具化泛型、协程、`rich`/`shared` 类型声明修饰与 wrapper 系统。表达式**没有运算符优先级**，连续运算必须按语言规范使用括号。

```text
.rg → Lexer / Parser → AST → P1/P2 符号解析 → P3 BoundTree
    → P4a LoweredTree → P4b BIL → BilVerifier
    → BIL VM，或 Middleware → LLVM → 原生产品
```

## 安装与使用

安装 NativeAOT Release、加入 PATH、创建首个模块及配置 Native 工具链，见 [安装指南](INSTALLATION.md)。安装后直接使用 `rigic help` 和 `rigic module --run --root app`，无需 .NET SDK/Runtime。

## 从源码快速开始

在包含 `.git` 与 `RigiCompiler.csproj` 的仓库根执行。需要 .NET 10 SDK；版本选择见 `global.json`。

```powershell
dotnet build RigiCompiler.sln
dotnet run help
dotnet run -- test                 # 转发独立 TUnit 宿主，显示兼容套件菜单
dotnet run -- test --all            # 转发 TUnit CoreCLR 全量；提交门槛见开发指南
```

`dotnet run help` 与 `dotnet run -- help` 都有效；独立的 `--` 是 .NET CLI 参数分隔符。下方带编译器选项的例子使用它，防止 .NET 把程序参数当作自己的选项；发布版直接运行 `rigic help`。

全部测试由独立 `Tests/TUnit/RigiCompiler.Tests.csproj` 发现执行，编译器不包含测试代码。`rigic test` 只转发到测试宿主，保留编号、标签、inventory 和 `--all` 兼容入口；单独发布编译器时，如需测试须另发布同版本宿主并设置 `RIGI_TEST_HOST`。提交门槛在 Windows/Linux 分别发布编译器和测试宿主，各执行一次 TUnit NativeAOT 全量，包含闭合泛型与协议契约。

CI 在每个平台使用 16 个独立 runner 按 suite/稳定 ID 精确分片，provider 全目录只执行一遍，框架契约仅在片 0 执行；最终汇总 JSON/TRX 证明完整覆盖，缺片或失败不能通过。本机提交仍串行运行两个平台的单进程全量，外层看门狗为 24 小时；CI 既有 Stress 600 与本机默认 3000 种子预算保持不变，具体命令与证据协议见 [开发指南](DEVELOPMENT.md#23-发布release--nativeaot)。

模块工作流从 `module.yaml` 管理源码、依赖和 profile：

```powershell
dotnet run -- module --init --root app
dotnet run -- module --run --root app
```

初始化模板的 debug profile 在 VM 执行，release profile 生成 Native。低级 CLI 保留 `compile`、`vm`、`native`，所有选项可通过 `help <command>` 查询。完整命令、模块产品/ZIP/hook 契约与验证要求见 [DEVELOPMENT.md](DEVELOPMENT.md)。

Native 完整链接需要 clang/lld、libuv 与 mimalloc；Native 模块工作流还需要归档器构建标准库等静态依赖。`tools/Fetch-LlvmToolchain.ps1`、`tools/Fetch-Libuv.ps1`、`tools/Fetch-Mimalloc.ps1` 提供预取。进程内 LLVM 使用项目引用的 LLVMSharp.Interop/libLLVM。Release 发布使用 NativeAOT，各操作系统须在对应宿主构建；CI 在 Windows/Linux 分别验证发布产物。`--emit-obj` 与 `--emit-ll` 可用于无需完整链接的诊断输出。

## 文档与代码

- [开发指南](DEVELOPMENT.md)：构建、CLI、测试、性能工具与维护约定，供维护者及其 coding agents 使用。
- [文档索引](docs/README.md)：语言、BIL、运行时、标准库以及各编译阶段的专题入口。
- [架构指南](docs/agent_guide/architecture.md)：代码组织与横跨各层的设计约束。
- 源码主目录：`Lexer/`、`Parser/`、`AST/`、`Semantic/`、`Lowering/`、`Bil/`、`Middleware/`、`Modules/`、`Core/`。
- `stdlib/` 是内嵌标准库源码，`rigi_rt/` 是 Native C 运行时；`Tests/` 的全部 provider、断言与工具只编入独立 `Tests/TUnit/` 项目，`tools/` 提供开发工具。

## 许可

项目采用 [Apache-2.0](LICENSE)；第三方归属说明见 [NOTICE](NOTICE)。
