using System.Collections.Generic;

namespace LatteCompiler
{
    // Bound 语句节点（S5 最小集 + S7b 控制流首批 + S7c-1 循环 + S7d switch/throw
    // + S7e try/seq，SEMANTIC_ROADMAP）：
    // 块 / 局部变量声明 / 表达式语句 / void 调用语句 / 赋值 / return /
    // if 语句 / 值块 / return@标签 取值 / 循环 / break/continue /
    // switch 语句 / throw / try-catch-finally / seq 语句。

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

    // 解构声明（S7f，SYNTAX §18）：var (a, b) = pair——pair 类型已查为
    // core.Pair\<TKey, TValue\> 子类；Entries 按声明序携带（分量局部,
    // 对应 Pair 字段符号），分量类型已按构造实参定型在 LocalSymbol.Type
    // （P4a 脱糖为 pair 物化 + 逐字段读取，BIL §3.4「精确字段读取」）
    public sealed class BoundDestructuringDeclarationStatement : BoundStatement
    {
        public BoundExpression Initializer { get; }
        public IReadOnlyList<(LocalSymbol Local, FieldSymbol Field)> Entries { get; }

        public BoundDestructuringDeclarationStatement(ASTNode syntax,
            BoundExpression initializer,
            IReadOnlyList<(LocalSymbol, FieldSymbol)> entries) : base(syntax)
        {
            Initializer = initializer;
            Entries = entries;
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

    // yield（S13，SYNTAX §7.5）：Alarm 为 null 表示裸 yield；非 null 时已
    // 定型为可赋值到 PollingAlarm 或 EventAlarm 的表达式。
    public sealed class BoundYieldStatement : BoundStatement
    {
        public BoundExpression? Alarm { get; }

        public BoundYieldStatement(ASTNode syntax, BoundExpression? alarm) : base(syntax)
        {
            Alarm = alarm;
        }
    }

    // void 调用语句（无结果方法调用只能作语句，SYNTAX §4：无隐式返回值利用）。
    // Receiver 为 null = 静态/全局调用（S7c-2 前唯一形态）；非 null = 实例
    // 调用（receiver 求值作首实参，BIL §7.3/§15.1）。
    // S9b 增补 TypeArguments：显式泛型实参（同 BoundCallExpression）
    // S9d-2 增补 GenericPack：泛型可变参数包推导产物（同 BoundCallExpression）
    public sealed class BoundCallStatement : BoundStatement
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }
        public BoundExpression? Receiver { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public BoundGenericVarArgsArgument? GenericPack { get; }
        // 间接调用（§15.3 callable 协议）：对 IndirectTarget 对象虚调用
        // 其 $$call 实现；非间接调用为 null
        public bool IsIndirect { get; }
        public BoundExpression? IndirectTarget { get; }

        public BoundCallStatement(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments, BoundExpression? receiver = null,
            IReadOnlyList<SemanticSymbol>? typeArguments = null,
            BoundGenericVarArgsArgument? genericPack = null, bool isIndirect = false,
            BoundExpression? indirectTarget = null)
            : base(syntax)
        {
            Method = method;
            Arguments = arguments;
            Receiver = receiver;
            TypeArguments = typeArguments ?? Array.Empty<SemanticSymbol>();
            GenericPack = genericPack;
            IsIndirect = isIndirect;
            IndirectTarget = indirectTarget;
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

    // 值块（SYNTAX §6.1/§7.1）：if 表达式分支体（switch 分支体/seq 表达式
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
        public SemanticSymbol? ValueType { get; internal set; }
        // volatile 修饰（仅 seq 表达式置位，S7e；BIL §9.6 block 修饰符）
        public bool IsVolatile { get; internal set; }

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

    // switch 语句（S7d，SYNTAX §7.2）：Selector 已定型；Cases 保序（首个命中
    // 胜出，BIL §16.6 表序语义）；DefaultBody 恒存在（Parser 强制）。
    // Latte 层 break 不指向 switch（规范未登记），故引用相等身份不需要
    public sealed class BoundSwitchStatement : BoundStatement
    {
        public BoundExpression Selector { get; }
        public IReadOnlyList<BoundSwitchCase> Cases { get; }
        public BoundBlock DefaultBody { get; }

        public BoundSwitchStatement(ASTNode syntax, BoundExpression selector,
            IReadOnlyList<BoundSwitchCase> cases, BoundBlock defaultBody) : base(syntax)
        {
            Selector = selector;
            Cases = cases;
            DefaultBody = defaultBody;
        }
    }

    // switch 分支（语句形态）：Match 为匹配表达式——IsPattern=false 时是
    // BoundLiteralExpression（编译期常量最小口径，类型与 selector 严格相同，
    // P3 已查）；IsPattern=true 时是 bool 表达式（_ 已绑为
    // BoundSwitchPlaceholderExpression，P3 已查 bool）。
    // 分类结果 P3 显式记录，P4a 不再回看语法（§16.6：含 _ 的 pattern
    // 分支不能进常量表，必须降级为嵌套条件）
    public sealed class BoundSwitchCase : BoundNode
    {
        public BoundExpression Match { get; }
        public bool IsPattern { get; }
        public BoundBlock Body { get; }

        public BoundSwitchCase(ASTNode syntax, BoundExpression match, bool isPattern,
            BoundBlock body) : base(syntax)
        {
            Match = match;
            IsPattern = isPattern;
            Body = body;
        }
    }

    // throw（S7d，SYNTAX §8）：Exception 已查与异常根 core.Exception 兼容
    // （BIL §16.9 要求根类型兼容值；视图转换 cast 归后续里程碑）
    public sealed class BoundThrowStatement : BoundStatement
    {
        public BoundExpression Exception { get; }

        public BoundThrowStatement(ASTNode syntax, BoundExpression exception) : base(syntax)
        {
            Exception = exception;
        }
    }

    // try-catch-finally（S7e，SYNTAX §8）：CatchClauses 保序（首个类型兼容
    // 命中胜出，BIL §16.7 catch-table 表序语义）；FinallyBlock 可空。
    // FinallyVariable = finally(e) 的 e（Nullable<core.Exception>，const，
    // 只读默认——规范未明，M50 登记）；FinallyBlock 非空且 FinallyVariable
    // 为 null = 无参 finally
    public sealed class BoundTryStatement : BoundStatement
    {
        public BoundBlock TryBlock { get; }
        public IReadOnlyList<BoundCatchClause> Catches { get; }
        public BoundBlock? FinallyBlock { get; }
        public LocalSymbol? FinallyVariable { get; }

        public BoundTryStatement(ASTNode syntax, BoundBlock tryBlock,
            IReadOnlyList<BoundCatchClause> catches, BoundBlock? finallyBlock,
            LocalSymbol? finallyVariable) : base(syntax)
        {
            TryBlock = tryBlock;
            Catches = catches;
            FinallyBlock = finallyBlock;
            FinallyVariable = finallyVariable;
        }
    }

    // catch 分支（S7e）：ExceptionType 已查与 core.Exception 兼容；
    // Variable 为 null = `_:` 无变量形态（SYNTAX §8）；变量 const（只读默认，
    // 规范未明，M50 登记），命中即视为已赋值
    public sealed class BoundCatchClause : BoundNode
    {
        public LocalSymbol? Variable { get; }
        public SemanticSymbol ExceptionType { get; }
        public BoundBlock Body { get; }

        public BoundCatchClause(ASTNode syntax, LocalSymbol? variable,
            SemanticSymbol exceptionType, BoundBlock body) : base(syntax)
        {
            Variable = variable;
            ExceptionType = exceptionType;
            Body = body;
        }
    }

    // seq 语句（S7e，SYNTAX §10.1）：块级顺序执行区（BIL §3.4 独立 block +
    // call 化）。不压值块标签栈——体内 return@ 指向它报未定义标签（规范
    // 未明，M50 登记）；using 绑定列表是语句 seq 的 P3 绑定产物，表达式
    // using 仍归 S13（expression lowering pending）
    public sealed class BoundSeqStatement : BoundStatement
    {
        // 施工壳模式（同 BoundValueBlock.Block）：语句 seq 作 return@ 目标
        // 时须在绑体前压标签栈（M61），体绑完回填
        public BoundBlock Body { get; internal set; } = null!;
        public IReadOnlyList<BoundUsingBinding> UsingBindings { get; internal set; } =
            Array.Empty<BoundUsingBinding>();
        public bool IsVolatile { get; }
        // named 标签（仅显式 named 时非 null——语句 seq 不享有值块的 `_`
        // 默认标签，避免与值块默认值冲突；非 null 即可作 return@ 目标，
        // SYNTAX §6.1，M61）
        public string? Label { get; }

        public BoundSeqStatement(ASTNode syntax, bool isVolatile, string? label = null,
            IReadOnlyList<BoundUsingBinding>? usingBindings = null)
            : base(syntax)
        {
            IsVolatile = isVolatile;
            Label = label;
            UsingBindings = usingBindings ?? Array.Empty<BoundUsingBinding>();
        }
    }

    // using 绑定的 P3 产物；DisposeMethod 已在绑定期解析，P4 不重新查名。
    public sealed class BoundUsingBinding : BoundNode
    {
        public LocalSymbol Local { get; }
        public BoundExpression Initializer { get; }
        public MethodSymbol DisposeMethod { get; }
        public BoundCallStatement DisposeCall { get; }

        public BoundUsingBinding(UsingBindingASTNode syntax, LocalSymbol local,
            BoundExpression initializer, MethodSymbol disposeMethod,
            BoundCallStatement disposeCall) : base(syntax)
        {
            Local = local;
            Initializer = initializer;
            DisposeMethod = disposeMethod;
            DisposeCall = disposeCall;
        }
    }

    // return@语句seq（M61，SYNTAX §6.1）：提前结束目标 seq 块（不携带值），
    // 继续执行块后语句。引用相等即身份（Target 为绑定时的 seq 施工节点）
    public sealed class BoundSeqExitStatement : BoundStatement
    {
        public BoundSeqStatement Target { get; }

        public BoundSeqExitStatement(ASTNode syntax, BoundSeqStatement target) : base(syntax)
        {
            Target = target;
        }
    }
}
