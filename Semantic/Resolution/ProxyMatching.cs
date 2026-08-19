namespace RigiCompiler
{
    // ===== M88：proxy 形状匹配设施（原 ProxyDispatchResolver.BuildChainForMember
    // 的诊断半——零符号合成）=====
    //
    // 给定宿主 + 成员 + WrapperApplication，判定 specific / wildcard / 不命中；
    // P2 阶段逐成员检查「名中形状不符」并落诊断（措辞逐字保留自
    // ProxyDispatchResolver）。DispatchExplainer 下一棒复用 Match 结果。
    internal enum ProxyMatchKind
    {
        None,
        Specific,
        Wildcard,
    }

    internal static class ProxyMatching
    {
        // Value wrapper 的 get-only 判定（§14.3「只实现 get 则只适用于只读
        // 变量」——P2 字段应用点与 P3 栈上变量应用点共用）：实现了
        // .proxy.get 但未实现 .proxy.set。不带任何 proxy 的 wrapper 是合法
        // 的纯状态修饰器（无拦截链，不参与本判定）
        public static bool IsGetOnlyValueWrapper(TypeSymbol wrapperType)
        {
            var getter = false;
            var setter = false;
            foreach (var method in wrapperType.Methods)
            {
                if (method.ProxyTemplate == null) continue;
                if (method.Name == ".proxy.get") getter = true;
                if (method.Name == ".proxy.set") setter = true;
            }
            return getter && !setter;
        }

        // 单应用 × 单成员命中结果（不落诊断；形状不符 → None，由 Check 阶段诊断）
        public static ProxyMatchKind Match(TypeSymbol host, MethodSymbol member,
            WrapperApplication application, ResolveEnvironment env,
            string specificName, string wildcardName)
        {
            var wrapperDef = application.WrapperDefinition;
            if (HasInvalidGenericArity(wrapperDef)) return ProxyMatchKind.None;
            if (ContainsErrorType(member)) return ProxyMatchKind.None;

            var specific = wrapperDef.Methods.FirstOrDefault(m => m.Name == specificName);
            if (specific != null && !ContainsErrorType(specific))
            {
                if (member.Kind is MethodKind.Getter or MethodKind.Setter)
                {
                    return ProxyMatchKind.Specific;
                }
                if (member.GenericParameters.Count == 0
                    && SpecificShapeMatches(specific, member, application, env))
                {
                    return ProxyMatchKind.Specific;
                }
                if (member.GenericParameters.Count == 0)
                {
                    // 名中形状不符——调用方诊断；此处仍返回 None（不落 wildcard）
                    return ProxyMatchKind.None;
                }
                // 泛型成员：specific 无法表达，落 wildcard
            }
            var wildcard = wrapperDef.Methods.FirstOrDefault(m => m.Name == wildcardName);
            return wildcard != null && !ContainsErrorType(wildcard)
                ? ProxyMatchKind.Wildcard
                : ProxyMatchKind.None;
        }

        // specific 名命中但形状不符（仅非访问器、非泛型成员）
        public static bool SpecificShapeMismatch(TypeSymbol host, MethodSymbol member,
            WrapperApplication application, ResolveEnvironment env, string specificName,
            out MethodSymbol? mismatchedProxy)
        {
            mismatchedProxy = null;
            var wrapperDef = application.WrapperDefinition;
            if (HasInvalidGenericArity(wrapperDef)) return false;
            if (ContainsErrorType(member)) return false;
            if (member.Kind is MethodKind.Getter or MethodKind.Setter) return false;
            if (member.GenericParameters.Count != 0) return false;
            var specific = wrapperDef.Methods.FirstOrDefault(m => m.Name == specificName);
            if (specific == null || ContainsErrorType(specific)) return false;
            if (SpecificShapeMatches(specific, member, application, env)) return false;
            mismatchedProxy = specific;
            return true;
        }

        // 宿主定义是否具备方法类别 `.proxy.*` wildcard（只读 AppliedWrappers，
        // 不回写实现者——interface 传染宿主见 IsDowngradeEligible 传递闭包）
        public static bool HasMethodWildcardProxy(TypeSymbol hostDefinition)
        {
            foreach (var application in hostDefinition.AppliedWrappers)
            {
                var wrapperDef = application.WrapperDefinition;
                if (HasInvalidGenericArity(wrapperDef)) continue;
                if (wrapperDef.Methods.Any(m => m.Name == ".proxy.*")) return true;
            }
            return false;
        }

        // #28③：receiver 自身 + BaseType 链 + 每层 Interfaces 传递闭包查降级资格。
        // 资格只依赖 wrapper 定义是否声明 `.proxy.*` → 定义级去重；构造泛型
        // 接口回退 definition（不引入 ResolveEnvironment）；HashSet 防接口环。
        public static bool IsDowngradeEligible(TypeSymbol receiverType)
        {
            var visited = new HashSet<TypeSymbol>();
            for (var t = receiverType; t != null; t = t.BaseType)
            {
                var definition = t.ConstructedFrom ?? t;
                if (!visited.Add(definition)) continue;
                if (HasMethodWildcardProxy(definition)) return true;
                if (AnyInterfaceEligible(definition, visited)) return true;
            }
            return false;
        }

        // Interfaces 传递闭包（class implements 与 interface : Base 均落
        // TypeSymbol.Interfaces；构造形态回退 definition）
        private static bool AnyInterfaceEligible(TypeSymbol definition,
            HashSet<TypeSymbol> visited)
        {
            foreach (var iface in definition.Interfaces)
            {
                var ifaceDef = iface.ConstructedFrom ?? iface;
                if (!visited.Add(ifaceDef)) continue;
                if (HasMethodWildcardProxy(ifaceDef)) return true;
                if (AnyInterfaceEligible(ifaceDef, visited)) return true;
            }
            return false;
        }

        public static bool HasInvalidGenericArity(TypeSymbol wrapperDefinition)
        {
            return wrapperDefinition.WrapperTarget == WrapperTargetKind.Entity
                ? wrapperDefinition.GenericParameters.Count > 1
                : wrapperDefinition.GenericParameters.Count > 0;
        }

        public static bool SpecificShapeMatches(MethodSymbol proxy, MethodSymbol member,
            WrapperApplication application, ResolveEnvironment env)
        {
            if (proxy.Parameters.Count != member.Parameters.Count) return false;
            for (var i = 0; i < proxy.Parameters.Count; i++)
            {
                var proxyParam = proxy.Parameters[i];
                var memberParam = member.Parameters[i];
                if (proxyParam.Name != memberParam.Name) return false;
                // #27⑦：可变/具名可变形状必须同形——普通参数与包参数不得
                // 误判同形（类型槽在包形态下是元素类型，仅比类型不够）
                if (proxyParam.IsVariadic != memberParam.IsVariadic) return false;
                if (proxyParam.IsNamedVariadic != memberParam.IsNamedVariadic) return false;
                if (!ReferenceEquals(
                        SubstituteViaApplication(proxyParam.Type, application, env),
                        memberParam.Type))
                {
                    return false;
                }
            }
            return ReferenceEquals(
                SubstituteViaApplication(proxy.ReturnType, application, env), member.ReturnType);
        }

        public static SemanticSymbol? SubstituteViaApplication(SemanticSymbol? type,
            WrapperApplication application, ResolveEnvironment env)
        {
            if (application.Wrapper.ConstructedFrom is { } definition)
            {
                return env.Substitute(type, definition, application.Wrapper);
            }
            return type;
        }

        public static bool ContainsErrorType(MethodSymbol method)
        {
            if (method.ReturnType is ErrorTypeSymbol) return true;
            foreach (var parameter in method.Parameters)
            {
                if (parameter.Type is ErrorTypeSymbol) return true;
            }
            return false;
        }
    }

    // P2 阶段：逐成员检查 specific 名中形状不符（措辞逐字保留）
    internal sealed class ProxyMatchChecker : ResolverVisitor<ProxyMatchChecker>
    {
        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.TypeEntries)
            {
                if (!entry.InGraph) continue;
                var host = (TypeSymbol)entry.Symbol;
                if (host.IsBuiltin || host.ConstructedFrom != null) continue;
                if (host.AppliedWrappers.Count == 0) continue;
                // §14.2：子类 Entity wrapper 拦截继承成员——形状校验须覆盖
                // BaseType 链（override 遮蔽后只查最派生版本）
                foreach (var member in EnumerateOwnAndInheritedMethods(host))
                {
                    var isOperator = member.Kind == MethodKind.Operator;
                    var specificName = (isOperator ? ".proxy.opr." : ".proxy.") + member.Name;
                    CheckMember(env, host, member, specificName);
                }
                foreach (var field in EnumerateOwnAndInheritedFields(host))
                {
                    if (field.Getter is { } getter)
                    {
                        CheckMember(env, host, getter, ".proxy.get." + field.Name);
                    }
                    if (field.Setter is { } setter)
                    {
                        CheckMember(env, host, setter, ".proxy.set." + field.Name);
                    }
                }
            }
        }

        // 自身 + BaseType 链上可拦截的实例方法（regular/operator；跳过
        // proxy 模板/static/native/abstract/无体/init）。同名同形参名由派生遮蔽。
        internal static IEnumerable<MethodSymbol> EnumerateOwnAndInheritedMethods(TypeSymbol host)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var t = host; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                if (def.IsBuiltin) break;
                foreach (var member in def.Methods)
                {
                    if (member.Kind != MethodKind.Regular && member.Kind != MethodKind.Operator)
                    {
                        continue;
                    }
                    if (member.ProxyTemplate != null || member.IsStatic || member.IsNative
                        || member.IsAbstract || !member.HasBody)
                    {
                        continue;
                    }
                    if (member.Kind == MethodKind.Init || member.Name == "init")
                    {
                        continue;
                    }
                    var key = member.Kind + ":" + member.Name + "(" +
                        string.Join(",", member.Parameters.Select(p => p.Name)) + ")";
                    if (!seen.Add(key)) continue;
                    yield return member;
                }
            }
        }

        internal static IEnumerable<FieldSymbol> EnumerateOwnAndInheritedFields(TypeSymbol host)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var t = host; t != null; t = t.BaseType)
            {
                var def = t.ConstructedFrom ?? t;
                if (def.IsBuiltin) break;
                foreach (var field in def.Fields)
                {
                    if (field.IsStatic) continue;
                    if (!seen.Add(field.Name)) continue;
                    yield return field;
                }
            }
        }

        private static void CheckMember(ResolveEnvironment env, TypeSymbol host,
            MethodSymbol member, string specificName)
        {
            if (ProxyMatching.ContainsErrorType(member)) return;
            if (!env.EntryOfSymbol.ContainsKey(member)) return;
            foreach (var application in host.AppliedWrappers)
            {
                if (!ProxyMatching.SpecificShapeMismatch(host, member, application, env,
                        specificName, out var mismatchedProxy))
                {
                    continue;
                }
                var wrapperDef = application.WrapperDefinition;
                env.Error(env.EntryOfSymbol[member].Node.Span,
                    $"Specific proxy '{mismatchedProxy!.Name}' on wrapper '{wrapperDef.Name}' " +
                    $"does not match the shape of member '{member.Name}' of " +
                    $"'{host.Name}' (§14.2)");
            }
        }
    }
}
