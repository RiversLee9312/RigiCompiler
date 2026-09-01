# RigiCompiler 项目指南（AGENTS.md）

> **用途**: 为 AI 编码代理提供本项目的**核心工作纪律与文档导航**。读者默认对本项目一无所知。
> 本文件只放核心要求与指路；架构与开发细节见 `docs/agent_guide/`，语言/BIL/运行时规范见 `docs/` 索引。

**项目名**: RigiCompiler
**语言**: C#（.NET 10.0，控制台程序，`Nullable` 与 `ImplicitUsings` 已启用；原则上纯 BCL 无第三方依赖，唯一豁免是 Middleware 的 LLVMSharp + libLLVM，见 `docs/agent_guide/development.md` 依赖纪律）
**版本控制**: Git（`main` 分支；CI 见 `.github/workflows/ci.yml`）

⚠️ **仓库根在内层 `RigiCompiler/RigiCompiler/`（`.git` 在此）**，外层目录只放 `RigiCompiler.sln`，不是仓库；`dotnet build`/`dotnet run`/git 等工作目录同样是内层。

## 1. 项目一句话

Rigi 是一门现代的、类型安全的编程语言（完全具化泛型、原生协程、`rich`/`shared` 类型声明修饰、Wrapper 系统、**无运算符优先级**），本仓库是它的编译器，含 BIL 对象模型、验证器与 BIL VM（行为参考解释器）。流水线：

```
Rigi 源码 (.rg) → Frontend (Lexer + Parser) → 语义分析（P1–P3）→ Lowering（P4）
              → BIL → Middleware → LLVM → 原生可执行
```

## 2. 构建与验证（动手前后必跑）

日常开发回路：

```bash
dotnet build                 # 须 0 错误 0 警告
dotnet run -- test --run N   # 按编号跑单套件（编号见裸 dotnet run -- test 菜单）
dotnet run -- test --all     # CoreCLR 全量（迭代用，**不能**代替提交前验证）
```

**提交前验证（必须，禁止只跑 `dotnet run`）**：用 **Release + NativeAOT publish** 产物在**本机已有的 Windows 与 Linux 环境各跑一遍全量测试**。本仓库开发机典型组合是 Windows 宿主 + WSL Ubuntu。AOT 与 CoreCLR 的行为差（反射根、RID 原生库、无 JIT）只在发布产物上暴露；CI 也是双平台 AOT，本地提交前必须同口径。

```bash
# Windows（仓库内层目录 RigiCompiler/RigiCompiler）
dotnet publish -c Release -r win-x64 -o publish/win-x64
./publish/win-x64/rigic.exe test --all

# Linux（WSL Ubuntu 等；AOT 不能跨 OS 交叉编译，必须在 Linux 里 publish）
dotnet publish -c Release -r linux-x64 -o publish/linux-x64
./publish/linux-x64/rigic test --all
```

Linux 侧前置：`dotnet-sdk-10.0` + `clang` + `zlib1g-dev`。细节见 `docs/agent_guide/development.md` §2.3。


## 3. 核心纪律（必须遵守）

- **文档驱动**：先读 `docs/SYNTAX.md` 相关章节再写代码，不凭其他语言的经验猜语法（项目已因此返工过）。
- **Rigi 没有运算符优先级**：连续运算符必须括号化；实现表达式功能时不要引入优先级概念。
- **git**：`git commit` 等变更操作先获得用户确认；提交前确保 `dotnet build` 通过，且**已用 publish 配置在可用的 Windows 与 Linux（如本机 WSL Ubuntu）环境分别跑通 `test --all`**（见 §2；禁止只靠 `dotnet run` 当作提交验证）。**备份纪律（快照对）**：工作区常有大量未提交变更（误删/误改无法用 git restore 恢复），快照统一为「stash 对」——`git stash push -u -m "<说明>" && git stash apply`（push 后立即 apply 原样恢复工作区，stash 条目留存为恢复点）。主代理：委派实施型子代理**之前与完成之后**各做一次快照。实施型子代理：**每个小阶段验证通过后必须立即做一次快照对**（消息 `<任务>: <阶段说明>`），便于分阶段回滚；遇误删/误改等意外时允许 `git stash apply stash@{N}` 恢复**自己创建**的快照条目自救（按消息前缀识别；apply 后条目保留，不 pop 不 drop）；其余 git 变更操作仍严禁（commit / pop / drop / restore / clean / checkout / reset 等）。**提交纪律**：提交前必须检查工作区无临时文件残留（`git status` 全量过一遍——playground/ 已入 .gitignore，但 `$null` 类 shell 误产文件与探测残留不得入库）；commit 完成后整条清理 stash 备份链（回滚由 commit 承担，stash 不再保留）。
- **子代理委派**：实施型任务的提示词必须含「操作须知」块（权限确认/环境事实/临时文件纪律/分阶段验证），规范全文见 `docs/agent_guide/development.md`「子代理委派规范」——缺此块曾致子代理权限幻觉空转零产出。**串行铁律（安全红线）**：会修改代码的实施型子代理**绝对禁止并行使用，只允许串行使用**（共享工作区，并发编辑与并发构建会互相破坏）；只有只读调研型子代理才允许并行。
- **语言**：注释与文档一律中文，关键逻辑必须注释；思考也用中文；向子代理下达任务时必须明确要求它也用中文思考。
- **日志**：编译器内部日志一律走 `Core/Logger`，禁止直接 `Console.WriteLine`（测试报告输出除外）；控制台日志走 stderr，不污染 stdout 数据流。
- **简洁优先**：新增代码前自问三问——真的有必要存在吗？有没有更简洁优雅的方法？可不可以复用已有的轮子？新代码模仿相邻文件风格；项目无 linter/格式化工具配置。
- **复用优先**：尽量复用现有的、高质量且久经验证的轮子——仓库内设施（如 `Bil/` 生态、`TestHarness`/`BilTestHarness`）优先，确需外部能力时选成熟可靠的外部库（如 Middleware 的 LLVMSharp），不重复造轮子；新增第三方依赖属纪律变更，先讨论并同步文档。
- **测试**：不使用任何测试框架；新 ParserLayer 必须在 `Tests/` 添加测试类并在 `TestRunner` 注册；三树新节点必须同步 BoundDescribe/LoweredDescribe 与三套件用例。
- **禁止 AskUserQuestion**（harness 为 Kimi Code 时）：该工具有显示 bug，用户看不到第一个问题之后的后续问题。需要用户决策时把问题整理好在回复正文中一次问完，然后停下来等待回答。
- **文档维护**：进度现状以代码与 git 历史为准，不在文档里记录里程碑/进度/易变测试数字；历史档案在 `docs/legacy/`（不再更新）；不新建单点完成报告/实现总结类文档。

