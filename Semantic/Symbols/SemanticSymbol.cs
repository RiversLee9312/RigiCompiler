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

        protected SemanticSymbol(string name)
        {
            Name = name;
        }
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
        public List<TypeSymbol> AppliedWrappers { get; } = new List<TypeSymbol>();
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
            BaseType = definition.BaseType;
            IsRich = definition.IsRich;
            IsShared = definition.IsShared;
            IsBuiltin = definition.IsBuiltin;
            IsValueTypeBranch = definition.IsValueTypeBranch;
            DerivesSharedSafetyFromTypeArgument = definition.DerivesSharedSafetyFromTypeArgument;
            IntrinsicOps = new HashSet<BilIntrinsicOp>();
            ConstructedFrom = definition;
            TypeArguments = typeArguments;
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
        // ext 限定名的目标路径原文（SYNTAX §4.4，如 "String"/"a.b.C"；
        // P1 拆名登记，P2 解析并注册到目标类型；非 ext 声明为 null）
        public string? ExtTargetPath { get; }
        public List<GenericParameterSymbol> GenericParameters { get; } = new List<GenericParameterSymbol>();
        public List<ParameterSymbol> Parameters { get; } = new List<ParameterSymbol>();
        // 返回类型；null = void（无结果方法）。P2 解析后填
        // （SemanticSymbol：TypeSymbol 或泛型声明内部的 GenericParameterSymbol）
        public SemanticSymbol? ReturnType { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<TypeSymbol> AppliedWrappers { get; } = new List<TypeSymbol>();

        public MethodSymbol(
            string name,
            MethodKind kind,
            TypeSymbol? owner = null,
            NamespaceSymbol? ns = null,
            bool isStatic = false,
            SemanticSymbol? returnType = null,
            string? extTargetPath = null)
            : base(name)
        {
            Kind = kind;
            Owner = owner;
            Namespace = ns;
            IsStatic = isStatic;
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
    // backing/computed/ext 等区分属性随 P1/P2 需要增补
    public sealed class FieldSymbol : SemanticSymbol
    {
        // 宿主类型（构造即定；ext 成员由 P2 注册到目标类型时改写）
        public TypeSymbol? Owner { get; private set; }
        public NamespaceSymbol? Namespace { get; private set; }
        public bool IsStatic { get; }
        // ext 限定名的目标路径原文（SYNTAX §4.4；P1 拆名登记，P2 解析注册；非 ext 为 null）
        public string? ExtTargetPath { get; }
        // 声明类型（P2 解析后填；SemanticSymbol：TypeSymbol 或
        // 泛型声明内部的 GenericParameterSymbol；无类型标注时留 null 归 P3 推断）
        public SemanticSymbol? FieldType { get; internal set; }
        // 挂载的 wrapper 应用（声明顺序，外层在前；P2 解析填充）
        public List<TypeSymbol> AppliedWrappers { get; } = new List<TypeSymbol>();

        public FieldSymbol(string name, TypeSymbol? owner = null, NamespaceSymbol? ns = null,
            bool isStatic = false, SemanticSymbol? fieldType = null, string? extTargetPath = null)
            : base(name)
        {
            Owner = owner;
            Namespace = ns;
            IsStatic = isStatic;
            FieldType = fieldType;
            ExtTargetPath = extTargetPath;
        }

        // P2 ext 注册：把符号挂靠到目标类型（Owner 改写、不再是全局变量）
        public void AttachToExtTarget(TypeSymbol target)
        {
            Owner = target;
            Namespace = null;
        }
    }

    public sealed class ParameterSymbol : SemanticSymbol
    {
        // 参数类型（P2 解析后填；SemanticSymbol：TypeSymbol 或 GenericParameterSymbol）
        public SemanticSymbol? Type { get; internal set; }

        public ParameterSymbol(string name, SemanticSymbol? type = null) : base(name)
        {
            Type = type;
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

    // 类型引用解析失败的毒化符号（SEMANTIC_ARCHITECTURE §8）：解析失败处
    // 绑定它，后续用到它的检查（闭包/闸门/约束等）一律静默跳过，抑制次生噪音。
    // 编译单元内单例（SymbolGraph.ErrorType）。
    public sealed class ErrorTypeSymbol : TypeSymbol
    {
        internal ErrorTypeSymbol() : base("<error>", TypeKind.Class) { }
    }
}
