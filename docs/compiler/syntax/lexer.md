# Lexer 驱动、Token 与字符串帧

Lexer 输出是 Parser 输入契约，位置与 AST 范围见 [ast.md](ast.md)，语法识别与消费协议见 [parser.md](parser.md)。

## Token 类型（词法分析器输出）

位于 `Lexer/Tokens.cs`（符号常量 `Notations` 在 `Lexer/Notations.cs`）。
**Token**: 抽象基类，含 `Content`、`Type`、`CharRange`。

| Token 类型 | TokenType | 说明 |
|-----------|-----------|------|
| **WordToken** | Word | 单词：标识符、关键字、数字字面量片段 |
| **StringToken** | String | 普通字符串的解码文本，或插值字符串的一个解码文本段；不包含插值表达式原文 |
| **CharToken** | Char | 字符字面量（`'a'`）：`Value` 为转义展开后的 `uint` Unicode 标量，无插值概念 |
| **NotationToken** | Notation | 符号：单字符（`(`、`.`、`<` 等）或多字符（`==`、`->`、`<=` 等） |
| **CommentToken** | Comment | 注释（Parser 主循环统一跳过，不参与语法） |
| **LineBreakToken** | LineBreak | 换行（Rigi 的语句终止符） |
| **InterpolationStartToken** | InterpolationStart | `${` 开始表达式词法帧；`Content` 为 `${`，当前 span 仅覆盖 `{` |
| **InterpolationEndToken** | InterpolationEnd | 插值帧配平的 `}`；替代对应的普通记号 Token |
| **EndOfFileToken** | EndOfFile | 文件结束（为正式 token）：Lexer 在输出末尾追加，只由 RootParserLayer 消费 |

注意：

- 关键字**不是**独立 Token 类型——以 WordToken 形式出现，由 Parser 比对 `Keywords` 常量识别（`Parser/Keywords.cs`）。
- `>` 系列（`>=`、`>>`、`>>>`）**不合并**为单个 token（嵌套泛型闭合需要独立 `>`）；由 ExpressionParserLayer 在运算符状态下重组。
- Lexer 不理解语义：`3.14` 输出 `Word "3"` + `Notation "."` + `Word "14"`，由 LiteralParserLayer 组合。

## 词法职责与边界

Lexer 负责词法识别与字符串解码，不做类型或名称语义判断；数值片段的组合由上文所述的 `LiteralParserLayer` 完成。不要在 Lexer 中加入语义判断。

- 斜杠家族（`/`、`//`、`/*`）由专门的 `SlashLexerLayer` 分流；
- `EndOfFileToken` 由 `Lexer.Tokenize` 在输出末尾追加；输入结束时以
  虚拟换行冲刷帧（FlushLayers）弹栈，未闭合字符串/块注释即 LexerException；
- 位置计量：`CharRange.sourceName` 是源名唯一来源
  （`CharPosition` 不携带）；`CharPosition.offset` 是 0 起始字符索引；
  行/列 1 起始，换行算当前行最后一列；token 头跳过空白字符；
  EOF 冲刷帧占一个末尾虚拟位置，保证冲刷 token 的 End 正确；
- **token 范围为左闭右开 [Start, End)**：End 是最后一个字符的下一位置；
- 块注释不吞字符、不吞换行（按行分段，换行以 LineBreakToken 入流）；
  行尾归一只把 `\r\n`/`\r` 归一为 `\n`；
- 复合赋值（`+=`/`*=` 等）不合并 token（与 `>=` 同策略，Parser 遇 op+`=` 重组为
  CompoundAssignmentExpressionASTNode）；字符字面量 `'` 已实现
  （CharLexerLayer + CharToken + CharLiteralASTNode，转义复用 StringEscape）；
  多行字符串 `"""` 已实现（SYNTAX §3.3：
  Swift 风格严格多行，QuoteLexerLayer 分流 `"`/`""`/`"""`，转义表 StringEscape 单源）；
- **字符串插值词法帧机制（SYNTAX §3.8）**：字符串层遇未转义的 `${`
  （挂起 `$` 延迟判定，`\$` 不算引导）产出文本段 + InterpolationStartToken
  并**压基础层**正常词法；驱动按 **token 层**大括号计数配平（字符串/字符/
  注释内容不产生记号 token，天然豁免），归零把 `}` 改发 InterpolationEndToken
  并弹回字符串层；嵌套插值经帧栈递归，EOF 帧未闭合先于冲刷报错。
  多行层段 token 以原文暂存保序，闭界确定缩进基准后统一回填解码内容。
  首个插值出现时修正首段起点和段尾，排除开界引号与引导 `$`；
  当前单行字符串的末段仍包含闭界引号范围，不能把所有段描述为“无引号 span”。

## 驱动与词法层

入口在 [Lexer.cs](../../../Lexer/Lexer.cs)。`Tokenize(StreamReader, sourceName)` 先读完整文本，只把 CRLF/CR 归一为 LF；不能用 `ReplaceLineEndings`，否则会改变字符串中的 form feed、NEL、U+2028 与 U+2029。行列和 `offset` 对应归一后的 UTF-16 码元流，不能直接当作原文件字节偏移或 Unicode 标量索引。`Tokenize(string)` 通过 UTF-8 内存流委托同一实现，使用 `GetAwaiter().GetResult()` 保留原始 `LexerException`。

主循环维护 `Stack<ILexerLayer>`，起始栈只有 `BaseLexerLayer`。`ParseChar` 返回：

