using System.Collections.Generic;

namespace LatteCompiler
{
    // Lowered 表达式节点（S6 最小集 + S7a 补齐 + S7b 脱糖 + S7c-2 实例成员
    // + S7e cast + S8a 类型谓词/typeOf + S8c 索引访问 + S11 enum case，
    // SEMANTIC_ROADMAP）：
    // 字面量 / 值引用 / 全局字段引用 / 二元与一元 intrinsic 运算 / 带返回值调用 /
    // new 构造 / 编译期常量（短路脱糖产物）/ this / 实例方法调用 / 实例字段访问 /
    // cast / is·supers·with / typeOf / 索引访问 / enum case 构造与判别匹配。
    // 字面量值不冗余存储——经 Origin.Syntax（LiteralExpressionASTNode.Literal）取。
    // S7b 起部分节点构造的 origin 参数放宽为 BoundNode：脱糖合成节点无逐一
    // 对应的 Bound 节点，Origin 按 ARCH §5.1 约定指向最近的语法来源。

    // 字面量（Type 由 P3 定型、值在 Origin 链上；无额外字段）
    public sealed class LoweredLiteralExpression : LoweredExpression
    {
        public LoweredLiteralExpression(BoundLiteralExpression origin) : base(origin)
        {
        }
    }

    // 编译期常量（P4a 合成节点，S7b）：bool 短路展开（BIL §11.3）的 true/false
    // 是唯一来源，S7b 仅 bool。Origin 约定 = 最近的语法来源（and/or 表达式本身
    // 的 Bound 节点）；Type 自带（语义上常量类型由自身携带，不走 Origin 透传）
    public sealed class LoweredConstantExpression : LoweredExpression
    {
        public object Value { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredConstantExpression(BoundNode origin, object value, SemanticSymbol type)
            : base(origin)
        {
            Value = value;
            this.type = type;
        }
    }

    // 值引用：局部变量（LocalSymbol）或参数（ParameterSymbol）。
    // 局部取符号自身类型（P3 构造 Bound 值引用时 Type 恒等于局部符号类型）。
    // 参数透传 Origin 的 Bound 表达式定型（P3 对可变参数引用定型为
    // Array\<元素类型\>——体内视角是包数组（S9d），与声明的元素类型不同；
    // ValueReferenceRewriter 路径 Origin 恒为表达式）；合成路径（Origin
    // 非表达式节点）回退符号声明类型
    public sealed class LoweredValueReferenceExpression : LoweredExpression
    {
        public SemanticSymbol Symbol { get; }

        public override SemanticSymbol Type => Symbol switch
        {
            // .breakid 局部（Type null）不作值引用——capability 不可读
            // （BIL §9.3），LoweredLoop/LoweredLoopControl 直接持有符号
            LocalSymbol local => local.Type ?? throw new CompilerInternalException(
                ".breakid 局部不能作值引用: " + local.Name),
            ParameterSymbol parameter => Origin is BoundExpression boundExpression
                ? boundExpression.Type
                : parameter.Type!,
            _ => throw new CompilerInternalException("未知值引用符号: " + Symbol.GetType().Name),
        };

        public LoweredValueReferenceExpression(BoundNode origin,
            SemanticSymbol symbol) : base(origin)
        {
            Symbol = symbol;
        }
    }

    // 全局/static 字段引用（实例字段引用属后续里程碑）。
    // Type 默认走 Origin 透传；显式传入 = 合成路径（统一 cell 存储：
    // cell 化静态字段的 cell 对象引用，Type = 隐藏子类而非值类型）
    public sealed class LoweredFieldReferenceExpression : LoweredExpression
    {
        public FieldSymbol Field { get; }
        private readonly SemanticSymbol? type;

        public override SemanticSymbol Type => type ?? base.Type;

        public LoweredFieldReferenceExpression(BoundNode origin,
            FieldSymbol field, SemanticSymbol? type = null) : base(origin)
        {
            Field = field;
            this.type = type;
        }
    }

