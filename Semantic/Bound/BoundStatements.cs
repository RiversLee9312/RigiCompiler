using System.Collections.Generic;

namespace LatteCompiler
{
    // Bound 语句节点（S5 最小集 + S7b 控制流首批 + S7c-1 循环，SEMANTIC_ROADMAP）：
    // 块 / 局部变量声明 / 表达式语句 / void 调用语句 / 赋值 / return /
    // if 语句 / 值块（if 表达式分支体）/ return@标签 取值 / 循环 / break/continue。

    // 块（绑定期每块一个作用域；作用域本身是分析期结构，不落树）
    public sealed class BoundBlock : BoundStatement
    {
        public IReadOnlyList<BoundStatement> Statements { get; }

        public BoundBlock(ASTNode syntax, IReadOnlyList<BoundStatement> statements) : base(syntax)
        {
            Statements = statements;
        }
    }

    // 局部变量声明（var/const；类型已定型在 LocalSymbol.Type 上——
    // 显式标注或经初始化器推断）
    public sealed class BoundLocalDeclarationStatement : BoundStatement
    {
        public LocalSymbol Local { get; }
        public BoundExpression? Initializer { get; }

        public BoundLocalDeclarationStatement(ASTNode syntax, LocalSymbol local,
            BoundExpression? initializer) : base(syntax)
        {
            Local = local;
            Initializer = initializer;
        }
    }

    // 表达式语句（表达式求值后结果被丢弃）
    public sealed class BoundExpressionStatement : BoundStatement
    {
        public BoundExpression Expression { get; }

        public BoundExpressionStatement(ASTNode syntax, BoundExpression expression) : base(syntax)
        {
            Expression = expression;
        }
    }

