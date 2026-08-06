namespace LatteCompiler
{
    // ===== 子任务 3：修饰符合法性 =====
    //
    // 自旧 DeclarationResolver.ResolveSession.CheckModifiers/CheckDuplicateModifiers/
    // CheckAccessModifierExclusivity/CheckTypeModifiers/CheckMemberModifiers 迁移，行为不变。
    internal sealed class ModifierChecker : ResolverVisitor<ModifierChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                var modifiers = ResolveEnvironment.ModifiersOf(entry.Node);
                CheckDuplicateModifiers(modifiers, entry, env);
                CheckAccessModifierExclusivity(modifiers, entry, env);
                if (entry.Symbol is TypeSymbol type)
                {
                    CheckTypeModifiers(type, modifiers, entry, env);
                }
                else
                {
                    CheckMemberModifiers(entry, modifiers, env);
                }
            }
        }

        private static void CheckDuplicateModifiers(List<string> modifiers, DeclEntry entry,
            ResolveEnvironment env)
        {
            foreach (var group in modifiers.GroupBy(m => m))
            {
                if (group.Count() > 1)
                {
                    env.Error(entry.Node.Span, $"Duplicate modifier '{group.Key}'");
                }
            }
        }

        private static void CheckAccessModifierExclusivity(List<string> modifiers, DeclEntry entry,
            ResolveEnvironment env)
        {
            var count = modifiers.Count(m =>
                m == Keywords.PUB || m == Keywords.PRIV || m == Keywords.PROTECTED || m == Keywords.INTERNAL);
            if (count > 1)
            {
                env.Error(entry.Node.Span, "Access modifiers are mutually exclusive (pub/protected/internal/priv)");
            }
        }

        private static void CheckTypeModifiers(TypeSymbol type, List<string> modifiers, DeclEntry entry,
            ResolveEnvironment env)
        {
            var span = entry.Node.Span;
            var rich = modifiers.Contains(Keywords.RICH);
            // rich 仅 struct/enum struct；wrapper 恒 rich 由声明形式隐含（§3.1.1/§14.9）
            if (rich && type.Kind == TypeKind.Class)
            {
                env.Error(span, $"'{type.Name}': 'rich' can only be applied to struct/enum struct");
            }
            if (rich && type.Kind == TypeKind.Interface)
            {
                env.Error(span, $"'{type.Name}': 'rich' cannot be applied to interface");
            }
            if (rich && type.Kind == TypeKind.Wrapper)
            {
                env.Error(span, $"'{type.Name}': 'rich' is implied by the wrapper declaration and must not be written");
            }
            if (modifiers.Contains(Keywords.SHARED) && type.Kind == TypeKind.Interface)
            {
                env.Error(span, $"'{type.Name}': 'shared' cannot be applied to interface");
            }
            // shared struct 必 rich（§3.1.1）
            if (type.IsShared && !type.IsRich &&
                (type.Kind == TypeKind.Struct || type.Kind == TypeKind.EnumStruct))
            {
                env.Error(span, $"'{type.Name}': 'shared' struct must also be 'rich'");
            }
            // 非 rich struct 不得 open/abstract（§3.1.1 封闭性）；
            // enum struct 由下方特例统一报（同因一报，不重复落诊断）
            if (type.Kind == TypeKind.Struct && !type.IsRich)
            {
                if (type.IsOpen)
                {
                    env.Error(span, $"'{type.Name}': non-rich struct cannot be 'open'");
                }
                if (type.IsAbstract)
                {
                    env.Error(span, $"'{type.Name}': non-rich struct cannot be 'abstract'");
                }
            }
            // enum struct 是封闭特例（§10）：不得 open；abstract 天然 open 同禁
            if (type.Kind == TypeKind.EnumStruct && type.IsOpen)
            {
                env.Error(span, $"'{type.Name}': enum struct cannot be 'open'");
            }
            if (type.Kind == TypeKind.EnumStruct && type.IsAbstract)
            {
                env.Error(span, $"'{type.Name}': enum struct cannot be 'abstract'");
            }
            // open 仅 class/struct（§9.2）；interface 不得 open（wrapper 见下）
            if (type.IsOpen && type.Kind == TypeKind.Interface)
            {
                env.Error(span, $"'{type.Name}': 'open' cannot be applied to interface");
            }
            // open × abstract 互斥（§9.2：abstract 天然 open）
            if (type.IsOpen && type.IsAbstract)
            {
                env.Error(span, $"'{type.Name}': 'open' and 'abstract' are mutually exclusive");
            }
            // singleton 仅 class、必须 shared（§3.1.1 闸门）、不得 abstract
            if (type.IsSingleton)
            {
                if (type.Kind != TypeKind.Class)
                {
                    env.Error(span, $"'{type.Name}': 'singleton' can only be applied to class");
                }
                if (!type.IsShared)
                {
                    env.Error(span, $"'{type.Name}': singleton class must also be 'shared'");
                }
                if (type.IsAbstract)
                {
                    env.Error(span, $"'{type.Name}': 'abstract' and 'singleton' are mutually exclusive");
                }
            }
            // wrapper 不得 open/abstract/singleton（§14.9）
            if (type.Kind == TypeKind.Wrapper && (type.IsOpen || type.IsAbstract))
            {
                env.Error(span, $"'{type.Name}': wrapper cannot be 'open' or 'abstract'");
            }
        }

        private static void CheckMemberModifiers(DeclEntry entry, List<string> modifiers,
            ResolveEnvironment env)
        {
            // async 仅适用于函数与 lambda（§9.2）
            if (entry.Symbol is FieldSymbol && modifiers.Contains(Keywords.ASYNC))
            {
                env.Error(entry.Node.Span, "'async' can only be applied to functions");
            }
            // interface 不得声明字段（SYNTAX §11 接口成员只有函数——字段不参与
            // 闭包检查、不产生实现要求、实现类不继承，纯死声明，声明侧拒绝）
            if (entry.Symbol is FieldSymbol interfaceField
                && entry.DeclaringType?.Kind == TypeKind.Interface)
            {
                env.Error(entry.Node.Span,
                    $"'{interfaceField.Name}': interfaces cannot declare fields");
            }
            // operator 必须实例（静态无多态：使用侧 FindInstanceOperators/
            // FindConversionOperator 只查实例方法，static operator 在任何使用点
            // 都不可达，纯死声明，声明侧拒绝）
            if (entry.Symbol is MethodSymbol { Kind: MethodKind.Operator } op && op.IsStatic)
            {
                env.Error(entry.Node.Span, $"'{op.Name}': operators cannot be 'static'");
            }
            // open/abstract/override 仅普通成员方法（§9.2.1）：字段/init/operator/
            // 全局函数/static 方法上使用即错误；静态无多态
            var open = modifiers.Contains(Keywords.OPEN);
            var abstractM = modifiers.Contains(Keywords.ABSTRACT);
            var overrideM = modifiers.Contains(Keywords.OVERRIDE);
            if (open || abstractM || overrideM)
            {
                if (entry.Symbol is not MethodSymbol inheritMethod ||
                    inheritMethod.Kind != MethodKind.Regular || entry.DeclaringType == null)
                {
                    env.Error(entry.Node.Span,
                        "'open'/'abstract'/'override' can only be applied to member methods");
                }
                else
                {
                    if (inheritMethod.IsStatic)
                    {
                        env.Error(entry.Node.Span,
                            "'open'/'abstract'/'override' cannot be applied to static methods");
                    }
                    // abstract 天然 open（§9.2，同类型级互斥）
                    if (open && abstractM)
                    {
                        env.Error(entry.Node.Span, "'open' and 'abstract' are mutually exclusive");
                    }
                    // 接口成员天然可覆写（§9.2.1）
                    if (entry.DeclaringType.Kind == TypeKind.Interface && (open || abstractM))
                    {
                        env.Error(entry.Node.Span, "'open'/'abstract' is redundant on interface members");
                    }
                }
            }
            // ext 必须是限定名（§4.4：TargetType.memberName）且只能用于全局声明；
            // 顶层无 protected 概念（§16.1：顶层仅 private/internal/public）
            if (modifiers.Contains(Keywords.EXT))
            {
                var hasQualifiedName = entry.Symbol switch
                {
                    FieldSymbol f => f.ExtTargetPath != null,
                    MethodSymbol m => m.ExtTargetPath != null,
                    _ => false,
                };
                if (!hasQualifiedName)
                {
                    env.Error(entry.Node.Span, "'ext' declaration requires a qualified name (TargetType.memberName)");
                }
                if (entry.DeclaringType != null)
                {
                    env.Error(entry.Node.Span, "'ext' can only be applied to global declarations");
                }
                if (modifiers.Contains(Keywords.PROTECTED))
                {
                    env.Error(entry.Node.Span,
                        "'protected' cannot be applied to extension members");
                }
            }
        }
    }
}
