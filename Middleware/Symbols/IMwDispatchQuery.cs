using System.Collections.Generic;

namespace RigiCompiler.Middleware.Symbols
{
    /// <summary>
    /// 派发闭包查询（依赖倒置：Layout 实现、Mir 消费）。只暴露
    /// MirReachability 真正需要的槽/继承/iMap 查询，签名仅用 BCL 类型，
    /// 不泄漏 Layout 计划对象。
    /// </summary>
    public interface IMwDispatchQuery
    {
        // 类型的 vtable 槽序（class / interface 空壳）；未布局为 null
        IReadOnlyList<string>? GetVTableSlots(string typeCanonical);

        bool IsClass(string typeCanonical);

        // 沿基类链判定 derived 是否派生自（含等于）base
        bool DerivesFrom(string derivedCanonical, string baseCanonical);

        // class 的 iMap：接口 canonical → 本类 vtable 段 base offset
        IReadOnlyList<(string IfaceType, int BaseOffset)>? GetIMap(string typeCanonical);

        // 全部已布局 class 的 canonical（遍历 override 后代用）
        IReadOnlyList<string> AllClassCanonicals();
        IReadOnlyList<(string Host, string Method)> ValueInterfaceImplementations(string iface, string signature);
    }
}
