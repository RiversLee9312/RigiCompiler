namespace RigiCompiler.Tests
{
    public static partial class BinderTests
    {
        // ===== if 语句（S7b，含 else if 链包装与 GuaranteesReturn 双分支升级）=====
        private static void TestIfStatements()
        {
            CompilerTestTools.Section("P3 If Statements");

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
            CaseAssertions.Check("双分支 if 语句", BoundDescribe.Body(BodyOf(bodies, "f")),
                "Body(f, [x: i32], [Decl(x, i32, = Int(0,i32)); " +
                "If(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Assign(Local(x,i32), Int(1,i32))], [Assign(Local(x,i32), Int(2,i32))]); " +
                "Return(Local(x,i32))])");
            CaseAssertions.Check("无 else if 语句", BoundDescribe.Body(BodyOf(bodies, "h")),
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
            CaseAssertions.Check("else if 链包装形态", BoundDescribe.Body(BodyOf(bodies2, "g")),
                "Body(g, [], [If(Binary(CmpGt, Param(a,i32), Int(0,i32), bool), " +
                "[Return(Int(1,i32))], " +
                "[If(Binary(CmpEq, Param(a,i32), Int(0,i32), bool), " +
                "[Return(Int(0,i32))], [Return(Int(-1,i32))])])])");

            // 结构性事实：else if 包装块是单语句 BoundBlock，且语句即 BoundIfStatement
            var outerIf = (BoundIfStatement)BodyOf(bodies2, "g").Body.Statements[0];
            CaseAssertions.CheckTrue("else if 链包成单语句 BoundBlock",
                outerIf.FalseBlock != null && outerIf.FalseBlock.Statements.Count == 1
                && outerIf.FalseBlock.Statements[0] is BoundIfStatement);

            // 单分支 return → GuaranteesReturn 不通过
            var (unit3, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    if (a > 0) { return 1 }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("单分支 return 不保证返回", unit3.Diagnostics,
                "must return a value on all code paths");

            var (unit4, _) = BindUnit("func f(x: i32) { if (x) { } }\n");
            CaseAssertions.CheckSemanticError("条件非 bool", unit4.Diagnostics,
                "if condition must be bool (got 'i32')");
        }

        // ===== if 表达式（S7b：值块隐式取值 / 显式 return@ / named 标签穿透）=====
        private static void TestIfExpressions()
        {
            CompilerTestTools.Section("P3 If Expressions");

            // 隐式取值（单表达式分支）
            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    return if (a > 0) { a } else { -a }\n" +
                "}\n");
            CheckNoErrors("无诊断（隐式取值）", unit);
            CaseAssertions.Check("隐式取值 if 表达式", BoundDescribe.Body(BodyOf(bodies, "f")),
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
            CaseAssertions.Check("显式 return@_ 值块", BoundDescribe.Body(BodyOf(bodies2, "f")),
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
            CaseAssertions.Check("named 标签值块", BoundDescribe.Body(BodyOf(bodies3, "f")),
                "Body(f, [], [Return(IfExpr(Binary(CmpGt, Param(x,i32), Int(0,i32), bool), " +
                "ValueBlock(pos, i32, [ReturnValue(pos, Int(1,i32))]), " +
                "ValueBlock(pos, i32, [ReturnValue(pos, Int(0,i32))]), i32))])");
            // 结构性事实：return@ 经引用命中所属值块
            var namedIf = (BoundIfExpression)((BoundReturnStatement)
                BodyOf(bodies3, "f").Body.Statements[0]).Value!;
            CaseAssertions.CheckTrue("return@pos 引用命中真分支值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    namedIf.TrueBranch.Block.Statements[0]).Target, namedIf.TrueBranch));
            CaseAssertions.CheckTrue("return@pos 引用命中假分支值块",
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
            CaseAssertions.Check("穿透外层标签形态", BoundDescribe.Body(BodyOf(bodies4, "f")),
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
            CaseAssertions.CheckTrue("穿透 return@outer 引用命中外层值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    innerIfExpr.TrueBranch.Block.Statements[0]).Target, outerIfExpr.TrueBranch));
            CaseAssertions.CheckTrue("内层 return@_ 引用命中内层值块",
                ReferenceEquals(((BoundReturnValueStatement)
                    innerIfExpr.FalseBranch.Block.Statements[0]).Target, innerIfExpr.FalseBranch));

            // 未定义标签
            var (unit5, _) = BindUnit(
                "func f(): i32 {\n" +
                "    var r = if (1 == 1) { return@nosuch 1 } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("未定义值块标签", unit5.Diagnostics,
                "Undefined value block label: 'nosuch'");

            // 多语句分支块尾缺 return@
            var (unit6, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 1\ny = 2 } else { 0 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("块尾缺 return@", unit6.Diagnostics,
                "must explicitly return@ a value");

            // 末语句 if 仅单分支 return@ → 路径不全覆盖
            var (unit7, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 0\nif (x > 1) { return@_ 1 } else { y = 2 } } " +
                "else { 0 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("分支内路径未全显式 return@", unit7.Diagnostics,
                "must explicitly return@ a value");

            // 分支类型不一致（两分支各产不同类型）
            var (unit8, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { 1 } else { \"s\" }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("分支类型不一致", unit8.Diagnostics,
                "if expression branches produce different types ('i32' and 'String')");

            // 同一块内多个 return@ 类型不一致
            var (unit9, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 0\nif (x > 1) { return@_ \"a\" } " +
                "else { return@_ 1 } } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("同块 return@ 类型不一致", unit9.Diagnostics,
                "if expression branch produces different types ('String' and 'i32')");

            // 两分支落穿无产值 → if 表达式无产值（旧口径：非全逃逸形态
            // 维持 must produce a value）
            var (unit10, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { var y = 1\ny = 2 } else { var z = 3\nz = 4 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("两分支落穿无产值", unit10.Diagnostics,
                "if expression must produce a value");

            // 两分支全逃逸但无期望类型（var 无标注 → expectedType 缺失）：
            // 逃逸形态的新报错口径（与 seq 对齐）
            var (unit10b, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) named o {\n" +
                "        return@o if (x > 1) { return@o 1 } else { return@o 2 }\n" +
                "    } else { 0 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("全逃逸无期望类型", unit10b.Diagnostics,
                "if expression escapes on all paths without producing a value");

            var (unit11, _) = BindUnit(
                "func f(x: i32): i32 {\n" +
                "    var r = if (x) { 1 } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("if 表达式条件非 bool", unit11.Diagnostics,
                "if condition must be bool (got 'i32')");

            // void 调用单语句分支无值可取
            var (unit12, _) = BindUnit(
                "func v() { }\n" +
                "func f(x: i32): i32 {\n" +
                "    var r = if (x > 0) { v() } else { 2 }\n" +
                "    return r\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("void 调用分支无产值", unit12.Diagnostics,
                "has no result");

            TestIfExpressionExpectedTypes();
        }

        // expectedType 传入隐式值块 + 有语境时按可赋性统一分支
        private static void TestIfExpressionExpectedTypes()
        {
            CompilerTestTools.Section("P3 If Expression Expected Types");

            const string enumSource =
                "pub enum struct E {\n" +
                "    pub const v: i32\n" +
                "    pub init(_ -> v)\n" +
                "}[\n" +
                "    A(v = _),\n" +
                "    B(0)\n" +
                "]\n";
            const string animalSource =
                "open class Animal { }\n" +
                "class Dog : Animal { }\n" +
                "class Cat : Animal { }\n";

            var (enumUnit, enumBodies) = BindUnit(enumSource +
                "func f(flag: bool): E {\n" +
                "    return if (flag) { .A(1) } else { .B }\n" +
                "}\n");
            CheckNoErrors("隐式 enum case 有返回语境", enumUnit);
            CaseAssertions.Check("隐式 enum if 形态",
                BoundDescribe.Body(BodyOf(enumBodies, "f")),
                "Body(f, [], [Return(IfExpr(Param(flag,bool), " +
                "ValueBlock(_, E, implicit, [ExprStmt(EnumCase(E.A, [Int(1,i32)]))]), " +
                "ValueBlock(_, E, implicit, [ExprStmt(EnumCase(E.B, []))]), E))])");

            var (nullUnit, nullBodies) = BindUnit(
                "func f(flag: bool): String? {\n" +
                "    return if (flag) { null } else { \"x\" }\n" +
                "}\n");
            CheckNoErrors("隐式 null 有可空返回语境", nullUnit);
            CaseAssertions.Check("隐式 null if 形态",
                BoundDescribe.Body(BodyOf(nullBodies, "f")),
                "Body(f, [], [Return(IfExpr(Param(flag,bool), " +
                "ValueBlock(_, String?, implicit, [ExprStmt(Null(String?))]), " +
                "ValueBlock(_, String, implicit, [ExprStmt(Str(\"x\",String))]), String?))])");

            var (explicitUnit, explicitBodies) = BindUnit(animalSource +
                "func pick(flag: bool): Animal {\n" +
                "    return if (flag) { return@_ new Dog() } else { return@_ new Cat() }\n" +
                "}\n");
            CheckNoErrors("公共基类显式 return@", explicitUnit);
            CaseAssertions.Check("公共基类显式 if 形态",
                BoundDescribe.Body(BodyOf(explicitBodies, "pick")),
                "Body(pick, [], [Return(IfExpr(Param(flag,bool), " +
                "ValueBlock(_, Dog, [ReturnValue(_, New(Dog, []))]), " +
                "ValueBlock(_, Cat, [ReturnValue(_, New(Cat, []))]), Animal))])");

            var (implicitUnit, implicitBodies) = BindUnit(animalSource +
                "func pick(flag: bool): Animal {\n" +
                "    return if (flag) { new Dog() } else { new Cat() }\n" +
                "}\n");
            CheckNoErrors("公共基类隐式分支", implicitUnit);
            CaseAssertions.Check("公共基类隐式 if 形态",
                BoundDescribe.Body(BodyOf(implicitBodies, "pick")),
                "Body(pick, [], [Return(IfExpr(Param(flag,bool), " +
                "ValueBlock(_, Dog, implicit, [ExprStmt(New(Dog, []))]), " +
                "ValueBlock(_, Cat, implicit, [ExprStmt(New(Cat, []))]), Animal))])");

            var (noExpected, _) = BindUnit(animalSource +
                "func f(flag: bool) {\n" +
                "    var x = if (flag) { new Dog() } else { new Cat() }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("无期望类型仍严格同型", noExpected.Diagnostics,
                "if expression branches produce different types ('Dog' and 'Cat')");

            var (incompatible, _) = BindUnit(
                "func f(flag: bool): i32 {\n" +
                "    return if (flag) { 1 } else { \"s\" }\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("有期望类型但不兼容报可赋性", incompatible.Diagnostics,
                "if expression branch type 'String' is not assignable to expected type 'i32'");
        }

        // ===== definite assignment 分支合并（S7b：before ∪ (setT ∩ setF)）=====
        private static void TestBranchDefiniteAssignment()
        {
            CompilerTestTools.Section("P3 Branch Definite Assignment");

            // 双分支都赋值 → 合并后可用
            var (unit, bodies) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    if (a > 0) { x = 1 } else { x = 2 }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("双分支赋值后可用", unit);
            CaseAssertions.Check("双分支赋值形态", BoundDescribe.Body(BodyOf(bodies, "f")),
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
            CaseAssertions.CheckSemanticError("仅单分支赋值报未赋值", unit2.Diagnostics,
                "Use of unassigned local variable 'x'");

            // else 分支不赋值 → 交集不含
            var (unit3, _) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    if (a > 0) { x = 1 } else { a = 2 }\n" +
                "    return x\n" +
                "}\n");
            CaseAssertions.CheckSemanticError("else 分支未赋值报未赋值", unit3.Diagnostics,
                "Use of unassigned local variable 'x'");

            // if 表达式形态同规则：两值块内赋值合并后可用
            var (unit4, bodies4) = BindUnit(
                "func f(a: i32): i32 {\n" +
                "    var x: i32\n" +
                "    var r = if (a > 0) { x = 1\nreturn@_ x } else { x = 2\nreturn@_ x }\n" +
                "    return x\n" +
                "}\n");
            CheckNoErrors("if 表达式值块内赋值合并", unit4);
            CaseAssertions.Check("值块内赋值形态", BoundDescribe.Body(BodyOf(bodies4, "f")),
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
            CaseAssertions.CheckSemanticError("分支内声明分支后不可见", unit5.Diagnostics,
                "Undefined name: 'y'");
        }
    }
}
