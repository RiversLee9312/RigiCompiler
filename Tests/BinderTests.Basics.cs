using System.Linq;

namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
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

        // ===== W5：全局/静态字段初值不得直接引用其它全局/静态字段 =====
        private static void TestGlobalFieldInitializerBan()
        {
            TestHarness.Section("P3 Global/Static Field Initializer Ban (§9.3 W5)");

            var (ok, _) = BindUnit(
                "var g: i32 = 42\n" +
                "const cg: i32 = 1\n" +
                "pub class Holder {\n" +
                "    pub static var s: i32 = 100\n" +
                "}\n" +
                "func f(): i32 { return g }\n");
            CheckNoErrors("合法字面量初值放行", ok);

            var (viaFn, _) = BindUnit(
                "var b: i32 = 1\n" +
                "func readB(): i32 { return b }\n" +
                "var a: i32 = readB()\n" +
                "func f(): i32 { return a }\n");
            CheckNoErrors("函数引用放行（逃逸口）", viaFn);

            var (inst, _) = BindUnit(
                "var g: i32 = 1\n" +
                "class C {\n" +
                "    pub var x: i32 = g\n" +
                "    pub init()\n" +
                "}\n" +
                "func f(): i32 { return 0 }\n");
            CheckNoErrors("实例字段初值引用全局放行", inst);

            var (cross, _) = BindUnit(
                "var a: i32 = (b + 1)\n" +
                "var b: i32 = 1\n" +
                "func f(): i32 { return a }\n");
            TestHarness.CheckSemanticError("相互/后向引用报错", cross.Diagnostics,
                "cannot reference global/static field 'b'");

            var (fwd, _) = BindUnit(
                "var a: i32 = 1\n" +
                "var b: i32 = (a + 1)\n" +
                "func f(): i32 { return b }\n");
            TestHarness.CheckSemanticError("前向引用同样报错", fwd.Diagnostics,
                "cannot reference global/static field 'a'");

            var (self, _) = BindUnit(
                "var a: i32 = a\n" +
                "func f(): i32 { return a }\n");
            TestHarness.CheckSemanticError("自引用报错", self.Diagnostics,
                "cannot reference global/static field 'a'");

            var (cst, _) = BindUnit(
                "const c: i32 = 1\n" +
                "var a: i32 = c\n" +
                "func f(): i32 { return a }\n");
            TestHarness.CheckSemanticError("引用 const 全局同样报错", cst.Diagnostics,
                "cannot reference global/static field 'c'");

            var (st, _) = BindUnit(
                "pub class Holder {\n" +
                "    pub static var s: i32 = 1\n" +
                "    pub static var t: i32 = (s + 1)\n" +
                "}\n" +
                "func f(): i32 { return Holder.t }\n");
            TestHarness.CheckSemanticError("静态字段互引报错", st.Diagnostics,
                "cannot reference global/static field 'Holder.s'");

            var (stG, _) = BindUnit(
                "var g: i32 = 1\n" +
                "pub class Holder {\n" +
                "    pub static var s: i32 = g\n" +
                "}\n" +
                "func f(): i32 { return Holder.s }\n");
            TestHarness.CheckSemanticError("静态初值引用全局报错", stG.Diagnostics,
                "cannot reference global/static field 'g'");

            var (wrapped, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper W {\n" +
                "    pub init()\n" +
                "}\n" +
                "@W\n" +
                "var wg: i32 = 1\n" +
                "@W\n" +
                "var wh: i32 = wg\n" +
                "func f(): i32 { return wh }\n");
            TestHarness.CheckSemanticError("wrapped 全局初值互引报错", wrapped.Diagnostics,
                "cannot reference global/static field 'wg'");

            var (companion, _) = BindUnitWithStdlib(
                "@WrapperTarget(.Value)\n" +
                "pub shared wrapper W {\n" +
                "    pub init()\n" +
                "}\n" +
                "pub class Holder {\n" +
                "    @W\n" +
                "    pub static var s: i32 = 1\n" +
                "    @W\n" +
                "    pub static var t: i32 = s\n" +
                "}\n" +
                "func f(): i32 { return Holder.t }\n");
            TestHarness.CheckSemanticError("companion 静态初值互引报错", companion.Diagnostics,
                "cannot reference global/static field 'Holder.s'");
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

        // ===== 用户类型 ==/!=（SYNTAX §13.2：映射 operator equals，!= 由 equals
        // 取反推导；VM §22.3 cmp.eq/cmp.ne 按精确类型派发用户 equals）=====
        private static void TestUserEqualityOperators()
        {
            TestHarness.Section("P3 User Equality Operators (==/!=)");

            // 正例：用户类型定义 equals → == 绑 CmpEq、!= 绑 CmpNe（结果 bool）
            var (unit, bodies) = BindUnit(
                "class Vec { pub operator equals(other: Vec): bool { return true } }\n" +
                "func eq(a: Vec, b: Vec): bool { return (a == b) }\n" +
                "func ne(a: Vec, b: Vec): bool { return (a != b) }\n");
            CheckNoErrors("无诊断（用户 equals 的 ==/!=）", unit);
            TestHarness.Check("用户类型 ==", BoundDescribe.Body(BodyOf(bodies, "eq")),
                "Body(eq, [], [Return(Binary(CmpEq, Param(a,Vec), Param(b,Vec), bool))])");
            TestHarness.Check("用户类型 !=", BoundDescribe.Body(BodyOf(bodies, "ne")),
                "Body(ne, [], [Return(Binary(CmpNe, Param(a,Vec), Param(b,Vec), bool))])");

            // 泛型 equals（TAnother 形参）同样命中（按名 + 参数个数查找）
            var (unit2, bodies2) = BindUnit(
                "class Vec { pub operator equals\\<TAnother>(another: TAnother): bool { return true } }\n" +
                "func eq(a: Vec, b: Vec): bool { return (a == b) }\n");
            CheckNoErrors("无诊断（泛型 equals 的 ==）", unit2);
            TestHarness.Check("泛型 equals 用户类型 ==", BoundDescribe.Body(BodyOf(bodies2, "eq")),
                "Body(eq, [], [Return(Binary(CmpEq, Param(a,Vec), Param(b,Vec), bool))])");

            // 负例：未定义 equals 的用户类型保持编译错误（文档无默认相等语义）
            var (unit3, _) = BindUnit(
                "class Plain { }\nfunc eq(a: Plain, b: Plain): bool { return (a == b) }\n");
            TestHarness.CheckSemanticError("未定义 equals 的用户类型 ==", unit3.Diagnostics,
                "Operator '==' is not defined for type 'Plain'");

            // 回归：用户引用类型 null 判等仍走 S8b 特例（不要求 equals）
            var (unit4, bodies4) = BindUnit(
                "class Plain { }\nfunc f(d: Plain?): bool { return (d == null) }\n");
            CheckNoErrors("无诊断（用户类型 == null）", unit4);
            TestHarness.Check("用户类型 null 判等", BoundDescribe.Body(BodyOf(bodies4, "f")),
                "Body(f, [], [Return(Binary(CmpEq, Param(d,Plain?), Null(Plain?), bool))])");
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
                "Missing argument for parameter 'b'");

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

            // 命名空间路径调用（跨文件前向引用；§16.1 起跨文件引用需 pub）
            var (unit8, bodies8) = BindUnit(
                "namespace a.b\npub func g(): i32 { return 1 }\n",
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

        // ===== init 参数映射赋值合成（SYNTAX §9.3）=====
        private static void TestInitMappingSynthesis()
        {
            TestHarness.Section("P3 Init Mapping Synthesis");

            // 无体 init 带映射：合成体 = 逐映射参数（声明序）的字段赋值
            var (unit, bodies) = BindUnit(
                "class Point {\n" +
                "    pub var x: i32\n" +
                "    pub var y: String\n" +
                "    pub init(_ -> x, _ -> y)\n" +
                "}\n");
            CheckNoErrors("无诊断（无体 init 映射合成）", unit);
            TestHarness.Check("无体 init 合成映射赋值体",
                BoundDescribe.Body(BodyOf(bodies, "init")),
                "Body(init, [], [Assign(InstField(x, This(Point), i32), Param(x,i32)); " +
                "Assign(InstField(y, This(Point), String), Param(y,String))])");
            // 结构性事实：合成节点 Syntax 回指 init 声明节点 + MappedField 引用相等
            var point = unit.Symbols.GlobalNamespace.Types.Single(t => t.Name == "Point");
            var initBody = BodyOf(bodies, "init");
            var initDecl = point.Methods.Single(m => m.Kind == MethodKind.Init);
            var firstAssign = (BoundAssignmentStatement)initBody.Body.Statements[0];
            TestHarness.CheckTrue("合成节点 Syntax 回指 init 声明 + 字段符号引用相等",
                ReferenceEquals(firstAssign.Syntax, initBody.Body.Syntax)
                && firstAssign.Syntax is CallableDeclarationASTNode
                && ReferenceEquals(((BoundFieldAccessExpression)firstAssign.Target).Field,
                    initDecl.Parameters[0].MappedField));

            // 无体 init 无映射：合成空体（§21.2 fn 定义门槛；接收参数不做事）
            var (unit2, bodies2) = BindUnit(
                "class Token {\n    pub init(n: i32)\n}\n");
            CheckNoErrors("无诊断（无体无映射 init）", unit2);
            TestHarness.Check("无体无映射 init 合成空体",
                BoundDescribe.Body(BodyOf(bodies2, "init")), "Body(init, [], [])");

            // 有体 init 带映射：映射赋值前插用户体头部（用户体再写 = 覆盖）
            var (unit3, bodies3) = BindUnit(
                "class C {\n" +
                "    pub var x: i32\n" +
                "    pub var y: i32\n" +
                "    pub init(_ -> x, y0: i32) { y = y0 }\n" +
                "}\n");
            CheckNoErrors("无诊断（有体 init 映射前插）", unit3);
            TestHarness.Check("有体 init 映射赋值前插",
                BoundDescribe.Body(BodyOf(bodies3, "init")),
                "Body(init, [], [Assign(InstField(x, This(C), i32), Param(x,i32)); " +
                "Assign(InstField(y, This(C), i32), Param(y0,i32))])");

            // 显式名显式类型映射（horizontal: i32 -> x）同样合成
            var (unit4, bodies4) = BindUnit(
                "class D {\n" +
                "    pub var x: i32\n" +
                "    pub init(horizontal: i32 -> x)\n" +
                "}\n");
            CheckNoErrors("无诊断（显式名映射合成）", unit4);
            TestHarness.Check("显式名映射合成赋值体",
                BoundDescribe.Body(BodyOf(bodies4, "init")),
                "Body(init, [], [Assign(InstField(x, This(D), i32), Param(horizontal,i32))])");
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
    }
}
