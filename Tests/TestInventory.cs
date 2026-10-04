using System.Text.Json;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests;

/// <summary>从执行 provider 枚举实际动作；方法组目录不冒称逐输入覆盖。</summary>
public static class TestInventory
{
    internal sealed record Case(int Index, string Label, bool Slow = false, string? Gate = null, int ComputeWorkers = 1, int MemoryMiB = 512,
        int? TimeoutMinutes = null);
    internal static IEnumerable<Case>? Cases(string name) => name switch
    {
        "BilReader" => BilReaderTests.InventoryCases,
        "Module" => ModuleTests.InventoryCases,
        "BilVmDispatch" => BilVmDispatchTests.InventoryCases,
        "BilVmStress" => BilVmStressTests.InventoryCases,
        "BilVmTask" => BilVmTaskTests.InventoryCases,
        "BilVm" => BilVmTests.InventoryCases,
        "BilVmWakeup" => BilVmWakeupTests.InventoryCases,
        "DispatchExplainer" => DispatchExplainerTests.InventoryCases,
        "EscapingSeqExpr" => EscapingSeqExprTests.InventoryCases,
        "EscapingSeqPosition" => EscapingSeqPositionTests.InventoryCases,
        "EscapingValueBlock" => EscapingValueBlockTests.InventoryCases,
        "Middleware" => MiddlewareTests.InventoryCases,
        "SeqRouteHint" => SeqRouteHintTests.InventoryCases,
        "VmFsBirthTime" => VmFsBirthTimeTests.InventoryCases,
        "VmFsDanglingDelete" => VmFsDanglingDeleteTests.InventoryCases,
        "VmFsDirOpen" => VmFsDirOpenTests.InventoryCases,
        "VmFsIdentity" => VmFsIdentityTests.InventoryCases,
        "VmFsJunctionDelete" => VmFsJunctionDeleteTests.InventoryCases,
        "VmFsNoReplace" => VmFsNoReplaceTests.InventoryCases,
        "VmFsRealpath" => VmFsRealpathTests.InventoryCases,
        "VmPrimitive" => VmPrimitiveTests.InventoryCases,
        "NativeE2E" => NativeE2ETests.InventoryCases,
        "E2e" => E2eCorpusTests.InventoryCases,
        "SemanticsFuzz" => SeededCases(Budget("RIGI_SEMFUZZ_CASES", 3000)),
        "StressFuzz" => SeededCases(Budget("RIGI_STRESSFUZZ_CASES", Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? 600 : 3000)),
        _ => LegacySuiteSpecs.Find(name) is { } spec
            ? spec.Cases.Select((c, i) => new Case(i, c.Label, MemoryMiB: spec.MemoryMiB)) : null,
    };
    internal static int Budget(string variable, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(variable), out int count) && count > 0 ? count : fallback;
    // 执行动作和清单来自同一现有 Spec，禁止反射或复制 case 数组。
    internal static ParallelSuiteRunner.SuiteSpec? Spec(string name) => name switch
    {
        "BilReader" => BilReaderTests.Spec,
        "Module" => ModuleTests.Spec,
        "BilVmDispatch" => BilVmDispatchTests.Spec,
        "BilVmStress" => BilVmStressTests.Spec,
        "BilVmTask" => BilVmTaskTests.Spec,
        "BilVm" => BilVmTests.Spec,
        "BilVmWakeup" => BilVmWakeupTests.Spec,
        "DispatchExplainer" => DispatchExplainerTests.Spec,
        "EscapingSeqExpr" => EscapingSeqExprTests.Spec,
        "EscapingSeqPosition" => EscapingSeqPositionTests.Spec,
        "EscapingValueBlock" => EscapingValueBlockTests.Spec,
        "Middleware" => MiddlewareTests.Spec,
        "SeqRouteHint" => SeqRouteHintTests.Spec,
        "VmFsBirthTime" => VmFsBirthTimeTests.Spec,
        "VmFsDanglingDelete" => VmFsDanglingDeleteTests.Spec,
        "VmFsDirOpen" => VmFsDirOpenTests.Spec,
        "VmFsIdentity" => VmFsIdentityTests.Spec,
        "VmFsJunctionDelete" => VmFsJunctionDeleteTests.Spec,
        "VmFsNoReplace" => VmFsNoReplaceTests.Spec,
        "VmFsRealpath" => VmFsRealpathTests.Spec,
        "VmPrimitive" => VmPrimitiveTests.Spec,
        "NativeE2E" => NativeE2ETests.ExecutionSpec,
        "E2e" => E2eCorpusTests.ExecutionSpec,
        _ => LegacySuiteSpecs.Find(name),
    };

