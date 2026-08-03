namespace LatteCompiler
{
    // 函数体内类型引用解析（迁移自旧 BindSession.ResolveBodyTypeReference）：
    // 委托 NameResolver（P2 同设施，诊断按 P3 phase 落袋）；泛型参数命中
    // 按 S9 归口诊断
    internal static class TypeReferences
    {
        public static TypeSymbol? Resolve(TypeReferenceASTNode typeRef, CharRange? span,
            BindContext ctx, BindEnvironment env)
        {
            var resolved = env.Names.ResolveTypeReference(typeRef, ctx.FileCtx, ctx.DeclaringType,
                ctx.Method, span);
            if (resolved is TypeSymbol type) return type;
            env.Error(span, "P3: generic type parameters are not supported yet (S9)");
            return null;
        }
    }
}
