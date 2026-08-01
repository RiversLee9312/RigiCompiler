using System;

namespace LatteCompiler.Tests
{
    // 表达式解析测试：全管线驱动，断言 AST 树产物
    // （AstDescribe 精确描述串 + 结构事实），不断言控制台文本。
    // 覆盖：字面量/符号/一元/二元/括号分组与「无运算符优先级」错误、类型标注声明、
    // 调用/成员访问/索引/new/泛型调用与后缀错误、is/supers/with 与 as/as? 类型操作、
    // 前导点 enum case 引用（SYNTAX §12）、is 右侧 enum case（§12.3）、
    // wrapper 路径访问（SYNTAX §14.1）、位运算符（§13.2）、复合赋值（§13.2）、
    // 括号内续行（§1.1）、位运算优先级与 in 的 M31 错误、AST 结构断言。
    public class ExpressionParserTests
    {
        // ===== 1. 字面量初始化 =====
        public static void TestLiteralInitializers()
        {
            TestHarness.Section("Literal Initializers");

            TestExpr("var x = 42", "Int(42,I32)");
            TestExpr("var h = 0xFF", "Int(255,I32,hex)");
            TestExpr("var l = 100L", "Int(100,I64)");
            TestExpr("var f = 3.14", "Float(3.14)");
            TestExpr("var ff = 0.1f", "Float(0.1f)");
            TestExpr("var s = \"Hello\"", "Str(\"Hello\")");
            TestExpr("var b = true", "Bool(True)");

            TestHarness.Blank();
        }

        // ===== 2. 符号引用 =====
        public static void TestSymbolReferences()
        {
            TestHarness.Section("Symbol References");

            TestExpr("var y = x", "Path(x, [])");
            TestExpr("var z = obj.field.sub", "Path(obj, [.field, .sub])");

            TestHarness.Blank();
        }

        // ===== 3. 一元运算符 =====
        public static void TestUnaryExpressions()
        {
            TestHarness.Section("Unary Expressions");

            TestExpr("var a = -x", "Unary(- Path(x, []))");
            TestExpr("var b = not flag", "Unary(not Path(flag, []))");

            TestHarness.Blank();
        }

        // ===== 4. 二元运算符 =====
        public static void TestBinaryExpressions()
        {
            TestHarness.Section("Binary Expressions");

            TestExpr("var r = 1 + 2", "Binary(Int(1,I32) + Int(2,I32))");
            TestExpr("var c = a == b", "Binary(Path(a, []) == Path(b, []))");
            TestExpr("var lg = x and y", "Binary(Path(x, []) and Path(y, []))");

            TestHarness.Blank();
        }

        // ===== 5. 括号分组 =====
        public static void TestGroupExpressions()
        {
            TestHarness.Section("Group Expressions");

            TestExpr("var g = (42)", "Group(Int(42,I32))");
            TestExpr("var p = (1 + 2)", "Group(Binary(Int(1,I32) + Int(2,I32)))");
            TestExpr("var m = 1 + (2 * 3)",
                "Binary(Int(1,I32) + Group(Binary(Int(2,I32) * Int(3,I32))))");
            TestExpr("var n2 = ((1 + 2) * 3)",
                "Group(Binary(Group(Binary(Int(1,I32) + Int(2,I32))) * Int(3,I32)))");
            TestExpr("var u = -(x + y)", "Unary(- Group(Binary(Path(x, []) + Path(y, []))))");

            TestHarness.Blank();
        }

        // ===== 6. 错误用例 =====
        public static void TestErrorCases()
        {
            TestHarness.Section("Error Cases (expect ParserException)");

            // Latte 没有运算符优先级：未括号化的多个运算符必须报错
            TestHarness.CheckParseError("var e1 = 1 + 2 * 3",
                () => TestHarness.ParseRoot("var e1 = 1 + 2 * 3"), "没有运算符优先级");
            // 括号未闭合
            TestHarness.CheckParseError("var e2 = (1 + 2",
                () => TestHarness.ParseRoot("var e2 = (1 + 2"), "Unexpected end of file");
            // 连续一元运算符未加括号
            TestHarness.CheckParseError("var e3 = not not x",
                () => TestHarness.ParseRoot("var e3 = not not x"), "连续的一元运算符");
            // 一元与二元混用未加括号
            TestHarness.CheckParseError("var e4 = -x + y",
                () => TestHarness.ParseRoot("var e4 = -x + y"), "没有运算符优先级");
            // 运算符后缺少右操作数
            TestHarness.CheckParseError("var e5 = 1 +",
                () => TestHarness.ParseRoot("var e5 = 1 +"), "Unexpected end of file");

            TestHarness.Blank();
        }

