using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RigiCompiler.PerfBaseline;

/// <summary>记录运行时可用预算与真实 cgroup 路径；不可读值明确标 unknown。</summary>
internal static class MachineResources
{
    internal static void Write(string path) => File.WriteAllText(path, Capture().ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    private static JsonObject Read(string path)
    {
        try { return new JsonObject { ["path"] = path, ["status"] = "available", ["value"] = File.ReadAllText(path).Trim() }; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new JsonObject { ["path"] = path, ["status"] = "unknown", ["error"] = exception.GetType().Name }; }
    }
    private static string DecodeMountPath(string path) => path.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
    internal static JsonObject Capture()
    {
        var result = new JsonObject
        {
            ["schemaVersion"] = 1, ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["os"] = RuntimeInformation.OSDescription, ["effectiveProcessorCount"] = Environment.ProcessorCount,
            ["gcTotalAvailableMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        };
        if (!OperatingSystem.IsLinux()) { result["linuxCgroups"] = "not-applicable"; return result; }
        var memberships = Read("/proc/self/cgroup");
        var mounts = Read("/proc/self/mountinfo");
        var cpu = Read("/proc/cpuinfo");
        var memory = Read("/proc/meminfo");
        result["procSelfCgroup"] = memberships;
        result["procMeminfo"] = memory;
        result["hostLogicalProcessorCount"] = cpu["status"]!.GetValue<string>() == "available"
            ? JsonValue.Create(cpu["value"]!.GetValue<string>().Split('\n').Count(l => l.StartsWith("processor\t", StringComparison.Ordinal))) : JsonValue.Create("unknown");
        var groups = new JsonArray(); result["linuxCgroups"] = groups;
        if (memberships["status"]!.GetValue<string>() != "available" || mounts["status"]!.GetValue<string>() != "available")
        { result["cgroupResolutionStatus"] = "unknown"; result["procMountinfo"] = mounts; return result; }
        var cgroupMounts = mounts["value"]!.GetValue<string>().Split('\n')
            .Select(line => (Line: line, Parts: line.Split(" - ")))
            .Where(entry => entry.Parts.Length == 2 && entry.Parts[1].Split(' ')[0] is "cgroup" or "cgroup2").ToArray();
        result["cgroupMountinfo"] = new JsonArray(cgroupMounts.Select(m => (JsonNode?)JsonValue.Create(m.Line)).ToArray());
        foreach (var line in memberships["value"]!.GetValue<string>().Split('\n'))
        {
            var membership = line.Split(':', 3);
            if (membership.Length != 3) continue;
            foreach (var mount in cgroupMounts)
            {
                var fields = mount.Parts[0].Split(' '); var filesystem = mount.Parts[1].Split(' ');
                bool v2 = filesystem[0] == "cgroup2";
                if (v2 != (membership[1].Length == 0)) continue;
                if (!v2 && !membership[1].Split(',').Intersect(filesystem[^1].Split(',')).Any()) continue;
                var root = Path.GetFullPath(DecodeMountPath(fields[3]));
                var memberPath = Path.GetFullPath(membership[2]);
                var mountPath = DecodeMountPath(fields[4]);
                if (root != "/" && memberPath != root && !memberPath.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                var relative = root == "/" ? memberPath.TrimStart('/') : memberPath[root.Length..].TrimStart('/');
                var directory = Path.Combine(mountPath, relative);
                var group = new JsonObject { ["filesystem"] = filesystem[0], ["controllers"] = membership[1],
                    ["membershipPath"] = membership[2], ["mountRootRaw"] = fields[3], ["mountPoint"] = mountPath, ["resolvedDirectory"] = directory };
                groups.Add((JsonNode)group);
                foreach (var name in v2 ? new[] { "cpu.max", "memory.max", "memory.high", "cpuset.cpus.effective" }
                    : new[] { "cpu.cfs_quota_us", "cpu.cfs_period_us", "memory.limit_in_bytes", "cpuset.cpus" })
                    group[name] = Read(Path.Combine(directory, name));
            }
        }
        result["cgroupResolutionStatus"] = groups.Count == 0 ? "unknown" : "resolved";
        return result;
    }
}
