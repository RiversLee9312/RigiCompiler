using System;

namespace RigiCompiler.Tests
{
    /// <summary>
    /// 测试专用垫底 Layer（大扫除 §13.3）
    ///
    /// 以 `Parser.Parse(tokens, new TestRootParserLayer(), entryLayer)` 驱动独立 Layer 测试：
    /// - 测试输入末尾由 Parser 自动加入 EOF；
    /// - 被测 Layer 弹出后，只有 EOF 允许到达本层；
    /// - 任何普通 token 到达本层都说明被测 Layer 提前结束或漏消费，测试立即失败。
    /// </summary>
    public class TestRootParserLayer : IParserLayer
    {
        public ParserLayerResult ParseToken(Token currentToken, ParserLayerContext context)
        {
            if (currentToken is EndOfFileToken)
            {
                return ParserLayerResult.Continue.Instance;
            }

            throw context.RaiseError(
                $"Test layer finished early or left unconsumed token: {currentToken}");
        }
    }
}