        // ===== 7. 类型标注 + 初始化 =====
        public static void TestTypedDeclarationsWithInit()
        {
            TestHarness.Section("Typed Declarations with Initializer");

            TestVarDecl("var x: i32 = 42", "var x: i32 = Int(42,I32)");
            TestVarDecl("const name: String = \"Hi\"", "const name: String = Str(\"Hi\")");
            TestVarDecl("var list: List\\<String> = null", "var list: List<String> = Null");
            TestVarDecl("var n: i32? = null", "var n: i32? = Null");
            // 回归：无初始化时 Initializer 必须为 null
            TestVarDecl("var count: i64", "var count: i64");

            TestHarness.Blank();
        }

        // ===== 8. 调用表达式 =====
        public static void TestCallExpressions()
        {
            TestHarness.Section("Call Expressions");

            TestExpr("var v = foo()", "Path(foo(), [])");
            TestExpr("var v = foo(1)", "Path(foo(Int(1,I32)), [])");
            TestExpr("var v = foo(1, x)", "Path(foo(Int(1,I32), Path(x, [])), [])");
            // 具名实参
            TestExpr("var v = foo(name = 1)", "Path(foo(name:Int(1,I32)), [])");
            // 具名判别：name 后不是 = 时是位置实参，且表达式继续
            TestExpr("var v = foo(name + 1)", "Path(foo(Binary(Path(name, []) + Int(1,I32))), [])");
            // 位置与具名混合
            TestExpr("var v = foo(1, name = 2)", "Path(foo(Int(1,I32), name:Int(2,I32)), [])");
            // 括号表达式作实参
            TestExpr("var v = foo((1 + 2), 3)",
                "Path(foo(Group(Binary(Int(1,I32) + Int(2,I32))), Int(3,I32)), [])");

            TestHarness.Blank();
        }

        // ===== 9. 成员访问与链式 =====
        public static void TestMemberAccessChains()
        {
            TestHarness.Section("Member Access Chains");

            // 调用结果上的成员访问
            TestExpr("var v = foo().bar", "Path(foo(), [.bar])");
            // 成员访问后再调用
            TestExpr("var v = foo().bar()", "Path(foo(), [.bar()])");
            // 纯符号路径保持 Symbol 形式（不产生 MemberAccess）
            TestExpr("var v = a.b.c", "Path(a, [.b, .c])");
            // 安全访问
            TestExpr("var v = obj?.field", "Path(obj, [?.field])");
            // if? 空值回退（S7f，SYNTAX §3.4：中缀 if + ? 重组为二元运算符）
            TestExpr("var v = a if? b", "Binary(Path(a, []) if? Path(b, []))");
            TestExpr("var v = obj?.field if? 0",
                "Binary(Path(obj, [?.field]) if? Int(0,I32))");

            TestHarness.Blank();
        }

        // ===== 9b. if? 重组与优先级错误（S7f）=====
        public static void TestNullFallbackParsing()
        {
            TestHarness.Section("Null Fallback Parsing (if?)");

            // 无优先级：a if? b if? c 必须加括号（右操作数层禁二元）
            TestHarness.CheckParseError("a if? b if? c",
                () => TestHarness.ParseFirstDecl("var v = a if? b if? c"),
                "必须用括号明确运算顺序");
            // 中缀 if 后必须是 ?
            TestHarness.CheckParseError("a if b",
                () => TestHarness.ParseFirstDecl("var v = a if b"),
                "Expected '?' after 'if'");

            TestHarness.Blank();
        }

