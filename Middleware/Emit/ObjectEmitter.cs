using LLVMSharp.Interop;

namespace RigiCompiler.Middleware
{
    /// <summary>
    /// 目标文件发射（MW6 骨架；MW0 收口：空模块进程内出 .o）。当前只发射带
    /// 宿主三元组的空 LLVM 模块——打通「LLVMSharp 进程内构建 → 验证 → 目标
    /// 文件」管线（MIDDLEWARE_ARCHITECTURE §1/§10 工具链契约）；MW1 起消费
    /// MIR 与布局计划逐函数填充模块（.ll 黄金快照产物届时接入）。
    /// </summary>
    public static class ObjectEmitter
    {
        // 失败返回 false 且 error 为人类可读消息（输出路径不可写 / 目标机不支持等）；
        // 模块验证失败属编译器内部错误（已门禁模块产生非法 LLVM IR = 发射器 bug）
        public static bool TryEmitObject(MwContext context, string outputPath, out string error)
        {
            error = "";
            using var module = LLVMModuleRef.CreateWithName(ModuleNameOf(context));
            module.Target = LlvmHost.HostTriple;

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

        // 模块名：Metadata 的 module 键（BIL §4.1，字面量原文含引号需去除），
        // 缺省回退 "rigi.module"
        private static string ModuleNameOf(MwContext context)
        {
            foreach (var entry in context.Module.Metadata)
            {
                if (entry.Key == "module" && entry.Type == Bil.BilScalarType.String)
                {
                    return entry.LiteralText.Trim('"');
                }
            }
            return "rigi.module";
        }
    }
}
