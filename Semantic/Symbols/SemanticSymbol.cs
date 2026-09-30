using System;
using System.Collections.Generic;

namespace RigiCompiler
{
    // 语义符号家族（P1/P2 产物，SEMANTIC_ARCHITECTURE §4）。
    // 核心不变量：每个声明实体在整个编译单元中恰有一个符号实例——
    // 引用相等即身份相等，比较一律 == / ReferenceEquals，禁止按名字字符串
    // 比较身份；canonical symbol 字符串只是序列化投影
    // （CanonicalSymbolPrinter），不作身份键。
    //
    // 命名注意：语法侧已有 Symbol / SymbolElement / SymbolASTNode
    // （AST/SymbolNodes.cs，表示源码路径），语义符号一律用 SemanticSymbol
    // 家族名称，禁止混用。
    //
    // 构造期两阶段（§4.2）：P1 建壳、P2 填内容（internal set 的后填字段），
    // P2 结束经 SymbolGraph.Freeze 冻结。

    public abstract class SemanticSymbol
    {
        public string Name { get; }

        // 访问级别（SYNTAX §16；P2 由声明修饰符写入，默认 Private 与规范
        // 默认一致；bootstrap 硬编码符号统一置 Public（BootstrapSymbols），
        // LocalSymbol 等不经声明修饰符的恒为默认值）。供 BIL 发射（pub/priv
        // 等修饰符投影）与使用点访问控制（S8e，AccessChecker）消费。
        public Accessibility Accessibility { get; internal set; } = Accessibility.Private;

        // 声明所在源文件（S8e 访问控制「同文件可见」判定的文件身份；P2
        // EntryCollector 由条目上下文写入，bootstrap/合成符号为 null——
        // 访问判定对 null 保守放行）
        public RootASTNode? SourceFile { get; internal set; }

        protected SemanticSymbol(string name)
        {
            Name = name;
        }
    }

    // 访问级别（SYNTAX §16）
    public enum Accessibility
    {
        Private,
        Protected,
        Internal,
        Public
    }

    // 命名空间（多段路径逐段嵌套："core.coroutine" = core → coroutine）。
    // 全局命名空间为 Name == "" 的单例（SymbolGraph.GlobalNamespace），
    // 无 namespace 声明的文件归属于此；FullName 拼段时跳过空名父级。
    public sealed class NamespaceSymbol : SemanticSymbol
    {
        public NamespaceSymbol? Parent { get; }

        // 容器成员表（P1 DeclarationCollector 填充，P2 冻结后只读）：
        // 本命名空间内直接声明的子命名空间 / 顶层类型 / 全局变量与常量 / 全局函数
        public List<NamespaceSymbol> ChildNamespaces { get; } = new List<NamespaceSymbol>();
        public List<TypeSymbol> Types { get; } = new List<TypeSymbol>();
        public List<FieldSymbol> Fields { get; } = new List<FieldSymbol>();
        public List<MethodSymbol> Methods { get; } = new List<MethodSymbol>();

        public NamespaceSymbol(string name, NamespaceSymbol? parent = null) : base(name)
        {
            Parent = parent;
        }

        public string FullName => Parent == null || Parent.FullName.Length == 0
            ? Name
            : Parent.FullName + "." + Name;
    }

    public enum TypeKind
    {
        Class,
        Struct,
        EnumStruct,
        Interface,
        Wrapper,
    }

