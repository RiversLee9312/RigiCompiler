using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler
{
    // 符号路径与类型引用解析（P2 声明骨架与 P3 函数体共用）。
    // 查找序：泛型参数（方法 → 合成 Owner 外层方法共享 → 宿主类型链）→
    //   宿主类型链 NestedTypes →
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

        // 类型引用解析：T? 即构造类型 Nullable\<T>（SYNTAX §3.4）。
        // 落袋前拦截非类型符号：路径解析可命中命名空间（如 `func m(): collections`），
        // 命名空间不是类型——诊断并毒化（统一在此拦截，各调用方静默消化 ErrorType）
        public SemanticSymbol ResolveTypeReference(TypeReferenceASTNode typeRef, FileContext ctx,
            TypeSymbol? declaringType, MethodSymbol? declaringMethod, CharRange? span)
        {
            var resolved = ResolveSymbolPath(typeRef.TypeSymbol.symbol, ctx,
                declaringType, declaringMethod, allowImports: true,
                reportErrors: true, span: span ?? typeRef.Span);
            if (resolved is ErrorTypeSymbol) return resolved;
            if (resolved is not (TypeSymbol or GenericParameterSymbol))
            {
                Error(span ?? typeRef.Span,
                    $"'{PathText(typeRef.TypeSymbol.symbol)}' is not a type");
                return unit.Symbols.ErrorType;
            }
            if (typeRef.IsNullable)
            {
                return unit.Symbols.GetConstructedType(unit.Symbols.Bootstrap.NullableDefinition, resolved);
            }
            return resolved;
        }

        // 符号路径解析：首段按查找序定位，后续逐段下钻，末段应用泛型实参。
        // allowBareGenericDefinition：裸名命中泛型定义时返回定义本身而非
        // 元数错误。用于 import 验证/消费（导入目标即定义，实参在使用处
        // 书写）以及 S11a wrapper 注解（TTarget 代入与元数校验归 P2 proxy）
        public SemanticSymbol ResolveSymbolPath(Symbol path, FileContext ctx,
            TypeSymbol? declaringType, MethodSymbol? declaringMethod,
            bool allowImports, bool reportErrors, CharRange? span,
            bool allowBareGenericDefinition = false)
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
                elements.Count == 1 ? elements[0].generics.Count : -1, span, out var importResolved);
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
            // 裸名（arity 0）回退命中带泛型参数的定义：未构造的泛型定义不能
            // 直接作类型——按 ApplyTypeArguments 同口径报元数错误并毒化
            // （wrapper 注解路径放行，S11a：TTarget 代入与元数归 proxy 阶段）
            if (!allowBareGenericDefinition
                && current is TypeSymbol { ConstructedFrom: null } genericDef
                && genericDef.GenericParameters.Count > 0)
            {
                if (reportErrors)
                {
                    Error(span, $"'{genericDef.Name}' expects {genericDef.GenericParameters.Count} " +
                        "type argument(s), got 0");
                }
                return unit.Symbols.ErrorType;
            }
            return current;
        }

        // ext 目标路径（字符串段，SYNTAX §4.4 原文无泛型）走同一查找序。
        // 收口：裸名命中泛型定义报元数错误；同名不同元数多命中报歧义
        // （不改共享 FindTypeIn，以免影响类型引用等其它路径）
        public SemanticSymbol ResolveDottedPath(string[] segments, FileContext ctx,
            bool allowImports, bool reportErrors, CharRange? span)
        {
            var current = ResolveFirstSegment(segments[0], ctx, declaringType: null,
                allowImports, arity: -1, span, out var importResolved);
            if (current == null)
            {
                if (reportErrors && !importResolved)
                {
                    Error(span, $"Unresolved extension target: '{string.Join(".", segments)}'");
                }
                return unit.Symbols.ErrorType;
            }
            if (RejectBareGenericExtTarget(current, reportErrors, span))
            {
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
                if (RejectBareGenericExtTarget(current, reportErrors, span))
                {
                    return unit.Symbols.ErrorType;
                }
            }
            return current;
        }

        // ext 目标段：同名不同元数歧义优先于裸名泛型元数诊断；诊断受
        // reportErrors 控制，毒化始终生效（NativeDeclarationChecker 静默路径契约）
        private bool RejectBareGenericExtTarget(SemanticSymbol symbol, bool reportErrors,
            CharRange? span)
        {
            if (symbol is not TypeSymbol type) return false;
            if (HasAmbiguousAritySiblings(type))
            {
                if (reportErrors)
                {
                    Error(span, $"Ambiguous extension target: '{type.Name}'");
                }
                return true;
            }
            if (type is { ConstructedFrom: null } && type.GenericParameters.Count > 0)
            {
                if (reportErrors)
                {
                    Error(span, $"'{type.Name}' expects {type.GenericParameters.Count} " +
                        "type argument(s), got 0");
                }
                return true;
            }
            return false;
        }

        // 同容器内同名不同类型元数共存（S10）时 ext 裸名无法消歧
        private static bool HasAmbiguousAritySiblings(TypeSymbol type)
        {
            IReadOnlyList<TypeSymbol>? siblings = type.DeclaringType != null
                ? type.DeclaringType.NestedTypes
                : type.Namespace?.Types;
            if (siblings == null) return false;
            return siblings.Count(t => t.Name == type.Name) > 1;
        }

        // 首段查找序：宿主类型链 NestedTypes → 文件命名空间及父链 → 全局命名空间
        // → import 列表（具名末段同名 / 通配容器内查）→ core 命名空间（隐式）。
        // importResolved：具名 import 同名条目全部失效（ImportValidator 已诊断），
        // 或双有效歧义（此处已诊断）时为 true——调用方静默毒化，不再报「未解析」。
        // arity：该段（单段路径即末段）的泛型实参个数；-1 = 容器下钻不筛元数。
        private SemanticSymbol? ResolveFirstSegment(string name, FileContext ctx,
            TypeSymbol? declaringType, bool allowImports, int arity, CharRange? span,
            out bool importResolved)
        {
            importResolved = false;
            for (var t = declaringType; t != null; t = t.DeclaringType)
            {
                var nested = FindTypeIn(t.NestedTypes, name, arity);
                if (nested != null) return nested;
            }
            // 文件命名空间及父链（链尾即全局命名空间，无需再显式查一次）
            for (var ns = ctx.Namespace; ns != null; ns = ns.Parent)
            {
                var hit = FindInNamespace(ns, name, arity);
                if (hit != null) return hit;
            }
            if (allowImports)
            {
                // 具名 import 同名多条目（`import a.Foo` + `import b.Foo`）：
                // 失效条目（自身解析失败，ImportValidator 已统一诊断）跳过继续
                // 查找——不再「先者胜」毒化有效者；两条都有效时无优先级可依，
                // 报歧义诊断（同一路径重复 import 解析结果引用相等，豁免）。
                // 具名命中已记录时通配条目不再覆盖（先者胜）
                SemanticSymbol? namedHit = null;
                foreach (var item in ctx.Imports)
                {
                    var importPath = item.symbolNode.symbol;
                    if (item.importAll)
                    {
                        if (namedHit != null) continue;
                        var container = ResolveSymbolPath(importPath, ctx, declaringType: null,
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null,
                            allowBareGenericDefinition: true);
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
                            declaringMethod: null, allowImports: false, reportErrors: false, span: null,
                            allowBareGenericDefinition: true);
                        if (resolved is ErrorTypeSymbol)
                        {
                            // 失效条目跳过；全部同名条目均失效时保持 true，
                            // 调用方静默毒化（ImportValidator 已报 Unresolved import）
                            importResolved = true;
                            continue;
                        }
                        if (namedHit != null && !ReferenceEquals(namedHit, resolved))
                        {
                            Error(span, $"Ambiguous import: '{name}'");
                            importResolved = true;
                            return null;
                        }
                        namedHit = resolved;
                    }
                }
                if (namedHit != null) return namedHit;
            }
            return FindInNamespace(unit.Symbols.Bootstrap.Core, name, arity);
        }

        // 按「名 + 期望元数」在类型表中查找（S10，SYNTAX §15.3：同名不同
        // 元数合法共存）：arity >= 0 时优先精确元数匹配，回退同名任意声明
        // （带实参但元数不匹配者落入 ApplyTypeArguments 的元数诊断；裸名
        // 回退命中泛型定义者由 ResolveSymbolPath 末段统一报元数错误）；
        // arity < 0 不筛。
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

        // 中间段下钻优先级与首段 FindInNamespace 一致：Types 优先于 ChildNamespaces
        private static SemanticSymbol? Descend(SemanticSymbol current, string name, int arity) => current switch
        {
            NamespaceSymbol ns => (SemanticSymbol?)FindTypeIn(ns.Types, name, arity)
                ?? ns.ChildNamespaces.FirstOrDefault(n => n.Name == name),
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
                // M112：合成类（lambda 隐藏类 / cell 子类）把外层方法泛型参数
                // 以同符号对象挂在 Owner.GenericParameters（M103 类型级共享）。
                // $$call/getValue 等合成方法自身无 GenericParameters，须从 Owner
                // 补齐「相对 DeclaringType 链多出来的」条目——方法级遮蔽类型级
                // （与源方法体查找序一致：method GP → type chain GP）
                if (declaringMethod.Owner != null)
                {
                    var typeChain = new HashSet<GenericParameterSymbol>();
                    for (var t = declaringType; t != null; t = t.DeclaringType)
                    {
                        foreach (var parameter in t.GenericParameters) typeChain.Add(parameter);
                    }
                    hit = declaringMethod.Owner.GenericParameters.FirstOrDefault(p =>
                        p.Name == name && !typeChain.Contains(p));
                    if (hit != null) return hit;
                }
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
