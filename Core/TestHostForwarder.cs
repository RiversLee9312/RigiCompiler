using System.Diagnostics;

namespace RigiCompiler;

/// <summary>编译器只转发测试请求；发现、断言与执行均属于独立 TUnit 宿主。</summary>
internal static class TestHostForwarder
{
    public static int Run(CommandLineParseResult result)
    {
        var configured = Environment.GetEnvironmentVariable("RIGI_TEST_HOST");
        if (!string.IsNullOrWhiteSpace(configured) && !File.Exists(configured))
        { Console.Error.WriteLine("RIGI_TEST_HOST 指定的测试宿主不存在：" + configured); return 2; }
        var name = OperatingSystem.IsWindows() ? "RigiCompiler.Tests.exe" : "RigiCompiler.Tests";
        var candidates = new[] {
            configured,
            Path.Combine(AppContext.BaseDirectory, name),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../publish-tests", name)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../publish-tests", new DirectoryInfo(AppContext.BaseDirectory).Name, name)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Tests/TUnit/bin/Debug/net10.0/RigiCompiler.Tests.dll")),
        };
        var artifact = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (artifact == null)
        {
            Console.Error.WriteLine("未找到 TUnit 测试宿主；请发布 Tests/TUnit/RigiCompiler.Tests.csproj 并设置 RIGI_TEST_HOST。");
            return 2;
        }
        var info = new ProcessStartInfo(artifact) { UseShellExecute = false };
        if (artifact.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        { info.FileName = "dotnet"; info.ArgumentList.Add(artifact); }
        info.ArgumentList.Add("--compat");
        foreach (var option in new[] { "--inventory", "--all", "--run", "--suite-args", "--spawned", "--worker", "--case-id", "--result-file", "--verbose", "--log-to" })
        {
            if (!result.Has(option)) continue;
            info.ArgumentList.Add(option);
            foreach (var argument in result.Get(option) ?? []) info.ArgumentList.Add(argument);
        }
        // CoreCLR 用程序集，AOT 用实际编译器进程；该变量不再定位测试 worker。
        info.Environment["RIGI_TEST_RIGIC"] = CompilerArtifact();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 TUnit 测试宿主");
        process.WaitForExit();
        return process.ExitCode;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "NativeAOT 使用当前编译器进程路径")]
    private static string CompilerArtifact() => typeof(Frontend).Assembly.Location is { Length: > 0 } assembly
        ? assembly : Environment.ProcessPath ?? throw new InvalidOperationException("无法定位编译器产物");
}
