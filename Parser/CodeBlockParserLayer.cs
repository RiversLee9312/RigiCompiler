using System;

namespace LatteCompiler
{
    /// <summary>
    /// 代码块解析器（roadmap #6，P2 语句系统核心）
    ///
    /// 解析 { ... } 代码块：消费 { ，循环识别并分发语句，直到 } 结束。
    /// 语句以换行结束；} 也可终止块内最后一条语句。
    ///
    /// 语句分发（Delegate, don't implement）：
    /// - var/const → VariableDeclarationParserLayer
    /// - if → IfStatementParserLayer（语句模式）
    /// - for/while/do → LoopParserLayer
    /// - 其他起点 → ExpressionParserLayer（表达式开头的语句，
    ///   由 ExpressionStatementASTNode 统一承载：纯表达式语句或赋值语句）
    /// - return/break/continue 为简短 keyword 语句，由本层子状态直接处理
    ///   （参照 ExpressionParserLayer 内联处理一元/二元运算符的先例）
    ///
    /// 施工协议（大扫除后）：
    /// - 语句节点直接挂入 targetBlock.Statements；
    /// - 表达式开头的语句先创建 ExpressionStatementASTNode（Parent = 本块），
    ///   表达式层直接向其 Expression Root 附加；遇 = 时再建 AssignValue Root
    ///   （Parent = 语句节点）解析右侧——两个 Root 槽创建时归属即定，
    ///   无节点搬家、无 Parent 重挂。
    ///
    /// 状态流转：
    /// OpenBraceExpected → StatementDispatch
    ///   →（表达式/赋值）AfterExpression → [StatementEnd] → StatementDispatch
    ///   →（return）ReturnLabelOrValue → [ReturnLabel] → [StatementEnd] → StatementDispatch
    /// </summary>
    public class CodeBlockParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly CodeBlockASTNode targetNode;

        // 层弹出时回填代码块节点的源码范围（M28）
        public void ReceiveSpan(CharRange span) => targetNode.Span ??= span;

        // 语句 span 施工（M28）：创建时记 Start（当前 token），完成时封 End
        // 到最近被消费的 token（当前 token 是换行/} 等终止符，不属于语句）
        private static void StartStatementSpan(ASTNode node, ParserLayerContext context)
        {
            var loc = context.GetLocation();
            node.Span = new CharRange { Start = loc.Start, End = loc.End, sourceName = loc.sourceName };
        }

        private static void SealStatementSpan(ASTNode node, ParserLayerContext context)
        {
            if (node.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                node.Span = s;
            }
        }

        private enum State
        {
            OpenBraceExpected,   // 等待 {
            StatementDispatch,   // 语句分发起点
            AfterExpression,     // 表达式已解析：= 转赋值，换行/} 按表达式语句收尾
            StatementEnd,        // 赋值值/return 值已解析，等待换行或 }
            ReturnLabelOrValue,  // return 已读：@标签、值表达式或裸 return
            ReturnLabel,         // return 的 @ 已读：等待标签名
            ReturnValue,         // return@标签 已读：可选值表达式
            LoopControlLabel,    // break/continue 已读：可选 @标签
            LoopControlLabelName,// break/continue 的 @ 已读：等待标签名
            LoopControlEnd,      // break/continue 标签已读：等待换行或 }
            ThrowValue,          // throw 已读：等待异常表达式
            YieldValue           // yield 已读：可选 alarm 表达式或直接结束
        }

        private State state = State.OpenBraceExpected;

        // 构建中的语句暂存
        private ExpressionStatementASTNode? pendingExpressionStatement = null;  // 表达式语句/赋值语句（创建时 Parent 已定为块）
        private ReturnStatementASTNode? pendingReturn = null;
        private LoopControlStatementASTNode? pendingLoopControl = null;
        private ThrowStatementASTNode? pendingThrow = null;
        private YieldStatementASTNode? pendingYield = null;
        // 进入 StatementEnd 状态的语句节点（赋值/带值 return/throw/带 alarm yield）：
        // 值表达式解析完成后在 HandleStatementEnd 封口 span（M28）
        private ASTNode? pendingStatementEnd = null;

