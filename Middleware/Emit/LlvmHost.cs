using LLVMSharp.Interop;

namespace RigiCompiler.Middleware.Emit
{
    /// <summary>
    /// LLVM 进程内宿主（MIDDLEWARE_ARCHITECTURE §2）：目标注册表一次性初始化
    ///（静态构造保证恰好一次、线程安全）与宿主 TargetMachine 解析。
    /// 锁定 LLVM 20（LLVMSharp.Interop 20.1.x + libLLVM NuGet 按 RID 分发）。
    /// </summary>
    internal static class LlvmHost
    {
        static LlvmHost()
        {
            LLVM.InitializeAllTargetInfos();
            LLVM.InitializeAllTargets();
            LLVM.InitializeAllTargetMCs();
            LLVM.InitializeAllAsmPrinters();
        }

        // 宿主默认三元组（如 x86_64-pc-windows-msvc / x86_64-pc-linux-gnu）
        public static string HostTriple => LLVMTargetRef.DefaultTriple;

        // 宿主 TargetMachine：通用 CPU、默认优化/代码模型；重定位模型锁定 PIC——
        // MW4 起产物含全局间指针初始化（TypeSheet/vtable 互引、胖引用 typeid），
        // linux 上 clang/lld 默认 PIE 链接会拒绝非 PIC 的 R_X86_64_32/32S 绝对
        // 重定位（win COFF 对此不敏感，PIC 无副作用）。
        // 进程级缓存共享一台：LLVMSharp 20.1.2 的 LLVMTargetMachineRef 非
        // IDisposable（释放需指针 API），进程退出时随 libLLVM 一并回收；
        // 发射调用方之间为顺序使用（CLI 单次 / 测试串行）。并发发射与逐编译
        // 单元的机器所有权随 MW6 定稿。
        private static readonly Lazy<LLVMTargetMachineRef> HostMachine = new(CreateHostTargetMachine);

        public static LLVMTargetMachineRef SharedHostMachine => HostMachine.Value;

        private static LLVMTargetMachineRef CreateHostTargetMachine()
        {
            var target = LLVMTargetRef.GetTargetFromTriple(HostTriple);
            return target.CreateTargetMachine(HostTriple, "generic", "",
                LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
                LLVMRelocMode.LLVMRelocPIC,
                LLVMCodeModel.LLVMCodeModelDefault);
        }
    }
}
