using System;
using System.Globalization;
using System.Text;

namespace RigiCompiler.Bil
{
    // §19.1 标量资源字面量的唯一解码点（BIL 词法知识归 Bil/ 层；
    // VM 与 Middleware Emit 都是消费方，避免两处各自实现漂移）。
    // 非法输入抛 FormatException，调用方按需包装为本层异常。
    public static class BilScalarLiteral
    {
        // string 资源：LiteralText 含首尾引号与转义，解码为实际字符序列
        public static string DecodeString(string literalText)
        {
            if (literalText.Length < 2 || literalText[0] != '"' || literalText[^1] != '"')
            {
                throw new FormatException("非法字符串资源字面量：" + literalText);
            }
            return Unescape(literalText.Substring(1, literalText.Length - 2));
        }

        // char 资源：单引号包围，解码后必须恰好一个 Unicode 标量
        // （U+0000–U+10FFFF 排除 U+D800–U+DFFF；char 32 位标量，STDLIB §4.3.1）。
        // 补充平面标量可来自 \u{...} 转义展开的代理对，或 BIL 文本直写的
        // 代理对字符——两者在此统一合成标量；孤立代理拒绝。
        public static uint DecodeChar(string literalText)
        {
            if (literalText.Length < 2 || literalText[0] != '\'' || literalText[^1] != '\'')
            {
                throw new FormatException("非法字符资源字面量：" + literalText);
            }
            var inner = Unescape(literalText.Substring(1, literalText.Length - 2));
            // 码元流 → 标量：恰一个非代理码元，或恰一对合法代理
            if (inner.Length == 1 && !char.IsSurrogate(inner[0]))
            {
                return inner[0];
            }
            if (inner.Length == 2
                && char.IsHighSurrogate(inner[0]) && char.IsLowSurrogate(inner[1]))
            {
                return 0x10000u
                    + (((uint)inner[0] - 0xD800u) << 10)
                    + ((uint)inner[1] - 0xDC00u);
            }
            throw new FormatException("字符资源不是单个 Unicode 标量：" + literalText);
        }

        // 有符号整族（i8..i64）统一解析入口
        public static long ParseSigned(string text)
        {
            return long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        // 无符号整族（u8..u64）统一解析入口
        public static ulong ParseUnsigned(string text)
        {
            return ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private static string Unescape(string escaped)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < escaped.Length; i++)
            {
                if (escaped[i] != '\\')
                {
                    sb.Append(escaped[i]);
                    continue;
                }
                if (i + 1 >= escaped.Length)
                {
                    throw new FormatException("字符串资源转义不完整");
                }
                i++;
                // \u{hex}（1–6 位十六进制）：BIL 标量资源通用转义，承载
                // 补充平面标量（EscapeChar 对 >0xFFFF 一律输出本形式）。
                // ≤0xFFFF 展开为单码元，>0xFFFF 展开为代理对（DecodeChar
                // 再合成标量；String 资源同样可读）。
                if (escaped[i] == 'u' && i + 1 < escaped.Length && escaped[i + 1] == '{')
                {
                    var close = escaped.IndexOf('}', i + 2);
                    if (close < 0 || close - i - 2 is < 1 or > 6)
                    {
                        throw new FormatException("非法 \\u{} 转义");
                    }
                    var hex = escaped.Substring(i + 2, close - i - 2);
                    if (!uint.TryParse(hex, NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var scalar)
                        || scalar > 0x10FFFFu)
                    {
                        throw new FormatException("非法 \\u{} 码点：\\u{" + hex + "}");
                    }
                    sb.Append(char.ConvertFromUtf32(unchecked((int)scalar)));
                    i = close;
                    continue;
                }
                sb.Append(escaped[i] switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    '$' => '$',
                    'a' => '\a',
                    'b' => '\b',
                    't' => '\t',
                    'n' => '\n',
                    'v' => '\v',
                    'f' => '\f',
                    'r' => '\r',
                    _ => throw new FormatException("未知转义 \\" + escaped[i]),
                });
            }
            return sb.ToString();
        }
    }
}
