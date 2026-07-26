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
    /// - 其他起点 → ExpressionParserLayer（表达式语句；后续跟 = 时转为赋值语句）
    /// - return/break/continue 为简短 keyword 语句，由本层子状态直接处理
    ///   （参照 ExpressionParserLayer 内联处理一元/二元运算符的先例）
    ///
    /// 状态流转：
    /// OpenBraceExpected → StatementDispatch
    ///   →（表达式/赋值）AfterExpression → [StatementEnd] → StatementDispatch
    ///   →（return）ReturnLabelOrValue → [ReturnLabel] → [StatementEnd] → StatementDispatch
    ///   →（break/continue）LoopControlLabel → [LoopControlLabelName] → StatementDispatch
    /// </summary>
    public class CodeBlockParserLayer : IParserLayer, IResultConsumer
    {
        private readonly CodeBlockASTNode targetNode;

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
            ThrowValue           // throw 已读：等待异常表达式
        }

        private State state = State.OpenBraceExpected;

        // 等待子 Layer 结果时的回填动作（委托前设置，OnChildResult 时消费）
        private Action<ASTNode?>? pendingResultHandler;

        // 构建中的语句暂存
        private ExpressionASTNode? pendingExpression = null;       // 表达式语句/赋值目标
        private ReturnStatementASTNode? pendingReturn = null;
        private LoopControlStatementASTNode? pendingLoopControl = null;
        private ThrowStatementASTNode? pendingThrow = null;

        public CodeBlockParserLayer(CodeBlockASTNode target)
        {
            targetNode = target;
        }

        // IResultConsumer：接收表达式/赋值值/return 值的解析结果
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
                default:
                    context.RaiseError($"Invalid CodeBlockParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
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
            return new ParserLayerResult.PopLayer(false);
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
                return new ParserLayerResult.PopLayer(false);
            }

            if (currentToken is WordToken wt)
            {
                // 变量声明
                if (wt.Content == Keywords.VAR || wt.Content == Keywords.CONST)
                {
                    var declNode = new VariableDeclarationASTNode(targetNode);
                    targetNode.Children.Add(declNode);
                    return new ParserLayerResult.PushLayer(
                        new VariableDeclarationParserLayer(declNode), true);
                }

                // if 语句（语句模式，else 可选）
                if (wt.Content == Keywords.IF)
                {
                    return new ParserLayerResult.PushLayer(
                        new IfStatementParserLayer(targetNode), true);
                }

                // 循环语句
                if (wt.Content == Keywords.FOR || wt.Content == Keywords.WHILE ||
                    wt.Content == Keywords.DO)
                {
                    return new ParserLayerResult.PushLayer(
                        new LoopParserLayer(targetNode), true);
                }

                // try-catch-finally 语句
                if (wt.Content == Keywords.TRY)
                {
                    return new ParserLayerResult.PushLayer(
                        new TryCatchFinallyParserLayer(targetNode), true);
                }

                // seq 块语句（含 volatile/using/named）
                if (wt.Content == Keywords.SEQ || wt.Content == Keywords.VOLATILE)
                {
                    return new ParserLayerResult.PushLayer(
                        new SeqBlockParserLayer(targetNode), true);
                }

                // return 语句（可选 @标签、可选值）
                if (wt.Content == Keywords.RETURN)
                {
                    pendingReturn = new ReturnStatementASTNode(targetNode);
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
                    state = State.LoopControlLabel;
                    return ParserLayerResult.Continue.Instance;
                }

                // throw 语句（必须跟异常表达式）
                if (wt.Content == Keywords.THROW)
                {
                    pendingThrow = new ThrowStatementASTNode(targetNode);
                    state = State.ThrowValue;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            // 其他起点：表达式语句（后续跟 = 时转为赋值语句）
            state = State.AfterExpression;
            pendingResultHandler = result => pendingExpression = (ExpressionASTNode)result!;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), true);
        }

        // 表达式已解析：= 转赋值，换行/} 按表达式语句收尾
        private ParserLayerResult HandleAfterExpression(Token currentToken, ParserLayerContext context)
        {
            // 赋值语句：target = value
            if (currentToken is NotationToken assign && assign.Content == "=")
            {
                var assignNode = new AssignStatementASTNode(targetNode)
                {
                    Target = pendingExpression!
                };
                targetNode.Children.Add(assignNode);
                pendingExpression = null;

                state = State.StatementEnd;
                pendingResultHandler = result => assignNode.Value = (ExpressionASTNode)result!;
                return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), false);
            }

            // 表达式语句：换行结束
            if (currentToken is LineBreakToken)
            {
                targetNode.Children.Add(pendingExpression!);
                pendingExpression = null;
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            // 表达式语句：} 结束（同时结束整个块）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                targetNode.Children.Add(pendingExpression!);
                pendingExpression = null;
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Unexpected token after expression statement: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 赋值值/return 值已解析：等待换行或 }
        private ParserLayerResult HandleStatementEnd(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected line break or '}}' after statement, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
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

        // return 的 @ 已读：等待标签名
        private ParserLayerResult HandleReturnLabel(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && IsIdentifierStart(wt.Content))
            {
                pendingReturn!.Label = wt.Content;
                state = State.ReturnValue;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label after 'return@', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
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
                CompleteReturn();
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            // 裸 return：} 结束（同时结束整个块）
            if (currentToken is NotationToken close && close.Content == "}")
            {
                CompleteReturn();
                return new ParserLayerResult.PopLayer(false);
            }

            // 带值 return：委托 ExpressionParserLayer
            // 注意：先把节点入列并置空 pendingReturn，handler 捕获局部变量
            var returnNode = pendingReturn!;
            targetNode.Children.Add(returnNode);
            pendingReturn = null;
            state = State.StatementEnd;
            pendingResultHandler = result => returnNode.Value = (ExpressionASTNode)result!;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), true);
        }

        private void CompleteReturn()
        {
            targetNode.Children.Add(pendingReturn!);
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
                CompleteLoopControl();
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken close && close.Content == "}")
            {
                CompleteLoopControl();
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected '@' or line break after break/continue, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // break/continue 的 @ 已读：等待标签名
        private ParserLayerResult HandleLoopControlLabelName(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && IsIdentifierStart(wt.Content))
            {
                pendingLoopControl!.Label = wt.Content;
                state = State.LoopControlEnd;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected label after 'break@'/'continue@', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 标识符首字符检查（标签不能是数字字面量；词法上数字也是 WordToken）
        private static bool IsIdentifierStart(string word)
        {
            return word.Length > 0 && (char.IsLetter(word[0]) || word[0] == '_');
        }

        // break/continue 标签已读：等待换行或 }
        private ParserLayerResult HandleLoopControlEnd(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is LineBreakToken)
            {
                CompleteLoopControl();
                state = State.StatementDispatch;
                return ParserLayerResult.Continue.Instance;
            }

            if (currentToken is NotationToken nt && nt.Content == "}")
            {
                CompleteLoopControl();
                return new ParserLayerResult.PopLayer(false);
            }

            context.RaiseError($"Expected line break or '}}' after break/continue label, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // throw 已读：解析异常表达式
        private ParserLayerResult HandleThrowValue(Token currentToken, ParserLayerContext context)
        {
            // 跳过换行
            if (currentToken is LineBreakToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            // throw 必须跟一个表达式
            // 先把节点入列并置空 pendingThrow，handler 捕获局部变量
            var throwNode = pendingThrow!;
            targetNode.Children.Add(throwNode);
            pendingThrow = null;
            state = State.StatementEnd;
            pendingResultHandler = result => throwNode.Exception = (ExpressionASTNode)result!;
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(targetNode), true);
        }

        private void CompleteLoopControl()
        {
            targetNode.Children.Add(pendingLoopControl!);
            pendingLoopControl = null;
        }
    }
}
