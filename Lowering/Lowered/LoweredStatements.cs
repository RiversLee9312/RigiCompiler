using System.Collections.Generic;

namespace LatteCompiler
{
    // Lowered 语句节点（S6 最小集，SEMANTIC_ROADMAP S6）：块 / void 调用语句 / return。

    // 块（恒等重写：作用域是 P3 分析期结构，不落树；嵌套块在发射期平铺）
    public sealed class LoweredBlock : LoweredStatement
    {
        public IReadOnlyList<LoweredStatement> Statements { get; }

        public LoweredBlock(BoundBlock origin, IReadOnlyList<LoweredStatement> statements)
            : base(origin)
        {
            Statements = statements;
        }
    }

    // void 调用语句（无结果方法调用只能作语句，SYNTAX §4）
    public sealed class LoweredCallStatement : LoweredStatement
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }

        public LoweredCallStatement(BoundCallStatement origin, MethodSymbol method,
            IReadOnlyList<LoweredExpression> arguments) : base(origin)
        {
            Method = method;
            Arguments = arguments;
        }
    }

    // return（Value 为 null = 裸 return，仅 void 函数合法）
    public sealed class LoweredReturnStatement : LoweredStatement
    {
        public LoweredExpression? Value { get; }

        public LoweredReturnStatement(BoundReturnStatement origin, LoweredExpression? value)
            : base(origin)
        {
            Value = value;
        }
    }
}
