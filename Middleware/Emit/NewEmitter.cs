using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 静态 new 发射（与 DynamicNewEmitter 为邻）：class 走 alloc + 隐藏
    /// typeid + wrapper + init；struct/enum 走槽内清零 + wrapper + init。
    /// 动态 ctor thunk 复用 EmitAllocAndInit / EmitInitValueOnSlot。
    /// </summary>
    internal static class NewEmitter
    {
        internal sealed class Object : LlvmEmitVisitor<Object, MirNewObject>
        {
            protected override void VisitCore(MirNewObject inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                // L7：Init = null（全链无 init 声明的零参 new）——无实参
                // 可编组，构造 = alloc + 可选 ..init.wrapper
                var expected = inst.Init != null
                    ? CallEmitter.ExpectedCallParams(session.FunctionOf(inst.Init.Canonical).Mir)
                    : null;
                var temps = new List<ArcEmitter.RichTemp>();
                var boxed = new List<ArcEmitter.FatTemp>();
                var userArgs = new LLVMValueRef[inst.Args.Count];
                for (var i = 0; i < inst.Args.Count; i++)
                {
                    var expectType = expected != null && i + 1 < expected.Count
                        ? expected[i + 1].Type
                        : null;
                    userArgs[i] = CallEmitter.CoerceArg(session, builder, slots, inst.Args[i],
                        expectType, aliasThis: false, temps, boxed);
                }
                // new.wrapped（刀5）：wrapper 实参按 ..init.wrapper 形参
                // 表同口径加工（.this 居首，实参自第 2 位起）
                LLVMValueRef[]? wrapperArgs = null;
                if (inst.WrapperArgs.Count > 0)
                {
                    var wrapperExpected = inst.InitWrapper != null
                        && session.TryGetFunction(inst.InitWrapper.Canonical, out var wrapperFn)
                            ? CallEmitter.ExpectedCallParams(wrapperFn.Mir)
                            : null;
                    wrapperArgs = new LLVMValueRef[inst.WrapperArgs.Count];
                    for (var i = 0; i < inst.WrapperArgs.Count; i++)
                    {
                        var expectType = wrapperExpected != null && i + 1 < wrapperExpected.Count
                            ? wrapperExpected[i + 1].Type
                            : null;
                        wrapperArgs[i] = CallEmitter.CoerceArg(session, builder, slots,
                            inst.WrapperArgs[i], expectType, aliasThis: false, temps, boxed);
                    }
                }
                var fat = EmitAllocAndInit(session, builder, slots, inst.Type.Canonical,
                    inst.InitWrapper, inst.Init, userArgs, wrapperArgs);
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                // 构造异常边（刀5）：wrapper/init 内抛出（pending 非空）→
                // 释放未落槽的新建 +1 后沿边走；空 → 落槽续行
                if (inst.ExcTarget != null)
                {
                    EmitConstructPendingCheck(session, builder, fat, inst.ExcTarget);
                }
                builder.BuildStore(fat, slots[inst.Target].Slot);
            }
        }

        internal sealed class Value : LlvmEmitVisitor<Value, MirNewValue>
        {
            protected override void VisitCore(MirNewValue inst, ModuleBuilder.Session session)
            {
                var builder = session.Builder;
                var slots = session.Slots;
                var temps = new List<ArcEmitter.RichTemp>();
                var boxed = new List<ArcEmitter.FatTemp>();
                // G1：泛型值类型 init 形参可为占位（胖值槽）——实参加工与
                // class 路径同口径（ExpectedCallParams 已剔类级 typeid，
                // 下标 +1 跳过 .this）；L7：Init = null 时无实参可编组
                var initExpected = inst.Init != null
                    ? CallEmitter.ExpectedCallParams(
                        session.FunctionOf(inst.Init.Canonical).Mir)
                    : null;
                var userArgs = new LLVMValueRef[inst.Args.Count];
                for (var i = 0; i < inst.Args.Count; i++)
                {
                    var expectType = initExpected != null && i + 1 < initExpected.Count
                        ? initExpected[i + 1].Type
                        : null;
                    userArgs[i] = CallEmitter.CoerceArg(session, builder, slots, inst.Args[i],
                        expectType, aliasThis: false, temps, boxed);
                }
                // new.wrapped（刀5）：wrapper 实参同口径编组
                LLVMValueRef[]? wrapperArgs = null;
                if (inst.WrapperArgs.Count > 0)
                {
                    var wrapperExpected = inst.InitWrapper != null
                        && session.TryGetFunction(inst.InitWrapper.Canonical, out var wrapperFn)
                            ? CallEmitter.ExpectedCallParams(wrapperFn.Mir)
                            : null;
                    wrapperArgs = new LLVMValueRef[inst.WrapperArgs.Count];
                    for (var i = 0; i < inst.WrapperArgs.Count; i++)
                    {
                        var expectType = wrapperExpected != null && i + 1 < wrapperExpected.Count
                            ? wrapperExpected[i + 1].Type
                            : null;
                        wrapperArgs[i] = CallEmitter.CoerceArg(session, builder, slots,
                            inst.WrapperArgs[i], expectType, aliasThis: false, temps, boxed);
                    }
                }
                EmitInitValueOnSlot(session, builder, slots, slots[inst.Target].Slot,
                    inst.Type.Canonical, inst.InitWrapper, inst.Init, userArgs, wrapperArgs);
                ArcEmitter.DestroyRichTemps(session, builder, temps);
                ArcEmitter.DestroyFatTemps(session, builder, boxed);
                // 构造异常边（R3，同 MirNewObject 口径）：wrapper/init
                // 内抛出（pending 非空）→ 沿边走；值类型原地落槽，
                // 无新建堆对象需释放。无边（无 try 作用域）保持历史
                // 「pending 推迟到下一检查点」形态
                if (inst.ExcTarget != null)
                {
                    ExceptionEmitter.EmitPendingCheck(session, builder, inst.ExcTarget);
                }
            }
        }

        // MirNewObject.ExcTarget 的 pending 检查（ExceptionEmitter.
        // EmitPendingCheck 同形态，唯失败路径先释放新建实例——其 +1 从未
        // 落槽，不归 MIR 槽配平）；fail/cont 为内联合成块（不入 MIR 块
        // 映射，延续 guard 块先例）
        private static void EmitConstructPendingCheck(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef fat, MirBlock excTarget)
        {
            var blocks = session.CurrentBlocks
                ?? throw new CompilerInternalException("构造异常边缺少当前函数块映射");
            var (pendFn, pendType) = CallEmitter.DeclareHelperFace(session,
                Runtime.RuntimeFaces.ExcPending, PointerType(),
                System.Array.Empty<LLVMTypeRef>());
            var pending = builder.BuildCall2(pendType, pendFn,
                System.Array.Empty<LLVMValueRef>(), "new.pending");
            var hasPending = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                LLVMValueRef.CreateConstNull(PointerType()), "new.has");
            var fail = session.CurrentFunction.AppendBasicBlock("new.fail");
            var cont = session.CurrentFunction.AppendBasicBlock("new.cont");
            builder.BuildCondBr(hasPending, fail, cont);
            builder.PositionAtEnd(fail);
            ArcEmitter.EmitReleaseFatValue(session, builder, fat);
            builder.BuildBr(blocks[excTarget.Id]);
            builder.PositionAtEnd(cont);
        }

        // 静态 new 与动态 ctor thunk 共用：alloc → 隐藏 typeid → wrapper → init。
        // wrapperArgs（刀5 new.wrapped）：有参 ..init.wrapper 的实参
        //（null/空 = 零参形态，调用形态不变）
        internal static LLVMValueRef EmitAllocAndInit(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            string typeCanonical, MwMemberSymbol? initWrapper, MwMemberSymbol? init,
            LLVMValueRef[] userArgs, LLVMValueRef[]? wrapperArgs = null)
        {
            var sheet = session.TypeSheetFor(typeCanonical);
            var (allocFn, allocType) = CallEmitter.DeclareHelperFace(session, "rigi_alloc",
                PointerType(), new[] { PointerType() });
            var obj = builder.BuildCall2(allocType, allocFn, new[] { sheet }, "new.obj");
            var fat = CallEmitter.BuildFatReference(session, builder, sheet, obj);
            WriteHiddenTypeIds(session, builder, slots, obj, typeCanonical);
            if (initWrapper != null && session.TryGetFunction(initWrapper.Canonical, out var wrapper))
            {
                var wrapperCallArgs = AppendReceiver(fat, wrapperArgs);
                builder.BuildCall2(wrapper.Type, wrapper.Value, wrapperCallArgs, "");
            }
            // L7：无 init 声明 + 零实参（Init = null）——alloc + wrapper
            // 缝合即完成构造（VM TryFindInit 零实参空 init 同口径）
            if (init == null)
            {
                return fat;
            }
            var emitted = session.FunctionOf(init.Canonical);
            var initArgs = new LLVMValueRef[userArgs.Length + 1];
            initArgs[0] = fat;
            for (var i = 0; i < userArgs.Length; i++)
            {
                initArgs[i + 1] = userArgs[i];
            }
            if (emitted.Value.ParamsCount != (uint)initArgs.Length)
            {
                throw new CompilerInternalException(
                    $"ctor thunk 调 init 参数个数不符: {init.Canonical} " +
                    $"llvm={emitted.Value.ParamsCount} 传入={initArgs.Length} " +
                    $"sheet={typeCanonical}");
            }
            builder.BuildCall2(emitted.Type, emitted.Value, initArgs, "");
            return fat;
        }

        // 静态 new 与动态 struct ctor thunk 共用：零初始化 → wrapper →
        // init（.this = 槽地址原地生效）。泛型 struct 无对象头隐藏槽，
        // 类级 typeid 保留在 init/wrapper 的 LLVM 调用约定内，按
        // typeCanonical 的构造形态代入合成（闭合构造 = TypeSheet 常量；
        // 外层占位 = 当前 fn 的 .generic.* 局部，与 class「被调方自取」对偶）。
        internal static void EmitInitValueOnSlot(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef slot, string typeCanonical,
            MwMemberSymbol? initWrapper, MwMemberSymbol? init, LLVMValueRef[] userArgs,
            LLVMValueRef[]? wrapperArgs = null)
        {
            var plan = session.TryFindValuePlan(typeCanonical, out var found)
                ? found
                : throw new CompilerInternalException($"值类型无布局计划: {typeCanonical}");
            session.EmitMemSetZero(builder, slot, plan.Size);
            if (initWrapper != null && session.TryGetFunction(initWrapper.Canonical, out var wrapper))
            {
                var wrapperCallArgs = CallEmitter.MergeClassTypeIds(session, builder, slots,
                    wrapper.Mir, typeCanonical, AppendReceiver(slot, wrapperArgs));
                builder.BuildCall2(wrapper.Type, wrapper.Value, wrapperCallArgs, "");
            }
            if (init == null)
            {
                return;
            }
            var emitted = session.FunctionOf(init.Canonical);
            var initArgs = CallEmitter.MergeClassTypeIds(session, builder, slots,
                emitted.Mir, typeCanonical, AppendReceiver(slot, userArgs));
            if (emitted.Value.ParamsCount != (uint)initArgs.Length)
            {
                throw new CompilerInternalException(
                    $"struct ctor 调 init 参数个数不符: {init.Canonical} " +
                    $"llvm={emitted.Value.ParamsCount} 传入={initArgs.Length} " +
                    $"sheet={typeCanonical}");
            }
            builder.BuildCall2(emitted.Type, emitted.Value, initArgs, "");
        }

        // 类级 typeid 写入隐藏字段（闭合构造经 TypeSheetFor；外层泛型参数
        // 取当前 fn 的 .generic.* 局部——与 prologue 自取对偶）
        private static void WriteHiddenTypeIds(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef obj, string typeRef)
        {
            var plan = session.Layout?.Find(typeRef);
            if (plan == null && session.Symbols.FindTypeByRef(typeRef) is { } template)
            {
                plan = session.Layout?.Find(GenericAbi.PlanKey(template));
            }
            if (plan == null)
            {
                return;
            }
            WriteHiddenTypeIds(session, builder, slots, obj, plan, typeRef);
        }

        private static void WriteHiddenTypeIds(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            LLVMValueRef obj, TypeLayoutPlan plan, string typeRef, int depth = 0)
        {
            if (depth > 32)
            {
                throw new CompilerInternalException("WriteHiddenTypeIds 基类链过深: " + typeRef);
            }
            var template = session.Symbols.FindTypeByRef(typeRef);
            if (plan.BasePlan != null && template?.Declaration.ExtendsType is { } baseRef)
            {
                var substBase = ConstructedTypeCollector.BuildSubstitution(typeRef,
                    template.Declaration);
                var substituted = MwTypeKey.Normalize(
                    ConstructedTypeCollector.Substitute(baseRef, substBase));
                WriteHiddenTypeIds(session, builder, slots, obj, plan.BasePlan, substituted,
                    depth + 1);
            }
            var substitution = template != null
                ? ConstructedTypeCollector.BuildSubstitution(typeRef, template.Declaration)
                : null;
            foreach (var (paramName, offset) in plan.HiddenTypeIdSlots)
            {
                LLVMValueRef sheetPtr;
                if (substitution != null
                    && substitution.TryGetValue(paramName, out var arg)
                    && GenericAbi.TryPlaceholderName(arg, out var placeholder)
                    && slots.ContainsKey(".generic." + placeholder))
                {
                    sheetPtr = session.LoadLocal(builder, slots,
                        new MirLocalOperand(".generic." + placeholder));
                }
                else if (substitution != null && substitution.TryGetValue(paramName, out arg))
                {
                    var sheet = session.TypeSheetFor(arg);
                    sheetPtr = LLVMValueRef.CreateConstBitCast(sheet,
                        LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
                }
                else if (substitution == null
                    && slots.ContainsKey(".generic." + paramName))
                {
                    // MW11d-D：裸模板 new——泛型 fn 内 new Reader\<TMessage\>
                    // 的 MIR typeRef 退化为模板 canonical（subst=null），
                    // GP 实参不经 subst 携带；类级/方法级 typeid 取当前 fn
                    // 的 .generic.* 局部（实例方法的 prologue 自取局部与
                    // 泛型函数的隐藏 typeid 参数同形，与上方占位分支同源）
                    sheetPtr = session.LoadLocal(builder, slots,
                        new MirLocalOperand(".generic." + paramName));
                }
                else
                {
                    continue;
                }
                var bits = builder.BuildPtrToInt(sheetPtr, LLVMTypeRef.Int64, "tid.store");
                var gep = builder.BuildGEP2(LLVMTypeRef.Int8, obj,
                    new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)offset, false) },
                    "tid.wgep");
                builder.BuildStore(bits, gep);
            }
        }

        // ..init.wrapper 调用实参拼装：.this（class 胖引用 / 值类型槽
        // 地址）居首，wrapper 实参随后
        private static LLVMValueRef[] AppendReceiver(LLVMValueRef receiver,
            LLVMValueRef[]? wrapperArgs)
        {
            if (wrapperArgs == null || wrapperArgs.Length == 0)
            {
                return new[] { receiver };
            }
            var args = new LLVMValueRef[wrapperArgs.Length + 1];
            args[0] = receiver;
            for (var i = 0; i < wrapperArgs.Length; i++)
            {
                args[i + 1] = wrapperArgs[i];
            }
            return args;
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
