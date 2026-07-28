using System;

namespace LatteCompiler
{
    /// <summary>
    /// typeOf 表达式解析器（SYNTAX.md §3.7）
    ///
    /// 解析 typeOf(expr)：取得某个值或类型的运行时类型，返回 Type\<T>。
    /// typeOf 关键字由 ExpressionParserLayer 消费，本层从 ( 开始。
    ///
    /// 状态流转：OpenParenExpected → OperandStart → CloseParenExpected → 弹出
    ///
    /// 施工协议（大扫除后）：操作数由 ExpressionParserLayer 直接附加到
    /// targetNode.Operand（ExpressionRootASTNode），无任何结果回传。
    /// </summary>
    public class TypeOfExpressionParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly TypeOfExpressionASTNode targetNode;

        // 父上下文是否允许裸 return（SYNTAX §5.1）：操作数深处的
        // if/switch 表达式分支体继承该标记（由 ExpressionParserLayer 设置）
        internal bool allowBareReturn = true;

        private enum State
        {
            OpenParenExpected,   // 等待 (
            OperandStart,        // ( 已读，等待操作数开始
            CloseParenExpected   // 操作数已解析，等待 )
        }

        private State state = State.OpenParenExpected;

        public TypeOfExpressionParserLayer(TypeOfExpressionASTNode target)
        {
            targetNode = target;
        }

        // 层弹出时回填施工目标的源码范围（M28）
        public void ReceiveSpan(CharRange span) => targetNode.Span ??= span;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：typeOf 必须由 ) 闭合，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/关键字），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.OpenParenExpected:
                    return HandleOpenParenExpected(currentToken, context);
                case State.OperandStart:
                    return HandleOperandStart(currentToken, context);
                case State.CloseParenExpected:
                    return HandleCloseParenExpected(currentToken, context);
                default:
                    context.RaiseError($"Invalid TypeOfExpressionParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 等待 (
        private ParserLayerResult HandleOpenParenExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                state = State.OperandStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '(' after typeOf, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 操作数开始：压入 ExpressionParserLayer（保留 token 交给它）
        private ParserLayerResult HandleOperandStart(Token currentToken, ParserLayerContext context)
        {
            state = State.CloseParenExpected;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(targetNode.Operand) { allowBareReturn = allowBareReturn },
                TokenDisposition.Replay);
        }

        // 等待 ) ：消费后完成解析，弹出本层
        private ParserLayerResult HandleCloseParenExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected ')' to close typeOf expression, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