    public class TypeSymbol : SemanticSymbol
    {
        public TypeKind Kind { get; }
        public NamespaceSymbol? Namespace { get; }
        // 嵌套类型的外层类型（canonical：命名空间::外层.内层）
        public TypeSymbol? DeclaringType { get; }
        // 基类（Any 为 null；P2 填内容阶段可后填）
        public TypeSymbol? BaseType { get; internal set; }
        public bool IsRich { get; }
        public bool IsShared { get; }
        public bool IsUnsafe { get; internal set; }
        // open/abstract/singleton 标记位（P2 由声明修饰符写入符号，供修饰符
        // 合法性检查、可继承性判定与后续 pass 消费；均与声明一一对应）
        public bool IsOpen { get; internal set; }
        public bool IsAbstract { get; internal set; }
        public bool IsSingleton { get; internal set; }
        // implements 图（class）与 BaseInterfaces（interface；P2 解析填充）；
        // struct 不得 implements（P2 诊断），enum struct/wrapper 恒为空
        public List<TypeSymbol> Interfaces { get; } = new List<TypeSymbol>();
        // like 委托目标字段名（SYNTAX §9.6；P1 由 class 声明写入，非 class
        // 或无 like 子句恒 null）——P2 待实现成员豁免与 P3 转发成员合成
        // 共用（LikeDelegationFacility）
        public string? LikeTarget { get; internal set; }
        // wrapper 目标类别（@WrapperTarget(.X)，P2 解析；非 wrapper 声明为 null）
        public WrapperTargetKind? WrapperTarget { get; internal set; }
        // @Terminal 内建注解（P2 BuiltinAnnotationChecker 落定）：该 wrapper
        // 处于修饰器组合终点，内层不得再嵌套其它 wrapper。只认定义级——
        // 构造类型经 WrapperDefinition 回退读取。
        public bool IsTerminal { get; internal set; }
        // @Internal 内建注解（P2 BuiltinAnnotationChecker 落定）：非声明
        // 命名空间不得拿它修饰自己的声明；API 签名暴露与 pub 可见性不变。
        public bool IsInternal { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();
        // 编译器硬编码内建（bootstrap 直造，无源码声明；core.rg 载入的不算）
        public bool IsBuiltin { get; }
        // 是否 ValueType 分支（构造即定：显式传入或沿基类链传播；
        // 供 shared-safe 推导等使用，避免与根类型单例做引用比较）
        public bool IsValueTypeBranch { get; }
        // 「shared 安全按泛型实参推导」的定义级特权（SYNTAX §3.1.2：Nullable\<T\>；
        // §5.2 起另含 core::Cell\<T\>/ReadonlyCell\<T\>——源码声明，P3 首次定位时认领）
        public bool DerivesSharedSafetyFromTypeArgument { get; internal set; }

        // BIL 类型引用投影提示（CanonicalSymbolPrinter 消费；两者皆空走 canonical）：
        // BilAlias = 固定内建别名（BIL §6.2：.i32/.string/.any …）；
        // BilStandardConstructor = 标准类型构造（BIL §6.3：.nullable/.typeid/.array …，
        // 登记在泛型定义上，构造类型经 ConstructedFrom 取；internal set 供源码
        // 声明的特权类型认领——core::Cell/ReadonlyCell，SYNTAX §5.2）
        public string? BilAlias { get; }
        public string? BilStandardConstructor { get; internal set; }

        // 基元 intrinsic 键空间（BIL §11：内建类型登记的「精确键」运算集合；
        // 非内建运算类型为空集；结果类型维度在消费侧判定，见 S5）
        public IReadOnlySet<BilIntrinsicOp> IntrinsicOps { get; }

        public List<GenericParameterSymbol> GenericParameters { get; } = new List<GenericParameterSymbol>();
        public List<FieldSymbol> Fields { get; } = new List<FieldSymbol>();
        public List<MethodSymbol> Methods { get; } = new List<MethodSymbol>();
        // enum case 表（SYNTAX §12；P1 DeclarationCollector 填充，P2 冻结后
        // 只读；非 enum struct 恒为空）
        public List<EnumCaseSymbol> Cases { get; } = new List<EnumCaseSymbol>();
        // 嵌套类型（P1 DeclarationCollector 填充；DeclaringType 反向指针构造即定）
        public List<TypeSymbol> NestedTypes { get; } = new List<TypeSymbol>();

        // 构造泛型类型（编译单元级驻留产物，只经 SymbolGraph.GetConstructedType 创建）：
        // ConstructedFrom 非空时本符号是该定义的构造实例（如 Nullable\<i32>）。
        // 实参元素为 SemanticSymbol：除 TypeSymbol 外还可能是 GenericParameterSymbol
        // （泛型声明内部的 List\<T>）
        public TypeSymbol? ConstructedFrom { get; }
        public IReadOnlyList<SemanticSymbol>? TypeArguments { get; }

        // lambda 隐藏类的闭包信息（SYNTAX §5.2；P3 LambdaVisitor 合成时写入，
        // 非 lambda 类型恒 null——兼作隐藏类识别标记，供 P4 闭包存储计划消费）
        public LambdaClosureInfo? LambdaClosure { get; internal set; }

        // cell 隐藏子类的存储信息回挂（统一 cell 存储；P3 CellClassFactory
        // 合成时写入，非 cell 子类恒 null——兼作 cell 子类识别标记，供 P4b
        // 声明收集消费）
        public CellStorageInfo? CellStorage { get; internal set; }

        // 静态 companion 信息回挂（BIL §8.7）：非 null 时本类型是声明类
        // 的 companion singleton（嵌套类、无 UUID），兼识别标记供 P4b
        // 声明收集。宿主类的 CompanionInfo 指向自己的 companion；companion
        // 自身也持有一份（自指，识别其 singleton 身份）。Shell 方法上的
        // Companion 槽（MethodSymbol.Companion）指向同一 companion 类型
        public StaticCompanionInfo? CompanionInfo { get; internal set; }

        public TypeSymbol(
            string name,
            TypeKind kind,
            NamespaceSymbol? ns = null,
            TypeSymbol? declaringType = null,
            TypeSymbol? baseType = null,
            bool isRich = false,
            bool isShared = false,
            bool isBuiltin = false,
            bool isValueTypeBranch = false,
            bool derivesSharedSafetyFromTypeArgument = false,
            string? bilAlias = null,
            string? bilStandardConstructor = null,
            IReadOnlySet<BilIntrinsicOp>? intrinsicOps = null)
            : base(name)
        {
            Kind = kind;
            Namespace = ns;
            DeclaringType = declaringType;
            BaseType = baseType;
            IsRich = isRich;
            IsShared = isShared;
            IsBuiltin = isBuiltin;
            IsValueTypeBranch = isValueTypeBranch || (baseType?.IsValueTypeBranch ?? false);
            DerivesSharedSafetyFromTypeArgument = derivesSharedSafetyFromTypeArgument;
            BilAlias = bilAlias;
            BilStandardConstructor = bilStandardConstructor;
            IntrinsicOps = intrinsicOps ?? new HashSet<BilIntrinsicOp>();
        }

        // 构造类型构造：声明属性自定义传播（驻留入口在 SymbolGraph）
        internal TypeSymbol(TypeSymbol definition, IReadOnlyList<SemanticSymbol> typeArguments)
            : base(definition.Name)
        {
            Kind = definition.Kind;
            Namespace = definition.Namespace;
            DeclaringType = definition.DeclaringType;
            // 兜底快照：GetConstructedType 入表后立即以 Substitute 代入结果覆写
            // （含 P2 继承解析完成后的 BackfillConstructedBaseTypes 统一重算）
            BaseType = definition.BaseType;
            IsRich = definition.IsRich;
            IsShared = definition.IsShared;
            IsUnsafe = definition.IsUnsafe;
            IsBuiltin = definition.IsBuiltin;
            IsValueTypeBranch = definition.IsValueTypeBranch;
            DerivesSharedSafetyFromTypeArgument = definition.DerivesSharedSafetyFromTypeArgument;
            // 构造类型必须携带实参；别名仅属于声明，不能擦除具化身份。
            BilAlias = null;
            IntrinsicOps = new HashSet<BilIntrinsicOp>();
            ConstructedFrom = definition;
            TypeArguments = typeArguments;
            // 访问控制两属性随定义传播（S8e：构造类型与定义同可见性/同声明文件）
            Accessibility = definition.Accessibility;
            SourceFile = definition.SourceFile;
        }

        // SYNTAX §3.1.1 共享安全类型白名单（「可离开单 Coroutine 所有权域」的完整集合）
        public bool IsSharedSafe()
        {
            // wrapper 绑定宿主：不能因普通字段非 rich 就允许其接收者跨协程逃逸。
            // shared 资格仍须显式声明，与 self 的 rich 豁免无关。
            if (Kind == TypeKind.Wrapper) return IsShared;
            // shared class / shared rich struct / shared wrapper
            if (IsShared) return true;
            // 非 rich ValueType（全部基元、String、Type\<T>、非 rich struct/enum struct）
            if (!IsRich && IsValueTypeBranch) return true;
            // Nullable\<T\>/Cell\<T\>/ReadonlyCell\<T\> 按 T 推导（§3.1.2/§5.2 特权）；
            // 内层为泛型参数时按其 extends 界链推导共享安全（界为外层 GP
            // 则递归；无约束 / 环界保守 false，与调用点闸门口径一致）
            if (ConstructedFrom is { DerivesSharedSafetyFromTypeArgument: true })
            {
                return TypeArguments![0] switch
                {
                    TypeSymbol element => element.IsSharedSafe(),
                    GenericParameterSymbol parameter => parameter.IsSharedSafe(),
                    _ => false,
                };
            }
            return false;
        }
    }

