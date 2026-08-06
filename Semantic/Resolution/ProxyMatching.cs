namespace LatteCompiler
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

        // 宿主是否具备方法类别 `.proxy.*` wildcard（降级资格，沿应用列表）
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

        // 沿 receiver 静态类型链（每步 ConstructedFrom 回退定义级）查降级资格
        public static bool IsDowngradeEligible(TypeSymbol receiverType)
        {
            for (var t = receiverType; t != null; t = t.BaseType)
            {
                var definition = t.ConstructedFrom ?? t;
                if (HasMethodWildcardProxy(definition)) return true;
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
                if (proxy.Parameters[i].Name != member.Parameters[i].Name) return false;
                if (!ReferenceEquals(
                        SubstituteViaApplication(proxy.Parameters[i].Type, application, env),
                        member.Parameters[i].Type))
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
                foreach (var member in host.Methods)
                {
                    if (member.Kind != MethodKind.Regular && member.Kind != MethodKind.Operator)
                    {
                        continue;
                    }
                    if (member.Name.StartsWith('.') || member.IsStatic || member.IsNative
                        || member.IsAbstract || !member.HasBody)
                    {
                        continue;
                    }
                    if (member.Parameters.Any(p => p.IsVariadic || p.IsNamedVariadic)) continue;
                    var isOperator = member.Kind == MethodKind.Operator;
                    var specificName = (isOperator ? ".proxy.opr." : ".proxy.") + member.Name;
                    CheckMember(env, host, member, specificName);
                }
                foreach (var field in host.Fields)
                {
                    if (field.IsStatic || field.Name.StartsWith('.')) continue;
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

        private static void CheckMember(ResolveEnvironment env, TypeSymbol host,
            MethodSymbol member, string specificName)
        {
            if (ProxyMatching.ContainsErrorType(member)) return;
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
