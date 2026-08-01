using System;
using System.Globalization;

namespace LatteCompiler
{
    // 字面量解析器层 - 使用状态机处理多个 token 组成的字面量
    // 施工协议：构造函数接收 LiteralExpressionASTNode 目标，
    // 解析出的字面量节点直接 AttachLiteral 到该目标，不产生任何返回值
    public class LiteralParserLayer : IParserLayer, ISpanReceiver
    {
        private readonly LiteralExpressionASTNode targetNode;
        // 已创建的字面量节点（ReceiveSpan 时与包装节点一并回填 span）
        private LiteralASTNode? createdLiteral = null;
        // 裸 return 边界标记（M33 传染链）：插值表达式里的 lambda 体沿用
        // 宿主函数/lambda 的边界（S7f 起本层经子解析创建 ExpressionParserLayer）
        internal bool allowBareReturn = true;

        // 状态机状态
        private enum ParserState
        {
            Initial,            // 初始状态，等待第一个 token
            IntegerPart,        // 已读取整数部分
            DotSeen,            // 已看到小数点
            FractionalPart,     // 已读取小数部分
            AwaitInterpolationStartOrDone,  // 已收文本段：等待 InterpolationStart
                                            // （插值模式）或其他（收尾）
            InterpolationEndExpected,       // 插值表达式已委托：等待 InterpolationEnd
            AwaitSegmentOrStartOrDone       // 插值段之间：文本段/又一 InterpolationStart/收尾
        }

        private ParserState state = ParserState.Initial;
        private string integerPart = "";
        private string fractionalPart = "";
        // 当前暂存的文本段 token（M53 插值段序列模式）：等待后续
        // InterpolationStartToken 判别插值模式，或按普通单段字符串收尾
        private StringToken? pendingSegment = null;

        public LiteralParserLayer(LiteralExpressionASTNode target)
        {
            targetNode = target;
        }

        // 层弹出时回填施工目标与字面量节点的源码范围（M28）
        public void ReceiveSpan(CharRange span)
        {
            targetNode.Span ??= span;
            if (createdLiteral != null)
            {
                createdLiteral.Span ??= span;
            }
        }

        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            // EOF：整数/浮点半途状态按已读部分收尾并上交 EOF；尚未读到内容则报错
            if (currentToken is EndOfFileToken)
            {
                return HandleEndOfFile(context);
            }

