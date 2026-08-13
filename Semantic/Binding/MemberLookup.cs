namespace RigiCompiler
{
    // 名字解析查找序设施（自旧 BindSession 迁移，行为不变）：
    // 值/调用的名字解析查找序——块作用域链（Scope，调用方查）→ 参数 →
    // 宿主类型成员（method.Owner 沿 BaseType 链；ext 方法 Owner = 目标类型）
    // → 命名空间链（文件命名空间及父链，顶端即全局命名空间）→ 通配 import
    // 容器成员。路径簇与 typeCheck 簇共用。
    internal static class MemberLookup
    {
        // 多段路径的前 N-1 段解析为容器（命名空间/类型）：
        // 段名序列还原为语法侧 Symbol 喂 NameResolver（保持 P2 同款诊断消息）；
        // reportErrors: false 为不落袋试探（typeCheck 动态形态等场景），
        // 静默时调用方传 span: null
        public static SemanticSymbol? ResolveContainer(IReadOnlyList<string> segments,
            CharRange? span, BindFunctionFrame frame, BindEnvironment env,
            bool reportErrors = true)
        {
            var head = new Symbol();
            for (int i = 0; i < segments.Count - 1; i++)
            {
                head.elements.Add(new SymbolElement { name = segments[i] });
            }
            var container = env.Names.ResolveSymbolPath(head, frame.FileCtx, frame.DeclaringType,
                frame.Method, allowImports: true, reportErrors: reportErrors, span: span);
            return container is ErrorTypeSymbol ? null : container;
        }

        public static SemanticSymbol? FindMember(SemanticSymbol container, string name)
        {
            return container switch
            {
                NamespaceSymbol ns => (SemanticSymbol?)ns.Fields.FirstOrDefault(f => f.Name == name)
                    ?? ns.Methods.FirstOrDefault(m => m.Name == name)
                    ?? (SemanticSymbol?)ns.Types.FirstOrDefault(t => t.Name == name)
                    ?? ns.ChildNamespaces.FirstOrDefault(n => n.Name == name),
                TypeSymbol t => (SemanticSymbol?)t.Fields.FirstOrDefault(f => f.Name == name)
                    ?? (SemanticSymbol?)t.Methods.FirstOrDefault(m => m.Name == name)
                    ?? t.NestedTypes.FirstOrDefault(n => n.Name == name),
                _ => null,
            };
        }

        // 字段查找序：宿主类型成员（S7c-2 落地——method.Owner 沿
        // BaseType 链，ext 方法 Owner = 目标类型；实例字段命中后由
        // 字段引用补 this，与 FindMethods 的宿主优先一致）
        // → 命名空间链 → 通配 import 容器字段
        public static FieldSymbol? FindField(string name, BindFunctionFrame frame,
            BindEnvironment env)
        {
            for (var host = frame.LookupHost; host != null; host = host.BaseType)
            {
                // 构造类型的成员表在其泛型定义上（S7f ConstructedFrom 回退）
                var owner = host.ConstructedFrom ?? host;
                var hostHit = owner.Fields.FirstOrDefault(f => f.Name == name);
                if (hostHit != null) return hostHit;
            }
            for (var ns = frame.FileCtx.Namespace; ns != null; ns = ns.Parent)
            {
                var hit = ns.Fields.FirstOrDefault(f => f.Name == name);
                if (hit != null) return hit;
            }
            foreach (var container in WildcardImportContainers(frame, env))
            {
                if (container is NamespaceSymbol ns)
                {
                    var hit = ns.Fields.FirstOrDefault(f => f.Name == name);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        // 方法查找序：宿主类型成员（method.Owner 沿 BaseType 链——ext
        // 方法 Owner = 目标类型，先于命名空间全局函数；实例方法命中后
        // 由被调用方绑定补 this 或静态性检查拦截）→ 命名空间链 →
        // 通配 import 容器方法
        public static List<MethodSymbol> FindMethods(string name, BindFunctionFrame frame,
            BindEnvironment env)
        {
            var result = new List<MethodSymbol>();
            for (var host = frame.LookupHost; host != null; host = host.BaseType)
            {
                var owner = host.ConstructedFrom ?? host;
                result.AddRange(owner.Methods.Where(m => m.Name == name));
            }
            for (var ns = frame.FileCtx.Namespace; ns != null; ns = ns.Parent)
            {
                result.AddRange(ns.Methods.Where(m => m.Name == name));
            }
            foreach (var container in WildcardImportContainers(frame, env))
            {
                if (container is NamespaceSymbol ns)
                {
                    result.AddRange(ns.Methods.Where(m => m.Name == name));
                }
            }
            return result;
        }

        // 通配 import 的容器（具名 import 经 P2 语义只导类型/命名空间，
        // 对值/函数查找无贡献）；import 路径解析静默（P2 已统一诊断）
        public static IEnumerable<SemanticSymbol> WildcardImportContainers(BindFunctionFrame frame,
            BindEnvironment env)
        {
            foreach (var item in frame.FileCtx.Imports)
            {
                if (!item.importAll) continue;
                var container = env.Names.ResolveSymbolPath(item.symbolNode.symbol, frame.FileCtx,
                    declaringType: null, declaringMethod: null,
                    allowImports: false, reportErrors: false, span: null);
                if (container is not ErrorTypeSymbol) yield return container;
            }
        }
    }
}
