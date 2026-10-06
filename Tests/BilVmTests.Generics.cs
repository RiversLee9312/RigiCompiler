using System;
using System.Collections.Generic;
using System.Linq;
using RigiCompiler.Bil;
using RigiCompiler.Bil.Vm;

namespace RigiCompiler.Tests
{
    public static partial class BilVmTests
    {
        // Generics 职责；与主文件共享同一类型、字段及生命周期。

        // bug13①：泛型构造类型的运行期 init 匹配——定义级 init 签名的
        // .generic 占位按构造实参代入后比对（dist repro_bug2 与牵连用例）：
        // new core.Pair<String, i32>(...) / 用户泛型类带参构造 / kwargs 打包
        private static void TestGenericConstructedNewInit()
        {
            var result = Run(
                "class Container\\<T> {\n" +
                "    pub const item: T\n" +
                "    pub init(_ -> item) { }\n" +
                "}\n" +
                "func config(options: named Any...) {\n" +
                "    core.io.Console.println(\"kwargs-ok\")\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new core.Pair\\<String, i32>(\"age\", 3)\n" +
                "    core.io.Console.println(\"${p.key}:${p.value}\")\n" +
                "    var c = new Container\\<i32>(1)\n" +
                "    core.io.Console.println(\"${c.item}\")\n" +
                "    config(isDark = true, level = 3)\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("泛型构造类型 init 匹配", result);
            CaseAssertions.Check("泛型构造 stdout", result.Stdout,
                "age:3\n1\nkwargs-ok\n");
            CheckI32("main 返回 0", result, 0);
        }

        // bug13① 牵连：解构用 Pair 子类的 super(k, v)——extends 构造实参
        // 代入基类定义级 init 签名后匹配（dist 12_destructure 形态）
        private static void TestDestructuringSuperGenericInit()
        {
            var result = Run(
                "pub class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n" +
                "        super(k, v)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var (k, v) = new Entry(\"age\", 3)\n" +
                "    core.io.Console.println(\"${k}:${v}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("Pair 子类 super 调用 + 解构", result);
            CaseAssertions.Check("解构 stdout", result.Stdout, "age:3\n");
            CheckI32("main 返回 0", result, 0);
        }

        // bug14：方法符号内嵌闭合泛型类型引用——canonical 符号是成员声明行
        // 的单个词（§5.2），实参分隔不得含空白；发射紧凑形态后 BilReader/
        // 验证器/VM 全链路可消化（dist _repro_func2param 形态扩展为实调）
        private static void TestClosedGenericParamSymbolEndToEnd()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub func take2(f: core.Func\\<i32, i32>): i32 {\n" +
                "    return f(1)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var f = func{(x: i32): i32 -> (x + 41)}\n" +
                "    return take2(f)\n" +
                "}\n");
            CaseAssertions.CheckTrue("全管线无诊断", !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(
                    d => $"{d.Phase}: {d.Message}")));
            // 签名形态回归锁：符号内闭合泛型紧凑无空白
            CaseAssertions.CheckTrue("方法符号内嵌闭合泛型紧凑形态",
                module.LocalSymbols.OfType<BilSimpleMemberDeclaration>().Any(d =>
                    d.Symbol == "$take2(f:core::Func<.i32,.i32>)@.i32"),
                string.Join(", ", module.LocalSymbols.OfType<BilSimpleMemberDeclaration>()
                    .Select(d => d.Symbol)));
            // BilWriter 文本经 BilReader 回读 + 验证器零错误（VM 装载前置）
            // 展示黄金会把全部 UUID 归一成同名；实际回读必须保留各闭包身份。
            var reparsed = BilReader.Read(BilWriter.Write(module));
            BilTestHarness.CheckBilValid("回读模块验证器零错误", reparsed);
            var result = BilVm.Run(reparsed);
            CheckOk("回读模块 VM 运行", result);
            CheckI32("take2(lambda) = 42", result, 42);
        }

