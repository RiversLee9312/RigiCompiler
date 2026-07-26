using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace LatteCompiler
{ 
    public static class Helper
    {
        public static void PrintTokenList(List<Token> tokens)
        {
            foreach (Token token in tokens) {
                if (token.Type == TokenType.LineBreak) {
                    Console.WriteLine($"[{TokenType.LineBreak}]");
                }
                else
                {
                    Console.Write(token.ToString());
                }
            }
        }
        public static void PrintASTNode(object node)
        {
            PrintRecursive(node, 0, "", new HashSet<object>());
        }

        private static void PrintRecursive(object obj, int indentLevel, string propName, HashSet<object> visited)
        {
            // 生成缩进字符串
            string indent = new string(' ', indentLevel * 2);
            string prefix = string.IsNullOrEmpty(propName) ? "" : $"{propName}: ";

            // 1. 处理 null
            if (obj == null)
            {
                Console.WriteLine($"{indent}{prefix}null");
                return;
            }

            Type type = obj.GetType();

            // 2. 处理基础类型 (String, Int, Boolean, Enum 等)
            // 这些类型直接打印值，不需要展开
            if (type.IsPrimitive || obj is string || type.IsEnum )
            {
                // 如果是简单值，直接打印 "属性名: 值"
                Console.WriteLine($"{indent}{prefix}{obj}");
                return;
            }

            // 3. 处理循环引用 (防止栈溢出)
            if (visited.Contains(obj))
            {
                Console.WriteLine($"{indent}{prefix}[Circular Reference: {type.Name}]");
                return;
            }
            visited.Add(obj);

            // 打印当前节点类型名称
            Console.WriteLine($"{indent}{prefix}{type.Name}");

            // 4. 处理集合 (List, Array 等)
            if (obj is IEnumerable collection)
            {
                int index = 0;
                foreach (var item in collection)
                {
                    // 递归打印集合中的每一项
                    PrintRecursive(item, indentLevel + 1, $"[{index}]", visited);
                    index++;
                }
                return;
            }
            
            // 5. 处理复杂对象 (AST 节点) - 使用反射遍历属性
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in fields)
            {
                // 可选：跳过某些不需要打印的属性，例如 "Parent" 指针，防止干扰
                if (prop.Name == nameof(ASTNode.parent)) continue;

                object value;
                try
                {
                    value = prop.GetValue(obj);
                }
                catch
                {
                    value = "<Error getting value>";
                }
                PrintRecursive(value, indentLevel + 1, prop.Name, visited);
            }
            PropertyInfo[] props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var prop in props)
            {
                // 可选：跳过某些不需要打印的属性，例如 "Parent" 指针，防止干扰
                if (prop.Name == nameof(ASTNode.parent)) continue;

                object value;
                try
                {
                    value = prop.GetValue(obj);
                }
                catch
                {
                    value = "<Error getting value>";
                }
                PrintRecursive(value, indentLevel + 1, prop.Name, visited);
            }
        }
    }
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
        public const string BLOCK_CMT_START_SIGN = "/*";
        public const string BLOCK_CMT_END_SIGN = "*/";
        public const char ASTERISK = '*';
        public const char PLUS = '+';
        public const char MINUS = '-';
        public const char FORWARD_SLASH = '/';
        public const char BACK_SLASH = '\\';
        public const char DOUBLE_QUOTATION_MARK = '"';
        public const char DOLLAR_SYMBOL = '$';
        public const char SINGLE_QUOTATION_MARK = '\'';
        public const char UNDERSCORE = '_';
        public const string GREATER_THAN_OR_EQUAL = ">=";
        public const string LESS_THAN_OR_EQUAL = "<=";
        public const string EQUAL = "==";
        public const char EXCLAMATION = '!';
        public const string NOT_EQUAL = "!=";
        public const string INCREMENT = "++";
        public const string DECREMENT = "--";
        public const string MULTIPLY_ASSIGN = "*=";
        public const string DIVIDE_ASSIGN = "/=";
        public const char VERTICAL_BAR = '|';
        public const char TILDE = '~';
        public const char L_ANGLE = '<';
        public const char R_ANGLE = '>';
        public const char CARET = '^';
        public const char AND = '&';
        public const char PERCENT = '%';
        public const string RIGHT_SHIFT = ">>";
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
    BLOCK_CMT_START_SIGN,
    BLOCK_CMT_END_SIGN,
    LESS_THAN_OR_EQUAL,
    EQUAL,
    NOT_EQUAL,
    INCREMENT,
    DECREMENT,
    MULTIPLY_ASSIGN,
    DIVIDE_ASSIGN,
    LEFT_SHIFT,
    ARROW
    // 注意：> 系列（>=、>>、>>>）刻意不参与多字符合并。
    // 嵌套泛型的连续闭合符（如 List<Map<String, i32>>）需要独立的 > token；
    // >=、>>、>>> 运算符由 ExpressionParserLayer 在运算符状态下重新组合。
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
        Notation
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

    public enum ASTNodeType
    {
        AssignStatement,
        InvokeStatemnt,
        IfStatement,
        SwitchStatement,
        TwoValueExpression,
        AcquisitionExpression,
        NumberValue,
        BoolValue,
        StringValue,
        Declaration,
        ImportStatement,
        CodeBlockExpression,
        Root,
        Symbol,
        OneValueExpression,
        ReceiverExpressionRoot,
        ValueExpressionRoot,
        NewExpression,
        LambdaExpression,
        Argument,
        IfExpression,
        SwitchExpression,
        TypeOfExpression,
        CastExpression,
        TypeCheckExpression,
        LoopStatement,
        ReturnStatement,
        LoopControlStatement,
        RangeExpression,
        TryCatchFinallyStatement,
        CatchClause,
        SeqBlockExpression,
        UsingBinding,
        ThrowStatement,
        YieldStatement,
        CallableDeclaration,
        ClassDeclaration,
        InterfaceDeclaration,
        StructDeclaration,
        EnumStructDeclaration,
        EnumCase,
        WrapperDeclaration,
        PropertyAccessor,
        Annotation,
        EnumCaseExpression,
        WrapperAccess,
        NamespaceDeclaration
    }
    public class AcquisitionExpressionASTNode : ASTNode
    {
        public AcquisitionExpressionASTNode(ASTNode? parent) : base(parent)
        {
            sourceSymbol = new(this);
            wrapperSymbols = new();
        }
        public override ASTNodeType NodeType { get; } = ASTNodeType.AcquisitionExpression;
        public SymbolASTNode sourceSymbol;
        public List<SymbolASTNode> wrapperSymbols;
    }

    public class ParserException : Exception
    {
        public ParserException(string message) : base(message) { }
    }

    public abstract class ASTNode
    {
        public abstract ASTNodeType NodeType { get; }
        public ASTNode(ASTNode? parent)
        {
            this.parent = parent;
        }

        public ASTNode? parent;

        // 子声明容器（全局作用域、类型体、嵌套类型共用同一个容器）
        public List<ASTNode> Children = new List<ASTNode>();

        // 注解 / wrapper 应用列表（SYNTAX §14.5）；仅声明节点使用，其余节点保持空
        public List<AnnotationASTNode> Annotations = new List<AnnotationASTNode>();
    }
    public class RootASTNode : ASTNode
    {
        public override ASTNodeType NodeType { get; } = ASTNodeType.Root;

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

        public override ASTNodeType NodeType { get; }= ASTNodeType.Symbol;
        public Symbol symbol = new();
    }
    public struct ImportItem
    {
        public SymbolASTNode symbolNode;
        public string? alias;
        public bool importAll;
    }
    public class ImportASTNode : ASTNode
    {
        
        public List<ImportItem> importedSymbols = new();
        public ImportASTNode(ASTNode? parent) : base(parent){ }
        
        public override ASTNodeType NodeType { get; } = ASTNodeType.ImportStatement;

    }

    public abstract class Token
    {
        public CharRange CharRange = new();
        public abstract string Content { get; set; }
        public abstract TokenType Type { get; }
        public override bool Equals(object? obj)
        {
            if (obj == null)
            {
                return false;
            }
            else
            {
                if (!(obj is Token))
                {
                    return false;
                }
                else
                {
                    var token = (Token)obj;
                    return (token.Content == Content) && (token.Type == Type);
                }
            }
        }
        public override int GetHashCode()
        {
            return $"{Type}({Content})".GetHashCode();
        }
        public override string ToString()
        {
            return $"[{Type} {Content}]";
        }
    }
}
