using System.Collections.Generic;

namespace RigiCompiler
{
    // Bound 表达式节点（S5 最小集 + S7b 首批 + S7c-2 实例成员 + S7d switch
    // + S7e cast/seq + S7f 安全访问/空值回退 + S8a 类型谓词/typeOf
    // + S8c 索引访问 + S11 enum case，SEMANTIC_ROADMAP）：
    // 字面量 / 值引用（局部变量与参数）/ 全局字段引用 / 二元与一元 intrinsic 运算 /
    // 直接调用（无重载）/ new 构造 / if 表达式 / 复合赋值 /
    // this / 实例方法调用 / 实例字段访问 / enum case 构造 / switch 表达式 / cast /
    // seq 表达式 / 安全访问 `?.`（含占位叶子）/ if? 空值回退 / is·supers·with·is .Case /
    // typeOf / 索引访问（getAtIndex·setAtIndex）/ wrapper place（S11）。
    // 字面量值不冗余存储——经 Syntax（LiteralExpressionASTNode.Literal）取。

    // 字面量（整/浮点/字符串/字符/bool/null；Type 由 P3 按字面量种类与上下文定型）
    public sealed class BoundLiteralExpression : BoundExpression
    {
        public BoundLiteralExpression(ASTNode syntax, SemanticSymbol type) : base(syntax, type)
        {
        }
    }

    // 值引用：局部变量（LocalSymbol）或参数（ParameterSymbol）
    public sealed class BoundValueReferenceExpression : BoundExpression
    {
        public SemanticSymbol Symbol { get; }

        public BoundValueReferenceExpression(ASTNode syntax, SemanticSymbol symbol, SemanticSymbol type)
            : base(syntax, type)
        {
            Symbol = symbol;
        }
    }

    // 全局字段引用（Owner == null 的 FieldSymbol；实例字段引用属后续里程碑）
    public sealed class BoundFieldReferenceExpression : BoundExpression
    {
        public FieldSymbol Field { get; }

