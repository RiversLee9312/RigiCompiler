using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// 调用图可达性（MIR 构建的输入）：从入口 fn 出发沿 invoke 边收闭包，
    /// 产出 MirBuilder 的构建顺序。模块中不可达的 fn（如 bootstrap 预定义
    /// 符号的编译器合成体 toString——它们不进符号段，verifier 以硬编码环境
    /// 闭合）不建 MIR 也不进发射；这同时是模块级死代码消除。
    /// MW4 边规则：invoke 按 Binding 派发形态展开——直接调用到目标 fn；
    /// 虚调用到静态目标 + 全部 override 后代（vtable 完整性）；interface
    /// 调用到各实现类的段内实现；new type(X) 到匹配 init + ..init.wrapper
    /// + X 的全部 vtable 槽实现（消灭 null 槽，abstract 无体槽除外）；
    /// new.indirect 保守收模块内全部 init 族（含 stdlib core*）；
    /// fn(..super) 到解析后的基类实现。派发闭包经 IMwDispatchQuery 查询
    ///（Layout 实现；LayoutStage 排在 MirBuild 之前）。
    /// </summary>
    public static class MirReachability
    {
        // 可达 fn 的 canonical 序（入口优先，BFS 发现序）。
        // TentativeInitFamily = 仅经 new.indirect 保守边引入的 init 族，
        // MIR 构建失败时试探性跳过（其余 fn 仍响亮失败）。
        public static (IReadOnlyList<string> Order, IReadOnlySet<string> TentativeInitFamily)
            ResolveBuildOrder(MwContext context)
        {
            var bySymbol = new Dictionary<string, BilFunction>(System.StringComparer.Ordinal);
            foreach (var bilFn in context.Module.Functions)
            {
                bySymbol.Add(bilFn.Symbol, bilFn);
            }

            var order = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            var tentative = new HashSet<string>(System.StringComparer.Ordinal);
            var queue = new Queue<(string Symbol, bool FromTentative)>();
            foreach (var bilFn in context.Module.Functions)
            {
                // 无符号段声明的 fn 是预定义符号的合成体（§9.1 verifier 以
                // 硬编码环境豁免），永不为入口；不可达则不建 MIR
                var member = context.Symbols.FindMember(bilFn.Symbol);
                if (member == null)
                {
                    continue;
                }
                if (member.HasKeyword(BilKeyword.Entrypoint)
                    // ..globals.init 不在任何 invoke 闭包内，但 rigi_entry
                    // stub 恒调用它（静态字段初值）：恒可达
                    || bilFn.Symbol.StartsWith("$..globals.init(", System.StringComparison.Ordinal))
                {
                    queue.Enqueue((bilFn.Symbol, false));
                }
            }
            while (queue.Count > 0)
            {
                var (symbol, fromTentative) = queue.Dequeue();
                if (!seen.Add(symbol))
                {
                    // 先经保守边入队、后经普通边到达：取消试探标记
                    if (!fromTentative)
                    {
                        tentative.Remove(symbol);
                    }
                    continue;
                }
                order.Add(symbol);
                if (fromTentative)
                {
                    tentative.Add(symbol);
                }
                var tentativeSink = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var edge in CallEdges(context, bySymbol[symbol], tentativeSink))
                {
                    if (bySymbol.ContainsKey(edge))
                    {
                        queue.Enqueue((edge, tentativeSink.Contains(edge)));
                    }
                }
            }
            return (order, tentative);
        }

        // fn 体内的全部可达边（BIL 的 block 平铺在 fn 级，无需递归遍历）
        private static IEnumerable<string> CallEdges(MwContext context, BilFunction fn,
            HashSet<string> tentativeSink)
        {
            var edges = new List<string>();
            // fn 局部类型表（super/new 的实参静态类型解析用）
            var localTypes = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var arg in fn.Args)
            {
                localTypes[arg.Name] = arg.TypeRef;
            }
            foreach (var varDecl in fn.Vars)
            {
                localTypes[varDecl.Name] = varDecl.TypeRef;
            }

            foreach (var block in fn.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    switch (inst)
                    {
                        case InvokeInstruction invoke:
                            AddCallEdges(context, fn, invoke.Method.Symbol,
                                ArgTypesOf(invoke.Arguments, localTypes), edges);
                            break;
                        case InvokeNoResultInstruction invokeNoResult:
                            AddCallEdges(context, fn, invokeNoResult.Method.Symbol,
                                ArgTypesOf(invokeNoResult.Arguments, localTypes), edges);
                            break;
                        case NewInstruction newInst:
                            AddNewEdges(context, newInst.Type.TypeRef, newInst.Arguments,
                                localTypes, edges);
                            break;
                        case NewIndirectInstruction:
                            // 运行期目标不可静态知：保守把模块内全部类型的
                            // init 族收入可达闭包（用户 init / 默认合成 init /
                            // ..init.wrapper / ..init.field.*）；仅此边引入的
                            // 记入 tentativeSink，供 MIR 试探性跳过
                            var before = edges.Count;
                            AddAllInitFamilyEdges(context, edges);
                            for (var i = before; i < edges.Count; i++)
                            {
                                tentativeSink.Add(edges[i]);
                            }
                            break;
                        case NewCaseInstruction newCase:
                            AddNewEdges(context, newCase.Type.TypeRef, newCase.Arguments,
                                localTypes, edges);
                            break;
                        case GetFieldInstruction getField:
                            AddAccessorEdge(context, fn, getField.Field.Symbol,
                                BilAccessorKind.Getter, edges);
                            break;
                        case SetFieldInstruction setField:
                            AddAccessorEdge(context, fn, setField.Field.Symbol,
                                BilAccessorKind.Setter, edges);
                            break;
                        case GetFieldStaticInstruction getStatic:
                            AddAccessorEdge(context, fn, getStatic.Field.Symbol,
                                BilAccessorKind.Getter, edges);
                            break;
                        case SetFieldStaticInstruction setStatic:
                            AddAccessorEdge(context, fn, setStatic.Field.Symbol,
                                BilAccessorKind.Setter, edges);
                            break;
                        case GetArrayInstruction getArray:
                            AddIndexOperatorEdge(context, localTypes, getArray.Array.Name,
                                isGet: true, edges);
                            break;
                        case SetArrayInstruction setArray:
                            AddIndexOperatorEdge(context, localTypes, setArray.Collection.Name,
                                isGet: false, edges);
                            break;
                        case InvokeIndirectInstruction invokeIndirect:
                            AddIndirectCallEdges(context, localTypes[invokeIndirect.CallTarget.Name],
                                ArgTypesOf(invokeIndirect.Arguments, localTypes),
                                localTypes[invokeIndirect.Target.Name], edges);
                            break;
                        case InvokeIndirectNoResultInstruction invokeIndirectNoResult:
                            AddIndirectCallEdges(context, localTypes[invokeIndirectNoResult.CallTarget.Name],
                                ArgTypesOf(invokeIndirectNoResult.Arguments, localTypes),
                                null, edges);
                            break;
                    }
                }
            }
            return edges;
        }

        // invoke 边：按 Binding 派发形态展开（native 面/外部声明不构成边）
        private static void AddCallEdges(MwContext context, BilFunction fn, string symbol,
            List<string> argTypes, List<string> edges)
        {
            if (symbol == BilSpellings.SuperReservedFunction)
            {
                edges.Add(MirBuilder.ResolveSuperCall(context, fn.Symbol, argTypes).Canonical);
                return;
            }
            // 目标不在符号段属预定义符号调用（MirBuilder 建 MIR 时受控拒绝）
            var member = context.Symbols.FindMember(symbol);
            if (member == null)
            {
                return;
            }
            switch (ImplBinder.BindCall(member))
            {
                case NativeDirectBinding:
                    break;
                case DirectCallBinding:
                    edges.Add(member.Canonical);
                    break;
                case VirtualCallBinding:
                    AddVirtualEdges(context, member, edges);
                    break;
                case InterfaceCallBinding:
                    AddInterfaceEdges(context, member, edges);
                    break;
            }
        }

        // invoke.indirect：按静态类型解析 $$call，再按宿主 Kind 走虚/接口闭包
        private static void AddIndirectCallEdges(MwContext context, string objectStaticType,
            List<string> argTypes, string? resultType, List<string> edges)
        {
            var binding = ImplBinder.BindIndirectCall(context.Symbols, objectStaticType,
                argTypes, resultType, context.Module.Functions);
            var callOperator = binding.CallOperator;
            if (callOperator.Owner == null)
            {
                return;
            }
            switch (callOperator.Owner.Declaration.Kind)
            {
                case BilTypeKind.Class:
                    AddVirtualEdges(context, callOperator, edges);
                    break;
                case BilTypeKind.Interface:
                    AddInterfaceEdges(context, callOperator, edges);
                    break;
            }
        }

        // 虚调用边：静态目标 + 全部 override 后代（同槽实现；vtable 完整性）
        private static void AddVirtualEdges(MwContext context, MwMemberSymbol target,
            List<string> edges)
        {
            edges.Add(target.Canonical);
            var query = context.DispatchQuery;
            var ownerSlots = query?.GetVTableSlots(target.Owner!.Canonical);
            if (ownerSlots == null)
            {
                return;
            }
            var slot = IndexOfSlot(ownerSlots, target.Canonical);
            if (slot < 0)
            {
                return;
            }
            foreach (var typeCanonical in query!.AllClassCanonicals())
            {
                if (!query.DerivesFrom(typeCanonical, target.Owner!.Canonical))
                {
                    continue;
                }
                var slots = query.GetVTableSlots(typeCanonical);
                if (slots != null)
                {
                    edges.Add(slots[slot]);
                }
            }
        }

        // interface 调用边：默认方法自身有 fn 体则入闭包；各实现类 iMap 段内实现
        private static void AddInterfaceEdges(MwContext context, MwMemberSymbol target,
            List<string> edges)
        {
            if (HasFunctionBody(context, target.Canonical))
            {
                edges.Add(target.Canonical);
            }
            var query = context.DispatchQuery;
            var ifaceSlots = query?.GetVTableSlots(target.Owner!.Canonical);
            if (ifaceSlots == null)
            {
                return;
            }
            var slot = IndexOfSlot(ifaceSlots, target.Canonical);
            if (slot < 0)
            {
                return;
            }
            foreach (var typeCanonical in query!.AllClassCanonicals())
            {
                var imap = query.GetIMap(typeCanonical);
                var slots = query.GetVTableSlots(typeCanonical);
                if (imap == null || slots == null)
                {
                    continue;
                }
                foreach (var (ifaceType, baseOffset) in imap)
                {
                    if (ifaceType == target.Owner!.Canonical
                        || context.Symbols.FindTypeByRef(ifaceType) == target.Owner)
                    {
                        edges.Add(slots[baseOffset + slot]);
                    }
                }
            }
        }

        // new.indirect 保守边：模块内全部类型的 init 族（含 stdlib core*）。
        // 运行期目标由 typeid 决定，静态无法收窄；宁可多留不可达 ctor。
        // 不可构建的 init 族由 MirBuilder 试探性跳过。
        private static void AddAllInitFamilyEdges(MwContext context, List<string> edges)
        {
            foreach (var type in context.Symbols.Types)
            {
                if (type.IsExternal
                    || type.Declaration.Kind is not (BilTypeKind.Class
                        or BilTypeKind.Struct or BilTypeKind.EnumStruct))
                {
                    continue;
                }
                foreach (var member in type.Members)
                {
                    if (member.Declaration.Kind != BilMemberKind.Method)
                    {
                        continue;
                    }
                    if (member.HasKeyword(BilKeyword.Init)
                        || IsInitFamilyName(member.Canonical))
                    {
                        edges.Add(member.Canonical);
                    }
                }
            }
        }

        private static bool IsInitFamilyName(string canonical)
        {
            var dollar = canonical.IndexOf('$');
            if (dollar < 0)
            {
                return false;
            }
            var rest = canonical.Substring(dollar + 1);
            return rest.StartsWith("..init.wrapper(", System.StringComparison.Ordinal)
                || rest.StartsWith(BilSpellings.InitFieldMethodPrefix, System.StringComparison.Ordinal);
        }

        // new type(X) 边：匹配 init + ..init.wrapper（+ class 的全部
        // vtable 槽实现——虚派发面闭包；abstract 无体槽在入队处按 fn 缺失
        // 过滤）；struct/enum 无 vtable，只到 init/init.wrapper
        private static void AddNewEdges(MwContext context, string typeRef,
            IReadOnlyList<BilVariableOperand> arguments,
            Dictionary<string, string> localTypes, List<string> edges)
        {
            if (context.Symbols.FindTypeByRef(typeRef) is not { } type
                || type.Declaration.Kind is not (BilTypeKind.Class
                    or BilTypeKind.Struct or BilTypeKind.EnumStruct))
            {
                // 不可解析/其他形态：MirBuilder 建 MIR 时受控拒绝，此处不构成边
                return;
            }
            edges.Add(MirBuilder.ResolveInit(context.Symbols, type,
                ArgTypesOf(arguments, localTypes), skipReceiver: 0,
                constructedTypeRef: typeRef).Canonical);
            var initWrapper = context.Symbols.FindMember(type.Canonical + "$..init.wrapper()@.void");
            if (initWrapper != null)
            {
                edges.Add(initWrapper.Canonical);
            }
            if (type.Declaration.Kind == BilTypeKind.Class)
            {
                var slots = context.DispatchQuery?.GetVTableSlots(MwTypeKey.Normalize(typeRef))
                    ?? context.DispatchQuery?.GetVTableSlots(type.Canonical);
                if (slots != null)
                {
                    edges.AddRange(slots);
                }
            }
        }

        // 字段访问边：字段带访问器时读/写经 getter/setter fn（VM
        // TryFindAccessor 同口径；excludingFn 传当前 fn，访问器体内对
        // 自身 backing 的直访不构成边）。实例访问器通常已被 vtable 槽
        // 覆盖，静态访问器无 vtable 兜底、必须靠此边
        private static void AddAccessorEdge(MwContext context, BilFunction fn,
            string fieldSymbol, BilAccessorKind kind, List<string> edges)
        {
            if (MirBuilder.FindAccessor(context.Symbols, fieldSymbol, kind,
                    fn.Symbol) is { } accessor)
            {
                edges.Add(accessor.Canonical);
            }
        }

        // 用户索引运算符边（内建 Array\<T\> 无边）
        private static void AddIndexOperatorEdge(MwContext context,
            Dictionary<string, string> localTypes, string collectionName, bool isGet,
            List<string> edges)
        {
            if (!localTypes.TryGetValue(collectionName, out var typeRef))
            {
                return;
            }
            var collectionType = MirType.Of(typeRef);
            if (Layout.TypeLayout.IsArray(collectionType))
            {
                return;
            }
            if (MirBuilder.FindIndexOperator(context.Symbols, collectionType, isGet) is { } method)
            {
                edges.Add(method.Canonical);
            }
        }

        private static bool HasFunctionBody(MwContext context, string symbol)
        {
            foreach (var function in context.Module.Functions)
            {
                if (function.Symbol == symbol)
                {
                    return true;
                }
            }
            return false;
        }

        private static int IndexOfSlot(IReadOnlyList<string> slots, string canonical)
        {
            for (var i = 0; i < slots.Count; i++)
            {
                if (slots[i] == canonical)
                {
                    return i;
                }
            }
            return -1;
        }

        private static List<string> ArgTypesOf(IReadOnlyList<BilVariableOperand> args,
            Dictionary<string, string> localTypes)
        {
            // 原始 BIL 类型引用（MirBuilder.ResolveInit 内部统一归一匹配）
            var types = new List<string>(args.Count);
            foreach (var arg in args)
            {
                types.Add(localTypes[arg.Name]);
            }
            return types;
        }
    }
}
