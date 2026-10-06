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
    /// <summary>
    /// native 端到端对拍套件（MIDDLEWARE_ARCHITECTURE §10 第 2 条）：同一 BIL
    /// 在 BIL VM（行为参考实现）与 native 产物上的可观察行为一致
    /// （stdout / 退出码）。用例走编译器真实产物（EmitBilUnit 全管线），
    /// 杜绝手编样例漂移。未找到 clang 工具链时整套 skip（不计失败）——
    /// CI 双平台 runner 预装 clang/lld 必跑。
    /// </summary>
    public static partial class NativeE2ETests
    {
        internal enum NativeE2eRunKind
        {
            List,
            Range,
            NameFilter,
        }

        // suite-args 选择语义（纯函数，仅保留兼容输入的纯解析契约）：
        //   list                    —— 列出索引+Label；
        //   首参为整数              —— 数字 from/to 区间（原语义原样转发，
        //                              含不完整区间的用法报错）；
        //   其余非空参数序列        —— 按 Label 子串过滤；
        //   空参数                  —— Range 转发（由兼容入口打印用法并报
        //                              非 0，与历史行为一致）。
        internal static (NativeE2eRunKind Kind, int From, int To,
            IReadOnlyList<string> Filters) ParseRunArgs(IReadOnlyList<string> args)
        {
            if (args.Count == 1 && args[0] == "list")
            {
                return (NativeE2eRunKind.List, 0, 0, Array.Empty<string>());
            }
            if (args.Count == 0)
            {
                // 空参数保持历史行为：交回兼容入口打印用法并报非 0。
                return (NativeE2eRunKind.Range, 0, 0, Array.Empty<string>());
            }
            if (args.Count > 0 && int.TryParse(args[0], out var from))
            {
                var to = args.Count > 1 && int.TryParse(args[1], out var parsed) ? parsed : from;
                return (NativeE2eRunKind.Range, from, to, Array.Empty<string>());
            }
            return (NativeE2eRunKind.NameFilter, 0, 0, args);
        }


        internal static IEnumerable<TestInventory.Case> InventoryCases =>
            Cases.Concat(SlowCases).Append((Label: "native.hello-world-bil", Run: (Action)RunPilotHelloBil)).Select((entry, index) => new TestInventory.Case(index, entry.Label,
                SlowCases.Any(c => c.Label == entry.Label), "RIGI_NATIVE_E2E_SLOW", RequiresConcurrentCompute(entry.Label) ? 4 : 1,
                // JSON 写侧整程序冷编译的 LLVM O2/对象码生成占用超过 2GiB；
                // 单核已需二十余分钟，共享预算满载时须有独立的有限期限余量。
                MemoryMiB: entry.Label == "JSON 写侧对拍" ? 4096 : 512,
                TimeoutMinutes: entry.Label == "JSON 写侧对拍" ? 75 : entry.Label == "native.hello-world-bil" ? 9 : null));

        private static readonly HashSet<string> ConcurrentSourceLabels = new(StringComparer.Ordinal);
        private static (string Label, Action Run) RegisterCase(string label, string source, Action run)
        {
            // 注册时直接检查实际 Rigi/BIL source；英文名或无提示标签也保留真实 Compute 并发。
            if (source.Contains("ComputeExecutor", StringComparison.Ordinal) || source.Contains("rigi_worker", StringComparison.Ordinal))
                ConcurrentSourceLabels.Add(label);
            return (label, run);
        }

        // 将既有真实标签映射成执行元数据；覆盖 MQ/生产者/跨执行器等非“并发”命名。
        internal static bool RequiresConcurrentCompute(string label) => ConcurrentSourceLabels.Contains(label) || new[] {
            "并发", "concurrent", "coroutine", "async", "Worker", "Compute", "唤醒", "MQ", "消息", "生产者", "跨执行器", "广播", "协程", "Executor", "Atomic", "多线程"
        }.Any(marker => label.Contains(marker, StringComparison.OrdinalIgnoreCase));

        internal static TestSuiteData ExecutionSpec => new("NativeE2E", Cases.Concat(SlowCases).Append((Label: "native.hello-world-bil", Run: (Action)RunPilotHelloBil)).ToArray(),
            sectionTitle: "native 对拍（VM vs 原生可执行）");

        // rigi_rt 的 ArtifactCache 按内容身份持有跨进程文件锁，并验证请求副本；
        // 独立 worker 冷启动仍单次构建，不再需要旧整套驱动的 BeforeSpawn 预热。

        // 注：声明须在 Cases 之前（静态初始化按文本序，EnvCase 合并要用）
        private static readonly Dictionary<string, string> MemtrackEnv =
            new() { ["RIGI_RT_MEMTRACK"] = "1" };

        // 手动慢组仍有正式 native 注册；按精确标签显式执行不依赖开关。
        private static readonly (string Label, Action Run)[] SlowCases =
        {
            // Windows 单例在 230 秒内仅完成 VM，native 编译链接尚无结果；
            // 保留精确标签手动重测，不纳入默认 NativeE2E。
            ("JSON Map 保留形普通字符串键独立烟测", RunJsonMapReservedKeySmoke),
            Case("JSON 嵌套 Map wire 扩展慢例对拍",
                SerializationGraphCorpus("json_map_wire_extended")),
            Case("fs 文件流对拍（完整慢例）", SerializationGraphCorpus("fs_filestream")),
            Case("fs 文件流对拍（核心慢例）", SerializationGraphCorpus("fs_stream_core")),
            // 空 List 排序烟测在 Windows 独立单例编译链接超过 180 秒，
            // 暂放手动慢组；仍可按精确标签直接执行，不影响默认套件。
            ("空List排序枚举器失效烟测", RunEmptyListSortSmoke),
        };

        private static readonly (string Label, Action Run)[] Cases = CreateCases();

        // 分组调用严格沿原目录顺序；标签、编号及注册副作用顺序保持不变。
        private static (string Label, Action Run)[] CreateCases() =>
        [
            ..CreateScalarsCases(),
            ..CreateObjectTypesCases(),
            ..CreateGenericsCases(),
            ..CreateWrapperCallsCases(),
            ..CreateWrapperDispatchCases(),
            ..CreateWrapperPlacesCases(),
            ..CreateArgumentsAndMemoryCases(),
            ..CreateDynamicTypesCases(),
            ..CreateExceptionsAndStackCallsCases(),
            ..CreateStackDispatchCases(),
            ..CreateCoroutinesCases(),
            ..CreateTasksAndLifetimeCases(),
            ..CreateGcAndCollectionsCases(),
            ..CreateLibraryRegressionsCases(),
        ];

        // 单用例：源 → 中端全管线 → BIL 文本 → VM 执行 + native 编译执行，
        // 比 stdout（行尾归一）与退出码（main 的 i32 返回）
        // 正向源码必须在执行 VM/native 前通过编译诊断检查；运行期负例
        // 同样要求合法源码。BIL 级刻意坏指令仍走各自专门驱动。
        private static BilModule EmitNativeSource(string source,
            [System.Runtime.CompilerServices.CallerMemberName] string label = "")
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            if (unit.Diagnostics.HasErrors)
                throw new InvalidOperationException(label + "：正向源码必须零 Error；" +
                    string.Join("; ", unit.Diagnostics.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Phase + ": " + d.Message)));
            CaseAssertions.CheckTrue(label + "：源码编译零 Error", true);
            return module;
        }

        private static void DeleteNativeTestDirectory(string dir)
        {
            // ExternalProcess 已 WaitForExit 并 Dispose；Windows 映像锁可能
            // 短暂延迟释放。仅对本用例目录有界重试，最终错误仍原样抛出。
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(dir, recursive: true); return; }
                catch (Exception ex) when (OperatingSystem.IsWindows() && attempt < 5
                    && (ex is UnauthorizedAccessException
                        || (ex is IOException && ((ex.HResult & 0xffff) is 5 or 32 or 33))))
                {
                    System.Threading.Thread.Sleep(20 << attempt);
                }
            }
        }

        private static void RunCase(string label, string source,
            IReadOnlyDictionary<string, string>? env = null, long maxSteps = 20_000_000)
        {
            RunCaseModule(label, EmitNativeSource(source, label), env, maxSteps);
        }

        // 驱动 native COMMAND 端到端，捕获 stdout/stderr（同 MiddlewareTests 模式）
        private static (int Code, string Out, string Err) RunNative(params string[] args)
        {
            if (!CommandLineParser.TryParse(args, out var result, out var error))
            {
                throw new InvalidOperationException($"测试构造的命令行应解析成功: {error}");
            }
            var oldOut = Console.Out;
            var oldErr = Console.Error;
            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            WorkerConsole.SetOut(outWriter);
            WorkerConsole.SetError(errWriter);
            try
            {
                int code = new NativeCommand().Execute(result!);
                return (code, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                WorkerConsole.SetOut(oldOut);
                WorkerConsole.SetError(oldErr);
            }
        }
    }
}
