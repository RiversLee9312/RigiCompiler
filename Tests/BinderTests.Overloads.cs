using System.Linq;

namespace LatteCompiler.Tests
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
                "func b(): String { return greet(\"Latte\") }\n" +
                "func c(): String { return greet(punct = \"?\") }\n");
            CheckNoErrors("无诊断（默认参数正例）", unit);
            TestHarness.Check("全缺省填充", BoundDescribe.Body(BodyOf(bodies, "a")),
                "Body(a, [], [Return(Call(greet, [Str(\"World\",String), Str(\"!\",String)], String))])");
            TestHarness.Check("部分缺省填充", BoundDescribe.Body(BodyOf(bodies, "b")),
                "Body(b, [], [Return(Call(greet, [Str(\"Latte\",String), Str(\"!\",String)], String))])");
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

            // 4. 泛型方法缺显式实参（零推导——一切显式）
            var (unit4, _) = BindUnit(
                "func identity\\<T>(x: T): T { return x }\n" +
                "func main() { var v = identity(1) }\n");
            TestHarness.CheckSemanticError("泛型方法需要显式实参", unit4.Diagnostics,
                "'identity' is a generic method; provide explicit type arguments");

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

            // 9. 泛型定义不可构造保留（无实参的泛型定义名）
            var (unit9, _) = BindUnit(
                "pub open class Box\\<T> { pub init(_ -> item) { } }\n" +
                "func main() { var box = new Box(1) }\n");
            TestHarness.CheckSemanticError("泛型定义不可构造", unit9.Diagnostics,
                "Cannot construct generic type definition 'Box'");
        }
    }
}
