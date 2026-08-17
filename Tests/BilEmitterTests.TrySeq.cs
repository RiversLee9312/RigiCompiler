using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // BilEmitter try/catch/finally 与 seq 发射测试（§16.7 + §19.5 catch-table、§16.1 call 化 + §9.6 volatile）

    public static partial class BilEmitterTests
    {
        // ===== S7e：try/catch/finally 发射（§16.7 + §19.5 catch-table）=====
        private static void TestTryEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "open class MyError : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "class DerivedError : MyError {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func f() {\n" +
                "    try {\n" +
                "        throw new DerivedError()\n" +
                "    } catch (d: DerivedError) {\n" +
                "        log()\n" +
                "    } catch (e: MyError) {\n" +
                "        log()\n" +
                "    } finally(x) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（try 发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（try 发射）", module);
            var f = module.Functions.Single(fn => fn.Symbol == "$f()@.void");
            BilTestHarness.CheckFnShape("try 多 block 文本", module, "$f()@.void",
                ".vars { DerivedError d, MyError e, .nullable<core::Exception> x, " +
                ".breakid .b0, DerivedError .t0, DerivedError .t1, MyError .t2 }\n" +
                ".block entry entrypoint {\n" +
                "try blk(try0-body) $x res(#0) blk(try0-finally) $.b0\n" +
                "ret\n" +
                "}\n" +
                ".block try0-body {\n" +
                "new type(DerivedError) $.t0 []\n" +
                "throw $.t0\n" +
                "}\n" +
                ".block try0-catch0 {\n" +
                "cast $x $.t1 type(DerivedError)\n" +
                "set.var $.t1 $d\n" +
                "invoke.noret fn($log()@.void) []\n" +
                "}\n" +
                ".block try0-catch1 {\n" +
                "cast $x $.t2 type(MyError)\n" +
                "set.var $.t2 $e\n" +
                "invoke.noret fn($log()@.void) []\n" +
                "}\n" +
                ".block try0-finally {\n" +
                "invoke.noret fn($log()@.void) []\n" +
                "}\n");
            // 结构性事实：§16.7 五操作数形状（breakid 为 §16.5 推广末位）
            var tryInstruction = f.Blocks[0].Instructions.Single(i => i is TryInstruction);
            TestHarness.CheckTrue("try 五操作数（body/slot/表/finally/breakid）",
                tryInstruction.Operands.Count == 5
                && tryInstruction.Operands[0] is BilBlockOperand
                && tryInstruction.Operands[1] is BilVariableOperand
                && tryInstruction.Operands[2] is BilResourceOperand
                && tryInstruction.Operands[3] is BilBlockOperand
                && tryInstruction.Operands[4].Render() == "$.b0");
            // §19.5 catch-table：多行形态、元素保序（表序即匹配序）
            var catchTable = module.Resources.OfType<BilCatchTableResource>().Single();
            TestHarness.Check("catch-table 元素（保序）",
                string.Join("\n", catchTable.Entries.Select(e => e.Render())),
                "type(DerivedError) -> blk(try0-catch0)\n" +
                "type(MyError) -> blk(try0-catch1)");
            TestHarness.CheckTrue("catch-table 多行形态（§19.5）",
                BilWriter.Write(module).Contains("catch-table {\n"));
        }

        // ===== S7e：seq 发射（§16.1 call 化 + §9.6 volatile 修饰符）=====
        private static void TestSeqEmission()
        {
            // 语句形态：seq / volatile seq 各一
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "func work() {\n" +
                "}\n" +
                "func s() {\n" +
                "    seq {\n" +
                "        var x = 1\n" +
                "    }\n" +
                "    volatile seq {\n" +
                "        work()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（seq 语句发射）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（seq 语句发射）", module);
            BilTestHarness.CheckFnShape("seq 语句多 block 文本", module, "$s()@.void",
                ".vars { .i32 x, .breakid .b0, .breakid .b1, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0) $.b0\n" +
                "call blk(seq1) $.b1\n" +
                "ret\n" +
                "}\n" +
                ".block seq0 {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $x\n" +
                "}\n" +
                ".block seq1 volatile {\n" +
                "invoke.noret fn($work()@.void) []\n" +
                "}\n");

            // 表达式形态（P4a 已脱糖为前置 seq 块写合成局部）+ volatile 变体
            var (unit2, module2, _) = BilTestHarness.EmitBilUnit(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n" +
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（seq 表达式发射）", unit2);
            BilTestHarness.CheckBilValid("验证器零错误（seq 表达式发射）", module2);
            BilTestHarness.CheckFnShape("seq 表达式多 block 文本", module2, "$se()@.i32",
                ".vars { .i32 .s0, .breakid .b0, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0) $.b0\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "}\n");
            BilTestHarness.CheckFnShape("volatile seq 表达式多 block 文本", module2, "$sv()@.i32",
                ".vars { .i32 .s0, .breakid .b0, .i32 .t0 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0) $.b0\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 volatile {\n" +
                "load res(#0) $.t0\n" +
                "set.var $.t0 $.s0\n" +
                "}\n");
            // 结构性事实：§16.1 call 双操作数 blk + breakid（不建栈帧；
            // breakid 为 §16.5 推广的 region-exit capability 末位操作数）
            var callInstruction = module2.Functions.Single(f => f.Symbol == "$se()@.i32")
                .Blocks[0].Instructions.Single(i => i is CallBlockInstruction);
            TestHarness.CheckTrue("call 双操作数 blk + breakid（§16.1/§16.5）",
                callInstruction.Operands.Count == 2
                && callInstruction.Operands[0] is BilBlockOperand
                && callInstruction.Operands[1].Render() == "$.b0");

            var (unit3, module3, _) = BilTestHarness.EmitBilUnit(
                "class ExprResource implements core.IDisposable { pub override func dispose() { } }\n" +
                "func acquireExpr(): ExprResource { return new ExprResource() }\n" +
                "func exprUsing(): ExprResource { return seq using(const a = acquireExpr()) " +
                "using(var b: ExprResource = a) { return@_ b } }\n");
            CheckNoErrors("表达式 using BIL 无诊断", unit3);
            BilTestHarness.CheckBilValid("表达式 using BIL 验证器零错误", module3);
            var exprFn = module3.Functions.Single(f => f.Symbol == "$exprUsing()@ExprResource");
            var exprText = BilWriter.Write(module3);
            // Stage B：return@_ 穿两层 try/finally——每层 try region 一条
            // i32 route 局部（进入前 load 0 初始化）+ region 后 dispatcher
            // （cmp.eq route → relay 写父 route + break 父 region）
            BilTestHarness.CheckFnShape("表达式 using 结果局部与 ret/vars", module3,
                "$exprUsing()@ExprResource",
                ".vars { ExprResource a, ExprResource b, ExprResource .s0, .breakid .b0, " +
                ".nullable<core::Exception> .s1, .breakid .b1, .nullable<core::Exception> .s2, " +
                ".breakid .b2, .i32 .s3, .i32 .s4, .breakid .b3, .breakid .b4, " +
                "ExprResource .t0, .i32 .t1, .i32 .t2, .i32 .t3, .i32 .t4, .bool .t5, " +
                ".i32 .t6, .i32 .t7, .bool .t8 }\n" +
                ".block entry entrypoint {\n" +
                "call blk(seq0) $.b0\n" +
                "ret $.s0\n" +
                "}\n" +
                ".block seq0 {\n" +
                "invoke fn($acquireExpr()@ExprResource) $.t0 []\n" +
                "set.var $.t0 $a\n" +
                "load res(#0) $.t1\n" +
                "set.var $.t1 $.s4\n" +
                "try blk(try0-body) $.s2 res(#1) blk(try0-finally) $.b2\n" +
                "load res(#2) $.t7\n" +
                "cmp.eq $.s4 $.t7 $.t8\n" +
                "if $.t8 blk(if1-then) none $.b4\n" +
                "}\n" +
                ".block try0-body {\n" +
                "set.var $a $b\n" +
                "load res(#0) $.t2\n" +
                "set.var $.t2 $.s3\n" +
                "try blk(try1-body) $.s1 res(#1) blk(try1-finally) $.b1\n" +
                "load res(#2) $.t4\n" +
                "cmp.eq $.s3 $.t4 $.t5\n" +
                "if $.t5 blk(if0-then) none $.b3\n" +
                "}\n" +
                ".block try1-body {\n" +
                "set.var $b $.s0\n" +
                "load res(#2) $.t3\n" +
                "set.var $.t3 $.s3\n" +
                "break $.b1\n" +
                "}\n" +
                ".block try1-finally {\n" +
                "invoke.noret fn(ExprResource$dispose()@.void) [$b]\n" +
                "}\n" +
                ".block if0-then {\n" +
                "load res(#2) $.t6\n" +
                "set.var $.t6 $.s4\n" +
                "break $.b2\n" +
                "}\n" +
                ".block try0-finally {\n" +
                "invoke.noret fn(ExprResource$dispose()@.void) [$a]\n" +
                "}\n" +
                ".block if1-then {\n" +
                "break $.b0\n" +
                "}\n");
            var exprBlocks = exprFn.Blocks;
            TestHarness.CheckTrue("表达式 using call blk 与 nested try 结构",
                exprBlocks.Any(b => b.Instructions.Any(i => i is CallBlockInstruction))
                && exprBlocks.Count(i => i.Instructions.Any(x => x is TryInstruction)) >= 2);
            var disposeReceivers = exprBlocks.SelectMany(b => b.Instructions)
                .OfType<InvokeNoResultInstruction>()
                .Where(i => i.Method.Symbol.Contains("dispose"))
                .Select(i => i.Arguments[0].Render())
                .ToList();
            TestHarness.Check("表达式 using BIL dispose 逆序", string.Join(",", disposeReceivers),
                "$b,$a");
        }
    }
}
