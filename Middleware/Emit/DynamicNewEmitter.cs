using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 动态构造 ABI（MW8b/MW8c）：调用点物化 argSheets / 胖槽实参、读
    /// TypeSheet vTable[0] 分发器、合成 ctor thunk（class 胖返回 / struct
    /// sret）。标量/String 零参走内建零值特判。TypeSheet 字段偏移与
    /// TypeSheetAbi.FieldVTable 互指。
    /// </summary>
    internal static class DynamicNewEmitter
    {
        internal static string DispatcherName(TypeLayoutPlan plan) =>
            DispatcherName(GenericAbi.PlanKey(plan.Symbol));

        internal static string DispatcherName(string planKey) =>
            GenericAbi.EscapeGlobalName("mw.init.dispatch.", planKey);

        internal static string ThunkName(string planKey, int index) =>
            GenericAbi.EscapeGlobalName("mw.init.ctor.", planKey) + "#" + index;

        // ===== 两阶段：先声明（vtable 槽 0 可填 ptr）再定义体 =====

        internal static void DeclareAll(ModuleBuilder.Session session)
        {
            if (session.Layout == null)
            {
                return;
            }
            var count = 0;
            foreach (var plan in session.Layout.Plans)
            {
                // class / struct 挂槽 0 分发器；enum 不声明（调用点 vTable
                // null 兜底 abort，对齐 VM 拒 enum）
                if (plan.Kind is not (TypeLayoutKind.Class or TypeLayoutKind.Struct))
                {
                    continue;
                }
                DeclareOne(session, plan);
                count++;
            }
            Logger.Verbose("Middleware", "动态 new 分发器已声明 " + count + " 个 class/struct");
        }

        internal static void EmitAll(ModuleBuilder.Session session, LLVMBuilderRef builder)
        {
            if (session.Layout == null)
            {
                return;
            }
            foreach (var plan in session.Layout.Plans)
            {
                if (plan.Kind is not (TypeLayoutKind.Class or TypeLayoutKind.Struct))
                {
                    continue;
                }
                EmitOne(session, builder, plan);
            }
        }

        internal sealed class Call : LlvmEmitVisitor<Call, MirNewIndirect>
        {
            protected override void VisitCore(MirNewIndirect inst, ModuleBuilder.Session session) =>
                EmitCall(session, session.Builder, session.Slots, inst);
        }

        internal static void EmitCall(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewIndirect inst)
        {
            var ptr = PointerType();
            var argc = inst.Args.Count;
            var sheet = session.LoadLocal(builder, slots, inst.TypeId);

            // a) 物化 argSheets：静态类型的 TypeSheet* / typeid 局部 / 驻留串
            LLVMValueRef argSheets;
            if (argc == 0)
            {
                argSheets = LLVMValueRef.CreateConstPointerNull(ptr);
            }
            else
            {
                var arrTy = LLVMTypeRef.CreateArray(ptr, (uint)argc);
                var arr = builder.BuildAlloca(arrTy, "dynnew.sheets");
                for (var i = 0; i < argc; i++)
                {
                    if (inst.Args[i] is not MirLocalOperand local)
                    {
                        throw new CompilerInternalException("new.indirect 实参非局部");
                    }
                    var token = MaterializeArgSheet(session, builder, slots,
                        slots[local.Name].Local.Type);
                    var gep = builder.BuildInBoundsGEP2(arrTy, arr, new[]
                    {
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false),
                        LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i, false),
                    }, "dynnew.sheet.gep");
                    builder.BuildStore(token, gep);
                }
                argSheets = builder.BuildInBoundsGEP2(arrTy, arr, new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false),
                }, "dynnew.sheets.ptr");
            }

            // b) 打包实参胖槽
            var packed = new LLVMValueRef[argc];
            for (var i = 0; i < argc; i++)
            {
                if (inst.Args[i] is not MirLocalOperand local)
                {
                    throw new CompilerInternalException("new.indirect 实参非局部");
                }
                packed[i] = PackArg(session, builder, slots, local.Name);
            }

            var fn = session.CurrentFunction;
            var abortBlock = fn.AppendBasicBlock("dynnew.abort");
            var loadVt = fn.AppendBasicBlock("dynnew.loadvt");
            var callDisp = fn.AppendBasicBlock("dynnew.dispatch");
            var callThunk = fn.AppendBasicBlock("dynnew.thunk");
            var done = fn.AppendBasicBlock("dynnew.done");

            // 标量/String 零参 T()：vTable 查找前比对内建零值可构造 sheet
            //（发射期已知常量集）。argc>0 标量无 init → 落既有 abort。
            if (argc == 0)
            {
                var vtCheck = fn.AppendBasicBlock("dynnew.vtcheck");
                EmitZeroConstruct(session, builder, slots, inst, sheet, vtCheck, done);
                builder.PositionAtEnd(vtCheck);
            }

            // c) 内联 GEP 读 sheet→vTable[0]（FieldVTable 与 TypeSheetEmitter 互指）
            var sheetTy = TypeSheetEmitter.SheetStructType(session.Context);
            var vtField = builder.BuildStructGEP2(sheetTy, sheet,
                (uint)TypeSheetAbi.FieldVTable, "dynnew.vt.field");
            var vTable = builder.BuildLoad2(ptr, vtField, "dynnew.vt");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, vTable,
                    LLVMValueRef.CreateConstPointerNull(ptr), "dynnew.vt.null"),
                abortBlock, loadVt);

            builder.PositionAtEnd(loadVt);
            var dispatch = builder.BuildLoad2(ptr, vTable, "dynnew.disp");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, dispatch,
                    LLVMValueRef.CreateConstPointerNull(ptr), "dynnew.disp.null"),
                abortBlock, callDisp);

            builder.PositionAtEnd(callDisp);
            var dispType = DispatcherType(session);
            var thunkPtr = builder.BuildCall2(dispType, dispatch, new[]
            {
                argSheets,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)argc, false),
            }, "dynnew.thunk.ptr");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, thunkPtr,
                    LLVMValueRef.CreateConstPointerNull(ptr), "dynnew.miss"),
                abortBlock, callThunk);

            builder.PositionAtEnd(abortBlock);
            // MW9b-G：vTable/分发器/匹配三连落空 → 抛可捕获
            // core.NoSuchMethodException（typeName = 目标 sheet 运行期
            // 显示名，对齐 VM 拼写），沿本指令异常边传播
            var typeName = ExceptionEmitter.LoadTypeDisplayNameFromSheet(session, builder, sheet);
            ExceptionEmitter.EmitThrowNewException(session, builder,
                "core::NoSuchMethodException", "typeName", new[] { typeName },
                inst.ExcTarget);

            builder.PositionAtEnd(callThunk);
            EmitThunkInvoke(session, builder, slots, inst, sheet, thunkPtr, packed, done);
            builder.PositionAtEnd(done);
        }

        // 内建零值可构造 sheet（与 VM IsPrimitiveZeroConstructible 同集）。
        // .typeid<X> 族 VM 不零值可构造，不纳入。
        private static readonly string[] ZeroConstructibleCanonicals =
        {
            "core::bool", "core::char",
            "core::i8", "core::u8", "core::i16", "core::u16",
            "core::i32", "core::u32", "core::i64", "core::u64",
            "core::float", "core::double", "core::String",
        };

        // argc==0：sheet 命中内建零值集则直产零值，否则落到 miss（vTable 路径）
        private static void EmitZeroConstruct(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirNewIndirect inst, LLVMValueRef sheet,
            LLVMBasicBlockRef miss, LLVMBasicBlockRef done)
        {
            var fn = session.CurrentFunction;
            var ptr = PointerType();
            var sheetPtr = builder.BuildBitCast(sheet, ptr, "znew.sheet");
            var checks = new LLVMBasicBlockRef[ZeroConstructibleCanonicals.Length];
            var hits = new LLVMBasicBlockRef[ZeroConstructibleCanonicals.Length];
            for (var i = 0; i < checks.Length; i++)
            {
                checks[i] = fn.AppendBasicBlock("znew.chk." + i);
                hits[i] = fn.AppendBasicBlock("znew.hit." + i);
            }
            builder.BuildBr(checks[0]);
            for (var i = 0; i < checks.Length; i++)
            {
                builder.PositionAtEnd(checks[i]);
                var fail = i + 1 < checks.Length ? checks[i + 1] : miss;
                if (!session.TryGetTypeSheet(ZeroConstructibleCanonicals[i], out var want)
                    && !session.TryGetTypeSheet(
                        TypeLayout.BuiltinSheetCanonical(
                            MirType.Of(ZeroConstructibleCanonicals[i])), out want))
                {
                    builder.BuildBr(fail);
                    builder.PositionAtEnd(hits[i]);
                    builder.BuildBr(fail);
                    continue;
                }
                var wantPtr = LLVMValueRef.CreateConstBitCast(want, ptr);
                var eq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sheetPtr, wantPtr,
                    "znew.eq");
                builder.BuildCondBr(eq, hits[i], fail);

                builder.PositionAtEnd(hits[i]);
                StoreZeroValue(session, builder, slots, inst.Target,
                    ZeroConstructibleCanonicals[i], sheetPtr);
                builder.BuildBr(done);
            }
        }

        private static void StoreZeroValue(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string target,
            string canonical, LLVMValueRef sheet)
        {
            var resultType = slots[target].Local.Type;
            var slot = slots[target].Slot;
            // 内联标量/String 槽直存零；胖槽（泛型界/.any）包 tag0/tag1
            if (MirBuilder.IsScalarOrString(resultType))
            {
                builder.BuildStore(NativeZeroOf(session, resultType), slot);
                return;
            }
            if (canonical == "core::String")
            {
                var block = Malloc(session, builder,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 16, false));
                var tmp = builder.BuildAlloca(StringAbi.ValueType(session.Context), "znew.str");
                builder.BuildStore(NativeZeroOf(session, MirType.Of("core::String")), tmp);
                session.EmitMemCopy(builder, block, tmp, 16);
                var payload = builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "znew.s.pl");
                var fat = BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagHeapValue,
                    payload, "znew");
                builder.BuildStore(fat, slot);
                return;
            }
            var zeroBits = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0, false);
            var packed = BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline,
                zeroBits, "znew");
            builder.BuildStore(packed, slot);
        }

        private static LLVMValueRef NativeZeroOf(ModuleBuilder.Session session, MirType type)
        {
            if (type.IsString || type.Key == "String")
            {
                return StringAbi.BuildConstant(session.Module, "", "znew.empty");
            }
            var llvm = TypeLayout.MapType(session.Context, type);
            return type.Key is "float" or "double"
                ? LLVMValueRef.CreateConstReal(llvm, 0.0)
                : LLVMValueRef.CreateConstInt(llvm, 0, false);
        }

        // 调用点按 result 静态类型二选一：内联 struct 槽 → sret 直写；
        // 胖槽 → 运行期读 FlagInlineValue 区分 struct（malloc/tag0 sret）
        // 与 class（胖返回）。泛型界静态类型不足以区分 class/struct。
        private static void EmitThunkInvoke(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewIndirect inst,
            LLVMValueRef sheet, LLVMValueRef thunkPtr, LLVMValueRef[] packed,
            LLVMBasicBlockRef done)
        {
            var resultType = slots[inst.Target].Local.Type;
            if (session.IsInlineValueType(resultType, out _))
            {
                var args = new LLVMValueRef[packed.Length + 1];
                args[0] = slots[inst.Target].Slot;
                for (var i = 0; i < packed.Length; i++)
                {
                    args[i + 1] = packed[i];
                }
                builder.BuildCall2(StructThunkType(session, packed.Length), thunkPtr, args, "");
                // MW9b-G：thunk 内部 miss 经 pending 接力（sret 落槽在
                // 调用内发生，检查结果前不触碰）
                ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
                builder.BuildBr(done);
                return;
            }

            var fn = session.CurrentFunction;
            var classPath = fn.AppendBasicBlock("dynnew.class");
            var structPath = fn.AppendBasicBlock("dynnew.struct");
            var sheetTy = TypeSheetEmitter.SheetStructType(session.Context);
            var flagsField = builder.BuildStructGEP2(sheetTy, sheet,
                (uint)TypeSheetAbi.FieldTypeFlags, "dynnew.flags.f");
            var flags = builder.BuildLoad2(LLVMTypeRef.Int32, flagsField, "dynnew.flags");
            var isInline = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                builder.BuildAnd(flags,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32,
                        TypeLayoutPlan.FlagInlineValue, false), "dynnew.inline.bits"),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0, false), "dynnew.isinline");
            builder.BuildCondBr(isInline, structPath, classPath);

            builder.PositionAtEnd(classPath);
            var fat = builder.BuildCall2(ThunkType(session, packed.Length), thunkPtr, packed,
                "dynnew.obj");
            // MW9b-G：thunk 内部 miss 经 pending 接力；须在结果落槽前检查
            ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
            builder.BuildStore(fat, slots[inst.Target].Slot);
            builder.BuildBr(done);

            builder.PositionAtEnd(structPath);
            EmitFatStructConstruct(session, builder, slots, inst, sheet, thunkPtr, packed);
            builder.BuildBr(done);
        }

        // 胖槽 struct：typeSize≤8 tag0 栈槽 sret；>8 malloc tag1 块 sret
        private static void EmitFatStructConstruct(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirNewIndirect inst,
            LLVMValueRef sheet, LLVMValueRef thunkPtr, LLVMValueRef[] packed)
        {
            var fn = session.CurrentFunction;
            var small = fn.AppendBasicBlock("dynnew.sret.small");
            var large = fn.AppendBasicBlock("dynnew.sret.large");
            var join = fn.AppendBasicBlock("dynnew.sret.join");
            var sheetTy = TypeSheetEmitter.SheetStructType(session.Context);
            var sizeField = builder.BuildStructGEP2(sheetTy, sheet,
                (uint)TypeSheetAbi.FieldTypeSize, "dynnew.size.f");
            var typeSize = builder.BuildLoad2(LLVMTypeRef.Int32, sizeField, "dynnew.size");
            builder.BuildCondBr(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, typeSize,
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, BoxEmitter.InlineLimit, false),
                    "dynnew.small"),
                small, large);

            builder.PositionAtEnd(small);
            var tmp = builder.BuildAlloca(
                LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, BoxEmitter.InlineLimit), "dynnew.sret");
            tmp.Alignment = BoxEmitter.InlineLimit;
            session.EmitMemSetZero(builder, tmp, BoxEmitter.InlineLimit);
            var smallArgs = PrefixOut(tmp, packed);
            builder.BuildCall2(StructThunkType(session, packed.Length), thunkPtr, smallArgs, "");
            // MW9b-G：thunk 内部 miss 经 pending 接力
            ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
            var bits = BoxEmitter.BitsFromSlot(session, builder, tmp, BoxEmitter.InlineLimit);
            builder.BuildStore(
                BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline, bits, "dynnew"),
                slots[inst.Target].Slot);
            builder.BuildBr(join);

            builder.PositionAtEnd(large);
            var block = Malloc(session, builder, typeSize);
            var largeArgs = PrefixOut(block, packed);
            builder.BuildCall2(StructThunkType(session, packed.Length), thunkPtr, largeArgs, "");
            // MW9b-G：thunk 内部 miss 经 pending 接力
            ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
            var payload = builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "dynnew.tag1.pl");
            builder.BuildStore(
                BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagHeapValue, payload,
                    "dynnew"),
                slots[inst.Target].Slot);
            builder.BuildBr(join);

            builder.PositionAtEnd(join);
        }

        private static LLVMValueRef[] PrefixOut(LLVMValueRef slot, LLVMValueRef[] packed)
        {
            var args = new LLVMValueRef[packed.Length + 1];
            args[0] = slot;
            for (var i = 0; i < packed.Length; i++)
            {
                args[i + 1] = packed[i];
            }
            return args;
        }

        private static LLVMValueRef Malloc(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef size)
        {
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session, RuntimeFaces.Malloc,
                PointerType(), new[] { LLVMTypeRef.Int32 });
            return builder.BuildCall2(fnType, fn, new[] { size }, "dynnew.mem");
        }

        // ===== 声明 =====

        private static void DeclareOne(ModuleBuilder.Session session, TypeLayoutPlan plan)
        {
            var key = GenericAbi.PlanKey(plan.Symbol);
            var dispName = DispatcherName(key);
            if (session.TryGetSynthetic(dispName, out _))
            {
                return;
            }
            var dispType = DispatcherType(session);
            var disp = session.Module.AddFunction(dispName, dispType);
            disp.Linkage = LLVMLinkage.LLVMInternalLinkage;
            session.RegisterSynthetic(dispName, disp, dispType);

            if (IsAbstract(plan.Symbol) || IsOpenGenericTemplate(plan))
            {
                return;
            }
            var inits = CollectInits(session, plan);
            for (var i = 0; i < inits.Count; i++)
            {
                var thunkName = ThunkName(key, i);
                var thunkType = plan.Kind == TypeLayoutKind.Struct
                    ? StructThunkType(session, inits[i].Argc)
                    : ThunkType(session, inits[i].Argc);
                var thunk = session.Module.AddFunction(thunkName, thunkType);
                thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
                session.RegisterSynthetic(thunkName, thunk, thunkType);
            }
        }

        // ===== 体 =====

        private static void EmitOne(ModuleBuilder.Session session, LLVMBuilderRef builder,
            TypeLayoutPlan plan)
        {
            var key = GenericAbi.PlanKey(plan.Symbol);
            if (!session.TryGetSynthetic(DispatcherName(key), out var disp))
            {
                return;
            }
            if (disp.Fn.BasicBlocksCount > 0)
            {
                return;
            }
            session.SetCurrentFunction(disp.Fn);
            var entry = disp.Fn.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            if (IsAbstract(plan.Symbol) || IsOpenGenericTemplate(plan))
            {
                builder.BuildRet(LLVMValueRef.CreateConstPointerNull(PointerType()));
                return;
            }
            var inits = CollectInits(session, plan);
            if (inits.Count == 0)
            {
                builder.BuildRet(LLVMValueRef.CreateConstPointerNull(PointerType()));
                return;
            }
            var argSheets = disp.Fn.GetParam(0);
            var argc = disp.Fn.GetParam(1);
            argSheets.Name = "argSheets";
            argc.Name = "argc";
            var miss = disp.Fn.AppendBasicBlock("miss");
            var checks = new LLVMBasicBlockRef[inits.Count];
            for (var i = 0; i < inits.Count; i++)
            {
                checks[i] = disp.Fn.AppendBasicBlock("chk." + i);
            }
            builder.BuildBr(checks[0]);
            for (var i = 0; i < inits.Count; i++)
            {
                var fail = i + 1 < inits.Count ? checks[i + 1] : miss;
                EmitDispatchArm(session, builder, plan, inits[i], i, argSheets, argc,
                    checks[i], fail);
            }
            builder.PositionAtEnd(miss);
            builder.BuildRet(LLVMValueRef.CreateConstPointerNull(PointerType()));

            for (var i = 0; i < inits.Count; i++)
            {
                EmitThunkBody(session, builder, plan, inits[i], i);
            }
        }

        private static void EmitDispatchArm(ModuleBuilder.Session session, LLVMBuilderRef builder,
            TypeLayoutPlan plan, InitOverload init, int index,
            LLVMValueRef argSheets, LLVMValueRef argc,
            LLVMBasicBlockRef check, LLVMBasicBlockRef fail)
        {
            var fn = session.CurrentFunction;
            var chkArgs = fn.AppendBasicBlock("chk.args." + index);
            var hit = fn.AppendBasicBlock("hit." + index);
            builder.PositionAtEnd(check);
            var argcOk = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, argc,
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)init.Argc, false),
                "argc.eq");
            builder.BuildCondBr(argcOk, chkArgs, fail);

            builder.PositionAtEnd(chkArgs);
            LLVMValueRef all = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1, false);
            var ptr = PointerType();
            for (var i = 0; i < init.Argc; i++)
            {
                var slot = builder.BuildInBoundsGEP2(ptr, argSheets, new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i, false),
                }, "arg.slot");
                var loaded = builder.BuildLoad2(ptr, slot, "arg.sheet");
                var want = ArgToken(session, init.ParamTypes[i]);
                var eq = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, loaded, want, "arg.eq");
                all = builder.BuildAnd(all, eq, "args.ok");
            }
            builder.BuildCondBr(all, hit, fail);
            builder.PositionAtEnd(hit);
            if (!session.TryGetSynthetic(ThunkName(GenericAbi.PlanKey(plan.Symbol), index),
                    out var thunk))
            {
                builder.BuildRet(LLVMValueRef.CreateConstPointerNull(ptr));
                return;
            }
            builder.BuildRet(thunk.Fn);
        }

        private static void EmitThunkBody(ModuleBuilder.Session session, LLVMBuilderRef builder,
            TypeLayoutPlan plan, InitOverload init, int index)
        {
            var name = ThunkName(GenericAbi.PlanKey(plan.Symbol), index);
            if (!session.TryGetSynthetic(name, out var thunk))
            {
                return;
            }
            if (thunk.Fn.BasicBlocksCount > 0)
            {
                return;
            }
            session.SetCurrentFunction(thunk.Fn);
            var entry = thunk.Fn.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            if (!session.TryGetFunction(init.Member.Canonical, out _))
            {
                // MW9b-G：thunk 内部 miss（init 族 MIR 试探性跳过产物）→
                // 构造 NoSuchMethodException + ExcRaise + 按 thunk 返回
                // 形态 ret undef/void；pending 由调用点 pending 检查接力。
                // typeName = 计划静态名常量（thunk 按类型合成，名称发射
                // 期已知；展示拼写对齐 VM 口径）
                var nameConst = session.InternStringConstant(
                    BilVerificationContext.DenormalizeTypeRef(plan.Symbol.Canonical));
                ExceptionEmitter.EmitThrowNewException(session, builder,
                    "core::NoSuchMethodException", "typeName", new[] { nameConst }, null);
                return;
            }
            var emptySlots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>(
                System.StringComparer.Ordinal);
            var userArgs = new LLVMValueRef[init.Argc];
            var emitted = session.FunctionOf(init.Member.Canonical);
            var expected = CallEmitter.ExpectedCallParams(emitted.Mir);
            var isStruct = plan.Kind == TypeLayoutKind.Struct;
            for (var i = 0; i < init.Argc; i++)
            {
                var fat = thunk.Fn.GetParam(isStruct ? (uint)(i + 1) : (uint)i);
                fat.Name = "arg" + i;
                var declared = i + 1 < expected.Count
                    ? expected[i + 1].Type
                    : MirType.Of(init.ParamTypes[i]);
                userArgs[i] = BoxEmitter.UnpackCtorArg(session, builder, fat, declared);
            }
            if (isStruct)
            {
                // sret：在 out 指针上完成零初始化 → wrapper → init
                var slot = thunk.Fn.GetParam(0);
                slot.Name = "out";
                NewEmitter.EmitInitValueOnSlot(session, builder, slot,
                    plan.Symbol.Canonical, init.Wrapper, init.Member, userArgs);
                builder.BuildRetVoid();
                return;
            }
            var fatResult = NewEmitter.EmitAllocAndInit(session, builder, emptySlots,
                plan.Symbol.Canonical, init.Wrapper, init.Member, userArgs);
            builder.BuildRet(fatResult);
        }

        // ===== 调用点辅助 =====

        private static LLVMValueRef MaterializeArgSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, MirType staticType)
        {
            if (GenericAbi.TryPlaceholderName(staticType.Canonical, out var name)
                && slots.ContainsKey(".generic." + name))
            {
                return session.LoadLocal(builder, slots,
                    new MirLocalOperand(".generic." + name));
            }
            return ArgToken(session, staticType.Canonical);
        }

        // 栈借用打包：胖值不逃逸出本次调用（不分发器检视、不存槽）。
        // tag0 内联；tag1 payload = 本帧 alloca 副本，不 Malloc、不
        // acquire/release；thunk 读后即弃，MW9 unwind 无清理义务。
        private static LLVMValueRef PackArg(ModuleBuilder.Session session, LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots, string localName)
        {
            var type = slots[localName].Local.Type;
            if (BoxEmitter.IsFatPassthrough(session, type))
            {
                return session.LoadLocal(builder, slots, new MirLocalOperand(localName));
            }
            var size = BoxEmitter.ValueByteSize(session, type);
            var sheet = BoxEmitter.TypeSheetOf(session, type);
            if (size <= BoxEmitter.InlineLimit)
            {
                var inline = BoxEmitter.PackInlinePayload(session, builder, slots, localName,
                    type, size);
                return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagInline,
                    inline, "dynnew");
            }
            var block = builder.BuildAlloca(
                LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)size), "dynnew.stack");
            block.Alignment = StackBorrowAlign(session, type);
            session.EmitMemCopy(builder, block, slots[localName].Slot, size);
            var payload = builder.BuildPtrToInt(block, LLVMTypeRef.Int64, "dynnew.stack.pl");
            return BoxEmitter.PackFat(session, builder, sheet, BoxEmitter.TagHeapValue,
                payload, "dynnew");
        }

        private static uint StackBorrowAlign(ModuleBuilder.Session session, MirType type)
        {
            if (session.IsInlineValueType(type, out var plan))
            {
                return (uint)plan.Alignment;
            }
            return type.IsString
                ? (uint)TypeLayout.ReferenceSlotAlignment
                : (uint)BoxEmitter.InlineLimit;
        }

        private static LLVMValueRef ArgToken(ModuleBuilder.Session session, string typeRef)
        {
            var type = MirType.Of(typeRef);
            var builtin = TypeLayout.BuiltinSheetCanonical(type);
            if (session.TryGetTypeSheet(typeRef, out var sheet)
                || session.TryGetTypeSheet(builtin, out sheet)
                || session.TryGetTypeSheet(type.Canonical, out sheet))
            {
                return builderBitCastSheet(session, sheet);
            }
            return session.InternCanonicalToken(type.Canonical);
        }

        private static LLVMValueRef builderBitCastSheet(ModuleBuilder.Session session,
            LLVMValueRef sheet)
        {
            return LLVMValueRef.CreateConstBitCast(sheet, PointerType());
        }

        // ===== 收集 =====

        private readonly struct InitOverload
        {
            public MwMemberSymbol Member { get; }
            public MwMemberSymbol? Wrapper { get; }
            public IReadOnlyList<string> ParamTypes { get; }
            public int Argc => ParamTypes.Count;

            public InitOverload(MwMemberSymbol member, MwMemberSymbol? wrapper,
                IReadOnlyList<string> paramTypes)
            {
                Member = member;
                Wrapper = wrapper;
                ParamTypes = paramTypes;
            }
        }

        private static List<InitOverload> CollectInits(ModuleBuilder.Session session,
            TypeLayoutPlan plan)
        {
            var list = new List<InitOverload>();
            var subst = ConstructedTypeCollector.BuildSubstitution(
                plan.Symbol.Canonical, plan.Symbol.Declaration);
            MwMemberSymbol? wrapper = session.Symbols.FindMember(
                plan.Symbol.Declaration.Symbol + "$..init.wrapper()@.void");
            foreach (var member in plan.Symbol.Members)
            {
                if (!member.HasKeyword(BilKeyword.Init))
                {
                    continue;
                }
                if (IsMethodLevelGeneric(member.Canonical))
                {
                    throw new MwNotSupportedException(
                        "new.indirect 不支持方法级泛型 init: " + member.Canonical);
                }
                if (!session.TryGetFunction(member.Canonical, out _))
                {
                    continue;
                }
                var signature = CanonicalSignature.Parse(member.Canonical);
                var paramTypes = new string[signature.Parameters.Count];
                for (var i = 0; i < signature.Parameters.Count; i++)
                {
                    paramTypes[i] = MwTypeKey.Normalize(
                        ConstructedTypeCollector.Substitute(signature.Parameters[i].TypeRef, subst));
                }
                list.Add(new InitOverload(member, wrapper, paramTypes));
            }
            return list;
        }

        // 语言无 init<T>；名段含 < 即为方法级泛型，受控拒绝
        private static bool IsMethodLevelGeneric(string canonical)
        {
            var dollar = canonical.IndexOf('$');
            var open = canonical.IndexOf('(');
            if (dollar < 0 || open <= dollar)
            {
                return false;
            }
            return canonical.IndexOf('<', dollar, open - dollar) >= 0;
        }

        // 开放泛型模板（PlanKey 为 Type<arity>）运行期不会作为 typeid
        // 出现；分发器 ret null，不合成 thunk
        private static bool IsOpenGenericTemplate(TypeLayoutPlan plan) =>
            plan.Symbol.Declaration.GenericParameters.Count > 0
            && !GenericAbi.IsClosedConstructed(plan.Symbol.Canonical);

        private static bool IsAbstract(MwTypeSymbol type)
        {
            foreach (var modifier in type.Declaration.Modifiers)
            {
                if (modifier is BilKeywordModifier keyword
                    && keyword.Keyword == BilKeyword.Abstract)
                {
                    return true;
                }
            }
            return false;
        }

        private static LLVMTypeRef DispatcherType(ModuleBuilder.Session session)
        {
            var ptr = PointerType();
            return LLVMTypeRef.CreateFunction(ptr, new[] { ptr, LLVMTypeRef.Int32 }, false);
        }

        private static LLVMTypeRef ThunkType(ModuleBuilder.Session session, int argc)
        {
            var fat = TypeLayout.FatReferenceType(session.Context);
            var ps = new LLVMTypeRef[argc];
            for (var i = 0; i < argc; i++)
            {
                ps[i] = fat;
            }
            return LLVMTypeRef.CreateFunction(fat, ps, false);
        }

        // struct ctor thunk：void(ptr %out, fat...)
        private static LLVMTypeRef StructThunkType(ModuleBuilder.Session session, int argc)
        {
            var fat = TypeLayout.FatReferenceType(session.Context);
            var ps = new LLVMTypeRef[argc + 1];
            ps[0] = PointerType();
            for (var i = 0; i < argc; i++)
            {
                ps[i + 1] = fat;
            }
            return LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, ps, false);
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
