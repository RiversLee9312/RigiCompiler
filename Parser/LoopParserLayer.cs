using System;

namespace LatteCompiler
{
    /// <summary>
    /// 循环解析器（roadmap #9，SYNTAX.md §7.3/§7.4）
    ///
    /// 支持全部循环形态：
    /// - for-each：for (item in collection) { ... }
    /// - 范围循环：for (i in 0 to 10) { ... }（起点在 Iterable，终点解析到 RangeTo Root）
    /// - while：while (condition) { ... }
    /// - do-while：do { ... } while (condition)
    /// - named 标签：for/while/do 均可带 named 标签，配合 break@标签 / continue@标签
    ///
    /// 状态流转：
    /// KeywordExpected
    ///   → for：ForOpenParenExpected → LoopVariableExpected → InKeywordExpected
    ///     → IterableStart → AfterIterable → [RangeEndStart] → CloseParenExpected
    ///     → NamedCheck → [LabelNameExpected →] BodyExpected → BodyDone → 弹出
    ///   → while：WhileOpenParenExpected → ConditionStart → WhileCloseParenExpected
    ///     → NamedCheck → ... → BodyDone → 弹出
    ///   → do：DoNamedCheck → [DoLabelNameExpected →] DoBodyExpected
    ///     → DoWhileKeywordExpected → DoWhileOpenParenExpected → DoConditionStart
    ///     → DoWhileCloseParenExpected → Done → 弹出
    ///
    /// 委托说明：迭代/范围/条件表达式由 ExpressionParserLayer 直接附加到目标节点的
    /// 各 ExpressionRootASTNode（大扫除后的施工协议，无回传），
    /// 循环体委托 CodeBlockParserLayer（原地写入 node.Body）。
    /// </summary>
    public class LoopParserLayer : IParserLayer
    {
        private readonly LoopStatementASTNode targetNode;

        private enum State
        {
            KeywordExpected,          // 等待 for/while/do
            // for
            ForOpenParenExpected,     // 等待 (
            LoopVariableExpected,     // 等待循环变量名
            InKeywordExpected,        // 等待 in
            IterableStart,            // in 已读，等待迭代表达式开始
            AfterIterable,            // 迭代表达式已解析：to（范围）或 )
            RangeEndStart,            // to 已读，等待范围终点表达式开始
            CloseParenExpected,       // for 的 ) 
            // while
            WhileOpenParenExpected,   // 等待 (
            ConditionStart,           // ( 已读，等待条件表达式开始
            WhileCloseParenExpected,  // while 的 )
            // 公共：标签与循环体
            NamedCheck,               // named 标签或 {
            LabelNameExpected,        // named 已读，等待标签名
            BodyExpected,             // 等待 {
            BodyDone,                 // 循环体已解析：弹出（保留 token）
            // do-while
            DoNamedCheck,             // named 标签或 {
            DoLabelNameExpected,      // named 已读，等待标签名
            DoBodyExpected,           // 等待 {
            DoWhileKeywordExpected,   // 体已结束，等待 while
            DoWhileOpenParenExpected, // 等待 (
            DoConditionStart,         // ( 已读，等待条件表达式开始
            DoWhileCloseParenExpected,// do-while 的 )
            Done                      // 弹出（保留 token）
        }

        private State state = State.KeywordExpected;

