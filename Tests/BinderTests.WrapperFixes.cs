using System.Linq;

namespace RigiCompiler.Tests
{
    // Wrapper 簇修复：§14.5 语句位 void 调用、§14.2/§14.3 get 禁 inner、
    // §14.2 子类 wrapper 对继承成员的形状校验、§14.4 Method wrapper
    // specific `.proxy.call` 形状全等。
    public static partial class BinderTests
    {
        internal static IReadOnlyList<string> GroupLabels(string group)
            => group.Equals("WRAP-001-review", System.StringComparison.OrdinalIgnoreCase)
                ? [nameof(TestGenericParameterFieldInitializers), nameof(TestForwardDefaultConstructionDa)]
                : [nameof(TestGenericParameterFieldInitializers), nameof(TestForwardDefaultConstructionDa),
                    nameof(TestWrapperFieldInitializers), nameof(TestWrapperDefaultConstructionDa)];

        // WRAP-001：wrapper 自身初值必须走真实初始化器，DA 与普通实体一致。


        private static void TestForwardDefaultConstructionDa()
        {
            CompilerTestTools.Section("P3 前向默认构造 DA（WRAP-001 review）");
            var contexts = new[]
            {
                "@WrapperTarget(.Entity)\nrich wrapper W { pub var payload: Payload = new Payload() }\n@W\nclass Host { }\n",
                "class Early { pub var payload: Payload = new Payload() }\n",
                "rich struct Early { pub var payload: Payload = new Payload() }\n",
                "class Early { pub static var payload: Payload = new Payload() }\n",
                "var payload: Payload = new Payload()\n",
                "func use(payload: Payload = new Payload()) { }\n",
                "rich enum struct Early { pub var payload: Payload\n pub init(_ -> payload) }[One(new Payload())]\n",
            };
            foreach (var context in contexts)
            {
                var (ok, _) = BindUnit(context + "shared class Payload { pub var n: i64 = 7L }\n");
                CheckNoErrors("后声明类型真实初值满足默认构造：" + context.Split('\n')[0], ok);
                var (missing, _) = BindUnit(context + "shared class Payload { pub var n: i64 }\n");
                CaseAssertions.CheckSemanticError("后声明类型无初值仍拒绝", missing.Diagnostics,
                    "has no constructor that assigns non-nullable field 'n'");
                var (bad, _) = BindUnit(context + "shared class Payload { pub var n: i64 = \"bad\" }\n");
                CaseAssertions.CheckSemanticError("后声明类型错误初值仍拒绝", bad.Diagnostics,
                    "initializer must be of type");
            }
            var (derived, bodies) = BindUnit(
                "class Early { pub var payload: Derived = new Derived() }\n" +
                "class Derived : Base { }\n" +
                "open class Base { pub var n: i64\n pub init() { n = 7L } }\n");
            CheckNoErrors("前向 Derived 默认构造由 super 担保基类字段", derived);
            CaseAssertions.CheckTrue("Derived 默认构造真实委托基类 init",
                bodies.Any(b => b.Method.Owner?.Name == "Derived" && b.Method.Kind == MethodKind.Init
                    && b.Body.Statements.Single() is BoundExpressionStatement
                        { Expression: BoundSuperCallExpression }));
            var (boundOk, _) = BindUnit(
                "func make\\<T extends Payload>(value: Payload = T()) { }\n" +
                "open class Payload { pub var n: i64 = 7L }\n");
            CheckNoErrors("泛型界 T() 默认值也等真实初值方法就位", boundOk);
            var (boundBad, _) = BindUnit(
                "func make\\<T extends Payload>(value: Payload = T()) { }\n" +
                "open class Payload { pub var n: i64 }\n");
            CaseAssertions.CheckSemanticError("泛型界 T() 缺初值仍拒绝", boundBad.Diagnostics,
                "has no init assigning non-nullable field 'n'");
            var tokenOnly = BindUnitWithStdlib(
                "import core.serialization.*\n" +
                "class Early { pub var payload: Payload = new Payload() }\n" +
                "@Serializable\nclass Payload { pub var n: i64 }\n");
            CaseAssertions.CheckSemanticError("token-only init 不担保普通零参构造", tokenOnly.Unit.Diagnostics,
                "has no constructor that assigns non-nullable field 'n'");
            var (enumOk, _) = BindUnit("enum struct E { pub var n: i64 = 7L }[One]\n");
            CheckNoErrors("enum 默认 case 初值检查延后到真实方法合成", enumOk);
            var (enumBad, _) = BindUnit("enum struct E { pub var n: i64 }[One]\n");
            CaseAssertions.CheckSemanticError("enum 默认 case 缺初值仍拒绝", enumBad.Diagnostics,
                "cannot assign non-nullable field 'n'");
        }

