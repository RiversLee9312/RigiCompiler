using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace RigiCompiler.Tests;

/// <summary>编译器与测试宿主分别定位，防止 AOT 测试进程冒充被测 CLI。</summary>
internal static class TestArtifacts
{
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "AOT 下必须显式指定编译器产物")]
    internal static string Compiler => Environment.GetEnvironmentVariable("RIGI_TEST_RIGIC") is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : typeof(Frontend).Assembly.Location is { Length: > 0 } assembly ? assembly
        : throw new InvalidOperationException("AOT 测试必须设置 RIGI_TEST_RIGIC 为实际 rigic 编译器");

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "AOT worker 使用同一测试宿主的进程路径")]
    internal static string Host => typeof(TestArtifacts).Assembly.Location is { Length: > 0 } assembly
        ? assembly : Environment.ProcessPath ?? throw new InvalidOperationException("无法定位测试宿主");

    internal static (string FileName, string[] Prefix) CompilerCommand => Compiler.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        ? ("dotnet", [Compiler]) : (Compiler, []);
}
