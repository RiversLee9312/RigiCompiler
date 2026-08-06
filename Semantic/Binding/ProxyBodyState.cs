namespace LatteCompiler
{
    // proxy 体绑定状态（S11b，ROADMAP S11b；M65 组件化先例，仿
    // AccessorBodyState）：逐组合绑定 proxy 声明体期间存活的语境组件，
    // 挂 BindContext.Proxy。非 proxy 语境 IsActive 恒 false（组件各槽
    // 为 null/空，PathVisitors/CallVisitors 的 self/inner/this 上色按
    // 普通规则）。
    //
    // 语境内容（ProxySpecializationInfo 已备链级元数据，本组件是
    // 「当前组合」的绑定视角）：
    // - InnerTarget：inner 的调用目标——链末环 = OriginalBody，其余 =
    //   下一环特化；wildcard 普通/operator 环 = 解包 shim（UnwrapShim，
    //   其 body 由驱动合成：解包后 invoke 下一环）；
    // - SelfType：self 的类型（TTarget 代入结果 = 应用记录 Wrapper 构造
    //   的实参；wrapper 零泛型参数时为 null——self 引用是编译错误，
    //   SYNTAX §14.2）；
    // - MaterializedLocals：前奏物化局部（wildcard 的 symbol/namedArgs/
    //   unnamedArgs 三形参与 get 类别的 value 形参——proxy 声明形参里
    //   与特化 fn（= 成员签名拷贝）不同名的部分，由驱动合成局部并前插
    //   初始化；裸名查找在作用域之后、参数之前命中本表）。
    //
    // 边界（归 S11g 复核）：proxy 声明泛型参数的体内类型引用代入
    //（`var x: TReturn`/`as TField`）未接线——典型 proxy 体（inner
    // 转发/value 直通）不消费，需要时在 TypeReferences 解析路径加
    // 代入映射。
    internal sealed class ProxyBodyState
    {
        // 当前组合的特化元数据（非 proxy 语境为 null）
        public ProxySpecializationInfo? Specialization { get; private set; }
        // inner 的调用目标符号（见文件头）
        public MethodSymbol? InnerTarget { get; private set; }
        // self 的类型（TTarget 代入结果；零泛型 wrapper 为 null）
        public TypeSymbol? SelfType { get; private set; }
        // 前奏物化局部（名 → 合成局部）
        public Dictionary<string, LocalSymbol> MaterializedLocals { get; } =
            new Dictionary<string, LocalSymbol>();

        public bool IsActive => Specialization != null;

        public WrapperApplication? Application => Specialization?.Application;

        public void Set(ProxySpecializationInfo specialization, MethodSymbol innerTarget,
            TypeSymbol? selfType)
        {
            Specialization = specialization;
            InnerTarget = innerTarget;
            SelfType = selfType;
        }
    }
}
