using System;
using System.Text;
using LLVMSharp.Interop;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// libLLVM 指针 API 的编组封装（仓库唯一 unsafe 点）：rigi_rt bitcode 的
    /// 解析与进程内模块合并（MIDDLEWARE_ARCHITECTURE §2：C 写的运行时面经
    /// 此通路进模块，优化管线统一内联）、新 PM 优化管线驱动。字符串一律
    /// UTF-8 编组。
    /// </summary>
    internal static unsafe class LlvmBitcode
    {
        // undef 常量（MW9a MirRetThrow 值返回出口：异常路径返回值无定义；
        // 安全封装只暴露值句柄，指针编组收敛于本类）
        public static LLVMValueRef UndefOf(LLVMTypeRef type) => LLVM.GetUndef(type);

        // opaque pointer 下函数的真实类型不能从 Value.TypeOf 反推。
        public static LLVMTypeRef FunctionTypeOf(LLVMValueRef function) =>
            LLVM.GlobalGetValueType(function);

        internal static string HostDataLayout()
        {
            var data = LLVM.CreateTargetDataLayout(LlvmHost.SharedHostMachine);
            try
            {
                var text = LLVM.CopyStringRepOfTargetData(data);
                try { return new string(text); }
                finally { LLVM.DisposeMessage(text); }
            }
            finally { LLVM.DisposeTargetData(data); }
        }

        // 命中与新产物均解析真实 bitcode，而不信任缓存目录名或 manifest。
        // 宿主 TargetMachine 的布局由 libLLVM 计算，拒绝相同 triple 下的
        // ABI 布局漂移；拒绝发生在 LinkModules2 输出 warning 之前。
        internal static void ValidateRuntimeTarget(string bitcodePath, string targetTriple)
        {
            var pathBytes = Utf8(bitcodePath);
            fixed (byte* pathPtr = pathBytes)
            {
                LLVMOpaqueMemoryBuffer* buffer;
                sbyte* message;
                if (LLVM.CreateMemoryBufferWithContentsOfFile((sbyte*)pathPtr,
                        &buffer, &message) != 0)
                {
                    try
                    {
                        throw new InvalidOperationException("rigi_rt bitcode 读取失败: "
                            + (message != null ? new string(message) : bitcodePath));
                    }
                    finally
                    {
                        if (message != null) LLVM.DisposeMessage(message);
                    }
                }
                try
                {
                    LLVMMemoryBufferRef bufferRef = buffer;
                    using var context = LLVMContextRef.Create();
                    if (!context.TryParseBitcode(bufferRef, out var runtime, out var error))
                        throw new InvalidOperationException($"rigi_rt bitcode 解析失败: {error}");
                    using (runtime)
                    {
                        // TargetData 是 TargetMachine 的真实布局；不由 triple 文本推断。
                        var layout = LlvmHost.HostDataLayout;
                        if (!StringComparer.Ordinal.Equals(runtime.Target, targetTriple)
                            || !StringComparer.Ordinal.Equals(runtime.DataLayout, layout))
                            throw new InvalidOperationException(
                                $"rigi_rt bitcode 目标不兼容：triple={runtime.Target}（期望 {targetTriple}），"
                                + $"data layout={runtime.DataLayout}（期望 {layout}）");
                    }
                }
                finally
                {
                    LLVM.DisposeMemoryBuffer(buffer);
                }
            }
        }

        // 读取 bitcode 文件并合并进目标模块；失败抛 MwNotSupportedException
        //（rigi_rt 编译产物损坏属环境/工具链问题，非编译器 bug）
        public static void MergeBitcodeFileInto(LLVMModuleRef module, string bitcodePath)
        {
            var pathBytes = Utf8(bitcodePath);
            fixed (byte* pathPtr = pathBytes)
            {
                LLVMOpaqueMemoryBuffer* buffer;
                sbyte* message;
                if (LLVM.CreateMemoryBufferWithContentsOfFile((sbyte*)pathPtr, &buffer, &message) != 0)
                {
                    try
                    {
                        var text = message != null ? new string(message) : "无法读取文件";
                        throw new MwNotSupportedException($"rigi_rt bitcode 读取失败: {text}（{bitcodePath}）");
                    }
                    finally
                    {
                        if (message != null) LLVM.DisposeMessage(message);
                    }
                }
                try
                {
                    LLVMMemoryBufferRef bufferRef = buffer;
                    if (!module.Context.TryParseBitcode(bufferRef,
                            out var runtimeModule, out var parseError))
                    {
                        throw new MwNotSupportedException(
                            $"rigi_rt bitcode 解析失败: {parseError}（{bitcodePath}）");
                    }
                    if (LLVM.LinkModules2(module, runtimeModule) != 0)
                    {
                        throw new CompilerInternalException("rigi_rt bitcode 合并失败（符号冲突？）");
                    }
                }
                finally
                {
                    LLVM.DisposeMemoryBuffer(buffer);
                }
            }
        }

        // 只遍历函数句柄计数，避免 PrintToString 巨型 IR 的分配/序列化开销。
        internal static int CountFunctions(LLVMModuleRef module)
        {
            var count = 0;
            for (var fn = LLVM.GetFirstFunction(module); fn != null;
                fn = LLVM.GetNextFunction(fn))
                count++;
            return count;
        }

        // 新 PM 默认优化管线（MIDDLEWARE §1：通用优化全交 LLVM；运行时面的
        // 内联在 default<O2> 内发生）。失败抛 CompilerInternalException
        public static void RunDefaultOptimization(LLVMModuleRef module)
        {
            var passes = Utf8("default<O2>");
            fixed (byte* passesPtr = passes)
            {
                var options = LLVM.CreatePassBuilderOptions();
                try
                {
                    var error = LLVM.RunPasses(module, (sbyte*)passesPtr,
                        LlvmHost.SharedHostMachine, options);
                    if (error != null)
                    {
                        var message = LLVM.GetErrorMessage(error);
                        try
                        {
                            throw new CompilerInternalException(
                                $"LLVM 优化管线失败: {new string(message)}");
                        }
                        finally
                        {
                            LLVM.DisposeErrorMessage(message);
                        }
                    }
                }
                finally
                {
                    LLVM.DisposePassBuilderOptions(options);
                }
            }
        }

        private static byte[] Utf8(string text)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
            return bytes;
        }
    }
}
