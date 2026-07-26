using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    /// <summary>
    /// 表达式解析器层 - 通用框架
    ///
    /// 职责：
    /// 1. 识别表达式的起点类型（字面量、new、符号、括号等）
    /// 2. 委托给专门的 Layer 进行具体解析
    /// 3. 处理运算符（一元、二元）
    /// 4. 管理表达式的组合
    ///
    /// 设计原则：
    /// - 不直接实现具体表达式的解析逻辑
    /// - 通过子 Layer 模块化处理各种表达式类型
    /// - 只负责框架性的工作（委托、运算符、组合）
    ///
    /// 结果传递：
    /// - 实现 IResultProducer：解析完成后通过 GetResult() 把表达式交给父层
    /// - 实现 IResultConsumer：通过 pendingResultHandler 接收子 Layer 的结果
    ///
    /// 重要：Latte 没有运算符优先级！
    /// 所有运算必须用括号明确指定，如 (1 + 2) * 3。
    /// 因此每层表达式最多消费一个二元运算符；右操作数与一元操作数
    /// 通过 allowBinaryOperator / allowPrefixUnary 禁止继续吞并运算符。
    /// </summary>
    public class ExpressionParserLayer : IParserLayer, IResultProducer, IResultConsumer
    {
        private readonly ASTNode parentNode;
        private ExpressionASTNode? currentExpression;

        // 等待子 Layer 结果时的回填动作（委托前设置，OnChildResult 时消费）
        private Action<ASTNode?>? pendingResultHandler;

        // 本层创建了一个括号分组，正在等待其右括号 )
        private bool expectClosingParen = false;

        // 运算符限制（Latte 无运算符优先级）：
        // 右操作数层与一元表达式结果禁止再直接消费二元运算符
        private bool allowBinaryOperator = true;
        // 一元操作数层禁止连续的一元运算符（not not x 必须写成 not (not x)）
        private bool allowPrefixUnary = true;

        private enum State
        {
            Initial,              // 等待主表达式
            PrimaryParsed,        // 主表达式已解析
            OperatorSeen,         // 看到运算符
            MemberNameExpected,   // . 或 ?. 之后等待成员名
            SafeDotExpected,      // ? 之后等待 .
            GenericAngleExpected, // \ 之后等待 <（泛型实参列表）
            GenericArgParsed,     // 一个泛型实参已解析，等待 , 或 >
            AsyncSeen,            // async 已读，等待 func（async lambda）
            TypeOperatorSeen,     // 类型操作符 is/as/supers/with 已读，等待右侧类型
            Completed             // 完成
        }

        private State state = State.Initial;
        private string? pendingOperator = null;

        // 类型操作（is/as/as?/supers/with）暂存：等待右侧类型引用解析
        private string? pendingTypeOperator = null;
        private CastExpressionASTNode? pendingCastNode = null;
        private TypeCheckExpressionASTNode? pendingCheckNode = null;
        private bool safeCastMarkConsumed = false;   // as? 的 ? 是否已消费

        // 后缀链状态
        private bool pendingSafeAccess = false;
        private readonly List<TypeReferenceASTNode> pendingGenericArgs = new();

        public ExpressionParserLayer(ASTNode parent, ExpressionASTNode? initialPrimary = null)
        {
            parentNode = parent;

            // 调用方已解析出主表达式时（如具名实参判别后退化为位置实参），
            // 直接从 PrimaryParsed 继续（可能跟随后缀链或运算符）
            if (initialPrimary != null)
            {
                currentExpression = initialPrimary;
                state = State.PrimaryParsed;
            }
        }

        // IResultProducer：返回解析出的表达式
        public ASTNode? GetResult() => currentExpression;

        // IResultConsumer：子 Layer 弹出时接收其结果
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
                case State.Initial:
                    return HandleInitial(currentToken, context);

                case State.PrimaryParsed:
                    return HandlePrimaryParsed(currentToken, context);

                case State.OperatorSeen:
                    return HandleOperatorSeen(currentToken, context);

                case State.MemberNameExpected:
                    return HandleMemberNameExpected(currentToken, context);

                case State.SafeDotExpected:
                    return HandleSafeDotExpected(currentToken, context);

                case State.GenericAngleExpected:
                    return HandleGenericAngleExpected(currentToken, context);

                case State.GenericArgParsed:
                    return HandleGenericArgParsed(currentToken, context);

                case State.AsyncSeen:
                    return HandleAsyncSeen(currentToken, context);

                case State.TypeOperatorSeen:
                    return HandleTypeOperatorSeen(currentToken, context);

                case State.Completed:
                    return HandleCompleted(currentToken, context);

                default:
                    context.RaiseError($"Invalid ExpressionParserLayer state: {state}");
                    return new ParserLayerResult.PopLayer(false);
            }
        }

        // 初始状态：识别表达式类型并委托
        private ParserLayerResult HandleInitial(Token currentToken, ParserLayerContext context)
        {
            // 1. 字面量 - 委托给 LiteralParserLayer
            if (IsLiteralStart(currentToken))
            {
                return DelegateLiteralParsing(currentToken, context);
            }

            // 2. 括号分组 - 递归解析
            if (currentToken is NotationToken nt && nt.Content == "(")
            {
                return DelegateGroupParsing(context);
            }

            // 3. new 表达式 - 委托给专门的 Layer
            if (currentToken is WordToken wt && wt.Content == Keywords.NEW)
            {
                return DelegateNewParsing(context);
            }

            // 3.1 关键字起始的结构化表达式（SYNTAX §5/§7/§3.7/§6）：
            // lambda、async lambda、if、switch、typeOf、seq
            if (currentToken is WordToken kw)
            {
                switch (kw.Content)
                {
                    case Keywords.FUNC:
                        return DelegateLambdaParsing(false);
                    case Keywords.ASYNC:
                        // 先消费 async，下一 token 必须是 func（AsyncSeen 状态处理）
                        state = State.AsyncSeen;
                        return ParserLayerResult.Continue.Instance;
                    case Keywords.IF:
                        var ifNode = new IfExpressionASTNode(parentNode);
                        return DelegateStructuredParsing(ifNode, new IfStatementParserLayer(ifNode));
                    case Keywords.SWITCH:
                        var switchNode = new SwitchExpressionASTNode(parentNode);
                        return DelegateStructuredParsing(switchNode, new SwitchStatementParserLayer(switchNode));
                    case Keywords.TYPEOF:
                        var typeOfNode = new TypeOfExpressionASTNode(parentNode);
                        return DelegateStructuredParsing(typeOfNode, new TypeOfExpressionParserLayer(typeOfNode));
                    case Keywords.SEQ:
                    case Keywords.VOLATILE:
                        // seq 块可以作为表达式使用（通过 return@seq/return@label 返回值）
                        // 保留当前 token，因为 SeqBlockParserLayer 需要重新读取它
                        var seqNode = new SeqBlockExpressionASTNode(parentNode);
                        return DelegateStructuredParsing(seqNode, new SeqBlockParserLayer(seqNode), shouldKeepToken: true);
                }
            }

            // 4. 一元前缀运算符
            if (IsPrefixUnaryOperator(currentToken))
            {
                if (!allowPrefixUnary)
                {
                    context.RaiseError(
                        "连续的一元运算符必须用括号明确嵌套关系，如 not (not x)");
                }
                return HandlePrefixUnary(currentToken, context);
            }

            // 5. 符号引用（变量、函数等）- 委托给 PathParserLayer
            if (currentToken is WordToken)
            {
                return DelegateSymbolParsing(context, currentToken);
            }

            context.RaiseError($"Unexpected token at start of expression: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 委托字面量解析
        private ParserLayerResult DelegateLiteralParsing(Token currentToken, ParserLayerContext context)
        {
            // 先创建字面量表达式包装，字面量节点由结果传递机制回填
            var literalExpr = new LiteralExpressionASTNode(parentNode, null!);
            currentExpression = literalExpr;

            state = State.PrimaryParsed;
            pendingResultHandler = result => literalExpr.LiteralNode = result!;

            return new ParserLayerResult.PushLayer(new LiteralParserLayer(literalExpr), true);
        }

        // 委托括号分组解析
        private ParserLayerResult DelegateGroupParsing(ParserLayerContext context)
        {
            var groupExpr = new GroupExpressionASTNode(parentNode);
            currentExpression = groupExpr;

            state = State.PrimaryParsed;
            expectClosingParen = true;
            pendingResultHandler = result => groupExpr.InnerExpression = (ExpressionASTNode)result!;

            // 递归解析括号内的表达式（消费掉 ( token）
            return new ParserLayerResult.PushLayer(new ExpressionParserLayer(groupExpr), false);
        }

        // 委托 new 表达式解析
        private ParserLayerResult DelegateNewParsing(ParserLayerContext context)
        {
            var newExpr = new NewExpressionASTNode(parentNode);
            currentExpression = newExpr;

            // 使用 TypeReferenceParserLayer 解析类型（直接写入 newExpr.Type，无需结果传递）
            // TODO: 构造参数列表 () 的解析在后续阶段实现
            state = State.PrimaryParsed;

            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(newExpr.Type),
                false  // 跳过 'new' token
            );
        }

        // 委托结构化表达式（if/switch/typeOf/lambda/seq）：
        // 子层产出完整节点，经结果传递回填为当前主表达式
        private ParserLayerResult DelegateStructuredParsing(
            ExpressionASTNode node,
            IParserLayer layer,
            bool shouldKeepToken = false)
        {
            pendingResultHandler = result => currentExpression = (ExpressionASTNode)result!;
            state = State.PrimaryParsed;
            // 起始关键字（if/switch/typeOf/func）已被本层消费，默认不保留
            // seq/volatile 需要保留，因为 SeqBlockParserLayer 需要重新读取
            return new ParserLayerResult.PushLayer(layer, shouldKeepToken);
        }

        // 委托 lambda 表达式解析（func 已消费；isAsync 标记 async lambda）
        private ParserLayerResult DelegateLambdaParsing(bool isAsync)
        {
            var lambdaNode = new LambdaExpressionASTNode(parentNode) { IsAsync = isAsync };
            return DelegateStructuredParsing(lambdaNode, new LambdaExpressionParserLayer(lambdaNode));
        }

        // async 已读：下一 token 必须是 func（async lambda，SYNTAX §5.3）
        private ParserLayerResult HandleAsyncSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.FUNC)
            {
                return DelegateLambdaParsing(true);
            }

            context.RaiseError($"Expected 'func' after 'async' (async lambda), got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 类型操作符已读：as 后可接一个 ?（安全转换），随后委托类型引用解析
        private ParserLayerResult HandleTypeOperatorSeen(Token currentToken, ParserLayerContext context)
        {
            // as? 安全转换标记（仅 as 可用，且只能出现一次）
            if (currentToken is NotationToken q && q.Content == "?")
            {
                if (pendingTypeOperator != Keywords.AS || safeCastMarkConsumed)
                {
                    context.RaiseError($"Unexpected '?' after type operator '{pendingTypeOperator}'");
                }
                pendingCastNode!.IsSafe = true;
                safeCastMarkConsumed = true;
                return ParserLayerResult.Continue.Instance;
            }

            // 委托 TypeReferenceParserLayer 解析右侧类型（保留 token）。
            // 类型完成后本表达式即完成：后续再接运算符必须加括号（无优先级规则）
            var targetType = pendingCastNode != null ? pendingCastNode.TargetType : pendingCheckNode!.TargetType;
            state = State.Completed;
            return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(targetType), true);
        }

        // 处理前缀一元运算符
        private ParserLayerResult HandlePrefixUnary(Token currentToken, ParserLayerContext context)
        {
            string op = GetOperatorString(currentToken);

            var unaryExpr = new UnaryExpressionASTNode(parentNode)
            {
                Operator = op,
                IsPrefix = true
            };
            currentExpression = unaryExpr;
            state = State.PrimaryParsed;

            // 一元结果不能再直接接二元运算符（-x + y 非法，须写 (-x) + y）
            allowBinaryOperator = false;

            // 递归解析操作数：操作数内禁止二元运算符与连续一元运算符
            var operandLayer = new ExpressionParserLayer(unaryExpr)
            {
                allowBinaryOperator = false,
                allowPrefixUnary = false
            };
            pendingResultHandler = result => unaryExpr.Operand = (ExpressionASTNode)result!;

            return new ParserLayerResult.PushLayer(operandLayer, false);
        }

        // 委托符号解析
        private ParserLayerResult DelegateSymbolParsing(ParserLayerContext context, Token currentToken)
        {
            var symbolExpr = new SymbolReferenceASTNode(parentNode);
            currentExpression = symbolExpr;

            // 使用 PathParserLayer 解析符号（直接写入 symbolExpr.Symbol，无需结果传递）
            state = State.PrimaryParsed;

            return new ParserLayerResult.PushLayer(
                new PathParserLayer(
                    PathParserLayer.PathType.SymbolPath,
                    symbolExpr.Symbol,
                    lineBreakSensitive: true
                ),
                true  // 保留当前 token
            );
        }

        // 主表达式已解析：检查后续操作
        private ParserLayerResult HandlePrimaryParsed(Token currentToken, ParserLayerContext context)
        {
            // 等待分组右括号的情况：只接受 )
            if (expectClosingParen)
            {
                if (currentToken is NotationToken nt && nt.Content == ")")
                {
                    // 消费 )，分组完成；分组整体仍可作为一个主表达式参与后续运算
                    expectClosingParen = false;
                    return ParserLayerResult.Continue.Instance;
                }

                context.RaiseError($"Expected ')' to close group expression, got: {currentToken}");
                return new ParserLayerResult.PopLayer(false);
            }

            // 后缀链（SYNTAX.md §1.4：调用/索引/成员路径在运算符之前整体形成）
            if (currentToken is NotationToken suffix)
            {
                // 调用后缀 (
                if (suffix.Content == "(")
                {
                    // new 表达式的构造参数列表不生成 Call 节点
                    if (currentExpression is NewExpressionASTNode newExpr)
                    {
                        return new ParserLayerResult.PushLayer(
                            new ArgumentListParserLayer(
                                newExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, parentNode),
                            false);
                    }

                    var callExpr = new CallExpressionASTNode(parentNode)
                    {
                        Callee = currentExpression!
                    };
                    currentExpression = callExpr;
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            callExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, parentNode),
                        false);
                }

                // 索引后缀 [
                if (suffix.Content == "[")
                {
                    var indexExpr = new IndexExpressionASTNode(parentNode)
                    {
                        Object = currentExpression!
                    };
                    currentExpression = indexExpr;
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            indexExpr.Indices, ArgumentListParserLayer.BracketKind.Square, parentNode),
                        false);
                }

                // 成员访问 .
                if (suffix.Content == ".")
                {
                    pendingSafeAccess = false;
                    state = State.MemberNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 安全访问 ?.
                if (suffix.Content == "?")
                {
                    state = State.SafeDotExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 泛型实参 \<（挂在 MemberAccess 上，如 foo().bar\<i32>；
                // 符号路径上的泛型已由 PathParserLayer 解析，不会到达这里）
                if (suffix.Content == "\\")
                {
                    state = State.GenericAngleExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // trailing lambda（SYNTAX §5.2）：
                // expr{...} 脱糖为以 lambda 为唯一实参的调用，如 list.map{...}
                if (suffix.Content == "{")
                {
                    var lambdaNode = new LambdaExpressionASTNode(parentNode);
                    var trailingCall = new CallExpressionASTNode(parentNode)
                    {
                        Callee = currentExpression!
                    };
                    currentExpression = trailingCall;
                    pendingResultHandler = result =>
                        trailingCall.Arguments.Add(
                            new ArgumentASTNode(trailingCall, (ExpressionASTNode)result!));
                    // { 交给 LambdaExpressionParserLayer 消费
                    return new ParserLayerResult.PushLayer(new LambdaExpressionParserLayer(lambdaNode), true);
                }
            }

            // 类型操作（SYNTAX §3.5/§3.7）：is / supers / with / as / as?
            // 右侧是类型引用而非表达式，生成专用节点，委托 TypeReferenceParserLayer
            if (currentToken is WordToken typeOp && IsTypeOperator(typeOp.Content))
            {
                pendingTypeOperator = typeOp.Content;
                safeCastMarkConsumed = false;
                if (typeOp.Content == Keywords.AS)
                {
                    pendingCastNode = new CastExpressionASTNode(parentNode)
                    {
                        Object = currentExpression!
                    };
                    pendingCheckNode = null;
                    currentExpression = pendingCastNode;
                }
                else
                {
                    pendingCheckNode = new TypeCheckExpressionASTNode(parentNode)
                    {
                        Object = currentExpression!,
                        Operator = typeOp.Content
                    };
                    pendingCastNode = null;
                    currentExpression = pendingCheckNode;
                }
                state = State.TypeOperatorSeen;
                return ParserLayerResult.Continue.Instance;
            }

            // 检查二元运算符
            if (IsBinaryOperator(currentToken))
            {
                if (!allowBinaryOperator)
                {
                    context.RaiseError(
                        $"Latte 没有运算符优先级：运算符 '{GetOperatorString(currentToken)}' " +
                        "必须用括号明确运算顺序");
                }

                pendingOperator = GetOperatorString(currentToken);
                state = State.OperatorSeen;
                return ParserLayerResult.Continue.Instance;
            }

            // 没有后续操作，表达式完成
            state = State.Completed;
            return new ParserLayerResult.PopLayer(true);
        }

        // 看到运算符后：解析右操作数
        private ParserLayerResult HandleOperatorSeen(Token currentToken, ParserLayerContext context)
        {
            if (pendingOperator == null)
            {
                context.RaiseError("Internal error: pendingOperator is null");
                return new ParserLayerResult.PopLayer(false);
            }

            // > 系列运算符的重新组合：
            // Lexer 不合并 > 系列（见 Notations.StringNotations 的注释），
            // 因此在运算符状态下把连续的 >、= 组合为 >=、>>、>>>
            if (pendingOperator.All(c => c == '>') &&
                currentToken is NotationToken nt &&
                (nt.Content == ">" || nt.Content == "="))
            {
                string combined = pendingOperator + nt.Content;
                if (combined is ">=" or ">>" or ">>>")
                {
                    pendingOperator = combined;
                    return ParserLayerResult.Continue.Instance;
                }
                // 其他组合（如 >>>>）不合法，落入正常流程后会在右操作数解析时报错
            }

            // 创建二元表达式节点
            var binaryExpr = new BinaryExpressionASTNode(parentNode)
            {
                Left = currentExpression!,
                Operator = pendingOperator
            };

            // 递归解析右操作数：右操作数层禁止再消费二元运算符
            // （1 + 2 * 3 是编译错误，必须写成 1 + (2 * 3)）
            var rightLayer = new ExpressionParserLayer(binaryExpr)
            {
                allowBinaryOperator = false
            };
            pendingResultHandler = result => binaryExpr.Right = (ExpressionASTNode)result!;

            currentExpression = binaryExpr;
            state = State.Completed;

            return new ParserLayerResult.PushLayer(rightLayer, true);
        }

        // 二元表达式已完成：再来运算符就违反"无运算符优先级"规则
        private ParserLayerResult HandleCompleted(Token currentToken, ParserLayerContext context)
        {
            if (IsBinaryOperator(currentToken))
            {
                context.RaiseError(
                    $"Latte 没有运算符优先级：运算符 '{GetOperatorString(currentToken)}' " +
                    "必须用括号明确运算顺序");
            }

            return new ParserLayerResult.PopLayer(true);
        }

        // . 或 ?. 之后：读取成员名，创建 MemberAccess 节点
        private ParserLayerResult HandleMemberNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken name)
            {
                var accessExpr = new MemberAccessASTNode(parentNode)
                {
                    Object = currentExpression!,
                    MemberName = name.Content,
                    IsSafeAccess = pendingSafeAccess
                };
                currentExpression = accessExpr;
                pendingSafeAccess = false;
                state = State.PrimaryParsed;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected member name after '.', got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // ? 之后：必须是 . （安全访问 ?.）
        private ParserLayerResult HandleSafeDotExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ".")
            {
                pendingSafeAccess = true;
                state = State.MemberNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '.' after '?' for safe member access, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // \ 之后：必须是 < ，开始解析泛型实参列表
        private ParserLayerResult HandleGenericAngleExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "<")
            {
                var arg = new TypeReferenceASTNode(parentNode);
                pendingGenericArgs.Add(arg);
                state = State.GenericArgParsed;
                return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(arg), false);
            }

            context.RaiseError($"Expected '<' after '\\' in generic argument list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 一个泛型实参已解析：等待 , 或 >
        private ParserLayerResult HandleGenericArgParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                if (nt.Content == ",")
                {
                    var arg = new TypeReferenceASTNode(parentNode);
                    pendingGenericArgs.Add(arg);
                    return new ParserLayerResult.PushLayer(new TypeReferenceParserLayer(arg), false);
                }

                if (nt.Content == ">")
                {
                    AttachGenericArguments(context);
                    state = State.PrimaryParsed;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected ',' or '>' in generic argument list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(false);
        }

        // 把收集到的泛型实参挂到当前表达式上
        private void AttachGenericArguments(ParserLayerContext context)
        {
            if (currentExpression is MemberAccessASTNode accessExpr)
            {
                accessExpr.GenericArguments.AddRange(pendingGenericArgs);
            }
            else if (currentExpression is SymbolReferenceASTNode symbolRef)
            {
                // 防御分支：符号路径上的泛型通常已由 PathParserLayer 解析
                var elements = symbolRef.Symbol.symbol.elements;
                if (elements.Count == 0)
                {
                    context.RaiseError("Cannot attach generic arguments to empty symbol");
                    return;
                }
                var last = elements[elements.Count - 1];
                foreach (var arg in pendingGenericArgs)
                {
                    last.generics.Add(arg.TypeSymbol.symbol);
                }
            }
            else
            {
                context.RaiseError(
                    $"Generic arguments are only allowed on symbols or member access, " +
                    $"got: {currentExpression?.GetType().Name}");
                return;
            }
            pendingGenericArgs.Clear();
        }

        // ===== 辅助判断方法 =====

        private bool IsLiteralStart(Token token)
        {
            if (token is StringToken) return true;

            if (token is WordToken wt)
            {
                // 布尔和 null
                if (wt.Content == Keywords.TRUE ||
                    wt.Content == Keywords.FALSE ||
                    wt.Content == Keywords.NULL)
                    return true;

                // 数字
                if (char.IsDigit(wt.Content[0]) ||
                    wt.Content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private bool IsPrefixUnaryOperator(Token token)
        {
            if (token is NotationToken nt)
            {
                return nt.Content == "-" || nt.Content == "+" || nt.Content == "!";
            }

            if (token is WordToken wt)
            {
                return wt.Content == Keywords.NOT;
                // await 暂不支持，需要先添加到 Keywords
            }

            return false;
        }

        private bool IsBinaryOperator(Token token)
        {
            if (token is NotationToken nt)
            {
                return nt.Content == "+" || nt.Content == "-" ||
                       nt.Content == "*" || nt.Content == "/" ||
                       nt.Content == "==" || nt.Content == "!=" ||
                       nt.Content == "<" || nt.Content == ">" ||
                       nt.Content == "<=" || nt.Content == ">=";
            }

            if (token is WordToken wt)
            {
                return wt.Content == Keywords.AND ||
                       wt.Content == Keywords.OR ||
                       wt.Content == Keywords.IN;
                // is/as 不是二元运算符：右侧是类型引用，见 TypeOperatorSeen 状态
            }

            return false;
        }

        // 类型操作符（右操作数为类型引用）：is / as / supers / with
        private static bool IsTypeOperator(string word)
        {
            return word == Keywords.IS || word == Keywords.AS ||
                   word == Keywords.SUPERS || word == Keywords.WITH;
        }

        private string GetOperatorString(Token token)
        {
            if (token is NotationToken nt) return nt.Content;
            if (token is WordToken wt) return wt.Content;
            return "";
        }
    }
}
