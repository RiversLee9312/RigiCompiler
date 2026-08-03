using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 函数级发射上下文（P4b）：一个 fn 定义发射期间存活的可变状态。
    // 每个函数新建一个实例（同 BindContext/LowerContext 原则）。
    internal sealed class EmitContext
    {
        public EmitContext(BilFunction function)
        {
            Function = function;
        }

        // 当前函数的 BIL 定义（.args/.vars/Blocks 填充目标）
        public BilFunction Function { get; }

        // 当前函数的临时变量（编译器保留名 .t0/.t1...，§5.1：用户标识符
        // 不得以 . 开头，与用户变量零冲突）；指令生成中登记，.vars 收尾输出
        public List<BilVarDeclaration> TempVars { get; } = new List<BilVarDeclaration>();

        public int TempCount { get; set; }

        // 分支 block 编号（函数内唯一递增）
        public int IfCount { get; set; }

        public int LoopCount { get; set; }

        public int SwitchCount { get; set; }

        public int SeqCount { get; set; }

        public int TryCount { get; set; }
    }
}