    // 二元 intrinsic 运算（BIL §11；bool 短路 and/or 在 S7b 已脱糖为
    // LoweredIfStatement 展开，此节点不再承载 And/Or）。
    // S7d 增补显式 Type：脱糖合成节点（pattern switch 值分支的 cmp.eq 条件）
    // 无类型相符的 Bound 节点可透传——Origin 指 case 匹配表达式（常量，
    // 类型与结果 bool 不同），显式 Type 优先于透传（先例：
    // LoweredConstantExpression/LoweredInstanceCallExpression）
    public sealed class LoweredBinaryExpression : LoweredExpression
    {
        public BilIntrinsicOp Op { get; }
        public LoweredExpression Left { get; }
        public LoweredExpression Right { get; }
        private readonly SemanticSymbol? type;

        public override SemanticSymbol Type => type ?? base.Type;

        public LoweredBinaryExpression(BoundNode origin, BilIntrinsicOp op,
            LoweredExpression left, LoweredExpression right, SemanticSymbol? type = null) : base(origin)
        {
            Op = op;
            Left = left;
            Right = right;
            this.type = type;
        }
    }

    // 一元 intrinsic 运算（opposite / not / bin.not）
    public sealed class LoweredUnaryExpression : LoweredExpression
    {
        public BilIntrinsicOp Op { get; }
        public LoweredExpression Operand { get; }

        public LoweredUnaryExpression(BoundUnaryExpression origin, BilIntrinsicOp op,
            LoweredExpression operand) : base(origin)
        {
            Op = op;
            Operand = operand;
        }
    }

    // 带返回值直接调用（void 调用作语句见 LoweredCallStatement）。
    // S9e 增补 TypeArguments：显式泛型实参（P4b 调用点物化 .generic.*
    // 隐藏实参的依据，§7.2）
    // S9d-2 增补 GenericPack：泛型可变参数包推导产物（null = 无；P4b
    // 在 TypeArguments 之后打包物化，§7.2 序）
    public sealed class LoweredCallExpression : LoweredExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public LoweredGenericVarArgsArgument? GenericPack { get; }
        // 间接调用（§15.3 callable 协议）：物化目标对象表达式后虚调用其
        // $$call；非间接调用为 null
        public bool IsIndirect { get; }
        public LoweredExpression? IndirectTarget { get; }

        public LoweredCallExpression(BoundCallExpression origin, MethodSymbol method,
            IReadOnlyList<LoweredExpression> arguments,
            IReadOnlyList<SemanticSymbol>? typeArguments = null,
            LoweredGenericVarArgsArgument? genericPack = null, bool isIndirect = false,
            LoweredExpression? indirectTarget = null) : base(origin)
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
    public sealed class LoweredNewExpression : LoweredExpression
    {
        public MethodSymbol? Init { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        // 合成路径显式类型（Origin 非 BoundNewExpression 时必带——闭包 cell
        // 构造与 lambda 隐藏类构造，SYNTAX §5.2）；null = Origin 透传
        private readonly SemanticSymbol? type;

        public override SemanticSymbol Type => type ?? base.Type;

        public LoweredNewExpression(BoundNode origin, MethodSymbol? init,
            IReadOnlyList<LoweredExpression> arguments, SemanticSymbol? type = null)
            : base(origin)
        {
            Init = init;
            Arguments = arguments;
            this.type = type;
        }
    }

    // this 引用（S7c-2；emitter 映射 $.this 变量操作数，零指令）；
    // 合成路径显式类型（lambda 构造的 this 捕获实参——Origin 是 lambda
    // 节点，透传类型是隐藏类而非外层 this 类型，§5.2）
    public sealed class LoweredThisExpression : LoweredExpression
    {
        private readonly SemanticSymbol? type;

        public override SemanticSymbol Type => type ?? base.Type;

        public LoweredThisExpression(BoundNode origin, SemanticSymbol? type = null) : base(origin)
        {
            this.type = type;
        }
    }

    // cell 对象引用（SYNTAX §5.2 闭包模型）：被捕获局部/参数的 cell 变量
    // 本身的引用——零指令取操作数（局部 = 原名 var；参数 = .c.<名> 合成
    // 局部）。只出现在两个位置：cell 读写调用（getValue/setValue）的
    // receiver、lambda 隐藏类构造的 init 实参。值语义的读取永远经
    // getValue 调用，不引用本节点
    public sealed class LoweredCellReferenceExpression : LoweredExpression
    {
        public SemanticSymbol Symbol { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredCellReferenceExpression(BoundNode origin, SemanticSymbol symbol,
            SemanticSymbol cellType) : base(origin)
        {
            Symbol = symbol;
            type = cellType;
        }
    }

