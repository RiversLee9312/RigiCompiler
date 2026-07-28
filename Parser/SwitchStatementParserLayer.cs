using System;

namespace LatteCompiler
{
    /// <summary>
    /// switch 解析器（roadmap #8，SYNTAX.md §7.2）
    ///
    /// 当前仅实现 switch 表达式模式：
    ///   switch(expr) { (pattern) -> { body } ... default -> { body } }
    /// - 不含 _ 的分支为值匹配（要求编译期常量，语义检查留待后续阶段）
    /// - 含 _ 的分支为模式匹配，_ 代表被检查的值，按普通符号解析
    /// - 作为表达式时必须有 default 分支（SYNTAX §7.2）；语句模式留待 P2
    /// switch 关键字由 ExpressionParserLayer 消费，本层从 ( 开始。
    ///
    /// 状态流转：
    /// SelectorOpenParenExpected → SelectorStart → SelectorCloseParenExpected
    ///   → OpenBraceExpected → CaseStart
    ///     →（值/模式分支）PatternStart → PatternCloseParenExpected → CaseArrowExpected
    ///       → CaseBodyOpenBraceExpected → CaseBodyStart → CaseBodyCloseBraceExpected → CaseStart
    ///     →（default）DefaultArrowExpected → DefaultBodyOpenBraceExpected
    ///       → DefaultBodyStart → DefaultBodyCloseBraceExpected → CaseStart
    ///     → } → 弹出
    ///
    /// 委托说明：selector/pattern/body 均由 ExpressionParserLayer 直接附加到目标
    /// 节点的各 ExpressionRootASTNode，无任何结果回传（大扫除后的施工协议）。
    /// 当前限制：分支体仅支持单表达式，多语句块待 P2 CodeBlockParserLayer。
    /// </summary>
    public class SwitchStatementParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly SwitchExpressionASTNode targetNode;

        private enum State
        {
            SelectorOpenParenExpected,   // 等待 selector 的 (
            SelectorStart,               // ( 已读，等待 selector 表达式开始
            SelectorCloseParenExpected,  // selector 已解析，等待 )
            OpenBraceExpected,           // 等待分支列表 {
            CaseStart,                   // 等待分支 ( 、default 或结束 }
            PatternStart,                // 分支 ( 已读，等待 pattern 表达式开始
            PatternCloseParenExpected,   // pattern 已解析，等待 )
            CaseArrowExpected,           // 等待分支 ->
            CaseBodyOpenBraceExpected,   // 等待分支体 {
            CaseBodyStart,               // { 已读，等待分支体表达式开始
            CaseBodyCloseBraceExpected,  // 分支体已解析，等待 }
            DefaultArrowExpected,        // default 已读，等待 ->
            DefaultBodyOpenBraceExpected,// 等待 default 体 {
            DefaultBodyStart,            // { 已读，等待 default 体表达式开始
            DefaultBodyCloseBraceExpected// default 体已解析，等待 }
        }

        private State state = State.SelectorOpenParenExpected;

        // 读取中的分支（累积，} 时提交到 Cases）
        private SwitchCaseASTNode? pendingCase = null;
        // 分支起点（( token 的范围），作分支 span 的 Start（M28）
        private CharRange? pendingCaseStart = null;

        public SwitchStatementParserLayer(SwitchExpressionASTNode target, bool isExpression = true)
        {
            targetNode = target;

            // 语句模式属于 P2，当前仅支持表达式模式
            if (!isExpression)
            {
                throw new NotImplementedException("switch 语句模式待 P2 实现，当前仅支持 switch 表达式");
            }
        }

