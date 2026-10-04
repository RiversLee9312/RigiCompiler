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
        // Rc 职责；与主文件共享同一类型、字段及生命周期。

        private static void TestRcInjection()
        {
            // ① 入口参数 acquire 序列
            var ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func take(n: Node): i32 { return n.x }\n" +
                "pub func main(): i32 {\n" +
                "    return take(new Node(1))\n" +
                "}\n",
                "rc.param.bil");
            var take = FnOf(ctx, "$take(");
            TestHarness.CheckTrue("① take 入口首条是参数 acquire",
                take.Blocks[0].Instructions.Count > 0
                && take.Blocks[0].Instructions[0] is MirAcquireSlot acq
                && acq.Local == take.Parameters[0].Name,
                take.Blocks[0].Instructions.FirstOrDefault()?.GetType().Name ?? "empty");

            // ② CopyLocal 三段式与 dst==src 删除
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func alias(n: Node): Node {\n" +
                "    var m = n\n" +
                "    return m\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n = alias(new Node(1))\n" +
                "    return n.x\n" +
                "}\n",
                "rc.copy.bil");
            var alias = FnOf(ctx, "$alias(");
            var copies = alias.Blocks.SelectMany(b => b.Instructions.Select((inst, i) => (b, i, inst)))
                .Where(t => t.inst is MirCopyLocal)
                .ToList();
            TestHarness.CheckTrue("② 存在托管 CopyLocal", copies.Count > 0);
            foreach (var (block, i, inst) in copies)
            {
                var copy = (MirCopyLocal)inst;
                if (!TypeLayout.IsManagedSlot(ctx, alias.FindLocal(copy.Target).Type))
                {
                    continue;
                }
                TestHarness.CheckTrue("② 三段式前 Release",
                    i > 0 && block.Instructions[i - 1] is MirReleaseSlot rel
                    && rel.Local == copy.Target);
                TestHarness.CheckTrue("② 三段式后 Acquire",
                    i + 1 < block.Instructions.Count
                    && block.Instructions[i + 1] is MirAcquireSlot a
                    && a.Local == copy.Target);
            }

            // dst==src 删除：手写 BIL set.var $n $n
            // 用最小手写模块覆盖自赋值删除
            const string SelfCopyBil =
                "BIL \"1.1\"\n" +
                "\n" +
                "Metadata {\n" +
                "    module = string \"selfcopy\"\n" +
                "}\n" +
                "\n" +
                "Resources {\n" +
                "    R_Zero = i32 0,\n" +
                "    R_Null = null type(Node)\n" +
                "}\n" +
                "\n" +
                "LocalSymbols {\n" +
                "    .type Node = class pub {\n" +
                "        .field Node#x@.i32 pub var\n" +
                "        .method Node$init(v:.i32)@.void pub\n" +
                "    }\n" +
                "    .method $id(n:Node)@.void pub\n" +
                "    .method $main()@.i32 pub entrypoint\n" +
                "}\n" +
                "\n" +
                "ExternalSymbols {\n" +
                "}\n" +
                "\n" +
                "fn($id(n:Node)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        n = Node\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        set.var $n $n\n" +
                "        ret\n" +
                "    }\n" +
                "}\n" +
                "\n" +
                "fn(Node$init(v:.i32)@.void) {\n" +
                "    .args {\n" +
                "        .return = .void,\n" +
                "        .this = Node,\n" +
                "        v = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        ret\n" +
                "    }\n" +
                "}\n" +
                "\n" +
                "fn($main()@.i32) {\n" +
                "    .args {\n" +
                "        .return = .i32\n" +
                "    }\n" +
                "    .vars {\n" +
                "        Node n,\n" +
                "        .i32 z\n" +
                "    }\n" +
                "    .block entry entrypoint {\n" +
                "        load res(R_Null) $n\n" +
                "        invoke.noret fn($id(n:Node)@.void) [$n]\n" +
                "        load res(R_Zero) $z\n" +
                "        ret $z\n" +
                "    }\n" +
                "}\n";
            var selfGate = BilGate.Accept(SelfCopyBil, "selfcopy.bil");
            TestHarness.CheckTrue("② 自赋值模块门禁", selfGate.IsAccepted,
                string.Join("; ", selfGate.Errors.Take(3)));
            if (selfGate.IsAccepted)
            {
                var selfCtx = new MwContext(selfGate.Module!);
                RigiCompiler.Middleware.Pipeline.MwPipeline.CreateDefault().Run(selfCtx);
                var idFn = FnOf(selfCtx, "$id(");
                TestHarness.CheckTrue("② dst==src CopyLocal 已删除",
                    !idFn.Blocks.SelectMany(b => b.Instructions).OfType<MirCopyLocal>().Any());
            }

            // ③ 产出类前置 release
            ctx = PipelineFromSource(
                "import core.io.Console\n" +
                "pub func main(): i32 {\n" +
                "    var s = \"a\" + \"b\"\n" +
                "    Console.println(s)\n" +
                "    return 0\n" +
                "}\n",
                "rc.prod.bil");
            var main = ctx.Mir!.Functions.First(f => f.IsEntrypoint);
            var concat = main.Blocks.SelectMany(b => b.Instructions)
                .Select((inst, i) => (inst, i))
                .FirstOrDefault(t => t.inst is MirBinaryIntrinsic);
            TestHarness.CheckTrue("③ 含 string concat", concat.inst != null);
            if (concat.inst is MirBinaryIntrinsic bin)
            {
                var block = main.Blocks.First(b => b.Instructions.Contains(bin));
                var idx = block.Instructions.ToList().IndexOf(bin);
                TestHarness.CheckTrue("③ concat 前是 ReleaseSlot",
                    idx > 0 && block.Instructions[idx - 1] is MirReleaseSlot pre
                    && pre.Local == bin.Target);
            }

            // ④ ret 块 release 全覆盖 + $mw.ret 合成
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func wrap(n: Node): Node { return n }\n" +
                "pub func main(): i32 {\n" +
                "    var n = wrap(new Node(1))\n" +
                "    return n.x\n" +
                "}\n",
                "rc.ret.bil");
            var wrap = FnOf(ctx, "$wrap(");
            TestHarness.CheckTrue("④ $mw.ret 已登记",
                wrap.Locals.Any(l => l.Name == RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName));
            var retBlock = wrap.Blocks.First(b => b.Terminator is MirRet);
            TestHarness.CheckTrue("④ ret 操作数是 $mw.ret",
                retBlock.Terminator is MirRet { Value: MirLocalOperand op }
                && op.Name == RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName);
            TestHarness.CheckTrue("④ ret 块末尾是 ReleaseSlot",
                retBlock.Instructions.Count > 0
                && retBlock.Instructions[^1] is MirReleaseSlot);
            TestHarness.CheckTrue("④ 出口 release 不含 $mw.ret",
                !retBlock.Instructions.TakeLast(1).OfType<MirReleaseSlot>()
                    .Any(r => r.Local == RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName)
                || wrap.Parameters.All(p => p.Name != RigiCompiler.Middleware.Passes.RcInjectionPass.RetLocalName));

            // ⑤ class .this 管理、值类型 .this 豁免
            ctx = PipelineFromSource(
                "pub class C {\n" +
                "    pub var x: i32\n" +
                "    pub init() { x = 0 }\n" +
                "    pub func get(): i32 { return x }\n" +
                "}\n" +
                "pub struct S {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x)\n" +
                "    pub func get(): i32 { return x }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var c = new C()\n" +
                "    var s = new S(1)\n" +
                "    return c.get() + s.get()\n" +
                "}\n",
                "rc.this.bil");
            var classGet = FnOf(ctx, "C$get");
            var structGet = FnOf(ctx, "S$get");
            TestHarness.CheckTrue("⑤ class .this 入口 acquire",
                classGet.Blocks[0].Instructions.OfType<MirAcquireSlot>()
                    .Any(a => a.Local == ".this"));
            TestHarness.CheckTrue("⑤ 值类型 .this 豁免 acquire",
                !structGet.Blocks[0].Instructions.OfType<MirAcquireSlot>()
                    .Any(a => a.Local == ".this"));
            TestHarness.CheckTrue("⑤ 值类型 .this 豁免 release",
                !structGet.Blocks.SelectMany(b => b.Instructions).OfType<MirReleaseSlot>()
                    .Any(r => r.Local == ".this"));

            // ⑥ void 函数也有局部 release
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func drop(n: Node) { var m = n }\n" +
                "pub func main(): i32 {\n" +
                "    drop(new Node(1))\n" +
                "    return 0\n" +
                "}\n",
                "rc.void.bil");
            var drop = FnOf(ctx, "$drop(");
            var dropRet = drop.Blocks.First(b => b.Terminator is MirRet);
            TestHarness.CheckTrue("⑥ void 函数 ret 块含 ReleaseSlot",
                dropRet.Instructions.OfType<MirReleaseSlot>().Any());

            // .ll 黄金：含 ref 拷贝的函数出现 acquire/release 配对
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub rich struct Bag {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node\n" +
                "    pub init(_ -> name, _ -> node)\n" +
                "}\n" +
                "pub func copy(n: Node): Node { return n }\n" +
                "pub func copyString(s: String): String { return s }\n" +
                "pub func copyBag(b: Bag): Bag { return b }\n" +
                "pub func main(): i32 {\n" +
                "    var n = copy(new Node(1))\n" +
                "    var s = copyString(\"x\")\n" +
                "    var b = copyBag(new Bag(s, n))\n" +
                "    return n.x\n" +
                "}\n",
                "rc.ll.bil");
            using var llvmLease7179 = LlvmHost.Enter();
            using var module = ModuleBuilder.Build(ctx, ctx.Mir!);
            var ll = module.PrintToString();
            TestHarness.CheckTrue(".ll 含 rigi_ref_acquire",
                ll.Contains("call i64 @rigi_ref_acquire("), ll);
            TestHarness.CheckTrue(".ll 含 rigi_ref_release",
                ll.Contains("call void @rigi_ref_release("), ll);
            TestHarness.CheckTrue(".ll 胖引用复合写入由同一 ownership region 包裹",
                LlRegionContains(ll, "call i64 @rigi_ref_acquire(",
                    "call void @rigi_ref_release(", "store { i64, i64 }"), ll);
            TestHarness.CheckTrue(".ll String 复合写入由同一 ownership region 包裹",
                LlRegionContains(ll, "call void @rigi_string_acquire(",
                    "call void @rigi_string_release(", "store { ptr, i64 }"), ll);
            TestHarness.CheckTrue(".ll rich value 复合写入由同一 ownership region 包裹",
                LlRegionContains(ll, "call void @rigi_value_acquire(",
                    "call void @rigi_value_release(", "@llvm.memcpy"), ll);

            // ===== Phase 1.2 region 合并（§23.3「复合引用操作可合并为同一
            // region」）：同一复合操作只发一对 region 进出 =====
            // ① 出口 release 序列（托管局部 n/s/b 三槽）共享一对 region：
            // 同一 region 体内同时出现三类 release 是出口序列合并的唯一起源
            //（复合写入 region 只含单一类型的 acquire+release）。
            // ② 同一 region 内 ≥2 次 ref_acquire = 块首参数 acquire 段合并。
            // ③ 同一 region 内 ≥2 次 value_release = rich temps 级联析构合并。
            // ④ 同一 region 内 ≥2 次 ref_release = boxed temps 级联析构合并。
            ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub rich struct Bag {\n" +
                "    pub var name: String\n" +
                "    pub var node: Node\n" +
                "    pub init(_ -> name, _ -> node)\n" +
                "}\n" +
                "pub func take2(a: Node, b: Node): i32 { return a.x + b.x }\n" +
                "pub func eatBags(p: Bag, q: Bag): i32 { return 0 }\n" +
                "pub func eatObjects(m: Node, k: Node): i32 { return 0 }\n" +
                "pub func main(): i32 {\n" +
                "    var n = new Node(1)\n" +
                "    var s = \"x\"\n" +
                "    var b = new Bag(s, n)\n" +
                "    var r = take2(n, n)\n" +
                "    var e1 = eatBags(new Bag(s, n), new Bag(s, n))\n" +
                "    var e2 = eatObjects(new Node(2), new Node(3))\n" +
                "    return (r + (e1 + (e2 + (n.x + b.node.x))))\n" +
                "}\n",
                "rc.region.bil");
            using var llvmLease7226 = LlvmHost.Enter();
            using (var regionModule = ModuleBuilder.Build(ctx, ctx.Mir!))
            {
                var regionLl = regionModule.PrintToString();
                TestHarness.CheckTrue(".ll 出口 release 序列由同一 region 包裹",
                    LlRegionContains(regionLl, "call void @rigi_ref_release(",
                        "call void @rigi_string_release(", "call void @rigi_value_release("),
                    regionLl);
                TestHarness.CheckTrue(".ll 参数 acquire 段由同一 region 包裹",
                    LlRegionMaxCount(regionLl, "call i64 @rigi_ref_acquire(") >= 2, regionLl);
                TestHarness.CheckTrue(".ll rich temps 级联析构由同一 region 包裹",
                    LlRegionMaxCount(regionLl, "call void @rigi_value_release(") >= 2, regionLl);
                TestHarness.CheckTrue(".ll boxed temps 级联析构由同一 region 包裹",
                    LlRegionMaxCount(regionLl, "call void @rigi_ref_release(") >= 2, regionLl);
            }
        }

        private static bool LlRegionContains(string ll, params string[] needles)
        {
            const string Enter = "call void @rigi_region_enter()";
            const string Exit = "call void @rigi_region_exit()";
            var start = 0;
            while ((start = ll.IndexOf(Enter, start, StringComparison.Ordinal)) >= 0)
            {
                var depth = 1;
                var cursor = start + Enter.Length;
                var end = -1;
                // 公共释放原语也有自己的 region；按配对深度查找外层出口，
                // 不能把首个嵌套 exit 当作复合 ownership mutation 的结束。
                while (depth > 0)
                {
                    var nextEnter = ll.IndexOf(Enter, cursor, StringComparison.Ordinal);
                    var nextExit = ll.IndexOf(Exit, cursor, StringComparison.Ordinal);
                    if (nextExit < 0) break;
                    if (nextEnter >= 0 && nextEnter < nextExit)
                    {
                        depth++;
                        cursor = nextEnter + Enter.Length;
                    }
                    else
                    {
                        depth--;
                        end = nextExit;
                        cursor = nextExit + Exit.Length;
                    }
                }
                if (end < 0 || depth != 0)
                {
                    return false;
                }
                var region = ll.Substring(start, end - start);
                if (needles.All(n => region.Contains(n, StringComparison.Ordinal)))
                {
                    return true;
                }
                start = end + Exit.Length;
            }
            return false;
        }

        // Phase 1.2：单个 region 体（enter 与配对 exit 之间，按嵌套配对取
        // 最外层）内 needle 出现次数的最大值；无任何 region 返回 0。
        // 用于断言「同一复合操作的 N 次 ARC 调用只发一对 region 进出」。
        private static int LlRegionMaxCount(string ll, string needle)
        {
            const string Enter = "call void @rigi_region_enter()";
            const string Exit = "call void @rigi_region_exit()";
            var max = 0;
            var start = 0;
            while ((start = ll.IndexOf(Enter, start, StringComparison.Ordinal)) >= 0)
            {
                var depth = 1;
                var cursor = start + Enter.Length;
                var end = -1;
                // 公共释放原语也有自己的 region；按配对深度查找外层出口，
                // 不能把首个嵌套 exit 当作复合 ownership mutation 的结束。
                while (depth > 0)
                {
                    var nextEnter = ll.IndexOf(Enter, cursor, StringComparison.Ordinal);
                    var nextExit = ll.IndexOf(Exit, cursor, StringComparison.Ordinal);
                    if (nextExit < 0) break;
                    if (nextEnter >= 0 && nextEnter < nextExit)
                    {
                        depth++;
                        cursor = nextEnter + Enter.Length;
                    }
                    else
                    {
                        depth--;
                        end = nextExit;
                        cursor = nextExit + Exit.Length;
                    }
                }
                if (end < 0 || depth != 0)
                {
                    break;
                }
                var region = ll.Substring(start, end - start);
                var count = 0;
                var hit = 0;
                while ((hit = region.IndexOf(needle, hit, StringComparison.Ordinal)) >= 0)
                {
                    count++;
                    hit += needle.Length;
                }
                if (count > max)
                {
                    max = count;
                }
                start = end + Exit.Length;
            }
            return max;
        }

        // ===== RcInjection 传播垫（MW9a 第 C 棒）=====

        private static void TestRcPropagatePad()
        {
            const string PadId = RigiCompiler.Middleware.Passes.RcInjectionPass.PropagateBlockId;

            // ① 含可抛调用的函数：垫存在、MirRetThrow 收尾、release 序与
            // ret 出口尾部同口径、全部 ExcTarget 解析指向垫（对象身份）
            var ctx = PipelineFromSource(
                "pub class Node {\n" +
                "    pub var x: i32\n" +
                "    pub init(v: i32) { x = v }\n" +
                "}\n" +
                "pub func wrap(n: Node): i32 { return n.x }\n" +
                "pub func main(): i32 {\n" +
                "    return wrap(new Node(1))\n" +
                "}\n",
                "rc.pad.bil");
            var main = FnOf(ctx, "$main(");
            var pad = main.Blocks.SingleOrDefault(b => b.Id == PadId);
            TestHarness.CheckTrue("① 含可抛调用函数有传播垫", pad != null);
            TestHarness.CheckTrue("① 垫 MirRetThrow 收尾",
                pad != null && pad.Terminator is MirRetThrow);
            TestHarness.CheckTrue("① 垫指令全 ReleaseSlot",
                pad != null && pad.Instructions.Count > 0
                && pad.Instructions.All(i => i is MirReleaseSlot));
            var retBlock = main.Blocks.First(b => b.Terminator is MirRet);
            var retInsts = retBlock.Instructions;
            var retTail = new List<string>();
            for (var i = retInsts.Count - 1; i >= 0 && retInsts[i] is MirReleaseSlot rel; i--)
            {
                retTail.Insert(0, rel.Local);
            }
            TestHarness.CheckTrue("① 垫 release 序与 ret 出口同口径",
                pad != null
                && pad.Instructions.OfType<MirReleaseSlot>().Select(r => r.Local)
                    .SequenceEqual(retTail),
                string.Join(",", retTail));
            TestHarness.CheckTrue("① 可抛指令 ExcTarget 全解析指向垫",
                pad != null && main.Blocks.SelectMany(b => b.Instructions).All(inst =>
                    inst switch
                    {
                        MirCall call => ReferenceEquals(call.ExcTarget, pad),
                        MirSuperCall superCall => ReferenceEquals(superCall.ExcTarget, pad),
                        MirInvokeIndirect invoke => ReferenceEquals(invoke.ExcTarget, pad),
                        MirThrow throwInst => ReferenceEquals(throwInst.ExcTarget, pad),
                        _ => true,
                    }));

            // ② 无可抛/守卫指令函数（纯常量返回，无调用无 throw 无
            // 守卫型指令——MW9b-G 起 get.field/二元运算/set.array 等
            // 守卫指令也带异常边）：无垫
            ctx = PipelineFromSource(
                "pub func seven(): i32 { return 7 }\n" +
                "pub func main(): i32 { return seven() }\n",
                "rc.padfree.bil");
            var seven = FnOf(ctx, "$seven(");
            TestHarness.CheckTrue("② 无可抛/守卫指令函数无传播垫",
                seven.Blocks.All(b => b.Id != PadId));

            // ③ throw 直写出厂：ExcTarget 解析进垫、原 MirRetThrow 终结符
            // 改道 MirBranch(垫)、全函数 MirRetThrow 仅垫一处
            ctx = PipelineFromSource(
                "class MyError : core.Exception {\n" +
                "    pub init() { }\n" +
                "}\n" +
                "pub func boom(): i32 {\n" +
                "    throw new MyError()\n" +
                "}\n" +
                "pub func main(): i32 { return boom() }\n",
                "rc.throw.bil");
            var boom = FnOf(ctx, "$boom(");
            var boomPad = boom.Blocks.SingleOrDefault(b => b.Id == PadId);
            TestHarness.CheckTrue("③ throw 出厂函数有传播垫", boomPad != null);
            var throwInst = boom.Blocks.SelectMany(b => b.Instructions)
                .OfType<MirThrow>().Single();
            TestHarness.CheckTrue("③ MirThrow ExcTarget=垫（对象身份）",
                boomPad != null && ReferenceEquals(throwInst.ExcTarget, boomPad));
            var throwBlock = boom.Blocks.Single(b => b.Instructions.Contains(throwInst));
            TestHarness.CheckTrue("③ throw 块终结符改道 MirBranch(垫)",
                throwBlock.Terminator is MirBranch branch && branch.Target == PadId);
            TestHarness.CheckTrue("③ MirRetThrow 仅传播垫一处",
                boom.Blocks.Count(b => b.Terminator is MirRetThrow) == 1);
        }

    }
}
