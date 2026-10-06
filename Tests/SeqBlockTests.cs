using System;

namespace RigiCompiler.Tests
{
    // Seq 块解析测试（roadmap #11，SYNTAX.md §6）：代码块独立驱动
    // （CompilerTestTools.ParseBlock），断言 AstDescribe 精确描述串。
    // 覆盖：简单 seq / volatile / using 资源绑定（单个/多个）/ named 标签 /
    // 组合（volatile + using + named）/ seq 作为表达式（return@_，§6.1 默认标签）/ 错误用例。
    public class SeqBlockTests
    {
        // ===== 1. 简单 seq 块 =====
        public static void TestSimpleSeq()
        {
            CompilerTestTools.Section("Testing Simple Seq Blocks");

            TestBlock("{ seq { var x = 1 } }",
                "[Seq([var x = Int(1,I32)])]");

            TestBlock("{ seq { var x = 1\nvar y = 2 } }",
                "[Seq([var x = Int(1,I32), var y = Int(2,I32)])]");

            CompilerTestTools.Blank();
        }

        // ===== 2. volatile seq =====
        public static void TestVolatileSeq()
        {
            CompilerTestTools.Section("Testing Volatile Seq");

            TestBlock("{ volatile seq { operation() } }",
                "[Seq(volatile, [Path(operation(), [])])]");

            foreach (var prefix in new[] { "unsafe volatile", "volatile unsafe" })
            {
                var block = CompilerTestTools.ParseBlock("{ " + prefix + " seq { operation() } }");
                CaseAssertions.Check(prefix, AstDescribe.Block(block),
                    "[Seq(volatile, unsafe, [Path(operation(), [])])]");
                CaseAssertions.CheckTrue(prefix + " 结构与父链",
                    block.Statements[0] is SeqBlockExpressionASTNode
                    { IsUnsafe: true, IsVolatile: true } seq && seq.Parent == block
                    && seq.Body.Parent == seq);
            }
            TestBlock("{ unsafe seq { operation() } }",
                "[Seq(unsafe, [Path(operation(), [])])]");
            TestInvalidBlock("{ unsafe unsafe seq { } }", "Duplicate modifier 'unsafe'");
            TestInvalidBlock("{ volatile volatile seq { } }", "Duplicate modifier 'volatile'");

            CompilerTestTools.Blank();
        }

        // ===== 3. using 资源绑定 =====
        public static void TestUsingBindings()
        {
            CompilerTestTools.Section("Testing Using Bindings");

            // 单个 using
            TestBlock("{ seq using(const file = open()) { use(file) } }",
                "[Seq(using(const file = Path(open(), [])), [Path(use(Path(file, [])), [])])]");

            // 多个 using
            TestBlock("{\n" +
                      "    seq using(const f = openFile())\n" +
                      "    using(var s = openStream(f)) {\n" +
                      "        read(s)\n" +
                      "    }\n" +
                      "}",
                "[Seq(using(const f = Path(openFile(), [])), " +
                "using(var s = Path(openStream(Path(f, [])), [])), " +
                "[Path(read(Path(s, [])), [])])]");

            // using 带类型标注
            TestBlock("{ seq using(const res: Resource = get()) { use(res) } }",
                "[Seq(using(const res: Resource = Path(get(), [])), [Path(use(Path(res, [])), [])])]");

            CompilerTestTools.Blank();
        }

        // ===== 4. named 标签 =====
        public static void TestNamedLabel()
        {
            CompilerTestTools.Section("Testing Named Labels");

            TestBlock("{ seq named myBlock { compute() } }",
                "[Seq(named myBlock, [Path(compute(), [])])]");

            TestBlock("{ seq named outer { seq named inner { work() } } }",
                "[Seq(named outer, [Seq(named inner, [Path(work(), [])])])]");

            CompilerTestTools.Blank();
        }

        // ===== 5. 组合 =====
        public static void TestCombinations()
        {
            CompilerTestTools.Section("Testing Combinations");

            // volatile + using + named
            TestBlock("{ volatile seq using(const x = init()) named block { process(x) } }",
                "[Seq(volatile, using(const x = Path(init(), [])), named block, [Path(process(Path(x, [])), [])])]");

            // 多个 using + named
            TestBlock("{\n" +
                      "    seq using(const a = getA())\n" +
                      "    using(const b = getB())\n" +
                      "    named mySeq {\n" +
                      "        work(a, b)\n" +
                      "    }\n" +
                      "}",
                "[Seq(using(const a = Path(getA(), [])), " +
                "using(const b = Path(getB(), [])), " +
                "named mySeq, " +
                "[Path(work(Path(a, []), Path(b, [])), [])])]");

            CompilerTestTools.Blank();
        }

        // ===== 6. seq 作为表达式（return@_，§6.1 匿名默认标签）=====
        public static void TestSeqAsExpression()
        {
            CompilerTestTools.Section("Testing Seq as Expression");

            // return@_（匿名 seq 的默认标签是 _）
            TestBlock("{ var result = seq { return@_ compute() } }",
                "[var result = Seq([Return@_(Path(compute(), []))])]");

            // return@label
            TestBlock("{ var r = seq named calc { return@calc getValue() } }",
                "[var r = Seq(named calc, [Return@calc(Path(getValue(), []))])]");

            // 复杂示例：SYNTAX.md §6.1
            TestBlock("{ const result = seq { const ac = a * c\nreturn@_ ac } }",
                "[const result = Seq([const ac = Binary(Path(a, []) * Path(c, [])), Return@_(Path(ac, []))])]");

            CompilerTestTools.Blank();
        }

        // ===== 7. 错误用例 =====
        public static void TestInvalidCases()
        {
            CompilerTestTools.Section("Testing Invalid Cases");

            // volatile 后没有 seq
            TestInvalidBlock("{ volatile { operation() } }",
                "Expected 'seq' after 'volatile'");

            // using 后缺少 (
            TestInvalidBlock("{ seq using const x = 1 { } }",
                "Expected '(' after 'using'");

            // using 内不是 var/const
            TestInvalidBlock("{ seq using(x = 1) { } }",
                "Expected 'const' or 'var' in using clause");

            // named 标签以数字开头
            TestInvalidBlock("{ seq named 123block { } }",
                "Label name cannot start with a digit");

            CompilerTestTools.Blank();
        }

        // ===== 辅助 =====

        // 解析代码块并比对 AST 描述串（label：多行源码 \n 转义显示）
        private static void TestBlock(string source, string expectedDesc)
        {
            try
            {
                var block = CompilerTestTools.ParseBlock(source);
                CaseAssertions.Check(source.Replace("\n", "\\n"), AstDescribe.Block(block), expectedDesc);
            }
            catch (Exception ex)
            {
                CaseAssertions.CheckTrue($"{source.Replace("\n", "\\n")} => 意外异常", false, ex.Message);
            }
        }

        private static void TestInvalidBlock(string source, string expectedError)
        {
            CaseAssertions.CheckParseError(source.Replace("\n", "\\n"),
                () => CompilerTestTools.ParseBlock(source), expectedError);
        }


        internal static TestSuiteData Spec { get; } = new("SeqBlock",
        [
            (nameof(TestSimpleSeq), TestSimpleSeq),
            (nameof(TestVolatileSeq), TestVolatileSeq),
            (nameof(TestUsingBindings), TestUsingBindings),
            (nameof(TestNamedLabel), TestNamedLabel),
            (nameof(TestCombinations), TestCombinations),
            (nameof(TestSeqAsExpression), TestSeqAsExpression),
            (nameof(TestInvalidCases), TestInvalidCases),
        ], sectionTitle: "SeqBlock");
    }
}
