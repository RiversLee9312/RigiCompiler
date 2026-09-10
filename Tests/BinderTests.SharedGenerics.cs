namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        private static void TestExplicitSharedGenerics()
        {
            TestHarness.Section("显式 shared 泛型约束：用户与标准库同规则");
            const string definitions = """
                pub class Local { }
                pub shared class Shared { }
                pub shared class Container\<shared T> {
                    pub var item: T
                    pub init(_ -> item)
                }
                pub func accept\<shared T>(value: T): T { return value }
                pub func forward\<shared T>(value: T): T { return accept\<T>(value) }
                pub func pack\<shared T...>(values: T...) { }
                """;
            foreach (var expression in new[] {
                "new Container\\<i32>(3)",
                "new Container\\<String>(\"中文\")",
                "new Container\\<Shared>(new Shared())",
                "new Container\\<Shared?>(null)",
                "forward\\<i32>(42)",
                "accept(new Shared())"
            })
            {
                var (unit, _) = BindUnitWithStdlib(definitions
                    + "\npub func test() { const result = " + expression + " }\n");
                CheckNoErrors("共享安全实参：" + expression, unit);
            }
            foreach (var body in new[] {
                "pub func bad() { const x = new Container\\<Local>(new Local()) }",
                "pub func bad() { const x = accept\\<Local>(new Local()) }",
                "pub func bad() { const x = accept(new Local()) }",
                "pub func bad() { const x = new Container\\<Local?>(null) }",
                "pub func bad\\<T>(value: T) { const x = accept\\<T>(value) }",
                "pub func bad(value: Container\\<Local>) {}",
                "pub func bad() { pack(1, new Local()) }"
            })
            {
                var (unit, _) = BindUnitWithStdlib(definitions + "\n" + body);
                TestHarness.CheckTrue("拒绝非共享安全填入：" + body,
                    unit.Diagnostics.HasErrors
                    && unit.Diagnostics.Diagnostics.Any(d => d.Message.Contains("shared-safe")),
                    string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => d.Message)));
            }

            const string handles = """
                pub shared class Ticket implements core.native.ICarrige {
                    pub override func retain(): Any? { return null }
                }
                pub class LocalHandle : core.native.NativeRcHandle\<Ticket> {
                    pub override func carry(): Ticket { return new Ticket() }
                    pub override func dispose() { }
                }
                """;
            foreach (var body in new[] {
                "pub async func bad(value: LocalHandle) {}",
                "pub shared class Bad { pub var handle: LocalHandle }",
                "pub func bad() { seq using(const ticket = new Ticket()) {} }",
                "pub func bad(value: core.coroutine.Task\\<i32>): core.coroutine.Task { return value }"
            })
            {
                var (unit, _) = BindUnitWithStdlib(handles + "\n" + body);
                TestHarness.CheckTrue("拒绝 Handle 越界或 Task 擦除：" + body,
                    unit.Diagnostics.HasErrors);
            }
            var (validHandle, _) = BindUnitWithStdlib(handles + """

                pub func use() {
                    seq using(const handle = new LocalHandle()) {
                        const ticket = handle.carry()
                        const value = ticket.retain()
                        if (value != null) {
                            seq using(const retained = value as LocalHandle) { }
                        }
                    }
                }
                """);
            CheckNoErrors("Handle using 与非 IDisposable Carrige 协议", validHandle);
        }
    }
}