    // 实例方法调用（S7c-2；BIL §7.3/§15.1：receiver 求值作首实参）。
    // 接口方法符号引用时分派归 Middleware（注释约定）。
    // Type 自带不走 Origin 透传：for 脱糖（S7c-2）合成节点的 Origin 是
    // BoundLoop（语句而非表达式，无法透传）；恒等降级路径由调用方传入
    // 与 Origin 相同的类型（同一来源两形态统一）。
    // S9e 增补 TypeArguments：显式泛型实参（同 LoweredCallExpression）
    // S9d-2 增补 GenericPack：泛型可变参数包推导产物（同 LoweredCallExpression）
    public sealed class LoweredInstanceCallExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public LoweredGenericVarArgsArgument? GenericPack { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredInstanceCallExpression(BoundNode origin, LoweredExpression receiver,
            MethodSymbol method, IReadOnlyList<LoweredExpression> arguments, SemanticSymbol type,
            IReadOnlyList<SemanticSymbol>? typeArguments = null,
            LoweredGenericVarArgsArgument? genericPack = null)
            : base(origin)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
            TypeArguments = typeArguments ?? Array.Empty<SemanticSymbol>();
            GenericPack = genericPack;
            this.type = type;
        }
    }

    // 实例字段访问（S7c-2；BIL §13.3 get.field/set.field）
    public sealed class LoweredFieldAccessExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public FieldSymbol Field { get; }
        // Type 默认走 Origin 透传（恒等降级路径，P3 已含替换后类型）；
        // 显式传入 = 合成路径（S7f 解构脱糖等 Origin 非表达式节点的场景，
        // 先例：LoweredCastExpression 的 Type 自带）
        private readonly SemanticSymbol? type;

        public override SemanticSymbol Type => type ?? base.Type;

        public LoweredFieldAccessExpression(BoundNode origin,
            LoweredExpression receiver, FieldSymbol field, SemanticSymbol? type = null) : base(origin)
        {
            Receiver = receiver;
            Field = field;
            this.type = type;
        }
    }

    // 索引访问（S8c；BIL §13.6 get.array/set.array）：读形态与赋值 place
    // 形态共用（指令选择归 P4b 按所在位置——值位置 get.array /
    // 赋值目标 set.array）。不携带 Operator 符号——§13.6 指令无符号
    // 操作数，验证器按「collection + index + result/element」严格
    // 三元组重查实现。Type 走 Origin 透传（读 = getAtIndex 返回类型，
    // 写 = setAtIndex 元素形参类型，P3 已定型）
    public sealed class LoweredIndexExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public LoweredExpression Index { get; }
        // variadic 参数索引（BIL §7.1）：Type 覆盖为容器 ABI 元素类型
        // （位置包 .any / 具名包 Pair<String, Any>——与 .vargs./.kwargs.
        // 隐藏条目声明对齐，get.array/set.array 按容器声明推元素期望）；
        // null = 透传 Origin 的 P3 定型（常规路径）
        private readonly SemanticSymbol? type;

        public override SemanticSymbol Type => type ?? base.Type;

        public LoweredIndexExpression(BoundIndexExpression origin,
            LoweredExpression receiver, LoweredExpression index,
            SemanticSymbol? type = null) : base(origin)
        {
            Receiver = receiver;
            Index = index;
            this.type = type;
        }
    }

    // wrapper 值拷贝（S11c，BIL §12.4 get.wrapper）：wrapper place 作成员
    // 访问接收者（字段读/方法调用/索引读）时的物化——取得绑定在宿主上的
    // 那份 wrapper 的值拷贝。Source = 宿主值（嵌套 place 链经外层
    // get.wrapper 产物逐级物化）；Wrapper = 目标 wrapper 类型（Entity
    // 应用可为 TTarget 代入后的构造类型）。Type 走 Origin 透传（Origin
    // 恒为 BoundWrapperAccessExpression，其 Type = Wrapper）
    public sealed class LoweredGetWrapperExpression : LoweredExpression
    {
        public LoweredExpression Source { get; }
        public TypeSymbol Wrapper { get; }

