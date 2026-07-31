using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S6 P4 最小闭环测试（M44）：Lowerer（P4a 恒等重写）+ BilEmitter（P4b 发射）
    /// 端到端——hello world 从 BoundTree 走到合法 BIL 文本。
    /// 覆盖：全管线无诊断、黄金输出逐行精确比对（LocalSymbols 符号段 /
    /// Resources 字面量提取 / println 与 main 两个 fn 定义）、Origin 调试链
    /// （BilInstruction.Origin → LoweredNode.Origin → BoundNode.Syntax → Span）、
    /// 资源去重、未覆盖节点负例（二元运算 → P4 Error + 跳过函数体）。
    /// </summary>
    public static class BilEmitterTests
    {
        // 用户源文件名（Origin 链断言 sourceName 用）
        private const string UserSourceName = "hello.latte";

        private const string HelloWorldSource =
            "pub func main(): i32 {\n" +
            "    core.io.Console.println(\"Hello, world!\")\n" +
            "    return 0\n" +
            "}\n";

        public static int RunAll()
        {
            TestHarness.Reset();
            TestHarness.Section("BilEmitter");

            TestGoldenOutput();
            TestOriginChain();
            TestResourceDeduplication();
            TestUnsupportedNodes();

            return TestHarness.Summary("BilEmitter");
        }

        // 全管线：stdlib（在前）+ 用户源组 CompilationUnit → P1 → P2 → P3
        // → P4a → P4b → BilWriter 文本
        private static (CompilationUnit Unit, BilModule Module, string Text) EmitUnit(
            string userSource, string moduleName = "hello")
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.Add(TestHarness.ParseRoot(userSource, UserSourceName));
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            var lowered = Lowerer.Lower(unit, bodies);
            var module = BilEmitter.Emit(unit, lowered, moduleName);
            return (unit, module, BilWriter.Write(module));
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }

        // ===== hello world 黄金输出（逐行精确比对）=====
        private static void TestGoldenOutput()
        {
            var (unit, _, text) = EmitUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断", unit);
            TestHarness.Check("hello world 黄金输出", text, TestHarness.Lines(
                "BIL \"1.1\"",
                "",
                "Metadata {",
                "    module = string \"hello\"",
                "}",
                "",
                "Resources {",
                "    R_0 = string \"\\n\",",
                "    R_1 = string \"Hello, world!\",",
                "    R_2 = i32 0",
                "}",
                "",
                "LocalSymbols {",
                "    .type core.io::Console = class pub {",
                "        .static-method core.io::Console$.static.print(text:.string)@.void priv native symbol(\"print\") lib(\"latte_rt\")",
                "        .static-method core.io::Console$.static.printErr(text:.string)@.void priv native symbol(\"printErr\") lib(\"latte_rt\")",
                "        .static-method core.io::Console$.static.println(text:.string)@.void pub",
                "    }",
                "    .method $main()@.i32 pub entrypoint",
                "}",
                "",
                "ExternalSymbols {",
                "}",
                "",
                "fn(core.io::Console$.static.println(text:.string)@.void) {",
                "    .args {",
                "        .return = .void,",
                "        text = .string",
                "    }",
                "",
                "    .vars {",
                "        .string .t0",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        invoke.noret fn(core.io::Console$.static.print(text:.string)@.void) [$text]",
                "        load res(R_0) $.t0",
                "        invoke.noret fn(core.io::Console$.static.print(text:.string)@.void) [$.t0]",
                "        ret",
                "    }",
                "}",
                "",
                "fn($main()@.i32) {",
                "    .args {",
                "        .return = .i32",
                "    }",
                "",
                "    .vars {",
                "        .string .t0,",
                "        .i32 .t1",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        load res(R_1) $.t0",
                "        invoke.noret fn(core.io::Console$.static.println(text:.string)@.void) [$.t0]",
                "        load res(R_2) $.t1",
                "        ret $.t1",
                "    }",
                "}"));
        }

        // ===== Origin 调试链（ARCHITECTURE §6.3）=====
        private static void TestOriginChain()
        {
            var (unit, module, _) = EmitUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断（Origin 链）", unit);

            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var invoke = main.Blocks[0].Instructions.First(i => i.Opcode == "invoke.noret");
            TestHarness.CheckTrue("invoke.noret 的 Origin 是 LoweredCallStatement",
                invoke.Origin is LoweredCallStatement,
                invoke.Origin?.GetType().Name ?? "<null>");
            var lowered = invoke.Origin as LoweredCallStatement;
            TestHarness.CheckTrue("LoweredCallStatement.Origin 是 BoundCallStatement",
                lowered?.Origin is BoundCallStatement,
                lowered?.Origin.GetType().Name ?? "<null>");
            var bound = lowered?.Origin as BoundCallStatement;
            TestHarness.CheckTrue("BoundCallStatement.Syntax 非空", bound?.Syntax != null);
            TestHarness.CheckTrue("Syntax.Span 非空", bound?.Syntax.Span != null);
            TestHarness.Check("Syntax.Span.sourceName 是用户文件名",
                bound?.Syntax.Span?.sourceName ?? "<null>", UserSourceName);

            // load 的 Origin 是字面量 LoweredNode（值经 Origin.Syntax 回取）
            var load = main.Blocks[0].Instructions.First(i => i.Opcode == "load");
            TestHarness.CheckTrue("load 的 Origin 是 LoweredLiteralExpression",
                load.Origin is LoweredLiteralExpression,
                load.Origin?.GetType().Name ?? "<null>");
        }

        // ===== 资源去重：同（类型, 原文）字面量只登记一次 =====
        private static void TestResourceDeduplication()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"same\")\n" +
                "    core.io.Console.println(\"same\")\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（资源去重）", unit);
            // R_0 = "\n"（stdlib println）、R_1 = "same"、R_2 = 0——"same" 不重复登记
            TestHarness.CheckTrue("相同字面量只登记一个资源",
                module.Resources.Count == 3
                && module.Resources.Count(r => r is BilScalarResource s
                    && s.LiteralText == "\"same\"") == 1,
                string.Join(", ", module.Resources.Select(r => r.Name)));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var loads = main.Blocks[0].Instructions.Where(i => i.Opcode == "load").ToList();
            // main 共三条 load（两次 "same" + return 0），两条指向同一资源 R_1
            TestHarness.CheckTrue("两处引用同一资源（R_1）",
                loads.Count == 3 && loads.Count(l => l.Operands[0] is BilResourceOperand ro
                    && ro.ResourceId == "R_1") == 2);
        }

        // ===== 负例：未覆盖节点 → P4 Error + 跳过该函数体 =====
        private static void TestUnsupportedNodes()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    return (1 + 2)\n" +
                "}\n");
            TestHarness.CheckSemanticError("二元运算报 P4 Error（not supported）",
                unit.Diagnostics, "not supported by minimal lowering");
            // main 被跳过（不产出 fn 定义）；stdlib println 不受影响（诊断互不阻断）
            TestHarness.CheckTrue("跳过未覆盖函数体（main 无 fn 定义）",
                module.Functions.All(f => f.Symbol != "$main()@.i32"));
            TestHarness.CheckTrue("stdlib println 仍正常发射",
                module.Functions.Any(f => f.Symbol == "core.io::Console$.static.println(text:.string)@.void"));
        }
    }
}
