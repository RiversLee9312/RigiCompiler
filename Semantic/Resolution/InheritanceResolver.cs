namespace RigiCompiler
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
            // 继承可见性单调性（§16.1，F2/V5）：基类有效可见性不得低于
            // 派生类型——报错但保留解析（基类本身合法，符号图照常填充，
            // 避免下游成员检查级联误诊）
            CheckBaseMonotonicity(type, baseType, "base class", "class",
                baseRef.Span ?? entry.Node.Span, env);
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
            // 基类子句是填入点（F2/V-C）：构造基类的实参须满足显式约束
            // 与实例化隐式限制（g4 框架，与类型标注同通道）
            CheckFillIn(baseType, baseRef.Span ?? entry.Node.Span, env);
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
            // 继承可见性单调性（§16.1，F2/V5）：同基类口径
            CheckBaseMonotonicity(type, baseType, "base struct", "struct",
                baseRef.Span ?? entry.Node.Span, env);
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
            // 基 struct 子句是填入点（F2/V-C，同基类口径）
            CheckFillIn(baseType, baseRef.Span ?? entry.Node.Span, env);
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
                // 继承可见性单调性（§16.1，F2/V5）：接口有效可见性不得
                // 低于派生类型（C# CS0061 式；覆盖 class implements 与
                // interface 继承两形态）
                CheckBaseMonotonicity(type, iface, "base interface", keyword,
                    ifaceRef.Span ?? entry.Node.Span, env);
                // 重复 implements 诊断（定义级判定：`I, I` 与 `I\<i32\>, I\<String\>`
                // 同定义即重复——接口契约按定义派发，构造实参不产生新的实现
                // 要求）；重复者不进 Interfaces 表（与重复声明惯例一致）
                if (type.Interfaces.Any(existing =>
                        ReferenceEquals(existing.ConstructedFrom ?? existing, ifaceDef)))
                {
                    env.Error(ifaceRef.Span ?? entry.Node.Span,
                        $"'{type.Name}': duplicate interface '{ifaceDef.Name}'");
                    continue;
                }
                type.Interfaces.Add(iface);
                // implements 子句是填入点（F2/V-C，同基类口径）
                CheckFillIn(iface, ifaceRef.Span ?? entry.Node.Span, env);
            }
            // interface 继承图的环：DFS 能回到自身即环（报错但保留图，
            // 后续消费 Interfaces 的遍历均为一层，不会死循环）
            if (type.Interfaces.Count > 0 && HasInterfaceCycle(type))
            {
                env.Error(entry.Node.Span, $"Circular interface inheritance involving '{type.Name}'");
            }
        }

        // 继承子句填入点登记（F2/V-C，SYNTAX §3.6/§3.1.1）：构造基类/
        // 接口（Cage\<i32>、SGate\<Local>）登记后由 InheritanceFillInChecker
        // 统一收口（彼时约束 Bound、字段/方法签名与 rich/shared 传染均
        // 就绪——与 TypeReferenceResolver 的 pendingFillIns 延迟同思路）
        private static void CheckFillIn(TypeSymbol baseType, CharRange? span,
            ResolveEnvironment env)
        {
            if (baseType.ConstructedFrom != null)
            {
                env.RegisterInheritanceFillIn(baseType, span);
            }
        }

        // 继承可见性单调性（F2/V5，SYNTAX §16.1，C# CS0060/CS0061 式）：
        // 基类/基接口的有效可见性（自身与嵌套宿主链逐级最小，构造实参
        // 递归——Box\<Hidden\> 的泄漏点是实参 Hidden，复审 fx_inh_mono2）
        // 不得低于派生类型——否则私有/内部类型经继承链泄漏为更可见类型
        // 的契约组成（含 like 合成转发器的来源接口，见 §9.6）。报错不
        // 拒绝：继承图照常填充，避免下游成员/覆写检查级联误诊
        private static void CheckBaseMonotonicity(TypeSymbol derived, TypeSymbol baseType,
            string baseKind, string derivedKind, CharRange? span, ResolveEnvironment env)
        {
            var derivedEffective = SignatureAccessibilityChecker.EffectiveAccessibility(
                derived.Accessibility, derived.DeclaringType);
            var hit = SignatureAccessibilityChecker.FindLessAccessible(baseType,
                derivedEffective);
            if (hit != null)
            {
                env.Error(span,
                    $"Inconsistent accessibility: {baseKind} '{hit.Name}' is less " +
                    $"accessible than {derivedKind} '{derived.Name}'");
            }
        }

        // 环判定按定义级比较：链上的 BaseType 可为构造实例（如 B\<T\> 的
        // 定义基类解析后链上是 A\<T-b\>），统一归一到 ConstructedFrom 再比
        private static bool CreatesCycle(TypeSymbol type, TypeSymbol baseType)
        {
            for (var t = baseType; t != null; t = t.BaseType)
            {
                if (ReferenceEquals(t.ConstructedFrom ?? t, type)) return true;
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

    // ===== 继承子句填入点统一收口（F2/V-C）=====
    //
    // 继承、签名和约束中的构造类型在 wrapper 应用就绪后统一检查：
    // 此时用户约束 Bound 已填充，被引用定义的字段/方法签名与 rich/shared
    // 传染均已就绪。隐式限制违规只诊断不拒绝（可恢复模型，同
    // TypeReferenceResolver 收口口径）
    internal sealed class InheritanceFillInChecker : ResolverVisitor<InheritanceFillInChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var (constructed, span) in env.InheritanceFillIns.Concat(env.TypeFillIns))
            {
                GenericConstraints.CheckConstructedType(constructed, span,
                    env.Unit.Symbols, env.Error);
            }
        }
    }
}