        // 用户泛型函数形参含函数泛型参数的构造类型（.array<.generic<T>> /
        // .nullable<.generic<T>>）：调用点帧未绑 .generic.T，cast 不得抛
        // 「无法解析泛型占位」；纯 .generic<T> 形参作对照。
        private static void TestGenericFunctionConstructedParams()
        {
            var firstI32 = Run(
                "import core.collections.*\n" +
                "pub func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 42\n" +
                "    a[1] = 1\n" +
                "    a[2] = 2\n" +
                "    return firstOf\\<i32>(a)\n" +
                "}\n");
            CheckOk("firstOf<i32>", firstI32);
            CheckI32("firstOf<i32> = 42", firstI32, 42);

            var firstString = Run(
                "import core.collections.*\n" +
                "pub func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOfElements\\<String>(\"ok\", \"no\")\n" +
                "    core.io.Console.println(firstOf\\<String>(a))\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("firstOf<String>", firstString);
            CaseAssertions.Check("firstOf<String> stdout", firstString.Stdout, "ok\n");
            CheckI32("firstOf<String> 返回 0", firstString, 0);

            var nested = Run(
                "import core.collections.*\n" +
                "pub func firstNested\\<T>(arr: Array\\<Array\\<T>>): Array\\<T> {\n" +
                "    return (arr[0] as Array\\<T>)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var row = arrayOf\\<i32>(2)\n" +
                "    row[0] = 7\n" +
                "    row[1] = 8\n" +
                "    var outer = arrayOf\\<Array\\<i32>>(1)\n" +
                "    outer[0] = row\n" +
                "    var got = firstNested\\<i32>(outer)\n" +
                "    return got[0] if? 0\n" +
                "}\n");
            CheckOk("嵌套 Array<Array<T>> 形参", nested);
            CheckI32("firstNested = 7", nested, 7);

            var nullable = Run(
                "pub func unwrapOr\\<T>(v: T?, fallback: T): T {\n" +
                "    if (v == null) {\n" +
                "        return fallback\n" +
                "    }\n" +
                "    return v\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var n: i32? = 9\n" +
                "    core.io.Console.println(\"${unwrapOr\\<i32>(n, 0)}\")\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("nullable<T> 形参", nullable);
            CaseAssertions.Check("unwrapOr(9, 0) 打印", nullable.Stdout, "9\n");
            CheckI32("nullable main 返回 0", nullable, 0);

            var id = Run(
                "pub func id\\<T>(x: T): T { return x }\n" +
                "pub func main(): i32 { return id\\<i32>(42) }\n");
            CheckOk("纯 .generic<T> 形参对照", id);
            CheckI32("id<i32>(42) = 42", id, 42);

            var typeOfT = Run(
                "pub func matchesT\\<T>(x: T): bool {\n" +
                "    var t = typeOf(T)\n" +
                "    return (x is t)\n" +
                "}\n" +
                "pub func main(): bool {\n" +
                "    return matchesT\\<i32>(1)\n" +
                "}\n");
            CheckOk("typeOf(T) 绑定帧 getid.type 解析", typeOfT);
            CheckBool("1 is typeOf(T)", typeOfT, true);
        }

        // 固定泛型推断端到端：无显式实参调用 + 泛型 operator 运算符位置
        private static void TestGenericInferenceEndToEnd()
        {
            var first = Run(
                "import core.collections.*\n" +
                "pub func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<i32>(3)\n" +
                "    a[0] = 42\n" +
                "    a[1] = 1\n" +
                "    a[2] = 2\n" +
                "    return firstOf(a)\n" +
                "}\n");
            CheckOk("firstOf 推断+运行", first);
            CheckI32("firstOf(a) = 42", first, 42);

            var plus = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus\\<TAnother>(another: TAnother): Vec {\n" +
                "        var w = another as Vec\n" +
                "        return new Vec((x + w.x))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    var c = a + b\n" +
                "    return c.x\n" +
                "}\n");
            CheckOk("泛型 plus 运算符位置", plus);
            CheckI32("1+2 = 3", plus, 3);

            var compare = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator compareTo\\<TAnother>(another: TAnother): ComparisonResult {\n" +
                "        var w = another as Vec\n" +
                "        if ((x < w.x)) { return .LesserThanAnother }\n" +
                "        if ((x > w.x)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(1)\n" +
                "    var b = new Vec(2)\n" +
                "    if ((a < b)) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("泛型 compareTo 运算符位置", compare);
            CheckI32("1 < 2", compare, 1);

            var compound = Run(
                "class Vec {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "    pub operator plus\\<TAnother>(another: TAnother): Vec {\n" +
                "        var w = another as Vec\n" +
                "        return new Vec((x + w.x))\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Vec(10)\n" +
                "    var b = new Vec(5)\n" +
                "    a += b\n" +
                "    return a.x\n" +
                "}\n");
            CheckOk("泛型 plus 复合赋值", compound);
            CheckI32("10+=5 = 15", compound, 15);

            var unary = Run(
                "class Bits {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator opposite(): Bits { return new Bits((0 - v)) }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new Bits(3)\n" +
                "    return (-a).v\n" +
                "}\n");
            CheckOk("非泛型一元对照", unary);
            CheckI32("-3", unary, -3);

            var dispatch = Run(
                "class Box {\n" +
                "    pub var tag: i32\n" +
                "    pub init(_ -> tag) { }\n" +
                "    pub operator plus\\<TAnother>(another: TAnother): i32 {\n" +
                "        if ((another is i32)) { return (tag + (another as i32)) }\n" +
                "        if ((another is String)) { return (tag + 100) }\n" +
                "        return tag\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box(1)\n" +
                "    var a = b + 2\n" +
                "    var c = b + \"x\"\n" +
                "    return (a + c)\n" +
                "}\n");
            CheckOk("推断类型运行时派发", dispatch);
            CheckI32("1+2 与 1+String → 3+101 = 104", dispatch, 104);
        }

        // 泛型参数经约束的成员/运算符：静态按界定型，运行时按实际 typeid 派发
        private static void TestGenericParamConstraintDispatch()
        {
            var sum = Run(
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub class A implements Addable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new A((n + ((other as A).n)))\n" +
                "    }\n" +
                "}\n" +
                "pub class B implements Addable {\n" +
                "    pub var n: i32\n" +
                "    pub init(_ -> n) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new B(((n + ((other as B).n)) + 100))\n" +
                "    }\n" +
                "}\n" +
                "pub func sum\\<T extends Addable>(a: T, b: T): Addable { return a + b }\n" +
                "pub func main(): i32 {\n" +
                "    var x = (sum\\<A>(new A(1), new A(2)) as A).n\n" +
                "    var y = (sum\\<B>(new B(3), new B(4)) as B).n\n" +
                "    return (x + y)\n" +
                "}\n");
            CheckOk("sum<T extends Addable> 动态派发", sum);
            CheckI32("A:1+2=3 与 B:3+4+100=107 → 110", sum, 110);

            var cmp = Run(
                "pub interface Ordered { pub operator compareTo(other: Ordered): ComparisonResult }\n" +
                "pub class N implements Ordered {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator compareTo(other: Ordered): ComparisonResult {\n" +
                "        var w = (other as N).v\n" +
                "        if ((v < w)) { return .LesserThanAnother }\n" +
                "        if ((v > w)) { return .GreaterThanAnother }\n" +
                "        return .Equal\n" +
                "    }\n" +
                "}\n" +
                "pub func clamp\\<T extends Ordered>(x: T, lo: T, hi: T): T {\n" +
                "    if ((x < lo)) { return lo }\n" +
                "    if ((x > hi)) { return hi }\n" +
                "    return x\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = new N(0)\n" +
                "    var b = new N(5)\n" +
                "    var c = new N(10)\n" +
                "    var r = clamp\\<N>(a, b, c)\n" +
                "    return r.v\n" +
                "}\n");
            CheckOk("clamp 经 compareTo 约束", cmp);
            CheckI32("clamp(0,5,10)=5", cmp, 5);

            var eq = Run(
                "pub interface Equatable { pub operator equals(other: Equatable): bool }\n" +
                "pub class Tag implements Equatable {\n" +
                "    pub var id: i32\n" +
                "    pub init(_ -> id) { }\n" +
                "    pub operator equals(other: Equatable): bool {\n" +
                "        return (id == ((other as Tag).id))\n" +
                "    }\n" +
                "}\n" +
                "pub func same\\<T extends Equatable>(a: T, b: T): bool { return a == b }\n" +
                "pub func main(): i32 {\n" +
                "    if (same\\<Tag>(new Tag(7), new Tag(7))) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("== 经 equals 约束", eq);
            CheckI32("same Tag(7)", eq, 1);

            var sumAll = Run(
                "import core.collections.*\n" +
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub class N implements Addable {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new N((v + ((other as N).v)))\n" +
                "    }\n" +
                "}\n" +
                "pub func sumAll\\<T extends Addable>(arr: Array\\<T>): Addable {\n" +
                "    var acc: Addable = (arr[0] as T)\n" +
                "    var i = 1\n" +
                "    while ((i < arr.length)) {\n" +
                "        acc = (acc + (arr[i] as T))\n" +
                "        i = (i + 1)\n" +
                "    }\n" +
                "    return acc\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var a = arrayOf\\<N>(3)\n" +
                "    a[0] = new N(10)\n" +
                "    a[1] = new N(20)\n" +
                "    a[2] = new N(12)\n" +
                "    return ((sumAll\\<N>(a) as N).v)\n" +
                "}\n");
            CheckOk("sumAll Array<T> 循环累加", sumAll);
            CheckI32("10+20+12=42", sumAll, 42);

            var ts = Run(
                "pub func show\\<T>(x: T): String { return x.toString() }\n" +
                "pub func main(): i32 {\n" +
                "    core.io.Console.println(show\\<i32>(42))\n" +
                "    return 0\n" +
                "}\n");
            CheckOk("无约束 T toString 端到端", ts);
            CaseAssertions.Check("toString(42)", ts.Stdout, "42\n");

            var pair = Run(
                "pub interface Addable { pub operator plus(other: Addable): Addable }\n" +
                "pub class N implements Addable {\n" +
                "    pub var v: i32\n" +
                "    pub init(_ -> v) { }\n" +
                "    pub operator plus(other: Addable): Addable {\n" +
                "        return new N((v + ((other as N).v)))\n" +
                "    }\n" +
                "}\n" +
                "pub class Pair\\<T extends Addable> {\n" +
                "    pub var a: T\n" +
                "    pub var b: T\n" +
                "    pub init(_ -> a, _ -> b) { }\n" +
                "    pub func add(): Addable { return a + b }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Pair\\<N>(new N(3), new N(4))\n" +
                "    return ((p.add() as N).v)\n" +
                "}\n");
            CheckOk("Pair<T extends Addable> 内 +", pair);
            CheckI32("3+4=7", pair, 7);
        }

        // getid.type 对含 .generic< 的构造类型走 ResolveTypeRef：绑定帧
        // 把 .array<.generic<$.generic.T>> 物化为 .array<.i32>
        private static void TestGetIdTypeResolvesNestedGeneric()
        {
            var module = new BilModule();
            var probe = new BilFunction("$probe()@.typeid");
            probe.Args.Add(new BilArgDeclaration(".return", ".typeid"));
            probe.Args.Add(new BilArgDeclaration(".generic.T", ".typeid"));
            probe.Vars.Add(new BilVarDeclaration(".typeid", "t"));
            var entry = new BilBlock("entry", BilBlockModifier.Entrypoint);
            entry.Instructions.Add(new GetIdTypeInstruction(
                BilOp.Type(".array<.generic<$.generic.T>>"), BilOp.Var("t")));
            entry.Instructions.Add(new RetInstruction(BilOp.Var("t")));
            probe.Blocks.Add(entry);
            module.Functions.Add(probe);

            var result = RunPrepared(module, "$probe()@.typeid",
                new[] { new VmTypeId(".i32") });
            CheckOk("getid.type 解析嵌套泛型占位", result);
            CaseAssertions.CheckTrue("物化为 .array<.i32>",
                result.ReturnValue is VmTypeId id && id.TypeSymbol == ".array<.i32>",
                result.ReturnValue?.ToStandardText() ?? "<null>");
        }

    }
}
