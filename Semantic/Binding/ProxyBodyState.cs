namespace RigiCompiler
{
    // proxy 体模板态绑定语境（M88，ARCH §5.2）：绑定 `.proxy.` 声明体期间
    // 存活的组件，挂 BindContext.Proxy。IsActive 时 self/inner 可用；
    // this 走普通实例上色（宿主 = wrapper 类型）。非 proxy 语境 IsActive
    // 恒 false。
    //
    // SelfType：宿主 wrapper 恰一泛型参数时 = 该 GenericParameterSymbol
    //（TTarget），否则 null——引用 self 报错（SYNTAX §14.2）。
    internal sealed class ProxyBodyState
    {
        public bool IsActive { get; private set; }

        // self 的类型（TTarget 泛型参数；零泛型 wrapper 为 null）
        public GenericParameterSymbol? SelfType { get; private set; }

        public void Activate(GenericParameterSymbol? selfType)
        {
            IsActive = true;
            SelfType = selfType;
        }
    }
}