        // 层弹出时回填 switch 表达式节点的源码范围（M28）
        public void ReceiveSpan(CharRange span) => targetNode.Span ??= span;

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：switch 必须由 } 闭合，收到 EOF 是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/箭头/花括号/default），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.SelectorOpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.SelectorStart,
                        "Expected '(' after switch");
                case State.SelectorStart:
                    // selector 在 () 内：换行按空白处理（M31，SYNTAX §1.1）
                    return DelegateExpression(State.SelectorCloseParenExpected, targetNode.Selector,
                        insideParens: true);
                case State.SelectorCloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.OpenBraceExpected,
                        "Expected ')' after switch selector");
                case State.OpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.CaseStart,
                        "Expected '{' to start switch case list");
                case State.CaseStart:
                    return HandleCaseStart(currentToken, context);
                case State.PatternStart:
                    pendingCase = new SwitchCaseASTNode(targetNode);
                    // 分支 span：起点为 (（见 HandleCaseStart），分支体解析完时封 End（M28）；
                    // pattern 在 () 内：换行按空白处理（M31，SYNTAX §1.1）
                    pendingCase.Span = pendingCaseStart;
                    return DelegateExpression(State.PatternCloseParenExpected, pendingCase.Pattern,
                        insideParens: true);
                case State.PatternCloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.CaseArrowExpected,
                        "Expected ')' after switch case pattern");
                case State.CaseArrowExpected:
                    return ExpectNotation(currentToken, context, Notations.ARROW, State.CaseBodyOpenBraceExpected,
                        "Expected '->' after switch case pattern");
                case State.CaseBodyOpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.CaseBodyStart,
                        "Expected '{' to start switch case body");
                case State.CaseBodyStart:
                    return DelegateExpression(State.CaseBodyCloseBraceExpected, pendingCase!.Body);
                case State.CaseBodyCloseBraceExpected:
                    return HandleCaseBodyCloseBraceExpected(currentToken, context);
                case State.DefaultArrowExpected:
                    return ExpectNotation(currentToken, context, Notations.ARROW, State.DefaultBodyOpenBraceExpected,
                        "Expected '->' after default");
                case State.DefaultBodyOpenBraceExpected:
                    return ExpectNotation(currentToken, context, "{", State.DefaultBodyStart,
                        "Expected '{' to start default body");
                case State.DefaultBodyStart:
                    targetNode.DefaultBody = new ExpressionRootASTNode(targetNode);
                    return DelegateExpression(State.DefaultBodyCloseBraceExpected, targetNode.DefaultBody);
                case State.DefaultBodyCloseBraceExpected:
                    return ExpectNotation(currentToken, context, "}", State.CaseStart,
                        "Expected '}' to close default body");
                default:
                    context.RaiseError($"Invalid SwitchStatementParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 委托一个子表达式：压入 ExpressionParserLayer（保留 token），
        // 表达式直接附加到目标 Root；insideParens 标记 () 内语境（换行按空白处理）
        private ParserLayerResult DelegateExpression(
            State nextState, ExpressionRootASTNode expressionTarget, bool insideParens = false)
        {
            state = nextState;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(expressionTarget) { insideParens = insideParens },
                TokenDisposition.Replay);
        }

        // 分支列表入口：普通分支 ( 、default 分支或结束 }
        private ParserLayerResult HandleCaseStart(Token currentToken, ParserLayerContext context)
        {
            // 普通分支：( pattern ) -> { body }
            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                pendingCaseStart = context.GetLocation();   // 分支 span 起点：(（M28）
                state = State.PatternStart;
                return ParserLayerResult.Continue.Instance;
            }

            // default 分支
            if (currentToken is WordToken wt && wt.Content == Keywords.DEFAULT)
            {
                if (targetNode.DefaultBody != null)
                {
                    context.RaiseError("switch 表达式只允许一个 default 分支");
                }
                state = State.DefaultArrowExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // 分支列表结束：表达式形式必须有 default（SYNTAX §7.2）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                if (targetNode.DefaultBody == null)
                {
                    context.RaiseError("switch 表达式必须包含 default 分支（SYNTAX.md §7.2）");
                }
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '(', 'default' or '}}' in switch case list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 分支体 } ：提交分支，回到 CaseStart 等待下一个分支
        private ParserLayerResult HandleCaseBodyCloseBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                // 分支完成：span 的 End 封到分支体最后一个 token
                // （当前 token 是 }，不属于分支内容）（M28）
                if (pendingCase!.Span is { } s)
                {
                    s.End = context.GetPreviousLocation().End;
                    pendingCase.Span = s;
                }
                targetNode.Cases.Add(pendingCase!);
                pendingCase = null;
                pendingCaseStart = null;
                state = State.CaseStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '}}' to close switch case body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }
    }
}
