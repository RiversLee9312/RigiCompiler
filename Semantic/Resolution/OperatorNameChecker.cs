namespace RigiCompiler
{
    // 用户 operator 名字白名单（SYNTAX §13.2：不可自定义新运算符名称）。
    // Kind == Operator 的声明必须是固定映射名、castTo/castFrom，或
    // `.proxy.*` 前缀（wrapper 模板，与 ConversionOperatorChecker 同登记路径）。
    internal sealed class OperatorNameChecker : ResolverVisitor<OperatorNameChecker>
    {
        private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
        {
            "plus", "minus", "times", "div", "opposite",
            "and", "or", "not",
            "leftShift", "rightShift", "unsignedRightShift",
            "bitwiseAnd", "bitwiseOr", "bitwiseXor", "bitwiseNot",
            "equals", "compareTo",
            "getAtIndex", "setAtIndex",
            "EnumerateInRange",
            "call",
            "castTo", "castFrom",
        };

        protected override void VisitCore(ResolveEnvironment env)
        {
            foreach (var entry in env.Entries)
            {
                if (!entry.InGraph) continue;
                if (entry.Symbol is not MethodSymbol { Kind: MethodKind.Operator } method)
                {
                    continue;
                }
                var name = method.Name;
                if (name.StartsWith(".proxy.", StringComparison.Ordinal)) continue;
                if (AllowedNames.Contains(name)) continue;
                env.Error(entry.Node.Span,
                    $"'{name}' is not a recognized operator name");
            }
        }
    }
}
