namespace RigiCompiler
{
    // 函数级绑定上下文的只读帧（M65 Bind 侧组件化拆分，自 BindContext
    // 迁出）：当前函数是谁、在哪、能否访问——构造一次性赋值，之后不可变。
    // 设施层（MemberLookup/TypeReferences/ConstFieldRules/收窄设施……）
    // 只消费只读信息时签名收窄为本类型，不接整个 BindContext。
    internal sealed class BindFunctionFrame
    {
        public BindFunctionFrame(MethodSymbol method, FileContext fileCtx, TypeSymbol? declaringType,
            bool isDefaultValueContext, TypeSymbol? lookupHost = null,
            bool banEnclosingTypeParameters = false)
        {
            Method = method;
            FileCtx = fileCtx;
            DeclaringType = declaringType;
            IsDefaultValueContext = isDefaultValueContext;
            LookupHost = lookupHost ?? method.Owner;
            BanEnclosingTypeParameters = banEnclosingTypeParameters
                || method.IsStatic
                || method.IsCompanionInstance;
        }

        // 当前函数上下文
        public MethodSymbol Method { get; }

        public FileContext FileCtx { get; }

        public TypeSymbol? DeclaringType { get; }

        // 默认值表达式绑定上下文（S8d，SYNTAX §4.2）：声明点作用域——
        // 看不到函数形参、视同静态上下文（无 this）；仅默认值绑定
        // （BindingDriver.BindOneParameterDefault 统一入口，含调用点懒触发）置位
        public bool IsDefaultValueContext { get; }

        // 当前上下文是否有 this receiver（实例方法/ext 方法体内；
        // 默认值表达式上下文视同静态——三处实例上色判定统一走此属性）。
        // 实例访问器（S8e）同样经此判定：访问器符号 Owner/IsStatic 随字段
        // （P1），实例访问器天然满足条件，无需特判。
        // M109b-2：companion 实例方法 BIL 有 .this，但源体本为静态方法——
        // 绑定态视同静态（无 this）
        public bool HasThis => Method.Owner != null && !Method.IsStatic && !IsDefaultValueContext
            && !Method.IsCompanionInstance;

        // 成员查找宿主（MemberLookup.FindField/FindMethods 的宿主链起点）：
        // 普通函数 = Method.Owner（ext 方法 = 目标类型）；lambda 语境 =
        // 外层查找宿主逐层传播（隐藏类 $$call 的 Owner 是隐藏类——词法上
        // 可见的宿主成员必须沿外层上下文解析，SYNTAX §5.2）
        public TypeSymbol? LookupHost { get; }

        // 静态成员（及由其嵌套的 lambda）不得使用所属类型链上的类型参数
        // （SYNTAX §9.2.3：类级 typeid 在实例上，静态体读不到）
        public bool BanEnclosingTypeParameters { get; }

        // 使用点访问控制便捷入口（S8e，SYNTAX §16.1）：以本上下文的文件/
        // 命名空间/宿主类型（DeclaringType = 词法宿主）判定目标符号可见性
        public bool CanAccess(SemanticSymbol target)
        {
            return AccessChecker.IsAccessible(target, FileCtx.File, FileCtx.Namespace,
                DeclaringType);
        }
    }
}
