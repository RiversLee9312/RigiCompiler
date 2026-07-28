using System;

namespace LatteCompiler
{
    /// <summary>
    /// if 解析器（roadmap #7，SYNTAX.md §7.1）
    ///
    /// 两种模式：
    /// - 表达式模式：if (cond) [named 标签] { then } else { else }，必须有 else（SYNTAX §7.1）；
    ///   if 关键字由 ExpressionParserLayer 消费，本层从 ( 开始，产出 IfExpressionASTNode；
    ///   分支体统一为代码块（委托 CodeBlockParserLayer）——「单表达式分支隐式取值」是
    ///   「块内恰好一条 ExpressionStatement」的语义规则，解析层无特判
    /// - 语句模式：if (cond) { ... } [else { ... } / else if ...]，else 可选；
    ///   本层从 if 关键字开始，产出 IfStatementASTNode 挂入代码块
    ///
    /// 状态流转（条件部分两模式共享）：
    /// [IfKeywordExpected] → OpenParenExpected → ConditionStart → CloseParenExpected
    ///   → 表达式：ThenNamedCheck → [ThenLabelNameExpected → ThenBodyBlockExpected]
    ///     →（委托 CodeBlockParserLayer 施工 ThenBody）→ ElseExpected
    ///     → ElseBlockExpected →（委托 CodeBlockParserLayer 施工 ElseBody）→ ExprDone → 弹出
    ///   → 语句：ThenBlockExpected（委托 CodeBlockParserLayer）→ ElseCheck
    ///     → [ElseBranchExpected →（块/嵌套 if）→ Done] → 弹出
    ///
    /// 施工协议（大扫除后）：条件表达式由 ExpressionParserLayer 直接附加到
    /// 目标节点的 Condition Root，分支代码块由 CodeBlockParserLayer 原地填充，
    /// 无任何结果回传。
    ///
    /// allowBareReturn（SYNTAX §5.1）：父上下文是否允许裸 return。lambda 体内为 false
    /// 并向所有嵌套代码块传染；if 表达式分支体不是 lambda 边界，继承父上下文标记。
    /// </summary>
    public class IfStatementParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly IfExpressionASTNode? exprNode;
        private readonly IfStatementASTNode? stmtNode;
        private readonly bool isExpression;
        private readonly bool allowBareReturn;

        private enum State
        {
            IfKeywordExpected,      // 语句模式：等待 if 关键字
            OpenParenExpected,      // 等待 (
            ConditionStart,         // ( 已读，等待条件表达式开始
            CloseParenExpected,     // 条件已解析，等待 )
            // 表达式模式
            ThenNamedCheck,         // ) 已读：named 标签或 then 分支 {
            ThenLabelNameExpected,  // named 已读：等待标签名
            ThenBodyBlockExpected,  // 标签已读：等待 then 分支 {
            ElseExpected,           // then 块已结束：等待 else 关键字
            ElseBlockExpected,      // else 已读：等待 else 分支 {
            ExprDone,               // 表达式模式收尾：直接弹出（保留 token）
            // 语句模式
            ThenBlockExpected,      // 等待 then 块 {（委托 CodeBlockParserLayer）
            ElseCheck,              // then 块已结束：else 分支或弹出
            ElseBranchExpected,     // else 已读：{ 或 if（else if 链）
            Done                    // 语句模式收尾：直接弹出（保留 token）
        }

        private State state;

        // 表达式模式：if 关键字已由 ExpressionParserLayer 消费
        public IfStatementParserLayer(IfExpressionASTNode target, bool allowBareReturn = true)
        {
            exprNode = target;
            isExpression = true;
            this.allowBareReturn = allowBareReturn;
            state = State.OpenParenExpected;
        }

        // 语句模式：创建 IfStatementASTNode 并挂入代码块
        public IfStatementParserLayer(CodeBlockASTNode parentBlock, bool allowBareReturn = true)
            : this(AttachStatement(parentBlock), allowBareReturn)
        {
        }

