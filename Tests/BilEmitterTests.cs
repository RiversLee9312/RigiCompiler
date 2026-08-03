using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S6 P4 最小闭环 + S7a 基础发射补齐测试：Lowerer（P4a 恒等重写）+
    /// BilEmitter（P4b 发射）端到端。
    /// M58 起迁移到 BilTestHarness 验证器框架：全管线无诊断（CheckNoErrors）
    /// → CheckBilValid（BilVerifier 零错误）→ CheckFnShape/CheckResShape
    /// 形状黄金（指令序列/操作数/.tN 编号/block id 逐字节锁定；资源名按
    /// 首次出现顺序重编号 res(#k)/#k，消除 stdlib 基线资源偏移脆弱点）。
    /// 覆盖：全管线无诊断、hello world 模块结构断言（Metadata 恰一条 /
    /// Resources 计数与标量资源 / LocalSymbols 含 core.io::Console 类型与
    /// println 静态方法声明 / main 与 println 两个 fn 定义 / main 恰一个
    /// entrypoint block）、Origin 调试链
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
            TestTypeCheckEmission();
            TestTypeOfEmission();
            TestUnsupportedNodes();

            return TestHarness.Summary("BilEmitter");
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }

        // ===== hello world 模块结构断言（M58 起：原全模块黄金文本改为
        // CheckBilValid + 结构断言；fn 形状回归由后续用例 CheckFnShape 承担）=====
        private static void TestGoldenOutput()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断", unit);
            BilTestHarness.CheckBilValid("验证器零错误（hello world）", module);
            TestHarness.CheckTrue("Metadata 恰一条 module = \"hello\"",
                module.Metadata.Count == 1
                && module.Metadata[0].Key == "module"
                && module.Metadata[0].Type == BilScalarType.String
                && module.Metadata[0].LiteralText == "\"hello\"");
            TestHarness.CheckTrue("Resources 恰 6 条且含 \"Hello, world!\" 标量资源",
                module.Resources.Count == 6
                && module.Resources.Any(r => r is BilScalarResource s
                    && s.Type == BilScalarType.String
                    && s.LiteralText == "\"Hello, world!\""));
            TestHarness.CheckTrue("LocalSymbols 含 core.io::Console 类型与 println 静态方法声明",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "core.io::Console")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Kind == BilMemberKind.StaticMethod
                        && d.Symbol == "core.io::Console$.static.println(text:.string)@.void"));
            TestHarness.CheckTrue("module.Functions 含 main 与 println 两个 fn",
                module.Functions.Any(f => f.Symbol == "$main()@.i32")
                && module.Functions.Any(f => f.Symbol
                    == "core.io::Console$.static.println(text:.string)@.void"));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("main 恰一个 entrypoint block",
                main.Blocks.Count(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint)) == 1);
        }

        // ===== Origin 调试链（ARCHITECTURE §6.3）=====
        private static void TestOriginChain()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(HelloWorldSource);
            CheckNoErrors("全管线无诊断（Origin 链）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Origin 链）", module);

            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var invoke = main.Blocks[0].Instructions.First(i => i is InvokeNoResultInstruction);
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
                bound?.Syntax.Span?.sourceName ?? "<null>", BilTestHarness.UserSourceName);

            // load 的 Origin 是字面量 LoweredNode（值经 Origin.Syntax 回取）
            var load = main.Blocks[0].Instructions.First(i => i is LoadInstruction);
            TestHarness.CheckTrue("load 的 Origin 是 LoweredLiteralExpression",
                load.Origin is LoweredLiteralExpression,
                load.Origin?.GetType().Name ?? "<null>");
        }

        // ===== 资源去重：同（类型, 原文）字面量只登记一次 =====
        private static void TestResourceDeduplication()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(\"same\")\n" +
                "    core.io.Console.println(\"same\")\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（资源去重）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（资源去重）", module);
            // stdlib 基线 R_0..R_4（"\n"/0/false/1/true）+ R_5 = "same"——
            // "same" 不重复登记；return 0 共享 R_1
            TestHarness.CheckTrue("相同字面量只登记一个资源",
                module.Resources.Count == 6
                && module.Resources.Count(r => r is BilScalarResource s
                    && s.LiteralText == "\"same\"") == 1,
                string.Join(", ", module.Resources.Select(r => r.Name)));
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            var loads = main.Blocks[0].Instructions.Where(i => i is LoadInstruction).ToList();
            // main 共三条 load（两次 "same" + return 0），两条指向同一资源 R_5
            TestHarness.CheckTrue("两处引用同一资源（R_5）",
                loads.Count == 3 && loads.Count(l => l.Operands[0] is BilResourceOperand ro
                    && ro.Resource.Name == "R_5") == 2);
        }

        // ===== 局部声明 + 初始化器（set.var）与赋值 =====
        private static void TestLocalDeclarationAndAssignment()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1 + 2\n" +
                "    x = x * 3\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（声明与赋值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（声明与赋值）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .i32 x, .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "load res(#1) $.t1\n" +
                "add $.t0 $.t1 $.t2\n" +
                "set.var $.t2 $x\n" +
                "load res(#2) $.t3\n" +
                "mul $x $.t3 $.t4\n" +
                "set.var $.t4 $x\n" +
                "ret $x\n");
        }

        // ===== 一元运算与比较运算（同字面量资源去重）=====
        private static void TestUnaryAndComparison()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var a: i32 = 5\n" +
                "    var n: i32 = -a\n" +
                "    var b: bool = n == 5\n" +
                "    return n\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（一元与比较）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（一元与比较）", module);
            // 两处 5 共用同一资源（stdlib 基线 5 条资源在前）
            BilTestHarness.CheckResShape("资源（5 去重）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true\n#5 = i32 5");
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .i32 a, .i32 n, .bool b, .i32 .t0, .i32 .t1, .i32 .t2, .bool .t3 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $a\n" +
                "opposite $a $.t1\n" +
                "set.var $.t1 $n\n" +
                "load res(#0) $.t2\n" +
                "cmp.eq $n $.t2 $.t3\n" +
                "set.var $.t3 $b\n" +
                "ret $n\n");
        }

        // ===== 带返回值 invoke（§15.1）与表达式语句（结果物化后丢弃）=====
        private static void TestInvokeWithResult()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func double(a: i32): i32 { return a * 2 }\n" +
                "pub func main(): i32 {\n" +
                "    double(5)\n" +
                "    return double(21)\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（invoke）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（invoke）", module);
            BilTestHarness.CheckFnShape("double 指令与 .vars", module, "$double(a:.i32)@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(#0) $.t0\n" +
                "mul $a $.t0 $.t1\n" +
                "ret $.t1\n");
            BilTestHarness.CheckFnShape("main 指令与 .vars（表达式语句结果丢弃）",
                module, "$main()@.i32",
                ".vars { .i32 .t0, .i32 .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "invoke fn($double(a:.i32)@.i32) $.t1 [$.t0]\n" +
                "load res(#1) $.t2\n" +
                "invoke fn($double(a:.i32)@.i32) $.t3 [$.t2]\n" +
                "ret $.t3\n");
        }

        // ===== new 构造（§14.1；零参无显式 init）=====
        private static void TestNew()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Empty { }\n" +
                "pub func main(): i32 {\n" +
                "    var e: Empty = new Empty()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（new）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（new）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { Empty e, Empty .t0, .i32 .t1 }\n" +
                "new type(Empty) $.t0 []\n" +
                "set.var $.t0 $e\n" +
                "load res(#0) $.t1\n" +
                "ret $.t1\n");
        }

        // ===== §18.1 标量资源全形态（bool/f64/f32/char/null）=====
        private static void TestLiteralResources()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var b: bool = true\n" +
                "    var d: double = 0.5\n" +
                "    var f: float = 0.1f\n" +
                "    var c: char = 'A'\n" +
                "    var s: String? = null\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（标量资源）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（标量资源）", module);
            BilTestHarness.CheckResShape("Resources 全形态", module,
                "#0 = string \"\\n\"\n" +
                "#1 = i32 0\n" +
                "#2 = bool false\n" +
                "#3 = i32 1\n" +
                "#4 = bool true\n" +
                "#5 = f64 0.5\n" +
                "#6 = f32 0.1\n" +
                "#7 = char 'A'\n" +
                "#8 = null type(.string)");
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .bool b, .f64 d, .f32 f, .char c, .nullable<.string> s, " +
                ".bool .t0, .f64 .t1, .f32 .t2, .char .t3, .nullable<.string> .t4, .i32 .t5 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $b\n" +
                "load res(#1) $.t1\n" +
                "set.var $.t1 $d\n" +
                "load res(#2) $.t2\n" +
                "set.var $.t2 $f\n" +
                "load res(#3) $.t3\n" +
                "set.var $.t3 $c\n" +
                "load res(#4) $.t4\n" +
                "set.var $.t4 $s\n" +
                "load res(#5) $.t5\n" +
                "ret $.t5\n");
        }

        // ===== static 字段读写（§13.4 get/set.field.static）=====
        private static void TestStaticFieldReadWrite()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Counter { pub static var value: i32 }\n" +
                "pub func main(): i32 {\n" +
                "    Counter.value = 42\n" +
                "    return Counter.value\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（static 字段）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（static 字段）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars", module, "$main()@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "load res(#0) $.t0\n" +
                "set.field.static $.t0 type(Counter) field(Counter#.static.value@.i32)\n" +
                "get.field.static $.t1 type(Counter) field(Counter#.static.value@.i32)\n" +
                "ret $.t1\n");
        }

        // ===== S7b：if 语句 → 多 block（§16.2 结构化条件）=====
        private static void TestIfStatementEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（if 语句发射）", module);
            // 两处 0 共用同一资源（stdlib 基线 5 条资源在前；2 新增一条）
            BilTestHarness.CheckResShape("资源（0 去重）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true\n#5 = i32 2");
            BilTestHarness.CheckFnShape("if 语句多 block 文本", module, "$main()@.i32",
                ".vars { .i32 x, .i32 .t0, .i32 .t1, .bool .t2, .i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $x\n" +
                "load res(#0) $.t1\n" +
                "cmp.eq $x $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "ret $x\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(#1) $.t3\n" +
                "set.var $.t3 $x\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#2) $.t4\n" +
                "set.var $.t4 $x\n" +
                "}\n");
            // 结构性事实：block id 函数内唯一且恰一个 entrypoint（§9.4）
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("恰一个 entrypoint block 且 id 唯一",
                main.Blocks.Count(b => b.Modifiers.Contains(BilBlockModifier.Entrypoint)) == 1
                && main.Blocks.Select(b => b.Id).Distinct().Count() == main.Blocks.Count);

            // 无 else → none 操作数
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    if (x == 0) { x = 1 }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（无 else if）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（无 else if）", module2);
            var ifInstruction = module2.Functions.Single(f => f.Symbol == "$main()@.i32").Blocks
                .SelectMany(b => b.Instructions).Single(i => i is IfInstruction);
            TestHarness.CheckTrue("无 else 用 none 操作数",
                ifInstruction.Operands.Count == 3
                && ifInstruction.Operands[2] is BilNoneOperand
                && ifInstruction.Operands[1].Render() == "blk(if0-then)");
            TestHarness.CheckTrue("无 else 不产 else block",
                module2.Functions.Single(f => f.Symbol == "$main()@.i32").Blocks
                    .All(b => b.Id != "if0-else"));
        }

        // ===== S7b：if 表达式 → 结果局部 + 分支块写值 =====
        private static void TestIfExpressionEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 1\n" +
                "    var r = if ((x > 0)) { return@_ 1 } else { return@_ 2 }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（if 表达式发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（if 表达式发射）", module);
            // 分支里的 1 与 x 初始化器 1 同键共享 stdlib 基线资源；
            // 0 共享基线；2 新增一条
            BilTestHarness.CheckResShape("资源（1 去重）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true\n#5 = i32 2");
            BilTestHarness.CheckFnShape("if 表达式多 block 文本", module, "$main()@.i32",
                ".vars { .i32 x, .i32 r, .i32 .s0, .i32 .t0, .i32 .t1, .bool .t2, " +
                ".i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $x\n" +
                "load res(#1) $.t1\n" +
                "cmp.gt $x $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $r\n" +
                "ret $r\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(#0) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#2) $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "}\n");
        }

        // ===== S7b：短路 and/or → §11.3 if 展开（无裸 and/or 指令）=====
        private static void TestShortCircuitEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var a: bool = true\n" +
                "    var b: bool = false\n" +
                "    var c = (a and b)\n" +
                "    var d = (a or b)\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（短路发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（短路发射）", module);
            TestHarness.CheckTrue("无裸 and/or 指令（§11.3 已展开）",
                module.Functions.Single(f => f.Symbol == "$main()@.i32").Blocks
                    .SelectMany(b => b.Instructions)
                    .All(i => i is not BinaryIntrinsicInstruction bin
                        || (bin.Op != BilBinaryOp.And && bin.Op != BilBinaryOp.Or)),
                string.Join(", ", module.Functions.Single(f => f.Symbol == "$main()@.i32").Blocks
                    .SelectMany(b => b.Instructions).Select(i => i.Opcode)));
            // 合成常量与源码字面量同键去重：true/false/0 全部共享 stdlib
            // 基线——资源零新增
            BilTestHarness.CheckResShape("资源（true/false 去重）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true");
            BilTestHarness.CheckFnShape("短路 and/or 多 block 文本", module, "$main()@.i32",
                ".vars { .bool a, .bool b, .bool c, .bool d, .bool .s0, .bool .s1, " +
                ".bool .t0, .bool .t1, .bool .t2, .bool .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $a\n" +
                "load res(#1) $.t1\n" +
                "set.var $.t1 $b\n" +
                "if $a blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $c\n" +
                "if $a blk(if1-then) blk(if1-else)\n" +
                "set.var $.s1 $d\n" +
                "load res(#2) $.t4\n" +
                "ret $.t4\n" +
                "}\n" +
                ".block if0-then {\n" +
                "set.var $b $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#1) $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "}\n" +
                ".block if1-then {\n" +
                "load res(#0) $.t3\n" +
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
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    while (x < 3) {\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（while 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（while 发射）", module);
            BilTestHarness.CheckResShape("资源（while）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true\n#5 = i32 3");
            BilTestHarness.CheckFnShape("while 多 block 文本", module, "$main()@.i32",
                ".vars { .i32 x, .bool .s0, .breakid .b0, .i32 .t0, .i32 .t1, " +
                ".i32 .t2, .i32 .t3, .bool .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $x\n" +
                "loop $.s0 blk(loop0-body) none blk(loop0-judge) $.b0\n" +
                "ret $x\n" +
                "}\n" +
                ".block loop0-body {\n" +
                "load res(#1) $.t1\n" +
                "add $x $.t1 $.t2\n" +
                "set.var $.t2 $x\n" +
                "}\n" +
                ".block loop0-judge {\n" +
                "load res(#2) $.t3\n" +
                "cmp.lt $x $.t3 $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "}\n");
            // 结构性事实：.vars 的 .breakid 条目（§9.3 别名投影）
            var mainFn = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue(".vars 含 .breakid 条目",
                mainFn.Vars.Any(v => v.TypeRef == ".breakid" && v.Name == ".b0"));

            // do-while → loop.rev（结构断言）
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < 3)\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（do-while 发射）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（do-while 发射）", module2);
            var revFn = module2.Functions.Single(f => f.Symbol == "$main()@.i32");
            var revInstruction = revFn.Blocks.SelectMany(b => b.Instructions)
                .Single(i => i is LoopInstruction { IsRev: true });
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
            var (unit3, module3, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（嵌套标签循环发射）", module3);
            var nestedFn = module3.Functions.Single(f => f.Symbol == "$main()@.i32");
            var breakInstruction = nestedFn.Blocks.SelectMany(b => b.Instructions)
                .Single(i => i is BreakInstruction);
            var continueInstruction = nestedFn.Blocks.SelectMany(b => b.Instructions)
                .Single(i => i is ContinueInstruction);
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
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（实例成员发射）", module);

            // init/operator/ext 声明形态（§8.4）——init 是 Counter .type
            // 的成员（类型成员嵌在类型声明内）；ext operator 是顶层裸条目
            TestHarness.CheckTrue("init 声明形态（普通 canonical + init 修饰符）",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol == "Counter")
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(d => d.Kind == BilMemberKind.Method
                        && d.Symbol == "Counter$init(v:.i32)@.void"
                        && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Init })
                        && d.Modifiers.Any(m => m is BilAccessibilityModifier
                            { Accessibility: BilAccessibility.Public })));
            TestHarness.CheckTrue("ext operator 声明形态（$$名 + ext + operator(名)）",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Kind == BilMemberKind.Method
                    && d.Symbol == "core::i32$$EnumerateInRange(end:.i32)" +
                        "@core.collections::IEnumerable<.i32>"
                    && d.Modifiers.Any(m => m is BilKeywordModifier { Keyword: BilKeyword.Ext })
                    && d.Modifiers.Any(m => m is BilOperatorModifier op
                        && op.Name == "EnumerateInRange")));

            // .this 进 .args（§9.2：.return 后、普通参数前；§7.3）
            var addFn = module.Functions.Single(f => f.Symbol == "Counter$add(n:.i32)@.i32");
            TestHarness.CheckTrue("add 的 .args = [.return, .this, n]",
                addFn.Args.Count == 3
                && addFn.Args[0].Name == ".return"
                && addFn.Args[1].Name == ".this" && addFn.Args[1].TypeRef == "Counter"
                && addFn.Args[2].Name == "n");
            var initFn = module.Functions.Single(f => f.Symbol == "Counter$init(v:.i32)@.void");
            TestHarness.CheckTrue("init 的 .args = [.return(.void), .this, v]",
                initFn.Args.Count == 3
                && initFn.Args[0].TypeRef == ".void"
                && initFn.Args[1].Name == ".this");

            // get.field/set.field（§13.3）与 void init 末尾补 ret
            BilTestHarness.CheckFnShape("init 指令（set.field $v $.this）",
                module, "Counter$init(v:.i32)@.void",
                ".vars {  }\n" +
                "set.field $v $.this field(Counter#value@.i32)\n" +
                "ret\n");
            BilTestHarness.CheckFnShape("add 指令（get.field $.this + add）",
                module, "Counter$add(n:.i32)@.i32",
                ".vars { .i32 .t0, .i32 .t1 }\n" +
                "get.field $.this $.t0 field(Counter#value@.i32)\n" +
                "add $.t0 $n $.t1\n" +
                "ret $.t1\n");

            // 实例 invoke：receiver 求值作首实参（§7.3/§15.1）
            BilTestHarness.CheckFnShape("main 指令（new + 实例 invoke receiver 首参）",
                module, "$main()@.i32",
                ".vars { Counter c, .i32 .t0, Counter .t1, .i32 .t2, .i32 .t3 }\n" +
                "load res(#0) $.t0\n" +
                "new type(Counter) $.t1 [$.t0]\n" +
                "set.var $.t1 $c\n" +
                "load res(#1) $.t2\n" +
                "invoke fn(Counter$add(n:.i32)@.i32) $.t3 [$c, $.t2]\n" +
                "ret $.t3\n");
        }

        // ===== S7c-2：for 端到端（iterate 前置 + LoweredLoop 复用发射）=====
        private static void TestForLoopEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    for (i in 0 to 3) {\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（for 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（for 发射）", module);
            BilTestHarness.CheckResShape("资源（for）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true\n#5 = i32 3");
            BilTestHarness.CheckFnShape("for 多 block 文本", module, "$main()@.i32",
                ".vars { .i32 sum, .i32 i, core.collections::IEnumerator<.i32> .s0, " +
                ".bool .s1, .breakid .b0, .i32 .t0, .i32 .t1, .i32 .t2, " +
                "core.collections::IEnumerable<.i32> .t3, " +
                "core.collections::IEnumerator<.i32> .t4, .i32 .t5, .i32 .t6, .bool .t7 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $sum\n" +
                "load res(#0) $.t1\n" +
                "load res(#1) $.t2\n" +
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
            var extFn = module.Functions.Single(f => f.Symbol ==
                "core::i32$$EnumerateInRange(end:.i32)@core.collections::IEnumerable<.i32>");
            TestHarness.CheckTrue("ext operator fn 的 .args = [.return, .this(.i32), end]",
                extFn.Args.Count == 3
                && extFn.Args[1].Name == ".this" && extFn.Args[1].TypeRef == ".i32"
                && extFn.Args[2].Name == "end");
        }

        // ===== S7d：常量 switch → switch 指令（§16.6）+ switch-table 资源（§18.4）=====
        private static void TestSwitchEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（switch 发射）", module);
            // 表元素只进表不产标量资源；#6 是 item1 分支体 return 2 的字面量
            BilTestHarness.CheckResShape("资源（switch-table 单行形态）", module,
                "#0 = string \"\\n\"\n#1 = i32 0\n#2 = bool false\n#3 = i32 1\n" +
                "#4 = bool true\n#5 = switch-table<.i32> { 1, 2 }\n#6 = i32 2");
            BilTestHarness.CheckFnShape("switch 多 block 文本", module, "$classify(x:.i32)@.i32",
                ".vars { .breakid .b0, .i32 .t0, .i32 .t1, .i32 .t2 }\n" +
                ".block entry entrypoint {\n" +
                "switch $x res(#0) [blk(switch0-item0), blk(switch0-item1)] " +
                "blk(switch0-default) $.b0\n" +
                "}\n" +
                ".block switch0-item0 {\n" +
                "load res(#1) $.t0\n" +
                "ret $.t0\n" +
                "}\n" +
                ".block switch0-item1 {\n" +
                "load res(#2) $.t1\n" +
                "ret $.t1\n" +
                "}\n" +
                ".block switch0-default {\n" +
                "load res(#3) $.t2\n" +
                "ret $.t2\n" +
                "}\n");
            // 结构性事实：§16.6 操作数形状与 .vars 的 .breakid 条目（§9.3）
            var classifyFn = module.Functions.Single(f => f.Symbol == "$classify(x:.i32)@.i32");
            var switchInstruction = classifyFn.Blocks[0].Instructions
                .Single(i => i is SwitchInstruction);
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
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（switch 表去重）", module2);
            TestHarness.CheckTrue("case 集相同的两个 switch 共享一张表",
                module2.Resources.Count(r => r is BilSwitchTableResource) == 1
                && module2.Functions.SelectMany(f => f.Blocks[0].Instructions)
                    .Where(i => i is SwitchInstruction)
                    .Select(i => ((BilResourceOperand)i.Operands[1]).Resource.Name)
                    .Distinct().Count() == 1,
                string.Join(", ", module2.Resources.Select(r => r.Name)));
        }

        // ===== S7d：pattern switch 不到 P4b（P4a 已降为 if 链）=====
        private static void TestPatternSwitchEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 5\n" +
                "    var label = switch (x) {\n" +
                "        (_ > 10) -> { return@_ 1 }\n" +
                "        default -> { return@_ 0 }\n" +
                "    }\n" +
                "    return label\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（pattern switch 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（pattern switch 发射）", module);
            var main = module.Functions.Single(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("pattern switch 降为 if 链（无 switch 指令）",
                main.Blocks.SelectMany(b => b.Instructions).All(i => i is not SwitchInstruction)
                && main.Blocks.SelectMany(b => b.Instructions).Any(i => i is IfInstruction));
            TestHarness.CheckTrue("无 switch-table 资源",
                module.Resources.All(r => r is not BilSwitchTableResource));
            // selector 物化一次（.s1），pattern 条件引用它而非重复求值
            BilTestHarness.CheckFnShape("pattern 链多 block 文本", module, "$main()@.i32",
                ".vars { .i32 x, .i32 label, .i32 .s0, .i32 .s1, .i32 .t0, .i32 .t1, " +
                ".bool .t2, .i32 .t3, .i32 .t4 }\n" +
                ".block entry entrypoint {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $x\n" +
                "set.var $x $.s1\n" +
                "load res(#1) $.t1\n" +
                "cmp.gt $.s1 $.t1 $.t2\n" +
                "if $.t2 blk(if0-then) blk(if0-else)\n" +
                "set.var $.s0 $label\n" +
                "ret $label\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(#2) $.t3\n" +
                "set.var $.t3 $.s0\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#3) $.t4\n" +
                "set.var $.t4 $.s0\n" +
                "}\n");
        }

        // ===== S7d：throw → §16.9 单操作数指令 =====
        private static void TestThrowEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func fail(): i32 {\n" +
                "    throw new core.Exception()\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（throw 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（throw 发射）", module);
            var fail = module.Functions.Single(f => f.Symbol == "$fail()@.i32");
            BilTestHarness.CheckFnShape("fail 指令与 .vars", module, "$fail()@.i32",
                ".vars { core::Exception .t0 }\n" +
                "new type(core::Exception) $.t0 []\n" +
                "throw $.t0\n");
            var throwInstruction = fail.Blocks[0].Instructions.Single(i => i is ThrowInstruction);
            TestHarness.CheckTrue("throw 单操作数（§16.9）",
                throwInstruction.Operands.Count == 1
                && throwInstruction.Operands[0] is BilVariableOperand);
            // throw 是终止指令：entry 块落尾不补 ret（§9.4 补 ret 逻辑只看 ret）
            TestHarness.CheckTrue("throw 终止后无赘余 ret",
                fail.Blocks[0].Instructions.Last() is ThrowInstruction);
        }

        // ===== S7e：cast 发射（§12.1/§12.2）=====
        private static void TestCastEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n" +
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（cast 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（cast 发射）", module);
            BilTestHarness.CheckFnShape("as 指令文本", module, "$f(s:.string)@.string",
                ".vars { .string .t0 }\n" +
                "cast $s $.t0 type(.string)\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("as? 指令文本", module, "$g(s:.string)@.nullable<.string>",
                ".vars { .nullable<.string> .t0 }\n" +
                "cast.safe $s $.t0 type(.string)\n" +
                "ret $.t0\n");
            // 结构性事实：§12.1 三操作数形状（SOURCE RESULT type(TARGET_TYPE)）
            var castInstruction = module.Functions.Single(f => f.Symbol == "$f(s:.string)@.string")
                .Blocks[0].Instructions.Single(i => i is CastInstruction);
            TestHarness.CheckTrue("cast 三操作数（§12.1）",
                castInstruction.Operands.Count == 3
                && castInstruction.Operands[0] is BilVariableOperand
                && castInstruction.Operands[1] is BilVariableOperand
                && castInstruction.Operands[2] is BilTypeOperand);
        }

        // ===== S7f：字符串插值端到端（§3.8：toString/add 链 + 装箱 cast + §11.2 拼接）=====
        private static void TestStringInterpolationEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func main() {\n" +
                "    var name = \"world\"\n" +
                "    var count = 3\n" +
                "    core.io.Console.println(\"Hello ${name}, count=${count + 1}\")\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（插值）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（插值）", module);
            BilTestHarness.CheckFnShape("main 指令与 .vars（插值端到端）", module, "$main()@.void",
                ".vars { .string name, .i32 count, .string .t0, .i32 .t1, .string .t2, " +
                ".string .t3, .string .t4, .string .t5, .i32 .t6, .i32 .t7, .any .t8, " +
                ".string .t9, .string .t10 }\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $name\n" +
                "load res(#1) $.t1\n" +
                "set.var $.t1 $count\n" +
                "load res(#2) $.t2\n" +
                "add $.t2 $name $.t3\n" +
                "load res(#3) $.t4\n" +
                "add $.t3 $.t4 $.t5\n" +
                "load res(#4) $.t6\n" +
                "add $count $.t6 $.t7\n" +
                "cast $.t7 $.t8 type(.any)\n" +
                "invoke fn(core::Any$toString()@.string) $.t9 [$.t8]\n" +
                "add $.t5 $.t9 $.t10\n" +
                "invoke.noret fn(core.io::Console$.static.println(text:.string)@.void) [$.t10]\n" +
                "ret\n");
            // 结构性事实：String 段直拼无 toString；非 String 段一经 cast 一 invoke
            var mainInstructions = module.Functions.Single(f => f.Symbol == "$main()@.void")
                .Blocks[0].Instructions;
            TestHarness.CheckTrue("toString 调用恰一次（仅非 String 段）",
                mainInstructions.Count(i => i is InvokeInstruction) == 1);
        }

        // ===== S7f：`?.` 发射（§3.4 脱糖：null 检查 + if + unwrap/wrap cast）=====
        private static void TestSafeAccessEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("全管线无诊断（?.）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（?.）", module);
            BilTestHarness.CheckFnShape("f 指令与 .vars（?. 发射）",
                module, "$f(u:.nullable<User>)@.nullable<.string>",
                ".vars { .nullable<User> .s0, .nullable<.string> .s1, .nullable<.string> .t0, " +
                ".nullable<User> .t1, .bool .t2, User .t3, .string .t4, .nullable<.string> .t5 }\n" +
                ".block entry entrypoint {\n" +
                "set.var $u $.s0\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $.s1\n" +
                "load res(#1) $.t1\n" +
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
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "pub func g(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("全管线无诊断（if?）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（if?）", module);
            BilTestHarness.CheckFnShape("g 指令与 .vars（if? 发射）",
                module, "$g(u:.nullable<User>)@User",
                ".vars { .nullable<User> .s0, User .s1, .nullable<User> .t0, .bool .t1, " +
                "User .t2, .string .t3, User .t4 }\n" +
                ".block entry entrypoint {\n" +
                "set.var $u $.s0\n" +
                "load res(#0) $.t0\n" +
                "cmp.ne $.s0 $.t0 $.t1\n" +
                "if $.t1 blk(if0-then) blk(if0-else)\n" +
                "ret $.s1\n" +
                "}\n" +
                ".block if0-then {\n" +
                "cast $.s0 $.t2 type(User)\n" +
                "set.var $.t2 $.s1\n" +
                "}\n" +
                ".block if0-else {\n" +
                "load res(#1) $.t3\n" +
                "new type(User) $.t4 [$.t3]\n" +
                "set.var $.t4 $.s1\n" +
                "}\n");
        }

        // ===== S7f：解构发射（§3.4 精确字段读取；基类泛型字段访问）=====
        private static void TestDestructuringEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "pub func h(): String {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "    return k\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（解构）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（解构）", module);
            BilTestHarness.CheckFnShape("h 指令与 .vars（解构发射）", module, "$h()@.string",
                ".vars { .string k, .i32 v, Entry .s0, .string .t0, .i32 .t1, Entry .t2, " +
                ".string .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "load res(#1) $.t1\n" +
                "new type(Entry) $.t2 [$.t0, $.t1]\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.s0 $.t3 field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.var $.t3 $k\n" +
                "get.field $.s0 $.t4 field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "set.var $.t4 $v\n" +
                "ret $k\n");
            BilTestHarness.CheckFnShape("init 内基类字段写入（替换后类型）",
                module, "Entry$init(k:.string,v:.i32)@.void",
                ".vars {  }\n" +
                "set.field $k $.this field(core::Pair#key@.generic<$.generic.TKey>)\n" +
                "set.field $v $.this field(core::Pair#value@.generic<$.generic.TValue>)\n" +
                "ret\n");
        }

        // ===== S7e：try/catch/finally 发射（§16.7 + §18.5 catch-table）=====
        private static void TestTryEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（try 发射）", module);
            var f = module.Functions.Single(fn => fn.Symbol == "$f()@.void");
            BilTestHarness.CheckFnShape("try 多 block 文本", module, "$f()@.void",
                ".vars { DerivedError d, MyError e, .nullable<core::Exception> x, " +
                "DerivedError .t0, DerivedError .t1, MyError .t2 }\n" +
                ".block entry entrypoint {\n" +
                "try blk(try0-body) $x res(#0) blk(try0-finally)\n" +
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
            var tryInstruction = f.Blocks[0].Instructions.Single(i => i is TryInstruction);
            TestHarness.CheckTrue("try 四操作数（body/slot/表/finally）",
                tryInstruction.Operands.Count == 4
                && tryInstruction.Operands[0] is BilBlockOperand
                && tryInstruction.Operands[1] is BilVariableOperand
                && tryInstruction.Operands[2] is BilResourceOperand
                && tryInstruction.Operands[3] is BilBlockOperand);
            // §18.5 catch-table：多行形态、元素保序（表序即匹配序）
            var catchTable = module.Resources.OfType<BilCatchTableResource>().Single();
            TestHarness.Check("catch-table 元素（保序）",
                string.Join("\n", catchTable.Entries.Select(e => e.Render())),
                "type(DerivedError) -> blk(try0-catch0)\n" +
                "type(MyError) -> blk(try0-catch1)");
            TestHarness.CheckTrue("catch-table 多行形态（§18.5）",
                BilWriter.Write(module).Contains("catch-table {\n"));
        }

        // ===== S7e：seq 发射（§16.1 call 化 + §9.6 volatile 修饰符）=====
        private static void TestSeqEmission()
        {
            // 语句形态：seq / volatile seq 各一
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
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
            BilTestHarness.CheckBilValid("验证器零错误（seq 语句发射）", module);
            BilTestHarness.CheckFnShape("seq 语句多 block 文本", module, "$s()@.void",
                ".vars { .i32 x, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0)\n" +
                "call blk(seq1)\n" +
                "ret\n" +
                "}\n" +
                ".block seq0 {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $x\n" +
                "}\n" +
                ".block seq1 volatile {\n" +
                "invoke.noret fn($work()@.void) []\n" +
                "}\n");

            // 表达式形态（P4a 已脱糖为前置 seq 块写合成局部）+ volatile 变体
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n" +
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（seq 表达式发射）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（seq 表达式发射）", module2);
            BilTestHarness.CheckFnShape("seq 表达式多 block 文本", module2, "$se()@.i32",
                ".vars { .i32 .s0, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0)\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "}\n");
            BilTestHarness.CheckFnShape("volatile seq 表达式多 block 文本", module2, "$sv()@.i32",
                ".vars { .i32 .s0, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0)\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 volatile {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "}\n");
            // 结构性事实：§16.1 call 单操作数 blk(...)（不建栈帧）
            var callInstruction = module2.Functions.Single(f => f.Symbol == "$se()@.i32")
                .Blocks[0].Instructions.Single(i => i is CallBlockInstruction);
            TestHarness.CheckTrue("call 单操作数 blk（§16.1）",
                callInstruction.Operands.Count == 1
                && callInstruction.Operands[0] is BilBlockOperand);
        }

        // ===== S8a：is/supers/with 发射（§12.3；黄金文本经 --emit-bil 冒烟核定）=====
        private static void TestTypeCheckEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "wrapper Serializable { }\n" +
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "pub func f(d: Dog): bool {\n    return d is Animal\n}\n" +
                "pub func g(d: Dog): bool {\n    return d supers Animal\n}\n" +
                "pub func h(d: Dog): bool {\n    return d with Serializable\n}\n" +
                "pub func k(d: Dog): bool {\n    var t = typeOf(d)\n    return d is t\n}\n");
            CheckNoErrors("全管线无诊断（类型谓词发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（类型谓词发射）", module);
            BilTestHarness.CheckFnShape("type.is 指令与 .vars", module, "$f(d:Dog)@.bool",
                ".vars { .bool .t0 }\n" +
                "type.is $d type(Animal) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("type.supers 指令与 .vars", module, "$g(d:Dog)@.bool",
                ".vars { .bool .t0 }\n" +
                "type.supers $d type(Animal) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("type.with 指令与 .vars", module, "$h(d:Dog)@.bool",
                ".vars { .bool .t0 }\n" +
                "type.with $d type(Serializable) $.t0\n" +
                "ret $.t0\n");
            // 动态形态：getid.var 前置（typeOf 值形态）+ type.is.indirect 三变量操作数
            BilTestHarness.CheckFnShape("type.is.indirect 指令与 .vars（getid.var 前置）",
                module, "$k(d:Dog)@.bool",
                ".vars { .typeid<Dog> t, .typeid<Dog> .t0, .bool .t1 }\n" +
                "getid.var $d $.t0\n" +
                "set.var $.t0 $t\n" +
                "type.is.indirect $d $t $.t1\n" +
                "ret $.t1\n");
            // 结构性事实：§12.3 静态三操作数形状（VALUE type(TARGET_TYPE) RESULT_BOOL）
            var isInstruction = module.Functions.Single(f => f.Symbol == "$f(d:Dog)@.bool")
                .Blocks[0].Instructions
                .Single(i => i is DirectTypeCheckInstruction { Kind: BilTypeCheckKind.Is });
            TestHarness.CheckTrue("type.is 三操作数（§12.3）",
                isInstruction.Operands.Count == 3
                && isInstruction.Operands[0] is BilVariableOperand
                && isInstruction.Operands[1] is BilTypeOperand
                && isInstruction.Operands[2] is BilVariableOperand);
            // 结构性事实：§12.3 动态三操作数全变量（VALUE TYPEID_VAR RESULT_BOOL）
            var indirectInstruction = module.Functions.Single(f => f.Symbol == "$k(d:Dog)@.bool")
                .Blocks[0].Instructions
                .Single(i => i is IndirectTypeCheckInstruction { Kind: BilTypeCheckKind.Is });
            TestHarness.CheckTrue("type.is.indirect 三操作数全变量（§12.3）",
                indirectInstruction.Operands.Count == 3
                && indirectInstruction.Operands.All(o => o is BilVariableOperand));
        }

        // ===== S8a：typeOf 发射（§12.5；黄金文本经 --emit-bil 冒烟核定）=====
        private static void TestTypeOfEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "pub func m(): Type\\<Animal> {\n    return typeOf(Animal)\n}\n" +
                "pub func n(d: Dog): Type\\<Dog> {\n    return typeOf(d)\n}\n");
            CheckNoErrors("全管线无诊断（typeOf 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（typeOf 发射）", module);
            BilTestHarness.CheckFnShape("getid.type 指令与 .vars（类型形态）",
                module, "$m()@.typeid<Animal>",
                ".vars { .typeid<Animal> .t0 }\n" +
                "getid.type type(Animal) $.t0\n" +
                "ret $.t0\n");
            BilTestHarness.CheckFnShape("getid.var 指令与 .vars（值形态）",
                module, "$n(d:Dog)@.typeid<Dog>",
                ".vars { .typeid<Dog> .t0 }\n" +
                "getid.var $d $.t0\n" +
                "ret $.t0\n");
            // 结构性事实：§12.5 getid.type 双操作数（type(TYPE_SYMBOL) TARGET_TYPEID）
            var getIdType = module.Functions.Single(f => f.Symbol == "$m()@.typeid<Animal>")
                .Blocks[0].Instructions.Single(i => i is GetIdTypeInstruction);
            TestHarness.CheckTrue("getid.type 双操作数（§12.5）",
                getIdType.Operands.Count == 2
                && getIdType.Operands[0] is BilTypeOperand
                && getIdType.Operands[1] is BilVariableOperand);
            // 结构性事实：§12.5 getid.var 双操作数（VALUE TARGET_TYPEID）
            var getIdVar = module.Functions.Single(f => f.Symbol == "$n(d:Dog)@.typeid<Dog>")
                .Blocks[0].Instructions.Single(i => i is GetIdVarInstruction);
            TestHarness.CheckTrue("getid.var 双操作数全变量（§12.5）",
                getIdVar.Operands.Count == 2
                && getIdVar.Operands.All(o => o is BilVariableOperand));
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
            var module = BilEmitter.Emit(unit, new[] { body }, "future");
            TestHarness.CheckSemanticError("未覆盖节点报 P4 Error", unit.Diagnostics,
                "not supported by minimal emission");
            // 夹具修补：手工 MethodSymbol 未经 P1 收集，模块缺其声明——补上
            // 以聚焦「坏函数体跳过」本身的结构健康
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$future()@.void",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            // 坏函数体跳过后产出的模块仍应过验证器
            BilTestHarness.CheckBilValid("验证器零错误（未覆盖节点跳过坏函数体）", module);
        }
    }
}
