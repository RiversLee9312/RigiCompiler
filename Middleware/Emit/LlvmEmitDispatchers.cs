using RigiCompiler.Middleware.Mir;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// MIR→LLVM 唯一分派器（对照 P3 Dispatchers.cs / MirLowerDispatchers）：
    /// 指令种类 → CRTP 类。新增 MIR 指令在此加一行 + 归属簇文件的 visitor。
    /// 终结符另走 TerminatorEmitter（不同类型根，对照前端语句/值双分派器）。
    /// </summary>
    internal static class LlvmEmitDispatchers
    {
        public static void Visit(MirInst inst, ModuleBuilder.Session session)
        {
            switch (inst)
            {
                case MirLoadResource load:
                    ResourceEmitter.Visit(load, session);
                    break;
                case MirCopyLocal copy:
                    CopyLocalEmitter.Visit(copy, session);
                    break;
                case MirBinaryIntrinsic binary:
                    ScalarEmitter.Binary.Visit(binary, session);
                    break;
                case MirUnaryIntrinsic unary:
                    ScalarEmitter.Unary.Visit(unary, session);
                    break;
                case MirGenericBinaryOp genericBinary:
                    GenericOpEmitter.Binary.Visit(genericBinary, session);
                    break;
                case MirGenericUnaryOp genericUnary:
                    GenericOpEmitter.Unary.Visit(genericUnary, session);
                    break;
                case MirCall call:
                    CallEmitter.Invoke.Visit(call, session);
                    break;
                case MirInvokeIndirect invokeIndirect:
                    CallEmitter.Indirect.Visit(invokeIndirect, session);
                    break;
                case MirSuperCall superCall:
                    CallEmitter.Super.Visit(superCall, session);
                    break;
                case MirNewIndirect newIndirect:
                    DynamicNewEmitter.Call.Visit(newIndirect, session);
                    break;
                case MirNewObject newObject:
                    NewEmitter.Object.Visit(newObject, session);
                    break;
                case MirNewValue newValue:
                    NewEmitter.Value.Visit(newValue, session);
                    break;
                case MirNewCase newCase:
                    EnumEmitter.NewCase.Visit(newCase, session);
                    break;
                case MirIsCase isCase:
                    EnumEmitter.IsCase.Visit(isCase, session);
                    break;
                case MirTypeCheck typeCheck:
                    TypeCheckEmitter.Visit(typeCheck, session);
                    break;
                case MirGetWrapper getWrapper:
                    WrapperEmitter.Get.Visit(getWrapper, session);
                    break;
                case MirGetSelf getSelf:
                    WrapperEmitter.GetSelf.Visit(getSelf, session);
                    break;
                case MirInnerCall:
                    throw new CompilerInternalException(
                        "MirInnerCall 未被 ProxyBaking 链接（invoke fn(..inner)）");
                case MirGetWrapperField getWrapperField:
                    WrapperEmitter.GetField.Visit(getWrapperField, session);
                    break;
                case MirGetWrapperAddr getWrapperAddr:
                    WrapperEmitter.GetAddr.Visit(getWrapperAddr, session);
                    break;
                case MirGetWrapperFieldAddr getWrapperFieldAddr:
                    WrapperEmitter.GetFieldAddr.Visit(getWrapperFieldAddr, session);
                    break;
                case MirGetWrapperMethodAddr getWrapperMethodAddr:
                    WrapperEmitter.GetMethodAddr.Visit(getWrapperMethodAddr, session);
                    break;
                case MirSetWrapperField setWrapperField:
                    WrapperEmitter.SetField.Visit(setWrapperField, session);
                    break;
                case MirNewWrapper newWrapper:
                    WrapperEmitter.New.Visit(newWrapper, session);
                    break;
                case MirGetField getField:
                    FieldEmitter.Get.Visit(getField, session);
                    break;
                case MirSetField setField:
                    FieldEmitter.Set.Visit(setField, session);
                    break;
                case MirGetStatic getStatic:
                    StaticFieldEmitter.Get.Visit(getStatic, session);
                    break;
                case MirSetStatic setStatic:
                    StaticFieldEmitter.Set.Visit(setStatic, session);
                    break;
                case MirGetArray getArray:
                    ArrayEmitter.Get.Visit(getArray, session);
                    break;
                case MirSetArray setArray:
                    ArrayEmitter.Set.Visit(setArray, session);
                    break;
                case MirNewArray newArray:
                    ArrayEmitter.New.Visit(newArray, session);
                    break;
                case MirGetTypeId getTypeId:
                    TypeIdEmitter.OfType.Visit(getTypeId, session);
                    break;
                case MirGetFieldId getFieldId:
                    TypeIdEmitter.OfField.Visit(getFieldId, session);
                    break;
                case MirGetTypeIdVar getTypeIdVar:
                    TypeIdEmitter.OfVar.Visit(getTypeIdVar, session);
                    break;
                case MirWrapNullable wrap:
                    NullableEmitter.Wrap.Visit(wrap, session);
                    break;
                case MirUnwrapNullable unwrap:
                    NullableEmitter.Unwrap.Visit(unwrap, session);
                    break;
                case MirBoxAny box:
                    BoxEmitter.Box.Visit(box, session);
                    break;
                case MirUnboxAny unbox:
                    BoxEmitter.Unbox.Visit(unbox, session);
                    break;
                case MirCast cast:
                    CastEmitter.Visit(cast, session);
                    break;
                case MirAcquireSlot acquire:
                    ArcEmitter.Acquire.Visit(acquire, session);
                    break;
                case MirReleaseSlot release:
                    ArcEmitter.Release.Visit(release, session);
                    break;
                case MirTakePending takePending:
                    ExceptionEmitter.TakePending.Visit(takePending, session);
                    break;
                case MirThrow throwInst:
                    ExceptionEmitter.Throw.Visit(throwInst, session);
                    break;
                // MW11c 棒5a：协程三指令（split 后形态；旧五面指令族
                // 已随 C 调度面删除）
                case MirCoroutineCreate create:
                    CoroutineEmitter.Create.Visit(create, session);
                    break;
                case MirFailureLoad failureLoad:
                    CoroutineEmitter.FailureLoad.Visit(failureLoad, session);
                    break;
                case MirCoroutineDone done:
                    CoroutineEmitter.Done.Visit(done, session);
                    break;
                // B-1：tainted→tainted 协议的 resume 直调
                case MirResumeCall resumeCall:
                    CoroutineEmitter.ResumeCall.Visit(resumeCall, session);
                    break;
                default:
                    throw new CompilerInternalException($"未覆盖的 MIR 指令: {inst.GetType().Name}");
            }
        }
    }
}
