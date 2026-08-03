namespace LatteCompiler
{
    // 只读绑定环境（VISITOR_REWRITE.md §3）：全编译期不变，作为固定参数
    // 传入所有 visitor。诊断统一经 Error 落袋（P3 phase；可恢复诊断模型）。
    internal sealed class BindEnvironment
    {
        public BindEnvironment(CompilationUnit unit, DeclarationCollection declarations)
        {
            Unit = unit;
            Declarations = declarations;
            Names = new NameResolver(unit, DiagnosticPhase.P3);
        }

        public CompilationUnit Unit { get; }

        public DeclarationCollection Declarations { get; }

        // 类型引用/符号路径解析（P2 同设施，本环境以 DiagnosticPhase.P3 实例化）
        public NameResolver Names { get; }

        public BootstrapSymbols B => Unit.Symbols.Bootstrap;

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P3, span, message);
        }
    }
}