    // ===== 静态 companion singleton（BIL §8.7）=====

    // 每个声明类的静态问题统一收敛到一个 companion singleton：同一类的
    // 被 Method wrapper 修饰的静态方法（迁为 companion 实例方法）与被
    // Value wrapper 修饰的静态字段（cell 存储挂 companion 实例）共用
    // 同一 companion——该 singleton 是声明类的嵌套类（canonical：
    // 命名空间::外层...companion，无 UUID），自身 singleton+shared+
    // compiler-generated；初始化（含 cell 构造与 wrapper 安装）由
    // VM/Middleware 在 main 前完成。
    public sealed class StaticCompanionInfo
    {
        // companion 类型自身（挂本信息的类型即 companion；宿主类的
        // CompanionInfo 槽也指向它）
        public TypeSymbol CompanionType { get; }
        // 迁入的静态字段条目（源字段 + companion 上的 cell 实例字段 + cell 存储）
        public List<StaticFieldCompanionEntry> Fields { get; } = new List<StaticFieldCompanionEntry>();

        public StaticCompanionInfo(TypeSymbol companionType)
        {
            CompanionType = companionType;
        }
    }

    // 每个被 Method wrapper 修饰的静态方法：实例方法承接原静态方法体 +
    // wrapper 应用；原方法降为壳体（new companion → invoke 实例方法 → ret）。
    // CompanionType 为宿主类共享的那一个 companion
    public sealed class StaticMethodCompanionInfo
    {
        public TypeSymbol CompanionType { get; }
        public MethodSymbol InstanceMethod { get; }
        public MethodSymbol ShellMethod { get; }

        public StaticMethodCompanionInfo(TypeSymbol companionType, MethodSymbol instanceMethod,
            MethodSymbol shellMethod)
        {
            CompanionType = companionType;
            InstanceMethod = instanceMethod;
            ShellMethod = shellMethod;
        }
    }

    // 迁入 companion 的静态字段条目（统一 cell 存储，SYNTAX §14.3）：cell
    // 对象成为 companion 实例字段，companion 的 init 里求值字段初始化器
    // 并构造 cell（含 ..init.wrapper 安装）
    public sealed class StaticFieldCompanionEntry
    {
        public FieldSymbol SourceField { get; }
        public FieldSymbol CellField { get; }
        public CellStorageInfo Storage { get; }
        // 字段初始化表达式绑定产物（声明点静态语境；null = 无初始化器）
        public BoundExpression? InitValue { get; internal set; }

        public StaticFieldCompanionEntry(FieldSymbol sourceField, FieldSymbol cellField,
            CellStorageInfo storage)
        {
            SourceField = sourceField;
            CellField = cellField;
            Storage = storage;
        }
    }

    // ===== 统一 cell 存储（SYNTAX §5.2 捕获 / §14.3 wrapper 值）=====