    private static IEnumerable<Case> SeededCases(int count) => Enumerable.Range(0, count).Select(i => new Case(i, $"seed-case-{i}"));

    public static void Write(Stream output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("passCountMeaning", "assertions, not cases");
        writer.WriteStartArray("suites");
        foreach (var name in TestRunner.SuiteNames)
        {
            var cases = Cases(name)?.ToArray();
            writer.WriteStartObject();
            writer.WriteString("name", name); writer.WriteNumber("number", TestRunner.GetSuiteNumber(name));
            writer.WriteString("granularity", cases == null ? "suite" : "case");
            writer.WriteString("status", cases == null ? "not-enumerable" : "not-selected");
            writer.WriteString("selector", name is "NativeE2E" or "E2e" ? "name-filter" : name is "SemanticsFuzz" or "StressFuzz" ? "range-or-global-indices" : cases == null ? "suite" : "exact-label");
            writer.WriteString("dispatchGranularity", cases == null ? "suite-exit" : name is "SemanticsFuzz" or "StressFuzz" ? "sparse-seed-batch" : "case-or-small-batch");
            if (LegacyDispatcher.GroupsFor(name).Count > 0)
            { writer.WriteStartArray("groups"); foreach (var group in LegacyDispatcher.GroupsFor(name)) writer.WriteStringValue(group); writer.WriteEndArray(); }
            if (name is "LexerFuzz" or "SemanticsFuzz" or "StressFuzz")
            {
                writer.WriteNumber("seed", name == "LexerFuzz" ? 20260726 : name == "SemanticsFuzz" ? 20260804 : 20260821);
                writer.WriteNumber("caseBudget", name == "LexerFuzz" ? 6000 : cases!.Length);
                writer.WriteString("budgetEnvironment", name == "LexerFuzz" ? null : name == "SemanticsFuzz" ? "RIGI_SEMFUZZ_CASES" : "RIGI_STRESSFUZZ_CASES");
            }
            var missingClang = name == "NativeE2E" && ToolchainResolver.ResolveClang(null) == null;
            writer.WriteBoolean("toolchainAvailable", !missingClang);
            writer.WriteStartArray("cases");
            foreach (var entry in cases ?? [])
            {
                writer.WriteStartObject(); writer.WriteNumber("index", entry.Index); writer.WriteString("label", entry.Label);
                writer.WriteNumber("minimumComputeWorkers", entry.ComputeWorkers);
                writer.WriteBoolean("slow", entry.Slow); writer.WriteString("gateEnvironment", entry.Gate);
                var gate = entry.Gate != null && Environment.GetEnvironmentVariable(entry.Gate) == "1";
                writer.WriteBoolean("gateEnabled", gate);
                writer.WriteString("status", missingClang ? "skipped-toolchain" : entry.Slow && !gate ? "gate-excluded" : "not-selected");
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("pilotCases");
        foreach (var entry in CaseCatalog.All)
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Id); writer.WriteString("suite", entry.Suite);
            writer.WriteString("source", entry.Source); writer.WriteString("group", entry.Group);
            writer.WriteString("trait", entry.Trait); writer.WriteString("legacyRef", entry.LegacyRef);
            writer.WriteString("granularity", "single-input");
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }
}
