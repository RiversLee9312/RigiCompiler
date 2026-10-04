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
        // Emission 职责；与主文件共享同一类型、字段及生命周期。

        // ===== .ll 黄金锚点 =====
        // 直调 MirBuilder.Build 后发射：与访问器/数组降级无关

        private static void TestLlGoldenAnchors()
        {
            var gate = BilGate.Accept(HelloConcatBil, "golden.bil");
            TestHarness.CheckTrue("黄金用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);
            using var llvmLease2282 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, mir);
            TestHarness.Check("生成模块与运行时的宿主目标一致", module.Target,
                LlvmHost.HostTriple);
            TestHarness.Check("生成模块与 TargetMachine 的布局一致", module.DataLayout,
                LlvmHost.HostDataLayout);
            var ll = module.PrintToString();

            // 黄金快照的 MW1 形态：锚定关键行（全文黄金比对随 .ll 快照基建落地）
            TestHarness.CheckTrue("模块名来自 Metadata", ll.Contains("; ModuleID = 'hello'"), ll);
            TestHarness.CheckTrue("字符串字面量进内部全局",
                ll.Contains("@str.R_Hello = internal constant { i32, i32, [7 x i8] } { i32 -1, i32 0, [7 x i8] c\"Hello, \""), ll);
            TestHarness.CheckTrue("字面量 data 指针 = 块+8",
                ll.Contains("getelementptr inbounds (i8, ptr @str.R_Hello, i64 8)"), ll);
            TestHarness.CheckTrue("String 槽零初始化",
                ll.Contains("store { ptr, i64 } zeroinitializer"), ll);
            TestHarness.CheckTrue("rigi_globals_cleanup 已发射",
                ll.Contains("define void @rigi_globals_cleanup()"), ll);
            TestHarness.CheckTrue("转义换行进字节常量",
                ll.Contains("c\"world!\\0A\""), ll);
            TestHarness.CheckTrue("入口发射为 rigi_entry",
                ll.Contains("define i32 @rigi_entry(i32 %0, ptr %1)"), ll);
            TestHarness.CheckTrue("string + → rigi_string_concat 调用",
                ll.Contains("call void @rigi_string_concat(ptr"), ll);
            TestHarness.CheckTrue("native print → rigi_print 声明",
                ll.Contains("declare void @rigi_print(ptr)"), ll);
            TestHarness.CheckTrue("返回装载 i32 0",
                ll.Contains("ret i32"), ll);
        }

        // ===== null 资源发射（MW2）=====

        private static void TestNullResourceEmission()
        {
            // 真实前端路径：var s: String? = null + == null 检查
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s: String? = null\n" +
                "    if (s == null) { Console.println(\"null\") }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "null.bil");
            TestHarness.CheckTrue("null 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            // MW9a 起发射要求 ExcTarget 已解析（RcInjection 传播垫）：
            // 含 Rigi 直接调用（Console.println 包装 fn）的用例走完整管线
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease2331 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            // null = 胖引用双段零（RUNTIME §3：Nullable 是 Object 子类）
            TestHarness.CheckTrue("null 资源 → 胖引用零常量",
                ll.Contains("{ i64, i64 } zeroinitializer"), ll);
            // nullable == → 胖引用双段 extractvalue 各自 icmp 取与
            TestHarness.CheckTrue("nullable == → 双段恒等比较",
                ll.Contains("extractvalue { i64, i64 }"), ll);
        }

        // ===== 除零 guard 发射（MW9b-G：抛 DividedByZeroException）=====

        private static void TestDivGuardEmission()
        {
            // 真实前端全管线路径：i32 除法 → divisor==0 条件分支 →
            // rigi_alloc + init() + rigi_exc_raise + br 传播垫；有符号窄
            // 宽度 MIN/-1 回绕的取负选择
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 0\n" +
                "    return (x / z)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "div.bil");
            TestHarness.CheckTrue("除零用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease2361 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            TestHarness.CheckTrue("除零 abort 面已退场",
                !ll.Contains("rigi_abort_divided_by_zero"), ll);
            TestHarness.CheckTrue("guard 构造 DividedByZeroException",
                ll.Contains("call ptr @rigi_alloc(")
                && ll.Contains("DividedByZeroException"), ll);
            TestHarness.CheckTrue("guard 调零参 init",
                ll.Contains("DividedByZeroException$init()"), ll);
            TestHarness.CheckTrue("guard 抛异常走 rigi_exc_raise",
                ll.Contains("call void @rigi_exc_raise(ptr"), ll);
            TestHarness.CheckTrue("有符号 MIN/-1 回绕取负选择",
                ll.Contains("sdiv.wrap"), ll);
            TestHarness.CheckTrue("异常边指向传播垫",
                ll.Contains("mw.propagate"), ll);
        }

        // ===== 模零 guard 与 srem/frem 发射（mod-3：取模 %）=====

        private static void TestModGuardEmission()
        {
            // 真实前端全管线路径：i32 取模 → divisor==0 条件分支 →
            // rigi_alloc + init() + rigi_exc_raise + br 传播垫（与 div
            // 同一异常面）；MIN/-1 走 smod.select 消毒（无 abort 臂）
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var z = 5\n" +
                "    return (x % z)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "mod.bil");
            TestHarness.CheckTrue("取模用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease2398 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            TestHarness.CheckTrue("模零 guard 构造 DividedByZeroException",
                ll.Contains("call ptr @rigi_alloc(")
                && ll.Contains("DividedByZeroException"), ll);
            TestHarness.CheckTrue("模零 guard 调零参 init",
                ll.Contains("DividedByZeroException$init()"), ll);
            TestHarness.CheckTrue("模零 guard 抛异常走 rigi_exc_raise",
                ll.Contains("call void @rigi_exc_raise(ptr"), ll);
            TestHarness.CheckTrue("有符号取模发射 srem",
                ll.Contains("srem i32"), ll);
            TestHarness.CheckTrue("MIN/-1 消毒 select（x % ±1 == 0）",
                ll.Contains("smod.safe") && ll.Contains("smod.wrap"), ll);
            TestHarness.CheckTrue("取模不引入 MIN/-1 abort 臂",
                !ll.Contains("rigi_abort_arithmetic_overflow"), ll);
            TestHarness.CheckTrue("取模异常边指向传播垫",
                ll.Contains("mw.propagate"), ll);

            // f64 取模：frem 直发（IEEE 754 截断余数），无 guard 无 raise
            (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 7.5\n" +
                "    var z = 2.25\n" +
                "    if ((x % z) == 0.75) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var floatGate = BilGate.Accept(text, "fmod.bil");
            TestHarness.CheckTrue("浮点取模用例门禁放行", floatGate.IsAccepted,
                string.Join("; ", floatGate.Errors));
            var floatContext = new MwContext(floatGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(floatContext);
            using var llvmLease2431 = LlvmHost.Enter();
            using var floatModule = ModuleBuilder.Build(floatContext, floatContext.Mir!);
            var floatLl = floatModule.PrintToString();

            TestHarness.CheckTrue("浮点取模发射 frem",
                floatLl.Contains("frem double"), floatLl);
            // stdlib 夹具自身携带异常面（rigi_exc_raise 全模块可见），故以
            // EmitModGuard 特有的 mod.zero 比较命名做负断言（f64 取模无
            // guard；模零得 NaN 由 E2E「浮点取模与模零 NaN」运行期对拍）
            TestHarness.CheckTrue("浮点取模无模零 guard（模零得 NaN）",
                !floatLl.Contains("mod.zero"), floatLl);
        }

        // ===== 布局引擎与 TypeSheet 发射（MW4 批 1）=====

        // 布局用例源（真实前端路径；类/继承/接口/enum/struct/引用字段/
        // rich 内嵌全覆盖；main 不实例化，避开尚未支持的 new 指令）
        private const string LayoutSource =
            "pub interface Named {\n" +
            "    func name(): String\n" +
            "}\n" +
            "pub open class Animal {\n" +
            "    pub var legs: i32\n" +
            "    pub init(l: i32) { legs = l }\n" +
            "    pub open func speak(): String { return \"...\" }\n" +
            "    pub func legCount(): i32 { return legs }\n" +
            "}\n" +
            "pub class Dog : Animal implements Named {\n" +
            "    pub var good: bool\n" +
            "    pub init(l: i32, g: bool) { legs = l\n" +
            "        good = g }\n" +
            "    pub override func speak(): String { return \"woof\" }\n" +
            "    pub override func name(): String { return \"dog\" }\n" +
            "}\n" +
            "pub enum struct Direction {\n" +
            "    pub const degrees: i32\n" +
            "    pub init(_ -> degrees)\n" +
            "}[\n" +
            "    North(0),\n" +
            "    South(180),\n" +
            "    East(90),\n" +
            "    West(270)\n" +
            "]\n" +
            "pub struct Point {\n" +
            "    pub var x: i32\n" +
            "    pub var y: i64\n" +
            "    pub var tag: String\n" +
            "}\n" +
            "pub class Link {\n" +
            "    pub var a: i32 = 0\n" +
            "    pub var p: Link? = null\n" +
            "    pub var b: i64 = 0L\n" +
            "    pub var q: Link? = null\n" +
            "}\n" +
            "pub rich struct Handle {\n" +
            "    pub var target: Link? = null\n" +
            "}\n" +
            "pub class Holder {\n" +
            "    pub var a: i32 = 0\n" +
            "    pub var h: Handle\n" +
            "    pub init(v: Handle) { h = v }\n" +
            "}\n" +
            "pub func main(): i32 {\n" +
            "    return 0\n" +
            "}\n";

        private static RigiCompiler.Middleware.Layout.LayoutPlanTable BuildLayout(string source)
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(source);
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "layout.bil");
            TestHarness.CheckTrue("布局用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            return RigiCompiler.Middleware.Layout.LayoutEngine.Build(
                new MwContext(gate.Module!).Symbols);
        }

        private static RigiCompiler.Middleware.Layout.FieldPlan? FieldOf(
            RigiCompiler.Middleware.Layout.TypeLayoutPlan plan, string namePart)
        {
            foreach (var field in plan.Fields)
            {
                if (field.Symbol.Contains(namePart))
                {
                    return field;
                }
            }
            return null;
        }

    }
}
