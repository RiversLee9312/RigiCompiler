using System.Diagnostics;
using System.Globalization;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using RigiCompiler.PerfBaseline;

namespace RigiCompiler;

/// <summary>资源权重不是 OS 总线程数；GC、IO 与工具内部辅助线程另有开销。</summary>
public sealed record ResourceRequest(int CpuSlots, int MemoryMiB, int ComputeWorkers, int LldThreads,
    ImmutableArray<string> ExclusiveGroups)
{
    public ResourceRequest(int cpuSlots, int memoryMiB, int computeWorkers = 1, int lldThreads = 1,
        params string[] exclusiveGroups) : this(cpuSlots, memoryMiB, computeWorkers, lldThreads,
            exclusiveGroups.ToImmutableArray()) { }
}

public sealed record ResourceCapacity(int CpuSlots, int MemoryMiB)
{
    public static ResourceCapacity FromEnvironment()
    {
        int cpus = Environment.ProcessorCount;
        if (OperatingSystem.IsLinux())
        {
            var affinity = File.ReadLines("/proc/self/status").FirstOrDefault(line => line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal));
            if (affinity != null) cpus = Math.Min(cpus, CountCpuSet(affinity.Split(':', 2)[1].Trim()));
        }
        if (OperatingSystem.IsWindows())
        {
            uint active = GetActiveProcessorCount(ushort.MaxValue);
            if (active > 0) cpus = Math.Min(cpus, checked((int)active));
            using var process = Process.GetCurrentProcess();
            var affinity = unchecked((ulong)process.ProcessorAffinity.ToInt64());
            if (affinity != 0) cpus = Math.Min(cpus, BitOperations.PopCount(affinity));
        }
        long memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        // 遥测与调度共用实际 membership/mount 路径解析，包括 v1/v2；取所有可见祖先限制。
        var resources = MachineResources.Capture();
        if (resources["linuxCgroups"] is System.Text.Json.Nodes.JsonArray groups)
            foreach (var group in groups)
            {
                if (group?["resolvedDirectory"]?.GetValue<string>() is not { } directory) continue;
                var mount = group["mountPoint"]!.GetValue<string>();
                for (var path = directory; path != null && (path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal));
                    path = Path.GetDirectoryName(path))
                {
                    string? Read(string name) { try { return File.ReadAllText(Path.Combine(path, name)).Trim(); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }
                    foreach (var name in new[] { "cpuset.cpus.effective", "cpuset.cpus" })
                        if (Read(name) is { Length: > 0 } cpuSet) cpus = Math.Min(cpus, CountCpuSet(cpuSet));
                    var quota = Read("cpu.max")?.Split(' ');
                    if (quota is { Length: 2 } && long.TryParse(quota[0], out long q) && long.TryParse(quota[1], out long p) && q > 0 && p > 0)
                        cpus = Math.Min(cpus, Math.Max(1, (int)(q / p)));
                    if (long.TryParse(Read("cpu.cfs_quota_us"), out q) && long.TryParse(Read("cpu.cfs_period_us"), out p) && q > 0 && p > 0)
                        cpus = Math.Min(cpus, Math.Max(1, (int)(q / p)));
                    foreach (var name in new[] { "memory.max", "memory.limit_in_bytes" })
                        if (long.TryParse(Read(name), out long limit) && limit > 0) memory = Math.Min(memory, limit);
                }
            }
        int availableMiB = checked((int)Math.Min(int.MaxValue, memory / (1024 * 1024)));
        // worker 已经由父进程预留总 lease；GC hardlimit 只占其一半。
        // 内部编译阶段使用该子预算，不再次为外层父宿主扣除 512MiB。
        // 仍取实际 GC/cgroup/CPU 的较小值，绝不扩大 child heap 或物理容量。
        if (Environment.GetEnvironmentVariable("RIGI_RESOURCE_LEASE_MEMORY_MIB") is { } leasedMemory)
        {
            if (!int.TryParse(leasedMemory, NumberStyles.None, CultureInfo.InvariantCulture, out var leaseMiB)
                || leaseMiB < 1 || !int.TryParse(Environment.GetEnvironmentVariable("RIGI_RESOURCE_LEASE_CPU_SLOTS"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var leaseCpu) || leaseCpu < 1)
                throw new ArgumentException("child resource lease 容量标记无效");
            cpus = Math.Min(cpus, leaseCpu);
            return new(Parse("RIGI_JOBS", cpus, 1, cpus), Math.Min(availableMiB, leaseMiB));
        }
        // 留父宿主至少 512MiB，通常保留四分之一；child GC hardlimit 再只占 lease 的一半。
        int childMemory = availableMiB - Math.Max(512, availableMiB / 4);
        if (childMemory < 256) throw new InvalidOperationException("可用内存不足以保留父宿主和测试 worker");
        return new(Parse("RIGI_JOBS", cpus, 1, cpus), Parse("RIGI_TEST_MEMORY_MIB", childMemory, 256, childMemory));
    }

    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort groupNumber);

    public static int CountCpuSet(string value)
    {
        var ids = new HashSet<int>();
        foreach (var item in value.Split(','))
        {
            var range = item.Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || from < 0 || from > 1_000_000) throw new InvalidDataException("CPU set 无效");
            var to = from;
            if (range.Length == 2 && (!int.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out to) || to < from || to > 1_000_000))
                throw new InvalidDataException("CPU set 范围无效");
            for (int index = from; index <= to; index++) ids.Add(index);
        }
        return ids.Count > 0 ? ids.Count : throw new InvalidDataException("CPU set 为空");
    }

    internal static int Parse(string variable, int fallback, int minimum, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (value == null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException($"{variable} 必须是 {minimum}..{maximum} 的十进制整数");
        return parsed;
    }
}

