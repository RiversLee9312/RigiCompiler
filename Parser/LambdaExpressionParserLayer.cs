using System;

namespace LatteCompiler
{
    /// <summary>
    /// Lambda 表达式解析器（roadmap #21，SYNTAX.md §5）
    ///
    /// 解析 [async] func{(params)\&lt;T&gt;: ReturnType -> body}
    /// func/async 关键字由 ExpressionParserLayer 先行消费，本层从 { 开始；
    /// trailing lambda（list.map{...}）同样从 { 进入，因此入口统一。
    ///
    /// 状态流转：
    /// OpenBraceExpected →（委托形参列表）→ AfterParameters
    ///   → [AfterGenerics] →（委托返回类型）→ ArrowExpected
    ///   → BodyStart →（委托 body 表达式）→ CloseBraceExpected → 弹出
    ///
    /// 委托说明（Delegate, don't implement）：
    /// - 形参列表委托 ParameterListParserLayer（原地写入 node.Parameters）
    /// - 泛型形参委托 GenericParametersParserLayer（原地写入 node.GenericParameters）
    /// - 返回类型委托 TypeReferenceParserLayer（原地写入 node.ReturnType）
    /// - body 委托 ExpressionParserLayer（结果经 IResultConsumer 回填）
    ///
    /// 当前限制：body 仅支持单表达式，多语句块待 P2 CodeBlockParserLayer。
    /// </summary>
    public class LambdaExpressionParserLayer : IParserLayer, IResultProducer, IResultConsumer
    {
        private readonly LambdaExpressionASTNode targetNode;

        private enum State
        {
            OpenBraceExpected,  // 等待 {
            AfterParameters,    // 形参列表已解析，等待 : 或 \<（泛型形参）
            AfterGenerics,      // 泛型形参已解析，等待 :
            ArrowExpected,      // 返回类型已解析，等待 ->
            BodyStart,          // -> 已读，等待 body 表达式开始
            CloseBraceExpected  // body 已解析，等待 }
        }

        private State state = State.OpenBraceExpected;

        // 等待子 Layer 结果时的回填动作（委托前设置，OnChildResult 时消费）
        private Action<ASTNode?>? pendingResultHandler;

        public LambdaExpressionParserLayer(LambdaExpressionASTNode target)
        {
            targetNode = target;
        }

        // IResultProducer：返回填好的 lambda 节点
        public ASTNode? GetResult() => targetNode;

        // IResultConsumer：接收 body 表达式的解析结果
        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            var handler = pendingResultHandler;
            pendingResultHandler = null;
            handler?.Invoke(result);
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // 本层只处于结构性等待状态（括号/箭头/花括号），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.OpenBraceExpected:
                    return HandleOpenBraceExpected(currentToken, context);
                case State.AfterParameters:
                    return HandleAfterParameters(currentToken, context);
                case State.AfterGenerics:
                    return HandleAfterGenerics(currentToken, context);
                case State.ArrowExpected:
                    return HandleArrowExpected(currentToken, context);
                case State.BodyStart:
                    return HandleBodyStart(currentToken, context);
                case State.CloseBraceExpected:
                    return HandleCloseBraceExpected(currentToken, context);
                default:
                    context.RaiseError($"Invalid LambdaExpressionParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        // 等待 { ：消费后直接委托形参列表（ParameterListParserLayer 自行等待 ( ）
        private ParserLayerResult HandleOpenBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.AfterParameters;
                return new ParserLayerResult.PushLayer(
                    new ParameterListParserLayer(targetNode.Parameters), false);
            }

            context.RaiseError($"Expected '{{' to start lambda body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 形参列表已解析：: 进入返回类型，\< 进入泛型形参（SYNTAX §5.1 泛型在形参列表之后）
        private ParserLayerResult HandleAfterParameters(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.ArrowExpected;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(targetNode.ReturnType), false);
            }

            if (currentToken is NotationToken bs && bs.Content == "\\")
            {
                targetNode.GenericParameters = new GenericParameterListASTNode(targetNode);
                state = State.AfterGenerics;
                // GenericParametersParserLayer 初始状态等待 \，保留当前 token
                return new ParserLayerResult.PushLayer(
                    new GenericParametersParserLayer(targetNode.GenericParameters), true);
            }

            context.RaiseError($"Expected ':' or '\\<' after lambda parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 泛型形参已解析：必须是 : （返回类型）
        private ParserLayerResult HandleAfterGenerics(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.ArrowExpected;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(targetNode.ReturnType), false);
            }

            context.RaiseError($"Expected ':' after lambda generic parameters, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 等待 ->
        private ParserLayerResult HandleArrowExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == Notations.ARROW)
            {
                state = State.BodyStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '->' before lambda body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // body 开始：压入 ExpressionParserLayer（保留 token 交给它）
        private ParserLayerResult HandleBodyStart(Token currentToken, ParserLayerContext context)
        {
            state = State.CloseBraceExpected;
            pendingResultHandler = result => targetNode.Body = (ExpressionASTNode)result!;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), true);
        }

        // 等待 } ：消费后完成解析，弹出本层
        private ParserLayerResult HandleCloseBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected '}}' to close lambda expression, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }
    }
}
