namespace RigiCompiler
{
    // 只读降级环境（P4a）：全编译期不变。诊断统一经 Error 落袋（P4 phase）。
    internal sealed class LowerEnvironment
    {
        public LowerEnvironment(CompilationUnit unit)
        {
            Unit = unit;
        }

        public CompilationUnit Unit { get; }

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P4, span, message);
        }
    }
}