        private static void TestGenericParameterFieldInitializers()
        {
            CompilerTestTools.Section("P3 泛型参数字段初值（WRAP-001 review）");
            foreach (var init in new[] { "", "pub init(value: TTarget) { reads = value }" })
            {
                var (unit, bodies) = BindUnit(
                    "func seed(): i64 { return 7L }\n" +
                    "@WrapperTarget(.Entity)\nrich wrapper W\\<TTarget> {\n" +
                    "pub var reads: TTarget = seed() as TTarget\n" + init + "\n}\n" +
                    "@W\\<i64>" + (init.Length == 0 ? "" : "(9L)") + "\nclass Host { }\n");
                CheckNoErrors("泛型参数字段初值合法：" + init, unit);
                CaseAssertions.CheckTrue("泛型字段真实初值保留函数调用与类型转换",
                    bodies.Any(b => b.Method.Name == "..init.field.reads"
                        && b.Body.Statements.Single() is BoundAssignmentStatement
                            { Value: BoundCastExpression { Source: BoundCallExpression } }));
            }
            var (bad, _) = BindUnit(
                "@WrapperTarget(.Entity)\nrich wrapper W\\<TTarget> { pub var reads: TTarget = 7L }\n");
            CaseAssertions.CheckSemanticError("不兼容泛型初值仍拒绝", bad.Diagnostics,
                "initializer must be of type");
            var (staticBad, _) = BindUnit(
                "class C\\<T> { pub static var value: T = 7L as T }\n");
            CaseAssertions.CheckSemanticError("静态字段仍禁宿主泛型参数", staticBad.Diagnostics,
                "static");
            var (thisBad, _) = BindUnit(
                "class C { pub var n: i64 = 7L\n pub var copy: i64 = this.n }\n");
            CaseAssertions.CheckSemanticError("实例字段初值仍禁 this", thisBad.Diagnostics, "this");
        }

        private static void TestWrapperFieldInitializers()
        {
            CompilerTestTools.Section("P3 Wrapper Field Initializers (WRAP-001)");
            foreach (var init in new[] { "", "pub init()", "pub init() { }", "pub init(v: i64) { reads = v }" })
            {
                var (unit, bodies) = BindUnit(
                    "@WrapperTarget(.Entity)\n" +
                    "wrapper W { pub var reads: i64 = 7L\n" + init + "\n}\n");
                CheckNoErrors("wrapper 初值与 init 形态：" + init, unit);
                var wrapper = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "W");
                CaseAssertions.CheckTrue("wrapper 字段初始化方法真实写字段",
                    bodies.Any(b => ReferenceEquals(b.Method.Owner, wrapper)
                        && b.Method.Name == "..init.field.reads"
                        && b.Body.Statements.Single() is BoundAssignmentStatement
                            { Target: BoundFieldAccessExpression { Field.Name: "reads" } }));
                CaseAssertions.CheckTrue("wrapper 安装阶段调用字段初始化方法",
                    bodies.Any(b => ReferenceEquals(b.Method.Owner, wrapper)
                        && b.Method.Name == "..init.wrapper"
                        && b.Body.Statements.Single() is BoundCallStatement
                            { Method.Name: "..init.field.reads" }));
                CaseAssertions.CheckTrue("wrapper 零参默认 init 只在省略 init 时合成",
                    wrapper.Methods.Count(m => m.Kind == MethodKind.Init && m.IsSynthetic)
                        == (init.Length == 0 ? 1 : 0));
            }
        }

        private static void TestWrapperDefaultConstructionDa()
        {
            CompilerTestTools.Section("P3 Wrapper Construction DA (WRAP-001)");
            foreach (var target in new[] { "Entity", "Method", "Value" })
            {
                var application = target switch
                {
                    "Entity" => "@W\nclass Host { }\n",
                    "Method" => "class Host { @W\n pub func read(): i64 { return 0L } }\n",
                    _ => "class Host { @W\n pub const value: i64 = 0L }\n",
                };
                foreach (var init in new[] { "", "pub init()", "pub init() { }", "pub init(v: i64) { }" })
                {
                    var (bad, _) = BindUnit(
                        application + "@WrapperTarget(." + target + ")\n" +
                        "wrapper W { pub var reads: i64\n" + init + "\n}\n");
                    CaseAssertions.CheckSemanticError("无初值非空字段拒绝：" + target + "/" + init,
                        bad.Diagnostics, init.Length == 0
                            ? "has no constructor that assigns non-nullable field 'reads'"
                            : "is not definitely assigned");
                }
                // 应用先于定义：初值方法与默认 init 合成完成前不得误判。
                var (ok, _) = BindUnit(
                    application + "@WrapperTarget(." + target + ")\n" +
                    "wrapper W { pub var reads: i64 = 7L }\n");
                CheckNoErrors("前向 wrapper 默认构造初值合法：" + target, ok);
                var (nullable, _) = BindUnit(
                    application + "@WrapperTarget(." + target + ")\n" +
                    "rich wrapper W { pub var reads: i64? }\n");
                CheckNoErrors("无 init Nullable wrapper 字段合法：" + target, nullable);
            }
            var (unused, _) = BindUnit("@WrapperTarget(.Entity)\nwrapper W { pub var reads: i64 }\n");
            CheckNoErrors("无 init wrapper 仅声明不构造仍合法", unused);
            var (generic, _) = BindUnit(
                "@W\\<i64>\nclass Host { }\n" +
                "@WrapperTarget(.Entity)\nwrapper W\\<TTarget> { pub var reads: TTarget }\n");
            CaseAssertions.CheckSemanticError("泛型 wrapper 非空字段仍拒绝", generic.Diagnostics,
                "has no constructor that assigns non-nullable field 'reads'");
        }

