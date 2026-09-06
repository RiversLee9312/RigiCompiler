using System;
using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler
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
    /// 重要：Rigi 没有运算符优先级！
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

        // 运算符限制（Rigi 无运算符优先级）：
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
            MemberNameExpected,   // 路径连接符（. / ?. / :）之后等待成员名
            EnumCaseNameExpected, // 前导点 . 已读：等待 enum case 名（SYNTAX §12）
            SafeDotExpected,      // ? 之后等待 .
            GenericAngleExpected, // \ 之后等待 <（泛型实参列表）
            GenericArgParsed,     // 一个泛型实参已解析，等待 , 或 >
            TypeOperatorSeen,     // 类型操作符 is/as/supers/with 已读，等待右侧类型
            IfNullFallbackSeen,   // 中缀 if 已读，等待 ? 组成 if?（S7f 空值回退）
            MinusCandidate,       // 前缀 - 已读（负号折叠候选）：等待紧随的 token 判别
            MinusIntCandidate,    // - 与整数词已读：等待下一 token 确认非浮点（非 . ）
            Completed             // 完成
        }

        private State state = State.Initial;
        private string? pendingOperator = null;

        // 负号折叠候选暂存（SYNTAX §3.3）：前缀 - 的位置与紧随的整数词
        private CharPosition pendingMinusStart;
        private WordToken? pendingMinusWord = null;
        // 种子数字词（负号折叠的浮点回退：-3.14 的 - 与 3 已被外层消费）：
        // 首 token 处理前先把种子注入字面量层，当前 token 走正常 Replay
        internal string? seedNumericWord = null;
        // pending 运算符末 token 的 End 位置（重组相邻性校验）：> 系列重组与
        // 复合赋值 op+= 重组要求两 token span 相邻——Lexer 合并的 << <= 天然
        // 相邻，拆 token 的 > 与 = 系列可能夹空白（`a > = b` 不得重组为 >=）
        private CharPosition pendingOperatorEnd;

        // 类型操作（is/as/as?/supers/with）暂存：等待右侧类型引用解析
        private string? pendingTypeOperator = null;
        private CastExpressionASTNode? pendingCastNode = null;
        private TypeCheckExpressionASTNode? pendingCheckNode = null;
        private bool safeCastMarkConsumed = false;   // as? 的 ? 是否已消费
        // is 右侧前导点 enum case 分流标记（SYNTAX §12.3）：true 时
        // HandleEnumCaseNameExpected 把 case 节点挂入 pendingCheckNode.TargetCase
        private bool enumCaseForTypeCheck = false;

        // 路径后缀链状态（M42 统一路径表达式）
        private PathConnector pendingConnector = PathConnector.Dot;
        // 最近创建、待封口的路径后缀（Suffix 的 span 含括号对，在下一个
        // 不属于本路径的 token 处封 End）
        private ASTNode? currentPathTail = null;
        private readonly List<TypeReferenceASTNode> pendingGenericArgs = new();
        // 刚闭合泛型实参列表：下一 token 若是紧邻的多余 '>'，不得当成比较/移位运算符
        private bool justClosedGenericArgs = false;
        private CharPosition genericCloseEnd;

        // Span 施工（M28）：前导 `.`（enum case 引用）的 token 起点；
        // trailing lambda 的实参节点（lambda 弹栈后与当前表达式一并封口）
        private CharPosition pendingSuffixStart;
        private ArgumentASTNode? pendingTrailingArgument = null;

        public ExpressionParserLayer(
            ExpressionRootASTNode target,
            PathExpressionASTNode? initialPath = null)
        {
            this.target = target;

            // seed：调用方已解析出未挂载的路径起点时（如具名实参判别后退化为
            // 位置实参的符号），直接从 PrimaryParsed 继续（可能跟随后缀链或运算符）
            if (initialPath != null)
            {
                currentExpression = initialPath;
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

        // 封口：当前 token 已不属于本节点，End 扩展到最近被消费的 token
        private static void SealSpanEnd(ASTNode? node, ParserLayerContext context)
        {
            if (node?.Span is { } s)
            {
                s.End = context.GetPreviousLocation().End;
                node.Span = s;
            }
        }

        // 封口当前表达式（含路径后缀生长点与 trailing lambda 的实参节点）
        private void SealCurrentExpression(ParserLayerContext context)
        {
            SealSpanEnd(currentExpression, context);
            if (currentPathTail != null)
            {
                SealSpanEnd(currentPathTail, context);
                currentPathTail = null;
            }
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

            // 种子数字词注入（负号折叠的浮点回退）：- 与整数部分已被外层消费，
            // 直接委托带种子的字面量层；当前 token（. 等）经 Replay 正常流入
            if (seedNumericWord != null)
            {
                var seed = seedNumericWord;
                seedNumericWord = null;
                var seedLiteral = new LiteralExpressionASTNode();
                currentExpression = seedLiteral;
                state = State.PrimaryParsed;
                return new ParserLayerResult.PushLayer(
                    new LiteralParserLayer(seedLiteral, seed) { allowBareReturn = allowBareReturn },
                    TokenDisposition.Replay);
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

                case State.SafeDotExpected:
                    return HandleSafeDotExpected(currentToken, context);

                case State.GenericAngleExpected:
                    return HandleGenericAngleExpected(currentToken, context);

                case State.GenericArgParsed:
                    return HandleGenericArgParsed(currentToken, context);

                case State.TypeOperatorSeen:
                    return HandleTypeOperatorSeen(currentToken, context);

                case State.IfNullFallbackSeen:
                    return HandleIfNullFallbackSeen(currentToken, context);

                case State.MinusCandidate:
                    return HandleMinusCandidate(currentToken, context);

                case State.MinusIntCandidate:
                    return HandleMinusIntCandidate(currentToken, context);

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
                // placeOf 是前缀语法；仅紧邻括号是被禁止的伪函数调用。
                // 空白/注释分隔的分组操作数继续交给普通表达式层。
                if (target.Parent is PlaceOfExpressionASTNode placeOf
                    && placeOf.Span is { } placeSpan
                    && currentToken.CharRange.Start.offset == placeSpan.End.offset)
                    context.RaiseError("placeOf operand requires separation; placeOf(x) is not valid syntax");
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
            // lambda、if、switch、typeOf、seq
            if (currentToken is WordToken kw)
            {
                switch (kw.Content)
                {
                    case Keywords.FUNC:
                        return DelegateLambdaParsing(context.GetLocation().Start, context);
                    case Keywords.ASYNC:
                        // async lambda 语法为 func{async (...)...}（SYNTAX §5），
                        // async 不是表达式起始关键字
                        context.RaiseError(
                            "async lambda 写作 func{async (...)...}，'async' 不能出现在表达式起始位置");
                        break;
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
                    case Keywords.PLACEOF:
                        if (!allowPrefixUnary)
                            context.RaiseError("连续的一元运算符必须用括号明确嵌套关系");
                        var placeOfNode = new PlaceOfExpressionASTNode();
                        StartSpan(placeOfNode, currentToken.CharRange.Start, context);
                        currentExpression = placeOfNode;
                        state = State.PrimaryParsed;
                        // 与其他前缀表达式一致，不引入运算符优先级。
                        allowBinaryOperator = false;
                        return new ParserLayerResult.PushLayer(
                            new ExpressionParserLayer(placeOfNode.Operand)
                            {
                                allowBinaryOperator = false,
                                allowPrefixUnary = false,
                                insideParens = insideParens,
                                allowBareReturn = allowBareReturn
                            }, TokenDisposition.Consume);
                    case Keywords.SEQ:
                    case Keywords.VOLATILE:
                    case Keywords.UNSAFE:
                        // seq 块可以作为表达式使用（单表达式隐式值，或多语句
                        // return@_/return@标签；匿名默认标签为 _，SYNTAX §6.1）
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
                // 负号折叠（SYNTAX §3.3）：前缀 - 直接作用于整数字面量时并入
                // 字面量（-2147483648 可达 i32 下界），先进入候选状态判别；
                // 其他一元运算符（!/not/await）维持既有路径
                if (currentToken is NotationToken minus && minus.Content == "-")
                {
                    pendingMinusStart = currentToken.CharRange.Start;
                    state = State.MinusCandidate;
                    return ParserLayerResult.Continue.Instance;
                }
                if (!allowPrefixUnary)
                {
                    context.RaiseError(
                        "连续的一元运算符必须用括号明确嵌套关系，如 not (not x)");
                }
                return HandlePrefixUnary(currentToken, context);
            }

            // 5. 路径表达式（SYNTAX §1.4）：符号起点（变量/函数/命名空间/类型路径），
            // 本层直接施工 PathExpression（M42 起不再委托 PathParserLayer——
            // 类型引用与 import 的符号路径仍由它负责）
            if (currentToken is WordToken word && Keywords.IsIdentifierStart(word.Content))
            {
                var path = new PathExpressionASTNode();
                path.Head.Name = word.Content;
                StartSpan(path, currentToken.CharRange.Start, context);
                path.Head.Span = currentToken.CharRange;
                currentExpression = path;
                state = State.PrimaryParsed;
                return ParserLayerResult.Continue.Instance;
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
                new LiteralParserLayer(literalExpr) { allowBareReturn = allowBareReturn },
                TokenDisposition.Replay);
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

        // 委托 lambda 表达式解析（func 已消费；async 标记由 LambdaExpressionParserLayer
        // 在 { 之后识别）；keywordStart 为 func 关键字起点：span 含起始关键字（M31）
        private ParserLayerResult DelegateLambdaParsing(
            CharPosition keywordStart, ParserLayerContext context)
        {
            var lambdaNode = new LambdaExpressionASTNode();
            StartSpan(lambdaNode, keywordStart, context);
            return DelegateStructuredParsing(lambdaNode, new LambdaExpressionParserLayer(lambdaNode));
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

        // ===== 负号折叠（SYNTAX §3.3）=====
        // 前缀 - 直接作用于整数字面量时折叠进字面量：AST 得负值 IntLiteral，
        // 不产生 Opposite 一元节点；范围检查按目标类型完整有符号区间
        // （-2147483648 合法、2147483648 超上限报错、-1U 不能为负）。
        // 不折叠情形（维持 Opposite 现状）：作用对象非整数字面量
        // （-x、-(5)、-1.5、-2e3 等）、二元减号（不经本路径）。

        // - 已读：判别紧随 token——整数词进入折叠确认，其余回退普通一元负路径
        private ParserLayerResult HandleMinusCandidate(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is WordToken word && NumericLiteral.IsNumericWord(word.Content)
                && !IsScientificWord(word.Content))
            {
                pendingMinusWord = word;
                state = State.MinusIntCandidate;
                return ParserLayerResult.Continue.Instance;
            }

            return DelegateMinusUnary(context, TokenDisposition.Replay);
        }

        // - 与整数词已读：遇 . 说明是浮点（-3.14，Lexer 把浮点切成多 token），
        // 回退普通一元负路径并把已消费的整数词作为种子还给字面量层；
        // 其余 token 确认整数，折叠后以新状态重派当前 token
        private ParserLayerResult HandleMinusIntCandidate(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken dot && dot.Content == ".")
            {
                var seed = pendingMinusWord!.Content;
                pendingMinusWord = null;
                return DelegateMinusUnary(context, TokenDisposition.Replay, seed);
            }

            FoldPendingMinus(context);
            return ParseToken(currentToken, context);
        }

        // 折叠施工：负号并入已读整数词，产出负值 IntLiteral 作为当前主表达式
        private void FoldPendingMinus(ParserLayerContext context)
        {
            var word = pendingMinusWord!;
            pendingMinusWord = null;
            if (!NumericLiteral.TryParseInt(word.Content, out var value, out var intType,
                    out var numBase, out var error, negative: true))
            {
                context.RaiseError(error!);
            }

            // span 覆盖负号到数字词全文
            var literalExpr = new LiteralExpressionASTNode();
            var intNode = new IntLiteralASTNode(literalExpr)
            {
                Value = value,
                IntType = intType,
                Base = numBase
            };
            literalExpr.AttachLiteral(intNode);
            literalExpr.Span = new CharRange
            {
                Start = pendingMinusStart,
                End = word.CharRange.End,
                sourceName = word.CharRange.sourceName
            };
            intNode.Span = literalExpr.Span;
            currentExpression = literalExpr;
            state = State.PrimaryParsed;
        }

        // 普通一元负（Opposite）路径：创建一元节点并下钻操作数层。
        // seed 为负号折叠浮点回退时已被本层消费的整数词（如 -3.14 的 3）
        private ParserLayerResult DelegateMinusUnary(
            ParserLayerContext context, TokenDisposition disposition, string? seed = null)
        {
            // 操作数非整数字面量时负号仍是一元运算符：连续一元限制在此生效
            // （- -5 内层是折叠字面量不受影响；- -x 保持报错）
            if (!allowPrefixUnary)
            {
                context.RaiseError(
                    "连续的一元运算符必须用括号明确嵌套关系，如 not (not x)");
            }

            var unaryExpr = new UnaryExpressionASTNode()
            {
                Operator = "-",
                IsPrefix = true
            };
            StartSpan(unaryExpr, pendingMinusStart, context);
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
                allowBareReturn = allowBareReturn,
                seedNumericWord = seed
            };

            return new ParserLayerResult.PushLayer(operandLayer, disposition);
        }

        // 科学计数法词判定（折叠候选排除）：无进制前缀且含 e/E 的词按浮点
        // 现状路径处理（2e3 完整指数、2e 待吸收指数；-2e3 不折叠）
        private static bool IsScientificWord(string content)
        {
            return !NumericLiteral.HasBasePrefix(content) &&
                   (content.Contains('e') || content.Contains('E'));
        }

        // ===== 路径后缀链施工（M42）=====

        // 确保当前表达式是路径形态：已是 PathExpression 直接返回；
        // 否则（字面量/分组/其他表达式遇上路径后缀）把当前表达式包装为
        // 路径的表达式底座（Head.Expression）
        private PathExpressionASTNode EnsurePathExpression(ParserLayerContext context)
        {
            if (currentExpression is PathExpressionASTNode existing)
            {
                return existing;
            }
            // 底座表达式到此为止（后缀不属于它），封口后包入路径首段
            SealCurrentExpression(context);
            var path = new PathExpressionASTNode();
            var root = new ExpressionRootASTNode(path.Head);
            root.Attach(currentExpression!);
            path.Head.Expression = root;
            // 底座已封口（Span 必有值）：首段 span 即底座范围
            path.Head.Span = currentExpression!.Span;
            WrapSpan(path, currentExpression!, context);
            currentExpression = path;
            return path;
        }

        // 路径当前生长点：最后一段；无段时为首段
        private static ASTNode CurrentPathOwner(PathExpressionASTNode path)
        {
            return path.Segments.Count > 0 ? path.Segments[^1] : (ASTNode)path.Head;
        }

        private static List<PathSuffixASTNode> SuffixesOf(ASTNode owner) => owner switch
        {
            PathHeadASTNode head => head.Suffixes,
            PathSegmentASTNode segment => segment.Suffixes,
            _ => throw new CompilerInternalException("非法路径生长点: " + owner.GetType().Name),
        };

        private static List<TypeReferenceASTNode> GenericArgumentsOf(ASTNode owner) => owner switch
        {
            PathHeadASTNode head => head.GenericArguments,
            PathSegmentASTNode segment => segment.GenericArguments,
            _ => throw new CompilerInternalException("非法路径生长点: " + owner.GetType().Name),
        };

        // 追加一个路径后缀（调用 () 或索引 []），实参列表委托 ArgumentListParserLayer
        private PathSuffixASTNode StartSuffix(PathExpressionASTNode path, PathSuffixKind kind,
            ParserLayerContext context)
        {
            var owner = CurrentPathOwner(path);
            var suffix = new PathSuffixASTNode(owner) { Kind = kind };
            StartSpan(suffix, context.GetLocation().Start, context);
            SuffixesOf(owner).Add(suffix);
            currentPathTail = suffix;
            return suffix;
        }

        // 主表达式已解析：检查后续操作
        private ParserLayerResult HandlePrimaryParsed(Token currentToken, ParserLayerContext context)
        {
            // 整数字面量成员访问回退（SYNTAX §3.3）：字面量层已把 '7.' 的 '.'
            // 记在节点上并交还标识符——视作路径连接符已读，直接按成员名处理
            // （7.twice() ≡ (7).twice()）
            if (currentExpression is LiteralExpressionASTNode literalWithDot &&
                literalWithDot.MemberAccessDotConsumed &&
                currentToken is WordToken)
            {
                literalWithDot.MemberAccessDotConsumed = false;
                EnsurePathExpression(context);
                pendingConnector = PathConnector.Dot;
                pendingSuffixStart = literalWithDot.MemberAccessDotRange.Start;
                state = State.MemberNameExpected;
                return HandleMemberNameExpected(currentToken, context);
            }

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

            // 泛型实参列表刚闭合：紧邻的多余 '>' 是畸形闭合，不是比较/移位运算符
            // （foo\<i32>>(...) 不得静默解析为 foo\<i32> > (...)；
            // 合法比较须与闭合符隔开空白：foo\<i32> > x）
            if (justClosedGenericArgs)
            {
                justClosedGenericArgs = false;
                if (currentToken is NotationToken extraClose && extraClose.Content == ">" &&
                    extraClose.CharRange.Start.offset == genericCloseEnd.offset)
                {
                    context.RaiseError("Unexpected extra '>' after generic argument list");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                }
            }

            // 路径后缀链（SYNTAX.md §1.4：路径表达式在运算符之前整体形成；
            // M42：一条完整路径链恰一个 PathExpressionASTNode）
            if (currentToken is NotationToken suffix)
            {
                // new 表达式的构造参数列表不生成路径后缀（紧邻 new Type 的首个 (）
                if (suffix.Content == "(" && currentExpression is NewExpressionASTNode newExpr)
                {
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            newExpr.Arguments, ArgumentListParserLayer.BracketKind.Round, newExpr)
                        {
                            allowBareReturn = allowBareReturn
                        },
                        TokenDisposition.Consume);
                }

                // 调用后缀 (
                if (suffix.Content == "(")
                {
                    var path = EnsurePathExpression(context);
                    var callSuffix = StartSuffix(path, PathSuffixKind.Call, context);
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            callSuffix.Arguments, ArgumentListParserLayer.BracketKind.Round, callSuffix)
                        {
                            allowBareReturn = allowBareReturn
                        },
                        TokenDisposition.Consume);
                }

                // 索引后缀 [
                if (suffix.Content == "[")
                {
                    var path = EnsurePathExpression(context);
                    var indexSuffix = StartSuffix(path, PathSuffixKind.Index, context);
                    return new ParserLayerResult.PushLayer(
                        new ArgumentListParserLayer(
                            indexSuffix.Arguments, ArgumentListParserLayer.BracketKind.Square, indexSuffix)
                        {
                            allowBareReturn = allowBareReturn
                        },
                        TokenDisposition.Consume);
                }

                // 成员路径连接符 .
                if (suffix.Content == ".")
                {
                    EnsurePathExpression(context);
                    pendingConnector = PathConnector.Dot;
                    pendingSuffixStart = context.GetLocation().Start;
                    state = State.MemberNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 安全访问 ?.
                if (suffix.Content == "?")
                {
                    EnsurePathExpression(context);
                    pendingSuffixStart = context.GetLocation().Start;
                    state = State.SafeDotExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // wrapper 访问连接符 :（SYNTAX §14.1；链式 obj:A:B 逐段左结合）
                if (suffix.Content == ":")
                {
                    EnsurePathExpression(context);
                    pendingConnector = PathConnector.Colon;
                    pendingSuffixStart = context.GetLocation().Start;
                    state = State.MemberNameExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // 泛型实参 \<（挂在路径当前生长点：首段符号头或末段成员，
                // 如 foo\<i32>、a.b\<i32>.c、foo().bar\<i32>）
                // 注意：泛型实参列表是当前路径段的延续，此处不封口
                if (suffix.Content == "\\")
                {
                    if (currentExpression is not PathExpressionASTNode)
                    {
                        context.RaiseError(
                            "Generic arguments are only allowed on symbols or member access, " +
                            $"got: {currentExpression?.GetType().Name}");
                    }
                    state = State.GenericAngleExpected;
                    return ParserLayerResult.Continue.Instance;
                }

                // trailing lambda（SYNTAX §5.2）：
                // expr{...} 脱糖为以 lambda 为唯一实参的调用后缀，如 list.map{...}
                if (suffix.Content == "{")
                {
                    var path = EnsurePathExpression(context);
                    var callSuffix = StartSuffix(path, PathSuffixKind.Call, context);
                    var lambdaNode = new LambdaExpressionASTNode();
                    // lambda 作为唯一实参直接挂入实参的 Root（未挂载节点，可安全 Attach）；
                    // LambdaExpressionParserLayer 之后原地填充该 lambda 节点
                    var argument = new ArgumentASTNode(callSuffix);
                    argument.Value.Attach(lambdaNode);
                    // 实参 span 起点 = { 处；lambda 弹栈后由 SealCurrentExpression 封口
                    StartSpan(argument, context.GetLocation().Start, context);
                    pendingTrailingArgument = argument;
                    callSuffix.Arguments.Add(argument);
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

            // if? 空值回退（S7f，SYNTAX §3.4）：中缀位置的 if 只可能是 if?
            // 运算符（if 表达式在 Initial 态分支识别，不冲突）；两 token 重组
            // （if + ?），参照 as? 的重组模式
            if (currentToken is WordToken ifWord && ifWord.Content == Keywords.IF)
            {
                if (!allowBinaryOperator)
                {
                    context.RaiseError(
                        "Rigi 没有运算符优先级：运算符 'if?' 必须用括号明确运算顺序");
                }
                SealCurrentExpression(context);
                pendingOperator = "if?";
                state = State.IfNullFallbackSeen;
                return ParserLayerResult.Continue.Instance;
            }

            // 检查二元运算符
            if (IsBinaryOperator(currentToken))
            {
                if (!allowBinaryOperator)
                {
                    context.RaiseError(
                        $"Rigi 没有运算符优先级：运算符 '{GetOperatorString(currentToken)}' " +
                        "必须用括号明确运算顺序");
                }

                // 左操作数到此为止（运算符不属于它），封口
                SealCurrentExpression(context);
                pendingOperator = GetOperatorString(currentToken);
                pendingOperatorEnd = currentToken.CharRange.End;
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
                (nt.Content == ">" || nt.Content == "=") &&
                IsAdjacentToPendingOperator(currentToken))
            {
                string combined = pendingOperator + nt.Content;
                if (combined is ">=" or ">>" or ">>>")
                {
                    pendingOperator = combined;
                    pendingOperatorEnd = currentToken.CharRange.End;
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
                IsCompoundAssignmentOperator(pendingOperator) &&
                IsAdjacentToPendingOperator(assign))
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

        // 中缀 if 已读：期待 ? 组成 if?（S7f 空值回退；中缀 if 的唯一合法形态）
        private ParserLayerResult HandleIfNullFallbackSeen(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken q && q.Content == "?")
            {
                state = State.OperatorSeen;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError(
                $"Expected '?' after 'if' (if? null fallback operator), got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 二元表达式已完成：再来运算符就违反"无运算符优先级"规则
        private ParserLayerResult HandleCompleted(Token currentToken, ParserLayerContext context)
        {
            if (IsBinaryOperator(currentToken)
                || (currentToken is WordToken ifWord && ifWord.Content == Keywords.IF))
            {
                context.RaiseError(
                    $"Rigi 没有运算符优先级：运算符 '{GetOperatorString(currentToken)}' " +
                    "必须用括号明确运算顺序");
            }

            return CompleteExpression(context, TokenDisposition.Replay);
        }

        // 路径连接符（. / ?. / :）之后：读取成员名，追加路径段
        private ParserLayerResult HandleMemberNameExpected(Token currentToken, ParserLayerContext context)
        {
            // 成员名不能是数字词（M31：3.14.15、foo.123 此前被接受；只查首字符）
            // 冒号后是 wrapper 名，true/false 等保留字不能冒充名称。
            if (currentToken is WordToken name && Keywords.IsIdentifierStart(name.Content)
                && (pendingConnector != PathConnector.Colon || Keywords.IsIdentifier(name.Content)))
            {
                var path = (PathExpressionASTNode)currentExpression!;
                var segment = new PathSegmentASTNode(path)
                {
                    Connector = pendingConnector,
                    Name = name.Content,
                };
                // 段 span：连接符起点到成员名尾（泛型实参到来时再扩，见 AttachGenericArguments）
                var current = context.GetLocation();
                segment.Span = new CharRange
                {
                    Start = pendingSuffixStart,
                    End = current.End,
                    sourceName = current.sourceName,
                };
                path.Segments.Add(segment);
                pendingConnector = PathConnector.Dot;
                state = State.PrimaryParsed;
                return ParserLayerResult.Continue.Instance;
            }

            var what = pendingConnector == PathConnector.Colon ? "wrapper name" : "member name";
            var connector = pendingConnector == PathConnector.Colon ? ":" : ".";
            context.RaiseError($"Expected {what} after '{connector}', got: {currentToken}");
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

        // \ 之后：必须是 < ，开始解析泛型实参列表
        private ParserLayerResult HandleGenericAngleExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == "<")
            {
                var arg = new TypeReferenceASTNode(CurrentPathOwner(
                    (PathExpressionASTNode)currentExpression!));
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
                    var arg = new TypeReferenceASTNode(CurrentPathOwner(
                        (PathExpressionASTNode)currentExpression!));
                    pendingGenericArgs.Add(arg);
                    return new ParserLayerResult.PushLayer(
                        new TypeReferenceParserLayer(arg), TokenDisposition.Consume);
                }

                if (nt.Content == ">")
                {
                    AttachGenericArguments(context);
                    justClosedGenericArgs = true;
                    genericCloseEnd = context.GetLocation().End;
                    state = State.PrimaryParsed;
                    return ParserLayerResult.Continue.Instance;
                }
            }

            context.RaiseError($"Expected ',' or '>' in generic argument list, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 把收集到的泛型实参挂到路径当前生长点（首段符号头或末段成员）
        private void AttachGenericArguments(ParserLayerContext context)
        {
            var path = (PathExpressionASTNode)currentExpression!;
            var owner = CurrentPathOwner(path);
            // 泛型只能直接跟在名字之后：表达式底座（(a+b)、foo() 的结果）
            // 与已带后缀的首段/段都不是泛型附着点
            var attachable = SuffixesOf(owner).Count == 0
                && owner is not PathHeadASTNode { Expression: not null };
            if (!attachable)
            {
                context.RaiseError(
                    "Generic arguments are only allowed on symbols or member access, " +
                    $"got: {currentExpression?.GetType().Name}");
            }
            GenericArgumentsOf(owner).AddRange(pendingGenericArgs);
            pendingGenericArgs.Clear();
            // 泛型实参列表的 > 属于本段：以当前 token 封 End
            if (owner.Span is { } segmentSpan)
            {
                segmentSpan.End = context.GetLocation().End;
                owner.Span = segmentSpan;
            }
            if (currentExpression?.Span is { } s)
            {
                s.End = context.GetLocation().End;
                currentExpression.Span = s;
            }
        }
        private ParserLayerResult HandleSafeDotExpected(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is NotationToken nt && nt.Content == ".")
            {
                pendingConnector = PathConnector.SafeDot;
                state = State.MemberNameExpected;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '.' after '?' for safe member access, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
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
            // 负号折叠候选在 EOF 处确认（- 与整数词后无更多 token）：折叠后按完成收尾
            if (state == State.MinusIntCandidate)
            {
                FoldPendingMinus(context);
                return CompleteExpression(context, TokenDisposition.Replay);
            }

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
                // SYNTAX §13.2 固定运算符表只定义一元 -（opposite）/!（bitwiseNot）
                // 与 not/await 关键字——无一元 +，`+x` 落入意外 token 报错
                return nt.Content == "-" || nt.Content == "!";
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

        // 重组相邻性判定：pending 运算符的末 token End 与当前 token 的 Start
        // 必须同位置（offset 比较最直接）——夹空白则按两个独立 token 走正常
        // 流程（`a > = b` 中 = 落入右操作数层报意外 token，解析错误）
        private bool IsAdjacentToPendingOperator(Token token)
        {
            return token.CharRange.Start.offset == pendingOperatorEnd.offset;
        }

        private string GetOperatorString(Token token)
        {
            if (token is NotationToken nt) return nt.Content;
            if (token is WordToken wt) return wt.Content;
            return "";
        }

        // 括号语境下换行透明的状态：等待主表达式、主表达式已解析（可能接运算符或 )）、
        // 等待右操作数、二元表达式已完成（可能接 ) 或犯错报优先级）；
        // 负号折叠候选（MinusCandidate/MinusIntCandidate）与 Initial 同性质，一并豁免
        private static bool IsLineBreakTransparentState(State state)
        {
            return state == State.Initial ||
                   state == State.PrimaryParsed ||
                   state == State.OperatorSeen ||
                   state == State.MinusCandidate ||
                   state == State.MinusIntCandidate ||
                   state == State.Completed;
        }
    }
}
