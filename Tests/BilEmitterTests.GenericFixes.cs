using System.Linq;
using RigiCompiler.Bil;

namespace RigiCompiler.Tests
{
    // Lowering/BIL 泛型修复：构造类型继承泛型类索引运算符；类级泛型
    // 参数打进实例方法帧（$.generic.TItem 在 .args 中声明）。
    public static partial class BilEmitterTests
    {
        private static void TestGenericIndexOperatorEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "pub class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "    pub operator getAtIndex(index: i32): T? { return item }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var b = new Box\\<i32>(7)\n" +
                "    return b[0] if? 0\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（泛型类 getAtIndex）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（Box<.i32> 继承定义级 getAtIndex）", module);
            CaseAssertions.CheckTrue("Box$$getAtIndex 声明存在",
                module.LocalSymbols.OfType<BilTypeDeclaration>()
                    .SelectMany(t => t.Members.OfType<BilSimpleMemberDeclaration>())
                    .Any(m => m.Symbol.Contains("Box$$getAtIndex")));

            var (unitNeg, moduleNeg, _) = BilTestHarness.EmitBilUnit(
                "pub class Plain\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item)\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    var p = new Plain\\<i32>(1)\n" +
                "    return p.item\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（无索引运算符对照）", unitNeg);
            BilTestHarness.CheckBilValid("无 getAtIndex 的泛型类仍可验证", moduleNeg);
        }

        private static void TestClassGenericParamFrameEmission()
        {
            var (unit, module, _) = BilTestHarness.EmitBilUnit(
                "import core.collections.*\n" +
                "pub class Repo\\<TItem> {\n" +
                "    pub func make(cap: i32): Array\\<TItem> {\n" +
                "        return arrayOf\\<TItem>(cap)\n" +
                "    }\n" +
                "    pub func mix\\<U>(x: TItem, y: U): U { return y }\n" +
                "}\n" +
                "pub class Outer\\<T> {\n" +
                "    pub class Inner {\n" +
                "        pub func id(x: T): T { return x }\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const r = new Repo\\<i32>()\n" +
                "    const a = r.make(3)\n" +
                "    a[0] = 7\n" +
                "    return (((a[0] if? 0) + r.mix\\<i32>(1, 2)))\n" +
                "}\n");
            CheckNoErrors("全管线无诊断（类泛型参数进方法帧）", unit);
            BilTestHarness.CheckBilValid("验证器零错误（$.generic.TItem 已声明）", module);

            var makeFn = module.Functions.First(f => f.Symbol.Contains("$make("));
            CaseAssertions.CheckTrue("make .args 含 .generic.TItem",
                makeFn.Args.Any(a => a.Name == ".generic.TItem" && a.TypeRef == ".typeid"));

            var mixFn = module.Functions.First(f => f.Symbol.Contains("$mix("));
            CaseAssertions.CheckTrue("mix 同时持有类级 TItem 与方法级 U",
                mixFn.Args.Any(a => a.Name == ".generic.TItem")
                && mixFn.Args.Any(a => a.Name == ".generic.U"));

            var innerFn = module.Functions.First(f => f.Symbol.Contains(".Inner$id("));
            CaseAssertions.CheckTrue("嵌套类方法帧含外层 T",
                innerFn.Args.Any(a => a.Name == ".generic.T" && a.TypeRef == ".typeid"));
        }
    }
}
