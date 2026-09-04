using RigiCompiler.Bil;

namespace RigiCompiler.Middleware.Mir
{
    /// <summary>
    /// BIL→MIR 唯一分派器（对照 P3 Dispatchers.cs）：指令种类 → CRTP 类。
    /// 新增 BIL opcode 在此加一行 + 归属簇文件的 visitor。
    /// </summary>
    internal static class MirLowerDispatchers
    {
        public static void Visit(BilInstruction inst, FlowBuilder flow)
        {
            switch (inst)
            {
                case IfInstruction ifInst:
                    IfLowering.Visit(ifInst, flow);
                    break;
                case LoopInstruction loop:
                    LoopLowering.Visit(loop, flow);
                    break;
                case SwitchInstruction sw:
                    SwitchLowering.Visit(sw, flow);
                    break;
                case CallBlockInstruction callBlock:
                    CallBlockLowering.Visit(callBlock, flow);
                    break;
                case BreakInstruction brk:
                    BreakLowering.Visit(brk, flow);
                    break;
                case ContinueInstruction cont:
                    ContinueLowering.Visit(cont, flow);
                    break;
                case RetInstruction ret:
                    RetLowering.Visit(ret, flow);
                    break;
                case HintInstruction hint:
                    HintLowering.Visit(hint, flow);
                    break;
                case TryInstruction tryInst:
                    TryLowering.Visit(tryInst, flow);
                    break;
                case ThrowInstruction throwInst:
                    ThrowLowering.Visit(throwInst, flow);
                    break;
                case LoadInstruction load:
                    LoadLowering.Visit(load, flow);
                    break;
                case SetVarInstruction setVar:
                    SetVarLowering.Visit(setVar, flow);
                    break;
                case GetVarInstruction getVar:
                    GetVarLowering.Visit(getVar, flow);
                    break;
                case BinaryIntrinsicInstruction binary:
                    BinaryIntrinsicLowering.Visit(binary, flow);
                    break;
                case UnaryIntrinsicInstruction unary:
                    UnaryIntrinsicLowering.Visit(unary, flow);
                    break;
                case InvokeInstruction invoke:
                    InvokeLowering.Visit(invoke, flow);
                    break;
                case InvokeNoResultInstruction invokeNoResult:
                    InvokeNoResultLowering.Visit(invokeNoResult, flow);
                    break;
                case InvokeIndirectInstruction invokeIndirect:
                    InvokeIndirectLowering.Visit(invokeIndirect, flow);
                    break;
                case InvokeIndirectNoResultInstruction invokeIndirectNoResult:
                    InvokeIndirectNoResultLowering.Visit(invokeIndirectNoResult, flow);
                    break;
                case GetFieldInstruction getField:
                    GetFieldLowering.Visit(getField, flow);
                    break;
                case GetFieldIndirectInstruction getFieldIndirect:
                    GetFieldIndirectLowering.Visit(getFieldIndirect, flow);
                    break;
                case SetFieldIndirectInstruction setFieldIndirect:
                    SetFieldIndirectLowering.Visit(setFieldIndirect, flow);
                    break;
                case GetFieldStaticIndirectInstruction getStaticIndirect:
                    GetFieldStaticIndirectLowering.Visit(getStaticIndirect, flow);
                    break;
                case SetFieldStaticIndirectInstruction setStaticIndirect:
                    SetFieldStaticIndirectLowering.Visit(setStaticIndirect, flow);
                    break;
                case SetFieldInstruction setField:
                    SetFieldLowering.Visit(setField, flow);
                    break;
                case GetFieldStaticInstruction getStatic:
                    GetStaticLowering.Visit(getStatic, flow);
                    break;
                case SetFieldStaticInstruction setStatic:
                    SetStaticLowering.Visit(setStatic, flow);
                    break;
                case GetArrayInstruction getArray:
                    GetArrayLowering.Visit(getArray, flow);
                    break;
                case SetArrayInstruction setArray:
                    SetArrayLowering.Visit(setArray, flow);
                    break;
                case GetIdTypeInstruction getIdType:
                    GetIdTypeLowering.Visit(getIdType, flow);
                    break;
                case GetIdFieldInstruction getIdField:
                    GetIdFieldLowering.Visit(getIdField, flow);
                    break;
                case GetIdVarInstruction getIdVar:
                    GetIdVarLowering.Visit(getIdVar, flow);
                    break;
                case DirectTypeCheckInstruction directCheck:
                    DirectTypeCheckLowering.Visit(directCheck, flow);
                    break;
                case IndirectTypeCheckInstruction indirectCheck:
                    IndirectTypeCheckLowering.Visit(indirectCheck, flow);
                    break;
                case GetWrapperInstruction getWrapper:
                    GetWrapperLowering.Visit(getWrapper, flow);
                    break;
                case GetWrapperIndirectInstruction getWrapperIndirect:
                    GetWrapperIndirectLowering.Visit(getWrapperIndirect, flow);
                    break;
                case GetSelfInstruction getSelf:
                    GetSelfLowering.Visit(getSelf, flow);
                    break;
                case GetWrapperFieldInstruction getWrapperField:
                    GetWrapperFieldLowering.Visit(getWrapperField, flow);
                    break;
                case SetWrapperFieldInstruction setWrapperField:
                    SetWrapperFieldLowering.Visit(setWrapperField, flow);
                    break;
                case NewWrapperEntityInstruction newWrapper:
                    NewWrapperEntityLowering.Visit(newWrapper, flow);
                    break;
                case NewWrapperFieldInstruction newWrapperField:
                    NewWrapperFieldLowering.Visit(newWrapperField, flow);
                    break;
                case NewWrapperMethodInstruction newWrapperMethod:
                    NewWrapperMethodLowering.Visit(newWrapperMethod, flow);
                    break;
                case NewIndirectInstruction newIndirect:
                    NewIndirectLowering.Visit(newIndirect, flow);
                    break;
                case NewInstruction newInst:
                    NewLowering.Visit(newInst, flow);
                    break;
                case NewWrappedInstruction newWrapped:
                    NewWrappedLowering.Visit(newWrapped, flow);
                    break;
                case NewCaseInstruction newCase:
                    NewCaseLowering.Visit(newCase, flow);
                    break;
                case NewWrappedCaseInstruction newWrappedCase:
                    NewWrappedCaseLowering.Visit(newWrappedCase, flow);
                    break;
                case IsCaseInstruction isCase:
                    IsCaseLowering.Visit(isCase, flow);
                    break;
                case CastInstruction cast:
                    CastLowering.Visit(cast, flow);
                    break;
                case CastIndirectInstruction castIndirect:
                    CastIndirectLowering.Visit(castIndirect, flow);
                    break;
                case AwaitInstruction awaitInst:
                    AwaitLowering.Visit(awaitInst, flow);
                    break;
                case YieldInstruction yieldInst:
                    YieldLowering.Visit(yieldInst, flow);
                    break;
                default:
                    throw new MwNotSupportedException(
                        $"MW3 不支持指令 {inst.Opcode}（fn {flow.FnSymbol}）");
            }
        }
    }
}
