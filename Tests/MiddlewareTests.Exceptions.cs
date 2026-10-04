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
        // Exceptions 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestMirTryExpand()
        {
            // ----- try/catch：派发垫形状 + is 链表序 + throw 直译 -----
            var main = BuildMainMir(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "class OtherError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyError()\n" +
                "    } catch (m: MyError) { return 1 }\n" +
                "    catch (o: OtherError) { return 2 }\n" +
                "    return 0\n" +
                "}\n", "trycatch.bil");
            var dispatch = BlockEnding(main, ".dispatch");
            TestHarness.CheckTrue("try/catch：派发垫入口 MirTakePending",
                dispatch.Instructions[0] is MirTakePending take0
                && take0.TargetLocal.StartsWith("$mw.exc.", StringComparison.Ordinal));
            var excLocal = ((MirTakePending)dispatch.Instructions[0]).TargetLocal;
            TestHarness.CheckTrue("try/catch：$mw.exc.N 注册为 core::Exception 局部",
                main.FindLocal(excLocal).Type.Canonical == "core::Exception");
            TestHarness.CheckTrue("try/catch：派发垫首条 is 目标表项 0（MyError）",
                dispatch.Instructions[1] is MirTypeCheck check0
                && check0.Kind == MirTypeCheckKind.Is
                && check0.Value is MirLocalOperand { Name: var v0 } && v0 == excLocal
                && check0.TargetTypeRef == "MyError");
            TestHarness.CheckTrue("try/catch：派发垫终结为条件跳转（命中垫/下一链节）",
                dispatch.Terminator is MirCondBranch);
            var chain1 = BlockEnding(main, ".dispatch.1");
            TestHarness.CheckTrue("try/catch：链节 1 的 is 目标表项 1（OtherError，表序）",
                chain1.Instructions[0] is MirTypeCheck check1
                && check1.TargetTypeRef == "OtherError");
            var excVar = ExcVarOf(main);
            var catchPre0 = BlockEnding(main, ".catchpre.0");
            TestHarness.CheckTrue("try/catch：命中前置垫写 EXC_VAR=$mw.exc.N 后进 catch 块",
                catchPre0.Instructions[0] is MirCopyLocal pre0
                && pre0.Source is MirLocalOperand { Name: var s0 } && s0 == excLocal
                && pre0.Target == excVar
                && catchPre0.Terminator is MirBranch { Target: "try0-catch0" });
            var throwBlock = main.Blocks.First(
                b => b.Instructions.OfType<MirThrow>().Any());
            var bodyThrow = throwBlock.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("throw 直译：ExcTarget=本 try 派发垫（对象身份）",
                ReferenceEquals(bodyThrow.ExcTarget, dispatch));
            TestHarness.CheckTrue("throw 直译：块终结跳派发垫",
                throwBlock.Terminator is MirBranch br0 && br0.Target == dispatch.Id);
            // 无 finally：未命中直接 MirThrow 外层（无外层 = 传播出函数）
            var miss = BlockEnding(main, ".miss");
            TestHarness.CheckTrue("try/catch 无 finally：未命中块 MirThrow",
                miss.Instructions.OfType<MirThrow>().Count() == 1);
            var missThrow = miss.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("try/catch 无 finally：未命中 MirThrow 无外层（null）",
                missThrow.ExcTarget == null
                && missThrow.Exception is MirLocalOperand { Name: var m0 } && m0 == excLocal);
            TestHarness.CheckTrue("try/catch 无 finally：未命中块 MirRetThrow 收尾",
                miss.Terminator is MirRetThrow);

            // ----- try/finally：单块双前置垫 + 路由器 + 逃逸垫 -----
            main = BuildMainMir(
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        log()\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "tryfinally.bil");
            excVar = ExcVarOf(main);
            var finPreNormal = BlockEnding(main, ".fin.pre.normal");
            TestHarness.CheckTrue("try/finally：正常前置垫首指令写 EXC_VAR=null",
                finPreNormal.Instructions[0] is MirLoadResource nullLoad
                && nullLoad.Resource is BilNullResource
                && nullLoad.Target == excVar);
            TestHarness.CheckTrue("try/finally：正常前置垫写 comp=0 后进 finally 单块",
                finPreNormal.Instructions[1] is MirLoadResource compLoad0
                && compLoad0.Resource is BilScalarResource { LiteralText: "0" }
                && compLoad0.Target.StartsWith("$mw.comp.", StringComparison.Ordinal)
                && finPreNormal.Terminator is MirBranch { Target: "try0-finally" });
            var finPreExc = BlockEnding(main, ".fin.pre.exc");
            TestHarness.CheckTrue("try/finally：异常前置垫写 EXC_VAR=异常对象",
                finPreExc.Instructions[0] is MirCopyLocal excCopy
                && excCopy.Source is MirLocalOperand { Name: var s1 }
                && s1.StartsWith("$mw.exc.", StringComparison.Ordinal)
                && excCopy.Target == excVar
                && finPreExc.Terminator is MirBranch { Target: "try0-finally" });
            var esc = BlockEnding(main, ".esc");
            TestHarness.CheckTrue("try/finally：逃逸垫 take 后汇入异常前置垫",
                esc.Instructions.Count == 1
                && esc.Instructions[0] is MirTakePending
                && esc.Terminator is MirBranch brEsc && brEsc.Target == finPreExc.Id);
            var router = BlockEnding(main, ".route");
            TestHarness.CheckTrue("try/finally：路由器 MirSwitch（default=after-try）",
                router.Terminator is MirSwitch sw0
                && sw0.Selector is MirLocalOperand { Name: var sel0 }
                && sel0.StartsWith("$mw.comp.", StringComparison.Ordinal)
                && sw0.DefaultTarget.EndsWith(".end", StringComparison.Ordinal));
            var rethrow = BlockEnding(main, ".rethrow");
            TestHarness.CheckTrue("try/finally：路由器 throw 分支 = 重抛块（无外层→出厂）",
                ((MirSwitch)router.Terminator).ItemTargets.Contains(rethrow.Id)
                && rethrow.Instructions[0] is MirThrow { ExcTarget: null }
                && rethrow.Terminator is MirRetThrow);
            var dispatchOnly = BlockEnding(main, ".dispatch");
            TestHarness.CheckTrue("try/finally 无 catch：派发垫 take 后直落异常前置垫",
                dispatchOnly.Instructions.Count == 1
                && dispatchOnly.Instructions[0] is MirTakePending
                && dispatchOnly.Terminator is MirBranch brD && brD.Target == finPreExc.Id);
            var tryBody = main.Blocks.Single(b => b.Id == "try0-body");
            TestHarness.CheckTrue("try/finally：try 体内调用 ExcTarget=派发垫",
                tryBody.Instructions.OfType<MirCall>().Any()
                && tryBody.Instructions.OfType<MirCall>().All(
                    c => c.ExcTarget == dispatchOnly));
            var finBody = main.Blocks.Single(b => b.Id == "try0-finally");
            TestHarness.CheckTrue("try/finally：finally 体内调用 ExcTarget=外层（此处 null）",
                finBody.Instructions.OfType<MirCall>().Any()
                && finBody.Instructions.OfType<MirCall>().All(c => c.ExcTarget == null));

            // ----- return 穿 finally：路由器 ret 分支 -----
            main = BuildMainMir(
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "tryret.bil");
            TestHarness.CheckTrue("return 穿 finally：$mw.retv 合成局部（i32）",
                main.Locals.Any(l => l.Name == "$mw.retv" && l.Type.Key == "i32"));
            var retBody = main.Blocks.Single(b => b.Id == "try0-body");
            TestHarness.CheckTrue("return 穿 finally：值先存 $mw.retv 再跳 ret 前置垫",
                retBody.Instructions.Last() is MirCopyLocal retvStore
                && retvStore.Target == "$mw.retv"
                && retBody.Terminator is MirBranch brR
                && brR.Target.Contains(".fin.pre.ret"));
            var retPad = main.Blocks.Single(b => b.Id == ((MirBranch)retBody.Terminator).Target);
            TestHarness.CheckTrue("return 穿 finally：ret 前置垫写 null + comp",
                retPad.Instructions[0] is MirLoadResource { Resource: BilNullResource }
                && retPad.Instructions[1] is MirLoadResource
                    { Resource: BilScalarResource { LiteralText: not "0" } }
                && retPad.Terminator is MirBranch { Target: "try0-finally" });
            var retBlock = BlockEnding(main, ".ret");
            TestHarness.CheckTrue("return 穿 finally：ret 出口块 MirRet($mw.retv)",
                retBlock.Terminator is MirRet
                {
                    Value: MirLocalOperand { Name: "$mw.retv" }
                });
            router = BlockEnding(main, ".route");
            TestHarness.CheckTrue("return 穿 finally：路由器含 ret 分支",
                ((MirSwitch)router.Terminator).ItemTargets.Contains(retBlock.Id));

            // ----- break 穿 finally：路由器分支直落外层 loop 出口 -----
            main = BuildMainMir(
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var x = 0\n" +
                "    while (x < 3) {\n" +
                "        try {\n" +
                "            break\n" +
                "        } finally(e) {\n" +
                "            log()\n" +
                "        }\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n", "trybreak.bil");
            TestHarness.CheckTrue("break 穿 finally：brk 前置垫存在",
                main.Blocks.Any(b => b.Id.Contains(".fin.pre.brk")));
            router = BlockEnding(main, ".route");
            TestHarness.CheckTrue("break 穿 finally：路由器分支落外层 loop 出口",
                ((MirSwitch)router.Terminator).ItemTargets.Any(
                    t => t.StartsWith("mw.loop.end.", StringComparison.Ordinal)));

            // ----- finally 内 throw：直解析覆盖（不经路由器）-----
            main = BuildMainMir(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        log()\n" +
                "    } finally(e) {\n" +
                "        throw new MyError()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "tryfinthrow.bil");
            var finThrowBlock = main.Blocks.Single(b => b.Id == "try0-finally");
            var finThrow = finThrowBlock.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("finally 内 throw：直解析（ExcTarget=外层，此处 null）",
                finThrow.ExcTarget == null);
            TestHarness.CheckTrue("finally 内 throw：MirRetThrow 收尾（不经路由器）",
                finThrowBlock.Terminator is MirRetThrow);

            // ----- 嵌套 try：内层未命中进外层派发垫 -----
            main = BuildMainMir(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "class OtherError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        try {\n" +
                "            throw new MyError()\n" +
                "        } catch (o: OtherError) {\n" +
                "            log()\n" +
                "        }\n" +
                "    } catch (m: MyError) {\n" +
                "        log()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n", "trynested.bil");
            var dispatches = main.Blocks
                .Where(b => b.Id.EndsWith(".dispatch", StringComparison.Ordinal))
                .OrderBy(b => b.Id, StringComparer.Ordinal)
                .ToList();
            TestHarness.CheckTrue("嵌套 try：两层派发垫", dispatches.Count == 2);
            var outerDispatch = dispatches[0];
            // 内层未命中 → 外层派发垫；外层未命中 → 出厂（ExcTarget null）
            var missBlocks = main.Blocks
                .Where(b => b.Id.EndsWith(".miss", StringComparison.Ordinal)).ToList();
            TestHarness.CheckTrue("嵌套 try：两层各有未命中块", missBlocks.Count == 2);
            var innerMiss = missBlocks.Single(
                b => b.Instructions.OfType<MirThrow>().Single().ExcTarget != null);
            var outerMiss = missBlocks.Single(
                b => b.Instructions.OfType<MirThrow>().Single().ExcTarget == null);
            TestHarness.CheckTrue("嵌套 try：外层未命中 MirThrow 出厂（MirRetThrow）",
                outerMiss.Terminator is MirRetThrow);
            var innerMissThrow = innerMiss.Instructions.OfType<MirThrow>().Single();
            TestHarness.CheckTrue("嵌套 try：内层未命中 MirThrow→外层派发垫（对象身份）",
                ReferenceEquals(innerMissThrow.ExcTarget, outerDispatch)
                && innerMiss.Terminator is MirBranch brN && brN.Target == outerDispatch.Id);
            var innerCatch = main.Blocks.Single(b => b.Id == "try1-catch0");
            TestHarness.CheckTrue("嵌套 try：内层 catch 体内调用 ExcTarget=外层派发垫",
                innerCatch.Instructions.OfType<MirCall>().Any()
                && innerCatch.Instructions.OfType<MirCall>().All(
                    c => c.ExcTarget == outerDispatch));
            var outerCatch = main.Blocks.Single(b => b.Id == "try0-catch0");
            TestHarness.CheckTrue("嵌套 try：外层 catch 体内调用 ExcTarget=null（出厂）",
                outerCatch.Instructions.OfType<MirCall>().Any()
                && outerCatch.Instructions.OfType<MirCall>().All(c => c.ExcTarget == null));
        }

    }
}
