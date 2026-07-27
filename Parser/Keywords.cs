using System;

namespace LatteCompiler
{
    // 关键字常量：Lexer 不区分关键字（一律输出 WordToken），
    // 由 Parser 各 Layer 比对这里的常量识别。
    public static class Keywords
    {
        public const string IMPORT = "import";
        //Control Stream
        public const string IF = "if";
        public const string ELSE = "else";
        public const string ELIF = "elif";
        public const string WHILE = "while";
        public const string FOR = "for";
        public const string FOREACH = "foreach";
        public const string WHEN = "when";
        public const string CASE = "case";
        public const string DEFAULT = "default";
        public const string TRY = "try";
        public const string CATCH = "catch";
        public const string FINALLY = "finally";
        public const string WITH = "with";
        //Declaration
        public const string VAR = "var";
        public const string CONST = "const";
        public const string FUNC = "func";
        public const string OPERATOR = "operator";
        public const string CLASS = "class";
        public const string WRAPPER = "wrapper";
        public const string INTERFACE = "interface";
        public const string STRUCT = "struct";
        //String operators
        public const string AND = "and";
        public const string OR = "or";
        public const string NOT = "not";
        public const string NEW = "new";
        public const string IS = "is";
        public const string IN = "in";
        public const string AS = "as";
        //Generic parameter keywords
        public const string EXTENDS = "extends";
        public const string SUPERS = "supers";
        public const string OUT = "out";
        public const string NAMED = "named";
        //String values
        public const string NULL = "null";
        public const string TRUE = "true";
        public const string FALSE = "false";
        public const string THIS = "this";
        public const string VALUE = "value";
        public const string INNER = "inner";
        public const string BASE = "base";
        //Declaration descriptors
        public const string PRIVATE = "private";
        public const string PUBLIC = "public";
        public const string FINAL = "final";
        public const string PROTECTED = "protected";
        public const string INTERNAL = "internal";
        public const string EXTENSION = "extension";
        public const string PROXY = "proxy";
        public const string ASYNC = "async";
        public const string SWITCH = "switch";
        public const string TYPEOF = "typeOf";
        public const string RETURN = "return";
        public const string BREAK = "break";
        public const string CONTINUE = "continue";
        public const string TO = "to";
        public const string DO = "do";
        public const string THROW = "throw";
        public const string SEQ = "seq";
        public const string USING = "using";
        public const string VOLATILE = "volatile";
        public const string AWAIT = "await";
        public const string YIELD = "yield";
        public const string ENUM = "enum";
        public const string OPEN = "open";
        public const string ABSTRACT = "abstract";
        public const string SINGLETON = "singleton";
        public const string SHARED = "shared";
        public const string RICH = "rich";
        public const string STATIC = "static";
        public const string OVERRIDE = "override";
        public const string IMPLEMENTS = "implements";
        public const string LIKE = "like";
        public const string EXT = "ext";
        public const string INIT = "init";
        public const string GET = "get";
        public const string SET = "set";
        public const string PUB = "pub";
        public const string PRIV = "priv";
        public const string NAMESPACE = "namespace";
        // 控制流关键字数组
        public static readonly string[] ControlStreamKeywords =
        {
    IF, ELSE, ELIF, WHILE, FOR, FOREACH, WHEN, CASE, DEFAULT, TRY, CATCH, FINALLY,WITH
};

        // 声明关键字数组
        public static readonly string[] DeclarationKeywords =
        {
    VAR, CONST, FUNC, OPERATOR, CLASS, WRAPPER, INTERFACE, STRUCT, ENUM
};

        // 字符串运算符数组
        public static readonly string[] StringOperators =
        {
    AND, OR, NOT, NEW, IS, IN, AS
};

        // 字符串值数组
        public static readonly string[] StringValues =
        {
    NULL, TRUE, FALSE, THIS, VALUE, INNER, BASE
};

        // 声明修饰符数组
        public static readonly string[] DeclarationDescriptors =
        {
    PRIVATE, PUBLIC, FINAL, PROTECTED, INTERNAL, EXTENSION, PROXY,
    PUB, PRIV, OPEN, ABSTRACT, SINGLETON, SHARED, RICH, STATIC, OVERRIDE, ASYNC, EXT
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
    }
}
