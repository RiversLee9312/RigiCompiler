using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LatteCompiler.Bil;

namespace LatteCompiler.Tests
{
    // BilEmitter lambda 对象模型（SYNTAX §5.2）：隐藏类 + new/invoke.indirect +
    // cell 捕获闭环（逐变量合成 ..cell..UUID 隐藏子类）。UUID 经
    // BilTestHarness.NormalizeLambdaUuids 归一。
    public static partial class BilEmitterTests
    {
        private static void TestLambdaEmission()
        {
            TestLambdaNoCapture();
            TestLambdaVarCapture();
            TestLambdaConstCapture();
            TestLambdaThisCapture();
            TestLambdaNestedCapture();
            TestLambdaVoidAction();
            TestLambdaAsync();
            TestLambdaExplicitFuncType();
            TestLambdaParamCapturePrologue();
            TestLambdaBlockBody();
            TestLambdaCompoundAssignCapture();
            TestLambdaGenericContext();
            TestLambdaMethodGenericCellCapture();
            TestLambdaMethodGenericNestedCapture();
            TestLambdaMethodGenericParamCapture();
            TestLambdaVoidIndirectCall();
            TestLambdaVoidIndirectCallGrouped();
            TestLambdaVoidIndirectCallReturned();
            TestLambdaVoidIndirectCallIndexed();
            TestLambdaForCapture();
            TestLambdaNestedForCapture();
            TestLambdaCatchCapture();
            TestLambdaFinallyCapture();
            TestLambdaUsingCapture();
        }

        // 无捕获：隐藏类 extends Func、new 空参、invoke.indirect
        private static void TestLambdaNoCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var fn = func{(x: i32): i32 -> (x + 1)}\n" +
                "    return fn(41)\n" +
                "}\n");
            CheckNoErrors("无捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("无捕获 lambda 验证器零错误", module);
            AssertLambdaClass(module, "无捕获", "core::Func<.i32, .i32>", hasCaptureField: false);
            TestHarness.CheckTrue("无捕获 lambda 文本含 new type + invoke.indirect",
                text.Contains("new type(..lambda..UUID)")
                && text.Contains("invoke.indirect "));
            BilTestHarness.CheckFnShape("无捕获 lambda main 形状", module, "$main()@.i32",
                ".vars { ..lambda..UUID fn, ..lambda..UUID .t0, .i32 .t1, .i32 .t2 }\n" +
                "new type(..lambda..UUID) $.t0 []\n" +
                "set.var $.t0 $fn\n" +
                "load res(#0) $.t1\n" +
                "invoke.indirect $fn $.t2 [$.t1]\n" +
                "ret $.t2\n");
        }

        // var 捕获闭环：cell 隐藏子类 + new cell + setValue 写 + invoke.indirect 调
        private static void TestLambdaVarCapture()
        {
            const string source =
                "pub func main(): i32 {\n" +
                "    var x = 1\n" +
                "    var f = func{(): i32 -> (x + 1)}\n" +
                "    x = 2\n" +
                "    return f()\n" +
                "}\n";
            var (unit, module, text) = BilTestHarness.EmitBilUnit(source);
            CheckNoErrors("var 捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("var 捕获 lambda 验证器零错误", module);
            AssertLambdaClass(module, "var 捕获", "core::Func<.i32>", hasCaptureField: true,
                captureTypeFragment: "..cell..");
            // cell 隐藏子类声明形态（新机制核心不变量）
            AssertCellSubclassDeclaration(module, "var 捕获", readOnly: false, elementType: ".i32");
            // TypeSymbol 结构：名以 ..cell.. 开头、CellStorage 非 null、基类 Cell
            var cellClass = CellClassOfCapturedLocal(source, "x");
            TestHarness.CheckTrue("var 捕获 TypeSymbol 为 cell 隐藏子类（Cell 基类）",
                cellClass != null
                && cellClass.Name.StartsWith("..cell..", StringComparison.Ordinal)
                && cellClass.CellStorage != null
                && cellClass.BaseType is { ConstructedFrom: { Name: "Cell" } });
            TestHarness.CheckTrue("var 捕获 .vars 含 cell 隐藏子类",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "x" && v.TypeRef.StartsWith("..cell..",
                        StringComparison.Ordinal)));
            TestHarness.CheckTrue("var 捕获文本：new cell / setValue / invoke.indirect",
                text.Contains("new type(..cell..UUID)")
                && text.Contains("invoke.noret fn(core::Cell$setValue")
                && text.Contains("invoke.indirect $f "));
            BilTestHarness.CheckFnShape("var 捕获 main 形状", module, "$main()@.i32",
                ".vars { ..cell..UUID x, ..lambda..UUID f, .i32 .t0, ..cell..UUID .t1, " +
                "..lambda..UUID .t2, .i32 .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $x\n" +
                "new type(..lambda..UUID) $.t2 [$x]\n" +
                "set.var $.t2 $f\n" +
                "load res(#1) $.t3\n" +
                "invoke.noret fn(core::Cell$setValue(v:.generic<$.generic.T>)@.void) [$x, $.t3]\n" +
                "invoke.indirect $f $.t4 []\n" +
                "ret $.t4\n");
        }