        // ===== 10. 索引表达式 =====
        public static void TestIndexExpressions()
        {
            TestHarness.Section("Index Expressions");

            TestExpr("var v = a[0]", "Path(a[Int(0,I32)], [])");
            TestExpr("var v = a[i + 1]", "Path(a[Binary(Path(i, []) + Int(1,I32))], [])");
            // 索引结果再调用
            TestExpr("var v = a[0](1)", "Path(a[Int(0,I32)](Int(1,I32)), [])");
            // 调用结果再索引
            TestExpr("var v = foo()[0]", "Path(foo()[Int(0,I32)], [])");

            TestHarness.Blank();
        }

        // ===== 11. new 表达式 =====
        public static void TestNewExpressions()
        {
            TestHarness.Section("New Expressions");

            TestExpr("var v = new File(\"./x\")", "New(File, [Str(\"./x\")])");
            TestExpr("var v = new User(id = 42)", "New(User, [id:Int(42,I32)])");
            TestExpr("var v = new List\\<i32>()", "New(List<i32>, [])");

            TestHarness.Blank();
        }

        // ===== 12. 泛型调用 =====
        public static void TestGenericCallExpressions()
        {
            TestHarness.Section("Generic Call Expressions");

            TestExpr("var v = foo\\<i32>(1)", "Path(foo<i32>(Int(1,I32)), [])");
            TestExpr("var v = a.b\\<i32>(x)", "Path(a, [.b<i32>(Path(x, []))])");
            // 底座是表达式的泛型成员调用
            TestExpr("var v = foo().bar\\<i32>(x)",
                "Path(foo(), [.bar<i32>(Path(x, []))])");

            TestHarness.Blank();
        }

        // ===== 13. 后缀链错误用例 =====
        public static void TestSuffixErrorCases()
        {
            TestHarness.Section("Suffix Error Cases (expect ParserException)");

            // 调用参数未闭合
            TestHarness.CheckParseError("var v = foo(1",
                () => TestHarness.ParseRoot("var v = foo(1"), "Unexpected end of file");
            // 索引未闭合
            TestHarness.CheckParseError("var v = a[1",
                () => TestHarness.ParseRoot("var v = a[1"), "Unexpected end of file");
            // ? 后缺少 .
            TestHarness.CheckParseError("var v = a?b",
                () => TestHarness.ParseRoot("var v = a?b"), "Expected '.' after '?'");
            // 实参前多余逗号
            TestHarness.CheckParseError("var v = foo(, 1)",
                () => TestHarness.ParseRoot("var v = foo(, 1)"), "Unexpected ',' before argument");

            TestHarness.Blank();
        }

        // ===== 14. 类型检查与转换（is/supers/with、as/as?） =====
        public static void TestTypeOperators()
        {
            TestHarness.Section("Type Operators (is/supers/with, as/as?)");

            TestExpr("var v = obj is String", "Check(Path(obj, []) is String)");
            TestExpr("var v = obj supers Animal", "Check(Path(obj, []) supers Animal)");
            TestExpr("var v = obj with Serializable", "Check(Path(obj, []) with Serializable)");
            // is 右侧也可以是 Type\<T> 值（词法上统一按类型引用解析，SYNTAX §3.7）
            TestExpr("var v = obj is t", "Check(Path(obj, []) is t)");
            TestExpr("var v = obj as String", "Cast(Path(obj, []) as String)");
            TestExpr("var v = obj as? String", "Cast(Path(obj, []) as? String)");
            // 泛型与可空目标类型
            TestExpr("var v = obj as List\\<i32>", "Cast(Path(obj, []) as List<i32>)");
            TestExpr("var v = obj as String?", "Cast(Path(obj, []) as String?)");
            // 后缀链之后再做类型操作
            TestExpr("var v = foo().bar as String", "Cast(Path(foo(), [.bar]) as String)");
            // 括号化之后可继续参与运算
            TestExpr("var v = (obj as String) + x",
                "Binary(Group(Cast(Path(obj, []) as String)) + Path(x, []))");

            TestHarness.Blank();
        }

        // ===== 15. 类型操作错误用例 =====
        public static void TestTypeOperatorErrorCases()
        {
            TestHarness.Section("Type Operator Error Cases (expect ParserException)");

            // as 后缺少类型
            TestHarness.CheckParseError("var e = obj as",
                () => TestHarness.ParseRoot("var e = obj as"), "Unexpected end of file");
            // ? 仅是 as 的安全转换标记，不能用于 is
            TestHarness.CheckParseError("var e = obj is? String",
                () => TestHarness.ParseRoot("var e = obj is? String"), "Unexpected '?' after type operator");
            // 类型操作后未加括号直接接二元运算符（无优先级规则）
            TestHarness.CheckParseError("var e = obj as String + x",
                () => TestHarness.ParseRoot("var e = obj as String + x"), "没有运算符优先级");

            TestHarness.Blank();
        }

