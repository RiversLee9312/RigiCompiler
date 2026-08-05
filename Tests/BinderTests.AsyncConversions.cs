using System.Linq;

namespace LatteCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== castTo/castFrom 名字分析（S8f，SYNTAX §3.5 转换优先级：
        // 源类型 castTo → 目标类型 castFrom → 内建；BIL §12.1 语义第 1–3 条）=====
        private static void TestConversionOperators()
        {
            TestHarness.Section("P3 Conversion Operators (castTo/castFrom)");

            // castTo 适用（非泛型形态）：as 选中源类型上的 castTo
            var (unit, bodies) = BindUnit(
                "class S { operator castTo(): i32 { return 1 } }\n" +
                "func f(s: S): i32 { return s as i32 }\n");
            CheckNoErrors("castTo 适用无诊断", unit);
            var cast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("castTo 被选中",
                cast.Conversion != null && cast.Conversion.Name == "castTo"
                && ReferenceEquals(cast.Conversion.Owner,
                    unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "S")));

            // castTo 泛型形态（spec 示例形态）：任意目标适用；体以 as TTarget
            // 返回（S9a 起泛型返回类型恢复全路径检查——体须有 return；
            // 体在运行时从不执行）
            var (unit2, bodies2) = BindUnit(
                "class S { operator castTo\\<TTarget>(): TTarget { return this as TTarget } }\n" +
                "func f(s: S): i32 { return s as i32 }\n" +
                "func g(s: S): String { return s as String }\n");
            CheckNoErrors("castTo 泛型形态无诊断", unit2);
            TestHarness.CheckTrue("泛型 castTo 对 i32 适用",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies2, "f")
                    .Body).Statements[0]).Value!).Conversion?.Name == "castTo");
            TestHarness.CheckTrue("泛型 castTo 对 String 适用",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies2, "g")
                    .Body).Statements[0]).Value!).Conversion?.Name == "castTo");

            // castFrom 兜底（源无 castTo）：目标类型上的 castFrom 被选中
            var (unit3, bodies3) = BindUnit(
                "class S { }\n" +
                "class C { operator castFrom(obj: S): C { return new C() } }\n" +
                "func f(s: S): C { return s as C }\n");
            CheckNoErrors("castFrom 兜底无诊断", unit3);
            var castFrom = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies3, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("castFrom 被选中",
                castFrom.Conversion != null && castFrom.Conversion.Name == "castFrom"
                && ReferenceEquals(castFrom.Conversion.Owner,
                    unit3.Symbols.GlobalNamespace.Types.Single(t => t.Name == "C")));

            // castFrom 泛型形态（TSource 代入源类型）
            var (unit3b, bodies3b) = BindUnit(
                "class S { }\n" +
                "class D { operator castFrom\\<TSource>(obj: TSource): D { return new D() } }\n" +
                "func f(s: S): D { return s as D }\n");
            CheckNoErrors("castFrom 泛型形态无诊断", unit3b);
            TestHarness.CheckTrue("泛型 castFrom 被选中",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies3b, "f")
                    .Body).Statements[0]).Value!).Conversion?.Name == "castFrom");

            // 优先级：源 castTo 与目标 castFrom 同时适用 → castTo 胜出
            var (unit4, bodies4) = BindUnit(
                "class S { operator castTo\\<TTarget>(): TTarget { return this as TTarget } }\n" +
                "class C { operator castFrom(obj: S): C { return new C() } }\n" +
                "func f(s: S): C { return s as C }\n");
            CheckNoErrors("优先级用例无诊断", unit4);
            var priorityCast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies4, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("castTo 优先于 castFrom",
                priorityCast.Conversion != null && priorityCast.Conversion.Name == "castTo");

            // 不适用回退内建：castTo 存在但产不出目标（返回 i32 ≠ String）
            var (unit5, bodies5) = BindUnit(
                "class S { operator castTo(): i32 { return 1 } }\n" +
                "func f(s: S): String { return s as String }\n");
            CheckNoErrors("不适用回退无诊断", unit5);
            TestHarness.CheckTrue("castTo 不适用则回退内建",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies5, "f")
                    .Body).Statements[0]).Value!).Conversion == null);

            // castFrom 参数类型不匹配 → 不适用（目标 C 的 castFrom 只收 i32）
            var (unit6, bodies6) = BindUnit(
                "class S { }\n" +
                "class C { operator castFrom(obj: i32): C { return new C() } }\n" +
                "func f(s: S): C { return s as C }\n");
            CheckNoErrors("castFrom 参数不匹配无诊断", unit6);
            TestHarness.CheckTrue("castFrom 参数不匹配回退内建",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies6, "f")
                    .Body).Statements[0]).Value!).Conversion == null);

            // as? 同名字分析：Conversion 照常记录，结果类型仍 Nullable<目标>
            var (unit7, bodies7) = BindUnit(
                "class S { operator castTo(): i32 { return 1 } }\n" +
                "func f(s: S): i32? { return s as? i32 }\n");
            CheckNoErrors("as? 名字分析无诊断", unit7);
            var safeCast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies7, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("as? 记录转换且结果可空",
                safeCast.IsSafe && safeCast.Conversion?.Name == "castTo"
                && safeCast.Type.Name == "Nullable");

            // 无任何转换运算符 → 内建兜底（既有行为不变）
            var (unit8, bodies8) = BindUnit(
                "open class Animal { }\nclass Dog : Animal { }\n" +
                "func d(a: Animal): Dog { return a as Dog }\n");
            CheckNoErrors("无转换运算符无诊断", unit8);
            TestHarness.CheckTrue("无转换记录 null",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies8, "d")
                    .Body).Statements[0]).Value!).Conversion == null);
        }

        // ===== async 边界闸门调用点 1/2 + lambda 捕获 4（S8f，SYNTAX §4.5；
        // 仅分析侧，无 P4 面）=====
        private static void TestAsyncGates()
        {
            TestHarness.Section("P3 Async Gates (§4.5)");

            // 闸门 1：async 实例方法调用点 receiver 是 local class
            var (unit, _) = BindUnit(
                "class Service { pub async func run(name: String) { } }\n" +
                "func f() {\n" +
                "    var s = new Service()\n" +
                "    s.run(\"x\")\n" +
                "}\n");
            TestHarness.CheckSemanticError("闸门 1 receiver", unit.Diagnostics,
                "async call receiver must be a shared-safe type: 'Service'");

            // 闸门 1 合法：shared 类的 async 实例方法
            var (unit2, _) = BindUnit(
                "shared class Service { pub async func run(name: String) { } }\n" +
                "func f() {\n" +
                "    var s = new Service()\n" +
                "    s.run(\"x\")\n" +
                "}\n");
            CheckNoErrors("shared receiver 无诊断", unit2);

            // 闸门 2 合法：声明侧已收口（可赋值 ⇒ 必共享安全——shared 单向
            // 传染保证子类同标），调用点检查为防御性兜底；语句位置 void 调用
            var (unit3, _) = BindUnit(
                "async func flush(name: String) { }\n" +
                "func f() { flush(\"x\") }\n");
            CheckNoErrors("async 调用合法实参无诊断", unit3);

            // 闸门 4：async lambda 捕获非共享安全局部
            var (unit4, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "class LocalUser { }\n" +
                "func load(u: LocalUser, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f() {\n" +
                "    var local = new LocalUser()\n" +
                "    var loader = async func{(id: i32): SharedUser -> load(local, id)}\n" +
                "}\n");
            TestHarness.CheckSemanticError("闸门 4 捕获非共享安全", unit4.Diagnostics,
                "async lambda captures 'local' of non-shared-safe type 'LocalUser'");

            // 闸门 4 合法：捕获共享安全局部（String）；lambda 形参不是捕获
            var (unit5, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "func load(name: String, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f() {\n" +
                "    var name = \"x\"\n" +
                "    var loader = async func{(id: i32): SharedUser -> load(name, id)}\n" +
                "}\n");
            TestHarness.CheckTrue("闸门 4 无捕获诊断",
                !unit5.Diagnostics.Diagnostics.Any(d => d.Message.Contains("captures")));

            // 闸门 4：捕获宿主参数
            var (unit6, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "class LocalUser { }\n" +
                "func load(u: LocalUser, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f(u: LocalUser) {\n" +
                "    var loader = async func{(id: i32): SharedUser -> load(u, id)}\n" +
                "}\n");
            TestHarness.CheckSemanticError("闸门 4 捕获宿主参数", unit6.Diagnostics,
                "async lambda captures 'u' of non-shared-safe type 'LocalUser'");

            // 闸门 4：体内局部声明名不算捕获（排除内层作用域名）
            var (unit7, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "func load(name: String, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f() {\n" +
                "    var name = \"x\"\n" +
                "    var loader = async func{(id: i32): SharedUser -> { var text = \"y\"\n" +
                "        return@_ load(text, id) }}\n" +
                "}\n");
            TestHarness.CheckTrue("闸门 4 体内声明名不判捕获",
                !unit7.Diagnostics.Diagnostics.Any(d => d.Message.Contains("captures")));
        }
    }
}
