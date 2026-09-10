namespace RigiCompiler.Tests
{
    // MW11d Phase A：@Terminal 局部组合、@Internal 非法目标、with 约束正反。
    public static partial class BinderTests
    {
        private static void TestMw11dSerializationFront()
        {
            TestHarness.Section("P3 MW11d @Terminal 局部组合");

            var okInner = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n@Terminal\nwrapper TermV { }\n" +
                "@WrapperTarget(.Value)\nwrapper OtherV { }\n" +
                "func f() {\n" +
                "    @OtherV\n" +
                "    @TermV\n" +
                "    var x: i32 = 0\n" +
                "}\n");
            CheckNoErrors("局部：terminal 最内层合法", okInner.Unit);

            var okOnly = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n@Terminal\nwrapper TermV { }\n" +
                "func f() {\n" +
                "    @TermV\n" +
                "    var x: i32 = 0\n" +
                "}\n");
            CheckNoErrors("局部：terminal 为唯一 wrapper 合法", okOnly.Unit);

            var okCombo = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\nwrapper A { }\n" +
                "@WrapperTarget(.Value)\nwrapper B { }\n" +
                "func f() {\n" +
                "    @A\n" +
                "    @B\n" +
                "    var x: i32 = 0\n" +
                "}\n");
            CheckNoErrors("局部：非 terminal 任意组合合法", okCombo.Unit);

            var bad = BindUnit(
                "@WrapperTarget(.Value)\n@Terminal\nwrapper TermV { }\n" +
                "@WrapperTarget(.Value)\nwrapper OtherV { }\n" +
                "func f() {\n" +
                "    @TermV\n" +
                "    @OtherV\n" +
                "    var x: i32 = 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("局部：terminal 内层再嵌套",
                bad.Unit.Diagnostics,
                "TermV is terminal and cannot contain another modifier.");

            var badOnLocal = BindUnit(
                "func f() {\n" +
                "    @Terminal\n" +
                "    var x: i32 = 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("@Terminal 挂局部变量",
                badOnLocal.Unit.Diagnostics,
                "@Terminal can only be applied to wrapper declarations");

            TestHarness.Section("P3 MW11d with Serializable/SerializationBase");

            var withBaseOk = BindUnitWithStdlib(
                "import core.SerializationBase\n" +
                "func take\\<T with SerializationBase>(x: T) { }\n" +
                "func f() { take\\<i32>(0) }\n");
            CheckNoErrors("with SerializationBase 填入 i32 合法", withBaseOk.Unit);

            var withBaseBad = BindUnitWithStdlib(
                "import core.SerializationBase\n" +
                "class Plain { pub init() }\n" +
                "func take\\<T with SerializationBase>(x: T) { }\n" +
                "func f() { take\\<Plain>(new Plain()) }\n");
            TestHarness.CheckSemanticError("with SerializationBase 填入普通 class 拒绝",
                withBaseBad.Unit.Diagnostics, "does not satisfy the 'With SerializationBase'");

            var withSerOk = BindUnitWithStdlib(
                "import core.serialization.Serializable\n" +
                "@Serializable\n" +
                "class Marked { pub var n: i32 = 0 }\n" +
                "func take\\<T with Serializable>(x: T) { }\n" +
                "func f() { take\\<Marked>(new Marked()) }\n");
            CheckNoErrors("with Serializable 填入 @Serializable 类合法", withSerOk.Unit);

            var withSerBad = BindUnitWithStdlib(
                "import core.serialization.Serializable\n" +
                "class Plain { pub init() }\n" +
                "func take\\<T with Serializable>(x: T) { }\n" +
                "func f() { take\\<Plain>(new Plain()) }\n");
            TestHarness.CheckSemanticError("with Serializable 填入普通 class 拒绝",
                withSerBad.Unit.Diagnostics, "does not satisfy the 'With Serializable'");

            TestHarness.Section("P3 MW11d-B1 List/Map/Parcel with SerializationBase");

            var withList = BindUnitWithStdlib(
                "import core.SerializationBase\n" +
                "import core.collections.List\n" +
                "func take\\<T with SerializationBase>(x: T) { }\n" +
                "func f() { take\\<List\\<i32>>(new List\\<i32>()) }\n");
            CheckNoErrors("with SerializationBase 填入 List<i32> 合法", withList.Unit);

            var withMap = BindUnitWithStdlib(
                "import core.SerializationBase\n" +
                "import core.collections.Map\n" +
                "func take\\<T with SerializationBase>(x: T) { }\n" +
                "func f() { take\\<Map\\<String, i32>>(new Map\\<String, i32>()) }\n");
            CheckNoErrors("with SerializationBase 填入 Map<String, i32> 合法", withMap.Unit);

            // Any.hash 承诺（Map 键判等，用户裁定）：无约束泛型的有效成员类型
            // 是 Any——hash 与 toString 同为可解析承诺（Map.keysEqual 的绑定前提）
            var anyHashOnK = BindUnitWithStdlib(
                "func probe\\<K>(x: K): i64 { return x.hash() }\n");
            CheckNoErrors("无约束 K 上 x.hash() 解析成功", anyHashOnK.Unit);

            var withParcel = BindUnitWithStdlib(
                "import core.SerializationBase\n" +
                "import core.serialization.Parcel\n" +
                "func take\\<T with SerializationBase>(x: T) { }\n" +
                "func f() { take\\<Parcel>(new Parcel(\"T\")) }\n");
            CheckNoErrors("with SerializationBase 填入 Parcel 合法", withParcel.Unit);

            var withListEnum = BindUnitWithStdlib(
                "import core.SerializationBase\n" +
                "import core.collections.ListEnumerator\n" +
                "func take\\<T with SerializationBase>(x: T) { }\n" +
                "func f(e: ListEnumerator\\<i32>) { take\\<ListEnumerator\\<i32>>(e) }\n");
            TestHarness.CheckSemanticError("with SerializationBase 填入 ListEnumerator 拒绝",
                withListEnum.Unit.Diagnostics, "does not satisfy the 'With SerializationBase'");

            TestHarness.Section("P3 MW11d-B2 toParcel/deepCopy 绑定");

            var deep = BindUnitWithStdlib(
                "import core.serialization.*\n" +
                "@Serializable\n" +
                "class Marked { pub var n: i32 = 0 }\n" +
                "func clone\\<T with Serializable>(x: T): T { return deepCopy(x) }\n" +
                "func f(): i32 {\n" +
                "    var m = new Marked()\n" +
                "    var p = m:Serializable.toParcel()\n" +
                "    var c = clone\\<Marked>(m)\n" +
                "    return c.n\n" +
                "}\n");
            CheckNoErrors("@Serializable 宿主 toParcel/deepCopy/clone 绑定", deep.Unit);
            var marked = deep.Unit.Symbols.GlobalNamespace.Types
                .FirstOrDefault(t => t.Name == "Marked");
            TestHarness.CheckTrue("@Serializable 宿主已合成 toParcel",
                marked != null && marked.Methods.Any(m =>
                    m.Name == RigiCompiler.Bil.BilSpellings.ToParcelMethodName));
            TestHarness.CheckTrue("@Serializable 宿主已合成 fromParcel",
                marked != null && marked.Methods.Any(m =>
                    m.Name == RigiCompiler.Bil.BilSpellings.FromParcelMethodName));

            TestHarness.Section("P3 SB 与 Serializable 必须独立声明");

            var implied = BindUnitWithStdlibSources(true,
                "namespace core.serialization\n" +
                "@SerializationBase\n" +
                "pub class BaseMarked { pub var n: i32 = 0 }\n");
            CheckNoErrors("base-only 宿主绑定无诊断", implied.Unit);
            var baseMarked = implied.Unit.Symbols.GlobalNamespace.ChildNamespaces
                .First(n => n.Name == "core").ChildNamespaces
                .First(n => n.Name == "serialization").Types
                .FirstOrDefault(t => t.Name == "BaseMarked");
            TestHarness.CheckTrue("base-only 宿主不合成 Serializable 方法",
                baseMarked != null && !baseMarked.Methods.Any(m =>
                    m.Name == RigiCompiler.Bil.BilSpellings.ToParcelMethodName
                    || m.Name == RigiCompiler.Bil.BilSpellings.FromParcelMethodName));

            var withSerImplied = BindUnitWithStdlibSources(true,
                "namespace core.serialization\n" +
                "@SerializationBase\n" +
                "class BaseTake { pub var n: i32 = 0 }\n" +
                "func take\\<T with Serializable>(x: T) { }\n" +
                "func f() { take\\<BaseTake>(new BaseTake()) }\n");
            TestHarness.CheckSemanticError("只有 SB 的类型不能满足 Serializable", withSerImplied.Unit.Diagnostics,
                "does not satisfy the 'With Serializable'");

            var withSerI32 = BindUnitWithStdlib(
                "import core.serialization.Serializable\n" +
                "func take\\<T with Serializable>(x: T) { }\n" +
                "func f() { take\\<i32>(0) }\n");
            CheckNoErrors("with Serializable 填入 i32 合法（源码双重应用）",
                withSerI32.Unit);

            var withSerViaParameter = BindUnitWithStdlib(
                "import core.serialization.*\n" +
                "func takeSer\\<T with Serializable>(x: T) { }\n" +
                "func forward\\<U with SerializationBase>(x: U) { takeSer\\<U>(x) }\n");
            TestHarness.CheckSemanticError("只有 SB 的泛型约束不能冒充 Serializable",
                withSerViaParameter.Unit.Diagnostics, "does not satisfy the 'With Serializable'");
            var fieldProof = BindUnitWithStdlib(
                "import core.serialization.*\n" +
                "@Serializable\nclass Envelope\\<T with SerializationBase> {\n" +
                " pub var value: T\n pub init(_ -> value)\n}\n");
            TestHarness.CheckSemanticError("仅 SB 泛型字段不能取得 Serializable 编码能力",
                fieldProof.Unit.Diagnostics, "不可序列化");
            var unknownBase = BindUnitWithStdlibSources(true,
                "namespace core.serialization\n@SerializationBase\n@Serializable\n" +
                "class UnknownBase { pub var value: i32 = 0 }\n");
            TestHarness.CheckSemanticError("未知 SB 编码器拒绝而非复制引用",
                unknownBase.Unit.Diagnostics, "未实现对应的 Serializable 编解码");
        }
    }
}
