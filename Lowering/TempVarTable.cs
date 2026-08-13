using RigiCompiler.Bil;

namespace RigiCompiler
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

        // 临时变量物化（§10.1/§10.3）：登记 .vars 条目并返回变量操作数；
        // S9 放宽为 SemanticSymbol——泛型参数经 §7.5 投影（.generic<$.generic.T>）
        public BilVariableOperand NewTemp(SemanticSymbol type)
        {
            var name = ".t" + tempCount;
            tempCount++;
            tempVars.Add(new BilVarDeclaration(CanonicalSymbolPrinter.PrintType(type), name));
            return BilOp.Var(name);
        }

        // typeid 值临时变量（S9e，BIL §12.5/§7.1）：getid.type 结果
        // 类型恒为 .typeid（无边界 = .typeid<.any>，§6.3）——不落类型符号
        public BilVariableOperand NewTypeIdTemp()
        {
            var name = ".t" + tempCount;
            tempCount++;
            tempVars.Add(new BilVarDeclaration(".typeid", name));
            return BilOp.Var(name);
        }
    }
}
