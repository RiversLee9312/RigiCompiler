using LLVMSharp.Interop;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// 目标文件发射（MW6）：已构建的 LLVM 模块 → 进程内验证 → 宿主目标机出
    /// .o（MIDDLEWARE_ARCHITECTURE §1/§10 工具链契约）。模块构建归
    /// ModuleBuilder，bitcode 合并与优化归 LlvmBitcode，本类只管最后一公里。
    /// </summary>
    public static class ObjectEmitter
    {
        // 失败返回 false 且 error 为人类可读消息（输出路径不可写 / 目标机不支持等）；
        // 模块验证失败属编译器内部错误（已门禁模块产生非法 LLVM IR = 发射器 bug）
        public static bool TryEmitObject(LLVMModuleRef module, string outputPath, out string error)
        {
            error = "";
            if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var verifyError))
            {
                throw new CompilerInternalException($"LLVM 模块验证失败: {verifyError}");
            }

            var machine = LlvmHost.SharedHostMachine;
            if (!machine.TryEmitToFile(module, outputPath, LLVMCodeGenFileType.LLVMObjectFile,
                out var emitError))
            {
                error = emitError;
                return false;
            }
            return true;
        }
    }
}
