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
    /// 每个实参表达式委托 ExpressionParserLayer 解析（结果经 IResultConsumer 回传）。
    /// 实参节点原地填充到调用方提供的 List&lt;ArgumentASTNode&gt;，无需结果回传本层。
    ///
    /// 具名判别：标识符后紧跟 = 才是具名实参；否则该标识符作为位置实参的
    /// 符号引用起点，表达式继续解析（foo(name + 1) 合法）。
    /// </summary>
    public class ArgumentListParserLayer : IParserLayer, IResultConsumer
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
        private ExpressionASTNode? pendingValue = null;

        public ArgumentListParserLayer(
            List<ArgumentASTNode> target, BracketKind kind, ASTNode parent)
        {
            targetList = target;
            bracketKind = kind;
            parentNode = parent;
        }

        // IResultConsumer：接收 ExpressionParserLayer 的实参解析结果
        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            if (result is ExpressionASTNode expr)
            {
                pendingValue = expr;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
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
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        // 期待实参或闭合括号
        private ParserLayerResult HandleArgStart(Token currentToken, ParserLayerContext context)
        {
            // 闭合括号：空列表或尾逗号结束
            if (IsClosingBracket(currentToken))
            {
                return new ParserLayerResult.PopLayer(false);
            }

            if (currentToken is NotationToken nt && nt.Content == ",")
            {
                context.RaiseError($"Unexpected ',' before argument: {currentToken}");
                return new ParserLayerResult.PopLayer(false);
            }

            // 标识符：可能是具名实参（name = ...），先记录名字。
            // 数字、true/false/null 等字面量词与关键字不算标识符
            if (currentToken is WordToken wt && IsIdentifierStart(wt.Content))
            {
                pendingName = wt.Content;
                state = State.MaybeNamed;
                return ParserLayerResult.Continue.Instance;
            }

            // 其他 token：位置实参，委托表达式解析
            state = State.ArgParsed;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(parentNode), true);
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
                // 具名实参：委托表达式解析值
                state = State.ArgParsed;
                return new ParserLayerResult.PushLayer(new ExpressionParserLayer(parentNode), false);
            }

            // 位置实参：标识符作为符号引用起点，表达式继续（foo(name + 1)）
            var symbolRef = new SymbolReferenceASTNode(parentNode);
            symbolRef.Symbol.symbol.elements.Add(new SymbolElement { name = pendingName! });
            pendingName = null;
            state = State.ArgParsed;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(parentNode, symbolRef), true);
        }

        // 实参已解析：等待 , 或闭合括号
        private ParserLayerResult HandleArgParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ",")
            {
                CommitArgument(context);
                state = State.ArgStart;
                return ParserLayerResult.Continue.Instance;
            }

            if (IsClosingBracket(currentToken))
            {
                CommitArgument(context);
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected ',' or '{ClosingBracket()}' after argument, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 提交实参节点到目标列表
        private void CommitArgument(ParserLayerContext context)
        {
            if (pendingValue == null)
            {
                context.RaiseError("Internal error: argument value is missing");
                return;
            }

            targetList.Add(new ArgumentASTNode(parentNode, pendingValue, pendingName));
            pendingName = null;
            pendingValue = null;
        }

        private bool IsClosingBracket(Token token)
        {
            return token is NotationToken nt && nt.Content == ClosingBracket();
        }

        private string ClosingBracket() => bracketKind == BracketKind.Round ? ")" : "]";
    }
}
