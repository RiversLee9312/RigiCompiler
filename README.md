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
dotnet build
dotnet run help
dotnet run -- test                 # 发现当前测试套件
dotnet run -- test --all            # CoreCLR 开发回路
```

`dotnet run help` 与 `dotnet run -- help` 都有效；独立的 `--` 是 .NET CLI 参数分隔符。下方带编译器选项的例子使用它，防止 .NET 把程序参数当作自己的选项；发布版直接运行 `rigic help`。

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
- `stdlib/` 是内嵌标准库源码，`rigi_rt/` 是 Native C 运行时；`Tests/` 和独立 `Tests/TUnit/` 共同承载测试，`tools/` 提供开发工具。

## 许可

项目采用 [Apache-2.0](LICENSE)；第三方归属说明见 [NOTICE](NOTICE)。