        private IfStatementParserLayer(IfStatementASTNode node, bool allowBareReturn)
        {
            stmtNode = node;
            isExpression = false;
            this.allowBareReturn = allowBareReturn;
            state = State.IfKeywordExpected;
        }

        private static IfStatementASTNode AttachStatement(CodeBlockASTNode parentBlock)
        {
            var node = new IfStatementASTNode(parentBlock);
            parentBlock.Statements.Add(node);
            return node;
        }

        // Span 回填（M28）：两模式共用入口，回填非 null 的那个施工目标
        public void ReceiveSpan(CharRange span)
        {
            if (exprNode != null)
            {
                exprNode.Span ??= span;
            }
            else
            {
                // 无 else 时：ElseCheck 为前瞻 else 跳过的换行不属于本节点（M31），
                // End 封回 then 块 }，span 不拖尾换行符到下一行
                if (elseCheckEntryEnd is { } entryEnd && stmtNode!.ElseBranch == null)
                {
                    span.End = entryEnd.End;
                }
                stmtNode!.Span ??= span;
            }
        }

        // ElseCheck 首次进入时 then 块 } 的范围（无 else 时用于封 End）
        private CharRange? elseCheckEntryEnd = null;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：结构完整态（else 可选/表达式已闭合）上交 EOF（规则 6）；
            // 其余（条件/分支/else 未完整）为不完整结构
            if (currentToken is EndOfFileToken)
            {
                if (state == State.ElseCheck || state == State.Done || state == State.ExprDone)
                {
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                }
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/花括号/else/named），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责。
            // 但 Done/ExprDone 终态不消费换行——节点 span 不拖尾换行符到下一行（M31，M28 约定）
            if (currentToken is LineBreakToken && state != State.Done && state != State.ExprDone)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.IfKeywordExpected:
                    return HandleIfKeywordExpected(currentToken, context);
                case State.OpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.ConditionStart,
                        "Expected '(' after if");
                case State.ConditionStart:
                    // 条件在 () 内：换行按空白处理（M31，SYNTAX §1.1）
                    return DelegateExpression(State.CloseParenExpected,
                        isExpression ? exprNode!.Condition : stmtNode!.Condition,
                        insideParens: true);
                case State.CloseParenExpected:
                    return HandleCloseParenExpected(currentToken, context);
                case State.ThenNamedCheck:
                    return HandleThenNamedCheck(currentToken, context);
                case State.ThenLabelNameExpected:
                    return HandleThenLabelNameExpected(currentToken, context);
                case State.ThenBodyBlockExpected:
                    return HandleThenBodyBlockExpected(currentToken, context);
                case State.ElseExpected:
                    return HandleElseExpected(currentToken, context);
                case State.ElseBlockExpected:
                    return HandleElseBlockExpected(currentToken, context);
                case State.ExprDone:
                    // 表达式模式收尾：不消费 token，交还给父层（表达式层）
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                case State.ThenBlockExpected:
                    return HandleThenBlockExpected(currentToken, context);
                case State.ElseCheck:
                    return HandleElseCheck(currentToken, context);
                case State.ElseBranchExpected:
                    return HandleElseBranchExpected(currentToken, context);
                case State.Done:
                    // 语句模式收尾：不消费 token，交还给父层（代码块分发）
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                default:
                    context.RaiseError($"Invalid IfStatementParserLayer state: {state}");
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
        // 表达式直接附加到目标 Root；insideParens 标记条件等 () 内语境（换行按空白处理）；
        // allowBareReturn 随父上下文传染（条件里的 if/switch 表达式分支体同规则）
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

        // 委托一个分支代码块：压入 CodeBlockParserLayer（保留 { 交给它），
        // 原地填充目标块；allowBareReturn 随父上下文传染
        private ParserLayerResult DelegateBlock(State nextState, CodeBlockASTNode blockTarget)
        {
            state = nextState;
            return new ParserLayerResult.PushLayer(
                new CodeBlockParserLayer(blockTarget, allowBareReturn), TokenDisposition.Replay);
        }

