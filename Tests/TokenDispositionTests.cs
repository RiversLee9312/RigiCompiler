using System;
using System.Collections.Generic;
using System.Linq;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// TokenDisposition 协议测试（大扫除 §13.1）
    ///
    /// 用假的 Parser Layer 单独验证四种组合的控制流语义：
    ///   Push + Consume / Push + Replay / Pop + Consume / Pop + Replay
    ///
    /// 每个用例验证：
    /// - 哪个 Layer 接收当前 Token（逐层记录接收序列并精确比对）；
    /// - Token offset 是否前进（Replay 时同一 token 被两层先后见到）；
    /// - 栈深是否正确（解析必须恰好收敛到 TestRoot，否则 Parser 报错）；
    /// - 不存在重复消费或漏消费（TestRoot 拒绝任何残留普通 token）。
    /// </summary>
    public class TokenDispositionTests
    {
        private static int passCount = 0;
        private static int failCount = 0;

        // 记录型假 Layer：按脚本逐步对 token 作出反应，并记录收到的每个 token 的内容
        private sealed class ScriptLayer : IParserLayer
        {
            private readonly Queue<Func<Token, ParserLayerResult>> steps;
            public readonly List<string> Received = new();

            public ScriptLayer(params Func<Token, ParserLayerResult>[] actions)
            {
                steps = new Queue<Func<Token, ParserLayerResult>>(actions);
            }

            public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
            {
                Received.Add(currentToken.Content);
                if (steps.Count == 0)
                {
                    throw context.RaiseError($"ScriptLayer received unexpected extra token: {currentToken}");
                }
                return steps.Dequeue()(currentToken);
            }
        }

        private static WordToken T(string content) => new WordToken(content);

        private static readonly Func<Token, ParserLayerResult> PopConsume =
            _ => new ParserLayerResult.PopLayer(TokenDisposition.Consume);
        private static readonly Func<Token, ParserLayerResult> PopReplay =
            _ => new ParserLayerResult.PopLayer(TokenDisposition.Replay);

        // Push + Consume：父层消费 t1 后压入子层，子层从 t2 开始解析
        private static void TestPushConsume()
        {
            var child = new ScriptLayer(PopConsume);                    // 收 t2，消费后弹出
            var parent = new ScriptLayer(
                _ => new ParserLayerResult.PushLayer(child, TokenDisposition.Consume),  // 收 t1 并消费
                PopConsume);                                            // 收 t3，消费后弹出

            Run("Push+Consume", parent, child,
                expectedParent: new[] { "t1", "t3" },
                expectedChild: new[] { "t2" },
                T("t1"), T("t2"), T("t3"));
        }

        // Push + Replay：父层不消费 t1 直接压入子层，t1 原样交给子层；
        // 子层弹出后 EOF 到达父层，父层按 EOF 规则 Pop(Replay) 上交 TestRoot
        private static void TestPushReplay()
        {
            var child = new ScriptLayer(PopConsume);                    // 收 t1（重放），消费后弹出
            var parent = new ScriptLayer(
                _ => new ParserLayerResult.PushLayer(child, TokenDisposition.Replay),   // 收 t1，不消费
                PopReplay);                                             // 收 EOF，上交

            Run("Push+Replay", parent, child,
                expectedParent: new[] { "t1", "" },
                expectedChild: new[] { "t1" },
                T("t1"));
        }

        // Pop + Consume：子层消费 t2 后弹出，父层从 t3 恢复解析（t2 不被父层重见）
        private static void TestPopConsume()
        {
            var child = new ScriptLayer(PopConsume);                    // 收 t2，消费后弹出
            var parent = new ScriptLayer(
                _ => new ParserLayerResult.PushLayer(child, TokenDisposition.Consume),  // 收 t1 并消费
                PopConsume);                                            // 收 t3，消费后弹出

            Run("Pop+Consume", parent, child,
                expectedParent: new[] { "t1", "t3" },
                expectedChild: new[] { "t2" },
                T("t1"), T("t2"), T("t3"));
        }

        // Pop + Replay：子层不消费 t2 直接弹出，t2 原样交给父层
        private static void TestPopReplay()
        {
            var child = new ScriptLayer(PopReplay);                     // 收 t2，不消费直接弹出
            var parent = new ScriptLayer(
                _ => new ParserLayerResult.PushLayer(child, TokenDisposition.Consume),  // 收 t1 并消费
                PopConsume);                                            // 收 t2（重放），消费后弹出

            Run("Pop+Replay", parent, child,
                expectedParent: new[] { "t1", "t2" },
                expectedChild: new[] { "t2" },
                T("t1"), T("t2"));
        }

        // 以 TestRoot 垫底驱动入口层，比对两层的实际接收序列
        private static void Run(
            string name,
            ScriptLayer parent,
            ScriptLayer child,
            string[] expectedParent,
            string[] expectedChild,
            params Token[] tokens)
        {
            try
            {
                new Parser().Parse(tokens.ToList(), new TestRootParserLayer(), parent);

                string actualParent = string.Join(",", parent.Received);
                string actualChild = string.Join(",", child.Received);
                string wantParent = string.Join(",", expectedParent);
                string wantChild = string.Join(",", expectedChild);

                if (actualParent == wantParent && actualChild == wantChild)
                {
                    Console.WriteLine($"  [PASS] {name}  parent=[{actualParent}] child=[{actualChild}]");
                    passCount++;
                }
                else
                {
                    Console.WriteLine($"  [FAIL] {name}");
                    Console.WriteLine($"      expected parent=[{wantParent}] child=[{wantChild}]");
                    Console.WriteLine($"      actual   parent=[{actualParent}] child=[{actualChild}]");
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {name}  => unexpected exception: {ex.Message}");
                failCount++;
            }
        }

        public static int RunAll()
        {
            Console.WriteLine("\n╔════════════════════════════════════╗");
            Console.WriteLine("║  Token Disposition Tests           ║");
            Console.WriteLine("╚════════════════════════════════════╝\n");

            passCount = 0;
            failCount = 0;

            TestPushConsume();
            TestPushReplay();
            TestPopConsume();
            TestPopReplay();

            Console.WriteLine($"=== Token Disposition Tests Complete: {passCount} passed, {failCount} failed ===\n");
            return failCount;
        }
    }
}
