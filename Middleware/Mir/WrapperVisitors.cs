using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR wrapper 存储簇（MW10）：get/set/new.wrapper.* → 隐藏槽；
    // get.self → 宿主参数占位，特化后消除。invoke fn(..inner) 在 CallVisitors 落 MirInnerCall。

    internal sealed class GetWrapperLowering
        : MirLowerVisitor<GetWrapperLowering, GetWrapperInstruction>
    {
        protected override void VisitCore(GetWrapperInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetWrapper(flow.Local(inst.Value),
                MwTypeKey.Normalize(inst.WrapperType.TypeRef), inst.Target.Name));
        }
    }

    // §12.4 动态形态：get.wrapper.indirect VALUE WRAPPER_TYPEID_VAR RESULT。
    // wrapper 隐藏槽偏移是布局期静态量（native 无运行期槽查找面），
    // typeid 局部经 FlowBuilder 静态 id 追踪解析回 typeref 后落与直译版
    // 相同的 MirGetWrapper（VM：RequireTypeId 后 GetWrapper 同路径）；
    // 静态不可解析（跨函数流转等）或宿主静态类型（沿 extends 链）无该
    // wrapper 槽时受控拒绝——编译期拒绝是 VM 运行期「宿主没有 wrapper」
    // 解析失败的保守超集（同 UserOperatorLowering 先例）
    internal sealed class GetWrapperIndirectLowering
        : MirLowerVisitor<GetWrapperIndirectLowering, GetWrapperIndirectInstruction>
    {
        protected override void VisitCore(GetWrapperIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            if (!flow.TryResolveTypeIdRef(inst.WrapperTypeId.Name, out var wrapperRef))
            {
                throw new MwNotSupportedException(
                    $"get.wrapper.indirect 的 wrapper typeid 静态不可解析: "
                    + $"${inst.WrapperTypeId.Name}（fn {flow.FnSymbol}）");
            }
            var wrapperType = MwTypeKey.Normalize(wrapperRef);
            if (flow.Context.Symbols.FindTypeByRef(wrapperType) is not
                { Declaration.Kind: BilTypeKind.Wrapper })
            {
                throw new MwNotSupportedException(
                    $"get.wrapper.indirect 的 typeid 目标不是 wrapper 类型: "
                    + $"{wrapperType}（fn {flow.FnSymbol}）");
            }
            var hostType = flow.TypeOf(inst.Value.Name).Canonical;
            if (!HasEntityWrapperSlot(flow, MwTypeKey.Normalize(hostType), wrapperType))
            {
                throw new MwNotSupportedException(
                    $"get.wrapper.indirect 宿主 {hostType} 无 wrapper 槽 {wrapperType}"
                    + $"（VM 运行期同形失败；native 槽偏移静态不可得，编译期拒绝）（fn {flow.FnSymbol}）");
            }
            flow.Add(new MirGetWrapper(flow.Local(inst.Value), wrapperType, inst.Target.Name));
        }

        // 沿 extends 链下探取 Entity 隐藏槽（WrapperEmitter.
        // ResolveEntitySlotSymbol 同口径，槽恒归首次声明名下随基类计划
        // 逐层拷入）
        private static bool HasEntityWrapperSlot(FlowBuilder flow, string hostCanonical,
            string wrapperType)
        {
            var current = hostCanonical;
            var guard = new HashSet<string>(System.StringComparer.Ordinal);
            while (guard.Add(current))
            {
                var candidate = WrapperAbi.EntityFieldSymbol(current, wrapperType);
                var plan = flow.Context.Layout?.Find(current);
                if (plan != null)
                {
                    foreach (var field in plan.Fields)
                    {
                        if (field.Symbol == candidate)
                        {
                            return true;
                        }
                    }
                }
                if (flow.Context.Symbols.FindTypeByRef(current)?.Declaration.ExtendsType
                        is not { } baseRef)
                {
                    return false;
                }
                current = MwTypeKey.Normalize(baseRef);
            }
            return false;
        }
    }

    internal sealed class GetSelfLowering : MirLowerVisitor<GetSelfLowering, GetSelfInstruction>
    {
        protected override void VisitCore(GetSelfInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetSelf(inst.Target.Name));
        }
    }

    internal sealed class GetWrapperFieldLowering
        : MirLowerVisitor<GetWrapperFieldLowering, GetWrapperFieldInstruction>
    {
        protected override void VisitCore(GetWrapperFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetWrapperField(flow.Local(inst.Object), inst.HostField.Symbol,
                MwTypeKey.Normalize(inst.WrapperType.TypeRef), inst.Target.Name));
        }
    }

    internal sealed class SetWrapperFieldLowering
        : MirLowerVisitor<SetWrapperFieldLowering, SetWrapperFieldInstruction>
    {
        protected override void VisitCore(SetWrapperFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var chain = new List<MirWrapperChainElem>(inst.Chain.Count);
            foreach (var elem in inst.Chain)
            {
                chain.Add(elem switch
                {
                    BilFieldOperand field => new MirWrapperChainField(field.Symbol),
                    BilWrapperOperand wrapper => new MirWrapperChainWrapper(
                        MwTypeKey.Normalize(wrapper.TypeRef)),
                    _ => throw new MwNotSupportedException(
                        $"set.wrapper.field 链元素未覆盖: {elem.GetType().Name}"),
                });
            }
            flow.Add(new MirSetWrapperField(flow.Local(inst.Source), flow.Local(inst.Object),
                chain, inst.InnerField.Symbol));
        }
    }

    internal sealed class NewWrapperEntityLowering
        : MirLowerVisitor<NewWrapperEntityLowering, NewWrapperEntityInstruction>
    {
        protected override void VisitCore(NewWrapperEntityInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(WrapperInstall.BuildInstall(flow, MirWrapperInstallKind.Entity,
                inst.WrapperType.TypeRef, inst.Arguments, field: null, method: null));
        }
    }

    internal sealed class NewWrapperFieldLowering
        : MirLowerVisitor<NewWrapperFieldLowering, NewWrapperFieldInstruction>
    {
        protected override void VisitCore(NewWrapperFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(WrapperInstall.BuildInstall(flow, MirWrapperInstallKind.Field,
                inst.WrapperType.TypeRef, inst.Arguments, inst.Field.Symbol, method: null));
        }
    }

    internal sealed class NewWrapperMethodLowering
        : MirLowerVisitor<NewWrapperMethodLowering, NewWrapperMethodInstruction>
    {
        protected override void VisitCore(NewWrapperMethodInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(WrapperInstall.BuildInstall(flow, MirWrapperInstallKind.Method,
                inst.WrapperType.TypeRef, inst.Arguments, field: null, inst.Method.Symbol));
        }
    }

    file static class WrapperInstall
    {
        internal static MirNewWrapper BuildInstall(FlowBuilder flow, MirWrapperInstallKind kind,
            string wrapperTypeRef, IReadOnlyList<BilVariableOperand> arguments,
            string? field, string? method)
        {
            var wrapperType = MwTypeKey.Normalize(wrapperTypeRef);
            var template = flow.Context.Symbols.FindTypeByRef(wrapperType)
                ?? throw new MwNotSupportedException("new.wrapper 的类型不可解析: " + wrapperTypeRef);
            MwMemberSymbol? init = null;
            try
            {
                init = MirBuilder.ResolveInit(flow.Context.Symbols, template,
                    flow.ArgTypes(arguments), skipReceiver: 0,
                    constructedTypeRef: wrapperType);
            }
            catch (MwNotSupportedException) when (arguments.Count == 0)
            {
                init = null;
            }
            var initWrapper = flow.Context.Symbols.FindInitWrapper(template, 0);
            return new MirNewWrapper(new MirLocalOperand(".this"), kind, wrapperType, field,
                method, init, initWrapper, FlowBuilder.Locals(arguments));
        }
    }
}
