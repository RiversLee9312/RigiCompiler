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
    /// 施工协议（大扫除后）：
    /// - 构造函数接收唯一的最终挂载位置 target（ExpressionRootASTNode，创建时必须为空）；
    /// - 内部先把完整表达式构造为未挂载子树（currentExpression），
    ///   后缀链/包装通过「新包装节点.Attach(旧子树)」逐层外卷；
    /// - 表达式完整时执行且仅执行一次 target.Attach(currentExpression)，随后立即 Pop；
    /// - 不产生任何返回值：委托出去的子 Layer 一律原地填充传入的目标节点。
    ///
    /// 重要：Latte 没有运算符优先级！
    /// 所有运算必须用括号明确指定，如 (1 + 2) * 3。
    /// 因此每层表达式最多消费一个二元运算符；右操作数与一元操作数
    /// 通过 allowBinaryOperator / allowPrefixUnary 禁止继续吞并运算符。
    /// </summary>
    public class ExpressionParserLayer : IParserLayer
    {
        private readonly ExpressionRootASTNode target;
        private ExpressionASTNode? currentExpression;

        // 本层创建了一个括号分组，正在等待其右括号 )
        private bool expectClosingParen = false;

        // 运算符限制（Latte 无运算符优先级）：
        // 右操作数层与一元表达式结果禁止再直接消费二元运算符
        private bool allowBinaryOperator = true;
        // 一元操作数层禁止连续的一元运算符（not not x 必须写成 not (not x)）
        private bool allowPrefixUnary = true;
        // 括号语境（M31，SYNTAX §1.1）：() / [] 未闭合时换行按空白处理。
        // 分组子层与实参/索引内的表达式层为 true；右操作数/一元操作数层继承。
        // internal：ArgumentListParserLayer 等外部层创建实参表达式层时设置
        internal bool insideParens = false;
        // 父上下文是否允许裸 return（SYNTAX §5.1）：lambda 体内为 false，
        // 沿委托链（分组/一元/二元/实参/if/switch 表达式分支体）传染；
        // lambda 自身是边界（LambdaExpressionParserLayer 对其体一律下传 false）
        internal bool allowBareReturn = true;

        private enum State
        {
            Initial,              // 等待主表达式
            PrimaryParsed,        // 主表达式已解析
            OperatorSeen,         // 看到运算符
            MemberNameExpected,   // . 或 ?. 之后等待成员名
            EnumCaseNameExpected, // 前导点 . 已读：等待 enum case 名（SYNTAX §12）
            WrapperNameExpected,  // : 已读：等待 wrapper 名（SYNTAX §14.1）
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
        // is 右侧前导点 enum case 分流标记（SYNTAX §12.3）：true 时
        // HandleEnumCaseNameExpected 把 case 节点挂入 pendingCheckNode.TargetCase
        private bool enumCaseForTypeCheck = false;

        // 后缀链状态
        private bool pendingSafeAccess = false;
        private readonly List<TypeReferenceASTNode> pendingGenericArgs = new();

        // Span 施工（M28）：前导 `.`（enum case 引用）的 token 起点；
        // trailing lambda 的实参节点（lambda 弹栈后与当前表达式一并封口）
        private CharPosition pendingSuffixStart;
        private ArgumentASTNode? pendingTrailingArgument = null;
        // async lambda 的 async 关键字起点（span 含 async，M31）
        private CharPosition pendingAsyncStart;

        public ExpressionParserLayer(
            ExpressionRootASTNode target,
            ExpressionASTNode? initialExpression = null)
        {
            this.target = target;

            // seed：调用方已解析出未挂载的表达式起点时（如具名实参判别后退化为
            // 位置实参的符号），直接从 PrimaryParsed 继续（可能跟随后缀链或运算符）
            if (initialExpression != null)
            {
                currentExpression = initialExpression;
                state = State.PrimaryParsed;
            }
        }

        // ===== Span 施工（M28）=====
        // 不变量：currentExpression 创建时即记 Start（End 先取当前 token），
        // 表达式不再生长时（后缀包装/运算符/完成）把 End 封到最近被消费的 token

        // 创建记 Start：以 start 为起点初始化节点 span
        private static void StartSpan(ASTNode node, CharPosition start, ParserLayerContext context)
        {
            var current = context.GetLocation();
            node.Span = new CharRange { Start = start, End = current.End, sourceName = current.sourceName };
        }

        // 包装记 Start：新包装节点的起点 = 被包内层表达式的起点（内层已封口，Span 必有值）
        private static void WrapSpan(ExpressionASTNode wrapper, ExpressionASTNode inner, ParserLayerContext context)
        {
            var current = context.GetLocation();
            wrapper.Span = new CharRange
            {
                Start = inner.Span!.Value.Start,
                End = current.End,
                sourceName = current.sourceName
            };
        }

        // 封口：当前 token 已不属于本表达式，End 扩展到最近被消费的 token
        private static void SealSpanEnd(ExpressionASTNode? node, ParserLayerContext context)
        {
            if (node?.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                node.Span = s;
            }
        }

        // 封口当前表达式（含 trailing lambda 的实参节点）
        private void SealCurrentExpression(ParserLayerContext context)
        {
            SealSpanEnd(currentExpression, context);
            if (pendingTrailingArgument != null)
            {
                if (pendingTrailingArgument.Span is { } s)
                {
                    s.End = context.GetPreviousLocation().End;
                    pendingTrailingArgument.Span = s;
                }
                pendingTrailingArgument = null;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：表达式已完整则挂载并弹出、上交 EOF；结构不完整（运算符后、括号内等）报错
            if (currentToken is EndOfFileToken)
            {
                return HandleEndOfFile(context);
            }

            // 括号语境下的换行透明化（M31，SYNTAX §1.1：() / [] 未闭合时换行按空白处理）：
            // 结构等待态遇换行直接跳过；后缀装配态（成员名/泛型/类型操作等）不在豁免内
            if (insideParens && currentToken is LineBreakToken && IsLineBreakTransparentState(state))
            {
                return ParserLayerResult.Continue.Instance;
            }

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

                case State.EnumCaseNameExpected:
                    return HandleEnumCaseNameExpected(currentToken, context);

                case State.WrapperNameExpected:
                    return HandleWrapperNameExpected(currentToken, context);

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
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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

            // 2.1 前导点 enum case 引用（SYNTAX §12）：.Success / .Entity；
            // 参数化 case 的调用（.Failed(404)）由后缀链自然脱糖为 Call
            if (currentToken is NotationToken caseDot && caseDot.Content == ".")
            {
                // 记忆前导点位置：作为 EnumCaseExpression 的 span 起点
                pendingSuffixStart = context.GetLocation().Start;
                state = State.EnumCaseNameExpected;
                return ParserLayerResult.Continue.Instance;
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
                        return DelegateLambdaParsing(false, context.GetLocation().Start, context);
                    case Keywords.ASYNC:
                        // 先消费 async（记起点：span 含 async），下一 token 必须是 func
                        pendingAsyncStart = context.GetLocation().Start;
                        state = State.AsyncSeen;
                        return ParserLayerResult.Continue.Instance;
                    case Keywords.IF:
                        // span 含起始关键字（M31：子层 FirstRange 从 ( 起算，这里显式记 Start）
                        var ifNode = new IfExpressionASTNode();
                        StartSpan(ifNode, context.GetLocation().Start, context);
                        return DelegateStructuredParsing(ifNode,
                            new IfStatementParserLayer(ifNode, allowBareReturn));
                    case Keywords.SWITCH:
                        var switchNode = new SwitchExpressionASTNode();
                        StartSpan(switchNode, context.GetLocation().Start, context);
                        return DelegateStructuredParsing(switchNode,
                            new SwitchStatementParserLayer(switchNode, allowBareReturn));
                    case Keywords.TYPEOF:
                        var typeOfNode = new TypeOfExpressionASTNode();
                        StartSpan(typeOfNode, context.GetLocation().Start, context);
                        return DelegateStructuredParsing(typeOfNode,
                            new TypeOfExpressionParserLayer(typeOfNode) { allowBareReturn = allowBareReturn });
                    case Keywords.SEQ:
                    case Keywords.VOLATILE:
                        // seq 块可以作为表达式使用（通过 return@_/return@标签 返回值，
                        // 匿名默认标签为 _，SYNTAX §6.1）
                        // 保留当前 token，因为 SeqBlockParserLayer 需要重新读取它
                        var seqNode = new SeqBlockExpressionASTNode();
                        return DelegateStructuredParsing(
                            seqNode, new SeqBlockParserLayer(seqNode, allowBareReturn),
                            TokenDisposition.Replay);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 委托字面量解析：本层创建 LiteralExpression 包装并作为当前主表达式，
        // LiteralParserLayer 原地填充（AttachLiteral），无需回传
        private ParserLayerResult DelegateLiteralParsing(Token currentToken, ParserLayerContext context)
        {
            var literalExpr = new LiteralExpressionASTNode();
            currentExpression = literalExpr;

            state = State.PrimaryParsed;

            return new ParserLayerResult.PushLayer(
                new LiteralParserLayer(literalExpr), TokenDisposition.Replay);
        }

        // 委托括号分组解析：内层表达式直接附加到 group.InnerExpression
        private ParserLayerResult DelegateGroupParsing(ParserLayerContext context)
        {
            var groupExpr = new GroupExpressionASTNode();
            StartSpan(groupExpr, context.GetLocation().Start, context);
            currentExpression = groupExpr;

            state = State.PrimaryParsed;
            expectClosingParen = true;

            // 递归解析括号内的表达式（消费掉 ( token）；
            // 括号语境：分组内换行按空白处理（SYNTAX §1.1）
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(groupExpr.InnerExpression)
                {
                    insideParens = true,
                    allowBareReturn = allowBareReturn
                },
                TokenDisposition.Consume);
        }

        // 委托 new 表达式解析
        private ParserLayerResult DelegateNewParsing(ParserLayerContext context)
        {
            var newExpr = new NewExpressionASTNode();
            StartSpan(newExpr, context.GetLocation().Start, context);
            currentExpression = newExpr;

            // 使用 TypeReferenceParserLayer 解析类型（原地填充 newExpr.Type）
            state = State.PrimaryParsed;

            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(newExpr.Type),
                TokenDisposition.Consume  // 跳过 'new' token
            );
        }

        // 委托结构化表达式（if/switch/typeOf/lambda/seq）：
        // 节点由本层创建并作为当前主表达式，子 Layer 原地填充该节点，无需回传
        private ParserLayerResult DelegateStructuredParsing(
            ExpressionASTNode node,
            IParserLayer layer,
            TokenDisposition disposition = TokenDisposition.Consume)
        {
            currentExpression = node;
            state = State.PrimaryParsed;
            // 起始关键字（if/switch/typeOf/func）已被本层消费，默认不保留
            // seq/volatile 需要保留，因为 SeqBlockParserLayer 需要重新读取
            return new ParserLayerResult.PushLayer(layer, disposition);
        }

        // 委托 lambda 表达式解析（func 已消费；isAsync 标记 async lambda）；
        // keywordStart 为 func（或 async）关键字起点：span 含起始关键字（M31）
        private ParserLayerResult DelegateLambdaParsing(
            bool isAsync, CharPosition keywordStart, ParserLayerContext context)
        {
            var lambdaNode = new LambdaExpressionASTNode() { IsAsync = isAsync };
            StartSpan(lambdaNode, keywordStart, context);
            return DelegateStructuredParsing(lambdaNode, new LambdaExpressionParserLayer(lambdaNode));
        }

        // async 已读：下一 token 必须是 func（async lambda，SYNTAX §5.3）
        private ParserLayerResult HandleAsyncSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken wt && wt.Content == Keywords.FUNC)
            {
                return DelegateLambdaParsing(true, pendingAsyncStart, context);
            }

            context.RaiseError($"Expected 'func' after 'async' (async lambda), got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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

            // is 右侧的前导点 enum case（SYNTAX §12.3：result is .Failed）：
            // 仅 is 允许；as/as?/supers/with 右侧必须是类型，
            // . 维持走下方类型委托路径，由 TypeReferenceParserLayer 报「Expected type name」
            if (currentToken is NotationToken dot && dot.Content == "." &&
                pendingTypeOperator == Keywords.IS)
            {
                // 记忆前导点位置（TargetCase 的 span 起点），转前导点既有路径
                pendingSuffixStart = context.GetLocation().Start;
                enumCaseForTypeCheck = true;
                state = State.EnumCaseNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            // 委托 TypeReferenceParserLayer 解析右侧类型（保留 token）。
            // 类型完成后本表达式即完成：后续再接运算符必须加括号（无优先级规则）
            TypeReferenceASTNode targetType;
            if (pendingCastNode != null)
            {
                targetType = pendingCastNode.TargetType;
            }
            else
            {
                // TargetType 槽在此创建填充（与 TargetCase 互斥，双槽先例：
                // 创建时归属即定，Parent 一次成型）
                targetType = new TypeReferenceASTNode(pendingCheckNode!);
                pendingCheckNode!.TargetType = targetType;
            }
            state = State.Completed;
            return new ParserLayerResult.PushLayer(
                new TypeReferenceParserLayer(targetType), TokenDisposition.Replay);
        }

        // 处理前缀一元运算符
        private ParserLayerResult HandlePrefixUnary(Token currentToken, ParserLayerContext context)
        {
            string op = GetOperatorString(currentToken);

            var unaryExpr = new UnaryExpressionASTNode()
            {
                Operator = op,
                IsPrefix = true
            };
            StartSpan(unaryExpr, currentToken.CharRange.Start, context);
            currentExpression = unaryExpr;
            state = State.PrimaryParsed;

            // 一元结果不能再直接接二元运算符（-x + y 非法，须写 (-x) + y）
            allowBinaryOperator = false;

            // 递归解析操作数：操作数内禁止二元运算符与连续一元运算符
            var operandLayer = new ExpressionParserLayer(unaryExpr.Operand)
            {
                allowBinaryOperator = false,
                allowPrefixUnary = false,
                insideParens = insideParens,
                allowBareReturn = allowBareReturn
            };

            return new ParserLayerResult.PushLayer(operandLayer, TokenDisposition.Consume);
        }

        // 委托符号解析
        private ParserLayerResult DelegateSymbolParsing(ParserLayerContext context, Token currentToken)
        {
            var symbolExpr = new SymbolReferenceASTNode();
            StartSpan(symbolExpr, currentToken.CharRange.Start, context);
            currentExpression = symbolExpr;

            // 使用 PathParserLayer 解析符号（原地填充 symbolExpr.Symbol）
            state = State.PrimaryParsed;

            return new ParserLayerResult.PushLayer(
                new PathParserLayer(symbolExpr.Symbol, lineBreakSensitive: true),
                TokenDisposition.Replay  // 保留当前 token
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
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
                                newExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, newExpr)
                            {
                                allowBareReturn = allowBareReturn
                            },
                            TokenDisposition.Consume);
                    }

                    // 被调表达式到此为止（( 不属于它），封口后包入 Call
                    SealCurrentExpression(context);
                    var callExpr = new CallExpressionASTNode();
                    callExpr.Callee.Attach(currentExpression!);
                    WrapSpan(callExpr, currentExpression!, context);
                    currentExpression = callExpr;
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            callExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, callExpr)
                        {
                            allowBareReturn = allowBareReturn
                        },
                        TokenDisposition.Consume);
                }

                // 索引后缀 [
                if (suffix.Content == "[")
                {
                    SealCurrentExpression(context);
                    var indexExpr = new IndexExpressionASTNode();
                    indexExpr.Object.Attach(currentExpression!);
                    WrapSpan(indexExpr, currentExpression!, context);
                    currentExpression = indexExpr;
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            indexExpr.Indices, ArgumentListParserLayer.BracketKind.Square, indexExpr)
                        {
                            allowBareReturn = allowBareReturn
                        },
                        TokenDisposition.Consume);
                }

                // 成员访问 .
                if (suffix.Content == ".")
                {
                    SealCurrentExpression(context);
                    pendingSafeAccess = false;
                    state = State.MemberNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 安全访问 ?.
                if (suffix.Content == "?")
                {
                    SealCurrentExpression(context);
                    state = State.SafeDotExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // wrapper 访问 :（SYNTAX §14.1，与成员访问同属路径后缀链；链式 obj:A:B 左结合）
                if (suffix.Content == ":")
                {
                    SealCurrentExpression(context);
                    state = State.WrapperNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 泛型实参 \<（挂在 MemberAccess 上，如 foo().bar\<i32>；
                // 符号路径上的泛型已由 PathParserLayer 解析，不会到达这里）
                // 注意：泛型实参列表是当前表达式的延续，此处不封口
                if (suffix.Content == "\\")
                {
                    state = State.GenericAngleExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // trailing lambda（SYNTAX §5.2）：
                // expr{...} 脱糖为以 lambda 为唯一实参的调用，如 list.map{...}
                if (suffix.Content == "{")
                {
                    SealCurrentExpression(context);
                    var lambdaNode = new LambdaExpressionASTNode();
                    var trailingCall = new CallExpressionASTNode();
                    trailingCall.Callee.Attach(currentExpression!);
                    WrapSpan(trailingCall, currentExpression!, context);
                    // lambda 作为唯一实参直接挂入实参的 Root（未挂载节点，可安全 Attach）；
                    // LambdaExpressionParserLayer 之后原地填充该 lambda 节点
                    var argument = new ArgumentASTNode(trailingCall);
                    argument.Value.Attach(lambdaNode);
                    // 实参 span 起点 = { 处；lambda 弹栈后由 SealCurrentExpression 封口
                    StartSpan(argument, context.GetLocation().Start, context);
                    pendingTrailingArgument = argument;
                    trailingCall.Arguments.Add(argument);
                    currentExpression = trailingCall;
                    // { 交给 LambdaExpressionParserLayer 消费
                    return new ParserLayerResult.PushLayer(
                        new LambdaExpressionParserLayer(lambdaNode), TokenDisposition.Replay);
                }
            }

            // 类型操作（SYNTAX §3.5/§3.7）：is / supers / with / as / as?
            // 右侧是类型引用而非表达式，生成专用节点，委托 TypeReferenceParserLayer
            if (currentToken is WordToken typeOp && IsTypeOperator(typeOp.Content))
            {
                SealCurrentExpression(context);
                pendingTypeOperator = typeOp.Content;
                safeCastMarkConsumed = false;
                if (typeOp.Content == Keywords.AS)
                {
                    pendingCastNode = new CastExpressionASTNode();
                    pendingCastNode.Object.Attach(currentExpression!);
                    WrapSpan(pendingCastNode, currentExpression!, context);
                    pendingCheckNode = null;
                    currentExpression = pendingCastNode;
                }
                else
                {
                    pendingCheckNode = new TypeCheckExpressionASTNode
                    {
                        Operator = typeOp.Content
                    };
                    pendingCheckNode.Object.Attach(currentExpression!);
                    WrapSpan(pendingCheckNode, currentExpression!, context);
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

                // 左操作数到此为止（运算符不属于它），封口
                SealCurrentExpression(context);
                pendingOperator = GetOperatorString(currentToken);
                state = State.OperatorSeen;
                return ParserLayerResult.Continue.Instance;
            }

            // 没有后续操作，表达式完成：挂载最终外层节点并弹出
            return CompleteExpression(context, TokenDisposition.Replay);
        }

        // 看到运算符后：解析右操作数
        private ParserLayerResult HandleOperatorSeen(Token currentToken, ParserLayerContext context)
        {
            if (pendingOperator == null)
            {
                context.RaiseError("Internal error: pendingOperator is null");
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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

            // 复合赋值（SYNTAX §13.2）：Lexer 一律拆 token（M31），在运算符状态下
            // 遇 = 与 pending 运算符重组；全集 10 个（+= -= *= /= <<= >>= >>>= &= |= ^=，
            // 无 %=）。>>= / >>>= 先经上方 > 系列重组把 pendingOperator 收拢为 >> / >>>
            // （比较的 >= >> >>> 已被重组逻辑拦截，不会到达这里）；不属于全集的组合
            // （如 == 后再遇 =）不拦截，落入正常二元流程，由右操作数层报意外 token
            if (currentToken is NotationToken assign && assign.Content == "=" &&
                IsCompoundAssignmentOperator(pendingOperator))
            {
                // 左操作数到此为止：当前表达式仍是未挂载子树（Attach 前已确定
                // 最终形态），挂入 Target Root——与二元 Left 同一路径，无 Root 替换
                var compoundExpr = new CompoundAssignmentExpressionASTNode
                {
                    Operator = pendingOperator
                };
                compoundExpr.Target.Attach(currentExpression!);
                WrapSpan(compoundExpr, currentExpression!, context);

                // 右操作数照常经 ExpressionRoot 委托解析（禁止再消费二元运算符）
                var valueLayer = new ExpressionParserLayer(compoundExpr.Value)
                {
                    allowBinaryOperator = false,
                    insideParens = insideParens,
                    allowBareReturn = allowBareReturn
                };

                currentExpression = compoundExpr;
                state = State.Completed;

                // = 是复合赋值符号的组成部分，由本层消费
                return new ParserLayerResult.PushLayer(valueLayer, TokenDisposition.Consume);
            }

            // 创建二元表达式节点：左操作数挂入 Left Root（未挂载子树包装）
            var binaryExpr = new BinaryExpressionASTNode
            {
                Operator = pendingOperator
            };
            binaryExpr.Left.Attach(currentExpression!);
            WrapSpan(binaryExpr, currentExpression!, context);

            // 递归解析右操作数：右操作数层禁止再消费二元运算符
            // （1 + 2 * 3 是编译错误，必须写成 1 + (2 * 3)）
            var rightLayer = new ExpressionParserLayer(binaryExpr.Right)
            {
                allowBinaryOperator = false,
                insideParens = insideParens,
                allowBareReturn = allowBareReturn
            };

            currentExpression = binaryExpr;
            state = State.Completed;

            return new ParserLayerResult.PushLayer(rightLayer, TokenDisposition.Replay);
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

            return CompleteExpression(context, TokenDisposition.Replay);
        }

        // . 或 ?. 之后：读取成员名，创建 MemberAccess 节点
        private ParserLayerResult HandleMemberNameExpected(Token currentToken, ParserLayerContext context)
        {
            // 成员名不能是数字词（M31：3.14.15、foo.123 此前被接受；只查首字符）
            if (currentToken is WordToken name && Keywords.IsIdentifierStart(name.Content))
            {
                var accessExpr = new MemberAccessASTNode()
                {
                    MemberName = name.Content,
                    IsSafeAccess = pendingSafeAccess
                };
                accessExpr.Object.Attach(currentExpression!);
                WrapSpan(accessExpr, currentExpression!, context);
                currentExpression = accessExpr;
                pendingSafeAccess = false;
                state = State.PrimaryParsed;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected member name after '.', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 前导点 . 已读：读取 case 名，创建 EnumCaseExpression 节点（SYNTAX §12）
        private ParserLayerResult HandleEnumCaseNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken name && Keywords.IsIdentifierStart(name.Content))
            {
                // is 右侧的 enum case（SYNTAX §12.3）：挂入 TypeCheck 的 TargetCase 槽
                // （与 TargetType 互斥）；类型检查表达式整体即完成——后续再接运算符
                // 必须加括号（与类型右侧同规则，见 HandleTypeOperatorSeen）
                if (enumCaseForTypeCheck)
                {
                    enumCaseForTypeCheck = false;
                    var targetCase = new EnumCaseExpressionASTNode(pendingCheckNode!)
                    {
                        CaseName = name.Content
                    };
                    StartSpan(targetCase, pendingSuffixStart, context);
                    pendingCheckNode!.TargetCase = targetCase;
                    state = State.Completed;
                    return ParserLayerResult.Continue.Instance;
                }

                var enumCaseNode = new EnumCaseExpressionASTNode()
                {
                    CaseName = name.Content
                };
                StartSpan(enumCaseNode, pendingSuffixStart, context);
                currentExpression = enumCaseNode;
                state = State.PrimaryParsed;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected enum case name after '.', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // : 已读：读取 wrapper 名，创建 WrapperAccess 节点（SYNTAX §14.1）
        private ParserLayerResult HandleWrapperNameExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken name && Keywords.IsIdentifierStart(name.Content))
            {
                var wrapperExpr = new WrapperAccessASTNode()
                {
                    WrapperName = name.Content
                };
                wrapperExpr.Object.Attach(currentExpression!);
                WrapSpan(wrapperExpr, currentExpression!, context);
                currentExpression = wrapperExpr;
                state = State.PrimaryParsed;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected wrapper name after ':', got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // \ 之后：必须是 < ，开始解析泛型实参列表
        private ParserLayerResult HandleGenericAngleExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "<")
            {
                var arg = new TypeReferenceASTNode(currentExpression!);
                pendingGenericArgs.Add(arg);
                state = State.GenericArgParsed;
                return new ParserLayerResult.PushLayer(
                    new TypeReferenceParserLayer(arg), TokenDisposition.Consume);
            }

            context.RaiseError($"Expected '<' after '\\' in generic argument list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 一个泛型实参已解析：等待 , 或 >
        private ParserLayerResult HandleGenericArgParsed(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt)
            {
                if (nt.Content == ",")
                {
                    var arg = new TypeReferenceASTNode(currentExpression!);
                    pendingGenericArgs.Add(arg);
                    return new ParserLayerResult.PushLayer(
                        new TypeReferenceParserLayer(arg), TokenDisposition.Consume);
                }

                if (nt.Content == ">")
                {
                    AttachGenericArguments(context);
                    state = State.PrimaryParsed;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected ',' or '>' in generic argument list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            // 泛型实参列表的 > 属于本表达式：以当前 token 封 End
            if (currentExpression?.Span is { } s)
            {
                s.End = context.GetLocation().End;
                currentExpression.Span = s;
            }
        }

        // ===== 表达式完成与 EOF =====

        // 表达式完成：把最终外层表达式挂载到 target（成功路径必须且只能执行一次），随后弹出
        private ParserLayerResult CompleteExpression(ParserLayerContext context, TokenDisposition disposition)
        {
            // 最终封口：当前 token 已不属于本表达式
            SealCurrentExpression(context);
            target.Attach(
                currentExpression
                    ?? throw context.RaiseError("Expression is incomplete.")
            );
            state = State.Completed;
            return new ParserLayerResult.PopLayer(disposition);
        }

        // EOF 处理：表达式可以自然结束的状态挂载并弹栈、上交 EOF；其余状态为不完整结构
        private ParserLayerResult HandleEndOfFile(ParserLayerContext context)
        {
            bool canComplete =
                state == State.Completed ||
                (state == State.PrimaryParsed && !expectClosingParen);

            if (canComplete)
            {
                return CompleteExpression(context, TokenDisposition.Replay);
            }

            context.RaiseError("Unexpected end of file");
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
        }

        // ===== 辅助判断方法 =====

        private bool IsLiteralStart(Token token)
        {
            if (token is StringToken) return true;
            if (token is CharToken) return true;

            if (token is WordToken wt)
            {
                // 布尔和 null
                if (wt.Content == Keywords.TRUE ||
                    wt.Content == Keywords.FALSE ||
                    wt.Content == Keywords.NULL)
                    return true;

                // 数字（判定统一走 NumericLiteral，M31）
                if (NumericLiteral.IsNumericWord(wt.Content))
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
                return wt.Content == Keywords.NOT || wt.Content == Keywords.AWAIT;
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
                       nt.Content == "<=" || nt.Content == ">=" ||
                       // 位运算符（SYNTAX §13.2）：<< 由 Lexer 合并；
                       // >>、>>> 由 OperatorSeen 状态重组；& | ^ 为单字符
                       nt.Content == "<<" || nt.Content == "&" ||
                       nt.Content == "|" || nt.Content == "^";
            }

            if (token is WordToken wt)
            {
                return wt.Content == Keywords.AND ||
                       wt.Content == Keywords.OR;
                // is/as 不是二元运算符：右侧是类型引用，见 TypeOperatorSeen 状态；
                // in 只属于 for 循环头（LoopParserLayer 自处理），不是二元运算符（M31 移除）
            }

            return false;
        }

        // 类型操作符（右操作数为类型引用）：is / as / supers / with
        private static bool IsTypeOperator(string word)
        {
            return word == Keywords.IS || word == Keywords.AS ||
                   word == Keywords.SUPERS || word == Keywords.WITH;
        }

        // 复合赋值基础运算符全集（SYNTAX §13.2，10 个，无 %=）：
        // pending 运算符与随后的 = 组成复合赋值；>> 与 >>> 由 > 系列重组先行收拢
        private static bool IsCompoundAssignmentOperator(string op)
        {
            return op is "+" or "-" or "*" or "/" or
                   "<<" or ">>" or ">>>" or "&" or "|" or "^";
        }

        private string GetOperatorString(Token token)
        {
            if (token is NotationToken nt) return nt.Content;
            if (token is WordToken wt) return wt.Content;
            return "";
        }

        // 括号语境下换行透明的状态：等待主表达式、主表达式已解析（可能接运算符或 )）、
        // 等待右操作数、二元表达式已完成（可能接 ) 或犯错报优先级）
        private static bool IsLineBreakTransparentState(State state)
        {
            return state == State.Initial ||
                   state == State.PrimaryParsed ||
                   state == State.OperatorSeen ||
                   state == State.Completed;
        }
    }
}