        // ===== 16. 前导点 enum case 引用（SYNTAX §12，P5）=====
        public static void TestEnumCaseReferences()
        {
            TestHarness.Section("Enum Case References");

            // 固定 case 引用（类型上下文由语义阶段校验，解析期只识别形态）
            TestExpr("var r = .Success", "EnumCase(.Success)");
            // 参数化 case 的调用：由后缀链自然脱糖为 Call
            TestExpr("var f = .Failed(404)", "Path((EnumCase(.Failed))(Int(404,I32)), [])");
            TestExpr("var n = .Failed(errorCode = 404)",
                "Path((EnumCase(.Failed))(errorCode:Int(404,I32)), [])");
            // 注解实参形态（@WrapperTarget(.Entity) 的同构表达式）
            TestExpr("var t = .Entity", "EnumCase(.Entity)");

            TestHarness.Blank();
        }

        // ===== 17. wrapper 路径访问（SYNTAX §14.1，P5）=====
        public static void TestWrapperAccess()
        {
            TestHarness.Section("Wrapper Access");

            // 基本形态：obj:Wrapper
            TestExpr("var w = service:Logged", "Path(service, [:Logged])");
            // 链式：obj:A:B 左结合（"obj 的修饰器 A 的修饰器 B"）
            TestExpr("var w = obj:A:B", "Path(obj, [:A, :B])");
            // 规范 §3 的完整路径示例：wrapper 访问在整条路径末尾
            TestExpr("var l = foo().bar[0]?.length:MyWrapper",
                "Path(foo(), [.bar[Int(0,I32)], ?.length, :MyWrapper])");
            // wrapper 访问后仍可继续成员后缀
            TestExpr("var t = service:Logged.level",
                "Path(service, [:Logged, .level])");

            TestHarness.Blank();
        }

        // ===== 18. 位运算符（SYNTAX §13.2，M31）=====
        public static void TestBitwiseOperators()
        {
            TestHarness.Section("Bitwise Operators");

            // << 由 Lexer 合并；>>、>>> 由运算符状态重组（顺带回归）；& | ^ 为单字符
            TestExpr("var s = (a << 2)", "Group(Binary(Path(a, []) << Int(2,I32)))");
            TestExpr("var s = (a >> 2)", "Group(Binary(Path(a, []) >> Int(2,I32)))");
            TestExpr("var s = (a >>> 2)", "Group(Binary(Path(a, []) >>> Int(2,I32)))");
            TestExpr("var s = (a & b)", "Group(Binary(Path(a, []) & Path(b, [])))");
            TestExpr("var s = (a | b)", "Group(Binary(Path(a, []) | Path(b, [])))");
            TestExpr("var s = (a ^ b)", "Group(Binary(Path(a, []) ^ Path(b, [])))");

            TestHarness.Blank();
        }

        // ===== 19. 续行（SYNTAX §1.1：() / [] 未闭合时换行按空白处理，M31）=====
        public static void TestLineContinuation()
        {
            TestHarness.Section("Line Continuation Inside Brackets");

            // 分组内运算符前后都可换行
            TestExprEscaped("var v = (1 +\n2)", "Group(Binary(Int(1,I32) + Int(2,I32)))");
            TestExprEscaped("var v = (\n1 + 2\n)", "Group(Binary(Int(1,I32) + Int(2,I32)))");
            // 调用实参表内逗号后续行
            TestExprEscaped("var v = foo(1,\n2)", "Path(foo(Int(1,I32), Int(2,I32)), [])");
            // 索引实参表内逗号后续行
            TestExprEscaped("var a = arr[0,\n1]", "Path(arr[Int(0,I32), Int(1,I32)], [])");

            TestHarness.Blank();
        }