        // const 捕获 → ReadonlyCell 隐藏子类 + ReadonlyCell$getValue
        private static void TestLambdaConstCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    const c = 41\n" +
                "    var f = func{(): i32 -> (c + 1)}\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("const 捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("const 捕获 lambda 验证器零错误", module);
            AssertLambdaClass(module, "const 捕获", "core::Func<.i32>", hasCaptureField: true,
                captureTypeFragment: "..cell..");
            AssertCellSubclassDeclaration(module, "const 捕获", readOnly: true, elementType: ".i32");
            TestHarness.CheckTrue("const 捕获 .vars 含 cell 隐藏子类",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "c" && v.TypeRef.StartsWith("..cell..",
                        StringComparison.Ordinal)));
            TestHarness.CheckTrue("const 捕获 $$call 走 ReadonlyCell$getValue",
                text.Contains("invoke fn(core::ReadonlyCell$getValue()@.generic<$.generic.T>)"));
        }

        // this 捕获：隐藏类 .capture.this + new [$.this] + 验证器闭环
        private static void TestLambdaThisCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "class Counter {\n" +
                "    pub var n: i32\n" +
                "    pub func bump(): i32 {\n" +
                "        var f = func{(): i32 -> (n + 1)}\n" +
                "        return f()\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("this 捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("this 捕获 lambda 验证器零错误", module);
            AssertLambdaClass(module, "this 捕获", "core::Func<.i32>", hasCaptureField: true,
                captureTypeFragment: "@Counter");
            TestHarness.CheckTrue("this 捕获字段名为 .capture.this",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .Where(t => t.Symbol.StartsWith("..lambda.."))
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(m => m.Kind == BilMemberKind.Field
                        && m.Symbol.Contains("#.capture.this@Counter")));
            TestHarness.CheckTrue("this 捕获构造传 $.this + invoke.indirect",
                text.Contains("new type(..lambda..UUID) $.t0 [$.this]")
                && text.Contains("invoke.indirect $f "));
            TestHarness.CheckTrue("this 捕获 init set.field 宿主 $.this",
                text.Contains("set.field $c0 $.this field(..lambda..UUID#.capture.this@Counter)"));
        }

        // 嵌套：外层返回 Func，内层捕获外层 cell（字段引用传递）
        private static void TestLambdaNestedCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 1\n" +
                "    var outer = func{(): core.Func\\<i32> -> func{(): i32 -> (x + 1)}}\n" +
                "    var mid = outer()\n" +
                "    return mid()\n" +
                "}\n");
            CheckNoErrors("嵌套 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("嵌套 lambda 验证器零错误", module);
            var lambdaTypes = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol.StartsWith("..lambda..")).ToList();
            TestHarness.CheckTrue("嵌套 lambda 恰两个隐藏类", lambdaTypes.Count == 2);
            TestHarness.CheckTrue("嵌套外层 extends Func<Func<i32>>",
                lambdaTypes.Any(t => t.ExtendsType == "core::Func<core::Func<.i32>>"));
            TestHarness.CheckTrue("嵌套内层 extends Func<i32>",
                lambdaTypes.Any(t => t.ExtendsType == "core::Func<.i32>"));
            TestHarness.CheckTrue("嵌套两层均含 .capture.x cell 字段",
                lambdaTypes.All(t => t.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(m => m.Kind == BilMemberKind.Field
                        && m.Symbol.Contains("#.capture.x@")
                        && m.Symbol.Contains("..cell.."))));
            TestHarness.CheckTrue("嵌套调用两次 invoke.indirect",
                Regex.Matches(text, @"invoke\.indirect \$").Count >= 2);
        }

        // void lambda → core::Action；语句位置 invoke.indirect.noret
        private static void TestLambdaVoidAction()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "pub func main(): i32 {\n" +
                "    var act = func{() -> { sink(1) }}\n" +
                "    act()\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("void lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("void lambda 验证器零错误", module);
            AssertLambdaClass(module, "void", "core::Action", hasCaptureField: false);
            TestHarness.CheckTrue("void lambda 语句调用 invoke.indirect.noret",
                text.Contains("invoke.indirect.noret $act []"));
            BilTestHarness.CheckFnShape("void lambda main 形状", module, "$main()@.i32",
                ".vars { ..lambda..UUID act, ..lambda..UUID .t0, .i32 .t1 }\n" +
                "new type(..lambda..UUID) $.t0 []\n" +
                "set.var $.t0 $act\n" +
                "invoke.indirect.noret $act []\n" +
                "load res(#0) $.t1\n" +
                "ret $.t1\n");
        }

        // async lambda → shared AsyncFunc；调用结果 Task<.string>
        private static void TestLambdaAsync()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var f = func{async (id: i32): String -> \"ok\"}\n" +
                "    var t = f(1)\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("async lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("async lambda 验证器零错误", module);
            var lambda = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol.StartsWith("..lambda.."));
            TestHarness.CheckTrue("async lambda extends AsyncFunc<.string, .i32>",
                lambda.ExtendsType == "core::AsyncFunc<.string, .i32>");
            TestHarness.CheckTrue("async lambda 类型带 shared",
                lambda.Modifiers.OfType<BilKeywordModifier>()
                    .Any(m => m.Keyword == BilKeyword.Shared));
            TestHarness.CheckTrue("async 调用结果 Task<.string>",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "t"
                        && v.TypeRef == "core.coroutine::Task<.string>"));
            TestHarness.CheckTrue("async $$call 声明带 async",
                lambda.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(m => m.Symbol.Contains("$$call")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Async)));
            TestHarness.CheckTrue("async 调用点 invoke.indirect（有结果）",
                text.Contains("invoke.indirect $f "));
        }

        // 显式 Func 标注：new 后 cast 到 core::Func
        private static void TestLambdaExplicitFuncType()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    const f: core.Func\\<i32, i32> = func{(x: i32): i32 -> x}\n" +
                "    return f(1)\n" +
                "}\n");
            CheckNoErrors("显式 Func 标注全管线无诊断", unit);
            BilTestHarness.CheckBilValid("显式 Func 标注验证器零错误", module);
            TestHarness.CheckTrue("显式标注 f 类型 = core::Func<.i32, .i32>",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "f" && v.TypeRef == "core::Func<.i32, .i32>"));
            BilTestHarness.CheckFnShape("显式 Func 标注 main 形状（new + cast）", module,
                "$main()@.i32",
                ".vars { core::Func<.i32, .i32> f, ..lambda..UUID .t0, " +
                "core::Func<.i32, .i32> .t1, .i32 .t2, .i32 .t3 }\n" +
                "new type(..lambda..UUID) $.t0 []\n" +
                "cast $.t0 $.t1 type(core::Func<.i32, .i32>)\n" +
                "set.var $.t1 $f\n" +
                "load res(#0) $.t2\n" +
                "invoke.indirect $f $.t3 [$.t2]\n" +
                "ret $.t3\n");
            TestHarness.CheckTrue("显式标注文本含 cast type(core::Func",
                text.Contains("cast $.t0 $.t1 type(core::Func<.i32, .i32>)"));
        }

        // 参数捕获 prologue：.c.<名> cell 局部
        private static void TestLambdaParamCapturePrologue()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func host(p: i32): i32 {\n" +
                "    var f = func{(): i32 -> p}\n" +
                "    return f()\n" +
                "}\n" +
                "pub func main(): i32 { return host(1) }\n");
            CheckNoErrors("参数捕获 prologue 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("参数捕获 prologue 验证器零错误", module);
            BilTestHarness.CheckFnShape("参数捕获 host 形状（.c.p prologue）", module,
                "$host(p:.i32)@.i32",
                ".vars { ..lambda..UUID f, ..cell..UUID .c.p, ..cell..UUID .t0, " +
                "..lambda..UUID .t1, .i32 .t2 }\n" +
                "new type(..cell..UUID) $.t0 [$p]\n" +
                "set.var $.t0 $.c.p\n" +
                "new type(..lambda..UUID) $.t1 [$.c.p]\n" +
                "set.var $.t1 $f\n" +
                "invoke.indirect $f $.t2 []\n" +
                "ret $.t2\n");
            TestHarness.CheckTrue("参数捕获文本含 $.c.p",
                text.Contains("$.c.p"));
        }

        // 块体 lambda：值块降级 return@_ → $$call 体含 ret
        private static void TestLambdaBlockBody()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var x = 1\n" +
                "    var fn = func{(): i32 -> {\n" +
                "        var d = (x * 2)\n" +
                "        return@_ d\n" +
                "    }}\n" +
                "    return fn()\n" +
                "}\n");
            CheckNoErrors("块体 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("块体 lambda 验证器零错误", module);
            AssertLambdaClass(module, "块体", "core::Func<.i32>", hasCaptureField: true,
                captureTypeFragment: "..cell..");
            var callFn = module.Functions.First(f => f.Symbol.Contains("$$call"));
            BilTestHarness.CheckFnShape("块体 lambda $$call 含 ret（值块降级）", module,
                callFn.Symbol,
                ".vars { .i32 d, .i32 .s0, ..cell..UUID .t0, .i32 .t1, .i32 .t2, .i32 .t3 }\n" +
                "get.field $.this $.t0 field(..lambda..UUID#.capture.x@..cell..UUID)\n" +
                "invoke fn(core::Cell$getValue()@.generic<$.generic.T>) $.t1 [$.t0]\n" +
                "load res(#0) $.t2\n" +
                "mul $.t1 $.t2 $.t3\n" +
                "set.var $.t3 $d\n" +
                "set.var $d $.s0\n" +
                "ret $.s0\n");
        }

        // 复合赋值捕获写回：getValue → 运算 → setValue（.s0 承载运算结果）
        private static void TestLambdaCompoundAssignCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var total = 0\n" +
                "    var add = func{(x: i32) -> { total += x }}\n" +
                "    add(5)\n" +
                "    return total\n" +
                "}\n");
            CheckNoErrors("复合赋值捕获写回全管线无诊断", unit);
            BilTestHarness.CheckBilValid("复合赋值捕获写回验证器零错误", module);
            AssertLambdaClass(module, "复合赋值捕获", "core::Action<.i32>", hasCaptureField: true,
                captureTypeFragment: "..cell..");
            var callFn = module.Functions.First(f => f.Symbol.Contains("$$call"));
            BilTestHarness.CheckFnShape("复合赋值捕获 $$call getValue→add→setValue", module,
                callFn.Symbol,
                ".vars { .i32 .s0, ..cell..UUID .t0, .i32 .t1, .i32 .t2, ..cell..UUID .t3 }\n" +
                "get.field $.this $.t0 field(..lambda..UUID#.capture.total@..cell..UUID)\n" +
                "invoke fn(core::Cell$getValue()@.generic<$.generic.T>) $.t1 [$.t0]\n" +
                "add $.t1 $x $.t2\n" +
                "set.var $.t2 $.s0\n" +
                "get.field $.this $.t3 field(..lambda..UUID#.capture.total@..cell..UUID)\n" +
                "invoke.noret fn(core::Cell$setValue(v:.generic<$.generic.T>)@.void) [$.t3, $.s0]\n" +
                "ret\n");
            BilTestHarness.CheckFnShape("复合赋值捕获 main 形状", module, "$main()@.i32",
                ".vars { ..cell..UUID total, ..lambda..UUID add, .i32 .t0, ..cell..UUID .t1, " +
                "..lambda..UUID .t2, .i32 .t3, .i32 .t4 }\n" +
                "load res(#0) $.t0\n" +
                "new type(..cell..UUID) $.t1 [$.t0]\n" +
                "set.var $.t1 $total\n" +
                "new type(..lambda..UUID) $.t2 [$total]\n" +
                "set.var $.t2 $add\n" +
                "load res(#1) $.t3\n" +
                "invoke.indirect.noret $add [$.t3]\n" +
                "invoke fn(core::Cell$getValue()@.generic<$.generic.T>) $.t4 [$total]\n" +
                "ret $.t4\n");
        }

        // 泛型上下文 lambda：隐藏类共享外层 generic(T)，构造点转发 typeid
        private static void TestLambdaGenericContext()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func wrap\\<T>(x: T): core.Func\\<T, T> {\n" +
                "    var id = func{(v: T): T -> v}\n" +
                "    return id\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("泛型上下文 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("泛型上下文 lambda 验证器零错误", module);
            var lambda = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol.StartsWith("..lambda.."));
            TestHarness.CheckTrue("泛型上下文隐藏类带 generic(T)",
                lambda.GenericParameters.Count == 1
                && lambda.GenericParameters[0] == "T");
            TestHarness.CheckTrue("泛型上下文 extends Func<T, T>",
                lambda.ExtendsType
                == "core::Func<.generic<$.generic.T>, .generic<$.generic.T>>");
            TestHarness.CheckTrue("构造点 type 操作数转发 typeid",
                text.Contains("new type(..lambda..UUID<.generic<$.generic.T>>)"));
            BilTestHarness.CheckFnShape("泛型上下文 wrap 形状", module,
                "$wrap(x:.generic<$.generic.T>)@core::Func<.generic<$.generic.T>, .generic<$.generic.T>>",
                ".vars { ..lambda..UUID<.generic<$.generic.T>> id, " +
                "..lambda..UUID<.generic<$.generic.T>> .t0, " +
                "core::Func<.generic<$.generic.T>, .generic<$.generic.T>> .t1 }\n" +
                "new type(..lambda..UUID<.generic<$.generic.T>>) $.t0 []\n" +
                "set.var $.t0 $id\n" +
                "cast $id $.t1 type(core::Func<.generic<$.generic.T>, .generic<$.generic.T>>)\n" +
                "ret $.t1\n");
        }

        // M112：外层方法泛型 T 的局部经 cell 捕获——cell/lambda 均 generic(T)，
        // 构造点转发 $.generic.T；读写经 getValue/setValue
        private static void TestLambdaMethodGenericCellCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func wrap\\<T>(x: T): T {\n" +
                "    var y: T = x\n" +
                "    var f = func{(): T -> y}\n" +
                "    y = x\n" +
                "    return f()\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("方法泛型 T cell 捕获全管线无诊断", unit);
            BilTestHarness.CheckBilValid("方法泛型 T cell 捕获验证器零错误", module);
            var cell = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol.StartsWith("..cell.."));
            var lambda = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Single(t => t.Symbol.StartsWith("..lambda.."));
            TestHarness.CheckTrue("cell 子类 generic(T)",
                cell.GenericParameters.Count == 1 && cell.GenericParameters[0] == "T");
            TestHarness.CheckTrue("cell extends .cell<T>",
                cell.ExtendsType == ".cell<.generic<$.generic.T>>");
            TestHarness.CheckTrue("lambda 隐藏类 generic(T)",
                lambda.GenericParameters.Count == 1 && lambda.GenericParameters[0] == "T");
            TestHarness.CheckTrue("构造 cell/lambda 均转发 $.generic.T",
                text.Contains("new type(..cell..UUID<.generic<$.generic.T>>)")
                && text.Contains("new type(..lambda..UUID<.generic<$.generic.T>>)"));
            TestHarness.CheckTrue("读写经 Cell getValue/setValue",
                text.Contains("getValue") && text.Contains("setValue"));
            BilTestHarness.CheckFnShape("方法泛型 T cell 捕获 wrap 形状", module,
                "$wrap(x:.generic<$.generic.T>)@.generic<$.generic.T>",
                ".vars { ..cell..UUID<.generic<$.generic.T>> y, " +
                "..lambda..UUID<.generic<$.generic.T>> f, " +
                "..cell..UUID<.generic<$.generic.T>> .t0, " +
                "..lambda..UUID<.generic<$.generic.T>> .t1, " +
                ".generic<$.generic.T> .t2 }\n" +
                "new type(..cell..UUID<.generic<$.generic.T>>) $.t0 [$x]\n" +
                "set.var $.t0 $y\n" +
                "new type(..lambda..UUID<.generic<$.generic.T>>) $.t1 [$y]\n" +
                "set.var $.t1 $f\n" +
                "invoke.noret fn(core::Cell$setValue(v:.generic<$.generic.T>)@.void) [$y, $x]\n" +
                "invoke.indirect $f $.t2 []\n" +
                "ret $.t2\n");
        }

        // M112：嵌套 lambda 捕获外层方法泛型 T 的参数——两侧 hidden class + cell
        private static void TestLambdaMethodGenericNestedCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func wrap\\<T>(x: T): T {\n" +
                "    var f = func{(v: T): T -> {\n" +
                "        var g = func{(): T -> v}\n" +
                "        return@_ g()\n" +
                "    }}\n" +
                "    return f(x)\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("嵌套 lambda 方法泛型 T 捕获全管线无诊断", unit);
            BilTestHarness.CheckBilValid("嵌套 lambda 方法泛型 T 捕获验证器零错误", module);
            var lambdas = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol.StartsWith("..lambda..")).ToList();
            var cells = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol.StartsWith("..cell..")).ToList();
            TestHarness.CheckTrue("两层 lambda 均 generic(T)",
                lambdas.Count == 2
                && lambdas.All(l => l.GenericParameters.Count == 1
                    && l.GenericParameters[0] == "T"));
            TestHarness.CheckTrue("捕获 cell 至少一枚且 generic(T)",
                cells.Count >= 1
                && cells.All(c => c.GenericParameters.Count == 1
                    && c.GenericParameters[0] == "T"));
            TestHarness.CheckTrue("嵌套构造转发 $.generic.T",
                text.Contains("new type(..lambda..UUID<.generic<$.generic.T>>)"));
        }

        // M112：直接捕获方法泛型参数 x: T（prologue .c.x cell）
        private static void TestLambdaMethodGenericParamCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func wrap\\<T>(x: T): T {\n" +
                "    var f = func{(): T -> x}\n" +
                "    return f()\n" +
                "}\n" +
                "pub func main(): i32 { return 0 }\n");
            CheckNoErrors("方法泛型参数捕获全管线无诊断", unit);
            BilTestHarness.CheckBilValid("方法泛型参数捕获验证器零错误", module);
            TestHarness.CheckTrue("参数 prologue 构造 cell<.generic.T>",
                text.Contains("new type(..cell..UUID<.generic<$.generic.T>>)"));
            TestHarness.CheckTrue(".vars 含 .c.x cell",
                module.Functions.First(f => f.Symbol.StartsWith("$wrap("))
                    .Vars.Any(v => v.Name == ".c.x"
                        && v.TypeRef.StartsWith("..cell..")
                        && v.TypeRef.Contains(".generic<$.generic.T>")));
        }

        // 语句位置 void 间接调用：Action 局部 act() → invoke.indirect.noret
        private static void TestLambdaVoidIndirectCall()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "pub func main(): i32 {\n" +
                "    var act: core.Action = func{() -> { sink(0) }}\n" +
                "    act()\n" +
                "    return 1\n" +
                "}\n");
            CheckNoErrors("语句位置 void 间接调用全管线无诊断", unit);
            BilTestHarness.CheckBilValid("语句位置 void 间接调用验证器零错误", module);
            TestHarness.CheckTrue("act 静态类型 core::Action",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "act" && v.TypeRef == "core::Action"));
            TestHarness.CheckTrue("语句位置 act() 发 invoke.indirect.noret",
                text.Contains("invoke.indirect.noret $act []"));
        }

        // M105：括号形态 (act)() 语句位置 → invoke.indirect.noret
        private static void TestLambdaVoidIndirectCallGrouped()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "pub func main(): i32 {\n" +
                "    var act: core.Action = func{() -> { sink(0) }}\n" +
                "    (act)()\n" +
                "    return 1\n" +
                "}\n");
            CheckNoErrors("括号形态 (act)() 语句位置全管线无诊断", unit);
            BilTestHarness.CheckBilValid("括号形态 (act)() 验证器零错误", module);
            TestHarness.CheckTrue("括号形态 (act)() 发 invoke.indirect.noret",
                text.Contains("invoke.indirect.noret $act []"));
        }

        // M105：(getHandler())() —— 返回 Action 的调用结果再间接调用
        private static void TestLambdaVoidIndirectCallReturned()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "pub func getHandler(): core.Action { return func{() -> { sink(0) }} }\n" +
                "pub func main(): i32 {\n" +
                "    (getHandler())()\n" +
                "    return 1\n" +
                "}\n");
            CheckNoErrors("(getHandler())() 语句位置全管线无诊断", unit);
            BilTestHarness.CheckBilValid("(getHandler())() 验证器零错误", module);
            TestHarness.CheckTrue("(getHandler())() 发 invoke.indirect.noret",
                text.Contains("invoke.indirect.noret"));
        }

        // M105：handlers[0]() —— 索引后 void 间接调用
        private static void TestLambdaVoidIndirectCallIndexed()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func sink(v: i32) { }\n" +
                "class Handlers {\n" +
                "    pub var h: core.Action\n" +
                "    pub init(_ -> h)\n" +
                "    pub operator getAtIndex(index: i32): core.Action { return h }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var handlers = new Handlers(h = func{() -> { sink(0) }})\n" +
                "    handlers[0]()\n" +
                "    return 1\n" +
                "}\n");
            CheckNoErrors("handlers[0]() 语句位置全管线无诊断", unit);
            BilTestHarness.CheckBilValid("handlers[0]() 验证器零错误", module);
            TestHarness.CheckTrue("handlers[0]() 发 invoke.indirect.noret",
                text.Contains("invoke.indirect.noret"));
        }

        // 块内指令文本（Opcode + 操作数 Render，供结构断言）
        private static string RenderBlockText(BilBlock block)
        {
            return string.Join("\n", block.Instructions.Select(i =>
                i.Opcode + " " + string.Join(" ", i.Operands.Select(o => o.Render()))));
        }

        // M106：for 循环变量捕获——每迭代新 cell（Body 头 new cell + invoke.indirect）
        private static void TestLambdaForCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var f: core.Func\\<i32> = func{(): i32 -> 0}\n" +
                "    for (i in 0 to 1) {\n" +
                "        f = func{(): i32 -> i}\n" +
                "    }\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("for 捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("for 捕获 lambda 验证器零错误", module);
            // 初始化器 lambda 无捕获——断言含 .capture.i 的那一个
            var capturing = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol.StartsWith("..lambda.."))
                .FirstOrDefault(t => t.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(m => m.Kind == BilMemberKind.Field
                        && m.Symbol.Contains("#.capture.i@")
                        && m.Symbol.Contains("..cell..")));
            TestHarness.CheckTrue("for 捕获隐藏类含 .capture.i cell 字段",
                capturing != null && capturing.ExtendsType == "core::Func<.i32>");
            AssertCellSubclassDeclaration(module, "for 捕获", readOnly: true, elementType: ".i32");
            TestHarness.CheckTrue("for 捕获 .vars 含 cell 化循环变量 i",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "i" && v.TypeRef.StartsWith("..cell..",
                        StringComparison.Ordinal)));
            // cell 构造落在 loop Body 块内（每迭代 new）
            var bodyBlock = module.Functions.First(f => f.Symbol == "$main()@.i32")
                .Blocks.First(b => b.Id.Contains("body"));
            TestHarness.CheckTrue("for 捕获 cell 构造在 loop Body 块内",
                BilTestHarness.NormalizeLambdaUuids(RenderBlockText(bodyBlock))
                    .Contains("new type(..cell..UUID)"));
            TestHarness.CheckTrue("for 捕获 invoke.indirect 可用",
                text.Contains("invoke.indirect $f "));
        }

        // M106：嵌套——外层 for 变量经外层 lambda 传给内层 lambda
        private static void TestLambdaNestedForCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    var outer: core.Func\\<core.Func\\<i32>> = " +
                "func{(): core.Func\\<i32> -> func{(): i32 -> 0}}\n" +
                "    for (i in 0 to 1) {\n" +
                "        outer = func{(): core.Func\\<i32> -> func{(): i32 -> i}}\n" +
                "    }\n" +
                "    var mid = outer()\n" +
                "    return mid()\n" +
                "}\n");
            CheckNoErrors("嵌套 for 捕获全管线无诊断", unit);
            BilTestHarness.CheckBilValid("嵌套 for 捕获验证器零错误", module);
            // 初始化器两层无捕获 + 循环内两层有捕获 = 至少 4 个隐藏类；
            // 断言捕获 i 的恰两层（外层 Func<Func<i32>> + 内层 Func<i32>）
            var capturingI = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol.StartsWith("..lambda.."))
                .Where(t => t.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(m => m.Kind == BilMemberKind.Field
                        && m.Symbol.Contains("#.capture.i@")
                        && m.Symbol.Contains("..cell..")))
                .ToList();
            TestHarness.CheckTrue("嵌套 for 捕获 i 的恰两个隐藏类", capturingI.Count == 2);
            TestHarness.CheckTrue("嵌套 for 外层 extends Func<Func<i32>>",
                capturingI.Any(t => t.ExtendsType == "core::Func<core::Func<.i32>>"));
            TestHarness.CheckTrue("嵌套 for 内层 extends Func<i32>",
                capturingI.Any(t => t.ExtendsType == "core::Func<.i32>"));
            TestHarness.CheckTrue("嵌套 for 捕获两次 invoke.indirect",
                Regex.Matches(text, @"invoke\.indirect \$").Count >= 2);
        }

        // M106：catch 变量捕获——catch 体头 new cell(cast slot)
        private static void TestLambdaCatchCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "open class MyError : core.Exception { }\n" +
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "        throw new MyError()\n" +
                "    } catch (e: MyError) {\n" +
                "        var act = func{() -> { var x = e }}\n" +
                "        act()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("catch 捕获 lambda 全管线无诊断", unit);
            BilTestHarness.CheckBilValid("catch 捕获 lambda 验证器零错误", module);
            AssertCellSubclassDeclaration(module, "catch 捕获", readOnly: true,
                elementType: "MyError");
            TestHarness.CheckTrue("catch 捕获 .vars 含 cell 化 e",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "e" && v.TypeRef.StartsWith("..cell..",
                        StringComparison.Ordinal)));
            var catchBlock = module.Functions.First(f => f.Symbol == "$main()@.i32")
                .Blocks.First(b => b.Id.Contains("catch"));
            var catchText = BilTestHarness.NormalizeLambdaUuids(RenderBlockText(catchBlock));
            TestHarness.CheckTrue("catch 体头 cast + new cell",
                catchText.Contains("cast $") && catchText.Contains("new type(..cell..UUID)"));
            TestHarness.CheckTrue("catch 捕获 invoke.indirect 可用",
                text.Contains("invoke.indirect.noret $act ")
                || text.Contains("invoke.indirect $act "));
        }

        // M106：finally(e) 捕获——slot 与变量分离，finally 体头 new cell(slot)
        private static void TestLambdaFinallyCapture()
        {
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "pub func main(): i32 {\n" +
                "    try {\n" +
                "    } finally(e) {\n" +
                "        var act = func{() -> { var x = e }}\n" +
                "        act()\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("finally(e) 捕获全管线无诊断", unit);
            BilTestHarness.CheckBilValid("finally(e) 捕获验证器零错误", module);
            var main = module.Functions.First(f => f.Symbol == "$main()@.i32");
            TestHarness.CheckTrue("finally 捕获 e 为 cell 类型",
                main.Vars.Any(v => v.Name == "e" && v.TypeRef.StartsWith("..cell..",
                    StringComparison.Ordinal)));
            // ExceptionSlot 是普通 .sN（Nullable），非 e 自身
            var tryInst = main.Blocks.SelectMany(b => b.Instructions)
                .OfType<TryInstruction>().First();
            var slotName = tryInst.ExceptionSlot.Name;
            TestHarness.CheckTrue("finally 捕获 ExceptionSlot 与 e 分离（.sN）",
                slotName.StartsWith(".s", StringComparison.Ordinal) && slotName != "e");
            TestHarness.CheckTrue("finally 捕获 slot 类型为 nullable Exception",
                main.Vars.Any(v => v.Name == slotName
                    && v.TypeRef == ".nullable<core::Exception>"));
            var finallyBlock = main.Blocks.First(b => b.Id.Contains("finally"));
            TestHarness.CheckTrue("finally 体头 new cell(slot)",
                BilTestHarness.NormalizeLambdaUuids(RenderBlockText(finallyBlock))
                    .Contains("new type(..cell..UUID)"));
            TestHarness.CheckTrue("finally 捕获 invoke.indirect 可用",
                text.Contains("invoke.indirect.noret $act ")
                || text.Contains("invoke.indirect $act "));
        }

        // M106：using 捕获——const/var 两风味 + dispose receiver 读改写
        private static void TestLambdaUsingCapture()
        {
            // const 风味
            var (unit, module, text) = BilTestHarness.EmitBilUnit(
                "class Res implements core.IDisposable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f: core.Func\\<i32> = func{(): i32 -> 0}\n" +
                "    seq using(const r = new Res(n = 7)) {\n" +
                "        f = func{(): i32 -> r.n}\n" +
                "    }\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("using const 捕获全管线无诊断", unit);
            BilTestHarness.CheckBilValid("using const 捕获验证器零错误", module);
            AssertCellSubclassDeclaration(module, "using const 捕获", readOnly: true,
                elementType: "Res");
            TestHarness.CheckTrue("using const 捕获 .vars 含 cell 化 r",
                module.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "r" && v.TypeRef.StartsWith("..cell..",
                        StringComparison.Ordinal)));
            TestHarness.CheckTrue("using const 捕获 new cell + dispose getValue",
                text.Contains("new type(..cell..UUID)")
                && text.Contains("invoke fn(core::ReadonlyCell$getValue")
                && text.Contains("dispose"));
            TestHarness.CheckTrue("using const 捕获 invoke.indirect 可用",
                text.Contains("invoke.indirect $f "));

            // var 风味（源写 var → Cell 风味；using 资源仍禁重赋值）
            var (unit2, module2, text2) = BilTestHarness.EmitBilUnit(
                "class Res2 implements core.IDisposable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n)\n" +
                "    pub override func dispose() { }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f: core.Func\\<i32> = func{(): i32 -> 0}\n" +
                "    seq using(var r: Res2 = new Res2(n = 3)) {\n" +
                "        f = func{(): i32 -> r.n}\n" +
                "    }\n" +
                "    return f()\n" +
                "}\n");
            CheckNoErrors("using var 捕获全管线无诊断", unit2);
            BilTestHarness.CheckBilValid("using var 捕获验证器零错误", module2);
            TestHarness.CheckTrue("using var 捕获 .vars 含 cell 化 r",
                module2.Functions.First(f => f.Symbol == "$main()@.i32")
                    .Vars.Any(v => v.Name == "r" && v.TypeRef.StartsWith("..cell..",
                        StringComparison.Ordinal)));
            TestHarness.CheckTrue("using var 捕获 dispose 经 getValue 读 receiver",
                text2.Contains("getValue") && text2.Contains("dispose"));
            TestHarness.CheckTrue("using var 捕获 invoke.indirect 可用",
                text2.Contains("invoke.indirect $f "));
        }

        // 隐藏类结构：class + extends + operator(call) + 可选 capture 字段
        // capture 字段类型：cell 捕获为 ..cell..UUID 隐藏子类；this 捕获为宿主类型
        private static void AssertLambdaClass(BilModule module, string label,
            string extendsType, bool hasCaptureField, string? captureTypeFragment = null)
        {
            var lambda = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .FirstOrDefault(t => t.Symbol.StartsWith("..lambda.."));
            TestHarness.CheckTrue($"{label} LocalSymbols 含 ..lambda..UUID class",
                lambda != null && lambda.Kind == BilTypeKind.Class);
            if (lambda == null) return;
            TestHarness.CheckTrue($"{label} extends {extendsType}",
                lambda.ExtendsType == extendsType);
            TestHarness.CheckTrue($"{label} 含 operator(call) 成员",
                lambda.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(m => m.Kind == BilMemberKind.Method
                        && m.Symbol.Contains("$$call")
                        && m.Modifiers.OfType<BilOperatorModifier>()
                            .Any(op => op.Name == "call")));
            TestHarness.CheckTrue($"{label} 含 init 成员",
                lambda.Members.OfType<BilSimpleMemberDeclaration>()
                    .Any(m => m.Kind == BilMemberKind.Method
                        && m.Symbol.Contains("$init")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Init)));
            var captureFields = lambda.Members.OfType<BilSimpleMemberDeclaration>()
                .Where(m => m.Kind == BilMemberKind.Field
                    && m.Symbol.Contains("#.capture.")).ToList();
            if (hasCaptureField)
            {
                TestHarness.CheckTrue($"{label} 含 .capture 字段",
                    captureFields.Count >= 1
                    && (captureTypeFragment == null
                        || captureFields.Any(f => f.Symbol.Contains(captureTypeFragment))));
            }
            else
            {
                TestHarness.CheckTrue($"{label} 无 .capture 字段", captureFields.Count == 0);
            }
        }

        // cell 隐藏子类声明形态：extends .cell<.T>/.readonly_cell<.T> + value 字段 +
        // override getValue（Cell 风味另含 init()/setValue）
        private static void AssertCellSubclassDeclaration(BilModule module, string label,
            bool readOnly, string elementType)
        {
            var expectedExtends = readOnly
                ? $".readonly_cell<{elementType}>"
                : $".cell<{elementType}>";
            var cells = module.LocalSymbols.OfType<BilTypeDeclaration>()
                .Where(t => t.Symbol.StartsWith("..cell..")).ToList();
            TestHarness.CheckTrue($"{label} LocalSymbols 含 ..cell.. 隐藏子类",
                cells.Count >= 1);
            var cell = cells.FirstOrDefault(t => t.ExtendsType == expectedExtends);
            TestHarness.CheckTrue($"{label} cell 子类 extends {expectedExtends}",
                cell != null);
            if (cell == null) return;
            var members = cell.Members.OfType<BilSimpleMemberDeclaration>().ToList();
            var valueField = members.FirstOrDefault(m =>
                m.Kind == BilMemberKind.Field && m.Symbol.Contains("#value@"));
            TestHarness.CheckTrue($"{label} cell 子类含 pub {(readOnly ? "const" : "var")} value",
                valueField != null
                && valueField.Symbol.EndsWith($"#value@{elementType}", StringComparison.Ordinal)
                && valueField.Modifiers.OfType<BilAccessibilityModifier>()
                    .Any(a => a.Accessibility == BilAccessibility.Public)
                && valueField.Modifiers.OfType<BilKeywordModifier>().Any(k =>
                    k.Keyword == (readOnly ? BilKeyword.Const : BilKeyword.Var)));
            TestHarness.CheckTrue($"{label} cell 子类 override getValue",
                members.Any(m => m.Kind == BilMemberKind.Method
                    && m.Symbol.Contains("$getValue()")
                    && m.Modifiers.OfType<BilKeywordModifier>()
                        .Any(k => k.Keyword == BilKeyword.Override)));
            if (readOnly)
            {
                TestHarness.CheckTrue($"{label} ReadonlyCell 无 setValue/空 init",
                    !members.Any(m => m.Symbol.Contains("$setValue"))
                    && !members.Any(m => m.Symbol.Contains("$init()@")));
                TestHarness.CheckTrue($"{label} ReadonlyCell 含 init(value)",
                    members.Any(m => m.Kind == BilMemberKind.Method
                        && m.Symbol.Contains("$init(value:")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Init)));
            }
            else
            {
                TestHarness.CheckTrue($"{label} Cell 含 init()/init(value)/override setValue",
                    members.Any(m => m.Symbol.Contains("$init()@")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Init))
                    && members.Any(m => m.Symbol.Contains("$init(value:")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Init))
                    && members.Any(m => m.Symbol.Contains("$setValue")
                        && m.Modifiers.OfType<BilKeywordModifier>()
                            .Any(k => k.Keyword == BilKeyword.Override)));
            }
        }

        // 重新绑定拿被捕获局部的 cell 隐藏子类 TypeSymbol（EmitBilUnit 不返回 bodies）
        private static TypeSymbol? CellClassOfCapturedLocal(string source, string localName)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.Add(TestHarness.ParseRoot(source, BilTestHarness.UserSourceName));
            var unit = new CompilationUnit(roots.ToArray());
            var declarations = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, declarations);
            var bodies = Binder.Bind(unit, declarations);
            var local = bodies.SelectMany(b => b.Locals)
                .FirstOrDefault(l => l.Name == localName);
            return local?.CellStorage?.CellClass;
        }
    }
}
