using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LatteCompiler
{
    // 派发链诊断报告（S11f / RUNTIME §15）：从编译单元符号图读取 S11a/S11e
    // 烘焙产物，按类型分组输出 outer→inner 链与降级路由。CLI
    // `compile --explain-dispatch` 与测试套件共用；调用点级过滤为预留扩展。
    public static class DispatchExplainer
    {
        // 生成完整报告文本（末尾换行；无产物时单行明示）
        public static string Explain(CompilationUnit unit)
        {
            var hosts = CollectHostTypes(unit.Symbols.GlobalNamespace)
                .Where(HasBakingProducts)
                .OrderBy(t => CanonicalSymbolPrinter.PrintType(t), StringComparer.Ordinal)
                .ToList();
            if (hosts.Count == 0) return "(no dispatch chains)\n";

            var sb = new StringBuilder();
            foreach (var host in hosts)
            {
                sb.Append("type ").Append(CanonicalSymbolPrinter.PrintType(host)).Append('\n');
                if (host.AppliedWrappers.Count > 0)
                {
                    sb.Append("  applied:");
                    foreach (var app in host.AppliedWrappers)
                    {
                        sb.Append(' ').Append(FormatApplication(app));
                    }
                    sb.Append('\n');
                }
                foreach (var member in EnumerateChainedMembers(host))
                {
                    sb.Append("  member ").Append(CanonicalSymbolPrinter.PrintMethod(member)).Append('\n');
                    var chain = member.WrapperChain!;
                    for (var i = 0; i < chain.Count; i++)
                    {
                        var link = chain[i];
                        var info = link.ProxySpecialization!;
                        sb.Append("    [").Append(i).Append("] ")
                            .Append(FormatLinkKind(info.Kind)).Append(' ')
                            .Append(info.ProxyDeclaration.Name).Append(' ')
                            .Append(CanonicalSymbolPrinter.PrintMethod(link)).Append('\n');
                    }
                    sb.Append("    wrapped ")
                        .Append(CanonicalSymbolPrinter.PrintMethod(member.WrappedBodySymbol!))
                        .Append('\n');
                }
                if (host.DowngradeRouter is { } router)
                {
                    sb.Append("  downgrade\n");
                    sb.Append("    router ").Append(CanonicalSymbolPrinter.PrintMethod(router)).Append('\n');
                    var dchain = host.DowngradeChain ?? (IReadOnlyList<MethodSymbol>)Array.Empty<MethodSymbol>();
                    for (var i = 0; i < dchain.Count; i++)
                    {
                        sb.Append("    [").Append(i).Append("] ")
                            .Append(CanonicalSymbolPrinter.PrintMethod(dchain[i])).Append('\n');
                    }
                    // 链末恒为 Any.call???（OriginalBody 槽，全单元共享）
                    var end = dchain.Count > 0
                        ? dchain[0].ProxySpecialization!.OriginalBody
                        : null;
                    if (end != null)
                    {
                        sb.Append("    end ").Append(CanonicalSymbolPrinter.PrintMethod(end)).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }

        private static bool HasBakingProducts(TypeSymbol type)
        {
            if (type.DowngradeRouter != null) return true;
            if (type.AppliedWrappers.Count > 0) return true;
            return type.Methods.Any(m => m.WrapperChain != null)
                || type.Fields.Any(f => f.Getter?.WrapperChain != null || f.Setter?.WrapperChain != null);
        }

        // 被拦截成员：Methods 表上的实例方法/运算符 + 访问器（Getter/Setter 不进 Methods）
        private static IEnumerable<MethodSymbol> EnumerateChainedMembers(TypeSymbol host)
        {
            var members = new List<MethodSymbol>();
            foreach (var m in host.Methods)
            {
                if (m.WrapperChain != null) members.Add(m);
            }
            foreach (var f in host.Fields)
            {
                if (f.Getter?.WrapperChain != null) members.Add(f.Getter);
                if (f.Setter?.WrapperChain != null) members.Add(f.Setter);
            }
            return members.OrderBy(m => CanonicalSymbolPrinter.PrintMethod(m), StringComparer.Ordinal);
        }

        private static string FormatApplication(WrapperApplication app)
        {
            // 定义名@应用类型（含 TTarget 代入后的构造）
            var def = app.WrapperDefinition;
            return def.Name + "@" + CanonicalSymbolPrinter.PrintType(app.Wrapper);
        }

        private static string FormatLinkKind(ProxyLinkKind kind) => kind switch
        {
            ProxyLinkKind.Specific => "specific",
            ProxyLinkKind.Wildcard => "wildcard",
            _ => kind.ToString().ToLowerInvariant(),
        };

        // 命名空间树 + 嵌套类型深度优先收集（跳过内建与构造实例）
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
