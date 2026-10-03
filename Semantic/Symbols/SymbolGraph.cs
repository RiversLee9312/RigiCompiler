using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RigiCompiler
{
    // 符号图容器（SEMANTIC_ARCHITECTURE §4）：编译单元唯一符号对象图的持有者。
    // 构造期两阶段（P1 建壳、P2 填内容），P2 结束 Freeze 后声明侧不可变
    // （P3/P4 只读）；构造泛型类型驻留 cache 是幂等透明派生物，不受冻结限制。
    public sealed partial class SymbolGraph
    {
        public BootstrapSymbols Bootstrap { get; }

        public string ModuleIdentity { get; }
        // 接口导入的声明单例与原始外部 BIL 契约；不保留 provider AST。
        internal Dictionary<string, SemanticSymbol> ImportedSymbols { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> ImportedDeclarations { get; } = new(StringComparer.Ordinal);
        internal List<RigiCompiler.Modules.ModuleArtifact> ImportedArtifacts { get; } = new();
        internal List<TypeSymbol> ImportedUserHosts { get; } = new();
        internal List<MethodSymbol> ApprovedLateHelpers { get; } = new();
        internal HashSet<string> LateHelperOverrides { get; } = new(StringComparer.Ordinal);
        // Monitor 可重入：同线程递归能取施工壳，其他线程必须等整次施工结束。
        private readonly object constructionGate = new();
        private List<ConstructedTypeKey>? constructionTransaction;

        // 全局命名空间（Name == ""）：无 namespace 声明的文件归属于此；
        // 用户命名空间树与 bootstrap 的 core 都挂在它下面
        public NamespaceSymbol GlobalNamespace { get; }

        // 构造泛型类型驻留 cache：同一 (泛型定义, 实参列表) 必得同一实例（§4.2）
        private readonly Dictionary<ConstructedTypeKey, TypeSymbol> constructedTypes =
            new Dictionary<ConstructedTypeKey, TypeSymbol>();

        public bool IsFrozen { get; private set; }

        // 返回驻留快照；遍历期间的泛型替换仍可安全驻留新类型。
        internal IReadOnlyList<TypeSymbol> ConstructedTypeSnapshot()
        {
            lock (constructionGate)
                return constructedTypes.Values.OrderBy(StableTypeIdentity, StringComparer.Ordinal).ToArray();
        }

        // 类型引用解析失败的毒化符号单例（P2 DeclarationResolver 使用）
        public ErrorTypeSymbol ErrorType { get; }

        public SymbolGraph(string moduleIdentity = "module") : this(moduleIdentity, false) { }

        internal SymbolGraph(string moduleIdentity, bool artifactOnly)
        {
            ModuleIdentity = moduleIdentity ?? throw new ArgumentNullException(nameof(moduleIdentity));
            GlobalNamespace = new NamespaceSymbol("");
            Bootstrap = new BootstrapSymbols(GlobalNamespace, artifactOnly);
            ErrorType = new ErrorTypeSymbol();
            AssignBootstrapIdentities(GlobalNamespace, "bootstrap");
            if (!artifactOnly) using (CompilerJobs.WithJobs(1)) Bootstrap.BindSourceSignatures(this);
            // 源码签名绑定后才存在方法；覆盖自举临时 unit 的文件路径身份。
            AssignBootstrapIdentities(GlobalNamespace, "bootstrap");
        }

        internal static SymbolGraph CreateArtifactOnly(string moduleIdentity) => new(moduleIdentity, true);

        public void Freeze()
        {
            IsFrozen = true;
        }

        // 命名空间逐段驻留（§4.2 同一份实体恰一个实例）：同一路径必得同一
        // 实例，多文件声明同一 namespace 因此天然合并（P1 DeclarationCollector 用）
        public NamespaceSymbol GetNamespace(IReadOnlyList<string> segments)
        {
            var current = GlobalNamespace;
            foreach (var segment in segments)
            {
                var next = current.ChildNamespaces.Find(ns => ns.Name == segment);
                if (next == null)
                {
                    next = new NamespaceSymbol(segment, current);
                    current.ChildNamespaces.Add(next);
                }
                current = next;
            }
            return current;
        }

        // T? 即构造类型 Nullable\<T>（SYNTAX §3.4：不设独立 nullable 表示）
        public TypeSymbol GetNullable(TypeSymbol elementType)
        {
            return GetConstructedType(Bootstrap.NullableDefinition, elementType);
        }

        public TypeSymbol GetConstructedType(TypeSymbol definition, params SemanticSymbol[] typeArguments)
        {
            return GetConstructedType(definition, (IReadOnlyList<SemanticSymbol>)typeArguments);
        }

        public TypeSymbol GetConstructedType(TypeSymbol definition, IReadOnlyList<SemanticSymbol> typeArguments)
        {
            lock (constructionGate)
            {
                var outermost = constructionTransaction == null;
                if (outermost) constructionTransaction = new();
                try
                {
                    // §3.4：泛型 T? 代入已经可空的 T 时仍是同一层可空视图。
                    // 在统一驻留边界归一，保证字段、返回值与方法实参替换口径一致。
                    if (ReferenceEquals(definition, Bootstrap.NullableDefinition)
                        && typeArguments.Count == 1
                        && typeArguments[0] is TypeSymbol nullable
                        && ReferenceEquals(nullable.ConstructedFrom, definition))
                    {
                        return nullable;
                    }
                    // 实参列表复制一份：驻留键与符号共用同一数组，杜绝调用方事后改写
                    var args = new SemanticSymbol[typeArguments.Count];
                    for (int i = 0; i < args.Length; i++)
                    {
                        args[i] = typeArguments[i];
                    }
                    var key = new ConstructedTypeKey(definition, args);
                    if (!constructedTypes.TryGetValue(key, out var constructed))
                    {
                        constructed = new TypeSymbol(definition, args);
                        // 先入表再回填 BaseType：Substitute 递归驻留回到本键时（如
                        // `class A\<T\> : B\<A\<T\>>`）命中半成品实例，避免无限重入
                        constructedTypes.Add(key, constructed);
                        constructionTransaction!.Add(key);
                        // 基类按定义 → 构造代入（Substitute 对非泛型基类原样返回）。
                        // 注意：InheritanceResolver 完成前驻留的构造类型拿到的是定义的
                        // 默认基类快照（定义显式基类尚未解析）——由 P2 继承解析完成后的
                        // BackfillConstructedBaseTypes 统一重算；此后（含 P3/P4）新驻留
                        // 的构造类型创建即正确。定义基类恒为 TypeSymbol（继承解析已校验），
                        // 代入结果强转安全
                        constructed.BaseType = (TypeSymbol?)Substitute(definition.BaseType, definition, constructed);
                    }
                    return constructed;
                }
                catch
                {
                    if (outermost)
                        foreach (var failedKey in constructionTransaction!) constructedTypes.Remove(failedKey);
                    throw;
                }
                finally { if (outermost) constructionTransaction = null; }
            }
        }

        // P2 继承解析完成后的统一回填（DeclarationResolver 在 InheritanceResolver
        // 之后调用一次）：重算全部已驻留构造类型的 BaseType 为
        // Substitute(定义基类, 定义, 构造实例)——同时修复「定义基类未解析时的
        // 陈旧快照」与「引用定义泛型参数的未代入快照」（如 Sub\<i32\>.BaseType
        // 应为 Base\<i32\> 而非 Base\<T-sub\>）。
        internal void BackfillConstructedBaseTypes()
        {
            lock (constructionGate)
            {
            // 快照遍历：Substitute 可能驻留新构造类型（新实例创建即代入正确，
            // 无需二次回填），先复制避免遍历时改表
            foreach (var constructed in constructedTypes.Values.ToArray())
            {
                var definition = constructed.ConstructedFrom!;
                constructed.BaseType = (TypeSymbol?)Substitute(definition.BaseType, definition, constructed);
            }

            }
        }

        // 泛型实参代入：类型中的泛型参数按构造类型的实参列表替换（递归；
        // 嵌套构造逐实参代入后经驻留入口重建）。非泛型参数/非构造类型原样返回。
        internal SemanticSymbol? Substitute(SemanticSymbol? type, TypeSymbol definition,
            TypeSymbol constructed)
        {
            lock (constructionGate)
            {
            if (type == null || ReferenceEquals(definition, constructed)) return type;
            if (type is GenericParameterSymbol gp)
            {
                var index = definition.GenericParameters.IndexOf(gp);
                return index >= 0 ? constructed.TypeArguments![index] : type;
            }
            if (type is TypeSymbol { ConstructedFrom: not null } inner)
            {
                var innerDef = inner.ConstructedFrom!;
                var args = new SemanticSymbol[inner.TypeArguments!.Count];
                for (int i = 0; i < args.Length; i++)
                {
                    args[i] = Substitute(inner.TypeArguments[i], definition, constructed)!;
                }
                return GetConstructedType(innerDef, args);
            }
            return type;

            }
        }

        internal string StableTypeIdentity(SemanticSymbol symbol)
        {
            if (symbol is TypeSymbol { ConstructedFrom: { } definition, TypeArguments: { } args })
                return StableTypeIdentity(definition) + "<[" + string.Join("][", args.Select(StableTypeIdentity)) + "]>";
            if (symbol.StableIdentity is { } stable) return stable;
            return symbol switch
            {
                TypeSymbol type => (type.DeclaringType != null ? StableTypeIdentity(type.DeclaringType)
                    : "namespace/" + type.Namespace?.FullName) + "/type/" + type.Name + "/" + type.GenericParameters.Count,
                MethodSymbol method => (method.Owner != null ? StableTypeIdentity(method.Owner)
                    : "namespace/" + method.Namespace?.FullName) + "/method/" + method.Name + "/" + method.Kind
                    + "/parameters/" + string.Join(";", method.Parameters.Select(parameter =>
                        parameter.Type == null ? "unknown" : StableTypeIdentity(parameter.Type))),
                _ => throw new CompilerInternalException("缺少稳定符号身份：" + symbol.Name),
            };
        }

        private static void AssignBootstrapIdentities(NamespaceSymbol ns, string prefix)
        {
            ns.StableIdentity = prefix + "/namespace/" + ns.FullName;
            void Type(TypeSymbol type, string owner)
            {
                type.StableIdentity = owner + "/type/" + type.Name + "/" + type.GenericParameters.Count;
                for (var i = 0; i < type.GenericParameters.Count; i++)
                    type.GenericParameters[i].StableIdentity = type.StableIdentity + "/gp/" + i;
                for (var i = 0; i < type.Methods.Count; i++)
                {
                    var method = type.Methods[i];
                    method.StableIdentity = type.StableIdentity + "/method/" + i;
                    for (var j = 0; j < method.GenericParameters.Count; j++)
                        method.GenericParameters[j].StableIdentity = method.StableIdentity + "/gp/" + j;
                }
                foreach (var child in type.NestedTypes) Type(child, type.StableIdentity);
            }
            foreach (var type in ns.Types) Type(type, ns.StableIdentity);
            foreach (var child in ns.ChildNamespaces) AssignBootstrapIdentities(child, prefix);
        }

        // 驻留键：定义与实参一律引用相等（引用相等即身份相等，§4.2）
        private readonly struct ConstructedTypeKey : IEquatable<ConstructedTypeKey>
        {
            private readonly TypeSymbol definition;
            private readonly IReadOnlyList<SemanticSymbol> arguments;

            public ConstructedTypeKey(TypeSymbol definition, IReadOnlyList<SemanticSymbol> arguments)
            {
                this.definition = definition;
                this.arguments = arguments;
            }

            public bool Equals(ConstructedTypeKey other)
            {
                if (!ReferenceEquals(definition, other.definition) || arguments.Count != other.arguments.Count)
                {
                    return false;
                }
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (!ReferenceEquals(arguments[i], other.arguments[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            public override bool Equals(object? obj)
            {
                return obj is ConstructedTypeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                int hash = RuntimeHelpers.GetHashCode(definition);
                foreach (var arg in arguments)
                {
                    hash = (hash * 31) + RuntimeHelpers.GetHashCode(arg);
                }
                return hash;
            }
        }
    }
}
