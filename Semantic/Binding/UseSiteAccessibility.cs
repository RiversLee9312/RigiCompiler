namespace RigiCompiler
{
    // F1 使用点类型可见性统一收口（SYNTAX §16.1；V-A 递归口径 + V-B 表达式
    // 直链钩子 + c6 诊断去重）。
    //
    // 背景：S5 挂点只覆盖「声明推断点」（无标注 const/var、解构、for-in），
    // 成员访问/await 解包/?./if? 链的中间结果类型没有任何检查——
    // `hd.h.n()`、`(await t).n()`、`b.get().n()` 把不可见类型无声带入
    // 他文件。本设施在 ExpressionDispatcher 定型出口对每个表达式的结果
    // 类型跑 AccessChecker.FindInaccessibleType（递归构造实参，与
    // SignatureAccessibilityChecker 同口径），任何路径产生的 bound 表达式
    // 都经过此门。
    //
    // 去重口径（BindContext 驻留，函数体粒度）：
    //   · 语法点去重——同一 AST 节点重绑（重载解析预绑/复用等）不重复查；
    //   · 类型去重——同一不可见类型在本函数体内只报一次：显式标注
    //     （TypeReferences，写出点恒报并登记）→ 推断声明（S5 挂点）→
    //     直链使用的泄漏链上，下游位置全部静默，不级联（unit17 口径：
    //     「下游成员访问只查成员可见性，不级联类型诊断」）。
    internal static class UseSiteAccessibility
    {
        // 表达式结果类型检查（V-B 收口）：ExpressionDispatcher.Visit 定型
        // 出口逐节点调用。bound 为 null（绑定失败，诊断已落袋）或本节点
        // 已查过时跳过
        public static void CheckExpressionResult(BoundExpression? bound, ASTNode node,
            BindContext ctx, BindEnvironment env)
        {
            if (bound == null || !ctx.NoteAccessibilityChecked(node)) return;
            var hit = AccessChecker.FindInaccessibleType(bound.Type, ctx.Frame.FileCtx.File,
                ctx.Frame.FileCtx.Namespace, ctx.Frame.DeclaringType);
            if (hit == null || !ctx.NoteInaccessibleTypeReported(hit)) return;
            env.Error(node.Span, AccessChecker.InaccessibleMessage(hit));
        }

        // 路径链中间值检查（V-B 段级收口）：实例成员链的入口 receiver
        //（名头段 + 头段后缀折叠产物，如 arr[0] 的索引结果——不经
        // ExpressionDispatcher）与逐段结果同门检查。不消耗语法点去重键
        //（路径整体节点键留给 dispatcher 对链末类型的检查）；驻留类型
        // 去重保证已报的引入点（标注/推断/上游段/dispatcher）不重复报
        public static void CheckChainValue(BoundExpression? bound, BindContext ctx,
            BindEnvironment env)
        {
            if (bound == null) return;
            var hit = AccessChecker.FindInaccessibleType(bound.Type, ctx.Frame.FileCtx.File,
                ctx.Frame.FileCtx.Namespace, ctx.Frame.DeclaringType);
            if (hit == null || !ctx.NoteInaccessibleTypeReported(hit)) return;
            env.Error(bound.Syntax.Span, AccessChecker.InaccessibleMessage(hit));
        }

        // 推断类型声明侧检查（S5 挂点 + V4 seq using 共用）：与收口同口径
        // 去重——初始化表达式先经 ExpressionDispatcher 收口，命中已登记时
        // 本挂点静默（兜底非 dispatcher 来源的推断类型，不重复报）
        public static void CheckInferredType(SemanticSymbol type, CharRange? span,
            BindContext ctx, BindEnvironment env)
        {
            var hit = AccessChecker.FindInaccessibleType(type, ctx.Frame.FileCtx.File,
                ctx.Frame.FileCtx.Namespace, ctx.Frame.DeclaringType);
            if (hit == null || !ctx.NoteInaccessibleTypeReported(hit)) return;
            env.Error(span, AccessChecker.InaccessibleMessage(hit));
        }

        // 显式写出类型引用的登记（TypeReferences/显式泛型实参）：写出点
        // 由调用方恒报（每处写出都是独立使用点），此处只登记命中类型，
        // 供同函数体内下游推断/直链位置去重
        public static void NoteExplicitlyReported(TypeSymbol hit, BindContext? ctx)
        {
            ctx?.NoteInaccessibleTypeReported(hit);
        }
    }
}
