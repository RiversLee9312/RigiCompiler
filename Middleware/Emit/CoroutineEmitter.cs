using System.Collections.Generic;
using LLVMSharp.Interop;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 协程指令发射（MW11c 棒5a 转向，RUNTIME §17.4）：运行时交互点
    /// 已从 rigi_rt 旧 C 面族（rigi_spawn/rigi_task_wait/…，已删除）
    /// 切换为「Rigi 世界方法调（普通 MirCall 经 CallEmitter）+ 最小
    /// 原语指令」。本类只剩三条专用指令：
    ///   MirCoroutineCreate → rigi_coroutine_create(resumeFn 地址
    ///     ptrtoint, frame payload) —— fn 指针物化（Rigi 无此通道）；
    ///   MirFailureLoad → rigi_failure_get(nodeId, outFat)（await 失败
    ///     快路径从 native 注册表读异常）；
    ///   MirCoroutineDone → 纯标记（RcInjection DONE 出口判定依据），
    ///     无发射；
    ///   MirResumeCall → callee resume fn 直调（B-1 tainted→tainted
    ///     协议；i32(ptr) C ABI 与 rigi_coroutine_resume 同形态，但走
    ///     原生栈下钻而非句柄面）。
    /// ARC 纪律：Emit 不插任何 acquire/release——MirCoroutineCreate.
    /// FrameSlot 的 +1 move 移交已由 RcInjection move 集配平；
    /// MirFailureLoad.OutFatSlot 是 +1 产出（RcInjection 产出类前置
    /// release 处理旧值）。resume fn 的 C ABI 特判（i32(ptr) 匹配
    /// RigiResumeFn）在 ModuleBuilder.DeclareFunction/EmitBody
    /// prologue。probe fn 棒5a 起是普通 MIR fn（无 C ABI 特判）。
    /// </summary>
    internal static class CoroutineEmitter
    {
        internal sealed class Create : LlvmEmitVisitor<Create, MirCoroutineCreate>
        {
            protected override void VisitCore(MirCoroutineCreate inst,
                ModuleBuilder.Session session) =>
                EmitCreate(session, session.Builder, session.Slots, inst);
        }

        internal sealed class FailureLoad : LlvmEmitVisitor<FailureLoad, MirFailureLoad>
        {
            protected override void VisitCore(MirFailureLoad inst,
                ModuleBuilder.Session session) =>
                EmitFailureLoad(session, session.Builder, session.Slots, inst);
        }

        internal sealed class Done : LlvmEmitVisitor<Done, MirCoroutineDone>
        {
            protected override void VisitCore(MirCoroutineDone inst,
                ModuleBuilder.Session session)
            {
                // 纯标记：RcInjection 的 DONE 出口判定依据，无发射
            }
        }

        internal sealed class ResumeCall : LlvmEmitVisitor<ResumeCall, MirResumeCall>
        {
            protected override void VisitCore(MirResumeCall inst,
                ModuleBuilder.Session session) =>
                EmitResumeCall(session, session.Builder, session.Slots, inst);
        }

        // MirResumeCall → 直调 callee resume fn（B-1 tainted→tainted
        // 协议）：resume fn 的 LLVM 类型恒为 i32(ptr)（DeclareFunction
        // 的 IsCoroutineResume 特判），frame 胖引用取 payload 半直传，
        // i32 RigiResumeCode 落 code 槽。ARC 纪律：frame 借用（调用方
        // callee 槽持 +1），本指令不产生/不消耗所有权
        private static void EmitResumeCall(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirResumeCall inst)
        {
            var frameFat = session.LoadLocal(builder, slots,
                new MirLocalOperand(inst.FrameSlot));
            var framePayload = builder.BuildExtractValue(frameFat, 1, "rc.frame.pl");
            var resume = session.FunctionOf(inst.ResumeFn.Canonical);
            var code = builder.BuildCall2(resume.Type, resume.Value,
                new[] { builder.BuildIntToPtr(framePayload, PointerType(),
                    "rc.frame.ptr") }, "rc.code");
            builder.BuildStore(code, slots[inst.CodeSlot].Slot);
        }

        // MirCoroutineCreate → rigi_coroutine_create(resumeFnAddr,
        // framePayload) → i64 落 handle 槽：frame 胖引用取 payload 半
        //（i64 直传）；resume fn 取 LLVM 函数地址 ptrtoint（其
        // i32(ptr) C ABI 由 DeclareFunction 强制）。面形状与 stdlib
        // priv native 声明（i64(i64,i64)）一致——NativeCallEmitter 对
        // 同一 C 符号的声明类型相同，无冲突
        private static void EmitCreate(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirCoroutineCreate inst)
        {
            var frameFat = session.LoadLocal(builder, slots,
                new MirLocalOperand(inst.FrameSlot));
            var framePayload = builder.BuildExtractValue(frameFat, 1, "cc.frame.pl");
            var resume = session.FunctionOf(inst.ResumeFn.Canonical);
            var resumeBits = builder.BuildPtrToInt(resume.Value, LLVMTypeRef.Int64,
                "cc.resume");
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session,
                "rigi_coroutine_create", LLVMTypeRef.Int64,
                new[] { LLVMTypeRef.Int64, LLVMTypeRef.Int64 });
            var handle = builder.BuildCall2(fnType, fn,
                new[] { resumeBits, framePayload }, "cc.handle");
            builder.BuildStore(handle, slots[inst.HandleSlot].Slot);
        }

        // MirFailureLoad → rigi_failure_get(nodeId, &outFat)：out 槽地址
        // 直传（RigiFatRef* 即 16B 胖槽，C 侧两段回填 +1 随拷贝移交）；
        // 返回码恒 1（节点恒在，未知 id 面内诊断 abort）
        private static void EmitFailureLoad(ModuleBuilder.Session session,
            LLVMBuilderRef builder,
            Dictionary<string, (LLVMValueRef Slot, MirLocal Local)> slots,
            MirFailureLoad inst)
        {
            var nodeId = session.LoadLocal(builder, slots,
                new MirLocalOperand(inst.NodeIdSlot));
            var (fn, fnType) = CallEmitter.DeclareHelperFace(session,
                "rigi_failure_get", LLVMTypeRef.Int32,
                new[] { LLVMTypeRef.Int64, PointerType() });
            builder.BuildCall2(fnType, fn,
                new[] { nodeId, slots[inst.OutFatSlot].Slot }, "");
        }

        private static LLVMTypeRef PointerType() =>
            LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
    }
}
