using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LatteCompiler
{
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
        public const char SINGLE_LINE_CMT_SIGN = '#';
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
        public const string INCREMENT = "++";
        public const string DECREMENT = "--";
        public const string MULTIPLY_ASSIGN = "*=";
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
    SINGLE_LINE_CMT_SIGN,
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
    INCREMENT,
    DECREMENT,
    MULTIPLY_ASSIGN,
    LEFT_SHIFT,
    ARROW
    // 注意：> 系列（>=、>>、>>>）刻意不参与多字符合并。
    // 嵌套泛型的连续闭合符（如 List<Map<String, i32>>）需要独立的 > token；
    // >=、>>、>>> 运算符由 ExpressionParserLayer 在运算符状态下重新组合。
    // 斜杠家族（/、/=、//、/*）一律由 SlashLexerLayer 分流，不在此合并。
};
    }
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

    public enum TokenType
    {
        Word,
        Comment,
        String,
        LineBreak,
        Notation,
        EndOfFile
    }

    public class WordToken : Token
    {
        public WordToken(String content)
        {
            Content = content;
        }
        public override string Content { get; set; }

        public override TokenType Type { get; } = TokenType.Word;
    }

    public class CommentToken : Token
    {
        public CommentToken(String content)
        {
            Content = content;
        }
        public override string Content { get; set; }

        public override TokenType Type { get; } = TokenType.Comment;
    }

    public class StringToken : Token
    {
        public StringToken(String content)
        {
            Content = content;
        }
        public override string Content { get; set; }

        public override TokenType Type { get; } = TokenType.String;
    }

    public class LineBreakToken : Token
    {

        public override string Content { get; set; } = "\n";

        public override TokenType Type { get; } = TokenType.LineBreak;
    }

    // 文件结束 token（EOF 正式 Token，不再用换行伪装）：
    // 由 Parser 在输入 token 列表的本地副本末尾追加；只由 RootParserLayer 消费。
    // 非 Root Layer 收到 EOF 时：语法结构已完整则 PopLayer(Replay) 层层上交，
    // 不完整则抛出 "Unexpected end of file"。
    public sealed class EndOfFileToken : Token
    {
        public override string Content
        {
            get => "";
            set { }
        }

        public override TokenType Type => TokenType.EndOfFile;
    }

    public class NotationToken : Token
    {
        public NotationToken(String content)
        {
            Content = content;
        }
        public override string Content { get; set; }

        public override TokenType Type { get; } = TokenType.Notation;
    }

    public class LexerException : Exception
    {
        public LexerException(string message) : base(message) { }
    }

    public class ParserException : Exception
    {
        public ParserException(string message) : base(message) { }
    }

    // AST 节点基类。节点类型一律用 CLR 类型判断（is / GetType()），
    // 不再有 ASTNodeType 枚举。子节点成员以 [ChildAstNode] 标注、
    // 父指针以 [ParentAstNode] 标注，供 ASTIntegrityValidator 反射遍历校验。
    public abstract class ASTNode
    {
        // 父节点只能设置一次：构造函数传入，或通过 AttachTo（供 ExpressionRootASTNode.Attach
        // 挂载未挂载表达式子树、DeclarationParserLayer 延迟挂接注解）。
        // 二次设置直接抛异常；禁止任何形式的重挂 Parent。
        [ParentAstNode]
        public ASTNode? Parent { get; private set; }

        // 源码范围（M28）：诊断用。Parser 施工时填充（层目标由主循环按 token 流回填，
        // 层内自建节点由所在层显式设置）；null = 未设置。
        // Validator 校验每个节点均有合法 Span（ExpressionRootASTNode 可透明继承内容表达式）。
        public virtual CharRange? Span { get; set; }

        protected ASTNode(ASTNode? parent)
        {
            Parent = parent;
        }

        // 把一个尚未拥有父节点的节点挂载到 parent（仅限一次）
        internal void AttachTo(ASTNode parent)
        {
            ArgumentNullException.ThrowIfNull(parent);

            if (Parent is not null)
            {
                throw new InvalidOperationException(
                    "AST node already has a parent.");
            }

            Parent = parent;
        }
    }
    public class RootASTNode : ASTNode
    {
        // 顶层条目容器：全局声明、import、namespace（以及测试驱动的顶层字面量表达式）
        [ChildAstNode] public List<ASTNode> Declarations = new List<ASTNode>();

        public RootASTNode() : base(null){ }
    }
    public class SymbolElement
    {
        public string name = "";
        public SymbolSet generics = new();

        public SymbolElement()
        {
        }
    }
    public class Symbol
    {
        public SymbolElementSet elements = new();
    }
    public class SymbolElementSet : List<SymbolElement>{ }
    public class SymbolSet : List<Symbol> { }
    public class SymbolASTNode : ASTNode
    {
        public SymbolASTNode(ASTNode? parent) : base(parent){ }

        public Symbol symbol = new();
    }

    // import 列表项（SYNTAX §15.2）：携带一个符号路径节点。
    // 不是 ASTNode（struct），以 [AstCarrier] 标注；配合 ImportASTNode.importedSymbols
    // 上的 [ChildAstNode]，Validator 会深入本类型公共字段，把 symbolNode
    // 视为 ImportASTNode 的子节点校验。
    [AstCarrier]
    public struct ImportItem
    {
        public SymbolASTNode symbolNode;
        public bool importAll;
    }
    public class ImportASTNode : ASTNode
    {
        [ChildAstNode] public List<ImportItem> importedSymbols = new();
        public ImportASTNode(ASTNode? parent) : base(parent){ }
    }

    public abstract class Token
    {
        public CharRange CharRange = new();
        public abstract string Content { get; set; }
        public abstract TokenType Type { get; }
        public override string ToString()
        {
            return $"[{Type} {Content}]";
        }
    }
}
