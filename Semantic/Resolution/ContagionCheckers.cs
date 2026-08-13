namespace RigiCompiler
{
    // ===== 子任务 4：rich/shared 单向传染 + 字段闭包 =====

    // 闭包表持有者行（SYNTAX §3.1.1 七行；None = 不查：interface、内建类型）
    internal enum HolderCategory
    {
        None,
        PlainStruct,        // 非 rich struct / 非 rich enum struct
        RichStruct,
        SharedRichStruct,
        WrapperPlain,       // 非 shared wrapper
        SharedWrapper,
        LocalClass,
        SharedClass,
    }

    // 字段类型分类（闭包表的两列：持有的 Object / 内嵌的 ValueType）
    internal enum FieldCategory
    {
        Unknown,            // 泛型参数 / 毒化 / 无标注（跳过检查）
        LocalObject,
        SharedObject,       // shared class 或 T 共享安全的 Nullable\<T\>
        NonRichValue,
        LocalRichValue,
        SharedRichValue,
    }

    // 自旧 DeclarationResolver.ResolveSession.CheckContagion 迁移，行为不变。
    internal sealed class ContagionChecker : ResolverVisitor<ContagionChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                var baseType = type.BaseType;
                if (baseType == null || baseType.IsBuiltin) continue;
                // 单向传染（§3.1.1）：基类 rich/shared ⇒ 子类必须同标；反向可收紧，
                // 收紧后的合法性由字段闭包检查兜底（CheckFieldClosures 含继承字段）
                var def = baseType.ConstructedFrom ?? baseType;
                if (def.IsShared && !type.IsShared)
                {
                    env.Error(entry.Node.Span,
                        $"'{type.Name}': base type '{def.Name}' is 'shared', so the derived type must also be 'shared'");
                }
                if (def.IsRich && !type.IsRich)
                {
                    env.Error(entry.Node.Span,
                        $"'{type.Name}': base type '{def.Name}' is 'rich', so the derived type must also be 'rich'");
                }
            }
        }
    }

    // 自旧 DeclarationResolver.ResolveSession.CheckFieldClosures/ClassifyHolder/
    // ClassifyFieldType/ClosureFieldsOf/CheckClosureField/CheckDirectClosure/
    // ExpandConstructedField 迁移，行为不变。
    internal sealed class FieldClosureChecker : ResolverVisitor<FieldClosureChecker>
    {
        // ext 实例字段的闭包检查入口（M80，ExtensionRegistrar 在注册前调用）：
        // ext 实例字段注册后即是目标类型的实例字段，与声明在目标体内同受
        // §3.1.1 闭包表约束——本检查器阶段运行在 ExtensionRegistrar 之前，
        // 无法覆盖 ext 字段，故开此入口（完整复用 CheckClosureField 的直接
        // 分类 + 泛型实参展开）。返回 true = 已落违规诊断（调用方不注册）
        public static bool CheckExtensionField(TypeSymbol targetType, FieldSymbol field,
            CharRange? span, ResolveEnvironment env)
        {
            var holder = ClassifyHolder(targetType);
            if (holder == HolderCategory.None) return false;
            var before = env.Unit.Diagnostics.Diagnostics.Count;
            CheckClosureField(holder, targetType, field.Name, field.FieldType, span,
                new HashSet<TypeSymbol>(), env);
            return env.Unit.Diagnostics.Diagnostics.Count != before;
        }

        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var type = (TypeSymbol)entry.Symbol;
                var holder = ClassifyHolder(type);
                if (holder == HolderCategory.None) continue;
                var visited = new HashSet<TypeSymbol>();
                foreach (var (name, fieldType) in ClosureFieldsOf(type, env))
                {
                    CheckClosureField(holder, type, name, fieldType, entry.Node.Span, visited, env);
                }
            }
        }

        private static HolderCategory ClassifyHolder(TypeSymbol type)
        {
            if (type.IsBuiltin) return HolderCategory.None;
            switch (type.Kind)
            {
                case TypeKind.Interface:
                    return HolderCategory.None;
                case TypeKind.Wrapper:
                    return type.IsShared ? HolderCategory.SharedWrapper : HolderCategory.WrapperPlain;
                case TypeKind.Class:
                    return type.IsShared ? HolderCategory.SharedClass : HolderCategory.LocalClass;
                default:    // Struct / EnumStruct
                    if (!type.IsRich) return HolderCategory.PlainStruct;
                    return type.IsShared ? HolderCategory.SharedRichStruct : HolderCategory.RichStruct;
            }
        }

        private static FieldCategory ClassifyFieldType(SemanticSymbol? fieldType)
        {
            if (fieldType is not TypeSymbol t) return FieldCategory.Unknown;
            if (t is ErrorTypeSymbol) return FieldCategory.Unknown;
            if (t.IsValueTypeBranch)
            {
                if (!t.IsRich) return FieldCategory.NonRichValue;
                return t.IsShared ? FieldCategory.SharedRichValue : FieldCategory.LocalRichValue;
            }
            // Nullable\<T> 按 T 推导（§3.1.2）；T 为泛型参数时安全性未知，跳过
            if (t.ConstructedFrom is { DerivesSharedSafetyFromTypeArgument: true }
                && t.TypeArguments![0] is not TypeSymbol)
            {
                return FieldCategory.Unknown;
            }
            return t.IsSharedSafe() ? FieldCategory.SharedObject : FieldCategory.LocalObject;
        }

        // 直接字段（声明类型原样）+ 沿基类链的继承字段（按构造基类代入实参）
        private static IEnumerable<(string Name, SemanticSymbol? FieldType)> ClosureFieldsOf(
            TypeSymbol type, ResolveEnvironment env)
        {
            foreach (var f in type.Fields)
            {
                yield return (f.Name, f.FieldType);
            }
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                var def = b.ConstructedFrom ?? b;
                if (def.IsBuiltin) yield break;
                foreach (var f in def.Fields)
                {
                    yield return (f.Name, env.Substitute(f.FieldType, def, b));
                }
            }
        }

        private static void CheckClosureField(HolderCategory holder, TypeSymbol holderType, string fieldName,
            SemanticSymbol? fieldType, CharRange? span, HashSet<TypeSymbol> visited, ResolveEnvironment env)
        {
            // 直接分类违规即报且不再展开（同一字段一处违规报一条，避免直接分类与
            // 实参展开对同一事实重复诊断）；直接分类放行时才需展开实参拦截
            if (CheckDirectClosure(holder, holderType, fieldName, fieldType, span, env)) return;
            // 泛型实参所展开的字段同样受限（§3.1.1）：用户构造类型字段代入实参递归查
            if (fieldType is TypeSymbol { ConstructedFrom: not null } constructed)
            {
                ExpandConstructedField(holder, holderType, fieldName, constructed, span, visited, env);
            }
        }

        // 持有者行 × 字段类型分类的直接检查；违规报一条诊断并返回 true
        private static bool CheckDirectClosure(HolderCategory holder, TypeSymbol holderType,
            string fieldName, SemanticSymbol? fieldType, CharRange? span, ResolveEnvironment env)
        {
            return CheckDirectClosure(holder, holderType, fieldName, ClassifyFieldType(fieldType), span, null, env);
        }

        private static bool CheckDirectClosure(HolderCategory holder, TypeSymbol holderType, string fieldName,
            FieldCategory category, CharRange? span, string? viaNote, ResolveEnvironment env)
        {
            if (category == FieldCategory.Unknown) return false;
            switch (holder)
            {
                case HolderCategory.PlainStruct:
                    if (category == FieldCategory.LocalObject || category == FieldCategory.SharedObject)
                    {
                        env.Error(span, $"Non-rich struct '{holderType.Name}' cannot hold object field '{fieldName}'{viaNote}");
                        return true;
                    }
                    if (category != FieldCategory.NonRichValue)
                    {
                        env.Error(span, $"Non-rich struct '{holderType.Name}' cannot embed rich value type field '{fieldName}'{viaNote}");
                        return true;
                    }
                    return false;
                case HolderCategory.SharedClass:
                case HolderCategory.SharedRichStruct:
                case HolderCategory.SharedWrapper:
                    if (category == FieldCategory.LocalObject)
                    {
                        env.Error(span, $"'{holderType.Name}' is shared and cannot hold local object field '{fieldName}'{viaNote}");
                        return true;
                    }
                    if (category == FieldCategory.LocalRichValue)
                    {
                        env.Error(span, $"'{holderType.Name}' is shared and cannot embed non-shared rich value type field '{fieldName}'{viaNote}");
                        return true;
                    }
                    return false;
                default:
                    // LocalClass / RichStruct / WrapperPlain：闭包不受限
                    return false;
            }
        }

        private static void ExpandConstructedField(HolderCategory holder, TypeSymbol holderType, string fieldName,
            TypeSymbol constructed, CharRange? span, HashSet<TypeSymbol> visited, ResolveEnvironment env)
        {
            // 引用相等去重：Node\<T> 自嵌套等场景沿展开链收敛
            if (!visited.Add(constructed)) return;
            var def = constructed.ConstructedFrom!;
            // 内建构造（Nullable/Box/Span/Type）的闭包属性由 §3.1.2 特权规则
            // 在 ClassifyFieldType/IsSharedSafe 覆盖，不展开
            if (def.IsBuiltin) return;
            foreach (var f in def.Fields)
            {
                var fieldType = env.Substitute(f.FieldType, def, constructed);
                var category = ClassifyFieldType(fieldType);
                if (CheckDirectClosure(holder, holderType, fieldName, category, span,
                    $" (via generic argument of '{def.Name}')", env))
                {
                    continue;
                }
                if (fieldType is TypeSymbol { ConstructedFrom: not null } inner)
                {
                    ExpandConstructedField(holder, holderType, fieldName, inner, span, visited, env);
                }
            }
        }
    }

    // ===== 子任务 5：共享安全闸门（全局/静态字段）=====
    //
    // 自旧 DeclarationResolver.ResolveSession.CheckSharedSafetyGates 迁移，行为不变。
    internal sealed class SharedSafetyGateChecker : ResolverVisitor<SharedSafetyGateChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph || entry.Symbol is not FieldSymbol field) continue;
                // 闸门 1（§3.1.1）：全局变量/常量、静态字段的类型必须共享安全；
                // ext 实例成员注册后是目标类型的实例字段，不受闸门约束
                var gated = field.IsStatic || (field.Owner == null && field.ExtTargetPath == null);
                if (!gated) continue;
                if (field.FieldType is not TypeSymbol type || type is ErrorTypeSymbol) continue;
                // 泛型上下文中的构造类型（含泛型参数实参）安全性未知，跳过
                if (ResolveEnvironment.ContainsGenericParameter(type)) continue;
                if (!type.IsSharedSafe())
                {
                    env.Error(entry.Node.Span,
                        $"Global or static field '{field.Name}' must have a shared-safe type (SYNTAX §3.1.1)");
                }
            }
        }
    }
}
