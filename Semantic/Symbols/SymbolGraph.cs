using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace LatteCompiler
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
        }

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
                constructedTypes.Add(key, constructed);
            }
            return constructed;
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
