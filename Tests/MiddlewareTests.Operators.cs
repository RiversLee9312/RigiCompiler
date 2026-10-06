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
        // Operators 职责；与主文件共享同一类型、字段及生命周期。

        // 同层 specific 压 wildcard + 运算符 specific/wildcard 烘焙形状
        //（运算符 fn 原名槽换 trampoline；native 调用点经 add 等内建指令
        // 分派属 ImplBinder 既有空白，此处只断言 MIR 烘焙形状）
        private static void TestOperatorAndSameLayerProxyBakingEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mix {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(x: i32): i32 { return inner(x) }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@Mix\n" +
                "pub class Service {\n" +
                "    pub init()\n" +
                "    pub func ping(x: i32): i32 { return x }\n" +
                "    pub func pong(x: i32): i32 { return x }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WO {\n" +
                "    pub init()\n" +
                "    operator .proxy.opr.plus(another: VecA): VecA { return inner(another) }\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper WW {\n" +
                "    pub init()\n" +
                "    operator .proxy.opr.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, " +
                "unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "@WO\n" +
                "pub class VecA {\n" +
                "    pub init()\n" +
                "    pub operator plus(another: VecA): VecA { return new VecA() }\n" +
                "}\n" +
                "@WW\n" +
                "pub class VecB {\n" +
                "    pub init()\n" +
                "    pub operator plus(another: VecB): VecB { return new VecB() }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var s = new Service()\n" +
                "    return ((s.ping(1) + s.pong(2)))\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.opr.bil");
            CaseAssertions.CheckTrue("运算符烘焙用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var allInsts = functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("运算符用例烘焙后无残留 MirInnerCall",
                !allInsts.OfType<MirInnerCall>().Any());

            // 同层择一：ping 走 specific 环（不打包），pong 走 wildcard 环
            var pingRing = functions.Single(f =>
                f.Symbol.Canonical.Contains("Mix$.bake.Service$ping"));
            CaseAssertions.CheckTrue("同层 specific 环直传（无打包）",
                !pingRing.Blocks.SelectMany(b => b.Instructions).OfType<MirNewArray>().Any()
                && pingRing.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("Service$.wrapped.ping")));
            CaseAssertions.CheckTrue("同层 wildcard 环兜底（pong）",
                functions.Any(f => f.Symbol.Canonical.Contains("Mix$.bake.Service$pong")));

            // 运算符 specific：VecA$$plus 原名槽换 trampoline（直传形态）
            var plusATrampoline = functions.Single(f =>
                f.Symbol.Canonical == "VecA$$plus(another:VecA)@VecA");
            CaseAssertions.CheckTrue("运算符 specific trampoline（get.wrapper.addr + 调环）",
                plusATrampoline.Blocks.SelectMany(b => b.Instructions)
                    .OfType<MirGetWrapperAddr>()
                    .Any(g => g.WrapperType == "WO")
                && plusATrampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("WO$.bake.VecA$$plus")));
            CaseAssertions.CheckTrue("运算符 specific $.wrapped. 原始体",
                functions.Any(f =>
                    f.Symbol.Canonical.Contains("VecA$.wrapped.$plus")));
            // 运算符 wildcard：VecB$$plus trampoline 打包 + 环动态分派 +
            // router 覆盖 VecB$$plus 分支
            var plusBTrampoline = functions.Single(f =>
                f.Symbol.Canonical == "VecB$$plus(another:VecB)@VecB");
            CaseAssertions.CheckTrue("运算符 wildcard trampoline 打包",
                plusBTrampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirNewArray>()
                    .Count() == 2
                && plusBTrampoline.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("WW$.bake.VecB$$plus")));
            var oprRouter = functions.Single(f =>
                f.Symbol.Canonical.Contains("VecB$.mw.router.1"));
            CaseAssertions.CheckTrue("运算符 router 覆盖 $$plus 分支并直调终态",
                oprRouter.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.Target.Canonical.Contains("VecB$.wrapped.$plus")));

            using var llvmLease3977 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("LLVM 含运算符烘焙环", ll.Contains(".bake.VecB$$plus"), ll);
        }

        // ===== 遗1：用户运算符 native 分派（VM FindOperator 口径） =====

        // intrinsic 直译形状：用户类型操作数的 add/cmp/opposite → MirCall
        //（OperatorDispatch，目标 operator fn）；!= = equals + not；
        // </<= = compareTo + ComparisonResult case 判别（VM OrderCompare
        // 同口径）；内建标量运算保持 MirBinaryIntrinsic 原形状
        private static void TestUserOperatorDispatchEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus(another: Vec): Vec { return new Vec((x + another.x)) }\n" +
                "    pub operator equals(another: Vec): bool { return (x == another.x) }\n" +
                "    pub operator compareTo(another: Vec): ComparisonResult {\n" +
                "        if ((x < another.x)) { return .LesserThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "    pub operator opposite(): Vec { return new Vec((0 - x)) }\n" +
                "}\n" +
                "pub struct Meter {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator plus(another: Meter): Meter { return new Meter((v + another.v)) }\n" +
                "}\n" +
                "pub interface Equatable { pub operator equals(other: Equatable): bool }\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    var d = (a == b)\n" +
                "    var e = (a != b)\n" +
                "    var f = (a < b)\n" +
                "    var g = (a <= b)\n" +
                "    var h = -a\n" +
                "    var m1 = new Meter(1)\n" +
                "    var m2 = new Meter(2)\n" +
                "    var m3 = m1 + m2\n" +
                "    return c.x\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "user.opr.bil");
            CaseAssertions.CheckTrue("用户运算符用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var functions = context.Mir!.Functions;
            var main = functions.Single(f => f.Symbol.Canonical == "$main()@.i32");
            var insts = main.Blocks.SelectMany(b => b.Instructions).ToList();
            var dispatchCalls = insts.OfType<MirCall>().Where(c => c.OperatorDispatch).ToList();

            CaseAssertions.Check("运算符直译调用数（Vec plus/equals×2/compareTo×2/opposite + Meter plus）",
                dispatchCalls.Count.ToString(), "7");
            CaseAssertions.CheckTrue("add → Vec$$plus（OperatorDispatch）",
                dispatchCalls.Any(c => c.Target.Canonical == "Vec$$plus(another:Vec)@Vec"));
            CaseAssertions.CheckTrue("==/!= → Vec$$equals 两次（== 直存、!= 取反）",
                dispatchCalls.Count(c =>
                    c.Target.Canonical == "Vec$$equals(another:Vec)@.bool") == 2);
            CaseAssertions.CheckTrue("</<= → Vec$$compareTo 两次",
                dispatchCalls.Count(c =>
                    c.Target.Canonical == "Vec$$compareTo(another:Vec)@core::ComparisonResult") == 2);
            CaseAssertions.CheckTrue("一元 - → Vec$$opposite",
                dispatchCalls.Any(c => c.Target.Canonical == "Vec$$opposite()@Vec"));

            // != 的 MIR 形状：equals 调用结果槽经 not 取反
            CaseAssertions.CheckTrue("!= 形状：equals 后跟 not",
                insts.OfType<MirUnaryIntrinsic>().Any(u => u.Op == BilUnaryOp.Not));
            // < 的 MIR 形状：compareTo + is.case(.LesserThanAnother)；
            // <= 多一个 is.case(.Equal) + or 组合
            var cases = insts.OfType<MirIsCase>().Select(
                c => c.Case.Declaration.QualifiedName).ToList();
            CaseAssertions.CheckTrue("< 形状：is.case LesserThanAnother 命中",
                cases.Contains("core::ComparisonResult.LesserThanAnother"));
            CaseAssertions.CheckTrue("<= 形状：is.case Equal + or 组合",
                cases.Contains("core::ComparisonResult.Equal")
                && insts.OfType<MirBinaryIntrinsic>().Any(b => b.Op == BilBinaryOp.Or));

            // 内建标量运算保持原形状（Vec$$plus 体内的 i32 add 不走用户派发）
            var plusFn = functions.Single(f =>
                f.Symbol.Canonical == "Vec$$plus(another:Vec)@Vec");
            CaseAssertions.CheckTrue("内建 i32 add 保持 MirBinaryIntrinsic",
                plusFn.Blocks.SelectMany(b => b.Instructions).OfType<MirBinaryIntrinsic>()
                    .Any(b => b.Op == BilBinaryOp.Add)
                && !plusFn.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                    .Any(c => c.OperatorDispatch));

            // struct 的 add：Meter$$plus 直译（OperatorDispatch；直调分流
            // 归 Binding）
            CaseAssertions.CheckTrue("struct add → Meter$$plus（OperatorDispatch）",
                insts.OfType<MirCall>().Any(c => c.OperatorDispatch
                    && c.Target.Canonical == "Meter$$plus(another:Meter)@Meter"));

            // 绑定分流：class 运算符 → 虚派发（运行期按实际类型落最派生
            // 实现，VM 口径）；struct → 直调；interface → iMap 派发
            var vecPlus = context.Symbols.FindMember("Vec$$plus(another:Vec)@Vec")!;
            CaseAssertions.CheckTrue("class 运算符绑定 → VirtualCallBinding",
                ImplBinder.BindOperatorCall(vecPlus) is VirtualCallBinding);
            var meterPlus = context.Symbols.FindMember("Meter$$plus(another:Meter)@Meter")!;
            CaseAssertions.CheckTrue("struct 运算符绑定 → DirectCallBinding",
                ImplBinder.BindOperatorCall(meterPlus) is DirectCallBinding);
            var ifaceEquals = context.Symbols.FindMember(
                "Equatable$$equals(other:Equatable)@.bool")!;
            CaseAssertions.CheckTrue("interface 运算符绑定 → InterfaceCallBinding",
                ImplBinder.BindOperatorCall(ifaceEquals) is InterfaceCallBinding);
            // 显式 invoke 运算符保持静态直调（VM ResolveDispatchSymbol
            // 对 operator 原样返回调用点符号的同口径）
            CaseAssertions.CheckTrue("显式 invoke 运算符保持 DirectCallBinding",
                ImplBinder.BindCall(vecPlus) is DirectCallBinding);
        }

        // ===== wrapper 应用索引继承闭包（MW10 刀4） =====

        // 闭包语义：本类声明序 outer→inner 在前；祖先未重申应用按基→本
        // 追加在后；同定义重申覆盖只装一次（VM CollectEntityWrappers 靠
        // §14.9 重申约束等价的同一口径）
        private static void TestWrapperIndexInheritanceClosure()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged { pub init() }\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Extra { pub init() }\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Third { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub open class Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "@Third\n" +
                "pub open class Mid : Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "@Third\n" +
                "pub class Leaf : Mid { pub init() }\n" +
                "pub func main(): i32 { return 0 }\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.index.closure.bil");
            CaseAssertions.CheckTrue("闭包索引用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var index = WrapperApplicationIndex.Build(context.Symbols);

            // 重申去重：Mid/Leaf 重申 Logged/Extra 后各只装一次，声明序保持
            CaseAssertions.CheckTrue("Base 闭包=本类声明",
                index.EntityWrappers("Base").SequenceEqual(new[] { "Logged", "Extra" }));
            CaseAssertions.CheckTrue("Mid 闭包=重申+追加、无重复",
                index.EntityWrappers("Mid").SequenceEqual(
                    new[] { "Logged", "Extra", "Third" }));
            CaseAssertions.CheckTrue("Leaf 闭包沿链去重不翻倍",
                index.EntityWrappers("Leaf").SequenceEqual(
                    new[] { "Logged", "Extra", "Third" }));

            // 手写 BIL（绕过 §14.9 重申；§21.3 门禁会拦安装侧不一致，
            // 故直读文本不入门禁）：子类声明剔除 wrapped 后，闭包仍并入
            // 基类应用（VM CollectEntityWrappers 只读本类属重申等价偷懒，
            // 此处按 §9.7 安装侧闭包口径购齐）
            var hand = text.Replace(
                "        pub open wrapped(Logged) wrapped(Extra) wrapped(Third) {",
                "        pub open {");
            CaseAssertions.CheckTrue("探测：BIL 文本确含可剔除的重申段", hand != text);
            var handIndex = WrapperApplicationIndex.Build(
                new MwContext(BilReader.Read(hand)).Symbols);
            CaseAssertions.CheckTrue("未重申子类闭包并入基类应用（基→本追加）",
                handIndex.EntityWrappers("Mid").SequenceEqual(
                    new[] { "Logged", "Extra" }));
        }

        // ===== Entity 隐藏槽跨层级去重（MW10 遗3） =====

        // VM HiddenEntityKey 仅含 wrapper TypeRef（不含声明类），子类重申
        // 同 ref 覆盖同一隐藏键——native 物理槽同口径：重申不另开槽，
        // 槽恒归首次声明（最基类）偏移，随 basePlan.Fields 原名逐层拷入。
        // 布局形状断言：Base 恰一个 Entity 槽；Mid/Leaf 无本类名前缀槽、
        // 各恰含一枚原名拷入的基类槽且偏移与 Base 一致
        private static void TestHiddenSlotEntityDedupAcrossHierarchy()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var hits: i32\n" +
                "    pub init() { hits = 0 }\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Extra { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub open class Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub open class Mid : Base { pub init() }\n" +
                "@Logged\n" +
                "@Extra\n" +
                "pub class Leaf : Mid { pub init() }\n" +
                "pub func main(): i32 { return 0 }\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "wrapper.slot.dedup.bil");
            CaseAssertions.CheckTrue("槽去重用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            CaseAssertions.CheckTrue("管线挂载布局", context.Layout != null);

            var basePlan = context.Layout!.Find("Base");
            var midPlan = context.Layout.Find("Mid");
            var leafPlan = context.Layout.Find("Leaf");
            CaseAssertions.CheckTrue("三级布局计划齐全",
                basePlan != null && midPlan != null && leafPlan != null);
            if (basePlan == null || midPlan == null || leafPlan == null)
            {
                return;
            }
            const string baseLogged = "Base#.wrapper.Logged@Logged";
            const string baseExtra = "Base#.wrapper.Extra@Extra";
            CaseAssertions.CheckTrue("Base 含两枚本类槽",
                basePlan.Fields.Count(f => f.Symbol == baseLogged) == 1
                    && basePlan.Fields.Count(f => f.Symbol == baseExtra) == 1);
            CaseAssertions.CheckTrue("Mid 重申不另开槽（无 Mid# 前缀 wrapper 槽）",
                !midPlan.Fields.Any(f => f.Symbol.StartsWith("Mid#.wrapper.",
                    System.StringComparison.Ordinal)));
            CaseAssertions.CheckTrue("Leaf 重申不另开槽（无 Leaf# 前缀 wrapper 槽）",
                !leafPlan.Fields.Any(f => f.Symbol.StartsWith("Leaf#.wrapper.",
                    System.StringComparison.Ordinal)));
            CaseAssertions.CheckTrue("Mid 恰含原名拷入的基类槽各一枚",
                midPlan.Fields.Count(f => f.Symbol == baseLogged) == 1
                    && midPlan.Fields.Count(f => f.Symbol == baseExtra) == 1);
            CaseAssertions.CheckTrue("Leaf 恰含原名拷入的基类槽各一枚",
                leafPlan.Fields.Count(f => f.Symbol == baseLogged) == 1
                    && leafPlan.Fields.Count(f => f.Symbol == baseExtra) == 1);
            var baseOffset = basePlan.Fields.First(f => f.Symbol == baseLogged).Offset;
            CaseAssertions.CheckTrue("基类槽偏移跨层级一致",
                midPlan.Fields.First(f => f.Symbol == baseLogged).Offset == baseOffset
                    && leafPlan.Fields.First(f => f.Symbol == baseLogged).Offset
                        == baseOffset);
        }

    }
}
