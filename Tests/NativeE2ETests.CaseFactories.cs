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
        // CaseFactories 职责；与主文件共享同一类型、字段及生命周期。

        private static (string Label, Action Run) Case(string label, string source,
            long maxSteps = 20_000_000) =>
            RegisterCase(label, source, () => RunCase(label, source, maxSteps: maxSteps));

        internal static void RunPilotHelloBil()
        {
            // 只编译一个小 BIL；沿用 VM/native 单 case 对拍驱动，默认 O2。
            const string bil = """
                BIL "1.1"
                Metadata {
                    module = string "pilot"
                }
                Resources {
                    hello = string "Hello, world!\n",
                    zero = i32 0
                }
                LocalSymbols {
                    .method $main()@.i32 pub entrypoint
                }
                ExternalSymbols {
                    .method $print(text:.string)@.void pub native symbol("print") lib("rigi_rt")
                }
                fn($main()@.i32) {
                    .args {
                        .return = .i32
                    }
                    .vars {
                        .string text,
                        .i32 result
                    }
                    .block entry entrypoint {
                        load res(hello) $text
                        invoke.noret fn($print(text:.string)@.void) [$text]
                        load res(zero) $result
                        ret $result
                    }
                }
                """;
            RunBilCase("hello world 小 BIL", bil);
        }

        private static (string Label, Action Run) BilCase(string label, string bil) =>
            RegisterCase(label, bil, () => RunBilCase(label, bil));

        // L6：非 rigi_rt 库 FFI 的 native-only 用例（VM 无对应 hook，§22.5
        // 表外拒绝是定稿行为，不做 VM 对拍）——cSource 现场 clang -c 出
        // 目标文件，经 native --link 链入，断言 stdout/退出码字面量
        private static (string Label, Action Run) NativeOnlyCase(string label,
            string source, string cSource, string expectedStdout, int expectedExit,
            IReadOnlyDictionary<string, string>? env = null,
            bool useFixtureRoot = false) =>
            RegisterCase(label, source, () =>
            {
                if (useFixtureRoot && !OperatingSystem.IsLinux())
                {
                    CaseAssertions.RecordSkip("  SKIP " + label + "：仅 Linux 原生文件系统机制探针");
                    return;
                }
                RunNativeOnlyCase(label, source, cSource,
                    expectedStdout, expectedExit, env, useFixtureRoot);
            });

        private static (string Label, Action Run) FailCase(string label, string source, string needle) =>
            RegisterCase(label, source, () => RunFailCase(label, source, needle, null));

        // MW9b-G：native stderr 关键字可与 VM 消息关键字不同（reporter
        // 新格式「{类型全名}: {message}」全名前缀 VM 消息没有）
        private static (string Label, Action Run) FailCase(string label, string source,
            string needle, string nativeNeedle) =>
            RegisterCase(label, source, () => RunFailCase(label, source, needle, nativeNeedle));

        // MW12b §25.2：native stderr 断言形态——VM 参照对拍 stdout + 退出
        // 码一致（VM 侧暂无 undisposed 事件通道，stdout 不受影响），额外
        // 断言 native stderr 含/不含 needle
        private static (string Label, Action Run) NativeErrCase(string label, string source,
            string needle, bool needlePresent = true) =>
            RegisterCase(label, source, () => RunNativeErrCase(label, source, needle, needlePresent));

        // MW12c：per-case env——env 与 MemtrackEnv 合并（MEMTRACK 恒在，
        // 泄漏即 exit 1 的判定口径不可关），per-case 同名键覆盖。
        // maxSteps 透传给两宿主执行预算（并发长跑用例需放宽，与 Case 同口径）
        private static (string Label, Action Run) EnvCase(string label, string source,
            IReadOnlyDictionary<string, string> env, long maxSteps = 20_000_000)
        {
            var merged = new Dictionary<string, string>(MemtrackEnv);
            foreach (var pair in env)
            {
                merged[pair.Key] = pair.Value;
            }
            return RegisterCase(label, source, () => RunCase(label, source, merged, maxSteps));
        }

    }
}
