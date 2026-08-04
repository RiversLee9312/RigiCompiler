namespace LatteCompiler
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

        // 类型引用解析命中的检查便捷入口：非 TypeSymbol（泛型参数）与毒化跳过
        public static bool IsTypeAccessible(SemanticSymbol resolved, RootASTNode? useFile,
            NamespaceSymbol? useNamespace, TypeSymbol? useHost)
        {
            return resolved is not TypeSymbol type || type is ErrorTypeSymbol ||
                IsAccessible(type, useFile, useNamespace, useHost);
        }

        // private：顶层声明同文件可见；成员/嵌套类型在声明类型及其嵌套类型
        // （递归）内可见（§16.1）
        private static bool CheckPrivate(SemanticSymbol target, RootASTNode? useFile,
            TypeSymbol? useHost)
        {
            var container = ContainingTypeOf(target);
            if (container == null)
            {
                // 顶层声明：同文件（SourceFile 缺失 = 不经声明收集的产物，保守放行）
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

        // 符号的直接宿主类型（成员 = Owner；嵌套类型 = DeclaringType；顶层 = null）
        public static TypeSymbol? ContainingTypeOf(SemanticSymbol target)
        {
            return target switch
            {
                TypeSymbol t => t.DeclaringType,
                FieldSymbol f => f.Owner,
                MethodSymbol m => m.Owner,
                _ => null,
            };
        }

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
