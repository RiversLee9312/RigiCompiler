using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RigiCompiler
{
    /// <summary>
    /// 字符串转义表（单行/多行字符串共用，SYNTAX §3.3）：
    /// \ 后的字符 → 实际字符；未知转义返回 false（由调用方带位置报错）。
    /// </summary>
    internal static class StringEscape
    {
        public static bool TryProcess(char currentChar, out char value)
        {
            switch (currentChar)
            {
                case Notations.DOLLAR_SYMBOL: value = Notations.DOLLAR_SYMBOL; return true;
                case 'a': value = '\a'; return true;
                case 'b': value = '\b'; return true;
                case 't': value = '\t'; return true;
                case 'n': value = '\n'; return true;
                case 'v': value = '\v'; return true;
                case 'f': value = '\f'; return true;
                case 'r': value = '\r'; return true;
                case Notations.SINGLE_QUOTATION_MARK: value = Notations.SINGLE_QUOTATION_MARK; return true;
                case Notations.DOUBLE_QUOTATION_MARK: value = Notations.DOUBLE_QUOTATION_MARK; return true;
                case Notations.BACK_SLASH: value = Notations.BACK_SLASH; return true;
                default: value = '\0'; return false;
            }
        }
    }
}
