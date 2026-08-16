using System;
using System.Globalization;

namespace RigiCompiler
{
    /// <summary>
    /// 数字字面量的统一判定与解析（M31）：LiteralParserLayer / RootParserLayer /
    /// ExpressionParserLayer 共用一份，替代原先三份规则不一致的私有判定。
    /// 规则（SYNTAX §3.3）：
    /// - 进制：十进制（默认）、0x 十六进制、0b 二进制、0o 八进制（前缀大小写不敏感）
    /// - 下划线分隔：只允许数字之间的单个 _（不允许连续 _、开头或结尾的 _）
    /// - 后缀：L/S/B/U/UL/US/UB（大小写不敏感）
    /// </summary>
    public static class NumericLiteral
    {
        // 「是否按数字字面量路由」的判定：首字符为数字，或带 0x/0b/0o 进制前缀
        public static bool IsNumericWord(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            if (HasBasePrefix(content)) return true;
            return char.IsDigit(content[0]);
        }

        public static bool HasBasePrefix(string content)
        {
            return content.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                   content.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ||
                   content.StartsWith("0o", StringComparison.OrdinalIgnoreCase);
        }

        // 校验数字串（纯数字与 _ 组成）中的下划线用法：不允许连续 _、开头 _、结尾 _
        public static bool ValidateUnderscores(string digits)
        {
            if (digits.StartsWith('_') || digits.EndsWith('_')) return false;
            return !digits.Contains("__");
        }

        // 解析整数字面量：拆进制前缀与后缀、校验下划线、换算值、按后缀
        // 目标类型查范围（转换前拦截，不再漏到 VM 装载）。值一律以 decimal
        // 装载（128 位十进制，可精确覆盖 u64 全范围 0..18446744073709551615）。
        // negative = 负号折叠语境（一元 - 直接作用于本字面量，SYNTAX §3.3）：
        // 按目标类型的完整有符号区间检查负侧幅度（如 i32 允许 -2147483648）
        // 并把值取负；无符号类型在负语境下报「不能为负」。
        // 失败返回 false 并给出 error（调用方经 context.RaiseError 抛出）。
        public static bool TryParseInt(
            string content,
            out decimal value,
            out IntType type,
            out LiteralIntBase numBase,
            out string? error,
            bool negative = false)
        {
            value = 0;
            type = IntType.I32;
            numBase = LiteralIntBase.Decimal;
            error = null;

            // 1. 进制前缀与合法数字字符集
            int digitStart = 0;
            Func<char, bool> isDigit = char.IsDigit;
            int convertBase = 10;
            if (content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                numBase = LiteralIntBase.Hex;
                digitStart = 2;
                isDigit = IsHexDigit;
                convertBase = 16;
            }
            else if (content.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                numBase = LiteralIntBase.Binary;
                digitStart = 2;
                isDigit = c => c == '0' || c == '1';
                convertBase = 2;
            }
            else if (content.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                numBase = LiteralIntBase.Octal;
                digitStart = 2;
                isDigit = c => c >= '0' && c <= '7';
                convertBase = 8;
            }

            // 2. 扫描数字部分（含 _），剩余为后缀。
            // 注意：必须按进制字符集扫描而非后缀字母剥离——
            // 否则 0xFF 末尾的 F 会被误判为后缀（得到错误的值 15）
            int digitEnd = digitStart;
            while (digitEnd < content.Length && (isDigit(content[digitEnd]) || content[digitEnd] == '_'))
            {
                digitEnd++;
            }
            string digits = content.Substring(digitStart, digitEnd - digitStart);
            string suffix = content.Substring(digitEnd).ToUpperInvariant();

            if (digits.Length == 0)
            {
                error = $"Invalid integer literal: '{content}' (missing digits)";
                return false;
            }
            if (!ValidateUnderscores(digits))
            {
                error = $"Invalid integer literal: '{content}' " +
                        "(underscore must appear singly between digits)";
                return false;
            }

            // 3. 后缀 → 整数类型（SYNTAX §3.3 整数后缀全集只有
            // L/S/B/U/UL/US/UB；f/F 是浮点后缀，出现在整数上即非法）
            type = suffix switch
            {
                "" => IntType.I32,
                "L" => IntType.I64,
                "S" => IntType.I16,
                "B" => IntType.I8,
                "U" => IntType.U32,
                "UL" => IntType.U64,
                "US" => IntType.U16,
                "UB" => IntType.U8,
                _ => (IntType)(-1)
            };
            if ((int)type == -1)
            {
                error = $"Invalid integer literal suffix: '{suffix}' in '{content}'";
                return false;
            }

            // 4. 值换算：十进制直接 Parse；非十进制走 decimal 累乘加——
            // 无符号路径手算（Convert.ToInt64 对 0xFFFFFFFFFFFFFFFFUL 这类
            // u64 高位字面量会溢出）；超出 decimal 范围即报错
            string stripped = digits.Replace("_", "");
            try
            {
                value = convertBase == 10
                    ? decimal.Parse(stripped, NumberStyles.None, CultureInfo.InvariantCulture)
                    : Accumulate(stripped, convertBase);
            }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException)
            {
                error = $"Invalid integer literal: '{content}' " +
                        "(value exceeds representable range)";
                return false;
            }

            // 5. 后缀目标类型范围检查（此时还有源码 span 可报好位置）；
            // 字面量本身不带符号（负号是一元运算符），下限检查为防御性。
            // 负号折叠语境：允许负侧幅度到下界（|值| <= -min）并取负，
            // 由此可书写各符号类型下界（-2147483648、-128B 等）
            var (min, max, typeName) = RangeOf(type);
            if (negative)
            {
                if (min == 0)
                {
                    error = $"Invalid integer literal: '-{content}' " +
                            $"(negative value not allowed for {typeName})";
                    return false;
                }
                if (value > -min)
                {
                    error = $"Invalid integer literal: '-{content}' " +
                            $"(value out of range for {typeName})";
                    return false;
                }
                value = -value;
                return true;
            }
            if (value < min || value > max)
            {
                error = $"Invalid integer literal: '{content}' " +
                        $"(value out of range for {typeName})";
                return false;
            }
            return true;
        }

        // 非十进制累乘加（decimal 算术）：digit 字符已由各进制字符集校验过
        private static decimal Accumulate(string digits, int numBase)
        {
            decimal result = 0;
            foreach (var c in digits)
            {
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    _ => c - 'A' + 10,
                };
                result = result * numBase + digit;
            }
            return result;
        }

        // 后缀目标类型的范围表（decimal 常量）：i8/i16/i32/i64/u8/u16/u32/u64
        private static (decimal Min, decimal Max, string Name) RangeOf(IntType type) => type switch
        {
            IntType.I8 => (-128m, 127m, "i8"),
            IntType.I16 => (-32768m, 32767m, "i16"),
            IntType.I32 => (-2147483648m, 2147483647m, "i32"),
            IntType.I64 => (-9223372036854775808m, 9223372036854775807m, "i64"),
            IntType.U8 => (0m, 255m, "u8"),
            IntType.U16 => (0m, 65535m, "u16"),
            IntType.U32 => (0m, 4294967295m, "u32"),
            IntType.U64 => (0m, 18446744073709551615m, "u64"),
            _ => throw new CompilerInternalException("未知 IntType: " + type),
        };

        private static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') ||
                   (c >= 'a' && c <= 'f') ||
                   (c >= 'A' && c <= 'F');
        }
    }
}
