namespace RigiCompiler.Tests;

/// <summary>协议契约可同时提交多个隔离请求；框架全量本身逐发现行调用客户端。</summary>
public static class CaseWorkers
{
    public static async Task<IReadOnlyList<CaseOutcome>> RunTasksAsync(IReadOnlyList<CaseSelection.TaskSpec> tasks,
        CancellationToken cancellationToken = default)
    {
        if (WorkerEnvironment.IsWorker) throw new InvalidOperationException("隔离 worker 禁止嵌套调度");
        var client = new CaseWorkerClient();
        return await Task.WhenAll(tasks.Select(task => client.RunAsync(task.Id, cancellationToken, task.Timeout)));
    }
}
