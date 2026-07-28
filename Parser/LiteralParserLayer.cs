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

        // 状态机状态
        private enum ParserState
        {
            Initial,            // 初始状态，等待第一个 token
            IntegerPart,        // 已读取整数部分
            DotSeen,            // 已看到小数点
            FractionalPart      // 已读取小数部分
        }

        private ParserState state = ParserState.Initial;
        private string integerPart = "";
        private string fractionalPart = "";

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

                default:
                    context.RaiseError($"Invalid parser state: {state}");
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }
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
                    AddLiteralToTarget(ParseStringLiteral(str));
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);

                // 注：单引号字符字面量由 Lexer 直接报错（未实现），不会到达这里

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

        // 解析字符串字面量
        private StringLiteralASTNode ParseStringLiteral(StringToken str)
        {
            // 插值标记以词法期判定为准（\$ 转义产出的字面 $ 不构成插值引导）
            return new StringLiteralASTNode(targetNode)
            {
                Value = str.Content,
                HasInterpolation = str.HasInterpolation
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
