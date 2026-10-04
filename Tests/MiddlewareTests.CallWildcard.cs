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
        // CallWildcard 职责；与主文件共享同一类型、字段及生命周期。

        // ===== call??? 降级改写（MW10 刀4） =====

        // 全链形状：调用点 invoke core::Any$call??? → $mw.call???.dispatch
        //（候选 if 链按继承深度深→浅、hit 取槽调 entry 首环、miss 抛
        // NoSuchMethodException）；entry 环（TReturn 擦除 .any、.generic
        // 剔除）inner 原地改写——末环 → router(T, 层+1)（无可烘焙成员
        // 宿主亦预建零分支 router）；无残留 MirInnerCall/call??? 调用
        private static void TestCallWildcardLoweringEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Router {\n" +
                "    pub init()\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        if (symbol == \"Base$fetchUserById(x:.i32)@.any\") {\n" +
                "            return (99 as TReturn)\n" +
                "        }\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Router\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return (x + 1) }\n" +
                "}\n" +
                "@Router\n" +
                "pub class Child : Base { pub init() }\n" +
                "pub func main(): i32 {\n" +
                "    var c = new Child()\n" +
                "    return (c.fetchUserById(42) as i32)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.callwildcard.bil");
            TestHarness.CheckTrue("call??? 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("call??? 烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());
            TestHarness.CheckTrue("调用点无残留 core::Any$call??? 调用",
                !allInsts.OfType<MirCall>().Any(c =>
                    c.Target.Canonical.StartsWith("core::Any$call???",
                        System.StringComparison.Ordinal)));

            MirFunction Fn(string needle) => functions.Single(f =>
                f.Symbol.Canonical.Contains(needle));
            var instsOf = new Func<MirFunction, List<MirInst>>(f =>
                f.Blocks.SelectMany(b => b.Instructions).ToList());
            var calls = new Func<MirFunction, List<MirCall>>(f =>
                instsOf(f).OfType<MirCall>().ToList());

            // dispatch fn：两候选（Child 深于 Base）if 链 + miss 抛
            var dispatch = Fn("$mw.call???.dispatch");
            var main = functions.Single(f => f.Symbol.Canonical == "$main()@.i32");
            TestHarness.CheckTrue("main 调用点改写为 dispatch",
                calls(main).Any(c => c.Target.Canonical == dispatch.Symbol.Canonical));
            var checks = instsOf(dispatch).OfType<MirTypeCheck>()
                .Where(t => t.Kind == MirTypeCheckKind.Is).ToList();
            TestHarness.CheckTrue("dispatch 含两候选实际类型判定",
                checks.Count == 2);
            TestHarness.CheckTrue("dispatch 候选按深度深→浅（Child 先 Base 后）",
                checks[0].TargetTypeRef == "Child" && checks[1].TargetTypeRef == "Base");
            TestHarness.CheckTrue("dispatch hit 取槽调 entry 首环并返回",
                instsOf(dispatch).OfType<MirGetWrapperAddr>().Any()
                && calls(dispatch).Any(c =>
                    c.Target.Canonical.Contains("$.mw.call???.entry."))
                && dispatch.Blocks.Any(b => b.Terminator is MirRet));
            TestHarness.CheckTrue("dispatch miss 抛 NoSuchMethodException",
                instsOf(dispatch).OfType<MirNewObject>().Any(n =>
                    n.Type.Canonical == "core::NoSuchMethodException")
                && instsOf(dispatch).OfType<MirThrow>().Any()
                && dispatch.Blocks.Any(b => b.Terminator is MirRetThrow));

            // entry 环：Child/Base 各一环（单层 wildcard）；返回 .any、
            // 无 .generic 形参；inner → router(T, 1)
            var childEntry = Fn("Child$.mw.call???.entry.0");
            TestHarness.CheckTrue("entry 环返回擦除 .any", childEntry.ReturnType.IsAny);
            TestHarness.CheckTrue("entry 环形参剔除 .generic",
                !childEntry.Parameters.Any(p => p.Name.StartsWith(".generic.")));
            TestHarness.CheckTrue("entry 环含 .proxy.* 模板体（symbol 比对）",
                instsOf(childEntry).OfType<MirBinaryIntrinsic>().Any(b =>
                    b.Op == BilBinaryOp.CmpEq && b.LeftType.IsString));
            TestHarness.CheckTrue("Child entry 末环 inner → router(Child, 1)",
                calls(childEntry).Any(c =>
                    c.Target.Canonical.Contains("Child$.mw.router.1")));
            var baseEntry = Fn("Base$.mw.call???.entry.0");
            TestHarness.CheckTrue("Base entry 末环 inner → router(Base, 1)",
                calls(baseEntry).Any(c =>
                    c.Target.Canonical.Contains("Base$.mw.router.1")));
            TestHarness.CheckTrue("router 预建覆盖无可烘焙成员宿主（Child 零分支亦建）",
                functions.Any(f => f.Symbol.Canonical.Contains("Child$.mw.router.1")));

            using var llvmLease4317 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("LLVM 含 dispatch", ll.Contains(".mw.call???"), ll);
            TestHarness.CheckTrue("LLVM 含 entry 环", ll.Contains(".mw.call???.entry"), ll);
        }

    }
}
