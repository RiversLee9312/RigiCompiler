namespace RigiCompiler.Tests;

/// <summary>只有隔离 worker 可以替换全局输出；不会触碰 TUnit 父宿主的日志。</summary>
internal static class WorkerConsole
{
    internal static void SetOut(TextWriter writer)
    {
        if (!WorkerEnvironment.IsWorker) throw new InvalidOperationException("控制台捕获只能在隔离 worker 内执行");
#pragma warning disable TUnit0055 // 本进程尚未启动 TUnit，是专用隔离 worker。
        Console.SetOut(writer);
#pragma warning restore TUnit0055
    }
    internal static void SetError(TextWriter writer)
    {
        if (!WorkerEnvironment.IsWorker) throw new InvalidOperationException("控制台捕获只能在隔离 worker 内执行");
#pragma warning disable TUnit0055 // 本进程尚未启动 TUnit，是专用隔离 worker。
        Console.SetError(writer);
#pragma warning restore TUnit0055
    }
}
