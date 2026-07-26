using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 表达式解析测试
    ///
    /// 覆盖 P1 第一阶段（结果传递机制 + 表达式框架）：
    /// 1. 字面量初始化结果正确保存到 VariableDeclarationASTNode.Initializer
    /// 2. 符号引用表达式
    /// 3. 一元运算符表达式
    /// 4. 二元运算符表达式
    /// 5. 括号分组表达式（Latte 无运算符优先级，括号是唯一的组合方式）
    /// 6. 错误用例：违反"无运算符优先级"规则必须报 ParserException
    /// 7. 类型标注与初始化表达式同时正确保存
    /// </summary>
    public class ExpressionParserTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 字面量初始化 =====
        public static void TestLiteralInitializers()
        {
            Console.WriteLine("=== Testing Literal Initializers ===");

            TestExpr("var x = 42", "Int(42,I32)");
            TestExpr("var h = 0xFF", "Int(255,I32,hex)");
            TestExpr("var l = 100L", "Int(100,I64)");
            TestExpr("var f = 3.14", "Float(3.14)");
            TestExpr("var ff = 0.1f", "Float(0.1f)");
            TestExpr("var s = \"Hello\"", "Str(\"Hello\")");
            TestExpr("var b = true", "Bool(True)");

            Console.WriteLine();
        }

        // ===== 2. 符号引用 =====
        public static void TestSymbolReferences()
        {
            Console.WriteLine("=== Testing Symbol References ===");

            TestExpr("var y = x", "Sym(x)");
            TestExpr("var z = obj.field.sub", "Sym(obj.field.sub)");

            Console.WriteLine();
        }

        // ===== 3. 一元运算符 =====
        public static void TestUnaryExpressions()
        {
            Console.WriteLine("=== Testing Unary Expressions ===");

            TestExpr("var a = -x", "Unary(- Sym(x))");
            TestExpr("var b = not flag", "Unary(not Sym(flag))");

            Console.WriteLine();
        }

        // ===== 4. 二元运算符 =====
        public static void TestBinaryExpressions()
        {
            Console.WriteLine("=== Testing Binary Expressions ===");

            TestExpr("var r = 1 + 2", "Binary(Int(1,I32) + Int(2,I32))");
            TestExpr("var c = a == b", "Binary(Sym(a) == Sym(b))");
            TestExpr("var lg = x and y", "Binary(Sym(x) and Sym(y))");

            Console.WriteLine();
        }

        // ===== 5. 括号分组 =====
        public static void TestGroupExpressions()
        {
            Console.WriteLine("=== Testing Group Expressions ===");

            TestExpr("var g = (42)", "Group(Int(42,I32))");
            TestExpr("var p = (1 + 2)", "Group(Binary(Int(1,I32) + Int(2,I32)))");
            TestExpr("var m = 1 + (2 * 3)",
                "Binary(Int(1,I32) + Group(Binary(Int(2,I32) * Int(3,I32))))");
            TestExpr("var n2 = ((1 + 2) * 3)",
                "Group(Binary(Group(Binary(Int(1,I32) + Int(2,I32))) * Int(3,I32)))");
            TestExpr("var u = -(x + y)", "Unary(- Group(Binary(Sym(x) + Sym(y))))");

            Console.WriteLine();
        }

        // ===== 6. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Error Cases (expect ParserException) ===");

            // Latte 没有运算符优先级：未括号化的多个运算符必须报错
            TestError("var e1 = 1 + 2 * 3", "多个二元运算符未加括号");
            // 括号未闭合
            TestError("var e2 = (1 + 2", "括号未闭合");
            // 连续一元运算符未加括号
            TestError("var e3 = not not x", "连续一元运算符未加括号");
            // 一元与二元混用未加括号
            TestError("var e4 = -x + y", "一元与二元混用未加括号");
            // 运算符后缺少右操作数
            TestError("var e5 = 1 +", "缺少右操作数");

            Console.WriteLine();
        }

        // ===== 7. 类型标注 + 初始化 =====
        public static void TestTypedDeclarationsWithInit()
        {
            Console.WriteLine("=== Testing Typed Declarations with Initializer ===");

            TestVarDecl("var x: i32 = 42", "var", "i32", "Int(42,I32)");
            TestVarDecl("const name: String = \"Hi\"", "const", "String", "Str(\"Hi\")");
            TestVarDecl("var list: List\\<String> = null", "var", "List<String>", "Null");
            TestVarDecl("var n: i32? = null", "var", "i32?", "Null");
            // 回归：无初始化时 Initializer 必须为 null
            TestVarDecl("var count: i64", "var", "i64", null);

            Console.WriteLine();
        }

        // ===== 8. 调用表达式 =====
        public static void TestCallExpressions()
        {
            Console.WriteLine("=== Testing Call Expressions ===");

            TestExpr("var v = foo()", "Call(Sym(foo), [])");
            TestExpr("var v = foo(1)", "Call(Sym(foo), [Int(1,I32)])");
            TestExpr("var v = foo(1, x)", "Call(Sym(foo), [Int(1,I32), Sym(x)])");
            // 具名实参
            TestExpr("var v = foo(name = 1)", "Call(Sym(foo), [name:Int(1,I32)])");
            // 具名判别：name 后不是 = 时是位置实参，且表达式继续
            TestExpr("var v = foo(name + 1)", "Call(Sym(foo), [Binary(Sym(name) + Int(1,I32))])");
            // 位置与具名混合
            TestExpr("var v = foo(1, name = 2)", "Call(Sym(foo), [Int(1,I32), name:Int(2,I32)])");
            // 括号表达式作实参
            TestExpr("var v = foo((1 + 2), 3)",
                "Call(Sym(foo), [Group(Binary(Int(1,I32) + Int(2,I32))), Int(3,I32)])");

            Console.WriteLine();
        }

        // ===== 9. 成员访问与链式 =====
        public static void TestMemberAccessChains()
        {
            Console.WriteLine("=== Testing Member Access Chains ===");

            // 调用结果上的成员访问
            TestExpr("var v = foo().bar", "Access(Call(Sym(foo), []), .bar)");
            // 成员访问后再调用
            TestExpr("var v = foo().bar()", "Call(Access(Call(Sym(foo), []), .bar), [])");
            // 纯符号路径保持 Symbol 形式（不产生 MemberAccess）
            TestExpr("var v = a.b.c", "Sym(a.b.c)");
            // 安全访问
            TestExpr("var v = obj?.field", "Access(Sym(obj), ?.field)");

            Console.WriteLine();
        }

        // ===== 10. 索引表达式 =====
        public static void TestIndexExpressions()
        {
            Console.WriteLine("=== Testing Index Expressions ===");

            TestExpr("var v = a[0]", "Index(Sym(a), [Int(0,I32)])");
            TestExpr("var v = a[i + 1]", "Index(Sym(a), [Binary(Sym(i) + Int(1,I32))])");
            // 索引结果再调用
            TestExpr("var v = a[0](1)", "Call(Index(Sym(a), [Int(0,I32)]), [Int(1,I32)])");
            // 调用结果再索引
            TestExpr("var v = foo()[0]", "Index(Call(Sym(foo), []), [Int(0,I32)])");

            Console.WriteLine();
        }

        // ===== 11. new 表达式 =====
        public static void TestNewExpressions()
        {
            Console.WriteLine("=== Testing New Expressions ===");

            TestExpr("var v = new File(\"./x\")", "New(File, [Str(\"./x\")])");
            TestExpr("var v = new User(id = 42)", "New(User, [id:Int(42,I32)])");
            TestExpr("var v = new List\\<i32>()", "New(List<i32>, [])");

            Console.WriteLine();
        }

        // ===== 12. 泛型调用 =====
        public static void TestGenericCallExpressions()
        {
            Console.WriteLine("=== Testing Generic Call Expressions ===");

            TestExpr("var v = foo\\<i32>(1)", "Call(Sym(foo<i32>), [Int(1,I32)])");
            TestExpr("var v = a.b\\<i32>(x)", "Call(Sym(a.b<i32>), [Sym(x)])");
            // 底座是表达式的泛型成员调用
            TestExpr("var v = foo().bar\\<i32>(x)",
                "Call(Access(Call(Sym(foo), []), .bar<i32>), [Sym(x)])");

            Console.WriteLine();
        }

        // ===== 13. 后缀链错误用例 =====
        public static void TestSuffixErrorCases()
        {
            Console.WriteLine("=== Testing Suffix Error Cases (expect ParserException) ===");

            TestError("var v = foo(1", "调用参数未闭合");
            TestError("var v = a[1", "索引未闭合");
            TestError("var v = a?b", "? 后缺少 .");
            TestError("var v = foo(, 1)", "实参前多余逗号");

            Console.WriteLine();
        }

        // ===== 14. 类型检查与转换（is/supers/with、as/as?） =====
        public static void TestTypeOperators()
        {
            Console.WriteLine("=== Testing Type Operators (is/supers/with, as/as?) ===");

            TestExpr("var v = obj is String", "Check(Sym(obj) is String)");
            TestExpr("var v = obj supers Animal", "Check(Sym(obj) supers Animal)");
            TestExpr("var v = obj with Serializable", "Check(Sym(obj) with Serializable)");
            // is 右侧也可以是 Type\<T> 值（词法上统一按类型引用解析，SYNTAX §3.7）
            TestExpr("var v = obj is t", "Check(Sym(obj) is t)");
            TestExpr("var v = obj as String", "Cast(Sym(obj) as String)");
            TestExpr("var v = obj as? String", "Cast(Sym(obj) as? String)");
            // 泛型与可空目标类型
            TestExpr("var v = obj as List\\<i32>", "Cast(Sym(obj) as List<i32>)");
            TestExpr("var v = obj as String?", "Cast(Sym(obj) as String?)");
            // 后缀链之后再做类型操作
            TestExpr("var v = foo().bar as String", "Cast(Access(Call(Sym(foo), []), .bar) as String)");
            // 括号化之后可继续参与运算
            TestExpr("var v = (obj as String) + x",
                "Binary(Group(Cast(Sym(obj) as String)) + Sym(x))");

            Console.WriteLine();
        }

        // ===== 15. 类型操作错误用例 =====
        public static void TestTypeOperatorErrorCases()
        {
            Console.WriteLine("=== Testing Type Operator Error Cases (expect ParserException) ===");

            // as 后缺少类型
            TestError("var e = obj as", "as 后缺少类型");
            // ? 仅是 as 的安全转换标记，不能用于 is
            TestError("var e = obj is? String", "? 不能用于 is");
            // 类型操作后未加括号直接接二元运算符（无优先级规则）
            TestError("var e = obj as String + x", "类型操作后未加括号");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 解析一段变量声明代码，返回声明节点
        private static VariableDeclarationASTNode? ParseVarDecl(string code)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(code);
            var parser = new Parser();
            var ast = parser.Parse(tokens);

            if (ast is RootASTNode root && root.Children.Count > 0)
            {
                return root.Children[0] as VariableDeclarationASTNode;
            }
            return null;
        }

        // 结构校验：初始化表达式的描述串必须与期望完全一致
        private static void TestExpr(string code, string expectedDesc)
        {
            try
            {
                var decl = ParseVarDecl(code);
                if (decl == null)
                {
                    Fail(code, "no variable declaration node produced");
                    return;
                }

                string actual = DescribeExpression(decl.Initializer!.Expression);
                if (actual == expectedDesc)
                {
                    Pass(code, actual);
                }
                else
                {
                    Fail(code, $"expected {expectedDesc}, got {actual}");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"unexpected exception: {ex.Message}");
            }
        }

        // 错误校验：解析必须抛出 ParserException
        private static void TestError(string code, string reason)
        {
            try
            {
                ParseVarDecl(code);
                Fail(code, $"expected ParserException ({reason}), but parse succeeded");
            }
            catch (ParserException)
            {
                Console.WriteLine($"  [PASS] {code}  (rejected: {reason})");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(code, $"expected ParserException ({reason}), got {ex.GetType().Name}: {ex.Message}");
            }
        }

        // 完整校验：const/var + 类型标注 + 初始化表达式
        private static void TestVarDecl(string code, string expectedKind, string? expectedType, string? expectedInit)
        {
            try
            {
                var decl = ParseVarDecl(code);
                if (decl == null)
                {
                    Fail(code, "no variable declaration node produced");
                    return;
                }

                string kind = decl.IsConst ? "const" : "var";
                string? type = decl.TypeAnnotation != null ? DescribeType(decl.TypeAnnotation) : null;
                string? init = decl.Initializer != null ? DescribeExpression(decl.Initializer!.Expression) : null;

                if (kind == expectedKind && type == expectedType && init == expectedInit)
                {
                    Pass(code, $"{kind} {decl.Name}: {type ?? "<none>"} = {init ?? "<no init>"}");
                }
                else
                {
                    Fail(code, $"expected [{expectedKind} {expectedType ?? "<none>"} = {expectedInit ?? "<no init>"}], " +
                               $"got [{kind} {type ?? "<none>"} = {init ?? "<no init>"}]");
                }
            }
            catch (Exception ex)
            {
                Fail(code, $"unexpected exception: {ex.Message}");
            }
        }

        private static void Pass(string code, string result)
        {
            Console.WriteLine($"  [PASS] {code}");
            Console.WriteLine($"      => {result}");
            passCount++;
        }

        private static void Fail(string code, string message)
        {
            Console.WriteLine($"  [FAIL] {code}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== AST 描述 =====

        // 把表达式节点描述为紧凑的结构串，用于精确比对
        private static string DescribeExpression(ASTNode? node)
        {
            return node switch
            {
                null => "<null>",
                LiteralExpressionASTNode lit => DescribeExpression(lit.Literal),
                IntLiteralASTNode i => $"Int({i.Value},{i.IntType}{(i.IsHex ? ",hex" : "")})",
                FloatLiteralASTNode f => $"Float({f.Value}{(f.IsFloat ? "f" : "")})",
                StringLiteralASTNode s => $"Str(\"{s.Value}\")",
                BoolLiteralASTNode b => $"Bool({b.Value})",
                NullLiteralASTNode => "Null",
                SymbolReferenceASTNode sref => $"Sym({DescribeSymbol(sref.Symbol.symbol)})",
                UnaryExpressionASTNode u => $"Unary({u.Operator} {DescribeExpression(u.Operand.Expression)})",
                BinaryExpressionASTNode b =>
                    $"Binary({DescribeExpression(b.Left.Expression)} {b.Operator} {DescribeExpression(b.Right.Expression)})",
                GroupExpressionASTNode g => $"Group({DescribeExpression(g.InnerExpression.Expression)})",
                CallExpressionASTNode c =>
                    $"Call({DescribeExpression(c.Callee.Expression)}, [{string.Join(", ", c.Arguments.Select(DescribeArgument))}])",
                IndexExpressionASTNode ix =>
                    $"Index({DescribeExpression(ix.Object.Expression)}, [{string.Join(", ", ix.Indices.Select(DescribeArgument))}])",
                MemberAccessASTNode m =>
                    $"Access({DescribeExpression(m.Object.Expression)}, {(m.IsSafeAccess ? "?" : "")}.{m.MemberName}{DescribeGenericArgs(m)})",
                NewExpressionASTNode n =>
                    $"New({DescribeType(n.Type)}, [{string.Join(", ", n.Arguments.Select(DescribeArgument))}])",
                CastExpressionASTNode c =>
                    $"Cast({DescribeExpression(c.Object.Expression)} as{(c.IsSafe ? "?" : "")} {DescribeType(c.TargetType)})",
                TypeCheckExpressionASTNode t =>
                    $"Check({DescribeExpression(t.Object.Expression)} {t.Operator} {DescribeType(t.TargetType)})",
                EnumCaseExpressionASTNode ec => $"EnumCase(.{ec.CaseName})",
                WrapperAccessASTNode w =>
                    $"WrapperAccess({DescribeExpression(w.Object.Expression)}, :{w.WrapperName})",
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述实参（具名时为 name:value）
        private static string DescribeArgument(ArgumentASTNode arg)
        {
            return arg.Name != null
                ? $"{arg.Name}:{DescribeExpression(arg.Value.Expression)}"
                : DescribeExpression(arg.Value.Expression);
        }

        // 描述成员访问上的泛型实参
        private static string DescribeGenericArgs(MemberAccessASTNode m)
        {
            if (m.GenericArguments.Count == 0) return "";
            return "<" + string.Join(",", m.GenericArguments.Select(DescribeType)) + ">";
        }

        private static string DescribeType(TypeReferenceASTNode typeNode)
        {
            string typeName = DescribeSymbol(typeNode.TypeSymbol.symbol);
            if (typeNode.IsNullable) typeName += "?";
            return typeName;
        }

        private static string DescribeSymbol(Symbol symbol)
        {
            var parts = new List<string>();
            foreach (var element in symbol.elements)
            {
                string part = element.name;
                if (element.generics.Count > 0)
                {
                    part += "<" + string.Join(",", element.generics.Select(g => DescribeSymbol(g))) + ">";
                }
                parts.Add(part);
            }
            return string.Join(".", parts);
        }

        // ===== 15. 前导点 enum case 引用（SYNTAX §12，P5）=====
        public static void TestEnumCaseReferences()
        {
            Console.WriteLine("=== Testing Enum Case References ===");

            // 固定 case 引用（类型上下文由语义阶段校验，解析期只识别形态）
            TestExpr("var r = .Success", "EnumCase(.Success)");
            // 参数化 case 的调用：由后缀链自然脱糖为 Call
            TestExpr("var f = .Failed(404)", "Call(EnumCase(.Failed), [Int(404,I32)])");
            TestExpr("var n = .Failed(errorCode = 404)",
                "Call(EnumCase(.Failed), [errorCode:Int(404,I32)])");
            // 注解实参形态（@WrapperTarget(.Entity) 的同构表达式）
            TestExpr("var t = .Entity", "EnumCase(.Entity)");

            Console.WriteLine();
        }

        // ===== 16. wrapper 路径访问（SYNTAX §14.1，P5）=====
        public static void TestWrapperAccess()
        {
            Console.WriteLine("=== Testing Wrapper Access ===");

            // 基本形态：obj:Wrapper
            TestExpr("var w = service:Logged", "WrapperAccess(Sym(service), :Logged)");
            // 链式：obj:A:B 左结合（"obj 的修饰器 A 的修饰器 B"）
            TestExpr("var w = obj:A:B", "WrapperAccess(WrapperAccess(Sym(obj), :A), :B)");
            // 规范 §3 的完整路径示例：wrapper 访问在整条路径末尾
            TestExpr("var l = foo().bar[0]?.length:MyWrapper",
                "WrapperAccess(Access(Index(Access(Call(Sym(foo), []), .bar), [Int(0,I32)]), ?.length), :MyWrapper)");
            // wrapper 访问后仍可继续成员后缀
            TestExpr("var t = service:Logged.level",
                "Access(WrapperAccess(Sym(service), :Logged), .level)");

            Console.WriteLine();
        }

        // ===== 17. AST 结构断言（大扫除 §13.4，字符串快照之外的结构性校验） =====
        public static void TestStructuralAssertions()
        {
            Console.WriteLine("=== Testing AST Structural Assertions ===");

            // 用例 1：字面量初始化——Root 存在、已填充、Expression 类型、Parent 链
            TestStructure("var x = 42", decl =>
            {
                Assert(decl.Initializer is not null, "Initializer Root 存在");
                var root = decl.Initializer!;
                Assert(root.IsAttached, "Root 已填充");
                Assert(root.Expression is LiteralExpressionASTNode, "Expression 类型为 LiteralExpression");
                Assert(root.Expression.Parent == root, "Expression.Parent 指向 Root");
                Assert(root.Parent == decl, "Root.Parent 指向声明节点");
                Assert(decl.Parent is RootASTNode, "声明的 Parent 是文件 Root");
            });

            // 用例 2：二元 + 分组——子 Root 均已填充、Parent 链正确
            TestStructure("var m = 1 + (2 * 3)", decl =>
            {
                var bin = (BinaryExpressionASTNode)decl.Initializer!.Expression;
                Assert(bin.Left.IsAttached && bin.Right.IsAttached, "二元左右 Root 均已填充");
                Assert(bin.Left.Expression.Parent == bin.Left, "Left 表达式的 Parent 指向 Left Root");
                Assert(bin.Right.Expression.Parent == bin.Right, "Right 表达式的 Parent 指向 Right Root");
                var group = (GroupExpressionASTNode)bin.Right.Expression;
                Assert(group.InnerExpression.IsAttached, "分组 InnerExpression 已填充");
                Assert(group.InnerExpression.Expression.Parent == group.InnerExpression,
                    "分组内表达式的 Parent 指向内层 Root");
            });

            // 用例 3：调用链——Callee Root 与实参 Value Root 均填充、节点无共享
            TestStructure("var v = foo(1, name = 2)", decl =>
            {
                var call = (CallExpressionASTNode)decl.Initializer!.Expression;
                Assert(call.Callee.IsAttached, "Callee Root 已填充");
                Assert(call.Arguments.Count == 2, "两个实参");
                Assert(call.Arguments[0].Value.IsAttached && call.Arguments[1].Value.IsAttached,
                    "实参 Value Root 均已填充");
                Assert(call.Arguments[0].Value.Expression != call.Arguments[1].Value.Expression,
                    "实参表达式不共享节点");
                Assert(call.Arguments[1].Name == "name", "具名实参名");
            });

            // 用例 4：无初始化——可选 Root 以 null 表示（禁止「非 null 但为空的 Root」）
            TestStructure("var count: i64", decl =>
            {
                Assert(decl.Initializer is null, "无初始化时 Initializer 为 null Root");
            });

            Console.WriteLine();
        }

        // 结构断言辅助：解析后对声明节点执行一组结构检查
        private static void TestStructure(string code, Action<VariableDeclarationASTNode> assertions)
        {
            try
            {
                var decl = ParseVarDecl(code);
                if (decl == null)
                {
                    Fail(code, "no variable declaration node produced");
                    return;
                }
                assertions(decl);
                Console.WriteLine($"  [PASS] {code}");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(code, $"structural assertion failed: {ex.Message}");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException($"断言失败: {message}");
            }
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Expression Parser Tests           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestLiteralInitializers();
            TestSymbolReferences();
            TestUnaryExpressions();
            TestBinaryExpressions();
            TestGroupExpressions();
            TestErrorCases();
            TestTypedDeclarationsWithInit();
            TestCallExpressions();
            TestMemberAccessChains();
            TestIndexExpressions();
            TestNewExpressions();
            TestGenericCallExpressions();
            TestSuffixErrorCases();
            TestTypeOperators();
            TestTypeOperatorErrorCases();
            TestEnumCaseReferences();
            TestWrapperAccess();
            TestStructuralAssertions();

            Console.WriteLine($"=== Expression Tests Complete: {passCount} passed, {failCount} failed ===\n");

            return failCount;
        }
    }
}