        public LoweredGetWrapperExpression(BoundWrapperAccessExpression origin,
            LoweredExpression source, TypeSymbol wrapper) : base(origin)
        {
            Source = source;
            Wrapper = wrapper;
        }
    }

    // 字段-Value wrapper 值拷贝（M84，BIL §12.4 get.wrapper.field）：
    // 从属主对象的特定字段应用取得 wrapper 值拷贝，供方法调用/索引读
    // 复用普通 invoke/get.array。Object = 字段属主；HostField = 带
    // Value wrapper 应用的实例字段；Wrapper = 应用类型。Type 走 Origin
    public sealed class LoweredGetFieldWrapperExpression : LoweredExpression
    {
        public LoweredExpression Object { get; }
        public FieldSymbol HostField { get; }
        public TypeSymbol Wrapper { get; }

        public LoweredGetFieldWrapperExpression(BoundWrapperAccessExpression origin,
            LoweredExpression objectValue, FieldSymbol hostField, TypeSymbol wrapper)
            : base(origin)
        {
            Object = objectValue;
            HostField = hostField;
            Wrapper = wrapper;
        }
    }

    // wrapper place 字段写 place（S11c/M88，BIL §13.3 set.wrapper.field）：
    // 仅作赋值目标，读侧一律 Materialize + 普通 LoweredFieldAccessExpression。
    // Receiver = 终极宿主值；PlaceChain = 寻址语义链（最外层→最内层）：
    //   TypeSymbol = 类型/Entity 应用（投影 wrapper(W)）；
    //   FieldSymbol = 字段-Value 应用的 HOST_FIELD（投影 field(F)；
    //     字段应用编码为相邻 FieldSymbol + TypeSymbol 对）。
    // Field = 最内层目标实例字段。Type 自带
    public sealed class LoweredWrapperFieldExpression : LoweredExpression
    {
        public LoweredExpression Receiver { get; }
        public IReadOnlyList<SemanticSymbol> PlaceChain { get; }
        public FieldSymbol Field { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredWrapperFieldExpression(BoundNode origin, LoweredExpression receiver,
            IReadOnlyList<SemanticSymbol> placeChain, FieldSymbol field, SemanticSymbol type)
            : base(origin)
        {
            Receiver = receiver;
            PlaceChain = placeChain;
            Field = field;
            this.type = type;
        }
    }

    // proxy 体 self（M88，BIL §12.5 get.self）：Type 透传（= TTarget）
    public sealed class LoweredGetSelfExpression : LoweredExpression
    {
        public LoweredGetSelfExpression(BoundSelfExpression origin) : base(origin)
        {
        }
    }

    // proxy 体 inner(...)（M88，BIL §15.4 invoke fn(..inner) / invoke.noret；
    // #27⑦）：Arguments 已下降；ForwardedGenericPacks 透传自 Bound
    // （P4b 前置 $.generic.<Name>）；Type 透传（void 时 IsVoid，P4b 选 noret）
    public sealed class LoweredCallInnerExpression : LoweredExpression
    {
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        public IReadOnlyList<GenericParameterSymbol> ForwardedGenericPacks { get; }
        public bool IsVoid { get; }

        public LoweredCallInnerExpression(BoundInnerCallExpression origin,
            IReadOnlyList<LoweredExpression> arguments) : base(origin)
        {
            Arguments = arguments;
            ForwardedGenericPacks = origin.ForwardedGenericPacks;
            IsVoid = origin.IsVoid;
        }
    }

    // await（S13，P4a 同构节点）：挂起点不退化为 invoke。
    public sealed class LoweredAwaitExpression : LoweredExpression
    {
        public LoweredExpression Operand { get; }
        public bool HasResult { get; }
        public SemanticSymbol? ResultType { get; }

        public LoweredAwaitExpression(BoundAwaitExpression origin,
            LoweredExpression operand) : base(origin)
        {
            Operand = operand;
            HasResult = origin.HasResult;
            ResultType = origin.ResultType;
        }
    }

