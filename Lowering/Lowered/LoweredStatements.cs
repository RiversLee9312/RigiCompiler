using System.Collections.Generic;

namespace RigiCompiler
{
    // Lowered 语句节点（S6 最小集 + S7a 补齐 + S7b 脱糖 + S7c-1 循环 + S7d
    // switch/throw + S7e try/seq，SEMANTIC_ROADMAP）：块 / 局部变量声明 /
    // 表达式语句 / void 调用语句 / 赋值 / return / if / 循环 / break/continue /
    // switch / throw / try-catch-finally / seq 块。
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
    // receiver 求值作首实参，BIL §7.3/§15.1）。
    // S9e 增补 TypeArguments：显式泛型实参（同 LoweredCallExpression）
    // S9d-2 增补 GenericPack：泛型可变参数包推导产物（同 LoweredCallExpression）
    public sealed class LoweredCallStatement : LoweredStatement
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        public LoweredExpression? Receiver { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public LoweredGenericVarArgsArgument? GenericPack { get; }
        // 间接调用（§15.3 callable 协议）：物化目标对象表达式后虚调用其
        // $$call；非间接调用为 null
        public bool IsIndirect { get; }
        public LoweredExpression? IndirectTarget { get; }

        public LoweredCallStatement(BoundNode origin, MethodSymbol method,
            IReadOnlyList<LoweredExpression> arguments, LoweredExpression? receiver = null,
            IReadOnlyList<SemanticSymbol>? typeArguments = null,
            LoweredGenericVarArgsArgument? genericPack = null,
            LoweredExpression? indirectTarget = null)
            : base(origin)
        {
            Method = method;
            Arguments = arguments;
            Receiver = receiver;
            TypeArguments = typeArguments ?? Array.Empty<SemanticSymbol>();
            GenericPack = genericPack;
            IsIndirect = indirectTarget != null;
            IndirectTarget = indirectTarget;
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

    // 局部变量声明（var/const；类型已定型在 LocalSymbol.Type 上）。
    // origin 放宽为 BoundNode：解构脱糖等合成路径的 Origin 按 ARCH §5.1
    // 约定指最近语法来源（先例：LoweredCastExpression/LoweredFieldAccessExpression）
    public sealed class LoweredLocalDeclarationStatement : LoweredStatement
    {
        public LocalSymbol Local { get; }
        public LoweredExpression? Initializer { get; }

        public LoweredLocalDeclarationStatement(BoundNode origin,
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

    // yield（S13，P4a 同构节点）：挂起点不退化为调用。
    public sealed class LoweredYieldStatement : LoweredStatement
    {
        public LoweredExpression? Alarm { get; }

        public LoweredYieldStatement(BoundYieldStatement origin, LoweredExpression? alarm)
            : base(origin)
        {
            Alarm = alarm;
        }
    }

    // 赋值（Target 限 LoweredValueReferenceExpression / LoweredFieldReferenceExpression
    // 这类 place——Bound 侧已强制；S7b 起也承载值块结果写入（return@ 脱糖产物，
    // Origin 为 BoundReturnValueStatement/BoundExpressionStatement；Stage B 起另承载
    // StructuredExitRouting 的 route 局部写入）与复合赋值写回）
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
    // 与 if 表达式的脱糖产物（Origin 指 and/or 表达式 / if 表达式的 Bound 节点）。
    // BreakId 是合成 .breakid 局部（.bN 命名，约定同 LoweredLoop；§16.5 推广的
    // region-exit capability——Stage B 起是 if 表达式分支值块 return@ 的目标
    // region id，被 StructuredExitRouting 展开的 break/dispatcher 引用）
    public sealed class LoweredIfStatement : LoweredStatement
    {
        public LoweredExpression Condition { get; }
        public LoweredBlock TrueBlock { get; }
        public LoweredBlock? FalseBlock { get; }
        public LocalSymbol BreakId { get; }

        // §18.1 rigi.seq-route hint 标记（StructuredExitRouting 的 region
        // 收尾在标准 route dispatcher 尾链首链节上设置 = 本 region 的
        // route 局部）：发射期在链首（汇聚边着陆点之后、第一条 cmp 之前）
        // 补一条 hint 指令；非 dispatcher 的普通 if 恒为 null
        public LocalSymbol? SeqRouteHintRoute { get; set; }

        public LoweredIfStatement(BoundNode origin, LoweredExpression condition,
            LoweredBlock trueBlock, LoweredBlock? falseBlock, LocalSymbol breakId)
            : base(origin)
        {
            Condition = condition;
            TrueBlock = trueBlock;
            FalseBlock = falseBlock;
            BreakId = breakId;
        }
    }

    // 循环（S7c-1；BIL §16.3/§16.4 结构化循环的直接对应）：
    // - Judge = 条件求值并写入 Condition 的语句序列（前置语句机制产物，
    //   末尾一条写条件局部的赋值；§16.3 要求每次读取 CONDITION 前由
    //   JUDGE_BLOCK 赋值）；
    // - Condition 是合成 bool 局部（.sN 体系）；
    // - BreakId 是合成 .breakid 局部（.bN 命名，函数内唯一；Type 为
    //   null 的特例见 LocalSymbol 注释，.vars 条目投影 .breakid §9.3）
    public sealed class LoweredLoop : LoweredStatement
    {
        public bool IsRev { get; }              // do-while → loop.rev（§16.4）
        public LoweredBlock Judge { get; }
        public LocalSymbol Condition { get; }
        public LoweredBlock Body { get; }
        public LocalSymbol BreakId { get; }

        public LoweredLoop(BoundLoop origin, bool isRev, LoweredBlock judge,
            LocalSymbol condition, LoweredBlock body, LocalSymbol breakId) : base(origin)
        {
            IsRev = isRev;
            Judge = judge;
            Condition = condition;
            Body = body;
            BreakId = breakId;
        }
    }

    // break/continue（S7c-1；BIL §16.5）：BreakId 经 BoundLoop → 合成
    // .breakid 局部的映射命中（穿透值块/嵌套块时属外层循环——BIL 动态
    // 结构作用域合法，降级不做任何展开，直接发 break/continue 指令）。
    // Stage B 起拆分为两节点（原 LoweredLoopControl 合并形态删除）；
    // LoweredBreakStatement 的 origin 放宽为 BoundNode——StructuredExitRouting
    // pass 的 region 收尾 / dispatcher relay 也合成 break（无逐一对应的
    // Bound 节点，Origin 按 ARCH §5.1 约定指最近的语法来源）
    public sealed class LoweredBreakStatement : LoweredStatement
    {
        public LocalSymbol BreakId { get; }

        public LoweredBreakStatement(BoundNode origin, LocalSymbol breakId) : base(origin)
        {
            BreakId = breakId;
        }
    }

    public sealed class LoweredContinueStatement : LoweredStatement
    {
        public LocalSymbol BreakId { get; }

        public LoweredContinueStatement(BoundLoopControl origin, LocalSymbol breakId)
            : base(origin)
        {
            BreakId = breakId;
        }
    }

    // source-level exit 标记（Stage B，return@ 重构）：return@值块 /
    // return@语句seq 的 ordinary lowering 产物——Target 为 BoundValueBlock
    // 或 BoundSeqStatement（引用相等身份），Value 为已降级的产值
    // （return@语句seq 恒 null）。待 StructuredExitRouting pass 处理
    // （展开为「写结果局部 + 写 route 局部（仅跨 region）+ break 当前
    // region」），pass 后树中不得残留（EmitDispatchers 兜底抛内部错误）
    public sealed class LoweredStructuredExit : LoweredStatement
    {
        public BoundNode Target { get; }
        public LoweredExpression? Value { get; }

        public LoweredStructuredExit(BoundNode origin, BoundNode target,
            LoweredExpression? value) : base(origin)
        {
            Target = target;
            Value = value;
        }
    }

    // switch（S7d；BIL §16.6 结构化 switch 的直接对应）：仅全值匹配形态
    // 到达本节点——含 pattern 的 switch 已在 P4a 降级为嵌套
    // LoweredIfStatement（§16.6：含 _ 的 pattern 分支不能进常量表）。
    // Cases 保序（表序 = 匹配序）；DefaultBody 恒存在（P3/Parser 强制）。
    // BreakId 是合成 .breakid 局部（.bN 命名，约定同 LoweredLoop；§16.5 推广的
    // region-exit capability——Stage B 起 switch 表达式全值路径的 id 是分支值块
    // return@ 的目标 region id，被 StructuredExitRouting 展开的
    // break/dispatcher 引用；switch 语句路径仍无人引用，仅满足形态要求）
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

    // try-catch-finally（S7e；BIL §16.7 的直接对应）：Catches 保序（表序 =
    // 匹配序，首个类型兼容命中胜出）；FinallyBlock 可空（发射 none 操作数）。
    // ExceptionSlot = try 指令 $slot 操作数的承载局部（Nullable<core.Exception>）：
    // finally(e) 的 e 非空时即该局部（指令直写），否则为合成 .sN——有名
    // catch 的变量由 P4a 在体头合成「变量 = cast slot」赋值填充（BIL §12.1
    // 显式收窄，P3 已查兼容）。BreakId 是合成 .breakid 局部（.bN 命名，
    // 约定同 LoweredLoop；§16.5 推广的 region-exit capability——Stage B 起
    // 可被 StructuredExitRouting 展开的 break/dispatcher 引用（return@ 穿
    // try/finally 的中继 region））
    public sealed class LoweredTryStatement : LoweredStatement
    {
        public LoweredBlock TryBlock { get; }
        public IReadOnlyList<LoweredTryCatch> Catches { get; }
        public LoweredBlock? FinallyBlock { get; }
        public LocalSymbol ExceptionSlot { get; }
        public LocalSymbol BreakId { get; }

        public LoweredTryStatement(BoundNode origin, LoweredBlock tryBlock,
            IReadOnlyList<LoweredTryCatch> catches, LoweredBlock? finallyBlock,
            LocalSymbol exceptionSlot, LocalSymbol breakId) : base(origin)
        {
            TryBlock = tryBlock;
            Catches = catches;
            FinallyBlock = finallyBlock;
            ExceptionSlot = exceptionSlot;
            BreakId = breakId;
        }
    }

    // catch 分支（S7e）：Variable 为 null = `_:` 无变量形态（有名变量的
    // 赋值已在 P4a 合成进 Body 头，此字段仅描述器展示用）；ExceptionType
    // 进 §19.5 catch-table 资源（P4b 登记）；Origin 指 BoundCatchClause
    public sealed class LoweredTryCatch : LoweredNode
    {
        public LocalSymbol? Variable { get; }
        public SemanticSymbol ExceptionType { get; }
        public LoweredBlock Body { get; }

        public LoweredTryCatch(BoundNode origin, LocalSymbol? variable,
            SemanticSymbol exceptionType, LoweredBlock body) : base(origin)
        {
            Variable = variable;
            ExceptionType = exceptionType;
            Body = body;
        }
    }

    // seq 块（S7e，SYNTAX §10；BIL §3.4 独立 block + call 化的直接对应）：
    // 两形态汇合——语句形态为恒等降级（Body = 体降级）；表达式形态为
    // P4a 脱糖产物（Body = 值块降级写结果局部，Origin 指 BoundSeqExpression）。
    // IsVolatile → §9.6 block 修饰符。BreakId 是合成 .breakid 局部（.bN
    // 命名，约定同 LoweredLoop；§16.5 推广的 region-exit capability——
    // Stage B 起是 seq 表达式值块 / named 语句 seq 的 return@ 目标 region
    // id，被 StructuredExitRouting 展开的 break/dispatcher 引用；另承载
    // 值块 lambda $$call 体与 pattern switch 表达式 if 链的包装 region）
    public sealed class LoweredSeqBlock : LoweredStatement
    {
        public LoweredBlock Body { get; }
        public bool IsVolatile { get; }
        public LocalSymbol BreakId { get; }

        public LoweredSeqBlock(BoundNode origin, LoweredBlock body, bool isVolatile,
            LocalSymbol breakId) : base(origin)
        {
            Body = body;
            IsVolatile = isVolatile;
            BreakId = breakId;
        }
    }

    // wrapper 安装（M109b-1，BIL §14.5）：仅 `..init.wrapper` 体内
    public sealed class LoweredNewWrapperStatement : LoweredStatement
    {
        public BoundNewWrapperKind Kind { get; }
        public TypeSymbol WrapperType { get; }
        public SemanticSymbol? Target { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }

        public LoweredNewWrapperStatement(BoundNode origin, BoundNewWrapperKind kind,
            TypeSymbol wrapperType, SemanticSymbol? target,
            IReadOnlyList<LoweredExpression> arguments) : base(origin)
        {
            Kind = kind;
            WrapperType = wrapperType;
            Target = target;
            Arguments = arguments;
        }
    }
}
