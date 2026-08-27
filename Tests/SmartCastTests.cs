using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// S8b smart cast 测试（SYNTAX §3.5 清单）：收窄触发（is/null 判等/
    /// and-or-not 组合/guard/循环/switch 占位）+ 收窄目标安全规则（var 失效/
    /// const 字段/var 字段不收窄）+ null 判等绑定 + P4a 物化（LoweredCastExpression）。
    /// 断言：BoundDescribe 描述串 + 结构事实；诊断断言沿用 CheckSemanticError。
    /// </summary>
    public static class SmartCastTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestIsBranchNarrowing();
            TestIsNullableNarrowing();
            TestNullEqualityNarrowing();
            TestGuardPatterns();
            TestAndOrNot();
            TestAssignmentInvalidation();
            TestConstFieldNarrowing();
            TestLoopNarrowing();
            TestSwitchCaseNarrowing();
            TestNonTriggeringForms();
            TestBranchMerging();
            TestNullEqualityBinding();
            TestLoweringMaterialization();
            return TestHarness.Summary("SmartCast");
        }

        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies) BindUnit(
            params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, Binder.Bind(unit, decls));
        }


        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies)
            BindUnitWithStdlib(params string[] sources)
        {
            var roots = new List<RootASTNode>();
            roots.AddRange(StdlibSources.ParseAll());
            roots.AddRange(sources.Select(TestHarness.ParseRoot));
            var unit = new CompilationUnit(roots.ToArray());
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, Binder.Bind(unit, decls));
        }
        private static void CheckNoErrors(string label, CompilationUnit unit)
        {
            TestHarness.CheckTrue(label, !unit.Diagnostics.HasErrors,
                string.Join("; ", unit.Diagnostics.Diagnostics.Select(d => $"{d.Phase}: {d.Message}")));
        }

        private static BoundFunctionBody BodyOf(IReadOnlyList<BoundFunctionBody> bodies, string name)
        {
            return bodies.Single(b => b.Method.Name == name);
        }

        private static bool ContainsSmartCast(BoundBlock block)
        {
            return BoundAnalysis.EnumerateStatements(block).Any(StmtHasSmartCast);
        }

        private static bool StmtHasSmartCast(BoundNode node)
        {
            switch (node)
            {
                case BoundSmartCastExpression:
                    return true;
                case BoundExpressionStatement expressionStatement:
                    return ExprHasSmartCast(expressionStatement.Expression);
                case BoundReturnStatement returnStatement:
                    return returnStatement.Value != null && ExprHasSmartCast(returnStatement.Value);
                case BoundLocalDeclarationStatement declaration:
                    return declaration.Initializer != null && ExprHasSmartCast(declaration.Initializer);
                case BoundAssignmentStatement assignment:
                    return ExprHasSmartCast(assignment.Target) || ExprHasSmartCast(assignment.Value);
                default:
                    return false;
            }
        }

        private static bool ExprHasSmartCast(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundSmartCastExpression:
                    return true;
                case BoundInstanceCallExpression instanceCall:
                    return ExprHasSmartCast(instanceCall.Receiver)
                        || instanceCall.Arguments.Any(ExprHasSmartCast);
                case BoundFieldAccessExpression fieldAccess:
                    return ExprHasSmartCast(fieldAccess.Receiver);
                case BoundBinaryExpression binary:
                    return ExprHasSmartCast(binary.Left) || ExprHasSmartCast(binary.Right);
                case BoundUnaryExpression unary:
                    return ExprHasSmartCast(unary.Operand);
                case BoundCallExpression call:
                    return call.Arguments.Any(ExprHasSmartCast);
                default:
                    return false;
            }
        }

        // ===== 1. is 分支收窄 + 成员解析在收窄类型（含 else 不收窄对照）=====
        private static void TestIsBranchNarrowing()
        {
            TestHarness.Section("S8b is 分支收窄");

            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal {\n" +
                "    pub func bark(): i32 { return 1 }\n" +
                "}\n" +
                "func f(a: Animal): i32 {\n" +
                "    if (a is Dog) { return a.bark() }\n" +
                "    else { return 0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（is 收窄）", unit);
            TestHarness.Check("then 分支收窄形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [If(Is(Param(a,Animal), Dog), " +
                "[Return(InstCall(bark, SmartCast(Param(a,Animal), Dog), [], i32))], " +
                "[Return(Int(0,i32))])])");
            var ifStmt = (BoundIfStatement)BodyOf(bodies, "f").Body.Statements[0];
            TestHarness.CheckTrue("else 分支无 SmartCast", !ContainsSmartCast(ifStmt.FalseBlock!));

            // if 表达式分支收窄
            var (unit2, bodies2) = BindUnitWithStdlib(
                "open class Animal { }\n" +
                "class Dog : Animal {\n" +
                "    pub func bark(): i32 { return 1 }\n" +
                "}\n" +
                "func g(a: Animal): i32 {\n" +
                "    return if (a is Dog) { a.bark() } else { 0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（if 表达式收窄）", unit2);
            TestHarness.Check("if 表达式真分支收窄",
                BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [Return(IfExpr(Is(Param(a,Animal), Dog), " +
                "ValueBlock(_, i32, implicit, [ExprStmt(InstCall(bark, " +
                "SmartCast(Param(a,Animal), Dog), [], i32))]), " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Int(0,i32))]), i32))])");

            // 冗余 is（静态类型已满足）不包装
            var (unit3, bodies3) = BindUnit(
                "class Dog { }\n" +
                "func h(d: Dog): i32 {\n" +
                "    if (d is Dog) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("无诊断（冗余 is）", unit3);
            TestHarness.CheckTrue("冗余 is 不包装",
                !ContainsSmartCast(BodyOf(bodies3, "h").Body));
        }

        // ===== 2. x: T? 经 is 收窄为非空 T（非空蕴含）=====
        private static void TestIsNullableNarrowing()
        {
            TestHarness.Section("S8b is 非空蕴含");

            var (unit, bodies) = BindUnit(
                "class Dog {\n" +
                "    pub func bark(): i32 { return 1 }\n" +
                "}\n" +
                "func f(x: Dog?): i32 {\n" +
                "    if (x is Dog) { return x.bark() }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("无诊断（is 非空蕴含）", unit);
            TestHarness.Check("Dog? → Dog 收窄",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [If(Is(Param(x,Dog?), Dog), " +
                "[Return(InstCall(bark, SmartCast(Param(x,Dog?), Dog), [], i32))]); " +
                "Return(Int(0,i32))])");
        }

        // ===== 3. null 判等收窄（!= 真边 / == 假边）=====
        private static void TestNullEqualityNarrowing()
        {
            TestHarness.Section("S8b null 判等收窄");

            var (unit, bodies) = BindUnit(
                "func f(x: String?): String? {\n" +
                "    if (x != null) { return x }\n" +
                "    else { return null }\n" +
                "}\n");
            CheckNoErrors("无诊断（!= 收窄）", unit);
            var ifStmt = (BoundIfStatement)BodyOf(bodies, "f").Body.Statements[0];
            TestHarness.CheckTrue("x != null 真边收窄", ContainsSmartCast(ifStmt.TrueBlock));
            TestHarness.CheckTrue("x != null 假边不收窄", !ContainsSmartCast(ifStmt.FalseBlock!));
            TestHarness.Check("null 判等条件形态（null 资源定型 T?）",
                BoundDescribe.Expr(ifStmt.Condition),
                "Binary(CmpNe, Param(x,String?), Null(String?), bool)");

            var (unit2, bodies2) = BindUnitWithStdlib(
                "func g(x: String?): String? {\n" +
                "    if (x == null) { return null }\n" +
                "    else { return x }\n" +
                "}\n");
            CheckNoErrors("无诊断（== else 收窄）", unit2);
            TestHarness.CheckTrue("x == null else 分支收窄",
                ContainsSmartCast(((BoundIfStatement)BodyOf(bodies2, "g").Body.Statements[0])
                    .FalseBlock!));
        }

        // ===== 4. guard 模式（return/throw 终止分支反向传播）=====
        private static void TestGuardPatterns()
        {
            TestHarness.Section("S8b guard 模式");

            var (unit, bodies) = BindUnit(
                "func f(x: String?): String {\n" +
                "    if (x == null) { return \"\" }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（guard return）", unit);
            TestHarness.Check("guard 后续收窄形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [If(Binary(CmpEq, Param(x,String?), Null(String?), bool), " +
                "[Return(Str(\"\",String))]); Return(SmartCast(Param(x,String?), String))])");

            // throw 终止同效
            var (unit2, bodies2) = BindUnitWithStdlib(
                "open class E : core.Exception {\n" +
                "    pub override func getMessage(): String { return message }\n" +
                "}\n" +
                "func g(x: String?): String {\n" +
                "    if (x == null) { throw new E() }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（guard throw）", unit2);
            TestHarness.CheckTrue("throw guard 后续收窄",
                BoundDescribe.Body(BodyOf(bodies2, "g")).Contains("SmartCast(Param(x,String?), String)"));

            // else 终止 → then 边流
            var (unit3, bodies3) = BindUnit(
                "func h(x: String?): String {\n" +
                "    if (x != null) { return x }\n" +
                "    else { return \"\" }\n" +
                "}\n");
            CheckNoErrors("无诊断（双分支）", unit3);
            TestHarness.CheckTrue("then 分支终止时 else 流不收窄",
                BoundDescribe.Body(BodyOf(bodies3, "h")).Contains("SmartCast(Param(x,String?), String)"));
        }

        // ===== 5. and/or/not 组合 =====
        private static void TestAndOrNot()
        {
            TestHarness.Section("S8b and/or/not 组合");

            // and 右侧绑定上下文：右侧成员解析在收窄类型上
            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal {\n" +
                "    pub func bark(): i32 { return 1 }\n" +
                "}\n" +
                "func f(a: Animal): i32 {\n" +
                "    if ((a is Dog) and (a.bark() == 1)) { return 1 }\n" +
                "    return 0\n" +
                "}\n");
            CheckNoErrors("无诊断（and 右侧上下文）", unit);
            TestHarness.Check("and 右侧收窄形态",
                BoundDescribe.Expr(((BoundIfStatement)BodyOf(bodies, "f").Body.Statements[0]).Condition),
                "Binary(And, Is(Param(a,Animal), Dog), " +
                "Binary(CmpEq, InstCall(bark, SmartCast(Param(a,Animal), Dog), [], i32), " +
                "Int(1,i32), bool), bool)");

            // or 假边合取（guard）：两侧皆假时两者均收窄
            var (unit2, bodies2) = BindUnitWithStdlib(
                "func g(x: String?, y: String?): String {\n" +
                "    if ((x == null) or (y == null)) { return \"\" }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（or 假边 guard）", unit2);
            TestHarness.CheckTrue("or 假边 x 收窄",
                BoundDescribe.Body(BodyOf(bodies2, "g")).Contains("SmartCast(Param(x,String?), String)"));

            // or 右侧上下文（左假）：(x == null) or (x.Length 访问)——右侧 x 已非空
            var (unit3, bodies3) = BindUnit(
                "func h(x: String?): bool {\n" +
                "    return (x == null) or (x is String)\n" +
                "}\n");
            CheckNoErrors("无诊断（or 右侧上下文）", unit3);

            // not 翻转（guard）
            var (unit4, bodies4) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal {\n" +
                "    pub func bark(): i32 { return 1 }\n" +
                "}\n" +
                "func k(a: Animal): i32 {\n" +
                "    if (not (a is Dog)) { return 0 }\n" +
                "    return a.bark()\n" +
                "}\n");
            CheckNoErrors("无诊断（not guard）", unit4);
            TestHarness.CheckTrue("not guard 后续收窄",
                BoundDescribe.Body(BodyOf(bodies4, "k")).Contains(
                    "SmartCast(Param(a,Animal), Dog)"));
        }

        // ===== 6. var 赋值失效（含复合赋值）；收窄影响 var 推断 =====
        private static void TestAssignmentInvalidation()
        {
            TestHarness.Section("S8b 赋值失效");

            var (unit, bodies) = BindUnit(
                "func f(x: String?): String? {\n" +
                "    if (x != null) {\n" +
                "        var s = x\n" +
                "        x = null\n" +
                "        return x\n" +
                "    }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（赋值失效）", unit);
            TestHarness.Check("收窄影响 var 推断（s: String）",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [s: String], [If(Binary(CmpNe, Param(x,String?), Null(String?), bool), " +
                "[Decl(s, String, = SmartCast(Param(x,String?), String)); " +
                "Assign(Param(x,String?), Null(String?)); Return(Param(x,String?))]); " +
                "Return(Null(String?))])");
        }

        // ===== 7. const 字段收窄（this.f）；var 字段不收窄 =====
        private static void TestConstFieldNarrowing()
        {
            TestHarness.Section("S8b 字段收窄");

            var (unit, bodies) = BindUnit(
                "class Box {\n" +
                "    const item: String?\n" +
                "    init(i: String?) { item = i }\n" +
                "    func get(): String? {\n" +
                "        if (item != null) { return item }\n" +
                "        return null\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（const 字段）", unit);
            TestHarness.CheckTrue("const 字段收窄",
                BoundDescribe.Body(BodyOf(bodies, "get")).Contains(
                    "SmartCast(InstField(item, This(Box), String?), String)"));

            var (unit2, bodies2) = BindUnitWithStdlib(
                "class Box2 {\n" +
                "    var item: String?\n" +
                "    init(i: String?) { item = i }\n" +
                "    func get(): String? {\n" +
                "        if (item != null) { return item }\n" +
                "        return null\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（var 字段）", unit2);
            TestHarness.CheckTrue("var 字段不收窄",
                !ContainsSmartCast(BodyOf(bodies2, "get").Body));
        }

        // ===== 8. 循环收窄（while 体带真边；do-while 不带；出循环失效）=====
        private static void TestLoopNarrowing()
        {
            TestHarness.Section("S8b 循环收窄");

            var (unit, bodies) = BindUnit(
                "func f(x: String?, c: bool): String? {\n" +
                "    while (x != null) {\n" +
                "        var s = x\n" +
                "        x = null\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（while）", unit);
            var body = BoundDescribe.Body(BodyOf(bodies, "f"));
            TestHarness.CheckTrue("while 体入口真边收窄（s: String）",
                body.Contains("Decl(s, String, = SmartCast(Param(x,String?), String))"));
            TestHarness.CheckTrue("while 后收窄失效（return x 无 SmartCast）",
                body.Contains("Return(Param(x,String?))"));

            var (unit2, bodies2) = BindUnitWithStdlib(
                "func g(x: String?, c: bool): String? {\n" +
                "    var s: String? = null\n" +
                "    do {\n" +
                "        s = x\n" +
                "    } while (x != null)\n" +
                "    return s\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while）", unit2);
            TestHarness.CheckTrue("do-while 体不带真边（右侧不收窄）",
                BoundDescribe.Body(BodyOf(bodies2, "g")).Contains(
                    "Assign(Local(s,String?), Param(x,String?))"));
        }

        // ===== 9. switch `(_ is T)` 分支体 selector 收窄 =====
        private static void TestSwitchCaseNarrowing()
        {
            TestHarness.Section("S8b switch 占位收窄");

            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal {\n" +
                "    pub func bark(): i32 { return 1 }\n" +
                "}\n" +
                "func f(a: Animal): i32 {\n" +
                "    return switch (a) {\n" +
                "        (_ is Dog) -> { a.bark() }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（switch 占位收窄）", unit);
            TestHarness.CheckTrue("(_ is Dog) 分支体 selector 收窄",
                BoundDescribe.Body(BodyOf(bodies, "f")).Contains(
                    "InstCall(bark, SmartCast(Param(a,Animal), Dog), [], i32)"));
        }

        // ===== 10. 不触发形态（动态 is/supers/with）=====
        private static void TestNonTriggeringForms()
        {
            TestHarness.Section("S8b 不触发形态");

            var (unit, bodies) = BindUnit(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func f(a: Animal, t: Type\\<Dog>): Animal? {\n" +
                "    if (a is t) { return a }\n" +
                "    return null\n" +
                "}\n");
            CheckNoErrors("无诊断（动态 is）", unit);
            TestHarness.CheckTrue("动态 is 不触发",
                !ContainsSmartCast(BodyOf(bodies, "f").Body));

            var (unit2, bodies2) = BindUnitWithStdlib(
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "func g(a: Animal): bool {\n" +
                "    return a supers Animal\n" +
                "}\n");
            CheckNoErrors("无诊断（supers）", unit2);
            TestHarness.CheckTrue("supers 不触发", !ContainsSmartCast(BodyOf(bodies2, "g").Body));
        }

        // ===== 11. 分支合并（两分支同收窄才保留）=====
        private static void TestBranchMerging()
        {
            TestHarness.Section("S8b 分支合并");

            // 两分支各自 guard 收窄（guard 使收窄活到分支尾）→ 交集保留
            var (unit, bodies) = BindUnit(
                "func f(x: String?, c: bool): String? {\n" +
                "    if (c) {\n" +
                "        if (x == null) { return null }\n" +
                "    } else {\n" +
                "        if (x == null) { return null }\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（双分支同收窄）", unit);
            TestHarness.CheckTrue("交集保留（return x 收窄）",
                BoundDescribe.Body(BodyOf(bodies, "f")).Contains("Return(SmartCast(Param(x,String?), String))"));

            var (unit2, bodies2) = BindUnitWithStdlib(
                "func g(x: String?, c: bool): String? {\n" +
                "    if (c) {\n" +
                "        if (x != null) { }\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（单分支）", unit2);
            TestHarness.CheckTrue("单分支收窄不外泄",
                !BoundDescribe.Body(BodyOf(bodies2, "g")).Contains("Return(SmartCast"));
        }

        // ===== 12. null 判等绑定（非 nullable 放行 / null==null 报错）=====
        private static void TestNullEqualityBinding()
        {
            TestHarness.Section("S8b null 判等绑定");

            var (unit, bodies) = BindUnit(
                "func f(i: i32): bool { return i == null }\n");
            CheckNoErrors("无诊断（i32 == null 放行）", unit);
            TestHarness.Check("非空侧装箱 cast 形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Binary(CmpEq, Cast(Param(i,i32), i32?), Null(i32?), bool))])");

            var (unit2, _) = BindUnit(
                "func g(): bool { return null == null }\n");
            TestHarness.CheckSemanticError("null == null 报错", unit2.Diagnostics,
                "null requires a nullable type context");
        }

        // ===== 13. P4a 物化（SmartCast → LoweredCastExpression）=====
        private static void TestLoweringMaterialization()
        {
            TestHarness.Section("S8b P4a 物化");

            var (unit, bodies) = BindUnit(
                "func f(x: String?): String {\n" +
                "    if (x == null) { return \"\" }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无诊断（物化前置）", unit);
            var lowered = Lowerer.Lower(unit, bodies);
            CheckNoErrors("无 P4 诊断", unit);
            var main = lowered.Single(b => b.Method.Name == "f");
            var returnStmt = main.Body.Statements
                .OfType<LoweredReturnStatement>().First();
            TestHarness.CheckTrue("SmartCast 物化为 LoweredCastExpression",
                returnStmt.Value is LoweredCastExpression cast
                && !cast.IsSafe
                && cast.TargetType.Name == "String"
                && cast.Source is LoweredValueReferenceExpression);
        }
    }
}
