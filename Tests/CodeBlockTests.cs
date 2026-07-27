using System;
using System.Collections.Generic;
using System.Linq;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 代码块解析测试（roadmap #6，P2 语句系统核心）
    ///
    /// 覆盖：
    /// 1. 空块与变量声明语句（含 } 终止块内末语句）
    /// 2. 表达式语句
    /// 3. 赋值语句（符号/成员/索引目标）
    /// 4. return 语句（裸/带值/@标签）
    /// 5. break/continue 语句（含 @标签）
    /// 6. if 语句（无 else/有 else/else if 链）
    /// 7. 错误用例
    ///
    /// 驱动方式：Parser.Parse(tokens, new CodeBlockParserLayer(block)) 独立入口，
    /// 源码以 { ... } 包裹。
    /// </summary>
    public class CodeBlockTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 1. 变量声明语句 =====
        public static void TestVariableDeclarations()
        {
            Console.WriteLine("=== Testing Variable Declarations in Block ===");

            TestBlock("{ }", "[]");
            TestBlock("{ var x = 42 }", "[var x = Int(42,I32)]");
            // 多语句 + } 终止块内末语句（无换行）
            TestBlock("{\n    var x = 1\n    var y: i32 = 2\n    const z = 3\n}",
                "[var x = Int(1,I32), var y: i32 = Int(2,I32), const z = Int(3,I32)]");
            // 无初始化声明（} 终止）
            TestBlock("{ var count: i64 }", "[var count: i64]");

            Console.WriteLine();
        }

        // ===== 2. 表达式语句 =====
        public static void TestExpressionStatements()
        {
            Console.WriteLine("=== Testing Expression Statements ===");

            TestBlock("{ foo(1) }", "[Call(Sym(foo), [Int(1,I32)])]");
            TestBlock("{\n    foo(1)\n    obj.field\n}",
                "[Call(Sym(foo), [Int(1,I32)]), Sym(obj.field)]");

            Console.WriteLine();
        }

        // ===== 3. 赋值语句 =====
        public static void TestAssignStatements()
        {
            Console.WriteLine("=== Testing Assign Statements ===");

            TestBlock("{ x = 5 }", "[Assign(Sym(x) = Int(5,I32))]");
            // 纯符号路径目标保持 Symbol 形态
            TestBlock("{ obj.field = v }", "[Assign(Sym(obj.field) = Sym(v))]");
            // 表达式底座上的成员/索引目标
            TestBlock("{ foo().field = (1 + 2) }",
                "[Assign(Access(Call(Sym(foo), []), .field) = Group(Binary(Int(1,I32) + Int(2,I32))))]");
            TestBlock("{ a[0] = v }", "[Assign(Index(Sym(a), [Int(0,I32)]) = Sym(v))]");

            Console.WriteLine();
        }

        // ===== 4. return 语句 =====
        public static void TestReturnStatements()
        {
            Console.WriteLine("=== Testing return Statements ===");

            TestBlock("{ return }", "[Return]");
            TestBlock("{ return 42 }", "[Return(Int(42,I32))]");
            TestBlock("{\n    return x\n}", "[Return(Sym(x))]");
            // @标签（SYNTAX §6.1 seq 默认标签）
            TestBlock("{ return@seq (x * 2) }",
                "[Return@seq(Group(Binary(Sym(x) * Int(2,I32))))]");

            Console.WriteLine();
        }

        // ===== 5. break / continue 语句 =====
        public static void TestLoopControlStatements()
        {
            Console.WriteLine("=== Testing break/continue Statements ===");

            TestBlock("{ break }", "[Break]");
            TestBlock("{ continue }", "[Continue]");
            TestBlock("{\n    break@outer\n    continue@inner\n}", "[Break@outer, Continue@inner]");

            Console.WriteLine();
        }

        // ===== 6. if 语句 =====
        public static void TestIfStatements()
        {
            Console.WriteLine("=== Testing if Statements ===");

            // 无 else
            TestBlock("{ if (x > 0) { foo() } }",
                "[IfStmt(Binary(Sym(x) > Int(0,I32)), [Call(Sym(foo), [])], <none>)]");
            // 有 else
            TestBlock("{\n    if (c) {\n        a()\n    } else {\n        b()\n    }\n}",
                "[IfStmt(Sym(c), [Call(Sym(a), [])], [Call(Sym(b), [])])]");
            // else if 链
            TestBlock("{ if (a) { f() } else if (b) { g() } else { h() } }",
                "[IfStmt(Sym(a), [Call(Sym(f), [])], " +
                "IfStmt(Sym(b), [Call(Sym(g), [])], [Call(Sym(h), [])]))]");
            // if 语句后跟其他语句（无 else 时正确交还 token）
            TestBlock("{\n    if (c) { a() }\n    b()\n}",
                "[IfStmt(Sym(c), [Call(Sym(a), [])], <none>), Call(Sym(b), [])]");
            // if 语句体内的变量声明与赋值
            TestBlock("{ if (c) { var x = 1\n x = 2 } }",
                "[IfStmt(Sym(c), [var x = Int(1,I32), Assign(Sym(x) = Int(2,I32))], <none>)]");

            Console.WriteLine();
        }

        // ===== 7. 错误用例 =====
        public static void TestErrorCases()
        {
            Console.WriteLine("=== Testing Code Block Error Cases (expect ParserException) ===");

            // 变量声明缺初始化
            TestError("{ var x = }", "缺初始化表达式");
            // if 语句缺 {
            TestError("{ if (c) foo() }", "if 语句缺 {");
            // 赋值缺值
            TestError("{ x = }", "赋值缺值");
            // return@ 缺标签
            TestError("{ return@ 1 }", "return@ 缺标签");
            // 块未闭合
            TestError("{ var x = 1", "块未闭合");

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 解析一段 { ... } 代码块，返回块节点
        private static CodeBlockASTNode ParseBlock(string code)
        {
            var lexer = new Lexer();
            var tokens = lexer.Tokenize(code);
            var parser = new Parser();
            var block = new CodeBlockASTNode(null);
            parser.Parse(tokens, new TestRootParserLayer(), new CodeBlockParserLayer(block));
            return block;
        }

        // 结构校验：块内语句的描述串必须与期望完全一致
        private static void TestBlock(string code, string expectedDesc)
        {
            try
            {
                var block = ParseBlock(code);
                string actual = DescribeBlock(block);
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
                ParseBlock(code);
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

        // 描述代码块：[stmt, stmt, ...]
        private static string DescribeBlock(CodeBlockASTNode block)
        {
            return "[" + string.Join(", ", block.Statements.Select(DescribeStatement)) + "]";
        }

        // 描述语句节点
        private static string DescribeStatement(ASTNode node)
        {
            return node switch
            {
                VariableDeclarationASTNode v => DescribeVarDecl(v),
                ExpressionStatementASTNode s => s.AssignValue != null
                    ? $"Assign({DescribeExpression(s.Expression.Expression)} = {DescribeExpression(s.AssignValue.Expression)})"
                    : DescribeExpression(s.Expression.Expression),
                ReturnStatementASTNode r =>
                    $"Return{(r.Label != null ? "@" + r.Label : "")}" +
                    $"{(r.Value != null ? $"({DescribeExpression(r.Value!.Expression)})" : "")}",
                LoopControlStatementASTNode l =>
                    $"{(l.IsBreak ? "Break" : "Continue")}{(l.Label != null ? "@" + l.Label : "")}",
                IfStatementASTNode i => DescribeIf(i),
                LoopStatementASTNode l => DescribeLoop(l),
                CodeBlockASTNode b => DescribeBlock(b),
                ExpressionASTNode e => DescribeExpression(e),
                _ => $"<{node.GetType().Name}>"
            };
        }

        // 描述变量声明：var name: Type = init（@ 注解为 SYNTAX §14.5 的 wrapper 应用）
        private static string DescribeVarDecl(VariableDeclarationASTNode v)
        {
            string desc = $"{(v.IsConst ? "const" : "var")} {v.Name}";
            if (v.TypeAnnotation != null) desc += $": {DescribeType(v.TypeAnnotation)}";
            if (v.Initializer != null) desc += $" = {DescribeExpression(v.Initializer!.Expression)}";
            return DescribeAnnotations(v) + desc;
        }

        // 描述注解列表（@Name[(args)]，可叠加）
        private static string DescribeAnnotations(IWrapperAttachable node)
        {
            if (node.Annotations.Count == 0) return "";
            return string.Join(" ", node.Annotations.Select(DescribeAnnotation)) + " ";
        }

        private static string DescribeAnnotation(AnnotationASTNode a)
        {
            var desc = "@" + DescribeSymbol(a.Name.symbol);
            if (a.HasArguments)
                desc += "(" + string.Join(", ", a.Arguments.Select(DescribeArgument)) + ")";
            return desc;
        }

        // 描述 if 语句：IfStmt(cond, [then], [else] / IfStmt(else if) / <none>)
        private static string DescribeIf(IfStatementASTNode i)
        {
            string elsePart = i.ElseBranch switch
            {
                null => "<none>",
                CodeBlockASTNode b => DescribeBlock(b),
                IfStatementASTNode nested => DescribeIf(nested),
                _ => $"<{i.ElseBranch.GetType().Name}>"
            };
            return $"IfStmt({DescribeExpression(i.Condition.Expression)}, {DescribeBlock(i.ThenBlock)}, {elsePart})";
        }

        // 描述循环语句（CodeBlockTests 仅覆盖 if 内嵌套循环的可能性，完整测试见 LoopTests）
        private static string DescribeLoop(LoopStatementASTNode l)
        {
            string label = l.Label != null ? $", named {l.Label}" : "";
            return l.Kind switch
            {
                LoopKind.For => l.RangeTo != null
                    ? $"For({l.VariableName}, Range({DescribeExpression(l.Iterable!.Expression)} to {DescribeExpression(l.RangeTo.Expression)}){label}, {DescribeBlock(l.Body)})"
                    : $"For({l.VariableName}, {DescribeExpression(l.Iterable!.Expression)}{label}, {DescribeBlock(l.Body)})",
                LoopKind.While =>
                    $"While({DescribeExpression(l.Condition!.Expression)}{label}, {DescribeBlock(l.Body)})",
                _ =>
                    $"DoWhile({DescribeExpression(l.Condition!.Expression)}{label}, {DescribeBlock(l.Body)})"
            };
        }

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
                    $"Access({DescribeExpression(m.Object.Expression)}, {(m.IsSafeAccess ? "?" : "")}.{m.MemberName})",
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

        // ===== 8. 栈上变量的 @ 注解（wrapper 应用，SYNTAX §14.3/§14.5，P5）=====
        public static void TestAnnotatedDeclarations()
        {
            Console.WriteLine("=== Testing Annotated Declarations in Block ===");

            // 规范 §14.3 示例形态：值 wrapper 修饰栈上变量
            TestBlock("{\n    @Clamped(0, 100)\n    var health: i32 = 50\n}",
                "[@Clamped(Int(0,I32), Int(100,I32)) var health: i32 = Int(50,I32)]");
            // 无参注解（不写括号）
            TestBlock("{\n    @Logged\n    var x: i32\n}",
                "[@Logged var x: i32]");

            Console.WriteLine();
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Code Block Tests                  ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestVariableDeclarations();
            TestExpressionStatements();
            TestAssignStatements();
            TestReturnStatements();
            TestLoopControlStatements();
            TestIfStatements();
            TestErrorCases();
            TestAnnotatedDeclarations();

            Console.WriteLine($"=== Code Block Tests Complete: {passCount} passed, {failCount} failed ===\n");

            return failCount;
        }
    }
}
