using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 发射环境（P4b，模块级）：整个发射期存活的共享状态。
    internal sealed class EmitEnvironment
    {
        public EmitEnvironment(CompilationUnit unit, string moduleName)
        {
            Unit = unit;
            ModuleName = moduleName;
            Module = new BilModule();
        }

        public CompilationUnit Unit { get; }

        public string ModuleName { get; }

        // 构建中的模块（LocalSymbols/Resources/Functions 逐段填充）
        public BilModule Module { get; }

        // 资源去重表（模块级，跨 fn 共享——§18.4 switch-table 等同元素
        // 序列资源跨 fn 去重）：键 = (BIL 资源类型关键字, 字面量原文)
        public Dictionary<(string TypeKeyword, string LiteralText), string> ResourceKeys { get; } =
            new Dictionary<(string, string), string>();

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P4, span, message);
        }
    }
}
