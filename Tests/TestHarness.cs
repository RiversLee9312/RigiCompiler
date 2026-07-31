using System;
using System.Collections.Generic;

namespace LatteCompiler.Tests
{
    /// <summary>
    /// 统一测试驱动与断言基建（M31）：全部测试套件共用一份，替代原先每个套件
    /// 各写一份的 Parse 驱动 / Pass-Fail 计数 / 打印样板。
    ///
    /// 用法（每个套件）：
    ///   public static int RunAll()
    ///   {
    ///       TestHarness.Reset();
    ///       TestHarness.Section("...");
    ///       ... 用例（Check/CheckParseError/CheckTrue）...
    ///       return TestHarness.Summary("套件名");
    ///   }
    ///
    /// 断言对象约定：除查的就是命令行/日志/token 流/层协议行为的套件外，
    /// 一律断言 AST 树产物（AstDescribe 描述串 + 结构事实），不断言控制台文本。
    /// </summary>
    public static class TestHarness
    {
        public static int PassCount { get; private set; }
        public static int FailCount { get; private set; }

        public static void Reset()
        {
            PassCount = 0;
            FailCount = 0;
        }

        public static void Section(string title) => Console.WriteLine($"=== {title} ===");

        public static void Blank() => Console.WriteLine();

        // ===== Parse 驱动 =====

        // 全管线：Lexer → Parser（含 ASTIntegrityValidator）→ Root
        public static RootASTNode ParseRoot(string code)
        {
            return ParseRoot(code, "<inline>");
        }

        // 带源名变体（重载而非可选参数：方法组 Select(TestHarness.ParseRoot)
        // 的类型推断依赖单签名）；sourceName 供中端套件的 Span/诊断链断言
        public static RootASTNode ParseRoot(string code, string sourceName)
        {
            return (RootASTNode)new Parser().Parse(new Lexer().Tokenize(code, sourceName));
        }

        // 独立代码块驱动（TestRootParserLayer 垫底：被测层漏消费 token 会立即暴露）
        public static CodeBlockASTNode ParseBlock(string code)
        {
            var block = new CodeBlockASTNode(null);
            ParseWithLayer(new CodeBlockParserLayer(block), code);
            return block;
        }

        // 独立 Layer 驱动（TestRootParserLayer 只接受 EOF）
        public static void ParseWithLayer(IParserLayer entryLayer, string code)
        {
            new Parser().Parse(new Lexer().Tokenize(code), new TestRootParserLayer(), entryLayer);
        }

        // 组合便捷：解析顶层并取第一个声明（字面量/变量声明套件常用）
        public static ASTNode ParseFirstDecl(string code)
        {
            var root = ParseRoot(code);
            if (root.Declarations.Count == 0)
                throw new InvalidOperationException("No AST node produced");
            return root.Declarations[0];
        }

        // ===== 黄金文本拼装 =====

        // 逐行精确比对的黄金文本：显式 \n，与源文件换行编码无关（autocrlf 免疫）
        public static string Lines(params string[] lines)
        {
            return string.Join("\n", lines) + "\n";
        }

        // ===== 断言 =====

        // 精确比对（AST 描述串等）
        public static void Check(string label, string actual, string expected)
        {
            if (actual == expected)
            {
                Console.WriteLine($"  [PASS] {label}");
                PassCount++;
            }
            else
            {
                Console.WriteLine($"  [FAIL] {label}");
                Console.WriteLine($"      expected: {expected}");
                Console.WriteLine($"      actual:   {actual}");
                FailCount++;
            }
        }

        // 结构事实断言（Parent 链、Root 填充、类型等）
        public static void CheckTrue(string label, bool condition, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine($"  [PASS] {label}");
                PassCount++;
            }
            else
            {
                Console.WriteLine($"  [FAIL] {label}{(detail.Length > 0 ? $" => {detail}" : "")}");
                FailCount++;
            }
        }

        // 期望解析失败：LexerException/ParserException 且消息含片段
        public static void CheckParseError(string label, Action parse, string expectedMessagePart)
        {
            try
            {
                parse();
                Console.WriteLine($"  [FAIL] {label} (应失败但成功了)");
                FailCount++;
            }
            catch (Exception ex) when (ex is ParserException || ex is LexerException)
            {
                if (ex.Message.Contains(expectedMessagePart))
                {
                    Console.WriteLine($"  [PASS] {label} (正确失败)");
                    PassCount++;
                }
                else
                {
                    Console.WriteLine($"  [FAIL] {label} (错误信息不匹配)");
                    Console.WriteLine($"      expected part: {expectedMessagePart}");
                    Console.WriteLine($"      actual:        {ex.Message}");
                    FailCount++;
                }
            }
        }

        // 期望语义诊断（M36 起的中端套件）：bag 中存在 Error 级且消息含片段的诊断
        public static void CheckSemanticError(string label, DiagnosticBag bag, string expectedMessagePart)
        {
            foreach (var d in bag.Diagnostics)
            {
                if (d.Severity == DiagnosticSeverity.Error && d.Message.Contains(expectedMessagePart))
                {
                    Console.WriteLine($"  [PASS] {label} (正确报错)");
                    PassCount++;
                    return;
                }
            }
            Console.WriteLine($"  [FAIL] {label} (缺少预期诊断)");
            Console.WriteLine($"      expected part: {expectedMessagePart}");
            Console.WriteLine($"      actual:        [{DescribeBag(bag)}]");
            FailCount++;
        }

        // 诊断袋内容简述（失败时对照用）
        private static string DescribeBag(DiagnosticBag bag)
        {
            var parts = new List<string>();
            foreach (var d in bag.Diagnostics)
            {
                parts.Add($"{d.Phase} {d.Severity}: {d.Message}");
            }
            return string.Join("; ", parts);
        }

        // 套件汇总（打印并返回失败数）
        public static int Summary(string suiteName)
        {
            Console.WriteLine($"=== {suiteName}: {PassCount} passed, {FailCount} failed ===");
            Console.WriteLine();
            return FailCount;
        }
    }
}
