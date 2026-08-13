using System;

namespace RigiCompiler
{
    /// <summary>
    /// switch 解析器（roadmap #8，SYNTAX.md §7.2）
    ///
    /// 两种形态共用同一套匹配规则，区别只在出现位置与分支体取值：
    /// - 表达式模式：switch(expr) [named 标签] { (pattern) -> { body } ... default -> { body } }
    ///   switch 关键字由 ExpressionParserLayer 消费，本层从 ( 开始，产出 SwitchExpressionASTNode
    /// - 语句模式：形态同上（无 named），出现在语句位置，结果值被丢弃；
    ///   本层从 switch 关键字开始，产出 SwitchStatementASTNode 挂入代码块
    ///
    /// 规则：
    /// - 不含 _ 的分支为值匹配（要求编译期常量，语义检查留待后续阶段）
    /// - 含 _ 的分支为模式匹配，_ 代表被检查的值，按普通符号解析
    /// - 两种形态都必须有 default 分支（SYNTAX §7.2）
    /// - 分支体统一为代码块（委托 CodeBlockParserLayer）：表达式形态「单表达式分支
    ///   隐式取值」是「块内恰好一条 ExpressionStatement」的语义规则，解析层无特判；
    ///   多语句分支体必须显式 return@_（匿名）或 return@标签（named 命名后）
    ///
    /// 状态流转：
    /// [SwitchKeywordExpected（语句模式）] → SelectorOpenParenExpected → SelectorStart
    ///   → SelectorCloseParenExpected
    ///   →（表达式）NamedCheck → [LabelNameExpected →] OpenBraceExpected
    ///   →（语句）OpenBraceExpected
    ///   → CaseStart
    ///     →（值/模式分支）PatternStart → PatternCloseParenExpected → CaseArrowExpected
    ///       → CaseBodyExpected →（委托 CodeBlockParserLayer）→ AfterCaseBody → CaseStart
    ///     →（default）DefaultArrowExpected → DefaultBodyExpected
    ///       →（委托 CodeBlockParserLayer）→ CaseStart
    ///     → } → 弹出
    ///
    /// 施工协议（大扫除后）：selector/pattern 由 ExpressionParserLayer 直接附加到目标
    /// 节点的各 ExpressionRootASTNode，分支体由 CodeBlockParserLayer 原地填充，无任何回传。
    ///
    /// allowBareReturn（SYNTAX §5.1）：父上下文是否允许裸 return。lambda 体内为 false
    /// 并向所有嵌套代码块传染；switch 分支体不是 lambda 边界，继承父上下文标记。
    /// </summary>
    public class SwitchStatementParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly SwitchExpressionASTNode? exprNode;
        private readonly SwitchStatementASTNode? stmtNode;
        private readonly bool isExpression;
        private readonly bool allowBareReturn;

        private enum State
        {
            SwitchKeywordExpected,   // 语句模式：等待 switch 关键字
            SelectorOpenParenExpected,   // 等待 selector 的 (
            SelectorStart,               // ( 已读，等待 selector 表达式开始
            SelectorCloseParenExpected,  // selector 已解析，等待 )
            NamedCheck,                  // 表达式模式：named 标签或分支列表 {
            LabelNameExpected,           // named 已读，等待标签名
            OpenBraceExpected,           // 等待分支列表 {
            CaseStart,                   // 等待分支 ( 、default 或结束 }
            PatternStart,                // 分支 ( 已读，等待 pattern 表达式开始
            PatternCloseParenExpected,   // pattern 已解析，等待 )
            CaseArrowExpected,           // 等待分支 ->
            CaseBodyExpected,            // 等待分支体 {（委托 CodeBlockParserLayer）
            AfterCaseBody,               // 分支体已解析：封口分支 span 并提交，回 CaseStart
            DefaultArrowExpected,        // default 已读，等待 ->
            DefaultBodyExpected          // 等待 default 体 {（委托 CodeBlockParserLayer）
        }

        private State state;
        // 非 null 的那个为施工目标（表达式/语句二选一）
        private SwitchExpressionASTNode TargetExpr => exprNode!;
        private SwitchStatementASTNode TargetStmt => stmtNode!;

