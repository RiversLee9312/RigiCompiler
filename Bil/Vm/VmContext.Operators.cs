using System.Globalization;
using System.IO;
using System.Text;

namespace RigiCompiler.Bil.Vm
{
    public sealed partial class VmContext
    {
        // Operators 职责；与主文件共享同一类型、字段及生命周期。

        public string? FindOperator(string ownerType, string operatorName,
            IReadOnlyList<VmValue> valueArguments)
        {
            return FindOperatorMember(ownerType, operatorName, valueArguments, callOnly: false);
        }

        private string? FindOperatorMember(string ownerType, string operatorName,
            IReadOnlyList<VmValue> valueArguments, bool callOnly)
        {
            var current = ownerType;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (visited.Add(StripTypeArguments(current)))
            {
                var declaration = FindType(current);
                if (declaration == null)
                {
                    break;
                }
                var found = MatchOperatorOn(declaration, operatorName, valueArguments,
                    skipGenericPrefix: callOnly);
                if (found != null)
                {
                    return found;
                }
                if (declaration.ExtendsType == null)
                {
                    break;
                }
                current = declaration.ExtendsType;
            }
            var needle = "$" + operatorName + "(";
            var hosts = visited;
            hosts.Add(StripTypeArguments(ownerType));
            foreach (var member in _members.Values)
            {
                if (!member.Symbol.Contains(needle, StringComparison.Ordinal))
                {
                    continue;
                }
                if (callOnly
                    && !member.Modifiers.Any(m => m is BilOperatorModifier { Name: "call" })
                    && !member.Symbol.Contains("$call(", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!BilVerificationContext.TryParseMethodSymbol(member.Symbol,
                        out var owner, out _, out _, out _))
                {
                    continue;
                }
                if (!hosts.Contains(owner) && !hosts.Contains(StripTypeArguments(owner)))
                {
                    continue;
                }
                if (OperatorParamsMatch(member.Symbol, valueArguments,
                        skipGenericPrefix: callOnly))
                {
                    return member.Symbol;
                }
            }
            return null;
        }

        private string? MatchOperatorOn(BilTypeDeclaration declaration, string operatorName,
            IReadOnlyList<VmValue> valueArguments, bool skipGenericPrefix)
        {
            foreach (var member in declaration.Members)
            {
                if (member is not BilSimpleMemberDeclaration simple)
                {
                    continue;
                }
                var isOperator = false;
                foreach (var modifier in simple.Modifiers)
                {
                    if (modifier is BilOperatorModifier op && op.Name == operatorName)
                    {
                        isOperator = true;
                        break;
                    }
                }
                if (!isOperator && !simple.Symbol.Contains("$" + operatorName + "(",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (OperatorParamsMatch(simple.Symbol, valueArguments, skipGenericPrefix))
                {
                    return simple.Symbol;
                }
            }
            return null;
        }

        private bool OperatorParamsMatch(string methodSymbol, IReadOnlyList<VmValue> valueArguments,
            bool skipGenericPrefix = false, string? receiverType = null)
        {
            if (!BilVerificationContext.TryParseMethodSymbol(methodSymbol,
                    out _, out _, out var parameters, out _))
            {
                return false;
            }
            var ordinary = new List<(string Name, string TypeRef)>();
            // 泛型宿主的 callable 参数按实际 receiver 具化后匹配。
            var receiverDeclaration = receiverType == null ? null : FindType(receiverType);
            var substitution = receiverDeclaration == null ? null
                : VmTypeSheetBuilder.BuildSubstitution(receiverType!, receiverDeclaration);
            foreach (var parameter in parameters)
            {
                if (parameter.Name.StartsWith(".generic.", StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                    || parameter.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                {
                    continue;
                }
                ordinary.Add(substitution == null ? parameter : (parameter.Name,
                    VmTypeSheetBuilder.SubstituteGenericArguments(parameter.TypeRef, substitution)));
            }
            if (skipGenericPrefix)
            {
                // §15.3：泛型 $$call 调用点前部平铺 typeid/包前缀；按 fn
                // 定义侧 .generic.* / 值包 hidden 条目数跳过后再逐值比对
                // （对齐 BilVerifier.TryFindCallOperator）。
                var genericHidden = new List<BilArgDeclaration>();
                var packArguments = new List<BilArgDeclaration>();
                var callee = FindFunction(methodSymbol);
                if (callee != null)
                {
                    foreach (var arg in callee.Args)
                    {
                        if (arg.Name.StartsWith(".generic.", StringComparison.Ordinal))
                        {
                            // 宿主 typeid 由 PushFrame 从 this 注入，不占调用实参数。
                            var name = arg.Name.Substring(".generic.".Length);
                            if (substitution == null || !substitution.ContainsKey(name))
                                genericHidden.Add(arg);
                        }
                        else if (arg.Name.StartsWith(".vargs.", StringComparison.Ordinal)
                            || arg.Name.StartsWith(".kwargs.", StringComparison.Ordinal))
                        {
                            packArguments.Add(arg);
                        }
                    }
                }
                var expectedCount = genericHidden.Count + ordinary.Count + packArguments.Count;
                if (valueArguments.Count != expectedCount)
                {
                    return false;
                }
                var valueStart = genericHidden.Count;
                for (var i = 0; i < ordinary.Count; i++)
                {
                    if (!TypeAssignable(valueArguments[valueStart + i].TypeRef,
                            ordinary[i].TypeRef))
                    {
                        return false;
                    }
                }
                for (var i = 0; i < packArguments.Count; i++)
                {
                    if (!TypeAssignable(
                            valueArguments[valueStart + ordinary.Count + i].TypeRef,
                            packArguments[i].TypeRef))
                    {
                        return false;
                    }
                }
                return true;
            }
            // 运算符：值实参不含 hidden typeid；泛型占位由 TypesEqual
            // 的 .generic< 降级匹配；typeid 在命中后由 InjectOperatorTypeIds
            // 从实参类型结构推断补入。
            if (valueArguments.Count != ordinary.Count)
            {
                return false;
            }
            for (var i = 0; i < ordinary.Count; i++)
            {
                if (!TypeAssignable(valueArguments[i].TypeRef, ordinary[i].TypeRef))
                {
                    return false;
                }
            }
            return true;
        }

        // 运算符实参：实际 typeid 可赋给形参类型（精确相等、.generic 占位、
        // 或沿 extends/implements 闭包命中——A 实现 Addable 时 plus(Addable) 可派发）
        private bool TypeAssignable(string from, string to)
        {
            if (TypesEqual(from, to)) return true;
            // 嵌套类型同样需要归一化内建别名（例如 nullable<core::i32>）。
            if (BilVerificationContext.NormalizeTypeRef(from)
                == BilVerificationContext.NormalizeTypeRef(to)) return true;
            if (from == ".null" && BilVerificationContext.NormalizeTypeRef(to)
                    .StartsWith("core::Nullable<", StringComparison.Ordinal)) return true;
            var normalizedTarget = BilVerificationContext.NormalizeTypeRef(to);
            if (normalizedTarget.StartsWith("core::Nullable<", StringComparison.Ordinal))
                return TypeAssignable(from, normalizedTarget.Substring(15, normalizedTarget.Length - 16));
            if (to is ".any" or "core::Any") return true;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            return TypeAssignableWalk(from, to, visited);
        }

        private bool TypeAssignableWalk(string current, string to, HashSet<string> visited)
        {
            if (!visited.Add(StripTypeArguments(current) + "\0" + current)) return false;
            if (TypesEqual(current, to)) return true;
            var declaration = FindType(current);
            if (declaration == null) return false;
            if (declaration.ExtendsType != null
                && TypeAssignableWalk(declaration.ExtendsType, to, visited))
            {
                return true;
            }
            foreach (var iface in declaration.ImplementsTypes)
            {
                if (TypeAssignableWalk(iface, to, visited)) return true;
            }
            return false;
        }

    }
}