    // 直接基类调用的独立 lowered 标记；发射目标固定为 fn(..super)。
    public sealed class LoweredSuperCallExpression : LoweredExpression
    {
        public MethodSymbol Method { get; }
        public IReadOnlyList<LoweredExpression> Arguments { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public LoweredGenericVarArgsArgument? GenericPack { get; }
        public bool IsVoid { get; }

        public LoweredSuperCallExpression(BoundSuperCallExpression origin,
            IReadOnlyList<LoweredExpression> arguments, LoweredGenericVarArgsArgument? genericPack)
            : base(origin)
        {
            Method = origin.Method;
            Arguments = arguments;
            TypeArguments = origin.TypeArguments;
            GenericPack = genericPack;
            IsVoid = origin.IsVoid;
        }
    }

    // cast（S7e；BIL §12.1/§12.2 直接对应）：as → cast、as? → cast.safe。
    // TargetType 是转换目标类型（指令的 type 操作数）；Type 是表达式结果
    // 类型（as 即 TargetType，as? 为 Nullable<TargetType>——P3 已定型）。
    // Type 自带不走 Origin 透传：命名 catch 体头的合成节点 Origin 是
    // BoundCatchClause（语句节点无法透传）；恒等降级路径传入
    // BoundCastExpression.Type（同一来源两形态统一，先例：
    // LoweredInstanceCallExpression）
    public sealed class LoweredCastExpression : LoweredExpression
    {
        public LoweredExpression Source { get; }
        public SemanticSymbol TargetType { get; }
        // true = as?（cast.safe，失败产 null）；false = as（cast，失败抛异常）
        public bool IsSafe { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredCastExpression(BoundNode origin, LoweredExpression source,
            SemanticSymbol targetType, bool isSafe, SemanticSymbol type) : base(origin)
        {
            Source = source;
            TargetType = targetType;
            IsSafe = isSafe;
            this.type = type;
        }
    }

    // is / supers / with（S8a；BIL §12.3 直接对应）：静态形态
    // type.is/type.supers/type.with，动态形态（TargetValue）加 .indirect。
    // 双形态互斥同 Bound 侧（构造时恰一个非 null）；Kind 复用 Bound 侧
    // 枚举（Lowering → Semantic 单向依赖，与构造参数回指 Bound 节点同理）。
    // S11 增补第三形态（SYNTAX §12.3）：Kind == IsCase 时 Case 承载匹配
    // 的 case 符号（TargetType/TargetValue 均 null）——P4b 发射独立的
    // type.is.case 指令，不走 type.is/supers/with 家族。
    // Type 自带不走 Origin 透传（恒等降级路径传入 Bound.Type，先例：
    // LoweredCastExpression）
    public sealed class LoweredTypeCheckExpression : LoweredExpression
    {
        public BoundTypeCheckKind Kind { get; }
        public LoweredExpression Operand { get; }
        public SemanticSymbol? TargetType { get; }
        public LoweredExpression? TargetValue { get; }
        // enum case 判别匹配的 case 符号（S11；仅 Kind == IsCase 时非 null）
        public EnumCaseSymbol? Case { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredTypeCheckExpression(BoundNode origin, BoundTypeCheckKind kind,
            LoweredExpression operand, SemanticSymbol? targetType,
            LoweredExpression? targetValue, SemanticSymbol type,
            EnumCaseSymbol? caseSymbol = null) : base(origin)
        {
            // 目标形态互斥不变量同 Bound 侧：IsCase 恰带 Case（双槽均
            // null）；其余 Kind 静态/动态恰居其一
            if (kind == BoundTypeCheckKind.IsCase
                ? caseSymbol == null || targetType != null || targetValue != null
                : (targetType == null) == (targetValue == null))
            {
                throw new CompilerInternalException(
                    "LoweredTypeCheckExpression 目标形态不合法（IsCase 须恰带 Case；" +
                    "其余 Kind 的 TargetType/TargetValue 必须恰一个非 null）");
            }
            Kind = kind;
            Operand = operand;
            TargetType = targetType;
            TargetValue = targetValue;
            Case = caseSymbol;
            this.type = type;
        }
    }

