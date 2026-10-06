using System.Xml.Linq;
using RigiCompiler.Tests;

namespace RigiCompiler.TUnitTests;

/// <summary>框架退出后核对本次新鲜 TRX，覆盖失败/跳过，防止同数量替换掩盖缺失契约。</summary>
internal static class FullRunProof
{
    // 迁移前的 36 条有效框架契约逐一保留；以下身份来自迁移后真实 TRX。
    // 旧 Dispatcher/LegacyParallel 与计数器契约只改为新发现、选择和 scope 协议名称。
    private static readonly string[] Contracts =
    [
        "RigiCompiler.TUnitTests.CaseSelectionTests.ChildEnvironmentAndTempOwnership",
        "RigiCompiler.TUnitTests.CaseSelectionTests.ConcurrentCountsAreIsolated",
        "RigiCompiler.TUnitTests.CaseSelectionTests.CrossSuiteSparseExecution",
        "RigiCompiler.TUnitTests.CaseSelectionTests.HeavyAndLightDeadlines",
        "RigiCompiler.TUnitTests.CaseSelectionTests.ModuleHeavyDeadlineBoundaries",
        "RigiCompiler.TUnitTests.CaseSelectionTests.NativeConcurrencyProfiles",
        "RigiCompiler.TUnitTests.CaseSelectionTests.SparseAndHonestGranularity",
        "RigiCompiler.TUnitTests.CaseSelectionTests.SpawnedRefusesNested",
        "RigiCompiler.TUnitTests.CaseSelectionTests.UnknownSelections",
        "RigiCompiler.TUnitTests.CaseSelectionTests.UnsupportedPlatformIsTypedSkip",
        "RigiCompiler.TUnitTests.DirectoryCoverageTests.EverySuiteHasEnumerableCoverage",
        "RigiCompiler.TUnitTests.DirectoryCoverageTests.MigratedWorkersReallyOverlap",
        "RigiCompiler.TUnitTests.DirectoryCoverageTests.ScopedEvidencePreservesAssertions",
        "RigiCompiler.TUnitTests.GenericDiscoveryTests.ClosedGeneric<int>",
        "RigiCompiler.TUnitTests.LinkerThreadTests.ElfAndCoffSameSpelling",
        "RigiCompiler.TUnitTests.LinkerThreadTests.StrictSettings",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.CompilerBulkProfiles",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.CpuSetRanges",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.ExceptionReleaseAndOversize",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.MemoryAndExclusiveGroups",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.MixedWeightsAndFairness",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.PerChildEnvironment",
        "RigiCompiler.TUnitTests.ResourceBudgetTests.WaitingCancellation",
        "RigiCompiler.TUnitTests.WorkerContractTests.ActiveCancellation",
        "RigiCompiler.TUnitTests.WorkerContractTests.CompilerChildUsesGrantedBudget",
        "RigiCompiler.TUnitTests.WorkerContractTests.EarlyRootExitWithInheritedPipe",
        "RigiCompiler.TUnitTests.WorkerContractTests.ExitWithoutResult",
        "RigiCompiler.TUnitTests.WorkerContractTests.ExplicitDeadlineOverridesHeavyDefault",
        "RigiCompiler.TUnitTests.WorkerContractTests.ExplicitSkip",
        "RigiCompiler.TUnitTests.WorkerContractTests.GrandchildTreeCleanup",
        "RigiCompiler.TUnitTests.WorkerContractTests.LexerOwnFailure",
        "RigiCompiler.TUnitTests.WorkerContractTests.PendingCancellation",
        "RigiCompiler.TUnitTests.WorkerContractTests.ScopedAssertionFailure",
        "RigiCompiler.TUnitTests.WorkerContractTests.Timeout",
        "RigiCompiler.TUnitTests.WorkerContractTests.TypedSkip",
        "RigiCompiler.TUnitTests.WorkerContractTests.UnknownId",

    ];
    internal static int ContractRows => Contracts.Length;
    internal static IReadOnlyList<string> ContractIdentities => Array.AsReadOnly(Contracts);
    internal static bool IsComplete(string path, IReadOnlyDictionary<string, CaseStatus> providers, bool includeContracts = true) =>
        File.Exists(path) && IsComplete(XDocument.Load(path), providers.Count + (includeContracts ? Contracts.Length : 0), includeContracts, providers);