        public LoopParserLayer(CodeBlockASTNode parentBlock)
        {
            targetNode = new LoopStatementASTNode(parentBlock);
            parentBlock.Children.Add(targetNode);
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：循环结构（子句/循环体）未完整时收到 EOF 均为不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 本层只处于结构性等待状态（括号/关键字/花括号），允许跨行；
            // 表达式内部的换行终止仍由 ExpressionParserLayer 负责
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            switch (state)
            {
                case State.KeywordExpected:
                    return HandleKeywordExpected(currentToken, context);
                case State.ForOpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.LoopVariableExpected,
                        "Expected '(' after for");
                case State.LoopVariableExpected:
                    return HandleLoopVariableExpected(currentToken, context);
                case State.InKeywordExpected:
                    return HandleInKeywordExpected(currentToken, context);
                case State.IterableStart:
                    // 迭代起点表达式先解析到 Iterable Root（此时不知道是否范围循环）
                    targetNode.Iterable = new ExpressionRootASTNode(targetNode);
                    return DelegateExpression(State.AfterIterable, targetNode.Iterable);
                case State.AfterIterable:
                    return HandleAfterIterable(currentToken, context);
                case State.RangeEndStart:
                    return HandleRangeEndStart(currentToken, context);
                case State.CloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.NamedCheck,
                        "Expected ')' after for clause");
                case State.WhileOpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.ConditionStart,
                        "Expected '(' after while");
                case State.ConditionStart:
                    targetNode.Condition = new ExpressionRootASTNode(targetNode);
                    return DelegateExpression(State.WhileCloseParenExpected, targetNode.Condition);
                case State.WhileCloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.NamedCheck,
                        "Expected ')' after while condition");
                case State.NamedCheck:
                    return HandleNamedCheck(currentToken, context);
                case State.LabelNameExpected:
                    return HandleLabelNameExpected(currentToken, context);
                case State.BodyExpected:
                    return HandleBodyExpected(currentToken, context);
                case State.BodyDone:
                    // 循环体已结束：保留 token 交还给代码块分发
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                case State.DoNamedCheck:
                    return HandleDoNamedCheck(currentToken, context);
                case State.DoLabelNameExpected:
                    return HandleDoLabelNameExpected(currentToken, context);
                case State.DoBodyExpected:
                    return HandleDoBodyExpected(currentToken, context);
                case State.DoWhileKeywordExpected:
                    return HandleDoWhileKeywordExpected(currentToken, context);
                case State.DoWhileOpenParenExpected:
                    return ExpectNotation(currentToken, context, "(", State.DoConditionStart,
                        "Expected '(' after while");
                case State.DoConditionStart:
                    targetNode.Condition = new ExpressionRootASTNode(targetNode);
                    return DelegateExpression(State.DoWhileCloseParenExpected, targetNode.Condition);
                case State.DoWhileCloseParenExpected:
                    return ExpectNotation(currentToken, context, ")", State.Done,
                        "Expected ')' after do-while condition");
                case State.Done:
                    return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
                default:
                    context.RaiseError($"Invalid LoopParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 等待 for/while/do 关键字，确定循环种类
        private ParserLayerResult HandleKeywordExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                if (wt.Content == Keywords.FOR)
                {
                    targetNode.Kind = LoopKind.For;
                    state = State.ForOpenParenExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                if (wt.Content == Keywords.WHILE)
                {
                    targetNode.Kind = LoopKind.While;
                    state = State.WhileOpenParenExpected;
                    return ParserLayerResult.Continue.Instance;
                }
                if (wt.Content == Keywords.DO)
                {
                    targetNode.Kind = LoopKind.DoWhile;
                    state = State.DoNamedCheck;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected 'for', 'while' or 'do', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待循环变量名
        private ParserLayerResult HandleLoopVariableExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt)
            {
                targetNode.VariableName = wt.Content;
                state = State.InKeywordExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected loop variable name, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待 in 关键字
        private ParserLayerResult HandleInKeywordExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.IN)
            {
                state = State.IterableStart;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'in' after loop variable, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 迭代表达式已解析：to（范围循环）或 )（for-each）
        private ParserLayerResult HandleAfterIterable(Token currentToken, ParserLayerContext context)
        {
            // 范围循环：0 to 10
            if (currentToken is WordToken wt && wt.Content == Keywords.TO)
            {
                state = State.RangeEndStart;
                return ParserLayerResult.Continue.Instance;
            }

            // for-each：) 结束 for 子句
            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                state = State.NamedCheck;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'to' or ')' after iterable, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // to 已读：起点表达式留在 Iterable 不动；为范围终点新建 RangeTo Root
        // （Parent 创建即定为循环节点），终点表达式直接解析到该 Root——无节点搬家
        private ParserLayerResult HandleRangeEndStart(Token currentToken, ParserLayerContext context)
        {
            targetNode.RangeTo = new ExpressionRootASTNode(targetNode);
            return DelegateExpression(State.CloseParenExpected, targetNode.RangeTo);
        }

        // named 标签或循环体 {
        private ParserLayerResult HandleNamedCheck(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                state = State.LabelNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.BodyDone;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(targetNode.Body), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected 'named' or '{{' after loop clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // named 已读：等待标签名
        private ParserLayerResult HandleLabelNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && IsIdentifierStart(wt.Content))
            {
                targetNode.Label = wt.Content;
                state = State.BodyExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label name after 'named', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 等待循环体 {
        private ParserLayerResult HandleBodyExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.BodyDone;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(targetNode.Body), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' to start loop body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // do：named 标签或体 {
        private ParserLayerResult HandleDoNamedCheck(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.NAMED)
            {
                state = State.DoLabelNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.DoWhileKeywordExpected;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(targetNode.Body), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected 'named' or '{{' after do, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // do 的 named 已读：等待标签名
        private ParserLayerResult HandleDoLabelNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && IsIdentifierStart(wt.Content))
            {
                targetNode.Label = wt.Content;
                state = State.DoBodyExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label name after 'named', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 标识符首字符检查（标签不能是数字字面量；词法上数字也是 WordToken）
        private static bool IsIdentifierStart(string word)
        {
            return word.Length > 0 && (char.IsLetter(word[0]) || word[0] == '_');
        }

        // do：等待体 {
        private ParserLayerResult HandleDoBodyExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.DoWhileKeywordExpected;
                return new ParserLayerResult.PushLayer(
                    new CodeBlockParserLayer(targetNode.Body), TokenDisposition.Replay);
            }

            context.RaiseError($"Expected '{{' to start do body, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // do 体已结束：等待 while
        private ParserLayerResult HandleDoWhileKeywordExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.WHILE)
            {
                state = State.DoWhileOpenParenExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'while' after do block, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
        // 表达式直接附加到目标 Root
        private ParserLayerResult DelegateExpression(State nextState, ExpressionRootASTNode expressionTarget)
        {
            state = nextState;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(expressionTarget), TokenDisposition.Replay);
        }
    }
}
