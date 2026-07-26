using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// AST 完整性验证器测试（Attribute 驱动重写后）
    ///
    /// 不经 Parser，手工构造 AST 直接调 ASTIntegrityValidator.Validate：
    /// - 合法小树（含 ExpressionStatement、Loop+RangeTo、ImportItem carrier、
    ///   注解 AttachTo 挂接）必须通过；
    /// - 各类结构破坏（Parent 指错、carrier 内节点 Parent 指错、
    ///   Root 未填充、节点共享）必须抛 CompilerInternalException。
    /// </summary>
    public static class ASTIntegrityValidatorTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // ===== 合法结构 =====

        // 合法小树：声明 + 初始化器 + import carrier + 注解 + 表达式语句 + 范围循环
        public static void TestValidTree()
        {
            Console.WriteLine("=== Testing Valid AST (expect pass) ===");

            var root = new RootASTNode();

            // var x = 42
            var decl = new VariableDeclarationASTNode(root) { Name = "x" };
            root.Children.Add(decl);
            decl.Initializer = new ExpressionRootASTNode(decl);
            decl.Initializer.Attach(MakeIntLiteral(42));

            // import core.collections.List（ImportItem carrier 场景）
            var import = new ImportASTNode(root);
            root.Children.Add(import);
            var pathSymbol = new SymbolASTNode(import);
            pathSymbol.symbol.elements.Add(new SymbolElement { name = "core" });
            pathSymbol.symbol.elements.Add(new SymbolElement { name = "collections" });
            pathSymbol.symbol.elements.Add(new SymbolElement { name = "List" });
            import.importedSymbols.Add(new ImportItem { symbolNode = pathSymbol });

            // @Logged var y（注解延迟一次性 AttachTo 挂接）
            var decl2 = new VariableDeclarationASTNode(root) { Name = "y" };
            root.Children.Add(decl2);
            var ann = new AnnotationASTNode(null);
            ann.AttachTo(decl2);
            decl2.Annotations.Add(ann);

            // func main() { foo()  for (i in 0 to 10) { } }
            var func = new CallableDeclarationASTNode(root) { Name = "main" };
            root.Children.Add(func);
            func.Body = new CodeBlockASTNode(func);

            // 表达式语句 foo()（ExpressionStatement 统一容器）
            var stmt = new ExpressionStatementASTNode(func.Body);
            func.Body.Children.Add(stmt);
            var call = new CallExpressionASTNode();
            stmt.Expression.Attach(call);
            var callee = new SymbolReferenceASTNode();
            call.Callee.Attach(callee);
            callee.Symbol.symbol.elements.Add(new SymbolElement { name = "foo" });

            // 范围循环（Iterable 起点 + RangeTo 终点，无搬家）
            var loop = new LoopStatementASTNode(func.Body) { Kind = LoopKind.For, VariableName = "i" };
            func.Body.Children.Add(loop);
            loop.Iterable = new ExpressionRootASTNode(loop);
            loop.Iterable.Attach(MakeIntLiteral(0));
            loop.RangeTo = new ExpressionRootASTNode(loop);
            loop.RangeTo.Attach(MakeIntLiteral(10));

            ExpectPass("合法小树（声明/import/注解/表达式语句/范围循环）", root);
            Console.WriteLine();
        }

        // ===== 结构破坏 =====

        public static void TestInvalidStructures()
        {
            Console.WriteLine("=== Testing Invalid AST (expect CompilerInternalException) ===");

            // 子节点 Parent 指错：TypeAnnotation 的 Parent 应为声明节点
            ExpectThrow("子节点 Parent 指错（TypeAnnotation → Root）", () =>
            {
                var root = new RootASTNode();
                var decl = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Children.Add(decl);
                decl.TypeAnnotation = new TypeReferenceASTNode(root);
                ASTIntegrityValidator.Validate(root);
            });

            // carrier 内节点 Parent 指错：symbolNode 的 Parent 应为 ImportASTNode
            ExpectThrow("carrier 内 symbolNode Parent 指错（→ Root）", () =>
            {
                var root = new RootASTNode();
                var import = new ImportASTNode(root);
                root.Children.Add(import);
                import.importedSymbols.Add(
                    new ImportItem { symbolNode = new SymbolASTNode(root) });
                ASTIntegrityValidator.Validate(root);
            });

            // ExpressionRoot 存在但未填充（可选位置必须用 null Root 表示）
            ExpectThrow("ExpressionRoot 未填充", () =>
            {
                var root = new RootASTNode();
                var decl = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Children.Add(decl);
                decl.Initializer = new ExpressionRootASTNode(decl);
                ASTIntegrityValidator.Validate(root);
            });

            // 节点共享：同一节点两次挂入 Children
            ExpectThrow("节点共享（Children 重复挂同一节点）", () =>
            {
                var root = new RootASTNode();
                var shared = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Children.Add(shared);
                root.Children.Add(shared);
                ASTIntegrityValidator.Validate(root);
            });

            Console.WriteLine();
        }

        // ===== 测试辅助 =====

        // 构造一个未挂载的整数字面量表达式（字面量节点的 Parent 即包装节点）
        private static LiteralExpressionASTNode MakeIntLiteral(long value)
        {
            var litExpr = new LiteralExpressionASTNode();
            litExpr.AttachLiteral(new IntLiteralASTNode(litExpr) { Value = value });
            return litExpr;
        }

        private static void ExpectPass(string name, RootASTNode root)
        {
            try
            {
                ASTIntegrityValidator.Validate(root);
                Console.WriteLine($"  [PASS] {name}");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(name, $"expected pass, got {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ExpectThrow(string name, Action build)
        {
            try
            {
                build();
                Fail(name, "expected CompilerInternalException, but validation passed");
            }
            catch (CompilerInternalException ex)
            {
                Console.WriteLine($"  [PASS] {name}  (rejected: {ex.Message})");
                passCount++;
            }
            catch (Exception ex)
            {
                Fail(name, $"expected CompilerInternalException, got {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Fail(string name, string message)
        {
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"      => {message}");
            failCount++;
        }

        // ===== 入口 =====
        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  AST Integrity Validator Tests     ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestValidTree();
            TestInvalidStructures();

            Console.WriteLine($"=== AST Integrity Validator Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
