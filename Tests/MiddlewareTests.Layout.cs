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
        // Layout 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestLayoutPlans()
        {
            var layout = BuildLayout(LayoutSource);

            // struct：自然对齐 + String 内联 16B（进 refMap kind1）
            var point = layout.Find("Point");
            TestHarness.CheckTrue("Point 已布局", point != null);
            TestHarness.CheckTrue("Point 种类", point!.Kind ==
                RigiCompiler.Middleware.Layout.TypeLayoutKind.Struct);
            TestHarness.CheckTrue("Point 字段偏移",
                FieldOf(point, "#x@")!.Offset == 0
                && FieldOf(point, "#y@")!.Offset == 8
                && FieldOf(point, "#tag@")!.Offset == 16);
            TestHarness.CheckTrue("Point 尺寸/对齐",
                point.Size == 32 && point.Alignment == 16);
            TestHarness.CheckTrue("Point refMap 含 String 槽 kind1",
                point.RefMap.Length == 1
                && FieldOf(point, "#tag@")!.IsStringSlot
                && point.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 1));
            TestHarness.CheckTrue("Point vtable 槽 0 为 init 分发器（一元、无 iMap）",
                point.VTableSlots.Count == 1
                && point.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && point.IMap.Count == 0);

            // class：对象头 16 起排、vtable 本类槽
            var animal = layout.Find("Animal");
            TestHarness.CheckTrue("Animal 已布局", animal != null);
            TestHarness.CheckTrue("Animal 字段偏移", FieldOf(animal!, "#legs@")!.Offset == 16);
            TestHarness.CheckTrue("Animal 尺寸/对齐",
                animal!.Size == 32 && animal.Alignment == 16);
            TestHarness.CheckTrue("Animal vtable 本类槽",
                animal.VTableSlots.Count == 3
                && animal.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && animal.VTableSlots[1] == "Animal$speak()@.string"
                && animal.VTableSlots[2] == "Animal$legCount()@.i32");

            // 继承：基类字段在前（本类字段从基类 Size 续排）、override
            // 复用基槽、interface 实现段独立追加（条目指针与自有槽重复，
            // §7 接口派发模型）+ iMap
            var dog = layout.Find("Dog");
            TestHarness.CheckTrue("Dog 已布局", dog != null);
            TestHarness.CheckTrue("Dog 基类计划", ReferenceEquals(dog!.BasePlan, animal));
            TestHarness.CheckTrue("Dog 字段（继承在前）",
                dog.Fields.Count == 2
                && FieldOf(dog, "#legs@")!.Offset == 16
                && FieldOf(dog, "#good@")!.Offset == 32);
            TestHarness.CheckTrue("Dog 尺寸", dog.Size == 48);
            TestHarness.CheckTrue("Dog override 复用基槽",
                dog.VTableSlots.Count == 5
                && dog.VTableSlots[0] == LayoutEngine.InitDispatchSlot
                && dog.VTableSlots[1] == "Dog$speak()@.string"
                && dog.VTableSlots[2] == "Animal$legCount()@.i32"
                && dog.VTableSlots[3] == "Dog$name()@.string"
                && dog.VTableSlots[4] == "Dog$name()@.string");
            TestHarness.CheckTrue("Dog iMap 段 base offset",
                dog.IMap.Count == 1 && dog.IMap[0].InterfaceType == "Named"
                && dog.IMap[0].BaseOffset == 4);

            // interface 空壳计划（批 2：iMap 键地址 + 接口内槽序表；无实例布局）
            var named = layout.Find("Named");
            TestHarness.CheckTrue("interface 空壳计划",
                named != null && named.Kind ==
                    RigiCompiler.Middleware.Layout.TypeLayoutKind.Interface
                && named.Size == 0 && named.Fields.Count == 0
                && named.VTableSlots.Count == 1
                && named.VTableSlots[0] == "Named$name()@.string");

            // enum：偏移 0 恒 u32 判别，实例字段续排；auto 判别值按声明序
            var direction = layout.Find("Direction");
            TestHarness.CheckTrue("Direction 已布局", direction != null);
            TestHarness.CheckTrue("Direction 种类", direction!.Kind ==
                RigiCompiler.Middleware.Layout.TypeLayoutKind.Enum);
            TestHarness.CheckTrue("Direction 字段偏移/尺寸",
                FieldOf(direction, "#degrees@")!.Offset == 4
                && direction.Size == 8 && direction.Alignment == 4);
            TestHarness.CheckTrue("Direction 判别值表",
                direction.EnumCases.Count == 4
                && direction.EnumCases[0].Discriminant == 0
                && direction.EnumCases[1].Discriminant == 1
                && direction.EnumCases[2].Discriminant == 2
                && direction.EnumCases[3].Discriminant == 3
                && direction.EnumCases[0].Case.Canonical == "Direction.North()");
            TestHarness.CheckTrue("enum 不挂 init 分发器槽 0",
                direction.VTableSlots.Count == 0);

            // 引用字段：16B 槽 16B 对齐、refMap 跳数（槽粒度）
            var link = layout.Find("Link");
            TestHarness.CheckTrue("Link 字段偏移",
                FieldOf(link!, "#a@")!.Offset == 16
                && FieldOf(link!, "#p@")!.Offset == 32
                && FieldOf(link!, "#b@")!.Offset == 48
                && FieldOf(link!, "#q@")!.Offset == 64);
            TestHarness.CheckTrue("Link 尺寸", link!.Size == 80);
            TestHarness.CheckTrue("Link refMap 跳数",
                link.RefMap.Length == 2 && link.RefMap[0] == 1 && link.RefMap[1] == 1);

            // rich struct：refMap 自身携带（值类型扫描起点 0）
            var handle = layout.Find("Handle");
            TestHarness.CheckTrue("Handle rich 标记",
                handle != null && (handle.TypeFlags
                    & RigiCompiler.Middleware.Layout.TypeLayoutPlan.FlagRich) != 0);
            TestHarness.CheckTrue("Handle refMap",
                handle!.RefMap.Length == 1 && handle.RefMap[0] == 0);

            // 内嵌 rich 值类型字段：引用按偏移折算拼入外层 refMap
            var holder = layout.Find("Holder");
            TestHarness.CheckTrue("Holder 字段偏移",
                FieldOf(holder!, "#a@")!.Offset == 16
                && FieldOf(holder!, "#h@")!.Offset == 32);
            TestHarness.CheckTrue("Holder 尺寸", holder!.Size == 48);
            TestHarness.CheckTrue("Holder refMap（rich 内嵌折算）",
                holder.RefMap.Length == 1 && holder.RefMap[0] == 1);
        }

        private static void TestTypeSheetEmission()
        {
            // 真实前端路径 + 全管线（MirBuild + Layout）：Node 的
            // TypeSheet/vtable/refMap 全局锚点
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub class Node {\n" +
                "    pub var value: i32\n" +
                "    pub var next: Node?\n" +
                "    pub init(v: i32) { value = v }\n" +
                "    pub func get(): i32 { return value }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const optional: Node? = null\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "sheet.bil");
            TestHarness.CheckTrue("TypeSheet 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            TestHarness.CheckTrue("管线挂载布局", context.Layout != null);
            using var llvmLease2656 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();

            // §6 结构：typeInfoId 指向 TypeInfo、默认基类 Object、typeSize 48
            //（头 16 + i32@16 + 引用槽@32）、vTableSize 2（槽 0 分发器 + get）
            TestHarness.CheckTrue("TypeSheet 全局锚点",
                ll.Contains("@typesheet.Node = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } " +
                    "{ ptr @typeinfo.Node, ptr @\"typesheet.core::Object\", i32 48, i32 0, i32 2, ptr @typesheet.vtable.Node, " +
                    "i32 0, ptr null, i32 1, ptr @typesheet.refmap.Node }"), ll);
            TestHarness.CheckTrue("TypeInfo 回指 sheet",
                ll.Contains("@typeinfo.Node =") && ll.Contains("ptr @typesheet.Node"), ll);
            TestHarness.CheckTrue("TypeInfo typeInfoId 非 null",
                !ll.Contains("@typesheet.Node = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } " +
                    "{ ptr null,"), ll);
            TestHarness.CheckTrue("Nullable 具化 sheet 保留元素信息",
                ll.Contains("typesheet.core::Nullable<Node>")
                && ll.Split('\n').Any(line => line.StartsWith("@\"typeinfo.core::Nullable<Node>\" =")
                    && line.EndsWith("ptr @typesheet.Node, ptr null, ptr null }")), ll);
            TestHarness.CheckTrue("TypeInfo 元素字段不改变旧字段位置",
                TypeSheetAbi.InfoFieldNullableElement == 6
                && TypeSheetAbi.InfoFieldTypeIdBound == 7
                && TypeSheetAbi.InfoFieldNativeDestructor == 8 && TypeSheetAbi.InfoFieldCount == 9);
            // 槽 0 = 分发器；get 不可达仍为 null
            TestHarness.CheckTrue("vtable 全局锚点",
                ll.Contains("@typesheet.vtable.Node = internal constant [2 x ptr] " +
                    "[ptr @mw.init.dispatch.Node, ptr null]"), ll);
            // refMap：next 槽@32 → 跳数 (32-16)/16 = 1（kind0）
            TestHarness.CheckTrue("refMap 全局锚点",
                ll.Contains("@typesheet.refmap.Node = internal constant [1 x i16] [i16 1]"), ll);
            TestHarness.CheckTrue("String 内建 sheet 含 kind1 refMap",
                ll.Contains("@\"typesheet.refmap.core::String\" = internal constant [1 x i16] [i16 16384]"), ll);
            TestHarness.CheckTrue("String 内建 sheet flags = INLINE|STRING",
                ll.Contains("i32 16, i32 40") && ll.Contains("typesheet.core::String"), ll);
            TestHarness.CheckTrue("Array 内建 sheet 前缀 32 + FlagArray",
                ll.Contains("i32 32, i32 16") && ll.Contains("typesheet.core::Array"), ll);
        }

        // ===== is/supers/with + TypeInfo（MW5 c3）=====

        private static void TestTypeCheckEmission()
        {
            var (_, textModule, text) = BilTestHarness.EmitBilUnit(
                "pub open class Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub class Dog : Animal {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub interface Named {\n" +
                "    func name(): String\n" +
                "}\n" +
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Mark {\n" +
                "    pub init()\n" +
                "}\n" +
                "@Mark\n" +
                "pub class Tagged implements Named {\n" +
                "    pub init() { }\n" +
                "    pub override func name(): String { return \"t\" }\n" +
                "}\n" +
                "pub func isDog(a: Animal): bool { return a is Dog }\n" +
                "pub func isNamed(t: Tagged): bool { return t is Named }\n" +
                "pub func supersDog(a: Animal): bool { return a supers Dog }\n" +
                "pub func withMark(t: Tagged): bool { return t with Mark }\n" +
                "pub func isIndirect\\<T>(a: Animal): bool { return a is T }\n" +
                "pub func main(): i32 {\n" +
                "    var d: Animal = new Dog()\n" +
                "    var t = new Tagged()\n" +
                "    if (isDog(d)) { }\n" +
                "    if (isNamed(t)) { }\n" +
                "    if (supersDog(d)) { }\n" +
                "    if (withMark(t)) { }\n" +
                "    if (isIndirect\\<Dog>(d)) { }\n" +
                "    return 0\n" +
                "}\n");
            text = BilWriter.Write(textModule);
            var gate = BilGate.Accept(text, "typeck.bil");
            TestHarness.CheckTrue("type.check 用例门禁放行", gate.IsAccepted,
                string.Join("; ", gate.Errors));
            var context = new MwContext(gate.Module!);
            RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(context);
            var allInsts = context.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            TestHarness.CheckTrue("MIR 含 type.is",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Is && !c.IsIndirect));
            TestHarness.CheckTrue("MIR 含 type.supers",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Supers && !c.IsIndirect));
            TestHarness.CheckTrue("MIR 含 type.with",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.With && !c.IsIndirect));
            TestHarness.CheckTrue("MIR 含 type.is.indirect",
                allInsts.OfType<MirTypeCheck>().Any(c =>
                    c.Kind == MirTypeCheckKind.Is && c.IsIndirect));
            var taggedPlan = context.Layout!.Find("Tagged");
            TestHarness.CheckTrue("Tagged Entity 隐藏存储 §5.3",
                taggedPlan != null && taggedPlan.Fields.Any(f =>
                    f.Symbol.Contains("#.wrapper.") && f.Symbol.Contains("Mark@")));
            var markPlan = context.Layout.Find("Mark");
            TestHarness.CheckTrue("Mark wrapper 为内联实例布局",
                markPlan != null
                && markPlan.Kind == RigiCompiler.Middleware.Layout.TypeLayoutKind.Wrapper);
            TestHarness.CheckTrue("纯标记 wrapper 不含 self 存储与 refMap",
                markPlan is { Size: 0, RefMapCount: 0 } && markPlan.Fields.Count == 0);
            TestHarness.CheckTrue("纯标记不扩张宿主对象头",
                taggedPlan?.Size == LayoutEngine.ObjectHeaderSize);

            using var llvmLease2764 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(context, context.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue("TypeInfo 全局名字符串",
                ll.Contains("@typeinfo.Tagged") && ll.Contains("ptr @typesheet.Tagged"), ll);
            TestHarness.CheckTrue("TypeInfo wrappers 数组",
                ll.Contains("typeinfo.wrappers.Tagged") && ll.Contains("@typesheet.Mark"), ll);
            TestHarness.CheckTrue("TypeInfo ifaceClosure",
                ll.Contains("typeinfo.ifaces.Tagged") && ll.Contains("@typesheet.Named"), ll);
            TestHarness.CheckTrue("TypeSheet typeInfoId 非 null",
                ll.Contains("ptr @typeinfo.Tagged") && !ll.Contains(
                    "@typesheet.Tagged = internal constant { ptr, ptr, i32, i32, i32, ptr, i32, ptr, i32, ptr } { ptr null,"),
                ll);
            TestHarness.CheckTrue("helper type.is",
                ll.Contains("call i32 @rigi_type_is("), ll);
            TestHarness.CheckTrue("helper type.supers",
                ll.Contains("call i32 @rigi_type_supers("), ll);
            TestHarness.CheckTrue("helper type.with",
                ll.Contains("call i32 @rigi_type_with("), ll);
            TestHarness.CheckTrue("helper type.is.indirect",
                ll.Contains("call i32 @rigi_type_is_indirect("), ll);
        }

    }
}
