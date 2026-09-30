using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Passes;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Mir
{
    // BIL→MIR 数据簇：load / 局部拷贝 / 字段 / 数组。无 RegionScope。

    internal sealed class LoadLowering : MirLowerVisitor<LoadLowering, LoadInstruction>
    {
        protected override void VisitCore(LoadInstruction inst, FlowBuilder flow)
        {
            flow.EnsureOpen();
            // null 常量物化登记：BinaryIntrinsicLowering 识别「x ==/!= null」
            // nullness-only 形态用（位比即语义，不走 Nullable 判等展开）
            if (inst.Resource is BilNullResource)
            {
                flow.MarkNullConstant(inst.Target.Name);
            }
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
            // 同型 Nullable<T> 判等只以 nullness 短路；双非空必须解包
            // 内层调用原有判等（包含开放占位的动态 typeid operator）。
            // 胖值身份即便相同也不能提前宣判相等：用户 equals 可返回 false。
            // 异内层类型不经此特判，仍走既有语言类型规则。
            // 「x ==/!= null」nullness-only 形态（一侧是 null 常量物化局部）
            // 不展开：位比即语义（ImplBinder Nullable 位比规则正只服务该
            // 形态）；且展开的双非空臂对 wrapper bake 特化体有害——特化
            //（ProxyBakeSupport.BuildSpecializedBody）只替换局部槽类型、
            // 不重写指令内嵌类型，模板期按占位生成的 MirGenericBinaryOp
            // 会配上具化标量槽（如 Temporary<TField> 的 i32），发射期
            // BuildExtractValue 对非聚合直接 SIGSEGV（编译器进程崩）
            if (inst.Op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe
                && Layout.TypeLayout.TryGetNullableInner(leftType, out var nullableInner)
                && flow.TypeOf(inst.Right.Name).Canonical == leftType.Canonical
                && !flow.IsNullConstant(inst.Left.Name)
                && !flow.IsNullConstant(inst.Right.Name))
            {
                NullableEqualityLowering.Lower(flow, inst, leftType, nullableInner);
                return;
            }
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

    // 同型可空判等：分别检查两侧 nullness，双空 true、单空 false；
    // 双非空无论胖值位形是否相同，都解包两侧并按 T 既有判等派发。
    // 占位内层 unwrap 写入 16B 胖槽，MirGenericBinaryOp 按实际 typeid
    // 依次检查标量/String、用户 operator 与 Any 默认 equals；不可将
    // 8B 标量/TypeId 擦除槽直接传给其 BuildExtractValue（旧 406）。
    // != 只在合并块对判等结果取反；可达边与该展开同构。
    internal static class NullableEqualityLowering
    {
        internal static void Lower(FlowBuilder flow, BinaryIntrinsicInstruction inst,
            MirType nullableType, MirType inner)
        {
            var boolType = MirType.Of(".bool");
            var excTarget = flow.Tries.CurrentExcTarget();
            // 判等结果先落合成槽，合并块按 ==/!= 落目标或取反
            var eqName = flow.RegisterSyntheticLocal(SynthName(flow, "nulleq"),
                boolType).Name;
            var bothNullId = flow.SyntheticId("nulleq.both");
            var singleNullId = flow.SyntheticId("nulleq.single");
            var innerId = flow.SyntheticId("nulleq.inner");
            var doneId = flow.SyntheticId("nulleq.done");
            // Nullable 的位比仅与 null 常量比较；不可比较左右位形
            // 后因同盒/同对象直接断言相等，用户 equals 有最终裁决权。
            var nullConst = flow.RegisterSyntheticLocal(SynthName(flow, "nullres"),
                nullableType).Name;
            flow.Add(new MirLoadResource(
                new BilNullResource(SynthName(flow, "null"), nullableType.Canonical),
                nullConst));
            var leftNull = flow.RegisterSyntheticLocal(SynthName(flow, "lnull"),
                boolType).Name;
            flow.Add(new MirBinaryIntrinsic(BilBinaryOp.CmpEq,
                flow.Local(inst.Left), new MirLocalOperand(nullConst),
                nullableType, nullableType, boolType, leftNull, excTarget));
            var rightNull = flow.RegisterSyntheticLocal(SynthName(flow, "rnull"),
                boolType).Name;
            flow.Add(new MirBinaryIntrinsic(BilBinaryOp.CmpEq,
                flow.Local(inst.Right), new MirLocalOperand(nullConst),
                nullableType, nullableType, boolType, rightNull, excTarget));
            var bothNull = flow.RegisterSyntheticLocal(SynthName(flow, "bnull"),
                boolType).Name;
            flow.Add(new MirBinaryIntrinsic(BilBinaryOp.And,
                new MirLocalOperand(leftNull), new MirLocalOperand(rightNull),
                boolType, boolType, boolType, bothNull, excTarget));
            flow.Terminate(new MirCondBranch(new MirLocalOperand(bothNull),
                bothNullId, singleNullId));
            SealConstBranch(flow, bothNullId, eqName, constText: "true", doneId);

            flow.SealAndStart(singleNullId);
            var eitherNull = flow.RegisterSyntheticLocal(SynthName(flow, "enull"),
                boolType).Name;
            flow.Add(new MirBinaryIntrinsic(BilBinaryOp.Or,
                new MirLocalOperand(leftNull), new MirLocalOperand(rightNull),
                boolType, boolType, boolType, eitherNull, excTarget));
            var singleNullArmId = flow.SyntheticId("nulleq.single.arm");
            flow.Terminate(new MirCondBranch(new MirLocalOperand(eitherNull),
                singleNullArmId, innerId));
            SealConstBranch(flow, singleNullArmId, eqName, constText: "false", doneId);

            // 双非空臂：解包两侧，递归内层判等 lowering（恒按 == 求值——
            // 合成槽是「判等」语义，!= 的取反只在合并块做一次，避免双取反）
            flow.SealAndStart(innerId);
            var unwrappedLeft = flow.RegisterSyntheticLocal(SynthName(flow, "ul"),
                inner).Name;
            flow.Add(new MirUnwrapNullable(flow.Local(inst.Left), inner,
                unwrappedLeft, excTarget));
            var unwrappedRight = flow.RegisterSyntheticLocal(SynthName(flow, "ur"),
                inner).Name;
            flow.Add(new MirUnwrapNullable(flow.Local(inst.Right), inner,
                unwrappedRight, excTarget));
            LowerInnerEquality(flow, BilBinaryOp.CmpEq, unwrappedLeft,
                unwrappedRight, inner, eqName);
            flow.Terminate(new MirBranch(doneId));

            // 合并块：== 直落，!= 取反
            flow.SealAndStart(doneId);
            if (inst.Op == BilBinaryOp.CmpEq)
            {
                flow.Add(new MirCopyLocal(new MirLocalOperand(eqName),
                    inst.Target.Name));
                return;
            }
            flow.Add(new MirUnaryIntrinsic(BilUnaryOp.Not,
                new MirLocalOperand(eqName), boolType, boolType, inst.Target.Name));
        }

        // 内层判等递归：与 BinaryIntrinsicLowering 的非可空二分同口径
        // （内建族 → MirBinaryIntrinsic；否则用户 operator lowering）
        private static void LowerInnerEquality(FlowBuilder flow, BilBinaryOp op,
            string leftName, string rightName, MirType inner, string target)
        {
            var excTarget = flow.Tries.CurrentExcTarget();
            if (ImplBinder.IsBuiltinBinaryOperand(inner.Canonical))
            {
                flow.Add(new MirBinaryIntrinsic(op, new MirLocalOperand(leftName),
                    new MirLocalOperand(rightName), inner, inner,
                    MirType.Of(".bool"), target, excTarget));
                return;
            }
            var innerInst = new BinaryIntrinsicInstruction(op,
                new BilVariableOperand(leftName), new BilVariableOperand(rightName),
                new BilVariableOperand(target));
            UserOperatorLowering.LowerBinary(flow, innerInst, inner);
        }

        // 常量布尔臂：物化 true/false 资源 → 判等合成槽 → 跳合并块
        private static void SealConstBranch(FlowBuilder flow, string id,
            string eqName, string constText, string doneId)
        {
            flow.SealAndStart(id);
            var constLocal = flow.RegisterSyntheticLocal(SynthName(flow, "c"),
                MirType.Of(".bool")).Name;
            flow.Add(new MirLoadResource(new BilScalarResource(
                SynthName(flow, "bool"), BilScalarType.Bool, constText), constLocal));
            flow.Add(new MirCopyLocal(new MirLocalOperand(constLocal), eqName));
            flow.Terminate(new MirBranch(doneId));
        }

        private static string SynthName(FlowBuilder flow, string kind) =>
            "mw.nulleq." + kind + "." + flow.NextSynthetic();
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
            // 构造宿主即使含开放实参仍有确定 operator；仅顶层占位需运行期搜索。
            if (Layout.GenericAbi.TryPlaceholderName(leftType.Canonical, out _))
            {
                flow.Add(new MirGenericBinaryOp(inst.Op,
                    flow.Local(inst.Left), flow.Local(inst.Right), leftType, rightType,
                    flow.TypeOf(target), target, flow.Tries.CurrentExcTarget()));
                return;
            }
            var member = ImplBinder.FindUserBinaryOperator(flow.Context.Symbols, inst.Op,
                leftType.Canonical, rightType.Canonical);
            if (member == null && inst.Op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe)
            {
                // Any 默认 equals 臂（==/!= 判等，SYNTAX §13.2，用户裁定）：
                // 左操作数静态链上没有 equals 声明时直调合成默认体（双虚调
                // hash 比较，equals-or-hash 判等链，绝不涉 toString；体内
                // Any$hash invoke 经 FlowBuilder 重定向 +
                // BuiltinToStringDispatchPass 得 override 感知派发；值类型/
                // 标量实参经 EmitDirectCall 的 CoerceArg 装箱为 .any）。
                // 已知边界：静态链无 equals 而运行期实际类型（子类 hiding
                // 再定义）有 equals 时，VM DispatchUserBinary 按实际类型
                // 派发用户 equals，native 直调默认体——分歧仅限该组合；
                // 泛型占位臂（Map 主路径）两端一致
                //（合成 fn 只有 fn 定义、无符号段声明——FindMember 查不到，
                // 与 toString/hash 先例同；用 SyntheticMember 直调，fn 是否
                // 在场以 any_hash native 声明为门，同 LocalSymbolEmitters
                // 的发射门控）
                member = flow.Context.Symbols.FindMember("core::$any_hash(value:.any)@.i64")
                    == null
                    ? null
                    : ProxyBakeSupport.SyntheticMember(ImplBinder.AnyEqualsCanonical, null);
            }
            if (member == null)
            {
                throw new MwNotSupportedException(
                    $"没有用户 operator {operatorName}：{leftType.Canonical}"
                    + "（VM 运行期同形异常；native 编译期拒绝）");
            }
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
            if (Layout.GenericAbi.TryPlaceholderName(operandType.Canonical, out _))
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
