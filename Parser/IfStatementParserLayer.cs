using System;

namespace LatteCompiler
{
    /// <summary>
    /// if 解析器（roadmap #7，SYNTAX.md §7.1）
    ///
    /// 两种模式：
    /// - 表达式模式：if (cond) { then } else { else }，必须有 else（SYNTAX §7.1）；
    ///   if 关键字由 ExpressionParserLayer 消费，本层从 ( 开始，产出 IfExpressionASTNode
    /// - 语句模式：if (cond) { ... } [else { ... } / else if ...]，else 可选；
    ///   本层从 if 关键字开始，产出 IfStatementASTNode 挂入代码块
    ///
    /// 状态流转（条件部分两模式共享）：
    /// [IfKeywordExpected] → OpenParenExpected → ConditionStart → CloseParenExpected
    ///   → 表达式：ThenOpenBraceExpected → ThenBodyStart → ThenCloseBraceExpected
    ///     → ElseExpected → ElseOpenBraceExpected → ElseBodyStart → ElseCloseBraceExpected → 弹出
    ///   → 语句：ThenBlockExpected（委托 CodeBlockParserLayer）→ ElseCheck
    ///     → [ElseBranchExpected →（块/嵌套 if）→ Done] → 弹出
    ///
    /// 施工协议（大扫除后）：条件与分支表达式由 ExpressionParserLayer 直接附加到
    /// 目标节点的各 ExpressionRootASTNode，无任何结果回传。
    /// </summary>
    public class IfStatementParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly IfExpressionASTNode? exprNode;
        private readonly IfStatementASTNode? stmtNode;
        private readonly bool isExpression;

        private enum State
        {
            IfKeywordExpected,      // 语句模式：等待 if 关键字
            OpenParenExpected,      // 等待 (
            ConditionStart,         // ( 已读，等待条件表达式开始
            CloseParenExpected,     // 条件已解析，等待 )
            // 表达式模式
            ThenOpenBraceExpected,  // 等待 then 分支 {
            ThenBodyStart,          // { 已读，等待 then 表达式开始
            ThenCloseBraceExpected, // then 已解析，等待 }
            ElseExpected,           // 等待 else 关键字
            ElseOpenBraceExpected,  // 等待 else 分支 {
            ElseBodyStart,          // { 已读，等待 else 表达式开始
            ElseCloseBraceExpected, // else 已解析，等待 }
            // 语句模式
            ThenBlockExpected,      // 等待 then 块 {（委托 CodeBlockParserLayer）
            ElseCheck,              // then 块已结束：else 分支或弹出
            ElseBranchExpected,     // else 已读：{ 或 if（else if 链）
            Done                    // 语句模式收尾：直接弹出（保留 token）
        }

        private State state;

        // 表达式模式：if 关键字已由 ExpressionParserLayer 消费
        public IfStatementParserLayer(IfExpressionASTNode target)
        {
            exprNode = target;
            isExpression = true;
            state = State.OpenParenExpected;
        }

        // 语句模式：创建 IfStatementASTNode 并挂入代码块
        public IfStatementParserLayer(CodeBlockASTNode parentBlock)
            : this(AttachStatement(parentBlock))
        {
        }

        private IfStatementParserLayer(IfStatementASTNode node)
        {
            stmtNode = node;
            isExpression = false;
            state = State.IfKeywordExpected;
        }

        private static IfStatementASTNode AttachStatement(CodeBlockASTNode parentBlock)
        {
            var node = new IfStatementASTNode(parentBlock);
            parentBlock.Children.Add(node);
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
                stmtNode!.Span ??= span;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：if 结构（条件/分支/else）未完整时收到 EOF 均为不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/花括号/else），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
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
                    return DelegateExpression(State.CloseParenExpected,
                        isExpression ? exprNode!.Condition : stmtNode!.Condition);
                case State.CloseParenExpected:
                    return HandleCloseParenExpected(currentToken, context);
                case State.ThenOpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.ThenBodyStart,
                        "Expected '{' to start if-then branch");
                case State.ThenBodyStart:
                    return DelegateExpression(State.ThenCloseBraceExpected, exprNode!.ThenExpression);
                case State.ThenCloseBraceExpected:
                    return ExpectNotation(currentToken, context, "}", State.ElseExpected,
                        "Expected '}' to close if-then branch");
                case State.ElseExpected:
                    return HandleElseExpected(currentToken, context);
                case State.ElseOpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.ElseBodyStart,
                        "Expected '{' to start else branch");
                case State.ElseBodyStart:
                    return DelegateExpression(State.ElseCloseBraceExpected, exprNode!.ElseExpression);
                case State.ElseCloseBraceExpected:
                    return HandleElseCloseBraceExpected(currentToken, context);
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
        // 表达式直接附加到目标 Root
        private ParserLayerResult DelegateExpression(State nextState, ExpressionRootASTNode expressionTarget)
        {
            state = nextState;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(expressionTarget), TokenDisposition.Replay);
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
                state = isExpression ? State.ThenOpenBraceExpected : State.ThenBlockExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ')' after if condition, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // ===== 表达式模式 =====

        // 等待 else：if 表达式必须包含 else 分支（SYNTAX §7.1）
        private ParserLayerResult HandleElseExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.ELSE)
            {
                state = State.ElseOpenBraceExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"if 表达式必须包含 else 分支（SYNTAX.md §7.1），got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // else 分支 } ：消费后完成解析，弹出本层
        private ParserLayerResult HandleElseCloseBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '}}' to close else branch, got: {currentToken}");
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
                    new CodeBlockParserLayer(stmtNode!.ThenBlock), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' to start if-then block, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // then 块已结束：语句模式的 else 可选
        private ParserLayerResult HandleElseCheck(Token currentToken, ParserLayerContext context)
        {
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
                    new CodeBlockParserLayer(elseBlock), TokenDisposition.Replay);
            }

            // else if 链：嵌套 IfStatementASTNode 作为 ElseBranch
            if (currentToken is WordToken wt && wt.Content == Keywords.IF)
            {
                var nested = new IfStatementASTNode(stmtNode);
                stmtNode!.ElseBranch = nested;
                state = State.Done;
                return new ParserLayerResult.PushLayer(
                    new IfStatementParserLayer(nested), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' or 'if' after else, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
