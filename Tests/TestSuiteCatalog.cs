namespace RigiCompiler.Tests;

/// <summary>纯 provider 数据；测试发现与执行不再经过旧套件驱动。</summary>
internal sealed class TestSuiteData(string suiteName, IReadOnlyList<(string Label, Action Run)> cases,
    string? sectionTitle = null, int memoryMiB = 512)
{
    internal string SuiteName { get; } = suiteName;
    internal IReadOnlyList<(string Label, Action Run)> Cases { get; } = cases;
    internal string? SectionTitle { get; } = sectionTitle;
    internal int MemoryMiB { get; } = memoryMiB;
}

/// <summary>编号仅服务既有 CLI 兼容；不持有 RunAll 或整套执行委托。</summary>
public static class TestSuiteCatalog
{
    public static IReadOnlyList<string> Names { get; } =
    ["Literal",
        "TypeReference",
        "VariableDeclaration",
        "Expression",
        "GenericParsing",
        "GenericParameters",
        "ParameterList",
        "Lambda",
        "IfExpression",
        "SwitchExpression",
        "TypeOf",
        "CodeBlock",
        "Loop",
        "TryCatchFinally",
        "SeqBlock",
        "Throw",
        "CoroutineOps",
        "TypeDeclaration",
        "PropertyAccessor",
        "Import",
        "Namespace",
        "TokenDisposition",
        "ASTIntegrityValidator",
        "LexerFuzz",
        "Logger",
        "AstJsonlSerializer",
        "CommandLineParser",
        "Path",
        "ArgumentList",
        "MultilineString",
        "Diagnostics",
        "SymbolGraph",
        "CanonicalSymbolPrinter",
        "BilWriter",
        "BilVerifier",
        "DeclarationCollector",
        "DeclarationResolver",
        "Binder",
        "StdlibSources",
        "BilEmitter",
        "Lowerer",
        "SmartCast",
        "SemanticsFuzz",
        "StressFuzz",
        "DispatchExplainer",
        "BilVm",
        "BilVmDispatch",
        "BilVmWakeup",
        "VmPrimitive",
        "BilReader",
        "BilVmStress",
        "EscapingSeqExpr",
        "EscapingValueBlock",
        "SeqRouteHint",
        "EscapingSeqPosition",
        "E2e",
        "Middleware",
        "NativeE2E",
        "BilVmTask",
        "NativeE2EArgs",
        "VmFsNoReplace",
        "VmFsDanglingDelete",
        "VmFsJunctionDelete",
        "VmFsIdentity",
        "VmFsBirthTime",
        "VmFsDirOpen",
        "VmFsRealpath",
        "PerformanceMetrics",
        "CompilerParallel",
        "Module"];
    public static int Count => Names.Count;
    public static int GetNumber(string name) => Names.ToList().IndexOf(name) + 1;
    public static void PrintMenu()
    {
        Console.WriteLine("TUnit 测试 provider（test --run <编号...>，兼容选择转交框架）：");
        for (int index = 0; index < Names.Count; index++) Console.WriteLine($"{index + 1}. {Names[index]}");
    }
}

internal static class WorkerEnvironment
{
    internal static bool IsWorker { get; set; }
}
