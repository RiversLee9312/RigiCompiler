using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// S5 P3 最小闭环测试（M41）：Binder 的 bound 形态与定型类型断言 + 结构性事实。
    /// 覆盖：字面量定型、局部变量声明（var 推断/显式标注/const）、值引用（局部/参数/
    /// 全局字段）、二元与一元 intrinsic 运算（结果类型维度）、赋值与 definite
    /// assignment 最小版、无重载直接调用（具名实参规范序重排）、宿主类型成员调用
    /// （类内裸名静态/实例拦截/多段路径类容器/遮蔽优先级）、new 构造、
    /// return 与「所有路径显式返回」、块作用域与遮蔽、诊断互不阻断。
    /// S7c-1 增补：while/do-while 绑定形态、循环条件 bool 检查、for 拦截
    /// （S7c-2）、break/continue 标签栈解析（无标签栈顶/named 穿透/循环外与
    /// 未定义标签诊断）、值块内穿透、definite assignment 循环规则、
    /// GuaranteesReturn 循环保守。
    /// 诊断断言沿用消息子串惯例（CheckSemanticError）；符号比较一律引用相等。
    /// </summary>
    public static class BinderTests
    {
        public static int RunAll()
        {
            TestHarness.Reset();
            TestLiterals();
            TestLocalDeclarations();
            TestValueReferences();
            TestBinaryOperators();
            TestUnaryOperators();
            TestAssignments();
            TestCalls();
            TestHostTypeMembers();
            TestNew();
            TestReturn();
            TestScopes();
            TestIfStatements();
            TestIfExpressions();
            TestBranchDefiniteAssignment();
            TestCompoundAssignments();
            TestLoops();
            TestLoopControl();
            TestInstanceMembers();
            TestForLoops();
            TestSwitch();
            TestThrow();
            TestCast();
            TestTry();
            TestSeq();
            TestStringInterpolation();
            TestSafeAccess();
            TestNullFallback();
            TestDestructuring();
            TestDiagnosticsAccumulation();
            return TestHarness.Summary("Binder");
        }

        // 多源文件经全管线（Parser → P1 → P2 → P3）后取编译单元与 bound 函数体列表
        private static (CompilationUnit Unit, IReadOnlyList<BoundFunctionBody> Bodies) BindUnit(
            params string[] sources)
        {
            var roots = sources.Select(TestHarness.ParseRoot).ToArray();
            var unit = new CompilationUnit(roots);
            var decls = DeclarationCollector.Collect(unit);
            DeclarationResolver.Resolve(unit, decls);
            return (unit, Binder.Bind(unit, decls));
        }

        // 带 stdlib 的全管线驱动（S7c-2：for 协议与 ext operator 用例需要
        // core.collections 与 .bootstrap 的 EnumerateInRange 注册）
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

        // ===== 字面量定型（含 null 的可空上下文）=====
        private static void TestLiterals()
        {
            TestHarness.Section("P3 Literals");

            var (unit, bodies) = BindUnit(
                "func i(): i32 { return 42 }\n" +
                "func l(): i64 { return 100L }\n" +
                "func s(): i16 { return 1S }\n" +
                "func b(): i8 { return 1B }\n" +
                "func u(): u32 { return 1U }\n" +
                "func d(): double { return 3.14 }\n" +
                "func f(): float { return 0.1f }\n" +
                "func t(): String { return \"hi\" }\n" +
                "func c(): char { return 'A' }\n" +
                "func o(): bool { return true }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("i32 默认", BoundDescribe.Body(BodyOf(bodies, "i")),
                "Body(i, [], [Return(Int(42,i32))])");
            TestHarness.Check("L 后缀", BoundDescribe.Body(BodyOf(bodies, "l")),
                "Body(l, [], [Return(Int(100,i64))])");
            TestHarness.Check("S 后缀", BoundDescribe.Body(BodyOf(bodies, "s")),
                "Body(s, [], [Return(Int(1,i16))])");
            TestHarness.Check("B 后缀", BoundDescribe.Body(BodyOf(bodies, "b")),
                "Body(b, [], [Return(Int(1,i8))])");
            TestHarness.Check("U 后缀", BoundDescribe.Body(BodyOf(bodies, "u")),
                "Body(u, [], [Return(Int(1,u32))])");
            TestHarness.Check("double 默认", BoundDescribe.Body(BodyOf(bodies, "d")),
                "Body(d, [], [Return(Float(3.14,double))])");
            TestHarness.Check("f 后缀", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Float(0.1,float))])");
            TestHarness.Check("String", BoundDescribe.Body(BodyOf(bodies, "t")),
                "Body(t, [], [Return(Str(\"hi\",String))])");
            TestHarness.Check("char", BoundDescribe.Body(BodyOf(bodies, "c")),
                "Body(c, [], [Return(Char('A',char))])");
            TestHarness.Check("bool", BoundDescribe.Body(BodyOf(bodies, "o")),
                "Body(o, [], [Return(Bool(True,bool))])");

            var (unit2, bodies2) = BindUnit("func n(): String? { return null }\n");
            CheckNoErrors("null 可空上下文", unit2);
            TestHarness.Check("null 定型为标注可空类型", BoundDescribe.Body(BodyOf(bodies2, "n")),
                "Body(n, [], [Return(Null(String?))])");

            var (unit3, _) = BindUnit("func f() { var x = null }\n");
            TestHarness.CheckSemanticError("null 无上下文诊断", unit3.Diagnostics,
                "null requires a nullable type context");

            // S7f 起插值落地（SYNTAX §3.8）：段表达式参与正常绑定，
            // 未定义名照常诊断
            var (unit4, _) = BindUnit("func f(): String { return \"${x}\" }\n");
            TestHarness.CheckSemanticError("插值段未定义名照常诊断", unit4.Diagnostics,
                "Undefined name: 'x'");
        }

        // ===== 局部变量声明（var 推断 / 显式标注 / const / 重复）=====
        private static void TestLocalDeclarations()
        {
            TestHarness.Section("P3 Local Declarations");

            var (unit, bodies) = BindUnit(
                "func f() {\n" +
                "    var x = 42\n" +
                "    var y: i32 = 1\n" +
                "    var z: i32\n" +
                "    const c = \"s\"\n" +
                "}\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("var 推断 / 标注 / 无初始化 / const",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32, y: i32, z: i32, c: String], " +
                "[Decl(x, i32, = Int(42,i32)); Decl(y, i32, = Int(1,i32)); Decl(z, i32); " +
                "Decl(c, String, = Str(\"s\",String))])");

            var (unit2, _) = BindUnit("func f() { var x: String = 42 }\n");
            TestHarness.CheckSemanticError("标注与初始化类型不匹配", unit2.Diagnostics,
                "Cannot assign 'i32' to 'String'");

            var (unit3, _) = BindUnit("func f() { var x }\n");
            TestHarness.CheckSemanticError("无标注无初始化", unit3.Diagnostics,
                "Variable 'x' requires a type annotation or an initializer");

            var (unit4, _) = BindUnit("func f() { const c: i32 }\n");
            TestHarness.CheckSemanticError("const 无初始化", unit4.Diagnostics,
                "Const 'c' must have an initializer");

            var (unit5, _) = BindUnit("func f() { var x = 1\nvar x = 2 }\n");
            TestHarness.CheckSemanticError("同块重复声明", unit5.Diagnostics,
                "Duplicate local variable 'x'");

            var (unit6, bodies6) = BindUnit(
                "func f(): String? { var s: String? = null\nreturn s }\n");
            CheckNoErrors("可空声明", unit6);
            TestHarness.Check("可空标注 + null 初始化", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [s: String?], [Decl(s, String?, = Null(String?)); Return(Local(s,String?))])");

            // 可空提升：T → Nullable\<T\>（BIL §6.5 source-level 判定）
            var (unit7, _) = BindUnit("func f() { var s: String? = \"a\" }\n");
            CheckNoErrors("可空提升赋值", unit7);

            // 字面量默认类型与标注严格不同时拒绝（i32 → i64 无隐式转换，BIL §6.4）
            var (unit8, _) = BindUnit("func f() { var x: i64 = 42 }\n");
            TestHarness.CheckSemanticError("i32 字面量不隐式转 i64", unit8.Diagnostics,
                "Cannot assign 'i32' to 'i64'");
        }

        // ===== 值引用（局部 / 参数 / 全局字段 / 未赋值 / 未定义）=====
        private static void TestValueReferences()
        {
            TestHarness.Section("P3 Value References");

            var (unit, bodies) = BindUnit(
                "var g: i32 = 0\n" +
                "func f(a: i32): i32 {\n" +
                "    var x = a\n" +
                "    return x\n" +
                "}\n" +
                "func h(): i32 { return g }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("参数与局部引用", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Param(a,i32)); Return(Local(x,i32))])");
            TestHarness.Check("全局字段引用", BoundDescribe.Body(BodyOf(bodies, "h")),
                "Body(h, [], [Return(Field(g,i32))])");

            var (unit2, _) = BindUnit("func f(): i32 { var x: i32\nreturn x }\n");
            TestHarness.CheckSemanticError("未赋值使用", unit2.Diagnostics,
                "Use of unassigned local variable 'x'");

            var (unit3, _) = BindUnit("func f(): i32 { return y }\n");
            TestHarness.CheckSemanticError("未定义名字", unit3.Diagnostics,
                "Undefined name: 'y'");

            // 结构性事实：参数引用符号与 MethodSymbol.Parameters 同一引用
            var (_, bodies4) = BindUnit("func f(a: i32): i32 { return a }\n");
            var body4 = BodyOf(bodies4, "f");
            var paramRef = ((BoundReturnStatement)body4.Body.Statements[0]).Value
                as BoundValueReferenceExpression;
            TestHarness.CheckTrue("参数符号引用相等",
                paramRef != null && ReferenceEquals(paramRef.Symbol, body4.Method.Parameters[0]));
            TestHarness.CheckTrue("Syntax 回指（Return → ReturnStatementASTNode）",
                body4.Body.Statements[0].Syntax is ReturnStatementASTNode);
        }

        // ===== 二元 intrinsic 运算（BIL §11 结果类型维度）=====
        private static void TestBinaryOperators()
        {
            TestHarness.Section("P3 Binary Operators");

            var (unit, bodies) = BindUnit(
                "func f(a: i32, b: i32): i32 { return ((a + b) * (a - b)) }\n" +
                "func g(a: i32, b: i32): bool { return (a < b) }\n" +
                "func e(a: bool, b: bool): bool { return (a and b) }\n" +
                "func o(a: bool, b: bool): bool { return (a or b) }\n" +
                "func w(a: i32, b: i32): i32 { return ((a & b) | (a ^ b)) }\n" +
                "func s(a: i32, b: i32): i32 { return (a << b) }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("算术同型结果", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Binary(Mul, Binary(Add, Param(a,i32), Param(b,i32), i32), " +
                "Binary(Sub, Param(a,i32), Param(b,i32), i32), i32))])");
            TestHarness.Check("比较结果 bool", BoundDescribe.Body(BodyOf(bodies, "g")),
                "Body(g, [], [Return(Binary(CmpLt, Param(a,i32), Param(b,i32), bool))])");
            TestHarness.Check("and 定型", BoundDescribe.Body(BodyOf(bodies, "e")),
                "Body(e, [], [Return(Binary(And, Param(a,bool), Param(b,bool), bool))])");
            TestHarness.Check("or 定型", BoundDescribe.Body(BodyOf(bodies, "o")),
                "Body(o, [], [Return(Binary(Or, Param(a,bool), Param(b,bool), bool))])");
            TestHarness.Check("位运算", BoundDescribe.Body(BodyOf(bodies, "w")),
                "Body(w, [], [Return(Binary(BinOr, Binary(BinAnd, Param(a,i32), Param(b,i32), i32), " +
                "Binary(BinXor, Param(a,i32), Param(b,i32), i32), i32))])");
            TestHarness.Check("移位", BoundDescribe.Body(BodyOf(bodies, "s")),
                "Body(s, [], [Return(Binary(ShiftLeft, Param(a,i32), Param(b,i32), i32))])");

            var (unit2, _) = BindUnit("func f(): i64 { return (1 + 2L) }\n");
            TestHarness.CheckSemanticError("操作数类型必须相同", unit2.Diagnostics,
                "requires operands of the same type");

            var (unit3, _) = BindUnit(
                "class C { }\nfunc f(a: C, b: C): C { return (a + b) }\n");
            TestHarness.CheckSemanticError("用户类型无 intrinsic（重载归 S8）", unit3.Diagnostics,
                "Operator '+' is not defined for type 'C'");

            var (unit4, _) = BindUnit("func f(a: i32, b: i32): bool { return (a and b) }\n");
            TestHarness.CheckSemanticError("i32 无 and 运算", unit4.Diagnostics,
                "Operator 'and' is not defined for type 'i32'");

            // == 对内建 String（仅有 CmpEq/CmpNe 键）
            var (unit5, bodies5) = BindUnit(
                "func f(a: String, b: String): bool { return (a == b) }\n");
            CheckNoErrors("String 相等", unit5);
            TestHarness.Check("String ==", BoundDescribe.Body(BodyOf(bodies5, "f")),
                "Body(f, [], [Return(Binary(CmpEq, Param(a,String), Param(b,String), bool))])");
        }

        // ===== 一元 intrinsic 运算 =====
        private static void TestUnaryOperators()
        {
            TestHarness.Section("P3 Unary Operators");

            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 { return -a }\n" +
                "func g(a: bool): bool { return not a }\n" +
                "func h(a: i32): i32 { return !a }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("opposite", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Unary(Opposite, Param(a,i32), i32))])");
            TestHarness.Check("not", BoundDescribe.Body(BodyOf(bodies, "g")),
                "Body(g, [], [Return(Unary(Not, Param(a,bool), bool))])");
            TestHarness.Check("bin.not", BoundDescribe.Body(BodyOf(bodies, "h")),
                "Body(h, [], [Return(Unary(BinNot, Param(a,i32), i32))])");

            var (unit2, _) = BindUnit("func f(a: u32): u32 { return -a }\n");
            TestHarness.CheckSemanticError("无符号无 Opposite", unit2.Diagnostics,
                "Operator '-' is not defined for type 'u32'");
        }

        // ===== 赋值与 definite assignment =====
        private static void TestAssignments()
        {
            TestHarness.Section("P3 Assignments");

            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    x = a\n" +
                "    a = x\n" +
                "    return a\n" +
                "}\n");
            CheckNoErrors("无诊断（赋值即 definite assignment）", unit);
            TestHarness.Check("局部与参数赋值", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32); Assign(Local(x,i32), Param(a,i32)); " +
                "Assign(Param(a,i32), Local(x,i32)); Return(Param(a,i32))])");

            var (unit2, _) = BindUnit("func f() { const c = 1\nc = 2 }\n");
            TestHarness.CheckSemanticError("const 赋值拒绝", unit2.Diagnostics,
                "Cannot assign to const 'c'");

            var (unit3, _) = BindUnit("func f() { var x = 1\nx = \"s\" }\n");
            TestHarness.CheckSemanticError("赋值类型不匹配", unit3.Diagnostics,
                "Cannot assign 'String' to 'i32'");

            var (unit4, _) = BindUnit("func g(): i32 { return 1 }\nfunc f() { g() = 2 }\n");
            TestHarness.CheckSemanticError("非变量赋值目标", unit4.Diagnostics,
                "Assignment target must be a variable");
        }

        // ===== 直接调用（无重载；具名实参规范序；void 语句）=====
        private static void TestCalls()
        {
            TestHarness.Section("P3 Calls");

            var (unit, bodies) = BindUnit(
                "func add(a: i32, b: i32): i32 { return a }\n" +
                "func f(): i32 { return add(1, 2) }\n" +
                "func n(): i32 { return add(b = 2, a = 1) }\n" +
                "func v() { }\n" +
                "func s() { v() }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("位置实参调用", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Call(add, [Int(1,i32), Int(2,i32)], i32))])");
            TestHarness.Check("具名实参重排为规范参数序", BoundDescribe.Body(BodyOf(bodies, "n")),
                "Body(n, [], [Return(Call(add, [Int(1,i32), Int(2,i32)], i32))])");
            TestHarness.Check("void 调用语句", BoundDescribe.Body(BodyOf(bodies, "s")),
                "Body(s, [], [CallStmt(v, [])])");

            // 结构性事实：BoundCallExpression.Method 与符号图方法同一引用
            var callExpr = ((BoundReturnStatement)BodyOf(bodies, "f").Body.Statements[0]).Value
                as BoundCallExpression;
            var addSymbol = unit.Symbols.GlobalNamespace.Methods.Single(m => m.Name == "add");
            TestHarness.CheckTrue("调用方法符号引用相等",
                callExpr != null && ReferenceEquals(callExpr.Method, addSymbol));

            var (unit2, _) = BindUnit("func v() { }\nfunc f() { var x = v() }\n");
            TestHarness.CheckSemanticError("void 作值诊断", unit2.Diagnostics,
                "has no result (void) and cannot be used as a value");

            var (unit3, _) = BindUnit("func f() { nosuch() }\n");
            TestHarness.CheckSemanticError("未定义函数", unit3.Diagnostics,
                "Undefined function: 'nosuch'");

            var (unit4, _) = BindUnit(
                "func add(a: i32, b: i32): i32 { return a }\nfunc f(): i32 { return add(1) }\n");
            TestHarness.CheckSemanticError("实参个数不符", unit4.Diagnostics,
                "expects 2 argument(s), got 1");

            var (unit5, _) = BindUnit(
                "func add(a: i32, b: i32): i32 { return a }\n" +
                "func f(): i32 { return add(\"s\", 2) }\n");
            TestHarness.CheckSemanticError("实参类型不匹配", unit5.Diagnostics,
                "Cannot pass 'String' as 'i32'");

            var (unit6, _) = BindUnit(
                "func add(a: i32, b: i32): i32 { return a }\n" +
                "func f(): i32 { return add(a = 1, a = 2) }\n");
            TestHarness.CheckSemanticError("重复实参", unit6.Diagnostics,
                "Duplicate argument for parameter 'a'");

            var (unit7, _) = BindUnit(
                "func add(a: i32, b: i32): i32 { return a }\n" +
                "func f(): i32 { return add(a = 1, c = 2) }\n");
            TestHarness.CheckSemanticError("未知形参名", unit7.Diagnostics,
                "has no parameter named 'c'");
            TestHarness.CheckSemanticError("缺失形参", unit7.Diagnostics,
                "Missing argument for parameter 'b'");

            // 命名空间路径调用（跨文件前向引用）
            var (unit8, bodies8) = BindUnit(
                "namespace a.b\nfunc g(): i32 { return 1 }\n",
                "func f(): i32 { return a.b.g() }\n");
            CheckNoErrors("命名空间路径调用", unit8);
            TestHarness.Check("a.b.g()", BoundDescribe.Body(BodyOf(bodies8, "f")),
                "Body(f, [], [Return(Call(g, [], i32))])");

            // void 实例调用语句（S7c-2：首段为值的多段调用形态 → 实例链上色）
            var (unit9, bodies9) = BindUnit(
                "class C { pub func m() { } }\nfunc f(c: C) { c.m() }\n");
            CheckNoErrors("void 实例调用语句", unit9);
            TestHarness.Check("InstCallStmt 形态", BoundDescribe.Body(BodyOf(bodies9, "f")),
                "Body(f, [], [InstCallStmt(m, Param(c,C), [])])");
        }

        // ===== 宿主类型成员调用（FindMethods 查找序：宿主沿 BaseType 链 → 命名空间链 → import）=====
        private static void TestHostTypeMembers()
        {
            TestHarness.Section("P3 Host Type Members");

            // 类内裸名调用同类静态方法（stdlib println → print 场景）
            var (unit, bodies) = BindUnit(
                "class A {\n" +
                "    static func helper(x: i32): i32 { return x }\n" +
                "    static func run(): i32 { return helper(42) }\n" +
                "}\n");
            CheckNoErrors("类内裸名静态调用", unit);
            TestHarness.Check("helper(42) 绑定到 A.helper",
                BoundDescribe.Body(BodyOf(bodies, "run")),
                "Body(run, [], [Return(Call(helper, [Int(42,i32)], i32))])");
            // 结构性事实：选中符号是 A 的静态方法（引用相等）
            var helperCall = ((BoundReturnStatement)BodyOf(bodies, "run").Body.Statements[0]).Value
                as BoundCallExpression;
            var typeA = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "A");
            TestHarness.CheckTrue("命中 A 的静态方法（引用相等）",
                helperCall != null && helperCall.Method.IsStatic
                && ReferenceEquals(helperCall.Method,
                    typeA.Methods.Single(m => m.Name == "helper")));

            // 类内裸名调用实例方法：命中宿主成员后被 BindCallee 静态性检查拦截
            var (unit2, _) = BindUnit(
                "class B {\n" +
                "    func inst(): i32 { return 1 }\n" +
                "    static func run(): i32 { return inst() }\n" +
                "}\n");
            TestHarness.CheckSemanticError("实例方法裸名调用被拦", unit2.Diagnostics,
                "instance method 'inst' requires a receiver");

            // 多段路径类容器静态调用（容器解析 + 末段成员，代码路径已有补用例）
            var (unit3, bodies3) = BindUnit(
                "namespace a.b\n" +
                "class C { pub static func f(v: i32): i32 { return v } }\n",
                "func g(): i32 { return a.b.C.f(7) }\n");
            CheckNoErrors("多段路径类容器调用", unit3);
            TestHarness.Check("a.b.C.f(7)", BoundDescribe.Body(BodyOf(bodies3, "g")),
                "Body(g, [], [Return(Call(f, [Int(7,i32)], i32))])");

            // 遮蔽优先级：宿主成员先于命名空间全局函数进入候选
            // （选择仍走实参个数唯一匹配：全局 dup 1 参、宿主 dup 0 参，dup() 选中宿主）
            var (unit4, bodies4) = BindUnit(
                "func dup(x: i32): i32 { return x }\n" +
                "class D {\n" +
                "    static func dup(): i32 { return 2 }\n" +
                "    static func run(): i32 { return dup() }\n" +
                "}\n");
            CheckNoErrors("宿主成员与全局函数同名", unit4);
            var dupCall = ((BoundReturnStatement)BodyOf(bodies4, "run").Body.Statements[0]).Value
                as BoundCallExpression;
            var typeD = unit4.Symbols.GlobalNamespace.Types.Single(t => t.Name == "D");
            TestHarness.CheckTrue("选中宿主类型的方法（引用相等）",
                dupCall != null && ReferenceEquals(dupCall.Method,
                    typeD.Methods.Single(m => m.Name == "dup")));
        }

        // ===== new 构造 =====
        private static void TestNew()
        {
            TestHarness.Section("P3 New");

            var (unit, bodies) = BindUnit(
                "class Point {\n" +
                "    pub var x: i32\n" +
                "    pub init(_ -> x) { }\n" +
                "}\n" +
                "class Empty { }\n" +
                "func f(): Point { return new Point(42) }\n" +
                "func e(): Empty { return new Empty() }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("init 匹配", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(New(Point, init, [Int(42,i32)]))])");
            TestHarness.Check("无 init 零参构造", BoundDescribe.Body(BodyOf(bodies, "e")),
                "Body(e, [], [Return(New(Empty, []))])");

            var (unit2, _) = BindUnit("class C { }\nfunc f(): C { return new C(1) }\n");
            TestHarness.CheckSemanticError("无 init 有参", unit2.Diagnostics,
                "Type 'C' has no constructor");

            var (unit3, _) = BindUnit("interface I { }\nfunc f(): I { return new I() }\n");
            TestHarness.CheckSemanticError("interface 不可构造", unit3.Diagnostics,
                "Cannot construct interface 'I'");

            var (unit4, _) = BindUnit(
                "class Point {\n    pub var x: i32\n    pub init(_ -> x) { }\n}\n" +
                "func f(): Point { return new Point(\"s\") }\n");
            TestHarness.CheckSemanticError("init 实参类型不匹配", unit4.Diagnostics,
                "Cannot pass 'String' as 'i32'");

            // 结构性事实：Init 符号引用相等
            var newExpr = ((BoundReturnStatement)BodyOf(bodies, "f").Body.Statements[0]).Value
                as BoundNewExpression;
            var pointType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Point");
            TestHarness.CheckTrue("init 符号引用相等",
                newExpr?.Init != null &&
                ReferenceEquals(newExpr.Init, pointType.Methods.Single(m => m.Kind == MethodKind.Init)));
        }

        // ===== return 与「所有路径显式返回」=====
        private static void TestReturn()
        {
            TestHarness.Section("P3 Return");

            var (unit, bodies) = BindUnit("func v() { return }\n");
            CheckNoErrors("无诊断", unit);
            TestHarness.Check("void 裸 return", BoundDescribe.Body(BodyOf(bodies, "v")),
                "Body(v, [], [Return])");

            var (unit2, _) = BindUnit("func f() { return 1 }\n");
            TestHarness.CheckSemanticError("void 带值返回", unit2.Diagnostics,
                "Void function 'f' cannot return a value");

            var (unit3, _) = BindUnit("func f(): i32 { return }\n");
            TestHarness.CheckSemanticError("非 void 裸 return", unit3.Diagnostics,
                "Function 'f' must return a value");

            var (unit4, _) = BindUnit("func f(): i32 { var x = 1 }\n");
            TestHarness.CheckSemanticError("缺失 return（所有路径检查）", unit4.Diagnostics,
                "must return a value on all code paths");

            var (unit5, _) = BindUnit("func f(): i32 { return \"s\" }\n");
            TestHarness.CheckSemanticError("返回类型不匹配", unit5.Diagnostics,
                "Cannot return 'String' from function returning 'i32'");
        }

        // ===== 作用域（前端无裸块语法，嵌套作用域随 S7 控制流接入；
        // 此处断言单层规则：局部遮蔽参数、函数间局部互不影响）=====
        private static void TestScopes()
        {
            TestHarness.Section("P3 Scopes");

            // 局部变量遮蔽同名参数（声明后引用命中局部）
            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 { var a = 2\nreturn a }\n");
            CheckNoErrors("局部遮蔽参数", unit);
            TestHarness.Check("遮蔽后引用命中局部", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [a: i32], [Decl(a, i32, = Int(2,i32)); Return(Local(a,i32))])");

            // 同名局部变量在不同函数间互不影响
            var (unit2, bodies2) = BindUnit(
                "func f(): i32 { var x = 1\nreturn x }\n" +
                "func g(): i32 { var x = 2\nreturn x }\n");
            CheckNoErrors("函数间同名局部", unit2);
            TestHarness.CheckTrue("各函数 Locals 独立",
                BodyOf(bodies2, "f").Locals.Count == 1 && BodyOf(bodies2, "g").Locals.Count == 1
                && !ReferenceEquals(BodyOf(bodies2, "f").Locals[0], BodyOf(bodies2, "g").Locals[0]));
        }

        // ===== if 语句（S7b，含 else if 链包装与 GuaranteesReturn 双分支升级）=====
        private static void TestIfStatements()
        {
            TestHarness.Section("P3 If Statements");

            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x = 0\n" +
                "    if (a > 0) {\n" +
                "        x = 1\n" +
                "    } else {\n" +
                "        x = 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n" +
                "func h(a: i32) {\n" +
                "    if (a > 0) { a = 0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（if 语句）", unit);
            TestHarness.Check("双分支 if 语句", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "If(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Local(x,i32), Int(1,i32))], [Assign(Local(x,i32), Int(2,i32))]); " +
                "Return(Local(x,i32))])");
            TestHarness.Check("无 else if 语句", BoundDescribe.Body(BodyOf(bodies, "h")),
                "Body(h, [], [If(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Param(a,i32), Int(0,i32))])])");

            // else if 链包成单语句 BoundBlock；双分支 return → GuaranteesReturn 通过
            var (unit2, bodies2) = BindUnit(
                "func g(a: i32): i32 {\n" +
                "    if (a > 0) { return 1 }\n" +
                "    else if (a == 0) { return 0 }\n" +
                "    else { return -1 }\n" +
                "}\n");
            CheckNoErrors("else if 链 + 双分支 return（GuaranteesReturn 通过）", unit2);
            TestHarness.Check("else if 链包装形态", BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [If(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Return(Int(1,i32))], " +
                "[If(Binary(CmpEq, Param(a,i32), Int(0,i32), bool), " +
                "[Return(Int(0,i32))], [Return(Unary(Opposite, Int(1,i32), i32))])])])");

            // 结构性事实：else if 包装块是单语句 BoundBlock，且语句即 BoundIfStatement
            var outerIf = (BoundIfStatement)BodyOf(bodies2, "g").Body.Statements[0];
            TestHarness.CheckTrue("else if 链包成单语句 BoundBlock",
                outerIf.FalseBlock != null && outerIf.FalseBlock.Statements.Count == 1
                && outerIf.FalseBlock.Statements[0] is BoundIfStatement);

            // 单分支 return → GuaranteesReturn 不通过
            var (unit3, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    if (a > 0) { return 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("单分支 return 不保证返回", unit3.Diagnostics,
                "must return a value on all code paths");

            var (unit4, _) = BindUnit("func f(x: i32) { if (x) { } }\n");
            TestHarness.CheckSemanticError("条件非 bool", unit4.Diagnostics,
                "if condition must be bool (got 'i32')");
        }

        // ===== if 表达式（S7b：值块隐式取值 / 显式 return@ / named 标签穿透）=====
        private static void TestIfExpressions()
        {
            TestHarness.Section("P3 If Expressions");

            // 隐式取值（单表达式分支）
            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    return if (a > 0) { a } else { -a }\n" +
                "}\n");
            CheckNoErrors("无诊断（隐式取值）", unit);
            TestHarness.Check("隐式取值 if 表达式", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(IfExpr(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Param(a,i32))]), " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Unary(Opposite, Param(a,i32), i32))]), " +
                "i32))])");

            // 显式 return@_（验收形态）
            var (unit2, bodies2) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if ((x > 0)) { return@_ 1 } else { return@_ 2 }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（显式 return@_）", unit2);
            TestHarness.Check("显式 return@_ 值块", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [r: i32], [Decl(r, i32, = " +
                "IfExpr(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "ValueBlock(_, i32, [ReturnValue(_, Int(1,i32))]), " +
                "ValueBlock(_, i32, [ReturnValue(_, Int(2,i32))]), i32)); " +
                "Return(Local(r,i32))])");

            // named 标签：两分支值块同源（if 表达式的标签）
            var (unit3, bodies3) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) named pos { return@pos 1 } else { return@pos 0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（named 标签）", unit3);
            TestHarness.Check("named 标签值块", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(IfExpr(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "ValueBlock(pos, i32, [ReturnValue(pos, Int(1,i32))]), " +
                "ValueBlock(pos, i32, [ReturnValue(pos, Int(0,i32))]), i32))])");
            // 结构性事实：return@ 经引用命中所属值块
            var namedIf = (BoundIfExpression)((BoundReturnStatement)
                BodyOf(bodies3, "f").Body.Statements[0]).Value!;
            TestHarness.CheckTrue("return@pos 引用命中真分支值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    namedIf.TrueBranch.Block.Statements[0]).Target, namedIf.TrueBranch));
            TestHarness.CheckTrue("return@pos 引用命中假分支值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    namedIf.FalseBranch.Block.Statements[0]).Target, namedIf.FalseBranch));

            // 穿透外层标签：内层 if 表达式真分支 return@outer（纯穿透，不参与
            // 类型统一），外层 return@outer 取内层 if 表达式的值
            var (unit4, bodies4) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) named outer {\n" +
                "        return@outer if (x > 1) { return@outer 2 } else { return@_ 1 }\n" +
                "    } else {\n" +
                "        return@outer 0\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（穿透外层标签）", unit4);
            TestHarness.Check("穿透外层标签形态", BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [], [Return(IfExpr(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "ValueBlock(outer, i32, [ReturnValue(outer, " +
                "IfExpr(Binary(CmpGt, Param(x,i32), Int(1,i32), bool), " +
                "ValueBlock(_, -, [ReturnValue(outer, Int(2,i32))]), " +
                "ValueBlock(_, i32, [ReturnValue(_, Int(1,i32))]), i32))]), " +
                "ValueBlock(outer, i32, [ReturnValue(outer, Int(0,i32))]), i32))])");
            // 结构性事实：穿透 return@ 命中外层值块（引用相等）
            var outerIfExpr = (BoundIfExpression)((BoundReturnStatement)
                BodyOf(bodies4, "f").Body.Statements[0]).Value!;
            var innerIfExpr = (BoundIfExpression)((BoundReturnValueStatement)
                outerIfExpr.TrueBranch.Block.Statements[0]).Value;
            TestHarness.CheckTrue("穿透 return@outer 引用命中外层值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    innerIfExpr.TrueBranch.Block.Statements[0]).Target, outerIfExpr.TrueBranch));
            TestHarness.CheckTrue("内层 return@_ 引用命中内层值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    innerIfExpr.FalseBranch.Block.Statements[0]).Target, innerIfExpr.FalseBranch));

            // 未定义标签
            var (unit5, _) = BindUnit(
                "func f(): i32 {\n" +
                "    var r = if (1 == 1) { return@nosuch 1 } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("未定义值块标签", unit5.Diagnostics,
                "Undefined value block label: 'nosuch'");

            // 多语句分支块尾缺 return@
            var (unit6, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 1\ny = 2 } else { 0 }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("块尾缺 return@", unit6.Diagnostics,
                "must explicitly return@ a value");

            // 末语句 if 仅单分支 return@ → 路径不全覆盖
            var (unit7, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 0\nif (x > 1) { return@_ 1 } else { y = 2 } } " +
                "else { 0 }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("分支内路径未全显式 return@", unit7.Diagnostics,
                "must explicitly return@ a value");

            // 分支类型不一致（两分支各产不同类型）
            var (unit8, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { 1 } else { \"s\" }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("分支类型不一致", unit8.Diagnostics,
                "if expression branches produce different types ('i32' and 'String')");

            // 同一块内多个 return@ 类型不一致
            var (unit9, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 0\nif (x > 1) { return@_ \"a\" } " +
                "else { return@_ 1 } } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("同块 return@ 类型不一致", unit9.Diagnostics,
                "if expression branch produces different types ('String' and 'i32')");

            // 两分支纯穿透 → if 表达式无产值
            var (unit10, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) named o {\n" +
                "        return@o if (x > 1) { return@o 1 } else { return@o 2 }\n" +
                "    } else { 0 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("两分支纯穿透无产值", unit10.Diagnostics,
                "if expression must produce a value");

            var (unit11, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x) { 1 } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("if 表达式条件非 bool", unit11.Diagnostics,
                "if condition must be bool (got 'i32')");

            // void 调用单语句分支无值可取
            var (unit12, _) = BindUnit(
                "func v() { }\n" +
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { v() } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            TestHarness.CheckSemanticError("void 调用分支无产值", unit12.Diagnostics,
                "if expression branch must produce a value");
        }

        // ===== definite assignment 分支合并（S7b：before ∪ (setT ∩ setF)）=====
        private static void TestBranchDefiniteAssignment()
        {
            TestHarness.Section("P3 Branch Definite Assignment");

            // 双分支都赋值 → 合并后可用
            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    if (a > 0) { x = 1 } else { x = 2 }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("双分支赋值后可用", unit);
            TestHarness.Check("双分支赋值形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32); " +
                "If(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Local(x,i32), Int(1,i32))], [Assign(Local(x,i32), Int(2,i32))]); " +
                "Return(Local(x,i32))])");

            // 无 else 仅单分支赋值 → 合并为 before（保守）
            var (unit2, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    if (a > 0) { x = 1 }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("仅单分支赋值报未赋值", unit2.Diagnostics,
                "Use of unassigned local variable 'x'");

            // else 分支不赋值 → 交集不含
            var (unit3, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    if (a > 0) { x = 1 } else { a = 2 }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("else 分支未赋值报未赋值", unit3.Diagnostics,
                "Use of unassigned local variable 'x'");

            // if 表达式形态同规则：两值块内赋值合并后可用
            var (unit4, bodies4) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    var r = if (a > 0) { x = 1\nreturn@_ x } else { x = 2\nreturn@_ x }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("if 表达式值块内赋值合并", unit4);
            TestHarness.Check("值块内赋值形态", BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [x: i32, r: i32], [Decl(x, i32); Decl(r, i32, = " +
                "IfExpr(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "ValueBlock(_, i32, [Assign(Local(x,i32), Int(1,i32)); " +
                "ReturnValue(_, Local(x,i32))]), " +
                "ValueBlock(_, i32, [Assign(Local(x,i32), Int(2,i32)); " +
                "ReturnValue(_, Local(x,i32))]), i32)); Return(Local(x,i32))])");

            // 分支内新声明的局部不泄出合并集（setT/setF 交集只含 before 可见名，
            // 此处验证分支内声明在分支后不可见）
            var (unit5, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    if (a > 0) { var y = 1 } else { var y = 2 }\n" +
                "    return y\n" +
                "}\n");
            TestHarness.CheckSemanticError("分支内声明分支后不可见", unit5.Diagnostics,
                "Undefined name: 'y'");
        }

        // ===== 复合赋值（S7b，SYNTAX §13.2）=====
        private static void TestCompoundAssignments()
        {
            TestHarness.Section("P3 Compound Assignments");

            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x = a\n" +
                "    x += 1\n" +
                "    x <<= 2\n" +
                "    return x\n" +
                "}\n" +
                "func g(a: i32): i32 {\n" +
                "    var x = a\n" +
                "    var y = (x += 1)\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("无诊断（复合赋值）", unit);
            TestHarness.Check("语句位置复合赋值", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Param(a,i32)); " +
                "ExprStmt(CompoundAssign(Add, Local(x,i32), Int(1,i32), i32)); " +
                "ExprStmt(CompoundAssign(ShiftLeft, Local(x,i32), Int(2,i32), i32)); " +
                "Return(Local(x,i32))])");
            TestHarness.Check("表达式位置取值（写回后值）",
                BoundDescribe.Body(BodyOf(bodies, "g")),
                "Body(g, [x: i32, y: i32], [Decl(x, i32, = Param(a,i32)); " +
                "Decl(y, i32, = CompoundAssign(Add, Local(x,i32), Int(1,i32), i32)); " +
                "Return(Local(y,i32))])");

            // 读语义：未赋值 target 报未赋值
            var (unit2, _) = BindUnit("func f() { var x: i32\nx += 1 }\n");
            TestHarness.CheckSemanticError("未赋值 target（读语义）", unit2.Diagnostics,
                "Use of unassigned local variable 'x'");

            var (unit3, _) = BindUnit("func f() { var x = 1\nx += \"s\" }\n");
            TestHarness.CheckSemanticError("操作数类型不一致", unit3.Diagnostics,
                "Compound assignment requires operands of the same type");

            var (unit4, _) = BindUnit("func f() { const c = 1\nc += 1 }\n");
            TestHarness.CheckSemanticError("const 复合赋值拒绝", unit4.Diagnostics,
                "Cannot assign to const 'c'");

            // S7f 起 String 有 Add intrinsic（§3.8 内建拼接）：s += "b" 合法
            var (unit5, bodies5) = BindUnit("func f() { var s = \"a\"\ns += \"b\" }\n");
            CheckNoErrors("String 复合赋值（内建拼接）", unit5);
            TestHarness.Check("String 复合赋值绑定形态", BoundDescribe.Body(BodyOf(bodies5, "f")),
                "Body(f, [s: String], [Decl(s, String, = Str(\"a\",String)); " +
                "ExprStmt(CompoundAssign(Add, Local(s,String), Str(\"b\",String), String))])");

            // 复合赋值即赋值：标记 assigned（此前未赋值的 x 经 += 后可用？——
            // += 是读+写，读时已要求已赋值，故此处验证写入后状态：x += 1 后再读合法）
            var (unit6, bodies6) = BindUnit(
                "class Counter { pub static var value: i32 }\n" +
                "func f() { Counter.value += 1 }\n");
            CheckNoErrors("static 字段复合赋值", unit6);
            TestHarness.Check("字段目标复合赋值", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [], [ExprStmt(CompoundAssign(Add, Field(value,i32), Int(1,i32), i32))])");
        }

        // ===== 循环（S7c-1：while/do-while 形态、条件 bool、DA 规则、for 拦截）=====
        private static void TestLoops()
        {
            TestHarness.Section("P3 Loops");

            var (unit, bodies) = BindUnit(
                "func f(a: i32) {\n" +
                "    var x = 0\n" +
                "    while (x < a) {\n" +
                "        x = x + 1\n" +
                "    }\n" +
                "}\n" +
                "func g(a: i32) {\n" +
                "    var x = 0\n" +
                "    do {\n" +
                "        x = x + 1\n" +
                "    } while (x < a)\n" +
                "}\n");
            CheckNoErrors("无诊断（while/do-while）", unit);
            TestHarness.Check("while 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Loop(while, Binary(CmpLt, Local(x,i32), Param(a,i32), bool), " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))])])");
            TestHarness.Check("do-while 绑定形态", BoundDescribe.Body(BodyOf(bodies, "g")),
                "Body(g, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "Loop(do-while, Binary(CmpLt, Local(x,i32), Param(a,i32), bool), " +
                "[Assign(Local(x,i32), Binary(Add, Local(x,i32), Int(1,i32), i32))])])");

            var (unit2, _) = BindUnit("func f(x: i32) { while (x) { } }\n");
            TestHarness.CheckSemanticError("while 条件非 bool", unit2.Diagnostics,
                "loop condition must be bool (got 'i32')");

            var (unit3, _) = BindUnit("func f(x: i32) { do { } while (x) }\n");
            TestHarness.CheckSemanticError("do-while 条件非 bool", unit3.Diagnostics,
                "loop condition must be bool (got 'i32')");

            // stdlib 缺席（BindUnit 不带 stdlib）：范围循环先报 operator 缺失
            var (unit4, _) = BindUnit("func f() { for (i in 0 to 3) { } }\n");
            TestHarness.CheckSemanticError("stdlib 缺席（无 EnumerateInRange operator）",
                unit4.Diagnostics, "has no EnumerateInRange operator");

            // DA：while 后 = before（体可能零次执行）——循环内赋值不生效
            var (unit5, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    while (a > 0) {\n" +
                "        x = 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("DA：while 后仍报未赋值", unit5.Diagnostics,
                "Use of unassigned local variable 'x'");

            // DA：do-while 后 = 体尾集合（体至少执行一次）
            var (unit6, bodies6) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    do {\n" +
                "        x = 1\n" +
                "    } while (a > 0)\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("DA：do-while 后视为已赋值", unit6);
            TestHarness.Check("do-while 赋值生效形态", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [x: i32], [Decl(x, i32); " +
                "Loop(do-while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Local(x,i32), Int(1,i32))]); Return(Local(x,i32))])");

            // GuaranteesReturn：循环保守 false（while (true) 特例留口）
            var (unit7, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    while (a > 0) { return 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("循环内 return 不保证返回（保守）", unit7.Diagnostics,
                "must return a value on all code paths");
        }

        // ===== break/continue（S7c-1：标签栈解析、穿透值块、诊断）=====
        private static void TestLoopControl()
        {
            TestHarness.Section("P3 Loop Control");

            var (unit, bodies) = BindUnit(
                "func f(a: i32) {\n" +
                "    while (a > 0) {\n" +
                "        if (a == 5) { break }\n" +
                "        if (a == 2) { continue }\n" +
                "        a = a - 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（break/continue）", unit);
            TestHarness.Check("无标签 break/continue 形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Loop(while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[If(Binary(CmpEq, Param(a,i32), Int(5,i32), bool), [Break]); " +
                "If(Binary(CmpEq, Param(a,i32), Int(2,i32), bool), [Continue]); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))])])");
            // 结构性事实：无标签命中最内层循环（引用相等）
            var loop = (BoundLoop)BodyOf(bodies, "f").Body.Statements[0];
            var breakStmt = (BoundLoopControl)((BoundIfStatement)
                loop.Body.Statements[0]).TrueBlock.Statements[0];
            TestHarness.CheckTrue("break 引用命中目标循环",
                ReferenceEquals(breakStmt.Target, loop));

            // named 嵌套标签：break@outer 穿透内层循环命中外层
            var (unit2, bodies2) = BindUnit(
                "func f(a: i32) {\n" +
                "    while (a > 0) named outer {\n" +
                "        while (a > 1) {\n" +
                "            break@outer\n" +
                "            continue\n" +
                "        }\n" +
                "        a = a - 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无诊断（named 标签）", unit2);
            TestHarness.Check("嵌套标签循环形态", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Loop(while@outer, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Loop(while, Binary(CmpGt, Param(a,i32), Int(1,i32), bool), " +
                "[Break@outer; Continue]); " +
                "Assign(Param(a,i32), Binary(Sub, Param(a,i32), Int(1,i32), i32))])])");
            // 结构性事实：break@outer 命中外层、无标签 continue 命中内层
            var outerLoop = (BoundLoop)BodyOf(bodies2, "f").Body.Statements[0];
            var innerLoop = (BoundLoop)outerLoop.Body.Statements[0];
            TestHarness.CheckTrue("break@outer 引用命中外层循环",
                ReferenceEquals(((BoundLoopControl)innerLoop.Body.Statements[0]).Target, outerLoop));
            TestHarness.CheckTrue("无标签 continue 引用命中内层循环",
                ReferenceEquals(((BoundLoopControl)innerLoop.Body.Statements[1]).Target, innerLoop));

            var (unit3, _) = BindUnit("func f() { while (true) { break@nope } }\n");
            TestHarness.CheckSemanticError("未定义循环标签", unit3.Diagnostics,
                "Undefined loop label: 'nope'");

            var (unit4, _) = BindUnit("func f() { break }\n");
            TestHarness.CheckSemanticError("循环外 break", unit4.Diagnostics,
                "'break' outside of a loop");

            var (unit5, _) = BindUnit("func f() { continue }\n");
            TestHarness.CheckSemanticError("循环外 continue", unit5.Diagnostics,
                "'continue' outside of a loop");

            // do-while 内 break
            var (unit6, bodies6) = BindUnit(
                "func f(a: i32) {\n" +
                "    do {\n" +
                "        break\n" +
                "    } while (a > 0)\n" +
                "}\n");
            CheckNoErrors("无诊断（do-while 内 break）", unit6);
            TestHarness.Check("do-while 内 break 形态", BoundDescribe.Body(BodyOf(bodies6, "f")),
                "Body(f, [], [Loop(do-while, Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Break])])");

            // 值块内 break 穿透（GuaranteesValueReturn 视其为路径终止）：
            // 纯穿透分支 + 产值分支的 if 表达式在循环内合法
            var (unit7, bodies7) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = 0\n" +
                "    while (x > 0) {\n" +
                "        r = if ((x == 5)) { break } else { return@_ 1 }\n" +
                "        x = x - 1\n" +
                "    }\n" +
                "    return r\n" +
                "}\n");
            CheckNoErrors("无诊断（值块内 break 穿透）", unit7);
            TestHarness.Check("值块内 break 穿透形态", BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [r: i32], [Decl(r, i32, = Int(0,i32)); " +
                "Loop(while, Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "[Assign(Local(r,i32), " +
                "IfExpr(Binary(CmpEq, Param(x,i32), Int(5,i32), bool), " +
                "ValueBlock(_, -, [Break]), " +
                "ValueBlock(_, i32, [ReturnValue(_, Int(1,i32))]), i32)); " +
                "Assign(Param(x,i32), Binary(Sub, Param(x,i32), Int(1,i32), i32))]); " +
                "Return(Local(r,i32))])");
            // 结构性事实：穿透 break 命中值块外的循环
            var outerWhile = (BoundLoop)BodyOf(bodies7, "f").Body.Statements[1];
            var assign = (BoundAssignmentStatement)outerWhile.Body.Statements[0];
            var ifExpr = (BoundIfExpression)assign.Value;
            TestHarness.CheckTrue("值块内 break 引用命中外层循环",
                ReferenceEquals(((BoundLoopControl)
                    ifExpr.TrueBranch.Block.Statements[0]).Target, outerWhile));

            // return@ 隔循环边界拦截（S7c 技术债：脱糖无法表达跳出中间循环）
            var (unit8, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        while (x > 1) { return@_ 1 }\n" +
                "        return@_ 2\n" +
                "    } else {\n" +
                "        return@_ 0\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("return@ 隔循环边界拦截", unit8.Diagnostics,
                "across a loop boundary not supported yet (S7c)");
        }

        // ===== 实例成员（S7c-2：this/实例调用/实例字段/裸名补 this/接口 receiver）=====
        private static void TestInstanceMembers()
        {
            TestHarness.Section("P3 Instance Members");

            var (unit, bodies) = BindUnit(
                "class Counter {\n" +
                "    pub var value: i32\n" +
                "    pub func add(n: i32): i32 {\n" +
                "        return value + n\n" +
                "    }\n" +
                "    pub func bump(): i32 {\n" +
                "        return this.add(1)\n" +
                "    }\n" +
                "}\n" +
                "func read(c: Counter): i32 { return c.value }\n" +
                "func call(c: Counter): i32 { return c.add(2) }\n" +
                "func write(c: Counter) { c.value = 5 }\n");
            CheckNoErrors("无诊断（实例成员）", unit);
            TestHarness.Check("裸名实例字段 → this.value",
                BoundDescribe.Body(BodyOf(bodies, "add")),
                "Body(add, [], [Return(Binary(Add, " +
                "InstField(value, This(Counter), i32), Param(n,i32), i32))])");
            TestHarness.Check("this 链实例调用",
                BoundDescribe.Body(BodyOf(bodies, "bump")),
                "Body(bump, [], [Return(InstCall(add, This(Counter), [Int(1,i32)], i32))])");
            TestHarness.Check("实例字段访问（参数 receiver）",
                BoundDescribe.Body(BodyOf(bodies, "read")),
                "Body(read, [], [Return(InstField(value, Param(c,Counter), i32))])");
            TestHarness.Check("实例方法调用（参数 receiver）",
                BoundDescribe.Body(BodyOf(bodies, "call")),
                "Body(call, [], [Return(InstCall(add, Param(c,Counter), [Int(2,i32)], i32))])");
            TestHarness.Check("实例字段写入",
                BoundDescribe.Body(BodyOf(bodies, "write")),
                "Body(write, [], [Assign(InstField(value, Param(c,Counter), i32), Int(5,i32))])");
            // 结构性事实：字段/方法符号引用相等（符号图唯一实例）
            var counterType = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Counter");
            var addReturn = (BoundReturnStatement)BodyOf(bodies, "add").Body.Statements[0];
            var valueAccess = (BoundFieldAccessExpression)
                ((BoundBinaryExpression)addReturn.Value!).Left;
            TestHarness.CheckTrue("实例字段符号引用相等",
                ReferenceEquals(valueAccess.Field,
                    counterType.Fields.Single(f => f.Name == "value")));
            var callReturn = (BoundReturnStatement)BodyOf(bodies, "call").Body.Statements[0];
            TestHarness.CheckTrue("实例方法符号引用相等",
                ReferenceEquals(((BoundInstanceCallExpression)callReturn.Value!).Method,
                    counterType.Methods.Single(m => m.Name == "add")));

            // 裸名实例方法调用（this 隐式 receiver）
            var (unit2, bodies2) = BindUnit(
                "class C {\n" +
                "    pub func a(): i32 { return b() }\n" +
                "    pub func b(): i32 { return 1 }\n" +
                "}\n");
            CheckNoErrors("无诊断（裸名实例方法）", unit2);
            TestHarness.Check("裸名实例方法 → InstCall(this)",
                BoundDescribe.Body(BodyOf(bodies2, "a")),
                "Body(a, [], [Return(InstCall(b, This(C), [], i32))])");

            // 实例链两段（字段的字段）
            var (unit3, bodies3) = BindUnit(
                "class Inner { pub var n: i32 }\n" +
                "class Outer { pub var child: Inner }\n" +
                "func f(o: Outer): i32 { return o.child.n }\n");
            CheckNoErrors("无诊断（两段实例链）", unit3);
            TestHarness.Check("o.child.n 链上色",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(InstField(n, " +
                "InstField(child, Param(o,Outer), Inner), i32))])");

            // 静态上下文诊断
            var (unit4, _) = BindUnit("func f(): i32 { return this }\n");
            TestHarness.CheckSemanticError("全局函数 this", unit4.Diagnostics,
                "'this' is not available in a static context");

            var (unit5, _) = BindUnit(
                "class C { pub var x: i32\npub static func f(): i32 { return x } }\n");
            TestHarness.CheckSemanticError("static 方法裸名实例字段", unit5.Diagnostics,
                "instance field 'x' requires a receiver");

            var (unit6, _) = BindUnit(
                "class C { pub func m(): i32 { return 1 }\n" +
                "pub static func f(): i32 { return m() } }\n");
            TestHarness.CheckSemanticError("static 方法裸名实例方法", unit6.Diagnostics,
                "instance method 'm' requires a receiver");

            // 接口 receiver：接口方法符号引用（分派归 Middleware）
            var (unit7, bodies7) = BindUnit(
                "interface Sized { func size(): i32 }\n" +
                "class Box implements Sized {\n" +
                "    pub override func size(): i32 { return 42 }\n" +
                "}\n" +
                "func f(s: Sized): i32 { return s.size() }\n");
            CheckNoErrors("无诊断（接口 receiver）", unit7);
            TestHarness.Check("接口方法调用",
                BoundDescribe.Body(BodyOf(bodies7, "f")),
                "Body(f, [], [Return(InstCall(size, Param(s,Sized), [], i32))])");
            var sizedType = unit7.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Sized");
            var fReturn = (BoundReturnStatement)BodyOf(bodies7, "f").Body.Statements[0];
            TestHarness.CheckTrue("接口方法符号引用（接口自身成员）",
                ReferenceEquals(((BoundInstanceCallExpression)fReturn.Value!).Method,
                    sizedType.Methods.Single(m => m.Name == "size")));
        }

        // ===== for 双形态（S7c-2：范围循环/for-each 协议，带 stdlib）=====
        private static void TestForLoops()
        {
            TestHarness.Section("P3 For Loops");

            // 范围循环：EnumerateInRange operator 调用 + 协议判定
            var (unit, bodies) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    for (i in 0 to 5) {\n" +
                "        sum = sum + i\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("无诊断（范围循环）", unit);
            TestHarness.Check("范围循环绑定形态", BoundDescribe.Body(BodyOf(bodies, "main")),
                "Body(main, [sum: i32, i: i32], [Decl(sum, i32, = Int(0,i32)); " +
                "For(i, InstCall(EnumerateInRange, Int(0,i32), [Int(5,i32)], IEnumerable<i32>), " +
                "[Assign(Local(sum,i32), Binary(Add, Local(sum,i32), Local(i,i32), i32))]); " +
                "Return(Local(sum,i32))])");
            // 结构性事实：Iterable 是 operator 实例调用；循环变量 const i32；
            // 协议三方法挂好（接口方法符号引用）
            var rangeLoop = (BoundLoop)BodyOf(bodies, "main").Body.Statements[1];
            var iterable = rangeLoop.Iterable as BoundInstanceCallExpression;
            TestHarness.CheckTrue("Iterable = EnumerateInRange operator 调用",
                iterable != null && iterable.Method.Kind == MethodKind.Operator
                && iterable.Method.Name == "EnumerateInRange");
            TestHarness.CheckTrue("循环变量 const i32",
                rangeLoop.LoopVariable != null && rangeLoop.LoopVariable.IsConst
                && ReferenceEquals(rangeLoop.LoopVariable.Type,
                    unit.Symbols.Bootstrap.Int32));
            var collectionsNs = unit.Symbols.GlobalNamespace.ChildNamespaces
                .Single(n => n.Name == "core").ChildNamespaces
                .Single(n => n.Name == "collections");
            var enumerableDef = collectionsNs.Types.Single(t => t.Name == "IEnumerable");
            var enumeratorDef = collectionsNs.Types.Single(t => t.Name == "IEnumerator");
            TestHarness.CheckTrue("协议三方法符号引用（接口成员）",
                ReferenceEquals(rangeLoop.IterateMethod,
                    enumerableDef.Methods.Single(m => m.Name == "iterate"))
                && ReferenceEquals(rangeLoop.MoveNextMethod,
                    enumeratorDef.Methods.Single(m => m.Name == "moveNext"))
                && ReferenceEquals(rangeLoop.CurrentMethod,
                    enumeratorDef.Methods.Single(m => m.Name == "current")));

            // for-each：集合类型实现 IEnumerable<i32>（stdlib RangeI32）
            var (unit2, bodies2) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var sum = 0\n" +
                "    var r = new core.collections.RangeI32(0, 3)\n" +
                "    for (x in r) {\n" +
                "        sum = sum + x\n" +
                "    }\n" +
                "    return sum\n" +
                "}\n");
            CheckNoErrors("无诊断（for-each）", unit2);
            TestHarness.Check("for-each 绑定形态", BoundDescribe.Body(BodyOf(bodies2, "main")),
                "Body(main, [sum: i32, r: RangeI32, x: i32], [Decl(sum, i32, = Int(0,i32)); " +
                "Decl(r, RangeI32, = New(RangeI32, init, [Int(0,i32), Int(3,i32)])); " +
                "For(x, Local(r,RangeI32), " +
                "[Assign(Local(sum,i32), Binary(Add, Local(sum,i32), Local(x,i32), i32))]); " +
                "Return(Local(sum,i32))])");

            // 诊断：未实现 IEnumerable 的类型
            var (unit3, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (x in 42) { }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("迭代源未实现 IEnumerable", unit3.Diagnostics,
                "does not implement core.collections.IEnumerable<T>");

            // 诊断：范围两端类型不一致
            var (unit4, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (i in 0 to \"s\") { }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("范围两端类型不一致", unit4.Diagnostics,
                "Range bounds must have the same type (got 'i32' and 'String')");

            // 诊断：循环变量 const 写入
            var (unit5, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    for (i in 0 to 3) {\n" +
                "        i = 5\n" +
                "    }\n" +
                "    return 0\n" +
                "}\n");
            TestHarness.CheckSemanticError("循环变量 const 写入拒绝", unit5.Diagnostics,
                "Cannot assign to const 'i'");

            // DA：for 后 = before（体可能零次执行）
            var (unit6, _) = BindUnitWithStdlib(
                "pub func main(): i32 {\n" +
                "    var x: i32\n" +
                "    for (i in 0 to 3) {\n" +
                "        x = 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("DA：for 后仍报未赋值", unit6.Diagnostics,
                "Use of unassigned local variable 'x'");
        }

        // ===== switch 语句/表达式（S7d，SYNTAX §7.2）=====
        private static void TestSwitch()
        {
            TestHarness.Section("P3 Switch");

            // 语句形态：常量值匹配；全分支 return → GuaranteesReturn 升级（无缺返回诊断）
            var (unit, bodies) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (2) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("常量 switch 无诊断", unit);
            TestHarness.Check("常量 switch 语句形态",
                BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Switch(Param(x,i32), " +
                "[Case(Int(1,i32), [Return(Int(1,i32))]); Case(Int(2,i32), [Return(Int(2,i32))])], " +
                "[Return(Int(0,i32))])])");

            // 混合分类：含 _ 的 case 记 IsPattern（_ 绑为占位，结果 bool）
            var (unit2, bodies2) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (1) -> { return 1 }\n" +
                "        (_ > 10) -> { return 2 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("混合 switch 无诊断", unit2);
            TestHarness.Check("pattern 分支占位绑定",
                BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Switch(Param(x,i32), " +
                "[Case(Int(1,i32), [Return(Int(1,i32))]); " +
                "CaseP(Binary(CmpGt, Placeholder(i32), Int(10,i32), bool), [Return(Int(2,i32))])], " +
                "[Return(Int(0,i32))])])");
            var switchStmt2 = (BoundSwitchStatement)((BoundBlock)BodyOf(bodies2, "f").Body).Statements[0];
            TestHarness.CheckTrue("IsPattern 分类显式记录",
                !switchStmt2.Cases[0].IsPattern && switchStmt2.Cases[1].IsPattern);
            var placeholder2 = (BoundSwitchPlaceholderExpression)
                ((BoundBinaryExpression)switchStmt2.Cases[1].Match).Left;
            TestHarness.CheckTrue("占位 Selector 回指引用相等（嵌套消歧）",
                ReferenceEquals(placeholder2.Selector, switchStmt2.Selector));

            // 表达式形态：单表达式分支隐式取值，全分支统一产值类型
            var (unit3, bodies3) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 10 }\n" +
                "        (_ > 5) -> { 20 }\n" +
                "        default -> { 0 }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("switch 表达式无诊断", unit3);
            TestHarness.Check("switch 表达式（隐式取值）",
                BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(SwitchExpr(Param(x,i32), " +
                "[Case(Int(1,i32), ValueBlock(_, i32, implicit, [ExprStmt(Int(10,i32))])); " +
                "CaseP(Binary(CmpGt, Placeholder(i32), Int(5,i32), bool), " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Int(20,i32))]))], " +
                "ValueBlock(_, i32, implicit, [ExprStmt(Int(0,i32))]), i32))])");

            // 表达式形态 named：多语句分支显式 return@标签
            var (unit4, bodies4) = BindUnit(
                "func f(x: i32): String {\n" +
                "    return switch (x) named match {\n" +
                "        (1) -> { return@match \"one\" }\n" +
                "        default -> { var y = \"other\"\nreturn@match y }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("named switch 表达式无诊断", unit4);
            TestHarness.Check("named switch 表达式（return@标签）",
                BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [y: String], [Return(SwitchExpr(Param(x,i32), " +
                "[Case(Int(1,i32), ValueBlock(match, String, [ReturnValue(match, Str(\"one\",String))]))], " +
                "ValueBlock(match, String, [Decl(y, String, = Str(\"other\",String)); " +
                "ReturnValue(match, Local(y,String))]), String))])");

            // 值块穿透：值块末语句为 switch 且全分支 return@ → 路径终止 +
            // 穿透命中的 return@ 参与外层产值类型收集
            var (unit5, bodies5) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return if (x > 0) {\n" +
                "        switch (x) {\n" +
                "            (1) -> { return@_ 1 }\n" +
                "            default -> { return@_ 2 }\n" +
                "        }\n" +
                "    } else { 0 }\n" +
                "}\n");
            CheckNoErrors("值块内 switch 穿透无诊断", unit5);
            TestHarness.CheckTrue("穿透后产值类型为 i32",
                ((BoundIfExpression)((BoundReturnStatement)
                    ((BoundBlock)BodyOf(bodies5, "f").Body).Statements[0]).Value!).Type.Name == "i32");

            // DA：全分支（含 default）都赋值 → 合并后可用
            var (unit6, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var y: i32\n" +
                "    switch (x) {\n" +
                "        (1) -> { y = 1 }\n" +
                "        default -> { y = 0 }\n" +
                "    }\n" +
                "    return y\n" +
                "}\n");
            CheckNoErrors("DA：全分支赋值合并后可用", unit6);

            // 诊断：值匹配非常量
            var (unit7, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var y = 1\n" +
                "    switch (x) {\n" +
                "        (y) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("值匹配非常量", unit7.Diagnostics,
                "switch value-match case requires a compile-time constant");

            // 诊断：case 常量类型与 selector 不符
            var (unit8, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (\"s\") -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("常量类型与 selector 不符", unit8.Diagnostics,
                "switch case constant type must equal the selector type (got 'String' and 'i32')");

            // 诊断：pattern 结果非 bool
            var (unit9, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    switch (x) {\n" +
                "        (_ + 1) -> { return 1 }\n" +
                "        default -> { return 0 }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("pattern 非 bool", unit9.Diagnostics,
                "switch pattern case must be bool (got 'i32')");

            // 诊断：DA 仅单分支赋值 → 合并后仍未赋值
            var (unit10, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var y: i32\n" +
                "    switch (x) {\n" +
                "        (1) -> { y = 1 }\n" +
                "        default -> { }\n" +
                "    }\n" +
                "    return y\n" +
                "}\n");
            TestHarness.CheckSemanticError("DA：单分支赋值合并后仍报未赋值", unit10.Diagnostics,
                "Use of unassigned local variable 'y'");

            // 诊断：switch 表达式产值类型不一致
            var (unit11, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    return switch (x) {\n" +
                "        (1) -> { 1 }\n" +
                "        default -> { \"s\" }\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("switch 表达式产值类型不一致", unit11.Diagnostics,
                "switch expression branches produce different types ('i32' and 'String')");
        }

        // ===== throw（S7d，SYNTAX §8；异常根 core.Exception 进 bootstrap）=====
        private static void TestThrow()
        {
            TestHarness.Section("P3 Throw");

            // throw 终止路径：函数仅 throw 即满足「所有路径显式返回」
            var (unit, bodies) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func f(): i32 {\n" +
                "    throw new MyException()\n" +
                "}\n");
            CheckNoErrors("throw 无诊断", unit);
            TestHarness.Check("throw 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Throw(New(MyException, []))])");
            var throwStmt = (BoundThrowStatement)((BoundBlock)BodyOf(bodies, "f").Body).Statements[0];
            TestHarness.CheckTrue("异常表达式定型为用户异常类",
                throwStmt.Exception.Type.Name == "MyException");

            // 直接抛异常根本身（open class 零参构造）
            var (unit2, _) = BindUnit(
                "func g() {\n" +
                "    throw new core.Exception()\n" +
                "}\n");
            CheckNoErrors("直接抛 core.Exception 无诊断", unit2);

            // 诊断：throw 非异常类型
            var (unit3, _) = BindUnit(
                "func f() {\n" +
                "    throw 1\n" +
                "}\n");
            TestHarness.CheckSemanticError("throw 非异常类型", unit3.Diagnostics,
                "Cannot throw 'i32' (not compatible with 'Exception')");
        }

        // ===== cast（S7e，SYNTAX §3.5）=====
        private static void TestCast()
        {
            TestHarness.Section("P3 Cast");

            // as：结果类型即目标类型
            var (unit, bodies) = BindUnit(
                "func f(s: String): String {\n" +
                "    return s as String\n" +
                "}\n");
            CheckNoErrors("as 无诊断", unit);
            TestHarness.Check("as 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(Cast(Param(s,String), String))])");
            var cast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies, "f").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("as 结果类型即目标类型",
                cast.Type.Name == "String" && !cast.IsSafe);

            // 向下转换不做静态拒绝（as 失败是运行时 core.CastException）
            var (unit2, bodies2) = BindUnit(
                "open class Animal {\n" +
                "}\n" +
                "class Dog : Animal {\n" +
                "}\n" +
                "func d(a: Animal): Dog {\n" +
                "    return a as Dog\n" +
                "}\n");
            CheckNoErrors("向下转换无诊断", unit2);
            TestHarness.Check("向下转换形态", BoundDescribe.Body(BodyOf(bodies2, "d")),
                "Body(d, [], [Return(Cast(Param(a,Animal), Dog))])");

            // as?：结果类型 = Nullable<目标类型>
            var (unit3, bodies3) = BindUnit(
                "func g(s: String): String? {\n" +
                "    return s as? String\n" +
                "}\n");
            CheckNoErrors("as? 无诊断", unit3);
            TestHarness.Check("as? 绑定形态", BoundDescribe.Body(BodyOf(bodies3, "g")),
                "Body(g, [], [Return(SafeCast(Param(s,String), String))])");
            var safeCast = (BoundCastExpression)((BoundReturnStatement)
                ((BoundBlock)BodyOf(bodies3, "g").Body).Statements[0]).Value!;
            TestHarness.CheckTrue("as? 结果类型为 Nullable<String>（目标类型不包装）",
                safeCast.IsSafe && safeCast.Type.Name == "Nullable"
                && safeCast.TargetType.Name == "String");

            // 诊断：目标类型解析失败
            var (unit4, _) = BindUnit(
                "func h(s: String): i32 {\n" +
                "    return s as Nosuch\n" +
                "}\n");
            TestHarness.CheckSemanticError("cast 目标类型未定义", unit4.Diagnostics,
                "Unresolved type or namespace: 'Nosuch'");
        }

        // ===== try-catch-finally（S7e，SYNTAX §8）=====
        private static void TestTry()
        {
            TestHarness.Section("P3 Try-Catch-Finally");

            // 基本形态：catch 变量 const，命中即已赋值
            var (unit, bodies) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func handle(e: MyException) {\n" +
                "}\n" +
                "func f() {\n" +
                "    try {\n" +
                "        throw new MyException()\n" +
                "    } catch (e: MyException) {\n" +
                "        handle(e)\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("try-catch 无诊断", unit);
            TestHarness.Check("try-catch 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [e: MyException], [Try([Throw(New(MyException, []))], " +
                "[Catch(e, MyException, [CallStmt(handle, [Local(e,MyException)])])])])");
            var tryStmt = (BoundTryStatement)((BoundBlock)BodyOf(bodies, "f").Body).Statements[0];
            TestHarness.CheckTrue("catch 变量 const",
                tryStmt.Catches[0].Variable != null && tryStmt.Catches[0].Variable!.IsConst);

            // _: 无变量形态
            var (unit2, bodies2) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func risky() {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func g() {\n" +
                "    try {\n" +
                "        risky()\n" +
                "    } catch (_: MyException) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("_: catch 无诊断", unit2);
            TestHarness.Check("_: catch 形态", BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [Try([CallStmt(risky, [])], " +
                "[Catch(MyException, [CallStmt(log, [])])])])");
            var try2 = (BoundTryStatement)((BoundBlock)BodyOf(bodies2, "g").Body).Statements[0];
            TestHarness.CheckTrue("_: 无 catch 变量", try2.Catches[0].Variable == null);

            // finally(e)：e 类型 = Nullable<core.Exception>，const
            var (unit3, bodies3) = BindUnit(
                "func risky() {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func h() {\n" +
                "    try {\n" +
                "        risky()\n" +
                "    } finally(e) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("finally(e) 无诊断", unit3);
            TestHarness.Check("finally(e) 形态", BoundDescribe.Body(BodyOf(bodies3, "h")),
                "Body(h, [e: Exception?], [Try([CallStmt(risky, [])], [], " +
                "Finally(e, [CallStmt(log, [])]))])");
            var try3 = (BoundTryStatement)((BoundBlock)BodyOf(bodies3, "h").Body).Statements[0];
            TestHarness.CheckTrue("finally 变量类型 Nullable<Exception> 且 const",
                try3.FinallyVariable != null && try3.FinallyVariable.IsConst
                && try3.FinallyVariable.Type!.Name == "Nullable");

            // 诊断：catch 类型与 Exception 不兼容
            var (unit4, _) = BindUnit(
                "func bad() {\n" +
                "    try {\n" +
                "    } catch (e: i32) {\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("catch 类型不兼容", unit4.Diagnostics,
                "catch type must be compatible with 'Exception' (got 'i32')");

            // 诊断：catch 变量 const 赋值拒绝
            var (unit5, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func cb() {\n" +
                "    try {\n" +
                "    } catch (e: MyException) {\n" +
                "        e = new MyException()\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("catch 变量只读", unit5.Diagnostics,
                "Cannot assign to const 'e'");

            // definite assignment：try/catch 交集——仅 try 赋值不够
            var (unit6, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func da(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } catch (_: MyException) {\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            TestHarness.CheckSemanticError("仅 try 赋值报未赋值", unit6.Diagnostics,
                "Use of unassigned local variable 'x'");

            // definite assignment：try 与 catch 都赋值 → 交集成立
            var (unit7, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func da2(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } catch (_: MyException) {\n" +
                "        x = 2\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("try/catch 双赋值通过", unit7);

            // definite assignment：无 catch 时 try 直通（异常必穿透）
            var (unit8, _) = BindUnit(
                "func da3(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } finally(f) {\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("无 catch try 直通", unit8);

            // definite assignment：finally 恒执行并集
            var (unit9, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func da4(): i32 {\n" +
                "    var x: i32\n" +
                "    try {\n" +
                "        x = 1\n" +
                "    } catch (_: MyException) {\n" +
                "    } finally(f) {\n" +
                "        x = 3\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("finally 并集通过", unit9);

            // GuaranteesReturn：finally 终止覆盖所有路径
            var (unit10, _) = BindUnit(
                "func risky() {\n" +
                "}\n" +
                "func gr(): i32 {\n" +
                "    try {\n" +
                "        risky()\n" +
                "    } finally(f) {\n" +
                "        return 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("finally 终止即保证返回", unit10);

            // GuaranteesReturn：try 与全部 catch 都返回
            var (unit11, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func gr2(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } catch (_: MyException) {\n" +
                "        return 2\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("全分支返回通过", unit11);

            // GuaranteesReturn：无 catch 时 try 单块判定（All 真空 true）
            var (unit12, _) = BindUnit(
                "func gr3(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } finally(f) {\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("无 catch try 返回通过", unit12);

            // 诊断：try 返回但 catch 不返回
            var (unit13, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func log() {\n" +
                "}\n" +
                "func gr4(): i32 {\n" +
                "    try {\n" +
                "        return 1\n" +
                "    } catch (_: MyException) {\n" +
                "        log()\n" +
                "    }\n" +
                "}\n");
            TestHarness.CheckSemanticError("catch 不返回报缺失", unit13.Diagnostics,
                "Function 'gr4' must return a value on all code paths");
        }

        // ===== seq（S7e，SYNTAX §10）=====
        private static void TestSeq()
        {
            TestHarness.Section("P3 Seq");

            // 语句形态：块级直通（作用域/assigned 语义同裸块）
            var (unit, bodies) = BindUnit(
                "func s() {\n" +
                "    seq {\n" +
                "        var x = 1\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("seq 语句无诊断", unit);
            TestHarness.Check("seq 语句形态", BoundDescribe.Body(BodyOf(bodies, "s")),
                "Body(s, [x: i32], [Seq([Decl(x, i32, = Int(1,i32))])])");

            // volatile 语句形态
            var (unit2, bodies2) = BindUnit(
                "func work() {\n" +
                "}\n" +
                "func s2() {\n" +
                "    volatile seq {\n" +
                "        work()\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("volatile seq 无诊断", unit2);
            TestHarness.Check("volatile seq 形态", BoundDescribe.Body(BodyOf(bodies2, "s2")),
                "Body(s2, [], [SeqVolatile([CallStmt(work, [])])])");

            // definite assignment 直通：seq 内赋值对外可见
            var (unit3, _) = BindUnit(
                "func sd(): i32 {\n" +
                "    var x: i32\n" +
                "    seq {\n" +
                "        x = 1\n" +
                "    }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("seq 赋值直通", unit3);

            // 表达式形态：显式 return@_
            var (unit4, bodies4) = BindUnit(
                "func se(): i32 {\n" +
                "    return seq { return@_ 42 }\n" +
                "}\n");
            CheckNoErrors("seq 表达式无诊断", unit4);
            TestHarness.Check("seq 表达式形态", BoundDescribe.Body(BodyOf(bodies4, "se")),
                "Body(se, [], [Return(SeqExpr(ValueBlock(_, i32, [ReturnValue(_, Int(42,i32))])))])");

            // 表达式形态：隐式取值（单表达式语句）
            var (unit5, bodies5) = BindUnit(
                "func si(): i32 {\n" +
                "    return seq { 42 }\n" +
                "}\n");
            CheckNoErrors("隐式取值无诊断", unit5);
            TestHarness.Check("隐式取值形态", BoundDescribe.Body(BodyOf(bodies5, "si")),
                "Body(si, [], [Return(SeqExpr(ValueBlock(_, i32, implicit, [ExprStmt(Int(42,i32))])))])");

            // 表达式形态：named 标签
            var (unit6, bodies6) = BindUnit(
                "func sn(): i32 {\n" +
                "    return seq named calc { return@calc 7 }\n" +
                "}\n");
            CheckNoErrors("named seq 无诊断", unit6);
            TestHarness.Check("named seq 形态", BoundDescribe.Body(BodyOf(bodies6, "sn")),
                "Body(sn, [], [Return(SeqExpr(ValueBlock(calc, i32, [ReturnValue(calc, Int(7,i32))])))])");

            // 表达式形态：volatile 置位到值块
            var (unit7, bodies7) = BindUnit(
                "func sv(): i32 {\n" +
                "    return volatile seq { 1 }\n" +
                "}\n");
            CheckNoErrors("volatile seq 表达式无诊断", unit7);
            TestHarness.Check("volatile 置位", BoundDescribe.Body(BodyOf(bodies7, "sv")),
                "Body(sv, [], [Return(SeqExpr(ValueBlock(_, i32, implicit, volatile, " +
                "[ExprStmt(Int(1,i32))])))])");

            // 诊断：表达式形态无产值
            var (unit8, _) = BindUnit(
                "func sb(): i32 {\n" +
                "    return seq { var x = 1\nreturn@_ x }\n" +
                "}\n");
            CheckNoErrors("多语句显式 return@ 无诊断", unit8);
            var (unit9, _) = BindUnit(
                "func sb2(): i32 {\n" +
                "    return seq { var x = 1 }\n" +
                "}\n");
            TestHarness.CheckSemanticError("无产值拒绝", unit9.Diagnostics,
                "seq expression must produce a value (at least one path must return@ a value)");

            // 诊断：using 绑定归 S13（语句与表达式形态同拦截）
            var (unit10, _) = BindUnit(
                "func openFile(): i32 {\n" +
                "    return 1\n" +
                "}\n" +
                "func use(f: i32) {\n" +
                "}\n" +
                "func su() {\n" +
                "    seq using(const file = openFile()) { use(file) }\n" +
                "}\n");
            TestHarness.CheckSemanticError("using 拦截", unit10.Diagnostics,
                "P3: using bindings are not supported yet (S13)");

            // 诊断：语句 seq 不压值块栈——return@ 指向它报未定义标签
            var (unit11, _) = BindUnit(
                "func sl(): i32 {\n" +
                "    seq {\n" +
                "        return@_ 1\n" +
                "    }\n" +
                "    return 2\n" +
                "}\n");
            TestHarness.CheckSemanticError("语句 seq 无标签", unit11.Diagnostics,
                "Undefined value block label: '_'");

            // return@ 穿透语句 seq 命中外层值块（末语句为 seq → 体穿透判定）
            var (unit12, bodies12) = BindUnit(
                "func st(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        seq {\n" +
                "            return@_ 1\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("seq 穿透无诊断", unit12);
            TestHarness.Check("seq 穿透形态", BoundDescribe.Body(BodyOf(bodies12, "st")),
                "Body(st, [dummy: i32], [Return(SeqExpr(ValueBlock(_, i32, [Decl(dummy, i32, = Int(0,i32)); Seq([ReturnValue(_, Int(1,i32))])])))])");

            // return@ 穿透 try 命中外层值块（try 与全部 catch 终止）
            var (unit13, _) = BindUnit(
                "class MyException : core.Exception {\n" +
                "}\n" +
                "func tt(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        try {\n" +
                "            return@_ 1\n" +
                "        } catch (_: MyException) {\n" +
                "            return@_ 2\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("try 穿透无诊断", unit13);

            // return@ 穿透 try——finally 终止覆盖
            var (unit14, _) = BindUnit(
                "func tt2(): i32 {\n" +
                "    return seq {\n" +
                "        var dummy = 0\n" +
                "        try {\n" +
                "            dummy = 1\n" +
                "        } finally(f) {\n" +
                "            return@_ 3\n" +
                "        }\n" +
                "    }\n" +
                "}\n");
            CheckNoErrors("finally 覆盖穿透无诊断", unit14);
        }

        // ===== 字符串插值（S7f，SYNTAX §3.8）=====
        private static void TestStringInterpolation()
        {
            TestHarness.Section("P3 String Interpolation");

            // 段序列绑定为左结合 + 链（String.Add intrinsic）
            var (unit, bodies) = BindUnit(
                "func f() {\n" +
                "    var x = \"a\"\n" +
                "    var y = \"b${x}c\"\n" +
                "}\n");
            CheckNoErrors("无诊断（插值拼接）", unit);
            TestHarness.Check("插值绑定为左结合 + 链", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: String, y: String], [Decl(x, String, = Str(\"a\",String)); " +
                "Decl(y, String, = Binary(Add, Binary(Add, Str(\"b\",String), Local(x,String), String), " +
                "Str(\"c\",String), String))])");

            // 非 String 段 → toString() 实例调用（Any 承诺）；单段无拼接
            var (unit2, bodies2) = BindUnit(
                "func f() {\n" +
                "    var n = 42\n" +
                "    var s = \"n=${n}\"\n" +
                "}\n");
            CheckNoErrors("无诊断（非 String 段）", unit2);
            TestHarness.Check("非 String 段包 toString 调用", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [n: i32, s: String], [Decl(n, i32, = Int(42,i32)); " +
                "Decl(s, String, = Binary(Add, Str(\"n=\",String), " +
                "InstCall(toString, Local(n,i32), [], String), String))])");

            // String 段直拼（不再包 toString）
            var (unit3, bodies3) = BindUnit(
                "func f(x: String): String { return \"${x}!\" }\n");
            CheckNoErrors("无诊断（String 段直拼）", unit3);
            TestHarness.Check("String 段直拼无 toString", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(Binary(Add, Param(x,String), Str(\"!\",String), String))])");

            // String 的 + 运算符随 S7f 开放（内建拼接，BIL §11.2）
            var (unit4, bodies4) = BindUnit(
                "func f(): String { return \"a\" + \"b\" }\n");
            CheckNoErrors("无诊断（String + 开放）", unit4);
            TestHarness.Check("String + 绑定形态", BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [], [Return(Binary(Add, Str(\"a\",String), Str(\"b\",String), String))])");

            // 显式 toString 调用（全类型承诺，含值类型）
            var (unit5, bodies5) = BindUnit(
                "func f() {\n" +
                "    var n = 1\n" +
                "    var s = n.toString()\n" +
                "}\n");
            CheckNoErrors("无诊断（显式 toString）", unit5);
            TestHarness.Check("值类型显式 toString 调用", BoundDescribe.Body(BodyOf(bodies5, "f")),
                "Body(f, [n: i32, s: String], [Decl(n, i32, = Int(1,i32)); " +
                "Decl(s, String, = InstCall(toString, Local(n,i32), [], String))])");

            // void 段：插值段必须产值
            var (unit6, _) = BindUnit(
                "func g() { }\n" +
                "func f(): String { return \"${g()}\" }\n");
            TestHarness.CheckSemanticError("void 插值段诊断", unit6.Diagnostics,
                "has no result (void) and cannot be used as a value");
        }

        // ===== 安全访问 `?.`（S7f，SYNTAX §3.4）=====
        private static void TestSafeAccess()
        {
            TestHarness.Section("P3 Safe Access (?.)");

            // 字段安全访问：结果包装 Nullable（成员 String → String?）
            var (unit, bodies) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): String? { return u?.name }\n");
            CheckNoErrors("无诊断（字段安全访问）", unit);
            TestHarness.Check("字段安全访问绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(SafeAccess(Param(u,User?), " +
                "InstField(name, SafeReceiver(User), String), String?))])");

            // 方法安全访问 + 已可空成员不二次包装
            var (unit2, bodies2) = BindUnit(
                "class Box { pub var content: String?\n    pub init(_ -> content) { } }\n" +
                "func f(b: Box?): String? { return b?.content }\n" +
                "func g(b: Box?): i32 { return 1 }\n");
            CheckNoErrors("无诊断（可空成员安全访问）", unit2);
            TestHarness.Check("已可空成员结果不二次包装", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Return(SafeAccess(Param(b,Box?), " +
                "InstField(content, SafeReceiver(Box), String?), String?))])");

            // 链式：a?.b?.c（a: A?，A.b: B，B.c: String）
            var (unit3, bodies3) = BindUnit(
                "class B { pub var c: String\n    pub init(_ -> c) { } }\n" +
                "class A { pub var b: B\n    pub init(_ -> b) { } }\n" +
                "func f(a: A?): String? { return a?.b?.c }\n");
            CheckNoErrors("无诊断（链式安全访问）", unit3);
            TestHarness.Check("链式安全访问绑定形态", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(SafeAccess(SafeAccess(Param(a,A?), " +
                "InstField(b, SafeReceiver(A), B), B?), " +
                "InstField(c, SafeReceiver(B), String), String?))])");

            // 诊断：非空 receiver
            var (unit4, _) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User) { var x = u?.name }\n");
            TestHarness.CheckSemanticError("非空 receiver 拒绝", unit4.Diagnostics,
                "Safe access '?.' requires a nullable receiver");

            // 诊断：可空结果上的普通段（须逐段标注 ?.）
            var (unit5, _) = BindUnit(
                "class B { pub var c: String\n    pub init(_ -> c) { } }\n" +
                "class A { pub var b: B?\n    pub init(_ -> b) { } }\n" +
                "func f(a: A?) { var x = a?.b.c }\n");
            TestHarness.CheckSemanticError("可空结果普通段拒绝", unit5.Diagnostics,
                "cannot be accessed on nullable type");
        }

        // ===== if? 空值回退（S7f，SYNTAX §3.4）=====
        private static void TestNullFallback()
        {
            TestHarness.Section("P3 Null Fallback (if?)");

            var (unit, bodies) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): User { return u if? new User(\"anon\") }\n");
            CheckNoErrors("无诊断（if? 回退）", unit);
            TestHarness.Check("if? 绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [], [Return(NullFallback(Param(u,User?), " +
                "New(User, init, [Str(\"anon\",String)]), User))])");

            // 组合：`?.` 与 if?（name?.x if? fallback 形态）
            var (unit2, bodies2) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): String { return u?.name if? \"anon\" }\n");
            CheckNoErrors("无诊断（?. + if? 组合）", unit2);
            TestHarness.Check("组合绑定形态", BoundDescribe.Body(BodyOf(bodies2, "f")),
                "Body(f, [], [Return(NullFallback(SafeAccess(Param(u,User?), " +
                "InstField(name, SafeReceiver(User), String), String?), " +
                "Str(\"anon\",String), String))])");

            // 诊断：左操作数非可空
            var (unit3, _) = BindUnit(
                "func f(s: String): String { return s if? \"x\" }\n");
            TestHarness.CheckSemanticError("左操作数非可空拒绝", unit3.Diagnostics,
                "Operator 'if?' requires a nullable left operand");

            // 诊断：回退值类型不兼容
            var (unit4, _) = BindUnit(
                "class User { pub var name: String\n    pub init(_ -> name) { } }\n" +
                "func f(u: User?): User { return u if? 42 }\n");
            TestHarness.CheckSemanticError("回退值类型不兼容", unit4.Diagnostics,
                "Null fallback must be assignable to 'User'");
        }

        // ===== 解构声明（S7f，SYNTAX §18）=====
        private static void TestDestructuring()
        {
            TestHarness.Section("P3 Destructuring");

            // 闭环：Pair 子类构造 + 解构（基类泛型字段初始化走替换）
            var (unit, bodies) = BindUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n" +
                "        key = k\n" +
                "        value = v\n" +
                "    }\n" +
                "}\n" +
                "func f() {\n" +
                "    var (k, v) = new Entry(\"a\", 1)\n" +
                "}\n");
            CheckNoErrors("无诊断（解构闭环）", unit);
            TestHarness.Check("解构绑定形态", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [k: String, v: i32], [Destructuring([k: String ← key; v: i32 ← value], " +
                "New(Entry, init, [Str(\"a\",String), Int(1,i32)]))])");
            // 基类泛型字段（TKey/TValue）在子类 init 里按构造实参替换
            var entryInit = bodies.Single(b => b.Method.Name == "init"
                && b.Method.Owner?.Name == "Entry");
            TestHarness.Check("init 内基类字段替换赋值", BoundDescribe.Body(entryInit),
                "Body(init, [], [Assign(InstField(key, This(Entry), String), Param(k,String)); " +
                "Assign(InstField(value, This(Entry), i32), Param(v,i32))])");

            // 诊断：初始化器非 Pair 子类
            var (unit2, _) = BindUnitWithStdlib("func f() {\n    var (a, b) = 42\n}\n");
            TestHarness.CheckSemanticError("非 Pair 子类拒绝", unit2.Diagnostics,
                "Destructuring requires a subtype of core.Pair");

            // 诊断：名字数不等于 2
            var (unit3, _) = BindUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "func f() {\n    var (a, b, c) = new Entry(\"a\", 1)\n}\n");
            TestHarness.CheckSemanticError("名字数拒绝", unit3.Diagnostics,
                "requires exactly 2 names");

            // 诊断：名字重复
            var (unit4, _) = BindUnitWithStdlib(
                "class Entry : core.Pair\\<String, i32> {\n" +
                "    pub init(k: String, v: i32) {\n        key = k\n        value = v\n    }\n" +
                "}\n" +
                "func f() {\n    var (k, k) = new Entry(\"a\", 1)\n}\n");
            TestHarness.CheckSemanticError("名字重复拒绝", unit4.Diagnostics,
                "Duplicate local variable 'k'");
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
    }
}
