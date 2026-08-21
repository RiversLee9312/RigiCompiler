namespace RigiCompiler
{
    // ===== 声明点签名泄漏检查（SYNTAX §16.1，bug S5 修复1；F2 补字段/访问器）=====
    //
    // pub/protected/internal 的顶层函数与成员签名（返回类型 + 参数类型）
    // 不得提及有效可见性更低的类型——否则私有类型经签名泄漏出文件/宿主，
    // 而使用点检查只在「写出类型名」时触发（TypeReferenceResolver/
    // TypeReferences），推断路径不经此门，`const h = make()` 会把私有
    // 类型无声带入他文件（CS0050/CS0051 式声明点拦截）。
    // F2 补齐：字段/全局变量与属性访问器同闸（`pub var x: Hidden` 是
    // 绕过函数签名的源头泄漏门；访问器签名 = 字段类型、可见性可独立
    // 于字段，§9.4.1）。
    //
    // 可见性比较直接按枚举序（Private < Protected < Internal < Public）：
    // internal 签名出现 private 类型报错；pub 签名出现 internal 类型同样
    // 报错（§16.1 访问层级：internal 模块内、pub 无限制）。
    // 有效可见性 = 符号自身可见性与嵌套宿主链（成员宿主类型链）逐级最小：
    // `class C { pub func f(): X }` 的 f 有效可见性随 C 为 private，
    // 不检查；`pub class C { ... priv class Inner ... pub func f(): Inner }`
    // 的 Inner 有效可见性为 private，报错。
    // 构造类型递归检查实参（Box\<Hidden\> 的泄漏点是 Hidden，报 Hidden）。
    // 泛型参数与毒化类型（ErrorType）跳过。执行序：紧随
    // TypeReferenceResolver（签名类型刚就绪；可见性在 EntryCollector
    // 已落定，接口成员默认 pub 同已生效）。
    internal sealed class SignatureAccessibilityChecker
        : ResolverVisitor<SignatureAccessibilityChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph)
                {
                    continue;
                }
                if (entry.Node is CallableDeclarationASTNode fn)
                {
                    CheckCallable(entry, fn, env);
                }
                else if (entry.Node is VariableDeclarationASTNode v)
                {
                    CheckField(entry, v, env);
                }
            }
        }

        // 函数/方法签名闸门（返回类型 + 参数类型）
        private static void CheckCallable(DeclEntry entry, CallableDeclarationASTNode fn,
            ResolveEnvironment env)
        {
            var method = (MethodSymbol)entry.Symbol;
            var memberEffective = EffectiveAccessibility(method.Accessibility,
                entry.DeclaringType);
            // 成员有效可见性为 private：签名不越出文件/宿主，无泄漏可查
            if (memberEffective == Accessibility.Private) return;
            if (fn.ReturnType != null && method.ReturnType != null)
            {
                CheckSignatureType(method, method.ReturnType, memberEffective,
                    isReturnType: true, fn.ReturnType.Span ?? fn.Span, env);
            }
            for (int i = 0; i < method.Parameters.Count &&
                i < fn.Parameters.Parameters.Count; i++)
            {
                var parameterType = method.Parameters[i].Type;
                if (parameterType == null) continue;
                CheckSignatureType(method, parameterType, memberEffective,
                    isReturnType: false,
                    fn.Parameters.Parameters[i].Span ?? fn.Span, env);
            }
        }

        // 字段/全局变量与属性访问器签名闸门（F2，§16.1）：pub/protected/
        // internal 字段的类型不得提及有效可见性更低的类型——字段是泄漏
        // 的源头门（`pub var x: Hidden` 绕过函数签名检查直泄）；访问器
        // 签名 = 字段类型，可见性可独立于字段（§9.4.1 显式修饰 ?? 字段
        // 级别——本阶段先于 AccessorChecker，此处按同规则就地展开）。
        // 无标注字段（FieldType 为 null）类型推断归 P3，不在此检查
        private static void CheckField(DeclEntry entry, VariableDeclarationASTNode v,
            ResolveEnvironment env)
        {
            var field = (FieldSymbol)entry.Symbol;
            if (field.FieldType == null) return;
            var fieldEffective = EffectiveAccessibility(field.Accessibility,
                entry.DeclaringType);
            if (fieldEffective != Accessibility.Private)
            {
                CheckFieldType(field, field.FieldType, fieldEffective, "field",
                    v.TypeAnnotation?.Span ?? v.Span, env);
            }
            CheckAccessor(v.Getter, field, entry, env);
            CheckAccessor(v.Setter, field, entry, env);
        }

        // 单访问器签名闸门：可见性 = 显式修饰 ?? 字段级别（与
        // AccessorChecker 落定同规则），签名类型 = 字段类型
        private static void CheckAccessor(PropertyAccessorASTNode? node, FieldSymbol field,
            DeclEntry entry, ResolveEnvironment env)
        {
            if (node == null || field.FieldType == null) return;
            var hasExplicit = node.Modifiers.Any(m =>
                m is Keywords.PUB or Keywords.PRIV or Keywords.PROTECTED or Keywords.INTERNAL);
            var own = hasExplicit
                ? ResolveEnvironment.ParseAccessibility(node.Modifiers)
                : field.Accessibility;
            var effective = EffectiveAccessibility(own, entry.DeclaringType);
            if (effective == Accessibility.Private) return;
            var kind = node.Kind == AccessorKind.Get ? "getter" : "setter";
            CheckFieldType(field, field.FieldType, effective, kind,
                node.Span ?? entry.Node.Span, env);
        }

        // 字段/访问器签名位置的递归检查（与函数签名同口径，措辞按位置）
        private static void CheckFieldType(FieldSymbol field, SemanticSymbol type,
            Accessibility required, string kind, CharRange? span, ResolveEnvironment env)
        {
            var hit = FindLessAccessible(type, required);
            if (hit == null) return;
            var position = kind switch
            {
                "getter" => "return",
                "setter" => "parameter",
                _ => "field",
            };
            env.Error(span,
                $"Inconsistent accessibility: {position} type '{hit.Name}' is less " +
                $"accessible than {kind} '{field.Name}'");
        }

        // 单个签名位置的递归检查：命中即报错并停（每个位置只报最深不可达者，
        // 避免同一位置对容器与实参重复落袋）
        private static void CheckSignatureType(MethodSymbol method, SemanticSymbol type,
            Accessibility required, bool isReturnType, CharRange? span,
            ResolveEnvironment env)
        {
            var hit = FindLessAccessible(type, required);
            if (hit == null) return;
            env.Error(span,
                $"Inconsistent accessibility: {(isReturnType ? "return" : "parameter")} " +
                $"type '{hit.Name}' is less accessible than function '{method.Name}'");
        }

        // 递归查找首个有效可见性低于 required 的类型：自身先判（命中即
        // 该级），否则递归构造实参。泛型参数（引用相等身份）与毒化类型
        // （ErrorType）跳过（InheritanceResolver 的继承单调性检查共用）
        internal static TypeSymbol? FindLessAccessible(SemanticSymbol type,
            Accessibility required)
        {
            if (type is not TypeSymbol t || t is ErrorTypeSymbol) return null;
            var typeEffective = EffectiveAccessibility(t.Accessibility, t.DeclaringType);
            if (typeEffective < required) return t;
            if (t.TypeArguments != null)
            {
                foreach (var argument in t.TypeArguments)
                {
                    var hit = FindLessAccessible(argument, required);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        // 有效可见性：自身可见性与嵌套宿主链逐级最小（InheritanceResolver
        // 的继承单调性检查共用）
        internal static Accessibility EffectiveAccessibility(Accessibility own,
            TypeSymbol? declaringType)
        {
            var effective = own;
            for (var host = declaringType; host != null; host = host.DeclaringType)
            {
                if (host.Accessibility < effective) effective = host.Accessibility;
            }
            return effective;
        }
    }
}
