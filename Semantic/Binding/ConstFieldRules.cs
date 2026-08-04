namespace LatteCompiler
{
    // const 字段规则（S8b 前置，FieldSymbol.IsConst 配套）：
    // - const 字段赋值检查：仅 init 构造方法体内的 const 实例字段赋值放行
    //   （构造期一次性赋值——SYNTAX §9.3；重复赋值的精确检查留后续）；
    //   其余位置（普通方法/static 上下文/全局 const 字段）一律诊断。
    // - 字段写入统一检查（S8e）：带访问器字段走 setter 存在性与可见性
    //   （§9.4.1），无访问器字段走 const 规则。
    // - smart cast 收窄资格（SMART_CAST_DESIGN §4.2）：仅 const 字段
    //   可收窄（var 字段别名赋值不可控）；带 getter/setter 的字段（§9.4）
    //   不收窄——外部读写一律经访问器，读取结果不承诺稳定（§9.4.1）。
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

        // 字段写入统一检查（S8e，SYNTAX §9.4.1；赋值与复合赋值 place 共用）：
        // 带访问器字段检查 setter 存在性与访问器自身可见性（const+set 已被
        // P2 拒；无 setter 的 const 访问器字段由 has no setter 拦截），
        // 不再走 const 检查；无访问器字段走 const 规则
        public static bool CheckWritable(FieldSymbol field, CharRange? span, BindContext ctx,
            BindEnvironment env)
        {
            if (field.Getter != null || field.Setter != null)
            {
                if (field.Setter == null)
                {
                    env.Error(span, $"'{field.Name}' has no setter");
                    return false;
                }
                if (!ctx.CanAccess(field.Setter))
                {
                    env.Error(span, $"'{field.Name}' setter is inaccessible due to its " +
                        "accessibility level");
                    return false;
                }
                return true;
            }
            return CheckAssignable(field, span, ctx, env);
        }

        // 收窄资格：const 字段（不带访问器的 backing field 直访——带访问器
        // 字段的读取经访问器、结果不承诺稳定，S8e 起排除）；构造方法 init
        // 体内的 this 字段保守排除（const 字段构造期可能尚未初始化，
        // SMART_CAST_DESIGN §4.3）
        public static bool IsNarrowable(FieldSymbol field, BindContext ctx)
        {
            if (!field.IsConst) return false;
            if (field.Getter != null || field.Setter != null) return false;
            if (ctx.Method.Kind == MethodKind.Init && field.Owner != null && !field.IsStatic)
            {
                return false;
            }
            return true;
        }
    }
}
