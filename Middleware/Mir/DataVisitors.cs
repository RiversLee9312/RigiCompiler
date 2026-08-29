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
            flow.Add(new MirCopyLocal(flow.Local(inst.Source), inst.Target.Name));
        }
    }

    internal sealed class GetVarLowering : MirLowerVisitor<GetVarLowering, GetVarInstruction>
    {
        protected override void VisitCore(GetVarInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
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
    //（T extends Bound 内运算）静态不可唯一解析，同形拒绝。
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
                throw new MwNotSupportedException(
                    $"泛型占位左操作数的二元运算静态不可解析（VM 运行期按实际类型派发）: "
                    + $"{leftType.Canonical} 的 operator {operatorName}");
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
                throw new MwNotSupportedException(
                    $"泛型占位操作数的一元运算静态不可解析（VM 运行期按实际类型派发）: "
                    + $"{operandType.Canonical}");
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