    // cell 化符号的存储信息：凡被 cell 盛装的符号（lambda 捕获的局部/参数、
    // 被 wrapper 修饰的局部/静态字段、带访问器的局部——M107 路线 C）逐变量
    // 合成一个 Cell/ReadonlyCell 隐藏子类（..cell..UUID，与用户源码不可名的
    // `..` 前缀，不进符号图容器表）。子类自持 pub value 字段（wrapper 应用经
    // 该字段的 wrapped(W) 标记承载），override 基类抽象 getValue/setValue——
    // 对 Middleware 而言就是「普通类 + 带标记字段」，一看即知如何烘焙；
    // .cell<T>/.readonly_cell<T> 特权拼写仅供 Middleware 激进优化识别。
    // 信息同时回挂隐藏子类 TypeSymbol.CellStorage（兼作 cell 子类识别标记，
    // 供 P4b 声明收集）
    public sealed class CellStorageInfo
    {
        // 隐藏子类定义级符号（基类 = Cell<T>/ReadonlyCell<T> 构造类型）
        public TypeSymbol CellClass { get; }
        // 存储类型引用：泛型上下文共享时为自构造形态，否则 = CellClass
        public TypeSymbol CellType { get; }
        // ReadonlyCell 风味（const 值；无 setValue/DefaultInit 写通道）
        public bool IsReadOnly { get; }
        // pub value 字段（盛装值；wrapper 应用的 wrapped(W) 载体；局部访问器
        // backing 形态的 value 别名目标）
        public FieldSymbol ValueField { get; }
        // init(value[, captures...])（值参构造点；访问器自由变量捕获实参接在值后）
        public MethodSymbol ValueInit { get; }
        // init([captures...])（空构造点——未初始化 var 的空 cell；仅 Cell 风味）
        public MethodSymbol? DefaultInit { get; }
        // 局部访问器 getValue/setValue 用户体引用的外层自由变量捕获
        // （M107：this 先行，其余名序；空 = 无自由变量 / 非访问器 cell）
        public IReadOnlyList<LambdaCaptureEntry> AccessorCaptures { get; }
        // cell 子类 `..init.wrapper`（M109b-1）：value 字段带 wrapped(W) 时合成；
        // 无 wrapper 应用为 null。有参时构造点改 new.wrapped
        public MethodSymbol? InitWrapper { get; internal set; }
        // 构造点传入 `..init.wrapper` 的实参（声明点绑定的扁平列表；
        // 与 InitWrapper.Parameters 一一对应；无参时为空）
        public IReadOnlyList<BoundExpression> WrapperInitArguments { get; internal set; }

        public CellStorageInfo(TypeSymbol cellClass, TypeSymbol cellType, bool isReadOnly,
            FieldSymbol valueField, MethodSymbol valueInit, MethodSymbol? defaultInit,
            IReadOnlyList<LambdaCaptureEntry>? accessorCaptures = null)
        {
            CellClass = cellClass;
            CellType = cellType;
            IsReadOnly = isReadOnly;
            ValueField = valueField;
            ValueInit = valueInit;
            DefaultInit = defaultInit;
            AccessorCaptures = accessorCaptures ?? Array.Empty<LambdaCaptureEntry>();
            WrapperInitArguments = Array.Empty<BoundExpression>();
        }
    }

    // lambda 捕获集条目（P3 LambdaVisitor 按符号身份落定，排序确定：
    // this 先行，其余按符号名序）：Symbol 是外层局部/参数/ThisSymbol；
    // Field 是隐藏类上对应的存储字段（this 捕获为普通字段，其余为
    // Cell/ReadonlyCell 构造类型字段）
    public sealed class LambdaCaptureEntry
    {
        public SemanticSymbol Symbol { get; }
        public FieldSymbol Field { get; }
        public bool IsThis { get; }
        public bool IsReadOnly { get; }

        public LambdaCaptureEntry(SemanticSymbol symbol, FieldSymbol field,
            bool isThis, bool isReadOnly)
        {
            Symbol = symbol;
            Field = field;
            IsThis = isThis;
            IsReadOnly = isReadOnly;
        }
    }

    // lambda 隐藏类的闭包信息（SYNTAX §5.2 对象模型）：挂在隐藏类
    // TypeSymbol.LambdaClosure 上（非 lambda 类型恒 null，兼作隐藏类标记）。
    // init 参数列表 = Captures 序逐条对应（无捕获时为空参数 init）；
    // ValueBlock = 有返回值块体 lambda 的值块（return@ 目标——P4a 按值块
    // 协议降级 $$call 体；单表达式体/void 体为 null）
    public sealed class LambdaClosureInfo
    {
        public TypeSymbol HiddenClass { get; }
        public MethodSymbol Init { get; }
        public MethodSymbol Call { get; }
        public IReadOnlyList<LambdaCaptureEntry> Captures { get; }
        public BoundValueBlock? ValueBlock { get; }
        // Method wrapper 安装方法（SYNTAX §14.4）：lambda 头内部注解挂到
        // $$call 运算符后合成；无 wrapper 应用为 null。有参时构造点改
        // new.wrapped（LambdaRewriter 按 WrapperInitArguments 平铺实参）
        public MethodSymbol? InitWrapper { get; }
        // 构造点传入 ..init.wrapper 的实参（lambda 表达式求值语境已绑定的
        // 扁平列表；与 InitWrapper.Parameters 一一对应；无参时为空）
        public IReadOnlyList<BoundExpression> WrapperInitArguments { get; }

        public LambdaClosureInfo(TypeSymbol hiddenClass, MethodSymbol init,
            MethodSymbol call, IReadOnlyList<LambdaCaptureEntry> captures,
            BoundValueBlock? valueBlock = null, MethodSymbol? initWrapper = null,
            IReadOnlyList<BoundExpression>? wrapperInitArguments = null)
        {
            HiddenClass = hiddenClass;
            Init = init;
            Call = call;
            Captures = captures;
            ValueBlock = valueBlock;
            InitWrapper = initWrapper;
            WrapperInitArguments = wrapperInitArguments ?? Array.Empty<BoundExpression>();
        }
    }

    public enum MethodKind
    {
        Regular,
        Init,
        Operator,
        Getter,
        Setter
    }

    // wrapper proxy 模板类别（SYNTAX §14.2）：P1 建壳时按源码声明名
    // （.proxy.<...> 为 Specific、以 .* 收尾为 Wildcard）落定一次，之后
    // 全部内部消费（P2 形状校验/P3 模板态绑定/P4 修饰符投影）只读属性，
    // 不再操作符号名字符串
    public enum ProxyTemplateKind
    {
        Specific,
        Wildcard,
    }

