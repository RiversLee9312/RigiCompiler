using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// G4：泛型占位操作数运算的运行期派发（MirGenericBinaryOp /
    /// MirGenericUnaryOp）。VM ExecuteBinary/DispatchUserBinary 口径：
    /// 内建标量（含 String）按实际 sheet 逐臂求值优先；否则按左操作数
    /// 实际 typeid 经 rigi_type_is 逐候选臂判定（候选 = ImplBinder.
    /// CollectOperatorCandidates，派生深度降序 = VM 沿派生链先命中
    /// 最具体实现；候选普通形参再经右臂 type_is 校验，对齐 VM
    /// OperatorParamsMatch 的运行期可赋检查）；!= 调 equals 取反、
    /// &lt;/&lt;=/&gt;/&gt;= 调 compareTo 按 core::ComparisonResult 判别
    /// 映射 bool（SYNTAX §13.2）。全落空抛 core.NoSuchMethodException
    ///（VM VmException「没有用户 operator …」的语言级对应面）。
    /// 泛型 class 候选按已具化的构造 sheet 分臂，类级 typeid 由 receiver
    /// 隐藏槽恢复。语义边界（与 VM 的分歧，编译期受控拒绝/兜底）：泛型
    /// 值类型宿主的 operator 候选受控拒绝；方法级泛型 typeid 注入仅支持「普通
    /// 形参恰为占位」的精确形态，其余注入 core::Any（VM 推断失败的
    /// 缺省同口径）；Entity wrapper 的 operator 代理链不经本面（VM
    /// 会改道 .proxy.opr.*）；null-like 与 Span 恒等的 ==/!= 特判不在
    /// 此（非空占位操作数由前端类型系统保证；Span 不作泛型界使用）。
    /// </summary>
    internal static class GenericOpEmitter
    {
        internal sealed class Binary : LlvmEmitVisitor<Binary, MirGenericBinaryOp>
        {
            protected override void VisitCore(MirGenericBinaryOp inst, ModuleBuilder.Session session)
                => EmitBinary(session, session.Builder, session.Slots, inst);
        }

        internal sealed class Unary : LlvmEmitVisitor<Unary, MirGenericUnaryOp>
        {
            protected override void VisitCore(MirGenericUnaryOp inst, ModuleBuilder.Session session)
                => EmitUnary(session, session.Builder, session.Slots, inst);
        }

        // 内建臂类型集（VM VmTypeOps.IsPrimitiveOperand 同集）
        private static readonly string[] PrimitiveCanonicals =
        {
            "core::bool", "core::char",
            "core::i8", "core::u8", "core::i16", "core::u16",
            "core::i32", "core::u32", "core::i64", "core::u64",
            "core::float", "core::double", "core::String",
        };

        private static bool IsCompare(BilBinaryOp op) =>
            op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe or BilBinaryOp.CmpLt
                or BilBinaryOp.CmpLe or BilBinaryOp.CmpGt or BilBinaryOp.CmpGe;

        // ===== 二元 =====

        private static void EmitBinary(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGenericBinaryOp inst)
        {
            var fn = session.CurrentFunction;
            var leftFat = LoadFat(session, builder, slots, inst.Left);
            var rightFat = LoadFat(session, builder, slots, inst.Right);
            var leftTid = builder.BuildExtractValue(leftFat, 0, "gop.ltid");
            var leftPayload = builder.BuildExtractValue(leftFat, 1, "gop.lpl");
            var rightTid = builder.BuildExtractValue(rightFat, 0, "gop.rtid");
            var rightPayload = builder.BuildExtractValue(rightFat, 1, "gop.rpl");
            var done = fn.AppendBasicBlock("gop.done");

            // 内建臂（VM 先行 IsPrimitiveOperand 双操作数检查——两臂 sheet
            // 等值即同型；前端可达形态两操作数恒同型（同 T））
            foreach (var canonical in PrimitiveCanonicals)
            {
                EmitBinaryPrimitiveArm(session, builder, slots, inst, canonical,
                    IsCompare(inst.Op) ? "core::bool" : canonical, leftFat, rightFat,
                    leftTid, rightTid, done);
            }
            // 用户 operator 候选臂（左操作数实际类型沿派生链最具体实现）
            var operatorName = ImplBinder.UserBinaryOperatorName(inst.Op);
            foreach (var candidate in ImplBinder.CollectOperatorCandidates(
                session.Symbols, operatorName))
            {
                EmitBinaryCandidateArm(session, builder, slots, inst, candidate,
                    leftFat, leftTid, leftPayload, rightFat, rightTid, rightPayload, done);
            }
            // Any 默认 equals 臂（==/!= 判等，SYNTAX §13.2，用户裁定）：
            // 候选臂全落空（实际类型沿派生链无 equals 声明）时的末臂——
            // 直调合成 fn core::Any$$equals（双虚调 hash 比较，
            // equals-or-hash 判等链，绝不涉 toString）。与 VM
            // DispatchUserBinary 的 Any fallback 同口径——Map 主路径两端一致。
            // 臂命中即恒 br done；落空（合成 fn 不在模块）才续接 miss 块
            if (inst.Op is BilBinaryOp.CmpEq or BilBinaryOp.CmpNe)
            {
                var anyMiss = fn.AppendBasicBlock("gop.anymiss");
                if (!EmitAnyDefaultEqualsArm(session, builder, slots, inst,
                        leftFat, rightFat, done))
                {
                    builder.BuildBr(anyMiss);
                }
                builder.PositionAtEnd(anyMiss);
            }
            EmitOperatorMiss(session, builder, leftTid, leftPayload, operatorName,
                inst.ExcTarget);
            builder.PositionAtEnd(done);
        }

        // Any 默认 equals 末臂：胖值直通（形参 .any，无拆箱）→ 直调合成
        // fn → pending 检查 → bool 落槽（!= 取反，与候选臂同口径）→ br done。
        // 合成 fn 不在模块（无 stdlib 夹具）时返回 false（无臂），保持落空
        private static bool EmitAnyDefaultEqualsArm(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirGenericBinaryOp inst, LLVMValueRef leftFat, LLVMValueRef rightFat,
            LLVMBasicBlockRef done)
        {
            if (!session.TryGetFunction(ImplBinder.AnyEqualsCanonical, out var callee))
            {
                return false;
            }
            var flag = builder.BuildCall2(callee.Type, callee.Value, new[]
            {
                CoerceFatToParam(session, builder, leftFat, MirType.Of(".any")),
                CoerceFatToParam(session, builder, rightFat, MirType.Of(".any")),
            }, "gop.anyeq");
            ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
            if (inst.Op == BilBinaryOp.CmpNe)
            {
                flag = builder.BuildNot(flag, "gop.anyne");
            }
            builder.BuildStore(flag, slots[inst.Target].Slot);
            builder.BuildBr(done);
            return true;
        }

        // 内建臂：sheet 双等守卫 → 拆载荷 → BindBinary 同路径求值 → 落槽。
        // 该类型不支持本运算（BindBinary 受控拒绝）= 无臂（VM 同形运行期
        // 失败由落空臂兜底）
        private static void EmitBinaryPrimitiveArm(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirGenericBinaryOp inst, string canonical, string resultCanonical,
            LLVMValueRef leftFat, LLVMValueRef rightFat,
            LLVMValueRef leftTid, LLVMValueRef rightTid, LLVMBasicBlockRef done)
        {
            ImplBinding binding;
            try
            {
                binding = ImplBinder.BindBinary(inst.Op, canonical, canonical, resultCanonical);
            }
            catch (MwNotSupportedException)
            {
                return;
            }
            if (!TrySheetOf(session, canonical, out var sheet))
            {
                return;
            }
            var fn = session.CurrentFunction;
            var check = fn.AppendBasicBlock("gop.chk");
            var hit = fn.AppendBasicBlock("gop.hit");
            builder.BuildBr(check);
            builder.PositionAtEnd(check);
            var want = builder.BuildPtrToInt(sheet, LLVMTypeRef.Int64, "gop.want");
            var mask = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (long)BoxEmitter.SheetMask,
                false);
            var leftSheet = builder.BuildAnd(leftTid, mask, "gop.lsheet");
            var rightSheet = builder.BuildAnd(rightTid, mask, "gop.rsheet");
            var cond = builder.BuildAnd(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, leftSheet, want, "gop.l.eq"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, rightSheet, want, "gop.r.eq"),
                "gop.cond");
            var next = fn.AppendBasicBlock("gop.next");
            builder.BuildCondBr(cond, hit, next);
            builder.PositionAtEnd(next);

            builder.PositionAtEnd(hit);
            var declared = MirType.Of(canonical);
            var left = BoxEmitter.UnpackCtorArg(session, builder, leftFat, declared);
            var right = BoxEmitter.UnpackCtorArg(session, builder, rightFat, declared);
            LLVMValueRef value;
            switch (binding)
            {
                case PrimitiveOpBinding primitive:
                    if (primitive.Kind is PrimitiveOpKind.IntSDiv or PrimitiveOpKind.IntUDiv)
                    {
                        session.Checks.EmitDivGuard(session, builder, left, right,
                            isSigned: primitive.Kind == PrimitiveOpKind.IntSDiv,
                            inst.ExcTarget);
                    }
                    value = ScalarEmitter.SelectPrimitive(builder, primitive.Kind, left, right);
                    break;
                case RuntimeFaceBinding face:
                    value = CallEmitter.EmitFaceCall(session, builder, face.FaceSymbol,
                        new[] { left, right });
                    break;
                case StringCompareBinding:
                    value = ScalarEmitter.EmitStringCompare(session, builder, inst.Op,
                        left, right);
                    break;
                default:
                    throw new CompilerInternalException("G4 内建臂的非预期绑定形态");
            }
            StoreResultValue(session, builder, slots, value, MirType.Of(resultCanonical),
                inst.Target);
            builder.BuildBr(done);
            // 臂收尾：builder 归位 next（hit 已终结；下一臂从 next 续链）
            builder.PositionAtEnd(next);
        }

        // 候选臂：type_is(左, 宿主 sheet) ∧ 逐普通形参 type_is(右, 形参
        // sheet) 守卫 → 直调 operator fn（VM InvokeValues 普通调用语义）
        private static void EmitBinaryCandidateArm(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirGenericBinaryOp inst, MwMemberSymbol candidate,
            LLVMValueRef leftFat, LLVMValueRef leftTid, LLVMValueRef leftPayload,
            LLVMValueRef rightFat, LLVMValueRef rightTid, LLVMValueRef rightPayload,
            LLVMBasicBlockRef done, string? constructedOwner = null)
        {
            if (!session.TryGetFunction(candidate.Canonical, out var callee))
            {
                return;
            }
            var owner = candidate.Owner;
            if (owner == null) return;
            if (constructedOwner == null && owner.Declaration.GenericParameters.Count > 0
                && owner.Declaration.Kind == BilTypeKind.Class)
            {
                // 类级实参由 receiver 隐藏槽恢复；按真实闭合宿主分别守卫，
                // 不以模板 sheet 冒充实例身份，也不让未实例化的声明污染候选。
                foreach (var ownerRef in ConstructedOwners(session, owner))
                    EmitBinaryCandidateArm(session, builder, slots, inst, candidate,
                        leftFat, leftTid, leftPayload, rightFat, rightTid, rightPayload,
                        done, ownerRef);
                return;
            }
            if (!TrySheetOf(session, constructedOwner ?? owner.Canonical, out var ownerSheet))
            {
                return;
            }
            if (constructedOwner == null) RejectUnsupportedCandidate(candidate, owner);
            var signature = CanonicalSignature.Parse(candidate.Canonical);
            if (signature.Parameters.Count != 1)
            {
                return;   // 二元 operator 恰一普通形参（声明点校验；防御）
            }
            var paramType = signature.Parameters[0].TypeRef;
            if (constructedOwner != null)
                paramType = ConstructedTypeCollector.Substitute(paramType,
                    ConstructedTypeCollector.BuildSubstitution(constructedOwner, owner.Declaration));

            var fn = session.CurrentFunction;
            var check = fn.AppendBasicBlock("gop.chk");
            var hit = fn.AppendBasicBlock("gop.hit");
            builder.BuildBr(check);
            builder.PositionAtEnd(check);
            var cond = EmitTypeIs(session, builder, leftTid, leftPayload, ownerSheet);
            if (NeedsRuntimeParamCheck(paramType)
                && TrySheetOf(session, MwTypeKey.Normalize(paramType), out var paramSheet))
            {
                cond = builder.BuildAnd(cond,
                    EmitTypeIs(session, builder, rightTid, rightPayload, paramSheet),
                    "gop.pcond");
            }
            var next = fn.AppendBasicBlock("gop.next");
            builder.BuildCondBr(cond, hit, next);
            builder.PositionAtEnd(next);

            builder.PositionAtEnd(hit);
            EmitCandidateCall(session, builder, slots, candidate, callee,
                leftFat, leftTid, rightFat, rightTid, paramType,
                inst.Op, inst.ExcTarget, inst.Target, done);
            builder.PositionAtEnd(next);
        }

        // ===== 一元 =====

        private static void EmitUnary(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirGenericUnaryOp inst)
        {
            var fn = session.CurrentFunction;
            var operandFat = LoadFat(session, builder, slots, inst.Operand);
            var operandTid = builder.BuildExtractValue(operandFat, 0, "gop.tid");
            var operandPayload = builder.BuildExtractValue(operandFat, 1, "gop.pl");
            var done = fn.AppendBasicBlock("gop.done");

            foreach (var canonical in PrimitiveCanonicals)
            {
                EmitUnaryPrimitiveArm(session, builder, slots, inst, canonical,
                    operandFat, operandTid, done);
            }
            var operatorName = ImplBinder.UserUnaryOperatorName(inst.Op);
            foreach (var candidate in ImplBinder.CollectOperatorCandidates(
                session.Symbols, operatorName))
            {
                EmitUnaryCandidateArm(session, builder, slots, inst, candidate,
                    operandFat, operandTid, operandPayload, done);
            }
            EmitOperatorMiss(session, builder, operandTid, operandPayload, operatorName,
                inst.ExcTarget);
            builder.PositionAtEnd(done);
        }

        private static void EmitUnaryPrimitiveArm(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirGenericUnaryOp inst, string canonical, LLVMValueRef operandFat,
            LLVMValueRef operandTid, LLVMBasicBlockRef done)
        {
            ImplBinding binding;
            try
            {
                binding = ImplBinder.BindUnary(inst.Op, canonical, canonical);
            }
            catch (MwNotSupportedException)
            {
                return;
            }
            if (binding is not PrimitiveOpBinding primitive
                || !TrySheetOf(session, canonical, out var sheet))
            {
                return;
            }
            var fn = session.CurrentFunction;
            var check = fn.AppendBasicBlock("gop.chk");
            var hit = fn.AppendBasicBlock("gop.hit");
            builder.BuildBr(check);
            builder.PositionAtEnd(check);
            var want = builder.BuildPtrToInt(sheet, LLVMTypeRef.Int64, "gop.want");
            var mask = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (long)BoxEmitter.SheetMask,
                false);
            var sheetBits = builder.BuildAnd(operandTid, mask, "gop.sheet");
            var cond = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sheetBits, want, "gop.cond");
            var next = fn.AppendBasicBlock("gop.next");
            builder.BuildCondBr(cond, hit, next);
            builder.PositionAtEnd(next);

            builder.PositionAtEnd(hit);
            var operand = BoxEmitter.UnpackCtorArg(session, builder, operandFat,
                MirType.Of(canonical));
            var value = primitive.Kind switch
            {
                PrimitiveOpKind.IntNeg => builder.BuildNeg(operand, "gop.neg"),
                PrimitiveOpKind.FloatNeg => builder.BuildFNeg(operand, "gop.fneg"),
                PrimitiveOpKind.LogicNot => builder.BuildNot(operand, "gop.not"),
                PrimitiveOpKind.BitNot => builder.BuildNot(operand, "gop.binnot"),
                _ => throw new CompilerInternalException($"G4 未覆盖的一元指令选择: {primitive.Kind}"),
            };
            StoreResultValue(session, builder, slots, value, MirType.Of(canonical),
                inst.Target);
            builder.BuildBr(done);
            builder.PositionAtEnd(next);
        }

        private static void EmitUnaryCandidateArm(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirGenericUnaryOp inst, MwMemberSymbol candidate,
            LLVMValueRef operandFat, LLVMValueRef operandTid, LLVMValueRef operandPayload,
            LLVMBasicBlockRef done, string? constructedOwner = null)
        {
            if (!session.TryGetFunction(candidate.Canonical, out var callee))
            {
                return;
            }
            var owner = candidate.Owner;
            if (owner == null) return;
            if (constructedOwner == null && owner.Declaration.GenericParameters.Count > 0
                && owner.Declaration.Kind == BilTypeKind.Class)
            {
                foreach (var ownerRef in ConstructedOwners(session, owner))
                    EmitUnaryCandidateArm(session, builder, slots, inst, candidate,
                        operandFat, operandTid, operandPayload, done, ownerRef);
                return;
            }
            if (!TrySheetOf(session, constructedOwner ?? owner.Canonical, out var ownerSheet))
            {
                return;
            }
            if (constructedOwner == null) RejectUnsupportedCandidate(candidate, owner);
            var signature = CanonicalSignature.Parse(candidate.Canonical);
            if (signature.Parameters.Count != 0)
            {
                return;   // 一元 operator 无普通形参（声明点校验；防御）
            }

            var fn = session.CurrentFunction;
            var check = fn.AppendBasicBlock("gop.chk");
            var hit = fn.AppendBasicBlock("gop.hit");
            builder.BuildBr(check);
            builder.PositionAtEnd(check);
            var cond = EmitTypeIs(session, builder, operandTid, operandPayload, ownerSheet);
            var next = fn.AppendBasicBlock("gop.next");
            builder.BuildCondBr(cond, hit, next);
            builder.PositionAtEnd(next);

            builder.PositionAtEnd(hit);
            EmitCandidateCall(session, builder, slots, candidate, callee,
                operandFat, operandTid, null, operandTid, null,
                null, inst.ExcTarget, inst.Target, done);
            builder.PositionAtEnd(next);
        }

        // ===== 候选调用与结果 =====

        private static IEnumerable<string> ConstructedOwners(ModuleBuilder.Session session,
            MwTypeSymbol owner)
        {
            if (session.Layout == null) yield break;
            foreach (var plan in session.Layout.Plans)
            {
                var typeRef = plan.Symbol.Canonical;
                if (GenericAbi.IsClosedConstructed(typeRef)
                    && session.Symbols.FindTypeByRef(typeRef)?.Declaration == owner.Declaration)
                    yield return typeRef;
            }
        }

        // 未经闭合 class 分臂适配的泛型宿主仍受控拒绝：值类型的
        // 类级 typeid 不能沿用 class 的 receiver 隐藏字段 ABI。
        private static void RejectUnsupportedCandidate(MwMemberSymbol candidate,
            MwTypeSymbol owner)
        {
            if (owner.Declaration.GenericParameters.Count > 0)
            {
                throw new MwNotSupportedException(
                    $"泛型占位运算的候选 operator 宿主为泛型类型: {candidate.Canonical}"
                    + "（构造 sheet 无法反解类型实参/模板身份；VM 按字符串 TypeRef 解析）");
            }
        }

        // 候选直调：sret（值类型返回，含 compareTo 的 ComparisonResult）→
        // pending 检查 → 结果落槽（比较族固定 bool；!= 取反；排序按判别
        // 映射）→ br done
        private static void EmitCandidateCall(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MwMemberSymbol candidate, EmittedFunction callee,
            LLVMValueRef receiverFat, LLVMValueRef receiverTid,
            LLVMValueRef? argFat, LLVMValueRef argTid, string? paramTypeRef,
            BilBinaryOp? op, MirBlock? excTarget, string target, LLVMBasicBlockRef done)
        {
            var returnType = callee.Mir.ReturnType;
            var targetType = slots[target].Local.Type;
            var hasOut = session.IsInlineValueType(returnType, out var outPlan);
            // sret 出参：目标是同形内联值槽 → 直写；否则开临时槽后装箱移交
            var outDirect = hasOut && session.IsInlineValueType(targetType, out _);
            LLVMValueRef outSlot = default;
            if (hasOut)
            {
                outSlot = outDirect
                    ? slots[target].Slot
                    : LlvmEmitEnvironment.BuildEntryAlloca(builder,
                        LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)outPlan.Size),
                        "gop.out");
                if (!outDirect)
                {
                    outSlot.Alignment = (uint)outPlan.Alignment;
                }
            }
            var callArgs = MarshalCandidateArgs(session, builder, callee,
                receiverFat, receiverTid, argFat, argTid, paramTypeRef);
            if (hasOut)
            {
                var withOut = new LLVMValueRef[callArgs.Length + 1];
                withOut[0] = outSlot;
                System.Array.Copy(callArgs, 0, withOut, 1, callArgs.Length);
                callArgs = withOut;
            }
            var callResult = builder.BuildCall2(callee.Type, callee.Value, callArgs,
                hasOut ? "" : "gop.call");
            ExceptionEmitter.EmitPendingCheck(session, builder, excTarget);

            if (op == BilBinaryOp.CmpEq || op == BilBinaryOp.CmpNe)
            {
                // equals → bool；!= 取反（VM DispatchUserBinary 同口径）
                var flag = op == BilBinaryOp.CmpNe
                    ? builder.BuildNot(callResult, "gop.ne")
                    : callResult;
                builder.BuildStore(flag, slots[target].Slot);
                builder.BuildBr(done);
                return;
            }
            if (op != null && ImplBinder.IsOrderCompare(op.Value))
            {
                // compareTo → ComparisonResult（enum 判别 u32 @ 偏移 0）映射
                // bool（VM OrderCompare 同口径；无载荷字段，临时槽免 release）
                var disc = builder.BuildLoad2(LLVMTypeRef.Int32, outSlot, "gop.disc");
                var plan = outPlan;
                var lesser = DiscriminantOf(plan, "LesserThanAnother");
                var greater = DiscriminantOf(plan, "GreaterThanAnother");
                LLVMValueRef flag2 = op.Value switch
                {
                    BilBinaryOp.CmpLt => builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, disc,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, lesser, false), "gop.lt"),
                    BilBinaryOp.CmpGt => builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, disc,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, greater, false), "gop.gt"),
                    BilBinaryOp.CmpLe => builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, disc,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, greater, false), "gop.le"),
                    _ => builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, disc,
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, lesser, false), "gop.ge"),
                };
                builder.BuildStore(flag2, slots[target].Slot);
                builder.BuildBr(done);
                return;
            }
            if (hasOut)
            {
                if (!outDirect)
                {
                    // 值类型结果 → 胖槽目标：装箱移交（callee 产出的 +1
                    // 随块/位移交目标槽，临时槽不 release）
                    builder.BuildStore(BoxFreshValue(session, builder, outSlot,
                        returnType), slots[target].Slot);
                }
                builder.BuildBr(done);
                return;
            }
            StoreProducedResult(session, builder, slots, returnType, callResult, target);
            builder.BuildBr(done);
        }

        // ComparisonResult 判别值（按 case 名后缀从 enum 计划查表）
        private static uint DiscriminantOf(TypeLayoutPlan plan, string caseSuffix)
        {
            foreach (var (caseSymbol, discriminant) in plan.EnumCases)
            {
                if (caseSymbol.Declaration.QualifiedName.EndsWith("." + caseSuffix,
                    System.StringComparison.Ordinal))
                {
                    return discriminant;
                }
            }
            throw new CompilerInternalException("ComparisonResult 缺 case: " + caseSuffix);
        }

        // 候选调用实参拼装（与 EmitDirectCall 同序：fn 参数位序）：
        // .this ← 派发接收者（值类型拆载荷借用副本/标量拆值/引用直通）；
        // 类级 typeid（class 宿主不进 LLVM 约定，跳过）；方法级 typeid ←
        // 精确占位形参对应的实际实参 typeid（无映射 = core::Any 兜底，
        // VM InjectOperatorTypeIds 缺省同口径）；普通形参 ← 右操作数
        private static LLVMValueRef[] MarshalCandidateArgs(ModuleBuilder.Session session,
            LLVMBuilderRef builder, EmittedFunction callee,
            LLVMValueRef receiverFat, LLVMValueRef receiverTid,
            LLVMValueRef? argFat, LLVMValueRef argTid, string? paramTypeRef)
        {
            var args = new List<LLVMValueRef>(callee.Mir.Parameters.Count);
            foreach (var parameter in callee.Mir.Parameters)
            {
                if (parameter.Name == ".this")
                {
                    args.Add(CoerceFatToParam(session, builder, receiverFat, parameter.Type));
                    continue;
                }
                if (GenericAbi.IsClassLevelTypeId(session.Symbols, callee.Mir.Symbol, parameter.Name))
                {
                    continue;   // class 宿主已从 LLVM 约定剔除（被调方自取）
                }
                if (parameter.Name.StartsWith(".generic.", System.StringComparison.Ordinal))
                {
                    // 方法级 typeid：普通形参恰为占位时映射到右操作数实际
                    // typeid（VM InferGenericBindings 精确形）；否则 Any 兜底
                    var paramName = parameter.Name.Substring(".generic.".Length);
                    if (paramTypeRef != null
                        && GenericAbi.TryPlaceholderName(paramTypeRef, out var placeholder)
                        && placeholder == paramName)
                    {
                        args.Add(IntToPtrSheet(builder, argTid));
                    }
                    else
                    {
                        args.Add(session.TypeSheetFor("core::Any"));
                    }
                    continue;
                }
                if (argFat == null)
                {
                    throw new CompilerInternalException(
                        $"G4 候选形参超派发实参数: {callee.Mir.Symbol.Canonical}");
                }
                args.Add(CoerceFatToParam(session, builder, argFat.Value, parameter.Type));
            }
            return args.ToArray();
        }

        private static LLVMValueRef IntToPtrSheet(LLVMBuilderRef builder, LLVMValueRef typeId)
        {
            var bits = builder.BuildAnd(typeId,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (long)BoxEmitter.SheetMask, false),
                "gop.tidmask");
            return builder.BuildIntToPtr(bits, PointerType(), "gop.tidptr");
        }

        // 胖值 → 形参形态：标量/String 拆值；用户值类型拆借用副本传
        // 指针（UnpackCtorArg「借来不 release」口径，调用期内有效）；
        // 引用/Any/占位直通
        private static LLVMValueRef CoerceFatToParam(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat, MirType paramType)
        {
            if (session.IsInlineValueType(paramType, out _)
                || MirBuilder.IsScalarOrString(paramType) || TypeLayout.IsTypeId(paramType))
            {
                return BoxEmitter.UnpackCtorArg(session, builder, fat, paramType);
            }
            return fat;
        }

        // ===== 结果落槽 =====

        // 内建臂结果：目标内联标量槽直存；胖槽（占位/Any/界类型）装箱
        private static void StoreResultValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef value, MirType valueType, string target)
        {
            var slotType = slots[target].Local.Type;
            if (!session.IsInlineValueType(slotType, out _)
                && (TypeLayout.IsGenericPlaceholder(slotType) || slotType.IsAnyOrObject
                    || IsReferenceSlotType(session, slotType)))
            {
                builder.BuildStore(BoxFreshScalar(session, builder, value, valueType),
                    slots[target].Slot);
                return;
            }
            builder.BuildStore(value, slots[target].Slot);
        }

        // 非比较族候选结果（非 sret）：标量/String 视目标槽装箱或直存；
        // 胖引用直通（+1 随调用结果移交目标槽）
        private static void StoreProducedResult(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirType returnType, LLVMValueRef callResult, string target)
        {
            var slotType = slots[target].Local.Type;
            if (MirBuilder.IsScalarOrString(returnType))
            {
                StoreResultValue(session, builder, slots, callResult, returnType, target);
                return;
            }
            builder.BuildStore(callResult, slots[target].Slot);
        }

        private static bool IsReferenceSlotType(ModuleBuilder.Session session, MirType type) =>
            !session.IsInlineValueType(type, out _)
            && (TypeLayout.ClassifySlot(session.Layout, type) == ManagedSlotKind.FatReference);

        // 标量/String 新鲜值装箱（+1 归目标槽；String 走 tag1 堆块移交
        // 所有权——面返回值已带 +1，不再 acquire）
        private static LLVMValueRef BoxFreshScalar(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef value, MirType type)
        {
            var sheet = BoxEmitter.TypeSheetOf(session, type);
            var temp = LlvmEmitEnvironment.BuildEntryAlloca(builder,
                TypeLayout.MapType(session.Context, type), "gop.box.tmp");
            builder.BuildStore(value, temp);
            if (type.IsString)
            {
                var block = Malloc(session, builder, 16);
                session.EmitMemCopy(builder, block, temp, 16);
                return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagHeapValue,
                    builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "gop.box.pl"), "gop.box");
            }
            var bits = BoxEmitter.BitsFromSlot(session, builder, temp,
                BoxEmitter.ValueByteSize(session, type));
            return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline, bits,
                "gop.box");
        }

        // 值类型临时槽（sret 出参）→ 胖值移交：≤8B 内联位（无引用内容，
        // 引用必占 16B 胖槽）；>8B tag1 堆块（callee 产出的 +1 随块移交）
        private static LLVMValueRef BoxFreshValue(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef valueSlot, MirType type)
        {
            var plan = session.IsInlineValueType(type, out var found)
                ? found
                : throw new CompilerInternalException($"G4 装箱的非值类型: {type.Canonical}");
            var sheet = BoxEmitter.TypeSheetOf(session, type);
            if (plan.Size <= BoxEmitter.InlineLimit)
            {
                var bits = BoxEmitter.BitsFromSlot(session, builder, valueSlot, plan.Size);
                return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline, bits,
                    "gop.box");
            }
            var block = Malloc(session, builder, plan.Size);
            session.EmitMemCopy(builder, block, valueSlot, plan.Size);
            return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagHeapValue,
                builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "gop.box.pl"), "gop.box");
        }

        private static LLVMValueRef Malloc(ModuleBuilder.Session session, LLVMBuilderRef builder,
            int size)
        {
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.Malloc,
                PointerType(), new[] { LLVMTypeRef.Int32 });
            return builder.BuildCall2(fnType, fn,
                new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)size, false) },
                "gop.mem");
        }

        private static LLVMValueRef LoadFat(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirOperand operand)
        {
            if (operand is not MirLocalOperand)
            {
                throw new CompilerInternalException("G4 占位运算操作数必须是局部");
            }
            return session.LoadLocal(builder, slots, operand);
        }

        private static LLVMValueRef EmitTypeIs(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef typeId, LLVMValueRef payload,
            LLVMValueRef targetSheet)
        {
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.TypeIs,
                LLVMTypeRef.Int32,
                new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64, PointerType() });
            var raw = builder.BuildCall2(fnType, fn, new[]
            {
                typeId, payload,
                LLVMValueRef.CreateConstBitCast(targetSheet, PointerType()),
            }, "gop.is");
            return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, raw,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, true), "gop.isb");
        }

        // 普通形参的运行期可赋校验是否需要（VM OperatorParamsMatch 口径）：
        // 占位/.any 形参 VM 降级放行；具体类型才查 type_is
        private static bool NeedsRuntimeParamCheck(string paramTypeRef)
        {
            var normalized = MwTypeKey.Normalize(paramTypeRef);
            return !normalized.Contains(".generic<", System.StringComparison.Ordinal)
                && !MwTypeKey.IsAny(normalized);
        }

        // 落空：抛 core.NoSuchMethodException（typeName = 左操作数实际
        // 类型显示名，VM VmException「没有用户 operator …」的对应面；
        // EmitThrowNewException 终结当前块并沿异常边传播）
        private static void EmitOperatorMiss(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef leftTid, LLVMValueRef leftPayload,
            string operatorName,
            MirBlock? excTarget)
        {
            _ = operatorName;
            // null 胖值的 typeid 为 0，不能直接当 TypeSheet* 解引用。
            // 统一经 rigi_typeof 取实际 sheet，并把 NULL 映射到 .null sheet。
            var (typeOf, typeOfType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.TypeOf, PointerType(),
                new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 });
            var sheet = builder.BuildCall2(typeOfType, typeOf,
                new[] { leftTid, leftPayload },
                "gop.miss.sheet");
            var nullSheet = LLVMValueRef.CreateConstBitCast(
                session.TypeSheetFor(TypeLayout.NullSheetCanonical), PointerType());
            sheet = builder.BuildSelect(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sheet,
                    LLVMValueRef.CreateConstNull(PointerType()), "gop.miss.null"),
                nullSheet, sheet, "gop.miss.actual");
            var displayName = ExceptionEmitter.LoadTypeDisplayNameFromSheet(session, builder,
                sheet);
            ExceptionEmitter.EmitThrowNewException(session, builder,
                "core::NoSuchMethodException", "typeName", new[] { displayName }, excTarget);
        }

        private static bool TrySheetOf(ModuleBuilder.Session session, string canonical,
            out LLVMValueRef sheet)
        {
            var key = TypeLayout.BuiltinSheetCanonical(MirType.Of(canonical));
            if (session.TryGetTypeSheet(key, out sheet!)
                || session.TryGetTypeSheet(MwTypeKey.Normalize(canonical), out sheet!))
            {
                return true;
            }
            if (session.Symbols.FindTypeByRef(canonical) is { } type)
            {
                return session.TryGetTypeSheet(GenericAbi.PlanKey(type), out sheet!);
            }
            sheet = LLVMValueRef.CreateConstPointerNull(PointerType());
            return false;
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
