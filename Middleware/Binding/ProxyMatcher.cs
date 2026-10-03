using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Binding
{
    public enum MwProxyMatchKind
    {
        None,
        Specific,
        Wildcard,
    }

    /// <summary>
    /// proxy 匹配（MW10）：规则对齐前端 <c>ProxyMatching</c>，不重跑 overload
    /// ranking。输入是已驻留的 BIL 成员符号；specific 名中形状不符 → None
    ///（不落 wildcard）。
    /// </summary>
    public static class ProxyMatcher
    {
        public static MwProxyMatchKind MatchMethod(MwTypeSymbol wrapper, MwMemberSymbol member)
        {
            var isOperator = IsOperator(member);
            var specificName = (isOperator ? ".proxy.opr." : ".proxy.") + SimpleName(member);
            var specific = FindProxy(wrapper, specificName, BilProxyKind.Specific);
            if (specific != null)
            {
                if (IsGenericMember(member))
                {
                    return FindProxy(wrapper, isOperator ? ".proxy.opr.*" : ".proxy.*",
                        BilProxyKind.Wildcard) != null
                        ? MwProxyMatchKind.Wildcard
                        : MwProxyMatchKind.None;
                }
                return ShapeMatches(specific, member)
                    ? MwProxyMatchKind.Specific
                    : MwProxyMatchKind.None;
            }
            return FindProxy(wrapper, isOperator ? ".proxy.opr.*" : ".proxy.*",
                BilProxyKind.Wildcard) != null
                ? MwProxyMatchKind.Wildcard
                : MwProxyMatchKind.None;
        }

        public static MwProxyMatchKind MatchFieldAccess(MwTypeSymbol wrapper, bool isSet)
        {
            var specificPrefix = isSet ? ".proxy.set." : ".proxy.get.";
            // 字段 specific 名由调用方按字段名拼；此处只回答「有无类别 wildcard /
            // Value 的无名字 .proxy.get/.proxy.set」
            var wildcard = isSet ? ".proxy.set.*" : ".proxy.get.*";
            if (FindProxy(wrapper, wildcard, BilProxyKind.Wildcard) != null)
            {
                return MwProxyMatchKind.Wildcard;
            }
            var valueName = isSet ? ".proxy.set" : ".proxy.get";
            return FindProxy(wrapper, valueName, BilProxyKind.Specific) != null
                || FindProxy(wrapper, valueName, BilProxyKind.Wildcard) != null
                ? MwProxyMatchKind.Specific
                : MwProxyMatchKind.None;
        }

        public static MwProxyMatchKind MatchFieldAccess(MwTypeSymbol wrapper, string fieldName,
            bool isSet)
        {
            var specificName = (isSet ? ".proxy.set." : ".proxy.get.") + BilLogicalName.Of(fieldName);
            if (FindProxy(wrapper, specificName, BilProxyKind.Specific) != null)
            {
                return MwProxyMatchKind.Specific;
            }
            return MatchFieldAccess(wrapper, isSet);
        }

        public static MwMemberSymbol? FindSpecificMethodProxy(MwTypeSymbol wrapper,
            MwMemberSymbol member)
        {
            var isOperator = IsOperator(member);
            var specificName = (isOperator ? ".proxy.opr." : ".proxy.") + SimpleName(member);
            return FindProxy(wrapper, specificName, BilProxyKind.Specific);
        }

        // Value wrapper 的无名 .proxy.get/.proxy.set 模板查找
        //（VM VmContext.FindWrapperProxy 同口径：精确名命中，specific /
        // wildcard 类别修饰符皆可）；字段-Value 链烘焙（刀2）消费
        public static MwMemberSymbol? FindValueProxy(MwTypeSymbol wrapper, bool isSet)
        {
            var name = isSet ? ".proxy.set" : ".proxy.get";
            return FindProxy(wrapper, name, BilProxyKind.Specific)
                ?? FindProxy(wrapper, name, BilProxyKind.Wildcard);
        }

        // Entity 字段 get/set 的逐层 proxy 模板查找（刀3b；VM
        // VmWrapperDispatch.FindProxy 的 Get/Set 类别同口径）：按字段名
        // specific（.proxy.get|set.<名>）优先、wildcard（.proxy.get|set.*）
        // 兜底，都无 → null（None 透明跳过）；不回落无名 .proxy.get/.proxy.set
        //（那是 Value 面形状）
        public static MwMemberSymbol? FindEntityFieldProxy(MwTypeSymbol wrapper, string fieldName,
            bool isSet, out bool isWildcard)
        {
            isWildcard = false;
            var specific = FindProxy(wrapper,
                (isSet ? ".proxy.set." : ".proxy.get.") + BilLogicalName.Of(fieldName), BilProxyKind.Specific);
            if (specific != null)
            {
                return specific;
            }
            var wildcard = FindProxy(wrapper, isSet ? ".proxy.set.*" : ".proxy.get.*",
                BilProxyKind.Wildcard);
            if (wildcard != null)
            {
                isWildcard = true;
                return wildcard;
            }
            return null;
        }

        public static MwMemberSymbol? FindProxy(MwTypeSymbol wrapper, string proxyName,
            BilProxyKind kind)
        {
            foreach (var member in wrapper.Members)
            {
                if (member.Declaration.Kind != BilMemberKind.Method)
                {
                    continue;
                }
                if (ProxyOperatorName(member) != proxyName)
                {
                    continue;
                }
                foreach (var modifier in member.Declaration.Modifiers)
                {
                    if (modifier is BilWrapperProxyModifier proxy && proxy.Kind == kind)
                    {
                        return member;
                    }
                }
            }
            return null;
        }

        public static string ProxyOperatorName(MwMemberSymbol member)
        {
            var canonical = member.Canonical;
            var marker = canonical.IndexOf("$$", System.StringComparison.Ordinal);
            if (marker < 0)
            {
                return "";
            }
            var open = canonical.IndexOf('(', marker);
            if (open < 0)
            {
                return "";
            }
            // 私有代理的模块后缀属于链接身份；匹配仍只在该 wrapper 的成员中进行。
            return BilLogicalName.Of(canonical.Substring(marker + 2, open - marker - 2));
        }

        private static bool ShapeMatches(MwMemberSymbol proxy, MwMemberSymbol member)
        {
            var proxySig = CanonicalSignature.Parse(proxy.Canonical);
            var memberSig = CanonicalSignature.Parse(member.Canonical);
            if (proxySig.Parameters.Count != memberSig.Parameters.Count)
            {
                return false;
            }
            for (var i = 0; i < proxySig.Parameters.Count; i++)
            {
                if (proxySig.Parameters[i].Name != memberSig.Parameters[i].Name)
                {
                    return false;
                }
            }
            return true;
        }

        private static string SimpleName(MwMemberSymbol member)
        {
            var key = member.SignatureKey;
            var open = key.IndexOf('(');
            var name = open < 0 ? key : key.Substring(0, open);
            if (name.StartsWith(".static.", System.StringComparison.Ordinal))
            {
                name = name.Substring(".static.".Length);
            }
            return BilLogicalName.Of(name.StartsWith('$') ? name.Substring(1) : name);
        }

        private static bool IsOperator(MwMemberSymbol member)
        {
            foreach (var modifier in member.Declaration.Modifiers)
            {
                if (modifier is BilOperatorModifier)
                {
                    return true;
                }
            }
            return member.Canonical.Contains("$$", System.StringComparison.Ordinal)
                && BilLogicalName.Method(member.Canonical) != "call";
        }

        private static bool IsGenericMember(MwMemberSymbol member) =>
            member.Canonical.Contains('<', System.StringComparison.Ordinal);
    }
}
