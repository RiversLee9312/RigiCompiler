using RigiCompiler.Bil;

namespace RigiCompiler
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

        // 资源去重表（模块级，跨 fn 共享——§19.4 switch-table 等同元素
        // 序列资源跨 fn 去重；M57 起按资源种类分表，值为资源对象）：
        // 标量键 = (类型, 字面量原文)；null 键 = 元素类型 canonical；
        // switch-table 键 = selector 类型引用 + 元素序列；
        // catch-table 键 = 元素文本序列（含 block id）
        public Dictionary<(BilScalarType Type, string LiteralText), BilScalarResource> ScalarKeys
            { get; } = new Dictionary<(BilScalarType, string), BilScalarResource>();
        public Dictionary<string, BilNullResource> NullKeys { get; } =
            new Dictionary<string, BilNullResource>();
        public Dictionary<string, BilSwitchTableResource> SwitchTableKeys { get; } =
            new Dictionary<string, BilSwitchTableResource>();
        public Dictionary<string, BilCatchTableResource> CatchTableKeys { get; } =
            new Dictionary<string, BilCatchTableResource>();

        public void Error(CharRange? span, string message)
        {
            Unit.Diagnostics.Error(DiagnosticPhase.P4, span, message);
        }
    }
}