    private static bool IsComplete(XDocument document, int expected, bool includeContracts = true,
        IReadOnlyDictionary<string, CaseStatus>? providers = null)
    {
        var rows = document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
        var completed = rows.Select(row => (string?)row.Attribute("testId")).ToArray();
        var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest").ToArray();
        if (definitions.Any(definition => definition.Elements().Count(method => method.Name.LocalName == "TestMethod") != 1)
            || completed.Any(string.IsNullOrWhiteSpace)
            || definitions.Any(definition => string.IsNullOrWhiteSpace((string?)definition.Attribute("id")))) return false;
        var contracts = definitions.Where(definition => definition.Elements().Any(method => method.Name.LocalName == "TestMethod"
            && (string?)method.Attribute("className") != "RigiCompiler.TUnitTests.CompilerCaseTests")).ToArray();
        var identities = contracts.Select(definition => definition.Elements().Single(method => method.Name.LocalName == "TestMethod"))
            .Select(method => (string?)method.Attribute("className") + "." + (string?)method.Attribute("name")).ToArray();
        var expectedContracts = includeContracts ? Contracts : [];
        var generic = contracts.Where((_, index) => identities[index] == "RigiCompiler.TUnitTests.GenericDiscoveryTests.ClosedGeneric<int>").ToArray();
        bool complete = rows.Length == expected && completed.Distinct().Count() == expected && definitions.Length == expected
            && definitions.Select(definition => (string?)definition.Attribute("id")).Distinct().Count() == expected
            && rows.All(row => (string?)row.Attribute("outcome") is "Passed" or "NotExecuted")
            && definitions.All(definition => completed.Contains((string?)definition.Attribute("id")))
            && contracts.Length == expectedContracts.Length && identities.Order(StringComparer.Ordinal).SequenceEqual(expectedContracts)
            && generic.All(definition => rows.Single(row => (string?)row.Attribute("testId") == (string?)definition.Attribute("id"))
                .Attribute("outcome")?.Value == "Passed");
        if (!complete) return false;
        providers ??= new Dictionary<string, CaseStatus>(StringComparer.Ordinal);
        var providerDefinitions = definitions.Except(contracts).ToArray();
        var providerIds = providerDefinitions.Select(definition => (string?)definition.Attribute("name") ?? "").ToArray();
        // 先精确去重对齐稳定身份，再按定义 GUID 关联完成行；同数量冒名或 Pass/Skip 错配不能通过。
        if (providerIds.Length != providers.Count || providerIds.Distinct(StringComparer.Ordinal).Count() != providers.Count
            || !providerIds.Order(StringComparer.Ordinal).SequenceEqual(providers.Keys.Order(StringComparer.Ordinal))) return false;
        var resultsById = rows.ToDictionary(row => (string)row.Attribute("testId")!, StringComparer.Ordinal);
        return providerDefinitions.All(definition => providers[(string)definition.Attribute("name")!] is CaseStatus.Pass or CaseStatus.Skip
            && (string?)resultsById[(string)definition.Attribute("id")!].Attribute("outcome")
                == (providers[(string)definition.Attribute("name")!] == CaseStatus.Pass ? "Passed" : "NotExecuted"));
    }

