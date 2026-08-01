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
    /// S7b：if 语句/表达式与短路 and/or 的多 block 黄金文本（§16.2 if
    /// 指令、none 操作数、if0-then 形态 block id、分支块落尾不补 ret、
    /// 合成 bool 常量同键去重）。
    /// S7c-1：while/do-while 的 loop/loop.rev 发射（§16.3/§16.4 三 block
    /// 黄金文本与操作数序 cond/body/none/judge/breakid、loop0-body/
    /// loop0-judge 块 id 递增）、break/continue（§16.5 嵌套标签命中外层
    /// breakid）、.vars 的 .breakid 条目（§9.3）。
    /// S7d：常量 switch 的 switch 指令发射（§16.6 操作数序 selector/
    /// res(表)/[blk item 表]/blk(default)/breakid、switch0-item0/
    /// switch0-default 块 id、§18.4 switch-table 单行资源与跨 fn 同表
    /// 去重）、pattern switch 不到 P4b（P4a 已降为 if 链——无 switch
    /// 指令与表资源）、throw（§16.9 单操作数、entry 块 throw 终止不补 ret）。
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
            TestLoopEmission();
            TestInstanceEmission();
            TestForLoopEmission();
            TestSwitchEmission();
            TestPatternSwitchEmission();
            TestThrowEmission();
            TestCastEmission();
            TestStringInterpolationEmission();
            TestSafeAccessEmission();
            TestNullFallbackEmission();
            TestDestructuringEmission();
            TestTryEmission();
            TestSeqEmission();
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
                "    R_1 = i32 0,",
                "    R_2 = bool false,",
                "    R_3 = i32 1,",
                "    R_4 = bool true,",
                "    R_5 = string \"Hello, world!\"",
                "}",
                "",
                "LocalSymbols {",
                "    .type core::Pair = class pub open {",
                "        .field core::Pair#key@.generic<$.generic.TKey> pub",
                "        .field core::Pair#value@.generic<$.generic.TValue> pub",
                "    }",
                "    .type core.io::Console = class pub {",
                "        .static-method core.io::Console$.static.print(text:.string)@.void priv native symbol(\"print\") lib(\"latte_rt\")",
                "        .static-method core.io::Console$.static.printErr(text:.string)@.void priv native symbol(\"printErr\") lib(\"latte_rt\")",
                "        .static-method core.io::Console$.static.println(text:.string)@.void pub",
                "    }",
                "    .type core.collections::IEnumerator = interface pub {",
                "        .method core.collections::IEnumerator$moveNext()@.bool priv",
                "        .method core.collections::IEnumerator$current()@.generic<$.generic.T> priv",
                "    }",
                "    .type core.collections::IEnumerable = interface pub {",
                "        .method core.collections::IEnumerable$iterate()@core.collections::IEnumerator<.generic<$.generic.T>> priv",
                "    }",
                "    .type core.collections::RangeEnumeratorI32 = class",
                "        implements core.collections::IEnumerator<.i32>",
                "        pub {",
                "        .field core.collections::RangeEnumeratorI32#start_@.i32 priv",
                "        .field core.collections::RangeEnumeratorI32#end_@.i32 priv",
                "        .field core.collections::RangeEnumeratorI32#value_@.i32 priv",
                "        .field core.collections::RangeEnumeratorI32#started_@.bool priv",
                "        .method core.collections::RangeEnumeratorI32$init(start:.i32,end:.i32)@.void pub init",
                "        .method core.collections::RangeEnumeratorI32$moveNext()@.bool pub",
                "        .method core.collections::RangeEnumeratorI32$current()@.i32 pub",
                "    }",
                "    .type core.collections::RangeI32 = class",
                "        implements core.collections::IEnumerable<.i32>",
                "        pub {",
                "        .field core.collections::RangeI32#start_@.i32 priv",
                "        .field core.collections::RangeI32#end_@.i32 priv",
                "        .method core.collections::RangeI32$init(start:.i32,end:.i32)@.void pub init",
                "        .method core.collections::RangeI32$iterate()@core.collections::IEnumerator<.i32> pub",
                "    }",
                "    .method $main()@.i32 pub entrypoint",
                "    .method core::i32$$EnumerateInRange(end:.i32)@core.collections::IEnumerable<.i32> pub ext operator(EnumerateInRange)",
                "}",
                "",
                "ExternalSymbols {",
                "}",
                "",
                "fn(core::i32$$EnumerateInRange(end:.i32)@core.collections::IEnumerable<.i32>) {",
                "    .args {",
                "        .return = core.collections::IEnumerable<.i32>,",
                "        .this = .i32,",
                "        end = .i32",
                "    }",
                "",
                "    .vars {",
                "        core.collections::RangeI32 .t0,",
                "        core.collections::IEnumerable<.i32> .t1",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        new type(core.collections::RangeI32) $.t0 [$.this, $end]",
                "        cast $.t0 $.t1 type(core.collections::IEnumerable<.i32>)",
                "        ret $.t1",
                "    }",
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
                "fn(core.collections::RangeEnumeratorI32$init(start:.i32,end:.i32)@.void) {",
                "    .args {",
                "        .return = .void,",
                "        .this = core.collections::RangeEnumeratorI32,",
                "        start = .i32,",
                "        end = .i32",
                "    }",
                "",
                "    .vars {",
                "        .i32 .t0,",
                "        .bool .t1",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        set.field $start $.this field(core.collections::RangeEnumeratorI32#start_@.i32)",
                "        set.field $end $.this field(core.collections::RangeEnumeratorI32#end_@.i32)",
                "        load res(R_1) $.t0",
                "        set.field $.t0 $.this field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "        load res(R_2) $.t1",
                "        set.field $.t1 $.this field(core.collections::RangeEnumeratorI32#started_@.bool)",
                "        ret",
                "    }",
                "}",
                "",
                "fn(core.collections::RangeEnumeratorI32$moveNext()@.bool) {",
                "    .args {",
                "        .return = .bool,",
                "        .this = core.collections::RangeEnumeratorI32",
                "    }",
                "",
                "    .vars {",
                "        .bool .t0,",
                "        .i32 .t1,",
                "        .i32 .t2,",
                "        .i32 .t3,",
                "        .i32 .t4,",
                "        .i32 .t5,",
                "        .bool .t6,",
                "        .i32 .t7,",
                "        .i32 .t8,",
                "        .bool .t9",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        get.field $.this $.t0 field(core.collections::RangeEnumeratorI32#started_@.bool)",
                "        if $.t0 blk(if0-then) blk(if0-else)",
                "        get.field $.this $.t7 field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "        get.field $.this $.t8 field(core.collections::RangeEnumeratorI32#end_@.i32)",
                "        cmp.lt $.t7 $.t8 $.t9",
                "        ret $.t9",
                "    }",
                "",
                "    .block if0-then {",
                "        get.field $.this $.t1 field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "        load res(R_3) $.t2",
                "        add $.t1 $.t2 $.t3",
                "        set.field $.t3 $.this field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "        get.field $.this $.t4 field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "    }",
                "",
                "    .block if0-else {",
                "        get.field $.this $.t5 field(core.collections::RangeEnumeratorI32#start_@.i32)",
                "        set.field $.t5 $.this field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "        load res(R_4) $.t6",
                "        set.field $.t6 $.this field(core.collections::RangeEnumeratorI32#started_@.bool)",
                "    }",
                "}",
                "",
                "fn(core.collections::RangeEnumeratorI32$current()@.i32) {",
                "    .args {",
                "        .return = .i32,",
                "        .this = core.collections::RangeEnumeratorI32",
                "    }",
                "",
                "    .vars {",
                "        .i32 .t0",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        get.field $.this $.t0 field(core.collections::RangeEnumeratorI32#value_@.i32)",
                "        ret $.t0",
                "    }",
                "}",
                "",
                "fn(core.collections::RangeI32$init(start:.i32,end:.i32)@.void) {",
                "    .args {",
                "        .return = .void,",
                "        .this = core.collections::RangeI32,",
                "        start = .i32,",
                "        end = .i32",
                "    }",
                "",
                "    .vars {",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        set.field $start $.this field(core.collections::RangeI32#start_@.i32)",
                "        set.field $end $.this field(core.collections::RangeI32#end_@.i32)",
                "        ret",
                "    }",
                "}",
                "",
                "fn(core.collections::RangeI32$iterate()@core.collections::IEnumerator<.i32>) {",
                "    .args {",
                "        .return = core.collections::IEnumerator<.i32>,",
                "        .this = core.collections::RangeI32",
                "    }",
                "",
                "    .vars {",
                "        .i32 .t0,",
                "        .i32 .t1,",
                "        core.collections::RangeEnumeratorI32 .t2,",
                "        core.collections::IEnumerator<.i32> .t3",
                "    }",
                "",
                "    .block entry entrypoint {",
                "        get.field $.this $.t0 field(core.collections::RangeI32#start_@.i32)",
                "        get.field $.this $.t1 field(core.collections::RangeI32#end_@.i32)",
                "        new type(core.collections::RangeEnumeratorI32) $.t2 [$.t0, $.t1]",
                "        cast $.t2 $.t3 type(core.collections::IEnumerator<.i32>)",
                "        ret $.t3",
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
                "        load res(R_5) $.t0",
                "        invoke.noret fn(core.io::Console$.static.println(text:.string)@.void) [$.t0]",
                "        load res(R_1) $.t1",
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
            // stdlib 基线 R_0..R_4（"\n"/0/false/1/true）+ R_5 = "same"——
            // "same" 不重复登记；return 0 共享 R_1
            TestHarness.CheckTrue("相同字面量只登记一个资源",
                module.Resources.Count == 6
                && module.Resources.Count(r => r is BilScalarResource s
                    && s.LiteralText == "\"same\"") == 1,
                string.Join(", ", module.Resources.Select(r => r.Name)));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var loads = main.Blocks[0].Instructions.Where(i => i.Opcode == "load").ToList();
            // main 共三条 load（两次 "same" + return 0），两条指向同一资源 R_5
            TestHarness.CheckTrue("两处引用同一资源（R_5）",
                loads.Count == 3 && loads.Count(l => l.Operands[0] is BilResourceOperand ro
                    && ro.ResourceId == "R_5") == 2);
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
                // 复合资源单行形态（§18.4 switch-table；map/catch-table 多行
                // 形态随 S7e/S7f 发射端到位再扩）
                BilCollectionResource c =>
                    $"{c.Name} = {c.Header} {{ {string.Join(", ", c.Elements)} }}",
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
                "load res(R_3) $.t0\n" +
                "load res(R_5) $.t1\n" +
                "add $.t0 $.t1 $.t2\n" +
                "set.var $.t2 $x\n" +
                "load res(R_6) $.t3\n" +
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
            // 两处 5 共用同一资源 R_5（R_0..R_4 为 stdlib 基线）
            TestHarness.Check("资源（5 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true\nR_5 = i32 5");
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .i32 a, .i32 n, .bool b, .i32 .t0, .i32 .t1, .i32 .t2, .bool .t3 }\n" +
                "load res(R_5) $.t0\n" +
                "set.var $.t0 $a\n" +
                "opposite $a $.t1\n" +
                "set.var $.t1 $n\n" +
                "load res(R_5) $.t2\n" +
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
                "load res(R_5) $.t0\n" +
                "mul $a $.t0 $.t1\n" +
                "ret $.t1\n");
            TestHarness.Check("main 指令与 .vars（表达式语句结果丢弃）",
                RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(R_6) $.t0\n" +
                "invoke fn($double(a:.i32)@.i32) $.t1 [$.t0]\n" +
                "load res(R_7) $.t2\n" +
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
                "R_1 = i32 0\n" +
                "R_2 = bool false\n" +
                "R_3 = i32 1\n" +
                "R_4 = bool true\n" +
                "R_5 = f64 0.5\n" +
                "R_6 = f32 0.1\n" +
                "R_7 = char 'A'\n" +
                "R_8 = null type(.string)");
            TestHarness.Check("main 指令与 .vars", RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { .bool b, .f64 d, .f32 f, .char c, .nullable<.string> s, " +
                ".bool .t0, .f64 .t1, .f32 .t2, .char .t3, .nullable<.string> .t4, .i32 .t5 }\n" +
                "load res(R_4) $.t0\n" +
                "set.var $.t0 $b\n" +
                "load res(R_5) $.t1\n" +
                "set.var $.t1 $d\n" +
                "load res(R_6) $.t2\n" +
                "set.var $.t2 $f\n" +
                "load res(R_7) $.t3\n" +
                "set.var $.t3 $c\n" +
                "load res(R_8) $.t4\n" +
                "set.var $.t4 $s\n" +
                "load res(R_1) $.t5\n" +
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
                "load res(R_5) $.t0\n" +
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
            // 两处 0 共用同一资源 R_1（R_0..R_4 为 stdlib 基线；2 新增 R_5）
            TestHarness.Check("资源（0 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true\nR_5 = i32 2");
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
                "load res(R_3) $.t3\n" +
                "set.var $.t3 $x\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_5) $.t4\n" +
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
            // 分支里的 1 与 x 初始化器 1 同键共享 R_3（stdlib 基线内）；
            // 0 共享 R_1；2 新增 R_5
            TestHarness.Check("资源（1 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true\nR_5 = i32 2");
            TestHarness.Check("if 表达式多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .i32 x, .i32 r, .i32 .s0, .i32 .t0, .i32 .t1, .bool .t2, " +
                ".i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_3) $.t0\n" +
                "set.var $.t0 $x\n" +
                "load res(R_1) $.t1\n" +
                "cmp.gt $x $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $r\n" +
                "ret $r\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(R_3) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_5) $.t4\n" +
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
            // 合成常量与源码字面量同键去重：true/false 分别共享 stdlib
            // 基线的 R_4/R_2；return 0 共享 R_1——资源零新增
            TestHarness.Check("资源（true/false 去重）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true");
            TestHarness.Check("短路 and/or 多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .bool a, .bool b, .bool c, .bool d, .bool .s0, .bool .s1, " +
                ".bool .t0, .bool .t1, .bool .t2, .bool .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_4) $.t0\n" +
                "set.var $.t0 $a\n" +
                "load res(R_2) $.t1\n" +
                "set.var $.t1 $b\n" +
                "if $a blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $c\n" +
                "if $a blk(if1-then) blk(if1-else)\n" +
                "set.var $.s1 $d\n" +
                "load res(R_1) $.t4\n" +
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
                "load res(R_4) $.t3\n" +
                "set.var $.t3 $.s1\n" +
                "}\n" +
                ".block if1-else {\n" +
                "set.var $b $.s1\n" +
                "}\n");
        }

        // ===== S7c-1：循环 → loop/loop.rev（§16.3/§16.4）+ break/continue（§16.5）=====
        private static void TestLoopEmission()
        {
            // while 端到端三 block 黄金文本（操作数序：cond、body、none、
            // judge、breakid）
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    while (x < 3) {\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（while 发射）", unit);
            TestHarness.Check("资源（while）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true\nR_5 = i32 3");
            TestHarness.Check("while 多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .i32 x, .bool .s0, .breakid .b0, .i32 .t0, .i32 .t1, " +
                ".i32 .t2, .i32 .t3, .bool .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $x\n" +
                "loop $.s0 blk(loop0-body) none blk(loop0-judge) $.b0\n" +
                "ret $x\n" +
                "}\n" +
                ".block loop0-body {\n" +
                "load res(R_3) $.t1\n" +
                "add $x $.t1 $.t2\n" +
                "set.var $.t2 $x\n" +
                "}\n" +
                ".block loop0-judge {\n" +
                "load res(R_5) $.t3\n" +
                "cmp.lt $x $.t3 $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "}\n");
            // 结构性事实：.vars 的 .breakid 条目（§9.3 别名投影）
            var mainFn = FnOf(module, "$main()@.i32");
            TestHarness.CheckTrue(".vars 含 .breakid 条目",
                mainFn.Vars.Any(v => v.TypeRef == ".breakid" && v.Name == ".b0"));

            // do-while → loop.rev（结构断言）
            var (unit2, module2, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < 3)\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（do-while 发射）", unit2);
            var revFn = FnOf(module2, "$main()@.i32");
            var revInstruction = revFn.Blocks.SelectMany(b => b.Instructions)
                .Single(i => i.Opcode == "loop.rev");
            TestHarness.CheckTrue("loop.rev 五操作数（cond/body/none/judge/breakid）",
                revInstruction.Operands.Count == 5
                && revInstruction.Operands[0].Render() == "$.s0"
                && revInstruction.Operands[1].Render() == "blk(loop0-body)"
                && revInstruction.Operands[2] is BilNoneOperand
                && revInstruction.Operands[3].Render() == "blk(loop0-judge)"
                && revInstruction.Operands[4].Render() == "$.b0");
            TestHarness.CheckTrue("loop.rev 产 body/judge block",
                revFn.Blocks.Any(b => b.Id == "loop0-body")
                && revFn.Blocks.Any(b => b.Id == "loop0-judge"));

            // 嵌套标签循环：break@outer 引用外层 breakid、continue 引用内层
            var (unit3, module3, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    while (x < 10) named outer {\n" +
                "        while (x < 5) {\n" +
                "            x = x + 1\n" +
                "            if (x == 3) { break@outer }\n" +
                "            continue\n" +
                "        }\n" +
                "        x = x + 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（嵌套标签循环发射）", unit3);
            var nestedFn = FnOf(module3, "$main()@.i32");
            var breakInstruction = nestedFn.Blocks.SelectMany(b => b.Instructions)
                .Single(i => i.Opcode == "break");
            var continueInstruction = nestedFn.Blocks.SelectMany(b => b.Instructions)
                .Single(i => i.Opcode == "continue");
            TestHarness.CheckTrue("break@outer → $.b0（外层 breakid）",
                breakInstruction.Operands.Count == 1
                && breakInstruction.Operands[0].Render() == "$.b0");
            TestHarness.CheckTrue("continue → $.b1（内层 breakid）",
                continueInstruction.Operands.Count == 1
                && continueInstruction.Operands[0].Render() == "$.b1");
            TestHarness.CheckTrue(".vars 含两个 .breakid 条目",
                nestedFn.Vars.Count(v => v.TypeRef == ".breakid") == 2);
            TestHarness.CheckTrue("嵌套循环 block id 递增（loop0/loop1）",
                nestedFn.Blocks.Any(b => b.Id == "loop1-body")
                && nestedFn.Blocks.Any(b => b.Id == "loop1-judge"));
        }

        // ===== S7c-2：实例成员发射（.this/实例 invoke/get.field/set.field/
        // init/operator 声明形态）=====
        private static void TestInstanceEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub init(v: i32) { value = v }\n" +
                "    pub func add(n: i32): i32 { return value + n }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Counter(1)\n" +
                "    return c.add(2)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（实例成员发射）", unit);

            // init/operator/ext 声明形态（§8.4）——init 是 Counter .type
            // 的成员（类型成员嵌在类型声明内）；ext operator 是顶层裸条目
            TestHarness.CheckTrue("init 声明形态（普通 canonical + init 修饰符）",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "Counter")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Keyword == ".method"
                        && d.Symbol == "Counter$init(v:.i32)@.void"
                        && d.Modifiers.Contains("init") && d.Modifiers.Contains("pub")));
            TestHarness.CheckTrue("ext operator 声明形态（$$名 + ext + operator(名)）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Keyword == ".method"
                    && d.Symbol == "core::i32$$EnumerateInRange(end:.i32)" +
                        "@core.collections::IEnumerable<.i32>"
                    && d.Modifiers.Contains("ext")
                    && d.Modifiers.Contains("operator(EnumerateInRange)")));

            // .this 进 .args（§9.2：.return 后、普通参数前；§7.3）
            var addFn = FnOf(module, "Counter$add(n:.i32)@.i32");
            TestHarness.CheckTrue("add 的 .args = [.return, .this, n]",
                addFn.Args.Count == 3
                && addFn.Args[0].Name == ".return"
                && addFn.Args[1].Name == ".this" && addFn.Args[1].TypeRef == "Counter"
                && addFn.Args[2].Name == "n");
            var initFn = FnOf(module, "Counter$init(v:.i32)@.void");
            TestHarness.CheckTrue("init 的 .args = [.return(.void), .this, v]",
                initFn.Args.Count == 3
                && initFn.Args[0].TypeRef == ".void"
                && initFn.Args[1].Name == ".this");

            // get.field/set.field（§13.3）与 void init 末尾补 ret
            TestHarness.Check("init 指令（set.field $v $.this）",
                RenderFn(initFn),
                ".vars {  }\n" +
                "set.field $v $.this field(Counter#value@.i32)\n" +
                "ret\n");
            TestHarness.Check("add 指令（get.field $.this + add）",
                RenderFn(addFn),
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "get.field $.this $.t0 field(Counter#value@.i32)\n" +
                "add $.t0 $n $.t1\n" +
                "ret $.t1\n");

            // 实例 invoke：receiver 求值作首实参（§7.3/§15.1）
            TestHarness.Check("main 指令（new + 实例 invoke receiver 首参）",
                RenderFn(FnOf(module, "$main()@.i32")),
                ".vars { Counter c, .i32 .t0, Counter .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(R_3) $.t0\n" +
                "new type(Counter) $.t1 [$.t0]\n" +
                "set.var $.t1 $c\n" +
                "load res(R_5) $.t2\n" +
                "invoke fn(Counter$add(n:.i32)@.i32) $.t3 [$c, $.t2]\n" +
                "ret $.t3\n");
        }

        // ===== S7c-2：for 端到端（iterate 前置 + LoweredLoop 复用发射）=====
        private static void TestForLoopEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    for (i in 0 to 3) {\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（for 发射）", unit);
            TestHarness.Check("资源（for）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true\nR_5 = i32 3");
            TestHarness.Check("for 多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$main()@.i32")),
                ".vars { .i32 sum, .i32 i, core.collections::IEnumerator<.i32> .s0, " +
                ".bool .s1, .breakid .b0, .i32 .t0, .i32 .t1, .i32 .t2, " +
                "core.collections::IEnumerable<.i32> .t3, " +
                "core.collections::IEnumerator<.i32> .t4, .i32 .t5, .i32 .t6, .bool .t7 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_1) $.t0\n" +
                "set.var $.t0 $sum\n" +
                "load res(R_1) $.t1\n" +
                "load res(R_5) $.t2\n" +
                "invoke fn(core::i32$$EnumerateInRange(end:.i32)" +
                "@core.collections::IEnumerable<.i32>) $.t3 [$.t1, $.t2]\n" +
                "invoke fn(core.collections::IEnumerable$iterate()" +
                "@core.collections::IEnumerator<.generic<$.generic.T>>) $.t4 [$.t3]\n" +
                "set.var $.t4 $.s0\n" +
                "loop $.s1 blk(loop0-body) none blk(loop0-judge) $.b0\n" +
                "ret $sum\n" +
                "}\n" +
                ".block loop0-body {\n" +
                "invoke fn(core.collections::IEnumerator$current()@.generic<$.generic.T>) " +
                "$.t5 [$.s0]\n" +
                "set.var $.t5 $i\n" +
                "add $sum $i $.t6\n" +
                "set.var $.t6 $sum\n" +
                "}\n" +
                ".block loop0-judge {\n" +
                "invoke fn(core.collections::IEnumerator$moveNext()@.bool) $.t7 [$.s0]\n" +
                "set.var $.t7 $.s1\n" +
                "}\n");
            // 结构性事实：ext operator fn 的 .args（§7.3 ext receiver 同形态）
            var extFn = FnOf(module,
                "core::i32$$EnumerateInRange(end:.i32)@core.collections::IEnumerable<.i32>");
            TestHarness.CheckTrue("ext operator fn 的 .args = [.return, .this(.i32), end]",
                extFn.Args.Count == 3
                && extFn.Args[1].Name == ".this" && extFn.Args[1].TypeRef == ".i32"
                && extFn.Args[2].Name == "end");
        }

        // ===== S7d：常量 switch → switch 指令（§16.6）+ switch-table 资源（§18.4）=====
        private static void TestSwitchEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func classify(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return classify(1)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（switch 发射）", unit);
            // 表元素只进表不产标量资源；R_6 是 item1 分支体 return 2 的字面量
            TestHarness.Check("资源（switch-table 单行形态）", RenderResources(module),
                "R_0 = string \"\\n\"\nR_1 = i32 0\nR_2 = bool false\nR_3 = i32 1\n" +
                "R_4 = bool true\nR_5 = switch-table<.i32> { 1, 2 }\nR_6 = i32 2");
            TestHarness.Check("switch 多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$classify(x:.i32)@.i32")),
                ".vars { .breakid .b0, .i32 .t0, .i32 .t1, .i32 .t2 }\n" +
                ".block entry entrypoint {\n" +
                "switch $x res(R_5) [blk(switch0-item0), blk(switch0-item1)] " +
                "blk(switch0-default) $.b0\n" +
                "}\n" +
                ".block switch0-item0 {\n" +
                "load res(R_3) $.t0\n" +
                "ret $.t0\n" +
                "}\n" +
                ".block switch0-item1 {\n" +
                "load res(R_6) $.t1\n" +
                "ret $.t1\n" +
                "}\n" +
                ".block switch0-default {\n" +
                "load res(R_1) $.t2\n" +
                "ret $.t2\n" +
                "}\n");
            // 结构性事实：§16.6 操作数形状与 .vars 的 .breakid 条目（§9.3）
            var classifyFn = FnOf(module, "$classify(x:.i32)@.i32");
            var switchInstruction = classifyFn.Blocks[0].Instructions
                .Single(i => i.Opcode == "switch");
            TestHarness.CheckTrue("switch 五操作数（selector/res/item表/default/breakid）",
                switchInstruction.Operands.Count == 5
                && switchInstruction.Operands[0] is BilVariableOperand
                && switchInstruction.Operands[1] is BilResourceOperand
                && switchInstruction.Operands[2] is BilOperandList itemList
                && itemList.Items.Count == 2
                && switchInstruction.Operands[3] is BilBlockOperand
                && switchInstruction.Operands[4].Render() == "$.b0");
            TestHarness.CheckTrue(".vars 含 .breakid 条目",
                classifyFn.Vars.Any(v => v.TypeRef == ".breakid" && v.Name == ".b0"));
            TestHarness.CheckTrue("item/default 块 id 函数内唯一",
                classifyFn.Blocks.Select(b => b.Id).Distinct().Count()
                == classifyFn.Blocks.Count);

            // 跨 fn 同表去重：case 集完全相同的两个 switch 共享一张 §18.4 表
            var (unit2, module2, _) = EmitUnit(
                "pub func a(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n" +
                "pub func b(y: i32): i32 {\n" +
                "    switch (y) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（switch 表去重）", unit2);
            TestHarness.CheckTrue("case 集相同的两个 switch 共享一张表",
                module2.Resources.Count(r => r is BilCollectionResource) == 1
                && module2.Functions.SelectMany(f => f.Blocks[0].Instructions)
                    .Where(i => i.Opcode == "switch")
                    .Select(i => ((BilResourceOperand)i.Operands[1]).ResourceId)
                    .Distinct().Count() == 1,
                string.Join(", ", module2.Resources.Select(r => r.Name)));
        }

        // ===== S7d：pattern switch 不到 P4b（P4a 已降为 if 链）=====
        private static void TestPatternSwitchEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 5\n" +
                "    var label = switch (x) {\n" +
                "        (_ > 10) -> { return@_ 1 }\n" +
                "        default -> { return@_ 0 }\n" +
                "    }\n" +
                "    return label\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（pattern switch 发射）", unit);
            var main = FnOf(module, "$main()@.i32");
            TestHarness.CheckTrue("pattern switch 降为 if 链（无 switch 指令）",
                main.Blocks.SelectMany(b => b.Instructions).All(i => i.Opcode != "switch")
                && main.Blocks.SelectMany(b => b.Instructions).Any(i => i.Opcode == "if"));
            TestHarness.CheckTrue("无 switch-table 资源",
                module.Resources.All(r => r is not BilCollectionResource));
            // selector 物化一次（.s1），pattern 条件引用它而非重复求值
            TestHarness.Check("pattern 链多 block 文本", RenderFnAllBlocks(main),
                ".vars { .i32 x, .i32 label, .i32 .s0, .i32 .s1, .i32 .t0, .i32 .t1, " +
                ".bool .t2, .i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(R_5) $.t0\n" +
                "set.var $.t0 $x\n" +
                "set.var $x $.s1\n" +
                "load res(R_6) $.t1\n" +
                "cmp.gt $.s1 $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $label\n" +
                "ret $label\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(R_3) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_1) $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "}\n");
        }

        // ===== S7d：throw → §16.9 单操作数指令 =====
        private static void TestThrowEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func fail(): i32 {\n" +
                "    throw new core.Exception()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（throw 发射）", unit);
            var fail = FnOf(module, "$fail()@.i32");
            TestHarness.Check("fail 指令与 .vars", RenderFn(fail),
                ".vars { core::Exception .t0 }\n" +
                "new type(core::Exception) $.t0 []\n" +
                "throw $.t0\n");
            var throwInstruction = fail.Blocks[0].Instructions.Single(i => i.Opcode == "throw");
            TestHarness.CheckTrue("throw 单操作数（§16.9）",
                throwInstruction.Operands.Count == 1
                && throwInstruction.Operands[0] is BilVariableOperand);
            // throw 是终止指令：entry 块落尾不补 ret（§9.4 补 ret 逻辑只看 ret）
            TestHarness.CheckTrue("throw 终止后无赘余 ret",
                fail.Blocks[0].Instructions.Last().Opcode == "throw");
        }

        // ===== S7e：cast 发射（§12.1/§12.2）=====
        private static void TestCastEmission()
        {
            var (unit, module, _) = EmitUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n" +
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（cast 发射）", unit);
            TestHarness.Check("as 指令文本", RenderFn(FnOf(module, "$f(s:.string)@.string")),
                ".vars { .string .t0 }\n" +
                "cast $s $.t0 type(.string)\n" +
                "ret $.t0\n");
            TestHarness.Check("as? 指令文本", RenderFn(FnOf(module, "$g(s:.string)@.nullable<.string>")),
                ".vars { .nullable<.string> .t0 }\n" +
                "cast.safe $s $.t0 type(.string)\n" +
                "ret $.t0\n");
            // 结构性事实：§12.1 三操作数形状（SOURCE RESULT type(TARGET_TYPE)）
            var castInstruction = FnOf(module, "$f(s:.string)@.string").Blocks[0].Instructions
                .Single(i => i.Opcode == "cast");
            TestHarness.CheckTrue("cast 三操作数（§12.1）",
                castInstruction.Operands.Count == 3
                && castInstruction.Operands[0] is BilVariableOperand
                && castInstruction.Operands[1] is BilVariableOperand
                && castInstruction.Operands[2] is BilTypeOperand);
        }

        // ===== S7f：字符串插值端到端（§3.8：toString/add 链 + 装箱 cast + §11.2 拼接）=====
        private static void TestStringInterpolationEmission()
        {
            var (unit, module, _) = EmitUnit(
                "pub func main() {\n" +
                "    var name = \"world\"\n" +
                "    var count = 3\n" +
                "    core.io.Console.println(\"Hello ${name}, count=${count + 1}\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（插值）", unit);
            TestHarness.Check("main 指令与 .vars（插值端到端）",
                RenderFn(FnOf(module, "$main()@.void")),
                ".vars { .string name, .i32 count, .string .t0, .i32 .t1, .string .t2, " +
                ".string .t3, .string .t4, .string .t5, .i32 .t6, .i32 .t7, .any .t8, " +
                ".string .t9, .string .t10 }\n" +
                "load res(R_5) $.t0\n" +
                "set.var $.t0 $name\n" +
                "load res(R_6) $.t1\n" +
                "set.var $.t1 $count\n" +
                "load res(R_7) $.t2\n" +
                "add $.t2 $name $.t3\n" +
                "load res(R_8) $.t4\n" +
                "add $.t3 $.t4 $.t5\n" +
                "load res(R_3) $.t6\n" +
                "add $count $.t6 $.t7\n" +
                "cast $.t7 $.t8 type(.any)\n" +
                "invoke fn(core::Any$toString()@.string) $.t9 [$.t8]\n" +
                "add $.t5 $.t9 $.t10\n" +
                "invoke.noret fn(core.io::Console$.static.println(text:.string)@.void) [$.t10]\n" +
                "ret\n");
            // 结构性事实：String 段直拼无 toString；非 String 段一经 cast 一 invoke
            var mainInstructions = FnOf(module, "$main()@.void").Blocks[0].Instructions;
            TestHarness.CheckTrue("toString 调用恰一次（仅非 String 段）",
                mainInstructions.Count(i => i.Opcode == "invoke") == 1);
        }

        // ===== S7f：`?.` 发射（§3.4 脱糖：null 检查 + if + unwrap/wrap cast）=====
        private static void TestSafeAccessEmission()
        {
            var (unit, module, _) = EmitUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("全管线无诊断（?.）", unit);
            TestHarness.Check("f 指令与 .vars（?. 发射）",
                RenderFnAllBlocks(FnOf(module, "$f(u:.nullable<User>)@.nullable<.string>")),
                ".vars { .nullable<User> .s0, .nullable<.string> .s1, .nullable<.string> .t0, " +
                ".nullable<User> .t1, .bool .t2, User .t3, .string .t4, .nullable<.string> .t5 }\n" +
                ".block entry entrypoint {\n" +
                "set.var $u $.s0\n" +
                "load res(R_5) $.t0\n" +
                "set.var $.t0 $.s1\n" +
                "load res(R_6) $.t1\n" +
                "cmp.ne $.s0 $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) none\n" +
                "ret $.s1\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t3 type(User)\n" +
                "get.field $.t3 $.t4 field(User#name@.string)\n" +
                "cast $.t4 $.t5 type(.nullable<.string>)\n" +
                "set.var $.t5 $.s1\n" +
                "}\n");
            // 结构性事实：null 资源形态（§18.1：null type(元素类型)）
            TestHarness.CheckTrue("null 资源按元素类型登记（R_5/R_6）",
                module.Resources.Any(r => r is BilNullResource n
                    && n.TypeRef == ".string")
                && module.Resources.Any(r => r is BilNullResource n
                    && n.TypeRef == "User"));
        }

        // ===== S7f：`if?` 发射（非空分支 unwrap / 空分支回退，延迟求值）=====
        private static void TestNullFallbackEmission()
        {
            var (unit, module, _) = EmitUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func g(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("全管线无诊断（if?）", unit);
            TestHarness.Check("g 指令与 .vars（if? 发射）",
                RenderFnAllBlocks(FnOf(module, "$g(u:.nullable<User>)@User")),
                ".vars { .nullable<User> .s0, User .s1, .nullable<User> .t0, .bool .t1, " +
                "User .t2, .string .t3, User .t4 }\n" +
                ".block entry entrypoint {\n" +
                "set.var $u $.s0\n" +
                "load res(R_5) $.t0\n" +
                "cmp.ne $.s0 $.t0 $.t1\n" +
                "if $.t1 blk(if0-then) blk(if0-else)\n" +
                "ret $.s1\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t2 type(User)\n" +
                "set.var $.t2 $.s1\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(R_6) $.t3\n" +
                "new type(User) $.t4 [$.t3]\n" +
                "set.var $.t4 $.s1\n" +
                "}\n");
        }

        // ===== S7f：解构发射（§3.4 精确字段读取；基类泛型字段访问）=====
        private static void TestDestructuringEmission()
        {
            var (unit, module, _) = EmitUnit(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "pub func h(): String {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "    return k\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（解构）", unit);
            TestHarness.Check("h 指令与 .vars（解构发射）",
                RenderFn(FnOf(module, "$h()@.string")),
                ".vars { .string k, .i32 v, Entry .s0, .string .t0, .i32 .t1, Entry .t2, " +
                ".string .t3, .i32 .t4 }\n" +
                "load res(R_5) $.t0\n" +
                "load res(R_3) $.t1\n" +
                "new type(Entry) $.t2 [$.t0, $.t1]\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.var $.t3 $k\n" +
                "get.field $.s0 $.t4 field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "set.var $.t4 $v\n" +
                "ret $k\n");
            TestHarness.Check("init 内基类字段写入（替换后类型）",
                RenderFn(FnOf(module, "Entry$init(k:.string,v:.i32)@.void")),
                ".vars {  }\n" +
                "set.field $k $.this field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.field $v $.this field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "ret\n");
        }

        // ===== S7e：try/catch/finally 发射（§16.7 + §18.5 catch-table）=====
        private static void TestTryEmission()
        {
            var (unit, module, _) = EmitUnit(
                "open class MyError : core.Exception {\n" +
                "}\n" +
                "class DerivedError : MyError {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func f() {\n" +
                "    try {\n" +
                "        throw new DerivedError()\n" +
                "    } catch (d: DerivedError) {\n" +
                "        log()\n" +
                "    } catch (e: MyError) {\n" +
                "        log()\n" +
                "    } finally(x) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（try 发射）", unit);
            var f = FnOf(module, "$f()@.void");
            TestHarness.Check("try 多 block 文本", RenderFnAllBlocks(f),
                ".vars { DerivedError d, MyError e, .nullable<core::Exception> x, " +
                "DerivedError .t0, DerivedError .t1, MyError .t2 }\n" +
                ".block entry entrypoint {\n" +
                "try blk(try0-body) $x res(R_5) blk(try0-finally)\n" +
                "ret\n" +
                "}\n" +
                ".block try0-body {\n" +
                "new type(DerivedError) $.t0 []\n" +
                "throw $.t0\n" +
                "}\n" +
                ".block try0-catch0 {\n" +
                "cast $x $.t1 type(DerivedError)\n" +
                "set.var $.t1 $d\n" +
                "invoke.noret fn($log()@.void) []\n" +
                "}\n" +
                ".block try0-catch1 {\n" +
                "cast $x $.t2 type(MyError)\n" +
                "set.var $.t2 $e\n" +
                "invoke.noret fn($log()@.void) []\n" +
                "}\n" +
                ".block try0-finally {\n" +
                "invoke.noret fn($log()@.void) []\n" +
                "}\n");
            // 结构性事实：§16.7 四操作数形状
            var tryInstruction = f.Blocks[0].Instructions.Single(i => i.Opcode == "try");
            TestHarness.CheckTrue("try 四操作数（body/slot/表/finally）",
                tryInstruction.Operands.Count == 4
                && tryInstruction.Operands[0] is BilBlockOperand
                && tryInstruction.Operands[1] is BilVariableOperand
                && tryInstruction.Operands[2] is BilResourceOperand
                && tryInstruction.Operands[3] is BilBlockOperand);
            // §18.5 catch-table：多行形态、元素保序（表序即匹配序）
            var catchTable = (BilCollectionResource)module.Resources.Single(
                r => r is BilCollectionResource c && c.Header == "catch-table");
            TestHarness.Check("catch-table 元素（保序）", string.Join("\n", catchTable.Elements),
                "type(DerivedError) -> blk(try0-catch0)\n" +
                "type(MyError) -> blk(try0-catch1)");
            TestHarness.CheckTrue("catch-table 多行形态（§18.5）", catchTable.Multiline);
        }

        // ===== S7e：seq 发射（§16.1 call 化 + §9.6 volatile 修饰符）=====
        private static void TestSeqEmission()
        {
            // 语句形态：seq / volatile seq 各一
            var (unit, module, _) = EmitUnit(
                "func work() {\n" +
                "}\n" +
                "func s() {\n" +
                "    seq {\n" +
                "        var x = 1\n" +
                "    }\n" +
                "    volatile seq {\n" +
                "        work()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（seq 语句发射）", unit);
            TestHarness.Check("seq 语句多 block 文本",
                RenderFnAllBlocks(FnOf(module, "$s()@.void")),
                ".vars { .i32 x, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0)\n" +
                "call blk(seq1)\n" +
                "ret\n" +
                "}\n" +
                ".block seq0 {\n" +
                "load res(R_3) $.t0\n" +
                "set.var $.t0 $x\n" +
                "}\n" +
                ".block seq1 volatile {\n" +
                "invoke.noret fn($work()@.void) []\n" +
                "}\n");

            // 表达式形态（P4a 已脱糖为前置 seq 块写合成局部）+ volatile 变体
            var (unit2, module2, _) = EmitUnit(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n" +
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（seq 表达式发射）", unit2);
            TestHarness.Check("seq 表达式多 block 文本",
                RenderFnAllBlocks(FnOf(module2, "$se()@.i32")),
                ".vars { .i32 .s0, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0)\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 {\n" +
                "load res(R_5) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "}\n");
            TestHarness.Check("volatile seq 表达式多 block 文本",
                RenderFnAllBlocks(FnOf(module2, "$sv()@.i32")),
                ".vars { .i32 .s0, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0)\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 volatile {\n" +
                "load res(R_3) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "}\n");
            // 结构性事实：§16.1 call 单操作数 blk(...)（不建栈帧）
            var callInstruction = FnOf(module2, "$se()@.i32").Blocks[0].Instructions
                .Single(i => i.Opcode == "call");
            TestHarness.CheckTrue("call 单操作数 blk（§16.1）",
                callInstruction.Operands.Count == 1
                && callInstruction.Operands[0] is BilBlockOperand);
        }

        // ===== 负例：未覆盖节点 → P4 Error =====
        // （S7c-2 后实例方法/init/operator 已开闸，源码侧 P4 发射全覆盖——
        // 以测试私有 Lowered 子类模拟「未来新增但 BilEmitter 尚未覆盖」的
        // 节点，仿 LowererTests.TestUnsupportedNode）
        private sealed class FutureLoweredStatement : LoweredStatement
        {
            public FutureLoweredStatement(BoundNode origin) : base(origin)
            {
            }
        }

        private static void TestUnsupportedNodes()
        {
            var root = TestHarness.ParseRoot("func f() { }\n");
            var unit = new CompilationUnit(root);
            var method = new MethodSymbol("future", MethodKind.Regular);
            var boundBody = new BoundBlock(root, new List<BoundStatement>());
            var body = new LoweredFunctionBody(method, new List<LocalSymbol>(),
                new LoweredBlock(boundBody,
                    new List<LoweredStatement> { new FutureLoweredStatement(boundBody) }));
            BilEmitter.Emit(unit, new[] { body }, "future");
            TestHarness.CheckSemanticError("未覆盖节点报 P4 Error", unit.Diagnostics,
                "not supported by minimal emission");
        }
    }
}