        // ===== 共享部分 =====

        // 语句模式：等待 if 关键字
        private ParserLayerResult HandleIfKeywordExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.IF)
            {
                state = State.OpenParenExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'if', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 条件 ) 已读：按模式进入 then 分支
        private ParserLayerResult HandleCloseParenExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                state = isExpression ? State.ThenNamedCheck : State.ThenBlockExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ')' after if condition, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ===== 表达式模式 =====

        // ) 已读：可选 named 标签，随后是 then 分支 {
        private ParserLayerResult HandleThenNamedCheck(Token currentToken, ParserLayerContext context)
        {
            // named 标签（SYNTAX §7.1）：命名后分支体内可用 return@标签 穿透内层匿名块
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                state = State.ThenLabelNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // then 分支 {：委托 CodeBlockParserLayer 施工 ThenBody
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                return DelegateBlock(State.ElseExpected, exprNode!.ThenBody);
            }

            context.RaiseError($"Expected 'named' or '{{' after if condition, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // named 已读：等待标签名（只查首字符，与循环标签同规则）
        private ParserLayerResult HandleThenLabelNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && Keywords.IsIdentifierStart(wt.Content))
            {
                exprNode!.Label = wt.Content;
                state = State.ThenBodyBlockExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label name after 'named', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 标签已读：必须是 then 分支 {
        private ParserLayerResult HandleThenBodyBlockExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                return DelegateBlock(State.ElseExpected, exprNode!.ThenBody);
            }

            context.RaiseError($"Expected '{{' to start if-then branch, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待 else：if 表达式必须包含 else 分支（SYNTAX §7.1）
        private ParserLayerResult HandleElseExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.ELSE)
            {
                state = State.ElseBlockExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"if 表达式必须包含 else 分支（SYNTAX.md §7.1），got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // else 已读：等待 else 分支 {，委托 CodeBlockParserLayer 施工 ElseBody
        private ParserLayerResult HandleElseBlockExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                return DelegateBlock(State.ExprDone, exprNode!.ElseBody);
            }

            context.RaiseError($"Expected '{{' to start else branch, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ===== 语句模式 =====

        // 等待 then 块 { ：委托 CodeBlockParserLayer（保留 token 交给它）
        private ParserLayerResult HandleThenBlockExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.ElseCheck;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(stmtNode!.ThenBlock, allowBareReturn), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' to start if-then block, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // then 块已结束：语句模式的 else 可选
        private ParserLayerResult HandleElseCheck(Token currentToken, ParserLayerContext context)
        {
            // 首次进入：记下 then 块 } 的范围（无 else 时 ReceiveSpan 用它封 End）
            elseCheckEntryEnd ??= context.GetPreviousLocation();

            if (currentToken is WordToken wt && wt.Content == Keywords.ELSE)
            {
                state = State.ElseBranchExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // 无 else：完成，保留 token 交还给代码块分发
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
        }

        // else 已读：{ 进入 else 块；if 进入 else if 链
        private ParserLayerResult HandleElseBranchExpected(Token currentToken, ParserLayerContext context)
        {
            // else 块
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                var elseBlock = new CodeBlockASTNode(stmtNode);
                stmtNode!.ElseBranch = elseBlock;
                state = State.Done;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(elseBlock, allowBareReturn), TokenDisposition.Replay);
            }

            // else if 链：嵌套 IfStatementASTNode 作为 ElseBranch
            if (currentToken is WordToken wt && wt.Content == Keywords.IF)
            {
                var nested = new IfStatementASTNode(stmtNode);
                stmtNode!.ElseBranch = nested;
                state = State.Done;
                return new ParserLayerResult.PushLayer(
                    new IfStatementParserLayer(nested, allowBareReturn), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' or 'if' after else, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
