using System.Collections.Generic;
using System.Linq;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter 控制流发射测试（if 语句/if 表达式/短路 and/or/while 与 do-while/for/常量 switch/pattern switch/throw）

    public static partial class BilEmitterTests
    {
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
            // 两处 0 共用同一资源（stdlib 基线 4 条资源在前；2 新增一条，
            // S9f collections 抽象基类移除 i32 0 字面量）
            BilTestHarness.CheckResShape("资源（0 去重）", module,
                "#0 = string \"\\n\"\n#1 = bool false\n#2 = i32 1\n" +
                "#3 = bool true\n#4 = i32 0\n#5 = i32 2");
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
            // 0 新增一条（S9f collections 抽象基类移除 stdlib 的 i32 0）
            BilTestHarness.CheckResShape("资源（1 去重）", module,
                "#0 = string \"\\n\"\n#1 = bool false\n#2 = i32 1\n" +
                "#3 = bool true\n#4 = i32 0\n#5 = i32 2");
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
            // 合成常量与源码字面量同键去重：true/false 共享 stdlib 基线；
            // 0 新增一条（S9f collections 抽象基类移除 stdlib 的 i32 0）
            BilTestHarness.CheckResShape("资源（true/false 去重）", module,
                "#0 = string \"\\n\"\n#1 = bool false\n#2 = i32 1\n" +
                "#3 = bool true\n#4 = i32 0");
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
                "#0 = string \"\\n\"\n#1 = bool false\n#2 = i32 1\n" +
                "#3 = bool true\n#4 = i32 0\n#5 = i32 3");
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
                "#0 = string \"\\n\"\n#1 = bool false\n#2 = i32 1\n" +
                "#3 = bool true\n#4 = i32 0\n#5 = i32 3");
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

        // ===== S7d：常量 switch → switch 指令（§16.6）+ switch-table 资源（§19.4）=====
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
            // 表元素只进表不产标量资源；#5 是 item1 分支体 return 2 的字面量；
            // #6 = i32 0 是 main 的 classify(1) 场景外字面量（S9f 基线顺移）
            BilTestHarness.CheckResShape("资源（switch-table 单行形态）", module,
                "#0 = string \"\\n\"\n#1 = bool false\n#2 = i32 1\n" +
                "#3 = bool true\n#4 = switch-table<.i32> { 1, 2 }\n#5 = i32 2\n#6 = i32 0");
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

            // 跨 fn 同表去重：case 集完全相同的两个 switch 共享一张 §19.4 表
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
    }
}
