using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RigiCompiler
{
    // 派发链诊断报告（S11f / RUNTIME §15；M88：烘焙归 Middleware——
    // frontend 报告应用登记 × proxy 形状匹配预览 + 降级资格，不读合成链槽）。
    // CLI `compile --explain-dispatch` 与测试套件共用。
    public static class DispatchExplainer
    {
        public static string Explain(CompilationUnit unit)
        {
            var hosts = CollectHostTypes(unit.Symbols.GlobalNamespace)
                .Where(t => t.AppliedWrappers.Count > 0)
                .OrderBy(t => CanonicalSymbolPrinter.PrintType(t), StringComparer.Ordinal)
                .ToList();
            if (hosts.Count == 0) return "(no dispatch chains)\n";

            var sb = new StringBuilder();
            foreach (var host in hosts)
            {
                sb.Append("type ").Append(CanonicalSymbolPrinter.PrintType(host)).Append('\n');
                sb.Append("  applied:");
                foreach (var app in host.AppliedWrappers)
                {
                    sb.Append(' ').Append(FormatApplication(app));
                }
                sb.Append('\n');
                foreach (var member in EnumerateMembers(host))
                {
                    var (specificName, wildcardName) = ProxyNamesOf(member);
                    sb.Append("  member ").Append(CanonicalSymbolPrinter.PrintMethod(member))
                        .Append('\n');
                    for (var i = 0; i < host.AppliedWrappers.Count; i++)
                    {
                        var app = host.AppliedWrappers[i];
                        var (kind, proxy) = MatchPreview(app, specificName, wildcardName);
                        sb.Append("    [").Append(i).Append("] ").Append(kind);
                        if (proxy != null)
                        {
                            sb.Append(' ').Append(CanonicalSymbolPrinter.PrintMethod(proxy));
                        }
                        sb.Append('\n');
                    }
                }
                // 降级资格：wrapper 链含 `.proxy.*` → 报告 invoke core::Any$call???
                if (ProxyMatching.HasMethodWildcardProxy(host))
                {
                    sb.Append("  downgrade\n");
                    sb.Append("    invoke core::Any$call???\n");
                }
            }
            return sb.ToString();
        }

        // 名命中预览（specific 优先 / wildcard 回退）；形状全等细节归 P2
        // ProxyMatchChecker——explainer 只报将命中的 proxy 模板声明
        private static (string Kind, MethodSymbol? Proxy) MatchPreview(
            WrapperApplication app, string specificName, string wildcardName)
        {
            var wrapperDef = app.WrapperDefinition;
            if (ProxyMatching.HasInvalidGenericArity(wrapperDef)) return ("inert", null);
            var specific = wrapperDef.Methods.FirstOrDefault(m => m.Name == specificName);
            if (specific != null && !ProxyMatching.ContainsErrorType(specific))
            {
                return ("specific", specific);
            }
            var wildcard = wrapperDef.Methods.FirstOrDefault(m => m.Name == wildcardName);
            if (wildcard != null && !ProxyMatching.ContainsErrorType(wildcard))
            {
                return ("wildcard", wildcard);
            }
            return ("inert", null);
        }

        private static (string Specific, string Wildcard) ProxyNamesOf(MethodSymbol member)
        {
            if (member.Kind == MethodKind.Getter)
            {
                return (".proxy.get." + member.Name, ".proxy.get.*");
            }
            if (member.Kind == MethodKind.Setter)
            {
                return (".proxy.set." + member.Name, ".proxy.set.*");
            }
            if (member.Kind == MethodKind.Operator)
            {
                return (".proxy.opr." + member.Name, ".proxy.opr.*");
            }
            return (".proxy." + member.Name, ".proxy.*");
        }

        private static IEnumerable<MethodSymbol> EnumerateMembers(TypeSymbol host)
        {
            var members = new List<MethodSymbol>();
            // §14.2：子类 wrapper 拦截继承成员，报告须覆盖 BaseType 链
            foreach (var m in ProxyMatchChecker.EnumerateOwnAndInheritedMethods(host))
            {
                members.Add(m);
            }
            foreach (var f in ProxyMatchChecker.EnumerateOwnAndInheritedFields(host))
            {
                if (f.Getter != null) members.Add(f.Getter);
                if (f.Setter != null) members.Add(f.Setter);
            }
            return members.OrderBy(m => CanonicalSymbolPrinter.PrintMethod(m), StringComparer.Ordinal);
        }

        private static string FormatApplication(WrapperApplication app)
        {
            var def = app.WrapperDefinition;
            return def.Name + "@" + CanonicalSymbolPrinter.PrintType(app.Wrapper);
        }

        private static IEnumerable<TypeSymbol> CollectHostTypes(NamespaceSymbol ns)
        {
            foreach (var type in ns.Types)
            {
                foreach (var t in CollectTypeTree(type)) yield return t;
            }
            foreach (var child in ns.ChildNamespaces)
            {
                foreach (var t in CollectHostTypes(child)) yield return t;
            }
        }

        private static IEnumerable<TypeSymbol> CollectTypeTree(TypeSymbol type)
        {
            if (!type.IsBuiltin && type.ConstructedFrom == null) yield return type;
            foreach (var nested in type.NestedTypes)
            {
                foreach (var t in CollectTypeTree(nested)) yield return t;
            }
        }
    }
}
