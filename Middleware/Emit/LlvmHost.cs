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
        private static readonly object Ownership = new();

        // global context 与共享 TargetMachine 的所有权必须覆盖模块整个生命期。
        // Monitor 可重入，短宿主查询/目标验证可在完整发射 lease 内复用。
        internal static IDisposable Enter()
        {
            Monitor.Enter(Ownership);
            return new Lease();
        }
        private sealed class Lease : IDisposable
        {
            public void Dispose() => Monitor.Exit(Ownership);
        }
        private static readonly LoadedLibraryIdentity? LoadedIdentity;
        internal static string? ContentIdentity()
        {
            using var lease = Enter();
            _ = SharedHostMachine;
            return LoadedIdentity?.Verify();
        }
        internal static void RequireNotOwned()
        {
            if (Monitor.IsEntered(Ownership))
                throw new InvalidOperationException("持有 LLVM lease 时禁止等待缓存锁");
        }
        internal static void RequireOwnership()
        {
            if (!Monitor.IsEntered(Ownership))
                throw new InvalidOperationException("LLVM 模块生命期必须持有 LlvmHost.Enter lease");
        }

        static LlvmHost()
        {
            LLVM.InitializeAllTargetInfos();
            LLVM.InitializeAllTargets();
            LLVM.InitializeAllTargetMCs();
            LLVM.InitializeAllAsmPrinters();
            // 必须紧随首次加载捕获，不能等到首轮对象查询才给旧映射配新磁盘摘要。
            LoadedIdentity = LoadedLibraryIdentity.Capture();
        }

        // 宿主默认三元组（如 x86_64-pc-windows-msvc / x86_64-pc-linux-gnu）
        public static string HostTriple { get { using var lease = Enter(); return LLVMTargetRef.DefaultTriple; } }

        // 宿主 TargetMachine：通用 CPU、默认优化/代码模型；重定位模型锁定 PIC——
        // MW4 起产物含全局间指针初始化（TypeSheet/vtable 互引、胖引用 typeid），
        // linux 上 clang/lld 默认 PIE 链接会拒绝非 PIC 的 R_X86_64_32/32S 绝对
        // 重定位（win COFF 对此不敏感，PIC 无副作用）。
        // 进程级缓存共享一台：LLVMSharp 20.1.2 的 LLVMTargetMachineRef 非
        // IDisposable（释放需指针 API），进程退出时随 libLLVM 一并回收；
        // 真实发射由可重入 Monitor lease 串行化；调用方从模块创建到 Dispose
        // 持有同一 lease，宿主查询/目标验证使用短 lease。
        private static readonly Lazy<LLVMTargetMachineRef> HostMachine = new(CreateHostTargetMachine);

        public static LLVMTargetMachineRef SharedHostMachine { get { RequireOwnership(); return HostMachine.Value; } }

        // 布局指针编组统一收敛于 LlvmBitcode 的唯一 unsafe 边界。
        public static string HostDataLayout => LlvmBitcode.HostDataLayout();

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
