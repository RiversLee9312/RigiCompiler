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
            FractionalPart,     // 已读取小数部分
            Completed           // 解析完成
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

        // EOF 处理：已读整数部分（含 `3.` 形态）按整数字面量收尾；Initial 为不完整结构
        private ParserLayerResult HandleEndOfFile(ParserLayerContext context)
        {
            if (state == ParserState.IntegerPart || state == ParserState.DotSeen)
            {
                var intNode = ParseIntegerLiteral(integerPart, context);
                AddLiteralToTarget(intNode);
                return new ParserLayerResult.PopLayer(TokenDisposition.Replay);
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

                    // 数字字面量 - 检查是否包含小数点
                    if (IsNumericToken(word.Content))
                    {
                        // 如果 token 本身就包含小数点，直接解析为浮点数
                        if (word.Content.Contains('.'))
                        {
                            var floatNode = ParseFloatLiteral(word.Content, context);
                            AddLiteralToTarget(floatNode);
                            return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
                        }

                        // 否则，可能是整数或浮点数的整数部分
                        integerPart = word.Content;
                        state = ParserState.IntegerPart;
                        return ParserLayerResult.Continue.Instance;
                    }

                    context.RaiseError($"Unexpected word token for literal: {word.Content}");
                    break;

                case StringToken str:
                    AddLiteralToTarget(ParseStringLiteral(str));
                    return new ParserLayerResult.PopLayer(TokenDisposition.Consume);

                case NotationToken notation when notation.Content == "'":
                    context.RaiseError("Character literal parsing not yet implemented");
                    break;

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
            if (currentToken is WordToken word && IsNumericToken(word.Content))
            {
                fractionalPart = word.Content;
                state = ParserState.FractionalPart;

                // 组合成完整的浮点数
                string fullNumber = integerPart + "." + fractionalPart;
                var floatNode = ParseFloatLiteral(fullNumber, context);
                AddLiteralToTarget(floatNode);
                return new ParserLayerResult.PopLayer(TokenDisposition.Consume);
            }

            // 小数点后不是数字，这可能是成员访问而不是浮点数
            // 将整数部分作为整数字面量，保留 "." token
            var intNode = ParseIntegerLiteral(integerPart, context);
            AddLiteralToTarget(intNode);
            context.LogWarning($"Parsed as integer followed by '.', not float. " +
                             $"Use explicit notation if float intended.");
            return new ParserLayerResult.PopLayer(TokenDisposition.Replay); // 保留当前 token（即 "." 后的 token）
        }

        // 判断是否为数字 token
        private bool IsNumericToken(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;

            // 十六进制
            if (content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return content.Length > 2;

            // 去除可能的后缀
            string withoutSuffix = content.TrimEnd('L', 'l', 'S', 's', 'B', 'b', 'U', 'u', 'F', 'f');
            if (withoutSuffix.Length == 0) return false;

            // 去除可能的两字符后缀（UL, US, UB）
            if (withoutSuffix.Length >= 2)
            {
                string lastTwo = withoutSuffix.Substring(withoutSuffix.Length - 2).ToUpper();
                if (lastTwo == "UL" || lastTwo == "US" || lastTwo == "UB")
                {
                    withoutSuffix = withoutSuffix.Substring(0, withoutSuffix.Length - 2);
                }
            }

            // 检查是否包含小数点（浮点数格式）
            if (withoutSuffix.Contains('.'))
            {
                var parts = withoutSuffix.Split('.');
                if (parts.Length != 2) return false;
                return parts[0].All(c => char.IsDigit(c)) && parts[1].All(c => char.IsDigit(c));
            }

            // 检查剩余部分是否全为数字（整数格式）
            return withoutSuffix.All(c => char.IsDigit(c));
        }

        // 解析整数字面量
        private IntLiteralASTNode ParseIntegerLiteral(string content, ParserLayerContext context)
        {
            // 检查后缀
            string suffix = "";
            string numberPart = content;

            bool isHex = content.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

            if (isHex)
            {
                // 十六进制：先扫描 hex 数字部分，剩余的才是后缀
                // （否则 0xFF 末尾的 F 会被误判为后缀，得到错误的值 15）
                int hexEnd = 2;
                while (hexEnd < content.Length && IsHexDigit(content[hexEnd])) hexEnd++;
                suffix = content.Substring(hexEnd).ToUpper();
                numberPart = content.Substring(0, hexEnd);
            }
            // 识别后缀（UL, US, UB, L, S, B, U, f）
            else if (content.Length > 2 && content.EndsWith("UL", StringComparison.OrdinalIgnoreCase))
            {
                suffix = content.Substring(content.Length - 2).ToUpper();
                numberPart = content.Substring(0, content.Length - 2);
            }
            else if (content.Length > 2 && content.EndsWith("US", StringComparison.OrdinalIgnoreCase))
            {
                suffix = content.Substring(content.Length - 2).ToUpper();
                numberPart = content.Substring(0, content.Length - 2);
            }
            else if (content.Length > 2 && content.EndsWith("UB", StringComparison.OrdinalIgnoreCase))
            {
                suffix = content.Substring(content.Length - 2).ToUpper();
                numberPart = content.Substring(0, content.Length - 2);
            }
            else if (content.Length > 1)
            {
                char lastChar = content[content.Length - 1];
                if ("LlSsBbUuFf".Contains(lastChar))
                {
                    suffix = lastChar.ToString().ToUpper();
                    numberPart = content.Substring(0, content.Length - 1);
                }
            }

            long value;

            try
            {
                if (isHex)
                {
                    value = Convert.ToInt64(numberPart.Substring(2), 16);
                }
                else
                {
                    value = long.Parse(numberPart);
                }
            }
            catch (Exception ex)
            {
                context.RaiseError($"Invalid integer literal: {numberPart} - {ex.Message}");
                throw;
            }

            IntType intType = suffix switch
            {
                "L" => IntType.I64,
                "S" => IntType.I16,
                "B" => IntType.I8,
                "U" => IntType.U32,
                "UL" => IntType.U64,
                "US" => IntType.U16,
                "UB" => IntType.U8,
                "F" => IntType.I32, // F 后缀在整数上无意义，当作 i32
                _ => IntType.I32
            };

            return new IntLiteralASTNode(targetNode)
            {
                Value = value,
                IntType = intType,
                IsHex = isHex
            };
        }

        // 判断字符是否为十六进制数字
        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
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
            string content = str.Content;
            bool hasInterpolation = content.Contains("${");

            return new StringLiteralASTNode(targetNode)
            {
                Value = content,
                HasInterpolation = hasInterpolation
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
