using System.Collections.Generic;

namespace LatteCompiler
{
    // canonical symbol 打印（SEMANTIC_ARCHITECTURE §4.4）：BIL §5.2 的
    // canonical 字符串是符号图的序列化投影，供 BIL 发射、诊断消息与派发链
    // 诊断工具（RUNTIME §15）共用。中端内部任何地方不得以此字符串做身份
    // 比较或查找键（BIL 发射之后的世界才以字符串为身份）。
    public static class CanonicalSymbolPrinter
    {
        public static string Print(SemanticSymbol symbol) => symbol switch
        {
            TypeSymbol type => PrintType(type),
            FieldSymbol field => PrintField(field),
            MethodSymbol method => PrintMethod(method),
            EnumCaseSymbol enumCase => PrintCase(enumCase),
            NamespaceSymbol ns => ns.FullName,
            GenericParameterSymbol generic => PrintTypeArgument(generic),
            _ => throw new CompilerInternalException($"未知的语义符号类型: {symbol.GetType().Name}"),
        };

        // BIL 类型引用投影（§6.1）：固定内建别名 > 标准类型构造 > canonical/闭合泛型；
        // S9 放宽为 SemanticSymbol——泛型参数走 §7.5 的 .generic<$.generic.T> 形态
        public static string PrintType(SemanticSymbol type)
        {
            return type switch
            {
                GenericParameterSymbol generic => $".generic<$.generic.{generic.Name}>",
                TypeSymbol symbol => PrintTypeSymbol(symbol),
                _ => throw new CompilerInternalException(
                    $"非法类型引用符号: {type.GetType().Name}"),
            };
        }

        private static string PrintTypeSymbol(TypeSymbol type)
        {
            if (type.BilAlias != null)
            {
                return type.BilAlias;
            }
            if (type.ConstructedFrom is { } definition)
            {
                var args = PrintTypeArguments(type.TypeArguments!);
                // 标准类型构造（§6.3：.nullable<T> / .typeid<T> / .array<T> …）
                if (definition.BilStandardConstructor != null)
                {
                    return $"{definition.BilStandardConstructor}<{args}>";
                }
                // 用户闭合泛型（§5.2：符号的类型部分可出现闭合泛型）
                return $"{CanonicalTypeName(definition)}<{args}>";
            }
            return CanonicalTypeName(type);
        }

        // 返回类型等可空位置：null 即 .void（§6.2：只能作无结果方法的返回类型）；
        // SemanticSymbol：TypeSymbol 走 PrintType，泛型参数走 §7.5 的 .generic 形态
        public static string PrintTypeReference(SemanticSymbol? type)
        {
            return type == null ? ".void" : Print(type);
        }

        // 字段 / 全局变量 / 全局常量（§5.2）：命名空间::[类名...]#[.static.]名称@字段类型。
        // cell 化的静态/全局字段（统一 cell 存储，SYNTAX §14.3）：存储类型为
        // cell 隐藏子类（值类型仍在符号 FieldType 上，语义层类型不变）
        public static string PrintField(FieldSymbol field)
        {
            var prefix = OwnerPrefix(field.Owner, field.Namespace);
            var staticMark = field.IsStatic ? ".static." : "";
            var fieldType = field.CellStorage is { } storage
                ? PrintType(storage.CellType)
                : PrintTypeReference(field.FieldType);
            return $"{prefix}#{staticMark}{field.Name}@{fieldType}";
        }

        // 方法（§5.2）：普通/init 走 $名 形态；operator 用 $$；getter/setter
        // 用 $.get.名 / $.set.名（无参数段）
        public static string PrintMethod(MethodSymbol method)
        {
            var prefix = OwnerPrefix(method.Owner, method.Namespace);
            switch (method.Kind)
            {
                case MethodKind.Getter:
                    return $"{prefix}${(method.IsStatic ? ".static" : "")}.get.{method.Name}@{PrintTypeReference(method.ReturnType)}";
                case MethodKind.Setter:
                    var valueType = method.Parameters.Count > 0 ? method.Parameters[0].Type : null;
                    return $"{prefix}${(method.IsStatic ? ".static" : "")}.set.{method.Name}@{PrintTypeReference(valueType)}";
                case MethodKind.Operator:
                    return $"{prefix}$${method.Name}({PrintParameters(method)})@{PrintTypeReference(method.ReturnType)}";
                default:
                    var staticMark = method.IsStatic ? ".static." : "";
                    return $"{prefix}${staticMark}{method.Name}({PrintParameters(method)})@{PrintTypeReference(method.ReturnType)}";
            }
        }

