using System.Linq;

namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== 默认参数（S8d，SYNTAX §4.2：声明点绑定 + 调用点规范序填充）=====
        private static void TestDefaultParameters()
        {
            TestHarness.Section("P3 Default Parameters (S8d)");

            // 全缺省 / 部分缺省 / 具名跳位：规范参数序填充（BoundDescribe 断言）
            var (unit, bodies) = BindUnit(
                "func greet(name: String = \"World\", punct: String = \"!\"): String { return name }\n" +
                "func a(): String { return greet() }\n" +
                "func b(): String { return greet(\"Rigi\") }\n" +
                "func c(): String { return greet(punct = \"?\") }\n");
            CheckNoErrors("无诊断（默认参数正例）", unit);
            TestHarness.Check("全缺省填充", BoundDescribe.Body(BodyOf(bodies, "a")),
                "Body(a, [], [Return(Call(greet, [Str(\"World\",String), Str(\"!\",String)], String))])");
            TestHarness.Check("部分缺省填充", BoundDescribe.Body(BodyOf(bodies, "b")),
                "Body(b, [], [Return(Call(greet, [Str(\"Rigi\",String), Str(\"!\",String)], String))])");
            TestHarness.Check("具名跳位缺省", BoundDescribe.Body(BodyOf(bodies, "c")),
                "Body(c, [], [Return(Call(greet, [Str(\"World\",String), Str(\"?\",String)], String))])");

            // 默认值引用全局函数（声明点作用域可及全局符号；每次调用重新求值）
            var (unit2, bodies2) = BindUnit(
                "func fallback(): i32 { return 7 }\n" +
                "func f(x: i32 = fallback()): i32 { return x }\n" +
                "func g(): i32 { return f() }\n");
            CheckNoErrors("无诊断（默认值调用全局函数）", unit2);
            TestHarness.Check("默认值为调用表达式", BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [Return(Call(f, [Call(fallback, [], i32)], i32))])");

            // 默认值类型不匹配（声明点诊断）
            var (unit3, _) = BindUnit("func f(x: i32 = \"s\") { }\n");
            TestHarness.CheckSemanticError("默认值类型不匹配", unit3.Diagnostics,
                "Default value of parameter 'x' must be of type 'i32', got 'String'");

            // 顺序违反（P2 声明侧）：默认值之后的形参必须全部携带默认值
            var (unit4, _) = BindUnit("func f(a: i32 = 1, b: i32) { }\n");
            TestHarness.CheckSemanticError("默认参数顺序违反", unit4.Diagnostics,
                "Parameter 'b' must declare a default value");

            // 声明点作用域：看不到函数形参
            var (unit5, _) = BindUnit("func f(a: i32, b: i32 = a) { }\n");
            TestHarness.CheckSemanticError("默认值引用形参拒绝", unit5.Diagnostics,
                "Undefined name: 'a'");

            // 声明点视同静态上下文：实例成员不可达
            var (unit6, _) = BindUnit(
                "class C {\n    pub var v: i32\n    pub func f(x: i32 = v) { }\n}\n");
            TestHarness.CheckSemanticError("默认值引用实例成员拒绝", unit6.Diagnostics,
                "requires a receiver");

            // 含局部声明的默认值归口（P4 无法物化跨函数局部）
            var (unit7, _) = BindUnit(
                "func f(x: i32 = if (1 == 1) { var y = 1\nreturn@_ y } else { return@_ 2 }): i32 { return x }\n");
            TestHarness.CheckSemanticError("默认值含局部声明归口", unit7.Diagnostics,
                "default value expressions with local declarations are not supported yet (S8d)");

            // init 默认参数（构造调用同规则填充）
            var (unit8, bodies8) = BindUnit(
                "class P {\n    pub var x: i32\n    pub init(_ -> x = 5) { }\n}\n" +
                "func m(): P { return new P() }\n");
            CheckNoErrors("无诊断（init 默认参数）", unit8);
            TestHarness.Check("init 默认值填充", BoundDescribe.Body(BodyOf(bodies8, "m")),
                "Body(m, [], [Return(New(P, init, [Int(5,i32)]))])");

            // 前向依赖：f 的默认值调用 h 且缺省使用 h 的默认值——声明顺序
            // 不影响语义（记忆化按需绑定递归触发被依赖参数的绑定）
            var (unit9, bodies9) = BindUnit(
                "func f(a: i32 = h()): i32 { return a }\n" +
                "func h(x: i32 = 41): i32 { return x }\n" +
                "func g(): i32 { return f() }\n");
            CheckNoErrors("无诊断（默认值前向依赖）", unit9);
            TestHarness.Check("前向依赖逐层填充", BoundDescribe.Body(BodyOf(bodies9, "g")),
                "Body(g, [], [Return(Call(f, [Call(h, [Int(41,i32)], i32)], i32))])");
        }

        // ===== 重载解析（S8d，SYNTAX §4.2：结构过滤 → 类型适用性 → 最具体胜出）=====
        private static void TestOverloadResolution()
        {
            TestHarness.Section("P3 Overload Resolution (S8d)");

            // 精确类型优于基类型（Any 是类型层级根）
            var (unit, bodies) = BindUnit(
                "func show(x: Any): i32 { return 1 }\n" +
                "func show(x: String): i32 { return 2 }\n" +
                "func f(): i32 { return show(\"hi\") }\n");
            CheckNoErrors("无诊断（精确优于基类型）", unit);
            var shows = unit.Symbols.GlobalNamespace.Methods.Where(m => m.Name == "show").ToList();
            var stringShow = shows.Single(m => ((TypeSymbol)m.Parameters[0].Type!).Name == "String");
            var showCall = ((BoundReturnStatement)BodyOf(bodies, "f").Body.Statements[0]).Value
                as BoundCallExpression;
            TestHarness.CheckTrue("命中 String 版", showCall != null
                && ReferenceEquals(showCall.Method, stringShow));

            // 继承链：子类候选优于基类候选
            var (unit2, bodies2) = BindUnit(
                "open class OpenAnimal { }\nclass Dog : OpenAnimal { }\n" +
                "func pick(a: OpenAnimal): i32 { return 1 }\n" +
                "func pick(d: Dog): i32 { return 2 }\n" +
                "func f(d: Dog): i32 { return pick(d) }\n");
            CheckNoErrors("无诊断（子类优于基类）", unit2);
            var dogPick = unit2.Symbols.GlobalNamespace.Methods
                .Single(m => m.Name == "pick" && m.Parameters[0].Type!.Name == "Dog");
            var pickCall = ((BoundReturnStatement)BodyOf(bodies2, "f").Body.Statements[0]).Value
                as BoundCallExpression;
            TestHarness.CheckTrue("命中 Dog 版", pickCall != null
                && ReferenceEquals(pickCall.Method, dogPick));

            // 二义：两候选互不占优
            var (unit3, _) = BindUnit(
                "func combine(a: String, b: Any) { }\n" +
                "func combine(a: Any, b: String) { }\n" +
                "func f() { combine(\"x\", \"y\") }\n");
            TestHarness.CheckSemanticError("二义诊断", unit3.Diagnostics,
                "Call to 'combine' is ambiguous between");

            // 无适用候选（类型适用性全灭）
            var (unit4, _) = BindUnit(
                "func only(x: i32) { }\nfunc only(x: bool) { }\n" +
                "func f() { only(\"s\") }\n");
            TestHarness.CheckSemanticError("无适用候选", unit4.Diagnostics,
                "No applicable overload of 'only'");

            // 结构过滤：个数适配（1 参实参只见 1 参候选）
            var (unit5, bodies5) = BindUnit(
                "func g(x: i32): i32 { return 1 }\n" +
                "func g(x: i32, y: i32): i32 { return 2 }\n" +
                "func f(): i32 { return g(1) }\n");
            CheckNoErrors("无诊断（个数结构过滤）", unit5);
            var unaryG = unit5.Symbols.GlobalNamespace.Methods
                .Single(m => m.Name == "g" && m.Parameters.Count == 1);
            var gCall = ((BoundReturnStatement)BodyOf(bodies5, "f").Body.Statements[0]).Value
                as BoundCallExpression;
            TestHarness.CheckTrue("命中 1 参版", gCall != null
                && ReferenceEquals(gCall.Method, unaryG));

            // 默认值平局打破：f(i32) 与 f(i32, i32 = 0) 同形适配时，填充默认值更少者优先
            var (unit6, bodies6) = BindUnit(
                "func h(x: i32): i32 { return 1 }\n" +
                "func h(x: i32, y: i32 = 0): i32 { return 2 }\n" +
                "func f(): i32 { return h(1) }\n" +
                "func f2(): i32 { return h(1, 2) }\n");
            CheckNoErrors("无诊断（默认值平局打破）", unit6);
            var hs = unit6.Symbols.GlobalNamespace.Methods.Where(m => m.Name == "h").ToList();
            var h1 = hs.Single(m => m.Parameters.Count == 1);
            var h2 = hs.Single(m => m.Parameters.Count == 2);
            var h1Call = ((BoundReturnStatement)BodyOf(bodies6, "f").Body.Statements[0]).Value
                as BoundCallExpression;
            var h2Call = ((BoundReturnStatement)BodyOf(bodies6, "f2").Body.Statements[0]).Value
                as BoundCallExpression;
            TestHarness.CheckTrue("h(1) 命中 1 参版", h1Call != null
                && ReferenceEquals(h1Call.Method, h1));
            TestHarness.CheckTrue("h(1, 2) 命中 2 参版", h2Call != null
                && ReferenceEquals(h2Call.Method, h2));

            // null 实参：仅 Nullable 形参适用（以胜者形参类型定型）
            var (unit7, bodies7) = BindUnit(
                "func n(x: String?): i32 { return 1 }\n" +
                "func n(x: i32): i32 { return 2 }\n" +
                "func f(): i32 { return n(null) }\n");
            CheckNoErrors("无诊断（null 实参）", unit7);
            TestHarness.Check("null 定型为胜者形参类型", BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [], [Return(Call(n, [Null(String?)], i32))])");

            // null 二义：两个 Nullable 候选互不占优
            var (unit8, _) = BindUnit(
                "class Box { }\n" +
                "func m(x: String?) { }\nfunc m(x: Box?) { }\n" +
                "func f() { m(null) }\n");
            TestHarness.CheckSemanticError("null 二义", unit8.Diagnostics,
                "Call to 'm' is ambiguous between");

            // 具名实参参与结构过滤（名字不存在即剔除候选）
            var (unit9, bodies9) = BindUnit(
                "func k(a: i32): i32 { return 1 }\n" +
                "func k(b: String): i32 { return 2 }\n" +
                "func f(): i32 { return k(b = \"s\") }\n");
            CheckNoErrors("无诊断（具名选候选）", unit9);
            var stringK = unit9.Symbols.GlobalNamespace.Methods
                .Single(m => m.Name == "k" && m.Parameters[0].Name == "b");
            var kCall = ((BoundReturnStatement)BodyOf(bodies9, "f").Body.Statements[0]).Value
                as BoundCallExpression;
            TestHarness.CheckTrue("具名命中 b 版", kCall != null
                && ReferenceEquals(kCall.Method, stringK));

            // 实例方法重载（receiver 链上色 + ranking）
            var (unit10, bodies10) = BindUnit(
                "class C {\n" +
                "    pub func m(x: i32): i32 { return 1 }\n" +
                "    pub func m(x: String): i32 { return 2 }\n" +
                "}\n" +
                "func f(c: C): i32 { return c.m(1) }\n");
            CheckNoErrors("无诊断（实例方法重载）", unit10);
            TestHarness.Check("实例重载命中 i32 版", BoundDescribe.Body(BodyOf(bodies10, "f")),
                "Body(f, [], [Return(InstCall(m, Param(c,C), [Int(1,i32)], i32))])");

            // init 重载：构造调用同一设施
            var (unit11, bodies11) = BindUnit(
                "class P {\n" +
                "    pub init(x: i32) { }\n" +
                "    pub init(x: String) { }\n" +
                "}\n" +
                "func m(): P { return new P(1) }\n");
            CheckNoErrors("无诊断（init 重载）", unit11);
            var intInit = unit11.Symbols.GlobalNamespace.Types.Single(t => t.Name == "P")
                .Methods.Single(m => m.Kind == MethodKind.Init
                    && ((TypeSymbol)m.Parameters[0].Type!).Name == "i32");
            var newExpr = ((BoundReturnStatement)BodyOf(bodies11, "m").Body.Statements[0]).Value
                as BoundNewExpression;
            TestHarness.CheckTrue("init 重载命中 i32 版", newExpr != null
                && ReferenceEquals(newExpr.Init, intInit));

            // 位置/具名冲突（S8d 修复：位置实参不再静默覆盖具名占位）
            var (unit12, _) = BindUnit(
                "func add(a: i32, b: i32): i32 { return a }\n" +
                "func f(): i32 { return add(a = 1, 2) }\n");
            TestHarness.CheckSemanticError("位置覆盖具名拒绝", unit12.Diagnostics,
                "Duplicate argument for parameter 'a'");

            // 可变参数调用（S9d）：单候选位置包放行——剩余实参打包为
            // BoundVarArgsArgument（规范序最后元素，Type = Array\<Any\>）
            var (unit13, bodies13) = BindUnit(
                "func sum(numbers: i32...): i32 { return 0 }\n" +
                "func f(): i32 { return sum(1, 2) }\n");
            CheckNoErrors("无诊断（可变参数调用）", unit13);
            var sumCall = (BoundCallExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies13, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("位置包打包为最后实参",
                sumCall.Arguments.Count == 1
                && sumCall.Arguments[0] is BoundVarArgsArgument pack
                && !pack.IsNamed && pack.Values.Count == 2);

            // smart cast 交互（M56 × S8d）：收窄后的实参类型参与 ranking——
            // guard 内命中 String 版，guard 外命中 Any 版
            var (unit14, bodies14) = BindUnit(
                "func take(a: Any): i32 { return 1 }\n" +
                "func take(s: String): i32 { return 2 }\n" +
                "func f(x: Any): i32 {\n" +
                "    if (x is String) { return take(x) }\n" +
                "    return take(x)\n" +
                "}\n");
            CheckNoErrors("无诊断（smart cast 参与 ranking）", unit14);
            var takes = unit14.Symbols.GlobalNamespace.Methods.Where(m => m.Name == "take")
                .ToList();
            var anyTake = takes.Single(m => ((TypeSymbol)m.Parameters[0].Type!).Name == "Any");
            var stringTake = takes.Single(
                m => ((TypeSymbol)m.Parameters[0].Type!).Name == "String");
            var fBody = BodyOf(bodies14, "f").Body;
            var guardCall = ((BoundReturnStatement)((BoundIfStatement)fBody.Statements[0])
                .TrueBlock.Statements[0]).Value as BoundCallExpression;
            var outerCall = ((BoundReturnStatement)fBody.Statements[1]).Value
                as BoundCallExpression;
            TestHarness.CheckTrue("收窄命中 String 版", guardCall != null
                && ReferenceEquals(guardCall.Method, stringTake));
            TestHarness.CheckTrue("未收窄命中 Any 版", outerCall != null
                && ReferenceEquals(outerCall.Method, anyTake));

            // 泛型函数声明（S9a）：函数体内泛型参数全放行——形参/返回
            // 类型为 T 正常绑定（引用相等身份），无诊断、无 must-return 级联
            var (unit15, bodies15) = BindUnit("func gf\\<T>(x: T): T { return x }\n");
            CheckNoErrors("无诊断（泛型函数体放行）", unit15);
            TestHarness.Check("泛型函数体绑定形态",
                BoundDescribe.Body(BodyOf(bodies15, "gf")),
                "Body(gf, [], [Return(Param(x,T))])");
            TestHarness.CheckTrue("返回类型为泛型参数的函数体产物",
                bodies15.Single(b => b.Method.Name == "gf").Body.Statements.Count == 1);
        }

        // ===== 诊断累积：函数间互不阻断，函数内多错累积 =====
        private static void TestDiagnosticsAccumulation()
        {
            TestHarness.Section("P3 Diagnostics Accumulation");

            var (unit, bodies) = BindUnit(
                "func bad(): i32 { return nosuch }\n" +
                "func good(): i32 { return 1 }\n");
            TestHarness.CheckSemanticError("坏函数诊断", unit.Diagnostics, "Undefined name: 'nosuch'");
            TestHarness.CheckTrue("函数体互不阻断（两个产物）", bodies.Count == 2);
            TestHarness.Check("好函数正常产出", BoundDescribe.Body(BodyOf(bodies, "good")),
                "Body(good, [], [Return(Int(1,i32))])");

            var (unit2, _) = BindUnit(
                "func f() {\n" +
                "    var x: String = 42\n" +
                "    return y\n" +
                "    var z = nosuch\n" +
                "}\n");
            TestHarness.CheckTrue("函数内多错累积",
                unit2.Diagnostics.Diagnostics.Count(d => d.Phase == DiagnosticPhase.P3) >= 3,
                string.Join("; ", unit2.Diagnostics.Diagnostics.Select(d => d.Message)));
        }

        // ===== 泛型调用（S9b，SYNTAX §4.2 定稿：显式实参唯一、候选池规则、
        // 代入后三步 ranking）=====
        private static void TestGenericCalls()
        {
            TestHarness.Section("P3 Generic Calls (S9b)");

            // 1. 显式实参泛型调用：代入后绑定（返回类型 = 实参，非定义级 T）
            var (unit, bodies) = BindUnit(
                "func identity\\<T>(x: T): T { return x }\n" +
                "func main() { var v = identity\\<i32>(1) }\n");
            CheckNoErrors("无诊断（显式实参泛型调用）", unit);
            var decl = (BoundLocalDeclarationStatement)BodyOf(bodies, "main").Body.Statements[0];
            TestHarness.Check("泛型调用绑定形态",
                BoundDescribe.Expr(decl.Initializer!),
                "Call(identity, [Int(1,i32)], i32)");
            var call = (BoundCallExpression)decl.Initializer!;
            TestHarness.CheckTrue("泛型调用返回类型代入（= 实参 i32）",
                ReferenceEquals(call.Type, unit.Symbols.Bootstrap.Int32));
            TestHarness.CheckTrue("泛型调用 TypeArguments 携带",
                call.TypeArguments.Count == 1
                && ReferenceEquals(call.TypeArguments[0], unit.Symbols.Bootstrap.Int32));

            // 2. 实例泛型方法调用（box.get\<U>(u)——段泛型实参）
            var (unit2, bodies2) = BindUnit(
                "pub open class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item) { }\n" +
                "    pub func get\\<U>(u: U): T { return item }\n" +
                "}\n" +
                "func main() {\n" +
                "    var box = new Box\\<i32>(1)\n" +
                "    var v = box.get\\<String>(\"s\")\n" +
                "}\n");
            CheckNoErrors("无诊断（实例泛型方法调用 + 泛型 new）", unit2);
            var main2 = BodyOf(bodies2, "main").Body;
            var getCall = (BoundInstanceCallExpression)
                ((BoundLocalDeclarationStatement)main2.Statements[1]).Initializer!;
            TestHarness.CheckTrue("实例泛型调用方法符号（定义级）",
                getCall.Method.Name == "get" && getCall.Method.GenericParameters.Count == 1);
            TestHarness.CheckTrue("实例泛型调用返回类型代入（宿主 T = i32）",
                ReferenceEquals(getCall.Type, unit2.Symbols.Bootstrap.Int32));
            TestHarness.CheckTrue("实例泛型调用 TypeArguments 携带",
                getCall.TypeArguments.Count == 1
                && ReferenceEquals(getCall.TypeArguments[0], unit2.Symbols.Bootstrap.String));

            // 3. 泛型实参个数不匹配
            var (unit3, _) = BindUnit(
                "func identity\\<T>(x: T): T { return x }\n" +
                "func main() { var v = identity\\<i32, i64>(1) }\n");
            TestHarness.CheckSemanticError("泛型实参个数不匹配", unit3.Diagnostics,
                "'identity' expects 1 type argument(s), got 2");

            // 4. 无显式实参：从值实参推断 T = i32
            var (unit4, bodies4) = BindUnit(
                "func identity\\<T>(x: T): T { return x }\n" +
                "func main() { var v = identity(1) }\n");
            CheckNoErrors("无诊断（推断 T = i32）", unit4);
            var decl4 = (BoundLocalDeclarationStatement)BodyOf(bodies4, "main").Body.Statements[0];
            var call4 = (BoundCallExpression)decl4.Initializer!;
            TestHarness.CheckTrue("推断调用 TypeArguments 携带 i32",
                call4.TypeArguments.Count == 1
                && ReferenceEquals(call4.TypeArguments[0], unit4.Symbols.Bootstrap.Int32)
                && ReferenceEquals(call4.Type, unit4.Symbols.Bootstrap.Int32));

            // 5. 显式实参与非泛型方法共存：带实参时仅泛型候选（不匹配即拒绝）
            var (unit5, bodies5) = BindUnit(
                "func foo(x: i32): i32 { return x }\n" +
                "func foo\\<T>(x: T): T { return x }\n" +
                "func main() { var v = foo\\<String>(\"s\") }\n");
            CheckNoErrors("无诊断（泛型与非泛型共存显式实参命中泛型）", unit5);
            var decl5 = (BoundLocalDeclarationStatement)BodyOf(bodies5, "main").Body.Statements[0];
            TestHarness.CheckTrue("命中泛型 foo（TypeArguments 非空）",
                ((BoundCallExpression)decl5.Initializer!).TypeArguments.Count == 1);

            // 6. 使用侧约束检查（SYNTAX §3.6）：类型引用实例化点
            //    （bootstrap Box\<T extends ValueType>）
            var (unit6, _) = BindUnit(
                "func ok() { var x = new Box\\<i32>() }\n" +
                "func bad() { var x = new Box\\<Any>() }\n");
            TestHarness.CheckSemanticError("使用侧约束违反（Box<Any>）", unit6.Diagnostics,
                "Type argument 'Any' does not satisfy the 'Extends ValueType' constraint of 'T'");
            TestHarness.CheckTrue("仅违反处报约束诊断（正例 i32 满足）",
                unit6.Diagnostics.Diagnostics.Count(d => d.Message.Contains("constraint")) == 1);

            // 7. 使用侧约束检查：泛型调用实参（自定义约束的泛型函数）
            var (unit7, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func only\\<T extends Animal>(x: T): T { return x }\n" +
                "func ok() { var v = only\\<Dog>(new Dog()) }\n" +
                "func bad() { var v = only\\<i32>(1) }\n");
            TestHarness.CheckSemanticError("泛型调用约束违反（only<i32>）", unit7.Diagnostics,
                "Type argument 'i32' does not satisfy the 'Extends Animal' constraint of 'T'");

            // 8. 泛型 new（S9c）：构造类型回退定义级 init + 宿主参数代入
            //    （`new Box<i32>(1)` 的 init(_ -> item: T) 实参按 i32 绑定）
            var (unit8, bodies8) = BindUnit(
                "pub open class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item) { }\n" +
                "}\n" +
                "func main() { var box = new Box\\<i32>(1) }\n");
            CheckNoErrors("无诊断（泛型 new）", unit8);
            var boxDecl = (BoundLocalDeclarationStatement)BodyOf(bodies8, "main").Body.Statements[0];
            var newExpr = (BoundNewExpression)boxDecl.Initializer!;
            TestHarness.CheckTrue("泛型 new 命中定义级 init", newExpr.Init != null
                && newExpr.Init.Name == "init"
                && ReferenceEquals(newExpr.Init.Owner,
                    unit8.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Box")));
            TestHarness.CheckTrue("泛型 new 构造类型与实参绑定",
                newExpr.Type is TypeSymbol { ConstructedFrom: not null } boxType
                && boxType.ConstructedFrom.Name == "Box"
                && ReferenceEquals(boxType.TypeArguments![0], unit8.Symbols.Bootstrap.Int32)
                && newExpr.Arguments.Count == 1
                && ReferenceEquals(newExpr.Arguments[0].Type, unit8.Symbols.Bootstrap.Int32));

            // 9. 泛型定义不可构造保留（无实参的泛型定义名）——裸名引用在
            // 名字解析层即报元数错误并毒化（NameResolver 末段统一拦截，
            // 与 ApplyTypeArguments 同口径），不再到达 new 的构造检查
            var (unit9, _) = BindUnit(
                "pub open class Box\\<T> { pub init(_ -> item) { } }\n" +
                "func main() { var box = new Box(1) }\n");
            TestHarness.CheckSemanticError("泛型定义不可构造", unit9.Diagnostics,
                "'Box' expects 1 type argument(s), got 0");
        }

        // ===== M108：间接调用泛型（operator call\<T> + 约束；与 direct 同构）=====
        private static void TestIndirectGenericCalls()
        {
            TestHarness.Section("P3 Indirect Generic Calls (M108)");

            // 1. 显式泛型实参间接调用：callable 对象的 operator call\<T>
            var (unit, bodies) = BindUnit(
                "pub class Mapper {\n" +
                "    pub operator call\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "func main() {\n" +
                "    var f = new Mapper()\n" +
                "    var v = f\\<i32>(1)\n" +
                "}\n");
            CheckNoErrors("无诊断（显式泛型间接调用）", unit);
            var main = BodyOf(bodies, "main").Body;
            var call = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)main.Statements[1]).Initializer!;
            TestHarness.CheckTrue("间接调用 IsIndirect + TypeArguments",
                call.IsIndirect && call.TypeArguments.Count == 1
                && ReferenceEquals(call.TypeArguments[0], unit.Symbols.Bootstrap.Int32)
                && ReferenceEquals(call.Type, unit.Symbols.Bootstrap.Int32)
                && call.Method.Name == "call" && call.Method.GenericParameters.Count == 1);

            // 2. 约束违反：间接路径 GenericConstraints 生效
            var (unit2, _) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "pub class OnlyAnimal {\n" +
                "    pub operator call\\<T extends Animal>(x: T): T { return x }\n" +
                "}\n" +
                "func bad() {\n" +
                "    var f = new OnlyAnimal()\n" +
                "    var v = f\\<i32>(1)\n" +
                "}\n");
            TestHarness.CheckSemanticError("间接调用约束违反", unit2.Diagnostics,
                "Type argument 'i32' does not satisfy the 'Extends Animal' constraint of 'T'");

            // 3. 泛型可变包命中 call 运算符（位置包推导）
            var (unit3, bodies3) = BindUnit(
                "pub class Collector {\n" +
                "    pub operator call\\<TArgs...>(values: TArgs...): i32 { return 0 }\n" +
                "}\n" +
                "func main() {\n" +
                "    var f = new Collector()\n" +
                "    var v = f(1, \"s\")\n" +
                "}\n");
            CheckNoErrors("无诊断（泛型可变包间接调用）", unit3);
            var packCall = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bodies3, "main").Body.Statements[1])
                .Initializer!;
            TestHarness.CheckTrue("间接调用 GenericPack 携带",
                packCall.IsIndirect && packCall.GenericPack != null
                && !packCall.GenericPack.IsNamed
                && packCall.GenericPack.TypeArguments.Count == 2
                && ReferenceEquals(packCall.GenericPack.TypeArguments[0],
                    unit3.Symbols.Bootstrap.Int32)
                && ReferenceEquals(packCall.GenericPack.TypeArguments[1],
                    unit3.Symbols.Bootstrap.String));

            // 4. 缺显式实参：间接调用同样推断 T = i32
            var (unit4, bodies4) = BindUnit(
                "pub class Mapper {\n" +
                "    pub operator call\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "func main() {\n" +
                "    var f = new Mapper()\n" +
                "    var v = f(1)\n" +
                "}\n");
            CheckNoErrors("无诊断（间接调用推断）", unit4);
            var inferCall = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bodies4, "main").Body.Statements[1])
                .Initializer!;
            TestHarness.CheckTrue("间接调用推断 TypeArguments",
                inferCall.IsIndirect && inferCall.TypeArguments.Count == 1
                && ReferenceEquals(inferCall.TypeArguments[0], unit4.Symbols.Bootstrap.Int32));
        }

        // ===== 泛型可变参数包（S9d-2，SYNTAX §4.3 定稿⑤：类型实参由
        // 对应值实参的静态类型推导——位置包 ← 位置实参序列、具名包 ←
        // 具名实参「名 → 类型」映射；包实参永不显式书写）=====
        private static void TestGenericVarArgs()
        {
            TestHarness.Section("P3 Generic Variadic Packs (S9d)");

            // 1. 位置包：TArgs... ← 位置实参静态类型序列（GenericPack 携带）
            var (unit, bodies) = BindUnit(
                "func collect\\<TArgs...>(values: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = collect(1, \"s\", true) }\n");
            CheckNoErrors("无诊断（位置泛型包推导）", unit);
            var decl = (BoundLocalDeclarationStatement)BodyOf(bodies, "main").Body.Statements[0];
            var call = (BoundCallExpression)decl.Initializer!;
            TestHarness.CheckTrue("位置包推导产物（TArgs = i32/String/bool）",
                call.GenericPack is { IsNamed: false } pack
                && pack.TypeArguments.Count == 3
                && ReferenceEquals(pack.TypeArguments[0], unit.Symbols.Bootstrap.Int32)
                && ReferenceEquals(pack.TypeArguments[1], unit.Symbols.Bootstrap.String)
                && ReferenceEquals(pack.TypeArguments[2], unit.Symbols.Bootstrap.Bool));

            // 2. 具名包：named TValues... ← 具名实参「名 → 类型」映射
            var (unit2, bodies2) = BindUnit(
                "func update\\<named TValues...>(configs: named TValues...): bool { return true }\n" +
                "func main() { var v = update(isDarkMode = true, userName = \"Andy\") }\n");
            CheckNoErrors("无诊断（具名泛型包推导）", unit2);
            var decl2 = (BoundLocalDeclarationStatement)BodyOf(bodies2, "main").Body.Statements[0];
            var call2 = (BoundCallExpression)decl2.Initializer!;
            TestHarness.CheckTrue("具名包推导产物（名 → 类型映射）",
                call2.GenericPack is { IsNamed: true } namedPack
                && namedPack.NamedTypes.Count == 2
                && namedPack.NamedTypes[0].Name == "isDarkMode"
                && ReferenceEquals(namedPack.NamedTypes[0].Type, unit2.Symbols.Bootstrap.Bool)
                && namedPack.NamedTypes[1].Name == "userName"
                && ReferenceEquals(namedPack.NamedTypes[1].Type, unit2.Symbols.Bootstrap.String));

            // 3. 与固定形参混合：超固定数的位置实参归包
            var (unit3, bodies3) = BindUnit(
                "func mix\\<TArgs...>(prefix: i32, xs: TArgs...): i32 { return prefix }\n" +
                "func main() { var v = mix(1, \"a\", 2.5) }\n");
            CheckNoErrors("无诊断（固定形参与泛型包混合）", unit3);
            var decl3 = (BoundLocalDeclarationStatement)BodyOf(bodies3, "main").Body.Statements[0];
            var call3 = (BoundCallExpression)decl3.Initializer!;
            TestHarness.CheckTrue("超固定数实参归包（TArgs = String/double）",
                call3.GenericPack is { IsNamed: false } pack3
                && pack3.TypeArguments.Count == 2
                && ReferenceEquals(pack3.TypeArguments[0], unit3.Symbols.Bootstrap.String)
                && ReferenceEquals(pack3.TypeArguments[1], unit3.Symbols.Bootstrap.Double));

            // 4. 泛型包方法不再被「需要显式实参」排除（单候选可绑定）；
            //    与非泛型共存时多候选含可变仍归口（包不参与 ranking）
            var (unit4, _) = BindUnit(
                "func pick(x: i32): i32 { return 1 }\n" +
                "func pick\\<TArgs...>(xs: TArgs...): i32 { return 2 }\n" +
                "func main() { var v = pick(1) }\n");
            TestHarness.CheckSemanticError("非泛型与泛型包共存多候选归口", unit4.Diagnostics,
                "P3: overload resolution with variadic parameters is not supported yet");

            // 5. 固定泛型参数可由值实参推断（包推导不独占推断通道）
            var (unit5, bodies5) = BindUnit(
                "func gf\\<T>(x: T): T { return x }\n" +
                "func main() { var v = gf(1) }\n");
            CheckNoErrors("无诊断（固定泛型推断）", unit5);
            var gfCall = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bodies5, "main").Body.Statements[0])
                .Initializer!;
            TestHarness.CheckTrue("gf 推断 T = i32",
                gfCall.TypeArguments.Count == 1
                && ReferenceEquals(gfCall.TypeArguments[0], unit5.Symbols.Bootstrap.Int32));

            // 6. 约束违反：逐推导类型做约束检查（extends ValueType，定位到实参）
            var (unit6, _) = BindUnit(
                "open class C { }\n" +
                "func only\\<TArgs... extends ValueType>(xs: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = only(1, new C()) }\n");
            TestHarness.CheckSemanticError("包推导约束违反（C 非 ValueType）", unit6.Diagnostics,
                "Type argument 'C' does not satisfy the 'Extends ValueType' constraint of 'TArgs'");

            // 7. 多可变泛型参数归口诊断
            var (unit7, _) = BindUnit(
                "func multi\\<TArgs..., TValues...>(a: TArgs..., b: TValues...): i32 { return 0 }\n" +
                "func main() { var v = multi(1) }\n");
            TestHarness.CheckSemanticError("多可变泛型参数归口", unit7.Diagnostics,
                "P3: multiple variadic generic parameters are not supported yet (S9d)");

            // 8. 包内 null 字面量无法推导类型 → 归口诊断
            var (unit8, _) = BindUnit(
                "func n\\<TArgs...>(xs: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = n(null) }\n");
            TestHarness.CheckSemanticError("包内 null 无法推导", unit8.Diagnostics,
                "P3: cannot infer a type argument from a null literal in a generic variadic pack (S9d)");

            // 9. 显式泛型实参命中全可变包候选拦截（§4.3：包类型实参由值实参
            //    推导，永不显式书写）——纯包候选显式实参报错
            var (unit9, _) = BindUnit(
                "func pack\\<TArgs...>(xs: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = pack\\<i32>(1) }\n");
            TestHarness.CheckSemanticError("纯包候选显式实参拒绝", unit9.Diagnostics,
                "'pack': generic variadic pack arguments are derived from value arguments");

            // 10. 普通泛型不受误伤：同名全可变包候选共存时显式实参仍命中固定
            //     泛型候选（包候选被排除出显式路径，不参与 ranking）
            var (unit10, bodies10) = BindUnit(
                "func foo\\<T>(x: T): T { return x }\n" +
                "func foo\\<TArgs...>(xs: TArgs...): i32 { return 0 }\n" +
                "func main() { var v = foo\\<i32>(1) }\n");
            CheckNoErrors("无诊断（普通泛型共存不受误伤）", unit10);
            var decl10 = (BoundLocalDeclarationStatement)BodyOf(bodies10, "main").Body.Statements[0];
            var call10 = (BoundCallExpression)decl10.Initializer!;
            TestHarness.CheckTrue("显式实参命中固定泛型候选",
                call10.Method.GenericParameters.Count == 1
                && !call10.Method.GenericParameters[0].IsVariadic
                && call10.TypeArguments.Count == 1
                && ReferenceEquals(call10.TypeArguments[0], unit10.Symbols.Bootstrap.Int32));

            // ===== #25① 混合泛型 `f\<T, TArgs...>`：显式实参按固定元数匹配
            // （SYNTAX §4.2/§4.3）——TypeArguments 只收固定显式，包进 GenericPack =====

            // 11. 正例：固定显式 1 个 + 位置包由值实参推导；返回类型代入 T
            var (unit11, bodies11) = BindUnit(
                "func mix\\<T, TArgs...>(seed: T, xs: TArgs...): T { return seed }\n" +
                "func main() { var v = mix\\<i32>(1, \"a\", true) }\n");
            CheckNoErrors("无诊断（混合泛型显式调用正例）", unit11);
            var decl11 = (BoundLocalDeclarationStatement)BodyOf(bodies11, "main").Body.Statements[0];
            var call11 = (BoundCallExpression)decl11.Initializer!;
            TestHarness.CheckTrue("混合调用返回类型代入（T = i32）",
                ReferenceEquals(call11.Type, unit11.Symbols.Bootstrap.Int32));
            TestHarness.CheckTrue("TypeArguments 只保留固定显式实参",
                call11.TypeArguments.Count == 1
                && ReferenceEquals(call11.TypeArguments[0], unit11.Symbols.Bootstrap.Int32));
            TestHarness.CheckTrue("GenericPack 承载包推导（String/bool）",
                call11.GenericPack is { IsNamed: false } pack11
                && pack11.TypeArguments.Count == 2
                && ReferenceEquals(pack11.TypeArguments[0], unit11.Symbols.Bootstrap.String)
                && ReferenceEquals(pack11.TypeArguments[1], unit11.Symbols.Bootstrap.Bool));
            TestHarness.CheckTrue("TypeArguments 与 GenericPack 分离（包类型不进 TypeArguments）",
                call11.TypeArguments.Count == 1 && call11.GenericPack != null
                && !call11.TypeArguments.Any(a =>
                    ReferenceEquals(a, unit11.Symbols.Bootstrap.String)
                    || ReferenceEquals(a, unit11.Symbols.Bootstrap.Bool)));

            // 12. 固定数量错误：多写（把包实参也写进显式列表）
            var (unit12, _) = BindUnit(
                "func mix\\<T, TArgs...>(seed: T, xs: TArgs...): T { return seed }\n" +
                "func main() { var v = mix\\<i32, String>(1, \"a\") }\n");
            TestHarness.CheckSemanticError("混合形态多写包实参", unit12.Diagnostics,
                "'mix' expects 1 type argument(s), got 2");

            // 13. 固定数量错误：少写（缺固定显式）
            var (unit13, _) = BindUnit(
                "func mix\\<T, U, TArgs...>(a: T, b: U, xs: TArgs...): T { return a }\n" +
                "func main() { var v = mix\\<i32>(1, \"s\") }\n");
            TestHarness.CheckSemanticError("混合形态固定数量不足", unit13.Diagnostics,
                "'mix' expects 2 type argument(s), got 1");

            // 14. 混合缺显式：固定 T 从 seed 推断，包仍由值实参推导
            var (unit14, bodies14) = BindUnit(
                "func mix\\<T, TArgs...>(seed: T, xs: TArgs...): T { return seed }\n" +
                "func main() { var v = mix(1, \"a\") }\n");
            CheckNoErrors("无诊断（混合形态固定部分推断）", unit14);
            var mixCall = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bodies14, "main").Body.Statements[0])
                .Initializer!;
            TestHarness.CheckTrue("混合推断 TypeArguments/GenericPack",
                mixCall.TypeArguments.Count == 1
                && ReferenceEquals(mixCall.TypeArguments[0], unit14.Symbols.Bootstrap.Int32)
                && mixCall.GenericPack is { IsNamed: false } mixPack
                && mixPack.TypeArguments.Count == 1
                && ReferenceEquals(mixPack.TypeArguments[0], unit14.Symbols.Bootstrap.String));

            // 15. 具名包混合：固定显式 + named 包推导
            var (unit15, bodies15) = BindUnit(
                "func cfg\\<T, named TValues...>(seed: T, opts: named TValues...): T { return seed }\n" +
                "func main() { var v = cfg\\<i32>(1, flag = true, name = \"x\") }\n");
            CheckNoErrors("无诊断（混合具名包）", unit15);
            var call15 = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bodies15, "main").Body.Statements[0])
                .Initializer!;
            TestHarness.CheckTrue("混合具名包 TypeArguments/GenericPack 分离",
                call15.TypeArguments.Count == 1
                && ReferenceEquals(call15.TypeArguments[0], unit15.Symbols.Bootstrap.Int32)
                && call15.GenericPack is { IsNamed: true } named15
                && named15.NamedTypes.Count == 2
                && named15.NamedTypes[0].Name == "flag"
                && ReferenceEquals(named15.NamedTypes[0].Type, unit15.Symbols.Bootstrap.Bool));
        }

        // ===== 泛型 operator 名字调用（S9f 复核 M69 注记：SYNTAX §4.2
        // 定稿「operator 名字形式与普通方法同规则」——a.plus\<T>(b) 命中
        // operator；运算符位置（a + b）/for 头/索引仍走专用解析）=====
        private static void TestOperatorNameCalls()
        {
            TestHarness.Section("P3 Operator Name Calls (S9f)");

            // 1. operator 名字调用（非泛型形态）
            var (unit, bodies) = BindUnit(
                "class Vec {\n" +
                "    pub operator plus(other: Vec): Vec { return this }\n" +
                "}\n" +
                "func f(a: Vec, b: Vec): Vec { return a.plus(b) }\n");
            CheckNoErrors("无诊断（operator 名字调用）", unit);
            var plus = (BoundInstanceCallExpression)((BoundReturnStatement)
                BodyOf(bodies, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("名字调用命中 operator",
                plus.Method.Kind == MethodKind.Operator && plus.Method.Name == "plus");

            // 2. 泛型 operator 名字调用：显式实参 + 代入后返回类型
            var (unit2, bodies2) = BindUnit(
                "class Vec {\n" +
                "    pub operator plus\\<TAnother>(other: TAnother): Vec { return this }\n" +
                "}\n" +
                "func f(a: Vec): Vec { return a.plus\\<String>(\"s\") }\n");
            CheckNoErrors("无诊断（泛型 operator 名字调用）", unit2);
            var genericPlus = (BoundInstanceCallExpression)((BoundReturnStatement)
                BodyOf(bodies2, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("泛型 operator 显式实参携带",
                genericPlus.Method.Kind == MethodKind.Operator
                && genericPlus.TypeArguments.Count == 1
                && ReferenceEquals(genericPlus.TypeArguments[0],
                    unit2.Symbols.Bootstrap.String));

            // 3. 运算符位置派发：a + b 查 operator plus，Bound 仍为 intrinsic Add
            var (unit3, bodies3) = BindUnit(
                "class Vec { pub operator plus(other: Vec): Vec { return this } }\n" +
                "func f(a: Vec, b: Vec): Vec { return a + b }\n");
            CheckNoErrors("无诊断（运算符位置 plus）", unit3);
            TestHarness.Check("运算符位置 plus 绑 Add", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(Binary(Add, Param(a,Vec), Param(b,Vec), Vec))])");
        }

        // ===== 用户 operator 运算符位置（SYNTAX §13.2）=====
        private static void TestUserOperatorPositions()
        {
            TestHarness.Section("P3 User Operator Positions (§13.2)");

            var header =
                "class Vec {\n" +
                "    pub operator plus(other: Vec): Vec { return this }\n" +
                "    pub operator minus(other: Vec): Vec { return this }\n" +
                "    pub operator times(other: Vec): Vec { return this }\n" +
                "    pub operator div(other: Vec): Vec { return this }\n" +
                "    pub operator mod(other: Vec): Vec { return this }\n" +
                "    pub operator bitwiseAnd(other: Vec): Vec { return this }\n" +
                "    pub operator bitwiseOr(other: Vec): Vec { return this }\n" +
                "    pub operator bitwiseXor(other: Vec): Vec { return this }\n" +
                "    pub operator leftShift(bits: i32): Vec { return this }\n" +
                "    pub operator rightShift(bits: i32): Vec { return this }\n" +
                "    pub operator unsignedRightShift(bits: i32): Vec { return this }\n" +
                "    pub operator compareTo(other: Vec): i32 { return 0 }\n" +
                "    pub operator and(other: Vec): Vec { return this }\n" +
                "    pub operator or(other: Vec): Vec { return this }\n" +
                "    pub operator opposite(): Vec { return this }\n" +
                "    pub operator not(): Vec { return this }\n" +
                "    pub operator bitwiseNot(): Vec { return this }\n" +
                "}\n";

            var (unit, bodies) = BindUnit(header +
                "func add(a: Vec, b: Vec): Vec { return a + b }\n" +
                "func sub(a: Vec, b: Vec): Vec { return a - b }\n" +
                "func mul(a: Vec, b: Vec): Vec { return a * b }\n" +
                "func quot(a: Vec, b: Vec): Vec { return a / b }\n" +
                "func rem(a: Vec, b: Vec): Vec { return a % b }\n" +
                "func band(a: Vec, b: Vec): Vec { return a & b }\n" +
                "func bor(a: Vec, b: Vec): Vec { return a | b }\n" +
                "func bxor(a: Vec, b: Vec): Vec { return a ^ b }\n" +
                "func shl(a: Vec, n: i32): Vec { return a << n }\n" +
                "func shr(a: Vec, n: i32): Vec { return a >> n }\n" +
                "func shru(a: Vec, n: i32): Vec { return a >>> n }\n" +
                "func lt(a: Vec, b: Vec): bool { return a < b }\n" +
                "func le(a: Vec, b: Vec): bool { return a <= b }\n" +
                "func gt(a: Vec, b: Vec): bool { return a > b }\n" +
                "func ge(a: Vec, b: Vec): bool { return a >= b }\n" +
                "func andalso(a: Vec, b: Vec): Vec { return a and b }\n" +
                "func orelse(a: Vec, b: Vec): Vec { return a or b }\n" +
                "func neg(a: Vec): Vec { return -a }\n" +
                "func lnot(a: Vec): Vec { return not a }\n" +
                "func bnot(a: Vec): Vec { return !a }\n" +
                "func addEq(a: Vec, b: Vec): Vec { a += b\nreturn a }\n" +
                "func modEq(a: Vec, b: Vec): Vec { a %= b\nreturn a }\n");
            CheckNoErrors("无诊断（用户运算符位置全家）", unit);
            TestHarness.Check("plus", BoundDescribe.Body(BodyOf(bodies, "add")),
                "Body(add, [], [Return(Binary(Add, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("minus", BoundDescribe.Body(BodyOf(bodies, "sub")),
                "Body(sub, [], [Return(Binary(Sub, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("times", BoundDescribe.Body(BodyOf(bodies, "mul")),
                "Body(mul, [], [Return(Binary(Mul, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("div", BoundDescribe.Body(BodyOf(bodies, "quot")),
                "Body(quot, [], [Return(Binary(Div, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("mod", BoundDescribe.Body(BodyOf(bodies, "rem")),
                "Body(rem, [], [Return(Binary(Mod, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("bitwiseAnd", BoundDescribe.Body(BodyOf(bodies, "band")),
                "Body(band, [], [Return(Binary(BinAnd, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("bitwiseOr", BoundDescribe.Body(BodyOf(bodies, "bor")),
                "Body(bor, [], [Return(Binary(BinOr, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("bitwiseXor", BoundDescribe.Body(BodyOf(bodies, "bxor")),
                "Body(bxor, [], [Return(Binary(BinXor, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("leftShift", BoundDescribe.Body(BodyOf(bodies, "shl")),
                "Body(shl, [], [Return(Binary(ShiftLeft, Param(a,Vec), Param(n,i32), Vec))])");
            TestHarness.Check("rightShift", BoundDescribe.Body(BodyOf(bodies, "shr")),
                "Body(shr, [], [Return(Binary(ShiftRight, Param(a,Vec), Param(n,i32), Vec))])");
            TestHarness.Check("unsignedRightShift", BoundDescribe.Body(BodyOf(bodies, "shru")),
                "Body(shru, [], [Return(Binary(ShiftRightUnsigned, Param(a,Vec), Param(n,i32), Vec))])");
            TestHarness.Check("compareTo < 结果 bool", BoundDescribe.Body(BodyOf(bodies, "lt")),
                "Body(lt, [], [Return(Binary(CmpLt, Param(a,Vec), Param(b,Vec), bool))])");
            TestHarness.Check("compareTo <=", BoundDescribe.Body(BodyOf(bodies, "le")),
                "Body(le, [], [Return(Binary(CmpLe, Param(a,Vec), Param(b,Vec), bool))])");
            TestHarness.Check("compareTo >", BoundDescribe.Body(BodyOf(bodies, "gt")),
                "Body(gt, [], [Return(Binary(CmpGt, Param(a,Vec), Param(b,Vec), bool))])");
            TestHarness.Check("compareTo >=", BoundDescribe.Body(BodyOf(bodies, "ge")),
                "Body(ge, [], [Return(Binary(CmpGe, Param(a,Vec), Param(b,Vec), bool))])");
            TestHarness.Check("and 不短路（Binary 节点）", BoundDescribe.Body(BodyOf(bodies, "andalso")),
                "Body(andalso, [], [Return(Binary(And, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("or 不短路（Binary 节点）", BoundDescribe.Body(BodyOf(bodies, "orelse")),
                "Body(orelse, [], [Return(Binary(Or, Param(a,Vec), Param(b,Vec), Vec))])");
            TestHarness.Check("opposite", BoundDescribe.Body(BodyOf(bodies, "neg")),
                "Body(neg, [], [Return(Unary(Opposite, Param(a,Vec), Vec))])");
            TestHarness.Check("not", BoundDescribe.Body(BodyOf(bodies, "lnot")),
                "Body(lnot, [], [Return(Unary(Not, Param(a,Vec), Vec))])");
            TestHarness.Check("bitwiseNot", BoundDescribe.Body(BodyOf(bodies, "bnot")),
                "Body(bnot, [], [Return(Unary(BinNot, Param(a,Vec), Vec))])");
            TestHarness.Check("复合赋值 +=", BoundDescribe.Body(BodyOf(bodies, "addEq")),
                "Body(addEq, [], [ExprStmt(CompoundAssign(Add, Param(a,Vec), Param(b,Vec), Vec)); " +
                "Return(Param(a,Vec))])");
            TestHarness.Check("复合赋值 %=", BoundDescribe.Body(BodyOf(bodies, "modEq")),
                "Body(modEq, [], [ExprStmt(CompoundAssign(Mod, Param(a,Vec), Param(b,Vec), Vec)); " +
                "Return(Param(a,Vec))])");

            // 负例：未定义 operator
            var (unit2, _) = BindUnit(
                "class Plain { }\nfunc f(a: Plain, b: Plain): Plain { return a + b }\n");
            TestHarness.CheckSemanticError("未定义 plus", unit2.Diagnostics,
                "Operator '+' is not defined for type 'Plain'");
            var (unit3, _) = BindUnit(
                "class Plain { }\nfunc f(a: Plain): Plain { return -a }\n");
            TestHarness.CheckSemanticError("未定义 opposite", unit3.Diagnostics,
                "Operator '-' is not defined for type 'Plain'");
            var (unit4, _) = BindUnit(
                "class Plain { }\nfunc f(a: Plain, b: Plain): bool { return a < b }\n");
            TestHarness.CheckSemanticError("未定义 compareTo", unit4.Diagnostics,
                "Operator '<' is not defined for type 'Plain'");
            var (unit5, _) = BindUnit(
                "class Plain { }\nfunc f(a: Plain, b: Plain) { a += b }\n");
            TestHarness.CheckSemanticError("未定义复合赋值 plus", unit5.Diagnostics,
                "Operator '+=' is not defined for type 'Plain'");

            // and/or 不短路：右侧类型错误仍报（两侧都绑定）
            var (unit6, _) = BindUnit(
                "class Flag { pub operator and(other: Flag): Flag { return this } }\n" +
                "func f(a: Flag): Flag { return a and 1 }\n");
            TestHarness.CheckSemanticError("用户 and 右侧仍绑定", unit6.Diagnostics,
                "No applicable overload of 'and'");

            // 泛型 operator 运算符位置：从右操作数推断
            var (unit7, bodies7) = BindUnit(
                "class Vec {\n" +
                "    pub operator plus\\<TAnother>(other: TAnother): Vec { return this }\n" +
                "}\n" +
                "func f(a: Vec): Vec { return a + \"s\" }\n");
            CheckNoErrors("无诊断（泛型 plus 运算符位置推断）", unit7);
            TestHarness.Check("运算符位置推断 plus 绑 Add",
                BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [], [Return(Binary(Add, Param(a,Vec), Str(\"s\",String), Vec))])");
        }

        // ===== 固定泛型参数推断（SYNTAX §4.2 修订）=====
        private static void TestGenericInference()
        {
            TestHarness.Section("P3 Generic Inference (§4.2)");

            // 单参数：i32 / String / 用户类型
            var (uI32, bI32) = BindUnit(
                "func id\\<T>(x: T): T { return x }\n" +
                "func main() { var v = id(1) }\n");
            CheckNoErrors("单参推断 i32", uI32);
            var cI32 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bI32, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("id(1) → T=i32",
                cI32.TypeArguments.Count == 1
                && ReferenceEquals(cI32.TypeArguments[0], uI32.Symbols.Bootstrap.Int32));

            var (uStr, bStr) = BindUnit(
                "func id\\<T>(x: T): T { return x }\n" +
                "func main() { var v = id(\"hi\") }\n");
            CheckNoErrors("单参推断 String", uStr);
            var cStr = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bStr, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("id(\"hi\") → T=String",
                ReferenceEquals(cStr.TypeArguments[0], uStr.Symbols.Bootstrap.String));

            var (uUser, bUser) = BindUnit(
                "class Spot { }\n" +
                "func id\\<T>(x: T): T { return x }\n" +
                "func main() { var v = id(new Spot()) }\n");
            CheckNoErrors("单参推断用户类型", uUser);
            var cUser = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bUser, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("id(new Spot()) → T=Spot",
                cUser.TypeArguments[0] is TypeSymbol { Name: "Spot" });

            // 构造模式：Array\<T>、Map\<K,V>、嵌套、T?
            var (uArr, bArr) = BindUnit(
                "func firstOf\\<T>(arr: Array\\<T>): T { return (arr[0] as T) }\n" +
                "func main(a: Array\\<i32>): i32 { return firstOf(a) }\n");
            CheckNoErrors("Array<T> 推断", uArr);
            var cArr = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bArr, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("firstOf(Array<i32>) → T=i32",
                cArr.TypeArguments.Count == 1
                && ReferenceEquals(cArr.TypeArguments[0], uArr.Symbols.Bootstrap.Int32));

            var (uMap, bMap) = BindUnit(
                "func keys\\<K, V>(m: Map\\<K, V>): i32 { return 0 }\n" +
                "func main(m: Map\\<String, i32>): i32 { return keys(m) }\n");
            CheckNoErrors("Map<K,V> 推断", uMap);
            var cMap = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bMap, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("keys(Map<String,i32>) → K=String V=i32",
                cMap.TypeArguments.Count == 2
                && ReferenceEquals(cMap.TypeArguments[0], uMap.Symbols.Bootstrap.String)
                && ReferenceEquals(cMap.TypeArguments[1], uMap.Symbols.Bootstrap.Int32));

            var (uNest, bNest) = BindUnit(
                "func firstNested\\<T>(a: Array\\<Array\\<T>>): Array\\<T> { return (a[0] as Array\\<T>) }\n" +
                "func main(a: Array\\<Array\\<i32>>): Array\\<i32> { return firstNested(a) }\n");
            CheckNoErrors("嵌套 Array<Array<T>> 推断", uNest);
            var cNest = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bNest, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("firstNested(Array<Array<i32>>) → T=i32",
                ReferenceEquals(cNest.TypeArguments[0], uNest.Symbols.Bootstrap.Int32));

            var (uNull, bNull) = BindUnit(
                "func wrap\\<T>(v: T?): T? { return v }\n" +
                "func main(n: i32?): i32? { return wrap(n) }\n");
            CheckNoErrors("T? 推断", uNull);
            var cNull = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bNull, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("wrap(i32?) → T=i32",
                ReferenceEquals(cNull.TypeArguments[0], uNull.Symbols.Bootstrap.Int32));

            // 双参数各绑一参
            var (uPair, bPair) = BindUnit(
                "func pair\\<A, B>(x: A, y: B): A { return x }\n" +
                "func main() { var v = pair(1, \"s\") }\n");
            CheckNoErrors("双参各绑一参", uPair);
            var cPair = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bPair, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("pair(i32, String) → A=i32 B=String",
                cPair.TypeArguments.Count == 2
                && ReferenceEquals(cPair.TypeArguments[0], uPair.Symbols.Bootstrap.Int32)
                && ReferenceEquals(cPair.TypeArguments[1], uPair.Symbols.Bootstrap.String));

            // 冲突绑定
            var (uConf, _) = BindUnit(
                "func same\\<T>(x: T, y: T): T { return x }\n" +
                "func main() { var v = same(1, \"s\") }\n");
            TestHarness.CheckSemanticError("冲突绑定报错", uConf.Diagnostics,
                "cannot infer type arguments from the given arguments");

            // 推断实参违反约束
            var (uCst, _) = BindUnit(
                "open class Animal { }\n" +
                "func only\\<T extends Animal>(x: T): T { return x }\n" +
                "func main() { var v = only(1) }\n");
            TestHarness.CheckSemanticError("推断违反约束", uCst.Diagnostics,
                "Type argument 'i32' does not satisfy the 'Extends Animal' constraint of 'T'");

            // 返回独占 T
            var (uRet, _) = BindUnit(
                "func make\\<T>(): T { return make\\<T>() }\n" +
                "func main() { var v = make() }\n");
            TestHarness.CheckSemanticError("返回独占 T 无法推断", uRet.Diagnostics,
                "cannot infer type arguments from the given arguments");

            // null 唯一绑定源
            var (uN1, _) = BindUnit(
                "func id\\<T>(x: T): T { return x }\n" +
                "func main() { var v = id(null) }\n");
            TestHarness.CheckSemanticError("null 唯一绑定源", uN1.Diagnostics,
                "cannot infer type arguments from the given arguments");

            // 多处绑定中 null 不参与，另一处成功（null 落在 T? 槽）
            var (uN2, bN2) = BindUnit(
                "func same\\<T>(x: T?, y: T): T { return y }\n" +
                "func main() { var v = same(null, 1) }\n");
            CheckNoErrors("null 不参与、另一处成功", uN2);
            var cN2 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bN2, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("same(null, 1) → T=i32",
                ReferenceEquals(cN2.TypeArguments[0], uN2.Symbols.Bootstrap.Int32));

            // 非泛型 vs 推断泛型同台 ranking
            // show(Any) vs show\<T>(T)：推断后 T=String 更具体 → 泛型胜
            var (uRank1, bRank1) = BindUnit(
                "func show(x: Any): i32 { return 1 }\n" +
                "func show\\<T>(x: T): i32 { return 2 }\n" +
                "func main() { var v = show(\"hi\") }\n");
            CheckNoErrors("非泛型 Any vs 推断 String：泛型胜", uRank1);
            var cRank1 = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bRank1, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("更具体的推断泛型胜出",
                cRank1.Method.GenericParameters.Count == 1
                && cRank1.TypeArguments.Count == 1
                && ReferenceEquals(cRank1.TypeArguments[0], uRank1.Symbols.Bootstrap.String));

            // show(String) vs show\<T>(T)：推断后两签名同等具体 → 二义
            var (uRank2, _) = BindUnit(
                "func show(x: String): i32 { return 1 }\n" +
                "func show\\<T>(x: T): i32 { return 2 }\n" +
                "func main() { var v = show(\"hi\") }\n");
            TestHarness.CheckSemanticError("同等具体：非泛型与推断泛型二义", uRank2.Diagnostics,
                "is ambiguous between");

            // 显式实参优先且不混推：带 \\<String> 只走泛型，非泛型不入池
            var (uExp, bExp) = BindUnit(
                "func foo(x: i32): i32 { return x }\n" +
                "func foo\\<T>(x: T): T { return x }\n" +
                "func main() { var v = foo\\<String>(\"s\") }\n");
            CheckNoErrors("显式实参只走泛型", uExp);
            var cExp = (BoundCallExpression)((BoundLocalDeclarationStatement)
                BodyOf(bExp, "main").Body.Statements[0]).Initializer!;
            TestHarness.CheckTrue("显式 String 命中泛型",
                cExp.TypeArguments.Count == 1
                && ReferenceEquals(cExp.TypeArguments[0], uExp.Symbols.Bootstrap.String));

            // 显式元数不符仍报错
            var (uArity, _) = BindUnit(
                "func id\\<T>(x: T): T { return x }\n" +
                "func main() { var v = id\\<i32, i64>(1) }\n");
            TestHarness.CheckSemanticError("显式元数不符", uArity.Diagnostics,
                "'id' expects 1 type argument(s), got 2");

            // 宿主泛型代入：Box\<T> 内方法推断 U
            var (uHost, bHost) = BindUnit(
                "pub open class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item) { }\n" +
                "    pub func get\\<U>(u: U): T { return item }\n" +
                "}\n" +
                "func main() {\n" +
                "    var box = new Box\\<i32>(1)\n" +
                "    var v = box.get(\"s\")\n" +
                "}\n");
            CheckNoErrors("宿主代入 + 方法推断", uHost);
            var cHost = (BoundInstanceCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bHost, "main").Body.Statements[1])
                .Initializer!;
            TestHarness.CheckTrue("box.get(\"s\") → U=String，返回宿主 T=i32",
                cHost.TypeArguments.Count == 1
                && ReferenceEquals(cHost.TypeArguments[0], uHost.Symbols.Bootstrap.String)
                && ReferenceEquals(cHost.Type, uHost.Symbols.Bootstrap.Int32));

            // ext 方法推断
            var (uExt, bExt) = BindUnit(
                "pub ext func String.wrap\\<T>(x: T): T { return x }\n" +
                "func main() { var v = \"s\".wrap(1) }\n");
            CheckNoErrors("ext 方法推断", uExt);
            var cExt = (BoundInstanceCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bExt, "main").Body.Statements[0])
                .Initializer!;
            TestHarness.CheckTrue("\"s\".wrap(1) → T=i32",
                cExt.TypeArguments.Count == 1
                && ReferenceEquals(cExt.TypeArguments[0], uExt.Symbols.Bootstrap.Int32));

            // 推断结果的 shared 安全检查仍生效
            var (uShare, _) = BindUnit(
                "class LocalUser { }\n" +
                "async func pass\\<T>(x: T): T { return x }\n" +
                "func f() { var v = pass(new LocalUser()) }\n");
            TestHarness.CheckSemanticError("推断实参仍走闸门 5", uShare.Diagnostics,
                "type argument of async function 'pass' must be a shared-safe type: 'LocalUser'");

            // 用户泛型类型构造模式
            var (uBox, bBox) = BindUnit(
                "pub open class Box\\<T> {\n" +
                "    pub var item: T\n" +
                "    pub init(_ -> item) { }\n" +
                "}\n" +
                "func take\\<T>(b: Box\\<T>): T { return b.item }\n" +
                "func main() {\n" +
                "    var box = new Box\\<String>(\"s\")\n" +
                "    var v = take(box)\n" +
                "}\n");
            CheckNoErrors("用户泛型类型构造模式", uBox);
            var cBox = (BoundCallExpression)
                ((BoundLocalDeclarationStatement)BodyOf(bBox, "main").Body.Statements[1])
                .Initializer!;
            TestHarness.CheckTrue("take(Box<String>) → T=String",
                ReferenceEquals(cBox.TypeArguments[0], uBox.Symbols.Bootstrap.String));

            // 类型级构造器不推断（回归：new Box 仍报元数）
            var (uNew, _) = BindUnit(
                "pub open class Box\\<T> { pub init() { } }\n" +
                "func main() { var box = new Box() }\n");
            TestHarness.CheckSemanticError("类型级构造器不推断", uNew.Diagnostics,
                "'Box' expects 1 type argument(s), got 0");

            // wrapper 方法推断（place 作 InstCall receiver）
            var (uWrap, bWrap) = BindUnit(
                "@WrapperTarget(.Entity)\n" +
                "pub wrapper Logged {\n" +
                "    pub func wrap\\<T>(x: T): T { return x }\n" +
                "}\n" +
                "@Logged\n" +
                "pub class Service { pub init() }\n" +
                "func main(s: Service): i32 { return s:Logged.wrap(1) }\n");
            CheckNoErrors("wrapper 方法推断", uWrap);
            var cWrap = (BoundInstanceCallExpression)((BoundReturnStatement)
                BodyOf(bWrap, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("s:Logged.wrap(1) → T=i32",
                cWrap.TypeArguments.Count == 1
                && ReferenceEquals(cWrap.TypeArguments[0], uWrap.Symbols.Bootstrap.Int32));

            // 接口闭包推断：List\<T> implements IEnumerable\<T>——实参类型
            // 与构造形参的关系在 implements 层（BaseType 链到不了）
            var (uIfc, bIfc) = BindUnitWithStdlib(
                "import core.collections.*\n" +
                "func total\\<T>(src: IEnumerable\\<T>): i32 { return 0 }\n" +
                "func main(l: List\\<i32>): i32 { return total(l) }\n");
            CheckNoErrors("接口闭包推断 List→IEnumerable", uIfc);
            var cIfc = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bIfc, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("total(List<i32>) → T=i32",
                cIfc.TypeArguments.Count == 1
                && ReferenceEquals(cIfc.TypeArguments[0], uIfc.Symbols.Bootstrap.Int32));

            // 接口闭包推断跨实参代入：Map\<K,V> implements
            // IEnumerable\<core.Pair\<K,V>>——闭包形态已代入 K/V
            var (uMapIfc, bMapIfc) = BindUnitWithStdlib(
                "import core.collections.*\n" +
                "func count\\<T>(src: IEnumerable\\<T>): i32 { return 0 }\n" +
                "func main(m: Map\\<String, i32>): i32 { return count(m) }\n");
            CheckNoErrors("接口闭包推断 Map→IEnumerable<Pair>", uMapIfc);
            var cMapIfc = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bMapIfc, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("count(Map<String,i32>) → T=Pair<String,i32>",
                cMapIfc.TypeArguments.Count == 1
                && cMapIfc.TypeArguments[0] is TypeSymbol { Name: "Pair", TypeArguments: { } pairArgs }
                && pairArgs.Count == 2
                && ReferenceEquals(pairArgs[0], uMapIfc.Symbols.Bootstrap.String)
                && ReferenceEquals(pairArgs[1], uMapIfc.Symbols.Bootstrap.Int32));

            // 构造接口实参直接匹配不回归：iterate() 返回 IEnumerator\<i32>
            //（代入后形态）→ T=i32
            var (uEnum, bEnum) = BindUnitWithStdlib(
                "import core.collections.*\n" +
                "func step\\<T>(e: IEnumerator\\<T>): i32 { return 0 }\n" +
                "func main(l: List\\<i32>): i32 { return step(l.iterate()) }\n");
            CheckNoErrors("构造接口实参直接匹配", uEnum);
            var cEnum = (BoundCallExpression)((BoundReturnStatement)
                BodyOf(bEnum, "main").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("step(IEnumerator<i32>) → T=i32",
                cEnum.TypeArguments.Count == 1
                && ReferenceEquals(cEnum.TypeArguments[0], uEnum.Symbols.Bootstrap.Int32));

            // 接口形参负例不回归：实参类型与接口无实现关系 → 仍无法推断
            var (uNeg, _) = BindUnitWithStdlib(
                "import core.collections.*\n" +
                "func total\\<T>(src: IEnumerable\\<T>): i32 { return 0 }\n" +
                "func main(s: String): i32 { return total(s) }\n");
            TestHarness.CheckSemanticError("接口形参无实现关系仍无法推断",
                uNeg.Diagnostics,
                "cannot infer type arguments from the given arguments");
        }
    }
}
