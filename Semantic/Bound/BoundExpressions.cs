using System.Collections.Generic;

namespace LatteCompiler
{
    // Bound 表达式节点（S5 最小集 + S7b 首批 + S7c-2 实例成员 + S7d switch
    // + S7e cast/seq + S7f 安全访问/空值回退 + S8a 类型谓词/typeOf，
    // SEMANTIC_ROADMAP）：
    // 字面量 / 值引用（局部变量与参数）/ 全局字段引用 / 二元与一元 intrinsic 运算 /
    // 直接调用（无重载）/ new 构造 / if 表达式 / 复合赋值 /
    // this / 实例方法调用 / 实例字段访问 / switch 表达式 / cast / seq 表达式 /
    // 安全访问 `?.`（含占位叶子）/ if? 空值回退 / is·supers·with / typeOf。
    // 字面量值不冗余存储——经 Syntax（LiteralExpressionASTNode.Literal）取。

    // 字面量（整/浮点/字符串/字符/bool/null；Type 由 P3 按字面量种类与上下文定型）
    public sealed class BoundLiteralExpression : BoundExpression
    {
        public BoundLiteralExpression(ASTNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    // 值引用：局部变量（LocalSymbol）或参数（ParameterSymbol）
    public sealed class BoundValueReferenceExpression : BoundExpression
    {
        public SemanticSymbol Symbol { get; }

        public BoundValueReferenceExpression(ASTNode syntax, SemanticSymbol symbol, TypeSymbol type)
            : base(syntax, type)
        {
            Symbol = symbol;
        }
    }

    // 全局字段引用（Owner == null 的 FieldSymbol；实例字段引用属后续里程碑）
    public sealed class BoundFieldReferenceExpression : BoundExpression
    {
        public FieldSymbol Field { get; }

        public BoundFieldReferenceExpression(ASTNode syntax, FieldSymbol field, TypeSymbol type)
            : base(syntax, type)
        {
            Field = field;
        }
    }

    // 二元 intrinsic 运算（BIL §11：两操作数类型严格相同，键 = opcode + 操作数类型
    // + 声明结果类型；比较运算 Type 为 bool，其余同操作数类型。
    // bool 的 and/or 在此仅定型——短路展开是 P4a 的职责，BIL §11.3）
    public sealed class BoundBinaryExpression : BoundExpression
    {
        public BilIntrinsicOp Op { get; }
        public BoundExpression Left { get; }
        public BoundExpression Right { get; }

        public BoundBinaryExpression(ASTNode syntax, BilIntrinsicOp op,
            BoundExpression left, BoundExpression right, TypeSymbol type)
            : base(syntax, type)
        {
            Op = op;
            Left = left;
            Right = right;
        }
    }

    // 一元 intrinsic 运算（opposite / not / bin.not）
    public sealed class BoundUnaryExpression : BoundExpression
    {
        public BilIntrinsicOp Op { get; }
        public BoundExpression Operand { get; }

        public BoundUnaryExpression(ASTNode syntax, BilIntrinsicOp op,
            BoundExpression operand, TypeSymbol type)
            : base(syntax, type)
        {
            Op = op;
            Operand = operand;
        }
    }

    // 直接函数调用（无重载，S8 才做 ranking）。实参已是绑定后的规范顺序
    // （具名实参已按形参名归位；默认参数填充属 S8）。
    // 仅用于有返回值的调用；void 调用作语句见 BoundCallStatement。
    public sealed class BoundCallExpression : BoundExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundCallExpression(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments, TypeSymbol type)
            : base(syntax, type)
        {
            Method = method;
            Arguments = arguments;
        }
    }

    // new 构造（SYNTAX §9.3）：Init 为匹配到的构造函数符号；
    // 无显式 init 的零参构造 Init 为 null
    public sealed class BoundNewExpression : BoundExpression
    {
        public MethodSymbol? Init { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundNewExpression(ASTNode syntax, TypeSymbol constructedType, MethodSymbol? init,
            IReadOnlyList<BoundExpression> arguments)
            : base(syntax, constructedType)
        {
            Init = init;
            Arguments = arguments;
        }
    }

