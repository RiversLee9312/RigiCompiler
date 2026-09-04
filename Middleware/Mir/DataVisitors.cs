using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 数据簇：load / 局部拷贝 / 字段 / 数组。无 RegionScope。

    internal sealed class LoadLowering : MirLowerVisitor<LoadLowering, LoadInstruction>
    {
        protected override void VisitCore(LoadInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirLoadResource(inst.Resource, inst.Target.Name));
        }
    }

    internal sealed class SetVarLowering : MirLowerVisitor<SetVarLowering, SetVarInstruction>
    {
        protected override void VisitCore(SetVarInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            // L1：typeid/fieldid 静态追踪随拷贝传播（indirect 族解析用）
            flow.PropagateIdCopy(inst.Source.Name, inst.Target.Name);
            flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
        }
    }

    internal sealed class GetVarLowering : MirLowerVisitor<GetVarLowering, GetVarInstruction>
    {
        protected override void VisitCore(GetVarInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.PropagateIdCopy(inst.Source.Name, inst.Target.Name);
            flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
        }
    }

    internal sealed class BinaryIntrinsicLowering
        : MirLowerVisitor<BinaryIntrinsicLowering, BinaryIntrinsicInstruction>
    {
        protected override void VisitCore(BinaryIntrinsicInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var leftType = flow.TypeOf(inst.Left.Name);
            if (ImplBinder.IsBuiltinBinaryOperand(leftType.Canonical))
            {
                flow.Add(new MirBinaryIntrinsic(inst.Op,
                    flow.Local(inst.Left), flow.Local(inst.Right),
                    leftType, flow.TypeOf(inst.Right.Name),
                    flow.TypeOf(inst.Target.Name), inst.Target.Name,
                    flow.Tries.CurrentExcTarget()));
                return;
            }
            UserOperatorLowering.LowerBinary(flow, inst, leftType);
        }
    }

    internal sealed class UnaryIntrinsicLowering
        : MirLowerVisitor<UnaryIntrinsicLowering, UnaryIntrinsicInstruction>
    {
        protected override void VisitCore(UnaryIntrinsicInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var operandType = flow.TypeOf(inst.Operand.Name);
            if (ImplBinder.IsBuiltinUnaryOperand(operandType.Canonical))
            {
                flow.Add(new MirUnaryIntrinsic(inst.Op, flow.Local(inst.Operand),
                    operandType, flow.TypeOf(inst.Target.Name), inst.Target.Name));
                return;
            }
            UserOperatorLowering.LowerUnary(flow, inst, operandType);
        }
    }

    // 遗1 用户运算符分派（VM DispatchUserBinary/ExecuteUnary 口径）：
    // 用户类型操作数的二元/一元 intrinsic 直译为 operator fn 调用
    //（MirCall OperatorDispatch——class 虚派发/interface iMap/值类型
    // 直调由 Binding.BindOperatorCall 回答，运行期按实际类型落最派生
    // 实现）。比较语义转换（SYNTAX §13.2）：== 直取 equals 的 bool；
    // != 调 equals 取反；< <= > >= 调 compareTo 得
    // core::ComparisonResult，按 case 判别映射 bool（VM OrderCompare
    // 同口径）。找不到 operator：编译期受控拒绝（VM 运行期抛
    // VmException「没有用户 operator …」；native 无运行期查找设施，
    // 编译期拒绝是其保守超集——分歧记遗1 报告）。泛型占位左操作数
    //（T extends Bound 内运算，G4）：直译 MirGenericBinaryOp，发射期
    // 运行期按实际 typeid 派发（GenericOpEmitter，VM ExecuteBinary 同
    // 口径——内建标量求值优先，否则按左操作数实际类型沿派生链找最
    // 具体实现，全落空抛 core.NoSuchMethodException）
    internal static class UserOperatorLowering
    {
        internal static void LowerBinary(FlowBuilder flow, BinaryIntrinsicInstruction inst,
            MirType leftType)
        {
            var rightType = flow.TypeOf(inst.Right.Name);
            var operatorName = ImplBinder.UserBinaryOperatorName(inst.Op);
            var target = inst.Target.Name;
            if (leftType.Canonical.Contains(".generic<", System.StringComparison.Ordinal))
            {
                flow.Add(new MirGenericBinaryOp(inst.Op,
                    flow.Local(inst.Left), flow.Local(inst.Right), leftType, rightType,
                    flow.TypeOf(target), target, flow.Tries.CurrentExcTarget()));
                return;
            }
            var member = ImplBinder.FindUserBinaryOperator(flow.Context.Symbols, inst.Op,
                leftType.Canonical, rightType.Canonical)
                ?? throw new MwNotSupportedException(
                    $"没有用户 operator {operatorName}：{leftType.Canonical}"
                    + "（VM 运行期同形异常；native 编译期拒绝）");
            var excTarget = flow.Tries.CurrentExcTarget();
            if (inst.Op == BilBinaryOp.CmpNe)
            {
                // != 由 equals 取反（VM DispatchUserBinary 同口径）
                var eq = flow.RegisterSyntheticLocal(SynthName(flow, "eq"),
                    MirType.Of(".bool"));
                flow.Add(new MirCall(member, Operands(inst.Left, inst.Right), eq.Name,
                    excTarget, operatorDispatch: true));
                flow.Add(new MirUnaryIntrinsic(BilUnaryOp.Not, new MirLocalOperand(eq.Name),
                    MirType.Of(".bool"), flow.TypeOf(target), target));
                return;
            }
            if (ImplBinder.IsOrderCompare(inst.Op))
            {
                LowerOrderCompare(flow, inst, member, excTarget, target);
                return;
            }
            flow.Add(new MirCall(member, Operands(inst.Left, inst.Right), target,
                excTarget, operatorDispatch: true));
        }

        internal static void LowerUnary(FlowBuilder flow, UnaryIntrinsicInstruction inst,
            MirType operandType)
        {
            var operatorName = ImplBinder.UserUnaryOperatorName(inst.Op);
            if (operandType.Canonical.Contains(".generic<",
                    System.StringComparison.Ordinal))
            {
                // G4：占位操作数一元运算——运行期按实际 typeid 派发
                flow.Add(new MirGenericUnaryOp(inst.Op, flow.Local(inst.Operand),
                    operandType, flow.TypeOf(inst.Target.Name), inst.Target.Name,
                    flow.Tries.CurrentExcTarget()));
                return;
            }
            var member = ImplBinder.FindUserUnaryOperator(flow.Context.Symbols, inst.Op,
                operandType.Canonical)
                ?? throw new MwNotSupportedException(
                    $"没有用户 operator {operatorName}：{operandType.Canonical}"
                    + "（VM 运行期同形异常；native 编译期拒绝）");
            flow.Add(new MirCall(member, new MirOperand[] { flow.Local(inst.Operand) },
                inst.Target.Name, flow.Tries.CurrentExcTarget(), operatorDispatch: true));
        }

        // < <= > >=：compareTo → ComparisonResult，按 case 判别组合 bool
        //（lesser/equal/greater 三态，VM OrderCompare 同口径）
        private static void LowerOrderCompare(FlowBuilder flow, BinaryIntrinsicInstruction inst,
            MwMemberSymbol member, MirBlock? excTarget, string target)
        {
            var cmp = flow.RegisterSyntheticLocal(SynthName(flow, "cmp"),
                MirType.Of("core::ComparisonResult"));
            flow.Add(new MirCall(member, Operands(inst.Left, inst.Right), cmp.Name,
                excTarget, operatorDispatch: true));
            var boolType = MirType.Of(".bool");
            string EmitIsCase(string caseName, string caseTarget)
            {
                var (_, caseSymbol) = flow.ResolveCase("core::ComparisonResult",
                    "core::ComparisonResult." + caseName);
                flow.Add(new MirIsCase(caseSymbol, new MirLocalOperand(cmp.Name), caseTarget));
                return caseTarget;
            }
            switch (inst.Op)
            {
                case BilBinaryOp.CmpLt:
                    EmitIsCase("LesserThanAnother", target);
                    break;
                case BilBinaryOp.CmpGt:
                    EmitIsCase("GreaterThanAnother", target);
                    break;
                case BilBinaryOp.CmpLe or BilBinaryOp.CmpGe:
                {
                    var primary = flow.RegisterSyntheticLocal(SynthName(flow, "case"), boolType);
                    var equal = flow.RegisterSyntheticLocal(SynthName(flow, "case"), boolType);
                    EmitIsCase(inst.Op == BilBinaryOp.CmpLe
                        ? "LesserThanAnother" : "GreaterThanAnother", primary.Name);
                    EmitIsCase("Equal", equal.Name);
                    flow.Add(new MirBinaryIntrinsic(BilBinaryOp.Or,
                        new MirLocalOperand(primary.Name), new MirLocalOperand(equal.Name),
                        boolType, boolType, flow.TypeOf(target), target, excTarget));
                    break;
                }
                default:
                    throw new CompilerInternalException($"非排序比较: {inst.Op}");
            }
        }

        private static MirOperand[] Operands(BilVariableOperand left, BilVariableOperand right)
        {
            return new MirOperand[] { new MirLocalOperand(left.Name),
                new MirLocalOperand(right.Name) };
        }

        private static string SynthName(FlowBuilder flow, string kind) =>
            "mw.opr." + kind + "." + flow.NextSynthetic();
    }

    internal sealed class GetFieldLowering : MirLowerVisitor<GetFieldLowering, GetFieldInstruction>
    {
        protected override void VisitCore(GetFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetField(flow.Local(inst.Object), inst.Field.Symbol,
                inst.Target.Name, flow.Tries.CurrentExcTarget()));
        }
    }

    // §13.5 间接实例字段族（L1）：FIELDID_VAR 局部经 FlowBuilder 静态
    // 追踪解析回字段符号，落与直译版相同的 MirGetField/MirSetField
    //（访问器/wrapper 链经 AccessorLoweringPass 同口径复用；VM：
    // RequireFieldId 后 GetField/SetField 同路径）。静态不可解析
    //（fieldid 跨函数流转等）受控拒绝——编译期拒绝是 VM 运行期解析
    // 的保守超集（同 get.wrapper.indirect / UserOperatorLowering 先例）
    internal sealed class GetFieldIndirectLowering
        : MirLowerVisitor<GetFieldIndirectLowering, GetFieldIndirectInstruction>
    {
        protected override void VisitCore(GetFieldIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var symbol = IndirectFieldResolve.Resolve(flow, inst.FieldId, "get.field.indirect");
            flow.Add(new MirGetField(flow.Local(inst.Object), symbol,
                inst.Target.Name, flow.Tries.CurrentExcTarget()));
        }
    }

    internal sealed class SetFieldIndirectLowering
        : MirLowerVisitor<SetFieldIndirectLowering, SetFieldIndirectInstruction>
    {
        protected override void VisitCore(SetFieldIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var symbol = IndirectFieldResolve.Resolve(flow, inst.FieldId, "set.field.indirect");
            flow.Add(new MirSetField(flow.Local(inst.Source), flow.Local(inst.Object), symbol));
        }
    }

    // §13.5 间接静态字段族（L1）：TYPEID_VAR 仅作类别校验（VM 同口径：
    // RequireTypeId 后弃置——字段符号自带 owner 段），字段符号静态解析
    // 后落 MirGetStatic/MirSetStatic（构造类型静态访问限制同直译版）
    internal sealed class GetFieldStaticIndirectLowering
        : MirLowerVisitor<GetFieldStaticIndirectLowering, GetFieldStaticIndirectInstruction>
    {
        protected override void VisitCore(GetFieldStaticIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var symbol = IndirectFieldResolve.Resolve(flow, inst.FieldId, "get.field.static.indirect");
            FlowBuilder.RejectConstructedStatic(symbol);
            flow.Add(new MirGetStatic(symbol, inst.Target.Name));
        }
    }

    internal sealed class SetFieldStaticIndirectLowering
        : MirLowerVisitor<SetFieldStaticIndirectLowering, SetFieldStaticIndirectInstruction>
    {
        protected override void VisitCore(SetFieldStaticIndirectInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            var symbol = IndirectFieldResolve.Resolve(flow, inst.FieldId, "set.field.static.indirect");
            FlowBuilder.RejectConstructedStatic(symbol);
            flow.Add(new MirSetStatic(flow.Local(inst.Source), symbol));
        }
    }

    file static class IndirectFieldResolve
    {
        internal static string Resolve(FlowBuilder flow, BilVariableOperand fieldId,
            string opcode)
        {
            return flow.TryResolveFieldIdSymbol(fieldId.Name, out var symbol)
                ? symbol
                : throw new MwNotSupportedException(
                    $"{opcode} 的 fieldid 静态不可解析: ${fieldId.Name}（fn {flow.FnSymbol}）");
        }
    }

    internal sealed class SetFieldLowering : MirLowerVisitor<SetFieldLowering, SetFieldInstruction>
    {
        protected override void VisitCore(SetFieldInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirSetField(flow.Local(inst.Source), flow.Local(inst.Object),
                inst.Field.Symbol));
        }
    }

    internal sealed class GetStaticLowering
        : MirLowerVisitor<GetStaticLowering, GetFieldStaticInstruction>
    {
        protected override void VisitCore(GetFieldStaticInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            FlowBuilder.RejectConstructedStatic(inst.Field.Symbol);
            flow.Add(new MirGetStatic(inst.Field.Symbol, inst.Target.Name));
        }
    }

    internal sealed class SetStaticLowering
        : MirLowerVisitor<SetStaticLowering, SetFieldStaticInstruction>
    {
        protected override void VisitCore(SetFieldStaticInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            FlowBuilder.RejectConstructedStatic(inst.Field.Symbol);
            flow.Add(new MirSetStatic(flow.Local(inst.Source), inst.Field.Symbol));
        }
    }

    internal sealed class GetArrayLowering : MirLowerVisitor<GetArrayLowering, GetArrayInstruction>
    {
        protected override void VisitCore(GetArrayInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirGetArray(flow.Local(inst.Array), flow.Local(inst.Index),
                flow.TypeOf(inst.Array.Name), inst.Target.Name));
        }
    }

    internal sealed class SetArrayLowering : MirLowerVisitor<SetArrayLowering, SetArrayInstruction>
    {
        protected override void VisitCore(SetArrayInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            flow.Add(new MirSetArray(flow.Local(inst.Collection), flow.Local(inst.Index),
                flow.Local(inst.Element), flow.TypeOf(inst.Collection.Name),
                flow.Tries.CurrentExcTarget()));
        }
    }
}
