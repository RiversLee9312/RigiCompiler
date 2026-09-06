using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 异常指令发射（Emit 分面，MW9a 第 C 棒）：checked-flag 便携模型的
    /// 三新指令。MirTakePending = rigi_exc_take 移动取回 TLS pending 并
    /// 按目标槽静态类型装箱胖引用落槽；MirThrow = 胖引用 payload 经
    /// rigi_exc_raise 置 TLS pending（内部 acquire）。控制边不由本类
    /// 发射：MirThrow 所在块的终结符（MirBranch(excTarget)/MirRetThrow）
    /// 镜像异常边（B 棒 MIR 形状契约），由 TerminatorEmitter 统一承载，
    /// 避免同块双终结符。
    /// </summary>
    internal static class ExceptionEmitter
    {
        internal sealed class TakePending : LlvmEmitVisitor<TakePending, MirTakePending>
        {
            protected override void VisitCore(MirTakePending inst, ModuleBuilder.Session session) =>
                EmitTakePending(session, session.Builder, session.Slots, inst);
        }

        internal sealed class Throw : LlvmEmitVisitor<Throw, MirThrow>
        {
            protected override void VisitCore(MirThrow inst, ModuleBuilder.Session session) =>
                EmitThrow(session, session.Builder, session.Slots, inst);
        }

        // MirTakePending：take 得 void* → 构造胖引用写目标 alloca
        //（typeid 半 = 目标槽静态类型的 TypeSheet 全局，tag2 对象形态；
        // +1 所有权随 take 移动给目标槽，不另 acquire）
        internal static void EmitTakePending(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirTakePending inst)
        {
            var (takeFn, takeType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.ExcTake, PointerType(), System.Array.Empty<LLVMTypeRef>());
            var obj = builder.BuildCall2(takeType, takeFn,
                System.Array.Empty<LLVMValueRef>(), "exc.take");
            var sheet = session.TypeSheetFor(slots[inst.TargetLocal].Local.Type.Canonical);
            var fat = BoxEmitter.PackObject(session, builder, sheet, obj);
            builder.BuildStore(fat, slots[inst.TargetLocal].Slot);
        }

        // MirThrow：操作数槽载胖引用 payload（恒 tag2 对象指针）→
        // rigi_exc_raise(ptr)（内部 acquire，TLS pending 持 +1；被抛槽位
        // 自身 +1 不动，照常配平）。传播边由块终结符承载
        internal static void EmitThrow(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirThrow inst)
        {
            var fat = session.LoadLocal(builder, slots, inst.Exception);
            var payload = builder.BuildExtractValue(fat, 1, "throw.payload");
            var obj = builder.BuildIntToPtr(payload, PointerType(), "throw.obj");
            var (raiseFn, raiseType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.ExcRaise, LLVMTypeRef.Void, new[] { PointerType() });
            builder.BuildCall2(raiseType, raiseFn, new[] { obj }, "");
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

        // ===== MW9b-G：守卫点抛真异常（与 VM 同型同消息，可被 try/catch）=====

        // 共享抛出辅助：rigi_alloc 分配 + 调对应 init（普通 Rigi 调用，走
        // NewEmitter.EmitAllocAndInit 设施）+ rigi_exc_raise + 归还构造侧
        // 临时 +1（raise 已 acquire 进 TLS）。excTarget 非空（函数体内守卫
        // 点，RcInjection 已解析）：br 到异常边目标块（入口 MirTakePending
        // 取回 pending）；excTarget == null（ctor thunk 内部，无 MIR 块
        // 映射）：按 thunk 返回形态 ret undef/void，pending 由调用点的
        // pending 检查接力。调用后本块已终结，调用方自行定位续行块
        internal static void EmitThrowNewException(ModuleBuilder.Session session,
            LLVMBuilderRef builder, string typeCanonical, string? initFirstParamName,
            LLVMValueRef[] args, MirBlock? excTarget)
        {
            var type = session.Symbols.FindTypeByRef(typeCanonical);
            var init = type != null ? ResolveInit(type, args.Length, initFirstParamName) : null;
            if (session.Layout == null || init == null
                || !session.TryGetFunction(init!.Canonical, out _)
                || !session.TryGetTypeSheet(type!.Canonical, out _))
            {
                // 防御回退：无 stdlib 的手写 BIL（异常类型/init MIR 均缺）
                // 无法构造真异常——退到未捕获出口（exit 1，无消息；真实
                // 前端产物恒带 stdlib，不走此路）
                var (haltFn, haltType) = CallEmitter.DeclareVoidFace(session,
                    RuntimeFaces.ExcHalt);
                builder.BuildCall2(haltType, haltFn, System.Array.Empty<LLVMValueRef>(), "");
                builder.BuildUnreachable();
                return;
            }
            var emptySlots = new Dictionary<string, (LLVMValueRef Slot, MirLocal Local)>(
                System.StringComparer.Ordinal);
            var wrapper = session.Symbols.FindMember(
                type!.Canonical + "$..init.wrapper()@.void");
            var fat = NewEmitter.EmitAllocAndInit(session, builder, emptySlots,
                type.Canonical, wrapper, init!, args);
            var payload = builder.BuildExtractValue(fat, 1, "guard.payload");
            var obj = builder.BuildIntToPtr(payload, PointerType(), "guard.obj");
            var (raiseFn, raiseType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.ExcRaise, LLVMTypeRef.Void, new[] { PointerType() });
            builder.BuildCall2(raiseType, raiseFn, new[] { obj }, "");
            ArcEmitter.EmitReleaseFatValue(session, builder, fat);
            if (excTarget != null)
            {
                var blocks = session.CurrentBlocks
                    ?? throw new CompilerInternalException("守卫抛出缺少当前函数块映射");
                builder.BuildBr(blocks[excTarget.Id]);
                return;
            }
            // opaque pointer 的 TypeOf 不是函数类型，须读取函数全局值类型。
            var thunkReturn = LlvmBitcode.FunctionTypeOf(session.CurrentFunction).ReturnType;
            if (thunkReturn.Kind == LLVMTypeKind.LLVMVoidTypeKind)
            {
                builder.BuildRetVoid();
                return;
            }
            builder.BuildRet(LlvmBitcode.UndefOf(thunkReturn));
        }

        // init 重载解析：按 Init 关键字 + 参数个数 + 可选首形参名
        //（NoSuchMethodException 的 init(typeName) 与 init(text) 同签名，
        // 以首形参名区分，VM NoSuchMethodForType 同口径）
        private static Symbols.MwMemberSymbol? ResolveInit(Symbols.MwTypeSymbol type,
            int argc, string? firstParamName)
        {
            foreach (var member in type.Members)
            {
                if (!member.HasKeyword(Bil.BilKeyword.Init))
                {
                    continue;
                }
                var signature = Symbols.CanonicalSignature.Parse(member.Canonical);
                if (signature.Parameters.Count != argc)
                {
                    continue;
                }
                if (firstParamName != null
                    && (argc == 0 || signature.Parameters[0].Name != firstParamName))
                {
                    continue;
                }
                return member;
            }
            return null;
        }

        // 运行期取 TypeSheet 的 TypeInfo.name（sheet→typeInfoId 字段 →
        // TypeInfo.name 槽装载；借用字面量永生存储，不 acquire——
        // rigi_type_name_of 同口径）。产出 StringAbi 值形态，可直接作
        // Rigi String 实参
        internal static LLVMValueRef LoadTypeNameFromSheet(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef sheet)
        {
            var sheetTy = TypeSheetEmitter.SheetStructType(session.Context);
            var infoField = builder.BuildStructGEP2(sheetTy, sheet,
                (uint)TypeSheetAbi.FieldTypeInfoId, "exc.ti.f");
            var info = builder.BuildLoad2(PointerType(), infoField, "exc.ti");
            var nameField = builder.BuildStructGEP2(TypeInfoEmitter.InfoStructType(session.Context),
                info, (uint)TypeSheetAbi.InfoFieldName, "exc.name.f");
            return builder.BuildLoad2(StringAbi.ValueType(session.Context), nameField,
                "exc.name");
        }

        // 显示名口径（对齐 VM 消息拼写：内建恒点拼写、用户类型恒
        // canonical）：内建 sheet 命中取点拼写常量（select 链），其余落
        // TypeInfo.name 原文
        internal static LLVMValueRef LoadTypeDisplayNameFromSheet(
            ModuleBuilder.Session session, LLVMBuilderRef builder, LLVMValueRef sheet)
        {
            var selected = LoadTypeNameFromSheet(session, builder, sheet);
            var sheetPtr = builder.BuildBitCast(sheet, PointerType(), "exc.sheet.p");
            foreach (var (canonical, display) in BuiltinDisplayNames)
            {
                if (!session.TryGetTypeSheet(canonical, out var builtinSheet))
                {
                    continue;
                }
                var wantPtr = LLVMValueRef.CreateConstBitCast(builtinSheet, PointerType());
                var hit = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, sheetPtr, wantPtr,
                    "exc.disp.eq");
                selected = builder.BuildSelect(hit,
                    session.InternStringConstant(display), selected, "exc.disp.sel");
            }
            return selected;
        }

        // 内建 canonical → BIL 点拼写（与 BilVerificationContext
        // BuiltinScalarAliases 逆投影同集；sheet 比对逐对展开）
        private static readonly (string Canonical, string Display)[] BuiltinDisplayNames =
        {
            ("core::bool", ".bool"), ("core::char", ".char"),
            ("core::i8", ".i8"), ("core::u8", ".u8"),
            ("core::i16", ".i16"), ("core::u16", ".u16"),
            ("core::i32", ".i32"), ("core::u32", ".u32"),
            ("core::i64", ".i64"), ("core::u64", ".u64"),
            ("core::float", ".f32"), ("core::double", ".f64"),
            ("core::String", ".string"),
            ("core::Any", ".any"), ("core::Object", ".object"),
            ("core::ValueType", ".valuetype"),
        };

        // 发射期静态类型名 → String 常量（Session 按文本查重；展示拼
        // 写对齐 VM 消息口径）
        internal static LLVMValueRef StaticTypeName(ModuleBuilder.Session session,
            MirType type) => session.InternStringConstant(
                Bil.BilVerificationContext.DenormalizeTypeRef(type.Canonical));

        // 调用后 pending 检查（checked-flag 便携模型）：rigi_exc_pending()
        // 非空沿异常边跳 ExcTarget 对应 LLVM 块（RcInjection 传播垫已解析，
        // 恒非空），空落内联合成继续块（延续 guard 块先例；继续块不入
        // MIR 块映射）。必须在调用结果落槽之前调用——被调方异常返回时
        // 结果未定义（sret 形态的落槽在调用内发生，无后置回存）
        internal static void EmitPendingCheck(ModuleBuilder.Session session,
            LLVMBuilderRef builder, MirBlock? excTarget)
        {
            var (pendFn, pendType) = CallEmitter.DeclareHelperFace(session,
                RuntimeFaces.ExcPending, PointerType(), System.Array.Empty<LLVMTypeRef>());
            var pending = builder.BuildCall2(pendType, pendFn,
                System.Array.Empty<LLVMValueRef>(), "exc.pending");
            var hasPending = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pending,
                LLVMValueRef.CreateConstNull(PointerType()), "exc.has");
            if (excTarget == null)
            {
                // 传播垫内部 / ctor thunk：无再入异常边。pending 则未捕获
                // 出口（halt），否则续行——避免垫内 fail/publish 虚调把
                // ExcTarget==null 当成 RcInjection 遗漏
                var halt = session.CurrentFunction.AppendBasicBlock("exc.halt");
                var cont = session.CurrentFunction.AppendBasicBlock("exc.cont");
                builder.BuildCondBr(hasPending, halt, cont);
                builder.PositionAtEnd(halt);
                var (haltFn, haltType) = CallEmitter.DeclareVoidFace(session,
                    RuntimeFaces.ExcHalt);
                builder.BuildCall2(haltType, haltFn, System.Array.Empty<LLVMValueRef>(), "");
                builder.BuildUnreachable();
                builder.PositionAtEnd(cont);
                return;
            }
            var blocks = session.CurrentBlocks
                ?? throw new CompilerInternalException("pending 检查缺少当前函数块映射");
            var contResolved = session.CurrentFunction.AppendBasicBlock("exc.cont");
            builder.BuildCondBr(hasPending, blocks[excTarget.Id], contResolved);
            builder.PositionAtEnd(contResolved);
        }
    }
}
