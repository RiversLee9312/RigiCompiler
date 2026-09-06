using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 类型运算簇：getid / type.is / is.case / cast / wrapper 安装占位。

    internal sealed class GetIdTypeLowering
        : MirLowerVisitor<GetIdTypeLowering, GetIdTypeInstruction>
    {
        protected override void VisitCore(GetIdTypeInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            // L1：登记静态 id 追踪（get.wrapper.indirect 等 indirect 族的
            // lowering 期解析用）；值本身照常经 MirGetTypeId 物化
            flow.NoteTypeId(inst.Target.Name, MwTypeKey.Normalize(inst.TargetType.TypeRef));
            flow.Add(new MirGetTypeId(inst.TargetType.TypeRef, inst.Target.Name));
        }
    }

    // §12.6 取得字段 fieldid：getid.field field(FIELD) TARGET_FIELDID。
    // fieldid 的运行时值在 native 无消费面（indirect 族 lowering 期经
    // 静态追踪解析回字段符号，与直译版同路径）；MIR 节点仅占位登记
    //（发射 = 槽内写 null，槽型 .fieldid → ptr，见 TypeLayout）
    internal sealed class GetIdFieldLowering
        : MirLowerVisitor<GetIdFieldLowering, GetIdFieldInstruction>
    {
        protected override void VisitCore(GetIdFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.NoteFieldId(inst.Target.Name, inst.Field.Symbol);
            flow.Add(new MirGetFieldId(inst.Field.Symbol, inst.Target.Name));
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

    // §12.1/§12.2 动态转换（cast.indirect / cast.safe.indirect）：目标
    // sheet 运行期取自 TYPEID_VAR 局部，直译复用 MirCast 的 indirect 形态
    //（CastEmitter.EmitDynamic：装箱源 + rigi_try_cast + 命中改写视图，
    // 落空按 IsSafe 产 null 或抛 CastException——与 VM CastOrThrow/
    // CastSafe(RequireTypeId) 逐条同语义）
    internal sealed class CastIndirectLowering
        : MirLowerVisitor<CastIndirectLowering, CastIndirectInstruction>
    {
        protected override void VisitCore(CastIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirCast(flow.Local(inst.Source), inst.Target.Name, inst.IsSafe,
                null, flow.Local(inst.TypeId), flow.Tries.CurrentExcTarget()));
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
            // 方法接收者的构造类 → 自身裸声明是 ABI 视图投影，须保留
            // 对象原有具化身份；不能把调用方同名 T 误当作接收者的实参。
            if (ConstructedTypeCollector.IsConstructed(sourceType.Canonical)
                && targetType.Canonical.IndexOf('<') < 0
                && flow.Context.Symbols.FindTypeByRef(sourceType.Canonical) is { } sourceClass
                && sourceClass.Declaration.Kind == BilTypeKind.Class
                && flow.Context.Symbols.FindTypeByRef(targetType.Canonical)?.Declaration
                    == sourceClass.Declaration)
            {
                flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
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
            // G1：泛型值类型「构造 → 裸模板」擦除 cast（frontend 对方法调用
            // 接收者的固定形态）——值语义恒等视图，降为拷贝并登记构造形态
            // 供类级 typeid 合成回溯（VM：cast 改写视图 typeid、无数据移动；
            // 值类型无视图可写，恒等即语义）
            if (flow.IsUserValueType(sourceType)
                && ConstructedTypeCollector.IsConstructed(sourceType.Canonical)
                && BilVerificationContext.StripTypeArguments(
                    MwTypeKey.Normalize(sourceType.Canonical)) == targetType.Canonical)
            {
                flow.NoteErasedValueHost(inst.Target.Name,
                    MwTypeKey.Normalize(sourceType.Canonical));
                flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
                return;
            }
            if (sourceType.IsAnyOrObject || MirBuilder.IsScalarOrString(sourceType)
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
