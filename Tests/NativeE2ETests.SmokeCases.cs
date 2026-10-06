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
        // SmokeCases 职责；与主文件共享同一类型、字段及生命周期。

        // RunCase 的执行半场（BIL 模块 → VM 参照 + native 编译执行对拍），
        // 供单源（RunCase）与多文件组（RunCaseFiles）共享
        private static void RunRealpathDotDotLinux()
        {
            const string label = "Linux getRealPath 链接前点点独立预期";
            if (!OperatingSystem.IsLinux())
            {
                CaseAssertions.RecordSkip("  SKIP " + label + "：仅 Linux 实盘链接解析，当前平台未验证");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), $"rigi_realpath_dotdot_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "real", "sub"));
            var link = Path.Combine(root, "link");
            try
            {
                var marker = Path.Combine(root, "real", "marker");
                File.WriteAllBytes(marker, new byte[] { 0x72, 0x65, 0x61, 0x6c });
                Directory.CreateSymbolicLink(link, "real/sub");
                CaseAssertions.CheckTrue(label + "：词法折叠目标不存在",
                    !File.Exists(Path.Combine(root, "marker")));
                var input = root + "/link/../marker";
                // 公共 Path.of 不做词法正规化；两宿主均须按原始路径访问磁盘。
                var source = $$"""
                    import core.fs.*
                    import core.io.*
                    import core.collections.*
                    pub func main(): i32 {
                        const resolved = getRealPath(Path.of("{{input}}"))
                        Console.println(resolved.text)
                        const reader = File.openRead(resolved)
                        const bytes = spanOf\<u8>(4)
                        reader.readExactly(bytes)
                        reader.dispose()
                        if (((((bytes[0] if? (0 as u8)) == (114 as u8))
                            and ((bytes[1] if? (0 as u8)) == (101 as u8)))
                            and ((bytes[2] if? (0 as u8)) == (97 as u8)))
                            and ((bytes[3] if? (0 as u8)) == (108 as u8))) {
                            Console.println("real-marker-ok")
                            return 0
                        }
                        return 1
                    }
                    """;
                RunCaseModule(label, EmitNativeSource(source, label),
                    expectedStdout: marker + "\nreal-marker-ok\n", expectedExitCode: 0,
                    assertVmExpected: true);
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(root, recursive: true);
            }
        }

        private static void RunJsonMapReservedKeySmoke()
        {
            const string label = "JSON Map 保留形普通字符串键独立烟测";
            RunCaseModule(label,
                EmitNativeSource(SerializationGraphCorpus("json_map_reserved_key_smoke"), label),
                expectedStdout: "json-map-reserved-key-smoke-ok\n",
                expectedExitCode: 0, assertVmExpected: true);
        }

        private static void RunFileStreamSmoke()
        {
            const string label = "fs 文件流对拍";
            RunCaseModule(label,
                EmitNativeSource(SerializationGraphCorpus("fs_stream_smoke"), label),
                expectedStdout: "fs-stream-smoke-ok\n", expectedExitCode: 0);
        }

        // §4.2.4：固定 stdout/退出码在 VM 与 native 各自独立断言，
        // 避免两宿主同错时仅靠对拍放行。
        private static void RunEmptyListSortSmoke()
        {
            const string label = "空List排序枚举器失效烟测";
            RunCaseModule(label,
                EmitNativeSource(SerializationGraphCorpus("collalgo_sort_empty"), label),
                expectedStdout: "empty-sort-move-ise\nempty-sort-current-ise\n",
                expectedExitCode: 0, assertVmExpected: true);
        }

    }
}
