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
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            if (currentToken is NotationToken nt && nt.Content == ",")
            {
                context.RaiseError($"Unexpected ',' before argument: {currentToken}");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 标识符：可能是具名实参（name = ...），先记录名字。
            // 数字、true/false/null 等字面量词与关键字不算标识符
            if (currentToken is WordToken wt && IsIdentifierStart(wt.Content))
            {
                pendingName = wt.Content;
                state = State.MaybeNamed;
                return ParserLayerResult.Continue.Instance;
            }

            // 其他 token：位置实参，委托表达式解析（先建实参节点并入列）
            return DelegatePositionalArgument(context, null, TokenDisposition.Replay);
        }

        // 判断 Word 内容是否为标识符起点（排除数字、字面量词与关键字）
        private static bool IsIdentifierStart(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            if (!char.IsLetter(content[0]) && content[0] != '_') return false;
            if (Keywords.ControlStreamKeywords.Contains(content) ||
                Keywords.DeclarationKeywords.Contains(content) ||
                Keywords.StringOperators.Contains(content) ||
                Keywords.StringValues.Contains(content)) return false;
            return true;
        }

        // 已读标识符：判断是否为具名实参
        private ParserLayerResult HandleMaybeNamed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "=")
            {
                // 具名实参：创建实参节点（带上名字）并委托表达式解析值
                var argument = new ArgumentASTNode(parentNode) { Name = pendingName };
                pendingName = null;
                targetList.Add(argument);
                state = State.ArgParsed;
                return new ParserLayerResult.PushLayer(
                    new ExpressionParserLayer(argument.Value), TokenDisposition.Consume);
            }

            // 位置实参：标识符作为符号引用起点，表达式继续（foo(name + 1)）。
            // 已消费的标识符包成未挂载的符号节点作为 seed 传给表达式层。
            var symbolRef = new SymbolReferenceASTNode();
            symbolRef.Symbol.symbol.elements.Add(new SymbolElement { name = pendingName! });
            pendingName = null;
            return DelegatePositionalArgument(context, symbolRef, TokenDisposition.Replay);
        }

        // 位置实参委托：先创建 ArgumentASTNode 并入列，再让表达式层填充其 Value Root
        private ParserLayerResult DelegatePositionalArgument(
            ParserLayerContext context,
            SymbolReferenceASTNode? seed,
            TokenDisposition disposition)
        {
            var argument = new ArgumentASTNode(parentNode);
            targetList.Add(argument);
            state = State.ArgParsed;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(argument.Value, seed), disposition);
        }

        // 实参已解析：等待 , 或闭合括号
        private ParserLayerResult HandleArgParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ",")
            {
                state = State.ArgStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (IsClosingBracket(currentToken))
            {
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