        public CodeBlockParserLayer(CodeBlockASTNode target)
        {
            targetNode = target;
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：代码块必须由 } 闭合，任何状态下收到 EOF 都是不完整结构
            if (currentToken is EndOfFileToken)
            {
                context.RaiseError("Unexpected end of file");
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            switch (state)
            {
                case State.OpenBraceExpected:
                    return HandleOpenBraceExpected(currentToken, context);
                case State.StatementDispatch:
                    return HandleStatementDispatch(currentToken, context);
                case State.AfterExpression:
                    return HandleAfterExpression(currentToken, context);
                case State.StatementEnd:
                    return HandleStatementEnd(currentToken, context);
                case State.ReturnLabelOrValue:
                    return HandleReturnLabelOrValue(currentToken, context);
                case State.ReturnLabel:
                    return HandleReturnLabel(currentToken, context);
                case State.ReturnValue:
                    return HandleReturnValue(currentToken, context);
                case State.LoopControlLabel:
                    return HandleLoopControlLabel(currentToken, context);
                case State.LoopControlLabelName:
                    return HandleLoopControlLabelName(currentToken, context);
                case State.LoopControlEnd:
                    return HandleLoopControlEnd(currentToken, context);
                case State.ThrowValue:
                    return HandleThrowValue(currentToken, context);
                case State.YieldValue:
                    return HandleYieldValue(currentToken, context);
                default:
                    context.RaiseError($"Invalid CodeBlockParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // 等待 {
        private ParserLayerResult HandleOpenBraceExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "{")
            {
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '{{' to start code block, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 语句分发起点：识别语句类型并委托
        private ParserLayerResult HandleStatementDispatch(Token currentToken, ParserLayerContext context)
        {
            // 语句间换行：跳过
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // 块结束
            if (currentToken is NotationToken close && close.Content == "}")
            {
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 注解 / wrapper 应用（SYNTAX §14.5）：@ 起始的声明走统一声明层
            // （与 RootParserLayer 的既有约定一致；注解后随 var/const/func 等声明）
            if (currentToken is NotationToken at && at.Content == "@")
            {
                return new ParserLayerResult.PushLayer(
                    new DeclarationParserLayer(targetNode, targetNode.Statements), TokenDisposition.Replay);
            }

            if (currentToken is WordToken wt)
            {
                // 变量声明
                if (wt.Content == Keywords.VAR || wt.Content == Keywords.CONST)
                {
                    var declNode = new VariableDeclarationASTNode(targetNode);
                    targetNode.Statements.Add(declNode);
                    return new ParserLayerResult.PushLayer(
                        new VariableDeclarationParserLayer(declNode), TokenDisposition.Replay);
                }

                // if 语句（语句模式，else 可选）
                if (wt.Content == Keywords.IF)
                {
                    return new ParserLayerResult.PushLayer(
                        new IfStatementParserLayer(targetNode), TokenDisposition.Replay);
                }

                // 循环语句
                if (wt.Content == Keywords.FOR || wt.Content == Keywords.WHILE ||
                    wt.Content == Keywords.DO)
                {
                    return new ParserLayerResult.PushLayer(
                        new LoopParserLayer(targetNode), TokenDisposition.Replay);
                }

                // try-catch-finally 语句
                if (wt.Content == Keywords.TRY)
                {
                    var tryNode = new TryCatchFinallyStatementASTNode(targetNode);
                    targetNode.Statements.Add(tryNode);
                    return new ParserLayerResult.PushLayer(
                        new TryCatchFinallyParserLayer(tryNode), TokenDisposition.Replay);
                }

                // seq 块语句（含 volatile/using/named）
                if (wt.Content == Keywords.SEQ || wt.Content == Keywords.VOLATILE)
                {
                    var seqNode = new SeqBlockExpressionASTNode(targetNode);
                    targetNode.Statements.Add(seqNode);
                    return new ParserLayerResult.PushLayer(
                        new SeqBlockParserLayer(seqNode), TokenDisposition.Replay);
                }

                // return 语句（可选 @标签、可选值）
                if (wt.Content == Keywords.RETURN)
                {
                    pendingReturn = new ReturnStatementASTNode(targetNode);
                    StartStatementSpan(pendingReturn, context);
                    state = State.ReturnLabelOrValue;
                    return ParserLayerResult.Continue.Instance;
                }

                // break / continue 语句（可选 @标签）
                if (wt.Content == Keywords.BREAK || wt.Content == Keywords.CONTINUE)
                {
                    pendingLoopControl = new LoopControlStatementASTNode(targetNode)
                    {
                        IsBreak = wt.Content == Keywords.BREAK
                    };
                    StartStatementSpan(pendingLoopControl, context);
                    state = State.LoopControlLabel;
                    return ParserLayerResult.Continue.Instance;
                }

                // throw 语句（必须跟异常表达式）
                if (wt.Content == Keywords.THROW)
                {
                    pendingThrow = new ThrowStatementASTNode(targetNode);
                    StartStatementSpan(pendingThrow, context);
                    state = State.ThrowValue;
                    return ParserLayerResult.Continue.Instance;
                }

                // yield 语句（可选 alarm 表达式）
                if (wt.Content == Keywords.YIELD)
                {
                    pendingYield = new YieldStatementASTNode(targetNode);
                    StartStatementSpan(pendingYield, context);
                    state = State.YieldValue;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            // 其他起点：表达式开头的语句（纯表达式语句或赋值语句，统一容器承载）。
            // 语句节点创建时 Parent 即定为本块；表达式层直接附加到其 Expression Root。
            var expressionStatement = new ExpressionStatementASTNode(targetNode);
            StartStatementSpan(expressionStatement, context);
            pendingExpressionStatement = expressionStatement;
            state = State.AfterExpression;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(expressionStatement.Expression), TokenDisposition.Replay);
        }

        // 表达式已解析：= 转赋值，换行/} 按表达式语句收尾
        private ParserLayerResult HandleAfterExpression(Token currentToken, ParserLayerContext context)
        {
            // 赋值语句：target = value
            // 右侧解析到语句节点的 AssignValue Root（创建时 Parent 即定为语句节点）
            if (currentToken is NotationToken assign && assign.Content == "=")
            {
                var stmt = pendingExpressionStatement!;
                stmt.AssignValue = new ExpressionRootASTNode(stmt);
                targetNode.Statements.Add(stmt);
                pendingExpressionStatement = null;

                // 赋值语句待值表达式完成后封口（HandleStatementEnd）
                pendingStatementEnd = stmt;
                state = State.StatementEnd;
                return new ParserLayerResult.PushLayer(
                    new ExpressionParserLayer(stmt.AssignValue), TokenDisposition.Consume);
            }

            // 表达式语句：换行结束
            if (currentToken is LineBreakToken)
            {
                SealStatementSpan(pendingExpressionStatement!, context);
                targetNode.Statements.Add(pendingExpressionStatement!);
                pendingExpressionStatement = null;
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            // 表达式语句：} 结束（同时结束整个块）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                SealStatementSpan(pendingExpressionStatement!, context);
                targetNode.Statements.Add(pendingExpressionStatement!);
                pendingExpressionStatement = null;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Unexpected token after expression statement: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 赋值值/return 值已解析：等待换行或 }
        private ParserLayerResult HandleStatementEnd(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                SealPendingStatementEnd(context);
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                SealPendingStatementEnd(context);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected line break or '}}' after statement, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // StatementEnd 状态下的语句封口（赋值/带值 return/throw/带 alarm yield）
        private void SealPendingStatementEnd(ParserLayerContext context)
        {
            if (pendingStatementEnd != null)
            {
                SealStatementSpan(pendingStatementEnd, context);
                pendingStatementEnd = null;
            }
        }

        // return 已读：@标签、值表达式或裸 return
        private ParserLayerResult HandleReturnLabelOrValue(Token currentToken, ParserLayerContext context)
        {
            // @标签
            if (currentToken is NotationToken at && at.Content == "@")
            {
                state = State.ReturnLabel;
                return ParserLayerResult.Continue.Instance;
            }

            return DelegateReturnValue(currentToken, context);
        }

        // return 的 @ 已读：等待标签名（只查首字符：return@seq 等保留字标签合法）
        private ParserLayerResult HandleReturnLabel(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && Keywords.IsIdentifierStart(wt.Content))
            {
                pendingReturn!.Label = wt.Content;
                state = State.ReturnValue;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label after 'return@', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // return@标签 已读：可选值表达式
        private ParserLayerResult HandleReturnValue(Token currentToken, ParserLayerContext context)
        {
            return DelegateReturnValue(currentToken, context);
        }

        // return 收尾：裸 return（换行/}）或委托值表达式
        private ParserLayerResult DelegateReturnValue(Token currentToken, ParserLayerContext context)
        {
            // 裸 return：换行结束
            if (currentToken is LineBreakToken)
            {
                CompleteReturn(context);
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            // 裸 return：} 结束（同时结束整个块）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                CompleteReturn(context);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 带值 return：委托 ExpressionParserLayer
            // 注意：先把节点入列并置空 pendingReturn，Value Root 由表达式层直接填充
            var returnNode = pendingReturn!;
            targetNode.Statements.Add(returnNode);
            pendingReturn = null;
            returnNode.Value = new ExpressionRootASTNode(returnNode);
            pendingStatementEnd = returnNode;
            state = State.StatementEnd;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(returnNode.Value), TokenDisposition.Replay);
        }

        private void CompleteReturn(ParserLayerContext context)
        {
            SealStatementSpan(pendingReturn!, context);
            targetNode.Statements.Add(pendingReturn!);
            pendingReturn = null;
        }

        // break/continue 已读：可选 @标签；换行/} 收尾
        private ParserLayerResult HandleLoopControlLabel(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken at && at.Content == "@")
            {
                state = State.LoopControlLabelName;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is LineBreakToken)
            {
                CompleteLoopControl(context);
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken close && close.Content == "}")
            {
                CompleteLoopControl(context);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '@' or line break after break/continue, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // break/continue 的 @ 已读：等待标签名
        private ParserLayerResult HandleLoopControlLabelName(Token currentToken, ParserLayerContext context)
        {
            // 标签只查首字符（M31 统一走 Keywords.IsIdentifierStart）：
            // return@seq 等保留字标签合法，不查保留字
            if (currentToken is WordToken wt && Keywords.IsIdentifierStart(wt.Content))
            {
                pendingLoopControl!.Label = wt.Content;
                state = State.LoopControlEnd;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label after 'break@'/'continue@', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // break/continue 标签已读：等待换行或 }
        private ParserLayerResult HandleLoopControlEnd(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                CompleteLoopControl(context);
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                CompleteLoopControl(context);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            context.RaiseError($"Expected line break or '}}' after break/continue label, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // throw 已读：解析异常表达式
        private ParserLayerResult HandleThrowValue(Token currentToken, ParserLayerContext context)
        {
            // throw 后换行是错误（M31，SYNTAX §1.1：续行只来自未闭合的符号结构，
            // throw 不是括号结构；与裸 return/裸 yield 遇换行即收尾的行为一致）
            if (currentToken is LineBreakToken)
            {
                context.RaiseError("Expected exception expression after 'throw'");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // throw 必须跟一个表达式
            // 先把节点入列并置空 pendingThrow，Exception Root 由表达式层直接填充
            var throwNode = pendingThrow!;
            targetNode.Statements.Add(throwNode);
            pendingThrow = null;
            pendingStatementEnd = throwNode;
            state = State.StatementEnd;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(throwNode.Exception), TokenDisposition.Replay);
        }

        // yield 已读：可选 alarm 表达式或直接结束
        private ParserLayerResult HandleYieldValue(Token currentToken, ParserLayerContext context)
        {
            // 换行：裸 yield
            if (currentToken is LineBreakToken)
            {
                SealStatementSpan(pendingYield!, context);
                targetNode.Statements.Add(pendingYield!);
                pendingYield = null;
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            // }：裸 yield 后直接结束块
            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                SealStatementSpan(pendingYield!, context);
                targetNode.Statements.Add(pendingYield!);
                pendingYield = null;
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 其他：带 alarm 表达式
            // 先把节点入列并置空 pendingYield，Alarm Root 由表达式层直接填充
            var yieldNode = pendingYield!;
            targetNode.Statements.Add(yieldNode);
            pendingYield = null;
            yieldNode.Alarm = new ExpressionRootASTNode(yieldNode);
            pendingStatementEnd = yieldNode;
            state = State.StatementEnd;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(yieldNode.Alarm), TokenDisposition.Replay);
        }

        private void CompleteLoopControl(ParserLayerContext context)
        {
            SealStatementSpan(pendingLoopControl!, context);
            targetNode.Statements.Add(pendingLoopControl!);
            pendingLoopControl = null;
        }
    }
}
