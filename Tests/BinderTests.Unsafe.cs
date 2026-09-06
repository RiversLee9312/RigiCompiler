namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        private static void TestUnsafeContexts()
        {
            TestHarness.Section("unsafe 词法上下文与危险操作");
            const string danger = "unsafe func danger(): i32 { return 7 }\n";
            foreach (var body in new[]
            {
                "unsafe seq { const x = danger() }",
                "const x = unsafe seq { danger() }",
                "unsafe volatile seq { seq { const x = danger() } }",
            })
            {
                var result = BindUnit(danger + "func f() { " + body + " }");
                CheckNoErrors(body, result.Unit);
            }
            CheckNoErrors("unsafe 方法体", BindUnit(danger
                + "unsafe func f(): i32 { return danger() }").Unit);
            CheckNoErrors("private unsafe 存储声明", BindUnit(
                "unsafe class Secret { }\nclass Safe { priv var hidden: Secret? = null }").Unit);

            foreach (var source in new[]
            {
                danger + "func f() { const x = danger() }",
                danger + "func f() { unsafe seq { danger() }\n danger() }",
                danger + "func f() { const x = unsafe seq { danger() }\n danger() }",
                "unsafe class Secret { }\nfunc f() { const x = new Secret() }",
                "class Secret { pub unsafe init() { } }\nfunc f() { const x = new Secret() }",
                danger + "unsafe func f(x: i32 = danger()) { }",
                "unsafe func danger\\<T>(x: T): T { return x }\nfunc f() { danger(1) }",
            })
            {
                var result = BindUnit(source);
                TestHarness.CheckTrue("安全调用拒绝：" + source,
                    result.Unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("requires an unsafe context")),
                    string.Join("; ", result.Unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            }
            // 每个负例配同源 unsafe 正例，避免语法错误冒充调用点拒绝。
            foreach (var (declaration, operation) in new[]
            {
                ("unsafe class Secret { }", "const t = typeOf(Secret)\n const x = new t()"),
                ("class Secret { pub unsafe init() { } }", "const t = typeOf(Secret)\n const x = new t()"),
                ("unsafe class Secret { pub var x: i32 { pub get\n pub set } }",
                    "const x = s.x"),
                ("unsafe class Secret { pub var x: i32 { pub get\n pub set } }",
                    "s.x = 2"),
                ("unsafe class Secret { pub var x: i32 { pub get\n pub set } }",
                    "s.x += 2"),
                ("class Secret { pub operator getAtIndex(index: i32): i32? { return 1 }\n pub unsafe operator setAtIndex(index: i32, element: i32) { } }",
                    "s[0] = 2"),
                ("class Secret { pub operator getAtIndex(index: i32): i32? { return 1 }\n pub unsafe operator setAtIndex(index: i32, element: i32) { } }",
                    "s[0] += 2"),
            })
            {
                if (!operation.Contains("[0] +=")) CheckNoErrors("unsafe 调用点允许：" + operation,
                    BindUnit(declaration + "\n func f(s: Secret) { unsafe seq { " + operation + " } }").Unit);
                var rejected = BindUnit(declaration + "\n func f(s: Secret) { " + operation + " }");
                TestHarness.CheckTrue("unsafe 调用点拒绝：" + operation,
                    rejected.Unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("requires an unsafe context")),
                    string.Join("; ", rejected.Unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            }
            CheckNoErrors("安全泛型动态构造透传", BindUnit(
                "func make\\<T>(t: Type\\<T>, x: i32): T { return new t(x) }").Unit);
            var invalid = BindUnit("unsafe var x: i32 = 1");
            TestHarness.CheckTrue("字段不能声明 unsafe",
                invalid.Unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("'unsafe' can only")));

            var leak = BindUnit("pub unsafe class Secret { }\npub class Safe { pub var hidden: Secret? = null }");
            TestHarness.CheckTrue("公开签名拒绝嵌套 unsafe 类型",
                leak.Unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("cannot expose unsafe type")));
            var overrideResult = BindUnit("pub open class Base { pub open func f() { } }\n"
                + "pub class Derived: Base { pub unsafe override func f() { } }");
            TestHarness.CheckTrue("覆写不能增加 unsafe 要求",
                overrideResult.Unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("unsafe implementation cannot override")));

            var trees = BindUnit("func f() { unsafe volatile seq { const x = 1 } }");
            CheckNoErrors("unsafe 三树输入", trees.Unit);
            var bound = BodyOf(trees.Bodies, "f");
            TestHarness.CheckTrue("Bound 保留 unsafe", BoundDescribe.Block(bound.Body).Contains("SeqVolatileUnsafe"));
            var lowered = Lowerer.Lower(trees.Unit, trees.Bodies);
            TestHarness.CheckTrue("Lowered 保留 unsafe", LoweredDescribe.Block(
                lowered.Single(b => b.Method.Name == "f").Body).Contains("SeqVolatileUnsafe"));
        }
    }
}