        // ===== 20. M31 错误用例（位运算优先级 / in）=====
        public static void TestM31ErrorCases()
        {
            TestHarness.Section("M31 Error Cases (expect ParserException)");

            // 位运算同样遵守「没有运算符优先级」：未括号化的连续位运算必须报错
            TestHarness.CheckParseError("var s = a & b & c",
                () => TestHarness.ParseRoot("var s = a & b & c"), "没有运算符优先级");
            // in 只属于 for 循环头，不是二元运算符（M31 移除）
            TestHarness.CheckParseError("var x = a in b",
                () => TestHarness.ParseRoot("var x = a in b"), "Unexpected token after initializer");

            TestHarness.Blank();
        }

        // ===== 21. 复合赋值（SYNTAX §13.2）=====
        public static void TestCompoundAssignments()
        {
            TestHarness.Section("Compound Assignments (§13.2)");

            // 全集 10 个运算符（语句位置：ExpressionStatement 包装，无 %=）
            TestBlock("{ a += 1 }", "[CompoundAssign(Path(a, []) += Int(1,I32))]");
            TestBlock("{ a -= 1 }", "[CompoundAssign(Path(a, []) -= Int(1,I32))]");
            TestBlock("{ a *= 2 }", "[CompoundAssign(Path(a, []) *= Int(2,I32))]");
            TestBlock("{ a /= 2 }", "[CompoundAssign(Path(a, []) /= Int(2,I32))]");
            TestBlock("{ a <<= 1 }", "[CompoundAssign(Path(a, []) <<= Int(1,I32))]");
            TestBlock("{ a >>= 1 }", "[CompoundAssign(Path(a, []) >>= Int(1,I32))]");
            // >>>= 是复合赋值而非比较：token 流 > > > = 经重组收拢为 >>> 后遇 = 分流
            TestBlock("{ a >>>= 1 }", "[CompoundAssign(Path(a, []) >>>= Int(1,I32))]");
            TestBlock("{ a &= b }", "[CompoundAssign(Path(a, []) &= Path(b, []))]");
            TestBlock("{ a |= b }", "[CompoundAssign(Path(a, []) |= Path(b, []))]");
            TestBlock("{ a ^= b }", "[CompoundAssign(Path(a, []) ^= Path(b, []))]");
            // 右操作数照常经 ExpressionRoot 委托解析（可为任意表达式）
            TestBlock("{ a += (b + 1) }",
                "[CompoundAssign(Path(a, []) += Group(Binary(Path(b, []) + Int(1,I32))))]");
            // 复合赋值整体是表达式节点：可出现在表达式位置
            TestExpr("var v = (a += 1)", "Group(CompoundAssign(Path(a, []) += Int(1,I32)))");
            // 区分：>= 保持比较语义（>>> 比较回归见 TestBitwiseOperators）
            TestExpr("var s = (a >= b)", "Group(Binary(Path(a, []) >= Path(b, [])))");

            // 结构断言：节点类型、Operator 字符串、Target/Value 内容与 Parent 链
            var block = TestHarness.ParseBlock("{ count += 42 }");
            var stmt = (ExpressionStatementASTNode)block.Statements[0];
            var compound = (CompoundAssignmentExpressionASTNode)stmt.Expression.Expression;
            TestHarness.CheckTrue("{ count += 42 }: Operator 为 +", compound.Operator == "+");
            TestHarness.CheckTrue("{ count += 42 }: Target 已填充且为路径",
                compound.Target.IsAttached &&
                compound.Target.Expression is PathExpressionASTNode);
            TestHarness.CheckTrue("{ count += 42 }: Value 已填充且为字面量",
                compound.Value.IsAttached &&
                compound.Value.Expression is LiteralExpressionASTNode);
            TestHarness.CheckTrue("{ count += 42 }: Target/Value Root 的 Parent 指向节点",
                ReferenceEquals(compound.Target.Parent, compound) &&
                ReferenceEquals(compound.Value.Parent, compound));
            TestHarness.CheckTrue("{ count += 42 }: Target 表达式的 Parent 指向 Target Root",
                ReferenceEquals(compound.Target.Expression.Parent, compound.Target));

            // 负例：不属于全集的组合不误判——== 是既有比较，
            // 再遇 = 落入正常二元流程，由右操作数层报意外 token
            TestHarness.CheckParseError("{ a == = b }",
                () => TestHarness.ParseBlock("{ a == = b }"), "Unexpected token at start of expression");

            TestHarness.Blank();
        }