    // 含 init、operator、getter/setter、全局函数、ext 成员（以属性区分）。
    // Kind == Getter/Setter 时 Name 为字段名：getter 的字段类型取 ReturnType，
    // setter 的字段类型取唯一参数（value）的类型（canonical 不列其参数段，BIL §5.2）。
    public sealed class MethodSymbol : SemanticSymbol
    {
        // 宿主类型；null = 全局函数（此时 Namespace 承载命名空间）。
        // 构造即定；ext 成员由 P2 注册到目标类型时改写（AttachToExtTarget）
        public TypeSymbol? Owner { get; private set; }
        public NamespaceSymbol? Namespace { get; private set; }
        public MethodKind Kind { get; }
        public bool IsStatic { get; }
        // native 函数标记位（SYNTAX §4.6：无体原生函数；P1 建壳读修饰符即定）
        public bool IsNative { get; }
        // async 函数标记位（SYNTAX §4.5：调用时另建协程；P1 建壳读修饰符
        // 即定，S8f async 边界五项闸门的检查点分派依据；仅 Kind=Regular 的
        // 函数可置位——其余 Kind 置位由 AsyncGateChecker 拒绝）
        public bool IsAsync { get; }
        public bool IsUnsafe { get; internal set; }
        public bool IsSynthetic { get; internal set; }
        // wrapper proxy 模板标记（SYNTAX §14.2；P1 建壳按声明名落定——
        // 非 proxy 成员恒 null）：P2/P3/P4 的模板识别与 Specific/Wildcard
        // 区分一律经此属性，禁止再按 Name 前缀判定
        public ProxyTemplateKind? ProxyTemplate { get; internal set; }
        // 有无函数体（P1 建壳即定；OverrideChecker 判定接口默认实现与无体方法，
        // 访问器符号恒 false——自动访问器体由 P3 合成，不经本标记）
        public bool HasBody { get; internal set; }
        // 继承多态标记（SYNTAX §9.2/§9.2.1；P2 EntryCollector/AccessorChecker
        // 读声明修饰符写入，OverrideChecker 消费）。普通方法与 getter/setter
        // 均可使用 IsOpen/IsOverride；IsAbstract 仍仅用于普通方法。
        public bool IsOpen { get; internal set; }
        public bool IsAbstract { get; internal set; }
        public bool IsOverride { get; internal set; }
        // ext 限定名的目标路径原文（SYNTAX §4.4，如 "String"/"a.b.C"；
        // P1 拆名登记，P2 解析并注册到目标类型；非 ext 声明为 null）
        public string? ExtTargetPath { get; }
        public List<GenericParameterSymbol> GenericParameters { get; } = new List<GenericParameterSymbol>();
        public List<ParameterSymbol> Parameters { get; } = new List<ParameterSymbol>();
        // 返回类型；null = void（无结果方法）。P2 解析后填
        // （SemanticSymbol：TypeSymbol 或泛型声明内部的 GenericParameterSymbol）
        public SemanticSymbol? ReturnType { get; internal set; }
        // native 路由元数据（§4.6 内建注解，P2 读注解后填；非 native 函数为 null）：
        // NativeSymbol = @NativeSymbol 实参（缺省取函数名）；NativeLibrary = @NativeLibrary 实参
        public string? NativeSymbol { get; internal set; }
        public string? NativeLibrary { get; internal set; }
        // 3b-δ1 借用返回标记（§4.6 内建注解 @NativeBorrow，P2 读注解后填；
        // 仅 native 函数可置位——P4b 发射 native-borrow 修饰符，RcInjection
        // 据此豁免调用结果槽的 acquire/release 义务：返回值是无 +1 的借用
        // 胖引用，借用寿命纪律由 unsafe 契约约束）
        public bool NativeBorrow { get; internal set; }
        // 程序入口标记（§17 内建注解 @EntryPoint，P2 EntryPointChecker 落定；
        // 裸 main 命名约定不经此标记——P4 发射侧两条件取或）
        public bool IsEntryPoint { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();
        // 静态 Method wrapper 壳体（M109b-2）：仅原静态方法置位，指向
        // companion singleton 与生成实例方法；companion 实例方法为 null
        public StaticMethodCompanionInfo? Companion { get; internal set; }
        // companion 内生成的实例方法（§8.7）：源码体迁入；BIL 有 .this，
        // 但绑定态视同静态（无 this——源体本为静态方法）
        public bool IsCompanionInstance { get; internal set; }
        // §17 切片归属覆盖（仅编译器合成 fn 使用）：..globals.init 收集
        // 跨命名空间的字段初值，单一 fn 若按 Namespace（全局）切片会使
        // 纯命名空间源的全局切片非空——canonical 仍 $..globals.init
        // （Namespace 保持全局命名空间，init 协议不变），仅发射切分归
        // 首个被初始化字段的命名空间（SliceNsOf 消费）
        public string? SliceNamespaceOverride { get; internal set; }

        public MethodSymbol(
            string name,
            MethodKind kind,
            TypeSymbol? owner = null,
            NamespaceSymbol? ns = null,
            bool isStatic = false,
            bool isNative = false,
            SemanticSymbol? returnType = null,
            string? extTargetPath = null,
            bool isAsync = false)
            : base(name)
        {
            Kind = kind;
            Owner = owner;
            Namespace = ns;
            IsStatic = isStatic;
            IsNative = isNative;
            IsAsync = isAsync;
            ReturnType = returnType;
            ExtTargetPath = extTargetPath;
        }

        // P2 ext 注册：把符号挂靠到目标类型（Owner 改写、不再是全局函数）
        public void AttachToExtTarget(TypeSymbol target)
        {
            Owner = target;
            Namespace = null;
        }
    }

