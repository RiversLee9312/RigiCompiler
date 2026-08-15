using System;
using System.Collections.Generic;

namespace RigiCompiler
{
    /// <summary>
    /// Lambda 表达式解析器（roadmap #21，SYNTAX.md §5.1）
    ///
    /// 解析 func{[@W(...)] [async] (params)[: ReturnType] -> [named 标签] body}
    /// （省略 : ReturnType 即为无返回值 void lambda，基类为 core.Action 族，见 §5.2）
    /// func 关键字由 ExpressionParserLayer 先行消费，本层从 { 开始；
    /// async 标记写在 { 之后、形参列表之前（SYNTAX §5：func{async (...)...}）；
    /// wrapper 注解写在同一位置且更靠前（用户裁定：func{ @Timed async (...)...}），
    /// 语义 = Method wrapper 修饰该 lambda（SYNTAX §14.4）；
    /// trailing lambda（list.map{...}）同样从 { 进入，因此入口统一。
    ///
    /// 体两形态（互斥，创建时定）：
    /// - 单表达式体：-> expr（有返回值时隐式取值；void 时为表达式语句语义，语义层定）
    /// - 多语句块体：-> { ... }（SYNTAX §5.1），委托 CodeBlockParserLayer；
    ///   有返回值时所有路径必须显式 return@_ / return@标签 产出值（语义层校验）
    /// named 标签写在 -> 之后、体之前，配合 return@标签 穿透内层匿名块。
    ///
    /// 状态流转：
    /// OpenBraceExpected → AnnotationsOrAsync →（可多 @Name[(args)]）→
    ///   AsyncOrParameters →（委托形参列表）→ AfterParameters
    ///   →（可选 : 返回类型）→ ArrowExpected / 或直接 -> 进 BodyStart（void）
    ///   → BodyStart → [BodyLabelExpected → BodyAfterLabel]
    ///   →（单表达式：委托 ExpressionParserLayer / 块体：委托 CodeBlockParserLayer）
    ///   → CloseBraceExpected → 弹出
    ///
    /// 委托说明（Delegate, don't implement）：
    /// - 注解名复用 PathParserLayer（原地写入 AnnotationASTNode.Name）
    /// - 注解实参复用 ArgumentListParserLayer（原地写入 AnnotationASTNode.Arguments）
    /// - 形参列表委托 ParameterListParserLayer（原地写入 node.Parameters）
    /// - 返回类型委托 TypeReferenceParserLayer（原地写入 node.ReturnType；省略则 null）
    /// - 单表达式体委托 ExpressionParserLayer（直接附加到 node.Body Root，无回传）
    /// - 块体委托 CodeBlockParserLayer（原地填充 node.BlockBody）
    ///
    /// lambda 不支持泛型形参（SYNTAX §5.1）：遇 \< 报明确错误；泛型 callable 请显式声明类型。
    ///
    /// lambda 是裸 return 边界（SYNTAX §5.1）：体内（含嵌套 seq/if/循环等代码块、
    /// 以及单表达式体内 if/switch 表达式的分支块）一律禁止裸 return，
    /// 两种体形态都以 allowBareReturn: false 下传。
    /// </summary>
    public class LambdaExpressionParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly LambdaExpressionASTNode targetNode;
        // 正在解析的注解（@Name[(args)]）：targetNode 即 lambda 节点，可直接挂接
        private AnnotationASTNode? currentAnnotation;

        private enum State
        {
            OpenBraceExpected,  // 等待 {
            AnnotationsOrAsync, // { 已读：可多个 @Name[(args)]，随后 async 或形参
            AnnotationName,     // @ 已读：注解名由 PathParserLayer 解析，等 ( 或下一 token
            AsyncOrParameters,  // 注解已读完：可选 async 标记，否则进入形参列表
            AfterParameters,    // 形参列表已解析，等待 :（返回类型）或 ->（void）
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
                case State.AnnotationsOrAsync:
                    return HandleAnnotationsOrAsync(currentToken, context);
                case State.AnnotationName:
                    return HandleAnnotationName(currentToken, context);
                case State.AsyncOrParameters:
                    return HandleAsyncOrParameters(currentToken, context);
                case State.AfterParameters:
                    return HandleAfterParameters(currentToken, context);
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

