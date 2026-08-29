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
            if (GenericAbi.IsClosedConstructed(normalized))
            {
                EnsureConstructedInterfacePlan(normalized, ifaceType, symbols, table);
            }
            var subst = ConstructedTypeCollector.BuildSubstitution(normalized, ifaceType.Declaration);
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

        // override 匹配：精确签名键，或名+顶层参数个数（具化 $$call(x:.i32)
        // 对 Func$$call(arg0:.generic<T0>) 与泛型基类具化 override
        // Base<T>.m(x:T) → m(x:.i32)）。参数个数兜底对运算符（$$ 族键以 $
        // 开头）禁用：普通运算符的重载（同名不同参，如 $$equals(other:Base)
        // 对 $$equals(other:Derived)）参数个数相同但语义独立，误中会错盖
        // 基槽；$$call 保留兜底（具化槽匹配的原设计用途）
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
            if (memberKey.StartsWith("$", System.StringComparison.Ordinal)
                && !memberKey.StartsWith("$call(", System.StringComparison.Ordinal))
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
            return ParameterCount(slotKey) == ParameterCount(memberKey);
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
