using System.Collections.Generic;

namespace LatteCompiler
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
        Wrapper
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
        // open/abstract/singleton 标记位（P2 由声明修饰符写入符号，供修饰符
        // 合法性检查、可继承性判定与后续 pass 消费；均与声明一一对应）
        public bool IsOpen { get; internal set; }
        public bool IsAbstract { get; internal set; }
        public bool IsSingleton { get; internal set; }
        // implements 图（class）与 BaseInterfaces（interface；P2 解析填充）；
        // struct 不得 implements（P2 诊断），enum struct/wrapper 恒为空
        public List<TypeSymbol> Interfaces { get; } = new List<TypeSymbol>();
        // wrapper 目标类别（@WrapperTarget(.X)，P2 解析；非 wrapper 声明为 null）
        public WrapperTargetKind? WrapperTarget { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();
        // call??? 降级链（S11e，SYNTAX §14.7 + BIL §15.4）：wrapper 链含方法类别
        // .proxy.* 时 P2 ProxyDispatchResolver 合成——router（宿主成员，名
        // call???，wrapper-proxy(router)）与逐应用降级特化链（.proxy.<序>.???，
        // outer→inner）；链末 inner = Any.call??? 默认实现。无 .proxy.* 链恒 null
        public MethodSymbol? DowngradeRouter { get; internal set; }
        public List<MethodSymbol>? DowngradeChain { get; internal set; }
        // 编译器硬编码内建（bootstrap 直造，无源码声明；core.latte 载入的不算）
        public bool IsBuiltin { get; }
        // 是否 ValueType 分支（构造即定：显式传入或沿基类链传播；
        // 供 shared-safe 推导等使用，避免与根类型单例做引用比较）
        public bool IsValueTypeBranch { get; }
        // 「shared 安全按泛型实参推导」的定义级特权（仅 Nullable\<T>，
        // SYNTAX §3.1.2：显式书写的库容器不适用）
        public bool DerivesSharedSafetyFromTypeArgument { get; }

        // BIL 类型引用投影提示（CanonicalSymbolPrinter 消费；两者皆空走 canonical）：
        // BilAlias = 固定内建别名（BIL §6.2：.i32/.string/.any …）；
        // BilStandardConstructor = 标准类型构造（BIL §6.3：.nullable/.typeid/.array …，
        // 登记在泛型定义上，构造类型经 ConstructedFrom 取）
        public string? BilAlias { get; }
        public string? BilStandardConstructor { get; }

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
            IsBuiltin = definition.IsBuiltin;
            IsValueTypeBranch = definition.IsValueTypeBranch;
            DerivesSharedSafetyFromTypeArgument = definition.DerivesSharedSafetyFromTypeArgument;
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
            // shared class / shared rich struct / shared wrapper
            if (IsShared) return true;
            // 非 rich ValueType（全部基元、String、Type\<T>、Span\<T>、非 rich struct/enum struct）
            if (!IsRich && IsValueTypeBranch) return true;
            // Nullable\<T> 按 T 推导（§3.1.2 特权，仅此一家）
            if (ConstructedFrom is { DerivesSharedSafetyFromTypeArgument: true }
                && TypeArguments![0] is TypeSymbol element && element.IsSharedSafe()) return true;
            return false;
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
        // 有无函数体（P1 建壳即定；OverrideChecker 判定接口默认实现与无体方法，
        // 访问器符号恒 false——自动访问器体由 P3 合成，不经本标记）
        public bool HasBody { get; internal set; }
        // 继承多态三标记（SYNTAX §9.2/§9.2.1；P2 EntryCollector 读声明修饰符写入，
        // OverrideChecker 消费；仅 Regular 成员方法可置位，其余 Kind 恒 false）
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
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();
        // wrapper 派发链（S11a P2 ProxyDispatchResolver 合成；仅被 Entity
        // wrapper 拦截的实例成员方法/运算符/访问器）：outer→inner 序的特化
        // fn 符号（各带 ProxySpecialization 槽），null = 无拦截。有链时本
        // 符号的 fn 退化为转发壳（invoke 链首——S11b 由 BindingDriver 阶段
        // 2.5 合成绑定改写），用户体由 WrappedBodySymbol 承载
        public List<MethodSymbol>? WrapperChain { get; internal set; }
        public MethodSymbol? WrappedBodySymbol { get; internal set; }
        // 本符号为 proxy 特化 fn 时的元数据（S11a；P3 逐组合绑定语境与
        // P4 发射消费）；普通成员为 null
        public ProxySpecializationInfo? ProxySpecialization { get; internal set; }

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
        // 编译器合成标记（S11a：`.wrapper.` 隐藏字段，BIL §5.3/§8.3.1；
        // S11c 起 LocalSymbolEmitters 照常发射（priv var backing
        // compiler-generated 形态））
        public bool IsCompilerGenerated { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();

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
        // 显式判别值（-> N；P2 落定，null = auto）。auto 编号按声明序从 0
        // （§12.4），发射侧按宿主 Cases 表序推导，符号上不另存
        public long? Discriminant { get; internal set; }
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
        // 位置可变（TArgs...）/ 具名可变（named TArgs...）泛型参数（P1 读标记位）
        public bool IsVariadic { get; }
        public bool IsNamedVariadic { get; }
        // 约束子句（extends/supers/with；P2 解析填充；声明顺序）
        public List<GenericConstraintInfo> Constraints { get; } = new List<GenericConstraintInfo>();

        public GenericParameterSymbol(string name, bool isVariadic = false, bool isNamedVariadic = false)
            : base(name)
        {
            IsVariadic = isVariadic;
            IsNamedVariadic = isNamedVariadic;
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
    // S11a 由裸 TypeSymbol 列表升级为记录——ROADMAP S11a「应用实参登记」）：
    // - Wrapper：应用后的 wrapper 类型。Entity wrapper 恰有一个泛型参数时
    //   为 TTarget 代入宿主的构造类型（泛型实参代入在此显形，M79 遗留
    //   兑现），其余情形为定义本身；
    // - Syntax：注解 AST 节点（`@W(...)` 的 init 实参与诊断位置来源；
    //   实参绑定与宿主构造安装归后续里程碑——安装赋值依赖隐藏字段
    //   的 P4 发射（§8.3.1 声明已于 S11c 开闸））；
    // - HiddenField：宿主上的 `.wrapper.` 隐藏字段符号（P2 ProxyDispatchResolver
    //   合成，BIL §5.3；栈上局部为 null——wrapper 实例在栈帧，存储合成
    //   归后续里程碑（S11c 暂归口，S11g 复核））。
    public sealed class WrapperApplication
    {
        public TypeSymbol Wrapper { get; }
        public AnnotationASTNode Syntax { get; }
        public FieldSymbol? HiddenField { get; internal set; }

        public WrapperApplication(TypeSymbol wrapper, AnnotationASTNode syntax)
        {
            Wrapper = wrapper;
            Syntax = syntax;
        }

        // 定义级 wrapper 类型（构造类型回退定义；with 约束匹配等定义级比较用）
        public TypeSymbol WrapperDefinition => Wrapper.ConstructedFrom ?? Wrapper;
    }

    // proxy 链环节别（S11a；对应 BIL §8.4 wrapper-proxy(PROXY_KIND) 的
    // specific/wildcard——router/original 的 BIL 投影归 S11d/S11e）
    public enum ProxyLinkKind
    {
        Specific,
        Wildcard
    }

    // proxy 特化元数据（(proxy 声明 × 目标成员) 组合，S11a P2 合成；
    // 挂在特化 fn 符号上）。per-member 链的下一环经
    // TargetMember.WrapperChain 的序号 + 1 取得，链末环的 inner 目标是
    // OriginalBody；S11e 降级链不适用（TargetMember 为 null，见下）
    public sealed class ProxySpecializationInfo
    {
        // 命中的 .proxy.* 声明符号（P3 逐组合绑定读取其声明体）
        public MethodSymbol ProxyDeclaration { get; }
        // 所属 wrapper 应用（隐藏字段符号与 TTarget 代入结果在此）
        public WrapperApplication Application { get; }
        public ProxyLinkKind Kind { get; }
        // 被拦截的成员（链宿主——转发壳退化的那个符号；S11e 降级链为
        // null——未声明方法的降级特化无目标成员）
        public MethodSymbol? TargetMember { get; }
        // 原始体 fn（用户方法体的新承载者；链末环 inner 的目标）
        public MethodSymbol OriginalBody { get; }
        // wildcard 解包 shim fn（S11b：仅普通方法/operator 类别的 wildcard
        // 环非空——其 inner 以包形态（namedArgs/unnamedArgs）调用，shim 签名
        // = 双包参，body = 解包（逐元素 cast）后 invoke 下一环；specific 环
        // 与 get/set wildcard 环的 inner 直通下一环，本槽为 null）
        public MethodSymbol? UnwrapShim { get; internal set; }

        public ProxySpecializationInfo(MethodSymbol proxyDeclaration, WrapperApplication application,
            ProxyLinkKind kind, MethodSymbol? targetMember, MethodSymbol originalBody)
        {
            ProxyDeclaration = proxyDeclaration;
            Application = application;
            Kind = kind;
            TargetMember = targetMember;
            OriginalBody = originalBody;
        }
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
        // 挂载的 wrapper 应用（声明顺序，外层在前；P3 局部声明绑定时解析
        // 登记——栈上声明不进 P1/P2，SYNTAX §14.9 矩阵 C 恒合法免检查）
        public List<WrapperApplication> AppliedWrappers { get; } = new List<WrapperApplication>();

        public LocalSymbol(string name, SemanticSymbol? type, bool isConst) : base(name)
        {
            Type = type;
            IsConst = isConst;
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
