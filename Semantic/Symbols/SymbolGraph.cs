using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace RigiCompiler
{
    // 符号图容器（SEMANTIC_ARCHITECTURE §4）：编译单元唯一符号对象图的持有者。
    // 构造期两阶段（P1 建壳、P2 填内容），P2 结束 Freeze 后声明侧不可变
    // （P3/P4 只读）；构造泛型类型驻留 cache 是幂等透明派生物，不受冻结限制。
    public sealed class SymbolGraph
    {
        public BootstrapSymbols Bootstrap { get; }

        // 全局命名空间（Name == ""）：无 namespace 声明的文件归属于此；
        // 用户命名空间树与 bootstrap 的 core 都挂在它下面
        public NamespaceSymbol GlobalNamespace { get; }

        // 构造泛型类型驻留 cache：同一 (泛型定义, 实参列表) 必得同一实例（§4.2）
        private readonly Dictionary<ConstructedTypeKey, TypeSymbol> constructedTypes =
            new Dictionary<ConstructedTypeKey, TypeSymbol>();

        public bool IsFrozen { get; private set; }

        // 类型引用解析失败的毒化符号单例（P2 DeclarationResolver 使用）
        public ErrorTypeSymbol ErrorType { get; }

        public SymbolGraph()
        {
            GlobalNamespace = new NamespaceSymbol("");
            Bootstrap = new BootstrapSymbols(GlobalNamespace);
            ErrorType = new ErrorTypeSymbol();
            // Q6（SYNTAX §13.2）：索引读取一律返回 T?——内建 Array/Span/SharedSpan
            // 的 getAtIndex 返回类型由 T 改为 Nullable\<T\>。bootstrap 构造
            // 期拿不到本图的驻留设施（GetNullable 经 constructedTypes 驻留），
            // 故在建图后即刻回填；此回填早于任何 P1–P4 消费，语义等同声明期
            BackfillIndexGetNullable(Bootstrap.ArrayDefinition);
            BackfillIndexGetNullable(Bootstrap.SpanDefinition);
            BackfillIndexGetNullable(Bootstrap.SharedSpanDefinition);
        }

        public void Freeze()
        {
            IsFrozen = true;
        }

        private void BackfillIndexGetNullable(TypeSymbol definition)
        {
            var getAtIndex = definition.Methods.First(m => m.Name == "getAtIndex");
            getAtIndex.ReturnType = GetConstructedType(Bootstrap.NullableDefinition,
                definition.GenericParameters[0]);
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

        // P2 继承解析完成后的统一回填（DeclarationResolver 在 InheritanceResolver
        // 之后调用一次）：重算全部已驻留构造类型的 BaseType 为
        // Substitute(定义基类, 定义, 构造实例)——同时修复「定义基类未解析时的
        // 陈旧快照」与「引用定义泛型参数的未代入快照」（如 Sub\<i32\>.BaseType
        // 应为 Base\<i32\> 而非 Base\<T-sub\>）。
        internal void BackfillConstructedBaseTypes()
        {
            // 快照遍历：Substitute 可能驻留新构造类型（新实例创建即代入正确，
            // 无需二次回填），先复制避免遍历时改表
            foreach (var constructed in constructedTypes.Values.ToArray())
            {
                var definition = constructed.ConstructedFrom!;
                constructed.BaseType = (TypeSymbol?)Substitute(definition.BaseType, definition, constructed);
            }
        }

        // 泛型实参代入：类型中的泛型参数按构造类型的实参列表替换（递归；
        // 嵌套构造逐实参代入后经驻留入口重建）。非泛型参数/非构造类型原样返回。
        internal SemanticSymbol? Substitute(SemanticSymbol? type, TypeSymbol definition,
            TypeSymbol constructed)
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
