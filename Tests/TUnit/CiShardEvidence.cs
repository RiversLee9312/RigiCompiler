using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RigiCompiler.Tests;

namespace RigiCompiler.TUnitTests;

/// <summary>分片证据绑定本轮来源；完整预期目录独立于执行结果生成，不能由结果并集反推。</summary>
internal sealed class CiShardEvidence
{
    private readonly CiShardSelection shard;
    private readonly string directory, trx, digest, rid;
    private readonly string? sha, runId, attempt;
    private readonly string[] gates;
    private readonly int semantics, stress;
    private readonly DateTime started = DateTime.UtcNow;

    private CiShardEvidence(CiShardSelection selection, string resultDirectory, string report)
    {
        shard = selection; directory = resultDirectory; trx = Path.GetFileName(report);
        rid = Environment.GetEnvironmentVariable("RIGI_TEST_CI_RID") ?? RuntimeInformation.RuntimeIdentifier;
        sha = Environment.GetEnvironmentVariable("GITHUB_SHA"); runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(attempt)
                || rid != RuntimeInformation.RuntimeIdentifier))
            throw new ArgumentException("CI 分片缺少 SHA/run/attempt 身份或 RID 与宿主不符。");
        semantics = TestInventory.Budget("RIGI_SEMFUZZ_CASES", 3000);
        stress = TestInventory.Budget("RIGI_STRESSFUZZ_CASES", Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 600 : 3000);
        gates = TestSuiteCatalog.Names.SelectMany(name => TestInventory.Cases(name)).Where(item => item.Gate != null)
            .Select(item => item.Gate!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(gate => gate + "=" + (Environment.GetEnvironmentVariable(gate) == "1" ? "1" : "0")).ToArray();
        digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', CaseCatalog.All
            .OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => $"{item.Id}\t{item.Suite}\t{item.InputCount}"))))).ToLowerInvariant();
    }

    internal static CiShardEvidence Begin(CiShardSelection shard, string directory, string trx)
    {
        var evidence = new CiShardEvidence(shard, directory, trx);
        Directory.CreateDirectory(directory);
        using (var inventory = File.Create(Path.Combine(directory, "inventory.json")))
            TestInventory.Write(inventory, FullRunProof.ContractIdentities);
        CaseResultJournal.WriteCiMetadata = evidence.WriteIdentity;
        evidence.Write(null);
        return evidence;
    }

    internal static void AttachToJournal(CiShardSelection shard)
    {
        // MTP 重启的 child 只写相同来源的 journal；完整 inventory/最终 manifest 仍由入口父进程负责。
        var evidence = new CiShardEvidence(shard, Path.GetDirectoryName(CaseResultJournal.ResultsPath)!, "");
        CaseResultJournal.WriteCiMetadata = evidence.WriteIdentity;
    }

    internal void Write(int? exitCode)
    {
        using var output = File.Create(Path.Combine(directory, "shard-manifest.json"));
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject(); json.WriteNumber("schemaVersion", 1);
        WriteIdentity(json); json.WriteString("trxFile", trx);
        json.WriteString("startedUtc", started); json.WriteString("status", exitCode == null ? "running" : exitCode == 0 ? "complete" : "failed");
        if (exitCode is { } code) { json.WriteNumber("exitCode", code); json.WriteString("completedUtc", DateTime.UtcNow); }
        else json.WriteNull("exitCode");
        json.WriteStartObject("capacity"); json.WriteNumber("cpuSlots", ResourceBudget.Shared.Capacity.CpuSlots);
        json.WriteNumber("memoryMiB", ResourceBudget.Shared.Capacity.MemoryMiB); json.WriteEndObject();
        json.WriteStartArray("expectedProviderIds"); foreach (var item in shard.Cases) json.WriteStringValue(item.Id); json.WriteEndArray();
        json.WriteStartArray("expectedContracts");
        if (shard.Index == 0) foreach (var identity in FullRunProof.ContractIdentities) json.WriteStringValue(identity);
        json.WriteEndArray(); json.WriteEndObject();
    }

    private void WriteIdentity(Utf8JsonWriter json)
    {
        json.WriteString("sha", sha); json.WriteString("rid", rid); json.WriteString("runId", runId); json.WriteString("attempt", attempt);
        json.WriteNumber("shardIndex", shard.Index); json.WriteNumber("shardCount", shard.Count); json.WriteString("directoryDigest", digest);
        json.WriteStartObject("budgets"); json.WriteNumber("semanticsSeeds", semantics); json.WriteNumber("stressSeeds", stress);
        json.WriteNumber("lexerSeeds", 6000); json.WriteStartArray("gates"); foreach (var gate in gates) json.WriteStringValue(gate);
        json.WriteEndArray(); json.WriteEndObject();
    }
}
