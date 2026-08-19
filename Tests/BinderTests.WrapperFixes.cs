using System.Linq;

namespace RigiCompiler.Tests
{
    // Wrapper 簇修复：§14.5 语句位 void 调用、§14.2/§14.3 get 禁 inner、
    // §14.2 子类 wrapper 对继承成员的形状校验。
    public static partial class BinderTests
    {
        private static void TestWrapperPlaceVoidStatement()
        {
            TestHarness.Section("P3 Wrapper Place Void Statement (§14.5)");

            var (unit, bodies) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub var level: String\n" +
                "    pub init(_ -> level)\n" +
                "    pub func bump() { level = \"X\" }\n" +
                "    pub func bumpBy(n: i32) { }\n" +
                "    pub func bumpDef(n: i32 = 1) { }\n" +
                "    pub func describe(): String { return level }\n" +
                "}\n" +
                "@Logged(\"INFO\")\n" +
                "pub class Svc { pub init() }\n" +
                "pub func f(s: Svc) {\n" +
                "    s:Logged.bump()\n" +
                "    s:Logged.bumpBy(2)\n" +
                "    s:Logged.bumpDef()\n" +
                "    s:Logged.describe()\n" +
                "}\n");
            CheckNoErrors("wrapper place 语句位 void/带参/默认值/有返回 均合法", unit);
            var body = BoundDescribe.Body(BodyOf(bodies, "f"));
            TestHarness.CheckTrue("void bump 落 InstCallStmt",
                body.Contains("InstCallStmt(bump, WrapperPlace(Param(s,Svc), Logged), [])"));
            TestHarness.CheckTrue("void bumpBy 带参落 InstCallStmt",
                body.Contains("InstCallStmt(bumpBy,"));
            TestHarness.CheckTrue("void bumpDef 默认值落 InstCallStmt",
                body.Contains("InstCallStmt(bumpDef,"));
            TestHarness.CheckTrue("有返回 describe 仍是表达式语句",
                body.Contains("InstCall(describe, WrapperPlace(Param(s,Svc), Logged), [], String)"));

            var (unitVal, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    pub func bump() { }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Svc { pub init() }\n" +
                "pub func g(s: Svc): i32 {\n" +
                "    var x = s:Logged.bump()\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("值位置 void 仍拒绝",
                unitVal.Diagnostics, "has no result (void) and cannot be used as a value");
        }

        private static void TestGetProxyInnerForbidden()
        {
            TestHarness.Section("P3 Get Proxy Inner Forbidden (§14.2/§14.3)");

            var (unitVal, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Peek {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue {\n" +
                "        return inner(value)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    @Peek()\n" +
                "    const x: i32 = 1\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("Value .proxy.get 禁 inner",
                unitVal.Diagnostics, "cannot call inner(...)");

            var (unitWild, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Peek {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.*\\<TValue>(symbol: String, value: TValue): TValue {\n" +
                "        return inner(symbol=symbol, value=value)\n" +
                "    }\n" +
                "}\n" +
                "@Peek\n" +
                "pub class Svc {\n" +
                "    pub var name: String\n" +
                "    pub init(_ -> name)\n" +
                "}\n");
            TestHarness.CheckSemanticError("Entity .proxy.get.* 禁 inner",
                unitWild.Diagnostics, "cannot call inner(...)");

            var (unitSpec, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Peek {\n" +
                "    pub init()\n" +
                "    operator .proxy.get.name\\<TField>(value: TField): TField {\n" +
                "        return inner(value)\n" +
                "    }\n" +
                "}\n" +
                "@Peek\n" +
                "pub class Svc {\n" +
                "    pub var name: String\n" +
                "    pub init(_ -> name)\n" +
                "}\n");
            TestHarness.CheckSemanticError("Entity .proxy.get.<名> 禁 inner",
                unitSpec.Diagnostics, "cannot call inner(...)");

            var (unitSet, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub wrapper Clamp {\n" +
                "    pub init()\n" +
                "    operator .proxy.get\\<TValue>(value: TValue): TValue { return value }\n" +
                "    operator .proxy.set\\<TValue>(value: TValue) { inner(value) }\n" +
                "}\n" +
                "pub func f() {\n" +
                "    @Clamp()\n" +
                "    var x: i32 = 1\n" +
                "    x = 2\n" +
                "}\n");
            CheckNoErrors("set proxy 仍可 inner；get 正例不调 inner", unitSet);

            var (unitMeth, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 { return inner(n) }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Svc {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n");
            CheckNoErrors("方法 proxy 仍可 inner", unitMeth);
        }

        private static void TestSubclassWrapperInheritedShape()
        {
            TestHarness.Section("P3 Subclass Wrapper Inherited Member Shape (§14.2)");

            var (unitOk, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 { return inner(n) }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Child : Base { pub init() }\n");
            CheckNoErrors("子类 wrapper specific 对继承方法形状匹配", unitOk);

            var (unitBad, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): String { return inner(n) }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Child : Base { pub init() }\n");
            TestHarness.CheckSemanticError("子类 wrapper specific 对继承方法形状不符",
                unitBad.Diagnostics, "does not match the shape");

            var (unitBoth, _) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub init()\n" +
                "    operator .proxy.ping(n: i32): i32 { return inner(n) }\n" +
                "    operator .proxy.*\\<named TNamedArgs..., TUnnamedArgs..., TReturn>(\n" +
                "        symbol: String, namedArgs: named TNamedArgs..., " +
                "unnamedArgs: TUnnamedArgs...\n" +
                "    ): TReturn {\n" +
                "        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)\n" +
                "    }\n" +
                "}\n" +
                "pub open class Base {\n" +
                "    pub init()\n" +
                "    pub func ping(n: i32): i32 { return n }\n" +
                "    pub func other(): i32 { return 0 }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Child : Base { pub init() }\n");
            CheckNoErrors("specific + wildcard 共存且覆盖继承成员", unitBoth);
        }
    }
}
