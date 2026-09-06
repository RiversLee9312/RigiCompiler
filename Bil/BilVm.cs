using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Bil
{
    // BIL VM 入口（BIL_VM_DESIGN §2 / §4 / §8 / §9 切片 5）：
    // 装载 BilModule、建 hook 表、找 entrypoint、启动 main coroutine，
    // Run 阻塞至全部协程终态（含 fire-and-forget）。入口与后台异常均可汇总。

    public sealed class BilVmResult
    {
        public string Stdout { get; }
        public string Stderr { get; }
        public VmValue? ReturnValue { get; }
        public VmException? Exception { get; }

        public BilVmResult(string stdout, string stderr, VmValue? returnValue, VmException? exception)
        {
            Stdout = stdout;
            Stderr = stderr;
            ReturnValue = returnValue;
            Exception = exception;
        }
    }

    public sealed class BilVm
    {
        public BilModule Module { get; }

        // 测试缝：最近一次 Run 的 VmContext（调度内部观测——Worker
        // 登记表/ResumeLog 等，BilVmTask 套件断言多 Executor 行为用）
        internal VmContext? LastContext { get; private set; }
        internal bool TraceResumes { get; set; }

        public BilVm(BilModule module)
        {
            Module = module;
        }

        // maxSteps：实现级指令步数上限；0（默认）= 不限制。超过抛
        // VmStepLimitException 并记入 Result.Exception，不崩溃。
        // entryPoint：--entry-point 显式指定的入口符号（BIL canonical）；
        // null = 自动查找（恰一个 entrypoint 才选中，否则抛 VmException）
        //
        // MW11c 棒4a（§17.4）：main 发布到 Dispatcher（MainExecutor 调度
        // 域），主线程即 Worker——同步解释 Dispatcher$workerLoop(0) 至
        // quiescence（live==0 且队列空），再汇总 main 失败/未观察失败
        public BilVmResult Run(long maxSteps = 0, string? entryPoint = null)
        {
            var context = new VmContext(Module);
            context.Dispatch.TraceResumes = TraceResumes;
            LastContext = context;
            context.MaxSteps = maxSteps;
            try
            {
                // §8.7：main 前急切初始化全部 singleton（companion 的 init 即完成
                // 静态字段 cell 构造与 wrapper 安装）；运行期 new type(singleton)
                // 返回同一份已初始化实例
                context.InitializeSingletons();
                // N1（§8.4.1/§9.3）：全局/静态字段声明初始值（..globals.init），
                // singleton 之后、main 之前
                context.InvokeGlobalInitializers();
            }
            catch (VmStepLimitException ex)
            {
                return new BilVmResult(context.Stdout, context.Stderr, null, ex);
            }
            var entry = context.FindEntrypoint(entryPoint);
            // 无 Dispatcher 的直建模块（单元测试）走降级通道同步直跑
            if (!context.Dispatch.HasDispatcher)
            {
                var standalone = context.Dispatch.RunStandalone(entry, Array.Empty<VmValue>());
                // MW12b §25.2：main 之后、失败汇总之前派发 undisposed 事件
                var standaloneDrain = context.CollectAndDispatchUndisposed();
                return new BilVmResult(context.Stdout, context.Stderr,
                    standalone.Result, standalone.Failure ?? standaloneDrain);
            }
            var main = context.Dispatch.Spawn(entry, Array.Empty<VmValue>(), caller: null);
            context.Dispatch.MainHandle = main.Handle;
            context.Dispatch.RunMainLoop();
            // MW12b §25.2（VM 半场）：main/drain 之后、失败汇总之前——逼 GC
            // 收出未 dispose 对象的终结器事件，逐条真构造异常调
            // GlobalExceptionHandler.dispatch；派发失败走未捕获异常归宿。
            // main 帧在终态已全弹（ReturnFromFrame），局部对象不再被根住；
            // 静态槽/单例保持根住（不模拟静态退出清理的销毁检查）
            var drainFailure = context.CollectAndDispatchUndisposed();
            var exception = main.Failure ?? context.Dispatch.UnobservedFailure ?? drainFailure;
            return new BilVmResult(context.Stdout, context.Stderr, main.Result, exception);
        }

        public static BilVmResult Run(BilModule module, long maxSteps = 0,
            string? entryPoint = null) =>
            new BilVm(module).Run(maxSteps, entryPoint);
    }
}