    // if 表达式（SYNTAX §7.1）：必须有 else 分支；分支体是值块（取值规则在 P3
    // 绑定值块时判定）。Type = 两分支共同产值类型（P3 统一检查）
    public sealed class BoundIfExpression : BoundExpression
    {
        public BoundExpression Condition { get; }
        public BoundValueBlock TrueBranch { get; }
        public BoundValueBlock FalseBranch { get; }

        public BoundIfExpression(ASTNode syntax, BoundExpression condition,
            BoundValueBlock trueBranch, BoundValueBlock falseBranch, TypeSymbol type)
            : base(syntax, type)
        {
            Condition = condition;
            TrueBranch = trueBranch;
            FalseBranch = falseBranch;
        }
    }

    // 复合赋值（SYNTAX §13.2）：a += b 即 a = a + b 的语义糖；节点本身是表达式，
    // 值为写回后的值。Op 是基础运算符对应的 intrinsic（10 个：+ - * / << >> >>> & | ^，
    // 无 and/or）；Target 限定 place（局部/字段引用，P3 强制，读前须已赋值——读语义）
    public sealed class BoundCompoundAssignmentExpression : BoundExpression
    {
        public BoundExpression Target { get; }
        public BilIntrinsicOp Op { get; }
        public BoundExpression Value { get; }

        public BoundCompoundAssignmentExpression(ASTNode syntax, BoundExpression target,
            BilIntrinsicOp op, BoundExpression value, TypeSymbol type)
            : base(syntax, type)
        {
            Target = target;
            Op = op;
            Value = value;
        }
    }

    // this 引用（S7c-2，SYNTAX §9）：Type = 宿主类型（普通成员为声明类型；
    // ext 成员为目标类型——method.Owner 统一承载）。静态上下文（static
    // 方法/全局函数）中的 this 在 P3 拒绝，不落树
    public sealed class BoundThisExpression : BoundExpression
    {
        public BoundThisExpression(ASTNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    // 实例方法调用（S7c-2）：Receiver 静态类型上色查找（沿 BaseType 链，
    // 接口 receiver 查接口自身成员；ext 注册成员同路径——P2 已挂目标类型
    // 成员表）。接口方法的调用以接口方法符号引用（分派归 Middleware，
    // BIL §15.1 注释约定）。Arguments 已是规范参数序
    public sealed class BoundInstanceCallExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundInstanceCallExpression(ASTNode syntax, BoundExpression receiver,
            MethodSymbol method, IReadOnlyList<BoundExpression> arguments, TypeSymbol type)
            : base(syntax, type)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
        }
    }

    // 实例字段访问（S7c-2）：Receiver 静态类型上色查找（沿 BaseType 链；
    // ext 注册成员同路径）。裸名实例字段在实例方法体内解析为 this.field
    public sealed class BoundFieldAccessExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public FieldSymbol Field { get; }

        public BoundFieldAccessExpression(ASTNode syntax, BoundExpression receiver,
            FieldSymbol field, TypeSymbol type) : base(syntax, type)
        {
            Receiver = receiver;
            Field = field;
        }
    }

    // switch 表达式（S7d，SYNTAX §7.2）：分支体（含 default）是值块（取值
    // 规则同 if 表达式，标签同源 Label ?? "_"）。Type = 全分支统一产值类型
    // （纯穿透分支不参与统一，P3 已查）
    public sealed class BoundSwitchExpression : BoundExpression
    {
        public BoundExpression Selector { get; }
        public IReadOnlyList<BoundSwitchExpressionCase> Cases { get; }
        public BoundValueBlock DefaultBody { get; }

        public BoundSwitchExpression(ASTNode syntax, BoundExpression selector,
            IReadOnlyList<BoundSwitchExpressionCase> cases, BoundValueBlock defaultBody,
            TypeSymbol type)
            : base(syntax, type)
        {
            Selector = selector;
            Cases = cases;
            DefaultBody = defaultBody;
        }
    }

