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

        public BilVmResult Run()
        {
            var context = new VmContext(Module);
            var executor = new VmExecutor(context);
            // §8.7：main 前急切初始化全部 singleton（companion 的 init 即完成
            // 静态字段 cell 构造与 wrapper 安装）；运行期 new type(singleton)
            // 返回同一份已初始化实例
            context.InitializeSingletons(executor);
            var entry = context.FindEntrypoint();
            var main = executor.Spawn(entry, Array.Empty<VmValue>());
            executor.Publish(main);
            executor.WaitQuiescence();
            var exception = main.Failure ?? executor.UnobservedFailure;
            return new BilVmResult(context.Stdout, context.Stderr, main.Result, exception);
        }

        public static BilVmResult Run(BilModule module) => new BilVm(module).Run();
    }
}
