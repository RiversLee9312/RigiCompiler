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
    /// - body 委托 ExpressionParserLayer（直接附加到 node.Body Root，无回传）
    ///
    /// 当前限制：body 仅支持单表达式，多语句块待 P2 CodeBlockParserLayer。
    /// </summary>
    public class LambdaExpressionParserLayer : IParserLayer
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

        public LambdaExpressionParserLayer(LambdaExpressionASTNode target)
        {
            targetNode = target;
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：lambda 必须由 } 闭合，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

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
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 等待 { ：消费后直接委托形参列表（ParameterListParserLayer 自行等待 ( ）
        private ParserLayerResult HandleOpenBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.AfterParameters;
                return new ParserLayerResult.PushLayer(
                    new ParameterListParserLayer(targetNode.Parameters), TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '{{' to start lambda body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 形参列表已解析：: 进入返回类型，\< 进入泛型形参（SYNTAX §5.1 泛型在形参列表之后）
        private ParserLayerResult HandleAfterParameters(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.ArrowExpected;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(targetNode.ReturnType), TokenDisposition.Consume);
            }

            if (currentToken is NotationToken bs && bs.Content == "\\")
            {
                targetNode.GenericParameters = new GenericParameterListASTNode(targetNode);
                state = State.AfterGenerics;
                // GenericParametersParserLayer 初始状态等待 \，保留当前 token
                return new ParserLayerResult.PushLayer(
                    new GenericParametersParserLayer(targetNode.GenericParameters), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected ':' or '\\<' after lambda parameter list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 泛型形参已解析：必须是 : （返回类型）
        private ParserLayerResult HandleAfterGenerics(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.ArrowExpected;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(targetNode.ReturnType), TokenDisposition.Consume);
            }

            context.RaiseError($"Expected ':' after lambda generic parameters, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // body 开始：压入 ExpressionParserLayer（保留 token 交给它），直接附加到 Body Root
        private ParserLayerResult HandleBodyStart(Token currentToken, ParserLayerContext context)
        {
            state = State.CloseBraceExpected;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(targetNode.Body), TokenDisposition.Replay);
        }

        // 等待 } ：消费后完成解析，弹出本层
        private ParserLayerResult HandleCloseBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '}}' to close lambda expression, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
