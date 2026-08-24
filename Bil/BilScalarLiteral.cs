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

        // char 资源：单引号包围，解码后必须恰一个字符（UTF-16 码元）
        public static char DecodeChar(string literalText)
        {
            if (literalText.Length < 2 || literalText[0] != '\'' || literalText[^1] != '\'')
            {
                throw new FormatException("非法字符资源字面量：" + literalText);
            }
            var inner = Unescape(literalText.Substring(1, literalText.Length - 2));
            if (inner.Length != 1)
            {
                throw new FormatException("字符资源不是单字符：" + literalText);
            }
            return inner[0];
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
