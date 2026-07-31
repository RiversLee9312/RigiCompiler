using System.Collections.Generic;
using System.Linq;
using System.Text;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S6 P4 最小闭环 + S7a 基础发射补齐测试：Lowerer（P4a 恒等重写）+
    /// BilEmitter（P4b 发射）端到端。
    /// 覆盖：全管线无诊断、黄金输出逐行精确比对（LocalSymbols 符号段 /
    /// Resources 字面量提取 / println 与 main 两个 fn 定义）、Origin 调试链
    /// （BilInstruction.Origin → LoweredNode.Origin → BoundNode.Syntax → Span）、
    /// 资源去重；S7a 新增：局部声明 + 初始化器（set.var）、赋值、二元运算
    /// （算术 + 比较）、一元运算、带返回值 invoke、表达式语句（结果丢弃）、
    /// new 构造、§18.1 标量资源全形态（bool/f64/f32/char/null）、static 字段
    /// 读写（get/set.field.static）；负例改为实例方法（P4 Error + 跳过 fn）。
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
            TestLocalDeclarationAndAssignment();
            TestUnaryAndComparison();
            TestInvokeWithResult();
            TestNew();
            TestLiteralResources();
            TestStaticFieldReadWrite();
            TestIfStatementEmission();
            TestIfExpressionEmission();
            TestShortCircuitEmission();
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

        // ===== S7a 渲染辅助：fn 的 .vars + entry block 指令精确比对 =====

        private static BilFunction FnOf(BilModule module, string symbol)
        {
            return module.Functions.Single(f => f.Symbol == symbol);
        }

        private static string RenderFn(BilFunction function)
        {
            var sb = new StringBuilder();
            sb.Append(".vars { ");
            sb.Append(string.Join(", ", function.Vars.Select(v => $"{v.TypeRef} {v.Name}")));
            sb.Append(" }\n");
            foreach (var instruction in function.Blocks[0].Instructions)
            {
                sb.Append(instruction.Opcode);
                foreach (var operand in instruction.Operands)
                {
                    sb.Append(" " + operand.Render());
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string RenderResources(BilModule module)
        {
            return string.Join("\n", module.Resources.Select(r => r switch
            {
                BilScalarResource s => $"{s.Name} = {s.TypeKeyword} {s.LiteralText}",
                BilNullResource n => $"{n.Name} = null type({n.TypeRef})",
                _ => $"<{r.GetType().Name}>",
            }));
        }

        // S7b 渲染辅助：fn 的 .vars + 全部 block 指令精确比对（多 block 用）
        private static string RenderFnAllBlocks(BilFunction function)
        {
            var sb = new StringBuilder();
            sb.Append(".vars { ");
            sb.Append(string.Join(", ", function.Vars.Select(v => $"{v.TypeRef} {v.Name}")));
            sb.Append(" }\n");
            foreach (var block in function.Blocks)
            {
                sb.Append($".block {block.Id}");
                if (block.Modifiers.Count > 0)
                {
                    sb.Append(" " + string.Join(" ", block.Modifiers));
                }
                sb.Append(" {\n");
                foreach (var instruction in block.Instructions)
                {
                    sb.Append(instruction.Opcode);
                    foreach (var operand in instruction.Operands)
                    {
                        sb.Append(" " + operand.Render());
                    }
                    sb.Append('\n');
                }
                sb.Append("}\n");
            }
            return sb.ToString();
        }

        // ===== 局部声明 + 初始化器（set.var）与赋值 =====
        private static void TestLocalDeclarationAndAssignment()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（声明与赋值）", unit);
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .i32 x, .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3, .i32 .t4 }\n" +
                "load res(R_1) $.t0\n" +
                "load res(R_2) $.t1\n" +
                "add $.t0 $.t1 $.t2\n" +
                "set.var $.t2 $x\n" +
                "load res(R_3) $.t3\n" +
                "mul $x $.t3 $.t4\n" +
                "set.var $.t4 $x\n" +
                "ret $x\n");
        }

        // ===== 一元运算与比较运算（同字面量资源去重）=====
        private static void TestUnaryAndComparison()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 5\n" +
                "    var n: i32 = -a\n" +
                "    var b: bool = n == 5\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（一元与比较）", unit);
            // 两处 5 共用同一资源 R_1
            TestHarness.Check("资源（5 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 5");
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .i32 a, .i32 n, .bool b, .i32 .t0, .i32 .t1, .i32 .t2, .bool .t3 }\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $a\n" +
                "opposite $a $.t1\n" +
                "set.var $.t1 $n\n" +
                "load res(R_1) $.t2\n" +
                "cmp.eq $n $.t2 $.t3\n" +
                "set.var $.t3 $b\n" +
                "ret $n\n");
        }

        // ===== 带返回值 invoke（§15.1）与表达式语句（结果物化后丢弃）=====
        private static void TestInvokeWithResult()
        {
            var (unit, module, _) = EmitUnit(
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    double(5)\n" +
                "    return double(21)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（invoke）", unit);
            TestHarness.Check("double 指令与 .vars",
                RenderFn(FnOf(module, "$double(a:.i32)@.i32")),
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(R_1) $.t0\n" +
                "mul $a $.t0 $.t1\n" +
                "ret $.t1\n");
            TestHarness.Check("main 指令与 .vars（表达式语句结果丢弃）",
                RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(R_2) $.t0\n" +
                "invoke fn($double(a:.i32)@.i32) $.t1 [$.t0]\n" +
                "load res(R_3) $.t2\n" +
                "invoke fn($double(a:.i32)@.i32) $.t3 [$.t2]\n" +
                "ret $.t3\n");
        }

        // ===== new 构造（§14.1；零参无显式 init）=====
        private static void TestNew()
        {
            var (unit, module, _) = EmitUnit(
                "pub class Empty { }\n" +
                "pub func main(): i32 {\n" +
                "    var e: Empty = new Empty()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（new）", unit);
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { Empty e, Empty .t0, .i32 .t1 }\n" +
                "new type(Empty) $.t0 []\n" +
                "set.var $.t0 $e\n" +
                "load res(R_1) $.t1\n" +
                "ret $.t1\n");
        }

        // ===== §18.1 标量资源全形态（bool/f64/f32/char/null）=====
        private static void TestLiteralResources()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var b: bool = true\n" +
                "    var d: double = 0.5\n" +
                "    var f: float = 0.1f\n" +
                "    var c: char = 'A'\n" +
                "    var s: String? = null\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（标量资源）", unit);
            TestHarness.Check("Resources 全形态", RenderResources(module),
                "R_0 = string \"\\n\"\n" +
                "R_1 = bool true\n" +
                "R_2 = f64 0.5\n" +
                "R_3 = f32 0.1\n" +
                "R_4 = char 'A'\n" +
                "R_5 = null type(.string)\n" +
                "R_6 = i32 0");
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .bool b, .f64 d, .f32 f, .char c, .nullable<.string> s, " +
                ".bool .t0, .f64 .t1, .f32 .t2, .char .t3, .nullable<.string> .t4, .i32 .t5 }\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $b\n" +
                "load res(R_2) $.t1\n" +
                "set.var $.t1 $d\n" +
                "load res(R_3) $.t2\n" +
                "set.var $.t2 $f\n" +
                "load res(R_4) $.t3\n" +
                "set.var $.t3 $c\n" +
                "load res(R_5) $.t4\n" +
                "set.var $.t4 $s\n" +
                "load res(R_6) $.t5\n" +
                "ret $.t5\n");
        }

        // ===== static 字段读写（§13.4 get/set.field.static）=====
        private static void TestStaticFieldReadWrite()
        {
            var (unit, module, _) = EmitUnit(
                "pub class Counter { pub static var value: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    Counter.value = 42\n" +
                "    return Counter.value\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（static 字段）", unit);
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(R_1) $.t0\n" +
                "set.field.static $.t0 type(Counter) field(Counter#.static.value@.i32)\n" +
                "get.field.static $.t1 type(Counter) field(Counter#.static.value@.i32)\n" +
                "ret $.t1\n");
        }

        // ===== S7b：if 语句 → 多 block（§16.2 结构化条件）=====
        private static void TestIfStatementEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    if (x == 0) {\n" +
                "        x = 1\n" +
                "    } else {\n" +
                "        x = 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（if 语句发射）", unit);
            // 两处 0 共用同一资源（去重键 = (i32, "0")）
            TestHarness.Check("资源（0 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = i32 1\nR_3 = i32 2");
            TestHarness.Check("if 语句多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .i32 x, .i32 .t0, .i32 .t1, .bool .t2, .i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $x\n" +
                "load res(R_1) $.t1\n" +
                "cmp.eq $x $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "ret $x\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(R_2) $.t3\n" +
                "set.var $.t3 $x\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_3) $.t4\n" +
                "set.var $.t4 $x\n" +
                "}\n");
            // 结构性事实：block id 函数内唯一且恰一个 entrypoint（§9.4）
            var main = FnOf(module, "$main()@.i32");
            TestHarness.CheckTrue("恰一个 entrypoint block 且 id 唯一",
                main.Blocks.Count(b => b.Modifiers.Contains("entrypoint")) == 1
                && main.Blocks.Select(b => b.Id).Distinct().Count() == main.Blocks.Count);

            // 无 else → none 操作数
            var (unit2, module2, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    if (x == 0) { x = 1 }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（无 else if）", unit2);
            var ifInstruction = FnOf(module2, "$main()@.i32").Blocks
                .SelectMany(b => b.Instructions).Single(i => i.Opcode == "if");
            TestHarness.CheckTrue("无 else 用 none 操作数",
                ifInstruction.Operands.Count == 3
                && ifInstruction.Operands[2] is BilNoneOperand
                && ifInstruction.Operands[1].Render() == "blk(if0-then)");
            TestHarness.CheckTrue("无 else 不产 else block",
                FnOf(module2, "$main()@.i32").Blocks.All(b => b.Id != "if0-else"));
        }

        // ===== S7b：if 表达式 → 结果局部 + 分支块写值 =====
        private static void TestIfExpressionEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    var r = if ((x > 0)) { return@_ 1 } else { return@_ 2 }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（if 表达式发射）", unit);
            // 分支里的 1 与 x 初始化器 1 同键去重
            TestHarness.Check("资源（1 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 1\nR_2 = i32 0\nR_3 = i32 2");
            TestHarness.Check("if 表达式多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .i32 x, .i32 r, .i32 .s0, .i32 .t0, .i32 .t1, .bool .t2, " +
                ".i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $x\n" +
                "load res(R_2) $.t1\n" +
                "cmp.gt $x $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $r\n" +
                "ret $r\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(R_1) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_3) $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "}\n");
        }

        // ===== S7b：短路 and/or → §11.3 if 展开（无裸 and/or 指令）=====
        private static void TestShortCircuitEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var a: bool = true\n" +
                "    var b: bool = false\n" +
                "    var c = (a and b)\n" +
                "    var d = (a or b)\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（短路发射）", unit);
            TestHarness.CheckTrue("无裸 and/or 指令（§11.3 已展开）",
                FnOf(module, "$main()@.i32").Blocks.SelectMany(b => b.Instructions)
                    .All(i => i.Opcode != "and" && i.Opcode != "or"),
                string.Join(", ", FnOf(module, "$main()@.i32").Blocks
                    .SelectMany(b => b.Instructions).Select(i => i.Opcode)));
            // 合成常量与源码字面量同键去重：true/false 各一个资源
            TestHarness.Check("资源（true/false 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = bool true\nR_2 = bool false\nR_3 = i32 0");
            TestHarness.Check("短路 and/or 多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .bool a, .bool b, .bool c, .bool d, .bool .s0, .bool .s1, " +
                ".bool .t0, .bool .t1, .bool .t2, .bool .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $a\n" +
                "load res(R_2) $.t1\n" +
                "set.var $.t1 $b\n" +
                "if $a blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $c\n" +
                "if $a blk(if1-then) blk(if1-else)\n" +
                "set.var $.s1 $d\n" +
                "load res(R_3) $.t4\n" +
                "ret $.t4\n" +
                "}\n" +
                ".block if0-then {\n" +
                "set.var $b $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_2) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "}\n" +
                ".block if1-then {\n" +
                "load res(R_1) $.t3\n" +
                "set.var $.t3 $.s1\n" +
                "}\n" +
                ".block if1-else {\n" +
                "set.var $b $.s1\n" +
                "}\n");
        }

        // ===== 负例：实例方法 → P4 Error + 跳过该 fn 定义 =====
        // （S7a 后 P3 能产出的节点已全部过 P4a/P4b，源码侧剩余的未覆盖发射
        // 形态是实例方法的 .this receiver）
        private static void TestUnsupportedNodes()
        {
            var (unit, module, _) = EmitUnit(
                "pub class Foo { pub func Bar() { } }\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("实例方法报 P4 Error（.this receiver）",
                unit.Diagnostics, "requires .this receiver");
            // Foo.Bar 被跳过（无 fn 定义）；main 与 stdlib println 不受影响
            TestHarness.CheckTrue("跳过实例方法 fn 定义",
                module.Functions.All(f => !f.Symbol.StartsWith("Foo$Bar")));
            TestHarness.CheckTrue("main 仍正常发射",
                module.Functions.Any(f => f.Symbol == "$main()@.i32"));
            TestHarness.CheckTrue("stdlib println 仍正常发射",
                module.Functions.Any(f => f.Symbol == "core.io::Console$.static.println(text:.string)@.void"));
        }
    }
}