## 4. 文档导航

### 语言与规范（权威，§ 章节号永不重排）

| 文档 | 内容 |
|---|---|
| `docs/SYNTAX.md` | **语言语法规范索引**（正文在 `docs/SYNTAX/`）⭐⭐⭐ 有歧义时以此为准 |
| `docs/RUNTIME.md` | 运行时模型索引（正文在 `docs/RUNTIME/`）⭐⭐⭐ |
| `docs/BIL_STANDARD.md` | BIL 中间语言规范索引（正文在 `docs/BIL_STANDARD/`）⭐⭐ |

### 项目架构与开发

| 文档 | 内容 |
|---|---|
| `docs/agent_guide/architecture.md` | 代码库结构 + 核心设计决策（**改动代码前必读**）⭐⭐⭐ |
| `docs/agent_guide/development.md` | 构建/CLI/测试策略/开发约定/新功能标准流程 ⭐⭐ |
| `docs/compiler/semantic/SEMANTIC_ARCHITECTURE.md` | 中端（语义分析 + Lowering）架构 ⭐⭐⭐ |
| `docs/compiler/middleware/MIDDLEWARE_ARCHITECTURE.md` | Middleware（BIL → 原生）架构 ⭐⭐⭐ |
| `docs/compiler/vm/BIL_VM_DESIGN.md` | BIL VM 设计（值模型/执行模型/hook 表）⭐⭐ |
| `docs/compiler/syntax/EXPRESSION_ARCHITECTURE.md` | 表达式层架构专项 ⭐⭐ |
| `docs/compiler/syntax/FRONTEND_TYPES.md` | 前端数据类型（Token/AST/Span）⭐⭐ |
| `docs/compiler/semantic/ASYNC_LOWERING_DESIGN.md` | async lowering 专项 ⭐⭐ |
| `docs/legacy/` | 历史档案（PROGRESS_REPORT 编年史、两份 ROADMAP；不再更新）|

## 5. 仓库结构速览

```
Lexer/ Parser/ AST/          # 前端（层栈 + 状态机 + 施工目标协议）
Semantic/                    # 中端 P1–P3：诊断/符号图/声明收集与解析/Binder（visitor 化）
Lowering/                    # 中端 P4：P4a 恒等重写 + P4b BIL 发射
Bil/                         # BIL 生态（对中端零依赖）：模型/Reader/Writer/Verifier/Merger/VM（Vm/）
Middleware/                  # BIL → 原生（Gate/Symbols/Binding/Mir/Layout/Emit/Toolchain/Runtime/Cli；架构见 docs/compiler/middleware/）
rigi_rt/                     # 运行时 shim 库（C，EmbeddedResource 内嵌，clang 现场编 bitcode 合并进模块；MW1 最小面）
tools/                       # 开发工具链脚本（Fetch-LlvmToolchain.ps1 + Fetch-Libuv.ps1；缓存 tools/.llvm/、tools/.libuv/ 不入库）
Core/                        # CLI 内核与插件（compile/test/vm/native/help）+ Logger
Tests/                       # 自研控制台测试（TestRunner 注册表驱动）
stdlib/                      # 编译器自携标准库源（EmbeddedResource 内嵌，同走 P1–P4）
docs/                        # 规范与设计文档（全部为权威参考）
```

详细目录树与各文件职责见 `docs/agent_guide/architecture.md` §3。
