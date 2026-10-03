namespace RigiCompiler;

// 一次阶段只建一个有限 worker 队列。递归 bootstrap/默认值/lambda 继承
// 当前施工上下文，走串行快路；禁止持同一 budget lease 再嵌套申请。
internal static class CompilerJobs
{
    private static readonly AsyncLocal<int> depth = new();
    private static readonly AsyncLocal<int?> overrideJobs = new();
    internal static bool InWorker => depth.Value != 0;
    internal static int Jobs => overrideJobs.Value ?? ResourceBudget.Shared.Capacity.CpuSlots;
    internal static IDisposable WithJobs(int jobs)
    {
        var old = overrideJobs.Value;
        overrideJobs.Value = jobs;
        return new Restore(() => overrideJobs.Value = old);
    }
    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    internal static T[] MapDiagnosed<T>(CompilationUnit unit, int count, Func<int, T> action, string? phase = null)
    {
        var jobs = Map(count, index =>
        {
            using var diagnostics = unit.Diagnostics.CaptureJob();
            using var logs = Logger.CaptureJob();
            return (Result: action(index), Diagnostics: diagnostics.Items, Logs: logs);
        }, phase: phase);
        foreach (var job in jobs)
        {
            foreach (var diagnostic in job.Diagnostics) unit.Diagnostics.Add(diagnostic);
            job.Logs.Replay();
        }
        return jobs.Select(job => job.Result).ToArray();
    }

    internal static T[] Map<T>(int count, Func<int, T> action, bool small = false,
        CancellationToken cancellationToken = default, string? phase = null)
    {
        var active = 0; var peak = 0; var started = 0; var completed = 0;
        var slots = 1; var acquired = false; var status = "completed";
        T Run(int index)
        {
            Interlocked.Increment(ref started);
            var current = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref peak); }
            while (observed < current && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            try
            {
                var value = action(index);
                Interlocked.Increment(ref completed);
                return value;
            }
            finally { Interlocked.Decrement(ref active); }
        }
        var result = new T[count];
        try
        {
            if (count < 2 || Jobs == 1 || small || depth.Value != 0)
            {
                for (var i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result[i] = Run(i);
                }
                return result;
            }
            var budget = ResourceBudget.Shared;
            slots = Math.Min(count, Math.Min(Jobs, budget.Capacity.CpuSlots));
            using var lease = budget.AcquireAsync(new ResourceRequest(slots,
                Math.Min(256, budget.Capacity.MemoryMiB)), cancellationToken).AsTask().GetAwaiter().GetResult();
            acquired = true;
            var previousDepth = depth.Value;
            var failures = new System.Runtime.ExceptionServices.ExceptionDispatchInfo?[count];
            depth.Value++;
            try
            {
                // indexed slots 无共享 append；join 是下一 pass 的发布屏障。
                Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = slots,
                    CancellationToken = cancellationToken }, i =>
                {
                    try { result[i] = Run(i); }
                    catch (Exception error) { failures[i] = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
                });
                // 等全部已启动的 worker 收尾后，按输入序重抛原异常。
                foreach (var failure in failures) failure?.Throw();
            }
            finally { depth.Value = previousDepth; }
            return result;
        }
        catch (OperationCanceledException) { status = "canceled"; throw; }
        catch { status = "failed"; throw; }
        finally
        {
            if (phase != null) PerformanceMetrics.CompilerWorkers(phase, count, slots, acquired,
                peak, started, completed, status);
        }
    }
}
