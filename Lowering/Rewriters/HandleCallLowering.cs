namespace RigiCompiler
{
    // 源码 T 保留在私有全局 helper 的方法泛型，.handle 自身不具化。
    internal static class HandleCallLowering
    {
        internal static bool IsHandle(MethodSymbol method) => method.Owner?.BilAlias == ".handle";

        internal static MethodSymbol Helper(MethodSymbol method, LowerEnvironment env) =>
            env.Unit.Symbols.GlobalNamespace.ChildNamespaces.Single(n => n.Name == "core")
                .Methods.Single(m => m.Name == "handle_" + method.Name
                    && m.SourceFile?.IsCompilerLibrary == true);

        internal static IReadOnlyList<SemanticSymbol> TypeArguments(BoundExpression receiver) =>
            ((TypeSymbol)receiver.Type).TypeArguments
                ?? throw new CompilerInternalException("Handle 调用缺失目标类型");

        internal static List<LoweredExpression> Arguments(BoundNode origin,
            LoweredExpression receiver, IEnumerable<LoweredExpression> arguments,
            LowerEnvironment env)
        {
            var any = env.Unit.Symbols.Bootstrap.Any;
            var result = new List<LoweredExpression>
            {
                new LoweredCastExpression(origin, receiver, any, false, any),
            };
            result.AddRange(arguments);
            return result;
        }
    }
}
