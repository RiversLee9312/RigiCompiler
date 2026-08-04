namespace LatteCompiler
{
    // ===== 访问器声明侧检查与签名回填（SYNTAX §9.4/§9.4.1，S8e）=====
    //
    // 前置：字段类型已由 TypeReferenceResolver 解析（访问器签名 = 字段类型）。
    // 访问器符号非声明条目（P1 起挂字段三槽），本阶段逐字段处理：
    // 修饰符白名单与互斥、可见性落定（显式 ?? 字段级别）、const+set、
    // 无体 computed 拒绝、返回/参数类型回填。
    internal sealed class AccessorChecker : ResolverVisitor<AccessorChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (entry.Node is not VariableDeclarationASTNode v) continue;
                var field = (FieldSymbol)entry.Symbol;
                if (field.Getter == null && field.Setter == null) continue;

                // 访问器签名依赖字段类型；无标注字段的类型推断归 P3，与访问器不共存
                if (field.FieldType == null)
                {
                    env.Error(v.Span,
                        $"Field '{field.Name}' with accessors requires a type annotation");
                }
                // const 不得声明 setter（§9.4.1）
                if (field.IsConst && field.Setter != null)
                {
                    env.Error(v.Setter!.Span ?? v.Span,
                        $"Const field '{field.Name}' cannot declare a setter");
                }
                // 无体 + 计算形态：编译器无法生成计算实现（§9.4.1；自动访问器
                // 仅限 backing 形态，体合成归 P3）
                if (v.Getter is { Body: null } && !field.HasBackingStorage)
                {
                    env.Error(v.Getter.Span ?? v.Span,
                        $"Computed getter of '{field.Name}' must have a body " +
                        "(compiler-generated accessors require a backing field)");
                }
                if (v.Setter is { Body: null } && !field.HasBackingStorage)
                {
                    env.Error(v.Setter.Span ?? v.Span,
                        $"Computed setter of '{field.Name}' must have a body " +
                        "(compiler-generated accessors require a backing field)");
                }

                CheckAccessor(v.Getter, field.Getter, field, env);
                CheckAccessor(v.Setter, field.Setter, field, env);

                // 签名回填：getter 返回类型 / setter value 参数类型 = 字段类型（毒化跳过）
                if (field.FieldType is not null and not ErrorTypeSymbol)
                {
                    if (field.Getter != null) field.Getter.ReturnType = field.FieldType;
                    if (field.Setter != null) field.Setter.Parameters[0].Type = field.FieldType;
                }
            }
        }

        private static void CheckAccessor(PropertyAccessorASTNode? node, MethodSymbol? symbol,
            FieldSymbol field, ResolveEnvironment env)
        {
            if (node == null || symbol == null) return;
            // 修饰符白名单：仅访问级别（§9.4.1）；重复与多级别互斥同声明规则
            foreach (var group in node.Modifiers.GroupBy(m => m))
            {
                if (group.Key is not (Keywords.PUB or Keywords.PRIV or Keywords.PROTECTED
                    or Keywords.INTERNAL))
                {
                    env.Error(node.Span,
                        $"Accessor modifier '{group.Key}' is not allowed here " +
                        "(access modifiers only: pub/protected/internal/priv)");
                }
                else if (group.Count() > 1)
                {
                    env.Error(node.Span, $"Duplicate modifier '{group.Key}'");
                }
            }
            if (node.Modifiers.Count(m =>
                m is Keywords.PUB or Keywords.PRIV or Keywords.PROTECTED or Keywords.INTERNAL) > 1)
            {
                env.Error(node.Span,
                    "Access modifiers are mutually exclusive (pub/protected/internal/priv)");
            }
            // 可见性落定：访问器显式修饰 ?? 字段声明级别（§9.4.1）；
            // 文件身份随字段（访问器符号不经 AddEntry）
            var hasExplicit = node.Modifiers.Any(m =>
                m is Keywords.PUB or Keywords.PRIV or Keywords.PROTECTED or Keywords.INTERNAL);
            symbol.Accessibility = hasExplicit
                ? ResolveEnvironment.ParseAccessibility(node.Modifiers)
                : field.Accessibility;
            symbol.SourceFile = field.SourceFile;
        }
    }
}
