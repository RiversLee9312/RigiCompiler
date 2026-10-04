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
        // Mir 职责；与主文件共享同一类型、字段及生命周期。

        // ===== 驻留符号表 =====

        private static void TestSymbolTable()
        {
            var gate = BilGate.Accept(MinimalValidBil, "ok.bil");
            var context = new MwContext(gate.Module!);
            var symbols = context.Symbols;

            var point = symbols.FindType("Point");
            TestHarness.CheckTrue("类型已登记", point != null);
            TestHarness.CheckTrue("本地类型非外部", point is { IsExternal: false });
            TestHarness.CheckTrue("类型成员数", point!.Members.Count == 2);
            TestHarness.CheckTrue("类型成员 canonical", point.Members[0].Canonical == "Point#x@.i32");

            var memberX = symbols.FindMember("Point#x@.i32");
            TestHarness.CheckTrue("成员已登记", memberX != null);
            TestHarness.CheckTrue("驻留=引用相等（类型表↔成员表同一对象）",
                ReferenceEquals(point.Members[0], memberX));
            TestHarness.CheckTrue("成员 Owner 回指宿主类型",
                ReferenceEquals(memberX!.Owner, point));
            TestHarness.CheckTrue("驻留=引用相等（重复查找同一对象）",
                ReferenceEquals(symbols.FindType("Point"), point));

            var external = symbols.FindType("core.String");
            TestHarness.CheckTrue("外部类型已登记", external is { IsExternal: true });
            TestHarness.CheckTrue("外部类型成员已登记",
                symbols.FindMember("core.String$get_Length()@.i32") is { IsExternal: true });

            var main = symbols.FindMember("$main()@.i32");
            TestHarness.CheckTrue("全局函数已登记", main != null);
            TestHarness.CheckTrue("全局函数 Owner 为 null", main!.Owner == null);
            TestHarness.CheckTrue("GlobalMembers 含全局函数",
                symbols.GlobalMembers.Any(m => m.Canonical == "$main()@.i32"));

            TestHarness.CheckTrue("未登记符号查找为 null",
                symbols.FindType("Nope") == null && symbols.FindMember("Nope$f()@.void") == null);
        }

        // ===== MIR 构造 =====
        // 本节直调 MirBuilder.Build：断言未改写的原始 MIR（访问器/数组
        // 降级归 Passes/，由 CreateDefault 管线用例覆盖）

        private static void TestMirConstruction()
        {
            // 编译器真实产物（println 全链）经文本往返 + 门禁后进 MIR
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    Console.println(\"hello\")\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "mir.bil");
            TestHarness.CheckTrue("MIR 输入门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);

            TestHarness.CheckTrue("MwContext 挂载 MIR", ReferenceEquals(context.Mir, mir));
            // MW9b-G：core 异常类型 init 族 + getMessage 恒可达白名单
            //（守卫点抛出是发射期引用），函数集必含 main + println + 白名单族
            TestHarness.CheckTrue("MIR 函数含 main + println",
                mir.Functions.Any(f => f.Symbol.Canonical == "$main()@.i32")
                && mir.Functions.Any(f => f.Symbol.Canonical.Contains("println")),
                string.Join(", ", mir.Functions.Select(f => f.Symbol.Canonical)));
            TestHarness.CheckTrue("MIR 函数含异常白名单（除零 init）",
                mir.Functions.Any(f => f.Symbol.Canonical
                    == "core::DividedByZeroException$init()@.void"),
                string.Join(", ", mir.Functions.Select(f => f.Symbol.Canonical)));

            var main = mir.Functions.First(f => f.Symbol.Canonical == "$main()@.i32");
            TestHarness.CheckTrue("main 是入口", main.IsEntrypoint);
            TestHarness.CheckTrue("main 返回 .i32", main.ReturnType.Key == "i32");
            TestHarness.CheckTrue("main 单 block", main.Blocks.Count == 1);
            TestHarness.CheckTrue("main 终结符是带值 MirRet",
                main.Blocks[0].Terminator is MirRet { Value: not null });
            TestHarness.CheckTrue("main 首指令是资源物化",
                main.Blocks[0].Instructions[0] is MirLoadResource);
            var printlnCall = main.Blocks[0].Instructions.OfType<MirCall>().FirstOrDefault();
            TestHarness.CheckTrue("main 含调用", printlnCall != null);
            TestHarness.CheckTrue("调用目标是 println 驻留符号",
                printlnCall!.Target.Canonical.Contains("println"));
            TestHarness.CheckTrue("void 调用无结果槽", printlnCall.Result == null);
            TestHarness.CheckTrue("调用目标与符号表同一对象（驻留）",
                ReferenceEquals(printlnCall.Target,
                    context.Symbols.FindMember(printlnCall.Target.Canonical)));

            var println = mir.Functions.First(f => f.Symbol.Canonical.Contains("println"));
            TestHarness.CheckTrue("println 非入口", !println.IsEntrypoint);
            TestHarness.CheckTrue("println 返回 .void", println.ReturnType.IsVoid);
            TestHarness.CheckTrue("println 单 string 参数",
                println.Parameters.Count == 1 && println.Parameters[0].Type.IsString);
            TestHarness.CheckTrue("println 体内含 string 拼接运算",
                println.Blocks[0].Instructions.OfType<MirBinaryIntrinsic>().Any(
                    b => b.Op == Bil.BilBinaryOp.Add && b.LeftType.IsString));
            TestHarness.CheckTrue("局部查找可用", println.FindLocal(println.Parameters[0].Name)
                .Type.IsString);
        }

        // ===== MIR 控制流直译（MW3）=====
        // 直调 MirBuilder.Build：CFG 形状与访问器/数组降级无关

        private static void TestMirControlFlow()
        {
            // if/else → CondBranch + 双分支 ret + 不可达汇聚块 unreachable 收尾
            var (_, ifTextModule, ifText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    if (1 < 2) { return 1 } else { return 2 }\n" +
                "}\n");
            ifText = BilWriter.Write(ifTextModule);
            var gate = BilGate.Accept(ifText, "if.bil");
            TestHarness.CheckTrue("if 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);
            var main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("if：entry 终结符是条件跳转",
                main.Blocks[0].Terminator is MirCondBranch);
            var condBranch = (MirCondBranch)main.Blocks[0].Terminator;
            TestHarness.CheckTrue("if：双分支各自 ret",
                main.Blocks.Single(b => b.Id == condBranch.ThenTarget).Terminator is MirRet
                && main.Blocks.Single(b => b.Id == condBranch.ElseTarget).Terminator is MirRet);
            var merge = main.Blocks.Single(
                b => b.Id.StartsWith("mw.if.end.", StringComparison.Ordinal));
            TestHarness.CheckTrue("if：双分支均终结，汇聚块不可达收尾 unreachable",
                merge.Terminator is MirUnreachable);

            // while → entry Br(judge)；judge CondBranch(cond, body, exit)；body Br(judge)
            var (_, loopTextModule, loopText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 3) { x = x + 1 }\n" +
                "    return x\n" +
                "}\n");
            loopText = BilWriter.Write(loopTextModule);
            gate = BilGate.Accept(loopText, "loop.bil");
            TestHarness.CheckTrue("while 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("while：entry 终结符跳 judge",
                main.Blocks[0].Terminator is MirBranch { Target: "loop0-judge" });
            TestHarness.CheckTrue("while：judge 条件跳转（true body / false exit）",
                main.Blocks.Single(b => b.Id == "loop0-judge").Terminator is MirCondBranch cond
                && cond.ThenTarget == "loop0-body"
                && cond.ElseTarget.StartsWith("mw.loop.end.", StringComparison.Ordinal));
            TestHarness.CheckTrue("while：body 落出回 judge（enum 为 none）",
                main.Blocks.Single(b => b.Id == "loop0-body").Terminator
                    is MirBranch { Target: "loop0-judge" });
            TestHarness.CheckTrue("while：exit 块含带值 ret",
                main.Blocks.Single(b => b.Id.StartsWith("mw.loop.end.", StringComparison.Ordinal))
                    .Terminator is MirRet { Value: not null });

            // do-while → loop.rev：entry Br(body)；judge CondBranch(cond, body, exit)
            var (_, revTextModule, revText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < 3)\n" +
                "    return x\n" +
                "}\n");
            revText = BilWriter.Write(revTextModule);
            gate = BilGate.Accept(revText, "rev.bil");
            TestHarness.CheckTrue("do-while 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("do-while：entry 终结符跳 body（先执行）",
                main.Blocks[0].Terminator is MirBranch { Target: "loop0-body" });
            TestHarness.CheckTrue("do-while：judge 条件跳转回 body",
                main.Blocks.Single(b => b.Id == "loop0-judge").Terminator is MirCondBranch rev
                && rev.ThenTarget == "loop0-body"
                && rev.ElseTarget.StartsWith("mw.loop.end.", StringComparison.Ordinal));

            // switch → MirSwitch + item/default 各自成块
            var (_, switchTextModule, switchText) = BilTestHarness.EmitBilUnit(
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
            switchText = BilWriter.Write(switchTextModule);
            gate = BilGate.Accept(switchText, "switch.bil");
            TestHarness.CheckTrue("switch 模块门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            var classify = mir.Functions.Single(f => f.Symbol.Canonical.Contains("classify"));
            TestHarness.CheckTrue("switch：entry 终结符是 MirSwitch",
                classify.Blocks[0].Terminator is MirSwitch);
            var sw = (MirSwitch)classify.Blocks[0].Terminator;
            TestHarness.CheckTrue("switch：两 item + 常量表两元素 + default 目标",
                sw.ItemTargets.Count == 2 && sw.Table.Elements.Count == 2
                && sw.DefaultTarget == "switch0-default");
            TestHarness.CheckTrue("switch：item 块各自 ret",
                classify.Blocks.Single(b => b.Id == sw.ItemTargets[0]).Terminator is MirRet
                && classify.Blocks.Single(b => b.Id == sw.ItemTargets[1]).Terminator is MirRet);

            // break@outer → MirBranch 指向外层 loop 出口；continue → 内层 judge
            var (_, breakTextModule, breakText) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
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
            breakText = BilWriter.Write(breakTextModule);
            gate = BilGate.Accept(breakText, "break.bil");
            TestHarness.CheckTrue("嵌套标签循环门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            context = new MwContext(gate.Module!);
            mir = MirBuilder.Build(context);
            main = mir.Functions.Single(f => f.IsEntrypoint);
            TestHarness.CheckTrue("break@outer 解析到外层出口块（mw.loop.end.0）",
                main.Blocks.Any(b => b.Terminator is MirBranch { Target: "mw.loop.end.0" }));
            TestHarness.CheckTrue("continue 解析到内层 judge（loop1-judge）",
                main.Blocks.Any(b => b.Terminator is MirBranch { Target: "loop1-judge" }));
        }

        // ===== MIR try 展开（MW9a 第 B 棒）=====
        // 直调 MirBuilder.Build：try/catch/finally 子图形状（checked-flag
        // 便携模型；ExcTarget==null 的解析与发射归后续棒）

        private static MirModule BuildMirModule(string source, string fileName)
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(source);
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, fileName);
            TestHarness.CheckTrue(fileName + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            return MirBuilder.Build(new MwContext(gate.Module!));
        }

    }
}
