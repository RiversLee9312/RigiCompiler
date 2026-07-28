namespace LatteCompiler
{
    // 符号（notation）常量：Lexer 字符识别与 Parser 符号比对共用的单字符/多字符记号。
    public static class Notations
    {
        public const char L_CURLY_BRACE = '{';
        public const char R_CURLY_BRACE = '}';
        public const char L_SQUARE_BRACKET = '[';
        public const char R_SQUARE_BRACKET = ']';
        public const char L_ROUND_BRACKET = '(';
        public const char R_ROUND_BRACKET = ')';
        public const char COMMA = ',';
        public const char SEMICOLON = ';';
        public const char COLON = ':';
        public const char DOT = '.';
        public const char ASSIGN = '=';
        public const char ASTERISK = '*';
        public const char PLUS = '+';
        public const char MINUS = '-';
        public const char FORWARD_SLASH = '/';
        public const char BACK_SLASH = '\\';
        public const char DOUBLE_QUOTATION_MARK = '"';
        public const char DOLLAR_SYMBOL = '$';
        public const char SINGLE_QUOTATION_MARK = '\'';
        public const char UNDERSCORE = '_';
        public const string LESS_THAN_OR_EQUAL = "<=";
        public const string EQUAL = "==";
        public const char EXCLAMATION = '!';
        public const string NOT_EQUAL = "!=";
        public const char VERTICAL_BAR = '|';
        public const char TILDE = '~';
        public const char L_ANGLE = '<';
        public const char R_ANGLE = '>';
        public const char CARET = '^';
        public const char AND = '&';
        public const char PERCENT = '%';
        public const string LEFT_SHIFT = "<<";
        public const char QUESTION_MARK = '?';
        public const char AT_SIGN = '@';
        public const string ARROW = "->";
        public static readonly char[] CharNotations =
            [
    L_CURLY_BRACE,
    R_CURLY_BRACE,
    L_SQUARE_BRACKET,
    R_SQUARE_BRACKET,
    L_ROUND_BRACKET,
    R_ROUND_BRACKET,
    COMMA,
    SEMICOLON,
    COLON,
    DOT,
    '#',
    ASSIGN,
    ASTERISK,
    PLUS,
    MINUS,
    FORWARD_SLASH,
    BACK_SLASH,
    DOUBLE_QUOTATION_MARK,
    DOLLAR_SYMBOL,
    SINGLE_QUOTATION_MARK,
    UNDERSCORE,
    EXCLAMATION,
    VERTICAL_BAR,
    TILDE,
    L_ANGLE,
    R_ANGLE,
    CARET,
    AND,
    PERCENT,
    QUESTION_MARK,
    AT_SIGN
];
        public static readonly string[] StringNotations =
{
    LESS_THAN_OR_EQUAL,
    EQUAL,
    NOT_EQUAL,
    LEFT_SHIFT,
    ARROW
    // 注意：> 系列（>=、>>、>>>）刻意不参与多字符合并。
    // 嵌套泛型的连续闭合符（如 List<Map<String, i32>>）需要独立的 > token；
    // >=、>>、>>> 运算符由 ExpressionParserLayer 在运算符状态下重新组合。
    // 斜杠家族（/、//、/*）一律由 SlashLexerLayer 分流，不在此合并。
    // 复合赋值（*=、/=、+= 等）同样不合并：拆成两个 token，将来由 Parser 重组（M31）。
    // ++、-- 不属于 Latte 语法（SYNTAX 全文无此运算符），不再合并。
};
    }
}
