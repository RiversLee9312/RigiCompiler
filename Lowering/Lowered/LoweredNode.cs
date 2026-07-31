using System.Collections.Generic;

namespace LatteCompiler
{
    // LoweredTree（P4a 产物，SEMANTIC_ARCHITECTURE §6.1）：降级重写后的语义树。
    // 基类 LoweredNode 回指 Origin: BoundNode（必填）——完整调试链
    // BilInstruction.Origin → LoweredNode.Origin → BoundNode.Syntax → ASTNode.Span。
    // S6 为最小集起步；S7a 补齐 P3（S5）能产出的全部 Bound 节点对应形态
    // （仍为恒等重写，无脱糖）；S7b 起落地脱糖（bool 短路 and/or、if 表达式、
    // 复合赋值）——脱糖产生的合成节点按 ARCH §5.1 约定：Origin 指向最近的
    // 语法来源（如 and/or 表达式本身的 Bound 节点）。

    public abstract class LoweredNode
    {
        // 重写来源（必填）：恒等重写（S6）下即对应的 Bound 节点；
        // 合成节点（S7b 脱糖产物）指向最近的语法来源
        public BoundNode Origin { get; }

        protected LoweredNode(BoundNode origin)
        {
            Origin = origin;
        }
    }

    // 表达式基类：Type 默认透传 Origin 的定型类型，不冗余存储
    // （virtual：合成节点类型不走透传时覆盖——LoweredConstantExpression 自带
    // 类型字段、LoweredValueReferenceExpression 取符号类型）
    public abstract class LoweredExpression : LoweredNode
    {
        public virtual TypeSymbol Type => ((BoundExpression)Origin).Type;

        protected LoweredExpression(BoundNode origin) : base(origin)
        {
        }
    }

    // 语句基类
    public abstract class LoweredStatement : LoweredNode
    {
        protected LoweredStatement(BoundNode origin) : base(origin)
        {
        }
    }

    // P4 发射单位（ARCHITECTURE §6.2）：一个函数体的完整降级结果。
    // 非 LoweredNode：仿 BoundFunctionBody，调试链经 Method（符号）与
    // Body.Origin（函数体块）回指。
    public sealed class LoweredFunctionBody
    {
        public MethodSymbol Method { get; }
        // 函数体内声明的全部局部变量（声明顺序；参数在 Method.Parameters）
        public IReadOnlyList<LocalSymbol> Locals { get; }
        public LoweredBlock Body { get; }

        public LoweredFunctionBody(MethodSymbol method, IReadOnlyList<LocalSymbol> locals,
            LoweredBlock body)
        {
            Method = method;
            Locals = locals;
            Body = body;
        }
    }
}
