using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler
{
    // 符号路径与类型引用解析（P2 声明骨架与 P3 函数体共用）。
    // 查找序：泛型参数（方法 → 宿主类型链）→ 宿主类型链 NestedTypes →
    //   文件命名空间及父链 → 全局命名空间 → import 列表（具名/通配）→
    //   core 命名空间（隐式可见：i32/String/Object 等裸名由此解析）。
    // 诊断按构造时给定的 Phase 写入编译单元诊断袋；解析失败一律返回
    // ErrorTypeSymbol 毒化（reportErrors 控制是否产出诊断——import 消费
    // 路径静默，由 import 校验统一报一次）。
    internal sealed class NameResolver
    {
        private readonly CompilationUnit unit;
        private readonly DiagnosticPhase phase;

        public NameResolver(CompilationUnit unit, DiagnosticPhase phase)
        {
            this.unit = unit;
            this.phase = phase;
        }

        private void Error(CharRange? span, string message)
        {
            unit.Diagnostics.Error(phase, span, message);
        }

        // 类型引用解析：T? 即构造类型 Nullable\<T>（SYNTAX §3.4）
        public SemanticSymbol ResolveTypeReference(TypeReferenceASTNode typeRef, FileContext ctx,
            TypeSymbol? declaringType, MethodSymbol? declaringMethod, CharRange? span)
        {
            var resolved = ResolveSymbolPath(typeRef.TypeSymbol.symbol, ctx,
                declaringType, declaringMethod, allowImports: true,
                reportErrors: true, span: span ?? typeRef.Span);
            if (typeRef.IsNullable && resolved is not ErrorTypeSymbol)
            {
                return unit.Symbols.GetConstructedType(unit.Symbols.Bootstrap.NullableDefinition, resolved);
            }
            return resolved;
        }

        // 符号路径解析：首段按查找序定位，后续逐段下钻，末段应用泛型实参。
        public SemanticSymbol ResolveSymbolPath(Symbol path, FileContext ctx,
            TypeSymbol? declaringType, MethodSymbol? declaringMethod,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            var elements = path.elements;
            if (elements.Count == 0)
            {
                if (reportErrors) Error(span, "Empty type reference");
                return unit.Symbols.ErrorType;
            }
            SemanticSymbol? current;
            // 泛型参数仅接受单段裸名引用（方法 → 宿主类型链）
            if (elements.Count == 1 && elements[0].generics.Count == 0)
            {
                current = FindGenericParameter(elements[0].name, declaringType, declaringMethod);
                if (current != null) return current;
            }
            current = ResolveFirstSegment(elements[0].name, ctx, declaringType, allowImports,
                elements.Count == 1 ? elements[0].generics.Count : -1, out var importResolved);
            if (current == null)
            {
                if (reportErrors && !importResolved)
                {
                    Error(span, $"Unresolved type or namespace: '{elements[0].name}'");
                }
                return unit.Symbols.ErrorType;
            }
            // 逐段下钻（命名空间 → 子命名空间/类型；类型 → 嵌套类型）。
            // 中间段不带泛型实参（-1 不筛元数）；末段按该段泛型实参个数分流
            // （S10：Task 与 Task\<T\> 同名共存——裸名优先非泛型、带实参优先
            // 精确元数，见 FindTypeIn）
            for (int i = 1; i < elements.Count; i++)
            {
                var next = Descend(current, elements[i].name,
                    i == elements.Count - 1 ? elements[^1].generics.Count : -1);
                if (next == null)
                {
                    if (reportErrors)
                    {
                        Error(span, $"Unresolved type or namespace: '{PathText(path)}'");
                    }
                    return unit.Symbols.ErrorType;
                }
                current = next;
            }
            // 末段泛型实参（递归解析后驻留构造）
            var last = elements[^1];
            if (last.generics.Count > 0)
            {
                return ApplyTypeArguments(current, last, ctx, declaringType, declaringMethod,
                    allowImports, reportErrors, span);
            }
            return current;
        }

        // ext 目标路径（字符串段，SYNTAX §4.4 原文无泛型）走同一查找序
        public SemanticSymbol ResolveDottedPath(string[] segments, FileContext ctx,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            var current = ResolveFirstSegment(segments[0], ctx, declaringType: null,
                allowImports, arity: -1, out var importResolved);
            if (current == null)
            {
                if (reportErrors && !importResolved)
                {
                    Error(span, $"Unresolved extension target: '{string.Join(".", segments)}'");
                }
                return unit.Symbols.ErrorType;
            }
            for (int i = 1; i < segments.Length; i++)
            {
                var next = Descend(current, segments[i], arity: -1);
                if (next == null)
                {
                    if (reportErrors)
                    {
                        Error(span, $"Unresolved extension target: '{string.Join(".", segments)}'");
                    }
                    return unit.Symbols.ErrorType;
                }
                current = next;
            }
            return current;
        }

        // 首段查找序：宿主类型链 NestedTypes → 文件命名空间及父链 → 全局命名空间
        // → import 列表（具名末段同名 / 通配容器内查）→ core 命名空间（隐式）。
        // importResolved：具名 import 命中但自身解析失败（已诊断过）时为 true——
        // 调用方静默毒化，不再报「未解析」。
        // arity：该段（单段路径即末段）的泛型实参个数；-1 = 容器下钻不筛元数。
        private SemanticSymbol? ResolveFirstSegment(string name, FileContext ctx,
            TypeSymbol? declaringType, bool allowImports, int arity, out bool importResolved)
        {
            importResolved = false;
            for (var t = declaringType; t != null; t = t.DeclaringType)
            {
                var nested = FindTypeIn(t.NestedTypes, name, arity);
                if (nested != null) return nested;
            }
            for (var ns = ctx.Namespace; ns != null; ns = ns.Parent)
            {
                var hit = FindInNamespace(ns, name, arity);
                if (hit != null) return hit;
            }
            var globalHit = FindInNamespace(unit.Symbols.GlobalNamespace, name, arity);
            if (globalHit != null) return globalHit;
            if (allowImports)
            {
                foreach (var item in ctx.Imports)
                {
                    var importPath = item.symbolNode.symbol;
                    if (item.importAll)
                    {
                        var container = ResolveSymbolPath(importPath, ctx, declaringType: null,
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null);
                        SemanticSymbol? hit = container switch
                        {
                            NamespaceSymbol ns => FindTypeIn(ns.Types, name, arity),
                            TypeSymbol t => FindTypeIn(t.NestedTypes, name, arity),
                            _ => null,
                        };
                        if (hit != null) return hit;
                    }
                    else if (importPath.elements.Count > 0 && importPath.elements[^1].name == name)
                    {
                        var resolved = ResolveSymbolPath(importPath, ctx, declaringType: null,
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null);
                        if (resolved is ErrorTypeSymbol)
                        {
                            importResolved = true;
                            return null;
                        }
                        return resolved;
                    }
                }
            }
            return FindInNamespace(unit.Symbols.Bootstrap.Core, name, arity);
        }

        // 按「名 + 期望元数」在类型表中查找（S10，SYNTAX §15.3：同名不同
        // 元数合法共存）：arity >= 0 时优先精确元数匹配，回退同名任意声明
        // （带实参但元数不匹配者落入 ApplyTypeArguments 的元数诊断；裸名但
        // 只有泛型定义者回退定义本身——保持既有行为）；arity < 0 不筛。
        private static TypeSymbol? FindTypeIn(IReadOnlyList<TypeSymbol> types, string name, int arity)
        {
            if (arity >= 0)
            {
                var exact = types.FirstOrDefault(t => t.Name == name && t.GenericParameters.Count == arity);
                if (exact != null) return exact;
            }
            return types.FirstOrDefault(t => t.Name == name);
        }

        private static SemanticSymbol? FindInNamespace(NamespaceSymbol ns, string name, int arity)
        {
            return (SemanticSymbol?)FindTypeIn(ns.Types, name, arity)
                ?? ns.ChildNamespaces.FirstOrDefault(n => n.Name == name);
        }

        private static SemanticSymbol? Descend(SemanticSymbol current, string name, int arity) => current switch
        {
            NamespaceSymbol ns => (SemanticSymbol?)ns.ChildNamespaces.FirstOrDefault(n => n.Name == name)
                ?? FindTypeIn(ns.Types, name, arity),
            TypeSymbol t => FindTypeIn(t.NestedTypes, name, arity),
            _ => null,
        };

        private static GenericParameterSymbol? FindGenericParameter(
            string name, TypeSymbol? declaringType, MethodSymbol? declaringMethod)
        {
            if (declaringMethod != null)
            {
                var hit = declaringMethod.GenericParameters.FirstOrDefault(p => p.Name == name);
                if (hit != null) return hit;
            }
            for (var t = declaringType; t != null; t = t.DeclaringType)
            {
                var hit = t.GenericParameters.FirstOrDefault(p => p.Name == name);
                if (hit != null) return hit;
            }
            return null;
        }

        private SemanticSymbol ApplyTypeArguments(SemanticSymbol current, SymbolElement last,
            FileContext ctx, TypeSymbol? declaringType, MethodSymbol? declaringMethod,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            if (current is not TypeSymbol definition)
            {
                if (reportErrors) Error(span, $"'{last.name}' is not a generic type");
                return unit.Symbols.ErrorType;
            }
            var required = definition.GenericParameters.Count(p => !p.IsVariadic && !p.IsNamedVariadic);
            var variadic = definition.GenericParameters.Any(p => p.IsVariadic || p.IsNamedVariadic);
            var countOk = variadic
                ? last.generics.Count >= required
                : last.generics.Count == definition.GenericParameters.Count;
            if (definition.GenericParameters.Count == 0 || !countOk)
            {
                if (reportErrors)
                {
                    Error(span, $"'{definition.Name}' expects {definition.GenericParameters.Count} " +
                        $"type argument(s), got {last.generics.Count}");
                }
                return unit.Symbols.ErrorType;
            }
            var args = new SemanticSymbol[last.generics.Count];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = ResolveSymbolPath(last.generics[i], ctx, declaringType, declaringMethod,
                    allowImports, reportErrors, span);
            }
            // 实参毒化传播（实参自身的诊断已报，此处静默）
            if (args.Any(a => a is ErrorTypeSymbol)) return unit.Symbols.ErrorType;
            return unit.Symbols.GetConstructedType(definition, args);
        }

        // 诊断消息中的路径原文（a.b.C）
        public static string PathText(Symbol path)
        {
            return string.Join(".", path.elements.Select(e => e.name));
        }
    }
}
