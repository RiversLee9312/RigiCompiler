using System.Diagnostics;
using System.Text.Json;

namespace RigiCompiler;

/// <summary>显式启用的旁路遥测；不写控制台，不使用反射序列化。</summary>
public static class PerformanceMetrics
{
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable("RIGI_PROFILE_DIR");
    private static readonly object WriteLock = new();
    private static readonly AsyncLocal<Scope?> Current = new();
    private static readonly string FileName = $"metrics-{Environment.ProcessId}-{Guid.NewGuid():N}.jsonl";
    public static bool Enabled => !string.IsNullOrWhiteSpace(DirectoryPath);

    public static Scope? Begin(string phase, string? detail = null) =>
        Enabled ? new Scope(phase, detail) : null;

    public static T Measure<T>(string phase, Func<T> action, string? detail = null, Func<bool>? hasErrors = null, Func<T, bool>? isFailure = null)
    {
        using var scope = Begin(phase, detail);
        try
        {
            var result = action();
            if (scope != null && (hasErrors?.Invoke() == true || isFailure?.Invoke(result) == true)) scope.ExitCode(1);
            return result;
        }
        catch (Exception exception) { scope?.Fail(exception); throw; }
    }

    public static void Measure(string phase, Action action, string? detail = null, Func<bool>? hasErrors = null) =>
        Measure(phase, () => { action(); return 0; }, detail, hasErrors);

    public static void Cache(string cache, string path, bool rebuilt) =>
        Event("cache", cache, rebuilt ? "miss" : "hit", path);

    public static void Event(string kind, string phase, string status, string? detail = null,
        int? childProcessId = null)
    {
        if (!Enabled) return;
        Write(writer =>
        {
            WriteIdentity(writer, kind, phase, Current.Value?.Id, status, detail);
            if (childProcessId.HasValue) writer.WriteNumber("childProcessId", childProcessId.Value);
        });
    }

    // 只在阶段 join 后写一条事件；峰值是实际进入 action 的 worker 数。
    public static void CompilerWorkers(string phase, int count, int slots, bool acquired,
        int peak, int started, int completed, string status)
    {
        if (!Enabled) return;
        Write(writer =>
        {
            WriteIdentity(writer, "compiler-workers", phase, Current.Value?.Id, status, null);
            writer.WriteNumber("jobCount", count);
            writer.WriteNumber("workerLimit", slots);
            writer.WriteBoolean("leaseAcquired", acquired);
            writer.WriteNumber("grantedCpuSlots", acquired ? slots : 0);
            writer.WriteNumber("startedJobCount", started);
            writer.WriteNumber("completedJobCount", completed);
            writer.WriteNumber("activeWorkerPeak", peak);
        });
    }

    public static void ChildCompleted(Process child, string executable, double wallMilliseconds,
        double? observedCpu, long? observedPeakRss)
    {
        if (!Enabled) return;
        // Linux 进程退出后 /proc 可能消失；明确区分最终计数与最后一次存活采样。
        double? cpu = null; long? peak = null;
        try { cpu = child.TotalProcessorTime.TotalMilliseconds; peak = child.PeakWorkingSet64; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Write(writer =>
        {
            WriteIdentity(writer, "child-process", "external-process", Current.Value?.Id,
                child.ExitCode == 0 ? "completed" : "failed", executable);
            writer.WriteNumber("childProcessId", child.Id);
            writer.WriteString("scope", "child-process-lifetime");
            writer.WriteNumber("exitCode", child.ExitCode);
            writer.WriteNumber("wallMilliseconds", wallMilliseconds);
            if (cpu.HasValue) writer.WriteNumber("childCpuMilliseconds", cpu.Value);
            if (peak.HasValue) writer.WriteNumber("childLifetimePeakRssBytes", peak.Value);
            if (observedCpu.HasValue) writer.WriteNumber("lastObservedChildCpuMilliseconds", observedCpu.Value);
            if (observedPeakRss.HasValue) writer.WriteNumber("observedChildLifetimePeakRssBytes", observedPeakRss.Value);
            writer.WriteNumber("childSamplingIntervalMilliseconds", 100);
        });
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string kind, string phase,
        string? parent, string status, string? detail)
    {
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("timestampUtc", DateTimeOffset.UtcNow);
        writer.WriteString("kind", kind);
        writer.WriteNumber("processId", Environment.ProcessId);
        writer.WriteString("phase", phase);
        writer.WriteString("parentPhaseId", parent);
        writer.WriteString("status", status);
        writer.WriteString("detail", detail);
    }

    private static void Write(Action<Utf8JsonWriter> emit)
    {
        // 遥测磁盘故障不改变编译结果或 stdout/stderr 的既有契约。
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(DirectoryPath!);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject(); emit(writer); writer.WriteEndObject();
                }
                using var file = new FileStream(Path.Combine(DirectoryPath!, FileName), FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Position = 0; stream.CopyTo(file); file.WriteByte((byte)'\n');
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    public sealed class Scope : IDisposable
    {
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        private readonly bool overlappingCounters = CompilerJobs.InWorker;
        private readonly Scope? parent;
        private readonly string phase;
        private readonly string? detail;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly long allocated = GC.GetTotalAllocatedBytes(false);
        private readonly Process process = Process.GetCurrentProcess();
        private readonly TimeSpan cpu;
        private string status = "completed";
        private string? error;
        private bool disposed;
        internal Scope(string phase, string? detail)
        {
            this.phase = phase; this.detail = detail;
            parent = Current.Value; Current.Value = this;
            cpu = process.TotalProcessorTime;
        }
        public void Fail(Exception exception) { status = "failed"; error = exception.GetType().Name + ": " + exception.Message; }
        public void ExitCode(int exitCode) { if (exitCode != 0) status = "failed"; }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocationDelta = GC.GetTotalAllocatedBytes(false) - allocated;
            process.Refresh();
            var cpuDelta = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            var rss = process.WorkingSet64;
            // PeakWorkingSet64 是本进程生命周期高水位，不是当前 phase 的采样峰。
            var lifetimePeak = process.PeakWorkingSet64;
            Current.Value = parent;
            Write(writer =>
            {
                WriteIdentity(writer, "scope", phase, parent?.Id, status, detail);
                writer.WriteString("scopeId", Id);
                writer.WriteString("scope", "current-process");
                writer.WriteBoolean("overlappingProcessCounters", overlappingCounters);
                writer.WriteNumber("wallMilliseconds", wall);
                writer.WriteNumber("cpuMilliseconds", cpuDelta);
                writer.WriteNumber("managedAllocatedBytes", allocationDelta);
                writer.WriteNumber("endRssBytes", rss);
                writer.WriteNumber("processLifetimePeakRssBytes", lifetimePeak);
                writer.WriteString("error", error);
            });
            process.Dispose();
        }
    }
}