    // switch 分支（表达式形态）：Match/IsPattern 语义同 BoundSwitchCase，
    // 分支体为值块（BoundValueBlock 复用，S7d 起 switch 分支体落地）
    public sealed class BoundSwitchExpressionCase : BoundNode
    {
        public BoundExpression Match { get; }
        public bool IsPattern { get; }
        public BoundValueBlock Body { get; }

        public BoundSwitchExpressionCase(ASTNode syntax, BoundExpression match, bool isPattern,
            BoundValueBlock body) : base(syntax)
        {
            Match = match;
            IsPattern = isPattern;
            Body = body;
        }
    }

    // switch pattern 占位（S7d，SYNTAX §7.2：pattern 中 _ 引用 selector 的值）：
    // Selector 回指所属 switch 的 selector 表达式（嵌套 switch 经引用相等消歧），
    // 仅出现在 case 匹配表达式内（分支体无 _ 语义）；Type = selector 类型。
    // P4a 降级为 selector 临时局部读取（selector 只求值一次）
    public sealed class BoundSwitchPlaceholderExpression : BoundExpression
    {
        public BoundExpression Selector { get; }

        public BoundSwitchPlaceholderExpression(ASTNode syntax, BoundExpression selector,
            TypeSymbol type) : base(syntax, type)
        {
            Selector = selector;
        }
    }

    // cast（S7e，SYNTAX §11：as / as?；BIL §12.1/§12.2）：TargetType 是转换
    // 目标类型（as 与 as? 同形）；节点 Type 是表达式结果类型——as 时即
    // TargetType，as? 时为 Nullable<TargetType>（P3 定型，P4 不再区分包装）。
    // 可转性不做静态拒绝（as 失败是运行时 core.CastException；castTo/castFrom
    // 名字分析归后续里程碑）
    public sealed class BoundCastExpression : BoundExpression
    {
        public BoundExpression Source { get; }
        public TypeSymbol TargetType { get; }
        // true = as?（失败产 null）；false = as（失败抛 core.CastException）
        public bool IsSafe { get; }

        public BoundCastExpression(ASTNode syntax, BoundExpression source,
            TypeSymbol targetType, bool isSafe, TypeSymbol type) : base(syntax, type)
        {
            Source = source;
            TargetType = targetType;
            IsSafe = isSafe;
        }
    }

    // seq 表达式（S7e，SYNTAX §10.2）：体即值块（复用 BoundValueBlock，
    // 取值规则同 if 表达式分支体；using 绑定列表属 S13，P3 已拦截）。
    // 壳存在的理由：BoundValueBlock 是 BoundNode 非表达式，BindExpression
    // 必须返回表达式节点；Type = Body.ValueType
    public sealed class BoundSeqExpression : BoundExpression
    {
        public BoundValueBlock Body { get; }

        public BoundSeqExpression(ASTNode syntax, BoundValueBlock body, TypeSymbol type)
            : base(syntax, type)
        {
            Body = body;
        }
    }

    // 安全访问 `?.`（S7f，SYNTAX §3.4）：Receiver 为空则整体为 null，否则为
    // 非空 receiver 上的成员访问结果。Access 内含且仅含一个
    // BoundSafeAccessReceiverExpression 占位叶子（段绑定时的非空 receiver
    // 替身，Placeholder 持有它供 P4a 映射物化局部）；结果类型 P3 定型：
    // 成员类型已可空则原样（不二次包装），否则 Nullable<成员类型>
    public sealed class BoundSafeAccessExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public BoundSafeAccessReceiverExpression Placeholder { get; }
        public BoundExpression Access { get; }

        public BoundSafeAccessExpression(ASTNode syntax, BoundExpression receiver,
            BoundSafeAccessReceiverExpression placeholder, BoundExpression access,
            TypeSymbol type) : base(syntax, type)
        {
            Receiver = receiver;
            Placeholder = placeholder;
            Access = access;
        }
    }