/// <summary>同一父进程共享严格 FIFO 加权队列；头部预留避免大任务被小任务饿死。</summary>
public sealed class ResourceBudget
{
    private static readonly Lazy<ResourceBudget> shared = new(() => new(ResourceCapacity.FromEnvironment()));
    public static ResourceBudget Shared => shared.Value;
    public ResourceCapacity Capacity { get; }
    private readonly object gate = new();
    private readonly LinkedList<Pending> pending = new();
    private readonly HashSet<string> exclusive = new(StringComparer.Ordinal);
    private int usedCpu, usedMemory;
    public (int CpuSlots, int MemoryMiB, int Pending) Usage { get { lock (gate) return (usedCpu, usedMemory, pending.Count); } }
    public ResourceBudget(ResourceCapacity capacity)
    {
        if (capacity.CpuSlots < 1 || capacity.MemoryMiB < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }
    private sealed class Pending(ResourceRequest request)
    {
        internal readonly ResourceRequest Request = request;
        internal readonly TaskCompletionSource<ResourceLease> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal LinkedListNode<Pending>? Node;
    }
    public async ValueTask<ResourceLease> AcquireAsync(ResourceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CpuSlots < 1 || request.CpuSlots > Capacity.CpuSlots || request.MemoryMiB < 1 || request.MemoryMiB > Capacity.MemoryMiB
            || request.ComputeWorkers is < 1 or > 254 || request.LldThreads < 1 || request.LldThreads > request.CpuSlots
            || request.ExclusiveGroups.IsDefault || request.ExclusiveGroups.Any(string.IsNullOrWhiteSpace)
            || request.ExclusiveGroups.Distinct(StringComparer.Ordinal).Count() != request.ExclusiveGroups.Length)
            throw new ArgumentOutOfRangeException(nameof(request), "资源请求无效或超过容量，禁止永久排队");
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Pending(request);
        lock (gate) { waiter.Node = pending.AddLast(waiter); Pump(); }
        using var registration = cancellationToken.Register(() =>
        {
            lock (gate)
            {
                if (waiter.Node?.List != null) { pending.Remove(waiter.Node); waiter.Completion.TrySetCanceled(cancellationToken); Pump(); }
            }
        });
        // 取消与授予竞争时，调用者仍取得 lease，随后在 finally 释放；不会丢失已授予资源。
        return await waiter.Completion.Task.ConfigureAwait(false);
    }
    private void Pump()
    {
        while (pending.First is { } first)
        {
            var request = first.Value.Request;
            if (usedCpu + request.CpuSlots > Capacity.CpuSlots || usedMemory + request.MemoryMiB > Capacity.MemoryMiB
                || request.ExclusiveGroups.Any(exclusive.Contains)) break;
            pending.RemoveFirst(); usedCpu += request.CpuSlots; usedMemory += request.MemoryMiB;
            foreach (var group in request.ExclusiveGroups) exclusive.Add(group);
            first.Value.Completion.SetResult(new ResourceLease(this, request));
        }
    }
    internal void Release(ResourceRequest request)
    {
        lock (gate)
        {
            usedCpu -= request.CpuSlots; usedMemory -= request.MemoryMiB;
            foreach (var group in request.ExclusiveGroups) exclusive.Remove(group);
            Pump();
        }
    }
}

/// <summary>不可变授予；只有 Dispose 状态可变，重复释放安全。</summary>
public sealed class ResourceLease : IAsyncDisposable, IDisposable
{
    private ResourceBudget? owner;
    public ResourceRequest Request { get; }
    internal ResourceLease(ResourceBudget budget, ResourceRequest request) { owner = budget; Request = request; }
    public void ApplyEnvironment(ProcessStartInfo info)
    {
        info.Environment["DOTNET_PROCESSOR_COUNT"] = Request.CpuSlots.ToString(CultureInfo.InvariantCulture);
        info.Environment["RIGI_JOBS"] = Request.CpuSlots.ToString(CultureInfo.InvariantCulture);
        info.Environment["RIGI_RESOURCE_LEASE_CPU_SLOTS"] = Request.CpuSlots.ToString(CultureInfo.InvariantCulture);
        info.Environment["RIGI_RESOURCE_LEASE_MEMORY_MIB"] = Request.MemoryMiB.ToString(CultureInfo.InvariantCulture);
        info.Environment.Remove("RIGI_TEST_MEMORY_MIB");
        info.Environment["RIGI_COMPUTE_WORKERS"] = Request.ComputeWorkers.ToString(CultureInfo.InvariantCulture);
        info.Environment["RIGI_LLD_THREADS"] = Request.LldThreads.ToString(CultureInfo.InvariantCulture);
        info.Environment["DOTNET_GCHeapHardLimit"] = ((long)Request.MemoryMiB * 1024 * 1024 / 2).ToString("X", CultureInfo.InvariantCulture);
        info.Environment.Remove("DOTNET_GCHeapHardLimitPercent");
        info.Environment.Remove("COMPlus_GCHeapHardLimit");
        info.Environment.Remove("COMPlus_GCHeapHardLimitPercent");
    }
    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(Request);
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