        // enum case（BIL §8.5）：宿主类型 canonical 名 + "." + case 名
        // （com.example::RequestResult.Failed）
        public static string PrintCase(EnumCaseSymbol enumCase)
        {
            return $"{PrintType(enumCase.Owner)}.{enumCase.Name}";
        }

        // S11e 降级请求 canonical symbol（SYNTAX §14.7/§14.8 落地形态）：未声明
        // 方法无声明位置与参数名——宿主前缀 = receiver 静态类型定义级 canonical
        // 名；显式泛型实参按调用点书写序编码在方法名后的 <...> 段；参数段按
        // 调用点书写序：位置实参只写静态类型、具名实参写 名:类型；
        // 返回段恒 .any（胖值 ABI 返回 Any，调用点转换由 P4a §6.5 物化承担，
        // 不符抛 core.CastException）
        public static string PrintDowngradeRequest(TypeSymbol receiverType, string name,
            IReadOnlyList<SemanticSymbol> typeArguments,
            IReadOnlyList<(string? ArgName, SemanticSymbol ArgType)> arguments)
        {
            var parts = new List<string>();
            foreach (var (argName, argType) in arguments)
            {
                parts.Add(argName == null
                    ? PrintTypeReference(argType)
                    : argName + ":" + PrintTypeReference(argType));
            }
            var owner = CanonicalTypeName(receiverType.ConstructedFrom ?? receiverType);
            var genericPart = typeArguments.Count == 0
                ? ""
                : $"<{string.Join(",", typeArguments.Select(PrintTypeReference))}>";
            return $"{owner}${name}{genericPart}({string.Join(",", parts)})@.any";
        }

        // 成员前缀：宿主类型 canonical 名；全局符号：命名空间全名 + "::"
        private static string OwnerPrefix(TypeSymbol? owner, NamespaceSymbol? ns)
        {
            if (owner != null)
            {
                return CanonicalTypeName(owner);
            }
            return ns is { FullName: { Length: > 0 } fullName } ? fullName + "::" : "";
        }

        // 参数段（§5.2：(参数名:参数类型,...)）。
        // S9d：可变参数（IsVariadic/IsNamedVariadic）不进 canonical 参数段
        // ——它们以隐藏参数形态存在于 fn .args（§7.1：.vargs.args/.kwargs.args）
        private static string PrintParameters(MethodSymbol method)
        {
            var parts = new List<string>();
            foreach (var p in method.Parameters)
            {
                if (p.IsVariadic || p.IsNamedVariadic) continue;
                parts.Add($"{p.Name}:{PrintTypeReference(p.Type)}");
            }
            return string.Join(",", parts);
        }

        // canonical 类型名：命名空间::外层.内层（嵌套链沿 DeclaringType 走）
        private static string CanonicalTypeName(TypeSymbol type)
        {
            var segments = new List<string>();
            var root = type;
            for (var t = type; t != null; t = t.DeclaringType)
            {
                segments.Insert(0, t.Name);
                root = t;
            }
            var ns = root.Namespace?.FullName;
            return (ns is { Length: > 0 } ? ns + "::" : "") + string.Join(".", segments);
        }

        private static string PrintTypeArguments(IReadOnlyList<SemanticSymbol> arguments)
        {
            var parts = new List<string>();
            foreach (var arg in arguments)
            {
                parts.Add(PrintTypeArgument(arg));
            }
            return string.Join(", ", parts);
        }

        private static string PrintTypeArgument(SemanticSymbol argument) => argument switch
        {
            TypeSymbol type => PrintType(type),
            // §7.5：函数体中泛型参数的值类型引用形态
            GenericParameterSymbol generic => $".generic<$.generic.{generic.Name}>",
            _ => throw new CompilerInternalException($"非法泛型实参符号: {argument.GetType().Name}"),
        };
    }
}
