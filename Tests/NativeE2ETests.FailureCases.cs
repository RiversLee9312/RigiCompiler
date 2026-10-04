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
        // FailureCases 职责；与主文件共享同一类型、字段及生命周期。

        // 失败对拍（MW9b-G 起两侧同为真异常未捕获出口）：VM 抛语言级
        // 异常（消息含关键字）、native 由顶层 reporter 打印
        // 「{类型全名}: {message}」——stderr 关键字对齐（nativeNeedle 缺省
        // 同 keyword；新格式全名前缀经 nativeNeedle 单断）、native 退出
        // 码 1 对齐 vm 命令未捕获异常出口、stdout 一致
        private static void RunFailCase(string label, string source, string keyword,
            string? nativeNeedle, IReadOnlyDictionary<string, string>? env = null)
        {
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);

                // VM 侧：应有未捕获语言级异常，消息含关键字
                var vm = BilVm.Run(BilReader.Read(text));
                TestHarness.CheckTrue(label + "：VM 有异常", vm.Exception != null);
                TestHarness.CheckTrue(label + "：VM 消息含关键字",
                    vm.Exception != null && vm.Exception.Message.Contains(keyword),
                    vm.Exception?.Message ?? "");

                // native 侧：编译链接应成功（guard 是合法 IR），运行退出码 1
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

        // MW10 刀5 ④：A↔B init 互引 → 急切初始化期构造环。VM 侧：环
        // 异常是基础设施级 VmException（VmContext.SingletonCycleException），
        // 从 InitializeSingletons 经 BilVm.Run 直接抛出（无 BilVmResult
        // 通道，RunFailCase 不适用）；native：get fn 在途检测抛
        // core::RuntimeException 未捕获 → reporter（"{类型全名}: {message}"）
        // → exit 1。已知分歧（以 VM 为准）：VM 消息带在途栈全链
        //（"A → B → A"），native v1 静态槽形态无在途链对象，只报触发类型
        private static void RunSingletonCycleCase(
            IReadOnlyDictionary<string, string>? env = null)
        {
            const string label = "singleton 构造环抛异常（急切初始化期）";
            var source =
                "pub shared singleton class A {\n" +
                "    pub var b: i32\n" +
                "    pub init() { b = new B().value }\n" +
                "}\n" +
                "pub shared singleton class B {\n" +
                "    pub var value: i32\n" +
                "    pub init() { value = new A().b }\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n";
            var dir = Path.Combine(Path.GetTempPath(), $"rigi_e2e_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                var module = EmitNativeSource(source, label);
                var text = BilWriter.Write(module);

                // VM 侧：构造环在急切初始化期以 VmException 炸出
                VmException? cycle = null;
                try
                {
                    BilVm.Run(BilReader.Read(text));
                }
                catch (VmException ex)
                {
                    cycle = ex;
                }
                TestHarness.CheckTrue(label + "：VM 抛构造环异常", cycle != null);
                TestHarness.CheckTrue(label + "：VM 消息含循环链前缀",
                    cycle != null && cycle.Message.Contains("singleton 初始化循环依赖"),
                    cycle?.Message ?? "");

                // native 侧：编译链接成功，运行 exit 1 + stderr 同前缀
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
                TestHarness.CheckTrue(label + "：native 退出码 1", runExit == 1,
                    $"exit={runExit} stderr={nativeErr}");
                TestHarness.CheckTrue(label + "：native stderr 含循环链前缀",
                    nativeErr.Contains("singleton 初始化循环依赖"), nativeErr);
                TestHarness.CheckTrue(label + "：native stdout 为空（与 VM 一致）",
                    NormalizeNewlines(nativeOut) == "", nativeOut);
            }
            finally
            {
                DeleteNativeTestDirectory(dir);
            }
        }

        // 前端 take(nums) 会把包再装箱成单元素；手改 invoke 整包转发后对拍
        private static void RunPackForwardCase()
        {
            var module = EmitNativeSource(
                "import core.io.Console\n" +
                "func take(nums: i32...): i32 { return nums.length }\n" +
                "func wrap(nums: i32...): i32 { return take(nums) }\n" +
                "pub func main(): i32 {\n" +
                "    if (wrap(1, 2, 3) == 3) { Console.println(\"fwd\") }\n" +
                "    return 0\n" +
                "}\n");
            var wrap = module.Functions.Single(f => f.Symbol == "$wrap()@.i32");
            var entry = wrap.Blocks.Single(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint));
            var invoke = entry.Instructions.OfType<InvokeInstruction>().Single();
            entry.Instructions.Clear();
            entry.Instructions.Add(new InvokeInstruction(invoke.Method, invoke.Target,
                new[] { new BilVariableOperand(".vargs.nums") }));
            entry.Instructions.Add(new RetInstruction(invoke.Target));
            RunBilCase("包转发（整包）", BilWriter.Write(module));
        }

    }
}
