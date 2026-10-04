# 前端数据类型导航

本文正文已按 Lexer、Parser 与 AST 职责完整迁移。

- [Lexer 与 Token](lexer.md)：九种 Token、关键字/记号边界、字符标量与插值帧。
- [AST 节点与 Span](ast.md)：位置、全部节点分类、归属、遍历/完整性与 JSONL。
- [Parser 协议与异常](parser.md)：施工规则、层分工和词法/语法/内部错误边界。
- [前端入口与完整阶段](README.md)：多文件调度及 AST→Bound→Lowered→BIL。
- [表达式 Parser](expressions.md)：组合与完整示例；通用开发约定见 [DEVELOPMENT.md](../../../DEVELOPMENT.md)。
