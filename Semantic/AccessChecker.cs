namespace RigiCompiler
{
    // 使用点访问控制判定（SYNTAX §16/§16.1，S8e）：P2 声明侧与 P3 函数体内共用。
    // 判定只读符号（Accessibility/SourceFile/宿主与命名空间驻留实例），
    // internal 恒可见（§16.1：当前单编译单元即模块）。调用方负责诊断措辞与落袋。
    internal static class AccessChecker
    {
        // useFile/useNamespace/useHost = 使用点上下文（文件身份引用、命名空间驻留
        // 实例、宿主类型——全局函数/全局初始化位置为 null）
        public static bool IsAccessible(SemanticSymbol target, RootASTNode? useFile,
            NamespaceSymbol? useNamespace, TypeSymbol? useHost)
        {
            return target.Accessibility switch
            {
                Accessibility.Public => true,
                // §16.1：当前单编译单元即模块，internal 恒可见
                Accessibility.Internal => true,
                Accessibility.Private => CheckPrivate(target, useFile, useHost),
                Accessibility.Protected => CheckProtected(target, useNamespace, useHost),
                _ => true,
            };
        }

        // 类型引用解析命中的检查便捷入口：非 TypeSymbol（泛型参数）与毒化跳过；
        // 递归口径同 FindInaccessibleType（F1/V-A：构造实参与嵌套宿主链参检）
        public static bool IsTypeAccessible(SemanticSymbol resolved, RootASTNode? useFile,
            NamespaceSymbol? useNamespace, TypeSymbol? useHost)
        {
            return FindInaccessibleType(resolved, useFile, useNamespace, useHost) == null;
        }

        // 递归使用点类型检查（F1/V-A，SYNTAX §16.1）：返回首个命中的不可见
        // 类型，全部可见返回 null。检查次序：
        //   1. 嵌套宿主链逐级（有效可见性 = 链上最小——pub 嵌套类型随 priv
        //      宿主不可见，与 SignatureAccessibilityChecker 的有效可见性
        //      口径一致；命中报该级，定位真正的泄漏点）；
        //   2. 构造实参递归（Box\<Hidden\>/Task\<Hidden\>/Hidden? 的泄漏点
        //      是实参 Hidden，报 Hidden 而非顶层容器——修复前 IsTypeAccessible
        //      只查顶层，构造具化路径整体漏检）。
        // 泛型参数（非 TypeSymbol）与毒化类型（ErrorType）跳过。
        public static TypeSymbol? FindInaccessibleType(SemanticSymbol? resolved,
            RootASTNode? useFile, NamespaceSymbol? useNamespace, TypeSymbol? useHost)
        {
            if (resolved is not TypeSymbol type || type is ErrorTypeSymbol) return null;
            for (var host = type; host != null; host = host.DeclaringType)
            {
                if (!IsAccessible(host, useFile, useNamespace, useHost)) return host;
            }
            if (type.TypeArguments != null)
            {
                foreach (var argument in type.TypeArguments)
                {
                    var hit = FindInaccessibleType(argument, useFile, useNamespace, useHost);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        // private：顶层声明同文件可见；成员/嵌套类型在声明类型及其嵌套类型
        // （递归）内可见（§16.1）。ext 成员按声明位置（顶层规则）而非目标类型
        // 容器判定（§4.4/§16.1）
        private static bool CheckPrivate(SemanticSymbol target, RootASTNode? useFile,
            TypeSymbol? useHost)
        {
            var container = ContainingTypeOf(target);
            if (container == null)
            {
                // 顶层声明（含顶层 ext）：同文件（SourceFile 缺失 = 不经声明
                // 收集的产物，保守放行）
                return target.SourceFile == null ||
                    (useFile != null && ReferenceEquals(target.SourceFile, useFile));
            }
            for (var h = useHost; h != null; h = h.DeclaringType)
            {
                if (ReferenceEquals(h.ConstructedFrom ?? h, container)) return true;
            }
            return false;
        }

        // protected：子类体内（useHost 基类链含声明宿主）或同包（同命名空间
        // 驻留实例——命名空间逐段驻留合并，同全名即同实例，§16.1）
        private static bool CheckProtected(SemanticSymbol target, NamespaceSymbol? useNamespace,
            TypeSymbol? useHost)
        {
            var container = ContainingTypeOf(target);
            if (container != null)
            {
                for (var h = useHost; h != null; h = h.BaseType)
                {
                    if (ReferenceEquals(h.ConstructedFrom ?? h, container)) return true;
                }
            }
            var targetNs = ContainingNamespaceOf(target);
            return targetNs != null && useNamespace != null && ReferenceEquals(targetNs, useNamespace);
        }

        // 符号的直接宿主类型（成员 = Owner；嵌套类型 = DeclaringType；顶层 = null）。
        // ext 成员虽经 AttachToExtTarget 改写 Owner，可见性仍按声明位置——
        // 此处视同无宿主（顶层），与 §4.4/§16.1 对齐
        public static TypeSymbol? ContainingTypeOf(SemanticSymbol target)
        {
            if (IsExtensionMember(target)) return null;
            return target switch
            {
                TypeSymbol t => t.DeclaringType,
                FieldSymbol f => f.Owner,
                MethodSymbol m => m.Owner,
                _ => null,
            };
        }

        // ext 成员识别（P1 拆名登记的 ExtTargetPath 原文非空）
        private static bool IsExtensionMember(SemanticSymbol target) => target switch
        {
            FieldSymbol { ExtTargetPath: not null } => true,
            MethodSymbol { ExtTargetPath: not null } => true,
            _ => false,
        };

        // 符号所在命名空间（沿宿主链上溯；顶层符号直取）
        public static NamespaceSymbol? ContainingNamespaceOf(SemanticSymbol target)
        {
            return target switch
            {
                TypeSymbol t => t.DeclaringType == null ? t.Namespace : ContainingNamespaceOf(t.DeclaringType),
                FieldSymbol f => f.Owner == null ? f.Namespace : ContainingNamespaceOf(f.Owner),
                MethodSymbol m => m.Owner == null ? m.Namespace : ContainingNamespaceOf(m.Owner),
                _ => null,
            };
        }

        // 统一诊断措辞（P2/P3 各阶段同文落袋）
        public static string InaccessibleMessage(SemanticSymbol target)
        {
            return $"'{target.Name}' is inaccessible due to its accessibility level";
        }
    }
}
