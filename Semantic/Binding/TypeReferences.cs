namespace LatteCompiler
{
    // 函数体内类型引用解析（迁移自旧 BindSession.ResolveBodyTypeReference）：
    // 委托 NameResolver（P2 同设施，诊断按 P3 phase 落袋）。S9a 起返回
    // SemanticSymbol：泛型参数（GenericParameterSymbol）按引用相等身份
    // 放行——`var x: T` / `x as T` / `new T()` 等形态（§7.5 投影）。
    // S8e（SYNTAX §16.1）：类型引用是使用点——命中后经 AccessChecker
    // 判定可见性（变量标注/cast/catch/new 统一收口于此；is/supers/with
    // 与 typeOf 的不落袋试探不经此路径，保持试探纯净）
    internal static class TypeReferences
    {
        public static SemanticSymbol? Resolve(TypeReferenceASTNode typeRef, CharRange? span,
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
                // 使用侧约束检查（S9b，SYNTAX §3.6）：构造类型实参须满足
                // 声明约束（含嵌套递归）；失败即拒绝（诊断已落袋）
                if (type.ConstructedFrom != null
                    && !GenericConstraints.CheckConstructedType(type, span, env))
                {
                    return null;
                }
                return type;
            }
            if (resolved is GenericParameterSymbol) return resolved;
            env.Error(span, "P3: generic type parameters are not supported yet (S9)");
            return null;
        }
    }
}
