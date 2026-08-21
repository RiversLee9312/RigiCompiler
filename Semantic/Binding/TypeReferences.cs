namespace RigiCompiler
{
    // 函数体内类型引用解析（迁移自旧 BindSession.ResolveBodyTypeReference）：
    // 委托 NameResolver（P2 同设施，诊断按 P3 phase 落袋）。S9a 起返回
    // SemanticSymbol：泛型参数（GenericParameterSymbol）按引用相等身份
    // 放行——`var x: T` / `x as T` / `new T()` 等形态（§7.5 投影）。
    // S8e（SYNTAX §16.1）：类型引用是使用点——命中后经 AccessChecker
    // 判定可见性（变量标注/cast/catch/new 统一收口于此；is/supers/with
    // 与 typeOf 的不落袋试探不经此路径，保持试探纯净）。F1/V-A 起为
    // 递归口径（FindInaccessibleType：构造实参与嵌套宿主链参检，命中
    // 报最深不可见者）；reportDedup 非空时登记命中类型，供同函数体内
    // 推断/直链下游位置去重（c6 级联控制，写出点本身恒报）
    internal static class TypeReferences
    {
        public static SemanticSymbol? Resolve(TypeReferenceASTNode typeRef, CharRange? span,
            BindFunctionFrame frame, BindEnvironment env, BindContext? reportDedup = null)
        {
            var resolved = env.Names.ResolveTypeReference(typeRef, frame.FileCtx,
                frame.DeclaringType, frame.Method, span);
            StaticGenericRules.CheckFrameUse(resolved, frame, span, env.Error);
            if (resolved is TypeSymbol type)
            {
                var inaccessible = AccessChecker.FindInaccessibleType(type, frame.FileCtx.File,
                    frame.FileCtx.Namespace, frame.DeclaringType);
                if (inaccessible != null)
                {
                    env.Error(span, AccessChecker.InaccessibleMessage(inaccessible));
                    UseSiteAccessibility.NoteExplicitlyReported(inaccessible, reportDedup);
                    return null;
                }
                // 使用侧统一填入点检查（S9b 显式界 + g4 隐式限制闭包，
                // SYNTAX §3.6/§3.1.1）：构造类型实参须满足声明约束与实例化
                // 后的布局闭包（含嵌套递归）。拒绝口径：显式界不满足即
                // 拒绝（诊断已落袋）；隐式限制违规按可恢复模型继续绑定，
                // 避免「缺初始化器」等级联误诊
                if (type.ConstructedFrom != null)
                {
                    GenericConstraints.CheckConstructedType(type, span, env,
                        out var explicitConstraintsOk);
                    if (!explicitConstraintsOk)
                    {
                        return null;
                    }
                }
                return type;
            }
            if (resolved is GenericParameterSymbol) return resolved;
            // 防御性不可达：NameResolver.ResolveTypeReference 落袋前已拦截
            // 命名空间等非类型符号并毒化为 ErrorType（"'x' is not a type"）；
            // 保留同口径措辞兜底（符号路径如 `a.b` 不是类型）
            env.Error(span, $"'{NameResolver.PathText(typeRef.TypeSymbol.symbol)}' is not a type");
            return null;
        }
    }
}
