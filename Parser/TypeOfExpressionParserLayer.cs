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
    /// 委托说明：操作数委托 ExpressionParserLayer，结果经 IResultConsumer 回填。
    /// </summary>
    public class TypeOfExpressionParserLayer : IParserLayer, IResultProducer, IResultConsumer
    {
        private readonly TypeOfExpressionASTNode targetNode;

        private enum State
        {
            OpenParenExpected,   // 等待 (
            OperandStart,        // ( 已读，等待操作数开始
            CloseParenExpected   // 操作数已解析，等待 )
        }

        private State state = State.OpenParenExpected;

        // 等待子 Layer 结果时的回填动作（委托前设置，OnChildResult 时消费）
        private Action<ASTNode?>? pendingResultHandler;

        public TypeOfExpressionParserLayer(TypeOfExpressionASTNode target)
        {
            targetNode = target;
        }

        // IResultProducer：返回填好的 typeOf 节点
        public ASTNode? GetResult() => targetNode;

        // IResultConsumer：接收操作数表达式的解析结果
        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            var handler = pendingResultHandler;
            pendingResultHandler = null;
            handler?.Invoke(result);
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
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
                    return new ParserLayerResult.PopLayer(false);
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
            return new ParserLayerResult.PopLayer(false);
        }

        // 操作数开始：压入 ExpressionParserLayer（保留 token 交给它）
        private ParserLayerResult HandleOperandStart(Token currentToken, ParserLayerContext context)
        {
            state = State.CloseParenExpected;
            pendingResultHandler = result => targetNode.Operand = (ExpressionASTNode)result!;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), true);
        }

        // 等待 ) ：消费后完成解析，弹出本层
        private ParserLayerResult HandleCloseParenExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected ')' to close typeOf expression, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }
    }
}