        // ===== 22. is 右侧 enum case（SYNTAX §12.3）=====
        public static void TestIsEnumCase()
        {
            TestHarness.Section("is with Enum Case (§12.3)");

            // 正例：is 右侧前导点 enum case（TargetCase 槽，与 TargetType 互斥）
            TestExpr("var v = result is .Failed", "Check(Path(result, []) is EnumCase(.Failed))");
            // switch 模式匹配走同一条 is 路径（§12.3 规范示例形态）
            TestExpr("var r = switch(n) { (_ is .Success) -> { \"ok\" } default -> { \"?\" } }",
                "Switch(Path(n, []), [Check(Path(_, []) is EnumCase(.Success)) -> [Str(\"ok\")]], " +
                "default -> [Str(\"?\")])");

            // 结构断言：双字段互斥、CaseName、Parent 链
            var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var v = result is .Failed");
            var check = (TypeCheckExpressionASTNode)decl.Initializer!.Expression;
            TestHarness.CheckTrue("result is .Failed: Operator 为 is", check.Operator == "is");
            TestHarness.CheckTrue("result is .Failed: TargetCase 非空、TargetType 为空（互斥）",
                check.TargetCase != null && check.TargetType == null);
            TestHarness.CheckTrue("result is .Failed: CaseName",
                check.TargetCase!.CaseName == "Failed");
            TestHarness.CheckTrue("result is .Failed: TargetCase.Parent 指向 Check 节点",
                ReferenceEquals(check.TargetCase.Parent, check));
            TestHarness.CheckTrue("result is .Failed: Object Root 已填充", check.Object.IsAttached);

            // 负例：as/supers 右侧必须是类型，遇 . 维持报错
            TestHarness.CheckParseError("var e = obj as .Failed",
                () => TestHarness.ParseRoot("var e = obj as .Failed"), "Expected type name");
            TestHarness.CheckParseError("var e = obj supers .X",
                () => TestHarness.ParseRoot("var e = obj supers .X"), "Expected type name");

            TestHarness.Blank();
        }

        // ===== 23. AST 结构断言（字符串快照之外的结构性校验，AGENTS §5）=====
        public static void TestStructuralAssertions()
        {
            TestHarness.Section("Structural Assertions");

            // 用例 1：字面量初始化——Root 存在、已填充、Expression 类型、Parent 链
            var decl1 = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var x = 42");
            TestHarness.CheckTrue("var x = 42: Initializer Root 存在", decl1.Initializer is not null);
            var init1 = decl1.Initializer!;
            TestHarness.CheckTrue("var x = 42: Root 已填充", init1.IsAttached);
            TestHarness.CheckTrue("var x = 42: Expression 类型为 LiteralExpression",
                init1.Expression is LiteralExpressionASTNode);
            TestHarness.CheckTrue("var x = 42: Expression.Parent 指向 Root",
                ReferenceEquals(init1.Expression.Parent, init1));
            TestHarness.CheckTrue("var x = 42: Root.Parent 指向声明节点",
                ReferenceEquals(init1.Parent, decl1));
            TestHarness.CheckTrue("var x = 42: 声明的 Parent 是文件 Root", decl1.Parent is RootASTNode);

            // 用例 2：二元 + 分组——子 Root 均已填充、Parent 链正确
            var decl2 = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var m = 1 + (2 * 3)");
            var bin = (BinaryExpressionASTNode)decl2.Initializer!.Expression;
            TestHarness.CheckTrue("var m = 1 + (2 * 3): 二元左右 Root 均已填充",
                bin.Left.IsAttached && bin.Right.IsAttached);
            TestHarness.CheckTrue("var m = 1 + (2 * 3): Left 表达式的 Parent 指向 Left Root",
                ReferenceEquals(bin.Left.Expression.Parent, bin.Left));
            TestHarness.CheckTrue("var m = 1 + (2 * 3): Right 表达式的 Parent 指向 Right Root",
                ReferenceEquals(bin.Right.Expression.Parent, bin.Right));
            var group = (GroupExpressionASTNode)bin.Right.Expression;
            TestHarness.CheckTrue("var m = 1 + (2 * 3): 分组 InnerExpression 已填充",
                group.InnerExpression.IsAttached);
            TestHarness.CheckTrue("var m = 1 + (2 * 3): 分组内表达式的 Parent 指向内层 Root",
                ReferenceEquals(group.InnerExpression.Expression.Parent, group.InnerExpression));

            // 用例 3：调用链（M42 路径形态）——首段符号 + Call 后缀，
            // 实参 Value Root 均填充、节点无共享
            var decl3 = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var v = foo(1, name = 2)");
            var call = (PathExpressionASTNode)decl3.Initializer!.Expression;
            TestHarness.CheckTrue("var v = foo(1, name = 2): 首段符号名 foo", call.Head.Name == "foo");
            TestHarness.CheckTrue("var v = foo(1, name = 2): 一个 Call 后缀",
                call.Head.Suffixes.Count == 1 && call.Head.Suffixes[0].Kind == PathSuffixKind.Call);
            var args = call.Head.Suffixes[0].Arguments;
            TestHarness.CheckTrue("var v = foo(1, name = 2): 两个实参", args.Count == 2);
            TestHarness.CheckTrue("var v = foo(1, name = 2): 实参 Value Root 均已填充",
                args[0].Value.IsAttached && args[1].Value.IsAttached);
            TestHarness.CheckTrue("var v = foo(1, name = 2): 实参表达式不共享节点",
                !ReferenceEquals(args[0].Value.Expression, args[1].Value.Expression));
            TestHarness.CheckTrue("var v = foo(1, name = 2): 具名实参名",
                args[1].Name == "name");

            // 用例 4：无初始化——可选 Root 以 null 表示（禁止「非 null 但为空的 Root」）
            var decl4 = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl("var count: i64");
            TestHarness.CheckTrue("var count: i64: 无初始化时 Initializer 为 null Root",
                decl4.Initializer is null);

            TestHarness.Blank();
        }