    // 安全访问的非空 receiver 占位叶子（引用相等即身份）：P3 绑定 `?.` 段时
    // 作为段内成员访问的 receiver 替身；P4a 降级时替换为物化 receiver 局部
    // 的 unwrap cast（§12.1 .nullable<T> → T）
    public sealed class BoundSafeAccessReceiverExpression : BoundExpression
    {
        public BoundSafeAccessReceiverExpression(ASTNode syntax, TypeSymbol type)
            : base(syntax, type)
        {
        }
    }

    // if? 空值回退（S7f，SYNTAX §3.4）：Left 非空时取其值（.nullable<T> → T
    // 展开），为空时取 Right（回退值，延迟求值）。Type = T（Left 的元素类型，
    // P3 已查 Right 可赋值到 T）
    public sealed class BoundNullFallbackExpression : BoundExpression
    {
        public BoundExpression Left { get; }
        public BoundExpression Right { get; }

        public BoundNullFallbackExpression(ASTNode syntax, BoundExpression left,
            BoundExpression right, TypeSymbol type) : base(syntax, type)
        {
            Left = left;
            Right = right;
        }
    }

    // 类型检查种类（S8a，SYNTAX §3.5/§3.7 的三个类型谓词）
    public enum BoundTypeCheckKind
    {
        Is,      // obj is T：obj 运行时类型为 T 或其子类
        Supers,  // obj supers T：obj 运行时类型为 T 的基类
        With,    // obj with W：obj 运行时类型被 wrapper W 修饰
    }

    // is / supers / with（S8a，SYNTAX §3.5/§3.7；BIL §12.3 直接对应）：
    // 右侧双形态互斥（构造时恰一个非 null）——TargetType = 类型引用静态形态，
    // TargetValue = Type\<T\> 值动态形态（其 Type 为 Type\<T\> 构造类型）。
    // 结果恒 bool（Type 由 P3 定型传入）；不做静态不可能性拒绝
    // （12 is String 不报错，运行时判定）
    public sealed class BoundTypeCheckExpression : BoundExpression
    {
        public BoundTypeCheckKind Kind { get; }
        public BoundExpression Operand { get; }
        public TypeSymbol? TargetType { get; }
        public BoundExpression? TargetValue { get; }

        public BoundTypeCheckExpression(ASTNode syntax, BoundTypeCheckKind kind,
            BoundExpression operand, TypeSymbol? targetType, BoundExpression? targetValue,
            TypeSymbol type) : base(syntax, type)
        {
            // 双形态互斥不变量：静态/动态恰居其一
            if ((targetType == null) == (targetValue == null))
            {
                throw new CompilerInternalException(
                    "BoundTypeCheckExpression 的 TargetType/TargetValue 必须恰一个非 null");
            }
            Kind = kind;
            Operand = operand;
            TargetType = targetType;
            TargetValue = targetValue;
        }
    }

    // typeOf（S8a，SYNTAX §3.7；BIL §12.5 直接对应）：Operand（值形态，
    // 取运行时实际类型）/ TargetType（类型形态）互斥（构造时恰一个非 null）。
    // Type = Type\<T\> 构造类型（P3 定型：值形态 T = 操作数静态类型，
    // 类型形态 T = 目标类型）
    public sealed class BoundTypeOfExpression : BoundExpression
    {
        public BoundExpression? Operand { get; }
        public TypeSymbol? TargetType { get; }

        public BoundTypeOfExpression(ASTNode syntax, BoundExpression? operand,
            TypeSymbol? targetType, TypeSymbol type) : base(syntax, type)
        {
            // 双形态互斥不变量：值/类型恰居其一
            if ((operand == null) == (targetType == null))
            {
                throw new CompilerInternalException(
                    "BoundTypeOfExpression 的 Operand/TargetType 必须恰一个非 null");
            }
            Operand = operand;
            TargetType = targetType;
        }
    }
}
