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
        // Types 职责；与主文件共享同一类型、字段及生命周期。

        // ===== Any/Box 物化（MW5 切片 c1：MirBoxAny/MirUnboxAny + tag 编码）=====

        private static void TestBoxAnyEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 42\n" +
                "    var a = x as Any\n" +
                "    var y = a as i32\n" +
                "    var s = \"hi\"\n" +
                "    var b = s as Any\n" +
                "    var t = b as String\n" +
                "    if (t == \"hi\") { return y }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "box.any.bil");
            CaseAssertions.CheckTrue("Box Any 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 MirBoxAny", allInsts.OfType<MirBoxAny>().Any());
            CaseAssertions.CheckTrue("MIR 含 MirUnboxAny", allInsts.OfType<MirUnboxAny>().Any());

            using var llvmLease5971 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("tag0 pack：or + insertvalue",
                ll.Contains("or i64") && ll.Contains("insertvalue { i64, i64 }"), ll);
            CaseAssertions.CheckTrue("tag1：rigi_malloc + memcpy",
                ll.Contains("call ptr @rigi_malloc(i32")
                && ll.Contains("call void @llvm.memcpy.p0.p0.i64"), ll);
            CaseAssertions.CheckTrue("unbox 类型检查（lshr tag + and sheet）",
                ll.Contains("lshr i64") && ll.Contains("and i64"), ll);
            // MW9b-G：拆箱不符由 abort 改抛 CastException
            CaseAssertions.CheckTrue("unbox 不符抛 CastException",
                ll.Contains("call void @rigi_exc_raise(ptr")
                && ll.Contains("CastException$init(fromType:"), ll);
            CaseAssertions.CheckTrue("invalid_cast abort 面已退场",
                !ll.Contains("rigi_abort_invalid_cast"), ll);

            var numLl = EmitLlFromSource(
                "pub func main(): i32 {\n" +
                "    var x = 1\n" +
                "    var y = x as i64\n" +
                "    return (y as i32)\n" +
                "}\n", "cast.num.bil");
            CaseAssertions.CheckTrue("数值 widening 发射 sext",
                numLl.Contains("sext i32"), numLl);
            CaseAssertions.CheckTrue("数值 narrowing 发射 trunc",
                numLl.Contains("trunc i64"), numLl);

            var phLl = EmitLlFromSource(
                "pub func conv\\<T>(x: Any): T { return x as T }\n" +
                "pub func main(): i32 {\n" +
                "    return conv\\<i32>(42 as Any)\n" +
                "}\n", "cast.ph.bil");
            CaseAssertions.CheckTrue("占位 cast 调 rigi_try_cast",
                phLl.Contains("call i32 @rigi_try_cast("), phLl);
            CaseAssertions.CheckTrue("占位 cast 失败抛 CastException",
                phLl.Contains("call void @rigi_exc_raise(ptr")
                && phLl.Contains("CastException$init(fromType:"), phLl);

            var stLl = EmitLlFromSource(
                "pub struct A {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub struct B {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new A(1)\n" +
                "    var b = a as B\n" +
                "    return 0\n" +
                "}\n", "cast.struct.bil");
            CaseAssertions.CheckTrue("struct 非恒等抛 CastException",
                stLl.Contains("call void @rigi_exc_raise(ptr")
                && stLl.Contains("CastException$init(fromType:"), stLl);
        }

        private static string EmitLlFromSource(string source, string label)
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(source);
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, label);
            CaseAssertions.CheckTrue(label + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease6037 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            return module.PrintToString();
        }

        private static void ExpectMwNotSupportedFromSource(string source, string needle, string label)
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(source);
            text = BilWriter.Write(textModule);
            ExpectMwNotSupportedFromBil(text, needle, label);
        }

        private static void ExpectMwNotSupportedFromBil(string text, string needle, string label)
        {
            var gate = BilGate.Accept(text, label);
            CaseAssertions.CheckTrue(label + " 门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var caught = false;
            var message = "";
            try
            {
                var context = new MwContext(gate.Module!);
                RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
                using var llvmLease6059 = LlvmHost.Enter();
                using var module = ModuleBuilder.Build(context, context.Mir!);
            }
            catch (MwNotSupportedException ex)
            {
                caught = ex.Message.Contains(needle);
                message = ex.Message;
            }
            CaseAssertions.CheckTrue(label, caught, message);
        }

        private static void ExpectGateRejectedFromBil(string text, string needle, string label)
        {
            var gate = BilGate.Accept(text, label);
            CaseAssertions.CheckTrue(label, !gate.IsAccepted
                && gate.Errors.Any(error => error.Contains(needle)), string.Join("; ", gate.Errors));
        }

        // ===== 接口默认方法布局（iMap 槽指向接口 fn）=====

        private static void TestInterfaceDefaultMethods()
        {
            var ll = EmitLlFromSource(
                "pub interface Shape {\n" +
                "    pub func area(): i32\n" +
                "    pub func describe(): String { return \"d\" }\n" +
                "}\n" +
                "pub class Sq implements Shape {\n" +
                "    pub var s: i32\n" +
                "    pub init(n: i32) { s = n }\n" +
                "    pub override func area(): i32 { return (s * s) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var sh: Shape = new Sq(3)\n" +
                "    return sh.area()\n" +
                "}\n",
                "iface.default.bil");
            CaseAssertions.CheckTrue("默认方法未抛未实现",
                !ll.Contains("MW4 接口方法未实现"), ll);
            CaseAssertions.CheckTrue("vtable 含接口默认方法",
                ll.Contains("Shape$describe") || ll.Contains("@\"Shape$describe"), ll);

            var genLl = EmitLlFromSource(
                "pub interface IBox\\<T> {\n" +
                "    func get(): T\n" +
                "    func tag(): i32 { return 7 }\n" +
                "}\n" +
                "pub class Box3\\<T> implements IBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub override func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: IBox\\<i32> = new Box3\\<i32>(41)\n" +
                "    return b.tag()\n" +
                "}\n",
                "iface.generic.default.bil");
            CaseAssertions.CheckTrue("泛型接口默认方法未抛未实现",
                !genLl.Contains("MW4 接口方法未实现"), genLl);
            CaseAssertions.CheckTrue("泛型接口默认方法入表",
                genLl.Contains("IBox$tag") || genLl.Contains("@\"IBox$tag"), genLl);
        }

        // ===== 构造类型具化（MW5 c2-a）=====

        private static void TestConstructedTypes()
        {
            // 收集闭包：嵌套构造 + 环保护（A<T>:B<T>:A<T> 手写 BIL，不经门禁）
            var cycleBil =
                "BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n}\n\n" +
                "LocalSymbols {\n" +
                "    .type Box = class generic(T) pub {\n" +
                "        .field Box#v@.generic<$.generic.T> pub var\n" +
                "        .method Box$init(v:.generic<$.generic.T>)@.void pub init\n" +
                "    }\n" +
                "    .type A = class generic(T)\n" +
                "        extends B<.generic<$.generic.T>>\n" +
                "        pub {\n" +
                "        .method A$init()@.void pub init\n" +
                "    }\n" +
                "    .type B = class generic(T)\n" +
                "        extends A<.generic<$.generic.T>>\n" +
                "        pub {\n" +
                "        .method B$init()@.void pub init\n" +
                "    }\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n\nExternalSymbols {\n}\n\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        Box<.i32> b,\n" +
                "        Box<Box<.i32>> n,\n" +
                "        A<.i32> a\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        ret\n" +
                "    }\n" +
                "}\n";
            var cycleModule = BilReader.Read(cycleBil);
            var cycleCtx = new MwContext(cycleModule);
            var collected = ConstructedTypeCollector.Collect(cycleCtx);
            CaseAssertions.CheckTrue("收集含 Box<i32>",
                collected.Contains("Box<core::i32>"));
            CaseAssertions.CheckTrue("收集含嵌套 Box<Box<i32>>",
                collected.Contains("Box<Box<core::i32>>"));
            CaseAssertions.CheckTrue("收集含环上 A<i32>/B<i32>",
                collected.Contains("A<core::i32>") && collected.Contains("B<core::i32>"));
            CaseAssertions.CheckTrue("环保护不重复入表",
                collected.Count(c => c == "A<core::i32>") == 1);

            // 具化计划：隐藏 typeid + 胖值槽 + vtable=模板 fn
            var (_, srcTextModule, srcText) = BilTestHarness.EmitBilUnit(
                "pub class Box2\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box2\\<i32>(7)\n" +
                "    return b.get()\n" +
                "}\n");
            srcText = BilWriter.Write(srcTextModule);
            var gate = BilGate.Accept(srcText, "c2a.bil");
            CaseAssertions.CheckTrue("构造类型源门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var closed = ConstructedTypeCollector.Collect(context);
            CaseAssertions.CheckTrue("前端路径收集 Box2<i32>",
                closed.Any(c => c.Contains("Box2") && c.Contains("i32")));
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var plan = context.Layout!.Find("Box2<core::i32>");
            CaseAssertions.CheckTrue("构造计划已入表", plan != null);
            CaseAssertions.CheckTrue("隐藏 typeid 槽@16",
                plan!.HiddenTypeIdSlots.Count == 1
                && plan.HiddenTypeIdSlots[0].ParamName == "T"
                && plan.HiddenTypeIdSlots[0].Offset == 16);
            var vField = FieldOf(plan, "#v@");
            CaseAssertions.CheckTrue("字段复用模板 canonical 且为胖值槽",
                vField != null && vField.IsReferenceSlot && vField.Offset == 32
                && vField.Symbol.StartsWith("Box2#"));
            CaseAssertions.CheckTrue("Size/refMap（头+typeid+胖槽）",
                plan.Size == 48 && plan.RefMap.Length == 1 && plan.RefMap[0] == 1);
            CaseAssertions.CheckTrue("vtable 槽=模板 fn",
                plan.VTableSlots.Any(s => s.Contains("Box2$get(")));

            using var llvmLease6205 = LlvmHost.Enter();
            using var llvm = ModuleBuilder.Build(context, context.Mir!);
            var ll = llvm.PrintToString();
            CaseAssertions.CheckTrue("构造 sheet 全局（转义名）",
                ll.Contains("typesheet.Box2<core::i32>"), ll);
            CaseAssertions.CheckTrue("tag2 pack 指向构造 sheet",
                ll.Contains("typesheet.Box2<core::i32>")
                && (ll.Contains("shl i64 2, 56") || ll.Contains("shl i64 2, i64 56")
                    || ll.Contains("or i64")), ll);
            CaseAssertions.CheckTrue("new 站 typeid 常量 store",
                ll.Contains("typesheet.core::i32") && ll.Contains("store i64"), ll);
            CaseAssertions.CheckTrue("prologue 从隐藏字段 load 类级 typeid",
                ll.Contains("tid.bits") || ll.Contains("load i64"), ll);

            // G1：泛型值类型构造（原受控拒绝翻正）——具化计划复用模板
            // 布局（占位字段 = 16B 胖值槽）、无对象头隐藏 typeid 槽、
            // 类级 typeid 随 init 调用直传（§7.2 序、构造实参 TypeSheet 常量）
            var (_, structTextModule, structText) = BilTestHarness.EmitBilUnit(
                "pub struct Wrap\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub func get(): T { return this.v }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var w = new Wrap\\<i32>(1)\n" +
                "    return w.get()\n" +
                "}\n");
            structText = BilWriter.Write(structTextModule);
            var structGate = BilGate.Accept(structText, "g1.struct.bil");
            CaseAssertions.CheckTrue("泛型 struct 源门禁放行", structGate.IsAccepted,
                string.Join("; ", structGate.Errors));
            var structContext = new MwContext(structGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(structContext);
            var structPlan = structContext.Layout!.Find("Wrap<core::i32>");
            CaseAssertions.CheckTrue("构造 struct 计划已入表", structPlan != null);
            CaseAssertions.CheckTrue("构造 struct 计划为 Struct 且无隐藏 typeid 槽",
                structPlan!.Kind == TypeLayoutKind.Struct
                && structPlan.HiddenTypeIdSlots.Count == 0);
            var structVField = FieldOf(structPlan, "#v@");
            CaseAssertions.CheckTrue("构造 struct 字段复用模板 canonical 且为胖值槽",
                structVField != null && structVField.IsReferenceSlot
                && structVField.Offset == 0 && structVField.Symbol.StartsWith("Wrap#"));
            CaseAssertions.CheckTrue("构造 struct Size=16（单胖槽）且槽 0 init 分发器",
                structPlan.Size == 16 && structPlan.VTableSlots.Count == 1
                && structPlan.VTableSlots[0] == LayoutEngine.InitDispatchSlot);

            using var llvmLease6250 = LlvmHost.Enter();
            using var structLlvm = ModuleBuilder.Build(structContext, structContext.Mir!);
            var structLl = structLlvm.PrintToString();
            CaseAssertions.CheckTrue("构造 struct sheet 全局（转义名）",
                structLl.Contains("typesheet.Wrap<core::i32>"), structLl);
            // init 调用：.this 槽指针 + 类级 typeid 常量 + 胖值实参
            CaseAssertions.CheckTrue("struct init 调用含类级 typeid 实参（TypeSheet 常量直传）",
                structLl.Contains(
                    "call void @\"Wrap$init(v:.generic<$.generic.T>)@.void\"(ptr %")
                && structLl.Contains("typesheet.core::i32"), structLl);
            // get 调用：类级 typeid 实参（擦除 cast 溯源回构造形态）
            CaseAssertions.CheckTrue("struct 方法调用含类级 typeid 实参",
                structLl.Contains(
                    "call void @\"Wrap$get()@.generic<$.generic.T>\"(ptr %")
                || structLl.Contains(
                    "call { i64, i64 } @\"Wrap$get()@.generic<$.generic.T>\"(ptr %"),
                structLl);

            // G1 enum 半边（frontend S11 不支持泛型 enum case，手写 BIL 直驱）：
            // 构造 enum 具化计划 + new.case 判别写入 + init 类级 typeid 直传
            var enumBil =
                "BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n    R_0 = i32 7\n    R_1 = i32 1\n}\n\n" +
                "LocalSymbols {\n" +
                "    .type Choice = enum-struct generic(T) pub {\n" +
                "        .field Choice#tag@.i32 pub var\n" +
                "        .field Choice#payload@.generic<$.generic.T> pub var\n" +
                "        .method Choice$init(tag:.i32,payload:.generic<$.generic.T>)@.void pub init\n" +
                "        .case Choice.Some(tag:.i32,payload:.generic<$.generic.T>) discriminant auto\n" +
                "    }\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n\nExternalSymbols {\n}\n\n" +
                "fn(Choice$init(tag:.i32,payload:.generic<$.generic.T>)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        .this = Choice<.generic<$.generic.T>>,\n" +
                "        .generic.T = .typeid,\n" +
                "        tag = .i32,\n" +
                "        payload = .generic<$.generic.T>\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        set.field $tag $.this field(Choice#tag@.i32)\n" +
                "        set.field $payload $.this field(Choice#payload@.generic<$.generic.T>)\n" +
                "        ret\n" +
                "    }\n" +
                "}\n\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        Choice<.i32> c,\n" +
                "        .i32 .t0,\n" +
                "        .i32 .t1\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_0) $.t0\n" +
                "        load res(R_1) $.t1\n" +
                "        new.case type(Choice<.i32>) case(Choice.Some) $c [$.t1, $.t0]\n" +
                "        ret $.t0\n" +
                "    }\n" +
                "}\n";
            var enumGate = BilGate.Accept(enumBil, "g1.enum.bil");
            CaseAssertions.CheckTrue("构造 enum 手写 BIL 门禁放行", enumGate.IsAccepted,
                string.Join("; ", enumGate.Errors));
            var enumContext = new MwContext(enumGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(enumContext);
            var enumPlan = enumContext.Layout!.Find("Choice<core::i32>");
            CaseAssertions.CheckTrue("构造 enum 计划已入表（Enum kind + 判别复用模板）",
                enumPlan != null && enumPlan.Kind == TypeLayoutKind.Enum
                && enumPlan.EnumCases.Count == 1
                && enumPlan.EnumCases[0].Discriminant == 0u);
            using var llvmLease6322 = LlvmHost.Enter();
            using var enumLlvm = ModuleBuilder.Build(enumContext, enumContext.Mir!);
            var enumLl = enumLlvm.PrintToString();
            CaseAssertions.CheckTrue("构造 enum sheet 全局（转义名）",
                enumLl.Contains("typesheet.Choice<core::i32>"), enumLl);
            CaseAssertions.CheckTrue("enum init 调用含类级 typeid 实参（直传）",
                enumLl.Contains(
                    "@\"Choice$init(tag:.i32,payload:.generic<$.generic.T>)@.void\"(ptr %")
                && enumLl.Contains("typesheet.core::i32"), enumLl);

            // G2 保留边界（手写 BIL 直驱；frontend P3 已拒 interface/wrapper
            // 构造）：构造 interface/wrapper 的 new 仍受控拒绝——native 对
            // 「无 init 声明 + 零实参」构造（含非泛型 class 同形）本就是
            // 受控拒绝面，两形态落在同一边界内
            ExpectMwNotSupportedFromBil(
                "BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n    R_0 = i32 0\n}\n\n" +
                "LocalSymbols {\n" +
                "    .type IBox = interface generic(T) pub {\n" +
                "        .method IBox$get()@.generic<$.generic.T> pub\n" +
                "    }\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n\nExternalSymbols {\n}\n\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        IBox<.i32> b,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        new type(IBox<.i32>) $b []\n" +
                "        load res(R_0) $r\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n",
                "构造类型形态",
                "G2 构造 interface new 保留受控拒绝");
            ExpectGateRejectedFromBil(
                "BIL \"1.1\"\n\nMetadata {\n}\n\nResources {\n    R_0 = i32 5\n}\n\n" +
                "LocalSymbols {\n" +
                "    .type W = wrapper generic(T) pub rich {\n" +
                "        .field W#level@.generic<$.generic.T> pub var\n" +
                "        .method W$init(level:.generic<$.generic.T>)@.void pub init\n" +
                "    }\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n\nExternalSymbols {\n}\n\n" +
                "fn(W$init(level:.generic<$.generic.T>)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        .this = W<.generic<$.generic.T>>,\n" +
                "        .generic.T = .typeid,\n" +
                "        level = .generic<$.generic.T>\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        set.field $level $.this field(W#level@.generic<$.generic.T>)\n" +
                "        ret\n" +
                "    }\n" +
                "}\n\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        W<.i32> w,\n" +
                "        .i32 r\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_0) $r\n" +
                "        new type(W<.i32>) $w [$r]\n" +
                "        ret $r\n" +
                "    }\n" +
                "}\n",
                "wrapper 借用不能整体取值",
                "G2 构造 wrapper new 在 BIL 门禁拒绝");

            // G4：存在未构造的泛型 operator 宿主不应拒绝实际 Num 调用。
            var genericCandidateSource =
                "pub interface Addable {\n" +
                "    operator plus(another: Addable): Addable\n" +
                "}\n" +
                "pub class Num implements Addable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub operator plus(another: Addable): Addable {\n" +
                "        return new Num(this.n + ((another as Num).n))\n" +
                "    }\n" +
                "}\n" +
                "pub class GBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(_ -> v)\n" +
                "    pub operator plus(other: GBox\\<T>): GBox\\<T> {\n" +
                "        return new GBox\\<T>(this.v)\n" +
                "    }\n" +
                "}\n" +
                "func add\\<T extends Addable>(a: T, b: T): Addable {\n" +
                "    return a + b\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = add\\<Num>(new Num(1), new Num(2))\n" +
                "    return 0\n" +
                "}\n";
            var (_, genericCandidateModule, _) = BilTestHarness.EmitBilUnit(genericCandidateSource);
            var genericCandidateGate = BilGate.Accept(BilWriter.Write(genericCandidateModule), "generic-candidate.bil");
            CaseAssertions.CheckTrue("G4 泛型 class 候选源码门禁放行", genericCandidateGate.IsAccepted,
                string.Join("; ", genericCandidateGate.Errors));
            var genericCandidateContext = new MwContext(genericCandidateModule);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(genericCandidateContext);
            using var llvmLease6431 = LlvmHost.Enter();
            using var genericCandidateLlvm = ModuleBuilder.Build(genericCandidateContext, genericCandidateContext.Mir!);
            CaseAssertions.CheckTrue("G4 未构造泛型宿主不阻断实际 operator 派发",
                genericCandidateLlvm.PrintToString().Contains("typesheet.Num"));
            ExpectMwNotSupportedFromSource(
                genericCandidateSource.Replace("pub class GBox", "pub rich struct GBox"),
                "候选 operator 宿主为泛型类型", "G4 泛型值类型 operator 宿主仍受控拒绝");
        }

        // ===== 构造接口 iMap + VirtualSlotOf 精确化（MW5 c2-b）=====

        private static void TestConstructedDispatch()
        {
            var (_, ifaceSrcModule, ifaceSrc) = BilTestHarness.EmitBilUnit(
                "pub interface IBox\\<T> {\n" +
                "    func get(): T\n" +
                "    func tag(): i32\n" +
                "}\n" +
                "pub class Box3\\<T> implements IBox\\<T> {\n" +
                "    pub var v: T\n" +
                "    pub init(v: T) { this.v = v }\n" +
                "    pub override func get(): T { return this.v }\n" +
                "    pub override func tag(): i32 { return 7 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b: IBox\\<i32> = new Box3\\<i32>(1)\n" +
                "    return b.tag()\n" +
                "}\n");
            ifaceSrc = BilWriter.Write(ifaceSrcModule);
            var ifaceGate = BilGate.Accept(ifaceSrc, "c2b.imap.bil");
            CaseAssertions.CheckTrue("构造接口源门禁放行", ifaceGate.IsAccepted,
                string.Join("; ", ifaceGate.Errors));
            var ifaceCtx = new MwContext(ifaceGate.Module!);
            var collected = ConstructedTypeCollector.Collect(ifaceCtx);
            CaseAssertions.CheckTrue("收集含 IBox<i32>",
                collected.Any(c => c.Contains("IBox") && c.Contains("i32")));
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(ifaceCtx);
            var boxPlan = ifaceCtx.Layout!.Find("Box3<core::i32>");
            CaseAssertions.CheckTrue("Box3<i32> 计划已入表", boxPlan != null);
            CaseAssertions.CheckTrue("构造接口 iMap 段键与槽基址",
                boxPlan!.IMap.Count >= 1
                && boxPlan.IMap.Any(e => e.InterfaceType == "IBox<core::i32>"
                    && e.BaseOffset == 3),
                boxPlan == null ? "" : string.Join(",", boxPlan.IMap));
            CaseAssertions.CheckTrue("iMap 槽序复用本类 get/tag",
                boxPlan!.VTableSlots.Count == 5
                && boxPlan.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && boxPlan.VTableSlots[3] == boxPlan.VTableSlots[1]
                && boxPlan.VTableSlots[4] == boxPlan.VTableSlots[2]);
            var ifacePlan = ifaceCtx.Layout.Find("IBox<core::i32>");
            CaseAssertions.CheckTrue("IBox<i32> 空壳 sheet 计划",
                ifacePlan != null
                && ifacePlan.Kind == TypeLayoutKind.Interface
                && ifacePlan.VTableSlots.Count == 2);
            using var llvmLease6484 = LlvmHost.Enter();
            using var ifaceLlvm = ModuleBuilder.Build(ifaceCtx, ifaceCtx.Mir!);
            var ifaceLl = ifaceLlvm.PrintToString();
            CaseAssertions.CheckTrue("构造接口 sheet 全局",
                ifaceLl.Contains("typesheet.IBox<core::i32>"), ifaceLl);
            CaseAssertions.CheckTrue("imap 引用具化接口 sheet",
                ifaceLl.Contains("typesheet.imap.Box3<core::i32>")
                && ifaceLl.Contains("typesheet.IBox<core::i32>"), ifaceLl);

            var (_, virtSrcModule, virtSrc) = BilTestHarness.EmitBilUnit(
                "pub open class PairV\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub open func foo(): i32 { return 1 }\n" +
                "    pub open func bar(): i32 { return 2 }\n" +
                "}\n" +
                "pub class PairD\\<T> : PairV\\<T> {\n" +
                "    pub init() { }\n" +
                "    pub override func foo(): i32 { return 10 }\n" +
                "    pub override func bar(): i32 { return 20 }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var x: PairV\\<i32> = new PairD\\<i32>()\n" +
                "    return (x.foo() + x.bar())\n" +
                "}\n");
            virtSrc = BilWriter.Write(virtSrcModule);
            var virtGate = BilGate.Accept(virtSrc, "c2b.virt.bil");
            CaseAssertions.CheckTrue("双虚泛型类门禁放行", virtGate.IsAccepted,
                string.Join("; ", virtGate.Errors));
            var virtCtx = new MwContext(virtGate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(virtCtx);
            var hostPlan = virtCtx.Layout!.Find("PairV<core::i32>");
            CaseAssertions.CheckTrue("PairV<i32> 构造计划双虚槽",
                hostPlan != null && hostPlan.VTableSlots.Count >= 3
                && hostPlan.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && hostPlan.VTableSlots[1].Contains("$foo(")
                && hostPlan.VTableSlots[2].Contains("$bar("),
                hostPlan == null ? "" : string.Join(",", hostPlan.VTableSlots));
            var derivedPlan = virtCtx.Layout.Find("PairD<core::i32>");
            CaseAssertions.CheckTrue("PairD<i32> override 复用基槽",
                derivedPlan != null && derivedPlan.VTableSlots.Count >= 3
                && derivedPlan.VTableSlots[1].Contains("PairD$foo(")
                && derivedPlan.VTableSlots[2].Contains("PairD$bar("));
            using var llvmLease6525 = LlvmHost.Enter();
            using var virtLlvm = ModuleBuilder.Build(virtCtx, virtCtx.Mir!);
            var virtLl = virtLlvm.PrintToString();
            CaseAssertions.CheckTrue("VirtualSlotOf 命中槽 1 与 2",
                virtLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && virtLl.Contains("i32 1") && virtLl.Contains("i32 2"), virtLl);

            var opLl = EmitLlFromSource(
                "pub class Doubler {\n" +
                "    pub init() { }\n" +
                "    pub operator call(x: i32): i32 { return (x * 2) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var d = new Doubler()\n" +
                "    return d(21)\n" +
                "}\n", "c2b.op.bil");
            CaseAssertions.CheckTrue("operator call 虚槽为 1（槽 0 分发器）",
                opLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && opLl.Contains("i32 1"), opLl);
            var lambdaLl = EmitLlFromSource(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n", "c2b.lambda.bil");
            CaseAssertions.CheckTrue("lambda 间接调用虚槽为 1（槽 0 分发器）",
                lambdaLl.Contains("call ptr @rigi_vtable_entry(ptr")
                && lambdaLl.Contains("i32 1"), lambdaLl);
        }

        // ===== vargs/kwargs 包（MW5 d）：签名放行 + 直译按位 =====

        private static void TestVargsKwargs()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "func sum(nums: i32...): i32 { return nums.length }\n" +
                "func show(opts: named String...): i32 { return opts.length }\n" +
                "func collect\\<TArgs...>(values: TArgs...): i32 { return values.length }\n" +
                "pub func main(): i32 {\n" +
                "    return ((sum(1, 2) + show(a = \"x\")) + collect(1, \"s\"))\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "vargs.bil");
            CaseAssertions.CheckTrue("包签名用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            var mir = MirBuilder.Build(context);

            var sum = mir.Functions.Single(f => f.Symbol.Canonical == "$sum()@.i32");
            CaseAssertions.CheckTrue("vargs 登记为普通参数",
                sum.Parameters.Count == 1 && sum.Parameters[0].Name == ".vargs.nums");
            CaseAssertions.CheckTrue("vargs 槽类型 = Array<Any>",
                TypeLayout.IsArray(sum.Parameters[0].Type)
                && sum.Parameters[0].Type.Canonical.Contains("Any"));

            var show = mir.Functions.Single(f => f.Symbol.Canonical == "$show()@.i32");
            CaseAssertions.CheckTrue("kwargs 登记为普通参数",
                show.Parameters.Count == 1 && show.Parameters[0].Name == ".kwargs.opts");
            CaseAssertions.CheckTrue("kwargs 槽类型 = Array<Pair>",
                TypeLayout.IsArray(show.Parameters[0].Type)
                && show.Parameters[0].Type.Canonical.Contains("Pair"));

            var collect = mir.Functions.Single(f => f.Symbol.Canonical == "$collect()@.i32");
            CaseAssertions.CheckTrue("泛型包+值包按 §7.2 序",
                collect.Parameters.Count == 2
                && collect.Parameters[0].Name == ".generic.TArgs"
                && collect.Parameters[1].Name == ".vargs.values");
            CaseAssertions.CheckTrue("泛型位置包 TypeRef = array（非 typeid）",
                TypeLayout.IsArray(collect.Parameters[0].Type)
                && !TypeLayout.IsTypeId(collect.Parameters[0].Type));
            CaseAssertions.CheckTrue("值包 TypeRef = Array<Any>",
                TypeLayout.IsArray(collect.Parameters[1].Type));

            var main = mir.Functions.Single(f => f.IsEntrypoint);
            var sumCall = main.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                .First(c => c.Target.Canonical == "$sum()@.i32");
            CaseAssertions.CheckTrue("调用点包为单实参", sumCall.Args.Count == 1);
            CaseAssertions.CheckTrue("调用点实参类型 = 包类型",
                sumCall.Args[0] is MirLocalOperand packArg
                && TypeLayout.IsArray(main.FindLocal(packArg.Name).Type));

            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            using var llvmLease6605 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("Any TypeSheet 供 Pair<,Any> 具化",
                ll.Contains("@\"typesheet.core::Any\""), ll);
            CaseAssertions.CheckTrue("sum LLVM 形参 = 胖引用",
                ll.Contains("define internal i32 @\"$sum()@.i32\"({ i64, i64 }"), ll);
            CaseAssertions.CheckTrue("show LLVM 形参 = 胖引用",
                ll.Contains("define internal i32 @\"$show()@.i32\"({ i64, i64 }"), ll);
            CaseAssertions.CheckTrue("collect LLVM 两包均胖引用",
                ll.Contains("define internal i32 @\"$collect()@.i32\"({ i64, i64 }")
                && ll.Contains("{ i64, i64 } %0, { i64, i64 } %1)"), ll);

            // 包转发：前端 take(nums) 会再打包；手改 invoke 整包转发后直译
            var (_, fwdModule, _) = BilTestHarness.EmitBilUnit(
                "func take(nums: i32...): i32 { return nums.length }\n" +
                "func wrap(nums: i32...): i32 { return take(nums) }\n" +
                "pub func main(): i32 { return wrap(1, 2, 3) }\n");
            RewriteWrapForward(fwdModule);
            var fwdText = BilWriter.Write(fwdModule);
            var fwdGate = BilGate.Accept(fwdText, "fwd.bil");
            CaseAssertions.CheckTrue("整包转发门禁放行", fwdGate.IsAccepted,
                string.Join("; ", fwdGate.Errors));
            var fwdCtx = new MwContext(fwdGate.Module!);
            var fwdMir = MirBuilder.Build(fwdCtx);
            var wrap = fwdMir.Functions.Single(f => f.Symbol.Canonical == "$wrap()@.i32");
            var fwdCall = wrap.Blocks.SelectMany(b => b.Instructions).OfType<MirCall>()
                .Single(c => c.Target.Canonical == "$take()@.i32");
            CaseAssertions.CheckTrue("整包转发实参 = $.vargs.nums",
                fwdCall.Args.Count == 1
                && fwdCall.Args[0] is MirLocalOperand { Name: ".vargs.nums" });
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(fwdCtx);
            using var llvmLease6636 = LlvmHost.Enter();
            using var fwdLl = ModuleBuilder.Build(fwdCtx, fwdCtx.Mir!);
            CaseAssertions.CheckTrue("整包转发 LLVM 发射",
                fwdLl.PrintToString().Contains("@\"$take()@.i32\""),
                fwdLl.PrintToString());
        }

    }
}
