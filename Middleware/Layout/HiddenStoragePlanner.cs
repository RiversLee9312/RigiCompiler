using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Layout
{
    /// <summary>
    /// 按应用标记为宿主合成 wrapper 隐藏存储（内联值，按实际字段生成
    /// refMap；self 独立传参，不在实例中存储）。不改 TypeSheet 地址身份。
    /// </summary>
    internal static class HiddenStoragePlanner
    {
        // Method 槽位收集（刀6）：BIL 方法声明不保留 wrapped 修饰符
        //（前端把 Method wrapper 应用织入宿主 ..init.wrapper 体的
        // new.wrapper.method 指令，§9.7 闭包缝合后子类对继承方法同样
        // 安装），槽钥匙归**方法声明类**名下（实现槽符号的宿主段；子类
        // 经 basePlan.Fields 原名拷入继承，偏移一致——o2 形态：Child 的
        // ..init.wrapper 安装 Base$work，槽位 Base# 前缀）。返回
        // 声明类 canonical → (方法 canonical, wrapper canonical) 保序表
        internal static Dictionary<string, IReadOnlyList<(string Method, string Wrapper)>>
            CollectMethodSlots(BilModule module)
        {
            var map = new Dictionary<string, List<(string, string)>>(
                System.StringComparer.Ordinal);
            foreach (var function in module.Functions)
            {
                foreach (var block in function.Blocks)
                {
                    foreach (var inst in block.Instructions)
                    {
                        if (inst is not NewWrapperMethodInstruction install)
                        {
                            continue;
                        }
                        var method = install.Method.Symbol;
                        var dollar = method.IndexOf('$');
                        if (dollar <= 0)
                        {
                            continue;
                        }
                        var owner = method.Substring(0, dollar);
                        var wrapper = MwTypeKey.Normalize(install.WrapperType.TypeRef);
                        if (!map.TryGetValue(owner, out var list))
                        {
                            list = new List<(string, string)>();
                            map[owner] = list;
                        }
                        if (!list.Contains((method, wrapper)))
                        {
                            list.Add((method, wrapper));
                        }
                    }
                }
            }
            var result = new Dictionary<string, IReadOnlyList<(string, string)>>(
                System.StringComparer.Ordinal);
            foreach (var pair in map)
            {
                result[pair.Key] = pair.Value;
            }
            return result;
        }

        internal static void AppendHostSlots(MwTypeSymbol host, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting, List<FieldPlan> fields,
            List<RefMapBuilder.RefSite> refEntries, ref int offset,
            IReadOnlyDictionary<string, IReadOnlyList<(string Method, string Wrapper)>>?
                methodSlots = null)
        {
            // Entity 应用（类型声明 wrapped(W)，声明序 = outer→inner）。
            // 遗3：槽身份对齐 VM HiddenEntityKey（VmContext.HiddenEntityKey——
            // 键仅 wrapper TypeRef，不含声明类；子类重申同 ref 在 VM 覆盖
            // 同一隐藏键）：祖先已声明同 ref 的 Entity 应用不另开物理槽——
            // 槽随 basePlan.Fields 原名拷入本类布局（偏移一致），重申的
            // 安装与环读经 WrapperEmitter 下探同归首次声明（最基类）槽。
            // （Field/Method 槽的 VM 键含字段/方法符号，天然归声明类
            // 唯一，无需此处去重。）
            var entitySeen = CollectAncestorEntityRefs(host, symbols);
            foreach (var wrapperRef in WrappedOf(host.Declaration.Modifiers))
            {
                if (!entitySeen.Add(wrapperRef))
                {
                    continue;
                }
                AppendSlot(fields, refEntries, ref offset,
                    WrapperAbi.EntityFieldSymbol(host.Canonical, wrapperRef),
                    wrapperRef, symbols, table, visiting);
            }
            // 字段-Value 应用（实例字段 wrapped(W)；静态已落到 companion cell）
            foreach (var member in LayoutEngine.InstanceFields(host))
            {
                foreach (var wrapperRef in WrappedOf(member.Declaration.Modifiers))
                {
                    AppendSlot(fields, refEntries, ref offset,
                        WrapperAbi.FieldValueSymbol(host.Canonical, member.Canonical, wrapperRef),
                        wrapperRef, symbols, table, visiting);
                }
            }
            // Method 应用（刀6）：手写 BIL 的方法声明 wrapped(W) 修饰符
            // 与 ..init.wrapper 体安装指令（前端唯一事实源）双源并集，
            // 槽符号去重；槽钥匙恒归方法声明类（本 host）名下
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var member in host.Members)
            {
                if (member.Declaration.Kind != BilMemberKind.Method)
                {
                    continue;
                }
                foreach (var wrapperRef in WrappedOf(member.Declaration.Modifiers))
                {
                    var symbol = WrapperAbi.MethodFieldSymbol(host.Canonical, member.Canonical,
                        wrapperRef);
                    if (seen.Add(symbol))
                    {
                        AppendSlot(fields, refEntries, ref offset, symbol, wrapperRef,
                            symbols, table, visiting);
                    }
                }
            }
            if (methodSlots != null
                && methodSlots.TryGetValue(host.Canonical, out var installs))
            {
                foreach (var (method, wrapperRef) in installs)
                {
                    var symbol = WrapperAbi.MethodFieldSymbol(host.Canonical, method, wrapperRef);
                    if (seen.Add(symbol))
                    {
                        AppendSlot(fields, refEntries, ref offset, symbol, wrapperRef,
                            symbols, table, visiting);
                    }
                }
            }
        }

        // 沿 extends 链收集祖先声明的 Entity wrapper 精确 TypeRef（环保护；
        // 外部/不可解析基类即止——其字段布局不随 basePlan 拷入，本类自行
        // 分配）。与 WrapperApplicationIndex 的定义级去重不同：VM 隐藏键
        // 是精确 TypeRef 串（泛型实参计入），故此处同口径按精确 ref 判重
        private static HashSet<string> CollectAncestorEntityRefs(MwTypeSymbol host,
            MwSymbolTable symbols)
        {
            var refs = new HashSet<string>(System.StringComparer.Ordinal);
            var guard = new HashSet<string>(System.StringComparer.Ordinal) { host.Canonical };
            var current = host;
            while (current.Declaration.ExtendsType is { } baseRef
                && symbols.FindTypeByRef(baseRef) is { IsExternal: false } baseType
                && guard.Add(baseType.Canonical))
            {
                foreach (var wrapperRef in WrappedOf(baseType.Declaration.Modifiers))
                {
                    refs.Add(wrapperRef);
                }
                current = baseType;
            }
            return refs;
        }

        private static void AppendSlot(List<FieldPlan> fields, List<RefMapBuilder.RefSite> refEntries,
            ref int offset, string symbol, string wrapperRef, MwSymbolTable symbols,
            LayoutPlanTable table, HashSet<string> visiting)
        {
            var wrapperType = symbols.FindTypeByRef(wrapperRef)
                ?? throw new CompilerInternalException("隐藏存储找不到 wrapper 类型: " + wrapperRef);
            var plan = LayoutEngine.Resolve(wrapperType, symbols, table, visiting, null)
                ?? throw new CompilerInternalException("wrapper 无布局计划: " + wrapperRef);
            var size = plan.Size;
            // 零状态 wrapper 只保留寻址标记，不能通过对齐填充改变宿主 ABI。
            var alignment = size == 0 ? 1 : (plan.Alignment > 0 ? plan.Alignment : 1);
            offset = LayoutEngine.AlignUp(offset, alignment);
            fields.Add(new FieldPlan(symbol, offset, size, alignment,
                isReferenceSlot: false, embeddedPlan: plan));
            RefMapBuilder.CollectRefSite(refEntries, new LayoutEngine.FieldTypeInfo(
                size, alignment, false, plan), offset);
            offset = LayoutEngine.CheckedAdd(offset, size,
                $"隐藏存储 {symbol}");
        }

        private static IEnumerable<string> WrappedOf(IReadOnlyList<BilModifier> modifiers)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier is BilWrappedModifier wrapped)
                {
                    yield return MwTypeKey.Normalize(wrapped.WrapperTypeRef);
                }
            }
        }
    }
}
