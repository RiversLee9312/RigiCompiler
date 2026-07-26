using System;

namespace LatteCompiler
{
    /// <summary>
    /// Try-Catch-Finally 语句解析器（roadmap #10，P2）
    ///
    /// 语法（SYNTAX.md §8）：
    /// try { ... } [catch (varName: Type) { ... }]* [finally(e) { ... }]
    ///
    /// - 至少要有一个 catch 或一个 finally
    /// - catch 可以有多个，按顺序检查类型匹配
    /// - 异常变量可以是具体名字或 _ 表示丢弃
    /// - finally 的参数 e 代表 try/catch 中抛出的异常，无异常时为 null
    ///
    /// 状态流转：
    /// TryKeyword → TryBlock（委托 CodeBlockParserLayer）
    ///   → CatchOrFinally（判断接下来是 catch/finally/结束）
    ///   → CatchOpenParen → CatchVariable → CatchColon → CatchType（委托 TypeReferenceParserLayer）
    ///   → CatchCloseParen → CatchBlock（委托 CodeBlockParserLayer）
    ///   → CatchOrFinally（继续）
    ///   → FinallyOpenParen → FinallyParameter → FinallyCloseParen
    ///   → FinallyBlock（委托 CodeBlockParserLayer）→ Completed
    /// </summary>
    public class TryCatchFinallyParserLayer : IParserLayer, IResultConsumer
    {
        private readonly ASTNode parentNode;
        private readonly TryCatchFinallyStatementASTNode tryNode;

        private enum State
        {
            TryKeyword,         // 等待 try 关键字
            TryBlock,           // 等待 try 代码块（委托中）
            CatchOrFinally,     // 检查是否有 catch/finally
            CatchOpenParen,     // 等待 catch 的 (
            CatchVariable,      // 等待异常变量名（可以是 _）
            CatchColon,         // 等待 :
            CatchType,          // 解析异常类型（委托中）
            CatchCloseParen,    // 等待 )
            CatchBlock,         // 解析 catch 代码块（委托中）
            FinallyOpenParen,   // 等待 finally 的 (
            FinallyParameter,   // 等待 finally 参数名
            FinallyCloseParen,  // 等待 )
            FinallyBlock,       // 解析 finally 代码块（委托中）
            Completed
        }

        private State state = State.TryKeyword;
        private Action<ASTNode?>? pendingResultHandler;
        private CatchClauseASTNode? currentCatch;

        public TryCatchFinallyParserLayer(ASTNode parent)
        {
            parentNode = parent;
            tryNode = new TryCatchFinallyStatementASTNode(parent);
        }

