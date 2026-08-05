using LatteCompiler.Bil;

namespace LatteCompiler
{
    // 临时变量表（M65 Lowering 侧组件化拆分，自 EmitContext 迁出）：
    // 当前函数的临时变量（编译器保留名 .t0/.t1...，§5.1：用户标识符
    // 不得以 . 开头，与用户变量零冲突）；指令生成中登记，.vars 收尾输出。
    internal sealed class TempVarTable
    {
        private readonly List<BilVarDeclaration> tempVars = new List<BilVarDeclaration>();

        private int tempCount;

        // 临时变量登记序列（只读暴露——EmittingDriver 收尾输出 .vars：
        // Locals 在前、临时变量在后）
        public IReadOnlyList<BilVarDeclaration> TempVars => tempVars;

        // 临时变量物化（§10.1/§10.3）：登记 .vars 条目并返回变量操作数
        public BilVariableOperand NewTemp(TypeSymbol type)
        {
            var name = ".t" + tempCount;
            tempCount++;
            tempVars.Add(new BilVarDeclaration(CanonicalSymbolPrinter.PrintType(type), name));
            return BilOp.Var(name);
        }
    }
}
