using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // ExitRouting 职责；与主文件共享同一类型、字段及生命周期。

        // ===== Stage B：return@ route 展开（StructuredExitRouting）端到端 =====
        private static void TestStructuredExitRouting()
        {
            // (a) if 表达式值块早退：结果正确且后续语句不执行
            var early = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    var r = if ((x == 0)) {\n" +
                "        x = (x + 1)\n" +
                "        if ((x == 1)) { return@_ 10 }\n" +
                "        x = (x + 100)\n" +
                "        return@_ x\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return (r + x)\n" +
                "}\n");
            CheckOk("值块早退", early);
            CheckI32("早退结果 10 且 x+100 跳过", early, 11);

            // (b) 嵌套 if/switch 多个 return@：指向外层 if target
            // （if region 穿 switch region 两级 relay）与同 target 多出口
            var nested = Run(
                "pub func main(): i32 {\n" +
                "    var r = if ((1 == 1)) named outer {\n" +
                "        var s = switch (2) {\n" +
                "            (2) -> {\n" +
                "                if ((2 == 2)) { return@outer 20 }\n" +
                "                return@_ 21\n" +
                "            }\n" +
                "            default -> { return@_ 99 }\n" +
                "        }\n" +
                "        return@outer s\n" +
                "    } else {\n" +
                "        return@outer 0\n" +
                "    }\n" +
                "    var t = switch (1) {\n" +
                "        (1) -> {\n" +
                "            if ((1 == 1)) { return@_ 7 }\n" +
                "            return@_ 8\n" +
                "        }\n" +
                "        default -> { return@_ 9 }\n" +
                "    }\n" +
                "    return (r + t)\n" +
                "}\n");
            CheckOk("嵌套 return@ 多 target", nested);
            CheckI32("if←switch relay 20 + switch←if relay 7", nested, 27);

            // (c) 用户 §5 例：return@middle / return@outer 两路径的
            // cleanup 执行与 afterTry/afterMiddle 跳过
            var pathA = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                if (true) { return@middle }\n" +
                "                if (false) { return@outer }\n" +
                "                core.io.Console.println(\"work\")\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("return@middle 路径", pathA);
            CaseAssertions.Check("cleanup 执行、afterTry 跳过、afterMiddle 执行",
                pathA.Stdout, "cleanup\nafterMiddle\n");
            CheckI32("return@middle 返回", pathA, 1);
            var pathB = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                if (false) { return@middle }\n" +
                "                if (true) { return@outer }\n" +
                "                core.io.Console.println(\"work\")\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    return 2\n" +
                "}\n");
            CheckOk("return@outer 路径", pathB);
            CaseAssertions.Check("cleanup 执行、afterTry/afterMiddle 跳过",
                pathB.Stdout, "cleanup\n");
            CheckI32("return@outer 返回", pathB, 2);
            var pathC = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                if (false) { return@middle }\n" +
                "                if (false) { return@outer }\n" +
                "                core.io.Console.println(\"work\")\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"cleanup\")\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    return 3\n" +
                "}\n");
            CheckOk("无 exit 路径", pathC);
            CaseAssertions.Check("work/cleanup/afterTry/afterMiddle 全执行",
                pathC.Stdout, "work\ncleanup\nafterTry\nafterMiddle\n");
            CheckI32("无 exit 返回", pathC, 3);

            // (d) return@ 穿 try/catch/finally：finally 执行、catch 不触发
            var throughTry = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            return@_ 5\n" +
                "        } catch (_: core.Exception) {\n" +
                "            core.io.Console.println(\"catch\")\n" +
                "        } finally(_) {\n" +
                "            core.io.Console.println(\"fin\")\n" +
                "        }\n" +
                "        return@_ 6\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("return@ 穿 try/catch/finally", throughTry);
            CaseAssertions.Check("finally 执行且 catch 不触发", throughTry.Stdout, "fin\n");
            CheckI32("return@ 产值 5", throughTry, 5);

            // (e) weaving 旧 bug 回归：try 后 continuation 抛 catch 类型
            // 异常不再被前 catch 捕获（continuation 不再织入 try region）
            var notCaught = Run(
                "pub func boom(): i32 {\n" +
                "    throw new core.RuntimeException(\"x\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            core.io.Console.println(\"t\")\n" +
                "        } catch (_: core.RuntimeException) {\n" +
                "            return@_ 2\n" +
                "        }\n" +
                "        return@_ boom()\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckTrue("try 后 continuation 异常不被前 catch 捕获",
                notCaught.Exception?.ExceptionObject is VmObject escaped
                && escaped.TypeRef.Contains("RuntimeException"),
                notCaught.Exception?.ToString() ?? "<null>");

            // (f) finally 中新 return@ 覆盖原 exit（route 覆写，
            // dispatcher 看到新目标）
            var overrideExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } finally(_) {\n" +
                "            return@_ 2\n" +
                "        }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("finally return@ 覆盖", overrideExit);
            CheckI32("finally 产值 2 覆盖 1", overrideExit, 2);
            // finally throw 覆盖并跳过 dispatcher
            var overrideThrow = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } finally(_) {\n" +
                "            throw new core.RuntimeException(\"f\")\n" +
                "        }\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckTrue("finally throw 覆盖并跳过 dispatcher",
                overrideThrow.Exception?.ExceptionObject is VmObject thrown
                && thrown.TypeRef.Contains("RuntimeException"),
                overrideThrow.Exception?.ToString() ?? "<null>");

            // (f2) finally 内尾位 return@ 必须以 abrupt completion（break）
            // 覆盖 SavedCompletion（同 region 尾位 break 省略的 finally
            // 例外回归）：body return@outer、finally return@middle——
            // 若 finally 的 break 被省略（落尾 Normal），VM 恢复 body 的
            // return@outer，afterMiddle/afterOuter 均被跳过；正确行为
            // 命中 middle——afterMiddle/afterOuter 都执行
            var finallyOverrideNamed = Run(
                "pub func main(): i32 {\n" +
                "    seq named outer {\n" +
                "        seq named middle {\n" +
                "            try {\n" +
                "                return@outer\n" +
                "            } finally(_) {\n" +
                "                return@middle\n" +
                "            }\n" +
                "            core.io.Console.println(\"afterTry\")\n" +
                "        }\n" +
                "        core.io.Console.println(\"afterMiddle\")\n" +
                "    }\n" +
                "    core.io.Console.println(\"afterOuter\")\n" +
                "    return 1\n" +
                "}\n");
            CheckOk("finally return@middle 覆盖 body return@outer", finallyOverrideNamed);
            CaseAssertions.Check("命中 middle（afterTry 跳过、afterMiddle/afterOuter 执行）",
                finallyOverrideNamed.Stdout, "afterMiddle\nafterOuter\n");
            CheckI32("middle 路径返回", finallyOverrideNamed, 1);

            // (g) 语句 seq return@name（无 result）执行
            var stmtSeq = Run(
                "pub func main(): i32 {\n" +
                "    var x: i32 = 0\n" +
                "    seq named s {\n" +
                "        x = 1\n" +
                "        if ((x == 1)) { return@s }\n" +
                "        x = 99\n" +
                "    }\n" +
                "    x = (x + 1)\n" +
                "    return x\n" +
                "}\n");
            CheckOk("语句 seq return@name", stmtSeq);
            CheckI32("x=99 跳过、x+1 执行", stmtSeq, 2);

            // (h) seq using 单/双层：return@ 穿越时逆序 dispose 后再 relay
            var usingRelay = Run(
                "class Res implements core.IDisposable {\n" +
                "    pub const tag: String\n" +
                "    pub init(t: String) { tag = t }\n" +
                "    pub override func dispose() { core.io.Console.println(tag) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        seq using(const a = new Res(\"da\")) using(const b = new Res(\"db\")) {\n" +
                "            return@_ 7\n" +
                "        }\n" +
                "        return@_ 8\n" +
                "    }\n" +
                "    var s = seq {\n" +
                "        seq using(const c = new Res(\"dc\")) {\n" +
                "            if ((1 == 1)) { return@_ 3 }\n" +
                "            return@_ 4\n" +
                "        }\n" +
                "        return@_ 5\n" +
                "    }\n" +
                "    return (r + s)\n" +
                "}\n");
            CheckOk("using 穿越 relay", usingRelay);
            CaseAssertions.Check("逆序 dispose（db,da 后 dc）", usingRelay.Stdout, "db\nda\ndc\n");
            CheckI32("using 穿越产值 7 + 3", usingRelay, 10);

            // (i) 循环体 return@ 外层值块（while / do-while / for）
            var whileExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 3\n" +
                "        while ((x > 1)) {\n" +
                "            return@_ 42\n" +
                "        }\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("while 体 return@ 外层值块", whileExit);
            CheckI32("while 体产值 42", whileExit, 42);
            var doWhileExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        do { return@_ 8 } while (false)\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("do-while 体 return@ 外层值块", doWhileExit);
            CheckI32("do-while 体产值 8", doWhileExit, 8);
            var forExit = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        for (i in 0 to 5) {\n" +
                "            if ((i == 2)) { return@_ 15 }\n" +
                "        }\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("for 体 return@ 外层值块", forExit);
            CheckI32("for 体产值 15", forExit, 15);

            // (j) 嵌套循环多级 relay
            var nestedLoop = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq named outer {\n" +
                "        while (true) {\n" +
                "            while (true) {\n" +
                "                return@outer 21\n" +
                "            }\n" +
                "            return@outer 0\n" +
                "        }\n" +
                "        return@outer 1\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("嵌套循环多级 relay", nestedLoop);
            CheckI32("内层 return@ 穿两层 loop 产值 21", nestedLoop, 21);

            // (j2) 循环体内 return@ + 嵌套 relay：混合形态（内层产值 /
            // 外层逃逸）经两层 loop dispatcher；hint 不得影响 VM 结果
            var mixedNested = Run(
                "pub func pick(flag: bool): i32 {\n" +
                "    var r: i32 = seq named outer {\n" +
                "        var t: i32 = seq {\n" +
                "            while (true) {\n" +
                "                while (flag) {\n" +
                "                    return@_ 21\n" +
                "                }\n" +
                "                return@outer 0\n" +
                "            }\n" +
                "            return@_ 1\n" +
                "        }\n" +
                "        return@outer t\n" +
                "    }\n" +
                "    return r\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    return (pick(true) + pick(false))\n" +
                "}\n");
            CheckOk("混合形态嵌套 loop relay", mixedNested);
            CheckI32("true→21 + false→0", mixedNested, 21);

            // (k) 循环内 break/continue 与 return@ 共存：break/continue
            // 不写 route，dispatcher fall-through 落到循环后 return@
            var mixBreak = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 5\n" +
                "        var acc: i32 = 0\n" +
                "        while ((x > 0)) {\n" +
                "            if ((x == 5)) { x = (x - 1)\ncontinue }\n" +
                "            if ((x == 1)) { break }\n" +
                "            if ((x == 99)) { return@_ 99 }\n" +
                "            acc = (acc + x)\n" +
                "            x = (x - 1)\n" +
                "        }\n" +
                "        return@_ acc\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("break/continue 与 return@ 共存（break 路径）", mixBreak);
            CheckI32("continue 跳过 5、break 于 1，acc=4+3+2", mixBreak, 9);
            var mixReturn = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 5\n" +
                "        while ((x > 0)) {\n" +
                "            if ((x == 3)) { return@_ 77 }\n" +
                "            if ((x == 1)) { break }\n" +
                "            x = (x - 1)\n" +
                "        }\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("break/continue 与 return@ 共存（return@ 路径）", mixReturn);
            CheckI32("x==3 时 return@ 77", mixReturn, 77);

            // (l) 循环内 return@ 穿 try/finally：finally 执行且可覆盖
            var loopThroughTry = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        var x: i32 = 1\n" +
                "        while ((x > 0)) {\n" +
                "            try {\n" +
                "                return@_ 5\n" +
                "            } finally(_) {\n" +
                "                core.io.Console.println(\"fin\")\n" +
                "            }\n" +
                "        }\n" +
                "        return@_ 6\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("循环内 return@ 穿 try/finally", loopThroughTry);
            CaseAssertions.Check("finally 执行", loopThroughTry.Stdout, "fin\n");
            CheckI32("穿 try 产值 5", loopThroughTry, 5);
            var loopFinallyOverride = Run(
                "pub func main(): i32 {\n" +
                "    var r = seq {\n" +
                "        while (true) {\n" +
                "            try {\n" +
                "                return@_ 1\n" +
                "            } finally(_) {\n" +
                "                return@_ 2\n" +
                "            }\n" +
                "        }\n" +
                "        return@_ 3\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckOk("循环内 finally return@ 覆盖", loopFinallyOverride);
            CheckI32("finally 产值 2 覆盖 1", loopFinallyOverride, 2);
        }

    }
}