- `Continue`：消费当前字符，继续本层。
- `PushLayer(layer, shouldKeepChar)`：压入新层；true 将同一字符交给新层重放，false 前进。
- `PopLayer(shouldKeepChar)`：弹出本层；true 将同一字符交给恢复的栈顶重放，false 前进。

字符重放不增加 `offset` 或重复推进位置。该 bool 是词法协议；Parser 使用独立的 `TokenDisposition` 枚举，两者不能混写。

`BaseLexerLayer` 按字符选择词法层：固定分类中的字母/数字或下划线进入 `WordLexerLayer`；单引号预消费后进入 `CharLexerLayer`；双引号预消费后进入 `QuoteLexerLayer`；斜杠预消费后进入 `SlashLexerLayer`；LF 发出 `LineBreakToken`，其他空白跳过；剩余字符进入 `NotationLexerLayer`。Word 可同时包含关键字、标识符与数值片段，保留字和合法名字由 Parser 判别。

`IdentifierCharacters` 用生成的固定 BMP 字母/数字范围定义分类，避免宿主 Unicode 分类差异；UTF-16 代理码元不会进入 Word。`tools/Generate-IdentifierCharacters.py` 是更新范围的生成入口，更新时须核验宿主分类与回归。`NotationLexerLayer` 最多看两个字符；只合并 `<=`、`==`、`!=`、`<<`、`->`。`>=`、`>>`、`>>>` 和复合赋值保持分离；`++`、`--` 不是 Rigi 运算符，不合并。

`SlashLexerLayer` 根据下个字符选择 `CommentLineLexerLayer` 或 `CommentBlockLexerLayer`，其他情况发出单独 `/` 并重放当前字符（`/=` 同样拆分）。注释层由分流层持有并转发，子层完成时分流层同步弹出。块注释不处理转义、不支持嵌套，按 `*/` 结束；挂起的 `*` 若非闭合则进入内容。行注释与块注释都保留真实换行作为独立 Token，Parser 只跳过注释本体。

## 字符串、字符与插值

`QuoteLexerLayer` 判断 `"…"`、`""` 与 `"""`，确定形态后向持有的字符串层转发，字符串层完成时一起弹出。`StringLexerLayer` 逐字符解码 `StringEscape` 的转义，拒绝真实换行与未知转义。`CharLexerLayer` 使用同一转义表，要求恰好一个 Unicode 标量；合法 UTF-16 代理对合为 `uint`，空字符、多标量、孤立代理、换行和未闭合均拒绝。

`MultilineStringLexerLayer` 的开界三引号后必须立即换行，该换行剥除；闭界前当前行只能有空白，以其长度作为缩进剥除基准。非空内容行不足此缩进时报错，全空白行成为空行。先剥缩进，再统一解码转义；转义的引号不参与闭界判定，不支持反斜杠接真实换行的续行。

插值的词法流为：

```text
StringToken(text)
InterpolationStartToken
  普通表达式 Token（可以包含字符串、字符、注释、lambda 与嵌套插值）
InterpolationEndToken
StringToken(text)
… EndOfFileToken
```

两种字符串层都挂起未转义的 `$`：下一字符是 `{` 才进入插值，否则补回字面文本；`\$` 不触发插值。文本段先入流（空段也发出），再发开始标记并压入帧根 `BaseLexerLayer`。`Lexer.ContextImpl` 以 Token 的大括号维护深度，字符串/字符/注释内容不产生大括号记号而天然豁免；深度归零把记号 `}` 改成结束标记，并在记号层弹出时弹出帧根、恢复宿主字符串层。嵌套开始标记递归压帧。

多行层在闭界之前不知道剥缩进基准，先把原文段 Token 保序放入流，闭界时回填全部段的解码内容；词法全部完成后才开始 Parser，因而不暴露半成品。只有首段的首行从行首开始并参与剥缩进；后续段的首行是插值闭合后的行内残余，不再次剥除。Parser 按解码文本判空，空文本段不生成插值 AST part。

范围来自原始归一化源码，而非解码内容长度。首个插值段的 Start 移过开界、段尾回收引导 `$`；开始标记的 span 仅覆盖 `{`，所以引导 `$` 不被任何 Token span 覆盖。单行末段仍以闭引号后一位置作 End，多行插值尾段不包含闭界三引号；普通无插值字符串的 Token 包括定界符。消费这些范围时须沿用代码中的真实契约。

## EOF、长度与验证

输入结束后先把当前位置推进到末尾，用一次虚拟 LF 冲刷 Word/Notation/Slash/行注释层；重放的虚拟 LF 不交给 Base，不产生额外语句终止符。栈须收敛为唯一 Base，未闭合字符串、字符或块注释抛 `LexerException`；插值帧仍有深度时，先报缺失 `}`，避免宿主字符串换行错误掩盖原因。随后追加唯一正式 EOF，其 span 为末尾零宽范围。

`Token.Content` 与 `Append` 以 `StringBuilder` 保存内容，单个内容上限是 `Token.MaxContentLength = 1024 * 1024` 个字符，超限抛词法错误；该值是 Token 容量限制，不是文件大小限制。字符 Token 和插值标记有专门的只读值/Content 语义。

回归入口包括 [LexerFuzzTests.cs](../../../Tests/LexerFuzzTests.cs) 的确定用例、错误、位置、固定 BMP 分类、随机不变量与 Parser 集成；字符、多行串和插值的 AST 验证还见 [LiteralParserTests.cs](../../../Tests/LiteralParserTests.cs)。
