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
        // DataAccess 职责；与主文件共享同一类型、字段及生命周期。

        // ===== getid.var + typeid 装箱 .any（MW8a）=====
        private static void TestGetTypeIdVarEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func probe\\<T>(x: T): Type\\<T> { return typeOf(x) }\n" +
                "pub func main(): i32 {\n" +
                "    var n = 42\n" +
                "    var t = typeOf(n)\n" +
                "    var a: Animal = new Dog()\n" +
                "    var td = typeOf(a)\n" +
                "    var boxed: Any = t\n" +
                "    var back = boxed as Type\\<i32>\n" +
                "    var g = probe\\<i32>(7)\n" +
                "    if (n is t) { }\n" +
                "    if (n is back) { }\n" +
                "    if (a is td) { }\n" +
                "    if (7 is g) { }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "typeof.var.bil");
            CaseAssertions.CheckTrue("getid.var 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 getid.var", allInsts.OfType<MirGetTypeIdVar>().Any());
            CaseAssertions.CheckTrue("MIR 含 typeid 装箱 .any", allInsts.OfType<MirBoxAny>().Any());
            CaseAssertions.CheckTrue("MIR 含 typeid 拆箱", allInsts.OfType<MirUnboxAny>().Any());

            using var llvmLease5095 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("getid.var 常量 store 路径",
                ll.Contains("store ptr @\"typesheet.core::i32\""), ll);
            CaseAssertions.CheckTrue("getid.var rigi_typeof 调用路径",
                ll.Contains("call ptr @rigi_typeof(i64") && ll.Contains("declare ptr @rigi_typeof(i64"),
                ll);
            CaseAssertions.CheckTrue("构造 .typeid<i32> sheet",
                ll.Contains("typesheet.core::Type<core::i32>"), ll);
            CaseAssertions.CheckTrue("typeid tag0 装箱",
                ll.Contains("or i64") && ll.Contains("insertvalue { i64, i64 }"), ll);
            CaseAssertions.CheckTrue(".null 内建 sheet",
                ll.Contains("@\"typesheet..null\"") || ll.Contains("@typesheet..null"), ll);
        }

        // ===== .typeid<X> 构造 sheet 全链（MW8c-1）=====

        private static void TestTypeIdConstructedSheets()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var t = typeOf(42)\n" +
                "    var boxed: Any = t\n" +
                "    var back = boxed as Type\\<i32>\n" +
                "    if (t is Type\\<i32>) { }\n" +
                "    if (t is Type\\<String>) { }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "typeid.constructed.bil");
            CaseAssertions.CheckTrue("构造 typeid 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            CaseAssertions.CheckTrue("布局含 Type<i32> 计划",
                context.Layout != null
                && context.Layout.Find("core::Type<core::i32>") != null);
            var i32Plan = context.Layout!.Find("core::Type<core::i32>")!;
            CaseAssertions.CheckTrue("Type<i32> 值类型 sheet 形态",
                i32Plan.Kind == TypeLayoutKind.Struct
                && i32Plan.Size == 8
                && i32Plan.TypeFlags == TypeLayoutPlan.FlagInlineValue
                && i32Plan.RefMapCount == 0
                && i32Plan.IMap.Count == 0
                && i32Plan.VTableSlots.Count == 0
                && i32Plan.BasePlan == null);
            using var llvmLease5141 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("构造 Type<i32> TypeSheet 全局",
                ll.Contains("typesheet.core::Type<core::i32>"), ll);
            CaseAssertions.CheckTrue("构造 Type<String> TypeSheet 全局",
                ll.Contains("typesheet.core::Type<core::String>"), ll);
            CaseAssertions.CheckTrue("装箱视图用构造键而非擦除 .typeid",
                !ll.Contains("typesheet..typeid") && ll.Contains("typesheet.core::Type<core::i32>"),
                ll);
            CaseAssertions.CheckTrue(".null sheet 锚点",
                ll.Contains("@\"typesheet..null\"") || ll.Contains("@typesheet..null"), ll);
        }

        // ===== 对象路径发射（MW4 批 2：new/字段/虚派发/.this 形态）=====

        private static void TestObjectPathEmission()
        {
            // 真实前端路径 + 全管线：基类槽装派生实例的虚调用
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub open class Base {\n" +
                "    pub init() { }\n" +
                "    pub open func who(): i32 { return 1 }\n" +
                "}\n" +
                "pub class Derived : Base {\n" +
                "    pub init() { }\n" +
                "    pub override func who(): i32 { return 2 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: Base = new Derived()\n" +
                "    return (b.who())\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "obj.bil");
            CaseAssertions.CheckTrue("对象用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;

            // .this 隐藏首参落参数表首位（胖引用形态）
            var who = mir.Functions.First(f => f.Symbol.Canonical.Contains("Derived$who"));
            CaseAssertions.CheckTrue(".this 隐藏首参",
                who.Parameters.Count == 1 && who.Parameters[0].Name == ".this");

            using var llvmLease5185 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // new → rigi_alloc(@typesheet.X)
            CaseAssertions.CheckTrue("new 调 rigi_alloc",
                ll.Contains("call ptr @rigi_alloc(ptr @typesheet.Derived)"), ll);
            // 虚调用 → rigi_vtable_entry 查槽
            CaseAssertions.CheckTrue("虚调用经 rigi_vtable_entry",
                ll.Contains("call ptr @rigi_vtable_entry(ptr"), ll);
            // 可达性扩编：Derived 的 vtable 条目非 null（override 实现进
            // 可达闭包，批 1 的 null 槽消灭）
            CaseAssertions.CheckTrue("vtable 条目非 null（可达性扩编）",
                ll.Contains("@typesheet.vtable.Derived = internal constant [2 x ptr] " +
                    "[ptr @mw.init.dispatch.Derived, ptr @\"Derived$who()@.i32\"]"), ll);
            // 实例方法签名：首参胖引用 {i64,i64}
            CaseAssertions.CheckTrue("实例方法首参胖引用",
                ll.Contains("define internal i32 @\"Derived$who()@.i32\"({ i64, i64 }"), ll);
        }

        // ===== 值类型路径发射（MW4 批 3：内联槽/判别/memcpy/ABI 形态）=====

        private static void TestValuePathEmission()
        {
            // 真实前端路径 + 全管线：struct 构造/方法与 enum case/判别
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub struct Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "    pub func sum(): i32 { return (x + y) }\n" +
                "}\n" +
                "pub enum struct Direction {\n" +
                "    pub const degrees: i32\n" +
                "    pub init(_ -> degrees)\n" +
                "}[\n" +
                "    North(0) -> 7,\n" +
                "    East(90) -> 42\n" +
                "]\n" +
                "pub func makePoint(a: i32, b: i32): Point { return new Point(a, b) }\n" +
                "pub func main(): i32 {\n" +
                "    var p = makePoint(1, 2)\n" +
                "    var s = p.sum()\n" +
                "    var d = Direction.East\n" +
                "    if (d is .East) { return s }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "value.bil");
            CaseAssertions.CheckTrue("值类型用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;

            // MIR 形态：new.case / type.is.case / new type(V)
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 MirNewValue", allInsts.OfType<MirNewValue>().Any());
            CaseAssertions.CheckTrue("MIR 含 MirNewCase", allInsts.OfType<MirNewCase>().Any());
            CaseAssertions.CheckTrue("MIR 含 MirIsCase", allInsts.OfType<MirIsCase>().Any());

            using var llvmLease5246 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // 值类型局部 = 计划尺寸内联 alloca（Point = 2×i32 = 8B）
            CaseAssertions.CheckTrue("值类型内联 alloca", ll.Contains("alloca [8 x i8]"), ll);
            // new 值类型：整槽清零（VM ZeroOf）
            CaseAssertions.CheckTrue("值类型清零 memset",
                ll.Contains("call void @llvm.memset.p0.i64"), ll);
            // 值语义深拷贝 memcpy
            CaseAssertions.CheckTrue("值语义 memcpy",
                ll.Contains("call void @llvm.memcpy.p0.p0.i64"), ll);
            // enum 判别：u32 @ 偏移 0 写判别常量（East -> 42）
            CaseAssertions.CheckTrue("判别 u32 写", ll.Contains("store i32 42, ptr"), ll);
            // type.is.case：读判别 + icmp eq 判别常量
            CaseAssertions.CheckTrue("判别比较", ll.Contains("icmp eq i32 %case.disc, 42"), ll);
            // 值类型返回 ABI：隐藏 out 首参 + void 返回
            CaseAssertions.CheckTrue("值返回隐藏 out 首参",
                ll.Contains("define internal void @\"$makePoint(a:.i32,b:.i32)@Point\"(ptr"), ll);
            // 值类型 .this：首参传指针（alloca 地址别名）
            CaseAssertions.CheckTrue("值类型 .this 传指针",
                ll.Contains("define internal i32 @\"Point$sum()@.i32\"(ptr"), ll);
        }

        // ===== 静态字段与 rigi_entry stub（MW4 批 4）=====

        private static void TestStaticEmission()
        {
            // 真实前端路径 + 全管线：全局字段 + class static + 初值缝合
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub var gCounter: i32 = 41\n" +
                "pub class Config {\n" +
                "    pub static var level: i32 = 3\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    gCounter = (gCounter + Config.level)\n" +
                "    return gCounter\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "static.bil");
            CaseAssertions.CheckTrue("静态用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;

            // MIR 形态：静态读写指令；..globals.init 恒可达（不在 invoke 闭包内）
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 MirGetStatic", allInsts.OfType<MirGetStatic>().Any());
            CaseAssertions.CheckTrue("MIR 含 MirSetStatic", allInsts.OfType<MirSetStatic>().Any());
            CaseAssertions.CheckTrue("..globals.init 恒可达",
                mir.Functions.Any(f => f.Symbol.Canonical == "$..globals.init()@.void"));

            using var llvmLease5299 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();

            // 静态槽全局（canonical 键、零值初始化、internal 链接）
            CaseAssertions.CheckTrue("全局字段槽锚点",
                ll.Contains("@\"static.#gCounter@.i32\" = internal global i32 0"), ll);
            CaseAssertions.CheckTrue("class static 槽锚点",
                ll.Contains("@\"static.Config#.static.level@.i32\" = internal global i32 0"), ll);
            // 入口 stub：..globals.init → main 调用序
            CaseAssertions.CheckTrue("rigi_entry stub 锚点",
                ll.Contains("define i32 @rigi_entry(i32 %0, ptr %1)"), ll);
            CaseAssertions.CheckTrue("stub 先调 ..globals.init 再调 main",
                ll.Contains("call void @\"$..globals.init()@.void\"()")
                && ll.IndexOf("call void @\"$..globals.init()@.void\"()",
                    System.StringComparison.Ordinal)
                < ll.IndexOf("call i32 @\"$main()@.i32\"()", System.StringComparison.Ordinal), ll);
            // 用户 main 以 canonical 名发射（internal 链接）
            CaseAssertions.CheckTrue("main 以 canonical 名发射",
                ll.Contains("define internal i32 @\"$main()@.i32\"()"), ll);
            CaseAssertions.CheckTrue("静态用例发射 rigi_globals_cleanup",
                ll.Contains("define void @rigi_globals_cleanup()"), ll);
        }

        // ===== 数组路径（MW4：alloc_array / get.array / set.array / length / raw）=====

        private static void TestArrayPathEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 7\n" +
                "    var x = a[0] if? 0\n" +
                "    var y = a[9] if? -1\n" +
                "    return ((x + y) + a.length)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "arr.bil");
            CaseAssertions.CheckTrue("数组用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 getid.type", allInsts.OfType<MirGetTypeId>().Any());
            CaseAssertions.CheckTrue("MIR 含 get.array", allInsts.OfType<MirGetArray>().Any());
            CaseAssertions.CheckTrue("MIR 含 set.array", allInsts.OfType<MirSetArray>().Any());
            CaseAssertions.CheckTrue("MIR 含 Nullable 拆箱", allInsts.OfType<MirUnwrapNullable>().Any());
            CaseAssertions.CheckTrue("MIR 含 Array.length",
                allInsts.OfType<MirGetField>().Any(f =>
                    RigiCompiler.Middleware.Layout.TypeLayout.IsLengthField(f.FieldSymbol)));

            using var llvmLease5352 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("alloc_array 面",
                ll.Contains("call ptr @rigi_alloc_array(ptr"), ll);
            CaseAssertions.CheckTrue("内建 i32 TypeSheet",
                ll.Contains("@\"typesheet.core::i32\""), ll);
            CaseAssertions.CheckTrue("Array<i32> 具有独立闭合 TypeSheet",
                ll.Contains("@\"typesheet.core::Array<core::i32>\""), ll);
            CaseAssertions.CheckTrue("越界读得 null 分支",
                ll.Contains("arr.get.oob"), ll);

            const string rawBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"rawarr\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Data = raw.hex x2FF2331C,\n" +
                "    R_Zero = i32 0\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        .array<.u8> d,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Data) $d\n" +
                "        load res(R_Zero) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n";
            var rawGate = BilGate.Accept(rawBil, "rawarr.bil");
            CaseAssertions.CheckTrue("raw→array<u8> 门禁放行", rawGate.IsAccepted,
                string.Join("; ", rawGate.Errors));
            var rawContext = new MwContext(rawGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(rawContext);
            using var llvmLease5401 = LlvmHost.Enter();
            using var rawModule = ModuleBuilder.Build(rawContext, rawContext.Mir!);
            var rawLl = rawModule.PrintToString();
            CaseAssertions.CheckTrue("raw 字节常量",
                rawLl.Contains("@raw.R_Data") && rawLl.Contains("c\"/\\F23\\1C\""), rawLl);
            CaseAssertions.CheckTrue("raw 走 alloc_array",
                rawLl.Contains("call ptr @rigi_alloc_array(ptr"), rawLl);
        }

        // ===== Span 路径（MW7b：span_alloc / 具化 sheet / stride 访问）=====

        private static void TestSpanPathEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<i32>(3)\n" +
                "    a[0] = 7\n" +
                "    var x = a[0] if? 0\n" +
                "    return (x + a.length)\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "span.bil");
            CaseAssertions.CheckTrue("Span 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var mir = context.Mir!;
            var allInsts = mir.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 get.array（Span）",
                allInsts.OfType<MirGetArray>().Any(g =>
                    TypeLayout.IsSpan(g.CollectionType)));
            CaseAssertions.CheckTrue("MIR 含 set.array（Span）",
                allInsts.OfType<MirSetArray>().Any(s =>
                    TypeLayout.IsSpan(s.CollectionType)));
            CaseAssertions.CheckTrue("MIR 含 Span.length",
                allInsts.OfType<MirGetField>().Any(f =>
                    TypeLayout.IsLengthField(f.FieldSymbol)));
            var spanPlan = context.Layout!.Find("core::Span<core::i32>");
            CaseAssertions.CheckTrue("具化 Span<i32> 计划入表", spanPlan != null);
            CaseAssertions.CheckTrue("Span 计划 FlagArray 且无 FlagShared",
                spanPlan != null
                && (spanPlan.TypeFlags & TypeLayoutPlan.FlagArray) != 0
                && (spanPlan.TypeFlags & TypeLayoutPlan.FlagShared) == 0);

            using var llvmLease5446 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, mir);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("span_alloc 编组形状",
                ll.Contains("call ptr @rigi_span_alloc(ptr"), ll);
            CaseAssertions.CheckTrue("具化 Span<i32> TypeSheet",
                ll.Contains("typesheet.core::Span<core::i32>"), ll);
            CaseAssertions.CheckTrue("Span sheet FlagArray（size 32 + flags 16）",
                SheetHasFlags(ll, "typesheet.core::Span<core::i32>", 32, 16), ll);
            CaseAssertions.CheckTrue("i32 stride 常量 4",
                ll.Contains("mul i64") && ll.Contains(", 4"), ll);

            var sharedLl = EmitLlFromSource(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = sharedSpanOf\\<i32>(1)\n" +
                "    return a.length\n" +
                "}\n",
                "span.shared.bil");
            CaseAssertions.CheckTrue("具化 SharedSpan<i32> TypeSheet",
                sharedLl.Contains("typesheet.core::SharedSpan<core::i32>"), sharedLl);
            CaseAssertions.CheckTrue("SharedSpan sheet FlagArray|FlagShared（flags 18）",
                SheetHasFlags(sharedLl, "typesheet.core::SharedSpan<core::i32>", 32, 18),
                sharedLl);

            var strLl = EmitLlFromSource(
                "import core.collections.*\n" +
                "pub func main(): i32 {\n" +
                "    var a = spanOf\\<String>(2)\n" +
                "    a[0] = \"x\"\n" +
                "    return a.length\n" +
                "}\n",
                "span.str.bil");
            CaseAssertions.CheckTrue("String 元素 stride 常量 16",
                strLl.Contains("mul i64") && strLl.Contains(", 16"), strLl);
        }

        private static bool SheetHasFlags(string ll, string sheetNeedle, int size, int flags)
        {
            var idx = ll.IndexOf(sheetNeedle, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }
            var slice = ll.Substring(idx, Math.Min(400, ll.Length - idx));
            return slice.Contains($"i32 {size}, i32 {flags}");
        }

    }
}
