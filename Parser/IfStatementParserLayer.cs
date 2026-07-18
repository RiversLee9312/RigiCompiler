using System;

namespace LatteCompiler
{
    /// <summary>
    /// if 解析器（roadmap #7，SYNTAX.md §7.1）
    ///
    /// 当前仅实现 if 表达式模式：if (cond) { then } else { else }
    /// 作为表达式时必须有 else 分支（SYNTAX §7.1）；语句模式留待 P2。
    /// if 关键字由 ExpressionParserLayer 消费，本层从 ( 开始。
    ///
    /// 状态流转：
    /// OpenParenExpected → ConditionStart → CloseParenExpected
    ///   → ThenOpenBraceExpected → ThenBodyStart → ThenCloseBraceExpected
    ///   → ElseExpected → ElseOpenBraceExpected → ElseBodyStart → ElseCloseBraceExpected → 弹出
    ///
    /// 委托说明：条件与两个分支均委托 ExpressionParserLayer（结果经 IResultConsumer 回填）。
    /// 当前限制：分支仅支持单表达式，多语句块待 P2 CodeBlockParserLayer。
    /// </summary>
    public class IfStatementParserLayer : IParserLayer, IResultProducer, IResultConsumer
    {
        private readonly IfExpressionASTNode targetNode;
        private readonly bool isExpression;

        private enum State
        {
            OpenParenExpected,      // 等待 (
            ConditionStart,         // ( 已读，等待条件表达式开始
            CloseParenExpected,     // 条件已解析，等待 )
            ThenOpenBraceExpected,  // 等待 then 分支 {
            ThenBodyStart,          // { 已读，等待 then 表达式开始
            ThenCloseBraceExpected, // then 已解析，等待 }
            ElseExpected,           // 等待 else 关键字
            ElseOpenBraceExpected,  // 等待 else 分支 {
            ElseBodyStart,          // { 已读，等待 else 表达式开始
            ElseCloseBraceExpected  // else 已解析，等待 }
        }

        private State state = State.OpenParenExpected;

        // 等待子 Layer 结果时的回填动作（委托前设置，OnChildResult 时消费）
        private Action<ASTNode?>? pendingResultHandler;

        public IfStatementParserLayer(IfExpressionASTNode target, bool isExpression = true)
        {
            targetNode = target;
            this.isExpression = isExpression;

            // 语句模式（if 语句/elif 链）属于 P2，当前仅支持表达式模式
            if (!isExpression)
            {
                throw new NotImplementedException("if 语句模式待 P2 实现，当前仅支持 if 表达式");
            }
        }

        // IResultProducer：返回填好的 if 表达式节点
        public ASTNode? GetResult() => targetNode;

        // IResultConsumer：接收条件/分支表达式的解析结果
        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            var handler = pendingResultHandler;
            pendingResultHandler = null;
            handler?.Invoke(result);
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // 本层只处于结构性等待状态（括号/花括号/else），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.OpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.ConditionStart,
                        "Expected '(' after if");
                case State.ConditionStart:
                    return DelegateExpression(State.CloseParenExpected,
                        result => targetNode.Condition = (ExpressionASTNode)result!);
                case State.CloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.ThenOpenBraceExpected,
                        "Expected ')' after if condition");
                case State.ThenOpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.ThenBodyStart,
                        "Expected '{' to start if-then branch");
                case State.ThenBodyStart:
                    return DelegateExpression(State.ThenCloseBraceExpected,
                        result => targetNode.ThenExpression = (ExpressionASTNode)result!);
                case State.ThenCloseBraceExpected:
                    return ExpectNotation(currentToken, context, "}", State.ElseExpected,
                        "Expected '}' to close if-then branch");
                case State.ElseExpected:
                    return HandleElseExpected(currentToken, context);
                case State.ElseOpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.ElseBodyStart,
                        "Expected '{' to start else branch");
                case State.ElseBodyStart:
                    return DelegateExpression(State.ElseCloseBraceExpected,
                        result => targetNode.ElseExpression = (ExpressionASTNode)result!);
                case State.ElseCloseBraceExpected:
                    return HandleElseCloseBraceExpected(currentToken, context);
                default:
                    context.RaiseError($"Invalid IfStatementParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
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
            return new ParserLayerResult.PopLayer(false);
        }

        // 委托一个子表达式：压入 ExpressionParserLayer（保留 token），并登记回填动作
        private ParserLayerResult DelegateExpression(State nextState, Action<ASTNode?> resultHandler)
        {
            state = nextState;
            pendingResultHandler = resultHandler;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), true);
        }

        // 等待 else：if 表达式必须包含 else 分支（SYNTAX §7.1）
        private ParserLayerResult HandleElseExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.ELSE)
            {
                state = State.ElseOpenBraceExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"if 表达式必须包含 else 分支（SYNTAX.md §7.1），got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // else 分支 } ：消费后完成解析，弹出本层
        private ParserLayerResult HandleElseCloseBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected '}}' to close else branch, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }
    }
}
