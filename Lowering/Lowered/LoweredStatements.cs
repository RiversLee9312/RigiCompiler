using System.Collections.Generic;

namespace LatteCompiler
{
    // Lowered 语句节点（S6 最小集 + S7a 补齐 + S7b 脱糖 + S7c-1 循环 + S7d
    // switch/throw，SEMANTIC_ROADMAP）：块 / 局部变量声明 / 表达式语句 /
    // void 调用语句 / 赋值 / return / if / 循环 / break/continue /
    // switch / throw。
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

    // void 调用语句（无结果方法调用只能作语句，SYNTAX §4）。
    // Receiver 为 null = 静态/全局调用；非 null = 实例调用（S7c-2，
    // receiver 求值作首实参，BIL §7.3/§15.1）
    public sealed class LoweredCallStatement : LoweredStatement
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        public LoweredExpression? Receiver { get; }

        public LoweredCallStatement(BoundCallStatement origin, MethodSymbol method,
            IReadOnlyList<LoweredExpression> arguments, LoweredExpression? receiver = null)
            : base(origin)
        {
            Method = method;
            Arguments = arguments;
            Receiver = receiver;
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

    // 循环（S7c-1；BIL §16.3/§16.4 结构化循环的直接对应）：
    // - Judge = 条件求值并写入 Condition 的语句序列（前置语句机制产物，
    //   末尾一条写条件局部的赋值；§16.3 要求每次读取 CONDITION 前由
    //   JUDGE_BLOCK 赋值）；
    // - Condition 是合成 bool 局部（.sN 体系）；
    // - Enumerator 恒 null（发射 none；for 的枚举器块随 S7c-2 落地）；
    // - BreakId 是合成 .breakid 局部（.bN 命名，函数内唯一；Type 为
    //   null 的特例见 LocalSymbol 注释，.vars 条目投影 .breakid §9.3）
    public sealed class LoweredLoop : LoweredStatement
    {
        public bool IsRev { get; }              // do-while → loop.rev（§16.4）
        public LoweredBlock Judge { get; }
        public LocalSymbol Condition { get; }
        public LoweredBlock Body { get; }
        public LoweredBlock? Enumerator { get; }
        public LocalSymbol BreakId { get; }

        public LoweredLoop(BoundLoop origin, bool isRev, LoweredBlock judge,
            LocalSymbol condition, LoweredBlock body, LoweredBlock? enumerator,
            LocalSymbol breakId) : base(origin)
        {
            IsRev = isRev;
            Judge = judge;
            Condition = condition;
            Body = body;
            Enumerator = enumerator;
            BreakId = breakId;
        }
    }

    // break/continue（S7c-1；BIL §16.5）：BreakId 经 BoundLoop → 合成
    // .breakid 局部的映射命中（穿透值块/嵌套块时属外层循环——BIL 动态
    // 结构作用域合法，降级不做任何展开，直接发 break/continue 指令）
    public sealed class LoweredLoopControl : LoweredStatement
    {
        public bool IsBreak { get; }
        public LocalSymbol BreakId { get; }

        public LoweredLoopControl(BoundLoopControl origin, bool isBreak,
            LocalSymbol breakId) : base(origin)
        {
            IsBreak = isBreak;
            BreakId = breakId;
        }
    }

    // switch（S7d；BIL §16.6 结构化 switch 的直接对应）：仅全值匹配形态
    // 到达本节点——含 pattern 的 switch 已在 P4a 降级为嵌套
    // LoweredIfStatement（§16.6：含 _ 的 pattern 分支不能进常量表）。
    // Cases 保序（表序 = 匹配序）；DefaultBody 恒存在（P3/Parser 强制）。
    // BreakId 是合成 .breakid 局部（.bN 命名，约定同 LoweredLoop；Latte 层
    // break 不指向 switch——规范未登记，该 id 仅满足指令形态要求，无人引用）
    public sealed class LoweredSwitch : LoweredStatement
    {
        public LoweredExpression Selector { get; }
        public IReadOnlyList<LoweredSwitchCase> Cases { get; }
        public LoweredBlock DefaultBody { get; }
        public LocalSymbol BreakId { get; }

        public LoweredSwitch(BoundNode origin, LoweredExpression selector,
            IReadOnlyList<LoweredSwitchCase> cases, LoweredBlock defaultBody,
            LocalSymbol breakId) : base(origin)
        {
            Selector = selector;
            Cases = cases;
            DefaultBody = defaultBody;
            BreakId = breakId;
        }
    }

    // switch 分支（全值匹配形态）：Value 为常量字面量表达式（类型与 selector
    // 严格相同，P3 已查），Origin 指 BoundSwitchCase
    public sealed class LoweredSwitchCase : LoweredNode
    {
        public LoweredExpression Value { get; }
        public LoweredBlock Body { get; }

        public LoweredSwitchCase(BoundNode origin, LoweredExpression value, LoweredBlock body)
            : base(origin)
        {
            Value = value;
            Body = body;
        }
    }

    // throw（S7d；BIL §16.9 的直接对应，恒等降级）
    public sealed class LoweredThrowStatement : LoweredStatement
    {
        public LoweredExpression Exception { get; }

        public LoweredThrowStatement(BoundThrowStatement origin, LoweredExpression exception)
            : base(origin)
        {
            Exception = exception;
        }
    }
}
