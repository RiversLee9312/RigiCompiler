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
        // Execution 职责；与主文件共享同一类型、字段及生命周期。

        private static void RunCaseModule(string label, BilModule module,
            IReadOnlyDictionary<string, string>? env = null, long maxSteps = 20_000_000,
            string? expectedStdout = null, int? expectedExitCode = null,
            bool assertVmExpected = false)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var text = BilWriter.Write(module);

                // VM 侧（行为参考实现）
                // 对拍也必须有执行预算；失败 Task 留下等待循环时应报告
                // 测试失败，而不能让整个并行套件无限等待。
                var vm = BilVm.Run(BilReader.Read(text), maxSteps: maxSteps);
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;
                if (assertVmExpected)
                {
                    if (expectedStdout != null)
                        TestHarness.Check(label + "：VM stdout 独立预期",
                            NormalizeNewlines(vm.Stdout), expectedStdout);
                    if (expectedExitCode != null)
                        TestHarness.CheckTrue(label + "：VM 退出码独立预期",
                            vm.ReturnValue is VmI32 { Value: var exit }
                                && exit == expectedExitCode.Value,
                            $"vm={expectedExit} expected={expectedExitCode.Value}");
                }

                // native 侧：CLI 编译 → 进程执行
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr, environment: env ?? MemtrackEnv,
                    // closeStdin = true：产物 stdin 一律为「启动后立即关闭
                    // 的管道」——确定性 EOF（B2-4b2 标准输入对拍），与其
                    // 继承测试宿主可能无效/未知的句柄，不如统一口径；
                    // 不读 stdin 的既有用例不受影响
                    closeStdin: true);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit} stderr={nativeErr}");
                // 烟测另以固定常量钉死 native 结果，不能仅凭 VM 对拍同错放行。
                if (expectedStdout != null)
                    TestHarness.Check(label + "：native stdout 独立预期",
                        NormalizeNewlines(nativeOut), expectedStdout);
                if (expectedExitCode != null)
                    TestHarness.CheckTrue(label + "：native 退出码独立预期",
                        runExit == expectedExitCode.Value,
                        $"native={runExit} expected={expectedExitCode.Value} stderr={nativeErr}");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // MW12b §25.2：流程同 RunCase（VM 参照对拍 stdout + 退出码一致，
        // VM 无异常），额外断言 native stderr 含/不含 needle；VM 半场 B2
        // 已接事件通道，VM stderr 同文本一并断言（needle 形态对事件条数/
        // 排序不敏感——多事件场景两宿主顺序天然不同）
        private static void RunNativeErrCase(string label, string source, string needle,
            bool needlePresent)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);

                // VM 侧（行为参考实现）：stdout/退出码即参照，stderr 经
                // Run 收尾 drain 的事件通道产生（与 native 同文本）
                var vm = BilVm.Run(BilReader.Read(text), maxSteps: 20_000_000);
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;
                TestHarness.CheckTrue(
                    label + (needlePresent ? "：VM stderr 含关键字" : "：VM stderr 无事件"),
                    vm.Stderr.Contains(needle) == needlePresent, vm.Stderr);

                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));
                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath, "--out", exePath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr, environment: MemtrackEnv);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
                TestHarness.CheckTrue(label + "：退出码一致",
                    runExit == expectedExit, $"native={runExit} vm={expectedExit} stderr={nativeErr}");
                TestHarness.CheckTrue(
                    label + (needlePresent ? "：native stderr 含关键字" : "：native stderr 无事件"),
                    nativeErr.Contains(needle) == needlePresent, nativeErr);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n");

    }
}
