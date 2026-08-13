using RigiCompiler.Bil;

namespace RigiCompiler
{
    // 函数级发射上下文（P4b）：一个 fn 定义发射期间存活的可变状态。
    // 每个函数新建一个实例（同 BindContext/LowerContext 原则）。
    //
    // 组件化结构（M65 Lowering 侧组件化拆分，同 BindContext 先例）：
    // 本类是组合根——Function 留根部（.args/.vars/Blocks 填充目标），
    // Temps（临时变量表 .tN）与 BlockIds（分支 block 编号分配器）
    // 两组件随组合根同生同灭。
    internal sealed class EmitContext
    {
        public EmitContext(BilFunction function)
        {
            Function = function;
        }

        // 当前函数的 BIL 定义（.args/.vars/Blocks 填充目标）
        public BilFunction Function { get; }

        // 临时变量表（.tN 工厂 + .vars 收尾输出源）
        public TempVarTable Temps { get; } = new TempVarTable();

        // 分支 block 编号分配器（if/loop/switch/seq/try）
        public BlockIdAllocator BlockIds { get; } = new BlockIdAllocator();
    }
}
