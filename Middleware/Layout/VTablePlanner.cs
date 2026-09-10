using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// vtable / iMap / 槽 0 分发器标记与接口实现段。
    /// </summary>
    internal static class VTablePlanner
    {
        // ===== 辅助 =====

        // 构造/非构造接口 iMap 段：闭合构造以具化 canonical 为键并确保
        // 接口空壳计划入表；开放构造（模板上的 I<T>）仍跳过
        internal static void AppendInterfaceSegment(List<string> slots, List<(string, int)> iMap,
            string ifaceRef, MwSymbolTable symbols, LayoutPlanTable table, string ownerCanonical,
            IReadOnlySet<string>? bodies)
        {
            var normalized = MwTypeKey.Normalize(ifaceRef);
            if (ConstructedTypeCollector.IsConstructed(normalized)
                && !GenericAbi.IsClosedConstructed(normalized))
            {
                // 开放 I\<T\>：iMap 仍挂接口模板段（invoke 擦成 I），
                // 否则 MapEnumerator 模板对 IEnumerator 查表失败。
                if (symbols.FindTypeByRef(normalized) is { } openIface
                    && openIface.Declaration.Kind == BilTypeKind.Interface)
                {
                    AppendInterfaceSegment(slots, iMap, openIface.Canonical, symbols, table,
                        ownerCanonical, bodies);
                }
                return;
            }
            if (symbols.FindTypeByRef(normalized) is not { IsExternal: false } ifaceType
                || ifaceType.Declaration.Kind != BilTypeKind.Interface)
            {
                return;
            }
            var imapKey = GenericAbi.IsClosedConstructed(normalized)
                ? normalized
                : ifaceType.Canonical;
            // 菱形去重：同一父接口只占一段
            foreach (var (existing, _) in iMap)
            {
                if (existing == imapKey || existing == ifaceType.Canonical)
                {
                    return;
                }
            }
            if (GenericAbi.IsClosedConstructed(normalized))
            {
                EnsureConstructedInterfacePlan(normalized, ifaceType, symbols, table);
            }
            var subst = ConstructedTypeCollector.BuildSubstitution(normalized, ifaceType.Declaration);
            // 先展开传递父接口（ExtendsType + ImplementsTypes），再写本接口段
            if (ifaceType.Declaration.ExtendsType is { } extends)
            {
                AppendInterfaceSegment(slots, iMap,
                    ConstructedTypeCollector.Substitute(extends, subst),
                    symbols, table, ownerCanonical, bodies);
            }
            foreach (var parent in ifaceType.Declaration.ImplementsTypes)
            {
                AppendInterfaceSegment(slots, iMap,
                    ConstructedTypeCollector.Substitute(parent, subst),
                    symbols, table, ownerCanonical, bodies);
            }
            var baseOffset = slots.Count;
            foreach (var ifaceMethod in LayoutEngine.InstanceMethods(ifaceType))
            {
                var impl = FindInterfaceImpl(slots, symbols, ifaceMethod, subst)
                    ?? DefaultMethodOf(ifaceMethod, bodies)
                    ?? throw new MwNotSupportedException(
                        $"MW4 接口方法未实现: {ifaceMethod.Canonical}（{ownerCanonical}）");
                slots.Add(impl);
            }
            iMap.Add((imapKey, baseOffset));
            // 前端 invoke 常把 I<i32> 擦成模板 I；补模板键别名使
            // TypeSheetFor(I) 与具化键同槽（VM 亦按声明键而非具化键）
            if (imapKey != ifaceType.Canonical)
            {
                iMap.Add((ifaceType.Canonical, baseOffset));
            }
        }

        internal static void EnsureConstructedInterfacePlan(string canonical, MwTypeSymbol template,
            MwSymbolTable symbols, LayoutPlanTable table)
        {
            if (table.Find(canonical) != null)
            {
                return;
            }
            var templatePlan = table.Find(GenericAbi.PlanKey(template));
            if (templatePlan == null || templatePlan.Kind != TypeLayoutKind.Interface)
            {
                return;
            }
            table.Add(new TypeLayoutPlan(new MwTypeSymbol(canonical, template),
                TypeLayoutKind.Interface, 0, 1, templatePlan.TypeFlags,
                templatePlan.Fields, templatePlan.VTableSlots,
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                templatePlan.EnumCases, null, ifaceClosure:
                CollectIfaceClosure(canonical, template, symbols)));
        }

        // 接口默认方法：模块内有 fn 体则 iMap 槽指向接口方法自身（VM 同口径）
        internal static string? DefaultMethodOf(MwMemberSymbol ifaceMethod,
            IReadOnlySet<string>? bodies) =>
            bodies != null && bodies.Contains(ifaceMethod.Canonical)
                ? ifaceMethod.Canonical
                : null;

        // 传递 implements 闭包（前端 OverrideChecker.InterfaceClosure 同口径）：
        // 沿宿主/基类链收 implements，再沿接口 ExtendsType+ImplementsTypes 展开。
        // 开放构造（I<T>）跳过；闭合构造保留具化键。
        internal static IReadOnlyList<string> CollectIfaceClosure(string typeRef,
            MwTypeSymbol type, MwSymbolTable symbols)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(System.StringComparer.Ordinal);
            var stack = new Stack<(string Ref, MwTypeSymbol Type)>();
            ConsiderHosts(typeRef, type, symbols, visited, result, stack);
            while (stack.Count > 0)
            {
                var (ifaceRef, ifaceType) = stack.Pop();
                var subst = ConstructedTypeCollector.BuildSubstitution(ifaceRef,
                    ifaceType.Declaration);
                if (ifaceType.Declaration.ExtendsType is { } extends)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(extends, subst),
                        symbols, visited, result, stack);
                }
                foreach (var parent in ifaceType.Declaration.ImplementsTypes)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(parent, subst),
                        symbols, visited, result, stack);
                }
            }
            return result;
        }

        internal static void ConsiderHosts(string typeRef, MwTypeSymbol type,
            MwSymbolTable symbols, HashSet<string> visited, List<string> result,
            Stack<(string, MwTypeSymbol)> stack)
        {
            var currentRef = typeRef;
            var current = type;
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            while (current != null && seen.Add(MwTypeKey.Normalize(currentRef)))
            {
                var subst = ConstructedTypeCollector.BuildSubstitution(currentRef,
                    current.Declaration);
                if (current.Declaration.Kind == BilTypeKind.Interface
                    && current.Declaration.ExtendsType is { } selfExtends)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(selfExtends, subst),
                        symbols, visited, result, stack);
                }
                foreach (var iface in current.Declaration.ImplementsTypes)
                {
                    ConsiderIface(ConstructedTypeCollector.Substitute(iface, subst),
                        symbols, visited, result, stack);
                }
                if (current.Declaration.Kind == BilTypeKind.Interface
                    || current.Declaration.ExtendsType is not { } baseRef)
                {
                    break;
                }
                currentRef = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(baseRef, subst));
                current = symbols.FindTypeByRef(currentRef);
            }
        }

        internal static void ConsiderIface(string typeRef, MwSymbolTable symbols,
            HashSet<string> visited, List<string> result,
            Stack<(string, MwTypeSymbol)> stack)
        {
            var normalized = MwTypeKey.Normalize(typeRef);
            if (ConstructedTypeCollector.IsConstructed(normalized)
                && !GenericAbi.IsClosedConstructed(normalized))
            {
                // 开放 I\<T\>：具化键跳过，但模板 I 仍进闭包，否则泛型
                // 类（MapEnumerator）try_cast 到擦除 IEnumerator 失败。
                if (symbols.FindTypeByRef(normalized) is { } openIface
                    && openIface.Declaration.Kind == BilTypeKind.Interface
                    && visited.Add(openIface.Canonical))
                {
                    result.Add(openIface.Canonical);
                }
                return;
            }
            if (symbols.FindTypeByRef(normalized) is not { } ifaceType
                || ifaceType.Declaration.Kind != BilTypeKind.Interface)
            {
                return;
            }
            var key = GenericAbi.IsClosedConstructed(normalized)
                ? normalized
                : ifaceType.Canonical;
            if (!visited.Add(key))
            {
                return;
            }
            result.Add(key);
            // 前端 invoke/cast 常把 I<T> 擦成模板 I（与 iMap 模板键别名
            // 同口径）；ifaceClosure 不含模板键时 try_cast 把
            // MapEnumerator\<K,V\> 当成无法转 IEnumerator。
            if (key != ifaceType.Canonical && visited.Add(ifaceType.Canonical))
            {
                result.Add(ifaceType.Canonical);
            }
            stack.Push((key, ifaceType));
        }

        // 先模板签名直中（G<T>:I<T> 槽仍是 .generic 占位）；再代入+归一
        // （C:I<i32> 的 pick(x:.i32) 对 I.pick(x:T)）
        internal static string? FindInterfaceImpl(List<string> slots, MwSymbolTable symbols,
            MwMemberSymbol ifaceMethod, Dictionary<string, string>? subst)
        {
            var raw = ifaceMethod.SignatureKey;
            var exact = slots.Find(s => s != LayoutEngine.InitDispatchSlot && KeyOf(symbols, s) == raw);
            if (exact != null)
            {
                return exact;
            }
            var want = NormalizeSignatureKey(ConstructedTypeCollector.Substitute(raw, subst));
            return slots.Find(s => s != LayoutEngine.InitDispatchSlot
                && NormalizeSignatureKey(KeyOf(symbols, s)) == want);
        }

        internal static string NormalizeSignatureKey(string key)
        {
            var open = key.IndexOf('(');
            var close = key.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                return key;
            }
            var inner = key.Substring(open + 1, close - open - 1);
            if (inner.Length == 0)
            {
                return key;
            }
            var parts = BilVerificationContext.SplitTopLevel(inner);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var colon = part.IndexOf(':');
                parts[i] = colon < 0
                    ? MwTypeKey.Normalize(part)
                    : part.Substring(0, colon + 1) + MwTypeKey.Normalize(part.Substring(colon + 1));
            }
            return key.Substring(0, open + 1) + string.Join(",", parts) + key.Substring(close);
        }
        // 槽 0 插入合成标记；基类已带则不重复（发射期按本类型填分发器）
        internal static void EnsureInitDispatchSlot(List<string> slots)
        {
            if (slots.Count > 0 && slots[0] == LayoutEngine.InitDispatchSlot)
            {
                return;
            }
            slots.Insert(0, LayoutEngine.InitDispatchSlot);
        }

        // 槽内 canonical 的签名键（经符号表成员反查；槽内符号恒已登记）
        internal static string KeyOf(MwSymbolTable symbols, string canonical) =>
            symbols.FindMember(canonical)!.SignatureKey;

        // override 匹配：精确签名键，或把继承槽中的泛型占位按结构统一到
        // 成员签名。绝不能仅凭名字+参数个数覆盖，否则换了参数类型的伪
        // override 会把调用方引到 ABI 不兼容实现。
        internal static bool CompatibleSignature(MwSymbolTable symbols, string slotCanonical,
            string memberKey)
        {
            if (slotCanonical == LayoutEngine.InitDispatchSlot)
            {
                return false;
            }
            var slotKey = KeyOf(symbols, slotCanonical);
            if (slotKey == memberKey)
            {
                return true;
            }
            if (!slotKey.Contains(".generic<", System.StringComparison.Ordinal)
                || (memberKey.StartsWith("$", System.StringComparison.Ordinal)
                    && !memberKey.StartsWith("$call(", System.StringComparison.Ordinal)))
            {
                return false;
            }
            var slotParen = slotKey.IndexOf('(');
            var memParen = memberKey.IndexOf('(');
            if (slotParen < 0 || memParen < 0
                || slotKey.Substring(0, slotParen) != memberKey.Substring(0, memParen))
            {
                return false;
            }
            var slotParameters = ParameterTypes(slotKey);
            var memberParameters = ParameterTypes(memberKey);
            if (slotParameters.Count != memberParameters.Count) return false;
            var substitution = new Dictionary<string, string>(System.StringComparer.Ordinal);
            for (var i = 0; i < slotParameters.Count; i++)
            {
                if (!TryMatchTypePattern(slotParameters[i], memberParameters[i], substitution))
                {
                    return false;
                }
            }
            return true;
        }

        private static List<string> ParameterTypes(string signatureKey)
        {
            var open = signatureKey.IndexOf('(');
            var close = signatureKey.LastIndexOf(')');
            var result = new List<string>();
            if (open < 0 || close <= open + 1) return result;
            foreach (var part in BilVerificationContext.SplitTopLevel(
                signatureKey.Substring(open + 1, close - open - 1)))
            {
                var colon = part.IndexOf(':');
                if (colon < 0) return new List<string>();
                result.Add(MwTypeKey.Normalize(part.Substring(colon + 1)));
            }
            return result;
        }

        private static bool TryMatchTypePattern(string pattern, string actual,
            Dictionary<string, string> substitution)
        {
            pattern = MwTypeKey.Normalize(pattern);
            actual = MwTypeKey.Normalize(actual);
            if (GenericAbi.TryPlaceholderName(pattern, out var name))
            {
                if (substitution.TryGetValue(name, out var existing))
                {
                    return existing == actual;
                }
                substitution[name] = actual;
                return true;
            }
            if (pattern == actual) return true;
            var patternArgs = ConstructedTypeCollector.TypeArgumentsOf(pattern);
            var actualArgs = ConstructedTypeCollector.TypeArgumentsOf(actual);
            if (patternArgs.Count == 0 || patternArgs.Count != actualArgs.Count
                || BilVerificationContext.StripTypeArguments(pattern)
                    != BilVerificationContext.StripTypeArguments(actual))
            {
                return false;
            }
            for (var i = 0; i < patternArgs.Count; i++)
            {
                if (!TryMatchTypePattern(patternArgs[i], actualArgs[i], substitution))
                {
                    return false;
                }
            }
            return true;
        }

        internal static int ParameterCount(string signatureKey)
        {
            var open = signatureKey.IndexOf('(');
            var close = signatureKey.LastIndexOf(')');
            if (open < 0 || close <= open + 1)
            {
                return 0;
            }
            return BilVerificationContext.SplitTopLevel(
                signatureKey.Substring(open + 1, close - open - 1)).Count;
        }
    }
}
