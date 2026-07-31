using System.Collections.Generic;

namespace LatteCompiler
{
    // LoweredTree（P4a 产物，SEMANTIC_ARCHITECTURE §6.1）：降级重写后的语义树。
    // 基类 LoweredNode 回指 Origin: BoundNode（必填）——完整调试链
    // BilInstruction.Origin → LoweredNode.Origin → BoundNode.Syntax → ASTNode.Span。
    // S6 最小集：只覆盖 hello world 端到端所需的五类节点（块/void 调用/return/
    // 字面量/值引用），不镜像 Bound 全部节点；其余节点种类随 S7+ 脱糖落地增补。

    public abstract class LoweredNode
    {
        // 重写来源（必填）：恒等重写（S6）下即对应的 Bound 节点
        public BoundNode Origin { get; }

        protected LoweredNode(BoundNode origin)
        {
            Origin = origin;
        }
    }

    // 表达式基类：Type 直接透传 Origin 的定型类型，不冗余存储
    public abstract class LoweredExpression : LoweredNode
    {
        public TypeSymbol Type => ((BoundExpression)Origin).Type;

        protected LoweredExpression(BoundExpression origin) : base(origin)
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
