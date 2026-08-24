using System;

namespace RigiCompiler
{
    // 前端异常：用户源码错误（词法/语法），与 CompilerInternalException
    // （编译器内部错误）严格区分。

    public class LexerException : Exception
    {
        public LexerException(string message) : base(message) { }
    }

    public class ParserException : Exception
    {
        public ParserException(string message) : base(message) { }
    }

    // 内部编译器错误：「不可能发生」的编译器内部状态错误（AST 完整性验证
    // 失败、未覆盖的指令形态等），三端共用。与用户源码错误（LexerException/
    // ParserException/诊断）及 MwNotSupportedException（合法 BIL 超出现阶段
    // 实现面的受控失败）严格区分——抛出它即编译器自身有 bug。
    public class CompilerInternalException : Exception
    {
        public CompilerInternalException(string message) : base(message) { }
    }
}
