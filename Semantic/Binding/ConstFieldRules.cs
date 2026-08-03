namespace LatteCompiler
{
    // const 字段规则（S8b 前置，FieldSymbol.IsConst 配套）：
    // - const 字段赋值检查：仅 init 构造方法体内的 const 实例字段赋值放行
    //   （构造期一次性赋值——SYNTAX §9.3；重复赋值的精确检查留后续）；
    //   其余位置（普通方法/static 上下文/全局 const 字段）一律诊断。
    // - smart cast 收窄资格（SMART_CAST_DESIGN §4.2）：仅 const 字段
    //   可收窄（var 字段别名赋值不可控）；带 getter/setter 的属性（§9.4）
    //   不收窄——访问器判定归 S8e 落地后细化（当前字段均无访问器信息，
    //   保守视为可收窄的纯字段）。
    internal static class ConstFieldRules
    {
        public static bool CheckAssignable(FieldSymbol field, CharRange? span, BindContext ctx,
            BindEnvironment env)
        {
            if (!field.IsConst) return true;
            // init 构造方法体内的 const 实例字段赋值放行（构造期一次性赋值）
            if (ctx.Method.Kind == MethodKind.Init && field.Owner != null && !field.IsStatic)
            {
                return true;
            }
            env.Error(span, $"Cannot assign to const field '{field.Name}'");
            return false;
        }

        // 收窄资格：const 字段（backing field 直访——带访问器属性的判定
        // 归 S8e 细化）；构造方法 init 体内的 this 字段保守排除
        // （const 字段构造期可能尚未初始化，SMART_CAST_DESIGN §4.3）
        public static bool IsNarrowable(FieldSymbol field, BindContext ctx)
        {
            if (!field.IsConst) return false;
            if (ctx.Method.Kind == MethodKind.Init && field.Owner != null && !field.IsStatic)
            {
                return false;
            }
            return true;
        }
    }
}
