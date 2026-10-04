# Lexer、Parser 与语法树

前端由 Lexer/、Parser/、AST/ 与 Core/Frontend.cs 组成。语言定义以 [SYNTAX.md](../../SYNTAX.md) 为准，本文档组解释实际代码架构。

- [Lexer 驱动、Token 与字符串帧](lexer.md)：字符调度、词法层、字符串/字符/注释、插值、EOF。
- [Parser 驱动、施工协议与层边界](parser.md)：Consume/Replay、施工目标、层职责、上下文与独立测试。
- [AST 节点、归属与源码范围](ast.md)：完整节点清单、Parent、ExpressionRoot、Span、Visitor、Validator 与 JSONL。
- [表达式 Parser](expressions.md)：起点、路径后缀、实参委托、无优先级运算符和完整解析示例。

## 阶段与产物

SourceInput（文本、源名、编译器资源身份）→ Lexer.Tokenize → List<Token>（含正式 EOF）→ Parser.Parse → RootASTNode → ASTIntegrityValidator（Parser 成功路径自动执行）→ P1 声明收集 / P2 声明解析 / P3 Binder → Bound Tree → P4a 恒等重写 → Lowered Tree → P4b BIL 发射 → BIL。

语义分析保留语法 AST，并建立 Bound Tree；后续通过 Lowered Tree 发射 BIL。语法完整性验证不能代替语义分析。阶段细节见 [语义架构入口](../semantic/SEMANTIC_ARCHITECTURE.md)。

## 入口与文件调度

Core/Frontend.cs 的 SourceInput 在任务启动前固定文本、源名以及 CompilerLibrary、Intrinsics 身份；读取失败通过 ReadError 保存，解析时重新抛出。每文件创建独立 Lexer、Parser，Validator 在 Parser 内运行，没有跨文件共享的解析状态。

ParseFiles 先读文件形成输入；ParseMany 经 CompilerJobs.Map 按输入索引执行，单文件结果为 ParsedSource(Root, Error, Logs)，使用 Logger.CaptureJob 捕获日志。总文本 UTF-16 容量估算小于 64 KiB 时走 small 调度路径；取消标记传入任务调度。ParseRoots 按输入顺序回放日志，并通过 GetRoot 以 ExceptionDispatchInfo 重抛错误，保持异常来源。Parse 还记录 frontend.lexer、frontend.parser，多文件阶段记录 frontend.files，完整性阶段记录 frontend.ast-integrity 性能指标。

RootASTNode.IsCompilerLibrary 与 IsIntrinsicDeclarations 由前端从可信输入授予，不能按用户提供的文件名或声明推断。CLI 的 parse-only、AST dump 和构建/测试命令见 [开发指南](../../../DEVELOPMENT.md)。