        public void OnChildResult(ASTNode? result, IParserLayer child)
        {
            var handler = pendingResultHandler;
            pendingResultHandler = null;
            handler?.Invoke(result);
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            switch (state)
            {
                case State.TryKeyword:
                    return HandleTryKeyword(currentToken, context);
                case State.TryBlock:
                    return HandleTryBlock(currentToken, context);
                case State.CatchOrFinally:
                    return HandleCatchOrFinally(currentToken, context);
                case State.CatchOpenParen:
                    return HandleCatchOpenParen(currentToken, context);
                case State.CatchVariable:
                    return HandleCatchVariable(currentToken, context);
                case State.CatchColon:
                    return HandleCatchColon(currentToken, context);
                case State.CatchType:
                    return HandleCatchType(currentToken, context);
                case State.CatchCloseParen:
                    return HandleCatchCloseParen(currentToken, context);
                case State.CatchBlock:
                    return HandleCatchBlock(currentToken, context);
                case State.FinallyOpenParen:
                    return HandleFinallyOpenParen(currentToken, context);
                case State.FinallyParameter:
                    return HandleFinallyParameter(currentToken, context);
                case State.FinallyCloseParen:
                    return HandleFinallyCloseParen(currentToken, context);
                case State.FinallyBlock:
                    return HandleFinallyBlock(currentToken, context);
                case State.Completed:
                    return HandleCompleted(currentToken, context);
                default:
                    context.RaiseError($"Invalid TryCatchFinallyParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        private ParserLayerResult HandleTryKeyword(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.TRY)
            {
                state = State.TryBlock;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected 'try' keyword, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleTryBlock(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 委托给 CodeBlockParserLayer 解析 try 块
            state = State.CatchOrFinally;
            return new ParserLayerResult.PushLayer(
                new CodeBlockParserLayer(tryNode.TryBlock), true);
        }

        private ParserLayerResult HandleCatchOrFinally(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                // catch 子句
                if (wt.Content == Keywords.CATCH)
                {
                    currentCatch = new CatchClauseASTNode(tryNode);
                    state = State.CatchOpenParen;
                    return ParserLayerResult.Continue.Instance;
                }

                // finally 子句
                if (wt.Content == Keywords.FINALLY)
                {
                    state = State.FinallyOpenParen;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            // 既没有 catch 也没有 finally
            if (tryNode.CatchClauses.Count == 0 && tryNode.FinallyBlock == null)
            {
                context.RaiseError("try 语句必须至少有一个 catch 或一个 finally 子句");
                return new ParserLayerResult.PopLayer(false);
            }

            // 结束
            state = State.Completed;
            return HandleCompleted(currentToken, context);
        }

        private ParserLayerResult HandleCatchOpenParen(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                state = State.CatchVariable;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '(' after 'catch', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleCatchVariable(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                // _ 表示丢弃异常变量
                if (wt.Content == "_")
                {
                    currentCatch!.VariableName = null;
                }
                else
                {
                    currentCatch!.VariableName = wt.Content;
                }

                state = State.CatchColon;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected variable name or '_' in catch clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleCatchColon(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == ":")
            {
                state = State.CatchType;
                // 创建类型引用节点并关联到 catch 子句
                currentCatch!.ExceptionType = new TypeReferenceASTNode(currentCatch);
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(currentCatch!.ExceptionType), false);
            }

            context.RaiseError($"Expected ':' after catch variable, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleCatchType(Token currentToken, ParserLayerContext context)
        {
            // 类型已由 TypeReferenceParserLayer 解析，现在等待 )
            state = State.CatchCloseParen;
            return ParseToken(currentToken, context);
        }

        private ParserLayerResult HandleCatchCloseParen(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                state = State.CatchBlock;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ')' after catch type, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleCatchBlock(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 委托给 CodeBlockParserLayer 解析 catch 块
            tryNode.CatchClauses.Add(currentCatch!);
            currentCatch = null;
            state = State.CatchOrFinally;
            return new ParserLayerResult.PushLayer(
                new CodeBlockParserLayer(tryNode.CatchClauses[tryNode.CatchClauses.Count - 1].Body), true);
        }

        private ParserLayerResult HandleFinallyOpenParen(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                state = State.FinallyParameter;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '(' after 'finally', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleFinallyParameter(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is WordToken wt)
            {
                tryNode.FinallyParameter = wt.Content;
                state = State.FinallyCloseParen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected parameter name in finally clause, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleFinallyCloseParen(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == ")")
            {
                tryNode.FinallyBlock = new CodeBlockASTNode(tryNode);
                state = State.FinallyBlock;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected ')' after finally parameter, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        private ParserLayerResult HandleFinallyBlock(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 委托给 CodeBlockParserLayer 解析 finally 块
            state = State.Completed;
            return new ParserLayerResult.PushLayer(
                new CodeBlockParserLayer(tryNode.FinallyBlock!), true);
        }

        private ParserLayerResult HandleCompleted(Token currentToken, ParserLayerContext context)
        {
            // 将完整的 try 节点添加到父节点
            if (parentNode is CodeBlockASTNode codeBlock)
            {
                codeBlock.Children.Add(tryNode);
            }
            else
            {
                context.RaiseError("try 语句只能出现在代码块中");
            }

            return new ParserLayerResult.PopLayer(true);
        }
    }
}
