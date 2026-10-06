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
        // TerminalCases 职责；与主文件共享同一类型、字段及生命周期。

        // ===== 异常指令发射（MW9a 第 C 棒，.ll 黄金锚点）=====

        private static void TestExceptionEmission()
        {
            var ctx = PipelineFromSource(
                "class MyError : core.RuntimeException {\n" +
                "    pub init(text: String) { message = text }\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "pub func fail(): i32 {\n" +
                "    throw new MyError(\"boom\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    try { return fail() } catch (e: core.RuntimeException) { return 1 }\n" +
                "}\n",
                "exc.emit.bil");
            using var llvmLease7440 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();

            CaseAssertions.CheckTrue("MirTakePending → rigi_exc_take",
                ll.Contains("call ptr @rigi_exc_take()"), ll);
            CaseAssertions.CheckTrue("MirThrow → rigi_exc_raise",
                ll.Contains("call void @rigi_exc_raise(ptr"), ll);
            CaseAssertions.CheckTrue("MirRetThrow → ret undef（值返回）",
                ll.Contains("ret i32 undef"), ll);
            CaseAssertions.CheckTrue("调用后 pending 检查",
                ll.Contains("call ptr @rigi_exc_pending()"), ll);
            CaseAssertions.CheckTrue("异常边跳传播垫",
                ll.Contains("label %mw.propagate"), ll);
            CaseAssertions.CheckTrue("正常边落内联继续块",
                ll.Contains("exc.cont"), ll);
            CaseAssertions.CheckTrue("派发垫胖引用落槽",
                ll.Contains("store { i64, i64 }"), ll);
            CaseAssertions.CheckTrue("reporter 块已发射",
                ll.Contains("entry.uncaught:"), ll);
            CaseAssertions.CheckTrue("reporter 取实际类型全名",
                ll.Contains("@rigi_type_name_of("), ll);
            CaseAssertions.CheckTrue("reporter 虚派发 getMessage",
                ll.Contains("call ptr @rigi_vtable_entry(ptr"), ll);
            CaseAssertions.CheckTrue("reporter 打印 stderr",
                ll.Contains("call void @rigi_print_err(ptr"), ll);
            CaseAssertions.CheckTrue("reporter 释放异常胖引用",
                ll.Contains("call void @rigi_ref_release("), ll);
            CaseAssertions.CheckTrue("reporter 出口 rigi_exc_halt",
                ll.Contains("call void @rigi_exc_halt()"), ll);
            // MW11c 棒5a：rigi_entry 固定序列锚点（对齐 VM BilVm.Run：
            // singletons → globals.init → main →（协程运行时段收编时）
            // Dispatcher workerLoop drain → 失败汇总（main 失败 >
            // 未观察失败——native 注册表）→ ret；无协程程序同样发射
            // workerLoop 段（quiescent 先检直返，零挂起））
            var stubStart = ll.IndexOf("define i32 @rigi_entry(i32 %0, ptr %1)", StringComparison.Ordinal);
            var stubLl = stubStart >= 0 ? ll.Substring(stubStart) : "";
            var firstCall = stubLl.IndexOf("call ", StringComparison.Ordinal);
            CaseAssertions.CheckTrue("rigi_entry 固定序列：singleton 急切初始化最先（stub 首调用）",
                firstCall >= 0 && stubLl.IndexOf("mw.singleton.get",
                    StringComparison.Ordinal) >= 0
                && stubLl.IndexOf("mw.singleton.get", StringComparison.Ordinal)
                    < stubLl.IndexOf("entry.mainexc:", StringComparison.Ordinal), ll);
            CaseAssertions.CheckTrue("rigi_entry 固定序列：workerLoop drain + 主 Worker 收尾",
                ll.Contains("$workerLoop(")
                && ll.Contains("call void @rigi_main_worker_shutdown()"), ll);
            CaseAssertions.CheckTrue("rigi_entry 固定序列：main 失败 take 进合成槽",
                ll.Contains("entry.mainexc:") && ll.Contains("entry.take:")
                && ll.Contains("entry.drain:"), ll);
            CaseAssertions.CheckTrue("rigi_entry 固定序列：未观察失败查询（native 注册表）",
                ll.Contains("call i32 @rigi_failure_take_unobserved(ptr")
                && ll.Contains("entry.unobserved:"), ll);
        }

        // MW7a 边界：enum String payload / 嵌套 rich 折算序 / 非 rich 含 String 也产 refMap
        private static void TestRefMapMw7a()
        {
            foreach (var kind in new[] { TypeLayout.RefMapKindFatRef, TypeLayout.RefMapKindString })
            {
                var maximum = TypeLayout.EncodeRefMap(kind, 0x3fff);
                CaseAssertions.CheckTrue("refMap 最大 hop 保留 kind 与全部低 14 位",
                    TypeLayout.RefMapKindOf(maximum) == kind && TypeLayout.RefMapHopOf(maximum) == 0x3fff);
                var accepted = RefMapBuilder.BuildRefMap(Array.Empty<FieldPlan>(),
                    new() { new(0x3fff * 16, kind, null) }, 0);
                CaseAssertions.CheckTrue("refMap 按 16 字节单位接受边界布局", accepted.Single() == maximum);
                foreach (var invalidHop in new[] { -1, 0x4000, 0xffff })
                {
                    var rejected = false;
                    try { TypeLayout.EncodeRefMap(kind, invalidHop); }
                    catch (CompilerInternalException) { rejected = true; }
                    CaseAssertions.CheckTrue("refMap 拒绝越界 hop " + invalidHop, rejected);
                }
                var overflowRejected = false;
                try { RefMapBuilder.BuildRefMap(Array.Empty<FieldPlan>(),
                    new() { new(0x4000 * 16, kind, null) }, 0); }
                catch (CompilerInternalException) { overflowRejected = true; }
                CaseAssertions.CheckTrue("refMap 构建拒绝越界布局而不污染 kind", overflowRejected);
            }
            var layout = BuildLayout(
                "pub class Node {\n" +
                "    pub var x: i32 = 0\n" +
                "}\n" +
                "pub enum struct Note {\n" +
                "    pub const text: String\n" +
                "    pub init(_ -> text)\n" +
                "}[\n" +
                "    A(\"a\"),\n" +
                "    B(text = _)\n" +
                "]\n" +
                "pub rich struct Inner {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node?\n" +
                "}\n" +
                "pub rich struct Outer {\n" +
                "    pub var kid: Inner\n" +
                "    pub var tag: String\n" +
                "}\n" +
                "pub struct Tagged {\n" +
                "    pub var n: i32\n" +
                "    pub var label: String\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");

            var note = layout.Find("Note");
            CaseAssertions.CheckTrue("enum String payload 产 kind1",
                note != null
                && note.RefMap.Length == 1
                && FieldOf(note, "#text@")!.Offset == 16
                && note.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 1),
                note == null ? "missing" : string.Join(",", note.RefMap));

            var inner = layout.Find("Inner");
            var outer = layout.Find("Outer");
            CaseAssertions.CheckTrue("嵌套 rich 外层 refMap 序（kind1,kind0,kind1）",
                inner != null && outer != null
                && inner.RefMap.Length == 2
                && inner.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0)
                && inner.RefMap[1] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindFatRef, 0)
                && outer.RefMap.Length == 3
                && outer.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0)
                && outer.RefMap[1] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindFatRef, 0)
                && outer.RefMap[2] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 0),
                outer == null ? "missing" : string.Join(",", outer.RefMap));

            var tagged = layout.Find("Tagged");
            CaseAssertions.CheckTrue("非 rich 含 String 也产 refMap kind1",
                tagged != null
                && (tagged.TypeFlags & TypeLayoutPlan.FlagRich) == 0
                && tagged.RefMap.Length == 1
                && FieldOf(tagged, "#label@")!.IsStringSlot
                && FieldOf(tagged, "#label@")!.Offset == 16
                && tagged.RefMap[0] == TypeLayout.EncodeRefMap(TypeLayout.RefMapKindString, 1),
                tagged == null ? "missing" : string.Join(",", tagged.RefMap));
        }

        // ===== 动态 new（MW8b）=====

        private static void TestDynamicNew()
        {
            var ctx = PipelineFromSource(
                "pub class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init() { x = 0 }\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func make\\<T extends Point>(): T { return T() }\n" +
                "pub func makeV\\<T extends Point>(v: i32): T { return T(v) }\n" +
                "pub func fromType(tid: Type\\<Point>, v: i32): Point { return new tid(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = make\\<Point>()\n" +
                "    var b = makeV\\<Point>(7)\n" +
                "    var c = fromType(typeOf(Point), 3)\n" +
                "    return ((a.x + b.x) + c.x)\n" +
                "}\n",
                "dynnew.bil");
            var allInsts = ctx.Mir!.Functions.SelectMany(f => f.Blocks)
                .SelectMany(b => b.Instructions).ToList();
            CaseAssertions.CheckTrue("MIR 含 new.indirect",
                allInsts.OfType<MirNewIndirect>().Any());

            using var llvmLease7599 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();
            CaseAssertions.CheckTrue("槽 0 为 init 分发器",
                ll.Contains("@typesheet.vtable.Point = internal constant")
                && ll.Contains("ptr @mw.init.dispatch.Point"), ll);
            CaseAssertions.CheckTrue("分发器 if 链比 argc",
                ll.Contains("define internal ptr @mw.init.dispatch.Point(ptr")
                && ll.Contains("icmp eq i32"), ll);
            CaseAssertions.CheckTrue("ctor thunk 序列 alloc",
                ll.Contains("define internal { i64, i64 } @\"mw.init.ctor.Point#")
                && ll.Contains("call ptr @rigi_alloc(ptr @typesheet.Point)"), ll);
            CaseAssertions.CheckTrue("无匹配 init 抛 NoSuchMethodException",
                ll.Contains("call void @rigi_exc_raise(ptr")
                && ll.Contains("NoSuchMethodException$init(typeName:"), ll);
            CaseAssertions.CheckTrue("调用点读 vTable 字段",
                ll.Contains("getelementptr") && ll.Contains("dynnew"), ll);

            var pairCtx = PipelineFromSource(
                "pub func main(): i32 {\n" +
                "    var sample = new Pair\\<String, i32>(\"a\", 1)\n" +
                "    var t = typeOf(sample)\n" +
                "    var p = new t(\"b\", 2)\n" +
                "    return p.value\n" +
                "}\n",
                "dynnew-pair.bil");
            CaseAssertions.CheckTrue("Pair typeOf 值 MIR 含 new.indirect",
                pairCtx.Mir!.Functions.SelectMany(f => f.Blocks)
                    .SelectMany(b => b.Instructions).OfType<MirNewIndirect>().Any());

            var exnCtx = PipelineFromSource(
                "pub func main(): i32 {\n" +
                "    var sample = new RuntimeException(\"x\")\n" +
                "    var t = typeOf(sample)\n" +
                "    var e = new t(\"hello\")\n" +
                "    if (e.getMessage() == \"hello\") { return 1 }\n" +
                "    return 0\n" +
                "}\n",
                "dynnew-exn.bil");
            CaseAssertions.CheckTrue("RuntimeException typeOf 值 MIR 含 new.indirect",
                exnCtx.Mir!.Functions.SelectMany(f => f.Blocks)
                    .SelectMany(b => b.Instructions).OfType<MirNewIndirect>().Any());

            var structCtx = PipelineFromSource(
                "pub struct Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func fromType(tid: Type\\<Vec>, v: i32): Vec { return new tid(v) }\n" +
                "pub func main(): i32 {\n" +
                "    var s = fromType(typeOf(Vec), 9)\n" +
                "    return s.x\n" +
                "}\n",
                "dynnew-struct.bil");
            using var llvmLease7652 = LlvmHost.Enter();
            using var structMod = ModuleBuilder.Build(structCtx, structCtx.Mir!);
            var structLl = structMod.PrintToString();
            CaseAssertions.CheckTrue("struct 槽 0 为 init 分发器",
                structLl.Contains("@typesheet.vtable.Vec = internal constant")
                && structLl.Contains("ptr @mw.init.dispatch.Vec"), structLl);
            CaseAssertions.CheckTrue("struct ctor thunk 为 sret void",
                structLl.Contains("define internal void @\"mw.init.ctor.Vec#")
                && structLl.Contains("call void @llvm.memset.p0.i64"), structLl);
        }

    }
}
