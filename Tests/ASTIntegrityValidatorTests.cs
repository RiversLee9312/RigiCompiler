using System;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// AST 完整性验证器测试（Attribute 驱动重写后；M28 增加 span 与类型审计用例）
    ///
    /// 不经 Parser，手工构造 AST 直接调 ASTIntegrityValidator.Validate：
    /// - 合法小树（含 ExpressionStatement、Loop+RangeTo、ImportItem carrier、
    ///   注解 AttachTo 挂接）必须通过；
    /// - 各类结构破坏（Parent 指错、carrier 内节点 Parent 指错、
    ///   Root 未填充、节点共享）必须抛 CompilerInternalException；
    /// - span 破坏（缺失、倒置）与类型审计（未标注的 AST 成员、
    ///   标注在非 AST 成员上）必须抛 CompilerInternalException。
    /// 手工树统一经 SpanStamper 补合法假 span，使结构用例只验证目标检查项。
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
            root.Declarations.Add(decl);
            decl.Initializer = new ExpressionRootASTNode(decl);
            decl.Initializer.Attach(MakeIntLiteral(42));

            // import core.collections.List（ImportItem carrier 场景）
            var import = new ImportASTNode(root);
            root.Declarations.Add(import);
            var pathSymbol = new SymbolASTNode(import);
            pathSymbol.symbol.elements.Add(new SymbolElement { name = "core" });
            pathSymbol.symbol.elements.Add(new SymbolElement { name = "collections" });
            pathSymbol.symbol.elements.Add(new SymbolElement { name = "List" });
            import.importedSymbols.Add(new ImportItem { symbolNode = pathSymbol });

            // @Logged var y（注解延迟一次性 AttachTo 挂接）
            var decl2 = new VariableDeclarationASTNode(root) { Name = "y" };
            root.Declarations.Add(decl2);
            var ann = new AnnotationASTNode(null);
            ann.AttachTo(decl2);
            decl2.Annotations.Add(ann);

            // func main() { foo()  for (i in 0 to 10) { } }
            var func = new CallableDeclarationASTNode(root) { Name = "main" };
            root.Declarations.Add(func);
            func.Body = new CodeBlockASTNode(func);

            // 表达式语句 foo()（ExpressionStatement 统一容器）
            var stmt = new ExpressionStatementASTNode(func.Body);
            func.Body.Statements.Add(stmt);
            var call = new CallExpressionASTNode();
            stmt.Expression.Attach(call);
            var callee = new SymbolReferenceASTNode();
            call.Callee.Attach(callee);
            callee.Symbol.symbol.elements.Add(new SymbolElement { name = "foo" });

            // 范围循环（Iterable 起点 + RangeTo 终点，无搬家）
            var loop = new LoopStatementASTNode(func.Body) { Kind = LoopKind.For, VariableName = "i" };
            func.Body.Statements.Add(loop);
            loop.Iterable = new ExpressionRootASTNode(loop);
            loop.Iterable.Attach(MakeIntLiteral(0));
            loop.RangeTo = new ExpressionRootASTNode(loop);
            loop.RangeTo.Attach(MakeIntLiteral(10));

            StampSpans(root);
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
                root.Declarations.Add(decl);
                decl.TypeAnnotation = new TypeReferenceASTNode(root);
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            // carrier 内节点 Parent 指错：symbolNode 的 Parent 应为 ImportASTNode
            ExpectThrow("carrier 内 symbolNode Parent 指错（→ Root）", () =>
            {
                var root = new RootASTNode();
                var import = new ImportASTNode(root);
                root.Declarations.Add(import);
                import.importedSymbols.Add(
                    new ImportItem { symbolNode = new SymbolASTNode(root) });
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            // ExpressionRoot 存在但未填充（可选位置必须用 null Root 表示）
            ExpectThrow("ExpressionRoot 未填充", () =>
            {
                var root = new RootASTNode();
                var decl = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Declarations.Add(decl);
                decl.Initializer = new ExpressionRootASTNode(decl);
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            // 节点共享：同一节点两次挂入 Children
            ExpectThrow("节点共享（Children 重复挂同一节点）", () =>
            {
                var root = new RootASTNode();
                var shared = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Declarations.Add(shared);
                root.Declarations.Add(shared);
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            Console.WriteLine();
        }

        // ===== Span 破坏与类型审计（M28）=====

        public static void TestSpanAndAuditViolations()
        {
            Console.WriteLine("=== Testing Span & Audit Violations (expect CompilerInternalException) ===");

            // 子节点缺 Span（root 已盖戳、子节点故意不盖）
            ExpectThrow("子节点缺 Span", () =>
            {
                var root = new RootASTNode();
                StampNode(root);
                var decl = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Declarations.Add(decl);
                ASTIntegrityValidator.Validate(root);
            });

            // Span 首尾倒置
            ExpectThrow("Span 首尾倒置", () =>
            {
                var root = new RootASTNode();
                var decl = new VariableDeclarationASTNode(root) { Name = "x" };
                root.Declarations.Add(decl);
                StampSpans(root);
                decl.Span = new CharRange
                {
                    Start = new CharPosition { line = 2, column = 5, offset = 10 },
                    End = new CharPosition { line = 1, column = 1, offset = 0 },
                    sourceName = "<test>"
                };
                ASTIntegrityValidator.Validate(root);
            });

            // 类型审计：装着 ASTNode 的字段忘记标注
            ExpectThrow("未标注的 ASTNode 字段（类型审计）", () =>
            {
                var root = new RootASTNode();
                var bad = new UnannotatedFieldNode(root);
                root.Declarations.Add(bad);
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            // 类型审计：装着 ASTNode 的自动属性忘记标注
            ExpectThrow("未标注的 ASTNode 自动属性（类型审计）", () =>
            {
                var root = new RootASTNode();
                var bad = new UnannotatedPropertyNode(root);
                root.Declarations.Add(bad);
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            // 类型审计：[ChildAstNode] 标在不装 ASTNode 的成员上
            ExpectThrow("[ChildAstNode] 标在非 AST 成员上", () =>
            {
                var root = new RootASTNode();
                var bad = new MisannotatedNode(root);
                root.Declarations.Add(bad);
                StampSpans(root);
                ASTIntegrityValidator.Validate(root);
            });

            Console.WriteLine();
        }

        // ===== 测试专用坏节点（类型审计用例）=====

        // 忘记标注 [ChildAstNode] 的 ASTNode 字段
        private class UnannotatedFieldNode : ASTNode
        {
            public SymbolASTNode Forgotten = new SymbolASTNode(null);
            public UnannotatedFieldNode(ASTNode? parent) : base(parent) { }
        }

        // 忘记标注 [ChildAstNode] 的 ASTNode 自动属性（有 backing 字段，会被审计）
        private class UnannotatedPropertyNode : ASTNode
        {
            public SymbolASTNode ForgottenProp { get; } = new SymbolASTNode(null);
            public UnannotatedPropertyNode(ASTNode? parent) : base(parent) { }
        }

        // [ChildAstNode] 标在不装 ASTNode 的字段上
        private class MisannotatedNode : ASTNode
        {
            [ChildAstNode] public string NotANode = "";
            public MisannotatedNode(ASTNode? parent) : base(parent) { }
        }

        // ===== 测试辅助 =====

        // 构造一个未挂载的整数字面量表达式（字面量节点的 Parent 即包装节点）
        private static LiteralExpressionASTNode MakeIntLiteral(long value)
        {
            var litExpr = new LiteralExpressionASTNode();
            litExpr.AttachLiteral(new IntLiteralASTNode(litExpr) { Value = value });
            return litExpr;
        }

        // 单个节点盖合法假 span（Validator 的 span 检查要求每节点都有）
        private static void StampNode(ASTNode node)
        {
            node.Span = new CharRange
            {
                Start = new CharPosition { line = 1, column = 1, offset = 0 },
                End = new CharPosition { line = 1, column = 1, offset = 0 },
                sourceName = "<test>"
            };
        }

        // 给手工树的全部节点盖戳：复用 ASTVisitor 统一遍历（顺带覆盖遍历基建）
        private static void StampSpans(ASTNode root)
        {
            new SpanStamper().Visit(root);
        }

        private sealed class SpanStamper : ASTVisitor
        {
            protected override void OnNode(ASTNode node, ASTNode? parent, string? via)
            {
                StampNode(node);
            }
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
            TestSpanAndAuditViolations();

            Console.WriteLine($"=== AST Integrity Validator Tests Complete: {passCount} passed, {failCount} failed ===");
            return failCount;
        }
    }
}