        private static void TestWrapperPlaceVoidStatement()
        {
            CompilerTestTools.Section("P3 Wrapper Place Void Statement (§14.5)");

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
            CaseAssertions.CheckTrue("void bump 落 InstCallStmt",
                body.Contains("InstCallStmt(bump, WrapperPlace(Param(s,Svc), Logged), [])"));
            CaseAssertions.CheckTrue("void bumpBy 带参落 InstCallStmt",
                body.Contains("InstCallStmt(bumpBy,"));
            CaseAssertions.CheckTrue("void bumpDef 默认值落 InstCallStmt",
                body.Contains("InstCallStmt(bumpDef,"));
            CaseAssertions.CheckTrue("有返回 describe 仍是表达式语句",
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
            CaseAssertions.CheckSemanticError("值位置 void 仍拒绝",
                unitVal.Diagnostics, "has no result (void) and cannot be used as a value");
        }

        private static void TestGetProxyInnerForbidden()
        {
            CompilerTestTools.Section("P3 Get Proxy Inner Forbidden (§14.2/§14.3)");

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
            CaseAssertions.CheckSemanticError("Value .proxy.get 禁 inner",
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
            CaseAssertions.CheckSemanticError("Entity .proxy.get.* 禁 inner",
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
            CaseAssertions.CheckSemanticError("Entity .proxy.get.<名> 禁 inner",
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
            CompilerTestTools.Section("P3 Subclass Wrapper Inherited Member Shape (§14.2)");

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
            CaseAssertions.CheckSemanticError("子类 wrapper specific 对继承方法形状不符",
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

        private static void TestMethodWrapperSpecificCallShape()
        {
            CompilerTestTools.Section("P3 Method Wrapper Specific .proxy.call Shape (§14.4)");

            var (unitBad, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn {\n" +
                "        const r = inner()\n" +
                "        return r\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const f = func{ @Trace (x: i32): i32 -> (x * x) }\n" +
                "    return f(6)\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("lambda specific .proxy.call 形状不符",
                unitBad.Diagnostics, "does not match the shape");

            var (unitOk, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn {\n" +
                "        return inner(x)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const f = func{ @Trace (x: i32): i32 -> (x * x) }\n" +
                "    return f(6)\n" +
                "}\n");
            CheckNoErrors("lambda specific .proxy.call 形状匹配", unitOk);

            var (unitWild, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call(.name: String, args: named Any...): Any {\n" +
                "        return inner(.name, args)\n" +
                "    }\n" +
                "}\n" +
                "pub func main(): i32 {\n" +
                "    const f = func{ @Trace (x: i32): i32 -> (x * x) }\n" +
                "    return f(6)\n" +
                "}\n");
            CheckNoErrors("lambda wildcard .proxy.call 任意形状合法", unitWild);

            var (unitMethBad, _) = BindUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(): TReturn { return inner() }\n" +
                "}\n" +
                "pub class Host {\n" +
                "    pub init()\n" +
                "    @Trace\n" +
                "    pub func f(x: i32): i32 { return x }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("声明方法 specific .proxy.call 形状不符",
                unitMethBad.Diagnostics, "does not match the shape");

            var (unitMethOk, _) = BindUnit(
                "@WrapperTarget(.Method)\n" +
                "pub wrapper Trace {\n" +
                "    pub init()\n" +
                "    operator .proxy.call\\<TReturn>(x: i32): TReturn { return inner(x) }\n" +
                "}\n" +
                "pub class Host {\n" +
                "    pub init()\n" +
                "    @Trace\n" +
                "    pub func f(x: i32): i32 { return x }\n" +
                "}\n");
            CheckNoErrors("声明方法 specific .proxy.call 形状匹配", unitMethOk);
        }
    }
}
