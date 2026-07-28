using System;

namespace LatteCompiler
{
    /// <summary>
    /// Lambda 表达式解析器（roadmap #21，SYNTAX.md §5.1）
    ///
    /// 解析 [async] func{(params)\&lt;T&gt;: ReturnType -> [named 标签] body}
    /// func/async 关键字由 ExpressionParserLayer 先行消费，本层从 { 开始；
    /// trailing lambda（list.map{...}）同样从 { 进入，因此入口统一。
    ///
    /// 体两形态（互斥，创建时定）：
    /// - 单表达式体：-> expr（隐式取值），委托 ExpressionParserLayer
    /// - 多语句块体：-> { ... }（SYNTAX §5.1），委托 CodeBlockParserLayer；
    ///   所有路径必须显式 return@_ / return@标签 产出值
    /// named 标签写在 -> 之后、体之前，配合 return@标签 穿透内层匿名块。
    ///
    /// 状态流转：
    /// OpenBraceExpected →（委托形参列表）→ AfterParameters
    ///   → [AfterGenerics] →（委托返回类型）→ ArrowExpected
    ///   → BodyStart → [BodyLabelExpected → BodyAfterLabel]
    ///   →（单表达式：委托 ExpressionParserLayer / 块体：委托 CodeBlockParserLayer）
    ///   → CloseBraceExpected → 弹出
    ///
    /// 委托说明（Delegate, don't implement）：
    /// - 形参列表委托 ParameterListParserLayer（原地写入 node.Parameters）
    /// - 泛型形参委托 GenericParametersParserLayer（原地写入 node.GenericParameters）
    /// - 返回类型委托 TypeReferenceParserLayer（原地写入 node.ReturnType）
    /// - 单表达式体委托 ExpressionParserLayer（直接附加到 node.Body Root，无回传）
    /// - 块体委托 CodeBlockParserLayer（原地填充 node.BlockBody）
    ///
    /// lambda 是裸 return 边界（SYNTAX §5.1）：体内（含嵌套 seq/if/循环等代码块、
    /// 以及单表达式体内 if/switch 表达式的分支块）一律禁止裸 return，
    /// 两种体形态都以 allowBareReturn: false 下传。
    /// </summary>
    public class LambdaExpressionParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly LambdaExpressionASTNode targetNode;

        private enum State
        {
            OpenBraceExpected,  // 等待 {
            AfterParameters,    // 形参列表已解析，等待 : 或 \<（泛型形参）
            AfterGenerics,      // 泛型形参已解析，等待 :
            ArrowExpected,      // 返回类型已解析，等待 ->
            BodyStart,          // -> 已读：named 标签、{ 块体或单表达式体
            BodyLabelExpected,  // named 已读：等待标签名
            BodyAfterLabel,     // 标签已读：{ 块体或单表达式体
            CloseBraceExpected  // body 已解析，等待 }
        }

        private State state = State.OpenBraceExpected;

        public LambdaExpressionParserLayer(LambdaExpressionASTNode target)
        {
            targetNode = target;
        }

        // 层弹出时回填施工目标的源码范围（M28）
        public void ReceiveSpan(CharRange span) => targetNode.Span ??= span;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：lambda 必须由 } 闭合，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/箭头/花括号/named），允许跨行；
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
                case State.BodyLabelExpected:
                    return HandleBodyLabelExpected(currentToken, context);
                case State.BodyAfterLabel:
                    return HandleBodyAfterLabel(currentToken, context);
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

        // -> 已读：named 标签、{ 块体或单表达式体
        private ParserLayerResult HandleBodyStart(Token currentToken, ParserLayerContext context)
        {
            // named 标签（SYNTAX §5.1）：写在 -> 之后、体之前
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                state = State.BodyLabelExpected;
                return ParserLayerResult.Continue.Instance;
            }

            return DelegateBody(currentToken, context);
        }

        // named 已读：等待标签名（只查首字符，与循环标签同规则）
        private ParserLayerResult HandleBodyLabelExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && Keywords.IsIdentifierStart(wt.Content))
            {
                targetNode.Label = wt.Content;
                state = State.BodyAfterLabel;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label name after 'named', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 标签已读：{ 块体或单表达式体（不允许第二个 named）
        private ParserLayerResult HandleBodyAfterLabel(Token currentToken, ParserLayerContext context)
        {
            return DelegateBody(currentToken, context);
        }

        // 体分发：{ → 多语句块体（CodeBlockParserLayer）；其余 → 单表达式体
        // （ExpressionParserLayer）。lambda 是裸 return 边界：两形态都以
        // allowBareReturn: false 下传（块体内裸 return 由 CodeBlockParserLayer 拒绝；
        // 单表达式体内 if/switch 表达式分支块同样禁止）
        private ParserLayerResult DelegateBody(Token currentToken, ParserLayerContext context)
        {
            // 块体：创建 BlockBody 代码块（与 Body 互斥，创建时定）
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                targetNode.BlockBody = new CodeBlockASTNode(targetNode);
                state = State.CloseBraceExpected;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(targetNode.BlockBody, allowBareReturn: false),
                    TokenDisposition.Replay);
            }

            // 单表达式体：创建 Body Root（保留 token 交给表达式层）
            targetNode.Body = new ExpressionRootASTNode(targetNode);
            state = State.CloseBraceExpected;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(targetNode.Body) { allowBareReturn = false },
                TokenDisposition.Replay);
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
