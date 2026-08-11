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

            // 多泛型参数 castTo（S9f 复核 #23③）：名字分析无法静态代入
            // 多个参数 → 按不适用回退内建（BIL §12.1 第 3 条兜底），不落诊断
            var (unit9, bodies9) = BindUnit(
                "class S { operator castTo\\<TTarget, TOther>(): TTarget { return this as TTarget } }\n" +
                "func f(s: S): i32 { return s as i32 }\n");
            CheckNoErrors("多泛型参数 castTo 回退无诊断", unit9);
            TestHarness.CheckTrue("多泛型参数回退内建",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies9, "f")
                    .Body).Statements[0]).Value!).Conversion == null);

            // 宿主泛型参数 castTo（S9f 复核 #23③）：签名含宿主泛型参数，
            // 名字分析无构造实参可代入 → 回退内建
            var (unit10, bodies10) = BindUnit(
                "pub open class Box\\<T> { operator castTo(): T { return this as T } }\n" +
                "func f(b: Box\\<i32>): i32 { return b as i32 }\n");
            CheckNoErrors("宿主泛型参数 castTo 回退无诊断", unit10);
            TestHarness.CheckTrue("宿主泛型参数回退内建",
                ((BoundCastExpression)((BoundReturnStatement)((BoundBlock)BodyOf(bodies10, "f")
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
                "    var loader = func{async (id: i32): SharedUser -> load(local, id)}\n" +
                "}\n");
            TestHarness.CheckSemanticError("闸门 4 捕获非共享安全", unit4.Diagnostics,
                "async lambda captures 'local' of non-shared-safe type 'LocalUser'");

            // 闸门 4 合法：捕获共享安全局部（String）；lambda 形参不是捕获
            var (unit5, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "func load(name: String, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f() {\n" +
                "    var name = \"x\"\n" +
                "    var loader = func{async (id: i32): SharedUser -> load(name, id)}\n" +
                "}\n");
            TestHarness.CheckTrue("闸门 4 无捕获诊断",
                !unit5.Diagnostics.Diagnostics.Any(d => d.Message.Contains("captures")));

            // async lambda 的闸门 2/3：形参与返回类型共享安全规则与 async
            // 函数一致，捕获检查仍独立按符号执行。
            var (unit5b, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "class LocalUser { }\n" +
                "func f() {\n" +
                "    var loader = func{async (u: LocalUser): SharedUser -> new SharedUser()}\n" +
                "}\n");
            TestHarness.CheckSemanticError("async lambda 非共享安全形参", unit5b.Diagnostics,
                "Parameter 'u' of async lambda must be a shared-safe type");

            var (unit5c, _) = BindUnit(
                "class LocalUser { }\n" +
                "func f() {\n" +
                "    var loader = func{async (): LocalUser -> new LocalUser()}\n" +
                "}\n");
            TestHarness.CheckSemanticError("async lambda 非共享安全返回类型", unit5c.Diagnostics,
                "Return type of async lambda must be a shared-safe type");

            // 闸门 4：捕获宿主参数
            var (unit6, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "class LocalUser { }\n" +
                "func load(u: LocalUser, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f(u: LocalUser) {\n" +
                "    var loader = func{async (id: i32): SharedUser -> load(u, id)}\n" +
                "}\n");
            TestHarness.CheckSemanticError("闸门 4 捕获宿主参数", unit6.Diagnostics,
                "async lambda captures 'u' of non-shared-safe type 'LocalUser'");

            // 闸门 4：体内局部声明名不算捕获（排除内层作用域名）
            var (unit7, _) = BindUnit(
                "shared class SharedUser { }\n" +
                "func load(name: String, id: i32): SharedUser { return new SharedUser() }\n" +
                "func f() {\n" +
                "    var name = \"x\"\n" +
                "    var loader = func{async (id: i32): SharedUser -> { var text = \"y\"\n" +
                "        return@_ load(text, id) }}\n" +
                "}\n");
            TestHarness.CheckTrue("闸门 4 体内声明名不判捕获",
                !unit7.Diagnostics.Diagnostics.Any(d => d.Message.Contains("captures")));

            // 闸门 5 调用点（S9f 解开 #23④）：async 泛型调用的类型实参
            // 必须共享安全（typeid 与实际值一同跨边界）
            var (unit8, _) = BindUnit(
                "class LocalUser { }\n" +
                "async func pass\\<T>(x: T): T { return x }\n" +
                "func f() { var v = pass\\<LocalUser>(new LocalUser()) }\n");
            TestHarness.CheckSemanticError("闸门 5 泛型实参非共享安全", unit8.Diagnostics,
                "type argument of async function 'pass' must be a shared-safe type: 'LocalUser'");

            // 闸门 5 合法：共享安全的泛型实参（i32）
            var (unit9, _) = BindUnit(
                "async func pass\\<T>(x: T): T { return x }\n" +
                "func f() { var v = pass\\<i32>(1) }\n");
            CheckNoErrors("闸门 5 共享安全泛型实参无诊断", unit9);
        }

        // ===== S10：async 调用表达式类型改写（SYNTAX §4.5 表——
        // `async func f(): TResult` 调用点类型 = core.coroutine.Task\<TResult\>、
        // `async func f()` = core.coroutine.Task；用户决策提前至 S10 落地，
        // await 运算符仍归 S13。Task 定义在 stdlib，用例经 BindUnitWithStdlib）=====
        private static void TestAsyncResultTypes()
        {
            TestHarness.Section("P3 Async Result Types (Task<T>/Task, S10)");

            // 1. 有结果 async 调用：var 推断 = Task\<String\> 构造
            var (unit, bodies) = BindUnitWithStdlib(
                "async func loadUser(id: i32): String { return \"u\" }\n" +
                "func main() { var t = loadUser(42) }\n");
            CheckNoErrors("无诊断（async 调用 var 推断）", unit);
            var decl = (BoundLocalDeclarationStatement)BodyOf(bodies, "main").Body.Statements[0];
            TestHarness.CheckTrue("async 调用表达式类型 = Task\\<String> 构造（core.coroutine）",
                decl.Local.Type is TypeSymbol taskType
                && taskType.ConstructedFrom is { Name: "Task" }
                && taskType.ConstructedFrom.Namespace!.FullName == "core.coroutine"
                && taskType.TypeArguments!.Count == 1
                && ReferenceEquals(taskType.TypeArguments[0], unit.Symbols.Bootstrap.String));

            // 2. 显式标注类型匹配：const t: core.coroutine.Task\<String\> = loadUser(42)
            var (unit2, _) = BindUnitWithStdlib(
                "async func loadUser(id: i32): String { return \"u\" }\n" +
                "func main() { const t: core.coroutine.Task\\<String> = loadUser(42) }\n");
            CheckNoErrors("无诊断（显式 Task 标注赋值匹配）", unit2);

            // 3. 无结果 async 调用可作值：var t = flushLogs() → Task 非泛型定义
            var (unit3, bodies3) = BindUnitWithStdlib(
                "async func flushLogs() { }\n" +
                "func main() { var t = flushLogs() }\n");
            CheckNoErrors("无诊断（无结果 async 调用作值）", unit3);
            TestHarness.CheckTrue("无结果 async 调用类型 = Task 非泛型定义",
                ((BoundLocalDeclarationStatement)BodyOf(bodies3, "main").Body.Statements[0])
                    .Local.Type is TypeSymbol taskDef
                && taskDef.Name == "Task" && taskDef.ConstructedFrom == null
                && taskDef.GenericParameters.Count == 0);

            // 4. 语句位置 async 调用 → BoundCallStatement（fire-and-forget）
            var (unit4, bodies4) = BindUnitWithStdlib(
                "async func flushLogs() { }\n" +
                "func main() { flushLogs() }\n");
            CheckNoErrors("无诊断（语句位置 async 调用）", unit4);
            TestHarness.CheckTrue("语句位置 async 调用 = BoundCallStatement",
                BodyOf(bodies4, "main").Body.Statements[0] is BoundCallStatement);

            // 5. 普通函数不受影响
            var (unit5, bodies5) = BindUnitWithStdlib(
                "func g(): i32 { return 1 }\n" +
                "func main() { var t = g() }\n");
            CheckNoErrors("无诊断（普通调用）", unit5);
            TestHarness.CheckTrue("普通调用类型仍为 i32",
                ReferenceEquals(((BoundLocalDeclarationStatement)BodyOf(bodies5, "main")
                    .Body.Statements[0]).Local.Type, unit5.Symbols.Bootstrap.Int32));

            // 6. 泛型 async 方法：Task\<i32\>（显式泛型实参）
            var (unit6, bodies6) = BindUnitWithStdlib(
                "async func gf\\<T>(x: T): T { return x }\n" +
                "func main() { var t = gf\\<i32>(42) }\n");
            CheckNoErrors("无诊断（泛型 async 调用）", unit6);
            TestHarness.CheckTrue("泛型 async 调用类型 = Task\\<i32> 构造",
                ((BoundLocalDeclarationStatement)BodyOf(bodies6, "main").Body.Statements[0])
                    .Local.Type is TypeSymbol task6
                && task6.ConstructedFrom?.Name == "Task"
                && task6.TypeArguments!.Count == 1
                && ReferenceEquals(task6.TypeArguments[0], unit6.Symbols.Bootstrap.Int32));

            // 7. 实例 async 方法调用（闸门 1 共享安全：shared receiver）
            var (unit7, bodies7) = BindUnitWithStdlib(
                "shared class Service { pub async func run(name: String): String { return name } }\n" +
                "func main() {\n" +
                "    var s = new Service()\n" +
                "    var t = s.run(\"x\")\n" +
                "}\n");
            CheckNoErrors("无诊断（实例 async 调用）", unit7);
            TestHarness.CheckTrue("实例 async 调用类型 = Task\\<String> 构造",
                ((BoundLocalDeclarationStatement)BodyOf(bodies7, "main").Body.Statements[1])
                    .Local.Type is TypeSymbol task7
                && task7.ConstructedFrom?.Name == "Task"
                && task7.TypeArguments!.Count == 1
                && ReferenceEquals(task7.TypeArguments[0], unit7.Symbols.Bootstrap.String));
        }

        private static void TestYieldBinding()
        {
            TestHarness.Section("P3 Yield");
            var (unit, bodies) = BindUnitWithStdlib(
                "import core.coroutine.*\n" +
                "shared abstract class MyPolling : core.coroutine.PollingAlarm { }\n" +
                "shared abstract class MyEvent : core.coroutine.EventAlarm { }\n" +
                "func main(p: MyPolling, e: MyEvent) {\n" +
                "    yield\n" +
                "    yield sleep(1)\n" +
                "    yield p\n" +
                "    yield e\n" +
                "}");
            CheckNoErrors("yield 裸/ sleep/Alarm 子类/普通 main 合法", unit);
            var statements = ((BoundBlock)BodyOf(bodies, "main").Body).Statements;
            TestHarness.CheckTrue("yield Bound 形态", statements.Count(s => s is BoundYieldStatement) == 4
                && ((BoundYieldStatement)statements[0]).Alarm == null);

            var (bad, _) = BindUnitWithStdlib("func take(s: String) { }\n" +
                "func f(x: String?) {\n" +
                "    if (x != null) {\n" +
                "        yield\n" +
                "        take(x)\n" +
                "    }\n" +
                "}\n" +
                "func g(): i32 {\n" +
                "    yield 1\n" +
                "    return 0\n" +
                "}");
            TestHarness.CheckSemanticError("yield 后 smart-cast 失效/非 Alarm 诊断", bad.Diagnostics,
                "yield alarm must be assignable");
            TestHarness.CheckSemanticError("yield 后 smart-cast 失效单独诊断", bad.Diagnostics,
                "argument");

            var (nested, _) = BindUnitWithStdlib("import core.coroutine.*\n" +
                "func f(x: core.coroutine.EventAlarm) {\n" +
                "    while (true) {\n" +
                "        yield x\n" +
                "        break\n" +
                "    }\n" +
                "    try { yield } finally(_) { yield x }\n" +
                "}");
            CheckNoErrors("yield 循环与 try/finally 合法", nested);

            // await 藏在索引 guard 内时，条件事实也必须失效；否则 d 的
            // 旧 non-null smart-cast 会从 guard 恢复到 return。
            var indexGuard = BindUnitWithStdlib(
                "class Dog { }\n" +
                "class Flags {\n" +
                "    pub operator getAtIndex(index: i32): bool { return true }\n" +
                "}\n" +
                "async func nextIndex(): i32 { return 0 }\n" +
                "func f(d: Dog?, flags: Flags): Dog {\n" +
                "    if ((d != null) and (flags[await nextIndex()])) { return d }\n" +
                "    return new Dog()\n" +
                "}\n");
            TestHarness.CheckSemanticError("索引 guard 内 await 后不恢复 smart cast",
                indexGuard.Unit.Diagnostics, "Cannot return");

            var constrainedAlarm = BindUnitWithStdlib(
                "import core.coroutine.*\n" +
                "func constrained\\<T extends PollingAlarm>(alarm: T) { yield alarm }\n");
            CheckNoErrors("泛型 Alarm extends 约束允许 yield", constrainedAlarm.Unit);
            var genericAlarm = BindUnitWithStdlib(
                "import core.coroutine.*\n" +
                "func unconstrained\\<T>(value: T) { yield value }\n");
            TestHarness.CheckSemanticError("无约束泛型 yield 仍拒绝",
                genericAlarm.Unit.Diagnostics, "yield alarm must be assignable");
        }

        private static void TestAwaitBinding()
        {
            TestHarness.Section("P3 Await Binding (S13)");

            var (unit, bodies) = BindUnitWithStdlib(
                "async func load(): String { return \"ok\" }\n" +
                "async func flush() { }\n" +
                "func main() {\n" +
                "    const value = await load()\n" +
                "    await flush()\n" +
                "}\n");
            CheckNoErrors("Task<T> 值 await 与 Task 语句 await 无诊断", unit);
            var statements = BodyOf(bodies, "main").Body.Statements;
            TestHarness.Check("Task<T> await 绑定形态", BoundDescribe.Stmt(statements[0]),
                "Decl(value, String, = Await(Call(load, [], Task<String>), String))");
            TestHarness.Check("Task await 语句绑定形态", BoundDescribe.Stmt(statements[1]),
                "ExprStmt(Await(Call(flush, [], Task), void))");

            var grouped = BindUnitWithStdlib(
                "async func flush() { }\n" +
                "func main() { (await flush()) }\n");
            CheckNoErrors("括号包裹的无结果 await 仍是合法表达式语句", grouped.Unit);

            var valueBlock = BindUnitWithStdlib(
                "async func flush() { }\n" +
                "func main() { var x = if (true) { await flush() } else { return@_ 1 } }\n");
            TestHarness.CheckSemanticError("值块不能隐式取无结果 await", valueBlock.Unit.Diagnostics,
                "branch must produce a value");

            var (nonTask, _) = BindUnitWithStdlib(
                "func value(): String { return \"x\" }\n" +
                "func main() { var x = await value() }\n");
            TestHarness.CheckSemanticError("非 Task await 拒绝", nonTask.Diagnostics,
                "await operand must be exactly");

            var (voidValue, _) = BindUnitWithStdlib(
                "async func flush() { }\n" +
                "func main() { var x = await flush() }\n");
            TestHarness.CheckSemanticError("Task 在值位置拒绝", voidValue.Diagnostics,
                "without a result is only valid");

            var (smartCast, _) = BindUnitWithStdlib(
                "class Dog { }\n" +
                "async func pause() { }\n" +
                "func f(d: Dog?): Dog {\n" +
                "    if (d != null) {\n" +
                "        await pause()\n" +
                "        return d\n" +
                "    }\n" +
                "    return new Dog()\n" +
                "}\n");
            TestHarness.CheckSemanticError("smart cast 跨 await 失效", smartCast.Diagnostics,
                "Cannot return");

            var guardAwait = BindUnitWithStdlib(
                "class Dog { }\n" +
                "async func ready(): bool { return true }\n" +
                "func f(d: Dog?): Dog {\n" +
                "    if ((d != null) and (await ready())) { return d }\n" +
                "    return new Dog()\n" +
                "}\n");
            TestHarness.CheckSemanticError("guard 中 await 后不复活 smart cast", guardAwait.Unit.Diagnostics,
                "Cannot return");

            var rightAfterAwait = BindUnitWithStdlib(
                "class Dog { pub var name: String }\n" +
                "async func ready(): bool { return true }\n" +
                "func f(d: Dog?): bool {\n" +
                "    return (await ready()) and (d.name == \"x\")\n" +
                "}\n");
            TestHarness.CheckSemanticError("and 右侧 await 后不继承旧 smart cast",
                rightAfterAwait.Unit.Diagnostics, "cannot be accessed on nullable type");
        }
    }
}
