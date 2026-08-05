namespace LatteCompiler
{
    // 函数体内类型引用解析（迁移自旧 BindSession.ResolveBodyTypeReference）：
    // 委托 NameResolver（P2 同设施，诊断按 P3 phase 落袋）；泛型参数命中
    // 按 S9 归口诊断。
    // S8e（SYNTAX §16.1）：类型引用是使用点——命中后经 AccessChecker
    // 判定可见性（变量标注/cast/catch/new 统一收口于此；is/supers/with
    // 与 typeOf 的不落袋试探不经此路径，保持试探纯净）
    internal static class TypeReferences
    {
        public static TypeSymbol? Resolve(TypeReferenceASTNode typeRef, CharRange? span,
            BindFunctionFrame frame, BindEnvironment env)
        {
            var resolved = env.Names.ResolveTypeReference(typeRef, frame.FileCtx,
                frame.DeclaringType, frame.Method, span);
            if (resolved is TypeSymbol type)
            {
                if (!AccessChecker.IsTypeAccessible(type, frame.FileCtx.File,
                    frame.FileCtx.Namespace, frame.DeclaringType))
                {
                    env.Error(span, AccessChecker.InaccessibleMessage(type));
                    return null;
                }
                return type;
            }
            env.Error(span, "P3: generic type parameters are not supported yet (S9)");
            return null;
        }
    }
}