        // 读取中的分支（累积，体解析完提交到 Cases）
        private SwitchCaseASTNode? pendingCase = null;
        // 分支起点（( token 的范围），作分支 span 的 Start（M28）
        private CharRange? pendingCaseStart = null;

        // 表达式模式：switch 关键字已由 ExpressionParserLayer 消费
        public SwitchStatementParserLayer(SwitchExpressionASTNode target, bool allowBareReturn = true)
        {
            exprNode = target;
            isExpression = true;
            this.allowBareReturn = allowBareReturn;
            state = State.SelectorOpenParenExpected;
        }

        // 语句模式：创建 SwitchStatementASTNode 并挂入代码块，本层从 switch 关键字开始
        public SwitchStatementParserLayer(CodeBlockASTNode parentBlock, bool allowBareReturn = true)
        {
            stmtNode = new SwitchStatementASTNode(parentBlock);
            parentBlock.Statements.Add(stmtNode);
            isExpression = false;
            this.allowBareReturn = allowBareReturn;
            state = State.SwitchKeywordExpected;
        }

        // 层弹出时回填 switch 节点的源码范围（M28）：回填非 null 的那个施工目标
        public void ReceiveSpan(CharRange span)
        {
            if (exprNode != null)
            {
                exprNode.Span ??= span;
            }
            else
            {
                stmtNode!.Span ??= span;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：switch 必须由 } 闭合，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/箭头/花括号/default/named），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.SwitchKeywordExpected:
                    return HandleSwitchKeywordExpected(currentToken, context);
                case State.SelectorOpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.SelectorStart,
                        "Expected '(' after switch");
                case State.SelectorStart:
                    // selector 在 () 内：换行按空白处理（M31，SYNTAX §1.1）
                    return DelegateExpression(State.SelectorCloseParenExpected,
                        isExpression ? TargetExpr.Selector : TargetStmt.Selector,
                        insideParens: true);
                case State.SelectorCloseParenExpected:
                    return ExpectNotation(currentToken, context, ")",
                        isExpression ? State.NamedCheck : State.OpenBraceExpected,
                        "Expected ')' after switch selector");
                case State.NamedCheck:
                    return HandleNamedCheck(currentToken, context);
                case State.LabelNameExpected:
                    return HandleLabelNameExpected(currentToken, context);
                case State.OpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.CaseStart,
                        "Expected '{' to start switch case list");
                case State.CaseStart:
                    return HandleCaseStart(currentToken, context);
                case State.PatternStart:
                    pendingCase = new SwitchCaseASTNode(
                        isExpression ? (ASTNode)TargetExpr : TargetStmt);
                    // 分支 span：起点为 (（见 HandleCaseStart），分支体解析完时封 End（M28）；
                    // pattern 在 () 内：换行按空白处理（M31，SYNTAX §1.1）
                    pendingCase.Span = pendingCaseStart;
                    return DelegateExpression(State.PatternCloseParenExpected, pendingCase.Pattern,
                        insideParens: true);
                case State.PatternCloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.CaseArrowExpected,
                        "Expected ')' after switch case pattern");
                case State.CaseArrowExpected:
                    return ExpectNotation(currentToken, context, Notations.ARROW, State.CaseBodyExpected,
                        "Expected '->' after switch case pattern");
                case State.CaseBodyExpected:
                    return HandleCaseBodyExpected(currentToken, context);
                case State.AfterCaseBody:
                    return HandleAfterCaseBody(currentToken, context);
                case State.DefaultArrowExpected:
                    return ExpectNotation(currentToken, context, Notations.ARROW, State.DefaultBodyExpected,
                        "Expected '->' after default");
                case State.DefaultBodyExpected:
                    return HandleDefaultBodyExpected(currentToken, context);
                default:
                    context.RaiseError($"Invalid SwitchStatementParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 期待一个结构性符号：命中则消费并转入下一状态
        private ParserLayerResult ExpectNotation(
            Token currentToken, ParserLayerContext context,
            string notation, State nextState, string errorMessage)
        {
            if (currentToken is NotationToken nt && nt.Content == notation)
            {
                state = nextState;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"{errorMessage}, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 委托一个子表达式：压入 ExpressionParserLayer（保留 token），
        // 表达式直接附加到目标 Root；insideParens 标记 () 内语境（换行按空白处理）；
        // allowBareReturn 随父上下文传染（selector/pattern 里的 if/switch 表达式分支体同规则）
        private ParserLayerResult DelegateExpression(
            State nextState, ExpressionRootASTNode expressionTarget, bool insideParens = false)
        {
            state = nextState;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(expressionTarget)
                {
                    insideParens = insideParens,
                    allowBareReturn = allowBareReturn
                },
                TokenDisposition.Replay);
        }

        // ===== 语句模式入口 =====

        // 语句模式：等待 switch 关键字
        private ParserLayerResult HandleSwitchKeywordExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.SWITCH)
            {
                state = State.SelectorOpenParenExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'switch', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ===== 表达式模式：named 标签 =====

        // ) 已读：named 标签或分支列表 {
        private ParserLayerResult HandleNamedCheck(Token currentToken, ParserLayerContext context)
        {
            // named 标签（SYNTAX §7.2）：命名后分支体内可用 return@标签 穿透内层匿名块
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                state = State.LabelNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // 分支列表 {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.CaseStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'named' or '{{' after switch selector, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // named 已读：等待标签名（只查首字符，与循环标签同规则）
        private ParserLayerResult HandleLabelNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && Keywords.IsIdentifierStart(wt.Content))
            {
                TargetExpr.Label = wt.Content;
                state = State.OpenBraceExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label name after 'named', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ===== 分支列表 =====

        // 分支列表入口：普通分支 ( 、default 分支或结束 }
        private ParserLayerResult HandleCaseStart(Token currentToken, ParserLayerContext context)
        {
            // 普通分支：( pattern ) -> { body }
            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                pendingCaseStart = context.GetLocation();   // 分支 span 起点：(（M28）
                state = State.PatternStart;
                return ParserLayerResult.Continue.Instance;
            }

            // default 分支
            if (currentToken is WordToken wt && wt.Content == Keywords.DEFAULT)
            {
                if ((isExpression ? TargetExpr.DefaultBody : TargetStmt.DefaultBody) != null)
                {
                    context.RaiseError("switch 只允许一个 default 分支");
                }
                state = State.DefaultArrowExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // 分支列表结束：两种形态都必须有 default（SYNTAX §7.2）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                if ((isExpression ? TargetExpr.DefaultBody : TargetStmt.DefaultBody) == null)
                {
                    context.RaiseError("switch 必须包含 default 分支（SYNTAX.md §7.2）");
                }
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '(', 'default' or '}}' in switch case list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待分支体 { ：委托 CodeBlockParserLayer 施工分支体
        private ParserLayerResult HandleCaseBodyExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.AfterCaseBody;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(pendingCase!.Body, allowBareReturn),
                    TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' to start switch case body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 分支体已解析：封口分支 span（最近被消费的 token 即分支体的 }）、提交分支，
        // 回到 CaseStart 重新分发当前 token
        private ParserLayerResult HandleAfterCaseBody(Token currentToken, ParserLayerContext context)
        {
            if (pendingCase!.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                pendingCase.Span = s;
            }
            if (isExpression)
            {
                TargetExpr.Cases.Add(pendingCase);
            }
            else
            {
                TargetStmt.Cases.Add(pendingCase);
            }
            pendingCase = null;
            pendingCaseStart = null;
            state = State.CaseStart;
            return ParseToken(currentToken, context);
        }

        // 等待 default 体 { ：创建 DefaultBody 代码块并委托 CodeBlockParserLayer 施工
        private ParserLayerResult HandleDefaultBodyExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                CodeBlockASTNode defaultBody;
                if (isExpression)
                {
                    defaultBody = new CodeBlockASTNode(TargetExpr);
                    TargetExpr.DefaultBody = defaultBody;
                }
                else
                {
                    defaultBody = new CodeBlockASTNode(TargetStmt);
                    TargetStmt.DefaultBody = defaultBody;
                }
                state = State.CaseStart;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(defaultBody, allowBareReturn),
                    TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' to start default body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
