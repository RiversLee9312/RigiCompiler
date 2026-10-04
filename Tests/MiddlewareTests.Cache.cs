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
        // Cache 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestComp003NativeCache()
        {
            var root = Path.Combine(Path.GetTempPath(), "rigi-comp003-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var bil = Path.Combine(root, "hello.bil");
                var text = HelloConcatBil.Replace("R_Zero = i32 0", "R_Zero = i32 17", StringComparison.Ordinal);
                File.WriteAllText(bil, text);
                var vm = BilVm.Run(BilReader.Read(text), maxSteps: 10000);
                TestHarness.CheckTrue("最小完整 BIL 的 VM 参考行为", vm.Exception == null
                    && vm.ReturnValue is Bil.Vm.VmI32 { Value: 17 } && vm.Stdout == "Hello, world!\n");
                var executable = Environment.ProcessPath!;
                var prefix = Path.GetFileNameWithoutExtension(executable) == "dotnet"
                    ? new[] { Environment.GetCommandLineArgs()[0] } : Array.Empty<string>();
                var cache = Path.Combine(root, "cache");
                var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
                (int Exit, System.Text.Json.JsonElement[] Rows) Compile(string name, string[] extra, string? overrideCache = null)
                {
                    var profile = Path.Combine(root, name + "-metrics");
                    var args = prefix.Concat(new[] { "native", "--file", bil, "--out", Path.Combine(root, name + suffix) }).Concat(extra).ToArray();
                    var result = ExternalProcess.Run(executable, args, out _, out var stderr, environment: new Dictionary<string, string>
                    { ["RIGI_PROFILE_DIR"] = profile, ["RIGI_CACHE_ROOT"] = overrideCache ?? cache }, closeStdin: true, timeoutMilliseconds: 120000);
                    if (result != 0 && name != "link-failure" && name != "invalid") Console.WriteLine(stderr);
                    var rows = Directory.Exists(profile) ? Directory.EnumerateFiles(profile, "*.jsonl").SelectMany(File.ReadLines).Select(line =>
                    { using var doc = System.Text.Json.JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray() : [];
                    return (result, rows);
                }
                bool Event(System.Text.Json.JsonElement[] rows, string phase, string status) => rows.Any(r =>
                    r.GetProperty("phase").GetString() == phase && r.GetProperty("status").GetString() == status);
                bool O2(System.Text.Json.JsonElement[] rows) => Event(rows, "llvm.optimize.O2", "completed");
                var cold = Compile("cold", []);
                TestHarness.CheckTrue("真实 whole-program 冷编仍走 O2", cold.Exit == 0 && O2(cold.Rows));
                if (!OperatingSystem.IsLinux())
                {
                    TestHarness.CheckTrue("未验证的平台正常编译并安全绕缓存", Event(cold.Rows, "native-object", "bypass"));
                    return;
                }
                var unavailable = cold.Rows.Any(r => r.GetProperty("phase").GetString() == "native-object"
                    && r.GetProperty("status").GetString() == "bypass"
                    && r.GetProperty("detail").GetString() is "runtime-identity-unavailable" or "backend-identity-unavailable");
                if (unavailable)
                {
                    Console.WriteLine("  [SKIP] runtime/backend 真实身份无法确认：fresh compile 已通过，缓存命中用例不适用");
                    return;
                }
                TestHarness.CheckTrue("冷编明确对象 miss", Event(cold.Rows, "native-object", "miss"));
                var objectPath = Directory.EnumerateFiles(Path.Combine(cache, "object-cache"), "program.o", SearchOption.AllDirectories).Single();
                var objectDigest = Middleware.Cache.ArtifactCache.HashFile(objectPath);
                var hot = Compile("hot", []);
                TestHarness.CheckTrue("不同 outpath 真实对象 hit 跳过 O2", hot.Exit == 0 && Event(hot.Rows, "native-object", "hit") && !O2(hot.Rows)
                    && !hot.Rows.Any(r => r.GetProperty("phase").GetString()!.StartsWith("middleware.", StringComparison.Ordinal)));
                TestHarness.CheckTrue("命中仍执行当前 final link", Event(hot.Rows, "native.final-link", "completed"));
                TestHarness.CheckTrue("真实 linker/CRT/link 库内容仅记录", hot.Rows.Any(r => r.GetProperty("phase").GetString() == "native.link-file"
                    && r.GetProperty("detail").GetString()!.Contains("ld.lld", StringComparison.Ordinal))
                    && hot.Rows.Any(r => r.GetProperty("phase").GetString() == "native.link-file"
                    && r.GetProperty("detail").GetString()!.Contains("crt", StringComparison.OrdinalIgnoreCase)));
                var linkSource = Path.Combine(root, "extra.c");
                var linkObject = Path.Combine(root, "extra.o");
                var clang = ToolchainResolver.ResolveClang(null)!;
                File.WriteAllText(linkSource, "#include <stdio.h>\n__attribute__((constructor)) static void marker(void) { puts(\"first-link\"); }\n");
                var builtLink = ExternalProcess.Run(clang, ["-c", "-fPIC", linkSource, "-o", linkObject], out _, out _, closeStdin: true);
                var firstLink = Compile("first-link", ["--link", linkObject]);
                ExternalProcess.Run(Path.Combine(root, "first-link" + suffix), [], out var firstStdout, out _, closeStdin: true);
                var firstLinkHash = Middleware.Cache.ArtifactCache.HashFile(linkObject);
                File.WriteAllText(linkSource, "#include <stdio.h>\n__attribute__((constructor)) static void marker(void) { puts(\"second-link\"); }\n");
                builtLink |= ExternalProcess.Run(clang, ["-c", "-fPIC", linkSource, "-o", linkObject], out _, out _, closeStdin: true);
                var secondLink = Compile("second-link", ["--link", linkObject]);
                ExternalProcess.Run(Path.Combine(root, "second-link" + suffix), [], out var secondStdout, out _, closeStdin: true);
                TestHarness.CheckTrue("同路径 linkinput 换内容仍对象 hit 且本次 relink", builtLink == 0 && firstLink.Exit == 0 && secondLink.Exit == 0
                    && Event(firstLink.Rows, "native-object", "hit") && Event(secondLink.Rows, "native-object", "hit")
                    && firstStdout.StartsWith("first-link\n", StringComparison.Ordinal) && secondStdout.StartsWith("second-link\n", StringComparison.Ordinal)
                    && firstLinkHash != Middleware.Cache.ArtifactCache.HashFile(linkObject));
                var nativeExit = ExternalProcess.Run(Path.Combine(root, "hot" + suffix), [], out var stdout, out var nativeError, closeStdin: true);
                TestHarness.CheckTrue("缓存命中 Native stdout/exit 与 VM 一致", stdout == vm.Stdout && nativeExit == 17 && nativeError == "");
                TestHarness.Check("缓存命中对象摘要未变", Middleware.Cache.ArtifactCache.HashFile(objectPath), objectDigest);
                var malformed = Path.Combine(root, "bad-link.o");
                File.WriteAllText(malformed, "bad linker input");
                var failedLink = Compile("link-failure", ["--link", malformed]);
                TestHarness.CheckTrue("当前链接输入失败不删除合法对象", failedLink.Exit == 2 && Event(failedLink.Rows, "native-object", "hit")
                    && Event(failedLink.Rows, "native.final-link", "failed") && Middleware.Cache.ArtifactCache.HashFile(objectPath) == objectDigest);
                File.WriteAllText(objectPath, "bad cached object");
                var repaired = Compile("repaired", []);
                TestHarness.CheckTrue("真实对象坏 cache 自愈且重新 O2", repaired.Exit == 0 && Event(repaired.Rows, "native-object", "miss") && O2(repaired.Rows));
                TestHarness.Check("对象修复内容一致", Middleware.Cache.ArtifactCache.HashFile(objectPath), objectDigest);
                var ll = Path.Combine(root, "debug.ll");
                var debugObject = Path.Combine(root, "debug.o");
                var diagnostic = Compile("diagnostic", ["--emit-ll", ll, "--emit-obj", debugObject]);
                TestHarness.CheckTrue("诊断产物请求绕对象快路且保持 O2", diagnostic.Exit == 0 && Event(diagnostic.Rows, "native-object", "bypass") && O2(diagnostic.Rows));
                TestHarness.CheckTrue("诊断 LL 是 merge 前且 debugobj 是 opt 前", !File.ReadAllText(ll).Contains("define i32 @main", StringComparison.Ordinal)
                    && File.Exists(debugObject) && Middleware.Cache.ArtifactCache.HashFile(debugObject) != objectDigest);
                var standaloneProfile = Path.Combine(root, "standalone-metrics");
                var standaloneExit = ExternalProcess.Run(executable, prefix.Concat(new[] { "native", "--file", bil,
                    "--emit-ll", Path.Combine(root, "standalone.ll"), "--emit-obj", Path.Combine(root, "standalone.o") }).ToArray(),
                    out _, out _, environment: new Dictionary<string, string>
                    { ["RIGI_PROFILE_DIR"] = standaloneProfile, ["RIGI_CACHE_ROOT"] = cache }, closeStdin: true);
                var standaloneRows = Directory.EnumerateFiles(standaloneProfile, "*.jsonl").SelectMany(File.ReadLines).Select(line =>
                { using var doc = System.Text.Json.JsonDocument.Parse(line); return doc.RootElement.Clone(); }).ToArray();
                TestHarness.CheckTrue("仅诊断产物无需 runtime/link/O2 且明确 bypass", standaloneExit == 0 && Event(standaloneRows, "native-object", "bypass")
                    && !O2(standaloneRows) && !standaloneRows.Any(r => r.GetProperty("phase").GetString() == "native.final-link"));
                var blockedRoot = Path.Combine(root, "blocked-cache");
                File.WriteAllText(blockedRoot, "file prevents cache directory");
                var fallback = Compile("cache-io", [], blockedRoot);
                TestHarness.CheckTrue("缓存 I/O 故障正常 fresh compile", fallback.Exit == 0 && O2(fallback.Rows) && Event(fallback.Rows, "native-object", "bypass"));
                File.WriteAllText(bil, UndeclaredVarBil);
                var invalid = Compile("invalid", []);
                TestHarness.CheckTrue("非法 BIL 不得借快路绕过 Gate", invalid.Exit == 1 && !invalid.Rows.Any(r => r.GetProperty("phase").GetString() == "native-object"));
            }
            finally { Directory.Delete(root, true); }
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "只在存在托管映像 Location 时核对该文件；NativeAOT 核对进程映像。")]
        private static void TestComp003ObjectIdentity()
        {
            var compilerAssembly = typeof(MiddlewareTests).Assembly;
            var bindingAssembly = typeof(LLVMSharp.Interop.LLVM).Assembly;
            var compilerIdentity = Middleware.Cache.NativeObjectIdentity.CompilerContentIdentity();
            var bindingIdentity = Middleware.Cache.NativeObjectIdentity.BindingContentIdentity();
            var managed = !string.IsNullOrEmpty(compilerAssembly.Location);
            TestHarness.CheckTrue("编译器身份选择实际托管映像而非动态代码开关",
                Middleware.Cache.NativeObjectIdentity.UsesManagedImages == managed);
            if (OperatingSystem.IsLinux())
            {
                var expectedCompiler = Middleware.Cache.ArtifactCache.HashFile(managed ? compilerAssembly.Location : "/proc/self/exe");
                var expectedBinding = managed ? Middleware.Cache.ArtifactCache.HashFile(bindingAssembly.Location) : expectedCompiler;
                TestHarness.Check("编译器身份等于实际 compiler 映像内容", compilerIdentity ?? "unknown", expectedCompiler);
                TestHarness.Check("绑定身份等于实际 LLVMSharp 映像内容", bindingIdentity ?? "unknown", expectedBinding);
                if (managed)
                    TestHarness.CheckTrue("托管 compiler 身份与 runtime 宿主不同",
                        compilerIdentity != Middleware.Cache.ArtifactCache.HashFile(Environment.ProcessPath!));
            }
            Console.WriteLine("  compiler-identity: " + new System.Text.Json.Nodes.JsonObject
            {
                ["managedImages"] = managed, ["dynamicCodeSupported"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
                ["compilerPath"] = compilerAssembly.Location, ["compilerSha"] = compilerIdentity,
                ["bindingPath"] = bindingAssembly.Location, ["bindingSha"] = bindingIdentity,
                ["processPath"] = Environment.ProcessPath, ["processSha"] = Middleware.Cache.ArtifactCache.HashFile(Environment.ProcessPath!),
                ["compilerMvid"] = compilerAssembly.ManifestModule.ModuleVersionId.ToString(),
                ["effectiveOverride"] = Environment.GetEnvironmentVariable("RIGI_TEST_RIGIC")
            }.ToJsonString());
            string Key(string bil = "bil", string compiler = "compiler", string binding = "binding", string llvm = "llvm",
                string target = "target", string layout = "layout", string runtime = "runtime", string kind = "whole-program-executable",
                string optimization = "default<O2>", string cpu = "generic", string features = "", string relocation = "PIC",
                string codeModel = "default", string abi = "host-object-v1") => Middleware.Cache.NativeObjectIdentity.Compute(
                    bil, compiler, binding, llvm, target, layout, runtime, kind, optimization, cpu, features, relocation, codeModel, abi);
            var original = Key();
            var variations = new[] { Key(bil: "changed"), Key(compiler: "changed"), Key(binding: "changed"), Key(llvm: "changed"),
                Key(target: "changed"), Key(layout: "changed"), Key(runtime: "changed"), Key(kind: "library"), Key(optimization: "O1"),
                Key(cpu: "native"), Key(features: "+sse"), Key(relocation: "static"), Key(codeModel: "large"), Key(abi: "changed") };
            TestHarness.CheckTrue("对象全部 codegen 边界进入 key", variations.All(k => k != original) && variations.Distinct().Count() == variations.Length);
            TestHarness.Check("固定输入身份稳定", Key(), original);
            string BilKey(string input) => Key(bil: BilWriter.Write(BilReader.Read(input)));
            var bilKey = BilKey(MinimalValidBil);
            var bilVariants = new[]
            {
                MinimalValidBil.Replace("symtest", "other-module", StringComparison.Ordinal),
                MinimalValidBil.Replace("R_Zero = i32 0", "R_Zero = i32 1", StringComparison.Ordinal),
                MinimalValidBil.Replace("Point#x@.i32 pub var", "Point#x@.i32 priv var", StringComparison.Ordinal),
                MinimalValidBil.Replace("load res(R_Zero) $a", "load res(R_Zero) $a\n        load res(R_Zero) $a", StringComparison.Ordinal),
            };
            TestHarness.CheckTrue("canonical BIL 包含 metadata/resources/declarations/body", bilVariants.All(input => BilKey(input) != bilKey));
            var loaded = LlvmHost.ContentIdentity();
            if (loaded == null)
                Console.WriteLine("  [SKIP] 实际 LLVM 内容身份无法确认：生产快路安全绕过");
            else
            {
                TestHarness.CheckTrue("实际加载 LLVM 文件内容身份", loaded.Length == 64);
                TestHarness.Check("LLVM immutable 身份可重复验证", LlvmHost.ContentIdentity() ?? "unknown", loaded);
                var gate = BilGate.Accept(MinimalValidBil, "identity.bil");
                var actualKey = Middleware.Cache.NativeObjectIdentity.TryCompute(gate.Module!, LlvmHost.HostTriple, LlvmHost.HostDataLayout, "runtime");
                if (actualKey == null) Console.WriteLine("  [SKIP] compiler/binding 已加载映射无法确认：生产快路安全绕过");
                else TestHarness.CheckTrue("真实 compiler/binding 与已加载映射匹配", actualKey.Length == 64);
            }
            using var lease = LlvmHost.Enter();
            var guarded = false;
            try { LlvmHost.RequireNotOwned(); } catch (InvalidOperationException) { guarded = true; }
            TestHarness.CheckTrue("LLVM 到文件锁逆序响亮拒绝", guarded);
            using var nested = LlvmHost.Enter();
            TestHarness.CheckTrue("LLVM lease 可重入", true);
        }

        private static void TestComp003RuntimeSnapshot()
        {
            var clang = ToolchainResolver.ResolveClang(null);
            var libuv = LibuvResolver.Resolve(null);
            TestHarness.CheckTrue("runtime 实际工具链就绪", clang != null && libuv != null);
            if (clang == null || libuv == null) return;
            var root = Path.Combine(Path.GetTempPath(), "rigi-comp003-runtime-" + Guid.NewGuid().ToString("N"));
            var previous = Environment.GetEnvironmentVariable("RIGI_CACHE_ROOT");
            try
            {
                Environment.SetEnvironmentVariable("RIGI_CACHE_ROOT", root);
                using var cold = RigiRtBuilder.PrepareBitcode(clang, LlvmHost.HostTriple, out var rebuilt, libuv);
                TestHarness.CheckTrue("真实 runtime 冷编 snapshot", rebuilt);
                if (!OperatingSystem.IsLinux())
                {
                    TestHarness.CheckTrue("未验证的平台安全绕缓存", cold.Identity == null);
                    return;
                }
                if (cold.Identity == null)
                {
                    LlvmBitcode.ValidateRuntimeTarget(cold.Path, LlvmHost.HostTriple);
                    Console.WriteLine("  [SKIP] runtime codegen closure 无法确认：实际 fresh compile 目标验证通过，缓存命中用例不适用");
                    return;
                }
                TestHarness.CheckTrue("Linux runtime codegen closure 可确认", cold.Identity.Length == 64);
                var digest = Middleware.Cache.ArtifactCache.HashFile(cold.Path);
                using var hot = RigiRtBuilder.PrepareBitcode(clang, LlvmHost.HostTriple, out rebuilt, libuv);
                TestHarness.CheckTrue("预处理随机私有路径不影响 hit", !rebuilt && hot.Identity == cold.Identity);
                TestHarness.Check("runtime 冷热 bitcode 内容一致", Middleware.Cache.ArtifactCache.HashFile(hot.Path), digest);
                if (cold.Identity != null)
                {
                    File.WriteAllText(Path.Combine(RigiRtBuilder.GetCacheRoot(), cold.Identity, "rigi_rt.bc"), "bad bitcode");
                    using var repaired = RigiRtBuilder.PrepareBitcode(clang, LlvmHost.HostTriple, out rebuilt, libuv);
                    TestHarness.CheckTrue("runtime 坏缓存自愈", rebuilt && repaired.Identity == cold.Identity);
                    TestHarness.Check("runtime 修复内容一致", Middleware.Cache.ArtifactCache.HashFile(repaired.Path), digest);
                }
                var includes = Path.Combine(root, "include");
                foreach (var header in Directory.EnumerateFiles(libuv.IncludeDir, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(includes, Path.GetRelativePath(libuv.IncludeDir, header));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(header, target);
                }
                var copied = new LibuvLayout(includes, libuv.StaticLibPath);
                using var beforeHeader = RigiRtBuilder.PrepareBitcode(clang, LlvmHost.HostTriple, out _, copied);
                File.AppendAllText(Path.Combine(includes, "uv.h"), "\ntypedef int rigi_comp003_header_probe;\n");
                using var afterHeader = RigiRtBuilder.PrepareBitcode(clang, LlvmHost.HostTriple, out rebuilt, copied);
                TestHarness.CheckTrue("同路径真实 libuv 头变化重新编译", rebuilt && beforeHeader.Identity != afterHeader.Identity);
                var blocked = Path.Combine(root, "blocked-root");
                File.WriteAllText(blocked, "blocks cache directory");
                Environment.SetEnvironmentVariable("RIGI_CACHE_ROOT", blocked);
                var legacy = RigiRtBuilder.EnsureBitcode(clang, LlvmHost.HostTriple, out rebuilt, libuv);
                LlvmBitcode.ValidateRuntimeTarget(legacy, LlvmHost.HostTriple);
                TestHarness.CheckTrue("旧 path helper 缓存 I/O 退化交付已验证请求副本", rebuilt && File.Exists(legacy));
            }
            finally
            {
                Environment.SetEnvironmentVariable("RIGI_CACHE_ROOT", previous);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void TestComp003ArtifactCache()
        {
            var root = Path.Combine(Path.GetTempPath(), "rigi-comp003-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var key = Middleware.Cache.ArtifactCache.Identity("schema", "body");
                var cache = Path.Combine(root, "cache");
                var builds = 0;
                Action<string> build = path => { Interlocked.Increment(ref builds); File.WriteAllText(path, "object"); };
                var first = Path.Combine(root, "first.o");
                TestHarness.CheckTrue("缓存首次原子发布", Middleware.Cache.ArtifactCache.TryMaterialize(
                    cache, key, "program.o", first, build, null, out var rebuilt) && rebuilt);
                TestHarness.CheckTrue("缓存目录采用完整身份", Directory.Exists(Path.Combine(cache, key)) && key.Length == 64);
                var second = Path.Combine(root, "second.o");
                TestHarness.CheckTrue("不同请求路径复用对象", Middleware.Cache.ArtifactCache.TryMaterialize(
                    cache, key, "program.o", second, build, null, out rebuilt) && !rebuilt && builds == 1);
                File.WriteAllText(Path.Combine(cache, key, "program.o"), "bad");
                TestHarness.CheckTrue("损坏条目在锁内重建", Middleware.Cache.ArtifactCache.TryMaterialize(
                    cache, key, "program.o", second, build, null, out rebuilt) && rebuilt && builds == 2);
                TestHarness.Check("请求得到完整对象", File.ReadAllText(second), "object");
                var parallelKey = Middleware.Cache.ArtifactCache.Identity("parallel");
                var tasks = Enumerable.Range(0, 8).Select(i => System.Threading.Tasks.Task.Run(() =>
                    Middleware.Cache.ArtifactCache.TryMaterialize(cache, parallelKey, "program.o",
                        Path.Combine(root, i + ".o"), build, null, out _))).ToArray();
                System.Threading.Tasks.Task.WaitAll(tasks);
                TestHarness.CheckTrue("同身份 builder singleflight", tasks.All(t => t.Result) && builds == 3);
                TestHarness.CheckTrue("稳定 sibling lock 保留", File.Exists(Path.Combine(cache, ".locks", parallelKey + ".lock")));
                TestHarness.CheckTrue("字段边界不可碰撞", Middleware.Cache.ArtifactCache.Identity("ab", "c")
                    != Middleware.Cache.ArtifactCache.Identity("a", "bc"));
                var diagnostic = false;
                try { Middleware.Cache.ArtifactCache.TryMaterialize(cache, Middleware.Cache.ArtifactCache.Identity("failure"),
                    "program.o", second, _ => throw new InvalidOperationException("semantic"), null, out _); }
                catch (InvalidOperationException ex) { diagnostic = ex.Message == "semantic"; }
                TestHarness.CheckTrue("builder 语义诊断原样传播", diagnostic);
            }
            finally { Directory.Delete(root, true); }
        }

    }
}
