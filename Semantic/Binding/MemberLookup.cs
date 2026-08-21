namespace RigiCompiler
{
    // 名字解析查找序设施（自旧 BindSession 迁移，行为不变）：
    // 值/调用的名字解析查找序——块作用域链（Scope，调用方查）→ 参数 →
    // 宿主类型成员（method.Owner 沿 BaseType 链；ext 方法 Owner = 目标类型）
    // → 命名空间链（文件命名空间及父链，顶端即全局命名空间）→ 具名 import
    // 顶层函数/全局字段（S4）→ 通配 import 容器成员。路径簇与 typeCheck 簇共用。
    internal static class MemberLookup
    {
        // 多段路径的前 N-1 段解析为容器（命名空间/类型）：
        // 段名序列还原为语法侧 Symbol 喂 NameResolver（保持 P2 同款诊断消息）；
        // reportErrors: false 为不落袋试探（typeCheck 动态形态等场景），
        // 静默时调用方传 span: null。
        // segmentGenerics（g7）：与 segments 下标对齐的各段泛型实参
        //（通常仅头段非 null，`Box\<i32>.wrap` 的 i32）——随段名一起还原，
        // NameResolver 对容器路径末段应用实参后可解析出构造类型
        // Box\<i32\>；null = 全部段无实参（旧行为）
        public static SemanticSymbol? ResolveContainer(IReadOnlyList<string> segments,
            CharRange? span, BindFunctionFrame frame, BindEnvironment env,
            bool reportErrors = true,
            IReadOnlyList<IReadOnlyList<TypeReferenceASTNode>?>? segmentGenerics = null,
            bool allowBareGenericDefinition = false)
        {
            var head = new Symbol();
            for (int i = 0; i < segments.Count - 1; i++)
            {
                var element = new SymbolElement { name = segments[i] };
                if (segmentGenerics != null && i < segmentGenerics.Count
                    && segmentGenerics[i] != null)
                {
                    element.generics.AddRange(segmentGenerics[i]!);
                }
                head.elements.Add(element);
            }
            // allowBareGenericDefinition：静态成员裸名容器（`Box.count()`）
            // 必须解析到泛型定义本身，不得报元数错误（SYNTAX §9.2.3）
            var container = env.Names.ResolveSymbolPath(head, frame.FileCtx, frame.DeclaringType,
                frame.Method, allowImports: true, reportErrors: reportErrors, span: span,
                allowBareGenericDefinition: allowBareGenericDefinition);
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
                // 构造类型的成员表在其泛型定义上（g7：Box\<i32\> 容器
                // 回退 Box 定义查成员，与 FindMethods/FindField 同口径）
                TypeSymbol t => FindMemberInTypeDefinition(t.ConstructedFrom ?? t, name),
                _ => null,
            };
        }

        private static SemanticSymbol? FindMemberInTypeDefinition(TypeSymbol definition,
            string name)
        {
            return (SemanticSymbol?)definition.Fields.FirstOrDefault(f => f.Name == name)
                ?? (SemanticSymbol?)definition.Methods.FirstOrDefault(m => m.Name == name)
                ?? definition.NestedTypes.FirstOrDefault(n => n.Name == name);
        }

        // 字段查找序：宿主类型成员（S7c-2 落地——method.Owner 沿
        // BaseType 链，ext 方法 Owner = 目标类型；实例字段命中后由
        // 字段引用补 this，与 FindMethods 的宿主优先一致）
        // → 命名空间链 → 具名 import 全局字段（S4）→ 通配 import 容器字段
        // （具名命中时通配不再覆盖，与 NameResolver 具名优先口径一致）
        public static FieldSymbol? FindField(string name, BindFunctionFrame frame,
            BindEnvironment env, CharRange? span = null)
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
            // 具名 import（S4）：双容器同名有效命中走 Ambiguous import 口径
            //（诊断落袋后取先者继续绑定，避免次生噪音）
            FieldSymbol? namedField = null;
            NamespaceSymbol? namedSource = null;
            foreach (var ns in NamedImportNamespaces(name, frame, env))
            {
                var hit = ns.Fields.FirstOrDefault(f => f.Name == name);
                if (hit == null) continue;
                if (namedField != null && !ReferenceEquals(namedSource, ns))
                {
                    env.Error(span, $"Ambiguous import: '{name}'");
                    break;
                }
                namedField = hit;
                namedSource = ns;
            }
            if (namedField != null) return namedField;
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
        // 由被调用方绑定补 this 或静态性检查拦截）→ 接口默认实现
        // （SYNTAX §11 隐式继承，裸名 greet() 与 d.greet() 同口径）
        // → 命名空间链 → 通配 import 容器方法。
        // 实例 Regular/Operator 复用 FindInstanceMethods 的 override 遮蔽口径
        // （bug O4：override 链算单一最派生槽，基类被 override 的方法不进池，
        // 裸名 show() 与 this.show() 同口径）；static 与 init/访问器等
        // 非实例查找覆盖的种类仍沿 BaseType 链无遮蔽收集（保留现状能力）。
        // S4：具名 import 顶层函数入池（同名重载全部——导入的是名字不是
        // 签名）；具名命中时通配不再覆盖（先者胜，与 NameResolver 同口径）；
        // 双容器同名有效命中报 Ambiguous import（诊断后取先者继续绑定）
        public static List<MethodSymbol> FindMethods(string name, BindFunctionFrame frame,
            BindEnvironment env, CharRange? span = null)
        {
            var result = new List<MethodSymbol>();
            if (frame.LookupHost != null)
            {
                result.AddRange(SymbolLookup.FindInstanceMethods(
                    frame.LookupHost, name, env.Unit.Symbols));
                for (var host = frame.LookupHost; host != null; host = host.BaseType)
                {
                    var owner = host.ConstructedFrom ?? host;
                    foreach (var method in owner.Methods.Where(m => m.Name == name
                        && (m.IsStatic
                            || (m.Kind != MethodKind.Regular && m.Kind != MethodKind.Operator))))
                    {
                        if (!result.Any(existing => ReferenceEquals(existing, method)))
                        {
                            result.Add(method);
                        }
                    }
                }
            }
            for (var ns = frame.FileCtx.Namespace; ns != null; ns = ns.Parent)
            {
                result.AddRange(ns.Methods.Where(m => m.Name == name));
            }
            List<MethodSymbol>? namedMethods = null;
            NamespaceSymbol? namedSource = null;
            foreach (var ns in NamedImportNamespaces(name, frame, env))
            {
                var group = ns.Methods.Where(m => m.Name == name).ToList();
                if (group.Count == 0) continue;
                if (namedMethods != null && !ReferenceEquals(namedSource, ns))
                {
                    env.Error(span, $"Ambiguous import: '{name}'");
                    break;
                }
                namedMethods = group;
                namedSource = ns;
            }
            if (namedMethods != null)
            {
                result.AddRange(namedMethods);
                return result;
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

        // 具名 import 的命名空间容器（S4）：末段同名（导入目标即该名字）的
        // 具名 import 逐条解析前 N-1 段容器路径，仅命名空间容器产出（类型
        // 容器不导静态成员——§15.2 具名导入范围是顶层函数/全局字段/类型）。
        // 单段具名导入无容器段（目标名经命名空间链已可见，无需贡献）；
        // 容器解析静默失败跳过（ImportValidator 已统一诊断）
        private static IEnumerable<NamespaceSymbol> NamedImportNamespaces(string name,
            BindFunctionFrame frame, BindEnvironment env)
        {
            foreach (var item in frame.FileCtx.Imports)
            {
                if (item.importAll) continue;
                var elements = item.symbolNode.symbol.elements;
                if (elements.Count < 2 || elements[^1].name != name) continue;
                var head = new Symbol();
                for (int i = 0; i < elements.Count - 1; i++)
                {
                    head.elements.Add(new SymbolElement { name = elements[i].name });
                }
                var container = env.Names.ResolveSymbolPath(head, frame.FileCtx,
                    declaringType: null, declaringMethod: null,
                    allowImports: false, reportErrors: false, span: null,
                    allowBareGenericDefinition: true);
                if (container is NamespaceSymbol ns) yield return ns;
            }
        }

        // 通配 import 的容器（具名 import 的贡献见 NamedImportNamespaces——
        // S4 起顶层函数/全局字段亦入值/函数查找）；import 路径解析静默
        //（P2 已统一诊断）
        public static IEnumerable<SemanticSymbol> WildcardImportContainers(BindFunctionFrame frame,
            BindEnvironment env)
        {
            foreach (var item in frame.FileCtx.Imports)
            {
                if (!item.importAll) continue;
                var container = env.Names.ResolveSymbolPath(item.symbolNode.symbol, frame.FileCtx,
                    declaringType: null, declaringMethod: null,
                    allowImports: false, reportErrors: false, span: null,
                    allowBareGenericDefinition: true);
                if (container is not ErrorTypeSymbol) yield return container;
            }
        }
    }
}
