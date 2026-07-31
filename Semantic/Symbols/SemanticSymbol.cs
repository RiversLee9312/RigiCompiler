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

    // 命名空间（多段路径逐段嵌套："core.coroutine" = core → coroutine）
    public sealed class NamespaceSymbol : SemanticSymbol
    {
        public NamespaceSymbol? Parent { get; }

        public NamespaceSymbol(string name, NamespaceSymbol? parent = null) : base(name)
        {
            Parent = parent;
        }

        public string FullName => Parent == null ? Name : Parent.FullName + "." + Name;
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
        // 宿主类型；null = 全局函数（此时 Namespace 承载命名空间）
        public TypeSymbol? Owner { get; }
        public NamespaceSymbol? Namespace { get; }
        public MethodKind Kind { get; }
        public bool IsStatic { get; }
        public List<ParameterSymbol> Parameters { get; } = new List<ParameterSymbol>();
        // 返回类型；null = void（无结果方法）。P2 解析后填
        public TypeSymbol? ReturnType { get; internal set; }

        public MethodSymbol(
            string name,
            MethodKind kind,
            TypeSymbol? owner = null,
            NamespaceSymbol? ns = null,
            bool isStatic = false,
            TypeSymbol? returnType = null)
            : base(name)
        {
            Kind = kind;
            Owner = owner;
            Namespace = ns;
            IsStatic = isStatic;
            ReturnType = returnType;
        }
    }

    // 含全局变量/常量（Owner 为 null、Namespace 承载命名空间）；
    // backing/computed/ext 等区分属性随 P1/P2 需要增补
    public sealed class FieldSymbol : SemanticSymbol
    {
        public TypeSymbol? Owner { get; }
        public NamespaceSymbol? Namespace { get; }
        public bool IsStatic { get; }
        // 声明类型（P2 解析后填）
        public TypeSymbol? FieldType { get; internal set; }

        public FieldSymbol(string name, TypeSymbol? owner = null, NamespaceSymbol? ns = null,
            bool isStatic = false, TypeSymbol? fieldType = null)
            : base(name)
        {
            Owner = owner;
            Namespace = ns;
            IsStatic = isStatic;
            FieldType = fieldType;
        }
    }

    public sealed class ParameterSymbol : SemanticSymbol
    {
        // 参数类型（P2 解析后填）
        public TypeSymbol? Type { get; internal set; }

        public ParameterSymbol(string name, TypeSymbol? type = null) : base(name)
        {
            Type = type;
        }
    }

    public sealed class GenericParameterSymbol : SemanticSymbol
    {
        // extends 约束（如 Span\<T extends ValueType> 的 ValueType；P2 解析填充）
        public TypeSymbol? Constraint { get; internal set; }

        public GenericParameterSymbol(string name, TypeSymbol? constraint = null) : base(name)
        {
            Constraint = constraint;
        }
    }
}
