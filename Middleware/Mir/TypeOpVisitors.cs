using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 类型运算簇：getid / type.is / is.case / cast / wrapper 安装占位。

    internal sealed class GetIdTypeLowering
        : MirLowerVisitor<GetIdTypeLowering, GetIdTypeInstruction>
    {
        protected override void VisitCore(GetIdTypeInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetTypeId(inst.TargetType.TypeRef, inst.Target.Name));
        }
    }

    internal sealed class GetIdVarLowering
        : MirLowerVisitor<GetIdVarLowering, GetIdVarInstruction>
    {
        protected override void VisitCore(GetIdVarInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetTypeIdVar(flow.Local(inst.Value), inst.Target.Name));
        }
    }

    internal sealed class DirectTypeCheckLowering
        : MirLowerVisitor<DirectTypeCheckLowering, DirectTypeCheckInstruction>
    {
        protected override void VisitCore(DirectTypeCheckInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            EmitTypeCheck(flow, inst.Kind, flow.Local(inst.Value),
                inst.TargetType.TypeRef, null, inst.Target.Name);
        }

        internal static void EmitTypeCheck(FlowBuilder flow, BilTypeCheckKind kind,
            MirOperand value, string? targetTypeRef, MirOperand? targetTypeId, string target)
        {
            if (targetTypeRef != null
                && GenericAbi.TryPlaceholderName(targetTypeRef, out var name))
            {
                targetTypeId = new MirLocalOperand(".generic." + name);
                targetTypeRef = null;
            }
            flow.Add(new MirTypeCheck(MapKind(kind), value, targetTypeRef, targetTypeId, target));
        }

        internal static MirTypeCheckKind MapKind(BilTypeCheckKind kind) =>
            kind switch
            {
                BilTypeCheckKind.Is => MirTypeCheckKind.Is,
                BilTypeCheckKind.Supers => MirTypeCheckKind.Supers,
                BilTypeCheckKind.With => MirTypeCheckKind.With,
                _ => throw new CompilerInternalException("未知 BilTypeCheckKind: " + kind),
            };
    }

    internal sealed class IndirectTypeCheckLowering
        : MirLowerVisitor<IndirectTypeCheckLowering, IndirectTypeCheckInstruction>
    {
        protected override void VisitCore(IndirectTypeCheckInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            DirectTypeCheckLowering.EmitTypeCheck(flow, inst.Kind, flow.Local(inst.Value),
                null, flow.Local(inst.TypeId), inst.Target.Name);
        }
    }

    internal sealed class IsCaseLowering : MirLowerVisitor<IsCaseLowering, IsCaseInstruction>
    {
        protected override void VisitCore(IsCaseInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var (_, caseSymbol) = flow.ResolveCase(
                FlowBuilder.EnumOwnerOf(inst.Case.QualifiedName), inst.Case.QualifiedName);
            flow.Add(new MirIsCase(caseSymbol, flow.Local(inst.Value), inst.Target.Name));
        }
    }

    internal sealed class CastLowering : MirLowerVisitor<CastLowering, CastInstruction>
    {
        protected override void VisitCore(CastInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var sourceType = flow.TypeOf(inst.Source.Name);
            var targetType = MirType.Of(inst.TargetType.TypeRef);
            var resultType = flow.TypeOf(inst.Target.Name);
            var excTarget = flow.Tries.CurrentExcTarget();
            if (TypeLayout.TryGetNullableInner(sourceType, out var unwrapInner)
                && unwrapInner.Canonical == targetType.Canonical)
            {
                flow.Add(new MirUnwrapNullable(flow.Local(inst.Source), unwrapInner,
                    inst.Target.Name));
                return;
            }
            if (TypeLayout.TryGetNullableInner(targetType, out var wrapInner)
                && wrapInner.Canonical == sourceType.Canonical)
            {
                flow.Add(new MirWrapNullable(flow.Local(inst.Source), wrapInner,
                    inst.Target.Name));
                return;
            }
            if (flow.IsBoxableValueType(sourceType) && targetType.IsAnyOrObject)
            {
                flow.Add(new MirBoxAny(flow.Local(inst.Source), inst.Target.Name));
                return;
            }
            if (sourceType.IsAnyOrObject && flow.IsBoxableValueType(targetType))
            {
                flow.Add(new MirUnboxAny(flow.Local(inst.Source), inst.Target.Name,
                    excTarget));
                return;
            }
            if (GenericAbi.TryPlaceholderName(targetType.Canonical, out var phName))
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, null, new MirLocalOperand(".generic." + phName),
                    excTarget));
                return;
            }
            if (TypeLayout.IsGenericPlaceholder(sourceType))
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, targetType.Canonical, null, excTarget));
                return;
            }
            if (MirBuilder.IsNumericScalar(sourceType)
                && MirBuilder.IsNumericScalar(targetType))
            {
                if (sourceType.Key == targetType.Key
                    && !TypeLayout.IsNullable(resultType))
                {
                    flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
                    return;
                }
                if (sourceType.Key == targetType.Key
                    && TypeLayout.TryGetNullableInner(resultType, out var numInner)
                    && numInner.Canonical == targetType.Canonical)
                {
                    flow.Add(new MirWrapNullable(flow.Local(inst.Source), numInner,
                        inst.Target.Name));
                    return;
                }
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, targetType.Canonical, null, excTarget));
                return;
            }
            if (sourceType.Canonical == targetType.Canonical)
            {
                if (TypeLayout.TryGetNullableInner(resultType, out var idInner)
                    && idInner.Canonical == targetType.Canonical)
                {
                    flow.Add(new MirWrapNullable(flow.Local(inst.Source), idInner,
                        inst.Target.Name));
                    return;
                }
                flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
                return;
            }
            if (MirBuilder.IsScalarOrString(sourceType)
                || MirBuilder.IsScalarOrString(targetType)
                || TypeLayout.IsTypeId(sourceType) || TypeLayout.IsTypeId(targetType)
                || flow.IsUserValueType(sourceType) || flow.IsUserValueType(targetType))
            {
                flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name,
                    inst.IsSafe, targetType.Canonical, null, excTarget));
                return;
            }
            flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
        }
    }
}
