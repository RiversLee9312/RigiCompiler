namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        private static void TestPlaceOfStorage()
        {
            CompilerTestTools.Section("Place 稳定存储与三树");
            var result = BindUnitWithStdlib("func f() { var n: i32 = 1\n"
                + "const p = placeOf n\n const q = placeOf n\n p.dispose()\n q.dispose() }");
            CheckNoErrors("Place 绑定", result.Unit);
            var body = BodyOf(result.Bodies, "f");
            var local = body.Locals.Single(l => l.Name == "n");
            var places = body.Body.Statements.OfType<BoundLocalDeclarationStatement>()
                .Select(s => s.Initializer).OfType<BoundPlaceOfExpression>().ToArray();
            CaseAssertions.CheckTrue("重复 placeOf 共用变量的同一 Cell",
                places.Length == 2 && local.CellStorage != null
                && places.All(p => ReferenceEquals(p.Storage, local.CellStorage)));
            CaseAssertions.CheckTrue("Bound 描述保留 Place",
                BoundDescribe.Block(body.Body).Contains("PlaceOf("));
            var lowered = Lowerer.Lower(result.Unit, result.Bodies);
            CheckNoErrors("Place 降级", result.Unit);
            var fn = lowered.Single(f => ReferenceEquals(f.Method, body.Method));
            CaseAssertions.CheckTrue("Lowered 使用 Place 构造且保留来源",
                fn.Body.Statements.OfType<LoweredLocalDeclarationStatement>()
                    .Count(s => s.Initializer is LoweredNewExpression { Origin: BoundPlaceOfExpression }) == 2);
            var forged = BindUnit("namespace core\npub class Place\\<T> { }\n"
                + "func f() { var n: i32 = 1\n const p = placeOf n }");
            CaseAssertions.CheckTrue("同名用户类型不能认领 Place 特权",
                forged.Unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("from the standard library")));
            foreach (var source in new[]
            {
                "func f() { var n = 1\n seq using(const p = placeOf n) { p.expose() } }",
                "unsafe func f() { const h = new Handle\\<i32>() }",
                "unsafe class Fake: Handle\\<i32> {}",
                "@NativeLibrary(\"rigi_rt\")\n@NativeSymbol(\"handle_target\")\nunsafe native func fake(capability: Any): Any",
            })
            {
                var rejected = BindUnitWithStdlib(source);
                CaseAssertions.CheckTrue("Handle 权限边界拒绝：" + source,
                    rejected.Unit.Diagnostics.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error));
            }
            var privileged = BindUnitWithStdlib("class Local {}\n"
                + "unsafe shared class Holder { pub var h: Handle\\<Local>? }\n"
                + "unsafe async func receive(h: Handle\\<Local>) { h.load() }\n"
                + "unsafe func f(x: Local) { seq using(const p = placeOf x) { const h = p.expose()\n h.load() } }");
            CheckNoErrors("Handle 允许 local 目标与 shared 容器", privileged.Unit);
        }
    }
}