        // 等待 { ：消费后进入注解/async/形参判定（ParameterListParserLayer 自行等待 ( ）
        private ParserLayerResult HandleOpenBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.AnnotationsOrAsync;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '{{' to start lambda body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // { 已读：可多个 @Name[(args)]（Method wrapper 应用，SYNTAX §14.4），
        // 随后可选的 async 标记，再进入形参列表。注解直接挂到 lambda 节点
        // （lambda 节点先于本层创建，无声明层的「先解析后 Attach」问题）。
        private ParserLayerResult HandleAnnotationsOrAsync(Token currentToken, ParserLayerContext context)
        {
            // 注解列表结束（或还没开始）：封口上一枚注解的 span
            SealCurrentAnnotation(context);

            if (currentToken is NotationToken at && at.Content == "@")
            {
                var ann = new AnnotationASTNode(targetNode);
                var loc = context.GetLocation();
                ann.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
                targetNode.Annotations.Add(ann);
                currentAnnotation = ann;
                state = State.AnnotationName;
                // 注解名（可为 a.b 路径）复用 PathParserLayer
                return new ParserLayerResult.PushLayer(
                    new PathParserLayer(ann.Name, lineBreakSensitive: true), TokenDisposition.Consume);
            }

            // 非注解：转 async/形参判定
            state = State.AsyncOrParameters;
            return HandleAsyncOrParameters(currentToken, context);
        }

        // 注解名已解析：( 转实参列表（复用 ArgumentListParserLayer，开括号由本层
        // 消费——与声明注解既有约定一致）；否则注解结束，当前 token 交回注解/async 判定
        private ParserLayerResult HandleAnnotationName(Token currentToken, ParserLayerContext context)
        {
            var ann = currentAnnotation!;
            // 注解名不得为空（@(1) 或 @ 后换行）
            if (ann.Name.symbol.elements.Count == 0)
            {
                throw context.RaiseError($"Expected annotation name after '@', got: {currentToken}");
            }

            // 注解名后紧跟 ( 才算注解实参（@Timed("tag")）；有空白间隔的 ( 是
            // lambda 形参列表（@Timed (x: i32)）——用户裁定的 lambda 头语法
            if (currentToken is NotationToken n && n.Content == "("
                && IsContiguousWithPrevious(currentToken, context))
            {
                ann.HasArguments = true;
                state = State.AnnotationsOrAsync;
                return new ParserLayerResult.PushLayer(
                    new ArgumentListParserLayer(
                        ann.Arguments, ArgumentListParserLayer.BracketKind.Round, ann), TokenDisposition.Consume);
            }

            SealCurrentAnnotation(context);
            state = State.AnnotationsOrAsync;
            return HandleAnnotationsOrAsync(currentToken, context);
        }

        // 当前 token 是否与最近被消费的 token 紧邻（无空白/注释间隔）。
        // lambda 头内注解名后的 ( 据此区分注解实参与 lambda 形参列表
        private static bool IsContiguousWithPrevious(Token currentToken, ParserLayerContext context)
        {
            var previous = context.GetPreviousLocation();
            return currentToken.CharRange.Start.offset == previous.End.offset
                && currentToken.CharRange.sourceName == previous.sourceName;
        }

        // 当前注解 span 封口：End 取最近被消费的 token（注解名末尾或实参列表的 )）
        private void SealCurrentAnnotation(ParserLayerContext context)
        {
            if (currentAnnotation is not { Span: { } s })
            {
                return;
            }
            s.End = context.GetPreviousLocation().End;
            currentAnnotation.Span = s;
            currentAnnotation = null;
        }

        // 注解已读完：可选的 async 标记（SYNTAX §5：func{async (...)...}，仅识别
        // 一次，且只在 ( 之前——形参名与 async 无歧义）；其余 token 交给形参列表层
        private ParserLayerResult HandleAsyncOrParameters(Token currentToken, ParserLayerContext context)
        {
            if (!targetNode.IsAsync && currentToken is WordToken wt && wt.Content == Keywords.ASYNC)
            {
                targetNode.IsAsync = true;
                return ParserLayerResult.Continue.Instance;
            }

            state = State.AfterParameters;
            return new ParserLayerResult.PushLayer(
                new ParameterListParserLayer(targetNode.Parameters), TokenDisposition.Replay);
        }

        // 形参列表已解析：
        // - : → 有返回类型，委托 TypeReferenceParserLayer，再等 ->
        // - -> → 省略返回类型 = void lambda（SYNTAX §5.1），直接进 BodyStart
        // - \< → 明确拒绝（lambda 不支持泛型形参）
        private ParserLayerResult HandleAfterParameters(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken colon && colon.Content == ":")
            {
                // 有返回类型：创建 ReturnType 施工目标后委托类型引用层
                targetNode.ReturnType = new TypeReferenceASTNode(targetNode);
                state = State.ArrowExpected;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(targetNode.ReturnType), TokenDisposition.Consume);
            }

            if (currentToken is NotationToken arrow && arrow.Content == Notations.ARROW)
            {
                // 省略返回类型 = 无返回值 void lambda（ReturnType 保持 null）
                state = State.BodyStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken bs && bs.Content == "\\")
            {
                context.RaiseError("lambda 不支持泛型参数（SYNTAX §5.1）");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected ':' or '->' after lambda parameter list, got: {currentToken}");
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
