using System;

namespace RigiCompiler
{
    // 前端异常：用户源码错误（词法/语法），与 CompilerInternalException
    // （编译器内部错误，见 AST/ASTIntegrityValidator.cs）严格区分。

    public class LexerException : Exception
    {
        public LexerException(string message) : base(message) { }
    }

    public class ParserException : Exception
    {
        public ParserException(string message) : base(message) { }
    }
}
