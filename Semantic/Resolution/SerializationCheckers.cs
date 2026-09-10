namespace RigiCompiler
{
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
                // SB 实现编码公开的值/元素，不遍历容器的内部表示。
                if (serializationBase != null && SerializationFacts.HasWrapper(host, serializationBase))
                {
                    if (!SerializationFacts.HasBaseCodec(host, symbols))
                        env.Error(entry.Node.Span,
                            $"SB 类型 '{host.Name}' 未实现对应的 Serializable 编解码，不能按普通值复制");
                    continue;
                }
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
        // 固定基元与标准容器的编码器集合；同名或任意 SB 应用不能取得值复制特权。
        public static bool HasBaseCodec(TypeSymbol type, SymbolGraph symbols)
        {
            var definition = type.ConstructedFrom ?? type;
            return (definition.IsBuiltin && definition.Kind == TypeKind.Struct
                    && definition.Name is "bool" or "char" or "i8" or "u8" or "i16" or "u16"
                        or "i32" or "u32" or "i64" or "u64" or "float" or "double" or "String")
                || IsArray(type, symbols, out _) || IsList(type, symbols, out _)
                || IsMap(type, symbols, out _, out _) || IsParcel(type, symbols);
        }

        public static TypeSymbol? FindWrapper(SymbolGraph symbols, string name)
        {
            return (name == "SerializationBase" ? symbols.Bootstrap.Core : FindSerializationNamespace(symbols))?.Types
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

        public static bool IsSerializableWrapper(TypeSymbol wrapper, SymbolGraph symbols)
        {
            var definition = wrapper.ConstructedFrom ?? wrapper;
            // 能力属于标准库符号身份；同名用户 wrapper 不能获得序列化特化。
            return ReferenceEquals(definition, FindWrapper(symbols, "Serializable"));
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
                    if (ReferenceEquals(boundDef, serializable))
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
            // 只有不含托管载荷的非 rich enum 可以直接按值保存。
            // rich enum 必须显式声明 Serializable，递归编码其对象字段。
            if (fieldType is TypeSymbol { Kind: TypeKind.EnumStruct, IsRich: false }) return true;
            if (HasWrapper(fieldType, serializable)) return true;
            var name = fieldType.Name;
            reason = $"类型 '{name}' 未声明 Serializable，也未被 @Temporary 切断";
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
