using LLVMSharp.Interop;
using RigiCompiler.Middleware.Runtime;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 标量运行时检查的策略注入点（MW2 占位语义：整数除零 / i64 MIN/-1
    /// → rigi_rt abort 面）。BIL §11.2 的语言级 DividedByZeroException
    /// 随 MW9 异常机制落地——届时提供抛异常的策略实现替换 Session.Checks
    /// 单例，ScalarEmitter 等 codegen 调用点不变（单点替换）。
    /// </summary>
    internal interface IScalarCheckPolicy
    {
        // 整数除法前检查（VM 基准）：divisor==0 全宽度有/无符号同例；
        // i64 MIN/-1 为基础设施溢出失败；i8/i16/i32 MIN/-1 回绕（不在此
        // 拦截，由 ScalarEmitter 以取负选择消 UB）
        void EmitDivGuard(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef dividend, LLVMValueRef divisor, bool isSigned);
    }

    /// <summary>
    /// 默认策略：条件分支命中 → 调 rigi_rt abort 面（noreturn）→
    /// unreachable；不命中落入续行块。abort/ok 块追加在当前函数尾，
    /// 原块以条件跳转自然收尾，控制流等价。
    /// </summary>
    internal sealed class AbortScalarCheckPolicy : IScalarCheckPolicy
    {
        public void EmitDivGuard(ModuleBuilder.Session session, LLVMBuilderRef builder,
            LLVMValueRef dividend, LLVMValueRef divisor, bool isSigned)
        {
            // divisor == 0 → abort（有/无符号、全宽度同例）
            EmitAbortGuard(session, builder,
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, divisor,
                    LLVMValueRef.CreateConstNull(divisor.TypeOf), "div.zero"),
                RuntimeFaces.AbortDividedByZero);

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

        // 条件命中 → 调 abort 面 → unreachable；否则落续行块（builder
        // 最终定位在续行块尾，后续发射自然衔接）
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