            switch (state)
            {
                case ParserState.Initial:
                    return HandleInitialState(currentToken, context);

                case ParserState.IntegerPart:
                    return HandleIntegerPartState(currentToken, context);

                case ParserState.DotSeen:
                    return HandleDotSeenState(currentToken, context);

                case ParserState.AwaitInterpolationStartOrDone:
                    return HandleAwaitInterpolationStartOrDone(currentToken, context);

                case ParserState.InterpolationEndExpected:
                    return HandleInterpolationEndExpected(currentToken, context);

                case ParserState.AwaitSegmentOrStartOrDone:
                    return HandleAwaitSegmentOrStartOrDone(currentToken, context);

                default:
                    context.RaiseError($"Invalid parser state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
        }

        // ===== 字符串插值段序列（M53，SYNTAX §3.8）=====
        // token 流形态：StringToken? (InterpolationStart 表达式 InterpolationEnd
        // StringToken?)*——Lexer 帧机制已保证配平；文本段解码后为空的跳过

        // 暂存当前文本段，等待 InterpolationStart 判别（或直接收尾）
        private ParserLayerResult HandleStringToken(StringToken str)
        {
            pendingSegment = str;
            state = ParserState.AwaitInterpolationStartOrDone;
            return ParserLayerResult.Continue.Instance;
        }

        private ParserLayerResult HandleAwaitInterpolationStartOrDone(Token currentToken,
            ParserLayerContext context)
        {
            if (currentToken is InterpolationStartToken)
            {
                // 插值模式：建插值字符串节点，暂存段入 parts，委托表达式段
                EnsureInterpolationMode();
                AddTextPart(pendingSegment!);
                pendingSegment = null;
                return DelegateInterpolationExpression();
            }
            // 无后续插值：插值模式内的字符串结束（暂存段入 parts 后收尾），
            // 或普通单段字符串收尾
            if (createdLiteral != null)
            {
                AddTextPart(pendingSegment!);
                pendingSegment = null;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }
            var node = new StringLiteralASTNode(targetNode) { Value = pendingSegment!.Content };
            pendingSegment = null;
            AddLiteralToTarget(node);
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
        }

        private ParserLayerResult HandleInterpolationEndExpected(Token currentToken,
            ParserLayerContext context)
        {
            if (currentToken is InterpolationEndToken)
            {
                state = ParserState.AwaitSegmentOrStartOrDone;
                return ParserLayerResult.Continue.Instance;
            }

            context.RaiseError($"Expected '}}' to close interpolation, got: {currentToken}");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        private ParserLayerResult HandleAwaitSegmentOrStartOrDone(Token currentToken,
            ParserLayerContext context)
        {
            if (currentToken is StringToken str)
            {
                // 与首段同路径：暂存待判（后续可能是又一插值或字符串结束）
                return HandleStringToken(str);
            }
            if (currentToken is InterpolationStartToken)
            {
                return DelegateInterpolationExpression();
            }
            // 字符串结束（换行/EOF/} 等）：Replay 上交，由父层收尾
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
        }

        // 插值模式入口（首个 InterpolationStart 到来时）：建插值字符串节点
        private void EnsureInterpolationMode()
        {
            if (createdLiteral != null) return;
            AddLiteralToTarget(new StringLiteralASTNode(targetNode)
            {
                Value = "",
                HasInterpolation = true,
                InterpolationParts = new List<StringInterpolationPart>()
            });
        }

        // 文本段入 parts：段级 LiteralExpression 子结构（与普通字符串字面量
        // 同构，P3/P4 复用字面量机器；span = 段 token span）；解码后为空的段跳过
        private void AddTextPart(StringToken segment)
        {
            if (segment.Content.Length == 0) return;
            var owner = (StringLiteralASTNode)createdLiteral!;
            var wrapper = new LiteralExpressionASTNode(owner);
            var literal = new StringLiteralASTNode(wrapper) { Value = segment.Content };
            wrapper.AttachLiteral(literal);
            wrapper.Span = segment.CharRange;
            literal.Span = segment.CharRange;
            owner.InterpolationParts!.Add(new StringInterpolationPart { Text = wrapper });
        }

        // 委托插值表达式段：ExpressionParserLayer 就地填充段 Root
        // （InterpolationStart 已消费；allowBareReturn 沿宿主上下文传染，M33）
        private ParserLayerResult DelegateInterpolationExpression()
        {
            var owner = (StringLiteralASTNode)createdLiteral!;
            var part = new StringInterpolationPart
            {
                Expression = new ExpressionRootASTNode(owner)
            };
            owner.InterpolationParts!.Add(part);
            state = ParserState.InterpolationEndExpected;
            return new ParserLayerResult.PushLayer(
                new ExpressionParserLayer(part.Expression) { allowBareReturn = allowBareReturn },
                TokenDisposition.Consume);
        }

        // EOF 处理：整数部分已读按整数字面量收尾；Initial/DotSeen 为不完整结构
        private ParserLayerResult HandleEndOfFile(ParserLayerContext context)
        {
            if (state == ParserState.IntegerPart)
            {
                var intNode = ParseIntegerLiteral(integerPart, context);
                AddLiteralToTarget(intNode);
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // 字符串在 AwaitInterpolationStartOrDone 遇 EOF：结构完整（插值
            // 配平由 Lexer 帧机制保证），按普通完成收尾（Replay 上交 EOF）
            if (state == ParserState.AwaitInterpolationStartOrDone
                && createdLiteral != null)
            {
                AddTextPart(pendingSegment!);
                pendingSegment = null;
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }
            if (state == ParserState.AwaitInterpolationStartOrDone)
            {
                var node = new StringLiteralASTNode(targetNode) { Value = pendingSegment!.Content };
                pendingSegment = null;
                AddLiteralToTarget(node);
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
            }

            // `3.` 后缺小数部分：报错（M31 起不再静默吞点按整数收尾）
            if (state == ParserState.DotSeen)
            {
                context.RaiseError("Expected digit after '.' in float literal");
            }

            context.RaiseError("Unexpected end of file");
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
        }

        // 处理初始状态
        private ParserLayerResult HandleInitialState(Token currentToken, ParserLayerContext context)
        {
            switch (currentToken)
            {
                case WordToken word:
                    // 布尔字面量
                    if (word.Content == Keywords.TRUE)
                    {
                        AddLiteralToTarget(new BoolLiteralASTNode(targetNode) { Value = true });
                        return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                    }
                    if (word.Content == Keywords.FALSE)
                    {
                        AddLiteralToTarget(new BoolLiteralASTNode(targetNode) { Value = false });
                        return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                    }

                    // null 字面量
                    if (word.Content == Keywords.NULL)
                    {
                        AddLiteralToTarget(new NullLiteralASTNode(targetNode));
                        return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                    }

                    // 数字字面量（Word 永不含 '.'——Lexer 把 3.14 切成三个 token，
                    // 浮点由 IntegerPart/DotSeen 状态组合；判定统一走 NumericLiteral）
                    if (NumericLiteral.IsNumericWord(word.Content))
                    {
                        integerPart = word.Content;
                        state = ParserState.IntegerPart;
                        return ParserLayerResult.Continue.Instance;
                    }

                    context.RaiseError($"Unexpected word token for literal: {word.Content}");
                    break;

                case StringToken str:
                    return HandleStringToken(str);

                // 插值开始标记（M53）："${x}" 等无首段文本的插值字符串
                case InterpolationStartToken:
                    EnsureInterpolationMode();
                    return DelegateInterpolationExpression();

                // 字符字面量（Lexer 已保证恰好一个字符或一个转义序列，SYNTAX §3.3）
                case CharToken ch:
                    AddLiteralToTarget(new CharLiteralASTNode(targetNode) { Value = ch.Value });
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);

                default:
                    context.RaiseError($"Unexpected token for literal: {currentToken}");
                    break;
            }

            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 处理已读取整数部分的状态
        private ParserLayerResult HandleIntegerPartState(Token currentToken, ParserLayerContext context)
        {
            // 检查是否为小数点
            if (currentToken is NotationToken notation && notation.Content == ".")
            {
                // 可能是浮点数，进入 DotSeen 状态
                state = ParserState.DotSeen;
                return ParserLayerResult.Continue.Instance;
            }

            // 不是小数点，说明是纯整数
            var intNode = ParseIntegerLiteral(integerPart, context);
            AddLiteralToTarget(intNode);
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay); // 保留当前 token
        }

        // 处理已看到小数点的状态
        private ParserLayerResult HandleDotSeenState(Token currentToken, ParserLayerContext context)
        {
            // 小数点后必须是数字
            if (currentToken is WordToken word && NumericLiteral.IsNumericWord(word.Content))
            {
                fractionalPart = word.Content;
                state = ParserState.FractionalPart;

                // 组合成完整的浮点数
                string fullNumber = integerPart + "." + fractionalPart;
                var floatNode = ParseFloatLiteral(fullNumber, context);
                AddLiteralToTarget(floatNode);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 小数点后不是数字：报错（M31 起不再吞掉 . 伪装成员访问；
            // `3.foo` 形态规范未定义，需要成员访问时请写 (3).foo）
            context.RaiseError("Expected digit after '.' in float literal");
            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        }

        // 解析整数字面量（规则统一走 NumericLiteral：0x/0b/0o 前缀、下划线、后缀）
        private IntLiteralASTNode ParseIntegerLiteral(string content, ParserLayerContext context)
        {
            if (!NumericLiteral.TryParseInt(content, out var value, out var intType, out var numBase, out var error))
            {
                context.RaiseError(error!);
            }

            return new IntLiteralASTNode(targetNode)
            {
                Value = value,
                IntType = intType,
                Base = numBase
            };
        }

        // 解析浮点数字面量
        private FloatLiteralASTNode ParseFloatLiteral(string fullNumber, ParserLayerContext context)
        {
            // 检查是否有 f/F 后缀
            bool isFloat = false;
            string numberPart = fullNumber;

            if (fullNumber.EndsWith("f", StringComparison.OrdinalIgnoreCase))
            {
                isFloat = true;
                numberPart = fullNumber.Substring(0, fullNumber.Length - 1);
            }

            // 下划线分隔（M31，与整数同规则）：整数/小数部分分别校验后剥离
            var dotIndex = numberPart.IndexOf('.');
            var intDigits = dotIndex >= 0 ? numberPart.Substring(0, dotIndex) : numberPart;
            var fracDigits = dotIndex >= 0 ? numberPart.Substring(dotIndex + 1) : "";
            if (!NumericLiteral.ValidateUnderscores(intDigits) ||
                !NumericLiteral.ValidateUnderscores(fracDigits))
            {
                context.RaiseError($"Invalid float literal: '{fullNumber}' " +
                                   "(underscore must appear singly between digits)");
            }
            numberPart = intDigits.Replace("_", "") + "." + fracDigits.Replace("_", "");

            double value;
            try
            {
                value = double.Parse(numberPart, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                context.RaiseError($"Invalid float literal: {numberPart} - {ex.Message}");
                throw;
            }

            return new FloatLiteralASTNode(targetNode)
            {
                Value = value,
                IsFloat = isFloat
            };
        }

        // 将字面量节点附加到施工目标（字面量节点的父节点即目标节点，一次性附加）
        private void AddLiteralToTarget(LiteralASTNode literalNode)
        {
            createdLiteral = literalNode;
            targetNode.AttachLiteral(literalNode);
        }
    }
}
