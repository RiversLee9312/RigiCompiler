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
        LineBreak,
        Notation,
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
