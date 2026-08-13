using System;

namespace RigiCompiler
{
    // 关键字常量：Lexer 不区分关键字（一律输出 WordToken），
    // 由 Parser 各 Layer 比对这里的常量识别。
    // 与 SYNTAX.md §19 的关键字一览保持一致（M31 大扫除：
    // 删除 elif/foreach/when/case/private/public/final/extension/base 幽灵词）。
    public static class Keywords
    {
        // ===== 控制流关键字（§19）=====
        public const string IF = "if";
        public const string ELSE = "else";
        public const string SWITCH = "switch";
        public const string DEFAULT = "default";
        public const string FOR = "for";
        public const string IN = "in";
        public const string TO = "to";
        public const string WHILE = "while";
        public const string DO = "do";
        public const string BREAK = "break";
        public const string CONTINUE = "continue";
        public const string RETURN = "return";
        public const string YIELD = "yield";
        public const string TRY = "try";
        public const string CATCH = "catch";
        public const string FINALLY = "finally";
        public const string THROW = "throw";
        // ===== 声明关键字（§19）=====
        public const string FUNC = "func";
        public const string VAR = "var";
        public const string CONST = "const";
        public const string CLASS = "class";
        public const string STRUCT = "struct";
        public const string INTERFACE = "interface";
        public const string ENUM = "enum";
        public const string WRAPPER = "wrapper";
        public const string OPERATOR = "operator";
        public const string INIT = "init";
        // ===== 模块关键字（§19；RootParserLayer 有专属层，不参与声明路由）=====
        public const string NAMESPACE = "namespace";
        public const string IMPORT = "import";
        // ===== 修饰符关键字（§19）=====
        public const string PUB = "pub";
        public const string PRIV = "priv";
        public const string PROTECTED = "protected";
        public const string INTERNAL = "internal";
        public const string OPEN = "open";
        public const string ABSTRACT = "abstract";
        public const string SINGLETON = "singleton";
        public const string STATIC = "static";
        public const string EXT = "ext";
        public const string OVERRIDE = "override";
        public const string NAMED = "named";
        public const string RICH = "rich";
        public const string SHARED = "shared";
        public const string ASYNC = "async";
        public const string NATIVE = "native";
        // ===== 运算符关键字（§19）=====
        public const string AND = "and";
        public const string OR = "or";
        public const string NOT = "not";
        public const string IS = "is";
        public const string SUPERS = "supers";
        public const string AS = "as";
        public const string WITH = "with";
        public const string NEW = "new";
        public const string TYPEOF = "typeOf";
        public const string AWAIT = "await";
        // ===== 类型关键字（§19）=====
        public const string EXTENDS = "extends";
        public const string IMPLEMENTS = "implements";
        public const string LIKE = "like";
        // ===== 其他关键字（§19）=====
        public const string THIS = "this";
        public const string SELF = "self";
        public const string INNER = "inner";
        public const string TRUE = "true";
        public const string FALSE = "false";
        public const string NULL = "null";
        public const string SEQ = "seq";
        public const string USING = "using";
        // ===== 非 §19 的上下文词汇 =====
        public const string VOLATILE = "volatile";      // seq 块修饰（§6）
        public const string OUT = "out";                // 泛型型变（§3.6）
        public const string VALUE = "value";            // 属性访问器参数（§9.4）
        public const string GET = "get";                // 属性访问器（§9.4）
        public const string SET = "set";                // 属性访问器（§9.4）
        public const string PROXY = "proxy";            // wrapper .proxy.* 代理成员名（§14.2）

        // ===== 保留字数组（用途：标识符位置的保留字拦截）=====

        // 控制流关键字数组
        public static readonly string[] ControlStreamKeywords =
        {
            IF, ELSE, SWITCH, DEFAULT, FOR, IN, TO, WHILE, DO,
            BREAK, CONTINUE, RETURN, YIELD, TRY, CATCH, FINALLY, THROW
        };

        // 声明路由关键字数组（RootParserLayer 用以识别声明起点；
        // import/namespace 有专属 ParserLayer，不在此列）
        public static readonly string[] DeclarationKeywords =
        {
            FUNC, VAR, CONST, CLASS, STRUCT, INTERFACE, ENUM, WRAPPER,
            OPERATOR, INIT
        };

        // 字符串运算符数组（运算符关键字）
        public static readonly string[] StringOperators =
        {
            AND, OR, NOT, IS, SUPERS, AS, WITH, NEW, TYPEOF, AWAIT
        };

        // 其他关键字数组（§19 其他关键字 + 模块关键字：保留字拦截用）
        public static readonly string[] StringValues =
        {
            NULL, TRUE, FALSE, THIS, SELF, INNER, SEQ, USING, NAMESPACE, IMPORT
        };

        // 声明修饰符数组（§19 修饰符关键字 + §4.4 的 ext；NAMED/SEQ/USING 是
        // 结构词而非修饰符，不在此列）
        public static readonly string[] DeclarationDescriptors =
        {
            PUB, PRIV, PROTECTED, INTERNAL, OPEN, ABSTRACT, SINGLETON,
            STATIC, EXT, OVERRIDE, RICH, SHARED, ASYNC, NATIVE
        };

        // 类型关键字（class, struct, interface, wrapper, enum）
        public static readonly string[] TypeKeywords =
        {
            CLASS, STRUCT, INTERFACE, WRAPPER, ENUM
        };

        // 辅助方法
        public static bool IsDescriptor(string word)
        {
            return Array.IndexOf(DeclarationDescriptors, word) >= 0;
        }

        public static bool IsTypeKeyword(string word)
        {
            return Array.IndexOf(TypeKeywords, word) >= 0;
        }

        // 标识符首字符检查（M31 统一：词法上数字也是 WordToken，
        // 命名位置必须拒绝数字词与保留字）
        public static bool IsIdentifierStart(string word)
        {
            return word.Length > 0 && (char.IsLetter(word[0]) || word[0] == '_');
        }

        // 保留字判定（M31 统一）：标识符位置不得使用的关键字
        public static bool IsReservedKeyword(string word)
        {
            return Array.IndexOf(ControlStreamKeywords, word) >= 0 ||
                   Array.IndexOf(DeclarationKeywords, word) >= 0 ||
                   Array.IndexOf(StringOperators, word) >= 0 ||
                   Array.IndexOf(StringValues, word) >= 0 ||
                   Array.IndexOf(DeclarationDescriptors, word) >= 0;
        }

        // 合法标识符判定：首字符合法且非保留字（M31 统一，供各命名位置使用）
        public static bool IsIdentifier(string word)
        {
            return IsIdentifierStart(word) && !IsReservedKeyword(word);
        }
    }
}
