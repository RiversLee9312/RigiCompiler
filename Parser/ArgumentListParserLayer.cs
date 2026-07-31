using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    /// <summary>
    /// 实参列表解析器
    ///
    /// 解析调用 `(...)`、索引 `[...]` 与 new 构造参数列表中的实参：
    /// - 位置实参：foo(1, x)
    /// - 具名实参：foo(name = 42, isDarkMode = true)
    /// - 混合：foo(1, name = 2)
    /// - 空列表：foo()
    ///
    /// 施工协议（大扫除后）：每个 ArgumentASTNode 在表达式解析之前就创建并加入
    /// 目标列表；实参表达式由 ExpressionParserLayer 直接附加到 argument.Value
    /// （ExpressionRootASTNode），无任何结果回传。
    ///
    /// 具名判别：标识符后紧跟 = 才是具名实参；否则该标识符作为位置实参的
    /// 符号引用起点，表达式继续解析（foo(name + 1) 合法）——退化的符号作为
    /// 未挂载 seed 传给 ExpressionParserLayer（父层已施工出的局部结构，非返回值）。
    /// </summary>
    public class ArgumentListParserLayer : IParserLayer
    {
        public enum BracketKind
        {
            Round,    // ( )
            Square    // [ ]
        }

        private readonly List<ArgumentASTNode> targetList;
        private readonly BracketKind bracketKind;
        private readonly ASTNode parentNode;

        // 父上下文是否允许裸 return（SYNTAX §5.1）：实参表达式深处的
        // if/switch 表达式分支体继承该标记（由 ExpressionParserLayer 设置）
        internal bool allowBareReturn = true;

        private enum State
        {
            ArgStart,     // 期待实参表达式、具名实参名或闭合括号
            MaybeNamed,   // 已读标识符，判断是否为具名实参（name = ...）
            ArgParsed     // 实参已解析，等待 , 或闭合括号
        }

        private State state = State.ArgStart;
        private string? pendingName = null;

        public ArgumentListParserLayer(
            List<ArgumentASTNode> target, BracketKind kind, ASTNode parent)
        {
            targetList = target;
            bracketKind = kind;
            parentNode = parent;
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：实参列表必须由闭合括号结束，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 括号未闭合时换行按空白处理（M31，SYNTAX §1.1）：
            // 调用实参表与索引实参表内允许自然续行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.ArgStart:
                    return HandleArgStart(currentToken, context);
                case State.MaybeNamed:
                    return HandleMaybeNamed(currentToken, context);
                case State.ArgParsed:
                    return HandleArgParsed(currentToken, context);
                default:
                    context.RaiseError($"Invalid ArgumentListParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 期待实参或闭合括号
        private ParserLayerResult HandleArgStart(Token currentToken, ParserLayerContext context)
        {
            // 闭合括号：空列表或尾逗号结束
            if (IsClosingBracket(currentToken))
            {
                // 空索引不合法（M31）：foo() 允许空参，a[] 不允许
                if (bracketKind == BracketKind.Square && targetList.Count == 0)
                {
                    context.RaiseError("Index argument list cannot be empty");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                }
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            if (currentToken is NotationToken nt && nt.Content == ",")
            {
                context.RaiseError($"Unexpected ',' before argument: {currentToken}");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 标识符：可能是具名实参（name = ...），先记录名字。
            // 必须是合法标识符（非数字词、非保留字，M31 统一走 Keywords.IsIdentifier）——
            // foo(return = 5) 这类保留字具名实参会被拒绝
            if (currentToken is WordToken wt && Keywords.IsIdentifier(wt.Content))
            {
                pendingName = wt.Content;
                state = State.MaybeNamed;
                return ParserLayerResult.Continue.Instance;
            }

            // 其他 token：位置实参，委托表达式解析（先建实参节点并入列）；
            // span 起点即当前 token（表达式的首个 token，Replay 交给表达式层）
            return DelegatePositionalArgument(context, null, context.GetLocation().Start, TokenDisposition.Replay);
        }

        // 已读标识符：判断是否为具名实参
        private ParserLayerResult HandleMaybeNamed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                // 具名实参：创建实参节点（带上名字）并委托表达式解析值；
                // span 起点是名字 token（当前为 =，名字即最近被消费的 token）
                var argument = new ArgumentASTNode(parentNode) { Name = pendingName };
                StartArgumentSpan(argument, context.GetPreviousLocation().Start, context);
                pendingName = null;
                targetList.Add(argument);
                state = State.ArgParsed;
                return new ParserLayerResult.PushLayer(
                    new ExpressionParserLayer(argument.Value)
                    {
                        insideParens = true,
                        allowBareReturn = allowBareReturn
                    },
                    TokenDisposition.Consume);
            }

            // 位置实参：标识符作为路径表达式起点，表达式继续（foo(name + 1)）。
            // 已消费的标识符包成未挂载的路径节点作为 seed 传给表达式层。
            // seed 及其首段节点的 span 即标识符 token（最近被消费的 token）的范围
            var seedRange = context.GetPreviousLocation();
            var seedPath = new PathExpressionASTNode();
            seedPath.Span = new CharRange
            {
                Start = seedRange.Start,
                End = seedRange.End,
                sourceName = seedRange.sourceName
            };
            seedPath.Head.Name = pendingName!;
            seedPath.Head.Span = seedPath.Span;
            pendingName = null;
            return DelegatePositionalArgument(context, seedPath, seedRange.Start, TokenDisposition.Replay);
        }

        // 位置实参委托：先创建 ArgumentASTNode 并入列，再让表达式层填充其 Value Root
        private ParserLayerResult DelegatePositionalArgument(
            ParserLayerContext context,
            PathExpressionASTNode? seed,
            CharPosition spanStart,
            TokenDisposition disposition)
        {
            var argument = new ArgumentASTNode(parentNode);
            StartArgumentSpan(argument, spanStart, context);
            targetList.Add(argument);
            state = State.ArgParsed;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(argument.Value, seed)
                {
                    insideParens = true,
                    allowBareReturn = allowBareReturn
                },
                disposition);
        }

        // 实参 span：创建时记 Start（具名为名字 token，位置实参为表达式首 token），
        // End 先取当前 token，待实参完成时封口
        private static void StartArgumentSpan(ArgumentASTNode argument, CharPosition start, ParserLayerContext context)
        {
            var current = context.GetLocation();
            argument.Span = new CharRange { Start = start, End = current.End, sourceName = current.sourceName };
        }

        // 实参 span 封口：`,` 或闭合括号出现时不属于实参，End 取最近被消费的 token
        private void SealLastArgument(ParserLayerContext context)
        {
            if (targetList.Count > 0 && targetList[targetList.Count - 1].Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                targetList[targetList.Count - 1].Span = s;
            }
        }

        // 实参已解析：等待 , 或闭合括号
        private ParserLayerResult HandleArgParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ",")
            {
                SealLastArgument(context);
                state = State.ArgStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (IsClosingBracket(currentToken))
            {
                SealLastArgument(context);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected ',' or '{ClosingBracket()}' after argument, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private bool IsClosingBracket(Token token)
        {
            return token is NotationToken nt && nt.Content == ClosingBracket();
        }

        private string ClosingBracket() => bracketKind == BracketKind.Round ? ")" : "]";
    }
}
