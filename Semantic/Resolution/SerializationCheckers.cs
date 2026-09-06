namespace RigiCompiler
{
    // ===== MW11d A4：内建类型登记 SerializationBase =====
    //
    // 内建类型无 stdlib 源可写 @SerializationBase，由编译器在符号层合成
    // 「已应用」事实，供 with SerializationBase 与 A5 可序列性检查消费。
    // VM/native with 运算若走 TypeInfo.wrappers，属 Phase B 遗留。
    internal sealed class SerializationBaseRegistrar : ResolverVisitor<SerializationBaseRegistrar>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            var serializationBase = SerializationFacts.FindWrapper(env.Unit.Symbols, "SerializationBase");
            if (serializationBase == null) return;
            var b = env.Unit.Symbols.Bootstrap;
            foreach (var type in new[]
            {
                b.Int8, b.Int16, b.Int32, b.Int64,
                b.UInt8, b.UInt16, b.UInt32, b.UInt64,
                b.Float, b.Double, b.Char, b.Bool, b.String,
                b.ArrayDefinition,
            })
            {
                Register(type, serializationBase);
            }
            // List/Map 声明在 core.collections，源码直接 @SerializationBase
            // 会触发 @Internal 拒绝；与 Array 同通道走编译器内部登记。
            var collections = b.Core.ChildNamespaces.FirstOrDefault(n => n.Name == "collections");
            if (collections != null)
            {
                foreach (var type in collections.Types)
                {
                    if (type.Kind != TypeKind.Class) continue;
                    if (type.Name is not ("List" or "Map")) continue;
                    Register(type, serializationBase);
                }
            }
        }

        private static void Register(TypeSymbol type, TypeSymbol serializationBase)
        {
            if (type.AppliedWrappers.Any(w =>
                ReferenceEquals(w.WrapperDefinition, serializationBase)))
            {
                return;
            }
            type.AppliedWrappers.Add(WrapperApplication.FromConstraint(serializationBase));
        }
    }

    // ===== @SerializationBase 隐含 @Serializable =====
    //
    // SerializationBase 与 Serializable 无继承关系，但源码级 @SerializationBase
    // 的宿主视同同时应用 @Serializable（合成 toParcel/fromParcel、字段可序列化
    // 检查、with Serializable 约束满足、:Serializable 暴露面、BIL
    // wrapped(Serializable) 发射与运行期 with 判定——下游判定点统一读
    // AppliedWrappers，在此追加一次合成应用即全链生效）。
    // 判据 Syntax != null：区分 SerializationBaseRegistrar 对内建基元
    // （i32/String/Array/List/Map）的 FromConstraint 登记——内建基元绝不能
    // 获得 Serializable（§20.2.3 回归防线）。
    // 豁免 core.serialization::Parcel：Parcel 是动态类型容器本体，其
    // data: Map<String, Any> 字段设计上不满足静态可序列性（§20.3 Map 递归
    // 检查 value=Any 不收）；若隐含 Serializable，字段检查与合成编码
    // （EncodeMap 以 T=Any 调 setElement<T with SerializationBase>）会炸掉
    // stdlib 编译。
    internal sealed class SerializableImplicationRegistrar : ResolverVisitor<SerializableImplicationRegistrar>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            var symbols = env.Unit.Symbols;
            var serializable = SerializationFacts.FindWrapper(symbols, "Serializable");
            var serializationBase = SerializationFacts.FindWrapper(symbols, "SerializationBase");
            if (serializable == null || serializationBase == null) return;
            var parcel = SerializationFacts.FindParcel(symbols);

            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph || entry.Symbol is not TypeSymbol
                    { Kind: TypeKind.Class or TypeKind.Struct } host) continue;
                if (parcel != null && ReferenceEquals(host, parcel)) continue;
                if (!host.AppliedWrappers.Any(w =>
                    ReferenceEquals(w.WrapperDefinition, serializationBase) && w.Syntax != null))
                {
                    continue;
                }
                if (SerializationFacts.HasWrapper(host, serializable)) continue;
                host.AppliedWrappers.Add(WrapperApplication.FromConstraint(serializable));
            }
        }
    }

    // ===== MW11d A5/B2-3：@Serializable 宿主字段可序列性检查 =====
    internal sealed class SerializableFieldChecker : ResolverVisitor<SerializableFieldChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            var symbols = env.Unit.Symbols;
            var serializable = SerializationFacts.FindWrapper(symbols, "Serializable");
            var serializationBase = SerializationFacts.FindWrapper(symbols, "SerializationBase");
            var temporary = SerializationFacts.FindWrapper(symbols, "Temporary");
            if (serializable == null) return;

            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph || entry.Symbol is not TypeSymbol host) continue;
                if (!SerializationFacts.HasWrapper(host, serializable)) continue;
                foreach (var field in host.Fields)
                {
                    if (field.IsStatic) continue;
                    if (temporary != null && field.AppliedWrappers.Any(w =>
                        ReferenceEquals(w.WrapperDefinition, temporary)))
                    {
                        continue;
                    }
                    if (SerializationFacts.IsStaticallySerializable(
                        field.FieldType, serializable, serializationBase, symbols, out var reason))
                    {
                        continue;
                    }
                    env.Error(entry.Node.Span,
                        $"可序列化类型 '{host.Name}' 的字段 '{field.Name}' 不可序列化：" +
                        reason);
                }
            }
        }
    }

    internal static class SerializationFacts
    {
        public static TypeSymbol? FindWrapper(SymbolGraph symbols, string name)
        {
            return FindSerializationNamespace(symbols)?.Types
                .FirstOrDefault(t => t.Name == name && t.Kind == TypeKind.Wrapper);
        }

        public static NamespaceSymbol? FindSerializationNamespace(SymbolGraph symbols)
        {
            var core = symbols.GlobalNamespace.ChildNamespaces.FirstOrDefault(n => n.Name == "core");
            return core?.ChildNamespaces.FirstOrDefault(n => n.Name == "serialization");
        }

        public static NamespaceSymbol? FindCollectionsNamespace(SymbolGraph symbols)
        {
            return symbols.Bootstrap.Core.ChildNamespaces
                .FirstOrDefault(n => n.Name == "collections");
        }

        public static TypeSymbol? FindCollectionsType(SymbolGraph symbols, string name)
        {
            return FindCollectionsNamespace(symbols)?.Types
                .FirstOrDefault(t => t.Name == name && t.Kind == TypeKind.Class);
        }

        public static TypeSymbol? FindParcel(SymbolGraph symbols)
        {
            return FindSerializationNamespace(symbols)?.Types
                .FirstOrDefault(t => t.Name == "Parcel" && t.Kind == TypeKind.Class);
        }

        public static bool HasWrapper(SemanticSymbol type, TypeSymbol wrapper)
        {
            var definition = type as TypeSymbol;
            if (definition?.ConstructedFrom != null) definition = definition.ConstructedFrom;
            return definition != null
                && definition.AppliedWrappers.Any(w =>
                    ReferenceEquals(w.WrapperDefinition, wrapper));
        }

        public static bool HasSerializableConstraint(SemanticSymbol type, TypeSymbol serializable)
        {
            if (type is not GenericParameterSymbol gp) return false;
            foreach (var constraint in gp.Constraints)
            {
                if (constraint.Kind != GenericConstraintKind.With) continue;
                var bound = constraint.Bound as TypeSymbol;
                var boundDef = bound?.ConstructedFrom ?? bound;
                if (ReferenceEquals(boundDef, serializable)) return true;
            }
            return false;
        }

        public static bool IsSerializableWrapper(TypeSymbol wrapper)
        {
            var definition = wrapper.ConstructedFrom ?? wrapper;
            return definition.Kind == TypeKind.Wrapper && definition.Name == "Serializable";
        }

        public static bool IsArray(SemanticSymbol? type, SymbolGraph symbols,
            out SemanticSymbol? element)
        {
            element = null;
            if (type is not TypeSymbol ts) return false;
            var def = ts.ConstructedFrom ?? ts;
            if (!ReferenceEquals(def, symbols.Bootstrap.ArrayDefinition)) return false;
            element = ts.TypeArguments is { Count: > 0 } args ? args[0] : null;
            return true;
        }

        public static bool IsList(SemanticSymbol? type, SymbolGraph symbols,
            out SemanticSymbol? element)
        {
            element = null;
            var listDef = FindCollectionsType(symbols, "List");
            if (listDef == null || type is not TypeSymbol ts) return false;
            var def = ts.ConstructedFrom ?? ts;
            if (!ReferenceEquals(def, listDef)) return false;
            element = ts.TypeArguments is { Count: > 0 } args ? args[0] : null;
            return true;
        }

        public static bool IsMap(SemanticSymbol? type, SymbolGraph symbols,
            out SemanticSymbol? key, out SemanticSymbol? value)
        {
            key = null;
            value = null;
            var mapDef = FindCollectionsType(symbols, "Map");
            if (mapDef == null || type is not TypeSymbol ts) return false;
            var def = ts.ConstructedFrom ?? ts;
            if (!ReferenceEquals(def, mapDef)) return false;
            if (ts.TypeArguments is { Count: >= 2 } args)
            {
                key = args[0];
                value = args[1];
            }
            return true;
        }

        public static bool IsParcel(SemanticSymbol? type, SymbolGraph symbols)
        {
            var parcel = FindParcel(symbols);
            if (parcel == null || type is not TypeSymbol ts) return false;
            var def = ts.ConstructedFrom ?? ts;
            return ReferenceEquals(def, parcel);
        }

        public static bool IsString(SemanticSymbol? type, SymbolGraph symbols)
        {
            if (type is not TypeSymbol ts) return false;
            var def = ts.ConstructedFrom ?? ts;
            return ReferenceEquals(def, symbols.Bootstrap.String);
        }

        // B2-3：Array/List 递归元素；Map 仅 K=String 且 V 可证明；Parcel 合法；
        // 其余沿 Phase A（SerializationBase / Serializable / 约束泛型）。
        public static bool IsStaticallySerializable(SemanticSymbol? fieldType,
            TypeSymbol serializable, TypeSymbol? serializationBase, SymbolGraph symbols,
            out string reason)
        {
            reason = "";
            if (fieldType is null or ErrorTypeSymbol) return true;
            if (fieldType is TypeSymbol { ConstructedFrom: { } nullable, TypeArguments: { Count: 1 } arguments }
                && ReferenceEquals(nullable, symbols.Bootstrap.NullableDefinition))
                return IsStaticallySerializable(arguments[0], serializable, serializationBase, symbols, out reason);
            if (fieldType is GenericParameterSymbol gp)
            {
                foreach (var constraint in gp.Constraints)
                {
                    if (constraint.Kind != GenericConstraintKind.With) continue;
                    var bound = constraint.Bound as TypeSymbol;
                    var boundDef = bound?.ConstructedFrom ?? bound;
                    if (ReferenceEquals(boundDef, serializable)
                        || ReferenceEquals(boundDef, serializationBase))
                    {
                        return true;
                    }
                }
                reason = $"无约束泛型参数 '{gp.Name}'";
                return false;
            }
            if (IsArray(fieldType, symbols, out var arrayElem))
            {
                if (IsStaticallySerializable(arrayElem, serializable, serializationBase,
                    symbols, out var elemReason))
                {
                    return true;
                }
                reason = $"Array 元素不可序列化：{elemReason}";
                return false;
            }
            if (IsList(fieldType, symbols, out var listElem))
            {
                if (IsStaticallySerializable(listElem, serializable, serializationBase,
                    symbols, out var elemReason))
                {
                    return true;
                }
                reason = $"List 元素不可序列化：{elemReason}";
                return false;
            }
            if (IsMap(fieldType, symbols, out var mapKey, out var mapValue))
            {
                if (!IsStaticallySerializable(mapKey, serializable, serializationBase, symbols, out var keyReason))
                {
                    reason = $"Map 键不可序列化：{keyReason}，可改用 @Temporary 切断该字段";
                    return false;
                }
                if (IsStaticallySerializable(mapValue, serializable, serializationBase,
                    symbols, out var valueReason))
                {
                    return true;
                }
                reason = $"Map 值不可序列化：{valueReason}";
                return false;
            }
            if (IsParcel(fieldType, symbols)) return true;
            if (HasWrapper(fieldType, serializable)) return true;
            if (serializationBase != null && HasWrapper(fieldType, serializationBase)) return true;
            var name = fieldType.Name;
            reason = $"类型 '{name}' 既非 SerializationBase/Serializable，也未被 @Temporary 切断";
            return false;
        }

        public static bool IsStaticallySerializable(SemanticSymbol? fieldType,
            TypeSymbol serializable, TypeSymbol? serializationBase, SymbolGraph symbols)
        {
            return IsStaticallySerializable(fieldType, serializable, serializationBase,
                symbols, out _);
        }
    }
}
