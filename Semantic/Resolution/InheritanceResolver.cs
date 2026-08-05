namespace LatteCompiler
{
    // ===== 子任务 2：继承 / implements 图 + 循环继承 =====
    //
    // 自旧 DeclarationResolver.ResolveSession.ResolveInheritance/ResolveBaseClass/
    // ResolveBaseStruct/ResolveInterfaces/CreatesCycle/HasInterfaceCycle 迁移，行为不变。
    internal sealed class InheritanceResolver : ResolverVisitor<InheritanceResolver>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                switch (entry.Node)
                {
                    case ClassDeclarationASTNode c:
                        if (c.BaseClass != null)
                        {
                            ResolveBaseClass(type, c.BaseClass, entry, env);
                        }
                        ResolveInterfaces(type, c.Interfaces, entry, "class", env);
                        break;
                    case StructDeclarationASTNode s:
                        if (s.BaseStruct != null)
                        {
                            ResolveBaseStruct(type, s.BaseStruct, entry, env);
                        }
                        // §10：struct 只能继承 struct，不能实现接口
                        if (s.Interfaces.Count > 0)
                        {
                            env.Error(s.Interfaces[0].Span ?? entry.Node.Span,
                                $"'{type.Name}': structs cannot implement interfaces");
                        }
                        break;
                    case InterfaceDeclarationASTNode i:
                        ResolveInterfaces(type, i.BaseInterfaces, entry, "interface", env);
                        break;
                    // enum struct / wrapper：固定继承链，无源码基类语法
                }
            }
        }

        private static void ResolveBaseClass(TypeSymbol type, TypeReferenceASTNode baseRef,
            DeclEntry entry, ResolveEnvironment env)
        {
            // 宿主上下文是类型自身（顶层类的 entry.DeclaringType 为 null——
            // 基类子句的泛型实参可引用自身泛型参数，如 `Sub\<T> : Base\<T>`）
            var resolved = env.Names.ResolveTypeReference(baseRef, entry.Context, type, null,
                baseRef.Span ?? entry.Node.Span);
            if (resolved is ErrorTypeSymbol) return;    // 毒化：保持默认基类
            if (resolved is not TypeSymbol baseType)
            {
                env.Error(baseRef.Span ?? entry.Node.Span, $"'{type.Name}': base class must be a type");
                return;
            }
            var def = baseType.ConstructedFrom ?? baseType;
            if (def.Kind != TypeKind.Class)
            {
                env.Error(baseRef.Span ?? entry.Node.Span,
                    $"'{type.Name}': a class can only inherit from a class (use 'implements' for interfaces)");
                return;
            }
            // 声明侧访问控制（§16，S8e）：基类引用即使用点
            if (!AccessChecker.IsAccessible(def, entry.Context.File, entry.Context.Namespace,
                entry.DeclaringType))
            {
                env.Error(baseRef.Span ?? entry.Node.Span, AccessChecker.InaccessibleMessage(def));
                return;
            }
            // 可继承性：基类必须 open/abstract；内建 Object 天然可继承
            if (!def.IsBuiltin && !def.IsOpen && !def.IsAbstract)
            {
                env.Error(baseRef.Span ?? entry.Node.Span,
                    $"'{type.Name}': base class '{def.Name}' is not open or abstract");
                return;
            }
            if (CreatesCycle(type, baseType))
            {
                env.Error(baseRef.Span ?? entry.Node.Span,
                    $"Circular inheritance involving '{type.Name}'");
                return;
            }
            type.BaseType = baseType;
        }

        private static void ResolveBaseStruct(TypeSymbol type, TypeReferenceASTNode baseRef,
            DeclEntry entry, ResolveEnvironment env)
        {
            // 宿主上下文是类型自身（同 ResolveBaseClass——基类子句可引用
            // 自身泛型参数）
            var resolved = env.Names.ResolveTypeReference(baseRef, entry.Context, type, null,
                baseRef.Span ?? entry.Node.Span);
            if (resolved is ErrorTypeSymbol) return;
            if (resolved is not TypeSymbol baseType)
            {
                env.Error(baseRef.Span ?? entry.Node.Span, $"'{type.Name}': base struct must be a type");
                return;
            }
            var def = baseType.ConstructedFrom ?? baseType;
            // §10：struct 只能继承 struct；可被继承的 struct 必须是 open 的 rich struct
            if (def.Kind != TypeKind.Struct)
            {
                env.Error(baseRef.Span ?? entry.Node.Span,
                    $"'{type.Name}': a struct can only inherit from a struct");
                return;
            }
            // 声明侧访问控制（§16，S8e）：基 struct 引用即使用点
            if (!AccessChecker.IsAccessible(def, entry.Context.File, entry.Context.Namespace,
                entry.DeclaringType))
            {
                env.Error(baseRef.Span ?? entry.Node.Span, AccessChecker.InaccessibleMessage(def));
                return;
            }
            if (!def.IsBuiltin && !(def.IsRich && def.IsOpen))
            {
                env.Error(baseRef.Span ?? entry.Node.Span,
                    $"'{type.Name}': base struct '{def.Name}' must be an open rich struct");
                return;
            }
            if (CreatesCycle(type, baseType))
            {
                env.Error(baseRef.Span ?? entry.Node.Span,
                    $"Circular inheritance involving '{type.Name}'");
                return;
            }
            type.BaseType = baseType;
        }

        private static void ResolveInterfaces(TypeSymbol type, List<TypeReferenceASTNode> interfaces,
            DeclEntry entry, string keyword, ResolveEnvironment env)
        {
            foreach (var ifaceRef in interfaces)
            {
                // 宿主上下文是类型自身（同 ResolveBaseClass——implements
                // 子句可引用自身泛型参数，如 `class C\<T> : I\<T>`）
                var resolved = env.Names.ResolveTypeReference(ifaceRef, entry.Context, type, null,
                    ifaceRef.Span ?? entry.Node.Span);
                if (resolved is ErrorTypeSymbol) continue;
                if (resolved is not TypeSymbol iface ||
                    (iface.ConstructedFrom ?? iface).Kind != TypeKind.Interface)
                {
                    env.Error(ifaceRef.Span ?? entry.Node.Span,
                        $"'{type.Name}': '{keyword}' target must be an interface");
                    continue;
                }
                // 声明侧访问控制（§16，S8e）：接口引用即使用点
                var ifaceDef = iface.ConstructedFrom ?? iface;
                if (!AccessChecker.IsAccessible(ifaceDef, entry.Context.File,
                    entry.Context.Namespace, entry.DeclaringType))
                {
                    env.Error(ifaceRef.Span ?? entry.Node.Span,
                        AccessChecker.InaccessibleMessage(ifaceDef));
                    continue;
                }
                type.Interfaces.Add(iface);
            }
            // interface 继承图的环：DFS 能回到自身即环（报错但保留图，
            // 后续消费 Interfaces 的遍历均为一层，不会死循环）
            if (type.Interfaces.Count > 0 && HasInterfaceCycle(type))
            {
                env.Error(entry.Node.Span, $"Circular interface inheritance involving '{type.Name}'");
            }
        }

        private static bool CreatesCycle(TypeSymbol type, TypeSymbol baseType)
        {
            for (var t = baseType; t != null; t = t.BaseType)
            {
                if (ReferenceEquals(t, type)) return true;
            }
            return false;
        }

        private static bool HasInterfaceCycle(TypeSymbol type)
        {
            var visited = new HashSet<TypeSymbol>();
            var stack = new Stack<TypeSymbol>(type.Interfaces);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (ReferenceEquals(current, type)) return true;
                if (!visited.Add(current)) continue;
                foreach (var next in current.Interfaces)
                {
                    stack.Push(next);
                }
            }
            return false;
        }
    }
}
