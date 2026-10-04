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
        // BilExecution 职责；与主文件共享同一类型、字段及生命周期。
        // 语言级异常（消息含 keyword）；native 编译链接成功、运行退出码
        // 1、stderr 含 nativeNeedle（缺省同 keyword）、stdout 一致
        private static void RunBilFailCase(string label, string bilText, string keyword,
            string? nativeNeedle = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var vm = BilVm.Run(BilReader.Read(bilText));
                TestHarness.CheckTrue(label + "：VM 有异常", vm.Exception != null);
                TestHarness.CheckTrue(label + "：VM 消息含关键字",
                    vm.Exception != null && vm.Exception.Message.Contains(keyword),
                    vm.Exception?.Message ?? "");

                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, bilText, new UTF8Encoding(false));
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
                TestHarness.CheckTrue(label + "：native 退出码 1", runExit == 1,
                    $"exit={runExit}");
                TestHarness.CheckTrue(label + "：native stderr 含关键字",
                    nativeErr.Contains(nativeNeedle ?? keyword), nativeErr);
                TestHarness.Check(label + "：stdout 一致",
                    NormalizeNewlines(nativeOut), NormalizeNewlines(vm.Stdout));
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // typeid 数组（泛型位置包 TArgs 的承载形态 .array<.typeid<.any>>）：
        // 元素 = 8B 内联 sheet 指针（sheet FlagInlineValue/typeSize=8），非 16B
        // 胖槽——回归 stride 双口径（发射 16B/分配 8B）导致的堆越界（linux glibc
        // abort）。前端无 TArgs[i] 语法，BIL 级直驱 get.array + nullable 解包 +
        // 间接 is 观测元素值正确性。
        private static void RunTypeIdArrayGetCase()
        {
            RunBilCase("typeid 数组元素读取（BIL 级）",
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"tidarr\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_0 = i32 0,\n" +
                "    R_1 = i32 1,\n" +
                "    R_42 = i32 42,\n" +
                "    R_V = i64 7\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "\n" +
                "    .vars {\n" +
                "        .breakid .b0,\n" +
                "        .typeid .t0,\n" +
                "        .typeid .t1,\n" +
                "        .array<.typeid<.any>> .t2,\n" +
                "        .i32 .t3,\n" +
                "        .nullable<.typeid<.any>> .t4,\n" +
                "        .typeid<.any> .t5,\n" +
                "        .i64 .t6,\n" +
                "        .bool .t7,\n" +
                "        .i32 .t8,\n" +
                "        .i32 .t9\n" +
                "    }\n" +
                "\n" +
                "    .block entry entrypoint {\n" +
                "        getid.type type(.i32) $.t0\n" +
                "        getid.type type(.i64) $.t1\n" +
                "        new type(.array<.typeid<.any>>) $.t2 [$.t0, $.t1]\n" +
                "        load res(R_1) $.t3\n" +
                "        get.array $.t2 $.t3 $.t4\n" +
                "        cast $.t4 $.t5 type(.typeid<.any>)\n" +
                "        load res(R_V) $.t6\n" +
                "        type.is.indirect $.t6 $.t5 $.t7\n" +
                "        if $.t7 blk(if0-then) none $.b0\n" +
                "        load res(R_0) $.t8\n" +
                "        ret $.t8\n" +
                "    }\n" +
                "\n" +
                "    .block if0-then {\n" +
                "        load res(R_42) $.t9\n" +
                "        ret $.t9\n" +
                "    }\n" +
                "}\n");
        }

        // BIL 级对拍（前端尚未降级的合法内建形态）：手写 BIL 直接驱 VM 与
        // native，比对口径与 RunCase 相同
        private static void RunBilCase(string label, string bilText,
            IReadOnlyDictionary<string, string>? env = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                // VM 侧（行为参考实现）
                var vm = BilVm.Run(BilReader.Read(bilText));
                TestHarness.CheckTrue(label + "：VM 无异常", vm.Exception == null,
                    vm.Exception?.Message ?? "");
                var expectedExit = vm.ReturnValue is VmI32 value ? value.Value : 0;

                // native 侧：CLI 编译 → 进程执行
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, bilText, new UTF8Encoding(false));
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
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // L6：非 rigi_rt 库 FFI native-only 驱动——Rigi 源走全管线出 BIL；
        // cSource 用工具链 clang -c 现场编成目标文件，native --file --out
        // --link 一次编译链接；执行产物断言 stdout（行尾归一）与退出码
        private static void RunNativeOnlyCase(string label, string source,
            string cSource, string expectedStdout, int expectedExit,
            IReadOnlyDictionary<string, string>? env = null,
            bool useFixtureRoot = false)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);
                var bilPath = Path.Combine(dir, "case.bil");
                File.WriteAllText(bilPath, text, new UTF8Encoding(false));

                // 现场出最小外部库（复用工具链解析，无外部依赖）
                var clang = ToolchainResolver.ResolveClang(null);
                TestHarness.CheckTrue(label + "：clang 可用", clang != null);
                if (clang == null)
                {
                    return;
                }
                var cPath = Path.Combine(dir, "ffi.c");
                var objPath = Path.Combine(dir, "ffi.o");
                File.WriteAllText(cPath, cSource, new UTF8Encoding(false));
                var cExit = ExternalProcess.Run(clang,
                    new[] { cPath, "-c", "-o", objPath }, out _, out var cErr);
                TestHarness.CheckTrue(label + "：C 源编译成功", cExit == 0, cErr);
                if (cExit != 0)
                {
                    return;
                }

                var exePath = Path.Combine(dir,
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "case.exe" : "case");
                var compiled = RunNative("native", "--file", bilPath,
                    "--out", exePath, "--link", objPath);
                TestHarness.CheckTrue(label + "：native 编译链接成功", compiled.Code == 0,
                    compiled.Err);
                if (compiled.Code != 0)
                {
                    return;
                }
                var runEnv = new Dictionary<string, string>(MemtrackEnv);
                if (env != null)
                    foreach (var entry in env) runEnv[entry.Key] = entry.Value;
                if (useFixtureRoot)
                {
                    var fixture = Path.Combine(dir, "fs_probe_root");
                    Directory.CreateDirectory(fixture);
                    runEnv["RIGI_FS_PROBE_ROOT"] = fixture;
                }
                var runExit = ExternalProcess.Run(exePath, Array.Empty<string>(),
                    out var nativeOut, out var nativeErr, environment: runEnv);
                TestHarness.Check(label + "：stdout 符合预期",
                    NormalizeNewlines(nativeOut), expectedStdout);
                TestHarness.CheckTrue(label + "：退出码符合预期",
                    runExit == expectedExit, $"native={runExit} 期望={expectedExit} stderr={nativeErr}");
                TestHarness.Check(label + "：无资源泄漏或运行时诊断", NormalizeNewlines(nativeErr), "");
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

    }
}
