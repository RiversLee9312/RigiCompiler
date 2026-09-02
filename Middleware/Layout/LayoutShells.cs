using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// interface 空壳计划（无实例布局，仅为 TypeSheet 地址身份与接口内槽序）。
    /// wrapper 实例布局见 <see cref="ValueTypeLayout.LayoutWrapper"/>。
    /// </summary>
    internal static class LayoutShells
    {
        internal static TypeLayoutPlan LayoutInterfaceShell(MwTypeSymbol type, MwSymbolTable symbols)
        {
            var slots = new List<string>();
            foreach (var member in LayoutEngine.InstanceMethods(type))
            {
                slots.Add(member.Canonical);
            }
            // 父接口槽接在自身成员之后（VM 拍平：自有槽 → 接口段）；自身
            // 下标不变，InterfaceSlotOf 对本接口方法仍从 0 计
            foreach (var parentKey in VTablePlanner.CollectIfaceClosure(type.Canonical, type,
                symbols))
            {
                var parent = symbols.FindTypeByRef(parentKey);
                if (parent == null || parent.Declaration.Kind != BilTypeKind.Interface)
                {
                    continue;
                }
                foreach (var member in LayoutEngine.InstanceMethods(parent))
                {
                    slots.Add(member.Canonical);
                }
            }
            return new TypeLayoutPlan(type, TypeLayoutKind.Interface, 0, 1,
                LayoutEngine.TypeFlagsOf(type), System.Array.Empty<FieldPlan>(), slots,
                System.Array.Empty<(string, int)>(), System.Array.Empty<ushort>(),
                System.Array.Empty<(MwCaseSymbol, uint)>(), null,
                ifaceClosure: VTablePlanner.CollectIfaceClosure(type.Canonical, type, symbols));
        }
    }
}
