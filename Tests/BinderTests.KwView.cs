using System.Linq;

namespace RigiCompiler.Tests
{
    // BinderTests 的 kwargs 体内视角部分（S9d 修正，BIL §7.1）：
    // named 值可变参数体内定型 Array\<Pair\<String, T\>\>（元素 = core::Pair
    // 构造，名 String + 值 T）——§7.1 ABI .array<.pair<.string, .any>> 是
    // 名+值对序列，体内元素访问必须看到 Pair（名字信息不丢失）。
    // 覆盖：参数引用定型（PathVisitors 首段参数）、调用链头同款包装
    // （CallVisitors.BindInstanceCallForm）、元素成员访问定型链（Pair 的
    // key/value 经 BindInstanceFieldAccess 同款 SymbolLookup 查询 + 宿主
    // 代入解析）、位置包视角不变、泛型具名包（值槽为泛型参数）。
    // 注：Array\<T\> 的 getAtIndex/setAtIndex 索引运算符已在 bootstrap
    // 自举，语言级 options[0] 端到端经 P4a 装箱/拆箱桥接全通（见
    // BilEmitterTests.TestVarArgsIndexBoxingEmission）；此处仍以绑定设施
    // 直证元素类型与成员解析链。
    public static partial class BinderTests
    {
        private static void TestKwArgsBodyView()
        {
            TestHarness.Section("P3 kwargs Body View (named variadic)");

            // ===== 1. 参数引用定型：Array\<Pair\<String, String\>\>
            // （PathVisitors 首段参数分支）=====
            var (unit, bodies) = BindUnitWithStdlib(
                "func f(options: named String...): Any { return options }\n");
            CheckNoErrors("无诊断（kwargs 参数引用）", unit);
            var reference = (BoundValueReferenceExpression)((BoundReturnStatement)
                BodyOf(bodies, "f").Body.Statements[0]).Value!;
            var pairDefinition = unit.Symbols.GlobalNamespace.ChildNamespaces
                .Single(n => n.Name == "core").Types.Single(t => t.Name == "Pair");
            TestHarness.CheckTrue("kwargs 体内视角 = Array<Pair<String, String>>",
                reference.Type is TypeSymbol { ConstructedFrom: not null } arrayType
                && ReferenceEquals(arrayType.ConstructedFrom,
                    unit.Symbols.Bootstrap.ArrayDefinition)
                && arrayType.TypeArguments![0] is TypeSymbol { ConstructedFrom: not null } pairType
                && ReferenceEquals(pairType.ConstructedFrom, pairDefinition)
                && ReferenceEquals(pairType.TypeArguments![0], unit.Symbols.Bootstrap.String)
                && ReferenceEquals(pairType.TypeArguments![1], unit.Symbols.Bootstrap.String));

            // ===== 2. 元素成员访问定型链：Pair\<String, String\> 的 key/value
            // 字段经 SymbolLookup（BindInstanceFieldAccess 同款查询 + 宿主
            // 代入）解析为 String——options[0].key/.value 的绑定路径直证
            // （语言级 options[0] 端到端见 BilEmitterTests 装箱/拆箱用例）=====
            var element = (TypeSymbol)((TypeSymbol)reference.Type).TypeArguments![0];
            var keyField = SymbolLookup.FindInstanceField(element, "key");
            var valueField = SymbolLookup.FindInstanceField(element, "value");
            TestHarness.CheckTrue("元素 key/value 字段解析（宿主代入 → String）",
                keyField != null && valueField != null
                && ReferenceEquals(SymbolLookup.SubstituteFieldType(keyField, element,
                    unit.Symbols),
                    unit.Symbols.Bootstrap.String)
                && ReferenceEquals(SymbolLookup.SubstituteFieldType(valueField, element,
                    unit.Symbols),
                    unit.Symbols.Bootstrap.String));

            // ===== 3. 调用链头同款包装（CallVisitors.BindInstanceCallForm）：
            // options.describe() 的 receiver = Array\<Pair\<String, String\>\> =====
            var (unit3, bodies3) = BindUnitWithStdlib(
                "pub ext func Any.describe(): String { return \"x\" }\n" +
                "func f(options: named String...): String { return options.describe() }\n");
            CheckNoErrors("无诊断（kwargs 链头调用）", unit3);
            var chainCall = (BoundInstanceCallExpression)((BoundReturnStatement)
                BodyOf(bodies3, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("kwargs 链头 receiver = Array<Pair<String, String>>",
                chainCall.Receiver.Type is TypeSymbol { ConstructedFrom: not null } chainArray
                && ReferenceEquals(chainArray.ConstructedFrom,
                    unit3.Symbols.Bootstrap.ArrayDefinition)
                && chainArray.TypeArguments![0] is TypeSymbol { ConstructedFrom: not null } chainPair
                && chainPair.ConstructedFrom.Name == "Pair"
                && ReferenceEquals(chainPair.TypeArguments![0], unit3.Symbols.Bootstrap.String)
                && ReferenceEquals(chainPair.TypeArguments![1], unit3.Symbols.Bootstrap.String));

            // ===== 4. 位置包视角不变：Array\<i32\>（与 .array<.any> 装箱
            // 往返自洽，不随 kwargs 修正改动）=====
            var (unit4, bodies4) = BindUnitWithStdlib(
                "func g(nums: i32...): Any { return nums }\n");
            CheckNoErrors("无诊断（vargs 参数引用）", unit4);
            var vargsReference = (BoundValueReferenceExpression)((BoundReturnStatement)
                BodyOf(bodies4, "g").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("vargs 体内视角保持 Array<i32>",
                vargsReference.Type is TypeSymbol { ConstructedFrom: not null } vargsArray
                && ReferenceEquals(vargsArray.ConstructedFrom,
                    unit4.Symbols.Bootstrap.ArrayDefinition)
                && ReferenceEquals(vargsArray.TypeArguments![0], unit4.Symbols.Bootstrap.Int32));

            // ===== 5. 泛型具名包：Array\<Pair\<String, TValues\>\>
            // （值槽为泛型参数，S9a 引用相等身份）=====
            var (unit5, bodies5) = BindUnitWithStdlib(
                "func u\\<named TValues...>(configs: named TValues...): Any { return configs }\n");
            CheckNoErrors("无诊断（泛型 kwargs 参数引用）", unit5);
            var genericReference = (BoundValueReferenceExpression)((BoundReturnStatement)
                BodyOf(bodies5, "u").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("泛型 kwargs 视角 = Array<Pair<String, TValues>>",
                genericReference.Type is TypeSymbol { ConstructedFrom: not null } genericArray
                && ReferenceEquals(genericArray.ConstructedFrom,
                    unit5.Symbols.Bootstrap.ArrayDefinition)
                && genericArray.TypeArguments![0] is TypeSymbol { ConstructedFrom: not null }
                    genericPair
                && genericPair.ConstructedFrom.Name == "Pair"
                && ReferenceEquals(genericPair.TypeArguments![0], unit5.Symbols.Bootstrap.String)
                && genericPair.TypeArguments![1] is GenericParameterSymbol { Name: "TValues" });
        }
    }
}