        public BoundFieldReferenceExpression(ASTNode syntax, FieldSymbol field, SemanticSymbol type)
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
            BoundExpression left, BoundExpression right, SemanticSymbol type)
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
            BoundExpression operand, SemanticSymbol type)
            : base(syntax, type)
        {
            Op = op;
            Operand = operand;
        }
    }

    // 直接函数调用（无重载，S8 才做 ranking）。实参已是绑定后的规范顺序
    // （具名实参已按形参名归位；默认参数填充属 S8）。
    // 仅用于有返回值的调用；void 调用作语句见 BoundCallStatement。
    // TypeArguments：固定泛型实参（显式或推断；非泛型调用为空——P4 发射
    // .generic.T 隐藏实参的依据，§7.2）
    // S9d-2 增补 GenericPack：泛型可变参数包推导产物（null = 无——非
    // 泛型或固定泛型方法；P4 发射在 TypeArguments 之后、普通实参之前，§7.2）
    public sealed class BoundCallExpression : BoundExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public BoundGenericVarArgsArgument? GenericPack { get; }
        // 间接调用（§15.3 callable 协议：对 IndirectTarget 对象虚调用其
        // $$call 实现）；非间接调用为 null
        public bool IsIndirect { get; }
        public BoundExpression? IndirectTarget { get; }

        public BoundCallExpression(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments, SemanticSymbol type,
            IReadOnlyList<SemanticSymbol>? typeArguments = null,
            BoundGenericVarArgsArgument? genericPack = null, bool isIndirect = false,
            BoundExpression? indirectTarget = null)
            : base(syntax, type)
        {
            Method = method;
            Arguments = arguments;
            TypeArguments = typeArguments ?? Array.Empty<SemanticSymbol>();
            GenericPack = genericPack;
            IsIndirect = isIndirect;
            IndirectTarget = indirectTarget;
        }
    }

    // new 构造（SYNTAX §9.3）：Init 为匹配到的构造函数符号；
    // 无显式 init 的零参构造 Init 为 null
    public sealed class BoundNewExpression : BoundExpression
    {
        public MethodSymbol? Init { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundNewExpression(ASTNode syntax, SemanticSymbol constructedType, MethodSymbol? init,
            IReadOnlyList<BoundExpression> arguments)
            : base(syntax, constructedType)
        {
            Init = init;
            Arguments = arguments;
        }
    }

    // 动态 new（SYNTAX §3.7；BIL §14.2 new.indirect）：目标为 Type\<T\> 值
    //（`new t(...)`，TypeValue 非空）或泛型参数（具化构造 `TResult()`，
    // GenericParameter 非空）——两者恰一非空，共用同一套 typeid 构造机制：
    // init 重载解析在运行期按 typeid 完成，目标为抽象类型/enum struct/无
    // 匹配 init 时抛 core.NoSuchMethodException。Arguments 按书写序绑定
    //（无静态 init 形参可对位）；Type = 结果静态类型（Type\<T\> 的 T 或
    // 泛型参数自身）
    public sealed class BoundDynamicNewExpression : BoundExpression
    {
        public BoundExpression? TypeValue { get; }
        public GenericParameterSymbol? GenericParameter { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }

        public BoundDynamicNewExpression(ASTNode syntax, BoundExpression? typeValue,
            GenericParameterSymbol? genericParameter, IReadOnlyList<BoundExpression> arguments,
            SemanticSymbol resultType)
            : base(syntax, resultType)
        {
            TypeValue = typeValue;
            GenericParameter = genericParameter;
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
            BoundValueBlock trueBranch, BoundValueBlock falseBranch, SemanticSymbol type)
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
            BilIntrinsicOp op, BoundExpression value, SemanticSymbol type)
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
        public BoundThisExpression(ASTNode syntax, SemanticSymbol type) : base(syntax, type)
        {
        }
    }

    // 实例方法调用（S7c-2）：Receiver 静态类型上色查找（沿 BaseType 链，
    // 接口 receiver 查接口自身成员；ext 注册成员同路径——P2 已挂目标类型
    // 成员表）。接口方法的调用以接口方法符号引用（分派归 Middleware，
    // BIL §15.1 注释约定）。Arguments 已是规范参数序。
    // S9b 增补 TypeArguments：显式泛型实参（同 BoundCallExpression）
    // S9d-2 增补 GenericPack：泛型可变参数包推导产物（同 BoundCallExpression）
    public sealed class BoundInstanceCallExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public BoundGenericVarArgsArgument? GenericPack { get; }

        public BoundInstanceCallExpression(ASTNode syntax, BoundExpression receiver,
            MethodSymbol method, IReadOnlyList<BoundExpression> arguments, SemanticSymbol type,
            IReadOnlyList<SemanticSymbol>? typeArguments = null,
            BoundGenericVarArgsArgument? genericPack = null)
            : base(syntax, type)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
            TypeArguments = typeArguments ?? Array.Empty<SemanticSymbol>();
            GenericPack = genericPack;
        }
    }

    // 实例字段访问（S7c-2）：Receiver 静态类型上色查找（沿 BaseType 链；
    // ext 注册成员同路径）。裸名实例字段在实例方法体内解析为 this.field
    public sealed class BoundFieldAccessExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public FieldSymbol Field { get; }

        public BoundFieldAccessExpression(ASTNode syntax, BoundExpression receiver,
            FieldSymbol field, SemanticSymbol type) : base(syntax, type)
        {
            Receiver = receiver;
            Field = field;
        }
    }

    // 索引访问（S8c，SYNTAX §13.2）：读形态由 getAtIndex 绑定（Type = 返回
    // 类型）；赋值 place 形态由 setAtIndex 绑定（Type = 元素形参类型）。
    // Operator 符号供测试断言与调试——P4 发射不需要它（BIL §13.6
    // get.array/set.array 不带符号操作数）
    public sealed class BoundIndexExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public BoundExpression Index { get; }
        public MethodSymbol Operator { get; }

        public BoundIndexExpression(ASTNode syntax, BoundExpression receiver,
            BoundExpression index, MethodSymbol op, SemanticSymbol type) : base(syntax, type)
        {
            Receiver = receiver;
            Index = index;
            Operator = op;
        }
    }

    // enum case 构造（S11，SYNTAX §12.1）：固定 case（Arguments 空）与参数化
    // case（Arguments = 洞实参，规范序 = 洞签名序 = init 参数序——调用点乱序
    // 具名实参已按洞名归位）。Type = 宿主 enum（定义级符号——泛型 enum 的
    // case 本阶段归口，无构造形态）。FixedArguments = 声明点模板绑定的
    // 固定实参缓存（BindEnvironment，init 参数序、洞位置 null 占位；
    // null = 模板信息缺失）——P4a 按 init 参数序组合「固定实参 + 洞实参」
    // 发 new.case（BIL §14.3），P4b 发射 §8.5 case 构造与判别比较
    //（§12.3 type.is.case）消费
    public sealed class BoundEnumCaseExpression : BoundExpression
    {
        public EnumCaseSymbol Case { get; }
        // 洞实参（规范序；固定 case 为空列表）
        public IReadOnlyList<BoundExpression> Arguments { get; }
        // 声明点模板固定实参（init 参数序，洞位置 null 占位；null = 缺失）
        public IReadOnlyList<BoundExpression?>? FixedArguments { get; }
        // 合成序列化构造已经按 init 参数序组装；仍由 BIL 校验真实 init 签名。
        public bool ArgumentsAreInitArguments { get; }

        public BoundEnumCaseExpression(ASTNode syntax, EnumCaseSymbol caseSymbol,
            IReadOnlyList<BoundExpression> arguments,
            IReadOnlyList<BoundExpression?>? fixedArguments = null,
            bool argumentsAreInitArguments = false, TypeSymbol? constructedType = null)
            : base(syntax, constructedType ?? caseSymbol.Owner)
        {
            Case = caseSymbol;
            Arguments = arguments;
            FixedArguments = fixedArguments;
            ArgumentsAreInitArguments = argumentsAreInitArguments;
        }
    }

    // wrapper place 访问（S11，SYNTAX §14.1/§14.5）：`obj:W`——绑定在宿主上的
    // 那份 wrapper 的只读存储位置。它只作成员访问的接收者出现（字段读写、
    // 方法调用、索引），永不作为完整表达式的值产出——P3 绑定期封死：链末
    // Colon 段按赋值/取值分别诊断（全拦截面收口于 PathFacility 一处）。
    // Application = 命中的 wrapper 应用记录（S11a：Entity 恰一泛型参数时
    // Wrapper 为 TTarget 代入后的构造类型——泛型实参显形；Value/Method 为
    // 定义本身）。Type = Wrapper。P4 发射（M88：操作数改 wrapper(W)，
    // 隐藏字段合成归 Middleware）
    public sealed class BoundWrapperAccessExpression : BoundExpression
    {
        public BoundExpression Receiver { get; }
        public WrapperApplication Application { get; }
        public TypeSymbol Wrapper => Application.Wrapper;

        public BoundWrapperAccessExpression(ASTNode syntax, BoundExpression receiver,
            WrapperApplication application) : base(syntax, application.Wrapper)
        {
            Receiver = receiver;
            Application = application;
        }
    }

    // proxy 体 self（M88，ARCH §5.2 / SYNTAX §14.2）：模板态下 self = 被修饰
    // 对象角色，Type = ProxyBodyState.SelfType（TTarget 泛型参数）。P4 发射
    // get.self 占位指令（下一棒）
    public sealed class BoundSelfExpression : BoundExpression
    {
        public BoundSelfExpression(ASTNode syntax, SemanticSymbol type) : base(syntax, type)
        {
        }
    }

    // proxy 体 inner(...)（M88，ARCH §5.2 / SYNTAX §14.2；#27⑦ 包透传）：
    // 模板态占位调用。Arguments = 源码层显式值实参绑定产物；
    // ForwardedGenericPacks = 当前 proxy 方法声明序中的可变泛型包
    // （IsVariadic/IsNamedVariadic；固定泛型不入列）——P4b 按 BIL §7.2
    // 序前置为 $.generic.<Name>，再接值实参（含 .kwargs./.vargs.）。
    // Type = proxy 声明返回类型（void 时本节点仅出现在语句位置，Type 取
    // Any 占位且 IsVoid = true）。P4 发射 invoke fn(..inner) 占位调用（下一棒）
    public sealed class BoundInnerCallExpression : BoundExpression
    {
        public IReadOnlyList<BoundExpression> Arguments { get; }
        public IReadOnlyList<GenericParameterSymbol> ForwardedGenericPacks { get; }
        public bool IsVoid { get; }

        public BoundInnerCallExpression(ASTNode syntax, IReadOnlyList<BoundExpression> arguments,
            SemanticSymbol type, bool isVoid = false,
            IReadOnlyList<GenericParameterSymbol>? forwardedGenericPacks = null)
            : base(syntax, type)
        {
            Arguments = arguments;
            ForwardedGenericPacks = forwardedGenericPacks
                ?? Array.Empty<GenericParameterSymbol>();
            IsVoid = isVoid;
        }
    }

    // lambda（SYNTAX §5.2 对象模型）：表达式的静态类型 = 编译期生成的隐藏类
    // （..lambda..UUID，与声明位置同命名空间，继承 core::Func/Action/AsyncFunc/
    // AsyncAction 之一）。捕获经隐藏类 init 以 Cell/ReadonlyCell 字段传入
    // （this 捕获为普通字段）；CallBody 是 $$call 运算符的函数体，InitBody
    // 是逐字段赋值的构造函数体。P4a 降级为普通 new 构造，无 BIL 特例
    public sealed class BoundLambdaExpression : BoundExpression
    {
        public LambdaExpressionASTNode LambdaSyntax { get; }
        // null = void lambda（基类 Action/AsyncAction）
        public SemanticSymbol? ReturnType { get; }
        public IReadOnlySet<SemanticSymbol> CapturedSymbols { get; }
        public LambdaClosureInfo Closure { get; }
        public BoundFunctionBody CallBody { get; }
        public BoundFunctionBody InitBody { get; }

        public BoundLambdaExpression(LambdaExpressionASTNode syntax,
            SemanticSymbol? returnType, IReadOnlySet<SemanticSymbol> capturedSymbols,
            LambdaClosureInfo closure, BoundFunctionBody callBody, BoundFunctionBody initBody,
            TypeSymbol expressionType)
            : base(syntax, expressionType)
        {
            LambdaSyntax = syntax;
            ReturnType = returnType;
            CapturedSymbols = capturedSymbols;
            Closure = closure;
            CallBody = callBody;
            InitBody = initBody;
        }
    }

    // await（S13）：Operand 必须是精确 core.coroutine.Task 或 Task<T>。
    // 无结果形态只由语句位置构造；其 Type 保留 Task 类型，避免用 Any
    // 伪造一个可参与推断的结果。
    public sealed class BoundAwaitExpression : BoundExpression
    {
        public BoundExpression Operand { get; }
        public bool HasResult { get; }
        public SemanticSymbol? ResultType { get; }

        public BoundAwaitExpression(ASTNode syntax, BoundExpression operand,
            bool hasResult, SemanticSymbol? resultType)
            : base(syntax, resultType ?? operand.Type)
        {
            Operand = operand;
            HasResult = hasResult;
            ResultType = resultType;
        }
    }

    // 直接基类实现调用：P4 必须发射保留符号 ..super，不能重入普通派发链。
    public sealed class BoundSuperCallExpression : BoundExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<BoundExpression> Arguments { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public BoundGenericVarArgsArgument? GenericPack { get; }
        public bool IsVoid { get; }

        public BoundSuperCallExpression(ASTNode syntax, MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments, SemanticSymbol type,
            IReadOnlyList<SemanticSymbol> typeArguments,
            BoundGenericVarArgsArgument? genericPack, bool isVoid = false) : base(syntax, type)
        {
            Method = method;
            Arguments = arguments;
            TypeArguments = typeArguments;
            GenericPack = genericPack;
            IsVoid = isVoid;
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
            SemanticSymbol type)
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
            SemanticSymbol type) : base(syntax, type)
        {
            Selector = selector;
        }
    }

    // cast（S7e，SYNTAX §11：as / as?；BIL §12.1/§12.2）：TargetType 是转换
    // 目标类型（as 与 as? 同形）；节点 Type 是表达式结果类型——as 时即
    // TargetType，as? 时为 Nullable<TargetType>（P3 定型，P4 不再区分包装）。
    // 可转性不做静态拒绝（as 失败是运行时 core.CastException；castTo/castFrom
    // 名字分析归 S8f 落地）。
    // Conversion（S8f，SYNTAX §3.5 转换优先级）：名字分析产物——源类型的
    // castTo 优先、目标类型的 castFrom 兜底，均为适用候选；null = 无用户
    // 定义转换，走内建引用视图/数值转换（BIL §12.1 第 3 条）。记录供测试
    // 断言与调试；P4 仍发 cast（BIL §12.1 语义含 castTo/castFrom，运行时
    // 自行分派——S8f 无 P4 面）。
    public sealed class BoundCastExpression : BoundExpression
    {
        public BoundExpression Source { get; }
        public SemanticSymbol TargetType { get; }
        // true = as?（失败产 null）；false = as（失败抛 core.CastException）
        public bool IsSafe { get; }
        // 名字分析选中的用户转换运算符（castTo/castFrom）；null = 内建
        public MethodSymbol? Conversion { get; }

        public BoundCastExpression(ASTNode syntax, BoundExpression source,
            SemanticSymbol targetType, bool isSafe, SemanticSymbol type,
            MethodSymbol? conversion = null) : base(syntax, type)
        {
            Source = source;
            TargetType = targetType;
            IsSafe = isSafe;
            Conversion = conversion;
        }
    }

    // smart cast 标记（S8b，SYNTAX §3.5；ARCH §5.2「P3 只做分析与标记」）：
    // 收窄区域内对被收窄引用（局部/参数/const 字段链/switch 占位）的包装——
    // Type = NarrowedType（成员解析自然按收窄类型）；P4a 物化为显式 cast
    // （ARCH §6.1「smart cast 标记 → 显式 cast」，T? → T 的 unwrap 与子类型
    // 收窄同属 §12.1 形态）
    public sealed class BoundSmartCastExpression : BoundExpression
    {
        public BoundExpression Operand { get; }

        // 收窄后类型（= 节点 Type；显式字段供描述器与 P4a 物化目标）
        public SemanticSymbol NarrowedType { get; }

        public BoundSmartCastExpression(ASTNode syntax, BoundExpression operand,
            SemanticSymbol narrowedType) : base(syntax, narrowedType)
        {
            Operand = operand;
            NarrowedType = narrowedType;
        }
    }

    // seq 表达式（S7e，SYNTAX §10.2）：体即值块（复用 BoundValueBlock，
    // 取值规则同 if 表达式分支体）；using 绑定列表为 P3 产物。
    // 壳存在的理由：BoundValueBlock 是 BoundNode 非表达式，BindExpression
    // 必须返回表达式节点；Type = Body.ValueType
    public sealed class BoundSeqExpression : BoundExpression
    {
        public BoundValueBlock Body { get; }
        public IReadOnlyList<BoundUsingBinding> UsingBindings { get; internal set; } =
            Array.Empty<BoundUsingBinding>();

        public BoundSeqExpression(ASTNode syntax, BoundValueBlock body, SemanticSymbol type,
            IReadOnlyList<BoundUsingBinding>? usingBindings = null)
            : base(syntax, type)
        {
            Body = body;
            UsingBindings = usingBindings ?? Array.Empty<BoundUsingBinding>();
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
            SemanticSymbol type) : base(syntax, type)
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
        public BoundSafeAccessReceiverExpression(ASTNode syntax, SemanticSymbol type)
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
            BoundExpression right, SemanticSymbol type) : base(syntax, type)
        {
            Left = left;
            Right = right;
        }
    }

    // 类型检查种类（S8a，SYNTAX §3.5/§3.7 的三个类型谓词；S11 增补 §12.3）
    public enum BoundTypeCheckKind
    {
        Is,      // obj is T：obj 运行时类型为 T 或其子类
        Supers,  // obj supers T：obj 运行时类型为 T 的基类
        With,    // obj with W：obj 运行时类型被 wrapper W 修饰
        IsCase,  // obj is .Case（S11，SYNTAX §12.3）：enum 隐藏判别字段比较
    }

    // is / supers / with（S8a，SYNTAX §3.5/§3.7；BIL §12.3 直接对应）：
    // 右侧双形态互斥（构造时恰一个非 null）——TargetType = 类型引用静态形态，
    // TargetValue = Type\<T\> 值动态形态（其 Type 为 Type\<T\> 构造类型）。
    // 结果恒 bool（Type 由 P3 定型传入）；不做静态不可能性拒绝
    // （12 is String 不报错，运行时判定）。
    // S11 增补第三形态（SYNTAX §12.3）：Kind == IsCase 时 Case 承载匹配
    // 的 case 符号（TargetType/TargetValue 均 null）——只查隐藏判别字段，
    // 不比较 payload，也不改变静态类型；smart cast 收窄事实只匹配
    // 「Kind: Is, TargetType: { }」（ConditionFactsExtractor），IsCase
    // 天然不触发收窄（§12.3 定稿口径，BIL §12.3 type.is.case 对应）
    public sealed class BoundTypeCheckExpression : BoundExpression
    {
        public BoundTypeCheckKind Kind { get; }
        public BoundExpression Operand { get; }
        public SemanticSymbol? TargetType { get; }
        public BoundExpression? TargetValue { get; }
        // enum case 判别匹配的 case 符号（S11；仅 Kind == IsCase 时非 null）
        public EnumCaseSymbol? Case { get; }

        public BoundTypeCheckExpression(ASTNode syntax, BoundTypeCheckKind kind,
            BoundExpression operand, SemanticSymbol? targetType, BoundExpression? targetValue,
            SemanticSymbol type, EnumCaseSymbol? caseSymbol = null) : base(syntax, type)
        {
            // 目标形态互斥不变量：IsCase 恰带 Case（双槽均 null）；
            // 其余 Kind 静态/动态双形态恰居其一
            if (kind == BoundTypeCheckKind.IsCase
                ? caseSymbol == null || targetType != null || targetValue != null
                : (targetType == null) == (targetValue == null))
            {
                throw new CompilerInternalException(
                    "BoundTypeCheckExpression 目标形态不合法（IsCase 须恰带 Case；" +
                    "其余 Kind 的 TargetType/TargetValue 必须恰一个非 null）");
            }
            Kind = kind;
            Operand = operand;
            TargetType = targetType;
            TargetValue = targetValue;
            Case = caseSymbol;
        }
    }

    // typeOf（S8a，SYNTAX §3.7；BIL §12.5 直接对应）：Operand（值形态，
    // 取运行时实际类型）/ TargetType（类型形态）互斥（构造时恰一个非 null）。
    // Type = Type\<T\> 构造类型（P3 定型：值形态 T = 操作数静态类型，
    // 类型形态 T = 目标类型）
    public sealed class BoundPlaceOfExpression : BoundExpression
    {
        public BoundExpression Operand { get; }
        public CellStorageInfo? Storage { get; }
        public bool HasDynamicTarget { get; }

        public BoundPlaceOfExpression(ASTNode syntax, BoundExpression operand,
            TypeSymbol type, CellStorageInfo? storage, bool hasDynamicTarget = false) : base(syntax, type)
        {
            Operand = operand;
            Storage = storage;
            HasDynamicTarget = hasDynamicTarget;
        }
    }

    public sealed class BoundTypeOfExpression : BoundExpression
    {
        public BoundExpression? Operand { get; }
        public SemanticSymbol? TargetType { get; }

        public BoundTypeOfExpression(ASTNode syntax, BoundExpression? operand,
            SemanticSymbol? targetType, SemanticSymbol type) : base(syntax, type)
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

    // 可变参数包实参（S9d，SYNTAX §4.3/§7.2）：调用点归包的剩余实参——
    // 作为 BoundCall 规范参数序的最后一个元素（vargs 位置包 / kwargs
    // 具名包）。Type = Array\<Any\> 构造（BIL .array<.any>；RUNTIME §10
    // 值进统一 Any 胖值槽）。元素保持实参原类型绑定，P4 打包时装箱 cast
    public sealed class BoundVarArgsArgument : BoundExpression
    {
        // true = 具名包（kwargs，元素为「名 → 值」对）；false = 位置包（vargs）
        public bool IsNamed { get; }
        // 位置包元素（IsNamed == false 时非空）
        public IReadOnlyList<BoundExpression> Values { get; }
        // 具名包元素（IsNamed == true 时非空）
        public IReadOnlyList<(string Name, BoundExpression Value)> NamedValues { get; }

        public BoundVarArgsArgument(ASTNode syntax, bool isNamed,
            IReadOnlyList<BoundExpression> values,
            IReadOnlyList<(string Name, BoundExpression Value)>? namedValues,
            SemanticSymbol type)
            : base(syntax, type)
        {
            IsNamed = isNamed;
            Values = values;
            NamedValues = namedValues ?? Array.Empty<(string, BoundExpression)>();
        }
    }

    // 泛型可变参数包推导产物（S9d-2，SYNTAX §4.3/§7.1）：对「泛型参数全
    // 为可变」的方法，类型实参由对应值实参的静态类型推导——位置包
    // （TArgs...）携带推导的类型实参序列（← 归包位置实参的静态类型）；
    // 具名包（named TValues...）携带「名 → 静态类型」映射（← 归包具名
    // 实参）。非 BoundExpression（无值语义，P4 打包 .array<.typeid<.any>>
    // / .map<.string, .typeid<.any>> 隐藏实参，§7.2 序在固定泛型后普通
    // 实参前——BoundCall 以独立槽承载）
    public sealed class BoundGenericVarArgsArgument : BoundNode
    {
        // true = 具名包（元素为「名 → 类型」对）；false = 位置包
        public bool IsNamed { get; }
        // 位置包推导类型序列（IsNamed == false 时非空）
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        // 具名包推导类型映射（IsNamed == true 时非空）
        public IReadOnlyList<(string Name, SemanticSymbol Type)> NamedTypes { get; }

        public BoundGenericVarArgsArgument(ASTNode syntax, bool isNamed,
            IReadOnlyList<SemanticSymbol> typeArguments,
            IReadOnlyList<(string Name, SemanticSymbol Type)>? namedTypes)
            : base(syntax)
        {
            IsNamed = isNamed;
            TypeArguments = typeArguments;
            NamedTypes = namedTypes ?? Array.Empty<(string, SemanticSymbol)>();
        }
    }
}