    // 含全局变量/常量（Owner 为 null、Namespace 承载命名空间）；
    // backing/computed 由访问器三槽（Getter/Setter/HasBackingStorage，S8e）表达、
    // ext 以 ExtTargetPath 区分
    public sealed class FieldSymbol : SemanticSymbol
    {
        // 宿主类型（构造即定；ext 成员由 P2 注册到目标类型时改写）
        public TypeSymbol? Owner { get; private set; }
        public NamespaceSymbol? Namespace { get; private set; }
        public bool IsStatic { get; }
        // const 字段（S8b 前置，P1 收集修饰符写入；M55 前是「符号 → 声明 AST
        // 反向映射缺失」技术债）：smart cast 字段收窄的安全前提（仅 const
        // 字段可收窄——引用不变 ⇒ 运行类型不变）与 const 赋值检查的依据
        public bool IsConst { get; }
        // ext 限定名的目标路径原文（SYNTAX §4.4；P1 拆名登记，P2 解析注册；非 ext 为 null）
        public string? ExtTargetPath { get; }
        // 声明类型（P2 解析后填；SemanticSymbol：TypeSymbol 或
        // 泛型声明内部的 GenericParameterSymbol；无类型标注时留 null 归 P3 推断）
        public SemanticSymbol? FieldType { get; internal set; }
        // 属性访问器（SYNTAX §9.4/§9.4.1，S8e；P1 收集创建，null = 无该访问器）。
        // 读写使用点存在性与访问控制检查、访问器体绑定（value 别名）消费；
        // 带访问器字段不收窄（smart cast）且不参与 BIL get.field/set.field 之外的形态
        public MethodSymbol? Getter { get; internal set; }
        public MethodSymbol? Setter { get; internal set; }
        // 访问器 backing 形态标记（§9.4：仅在 Getter/Setter 任一非空时有意义）——
        // true = 编译器生成 backing 存储（体内 value 别名；P4 发 backing 修饰），
        // false = 计算属性（无存储，P4 发 computed 修饰）
        public bool HasBackingStorage { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();
        // 静态/全局字段的 wrapper cell 化存储（统一 cell 存储，SYNTAX §14.3；
        // P3 BindingDriver 阶段 1.6 写入）：非 null 时本字段在 BIL 的存储是
        // cell——字段类型投影为隐藏子类（wrapped(W) 标记由子类 value 字段
        // 承载），读写全经 getValue/setValue；构造时机归 Middleware。
        // 实例字段无此形态（wrapper 存储 = 宿主隐藏存储，M88）
        public CellStorageInfo? CellStorage { get; internal set; }
        // 静态字段的 companion cell 落地（BIL §8.7）：非 null 时本静态
        // 字段的 cell 存储在 companion 实例上（CompanionCellField 即
        // companion 上盛装 cell 的实例字段）；BIL 不再为宿主发静态字段
        // 声明，读写经「companion 单例 → 该实例字段（cell）→ getValue/setValue」
        public FieldSymbol? CompanionCellField { get; internal set; }
        // 字段 open/override（SYNTAX §9.2.1 字段覆写）：open = 允许子类
        // 以同名字段 override 替换初始值；override = 本声明覆写继承字段
        // （存储仍是基类槽，OverriddenField 指向被覆写的基类字段符号，
        // P2 OverrideChecker 校验通过后本字段从宿主 Fields 表移除——
        // 名称解析自然落到基类槽，BIL 不再发新字段声明）
        public bool IsOpen { get; internal set; }
        public bool IsOverride { get; internal set; }
        public FieldSymbol? OverriddenField { get; internal set; }

        public FieldSymbol(string name, TypeSymbol? owner = null, NamespaceSymbol? ns = null,
            bool isStatic = false, SemanticSymbol? fieldType = null, string? extTargetPath = null,
            bool isConst = false)
            : base(name)
        {
            Owner = owner;
            Namespace = ns;
            IsStatic = isStatic;
            FieldType = fieldType;
            ExtTargetPath = extTargetPath;
            IsConst = isConst;
        }

        // P2 ext 注册：把符号挂靠到目标类型（Owner 改写、不再是全局变量）
        public void AttachToExtTarget(TypeSymbol target)
        {
            Owner = target;
            Namespace = null;
        }
    }

    // enum case 符号（SYNTAX §12，S11）：宿主 enum struct 的 case 声明。
    // 构造期三阶段：P1 建壳（名 + 宿主，DeclarationCollector）、P2 落定显式
    // 判别值（EnumCaseResolver）、P3 声明点绑定 init 调用模板（两槽后填，
    // 仿 S8d 参数默认值声明点绑定先例）。
    public sealed class EnumCaseSymbol : SemanticSymbol
    {
        // 宿主 enum 类型（构造即定；canonical 投影见 PrintCase：{Owner}.{Name}，BIL §8.5）
        public TypeSymbol Owner { get; }
        // 显式判别值（-> N；P2 落定，null = auto；decimal 装载，发射侧
        // 收窄为 i32 标量资源）。auto 编号按声明序从 0（§12.4），发射侧按
        // 宿主 Cases 表序推导，符号上不另存
        public decimal? Discriminant { get; internal set; }
        // init 调用模板绑定产物（§12.1，P3 声明点落定，本阶段只开槽）：
        // ResolvedInit = 选中的 init；HoleParameters = 参数洞签名列表
        // （null = 未绑定；空列表 = 固定 case）。固定实参表达式的绑定产物
        // （BoundExpression）归 P3 侧环境缓存（仿 ParameterSymbol.DefaultValue
        // 产物的 BindEnvironment.ParameterDefaults 先例）——符号层只持结构
        // 信息，不依赖 Bound 节点
        public MethodSymbol? ResolvedInit { get; internal set; }
        public List<EnumCaseHoleParameter>? HoleParameters { get; internal set; }