    // 对完整、同数量替换和重复结果三种证据做协议回归，无需再启动一次全量测试。
    internal static void VerifyGuard()
    {
        var definitions = Contracts.Select((identity, index) => new XElement("UnitTest", new XAttribute("id", index),
            new XElement("TestMethod", new XAttribute("className", identity[..identity.LastIndexOf('.')]),
                new XAttribute("name", identity[(identity.LastIndexOf('.') + 1)..]))));
        var results = Enumerable.Range(0, Contracts.Length).Select(index => new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("outcome", "Passed")));
        var document = new XDocument(new XElement("TestRun", new XElement("TestDefinitions", definitions), new XElement("Results", results)));
        if (!IsComplete(document, Contracts.Length)) throw new InvalidOperationException("完整 TRX 身份应被接受");
        var method = document.Descendants("TestMethod").First();
        var original = method.Attribute("name")!.Value; method.SetAttributeValue("name", "被同数量未知契约替换");
        if (IsComplete(document, Contracts.Length)) throw new InvalidOperationException("同数量替换不得掩盖丢契约");
        method.SetAttributeValue("name", original);
        document.Descendants("UnitTestResult").Last().SetAttributeValue("testId", 0);
        if (IsComplete(document, Contracts.Length)) throw new InvalidOperationException("重复结果不得冒充完整执行");
        document.Descendants("UnitTestResult").Last().SetAttributeValue("testId", Contracts.Length - 1);
        var genericId = Array.IndexOf(Contracts, "RigiCompiler.TUnitTests.GenericDiscoveryTests.ClosedGeneric<int>");
        document.Descendants("UnitTestResult").ElementAt(genericId).SetAttributeValue("outcome", "NotExecuted");
        if (IsComplete(document, Contracts.Length)) throw new InvalidOperationException("闭合泛型跳过不得冒充 AOT 发现通过");
        var provider = new XDocument(new XElement("TestRun", new XElement("UnitTest", new XAttribute("id", "provider"), new XAttribute("name", "provider.skip"),
            new XElement("TestMethod", new XAttribute("className", "RigiCompiler.TUnitTests.CompilerCaseTests"), new XAttribute("name", "Run"))),
            new XElement("UnitTestResult", new XAttribute("testId", "provider"), new XAttribute("outcome", "NotExecuted"))));
        var skipProvider = new Dictionary<string, CaseStatus>(StringComparer.Ordinal) { ["provider.skip"] = CaseStatus.Skip };
        if (!IsComplete(provider, 1, false, skipProvider)) throw new InvalidOperationException("非零片合法 provider Skip 应被计入完成性");
        if (IsComplete(provider, 1, true)) throw new InvalidOperationException("片 0 不得漏掉契约");
        provider.Root!.Add(document.Descendants("UnitTest").First());
        provider.Root.Add(new XElement("UnitTestResult", new XAttribute("testId", 0), new XAttribute("outcome", "Passed")));
        if (IsComplete(provider, 2, false)) throw new InvalidOperationException("非零片不得重复执行框架契约");
        provider.Descendants("UnitTestResult").Last().Remove();
        if (IsComplete(provider, 2, false)) throw new InvalidOperationException("分片缺失结果不得通过");
        document.Descendants("UnitTestResult").ElementAt(genericId).SetAttributeValue("outcome", "Passed");
        var combined = new XDocument(document);
        combined.Root!.Add(new XElement(provider.Root!.Element("UnitTest")!), new XElement(provider.Root.Element("UnitTestResult")!));
        combined.Root.Add(new XElement("UnitTest", new XAttribute("id", "passed"), new XAttribute("name", "provider.pass"),
            new XElement("TestMethod", new XAttribute("className", "RigiCompiler.TUnitTests.CompilerCaseTests"), new XAttribute("name", "Run"))),
            new XElement("UnitTestResult", new XAttribute("testId", "passed"), new XAttribute("outcome", "Passed")));
        var providerStatuses = new Dictionary<string, CaseStatus>(StringComparer.Ordinal) { ["provider.pass"] = CaseStatus.Pass, ["provider.skip"] = CaseStatus.Skip };
        if (!IsComplete(combined, Contracts.Length + 2, true, providerStatuses)) throw new InvalidOperationException("真实 provider Pass/Skip 加完整契约应通过");
        var passedDefinition = combined.Descendants("UnitTest").Single(definition => (string?)definition.Attribute("id") == "passed");
        passedDefinition.SetAttributeValue("name", "provider.forged");
        if (IsComplete(combined, Contracts.Length + 2, true, providerStatuses)) throw new InvalidOperationException("同数量 provider 冒名不得通过");
        passedDefinition.SetAttributeValue("name", "provider.pass");
        var passedResult = combined.Descendants("UnitTestResult").Single(row => (string?)row.Attribute("testId") == "passed");
        passedResult.SetAttributeValue("outcome", "NotExecuted");
        if (IsComplete(combined, Contracts.Length + 2, true, providerStatuses)) throw new InvalidOperationException("journal Pass/TRX Skip 错配不得通过");
        passedResult.SetAttributeValue("outcome", "Passed");
        combined.Descendants("UnitTestResult").Single(row => (string?)row.Attribute("testId") == "provider").SetAttributeValue("outcome", "Passed");
        if (IsComplete(combined, Contracts.Length + 2, true, providerStatuses)) throw new InvalidOperationException("journal Skip/TRX Pass 错配不得通过");
    }
}