    // typeOf（S8a；BIL §12.5 直接对应）：Operand（值形态 → getid.var）/
    // TargetType（类型形态 → getid.type）互斥（构造时恰一个非 null）。
    // Type 自带不走 Origin 透传（Type\<T\> 构造类型，恒等降级路径传入
    // Bound.Type）
    public sealed class LoweredTypeOfExpression : LoweredExpression
    {
        public LoweredExpression? Operand { get; }
        public SemanticSymbol? TargetType { get; }
        private readonly SemanticSymbol type;

        public override SemanticSymbol Type => type;

        public LoweredTypeOfExpression(BoundNode origin, LoweredExpression? operand,
            SemanticSymbol? targetType, SemanticSymbol type) : base(origin)
        {
            // 双形态互斥不变量：值/类型恰居其一
            if ((operand == null) == (targetType == null))
            {
                throw new CompilerInternalException(
                    "LoweredTypeOfExpression 的 Operand/TargetType 必须恰一个非 null");
            }
            Operand = operand;
            TargetType = targetType;
            this.type = type;
        }
    }

    // enum case 构造（S11，SYNTAX §12.1；BIL §14.3 new.case 直接对应，
    // 无脱糖）：Case 为 case 符号（宿主 enum 为定义级符号——泛型 enum
    // 的 case 已归口，P4 不会遇到）；Arguments = 洞实参（规范序 = 洞
    // 签名序，固定 case 为空）。Type 走 Origin 透传（Bound 侧 Type =
    // Case.Owner）。固定实参不进 BIL（§8.5/§14.3 不携带——case 入口的
    // init 调用语义归 VM/Middleware），本节点只携带调用点洞实参
    public sealed class LoweredEnumCaseExpression : LoweredExpression
    {
        public EnumCaseSymbol Case { get; }
        // 洞实参（规范序；固定 case 为空列表）
        public IReadOnlyList<LoweredExpression> Arguments { get; }

        public LoweredEnumCaseExpression(BoundEnumCaseExpression origin,
            EnumCaseSymbol caseSymbol, IReadOnlyList<LoweredExpression> arguments) : base(origin)
        {
            Case = caseSymbol;
            Arguments = arguments;
        }
    }

    // 可变参数包实参（S9d）：调用点打包形态，Type = Array\<Any\> 构造
    // （.array<.any>）。元素保持降级原类型，P4b 打包时装箱 cast 到 .any
    public sealed class LoweredVarArgsArgument : LoweredExpression
    {
        public bool IsNamed { get; }
        public IReadOnlyList<LoweredExpression> Values { get; }
        public IReadOnlyList<(string Name, LoweredExpression Value)> NamedValues { get; }

        public LoweredVarArgsArgument(BoundVarArgsArgument origin, bool isNamed,
            IReadOnlyList<LoweredExpression> values,
            IReadOnlyList<(string Name, LoweredExpression Value)>? namedValues)
            : base(origin)
        {
            IsNamed = isNamed;
            Values = values;
            NamedValues = namedValues ?? Array.Empty<(string, LoweredExpression)>();
        }
    }

    // 泛型可变参数包推导产物（S9d-2）：调用点打包形态的 P4a 恒等透传
    // （元素为类型列表，无值子节点可降级）。非 LoweredExpression——包
    // 不是值表达式，P4b 由调用发射器直接派发 GenericVarArgsEmitter
    public sealed class LoweredGenericVarArgsArgument : LoweredNode
    {
        public bool IsNamed { get; }
        public IReadOnlyList<SemanticSymbol> TypeArguments { get; }
        public IReadOnlyList<(string Name, SemanticSymbol Type)> NamedTypes { get; }

        public LoweredGenericVarArgsArgument(BoundGenericVarArgsArgument origin, bool isNamed,
            IReadOnlyList<SemanticSymbol> typeArguments,
            IReadOnlyList<(string Name, SemanticSymbol Type)>? namedTypes) : base(origin)
        {
            IsNamed = isNamed;
            TypeArguments = typeArguments;
            NamedTypes = namedTypes ?? Array.Empty<(string, SemanticSymbol)>();
        }
    }
}