        public EnumCaseSymbol(string name, TypeSymbol owner) : base(name)
        {
            Owner = owner;
        }
    }

    // 参数洞签名（SYNTAX §12.1：洞的名/类型/位置取自它对应的 init 参数）：
    // P3 声明点模板绑定时从命中的 init 参数抄录，供 BIL §8.5 声明发射与
    // 调用点实参匹配消费
    public sealed class EnumCaseHoleParameter
    {
        public string Name { get; }
        // 洞类型 = 对应 init 参数类型（SemanticSymbol：TypeSymbol 或泛型参数）
        public SemanticSymbol Type { get; }
        // 对应 init 参数在 MethodSymbol.Parameters 中的下标
        public int InitParameterIndex { get; }

        public EnumCaseHoleParameter(string name, SemanticSymbol type, int initParameterIndex)
        {
            Name = name;
            Type = type;
            InitParameterIndex = initParameterIndex;
        }
    }

    public sealed class ParameterSymbol : SemanticSymbol
    {
        // 参数类型（P2 解析后填；SemanticSymbol：TypeSymbol 或 GenericParameterSymbol）
        public SemanticSymbol? Type { get; internal set; }

        // lambda 捕获 cell 化存储（统一 cell 存储，SYNTAX §5.2；P3
        // LambdaVisitor 在捕获集落定后写入）：非 null 时本参数在 BIL 的
        // 存储是 cell——函数入口由 P4a 合成 .c.<名> cell 局部并用实参构造，
        // 体内读写全经 getValue/setValue（this 从不捕获为参数，无此形态）
        public CellStorageInfo? CellStorage { get; internal set; }

        // init 参数映射的目标字段（SYNTAX §9.3：`name[:type] -> field`；
        // P2 TypeReferenceResolver 字段存在性检查命中时落定，无映射为 null）。
        // P3 BindingDriver 据此合成构造时映射赋值（this.field = param）
        public FieldSymbol? MappedField { get; internal set; }

        // 默认值表达式根（SYNTAX §4.2；无默认值时为 null）。挂 AST 引用——
        // P3 预绑定阶段（BindingDriver）在声明点作用域绑定，产物缓存于
        // BindEnvironment.ParameterDefaults，调用点缺省时填充（每次调用
        // 重新求值语义由 P4a 每次降级自然保证）
        public ExpressionRootASTNode? DefaultValue { get; }

        // 位置可变（Type...）/ 具名可变（named Type...）参数标记（SYNTAX §4.3）。
        // S8d 起 P3 调用绑定遇之归口诊断（可变参数调用归后续里程碑）
        public bool IsVariadic { get; }
        public bool IsNamedVariadic { get; }

        public ParameterSymbol(string name, SemanticSymbol? type = null,
            ExpressionRootASTNode? defaultValue = null, bool isVariadic = false,
            bool isNamedVariadic = false) : base(name)
        {
            Type = type;
            DefaultValue = defaultValue;
            IsVariadic = isVariadic;
            IsNamedVariadic = isNamedVariadic;
        }
    }

    public sealed class GenericParameterSymbol : SemanticSymbol
    {
        // 源码 shared T；用户与标准库走同一约束满足判定。
        internal bool RequiresSharedSafe { get; set; }
        // 类型声明泛型参数的型变方向（函数泛型参数必须保持 invariant）。
        public GenericVariance Variance { get; }
        // 位置可变（TArgs...）/ 具名可变（named TArgs...）泛型参数（P1 读标记位）
        public bool IsVariadic { get; }
        public bool IsNamedVariadic { get; }
        // 约束子句（extends/supers/with；P2 解析填充；声明顺序）
        public List<GenericConstraintInfo> Constraints { get; } = new List<GenericConstraintInfo>();

        public GenericParameterSymbol(string name, bool isVariadic = false,
            bool isNamedVariadic = false, GenericVariance variance = GenericVariance.None)
            : base(name)
        {
            Variance = variance;
            IsVariadic = isVariadic;
            IsNamedVariadic = isNamedVariadic;
        }

        // 按 extends 界链推导共享安全：界为 TypeSymbol 则问其 IsSharedSafe；
        // 界为外层 GP 则递归。无约束 / 仅 supers/with / 环界 → false。
        public bool IsSharedSafe() => IsSharedSafe(null);

        internal bool IsSharedSafe(HashSet<GenericParameterSymbol>? visiting)
        {
            if (RequiresSharedSafe) return true;
            visiting ??= new HashSet<GenericParameterSymbol>();
            if (!visiting.Add(this)) return false;
            try
            {
                foreach (var constraint in Constraints)
                {
                    if (constraint.Kind != GenericConstraintKind.Extends) continue;
                    switch (constraint.Bound)
                    {
                        case TypeSymbol bound when bound.IsSharedSafe():
                            return true;
                        case GenericParameterSymbol outer when outer.IsSharedSafe(visiting):
                            return true;
                    }
                }
                return false;
            }
            finally
            {
                visiting.Remove(this);
            }
        }
    }

    // 泛型约束（语义侧）：Kind 复用语法侧 GenericConstraintKind
    // （Semantic → AST 依赖方向合法）；Bound 为 SemanticSymbol——
    // 除 TypeSymbol 外不禁止泛型参数作边界（规范未明，不做额外收紧）
    public sealed class GenericConstraintInfo
    {
        public GenericConstraintKind Kind { get; }
        public SemanticSymbol Bound { get; }

