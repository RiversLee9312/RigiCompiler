# 文档索引

## 规范

- [SYNTAX.md](SYNTAX.md)：语言语法与合法性；正文在 `SYNTAX/`。
- [RUNTIME.md](RUNTIME.md)：运行时可观察行为；正文在 `RUNTIME/`。
- [BIL_STANDARD.md](BIL_STANDARD.md)：BIL 文本、编码和验证契约；正文在 `BIL_STANDARD/`。
- [STDLIB.md](STDLIB.md)：标准库设计契约与组织，设计说明不等于实现清单。

规范章节号沿用既有编号；语法以 SYNTAX 为准，运行时行为以 RUNTIME 为准，BIL 编码以 BIL_STANDARD 为准。实际实现范围应结合源码和测试核对。

## 维护与实现

- [安装与发行打包](../INSTALLATION.md)：NativeAOT Release、Windows/Linux 用户安装、PATH 与第一次运行。

- [DEVELOPMENT.md](../DEVELOPMENT.md)：通用构建、CLI、测试及代码约定。
- [架构指南](agent_guide/architecture.md)：跨层约束与代码结构。
- [Lexer、Parser 与 AST](compiler/syntax/README.md)。
- [语义分析与 Lowering](compiler/semantic/SEMANTIC_ARCHITECTURE.md)。
- [Middleware](compiler/middleware/MIDDLEWARE_ARCHITECTURE.md)。
- [BIL VM](compiler/vm/BIL_VM_DESIGN.md)。

`legacy/` 中的 PROGRESS_REPORT 与两份 ROADMAP 是历史档案，不作为当前实现依据；Git 历史保留历史决策与进度。
