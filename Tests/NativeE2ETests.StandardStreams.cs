using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;

namespace RigiCompiler.Tests
{
    public static partial class NativeE2ETests
    {
        // StandardStreams 职责；与主文件共享同一类型、字段及生命周期。

        // 标准流分片 UTF-8 单例：绕过 RunCaseModule 的文本解码/换行
        // 归一化；直接捕获双通道原始字节，Windows 也必须与 VM 一致。
        private static void RunSplitUtf8StdstreamsCase()
        {
            const string label = "标准流分片 UTF-8 与文本交错";
            var module = EmitNativeSource(
                SerializationGraphCorpus("io_stdstreams_split_utf8"), label);
            var text = BilWriter.Write(module);
            var vm = BilVm.Run(BilReader.Read(text), maxSteps: 20_000_000);
            TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                vm.Exception?.Message ?? "");
            TestHarness.Check(label + "：VM stdout 精确", vm.Stdout, "前\n中\n后\n");
            TestHarness.Check(label + "：VM stderr 精确", vm.Stderr, "中\n");
            TestHarness.CheckTrue(label + "：VM 退出码 0",
                vm.ReturnValue is VmI32 { Value: 0 });

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    OperatingSystem.IsWindows() ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0) return;

                var start = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                };
                start.Environment["RIGI_RT_MEMTRACK"] = "1";
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException(label + "：native 产物无法启动");
                process.StandardInput.Close();
                // 同时抽取两路原始管道，避免文本 ReadToEnd 把 CRLF 或
                // 非法 UTF-8 提前归一/替换；同步等待前并发读避免满管道死锁。
                using var stdoutBytes = new MemoryStream();
                using var stderrBytes = new MemoryStream();
                var stdoutRead = process.StandardOutput.BaseStream.CopyToAsync(stdoutBytes);
                var stderrRead = process.StandardError.BaseStream.CopyToAsync(stderrBytes);
                if (!process.WaitForExit(600_000)
                    || !System.Threading.Tasks.Task.WhenAll(stdoutRead, stderrRead)
                        .Wait(30_000))
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException(label + "：native 产物执行超时");
                }
                var stdout = stdoutBytes.ToArray();
                var stderr = stderrBytes.ToArray();
                // 独立字节常量包含 E4 B8 AD 0A；不能用文本换行归一化
                // 或同一编码器重建预期而掩盖 CRT 的 LF→CRLF 转写。
                TestHarness.Check(label + "：native stdout 原始字节", Convert.ToHexString(stdout),
                    "E5898D0AE4B8AD0AE5908E0A");
                TestHarness.Check(label + "：native stderr 原始字节", Convert.ToHexString(stderr),
                    "E4B8AD0A");
                TestHarness.CheckTrue(label + "：native 退出码 0", process.ExitCode == 0,
                    $"exit={process.ExitCode} stderr={Convert.ToHexString(stderr)}");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // Windows _read 的文本模式还会折叠 CRLF、把 0x1A 当 EOF；
        // 通过真实 stdin 管道喂入原字节并由 Rigi 逐字节读回至 stdout。
        private static void RunBinaryStdinCase()
        {
            const string label = "标准输入原始字节往返";
            const string source = """
                import core.io.*
                import core.collections.*
                pub func main(): i32 {
                    const input = StandardStreams.standardInput()
                    const output = StandardStreams.standardOutput()
                    const data = spanOf\<u8>(8)
                    const count = input.read(data, 0, 8)
                    output.write(data, 0, count)
                    output.flush()
                    input.dispose()
                    output.dispose()
                    return 0
                }
                """;
            var module = EmitNativeSource(source, label);
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, BilWriter.Write(module), new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    OperatingSystem.IsWindows() ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0) return;

                var start = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                start.Environment["RIGI_RT_MEMTRACK"] = "1";
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException(label + "：native 产物无法启动");
                var expected = new byte[] { 0x41, 0x0D, 0x0A, 0x42, 0x1A, 0x43 };
                using var actual = new MemoryStream();
                using var errors = new MemoryStream();
                var stdoutRead = process.StandardOutput.BaseStream.CopyToAsync(actual);
                var stderrRead = process.StandardError.BaseStream.CopyToAsync(errors);
                process.StandardInput.BaseStream.Write(expected);
                process.StandardInput.Close();
                if (!process.WaitForExit(600_000)
                    || !System.Threading.Tasks.Task.WhenAll(stdoutRead, stderrRead)
                        .Wait(30_000))
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw new InvalidOperationException(label + "：native 产物执行超时");
                }
                TestHarness.Check(label + "：stdin 原字节回显",
                    Convert.ToHexString(actual.ToArray()), Convert.ToHexString(expected));
                TestHarness.Check(label + "：stderr 无诊断",
                    Convert.ToHexString(errors.ToArray()), "");
                TestHarness.CheckTrue(label + "：退出码 0", process.ExitCode == 0,
                    $"exit={process.ExitCode}");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // richretrfix：多文件语料组对拍。语料目录组 = e2e/rigi/<name>/*.rg
        //（E2eCorpusTests Discover 同规则：按文件名序一起编译），VM 参照
        // + native 对拍与 RunCase 完全同口径（stdout 一致 + 退出码一致 +
        // RIGI_RT_MEMTRACK 零泄漏）
        private static (string Label, Action Run) MultiFileCase(string label,
            IReadOnlyList<string> files) =>
            (label, () => RunCaseFiles(label, files));

        private static IReadOnlyList<string> CorpusGroup(string name,
            [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
            Directory.GetFiles(TestCorpusPaths.Resolve("Tests/e2e/rigi/" + name, path), "*.rg")
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();

        private static void RunCaseFiles(string label, IReadOnlyList<string> files)
        {
            // 多 root 编译（stdlib 在前 + 组内全部源文件），与 E2eCorpusTests
            // RunCase 同一管线序
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.AddRange(Frontend.ParseRoots(files.Select(file =>
                new SourceInput(File.ReadAllText(file), Path.GetFileName(file))).ToArray()));
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            if (unit.Diagnostics.HasErrors)
            {
                throw new InvalidOperationException(label + "：正向语料必须零 Error；" +
                    string.Join("; ", unit.Diagnostics.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.Phase + ": " + d.Message)));
            }
            var lowered = Lowerer.Lower(unit, bodies);
            var module = BilEmitter.Emit(unit, lowered, "case_group");
            RunCaseModule(label, module);
        }

    }
}