        // 辅助：解析变量声明并比对初始化表达式的 AST 描述串
        private static void TestExpr(string code, string expectedDesc)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                TestHarness.Check(code, AstDescribe.Expr(decl.Initializer!.Expression), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        // 辅助：解析代码块并比对块内容的 AST 描述串
        private static void TestBlock(string code, string expectedDesc)
        {
            try
            {
                var block = TestHarness.ParseBlock(code);
                TestHarness.Check(code, AstDescribe.Block(block), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        // 辅助：解析变量声明并比对初始化表达式的 AST 描述串（label 中 \n 转义显示）
        private static void TestExprEscaped(string code, string expectedDesc)
        {
            try
            {
                var decl = (VariableDeclarationASTNode)TestHarness.ParseFirstDecl(code);
                TestHarness.Check(code.Replace("\n", "\\n"),
                    AstDescribe.Expr(decl.Initializer!.Expression), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        // 辅助：解析变量声明并比对完整声明的 AST 描述串
        private static void TestVarDecl(string code, string expectedDesc)
        {
            try
            {
                var node = TestHarness.ParseFirstDecl(code);
                TestHarness.Check(code, AstDescribe.Decl(node), expectedDesc);
            }
            catch (Exception ex)
            {
                TestHarness.CheckTrue($"{code} => 意外异常", false, ex.Message);
            }
        }

        public static int RunAll()
        {
            TestHarness.Reset();

            TestLiteralInitializers();
            TestSymbolReferences();
            TestUnaryExpressions();
            TestBinaryExpressions();
            TestGroupExpressions();
            TestErrorCases();
            TestTypedDeclarationsWithInit();
            TestCallExpressions();
            TestMemberAccessChains();
            TestNullFallbackParsing();
            TestIndexExpressions();
            TestNewExpressions();
            TestGenericCallExpressions();
            TestSuffixErrorCases();
            TestTypeOperators();
            TestTypeOperatorErrorCases();
            TestEnumCaseReferences();
            TestWrapperAccess();
            TestBitwiseOperators();
            TestLineContinuation();
            TestM31ErrorCases();
            TestCompoundAssignments();
            TestIsEnumCase();
            TestStructuralAssertions();

            return TestHarness.Summary("Expression");
        }
    }
}
