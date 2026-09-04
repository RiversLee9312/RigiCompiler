using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 调用/构造簇：invoke 族与 new 族。无 RegionScope。

    internal sealed class InvokeLowering : MirLowerVisitor<InvokeLowering, InvokeInstruction>
    {
        protected override void VisitCore(InvokeInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            if (inst.Method.Symbol == BilSpellings.InnerReservedFunction)
            {
                flow.Add(new MirInnerCall(FlowBuilder.Locals(inst.Arguments), inst.Target.Name,
                    flow.Tries.CurrentExcTarget()));
                return;
            }
            flow.Add(flow.EmitCallOrSuper(inst.Method.Symbol,
                FlowBuilder.Locals(inst.Arguments), inst.Target.Name));
        }
    }

    internal sealed class InvokeNoResultLowering
        : MirLowerVisitor<InvokeNoResultLowering, InvokeNoResultInstruction>
    {
        protected override void VisitCore(InvokeNoResultInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            if (inst.Method.Symbol == BilSpellings.InnerReservedFunction)
            {
                flow.Add(new MirInnerCall(FlowBuilder.Locals(inst.Arguments), null,
                    flow.Tries.CurrentExcTarget()));
                return;
            }
            flow.Add(flow.EmitCallOrSuper(inst.Method.Symbol,
                FlowBuilder.Locals(inst.Arguments), null));
        }
    }

    internal sealed class InvokeIndirectLowering
        : MirLowerVisitor<InvokeIndirectLowering, InvokeIndirectInstruction>
    {
        protected override void VisitCore(InvokeIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirInvokeIndirect(
                flow.Local(inst.CallTarget), FlowBuilder.Locals(inst.Arguments),
                inst.Target.Name, flow.TypeOf(inst.CallTarget.Name),
                flow.Tries.CurrentExcTarget()));
        }
    }

    internal sealed class InvokeIndirectNoResultLowering
        : MirLowerVisitor<InvokeIndirectNoResultLowering, InvokeIndirectNoResultInstruction>
    {
        protected override void VisitCore(InvokeIndirectNoResultInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirInvokeIndirect(
                flow.Local(inst.CallTarget), FlowBuilder.Locals(inst.Arguments),
                null, flow.TypeOf(inst.CallTarget.Name),
                flow.Tries.CurrentExcTarget()));
        }
    }

    internal sealed class NewIndirectLowering
        : MirLowerVisitor<NewIndirectLowering, NewIndirectInstruction>
    {
        protected override void VisitCore(NewIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirNewIndirect(flow.Local(inst.TypeId),
                FlowBuilder.Locals(inst.Arguments), inst.Target.Name,
                flow.Tries.CurrentExcTarget()));
        }
    }

    internal sealed class NewLowering : MirLowerVisitor<NewLowering, NewInstruction>
    {
        protected override void VisitCore(NewInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            EmitConstruction(flow, inst.Type.TypeRef, inst.Arguments, wrapperArguments: null,
                inst.Target.Name);
        }

        // new type(T) / new.wrapped 共用的构造翻译（刀5 抽出）：wrapperArgs
        // 非空 = 有参 ..init.wrapper 形态（§14.4.1，实参交 wrapper 缝合 fn）
        internal static void EmitConstruction(FlowBuilder flow, string typeRef,
            IReadOnlyList<BilVariableOperand> initArguments,
            IReadOnlyList<BilVariableOperand>? wrapperArguments, string target)
        {
            var newType = MirType.Of(typeRef);
            if (TypeLayout.IsArray(newType))
            {
                if (wrapperArguments != null)
                {
                    throw new MwNotSupportedException($"new.wrapped 不支持数组构造: {typeRef}");
                }
                flow.Add(new MirNewArray(newType, FlowBuilder.Locals(initArguments), target));
                return;
            }
            var template = flow.Context.Symbols.FindTypeByRef(typeRef)
                ?? throw new MwNotSupportedException($"MW4 new 的类型不可解析: {typeRef}");
            // G2：构造类型形态边界——class 与值类型（struct/enum，G1）具化
            // 支持；interface/wrapper 的 new 语言层非法（frontend P3 拒绝），
            // 仅手写 BIL 可达，保留受控拒绝
            if (ConstructedTypeCollector.IsConstructed(typeRef)
                && template.Declaration.Kind is not (BilTypeKind.Class
                    or BilTypeKind.Struct or BilTypeKind.EnumStruct))
            {
                throw new MwNotSupportedException($"MW5 暂不支持的构造类型形态: {typeRef}");
            }
            MwMemberSymbol init;
            try
            {
                init = MirBuilder.ResolveInit(flow.Context.Symbols, template,
                    flow.ArgTypes(initArguments), skipReceiver: 0, constructedTypeRef: typeRef);
            }
            catch (MwNotSupportedException) when (initArguments.Count == 0
                && SingletonPlanner.IsSingleton(template)
                && !SingletonPlanner.HasInitMember(template))
            {
                // 刀5：无 init 声明的 singleton（..globals.host 形态）——
                // 挂合成空 init 占位（定名确定，SingletonLoweringPass
                // 合成同 canonical 的 fn 体；该 MirNewObject 随后即被
                // 改写为 get 调用，占位自身不进发射）
                init = new MwMemberSymbol(
                    new BilSimpleMemberDeclaration(BilMemberKind.Method,
                        template.Canonical + "$init()@.void"),
                    template, isExternal: false);
            }
            // ..init.wrapper 解析：普通 new 只挂零参形态（有参形态上普通
            // new 由 verifier §14.4.1 拒绝）；new.wrapped 按 wrapper 实参
            // 个数匹配有参形态（VM TryFindInitWrapper + 实参个数校验同口径）
            MwMemberSymbol? initWrapper;
            if (wrapperArguments == null)
            {
                initWrapper = flow.Context.Symbols.FindMember(
                    template.Canonical + "$..init.wrapper()@.void");
            }
            else
            {
                initWrapper = MirBuilder.FindInitWrapper(flow.Context.Symbols, template,
                    wrapperArguments.Count);
                if (initWrapper == null && wrapperArguments.Count > 0)
                {
                    throw new MwNotSupportedException(
                        $"new.wrapped 无匹配 {wrapperArguments.Count} 参 ..init.wrapper: {typeRef}");
                }
            }
            var sheetCanonical = GenericAbi.IsClosedConstructed(typeRef)
                ? MwTypeKey.Normalize(typeRef)
                : template.Canonical;
            // G1 值类型：开放构造不坍缩到模板 canonical——值类型无 alloc
            // sheet 消费，保留原 ref 供类级 typeid 代入（外层泛型占位名
            // 可与模板参数名不同，坍缩后 substitution 丢失实参名）
            if (template.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct
                && ConstructedTypeCollector.IsConstructed(typeRef))
            {
                sheetCanonical = MwTypeKey.Normalize(typeRef);
            }
            var type = sheetCanonical == template.Canonical
                ? template
                : new MwTypeSymbol(sheetCanonical, template);
            var wrappedArgs = wrapperArguments == null
                ? null
                : (IReadOnlyList<MirOperand>)FlowBuilder.Locals(wrapperArguments);
            if (template.Declaration.Kind == BilTypeKind.Class)
            {
                flow.Add(new MirNewObject(type, initWrapper, init,
                    FlowBuilder.Locals(initArguments), target, wrappedArgs));
                return;
            }
            if (template.Declaration.Kind is BilTypeKind.Struct or BilTypeKind.EnumStruct)
            {
                flow.Add(new MirNewValue(type, initWrapper, init,
                    FlowBuilder.Locals(initArguments), target, wrappedArgs));
                return;
            }
            throw new MwNotSupportedException($"MW4 new 暂不支持类型形态: {typeRef}");
        }
    }

    // new.wrapped type(T)（§14.4.1，MW10 刀5）：有参 ..init.wrapper 的构造
    internal sealed class NewWrappedLowering : MirLowerVisitor<NewWrappedLowering, NewWrappedInstruction>
    {
        protected override void VisitCore(NewWrappedInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            NewLowering.EmitConstruction(flow, inst.Type.TypeRef, inst.InitArguments,
                inst.WrapperArguments, inst.Target.Name);
        }
    }

    internal sealed class NewCaseLowering : MirLowerVisitor<NewCaseLowering, NewCaseInstruction>
    {
        protected override void VisitCore(NewCaseInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            EmitNewCase(flow, inst.Type.TypeRef, inst.Case.QualifiedName, inst.Arguments,
                wrapperArguments: null, inst.Target.Name);
        }

        // new.case / new.wrapped.case 共用的 case 构造翻译（L1 抽出）：
        // wrapperArguments 非空 = §14.4.2 形态——按 wrapper 实参个数匹配
        // 有参 ..init.wrapper（VM PushConstructorTail 的
        // TryFindInitWrapper + 实参个数校验同口径），无匹配受控拒绝
        //（VM 运行期「类型没有 ..init.wrapper / 实参个数不匹配」同形
        // 异常；native 编译期拒绝，同 new.case init 失配先例）
        internal static void EmitNewCase(FlowBuilder flow, string typeRef,
            string caseQualifiedName, IReadOnlyList<BilVariableOperand> caseArguments,
            IReadOnlyList<BilVariableOperand>? wrapperArguments, string target)
        {
            var (type, caseSymbol) = flow.ResolveCase(typeRef, caseQualifiedName);
            MwMemberSymbol? init = null;
            try
            {
                // G1：构造 enum 的 init 匹配代入构造实参（同 new 路径口径）
                init = MirBuilder.ResolveInit(flow.Context.Symbols, type,
                    flow.ArgTypes(caseArguments), skipReceiver: 0,
                    constructedTypeRef: typeRef);
            }
            catch (MwNotSupportedException) when (caseArguments.Count == 0)
            {
                // 无 init 声明 + 零实参：仅写判别（VM NewCase 同口径）
            }
            if (init == null && caseArguments.Count > 0)
            {
                throw new MwNotSupportedException(
                    $"new.case 实参不匹配任何 init: {typeRef}（VM 运行期同形异常）");
            }
            MwMemberSymbol? initWrapper = null;
            if (wrapperArguments != null)
            {
                initWrapper = MirBuilder.FindInitWrapper(flow.Context.Symbols, type,
                    wrapperArguments.Count);
                if (initWrapper == null && wrapperArguments.Count > 0)
                {
                    throw new MwNotSupportedException(
                        $"new.wrapped.case 无匹配 {wrapperArguments.Count} 参 "
                        + $"..init.wrapper: {typeRef}（VM 运行期同形异常）");
                }
            }
            flow.Add(new MirNewCase(caseSymbol, init, FlowBuilder.Locals(caseArguments),
                target, initWrapper,
                wrapperArguments == null
                    ? null
                    : (IReadOnlyList<MirOperand>)FlowBuilder.Locals(wrapperArguments)));
        }
    }

    // new.wrapped.case type(E) case(E.C) TARGET [WRAPPER_ARGS] [CASE_ARGS]
    //（§14.4.2，L1）：有参 ..init.wrapper 的 enum case 构造
    internal sealed class NewWrappedCaseLowering
        : MirLowerVisitor<NewWrappedCaseLowering, NewWrappedCaseInstruction>
    {
        protected override void VisitCore(NewWrappedCaseInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            NewCaseLowering.EmitNewCase(flow, inst.Type.TypeRef, inst.Case.QualifiedName,
                inst.CaseArguments, inst.WrapperArguments, inst.Target.Name);
        }
    }
}
