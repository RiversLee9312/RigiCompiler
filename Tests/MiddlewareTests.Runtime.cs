using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using RigiCompiler.Bil;
using RigiCompiler.Middleware;
using RigiCompiler.Middleware.Binding;
using RigiCompiler.Middleware.Layout;
using RigiCompiler.Middleware.Cli;
using RigiCompiler.Middleware.Emit;
using RigiCompiler.Middleware.Gate;
using RigiCompiler.Middleware.Mir;
using RigiCompiler.Middleware.Passes;
using RigiCompiler.Middleware.Runtime;
using RigiCompiler.Middleware.Toolchain;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Tests
{
    public static partial class MiddlewareTests
    {
        // Runtime 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestRuntimeFeatureMacroPreamble()
        {
            var sources = new (string FileName, string Text)[]
            {
                ("arc.c", ""), ("arc.h", ""), ("fs.c", ""),
            };
            var macros = new[] { "-D_POSIX_C_SOURCE=200809L", "-D_DEFAULT_SOURCE" };
            var actual = RigiRtBuilder.BuildUnitySource(sources, macros);
            CaseAssertions.Check("特性宏先于 include 且宏体与 -D 一致", actual,
                "#define _POSIX_C_SOURCE 200809L\n#define _DEFAULT_SOURCE 1\n" +
                "#include \"arc.c\"\n#include \"fs.c\"\n");
            CaseAssertions.Check("有值宏只移除第一个等号", RigiRtBuilder.BuildUnitySource(
                Array.Empty<(string FileName, string Text)>(), new[] { "-DCONDITION=a==b" }),
                "#define CONDITION a==b\n");
            CaseAssertions.Check("显式空宏保持空宏体", RigiRtBuilder.BuildUnitySource(
                Array.Empty<(string FileName, string Text)>(), new[] { "-DEMPTY=" }),
                "#define EMPTY \n");
            CaseAssertions.Check("无特性宏仍只包含 C 编译单元", RigiRtBuilder.BuildUnitySource(
                sources, Array.Empty<string>()), "#include \"arc.c\"\n#include \"fs.c\"\n");
            CaseAssertions.CheckTrue("有值宏前导不得包含分隔等号",
                !actual.Contains("#define _POSIX_C_SOURCE =", StringComparison.Ordinal));
        }

        private static void TestRuntimeTargetIdentity()
        {
            var sources = new (string FileName, string Text)[] { ("probe.c", "int probe(void) { return 1; }") };
            var unity = RigiRtBuilder.BuildUnitySource(sources, Array.Empty<string>());
            var host = LlvmHost.HostTriple;
            Console.WriteLine("  LLVM 宿主目标: " + host);
            Console.WriteLine("  LLVM TargetMachine layout: " + LlvmHost.HostDataLayout);
            // Linux libLLVM 的 vendor 可能是 unknown；显式翻转，不能假设恒为 pc。
            var targetParts = host.Split('-');
            targetParts[1] = targetParts[1] == "pc" ? "unknown" : "pc";
            var other = string.Join('-', targetParts);
            CaseAssertions.CheckTrue("宿主目标与对照目标不同", host != other);
            var hostArgs = RigiRtBuilder.BuildCompileArgs(host, null, "unity.c", "rigi_rt.bc");
            var otherArgs = RigiRtBuilder.BuildCompileArgs(other, null, "unity.c", "rigi_rt.bc");
            CaseAssertions.Check("clang 参数明确指定 LLVM 目标", hostArgs[0], "--target=" + host);
            var hostKey = RigiRtBuilder.ComputeCacheIdentity(sources, unity, "clang-sha", hostArgs);
            var otherKey = RigiRtBuilder.ComputeCacheIdentity(sources, unity, "clang-sha", otherArgs);
            CaseAssertions.CheckTrue("相同源码不同目标不可复用缓存", hostKey != otherKey);
            var changedArgs = new List<string>(hostArgs) { "-fno-builtin" };
            CaseAssertions.CheckTrue("编译参数改变不可复用缓存", hostKey !=
                RigiRtBuilder.ComputeCacheIdentity(sources, unity, "clang-sha", changedArgs));
            CaseAssertions.CheckTrue("clang 内容改变不可复用缓存", hostKey !=
                RigiRtBuilder.ComputeCacheIdentity(sources, unity, "other-sha", hostArgs));

            var clang = RigiCompiler.Middleware.Toolchain.ToolchainResolver.ResolveClang(null);
            if (clang == null)
            {
                Console.WriteLine("  SKIP 运行时目标实编探针：未找到 clang");
                return;
            }
            var root = Path.Combine(Path.GetTempPath(), "rigi-target-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "probe.c");
                var bitcode = Path.Combine(root, "probe.bc");
                File.WriteAllText(source, sources[0].Text);
                var args = RigiRtBuilder.BuildCompileArgs(host, null, source, bitcode);
                var exit = RigiCompiler.Middleware.Toolchain.ExternalProcess.Run(clang, args,
                    out _, out var stderr, workingDirectory: root);
                CaseAssertions.CheckTrue("clang 实编宿主目标 bitcode", exit == 0);
                if (exit == 0)
                {
                    LlvmBitcode.ValidateRuntimeTarget(bitcode, host);
                    CaseAssertions.CheckTrue("真实 bitcode triple/layout 等于宿主 TargetMachine", true);
                    var libuv = RigiCompiler.Middleware.Toolchain.LibuvResolver.Resolve(null);
                    if (libuv != null)
                    {
                        using var prepared = RigiRtBuilder.PrepareBitcode(clang, host, out var rebuilt, libuv);
                        var cached = prepared.Path;
                        Console.WriteLine("  rigi_rt 实际缓存: " + cached + " rebuilt=" + rebuilt);
                        LlvmBitcode.ValidateRuntimeTarget(cached, host);
                        CaseAssertions.CheckTrue("真实运行时缓存 bitcode 目标相容", true);
                        using var hit = RigiRtBuilder.PrepareBitcode(clang, host, out var rebuiltAgain, libuv);
                        if (prepared.Identity != null && hit.Identity != null)
                        {
                            CaseAssertions.Check("相同目标与参数命中同一缓存", hit.Identity, prepared.Identity);
                            CaseAssertions.CheckTrue("命中无需再次编译", !rebuiltAgain);
                        }
                        else
                        {
                            LlvmBitcode.ValidateRuntimeTarget(hit.Path, host);
                            CaseAssertions.CheckTrue("未知身份正常 fresh compile", rebuiltAgain);
                            Console.WriteLine("  [SKIP] runtime 身份未知：不把 uncached 路径比较计为缓存命中");
                        }
                    }
                    var rejected = false;
                    try { LlvmBitcode.ValidateRuntimeTarget(bitcode, other); }
                    catch (InvalidOperationException) { rejected = true; }
                    CaseAssertions.CheckTrue("真实目标不符在链接之前拒绝", rejected);
                }
                else Console.WriteLine("  clang stderr: " + stderr);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static void TestExternalProcessDeadline()
        {
            CaseAssertions.CheckTrue("LLVM 全局名保留不同泛型形状的身份",
                GenericAbi.EscapeGlobalName("typesheet.", "A<B,C>")
                != GenericAbi.EscapeGlobalName("typesheet.", "A<B.C>"));
            var probe = Path.GetTempFileName();
            try
            {
                File.WriteAllText(probe, "trusted-tool");
                var digest = RigiCompiler.Middleware.Toolchain.ToolchainResolver.Fingerprint(probe);
                CaseAssertions.Check("工具链摘要钉值验证",
                    RigiCompiler.Middleware.Toolchain.ToolchainResolver.Fingerprint(probe, digest), digest);
                File.WriteAllText(probe, "tampered-tool");
                var rejected = false;
                try { RigiCompiler.Middleware.Toolchain.ToolchainResolver.Fingerprint(probe, digest); }
                catch (InvalidOperationException) { rejected = true; }
                CaseAssertions.CheckTrue("篡改工具链在执行前拒绝", rejected);
            }
            finally { File.Delete(probe); }
            var windows = OperatingSystem.IsWindows();
            var shell = windows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh";
            var args = windows ? new[] { "/d", "/c", "echo output & echo error 1>&2 & exit /b 7" }
                : new[] { "-c", "printf 'output\\n'; printf 'error\\n' >&2; exit 7" };
            var code = RigiCompiler.Middleware.Toolchain.ExternalProcess.Run(shell, args,
                out var output, out var error, timeoutMilliseconds: 10_000);
            CaseAssertions.CheckTrue("外部进程保留退出码和双路输出",
                code == 7 && output.Trim() == "output" && error.Trim() == "error");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var timedOut = false;
            try
            {
                RigiCompiler.Middleware.Toolchain.ExternalProcess.Run(shell,
                    windows ? new[] { "/d", "/c", "ping -n 30 127.0.0.1 >nul" }
                        : new[] { "-c", "sleep 30" }, out _, out _, timeoutMilliseconds: 150);
            }
            catch (InvalidOperationException ex) { timedOut = ex.Message.Contains("150ms"); }
            CaseAssertions.CheckTrue("外部进程超时受控退出且不无限等待读管道",
                timedOut && timer.ElapsedMilliseconds < 10_000);
        }

        private static void TestWrapperGenericInitArgumentAbi()
        {
            var (unit, bil, _) = BilTestHarness.EmitBilUnit(
                "rich struct Data { pub var text: String = \"ok\" }\n" +
                "@WrapperTarget(.Entity)\nrich wrapper W\\<TTarget> { pub var value: TTarget\n" +
                "pub init(value: TTarget) { this.value = value } }\n" +
                "@W\\<i64>(9L)\nclass NumberHost { }\n" +
                "@W\\<String>(\"ok\")\nclass StringHost { }\n" +
                "@W\\<Data>(new Data())\nclass RichHost { }\n" +
                "@W\\<Array\\<i64>>(core.collections.arrayOf\\<i64>(1))\nclass ArrayHost { }\n" +
                "pub func main(): i32 { var n = new NumberHost()\n var s = new StringHost()\n" +
                "var r = new RichHost()\n var a = new ArrayHost()\n return 0 }\n");
            CaseAssertions.CheckTrue("GP wrapper 实参语义合法", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            if (unit.Diagnostics.HasErrors) return;
            BilTestHarness.CheckBilValid("GP wrapper 四种实参 BIL 合法", bil);
            var vm = RigiCompiler.Bil.BilVm.Run(bil);
            CaseAssertions.CheckTrue("GP wrapper 四种实参 VM 合法",
                vm.Exception == null && vm.ReturnValue is RigiCompiler.Bil.Vm.VmI32 { Value: 0 });
            var context = new MwContext(bil);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease392 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var calls = module.PrintToString().Split('\n').Where(line =>
                line.Contains("call void @\"W$init(", StringComparison.Ordinal)).ToArray();
            CaseAssertions.CheckTrue("GP init 的整数/String/rich struct/数组实参均按胖值 ABI 传递",
                calls.Length == 4 && calls.All(line => line.Contains("{ i64, i64 }", StringComparison.Ordinal)),
                string.Join("\n", calls));
        }

        private static void TestCapabilityConstructedCalls(
            [System.Runtime.CompilerServices.CallerFilePath] string path = "")
        {
            var source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!,
                "e2e", "rigi", "capability_generic_cells.rg"));
            var (_, module, _) = BilTestHarness.EmitBilUnit(source);
            var collected = ConstructedTypeCollector.Collect(new MwContext(module));
            foreach (var type in new[] { "i32", "i64", "u32", "u16", "u64", "i8" })
                CaseAssertions.CheckTrue("实际调用闭合 Cell<" + type + ">",
                    collected.Contains("core::Cell<core::" + type + ">"),
                    string.Join(", ", collected.Where(t => t.StartsWith("core::Cell<"))));
            CaseAssertions.CheckTrue("未调用 grow 不扩张构造闭包",
                !collected.Contains("Box<Box<core::i32>>"));
            var (_, inherited, _) = BilTestHarness.EmitBilUnit(
                "pub open class Base\\<T> { pub var value:T\n pub init(_ -> value) }\n" +
                "pub class Derived:Base\\<i32> { pub var marker:i32=9\n pub init(v:i32) { super(v) } }\n" +
                "pub func main():i32 { const d = new Derived(27)\n return 0 }\n");
            var inheritedContext = new MwContext(inherited);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(inheritedContext);
            CaseAssertions.CheckTrue("普通子类保留闭合泛型基类身份",
                inheritedContext.Layout!.Find("Derived")?.BasePlan?.Symbol.Canonical == "Base<core::i32>");
            var derived = inheritedContext.Layout.Find("Derived")!;
            var template = inheritedContext.Layout.Find("Base")!;
            CaseAssertions.CheckTrue("闭合基类不改变继承字段与隐藏typeid偏移",
                template.Fields.All(field => derived.Fields.Any(inheritedField => inheritedField.Symbol == field.Symbol
                    && inheritedField.Offset == field.Offset && inheritedField.Size == field.Size
                    && inheritedField.IsHiddenTypeId == field.IsHiddenTypeId)));
            CaseAssertions.CheckTrue("派生字段排在完整基类布局之后",
                derived.Fields.Single(field => field.Symbol.Contains("#marker@")).Offset >= template.Size);
        }

    }
}
