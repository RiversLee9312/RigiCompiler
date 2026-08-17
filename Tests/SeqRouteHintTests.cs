using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// §18.1 rigi.seq-route hint 的发射与消费（BIL §18.1/§21.4 route
    /// dispatcher 分组）：混合形态（产值臂与逃逸臂并存）的 seq/if/switch
    /// 表达式结果局部在 fall-through 路径的精确 DA——修复前 verifier
    /// §21.4 以全臂交集保守拒绝（动态上路 ≠0 时该读不可达），修复后
    /// emitter 在每条标准 route dispatcher 尾链首发 hint、verifier 按
    /// V0–V4 校验并分组消费。
    /// 覆盖：混合 seq/if/switch 三形态端到端（过验证器 + VM 两路径输出）、
    /// 手工 BIL 正例（带正确 hint 过验证器；同模块无 hint 必报 §21.4）、
    /// 手工 BIL 负例（畸形 JSON / route 名不存在 / route 非 .i32 / 比较
    /// 常量与写入不匹配——均静默忽略 hint、退回保守、§21.4 照报，且不
    /// 产生任何 hint 相关新诊断）。
    /// </summary>
    public static class SeqRouteHintTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestMixedSeqExpressionEndToEnd();
            TestMixedIfExpressionEndToEnd();
            TestMixedSwitchExpressionEndToEnd();
            TestHandModuleWithHintPasses();
            TestHandModuleWithoutHintRejected();
            TestMalformedHintJsonIgnored();
            TestUnknownRouteNameIgnored();
            TestNonI32RouteIgnored();
            TestMismatchedChainConstantIgnored();
            return TestHarness.Summary("SeqRouteHint");
        }

        // ===== 端到端：混合形态三兄弟（产值臂 + 逃逸臂并存）=====

        // 混合 seq 表达式：内层 seq 一臂 return@_ 产值、一臂 return@ 外层
        // 标签逃逸——修复前 §21.4 拦「$.s1 在赋值前被读取」
        private static void TestMixedSeqExpressionEndToEnd()
        {
            TestHarness.Section("混合 seq 表达式端到端");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = seq {\n" +
                "            if (flag) {\n" +
                "                return@_ 1\n" +
                "            } else {\n" +
                "                return@decide \"escaped\"\n" +
                "            }\n" +
                "        }\n" +
                "        return@decide \"normal\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            CheckMixedEndToEnd("混合 seq", source, "normal\nescaped\n");
        }

        // 混合 if 表达式：if 表达式一臂 return@_ 产值、一臂 return@ 逃逸
        private static void TestMixedIfExpressionEndToEnd()
        {
            TestHarness.Section("混合 if 表达式端到端");
            var source =
                "pub func pick(flag: bool): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = if (flag) {\n" +
                "            return@_ 1\n" +
                "        } else {\n" +
                "            return@decide \"escaped\"\n" +
                "        }\n" +
                "        return@decide \"normal\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(true))\n" +
                "    core.io.Console.println(pick(false))\n" +
                "    return 0\n" +
                "}\n";
            CheckMixedEndToEnd("混合 if", source, "normal\nescaped\n");
        }

        // 混合 switch 表达式（值匹配）：一臂 return@decide 逃逸，余臂产值
        private static void TestMixedSwitchExpressionEndToEnd()
        {
            TestHarness.Section("混合 switch 表达式端到端");
            var source =
                "pub func pick(n: i32): String {\n" +
                "    var result: String = seq named decide {\n" +
                "        var t: i32 = switch (n) {\n" +
                "            (1) -> { return@_ 10 }\n" +
                "            (2) -> { return@decide \"escaped\" }\n" +
                "            default -> { return@_ 0 }\n" +
                "        }\n" +
                "        return@decide \"normal\"\n" +
                "    }\n" +
                "    return result\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(pick(1))\n" +
                "    core.io.Console.println(pick(2))\n" +
                "    core.io.Console.println(pick(9))\n" +
                "    return 0\n" +
                "}\n";
            CheckMixedEndToEnd("混合 switch", source, "normal\nescaped\nnormal\n");
        }

        private static void CheckMixedEndToEnd(string label, string source, string expectedStdout)
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors(label + " 无诊断", unit);
            BilTestHarness.CheckBilValid(label + " BIL 过验证器", module);
            var result = BilVm.Run(module);
            TestHarness.CheckTrue(label + " VM 无异常", result.Exception == null,
                result.Exception?.ToString() ?? "");
            TestHarness.Check(label + " VM 输出", result.Stdout, expectedStdout);
            TestHarness.CheckTrue(label + " main 返回 0",
                result.ReturnValue is VmI32 exitCode && exitCode.Value == 0,
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

        // ===== 手工 BIL：混合形态模块（双层 region，内层混合臂）=====

        // 正例：带正确 hint 的混合形态模块过验证器
        private static void TestHandModuleWithHintPasses()
        {
            TestHarness.Section("rigi.seq-route hint：手工模块正例");
            var (module, _, _, _) = SeqRouteModule(out _, out _, out _);
            BilTestHarness.CheckBilValid("带正确 hint 的混合形态模块过验证器", module);
        }

        // 正例对照：同模块去掉 hint 必报 §21.4（保守全合并拒绝混合形态）
        private static void TestHandModuleWithoutHintRejected()
        {
            var (module, seq0, seq1, _) = SeqRouteModule(out _, out _, out _);
            RemoveHints(seq0);
            RemoveHints(seq1);
            CheckRejectedWithConservativeFallback("无 hint 同模块", module);
        }

        // 负例：hint 资源是畸形 JSON——忽略 hint，§21.4 照报。
        // （统一弄坏内层 seq1 的 hint：只弄坏外层时内层 hint 仍独立成立
        // ——seq1 落尾静态不可达、seq0 续点整个不可达，模块反而合法，
        // 属内层 hint 单独带来的可证明性提升，不是保守退回）
        private static void TestMalformedHintJsonIgnored()
        {
            var (module, _, seq1, _) = SeqRouteModule(out _, out _, out _);
            ReplaceHintResource(module, seq1, "\"not a json\"");
            CheckRejectedWithConservativeFallback("畸形 JSON hint", module);
        }

        // 负例：route 名不存在——忽略 hint，§21.4 照报
        private static void TestUnknownRouteNameIgnored()
        {
            var (module, _, seq1, _) = SeqRouteModule(out _, out _, out _);
            ReplaceHintResource(module, seq1,
                "\"{\\\"kind\\\":\\\"rigi.seq-route\\\",\\\"version\\\":1," +
                "\\\"route\\\":\\\"$.nope\\\"}\"");
            CheckRejectedWithConservativeFallback("route 名不存在", module);
        }

        // 负例：route 名存在但非 .i32 局部——忽略 hint，§21.4 照报
        private static void TestNonI32RouteIgnored()
        {
            var (module, _, seq1, _) = SeqRouteModule(out _, out _, out _);
            ReplaceHintResource(module, seq1,
                "\"{\\\"kind\\\":\\\"rigi.seq-route\\\",\\\"version\\\":1," +
                "\\\"route\\\":\\\"$flag\\\"}\"");
            CheckRejectedWithConservativeFallback("route 非 .i32 局部", module);
        }

        // 负例：尾链比较常量与 route 写入常量不匹配（写 0/1/2 比 3）——
        // 忽略 hint，§21.4 照报
        private static void TestMismatchedChainConstantIgnored()
        {
            var (module, _, seq1, three) = SeqRouteModule(out _, out _, out _);
            // seq1 尾链首链节的比较常量资源换成 i32 3（route $.s2 的写入
            // 只有 0/1/2）——hint 之后的第一个 load 即链节 load
            var hintIndex = seq1.Instructions.IndexOf(
                seq1.Instructions.OfType<HintInstruction>().Single());
            var chainLoad = (LoadInstruction)seq1.Instructions[hintIndex + 1];
            seq1.Instructions[hintIndex + 1] =
                new LoadInstruction(three, chainLoad.Target);
            CheckRejectedWithConservativeFallback("比较常量与写入不匹配", module);
        }

        // 负例公共断言：退回保守后 §21.4 照报「$.s1 在赋值前被读取」，且
        // 不产生任何 hint 相关新诊断（非法 hint = 没发一样）
        private static void CheckRejectedWithConservativeFallback(string label, BilModule module)
        {
            BilTestHarness.CheckBilInvalid(label + "：退回保守 §21.4 照报", module,
                "$.s1\" 在赋值前被读取");
            var errors = BilVerifier.Verify(module);
            TestHarness.CheckTrue(label + "：无 hint 相关新诊断",
                errors.All(e => !e.Message.Contains("hint") && !e.Message.Contains("seq-route")),
                string.Join("; ", errors.Select(e => e.ToString())));
        }

        private static void RemoveHints(BilBlock block)
        {
            block.Instructions.RemoveAll(i => i is HintInstruction);
        }

        // 把 block 内 hint 引用的资源原文替换为给定 BIL 字符串字面量原文
        //（资源对象替换——hint 指令持资源引用，换资源即改内容）
        private static void ReplaceHintResource(BilModule module, BilBlock block, string literalText)
        {
            var hint = block.Instructions.OfType<HintInstruction>().Single();
            var index = module.Resources.IndexOf(hint.Resource);
            module.Resources[index] = new BilScalarResource(hint.Resource.Name,
                BilScalarType.String, literalText);
            block.Instructions[block.Instructions.IndexOf(hint)] =
                new HintInstruction(module.Resources[index]);
        }

        // 混合形态手工模块（与 emitter 对 mixed.rg 的产出同构，全 .i32 化）：
        // entry 调 seq0（外层 region b0 无 route）；seq0 内 call blk(seq1)
        // b1 带 hint（route $.s3，写 0/1，单链节比 1 → if3-then break b0）；
        // seq1 内 if $flag 带 hint（route $.s2，写 0/1/2，双链节：比 1 →
        // if1-then break b1 / else 块内比 2 → if2-then 写 $.s3=1 break b1）。
        // then 臂写结果局部 $.s1（产值）、else 臂写 $.s0（逃逸）——$.s1 只在
        // 1 组边（relay break b1 落入 0 组）可见，seq0 续点 set.var $.s1 $t
        // 读取它：带 hint 过验证器，无 hint/非法 hint 必报 §21.4
        private static (BilModule Module, BilBlock Seq0, BilBlock Seq1,
            BilScalarResource Three) SeqRouteModule(out BilScalarResource zero,
                out BilScalarResource one, out BilScalarResource two)
        {
            var module = new BilModule();
            zero = new BilScalarResource("R_Zero", BilScalarType.I32, "0");
            one = new BilScalarResource("R_One", BilScalarType.I32, "1");
            two = new BilScalarResource("R_Two", BilScalarType.I32, "2");
            var three = new BilScalarResource("R_Three", BilScalarType.I32, "3");
            var seven = new BilScalarResource("R_Seven", BilScalarType.I32, "7");
            var trueResource = new BilScalarResource("R_True", BilScalarType.Bool, "true");
            var hintS2 = new BilScalarResource("R_HintS2", BilScalarType.String,
                "\"{\\\"kind\\\":\\\"rigi.seq-route\\\",\\\"version\\\":1," +
                "\\\"route\\\":\\\"$.s2\\\"}\"");
            var hintS3 = new BilScalarResource("R_HintS3", BilScalarType.String,
                "\"{\\\"kind\\\":\\\"rigi.seq-route\\\",\\\"version\\\":1," +
                "\\\"route\\\":\\\"$.s3\\\"}\"");
            module.Resources.Add(zero);
            module.Resources.Add(one);
            module.Resources.Add(two);
            module.Resources.Add(three);
            module.Resources.Add(seven);
            module.Resources.Add(trueResource);
            module.Resources.Add(hintS2);
            module.Resources.Add(hintS3);
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[] { new BilAccessibilityModifier(BilAccessibility.Public) }));
            var main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            foreach (var (type, name) in new[]
            {
                (".bool", "flag"), (".i32", "t"), (".i32", "result"),
                (".i32", ".s0"), (".breakid", ".b0"), (".i32", ".s1"),
                (".breakid", ".b1"), (".breakid", ".b2"), (".i32", ".s2"),
                (".i32", ".s3"), (".breakid", ".b3"), (".breakid", ".b4"),
                (".breakid", ".b5"), (".i32", ".t0"), (".i32", ".t1"),
                (".i32", ".t2"), (".i32", ".t3"), (".i32", ".t4"),
                (".i32", ".t5"), (".i32", ".t6"), (".bool", ".t7"),
                (".i32", ".t8"), (".bool", ".t9"), (".i32", ".t10"),
                (".i32", ".t11"), (".bool", ".t12"), (".i32", ".t13"),
                (".bool", ".f0"),
            })
            {
                main.Vars.Add(new BilVarDeclaration(type, name));
            }

            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            var seq0 = new BilBlock("seq0");
            var seq1 = new BilBlock("seq1");
            var if0Then = new BilBlock("if0-then");
            var if0Else = new BilBlock("if0-else");
            var if1Then = new BilBlock("if1-then");
            var if1Else = new BilBlock("if1-else");
            var if2Then = new BilBlock("if2-then");
            var if3Then = new BilBlock("if3-then");

            entry.Instructions.Add(new LoadInstruction(trueResource, BilOp.Var(".f0")));
            entry.Instructions.Add(new SetVarInstruction(BilOp.Var(".f0"), BilOp.Var("flag")));
            entry.Instructions.Add(new CallBlockInstruction(seq0, BilOp.Var(".b0")));
            entry.Instructions.Add(new SetVarInstruction(BilOp.Var(".s0"), BilOp.Var("result")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("result")));

            seq0.Instructions.Add(new LoadInstruction(zero, BilOp.Var(".t0")));
            seq0.Instructions.Add(new SetVarInstruction(BilOp.Var(".t0"), BilOp.Var(".s3")));
            seq0.Instructions.Add(new CallBlockInstruction(seq1, BilOp.Var(".b1")));
            seq0.Instructions.Add(new HintInstruction(hintS3));
            seq0.Instructions.Add(new LoadInstruction(one, BilOp.Var(".t11")));
            seq0.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpEq,
                BilOp.Var(".s3"), BilOp.Var(".t11"), BilOp.Var(".t12")));
            seq0.Instructions.Add(new IfInstruction(BilOp.Var(".t12"), if3Then, null,
                BilOp.Var(".b5")));
            seq0.Instructions.Add(new SetVarInstruction(BilOp.Var(".s1"), BilOp.Var("t")));
            seq0.Instructions.Add(new LoadInstruction(seven, BilOp.Var(".t13")));
            seq0.Instructions.Add(new SetVarInstruction(BilOp.Var(".t13"), BilOp.Var(".s0")));

            seq1.Instructions.Add(new LoadInstruction(zero, BilOp.Var(".t1")));
            seq1.Instructions.Add(new SetVarInstruction(BilOp.Var(".t1"), BilOp.Var(".s2")));
            seq1.Instructions.Add(new IfInstruction(BilOp.Var("flag"), if0Then, if0Else,
                BilOp.Var(".b2")));
            seq1.Instructions.Add(new HintInstruction(hintS2));
            seq1.Instructions.Add(new LoadInstruction(one, BilOp.Var(".t6")));
            seq1.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpEq,
                BilOp.Var(".s2"), BilOp.Var(".t6"), BilOp.Var(".t7")));
            seq1.Instructions.Add(new IfInstruction(BilOp.Var(".t7"), if1Then, if1Else,
                BilOp.Var(".b4")));

            if0Then.Instructions.Add(new LoadInstruction(one, BilOp.Var(".t2")));
            if0Then.Instructions.Add(new SetVarInstruction(BilOp.Var(".t2"), BilOp.Var(".s1")));
            if0Then.Instructions.Add(new LoadInstruction(one, BilOp.Var(".t3")));
            if0Then.Instructions.Add(new SetVarInstruction(BilOp.Var(".t3"), BilOp.Var(".s2")));
            if0Then.Instructions.Add(new BreakInstruction(BilOp.Var(".b2")));

            if0Else.Instructions.Add(new LoadInstruction(two, BilOp.Var(".t4")));
            if0Else.Instructions.Add(new SetVarInstruction(BilOp.Var(".t4"), BilOp.Var(".s0")));
            if0Else.Instructions.Add(new LoadInstruction(two, BilOp.Var(".t5")));
            if0Else.Instructions.Add(new SetVarInstruction(BilOp.Var(".t5"), BilOp.Var(".s2")));
            if0Else.Instructions.Add(new BreakInstruction(BilOp.Var(".b2")));

            if1Then.Instructions.Add(new BreakInstruction(BilOp.Var(".b1")));

            if1Else.Instructions.Add(new LoadInstruction(two, BilOp.Var(".t8")));
            if1Else.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.CmpEq,
                BilOp.Var(".s2"), BilOp.Var(".t8"), BilOp.Var(".t9")));
            if1Else.Instructions.Add(new IfInstruction(BilOp.Var(".t9"), if2Then, null,
                BilOp.Var(".b3")));

            if2Then.Instructions.Add(new LoadInstruction(one, BilOp.Var(".t10")));
            if2Then.Instructions.Add(new SetVarInstruction(BilOp.Var(".t10"), BilOp.Var(".s3")));
            if2Then.Instructions.Add(new BreakInstruction(BilOp.Var(".b1")));

            if3Then.Instructions.Add(new BreakInstruction(BilOp.Var(".b0")));

            main.Blocks.Add(entry);
            main.Blocks.Add(seq0);
            main.Blocks.Add(seq1);
            main.Blocks.Add(if0Then);
            main.Blocks.Add(if0Else);
            main.Blocks.Add(if1Then);
            main.Blocks.Add(if1Else);
            main.Blocks.Add(if2Then);
            main.Blocks.Add(if3Then);
            module.Functions.Add(main);
            return (module, seq0, seq1, three);
        }

        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
        }
    }
}
