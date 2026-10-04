using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // TryFinally 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestTryCatchFinally()
        {
            var caught = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.RuntimeException(\"boom\")\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        core.io.Console.println(\"catch\")\n" +
                "        return 7\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("try 落 catch", caught);
            TestHarness.Check("catch+finally 顺序", caught.Stdout, "catch\nfin\n");
            CheckI32("catch 返回 7", caught, 7);
            var normal = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        core.io.Console.println(\"try\")\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("try 正常 + finally", normal);
            TestHarness.Check("正常路径 finally", normal.Stdout, "try\nfin\n");
            CheckI32("正常路径返回", normal, 1);
            var inherit = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 4\n" +
                "    }\n" +
                "}\n");
            CheckOk("catch 兼容子类", inherit);
            CheckI32("IOException 命中 RuntimeException", inherit, 4);
            var miss = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "        return 0\n" +
                "    } catch (_: core.CastException) {\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckTrue("未命中 catch 传播",
                miss.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("IOException"),
                miss.Exception?.ToString() ?? "<null>");
            var nested = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.IOException(\"io\")\n" +
                "        } catch (_: core.CastException) {\n" +
                "            core.io.Console.println(\"no\")\n" +
                "        }\n" +
                "        return 0\n" +
                "    } catch (_: core.IOException) {\n" +
                "        core.io.Console.println(\"io\")\n" +
                "        return 5\n" +
                "    }\n" +
                "}\n");
            CheckOk("嵌套 try", nested);
            TestHarness.Check("嵌套 catch 外层", nested.Stdout, "io\n");
            CheckI32("嵌套返回 5", nested, 5);
            var rethrow = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new core.IOException(\"io\")\n" +
                "    } catch (_: core.IOException) {\n" +
                "        throw new core.RuntimeException(\"re\")\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"f\")\n" +
                "    }\n" +
                "}\n");
            TestHarness.Check("catch 再抛仍跑 finally", rethrow.Stdout, "f\n");
            TestHarness.CheckTrue("finally 后传播新异常",
                rethrow.Exception?.ExceptionObject is VmObject re
                && re.TypeRef.Contains("RuntimeException"),
                rethrow.Exception?.ToString() ?? "<null>");
            var finThrow = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } finally(_) {\n" +
                "        throw new core.RuntimeException(\"fin\")\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckTrue("finally throw 覆盖 ret",
                finThrow.Exception?.ExceptionObject is VmObject ft
                && ft.TypeRef.Contains("RuntimeException"),
                finThrow.Exception?.ToString() ?? "<null>");
            var castCatch = Run(
                "pub open class Animal { pub init() {} }\n" +
                "pub class Dog : Animal { pub init() {} }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        var a: Animal = new Animal()\n" +
                "        var d = (a as Dog)\n" +
                "        return 0\n" +
                "    } catch (_: core.CastException) {\n" +
                "        return 8\n" +
                "    }\n" +
                "}\n");
            CheckOk("CastException 可 catch", castCatch);
            CheckI32("cast 失败落 catch", castCatch, 8);
        }

        private static void TestThrowAcrossFunction()
        {
            var result = Run(
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        boom()\n" +
                "        return 0\n" +
                "    } catch (_: core.RuntimeException) {\n" +
                "        return 7\n" +
                "    }\n" +
                "}\n");
            CheckOk("throw 跨函数", result);
            CheckI32("跨函数 catch", result, 7);
            var uncaught = Run(
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return boom()\n" +
                "}\n");
            TestHarness.CheckTrue("无人捕获则 Failed",
                uncaught.Exception?.ExceptionObject is VmObject obj
                && obj.TypeRef.Contains("RuntimeException"),
                uncaught.Exception?.ToString() ?? "<null>");
        }

        private static void TestRetBreakContinueThroughFinally()
        {
            var ret = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 3\n" +
                "    } finally(_) {\n" +
                "        core.io.Console.println(\"fin\")\n" +
                "    }\n" +
                "}\n");
            CheckOk("ret 穿越 finally", ret);
            TestHarness.Check("ret 前 finally", ret.Stdout, "fin\n");
            CheckI32("ret 值保留", ret, 3);
            var brk = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        try {\n" +
                "            i = (i + 1)\n" +
                "            break\n" +
                "        } finally(_) {\n" +
                "            core.io.Console.println(\"f\")\n" +
                "        }\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckOk("break 穿越 finally", brk);
            TestHarness.Check("break 前 finally", brk.Stdout, "f\n");
            CheckI32("break 后 i", brk, 1);
            var cont = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    var n: i32 = 0\n" +
                "    while (i < 3) {\n" +
                "        i = (i + 1)\n" +
                "        try {\n" +
                "            continue\n" +
                "        } finally(_) {\n" +
                "            n = (n + 1)\n" +
                "        }\n" +
                "        n = (n + 100)\n" +
                "    }\n" +
                "    return n\n" +
                "}\n");
            CheckOk("continue 穿越 finally", cont);
            CheckI32("continue 只跑 finally", cont, 3);
        }

        // ===== §16.5 推广：call/if/try region 的 breakid matching 消费
        // （手工模块——frontend 不生成引用这些 token 的 break）=====
        private static void TestRegionBreakIdConsumption()
        {
            // (a) call blk region：块内 break 命中自身 breakid 被消费，
            // 续 call 的后一条指令（break 后指令不执行、不外溢越界）
            var callModule = RegionModuleSkeleton(out var callMain, out var callEntry);
            var callSeq = new BilBlock("seq0");
            callMain.Blocks.Add(callSeq);
            callEntry.Instructions.Add(LoadI32(callModule, 0, "a"));
            callEntry.Instructions.Add(new CallBlockInstruction(callSeq, BilOp.Var("b0")));
            callEntry.Instructions.Add(LoadI32(callModule, 7, "x"));
            callEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("x")));
            callEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            callSeq.Instructions.Add(LoadI32(callModule, 5, "a"));
            callSeq.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            callSeq.Instructions.Add(LoadI32(callModule, 9, "a"));
            var callResult = BilVm.Run(callModule);
            CheckOk("call region break 消费", callResult);
            CheckI32("call 后续行（5+7）", callResult, 12);

            // (b1) if region：then 内 break 命中消费，续 if 后指令
            var thenModule = RegionModuleSkeleton(out var thenMain, out var thenEntry);
            var thenBlock = new BilBlock("if0-then");
            var elseBlock = new BilBlock("if0-else");
            thenMain.Blocks.Add(thenBlock);
            thenMain.Blocks.Add(elseBlock);
            thenEntry.Instructions.Add(new LoadInstruction(thenModule.Resources[10],
                BilOp.Var("c")));
            thenEntry.Instructions.Add(new IfInstruction(BilOp.Var("c"), thenBlock, elseBlock,
                BilOp.Var("b0")));
            thenEntry.Instructions.Add(LoadI32(thenModule, 7, "x"));
            thenEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("x")));
            thenEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            thenBlock.Instructions.Add(LoadI32(thenModule, 5, "a"));
            thenBlock.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            thenBlock.Instructions.Add(LoadI32(thenModule, 9, "a"));
            elseBlock.Instructions.Add(LoadI32(thenModule, 9, "a"));
            var thenResult = BilVm.Run(thenModule);
            CheckOk("if then break 消费", thenResult);
            CheckI32("if then 后续行（5+7）", thenResult, 12);

            // (b2) if region：else 内 break 命中消费
            var elseModule = RegionModuleSkeleton(out var elseMain, out var elseEntry);
            var then2 = new BilBlock("if0-then");
            var else2 = new BilBlock("if0-else");
            elseMain.Blocks.Add(then2);
            elseMain.Blocks.Add(else2);
            elseEntry.Instructions.Add(new LoadInstruction(elseModule.Resources[11],
                BilOp.Var("c")));
            elseEntry.Instructions.Add(new IfInstruction(BilOp.Var("c"), then2, else2,
                BilOp.Var("b0")));
            elseEntry.Instructions.Add(LoadI32(elseModule, 7, "x"));
            elseEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("x")));
            elseEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            then2.Instructions.Add(LoadI32(elseModule, 9, "a"));
            else2.Instructions.Add(LoadI32(elseModule, 5, "a"));
            else2.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            else2.Instructions.Add(LoadI32(elseModule, 9, "a"));
            var elseResult = BilVm.Run(elseModule);
            CheckOk("if else break 消费", elseResult);
            CheckI32("if else 后续行（5+7）", elseResult, 12);

            // (c1) try region：body 内 break tryId——finally 先执行，
            // 再在 try 边界消费
            var bodyModule = RegionModuleSkeleton(out var bodyMain, out var bodyEntry);
            var tryBody = new BilBlock("try0-body");
            var tryFin = new BilBlock("try0-finally");
            bodyMain.Blocks.Add(tryBody);
            bodyMain.Blocks.Add(tryFin);
            bodyEntry.Instructions.Add(new TryInstruction(tryBody, BilOp.Var("slot"),
                EmptyCatchTable(bodyModule), tryFin, BilOp.Var("b0")));
            bodyEntry.Instructions.Add(LoadI32(bodyModule, 7, "x"));
            bodyEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("f"), BilOp.Var("a")));
            bodyEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("a")));
            bodyEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            tryBody.Instructions.Add(LoadI32(bodyModule, 5, "a"));
            tryBody.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            tryBody.Instructions.Add(LoadI32(bodyModule, 9, "a"));
            tryFin.Instructions.Add(LoadI32(bodyModule, 3, "f"));
            var bodyResult = BilVm.Run(bodyModule);
            CheckOk("try body break 经 finally 消费", bodyResult);
            CheckI32("finally 执行且 break 消费（5+3+7）", bodyResult, 15);

            // (c2) try region：catch handler 内 break tryId——handler 完成
            // 后经 finally 在 try 边界消费
            var catchModule = RegionModuleSkeleton(out var catchMain, out var catchEntry);
            var catchBody = new BilBlock("try0-body");
            var catchHandler = new BilBlock("try0-catch0");
            var catchFin = new BilBlock("try0-finally");
            catchMain.Blocks.Add(catchBody);
            catchMain.Blocks.Add(catchHandler);
            catchMain.Blocks.Add(catchFin);
            catchEntry.Instructions.Add(new TryInstruction(catchBody, BilOp.Var("slot"),
                MyErrCatchTable(catchModule, catchHandler), catchFin, BilOp.Var("b0")));
            catchEntry.Instructions.Add(LoadI32(catchModule, 7, "x"));
            catchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("h"), BilOp.Var("a")));
            catchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("f"), BilOp.Var("a")));
            catchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("a")));
            catchEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            catchBody.Instructions.Add(LoadI32(catchModule, 1, "a"));
            catchBody.Instructions.Add(new NewInstruction(BilOp.Type("MyErr"),
                BilOp.Var("obj"), Array.Empty<BilVariableOperand>()));
            catchBody.Instructions.Add(new ThrowInstruction(BilOp.Var("obj")));
            catchHandler.Instructions.Add(LoadI32(catchModule, 3, "h"));
            catchHandler.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            catchHandler.Instructions.Add(LoadI32(catchModule, 9, "h"));
            catchFin.Instructions.Add(LoadI32(catchModule, 5, "f"));
            var catchResult = BilVm.Run(catchModule);
            CheckOk("catch handler break 消费", catchResult);
            CheckI32("handler break 经 finally 消费（1+3+5+7）", catchResult, 16);

            // (c3) try region：finally 内 break tryId——finally 完成后在
            // try 边界消费（SavedCompletion=Normal 被 finally 的 Break 覆盖）
            var finModule = RegionModuleSkeleton(out var finMain, out var finEntry);
            var finBody = new BilBlock("try0-body");
            var finFin = new BilBlock("try0-finally");
            finMain.Blocks.Add(finBody);
            finMain.Blocks.Add(finFin);
            finEntry.Instructions.Add(new TryInstruction(finBody, BilOp.Var("slot"),
                EmptyCatchTable(finModule), finFin, BilOp.Var("b0")));
            finEntry.Instructions.Add(LoadI32(finModule, 7, "x"));
            finEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("f"), BilOp.Var("a")));
            finEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("a"), BilOp.Var("x"), BilOp.Var("a")));
            finEntry.Instructions.Add(new RetInstruction(BilOp.Var("a")));
            finBody.Instructions.Add(LoadI32(finModule, 5, "a"));
            finFin.Instructions.Add(LoadI32(finModule, 3, "f"));
            finFin.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            finFin.Instructions.Add(LoadI32(finModule, 9, "f"));
            var finResult = BilVm.Run(finModule);
            CheckOk("finally 内 break 消费", finResult);
            CheckI32("finally break 在 try 边界消费（5+3+7）", finResult, 15);

            // (c4) break tryId 不进 catch matching：body break 时 catch
            // 不触发（handler 不写 h），break 在 try 边界消费
            var noCatchModule = RegionModuleSkeleton(out var noCatchMain, out var noCatchEntry);
            var ncBody = new BilBlock("try0-body");
            var ncHandler = new BilBlock("try0-catch0");
            noCatchMain.Blocks.Add(ncBody);
            noCatchMain.Blocks.Add(ncHandler);
            noCatchEntry.Instructions.Add(LoadI32(noCatchModule, 0, "h"));
            noCatchEntry.Instructions.Add(new TryInstruction(ncBody, BilOp.Var("slot"),
                MyErrCatchTable(noCatchModule, ncHandler), null, BilOp.Var("b0")));
            noCatchEntry.Instructions.Add(LoadI32(noCatchModule, 7, "x"));
            noCatchEntry.Instructions.Add(new BinaryIntrinsicInstruction(BilBinaryOp.Add,
                BilOp.Var("h"), BilOp.Var("x"), BilOp.Var("x")));
            noCatchEntry.Instructions.Add(new RetInstruction(BilOp.Var("x")));
            ncBody.Instructions.Add(new BreakInstruction(BilOp.Var("b0")));
            ncHandler.Instructions.Add(LoadI32(noCatchModule, 9, "h"));
            var noCatchResult = BilVm.Run(noCatchModule);
            CheckOk("break 不进 catch matching", noCatchResult);
            CheckI32("catch 未触发且 break 消费（0+7）", noCatchResult, 7);
        }

        // ===== §16.7 finally(e) 基础语义：进入 finally 时仅 pending 为
        // Throw 才把异常对象写入 ExceptionSlot；Normal/Return/Break 等
        // 一切非 Throw completion 必须看到 null =====
        private static void TestFinallyExceptionSlotSemantics()
        {
            var normal = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "    } finally(e) {\n" +
                "        if (e == null) { core.io.Console.println(\"null\") }\n" +
                "        else { core.io.Console.println(\"exc\") }\n" +
                "    }\n" +
                "    return 5\n" +
                "}\n");
            CheckOk("Normal 进 finally(e)", normal);
            TestHarness.Check("Normal 路径 e 为 null", normal.Stdout, "null\n");
            CheckI32("Normal 后续行", normal, 5);

            var ret = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 3\n" +
                "    } finally(e) {\n" +
                "        if (e == null) { core.io.Console.println(\"null\") }\n" +
                "        else { core.io.Console.println(\"exc\") }\n" +
                "    }\n" +
                "}\n");
            CheckOk("Return 进 finally(e)", ret);
            TestHarness.Check("Return 路径 e 为 null", ret.Stdout, "null\n");
            CheckI32("Return 值保留", ret, 3);

            var brk = Run(
                "pub func main(): i32 {\n" +
                "    var i: i32 = 0\n" +
                "    while (i < 10) {\n" +
                "        try {\n" +
                "            i = (i + 1)\n" +
                "            break\n" +
                "        } finally(e) {\n" +
                "            if (e == null) { core.io.Console.println(\"null\") }\n" +
                "            else { core.io.Console.println(\"exc\") }\n" +
                "        }\n" +
                "    }\n" +
                "    return i\n" +
                "}\n");
            CheckOk("Break 进 finally(e)", brk);
            TestHarness.Check("Break 路径 e 为 null", brk.Stdout, "null\n");
            CheckI32("Break 后 i", brk, 1);

            // Throw 路径回归：未捕获异常进入 finally 时 e 为异常对象
            var thr = Run(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new core.RuntimeException(\"x\")\n" +
                "        } finally(e) {\n" +
                "            if (e == null) { core.io.Console.println(\"null\") }\n" +
                "            else { core.io.Console.println(\"exc\") }\n" +
                "        }\n" +
                "    } catch (_: core.Exception) {\n" +
                "        return 1\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Throw 进 finally(e)", thr);
            TestHarness.Check("Throw 路径 e 为异常对象", thr.Stdout, "exc\n");
            CheckI32("外层 catch 捕获", thr, 1);
        }

        // region break 手工模块骨架：资源 R_0..R_9（i32 0-9）+ R_T/R_F
        //（bool true/false，下标 10/11）+ R_N（null，下标 12）；类型
        // MyErr class；main 变量 a/h/f/x（.i32）、c（.bool）、b0/b1/b2
        //（.breakid）、slot/obj（.any）；entry 为 entrypoint
        private static BilModule RegionModuleSkeleton(out BilFunction main, out BilBlock entry)
        {
            var module = new BilModule();
            for (var i = 0; i <= 9; i++)
            {
                module.Resources.Add(new BilScalarResource("R_" + i, BilScalarType.I32,
                    i.ToString()));
            }
            module.Resources.Add(new BilScalarResource("R_T", BilScalarType.Bool, "true"));
            module.Resources.Add(new BilScalarResource("R_F", BilScalarType.Bool, "false"));
            module.Resources.Add(new BilNullResource("R_N", ".any"));
            module.LocalSymbols.Add(new BilTypeDeclaration("MyErr", BilTypeKind.Class,
                new BilAccessibilityModifier(BilAccessibility.Public)));
            module.LocalSymbols.Add(new BilSimpleMemberDeclaration(BilMemberKind.Method,
                "$main()@.i32",
                new BilModifier[]
                {
                    new BilAccessibilityModifier(BilAccessibility.Public),
                    new BilKeywordModifier(BilKeyword.Entrypoint),
                }));
            main = new BilFunction("$main()@.i32");
            main.Args.Add(new BilArgDeclaration(".return", ".i32"));
            main.Vars.Add(new BilVarDeclaration(".i32", "a"));
            main.Vars.Add(new BilVarDeclaration(".i32", "h"));
            main.Vars.Add(new BilVarDeclaration(".i32", "f"));
            main.Vars.Add(new BilVarDeclaration(".i32", "x"));
            main.Vars.Add(new BilVarDeclaration(".bool", "c"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b0"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b1"));
            main.Vars.Add(new BilVarDeclaration(".breakid", "b2"));
            main.Vars.Add(new BilVarDeclaration(".any", "slot"));
            main.Vars.Add(new BilVarDeclaration(".any", "obj"));
            entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            main.Blocks.Add(entry);
            module.Functions.Add(main);
            return module;
        }

        private static LoadInstruction LoadI32(BilModule module, int n, string variable) =>
            new LoadInstruction(module.Resources[n], BilOp.Var(variable));

        private static BilCatchTableResource EmptyCatchTable(BilModule module)
        {
            var table = new BilCatchTableResource("R_CT", Array.Empty<BilCatchEntry>());
            module.Resources.Add(table);
            return table;
        }

        private static BilCatchTableResource MyErrCatchTable(BilModule module, BilBlock handler)
        {
            var table = new BilCatchTableResource("R_CTE",
                new[] { new BilCatchEntry(new BilTypeOperand("MyErr"), handler) });
            module.Resources.Add(table);
            return table;
        }

    }
}
