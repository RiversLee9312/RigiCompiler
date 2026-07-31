using System.Collections.Generic;

namespace LatteCompiler
{
    // Lowered 语句节点（S6 最小集 + S7a 补齐 + S7b 脱糖，SEMANTIC_ROADMAP）：
    // 块 / 局部变量声明 / 表达式语句 / void 调用语句 / 赋值 / return / if。
    // S7b 起部分节点构造的 origin 参数放宽为 BoundNode：脱糖合成节点
    // （值块写入赋值、if 转换新建块等）无逐一对应的 Bound 节点，
    // Origin 按 ARCH §5.1 约定指向最近的语法来源。

    // 块（恒等重写：作用域是 P3 分析期结构，不落树；嵌套块在发射期平铺）
    public sealed class LoweredBlock : LoweredStatement
    {
        public IReadOnlyList<LoweredStatement> Statements { get; }

        public LoweredBlock(BoundNode origin, IReadOnlyList<LoweredStatement> statements)
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

    // 局部变量声明（var/const；类型已定型在 LocalSymbol.Type 上）
    public sealed class LoweredLocalDeclarationStatement : LoweredStatement
    {
        public LocalSymbol Local { get; }
        public LoweredExpression? Initializer { get; }

        public LoweredLocalDeclarationStatement(BoundLocalDeclarationStatement origin,
            LocalSymbol local, LoweredExpression? initializer) : base(origin)
        {
            Local = local;
            Initializer = initializer;
        }
    }

    // 表达式语句（表达式求值后结果被丢弃）
    public sealed class LoweredExpressionStatement : LoweredStatement
    {
        public LoweredExpression Expression { get; }

        public LoweredExpressionStatement(BoundExpressionStatement origin,
            LoweredExpression expression) : base(origin)
        {
            Expression = expression;
        }
    }

    // 赋值（Target 限 LoweredValueReferenceExpression / LoweredFieldReferenceExpression
    // 这类 place——Bound 侧已强制；S7b 起也承载值块写入（return@ 脱糖产物，
    // Origin 为 BoundReturnValueStatement/BoundExpressionStatement）与复合赋值写回）
    public sealed class LoweredAssignmentStatement : LoweredStatement
    {
        public LoweredExpression Target { get; }
        public LoweredExpression Value { get; }

        public LoweredAssignmentStatement(BoundNode origin,
            LoweredExpression target, LoweredExpression value) : base(origin)
        {
            Target = target;
            Value = value;
        }
    }

    // if（S7b；BIL §16.2 结构化条件的直接对应）：FalseBlock 为 null = 无 else
    // （发射期 none 操作数）。来源两途：BoundIfStatement 恒等降级；短路 and/or
    // 与 if 表达式的脱糖产物（Origin 指 and/or 表达式 / if 表达式的 Bound 节点）
    public sealed class LoweredIfStatement : LoweredStatement
    {
        public LoweredExpression Condition { get; }
        public LoweredBlock TrueBlock { get; }
        public LoweredBlock? FalseBlock { get; }

        public LoweredIfStatement(BoundNode origin, LoweredExpression condition,
            LoweredBlock trueBlock, LoweredBlock? falseBlock) : base(origin)
        {
            Condition = condition;
            TrueBlock = trueBlock;
            FalseBlock = falseBlock;
        }
    }
}