    // void 调用语句（无结果方法调用只能作语句，SYNTAX §4：无隐式返回值利用）。
    // Receiver 为 null = 静态/全局调用（S7c-2 前唯一形态）；非 null = 实例
    // 调用（receiver 求值作首实参，BIL §7.3/§15.1）
    public sealed class BoundCallStatement : BoundStatement
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }
        public BoundExpression? Receiver { get; }

        public BoundCallStatement(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments, BoundExpression? receiver = null)
            : base(syntax)
        {
            Method = method;
            Arguments = arguments;
            Receiver = receiver;
        }
    }

    // 赋值（Target 限 BoundValueReferenceExpression / BoundFieldReferenceExpression
    // 这类 place——Binder 强制；const 目标已在 P3 拒绝）
    public sealed class BoundAssignmentStatement : BoundStatement
    {
        public BoundExpression Target { get; }
        public BoundExpression Value { get; }

        public BoundAssignmentStatement(ASTNode syntax, BoundExpression target,
            BoundExpression value) : base(syntax)
        {
            Target = target;
            Value = value;
        }
    }

    // return（Value 为 null = 裸 return，仅 void 函数合法）
    public sealed class BoundReturnStatement : BoundStatement
    {
        public BoundExpression? Value { get; }

        public BoundReturnStatement(ASTNode syntax, BoundExpression? value) : base(syntax)
        {
            Value = value;
        }
    }

    // if 语句（SYNTAX §7.1）；else if 链在绑定时包成单语句 BoundBlock，
    // Bound 层只有双分支形态（FalseBlock 为 null = 无 else）
    public sealed class BoundIfStatement : BoundStatement
    {
        public BoundExpression Condition { get; }
        public BoundBlock TrueBlock { get; }
        public BoundBlock? FalseBlock { get; }

        public BoundIfStatement(ASTNode syntax, BoundExpression condition,
            BoundBlock trueBlock, BoundBlock? falseBlock) : base(syntax)
        {
            Condition = condition;
            TrueBlock = trueBlock;
            FalseBlock = falseBlock;
        }
    }

    // 值块（SYNTAX §6.1/§7.1）：if 表达式分支体（后续 switch 分支体/seq 表达式
    // 复用同一节点）。引用相等即身份——BoundReturnValueStatement 经引用命中目标块。
    // IsImplicitValue = M33 判定结果（块内恰好一条 ExpressionStatement，P3 显式
    // 记录，P4 不再看语法形态）。
    // 绑定施工壳先于分支体绑定创建（标签栈需要），Block/IsImplicitValue/ValueType
    // 在分支体绑完后回填（internal set）。
    public sealed class BoundValueBlock : BoundNode
    {
        // named 标签，缺省 "_"
        public string Label { get; }
        public BoundBlock Block { get; internal set; } = null!;
        public bool IsImplicitValue { get; internal set; }
        // 产值类型；分支纯穿透终止（无本块产值）时为 null
        public TypeSymbol? ValueType { get; internal set; }

        public BoundValueBlock(ASTNode syntax, string label) : base(syntax)
        {
            Label = label;
        }
    }

    // return@标签 取值（SYNTAX §6.1）：终止 Target 值块路径并把 Value 作为该块产值；
    // Target 经值块标签栈解析，引用相等命中（穿透外层值块时 Target 是外层块）
    public sealed class BoundReturnValueStatement : BoundStatement
    {
        public BoundValueBlock Target { get; }
        public BoundExpression Value { get; }

        public BoundReturnValueStatement(ASTNode syntax, BoundValueBlock target,
            BoundExpression value) : base(syntax)
        {
            Target = target;
            Value = value;
        }
    }

    // 循环语句（SYNTAX §7.3/§7.4）：引用相等即身份——BoundLoopControl 经引用
    // 命中目标循环。Kind 复用 AST 的 LoopKind 枚举。Label 为 named 标签
    // （可空），Condition 已查 bool。
    // 形态互斥（注释即约定）：While/DoWhile 时 Condition 非 null、For 专属
    // 字段全 null；For 时（S7c-2）Condition 为 null，LoopVariable/Iterable/
    // 协议三方法非 null。
    // 绑定施工壳先于循环体绑定创建（循环标签栈需要——体内 break/continue
    // 经引用命中本壳），Condition/Body 绑完后回填（internal set，仿
    // BoundValueBlock 的壳模式）
    public sealed class BoundLoop : BoundStatement
    {
        public LoopKind Kind { get; }
        public string? Label { get; }
        public BoundExpression? Condition { get; internal set; }
        public BoundBlock Body { get; internal set; } = null!;

        // ===== For 专属（S7c-2；While/DoWhile 恒 null）=====
        // 循环变量（const 局部——只读默认，规范未明，M48 登记；类型 = 元素
        // 类型 TItem，每轮迭代由枚举器赋值，体入口视为已赋值）
        public LocalSymbol? LoopVariable { get; internal set; }
        // 迭代源表达式：for-each 为集合表达式；范围循环为
        // BoundInstanceCallExpression{a, EnumerateInRange operator, [b]}
        public BoundExpression? Iterable { get; internal set; }
        // for-each 协议三方法符号（P3 解析挂好，P4 不做名字分析，
        // ARCH §11.3 纪律）：接口方法符号引用（分派归 Middleware）；
        // 枚举器局部类型由 P4a 经 GetConstructedType(IEnumerator, TItem)
        // 驻留构造（LoopVariable.Type 即 TItem，P4a 自取）
        public MethodSymbol? IterateMethod { get; internal set; }
        public MethodSymbol? MoveNextMethod { get; internal set; }
        public MethodSymbol? CurrentMethod { get; internal set; }

        public BoundLoop(ASTNode syntax, LoopKind kind, string? label) : base(syntax)
        {
            Kind = kind;
            Label = label;
        }
    }

    // break/continue（SYNTAX §7.4）：Target 经循环标签栈解析，引用相等命中
    // （穿透值块/嵌套块时 Target 是外层循环，BIL §16.5 动态结构作用域合法）
    public sealed class BoundLoopControl : BoundStatement
    {
        public bool IsBreak { get; }
        public BoundLoop Target { get; }

        public BoundLoopControl(ASTNode syntax, bool isBreak, BoundLoop target)
            : base(syntax)
        {
            IsBreak = isBreak;
            Target = target;
        }
    }
}
