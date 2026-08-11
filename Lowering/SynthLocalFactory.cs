namespace LatteCompiler
{
    // 合成局部工厂（M65 Lowering 侧组件化拆分，自 LowerContext 迁出）：
    // 合成局部（.sN，BIL §5.1 编译器保留名，函数内唯一）与 .breakid
    // 局部（.bN）的命名与登记唯一来源 + 合成值引用工厂；登记序列收尾
    // 追加进 LoweredFunctionBody.Locals。
    // ⚠ 顺序敏感：.sN 先于 .bN——SynthLocals 顺序即 BIL .vars 发射
    // 顺序（BIL 文本快照敏感），两计数器独立、统一进同一登记序列。
    internal sealed class SynthLocalFactory
    {
        // 合成局部（.sN，BIL §5.1 编译器保留名，函数内唯一）与 .breakid
        // 局部（.bN）；收尾追加进 LoweredFunctionBody.Locals
        private readonly List<LocalSymbol> synthLocals = new List<LocalSymbol>();

        private int synthCount;

        private int breakIdCount;

        // 合成局部登记序列（只读暴露——LoweringDriver 收尾追加进
        // LoweredFunctionBody.Locals；顺序即 .vars 发射顺序）
        public IReadOnlyList<LocalSymbol> SynthLocals => synthLocals;

        // 合成局部（BIL §5.1 编译器保留名 .sN，函数内唯一）；
        // S9 放宽为 SemanticSymbol：泛型参数类型可作合成局部类型
        public LocalSymbol NewSynthLocal(SemanticSymbol type)
        {
            var local = new LocalSymbol(".s" + synthCount, type, isConst: false);
            synthCount++;
            synthLocals.Add(local);
            return local;
        }

        // 合成 .breakid 局部（S7c-1，BIL §9.3 capability）：.bN 命名，
        // 函数内唯一；Type 为 null（无对应 TypeSymbol，见 LocalSymbol
        // 注释），emitter 侧 .vars 条目按 .breakid 投影
        public LocalSymbol NewBreakIdLocal()
        {
            var local = new LocalSymbol(".b" + breakIdCount, null, isConst: false);
            breakIdCount++;
            synthLocals.Add(local);
            return local;
        }

        // 被捕获参数的 cell 局部（SYNTAX §5.2 闭包模型）：.c.<参数名> 命名，
        // 函数入口 prologue 用实参构造；命名对参数名确定——发射侧零映射表
        //（EmittingFacility.ValueVariableName 同名规则直接命中）
        public LocalSymbol NewCaptureCellLocal(string parameterName, SemanticSymbol cellType)
        {
            var local = new LocalSymbol(".c." + parameterName, cellType, isConst: false);
            synthLocals.Add(local);
            return local;
        }

        // 合成值引用（Origin 指最近语法来源，ARCH §5.1）
        public static LoweredValueReferenceExpression ReferenceTo(BoundNode origin,
            SemanticSymbol symbol)
        {
            return new LoweredValueReferenceExpression(origin, symbol);
        }
    }
}
