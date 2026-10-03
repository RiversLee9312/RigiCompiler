using System.Diagnostics;
using RigiCompiler;
using TUnit.Core;

namespace RigiCompiler.TUnitTests;

[Category("Budget")]
public class ResourceBudgetTests
{
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }

    [Test]
    public void CpuSetRanges()
    {
        Check(ResourceCapacity.CountCpuSet("0-3,8,10-11") == 7, "cpuset 范围");
        Check(ResourceCapacity.CountCpuSet("1-3,2-4") == 4, "重复集合不重复计数");
        try { ResourceCapacity.CountCpuSet("4-1"); throw new InvalidOperationException("坏范围被接受"); }
        catch (InvalidDataException) { }
    }

    [Test]
    public async Task MixedWeightsAndFairness()
    {
        var budget = new ResourceBudget(new(4, 1024));
        using var first = await budget.AcquireAsync(new(2, 256));
        var big = budget.AcquireAsync(new(4, 768)).AsTask();
        var small = budget.AcquireAsync(new(1, 256)).AsTask();
        Check(!big.IsCompleted && !small.IsCompleted && budget.Usage == (2, 256, 2), "FIFO 头部必须预留大请求");
        first.Dispose();
        using var granted = await big.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!small.IsCompleted && budget.Usage == (4, 768, 1), "大请求不能被后来的小请求越过");
        granted.Dispose();
        using var last = await small.WaitAsync(TimeSpan.FromSeconds(2));
        Check(budget.Usage == (1, 256, 0), "混合 CPU/内存权重准确");
    }

    [Test]
    public async Task MemoryAndExclusiveGroups()
    {
        var budget = new ResourceBudget(new(4, 1024));
        using var first = await budget.AcquireAsync(new(1, 768, 1, 1, "LLVM"));
        var next = budget.AcquireAsync(new(1, 512)).AsTask();
        Check(!next.IsCompleted, "内存与同进程 exclusive 都应阻止授予");
        first.Dispose();
        using var granted = await next.WaitAsync(TimeSpan.FromSeconds(2));
        Check(budget.Usage == (1, 512, 0), "内存单独约束且释放配对");
        using var holder = await budget.AcquireAsync(new(1, 128, 1, 1, "LLVM"));
        var sameGroup = budget.AcquireAsync(new(1, 128, 1, 1, "LLVM")).AsTask();
        Check(!sameGroup.IsCompleted, "CPU和内存均足够时exclusive单独约束");
        holder.Dispose();
        using var same = await sameGroup.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task WaitingCancellation()
    {
        var budget = new ResourceBudget(new(2, 1024));
        using var first = await budget.AcquireAsync(new(1, 512));
        using var cancellation = new CancellationTokenSource();
        var blocked = budget.AcquireAsync(new(2, 512), cancellation.Token).AsTask();
        var following = budget.AcquireAsync(new(1, 256)).AsTask();
        cancellation.Cancel();
        try { await blocked; throw new InvalidOperationException("未取消"); } catch (OperationCanceledException) { }
        using var granted = await following.WaitAsync(TimeSpan.FromSeconds(2));
        Check(budget.Usage == (2, 768, 0), "取消排队头部后应立即推进后续请求");
    }

    [Test]
    public async Task ExceptionReleaseAndOversize()
    {
        var budget = new ResourceBudget(new(1, 512));
        try { await using var lease = await budget.AcquireAsync(new(1, 512)); throw new IOException("探针"); }
        catch (IOException) { }
        Check(budget.Usage == (0, 0, 0), "异常必须释放所有权重");
        try { await budget.AcquireAsync(new(2, 512)); throw new InvalidOperationException("超容量被接受"); }
        catch (ArgumentOutOfRangeException) { }
        Check(budget.Usage == (0, 0, 0), "超容量应明确拒绝而非无限等");
    }

    [Test]
    public void CompilerBulkProfiles()
    {
        foreach (var name in new[] { "DeclarationResolver", "Binder", "BilEmitter", "Lowerer", "SmartCast", "StdlibSources" })
        {
            var number = RigiCompiler.Tests.TestRunner.GetSuiteNumber(name);
            var task = RigiCompiler.Tests.LegacyDispatcher.Select(number,
                name is "Binder" or "BilEmitter" ? ["WRAP-001"] : []).Single();
            var request = RigiCompiler.Tests.LegacyDispatcher.ResourcesFor(task.Id);
            Check(request.MemoryMiB == 2048 && request.CpuSlots == 1,
                "多份 stdlib 图的旧整组生命周期必须声明实际内存：" + name);
        }
        var single = RigiCompiler.Tests.LegacyDispatcher.Select(
            RigiCompiler.Tests.TestRunner.GetSuiteNumber("E2e"), ["rich_return_nullable"]).Single();
        Check(RigiCompiler.Tests.LegacyDispatcher.ResourcesFor(single.Id).MemoryMiB == 512,
            "单编译 child 契约应继续按自身 profile 授予");
    }

    [Test]
    public async Task PerChildEnvironment()
    {
        var budget = new ResourceBudget(new(4, 2048));
        using var lease = await budget.AcquireAsync(new(4, 1024, 4, 2));
        var first = new ProcessStartInfo(); var second = new ProcessStartInfo();
        var parent = Environment.GetEnvironmentVariable("RIGI_COMPUTE_WORKERS");
        lease.ApplyEnvironment(first);
        Check(first.Environment["RIGI_COMPUTE_WORKERS"] == "4" && first.Environment["RIGI_LLD_THREADS"] == "2"
            && first.Environment["DOTNET_PROCESSOR_COUNT"] == "4" && first.Environment["DOTNET_GCHeapHardLimit"] == "20000000", "lease 子环境");
        Check(first.Environment["RIGI_RESOURCE_LEASE_CPU_SLOTS"] == "4"
            && first.Environment["RIGI_RESOURCE_LEASE_MEMORY_MIB"] == "1024"
            && !first.Environment.ContainsKey("RIGI_TEST_MEMORY_MIB"), "子预算标记不能继承父内存容量");
        Check(Environment.GetEnvironmentVariable("RIGI_COMPUTE_WORKERS") == parent
            && (second.Environment.TryGetValue("RIGI_COMPUTE_WORKERS", out var other) ? other : null) == parent, "不得更改父或其他 child 环境");
    }
}
