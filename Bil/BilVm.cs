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

        public BilVm(BilModule module)
        {
            Module = module;
        }

        // maxSteps：实现级指令步数上限；0（默认）= 不限制。超过抛
        // VmStepLimitException 并记入 Result.Exception，不崩溃。
        public BilVmResult Run(long maxSteps = 0)
        {
            var context = new VmContext(Module);
            context.MaxSteps = maxSteps;
            var executor = new VmExecutor(context);
            try
            {
                // §8.7：main 前急切初始化全部 singleton（companion 的 init 即完成
                // 静态字段 cell 构造与 wrapper 安装）；运行期 new type(singleton)
                // 返回同一份已初始化实例
                context.InitializeSingletons(executor);
                // N1（§8.4.1/§9.3）：全局/静态字段声明初始值（..globals.init），
                // singleton 之后、main 之前
                context.InvokeGlobalInitializers(executor);
            }
            catch (VmStepLimitException ex)
            {
                return new BilVmResult(context.Stdout, context.Stderr, null, ex);
            }
            var entry = context.FindEntrypoint();
            var main = executor.Spawn(entry, Array.Empty<VmValue>());
            executor.Publish(main);
            executor.WaitQuiescence();
            var exception = main.Failure ?? executor.UnobservedFailure;
            return new BilVmResult(context.Stdout, context.Stderr, main.Result, exception);
        }

        public static BilVmResult Run(BilModule module, long maxSteps = 0) =>
            new BilVm(module).Run(maxSteps);
    }
}
