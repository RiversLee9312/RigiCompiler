using LLVMSharp.Interop;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 标量运行时检查的策略注入点。MW9b-G：整数除零 → 抛语言级
    /// core.DividedByZeroException（与 VM 同型同消息，可被 try/catch
    /// 捕获，沿 MIR 异常边传播）；i64 MIN/-1 的基础设施溢出失败仍走
    /// rigi_rt abort 面（VM 基准为溢出失败，非语言级异常）。
    /// </summary>
    internal interface IScalarCheckPolicy
    {
        // 整数除法前检查（VM 基准）：divisor==0 全宽度有/无符号同例抛
        // DividedByZeroException；i64 MIN/-1 为基础设施溢出失败；
        // i8/i16/i32 MIN/-1 回绕（不在此拦截，由 ScalarEmitter 以取负
        // 选择消 UB）。excTarget = MIR 异常边目标（RcInjection 已解析）
        void EmitDivGuard(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef dividend, LLVMValueRef divisor, bool isSigned,
            MirBlock? excTarget);

        // 整数取模前检查（mod-3）：仅 divisor==0 抛 DividedByZeroException
        //（与除法同一异常面）；刻意无 i64 MIN/-1 abort 臂——取模无溢出
        // UB 面（x % ±1 == 0，ScalarEmitter.BuildSignedMod 以 select 消毒
        // 消 srem 的 MIN/-1 UB），与 VM 的「模 ±1 得 0」行为一致
        void EmitModGuard(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef divisor, MirBlock? excTarget);
    }

    /// <summary>
    /// 默认策略（MW9b-G）：条件分支命中 → 抛真异常（除零走
    /// ExceptionEmitter 共享抛出辅助；溢出保留 abort 面 noreturn）→
    /// 不命中落入续行块。throw/abort/ok 块追加在当前函数尾，原块以
    /// 条件跳转自然收尾，控制流等价。
    /// </summary>
    internal sealed class ThrowScalarCheckPolicy : IScalarCheckPolicy
    {
        public void EmitDivGuard(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef dividend, LLVMValueRef divisor, bool isSigned,
            MirBlock? excTarget)
        {
            // divisor == 0 → 抛 core.DividedByZeroException（有/无符号、
            // 全宽度同例）
            EmitThrowGuard(session, builder,
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, divisor,
                    LLVMValueRef.CreateConstNull(divisor.TypeOf), "div.zero"),
                excTarget);

            // i64 有符号 MIN/-1：VM 基准为基础设施溢出失败 → abort；
            // 窄宽度回绕由 ScalarEmitter 的取负选择处理
            if (!isSigned || dividend.TypeOf.IntWidth < 64)
            {
                return;
            }
            var isOverflow = builder.BuildAnd(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, dividend,
                    LLVMValueRef.CreateConstInt(dividend.TypeOf, 1UL << 63, false), "div.min"),
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, divisor,
                    LLVMValueRef.CreateConstAllOnes(divisor.TypeOf), "div.negone"),
                "div.ovf");
            EmitAbortGuard(session, builder, isOverflow, RuntimeFaces.AbortArithmeticOverflow);
        }

        // mod 与 div 同一异常面：divisor == 0 → 抛 DividedByZeroException；
        // 不带 i64 MIN/-1 abort 臂（取模无溢出 UB 面，BuildSignedMod 已
        // select 消毒）；EmitThrowGuard 落续行块后自然衔接 srem/urem
        public void EmitModGuard(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef divisor, MirBlock? excTarget)
        {
            EmitThrowGuard(session, builder,
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, divisor,
                    LLVMValueRef.CreateConstNull(divisor.TypeOf), "mod.zero"),
                excTarget);
        }

        // 条件命中 → 抛 DividedByZeroException（无参 init）→ 沿异常边
        // br ExcTarget；否则落续行块（builder 最终定位在续行块尾，后续
        // 发射自然衔接）
        private static void EmitThrowGuard(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef condition, MirBlock? excTarget)
        {
            var fn = session.CurrentFunction;
            var throwBlock = fn.AppendBasicBlock("check.throw");
            var okBlock = fn.AppendBasicBlock("check.ok");
            builder.BuildCondBr(condition, throwBlock, okBlock);
            builder.PositionAtEnd(throwBlock);
            ExceptionEmitter.EmitThrowNewException(session, builder,
                "core::DividedByZeroException", null,
                System.Array.Empty<LLVMValueRef>(), excTarget);
            builder.PositionAtEnd(okBlock);
        }

        // 条件命中 → 调 abort 面 → unreachable；否则落续行块
        private static void EmitAbortGuard(ModuleBuilder.Session session,
            LLVMBuilderRef builder, LLVMValueRef condition, string faceSymbol)
        {
            var fn = session.CurrentFunction;
            var abortBlock = fn.AppendBasicBlock("check.abort");
            var okBlock = fn.AppendBasicBlock("check.ok");
            builder.BuildCondBr(condition, abortBlock, okBlock);
            builder.PositionAtEnd(abortBlock);
            var (face, faceType) = CallEmitter.DeclareVoidFace(session, faceSymbol);
            builder.BuildCall2(faceType, face, System.Array.Empty<LLVMValueRef>(), "");
            builder.BuildUnreachable();
            builder.PositionAtEnd(okBlock);
        }
    }
}
