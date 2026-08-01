using System;

namespace LatteCompiler
{
    // Token 定义：Lexer 的输出契约（Parser 的输入）。
    // 关键字不是独立 Token 类型——以 WordToken 形式出现，由 Parser 比对 Keywords 常量识别。

    public enum TokenType
    {
        Word,
        Comment,
        String,
        Char,
        LineBreak,
        Notation,
        InterpolationStart,
        InterpolationEnd,
        EndOfFile
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

    // 插值开始标记（${，M53 词法帧机制）：字符串层遇未转义的 ${ 时产出
    // （span 覆盖 ${ 两字符），同时压基础层嵌套解析插值表达式；
    // Parser 由它驱动插值表达式段的委托解析（SYNTAX §3.8）
    public class InterpolationStartToken : Token
    {
        public override string Content
        {
            get => "${";
            set { }
        }

        public override TokenType Type { get; } = TokenType.InterpolationStart;
    }

    // 插值结束标记（配平 }，M53）：插值帧内大括号计数归零时由驱动把
    // 该 } 记号改发为本类型（原 } NotationToken 不入流），span 沿用该记号
    public class InterpolationEndToken : Token
    {
        public override string Content
        {
            get => "}";
            set { }
        }

        public override TokenType Type { get; } = TokenType.InterpolationEnd;
    }

    // 字符字面量 token（'...'，SYNTAX §3.3）：Value 为转义展开后的字符；
    // char 无插值概念，不复用 StringToken
    public class CharToken : Token
    {
        public CharToken(char value)
        {
            Value = value;
        }

        public char Value { get; }

        // Content 即 Value 的字符串形式（ToString/日志用；字符值以 Value 为准）
        public override string Content
        {
            get => Value.ToString();
            set { }
        }

        public override TokenType Type { get; } = TokenType.Char;
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

    // 文件结束 token（EOF 正式 Token，不再用换行伪装）：
    // 由 Lexer.Tokenize 在输出 token 列表末尾追加（M25；Parser 对绕过 Lexer
    // 手工构造 token 流的调用方保持末尾追加兼容）；只由 RootParserLayer 消费。
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
}
