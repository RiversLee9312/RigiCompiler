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
        // Cli 职责；与主文件共享同一类型、字段及生命周期。

        private static void RewriteWrapForward(BilModule module)
        {
            var wrap = module.Functions.Single(f => f.Symbol == "$wrap()@.i32");
            var entry = wrap.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            var invoke = entry.Instructions.OfType<InvokeInstruction>().Single();
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(invoke.Method, invoke.Target,
                new[] { new BilVariableOperand(".vargs.nums") }));
            entry.Instructions.Add(new RetInstruction(invoke.Target));
        }

        // ===== 受控失败 =====

        private static void TestNotSupported()
        {
            // try 已过门禁且 MIR 面随 MW9a 落地：MirBuilder 展开不再受控拒绝
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    try { return 1 } catch (e: core.RuntimeException) { return 2 }\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "try.bil");
            TestHarness.CheckTrue("try 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var tryMir = MirBuilder.Build(new MwContext(gate.Module!));
            TestHarness.CheckTrue("try MIR 构造放行（MW9a）",
                tryMir.Functions.Any(f => f.IsEntrypoint));

            // raw.hex/raw.bin：§19.3 字节序列物化已随 L5 定稿（字节缓冲区
            // 三族 .array<u8>/core::Span<u8>/core::SharedSpan<u8>，见
            // TestRawBufferEmission）；.string 等缓冲区外目标无字节序列
            // 语义（VM 对 raw load 整体拒绝）——门禁放行（verifier 对
            // raw 跳过严格匹配），资源发射受控拒绝
            const string rawBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawtest\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Zero = i32 0\n" +
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
                "    .vars {\n" +
                "        .string d,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var rawGate = BilGate.Accept(rawBil, "raw.bil");
            TestHarness.CheckTrue("raw 用例门禁放行", rawGate.IsAccepted,
                string.Join("; ", rawGate.Errors));
            var rawCaught = false;
            try
            {
                var rawContext = new MwContext(rawGate.Module!);
                var rawMir = MirBuilder.Build(rawContext);
                using var llvmLease6716 = LlvmHost.Enter();
                using var rawModule = ModuleBuilder.Build(rawContext, rawMir);
            }
            catch (MwNotSupportedException ex)
            {
                rawCaught = ex.Message.Contains("array<u8>");
            }
            TestHarness.CheckTrue("raw 资源受控拒绝（非 .array<u8> 目标）", rawCaught);

            // CLI 路径：受控失败转退出码 2 而非崩溃（try 已随 MW9a 落地，
            // 受控失败样本改用 raw.hex 非 .array<u8> 目标）
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_unsupported_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var bilPath = Path.Combine(dir, "raw.bil");
                File.WriteAllText(bilPath, rawBil, new UTF8Encoding(false));
                var result = RunNative("native", "--file", bilPath,
                    "--emit-obj", Path.Combine(dir, "raw.o"));
                TestHarness.CheckTrue("不支持形态 CLI 退出码 2", result.Code == 2);
                TestHarness.CheckTrue("不支持形态错误走 stderr", result.Err.Contains("array<u8>"),
                    result.Err);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }

            // R3：合成入口脊柱直调站点（非 MIR、不可协议化）的 taint
            // 补闸——脊柱运行于调度器启动前/关停后，挂起点无泵可恢复，
            // tainted 必崩（实证 trap/AV），受控拒绝
            // ① 全局异常处理器 lambda 含 yield（invoke.indirect 传染
            //    dispatch，entry stub gexc drain 直调）
            ExpectMwNotSupportedFromSource(
                "pub class Res implements core.IDisposable {\n" +
                "    pub init() { }\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.GlobalExceptionHandler.register(func{ (e: core.Exception) -> {\n" +
                "        yield\n" +
                "    } })\n" +
                "    const r = new Res()\n" +
                "    return 0\n" +
                "}\n",
                "全局异常处理器",
                "R3 全局异常处理器含挂起点受控拒绝");
            // ② 全局初始值设定项调 tainted fn（..globals.init 脊柱直调）
            ExpectMwNotSupportedFromSource(
                "var g: i32 = slowCall()\n" +
                "func slowCall(): i32 {\n" +
                "    var f = func{(x: i32): i32 -> {\n" +
                "        yield\n" +
                "        return@_ x + 1\n" +
                "    }}\n" +
                "    return f(41)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return g\n" +
                "}\n",
                "全局初始值设定项",
                "R3 全局初始值含挂起点受控拒绝");
            // ③ singleton init 含 yield（急切 get fn 脊柱直调）
            ExpectMwNotSupportedFromSource(
                "pub shared singleton class S {\n" +
                "    pub var v: i32\n" +
                "    pub init() {\n" +
                "        yield\n" +
                "        v = 42\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new S()\n" +
                "    return 0\n" +
                "}\n",
                "singleton init",
                "R3 singleton init 含挂起点受控拒绝");
            // ④ Exception.getMessage override 含 yield（reporter 脊柱
            //    虚调；优先报根因而非被传染的 dispatch 脊柱）
            ExpectMwNotSupportedFromSource(
                "pub class SlowExc : core.Exception {\n" +
                "    pub init() { }\n" +
                "    pub override func getMessage(): String {\n" +
                "        yield\n" +
                "        return \"slow\"\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    throw new SlowExc()\n" +
                "}\n",
                "getMessage override",
                "R3 getMessage override 含挂起点受控拒绝");
        }

        // ===== 空模块 .o 发射 =====

        private static void TestObjectEmission()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_test_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var gate = BilGate.Accept(MinimalValidBil, "ok.bil");
                var context = new MwContext(gate.Module!);
                var mir = MirBuilder.Build(context);
                using var llvmLease6820 = LlvmHost.Enter();
                using var module = ModuleBuilder.Build(context, mir);
                var objPath = Path.Combine(dir, "symtest.o");

                var ok = ObjectEmitter.TryEmitObject(module, objPath, out var error);
                TestHarness.CheckTrue("空模块 .o 发射成功", ok, error);
                TestHarness.CheckTrue(".o 已落盘且非空",
                    File.Exists(objPath) && new FileInfo(objPath).Length > 0);

                // 目标文件魔数：win-x64 → COFF（前 2 字节 machine 0x8664 小端）；
                // linux-x64 → ELF（0x7F 'E' 'L' 'F'）
                var head = File.ReadAllBytes(objPath).Take(4).ToArray();
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    TestHarness.CheckTrue(".o 为 COFF（x64 machine 魔数）",
                        head[0] == 0x64 && head[1] == 0x86,
                        BitConverter.ToString(head));
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    TestHarness.CheckTrue(".o 为 ELF（魔数）",
                        head[0] == 0x7F && head[1] == 0x45 && head[2] == 0x4C && head[3] == 0x46,
                        BitConverter.ToString(head));
                }

                // 失败路径：不存在目录下的输出路径 → false + 可读错误（不抛崩）
                var badPath = Path.Combine(dir, "no_such_dir", "x.o");
                var fail = ObjectEmitter.TryEmitObject(module, badPath, out var failError);
                TestHarness.CheckTrue("不可写路径发射返回 false", !fail);
                TestHarness.CheckTrue("不可写路径错误可读", failError.Length > 0);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ===== native CLI 端到端 =====

        private static void TestNativeCli()
        {
            var missingFile = RunNative("native");
            TestHarness.CheckTrue("native 缺 --file 退出码 2", missingFile.Code == 2);

            var missingOut = RunNative("native", "--file", "x.bil");
            TestHarness.CheckTrue("native 缺 --out 退出码 2", missingOut.Code == 2);

            var dir = Path.Combine(Path.GetTempPath(), $"rigi_mw_cli_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var badPath = Path.Combine(dir, "bad.bil");
                File.WriteAllText(badPath, UndeclaredVarBil, new UTF8Encoding(false));
                var rejected = RunNative("native", "--file", badPath,
                    "--out", Path.Combine(dir, "bad.o"));
                TestHarness.CheckTrue("native 非法 BIL 退出码 1", rejected.Code == 1);
                TestHarness.CheckTrue("native 非法 BIL 错误走 stderr", rejected.Err.Contains("$missing"));
                TestHarness.CheckTrue("native 拒绝后不产 .o",
                    !File.Exists(Path.Combine(dir, "bad.o")));

                var okPath = Path.Combine(dir, "ok.bil");
                File.WriteAllText(okPath, MinimalValidBil, new UTF8Encoding(false));
                var objPath = Path.Combine(dir, "app.o");
                var accepted = RunNative("native", "--file", okPath, "--emit-obj", objPath);
                TestHarness.CheckTrue("native 合法 BIL 退出码 0", accepted.Code == 0, accepted.Err);
                TestHarness.CheckTrue("native 发射 .o 落盘",
                    File.Exists(objPath) && new FileInfo(objPath).Length > 0);
                TestHarness.CheckTrue("native stdout 纯净", accepted.Out.Length == 0,
                    accepted.Out);

                // L6：--link 链接输入存在性校验（早失败，退出码 2）
                var missingLink = RunNative("native", "--file", okPath,
                    "--emit-obj", Path.Combine(dir, "app2.o"),
                    "--link", Path.Combine(dir, "nope.lib"));
                TestHarness.CheckTrue("native --link 输入不存在退出码 2",
                    missingLink.Code == 2);
                TestHarness.CheckTrue("native --link 错误走 stderr",
                    missingLink.Err.Contains("--link"), missingLink.Err);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ===== RcInjection（MW7a）=====

        private static MwContext PipelineFromSource(string source, string file)
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(source);
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, file);
            TestHarness.CheckTrue(file + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors.Take(3)));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            return context;
        }

        private static MirFunction FnOf(MwContext context, string needle)
        {
            var found = context.Mir!.Functions.FirstOrDefault(f => f.Symbol.Canonical.Contains(needle));
            TestHarness.CheckTrue("找到函数 " + needle, found != null,
                string.Join(", ", context.Mir.Functions.Select(f => f.Symbol.Canonical)));
            return found!;
        }

    }
}