        public GenericConstraintInfo(GenericConstraintKind kind, SemanticSymbol bound)
        {
            Kind = kind;
            Bound = bound;
        }
    }

    // wrapper 目标类别（SYNTAX §14.1 三分类互斥）：@WrapperTarget(.X) 的实参
    public enum WrapperTargetKind
    {
        Entity,
        Value,
        Method
    }

    // wrapper 应用记录（SYNTAX §14.5：`@W(...)` 挂类型/方法/字段/栈上变量；
    // S11a 由裸 TypeSymbol 列表升级为记录——ROADMAP S11a「应用实参登记」；
    // M88：仅标记，不合成隐藏字段/派发链——烘焙归 Middleware）：
    // - Wrapper：应用后的 wrapper 类型。Entity wrapper 恰有一个泛型参数时
    //   为 TTarget 代入宿主的构造类型（泛型实参代入在此显形），其余情形
    //   为定义本身；
    // - Syntax：注解 AST 节点（`@W(...)` 的 init 实参与诊断位置来源）。
    //   约束推导合成应用（FromConstraint）无注解——Syntax 为 null。
    public sealed class WrapperApplication
    {
        public TypeSymbol Wrapper { get; }
        public AnnotationASTNode? Syntax { get; }

        // wrapper init 实参绑定产物（M109b-1）：规范序；null = 尚未绑定 /
        // 绑定失败。cell 场景在声明点词法作用域绑定；类型级在合成
        // ..init.wrapper 体内绑定。FromConstraint 无注解 → 恒空列表
        public IReadOnlyList<BoundExpression>? BoundInitArguments { get; internal set; }

        public WrapperApplication(TypeSymbol wrapper, AnnotationASTNode? syntax)
        {
            Wrapper = wrapper;
            Syntax = syntax;
        }

        // 定义级 wrapper 类型（构造类型回退定义；with 约束匹配等定义级比较用）
        public TypeSymbol WrapperDefinition => Wrapper.ConstructedFrom ?? Wrapper;

        // with 约束推导的合成应用（无 @W 注解；存储属实参宿主，编译器不合成）
        public static WrapperApplication FromConstraint(TypeSymbol wrapper) =>
            new WrapperApplication(wrapper, null);
    }

    // 函数体局部变量（P3 Binder 产生；ARCHITECTURE §4.1：挂在函数分析结果
    // BoundFunctionBody.Locals 上，不进符号图容器表，不受 Freeze 约束）。
    // 参数不归此类——参数符号是 ParameterSymbol，随 MethodSymbol 声明侧存在。
    // Type 为 null 是 P4a 合成 .breakid 局部的唯一特例（BIL §9.3：.breakid
    // 是结构化控制 capability，无对应 TypeSymbol；emitter 侧 .vars 条目按
    // 「Type 为 null → typeRef 用 .breakid」投影）——源码局部与 .sN 合成
    // 局部恒非空。
    public sealed class LocalSymbol : SemanticSymbol
    {
        // S9 放宽为 SemanticSymbol：泛型函数体内局部声明的类型可为泛型参数
        public SemanticSymbol? Type { get; }
        public bool IsConst { get; }
        // using 资源绑定即使写作 var 也不可重赋值，避免 finally 捕获错误资源。
        public bool IsUsingResource { get; }
        // cell 化存储（统一 cell 存储；P3 写入）：lambda 捕获（§5.2）、被
        // wrapper 修饰（§14.3）或带访问器（§9.4 路线 C，M107）时本局部在
        // BIL 的存储是 cell（.vars 条目类型为隐藏子类，声明处构造，读写
        // 全经 getValue/setValue——访问器体即 override 体）
        public CellStorageInfo? CellStorage { get; internal set; }
        // 局部访问器槽（SYNTAX §9.4 路线 C，M107；null = 无该访问器）。
        // 指向 cell 子类的 getValue/setValue override（存在性供读写检查；
        // 体经 SyntheticCellBodies 走统一 P4）。无字段访问器的
        // getter(FIELD) BIL 投影——局部访问器不进类型成员表
        public MethodSymbol? Getter { get; internal set; }
        public MethodSymbol? Setter { get; internal set; }
        // 访问器 backing 形态标记（§9.4：仅在 Getter/Setter 任一非空时有意义）
        public bool HasBackingStorage { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P3 局部声明绑定时解析
        // 登记——栈上声明不进 P1/P2，SYNTAX §14.9 矩阵 C 恒合法免检查）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();

        public LocalSymbol(string name, SemanticSymbol? type, bool isConst,
            bool isUsingResource = false) : base(name)
        {
            Type = type;
            IsConst = isConst;
            IsUsingResource = isUsingResource;
        }
    }

    // 实例上下文的 this 身份。它不是用户可声明符号，也不进入符号图；
    // 仅供 lambda 捕获集合按引用身份记录。
    public sealed class ThisSymbol : SemanticSymbol
    {
        public TypeSymbol Type { get; }

        public ThisSymbol(TypeSymbol type) : base("this")
        {
            Type = type;
        }
    }

    // 类型引用解析失败的毒化符号（SEMANTIC_ARCHITECTURE §8）：解析失败处
    // 绑定它，后续用到它的检查（闭包/闸门/约束等）一律静默跳过，抑制次生噪音。
    // 编译单元内单例（SymbolGraph.ErrorType）。
    public sealed class ErrorTypeSymbol : TypeSymbol
    {
        internal ErrorTypeSymbol() : base("<error>", TypeKind.Class) { }
    }
}
